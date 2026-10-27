using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Wms.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task WmsWarehouseCreationRequiresExplicitSeparateDeploymentAssembly()
    {
        await WmsHostAsync();
        await GrantWarehouseCreationAsync();
        using var denied = await CreateWarehouseHttpAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, denied.StatusCode);
        await AssertNoWarehouseCreationAsync();
    }

    [Fact]
    public async Task WmsWarehouseCreationUsesCurrentOwnAuthorityAndCommitsOneDisabledIdentityFactAndEvent()
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        var operation = Guid.NewGuid();
        using var created = await CreateWarehouseHttpAsync(operation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var first = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, first.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, first.GetProperty("decision").GetInt32());
        var id = first.GetProperty("id").GetGuid();
        var version = first.GetProperty("version").GetString();
        Assert.Equal($"/api/wms/warehouses/{id:D}", created.Headers.Location?.OriginalString);
        using var replayed = await CreateWarehouseHttpAsync(operation);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        Assert.Equal(id, replay.GetProperty("id").GetGuid());
        Assert.Equal(version, replay.GetProperty("version").GetString());
        using var read = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var detail = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NEW-WH", detail.GetProperty("code").GetString());
        Assert.False(detail.GetProperty("enabled").GetBoolean());
        await using var verify = OpenWmsMasterData();
        var warehouse = await verify.Warehouses.SingleAsync(row => row.Id == id);
        Assert.False(warehouse.Enabled);
        var fact = await verify.MasterDataChanges.SingleAsync(row => row.OperationId == operation);
        Assert.Equal(_user, fact.ActorId);
        Assert.Equal(id, fact.WarehouseId);
        Assert.Equal(version, fact.AfterVersion);
        var message = await verify.OutboxMessages.SingleAsync(row => row.EventId == operation);
        Assert.Equal(id, message.AggregateId);
        Assert.Equal(fact.CorrelationId, message.CorrelationId);
        Assert.Equal(WmsOutboxStatus.Pending, message.Status);
        Assert.Equal(2, await verify.Warehouses.CountAsync());
        Assert.Equal(3, await verify.MasterDataChanges.CountAsync());
        Assert.Equal(3, await verify.OutboxMessages.CountAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    [Theory]
    [InlineData("Code")]
    [InlineData("Name")]
    public async Task WmsWarehouseCreationRequiresEveryCurrentWriteField(string missing)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync(new[] { "Code", "Name" }.Where(field => field != missing).ToArray());
        using var denied = await CreateWarehouseHttpAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await AssertNoWarehouseCreationAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsWarehouseCreationReplayCannotDiscloseIdentityAfterCurrentGrantRevocation(bool disableRole)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        var operation = Guid.NewGuid();
        using var created = await CreateWarehouseHttpAsync(operation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(row => row.Code == "WMS-CREATE");
            role.Change(role.Version, !disableRole, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Name") }));
            await platform.SaveChangesAsync();
        }
        using var denied = await CreateWarehouseHttpAsync(operation);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("\"id\"", await denied.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(3, await verify.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task WmsWarehouseCreationRejectsChangedOperationBodyAndDifferentOperationWithSameCode()
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        var operation = Guid.NewGuid();
        using var created = await CreateWarehouseHttpAsync(operation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var changed = await CreateWarehouseHttpAsync(operation, "{\"code\":\"new-wh\",\"name\":\"Changed\"}");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var duplicate = await CreateWarehouseHttpAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.DoesNotContain("\"id\"", await changed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"version\"", await duplicate.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.Warehouses.CountAsync());
        Assert.Equal(3, await verify.OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WmsWarehouseCreationConcurrentHttpRequestsHaveOneEffect(bool sameOperation)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        var operation = Guid.NewGuid();
        var tasks = new[] { CreateWarehouseHttpAsync(operation), CreateWarehouseHttpAsync(sameOperation ? operation : Guid.NewGuid()) };
        var responses = await Task.WhenAll(tasks);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == (sameOperation ? HttpStatusCode.OK : HttpStatusCode.Conflict));
            await using var verify = OpenWmsMasterData();
            Assert.Single(await verify.Warehouses.Where(row => row.Code == "NEW-WH").ToListAsync());
            Assert.Equal(3, await verify.MasterDataChanges.CountAsync());
            Assert.Equal(3, await verify.OutboxMessages.CountAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"code\":null,\"name\":\"Name\"}")]
    [InlineData("{\"code\":\"A\",\"name\":null}")]
    [InlineData("{\"code\":\" A\",\"name\":\"Name\"}")]
    [InlineData("{\"code\":\"A\",\"name\":\" Name\"}")]
    [InlineData("{\"code\":\"A\",\"name\":\"Name\",\"enabled\":true}")]
    [InlineData("{\"code\":\"A\",\"name\":\"Name\",\"tenantId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"code\":\"A\",\"name\":\"Name\",\"actorId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"code\":\"A\",\"name\":\"Name\",\"warehouseId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"code\":42,\"name\":\"Name\"}")]
    [InlineData("null")]
    public async Task WmsWarehouseCreationRejectsInvalidOrExtraAuthorityAndActivationInput(string json)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        using var denied = await CreateWarehouseHttpAsync(Guid.NewGuid(), json);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        await AssertNoWarehouseCreationAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000000000000000000000000001")]
    public async Task WmsWarehouseCreationRequiresCanonicalNonemptyOperationKey(string? key)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/warehouses")
        { Content = JsonContent.Create(new { code = "new-wh", name = "New warehouse" }) };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWarehouseCreationAsync();
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("http")]
    [InlineData("no-client")]
    [InlineData("invalid-client")]
    [InlineData("logout")]
    public async Task WmsWarehouseCreationRequiresLiveAuthenticatedHttpsClient(string variant)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        if (variant == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (variant == "no-client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        if (variant == "invalid-client")
        {
            _client.DefaultRequestHeaders.Remove("X-WES-Client");
            _client.DefaultRequestHeaders.Add("X-WES-Client", "untrusted");
        }
        if (variant == "logout")
        {
            using var logout = await _client.PostAsync("/api/identity/logout", null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post,
            variant == "http" ? "http://localhost/api/wms/warehouses" : "/api/wms/warehouses")
        { Content = JsonContent.Create(new { code = "new-wh", name = "New warehouse" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(variant is "anonymous" or "logout" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWarehouseCreationAsync();
    }

    [Fact]
    public async Task WmsWarehouseCreationDoesNotExposeLocationCreationOrInferWriteFromReadGrants()
    {
        await WmsHostAsync(warehouseCreation: true);
        using var warehouse = await CreateWarehouseHttpAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, warehouse.StatusCode);
        using var location = await _client.PostAsJsonAsync("/api/wms/locations",
            new { code = "A2", name = "Location", warehouseId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, location.StatusCode);
        await AssertNoWarehouseCreationAsync();
    }

    [Theory]
    [InlineData(64, 128, true)]
    [InlineData(65, 128, false)]
    [InlineData(64, 129, false)]
    public async Task WmsWarehouseCreationEnforcesExactCodeAndNameLengthBounds(int codeLength, int nameLength, bool valid)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        using var response = await CreateWarehouseHttpAsync(Guid.NewGuid(),
            JsonSerializer.Serialize(new { code = new string('a', codeLength), name = new string('N', nameLength) }));
        Assert.Equal(valid ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
        if (!valid) await AssertNoWarehouseCreationAsync();
        else
        {
            await using var verify = OpenWmsMasterData();
            var warehouse = await verify.Warehouses.SingleAsync(row => row.Code != "WH-HTTP");
            Assert.Equal(new string('A', codeLength), warehouse.Code);
            Assert.Equal(nameLength, warehouse.Name.Length);
        }
    }

    [Theory]
    [InlineData("Idempotency-Key")]
    [InlineData("X-WES-Client")]
    public async Task WmsWarehouseCreationRejectsMultipleTransportIdentityHeaders(string header)
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/warehouses")
        { Content = JsonContent.Create(new { code = "new-wh", name = "New warehouse" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add(header, header == "X-WES-Client" ? new[] { "ops-web", "pda" } : new[] { Guid.NewGuid().ToString("D") });
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWarehouseCreationAsync();
    }

    [Fact]
    public async Task WmsWarehouseCreationIgnoresSpoofedUrlAndHeadersAndDoesNotGrantReadAccess()
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        await using (var platform = Open())
        {
            var readRole = await platform.Roles.SingleAsync(row => row.Code == "WMS");
            readRole.Change(readRole.Version, false, readRole.Rules);
            await platform.SaveChangesAsync();
        }
        var operation = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/wms/warehouses?tenantId={_wmsForeignTenant:D}&actorId={Guid.NewGuid():D}&enabled=true")
        { Content = JsonContent.Create(new { code = "new-wh", name = "New warehouse" }) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        request.Headers.Add("X-Tenant-Id", _wmsForeignTenant.ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var deniedRead = await _client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        await using (var own = OpenWmsMasterData())
        {
            var fact = await own.MasterDataChanges.SingleAsync(row => row.OperationId == operation);
            Assert.Equal(_user, fact.ActorId);
            Assert.Equal(_tenant, fact.TenantId);
            Assert.False((await own.Warehouses.SingleAsync(row => row.Id == fact.WarehouseId)).Enabled);
        }
        await using var foreign = OpenWmsMasterData(_wmsForeignTenant);
        Assert.Empty(await foreign.MasterDataChanges.ToListAsync());
        Assert.Empty(await foreign.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task WmsWarehouseCreationSuccessfulOperationCannotBeReplayedAfterSessionRevocation()
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        var operation = Guid.NewGuid();
        using var created = await CreateWarehouseHttpAsync(operation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var logout = await _client.PostAsync("/api/identity/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var replay = await CreateWarehouseHttpAsync(operation);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.DoesNotContain("\"id\"", await replay.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(3, await verify.OutboxMessages.CountAsync());
    }

    private Task<HttpResponseMessage> CreateWarehouseHttpAsync(Guid operation,
        string json = "{\"code\":\"new-wh\",\"name\":\"New warehouse\"}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/warehouses")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        return SendAsync();
        async Task<HttpResponseMessage> SendAsync()
        {
            using (request) return await _client.SendAsync(request);
        }
    }

    private async Task GrantWarehouseCreationAsync(string[]? fields = null)
    {
        await using var platform = Open();
        platform.Roles.Add(new AccessRole(_tenant, "WMS-CREATE",
        [
            new("wms.warehouses.write", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                (fields ?? ["Code", "Name"]).ToImmutableHashSet(StringComparer.Ordinal))
        ]));
        var role = platform.Roles.Local.Single(row => row.Code == "WMS-CREATE");
        platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
    }

    private async Task AssertNoWarehouseCreationAsync()
    {
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.Warehouses.ToListAsync());
        Assert.Single(await verify.Locations.ToListAsync());
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
    }
}
