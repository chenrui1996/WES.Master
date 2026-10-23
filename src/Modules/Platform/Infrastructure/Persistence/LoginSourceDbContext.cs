using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wes.Platform.Domain;

namespace Wes.Platform.Infrastructure.Persistence;

/// <summary>Platform-private global source risk only; no tenant entities, credentials, roles or sessions belong here.</summary>
public sealed class LoginSourceDbContext(DbContextOptions<LoginSourceDbContext> options) : DbContext(options)
{
    /// <summary>One shared source budget across all accounts and tenants, as required by PAR-02.</summary>
    public DbSet<LoginSourceBudget> Sources => Set<LoginSourceBudget>();

    /// <summary>The dedicated owner schema has no tenant-zero escape or cross-owner table mapping.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var source = modelBuilder.Entity<LoginSourceBudget>();
        source.ToTable("platform_login_sources");
        source.HasKey(item => item.SourceDigest);
        source.Property(item => item.SourceDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        source.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        source.Property<string>("ObservationsData").HasColumnType("varchar(2048)").HasMaxLength(2048).IsRequired();
        source.Property(item => item.LastObservedAt).HasConversion(
            value => value.HasValue ? value.Value.UtcDateTime : (DateTime?)null,
            value => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : (DateTimeOffset?)null)
            .HasColumnType("datetime(6)");
        source.Ignore(item => item.Observations);
    }
}

/// <summary>Design-time metadata only; applying migrations requires a separately authorized runtime connection.</summary>
public sealed class LoginSourceDesignTimeFactory : IDesignTimeDbContextFactory<LoginSourceDbContext>
{
    /// <summary>Does not read connection secrets, open a database or create seed accounts.</summary>
    public LoginSourceDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<LoginSourceDbContext>()
        .UseMySQL(mysql => mysql.MigrationsHistoryTable("__WesLoginSourceMigrations")).Options);
}
