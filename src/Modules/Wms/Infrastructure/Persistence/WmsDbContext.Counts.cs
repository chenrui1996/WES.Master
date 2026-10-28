using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<StockCount> Counts => Set<StockCount>();
    internal DbSet<StockCountFact> CountFacts => Set<StockCountFact>();

    private void ConfigureCounts(ModelBuilder builder)
    {
        var plan = builder.Entity<StockCount>();
        plan.ToTable("wms_stock_counts");
        plan.HasKey(row => new { row.TenantId, row.Id });
        plan.HasQueryFilter(row => row.TenantId == TenantId);
        plan.Ignore(row => row.Lines); plan.Ignore(row => row.Active);
        plan.Property(row => row.Code).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        plan.Property(row => row.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        plan.Property(row => row.LinesJson).HasColumnType("longtext");
        plan.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        plan.HasIndex(row => new { row.TenantId, row.WarehouseId, row.LocationId, row.State });
        plan.HasOne<WarehouseLocation>().WithMany().HasForeignKey(row => new { row.TenantId, row.LocationId }).OnDelete(DeleteBehavior.Restrict);
        var fact = builder.Entity<StockCountFact>();
        fact.ToTable("wms_stock_count_facts");
        fact.HasKey(row => new { row.TenantId, row.Id });
        fact.HasQueryFilter(row => row.TenantId == TenantId);
        fact.Property(row => row.Input).HasColumnType("longtext");
        fact.Property(row => row.LinesJson).HasColumnType("longtext");
        fact.Property(row => row.AfterVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        fact.HasIndex(row => new { row.TenantId, row.OperationId }).IsUnique();
        fact.HasIndex(row => new { row.TenantId, row.PlanId, row.Sequence }).IsUnique();
        fact.HasOne<StockCount>().WithMany().HasForeignKey(row => new { row.TenantId, row.PlanId }).OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateCountWrites()
    {
        var adjustments = ChangeTracker.Entries<StockPosting>().Where(row => row.State == EntityState.Added
            && row.Entity.Kind is StockPostingKind.AdjustmentIn or StockPostingKind.AdjustmentOut).Select(row => row.Entity).ToArray();
        foreach (var posting in adjustments)
        {
            var plan = ChangeTracker.Entries<StockCount>().SingleOrDefault(row => row.State == EntityState.Modified
                && row.Entity.Id == posting.SourceDocumentId)?.Entity;
            var fact = ChangeTracker.Entries<StockCountFact>().SingleOrDefault(row => row.State == EntityState.Added
                && row.Entity.PlanId == posting.SourceDocumentId && row.Entity.Action == StockCountAction.Adjust)?.Entity;
            var line = plan?.Lines.SingleOrDefault(row => row.Id == posting.SourceLineId);
            if (plan is null || fact is null || line?.Quantity is null || plan.State != StockCountState.Completed
                || plan.Generation is null || fact.Generation != plan.Generation || fact.ActorId != posting.ActorId
                || fact.CorrelationId != posting.CorrelationId || line.CountedBy == posting.ActorId
                || posting.ExecutionUnitId != line.Id || posting.Quantity != Math.Abs(line.Quantity.Value - line.BookQuantity)
                || (line.Quantity > line.BookQuantity
                    ? posting.Kind != StockPostingKind.AdjustmentIn || posting.SourceBalanceId is not null || posting.TargetBalanceId != line.BalanceId
                    : posting.Kind != StockPostingKind.AdjustmentOut || posting.TargetBalanceId is not null || posting.SourceBalanceId != line.BalanceId))
                throw new InvalidOperationException("Adjustment postings require the exact approved current count generation.");
        }
        foreach (var entry in ChangeTracker.Entries<StockCountFact>().Where(row => row.State != EntityState.Unchanged))
        {
            if (entry.State != EntityState.Added || Database.CurrentTransaction is null)
                throw new InvalidOperationException("Count facts are append-only transaction facts.");
            var plan = ChangeTracker.Entries<StockCount>().SingleOrDefault(row => row.Entity.Id == entry.Entity.PlanId
                && row.State is EntityState.Added or EntityState.Modified)?.Entity;
            if (plan is null || plan.Version != entry.Entity.AfterVersion || plan.Sequence != entry.Entity.Sequence
                || plan.LinesJson != entry.Entity.LinesJson || plan.State != entry.Entity.State || plan.Generation != entry.Entity.Generation)
                throw new InvalidOperationException("Count facts must explain the exact current generation.");
        }
        foreach (var entry in ChangeTracker.Entries<StockCount>().Where(row => row.State != EntityState.Unchanged))
        {
            if (entry.State == EntityState.Deleted || Database.CurrentTransaction is null
                || ChangeTracker.Entries<StockCountFact>().Count(row => row.State == EntityState.Added
                    && row.Entity.PlanId == entry.Entity.Id && row.Entity.AfterVersion == entry.Entity.Version) != 1)
                throw new InvalidOperationException("Count transitions require one permanent fact.");
            if (entry.State == EntityState.Modified && entry.Properties.Any(row => row.IsModified
                && row.Metadata.Name is nameof(StockCount.Code) or nameof(StockCount.CreatorId) or nameof(StockCount.WarehouseId)
                    or nameof(StockCount.LocationId) or nameof(StockCount.CreatedAt)))
                throw new InvalidOperationException("Count scope and original creator are immutable.");
            if (entry.Entity.State == StockCountState.Completed
                && (entry.Entity.Lines.Any(row => row.Quantity is null)
                    || entry.Entity.Lines.Where(row => row.Quantity != row.BookQuantity).Any(line =>
                        adjustments.Count(row => row.SourceDocumentId == entry.Entity.Id && row.SourceLineId == line.Id) != 1)
                    || adjustments.Count(row => row.SourceDocumentId == entry.Entity.Id)
                        != entry.Entity.Lines.Count(row => row.Quantity != row.BookQuantity)))
                throw new InvalidOperationException("Count completion must atomically explain every nonzero discrepancy exactly once.");
        }
    }
}
