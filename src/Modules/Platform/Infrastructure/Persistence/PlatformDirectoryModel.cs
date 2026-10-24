using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wes.Platform.Domain;

namespace Wes.Platform.Infrastructure.Persistence;

public sealed partial class PlatformDbContext
{
    /// <summary>Explicit tenant metadata; not a global unfiltered tenant directory.</summary>
    public DbSet<DirectoryTenant> DirectoryTenants => Set<DirectoryTenant>();
    /// <summary>Credential-free tenant subjects.</summary>
    public DbSet<DirectoryUser> DirectoryUsers => Set<DirectoryUser>();
    /// <summary>Authoritative department owner records.</summary>
    public DbSet<DirectoryDepartment> Departments => Set<DirectoryDepartment>();
    /// <summary>Stable business dictionary identities.</summary>
    public DbSet<DictionaryEntry> DictionaryEntries => Set<DictionaryEntry>();
    /// <summary>Localized labels, never replacement business codes.</summary>
    public DbSet<DictionaryTranslation> DictionaryTranslations => Set<DictionaryTranslation>();
    /// <summary>Durable explicit dictionary usage.</summary>
    public DbSet<DictionaryReference> DictionaryReferences => Set<DictionaryReference>();

    private void ConfigureDirectory(ModelBuilder model)
    {
        ConfigureCommandReceipts(model);
        var tenant = model.Entity<DirectoryTenant>();
        tenant.ToTable("platform_directory_tenants");
        tenant.HasKey(x => x.TenantId);
        Uuid(tenant.Property(x => x.TenantId));
        Text(tenant.Property(x => x.Name));
        Text(tenant.Property(x => x.DefaultLanguage), 35);
        Version(tenant.Property(x => x.Version));
        tenant.HasQueryFilter(x => x.TenantId == TenantId);

        var department = model.Entity<DirectoryDepartment>();
        department.ToTable("platform_departments");
        department.HasKey(x => new { x.TenantId, x.Id });
        Uuid(department.Property(x => x.TenantId));
        Uuid(department.Property(x => x.Id));
        department.Property(x => x.ParentId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        Text(department.Property(x => x.Code));
        Text(department.Property(x => x.Name));
        Version(department.Property(x => x.Version));
        department.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        department.HasOne<DirectoryTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        department.HasOne<DirectoryDepartment>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentId })
            .OnDelete(DeleteBehavior.Restrict);
        department.HasQueryFilter(x => x.TenantId == TenantId);

        var user = model.Entity<DirectoryUser>();
        user.ToTable("platform_directory_users");
        user.HasKey(x => new { x.TenantId, x.Id });
        Uuid(user.Property(x => x.TenantId));
        Uuid(user.Property(x => x.Id));
        Uuid(user.Property(x => x.DepartmentId));
        Text(user.Property(x => x.Account), 512);
        Text(user.Property(x => x.AccountDigest), 64);
        Text(user.Property(x => x.Name));
        Version(user.Property(x => x.Version));
        user.HasIndex(x => new { x.TenantId, x.AccountDigest }).IsUnique();
        user.HasOne<DirectoryDepartment>().WithMany().HasForeignKey(x => new { x.TenantId, Id = x.DepartmentId })
            .OnDelete(DeleteBehavior.Restrict);
        user.HasQueryFilter(x => x.TenantId == TenantId);

        var entry = model.Entity<DictionaryEntry>();
        entry.ToTable("platform_dictionary_entries");
        entry.HasKey(x => new { x.TenantId, x.Id });
        Uuid(entry.Property(x => x.TenantId));
        Uuid(entry.Property(x => x.Id));
        Text(entry.Property(x => x.Category));
        Text(entry.Property(x => x.Code));
        Text(entry.Property(x => x.LanguageKey));
        Version(entry.Property(x => x.Version));
        entry.HasIndex(x => new { x.TenantId, x.Category, x.Code }).IsUnique();
        entry.HasQueryFilter(x => x.TenantId == TenantId);

        var translation = model.Entity<DictionaryTranslation>();
        translation.ToTable("platform_dictionary_translations");
        translation.HasKey(x => new { x.TenantId, x.EntryId, x.Language });
        Uuid(translation.Property(x => x.TenantId));
        Uuid(translation.Property(x => x.EntryId));
        Text(translation.Property(x => x.Language), 35);
        Text(translation.Property(x => x.Value));
        translation.HasOne<DictionaryEntry>().WithMany().HasForeignKey(x => new { x.TenantId, Id = x.EntryId })
            .OnDelete(DeleteBehavior.Restrict);
        translation.HasQueryFilter(x => x.TenantId == TenantId);

        var reference = model.Entity<DictionaryReference>();
        reference.ToTable("platform_dictionary_references");
        reference.HasKey(x => new { x.TenantId, x.EntryId, x.ReferenceId });
        Uuid(reference.Property(x => x.TenantId));
        Uuid(reference.Property(x => x.EntryId));
        Uuid(reference.Property(x => x.ReferenceId));
        reference.HasOne<DictionaryEntry>().WithMany().HasForeignKey(x => new { x.TenantId, Id = x.EntryId })
            .OnDelete(DeleteBehavior.Restrict);
        reference.HasQueryFilter(x => x.TenantId == TenantId);
    }

    private static void Uuid(PropertyBuilder<Guid> property) =>
        property.HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
    private static void Text(PropertyBuilder<string> property, int length = 128) =>
        property.HasColumnType($"varchar({length})").HasMaxLength(length).IsRequired();
    private static void Version(PropertyBuilder<string> property) =>
        property.HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
}
