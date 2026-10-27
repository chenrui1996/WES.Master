using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    [Fact]
    public async Task InboundCatalogFiltersRealCreatorBeforePagingAndCanSelectTheCurrentDetail()
    {
        var (document, line) = await InboundAsync();
        await using (var seed = Open(_tenant))
        {
            for (var index = 0; index < 26; index++)
                seed.InboundDocuments.Add(new(_tenant, Guid.NewGuid(), Guid.NewGuid(),
                    document.WarehouseId, $"A-{index:D2}", [line], _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var access = new InboundAccess(owner, new InboundAuthority(_actor), NullLogger<InboundAccess>.Instance);
        var page = await access.QueryAsync(InboundReader, new());
        Assert.Equal(InboundReadDecision.Success, page.Decision);
        Assert.Equal(document.Id, Assert.Single(page.Rows).Id);
        Assert.Equal(InboundReadDecision.Success, (await access.ReadAsync(InboundReader, page.Rows[0].Id)).Decision);
    }

    [Fact]
    public async Task InboundCatalogPagesInStableCodeOrderAndUsesExactFilters()
    {
        var (document, line) = await InboundAsync();
        await using (var seed = Open(_tenant))
        {
            for (var index = 0; index < 30; index++)
                seed.InboundDocuments.Add(new(_tenant, Guid.NewGuid(), _actor, document.WarehouseId,
                    $"A-{index:D2}", [line], _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var access = new InboundAccess(owner, new InboundAuthority(_actor), NullLogger<InboundAccess>.Instance);
        var first = await access.QueryAsync(InboundReader, new());
        var next = await access.QueryAsync(InboundReader, new(25));
        Assert.Equal(25, first.Rows.Length);
        Assert.Equal(6, next.Rows.Length);
        Assert.Equal("A-00", first.Rows[0].Code);
        Assert.Equal("A-25", next.Rows[0].Code);
        Assert.Equal("IN-1", next.Rows[^1].Code);
        Assert.Equal(31, first.Rows.Concat(next.Rows).Select(row => row.Id).Distinct().Count());
        var exact = await access.QueryAsync(InboundReader, new(Code: "in-1", WarehouseId: document.WarehouseId, State: InboundState.Approved));
        Assert.Equal(document.Id, Assert.Single(exact.Rows).Id);
        Assert.Empty((await access.QueryAsync(InboundReader, new(Code: "in-1", State: InboundState.Draft))).Rows);
        Assert.Empty((await access.QueryAsync(InboundReader, new(WarehouseId: Guid.NewGuid()))).Rows);
        Assert.Empty(owner.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(-1, 25, null, null)]
    [InlineData(10001, 25, null, null)]
    [InlineData(0, 1, null, null)]
    [InlineData(0, 25, "", null)]
    [InlineData(0, 25, " space", null)]
    [InlineData(0, 25, "汉", null)]
    [InlineData(0, 25, null, -1)]
    [InlineData(0, 25, null, 4)]
    public async Task InboundCatalogRejectsInvalidBoundsAndFiltersBeforeAuthority(int offset, int limit, string? code, int? state)
    {
        var authority = new InboundAuthority(_actor);
        await using var owner = Open(_tenant);
        var result = await new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance).QueryAsync(InboundReader,
            new(offset, limit, code, State: state.HasValue ? (InboundState)state.Value : null));
        Assert.Equal(InboundReadDecision.Invalid, result.Decision);
        Assert.Empty(result.Rows);
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task InboundCatalogCurrentRevocationDiscardsMaterializedRows()
    {
        await InboundAsync();
        await using var owner = Open(_tenant);
        var authority = new InboundAuthority(_actor) { RevokeAfterFirst = true };
        var result = await new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance).QueryAsync(InboundReader, new());
        Assert.Equal(InboundReadDecision.Forbidden, result.Decision);
        Assert.Empty(result.Rows);
        Assert.Equal(2, authority.Calls);
    }

    [Fact]
    public async Task InboundCatalogCurrentCreatorScopeShrinkDiscardsPreviouslyVisibleRows()
    {
        await InboundAsync();
        await using var owner = Open(_tenant);
        var result = await new InboundAccess(owner, new InboundAuthority(_actor) { NoCreatorsAfterFirst = true },
            NullLogger<InboundAccess>.Instance).QueryAsync(InboundReader, new());
        Assert.Equal(InboundReadDecision.Forbidden, result.Decision);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task InboundCatalogAllowedEmptyCreatorsAreNotMissingAuthority()
    {
        await InboundAsync();
        await using var owner = Open(_tenant);
        var result = await new InboundAccess(owner, new InboundAuthority(_actor) { NoCreators = true },
            NullLogger<InboundAccess>.Instance).QueryAsync(InboundReader, new());
        Assert.Equal(InboundReadDecision.Success, result.Decision);
        Assert.Empty(result.Rows);
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
    public async Task InboundCatalogRequiresCurrentCompleteProjection(string missing)
    {
        await using var owner = Open(_tenant);
        var result = await new InboundAccess(owner, new InboundAuthority(_actor) { Missing = missing },
            NullLogger<InboundAccess>.Instance).QueryAsync(InboundReader, new());
        Assert.Equal(InboundReadDecision.Forbidden, result.Decision);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task InboundCatalogCancellationAfterMaterializationDisclosesNoPage()
    {
        await InboundAsync();
        using var cancel = new CancellationTokenSource();
        await using var owner = Open(_tenant);
        var authority = new InboundAuthority(_actor) { OnSecond = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InboundAccess(owner, authority, NullLogger<InboundAccess>.Instance).QueryAsync(InboundReader, new(), cancel.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundCatalogSanitizesDatabaseFailures(bool transient)
    {
        await using var owner = Open(_tenant, new QueryFault(transient, "wms_inbound_documents"));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            new InboundAccess(owner, new InboundAuthority(_actor), NullLogger<InboundAccess>.Instance).QueryAsync(InboundReader, new()));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
    }
}
