using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData("warehouses")]
    [InlineData("locations")]
    public async Task WmsCatalogIsAbsentWithoutExplicitAssembly(string kind)
    {
        using var response = await _client.GetAsync($"/api/wms/{kind}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsCatalogReturnsOnlyAuthorizedRealtimeRowsAndExactPagingShape(bool location)
    {
        var ids = await WmsHostAsync();
        using var response = await _client.GetAsync($"/api/wms/{(location ? "locations" : "warehouses")}?offset=0&limit=25&enabled=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "limit", "offset", "rows" }, json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(0, json.GetProperty("offset").GetInt32());
        Assert.Equal(25, json.GetProperty("limit").GetInt32());
        var row = Assert.Single(json.GetProperty("rows").EnumerateArray());
        Assert.Equal(location ? ids.Location : ids.Warehouse, row.GetProperty("id").GetGuid());
        Assert.Equal(location ? 9 : 6, row.EnumerateObject().Count());
        Assert.DoesNotContain("actor", row.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("total", json.ToString(), StringComparison.OrdinalIgnoreCase);
        using var next = await _client.GetAsync($"/api/wms/{(location ? "locations" : "warehouses")}?offset=1&limit=50");
        Assert.Empty((await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    [Fact]
    public async Task WmsCatalogFiltersCodeAndWarehouseBeforePagingAndDoesNotTrustClientScope()
    {
        var ids = await WmsHostAsync();
        using var match = await _client.GetAsync($"/api/wms/locations?code=a1&warehouseId={ids.Warehouse:D}&tenantId={Guid.NewGuid():D}&scope=All");
        Assert.Single((await match.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        using var mismatch = await _client.GetAsync($"/api/wms/locations?code=A1&warehouseId={Guid.NewGuid():D}");
        Assert.Empty((await mismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("limit=1")]
    [InlineData("offset=no")]
    [InlineData("enabled=maybe")]
    [InlineData("warehouseId=no")]
    [InlineData("warehouseId=00000000-0000-0000-0000-000000000000")]
    [InlineData("code=BAD%20CODE")]
    [InlineData("code=")]
    public async Task WmsCatalogRejectsInvalidQueryWithoutSuccessPage(string query)
    {
        await WmsHostAsync();
        using var response = await _client.GetAsync($"/api/wms/locations?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("\"rows\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WmsWarehouseCatalogDoesNotSilentlyIgnoreALocationOnlyFilter()
    {
        var ids = await WmsHostAsync();
        using var response = await _client.GetAsync($"/api/wms/warehouses?warehouseId={ids.Warehouse:D}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WmsCatalogRechecksCurrentFieldsAndSessionAndRequiresHttps()
    {
        await WmsHostAsync();
        using var plain = await _client.GetAsync("http://localhost/api/wms/warehouses");
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        using var first = await _client.GetAsync("/api/wms/warehouses");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync(item => item.Code == "WMS");
            role.Change(role.Version, true, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Name") }));
            await writer.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync("/api/wms/warehouses");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("WH-HTTP", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var logout = await _client.PostAsync("/api/identity/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await _client.GetAsync("/api/wms/warehouses");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task WmsCatalogExcludesActualForeignTenantRowsAndCannotCreateViaTheCollection()
    {
        await WmsHostAsync();
        await using (var foreign = OpenWmsMasterData(_wmsForeignTenant))
        {
            var warehouse = new Warehouse(_wmsForeignTenant, "FOREIGN", "Private foreign definition", _clock.GetUtcNow());
            foreign.Warehouses.Add(warehouse);
            foreign.MasterDataChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), warehouse));
            await foreign.SaveChangesAsync();
        }
        using var response = await _client.GetAsync($"/api/wms/warehouses?tenantId={_wmsForeignTenant:D}&scope=All");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("FOREIGN", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var write = await _client.PostAsJsonAsync("/api/wms/warehouses", new { code = "NEW" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
    }

    [Fact]
    public async Task WmsCatalogDoesNotUseWarehouseFieldsToAuthorizeLocationCollections()
    {
        await WmsHostAsync();
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync(item => item.Code == "WMS");
            role.Change(role.Version, true, role.Rules.Where(rule => rule.Permission == "wms.warehouses.read"));
            await writer.SaveChangesAsync();
        }
        using var warehouses = await _client.GetAsync("/api/wms/warehouses");
        Assert.Equal(HttpStatusCode.OK, warehouses.StatusCode);
        using var locations = await _client.GetAsync("/api/wms/locations");
        Assert.Equal(HttpStatusCode.Forbidden, locations.StatusCode);
    }
}
