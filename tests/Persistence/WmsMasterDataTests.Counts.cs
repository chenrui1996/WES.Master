using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms.Features.Counts;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Domain;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Theory]
    [InlineData("12", 12, 1)]
    [InlineData("8", 8, 1)]
    [InlineData("10", 10, 0)]
    [Trait("CaseId", "TC-T11-COUNT-01")]
    public async Task BlindCountAdjustsOnlyThroughLedgerAndSeparatesFinalCounter(string quantity, decimal expected, int postings)
    {
        var fixture = await OutboundReadyAsync();
        CountResult created;
        await using (var owner = Open(_tenant))
            created = await Counts(owner).CreateAsync(Reader, new("COUNT-01", fixture.Warehouse, fixture.Location),
                Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(CountDecision.Committed, created.Decision);
        CountResult started;
        await using (var owner = Open(_tenant))
            started = await Counts(owner).StartAsync(Reader, created.Id!.Value, new(created.Version!), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(CountDecision.Committed, started.Decision);
        Guid line;
        await using (var owner = Open(_tenant))
        {
            var detail = await Counts(owner).ReadAsync(Reader, created.Id!.Value);
            Assert.Equal(CountDecision.Committed, detail.Decision);
            line = Assert.Single(detail.Record!.Lines).Id;
            Assert.Null(detail.Record.Lines[0].Quantity);
            Assert.DoesNotContain("book", System.Text.Json.JsonSerializer.Serialize(detail.Record).ToLowerInvariant());
            Assert.Equal(CountDecision.Conflict, (await Counts(owner).AdjustAsync(Reader, created.Id.Value,
                new(started.Version!, "Not counted"), Guid.NewGuid(), Guid.NewGuid())).Decision);
        }
        CountResult recorded;
        await using (var owner = Open(_tenant))
            recorded = await Counts(owner).RecordAsync(Reader, created.Id!.Value,
                new(started.Version!, line, quantity, fixture.Location, fixture.Item, fixture.Container, "Scanned"),
                Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(CountDecision.Committed, recorded.Decision);
        var operation = Guid.NewGuid();
        var request = new CountStateRequest(recorded.Version!, "Explicit confirmation");
        var approver = expected == 10m ? Reader : Reader with { ActorId = Guid.NewGuid() };
        await using (var owner = Open(_tenant))
        {
            if (expected != 10m)
                Assert.Equal(CountDecision.Forbidden, (await Counts(owner).AdjustAsync(Reader, created.Id!.Value,
                    request, Guid.NewGuid(), Guid.NewGuid())).Decision);
            var adjusted = await Counts(owner).AdjustAsync(approver, created.Id!.Value, request, operation, Guid.NewGuid());
            Assert.Equal(CountDecision.Committed, adjusted.Decision);
            Assert.Equal(adjusted with { Decision = CountDecision.Replayed }, await Counts(owner).AdjustAsync(approver,
                created.Id.Value, request, operation, Guid.NewGuid()));
        }
        await using var verify = Open(_tenant);
        Assert.Equal(expected, (await verify.Balances.SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        Assert.Equal(postings, await verify.Postings.CountAsync(row => row.SourceDocumentId == created.Id));
    }

    private CountAccess Counts(Wes.Wms.Infrastructure.Persistence.WmsDbContext owner,
        IMasterDataAuthorization? authority = null, TimeProvider? clock = null) =>
        new(owner, authority ?? new CountAuthority(), new Schema(LocationSchemaDecision.Valid), clock ?? _clock);
}
