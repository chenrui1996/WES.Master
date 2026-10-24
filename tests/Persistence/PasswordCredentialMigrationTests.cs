using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Review gate for the exact additive credential owner migration, before any authorized test connection.</summary>
public sealed class PasswordCredentialMigrationTests
{
    /// <summary>Only the credential table and tenant/account unique index may be introduced.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-MIGRATION-001")]
    public void MigrationOnlyAddsCredentialOwnerTableAndUniqueIndex()
    {
        using var database = new PlatformDesignTimeFactory().CreateDbContext([]);
        var assembly = database.GetService<IMigrationsAssembly>();
        var metadata = Assert.Single(assembly.Migrations, item => item.Key.EndsWith("_PasswordCredentials", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(metadata.Value, database.Database.ProviderName!);
        Assert.Equal(2, migration.UpOperations.Count);
        var table = Assert.IsType<CreateTableOperation>(migration.UpOperations[0]);
        Assert.Equal("platform_password_credentials", table.Name);
        Assert.Equal(new[] { "TenantId", "UserId" }, table.PrimaryKey!.Columns);
        Assert.Empty(table.ForeignKeys);
        var index = Assert.IsType<CreateIndexOperation>(migration.UpOperations[1]);
        Assert.Equal(table.Name, index.Table);
        Assert.True(index.IsUnique);
        Assert.Equal(new[] { "TenantId", "AccountDigest" }, index.Columns);
        Assert.Equal(table.Name, Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations)).Name);
        var entity = database.Model.FindEntityType(typeof(PasswordCredential))!;
        Assert.Single(entity.GetDeclaredQueryFilters());
        Assert.True(entity.FindProperty("Version")!.IsConcurrencyToken);
        Assert.Equal(2048, entity.FindProperty("HistoryData")!.GetMaxLength());
        Assert.Null(entity.FindProperty("Recent"));
        Assert.Null(entity.FindProperty("Password"));
    }
}
