using Wes.Host.Identity;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> ReadCreationEventSummaryAsync(HttpContext http, MasterDataDatabase database, CancellationToken ct)
    {
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var result = await owner.ReadCreationEventSummaryAsync(new(context.TenantId, context.ActorId, context.SessionId), ct);
            return result.Decision switch
            {
                MasterDataReadDecision.Success when result.Summary is not null => Results.Ok(result.Summary),
                MasterDataReadDecision.Forbidden => Results.Forbid(),
                _ => throw new InvalidOperationException("Unknown WMS creation-event summary decision.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS creation-event count access failed.");
        }
    }
}
