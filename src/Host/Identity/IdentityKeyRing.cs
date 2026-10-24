using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Wes.Host.Identity;

/// <summary>Production cookie keys require explicit reviewed persistent protected storage.</summary>
public static class IdentityKeyRing
{
    /// <summary>Rejects missing or nonabsolute storage instead of silently using an ephemeral production key ring.</summary>
    public static void Validate(string? path, bool production)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (production) throw new InvalidOperationException("Production identity requires a reviewed persistent protected key ring.");
            return;
        }
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidOperationException("Identity key ring must be an existing reviewed absolute directory.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This deployment requires a reviewed non-Windows key-at-rest protector before identity can be enabled.");
    }
    /// <summary>Uses the final deployment configuration; Windows DPAPI protects stored keys for the service identity.</summary>
    public static void Configure(KeyManagementOptions options, IConfiguration configuration, IHostEnvironment environment, ILoggerFactory logs)
    {
        var path = configuration["Wes:Identity:KeyRingPath"];
        Validate(path, environment.IsProduction());
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A reviewed platform key protector is required.");
        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), logs);
        options.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: false, logs);
    }
}
