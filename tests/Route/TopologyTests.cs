using System.Collections.Immutable;
using Wes.Wcs.FourWay.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class TopologyTests
{
    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Topology_and_planner_are_owned_by_the_FourWay_private_assembly_not_a_shared_route_module()
    {
        Assert.Equal("Wes.Wcs.FourWay.Route", typeof(Topology).Assembly.GetName().Name);
        Assert.Equal("Wes.Wcs.FourWay.Routing", typeof(Topology).Namespace);
        Assert.Same(typeof(Topology).Assembly, typeof(PhysicalConflictMap).Assembly);
        Assert.Same(typeof(Topology).Assembly, typeof(DijkstraPlanner).Assembly);
        Assert.DoesNotContain(typeof(Topology).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is not null && reference.Name.StartsWith("Wes.", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Valid_artifact_is_immutable_and_hash_is_order_independent_but_content_sensitive()
    {
        var draft = RouteFixtures.G1();
        var graph = Topology.Create(draft);
        var reordered = draft with
        {
            Nodes = draft.Nodes.Reverse().ToImmutableArray(),
            Edges = draft.Edges.Reverse().ToImmutableArray(),
            Capabilities = draft.Capabilities.Reverse().ToImmutableArray()
        };
        Assert.Equal(graph.Hash, Topology.Create(reordered).Hash);
        Assert.Equal(64, graph.Hash.Length);
        Assert.NotEqual(graph.Hash, Topology.Create(draft with { VersionId = "MV2" }).Hash);
        Assert.NotEqual(graph.Hash, Topology.Create(draft with
        {
            Edges = draft.Edges.SetItem(0, draft.Edges[0] with { CommandMapping = "other" })
        }).Hash);
        Assert.Equal("move", graph.Definition.Edges.Single(x => x.Id == "AB").CommandMapping);
    }

    public static IEnumerable<object[]> InvalidDrafts()
    {
        var g = RouteFixtures.G1();
        yield return [g with { Nodes = g.Nodes.Add(g.Nodes[0]) }, "DuplicateId"];
        yield return [g with { Edges = g.Edges.Add(g.Edges[0]) }, "DuplicateId"];
        yield return [g with { Resources = g.Resources.Add(g.Resources[0]) }, "DuplicateId"];
        yield return [g with { PhysicalDomains = g.PhysicalDomains.Add(g.PhysicalDomains[0]) }, "DuplicateId"];
        yield return [g with { VersionId = "" }, "Required"];
        yield return [g with { PackKind = (PackKind)99 }, "UnsupportedPack"];
        yield return [g with { PackKind = PackKind.Stacker }, "UnsupportedPack"];
        yield return [g with { Nodes = [] }, "EmptyGraph"];
        yield return [g with { Capabilities = default }, "MissingCollection"];
        yield return [g with { Edges = default }, "MissingCollection"];
        yield return [g with { Resources = default }, "MissingCollection"];
        yield return [g with { PhysicalDomains = default }, "MissingCollection"];
        yield return [g with { Nodes = [null!] }, "NullElement"];
        yield return [g with { Nodes = g.Nodes.SetItem(0, g.Nodes[0] with { LayerId = "L2" }) }, "WrongLayer"];
        yield return [g with { Nodes = g.Nodes.SetItem(0, g.Nodes[0] with { PackId = "ST1" }) }, "WrongPack"];
        yield return [g with { Nodes = g.Nodes.Add(new("X", "FW1", "L1", true, ["R1"])) }, "IsolatedDevice"];
        yield return [g with { Resources = g.Resources.SetItem(0, g.Resources[0] with { PhysicalConflictIds = ["unknown"] }) }, "UnknownPhysicalConflict"];
        yield return [g with { Resources = g.Resources.SetItem(0, g.Resources[0] with { PhysicalConflictIds = [] }) }, "MissingPhysicalMapping"];
        yield return [g with { PhysicalDomains = g.PhysicalDomains.SetItem(0, g.PhysicalDomains[0] with { Capacity = 0 }) }, "InvalidCapacity"];
        yield return [g with { Resources = g.Resources.SetItem(0, g.Resources[0] with { Capacity = 0 }) }, "InvalidCapacity"];
        yield return [g with { Resources = g.Resources.SetItem(0, g.Resources[0] with { Capacity = 2 }) }, "CapacityExceedsPhysical"];
        yield return [g with { Resources = g.Resources.SetItem(0, g.Resources[0] with { PhysicalConflictIds = ["P1", "P1"] }) }, "DuplicateId"];
        yield return [g with { Nodes = g.Nodes.SetItem(0, g.Nodes[0] with { ResourceIds = ["unknown"] }) }, "UnknownResource"];
        foreach (var cost in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(cost) }) }, "InvalidCost"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(double.MaxValue, double.MaxValue) }) }, "InvalidCost"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Capacity = 0 }) }, "InvalidCapacity"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Capacity = 2 }) }, "CapacityExceedsResource"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { MaxLoad = double.NaN }) }, "InvalidLoad"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { To = "X" }) }, "MissingEndpoint"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Direction = (TravelDirection)77 }) }, "InvalidDirection"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { ResourceIds = ["unknown"] }) }, "UnknownResource"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { RequiredCapabilities = ["unknown"] }) }, "UnknownCapability"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { RequiredCapabilities = default }) }, "MissingCollection"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(0, -1) }) }, "InvalidCost"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { CommandMapping = "" }) }, "UnmappedCommand"];
        yield return [g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { ExecutionSegmentId = "" }) }, "UnmappedSegment"];
    }

    [Theory]
    [MemberData(nameof(InvalidDrafts))]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Invalid_artifact_is_rejected_with_reference_chain(TopologyDefinition draft, string code)
    {
        var exception = Assert.Throws<TopologyValidationException>(() => Topology.Create(draft));
        Assert.Contains(exception.Issues, x => x.Code == code && !string.IsNullOrWhiteSpace(x.Reference));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Hash_covers_constraint_and_identity_fields_and_is_culture_independent()
    {
        var g = RouteFixtures.G1();
        var hash = Topology.Create(g).Hash;
        var variants = new[]
        {
            g with { CostUnit = "other-unit" },
            g with { CoordinateSystem = "other-frame" },
            g with { Capabilities = g.Capabilities.Add("other") },
            g with { PhysicalDomains = [g.PhysicalDomains[0] with { GeometryRevision = "2" }] },
            g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { MaxLoad = 5 }) },
            g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Direction = TravelDirection.Reverse }) },
            g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { RequiredCapabilities = ["lift"] }) },
            g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Forbidden = true }) },
            g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(0, 1) }) }
        };
        Assert.All(variants, draft => Assert.NotEqual(hash, Topology.Create(draft).Hash));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(hash, Topology.Create(g).Hash);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Published_artifact_is_not_changed_by_original_array_or_new_draft_edits()
    {
        var input = RouteFixtures.G1();
        var source = input.Edges.ToArray();
        var draft = input with { Edges = ImmutableArray.CreateRange(source) };
        var published = Topology.Create(draft);
        var hash = published.Hash;
        source[0] = source[0] with { Cost = new(99) };
        _ = draft with { Edges = draft.Edges.SetItem(0, draft.Edges[0] with { Capacity = 99 }) };
        Assert.Equal(hash, published.Hash);
        Assert.Equal(1, published.Definition.Edges.Single(x => x.Id == "AB").Cost.Total);
    }
}
