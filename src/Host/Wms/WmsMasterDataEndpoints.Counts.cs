using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Counts;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Fixed single-location draft input without client-selected lines.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CountCreateInput([property: JsonRequired] string Code, [property: JsonRequired] Guid WarehouseId,
    [property: JsonRequired] Guid LocationId);
/// <summary>Expected current draft or invalidated version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CountStartInput([property: JsonRequired] string ExpectedVersion);
/// <summary>Scanned blind observation; quantities are strict strings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CountRecordInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] Guid LineId,
    [property: JsonRequired] string Quantity, [property: JsonRequired] Guid LocationId, [property: JsonRequired] Guid ItemId,
    [property: JsonRequired] Guid ContainerId, [property: JsonRequired] string Reason);
/// <summary>Reasoned explicit approval or invalidation of the exact current version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CountStateInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] string Reason);

public static partial class WmsMasterDataEndpoints
{
    private static void MapCounts(RouteGroupBuilder group)
    {
        group.MapPost("/counts", (StrictOutboundBody<CountCreateInput> body, HttpContext http, MasterDataDatabase database,
            LocationCreationSchema schema, CancellationToken ct) =>
            WriteCountAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
                owner.CreateAsync(subject, new(body.Value.Code, body.Value.WarehouseId, body.Value.LocationId), operation, correlation, ct), ct));
        group.MapPost("/counts/{id:guid}/start", (Guid id, StrictOutboundBody<CountStartInput> body, HttpContext http,
            MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            WriteCountAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
                owner.StartAsync(subject, id, new(body.Value.ExpectedVersion), operation, correlation, ct), ct));
        group.MapPost("/counts/{id:guid}/records", (Guid id, StrictOutboundBody<CountRecordInput> body, HttpContext http,
            MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            WriteCountAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
                owner.RecordAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.LineId, body.Value.Quantity,
                    body.Value.LocationId, body.Value.ItemId, body.Value.ContainerId, body.Value.Reason), operation, correlation, ct), ct));
        group.MapPost("/counts/{id:guid}/adjustment", (Guid id, StrictOutboundBody<CountStateInput> body, HttpContext http,
            MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            WriteCountAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
                owner.AdjustAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.Reason), operation, correlation, ct), ct));
        group.MapPost("/counts/{id:guid}/invalidation", (Guid id, StrictOutboundBody<CountStateInput> body, HttpContext http,
            MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
            WriteCountAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
                owner.InvalidateAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.Reason), operation, correlation, ct), ct));
        group.MapGet("/counts/{id:guid}", (Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadCountAsync(id, false, http, database, ct));
        group.MapGet("/counts/{id:guid}/review", (Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
            ReadCountAsync(id, true, http, database, ct));
    }

    private static async Task<IResult> WriteCountAsync(HttpContext http, MasterDataDatabase database, IWarehouseLocationSchema schema,
        Func<WmsCountWriter, MasterDataSubject, Guid, Guid, Task<CountResult>> execute, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryWmsMutationOperation(http, "count operation", out var operation)) return Results.BadRequest();
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenCounts(actor.TenantId, schema);
            var result = await execute(owner, new(actor.TenantId, actor.ActorId, actor.SessionId), operation, Guid.Parse(http.TraceIdentifier));
            return result.Decision switch
            {
                CountDecision.Committed when result.Id is not null && result.Version is not null =>
                    Results.Created($"/api/wms/counts/{result.Id.Value:D}", result),
                CountDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                _ => CountFailure(result.Decision)
            };
        }
        catch (WmsDatabaseException error) { return CountDatabaseFailure(error); }
    }

    private static async Task<IResult> ReadCountAsync(Guid id, bool review, HttpContext http, MasterDataDatabase database, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenCounts(actor.TenantId, WarehouseOnlySchema.Instance);
            var subject = new MasterDataSubject(actor.TenantId, actor.ActorId, actor.SessionId);
            if (review)
            {
                var result = await owner.ReviewAsync(subject, id, ct);
                return result.Decision == CountDecision.Committed && result.Record is not null
                    ? Results.Ok(new { decision = 0, record = result.Record }) : CountFailure(result.Decision);
            }
            else
            {
                var result = await owner.ReadAsync(subject, id, ct);
                return result.Decision == CountDecision.Committed && result.Record is not null
                    ? Results.Ok(new { decision = 0, record = result.Record }) : CountFailure(result.Decision);
            }
        }
        catch (WmsDatabaseException error) { return CountDatabaseFailure(error); }
    }

    private static IResult CountFailure(CountDecision decision) => decision switch
    {
        CountDecision.Invalid => Results.BadRequest(),
        CountDecision.Forbidden => Results.Forbid(),
        CountDecision.NotFound => Results.NotFound(),
        CountDecision.Conflict or CountDecision.Blocked => Results.Conflict(),
        CountDecision.SchemaRejected => Results.UnprocessableEntity(),
        CountDecision.SchemaUnavailable => Results.Problem(statusCode: 503, title: "WMS count schema is unavailable."),
        _ => throw new InvalidOperationException("Unexpected WMS count outcome.")
    };
    private static IResult CountDatabaseFailure(WmsDatabaseException error) =>
        Results.Problem(statusCode: error.IsTransient ? 503 : 500, title: "WMS count operation failed.");
}
