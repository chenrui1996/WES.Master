using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    [Fact]
    public async Task PostingCompositionRunsOnceAndReplaysWithoutRestaging()
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 2m);
        var staged = 0;
        await using (var owner = Open(_tenant))
            Assert.Equal(StockPostingDecision.Posted, (await new PostStock(owner, _clock)
                .ExecuteComposedAsync(command, _ => { staged++; return Task.CompletedTask; })).Decision);
        await using (var owner = Open(_tenant))
            Assert.Equal(StockPostingDecision.Replayed, (await new PostStock(owner, _clock)
                .ExecuteComposedAsync(command, _ => { staged++; return Task.CompletedTask; })).Decision);
        Assert.Equal(1, staged);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Entries.ToListAsync());
        Assert.Equal(2m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
    }

    [Fact]
    public async Task CompositionFailureLeavesNoLedgerOrOperationAndOriginalCommandCanRetry()
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 2m);
        await using (var owner = Open(_tenant))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostStock(owner, _clock)
                .ExecuteComposedAsync(command, _ => throw new InvalidOperationException("Injected inbound staging failure.")));
        await using (var verify = Open(_tenant))
        {
            Assert.Empty(await verify.Entries.ToListAsync());
            Assert.Empty(await verify.Postings.ToListAsync());
            Assert.Equal(0m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        }
        Assert.Equal(StockPostingDecision.Posted, (await ExecuteAsync(command)).Decision);
    }
}
