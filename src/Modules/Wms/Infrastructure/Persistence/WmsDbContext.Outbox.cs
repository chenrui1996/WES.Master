using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    /// <summary>WMS-owned pending events, never another owner's transaction store.</summary>
    public DbSet<WmsOutboxMessage> OutboxMessages => Set<WmsOutboxMessage>();

    private void ConfigureOutbox(ModelBuilder builder)
    {
        var outbox = builder.Entity<WmsOutboxMessage>();
        outbox.ToTable("wms_outbox_messages");
        outbox.HasKey(message => new { message.TenantId, message.EventId });
        outbox.HasQueryFilter(message => message.TenantId == TenantId);
        outbox.Property(message => message.ContractName).HasColumnType("varchar(128)").HasMaxLength(128);
        outbox.Property(message => message.Owner).HasColumnType("varchar(32)").HasMaxLength(32);
        outbox.Property(message => message.AggregateVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        outbox.Property(message => message.Payload).HasColumnType("longtext").IsRequired();
        outbox.Property(message => message.Status).HasConversion<string>().HasColumnType("varchar(16)").HasMaxLength(16);
        outbox.HasIndex(message => new { message.TenantId, message.Status, message.OccurredAt, message.EventId });
        outbox.HasOne<MasterDataChange>().WithOne().HasForeignKey<WmsOutboxMessage>(
            message => new { message.TenantId, OperationId = message.EventId }).OnDelete(DeleteBehavior.Restrict);
    }

    private void StageMasterDataOutbox()
    {
        var changes = ChangeTracker.Entries<MasterDataChange>()
            .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToArray();
        foreach (var entry in ChangeTracker.Entries<WmsOutboxMessage>().Where(entry =>
            entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("WMS staged event identity and payload are immutable.");

        var added = ChangeTracker.Entries<WmsOutboxMessage>()
            .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToList();
        foreach (var change in changes)
        {
            var matches = added.Where(message => message.TenantId == change.TenantId && message.EventId == change.OperationId).ToArray();
            if (matches.Length == 0)
            {
                var message = new WmsOutboxMessage(change);
                OutboxMessages.Add(message);
                added.Add(message);
            }
            else if (matches.Length != 1 || !matches[0].Matches(change))
                throw new InvalidOperationException("A staged event must exactly match its new WMS source fact.");
        }
        foreach (var message in added)
            if (changes.Count(message.Matches) != 1)
                throw new InvalidOperationException("WMS outbox events require exactly one matching new source fact.");
    }
}
