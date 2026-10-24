using System.Text;
using Wes.Platform;
using Wes.Platform.Features.Directory;

namespace Wes.Host.Identity;

/// <summary>Explicit process-only provisioning adapter; never registered as an HTTP route.</summary>
public static class LocalProvisioning
{
    /// <summary>Validates the local gate and reviewed metadata before reading a non-echoing password.</summary>
    public static async Task<bool> RunAsync(IServiceProvider services, IConfiguration configuration, Func<string> readPassword)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("LocalProvisioning");
        var section = configuration.GetSection("Wes:Provisioning");
        if (!section.GetValue<bool>("Enabled"))
        { logger.LogWarning("Local provisioning rejected {Code}", "ProvisioningGateClosed"); return false; }
        string Required(string key) => !string.IsNullOrWhiteSpace(section[key]) ? section[key]!
            : throw new InvalidOperationException("Explicit reviewed provisioning metadata is incomplete.");
        Guid Id(string key) => Guid.TryParseExact(Required(key), "D", out var value) && value != Guid.Empty ? value
            : throw new InvalidOperationException("Explicit provisioning references must be canonical nonempty UUIDs.");
        var tenant = Id("TenantId");
        var grants = section.GetSection("ReviewedGrants").Get<PermissionRuleInput[]>()
            ?? throw new InvalidOperationException("Explicit reviewed provisioning grants are required.");
        if (grants.Length == 0) throw new InvalidOperationException("An empty initial management grant set cannot provision an operator.");
        var input = new LocalDirectoryInput(Id("UserId"), Required("TenantName"), Required("DefaultLanguage"),
            Required("DepartmentCode"), Required("DepartmentName"), Required("Account"), Required("DisplayName"), Required("RoleCode"), grants);
        var database = services.GetRequiredService<IdentityDatabase>();
        var breach = services.GetRequiredService<LocalBreachCorpus>();
        var password = readPassword();
        var evidence = await breach.CheckAsync(password, CancellationToken.None);
        await using var owner = database.Open(tenant);
        var result = await owner.ProvisionLocalAsync(true, input, password, evidence, Guid.NewGuid());
        if (result.Decision != DirectoryDecision.Changed)
        { logger.LogWarning("Local provisioning rejected {Code}", result.Decision); return false; }
        logger.LogInformation("Local provisioning committed {Code}; no HTTP listener was started", "DirectoryProvisioned");
        return true;
    }

    /// <summary>Reads an exact interactive password without terminal echo, command-line arguments or redirected secret files.</summary>
    public static string ReadPassword()
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Provisioning requires an interactive non-echoing terminal.");
        Console.Write("Initial password (input hidden): ");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    var count = value.Length >= 2 && char.IsSurrogatePair(value[value.Length - 2], value[value.Length - 1]) ? 2 : 1;
                    value.Remove(value.Length - count, count);
                }
            }
            else if (key.KeyChar != '\0')
            {
                if (value.Length == 512) throw new InvalidOperationException("Password input exceeds the approved transport bound.");
                value.Append(key.KeyChar);
            }
        }
    }
}
