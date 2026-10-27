using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task WmsCreationEventsRequireExplicitReadAssembly()
    {
        using var response = await _client.GetAsync("/api/wms/master-data-events");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsCreationEventsExposeOnlyCurrentNineFieldMetadataWithoutEnablingWarehouseWrites()
    {
        await WmsHostAsync();
        await GrantCreationEventReadAsync();
        using var response = await _client.GetAsync("/api/wms/master-data-events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "rows", "offset", "limit" }, json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, json.GetProperty("offset").GetInt32());
        Assert.Equal(25, json.GetProperty("limit").GetInt32());
        var rows = json.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        await using var verify = OpenWmsMasterData();
        foreach (var row in rows)
        {
            Assert.Equal(new[] { "eventId", "owner", "contractName", "schemaVersion", "aggregateId", "aggregateVersion", "correlationId", "occurredAt", "status" },
                row.EnumerateObject().Select(property => property.Name));
            Assert.Equal("Wms", row.GetProperty("owner").GetString());
            Assert.Equal("wms.master-data.created", row.GetProperty("contractName").GetString());
            Assert.Equal(1, row.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(0, row.GetProperty("status").GetInt32());
            var eventId = row.GetProperty("eventId").GetGuid();
            var fact = await verify.MasterDataChanges.SingleAsync(item => item.OperationId == eventId);
            Assert.Equal(fact.LocationId ?? fact.WarehouseId, row.GetProperty("aggregateId").GetGuid());
            Assert.Equal(fact.AfterVersion, row.GetProperty("aggregateVersion").GetString());
            Assert.Equal(fact.CorrelationId, row.GetProperty("correlationId").GetGuid());
            Assert.Equal(fact.OccurredAt, row.GetProperty("occurredAt").GetDateTimeOffset());
        }
        using var notEnabled = await CreateWarehouseHttpAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, notEnabled.StatusCode);
        using var post = await _client.PostAsJsonAsync("/api/wms/master-data-events", new { eventId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task WmsCreationEventsCannotUseMasterDataOrPlatformMessageAuthority()
    {
        await WmsHostAsync(warehouseCreation: true);
        await GrantWarehouseCreationAsync();
        await GrantPlatformAsync();
        using var response = await _client.GetAsync("/api/wms/master-data-events");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("wms.master-data.created", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?offset=-1")]
    [InlineData("?offset=10001")]
    [InlineData("?offset=abc")]
    [InlineData("?limit=10")]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    public async Task WmsCreationEventsRejectInvalidPageParameters(string query)
    {
        await WmsHostAsync();
        await GrantCreationEventReadAsync();
        using var response = await _client.GetAsync("/api/wms/master-data-events" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("http")]
    [InlineData("logout")]
    public async Task WmsCreationEventsRequireLiveHttpsIdentity(string variant)
    {
        await WmsHostAsync();
        await GrantCreationEventReadAsync();
        if (variant == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (variant == "logout")
        {
            using var logout = await _client.PostAsync("/api/identity/logout", null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        using var response = await _client.GetAsync(variant == "http" ? "http://localhost/api/wms/master-data-events" : "/api/wms/master-data-events");
        Assert.Equal(variant == "http" ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WmsCreationEventsRecheckFieldsAndIgnoreSpoofedCreatorOrTenant()
    {
        await WmsHostAsync();
        await GrantCreationEventReadAsync();
        await using (var foreign = OpenWmsMasterData(_wmsForeignTenant))
        {
            var warehouse = new Wes.Wms.Domain.Warehouse(_wmsForeignTenant, "FOREIGN", "Foreign", _clock.GetUtcNow());
            foreign.Warehouses.Add(warehouse);
            foreign.MasterDataChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), warehouse));
            await foreign.SaveChangesAsync();
        }
        using var response = await _client.GetAsync($"/api/wms/master-data-events?tenantId={_wmsForeignTenant:D}&creatorId={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").GetArrayLength());
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(item => item.Code == "WMS-EVENT-READ");
            role.Change(role.Version, true, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Owner") }));
            await platform.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync("/api/wms/master-data-events");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("aggregateId", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task GrantCreationEventReadAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "WMS-EVENT-READ",
        [
            new("wms.messages.read", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                ImmutableHashSet.Create("EventId", "Owner", "ContractName", "SchemaVersion", "AggregateId",
                    "AggregateVersion", "CorrelationId", "OccurredAt", "Status"))
        ]);
        platform.Roles.Add(role);
        platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
    }
}
