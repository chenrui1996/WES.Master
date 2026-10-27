using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Platform.Domain;
using Wes.Wms.Domain;
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
    public async Task WmsLocationCreationRejectsUnassembledSchemaBeforeStartupCompletes(string invalid)
    {
        // Arrange: no schema result is a substitute for an explicitly assembled adapter.
        var schema = new LocationHttpSchema();
        void Register(IServiceCollection services)
        {
            if (invalid == "duplicate") { services.AddSingleton<IWarehouseLocationSchema>(schema); services.AddSingleton<IWarehouseLocationSchema>(schema); }
            if (invalid == "scoped") services.AddScoped<IWarehouseLocationSchema>(_ => schema);
            if (invalid == "transient") services.AddTransient<IWarehouseLocationSchema>(_ => schema);
            if (invalid == "keyed") services.AddKeyedSingleton<IWarehouseLocationSchema>("not-default", schema);
        }

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WmsHostAsync(locationCreation: true, locationSchemas: Register));

        // Assert
        Assert.Contains("Location creation requires exactly one explicitly configured singleton schema adapter.", error.Message);
    }

    [Fact]
    public async Task WmsLocationCreationCommitsOneDisabledIdentityFactAndEventAndReplaysOriginalReceipt()
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        var operation = Guid.NewGuid();

        // Act
        using var created = await CreateLocationHttpAsync(operation, ids.Warehouse);
        using var replayed = await CreateLocationHttpAsync(operation, ids.Warehouse);

        // Assert
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var first = await created.Content.ReadFromJsonAsync<JsonElement>();
        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, first.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, first.GetProperty("decision").GetInt32());
        Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        var id = first.GetProperty("id").GetGuid();
        Assert.Equal(id, replay.GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("version").GetString(), replay.GetProperty("version").GetString());
        Assert.Equal($"/api/wms/locations/{id:D}", created.Headers.Location?.OriginalString);
        Assert.Equal((_tenant, ids.Warehouse, LocationHttpSchema.Pack, "v1", "NEW-LOC"), Assert.Single(schema.Calls));
        using var read = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        await using var verify = OpenWmsMasterData();
        var location = await verify.Locations.SingleAsync(row => row.Id == id);
        Assert.False(location.Enabled);
        Assert.Equal(LocationHttpSchema.Pack, location.PackId);
        Assert.Equal(ids.Warehouse, location.WarehouseId);
        var fact = await verify.MasterDataChanges.SingleAsync(row => row.OperationId == operation);
        var message = await verify.OutboxMessages.SingleAsync(row => row.EventId == operation);
        Assert.Equal((_user, id, first.GetProperty("version").GetString()), (fact.ActorId, fact.LocationId, fact.AfterVersion));
        Assert.Equal((id, fact.CorrelationId, WmsOutboxStatus.Pending), (message.AggregateId, message.CorrelationId, message.Status));
        Assert.Equal(3, await verify.MasterDataChanges.CountAsync());
        Assert.Equal(3, await verify.OutboxMessages.CountAsync());
        Assert.Equal(2, await verify.Locations.CountAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
        Assert.All(await verify.Warehouses.ToListAsync(), warehouse => Assert.False(warehouse.Enabled));
    }

    [Theory]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("WarehouseId")]
    [InlineData("PackId")]
    [InlineData("SchemaVersion")]
    [InlineData("parent-read")]
    public async Task WmsLocationCreationRequiresEveryCurrentWriteFieldAndParentReadBeforeSchema(string missing)
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync(new[] { "Code", "Name", "WarehouseId", "PackId", "SchemaVersion" }
            .Where(field => field != missing).ToArray());
        if (missing == "parent-read") await DisableWmsReadRoleAsync();

        // Act
        using var denied = await CreateLocationHttpAsync(Guid.NewGuid(), ids.Warehouse);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(schema.Calls);
        await AssertNoLocationCreationAsync();
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected, HttpStatusCode.UnprocessableEntity)]
    [InlineData(LocationSchemaDecision.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task WmsLocationCreationCannotTurnMissingSchemaEvidenceIntoSuccess(LocationSchemaDecision decision, HttpStatusCode expected)
    {
        // Arrange
        var schema = new LocationHttpSchema { Decision = decision };
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();

        // Act
        using var response = await CreateLocationHttpAsync(Guid.NewGuid(), ids.Warehouse);

        // Assert
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("\"id\"", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(schema.Calls);
        await AssertNoLocationCreationAsync();
    }

    [Fact]
    public async Task WmsLocationCreationRejectsForeignParentBeforeSchemaOrAnyWrite()
    {
        // Arrange
        var schema = new LocationHttpSchema();
        await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        var foreign = new Warehouse(_wmsForeignTenant, "FOREIGN", "Foreign", _clock.GetUtcNow());
        await using (var seed = OpenWmsMasterData(_wmsForeignTenant))
        {
            seed.Warehouses.Add(foreign);
            seed.MasterDataChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), foreign));
            await seed.SaveChangesAsync();
        }

        // Act
        using var denied = await CreateLocationHttpAsync(Guid.NewGuid(), foreign.Id);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Empty(schema.Calls);
        await AssertNoLocationCreationAsync();
    }

    [Theory]
    [InlineData("write")]
    [InlineData("parent")]
    [InlineData("session")]
    public async Task WmsLocationCreationReplaysOnlyWhileCurrentlyAuthorized(string revoked)
    {
        // Arrange
        var schema = new LocationHttpSchema();
        var ids = await LocationHostAsync(schema);
        await GrantLocationCreationAsync();
        var operation = Guid.NewGuid();
        using var created = await CreateLocationHttpAsync(operation, ids.Warehouse);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        if (revoked == "session")
        {
            using var logout = await _client.PostAsync("/api/identity/logout", null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        else if (revoked == "parent") await DisableWmsReadRoleAsync();
        else
        {
            await using var platform = Open();
            var role = await platform.Roles.SingleAsync(row => row.Code == "WMS-LOCATION-CREATE");
            role.Change(role.Version, false, role.Rules);
            await platform.SaveChangesAsync();
        }

        // Act
        using var denied = await CreateLocationHttpAsync(operation, ids.Warehouse);

        // Assert
        Assert.Equal(revoked == "session" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("\"id\"", await denied.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(schema.Calls);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(3, await verify.OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WmsLocationCreationConcurrentRequestsHaveOneDurableEffect(bool sameOperation)
    {
        // Arrange
        var ids = await LocationHostAsync(new LocationHttpSchema());
        await GrantLocationCreationAsync();
        var operation = Guid.NewGuid();

        // Act
        var responses = await Task.WhenAll(CreateLocationHttpAsync(operation, ids.Warehouse),
            CreateLocationHttpAsync(sameOperation ? operation : Guid.NewGuid(), ids.Warehouse));

        // Assert
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == (sameOperation ? HttpStatusCode.OK : HttpStatusCode.Conflict));
            using var changed = await CreateLocationHttpAsync(operation, ids.Warehouse, name: "Changed");
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            await using var verify = OpenWmsMasterData();
            Assert.Equal(2, await verify.Locations.CountAsync());
            Assert.Equal(3, await verify.MasterDataChanges.CountAsync());
            Assert.Equal(3, await verify.OutboxMessages.CountAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private Task<(Guid Warehouse, Guid Location)> LocationHostAsync(LocationHttpSchema schema) =>
        WmsHostAsync(locationCreation: true, locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(schema));

    private async Task<HttpResponseMessage> CreateLocationHttpAsync(Guid operation, Guid warehouse, string name = "New location")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wms/locations")
        {
            Content = JsonContent.Create(new { code = "new-loc", name, warehouseId = warehouse, packId = LocationHttpSchema.Pack, schemaVersion = "v1" })
        };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        return await _client.SendAsync(request);
    }

    private async Task GrantLocationCreationAsync(string[]? fields = null)
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "WMS-LOCATION-CREATE",
        [
            new("wms.locations.write", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                (fields ?? ["Code", "Name", "WarehouseId", "PackId", "SchemaVersion"]).ToImmutableHashSet(StringComparer.Ordinal))
        ]);
        platform.Roles.Add(role);
        platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
    }

    private async Task DisableWmsReadRoleAsync()
    {
        await using var platform = Open();
        var role = await platform.Roles.SingleAsync(row => row.Code == "WMS");
        role.Change(role.Version, false, role.Rules);
        await platform.SaveChangesAsync();
    }

    private async Task AssertNoLocationCreationAsync()
    {
        await using var verify = OpenWmsMasterData();
        Assert.Single(await verify.Locations.ToListAsync());
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    private sealed class LocationHttpSchema : IWarehouseLocationSchema
    {
        internal static readonly Guid Pack = Guid.NewGuid();
        internal LocationSchemaDecision Decision { get; init; } = LocationSchemaDecision.Valid;
        internal Exception? Failure { get; init; }
        internal bool WaitForCancellation { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal System.Collections.Concurrent.ConcurrentQueue<(Guid Tenant, Guid Warehouse, Guid Pack, string Version, string Code)> Calls { get; } = new();
        public async Task<LocationSchemaDecision> ValidateAsync(Guid tenantId, Guid warehouseId, Guid packId,
            string schemaVersion, string code, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue((tenantId, warehouseId, packId, schemaVersion, code));
            if (Failure is not null) throw Failure;
            if (WaitForCancellation)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
            return Decision;
        }
    }
}
