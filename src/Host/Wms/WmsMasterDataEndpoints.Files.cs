using Microsoft.AspNetCore.Routing;
using Wes.Host.Identity;
using Wes.Host.Platform;
using Wes.Platform;
using Wes.Platform.Features.Files;
using Wes.Platform.Features.Tenancy;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;

namespace Wes.Host.Wms;

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
            files.MapGet("/{fileId:guid}", (Guid entityId, Guid fileId, HttpContext http, IdentityDatabase platform,
                MasterDataDatabase database, PlatformFileAdapters adapters, CancellationToken ct) =>
                ReadMasterDataFileAsync(entityId, fileId, location, content: false, http, platform, database, adapters, ct));
            files.MapGet("/{fileId:guid}/content", (Guid entityId, Guid fileId, HttpContext http, IdentityDatabase platform,
                MasterDataDatabase database, PlatformFileAdapters adapters, CancellationToken ct) =>
                ReadMasterDataFileAsync(entityId, fileId, location, content: true, http, platform, database, adapters, ct));
        }
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
        FileAccessDecision.VersionConflict => Results.StatusCode(412),
        FileAccessDecision.StateConflict => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown business file read outcome.")
    };

    private static IResult FileFailure(HttpContext http, int status)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WmsFiles")
            .LogWarning("Business file dependency failed with status {StatusCode}.", status);
        return Results.StatusCode(status);
    }

    private sealed class MasterDataFileAuthority(MasterDataDatabase database, bool location) : IBusinessFileAuthorization
    {
        public async Task<bool> CanReadAsync(TenantContext context, FileEntityReference entity, CancellationToken cancellationToken)
        {
            if (entity.Owner != FileEntityOwner(location))
                throw new InvalidOperationException("Business file owner does not match the named entity adapter.");
            await using var owner = database.Open(context.TenantId);
            var subject = new MasterDataSubject(context.TenantId, context.ActorId, context.SessionId);
            var result = location
                ? await owner.ReadLocationAsync(subject, entity.EntityId, cancellationToken)
                : await owner.ReadWarehouseAsync(subject, entity.EntityId, cancellationToken);
            return result.Decision switch
            {
                MasterDataReadDecision.Success => true,
                MasterDataReadDecision.Forbidden or MasterDataReadDecision.NotFound => false,
                _ => throw new InvalidOperationException("Validated business file entity produced an invalid read outcome.")
            };
        }
    }
}
