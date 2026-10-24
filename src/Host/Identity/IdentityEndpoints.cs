using System.Net;
using Wes.Platform;
using Wes.Platform.Features.Identity;

namespace Wes.Host.Identity;

/// <summary>Transport-only anonymous credential input; no preverified flags or caller-defined actor/grants.</summary>
public sealed record LoginInput(Guid TenantId, string Account, string Password, IdentityClient Client = IdentityClient.Web,
    Guid? ChallengeId = null, string? Answer = null, string? ReturnUrl = null)
{
    /// <summary>Credential-bearing transport values never participate in incidental string formatting.</summary>
    public override string ToString() => "LoginInput[REDACTED]";
}
/// <summary>Anonymous challenge routing, not an authenticated tenant claim.</summary>
public sealed record ChallengeInput(Guid TenantId, string Account);
/// <summary>Separate current-subject credential operation; breach/history evidence is exclusively server-owned.</summary>
public sealed record PasswordInput(string ExpectedVersion, string OldPassword, string NewPassword)
{
    /// <summary>Neither old nor new secret may appear in incidental record formatting.</summary>
    public override string ToString() => "PasswordInput[REDACTED]";
}
/// <summary>Opaque current family version only; ownership is read from the tenant owner.</summary>
public sealed record RevokeInput(string ExpectedVersion);

/// <summary>Thin HTTP adapters over the reviewed identity owners; no passwords or refresh secrets enter logs/URLs.</summary>
public static class IdentityEndpoints
{
    /// <summary>Maps only after explicit deployment opt-in and rejects unsafe transport before credential handlers run.</summary>
    public static void MapWesIdentity(this WebApplication app)
    {
        var group = app.MapGroup("/api/identity");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps || (http.Request.Method != "GET"
                && http.Request.Headers["X-WES-Client"] is not (["ops-web"] or ["pda"])))
                return Results.BadRequest();
            try { return await next(invocation); }
            catch (ArgumentException)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityInput")
                    .LogWarning("Identity input rejected {Code}", "ValidationFailed");
                return Results.BadRequest();
            }
        });
        group.MapPost("/challenge", ChallengeAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapGet("/me", MeAsync).RequireAuthorization();
        group.MapGet("/sessions", SessionsAsync).RequireAuthorization();
        group.MapGet("/admin-sessions", AdministrativeSessionsAsync).RequireAuthorization();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/password", PasswordAsync).RequireAuthorization();
        group.MapPost("/activity", ActivityAsync).RequireAuthorization();
        group.MapPost("/sessions/{id:guid}/revoke", RevokeAsync).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(LoginInput input, HttpContext http, IdentityDatabase factory,
        LoginChallengeProtection protection, IdentityTokens tokens, CancellationToken ct)
    {
        if (!TryAccount(input.TenantId, input.Account, out var account) || input.Password is null or { Length: > 512 }
            || !Enum.IsDefined(input.Client) || input.Answer is { Length: > 128 }) return Results.BadRequest();
        var peer = Peer(http);
        if (peer is null) return Results.BadRequest();
        await using var database = factory.Open(input.TenantId);
        var result = await database.LoginAsync(account, input.Password, peer, input.Client,
            Guid.Parse(http.TraceIdentifier), protection, input.ChallengeId, input.Answer, ct);
        if (result.Delivery is not { } delivery) return Results.Unauthorized();
        return Results.Ok(tokens.Deliver(http, input.TenantId, delivery.UserId, delivery.SessionId, delivery.RequiresPasswordChange,
            delivery.Secret, SafeReturn(input.ReturnUrl)));
    }

    private static async Task<IResult> ChallengeAsync(ChallengeInput input, HttpContext http, IdentityDatabase factory,
        LoginChallengeProtection protection, CancellationToken ct)
    {
        if (!TryAccount(input.TenantId, input.Account, out var account) || Peer(http) is not { } peer) return Results.BadRequest();
        await using var database = factory.Open(input.TenantId);
        var result = await database.ChallengeAsync(account, peer, protection, ct);
        if (!result.Admitted || result.Delivery is not { } delivery) return Results.StatusCode(429);
        return Results.Ok(new { delivery.Id, delivery.ExpiresAt, imageDataUrl = "data:image/png;base64," + Convert.ToBase64String(ChallengePng.Render(delivery.Answer)) });
    }

    private static async Task<IResult> RefreshAsync(HttpContext http, IdentityDatabase factory,
        IdentityTokens tokens, CancellationToken ct)
    {
        if (tokens.ReadCookie(http) is not { } cookie)
        { IdentityTokens.ClearCookie(http); return Results.Unauthorized(); }
        await using var database = factory.Open(cookie.Tenant);
        var result = await database.RefreshAsync(cookie.User, cookie.Session, cookie.Secret, ct);
        if (!result.Completed || result.Secret is null)
        { IdentityTokens.ClearCookie(http); return Results.Unauthorized(); }
        return Results.Ok(tokens.Deliver(http, cookie.Tenant, cookie.User, cookie.Session, result.RequiresPasswordChange, result.Secret, "/account/sessions"));
    }

    private static async Task<IResult> MeAsync(HttpContext http, IdentityDatabase factory, CancellationToken ct)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Results.Ok(await database.SubjectAsync(subject, ct));
    }

    private static async Task<IResult> SessionsAsync(HttpContext http, IdentityDatabase factory, CancellationToken ct,
        string? state = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int offset = 0, int limit = 50)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Results.Ok(await database.SessionsAsync(subject, new SessionQuery(state, from, to, offset, limit), ct));
    }
    private static async Task<IResult> AdministrativeSessionsAsync(HttpContext http, IdentityDatabase factory, CancellationToken ct,
        string? state = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int offset = 0, int limit = 50, Guid? userId = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.AdministrativeSessionsAsync(subject, new SessionQuery(state, from, to, offset, limit, userId), ct);
        return result.Authorized ? Results.Ok(result) : Results.Forbid();
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, IdentityDatabase factory, CancellationToken ct)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.LogoutAsync(subject, Guid.Parse(http.TraceIdentifier), ct);
        IdentityTokens.ClearCookie(http);
        return result == LogoutDecision.Completed ? Results.NoContent() : Results.Unauthorized();
    }

    private static async Task<IResult> PasswordAsync(PasswordInput input, HttpContext http, IdentityDatabase factory,
        LocalBreachCorpus breach, CancellationToken ct)
    {
        if (!Guid.TryParseExact(input.ExpectedVersion, "D", out var version) || version == Guid.Empty
            || input.OldPassword is null or { Length: > 512 } || input.NewPassword is null or { Length: > 512 }) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        var evidence = await breach.CheckAsync(input.NewPassword, ct);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.PasswordAsync(subject, input.ExpectedVersion, input.OldPassword, input.NewPassword,
            evidence, Guid.Parse(http.TraceIdentifier), ct);
        if (result.Decision == PlatformDecision.Changed)
        { IdentityTokens.ClearCookie(http); return Results.NoContent(); }
        return result.Decision switch
        {
            PlatformDecision.VersionConflict => Results.StatusCode(412),
            PlatformDecision.Invalid => Results.BadRequest(),
            _ => Results.Unauthorized()
        };
    }

    private static async Task<IResult> ActivityAsync(HttpContext http, IdentityDatabase factory, CancellationToken ct)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var decision = await database.ActivityAsync(subject, ct);
        return decision switch
        {
            ActivityDecision.Recorded => Results.NoContent(),
            ActivityDecision.VersionConflict => Results.StatusCode(412),
            _ => Results.Unauthorized()
        };
    }

    private static async Task<IResult> RevokeAsync(Guid id, RevokeInput input, HttpContext http, IdentityDatabase factory,
        CancellationToken ct)
    {
        if (id == Guid.Empty || !Guid.TryParseExact(input.ExpectedVersion, "D", out var version) || version == Guid.Empty)
            return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.RevokeAsync(subject, id, input.ExpectedVersion, Guid.Parse(http.TraceIdentifier), ct);
        return result switch
        {
            SessionRevokeDecision.Completed => Results.NoContent(),
            SessionRevokeDecision.VersionConflict => Results.StatusCode(412),
            _ => Results.Forbid()
        };
    }

    private static bool TryAccount(Guid tenant, string? input, out string account)
    {
        account = "";
        if (tenant == Guid.Empty || input is null or { Length: > 512 }) return false;
        try { account = AccountName.Normalize(input); return true; }
        catch (ArgumentException) { return false; }
    }
    private static IPAddress? Peer(HttpContext http) => http.Connection.RemoteIpAddress;
    /// <summary>Fixed route allowlist prevents external/encoded/protocol-relative redirect targets.</summary>
    public static string SafeReturn(string? target) => target is "/account/sessions" or "/admin/users" or "/admin/roles"
        or "/admin/tenants" or "/admin/configuration" or "/admin/audit" ? target : "/account/sessions";
}
