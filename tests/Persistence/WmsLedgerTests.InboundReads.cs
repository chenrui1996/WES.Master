using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Features.Inbound;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    private static readonly string[] InboundFields =
        ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines", "Receipts"];
    private InboundSubject InboundReader => new(_tenant, _actor, Guid.NewGuid());

    [Fact]
    public async Task InboundDetailShowsExactRemainingAndRealPendingTransportWithoutWriting()
    {
        var (document, line) = await InboundAsync();
        var command = (await InboundCommandAsync(line, 2m)) with { TargetLocationId = Guid.NewGuid() };
        await ReceiveInboundAsync(document.Id, command);
        var authority = new InboundAuthority(_actor);
        await using var scope = new WmsInboundScope(_connection, _tenant, authority, NullLoggerFactory.Instance);
        var result = await scope.ReadAsync(InboundReader, document.Id);
        Assert.Equal(InboundReadDecision.Success, result.Decision);
        var view = Assert.IsType<InboundView>(result.Order);
        Assert.Equal(InboundState.Executing, view.State);
        var plan = Assert.Single(view.Lines);
        Assert.Equal(("5.000000", "2.000000", "3.000000"), (plan.Planned, plan.Received, plan.Remaining));
        var receipt = Assert.Single(view.Receipts);
        Assert.Equal(command.ExecutionId, receipt.Id);
        Assert.Equal("2.000000", receipt.Quantity);
        Assert.True(receipt.RequiresTransport);
        Assert.False(receipt.Arrived);
        Assert.Null(receipt.TransportId);
        Assert.Equal(2, authority.Calls);
        await using var verify = Open(_tenant);
        Assert.Equal(2, await verify.InboundFacts.CountAsync());
        Assert.Single(await verify.Postings.ToListAsync());
    }

    [Fact]
    public async Task InboundDetailRevalidatesAuthorityAfterReadingFacts()
    {
        var (document, _) = await InboundAsync();
        var authority = new InboundAuthority(_actor) { RevokeAfterFirst = true };
        await using var owner = Open(_tenant);
        var access = new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance);
        var denied = await access.ReadAsync(InboundReader, document.Id);
        Assert.Equal(InboundReadDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Order);
        Assert.Equal(2, authority.Calls);
        Assert.Empty(owner.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Code")]
    [InlineData("WarehouseId")]
    [InlineData("State")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    [InlineData("Lines")]
    [InlineData("Receipts")]
    public async Task InboundDetailRequiresEveryExactIndependentField(string missing)
    {
        var (document, _) = await InboundAsync();
        await using var owner = Open(_tenant);
        var authority = new InboundAuthority(_actor) { Missing = missing };
        var result = await new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance).ReadAsync(InboundReader, document.Id);
        Assert.Equal(InboundReadDecision.Forbidden, result.Decision);
        Assert.Null(result.Order);
        Assert.Equal(1, authority.Calls);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    [InlineData("empty")]
    [InlineData("unknown")]
    public async Task InboundDetailRejectsInvalidSubjectsAndUnknownDocumentsWithoutAuthorityCalls(string variant)
    {
        var (document, _) = await InboundAsync();
        var authority = new InboundAuthority(_actor);
        await using var owner = Open(_tenant);
        var subject = InboundReader;
        var id = document.Id;
        if (variant == "tenant") subject = subject with { TenantId = _other };
        if (variant == "actor") subject = subject with { ActorId = Guid.Empty };
        if (variant == "session") subject = subject with { SessionId = Guid.Empty };
        if (variant == "empty") id = Guid.Empty;
        if (variant == "unknown") id = Guid.NewGuid();
        var result = await new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance).ReadAsync(subject, id);
        Assert.Equal(variant == "empty" ? InboundReadDecision.Invalid : variant == "unknown" ? InboundReadDecision.NotFound
            : InboundReadDecision.Forbidden, result.Decision);
        Assert.Null(result.Order);
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task InboundDetailOtherTenantCannotReadAnActualOrder()
    {
        var (document, _) = await InboundAsync();
        var authority = new InboundAuthority(_actor);
        await using var owner = Open(_other);
        var result = await new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance)
            .ReadAsync(new(_other, _actor, Guid.NewGuid()), document.Id);
        Assert.Equal(InboundReadDecision.NotFound, result.Decision);
        Assert.Null(result.Order);
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task InboundDetailCancellationAfterMaterializationReturnsNoProjection()
    {
        var (document, _) = await InboundAsync();
        using var cancel = new CancellationTokenSource();
        var authority = new InboundAuthority(_actor) { OnSecond = cancel.Cancel };
        await using var owner = Open(_tenant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance)
            .ReadAsync(InboundReader, document.Id, cancel.Token));
        Assert.Null(owner.Database.CurrentTransaction);
        Assert.Empty(owner.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundDetailDatabaseErrorsAreSanitized(bool transient)
    {
        var (document, _) = await InboundAsync();
        await using var owner = Open(_tenant, new QueryFault(transient, "wms_inbound_facts"));
        var error = await Assert.ThrowsAsync<Wes.Wms.Features.Ledger.WmsDatabaseException>(() =>
            new InboundAccess(owner, new InboundAuthority(_actor), NullLogger<InboundAccess>.Instance).ReadAsync(InboundReader, document.Id));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
        Assert.Null(owner.Database.CurrentTransaction);
    }

    [Fact]
    public async Task InboundDetailSnapshotNeverCombinesOldHeaderWithNewReceiptHistory()
    {
        var (document, line) = await InboundAsync();
        var command = await InboundCommandAsync(line, 2m);
        var interleave = new BeforeInboundHistoryRead(async () =>
            Assert.Equal(InboundWriteDecision.Committed, (await ReceiveInboundAsync(document.Id, command)).Decision));
        await using var owner = Open(_tenant, interleave);
        var access = new InboundAccess(owner, new InboundAuthority(_actor), NullLogger<InboundAccess>.Instance);
        var result = await access.ReadAsync(InboundReader, document.Id);
        Assert.True(interleave.Reached);
        Assert.Equal(InboundReadDecision.Success, result.Decision);
        Assert.Equal(document.Version, result.Order!.Version);
        Assert.Equal("0.000000", Assert.Single(result.Order.Lines).Received);
        Assert.Empty(result.Order.Receipts);
        var current = await access.ReadAsync(InboundReader, document.Id);
        Assert.Equal("2.000000", Assert.Single(current.Order!.Lines).Received);
        Assert.Single(current.Order.Receipts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundDetailRejectsMalformedPersistedJsonWithoutLeakingPayload(bool receipt)
    {
        var (document, line) = await InboundAsync();
        if (receipt) await ReceiveInboundAsync(document.Id, await InboundCommandAsync(line, 1m));
        await using var owner = Open(_tenant);
        const string invalid = "{private-sentinel";
        if (receipt)
            await owner.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE wms_inbound_facts SET Payload = {invalid} WHERE TenantId = {_tenant.ToString("D")} AND Kind = {"Received"}");
        else
            await owner.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE wms_inbound_documents SET Plan = {invalid} WHERE TenantId = {_tenant.ToString("D")}");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InboundAccess(owner, new InboundAuthority(_actor), NullLogger<InboundAccess>.Instance).ReadAsync(InboundReader, document.Id));
        Assert.DoesNotContain("private-sentinel", error.ToString(), StringComparison.Ordinal);
        Assert.Null(owner.Database.CurrentTransaction);
    }

    [Fact]
    public async Task InboundDetailCannotReadInsideAnotherOwnersTransactionOrDirtyTracker()
    {
        var (document, _) = await InboundAsync();
        await using var owner = Open(_tenant);
        var access = new InboundAccess(owner, new InboundAuthority(_actor), NullLogger<InboundAccess>.Instance);
        _ = await owner.InboundDocuments.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => access.ReadAsync(InboundReader, document.Id));
        owner.ChangeTracker.Clear();
        await using var transaction = await owner.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => access.ReadAsync(InboundReader, document.Id));
    }

    private sealed class BeforeInboundHistoryRead(Func<Task> commit) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        internal bool Reached { get; private set; }
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Reached && command.CommandText.Contains("wms_inbound_facts", StringComparison.Ordinal))
            {
                Reached = true;
                await commit();
            }
            return result;
        }
    }

    private sealed class InboundAuthority(Guid creator) : IInboundAuthorization
    {
        internal int Calls { get; private set; }
        internal bool RevokeAfterFirst { get; init; }
        internal string? Missing { get; init; }
        internal Action? OnSecond { get; init; }
        public Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid actualCreator, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(creator, actualCreator);
            Calls++;
            if (Calls == 2) OnSecond?.Invoke();
            IReadOnlySet<string> fields = RevokeAfterFirst && Calls > 1 ? new HashSet<string>()
                : InboundFields.Where(field => field != Missing).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(fields);
        }
    }
}
