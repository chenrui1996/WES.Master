using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Wes.Host.Identity;
using Wes.Host.Platform;
using Wes.Platform;
using Wes.Platform.Features.Files;
using Wes.Platform.Features.Tenancy;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

/// <summary>Exact opaque file precondition; owner, actor and business target cannot be supplied in the body.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileBindingInput(string? ExpectedVersion);

public static partial class WmsMasterDataEndpoints
{
    private static void MapMasterDataFiles(RouteGroupBuilder group)
    {
        Map("/warehouses/{entityId:guid}/files", location: false);
        Map("/locations/{entityId:guid}/files", location: true);

        void Map(string prefix, bool location)
        {
            var files = group.MapGroup(prefix);
            files.AddEndpointFilter(async (invocation, next) =>
            {
                try { return await next(invocation); }
                catch (FileAccessException error) { return FileFailure(invocation.HttpContext, error.IsTransient ? 503 : 500); }
                catch (WmsDatabaseException error) { return FileFailure(invocation.HttpContext, error.IsTransient ? 503 : 500); }
                catch (Exception error) when (PlatformFileAdapters.IsStorageUnavailable(error))
                { return FileFailure(invocation.HttpContext, 503); }
                catch (Exception error) when (PlatformFileAdapters.IsStorageIntegrityFailure(error))
                { return FileFailure(invocation.HttpContext, 500); }
            });
            files.MapGet("", (Guid entityId, int? offset, int? limit, int? state, HttpContext http,
                IdentityDatabase platform, MasterDataDatabase database, PlatformFileAdapters adapters, CancellationToken ct) =>
                QueryMasterDataFilesAsync(entityId, offset, limit, state, location, http, platform, database, adapters, ct));
            files.MapGet("/{fileId:guid}", (Guid entityId, Guid fileId, HttpContext http, IdentityDatabase platform,
                MasterDataDatabase database, PlatformFileAdapters adapters, CancellationToken ct) =>
                ReadMasterDataFileAsync(entityId, fileId, location, content: false, http, platform, database, adapters, ct));
            files.MapGet("/{fileId:guid}/content", (Guid entityId, Guid fileId, HttpContext http, IdentityDatabase platform,
                MasterDataDatabase database, PlatformFileAdapters adapters, CancellationToken ct) =>
                ReadMasterDataFileAsync(entityId, fileId, location, content: true, http, platform, database, adapters, ct));
            files.MapPut("/{fileId:guid}", (Guid entityId, Guid fileId, FileBindingInput input, HttpContext http,
                IdentityDatabase platform, MasterDataDatabase database, CancellationToken ct) =>
                ChangeMasterDataFileAsync(entityId, fileId, input, location, attached: true, http, platform, database, ct));
            files.MapDelete("/{fileId:guid}", (Guid entityId, Guid fileId,
                [Microsoft.AspNetCore.Mvc.FromBody] FileBindingInput input, HttpContext http,
                IdentityDatabase platform, MasterDataDatabase database, CancellationToken ct) =>
                ChangeMasterDataFileAsync(entityId, fileId, input, location, attached: false, http, platform, database, ct));
        }

    }

    private static async Task<IResult> QueryMasterDataFilesAsync(Guid entityId, int? offset, int? limit, int? state,
        bool location, HttpContext http, IdentityDatabase platform, MasterDataDatabase database,
        PlatformFileAdapters adapters, CancellationToken ct)
    {
        if (entityId == Guid.Empty || http.Request.Query.Any(item =>
            item.Key is not ("offset" or "limit" or "state") || item.Value.Count != 1 || string.IsNullOrWhiteSpace(item.Value[0])))
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WmsFiles")
                .LogWarning("Business file catalog rejected invalid identity or query fields.");
            return Results.BadRequest();
        }
        var subject = IdentityRuntime.Subject(http.User);
        await using var owner = platform.Open(subject.TenantId);
        var access = owner.BusinessFiles(new(FileEntityOwner(location), entityId),
            new MasterDataFileAuthority(database, location), adapters.Storage, adapters.Scanner);
        var result = await access.QueryAsync(subject, offset ?? 0, limit ?? 25, state, ct);
        return FileOutcome(result.Decision, result);
    }

    private static async Task<IResult> ChangeMasterDataFileAsync(Guid entityId, Guid fileId, FileBindingInput input,
        bool location, bool attached, HttpContext http, IdentityDatabase platform, MasterDataDatabase database, CancellationToken ct)
    {
        if (entityId == Guid.Empty || fileId == Guid.Empty
            || !TryWmsMutationOperation(http, "attachment", out var operation)) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        var entity = new FileEntityReference(FileEntityOwner(location), entityId);
        await using var owner = platform.Open(subject.TenantId);
        var bindings = owner.BusinessFileBindings(entity, new MasterDataFileAuthority(database, location));
        var result = attached
            ? await bindings.BindAsync(subject, fileId, operation, Guid.Parse(http.TraceIdentifier), input.ExpectedVersion, ct)
            : await bindings.UnbindAsync(subject, fileId, operation, Guid.Parse(http.TraceIdentifier), input.ExpectedVersion, ct);
        if (result.Decision != FileAccessDecision.Success) return FileOutcome(result.Decision, result);
        var receipt = new { result.Id, result.Version, result.Attached };
        return attached && !result.Replayed
            ? Results.Created($"/api/wms/{(location ? "locations" : "warehouses")}/{entityId:D}/files/{fileId:D}", receipt)
            : Results.Ok(receipt);
    }

    private static async Task<IResult> ReadMasterDataFileAsync(Guid entityId, Guid fileId, bool location, bool content,
        HttpContext http, IdentityDatabase platform, MasterDataDatabase database, PlatformFileAdapters adapters, CancellationToken ct)
    {
        if (entityId == Guid.Empty || fileId == Guid.Empty) return Results.BadRequest();
        var subject = IdentityRuntime.Subject(http.User);
        var entity = new FileEntityReference(FileEntityOwner(location), entityId);
        await using var owner = platform.Open(subject.TenantId);
        var access = owner.BusinessFiles(entity, new MasterDataFileAuthority(database, location), adapters.Storage, adapters.Scanner);
        if (!content)
        {
            var result = await access.ReadAsync(subject, fileId, ct);
            return FileOutcome(result.Decision, result);
        }
        var download = await access.DownloadAsync(subject, fileId, Guid.Parse(http.TraceIdentifier), ct);
        if (download.Decision != FileAccessDecision.Success) return FileOutcome(download.Decision, download);
        var bytes = download.Download ?? throw new InvalidOperationException("Successful download has no owned content.");
        http.Response.RegisterForDispose(bytes);
        return new StagingFileDownload(bytes);
    }

    private static string FileEntityOwner(bool location) => location ? "wms-location" : "wms-warehouse";

    private static IResult FileOutcome(FileAccessDecision decision, object result) => decision switch
    {
        FileAccessDecision.Success => Results.Ok(result),
        FileAccessDecision.Forbidden => Results.Forbid(),
        FileAccessDecision.NotFound => Results.NotFound(),
        FileAccessDecision.Invalid => Results.BadRequest(),
        FileAccessDecision.VersionRequired => Results.StatusCode(428),
        FileAccessDecision.VersionConflict => Results.StatusCode(412),
        FileAccessDecision.StateConflict or FileAccessDecision.IdempotencyConflict => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown business file read outcome.")
    };

    private static IResult FileFailure(HttpContext http, int status)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WmsFiles")
            .LogWarning("Business file dependency failed with status {StatusCode}.", status);
        return Results.StatusCode(status);
    }

    private sealed class MasterDataFileAuthority(MasterDataDatabase database, bool location) : IBusinessFileBindingAuthorization
    {
        public async Task<bool> CanReadAsync(TenantContext context, FileEntityReference entity, CancellationToken cancellationToken)
        {
            bool? sourceLocation = entity.Owner switch
            {
                "wms-warehouse" => false,
                "wms-location" => true,
                _ => null
            };
            if (sourceLocation is null) return false;
            await using var owner = database.Open(context.TenantId);
            var subject = new MasterDataSubject(context.TenantId, context.ActorId, context.SessionId);
            var result = sourceLocation.Value
                ? await owner.ReadLocationAsync(subject, entity.EntityId, cancellationToken)
                : await owner.ReadWarehouseAsync(subject, entity.EntityId, cancellationToken);
            return Allowed(result.Decision);
        }

        public async Task<bool> CanChangeAsync(TenantContext context, FileEntityReference entity,
            FileBindingAction action, CancellationToken cancellationToken)
        {
            if (entity.Owner != FileEntityOwner(location))
                throw new InvalidOperationException("Business file owner does not match the named entity adapter.");
            var change = action switch
            {
                FileBindingAction.Attach => MasterDataAttachmentChange.Attach,
                FileBindingAction.Detach => MasterDataAttachmentChange.Detach,
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            await using var owner = database.Open(context.TenantId);
            var subject = new MasterDataSubject(context.TenantId, context.ActorId, context.SessionId);
            return Allowed(location
                ? await owner.AuthorizeLocationFileAsync(subject, entity.EntityId, change, cancellationToken)
                : await owner.AuthorizeWarehouseFileAsync(subject, entity.EntityId, change, cancellationToken));
        }

        private static bool Allowed(MasterDataReadDecision decision) => decision switch
        {
            MasterDataReadDecision.Success => true,
            MasterDataReadDecision.Forbidden or MasterDataReadDecision.NotFound => false,
            _ => throw new InvalidOperationException("Validated business file entity produced an invalid read outcome.")
        };
    }
}
