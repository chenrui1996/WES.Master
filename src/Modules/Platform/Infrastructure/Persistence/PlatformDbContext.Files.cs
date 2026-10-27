using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    /// <summary>Permanent attachment operation identities, retained independently of the current membership.</summary>
    public DbSet<FileBindingOperation> FileBindingOperations => Set<FileBindingOperation>();

    // Lock the parent before inserting FK children: competing shared FK locks otherwise deadlock on the version update.
    internal async Task<bool> LockFileForBindingAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var transaction = Database.CurrentTransaction
            ?? throw new InvalidOperationException("File binding locks require an active owner transaction.");
        await using var command = Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT 1 FROM platform_files WHERE TenantId = @tenant AND Id = @file FOR UPDATE";
        if (Database.GetCommandTimeout() is { } timeout) command.CommandTimeout = timeout;
        foreach (var (name, value) in new[] { ("@tenant", TenantId.ToString("D")), ("@file", id.ToString("D")) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = System.Data.DbType.String;
            parameter.Size = 36;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteScalarAsync(ct) is not null;
    }

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

        var operation = modelBuilder.Entity<FileBindingOperation>();
        operation.ToTable("platform_file_binding_operations");
        operation.HasKey(item => new { item.TenantId, item.OperationId });
        foreach (var name in new[] { "TenantId", "OperationId", "ActorId", "FileId", "EntityId", "AuditId", "CorrelationId" })
            operation.Property<Guid>(name).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        operation.Property(item => item.Owner).HasColumnType("varchar(128)").HasMaxLength(128)
            .UseCollation("utf8mb4_bin").ForMySQLHasCollation("utf8mb4_bin");
        operation.Property(item => item.ExpectedVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        operation.Property(item => item.ResultVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        operation.Property(item => item.CreatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        operation.HasOne<QuarantinedFile>().WithMany().HasForeignKey(item => new { item.TenantId, item.FileId })
            .OnDelete(DeleteBehavior.Restrict);
        operation.HasIndex(item => new { item.TenantId, item.FileId, item.CreatedAt });
        operation.HasQueryFilter(item => item.TenantId == TenantId);
    }
}
