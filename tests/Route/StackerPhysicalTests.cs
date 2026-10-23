using System.Collections.Immutable;
using Wes.Wcs.Stacker.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class StackerPhysicalTests
{
    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Stacker_renames_preserve_stable_conflicts_for_distinct_opposite_edges()
    {
        var g = StackerRouteTests.G1();
        g = g with { Edges = g.Edges.Add(StackerRouteTests.Edge("BA", "B", "A")) };
        var renamed = g with
        {
            VersionId = "MV2",
            Resources = [g.Resources[0] with { Id = "RENAMED" }],
            Nodes = g.Nodes.Select(x => x with { ResourceIds = ["RENAMED"] }).ToImmutableArray(),
            Edges = g.Edges.Select(x => x with { ResourceIds = ["RENAMED"] }).ToImmutableArray()
        };
        var before = Topology.Create(g);
        var after = Topology.Create(renamed);
        Assert.Equal<string>(["P1"], PhysicalConflictMap.SharedDomains(before, "R1", after, "RENAMED"));
        var review = PhysicalConflictMap.Review(before, after, [new(["R1"], ["RENAMED"])]);
        Assert.True(review.CoverageValid);
        Assert.True(review.SemanticallyCompatible);
        Assert.Empty(review.Issues);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Split_merge_covers_physical_union_and_rejects_missing_coverage()
    {
        var g = StackerRouteTests.G1();
        g = g with
        {
            Resources = [g.Resources[0] with { PhysicalConflictIds = ["P1", "P2"] }],
            PhysicalDomains = g.PhysicalDomains.Add(g.PhysicalDomains[0] with { Id = "P2" })
        };
        var split = g with
        {
            VersionId = "MV2",
            Resources = [new("R8", ResourceKind.Corridor, 1, ["P1"]), new("R9", ResourceKind.Corridor, 1, ["P2"])],
            Nodes = g.Nodes.Select(x => x with { ResourceIds = ["R8", "R9"] }).ToImmutableArray(),
            Edges = g.Edges.Select(x => x with { ResourceIds = ["R8", "R9"] }).ToImmutableArray()
        };
        var before = Topology.Create(g);
        var after = Topology.Create(split);
        Assert.True(PhysicalConflictMap.Review(before, after, [new(["R1"], ["R8", "R9"])]).SemanticallyCompatible);
        Assert.True(PhysicalConflictMap.Review(after, before, [new(["R8", "R9"], ["R1"])]).SemanticallyCompatible);
        var missing = PhysicalConflictMap.Review(before, after, [new(["R1"], ["R8"])]);
        Assert.False(missing.CoverageValid);
        Assert.Contains(missing.Issues, x => x.Code == "PhysicalCoverageMismatch");
        Assert.False(PhysicalConflictMap.Review(before, after, []).CoverageValid);
        Assert.False(PhysicalConflictMap.Review(before, after, default).CoverageValid);
        Assert.False(PhysicalConflictMap.Review(before, after, [new(["R1"], ["bad"])]).CoverageValid);
        Assert.False(PhysicalConflictMap.Review(before, after, [new(["R1", "R1"], ["R8", "R9"])]).CoverageValid);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("exeStack")]
    [InlineData("command")]
    [InlineData("connectivity")]
    [InlineData("parallel")]
    [InlineData("direction")]
    [InlineData("load")]
    [InlineData("capability")]
    [InlineData("geometry")]
    [InlineData("capacity")]
    [InlineData("binding")]
    [InlineData("opposing")]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Stacker_execution_or_physical_changes_are_not_compatible(string variant)
    {
        var g = StackerRouteTests.G1();
        var edge = g.Edges[0];
        var domain = g.PhysicalDomains[0];
        var modified = variant switch
        {
            "device" => g with { Edges = g.Edges.SetItem(0, edge with { DeviceCode = "CRANE2" }) },
            "exeStack" => g with { Edges = g.Edges.SetItem(0, edge with { ExeStackCode = "S9" }) },
            "command" => g with { Edges = g.Edges.SetItem(0, edge with { CommandMapping = "other" }) },
            "connectivity" => g with { Edges = g.Edges.SetItem(0, edge with { To = "C" }) },
            "parallel" => g with { Edges = g.Edges.Add(edge with { Id = "other" }) },
            "direction" => g with { Edges = g.Edges.SetItem(0, edge with { Direction = TravelDirection.Reverse }) },
            "load" => g with { Edges = g.Edges.SetItem(0, edge with { MaxLoad = 20 }) },
            "capability" => g with { Edges = g.Edges.SetItem(0, edge with { RequiredCapabilities = ["deep"] }) },
            "geometry" => g with { PhysicalDomains = [domain with { GeometryRevision = "g2" }] },
            "capacity" => g with { PhysicalDomains = [domain with { Capacity = 2 }] },
            "binding" => g with { PhysicalDomains = [domain with { DeviceBinding = "CRANE2" }] },
            _ => g with { PhysicalDomains = [domain with { OpposingSharing = true }] }
        };
        var review = PhysicalConflictMap.Review(Topology.Create(g), Topology.Create(modified), [new(["R1"], ["R1"])]);
        Assert.True(review.CoverageValid);
        Assert.False(review.SemanticallyCompatible);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Cross_pack_domains_do_not_create_cross_pack_shared_state()
    {
        var g = StackerRouteTests.G1();
        var other = g with { PackId = "ST2", Nodes = g.Nodes.Select(x => x with { PackId = "ST2" }).ToImmutableArray() };
        var left = Topology.Create(g);
        var right = Topology.Create(other);
        Assert.False(PhysicalConflictMap.Review(left, right, [new(["R1"], ["R1"])]).CoverageValid);
        Assert.Throws<ArgumentException>(() => PhysicalConflictMap.SharedDomains(left, "R1", right, "R1"));
        Assert.Throws<ArgumentException>(() => PhysicalConflictMap.SharedDomains(left, "unknown", left, "R1"));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Unique_physical_endpoints_allow_node_renames_but_not_rewired_execution()
    {
        var g = StackerRouteTests.G1();
        g = g with
        {
            Nodes = g.Nodes.Select(x => x with { ResourceIds = ["N" + x.Id] }).ToImmutableArray(),
            Resources = g.Resources.AddRange(g.Nodes.Select(x => new RouteResource("N" + x.Id, ResourceKind.Node, 1, ["P" + x.Id]))),
            PhysicalDomains = g.PhysicalDomains.AddRange(g.Nodes.Select(x => new PhysicalDomain("P" + x.Id, 1, false, "g1", "CRANE1")))
        };
        var renamed = g with
        {
            VersionId = "MV2",
            Nodes = g.Nodes.Select(x => x with { Id = "renamed-" + x.Id }).ToImmutableArray(),
            Edges = g.Edges.Select(x => x with { From = "renamed-" + x.From, To = "renamed-" + x.To }).ToImmutableArray()
        };
        var lineage = g.Resources.Select(x => new ResourceLineage([x.Id], [x.Id])).ToImmutableArray();
        Assert.True(PhysicalConflictMap.Review(Topology.Create(g), Topology.Create(renamed), lineage).SemanticallyCompatible);
        var rewired = renamed with { Edges = renamed.Edges.SetItem(0, renamed.Edges[0] with { To = "renamed-C" }) };
        Assert.False(PhysicalConflictMap.Review(Topology.Create(g), Topology.Create(rewired), lineage).SemanticallyCompatible);
    }
}
