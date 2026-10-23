using Microsoft.EntityFrameworkCore;

namespace Wes.Persistence.Tests;

internal sealed class ProbeContext(
    DbContextOptions<ProbeContext> options,
    string runId,
    string tenantId) : DbContext(options)
{
    internal DbSet<ProbeRow> Rows => Set<ProbeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<ProbeRow>();
        row.ToTable("wes_foundation_probe");
        row.HasKey(item => new { item.RunId, item.TenantId, item.Id });
        row.Property(item => item.RunId).HasMaxLength(32);
        row.Property(item => item.TenantId).HasMaxLength(32);
        row.Property(item => item.Id).HasMaxLength(32);
        row.Property(item => item.Code).HasMaxLength(64);
        row.Property(item => item.Quantity).HasPrecision(28, 6);
        row.Property(item => item.Version).IsConcurrencyToken();
        row.HasIndex(item => new { item.RunId, item.TenantId, item.Code })
            .IsUnique().HasDatabaseName("UX_probe_run_tenant_code");
        row.HasQueryFilter(item => item.RunId == RunId && item.TenantId == TenantId);
    }

    internal string RunId { get; } = runId;
    internal string TenantId { get; } = tenantId;
}

internal sealed class ProbeRow
{
    public required string RunId { get; init; }
    public required string TenantId { get; init; }
    public required string Id { get; init; }
    public required string Code { get; init; }
    public decimal Quantity { get; set; }
    public long Version { get; set; } = 1;
}
