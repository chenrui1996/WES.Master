using Wes.Host.Identity;
using Wes.Host.Platform;
using Wes.Platform.Features.Tenancy;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Composition and named HTTP adaptation; neither inventory nor resource-authorization rules belong here.</summary>
public static partial class WmsMasterDataEndpoints
{
    /// <summary>Registers immutable deployment settings and public owner adapters without opening a database or applying migrations.</summary>
    public static IServiceCollection AddWesWmsMasterData(this IServiceCollection services)
    {
        services.AddSingleton(provider => WmsMasterDataSettings.Load(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<MasterDataAuthority>();
        services.AddSingleton<MasterDataDatabase>();
        services.AddSingleton(provider => LoadLocationSchema(services, provider));
        services.AddSingleton<InventoryAuthority>();
        services.AddSingleton<InventoryDatabase>();
        services.AddSingleton<StockLedgerAuthority>();
        services.AddSingleton<StockLedgerDatabase>();
        services.AddSingleton<InboundAuthority>();
        services.AddSingleton<InboundDatabase>();
        services.AddSingleton<InboundDraftAuthority>();
        services.AddSingleton<InboundRevisionAuthority>();
        return services;
    }

    /// <summary>Maps authenticated reads and independently enabled owner mutations; never grants physical-control authority.</summary>
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
        group.MapGet("/inbound/{id:guid}", ReadInboundAsync);
        group.MapGet("/inbound", QueryInboundAsync);
        MapStockResourceReads(group);
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().StockResourceRegistrationEnabled)
        {
            _ = app.Services.GetRequiredService<LocationCreationSchema>();
            MapStockResourceWrites(group);
        }
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().InboundDraftCreationEnabled)
            group.MapPost("/inbound", CreateInboundDraftAsync);
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().InboundDraftRevisionEnabled)
            group.MapPut("/inbound/{id:guid}/draft", ReviseInboundDraftAsync);
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().InboundApprovalEnabled)
            group.MapPost("/inbound/{id:guid}/approval", ApproveInboundAsync);
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().InboundReceiptEnabled)
        {
            _ = app.Services.GetRequiredService<LocationCreationSchema>();
            group.MapPost("/inbound/{id:guid}/receipts", ReceiveInboundAsync);
        }
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().OutboundEnabled)
        {
            _ = app.Services.GetRequiredService<LocationCreationSchema>();
            group.MapPost("/outbound", CreateOutboundStrictAsync);
            group.MapGet("/outbound/{id:guid}", ReadOutboundAsync);
            group.MapPost("/outbound/{id:guid}/approval", ApproveOutboundStrictAsync);
            group.MapPost("/outbound/{id:guid}/picking", PickOutboundStrictAsync);
            group.MapPost("/outbound/{id:guid}/shortage-release", ReleaseOutboundShortageStrictAsync);
            group.MapPost("/outbound/{id:guid}/shipment", ShipOutboundStrictAsync);
            group.MapPost("/outbound/{id:guid}/cancellation", CancelOutboundStrictAsync);
            group.MapPost("/outbound/{id:guid}/close-remainder", CloseOutboundRemainderStrictAsync);
        }
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().TransfersEnabled)
        {
            app.Services.GetRequiredService<LocationCreationSchema>();
            MapTransfers(group);
        }
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().CountsEnabled)
        {
            _ = app.Services.GetRequiredService<LocationCreationSchema>();
            MapCounts(group);
        }
        group.MapGet("/master-data-events", (int? offset, int? limit, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadCreationEventsAsync(new(offset ?? 0, limit ?? 25), http, database, ct));
        group.MapGet("/master-data-events/summary", ReadCreationEventSummaryAsync);
        group.MapGet("/inventory", (int? offset, int? limit, Guid? warehouseId, Guid? locationId, Guid? itemId,
            Guid? containerId, string? batch, HttpContext http, InventoryDatabase database, CancellationToken ct) =>
            QueryInventoryAsync(new(offset ?? 0, limit ?? 25, warehouseId, locationId, itemId, containerId, batch), http, database, ct));
        group.MapGet("/ledger", (int? offset, int? limit, Guid? warehouseId, Guid? locationId, Guid? itemId,
            Guid? containerId, string? batch, Guid? sourceDocumentId, Guid? correlationId, int? kind,
            string? from, string? before, HttpContext http, StockLedgerDatabase database, CancellationToken ct) =>
            QueryStockLedgerAsync(new(new(offset ?? 0, limit ?? 25, warehouseId, locationId, itemId, containerId, batch),
                sourceDocumentId, correlationId, kind.HasValue ? (StockLedgerKind)kind.Value : null), from, before, http, database, ct));
        if (app.Services.GetRequiredService<PlatformFileSettings>().Enabled) MapMasterDataFiles(group);
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().WarehouseCreationEnabled)
            group.MapPost("/warehouses", CreateWarehouseAsync);
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().WarehouseStateEnabled)
            group.MapPut("/warehouses/{id:guid}/state", ChangeWarehouseStateAsync);
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().LocationStateEnabled)
        {
            _ = app.Services.GetRequiredService<LocationCreationSchema>();
            group.MapPut("/locations/{id:guid}/state", ChangeLocationStateAsync);
        }
        if (app.Services.GetRequiredService<WmsMasterDataSettings>().LocationCreationEnabled)
        {
            _ = app.Services.GetRequiredService<LocationCreationSchema>();
            group.MapPost("/locations", CreateLocationAsync);
        }
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

    private static async Task<IResult> ReadCreationEventsAsync(MasterDataEventQuery query, HttpContext http, MasterDataDatabase database, CancellationToken ct)
    {
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var result = await owner.QueryCreationEventsAsync(new(context.TenantId, context.ActorId, context.SessionId), query, ct);
            return result.Decision switch
            {
                MasterDataReadDecision.Success => Results.Ok(new { result.Rows, result.Offset, result.Limit }),
                MasterDataReadDecision.Forbidden => Results.Forbid(),
                MasterDataReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unknown WMS creation-event read decision.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS creation-event access failed.");
        }
    }

    private sealed class MasterDataDatabase(IdentitySettings settings, MasterDataAuthority authorization, ILoggerFactory logs, TimeProvider clock)
    {
        internal WmsMasterDataScope Open(Guid tenant) => new(settings.ConnectionString, tenant, authorization, logs, clock);
        internal WmsMasterDataWriter OpenWarehouseWriter(Guid tenant) =>
            new(settings.ConnectionString, tenant, authorization, WarehouseOnlySchema.Instance, clock, logs);
        internal WmsWarehouseStateWriter OpenWarehouseStateWriter(Guid tenant) =>
            new(settings.ConnectionString, tenant, authorization, clock, logs);
        internal WmsLocationStateWriter OpenLocationStateWriter(Guid tenant, IWarehouseLocationSchema schema) =>
            new(settings.ConnectionString, tenant, authorization, schema, clock, logs);
        internal WmsMasterDataWriter OpenLocationWriter(Guid tenant, IWarehouseLocationSchema schema) =>
            new(settings.ConnectionString, tenant, authorization, schema, clock, logs);
        internal WmsStockResourceWriter OpenStockResources(Guid tenant, IWarehouseLocationSchema schema) =>
            new(settings.ConnectionString, tenant, authorization, schema, clock, logs);
        internal WmsInboundExecutionWriter OpenInboundExecution(Guid tenant, IWarehouseLocationSchema schema) =>
            new(settings.ConnectionString, tenant, authorization, schema, clock, logs);
        internal WmsCountWriter OpenCounts(Guid tenant, IWarehouseLocationSchema schema) =>
            new(settings.ConnectionString, tenant, authorization, schema, clock, logs);
        internal WmsTransferWriter OpenTransfers(Guid tenant, IWarehouseLocationSchema schema) =>
            new(settings.ConnectionString, tenant, authorization, schema, clock, logs);
        internal WmsOutboundWriter OpenOutbound(Guid tenant, IWarehouseLocationSchema schema) =>
            new(settings.ConnectionString, tenant, authorization, schema, clock, logs);
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
