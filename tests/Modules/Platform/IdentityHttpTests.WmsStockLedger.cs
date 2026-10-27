using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    private static readonly string[] StockLedgerReadFields = ["Id", "PostingId", "BalanceId", "WarehouseId", "LocationId",
        "ItemId", "Batch", "ContainerId", "UnitId", "Precision", "Kind", "Quantity", "Before", "After",
        "SourceDocumentId", "SourceLineId", "ExecutionUnitId", "CorrelationId", "OccurredAt"];

    [Fact]
    public async Task WmsStockLedgerIsAbsentUnlessExplicitlyAssembled()
    {
        using var response = await _client.GetAsync("/api/wms/ledger");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsStockLedgerExposesOnlyExactImmutableProvenanceAndLosslessSignedAmounts()
    {
        var balance = await StockLedgerHostAsync();
        await using (var database = OpenWmsMasterData())
        {
            var current = await database.Balances.SingleAsync(item => item.Id == balance.Id);
            database.ChangeTracker.Clear();
            Assert.Equal(StockPostingDecision.Posted, (await new PostStock(database, _clock).ExecuteAsync(new(
                Guid.NewGuid(), _user, Guid.NewGuid(), StockPostingKind.Shipment, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), current.Id, null, 0.000001m, current.Version, null))).Decision);
        }
        using var response = await _client.GetAsync("/api/wms/ledger");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "rows", "offset", "limit" }, page.EnumerateObject().Select(property => property.Name));
        var rows = page.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(StockLedgerReadFields.Select(field => char.ToLowerInvariant(field[0]) + field[1..]),
                row.EnumerateObject().Select(property => property.Name));
            Assert.Equal(balance.Id, row.GetProperty("balanceId").GetGuid());
        });
        var receipt = Assert.Single(rows, row => row.GetProperty("kind").GetInt32() == 0);
        Assert.Equal("999999999.999999", receipt.GetProperty("quantity").GetString());
        Assert.Equal("0.000000", receipt.GetProperty("before").GetString());
        var shipment = Assert.Single(rows, row => row.GetProperty("kind").GetInt32() == 1);
        Assert.Equal("-0.000001", shipment.GetProperty("quantity").GetString());
        Assert.Equal("999999999.999998", shipment.GetProperty("after").GetString());
        using var write = await _client.PostAsJsonAsync("/api/wms/ledger", new { quantity = "1" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WmsStockLedgerDoesNotSubstitutePostingActorOrDepartmentForTenantWideAuthority(int scope)
    {
        await StockLedgerHostAsync((DataScope)scope);
        using var response = await _client.GetAsync("/api/wms/ledger");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("field")]
    [InlineData("membership")]
    public async Task WmsStockLedgerRechecksAuthorityWithoutBorrowingInventoryOrOtherScopeFields(string revoked)
    {
        await StockLedgerHostAsync();
        using var first = await _client.GetAsync("/api/wms/ledger");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync(item => item.Code == "LEDGER-READ");
            if (revoked == "membership") writer.Memberships.Remove(await writer.Memberships.SingleAsync(item => item.RoleId == role.Id));
            else role.Change(role.Version, revoked != "role",
            [
                new("wms.ledger.read", DataScope.All, [], StockLedgerReadFields.Where(field => field != "Quantity").ToImmutableHashSet()),
                new("wms.ledger.read", DataScope.Own, [], StockLedgerReadFields.ToImmutableHashSet())
            ]);
            await writer.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync("/api/wms/ledger");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("999999999", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WmsStockLedgerRequiresEveryIndependentAllField()
    {
        await StockLedgerHostAsync();
        await using var platform = Open();
        var role = await platform.Roles.SingleAsync(item => item.Code == "LEDGER-READ");
        foreach (var missing in StockLedgerReadFields)
        {
            role.Change(role.Version, true,
            [
                new("wms.ledger.read", DataScope.All, [], StockLedgerReadFields.Where(field => field != missing).ToImmutableHashSet()),
                new("wms.ledger.read", DataScope.Own, [], ImmutableHashSet.Create(missing))
            ]);
            await platform.SaveChangesAsync();
            using var response = await _client.GetAsync("/api/wms/ledger");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("?offset=-1")]
    [InlineData("?offset=10001")]
    [InlineData("?limit=1")]
    [InlineData("?locationId=bad")]
    [InlineData("?sourceDocumentId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?correlationId=bad")]
    [InlineData("?kind=-1")]
    [InlineData("?kind=3")]
    [InlineData("?kind=Receipt")]
    [InlineData("?batch=%20Batch")]
    [InlineData("?from=2026-01-01T00:00:00.0000001Z")]
    [InlineData("?from=2026-01-01T00:00:00%2B01:00")]
    [InlineData("?from=2026-01-01T00:00:00Z&before=2026-01-01T00:00:00Z")]
    [InlineData("?before=bad")]
    [InlineData("?from=2026-01-01T00:00:00")]
    [InlineData("?from=2026-01-01")]
    [InlineData("?before=")]
    public async Task WmsStockLedgerRejectsInvalidQueriesExplicitly(string query)
    {
        await StockLedgerHostAsync();
        using var response = await _client.GetAsync("/api/wms/ledger" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WmsStockLedgerFiltersActualDimensionsAndPostingSourcesBeforePaging()
    {
        var balance = await StockLedgerHostAsync();
        await using var owner = OpenWmsMasterData();
        var posting = await owner.Postings.AsNoTracking().SingleAsync();
        using var response = await _client.GetAsync($"/api/wms/ledger?warehouseId={balance.WarehouseId:D}&locationId={balance.LocationId:D}&itemId={balance.ItemId:D}&containerId={balance.ContainerId:D}&batch=Batch&sourceDocumentId={posting.SourceDocumentId:D}&correlationId={posting.CorrelationId:D}&kind=0&from={Uri.EscapeDataString(posting.OccurredAt.ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        foreach (var query in new[] { "batch=batch", $"sourceDocumentId={Guid.NewGuid():D}", "kind=1", $"before={Uri.EscapeDataString(posting.OccurredAt.ToString("O"))}" })
        {
            using var empty = await _client.GetAsync("/api/wms/ledger?" + query);
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Empty((await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        }
    }

    [Fact]
    public async Task WmsStockLedgerRequiresAuthenticatedHttpsAndCurrentSession()
    {
        await StockLedgerHostAsync();
        var token = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync("/api/wms/ledger");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        _client.DefaultRequestHeaders.Authorization = token;
        using var plain = await _client.GetAsync("http://localhost/api/wms/ledger");
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        using var logout = await _client.PostAsync("/api/identity/logout", null);
        using var revoked = await _client.GetAsync("/api/wms/ledger");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task WmsStockLedgerCannotSelectAnActualForeignTenantPostingUsingClientTenantClaims()
    {
        await StockLedgerHostAsync();
        var foreignWarehouse = Guid.NewGuid();
        var foreignDocument = Guid.NewGuid();
        await using (var foreign = OpenWmsMasterData(_wmsForeignTenant))
        {
            var balance = new StockBalance(_wmsForeignTenant, foreignWarehouse, Guid.NewGuid(), Guid.NewGuid(),
                "Foreign", Guid.NewGuid(), Guid.NewGuid(), 6);
            foreign.Balances.Add(balance);
            await foreign.SaveChangesAsync();
            foreign.ChangeTracker.Clear();
            Assert.Equal(StockPostingDecision.Posted, (await new PostStock(foreign, _clock).ExecuteAsync(new(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StockPostingKind.Receipt, foreignDocument,
                Guid.NewGuid(), Guid.NewGuid(), null, balance.Id, 1.000001m, null, balance.Version))).Decision);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/wms/ledger?warehouseId={foreignWarehouse:D}&sourceDocumentId={foreignDocument:D}&tenantId={_wmsForeignTenant:D}");
        request.Headers.Add("X-Tenant-Id", _wmsForeignTenant.ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
    }

    private async Task<StockBalance> StockLedgerHostAsync(DataScope scope = DataScope.All)
    {
        var balance = await InventoryHostAsync();
        await GrantStockLedgerReadAsync(scope);
        return balance;
    }

    private async Task GrantStockLedgerReadAsync(DataScope scope = DataScope.All)
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "LEDGER-READ",
            [new("wms.ledger.read", scope, scope == DataScope.Departments ? ImmutableHashSet.Create(Guid.NewGuid()) : [],
                StockLedgerReadFields.ToImmutableHashSet(StringComparer.Ordinal))]);
        platform.Roles.Add(role);
        platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
    }
}
