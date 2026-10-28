using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Host.Wms;
using Wes.Platform.Domain;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task InboundExecutionHttpApprovesReceivesAndRecoversOnlyExactOriginalBodies()
    {
        var fixture = await ExecutionHttpHostAsync();
        var operation = Guid.NewGuid(); var input = new InboundApprovalInput(fixture.OrderVersion);
        using var approval = await ExecutionPostAsync(fixture.Order, "approval", operation, input);
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        var approved = await approval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "id", "version" }, approved.EnumerateObject().Select(row => row.Name));
        Assert.Equal("no-store", approval.Headers.CacheControl?.ToString());
        using var recovered = await ExecutionPostAsync(fixture.Order, "approval", operation, input);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        using var conflict = await ExecutionPostAsync(fixture.Order, "approval", operation, input with { ExpectedVersion = Guid.NewGuid().ToString("D") });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var receiptInput = fixture.Receipt(approved.GetProperty("version").GetString()!);
        var receiptOperation = Guid.NewGuid();
        using var received = await ExecutionPostAsync(fixture.Order, "receipts", receiptOperation, receiptInput);
        Assert.Equal(HttpStatusCode.Created, received.StatusCode);
        var receipt = await received.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(receiptInput.ExecutionId, receipt.GetProperty("id").GetGuid());
        using var receiptRecovered = await ExecutionPostAsync(fixture.Order, "receipts", receiptOperation, receiptInput);
        Assert.Equal(HttpStatusCode.OK, receiptRecovered.StatusCode);
        using var receiptConflict = await ExecutionPostAsync(fixture.Order, "receipts", receiptOperation, receiptInput with { Quantity = "5" });
        Assert.Equal(HttpStatusCode.Conflict, receiptConflict.StatusCode);
        using var current = await _client.GetAsync($"/api/wms/inbound/{fixture.Order:D}");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var definition = await current.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)InboundState.Completed, definition.GetProperty("state").GetInt32());
        await using var verify = OpenWmsMasterData();
        Assert.Equal(5m, (await verify.Balances.SingleAsync()).Quantity);
        Assert.Single(await verify.Postings.ToListAsync()); Assert.Single(await verify.Entries.ToListAsync());
        Assert.Equal(2, await verify.InboundExecutionOperations.CountAsync());
        Assert.All(await verify.InboundExecutionOperations.ToArrayAsync(), row => Assert.Equal(_user, row.ActorId));
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("receipts")]
    public async Task InboundExecutionHttpIsDefaultDisabled(string action)
    {
        await WmsHostAsync();
        using var response = await ExecutionPostAsync(Guid.NewGuid(), action, Guid.NewGuid(), new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing-version")]
    [InlineData("missing-execution")]
    [InlineData("quantity-number")]
    [InlineData("quantity-exponent")]
    [InlineData("supplied-actor")]
    public async Task InboundExecutionHttpRejectsMalformedReceiptWithoutLedger(string invalid)
    {
        var fixture = await ExecutionHttpHostAsync();
        using var approved = await ExecutionPostAsync(fixture.Order, "approval", Guid.NewGuid(), new InboundApprovalInput(fixture.OrderVersion));
        var version = (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
        var body = JsonSerializer.SerializeToNode(fixture.Receipt(version), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (invalid == "unknown") body["precision"] = 0;
        if (invalid == "missing-version") body.Remove("expectedVersion");
        if (invalid == "missing-execution") body.Remove("executionId");
        if (invalid == "quantity-number") body["quantity"] = 5;
        if (invalid == "quantity-exponent") body["quantity"] = "5e0";
        if (invalid == "supplied-actor") body["actorId"] = _user.ToString("D");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/wms/inbound/{fixture.Order:D}/receipts")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D")); request.Headers.Add("X-WES-Client", "ops-web");
        using var response = await _client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.Postings.ToListAsync());
        Assert.Single(await verify.InboundExecutionOperations.ToListAsync()); Assert.Equal(0m, (await verify.Balances.SingleAsync()).Quantity);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("receipts")]
    public async Task InboundExecutionHttpRechecksCurrentRoleWithoutReplacingToken(string action)
    {
        var fixture = await ExecutionHttpHostAsync();
        await using (var platform = Open())
        {
            var role = await platform.Roles.SingleAsync(row => row.Code == "INBOUND-EXECUTION");
            role.Change(role.Version, true, role.Rules.Where(rule => rule.Permission != $"wms.inbound.{(action == "approval" ? "approve" : "receive")}"));
            await platform.SaveChangesAsync();
        }
        using var response = action == "approval"
            ? await ExecutionPostAsync(fixture.Order, action, Guid.NewGuid(), new InboundApprovalInput(fixture.OrderVersion))
            : await ExecutionPostAsync(fixture.Order, action, Guid.NewGuid(), fixture.Receipt(fixture.OrderVersion));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verify = OpenWmsMasterData(); Assert.Empty(await verify.InboundFacts.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
    }

    private async Task<ExecutionHttpFixture> ExecutionHttpHostAsync()
    {
        var ids = await StockResourceHostAsync(operational: true, inboundApproval: true, inboundReceipt: true);
        await GrantInboundExecutionAsync();
        await using var owner = OpenWmsMasterData();
        var unit = new StockUnit(_tenant, Guid.NewGuid(), _user, Guid.NewGuid(), "EA", "Each", true, 0, _clock.GetUtcNow());
        var item = new StockItem(_tenant, Guid.NewGuid(), _user, Guid.NewGuid(), "ITEM", "Item", unit.Id, false, _clock.GetUtcNow());
        var container = new StockContainer(_tenant, Guid.NewGuid(), _user, Guid.NewGuid(), "PALLET", "Pallet", ids.Warehouse, _clock.GetUtcNow());
        var balance = new StockBalance(_tenant, ids.Warehouse, ids.Location, item.Id, "", container.Id, unit.Id, 0);
        await using (var transaction = await owner.Database.BeginTransactionAsync())
        {
            owner.AddRange(unit, item, container, balance, new StockDimensionRegistration(Guid.NewGuid(), _user, Guid.NewGuid(), balance, _clock.GetUtcNow()));
            await owner.SaveChangesAsync(); await transaction.CommitAsync();
        }
        owner.ChangeTracker.Clear();
        var line = Guid.NewGuid();
        var order = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), _user, ids.Warehouse,
            "EXECUTION-HTTP", [new(line, item.Id, unit.Id, "", 0, 5m)]);
        return new(order.Id!.Value, order.Version!, line, ids.Location, container.Id, balance.Id, balance.Version);
    }

    private async Task GrantInboundExecutionAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "INBOUND-EXECUTION",
        [
            new("wms.inbound.read", DataScope.Own, [], InboundReadFields.ToImmutableHashSet()),
            new("wms.inbound.approve", DataScope.Own, [], ImmutableHashSet.Create("State")),
            new("wms.inbound.receive", DataScope.Own, [], ImmutableHashSet.Create("LineId", "ExecutionId", "Quantity",
                "ContainerId", "ReceiptLocationId", "TargetLocationId", "BalanceId"))
        ]);
        platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id)); await platform.SaveChangesAsync();
    }
    private async Task<HttpResponseMessage> ExecutionPostAsync(Guid order, string action, Guid operation, object input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/wms/inbound/{order:D}/{action}") { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D")); request.Headers.Add("X-WES-Client", "ops-web");
        return await _client.SendAsync(request);
    }
    private sealed record ExecutionHttpFixture(Guid Order, string OrderVersion, Guid Line, Guid Location, Guid Container, Guid Balance, string BalanceVersion)
    {
        internal InboundReceiptInput Receipt(string version) => new(version, BalanceVersion, Line, Guid.NewGuid(), "5.000",
            Container, Location, Location, Balance);
    }
}
