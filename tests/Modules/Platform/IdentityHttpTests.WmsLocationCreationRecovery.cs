using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData(64, 128, 64, true)]
    [InlineData(65, 128, 64, false)]
    [InlineData(64, 129, 64, false)]
    [InlineData(64, 128, 65, false)]
    public async Task WmsLocationCreationEnforcesExactCodeNameAndSchemaBounds(int codeLength, int nameLength, int schemaLength, bool valid)
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/locations")
        {
            Content = JsonContent.Create(new
            {
                code = new string('a', codeLength),
                name = new string('N', nameLength),
                warehouseId = ids.Warehouse,
                packId = LocationHttpSchema.Pack,
                schemaVersion = new string('v', schemaLength)
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(valid ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
        if (!valid)
        {
            Assert.Empty(schema.Calls);
            await AssertNoLocationCreationAsync();
        }
        else
        {
            Assert.Equal(new string('A', codeLength), Assert.Single(schema.Calls).Code);
            await using var verify = OpenWmsMasterData();
            var location = await verify.Locations.SingleAsync(row => row.Code != "A1");
            Assert.Equal((codeLength, nameLength, schemaLength), (location.Code.Length, location.Name.Length, location.SchemaVersion.Length));
        }
    }

    [Fact]
    public async Task WmsLocationCreationDisabledDoesNotResolveOrCallSuppliedSchema()
    {
        // Arrange
        var resolutions = 0;
        var ids = await WmsHostAsync(warehouseCreation: true, locationSchemas: services =>
            services.AddSingleton<IWarehouseLocationSchema>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                throw new InvalidOperationException("Disabled schema must never be resolved.");
            }));
        await GrantWarehouseCreationAsync();

        // Act
        using var denied = await CreateLocationHttpAsync(Guid.NewGuid(), ids.Warehouse);
        using var warehouse = await CreateWarehouseHttpAsync(Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.MethodNotAllowed, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Created, warehouse.StatusCode);
        Assert.Equal(0, resolutions);
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.Locations.ToListAsync());
    }

    [Fact]
    public async Task WmsLocationCreationReadGrantsNeverImplyWriteAuthority()
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);

        // Act
        using var response = await CreateLocationHttpAsync(Guid.NewGuid(), ids.Warehouse);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(schema.Calls);
        await AssertNoLocationCreationAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WmsLocationCreationMapsSafeDatabaseFailureWithoutPretendingSchemaRejection(bool transient)
    {
        // Arrange: exercise the HTTP safe-exception mapping, not a real provider outage.
        var ids = await LocationHostAsync(new LocationHttpSchema { Failure = new WmsDatabaseException(transient) });
        await GrantLocationCreationAsync();

        // Act
        using var response = await CreateLocationHttpAsync(Guid.NewGuid(), ids.Warehouse);

        // Assert
        Assert.Equal(transient ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("WMS location creation failed.", body);
        Assert.DoesNotContain("MySql", body, StringComparison.OrdinalIgnoreCase);
        await AssertNoLocationCreationAsync();
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("schema")]
    public async Task WmsLocationCreationCannotReusePermanentOperationForAnotherPinnedTuple(string changed)
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        var operation = Guid.NewGuid();
        using var created = await CreateLocationHttpAsync(operation, ids.Warehouse);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/locations")
        {
            Content = JsonContent.Create(new
            {
                code = "new-loc",
                name = "New location",
                warehouseId = ids.Warehouse,
                packId = changed == "pack" ? Guid.NewGuid() : LocationHttpSchema.Pack,
                schemaVersion = changed == "schema" ? "v2" : "v1"
            })
        };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));

        // Act
        using var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(schema.Calls);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(3, await verify.OutboxMessages.CountAsync());
        Assert.Equal(2, await verify.Locations.CountAsync());
    }
}
