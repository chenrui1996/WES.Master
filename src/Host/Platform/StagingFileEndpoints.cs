using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Wes.Host.Identity;
using Wes.Platform;
using Wes.Platform.Features.Files;

namespace Wes.Host.Platform;

/// <summary>Opaque lifecycle fence; never a client-supplied scan result, object path or entity authorization.</summary>
public sealed record FileVersionInput(string? ExpectedVersion);

/// <summary>Raw bounded transport for unbound staging files; bound business attachments remain forbidden by the owner.</summary>
public static class StagingFileEndpoints
{
    /// <summary>Uses the existing authenticated HTTPS/client-filtered Platform group only after explicit deployment opt-in.</summary>
    public static void MapStagingFiles(this RouteGroupBuilder platform)
    {
        var group = platform.MapGroup("/files/staging");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (FileAccessException error)
            {
                return Failure(invocation.HttpContext, error.IsTransient ? 503 : 500);
            }
            catch (Exception error) when (PlatformFileAdapters.IsStorageUnavailable(error))
            {
                return Failure(invocation.HttpContext, 503);
            }
            catch (Exception error) when (PlatformFileAdapters.IsStorageIntegrityFailure(error))
            {
                return Failure(invocation.HttpContext, 500);
            }
        });
        group.MapPost("", UploadAsync);
        group.MapGet("", async (int? offset, int? limit, int? state, HttpContext http, IdentityDatabase factory,
            PlatformFileAdapters adapters, CancellationToken ct) =>
        {
            var subject = IdentityRuntime.Subject(http.User);
            await using var owner = factory.Open(subject.TenantId);
            var result = await owner.StagingFiles(adapters.Storage, adapters.Scanner).QueryAsync(subject,
                offset ?? 0, limit ?? 25, state, ct);
            return Outcome(result.Decision, result);
        });
        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IdentityDatabase factory, PlatformFileAdapters adapters,
            CancellationToken ct) =>
        {
            var subject = IdentityRuntime.Subject(http.User);
            await using var owner = factory.Open(subject.TenantId);
            var result = await owner.StagingFiles(adapters.Storage, adapters.Scanner).ReadAsync(subject, id, ct);
            return Outcome(result.Decision, result);
        });
        group.MapGet("/{id:guid}/content", async (Guid id, HttpContext http, IdentityDatabase factory,
            PlatformFileAdapters adapters, CancellationToken ct) =>
        {
            var subject = IdentityRuntime.Subject(http.User);
            await using var owner = factory.Open(subject.TenantId);
            var result = await owner.StagingFiles(adapters.Storage, adapters.Scanner)
                .DownloadAsync(subject, id, Guid.Parse(http.TraceIdentifier), ct);
            if (result.Decision != FileAccessDecision.Success) return Outcome(result.Decision, result);
            var download = result.Download ?? throw new InvalidOperationException("Successful download has no owned content.");
            http.Response.RegisterForDispose(download);
            return (IResult)new StagingFileDownload(download);
        });
        group.MapPost("/{id:guid}/scan", async (Guid id, FileVersionInput input, HttpContext http, IdentityDatabase factory,
            PlatformFileAdapters adapters, CancellationToken ct) =>
        {
            var subject = IdentityRuntime.Subject(http.User);
            await using var owner = factory.Open(subject.TenantId);
            var result = await owner.StagingFiles(adapters.Storage, adapters.Scanner).ScanAsync(subject, id, input.ExpectedVersion, ct);
            return Outcome(result.Decision, result);
        });
        group.MapDelete("/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromBody] FileVersionInput input, HttpContext http,
            IdentityDatabase factory, PlatformFileAdapters adapters, CancellationToken ct) =>
        {
            var subject = IdentityRuntime.Subject(http.User);
            await using var owner = factory.Open(subject.TenantId);
            var result = await owner.StagingFiles(adapters.Storage, adapters.Scanner).DeleteOrphanAsync(subject, id, input.ExpectedVersion, ct);
            return Outcome(result.Decision, result);
        });
    }

    private static async Task<IResult> UploadAsync(HttpContext http, IdentityDatabase factory, PlatformFileAdapters adapters,
        CancellationToken ct)
    {
        var request = http.Request;
        var operationHeader = request.Headers["Idempotency-Key"];
        var name = request.Headers["X-WES-File-Name"];
        if (operationHeader.Count != 1 || !Guid.TryParseExact(operationHeader[0], "D", out var operation)
            || operation == Guid.Empty || name.Count != 1 || string.IsNullOrWhiteSpace(name[0]) || name[0]!.Length > 3060
            || string.IsNullOrWhiteSpace(request.ContentType)
            || (request.Headers.ContentEncoding.Count != 0 && request.Headers.ContentEncoding is not (["identity"])))
            return Results.BadRequest();
        var fileName = Uri.UnescapeDataString(name[0]!);
        // Round-trip a canonical RFC3986 UTF8 value: malformed escapes/UTF8 must not become a different durable name.
        if (!Uri.EscapeDataString(fileName).Equals(name[0], StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        using var body = await FileUploadBody.ReadAsync(request.Body, request.ContentLength, ct);
        var subject = IdentityRuntime.Subject(http.User);
        await using var owner = factory.Open(subject.TenantId);
        var result = await owner.StagingFiles(adapters.Storage, adapters.Scanner).UploadAsync(subject, operation,
            Guid.Parse(http.TraceIdentifier), fileName, request.ContentType, body.Content, ct);
        return Outcome(result.Decision, result);
    }

    private static IResult Outcome(FileAccessDecision decision, object result) => decision switch
    {
        FileAccessDecision.Success => Results.Ok(result),
        FileAccessDecision.Forbidden => Results.Forbid(),
        FileAccessDecision.NotFound => Results.NotFound(),
        FileAccessDecision.Invalid => Results.BadRequest(),
        FileAccessDecision.VersionRequired => Results.StatusCode(428),
        FileAccessDecision.VersionConflict => Results.StatusCode(412),
        FileAccessDecision.IdempotencyConflict or FileAccessDecision.StateConflict or FileAccessDecision.ObsoleteAttempt
            or FileAccessDecision.TooYoung => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown staging file outcome.")
    };
    private static IResult Failure(HttpContext http, int status)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PlatformFiles")
            .LogWarning("Staging file dependency failed with status {StatusCode}.", status);
        return Results.StatusCode(status);
    }
}

/// <summary>Executes only an owner-authorized and already-audited download, clearing content on successful or failed writes.</summary>
public sealed class StagingFileDownload(FileDownload download) : IResult
{
    /// <summary>Always attachment/no-store/nosniff; no public URL, HTML execution, range or cache authorization bypass.</summary>
    public async Task ExecuteAsync(HttpContext http)
    {
        using (download)
        {
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(download.FileName);
            http.Response.Headers.ContentDisposition = disposition.ToString();
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.ContentType = download.ContentType;
            http.Response.ContentLength = download.Content.Length;
            await http.Response.Body.WriteAsync(download.Content, http.RequestAborted);
        }
    }
}
