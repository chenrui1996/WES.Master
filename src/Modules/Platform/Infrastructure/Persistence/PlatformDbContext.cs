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
    /// <summary>Tenant-owned named roles; menus are never treated as grants.</summary>
    public DbSet<AccessRole> Roles => Set<AccessRole>();
    /// <summary>Explicit tenant-composite assignments.</summary>
    public DbSet<RoleMembership> Memberships => Set<RoleMembership>();
    /// <summary>Only reviewed, typed nonsecret settings.</summary>
    public DbSet<VersionedSetting> Settings => Set<VersionedSetting>();
    /// <summary>Append-only owner audit facts, never raw request data.</summary>
    public DbSet<PlatformAudit> Audit => Set<PlatformAudit>();
    /// <summary>Tenant-account risk independent of user existence, never global source budgets.</summary>
    public DbSet<LoginAccountBudget> LoginAccounts => Set<LoginAccountBudget>();

    /// <summary>Maps only Platform-owned tables with UTC instants, tenant keys and explicit concurrency.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var utc = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var account = modelBuilder.Entity<LoginAccountBudget>();
        account.ToTable("platform_login_accounts");
        account.HasKey(item => new { item.TenantId, item.AccountDigest });
        account.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        account.Property(item => item.AccountDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        account.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        account.Property(item => item.LastFailureAt).HasConversion(utc).HasColumnType("datetime(6)");
        account.Property(item => item.LockedUntil).HasConversion(utc).HasColumnType("datetime(6)");
        account.Property<string>("FailuresData").HasColumnType("varchar(1024)").HasMaxLength(1024).IsRequired();
        account.Ignore(item => item.Failures);
        account.HasQueryFilter(item => item.TenantId == TenantId);

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

        var role = modelBuilder.Entity<AccessRole>();
        role.ToTable("platform_roles");
        role.HasKey(item => new { item.TenantId, item.Id });
        role.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        role.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        role.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64);
        role.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
        role.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        role.Property<string>("RulesData").HasColumnType("longtext").IsRequired();
        role.Ignore(item => item.Rules);
        role.HasQueryFilter(item => item.TenantId == TenantId);

        var member = modelBuilder.Entity<RoleMembership>();
        member.ToTable("platform_role_memberships");
        member.HasKey(item => new { item.TenantId, item.UserId, item.RoleId });
        member.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        member.Property(item => item.UserId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        member.Property(item => item.RoleId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        member.HasOne<AccessRole>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.RoleId })
            .OnDelete(DeleteBehavior.Restrict);
        member.HasQueryFilter(item => item.TenantId == TenantId);

        var setting = modelBuilder.Entity<VersionedSetting>();
        setting.ToTable("platform_settings");
        setting.HasKey(item => new { item.TenantId, item.Id });
        setting.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        setting.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        setting.Property(item => item.Key).HasColumnType("varchar(128)").HasMaxLength(128);
        setting.Property(item => item.Value).HasColumnType("varchar(64)").HasMaxLength(64);
        setting.Property(item => item.Source).HasColumnType("varchar(32)").HasMaxLength(32);
        setting.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        setting.HasIndex(item => new { item.TenantId, item.Key }).IsUnique();
        setting.HasQueryFilter(item => item.TenantId == TenantId);

        var audit = modelBuilder.Entity<PlatformAudit>();
        audit.ToTable("platform_audit");
        audit.HasKey(item => new { item.TenantId, item.Id });
        audit.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.ActorId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.TargetId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.CorrelationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.Action).HasConversion<string>().HasColumnType("varchar(32)").HasMaxLength(32);
        audit.Property(item => item.BeforeVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.AfterVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.OccurredAt).HasConversion(utc).HasColumnType("datetime(6)");
        audit.HasIndex(item => new { item.TenantId, item.OccurredAt, item.Id });
        audit.HasQueryFilter(item => item.TenantId == TenantId);
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
            if (entry.Entity is PlatformAudit && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Platform audit facts are append-only.");
            }
            var tenant = entry.Property("TenantId");
            if (tenant.CurrentValue is not Guid current || current != TenantId
                || (entry.State != EntityState.Added && (tenant.OriginalValue is not Guid original || original != TenantId)))
            {
                throw new InvalidOperationException("Platform write exceeds the trusted tenant scope.");
            }
        }
    }
}
