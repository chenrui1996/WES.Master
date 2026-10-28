using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
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
    [Fact]
    [Trait("CaseId", "TC-T11-COUNT-07")]
    public async Task CountsHttpDefaultOffHasNoRoutes()
    {
        await WmsHostAsync();
        foreach (var suffix in new[] { "", "/review" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/wms/counts/{Guid.NewGuid():D}{suffix}")).StatusCode);
        foreach (var action in new[] { "start", "records", "adjustment", "invalidation" })
            Assert.Equal(HttpStatusCode.NotFound, (await OutboundHttpPostAsync($"/api/wms/counts/{Guid.NewGuid():D}/{action}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await OutboundHttpPostAsync("/api/wms/counts", new { })).StatusCode);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("session", HttpStatusCode.Unauthorized)]
    [InlineData("http", HttpStatusCode.BadRequest)]
    [InlineData("client", HttpStatusCode.BadRequest)]
    [InlineData("missing-key", HttpStatusCode.BadRequest)]
    [InlineData("empty-key", HttpStatusCode.BadRequest)]
    [InlineData("malformed-key", HttpStatusCode.BadRequest)]
    [InlineData("duplicate-key", HttpStatusCode.BadRequest)]
    [InlineData("permission", HttpStatusCode.Forbidden)]
    public async Task CountsHttpRequiresTrustedIdentityClientPermanentKeyAndAuthority(string invalid, HttpStatusCode expected)
    {
        var fixture = await CountsHttpHostAsync();
        if (invalid == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (invalid == "session") await _client.PostAsync("/api/identity/logout", null);
        if (invalid == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        if (invalid == "permission") await RemoveOutboundHttpPermissionAsync("wms.counts.create");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{(invalid == "http" ? "http://localhost" : "")}/api/wms/counts")
        { Content = JsonContent.Create(new { code = "COUNT-HTTP", warehouseId = fixture.Warehouse, locationId = fixture.Location }) };
        if (invalid != "missing-key")
            request.Headers.Add("Idempotency-Key", invalid switch
            {
                "empty-key" => Guid.Empty.ToString("D"),
                "malformed-key" => Guid.NewGuid().ToString("N"),
                _ => Guid.NewGuid().ToString("D")
            });
        if (invalid == "duplicate-key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.Counts.ToArrayAsync());
    }

    [Fact]
    public async Task CountsHttpUsesExactBlindReviewAndOriginalMinimalReceiptShapes()
    {
        var fixture = await CountsHttpHostAsync();
        var input = new { code = "COUNT-HTTP", warehouseId = fixture.Warehouse, locationId = fixture.Location };
        var operation = Guid.NewGuid();
        using var created = await OutboundHttpPostAsync("/api/wms/counts", input, operation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, receipt.EnumerateObject().Select(row => row.Name).Order().ToArray());
        var id = receipt.GetProperty("id").GetGuid(); var version = receipt.GetProperty("version").GetString()!;
        Assert.Equal($"/api/wms/counts/{id:D}", created.Headers.Location!.ToString());
        using var started = await OutboundHttpPostAsync($"/api/wms/counts/{id:D}/start", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        version = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
        using var blind = await _client.GetAsync($"/api/wms/counts/{id:D}");
        Assert.Equal(HttpStatusCode.OK, blind.StatusCode); Assert.Equal("no-store", blind.Headers.CacheControl!.ToString());
        var detail = (await blind.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("record");
        Assert.Equal(new[] { "code", "createdAt", "id", "lines", "locationId", "lockOwnerId", "policy", "reminderDue",
            "snapshotAt", "state", "version", "warehouseId" }, detail.EnumerateObject().Select(row => row.Name).Order().ToArray());
        var line = Assert.Single(detail.GetProperty("lines").EnumerateArray());
        Assert.Equal(new[] { "batch", "containerId", "countedAt", "countedBy", "id", "itemId", "precision", "quantity", "unitId" },
            line.EnumerateObject().Select(row => row.Name).Order().ToArray());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("quantity").ValueKind);
        using var review = await _client.GetAsync($"/api/wms/counts/{id:D}/review");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var reviewLine = Assert.Single((await review.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("record").GetProperty("lines").EnumerateArray());
        Assert.Equal("10.000000", reviewLine.GetProperty("bookQuantity").GetString());
        Assert.Equal(JsonValueKind.Null, reviewLine.GetProperty("difference").ValueKind);
        using var recorded = await OutboundHttpPostAsync($"/api/wms/counts/{id:D}/records",
            new
            {
                expectedVersion = version,
                lineId = line.GetProperty("id").GetGuid(),
                quantity = "0",
                locationId = fixture.Location,
                itemId = line.GetProperty("itemId").GetGuid(),
                containerId = line.GetProperty("containerId").GetGuid(),
                reason = "Empty scanned position"
            });
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        version = (await recorded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
        using var self = await OutboundHttpPostAsync($"/api/wms/counts/{id:D}/adjustment", new { expectedVersion = version, reason = "Self" });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var original = await OutboundHttpPostAsync("/api/wms/counts", input, operation);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var replay = await original.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(receipt.GetProperty("version").GetString(), replay.GetProperty("version").GetString());
        Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        await RemoveOutboundHttpPermissionAsync("wms.containers.read");
        using var deniedReplay = await OutboundHttpPostAsync("/api/wms/counts", input, operation);
        using var deniedRead = await _client.GetAsync($"/api/wms/counts/{id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, deniedReplay.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
    }

    [Fact]
    public async Task CountsHttpStrictlyRejectsUnknownDuplicateMissingAndNumericQuantityMembers()
    {
        var fixture = await CountsHttpHostAsync();
        var body = OutboundHttpJson(new { code = "STRICT", warehouseId = fixture.Warehouse, locationId = fixture.Location });
        var invalid = new List<string> { "null", body.ToJsonString().Insert(1, "\"code\":\"SHADOW\",") };
        foreach (var name in new[] { "code", "warehouseId", "locationId" })
        { var missing = body.DeepClone().AsObject(); missing.Remove(name); invalid.Add(missing.ToJsonString()); }
        foreach (var name in new[] { "actorId", "operationId", "lines", "bookQuantity" })
        { var unknown = body.DeepClone().AsObject(); unknown[name] = "forbidden"; invalid.Add(unknown.ToJsonString()); }
        foreach (var json in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await OutboundHttpRawPostAsync("/api/wms/counts", json)).StatusCode);
        using var created = await OutboundHttpPostAsync("/api/wms/counts", body);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = receipt.GetProperty("id").GetGuid();
        foreach (var (action, input) in new Dictionary<string, object>
        {
            ["start"] = new { expectedVersion = receipt.GetProperty("version").GetString() },
            ["adjustment"] = new { expectedVersion = Guid.NewGuid().ToString("D"), reason = "Reviewed" },
            ["invalidation"] = new { expectedVersion = Guid.NewGuid().ToString("D"), reason = "Reviewed" },
            ["records"] = new
            {
                expectedVersion = Guid.NewGuid().ToString("D"),
                lineId = Guid.NewGuid(),
                quantity = "0",
                locationId = fixture.Location,
                itemId = Guid.NewGuid(),
                containerId = Guid.NewGuid(),
                reason = "Scanned"
            }
        })
        {
            var original = OutboundHttpJson(input);
            foreach (var field in original.Select(row => row.Key).ToArray())
            {
                var missing = original.DeepClone().AsObject(); missing.Remove(field);
                Assert.Equal(HttpStatusCode.BadRequest, (await OutboundHttpRawPostAsync($"/api/wms/counts/{id:D}/{action}", missing.ToJsonString())).StatusCode);
            }
            original["bookQuantity"] = "10";
            Assert.Equal(HttpStatusCode.BadRequest, (await OutboundHttpRawPostAsync($"/api/wms/counts/{id:D}/{action}", original.ToJsonString())).StatusCode);
            if (action == "records")
            {
                original.Remove("bookQuantity"); original["quantity"] = 0;
                Assert.Equal(HttpStatusCode.BadRequest, (await OutboundHttpRawPostAsync($"/api/wms/counts/{id:D}/{action}", original.ToJsonString())).StatusCode);
            }
        }
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected, HttpStatusCode.UnprocessableEntity)]
    [InlineData(LocationSchemaDecision.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task CountsHttpNeverFallsBackWhenSchemaUnavailable(LocationSchemaDecision decision, HttpStatusCode expected)
    {
        var fixture = await CountsHttpHostAsync(new LocationHttpSchema { Decision = decision });
        using var response = await OutboundHttpPostAsync("/api/wms/counts",
            new { code = "SCHEMA", warehouseId = fixture.Warehouse, locationId = fixture.Location });
        Assert.Equal(expected, response.StatusCode);
    }

    private async Task<(Guid Warehouse, Guid Location)> CountsHttpHostAsync(LocationHttpSchema? schema = null)
    {
        var fixture = await OutboundHttpHostAsync();
        Guid location;
        await using (var seed = OpenWmsMasterData()) location = (await seed.Balances.SingleAsync()).LocationId;
        await GrantCountsAsync();
        _client.Dispose(); await _factory.DisposeAsync();
        _factory = new Factory(_connection, _corpus, _clock, wmsMasterData: true, wmsCounts: true,
            locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(schema ?? new LocationHttpSchema()));
        _client = Client(); await SignInAsync();
        return (fixture.Warehouse, location);
    }

    private async Task GrantCountsAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "COUNTS",
        [
            new("wms.counts.read", DataScope.Own, [], ImmutableHashSet.Create("Id", "Code", "WarehouseId", "LocationId", "State", "Version", "Lines")),
            new("wms.counts.create", DataScope.Own, [], ImmutableHashSet.Create("Code", "WarehouseId", "LocationId")),
            new("wms.counts.start", DataScope.Own, [], ImmutableHashSet.Create("State")),
            new("wms.counts.record", DataScope.Own, [], ImmutableHashSet.Create("LineId", "Quantity", "LocationId", "ItemId", "ContainerId", "Reason")),
            new("wms.counts.adjust", DataScope.Own, [], ImmutableHashSet.Create("State", "Reason")),
            new("wms.counts.invalidate", DataScope.Own, [], ImmutableHashSet.Create("State", "Reason")),
            new("wms.counts.review", DataScope.Own, [], ImmutableHashSet.Create("BookQuantity", "Difference"))
        ]);
        platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id)); await platform.SaveChangesAsync();
    }
}
