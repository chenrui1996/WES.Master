using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Reviews the global source-only schema before any fixture connects or applies it.</summary>
public sealed class LoginSourceMigrationTests
{
    /// <summary>Global risk ownership cannot grow into business or tenant mappings under the source migration.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-MIGRATION-001")]
    public void Migration_OnlyCreatesSourceTable_WithoutDatabaseChanges()
    {
        using var database = new LoginSourceDesignTimeFactory().CreateDbContext([]);
        var assembly = database.GetService<IMigrationsAssembly>();
        var operations = assembly.Migrations.SelectMany(item =>
            assembly.CreateMigration(item.Value, database.Database.ProviderName!).UpOperations).ToArray();
        Assert.Single(operations);
        Assert.Equal("platform_login_sources", Assert.IsType<CreateTableOperation>(operations[0]).Name);
        Assert.Contains("__WesLoginSourceMigrations",
            database.GetService<IHistoryRepository>().GetCreateScript(), StringComparison.Ordinal);
    }
}
