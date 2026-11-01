using System.Net.Http.Headers;
using System.Text.Json;
using Wes.Host.Identity;
using Wes.Notifications;
using Wes.Notifications.Features.Alarms;

namespace Wes.Host.Notifications;

/// <summary>Thin HTTPS HTTP and Platform application-authority adapters. No module persistence crosses this boundary.</summary>
public static class NotificationsEndpoints
{
    /// <summary>Registers only configuration and owner-authority adapters; no migration, source adapter or worker.</summary>
    public static IServiceCollection AddWesNotifications(this IServiceCollection services)
    {
        services.AddSingleton(provider => NotificationsSettings.Load(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IAlarmAuthorization, PlatformAlarmAuthority>();
        return services;
    }

    /// <summary>Maps independently enabled authenticated no-store console routes.</summary>
    public static void MapWesNotifications(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<NotificationsSettings>().Enabled) return;
        var group = app.MapGroup("/api/notifications").RequireAuthorization();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            return invocation.HttpContext.Request.IsHttps ? await next(invocation) : Results.BadRequest();
        });
        group.MapGet("/alarms", (int? offset, int? limit, HttpContext http, CancellationToken ct) =>
            Run(http, scope => scope.QueryAlarmsAsync(Subject(http), offset ?? 0, limit ?? 50, ct)));
        group.MapGet("/alarms/{id:guid}", (Guid id, HttpContext http, CancellationToken ct) =>
            Run(http, scope => scope.ReadAlarmAsync(Subject(http), id, ct)));
        group.MapGet("/deliveries", (int? offset, int? limit, HttpContext http, CancellationToken ct) =>
            Run(http, scope => scope.QueryDeliveriesAsync(Subject(http), offset ?? 0, limit ?? 50, ct)));
        group.MapGet("/deliveries/{id:guid}", (Guid id, HttpContext http, CancellationToken ct) =>
            Run(http, scope => scope.ReadDeliveryAsync(Subject(http), id, ct)));
        group.MapPost("/alarms/manual", CreateManual);
        group.MapPost("/alarms/{id:guid}/acknowledge", (Guid id, HttpContext http, CancellationToken ct) => AlarmAction(id, "acknowledge", http, ct));
        group.MapPost("/alarms/{id:guid}/clear", (Guid id, HttpContext http, CancellationToken ct) => AlarmAction(id, "clear", http, ct));
        group.MapPost("/alarms/{id:guid}/silence", (Guid id, HttpContext http, CancellationToken ct) => AlarmAction(id, "silence", http, ct));
        group.MapPost("/deliveries/{id:guid}/retry", Retry);
    }

    private static async Task<IResult> CreateManual(HttpContext http, CancellationToken ct)
    {
        var command = await ReadBody<ManualAlarmCommand>(http,
            ["operationId", "resourceId", "code", "severity", "summary"], optional: null, ct);
        if (command is null || !OperationMatches(http, command.OperationId)) return Results.BadRequest();
        return await Run(http, scope => scope.CreateManualAsync(Subject(http), command, ct), mutation: true);
    }
    private static async Task<IResult> AlarmAction(Guid id, string kind, HttpContext http, CancellationToken ct)
    {
        var command = await ReadBody<AlarmActionCommand>(http, ["operationId", "expectedVersion", "reason"],
            kind == "silence" ? "seconds" : null, ct);
        if (command is null || !OperationMatches(http, command.OperationId)) return Results.BadRequest();
        return await Run(http, scope => kind switch
        {
            "acknowledge" => scope.AcknowledgeAsync(Subject(http), id, command, ct),
            "clear" => scope.ClearManualAsync(Subject(http), id, command, ct),
            "silence" => scope.SilenceAsync(Subject(http), id, command, ct),
            _ => throw new InvalidOperationException("Unknown alarm action.")
        }, mutation: true);
    }
    private static async Task<IResult> Retry(Guid id, HttpContext http, CancellationToken ct)
    {
        var command = await ReadBody<AlarmActionCommand>(http, ["operationId", "expectedVersion", "reason"], null, ct);
        if (command is null || !OperationMatches(http, command.OperationId)) return Results.BadRequest();
        return await Run(http, scope => scope.RetryDeliveryAsync(Subject(http), id, command, ct), mutation: true);
    }
    private static AlarmSubject Subject(HttpContext http)
    {
        var subject = IdentityRuntime.Subject(http.User);
        return new(subject.TenantId, subject.ActorId, subject.SessionId);
    }
    private static async Task<IResult> Run<T>(HttpContext http, Func<AlarmConsoleScope, Task<AlarmResult<T>>> action, bool mutation = false)
    {
        try
        {
            var subject = Subject(http);
            await using var scope = new AlarmConsoleScope(http.RequestServices.GetRequiredService<IdentitySettings>().ConnectionString,
                subject.TenantId, http.RequestServices.GetRequiredService<IAlarmAuthorization>(),
                http.RequestServices.GetRequiredService<TimeProvider>());
            var result = await action(scope);
            return result.Decision switch
            {
                "Success" => Results.Ok(result.Value),
                "Applied" or "Replayed" when mutation => Results.Ok(new { result.Decision, result.Value }),
                "Forbidden" => Results.Forbid(),
                "Invalid" => Results.BadRequest(),
                "NotFound" => Results.NotFound(),
                "Unavailable" => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Notifications external delivery adapter is unavailable."),
                "VersionConflict" or "IdempotencyConflict" => Results.Conflict(),
                _ => throw new InvalidOperationException("Unknown Notifications decision.")
            };
        }
        catch (AlarmConsoleDatabaseException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Notifications persistence is unavailable.");
        }
    }
    private static bool OperationMatches(HttpContext http, Guid operation)
    {
        if (operation == Guid.Empty || http.Request.Headers["X-WES-Client"] is not (["ops-web"] or ["pda"])) return false;
        var key = http.Request.Headers["Idempotency-Key"];
        return key.Count == 0 || key.Count == 1 && Guid.TryParseExact(key[0], "D", out var id) && id == operation;
    }
    private static async Task<T?> ReadBody<T>(HttpContext http, string[] required, string? optional, CancellationToken ct) where T : class
    {
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var content) ||
            !string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            content.CharSet is not null && !string.Equals(content.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase) ||
            http.Request.ContentLength > 4096) return null;
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(bytes, ct);
            if (read == 0) break;
            if (buffer.Length + read > 4096) return null;
            await buffer.WriteAsync(bytes.AsMemory(0, read), ct);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                required.Any(name => !names.Contains(name, StringComparer.Ordinal)) ||
                names.Any(name => !required.Contains(name, StringComparer.Ordinal) && name != optional)) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "operationId" or "resourceId" &&
                    (property.Value.ValueKind != JsonValueKind.String ||
                     !Guid.TryParseExact(property.Value.GetString(), "D", out var identity) || identity == Guid.Empty)) return null;
                if (property.Name == "seconds" && !property.Value.TryGetInt32(out _)) return null;
            }
            return document.RootElement.Deserialize<T>(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false
            });
        }
        catch (JsonException) { return null; }
    }
    private sealed class PlatformAlarmAuthority(IdentityDatabase database) : IAlarmAuthorization
    {
        public async Task<AlarmCreatorAccess> ReadCreatorsAsync(AlarmSubject subject, string permission,
            IReadOnlyCollection<string> fields, CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            HashSet<Guid>? creators = null;
            // Platform's public creator query accepts at most sixteen explicit fields per call.
            foreach (var batch in fields.Chunk(16))
            {
                var access = await owner.VisibleOwnedResourceCreatorsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                    permission, batch, cancellationToken);
                if (!access.Authorized) return new(false, new HashSet<Guid>());
                if (creators is null) creators = access.CreatorIds.ToHashSet();
                else creators.IntersectWith(access.CreatorIds);
            }
            return new(creators is not null, creators ?? new HashSet<Guid>());
        }
        public async Task<IReadOnlySet<string>> ReadFieldsAsync(AlarmSubject subject, string permission, Guid creatorId,
            CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleOwnedResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                permission, subject.TenantId, creatorId, cancellationToken);
        }
    }
}
