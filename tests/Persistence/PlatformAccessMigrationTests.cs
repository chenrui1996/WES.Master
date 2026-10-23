using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Verifies the exact owner tables before applying a new migration to the authorized database.</summary>
public sealed class PlatformAccessMigrationTests
{
    /// <summary>No missing table, database-default change or raw SQL may hide in the access migration.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-MIGRATION-001")]
    public void OwnerMigration_ContainsOnlyAccessTablesAndIndexes()
    {
        using var context = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL().Options, Guid.NewGuid());
        var assembly = context.GetService<IMigrationsAssembly>();
        var operations = assembly.Migrations.Where(item => item.Key.EndsWith("_AccessSettingsAudit", StringComparison.Ordinal))
            .SelectMany(item => assembly.CreateMigration(item.Value, context.Database.ProviderName!).UpOperations).ToArray();

        Assert.NotEmpty(operations);
        Assert.DoesNotContain(operations, item => item is AlterDatabaseOperation or SqlOperation or DropTableOperation);
        Assert.Equal(new[] { "platform_audit", "platform_role_memberships", "platform_roles", "platform_settings" },
            operations.OfType<CreateTableOperation>().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.All(operations, item => Assert.True(item is CreateTableOperation or CreateIndexOperation));
    }
}
