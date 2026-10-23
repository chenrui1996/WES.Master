using System.Collections.Immutable;
using Wes.Wcs.FourWay.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class PhysicalConflictMapTests
{
    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Rewired_connectivity_is_incompatible_despite_unchanged_properties_and_domain_coverage()
    {
        var g = RouteFixtures.G1();
        var rewired = g with
        {
            VersionId = "MV2",
            Edges = g.Edges.SetItem(0, g.Edges[0] with { From = "D", To = "B" })
        };
        var review = PhysicalConflictMap.Review(Topology.Create(g), Topology.Create(rewired), [new(["R1"], ["R1"])]);
        Assert.True(review.CoverageValid);
        Assert.False(review.SemanticallyCompatible);
        Assert.Contains(review.Issues, x => x.Code == "IncompatibleResourceOrExecution");
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Unambiguous_endpoint_physical_identities_allow_legitimate_node_renames()
    {
        var g = RouteFixtures.G1();
        var nodeResources = g.Nodes.Select(x => new RouteResource("N" + x.Id, ResourceKind.Node, 1, ["P" + x.Id])).ToImmutableArray();
        g = g with
        {
            Nodes = g.Nodes.Select(x => x with { ResourceIds = ["N" + x.Id] }).ToImmutableArray(),
            Resources = g.Resources.AddRange(nodeResources),
            PhysicalDomains = g.PhysicalDomains.AddRange(g.Nodes.Select(x => new PhysicalDomain("P" + x.Id, 1, false, "geometry-1", "device-1")))
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

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Duplicate_execution_connectivity_is_not_lost_in_set_comparison()
    {
        var g = RouteFixtures.G1();
        var parallel = g with { Edges = g.Edges.Add(g.Edges[0] with { Id = "AB-parallel" }) };
        var review = PhysicalConflictMap.Review(Topology.Create(g), Topology.Create(parallel), [new(["R1"], ["R1"])]);
        Assert.True(review.CoverageValid);
        Assert.False(review.SemanticallyCompatible);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Ambiguous_endpoint_physical_domains_cannot_certify_a_node_rename()
    {
        var g = RouteFixtures.G1();
        var renamed = g with
        {
            VersionId = "MV2",
            Nodes = g.Nodes.Select(x => x with { Id = "new-" + x.Id }).ToImmutableArray(),
            Edges = g.Edges.Select(x => x with { From = "new-" + x.From, To = "new-" + x.To }).ToImmutableArray()
        };
        var review = PhysicalConflictMap.Review(Topology.Create(g), Topology.Create(renamed), [new(["R1"], ["R1"])]);
        Assert.True(review.CoverageValid);
        Assert.False(review.SemanticallyCompatible);
    }

    private static TopologyDefinition Renamed()
    {
        var g = RouteFixtures.G1("MV2");
        return g with
        {
            Resources = [g.Resources[0] with { Id = "R9" }],
            Nodes = g.Nodes.Select(x => x with { ResourceIds = ["R9"] }).ToImmutableArray(),
            Edges = g.Edges.Select(x => x with { ResourceIds = ["R9"] }).ToImmutableArray()
        };
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Rename_and_opposite_edge_ids_preserve_physical_identity_across_versions()
    {
        var g = RouteFixtures.G1();
        var original = Topology.Create(g with { Edges = g.Edges.Add(RouteFixtures.Edge("BA", "B", "A")) });
        var renamedDraft = Renamed();
        var renamed = Topology.Create(renamedDraft with
        {
            Edges = renamedDraft.Edges.Add(RouteFixtures.Edge("BA", "B", "A") with { ResourceIds = ["R9"] })
        });
        Assert.Equal<string>(["P1"], PhysicalConflictMap.SharedDomains(original, "R1", renamed, "R9"));
        var review = PhysicalConflictMap.Review(original, renamed, [new(["R1"], ["R9"])]);
        Assert.True(review.CoverageValid);
        Assert.True(review.SemanticallyCompatible);
        Assert.Empty(review.Issues);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Split_and_merge_require_exact_physical_union_and_keep_overlap_identity()
    {
        var g = RouteFixtures.G1();
        var merged = g with
        {
            Resources = [g.Resources[0] with { PhysicalConflictIds = ["P1", "P2"] }],
            PhysicalDomains = g.PhysicalDomains.Add(g.PhysicalDomains[0] with { Id = "P2" })
        };
        var split = merged with
        {
            VersionId = "MV2",
            Resources = [new("R8", ResourceKind.Corridor, 1, ["P1"]), new("R9", ResourceKind.Corridor, 1, ["P2"])],
            Nodes = g.Nodes.Select(x => x with { ResourceIds = ["R8", "R9"] }).ToImmutableArray(),
            Edges = g.Edges.Select(x => x with { ResourceIds = ["R8", "R9"] }).ToImmutableArray()
        };
        var oldMap = Topology.Create(merged);
        var newMap = Topology.Create(split);
        Assert.True(PhysicalConflictMap.Review(oldMap, newMap, [new(["R1"], ["R8", "R9"])]).CoverageValid);
        Assert.True(PhysicalConflictMap.Review(newMap, oldMap, [new(["R8", "R9"], ["R1"])]).CoverageValid);
        Assert.Equal<string>(["P1"], PhysicalConflictMap.SharedDomains(oldMap, "R1", newMap, "R8"));
        Assert.Empty(PhysicalConflictMap.SharedDomains(newMap, "R8", newMap, "R9"));
        var missing = PhysicalConflictMap.Review(oldMap, newMap, [new(["R1"], ["R8"])]);
        Assert.False(missing.CoverageValid);
        Assert.Contains(missing.Issues, x => x.Code == "PhysicalCoverageMismatch");
        Assert.Contains(missing.Issues, x => x.Code == "MissingNewResource");
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("geometry")]
    [InlineData("binding")]
    [InlineData("opposing")]
    [InlineData("direction")]
    [InlineData("load")]
    [InlineData("capability")]
    [InlineData("execution")]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Semantic_changes_are_incompatible_even_with_same_physical_ids(string variant)
    {
        var original = Topology.Create(RouteFixtures.G1());
        var draft = Renamed();
        var domain = draft.PhysicalDomains[0];
        draft = variant switch
        {
            "capacity" => draft with { PhysicalDomains = [domain with { Capacity = 2 }] },
            "geometry" => draft with { PhysicalDomains = [domain with { GeometryRevision = "changed" }] },
            "binding" => draft with { PhysicalDomains = [domain with { DeviceBinding = "other-device" }] },
            "opposing" => draft with { PhysicalDomains = [domain with { OpposingSharing = true }] },
            "direction" => draft with { Edges = draft.Edges.SetItem(0, draft.Edges[0] with { Direction = TravelDirection.Reverse }) },
            "load" => draft with { Edges = draft.Edges.SetItem(0, draft.Edges[0] with { MaxLoad = 50 }) },
            "capability" => draft with { Edges = draft.Edges.SetItem(0, draft.Edges[0] with { RequiredCapabilities = ["lift"] }) },
            _ => draft with { Edges = draft.Edges.SetItem(0, draft.Edges[0] with { CommandMapping = "other-command" }) }
        };
        var result = PhysicalConflictMap.Review(original, Topology.Create(draft), [new(["R1"], ["R9"])]);
        Assert.True(result.CoverageValid);
        Assert.False(result.SemanticallyCompatible);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Missing_duplicate_unknown_and_cross_pack_lineage_fail_closed()
    {
        var oldMap = Topology.Create(RouteFixtures.G1());
        var newMap = Topology.Create(Renamed());
        Assert.False(PhysicalConflictMap.Review(oldMap, newMap, []).CoverageValid);
        Assert.False(PhysicalConflictMap.Review(oldMap, newMap, default).CoverageValid);
        Assert.False(PhysicalConflictMap.Review(oldMap, newMap, [new(["bad"], ["R9"])]).CoverageValid);
        Assert.False(PhysicalConflictMap.Review(oldMap, newMap, [new(["R1"], ["R9"]), new(["R1"], ["R9"])]).CoverageValid);
        var g = Renamed();
        var other = Topology.Create(g with { PackId = "FW2", Nodes = g.Nodes.Select(x => x with { PackId = "FW2" }).ToImmutableArray() });
        Assert.False(PhysicalConflictMap.Review(oldMap, other, [new(["R1"], ["R9"])]).CoverageValid);
        Assert.Throws<ArgumentException>(() => PhysicalConflictMap.SharedDomains(oldMap, "R1", other, "R9"));
        Assert.Throws<ArgumentException>(() => PhysicalConflictMap.SharedDomains(oldMap, "unknown", newMap, "R9"));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-003")]
    public void Different_resource_ids_and_roles_share_crossing_physical_domain_without_deduplicating_output()
    {
        var g = RouteFixtures.G1();
        var draft = g with
        {
            Resources = g.Resources.Add(new("X1", ResourceKind.Intersection, 1, ["P1"])),
            Edges = g.Edges.Select(x => x with { ResourceIds = ["R1", "X1"] }).ToImmutableArray()
        };
        var map = Topology.Create(draft);
        Assert.Equal<string>(["P1"], PhysicalConflictMap.SharedDomains(map, "R1", map, "X1"));
        var plan = DijkstraPlanner.Plan(map, DijkstraTests.Request());
        Assert.Equal(new[] { "R1", "R1", "X1", "R1", "R1", "X1", "R1" }, plan.Resources.Select(x => x.ResourceId));
        Assert.All(plan.Resources, occurrence => Assert.Equal<string>(["P1"], occurrence.PhysicalConflictIds));
    }
}
