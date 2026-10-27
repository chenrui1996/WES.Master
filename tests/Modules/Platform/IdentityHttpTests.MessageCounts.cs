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
    private static readonly string[] SummaryFields =
        ["Owner", "ObservedAt", "PendingCount", "LeasedCount", "PublishedCount", "QuarantinedCount", "InboxReceiptCount"];

    [Fact]
    public async Task MessageCountsHttpRequiresExplicitCurrentFieldsAndReturnsOnlyOwnMetadata()
    {
        using (var anonymous = await _client.GetAsync("/api/platform/messages/summary"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await GrantPlatformAsync();
        await SignInAsync();
        using (var rowsOnly = await _client.GetAsync("/api/platform/messages/summary"))
            Assert.Equal(HttpStatusCode.Forbidden, rowsOnly.StatusCode);
        await GrantSummaryAsync();
        await using (var seed = Open())
        {
            seed.Audit.Add(new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
        }
        using var response = await _client.GetAsync("/api/platform/messages/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("authorized").GetBoolean());
        var summary = body.GetProperty("summary");
        Assert.Equal(7, summary.EnumerateObject().Count());
        Assert.Equal("Platform", summary.GetProperty("owner").GetString());
        Assert.Equal(1, summary.GetProperty("pendingCount").GetInt64());
        Assert.Equal(0, summary.GetProperty("inboxReceiptCount").GetInt64());
        Assert.DoesNotContain("payload", summary.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await using (var revoke = Open())
        {
            var role = await revoke.Roles.SingleAsync(item => item.Code == "fixture-summary");
            role.Change(role.Version, false, role.Rules);
            await revoke.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync("/api/platform/messages/summary");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("ObservedAt")]
    [InlineData("PendingCount")]
    [InlineData("LeasedCount")]
    [InlineData("PublishedCount")]
    [InlineData("QuarantinedCount")]
    [InlineData("InboxReceiptCount")]
    public async Task MessageCountsHttpDoesNotReturnPartialProjection(string missing)
    {
        await GrantSummaryAsync(missing);
        await SignInAsync();
        using var response = await _client.GetAsync("/api/platform/messages/summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task GrantSummaryAsync(string? missing = null)
    {
        await using var writer = Open();
        var role = new AccessRole(_tenant, "fixture-summary",
            [new("platform.messages.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                SummaryFields.Where(field => field != missing).ToImmutableHashSet(StringComparer.Ordinal))]);
        writer.Roles.Add(role);
        writer.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
        await writer.SaveChangesAsync();
    }
}
