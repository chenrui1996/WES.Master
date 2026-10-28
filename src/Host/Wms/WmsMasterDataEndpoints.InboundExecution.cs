using System.Text.Json.Serialization;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Approval binds the exact original order version; operation and actor are not accepted from the body.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InboundApprovalInput(string ExpectedVersion);
/// <summary>Explicit online receipt tuple; quantity is a strict decimal string and all identities are mandatory.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InboundReceiptInput(string ExpectedVersion, string ExpectedBalanceVersion, [property: JsonRequired] Guid LineId,
    [property: JsonRequired] Guid ExecutionId, string Quantity, [property: JsonRequired] Guid ContainerId,
    [property: JsonRequired] Guid ReceiptLocationId, [property: JsonRequired] Guid TargetLocationId, [property: JsonRequired] Guid BalanceId);

public static partial class WmsMasterDataEndpoints
{
    private static Task<IResult> ApproveInboundAsync(Guid id, InboundApprovalInput input, HttpContext http, MasterDataDatabase database, CancellationToken ct) =>
        ExecuteInboundAsync(id, http, database, WarehouseOnlySchema.Instance,
            (owner, subject, operation, correlation) => owner.ApproveAsync(subject, id, new(operation, correlation, input.ExpectedVersion), ct), ct);

    private static Task<IResult> ReceiveInboundAsync(Guid id, InboundReceiptInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteInboundAsync(id, http, database, schema.Adapter,
            (owner, subject, operation, correlation) => owner.ReceiveAsync(subject, id, new(operation, correlation,
                input.ExpectedVersion, input.ExpectedBalanceVersion, input.LineId, input.ExecutionId, input.Quantity,
                input.ContainerId, input.ReceiptLocationId, input.TargetLocationId, input.BalanceId), ct), ct);

    private static async Task<IResult> ExecuteInboundAsync(Guid id, HttpContext http, MasterDataDatabase database, IWarehouseLocationSchema schema,
        Func<WmsInboundExecutionWriter, MasterDataSubject, Guid, Guid, Task<InboundExecutionResult>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryWmsMutationOperation(http, "inbound execution", out var operation)) return Results.BadRequest();
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenInboundExecution(actor.TenantId, schema);
            var result = await execute(owner, new(actor.TenantId, actor.ActorId, actor.SessionId), operation, Guid.Parse(http.TraceIdentifier));
            return result.Decision switch
            {
                InboundExecutionDecision.Committed when result.Id is not null && result.Version is not null =>
                    Results.Created($"/api/wms/inbound/{id:D}", result),
                InboundExecutionDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                InboundExecutionDecision.Invalid => Results.BadRequest(),
                InboundExecutionDecision.Forbidden => Results.Forbid(),
                InboundExecutionDecision.NotFound => Results.NotFound(),
                InboundExecutionDecision.Conflict => Results.Conflict(),
                InboundExecutionDecision.Blocked => Results.Conflict(new { code = "InboundExecutionBlocked" }),
                InboundExecutionDecision.SchemaRejected => Results.UnprocessableEntity(),
                InboundExecutionDecision.SchemaUnavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "WMS location schema unavailable."),
                _ => throw new InvalidOperationException("Unexpected inbound execution outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS inbound execution failed.");
        }
    }
}
