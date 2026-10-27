using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    [Fact]
    public async Task InboundDraftPublicReceiptRetainsOnlyOriginalIdAfterApproval()
    {
        var (warehouse, _) = await InboundAsync(false);
        var command = Draft(warehouse.WarehouseId);
        InboundDraftResult created;
        await using (var writer = DraftWriter(new DraftAuthority(_actor)))
            created = await writer.CreateAsync(InboundReader, command);
        Assert.Equal(InboundDraftDecision.Created, created.Decision);
        Assert.NotNull(created.Id);
        await using (var advance = Open(_tenant))
        {
            var document = await advance.InboundDocuments.AsNoTracking().SingleAsync(item => item.Id == created.Id);
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(advance, _clock).ApproveAsync(
                document.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), document.Version)).Decision);
        }
        await using var replay = DraftWriter(new DraftAuthority(_actor));
        Assert.Equal(created with { Decision = InboundDraftDecision.Replayed },
            await replay.CreateAsync(InboundReader, command));
        Assert.Equal(new[] { "Decision", "Id" }, typeof(InboundDraftResult).GetProperties().Select(property => property.Name));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Postings.ToListAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    private InboundDraftCreation Draft(Guid warehouse) => new(Guid.NewGuid(), "draft-new", warehouse,
        [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 3, "5.000")]);

    [Theory]
    [InlineData("Code")]
    [InlineData("WarehouseId")]
    [InlineData("Lines")]
    [InlineData("parent:Id")]
    [InlineData("parent:Code")]
    [InlineData("parent:Name")]
    [InlineData("parent:Enabled")]
    [InlineData("parent:Version")]
    [InlineData("parent:CreatedAt")]
    public async Task InboundDraftRequiresEveryExactWriteAndParentReadField(string missing)
    {
        var (document, _) = await InboundAsync(false);
        await using var writer = DraftWriter(new DraftAuthority(_actor) { Missing = missing });
        var result = await writer.CreateAsync(InboundReader, Draft(document.WarehouseId));
        Assert.Equal(InboundDraftDecision.Forbidden, result.Decision);
        Assert.Null(result.Id);
        await AssertNoNewDraftAsync();
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task InboundDraftRejectsInvalidTrustedContextBeforeAuthority(string invalid)
    {
        var (document, _) = await InboundAsync(false);
        var authority = new DraftAuthority(_actor);
        var subject = invalid switch
        {
            "tenant" => InboundReader with { TenantId = _other },
            "actor" => InboundReader with { ActorId = Guid.Empty },
            _ => InboundReader with { SessionId = Guid.Empty }
        };
        await using var writer = DraftWriter(authority);
        Assert.Equal(InboundDraftDecision.Forbidden, (await writer.CreateAsync(subject, Draft(document.WarehouseId))).Decision);
        Assert.Equal(0, authority.Calls);
        await AssertNoNewDraftAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1e2")]
    [InlineData(" 1")]
    [InlineData("1,000")]
    [InlineData("1.0000001")]
    [InlineData("10000000000000000000000")]
    public async Task InboundDraftRejectsInvalidQuantityBeforeAuthority(string quantity)
    {
        var authority = new DraftAuthority(_actor);
        var command = Draft(Guid.NewGuid());
        command = command with { Lines = [command.Lines[0] with { Planned = quantity }] };
        await using var writer = DraftWriter(authority);
        Assert.Equal(InboundDraftDecision.Invalid, (await writer.CreateAsync(InboundReader, command)).Decision);
        Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("warehouse")]
    [InlineData("code")]
    [InlineData("empty-plan")]
    [InlineData("too-many")]
    [InlineData("duplicate-line")]
    [InlineData("item")]
    [InlineData("unit")]
    [InlineData("precision")]
    [InlineData("overprecision")]
    [InlineData("batch")]
    public async Task InboundDraftRejectsInvalidPlanBeforeAuthority(string invalid)
    {
        var command = Draft(Guid.NewGuid());
        var line = command.Lines[0];
        command = invalid switch
        {
            "operation" => command with { OperationId = Guid.Empty },
            "warehouse" => command with { WarehouseId = Guid.Empty },
            "code" => command with { Code = " bad" },
            "empty-plan" => command with { Lines = [] },
            "too-many" => command with { Lines = Enumerable.Range(0, 101).Select(_ => line with { Id = Guid.NewGuid() }).ToArray() },
            "duplicate-line" => command with { Lines = [line, line] },
            "item" => command with { Lines = [line with { ItemId = Guid.Empty }] },
            "unit" => command with { Lines = [line with { UnitId = Guid.Empty }] },
            "precision" => command with { Lines = [line with { Precision = 7 }] },
            "overprecision" => command with { Lines = [line with { Precision = 0, Planned = "1.1" }] },
            _ => command with { Lines = [line with { Batch = "bad\n" }] }
        };
        var authority = new DraftAuthority(_actor);
        await using var writer = DraftWriter(authority);
        Assert.Equal(InboundDraftDecision.Invalid, (await writer.CreateAsync(InboundReader, command)).Decision);
        Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundDraftReauthorizationAfterSaveRollsBackOnRevocationOrCancellation(bool cancel)
    {
        var (document, _) = await InboundAsync(false);
        using var cancellation = new CancellationTokenSource();
        var authority = new DraftAuthority(_actor) { RevokeAfterFirst = !cancel, OnSecond = cancel ? cancellation.Cancel : null };
        await using var writer = DraftWriter(authority);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.CreateAsync(InboundReader, Draft(document.WarehouseId), cancellation.Token));
        else
            Assert.Equal(InboundDraftDecision.Forbidden, (await writer.CreateAsync(InboundReader, Draft(document.WarehouseId))).Decision);
        Assert.Equal(2, authority.Calls);
        await AssertNoNewDraftAsync();
    }

    [Fact]
    public async Task InboundDraftChangedBodyOrActorCannotReuseOriginalOperationAndRevokedReplayDoesNotDiscloseId()
    {
        var (document, _) = await InboundAsync(false);
        var command = Draft(document.WarehouseId);
        await using (var first = DraftWriter(new DraftAuthority(_actor)))
            Assert.Equal(InboundDraftDecision.Created, (await first.CreateAsync(InboundReader, command)).Decision);
        foreach (var changed in new[] { command with { Code = "OTHER" },
            command with { Lines = [command.Lines[0] with { Planned = "6" }] } })
        {
            await using var writer = DraftWriter(new DraftAuthority(_actor));
            Assert.Equal(InboundDraftDecision.Conflict, (await writer.CreateAsync(InboundReader, changed)).Decision);
        }
        await using (var writer = DraftWriter(new DraftAuthority(_actor)))
            Assert.Equal(InboundDraftDecision.Conflict,
                (await writer.CreateAsync(InboundReader with { ActorId = Guid.NewGuid() }, command)).Decision);
        await using var denied = DraftWriter(new DraftAuthority(_actor) { RevokeAfterFirst = true });
        var result = await denied.CreateAsync(InboundReader, command);
        Assert.Equal(InboundDraftDecision.Forbidden, result.Decision);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task InboundDraftSameOperationConcurrentRequestsHaveOneCreationAndStableId()
    {
        var (document, _) = await InboundAsync(false);
        var command = Draft(document.WarehouseId);
        async Task<InboundDraftResult> Execute()
        {
            await using var writer = DraftWriter(new DraftAuthority(_actor));
            return await writer.CreateAsync(InboundReader, command);
        }
        var results = await Task.WhenAll(Execute(), Execute());
        Assert.Single(results, item => item.Decision == InboundDraftDecision.Created);
        Assert.Single(results, item => item.Decision == InboundDraftDecision.Replayed);
        Assert.Equal(results[0].Id, results[1].Id);
        await using var verify = Open(_tenant);
        Assert.Equal(2, await verify.InboundDocuments.CountAsync());
        Assert.Empty(await verify.InboundFacts.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
    }

    [Fact]
    public async Task InboundDraftCanonicalQuantityAndCodeReplayAndMaxPrecisionRemainExact()
    {
        var (document, _) = await InboundAsync(false);
        var command = Draft(document.WarehouseId);
        command = command with { Lines = [command.Lines[0] with { Planned = "9999999999999999999999.999999", Precision = 6 }] };
        InboundDraftResult created;
        await using (var writer = DraftWriter(new DraftAuthority(_actor)))
            created = await writer.CreateAsync(InboundReader, command);
        Assert.Equal(InboundDraftDecision.Created, created.Decision);
        await using (var verify = Open(_tenant))
            Assert.Equal(9999999999999999999999.999999m,
                (await verify.InboundDocuments.SingleAsync(item => item.Id == created.Id)).Restore([]).Lines[0].PlannedQuantity);
        var simple = Draft(document.WarehouseId) with { Code = "SIMPLE" };
        await using (var writer = DraftWriter(new DraftAuthority(_actor)))
            Assert.Equal(InboundDraftDecision.Created, (await writer.CreateAsync(InboundReader, simple)).Decision);
        await using var replay = DraftWriter(new DraftAuthority(_actor));
        Assert.Equal(InboundDraftDecision.Replayed, (await replay.CreateAsync(InboundReader, simple with
        {
            Code = "simple",
            Lines = [simple.Lines[0] with { Planned = "5" }]
        })).Decision);
    }

    [Fact]
    public async Task InboundDraftMissingOrForeignParentCannotCreate()
    {
        await using var writer = DraftWriter(new DraftAuthority(_actor));
        Assert.Equal(InboundDraftDecision.NotFound, (await writer.CreateAsync(InboundReader, Draft(Guid.NewGuid()))).Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.InboundDocuments.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundDraftSaveFailureOrCancellationLeavesNoNewDraft(bool cancel)
    {
        var (document, _) = await InboundAsync(false);
        using var cancellation = new CancellationTokenSource();
        await using (var database = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            var access = new InboundDraftAccess(database, new DraftAuthority(_actor), _clock, NullLogger<InboundDraftAccess>.Instance);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                access.CreateAsync(InboundReader, Draft(document.WarehouseId), cancellation.Token));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => access.CreateAsync(InboundReader, Draft(document.WarehouseId)));
        }
        await AssertNoNewDraftAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundDraftSanitizesDatabaseFailureWithoutSuccessFallback(bool transient)
    {
        await using var database = Open(_tenant, new QueryFault(transient, "wms_warehouses"));
        var access = new InboundDraftAccess(database, new DraftAuthority(_actor), _clock, NullLogger<InboundDraftAccess>.Instance);
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() => access.CreateAsync(InboundReader, Draft(Guid.NewGuid())));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
    }

    private async Task AssertNoNewDraftAsync()
    {
        await using var verify = Open(_tenant);
        Assert.Single(await verify.InboundDocuments.ToListAsync());
        Assert.Empty(await verify.InboundFacts.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    private WmsInboundDraftWriter DraftWriter(IInboundDraftAuthorization authority) =>
        new(_connection, _tenant, authority, _clock, NullLoggerFactory.Instance);

    private sealed class DraftAuthority(Guid creator) : IInboundDraftAuthorization
    {
        internal string? Missing { get; init; }
        internal bool RevokeAfterFirst { get; init; }
        internal Action? OnSecond { get; init; }
        internal int Calls { get; private set; }
        public Task<IReadOnlySet<string>> WriteFieldsAsync(InboundSubject subject, CancellationToken cancellationToken)
        {
            ++Calls;
            if (Calls == 2) OnSecond?.Invoke();
            return Task.FromResult<IReadOnlySet<string>>(RevokeAfterFirst && Calls > 1 ? new HashSet<string>()
                : new[] { "Code", "WarehouseId", "Lines" }.Where(field => field != Missing).ToHashSet());
        }
        public Task<IReadOnlySet<string>> WarehouseFieldsAsync(InboundSubject subject, Guid actualCreator, CancellationToken cancellationToken)
        {
            Assert.Equal(creator, actualCreator);
            return Task.FromResult<IReadOnlySet<string>>(new[] { "Id", "Code", "Name", "Enabled", "Version", "CreatedAt" }
                .Where(field => $"parent:{field}" != Missing).ToHashSet());
        }
    }
}
