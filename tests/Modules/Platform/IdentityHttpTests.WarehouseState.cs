using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Host.Wms;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task WarehouseStateHttpIsDefaultDisabledEvenWhenCreationEnabled()
    {
        var ids = await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseStateAsync();
        using var response = await StateHttpAsync(ids.Warehouse, Guid.NewGuid(), await StateInputAsync(ids.Warehouse));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.WarehouseStateChanges.ToListAsync());
    }

    [Fact]
    public async Task WarehouseStateHttpRoundTripRecoversOriginalVersionAndDoesNotEnableChildren()
    {
        var ids = await WmsHostAsync(warehouseState: true);
        await GrantWarehouseStateAsync();
        var input = await StateInputAsync(ids.Warehouse);
        var operation = Guid.NewGuid();
        using var enable = await StateHttpAsync(ids.Warehouse, operation, input);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal("no-store", enable.Headers.CacheControl?.ToString());
        var receipt = await enable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, receipt.EnumerateObject().Select(property => property.Name));
        Assert.Equal(ids.Warehouse, receipt.GetProperty("id").GetGuid());
        Assert.Equal(0, receipt.GetProperty("decision").GetInt32());
        using var current = await _client.GetAsync($"/api/wms/warehouses/{ids.Warehouse:D}");
        Assert.True((await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        using var disable = await StateHttpAsync(ids.Warehouse, Guid.NewGuid(),
            input with { ExpectedVersion = receipt.GetProperty("version").GetString()!, Enabled = false, Reason = "No work" });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        using var replayed = await StateHttpAsync(ids.Warehouse, operation, input);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        Assert.Equal(receipt.GetProperty("version").GetString(), replay.GetProperty("version").GetString());
        using var conflict = await StateHttpAsync(ids.Warehouse, operation, input with { Reason = "Other" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var create = await _client.PostAsJsonAsync("/api/wms/warehouses", new { code = "SHOULD-NOT-CREATE", name = "No" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, create.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
        Assert.False((await verify.Locations.SingleAsync()).Enabled);
        Assert.Equal(2, await verify.WarehouseStateChanges.CountAsync());
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.Empty(await verify.Balances.ToListAsync());
    }

    [Theory]
    [InlineData("write:Enabled")]
    [InlineData("Id")]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("Enabled")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    [InlineData("role")]
    [InlineData("membership")]
    public async Task WarehouseStateHttpRequiresCurrentFieldsRoleAndMembershipOnReplay(string revoke)
    {
        var ids = await WmsHostAsync(warehouseState: true);
        await GrantWarehouseStateAsync();
        var input = await StateInputAsync(ids.Warehouse);
        var operation = Guid.NewGuid();
        using var first = await StateHttpAsync(ids.Warehouse, operation, input);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var platform = Open())
        {
            if (revoke.StartsWith("write:", StringComparison.Ordinal) || revoke is "role" or "membership")
            {
                var role = await platform.Roles.SingleAsync(item => item.Code == "WAREHOUSE-STATE");
                if (revoke == "membership") platform.Memberships.Remove(await platform.Memberships.SingleAsync(item => item.RoleId == role.Id));
                else role.Change(role.Version, revoke != "role",
                    [new("wms.warehouses.write", DataScope.Own, [], ImmutableHashSet.Create("Code", "Name"))]);
            }
            else
            {
                var role = await platform.Roles.SingleAsync(item => item.Code == "WMS");
                role.Change(role.Version, true, [new("wms.warehouses.read", DataScope.Own, [],
                    new[] { "Id", "Code", "Name", "Enabled", "Version", "CreatedAt" }.Where(field => field != revoke).ToImmutableHashSet())]);
            }
            await platform.SaveChangesAsync();
        }
        using var denied = await StateHttpAsync(ids.Warehouse, operation, input);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("version", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-version")]
    [InlineData("missing-enabled")]
    [InlineData("missing-reason")]
    [InlineData("unknown")]
    [InlineData("number-enabled")]
    [InlineData("invalid-version")]
    [InlineData("empty-reason")]
    [InlineData("trim-reason")]
    public async Task WarehouseStateHttpRejectsStrictMalformedInputWithoutStateFact(string variant)
    {
        var ids = await WmsHostAsync(warehouseState: true);
        await GrantWarehouseStateAsync();
        var input = await StateInputAsync(ids.Warehouse);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(input,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        switch (variant)
        {
            case "missing-version": node.Remove("expectedVersion"); break;
            case "missing-enabled": node.Remove("enabled"); break;
            case "missing-reason": node.Remove("reason"); break;
            case "unknown": node["actorId"] = _user.ToString("D"); break;
            case "number-enabled": node["enabled"] = 1; break;
            case "invalid-version": node["expectedVersion"] = "bad"; break;
            case "empty-reason": node["reason"] = ""; break;
            case "trim-reason": node["reason"] = " Reason "; break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/wms/warehouses/{ids.Warehouse:D}/state")
        { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.WarehouseStateChanges.ToListAsync());
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("session", HttpStatusCode.Unauthorized)]
    [InlineData("http", HttpStatusCode.BadRequest)]
    [InlineData("key", HttpStatusCode.BadRequest)]
    [InlineData("client", HttpStatusCode.BadRequest)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    public async Task WarehouseStateHttpRequiresTrustedTransportAndIdentity(string variant, HttpStatusCode status)
    {
        var ids = await WmsHostAsync(warehouseState: true);
        await GrantWarehouseStateAsync();
        var input = await StateInputAsync(ids.Warehouse);
        if (variant == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (variant == "session") { using var logout = await _client.PostAsync("/api/identity/logout", null); }
        if (variant == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        var id = variant == "empty" ? Guid.Empty : variant == "missing" ? Guid.NewGuid() : ids.Warehouse;
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{(variant == "http" ? "http://localhost" : "")}/api/wms/warehouses/{id:D}/state")
        { Content = JsonContent.Create(input) };
        if (variant != "key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.WarehouseStateChanges.ToListAsync());
    }

    [Fact]
    public async Task WarehouseStateHttpRejectsActualOtherCreatorEvenWithSpoofedCreator()
    {
        await WmsHostAsync(warehouseState: true);
        await GrantWarehouseStateAsync();
        var warehouse = new Wes.Wms.Domain.Warehouse(_tenant, "OTHER", "Other creator", _clock.GetUtcNow());
        await using (var seed = OpenWmsMasterData())
        {
            seed.Warehouses.Add(warehouse);
            seed.MasterDataChanges.Add(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), warehouse));
            await seed.SaveChangesAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Creator-Id", _user.ToString("D"));
        using var denied = await StateHttpAsync(warehouse.Id, Guid.NewGuid(), new(warehouse.Version, true, "Ready"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task WarehouseStateHttpReportsActivityInsteadOfDisablingAnApprovedInboundWarehouse()
    {
        var ids = await WmsHostAsync(warehouseState: true);
        await GrantWarehouseStateAsync();
        using var enabled = await StateHttpAsync(ids.Warehouse, Guid.NewGuid(), await StateInputAsync(ids.Warehouse));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        await SeedInboundAsync(ids, receive: true);
        using var blocked = await StateHttpAsync(ids.Warehouse, Guid.NewGuid(),
            (await StateInputAsync(ids.Warehouse)) with { Enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("WarehouseActivityBlocked", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var verify = OpenWmsMasterData();
        Assert.True((await verify.Warehouses.SingleAsync()).Enabled);
        Assert.Single(await verify.WarehouseStateChanges.ToListAsync());
    }

    private async Task GrantWarehouseStateAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "WAREHOUSE-STATE",
            [new("wms.warehouses.write", DataScope.Own, [], ImmutableHashSet.Create("Enabled"))]);
        platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id)); await platform.SaveChangesAsync();
    }
    private async Task<WarehouseStateInput> StateInputAsync(Guid id)
    {
        await using var database = OpenWmsMasterData();
        return new((await database.Warehouses.SingleAsync(item => item.Id == id)).Version, true, "Ready for logical use");
    }
    private async Task<HttpResponseMessage> StateHttpAsync(Guid id, Guid operation, WarehouseStateInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/wms/warehouses/{id:D}/state") { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D")); return await _client.SendAsync(request);
    }
}
