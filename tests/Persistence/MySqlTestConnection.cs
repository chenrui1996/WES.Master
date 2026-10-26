using System.Text.Json;
using MySql.Data.MySqlClient;

namespace Wes.Persistence.Tests;

internal static class MySqlTestConnection
{
    internal static string FromAuthorizedSource(string source)
    {
        var builder = Parse(source);
        builder.Database = "wes_test";
        builder.AllowLoadLocalInfile = false;
        builder.PersistSecurityInfo = false;
        builder.Pooling = false;
        builder.ConnectionTimeout = 5;
        builder.DefaultCommandTimeout = 10;
        return builder.ConnectionString;
    }

    internal static void ValidateTarget(string connectionString)
    {
        if (Parse(connectionString).Database != "wes_test")
        {
            throw new InvalidOperationException("MySQL test connection must target wes_test.");
        }
    }

    internal static string ReadSource(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Database", out var database)
                && database.ValueKind == JsonValueKind.Object
                && database.TryGetProperty("ConnectionString", out var connection)
                && connection.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(connection.GetString()))
            {
                return connection.GetString()!;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Authorized MySQL configuration is not valid JSON.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Authorized MySQL configuration cannot be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Authorized MySQL configuration access was denied.");
        }

        throw new InvalidOperationException("Authorized configuration requires Database.ConnectionString.");
    }

    internal static string LoadAuthorizedTestConnection()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WES.slnx")))
        {
            root = root.Parent;
        }

        if (root is null) throw new InvalidOperationException("Cannot locate the authorized sibling configuration.");
        return FromAuthorizedSource(ReadSource(ResolveAuthorizedSourcePath(root)));
    }

    internal static string ResolveAuthorizedSourcePath(DirectoryInfo repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        var projectRoot = repositoryRoot.Parent;
        if (projectRoot?.Name.EndsWith(".worktrees", StringComparison.OrdinalIgnoreCase) == true)
            projectRoot = projectRoot.Parent;
        if (projectRoot is null) throw new InvalidOperationException("Cannot locate the authorized sibling configuration.");
        return Path.Combine(projectRoot.FullName, "Seven.Master", "Seven.Net8", "Seven.WebApi", "appsettings.json");
    }

    internal static async Task<MySqlConnection> OpenAsync(string connectionString)
    {
        ValidateTarget(connectionString);
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch (MySqlException error)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException($"MySQL test connection failed (driver code {error.Number}); credentials withheld.");
        }
    }

    private static MySqlConnectionStringBuilder Parse(string value)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            // Driver messages can reflect the original configuration; never attach the exception.
            throw new InvalidOperationException("MySQL source connection is malformed.");
        }

        if (string.IsNullOrWhiteSpace(builder.Server) || string.IsNullOrWhiteSpace(builder.UserID)
            || string.IsNullOrEmpty(builder.Password))
        {
            throw new InvalidOperationException("MySQL source requires server, username and password.");
        }

        return builder;
    }
}
