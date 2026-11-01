using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Wes.WorkflowForm.Infrastructure.Persistence;

/// <summary>WorkflowForm-owned persistence, explicitly scoped to one trusted tenant.</summary>
public sealed class WorkflowFormDbContext(DbContextOptions<WorkflowFormDbContext> options, Guid tenantId) : DbContext(options)
{
    /// <summary>Owner tenant scope supplied by trusted application code.</summary>
    public Guid TenantId { get; } = tenantId != Guid.Empty
        ? tenantId
        : throw new ArgumentException("WorkflowForm persistence requires a trusted tenant.", nameof(tenantId));

    internal DbSet<WorkflowSchemaDraftEntity> SchemaDrafts => Set<WorkflowSchemaDraftEntity>();
    internal DbSet<WorkflowDefinitionDraftEntity> DefinitionDrafts => Set<WorkflowDefinitionDraftEntity>();
    internal DbSet<WorkflowSchemaVersionEntity> SchemaVersions => Set<WorkflowSchemaVersionEntity>();
    internal DbSet<WorkflowDefinitionVersionEntity> DefinitionVersions => Set<WorkflowDefinitionVersionEntity>();
    internal DbSet<WorkflowOperationReceiptEntity> OperationReceipts => Set<WorkflowOperationReceiptEntity>();
    internal DbSet<WorkflowAuditFactEntity> AuditFacts => Set<WorkflowAuditFactEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        ConfigureDraft(builder.Entity<WorkflowSchemaDraftEntity>(), "wf_schema_drafts");
        ConfigureDraft(builder.Entity<WorkflowDefinitionDraftEntity>(), "wf_definition_drafts");
        ConfigureVersion(builder.Entity<WorkflowSchemaVersionEntity>(), "wf_schema_versions");
        ConfigureVersion(builder.Entity<WorkflowDefinitionVersionEntity>(), "wf_definition_versions");

        var audit = builder.Entity<WorkflowAuditFactEntity>();
        audit.ToTable("wf_audit_facts");
        audit.HasKey(item => new { item.TenantId, item.Id });
        audit.HasQueryFilter(item => item.TenantId == TenantId);
        audit.Property(item => item.ResourceKind).HasColumnType("varchar(32)").HasMaxLength(32).UseCollation("utf8mb4_bin");
        audit.Property(item => item.Action).HasColumnType("varchar(48)").HasMaxLength(48).UseCollation("utf8mb4_bin");
        audit.Property(item => item.BeforeVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.AfterVersion).HasColumnType("varchar(36)").HasMaxLength(36).IsRequired();
        audit.HasIndex(item => new { item.TenantId, item.OperationId }).IsUnique();

        var receipt = builder.Entity<WorkflowOperationReceiptEntity>();
        receipt.ToTable("wf_operation_receipts");
        receipt.HasKey(item => new { item.TenantId, item.OperationId });
        receipt.HasQueryFilter(item => item.TenantId == TenantId);
        receipt.Property(item => item.Action).HasColumnType("varchar(48)").HasMaxLength(48).UseCollation("utf8mb4_bin");
        receipt.Property(item => item.Fingerprint).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("ascii_bin");
        receipt.Property(item => item.ResultJson).HasColumnType("longtext").IsRequired();
        receipt.HasOne<WorkflowAuditFactEntity>().WithMany()
            .HasForeignKey(item => new { item.TenantId, Id = item.AuditFactId }).OnDelete(DeleteBehavior.Restrict);

        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?))
                {
                    property.SetValueConverter(property.ClrType == typeof(Guid)
                        ? new ValueConverter<Guid, string>(value => value.ToString("D"), value => Guid.Parse(value))
                        : new ValueConverter<Guid?, string?>(value => value.HasValue ? value.Value.ToString("D") : null,
                            value => value == null ? null : Guid.Parse(value)));
                    property.SetColumnType("varchar(36)");
                    property.SetMaxLength(36);
                }

                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, DateTime>(
                        value => value.UtcDateTime,
                        value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))));
                    property.SetColumnType("datetime(6)");
                }
            }
        }
    }

    private void ConfigureDraft<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        string table) where TEntity : class
    {
        entity.ToTable(table);
        entity.HasKey("TenantId", "Id");
        entity.HasQueryFilter(item => EF.Property<Guid>(item, "TenantId") == TenantId);
        entity.Property<string>("BusinessType").HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        entity.Property<int>("PlannedVersion").IsRequired();
        entity.Property<string>("DraftVersion").HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        entity.Property<string>("SnapshotJson").HasColumnType("longtext").IsRequired();
        entity.HasIndex("TenantId", "LastOperationId").IsUnique();
        entity.HasIndex("TenantId", "AuditFactId").IsUnique();
        entity.HasOne<WorkflowOperationReceiptEntity>().WithMany()
            .HasForeignKey("TenantId", "LastOperationId").OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<WorkflowAuditFactEntity>().WithMany()
            .HasForeignKey("TenantId", "AuditFactId").OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureVersion<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        string table) where TEntity : class
    {
        entity.ToTable(table);
        entity.HasKey("TenantId", "Id", "Version");
        entity.HasQueryFilter(item => EF.Property<Guid>(item, "TenantId") == TenantId);
        entity.Property<string>("VersionToken").HasColumnType("varchar(36)").HasMaxLength(36).IsRequired();
        entity.Property<string>("BusinessType").HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        entity.Property<string>("SnapshotJson").HasColumnType("longtext").IsRequired();
        entity.Property<Guid>("OperationId").IsRequired();
        entity.HasIndex("TenantId", "OperationId").IsUnique();
        entity.HasOne<WorkflowOperationReceiptEntity>().WithMany()
            .HasForeignKey("TenantId", "OperationId").OnDelete(DeleteBehavior.Restrict);
    }

    /// <inheritdoc />
    public override int SaveChanges()
    {
        ValidateOwnerWrites();
        return base.SaveChanges();
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ValidateOwnerWrites();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ValidateOwnerWrites()
    {
        if (ChangeTracker.Entries().Any(entry =>
                (entry.Entity is WorkflowSchemaVersionEntity or WorkflowDefinitionVersionEntity
                    or WorkflowOperationReceiptEntity or WorkflowAuditFactEntity)
                && entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Published WorkflowForm snapshots, operation receipts and audit facts are immutable.");

        var receipts = ChangeTracker.Entries<WorkflowOperationReceiptEntity>()
            .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToArray();
        var facts = ChangeTracker.Entries<WorkflowAuditFactEntity>()
            .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToArray();
        if (receipts.Length != facts.Length || receipts.Any(receipt => facts.Count(fact =>
                fact.TenantId == receipt.TenantId && fact.Id == receipt.AuditFactId
                && fact.OperationId == receipt.OperationId && fact.ResourceId == receipt.ResourceId
                && fact.ActorId == receipt.ActorId && fact.CorrelationId == receipt.CorrelationId) != 1))
            throw new InvalidOperationException("Each WorkflowForm mutation requires one matching owner-local audit fact.");

        foreach (var entry in ChangeTracker.Entries().Where(entry =>
                     entry.Entity is WorkflowSchemaDraftEntity or WorkflowDefinitionDraftEntity
                     && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var tenant = (Guid)entry.Property(nameof(WorkflowSchemaDraftEntity.TenantId)).CurrentValue!;
            var operationId = (Guid)entry.Property(nameof(WorkflowSchemaDraftEntity.LastOperationId)).CurrentValue!;
            var auditId = (Guid)entry.Property(nameof(WorkflowSchemaDraftEntity.AuditFactId)).CurrentValue!;
            if (!receipts.Any(receipt => receipt.TenantId == tenant && receipt.OperationId == operationId && receipt.AuditFactId == auditId))
                throw new InvalidOperationException("WorkflowForm draft changes require a same-transaction operation and audit fact.");
        }

        foreach (var entry in ChangeTracker.Entries().Where(entry =>
                     entry.Entity is WorkflowSchemaVersionEntity or WorkflowDefinitionVersionEntity
                     && entry.State == EntityState.Added))
        {
            var tenant = (Guid)entry.Property(nameof(WorkflowSchemaVersionEntity.TenantId)).CurrentValue!;
            var operationId = (Guid)entry.Property(nameof(WorkflowSchemaVersionEntity.OperationId)).CurrentValue!;
            if (!receipts.Any(receipt => receipt.TenantId == tenant && receipt.OperationId == operationId))
                throw new InvalidOperationException("Published WorkflowForm versions require a same-transaction operation receipt.");
        }
    }
}
