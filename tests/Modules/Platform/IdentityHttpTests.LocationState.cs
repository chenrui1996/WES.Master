using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Host.Wms;
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
    public async Task LocationStateRejectsUnassembledSchemaBeforeStartupCompletes(string invalid)
    {
        var schema = new LocationHttpSchema();
        void Register(IServiceCollection services)
        {
            if (invalid == "duplicate") { services.AddSingleton<IWarehouseLocationSchema>(schema); services.AddSingleton<IWarehouseLocationSchema>(schema); }
            if (invalid == "scoped") services.AddScoped<IWarehouseLocationSchema>(_ => schema);
            if (invalid == "transient") services.AddTransient<IWarehouseLocationSchema>(_ => schema);
            if (invalid == "keyed") services.AddKeyedSingleton<IWarehouseLocationSchema>("not-default", schema);
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WmsHostAsync(locationState: true, locationSchemas: Register));
        Assert.Contains("Location writes require exactly one explicitly configured singleton schema adapter.", error.Message);
    }

    [Fact]
    public async Task LocationStateHttpIsDefaultDisabledEvenWhenLocationCreationEnabled()
    {
        var ids = await LocationHostAsync(new LocationHttpSchema());
        await GrantLocationStateAsync();
        using var response = await LocationStateHttpAsync(ids.Location, Guid.NewGuid(), await LocationStateInputAsync(ids.Location));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoLocationCreationAsync();
    }

    [Fact]
    public async Task LocationStateHttpRoundTripRecoversOriginalVersionWithoutCreatingResources()
    {
        var schema = new LocationHttpSchema();
        var ids = await StateLocationHostAsync(schema);
        var input = await LocationStateInputAsync(ids.Location); var operation = Guid.NewGuid();
        using var enabled = await LocationStateHttpAsync(ids.Location, operation, input);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal("no-store", enabled.Headers.CacheControl?.ToString());
        var receipt = await enabled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, receipt.EnumerateObject().Select(property => property.Name));
        Assert.Equal(ids.Location, receipt.GetProperty("id").GetGuid()); Assert.Equal(0, receipt.GetProperty("decision").GetInt32());
        using var disabled = await LocationStateHttpAsync(ids.Location, Guid.NewGuid(),
            input with { ExpectedVersion = receipt.GetProperty("version").GetString()!, Enabled = false, Reason = "Stop" });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using var replayed = await LocationStateHttpAsync(ids.Location, operation, input);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        Assert.Equal(receipt.GetProperty("version").GetString(), replay.GetProperty("version").GetString());
        using var conflict = await LocationStateHttpAsync(ids.Location, operation, input with { Reason = "Other" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var create = await _client.PostAsJsonAsync("/api/wms/locations", new { code = "NO", name = "No" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, create.StatusCode);
        Assert.Single(schema.Calls);
        await using var verify = OpenWmsMasterData();
        Assert.False((await verify.Locations.SingleAsync()).Enabled);
        Assert.Equal(2, await verify.LocationStateChanges.CountAsync());
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync()); Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.Empty(await verify.Entries.ToListAsync()); Assert.Empty(await verify.Balances.ToListAsync());
    }

    [Theory]
    [InlineData("write:Enabled")]
    [InlineData("Id")]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("Enabled")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    [InlineData("WarehouseId")]
    [InlineData("PackId")]
    [InlineData("SchemaVersion")]
    [InlineData("parent:Id")]
    [InlineData("parent:Code")]
    [InlineData("parent:Name")]
    [InlineData("parent:Enabled")]
    [InlineData("parent:Version")]
    [InlineData("parent:CreatedAt")]
    [InlineData("role")]
    [InlineData("membership")]
    public async Task LocationStateHttpRequiresCurrentFieldsRoleAndMembershipOnReplay(string revoke)
    {
        var ids = await StateLocationHostAsync(new LocationHttpSchema());
        var input = await LocationStateInputAsync(ids.Location); var operation = Guid.NewGuid();
        using var first = await LocationStateHttpAsync(ids.Location, operation, input); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var platform = Open())
        {
            if (revoke is "write:Enabled" or "role" or "membership")
            {
                var role = await platform.Roles.SingleAsync(item => item.Code == "LOCATION-STATE");
                if (revoke == "membership") platform.Memberships.Remove(await platform.Memberships.SingleAsync(item => item.RoleId == role.Id));
                else role.Change(role.Version, revoke != "role", [new("wms.locations.write", DataScope.Own, [], ImmutableHashSet.Create("Code", "Name"))]);
            }
            else
            {
                var role = await platform.Roles.SingleAsync(item => item.Code == "WMS");
                var permission = revoke.StartsWith("parent:", StringComparison.Ordinal) ? "wms.warehouses.read" : "wms.locations.read";
                var field = revoke.Replace("parent:", "", StringComparison.Ordinal);
                role.Change(role.Version, true, role.Rules.Select(rule => rule.Permission == permission
                    ? rule with { Fields = rule.Fields.Remove(field) } : rule));
            }
            await platform.SaveChangesAsync();
        }
        using var denied = await LocationStateHttpAsync(ids.Location, operation, input);
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
    public async Task LocationStateHttpRejectsStrictMalformedInputWithoutFacts(string variant)
    {
        var ids = await StateLocationHostAsync(new LocationHttpSchema());
        var input = await LocationStateInputAsync(ids.Location);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(input,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        switch (variant)
        {
            case "missing-version": node.Remove("expectedVersion"); break;
            case "missing-enabled": node.Remove("enabled"); break;
            case "missing-reason": node.Remove("reason"); break;
            case "unknown": node["warehouseId"] = ids.Warehouse.ToString("D"); break;
            case "number-enabled": node["enabled"] = 1; break;
            case "invalid-version": node["expectedVersion"] = "bad"; break;
            case "empty-reason": node["reason"] = ""; break;
            case "trim-reason": node["reason"] = " Reason "; break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/wms/locations/{ids.Location:D}/state")
        { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.LocationStateChanges.ToListAsync());
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected, HttpStatusCode.UnprocessableEntity)]
    [InlineData(LocationSchemaDecision.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task LocationStateHttpReportsSchemaRefusalWithoutActivation(LocationSchemaDecision evidence, HttpStatusCode status)
    {
        var ids = await StateLocationHostAsync(new LocationHttpSchema { Decision = evidence });
        using var response = await LocationStateHttpAsync(ids.Location, Guid.NewGuid(), await LocationStateInputAsync(ids.Location));
        Assert.Equal(status, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.LocationStateChanges.ToListAsync());
        Assert.False((await verify.Locations.SingleAsync()).Enabled);
    }

    [Fact]
    public async Task LocationStateHttpDisabledParentIsExplicitlyBlocked()
    {
        var ids = await WmsHostAsync(locationState: true,
            locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(new LocationHttpSchema()));
        await GrantLocationStateAsync();
        using var response = await LocationStateHttpAsync(ids.Location, Guid.NewGuid(), await LocationStateInputAsync(ids.Location));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("LocationActivityBlocked", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("session", HttpStatusCode.Unauthorized)]
    [InlineData("http", HttpStatusCode.BadRequest)]
    [InlineData("key", HttpStatusCode.BadRequest)]
    [InlineData("client", HttpStatusCode.BadRequest)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    public async Task LocationStateHttpRequiresTrustedTransportAndIdentity(string variant, HttpStatusCode status)
    {
        var ids = await StateLocationHostAsync(new LocationHttpSchema());
        var input = await LocationStateInputAsync(ids.Location);
        if (variant == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (variant == "session") { using var logout = await _client.PostAsync("/api/identity/logout", null); }
        if (variant == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        var id = variant == "empty" ? Guid.Empty : variant == "missing" ? Guid.NewGuid() : ids.Location;
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{(variant == "http" ? "http://localhost" : "")}/api/wms/locations/{id:D}/state")
        { Content = JsonContent.Create(input) };
        if (variant != "key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.LocationStateChanges.ToListAsync());
    }

    [Fact]
    public async Task LocationStateHttpUsesRealLocationCreatorNotSpoofedCreator()
    {
        var ids = await StateLocationHostAsync(new LocationHttpSchema());
        var location = new Wes.Wms.Domain.WarehouseLocation(_tenant, ids.Warehouse, Guid.NewGuid(), "v1", "OTHER", "Other", _clock.GetUtcNow());
        await using (var database = OpenWmsMasterData())
        {
            database.Locations.Add(location);
            database.MasterDataChanges.Add(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), location));
            await database.SaveChangesAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Creator-Id", _user.ToString("D"));
        using var denied = await LocationStateHttpAsync(location.Id, Guid.NewGuid(), new(location.Version, true, "Ready"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private async Task<(Guid Warehouse, Guid Location)> StateLocationHostAsync(LocationHttpSchema schema)
    {
        var ids = await WmsHostAsync(locationState: true, warehouseState: true,
            locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(schema));
        await GrantLocationStateAsync(); await GrantWarehouseStateAsync();
        using var enabled = await StateHttpAsync(ids.Warehouse, Guid.NewGuid(), await StateInputAsync(ids.Warehouse));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        return ids;
    }

    private async Task GrantLocationStateAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "LOCATION-STATE", [new("wms.locations.write", DataScope.Own, [], ImmutableHashSet.Create("Enabled"))]);
        platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id)); await platform.SaveChangesAsync();
    }

    private async Task<WarehouseStateInput> LocationStateInputAsync(Guid id)
    {
        await using var database = OpenWmsMasterData();
        return new((await database.Locations.SingleAsync(item => item.Id == id)).Version, true, "Ready");
    }

    private async Task<HttpResponseMessage> LocationStateHttpAsync(Guid id, Guid operation, WarehouseStateInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/wms/locations/{id:D}/state") { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D")); return await _client.SendAsync(request);
    }
}
