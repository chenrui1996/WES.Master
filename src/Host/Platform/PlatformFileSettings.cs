using System.Globalization;
using System.Net;
using Wes.Platform;

namespace Wes.Host.Platform;

/// <summary>Explicit deployment configuration; storage paths are not tenant-editable or diagnostic-formatted.</summary>
public sealed class PlatformFileSettings
{
    private PlatformFileSettings(bool enabled, string? root, IPAddress? address, int port)
    { Enabled = enabled; StorageRoot = root; ScannerAddress = address; ScannerPort = port; }
    /// <summary>Default-closed staging HTTP surface.</summary>
    public bool Enabled { get; }
    /// <summary>Existing private root outside all configured Host static roots.</summary>
    public string? StorageRoot { get; }
    /// <summary>Explicit loopback IP, not unauthenticated remote clamd.</summary>
    public IPAddress? ScannerAddress { get; }
    /// <summary>Explicit scanner endpoint; zero only when not configured and disabled.</summary>
    public int ScannerPort { get; }

    /// <summary>Rejects unknown/nested/malformed settings, disabled invalid values, and overlapping public roots.</summary>
    public static PlatformFileSettings Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Wes:Files");
        var entries = section.GetChildren().ToArray();
        string[] supported = ["Enabled", "StorageRoot", "ScannerAddress", "ScannerPort"];
        InvalidOperationException Invalid() => new("Platform file deployment configuration is invalid.");
        if (section.Value is not null || entries.Any(entry => entry.GetChildren().Any()
            || !supported.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))) throw Invalid();
        bool Has(string key) => entries.Any(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var enabled = false;
        if (Has("Enabled") && !bool.TryParse(section["Enabled"], out enabled)) throw Invalid();
        string? root = section["StorageRoot"];
        if (Has("StorageRoot") || enabled)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw Invalid();
            root = Path.GetFullPath(root);
            string?[] publicRoots = [environment.ContentRootPath, (environment as IWebHostEnvironment)?.WebRootPath,
                configuration["Wes:Web:Root"]];
            foreach (var publicRoot in publicRoots.Where(value => !string.IsNullOrWhiteSpace(value)))
                if (Overlaps(root, Path.GetFullPath(publicRoot!))) throw Invalid();
        }
        IPAddress? address = null;
        if ((Has("ScannerAddress") || enabled) && (!IPAddress.TryParse(section["ScannerAddress"], out address)
            || !IPAddress.IsLoopback(address))) throw Invalid();
        var port = 0;
        if ((Has("ScannerPort") || enabled) && (!int.TryParse(section["ScannerPort"], NumberStyles.None,
            CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) throw Invalid();
        if (enabled && (!bool.TryParse(configuration["Wes:Identity:Enabled"], out var identity) || !identity)) throw Invalid();
        return new(enabled, root, address, port);
    }

    private static bool Overlaps(string left, string right) => Within(left, right) || Within(right, left);
    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
    /// <summary>Does not reveal a local storage path in incidental formatting.</summary>
    public override string ToString() => "PlatformFileSettings[REDACTED]";
}

/// <summary>Default-closed file deployment composition; creates no roots, services or migrations.</summary>
public static class PlatformFileRuntime
{
    /// <summary>Registers only explicit real local/loopback adapters; startup validates the existing private root.</summary>
    public static IServiceCollection AddWesPlatformFiles(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddSingleton(provider => PlatformFileSettings.Load(provider.GetRequiredService<IConfiguration>(), environment));
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<PlatformFileSettings>();
            if (!settings.Enabled) throw new InvalidOperationException("Platform file deployment is disabled.");
            return PlatformFileAdapters.Local(settings.StorageRoot!, settings.ScannerAddress!, settings.ScannerPort,
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILoggerFactory>());
        });
        return services;
    }
}
