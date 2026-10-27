using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsFileBindingHttpCommitsReplaysAndUnbindsAtomically(bool location)
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location);
        var route = BusinessFileRoute(location, entity, file);
        var operation = Guid.NewGuid();
        using var bind = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));
        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);
        Assert.Equal(route, bind.Headers.Location?.OriginalString);
        var receipt = await BindingReceiptAsync(bind, file, attached: true);
        using var replay = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await BindingReceiptAsync(replay, file, attached: true));
        using var read = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var detachOperation = Guid.NewGuid();
        using var detach = await _client.SendAsync(BindingRequest(HttpMethod.Delete, route, receipt, detachOperation));
        Assert.Equal(HttpStatusCode.OK, detach.StatusCode);
        var detached = await BindingReceiptAsync(detach, file, attached: false);
        using var detachReplay = await _client.SendAsync(BindingRequest(HttpMethod.Delete, route, receipt, detachOperation));
        Assert.Equal(HttpStatusCode.OK, detachReplay.StatusCode);
        Assert.Equal(detached, await BindingReceiptAsync(detachReplay, file, attached: false));
        using var noLongerBound = await _client.GetAsync(route);
        using var staging = await _client.GetAsync($"/api/platform/files/staging/{file:D}");
        Assert.Equal(HttpStatusCode.Forbidden, noLongerBound.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staging.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.FileReferences.ToListAsync());
        Assert.Equal(0, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Equal(2, await verify.FileBindingOperations.CountAsync());
        Assert.Equal(new[] { PlatformAuditAction.FileAttached, PlatformAuditAction.FileDetached },
            (await verify.Audit.Where(item => item.TargetId == file).OrderBy(item => item.Action).ToListAsync()).Select(item => item.Action));
    }

    [Theory]
    [InlineData("missing", 428)]
    [InlineData("null", 428)]
    [InlineData("invalid", 400)]
    [InlineData("owner", 400)]
    [InlineData("actor", 400)]
    [InlineData("entity", 400)]
    [InlineData("array", 400)]
    [InlineData("stale", 412)]
    public async Task WmsFileBindingHttpRequiresExactBodyAndVersion(string boundary, int expected)
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        var raw = boundary switch
        {
            "missing" => "{}",
            "null" => "{\"expectedVersion\":null}",
            "invalid" => "{\"expectedVersion\":\"invalid\"}",
            "array" => "[]",
            "owner" => JsonSerializer.Serialize(new { expectedVersion = version, owner = "other" }),
            "actor" => JsonSerializer.Serialize(new { expectedVersion = version, actorId = Guid.NewGuid() }),
            "entity" => JsonSerializer.Serialize(new { expectedVersion = version, entityId = Guid.NewGuid() }),
            _ => JsonSerializer.Serialize(new { expectedVersion = Guid.NewGuid().ToString("D") })
        };
        using var request = BindingRequest(HttpMethod.Put, BusinessFileRoute(false, entity, file), version);
        request.Content!.Dispose();
        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        await AssertNoHttpBindingEffectAsync();
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("empty-key")]
    [InlineData("invalid-key")]
    [InlineData("multiple-key")]
    [InlineData("missing-client")]
    [InlineData("bad-client")]
    [InlineData("multiple-client")]
    public async Task WmsFileBindingHttpRequiresSingleOperationAndReviewedClient(string boundary)
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        using var request = BindingRequest(HttpMethod.Put, BusinessFileRoute(false, entity, file), version);
        request.Headers.Remove("Idempotency-Key");
        if (boundary != "missing-key")
            request.Headers.TryAddWithoutValidation("Idempotency-Key", boundary switch
            {
                "empty-key" => Guid.Empty.ToString("D"),
                "invalid-key" => "invalid",
                "multiple-key" => $"{Guid.NewGuid():D},{Guid.NewGuid():D}",
                _ => Guid.NewGuid().ToString("D")
            });
        if (boundary.EndsWith("client", StringComparison.Ordinal))
        {
            _client.DefaultRequestHeaders.Remove("X-WES-Client");
            if (boundary != "missing-client")
                request.Headers.TryAddWithoutValidation("X-WES-Client", boundary == "bad-client" ? "arbitrary" : "ops-web,pda");
        }

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoHttpBindingEffectAsync();
    }

    [Theory]
    [InlineData("entity-attach")]
    [InlineData("file-attach")]
    [InlineData("entity-read")]
    [InlineData("entity-detach")]
    [InlineData("file-detach")]
    public async Task WmsFileBindingHttpRequiresExactCurrentWriteActionOnBothOwners(string revoked)
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        var route = BusinessFileRoute(false, entity, file);
        var detach = revoked.EndsWith("detach", StringComparison.Ordinal);
        if (detach)
        {
            using var bind = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version));
            Assert.Equal(HttpStatusCode.Created, bind.StatusCode);
            version = await BindingReceiptAsync(bind, file, attached: true);
        }
        await using (var change = Open())
        {
            var role = await change.Roles.SingleAsync(item => item.Code == (revoked.StartsWith("entity", StringComparison.Ordinal) ? "WMS" : "files"));
            var field = revoked == "entity-read" ? "Name" : detach ? "Detach" : "Attach";
            role.Change(role.Version, true, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove(field) }));
            await change.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(BindingRequest(detach ? HttpMethod.Delete : HttpMethod.Put, route, version));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verify = Open();
        Assert.Equal(detach ? 1 : 0, await verify.FileReferences.CountAsync());
        Assert.Equal(detach ? 1 : 0, await verify.FileBindingOperations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsFileBindingHttpConcurrentRequestsPreserveExactlyOneEffect(bool sameOperation)
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        var route = BusinessFileRoute(false, entity, file);
        var operation = Guid.NewGuid();
        using var first = BindingRequest(HttpMethod.Put, route, version, operation);
        using var second = BindingRequest(HttpMethod.Put, route, version, sameOperation ? operation : Guid.NewGuid());

        var responses = await Task.WhenAll(_client.SendAsync(first), _client.SendAsync(second));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == (sameOperation ? HttpStatusCode.OK : HttpStatusCode.PreconditionFailed));
        }
        finally { foreach (var response in responses) response.Dispose(); }

        await using var verify = Open();
        Assert.Equal(1, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
        Assert.Single(await verify.Audit.Where(item => item.TargetId == file).ToListAsync());
    }

    [Fact]
    public async Task WmsFileBindingHttpChangedOriginalVersionConflictsAndRevokedReplayIsForbidden()
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        var route = BusinessFileRoute(false, entity, file);
        var operation = Guid.NewGuid();
        using var bind = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));
        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);
        var current = await BindingReceiptAsync(bind, file, attached: true);
        using var changed = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, current, operation));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        await using (var change = Open())
        {
            var role = await change.Roles.SingleAsync(item => item.Code == "WMS");
            role.Change(role.Version, true, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Attach") }));
            await change.SaveChangesAsync();
        }

        using var replay = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        await using var verify = Open();
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
    }

    [Fact]
    public async Task WmsFileBindingHttpRequiresAuthenticatedHttpsAndNonemptyTargets()
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        var route = BusinessFileRoute(false, entity, file);
        var token = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version));
        _client.DefaultRequestHeaders.Authorization = token;

        using var plain = await _client.SendAsync(BindingRequest(HttpMethod.Put, "http://localhost" + route, version));
        using var empty = await _client.SendAsync(BindingRequest(HttpMethod.Put, BusinessFileRoute(false, Guid.Empty, file), version));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        await AssertNoHttpBindingEffectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsFileBindingHttpUsesPersistedEntityAndFileCreatorsRatherThanRequestActor(bool entityCreator)
    {
        var creator = Guid.NewGuid();
        await using (var seed = Open())
        {
            seed.DirectoryUsers.Add(new(_tenant, creator, "OTHER", "Other creator", (await seed.Departments.SingleAsync()).Id));
            await seed.SaveChangesAsync();
        }
        var (entity, file, version) = await WmsFileBindingHostAsync(false, entityCreator ? null : creator);
        if (entityCreator)
        {
            await using var wms = OpenWmsMasterData();
            var warehouse = new Wes.Wms.Domain.Warehouse(_tenant, "OTHER", "Other creator warehouse", _clock.GetUtcNow());
            wms.Warehouses.Add(warehouse);
            wms.MasterDataChanges.Add(new(Guid.NewGuid(), creator, Guid.NewGuid(), warehouse));
            await wms.SaveChangesAsync();
            entity = warehouse.Id;
            await using var platform = Open();
            var role = await platform.Roles.SingleAsync(item => item.Code == "WMS");
            role.Change(role.Version, true, role.Rules.Select(rule => rule.Permission.EndsWith(".read", StringComparison.Ordinal)
                ? rule with { Scope = DataScope.All } : rule));
            await platform.SaveChangesAsync();
        }
        var route = BusinessFileRoute(false, entity, file);
        var operation = Guid.NewGuid();
        using var denied = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using (var change = Open())
        {
            var role = await change.Roles.SingleAsync(item => item.Code == (entityCreator ? "WMS" : "files"));
            role.Change(role.Version, true, role.Rules.Select(rule => rule.Permission.EndsWith(".write", StringComparison.Ordinal)
                ? rule with { Scope = DataScope.All } : rule));
            await change.SaveChangesAsync();
        }

        using var allowed = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        if (!entityCreator)
        {
            using var read = await _client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        }
        await using var verify = Open();
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
    }

    [Fact]
    public async Task WmsFileBindingHttpWritePermissionDoesNotGrantFileRead()
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        await using (var change = Open())
        {
            var role = await change.Roles.SingleAsync(item => item.Code == "files");
            role.Change(role.Version, true, role.Rules.Where(rule => rule.Permission != "platform.files.read"));
            await change.SaveChangesAsync();
        }
        var route = BusinessFileRoute(false, entity, file);

        using var bind = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version));
        using var read = await _client.GetAsync(route);
        using var content = await _client.GetAsync(route + "/content");

        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, content.StatusCode);
        await using var verify = Open();
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Empty(await verify.Audit.Where(item => item.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
    }

    [Fact]
    public async Task WmsFileBindingHttpProtectsCleanupUntilUnboundAndRetainsReceiptAfterPhysicalDeletion()
    {
        var (entity, file, version) = await WmsFileBindingHostAsync(location: false);
        var route = BusinessFileRoute(false, entity, file);
        var operation = Guid.NewGuid();
        using var bind = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));
        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);
        var bound = await BindingReceiptAsync(bind, file, attached: true);
        var staging = $"/api/platform/files/staging/{file:D}";
        using var forbiddenDelete = await _client.SendAsync(BindingRequest(HttpMethod.Delete, staging, bound));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        var path = Path.Combine(_filesRoot!, _tenant.ToString("N"), file.ToString("N"));
        Assert.True(File.Exists(path));
        using var detach = await _client.SendAsync(BindingRequest(HttpMethod.Delete, route, bound));
        Assert.Equal(HttpStatusCode.OK, detach.StatusCode);
        var detached = await BindingReceiptAsync(detach, file, attached: false);

        using var deleted = await _client.SendAsync(BindingRequest(HttpMethod.Delete, staging, detached));
        using var replay = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(File.Exists(path));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(bound, await BindingReceiptAsync(replay, file, attached: true));
        await using var verify = Open();
        Assert.Equal(QuarantinedFileState.Deleted, (await verify.Files.SingleAsync()).State);
        Assert.Empty(await verify.FileReferences.ToListAsync());
        Assert.Equal(2, await verify.FileBindingOperations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsFileBindingHttpRequiresAllExistingSourceEntitiesAndRejectsUnregisteredSourceOwners(bool unknownOwner)
    {
        var (source, file) = await WmsBusinessFileHostAsync(location: false);
        Guid target;
        await using (var wms = OpenWmsMasterData()) target = (await wms.Locations.SingleAsync()).Id;
        string version;
        await using (var change = Open())
        {
            var files = await change.Roles.SingleAsync(item => item.Code == "files");
            files.Change(files.Version, true, files.Rules.Select(rule => rule.Permission == "platform.files.write"
                ? rule with { Fields = rule.Fields.Add("Attach") } : rule));
            var wms = await change.Roles.SingleAsync(item => item.Code == "WMS");
            wms.Change(wms.Version, true, wms.Rules.Where(rule => unknownOwner || rule.Permission != "wms.warehouses.read")
                .Append(new PermissionGrant("wms.locations.write", DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Attach"))));
            var bound = await change.Files.SingleAsync();
            if (unknownOwner)
            {
                change.FileReferences.Remove(await change.FileReferences.SingleAsync());
                change.FileReferences.Add(new(_tenant, file, "Wms", source));
                bound.RemoveReference(_clock.GetUtcNow());
                bound.AddReference(_clock.GetUtcNow());
            }
            await change.SaveChangesAsync();
            version = bound.Version;
        }
        var route = BusinessFileRoute(true, target, file);
        var operation = Guid.NewGuid();

        using var denied = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using (var verify = Open())
        {
            Assert.Equal(1, (await verify.Files.SingleAsync()).ReferenceCount);
            Assert.Single(await verify.FileReferences.ToListAsync());
            Assert.Empty(await verify.FileBindingOperations.ToListAsync());
        }
        if (unknownOwner) return;
        await using (var restore = Open())
        {
            var role = await restore.Roles.SingleAsync(item => item.Code == "WMS");
            role.Change(role.Version, true, role.Rules.Append(new PermissionGrant("wms.warehouses.read", DataScope.Own,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "Code", "Name", "Enabled", "Version", "CreatedAt"))));
            await restore.SaveChangesAsync();
        }
        using var allowed = await _client.SendAsync(BindingRequest(HttpMethod.Put, route, version, operation));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        await using var final = Open();
        Assert.Equal(2, (await final.Files.SingleAsync()).ReferenceCount);
        Assert.Equal(2, await final.FileReferences.CountAsync());
        Assert.Single(await final.FileBindingOperations.ToListAsync());
    }

    private static HttpRequestMessage BindingRequest(HttpMethod method, string route, string? version, Guid? operation = null)
    {
        var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(new { expectedVersion = version }) };
        request.Headers.Add("Idempotency-Key", (operation ?? Guid.NewGuid()).ToString("D"));
        return request;
    }

    private static async Task<string> BindingReceiptAsync(HttpResponseMessage response, Guid file, bool attached)
    {
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "attached", "id", "version" }, receipt.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(file, receipt.GetProperty("id").GetGuid());
        Assert.Equal(attached, receipt.GetProperty("attached").GetBoolean());
        var version = receipt.GetProperty("version").GetString();
        Assert.True(Guid.TryParseExact(version, "D", out var id) && id != Guid.Empty);
        return Assert.IsType<string>(version);
    }

    private async Task AssertNoHttpBindingEffectAsync()
    {
        await using var verify = Open();
        Assert.Equal(0, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Empty(await verify.FileReferences.ToListAsync());
        Assert.Empty(await verify.FileBindingOperations.ToListAsync());
        Assert.Empty(await verify.Audit.Where(item => item.Action == PlatformAuditAction.FileAttached
            || item.Action == PlatformAuditAction.FileDetached).ToListAsync());
    }

    private async Task<(Guid Entity, Guid File, string Version)> WmsFileBindingHostAsync(bool location, Guid? fileCreator = null)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location, bound: false, fileCreator);
        await using var seed = Open();
        var files = await seed.Roles.SingleAsync(item => item.Code == "files");
        files.Change(files.Version, true, files.Rules.Select(rule => rule.Permission == "platform.files.write"
            ? rule with { Fields = rule.Fields.Union(["Attach", "Detach"]) } : rule));
        var wms = await seed.Roles.SingleAsync(item => item.Code == "WMS");
        wms.Change(wms.Version, true, wms.Rules.Append(new PermissionGrant(location ? "wms.locations.write" : "wms.warehouses.write",
            DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Attach", "Detach"))));
        await seed.SaveChangesAsync();
        return (entity, file, (await seed.Files.AsNoTracking().SingleAsync()).Version);
    }
}
