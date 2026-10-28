using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Outbound;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<OutboundDocument> OutboundDocuments => Set<OutboundDocument>();
    internal DbSet<OutboundFact> OutboundFacts => Set<OutboundFact>();

    private void ConfigureOutbound(ModelBuilder builder)
    {
        var document = builder.Entity<OutboundDocument>();
        document.ToTable("wms_outbound_documents");
        document.HasKey(row => new { row.TenantId, row.Id });
        document.HasQueryFilter(row => row.TenantId == TenantId);
        document.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        document.HasIndex(row => new { row.TenantId, row.CreationOperationId }).IsUnique();
        document.Property(row => row.Code).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        document.Property(row => row.Plan).HasColumnType("longtext").IsRequired();
        document.Property(row => row.State).HasConversion<string>().HasMaxLength(16);
        document.Property(row => row.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        document.HasOne<Warehouse>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.WarehouseId })
            .OnDelete(DeleteBehavior.Restrict);

        var fact = builder.Entity<OutboundFact>();
        fact.ToTable("wms_outbound_facts");
        fact.HasKey(row => new { row.TenantId, row.OperationId });
        fact.HasQueryFilter(row => row.TenantId == TenantId);
        fact.HasIndex(row => new { row.TenantId, row.OrderId, row.Sequence }).IsUnique();
        fact.Property(row => row.Kind).HasConversion<string>().HasMaxLength(24);
        fact.Property(row => row.Input).HasColumnType("longtext").IsRequired();
        fact.Property(row => row.AfterVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        fact.HasOne<OutboundDocument>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.OrderId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateOutboundWrites()
    {
        var facts = ChangeTracker.Entries<OutboundFact>().Where(row => row.State == EntityState.Added)
            .Select(row => row.Entity).ToArray();
        if (ChangeTracker.Entries<OutboundFact>().Any(row => row.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<OutboundDocument>().Any(row => row.State == EntityState.Deleted))
            throw new InvalidOperationException("Outbound source facts and order identities must be retained.");
        var documents = ChangeTracker.Entries<OutboundDocument>()
            .Where(row => row.State is EntityState.Added or EntityState.Modified).ToArray();
        foreach (var entry in documents)
        {
            var document = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                if (document.State != OutboundOrderState.Draft || document.Sequence != 1
                    || facts.Count(fact => fact.OrderId == document.Id && fact.Sequence == 1
                        && fact.OperationId == document.CreationOperationId && fact.Kind == OutboundFactKind.Created
                        && fact.AfterVersion == document.Version) != 1)
                    throw new InvalidOperationException("An outbound draft requires its exact creation fact.");
                continue;
            }
            if (entry.Properties.Any(property => property.IsModified && property.Metadata.Name
                    is not (nameof(OutboundDocument.State) or nameof(OutboundDocument.Sequence) or nameof(OutboundDocument.Version)))
                || document.Sequence != entry.Property(row => row.Sequence).OriginalValue + 1
                || document.Version == entry.Property(row => row.Version).OriginalValue
                || facts.Count(fact => fact.OrderId == document.Id && fact.Sequence == document.Sequence
                    && fact.AfterVersion == document.Version) != 1
                || Database.CurrentTransaction is null)
                throw new InvalidOperationException("Outbound progress requires one new matching fact in its owner transaction.");
            var fact = facts.Single(row => row.OrderId == document.Id && row.Sequence == document.Sequence);
            var previous = entry.Property(row => row.State).OriginalValue;
            var stateValid = fact.Kind switch
            {
                OutboundFactKind.Approved => previous == OutboundOrderState.Draft && document.State == OutboundOrderState.Approved,
                OutboundFactKind.Picked => previous is OutboundOrderState.Approved or OutboundOrderState.Executing
                    && document.State == OutboundOrderState.Executing,
                OutboundFactKind.ShortageReleased => previous is OutboundOrderState.Approved or OutboundOrderState.Executing
                    && document.State == previous,
                OutboundFactKind.Shipped => previous is OutboundOrderState.Approved or OutboundOrderState.Executing
                    && document.State is OutboundOrderState.Executing or OutboundOrderState.Completed,
                OutboundFactKind.Cancelled => previous is OutboundOrderState.Draft or OutboundOrderState.Approved or OutboundOrderState.Executing
                    && document.State == OutboundOrderState.Cancelled,
                OutboundFactKind.ClosedPartial => previous == OutboundOrderState.Executing
                    && document.State == OutboundOrderState.ClosedPartial,
                _ => false
            };
            if (!stateValid || facts.Count(row => row.OperationId == fact.OperationId) != 1)
                throw new InvalidOperationException("An outbound fact must match its exact owner state transition.");
            if (fact.Kind == OutboundFactKind.Approved)
            {
                var lines = document.Lines;
                var reservations = ChangeTracker.Entries<StockReservation>().Where(row => row.State == EntityState.Added)
                    .Select(row => row.Entity).ToArray();
                if (reservations.Length != lines.Count || lines.Any(line => reservations.Count(row =>
                        row.ExecutionUnitId == line.ExecutionId && row.BalanceId == line.BalanceId
                        && row.SourceDocumentId == document.Id && row.SourceLineId == line.Id
                        && row.OriginalQuantity == line.PlannedQuantity) != 1))
                    throw new InvalidOperationException("Outbound approval must reserve every exact planned line in one transaction.");
            }
            if (fact.Kind == OutboundFactKind.Shipped)
            {
                var input = System.Text.Json.JsonSerializer.Deserialize<OutboundShipmentRequest>(fact.Input)
                    ?? throw new InvalidOperationException("Outbound shipment input is missing.");
                var line = document.Lines.SingleOrDefault(row => row.Id == input.LineId);
                if (line is null || line.Branch != OutboundBranch.DirectShipment
                    || !decimal.TryParse(input.Quantity, System.Globalization.NumberStyles.AllowDecimalPoint,
                        System.Globalization.CultureInfo.InvariantCulture, out var quantity)
                    || ChangeTracker.Entries<StockPosting>().Count(row => row.State == EntityState.Added
                        && row.Entity.OperationId == fact.OperationId && row.Entity.Kind == StockPostingKind.Shipment
                        && row.Entity.SourceDocumentId == document.Id && row.Entity.SourceLineId == line.Id
                        && row.Entity.ExecutionUnitId == line.ExecutionId && row.Entity.SourceBalanceId == line.BalanceId
                        && row.Entity.Quantity == quantity && row.Entity.ActorId == fact.ActorId
                        && row.Entity.CorrelationId == fact.CorrelationId) != 1)
                    throw new InvalidOperationException("An outbound shipment fact requires its exact same-transaction WMS ledger posting.");
            }
            else if (ChangeTracker.Entries<StockPosting>().Any(row => row.State == EntityState.Added
                         && row.Entity.SourceDocumentId == document.Id))
                throw new InvalidOperationException("Only a direct shipment may stage outbound inventory movement.");
        }
        foreach (var fact in facts)
            if (!documents.Any(row => row.Entity.Id == fact.OrderId && row.Entity.Version == fact.AfterVersion)
                || fact.Sequence < 1)
                throw new InvalidOperationException("An outbound fact cannot be detached from its order transition.");
    }
}
