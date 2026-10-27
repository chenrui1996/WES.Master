using Wes.Host.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Composition and read-only HTTP adaptation; neither inventory nor resource-authorization rules belong here.</summary>
public static class WmsMasterDataEndpoints
{
    /// <summary>Registers immutable deployment settings and public owner adapters without opening a database or applying migrations.</summary>
    public static IServiceCollection AddWesWmsMasterData(this IServiceCollection services)
    {
        services.AddSingleton(provider => WmsMasterDataSettings.Load(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<MasterDataAuthority>();
        services.AddSingleton<MasterDataDatabase>();
        return services;
    }

    /// <summary>Maps authenticated catalog/detail GETs only when explicitly assembled; no creation, activation or stock write endpoint exists.</summary>
    public static void MapWesWmsMasterData(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<WmsMasterDataSettings>().Enabled) return;
        var group = app.MapGroup("/api/wms").RequireAuthorization();
        group.AddEndpointFilter(async (invocation, next) =>
            invocation.HttpContext.Request.IsHttps ? await next(invocation) : Results.BadRequest());
        group.MapGet("/warehouses", (int? offset, int? limit, string? code, bool? enabled, Guid? warehouseId,
            HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            QueryAsync(new(offset ?? 0, limit ?? 25, code, enabled, warehouseId), http, database, location: false, ct));
        group.MapGet("/locations", (int? offset, int? limit, string? code, bool? enabled, Guid? warehouseId,
            HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            QueryAsync(new(offset ?? 0, limit ?? 25, code, enabled, warehouseId), http, database, location: true, ct));
        group.MapGet("/warehouses/{id:guid}", (Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadAsync(id, http, database, location: false, ct));
        group.MapGet("/locations/{id:guid}", (Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadAsync(id, http, database, location: true, ct));
    }

    private static async Task<IResult> QueryAsync(MasterDataQuery query, HttpContext http, MasterDataDatabase database, bool location, CancellationToken ct)
    {
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var subject = new MasterDataSubject(context.TenantId, context.ActorId, context.SessionId);
            var result = location ? await owner.QueryLocationsAsync(subject, query, ct) : await owner.QueryWarehousesAsync(subject, query, ct);
            return result.Decision switch
            {
                MasterDataReadDecision.Success => location
                    ? Results.Ok(new { rows = result.Locations, result.Offset, result.Limit })
                    : Results.Ok(new { rows = result.Warehouses, result.Offset, result.Limit }),
                MasterDataReadDecision.Forbidden => Results.Forbid(),
                MasterDataReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unknown WMS catalog decision.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS master-data access failed.");
        }
    }

    private static async Task<IResult> ReadAsync(Guid id, HttpContext http, MasterDataDatabase database, bool location, CancellationToken ct)
    {
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var subject = new MasterDataSubject(context.TenantId, context.ActorId, context.SessionId);
            var result = location ? await owner.ReadLocationAsync(subject, id, ct) : await owner.ReadWarehouseAsync(subject, id, ct);
            return result.Decision switch
            {
                MasterDataReadDecision.Success => location ? Results.Ok(result.Location) : Results.Ok(result.Warehouse),
                MasterDataReadDecision.Forbidden => Results.Forbid(),
                MasterDataReadDecision.NotFound => Results.NotFound(),
                MasterDataReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unknown WMS master-data read decision.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS master-data access failed.");
        }
    }

    private sealed class MasterDataDatabase(IdentitySettings settings, MasterDataAuthority authorization, ILoggerFactory logs)
    {
        internal WmsMasterDataScope Open(Guid tenant) => new(settings.ConnectionString, tenant, authorization, logs);
    }

    private sealed class MasterDataAuthority(IdentityDatabase database) : IMasterDataAuthorization
    {
        public async Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            var result = await owner.VisibleOwnedResourceCreatorsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                permission, requiredFields, cancellationToken);
            return new(result.Authorized, result.CreatorIds.ToHashSet());
        }

        public async Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator,
            CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleOwnedResourceFieldsAsync(
                new TenantContext(subject.TenantId, subject.ActorId, subject.SessionId), permission, subject.TenantId, creator, cancellationToken);
        }
    }
}
