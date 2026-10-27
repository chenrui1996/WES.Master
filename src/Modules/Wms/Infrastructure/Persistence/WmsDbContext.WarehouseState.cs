using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<WarehouseStateChange> WarehouseStateChanges => Set<WarehouseStateChange>();

    private void ConfigureWarehouseState(ModelBuilder builder)
    {
        var change = builder.Entity<WarehouseStateChange>();
        change.ToTable("wms_warehouse_state_changes");
        change.HasKey(item => new { item.TenantId, item.OperationId });
        change.HasQueryFilter(item => item.TenantId == TenantId);
        change.HasIndex(item => new { item.TenantId, item.WarehouseId, item.AfterVersion }).IsUnique();
        change.Property(item => item.BeforeVersion).HasMaxLength(36);
        change.Property(item => item.AfterVersion).HasMaxLength(36);
        change.Property(item => item.Reason).HasMaxLength(256);
        change.HasOne<Warehouse>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.WarehouseId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateWarehouseStateWrites()
    {
        if (ChangeTracker.Entries<Warehouse>().Any(entry => entry.State == EntityState.Deleted)
            || ChangeTracker.Entries<WarehouseStateChange>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Warehouse identities and state facts must be retained.");
        var warehouses = ChangeTracker.Entries<Warehouse>().Where(entry => entry.State == EntityState.Modified).ToArray();
        var changes = ChangeTracker.Entries<WarehouseStateChange>().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity).ToArray();
        foreach (var entry in warehouses)
        {
            if (entry.Properties.Any(property => property.IsModified && property.Metadata.Name
                    is not (nameof(Warehouse.Enabled) or nameof(Warehouse.Version)))
                || !entry.Property(item => item.Enabled).IsModified || !entry.Property(item => item.Version).IsModified
                || entry.Entity.Version == entry.Property(item => item.Version).OriginalValue
                || changes.Count(change => change.WarehouseId == entry.Entity.Id
                    && change.BeforeVersion == entry.Property(item => item.Version).OriginalValue
                    && change.BeforeEnabled == entry.Property(item => item.Enabled).OriginalValue
                    && change.AfterVersion == entry.Entity.Version && change.Enabled == entry.Entity.Enabled
                    && WarehouseStateChange.ValidReason(change.Reason)) != 1)
                throw new InvalidOperationException("A warehouse state change requires exactly one matching immutable fact.");
        }
        foreach (var change in changes)
            if (warehouses.Count(entry => entry.Entity.Id == change.WarehouseId && entry.Entity.Version == change.AfterVersion) != 1)
                throw new InvalidOperationException("A warehouse state fact cannot be detached from its matching transition.");
    }
}
