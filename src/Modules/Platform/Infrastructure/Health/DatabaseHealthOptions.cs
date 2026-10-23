using MySql.Data.MySqlClient;

namespace Wes.Platform.Infrastructure.Health;

/// <summary>
/// Immutable-after-startup dependency probe configuration.
/// Secrets are injected by the host environment and never formatted or logged.
/// </summary>
public sealed class DatabaseHealthOptions
{
    /// <summary>Empty means unconfigured, not healthy. This development slice permits only wes_test.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Maximum probe duration, from one through five seconds; not a device deadline.</summary>
    public int TimeoutSeconds { get; set; } = 3;

    internal static bool HasValidConnection(DatabaseHealthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return true;
        }

        try
        {
            var parsed = new MySqlConnectionStringBuilder(options.ConnectionString);
            return parsed.Database == "wes_test" && !string.IsNullOrWhiteSpace(parsed.Server)
                && !string.IsNullOrWhiteSpace(parsed.UserID) && !string.IsNullOrEmpty(parsed.Password);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
