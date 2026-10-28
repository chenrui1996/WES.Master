using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<InboundExecutionOperation> InboundExecutionOperations => Set<InboundExecutionOperation>();

    private void ConfigureInboundExecution(ModelBuilder builder)
    {
        var operation = builder.Entity<InboundExecutionOperation>();
        operation.ToTable("wms_inbound_execution_operations");
        operation.HasKey(row => new { row.TenantId, row.OperationId });
        operation.HasQueryFilter(row => row.TenantId == TenantId);
        operation.HasIndex(row => new { row.TenantId, row.Kind, row.ResultId }).IsUnique();
        operation.Property(row => row.Kind).HasConversion<string>().HasMaxLength(16);
        operation.Property(row => row.Input).HasMaxLength(4096);
        operation.Property(row => row.AfterVersion).HasMaxLength(36);
        operation.HasOne<InboundFact>().WithMany().HasForeignKey(row => new { row.TenantId, row.OperationId }).OnDelete(DeleteBehavior.Restrict);
        operation.HasOne<InboundDocument>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.OrderId }).OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateInboundExecutionWrites()
    {
        var operations = ChangeTracker.Entries<InboundExecutionOperation>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (operations.Any(entry => entry.State != EntityState.Added))
            throw new InvalidOperationException("Public execution request identities must be retained unchanged.");
        foreach (var entry in operations)
        {
            var operation = entry.Entity;
            if (Database.CurrentTransaction is null || ChangeTracker.Entries<InboundFact>().Count(fact => fact.State == EntityState.Added
                && fact.Entity.TenantId == operation.TenantId && fact.Entity.OperationId == operation.OperationId
                && fact.Entity.OrderId == operation.OrderId && fact.Entity.ActorId == operation.ActorId
                && fact.Entity.CorrelationId == operation.CorrelationId && fact.Entity.Kind == operation.Kind
                && fact.Entity.AfterVersion == operation.AfterVersion
                && operation.ResultId == (operation.Kind == InboundFactKind.Approved ? fact.Entity.OrderId : fact.Entity.ExecutionId)) != 1)
                throw new InvalidOperationException("An execution operation requires one exact newly staged inbound fact in its owner transaction.");
        }
    }
}
