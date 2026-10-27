using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Inventory;
using Wes.Wms.Features.Ledger;

namespace Wes.Host.Wms;

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> QueryInventoryAsync(InventoryQuery query, HttpContext http,
        InventoryDatabase database, CancellationToken ct)
    {
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var result = await owner.QueryAsync(new(context.TenantId, context.ActorId, context.SessionId), query, ct);
            return result.Decision switch
            {
                InventoryReadDecision.Success => Results.Ok(new { result.Rows, result.Offset, result.Limit }),
                InventoryReadDecision.Forbidden => Results.Forbid(),
                InventoryReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unknown inventory read decision.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS inventory access failed.");
        }
    }

    private sealed class InventoryDatabase(IdentitySettings settings, InventoryAuthority authorization, ILoggerFactory logs)
    {
        internal WmsInventoryScope Open(Guid tenant) => new(settings.ConnectionString, tenant, authorization, logs);
    }

    private sealed class InventoryAuthority(IdentityDatabase database) : IInventoryAuthorization
    {
        public async Task<IReadOnlySet<string>> ReadAllFieldsAsync(InventorySubject subject, CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleTenantResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                "wms.inventory.read", cancellationToken);
        }
    }
}
