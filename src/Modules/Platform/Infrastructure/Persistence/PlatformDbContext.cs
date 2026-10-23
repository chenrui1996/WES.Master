using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wes.Platform.Domain;

namespace Wes.Platform.Infrastructure.Persistence;

/// <summary>Platform-owned persistence only. Every operational instance requires an explicit trusted tenant.</summary>
public sealed class PlatformDbContext : DbContext
{
    /// <summary>Rejects absent tenant identity before any model, query, or database connection is used.</summary>
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options, Guid tenantId) : base(options)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Platform persistence requires a trusted tenant.");
        }

        TenantId = tenantId;
    }

    /// <summary>Trusted tenant scope, not a client-controlled filter or implicit shared owner.</summary>
    public Guid TenantId { get; }

    /// <summary>Refresh families belonging to the current tenant.</summary>
    public DbSet<Session> Sessions => Set<Session>();

    /// <summary>Maps only Platform-owned tables with UTC instants, tenant keys and explicit concurrency.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var utc = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var session = modelBuilder.Entity<Session>();
        session.ToTable("platform_sessions");
        session.HasKey(item => new { item.TenantId, item.Id });
        session.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        session.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        session.Property(item => item.UserId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        session.Property(item => item.Client).HasConversion<string>().HasColumnType("varchar(8)").HasMaxLength(8);
        session.Property(item => item.CreatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        session.Property(item => item.LastActivityAt).HasConversion(utc).HasColumnType("datetime(6)");
        session.Property(item => item.RevokedAt).HasConversion(utc).HasColumnType("datetime(6)");
        session.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        session.Ignore(item => item.ExpiresAt);
        session.HasQueryFilter(item => item.TenantId == TenantId);
        session.HasMany(item => item.Credentials).WithOne()
            .HasForeignKey(item => new { item.TenantId, item.SessionId }).OnDelete(DeleteBehavior.Cascade);
        session.Navigation(item => item.Credentials).HasField("_credentials").UsePropertyAccessMode(PropertyAccessMode.Field);

        var credential = modelBuilder.Entity<RefreshCredential>();
        credential.ToTable("platform_refresh_tokens");
        credential.HasKey(item => new { item.TenantId, item.Digest });
        credential.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        credential.Property(item => item.SessionId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        credential.Property(item => item.Digest).HasColumnType("varchar(64)").HasMaxLength(64);
        credential.Property(item => item.ConsumedAt).HasConversion(utc).HasColumnType("datetime(6)");
        credential.HasQueryFilter(item => item.TenantId == TenantId);
    }

    /// <summary>Protects every synchronous write path from cross-tenant attachment or owner changes.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateWriteScope();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>Protects asynchronous writes and propagates cancellation into the owner transaction.</summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateWriteScope();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateWriteScope()
    {
        foreach (var entry in ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var tenant = entry.Property("TenantId");
            if (tenant.CurrentValue is not Guid current || current != TenantId
                || (entry.State != EntityState.Added && (tenant.OriginalValue is not Guid original || original != TenantId)))
            {
                throw new InvalidOperationException("Platform write exceeds the trusted tenant scope.");
            }
        }
    }
}
