using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task OutboxFilteredHttpAppliesStatusAndHalfOpenUtcBeforePaging()
    {
        await GrantPlatformAsync();
        await SignInAsync();
        Guid included;
        await using (var seed = Open())
        {
            var first = new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow().AddSeconds(5));
            included = first.Id;
            seed.Audit.AddRange(first, new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow().AddSeconds(6)));
            await seed.SaveChangesAsync();
        }
        var from = Uri.EscapeDataString(_clock.GetUtcNow().AddSeconds(5).ToString("O"));
        var before = Uri.EscapeDataString(_clock.GetUtcNow().AddSeconds(6).ToString("O"));
        using var filtered = await _client.GetAsync($"/api/platform/messages?status=0&createdFrom={from}&createdBefore={before}&limit=1");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var body = await filtered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(included, Assert.Single(body.GetProperty("rows").EnumerateArray()).GetProperty("eventId").GetGuid());
        using var empty = await _client.GetAsync($"/api/platform/messages?status=3&createdFrom={from}&createdBefore={before}");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
    }

    [Theory]
    [InlineData("status=-1")]
    [InlineData("status=4")]
    [InlineData("status=unknown")]
    [InlineData("createdFrom=invalid")]
    [InlineData("createdBefore=invalid")]
    [InlineData("createdFrom=2026-10-24T00%3A00%3A00%2B08%3A00")]
    [InlineData("createdBefore=0001-01-01T00%3A00%3A00Z")]
    [InlineData("createdFrom=2026-10-24T00%3A00%3A00Z&createdBefore=2026-10-24T00%3A00%3A00Z")]
    [InlineData("createdFrom=2026-10-25T00%3A00%3A00Z&createdBefore=2026-10-24T00%3A00%3A00Z")]
    public async Task OutboxFilteredHttpRejectsInvalidInput(string query)
    {
        await GrantPlatformAsync();
        await SignInAsync();
        using var response = await _client.GetAsync($"/api/platform/messages?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
