using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MySql.EntityFrameworkCore.Extensions;
using Wes.Platform.Domain;

namespace Wes.Platform.Infrastructure.Persistence;

public sealed partial class PlatformDbContext
{
    /// <summary>Tenant-owned file evidence; content remains in private quarantine storage.</summary>
    public DbSet<QuarantinedFile> Files => Set<QuarantinedFile>();
    /// <summary>Unique business bindings, committed with the file reference-count version fence.</summary>
    public DbSet<FileReference> FileReferences => Set<FileReference>();

    private void ConfigureFiles(ModelBuilder modelBuilder)
    {
        var utc = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var file = modelBuilder.Entity<QuarantinedFile>();
        file.ToTable("platform_files");
        file.HasKey(item => new { item.TenantId, item.Id });
        file.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        file.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        file.Property(item => item.CreatorId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        file.Property(item => item.CorrelationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        file.Property(item => item.UploadOperationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        file.HasIndex(item => new { item.TenantId, item.UploadOperationId }).IsUnique();
        file.Property(item => item.ScanAttemptId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        file.Property(item => item.FileName).HasColumnType("varchar(255)").HasMaxLength(255).IsRequired();
        file.Property(item => item.ContentType).HasColumnType("varchar(32)").HasMaxLength(32).IsRequired();
        file.Property(item => item.Sha256).HasColumnType("varchar(64)").HasMaxLength(64).IsRequired();
        file.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        file.Property(item => item.CreatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        file.Property(item => item.UpdatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        file.Property(item => item.ScanStartedAt).HasConversion(utc).HasColumnType("datetime(6)");
        file.HasIndex(item => new { item.TenantId, item.State, item.CreatedAt });
        file.HasOne<DirectoryTenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
        file.HasQueryFilter(item => item.TenantId == TenantId);

        var reference = modelBuilder.Entity<FileReference>();
        reference.ToTable("platform_file_references");
        reference.HasKey(item => new { item.TenantId, item.FileId, item.Owner, item.EntityId });
        reference.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        reference.Property(item => item.FileId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        reference.Property(item => item.EntityId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        reference.Property(item => item.Owner).HasColumnType("varchar(128)").HasMaxLength(128)
            .UseCollation("utf8mb4_bin").ForMySQLHasCollation("utf8mb4_bin");
        reference.HasOne<QuarantinedFile>().WithMany()
            .HasForeignKey(item => new { item.TenantId, item.FileId }).OnDelete(DeleteBehavior.Restrict);
        reference.HasIndex(item => new { item.TenantId, item.Owner, item.EntityId });
        reference.HasQueryFilter(item => item.TenantId == TenantId);
    }
}
