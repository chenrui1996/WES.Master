using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Wes.Host.Runtime;

/// <summary>Maps the safe, anonymous client module catalog.</summary>
public static class FeatureCatalogEndpoints
{
    /// <summary>Maps the allowlisted public feature catalog endpoint.</summary>
    public static RouteHandlerBuilder MapFeatureCatalog(this WebApplication app) =>
        app.MapGet("/api/config/features", (HttpContext context, IOptions<FeatureSwitches> options) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(options.Value.ToPublicCatalog());
            })
            .AllowAnonymous();
}
