using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Host.Wms;
using Wes.Platform.Domain;
using Wes.Wms.Features.Inbound;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task WmsInboundDraftCreationIsIndependentlyDefaultDisabled()
    {
        var ids = await WmsHostAsync(warehouseCreation: true, locationCreation: false);
        await GrantInboundDraftAsync();
        using var response = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(ids.Warehouse));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await AssertNoHttpDraftAsync();
    }

    [Fact]
    public async Task WmsInboundDraftCreatesAndRecoversOnlyStableIdAfterInternalApproval()
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        var operation = Guid.NewGuid();
        var input = InboundDraftInput(ids.Warehouse);
        using var created = await CreateInboundDraftHttpAsync(operation, input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id" }, receipt.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, receipt.GetProperty("decision").GetInt32());
        var id = receipt.GetProperty("id").GetGuid();
        Assert.Equal($"/api/wms/inbound/{id:D}", created.Headers.Location?.OriginalString);
        using var detail = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("state").GetInt32());
        Assert.Equal("5.000000", Assert.Single(body.GetProperty("lines").EnumerateArray()).GetProperty("planned").GetString());
        await using (var advance = OpenWmsMasterData())
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(advance, _clock).ApproveAsync(
                id, Guid.NewGuid(), _user, Guid.NewGuid(), body.GetProperty("version").GetString()!)).Decision);
        using var replayed = await CreateInboundDraftHttpAsync(operation, input);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id" }, replay.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        Assert.Equal(id, replay.GetProperty("id").GetGuid());
        using var changed = await CreateInboundDraftHttpAsync(operation, input with { Code = "DIFFERENT" });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var duplicate = await CreateInboundDraftHttpAsync(Guid.NewGuid(), input);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using var verify = OpenWmsMasterData();
        var saved = Assert.Single(await verify.InboundDocuments.ToListAsync());
        Assert.Equal(_user, saved.CreatorId);
        Assert.Equal(operation, saved.CreationOperationId);
        Assert.Single(await verify.InboundFacts.ToListAsync());
        Assert.Empty(await verify.Balances.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
    }

    [Theory]
    [InlineData("Code")]
    [InlineData("WarehouseId")]
    [InlineData("Lines")]
    [InlineData("role")]
    [InlineData("membership")]
    public async Task WmsInboundDraftReplaysRequireCurrentExactWriteAuthority(string revoke)
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        var input = InboundDraftInput(ids.Warehouse);
        var operation = Guid.NewGuid();
        using var created = await CreateInboundDraftHttpAsync(operation, input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(item => item.Code == "INBOUND-DRAFT");
            if (revoke == "membership") platform.Memberships.Remove(await platform.Memberships.SingleAsync(item => item.RoleId == role.Id));
            else role.Change(role.Version, revoke != "role",
                [new("wms.inbound.write", DataScope.Own, [],
                    new[] { "Code", "WarehouseId", "Lines" }.Where(field => field != revoke).ToImmutableHashSet())]);
            await platform.SaveChangesAsync();
        }
        using var denied = await CreateInboundDraftHttpAsync(operation, input);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("id", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WmsInboundDraftRequiresRealWarehouseCreatorRatherThanRequestActor()
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        Guid parent;
        await using (var seed = OpenWmsMasterData())
        {
            var warehouse = new Wes.Wms.Domain.Warehouse(_tenant, "OTHER-CREATOR", "Other", _clock.GetUtcNow());
            seed.Warehouses.Add(warehouse);
            seed.MasterDataChanges.Add(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), warehouse));
            await seed.SaveChangesAsync();
            parent = warehouse.Id;
        }
        using var denied = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(parent));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var allowed = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(ids.Warehouse));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("Enabled")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    public async Task WmsInboundDraftRequiresCompleteCurrentParentRead(string missing)
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(item => item.Code == "WMS");
            role.Change(role.Version, true, [new("wms.warehouses.read", DataScope.Own, [],
                new[] { "Id", "Code", "Name", "Enabled", "Version", "CreatedAt" }.Where(field => field != missing).ToImmutableHashSet())]);
            await platform.SaveChangesAsync();
        }
        using var denied = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(ids.Warehouse));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await AssertNoHttpDraftAsync();
    }

    [Theory]
    [InlineData("quantity-number")]
    [InlineData("unknown-root")]
    [InlineData("unknown-line")]
    [InlineData("null-plan")]
    [InlineData("null-line")]
    [InlineData("empty-plan")]
    [InlineData("quantity-exponent")]
    [InlineData("quantity-overprecision")]
    [InlineData("missing-precision")]
    public async Task WmsInboundDraftRejectsMalformedStrictBodiesWithoutEffects(string invalid)
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        var input = InboundDraftInput(ids.Warehouse);
        var body = JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        body = invalid switch
        {
            "quantity-number" => body.Replace("\"planned\":\"5.000\"", "\"planned\":5", StringComparison.Ordinal),
            "unknown-root" => body.Insert(1, "\"actorId\":\"invented\","),
            "unknown-line" => body.Replace("\"planned\":", "\"received\":\"5\",\"planned\":", StringComparison.Ordinal),
            "null-plan" => JsonSerializer.Serialize(new { input.Code, input.WarehouseId, lines = (object?)null }),
            "null-line" => JsonSerializer.Serialize(new { input.Code, input.WarehouseId, lines = new object?[] { null } }),
            "empty-plan" => JsonSerializer.Serialize(new { input.Code, input.WarehouseId, lines = Array.Empty<object>() }),
            "quantity-exponent" => body.Replace("5.000", "5e0", StringComparison.Ordinal),
            "missing-precision" => body.Replace("\"precision\":3,", "", StringComparison.Ordinal),
            _ => body.Replace("5.000", "5.0000001", StringComparison.Ordinal)
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/inbound")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-WES-Client", "ops-web");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoHttpDraftAsync();
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("empty-key")]
    [InlineData("bad-key")]
    [InlineData("multiple-key")]
    [InlineData("missing-client")]
    [InlineData("wrong-client")]
    [InlineData("http")]
    [InlineData("anonymous")]
    public async Task WmsInboundDraftRequiresExactMutationProtocolAndTrustedSession(string invalid)
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            invalid == "http" ? "http://localhost/api/wms/inbound" : "/api/wms/inbound")
        { Content = JsonContent.Create(InboundDraftInput(ids.Warehouse)) };
        if (invalid != "missing-key")
            request.Headers.Add("Idempotency-Key", invalid == "empty-key" ? Guid.Empty.ToString("D")
                : invalid == "bad-key" ? "invalid" : Guid.NewGuid().ToString("D"));
        if (invalid == "multiple-key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        if (invalid == "missing-client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        if (invalid != "missing-client") request.Headers.Add("X-WES-Client", invalid == "wrong-client" ? "invented" : "ops-web");
        if (invalid == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        using var response = await _client.SendAsync(request);
        Assert.Equal(invalid == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoHttpDraftAsync();
    }

    [Fact]
    public async Task WmsInboundDraftCannotBorrowReadAuthorityOrUseMissingWarehouse()
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        using var denied = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(ids.Warehouse));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await GrantInboundDraftAsync();
        using var missing = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertNoHttpDraftAsync();
    }

    [Fact]
    public async Task WmsInboundDraftCannotCreateInForeignTenantWarehouseUsingSpoofedHeaders()
    {
        await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        Guid parent;
        await using (var seed = OpenWmsMasterData(_wmsForeignTenant))
        {
            var warehouse = new Wes.Wms.Domain.Warehouse(_wmsForeignTenant, "FOREIGN-DRAFT", "Foreign", _clock.GetUtcNow());
            seed.Warehouses.Add(warehouse);
            seed.MasterDataChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), warehouse));
            await seed.SaveChangesAsync();
            parent = warehouse.Id;
        }
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _wmsForeignTenant.ToString("D"));
        using var response = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(parent));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoHttpDraftAsync();
        await using var verify = OpenWmsMasterData(_wmsForeignTenant);
        Assert.Empty(await verify.InboundDocuments.ToListAsync());
    }

    private static CreateInboundDraftInput InboundDraftInput(Guid warehouse) => new("IN-DRAFT", warehouse,
        [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 3, "5.000")]);

    private async Task GrantInboundDraftAsync(bool includeRead = true)
    {
        await using var platform = Open();
        var rules = new List<PermissionGrant>
        {
            new("wms.inbound.write", DataScope.Own, [], ImmutableHashSet.Create("Code", "WarehouseId", "Lines"))
        };
        if (includeRead) rules.Add(new("wms.inbound.read", DataScope.Own, [], InboundReadFields.ToImmutableHashSet()));
        var role = new AccessRole(_tenant, "INBOUND-DRAFT", rules);
        platform.Roles.Add(role);
        platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> CreateInboundDraftHttpAsync(Guid operation, CreateInboundDraftInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/inbound") { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        request.Headers.Add("X-WES-Client", "ops-web");
        return await _client.SendAsync(request);
    }

    private async Task AssertNoHttpDraftAsync()
    {
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.InboundDocuments.ToListAsync());
        Assert.Empty(await verify.InboundFacts.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
        Assert.Empty(await verify.Balances.ToListAsync());
    }
}
