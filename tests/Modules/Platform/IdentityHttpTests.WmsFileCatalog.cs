using System.Net;
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
    public async Task WmsFileCatalogHttpReturnsOnlyTheNamedCurrentEntityAttachments(bool location)
    {
        var (entity, file) = await WmsBusinessFileHostAsync(location);
        var collection = BusinessFileCollection(location, entity);

        using var response = await _client.GetAsync(collection);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        Assert.Equal(new[] { "decision", "limit", "offset", "rows" }, page.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(0, page.GetProperty("decision").GetInt32());
        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal(file, row.GetProperty("id").GetGuid());
        Assert.Equal(new[] { "byteCount", "contentType", "createdAt", "fileName", "id", "processingProblem", "state", "stored", "version" },
            row.EnumerateObject().Select(item => item.Name).Order());
    }

    [Theory]
    [InlineData("?offset=-1")]
    [InlineData("?offset=10001")]
    [InlineData("?offset=bad")]
    [InlineData("?offset=")]
    [InlineData("?limit=26")]
    [InlineData("?limit=101")]
    [InlineData("?state=-1")]
    [InlineData("?state=5")]
    [InlineData("?state=bad")]
    [InlineData("?state=")]
    [InlineData("?offset=0&offset=25")]
    [InlineData("?owner=wms-location")]
    [InlineData("?entityId=other")]
    public async Task WmsFileCatalogHttpRejectsInvalidPagingStateAndCallerSelectedPolicy(string query)
    {
        var (entity, _) = await WmsBusinessFileHostAsync(location: false);

        using var response = await _client.GetAsync(BusinessFileCollection(false, entity) + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("file")]
    [InlineData("session")]
    public async Task WmsFileCatalogHttpRevalidatesCurrentAuthorityOnEveryQuery(string boundary)
    {
        var (entity, _) = await WmsBusinessFileHostAsync(location: false);
        var route = BusinessFileCollection(false, entity);
        using var before = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await using (var change = Open())
        {
            if (boundary == "session")
            {
                foreach (var session in await change.Sessions.Where(item => item.UserId == _user).ToListAsync())
                    session.Revoke(_clock.GetUtcNow());
            }
            else
            {
                var role = await change.Roles.SingleAsync(item => item.Code == (boundary == "entity" ? "WMS" : "files"));
                var permission = boundary == "entity" ? "wms.warehouses.read" : "platform.files.read";
                role.Change(role.Version, true, role.Rules.Select(rule => rule.Permission == permission
                    ? rule with { Fields = rule.Fields.Remove(boundary == "entity" ? "Name" : "Version") } : rule));
            }
            await change.SaveChangesAsync();
        }

        using var denied = await _client.GetAsync(route);

        Assert.Equal(boundary == "session" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("attached.txt", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsFileCatalogHttpDoesNotMixEntityTypesOrUnknownEntities(bool otherType)
    {
        var (entity, _) = await WmsBusinessFileHostAsync(location: false);
        Guid target;
        if (otherType)
        {
            await using var wms = OpenWmsMasterData();
            target = (await wms.Locations.SingleAsync()).Id;
        }
        else target = Guid.NewGuid();

        using var response = await _client.GetAsync(BusinessFileCollection(otherType, target));

        Assert.Equal(otherType ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (otherType)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(json.RootElement.GetProperty("rows").EnumerateArray());
        }
    }

    [Theory]
    [InlineData(0, 25, "1", 1)]
    [InlineData(0, 50, "0", 0)]
    [InlineData(10000, 100, "", 0)]
    public async Task WmsFileCatalogHttpReturnsRequestedPageAndStateWithoutInventingTotals(int offset, int limit, string state, int count)
    {
        var (entity, _) = await WmsBusinessFileHostAsync(location: false);

        using var response = await _client.GetAsync(BusinessFileCollection(false, entity) +
            $"?offset={offset}&limit={limit}" + (state.Length == 0 ? "" : $"&state={state}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(offset, json.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(limit, json.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(count, json.RootElement.GetProperty("rows").GetArrayLength());
        Assert.False(json.RootElement.TryGetProperty("total", out _));
    }

    [Fact]
    public async Task WmsFileCatalogHttpMetadataDoesNotRequireOrGrantDownloadAndUsesActualFileCreatorScope()
    {
        var (entity, _) = await WmsBusinessFileHostAsync(location: false);
        await using (var change = Open())
        {
            var role = await change.Roles.SingleAsync(item => item.Code == "files");
            role.Change(role.Version, true, role.Rules.Where(rule => rule.Permission != "platform.files.download")
                .Select(rule => rule.Permission == "platform.files.read" ? rule with { Scope = DataScope.Own } : rule));
            await change.SaveChangesAsync();
        }

        using var allowed = await _client.GetAsync(BusinessFileCollection(false, entity));

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var json = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        var file = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray()).GetProperty("id").GetGuid();
        using var deniedDownload = await _client.GetAsync(BusinessFileRoute(false, entity, file) + "/content");
        Assert.Equal(HttpStatusCode.Forbidden, deniedDownload.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsFileCatalogHttpRemainsUnavailableWhenEitherDeploymentGateIsClosed(bool filesOnly)
    {
        if (filesOnly) await FileHostAsync();
        else await WmsHostAsync();

        using var response = await _client.GetAsync(BusinessFileCollection(false, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WmsFileCatalogHttpRejectsEmptyEntityIdentity()
    {
        await WmsBusinessFileHostAsync(location: false);

        using var response = await _client.GetAsync(BusinessFileCollection(false, Guid.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WmsFileCatalogHttpRequiresHttpsAuthenticationAndNeverCreatesThroughTheCollection()
    {
        var (entity, _) = await WmsBusinessFileHostAsync(location: false);
        var route = BusinessFileCollection(false, entity);
        var token = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync(route);
        _client.DefaultRequestHeaders.Authorization = token;

        using var plain = await _client.GetAsync("http://localhost" + route);
        using var post = await _client.PostAsync(route, null);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.FileBindingOperations.ToListAsync());
    }

    private static string BusinessFileCollection(bool location, Guid entity) =>
        $"/api/wms/{(location ? "locations" : "warehouses")}/{entity:D}/files";
}
