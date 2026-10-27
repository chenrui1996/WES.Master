using System.Collections.Immutable;
using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

/// <summary>Transient authority result; empty allowed creators are different from missing full-projection authority.</summary>
public sealed record MasterDataCreatorAccess(bool Authorized, IReadOnlySet<Guid> CreatorIds);
/// <summary>Realtime query bounds and exact optional filters; WarehouseId is supported only by location catalogs.</summary>
public sealed record MasterDataQuery(int Offset = 0, int Limit = 25, string? Code = null, bool? Enabled = null, Guid? WarehouseId = null);
/// <summary>One catalog kind on Success; no totals, hidden creators, inventory or immutable snapshot claim.</summary>
public sealed record MasterDataCatalogResult(MasterDataReadDecision Decision, ImmutableArray<WarehouseMasterView> Warehouses,
    ImmutableArray<LocationMasterView> Locations, int Offset, int Limit);

internal sealed partial class MasterDataAccess
{
    internal Task<MasterDataCatalogResult> QueryWarehousesAsync(MasterDataSubject subject, MasterDataQuery query, CancellationToken ct = default) =>
        QueryAsync(subject, query, location: false, ct);
    internal Task<MasterDataCatalogResult> QueryLocationsAsync(MasterDataSubject subject, MasterDataQuery query, CancellationToken ct = default) =>
        QueryAsync(subject, query, location: true, ct);

    private async Task<MasterDataCatalogResult> QueryAsync(MasterDataSubject subject, MasterDataQuery query, bool location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Master-data catalogs require a clean WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(MasterDataReadDecision.Forbidden);
        if (query.Offset is < 0 or > 10000 || query.Limit is not (25 or 50 or 100)
            || query.Code is not null && !MasterDataValue.IsCode(query.Code)
            || query.WarehouseId == Guid.Empty || !location && query.WarehouseId.HasValue)
            return Rejected(MasterDataReadDecision.Invalid);
        try
        {
            var authority = await _authorization.ReadCreatorsAsync(subject,
                location ? "wms.locations.read" : "wms.warehouses.read", location ? LocationFields : WarehouseFields, ct);
            ct.ThrowIfCancellationRequested();
            if (authority is null || authority.CreatorIds is null || authority.CreatorIds.Contains(Guid.Empty))
                throw new InvalidOperationException("Master-data catalog authorization returned an invalid decision.");
            if (!authority.Authorized) return Rejected(MasterDataReadDecision.Forbidden);
            var changes = CreationFactsFor(authority.CreatorIds);
            var code = query.Code?.ToUpperInvariant();
            if (location)
            {
                var rows = await (from item in database.Locations.AsNoTracking()
                                  join change in changes on new { item.TenantId, Id = (Guid?)item.Id }
                                      equals new { change.TenantId, Id = change.LocationId }
                                  where (code == null || item.Code == code)
                                      && (!query.Enabled.HasValue || item.Enabled == query.Enabled.Value)
                                      && (!query.WarehouseId.HasValue || item.WarehouseId == query.WarehouseId.Value)
                                  orderby item.Code, item.Id
                                  select new LocationMasterView(item.Id, item.WarehouseId, item.PackId, item.SchemaVersion,
                                      item.Code, item.Name, item.Enabled, item.Version, item.CreatedAt))
                    .Skip(query.Offset).Take(query.Limit).ToListAsync(ct);
                ct.ThrowIfCancellationRequested();
                return new(MasterDataReadDecision.Success, [], rows.ToImmutableArray(), query.Offset, query.Limit);
            }
            var warehouses = await (from item in database.Warehouses.AsNoTracking()
                                    join change in changes on new { item.TenantId, Id = item.Id }
                                        equals new { change.TenantId, Id = change.WarehouseId }
                                    where change.LocationId == null && (code == null || item.Code == code)
                                        && (!query.Enabled.HasValue || item.Enabled == query.Enabled.Value)
                                    orderby item.Code, item.Id
                                    select new WarehouseMasterView(item.Id, item.Code, item.Name, item.Enabled, item.Version, item.CreatedAt))
                .Skip(query.Offset).Take(query.Limit).ToListAsync(ct);
            ct.ThrowIfCancellationRequested();
            return new(MasterDataReadDecision.Success, warehouses.ToImmutableArray(), [], query.Offset, query.Limit);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS master-data catalog failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }

        MasterDataCatalogResult Rejected(MasterDataReadDecision decision)
        {
            logger.LogWarning("WMS master-data catalog rejected {Code}", decision.ToString());
            return new(decision, [], [], query.Offset, query.Limit);
        }
    }

    private IQueryable<MasterDataChange> CreationFactsFor(IReadOnlySet<Guid> creators)
    {
        // Oracle MySQL EF cannot bind Guid primitive collections reliably; keep the filter typed on the mapped column.
        var actor = Expression.Parameter(typeof(MasterDataChange), "change");
        Expression allowed = Expression.Constant(false);
        foreach (var creator in creators)
            allowed = Expression.OrElse(allowed, Expression.Equal(Expression.Property(actor, nameof(MasterDataChange.ActorId)), Expression.Constant(creator)));
        return database.MasterDataChanges.AsNoTracking().Where(Expression.Lambda<Func<MasterDataChange, bool>>(allowed, actor));
    }
}
