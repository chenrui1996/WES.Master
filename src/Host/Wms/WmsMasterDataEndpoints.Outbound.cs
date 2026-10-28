using System.Text.Json.Serialization;
using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.Outbound;

namespace Wes.Host.Wms;

/// <summary>Explicit source balance and exact planned quantity for one outbound row.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundDraftLineInput([property: JsonRequired] Guid Id, [property: JsonRequired] Guid BalanceId,
    [property: JsonRequired] string Quantity, [property: JsonRequired] int Branch);
/// <summary>Strict multi-line outbound draft body; actor, tenant and operation key are server supplied.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundDraftInput([property: JsonRequired] string Code, [property: JsonRequired] Guid WarehouseId,
    [property: JsonRequired] IReadOnlyList<OutboundDraftLineInput> Lines);
/// <summary>Expected original balance version for one complete approval row.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundBalanceVersionInput([property: JsonRequired] Guid BalanceId, [property: JsonRequired] string ExpectedVersion);
/// <summary>Approval body requires the exact order version and complete no-duplicate balance-version set.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundApprovalInput([property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] IReadOnlyList<OutboundBalanceVersionInput> Balances);
/// <summary>Incremental pick/shortage input; short quantity is not an implicit reservation release.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundPickInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] Guid LineId,
    [property: JsonRequired] string PickedQuantity, [property: JsonRequired] string ShortageQuantity, [property: JsonRequired] string Reason);
/// <summary>Explicit cross-actor release of an owner-confirmed unexecuted shortage.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundShortageReleaseInput([property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] Guid LineId, [property: JsonRequired] string Quantity, [property: JsonRequired] string Reason);
/// <summary>One direct-shipment input for exactly the line's picked quantity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundShipmentInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] Guid LineId,
    [property: JsonRequired] string Quantity);
/// <summary>Reasoned cancellation or remainder-closure body.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutboundStateInput([property: JsonRequired] string ExpectedVersion, [property: JsonRequired] string Reason);

internal sealed record StrictOutboundBody<T>(T Value) where T : class
{
    public static async ValueTask<StrictOutboundBody<T>> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        if (!context.Request.HasJsonContentType())
            throw new BadHttpRequestException("Outbound requests require a JSON body.", StatusCodes.Status415UnsupportedMediaType);
        var defaults = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var options = new JsonSerializerOptions(defaults) { AllowDuplicateProperties = false };
        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, options, context.RequestAborted);
            return value is null
                ? throw new BadHttpRequestException("Outbound request body is required.", StatusCodes.Status400BadRequest)
                : new(value);
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Outbound request body is invalid.", StatusCodes.Status400BadRequest);
        }
    }
}

public static partial class WmsMasterDataEndpoints
{
    private static Task<IResult> CreateOutboundStrictAsync(StrictOutboundBody<OutboundDraftInput> body, HttpContext http, MasterDataDatabase database,
        LocationCreationSchema schema, CancellationToken ct) =>
        CreateOutboundAsync(body.Value, http, database, schema, ct);

    private static async Task<IResult> CreateOutboundAsync(OutboundDraftInput input, HttpContext http, MasterDataDatabase database,
        LocationCreationSchema schema, CancellationToken ct) =>
        input.Lines is null || input.Lines.Any(line => line is null)
            ? Results.BadRequest()
            : await ExecuteOutboundAsync(http, database, schema.Adapter,
                (owner, subject, operation, correlation) => owner.CreateAsync(subject,
                    new(operation, correlation, input.Code, input.WarehouseId,
                        input.Lines.Select(line => new OutboundDraftLineRequest(line.Id, line.BalanceId, line.Quantity, line.Branch)).ToArray()),
                    ct), ct, createdPath: true);

    private static Task<IResult> ApproveOutboundStrictAsync(Guid id, StrictOutboundBody<OutboundApprovalInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ApproveOutboundAsync(id, body.Value, http, database, schema, ct);

    private static async Task<IResult> ApproveOutboundAsync(Guid id, OutboundApprovalInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        input.Balances is null || input.Balances.Any(row => row is null)
            ? Results.BadRequest()
            : await ExecuteOutboundAsync(http, database, schema.Adapter,
                (owner, subject, operation, correlation) => owner.ApproveAsync(subject, id,
                    new(input.ExpectedVersion, input.Balances.Select(row =>
                        new OutboundBalanceVersionRequest(row.BalanceId, row.ExpectedVersion)).ToArray()),
                    operation, correlation, ct), ct, id);

    private static Task<IResult> PickOutboundStrictAsync(Guid id, StrictOutboundBody<OutboundPickInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        PickOutboundAsync(id, body.Value, http, database, schema, ct);

    private static Task<IResult> PickOutboundAsync(Guid id, OutboundPickInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteOutboundAsync(http, database, schema.Adapter,
            (owner, subject, operation, correlation) => owner.PickAsync(subject, id,
                new(input.ExpectedVersion, input.LineId, input.PickedQuantity, input.ShortageQuantity, input.Reason), operation, correlation, ct),
            ct, id);

    private static Task<IResult> ReleaseOutboundShortageStrictAsync(Guid id, StrictOutboundBody<OutboundShortageReleaseInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ReleaseOutboundShortageAsync(id, body.Value, http, database, schema, ct);

    private static Task<IResult> ReleaseOutboundShortageAsync(Guid id, OutboundShortageReleaseInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteOutboundAsync(http, database, schema.Adapter,
            (owner, subject, operation, correlation) => owner.ReleaseShortageAsync(subject, id,
                new(input.ExpectedVersion, input.LineId, input.Quantity, input.Reason), operation, correlation, ct), ct, id);

    private static Task<IResult> ShipOutboundStrictAsync(Guid id, StrictOutboundBody<OutboundShipmentInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ShipOutboundAsync(id, body.Value, http, database, schema, ct);

    private static Task<IResult> ShipOutboundAsync(Guid id, OutboundShipmentInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteOutboundAsync(http, database, schema.Adapter,
            (owner, subject, operation, correlation) => owner.ShipAsync(subject, id,
                new(input.ExpectedVersion, input.LineId, input.Quantity), operation, correlation, ct), ct, id);

    private static Task<IResult> CancelOutboundStrictAsync(Guid id, StrictOutboundBody<OutboundStateInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        CancelOutboundAsync(id, body.Value, http, database, schema, ct);

    private static Task<IResult> CancelOutboundAsync(Guid id, OutboundStateInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteOutboundAsync(http, database, schema.Adapter,
            (owner, subject, operation, correlation) => owner.CancelAsync(subject, id,
                new(input.ExpectedVersion, input.Reason), operation, correlation, ct), ct, id);

    private static Task<IResult> CloseOutboundRemainderStrictAsync(Guid id, StrictOutboundBody<OutboundStateInput> body, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        CloseOutboundRemainderAsync(id, body.Value, http, database, schema, ct);

    private static Task<IResult> CloseOutboundRemainderAsync(Guid id, OutboundStateInput input, HttpContext http,
        MasterDataDatabase database, LocationCreationSchema schema, CancellationToken ct) =>
        ExecuteOutboundAsync(http, database, schema.Adapter,
            (owner, subject, operation, correlation) => owner.CloseRemainderAsync(subject, id,
                new(input.ExpectedVersion, input.Reason), operation, correlation, ct), ct, id);

    private static async Task<IResult> ExecuteOutboundAsync(HttpContext http, MasterDataDatabase database,
        Wes.Wms.Features.MasterData.IWarehouseLocationSchema schema,
        Func<WmsOutboundWriter, Wes.Wms.Features.MasterData.MasterDataSubject, Guid, Guid, Task<OutboundResult>> execute,
        CancellationToken ct, Guid? id = null, bool createdPath = false)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryWmsMutationOperation(http, "outbound operation", out var operation)) return Results.BadRequest();
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenOutbound(actor.TenantId, schema);
            var result = await execute(owner,
                new(actor.TenantId, actor.ActorId, actor.SessionId), operation, Guid.Parse(http.TraceIdentifier));
            if (createdPath && result.Decision == OutboundDecision.Committed && result.Id is not null && result.Version is not null)
                return Results.Created($"/api/wms/outbound/{result.Id.Value:D}", result);
            return result.Decision switch
            {
                OutboundDecision.Committed when result.Id is not null && result.Version is not null =>
                    Results.Created($"/api/wms/outbound/{id ?? result.Id.Value:D}", result),
                OutboundDecision.Replayed when result.Id is not null && result.Version is not null => Results.Ok(result),
                OutboundDecision.Invalid => Results.BadRequest(),
                OutboundDecision.Forbidden => Results.Forbid(),
                OutboundDecision.NotFound => Results.NotFound(),
                OutboundDecision.Conflict or OutboundDecision.Blocked => Results.Conflict(),
                OutboundDecision.Unsupported or OutboundDecision.SchemaRejected => Results.UnprocessableEntity(),
                OutboundDecision.SchemaUnavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "WMS outbound schema is unavailable."),
                _ => throw new InvalidOperationException("Unexpected WMS outbound outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS outbound operation failed.");
        }
    }

    private static async Task<IResult> ReadOutboundAsync(Guid id, HttpContext http, MasterDataDatabase database,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.OpenOutbound(actor.TenantId, WarehouseOnlySchema.Instance);
            var result = await owner.ReadAsync(new(actor.TenantId, actor.ActorId, actor.SessionId), id, ct);
            return result.Decision switch
            {
                OutboundReadDecision.Success when result.Record is not null => Results.Ok(new { decision = 0, record = result.Record }),
                OutboundReadDecision.Invalid => Results.BadRequest(),
                OutboundReadDecision.Forbidden => Results.Forbid(),
                OutboundReadDecision.NotFound => Results.NotFound(),
                _ => throw new InvalidOperationException("Unexpected WMS outbound read outcome.")
            };
        }
        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS outbound read failed.");
        }
    }
}
