using MySql.Data.MySqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Wes.Persistence.Tests;

/// <summary>Uses the authorized source in memory and opens only the isolated test database.</summary>
public sealed class MySqlConnectivityTests(ITestOutputHelper output)
{
    /// <summary>Confirms the actual connection target and reports only non-secret engine metadata.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "MYSQL-CONNECT-001")]
    public async Task AuthorizedConnection_OpensOnlyTestDatabase()
    {
        var connectionString = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var connection = await MySqlTestConnection.OpenAsync(connectionString);
        await using var command = new MySqlCommand("SELECT DATABASE(), VERSION()", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("wes_test", reader.GetString(0));
        output.WriteLine("MySQL server version: {0}", reader.GetString(1));
    }
}
