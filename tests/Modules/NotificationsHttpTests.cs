using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Wes.Host.Notifications;
using Wes.Host.Runtime;
using Wes.Notifications;
using Wes.Notifications.Features.Alarms;
using Wes.Notifications.Infrastructure.Persistence;
using Wes.Persistence.Tests;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Modules.Tests;

[Collection("IdentityHttp")]
[Trait("Provider", "MySQL")]
public sealed class NotificationsHttpTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Clock _clock = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(64);
    private string _connection = "";
    private Guid _session;
    private bool _sourceExisted;
    private Factory? _factory;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        MySqlTestConnection.ValidateTarget(_connection);
        await using var platform = Platform();
        await platform.Database.MigrateAsync();
        var department = new DirectoryDepartment(_tenant, "alarm-http", "Alarm HTTP fixture");
        platform.DirectoryTenants.Add(new DirectoryTenant(_tenant, "Alarm HTTP tenant"));
        platform.Departments.Add(department);
        platform.DirectoryUsers.Add(new DirectoryUser(_tenant, _actor, "FIXTURE", "Alarm actor", department.Id));
        platform.PasswordCredentials.Add(new PasswordCredential(_tenant, _actor, "FIXTURE",
            StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1,
                "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS"), _clock.GetUtcNow()));
        var session = new Session(_tenant, _actor, SessionClient.Web, _clock.GetUtcNow(), new string('A', 64));
        _session = session.Id;
        platform.Sessions.Add(session);
        platform.Roles.Add(new AccessRole(_tenant, "alarm-http", Grants()));
        var role = platform.Roles.Local.Single();
        platform.Memberships.Add(new RoleMembership(_tenant, _actor, role.Id));
        await platform.SaveChangesAsync();
        await using var notifications = Notifications();
        await notifications.Database.MigrateAsync();
        await using var sources = Sources();
        await sources.Database.MigrateAsync();
        var source = LoginSourceKey.FromTrustedPeer(IPAddress.Loopback);
        _sourceExisted = await sources.Sources.AnyAsync(item => item.SourceDigest == source.Digest);
    }

    [Fact]
    public void IndependentSettingsDefaultFalseAndNoUnsupportedAdapterFallback()
    {
        Assert.False(NotificationsSettings.Load(Config()).Enabled);
        foreach (var (key, value) in new[] { ("Enabled", "sensitive"), ("EmailEnabled", "true"),
                     ("SignalREnabled", "true"), ("Enabld", "true") })
        {
            var error = Assert.Throws<InvalidOperationException>(() => NotificationsSettings.Load(Config(key, value)));
            Assert.DoesNotContain("sensitive", error.ToString());
        }
        Assert.Throws<InvalidOperationException>(() => NotificationsSettings.Load(Config("Enabled", "true")));
    }

    [Fact]
    public async Task ActualHostDefaultDisabledDoesNotExposeAnyAlarmRoute()
    {
        Start(enabled: false);
        foreach (var path in new[] { "/alarms", $"/alarms/{Guid.NewGuid():D}", "/deliveries", $"/deliveries/{Guid.NewGuid():D}" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/notifications" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/alarms/manual", Manual())).StatusCode);
    }

    [Fact]
    public async Task ActualHostManualAggregationAcknowledgeAndIndependentDeliveryUseExactStableShapes()
    {
        Start();
        var body = Manual("High");
        using var created = await Post("/alarms/manual", body);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl!.ToString());
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "decision", "value" }, receipt.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal("Applied", receipt.GetProperty("decision").GetString());
        var alarm = receipt.GetProperty("value");
        AssertAlarmKeys(alarm);
        Assert.Equal("Manual", alarm.GetProperty("sourceKind").GetString());
        Assert.Equal("High", alarm.GetProperty("severity").GetString());
        Assert.Equal("Active", alarm.GetProperty("state").GetString());
        var id = alarm.GetProperty("id").GetGuid();
        using var replay = await Post("/alarms/manual", body);
        Assert.Equal("Replayed", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("decision").GetString());
        await using (var owner = new AlarmConsoleScope(_connection, _tenant,
                         _factory!.Services.GetRequiredService<IAlarmAuthorization>(), _clock))
            Assert.Equal("Success", (await owner.QueryAlarmsAsync(new(_tenant, _actor, _session), 0, 1)).Decision);
        using var list = await _client.GetAsync("/api/notifications/alarms?offset=0&limit=1");
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "limit", "offset", "rows" }, page.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Single(page.GetProperty("rows").EnumerateArray());
        using var detailResponse = await _client.GetAsync($"/api/notifications/alarms/{id:D}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(15, detail.EnumerateObject().Count());
        var fact = Assert.Single(detail.GetProperty("occurrences").EnumerateArray());
        Assert.Equal(new[] { "correlationId", "id", "occurredAt", "receivedAt", "sourceEventId", "summary" },
            fact.EnumerateObject().Select(item => item.Name).Order().ToArray());
        using var acknowledged = await Post($"/alarms/{id:D}/acknowledge",
            new { operationId = Guid.NewGuid(), expectedVersion = alarm.GetProperty("version").GetInt64(), reason = "Operator review" });
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.Equal("Acknowledged", (await acknowledged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetProperty("state").GetString());
        using var deliveryResponse = await _client.GetAsync("/api/notifications/deliveries?offset=0&limit=50");
        var delivery = Assert.Single((await deliveryResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        Assert.Equal("Sent", delivery.GetProperty("state").GetString());
        Assert.Equal("InApp", delivery.GetProperty("channel").GetString());
        Assert.Equal(10, delivery.EnumerateObject().Count());
        using var attemptsResponse = await _client.GetAsync($"/api/notifications/deliveries/{delivery.GetProperty("id").GetGuid():D}");
        var attempts = await attemptsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(11, attempts.EnumerateObject().Count());
        Assert.Equal(new[] { "attemptedAt", "errorCode", "id", "state" },
            Assert.Single(attempts.GetProperty("attempts").EnumerateArray()).EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.DoesNotContain("creatorId", detail.GetRawText());
        Assert.DoesNotContain("tenantId", detail.GetRawText());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("session", HttpStatusCode.Unauthorized)]
    [InlineData("permission", HttpStatusCode.Forbidden)]
    [InlineData("http", HttpStatusCode.BadRequest)]
    [InlineData("client", HttpStatusCode.BadRequest)]
    [InlineData("key", HttpStatusCode.BadRequest)]
    public async Task RealIdentityHttpsClientAndExactOptionalKeyRequired(string invalid, HttpStatusCode expected)
    {
        Start();
        if (invalid == "anonymous") _client.DefaultRequestHeaders.Authorization = null;
        if (invalid == "session")
        {
            await using var database = Platform();
            (await database.Sessions.SingleAsync()).Revoke(_clock.GetUtcNow());
            await database.SaveChangesAsync();
        }
        if (invalid == "permission") await RemoveField("notifications.alarms.manual.create", "Code");
        if (invalid == "client") _client.DefaultRequestHeaders.Remove("X-WES-Client");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            (invalid == "http" ? "http://localhost" : "") + "/api/notifications/alarms/manual")
        { Content = JsonContent.Create(Manual()) };
        if (invalid == "key") request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("numeric-severity")]
    [InlineData("wrong-case")]
    [InlineData("null")]
    [InlineData("noncanonical-uuid")]
    [InlineData("future-device")]
    [InlineData("content")]
    [InlineData("oversize")]
    public async Task StrictBodyRejectsAllUntrustedAndMalformedShapes(string invalid)
    {
        Start();
        var body = JsonSerializer.Serialize(Manual());
        var json = invalid switch
        {
            "unknown" => body.Insert(1, "\"actorId\":\"untrusted\","),
            "duplicate" => body.Insert(1, "\"code\":\"Shadow\","),
            "missing" => "{\"operationId\":\"" + Guid.NewGuid().ToString("D") + "\"}",
            "numeric-severity" => body.Replace("\"Warning\"", "2", StringComparison.Ordinal),
            "wrong-case" => body.Replace("\"code\"", "\"Code\"", StringComparison.Ordinal),
            "null" => "null",
            "noncanonical-uuid" => body.Replace("-", "", StringComparison.Ordinal),
            "future-device" => body.Insert(1, "\"sourceKind\":\"Device\","),
            "oversize" => body + new string(' ', 4097),
            _ => body
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/alarms/manual")
        { Content = new StringContent(json, Encoding.UTF8, invalid == "content" ? "text/plain" : "application/json") };
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    public async Task StrictPaginationNeverAppearsSuccessful(string query)
    {
        Start();
        foreach (var collection in new[] { "alarms", "deliveries" })
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/notifications/{collection}?{query}")).StatusCode);
    }

    [Theory]
    [InlineData("Summary")]
    [InlineData("Id")]
    [InlineData("Version")]
    public async Task CurrentFieldRevocationDeniesDetailAndManualReplay(string field)
    {
        Start();
        var body = Manual();
        using var first = await Post("/alarms/manual", body);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetProperty("id").GetGuid();
        await RemoveField("notifications.alarms.read", field);
        using var read = await _client.GetAsync($"/api/notifications/alarms/{id:D}");
        using var replay = await Post("/alarms/manual", body);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
    }

    [Fact]
    public async Task CurrentDepartmentAndRealCreatorAreOwnerMetadataNotTenantHeader()
    {
        Start();
        var body = Manual();
        using var created = await Post("/alarms/manual", body);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetProperty("id").GetGuid();
        await using (var database = Platform())
        {
            var role = await database.Roles.SingleAsync();
            var original = (await database.Departments.SingleAsync()).Id;
            role.Change(role.Version, true, role.Rules.Select(rule => rule with
            { Scope = DataScope.Departments, Departments = ImmutableHashSet.Create(original) }));
            var other = new DirectoryDepartment(_tenant, "other", "Other");
            database.Departments.Add(other);
            var user = await database.DirectoryUsers.SingleAsync();
            database.Entry(user).Property(item => item.DepartmentId).CurrentValue = other.Id;
            await database.SaveChangesAsync();
        }
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString("D"));
        using var read = await _client.GetAsync($"/api/notifications/alarms/{id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [Fact]
    public async Task ProviderFailureHasSafeNonSuccessJsonAndNoConnectionDetails()
    {
        Start(providerFailure: true);
        using var response = await _client.GetAsync("/api/notifications/alarms");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ConnectionString", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rows", text, StringComparison.Ordinal);
        Assert.Contains("InternalError", text);
    }

    [Fact]
    [Trait("CaseId", "TC-T14-CONSOLE-BROWSER-01")]
    public async Task ActualHttpsMySqlBrowserRecoversExactAcknowledgementAndIndependentInAppDelivery()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WES.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var web = Path.Combine(root.FullName, "clients", "ops-web");
        Assert.True(File.Exists(Path.Combine(web, "dist", "index.html")), "Build the reviewed client before browser validation.");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        await using var server = new Factory(_connection, _key, _clock, true, Path.Combine(web, "dist"));
        server.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        server.StartServer();
        var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var handler = new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint };
        using var probe = new HttpClient(handler);
        Assert.Equal(HttpStatusCode.OK, (await probe.GetAsync(address + "/health/live")).StatusCode);
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = web,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(Path.Combine(web, "node_modules", "@playwright", "test", "cli.js"));
        start.ArgumentList.Add("test"); start.ArgumentList.Add("alarms.spec.ts"); start.ArgumentList.Add("--config=playwright.config.ts");
        start.Environment["WES_E2E_URL"] = address;
        start.Environment["WES_E2E_TENANT"] = _tenant.ToString("D");
        start.Environment["WES_E2E_NOTIFICATION_FIXTURE"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
        await using var notifications = Notifications();
        await notifications.Database.OpenConnectionAsync();
        using var verification = notifications.Database.GetDbConnection().CreateCommand();
        verification.CommandText = "SELECT State,OccurrenceCount FROM ntf_alarms WHERE TenantId=@tenant";
        var tenant = verification.CreateParameter(); tenant.ParameterName = "@tenant"; tenant.Value = _tenant.ToString("D");
        verification.Parameters.Add(tenant);
        using (var reader = await verification.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal("Cleared", reader.GetString(0)); Assert.Equal(1, reader.GetInt32(1));
            Assert.False(await reader.ReadAsync());
        }
        verification.CommandText = "SELECT COUNT(*) FROM ntf_alarm_audits WHERE TenantId=@tenant";
        Assert.Equal(3L, Convert.ToInt64(await verification.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        verification.CommandText = "SELECT COUNT(*) FROM ntf_alarm_delivery_attempts WHERE TenantId=@tenant";
        Assert.Equal(1L, Convert.ToInt64(await verification.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    private void Start(bool enabled = true, bool providerFailure = false)
    {
        _factory = new Factory(providerFailure ? "Server=127.0.0.1;Port=1;Database=wes_test;Connection Timeout=1;Pooling=false" : _connection,
            _key, _clock, enabled);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        _client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        var now = _clock.GetUtcNow();
        var token = new JwtSecurityToken("alarm-http", "alarm-http", new[]
        {
            new Claim("sub", _actor.ToString("D")), new Claim("tid", _tenant.ToString("D")), new Claim("sid", _session.ToString("D")),
            new Claim("wti", now.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            new Claim("iat", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        }, now.UtcDateTime, now.AddMinutes(15).UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha512));
        _client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private static object Manual(string severity = "Warning") =>
        new { operationId = Guid.NewGuid(), resourceId = Guid.NewGuid(), code = "Manual.Http", severity, summary = "Actor-owned manual alarm" };
    private Task<HttpResponseMessage> Post(string path, object body) => _client.PostAsJsonAsync("/api/notifications" + path, body);
    private static void AssertAlarmKeys(JsonElement alarm) => Assert.Equal(new[]
    {
        "acknowledgedAt", "acknowledgedBy", "code", "firstOccurredAt", "id", "lastOccurredAt", "occurrenceCount",
        "resourceId", "severity", "silenceUntil", "sourceKind", "sourceOwner", "state", "version"
    }, alarm.EnumerateObject().Select(item => item.Name).Order().ToArray());
    private static PermissionGrant[] Grants()
    {
        var alarms = AlarmConsoleFields.AlarmRead.ToImmutableHashSet();
        var deliveries = AlarmConsoleFields.DeliveryRead.ToImmutableHashSet();
        var action = alarms.Union(new[] { "OperationId", "ExpectedVersion", "Reason", "Seconds" });
        return new[]
        {
            new PermissionGrant("notifications.alarms.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, alarms),
            new PermissionGrant("notifications.alarms.manual.create", DataScope.Own, ImmutableHashSet<Guid>.Empty, AlarmConsoleFields.ManualCreate.ToImmutableHashSet()),
            new PermissionGrant("notifications.alarms.acknowledge", DataScope.Own, ImmutableHashSet<Guid>.Empty, action),
            new PermissionGrant("notifications.alarms.manual.clear", DataScope.Own, ImmutableHashSet<Guid>.Empty, action),
            new PermissionGrant("notifications.alarms.silence", DataScope.Own, ImmutableHashSet<Guid>.Empty, action),
            new PermissionGrant("notifications.deliveries.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, deliveries),
            new PermissionGrant("notifications.deliveries.retry", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                deliveries.Union(new[] { "OperationId", "ExpectedVersion", "Reason" }))
        };
    }
    private async Task RemoveField(string permission, string field)
    {
        await using var database = Platform();
        var role = await database.Roles.SingleAsync();
        role.Change(role.Version, true, role.Rules.Select(rule => rule.Permission == permission
            ? rule with { Fields = rule.Fields.Remove(field) } : rule));
        await database.SaveChangesAsync();
    }
    private static IConfiguration Config(string? key = null, string? value = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(key is null ? [] : new Dictionary<string, string?> { [$"Wes:Notifications:{key}"] = value }).Build();
    private PlatformDbContext Platform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options, _tenant);
    private NotificationsDbContext Notifications() => new(new DbContextOptionsBuilder<NotificationsDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesNotificationsMigrations")).Options, _tenant);
    private LoginSourceDbContext Sources() => new(new DbContextOptionsBuilder<LoginSourceDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesLoginSourceMigrations")).Options);
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class Factory(string connection, byte[] key, TimeProvider clock, bool enabled, string? webRoot = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wes:Identity:Enabled"] = "true",
                ["Wes:Identity:ConnectionString"] = connection,
                ["Wes:Identity:SigningKey"] = Convert.ToBase64String(key),
                ["Wes:Identity:ChallengeKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Wes:Identity:Issuer"] = "alarm-http",
                ["Wes:Identity:Audience"] = "alarm-http",
                ["Wes:Identity:BreachCorpusPath"] = "unused-alarm-http-corpus",
                ["Wes:Notifications:Enabled"] = enabled.ToString(),
                ["Features:Modules:Platform"] = "true",
                ["Wes:Web:Root"] = webRoot
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton(clock);
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.RemoveAll<FeatureSwitches>();
                services.RemoveAll<Microsoft.Extensions.Options.IOptions<FeatureSwitches>>();
                services.AddSingleton(provider => FeatureSwitchConfiguration.Load(provider.GetRequiredService<IConfiguration>()));
                services.AddSingleton(provider => Microsoft.Extensions.Options.Options.Create(provider.GetRequiredService<FeatureSwitches>()));
            });
        }
    }
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_connection.Length == 0) return;
        await using (var notifications = Notifications())
        {
            foreach (var sql in new[]
            {
                "DELETE FROM ntf_alarm_delivery_attempts WHERE TenantId = {0}", "DELETE FROM ntf_alarm_memberships WHERE TenantId = {0}",
                "DELETE FROM ntf_alarm_deliveries WHERE TenantId = {0}", "DELETE FROM ntf_alarm_audits WHERE TenantId = {0}",
                "DELETE FROM ntf_alarm_operations WHERE TenantId = {0}", "DELETE FROM ntf_alarms WHERE TenantId = {0}",
                "DELETE FROM ntf_alarm_occurrences WHERE TenantId = {0}", "DELETE FROM ntf_alarm_console_locks WHERE TenantId = {0}"
            }) await notifications.Database.ExecuteSqlRawAsync(sql, _tenant.ToString("D"));
        }
        await using var platform = Platform();
        await platform.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {_tenant.ToString("D")}");
        await platform.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_login_events WHERE TenantId = {_tenant.ToString("D")}");
        platform.Memberships.RemoveRange(await platform.Memberships.ToListAsync());
        platform.Sessions.RemoveRange(await platform.Sessions.ToListAsync());
        platform.Roles.RemoveRange(await platform.Roles.ToListAsync());
        platform.PasswordCredentials.RemoveRange(await platform.PasswordCredentials.ToListAsync());
        platform.LoginAccounts.RemoveRange(await platform.LoginAccounts.ToListAsync());
        platform.LoginChallenges.RemoveRange(await platform.LoginChallenges.ToListAsync());
        platform.DirectoryUsers.RemoveRange(await platform.DirectoryUsers.ToListAsync());
        platform.Departments.RemoveRange(await platform.Departments.ToListAsync());
        platform.DirectoryTenants.RemoveRange(await platform.DirectoryTenants.ToListAsync());
        await platform.SaveChangesAsync();
        if (!_sourceExisted)
        {
            await using var sources = Sources();
            var source = LoginSourceKey.FromTrustedPeer(IPAddress.Loopback);
            sources.Sources.RemoveRange(await sources.Sources.Where(item => item.SourceDigest == source.Digest).ToListAsync());
            await sources.SaveChangesAsync();
        }
    }
}
