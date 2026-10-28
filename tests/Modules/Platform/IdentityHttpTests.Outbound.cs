using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Host.Wms;
using Wes.Platform.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    private static readonly string[] OutboundHttpActions =
        ["approval", "picking", "shortage-release", "shipment", "cancellation", "close-remainder"];
    private static readonly string[] OutboundHttpReadFields =
        ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines"];

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpRoutesAreAbsentUnlessExplicitlyEnabled()
    {
        await WmsHostAsync();
        var id = Guid.NewGuid();

        using var detail = await _client.GetAsync($"/api/wms/outbound/{id:D}");
        using var draft = await OutboundHttpPostAsync("/api/wms/outbound", new { });

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        foreach (var action in OutboundHttpActions)
        {
            using var response = await OutboundHttpPostAsync($"/api/wms/outbound/{id:D}/{action}", new { });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
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
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpDraftRequiresTrustedIdentityClientPermanentKeyAndAuthority(string invalid, HttpStatusCode expected)
    {
        var fixture = await OutboundHttpHostAsync();
        if (invalid == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (invalid == "session")
        {
            using var logout = await _client.PostAsync("/api/identity/logout", null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        if (invalid == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        if (invalid == "permission") await RemoveOutboundHttpPermissionAsync("wms.outbound.create");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{(invalid == "http" ? "http://localhost" : "")}/api/wms/outbound")
        { Content = JsonContent.Create(fixture.Draft()) };
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
        await AssertNoOutboundHttpEffectsAsync();
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpDraftStrictlyRejectsUnknownDuplicateMissingNumericAndSuppliedIdentityMembers()
    {
        var fixture = await OutboundHttpHostAsync();
        var malformed = new List<string>();
        foreach (var field in new[] { "precision", "actorId", "tenantId", "operationId" })
        {
            var body = OutboundHttpJson(fixture.Draft());
            body[field] = field == "precision" ? JsonValue.Create(0) : JsonValue.Create(Guid.NewGuid().ToString("D"));
            malformed.Add(body.ToJsonString());
        }
        foreach (var field in new[] { "code", "warehouseId", "lines" })
        {
            var body = OutboundHttpJson(fixture.Draft());
            body.Remove(field);
            malformed.Add(body.ToJsonString());
        }
        foreach (var field in new[] { "id", "balanceId", "quantity", "branch" })
        {
            var body = OutboundHttpJson(fixture.Draft());
            body["lines"]![0]!.AsObject().Remove(field);
            malformed.Add(body.ToJsonString());
        }
        var numeric = OutboundHttpJson(fixture.Draft());
        numeric["lines"]![0]!["quantity"] = 7;
        malformed.Add(numeric.ToJsonString());
        foreach (var quantity in new[] { "7e0", "-1", "0", "1.1", "1.0000001", " 7" })
        {
            var body = OutboundHttpJson(fixture.Draft());
            body["lines"]![0]!["quantity"] = quantity;
            malformed.Add(body.ToJsonString());
        }
        var nestedUnknown = OutboundHttpJson(fixture.Draft());
        nestedUnknown["lines"]![0]!["actorId"] = _user.ToString("D");
        malformed.Add(nestedUnknown.ToJsonString());
        var canonical = OutboundHttpJson(fixture.Draft()).ToJsonString();
        malformed.Add(canonical.Insert(1, "\"code\":\"SHADOW\","));
        malformed.Add(canonical.Replace("\"quantity\":\"7\"", "\"quantity\":\"1\",\"quantity\":\"7\"", StringComparison.Ordinal));

        foreach (var json in malformed)
        {
            using var response = await OutboundHttpRawPostAsync("/api/wms/outbound", json);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var nullBody = await OutboundHttpRawPostAsync("/api/wms/outbound", "null");
        using var wrongType = await OutboundHttpRawPostAsync("/api/wms/outbound", "{}", mediaType: "text/plain");

        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        await AssertNoOutboundHttpEffectsAsync();
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpActionsRejectMalformedBodiesVersionsAndNumericQuantitiesBeforeEffects()
    {
        var fixture = await OutboundHttpHostAsync();
        using var creation = await OutboundHttpPostAsync("/api/wms/outbound", fixture.Draft());
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var draft = await creation.Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetGuid();
        var version = draft.GetProperty("version").GetString()!;
        var bodies = new Dictionary<string, object>
        {
            ["approval"] = new { expectedVersion = version, balances = new[] { new { balanceId = fixture.Balance, expectedVersion = fixture.BalanceVersion } } },
            ["picking"] = new { expectedVersion = version, lineId = fixture.Line, pickedQuantity = "5", shortageQuantity = "2", reason = "Short pick" },
            ["shortage-release"] = new { expectedVersion = version, lineId = fixture.Line, quantity = "2", reason = "Reviewed" },
            ["shipment"] = new { expectedVersion = version, lineId = fixture.Line, quantity = "5" },
            ["cancellation"] = new { expectedVersion = version, reason = "Cancel draft" },
            ["close-remainder"] = new { expectedVersion = version, reason = "Close remainder" }
        };

        foreach (var (action, input) in bodies)
        {
            var original = OutboundHttpJson(input);
            var invalid = new List<string>();
            var unknown = OutboundHttpJson(input); unknown["actorId"] = _user.ToString("D"); invalid.Add(unknown.ToJsonString());
            foreach (var field in original.Select(property => property.Key))
            {
                var missing = OutboundHttpJson(input); missing.Remove(field); invalid.Add(missing.ToJsonString());
            }
            var malformedVersion = OutboundHttpJson(input); malformedVersion["expectedVersion"] = "not-a-version";
            invalid.Add(malformedVersion.ToJsonString());
            invalid.Add(original.ToJsonString().Insert(1, $"\"expectedVersion\":\"{version}\","));
            foreach (var quantity in new[] { "quantity", "pickedQuantity", "shortageQuantity" }.Where(original.ContainsKey))
            {
                var numeric = OutboundHttpJson(input); numeric[quantity] = 5; invalid.Add(numeric.ToJsonString());
            }
            foreach (var json in invalid)
            {
                using var response = await OutboundHttpRawPostAsync($"/api/wms/outbound/{id:D}/{action}", json);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }
        var approval = OutboundHttpJson(bodies["approval"]);
        approval["expectedVersion"] = Guid.NewGuid().ToString("D");
        using var stale = await OutboundHttpPostAsync($"/api/wms/outbound/{id:D}/approval", approval);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.OutboundFacts.ToArrayAsync());
        Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Single(await verify.Postings.ToArrayAsync());
        Assert.Equal(10m, (await verify.Balances.SingleAsync()).Quantity);
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpEveryWriteRequiresItsOwnCurrentPermission()
    {
        var fixture = await OutboundHttpHostAsync();
        using var creation = await OutboundHttpPostAsync("/api/wms/outbound", fixture.Draft());
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var draft = await creation.Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetGuid();
        var version = draft.GetProperty("version").GetString()!;
        var actions = new (string Path, string Permission, object Input)[]
        {
            ("approval", "approve", new OutboundApprovalInput(version, [new(fixture.Balance, fixture.BalanceVersion)])),
            ("picking", "pick", new OutboundPickInput(version, fixture.Line, "5", "2", "Short pick")),
            ("shortage-release", "release", new OutboundShortageReleaseInput(version, fixture.Line, "2", "Reviewed")),
            ("shipment", "ship", new OutboundShipmentInput(version, fixture.Line, "5")),
            ("cancellation", "cancel", new OutboundStateInput(version, "Cancel draft")),
            ("close-remainder", "close", new OutboundStateInput(version, "Close remainder"))
        };
        foreach (var (path, permission, input) in actions)
        {
            await RemoveOutboundHttpPermissionAsync($"wms.outbound.{permission}");

            using var response = await OutboundHttpPostAsync($"/api/wms/outbound/{id:D}/{path}", input);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.OutboundFacts.ToArrayAsync());
        Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Single(await verify.Postings.ToArrayAsync());
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpApprovalRequiresExactCompleteUniqueBalanceVersions()
    {
        var fixture = await OutboundHttpHostAsync();
        using var creation = await OutboundHttpPostAsync("/api/wms/outbound", fixture.Draft());
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var draft = await creation.Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetGuid();
        var version = draft.GetProperty("version").GetString()!;
        var path = $"/api/wms/outbound/{id:D}/approval";
        var balance = new OutboundBalanceVersionInput(fixture.Balance, fixture.BalanceVersion);
        var malformed = new List<string>();
        foreach (var field in new[] { "balanceId", "expectedVersion" })
        {
            var input = OutboundHttpJson(new OutboundApprovalInput(version, [balance]));
            input["balances"]![0]!.AsObject().Remove(field);
            malformed.Add(input.ToJsonString());
        }
        var unknown = OutboundHttpJson(new OutboundApprovalInput(version, [balance]));
        unknown["balances"]![0]!["quantity"] = "7";
        malformed.Add(unknown.ToJsonString());
        var duplicate = OutboundHttpJson(new OutboundApprovalInput(version, [balance])).ToJsonString()
            .Replace($"\"balanceId\":\"{fixture.Balance:D}\"",
                $"\"balanceId\":\"{fixture.Balance:D}\",\"balanceId\":\"{fixture.Balance:D}\"", StringComparison.Ordinal);
        malformed.Add(duplicate);
        foreach (var input in malformed)
        {
            using var rejected = await OutboundHttpRawPostAsync(path, input);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        foreach (var balances in new IReadOnlyList<OutboundBalanceVersionInput>[]
            { [], [balance, balance], [balance with { ExpectedVersion = "bad-version" }] })
        {
            using var rejected = await OutboundHttpPostAsync(path, new OutboundApprovalInput(version, balances));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var stale = await OutboundHttpPostAsync(path,
            new OutboundApprovalInput(version, [balance with { ExpectedVersion = Guid.NewGuid().ToString("D") }]));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.OutboundFacts.ToArrayAsync());
        Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Equal(10m, (await verify.Balances.SingleAsync()).Quantity);
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpOriginalReceiptRemainsImmutableWhileReadReturnsCurrentProgressAndPermissions()
    {
        var fixture = await OutboundHttpHostAsync();
        var operation = Guid.NewGuid();
        var input = fixture.Draft();
        using var creation = await OutboundHttpPostAsync("/api/wms/outbound", input, operation);
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        Assert.Equal("no-store", creation.Headers.CacheControl?.ToString());
        var original = await creation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, original.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, original.GetProperty("decision").GetInt32());
        var id = original.GetProperty("id").GetGuid();
        var originalVersion = original.GetProperty("version").GetString()!;
        Assert.Equal($"/api/wms/outbound/{id:D}", creation.Headers.Location?.OriginalString);
        using var approval = await OutboundHttpPostAsync($"/api/wms/outbound/{id:D}/approval",
            new OutboundApprovalInput(originalVersion, [new(fixture.Balance, fixture.BalanceVersion)]));
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        var approvedVersion = (await approval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;

        using var replay = await OutboundHttpPostAsync("/api/wms/outbound", input, operation);
        using var detail = await _client.GetAsync($"/api/wms/outbound/{id:D}");
        using var changed = await OutboundHttpPostAsync("/api/wms/outbound", input with { Code = "DIFFERENT" }, operation);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var receipt = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, receipt.GetProperty("decision").GetInt32());
        Assert.Equal(originalVersion, receipt.GetProperty("version").GetString());
        Assert.Equal(id, receipt.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("no-store", detail.Headers.CacheControl?.ToString());
        var envelope = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "record" }, envelope.EnumerateObject().Select(property => property.Name));
        var current = envelope.GetProperty("record");
        Assert.Equal(OutboundHttpReadFields.Select(field => char.ToLowerInvariant(field[0]) + field[1..]),
            current.EnumerateObject().Select(property => property.Name));
        Assert.Equal(approvedVersion, current.GetProperty("version").GetString());
        Assert.Equal(1, current.GetProperty("state").GetInt32());
        var line = Assert.Single(current.GetProperty("lines").EnumerateArray());
        Assert.Equal(new[] { "id", "executionId", "balanceId", "itemId", "unitId", "batch", "precision",
            "containerId", "locationId", "branch", "plannedQuantity", "reservedQuantity", "pickedQuantity",
            "shortageQuantity", "releasedQuantity", "shippedQuantity" }, line.EnumerateObject().Select(property => property.Name));
        Assert.Equal("7.000000", line.GetProperty("plannedQuantity").GetString());
        Assert.Equal("7.000000", line.GetProperty("reservedQuantity").GetString());
        Assert.Equal("0.000000", line.GetProperty("shippedQuantity").GetString());

        await RemoveOutboundHttpPermissionAsync("wms.outbound.create");
        using var revokedReplay = await OutboundHttpPostAsync("/api/wms/outbound", input, operation);
        using var stillReadable = await _client.GetAsync($"/api/wms/outbound/{id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillReadable.StatusCode);
        await RemoveOutboundHttpPermissionAsync("wms.outbound.read");
        using var deniedRead = await _client.GetAsync($"/api/wms/outbound/{id:D}");
        using var deniedApprovalReplay = await OutboundHttpPostAsync($"/api/wms/outbound/{id:D}/approval",
            new OutboundApprovalInput(originalVersion, [new(fixture.Balance, fixture.BalanceVersion)]));
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedApprovalReplay.StatusCode);
        Assert.DoesNotContain("OUT-HTTP", await deniedRead.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.OutboundFacts.CountAsync());
        Assert.Equal(10m, (await verify.Balances.SingleAsync()).Quantity);
    }

    [Theory]
    [InlineData("wms.units.read")]
    [InlineData("wms.items.read")]
    [InlineData("wms.containers.read")]
    [InlineData("wms.warehouses.read")]
    [InlineData("wms.locations.read")]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpRechecksEachDistinctResourcePermissionOnReadAndReplay(string permission)
    {
        var fixture = await OutboundHttpHostAsync();
        var operation = Guid.NewGuid();
        var input = fixture.Draft();
        using var creation = await OutboundHttpPostAsync("/api/wms/outbound", input, operation);
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var id = (await creation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await RemoveOutboundHttpPermissionAsync(permission);

        using var read = await _client.GetAsync($"/api/wms/outbound/{id:D}");
        using var replay = await OutboundHttpPostAsync("/api/wms/outbound", input, operation);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.OutboundFacts.ToArrayAsync());
        Assert.Empty(await verify.Reservations.ToArrayAsync());
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected, HttpStatusCode.UnprocessableEntity)]
    [InlineData(LocationSchemaDecision.Unavailable, HttpStatusCode.ServiceUnavailable)]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundHttpMissingSchemaEvidenceCannotBecomeSuccessfulDraft(LocationSchemaDecision evidence, HttpStatusCode expected)
    {
        var fixture = await OutboundHttpHostAsync(new LocationHttpSchema { Decision = evidence });

        using var response = await OutboundHttpPostAsync("/api/wms/outbound", fixture.Draft());

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        await AssertNoOutboundHttpEffectsAsync();
    }

    private async Task<OutboundHttpFixture> OutboundHttpHostAsync(LocationHttpSchema? schema = null)
    {
        var source = await ExecutionHttpHostAsync();
        Guid warehouse;
        Guid inboundLine = Guid.NewGuid();
        Guid inboundId;
        string inboundVersion;
        await using (var seed = OpenWmsMasterData())
        {
            var balance = await seed.Balances.AsNoTracking().SingleAsync();
            warehouse = balance.WarehouseId;
            var draft = await new InboundWriter(seed, _clock).CreateAsync(Guid.NewGuid(), _user, warehouse,
                "OUT-HTTP-SEED", [new(inboundLine, balance.ItemId, balance.UnitId, balance.Batch, balance.Precision, 10m)]);
            inboundId = draft.Id!.Value;
            inboundVersion = draft.Version!;
        }
        using var approval = await ExecutionPostAsync(inboundId, "approval", Guid.NewGuid(), new InboundApprovalInput(inboundVersion));
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        var approved = (await approval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
        using var receipt = await ExecutionPostAsync(inboundId, "receipts", Guid.NewGuid(),
            new InboundReceiptInput(approved, source.BalanceVersion, inboundLine, Guid.NewGuid(), "10",
                source.Container, source.Location, source.Location, source.Balance));
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        string balanceVersion;
        await using (var current = OpenWmsMasterData())
        {
            var balance = await current.Balances.AsNoTracking().SingleAsync();
            Assert.Equal(10m, balance.Quantity);
            balanceVersion = balance.Version;
        }
        await GrantOutboundHttpAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = new Factory(_connection, _corpus, _clock, wmsMasterData: true, wmsOutbound: true,
            locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(schema ?? new LocationHttpSchema()));
        _client = Client();
        await SignInAsync();
        return new(warehouse, source.Balance, balanceVersion, Guid.NewGuid());
    }

    private async Task GrantOutboundHttpAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "OUTBOUND-HTTP",
        [
            new("wms.outbound.read", DataScope.Own, [], OutboundHttpReadFields.ToImmutableHashSet()),
            new("wms.outbound.create", DataScope.Own, [], ImmutableHashSet.Create("Code", "WarehouseId", "Lines")),
            new("wms.outbound.approve", DataScope.Own, [], ImmutableHashSet.Create("State")),
            new("wms.outbound.pick", DataScope.Own, [], ImmutableHashSet.Create("LineId", "PickedQuantity", "ShortageQuantity", "Reason")),
            new("wms.outbound.release", DataScope.Own, [], ImmutableHashSet.Create("LineId", "Quantity", "Reason")),
            new("wms.outbound.ship", DataScope.Own, [], ImmutableHashSet.Create("LineId", "Quantity")),
            new("wms.outbound.cancel", DataScope.Own, [], ImmutableHashSet.Create("State", "Reason")),
            new("wms.outbound.close", DataScope.Own, [], ImmutableHashSet.Create("State", "Reason"))
        ]);
        platform.Roles.Add(role);
        platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
    }

    private async Task RemoveOutboundHttpPermissionAsync(string permission)
    {
        await using var platform = Open();
        foreach (var role in await platform.Roles.ToListAsync())
            if (role.Rules.Any(rule => rule.Permission == permission))
                role.Change(role.Version, true, role.Rules.Where(rule => rule.Permission != permission));
        await platform.SaveChangesAsync();
    }

    private async Task AssertNoOutboundHttpEffectsAsync()
    {
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.OutboundDocuments.ToArrayAsync());
        Assert.Empty(await verify.OutboundFacts.ToArrayAsync());
        Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Single(await verify.Postings.ToArrayAsync());
        Assert.Single(await verify.Entries.ToArrayAsync());
        Assert.Equal(10m, (await verify.Balances.SingleAsync()).Quantity);
    }

    private static JsonObject OutboundHttpJson(object value) =>
        JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();

    private async Task<HttpResponseMessage> OutboundHttpPostAsync(string path, object input, Guid? operation = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", (operation ?? Guid.NewGuid()).ToString("D"));
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> OutboundHttpRawPostAsync(string path, string json, string mediaType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(json, Encoding.UTF8, mediaType) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await _client.SendAsync(request);
    }

    private sealed record OutboundHttpFixture(Guid Warehouse, Guid Balance, string BalanceVersion, Guid Line)
    {
        internal OutboundDraftInput Draft() => new("OUT-HTTP", Warehouse, [new(Line, Balance, "7", 0)]);
    }
}
