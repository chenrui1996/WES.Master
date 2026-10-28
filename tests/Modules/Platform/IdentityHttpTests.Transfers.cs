using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wes.Host.Wms;
using Wes.Platform.Domain;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task TransfersHttpDefaultOffHasNoRoutes()
    {
        await WmsHostAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/wms/transfers/{Guid.NewGuid():D}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await OutboundHttpPostAsync("/api/wms/transfers", new { })).StatusCode);
        foreach (var action in new[] { "approval", "dispatch", "arrivals", "synchronizations", "unknown", "closure" })
            Assert.Equal(HttpStatusCode.NotFound, (await OutboundHttpPostAsync($"/api/wms/transfers/{Guid.NewGuid():D}/{action}", new { })).StatusCode);
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
    public async Task TransfersHttpRequiresTrustedIdentityClientPermanentKeyAndAuthority(string invalid, HttpStatusCode expected)
    {
        var fixture = await TransfersHttpHostAsync();
        if (invalid == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (invalid == "session") await _client.PostAsync("/api/identity/logout", null);
        if (invalid == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        if (invalid == "permission") await RemoveOutboundHttpPermissionAsync("wms.transfers.create");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{(invalid == "http" ? "http://localhost" : "")}/api/wms/transfers")
        { Content = JsonContent.Create(fixture.Draft()) };
        if (invalid != "missing-key")
            request.Headers.Add("Idempotency-Key", invalid switch
            {
                "empty-key" => Guid.Empty.ToString("D"),
                "malformed-key" => Guid.NewGuid().ToString("N"),
                _ => Guid.NewGuid().ToString("D")
            });
        if (invalid == "duplicate-key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        Assert.Equal(expected, (await _client.SendAsync(request)).StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.TransferDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData("wms.transfers.read")]
    [InlineData("wms.units.read")]
    [InlineData("wms.items.read")]
    [InlineData("wms.containers.read")]
    [InlineData("wms.warehouses.read")]
    [InlineData("wms.locations.read")]
    public async Task TransfersHttpRechecksEachCurrentPermissionOnReadAndOriginalReplay(string permission)
    {
        var fixture = await TransfersHttpHostAsync(); var operation = Guid.NewGuid(); var draft = fixture.Draft();
        using var creation = await OutboundHttpPostAsync("/api/wms/transfers", draft, operation);
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var id = (await creation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await RemoveOutboundHttpPermissionAsync(permission);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"/api/wms/transfers/{id:D}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await OutboundHttpPostAsync("/api/wms/transfers", draft, operation)).StatusCode);
    }

    [Fact]
    public async Task TransfersHttpStrictShapesOriginalReceiptsAndWholeApproval()
    {
        var fixture = await TransfersHttpHostAsync(); var input = fixture.Draft(); var operation = Guid.NewGuid();
        using var creation = await OutboundHttpPostAsync("/api/wms/transfers", input, operation);
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var original = await creation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, original.EnumerateObject().Select(row => row.Name));
        var id = original.GetProperty("id").GetGuid();
        Assert.Equal($"/api/wms/transfers/{id:D}", creation.Headers.Location!.ToString());
        var detail = (await _client.GetFromJsonAsync<JsonElement>($"/api/wms/transfers/{id:D}")).GetProperty("record");
        Assert.Equal(new[] { "id", "code", "warehouseId", "state", "version", "createdAt", "lines" }, detail.EnumerateObject().Select(row => row.Name));
        var lines = detail.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(new[] { "id", "executionId", "sourceBalanceId", "targetBalanceId", "itemId", "unitId", "batch", "precision",
            "containerId", "sourceLocationId", "targetLocationId", "branch", "plannedQuantity", "reservedQuantity",
            "movedQuantity", "closedQuantity", "executionStatus", "postingStatus", "sourceVersion", "targetVersion" },
            lines[0].EnumerateObject().Select(row => row.Name));
        using var approval = await OutboundHttpPostAsync($"/api/wms/transfers/{id:D}/approval",
            new TransferApprovalInput(original.GetProperty("version").GetString()!, lines.SelectMany(row => new[] {
                new TransferBalanceVersionInput(row.GetProperty("sourceBalanceId").GetGuid(), row.GetProperty("sourceVersion").GetString()!),
                new TransferBalanceVersionInput(row.GetProperty("targetBalanceId").GetGuid(), row.GetProperty("targetVersion").GetString()!) }).ToArray()));
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        using var replay = await OutboundHttpPostAsync("/api/wms/transfers", input, operation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var receipt = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, receipt.GetProperty("decision").GetInt32());
        Assert.Equal(original.GetProperty("version").GetString(), receipt.GetProperty("version").GetString());
        using var conflict = await OutboundHttpPostAsync("/api/wms/transfers", input with { Code = "DIFFERENT" }, operation);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.Reservations.CountAsync()); Assert.Equal(4m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
    }

    [Theory]
    [InlineData("approval", "approve")]
    [InlineData("dispatch", "dispatch")]
    [InlineData("arrivals", "arrive")]
    [InlineData("synchronizations", "sync")]
    [InlineData("unknown", "unknown")]
    [InlineData("closure", "close")]
    public async Task TransfersHttpEveryActionRequiresItsFixedCurrentPermission(string action, string permission)
    {
        var fixture = await TransfersHttpHostAsync();
        using var created = await OutboundHttpPostAsync("/api/wms/transfers", fixture.Draft());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = receipt.GetProperty("id").GetGuid(); var version = receipt.GetProperty("version").GetString()!;
        var detail = (await _client.GetFromJsonAsync<JsonElement>($"/api/wms/transfers/{id:D}")).GetProperty("record");
        var lines = detail.GetProperty("lines").EnumerateArray().ToArray(); var line = lines[0];
        await RemoveOutboundHttpPermissionAsync($"wms.transfers.{permission}");
        object input = action switch
        {
            "approval" => new TransferApprovalInput(version, lines.SelectMany(row => new[] {
                new TransferBalanceVersionInput(row.GetProperty("sourceBalanceId").GetGuid(), row.GetProperty("sourceVersion").GetString()!),
                new TransferBalanceVersionInput(row.GetProperty("targetBalanceId").GetGuid(), row.GetProperty("targetVersion").GetString()!) }).ToArray()),
            "arrivals" => new TransferArrivalInput(version, line.GetProperty("id").GetGuid(),
                line.GetProperty("sourceLocationId").GetGuid(), line.GetProperty("targetLocationId").GetGuid(),
                line.GetProperty("containerId").GetGuid(), "3", "Exact scans"),
            "unknown" => new TransferUnknownInput(version, line.GetProperty("id").GetGuid(), "Unknown"),
            "closure" => new TransferClosureInput(version, "Safe closure"),
            _ => new TransferLineInput(version, line.GetProperty("id").GetGuid())
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await OutboundHttpPostAsync($"/api/wms/transfers/{id:D}/{action}", input)).StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.TransferFacts.ToArrayAsync()); Assert.Empty(await verify.Reservations.ToArrayAsync());
    }

    [Fact]
    public void TransfersHttpEnabledWithoutExplicitSchemaFailsStartup()
    {
        using var factory = new Factory(_connection, _corpus, _clock, wmsMasterData: true, wmsTransfers: true);
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task TransfersHttpRejectsEveryMissingDuplicateUnknownAndNumericQuantityField()
    {
        var fixture = await TransfersHttpHostAsync();
        var draft = OutboundHttpJson(fixture.Draft()); var cases = new List<(string Path, string Body)>();
        void Malformed(string path, System.Text.Json.Nodes.JsonObject body)
        {
            cases.Add((path, "null"));
            foreach (var field in body.Select(row => row.Key).ToArray())
            {
                var missing = body.DeepClone().AsObject(); missing.Remove(field); cases.Add((path, missing.ToJsonString()));
                var value = body[field]!.ToJsonString();
                cases.Add((path, body.ToJsonString().Insert(1, $"\"{field}\":{value},")));
            }
            foreach (var field in new[] { "actorId", "tenantId", "correlationId", "unexpected" })
            { var unknown = body.DeepClone().AsObject(); unknown[field] = Guid.NewGuid(); cases.Add((path, unknown.ToJsonString())); }
        }
        Malformed("/api/wms/transfers", draft);
        cases.Add(("/api/wms/transfers", draft.ToJsonString().Insert(1, "\"Code\":\"CASE-SHADOW\",")));
        cases.Add(("/api/wms/transfers", draft.ToJsonString().Replace("\"sourceBalanceId\":",
            $"\"SourceBalanceId\":\"{Guid.NewGuid():D}\",\"sourceBalanceId\":", StringComparison.Ordinal)));
        foreach (var field in draft["lines"]![0]!.AsObject().Select(row => row.Key).ToArray())
        {
            var missing = draft.DeepClone().AsObject(); missing["lines"]![0]!.AsObject().Remove(field);
            cases.Add(("/api/wms/transfers", missing.ToJsonString()));
        }
        var numeric = draft.DeepClone().AsObject(); numeric["lines"]![0]!["quantity"] = 3;
        cases.Add(("/api/wms/transfers", numeric.ToJsonString()));
        var nestedUnknown = draft.DeepClone().AsObject(); nestedUnknown["lines"]![0]!["actorId"] = Guid.NewGuid();
        cases.Add(("/api/wms/transfers", nestedUnknown.ToJsonString()));
        var version = Guid.NewGuid().ToString("D"); var id = Guid.NewGuid(); var line = Guid.NewGuid();
        Malformed($"/api/wms/transfers/{id:D}/approval", OutboundHttpJson(new TransferApprovalInput(version,
            [new(fixture.SourceA, version), new(fixture.TargetA, version)])));
        foreach (var action in new[] { "dispatch", "synchronizations" })
            Malformed($"/api/wms/transfers/{id:D}/{action}", OutboundHttpJson(new TransferLineInput(version, line)));
        Malformed($"/api/wms/transfers/{id:D}/arrivals", OutboundHttpJson(
            new TransferArrivalInput(version, line, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "3", "Scans")));
        Malformed($"/api/wms/transfers/{id:D}/unknown", OutboundHttpJson(new TransferUnknownInput(version, line, "Unknown")));
        Malformed($"/api/wms/transfers/{id:D}/closure", OutboundHttpJson(new TransferClosureInput(version, "Close")));
        foreach (var invalid in cases)
            Assert.Equal(HttpStatusCode.BadRequest, (await OutboundHttpRawPostAsync(invalid.Path, invalid.Body)).StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.TransferDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(null, false, false, false)]
    [InlineData("false", false, false, false)]
    [InlineData("true", true, true, true)]
    [InlineData("true", false, true, false)]
    [InlineData("true", true, false, false)]
    [InlineData("invalid", true, true, false)]
    public void TransfersSettingsAreStrictAndDefaultOff(string? toggle, bool master, bool identity, bool acceptedEnabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wes:WmsMasterData:Enabled"] = master.ToString(),
            ["Wes:Identity:Enabled"] = identity.ToString(),
            ["Wes:WmsMasterData:TransfersEnabled"] = toggle
        }).Build();
        if (toggle is "true" && (!master || !identity) || toggle == "invalid")
            Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
        else Assert.Equal(acceptedEnabled, WmsMasterDataSettings.Load(configuration).TransfersEnabled);
    }

    private async Task<TransfersHttpFixture> TransfersHttpHostAsync()
    {
        var outbound = await OutboundHttpHostAsync();
        async Task<Guid> Location(string code)
        {
            MasterDataResult result;
            await using (var seed = OpenWmsMasterData())
                result = await new CreateMasterData(seed, _clock, new LocationHttpSchema()).ExecuteAsync(
                    new(Guid.NewGuid(), _user, Guid.NewGuid(), code, "Transfer location", outbound.Warehouse, Guid.NewGuid(), "v1"));
            Assert.Equal(MasterDataDecision.Created, result.Decision);
            await using var state = OpenWmsMasterData();
            await using var transaction = await state.Database.BeginTransactionAsync();
            var location = await state.Locations.SingleAsync(row => row.Id == result.Id); var before = location.Version;
            location.ChangeState(true);
            state.LocationStateChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), location, before, false, "Fixture ready", _clock.GetUtcNow()));
            await state.SaveChangesAsync(); await transaction.CommitAsync();
            return location.Id;
        }
        var targetLocationA = await Location("TRANSFER-TARGET-A");
        var sourceLocationB = await Location("TRANSFER-SOURCE-B");
        var targetLocationB = await Location("TRANSFER-TARGET-B");
        Guid sourceA; Guid targetA; Guid sourceB; Guid targetB;
        await using (var seed = OpenWmsMasterData())
        {
            var original = await seed.Balances.AsNoTracking().SingleAsync(); sourceA = original.Id;
            var reduced = await new PostStock(seed, _clock).ExecuteAsync(new(Guid.NewGuid(), _user, Guid.NewGuid(), StockPostingKind.Shipment,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), original.Id, null, 7m, original.Version, null));
            Assert.Equal(StockPostingDecision.Posted, reduced.Decision); seed.ChangeTracker.Clear();
            var container = new StockContainer(_tenant, Guid.NewGuid(), _user, Guid.NewGuid(), "TRANSFER-B", "Transfer B", original.WarehouseId, _clock.GetUtcNow());
            var aTarget = new StockBalance(_tenant, original.WarehouseId, targetLocationA, original.ItemId, "", original.ContainerId, original.UnitId, 0);
            var bSource = new StockBalance(_tenant, original.WarehouseId, sourceLocationB, original.ItemId, "", container.Id, original.UnitId, 0);
            var bTarget = new StockBalance(_tenant, original.WarehouseId, targetLocationB, original.ItemId, "", container.Id, original.UnitId, 0);
            await using (var transaction = await seed.Database.BeginTransactionAsync())
            {
                seed.AddRange(container, aTarget, bSource, bTarget);
                foreach (var balance in new[] { aTarget, bSource, bTarget })
                    seed.DimensionRegistrations.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), balance, _clock.GetUtcNow()));
                await seed.SaveChangesAsync(); await transaction.CommitAsync();
            }
            targetA = aTarget.Id; sourceB = bSource.Id; targetB = bTarget.Id;
            seed.ChangeTracker.Clear();
            var received = await new PostStock(seed, _clock).ExecuteAsync(new(Guid.NewGuid(), _user, Guid.NewGuid(), StockPostingKind.Receipt,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, sourceB, 1m, null, bSource.Version));
            Assert.Equal(StockPostingDecision.Posted, received.Decision);
        }
        await using (var platform = Open())
        {
            var role = new AccessRole(_tenant, "TRANSFERS",
            [
                new("wms.transfers.read", DataScope.Own, [], ImmutableHashSet.Create("Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines")),
                new("wms.transfers.create", DataScope.Own, [], ImmutableHashSet.Create("Code", "WarehouseId", "Lines")),
                new("wms.transfers.approve", DataScope.Own, [], ImmutableHashSet.Create("State")),
                new("wms.transfers.dispatch", DataScope.Own, [], ImmutableHashSet.Create("State")),
                new("wms.transfers.arrive", DataScope.Own, [], ImmutableHashSet.Create("LineId", "SourceLocationId", "TargetLocationId", "ContainerId", "Quantity", "Reason")),
                new("wms.transfers.sync", DataScope.Own, [], ImmutableHashSet.Create("State")),
                new("wms.transfers.unknown", DataScope.Own, [], ImmutableHashSet.Create("State", "Reason")),
                new("wms.transfers.close", DataScope.Own, [], ImmutableHashSet.Create("State", "Reason"))
            ]);
            platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id)); await platform.SaveChangesAsync();
        }
        _client.Dispose(); await _factory.DisposeAsync();
        _factory = new Factory(_connection, _corpus, _clock, wmsMasterData: true, wmsTransfers: true,
            locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(new LocationHttpSchema()));
        _client = Client(); await SignInAsync();
        return new(outbound.Warehouse, sourceA, targetA, sourceB, targetB);
    }

    private sealed record TransfersHttpFixture(Guid Warehouse, Guid SourceA, Guid TargetA, Guid SourceB, Guid TargetB)
    {
        internal TransferDraftInput Draft() => new("TRANSFER-HTTP", Warehouse,
            [new(Guid.NewGuid(), SourceA, TargetA, "3", 0), new(Guid.NewGuid(), SourceB, TargetB, "1", 0)]);
    }
}
