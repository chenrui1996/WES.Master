using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    /// <summary>WMS-owned warehouse identities; no Platform or Pack foreign keys.</summary>
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    /// <summary>Disabled location definitions with immutable schema lineage.</summary>
    public DbSet<WarehouseLocation> Locations => Set<WarehouseLocation>();
    /// <summary>Permanent immutable creation facts and operation identities.</summary>
    public DbSet<MasterDataChange> MasterDataChanges => Set<MasterDataChange>();

    private void ConfigureMasterData(ModelBuilder builder)
    {
        var warehouse = builder.Entity<Warehouse>();
        warehouse.ToTable("wms_warehouses");
        warehouse.HasKey(item => new { item.TenantId, item.Id });
        warehouse.HasQueryFilter(item => item.TenantId == TenantId);
        warehouse.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
        warehouse.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        warehouse.Property(item => item.Name).HasColumnType("varchar(128)").HasMaxLength(128);
        warehouse.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();

        var location = builder.Entity<WarehouseLocation>();
        location.ToTable("wms_locations");
        location.HasKey(item => new { item.TenantId, item.Id });
        location.HasQueryFilter(item => item.TenantId == TenantId);
        location.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
        location.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        location.Property(item => item.Name).HasColumnType("varchar(128)").HasMaxLength(128);
        location.Property(item => item.SchemaVersion).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        location.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        location.HasOne<Warehouse>().WithMany().HasForeignKey(item => new { item.TenantId, item.WarehouseId })
            .OnDelete(DeleteBehavior.Restrict);

        var change = builder.Entity<MasterDataChange>();
        change.ToTable("wms_master_data_changes");
        change.HasKey(item => new { item.TenantId, item.OperationId });
        change.HasQueryFilter(item => item.TenantId == TenantId);
        change.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        change.Property(item => item.Name).HasColumnType("varchar(128)").HasMaxLength(128);
        change.Property(item => item.SchemaVersion).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        change.Property(item => item.AfterVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        change.HasOne<Warehouse>().WithMany().HasForeignKey(item => new { item.TenantId, item.WarehouseId })
            .OnDelete(DeleteBehavior.Restrict);
        change.HasOne<WarehouseLocation>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.LocationId })
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureWarehouseState(builder);
    }

    private void ValidateMasterDataWrites()
    {
        var warehouses = ChangeTracker.Entries<Warehouse>().Where(item => item.State == EntityState.Added).Select(item => item.Entity).ToArray();
        var locations = ChangeTracker.Entries<WarehouseLocation>().Where(item => item.State == EntityState.Added).Select(item => item.Entity).ToArray();
        var changes = ChangeTracker.Entries<MasterDataChange>().Where(item => item.State == EntityState.Added).Select(item => item.Entity).ToArray();
        foreach (var entry in ChangeTracker.Entries().Where(item =>
            item.Entity is WarehouseLocation or MasterDataChange
            && item.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Master-data identities and creation facts cannot be changed or deleted by the creation unit of work.");
        ValidateWarehouseStateWrites();
        foreach (var warehouse in warehouses)
            if (warehouse.Enabled || changes.Count(change => Matches(change, warehouse)) != 1)
                throw new InvalidOperationException("Every disabled warehouse creation requires exactly one matching new creation fact.");
        foreach (var location in locations)
            if (location.Enabled || changes.Count(change => Matches(change, location)) != 1)
                throw new InvalidOperationException("Every disabled location creation requires exactly one matching new creation fact.");
        foreach (var change in changes)
            if (change.LocationId.HasValue
                ? !locations.Any(location => Matches(change, location))
                : !warehouses.Any(warehouse => Matches(change, warehouse)))
                throw new InvalidOperationException("A new creation fact cannot describe an existing or unrelated master-data identity.");
    }

    private static bool Matches(MasterDataChange change, Warehouse warehouse) => change.TenantId == warehouse.TenantId
        && change.WarehouseId == warehouse.Id && change.LocationId is null && change.PackId is null && change.SchemaVersion is null
        && change.Code == warehouse.Code && change.Name == warehouse.Name && change.AfterVersion == warehouse.Version
        && change.OccurredAt == warehouse.CreatedAt;
    private static bool Matches(MasterDataChange change, WarehouseLocation location) => change.TenantId == location.TenantId
        && change.LocationId == location.Id && change.WarehouseId == location.WarehouseId && change.PackId == location.PackId
        && change.SchemaVersion == location.SchemaVersion && change.Code == location.Code && change.Name == location.Name
        && change.AfterVersion == location.Version && change.OccurredAt == location.CreatedAt;
}
