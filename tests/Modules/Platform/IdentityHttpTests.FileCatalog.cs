using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task FileCatalog_ReturnsAuthorizedSafeMetadataWithoutPhysicalReadsOrNewAudit()
    {
        await FileHostAsync();
        using var uploaded = await _client.SendAsync(FileRequest("safe"u8.ToArray(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var response = await _client.GetAsync("/api/platform/files/staging?offset=0&limit=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("decision").GetInt32());
        Assert.Equal(25, json.GetProperty("limit").GetInt32());
        Assert.Equal(0, json.GetProperty("offset").GetInt32());
        var row = Assert.Single(json.GetProperty("rows").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.Equal("note.txt", row.GetProperty("fileName").GetString());
        Assert.Equal(0, row.GetProperty("state").GetInt32());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("sha256", json.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_filesRoot!, json.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var verify = Open();
        Assert.Equal(2, await verify.Audit.CountAsync());
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=26")]
    [InlineData("state=5")]
    [InlineData("state=-1")]
    [InlineData("state=Available")]
    public async Task FileCatalog_InvalidBoundsNeverBecomeDefaultQueries(string query)
    {
        await FileHostAsync();
        using var response = await _client.GetAsync($"/api/platform/files/staging?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(DataScope.Own, 1)]
    [InlineData(DataScope.Departments, 2)]
    [InlineData(DataScope.All, 3)]
    public async Task FileCatalog_FiltersByCurrentCreatorScopeBeforePaging(DataScope scope, int expected)
    {
        await FileHostAsync();
        await SeedCatalogAsync(scope);
        using var response = await _client.GetAsync("/api/platform/files/staging?limit=25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows");
        Assert.Equal(expected, rows.GetArrayLength());
        Assert.Contains(rows.EnumerateArray(), row => row.GetProperty("fileName").GetString() == "own.txt");
        if (scope == DataScope.Own) Assert.Equal("own.txt", rows[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task FileCatalog_MissingMetadataFieldIsForbiddenNotAnEmptySuccess()
    {
        await FileHostAsync();
        await SeedCatalogAsync(DataScope.All, missingField: true);
        using var response = await _client.GetAsync("/api/platform/files/staging");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FileCatalog_StateFilterAndEmptyLaterPagePreserveRealtimeSemantics()
    {
        await FileHostAsync();
        await SeedCatalogAsync(DataScope.All);
        using var emptyState = await _client.GetAsync("/api/platform/files/staging?state=1");
        Assert.Equal(HttpStatusCode.OK, emptyState.StatusCode);
        Assert.Empty((await emptyState.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        using var later = await _client.GetAsync("/api/platform/files/staging?offset=25&limit=25");
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Empty((await later.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
    }

    private async Task SeedCatalogAsync(DataScope scope, bool missingField = false)
    {
        await using var seed = Open();
        var actor = await seed.DirectoryUsers.SingleAsync(user => user.Id == _user);
        var outside = new DirectoryDepartment(_tenant, "catalog-outside", "Outside");
        seed.Departments.Add(outside);
        var insideCreator = new DirectoryUser(_tenant, Guid.NewGuid(), "CATALOG-INSIDE", "Inside", actor.DepartmentId);
        var outsideCreator = new DirectoryUser(_tenant, Guid.NewGuid(), "CATALOG-OUTSIDE", "Outside", outside.Id);
        seed.DirectoryUsers.AddRange(insideCreator, outsideCreator);
        var fields = ImmutableHashSet.Create("Id", "FileName", "ContentType", "ByteCount", "State", "Stored", "Version", "CreatedAt", "ProcessingProblem");
        if (missingField) fields = fields.Remove("FileName");
        var role = await seed.Roles.SingleAsync(item => item.Code == "files");
        role.Change(role.Version, true, [new("platform.files.read", scope,
            scope == DataScope.Departments ? ImmutableHashSet.Create(actor.DepartmentId) : ImmutableHashSet<Guid>.Empty, fields)]);
        foreach (var (creator, name) in new[] { (_user, "own.txt"), (insideCreator.Id, "inside.txt"), (outsideCreator.Id, "outside.txt") })
            seed.Files.Add(new QuarantinedFile(_tenant, creator, Guid.NewGuid(), name, "text/plain", 4,
                Convert.ToHexString(SHA256.HashData("safe"u8)), _clock.GetUtcNow()));
        await seed.SaveChangesAsync();
    }
}
