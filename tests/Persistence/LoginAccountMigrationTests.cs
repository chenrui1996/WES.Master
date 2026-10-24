using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Reviews account-risk ownership before any fixture applies its migration.</summary>
public sealed class LoginAccountMigrationTests
{
    /// <summary>Account risk cannot weaken existing tables, source ownership or tenant filtering.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-MIGRATION-001")]
    public void MigrationCreatesOnlyTenantAccountRisk_AndModelRetainsIsolation()
    {
        using var database = new PlatformDesignTimeFactory().CreateDbContext([]);
        var assembly = database.GetService<IMigrationsAssembly>();
        var migration = assembly.Migrations.Where(item => item.Key.EndsWith("_AccountLoginRisk", StringComparison.Ordinal)).ToArray();
        Assert.Single(migration);
        var operations = assembly.CreateMigration(migration[0].Value, database.Database.ProviderName!).UpOperations;
        var table = Assert.IsType<CreateTableOperation>(Assert.Single(operations));
        Assert.Equal("platform_login_accounts", table.Name);
        Assert.Equal(new[] { "TenantId", "AccountDigest" }, table.PrimaryKey!.Columns);
        Assert.Empty(table.ForeignKeys);
        var entity = database.Model.FindEntityType(typeof(LoginAccountBudget))!;
        Assert.NotNull(entity.GetDeclaredQueryFilters().Single());
        Assert.True(entity.FindProperty("Version")!.IsConcurrencyToken);
        Assert.Null(database.Model.FindEntityType(typeof(LoginSourceBudget)));
    }
}
