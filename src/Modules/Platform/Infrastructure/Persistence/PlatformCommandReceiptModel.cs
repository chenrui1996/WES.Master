using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wes.Platform.Domain;

namespace Wes.Platform.Infrastructure.Persistence;

public sealed partial class PlatformDbContext
{
    /// <summary>Private owner command receipts; never an authorization or directory query source.</summary>
    public DbSet<DirectoryCommandReceipt> CommandReceipts => Set<DirectoryCommandReceipt>();
    /// <summary>Private membership delegation references, removed with their expired receipt.</summary>
    public DbSet<DirectoryDelegationRequirement> CommandDelegations => Set<DirectoryDelegationRequirement>();

    private void ConfigureCommandReceipts(ModelBuilder model)
    {
        var receipt = model.Entity<DirectoryCommandReceipt>();
        receipt.ToTable("platform_directory_command_receipts");
        receipt.HasKey(x => new { x.TenantId, x.ActorId, x.OperationId });
        Uuid(receipt.Property(x => x.TenantId));
        Uuid(receipt.Property(x => x.ActorId));
        Uuid(receipt.Property(x => x.OperationId));
        Uuid(receipt.Property(x => x.ResultId));
        receipt.Property(x => x.Kind).HasConversion<string>().HasColumnType("varchar(32)").HasMaxLength(32);
        Text(receipt.Property(x => x.Fingerprint), 64);
        Text(receipt.Property(x => x.ResultVersion), 36);
        receipt.Property(x => x.CreatedAt).HasConversion(new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))).HasColumnType("datetime(6)");
        receipt.HasIndex(x => new { x.TenantId, x.CreatedAt });
        receipt.HasQueryFilter(x => x.TenantId == TenantId);
        var delegation = model.Entity<DirectoryDelegationRequirement>();
        delegation.ToTable("platform_directory_delegation_requirements");
        delegation.HasKey(x => new { x.TenantId, x.ActorId, x.OperationId });
        Uuid(delegation.Property(x => x.TenantId));
        Uuid(delegation.Property(x => x.ActorId));
        Uuid(delegation.Property(x => x.OperationId));
        delegation.Property<string>("RoleIdsData").HasColumnType("longtext").IsRequired();
        delegation.Ignore(x => x.RoleIds);
        delegation.HasOne<DirectoryCommandReceipt>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ActorId, x.OperationId }).OnDelete(DeleteBehavior.Cascade);
        delegation.HasQueryFilter(x => x.TenantId == TenantId);
    }
}
