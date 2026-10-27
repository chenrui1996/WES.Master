using Wes.Host.Identity;
using Wes.Platform;
using Wes.Platform.Features.Audit;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Messaging;
using Wes.Platform.Features.Tenancy;

namespace Wes.Host.Platform;

/// <summary>Credential-free profile creation. Department is a requested association, not authority.</summary>
public sealed record CreateUserInput(string Account, string Name, Guid DepartmentId);
/// <summary>Exact-version profile transition; the owner resolves both current and requested department scope.</summary>
public sealed record ChangeUserInput(string? ExpectedVersion, string Name, Guid DepartmentId, bool Enabled);
/// <summary>Separate first-credential creation; evidence can only come from the server's local corpus.</summary>
public sealed record CreateCredentialInput(string? ExpectedVersion, string Password)
{
    /// <summary>Prevents incidental secret formatting.</summary>
    public override string ToString() => "CreateCredentialInput[REDACTED]";
}
/// <summary>Explicit credential shutdown guarded by its current opaque version.</summary>
public sealed record DisableCredentialInput(string ExpectedVersion);
/// <summary>Explicit membership replacement under the user's version fence.</summary>
public sealed record MembershipInput(string? ExpectedVersion, Guid[] RoleIds);
/// <summary>Named department creation; stable codes cannot be renamed.</summary>
public sealed record CreateDepartmentInput(string Code, string Name, Guid? ParentId = null);
/// <summary>Separate exact-version hierarchy edge change, never permission inheritance.</summary>
public sealed record ReparentDepartmentInput(string? ExpectedVersion, Guid? ParentId);
/// <summary>Exact-version department label/eligibility change.</summary>
public sealed record ChangeDepartmentInput(string? ExpectedVersion, string Name, bool Enabled);
/// <summary>New role with a bounded explicit grant snapshot.</summary>
public sealed record CreateRoleInput(string Code, PermissionRuleInput[] Rules);
/// <summary>Current role replacement; delegation is revalidated against both old and requested grants.</summary>
public sealed record ChangeRoleInput(string? ExpectedVersion, bool Enabled, PermissionRuleInput[] Rules);
/// <summary>Only current-tenant metadata may be edited by this route.</summary>
public sealed record ChangeTenantInput(string? ExpectedVersion, string Name, bool Enabled, string DefaultLanguage);
/// <summary>An eligibility lookup, never token issuance or an authenticated target tenant.</summary>
public sealed record SwitchInput(Guid TargetTenantId);
/// <summary>Stable dictionary identity, separated from language labels.</summary>
public sealed record CreateDictionaryInput(string Category, string Code, string LanguageKey);
/// <summary>One versioned plain-text translation.</summary>
public sealed record TranslationInput(string? ExpectedVersion, string Language, string Label);
/// <summary>One nonsecret catalog scalar; arbitrary keys, secret storage and physical approval are excluded.</summary>
public sealed record SettingInput(string? ExpectedVersion, string Value);
/// <summary>Replay precondition and operator reason; envelope content is never accepted from the caller.</summary>
public sealed record ReplayOutboxInput(int ExpectedAttempts, string Reason);
/// <summary>Bounded audit query used to create a short-lived owner-local verification snapshot.</summary>
public sealed record AuditSnapshotInput(DateTimeOffset From, DateTimeOffset To, int Limit = 25,
    int? Action = null, Guid? CorrelationId = null);
/// <summary>Explicit metadata-only columns for a fixed snapshot; no arbitrary expressions or payload selector.</summary>
public sealed record AuditExportInput(string[] Columns);

/// <summary>Named transport adapters; every use case revalidates current owner authorization and versions.</summary>
public static class PlatformEndpoints
{
    /// <summary>Maps only within explicitly enabled identity deployment; no anonymous provisioning or audit mutation exists.</summary>
    public static void MapWesPlatform(this WebApplication app)
    {
        var group = app.MapGroup("/api/platform").RequireAuthorization();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            if (!http.Request.IsHttps || (http.Request.Method != "GET"
                && http.Request.Headers["X-WES-Client"] is not (["ops-web"] or ["pda"])))
                return Results.BadRequest();
            try { return await next(invocation); }
            catch (ArgumentException)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PlatformInput")
                    .LogWarning("Platform input rejected {Code}", "ValidationFailed");
                return Results.BadRequest();
            }
        });
        if (app.Services.GetRequiredService<PlatformFileSettings>().Enabled) group.MapStagingFiles();
        group.MapGet("/users", UsersAsync);
        group.MapPost("/users", async (CreateUserInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.CreateUserAsync(subject, input.Account, input.Name, input.DepartmentId, correlation, ct)));
        group.MapPut("/users/{id:guid}", async (Guid id, ChangeUserInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.ChangeUserAsync(subject, id, input.ExpectedVersion, input.Name, input.DepartmentId, input.Enabled, correlation, ct)));
        group.MapPost("/users/{id:guid}/credential", CreateCredentialAsync);
        group.MapPost("/users/{id:guid}/disable", DisableCredentialAsync);
        group.MapPut("/users/{id:guid}/roles", async (Guid id, MembershipInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            input.RoleIds is null || input.RoleIds.Length > 100 ? Results.BadRequest() :
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.ChangeMembershipsAsync(subject, id, input.ExpectedVersion, input.RoleIds, correlation, ct)));
        group.MapGet("/departments/references", DepartmentReferencesAsync);
        group.MapGet("/departments", DepartmentsAsync);
        group.MapPost("/departments", async (CreateDepartmentInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.CreateDepartmentAsync(subject, input.Code, input.Name, correlation, ct, input.ParentId)));
        group.MapPut("/departments/{id:guid}/parent", async (Guid id, ReparentDepartmentInput input, HttpContext http,
            IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.ReparentDepartmentAsync(subject, id, input.ExpectedVersion, input.ParentId, correlation, ct)));
        group.MapPut("/departments/{id:guid}", async (Guid id, ChangeDepartmentInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.ChangeDepartmentAsync(subject, id, input.ExpectedVersion, input.Name, input.Enabled, correlation, ct)));
        group.MapGet("/roles/references", RoleReferencesAsync);
        group.MapGet("/roles", RolesAsync);
        group.MapPost("/roles", async (CreateRoleInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.CreateRoleAsync(subject, input.Code, input.Rules, correlation, ct)));
        group.MapPut("/roles/{id:guid}", ChangeRoleAsync);
        group.MapGet("/tenants", TenantsAsync);
        group.MapPut("/tenants/current", async (ChangeTenantInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.ChangeTenantAsync(subject, input.ExpectedVersion, input.Name, input.Enabled, input.DefaultLanguage, correlation, ct)));
        group.MapPost("/tenants/switch-check", SwitchAsync);
        group.MapGet("/dictionary", DictionaryAsync);
        group.MapPost("/dictionary", async (CreateDictionaryInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.CreateDictionaryAsync(subject, input.Category, input.Code, input.LanguageKey, correlation, ct)));
        group.MapPut("/dictionary/{id:guid}/translation", async (Guid id, TranslationInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.ChangeTranslationAsync(subject, id, input.ExpectedVersion, input.Language, input.Label, correlation, ct)));
        group.MapDelete("/dictionary/{id:guid}", async (Guid id, string? expectedVersion, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct) =>
            await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
                owner.Directory.DeleteDictionaryAsync(subject, id, expectedVersion, correlation, ct)));
        group.MapGet("/settings", SettingsAsync);
        group.MapPut("/settings/{id:guid}", ChangeSettingAsync);
        group.MapGet("/messages", OutboxAsync);
        group.MapPost("/messages/{eventId:guid}/replay", ReplayOutboxAsync);
        group.MapGet("/audit", AuditAsync);
        group.MapPost("/audit/snapshots", CreateAuditSnapshotAsync);
        group.MapGet("/audit/snapshots/{snapshotId:guid}", ReadAuditSnapshotAsync);
        group.MapPost("/audit/snapshots/{snapshotId:guid}/export", ExportAuditSnapshotAsync);
        group.MapGet("/login-events", LoginEventsAsync);
    }

    private static async Task<IResult> UsersAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct,
        string? account = null, string? name = null, Guid? departmentId = null, bool? enabled = null, int offset = 0, int limit = 50)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.Directory.ReadUsersAsync(subject,
            new UserDirectoryFilter(account, name, departmentId, enabled, offset, limit), ct);
        return Page(result);
    }
    private static async Task<IResult> DepartmentsAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct,
        int offset = 0, int limit = 50, string? code = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Page(await database.Directory.ReadDepartmentsAsync(subject, offset, limit, ct, code));
    }
    private static async Task<IResult> DepartmentReferencesAsync(HttpContext http, IdentityDatabase factory,
        CancellationToken ct, Guid[]? ids = null)
    {
        if (ids is null || ids.Length is 0 or > 100 || ids.Contains(Guid.Empty)) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Page(await database.Directory.ReadDepartmentReferencesAsync(subject, ids, ct));
    }
    private static async Task<IResult> RolesAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct,
        int offset = 0, int limit = 50, string? code = null, string? permission = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Page(await database.Directory.ReadRolesAsync(subject, offset, limit, ct, code, permission));
    }
    private static async Task<IResult> RoleReferencesAsync(HttpContext http, IdentityDatabase factory,
        CancellationToken ct, Guid[]? ids = null)
    {
        if (ids is null || ids.Length is 0 or > 100 || ids.Contains(Guid.Empty)) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Page(await database.Directory.ReadRoleReferencesAsync(subject, ids, ct));
    }
    private static async Task<IResult> TenantsAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct,
        int offset = 0, int limit = 50, Guid? tenantId = null, bool? enabled = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Page(await database.Directory.ReadTenantsAsync(subject, offset, limit, ct, tenantId, enabled));
    }
    private static async Task<IResult> DictionaryAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct,
        string category, string language = "zh-CN", int offset = 0, int limit = 50, bool? missingTranslation = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return Page(await database.Directory.ReadDictionaryAsync(subject, category, language, offset, limit, ct, missingTranslation));
    }
    private static async Task<IResult> SettingsAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct,
        int offset = 0, int limit = 50, string? kind = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.SettingsAsync(subject, offset, limit, ct, kind);
        return result.Authorized ? Results.Ok(result) : Results.Forbid();
    }
    private static async Task<IResult> SwitchAsync(SwitchInput input, HttpContext http, IdentityDatabase factory, TimeProvider clock, CancellationToken ct)
    {
        if (input.TargetTenantId == Guid.Empty) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.Directory.CheckTenantSwitchAsync(subject, input.TargetTenantId, ct);
        return result.Authorized ? Results.Ok(result) : Results.Forbid();
    }
    private static async Task<IResult> CreateCredentialAsync(Guid id, CreateCredentialInput input, HttpContext http, IdentityDatabase factory,
        TimeProvider clock, LocalBreachCorpus breach, CancellationToken ct)
    {
        if (input.Password is null or { Length: > 512 } || http.Request.Headers.ContainsKey("Idempotency-Key")) return Results.BadRequest();
        var evidence = await breach.CheckAsync(input.Password, ct);
        return await WriteAsync(http, factory, clock, (owner, subject, correlation) =>
            owner.CreateCredentialAsync(subject, id, input.ExpectedVersion, input.Password, evidence, correlation, ct), receipted: false);
    }
    private static async Task<IResult> DisableCredentialAsync(Guid id, DisableCredentialInput input, HttpContext http,
        IdentityDatabase factory, TimeProvider clock, CancellationToken ct)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var decision = await database.DisableAccountAsync(subject, id, input.ExpectedVersion, Guid.Parse(http.TraceIdentifier), ct);
        return decision switch
        {
            AccountDisableDecision.Completed => Results.NoContent(),
            AccountDisableDecision.Rejected => Results.Forbid(),
            AccountDisableDecision.VersionConflict => Results.StatusCode(412),
            _ => throw new InvalidOperationException("Unknown account-disable outcome.")
        };
    }
    private static async Task<IResult> ChangeRoleAsync(Guid id, ChangeRoleInput input, HttpContext http, IdentityDatabase factory,
        TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId, Operation(http));
        var result = await database.ChangeRoleAsync(subject, id, input.ExpectedVersion, input.Enabled, input.Rules, Guid.Parse(http.TraceIdentifier), ct);
        return SettingResult(result.Decision, id, result.Version);
    }
    private static async Task<IResult> ChangeSettingAsync(Guid id, SettingInput input, HttpContext http, IdentityDatabase factory,
        TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (input.Value is null or { Length: > 128 }) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId, Operation(http));
        var result = await database.ChangeSettingAsync(subject, id, input.ExpectedVersion, input.Value, Guid.Parse(http.TraceIdentifier), ct);
        return SettingResult(result.Decision, id, result.Version);
    }
    private static async Task<IResult> AuditAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, ILoggerFactory logs, CancellationToken ct,
        DateTimeOffset from, DateTimeOffset to, int offset = 0, int limit = 100, int? action = null, Guid? correlationId = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.AuditAsync(subject, from, to, offset, limit, ct, action, correlationId);
        return result.Authorized ? Results.Ok(result) : Results.Forbid();
    }
    private static async Task<IResult> CreateAuditSnapshotAsync(AuditSnapshotInput input, HttpContext http,
        IdentityDatabase factory, CancellationToken ct)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return AuditSnapshotResponse(await database.CreateAuditSnapshotAsync(subject, input.From, input.To,
            input.Limit, ct, input.Action, input.CorrelationId));
    }
    private static async Task<IResult> ReadAuditSnapshotAsync(Guid snapshotId, HttpContext http,
        IdentityDatabase factory, CancellationToken ct, int offset = 0, int limit = 25)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        return AuditSnapshotResponse(await database.ReadAuditSnapshotAsync(subject, snapshotId, offset, limit, ct));
    }
    private static IResult AuditSnapshotResponse(AuditSnapshotResult result) => result.Decision switch
    {
        AuditSnapshotDecision.Success => Results.Ok(result.Page),
        AuditSnapshotDecision.Forbidden => Results.Forbid(),
        AuditSnapshotDecision.NotFound => Results.NotFound(),
        AuditSnapshotDecision.Expired => Results.StatusCode(StatusCodes.Status410Gone),
        AuditSnapshotDecision.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
        _ => throw new InvalidOperationException("Unknown audit snapshot outcome.")
    };
    private static async Task<IResult> ExportAuditSnapshotAsync(Guid snapshotId, AuditExportInput input, HttpContext http,
        IdentityDatabase factory, CancellationToken ct)
    {
        if (input.Columns is null) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.ExportAuditSnapshotAsync(subject, snapshotId, Operation(http).OperationId,
            input.Columns, Guid.Parse(http.TraceIdentifier), ct);
        if (result.Decision != AuditExportDecision.Success) return result.Decision switch
        {
            AuditExportDecision.Forbidden => Results.Forbid(),
            AuditExportDecision.NotFound => Results.NotFound(),
            AuditExportDecision.Expired => Results.StatusCode(StatusCodes.Status410Gone),
            AuditExportDecision.IdempotencyConflict => Results.Conflict(),
            _ => throw new InvalidOperationException("Unknown audit export outcome.")
        };
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(result.Content!, "text/csv; charset=utf-8", $"audit-{snapshotId:D}.csv", enableRangeProcessing: false);
    }
    private static async Task<IResult> OutboxAsync(HttpContext http, IdentityDatabase factory, CancellationToken ct,
        int offset = 0, int limit = 50, int? status = null, DateTimeOffset? createdFrom = null, DateTimeOffset? createdBefore = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.OutboxAsync(subject, offset, limit, ct, new OutboxQueryFilter(status, createdFrom, createdBefore));
        return result.Authorized ? Results.Ok(result) : Results.Forbid();
    }
    private static async Task<IResult> ReplayOutboxAsync(Guid eventId, ReplayOutboxInput input, HttpContext http,
        IdentityDatabase factory, CancellationToken ct)
    {
        var subject = IdentityRuntime.Subject(http.User);
        var operation = Operation(http);
        await using var database = factory.Open(subject.TenantId, operation);
        var result = await database.ReplayOutboxAsync(subject, operation, eventId,
            input.ExpectedAttempts, input.Reason, ct);
        return result.Decision switch
        {
            OutboxReplayDecision.Replayed => Results.Ok(result),
            OutboxReplayDecision.Forbidden => Results.Forbid(),
            OutboxReplayDecision.NotFound => Results.NotFound(),
            OutboxReplayDecision.Conflict or OutboxReplayDecision.NotReplayable
                or OutboxReplayDecision.IdempotencyConflict => Results.Conflict(),
            _ => throw new InvalidOperationException("Unknown Outbox replay outcome.")
        };
    }
    private static async Task<IResult> LoginEventsAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock, ILoggerFactory logs, CancellationToken ct,
        DateTimeOffset from, DateTimeOffset to, int offset = 0, int limit = 100, LoginEventKind? kind = null, Guid? correlationId = null)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId);
        var result = await database.LoginEventsAsync(subject, from, to, offset, limit, kind, correlationId, ct);
        return result.Authorized ? Results.Ok(result) : Results.Forbid();
    }
    private static IResult Page<T>(DirectoryPage<T> page) => page.Authorized ? Results.Ok(page) : Results.Forbid();
    private static async Task<IResult> WriteAsync(HttpContext http, IdentityDatabase factory, TimeProvider clock,
        Func<PlatformScope, TenantContext, Guid, Task<DirectoryWriteResult>> command, bool receipted = true)
    {
        var subject = IdentityRuntime.Subject(http.User);
        await using var database = factory.Open(subject.TenantId, receipted ? Operation(http) : null);
        var result = await command(database, subject, Guid.Parse(http.TraceIdentifier));
        return result.Decision switch
        {
            DirectoryDecision.Changed => Results.Ok(result),
            DirectoryDecision.Forbidden => Results.Forbid(),
            DirectoryDecision.VersionRequired => Results.StatusCode(428),
            DirectoryDecision.VersionConflict => Results.StatusCode(412),
            DirectoryDecision.Duplicate or DirectoryDecision.Referenced or DirectoryDecision.IdempotencyConflict => Results.Conflict(),
            DirectoryDecision.PasswordRejected => Results.BadRequest(),
            _ => throw new InvalidOperationException("Unknown directory outcome.")
        };
    }
    private static IResult SettingResult(PlatformDecision decision, Guid id, string? version) => decision switch
    {
        PlatformDecision.Changed => Results.Ok(new { decision = DirectoryDecision.Changed, id, version }),
        PlatformDecision.Forbidden => Results.Forbid(),
        PlatformDecision.VersionRequired => Results.StatusCode(428),
        PlatformDecision.VersionConflict => Results.StatusCode(412),
        PlatformDecision.NotFound => Results.NotFound(),
        PlatformDecision.Invalid => Results.BadRequest(),
        PlatformDecision.Unverified => Results.UnprocessableEntity(),
        PlatformDecision.IdempotencyConflict => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown setting outcome.")
    };
    private static DirectoryOperation Operation(HttpContext http)
    {
        var value = http.Request.Headers["Idempotency-Key"];
        if (value.Count != 1 || !Guid.TryParseExact(value[0], "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("A canonical nonempty operation UUID is required.");
        return new(id);
    }
}
