using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData("code", null)]
    [InlineData("name", null)]
    [InlineData("schemaVersion", null)]
    [InlineData("schemaVersion", " v1")]
    [InlineData("schemaVersion", "")]
    [InlineData("warehouseId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("packId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("warehouseId", "bad")]
    [InlineData("packId", "bad")]
    [InlineData("code", " bad")]
    [InlineData("name", " bad")]
    [InlineData("enabled", "true")]
    [InlineData("tenantId", "00000000-0000-0000-0000-000000000001")]
    [InlineData("actorId", "00000000-0000-0000-0000-000000000001")]
    [InlineData("operationId", "00000000-0000-0000-0000-000000000001")]
    public async Task WmsLocationCreationRejectsInvalidOrUnknownBodyFieldsBeforeSchema(string field, string? value)
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        var body = new Dictionary<string, object?>
        {
            ["code"] = "new-loc",
            ["name"] = "New location",
            ["warehouseId"] = ids.Warehouse,
            ["packId"] = LocationHttpSchema.Pack,
            ["schemaVersion"] = "v1"
        };
        body[field] = value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/locations") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(schema.Calls);
        await AssertNoLocationCreationAsync();
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("empty-key")]
    [InlineData("bad-key")]
    [InlineData("multiple-key")]
    [InlineData("no-client")]
    [InlineData("bad-client")]
    [InlineData("multiple-client")]
    [InlineData("anonymous")]
    [InlineData("http")]
    public async Task WmsLocationCreationRequiresLiveHttpsIdentityAndSingleCanonicalTransportHeaders(string invalid)
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        if (invalid == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (invalid is "no-client" or "bad-client" or "multiple-client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            invalid == "http" ? "http://localhost/api/wms/locations" : "/api/wms/locations")
        {
            Content = JsonContent.Create(new
            {
                code = "new-loc",
                name = "New location",
                warehouseId = ids.Warehouse,
                packId = LocationHttpSchema.Pack,
                schemaVersion = "v1"
            })
        };
        if (invalid != "missing-key")
            request.Headers.Add("Idempotency-Key", invalid == "empty-key" ? Guid.Empty.ToString("D")
                : invalid == "bad-key" ? "bad" : Guid.NewGuid().ToString("D"));
        if (invalid == "multiple-key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        if (invalid == "bad-client") request.Headers.Add("X-WES-Client", "untrusted");
        if (invalid == "multiple-client") request.Headers.Add("X-WES-Client", new[] { "ops-web", "pda" });

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(invalid == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(schema.Calls);
        await AssertNoLocationCreationAsync();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("exception")]
    public async Task WmsLocationCreationSchemaContractErrorsRemainSafeFailuresAndNeverCommit(string invalid)
    {
        // Arrange
        var schema = new LocationHttpSchema
        {
            Decision = invalid == "unknown" ? (LocationSchemaDecision)99 : LocationSchemaDecision.Valid,
            Failure = invalid == "exception" ? new InvalidOperationException("private-adapter-sentinel") : null
        };
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();

        // Act
        using var response = await CreateLocationHttpAsync(Guid.NewGuid(), ids.Warehouse);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("private-adapter-sentinel", await response.Content.ReadAsStringAsync());
        Assert.Single(schema.Calls);
        await AssertNoLocationCreationAsync();
    }

    [Fact]
    public async Task WmsLocationCreationCancellationReachesSchemaAndLeavesNoDurableEffect()
    {
        // Arrange
        var schema = new LocationHttpSchema { WaitForCancellation = true };
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/locations")
        {
            Content = JsonContent.Create(new
            {
                code = "new-loc",
                name = "New location",
                warehouseId = ids.Warehouse,
                packId = LocationHttpSchema.Pack,
                schemaVersion = "v1"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        var pending = _client.SendAsync(request, cancellation.Token);
        await schema.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await schema.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertNoLocationCreationAsync();
    }

    [Fact]
    public async Task WmsLocationCreationIgnoresSpoofedIdentityAndDoesNotGrantLocationRead()
    {
        // Arrange
        var ids = await LocationHostAsync(new LocationHttpSchema());
        await GrantLocationCreationAsync();
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(row => row.Code == "WMS");
            role.Change(role.Version, true, role.Rules.Where(rule => rule.Permission != "wms.locations.read"));
            await platform.SaveChangesAsync();
        }
        var operation = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/wms/locations?tenantId={_wmsForeignTenant:D}&actorId={Guid.NewGuid():D}&enabled=true")
        {
            Content = JsonContent.Create(new
            {
                code = "new-loc",
                name = "New location",
                warehouseId = ids.Warehouse,
                packId = LocationHttpSchema.Pack,
                schemaVersion = "v1"
            })
        };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        request.Headers.Add("X-Tenant-Id", _wmsForeignTenant.ToString("D"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var deniedRead = await _client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        await using var verify = OpenWmsMasterData();
        var fact = await verify.MasterDataChanges.SingleAsync(row => row.OperationId == operation);
        Assert.Equal((_tenant, _user), (fact.TenantId, fact.ActorId));
        await using var foreign = OpenWmsMasterData(_wmsForeignTenant);
        Assert.Empty(await foreign.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData("missing-warehouse")]
    [InlineData("missing-pack")]
    [InlineData("missing-schema")]
    [InlineData("missing-code")]
    [InlineData("missing-name")]
    [InlineData("invalid-type")]
    [InlineData("malformed")]
    public async Task WmsLocationCreationRejectsIncompleteOrMalformedBody(string invalid)
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        var body = new Dictionary<string, object?>
        {
            ["code"] = "new-loc",
            ["name"] = "New location",
            ["warehouseId"] = ids.Warehouse,
            ["packId"] = LocationHttpSchema.Pack,
            ["schemaVersion"] = "v1"
        };
        if (invalid.StartsWith("missing-", StringComparison.Ordinal))
            body.Remove(invalid switch
            {
                "missing-warehouse" => "warehouseId",
                "missing-pack" => "packId",
                "missing-schema" => "schemaVersion",
                "missing-code" => "code",
                _ => "name"
            });
        if (invalid == "invalid-type") body["code"] = 42;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/locations")
        { Content = new StringContent(invalid == "malformed" ? "{" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(schema.Calls);
        await AssertNoLocationCreationAsync();
    }
}
