using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Schema scope/default review before integrated login fixtures apply the owner migration.</summary>
public sealed class LoginOwnerMigrationTests
{
    /// <summary>Only two explicit fail-closed columns and one owner evidence table/index may be added.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-MIGRATION-001")]
    public void MigrationAddsOnlyOwnerStateAndAppendOnlyEvidence()
    {
        using var database = new PlatformDesignTimeFactory().CreateDbContext([]);
        var assembly = database.GetService<IMigrationsAssembly>();
        var metadata = Assert.Single(assembly.Migrations, item => item.Key.EndsWith("_LoginOwner", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(metadata.Value, database.Database.ProviderName!);
        Assert.Equal(4, migration.UpOperations.Count);
        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToArray();
        Assert.Equal(2, columns.Length);
        var enabled = Assert.Single(columns, item => item.Table == "platform_password_credentials");
        Assert.Equal("Enabled", enabled.Name);
        Assert.Equal(false, enabled.DefaultValue);
        var restricted = Assert.Single(columns, item => item.Table == "platform_sessions");
        Assert.Equal("RequiresPasswordChange", restricted.Name);
        Assert.Equal(true, restricted.DefaultValue);
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("platform_login_events", table.Name);
        Assert.Equal(new[] { "TenantId", "Id" }, table.PrimaryKey!.Columns);
        Assert.Empty(table.ForeignKeys);
        Assert.Equal(table.Name, Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>()).Table);
        var evidence = database.Model.FindEntityType(typeof(LoginSecurityEvent))!;
        Assert.Single(evidence.GetDeclaredQueryFilters());
        Assert.Null(evidence.FindProperty("Password"));
        Assert.Null(evidence.FindProperty("Answer"));
        Assert.Null(evidence.FindProperty("Payload"));
        Assert.True(database.Model.FindEntityType(typeof(PasswordCredential))!.FindProperty("Version")!.IsConcurrencyToken);
        Assert.NotNull(database.Model.FindEntityType(typeof(Session))!.FindProperty("RequiresPasswordChange"));
        Assert.Equal(3, migration.DownOperations.Count);
        Assert.All(migration.DownOperations, item => Assert.True(item is DropColumnOperation or DropTableOperation));
    }
}
