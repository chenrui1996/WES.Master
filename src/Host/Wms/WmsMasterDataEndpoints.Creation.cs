using System.Text.Json.Serialization;
using Wes.Host.Identity;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Only code and name are accepted; creator, tenant, activation and schema are never caller-selectable here.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWarehouseInput(string Code, string Name);

public static partial class WmsMasterDataEndpoints
{
    private static async Task<IResult> CreateWarehouseAsync(CreateWarehouseInput input, HttpContext http,
        MasterDataDatabase database, CancellationToken ct)
    {
        if (!TryCreationOperation(http, "warehouse", out var operation)) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenWarehouseWriter(subject.TenantId);
            var result = await owner.CreateAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                new(operation, Guid.Parse(http.TraceIdentifier), input.Code, input.Name), ct);
            return result.Decision switch
            {
                MasterDataWriteDecision.Created when result.Id is not null && result.Version is not null =>
                    Results.Created($"/api/wms/warehouses/{result.Id.Value:D}", result),
                MasterDataWriteDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                MasterDataWriteDecision.Forbidden => Results.Forbid(),
                MasterDataWriteDecision.Invalid => Results.BadRequest(),
                MasterDataWriteDecision.CodeConflict or MasterDataWriteDecision.IdempotencyConflict => Results.Conflict(),
                _ => throw new InvalidOperationException("Unexpected warehouse creation outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS warehouse creation failed.");
        }
    }

    private static bool TryCreationOperation(HttpContext http, string kind, out Guid operation)
    {
        operation = Guid.Empty;
        var key = http.Request.Headers["Idempotency-Key"];
        if (http.Request.Headers["X-WES-Client"] is (["ops-web"] or ["pda"])
            && key.Count == 1 && Guid.TryParseExact(key[0], "D", out operation) && operation != Guid.Empty) return true;
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WmsInput")
            .LogWarning("WMS {Kind} creation input rejected {Code}", kind, "ValidationFailed");
        return false;
    }

    private sealed class WarehouseOnlySchema : IWarehouseLocationSchema
    {
        internal static readonly WarehouseOnlySchema Instance = new();
        public Task<LocationSchemaDecision> ValidateAsync(Guid tenantId, Guid warehouseId, Guid packId,
            string schemaVersion, string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Location creation requires a separately assembled Pack schema adapter.");
    }
}
