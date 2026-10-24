using Microsoft.AspNetCore.Routing;

namespace Wes.Host.Runtime;

/// <summary>
/// Owns transport correlation and safe error mapping, not business outcomes.
/// Only allowlisted metadata enters logs; exception messages and request payloads never do.
/// </summary>
public sealed class HttpRuntimeBoundary(RequestDelegate next, ILogger<HttpRuntimeBoundary> logger)
{
    /// <summary>
    /// Propagates one validated UUID for this asynchronous request and maps unstarted failures.
    /// Client cancellation is not a business failure; an already started response is never rewritten.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var header = context.Request.Headers["X-Correlation-Id"];
        var correlation = header.Count == 1 && Guid.TryParseExact(header[0], "D", out var supplied)
            && supplied != Guid.Empty ? supplied : Guid.NewGuid();
        var id = correlation.ToString("D");
        context.TraceIdentifier = id;
        context.Response.Headers["X-Correlation-Id"] = id;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id });

        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400 && !context.Response.HasStarted
                && context.Response.ContentLength is null or 0 && context.Response.ContentType is null)
            {
                await WriteProblemAsync(context, context.Response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request cancelled by caller; outcome {Code}", "RequestAborted");
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 499;
            }
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            var status = error switch
            {
                TimeoutException => StatusCodes.Status503ServiceUnavailable,
                Wes.Host.Identity.BreachEvidenceUnavailableException => StatusCodes.Status503ServiceUnavailable,
                BadHttpRequestException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            };
            logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning,
                "HTTP failure {Code} with status {StatusCode}", SafeProblem.For(status).Code, status);
            context.Response.Clear();
            context.Response.Headers["X-Correlation-Id"] = id;
            await WriteProblemAsync(context, status);
        }
        catch (Exception)
        {
            logger.LogError("HTTP failure {Code}; response already started", "ResponseStartedFailure");
            throw;
        }
        finally
        {
            var method = context.Request.Method switch
            {
                "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" => context.Request.Method,
                _ => "OTHER"
            };
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            logger.LogInformation("HTTP {Method} {RouteTemplate} returned {StatusCode}", method, route, context.Response.StatusCode);
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int status)
    {
        var (code, title) = SafeProblem.For(status);
        return Results.Problem(statusCode: status, title: title, detail: title,
            type: "about:blank",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["correlationId"] = context.TraceIdentifier
            }).ExecuteAsync(context);
    }
}

internal static class SafeProblem
{
    internal static (string Code, string Title) For(int status) => status switch
    {
        400 => ("ValidationFailed", "The request is not valid."),
        401 => ("AuthenticationRequired", "Authentication is required."),
        403 => ("PermissionDenied", "This operation is not permitted."),
        404 => ("ResourceNotFound", "The requested resource was not found."),
        405 => ("MethodNotAllowed", "The request method is not supported."),
        409 => ("ResourceConflict", "The request conflicts with current state."),
        412 => ("VersionConflict", "The resource version has changed."),
        422 => ("UnsupportedCapability", "The requested capability is not supported."),
        428 => ("VersionRequired", "A resource version is required."),
        429 => ("RateLimited", "The request limit has been reached."),
        503 => ("DependencyUnavailable", "A required dependency is unavailable."),
        >= 500 => ("InternalError", "An unexpected error occurred."),
        _ => ("RequestRejected", "The request could not be completed.")
    };
}
