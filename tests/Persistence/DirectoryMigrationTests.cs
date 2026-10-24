using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Reviews the exact additive directory owner model without reading application data.</summary>
public sealed class DirectoryMigrationTests
{
    /// <summary>Directory rollout adds only its six owner tables/indexes and restrictive tenant-composite relations.</summary>
    [Fact]
    public void DirectoryMigration_IsAdditiveAndOwnerScoped()
    {
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL().Options, Guid.NewGuid());
        var assembly = database.GetService<IMigrationsAssembly>();
        var operations = assembly.Migrations.Where(x => x.Key.EndsWith("_DirectoryOwner", StringComparison.Ordinal))
            .SelectMany(x => assembly.CreateMigration(x.Value, database.Database.ProviderName!).UpOperations).ToArray();
        Assert.NotEmpty(operations);
        Assert.All(operations, x => Assert.True(x is CreateTableOperation or CreateIndexOperation));
        Assert.Equal(new[]
        {
            "platform_departments", "platform_dictionary_entries", "platform_dictionary_references",
            "platform_dictionary_translations", "platform_directory_tenants", "platform_directory_users"
        }, operations.OfType<CreateTableOperation>().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.All(operations.OfType<CreateTableOperation>().SelectMany(x => x.ForeignKeys),
            x => Assert.Equal(ReferentialAction.Restrict, x.OnDelete));
    }
}
