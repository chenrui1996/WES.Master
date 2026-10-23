using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>
/// Owns only synthetic probe rows in wes_test. It never creates or drops a database.
/// The test class serializes DDL and tracks exact run IDs for cleanup.
/// </summary>
public sealed class MySqlProviderFixture : IAsyncLifetime
{
    private readonly List<string> _runs = [];
    private string _connectionString = "";
    private bool _initialized;

    internal string RunId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Validates the authorized connection before any EF operation.</summary>
    public async Task InitializeAsync()
    {
        _connectionString = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var connection = await MySqlTestConnection.OpenAsync(_connectionString);
        await using var context = await CreateContextAsync();
        await context.Database.MigrateAsync();
        _initialized = true;
    }

    internal async Task<ProbeContext> CreateContextAsync(string tenant = "tenant-a", string? run = null)
    {
        MySqlTestConnection.ValidateTarget(_connectionString);
        var options = new DbContextOptionsBuilder<ProbeContext>()
            // Give EF the protected original: cloning an opened connection loses its password
            // when PersistSecurityInfo=false. Do not weaken that setting to fix authentication.
            .UseMySQL(_connectionString, mysql => mysql.MigrationsHistoryTable("__WesFoundationMigrations"))
            .Options;
        var runId = run ?? RunId;
        if (!_runs.Contains(runId))
        {
            _runs.Add(runId);
        }

        var context = new ProbeContext(options, runId, tenant);
        try
        {
            await context.Database.OpenConnectionAsync();
            return context;
        }
        catch (MySql.Data.MySqlClient.MySqlException error)
        {
            await context.DisposeAsync();
            throw new InvalidOperationException($"MySQL EF connection failed (driver code {error.Number}); credentials withheld.");
        }
    }

    internal static ProbeRow Row(ProbeContext context, string code, string? id = null, decimal quantity = 10m) => new()
    {
        RunId = context.RunId,
        TenantId = context.TenantId,
        Id = id ?? Guid.NewGuid().ToString("N"),
        Code = code,
        Quantity = quantity
    };

    internal async Task DeleteRunAsync(string run)
    {
        if (!_runs.Contains(run))
        {
            throw new InvalidOperationException("Cleanup must reference a run owned by this fixture.");
        }

        await using var context = await CreateContextAsync(run: run);
        // MySQL 8.0.12 rejects the aliased DELETE emitted by ExecuteDelete.
        // Bounded fixture rows use tracked primary-key deletes, not an untested bulk fallback.
        var rows = await context.Rows.IgnoreQueryFilters().Where(item => item.RunId == run).ToListAsync();
        context.Rows.RemoveRange(rows);
        await context.SaveChangesAsync();
        Assert.Equal(0, await context.Rows.IgnoreQueryFilters().CountAsync(item => item.RunId == run));
    }

    /// <summary>Deletes and verifies only run IDs allocated by this fixture; table/history remain for reruns.</summary>
    public async Task DisposeAsync()
    {
        // Test methods cannot have created rows if fixture initialization failed.
        // Preserve the original migration failure instead of causing a second cleanup failure.
        if (!_initialized)
        {
            return;
        }

        foreach (var run in _runs.ToArray())
        {
            await DeleteRunAsync(run);
        }
    }
}
