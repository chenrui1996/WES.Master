using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Transfers;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("dispatch")]
    [InlineData("arrival")]
    [InlineData("sync")]
    [InlineData("unknown")]
    [InlineData("close")]
    public async Task TransferEverySaveRollsBackWhenCurrentCompleteAuthorityRevokesAfterSaving(string action)
    {
        var fixture = await TransferReadyAsync();
        if (action == "create")
        {
            var createAuthority = new TransferAuthority();
            await using var revoked = Open(_tenant, new RevokeTransferAfterSave(createAuthority));
            Assert.Equal(TransferDecision.Forbidden, (await Transfers(revoked, createAuthority).CreateAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-REVOKED", fixture.Warehouse,
                    [new(fixture.A, fixture.SourceA, fixture.TargetA, "3", 0)]), CancellationToken.None)).Decision);
            await using var empty = Open(_tenant); Assert.Empty(await empty.TransferDocuments.ToArrayAsync());
            return;
        }
        var created = await CreateTransferAsync(fixture); var detail = await TransferDetailAsync(created.Id!.Value);
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        detail = await TransferDetailAsync(detail.Id);
        if (action is "arrival" or "sync" or "unknown")
        {
            await using var owner = Open(_tenant);
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).DispatchAsync(Reader, detail.Id,
                new(detail.Version, fixture.A), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            detail = await TransferDetailAsync(detail.Id);
        }
        if (action == "sync")
        {
            var line = detail.Lines[0];
            await using var owner = Open(_tenant, new FailTransferPosting());
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ArriveAsync(Reader, detail.Id,
                new(detail.Version, fixture.A, line.SourceLocationId, line.TargetLocationId, line.ContainerId, "3", "Final scans"),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            detail = await TransferDetailAsync(detail.Id);
        }
        var before = detail; var authority = new TransferAuthority(); TransferResult result;
        await using (var owner = Open(_tenant, new RevokeTransferAfterSave(authority)))
        {
            var access = Transfers(owner, authority); var operation = Guid.NewGuid(); var correlation = Guid.NewGuid(); var line = detail.Lines[0];
            result = action switch
            {
                "dispatch" => await access.DispatchAsync(Reader, detail.Id, new(detail.Version, fixture.A), operation, correlation, CancellationToken.None),
                "arrival" => await access.ArriveAsync(Reader, detail.Id,
                    new(detail.Version, fixture.A, line.SourceLocationId, line.TargetLocationId, line.ContainerId, "3", "Final scans"),
                    operation, correlation, CancellationToken.None),
                "sync" => await access.SyncAsync(Reader, detail.Id, new(detail.Version, fixture.A), operation, correlation, CancellationToken.None),
                "unknown" => await access.UnknownAsync(Reader, detail.Id, new(detail.Version, fixture.A, "Unknown"),
                    operation, correlation, CancellationToken.None),
                _ => await access.CloseAsync(Reader, detail.Id, new(detail.Version, "Safe closure"),
                    operation, correlation, CancellationToken.None)
            };
        }
        Assert.Equal(TransferDecision.Forbidden, result.Decision);
        var after = await TransferDetailAsync(detail.Id); Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Lines, after.Lines);
        await using var verify = Open(_tenant); Assert.Equal(4m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.Empty(await verify.TransferOutbox.ToArrayAsync());
        Assert.Empty(await verify.Postings.Where(row => row.Kind == StockPostingKind.Transfer).ToArrayAsync());
    }
}
