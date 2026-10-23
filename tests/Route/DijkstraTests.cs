using System.Collections.Immutable;
using Wes.Wcs.FourWay.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class DijkstraTests
{
    internal static PlanRequest Request(string start = "A", string goal = "D")
    {
        var time = DateTimeOffset.Parse("2026-10-23T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return new("decision-1", "task-1", "goal-1", start, goal,
            new("V1", "FW1", "L1", "MV1", "state-10", start, TravelDirection.Forward, 10, ["turn", "lift"],
                [TravelDirection.Forward, TravelDirection.Reverse, TravelDirection.Turn, TravelDirection.Lift],
                "fixture", time, 1, 10, SnapshotQuality.Trusted),
            time, TimeSpan.FromSeconds(10), "fixture", 1, 10, [], []);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void G1_replays_ABD_and_G2_uses_ACD_with_cost_two()
    {
        var g1 = RouteFixtures.G1();
        for (var i = 0; i < 10; i++)
        {
            var shuffled = g1 with { Edges = g1.Edges.Reverse().ToImmutableArray() };
            var result = DijkstraPlanner.Plan(Topology.Create(i % 2 == 0 ? g1 : shuffled), Request());
            Assert.Equal(PlanStatus.Planned, result.Status);
            Assert.Equal<string>(["A", "B", "D"], result.NodeIds);
            Assert.Equal(2, result.Cost.Total);
            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(DijkstraPlanner.Version, result.PlannerVersion);
            Assert.Equal("state-10", result.Input.Vehicle.StateVersion);
        }
        var g2 = g1 with { Edges = g1.Edges.Select(x => x.Id == "BD" ? x with { Cost = new(4) } : x).ToImmutableArray() };
        var second = DijkstraPlanner.Plan(Topology.Create(g2), Request());
        Assert.Equal<string>(["A", "C", "D"], second.NodeIds);
        Assert.Equal(2, second.Cost.Total);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Map_selection_is_exact_and_never_first_record_fallback()
    {
        var g1 = Topology.Create(RouteFixtures.G1());
        var g2 = Topology.Create(RouteFixtures.G1("MV2"));
        Assert.Equal("NoMap", MapSelector.Resolve([], "FW1", "L1").Error);
        Assert.Equal("NoActiveMap", MapSelector.Resolve([new(g1, false)], "FW1", "L1").Error);
        Assert.Equal("AmbiguousActiveMap", MapSelector.Resolve([new(g1, true), new(g2, true)], "FW1", "L1").Error);
        Assert.Same(g1, MapSelector.Resolve([new(g1, false), new(g2, true)], "FW1", "L1", "MV1").Topology);
        Assert.Equal("NoMap", MapSelector.Resolve([new(g1, true)], "ST1", "L1").Error);
        Assert.Equal("NoMap", MapSelector.Resolve([new(g1, true)], "FW1", "L9", "MV1").Error);
        Assert.Equal("UnknownVersion", MapSelector.Resolve([new(g1, true)], "FW1", "L1", "MV9").Error);
        Assert.Equal("AmbiguousVersion", MapSelector.Resolve([new(g1, true), new(g1, false)], "FW1", "L1", "MV1").Error);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Same_node_singleton_disconnected_and_missing_map_are_explicit()
    {
        var g = RouteFixtures.G1();
        var same = DijkstraPlanner.Plan(Topology.Create(g), Request("A", "A"));
        Assert.Equal(PlanStatus.Planned, same.Status);
        Assert.Equal<string>(["A"], same.NodeIds);
        Assert.Empty(same.Edges);
        Assert.Empty(same.Segments);
        Assert.Single(same.Resources);
        Assert.Equal(0, same.Cost.Total);
        var singleton = g with { Nodes = [g.Nodes[0]], Edges = [] };
        Assert.Equal(PlanStatus.Planned, DijkstraPlanner.Plan(Topology.Create(singleton), Request("A", "A")).Status);
        AssertFailure(DijkstraPlanner.Plan(Topology.Create(g with { Edges = [] }), Request()), PlanStatus.NoPath);
        AssertFailure(DijkstraPlanner.Plan(null, Request()), PlanStatus.NoMap);
        AssertFailure(DijkstraPlanner.Plan(Topology.Create(g), Request("A", "X")), PlanStatus.InvalidInput);
        AssertFailure(DijkstraPlanner.Plan(Topology.Create(g), Request("D", "A")), PlanStatus.NoPath);
    }

    [Theory]
    [InlineData("Direction")]
    [InlineData("Load")]
    [InlineData("Capability")]
    [InlineData("Forbidden")]
    [InlineData("Closed")]
    [InlineData("BlockedResource")]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Hard_constraints_cannot_be_offset_by_cheap_cost(string reason)
    {
        var g = RouteFixtures.G1();
        var edges = g.Edges.Select(x => x.From != "A" ? x : reason switch
        {
            "Direction" => x with { Direction = TravelDirection.Reverse, Cost = new(0) },
            "Load" => x with { MaxLoad = 9, Cost = new(0) },
            "Capability" => x with { RequiredCapabilities = ["lift"], Cost = new(0) },
            "Forbidden" => x with { Forbidden = true, Cost = new(0) },
            _ => x
        }).ToImmutableArray();
        var input = Request();
        input = reason switch
        {
            "Direction" => input with { Vehicle = input.Vehicle with { AllowedDirections = [TravelDirection.Forward] } },
            "Capability" => input with { Vehicle = input.Vehicle with { Capabilities = ["turn"] } },
            "Closed" => input with { ClosedEdgeIds = ["AB", "AC"] },
            "BlockedResource" => input with { BlockedResourceIds = ["R1"] },
            _ => input
        };
        var result = DijkstraPlanner.Plan(Topology.Create(g with { Edges = edges }), input);
        AssertFailure(result, PlanStatus.Blocked);
        Assert.Contains(result.Rejections, x => x.Code == reason);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Capability_filter_selects_longer_executable_branch_and_reports_rejections()
    {
        var g = RouteFixtures.G1();
        var draft = g with { Edges = g.Edges.Select(x => x.Id == "AB" ? x with { RequiredCapabilities = ["lift"] } : x).ToImmutableArray() };
        var input = Request();
        var result = DijkstraPlanner.Plan(Topology.Create(draft), input with { Vehicle = input.Vehicle with { Capabilities = ["turn"] } });
        Assert.Equal<string>(["A", "C", "D"], result.NodeIds);
        Assert.Contains(result.Rejections, x => x.Reference == "AB" && x.Code == "Capability");
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Canonical_G2_with_C_capability_banned_uses_ABD_despite_higher_cost()
    {
        var g = RouteFixtures.G1();
        var draft = g with
        {
            Edges = g.Edges.Select(x => x.Id switch
            {
                "BD" => x with { Cost = new(4) },
                "AC" or "CD" => x with { RequiredCapabilities = ["lift"] },
                _ => x
            }).ToImmutableArray()
        };
        var input = Request();
        var result = DijkstraPlanner.Plan(Topology.Create(draft), input with { Vehicle = input.Vehicle with { Capabilities = ["turn"] } });
        Assert.Equal(PlanStatus.Planned, result.Status);
        Assert.Equal<string>(["A", "B", "D"], result.NodeIds);
        Assert.Equal(5, result.Cost.Total);
        Assert.Equal(new[] { "AC", "CD" }, result.Rejections.Where(x => x.Code == "Capability").Select(x => x.Reference));
    }

    [Theory]
    [InlineData("stale", PlanStatus.StaleState)]
    [InlineData("future", PlanStatus.StaleState)]
    [InlineData("sequence", PlanStatus.StaleState)]
    [InlineData("generation", PlanStatus.StaleState)]
    [InlineData("source", PlanStatus.StaleState)]
    [InlineData("unknown", PlanStatus.StaleState)]
    [InlineData("conflict", PlanStatus.StaleState)]
    [InlineData("version", PlanStatus.MapMismatch)]
    [InlineData("pack", PlanStatus.MapMismatch)]
    [InlineData("layer", PlanStatus.MapMismatch)]
    [InlineData("position", PlanStatus.InvalidInput)]
    [InlineData("load", PlanStatus.InvalidInput)]
    [InlineData("algorithm", PlanStatus.Unsupported)]
    [InlineData("unknownCapability", PlanStatus.Unsupported)]
    [InlineData("freshness", PlanStatus.InvalidInput)]
    [InlineData("unknownClosedEdge", PlanStatus.InvalidInput)]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Bad_or_unsupported_snapshot_never_returns_a_candidate(string variant, PlanStatus status)
    {
        var input = Request();
        var v = input.Vehicle;
        input = variant switch
        {
            "stale" => input with { Now = input.Now.AddSeconds(10) },
            "future" => input with { Now = input.Now.AddTicks(-1) },
            "sequence" => input with { Vehicle = v with { Sequence = 9 } },
            "generation" => input with { Vehicle = v with { Generation = 0 } },
            "source" => input with { ExpectedSource = "wrong" },
            "unknown" => input with { Vehicle = v with { Quality = SnapshotQuality.Unknown } },
            "conflict" => input with { Vehicle = v with { Quality = SnapshotQuality.Conflict } },
            "version" => input with { Vehicle = v with { MapVersionId = "MV9" } },
            "pack" => input with { Vehicle = v with { PackId = "ST1" } },
            "layer" => input with { Vehicle = v with { LayerId = "L2" } },
            "position" => input with { Vehicle = v with { PositionNodeId = "C" } },
            "load" => input with { Vehicle = v with { Load = double.NaN } },
            "algorithm" => input with { Algorithm = "A*" },
            "unknownCapability" => input with { Vehicle = v with { Capabilities = ["unknown"] } },
            "freshness" => input with { FreshnessLimit = TimeSpan.Zero },
            _ => input with { ClosedEdgeIds = ["missing"] }
        };
        AssertFailure(DijkstraPlanner.Plan(Topology.Create(RouteFixtures.G1()), input), status);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void FourWay_output_retains_node_edge_resources_costs_and_per_edge_segments()
    {
        var g = RouteFixtures.G1();
        var st = g;
        st = st with { Edges = st.Edges.Select(x => x with { Cost = new(.5, .25, .25) }).ToImmutableArray() };
        var graph = Topology.Create(st);
        var result = DijkstraPlanner.Plan(graph, Request());
        Assert.Equal(graph.Hash, result.MapHash);
        Assert.Equal("MV1", result.MapVersionId);
        Assert.Equal("test-cost", result.CostUnit);
        Assert.Equal(64, result.InputHash.Length);
        Assert.Equal(1, result.Cost.Base);
        Assert.Equal(.5, result.Cost.Congestion);
        Assert.Equal(.5, result.Cost.Energy);
        Assert.Equal(["AB", "BD"], result.Edges.Select(x => x.EdgeId));
        Assert.Equal(5, result.Resources.Length);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.Resources.Select(x => x.Index));
        Assert.All(result.Resources, x => Assert.Equal<string>(["P1"], x.PhysicalConflictIds));
        Assert.Equal(2, result.Segments.Length);
        Assert.Equal<string>(["AB", "BD"], result.Segments.SelectMany(x => x.EdgeIds));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.Segments.SelectMany(x => x.ResourceOccurrenceIndices));
        var fw = DijkstraPlanner.Plan(Topology.Create(g), Request());
        Assert.Equal(2, fw.Segments.Length);
        Assert.Equal<string>(["AB"], fw.Segments[0].EdgeIds);
        Assert.Equal<string>(["BD"], fw.Segments[1].EdgeIds);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Equal_cost_prefers_fewer_hops_then_ordinal_nodes_then_parallel_edge_ids_despite_zero_cycles()
    {
        var g = RouteFixtures.G1();
        var draft = g with
        {
            Edges = [RouteFixtures.Edge("Z", "A", "B", 0), RouteFixtures.Edge("A", "A", "B", 0),
            RouteFixtures.Edge("BA", "B", "A", 0), RouteFixtures.Edge("BB", "B", "B", 0),
            RouteFixtures.Edge("BD", "B", "D", 0), RouteFixtures.Edge("AC", "A", "C", 0),
            RouteFixtures.Edge("CD", "C", "D", 0)]
        };
        var result = DijkstraPlanner.Plan(Topology.Create(draft), Request());
        Assert.Equal<string>(["A", "B", "D"], result.NodeIds);
        Assert.Equal(["A", "BD"], result.Edges.Select(x => x.EdgeId));
        var direct = DijkstraPlanner.Plan(Topology.Create(draft with { Edges = draft.Edges.Add(RouteFixtures.Edge("AD", "A", "D", 0)) }), Request());
        Assert.Equal<string>(["A", "D"], direct.NodeIds);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Accumulated_cost_overflow_is_not_a_success_or_a_fake_disconnection()
    {
        var g = RouteFixtures.G1();
        var edges = g.Edges.Select(x => x with { Cost = new(double.MaxValue) }).ToImmutableArray();
        var result = DijkstraPlanner.Plan(Topology.Create(g with { Edges = edges }), Request());
        AssertFailure(result, PlanStatus.Unsupported);
        Assert.Contains(result.Rejections, x => x.Code == "CostOverflow");
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Cancellation_is_propagated_without_partial_result()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => DijkstraPlanner.Plan(Topology.Create(RouteFixtures.G1()), Request(), source.Token));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Malformed_reference_collections_return_invalid_input_instead_of_throwing()
    {
        var graph = Topology.Create(RouteFixtures.G1());
        var input = Request();
        AssertFailure(DijkstraPlanner.Plan(graph, input with { ClosedEdgeIds = [null!] }), PlanStatus.InvalidInput);
        AssertFailure(DijkstraPlanner.Plan(graph, input with { BlockedResourceIds = [null!] }), PlanStatus.InvalidInput);
        AssertFailure(DijkstraPlanner.Plan(graph, input with { ClosedEdgeIds = default }), PlanStatus.InvalidInput);
        AssertFailure(DijkstraPlanner.Plan(graph, input with { Vehicle = input.Vehicle with { Capabilities = default } }), PlanStatus.InvalidInput);
        AssertFailure(DijkstraPlanner.Plan(graph, input with { Vehicle = input.Vehicle with { AllowedDirections = [] } }), PlanStatus.Unsupported);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Load_and_freshness_boundaries_are_explicit_and_new_generation_resets_sequence_floor()
    {
        var graph = Topology.Create(RouteFixtures.G1());
        var input = Request();
        var result = DijkstraPlanner.Plan(graph, input with
        {
            Now = input.Now.AddSeconds(10).AddTicks(-1),
            Vehicle = input.Vehicle with { Load = 100, Generation = 2, Sequence = 0 }
        });
        Assert.Equal(PlanStatus.Planned, result.Status);
        AssertFailure(DijkstraPlanner.Plan(graph, input with { Vehicle = input.Vehicle with { Load = 100.00001 } }), PlanStatus.Blocked);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Turn_and_lift_are_hard_direction_capabilities_and_FourWay_preserves_every_edge()
    {
        var g = RouteFixtures.G1();
        var linear = g with
        {
            Edges = [
                RouteFixtures.Edge("AB", "A", "B") with { ExecutionSegmentId = "S1", Direction = TravelDirection.Turn, RequiredCapabilities = ["turn"] },
                RouteFixtures.Edge("BC", "B", "C") with { ExecutionSegmentId = "S2", Direction = TravelDirection.Lift, RequiredCapabilities = ["lift"] },
                RouteFixtures.Edge("CD", "C", "D") with { ExecutionSegmentId = "S1" }]
        };
        var graph = Topology.Create(linear);
        var input = Request();
        var result = DijkstraPlanner.Plan(graph, input);
        Assert.Equal(PlanStatus.Planned, result.Status);
        Assert.Equal(new[] { "S1", "S2", "S1" }, result.Segments.Select(x => x.SegmentId));
        Assert.Equal(Enumerable.Range(0, result.Resources.Length), result.Segments.SelectMany(x => x.ResourceOccurrenceIndices));
        var unsupported = input with { Vehicle = input.Vehicle with { AllowedDirections = [TravelDirection.Forward] } };
        AssertFailure(DijkstraPlanner.Plan(graph, unsupported), PlanStatus.Blocked);
        var noLift = input with { Vehicle = input.Vehicle with { Capabilities = ["turn"] } };
        AssertFailure(DijkstraPlanner.Plan(graph, noLift), PlanStatus.Blocked);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Same_version_with_different_hash_is_rejected_even_when_only_one_record_is_active()
    {
        var g = RouteFixtures.G1();
        var changed = g with { Edges = g.Edges.SetItem(0, g.Edges[0] with { Cost = new(2) }) };
        var result = MapSelector.Resolve([new(Topology.Create(g), false), new(Topology.Create(changed), true)], "FW1", "L1");
        Assert.Null(result.Topology);
        Assert.Equal("ConflictingVersionIdentity", result.Error);
    }

    internal static void AssertFailure(RoutePlan result, PlanStatus expected)
    {
        Assert.Equal(expected, result.Status);
        Assert.Equal(0, result.CandidateCount);
        Assert.Empty(result.NodeIds);
        Assert.Empty(result.Edges);
        Assert.Empty(result.Resources);
        Assert.Empty(result.Segments);
        Assert.NotEmpty(result.Rejections);
    }
}
