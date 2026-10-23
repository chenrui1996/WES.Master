using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Infrastructure.Persistence.Migrations;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Owner migration review gate runs without a database and rejects database-wide generated operations.</summary>
public sealed class PlatformMigrationScopeTests
{
    /// <summary>Generated EF migrations must not alter the shared database merely to create owner tables.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-TENANT-001")]
    public void InitialSessionMigration_OnlyTouchesPlatformTables()
    {
        var operations = new InitialSessions().UpOperations;

        Assert.DoesNotContain(operations, operation => operation is AlterDatabaseOperation or SqlOperation
            or DropTableOperation);
        Assert.All(operations, operation =>
        {
            var table = operation switch
            {
                CreateTableOperation create => create.Name,
                CreateIndexOperation index => index.Table,
                _ => throw new InvalidOperationException("Unexpected operation in initial Platform migration.")
            };
            Assert.Contains(table, new[] { "platform_sessions", "platform_refresh_tokens" });
        });
    }
}
