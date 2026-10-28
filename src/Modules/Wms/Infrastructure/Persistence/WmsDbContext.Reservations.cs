using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    /// <summary>Permanent disjoint execution portions, including fully released or consumed identities.</summary>
    public DbSet<StockReservation> Reservations => Set<StockReservation>();
    /// <summary>Immutable identities of reservation changes; not inventory movement entries.</summary>
    public DbSet<ReservationChange> ReservationChanges => Set<ReservationChange>();

    private void ConfigureReservations(ModelBuilder modelBuilder)
    {
        var reservation = modelBuilder.Entity<StockReservation>();
        reservation.ToTable("wms_stock_reservations");
        reservation.HasKey(item => new { item.TenantId, item.ExecutionUnitId });
        reservation.HasQueryFilter(item => item.TenantId == TenantId);
        reservation.Property(item => item.OriginalQuantity).HasPrecision(28, 6);
        reservation.Property(item => item.RemainingQuantity).HasPrecision(28, 6);
        reservation.HasOne<StockBalance>().WithMany().HasForeignKey(item => new { item.TenantId, item.BalanceId })
            .OnDelete(DeleteBehavior.Restrict);

        var change = modelBuilder.Entity<ReservationChange>();
        change.ToTable("wms_reservation_changes");
        change.HasKey(item => new { item.TenantId, item.Id });
        change.HasQueryFilter(item => item.TenantId == TenantId);
        change.HasIndex(item => new { item.TenantId, item.OperationId }).IsUnique();
        change.Property(item => item.Quantity).HasPrecision(28, 6);
        change.Property(item => item.Before).HasPrecision(28, 6);
        change.Property(item => item.After).HasPrecision(28, 6);
        change.HasOne<StockReservation>().WithMany().HasForeignKey(item => new { item.TenantId, item.ExecutionUnitId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateReservationEffects()
    {
        var reservations = ChangeTracker.Entries<StockReservation>().Where(item =>
            item.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        var changes = ChangeTracker.Entries<ReservationChange>().Where(item => item.State == EntityState.Added)
            .Select(item => item.Entity).ToArray();
        if (reservations.Length == 0 && changes.Length == 0) return;
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("Reservations require an explicit WMS owner transaction.");
        foreach (var reservation in reservations)
        {
            if (reservation.State == EntityState.Deleted)
                throw new InvalidOperationException("Reservation execution identities must be retained.");
            var value = reservation.Entity;
            if (reservation.State == EntityState.Modified && reservation.Properties.Any(item => item.IsModified
                && item.Metadata.Name is not (nameof(StockReservation.Kind) or nameof(StockReservation.RemainingQuantity))))
                throw new InvalidOperationException("Reservation business references and initial quantity are immutable.");
            var balance = ChangeTracker.Entries<StockBalance>().SingleOrDefault(item => item.State == EntityState.Modified
                && item.Entity.Id == value.BalanceId && item.Entity.TenantId == value.TenantId);
            if (balance is null || Equals(balance.Property(item => item.Version).OriginalValue, balance.Entity.Version))
                throw new InvalidOperationException("Reservation mutations must claim the stock balance version.");
            var before = reservation.State == EntityState.Added ? 0m : reservation.Property(item => item.RemainingQuantity).OriginalValue;
            var beforeKind = reservation.State == EntityState.Added ? 0 : reservation.Property(item => item.Kind).OriginalValue;
            var audit = changes.Where(item => item.ExecutionUnitId == value.ExecutionUnitId && item.TenantId == value.TenantId).ToArray();
            if (audit.Length == 1)
            {
                var change = audit[0];
                var validAction = change.Action switch
                {
                    ReservationAction.Reserve => reservation.State == EntityState.Added && change.Quantity > 0m
                        && value.OriginalQuantity == change.Quantity && value.RemainingQuantity == change.Quantity
                        && (value.Kind == StockProtectionKind.Reserved || ExactReceiptProtection(value)),
                    ReservationAction.ReleaseUnstarted => reservation.State == EntityState.Modified
                        && beforeKind == StockProtectionKind.Reserved && value.Kind == beforeKind
                        && change.Quantity > 0m && value.RemainingQuantity == before - change.Quantity,
                    ReservationAction.MarkInTransit => reservation.State == EntityState.Modified && change.Quantity == 0m
                        && beforeKind == StockProtectionKind.Reserved && value.Kind == (beforeKind | StockProtectionKind.InTransit)
                        && value.RemainingQuantity == before && before > 0m,
                    ReservationAction.MarkUnknown => reservation.State == EntityState.Modified && change.Quantity == 0m
                        && (beforeKind & StockProtectionKind.Unknown) == 0 && value.Kind == (beforeKind | StockProtectionKind.Unknown)
                        && value.RemainingQuantity == before && before > 0m,
                    _ => false
                };
                if (!validAction || change.Before != before || change.After != value.RemainingQuantity
                    || change.BeforeKind != beforeKind || change.AfterKind != value.Kind || change.BalanceId != value.BalanceId
                    || change.SourceDocumentId != value.SourceDocumentId || change.SourceLineId != value.SourceLineId
                    || (ExactReceiptProtection(value) && (change.ActorId != value.ActorId
                        || !ChangeTracker.Entries<InboundFact>().Any(fact => fact.State == EntityState.Added
                            && fact.Entity.OperationId == change.OperationId && fact.Entity.CorrelationId == change.CorrelationId
                            && fact.Entity.ExecutionId == value.ExecutionUnitId)))
                    || value.RemainingQuantity < 0m || value.RemainingQuantity > value.OriginalQuantity
                    || !QuantityPrecision.Create(balance.Entity.Precision).CanRepresent(value.RemainingQuantity))
                    throw new InvalidOperationException("Reservation change must match its exact persisted transition.");
            }
            else
            {
                var posting = ChangeTracker.Entries<StockPosting>().SingleOrDefault(item => item.State == EntityState.Added
                    && item.Entity.TenantId == value.TenantId && item.Entity.SourceBalanceId == value.BalanceId
                    && item.Entity.ExecutionUnitId == value.ExecutionUnitId)?.Entity;
                if (audit.Length != 0 || reservation.State != EntityState.Modified || posting is null
                    || posting.SourceDocumentId != value.SourceDocumentId || posting.SourceLineId != value.SourceLineId
                    || (beforeKind & StockProtectionKind.Unknown) != 0 || value.Kind != beforeKind
                    || value.RemainingQuantity != before - posting.Quantity || value.RemainingQuantity < 0m)
                    throw new InvalidOperationException("Reservation consumption requires its matching exact ledger posting.");
                if (InboundTransportIntents.AsNoTracking().Any(intent => intent.ExecutionId == value.ExecutionUnitId)
                    && (posting.Kind != StockPostingKind.Transfer || value.RemainingQuantity != 0m
                        || !ChangeTracker.Entries<InboundTransportInbox>().Any(inbox => inbox.State == EntityState.Modified
                            && inbox.Entity.Applied && inbox.Entity.ExecutionId == value.ExecutionUnitId
                            && inbox.Entity.PostingId == posting.Id)))
                    throw new InvalidOperationException("Receipt protection can only be consumed by its exact applied final transfer.");
            }
        }
        if (changes.Any(change => !reservations.Any(item => item.Entity.TenantId == change.TenantId
            && item.Entity.ExecutionUnitId == change.ExecutionUnitId)))
            throw new InvalidOperationException("A reservation command identity cannot be committed without its effect.");
    }
}
