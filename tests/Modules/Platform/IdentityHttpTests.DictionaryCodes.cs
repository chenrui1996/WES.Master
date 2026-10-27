using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("1Ready")]
    public async Task NumericDictionaryHttpPreservesCodesAndVersionedTranslation(string code)
    {
        await SignInAsync();
        await GrantPlatformAsync();
        using var create = await _client.PostAsJsonAsync("/api/platform/dictionary", new { category = "task", code, languageKey = "task.zero" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var result = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = result.GetProperty("id").GetGuid();
        var version = result.GetProperty("version").GetString();
        using var translation = await _client.PutAsJsonAsync($"/api/platform/dictionary/{id:D}/translation",
            new { expectedVersion = version, language = "zh-CN", label = "零状态" });
        Assert.Equal(HttpStatusCode.OK, translation.StatusCode);
        using var read = await _client.GetAsync("/api/platform/dictionary?category=task&language=de-DE");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var row = Assert.Single((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        Assert.Equal(code, row.GetProperty("code").GetString());
        Assert.Equal("零状态", row.GetProperty("label").GetString());
        Assert.Equal("zh-CN", row.GetProperty("resolvedLanguage").GetString());
        using var duplicate = await _client.PostAsJsonAsync("/api/platform/dictionary", new { category = "task", code, languageKey = "task.zero" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using var verify = Open();
        Assert.Equal(code, (await verify.DictionaryEntries.SingleAsync()).Code);
        Assert.Equal(2, await verify.Audit.CountAsync(item => item.Action == PlatformAuditAction.DictionaryChanged));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0 ")]
    [InlineData("0\n")]
    [InlineData("<script>")]
    [InlineData("０")]
    public async Task NumericDictionaryHttpRejectsInvalidCodesWithoutPersistence(string code)
    {
        await SignInAsync();
        await GrantPlatformAsync();
        using var response = await _client.PostAsJsonAsync("/api/platform/dictionary", new { category = "task", code, languageKey = "task.zero" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.DictionaryEntries.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task NumericDictionaryHttpDoesNotGrantWriteAuthority()
    {
        await SignInAsync();
        using var response = await _client.PostAsJsonAsync("/api/platform/dictionary",
            new { category = "task", code = "0", languageKey = "task.zero" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.DictionaryEntries.ToListAsync());
    }
}
