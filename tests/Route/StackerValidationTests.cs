using System.Collections.Immutable;
using Wes.Wcs.Stacker.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class StackerValidationTests
{
    public static IEnumerable<object[]> InvalidArtifacts()
    {
        var g = StackerRouteTests.G1();
        yield return [g with { VersionId = "" }, "Required"];
        yield return [g with { Nodes = [] }, "EmptyGraph"];
        yield return [g with { Nodes = default }, "MissingCollection"];
        yield return [g with { Nodes = [null!] }, "NullElement"];
        yield return [g with { Nodes = g.Nodes.Add(g.Nodes[0]) }, "DuplicateId"];
        yield return [g with { Edges = g.Edges.Add(g.Edges[0]) }, "DuplicateId"];
        yield return [g with { Resources = g.Resources.Add(g.Resources[0]) }, "DuplicateId"];
        yield return [g with { PhysicalDomains = g.PhysicalDomains.Add(g.PhysicalDomains[0]) }, "DuplicateId"];
        yield return [g with { Capabilities = default }, "MissingCollection"];
        yield return [g with { DeviceCodes = default }, "MissingCollection"];
        yield return [g with { DeviceCodes = ["CRANE1", "CRANE1"] }, "DuplicateId"];
        yield return [g with { Edges = default }, "MissingCollection"];
        yield return [g with { Resources = default }, "MissingCollection"];
        yield return [g with { PhysicalDomains = default }, "MissingCollection"];
        yield return [g with { Nodes = g.Nodes.SetItem(0, g.Nodes[0] with { PackId = "FW1" }) }, "WrongPack"];
        yield return [g with { Nodes = g.Nodes.SetItem(0, g.Nodes[0] with { AisleId = "OTHER" }) }, "WrongAisle"];
        yield return [g with { Nodes = g.Nodes.Add(new("X", "ST1", "AISLE1", true, ["R1"])) }, "IsolatedDevice"];
        yield return [g with { Nodes = g.Nodes.SetItem(0, g.Nodes[0] with { ResourceIds = ["bad"] }) }, "UnknownResource"];
        yield return [g with { Resources = [g.Resources[0] with { PhysicalConflictIds = [] }] }, "MissingPhysicalMapping"];
        yield return [g with { Resources = [g.Resources[0] with { PhysicalConflictIds = ["bad"] }] }, "UnknownPhysicalConflict"];
        yield return [g with { Resources = [g.Resources[0] with { PhysicalConflictIds = ["P1", "P1"] }] }, "DuplicateId"];
        yield return [g with { Resources = [g.Resources[0] with { Capacity = 0 }] }, "InvalidCapacity"];
        yield return [g with { Resources = [g.Resources[0] with { Capacity = 2 }] }, "CapacityExceedsPhysical"];
        yield return [g with { PhysicalDomains = [g.PhysicalDomains[0] with { Capacity = 0 }] }, "InvalidCapacity"];
        foreach (var badCost in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(badCost) }) }, "InvalidCost"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(double.MaxValue, double.MaxValue) }) }, "InvalidCost"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(1, -1) }) }, "InvalidCost"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { To = "missing" }) }, "MissingEndpoint"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Capacity = 0 }) }, "InvalidCapacity"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Capacity = 2 }) }, "CapacityExceedsResource"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Direction = (TravelDirection)99 }) }, "InvalidDirection"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { MaxLoad = double.NaN }) }, "InvalidLoad"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { ResourceIds = ["missing"] }) }, "UnknownResource"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { RequiredCapabilities = ["missing"] }) }, "UnknownCapability"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { ExeStackCode = "" }) }, "UnmappedExeStack"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { DeviceCode = "" }) }, "UnmappedDevice"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { DeviceCode = "NOT-MAPPED" }) }, "UnknownDevice"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { CommandMapping = "" }) }, "UnmappedCommand"];
    }

    [Theory]
    [MemberData(nameof(InvalidArtifacts))]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Invalid_Stacker_artifact_is_not_published(TopologyDefinition draft, string code)
    {
        var error = Assert.Throws<TopologyValidationException>(() => Topology.Create(draft));
        Assert.Contains(error.Issues, issue => issue.Code == code && !string.IsNullOrWhiteSpace(issue.Reference));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Version_hash_and_Stacker_device_mapping_are_immutable_and_order_independent()
    {
        var g = StackerRouteTests.G1();
        var original = Topology.Create(g);
        var shuffled = g with
        {
            Nodes = g.Nodes.Reverse().ToImmutableArray(),
            Edges = g.Edges.Reverse().ToImmutableArray(),
            Capabilities = g.Capabilities.Reverse().ToImmutableArray()
        };
        Assert.Equal(original.Hash, Topology.Create(shuffled).Hash);
        Assert.Equal(64, original.Hash.Length);
        Assert.NotEqual(original.Hash, Topology.Create(g with { VersionId = "MV2" }).Hash);
        Assert.NotEqual(original.Hash, Topology.Create(g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { ExeStackCode = "S9" }) }).Hash);
        Assert.NotEqual(original.Hash, Topology.Create(g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { DeviceCode = "CRANE2" }) }).Hash);
        Assert.Equal("S1", original.Definition.Edges.Single(x => x.Id == "AB").ExeStackCode);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Stacker_map_resolution_never_uses_the_first_record_or_silently_replaces_a_pinned_version()
    {
        var g = StackerRouteTests.G1();
        var a = Topology.Create(g);
        var b = Topology.Create(g with { VersionId = "MV2" });
        Assert.Equal("NoMap", MapSelector.Resolve([], "ST1", "AISLE1").Error);
        Assert.Equal("NoActiveMap", MapSelector.Resolve([new(a, false)], "ST1", "AISLE1").Error);
        Assert.Equal("AmbiguousActiveMap", MapSelector.Resolve([new(a, true), new(b, true)], "ST1", "AISLE1").Error);
        Assert.Same(a, MapSelector.Resolve([new(a, false), new(b, true)], "ST1", "AISLE1", "MV1").Topology);
        Assert.Same(b, MapSelector.Resolve([new(a, false), new(b, true)], "ST1", "AISLE1").Topology);
        Assert.Equal("NoMap", MapSelector.Resolve([new(a, true)], "FW1", "AISLE1").Error);
        Assert.Equal("NoMap", MapSelector.Resolve([new(a, true)], "ST1", "OTHER").Error);
        Assert.Equal("UnknownVersion", MapSelector.Resolve([new(a, true)], "ST1", "AISLE1", "MV9").Error);
        Assert.Equal("AmbiguousVersion", MapSelector.Resolve([new(a, true), new(a, false)], "ST1", "AISLE1", "MV1").Error);
        var changed = Topology.Create(g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(2) }) });
        Assert.Equal("ConflictingVersionIdentity", MapSelector.Resolve([new(a, false), new(changed, true)], "ST1", "AISLE1").Error);
    }

    [Theory]
    [InlineData("direction", PlanStatus.Blocked)]
    [InlineData("load", PlanStatus.Blocked)]
    [InlineData("capability", PlanStatus.Blocked)]
    [InlineData("closed", PlanStatus.Blocked)]
    [InlineData("resource", PlanStatus.Blocked)]
    [InlineData("stale", PlanStatus.StaleState)]
    [InlineData("future", PlanStatus.StaleState)]
    [InlineData("source", PlanStatus.StaleState)]
    [InlineData("sequence", PlanStatus.StaleState)]
    [InlineData("generation", PlanStatus.StaleState)]
    [InlineData("unknown", PlanStatus.StaleState)]
    [InlineData("map", PlanStatus.MapMismatch)]
    [InlineData("aisle", PlanStatus.MapMismatch)]
    [InlineData("pack", PlanStatus.MapMismatch)]
    [InlineData("position", PlanStatus.InvalidInput)]
    [InlineData("badload", PlanStatus.InvalidInput)]
    [InlineData("badreference", PlanStatus.InvalidInput)]
    [InlineData("freshness", PlanStatus.InvalidInput)]
    [InlineData("algorithm", PlanStatus.Unsupported)]
    [InlineData("unknownCapability", PlanStatus.Unsupported)]
    [InlineData("unknownDevice", PlanStatus.Unsupported)]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Hard_constraints_and_untrusted_inputs_never_return_Stacker_execution_segments(string variant, PlanStatus status)
    {
        var g = StackerRouteTests.G1();
        var input = StackerRouteTests.Request();
        var device = input.Device;
        input = variant switch
        {
            "direction" => input with { Device = device with { AllowedDirections = [TravelDirection.Reverse] } },
            "load" => input with { Device = device with { Load = 101 } },
            "capability" => input with { Device = device with { Capabilities = [] } },
            "closed" => input with { ClosedEdgeIds = ["AB", "AC"] },
            "resource" => input with { BlockedResourceIds = ["R1"] },
            "stale" => input with { Now = input.Now.AddSeconds(10) },
            "future" => input with { Now = input.Now.AddTicks(-1) },
            "source" => input with { ExpectedSource = "wrong" },
            "sequence" => input with { Device = device with { Sequence = 9 } },
            "generation" => input with { Device = device with { Generation = 0 } },
            "unknown" => input with { Device = device with { Quality = SnapshotQuality.Unknown } },
            "map" => input with { Device = device with { MapVersionId = "MV2" } },
            "aisle" => input with { Device = device with { AisleId = "OTHER" } },
            "pack" => input with { Device = device with { PackId = "ST2" } },
            "position" => input with { Device = device with { PositionNodeId = "D" } },
            "badload" => input with { Device = device with { Load = double.NaN } },
            "badreference" => input with { ClosedEdgeIds = [null!] },
            "freshness" => input with { FreshnessLimit = TimeSpan.Zero },
            "algorithm" => input with { Algorithm = "A*" },
            "unknownDevice" => input with { Device = device with { DeviceId = "NOT-MAPPED" } },
            _ => input with { Device = device with { Capabilities = ["not-supported"] } }
        };
        if (variant == "capability")
            g = g with { Edges = g.Edges.Select(x => x with { RequiredCapabilities = ["deep"] }).ToImmutableArray() };
        AssertFailure(DijkstraPlanner.Plan(Topology.Create(g), input), status);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Empty_singleton_same_node_disconnected_and_overflow_are_explicit_and_cancellable()
    {
        var g = StackerRouteTests.G1();
        AssertFailure(DijkstraPlanner.Plan(null, StackerRouteTests.Request()), PlanStatus.NoMap);
        var disconnected = g with { Nodes = g.Nodes.Select(x => x with { IsDevicePoint = false }).ToImmutableArray(), Edges = [] };
        AssertFailure(DijkstraPlanner.Plan(Topology.Create(disconnected), StackerRouteTests.Request()), PlanStatus.NoPath);
        var singleton = disconnected with { Nodes = [disconnected.Nodes[0]] };
        var same = DijkstraPlanner.Plan(Topology.Create(singleton), StackerRouteTests.Request("A", "A"));
        Assert.Equal(PlanStatus.Planned, same.Status);
        Assert.Equal<string>(["A"], same.NodeIds);
        Assert.Empty(same.Edges);
        Assert.Empty(same.Segments);
        Assert.Single(same.Resources);
        Assert.Equal(0, same.Cost.Total);
        var overflow = g with { Edges = g.Edges.Select(x => x with { Cost = new(double.MaxValue) }).ToImmutableArray() };
        AssertFailure(DijkstraPlanner.Plan(Topology.Create(overflow), StackerRouteTests.Request()), PlanStatus.Unsupported);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DijkstraPlanner.Plan(Topology.Create(g), StackerRouteTests.Request(), cancellation.Token));
    }

    internal static void AssertFailure(RoutePlan result, PlanStatus status)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(0, result.CandidateCount);
        Assert.Empty(result.Edges);
        Assert.Empty(result.NodeIds);
        Assert.Empty(result.Segments);
        Assert.Empty(result.Resources);
        Assert.NotEmpty(result.Rejections);
    }
}
