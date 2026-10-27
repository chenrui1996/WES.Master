using System.Text.Json.Serialization;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;

namespace Wes.Host.Wms;

/// <summary>Full replacement plan and exact original version; the persisted code, warehouse and creator cannot be supplied or changed.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseInboundDraftInput(
    [property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] IReadOnlyList<CreateInboundDraftLineInput> Lines);

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> ReviseInboundDraftAsync(Guid id, ReviseInboundDraftInput input, HttpContext http,
        IdentitySettings settings, InboundRevisionAuthority authorization, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (!TryWmsMutationOperation(http, "inbound draft revision", out var operation)) return Results.BadRequest();
        if (input.Lines is null || input.Lines.Count is < 1 or > 100 || input.Lines.Any(line => line is null))
        {
            logs.CreateLogger("WmsInput").LogWarning("WMS inbound revision input rejected {Code}", "ValidationFailed");
            return Results.BadRequest();
        }
        var subject = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = new WmsInboundRevisionWriter(settings.ConnectionString, subject.TenantId, authorization, clock, logs);
            var request = new InboundDraftRevision(operation, input.ExpectedVersion,
                input.Lines.Select(line => new InboundDraftLine(line.Id, line.ItemId, line.UnitId, line.Batch, line.Precision, line.Planned)).ToArray());
            var result = await owner.ReviseAsync(new(subject.TenantId, subject.ActorId, subject.SessionId), id, request, ct);
            return result.Decision switch
            {
                InboundRevisionDecision.Revised or InboundRevisionDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                InboundRevisionDecision.Forbidden => Results.Forbid(),
                InboundRevisionDecision.Invalid => Results.BadRequest(),
                InboundRevisionDecision.NotFound => Results.NotFound(),
                InboundRevisionDecision.Conflict => Results.Conflict(),
                _ => throw new InvalidOperationException("Unknown inbound revision HTTP outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS inbound draft revision failed.");
        }
    }

    private sealed class InboundRevisionAuthority(IdentityDatabase database) : IInboundRevisionAuthorization
    {
        public Task<IReadOnlySet<string>> WriteFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken) =>
            FieldsAsync(subject, creator, "wms.inbound.write", cancellationToken);

        public Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken) =>
            FieldsAsync(subject, creator, "wms.inbound.read", cancellationToken);

        private async Task<IReadOnlySet<string>> FieldsAsync(InboundSubject subject, Guid creator, string permission, CancellationToken ct)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleOwnedResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                permission, subject.TenantId, creator, ct);
        }
    }
}
