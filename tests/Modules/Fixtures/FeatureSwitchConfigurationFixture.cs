namespace Wes.Modules.Tests.Fixtures;

internal static class FeatureSwitchConfigurationFixture
{
    internal static Dictionary<string, string?> ValidValues() => new()
    {
        ["Features:Modules:Platform"] = "false",
        ["Features:Modules:Wms"] = "false",
        ["Features:Modules:Orchestration"] = "false",
        ["Features:Modules:WcsStacker"] = "false",
        ["Features:Modules:WcsFourWay"] = "false",
        ["Features:Modules:Simulator"] = "false",
        ["Features:Capabilities:Quartz"] = "false",
        ["Features:Capabilities:SignalR"] = "false",
        ["Features:Capabilities:MessageQueue"] = "false",
        ["Features:Capabilities:Outbox"] = "false",
        ["Features:Capabilities:Mail"] = "false",
        ["Features:Capabilities:ObjectStorage"] = "false",
        ["Features:Capabilities:DeviceCommunication"] = "false"
    };
}
