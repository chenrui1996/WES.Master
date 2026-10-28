using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<InboundDocument> InboundDocuments => Set<InboundDocument>();
    internal DbSet<InboundFact> InboundFacts => Set<InboundFact>();

    private void ConfigureInbound(ModelBuilder builder)
    {
        ConfigureInboundExecution(builder);
        var document = builder.Entity<InboundDocument>();
        document.ToTable("wms_inbound_documents");
        document.HasKey(item => new { item.TenantId, item.Id });
        document.HasQueryFilter(item => item.TenantId == TenantId);
        document.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
        document.HasIndex(item => new { item.TenantId, item.CreationOperationId }).IsUnique();
        document.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        document.Property(item => item.Plan).HasColumnType("longtext").IsRequired();
        document.Property(item => item.State).HasConversion<string>().HasMaxLength(16);
        document.Property(item => item.Version).HasMaxLength(36).IsConcurrencyToken();
        document.HasOne<Warehouse>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.WarehouseId })
            .OnDelete(DeleteBehavior.Restrict);

        var fact = builder.Entity<InboundFact>();
        fact.ToTable("wms_inbound_facts");
        fact.HasKey(item => new { item.TenantId, item.OperationId });
        fact.HasQueryFilter(item => item.TenantId == TenantId);
        fact.HasIndex(item => new { item.TenantId, item.OrderId, item.Sequence }).IsUnique();
        fact.HasIndex(item => new { item.TenantId, item.ExecutionId }).IsUnique();
        fact.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
        fact.Property(item => item.Payload).HasColumnType("longtext").IsRequired();
        fact.Property(item => item.AfterVersion).HasMaxLength(36);
        fact.HasOne<InboundDocument>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.OrderId })
            .OnDelete(DeleteBehavior.Restrict);
        fact.HasOne<StockPosting>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.PostingId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateInboundWrites()
    {
        ValidateInboundExecutionWrites();
        var facts = ChangeTracker.Entries<InboundFact>().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity).ToArray();
        var documents = ChangeTracker.Entries<InboundDocument>().Where(entry => entry.State == EntityState.Modified).ToArray();
        if (ChangeTracker.Entries<InboundFact>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<InboundDocument>().Any(entry => entry.State == EntityState.Deleted))
            throw new InvalidOperationException("Inbound source facts and identities must be retained.");
        foreach (var entry in documents)
        {
            if (entry.Properties.Any(property => property.IsModified && property.Metadata.Name
                is not (nameof(InboundDocument.State) or nameof(InboundDocument.Sequence) or nameof(InboundDocument.Version)))
                || entry.Entity.Sequence != entry.Property(item => item.Sequence).OriginalValue + 1
                || entry.Entity.Version == entry.Property(item => item.Version).OriginalValue
                || facts.Count(fact => fact.OrderId == entry.Entity.Id && fact.Sequence == entry.Entity.Sequence
                    && fact.AfterVersion == entry.Entity.Version) != 1)
                throw new InvalidOperationException("Inbound progress requires exactly one new matching versioned source fact.");
        }
        foreach (var fact in facts)
        {
            var document = documents.SingleOrDefault(entry => entry.Entity.Id == fact.OrderId && entry.Entity.Version == fact.AfterVersion);
            if (document is null)
                throw new InvalidOperationException("An inbound fact cannot be detached from its order transition.");
            if (fact.Kind == InboundFactKind.Approved)
            {
                if (document.Property(item => item.State).OriginalValue != InboundOrderState.Draft
                    || document.Entity.State != InboundOrderState.Approved
                    || fact.Payload != "" || fact.ExecutionId is not null || fact.PostingId is not null)
                    throw new InvalidOperationException("An approval must only approve a draft without receipt effects.");
                continue;
            }
            if (fact.Kind == InboundFactKind.Revised)
            {
                var revision = fact.Revision();
                if (document.Property(item => item.State).OriginalValue != InboundOrderState.Draft
                    || document.Entity.State != InboundOrderState.Draft
                    || revision.ExpectedVersion != document.Property(item => item.Version).OriginalValue
                    || ChangeTracker.Entries<StockPosting>().Any(entry => entry.State == EntityState.Added))
                    throw new InvalidOperationException("A plan revision requires its original draft version and no inventory posting.");
                _ = new InboundOrder(document.Entity.TenantId, document.Entity.WarehouseId, document.Entity.Code, revision.Lines);
                continue;
            }
            if (fact.Kind != InboundFactKind.Received
                || document.Property(item => item.State).OriginalValue is not (InboundOrderState.Approved or InboundOrderState.Executing)
                || document.Entity.State is not (InboundOrderState.Executing or InboundOrderState.Completed))
                throw new InvalidOperationException("An inbound receipt requires an approved or executing order.");
            var command = fact.Receipt();
            if (!ChangeTracker.Entries<StockPosting>().Any(entry => entry.State == EntityState.Added
                && entry.Entity.Id == fact.PostingId && entry.Entity.Kind == StockPostingKind.Receipt
                && entry.Entity.SourceDocumentId == fact.OrderId && entry.Entity.SourceLineId == command.LineId
                && entry.Entity.ActorId == fact.ActorId && entry.Entity.OperationId == fact.OperationId
                && entry.Entity.ExecutionUnitId == command.ExecutionId && entry.Entity.Quantity == command.Quantity))
                throw new InvalidOperationException("Inbound receipts require their exact new ledger posting in the same commit.");
        }
    }
}
