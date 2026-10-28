using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<StockUnit> StockUnits => Set<StockUnit>();
    internal DbSet<StockItem> StockItems => Set<StockItem>();
    internal DbSet<StockContainer> StockContainers => Set<StockContainer>();
    internal DbSet<StockDimensionRegistration> DimensionRegistrations => Set<StockDimensionRegistration>();

    private void ConfigureStockResources(ModelBuilder builder)
    {
        var unit = builder.Entity<StockUnit>();
        unit.ToTable("wms_stock_units"); unit.HasKey(row => new { row.TenantId, row.Id });
        unit.HasQueryFilter(row => row.TenantId == TenantId);
        unit.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        unit.HasIndex(row => new { row.TenantId, row.OperationId }).IsUnique();
        unit.Property(row => row.Code).HasMaxLength(64).UseCollation("utf8mb4_bin");
        unit.Property(row => row.Name).HasMaxLength(128);
        unit.Property(row => row.Version).HasMaxLength(36);

        var item = builder.Entity<StockItem>();
        item.ToTable("wms_stock_items"); item.HasKey(row => new { row.TenantId, row.Id });
        item.HasQueryFilter(row => row.TenantId == TenantId);
        item.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        item.HasIndex(row => new { row.TenantId, row.OperationId }).IsUnique();
        item.Property(row => row.Code).HasMaxLength(64).UseCollation("utf8mb4_bin");
        item.Property(row => row.Name).HasMaxLength(128);
        item.Property(row => row.Version).HasMaxLength(36);
        item.HasOne<StockUnit>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.BaseUnitId }).OnDelete(DeleteBehavior.Restrict);

        var container = builder.Entity<StockContainer>();
        container.ToTable("wms_stock_containers"); container.HasKey(row => new { row.TenantId, row.Id });
        container.HasQueryFilter(row => row.TenantId == TenantId);
        container.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        container.HasIndex(row => new { row.TenantId, row.OperationId }).IsUnique();
        container.Property(row => row.Code).HasMaxLength(64).UseCollation("utf8mb4_bin");
        container.Property(row => row.Name).HasMaxLength(128);
        container.Property(row => row.Version).HasMaxLength(36);
        container.HasOne<Warehouse>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.WarehouseId }).OnDelete(DeleteBehavior.Restrict);

        var registration = builder.Entity<StockDimensionRegistration>();
        registration.ToTable("wms_stock_dimension_registrations"); registration.HasKey(row => new { row.TenantId, row.OperationId });
        registration.HasQueryFilter(row => row.TenantId == TenantId);
        registration.HasIndex(row => new { row.TenantId, row.BalanceId }).IsUnique();
        registration.Property(row => row.Batch).HasMaxLength(128).UseCollation("utf8mb4_bin");
        registration.Property(row => row.AfterVersion).HasMaxLength(36);
        registration.HasOne<StockBalance>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.BalanceId }).OnDelete(DeleteBehavior.Restrict);
        registration.HasOne<StockItem>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.ItemId }).OnDelete(DeleteBehavior.Restrict);
        registration.HasOne<StockUnit>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.UnitId }).OnDelete(DeleteBehavior.Restrict);
        registration.HasOne<StockContainer>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.ContainerId }).OnDelete(DeleteBehavior.Restrict);
        registration.HasOne<WarehouseLocation>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.LocationId }).OnDelete(DeleteBehavior.Restrict);
        registration.HasOne<Warehouse>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.WarehouseId }).OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateStockResourceWrites()
    {
        var resources = ChangeTracker.Entries().Where(entry => entry.Entity is StockUnit or StockItem or StockContainer or StockDimensionRegistration
            && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (resources.Any(entry => entry.State != EntityState.Added))
            throw new InvalidOperationException("Registered stock resources and dimension creation facts are immutable.");
        if (resources.Length > 0 && Database.CurrentTransaction is null)
            throw new InvalidOperationException("Stock resource registration requires an explicit owner transaction.");
        foreach (var registration in resources.Select(entry => entry.Entity).OfType<StockDimensionRegistration>())
            if (ChangeTracker.Entries<StockBalance>().Count(entry => entry.State == EntityState.Added
                && entry.Entity.TenantId == registration.TenantId && entry.Entity.Id == registration.BalanceId
                && entry.Entity.WarehouseId == registration.WarehouseId && entry.Entity.LocationId == registration.LocationId
                && entry.Entity.ItemId == registration.ItemId && entry.Entity.UnitId == registration.UnitId
                && entry.Entity.ContainerId == registration.ContainerId && entry.Entity.Batch == registration.Batch
                && entry.Entity.Precision == registration.Precision && entry.Entity.Quantity == 0m
                && entry.Entity.Version == registration.AfterVersion) != 1)
                throw new InvalidOperationException("A dimension registration requires one exact newly created empty balance.");
    }
}
