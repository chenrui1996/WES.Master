using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    [Fact]
    public async Task InboundRevisionRetainsOriginalPlanAndReplaysOriginalVersionAfterApproval()
    {
        var (document, line) = await InboundAsync(false);
        var operation = Guid.NewGuid();
        var revised = line with { PlannedQuantity = 7m };
        InboundWriteResult first;
        await using (var writer = Open(_tenant))
            first = await new InboundWriter(writer, _clock).ReviseAsync(document.Id, operation, _actor,
                Guid.NewGuid(), document.Version, [revised]);
        Assert.Equal(InboundWriteDecision.Committed, first.Decision);
        await using (var verify = Open(_tenant))
        {
            var header = await verify.InboundDocuments.SingleAsync();
            Assert.Equal(document.Plan, header.Plan);
            Assert.Equal(7m, header.Restore(await verify.InboundFacts.OrderBy(item => item.Sequence).ToListAsync()).Lines[0].PlannedQuantity);
        }
        await using (var approve = Open(_tenant))
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(approve, _clock).ApproveAsync(
                document.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), first.Version!)).Decision);
        await using var replay = Open(_tenant);
        Assert.Equal(first with { Decision = InboundWriteDecision.Replayed }, await new InboundWriter(replay, _clock).ReviseAsync(
            document.Id, operation, _actor, Guid.NewGuid(), document.Version, [revised]));
    }

    [Fact]
    public async Task InboundRevisionHistoryDrivesReceiptAndDoesNotChangeOriginalCreationIdentity()
    {
        var (document, line) = await InboundAsync(false);
        var request = Revision(document, line);
        InboundRevisionResult first;
        await using (var owner = RevisionWriter(new RevisionAuthority(_actor)))
            first = await owner.ReviseAsync(InboundReader, document.Id, request);
        var next = request with
        {
            OperationId = Guid.NewGuid(),
            ExpectedVersion = first.Version!,
            Lines = [request.Lines[0] with { Planned = "9" }]
        };
        InboundRevisionResult second;
        await using (var owner = RevisionWriter(new RevisionAuthority(_actor)))
            second = await owner.ReviseAsync(InboundReader, document.Id, next);
        Assert.Equal(InboundRevisionDecision.Revised, second.Decision);
        await using (var approve = Open(_tenant))
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(approve, _clock).ApproveAsync(
                document.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), second.Version!)).Decision);
        Assert.Equal(InboundWriteDecision.Committed,
            (await ReceiveInboundAsync(document.Id, await InboundCommandAsync(line, 9m))).Decision);
        await using (var replay = RevisionWriter(new RevisionAuthority(_actor)))
            Assert.Equal(first with { Decision = InboundRevisionDecision.Replayed }, await replay.ReviseAsync(InboundReader, document.Id,
                request with { Lines = [request.Lines[0] with { Planned = "7.000000" }] }));
        await using (var original = Open(_tenant))
            Assert.Equal(InboundWriteDecision.Replayed, (await new InboundWriter(original, _clock).CreateAsync(
                document.CreationOperationId, _actor, document.WarehouseId, document.Code, [line])).Decision);
        await using var verify = Open(_tenant);
        var saved = await verify.InboundDocuments.SingleAsync();
        Assert.Equal(document.Plan, saved.Plan);
        Assert.Equal(InboundOrderState.Completed, saved.Restore(await verify.InboundFacts.OrderBy(item => item.Sequence).ToListAsync()).State);
        Assert.Equal(4, await verify.InboundFacts.CountAsync());
        Assert.Single(await verify.Postings.ToListAsync());
        Assert.Equal(9m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
    }

    [Theory]
    [InlineData("Code")]
    [InlineData("WarehouseId")]
    [InlineData("Lines")]
    [InlineData("read:Id")]
    [InlineData("read:Code")]
    [InlineData("read:WarehouseId")]
    [InlineData("read:State")]
    [InlineData("read:Version")]
    [InlineData("read:CreatedAt")]
    [InlineData("read:Lines")]
    [InlineData("read:Receipts")]
    public async Task InboundRevisionNeedsEveryCurrentExactFieldBeforeNewWriteAndReplay(string missing)
    {
        var (document, line) = await InboundAsync(false);
        var request = Revision(document, line);
        await using (var denied = RevisionWriter(new RevisionAuthority(_actor) { Missing = missing }))
            Assert.Equal(new(InboundRevisionDecision.Forbidden), await denied.ReviseAsync(InboundReader, document.Id, request));
        await AssertRevisionUnchangedAsync(document);
        await using (var create = RevisionWriter(new RevisionAuthority(_actor)))
            Assert.Equal(InboundRevisionDecision.Revised, (await create.ReviseAsync(InboundReader, document.Id, request)).Decision);
        await using var replay = RevisionWriter(new RevisionAuthority(_actor) { Missing = missing });
        Assert.Equal(new(InboundRevisionDecision.Forbidden), await replay.ReviseAsync(InboundReader, document.Id, request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundRevisionRechecksAuthorityAfterSaveOrBeforeReplayDisclosure(bool replay)
    {
        var (document, line) = await InboundAsync(false);
        var request = Revision(document, line);
        if (replay)
        {
            await using var create = RevisionWriter(new RevisionAuthority(_actor));
            Assert.Equal(InboundRevisionDecision.Revised, (await create.ReviseAsync(InboundReader, document.Id, request)).Decision);
        }
        await using (var denied = RevisionWriter(new RevisionAuthority(_actor) { RevokeAfterFirst = true }))
            Assert.Equal(new(InboundRevisionDecision.Forbidden), await denied.ReviseAsync(InboundReader, document.Id, request));
        if (!replay) await AssertRevisionUnchangedAsync(document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundRevisionSavedThenFailureOrCancellationRollsBack(bool cancel)
    {
        var (document, line) = await InboundAsync(false);
        using var cancellation = new CancellationTokenSource();
        await using (var database = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            var access = new InboundRevisionAccess(database, new RevisionAuthority(_actor), _clock, NullLogger<InboundRevisionAccess>.Instance);
            Task<InboundRevisionResult> Execute() => access.ReviseAsync(InboundReader, document.Id, Revision(document, line), cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await AssertRevisionUnchangedAsync(document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundRevisionSharesParentFenceWithConcurrentRevisionOrApproval(bool approval)
    {
        var (document, line) = await InboundAsync(false);
        async Task<InboundWriteResult> Execute(bool other)
        {
            await using var database = Open(_tenant);
            var writer = new InboundWriter(database, _clock);
            return approval && other
                ? await writer.ApproveAsync(document.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), document.Version)
                : await writer.ReviseAsync(document.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), document.Version,
                    [line with { PlannedQuantity = other ? 9m : 7m }]);
        }
        var results = await Task.WhenAll(Execute(false), Execute(true));
        Assert.Single(results, item => item.Decision == InboundWriteDecision.Committed);
        Assert.Single(results, item => item.Decision == InboundWriteDecision.VersionConflict);
        await using var verify = Open(_tenant);
        var saved = await verify.InboundDocuments.SingleAsync();
        Assert.Single(await verify.InboundFacts.ToListAsync());
        _ = saved.Restore(await verify.InboundFacts.OrderBy(item => item.Sequence).ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundRevisionConcurrentOriginalTupleIsOneRevisionAndOneOriginalReplay(bool different)
    {
        var (document, line) = await InboundAsync(false);
        var request = Revision(document, line);
        async Task<InboundRevisionResult> Execute(InboundDraftRevision command)
        {
            await using var owner = RevisionWriter(new RevisionAuthority(_actor));
            return await owner.ReviseAsync(InboundReader, document.Id, command);
        }
        var results = await Task.WhenAll(Execute(request), Execute(different
            ? request with { Lines = [request.Lines[0] with { Planned = "9" }] } : request));
        Assert.Single(results, item => item.Decision == InboundRevisionDecision.Revised);
        Assert.Single(results, item => item.Decision == (different ? InboundRevisionDecision.Conflict : InboundRevisionDecision.Replayed));
        await using var verify = Open(_tenant);
        Assert.Single(await verify.InboundFacts.ToListAsync());
    }

    [Fact]
    public async Task InboundRevisionRejectsStaleVersionNonDraftAndChangedOperationTuple()
    {
        var (document, line) = await InboundAsync(false);
        var request = Revision(document, line);
        async Task<InboundRevisionResult> Execute(InboundDraftRevision command)
        {
            await using var owner = RevisionWriter(new RevisionAuthority(_actor));
            return await owner.ReviseAsync(InboundReader, document.Id, command);
        }
        Assert.Equal(InboundRevisionDecision.Conflict,
            (await Execute(request with { ExpectedVersion = Guid.NewGuid().ToString("D") })).Decision);
        var first = await Execute(request);
        Assert.Equal(InboundRevisionDecision.Revised, first.Decision);
        foreach (var changed in new[] {
            request with { ExpectedVersion = first.Version! },
            request with { Lines = [request.Lines[0] with { Batch = "B" }] },
            request with { Lines = [request.Lines[0] with { Id = Guid.NewGuid() }] } })
            Assert.Equal(InboundRevisionDecision.Conflict, (await Execute(changed)).Decision);
        await using (var approve = Open(_tenant))
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(approve, _clock).ApproveAsync(
                document.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), first.Version!)).Decision);
        await using var verify = Open(_tenant);
        var current = await verify.InboundDocuments.SingleAsync();
        Assert.Equal(InboundRevisionDecision.Conflict,
            (await Execute(request with { OperationId = Guid.NewGuid(), ExpectedVersion = current.Version })).Decision);
        Assert.Equal(2, await verify.InboundFacts.CountAsync());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("operation")]
    [InlineData("quantity")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public async Task InboundRevisionRejectsMalformedInputBeforeAuthority(string invalid)
    {
        var (document, line) = await InboundAsync(false);
        var request = Revision(document, line);
        request = invalid switch
        {
            "version" => request with { ExpectedVersion = "bad" },
            "operation" => request with { OperationId = Guid.Empty },
            "quantity" => request with { Lines = [request.Lines[0] with { Planned = "1e2" }] },
            "duplicate" => request with { Lines = [request.Lines[0], request.Lines[0]] },
            _ => request with { Lines = [] }
        };
        var authority = new RevisionAuthority(_actor);
        await using var owner = RevisionWriter(authority);
        Assert.Equal(new(InboundRevisionDecision.Invalid), await owner.ReviseAsync(InboundReader, document.Id, request));
        Assert.Equal(0, authority.Calls);
        await AssertRevisionUnchangedAsync(document);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task InboundRevisionRequiresCompleteTrustedSubject(string invalid)
    {
        var (document, line) = await InboundAsync(false);
        var authority = new RevisionAuthority(_actor);
        var subject = invalid switch
        {
            "tenant" => InboundReader with { TenantId = _other },
            "actor" => InboundReader with { ActorId = Guid.Empty },
            _ => InboundReader with { SessionId = Guid.Empty }
        };
        await using var owner = RevisionWriter(authority);
        Assert.Equal(new(InboundRevisionDecision.Forbidden), await owner.ReviseAsync(subject, document.Id, Revision(document, line)));
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task InboundRevisionUsesPersistedCreatorAndDoesNotReplaceItWithTheWriter()
    {
        var (document, line) = await InboundAsync(false);
        var authority = new RevisionAuthority(_actor);
        await using var owner = RevisionWriter(authority);
        var result = await owner.ReviseAsync(InboundReader with { ActorId = Guid.NewGuid() }, document.Id, Revision(document, line));
        Assert.Equal(InboundRevisionDecision.Revised, result.Decision);
        Assert.Equal(2, authority.Calls);
    }

    [Fact]
    public async Task InboundRevisionSupportsOrderedFullReplacementAndExactMaximumWithoutChangingBasePlan()
    {
        var (document, line) = await InboundAsync(false);
        var input = Revision(document, line);
        var maximum = input.Lines[0] with { Id = Guid.NewGuid(), Planned = "9999999999999999999999.999999", Precision = 6 };
        var minimum = input.Lines[0] with { Id = Guid.NewGuid(), Planned = "0.000001", Precision = 6 };
        input = input with { Lines = [maximum, minimum] };
        InboundRevisionResult first;
        await using (var owner = RevisionWriter(new RevisionAuthority(_actor)))
            first = await owner.ReviseAsync(InboundReader, document.Id, input);
        Assert.Equal(InboundRevisionDecision.Revised, first.Decision);
        await using (var changed = RevisionWriter(new RevisionAuthority(_actor)))
            Assert.Equal(InboundRevisionDecision.Conflict,
                (await changed.ReviseAsync(InboundReader, document.Id, input with { Lines = [minimum, maximum] })).Decision);
        await using (var replace = RevisionWriter(new RevisionAuthority(_actor)))
            Assert.Equal(InboundRevisionDecision.Revised, (await replace.ReviseAsync(InboundReader, document.Id,
                input with { OperationId = Guid.NewGuid(), ExpectedVersion = first.Version!, Lines = [minimum] })).Decision);
        await using var verify = Open(_tenant);
        var saved = await verify.InboundDocuments.SingleAsync();
        var restored = saved.Restore(await verify.InboundFacts.OrderBy(item => item.Sequence).ToListAsync());
        Assert.Equal(minimum.Id, Assert.Single(restored.Lines).Id);
        Assert.Equal(0.000001m, restored.Lines[0].PlannedQuantity);
        Assert.Equal(document.Plan, saved.Plan);
        Assert.Empty(await verify.Postings.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundRevisionCannotDiscloseMissingOrOtherTenantOrder(bool foreign)
    {
        var (document, line) = await InboundAsync(false);
        var authority = new RevisionAuthority(_actor);
        await using var owner = new WmsInboundRevisionWriter(_connection, foreign ? _other : _tenant,
            authority, _clock, NullLoggerFactory.Instance);
        Assert.Equal(new(InboundRevisionDecision.NotFound), await owner.ReviseAsync(
            InboundReader with { TenantId = foreign ? _other : _tenant }, foreign ? document.Id : Guid.NewGuid(), Revision(document, line)));
        Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundRevisionDatabaseFailureIsExplicitAndSanitized(bool transient)
    {
        await using var database = Open(_tenant, new QueryFault(transient, "wms_inbound_documents"));
        var access = new InboundRevisionAccess(database, new RevisionAuthority(_actor), _clock, NullLogger<InboundRevisionAccess>.Instance);
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() => access.ReviseAsync(InboundReader, Guid.NewGuid(),
            new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, "7")])));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("state")]
    [InlineData("plan")]
    public async Task InboundRevisionPersistenceGuardRejectsWrongFenceStateOrOverwrittenCreationPlan(string tamper)
    {
        var (document, line) = await InboundAsync(false);
        await using (var database = Open(_tenant))
        {
            var header = await database.InboundDocuments.SingleAsync();
            header.Advance(InboundOrderState.Draft);
            var fact = new InboundFact(header, Guid.NewGuid(), _actor, Guid.NewGuid(), InboundFactKind.Revised,
                _clock.GetUtcNow(), revision: new(tamper == "version" ? Guid.NewGuid().ToString("D") : document.Version, [line]));
            database.InboundFacts.Add(fact);
            if (tamper == "state") database.Entry(header).Property(item => item.State).CurrentValue = InboundOrderState.Approved;
            if (tamper == "plan") database.Entry(header).Property(item => item.Plan).CurrentValue = "[]";
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await AssertRevisionUnchangedAsync(document);
    }

    private static InboundDraftRevision Revision(InboundDocument document, InboundPlanLine line) =>
        new(Guid.NewGuid(), document.Version, [new(line.Id, line.ItemId, line.UnitId, line.Batch, line.Precision, "7")]);

    private WmsInboundRevisionWriter RevisionWriter(IInboundRevisionAuthorization authority) =>
        new(_connection, _tenant, authority, _clock, NullLoggerFactory.Instance);

    private async Task AssertRevisionUnchangedAsync(InboundDocument document)
    {
        await using var verify = Open(_tenant);
        var saved = await verify.InboundDocuments.SingleAsync();
        Assert.Equal(document.Version, saved.Version);
        Assert.Equal(document.Plan, saved.Plan);
        Assert.Empty(await verify.InboundFacts.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    private sealed class RevisionAuthority(Guid creator) : IInboundRevisionAuthorization
    {
        internal string? Missing { get; init; }
        internal bool RevokeAfterFirst { get; init; }
        internal int Calls { get; private set; }
        public Task<IReadOnlySet<string>> WriteFieldsAsync(InboundSubject subject, Guid actualCreator, CancellationToken cancellationToken)
        {
            Assert.Equal(creator, actualCreator); ++Calls;
            return Task.FromResult<IReadOnlySet<string>>(RevokeAfterFirst && Calls > 1 ? new HashSet<string>()
                : InboundDraftAccess.WriteFields.Where(field => field != Missing).ToHashSet());
        }
        public Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid actualCreator, CancellationToken cancellationToken)
        {
            Assert.Equal(creator, actualCreator);
            return Task.FromResult<IReadOnlySet<string>>(InboundAccess.Fields.Where(field => $"read:{field}" != Missing).ToHashSet());
        }
    }
}
