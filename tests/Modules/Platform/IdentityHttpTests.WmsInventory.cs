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
    private static readonly string[] InventoryReadFields = ["Id", "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId",
        "UnitId", "Precision", "Quantity", "Reserved", "InTransit", "Unknown", "Protected", "Available", "Version"];

    [Fact]
    public async Task WmsInventoryIsAbsentUnlessExplicitlyAssembled()
    {
        using var response = await _client.GetAsync("/api/wms/inventory");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsInventoryReturnsOnlyFixedFieldsAndLosslessQuantitiesWithoutOpeningWrites()
    {
        var balance = await InventoryHostAsync();
        using var response = await _client.GetAsync("/api/wms/inventory");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "rows", "offset", "limit" }, page.EnumerateObject().Select(property => property.Name));
        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal(balance.Id, row.GetProperty("id").GetGuid());
        Assert.Equal("999999999.999999", row.GetProperty("quantity").GetString());
        Assert.Equal("999999999.999999", row.GetProperty("available").GetString());
        Assert.Equal("0.000000", row.GetProperty("protected").GetString());
        Assert.Equal(InventoryReadFields.Select(field => char.ToLowerInvariant(field[0]) + field[1..]),
            row.EnumerateObject().Select(property => property.Name));
        using var write = await _client.PostAsJsonAsync("/api/wms/inventory", new { quantity = 999 });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WmsInventoryNeverMistakesOwnOrDepartmentForTenantWideAuthority(int scope)
    {
        await InventoryHostAsync(scope: (DataScope)scope);
        using var response = await _client.GetAsync("/api/wms/inventory");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("field")]
    [InlineData("membership")]
    public async Task WmsInventoryRechecksCurrentAuthorityOnEveryRequest(string revoked)
    {
        await InventoryHostAsync();
        using var first = await _client.GetAsync("/api/wms/inventory");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync(item => item.Code == "INVENTORY-READ");
            if (revoked == "membership") writer.Memberships.Remove(await writer.Memberships.SingleAsync(item => item.RoleId == role.Id));
            else role.Change(role.Version, revoked != "role", role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Available") }));
            await writer.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync("/api/wms/inventory");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("9999999", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?offset=-1")]
    [InlineData("?offset=10001")]
    [InlineData("?limit=1")]
    [InlineData("?warehouseId=bad")]
    [InlineData("?warehouseId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?batch=%20Batch")]
    [InlineData("?offset=bad")]
    public async Task WmsInventoryRejectsInvalidQueries(string query)
    {
        await InventoryHostAsync();
        using var response = await _client.GetAsync("/api/wms/inventory" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WmsInventoryFiltersBeforePagingAndDoesNotAcceptClientTenantAuthority()
    {
        var balance = await InventoryHostAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/wms/inventory?warehouseId={balance.WarehouseId:D}&locationId={balance.LocationId:D}&itemId={balance.ItemId:D}&containerId={balance.ContainerId:D}&batch=Batch&tenantId={_wmsForeignTenant:D}");
        request.Headers.Add("X-Tenant-Id", _wmsForeignTenant.ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        using var wrongCase = await _client.GetAsync("/api/wms/inventory?batch=batch");
        Assert.Empty((await wrongCase.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task WmsInventoryRequiresAuthenticatedHttpsAndCurrentSession()
    {
        await InventoryHostAsync();
        var token = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync("/api/wms/inventory");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        _client.DefaultRequestHeaders.Authorization = token;
        using var plain = await _client.GetAsync("http://localhost/api/wms/inventory");
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        using var logout = await _client.PostAsync("/api/identity/logout", null);
        using var revoked = await _client.GetAsync("/api/wms/inventory");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task WmsInventoryAllFieldsCannotBorrowMissingFieldsFromOwnGrants()
    {
        await InventoryHostAsync();
        await using var platform = Open();
        var role = await platform.Roles.SingleAsync(item => item.Code == "INVENTORY-READ");
        foreach (var missing in InventoryReadFields)
        {
            role.Change(role.Version, true,
            [
                new("wms.inventory.read", DataScope.All, [], InventoryReadFields.Where(field => field != missing).ToImmutableHashSet()),
                new("wms.inventory.read", DataScope.Own, [], ImmutableHashSet.Create(missing))
            ]);
            await platform.SaveChangesAsync();
            using var denied = await _client.GetAsync("/api/wms/inventory");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }

    [Fact]
    public async Task WmsInventoryDoesNotDiscloseAnActualForeignTenantDimension()
    {
        await InventoryHostAsync();
        var id = Guid.NewGuid();
        await using (var foreign = OpenWmsMasterData(_wmsForeignTenant))
        {
            foreign.Balances.Add(new(_wmsForeignTenant, id, Guid.NewGuid(), Guid.NewGuid(), "Foreign", Guid.NewGuid(), Guid.NewGuid(), 6));
            await foreign.SaveChangesAsync();
        }
        using var response = await _client.GetAsync($"/api/wms/inventory?warehouseId={id:D}&tenantId={_wmsForeignTenant:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
    }

    private async Task<StockBalance> InventoryHostAsync(DataScope scope = DataScope.All)
    {
        var ids = await WmsHostAsync();
        await using (var platform = Open())
        {
            var role = new AccessRole(_tenant, "INVENTORY-READ",
                [new("wms.inventory.read", scope, scope == DataScope.Departments ? ImmutableHashSet.Create(Guid.NewGuid()) : [],
                    InventoryReadFields.ToImmutableHashSet(StringComparer.Ordinal))]);
            platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id));
            await platform.SaveChangesAsync();
        }
        await using var database = OpenWmsMasterData();
        var balance = new StockBalance(_tenant, ids.Warehouse, ids.Location, Guid.NewGuid(), "Batch", Guid.NewGuid(), Guid.NewGuid(), 6);
        database.Balances.Add(balance); await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var posted = await new PostStock(database, _clock).ExecuteAsync(new(Guid.NewGuid(), _user, Guid.NewGuid(),
            StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, balance.Id,
            StockMovementQuantity.MaximumValue, null, balance.Version));
        Assert.Equal(StockPostingDecision.Posted, posted.Decision);
        return balance;
    }
}
