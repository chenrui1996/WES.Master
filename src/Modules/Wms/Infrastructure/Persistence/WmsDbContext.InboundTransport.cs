using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<InboundTransportIntent> InboundTransportIntents => Set<InboundTransportIntent>();
    internal DbSet<InboundTransportInbox> InboundTransportInbox => Set<InboundTransportInbox>();

    private void ConfigureInboundTransport(ModelBuilder builder)
    {
        var intent = builder.Entity<InboundTransportIntent>();
        intent.ToTable("wms_inbound_transport_intents");
        intent.HasKey(row => new { row.TenantId, row.Id });
        intent.HasQueryFilter(row => row.TenantId == TenantId);
        intent.HasIndex(row => new { row.TenantId, row.ExecutionId }).IsUnique();
        intent.HasIndex(row => new { row.TenantId, row.EventId }).IsUnique();
        intent.HasIndex(row => new { row.TenantId, row.TransportId }).IsUnique();
        intent.Property(row => row.Envelope).HasColumnType("longtext").IsRequired();
        intent.Property(row => row.Version).HasMaxLength(36).IsConcurrencyToken();
        intent.HasOne<InboundFact>().WithMany().HasForeignKey(row => new { row.TenantId, OperationId = row.ReceiptOperationId })
            .OnDelete(DeleteBehavior.Restrict);
        intent.HasOne<InboundDocument>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.OrderId })
            .OnDelete(DeleteBehavior.Restrict);
        intent.HasOne<StockBalance>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.SourceBalanceId })
            .OnDelete(DeleteBehavior.Restrict);
        var inbox = builder.Entity<InboundTransportInbox>();
        inbox.ToTable("wms_inbound_transport_inbox");
        inbox.HasKey(row => new { row.TenantId, row.EventId });
        inbox.HasQueryFilter(row => row.TenantId == TenantId);
        inbox.HasIndex(row => new { row.TenantId, row.IntentId }).IsUnique();
        inbox.HasIndex(row => new { row.TenantId, row.CompletionId }).IsUnique();
        inbox.HasIndex(row => new { row.TenantId, row.ExecutionId }).IsUnique();
        inbox.Property(row => row.Body).HasColumnType("longtext").IsRequired();
        inbox.Property(row => row.Version).HasMaxLength(36).IsConcurrencyToken();
        inbox.HasOne<InboundTransportIntent>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.IntentId })
            .OnDelete(DeleteBehavior.Restrict);
        inbox.HasOne<StockPosting>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.PostingId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private bool ExactReceiptProtection(StockReservation reservation)
    {
        var intent = ChangeTracker.Entries<InboundTransportIntent>().SingleOrDefault(entry => entry.State == EntityState.Added
            && entry.Entity.TenantId == reservation.TenantId && entry.Entity.ExecutionId == reservation.ExecutionUnitId)?.Entity;
        var fact = ChangeTracker.Entries<InboundFact>().SingleOrDefault(entry => entry.State == EntityState.Added
            && entry.Entity.Kind == InboundFactKind.Received && entry.Entity.ExecutionId == reservation.ExecutionUnitId)?.Entity;
        if (intent is null || fact is null) return false;
        var receipt = fact.Receipt();
        var posting = ChangeTracker.Entries<StockPosting>().SingleOrDefault(entry => entry.State == EntityState.Added
            && entry.Entity.Id == fact.PostingId)?.Entity;
        return receipt.ReceiptLocationId != receipt.TargetLocationId && intent.ReceiptOperationId == fact.OperationId
            && intent.OrderId == fact.OrderId && intent.SourceBalanceId == reservation.BalanceId
            && reservation.SourceDocumentId == fact.OrderId && reservation.SourceLineId == receipt.LineId
            && reservation.ActorId == fact.ActorId && reservation.CreatedAt == fact.OccurredAt
            && reservation.OriginalQuantity == receipt.Quantity && reservation.RemainingQuantity == receipt.Quantity
            && reservation.Kind == (StockProtectionKind.Reserved | StockProtectionKind.InTransit)
            && posting is not null && posting.Kind == StockPostingKind.Receipt
            && posting.TargetBalanceId == reservation.BalanceId && posting.ExecutionUnitId == reservation.ExecutionUnitId
            && posting.Quantity == reservation.OriginalQuantity;
    }

    private void ValidateInboundTransportWrites()
    {
        foreach (var operation in ChangeTracker.Entries<InboundExecutionOperation>()
            .Where(row => row.State == EntityState.Added && row.Entity.Kind == InboundFactKind.Received))
        {
            var fact = ChangeTracker.Entries<InboundFact>().Single(row => row.State == EntityState.Added
                && row.Entity.OperationId == operation.Entity.OperationId).Entity;
            var receipt = fact.Receipt();
            if (receipt.ReceiptLocationId != receipt.TargetLocationId
                && ChangeTracker.Entries<InboundTransportIntent>().Count(row => row.State == EntityState.Added
                    && row.Entity.ReceiptOperationId == fact.OperationId && row.Entity.ExecutionId == receipt.ExecutionId) != 1)
                throw new InvalidOperationException("A public different-location receipt requires one atomic durable transport intent.");
        }
        foreach (var entry in ChangeTracker.Entries<InboundTransportIntent>().Where(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var value = entry.Entity;
            if (Database.CurrentTransaction is null || entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Transport identities require their owner transaction and must be retained.");
            if (entry.State == EntityState.Added)
            {
                var reservation = ChangeTracker.Entries<StockReservation>().SingleOrDefault(row => row.State == EntityState.Added
                    && row.Entity.ExecutionUnitId == value.ExecutionId)?.Entity;
                var fact = ChangeTracker.Entries<InboundFact>().SingleOrDefault(row => row.State == EntityState.Added
                    && row.Entity.OperationId == value.ReceiptOperationId)?.Entity;
                if (reservation is null || fact is null || !ExactReceiptProtection(reservation) || value.TransportId is not null
                    || value.AssociationOperationId is not null)
                    throw new InvalidOperationException("Transport intent requires its exact atomic receipt protection.");
                var request = value.Request();
                var receipt = fact.Receipt();
                var header = ChangeTracker.Entries<InboundDocument>().Single(row => row.Entity.Id == fact.OrderId).Entity;
                var balance = ChangeTracker.Entries<StockBalance>().Single(row => row.Entity.Id == value.SourceBalanceId).Entity;
                if (request.EventId != value.EventId || request.IntentId != value.Id || request.TenantId != TenantId
                    || value.EventId == Guid.Empty || value.Id == Guid.Empty
                    || request.DocumentId != fact.OrderId || request.LineId != receipt.LineId || request.ExecutionId != receipt.ExecutionId
                    || request.WarehouseId != header.WarehouseId || request.ContainerId != receipt.ContainerId
                    || request.ItemId != receipt.ItemId || request.Batch != receipt.Batch || request.UnitId != receipt.UnitId
                    || request.SourceLocationId != receipt.ReceiptLocationId || request.TargetLocationId != receipt.TargetLocationId
                    || request.Quantity != receipt.Quantity.ToString(CultureInfo.InvariantCulture) || request.Precision != balance.Precision
                    || request.ActorId != fact.ActorId || request.CorrelationId != fact.CorrelationId
                    || request.CausationId != fact.OperationId || request.OccurredAt != fact.OccurredAt
                    || request.OccurredAt.Offset != TimeSpan.Zero
                    || request.SchemaVersion != 1 || request.PolicyVersion != "WES-RULES-1.0")
                    throw new InvalidOperationException("Transport envelope must preserve the complete original receipt.");
                if (balance.WarehouseId != request.WarehouseId || balance.LocationId != request.SourceLocationId
                    || balance.ContainerId != request.ContainerId || balance.ItemId != request.ItemId
                    || balance.Batch != request.Batch || balance.UnitId != request.UnitId)
                    throw new InvalidOperationException("Receipt protection requires its exact source stock dimension.");
            }
            else if (entry.Properties.Any(property => property.IsModified && property.Metadata.Name
                    is not (nameof(InboundTransportIntent.TransportId) or nameof(InboundTransportIntent.AssociationOperationId) or nameof(InboundTransportIntent.Version)))
                || entry.Property(row => row.TransportId).OriginalValue is not null || value.TransportId is null
                || value.Version == entry.Property(row => row.Version).OriginalValue
                || !ChangeTracker.Entries<InboundFact>().Any(row => row.State == EntityState.Added
                    && row.Entity.Kind == InboundFactKind.TransportLinked && row.Entity.OperationId == value.AssociationOperationId
                    && row.Entity.Transport().IntentId == value.Id && row.Entity.Transport().TransportId == value.TransportId))
                throw new InvalidOperationException("Transport association requires its one immutable business transition.");
        }
        foreach (var entry in ChangeTracker.Entries<InboundTransportInbox>().Where(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var value = entry.Entity;
            if (Database.CurrentTransaction is null || entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Completion evidence must be retained in its owner transaction.");
            if (entry.State == EntityState.Added)
            {
                var intent = InboundTransportIntents.AsNoTracking().Single(row => row.Id == value.IntentId);
                var completion = value.Completion();
                var request = intent.Request();
                if (value.Applied || value.PostingId is not null || value.ExecutionId != intent.ExecutionId
                    || completion.EventId != value.EventId || completion.CompletionId != value.CompletionId
                    || completion.TenantId != TenantId || completion.IntentId != intent.Id || completion.ExecutionId != intent.ExecutionId
                    || completion.TransportId != intent.TransportId || completion.ContainerId != request.ContainerId
                    || completion.SourceLocationId != request.SourceLocationId || completion.FinalLocationId != request.TargetLocationId
                    || completion.SchemaVersion != 1 || completion.Producer != "Orchestration"
                    || completion.EvidenceRefs.Count == 0 || completion.CompletedAt.Offset != TimeSpan.Zero)
                    throw new InvalidOperationException("Completion evidence must match the complete linked intent.");
            }
            else if (entry.Properties.Any(property => property.IsModified && property.Metadata.Name
                    is not (nameof(Domain.InboundTransportInbox.Applied) or nameof(Domain.InboundTransportInbox.PostingId) or nameof(Domain.InboundTransportInbox.Version)))
                || entry.Property(row => row.Applied).OriginalValue || !value.Applied || value.PostingId is null
                || value.Version == entry.Property(row => row.Version).OriginalValue
                || !ChangeTracker.Entries<InboundFact>().Any(row => row.State == EntityState.Added
                    && row.Entity.Kind == InboundFactKind.Arrived && row.Entity.PostingId == value.PostingId
                    && row.Entity.Transport().CompletionEventId == value.EventId && row.Entity.Transport().IntentId == value.IntentId))
                throw new InvalidOperationException("Applied completion requires its exact atomic arrival.");
        }
    }

    private void ValidateTransportFact(InboundFact fact, string expectedVersion)
    {
        var transition = fact.Transport();
        var intent = ChangeTracker.Entries<InboundTransportIntent>().SingleOrDefault(row => row.Entity.Id == transition.IntentId)?.Entity
            ?? InboundTransportIntents.AsNoTracking().Single(row => row.Id == transition.IntentId);
        var request = intent.Request();
        if (Database.CurrentTransaction is null || transition.ExpectedVersion != expectedVersion
            || fact.OrderId != intent.OrderId || transition.ExecutionId != intent.ExecutionId
            || transition.TransportId != intent.TransportId || transition.ContainerId != request.ContainerId
            || transition.SourceLocationId != request.SourceLocationId || transition.TargetLocationId != request.TargetLocationId
            || fact.CorrelationId != request.CorrelationId || fact.ActorId == request.ActorId || fact.OccurredAt.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Transport history must preserve its parent version and full original tuple.");
        if (fact.Kind == InboundFactKind.TransportLinked)
        {
            if (intent.AssociationOperationId != fact.OperationId || !ChangeTracker.Entries<InboundTransportIntent>().Any(row =>
                row.State == EntityState.Modified && row.Entity.Id == intent.Id))
                throw new InvalidOperationException("A linked fact must commit its original association.");
        }
        else
        {
            var posting = ChangeTracker.Entries<StockPosting>().SingleOrDefault(row =>
                row.State == EntityState.Added && row.Entity.Id == fact.PostingId)?.Entity;
            var inbox = ChangeTracker.Entries<InboundTransportInbox>().SingleOrDefault(row =>
                row.State == EntityState.Modified && row.Entity.EventId == transition.CompletionEventId)?.Entity;
            var target = posting is null ? null : ChangeTracker.Entries<StockBalance>()
                .SingleOrDefault(row => row.Entity.Id == posting.TargetBalanceId)?.Entity;
            if (posting is null || inbox is null || !inbox.Applied || inbox.PostingId != posting.Id
                || inbox.IntentId != intent.Id || inbox.ExecutionId != intent.ExecutionId || posting.Kind != StockPostingKind.Transfer
                || posting.OperationId != inbox.EventId || fact.OperationId != inbox.EventId
                || posting.ActorId != fact.ActorId || posting.CorrelationId != fact.CorrelationId
                || posting.SourceDocumentId != fact.OrderId || posting.SourceLineId != request.LineId
                || posting.ExecutionUnitId != intent.ExecutionId || posting.SourceBalanceId != intent.SourceBalanceId
                || posting.Quantity != decimal.Parse(request.Quantity, CultureInfo.InvariantCulture)
                || target is null || target.WarehouseId != request.WarehouseId || target.LocationId != request.TargetLocationId
                || target.ContainerId != request.ContainerId || target.ItemId != request.ItemId || target.Batch != request.Batch
                || target.UnitId != request.UnitId || target.Precision != request.Precision
                || !DimensionRegistrations.AsNoTracking().Any(row => row.BalanceId == target.Id))
                throw new InvalidOperationException("An arrival requires its exact registered final transfer and applied Inbox.");
        }
        var header = ChangeTracker.Entries<InboundDocument>().Single(row => row.Entity.Id == fact.OrderId).Entity;
        var history = InboundFacts.AsNoTracking().Where(row => row.OrderId == fact.OrderId).OrderBy(row => row.Sequence).ToList();
        history.Add(fact);
        _ = header.Restore(history);
    }
}
