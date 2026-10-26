using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;

namespace Wes.Platform.Infrastructure.Persistence;

public sealed partial class PlatformDbContext
{
    /// <summary>Immutable metadata-only export identities, never reusable as cached authorization.</summary>
    public DbSet<PlatformAuditExportReceipt> AuditExportReceipts => Set<PlatformAuditExportReceipt>();

    private void ConfigureAuditExports(ModelBuilder modelBuilder)
    {
        var receipt = modelBuilder.Entity<PlatformAuditExportReceipt>();
        receipt.ToTable("platform_audit_export_receipts");
        receipt.HasKey(item => new { item.TenantId, item.OperationId });
        foreach (var property in new[] { "TenantId", "OperationId", "ActorId", "SessionId", "SnapshotId", "AuditId" })
            receipt.Property<Guid>(property).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        receipt.Property(item => item.Columns).HasColumnType("varchar(128)").HasMaxLength(128).IsRequired();
        receipt.Property(item => item.CreatedAt).HasConversion(
            value => value.UtcDateTime, value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
            .HasColumnType("datetime(6)");
        receipt.HasOne<PlatformAudit>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.AuditId })
            .OnDelete(DeleteBehavior.Restrict);
        receipt.HasQueryFilter(item => item.TenantId == TenantId);
    }

    private void ValidateAuditExportWrites()
    {
        foreach (var receipt in ChangeTracker.Entries<PlatformAuditExportReceipt>().Where(item => item.State == EntityState.Added))
        {
            var identity = receipt.Entity;
            if (!ChangeTracker.Entries<PlatformAudit>().Any(item => item.State == EntityState.Added
                && item.Entity.TenantId == identity.TenantId && item.Entity.Id == identity.AuditId
                && item.Entity.ActorId == identity.ActorId && item.Entity.TargetId == identity.SnapshotId
                && item.Entity.Action == PlatformAuditAction.AuditExported
                && item.Entity.AfterVersion == identity.OperationId.ToString("D")))
                throw new InvalidOperationException("Each export identity requires its own matching new delivery audit in the same owner commit.");
        }
    }
}
