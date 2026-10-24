using Microsoft.Extensions.Configuration;

namespace Wes.Host.Runtime;

/// <summary>Immutable deployment feature snapshot captured when the Host is composed.</summary>
public sealed class FeatureSwitches
{
    internal FeatureSwitches(FeatureModuleSwitches modules, RuntimeCapabilitySwitches capabilities)
    {
        Modules = modules;
        Capabilities = capabilities;
    }

    /// <summary>Client-entry visibility flags; these never control API registration or authorization.</summary>
    public FeatureModuleSwitches Modules { get; }

    /// <summary>Optional runtime capabilities with an explicitly registered owner.</summary>
    public RuntimeCapabilitySwitches Capabilities { get; }

    /// <summary>Creates the allowlisted public module catalog without exposing capability state.</summary>
    internal FeatureCatalog ToPublicCatalog() => new(
    [
        new("Platform", Modules.Platform),
        new("Wms", Modules.Wms),
        new("Orchestration", Modules.Orchestration),
        new("WcsStacker", Modules.WcsStacker),
        new("WcsFourWay", Modules.WcsFourWay),
        new("Simulator", Modules.Simulator)
    ]);
}

/// <summary>Client-only visibility for known WES modules.</summary>
public sealed class FeatureModuleSwitches
{
    internal FeatureModuleSwitches(
        bool platform,
        bool wms,
        bool orchestration,
        bool wcsStacker,
        bool wcsFourWay,
        bool simulator)
    {
        Platform = platform;
        Wms = wms;
        Orchestration = orchestration;
        WcsStacker = wcsStacker;
        WcsFourWay = wcsFourWay;
        Simulator = simulator;
    }

    /// <summary>Whether the Platform client entry is visible.</summary>
    public bool Platform { get; }

    /// <summary>Whether the WMS client entry is visible.</summary>
    public bool Wms { get; }

    /// <summary>Whether the Orchestration client entry is visible.</summary>
    public bool Orchestration { get; }

    /// <summary>Whether the Stacker WCS client entry is visible.</summary>
    public bool WcsStacker { get; }

    /// <summary>Whether the FourWay WCS client entry is visible.</summary>
    public bool WcsFourWay { get; }

    /// <summary>Whether the Simulator client entry is visible.</summary>
    public bool Simulator { get; }
}

/// <summary>Deployment switches for optional runtime capabilities.</summary>
public sealed class RuntimeCapabilitySwitches
{
    internal RuntimeCapabilitySwitches(
        bool quartz,
        bool signalR,
        bool messageQueue,
        bool outbox,
        bool mail,
        bool objectStorage,
        bool deviceCommunication)
    {
        Quartz = quartz;
        SignalR = signalR;
        MessageQueue = messageQueue;
        Outbox = outbox;
        Mail = mail;
        ObjectStorage = objectStorage;
        DeviceCommunication = deviceCommunication;
    }

    /// <summary>Whether Quartz scheduling is enabled.</summary>
    public bool Quartz { get; }

    /// <summary>Whether SignalR is enabled.</summary>
    public bool SignalR { get; }

    /// <summary>Whether a message queue is enabled.</summary>
    public bool MessageQueue { get; }

    /// <summary>Whether Outbox delivery is enabled.</summary>
    public bool Outbox { get; }

    /// <summary>Whether mail delivery is enabled.</summary>
    public bool Mail { get; }

    /// <summary>Whether object storage is enabled.</summary>
    public bool ObjectStorage { get; }

    /// <summary>Whether device communication is enabled.</summary>
    public bool DeviceCommunication { get; }
}

internal sealed record FeatureCatalog(IReadOnlyList<FeatureCatalogModule> Modules);

internal sealed record FeatureCatalogModule(string Code, bool Enabled);

/// <summary>Reads and validates deployment feature configuration before Host startup.</summary>
public static class FeatureSwitchConfiguration
{
    private static readonly string[] RootNames = ["Modules", "Capabilities"];
    private static readonly string[] ModuleNames =
        ["Platform", "Wms", "Orchestration", "WcsStacker", "WcsFourWay", "Simulator"];
    private static readonly string[] CapabilityNames =
        ["Quartz", "SignalR", "MessageQueue", "Outbox", "Mail", "ObjectStorage", "DeviceCommunication"];

    /// <summary>Creates a validated immutable snapshot and rejects unsupported runtime capabilities.</summary>
    public static FeatureSwitches Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        var root = configuration.GetSection("Features");
        ValidateKeys(root, "Features", RootNames, errors);
        var modules = ReadValues(root.GetSection("Modules"), "Features:Modules", ModuleNames, errors);
        var capabilities = ReadValues(root.GetSection("Capabilities"), "Features:Capabilities", CapabilityNames, errors);

        foreach (var name in CapabilityNames)
        {
            if (capabilities.TryGetValue(name, out var enabled) && enabled)
                errors.Add($"Features:Capabilities:{name}: no runtime owner is registered.");
        }

        if (errors.Count != 0)
            throw new InvalidOperationException($"Invalid Features configuration: {string.Join("; ", errors)}");

        return new FeatureSwitches(
            new FeatureModuleSwitches(
                modules["Platform"],
                modules["Wms"],
                modules["Orchestration"],
                modules["WcsStacker"],
                modules["WcsFourWay"],
                modules["Simulator"]),
            new RuntimeCapabilitySwitches(
                capabilities["Quartz"],
                capabilities["SignalR"],
                capabilities["MessageQueue"],
                capabilities["Outbox"],
                capabilities["Mail"],
                capabilities["ObjectStorage"],
                capabilities["DeviceCommunication"]));
    }

    private static Dictionary<string, bool> ReadValues(
        IConfigurationSection section,
        string path,
        IReadOnlyCollection<string> expectedNames,
        ICollection<string> errors)
    {
        ValidateKeys(section, path, expectedNames, errors);
        var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in expectedNames)
        {
            var matches = section.GetChildren()
                .Where(child => string.Equals(child.Key, name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var keyPath = $"{path}:{name}";
            if (matches.Length == 0)
            {
                errors.Add($"{keyPath}: value is required.");
                continue;
            }
            if (matches.Length > 1)
            {
                errors.Add($"{keyPath}: duplicate key.");
                continue;
            }

            var value = matches[0];
            if (value.GetChildren().Any() || !bool.TryParse(value.Value, out var enabled))
            {
                errors.Add($"{keyPath}: expected a boolean.");
                continue;
            }
            values[name] = enabled;
        }
        return values;
    }

    private static void ValidateKeys(
        IConfigurationSection section,
        string path,
        IReadOnlyCollection<string> expectedNames,
        ICollection<string> errors)
    {
        var children = section.GetChildren().ToArray();
        var duplicates = children.GroupBy(child => child.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicates)
            errors.Add($"{path}:{duplicate.Key}: duplicate key.");
        foreach (var child in children)
        {
            if (!expectedNames.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{path}:{child.Key}: unknown key.");
        }
    }
}
