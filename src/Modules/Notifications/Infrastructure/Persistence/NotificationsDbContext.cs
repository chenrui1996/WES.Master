using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MySql.EntityFrameworkCore.Extensions;

namespace Wes.Notifications.Infrastructure.Persistence;

/// <summary>Notifications-owned storage scoped to one tenant supplied by trusted application code.</summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, Guid tenantId) : DbContext(options)
{
    /// <summary>Gets the fixed nonempty owner tenant; never derived from an untrusted request body.</summary>
    public Guid TenantId { get; } = tenantId != Guid.Empty
        ? tenantId : throw new ArgumentException("Notifications requires a trusted tenant.", nameof(tenantId));

    internal DbSet<AlarmOccurrenceEntity> Occurrences => Set<AlarmOccurrenceEntity>();
    internal DbSet<AlarmEntity> Alarms => Set<AlarmEntity>();
    internal DbSet<AlarmMembershipEntity> Memberships => Set<AlarmMembershipEntity>();
    internal DbSet<AlarmDeliveryEntity> Deliveries => Set<AlarmDeliveryEntity>();
    internal DbSet<AlarmAttemptEntity> Attempts => Set<AlarmAttemptEntity>();
    internal DbSet<AlarmOperationEntity> Operations => Set<AlarmOperationEntity>();
    internal DbSet<AlarmAuditEntity> Audits => Set<AlarmAuditEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        var fact = builder.Entity<AlarmOccurrenceEntity>();
        fact.ToTable("ntf_alarm_occurrences");
        fact.HasKey(item => new { item.TenantId, item.Id });
        fact.HasQueryFilter(item => item.TenantId == TenantId);
        fact.HasIndex(item => new { item.TenantId, item.SourceOwner, item.SourceEventId }).IsUnique();
        fact.HasIndex(item => new { item.TenantId, item.ReceivedAt, item.Id });
        fact.Property(item => item.SourceOwner).HasColumnType("varchar(64)").HasMaxLength(64)
            .UseCollation("ascii_bin").ForMySQLHasCollation("ascii_bin").HasAnnotation("MySQL:Charset", "ascii");
        fact.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64)
            .UseCollation("ascii_bin").ForMySQLHasCollation("ascii_bin").HasAnnotation("MySQL:Charset", "ascii");
        fact.Property(item => item.Summary).HasColumnType("varchar(512)").HasMaxLength(512)
            .UseCollation("utf8mb4_bin").ForMySQLHasCollation("utf8mb4_bin").HasAnnotation("MySQL:Charset", "utf8mb4");
        fact.Property(item => item.Fingerprint).HasColumnType("varchar(64)").HasMaxLength(64)
            .UseCollation("ascii_bin").ForMySQLHasCollation("ascii_bin").HasAnnotation("MySQL:Charset", "ascii");
        fact.Property(item => item.OccurredAt).HasConversion(
            new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero)));
        fact.Property(item => item.ReceivedAt).HasConversion(new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime, value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))))
            .HasColumnType("datetime(6)");
        foreach (var property in fact.Metadata.GetProperties().Where(property => property.ClrType == typeof(Guid)))
        {
            property.SetValueConverter(new ValueConverter<Guid, string>(value => value.ToString("D"), value => Guid.Parse(value)));
            property.SetColumnType("varchar(36)");
            property.SetMaxLength(36);
        }
        ConfigureConsole(builder);
    }

    /// <inheritdoc />
    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateFacts();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateFacts();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateFacts()
    {
        foreach (var entry in ChangeTracker.Entries<AlarmOccurrenceEntity>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Alarm source occurrences are immutable.");
            if (entry.State == EntityState.Added && entry.Entity.TenantId != TenantId)
                throw new InvalidOperationException("Alarm occurrence tenant does not match its owner scope.");
        }
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.Entity is AlarmMembershipEntity or
                     AlarmAttemptEntity or AlarmOperationEntity or AlarmAuditEntity))
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Alarm memberships, attempts, audits and operation receipts are immutable.");
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added ||
                     entry.State == EntityState.Modified))
            if (entry.Property("TenantId").CurrentValue is Guid tenant && tenant != TenantId)
                throw new InvalidOperationException("Notifications entity tenant does not match its owner scope.");
    }

    private void ConfigureConsole(ModelBuilder builder)
    {
        var alarm = builder.Entity<AlarmEntity>();
        alarm.ToTable("ntf_alarms").HasKey(item => new { item.TenantId, item.Id });
        alarm.HasQueryFilter(item => item.TenantId == TenantId);
        alarm.HasIndex(item => new { item.TenantId, item.AggregateKey });
        alarm.HasIndex(item => new { item.TenantId, item.CreatorId, item.LastOccurredAt, item.Id });
        alarm.Property(item => item.Version).IsConcurrencyToken();
        var membership = builder.Entity<AlarmMembershipEntity>();
        membership.ToTable("ntf_alarm_memberships").HasKey(item => new { item.TenantId, item.OccurrenceId });
        membership.HasQueryFilter(item => item.TenantId == TenantId);
        membership.HasOne<AlarmEntity>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.AlarmId })
            .OnDelete(DeleteBehavior.Restrict);
        membership.HasOne<AlarmOccurrenceEntity>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.OccurrenceId })
            .OnDelete(DeleteBehavior.Restrict);
        var delivery = builder.Entity<AlarmDeliveryEntity>();
        delivery.ToTable("ntf_alarm_deliveries").HasKey(item => new { item.TenantId, item.Id });
        delivery.HasQueryFilter(item => item.TenantId == TenantId);
        delivery.HasIndex(item => new { item.TenantId, item.AlarmId }).IsUnique();
        delivery.HasIndex(item => new { item.TenantId, item.State, item.DueAt, item.Id });
        delivery.Property(item => item.Version).IsConcurrencyToken();
        delivery.HasOne<AlarmEntity>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.AlarmId })
            .OnDelete(DeleteBehavior.Restrict);
        var attempt = builder.Entity<AlarmAttemptEntity>();
        attempt.ToTable("ntf_alarm_delivery_attempts").HasKey(item => new { item.TenantId, item.Id });
        attempt.HasQueryFilter(item => item.TenantId == TenantId);
        attempt.HasOne<AlarmDeliveryEntity>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.DeliveryId })
            .OnDelete(DeleteBehavior.Restrict);
        var operation = builder.Entity<AlarmOperationEntity>();
        operation.ToTable("ntf_alarm_operations").HasKey(item => new { item.TenantId, item.OperationId });
        operation.HasQueryFilter(item => item.TenantId == TenantId);
        var audit = builder.Entity<AlarmAuditEntity>();
        audit.ToTable("ntf_alarm_audits").HasKey(item => new { item.TenantId, item.Id });
        audit.HasQueryFilter(item => item.TenantId == TenantId);
        audit.HasIndex(item => new { item.TenantId, item.OperationId }).IsUnique();
        var ownerLock = builder.Entity<AlarmConsoleLockEntity>();
        ownerLock.ToTable("ntf_alarm_console_locks").HasKey(item => item.TenantId);
        ownerLock.HasQueryFilter(item => item.TenantId == TenantId);
        foreach (var entity in builder.Model.GetEntityTypes().Where(entity => entity.ClrType != typeof(AlarmOccurrenceEntity)))
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?))
                {
                    property.SetValueConverter(new ValueConverter<Guid, string>(value => value.ToString("D"), value => Guid.Parse(value)));
                    property.SetColumnType("varchar(36)");
                    property.SetMaxLength(36);
                }
                else if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(
                        value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero)));
                else if (property.ClrType == typeof(string))
                {
                    var length = property.Name switch { "Body" => 4096, "Reason" => 512, _ => 64 };
                    var ascii = property.Name is not ("Body" or "Reason");
                    var collation = ascii ? "ascii_bin" : "utf8mb4_bin";
                    builder.Entity(entity.ClrType).Property(property.Name).HasColumnType($"varchar({length})")
                        .HasMaxLength(length).UseCollation(collation).ForMySQLHasCollation(collation)
                        .HasAnnotation("MySQL:Charset", ascii ? "ascii" : "utf8mb4");
                }
            }
        }
    }
}
