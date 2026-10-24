using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Schema ownership gate before any challenge fixture connects or applies the new migration.</summary>
public sealed class LoginChallengeMigrationTests
{
    /// <summary>Only the challenge table may be added; tenant binding and CAS remain mandatory.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-MIGRATION-001")]
    public void MigrationOnlyAddsTenantChallengeTable()
    {
        using var database = new PlatformDesignTimeFactory().CreateDbContext([]);
        var assembly = database.GetService<IMigrationsAssembly>();
        var migrations = assembly.Migrations.Where(item => item.Key.EndsWith("_LoginChallenges", StringComparison.Ordinal)).ToArray();
        Assert.Single(migrations);
        var migration = assembly.CreateMigration(migrations[0].Value, database.Database.ProviderName!);
        var table = Assert.IsType<CreateTableOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("platform_login_challenges", table.Name);
        Assert.Equal(new[] { "TenantId", "Id" }, table.PrimaryKey!.Columns);
        Assert.Empty(table.ForeignKeys);
        Assert.Equal(table.Name, Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations)).Name);
        var entity = database.Model.FindEntityType(typeof(LoginChallenge))!;
        Assert.Single(entity.GetDeclaredQueryFilters());
        Assert.True(entity.FindProperty("Version")!.IsConcurrencyToken);
        Assert.Null(entity.FindProperty("Answer"));
        Assert.Null(entity.FindProperty("ExpiresAt"));
        Assert.Null(database.Model.FindEntityType(typeof(LoginSourceBudget)));
    }
}
