using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

/// <summary>WMS-owned unit of work; no Platform or execution-module tables are referenced.</summary>
public sealed partial class WmsDbContext : DbContext
{
    /// <summary>Requires one explicit nonempty trusted tenant for every runtime and design-time scope.</summary>
    public WmsDbContext(DbContextOptions<WmsDbContext> options, Guid tenant) : base(options)
    {
        if (tenant == Guid.Empty) throw new ArgumentException("WMS persistence requires a trusted tenant.", nameof(tenant));
        TenantId = tenant;
    }

    /// <summary>Owner scope, never inferred from a request's stock identifier.</summary>
    public Guid TenantId { get; }
    /// <summary>Book balances; quantity changes require immutable ledger entries, while reservation changes claim the same version fence.</summary>
    public DbSet<StockBalance> Balances => Set<StockBalance>();
    /// <summary>Permanent business posting identities.</summary>
    public DbSet<StockPosting> Postings => Set<StockPosting>();
    /// <summary>Append-only signed inventory facts.</summary>
    public DbSet<StockEntry> Entries => Set<StockEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureReservations(modelBuilder);
        ConfigureMasterData(modelBuilder);
        ConfigureOutbox(modelBuilder);
        ConfigureInbound(modelBuilder);
        ConfigureInboundTransport(modelBuilder);
        ConfigureStockResources(modelBuilder);
        ConfigureOutbound(modelBuilder);
        ConfigureCounts(modelBuilder);
        ConfigureTransfers(modelBuilder);
        var balance = modelBuilder.Entity<StockBalance>();
        balance.ToTable("wms_stock_balances");
        balance.HasKey(item => new { item.TenantId, item.Id });
        balance.HasQueryFilter(item => item.TenantId == TenantId);
        balance.Property(item => item.Batch).HasColumnType("varchar(128)").HasMaxLength(128).IsRequired()
            .UseCollation("utf8mb4_bin");
        balance.Property(item => item.Quantity).HasPrecision(28, 6);
        balance.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        balance.HasIndex(item => new
        {
            item.TenantId,
            item.WarehouseId,
            item.LocationId,
            item.ItemId,
            item.Batch,
            item.ContainerId,
            item.UnitId
        }).IsUnique();

        var posting = modelBuilder.Entity<StockPosting>();
        posting.ToTable("wms_stock_postings");
        posting.HasKey(item => new { item.TenantId, item.Id });
        posting.HasQueryFilter(item => item.TenantId == TenantId);
        posting.Property(item => item.Quantity).HasPrecision(28, 6);
        posting.HasIndex(item => new { item.TenantId, item.OperationId }).IsUnique();
        posting.HasIndex(item => new { item.TenantId, item.Kind, item.ExecutionUnitId }).IsUnique();
        posting.HasOne<StockBalance>().WithMany().HasForeignKey(item => new { item.TenantId, item.SourceBalanceId })
            .OnDelete(DeleteBehavior.Restrict);
        posting.HasOne<StockBalance>().WithMany().HasForeignKey(item => new { item.TenantId, item.TargetBalanceId })
            .OnDelete(DeleteBehavior.Restrict);

        var entry = modelBuilder.Entity<StockEntry>();
        entry.ToTable("wms_stock_entries");
        entry.HasKey(item => new { item.TenantId, item.Id });
        entry.HasQueryFilter(item => item.TenantId == TenantId);
        entry.Property(item => item.Quantity).HasPrecision(28, 6);
        entry.Property(item => item.Before).HasPrecision(28, 6);
        entry.Property(item => item.After).HasPrecision(28, 6);
        entry.HasIndex(item => new { item.TenantId, item.PostingId, item.BalanceId }).IsUnique();
        entry.HasIndex(item => new { item.TenantId, item.OccurredAt, item.Id });
        entry.HasOne<StockPosting>().WithMany().HasForeignKey(item => new { item.TenantId, item.PostingId })
            .OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<StockBalance>().WithMany().HasForeignKey(item => new { item.TenantId, item.BalanceId })
            .OnDelete(DeleteBehavior.Restrict);

        var utc = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?))
                {
                    property.SetValueConverter(property.ClrType == typeof(Guid)
                        ? new ValueConverter<Guid, string>(value => value.ToString("D"), value => Guid.Parse(value))
                        : new ValueConverter<Guid?, string?>(value => value.HasValue ? value.Value.ToString("D") : null,
                            value => value == null ? null : Guid.Parse(value)));
                    property.SetColumnType("varchar(36)");
                    property.SetMaxLength(36);
                }
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(utc);
                    property.SetColumnType("datetime(6)");
                }
            }
        }
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateWrites()
    {
        ValidateLedgerEffects();
        ValidateInboundWrites();
        ValidateInboundTransportWrites();
        ValidateOutboundWrites();
        ValidateCountWrites();
        ValidateTransferWrites();
        ValidateReservationEffects();
        ValidateMasterDataWrites();
        ValidateStockResourceWrites();
        StageMasterDataOutbox();
        foreach (var entry in ChangeTracker.Entries().Where(item =>
            item.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Property(nameof(StockBalance.TenantId)).CurrentValue is not Guid tenant || tenant != TenantId
                || (entry.State != EntityState.Added
                    && !Equals(entry.Property(nameof(StockBalance.TenantId)).OriginalValue, TenantId)))
                throw new InvalidOperationException("WMS writes require a matching trusted tenant.");
            if (entry.Entity is StockPosting or StockEntry or ReservationChange && entry.State != EntityState.Added)
                throw new InvalidOperationException("WMS posting identities, ledger entries and reservation changes are immutable.");
            if (entry.Entity is not StockBalance balance) continue;
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("WMS balance identities must be retained with their ledger.");
            if (entry.State == EntityState.Added)
            {
                if (balance.Quantity != 0m)
                    throw new InvalidOperationException("New stock dimensions must start empty.");
                continue;
            }
            if (entry.Properties.Any(property => property.IsModified
                && property.Metadata.Name is not (nameof(StockBalance.Quantity) or nameof(StockBalance.Version))))
                throw new InvalidOperationException("Stock dimensions and unit precision are immutable.");
            var facts = ChangeTracker.Entries<StockEntry>().Where(item => item.State == EntityState.Added
                && item.Entity.TenantId == TenantId && item.Entity.BalanceId == balance.Id).ToArray();
            if (Equals(entry.Property(nameof(StockBalance.Quantity)).OriginalValue, balance.Quantity)
                && ChangeTracker.Entries<ReservationChange>().Any(item => item.State == EntityState.Added
                    && item.Entity.TenantId == TenantId && item.Entity.BalanceId == balance.Id)
                && !Equals(entry.Property(nameof(StockBalance.Version)).OriginalValue, balance.Version))
                continue;
            if (facts.Length != 1)
                throw new InvalidOperationException("Stock changes require exactly one ledger fact.");
            if (facts[0].Entity.Before != (decimal)entry.Property(nameof(StockBalance.Quantity)).OriginalValue!
                || facts[0].Entity.After != balance.Quantity)
                throw new InvalidOperationException("Stock ledger facts must exactly explain the balance change.");
            if (Equals(entry.Property(nameof(StockBalance.Version)).OriginalValue, balance.Version))
                throw new InvalidOperationException("Stock changes require a new balance version.");
        }
    }

    private void ValidateLedgerEffects()
    {
        var postings = ChangeTracker.Entries<StockPosting>().Where(item => item.State == EntityState.Added)
            .Select(item => item.Entity).ToArray();
        var facts = ChangeTracker.Entries<StockEntry>().Where(item => item.State == EntityState.Added)
            .Select(item => item.Entity).ToArray();
        if (postings.Length == 0 && facts.Length == 0) return;
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("Inventory facts require an explicit WMS owner transaction.");
        foreach (var posting in postings)
        {
            var effects = facts.Where(item => item.PostingId == posting.Id && item.TenantId == posting.TenantId).ToArray();
            var expected = posting.Kind == StockPostingKind.Transfer ? 2 : 1;
            if (!Enum.IsDefined(posting.Kind) || effects.Length != expected
                || (posting.SourceBalanceId.HasValue && effects.Count(item => item.BalanceId == posting.SourceBalanceId
                    && item.Quantity == -posting.Quantity) != 1)
                || (posting.TargetBalanceId.HasValue && effects.Count(item => item.BalanceId == posting.TargetBalanceId
                    && item.Quantity == posting.Quantity) != 1))
                throw new InvalidOperationException("A posting must carry all of its exact signed balance effects.");
        }
        foreach (var fact in facts)
        {
            var posting = postings.SingleOrDefault(item => item.Id == fact.PostingId && item.TenantId == fact.TenantId);
            var balance = ChangeTracker.Entries<StockBalance>().SingleOrDefault(item => item.State == EntityState.Modified
                && item.Entity.Id == fact.BalanceId && item.Entity.TenantId == fact.TenantId);
            if (posting is null || balance is null || fact.Quantity == 0m || fact.Before < 0m || fact.After < 0m
                || fact.Quantity != fact.After - fact.Before || Math.Abs(fact.Quantity) != posting.Quantity
                || (fact.Quantity < 0m ? posting.SourceBalanceId : posting.TargetBalanceId) != fact.BalanceId
                || fact.CorrelationId != posting.CorrelationId || fact.OccurredAt != posting.OccurredAt
                || fact.Before != (decimal)balance.Property(item => item.Quantity).OriginalValue
                || fact.After != balance.Entity.Quantity)
                throw new InvalidOperationException("A ledger fact requires a new posting and an exact current balance transition.");
        }
    }
}
