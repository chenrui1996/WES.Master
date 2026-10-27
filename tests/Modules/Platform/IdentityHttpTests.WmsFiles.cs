using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Platform;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsBusinessFilesAreNotExposedWithoutFileDeployment(bool location)
    {
        var ids = await WmsHostAsync();

        using var response = await _client.GetAsync(BusinessFileRoute(location, location ? ids.Location : ids.Warehouse, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsBusinessFilesAreNotExposedByFileDeploymentAlone()
    {
        await FileHostAsync();

        using var response = await _client.GetAsync(BusinessFileRoute(false, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsBusinessFilesReturnExactMetadataAndAuditedPrivateBytes(bool location)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location);
        var route = BusinessFileRoute(location, entity, file);

        using var read = await _client.GetAsync(route);
        using var content = await _client.GetAsync(route + "/content");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        var projection = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("file");
        string[] fields = ["id", "fileName", "contentType", "byteCount", "state", "stored", "version", "createdAt", "processingProblem"];
        Assert.Equal(fields.Order(StringComparer.Ordinal), projection.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(file, projection.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("no-store", content.Headers.CacheControl?.ToString());
        Assert.Equal("attachment", content.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(content.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("safe"u8.ToArray(), await content.Content.ReadAsByteArrayAsync());
        using var staging = await _client.GetAsync($"/api/platform/files/staging/{file:D}/content");
        Assert.Equal(HttpStatusCode.Forbidden, staging.StatusCode);
        await using var verify = Open();
        var audit = Assert.Single(await verify.Audit.Where(item => item.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
        Assert.Equal(_user, audit.ActorId);
        Assert.Equal(file, audit.TargetId);
    }

    [Theory]
    [InlineData(false, "entity")]
    [InlineData(true, "entity")]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "binding")]
    [InlineData(true, "binding")]
    public async Task WmsBusinessFilesRecheckBothOwnersAndExactBindingOnEveryRequest(bool location, string revoked)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location);
        var route = BusinessFileRoute(location, entity, file);
        using var first = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using (var change = Open())
        {
            if (revoked == "binding")
            {
                change.FileReferences.Remove(await change.FileReferences.SingleAsync());
                (await change.Files.SingleAsync()).RemoveReference(_clock.GetUtcNow());
            }
            else
            {
                var role = await change.Roles.SingleAsync(item => item.Code == (revoked == "entity" ? "WMS" : "files"));
                role.Change(role.Version, true, role.Rules.Select(rule => rule with
                { Fields = rule.Fields.Remove(revoked == "entity" ? "Name" : "Content") }));
            }
            await change.SaveChangesAsync();
        }

        using var response = await _client.GetAsync(route + "/content");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("safe", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var verify = Open();
        Assert.Empty(await verify.Audit.Where(item => item.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsBusinessFilesCannotSelectAnotherEntityOrOwnerUsingAFileGrant(bool location)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location);

        using var wrongOwner = await _client.GetAsync(BusinessFileRoute(!location, entity, file) + "/content");
        using var wrongEntity = await _client.GetAsync(BusinessFileRoute(location, Guid.NewGuid(), file) + "/content");

        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongEntity.StatusCode);
    }

    [Fact]
    public async Task WmsBusinessFilesRequireAuthenticatedHttpsAndNonemptyIdentities()
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location: false);
        var route = BusinessFileRoute(false, entity, file);
        var token = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync(route);
        _client.DefaultRequestHeaders.Authorization = token;

        using var plain = await _client.GetAsync("http://localhost" + route);
        using var emptyEntity = await _client.GetAsync(BusinessFileRoute(false, Guid.Empty, file));
        using var emptyFile = await _client.GetAsync(BusinessFileRoute(false, entity, Guid.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyEntity.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyFile.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsBusinessFilesDoNotTrustAllFileGrantsForMissingOrForeignEntities(bool foreign)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location: false);
        var target = Guid.NewGuid();
        if (foreign)
        {
            await using var wms = OpenWmsMasterData(_wmsForeignTenant);
            var warehouse = new Wes.Wms.Domain.Warehouse(_wmsForeignTenant, "FOREIGN", "Foreign attachment", _clock.GetUtcNow());
            target = warehouse.Id;
            wms.Warehouses.Add(warehouse);
            wms.MasterDataChanges.Add(new Wes.Wms.Domain.MasterDataChange(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), warehouse));
            await wms.SaveChangesAsync();
        }
        await using (var change = Open())
        {
            var role = await change.Roles.SingleAsync(item => item.Code == "files");
            role.Change(role.Version, true, role.Rules.Select(rule => rule with { Scope = DataScope.All }));
            change.FileReferences.Remove(await change.FileReferences.SingleAsync());
            change.FileReferences.Add(new(_tenant, file, "wms-warehouse", target));
            var bound = await change.Files.SingleAsync();
            bound.RemoveReference(_clock.GetUtcNow());
            bound.AddReference(_clock.GetUtcNow());
            await change.SaveChangesAsync();
        }

        using var response = await _client.GetAsync(BusinessFileRoute(false, target, file) + "/content");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("Foreign", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var verify = Open();
        Assert.Empty(await verify.Audit.Where(item => item.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsBusinessFilesNeverReturnStorageFailureAsContent(bool missing)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location: false);
        var path = Path.Combine(_filesRoot!, _tenant.ToString("N"), file.ToString("N"));
        if (missing) File.Delete(path);
        else await File.WriteAllBytesAsync(path, "evil"u8.ToArray());

        using var response = await _client.GetAsync(BusinessFileRoute(false, entity, file) + "/content");

        Assert.Equal(missing ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_filesRoot!, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil", text, StringComparison.Ordinal);
        await using var verify = Open();
        Assert.Empty(await verify.Audit.Where(item => item.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task WmsBusinessFilesDoNotExposeBindingOrLifecycleWrites(string method)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location: false);
        using var request = new HttpRequestMessage(new HttpMethod(method), BusinessFileRoute(false, entity, file));

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await using var verify = Open();
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Equal(1, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Empty(await verify.Audit.Where(item => item.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
    }

    private static string BusinessFileRoute(bool location, Guid entity, Guid file) =>
        $"/api/wms/{(location ? "locations" : "warehouses")}/{entity:D}/files/{file:D}";

    private async Task<(Guid Entity, Guid File)> WmsBusinessFileHostAsync(bool location)
    {
        await FileHostAsync();
        var ids = await WmsHostAsync(filesRoot: _filesRoot);
        var entity = location ? ids.Location : ids.Warehouse;
        var file = new QuarantinedFile(_tenant, _user, Guid.NewGuid(), "attached.txt", "text/plain", 4,
            Convert.ToHexString(SHA256.HashData("safe"u8)), _clock.GetUtcNow().AddDays(-1));
        var adapters = _factory.Services.GetRequiredService<PlatformFileAdapters>();
        await adapters.Storage.WriteAsync(_tenant, file.Id, "safe"u8.ToArray(), CancellationToken.None);
        file.ConfirmStored(file.Sha256, 4, _clock.GetUtcNow());
        var attempt = file.BeginScan(_clock.GetUtcNow());
        file.CompleteScan(attempt, FileScanVerdict.Clean, file.Sha256, 4, _clock.GetUtcNow());
        file.AddReference(_clock.GetUtcNow());
        await using var seed = Open();
        seed.Files.Add(file);
        seed.FileReferences.Add(new(_tenant, file.Id, location ? "wms-location" : "wms-warehouse", entity));
        await seed.SaveChangesAsync();
        return (entity, file.Id);
    }
}
