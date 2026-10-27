using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;

namespace Wes.Host.Wms;

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> QueryInboundAsync(int? offset, int? limit, string? code, Guid? warehouseId, int? state,
        HttpContext http, InboundDatabase database, CancellationToken ct)
    {
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var result = await owner.QueryAsync(new(context.TenantId, context.ActorId, context.SessionId),
                new(offset ?? 0, limit ?? 25, code, warehouseId, state.HasValue ? (InboundState)state.Value : null), ct);
            return result.Decision switch
            {
                InboundReadDecision.Success => Results.Ok(new { result.Rows, result.Offset, result.Limit }),
                InboundReadDecision.Forbidden => Results.Forbid(),
                InboundReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unknown inbound catalog decision.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS inbound access failed.");
        }
    }

    private static async Task<IResult> ReadInboundAsync(Guid id, HttpContext http, InboundDatabase database, CancellationToken ct)
    {
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var result = await owner.ReadAsync(new(context.TenantId, context.ActorId, context.SessionId), id, ct);
            return result.Decision switch
            {
                InboundReadDecision.Success => Results.Ok(result.Order),
                InboundReadDecision.Forbidden => Results.Forbid(),
                InboundReadDecision.NotFound => Results.NotFound(),
                InboundReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unknown inbound read decision.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS inbound access failed.");
        }
    }

    private sealed class InboundDatabase(IdentitySettings settings, InboundAuthority authorization, ILoggerFactory logs)
    {
        internal WmsInboundScope Open(Guid tenant) => new(settings.ConnectionString, tenant, authorization, logs);
    }

    private sealed class InboundAuthority(IdentityDatabase database) : IInboundAuthorization
    {
        public async Task<InboundCreatorAccess> ReadCreatorsAsync(InboundSubject subject, IReadOnlyCollection<string> requiredFields,
            CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            var result = await owner.VisibleOwnedResourceCreatorsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                "wms.inbound.read", requiredFields, cancellationToken);
            return new(result.Authorized, result.CreatorIds.ToHashSet());
        }

        public async Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleOwnedResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                "wms.inbound.read", subject.TenantId, creator, cancellationToken);
        }
    }
}
