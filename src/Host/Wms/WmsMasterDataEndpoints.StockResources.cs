using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Explicit base-unit policy; omitted value types and caller identity metadata are rejected.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterUnitInput(string Code, string Name, [property: JsonRequired] bool Countable, [property: JsonRequired] int Precision);
/// <summary>Immutable registered base-unit and batch policy, not arbitrary supplied precision or tracking defaults.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterItemInput(string Code, string Name, [property: JsonRequired] Guid BaseUnitId, [property: JsonRequired] bool BatchRequired);
/// <summary>Container registration does not select or claim an occupied location.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterContainerInput(string Code, string Name, [property: JsonRequired] Guid WarehouseId);
/// <summary>Only exact legal dimension identities; no quantity, unit, precision or activation input.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterDimensionInput([property: JsonRequired] Guid WarehouseId, [property: JsonRequired] Guid LocationId,
    [property: JsonRequired] Guid ItemId, string Batch, [property: JsonRequired] Guid ContainerId);

public static partial class WmsMasterDataEndpoints
{
    private static void MapStockResourceReads(RouteGroupBuilder group)
    {
        group.MapGet("/units/{id:guid}", (Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadStockResourceAsync(id, http, database, (owner, subject) => owner.ReadUnitAsync(subject, id, ct), ct));
        group.MapGet("/items/{id:guid}", (Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadStockResourceAsync(id, http, database, (owner, subject) => owner.ReadItemAsync(subject, id, ct), ct));
        group.MapGet("/containers/{id:guid}", (Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadStockResourceAsync(id, http, database, (owner, subject) => owner.ReadContainerAsync(subject, id, ct), ct));
    }

    private static void MapStockResourceWrites(RouteGroupBuilder group)
    {
        group.MapPost("/units", (RegisterUnitInput input, HttpContext http, MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            RegisterStockResourceAsync("units", http, database, schema, (owner, subject, operation, correlation) =>
                owner.RegisterUnitAsync(subject, new(operation, correlation, input.Code, input.Name, input.Countable, input.Precision), ct), ct));
        group.MapPost("/items", (RegisterItemInput input, HttpContext http, MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            RegisterStockResourceAsync("items", http, database, schema, (owner, subject, operation, correlation) =>
                owner.RegisterItemAsync(subject, new(operation, correlation, input.Code, input.Name, input.BaseUnitId, input.BatchRequired), ct), ct));
        group.MapPost("/containers", (RegisterContainerInput input, HttpContext http, MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            RegisterStockResourceAsync("containers", http, database, schema, (owner, subject, operation, correlation) =>
                owner.RegisterContainerAsync(subject, new(operation, correlation, input.Code, input.Name, input.WarehouseId), ct), ct));
        group.MapPost("/dimensions", (RegisterDimensionInput input, HttpContext http, MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            RegisterStockResourceAsync("dimensions", http, database, schema, (owner, subject, operation, correlation) =>
                owner.RegisterDimensionAsync(subject, new(operation, correlation, input.WarehouseId, input.LocationId, input.ItemId, input.Batch, input.ContainerId), ct), ct));
    }

    private static async Task<IResult> RegisterStockResourceAsync(string kind, HttpContext http, MasterDataDatabase database,
        LocationCreationSchema schema, Func<WmsStockResourceWriter, MasterDataSubject, Guid, Guid, Task<StockResourceResult>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryWmsMutationOperation(http, "stock resource registration", out var operation)) return Results.BadRequest();
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenStockResources(actor.TenantId, schema.Adapter);
            var result = await execute(owner, new(actor.TenantId, actor.ActorId, actor.SessionId), operation, Guid.Parse(http.TraceIdentifier));
            return result.Decision switch
            {
                StockResourceDecision.Created when result.Id is not null && result.Version is not null => Results.Created(
                    kind == "dimensions" ? "/api/wms/inventory" : $"/api/wms/{kind}/{result.Id.Value:D}", result),
                StockResourceDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                StockResourceDecision.Forbidden => Results.Forbid(),
                StockResourceDecision.Invalid => Results.BadRequest(),
                StockResourceDecision.NotFound => Results.NotFound(),
                StockResourceDecision.Conflict => Results.Conflict(),
                StockResourceDecision.Blocked => Results.Conflict(new { code = "StockResourceBlocked" }),
                StockResourceDecision.SchemaRejected => Results.UnprocessableEntity(),
                StockResourceDecision.SchemaUnavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "WMS location schema unavailable."),
                _ => throw new InvalidOperationException("Unexpected stock registration outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS stock resource registration failed.");
        }
    }

    private static async Task<IResult> ReadStockResourceAsync<T>(Guid id, HttpContext http, MasterDataDatabase database,
        Func<WmsStockResourceWriter, MasterDataSubject, Task<StockResourceReadResult<T>>> read, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenStockResources(actor.TenantId, WarehouseOnlySchema.Instance);
            var result = await read(owner, new(actor.TenantId, actor.ActorId, actor.SessionId));
            return result.Decision switch
            {
                MasterDataReadDecision.Success when result.Definition is not null => Results.Ok(result.Definition),
                MasterDataReadDecision.Forbidden => Results.Forbid(),
                MasterDataReadDecision.NotFound => Results.NotFound(),
                MasterDataReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unexpected stock resource read outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS stock resource access failed.");
        }
    }
}
