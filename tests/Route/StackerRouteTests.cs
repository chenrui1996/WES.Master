using System.Collections.Immutable;
using Wes.Wcs.Stacker.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class StackerRouteTests
{
    internal static RouteEdge Edge(string id, string from, string to, double cost = 1, string stack = "S1", string device = "CRANE1") =>
        new(id, from, to, new(cost), 1, TravelDirection.Forward, 100, [], ["R1"], stack, device, "travel");

    internal static TopologyDefinition G1() => new("MV1", "ST1", "AISLE1", "local-mm", "test-cost", ["deep", "lift"],
        new[] { "A", "B", "C", "D" }.Select(x => new RouteNode(x, "ST1", "AISLE1", true, ["R1"])).ToImmutableArray(),
        [Edge("AB", "A", "B"), Edge("BD", "B", "D", stack: "S2"), Edge("AC", "A", "C"), Edge("CD", "C", "D", stack: "S2")],
        [new("R1", ResourceKind.Corridor, 1, ["P1"])], [new("P1", 1, false, "g1", "CRANE1")], ["CRANE1", "CRANE2"]);

    internal static PlanRequest Request(string start = "A", string goal = "D")
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return new("decision-1", "task-1", "leg-1", "goal-1", start, goal,
            new("CRANE1", "ST1", "AISLE1", "MV1", "state-10", start, TravelDirection.Forward, 10, ["deep", "lift"],
                [TravelDirection.Forward, TravelDirection.Reverse, TravelDirection.Lift], "fixture", now, 1, 10, SnapshotQuality.Trusted),
            now, TimeSpan.FromSeconds(10), "fixture", 1, 10, [], []);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-002")]
    public void Stacker_consumes_its_own_validated_graph_and_snapshot_for_G1_G2_and_forbidden_C()
    {
        var draft = G1();
        var graph = Topology.Create(draft);
        for (var i = 0; i < 10; i++)
        {
            var result = DijkstraPlanner.Plan(graph, Request());
            Assert.Equal(PlanStatus.Planned, result.Status);
            Assert.Equal<string>(["A", "B", "D"], result.NodeIds);
            Assert.Equal(2, result.Cost.Total);
            Assert.Equal(graph.Hash, result.MapHash);
            Assert.Equal("leg-1", result.Input.LegId);
        }
        var g2 = draft with { Edges = draft.Edges.Select(x => x.Id == "BD" ? x with { Cost = new(4) } : x).ToImmutableArray() };
        Assert.Equal<string>(["A", "C", "D"], DijkstraPlanner.Plan(Topology.Create(g2), Request()).NodeIds);
        var forbidden = g2 with { Edges = g2.Edges.Select(x => x.Id == "AC" ? x with { RequiredCapabilities = ["deep"] } : x).ToImmutableArray() };
        var request = Request();
        var filtered = DijkstraPlanner.Plan(Topology.Create(forbidden), request with { Device = request.Device with { Capabilities = ["lift"] } });
        Assert.Equal<string>(["A", "B", "D"], filtered.NodeIds);
        Assert.Equal(5, filtered.Cost.Total);
        Assert.Contains(filtered.Rejections, x => x.Code == "Capability" && x.Reference == "AC");
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void ExeStack_grouping_preserves_contiguous_edges_resource_occurrences_and_device_boundaries()
    {
        var g = G1();
        var linear = g with { Edges = [Edge("AB", "A", "B"), Edge("BC", "B", "C"), Edge("CD", "C", "D", stack: "S2")] };
        var result = DijkstraPlanner.Plan(Topology.Create(linear), Request());
        Assert.Equal(2, result.Segments.Length);
        Assert.Equal<string>(["AB", "BC"], result.Segments[0].EdgeIds);
        Assert.Equal("S1", result.Segments[0].ExeStackCode);
        Assert.Equal("CRANE1", result.Segments[0].DeviceCode);
        Assert.Equal<string>(["CD"], result.Segments[1].EdgeIds);
        Assert.Equal(7, result.Resources.Length);
        Assert.Equal(Enumerable.Range(0, 7), result.Segments.SelectMany(x => x.ResourceOccurrenceIndices));
        Assert.All(result.Resources, x => Assert.Equal<string>(["P1"], x.PhysicalConflictIds));
        var changedDevice = linear with { Edges = linear.Edges.SetItem(1, linear.Edges[1] with { DeviceCode = "CRANE2" }) };
        StackerValidationTests.AssertFailure(DijkstraPlanner.Plan(Topology.Create(changedDevice), Request()), PlanStatus.Blocked);
        var returning = linear with
        {
            Edges = linear.Edges.SetItem(1, linear.Edges[1] with { ExeStackCode = "S2" })
                .SetItem(2, linear.Edges[2] with { ExeStackCode = "S1" })
        };
        Assert.Equal(new[] { "S1", "S2", "S1" }, DijkstraPlanner.Plan(Topology.Create(returning), Request()).Segments.Select(x => x.ExeStackCode));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Other_device_edges_are_rejected_while_a_more_expensive_owned_path_remains_feasible()
    {
        var g = G1();
        var draft = g with
        {
            Edges = g.Edges.Select(edge => edge.Id switch
            {
                "AB" or "BD" => edge with { DeviceCode = "CRANE2", Cost = new(0) },
                _ => edge with { Cost = new(4) }
            }).ToImmutableArray()
        };
        var result = DijkstraPlanner.Plan(Topology.Create(draft), Request());
        Assert.Equal(PlanStatus.Planned, result.Status);
        Assert.Equal<string>(["A", "C", "D"], result.NodeIds);
        Assert.Equal(8, result.Cost.Total);
        Assert.All(result.Edges, edge => Assert.Equal("CRANE1", edge.DeviceCode));
        Assert.All(result.Segments, segment => Assert.Equal("CRANE1", segment.DeviceCode));
        Assert.Equal(new[] { "AB", "BD" }, result.Rejections.Where(x => x.Code == "DeviceMismatch").Select(x => x.Reference));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Only_other_device_paths_return_blocked_without_candidate_or_handoff()
    {
        var g = G1();
        var draft = g with { Edges = g.Edges.Select(edge => edge with { DeviceCode = "CRANE2" }).ToImmutableArray() };
        var graph = Topology.Create(draft);
        var result = DijkstraPlanner.Plan(graph, Request());
        StackerValidationTests.AssertFailure(result, PlanStatus.Blocked);
        Assert.Equal(4, result.Rejections.Count(x => x.Code == "DeviceMismatch"));
        var otherRequest = Request();
        var owned = DijkstraPlanner.Plan(graph, otherRequest with { Device = otherRequest.Device with { DeviceId = "CRANE2" } });
        Assert.Equal(PlanStatus.Planned, owned.Status);
        Assert.All(owned.Edges, edge => Assert.Equal("CRANE2", edge.DeviceCode));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-001")]
    public void Stacker_owns_models_and_cannot_reference_FourWay_or_a_shared_route_assembly()
    {
        Assert.Equal("Wes.Wcs.Stacker.Route", typeof(Topology).Assembly.GetName().Name);
        Assert.Equal("Wes.Wcs.Stacker.Routing", typeof(Topology).Namespace);
        Assert.DoesNotContain(typeof(Topology).Assembly.GetReferencedAssemblies(), x => x.Name!.StartsWith("Wes.", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Same_ExeStack_but_different_command_starts_a_new_segment_and_retains_cost_breakdown()
    {
        var g = G1();
        var linear = g with
        {
            Edges = [
                Edge("AB", "A", "B") with { Cost = new(.5, .25, .25) },
                Edge("BC", "B", "C") with { Cost = new(.5, .25, .25), CommandMapping = "transfer" },
                Edge("CD", "C", "D") with { Cost = new(.5, .25, .25), CommandMapping = "transfer" }]
        };
        var result = DijkstraPlanner.Plan(Topology.Create(linear), Request());
        Assert.Equal(2, result.Segments.Length);
        Assert.Equal<string>(["BC", "CD"], result.Segments[1].EdgeIds);
        Assert.Equal("transfer", result.Segments[1].CommandMapping);
        Assert.Equal(1.5, result.Cost.Base);
        Assert.Equal(.75, result.Cost.Congestion);
        Assert.Equal(.75, result.Cost.Energy);
        Assert.Equal(3, result.Cost.Total);
        Assert.Equal(DijkstraPlanner.Version, result.PlannerVersion);
        Assert.Equal(64, result.InputHash.Length);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Blocked_double_deep_location_group_is_a_hard_projection_not_a_request_to_move_inventory()
    {
        var g = G1();
        var retrieval = g with
        {
            Edges = [Edge("AB", "A", "B"), Edge("BD", "B", "D") with { ResourceIds = ["R1", "DEEP-GROUP"] }],
            Nodes = g.Nodes.Select(x => x with { IsDevicePoint = false }).ToImmutableArray(),
            Resources = g.Resources.Add(new("DEEP-GROUP", ResourceKind.LocationGroup, 1, ["GROUP1"])),
            PhysicalDomains = g.PhysicalDomains.Add(new("GROUP1", 1, false, "double-deep-fixture", "CRANE1"))
        };
        var graph = Topology.Create(retrieval);
        var input = Request();
        var blocked = DijkstraPlanner.Plan(graph, input with { BlockedResourceIds = ["DEEP-GROUP"] });
        StackerValidationTests.AssertFailure(blocked, PlanStatus.Blocked);
        Assert.Contains(blocked.Rejections, x => x.Reference == "BD" && x.Code == "BlockedResource");
        // The Pack supplies this projection after blocker handling; the kernel never invents shallow-bin
        // clearance, creates a transfer task, or treats this software candidate as release evidence.
        var clearProjection = DijkstraPlanner.Plan(graph, input);
        Assert.Equal(PlanStatus.Planned, clearProjection.Status);
        Assert.Contains(clearProjection.Resources, x => x.ResourceId == "DEEP-GROUP" && x.PhysicalConflictIds.Contains("GROUP1"));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Pinned_Stacker_task_consumes_selected_old_artifact_and_does_not_mix_new_active_executor_mapping()
    {
        var oldDraft = G1();
        var old = Topology.Create(oldDraft);
        var next = Topology.Create(oldDraft with
        {
            VersionId = "MV2",
            DeviceCodes = ["CRANE-NEW"],
            Edges = oldDraft.Edges.Select(edge => edge with { DeviceCode = "CRANE-NEW", ExeStackCode = "NEW-STACK" }).ToImmutableArray()
        });
        var input = Request();
        var selected = MapSelector.Resolve([new(old, false), new(next, true)], input.Device.PackId, input.Device.AisleId, input.Device.MapVersionId);
        Assert.Null(selected.Error);
        var candidate = DijkstraPlanner.Plan(selected.Topology, input);
        Assert.Equal(PlanStatus.Planned, candidate.Status);
        Assert.Equal("MV1", candidate.MapVersionId);
        Assert.Equal(old.Hash, candidate.MapHash);
        Assert.Equal(new[] { "S1", "S2" }, candidate.Segments.Select(x => x.ExeStackCode));
        Assert.All(candidate.Segments, segment => Assert.Equal("CRANE1", segment.DeviceCode));
        StackerValidationTests.AssertFailure(DijkstraPlanner.Plan(next, input), PlanStatus.MapMismatch);
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-004")]
    public void Verified_fixture_load_freshness_and_generation_boundaries_are_explicit_not_site_defaults()
    {
        var graph = Topology.Create(G1());
        var input = Request();
        var valid = input with
        {
            Now = input.Now.AddSeconds(10).AddTicks(-1),
            Device = input.Device with { Load = 100, Generation = 2, Sequence = 0 }
        };
        Assert.Equal(PlanStatus.Planned, DijkstraPlanner.Plan(graph, valid).Status);
        StackerValidationTests.AssertFailure(DijkstraPlanner.Plan(graph, valid with { Now = input.Now.AddSeconds(10) }), PlanStatus.StaleState);
        StackerValidationTests.AssertFailure(DijkstraPlanner.Plan(graph, valid with { Device = valid.Device with { Load = 100.000001 } }), PlanStatus.Blocked);
    }
}
