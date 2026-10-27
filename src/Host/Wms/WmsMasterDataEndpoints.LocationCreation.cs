using System.Text.Json.Serialization;
using Wes.Host.Identity;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Disabled location definition and pinned schema tuple; tenant, actor and activation cannot be supplied.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateLocationInput(string Code, string Name, Guid WarehouseId, Guid PackId, string SchemaVersion);

public static partial class WmsMasterDataEndpoints
{
    private static LocationCreationSchema LoadLocationSchema(IServiceCollection services, IServiceProvider provider)
    {
        var schemas = services.Where(item => item.ServiceType == typeof(IWarehouseLocationSchema) && !item.IsKeyedService).ToArray();
        if (schemas.Length != 1 || schemas[0].Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Location creation requires exactly one explicitly configured singleton schema adapter.");
        return new(provider.GetRequiredService<IWarehouseLocationSchema>());
    }

    private static async Task<IResult> CreateLocationAsync(CreateLocationInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct)
    {
        if (!TryWmsMutationOperation(http, "location creation", out var operation)) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenLocationWriter(subject.TenantId, schema.Adapter);
            var result = await owner.CreateAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                new(operation, Guid.Parse(http.TraceIdentifier), input.Code, input.Name,
                    input.WarehouseId, input.PackId, input.SchemaVersion), ct);
            return result.Decision switch
            {
                MasterDataWriteDecision.Created when result.Id is not null && result.Version is not null =>
                    Results.Created($"/api/wms/locations/{result.Id.Value:D}", result),
                MasterDataWriteDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                MasterDataWriteDecision.Forbidden => Results.Forbid(),
                MasterDataWriteDecision.Invalid => Results.BadRequest(),
                MasterDataWriteDecision.NotFound => Results.NotFound(),
                MasterDataWriteDecision.CodeConflict or MasterDataWriteDecision.IdempotencyConflict => Results.Conflict(),
                MasterDataWriteDecision.SchemaRejected => Results.UnprocessableEntity(),
                MasterDataWriteDecision.SchemaUnavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "WMS location schema unavailable."),
                _ => throw new InvalidOperationException("Unexpected location creation outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS location creation failed.");
        }
    }

    private sealed record LocationCreationSchema(IWarehouseLocationSchema Adapter);
}
