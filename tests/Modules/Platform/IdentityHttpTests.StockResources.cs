using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Platform.Domain;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("scoped")]
    [InlineData("transient")]
    [InlineData("keyed")]
    public async Task StockResourceHttpRegistrationRequiresExplicitUniqueSingletonSchema(string invalid)
    {
        var schema = new LocationHttpSchema();
        void Register(IServiceCollection services)
        {
            if (invalid == "duplicate") { services.AddSingleton<IWarehouseLocationSchema>(schema); services.AddSingleton<IWarehouseLocationSchema>(schema); }
            if (invalid == "scoped") services.AddScoped<IWarehouseLocationSchema>(_ => schema);
            if (invalid == "transient") services.AddTransient<IWarehouseLocationSchema>(_ => schema);
            if (invalid == "keyed") services.AddKeyedSingleton<IWarehouseLocationSchema>("not-default", schema);
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WmsHostAsync(stockResources: true, locationSchemas: Register));
        Assert.Contains("Location writes require exactly one explicitly configured singleton schema adapter.", error.Message);
    }

    [Fact]
    public async Task StockResourceHttpRegistrationIsDefaultDisabledAndDoesNotImplyOtherWrites()
    {
        await WmsHostAsync();
        using var disabled = await ResourcePostAsync("units", Guid.NewGuid(), new { code = "EA", name = "Each", countable = true, precision = 0 });
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.StockUnits.ToListAsync());
    }

    [Fact]
    public async Task StockResourceHttpRegistersAndReadsOnlyExactCurrentProjectionsAndZeroBalance()
    {
        var ids = await StockResourceHostAsync(operational: true);
        var unitBody = new { code = "ea", name = "Each", countable = true, precision = 0 };
        var operation = Guid.NewGuid();
        using var first = await ResourcePostAsync("units", operation, unitBody);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var unit = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, unit.EnumerateObject().Select(row => row.Name));
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var replayed = await ResourcePostAsync("units", operation, unitBody);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        Assert.Equal(unit.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        using var itemResponse = await ResourcePostAsync("items", Guid.NewGuid(),
            new { code = "ITEM", name = "Item", baseUnitId = unit.GetProperty("id").GetGuid(), batchRequired = false });
        Assert.Equal(HttpStatusCode.Created, itemResponse.StatusCode);
        var item = await itemResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var containerResponse = await ResourcePostAsync("containers", Guid.NewGuid(), new { code = "PALLET", name = "Pallet", warehouseId = ids.Warehouse });
        Assert.Equal(HttpStatusCode.Created, containerResponse.StatusCode);
        var container = await containerResponse.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var (response, fields) in new[]
        {
            (first, new[] { "id", "code", "name", "countable", "precision", "version", "createdAt" }),
            (itemResponse, new[] { "id", "code", "name", "baseUnitId", "batchRequired", "version", "createdAt" }),
            (containerResponse, new[] { "id", "code", "name", "warehouseId", "version", "createdAt" })
        })
        {
            using var read = await _client.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var json = await read.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(fields, json.EnumerateObject().Select(row => row.Name));
            Assert.DoesNotContain("actor", json.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("operation", json.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        var dimensionOperation = Guid.NewGuid();
        var dimensionBody = new
        {
            warehouseId = ids.Warehouse,
            locationId = ids.Location,
            itemId = item.GetProperty("id").GetGuid(),
            batch = "",
            containerId = container.GetProperty("id").GetGuid()
        };
        using var dimensionResponse = await ResourcePostAsync("dimensions", dimensionOperation, dimensionBody);
        Assert.Equal(HttpStatusCode.Created, dimensionResponse.StatusCode);
        using var dimensionReplay = await ResourcePostAsync("dimensions", dimensionOperation, dimensionBody);
        Assert.Equal(HttpStatusCode.OK, dimensionReplay.StatusCode);
        using var otherWrite = await _client.PostAsJsonAsync("/api/wms/warehouses", new { code = "NO", name = "No" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, otherWrite.StatusCode);
        await using var verify = OpenWmsMasterData();
        var balance = await verify.Balances.SingleAsync(); Assert.Equal(0m, balance.Quantity); Assert.Equal(0, balance.Precision);
        Assert.Single(await verify.DimensionRegistrations.ToListAsync()); Assert.Empty(await verify.Entries.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync()); Assert.Empty(await verify.InboundFacts.ToListAsync());
    }

    [Theory]
    [InlineData("missing-countable")]
    [InlineData("missing-precision")]
    [InlineData("unknown")]
    [InlineData("numeric-bool")]
    [InlineData("countable-precision")]
    [InlineData("range")]
    public async Task StockResourceHttpRejectsMalformedUnitWithoutFacts(string variant)
    {
        await StockResourceHostAsync();
        var json = System.Text.Json.Nodes.JsonNode.Parse("""{"code":"EA","name":"Each","countable":true,"precision":0}""")!.AsObject();
        switch (variant)
        {
            case "missing-countable": json.Remove("countable"); break;
            case "missing-precision": json.Remove("precision"); break;
            case "unknown": json["actorId"] = _user.ToString("D"); break;
            case "numeric-bool": json["countable"] = 1; break;
            case "countable-precision": json["precision"] = 1; break;
            default: json["countable"] = false; json["precision"] = 7; break;
        }
        using var content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/units") { Content = content };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.StockUnits.ToListAsync());
    }

    [Theory]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("Countable")]
    [InlineData("Precision")]
    [InlineData("role")]
    [InlineData("membership")]
    public async Task StockResourceHttpRechecksCurrentUnitWriteOnOriginalReplay(string revoke)
    {
        await StockResourceHostAsync();
        var operation = Guid.NewGuid(); var body = new { code = "EA", name = "Each", countable = true, precision = 0 };
        using var first = await ResourcePostAsync("units", operation, body); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(row => row.Code == "STOCK-RESOURCES");
            if (revoke == "membership") platform.Memberships.Remove(await platform.Memberships.SingleAsync(row => row.RoleId == role.Id));
            else role.Change(role.Version, revoke != "role", role.Rules.Select(rule => rule.Permission == "wms.units.write"
                ? rule with { Fields = rule.Fields.Remove(revoke) } : rule));
            await platform.SaveChangesAsync();
        }
        using var denied = await ResourcePostAsync("units", operation, body);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("version", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http", HttpStatusCode.BadRequest)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("session", HttpStatusCode.Unauthorized)]
    [InlineData("key", HttpStatusCode.BadRequest)]
    [InlineData("client", HttpStatusCode.BadRequest)]
    public async Task StockResourceHttpRequiresTrustedTransportAndIdentity(string variant, HttpStatusCode status)
    {
        await StockResourceHostAsync();
        if (variant == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (variant == "session") { using var logout = await _client.PostAsync("/api/identity/logout", null); }
        if (variant == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{(variant == "http" ? "http://localhost" : "")}/api/wms/units")
        { Content = JsonContent.Create(new { code = "EA", name = "Each", countable = true, precision = 0 }) };
        if (variant != "key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request); Assert.Equal(status, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.StockUnits.ToListAsync());
    }

    private async Task<(Guid Warehouse, Guid Location)> StockResourceHostAsync(bool operational = false, bool inboundApproval = false, bool inboundReceipt = false)
    {
        var ids = await WmsHostAsync(stockResources: true, warehouseState: operational, locationState: operational,
            locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(new LocationHttpSchema()),
            inboundApproval: inboundApproval, inboundReceipt: inboundReceipt);
        await GrantStockResourcesAsync();
        if (operational)
        {
            await GrantWarehouseStateAsync(); await GrantLocationStateAsync();
            using var enabledParent = await StateHttpAsync(ids.Warehouse, Guid.NewGuid(), await StateInputAsync(ids.Warehouse));
            Assert.Equal(HttpStatusCode.OK, enabledParent.StatusCode);
            using var enabledLocation = await LocationStateHttpAsync(ids.Location, Guid.NewGuid(), await LocationStateInputAsync(ids.Location));
            Assert.Equal(HttpStatusCode.OK, enabledLocation.StatusCode);
        }
        return ids;
    }

    private async Task GrantStockResourcesAsync()
    {
        await using (var platform = Open())
        {
            var basic = ImmutableHashSet.Create("Id", "Code", "Name", "Version", "CreatedAt");
            var role = new AccessRole(_tenant, "STOCK-RESOURCES",
            [
                new("wms.units.write", DataScope.Own, [], ImmutableHashSet.Create("Code", "Name", "Countable", "Precision")),
                new("wms.units.read", DataScope.Own, [], basic.Union(["Countable", "Precision"])),
                new("wms.items.write", DataScope.Own, [], ImmutableHashSet.Create("Code", "Name", "BaseUnitId", "BatchRequired")),
                new("wms.items.read", DataScope.Own, [], basic.Union(["BaseUnitId", "BatchRequired"])),
                new("wms.containers.write", DataScope.Own, [], ImmutableHashSet.Create("Code", "Name", "WarehouseId")),
                new("wms.containers.read", DataScope.Own, [], basic.Add("WarehouseId")),
                new("wms.dimensions.write", DataScope.Own, [], ImmutableHashSet.Create("WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId"))
            ]);
            platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id)); await platform.SaveChangesAsync();
        }
    }

    private async Task<HttpResponseMessage> ResourcePostAsync(string kind, Guid operation, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/wms/{kind}") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D")); return await _client.SendAsync(request);
    }
}
