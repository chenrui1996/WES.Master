using Microsoft.Extensions.FileProviders;

namespace Wes.Host.Runtime;

/// <summary>Explicit same-origin operations bundle hosting; never maps a source tree or hides unknown API routes with HTML.</summary>
public static class OperationsWeb
{
    /// <summary>Serves only a deployment-supplied built bundle and fixed SPA fallback, with HTTPS and browser isolation headers.</summary>
    public static void MapOperationsWeb(this WebApplication app)
    {
        var root = app.Configuration["Wes:Web:Root"];
        if (string.IsNullOrWhiteSpace(root)) return;
        if (!Path.IsPathFullyQualified(root) || !File.Exists(Path.Combine(root, "index.html")))
            throw new InvalidOperationException("Operations web root must be an explicit absolute built-bundle directory.");
        var files = new PhysicalFileProvider(root);
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        app.Use(async (http, next) =>
        {
            if (!http.Request.IsHttps && !http.Request.Path.StartsWithSegments("/health"))
            { http.Response.StatusCode = 400; return; }
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(http);
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = response => response.Context.Response.Headers.CacheControl =
                response.File.Name == "index.html" ? "no-store" : "public, max-age=3600"
        });
        app.MapFallback(async http =>
        {
            if (http.Request.Path.StartsWithSegments("/api"))
            { await Results.NotFound().ExecuteAsync(http); return; }
            http.Response.Headers.CacheControl = "no-store";
            await Results.File(Path.Combine(root, "index.html"), "text/html; charset=utf-8").ExecuteAsync(http);
        });
    }
}
