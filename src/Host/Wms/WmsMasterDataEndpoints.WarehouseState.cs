using System.Text.Json.Serialization;
using Wes.Host.Identity;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Logical state only; no child-resource, inventory, tenant or creator fields are accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WarehouseStateInput(
    [property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string Reason);

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> ChangeWarehouseStateAsync(Guid id, WarehouseStateInput input, HttpContext http,
        MasterDataDatabase database, CancellationToken ct)
    {
        if (!TryWmsMutationOperation(http, "warehouse state", out var operation)) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenWarehouseStateWriter(subject.TenantId);
            var result = await owner.ChangeAsync(new(subject.TenantId, subject.ActorId, subject.SessionId), id,
                new(operation, input.ExpectedVersion, input.Enabled, input.Reason), ct);
            return result.Decision switch
            {
                WarehouseStateDecision.Changed or WarehouseStateDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                WarehouseStateDecision.Forbidden => Results.Forbid(),
                WarehouseStateDecision.Invalid => Results.BadRequest(),
                WarehouseStateDecision.NotFound => Results.NotFound(),
                WarehouseStateDecision.Conflict => Results.Conflict(),
                WarehouseStateDecision.Blocked => Results.Conflict(new { code = "WarehouseActivityBlocked" }),
                _ => throw new InvalidOperationException("Unexpected warehouse state outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS warehouse state change failed.");
        }
    }
}
