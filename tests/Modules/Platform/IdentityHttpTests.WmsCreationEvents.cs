using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task WmsHostedPublisherRunnerUsesANewRealOwnerForEachBoundedTenantPassWithoutEnablingDefaultWorker()
    {
        await WmsHostAsync();
        Assert.DoesNotContain(_factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service is Wes.Host.Wms.WmsPublisherWorker);
        Guid[] events;
        await using (var before = OpenWmsMasterData())
            events = await before.OutboxMessages.Select(message => message.EventId).ToArrayAsync();
        var runner = new Wes.Host.Wms.WmsPublisherRunner(_factory.Services.GetRequiredService<Wes.Host.Identity.IdentitySettings>(),
            new CreationEventTransport(2), _clock, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        Assert.Equal(new Wes.Wms.WmsPublishBatchResult(1, 0, 0, 0, 0), await runner.RunAsync(_tenant, 1, default));
        Assert.Equal(new Wes.Wms.WmsPublishBatchResult(1, 0, 0, 0, 0), await runner.RunAsync(_tenant, 1, default));
        Assert.Equal(new Wes.Wms.WmsPublishBatchResult(0, 0, 0, 0, 0), await runner.RunAsync(_tenant, 1, default));
        Assert.Equal(new Wes.Wms.WmsPublishBatchResult(0, 0, 0, 0, 0), await runner.RunAsync(_wmsForeignTenant, 1, default));

        await using var verify = OpenWmsMasterData();
        var messages = await verify.OutboxMessages.ToListAsync();
        Assert.Equal(events.Order(), messages.Select(message => message.EventId).Order());
        Assert.All(messages, message => Assert.Equal((Wes.Wms.Domain.WmsOutboxStatus.Published, 1), (message.Status, message.Attempts)));
        Assert.All(await verify.Warehouses.ToListAsync(), warehouse => Assert.False(warehouse.Enabled));
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WmsCreationEventsExposeDurablePublisherStatesWithoutChangingNineFieldsOrPendingCount(int status)
    {
        await WmsHostAsync();
        await GrantCreationEventReadAsync();
        await GrantCreationEventCountsAsync();
        await using (var publisher = new Wes.Wms.WmsPublishingScope(_connection, _tenant,
            new CreationEventTransport(status), _clock, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance))
        {
            if (status == 1) await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.DispatchDueAsync(1));
            else
            {
                var delivered = await publisher.DispatchDueAsync(1);
                Assert.Equal(status == 2 ? 1 : 0, delivered.Published);
                Assert.Equal(status == 3 ? 1 : 0, delivered.Quarantined);
            }
        }
        using var response = await _client.GetAsync("/api/wms/master-data-events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = json.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { 0, status }, rows.Select(row => row.GetProperty("status").GetInt32()).Order());
        Assert.All(rows, row => Assert.Equal(9, row.EnumerateObject().Count()));
        using var summary = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var counts = await summary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, counts.EnumerateObject().Count());
        Assert.Equal(1, counts.GetProperty("pendingCount").GetInt32());
    }

    private sealed class CreationEventTransport(int status) : Wes.Wms.Messaging.IWmsOutboxTransport
    {
        public Task<Wes.Wms.Messaging.WmsOutboxTransportOutcome> PublishAsync(Wes.Wms.Messaging.WmsOutboxEnvelope envelope,
            CancellationToken cancellationToken) => status == 1
                ? throw new InvalidOperationException("Controlled send interruption.")
                : Task.FromResult(status == 2 ? Wes.Wms.Messaging.WmsOutboxTransportOutcome.Acknowledged
                    : Wes.Wms.Messaging.WmsOutboxTransportOutcome.PermanentFailure);
    }

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
