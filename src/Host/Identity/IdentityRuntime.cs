using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Wes.Platform;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;

namespace Wes.Host.Identity;

/// <summary>Explicit deployment-owned identity settings; never diagnostic-formatted or populated with default secrets.</summary>
public sealed class IdentitySettings
{
    /// <summary>Owner connection, supplied only by server configuration.</summary>
    public required string ConnectionString { get; init; }
    /// <summary>HS512 signing material, at least 512 independent bits.</summary>
    public required byte[] SigningKey { get; init; }
    /// <summary>Independent challenge MAC material, at least 256 bits.</summary>
    public required byte[] ChallengeKey { get; init; }
    /// <summary>Exact issuer binding.</summary>
    public required string Issuer { get; init; }
    /// <summary>Exact audience binding.</summary>
    public required string Audience { get; init; }
    /// <summary>Deployment-reviewed local SHA256 corpus, never an HTTP-supplied path.</summary>
    public required string BreachCorpusPath { get; init; }
    /// <summary>Rejects incomplete or reused signing/MAC secrets before the first credential operation.</summary>
    public static IdentitySettings Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("Wes:Identity");
        string Required(string key) => !string.IsNullOrWhiteSpace(section[key]) ? section[key]!
            : throw new InvalidOperationException("Identity deployment configuration is incomplete.");
        byte[] Key(string name, int length)
        {
            if (!Convert.TryFromBase64String(Required(name), new Span<byte>(new byte[1024]), out _))
                throw new InvalidOperationException("Identity deployment key encoding is invalid.");
            var key = Convert.FromBase64String(Required(name));
            if (key.Length < length || key.Length > 256) throw new InvalidOperationException("Identity deployment key length is invalid.");
            return key;
        }
        var signing = Key("SigningKey", 64);
        var challenge = Key("ChallengeKey", 32);
        if (signing.AsSpan().SequenceEqual(challenge)) throw new InvalidOperationException("Identity purposes require independent keys.");
        return new IdentitySettings
        {
            ConnectionString = Required("ConnectionString"),
            SigningKey = signing,
            ChallengeKey = challenge,
            Issuer = Required("Issuer"),
            Audience = Required("Audience"),
            BreachCorpusPath = Required("BreachCorpusPath")
        };
    }
    /// <summary>Prevents incidental configuration/secret formatting.</summary>
    public override string ToString() => "IdentitySettings[REDACTED]";
}

/// <summary>Creates clean tenant-owner units; no request header can change an authenticated context.</summary>
public sealed class IdentityDatabase(IdentitySettings settings, TimeProvider clock, ILoggerFactory logs)
{
    /// <summary>The tenant is either verified JWT context or anonymous credential routing, never an authorization claim by itself.</summary>
    public PlatformScope Open(Guid tenant, DirectoryOperation? operation = null) => new(settings.ConnectionString, tenant, clock, logs, operation);
}

/// <summary>Read-only local breach evidence; missing/malformed deployment data fails explicitly, never clears an unknown password.</summary>
public sealed class LocalBreachCorpus(IdentitySettings settings)
{
    /// <summary>Checks the full UTF8 value against the deployment's SHA256 corpus without sending password material to a network.</summary>
    public async Task<PasswordBreachEvidence> CheckAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        cancellationToken.ThrowIfCancellationRequested();
        if (password.Length > 512) throw new ArgumentException("Password input exceeds the transport bound.");
        var bytes = new UTF8Encoding(false, true).GetBytes(password);
        string digest;
        try { digest = Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        try
        {
            using var stream = new StreamReader(settings.BreachCorpusPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var count = 0;
            var found = false;
            while (await stream.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length != 64 || line.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterLower(character)))
                    throw new BreachEvidenceUnavailableException();
                found |= line == digest;
                count++;
            }
            if (count == 0) throw new BreachEvidenceUnavailableException();
            return found ? PasswordBreachEvidence.Prohibited : PasswordBreachEvidence.Clear;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new BreachEvidenceUnavailableException();
        }
    }
}

/// <summary>A required deployment evidence source is unavailable; caller must retry later, never accept the password.</summary>
public sealed class BreachEvidenceUnavailableException() : Exception("Password security evidence is unavailable.");

/// <summary>JWT and protected browser-cookie delivery; no role claims or refresh secret JSON.</summary>
public sealed class IdentityTokens(IdentitySettings settings, TimeProvider clock, IDataProtectionProvider protection)
{
    private readonly IDataProtector _cookies = protection.CreateProtector("Wes.Identity.RefreshCookie.v1");
    /// <summary>Creates a signed 15-minute access token after its session owner committed.</summary>
    public object Deliver(HttpContext http, Guid tenant, Guid user, Guid session, bool restricted, RefreshSecret secret, string returnUrl)
    {
        var now = clock.GetUtcNow();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.ToString("D")), new Claim("tid", tenant.ToString("D")),
            new Claim("sid", session.ToString("D")),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new Claim("wti", now.UtcTicks.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        var expires = now.AddSeconds(900);
        // NumericDate uses whole seconds; round its envelope up while signed wti and the owner enforce the exact deadline.
        var envelope = expires.UtcTicks % TimeSpan.TicksPerSecond == 0 ? expires
            : expires.AddTicks(TimeSpan.TicksPerSecond - expires.UtcTicks % TimeSpan.TicksPerSecond);
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, now.UtcDateTime,
            envelope.UtcDateTime, new SigningCredentials(new SymmetricSecurityKey(settings.SigningKey), SecurityAlgorithms.HmacSha512));
        http.Response.Cookies.Append("__Host-wes-refresh", _cookies.Protect($"{tenant:D}|{user:D}|{session:D}|{secret.Value}"),
            CookieOptions());
        return new
        {
            accessToken = new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt = expires,
            requiresPasswordChange = restricted,
            returnUrl
        };
    }
    /// <summary>Unprotects only the server's refresh purpose; malformed cookies do not provide a tenant context.</summary>
    public (Guid Tenant, Guid User, Guid Session, string Secret)? ReadCookie(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue("__Host-wes-refresh", out var raw) || raw.Length > 2048) return null;
        string value;
        try { value = _cookies.Unprotect(raw); }
        catch (CryptographicException) { return null; }
        var parts = value.Split('|');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "D", out var tenant) || tenant == Guid.Empty
            || !Guid.TryParseExact(parts[1], "D", out var user) || user == Guid.Empty
            || !Guid.TryParseExact(parts[2], "D", out var session) || session == Guid.Empty || parts[3].Length != 43) return null;
        return (tenant, user, session, parts[3]);
    }
    /// <summary>Removes the same host-only secure cookie on logout/rejection/change.</summary>
    public static void ClearCookie(HttpContext http) => http.Response.Cookies.Delete("__Host-wes-refresh", CookieOptions());
    private static CookieOptions CookieOptions() => new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };
}

/// <summary>Composes strict signature/lifetime verification with per-request durable family verification.</summary>
public static class IdentityRuntime
{
    /// <summary>Registers identity without enabling endpoints, migrations, seed users or business readiness by default.</summary>
    public static IServiceCollection AddWesIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        services.AddDataProtection().SetApplicationName("WES.Master.Identity");
        services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, IHostEnvironment, ILoggerFactory>(IdentityKeyRing.Configure);
        services.AddSingleton(provider => IdentitySettings.Load(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IdentityDatabase>();
        services.AddSingleton<LocalBreachCorpus>();
        services.AddSingleton<IdentityTokens>();
        services.AddSingleton(provider => new LoginChallengeProtection(provider.GetRequiredService<IdentitySettings>().ChallengeKey));
        services.AddHttpContextAccessor();
        services.AddScoped(provider => Subject(provider.GetRequiredService<IHttpContextAccessor>().HttpContext!.User));
        services.AddAuthentication("IdentityNotConfigured")
            .AddScheme<AuthenticationSchemeOptions, DisabledIdentityHandler>("IdentityNotConfigured", _ => { })
            .AddJwtBearer();
        services.AddOptions<AuthenticationOptions>().Configure<IConfiguration>((options, config) =>
        {
            if (config.GetValue<bool>("Wes:Identity:Enabled")) options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        });
        services.AddAuthorization();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IdentitySettings, TimeProvider>((options, settings, clock) =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(settings.SigningKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha512],
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    LifetimeValidator = (notBefore, expires, _, _) => notBefore.HasValue && expires.HasValue
                        && notBefore.Value <= clock.GetUtcNow().UtcDateTime && clock.GetUtcNow().UtcDateTime < expires.Value
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async validation =>
                    {
                        var principal = validation.Principal!;
                        if (!TrySubject(principal, out var subject)
                            || !long.TryParse(principal.FindFirstValue("wti"), CultureInfo.InvariantCulture, out var ticks)
                            || ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks)
                        { validation.Fail("AuthenticationRequired"); return; }
                        var issued = new DateTimeOffset(ticks, TimeSpan.Zero);
                        if (!long.TryParse(principal.FindFirstValue("iat"), CultureInfo.InvariantCulture, out var seconds)
                            || issued.ToUnixTimeSeconds() != seconds)
                        { validation.Fail("AuthenticationRequired"); return; }
                        var factory = validation.HttpContext.RequestServices.GetRequiredService<IdentityDatabase>();
                        await using var database = factory.Open(subject.TenantId);
                        if (!await database.AcceptsAccessAsync(subject, issued, validation.HttpContext.RequestAborted))
                            validation.Fail("AuthenticationRequired");
                    }

                };
            });
        return services;
    }
    /// <summary>Retrieves only canonical verified JWT IDs; missing context is never tenant zero.</summary>
    public static TenantContext Subject(ClaimsPrincipal principal) => TrySubject(principal, out var context) ? context
        : throw new InvalidOperationException("A verified identity context is required.");
    private static bool TrySubject(ClaimsPrincipal principal, [NotNullWhen(true)] out TenantContext? context)
    {
        context = null;
        if (principal.Identity?.IsAuthenticated != true
            || !Guid.TryParseExact(principal.FindFirstValue("tid"), "D", out var tenant) || tenant == Guid.Empty
            || !Guid.TryParseExact(principal.FindFirstValue("sub"), "D", out var user) || user == Guid.Empty
            || !Guid.TryParseExact(principal.FindFirstValue("sid"), "D", out var session) || session == Guid.Empty) return false;
        context = new TenantContext(tenant, user, session);
        return true;
    }
}

/// <summary>Default non-authentication when deployment has not opted in; never invents a principal or secret.</summary>
public sealed class DisabledIdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Unconfigured identity cannot authenticate any request.</summary>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}
