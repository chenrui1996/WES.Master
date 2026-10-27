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
    public async Task WmsEventCountsRequireExplicitAssembly()
    {
        using var response = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsEventCountsExposeOnlyThreeFieldsWithoutSyntheticInboxOrWrites()
    {
        await WmsHostAsync();
        await GrantCreationEventCountsAsync();
        using var response = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "owner", "observedAt", "pendingCount" }, json.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Wms", json.GetProperty("owner").GetString());
        Assert.Equal(_clock.GetUtcNow(), json.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(2, json.GetProperty("pendingCount").GetInt64());
        using var deniedRows = await _client.GetAsync("/api/wms/master-data-events");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRows.StatusCode);
        using var post = await _client.PostAsJsonAsync("/api/wms/master-data-events/summary", new { });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("ObservedAt")]
    [InlineData("PendingCount")]
    public async Task WmsEventCountsRecheckEveryCurrentField(string missing)
    {
        await WmsHostAsync();
        await GrantCreationEventCountsAsync();
        using var before = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(item => item.Code == "WMS-EVENT-COUNTS");
            role.Change(role.Version, true, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove(missing) }));
            await platform.SaveChangesAsync();
        }
        using var response = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("pendingCount", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WmsEventCountsCannotBorrowEventRowsOrOtherOwnerPermissions()
    {
        await WmsHostAsync();
        await GrantCreationEventReadAsync();
        await GrantPlatformAsync();
        using var response = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("http")]
    [InlineData("logout")]
    public async Task WmsEventCountsRequireCurrentHttpsIdentity(string variant)
    {
        await WmsHostAsync();
        await GrantCreationEventCountsAsync();
        if (variant == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (variant == "logout")
        {
            using var logout = await _client.PostAsync("/api/identity/logout", null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        using var response = await _client.GetAsync(variant == "http" ? "http://localhost/api/wms/master-data-events/summary" : "/api/wms/master-data-events/summary");
        Assert.Equal(variant == "http" ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("membership")]
    public async Task WmsEventCountsRecheckCurrentAuthority(string revoke)
    {
        await WmsHostAsync();
        await GrantCreationEventCountsAsync();
        using var before = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(item => item.Code == "WMS-EVENT-COUNTS");
            if (revoke == "role") role.Change(role.Version, false, role.Rules);
            else platform.Memberships.Remove(await platform.Memberships.SingleAsync(item => item.RoleId == role.Id));
            await platform.SaveChangesAsync();
        }
        using var response = await _client.GetAsync("/api/wms/master-data-events/summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WmsEventCountsIgnoreSpoofedTenantCreatorAndPagination()
    {
        await WmsHostAsync();
        await GrantCreationEventCountsAsync();
        await using (var foreign = OpenWmsMasterData(_wmsForeignTenant))
        {
            var warehouse = new Wes.Wms.Domain.Warehouse(_wmsForeignTenant, "FOREIGN", "Foreign", _clock.GetUtcNow());
            foreign.Warehouses.Add(warehouse);
            foreign.MasterDataChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), warehouse));
            await foreign.SaveChangesAsync();
        }
        using var response = await _client.GetAsync($"/api/wms/master-data-events/summary?tenantId={_wmsForeignTenant:D}&creatorId={Guid.NewGuid():D}&limit=1&offset=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pendingCount").GetInt64());
    }

    private async Task GrantCreationEventCountsAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "WMS-EVENT-COUNTS",
        [
            new("wms.messages.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Owner", "ObservedAt", "PendingCount"))
        ]);
        platform.Roles.Add(role);
        platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
    }
}
