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
    public async Task WmsInboundRevisionIsIndependentOfCreationAndDefaultDisabled()
    {
        var ids = await WmsHostAsync(inboundDraftCreation: true);
        await GrantInboundDraftAsync();
        var (id, input) = await SeedRevisionHttpAsync(ids.Warehouse);
        using var disabled = await ReviseInboundHttpAsync(id, Guid.NewGuid(), input);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.InboundFacts.ToListAsync());
    }

    [Fact]
    public async Task WmsInboundRevisionReturnsOriginalReceiptAndReadsCurrentPlanWithoutCreationEnabled()
    {
        var ids = await WmsHostAsync(inboundDraftRevision: true);
        await GrantInboundDraftAsync();
        var (id, input) = await SeedRevisionHttpAsync(ids.Warehouse);
        var operation = Guid.NewGuid();
        using var updated = await ReviseInboundHttpAsync(id, operation, input);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("no-store", updated.Headers.CacheControl?.ToString());
        var receipt = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, receipt.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, receipt.GetProperty("decision").GetInt32());
        Assert.Equal(id, receipt.GetProperty("id").GetGuid());
        using var detail = await _client.GetAsync($"/api/wms/inbound/{id:D}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("7.000000", Assert.Single(body.GetProperty("lines").EnumerateArray()).GetProperty("planned").GetString());
        Assert.Equal(receipt.GetProperty("version").GetString(), body.GetProperty("version").GetString());
        await using (var approve = OpenWmsMasterData())
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(approve, _clock).ApproveAsync(
                id, Guid.NewGuid(), _user, Guid.NewGuid(), receipt.GetProperty("version").GetString()!)).Decision);
        using var replayed = await ReviseInboundHttpAsync(id, operation, input);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, replay.GetProperty("decision").GetInt32());
        Assert.Equal(receipt.GetProperty("version").GetString(), replay.GetProperty("version").GetString());
        using var changed = await ReviseInboundHttpAsync(id, operation, input with { ExpectedVersion = Guid.NewGuid().ToString("D") });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var nonDraft = await ReviseInboundHttpAsync(id, Guid.NewGuid(), input);
        Assert.Equal(HttpStatusCode.Conflict, nonDraft.StatusCode);
        using var creation = await CreateInboundDraftHttpAsync(Guid.NewGuid(), InboundDraftInput(ids.Warehouse));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, creation.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(2, await verify.InboundFacts.CountAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
        Assert.Empty(await verify.Balances.ToListAsync());
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
    }

    [Theory]
    [InlineData("Code")]
    [InlineData("WarehouseId")]
    [InlineData("Lines")]
    [InlineData("read:Id")]
    [InlineData("read:Code")]
    [InlineData("read:WarehouseId")]
    [InlineData("read:State")]
    [InlineData("read:Version")]
    [InlineData("read:CreatedAt")]
    [InlineData("read:Lines")]
    [InlineData("read:Receipts")]
    [InlineData("role")]
    [InlineData("membership")]
    public async Task WmsInboundRevisionRechecksEveryCurrentFieldAndMembershipOnReplay(string revoke)
    {
        var ids = await WmsHostAsync(inboundDraftRevision: true);
        await GrantInboundDraftAsync();
        var (id, input) = await SeedRevisionHttpAsync(ids.Warehouse);
        var operation = Guid.NewGuid();
        using var revised = await ReviseInboundHttpAsync(id, operation, input);
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(item => item.Code == "INBOUND-DRAFT");
            if (revoke == "membership") platform.Memberships.Remove(await platform.Memberships.SingleAsync(item => item.RoleId == role.Id));
            else role.Change(role.Version, revoke != "role",
                [new("wms.inbound.write", DataScope.Own, [], new[] { "Code", "WarehouseId", "Lines" }
                    .Where(field => field != revoke).ToImmutableHashSet()),
                 new("wms.inbound.read", DataScope.Own, [], InboundReadFields.Where(field => $"read:{field}" != revoke).ToImmutableHashSet())]);
            await platform.SaveChangesAsync();
        }
        using var denied = await ReviseInboundHttpAsync(id, operation, input);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("version", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WmsInboundRevisionUsesActualCreatorAndIgnoresSpoofedCreatorHeaders()
    {
        var ids = await WmsHostAsync(inboundDraftRevision: true);
        await GrantInboundDraftAsync();
        var (id, input) = await SeedRevisionHttpAsync(ids.Warehouse, Guid.NewGuid());
        _client.DefaultRequestHeaders.Add("X-Creator-Id", _user.ToString("D"));
        using var denied = await ReviseInboundHttpAsync(id, Guid.NewGuid(), input);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.InboundFacts.ToListAsync());
    }

    [Theory]
    [InlineData("missing-version")]
    [InlineData("bad-version")]
    [InlineData("unknown-root")]
    [InlineData("missing-precision")]
    [InlineData("unknown-line")]
    [InlineData("number-quantity")]
    [InlineData("duplicate-line")]
    [InlineData("empty-plan")]
    [InlineData("null-line")]
    [InlineData("precision")]
    [InlineData("quantity")]
    public async Task WmsInboundRevisionRejectsStrictMalformedInputWithoutFacts(string variant)
    {
        var ids = await WmsHostAsync(inboundDraftRevision: true);
        await GrantInboundDraftAsync();
        var (id, input) = await SeedRevisionHttpAsync(ids.Warehouse);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(input,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        var lines = node["lines"]!.AsArray();
        switch (variant)
        {
            case "missing-version": node.Remove("expectedVersion"); break;
            case "bad-version": node["expectedVersion"] = "bad"; break;
            case "unknown-root": node["warehouseId"] = ids.Warehouse.ToString("D"); break;
            case "missing-precision": lines[0]!.AsObject().Remove("precision"); break;
            case "unknown-line": lines[0]!["received"] = "1"; break;
            case "number-quantity": lines[0]!["planned"] = 7; break;
            case "duplicate-line": lines.Add(lines[0]!.DeepClone()); break;
            case "empty-plan": lines.Clear(); break;
            case "null-line": lines[0] = null; break;
            case "precision": lines[0]!["precision"] = 7; break;
            case "quantity": lines[0]!["planned"] = "1e2"; break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/wms/inbound/{id:D}/draft")
        { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.InboundFacts.ToListAsync());
        Assert.Equal(input.ExpectedVersion, (await verify.InboundDocuments.SingleAsync()).Version);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("http", HttpStatusCode.BadRequest)]
    [InlineData("session", HttpStatusCode.Unauthorized)]
    [InlineData("key", HttpStatusCode.BadRequest)]
    [InlineData("client", HttpStatusCode.BadRequest)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    public async Task WmsInboundRevisionRequiresTrustedHttpContextAndExactIdentity(string variant, HttpStatusCode status)
    {
        var ids = await WmsHostAsync(inboundDraftRevision: true);
        await GrantInboundDraftAsync();
        var (id, input) = await SeedRevisionHttpAsync(ids.Warehouse);
        if (variant == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (variant == "session") { using var logout = await _client.PostAsync("/api/identity/logout", null); }
        if (variant == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        var target = variant == "missing" ? Guid.NewGuid() : variant == "empty" ? Guid.Empty : id;
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{(variant == "http" ? "http://localhost" : "")}/api/wms/inbound/{target:D}/draft")
        { Content = JsonContent.Create(input) };
        if (variant != "key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        await using var verify = OpenWmsMasterData();
        Assert.Empty(await verify.InboundFacts.ToListAsync());
    }

    private async Task<(Guid Id, ReviseInboundDraftInput Input)> SeedRevisionHttpAsync(Guid warehouse, Guid? creator = null)
    {
        var line = new Wes.Wms.Domain.InboundPlanLine(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, 5m);
        await using var owner = OpenWmsMasterData();
        var result = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), creator ?? _user, warehouse, "IN-REV", [line]);
        Assert.Equal(InboundWriteDecision.Committed, result.Decision);
        return (result.Id!.Value, new(result.Version!, [new(line.Id, line.ItemId, line.UnitId, line.Batch, line.Precision, "7")]));
    }

    private async Task<HttpResponseMessage> ReviseInboundHttpAsync(Guid id, Guid operation, ReviseInboundDraftInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/wms/inbound/{id:D}/draft") { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        return await _client.SendAsync(request);
    }
}
