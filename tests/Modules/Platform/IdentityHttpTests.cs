using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Wes.Host.Identity;
using Wes.Persistence.Tests;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Messaging;
using Wes.Platform.Infrastructure.Messaging;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Real HTTP and isolated MySQL authentication, cookie secrecy and durable access revocation.</summary>
[Collection("IdentityHttp")]
[Trait("Provider", "MySQL")]
public sealed partial class IdentityHttpTests : IAsyncLifetime
{
    private const string Password = "WES reference fixture password";
    private const string Hash = "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS";
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly MutableClock _clock = new();
    private readonly string _corpus = Path.Combine(Path.GetTempPath(), $"wes-breach-{Guid.NewGuid():N}.txt");
    private string _connection = "";
    private Factory _factory = null!;
    private HttpClient _client = null!;
    private bool _migrated;

    /// <summary>Only synthetic credentials, random fixture owners and runtime-generated keys are provisioned.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await File.WriteAllTextAsync(_corpus, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("known breached synthetic fixture"))));
        await using var database = Open();
        await database.Database.MigrateAsync();
        await using var sources = Sources();
        await sources.Database.MigrateAsync();
        _migrated = true;
        var department = new DirectoryDepartment(_tenant, "fixture", "Fixture department");
        database.DirectoryTenants.Add(new DirectoryTenant(_tenant, "Fixture tenant", "zh-CN"));
        database.Departments.Add(department);
        database.DirectoryUsers.Add(new DirectoryUser(_tenant, _user, "FIXTURE", "Fixture subject", department.Id));
        database.PasswordCredentials.Add(new PasswordCredential(_tenant, _user, "FIXTURE",
            StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, Hash), _clock.GetUtcNow()));
        await database.SaveChangesAsync();
        _factory = new Factory(_connection, _corpus, _clock);
        _client = Client();
    }

    /// <summary>Successful login must deliver a signed access token but only a protected HttpOnly refresh cookie.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-001")]
    public async Task LoginCreatesDurableFamilyAndProtectedCookieThenLogoutRevokesAccess()
    {
        using var response = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("accessToken").GetString()!;
        Assert.Equal(3, token.Split('.').Length);
        Assert.False(json.TryGetProperty("refreshToken", out _));
        Assert.False(json.TryGetProperty("password", out _));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var me = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var subject = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_tenant, subject.GetProperty("tenantId").GetGuid());
        Assert.Equal(_user, subject.GetProperty("userId").GetGuid());
        Assert.False(subject.TryGetProperty("historyData", out _));
        using var sessions = await _client.GetAsync("/api/identity/sessions");
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        Assert.DoesNotContain("digest", await sessions.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var logout = await _client.PostAsync("/api/identity/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var denied = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await using var verify = Open();
        Assert.NotNull((await verify.Sessions.SingleAsync()).RevokedAt);
        Assert.Equal(2, await verify.Audit.CountAsync() + await verify.LoginEvents.CountAsync());
    }

    /// <summary>Changing only the account-existence fact must not change the anonymous failure status/body shape.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-001")]
    public async Task UnknownAccountAndWrongPasswordHaveTheSameSafeFailure()
    {
        using var wrong = await LoginAsync(password: "not the correct fixture password");
        using var unknown = await LoginAsync(account: "UNKNOWN");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        var left = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        var right = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(left.GetProperty("code").GetString(), right.GetProperty("code").GetString());
        Assert.Equal(left.GetProperty("detail").GetString(), right.GetProperty("detail").GetString());
        Assert.False(left.TryGetProperty("requiresChallenge", out _));
        await using var verify = Open();
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Equal(2, await verify.LoginEvents.CountAsync());
    }

    /// <summary>Cookie rotation must commit before delivery; replaying an actual old cookie revokes the family and its access.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-002")]
    public async Task RefreshRotatesProtectedCookieAndOldCookieReplayRevokesFamily()
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var oldCookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var refresh = await _client.PostAsync("/api/identity/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.NotEqual(oldCookie, Assert.Single(refresh.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        var token = (await refresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        using var replayClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        replayClient.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        replayClient.DefaultRequestHeaders.Add("Cookie", oldCookie);
        using var replay = await replayClient.PostAsync("/api/identity/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var denied = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>Access at second 899 works after real activity; the same signature must fail at its exact 900-second deadline.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-003")]
    public async Task JwtHasExclusiveDeadlineAndActivityDoesNotExtendTheAccessToken()
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _clock.Advance(TimeSpan.FromSeconds(899));
        using var activity = await _client.PostAsync("/api/identity/activity", null);
        Assert.Equal(HttpStatusCode.NoContent, activity.StatusCode);
        using var accepted = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var refresh = await _client.PostAsync("/api/identity/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    /// <summary>Neither an altered token signature nor untrusted tenant/proxy headers can replace verified claims/peer identity.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-003")]
    public async Task TamperedTokenIsRejectedAndCallerTenantHeaderCannotMoveTheSubject()
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString("D"));
        _client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.42");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var correct = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
        Assert.Equal(_tenant, (await correct.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenantId").GetGuid());
        var pieces = token.Split('.');
        pieces[2] = (pieces[2][0] == 'A' ? "B" : "A") + pieces[2][1..];
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", string.Join('.', pieces));
        using var bad = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    /// <summary>A valid family cannot bypass exact issuer/audience/algorithm, required signature or signed issuance checks.</summary>
    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("algorithm")]
    [InlineData("unsigned")]
    [InlineData("future")]
    [InlineData("missing-issuance")]
    [InlineData("mismatched-issuance")]
    [Trait("CaseId", "IDENTITY-HTTP-003")]
    public async Task ValidFamilyDoesNotAuthorizeMalformedTokenBindings(string variant)
    {
        await SignInAsync();
        var original = new JwtSecurityTokenHandler().ReadJwtToken(_client.DefaultRequestHeaders.Authorization!.Parameter);
        var settings = _factory.Services.GetRequiredService<IdentitySettings>();
        var claims = original.Claims.Where(item => item.Type is not ("iss" or "aud" or "exp" or "nbf")).ToList();
        if (variant == "missing-issuance") claims.RemoveAll(item => item.Type == "wti");
        if (variant == "mismatched-issuance")
        {
            claims.RemoveAll(item => item.Type == "iat");
            claims.Add(new Claim("iat", (_clock.GetUtcNow().ToUnixTimeSeconds() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));
        }
        var credentials = variant == "unsigned" ? null : new SigningCredentials(new SymmetricSecurityKey(settings.SigningKey),
            variant == "algorithm" ? SecurityAlgorithms.HmacSha256 : SecurityAlgorithms.HmacSha512);
        var token = new JwtSecurityToken(variant == "issuer" ? "wrong-issuer" : settings.Issuer,
            variant == "audience" ? "wrong-audience" : settings.Audience, claims,
            _clock.GetUtcNow().AddSeconds(variant == "future" ? 1 : 0).UtcDateTime,
            _clock.GetUtcNow().AddSeconds(900).UtcDateTime, credentials);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Simple cross-site form requests and insecure transport cannot reach credential or refresh owners.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-HTTP-004")]
    public async Task CredentialWritesRequireHttpsAndNonSimpleClientHeader(bool insecure)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri(insecure ? "http://localhost" : "https://localhost"), AllowAutoRedirect = false });
        if (insecure) client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        using var response = await client.PostAsJsonAsync("/api/identity/login", LoginRequest());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.Sessions.ToListAsync());
    }

    /// <summary>Raster challenge output contains no plaintext answer; its committed binding lasts exactly 120 seconds.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-004")]
    public async Task ChallengeIsPngNotAnswerTextAndUsesDurableExpiry()
    {
        using var response = await _client.PostAsJsonAsync("/api/identity/challenge", new { tenantId = _tenant, account = "fixture" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("answer", out _));
        var url = json.GetProperty("imageDataUrl").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url);
        var png = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8));
        var position = 8;
        var chunks = new List<string>();
        byte[]? raster = null;
        while (position < png.Length)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position, 4)));
            var name = Encoding.ASCII.GetString(png, position + 4, 4);
            chunks.Add(name);
            var data = png.AsSpan(position + 8, size);
            uint crc = uint.MaxValue;
            foreach (var item in png.AsSpan(position + 4, size + 4))
            {
                crc ^= item;
                for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            Assert.Equal(~crc, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + size + 8, 4)));
            if (name == "IHDR")
            {
                Assert.Equal(220u, BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                Assert.Equal(64u, BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                Assert.Equal(new byte[] { 8, 2, 0, 0, 0 }, data[8..].ToArray());
            }
            if (name == "IDAT")
            {
                await using var compressed = new MemoryStream(data.ToArray());
                await using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                await using var decoded = new MemoryStream();
                await zlib.CopyToAsync(decoded);
                raster = decoded.ToArray();
            }
            position += size + 12;
        }
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks);
        Assert.NotNull(raster);
        Assert.Equal((220 * 3 + 1) * 64, raster.Length);
        Assert.All(Enumerable.Range(0, 64), row => Assert.Equal(0, raster[row * (220 * 3 + 1)]));
        Assert.Equal(_clock.GetUtcNow().AddSeconds(120), json.GetProperty("expiresAt").GetDateTimeOffset());
        await using var verify = Open();
        Assert.Single(await verify.LoginChallenges.ToListAsync());
    }

    /// <summary>A server-side prohibited password must not mutate the credential or revoke a valid family.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-005")]
    public async Task PasswordChangeUsesServerBreachCorpusAndRevokesAllFamiliesOnlyAfterAcceptance()
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var me = await _client.GetAsync("/api/identity/me");
        var version = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credentialVersion").GetString();
        using var rejected = await _client.PostAsJsonAsync("/api/identity/password", new
        { expectedVersion = version, oldPassword = Password, newPassword = "known breached synthetic fixture" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var accepted = await _client.PostAsJsonAsync("/api/identity/password", new
        { expectedVersion = version, oldPassword = Password, newPassword = "a genuinely different fixture password" });
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var revoked = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var newLogin = await LoginAsync(password: "a genuinely different fixture password");
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    /// <summary>Return URLs cannot become protocol-relative, encoded or external redirects after successful authentication.</summary>
    [Theory]
    [InlineData("//evil.invalid")]
    [InlineData("https://evil.invalid")]
    [InlineData("/%2f%2fevil.invalid")]
    [InlineData("/\\evil.invalid")]
    [InlineData("/admin/users")]
    [Trait("CaseId", "IDENTITY-HTTP-004")]
    public async Task ReturnTargetUsesFixedInternalAllowlist(string target)
    {
        using var response = await _client.PostAsJsonAsync("/api/identity/login", LoginRequest(returnUrl: target));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(target == "/admin/users" ? target : "/account/sessions",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnUrl").GetString());
    }

    /// <summary>A refresh poll does not count as real activity and cannot extend either Web or PDA idle deadlines.</summary>
    [Theory]
    [InlineData(0, 900)]
    [InlineData(1, 300)]
    [Trait("CaseId", "IDENTITY-HTTP-003")]
    public async Task RefreshCannotUnlockAnIdleFamily(int client, int idleSeconds)
    {
        using var response = await _client.PostAsJsonAsync("/api/identity/login",
            new { tenantId = _tenant, account = "fixture", password = Password, client });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(idleSeconds - 1));
        using var first = await _client.PostAsync("/api/identity/refresh", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var idle = await _client.PostAsync("/api/identity/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, idle.StatusCode);
        await using var verify = Open();
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 4, 30, 0, TimeSpan.Zero), (await verify.Sessions.SingleAsync()).LastActivityAt);
    }

    /// <summary>Removing/changing the deployment corpus must fail explicitly, never manufacture Clear evidence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-HTTP-005")]
    public async Task MissingOrMalformedBreachCorpusCannotChangePassword(bool malformed)
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        using var me = await _client.GetAsync("/api/identity/me");
        var version = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credentialVersion").GetString();
        if (malformed) await File.WriteAllTextAsync(_corpus, "not a reviewed SHA256 corpus");
        else File.Delete(_corpus);
        using var change = await _client.PostAsJsonAsync("/api/identity/password", new
        { expectedVersion = version, oldPassword = Password, newPassword = "a genuinely different fixture password" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, change.StatusCode);
        await using var verify = Open();
        Assert.Equal(version, (await verify.PasswordCredentials.SingleAsync()).Version);
        Assert.Null((await verify.Sessions.SingleAsync()).RevokedAt);
    }

    /// <summary>An authenticated subject can revoke its own selected family but cannot select another user's or tenant's family.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-006")]
    public async Task RevokeSelectedOwnFamilyRequiresCurrentVersionAndCannotRevokeOtherOwners()
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        Guid id, otherId;
        string version;
        await using (var seed = Open())
        {
            var own = new Session(_tenant, _user, SessionClient.Pda, _clock.GetUtcNow(), RefreshSecret.Generate().Digest);
            var other = new Session(_tenant, Guid.NewGuid(), SessionClient.Web, _clock.GetUtcNow(), RefreshSecret.Generate().Digest);
            id = own.Id; otherId = other.Id; version = own.Version;
            seed.Sessions.AddRange(own, other);
            await seed.SaveChangesAsync();
        }
        using var foreign = await _client.PostAsJsonAsync($"/api/identity/sessions/{otherId:D}/revoke", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var stale = await _client.PostAsJsonAsync($"/api/identity/sessions/{id:D}/revoke", new { expectedVersion = Guid.NewGuid().ToString("D") });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var revoked = await _client.PostAsJsonAsync($"/api/identity/sessions/{id:D}/revoke", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var current = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        await using var verify = Open();
        Assert.NotNull((await verify.Sessions.SingleAsync(item => item.Id == id)).RevokedAt);
        Assert.Null((await verify.Sessions.SingleAsync(item => item.Id == otherId)).RevokedAt);
        Assert.Single(await verify.Audit.ToListAsync());
    }

    /// <summary>A fail-closed migrated/imported credential must deny current access and refresh even if an orphan family survives.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-006")]
    public async Task DisabledCredentialCannotUseOrRefreshAnOrphanFamily()
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        await using (var seed = Open())
        {
            var credential = await seed.PasswordCredentials.SingleAsync();
            credential.SetEnabled(false, _clock.GetUtcNow());
            await seed.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        _client.DefaultRequestHeaders.Authorization = null;
        using var refresh = await _client.PostAsync("/api/identity/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        await using var verify = Open();
        Assert.Single((await verify.Sessions.Include(item => item.Credentials).SingleAsync()).Credentials);
    }

    private object LoginRequest(string account = "fixture", string password = Password, string? returnUrl = null) =>
        new { tenantId = _tenant, account, password, client = 0, returnUrl };

    /// <summary>Authentication middleware rejections must not become cacheable before the endpoint filter runs.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-007")]
    public async Task UnauthorizedIdentityResponseIsNeverCacheable()
    {
        using var response = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    /// <summary>Public credential transport records must be safe against incidental string formatting.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-007")]
    public void CredentialTransportRecordsCannotFormatPasswordsOrChallengeAnswers()
    {
        var login = new Wes.Host.Identity.LoginInput(_tenant, "fixture-account", "private-login-fixture",
            ChallengeId: Guid.NewGuid(), Answer: "ABC789");
        var change = new Wes.Host.Identity.PasswordInput(Guid.NewGuid().ToString("D"), "private-old-fixture", "private-new-fixture");
        Assert.DoesNotContain("private-login-fixture", login.ToString());
        Assert.DoesNotContain("ABC789", login.ToString());
        Assert.DoesNotContain("private-old-fixture", change.ToString());
        Assert.DoesNotContain("private-new-fixture", change.ToString());
    }

    /// <summary>Fractional issuance must preserve the full promised 900 seconds, rather than rounding expiration down.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-003")]
    public async Task FractionalIssueTimeDoesNotLoseTheLastPartOfItsPromisedLifetime()
    {
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        _clock.Advance(TimeSpan.FromSeconds(899.75));
        using var before = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(.25));
        using var at = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, at.StatusCode);
    }

    /// <summary>Neither a matching password nor an existing signed token overrides disabled current directory facts.</summary>
    [Theory]
    [InlineData("tenant")]
    [InlineData("department")]
    [InlineData("user")]
    [Trait("CaseId", "IDENTITY-HTTP-008")]
    public async Task DisabledCurrentDirectoryRejectsLoginAccessAndRefresh(string kind)
    {
        using var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        await using (var change = Open())
        {
            if (kind == "tenant")
            {
                var tenant = await change.DirectoryTenants.SingleAsync();
                tenant.Change(tenant.Version, tenant.Name, false, tenant.DefaultLanguage);
            }
            else if (kind == "department")
            {
                var department = await change.Departments.SingleAsync();
                department.Change(department.Version, department.Name, false);
            }
            else
            {
                var user = await change.DirectoryUsers.SingleAsync();
                user.Change(user.Version, user.Name, user.DepartmentId, false);
            }
            await change.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        _client.DefaultRequestHeaders.Authorization = null;
        using var refresh = await _client.PostAsync("/api/identity/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        using var newLogin = await LoginAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, newLogin.StatusCode);
        await using var verify = Open();
        Assert.Single(await verify.Sessions.ToListAsync());
        Assert.Single((await verify.Sessions.Include(item => item.Credentials).SingleAsync()).Credentials);
    }

    /// <summary>HTTP cannot treat a valid login as permission to inspect or edit other users.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-001")]
    public async Task DirectoryHttpRequiresCurrentGrantsAndSeparatesUserCreationFromCredentialProvision()
    {
        await SignInAsync();
        using var denied = await _client.GetAsync("/api/platform/users");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await GrantPlatformAsync();
        Guid department;
        await using (var seed = Open()) department = (await seed.Departments.SingleAsync()).Id;
        using var created = await _client.PostAsJsonAsync("/api/platform/users", new { account = "new-user", name = "New subject", departmentId = department });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var result = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = result.GetProperty("id").GetGuid();
        var version = result.GetProperty("version").GetString();
        await using (var verify = Open())
        {
            Assert.Equal(2, await verify.DirectoryUsers.CountAsync());
            Assert.False(await verify.PasswordCredentials.AnyAsync(item => item.UserId == id));
        }
        using var users = await _client.GetAsync("/api/platform/users?account=new-user&limit=1");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var page = await users.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, Assert.Single(page.GetProperty("rows").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.DoesNotContain("hash", await users.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var credential = await _client.PostAsJsonAsync($"/api/platform/users/{id:D}/credential", new
        { expectedVersion = version, password = "new subject fixture password" });
        Assert.Equal(HttpStatusCode.OK, credential.StatusCode);
        using var duplicate = await _client.PostAsJsonAsync("/api/platform/users", new { account = " NEW-USER ", name = "Duplicate", departmentId = department });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var stale = await _client.PutAsJsonAsync($"/api/platform/users/{id:D}", new
        { expectedVersion = version, name = "Must not overwrite", departmentId = department, enabled = true });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        await using var final = Open();
        Assert.Equal("New subject", (await final.DirectoryUsers.SingleAsync(item => item.Id == id)).Name);
    }

    /// <summary>Credential disable is exposed only to current field authority and atomically revokes every own family.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-HTTP-001")]
    public async Task CredentialDisableHttpChecksVersionAndRevokesCurrentFamily()
    {
        await SignInAsync();
        var target = Guid.NewGuid();
        await using (var seed = Open())
        {
            var departmentId = await seed.Departments.Select(item => item.Id).SingleAsync();
            seed.DirectoryUsers.Add(new DirectoryUser(_tenant, target, "TARGET", "Target subject", departmentId));
            seed.PasswordCredentials.Add(new PasswordCredential(_tenant, target, "TARGET",
                StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, Hash), _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
        }
        using var targetClient = Client();
        using var firstLogin = await targetClient.PostAsJsonAsync("/api/identity/login", LoginRequest("TARGET"));
        using var secondLogin = await targetClient.PostAsJsonAsync("/api/identity/login", LoginRequest("TARGET"));
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        string credentialVersion;
        await using (var read = Open())
            credentialVersion = (await read.PasswordCredentials.SingleAsync(item => item.UserId == target)).Version;

        using var unauthorized = await _client.PostAsJsonAsync($"/api/platform/users/{target:D}/disable",
            new { expectedVersion = credentialVersion });
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        await GrantPlatformAsync();
        using var projection = await _client.GetAsync("/api/platform/users?account=TARGET");
        Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
        var projectedRows = (await projection.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray();
        Assert.Equal(credentialVersion, Assert.Single(projectedRows).GetProperty("credentialVersion").GetString());

        using var stale = await _client.PostAsJsonAsync($"/api/platform/users/{target:D}/disable",
            new { expectedVersion = Guid.NewGuid().ToString("D") });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        await using (var verify = Open())
            Assert.True((await verify.PasswordCredentials.SingleAsync(item => item.UserId == target)).Enabled);

        using var response = await _client.PostAsJsonAsync($"/api/platform/users/{target:D}/disable",
            new { expectedVersion = credentialVersion });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var repeated = await _client.PostAsJsonAsync($"/api/platform/users/{target:D}/disable",
            new { expectedVersion = credentialVersion });
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        using var targetAccess = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        var targetToken = (await secondLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        targetAccess.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
        using var denied = await targetClient.SendAsync(targetAccess);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await using var sessionRead = Open();
        var remaining = await sessionRead.Sessions.Where(item => item.UserId == target).ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, family => Assert.NotNull(family.RevokedAt));
        await using var final = Open();
        Assert.False((await final.PasswordCredentials.SingleAsync(item => item.UserId == target)).Enabled);
        Assert.Single(await final.Audit.Where(item => item.Action == PlatformAuditAction.AccountDisabled).ToListAsync());
    }

    /// <summary>Config source/version and H status are safe projections, never secret keys or client-approved physical evidence.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-002")]
    public async Task SettingsHttpPreservesVersionsAndUnverifiedPhysicalBoundary()
    {
        await SignInAsync(); await GrantPlatformAsync();
        Guid id, physical;
        string version;
        await using (var seed = Open())
        {
            var setting = new VersionedSetting(_tenant, "query.snapshotTtlSeconds");
            var hardware = new VersionedSetting(_tenant, "device.feedbackFreshnessMilliseconds");
            seed.Settings.AddRange(setting, hardware);
            await seed.SaveChangesAsync();
            id = setting.Id; version = setting.Version; physical = hardware.Id;
        }
        using var response = await _client.GetAsync("/api/platform/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, page.GetProperty("rows").GetArrayLength());
        Assert.Contains("Unverified", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var invalid = await _client.PutAsJsonAsync($"/api/platform/settings/{id:D}", new { expectedVersion = version, value = "-1" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var change = await _client.PutAsJsonAsync($"/api/platform/settings/{id:D}", new { expectedVersion = version, value = "300" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        using var stale = await _client.PutAsJsonAsync($"/api/platform/settings/{id:D}", new { expectedVersion = version, value = "400" });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        string hardwareVersion;
        await using (var read = Open()) hardwareVersion = (await read.Settings.SingleAsync(item => item.Id == physical)).Version;
        using var hardwareWrite = await _client.PutAsJsonAsync($"/api/platform/settings/{physical:D}", new { expectedVersion = hardwareVersion, value = "500" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hardwareWrite.StatusCode);
        await using var verify = Open();
        Assert.Null((await verify.Settings.SingleAsync(item => item.Id == physical)).Value);
        Assert.Single(await verify.Audit.ToListAsync());
    }

    /// <summary>The public adapter must retain one owner effect/audit and reauthorize durable receipt replay.</summary>
    [Theory]
    [InlineData("user")]
    [InlineData("role")]
    [InlineData("setting")]
    [Trait("CaseId", "PLATFORM-HTTP-008")]
    public async Task NamedPlatformWritesReplayOriginalSafeResultAndDetectChangedRequests(string kind)
    {
        await SignInAsync(); await GrantPlatformAsync();
        string path;
        object payload, changed;
        var method = HttpMethod.Put;
        await using (var seed = Open())
        {
            if (kind == "user")
            {
                var department = (await seed.Departments.SingleAsync()).Id;
                path = "/api/platform/users"; method = HttpMethod.Post;
                payload = new { account = "receipt-user", name = "Original", departmentId = department };
                changed = new { account = "receipt-user", name = "Changed", departmentId = department };
            }
            else if (kind == "role")
            {
                var role = new AccessRole(_tenant, "receipt-role", []);
                seed.Roles.Add(role); await seed.SaveChangesAsync();
                path = $"/api/platform/roles/{role.Id:D}";
                payload = new { expectedVersion = role.Version, enabled = false, rules = Array.Empty<object>() };
                changed = new { expectedVersion = role.Version, enabled = true, rules = Array.Empty<object>() };
            }
            else
            {
                var setting = new VersionedSetting(_tenant, "query.snapshotTtlSeconds");
                seed.Settings.Add(setting); await seed.SaveChangesAsync();
                path = $"/api/platform/settings/{setting.Id:D}";
                payload = new { expectedVersion = setting.Version, value = "300" };
                changed = new { expectedVersion = setting.Version, value = "400" };
            }
        }
        var operation = Guid.NewGuid().ToString("D");
        async Task<HttpResponseMessage> SendAsync(object input)
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(input) };
            request.Headers.Add("Idempotency-Key", operation);
            return await _client.SendAsync(request);
        }
        using var first = await SendAsync(payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = await first.Content.ReadAsStringAsync();
        using var replay = await SendAsync(payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        using var conflict = await SendAsync(changed);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using (var verify = Open())
        {
            Assert.Single(await verify.Audit.ToListAsync());
            Assert.Single(await verify.CommandReceipts.ToListAsync());
            var callerRole = await verify.Roles.SingleAsync(item => item.Code == "fixture-owner");
            callerRole.Change(callerRole.Version, false, callerRole.Rules);
            await verify.SaveChangesAsync();
        }
        using var revoked = await SendAsync(payload);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    /// <summary>Ordinary writes require a canonical operation UUID before a command can be submitted.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("invalid-key")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [Trait("CaseId", "PLATFORM-HTTP-008")]
    public async Task MissingOrInvalidOperationKeyCannotMutateDirectory(string? key)
    {
        await SignInAsync(); await GrantPlatformAsync();
        Guid department;
        await using (var read = Open()) department = (await read.Departments.SingleAsync()).Id;
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        if (key is not null) client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var response = await client.PostAsJsonAsync("/api/platform/users", new { account = "must-not-create", name = "Rejected", departmentId = department });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = Open();
        Assert.Single(await verify.DirectoryUsers.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    /// <summary>Audits and login events share current-authorized read transport, with no route for historical edits.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-003")]
    public async Task AuditHttpHasBoundedAuthorizedReadButNoMutationSurface()
    {
        await SignInAsync(); await GrantPlatformAsync();
        using var events = await _client.GetAsync("/api/platform/login-events?from=2026-10-24T00:00:00Z&to=2026-10-25T00:00:00Z&limit=1");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var page = await events.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(page.GetProperty("rows").EnumerateArray());
        using var oversized = await _client.GetAsync("/api/platform/audit?from=2026-10-24T00:00:00Z&to=2026-10-25T00:00:00Z&limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        using var edit = await _client.PutAsJsonAsync("/api/platform/audit", new { action = "rewrite" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, edit.StatusCode);
    }

    /// <summary>Audit verification freezes ordered membership across new facts and enforces its exact 600-second lifetime.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-011-SNAPSHOT")]
    public async Task AuditSnapshot_KeepsOriginalRowsAndExpiresAtThe600SecondBoundary()
    {
        await SignInAsync(); await GrantPlatformAsync();
        var from = _clock.GetUtcNow().AddMinutes(-1);
        var to = _clock.GetUtcNow().AddMinutes(1);
        await using (var seed = Open())
        {
            seed.Audit.AddRange(Enumerable.Range(0, 60).Select(index =>
                new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(),
                    PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), from.AddSeconds(index))));
            await seed.SaveChangesAsync();
        }

        using var create = await _client.PostAsJsonAsync("/api/platform/audit/snapshots",
            new { from, to, limit = 25 });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var firstPage = await create.Content.ReadFromJsonAsync<JsonElement>();
        var snapshotId = firstPage.GetProperty("snapshotId").GetGuid();
        Assert.Equal(60, firstPage.GetProperty("count").GetInt32());
        Assert.Equal(25, firstPage.GetProperty("rows").GetArrayLength());
        Assert.Equal(_clock.GetUtcNow().AddSeconds(600),
            firstPage.GetProperty("expiresAt").GetDateTimeOffset());

        await using (var insert = Open())
        {
            insert.Audit.Add(new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), from.AddSeconds(-1)));
            await insert.SaveChangesAsync();
        }

        async Task<JsonElement> ReadPageAsync(int offset)
        {
            using var response = await _client.GetAsync(
                $"/api/platform/audit/snapshots/{snapshotId:D}?offset={offset}&limit=25");
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"Expected a readable snapshot page, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var secondPage = await ReadPageAsync(25);
        var thirdPage = await ReadPageAsync(50);
        var ids = firstPage.GetProperty("rows").EnumerateArray()
            .Concat(secondPage.GetProperty("rows").EnumerateArray())
            .Concat(thirdPage.GetProperty("rows").EnumerateArray())
            .Select(row => row.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(60, ids.Distinct().Count());
        Assert.Equal(60, ids.Length);

        _clock.Advance(TimeSpan.FromSeconds(599));
        using (var beforeExpiry = await _client.GetAsync(
            $"/api/platform/audit/snapshots/{snapshotId:D}?offset=0&limit=25"))
            Assert.Equal(HttpStatusCode.OK, beforeExpiry.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = await _client.GetAsync(
            $"/api/platform/audit/snapshots/{snapshotId:D}?offset=0&limit=25");
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    /// <summary>Outbox metadata is hidden by default and requires the dedicated current-session read grant.</summary>
    [Fact]
    public async Task OutboxHttpRequiresDedicatedReadPermissionAndNeverExposesPayload()
    {
        await SignInAsync();

        using var denied = await _client.GetAsync("/api/platform/messages?limit=1");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await GrantPlatformAsync();
        using var authorized = await _client.GetAsync("/api/platform/messages?limit=1");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        var page = await authorized.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(page.GetProperty("authorized").GetBoolean());
        Assert.All(page.GetProperty("rows").EnumerateArray(),
            row => Assert.False(row.TryGetProperty("payload", out _)));
    }

    /// <summary>Outbox replay requires a separate current grant, expected-attempt fencing and header idempotency.</summary>
    [Fact]
    public async Task OutboxReplayHttpIsAuthorizedIdempotentAndDoesNotAcceptEnvelopeData()
    {
        using (var anonymous = await _client.PostAsJsonAsync(
            $"/api/platform/messages/{Guid.NewGuid():D}/replay", new { expectedAttempts = 1, reason = "test replay" }))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await SignInAsync();
        using (var unkeyedClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }))
        {
            unkeyedClient.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
            unkeyedClient.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
            foreach (var key in new string?[] { null, "invalid-key" })
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"/api/platform/messages/{Guid.NewGuid():D}/replay")
                {
                    Content = JsonContent.Create(new { expectedAttempts = 1, reason = "test replay" })
                };
                if (key is not null) request.Headers.Add("Idempotency-Key", key);
                using var response = await unkeyedClient.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }

        Guid eventId;
        string payload;
        await using (var seed = Open())
        {
            seed.Audit.Add(new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
            var message = await seed.OutboxMessages.SingleAsync();
            eventId = message.EventId;
            payload = message.Payload;
            var leaseId = Guid.NewGuid();
            Assert.True(await new PlatformOutboxLeaseStore(seed).TryLeaseAsync(
                eventId, leaseId, _clock.GetUtcNow(), TimeSpan.FromSeconds(30)));
            message.RecordFailure(leaseId, _clock.GetUtcNow(), OutboxFailureCategory.InvalidContract);
            await seed.SaveChangesAsync();
        }

        async Task<HttpResponseMessage> ReplayAsync(Guid operation, string reason, int expectedAttempts = 1)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/messages/{eventId:D}/replay")
            {
                Content = JsonContent.Create(new { expectedAttempts, reason })
            };
            request.Headers.Add("Idempotency-Key", operation.ToString("D"));
            return await _client.SendAsync(request);
        }

        var operation = Guid.NewGuid();
        using (var denied = await ReplayAsync(operation, "recover after contract repair"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await GrantPlatformAsync();
        await GrantReplayAsync();
        using (var invalidReason = await ReplayAsync(Guid.NewGuid(), "x"))
            Assert.Equal(HttpStatusCode.BadRequest, invalidReason.StatusCode);
        using (var stale = await ReplayAsync(Guid.NewGuid(), "stale attempt expectation", expectedAttempts: 2))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var replayed = await ReplayAsync(operation, "recover after contract repair"))
        {
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            var response = await replayed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(response.TryGetProperty("payload", out _));
            Assert.False(response.TryGetProperty("reason", out _));
        }
        using (var duplicate = await ReplayAsync(operation, "recover after contract repair"))
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using (var conflictingReuse = await ReplayAsync(operation, "different reason"))
            Assert.Equal(HttpStatusCode.Conflict, conflictingReuse.StatusCode);
        using (var noLongerQuarantined = await ReplayAsync(Guid.NewGuid(), "already replayed"))
            Assert.Equal(HttpStatusCode.Conflict, noLongerQuarantined.StatusCode);

        await using var verify = Open();
        var replayedMessage = await verify.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxMessageStatus.Pending, replayedMessage.Status);
        Assert.Equal(0, replayedMessage.Attempts);
        Assert.Equal(payload, replayedMessage.Payload);
        Assert.Equal(eventId, replayedMessage.EventId);
        Assert.Single(await verify.OutboxReplayReceipts.ToListAsync());
    }

    /// <summary>Authorized action/correlation filters run before pagination and cannot become hidden-field oracles.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-007")]
    public async Task AuditActionFilterIsAppliedAndRequiresCurrentFieldAuthority()
    {
        await SignInAsync(); await GrantPlatformAsync();
        var correlation = Guid.NewGuid();
        await using (var seed = Open())
        {
            seed.Audit.Add(new PlatformAudit(_tenant, _user, Guid.NewGuid(), correlation, PlatformAuditAction.SettingChanged,
                null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow()));
            seed.Audit.Add(new PlatformAudit(_tenant, _user, Guid.NewGuid(), Guid.NewGuid(), PlatformAuditAction.RoleChanged,
                null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
        }
        var path = $"/api/platform/audit?from=2026-10-24T00:00:00Z&to=2026-10-25T00:00:00Z&action={(int)PlatformAuditAction.SettingChanged}&correlationId={correlation:D}";
        using var read = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var row = Assert.Single((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        Assert.Equal((int)PlatformAuditAction.SettingChanged, row.GetProperty("action").GetInt32());
        await using (var seed = Open())
        {
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true, [new PermissionGrant("platform.audit.read", DataScope.All,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "OccurredAt"))]);
            await seed.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private async Task SignInAsync()
    {
        using var response = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    /// <summary>Session state/time filters are applied before bounded paging rather than ignored by the transport.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-009")]
    public async Task OwnSessionFiltersDoNotReturnActiveRowsForRevokedStateOrOutsideTimeRange()
    {
        await SignInAsync();
        using var revoked = await _client.GetAsync("/api/identity/sessions?state=revoked&limit=1");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Empty((await revoked.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        using var range = await _client.GetAsync("/api/identity/sessions?from=2026-10-25T00:00:00Z&limit=1");
        Assert.Equal(HttpStatusCode.OK, range.StatusCode);
        Assert.Empty((await range.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        using var invalid = await _client.GetAsync("/api/identity/sessions?state=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    /// <summary>Session administrators get projected owner pages, never an Own version on another subject's family.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-010")]
    public async Task AdministrativeSessionQueriesProjectFieldsPerFamilyAndCheckCurrentDepartmentBeforeRevoke()
    {
        await SignInAsync();
        using var denied = await _client.GetAsync("/api/identity/admin-sessions");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Guid target, otherUser, currentDepartment;
        string version;
        await using (var seed = Open())
        {
            otherUser = Guid.NewGuid();
            currentDepartment = (await seed.Departments.SingleAsync()).Id;
            var otherDepartment = new DirectoryDepartment(_tenant, "other", "Other department");
            seed.Departments.Add(otherDepartment);
            seed.DirectoryUsers.Add(new DirectoryUser(_tenant, otherUser, "OTHER", "Other subject", otherDepartment.Id));
            var family = new Session(_tenant, otherUser, SessionClient.Web, _clock.GetUtcNow(), RefreshSecret.Generate().Digest);
            seed.Sessions.Add(family); target = family.Id; version = family.Version;
            var grants = new[] {
                new PermissionGrant("platform.sessions.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Id", "UserId", "CreatedAt", "State")),
                new PermissionGrant("platform.sessions.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Version")),
                new PermissionGrant("platform.sessions.revoke", DataScope.Departments, ImmutableHashSet.Create(currentDepartment), ImmutableHashSet.Create("RevokedAt"))
            };
            var role = new AccessRole(_tenant, "session-reader", grants);
            seed.Roles.Add(role); seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
            await seed.SaveChangesAsync();
        }
        using var read = await _client.GetAsync("/api/identity/admin-sessions?limit=50");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var page = await read.Content.ReadFromJsonAsync<JsonElement>();
        var other = Assert.Single(page.GetProperty("rows").EnumerateArray(), row => row.GetProperty("id").GetGuid() == target);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("version").ValueKind);
        Assert.Equal(otherUser, other.GetProperty("userId").GetGuid());
        using var revoke = await _client.PostAsJsonAsync($"/api/identity/sessions/{target:D}/revoke", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        await using var verify = Open();
        Assert.Null((await verify.Sessions.SingleAsync(row => row.Id == target)).RevokedAt);
    }

    /// <summary>Named directory read adapters expose only current authorized owner pages.</summary>
    [Theory]
    [InlineData("departments")]
    [InlineData("roles")]
    [InlineData("tenants")]
    [InlineData("dictionary?category=task&language=zh-CN")]
    [Trait("CaseId", "PLATFORM-HTTP-004")]
    public async Task NamedDirectoryPagesUseAuthorizedBoundedOwnerQueries(string path)
    {
        await SignInAsync(); await GrantPlatformAsync();
        using var response = await _client.GetAsync("/api/platform/" + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("authorized").GetBoolean());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("rows").ValueKind);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    /// <summary>Department code lookup filters the authorized directory before applying its page window.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task DepartmentCandidateCodeLookupFiltersBeforePaging()
    {
        await SignInAsync(); await GrantPlatformAsync();
        await using (var seed = Open())
        {
            seed.Departments.AddRange(
                new DirectoryDepartment(_tenant, "LOOKUP-A", "Lookup A"),
                new DirectoryDepartment(_tenant, "LOOKUP-AB", "Lookup AB"));
            await seed.SaveChangesAsync();
        }

        using var response = await _client.GetAsync("/api/platform/departments?offset=0&limit=1&code=LOOKUP-AB");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(page.GetProperty("authorized").GetBoolean());
        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal("LOOKUP-AB", row.GetProperty("code").GetString());

        using var afterMatch = await _client.GetAsync("/api/platform/departments?offset=1&limit=25&code=LOOKUP-AB");
        Assert.Equal(HttpStatusCode.OK, afterMatch.StatusCode);
        Assert.Empty((await afterMatch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rows").EnumerateArray());
    }

    /// <summary>A department code lookup is denied when the caller cannot read the Code field.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task DepartmentCandidateCodeLookupRequiresCodeFieldGrant()
    {
        await SignInAsync();
        await using (var seed = Open())
        {
            var role = new AccessRole(_tenant, "department-name-reader",
            [
                new PermissionGrant("platform.departments.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Id", "Name", "Enabled"))
            ]);
            seed.Roles.Add(role);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
            await seed.SaveChangesAsync();
        }

        using var response = await _client.GetAsync("/api/platform/departments?offset=0&limit=25&code=FIXTURE");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Code filtering intersects its field scope with the department ID scope before querying.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task DepartmentCandidateCodeLookupIntersectsIdAndCodeScopes()
    {
        await SignInAsync();
        Guid codeScopedDepartment;
        await using (var seed = Open())
        {
            var department = new DirectoryDepartment(_tenant, "CODE-SCOPED", "Code scoped");
            seed.Departments.Add(department);
            codeScopedDepartment = department.Id;
            var role = new AccessRole(_tenant, "department-field-scope-reader",
            [
                new PermissionGrant("platform.departments.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Id", "Name", "Enabled")),
                new PermissionGrant("platform.departments.read", DataScope.Departments,
                    ImmutableHashSet.Create(codeScopedDepartment), ImmutableHashSet.Create("Code"))
            ]);
            seed.Roles.Add(role);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
            await seed.SaveChangesAsync();
        }

        using var hidden = await _client.GetAsync("/api/platform/departments?offset=0&limit=25&code=FIXTURE");
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        var hiddenPage = await hidden.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(hiddenPage.GetProperty("authorized").GetBoolean());
        Assert.Empty(hiddenPage.GetProperty("rows").EnumerateArray());

        using var visible = await _client.GetAsync("/api/platform/departments?offset=0&limit=25&code=CODE-SCOPED");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        var visiblePage = await visible.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(visiblePage.GetProperty("rows").EnumerateArray());
        Assert.Equal(codeScopedDepartment, row.GetProperty("id").GetGuid());
        Assert.Equal("CODE-SCOPED", row.GetProperty("code").GetString());
    }

    /// <summary>Department reference resolution returns only ID-authorized rows and projects other fields per resource.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task DepartmentReferenceResolutionReturnsAuthorizedFieldProjections()
    {
        await SignInAsync();
        Guid firstId, secondId, missingId;
        await using (var seed = Open())
        {
            var first = new DirectoryDepartment(_tenant, "REF-A", "Reference A");
            var second = new DirectoryDepartment(_tenant, "REF-B", "Reference B");
            seed.Departments.AddRange(first, second);
            firstId = first.Id; secondId = second.Id; missingId = Guid.NewGuid();
            var role = new AccessRole(_tenant, "department-reference-reader",
            [
                new PermissionGrant("platform.departments.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Id")),
                new PermissionGrant("platform.departments.read", DataScope.Departments,
                    ImmutableHashSet.Create(firstId), ImmutableHashSet.Create("Name", "Code")),
                new PermissionGrant("platform.departments.read", DataScope.Departments,
                    ImmutableHashSet.Create(secondId), ImmutableHashSet.Create("Enabled"))
            ]);
            seed.Roles.Add(role);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
            await seed.SaveChangesAsync();
        }

        using var response = await _client.GetAsync(
            $"/api/platform/departments/references?ids={firstId:D}&ids={secondId:D}&ids={missingId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(page.GetProperty("authorized").GetBoolean());
        var rows = page.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var firstRow = Assert.Single(rows, row => row.GetProperty("id").GetGuid() == firstId);
        Assert.Equal("Reference A", firstRow.GetProperty("name").GetString());
        Assert.Equal("REF-A", firstRow.GetProperty("code").GetString());
        var secondRow = Assert.Single(rows, row => row.GetProperty("id").GetGuid() == secondId);
        Assert.Equal(JsonValueKind.Null, secondRow.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, secondRow.GetProperty("code").ValueKind);
        Assert.DoesNotContain(rows, row => row.GetProperty("id").GetGuid() == missingId);
    }

    /// <summary>Department reference resolution requires ID authorization and rejects unbounded or empty batches.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task DepartmentReferenceResolutionEnforcesAuthorizationAndBatchBounds()
    {
        await SignInAsync();
        await using (var seed = Open())
        {
            var role = new AccessRole(_tenant, "department-name-only-reader",
            [
                new PermissionGrant("platform.departments.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Name"))
            ]);
            seed.Roles.Add(role);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
            await seed.SaveChangesAsync();
        }

        using var denied = await _client.GetAsync($"/api/platform/departments/references?ids={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var empty = await _client.GetAsync("/api/platform/departments/references");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var tooManyIds = string.Join("&ids=", Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString("D")));
        using var tooMany = await _client.GetAsync($"/api/platform/departments/references?ids={tooManyIds}");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    /// <summary>Role reference resolution returns only requested roles with currently authorized fields.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task RoleReferenceResolutionReturnsAuthorizedFieldProjections()
    {
        await SignInAsync();
        Guid codeOnlyId, enabledOnlyId, missingId;
        await using (var seed = Open())
        {
            var codeOnly = new AccessRole(_tenant, "REFERENCE-CODE", []);
            var enabledOnly = new AccessRole(_tenant, "REFERENCE-STATUS", []);
            enabledOnly.Change(enabledOnly.Version, false, []);
            seed.Roles.AddRange(codeOnly, enabledOnly);
            codeOnlyId = codeOnly.Id; enabledOnlyId = enabledOnly.Id; missingId = Guid.NewGuid();
            var reader = new AccessRole(_tenant, "role-reference-reader",
            [
                new PermissionGrant("platform.roles.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Id")),
                new PermissionGrant("platform.roles.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Code")),
                new PermissionGrant("platform.roles.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Enabled"))
            ]);
            seed.Roles.Add(reader);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, reader.Id));
            await seed.SaveChangesAsync();
        }

        using var response = await _client.GetAsync(
            $"/api/platform/roles/references?ids={codeOnlyId:D}&ids={codeOnlyId:D}&ids={enabledOnlyId:D}&ids={missingId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(page.GetProperty("authorized").GetBoolean());
        var rows = page.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var codeRow = Assert.Single(rows, row => row.GetProperty("id").GetGuid() == codeOnlyId);
        Assert.Equal("REFERENCE-CODE", codeRow.GetProperty("code").GetString());
        Assert.True(codeRow.GetProperty("enabled").GetBoolean());
        var statusRow = Assert.Single(rows, row => row.GetProperty("id").GetGuid() == enabledOnlyId);
        Assert.Equal("REFERENCE-STATUS", statusRow.GetProperty("code").GetString());
        Assert.False(statusRow.GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(rows, row => row.GetProperty("id").GetGuid() == missingId);
    }

    /// <summary>Role reference resolution omits fields that the current subject cannot read.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task RoleReferenceResolutionOmitsUnapprovedFields()
    {
        await SignInAsync();
        Guid roleId;
        await using (var seed = Open())
        {
            var target = new AccessRole(_tenant, "HIDDEN-ROLE-CODE", []);
            var reader = new AccessRole(_tenant, "role-reference-id-only",
            [
                new PermissionGrant("platform.roles.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Id"))
            ]);
            seed.Roles.AddRange(target, reader);
            roleId = target.Id;
            seed.Memberships.Add(new RoleMembership(_tenant, _user, reader.Id));
            await seed.SaveChangesAsync();
        }

        using var response = await _client.GetAsync($"/api/platform/roles/references?ids={roleId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal(roleId, row.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("code").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("enabled").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("rules").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("affectedSubjects").ValueKind);
    }

    /// <summary>Role reference resolution requires ID authorization and rejects empty or unbounded batches.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task RoleReferenceResolutionEnforcesAuthorizationAndBatchBounds()
    {
        await SignInAsync();
        await using (var seed = Open())
        {
            var reader = new AccessRole(_tenant, "role-reference-code-only",
            [
                new PermissionGrant("platform.roles.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Code"))
            ]);
            seed.Roles.Add(reader);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, reader.Id));
            await seed.SaveChangesAsync();
        }

        using var denied = await _client.GetAsync($"/api/platform/roles/references?ids={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var empty = await _client.GetAsync("/api/platform/roles/references");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var emptyId = await _client.GetAsync($"/api/platform/roles/references?ids={Guid.Empty:D}");
        Assert.Equal(HttpStatusCode.BadRequest, emptyId.StatusCode);
        var tooManyIds = string.Join("&ids=", Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString("D")));
        using var tooMany = await _client.GetAsync($"/api/platform/roles/references?ids={tooManyIds}");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    /// <summary>A forged role reference is rejected by the owner without changing membership, version, or audit.</summary>
    [Fact]
    [Trait("CaseId", "DTC-UI-022")]
    public async Task MembershipCommandRejectsUnknownRoleIdWithoutBusinessWrites()
    {
        await SignInAsync();
        await GrantPlatformAsync();
        string userVersion;
        Guid[] existingRoleIds;
        int auditCount;
        await using (var read = Open())
        {
            var user = await read.DirectoryUsers.SingleAsync(item => item.Id == _user);
            userVersion = user.Version;
            existingRoleIds = await read.Memberships.Where(item => item.UserId == _user)
                .Select(item => item.RoleId).ToArrayAsync();
            auditCount = await read.Audit.CountAsync();
        }

        using var response = await _client.PutAsJsonAsync($"/api/platform/users/{_user:D}/roles",
            new { expectedVersion = userVersion, roleIds = existingRoleIds.Append(Guid.NewGuid()).ToArray() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var verify = Open();
        Assert.Equal(userVersion, (await verify.DirectoryUsers.SingleAsync(item => item.Id == _user)).Version);
        Assert.Equal(existingRoleIds.Order(), await verify.Memberships.Where(item => item.UserId == _user)
            .Select(item => item.RoleId).Order().ToArrayAsync());
        Assert.Equal(auditCount, await verify.Audit.CountAsync());
    }

    /// <summary>Named filters must execute before pagination rather than merely decorate a client control.</summary>
    [Theory]
    [InlineData("/api/platform/roles?code=not-present")]
    [InlineData("/api/platform/roles?permission=not.present")]
    [InlineData("/api/platform/tenants?enabled=false")]
    [InlineData("/api/platform/settings?kind=H")]
    [InlineData("/api/platform/dictionary?category=task&missingTranslation=false")]
    [Trait("CaseId", "PLATFORM-HTTP-010")]
    public async Task NamedCatalogFiltersAreAppliedBeforePaging(string path)
    {
        await SignInAsync(); await GrantPlatformAsync();
        await using (var seed = Open())
        {
            seed.Settings.Add(new VersionedSetting(_tenant, "query.snapshotTtlSeconds"));
            seed.DictionaryEntries.Add(new DictionaryEntry(_tenant, "task", "ready", "task.ready"));
            await seed.SaveChangesAsync();
        }
        using var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
    }

    /// <summary>Impact is an explicitly field-authorized current membership count, never a cross-tenant/global assertion.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-010")]
    public async Task RoleImpactIsCurrentAndOnlyDisclosedWithItsFieldGrant()
    {
        await SignInAsync(); await GrantPlatformAsync();
        using var first = await _client.GetAsync("/api/platform/roles?code=fixture-owner");
        var row = Assert.Single((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray());
        Assert.True(row.TryGetProperty("affectedSubjects", out var affected));
        Assert.Equal(1, affected.GetInt32());
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync();
            role.Change(role.Version, true, role.Rules.Select(rule => new PermissionGrant(rule.Permission, rule.Scope,
                rule.Departments, rule.Fields.Remove("AffectedSubjects"))));
            await writer.SaveChangesAsync();
        }
        using var hidden = await _client.GetAsync("/api/platform/roles");
        Assert.Equal(JsonValueKind.Null, Assert.Single((await hidden.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rows").EnumerateArray()).GetProperty("affectedSubjects").ValueKind);
    }

    /// <summary>Dictionary codes survive label changes; translation writes require an exact owner version.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-005")]
    public async Task DictionaryHttpUsesStableCodesAndVersionedLanguageFallback()
    {
        await SignInAsync(); await GrantPlatformAsync();
        using var create = await _client.PostAsJsonAsync("/api/platform/dictionary", new { category = "task", code = "Ready", languageKey = "task.ready" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var result = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = result.GetProperty("id").GetGuid();
        var version = result.GetProperty("version").GetString();
        using var translation = await _client.PutAsJsonAsync($"/api/platform/dictionary/{id:D}/translation", new { expectedVersion = version, language = "zh-CN", label = "就绪" });
        Assert.Equal(HttpStatusCode.OK, translation.StatusCode);
        using var read = await _client.GetAsync("/api/platform/dictionary?category=task&language=de-DE");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var page = await read.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal("Ready", row.GetProperty("code").GetString());
        Assert.Equal("就绪", row.GetProperty("label").GetString());
        Assert.Equal("zh-CN", row.GetProperty("resolvedLanguage").GetString());
        using var stale = await _client.DeleteAsync($"/api/platform/dictionary/{id:D}?expectedVersion={version}");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    /// <summary>Role and organization changes use separate explicit commands, not generated CRUD or client-supplied authority.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-006")]
    public async Task DepartmentAndRoleCommandsRequireVersionsAndDoNotIssueTenantTokens()
    {
        await SignInAsync(); await GrantPlatformAsync();
        using var create = await _client.PostAsJsonAsync("/api/platform/departments", new { code = "new-dept", name = "New department" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var department = await create.Content.ReadFromJsonAsync<JsonElement>();
        using var edit = await _client.PutAsJsonAsync($"/api/platform/departments/{department.GetProperty("id").GetGuid():D}",
            new { expectedVersion = department.GetProperty("version").GetString(), name = "Changed department", enabled = true });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var rules = new[] { new { permission = "platform.users.read", scope = (int)DataScope.Own, departments = Array.Empty<Guid>(), fields = new[] { "Id", "Name" } } };
        using var roleCreate = await _client.PostAsJsonAsync("/api/platform/roles", new { code = "reader", rules });
        Assert.Equal(HttpStatusCode.OK, roleCreate.StatusCode);
        var role = await roleCreate.Content.ReadFromJsonAsync<JsonElement>();
        using var roleEdit = await _client.PutAsJsonAsync($"/api/platform/roles/{role.GetProperty("id").GetGuid():D}",
            new { expectedVersion = role.GetProperty("version").GetString(), enabled = false, rules });
        Assert.Equal(HttpStatusCode.OK, roleEdit.StatusCode);
        using var forbiddenSwitch = await _client.PostAsJsonAsync("/api/platform/tenants/switch-check", new { targetTenantId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenSwitch.StatusCode);
        Assert.False(forbiddenSwitch.Headers.Contains("Set-Cookie"));
        using var me = await _client.GetAsync("/api/identity/me");
        Assert.Equal(_tenant, (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenantId").GetGuid());
    }

    /// <summary>Organization links are versioned owner facts; self/cyclic parents never become implicit scope inheritance.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-HTTP-009")]
    public async Task DepartmentHierarchyIsProjectedAndRejectsCyclesWithExactVersionReparenting()
    {
        await SignInAsync(); await GrantPlatformAsync();
        using var rootResponse = await _client.PostAsJsonAsync("/api/platform/departments", new { code = "root", name = "Root" });
        var root = await rootResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rootId = root.GetProperty("id").GetGuid();
        using var childResponse = await _client.PostAsJsonAsync("/api/platform/departments", new { code = "child", name = "Child", parentId = rootId });
        Assert.Equal(HttpStatusCode.OK, childResponse.StatusCode);
        var child = await childResponse.Content.ReadFromJsonAsync<JsonElement>();
        var childId = child.GetProperty("id").GetGuid();
        using var read = await _client.GetAsync("/api/platform/departments");
        var row = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == childId);
        Assert.True(row.TryGetProperty("parentId", out var parent));
        Assert.Equal(rootId, parent.GetGuid());
        Assert.Equal("Root", row.GetProperty("parentName").GetString());
        using var cycle = await _client.PutAsJsonAsync($"/api/platform/departments/{rootId:D}/parent",
            new { expectedVersion = root.GetProperty("version").GetString(), parentId = childId });
        Assert.Equal(HttpStatusCode.Forbidden, cycle.StatusCode);
        using var self = await _client.PutAsJsonAsync($"/api/platform/departments/{childId:D}/parent",
            new { expectedVersion = child.GetProperty("version").GetString(), parentId = childId });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var detached = await _client.PutAsJsonAsync($"/api/platform/departments/{childId:D}/parent",
            new { expectedVersion = child.GetProperty("version").GetString(), parentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
        using var stale = await _client.PutAsJsonAsync($"/api/platform/departments/{childId:D}/parent",
            new { expectedVersion = child.GetProperty("version").GetString(), parentId = rootId });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        await using var verify = Open();
        Assert.NotEqual(child.GetProperty("version").GetString(), (await verify.Departments.SingleAsync(item => item.Id == childId)).Version);
        Assert.Equal(3, await verify.Audit.CountAsync());
    }
    private async Task GrantPlatformAsync()
    {
        string[] fields = ["Id", "Account", "Name", "DepartmentId", "Enabled", "Version", "Roles", "Credential",
            "Code", "Rules", "DefaultLanguage", "Key", "Value", "Source", "Category", "LanguageKey", "Label",
            "Translations", "References", "Delete", "OccurredAt", "ActorId", "TargetId", "CorrelationId", "Action",
            "BeforeVersion", "AfterVersion", "Kind", "AccountDigest", "SourceDigest", "SessionId", "ParentId",
            "AffectedSubjects", "EventId", "Status", "CreatedAt", "Owner", "ContractName", "SchemaVersion", "Attempts",
            "NextAttemptAt", "PublishedAt", "LastErrorCategory"];
        string[] permissions = ["platform.users", "platform.departments", "platform.roles", "platform.tenants",
            "platform.dictionary", "platform.settings", "platform.audit", "platform.messages"];
        var grants = permissions.SelectMany(permission => new[] { ".read", ".write" }
            .Select(action => new PermissionGrant(permission + action, DataScope.All, ImmutableHashSet<Guid>.Empty,
                fields.ToImmutableHashSet(StringComparer.Ordinal)))).ToArray();
        await using var seed = Open();
        var role = new AccessRole(_tenant, "fixture-owner", grants);
        seed.Roles.Add(role);
        seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
        await seed.SaveChangesAsync();
    }

    private async Task GrantReplayAsync()
    {
        await using var seed = Open();
        var role = new AccessRole(_tenant, "fixture-message-replay",
        [
            new PermissionGrant("platform.messages.replay", DataScope.All, ImmutableHashSet<Guid>.Empty,
                ImmutableHashSet.Create(StringComparer.Ordinal, "EventId", "Status", "Attempts", "Reason"))
        ]);
        seed.Roles.Add(role);
        seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
        await seed.SaveChangesAsync();
    }

    /// <summary>Real Chromium against HTTPS Kestrel and wes_test; never a network substitute or production URL.</summary>
    [Fact]
    [Trait("CaseId", "PLATFORM-BROWSER-001")]
    public async Task RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages()
    {
        await GrantPlatformAsync();
        await GrantAuditExportAsync();
        await GrantReplayAsync();
        await GrantSummaryAsync();
        Guid settingId;
        Guid replayEventId;
        string replayPayload;
        var auditCorrelation = Guid.NewGuid();
        await using (var seed = Open())
        {
            var setting = new VersionedSetting(_tenant, "query.snapshotTtlSeconds");
            seed.Settings.Add(setting);
            var readyDictionary = new DictionaryEntry(_tenant, "task", "Ready", "task.ready");
            seed.DictionaryEntries.AddRange(readyDictionary, new DictionaryEntry(_tenant, "task", "Missing", "task.missing"));
            seed.DictionaryTranslations.AddRange(new DictionaryTranslation(_tenant, readyDictionary.Id, "zh-CN", "就绪"),
                new DictionaryTranslation(_tenant, readyDictionary.Id, "en", "Ready"));
            seed.DictionaryReferences.Add(new DictionaryReference(_tenant, readyDictionary.Id, Guid.NewGuid()));
            seed.Audit.AddRange(Enumerable.Range(0, 60).Select(index =>
                new PlatformAudit(_tenant, _user, Guid.NewGuid(), auditCorrelation,
                    PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), _clock.GetUtcNow().AddSeconds(index))));
            await seed.SaveChangesAsync();
            settingId = setting.Id;
            var replay = await seed.OutboxMessages.OrderBy(item => item.CreatedAt).ThenBy(item => item.EventId).FirstAsync();
            replayEventId = replay.EventId;
            replayPayload = replay.Payload;
            var lease = Guid.NewGuid();
            Assert.True(await new PlatformOutboxLeaseStore(seed).TryLeaseAsync(
                replayEventId, lease, _clock.GetUtcNow(), TimeSpan.FromSeconds(30)));
            replay.RecordFailure(lease, _clock.GetUtcNow(), OutboxFailureCategory.InvalidContract);
            await seed.SaveChangesAsync();
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WES.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var web = Path.Combine(root.FullName, "clients", "ops-web");
        Assert.True(File.Exists(Path.Combine(web, "dist", "index.html")), "Build the reviewed operations client before browser acceptance.");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        await using var server = new Factory(_connection, _corpus, _clock, Path.Combine(web, "dist"));
        server.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        server.StartServer();
        var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var probe = new HttpClient(handler);
        using var page = await probe.GetAsync(address + "/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = web,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(web, "node_modules", "@playwright", "test", "cli.js"));
        start.ArgumentList.Add("test"); start.ArgumentList.Add("--config=playwright.config.ts");
        start.Environment["WES_E2E_URL"] = address; start.Environment["WES_E2E_TENANT"] = _tenant.ToString("D");
        start.Environment["WES_E2E_AUDIT_CORRELATION"] = auditCorrelation.ToString("D");
        start.Environment["WES_E2E_NOW"] = _clock.GetUtcNow().ToString("O");
        start.Environment["WES_E2E_REPLAY_EVENT"] = replayEventId.ToString("D");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
        await using var verify = Open();
        Assert.NotNull((await verify.Sessions.SingleAsync()).RevokedAt);
        var changedSetting = await verify.Settings.SingleAsync(item => item.Id == settingId);
        Assert.Equal("300", changedSetting.Value);
        Assert.Equal("AuthorizedOverride", changedSetting.Source);
        Assert.Single(await verify.Audit.Where(item => item.TargetId == settingId
            && item.Action == PlatformAuditAction.SettingChanged).ToListAsync());
        var created = await verify.DirectoryUsers.SingleAsync(item => item.Account == "BROWSER-NEW");
        Assert.Equal("Browser subject", created.Name);
        Assert.False((await verify.PasswordCredentials.SingleAsync(item => item.UserId == created.Id)).Enabled);
        var role = await verify.Roles.SingleAsync(item => item.Code == "fixture-owner");
        Assert.Equal(role.Id, (await verify.Memberships.SingleAsync(item => item.UserId == created.Id)).RoleId);
        Assert.Equal(6, await verify.CommandReceipts.CountAsync());
        var zero = await verify.DictionaryEntries.SingleAsync(item => item.Code == "0");
        Assert.Equal("task", zero.Category);
        Assert.Equal("task.zero", zero.LanguageKey);
        Assert.Equal("零状态", (await verify.DictionaryTranslations.SingleAsync(item => item.EntryId == zero.Id)).Value);
        Assert.Equal(3, await verify.DictionaryEntries.CountAsync());
        var ready = await verify.DictionaryEntries.SingleAsync(item => item.Code == "Ready");
        Assert.Single(await verify.DictionaryReferences.Where(item => item.EntryId == ready.Id).ToListAsync());
        Assert.Equal(2, await verify.DictionaryTranslations.CountAsync(item => item.EntryId == ready.Id));
        Assert.Equal(3, await verify.Audit.CountAsync(item => item.Action == PlatformAuditAction.DictionaryChanged));
        Assert.Equal(1, await verify.AuditExportReceipts.CountAsync());
        Assert.Equal(1, await verify.Audit.CountAsync(item => item.Action == PlatformAuditAction.AuditExported));
        var replayed = await verify.OutboxMessages.SingleAsync(item => item.EventId == replayEventId);
        Assert.Equal(OutboxMessageStatus.Pending, replayed.Status);
        Assert.Equal(0, replayed.Attempts);
        Assert.Equal(replayPayload, replayed.Payload);
        var replayReceipt = Assert.Single(await verify.OutboxReplayReceipts.ToListAsync());
        Assert.Equal(replayEventId, replayReceipt.EventId);
        Assert.Equal("Browser reviewed contract repair", replayReceipt.Reason);
    }

    /// <summary>Independent host/provider lifetimes read the same protected key ring and continue a durable refresh family.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-011")]
    public async Task ProtectedPersistentKeyRingSurvivesHostProviderReplacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wes-keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            string cookie;
            await using (var first = new Factory(_connection, _corpus, _clock, keyRing: path))
            {
                using var client = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
                client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
                using var login = await client.PostAsJsonAsync("/api/identity/login", LoginRequest());
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
            }
            var files = Directory.GetFiles(path, "key-*.xml");
            Assert.NotEmpty(files);
            Assert.All(files, file =>
            {
                var document = XDocument.Load(file);
                var encrypted = Assert.Single(document.Descendants(), element => element.Name.LocalName == "encryptedSecret");
                Assert.Contains("DpapiXmlDecryptor", encrypted.Attribute("decryptorType")?.Value ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "masterKey");
            });
            await using (var second = new Factory(_connection, _corpus, _clock, keyRing: path))
            {
                using var client = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
                client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
                client.DefaultRequestHeaders.Add("Cookie", cookie);
                using var refresh = await client.PostAsync("/api/identity/refresh", null);
                Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
                Assert.NotEqual(cookie, Assert.Single(refresh.Headers.GetValues("Set-Cookie")).Split(';')[0]);
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(path)) File.Delete(file);
            Directory.Delete(path);
        }
    }
    private Task<HttpResponseMessage> LoginAsync(string account = "fixture", string password = Password) =>
        _client.PostAsJsonAsync("/api/identity/login", LoginRequest(account, password));
    private HttpClient Client()
    {
        var client = _factory.CreateDefaultClient(new Uri("https://localhost"), new OperationHeaders(), new CookieContainerHandler());
        client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        return client;
    }
    private sealed class OperationHeaders : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get && path.StartsWith("/api/platform/", StringComparison.Ordinal)
                && !path.EndsWith("/credential", StringComparison.Ordinal) && path != "/api/platform/tenants/switch-check"
                && !request.Headers.Contains("Idempotency-Key"))
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            return base.SendAsync(request, cancellationToken);
        }
    }
    private PlatformDbContext Open() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options, _tenant);
    private LoginSourceDbContext Sources() => new(new DbContextOptionsBuilder<LoginSourceDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesLoginSourceMigrations")).Options);

    /// <summary>Only exact fixture tenant evidence and the explicit loopback test source are cleaned, never old business tables.</summary>
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_filesRoot is not null && Directory.Exists(_filesRoot)) Directory.Delete(_filesRoot, recursive: true);
        if (File.Exists(_corpus)) File.Delete(_corpus);
        if (!_migrated) return;
        await RemoveWmsMasterDataAsync();
        await using var database = Open();
        await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_file_binding_operations WHERE TenantId = {_tenant.ToString("D")}");
        await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit_export_receipts WHERE TenantId = {_tenant.ToString("D")}");
        await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {_tenant.ToString("D")}");
        await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_login_events WHERE TenantId = {_tenant.ToString("D")}");
        await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_file_references WHERE TenantId = {_tenant.ToString("D")}");
        await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_files WHERE TenantId = {_tenant.ToString("D")}");
        database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
        database.PasswordCredentials.RemoveRange(await database.PasswordCredentials.ToListAsync());
        database.LoginAccounts.RemoveRange(await database.LoginAccounts.ToListAsync());
        database.LoginChallenges.RemoveRange(await database.LoginChallenges.ToListAsync());
        database.CommandReceipts.RemoveRange(await database.CommandReceipts.ToListAsync());
        database.Memberships.RemoveRange(await database.Memberships.ToListAsync());
        database.Roles.RemoveRange(await database.Roles.ToListAsync());
        database.Settings.RemoveRange(await database.Settings.ToListAsync());
        database.DictionaryReferences.RemoveRange(await database.DictionaryReferences.ToListAsync());
        database.DictionaryTranslations.RemoveRange(await database.DictionaryTranslations.ToListAsync());
        database.DictionaryEntries.RemoveRange(await database.DictionaryEntries.ToListAsync());
        database.DirectoryUsers.RemoveRange(await database.DirectoryUsers.ToListAsync());
        database.Departments.RemoveRange(await database.Departments.ToListAsync());
        database.DirectoryTenants.RemoveRange(await database.DirectoryTenants.ToListAsync());
        await database.SaveChangesAsync();
        await using var sources = Sources();
        var key = LoginSourceKey.FromTrustedPeer(IPAddress.Loopback);
        sources.Sources.RemoveRange(await sources.Sources.Where(item => item.SourceDigest == key.Digest).ToListAsync());
        await sources.SaveChangesAsync();
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 4, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
    private sealed class Factory(string connection, string corpus, TimeProvider clock, string? webRoot = null, string? keyRing = null,
        string? filesRoot = null, int scannerPort = 3310, bool wmsMasterData = false, bool wmsWarehouseCreation = false,
        bool wmsLocationCreation = false, Action<IServiceCollection>? locationSchemas = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wes:Identity:Enabled"] = "true",
                ["Wes:Identity:ConnectionString"] = connection,
                ["Wes:Identity:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
                ["Wes:Identity:ChallengeKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Wes:Identity:Issuer"] = "wes-fixture",
                ["Wes:Identity:Audience"] = "wes-ops",
                ["Wes:Identity:BreachCorpusPath"] = corpus,
                ["Wes:Web:Root"] = webRoot,
                ["Wes:Identity:KeyRingPath"] = keyRing,
                ["Wes:WmsMasterData:Enabled"] = wmsMasterData.ToString(),
                ["Wes:WmsMasterData:WarehouseCreationEnabled"] = wmsWarehouseCreation.ToString(),
                ["Wes:WmsMasterData:LocationCreationEnabled"] = wmsLocationCreation.ToString(),
                ["Features:Modules:Wms"] = wmsMasterData.ToString()
            }));
            if (filesRoot is not null)
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Wes:Files:Enabled"] = "true",
                    ["Wes:Files:StorageRoot"] = filesRoot,
                    ["Features:Modules:Platform"] = "true",
                    ["Wes:Files:ScannerAddress"] = "127.0.0.1",
                    ["Wes:Files:ScannerPort"] = scannerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }));
            builder.ConfigureServices(services =>
            {
                locationSchemas?.Invoke(services);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                if (filesRoot is not null || wmsMasterData)
                {
                    // The test factory adds configuration after top-level composition captures the deployment catalog.
                    services.RemoveAll<Wes.Host.Runtime.FeatureSwitches>();
                    services.RemoveAll<Microsoft.Extensions.Options.IOptions<Wes.Host.Runtime.FeatureSwitches>>();
                    services.AddSingleton(provider => Wes.Host.Runtime.FeatureSwitchConfiguration.Load(provider.GetRequiredService<IConfiguration>()));
                    services.AddSingleton(provider => Microsoft.Extensions.Options.Options.Create(provider.GetRequiredService<Wes.Host.Runtime.FeatureSwitches>()));
                }
                services.AddSingleton<IStartupFilter, PeerFixture>();
                if (keyRing is null) services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            });
        }
    }
    private sealed class PeerFixture : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(nextRequest => http =>
                {
                    http.Connection.RemoteIpAddress = IPAddress.Loopback;
                    return nextRequest(http);
                });
                next(app);
            };
    }
}

/// <summary>All HTTP identity fixtures share the explicit loopback peer source budget and run serially.</summary>
[CollectionDefinition("IdentityHttp")]
public sealed class IdentityHttpCollection;
