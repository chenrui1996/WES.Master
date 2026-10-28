using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    private bool _wmsMasterDataReady;
    private readonly Guid _wmsForeignTenant = Guid.NewGuid();

    [Fact]
    public async Task WmsDetailsAreAbsentUnlessExplicitlyAssembled()
    {
        using var response = await _client.GetAsync($"/api/wms/warehouses/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsDetailsReturnOnlyCurrentAuthorizedFixedProjection(bool location)
    {
        var ids = await WmsHostAsync();
        using var response = await _client.GetAsync($"/api/wms/{(location ? "locations" : "warehouses")}/{(location ? ids.Location : ids.Warehouse):D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(location ? ids.Location : ids.Warehouse, json.GetProperty("id").GetGuid());
        Assert.False(json.GetProperty("enabled").GetBoolean());
        string[] expectedFields = ["id", "code", "name", "enabled", "version", "createdAt"];
        if (location) expectedFields = [.. expectedFields, "warehouseId", "packId", "schemaVersion"];
        Assert.Equal(expectedFields.Order(StringComparer.Ordinal),
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(location ? "A1" : "WH-HTTP", json.GetProperty("code").GetString());
        Assert.Equal(location ? "HTTP location" : "HTTP warehouse", json.GetProperty("name").GetString());
        Assert.Equal(_clock.GetUtcNow(), json.GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(Guid.TryParseExact(json.GetProperty("version").GetString(), "D", out _));
        if (location)
        {
            Assert.Equal(ids.Warehouse, json.GetProperty("warehouseId").GetGuid());
            Assert.Equal("v1", json.GetProperty("schemaVersion").GetString());
        }
        Assert.DoesNotContain("actor", json.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operation", json.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quantity", json.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var owner = OpenWmsMasterData();
        Assert.Equal(2, await owner.MasterDataChanges.CountAsync());
    }

    [Fact]
    public async Task WmsDetailsRequireAnAuthenticatedHttpsRequest()
    {
        var ids = await WmsHostAsync();
        var token = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync($"/api/wms/warehouses/{ids.Warehouse:D}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        _client.DefaultRequestHeaders.Authorization = token;
        using var plain = await _client.GetAsync($"http://localhost/api/wms/warehouses/{ids.Warehouse:D}");
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsDetailsRejectMissingAndEmptyIdentities(bool empty)
    {
        await WmsHostAsync();
        using var response = await _client.GetAsync($"/api/wms/locations/{(empty ? Guid.Empty : Guid.NewGuid()):D}");
        Assert.Equal(empty ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsDetailsRecheckCurrentFieldsAndRolesOnEveryRequest(bool roleDisabled)
    {
        var ids = await WmsHostAsync();
        using var first = await _client.GetAsync($"/api/wms/warehouses/{ids.Warehouse:D}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync(item => item.Code == "WMS");
            role.Change(role.Version, !roleDisabled, role.Rules.Select(rule =>
                rule with { Fields = rule.Fields.Remove("Name") }));
            await writer.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync($"/api/wms/warehouses/{ids.Warehouse:D}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("WH-HTTP", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WmsDetailsCannotUseARevokedSession()
    {
        var ids = await WmsHostAsync();
        using var logout = await _client.PostAsync("/api/identity/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var denied = await _client.GetAsync($"/api/wms/warehouses/{ids.Warehouse:D}");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task WmsDetailsCannotTakeAuthorityFromQueryOrHeadersOrExposeWriteMethods()
    {
        var ids = await WmsHostAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/wms/warehouses/{ids.Warehouse:D}?tenantId={Guid.NewGuid():D}&creatorId={Guid.NewGuid():D}");
        request.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var write = await _client.PostAsJsonAsync($"/api/wms/warehouses/{ids.Warehouse:D}", new { enabled = true });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
        using var update = await _client.PutAsJsonAsync($"/api/wms/warehouses/{ids.Warehouse:D}", new { enabled = true });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, update.StatusCode);
        using var delete = await _client.DeleteAsync($"/api/wms/warehouses/{ids.Warehouse:D}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
    }

    [Fact]
    public async Task WmsDetailsCannotReadAnActualForeignTenantDefinition()
    {
        await WmsHostAsync();
        Guid id;
        await using (var foreign = OpenWmsMasterData(_wmsForeignTenant))
        {
            var warehouse = new Warehouse(_wmsForeignTenant, "WH-HTTP", "Foreign", _clock.GetUtcNow());
            foreign.Warehouses.Add(warehouse);
            foreign.MasterDataChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), warehouse));
            await foreign.SaveChangesAsync();
            id = warehouse.Id;
        }
        using var response = await _client.GetAsync($"/api/wms/warehouses/{id:D}?tenantId={_wmsForeignTenant:D}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Foreign", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<(Guid Warehouse, Guid Location)> WmsHostAsync(bool warehouseCreation = false,
        bool locationCreation = false, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? locationSchemas = null,
        string? filesRoot = null, bool inboundDraftCreation = false, bool inboundDraftRevision = false, bool warehouseState = false,
        bool locationState = false, bool stockResources = false, bool inboundApproval = false, bool inboundReceipt = false)
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = new Factory(_connection, _corpus, _clock, wmsMasterData: true, wmsWarehouseCreation: warehouseCreation,
            wmsLocationCreation: locationCreation, locationSchemas: locationSchemas, filesRoot: filesRoot,
            wmsInboundDraftCreation: inboundDraftCreation, wmsInboundDraftRevision: inboundDraftRevision, wmsWarehouseState: warehouseState,
            wmsLocationState: locationState, wmsStockResources: stockResources, wmsInboundApproval: inboundApproval, wmsInboundReceipt: inboundReceipt);
        _client = Client();
        await using (var seed = OpenWmsMasterData())
        {
            await seed.Database.MigrateAsync();
            _wmsMasterDataReady = true;
            var warehouse = new Warehouse(_tenant, "WH-HTTP", "HTTP warehouse", _clock.GetUtcNow());
            var location = new WarehouseLocation(_tenant, warehouse.Id, Guid.NewGuid(), "v1", "A1", "HTTP location", _clock.GetUtcNow());
            seed.Warehouses.Add(warehouse);
            seed.Locations.Add(location);
            seed.MasterDataChanges.AddRange(new MasterDataChange(Guid.NewGuid(), _user, Guid.NewGuid(), warehouse),
                new MasterDataChange(Guid.NewGuid(), _user, Guid.NewGuid(), location));
            await seed.SaveChangesAsync();
        }
        await using (var platform = Open())
        {
            var fields = ImmutableHashSet.Create("Id", "Code", "Name", "Enabled", "Version", "CreatedAt");
            var role = new AccessRole(_tenant, "WMS",
            [
                new("wms.warehouses.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, fields),
                new("wms.locations.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, fields.Union(["WarehouseId", "PackId", "SchemaVersion"]))
            ]);
            platform.Roles.Add(role);
            platform.Memberships.Add(new(_tenant, _user, role.Id));
            await platform.SaveChangesAsync();
        }
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        await using var read = OpenWmsMasterData();
        return ((await read.Warehouses.SingleAsync()).Id, (await read.Locations.SingleAsync()).Id);
    }

    private WmsDbContext OpenWmsMasterData(Guid? tenant = null) => new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant ?? _tenant);

    private async Task RemoveWmsMasterDataAsync()
    {
        if (!_wmsMasterDataReady) return;
        foreach (var tenant in new[] { _tenant, _wmsForeignTenant })
        {
            await using var owner = OpenWmsMasterData(tenant);
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_outbound_facts WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_count_facts WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_counts WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_outbound_documents WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_transport_inbox WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_transport_intents WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_execution_operations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_facts WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_documents WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_reservation_changes WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_reservations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_entries WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_postings WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_dimension_registrations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_balances WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_containers WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_items WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_units WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_outbox_messages WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_location_state_changes WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_warehouse_state_changes WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_master_data_changes WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_locations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_warehouses WHERE TenantId = {tenant.ToString("D")}");
        }
    }
}
