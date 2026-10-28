using Wes.Host.Identity;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> ChangeLocationStateAsync(Guid id, WarehouseStateInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct)
    {
        if (!TryWmsMutationOperation(http, "location state", out var operation)) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenLocationStateWriter(subject.TenantId, schema.Adapter);
            var result = await owner.ChangeAsync(new(subject.TenantId, subject.ActorId, subject.SessionId), id,
                new(operation, input.ExpectedVersion, input.Enabled, input.Reason), ct);
            return result.Decision switch
            {
                LocationStateDecision.Changed or LocationStateDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                LocationStateDecision.Forbidden => Results.Forbid(),
                LocationStateDecision.Invalid => Results.BadRequest(),
                LocationStateDecision.NotFound => Results.NotFound(),
                LocationStateDecision.Conflict => Results.Conflict(),
                LocationStateDecision.Blocked => Results.Conflict(new { code = "LocationActivityBlocked" }),
                LocationStateDecision.SchemaRejected => Results.UnprocessableEntity(),
                LocationStateDecision.SchemaUnavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "WMS location schema unavailable."),
                _ => throw new InvalidOperationException("Unexpected location state outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS location state change failed.");
        }
    }
}
