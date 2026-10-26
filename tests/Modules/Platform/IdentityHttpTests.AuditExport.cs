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
    [Fact]
    public async Task AuditExport_DeliversOnlySelectedFixedMembersAfterOneDurableAudit()
    {
        await SignInAsync(); await GrantPlatformAsync(); await GrantAuditExportAsync();
        var snapshot = await CreateExportSnapshotAsync(105);
        await using (var insert = Open())
        {
            insert.Audit.Add(new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.SettingChanged, null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow()));
            await insert.SaveChangesAsync();
        }
        var operation = Guid.NewGuid();
        using var first = await ExportAuditAsync(snapshot, operation, ["Id", "OccurredAt", "Action"]);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("text/csv", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", first.Content.Headers.ContentDisposition?.DispositionType);
        Assert.True(first.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", first.Headers.GetValues("X-Content-Type-Options").Single());
        var csv = await first.Content.ReadAsStringAsync();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(106, lines.Length);
        Assert.Equal("\"Id\",\"OccurredAt\",\"Action\"", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.EndsWith(",\"RoleChanged\"", line));
        Assert.DoesNotContain("Payload", csv);
        using var repeat = await ExportAuditAsync(snapshot, operation, ["Id", "OccurredAt", "Action"]);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(csv, await repeat.Content.ReadAsStringAsync());
        await using var verify = Open();
        Assert.Equal(1, await verify.Audit.CountAsync(item => item.TargetId == snapshot));
    }

    [Fact]
    public async Task AuditExport_ReadPermissionAloneCannotDeliverOrAudit()
    {
        await SignInAsync(); await GrantPlatformAsync();
        var snapshot = await CreateExportSnapshotAsync(1);
        using var denied = await ExportAuditAsync(snapshot, Guid.NewGuid(), ["Id", "OccurredAt"]);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var verify = Open();
        Assert.False(await verify.Audit.AnyAsync(item => item.TargetId == snapshot));
    }

    [Theory]
    [InlineData("Payload")]
    [InlineData("Id")]
    [InlineData("BeforeVersion")]
    public async Task AuditExport_InvalidOrUnselectedIdentityColumnsAreRejected(string column)
    {
        await SignInAsync(); await GrantPlatformAsync(); await GrantAuditExportAsync();
        var snapshot = await CreateExportSnapshotAsync(1);
        using var denied = await ExportAuditAsync(snapshot, Guid.NewGuid(), ["Id", column]);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
    }

    [Fact]
    public async Task AuditExport_OperationCannotChangeColumnsAndExpiresEvenAfterSuccess()
    {
        await SignInAsync(); await GrantPlatformAsync(); await GrantAuditExportAsync();
        var snapshot = await CreateExportSnapshotAsync(1);
        var operation = Guid.NewGuid();
        using var initial = await ExportAuditAsync(snapshot, operation, ["Id", "OccurredAt"]);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var conflict = await ExportAuditAsync(snapshot, operation, ["Id", "OccurredAt", "Action"]);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(600));
        using var expired = await ExportAuditAsync(snapshot, operation, ["Id", "OccurredAt"]);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        await using var verify = Open();
        Assert.Equal(1, await verify.Audit.CountAsync(item => item.TargetId == snapshot));
    }

    [Fact]
    public async Task AuditExport_AnExportGrantCannotDeliverAnUngrantedFieldOrMissingOperation()
    {
        await SignInAsync(); await GrantPlatformAsync(); await GrantAuditExportAsync();
        var snapshot = await CreateExportSnapshotAsync(1);
        using var hidden = await ExportAuditAsync(snapshot, Guid.NewGuid(), ["Id", "OccurredAt", "ActorId"]);
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        using var unkeyed = _factory.CreateDefaultClient(new Uri("https://localhost"));
        unkeyed.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        unkeyed.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        using var absent = await unkeyed.PostAsJsonAsync($"/api/platform/audit/snapshots/{snapshot:D}/export",
            new { columns = new[] { "Id", "OccurredAt" } });
        Assert.Equal(HttpStatusCode.BadRequest, absent.StatusCode);
        await using var verify = Open();
        Assert.False(await verify.AuditExportReceipts.AnyAsync());
    }

    [Fact]
    public async Task AuditExport_SameOperationCannotBeMovedToAnotherSnapshotOrSession()
    {
        await SignInAsync(); await GrantPlatformAsync(); await GrantAuditExportAsync();
        var snapshot = await CreateExportSnapshotAsync(1);
        var operation = Guid.NewGuid();
        using var initial = await ExportAuditAsync(snapshot, operation, ["Id", "OccurredAt"]);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var otherSnapshot = await CreateExportSnapshotAsync(0);
        using var conflict = await ExportAuditAsync(otherSnapshot, operation, ["Id", "OccurredAt"]);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await SignInAsync();
        using var session = await ExportAuditAsync(snapshot, operation, ["Id", "OccurredAt"]);
        Assert.Equal(HttpStatusCode.NotFound, session.StatusCode);
        await using var verify = Open();
        Assert.Equal(1, await verify.AuditExportReceipts.CountAsync());
    }

    private async Task GrantAuditExportAsync()
    {
        await using var seed = Open();
        var role = new AccessRole(_tenant, "fixture-export", [new PermissionGrant("platform.audit.export", DataScope.All,
            ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "OccurredAt", "Action"))]);
        seed.Roles.Add(role);
        seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
        await seed.SaveChangesAsync();
    }

    private async Task<Guid> CreateExportSnapshotAsync(int count)
    {
        await using (var seed = Open())
        {
            seed.Audit.AddRange(Enumerable.Range(0, count).Select(index =>
                new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(), PlatformAuditAction.RoleChanged,
                    null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow().AddSeconds(-index))));
            await seed.SaveChangesAsync();
        }
        using var create = await _client.PostAsJsonAsync("/api/platform/audit/snapshots",
            new { from = _clock.GetUtcNow().AddHours(-1), to = _clock.GetUtcNow().AddMinutes(1), limit = 25 });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("snapshotId").GetGuid();
    }

    private Task<HttpResponseMessage> ExportAuditAsync(Guid snapshot, Guid operation, string[] columns)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/audit/snapshots/{snapshot:D}/export")
        { Content = JsonContent.Create(new { columns }) };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        return SendExportAsync(request);
    }

    private async Task<HttpResponseMessage> SendExportAsync(HttpRequestMessage request)
    {
        using (request) return await _client.SendAsync(request);
    }
}
