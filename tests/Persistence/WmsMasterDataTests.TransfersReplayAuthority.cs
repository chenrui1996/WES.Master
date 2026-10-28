using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Transfers;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    public static TheoryData<string, string> TransferReplayAuthorityCases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var action in new[] { "create", "approve" })
                foreach (var permission in new[] { $"wms.transfers.{action}", "wms.transfers.read",
                    "wms.warehouses.read", "wms.locations.read", "wms.items.read", "wms.units.read", "wms.containers.read" })
                    cases.Add(action, permission);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(TransferReplayAuthorityCases))]
    public Task TransferConcurrentOriginalRechecksCompleteAuthorityAfterFence(string action, string permission) =>
        TransferConcurrentOriginalAfterFenceAsync(action, permission);

    [Theory]
    [InlineData("create")]
    [InlineData("approve")]
    public Task TransferConcurrentOriginalDoesNotReapplyOperationalOrNewStateChecks(string action) =>
        TransferConcurrentOriginalAfterFenceAsync(action, null);

    private async Task TransferConcurrentOriginalAfterFenceAsync(string action, string? revokedPermission)
    {
        var fixture = await TransferReadyAsync();
        var subject = Reader with { ActorId = Guid.NewGuid() };
        Assert.NotEqual(_actor, subject.ActorId);
        var draft = new TransferDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-FENCED-REPLAY", fixture.Warehouse,
            [new(fixture.A, fixture.SourceA, fixture.TargetA, "3", 0), new(fixture.B, fixture.SourceB, fixture.TargetB, "1", 0)]);
        TransferRecord? detail = null;
        if (action == "approve")
        {
            await using var creation = Open(_tenant);
            var created = await Transfers(creation).CreateAsync(subject, draft, CancellationToken.None);
            Assert.Equal(TransferDecision.Committed, created.Decision);
            detail = await TransferDetailAsync(created.Id!.Value);
        }
        var operation = Guid.NewGuid();
        var approval = detail is null ? null : Approval(detail);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Task<TransferResult> Execute(TransferAccess access) => action == "create"
            ? access.CreateAsync(subject, draft, deadline.Token)
            : access.ApproveAsync(subject, detail!.Id, approval!, operation, Guid.NewGuid(), deadline.Token);
        var pause = new TransferReplayCommitPause();
        await using var winnerOwner = Open(_tenant, pause);
        var winner = Execute(Transfers(winnerOwner));
        await pause.Entered.Task.WaitAsync(deadline.Token);
        var authority = new TransferReplayAuthority();
        var observer = new TransferReplayFenceObserver(() =>
        {
            authority.Permit = (permission, creator) => permission != revokedPermission
                || creator != (permission.StartsWith("wms.transfers.", StringComparison.Ordinal) ? subject.ActorId : _actor);
        });
        await using var waiterOwner = Open(_tenant, observer);
        // The waiter alone loses schema availability. Successful originals must not revalidate it.
        var waiter = Execute(Transfers(waiterOwner, authority, new Schema(LocationSchemaDecision.Unavailable)));
        try
        {
            await observer.InitialOriginalRead.Task.WaitAsync(deadline.Token);
            Assert.False(winner.IsCompleted);
            Assert.False(waiter.IsCompleted);
            pause.Release.TrySetResult();
            var committed = await winner.WaitAsync(deadline.Token);
            Assert.Equal(TransferDecision.Committed, committed.Decision);
            var result = await waiter.WaitAsync(deadline.Token);
            Assert.True(observer.FencePassed);
            Assert.Equal(revokedPermission is null ? TransferDecision.Replayed : TransferDecision.Forbidden, result.Decision);
            Assert.True(observer.DocumentReadsAfterFence > 0, "Original document must be reloaded after the resource fences.");
            Assert.True(observer.ResourceReadsAfterFence > 0, "Original resource creators must be reloaded after the resource fences.");
            Assert.True(authority.CallsAfterFence > 0, "Current read/action/resource authority must be checked after the resource fences.");
            if (revokedPermission is null)
                Assert.Equal(committed with { Decision = TransferDecision.Replayed }, result);
            else
                Assert.Null(result.Id);
            await using var verify = Open(_tenant);
            Assert.Equal(action == "create" ? 1 : 2, await verify.TransferFacts.CountAsync());
            Assert.Equal(action == "create" ? 0 : 2, await verify.Reservations.CountAsync());
            Assert.Empty(await verify.TransferOutbox.ToArrayAsync());
        }
        finally
        {
            pause.Release.TrySetResult();
            await winner.WaitAsync(deadline.Token);
            await waiter.WaitAsync(deadline.Token);
        }
    }

    private sealed class TransferReplayCommitPause : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class TransferReplayAuthority : IMasterDataAuthorization
    {
        private readonly TransferAuthority _inner = new();
        internal int CallsAfterFence { get; private set; }
        internal Func<string, Guid, bool>? Permit
        {
            set => _inner.Permit = value;
        }
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator,
            CancellationToken ct)
        {
            if (_inner.Permit is not null) CallsAfterFence++;
            return _inner.ReadFieldsAsync(subject, permission, creator, ct);
        }
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class TransferReplayFenceObserver(Action revoke) : DbCommandInterceptor
    {
        internal TaskCompletionSource InitialOriginalRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FencePassed { get; private set; }
        internal int DocumentReadsAfterFence { get; private set; }
        internal int ResourceReadsAfterFence { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            var where = command.CommandText.IndexOf("WHERE", StringComparison.Ordinal);
            if (!FencePassed && where >= 0
                && command.CommandText.Contains("wms_transfer_facts", StringComparison.Ordinal)
                && command.CommandText[where..].Contains("OperationId", StringComparison.Ordinal))
                InitialOriginalRead.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!FencePassed && command.CommandText.Contains("wms_stock_counts", StringComparison.Ordinal))
            {
                // This query follows all raw SELECT FOR UPDATE fences, while the owner transaction still holds them.
                FencePassed = true;
                revoke();
            }
            if (FencePassed)
            {
                if (command.CommandText.Contains("wms_transfer_documents", StringComparison.Ordinal)) DocumentReadsAfterFence++;
                if (command.CommandText.Contains("wms_stock_dimension_registrations", StringComparison.Ordinal)) ResourceReadsAfterFence++;
            }
            return ValueTask.FromResult(result);
        }
    }
}
