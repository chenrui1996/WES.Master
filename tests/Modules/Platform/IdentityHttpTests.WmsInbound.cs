using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    private static readonly string[] InboundReadFields =
        ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines", "Receipts"];

    [Fact]
    public async Task WmsInboundIsAbsentUnlessExplicitlyAssembled()
    {
        using var response = await _client.GetAsync($"/api/wms/inbound/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsInboundShowsOnlyExactAuthorizedShapeAndLosslessAmounts()
    {
        var id = await InboundHostAsync(receive: true);
        using var response = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var row = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(InboundReadFields.Select(field => char.ToLowerInvariant(field[0]) + field[1..]),
            row.EnumerateObject().Select(property => property.Name));
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.Equal(2, row.GetProperty("state").GetInt32());
        var line = Assert.Single(row.GetProperty("lines").EnumerateArray());
        Assert.Equal(new[] { "id", "itemId", "unitId", "batch", "precision", "planned", "received", "remaining" },
            line.EnumerateObject().Select(property => property.Name));
        Assert.Equal("999999999.999999", line.GetProperty("planned").GetString());
        Assert.Equal("0.000001", line.GetProperty("received").GetString());
        Assert.Equal("999999999.999998", line.GetProperty("remaining").GetString());
        var receipt = Assert.Single(row.GetProperty("receipts").EnumerateArray());
        Assert.Equal(new[] { "id", "lineId", "containerId", "receiptLocationId", "targetLocationId", "quantity",
            "requiresTransport", "arrived", "transportId" }, receipt.EnumerateObject().Select(property => property.Name));
        Assert.Equal("0.000001", receipt.GetProperty("quantity").GetString());
        Assert.True(receipt.GetProperty("requiresTransport").GetBoolean());
        Assert.False(receipt.GetProperty("arrived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("transportId").ValueKind);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"/api/wms/inbound/{id:D}");
            using var write = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
        }
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.InboundFacts.CountAsync());
        Assert.Single(await verify.Postings.ToListAsync());
    }

    [Theory]
    [InlineData("role")]
    [InlineData("field")]
    [InlineData("membership")]
    public async Task WmsInboundRechecksCurrentAuthorityOnEveryRequest(string revoked)
    {
        var id = await InboundHostAsync();
        using var first = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(item => item.Code == "INBOUND-READ");
            if (revoked == "membership") platform.Memberships.Remove(await platform.Memberships.SingleAsync(item => item.RoleId == role.Id));
            else role.Change(role.Version, revoked != "role",
                [new("wms.inbound.read", DataScope.Own, [], InboundReadFields.Where(field => field != "Lines").ToImmutableHashSet())]);
            await platform.SaveChangesAsync();
        }
        using var response = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("IN-HTTP", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WmsInboundOwnScopeUsesRealDocumentCreatorNotReaderOrWarehouseCreator()
    {
        var id = await InboundHostAsync(creator: Guid.NewGuid());
        using var denied = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task WmsInboundResolvesCreatorDepartmentAgainThroughPlatformOwner()
    {
        var creator = Guid.NewGuid();
        var id = await InboundHostAsync(creator);
        Guid outside;
        await using (var platform = Open())
        {
            var department = await platform.Departments.SingleAsync();
            var other = new DirectoryDepartment(_tenant, "INBOUND-OUTSIDE", "Outside");
            platform.Departments.Add(other);
            outside = other.Id;
            platform.DirectoryUsers.Add(new(_tenant, creator, "INBOUND-CREATOR", "Inbound creator", department.Id));
            var role = await platform.Roles.SingleAsync(item => item.Code == "INBOUND-READ");
            role.Change(role.Version, true,
                [new("wms.inbound.read", DataScope.Departments, ImmutableHashSet.Create(department.Id), InboundReadFields.ToImmutableHashSet())]);
            await platform.SaveChangesAsync();
        }
        using var allowed = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await using (var platform = Open())
        {
            var user = await platform.DirectoryUsers.SingleAsync(item => item.Id == creator);
            platform.Entry(user).Property(item => item.DepartmentId).CurrentValue = outside;
            await platform.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task WmsInboundRequiresEveryExactField()
    {
        var id = await InboundHostAsync();
        await using var platform = Open();
        var role = await platform.Roles.SingleAsync(item => item.Code == "INBOUND-READ");
        foreach (var missing in InboundReadFields)
        {
            role.Change(role.Version, true,
                [new("wms.inbound.read", DataScope.Own, [], InboundReadFields.Where(field => field != missing)
                    .Append(missing.ToLowerInvariant()).ToImmutableHashSet())]);
            await platform.SaveChangesAsync();
            using var response = await _client.GetAsync($"/api/wms/inbound/{id:D}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    public async Task WmsInboundRejectsInvalidOrUnknownIdentities(string variant)
    {
        await InboundHostAsync();
        var id = variant == "empty" ? Guid.Empty.ToString("D") : variant == "malformed" ? "bad" : Guid.NewGuid().ToString("D");
        using var response = await _client.GetAsync("/api/wms/inbound/" + id);
        Assert.Equal(variant == "empty" ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsInboundRequiresHttpsAuthenticationAndCurrentSession()
    {
        var id = await InboundHostAsync();
        var token = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        _client.DefaultRequestHeaders.Authorization = token;
        using var plain = await _client.GetAsync($"http://localhost/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        using var logout = await _client.PostAsync("/api/identity/logout", null);
        using var expired = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task WmsInboundDoesNotBorrowOtherWmsPermissions()
    {
        var ids = await WmsHostAsync();
        Guid id;
        await using (var owner = OpenWmsMasterData())
        {
            var created = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), _user, ids.Warehouse, "IN-NONE",
                [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, 5m)]);
            id = created.Id!.Value;
        }
        using var response = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WmsInboundCannotReadActualForeignTenantEvenWithSpoofedTenantHeaders()
    {
        await InboundHostAsync();
        Guid id;
        await using (var foreign = OpenWmsMasterData(_wmsForeignTenant))
        {
            var warehouse = new Warehouse(_wmsForeignTenant, "WH-IN", "Foreign", _clock.GetUtcNow());
            foreign.Warehouses.Add(warehouse);
            foreign.MasterDataChanges.Add(new(Guid.NewGuid(), _user, Guid.NewGuid(), warehouse));
            await foreign.SaveChangesAsync();
            foreign.ChangeTracker.Clear();
            var result = await new InboundWriter(foreign, _clock).CreateAsync(Guid.NewGuid(), _user, warehouse.Id,
                "FOREIGN-IN", [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, 1m)]);
            id = result.Id!.Value;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/wms/inbound/{id:D}?tenantId={_wmsForeignTenant:D}");
        request.Headers.Add("X-Tenant-Id", _wmsForeignTenant.ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("FOREIGN", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<Guid> InboundHostAsync(Guid? creator = null, bool receive = false)
    {
        var ids = await WmsHostAsync();
        return await SeedInboundAsync(ids, creator, receive);
    }

    private async Task<Guid> SeedInboundAsync((Guid Warehouse, Guid Location) ids, Guid? creator = null, bool receive = false)
    {
        await using (var platform = Open())
        {
            var role = new AccessRole(_tenant, "INBOUND-READ",
                [new("wms.inbound.read", DataScope.Own, [], InboundReadFields.ToImmutableHashSet(StringComparer.Ordinal))]);
            platform.Roles.Add(role);
            platform.Memberships.Add(new(_tenant, _user, role.Id));
            await platform.SaveChangesAsync();
        }
        var line = new InboundPlanLine(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 6, 999999999.999999m);
        Guid id;
        string version;
        await using (var owner = OpenWmsMasterData())
        {
            var result = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), creator ?? _user, ids.Warehouse, "IN-HTTP", [line]);
            Assert.Equal(InboundWriteDecision.Committed, result.Decision);
            id = result.Id!.Value;
            version = result.Version!;
        }
        if (!receive) return id;
        await using (var owner = OpenWmsMasterData())
        {
            var result = await new InboundWriter(owner, _clock).ApproveAsync(id, Guid.NewGuid(), _user, Guid.NewGuid(), version);
            Assert.Equal(InboundWriteDecision.Committed, result.Decision);
            version = result.Version!;
        }
        StockBalance balance;
        await using (var owner = OpenWmsMasterData())
        {
            balance = new(_tenant, ids.Warehouse, ids.Location, line.ItemId, "", Guid.NewGuid(), line.UnitId, 6);
            owner.Balances.Add(balance);
            await owner.SaveChangesAsync();
        }
        await using (var owner = OpenWmsMasterData())
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(owner, _clock).ReceiveAsync(id,
                new(Guid.NewGuid(), _user, Guid.NewGuid(), line.Id, line.ItemId, line.UnitId, "", balance.ContainerId,
                    ids.Location, Guid.NewGuid(), 0.000001m), Guid.NewGuid(), version, balance.Id, balance.Version)).Decision);
        return id;
    }
}
