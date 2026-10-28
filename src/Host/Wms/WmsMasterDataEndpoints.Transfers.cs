using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.Transfers;

namespace Wes.Host.Wms;

/// <summary>Strict fixed whole-container transfer plan row.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferDraftLineInput([property: JsonRequired] Guid Id, [property: JsonRequired] Guid SourceBalanceId,
    [property: JsonRequired] Guid TargetBalanceId, [property: JsonRequired] string Quantity, [property: JsonRequired] int Branch);
/// <summary>Strict creation body; trusted identities are never client supplied.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferDraftInput([property: JsonRequired] string Code, [property: JsonRequired] Guid WarehouseId,
    [property: JsonRequired] IReadOnlyList<TransferDraftLineInput> Lines);
/// <summary>One expected current preview version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferBalanceVersionInput([property: JsonRequired] Guid BalanceId, [property: JsonRequired] string ExpectedVersion);
/// <summary>Exact all-source/all-target preview version set.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferApprovalInput([property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] IReadOnlyList<TransferBalanceVersionInput> Balances);
/// <summary>One explicitly selected transfer execution unit.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferLineInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] Guid LineId);
/// <summary>Exact final manual arrival scans.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferArrivalInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] Guid LineId,
    [property: JsonRequired] Guid SourceLocationId, [property: JsonRequired] Guid TargetLocationId,
    [property: JsonRequired] Guid ContainerId, [property: JsonRequired] string Quantity, [property: JsonRequired] string Reason);
/// <summary>Reasoned unknown execution; never an inventory release.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferUnknownInput([property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] Guid LineId, [property: JsonRequired] string Reason);
/// <summary>Safe closure of only unstarted remainder.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferClosureInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] string Reason);

public static partial class WmsMasterDataEndpoints
{
    private static void MapTransfers(RouteGroupBuilder group)
    {
        group.MapPost("/transfers", CreateTransferAsync);
        group.MapGet("/transfers/{id:guid}", ReadTransferAsync);
        group.MapPost("/transfers/{id:guid}/approval", ApproveTransferAsync);
        group.MapPost("/transfers/{id:guid}/dispatch", DispatchTransferAsync);
        group.MapPost("/transfers/{id:guid}/arrivals", ArriveTransferAsync);
        group.MapPost("/transfers/{id:guid}/synchronizations", SyncTransferAsync);
        group.MapPost("/transfers/{id:guid}/unknown", UnknownTransferAsync);
        group.MapPost("/transfers/{id:guid}/closure", CloseTransferAsync);
    }

    private static Task<IResult> CreateTransferAsync(StrictOutboundBody<TransferDraftInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct)
    {
        var input = body.Value;
        return input.Lines is null || input.Lines.Any(row => row is null) ? Task.FromResult(Results.BadRequest() as IResult)
            : ExecuteTransferAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
                owner.CreateAsync(subject, new(operation, correlation, input.Code, input.WarehouseId,
                    input.Lines.Select(row => new TransferDraftLineRequest(row.Id, row.SourceBalanceId, row.TargetBalanceId,
                        row.Quantity, row.Branch)).ToArray()), ct), ct);
    }

    private static Task<IResult> ApproveTransferAsync(Guid id, StrictOutboundBody<TransferApprovalInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct)
    {
        var input = body.Value;
        return input.Balances is null || input.Balances.Any(row => row is null) ? Task.FromResult(Results.BadRequest() as IResult)
            : ExecuteTransferAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
                owner.ApproveAsync(subject, id, new(input.ExpectedVersion, input.Balances.Select(row =>
                    new TransferBalanceVersionRequest(row.BalanceId, row.ExpectedVersion)).ToArray()), operation, correlation, ct), ct);
    }

    private static Task<IResult> DispatchTransferAsync(Guid id, StrictOutboundBody<TransferLineInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteTransferAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
            owner.DispatchAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.LineId), operation, correlation, ct), ct);

    private static Task<IResult> SyncTransferAsync(Guid id, StrictOutboundBody<TransferLineInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteTransferAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
            owner.SyncAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.LineId), operation, correlation, ct), ct);

    private static Task<IResult> ArriveTransferAsync(Guid id, StrictOutboundBody<TransferArrivalInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteTransferAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
            owner.ArriveAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.LineId, body.Value.SourceLocationId,
                body.Value.TargetLocationId, body.Value.ContainerId, body.Value.Quantity, body.Value.Reason), operation, correlation, ct), ct);

    private static Task<IResult> UnknownTransferAsync(Guid id, StrictOutboundBody<TransferUnknownInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteTransferAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
            owner.UnknownAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.LineId, body.Value.Reason), operation, correlation, ct), ct);

    private static Task<IResult> CloseTransferAsync(Guid id, StrictOutboundBody<TransferClosureInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteTransferAsync(http, database, schema.Adapter, (owner, subject, operation, correlation) =>
            owner.CloseAsync(subject, id, new(body.Value.ExpectedVersion, body.Value.Reason), operation, correlation, ct), ct);

    private static async Task<IResult> ExecuteTransferAsync(HttpContext http, MasterDataDatabase database,
        Wes.Wms.Features.MasterData.IWarehouseLocationSchema schema,
        Func<WmsTransferWriter, Wes.Wms.Features.MasterData.MasterDataSubject, Guid, Guid, Task<TransferResult>> execute,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryWmsMutationOperation(http, "transfer operation", out var operation)) return Results.BadRequest();
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenTransfers(actor.TenantId, schema);
            var result = await execute(owner, new(actor.TenantId, actor.ActorId, actor.SessionId), operation, Guid.Parse(http.TraceIdentifier));
            return result.Decision switch
            {
                TransferDecision.Committed when result.Id is not null && result.Version is not null =>
                    Results.Created($"/api/wms/transfers/{result.Id.Value:D}", result),
                TransferDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                TransferDecision.Invalid => Results.BadRequest(),
                TransferDecision.Forbidden => Results.Forbid(),
                TransferDecision.NotFound => Results.NotFound(),
                TransferDecision.Conflict or TransferDecision.Blocked => Results.Conflict(),
                TransferDecision.Unsupported or TransferDecision.SchemaRejected => Results.UnprocessableEntity(),
                TransferDecision.SchemaUnavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "WMS transfer schema is unavailable."),
                _ => throw new InvalidOperationException("Unexpected transfer outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS transfer operation failed.");
        }
    }

    private static async Task<IResult> ReadTransferAsync(Guid id, HttpContext http, MasterDataDatabase database, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenTransfers(actor.TenantId, WarehouseOnlySchema.Instance);
            var result = await owner.ReadAsync(new(actor.TenantId, actor.ActorId, actor.SessionId), id, ct);
            return result.Decision switch
            {
                TransferReadDecision.Success when result.Record is not null => Results.Ok(new { decision = 0, record = result.Record }),
                TransferReadDecision.Invalid => Results.BadRequest(),
                TransferReadDecision.Forbidden => Results.Forbid(),
                TransferReadDecision.NotFound => Results.NotFound(),
                _ => throw new InvalidOperationException("Unexpected transfer read outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS transfer read failed.");
        }
    }
}
