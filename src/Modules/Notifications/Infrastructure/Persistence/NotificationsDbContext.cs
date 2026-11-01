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
    }
}
