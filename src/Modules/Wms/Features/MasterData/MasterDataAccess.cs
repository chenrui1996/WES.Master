using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

/// <summary>Trusted validated principal identities; never accepts client roles, permissions or department metadata.</summary>
public sealed record MasterDataSubject(Guid TenantId, Guid ActorId, Guid SessionId);

/// <summary>
/// Current external authority for a WMS-owned resource. Implementations must recheck live session/roles and resolve
/// the trusted creator's current department through its owner, without exposing another module's private tables.
/// </summary>
public interface IMasterDataAuthorization
{
    /// <summary>Returns exact currently visible field names for this permission and creator; missing authority returns no fields.</summary>
    Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator,
        CancellationToken cancellationToken);
}

/// <summary>Finite read decisions; only Success carries a projection.</summary>
public enum MasterDataReadDecision
{
    /// <summary>All fields in the exact projection are currently authorized.</summary>
    Success,
    /// <summary>The trusted context or current resource/field authority does not permit disclosure.</summary>
    Forbidden,
    /// <summary>The identity and its creation provenance were not found in this tenant.</summary>
    NotFound,
    /// <summary>The requested identity is invalid.</summary>
    Invalid
}

/// <summary>Safe warehouse definition; Enabled false is not allocation or physical safety evidence.</summary>
public sealed record WarehouseMasterView(Guid Id, string Code, string Name, bool Enabled, string Version, DateTimeOffset CreatedAt);
/// <summary>Safe pinned semantic location definition; no device addresses, inventory or owner audit metadata.</summary>
public sealed record LocationMasterView(Guid Id, Guid WarehouseId, Guid PackId, string SchemaVersion,
    string Code, string Name, bool Enabled, string Version, DateTimeOffset CreatedAt);
/// <summary>One authorized definition on Success; all other decisions carry neither projection.</summary>
public sealed record MasterDataReadResult(MasterDataReadDecision Decision,
    WarehouseMasterView? Warehouse = null, LocationMasterView? Location = null);

/// <summary>Tenant-owned single-resource reads with provenance derived inside WMS, not supplied by the caller.</summary>
internal sealed class MasterDataAccess(WmsDbContext database, IMasterDataAuthorization authorization, ILogger<MasterDataAccess> logger)
{
    private static readonly string[] WarehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
    private static readonly string[] LocationFields = [.. WarehouseFields, "WarehouseId", "PackId", "SchemaVersion"];
    private readonly IMasterDataAuthorization _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));

    internal Task<MasterDataReadResult> ReadWarehouseAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) =>
        ReadAsync(subject, id, location: false, ct);
    internal Task<MasterDataReadResult> ReadLocationAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) =>
        ReadAsync(subject, id, location: true, ct);

    private async Task<MasterDataReadResult> ReadAsync(MasterDataSubject subject, Guid id, bool location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Master-data reads require a clean WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Reject(MasterDataReadDecision.Forbidden);
        if (id == Guid.Empty) return Reject(MasterDataReadDecision.Invalid);
        try
        {
            var row = location
                ? await (from item in database.Locations.AsNoTracking()
                         join change in database.MasterDataChanges.AsNoTracking()
                             on new { item.TenantId, Id = (Guid?)item.Id } equals new { change.TenantId, Id = change.LocationId }
                         where item.Id == id
                         select new Definition(change.ActorId, null, new LocationMasterView(item.Id, item.WarehouseId,
                             item.PackId, item.SchemaVersion, item.Code, item.Name, item.Enabled, item.Version, item.CreatedAt)))
                    .SingleOrDefaultAsync(ct)
                : await (from item in database.Warehouses.AsNoTracking()
                         join change in database.MasterDataChanges.AsNoTracking()
                             on new { item.TenantId, Id = item.Id } equals new { change.TenantId, Id = change.WarehouseId }
                         where item.Id == id && change.LocationId == null
                         select new Definition(change.ActorId, new WarehouseMasterView(item.Id, item.Code, item.Name,
                             item.Enabled, item.Version, item.CreatedAt), null))
                    .SingleOrDefaultAsync(ct);
            if (row is null) return new(MasterDataReadDecision.NotFound);
            var fields = await _authorization.ReadFieldsAsync(subject,
                location ? "wms.locations.read" : "wms.warehouses.read", row.Creator, ct);
            ct.ThrowIfCancellationRequested();
            if (fields is null) throw new InvalidOperationException("Master-data authorization returned no decision.");
            var exact = fields.ToHashSet(StringComparer.Ordinal);
            if (!(location ? LocationFields : WarehouseFields).All(exact.Contains))
                return Reject(MasterDataReadDecision.Forbidden);
            return new(MasterDataReadDecision.Success, row.Warehouse, row.Location);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS master-data read failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
    }

    private MasterDataReadResult Reject(MasterDataReadDecision decision)
    {
        logger.LogWarning("WMS master-data read rejected {Code}", decision.ToString());
        return new(decision);
    }

    private sealed record Definition(Guid Creator, WarehouseMasterView? Warehouse, LocationMasterView? Location);
}
