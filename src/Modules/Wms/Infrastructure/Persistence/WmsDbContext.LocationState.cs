using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<LocationStateChange> LocationStateChanges => Set<LocationStateChange>();

    private void ConfigureLocationState(ModelBuilder builder)
    {
        var change = builder.Entity<LocationStateChange>();
        change.ToTable("wms_location_state_changes");
        change.HasKey(item => new { item.TenantId, item.OperationId });
        change.HasQueryFilter(item => item.TenantId == TenantId);
        change.HasIndex(item => new { item.TenantId, item.LocationId, item.AfterVersion }).IsUnique();
        change.Property(item => item.BeforeVersion).HasMaxLength(36);
        change.Property(item => item.AfterVersion).HasMaxLength(36);
        change.Property(item => item.Reason).HasMaxLength(256);
        change.HasOne<WarehouseLocation>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.LocationId })
            .OnDelete(DeleteBehavior.Restrict);
        change.HasOne<Warehouse>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.WarehouseId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateLocationStateWrites()
    {
        if (ChangeTracker.Entries<WarehouseLocation>().Any(entry => entry.State == EntityState.Deleted)
            || ChangeTracker.Entries<LocationStateChange>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Locations and their state facts must be retained.");
        var locations = ChangeTracker.Entries<WarehouseLocation>().Where(entry => entry.State == EntityState.Modified).ToArray();
        var changes = ChangeTracker.Entries<LocationStateChange>().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity).ToArray();
        if ((locations.Length > 0 || changes.Length > 0) && Database.CurrentTransaction is null)
            throw new InvalidOperationException("Location transitions require an explicit owner transaction.");
        foreach (var entry in locations)
        {
            if (entry.Properties.Any(property => property.IsModified && property.Metadata.Name
                    is not (nameof(WarehouseLocation.Enabled) or nameof(WarehouseLocation.Version)))
                || !entry.Property(item => item.Enabled).IsModified || !entry.Property(item => item.Version).IsModified
                || entry.Entity.Version == entry.Property(item => item.Version).OriginalValue
                || changes.Count(change => change.TenantId == entry.Entity.TenantId && change.LocationId == entry.Entity.Id
                    && change.WarehouseId == entry.Entity.WarehouseId
                    && change.BeforeVersion == entry.Property(item => item.Version).OriginalValue
                    && change.BeforeEnabled == entry.Property(item => item.Enabled).OriginalValue
                    && change.AfterVersion == entry.Entity.Version && change.Enabled == entry.Entity.Enabled
                    && WarehouseStateChange.ValidReason(change.Reason)) != 1)
                throw new InvalidOperationException("A location transition requires one exact immutable state fact.");
        }
        foreach (var change in changes)
            if (locations.Count(entry => entry.Entity.Id == change.LocationId && entry.Entity.Version == change.AfterVersion) != 1)
                throw new InvalidOperationException("A location state fact cannot be detached from its transition.");
    }
}
