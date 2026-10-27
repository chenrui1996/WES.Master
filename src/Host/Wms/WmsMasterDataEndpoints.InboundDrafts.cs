using System.Text.Json.Serialization;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;

namespace Wes.Host.Wms;

/// <summary>Strict ordered plan input; quantity is a decimal string, never a floating-point JSON number.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInboundDraftLineInput(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] Guid ItemId,
    [property: JsonRequired] Guid UnitId,
    [property: JsonRequired] string Batch,
    [property: JsonRequired] int Precision,
    [property: JsonRequired] string Planned);

/// <summary>Draft only: no caller actor, tenant, session, approval state, received quantity or mutable version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInboundDraftInput(
    [property: JsonRequired] string Code,
    [property: JsonRequired] Guid WarehouseId,
    [property: JsonRequired] IReadOnlyList<CreateInboundDraftLineInput> Lines);

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> CreateInboundDraftAsync(CreateInboundDraftInput input, HttpContext http,
        IdentitySettings settings, InboundDraftAuthority authorization, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (!TryWmsMutationOperation(http, "inbound draft creation", out var operation)) return Results.BadRequest();
        if (input.Lines is null || input.Lines.Count is < 1 or > 100 || input.Lines.Any(line => line is null))
        {
            logs.CreateLogger("WmsInput").LogWarning("WMS inbound draft input rejected {Code}", "ValidationFailed");
            return Results.BadRequest();
        }
        var subject = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = new WmsInboundDraftWriter(settings.ConnectionString, subject.TenantId, authorization, clock, logs);
            var request = new InboundDraftCreation(operation, input.Code, input.WarehouseId,
                input.Lines.Select(line => new InboundDraftLine(line.Id, line.ItemId, line.UnitId, line.Batch, line.Precision, line.Planned)).ToArray());
            var result = await owner.CreateAsync(new(subject.TenantId, subject.ActorId, subject.SessionId), request, ct);
            return result.Decision switch
            {
                InboundDraftDecision.Created when result.Id is not null => Results.Created($"/api/wms/inbound/{result.Id.Value:D}", result),
                InboundDraftDecision.Replayed when result.Id is not null => Results.Ok(result),
                InboundDraftDecision.Forbidden => Results.Forbid(),
                InboundDraftDecision.Invalid => Results.BadRequest(),
                InboundDraftDecision.NotFound => Results.NotFound(),
                InboundDraftDecision.Conflict => Results.Conflict(),
                _ => throw new InvalidOperationException("Unknown inbound draft HTTP outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS inbound draft creation failed.");
        }
    }

    private sealed class InboundDraftAuthority(IdentityDatabase database) : IInboundDraftAuthorization
    {
        public async Task<IReadOnlySet<string>> WriteFieldsAsync(InboundSubject subject, CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleOwnedResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                "wms.inbound.write", subject.TenantId, subject.ActorId, cancellationToken);
        }

        public async Task<IReadOnlySet<string>> WarehouseFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleOwnedResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                "wms.warehouses.read", subject.TenantId, creator, cancellationToken);
        }
    }
}
