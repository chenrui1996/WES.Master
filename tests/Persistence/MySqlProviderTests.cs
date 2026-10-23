using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Checks real MySQL effects with bounded synthetic data, not an in-memory provider.</summary>
public sealed class MySqlProviderTests(MySqlProviderFixture fixture) : IClassFixture<MySqlProviderFixture>
{
    /// <summary>The migration is applied to wes_test and a second application must preserve data.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-MIGRATE-001")]
    public async Task Migration_IsAppliedAndIdempotent()
    {
        await using var context = await fixture.CreateContextAsync();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains("202610230001_FoundationProbe", applied);
        Assert.Contains("202610230002_ProbeConstraints", applied);
        var row = MySqlProviderFixture.Row(context, Guid.NewGuid().ToString("N"));
        context.Rows.Add(row);
        await context.SaveChangesAsync();
        await context.Database.MigrateAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.True(await context.Rows.AnyAsync(item => item.Id == row.Id));
    }

    /// <summary>A failure after SaveChanges but before commit must leave no durable row.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-TXN-001")]
    public async Task Transaction_RollbackRemovesUncommittedWrite_CommitPreservesDecimal()
    {
        var id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var context = await fixture.CreateContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Rows.Add(MySqlProviderFixture.Row(context, id, id));
            await context.SaveChangesAsync();
            throw new InvalidOperationException("Injected application failure before commit.");
        });

        await using var verification = await fixture.CreateContextAsync();
        Assert.False(await verification.Rows.AnyAsync(item => item.Id == id));
        verification.Rows.Add(MySqlProviderFixture.Row(verification, id, id, 1234567890123456789012.123456m));
        await verification.SaveChangesAsync();
        await using var committed = await fixture.CreateContextAsync();
        Assert.Equal(1234567890123456789012.123456m,
            await committed.Rows.Where(item => item.Id == id).Select(item => item.Quantity).SingleAsync());
    }

    /// <summary>The database, not a preflight application query, rejects a duplicate tenant code.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-UNIQUE-001")]
    public async Task DuplicateCode_IsRejectedWithinTenant_ButAllowedAcrossTenants()
    {
        var code = Guid.NewGuid().ToString("N");
        await using (var context = await fixture.CreateContextAsync())
        {
            context.Rows.Add(MySqlProviderFixture.Row(context, code));
            await context.SaveChangesAsync();
        }

        await using (var duplicate = await fixture.CreateContextAsync())
        {
            duplicate.Rows.Add(MySqlProviderFixture.Row(duplicate, code));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            Assert.Equal(1062, Assert.IsType<MySqlException>(error.InnerException).Number);
        }

        await using var originalTenant = await fixture.CreateContextAsync();
        Assert.Equal(1, await originalTenant.Rows.CountAsync(item => item.Code == code));
        await using var otherTenant = await fixture.CreateContextAsync("tenant-b");
        otherTenant.Rows.Add(MySqlProviderFixture.Row(otherTenant, code));
        await otherTenant.SaveChangesAsync();
        Assert.Equal(1, await otherTenant.Rows.CountAsync(item => item.Code == code));
    }

    /// <summary>Two contexts reuse the EF model but retain their own tenant/run filter parameters.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-TENANT-001")]
    public async Task ContextFilters_IsolateTenantsWithSameRowId()
    {
        var id = Guid.NewGuid().ToString("N");
        await using (var a = await fixture.CreateContextAsync())
        {
            a.Rows.Add(MySqlProviderFixture.Row(a, id, id, 3m));
            await a.SaveChangesAsync();
        }

        await using (var b = await fixture.CreateContextAsync("tenant-b"))
        {
            b.Rows.Add(MySqlProviderFixture.Row(b, id, id, 7m));
            await b.SaveChangesAsync();
        }

        await using var readA = await fixture.CreateContextAsync();
        await using var readB = await fixture.CreateContextAsync("tenant-b");
        Assert.Equal(3m, await readA.Rows.Where(item => item.Id == id).Select(item => item.Quantity).SingleAsync());
        Assert.Equal(7m, await readB.Rows.Where(item => item.Id == id).Select(item => item.Quantity).SingleAsync());
    }

    /// <summary>Two independently tracked stale versions race; only one may alter durable state.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-CONCURRENCY-001")]
    public async Task ConcurrentUpdates_OnlyOneVersionWins()
    {
        var id = Guid.NewGuid().ToString("N");
        await using (var seed = await fixture.CreateContextAsync())
        {
            seed.Rows.Add(MySqlProviderFixture.Row(seed, id, id));
            await seed.SaveChangesAsync();
        }

        await using var first = await fixture.CreateContextAsync();
        await using var second = await fixture.CreateContextAsync();
        var a = await first.Rows.SingleAsync(item => item.Id == id);
        var b = await second.Rows.SingleAsync(item => item.Id == id);
        a.Quantity = 11m;
        b.Quantity = 12m;
        a.Version = b.Version = 2;

        var results = await Task.WhenAll(TrySaveAsync(first), TrySaveAsync(second));

        Assert.Equal(1, results.Count(success => success));
        await using var verification = await fixture.CreateContextAsync();
        var stored = await verification.Rows.SingleAsync(item => item.Id == id);
        Assert.Equal(results[0] ? 11m : 12m, stored.Quantity);
        Assert.Equal(2, stored.Version);
    }

    /// <summary>Cleanup targets the owned run only, preserving another synthetic run as a sentinel.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-CLEANUP-001")]
    public async Task Cleanup_DeletesOnlySelectedRun()
    {
        var ownedRun = Guid.NewGuid().ToString("N");
        var otherRun = Guid.NewGuid().ToString("N");
        await using var owned = await fixture.CreateContextAsync(run: ownedRun);
        await using var other = await fixture.CreateContextAsync(run: otherRun);
        owned.Rows.Add(MySqlProviderFixture.Row(owned, "cleanup"));
        other.Rows.Add(MySqlProviderFixture.Row(other, "sentinel"));
        await owned.SaveChangesAsync();
        await other.SaveChangesAsync();

        await fixture.DeleteRunAsync(ownedRun);

        Assert.Equal(0, await owned.Rows.IgnoreQueryFilters().CountAsync(item => item.RunId == ownedRun));
        Assert.Equal(1, await other.Rows.IgnoreQueryFilters().CountAsync(item => item.RunId == otherRun));
    }

    /// <summary>Cleanup must never accept a run ID not allocated by the fixture.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-CLEANUP-001")]
    public async Task Cleanup_UnownedRun_IsRejectedBeforeDatabaseWrite()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeleteRunAsync(Guid.NewGuid().ToString("N")));
    }

    /// <summary>Confirms transactional storage and the actual database precision, not just EF metadata.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-MIGRATE-001")]
    public async Task ProbeSchema_UsesTransactionalEngineAndRequiredDecimalPrecision()
    {
        await using var context = await fixture.CreateContextAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT t.ENGINE, c.NUMERIC_PRECISION, c.NUMERIC_SCALE
            FROM information_schema.TABLES AS t
            JOIN information_schema.COLUMNS AS c
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_SCHEMA = DATABASE()
              AND t.TABLE_NAME = 'wes_foundation_probe' AND c.COLUMN_NAME = 'Quantity'
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("InnoDB", reader.GetString(0));
        Assert.Equal(28, reader.GetInt32(1));
        Assert.Equal(6, reader.GetInt32(2));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<bool> TrySaveAsync(ProbeContext context)
    {
        try
        {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
