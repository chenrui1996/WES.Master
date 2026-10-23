using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wes.Wcs.Stacker.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class StackerOracleTests
{
    private sealed record OraclePath(double Cost, string[] Nodes, RouteEdge[] Edges);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [Trait("CaseId", "DTC-ROUTE-013")]
    public void Private_Stacker_graphs_match_independent_exhaustive_Floyd_and_segment_invariants(int seed)
    {
        var random = new Random(seed);
        for (var trial = 0; trial < 20; trial++)
        {
            var g = Generate(random, trial);
            var graph = Topology.Create(g);
            var reversed = Topology.Create(g with
            {
                Nodes = g.Nodes.Reverse().ToImmutableArray(),
                Edges = g.Edges.Reverse().ToImmutableArray(),
                Capabilities = g.Capabilities.Reverse().ToImmutableArray()
            });
            Assert.Equal(graph.Hash, reversed.Hash);
            var basis = StackerRouteTests.Request();
            basis = basis with
            {
                Device = basis.Device with
                {
                    Load = random.Next(101),
                    Capabilities = random.Next(2) == 0 ? ["deep", "lift"] : ["lift"],
                    AllowedDirections = random.Next(2) == 0 ? [TravelDirection.Forward] : [TravelDirection.Forward, TravelDirection.Reverse]
                },
                ClosedEdgeIds = g.Edges.Where(_ => random.Next(12) == 0).Select(x => x.Id).ToImmutableArray(),
                BlockedResourceIds = random.Next(10) == 0 ? ["R1"] : []
            };
            foreach (var (from, to) in g.Nodes.SelectMany(from => g.Nodes.Select(to => (from.Id, to.Id))))
            {
                var input = basis with { Start = from, Goal = to, Device = basis.Device with { PositionNodeId = from } };
                var expected = Exhaustive(g, input);
                var result = DijkstraPlanner.Plan(graph, input);
                if (!Matches(result, expected) || (expected?.Cost ?? double.PositiveInfinity) != Floyd(g, input))
                {
                    SaveFailure(g, input, seed, trial);
                    Assert.Fail($"Stacker seed={seed}, trial={trial}, {from}->{to}: minimized replay saved.");
                }
                var replay = DijkstraPlanner.Plan(reversed, input);
                Assert.Equal<string>(result.NodeIds, replay.NodeIds);
                Assert.Equal(result.InputHash, replay.InputHash);
                Assert.Equal<PlanRejection>(result.Rejections, replay.Rejections);
                Assert.Equal(result.Edges.Select(x => x.EdgeId), replay.Edges.Select(x => x.EdgeId));
                if (expected is null)
                {
                    Assert.Empty(result.Segments);
                    Assert.Empty(result.Resources);
                    Assert.Equal(0, result.CandidateCount);
                    continue;
                }
                Assert.Equal(result.NodeIds.Length, result.NodeIds.Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(result.Edges.Select(x => x.EdgeId), result.Segments.SelectMany(x => x.EdgeIds));
                // Same-node candidates retain starting occupancy but no meaningless executable segment.
                if (result.Edges.IsEmpty)
                {
                    Assert.Empty(result.Segments);
                    Assert.Equal(g.Nodes.Single(x => x.Id == from).ResourceIds.Length, result.Resources.Length);
                }
                else
                {
                    Assert.Equal(Enumerable.Range(0, result.Resources.Length), result.Segments.SelectMany(x => x.ResourceOccurrenceIndices));
                    Assert.Equal(g.Nodes.Single(x => x.Id == from).ResourceIds.Length + expected.Edges.Sum(edge =>
                        edge.ResourceIds.Length + g.Nodes.Single(node => node.Id == edge.To).ResourceIds.Length), result.Resources.Length);
                    foreach (var (segment, edgeId) in result.Segments.SelectMany(segment => segment.EdgeIds.Select(edgeId => (segment, edgeId))))
                    {
                        var edge = g.Edges.Single(x => x.Id == edgeId);
                        Assert.Equal(edge.ExeStackCode, segment.ExeStackCode);
                        Assert.Equal(edge.DeviceCode, segment.DeviceCode);
                        Assert.Equal(input.Device.DeviceId, edge.DeviceCode);
                        Assert.Equal(edge.CommandMapping, segment.CommandMapping);
                    }
                    for (var i = 1; i < result.Segments.Length; i++)
                    {
                        var a = result.Segments[i - 1];
                        var b = result.Segments[i];
                        Assert.True(a.ExeStackCode != b.ExeStackCode || a.DeviceCode != b.DeviceCode || a.CommandMapping != b.CommandMapping);
                    }
                }
            }
        }
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-013")]
    public void Independent_Stacker_oracles_match_hand_calculated_G1_G2_and_zero_cycles()
    {
        var g = StackerRouteTests.G1();
        var input = StackerRouteTests.Request();
        Assert.Equal(new[] { "A", "B", "D" }, Exhaustive(g, input)!.Nodes);
        Assert.Equal(2, Floyd(g, input));
        var g2 = g with { Edges = g.Edges.Select(x => x.Id == "BD" ? x with { Cost = new(4) } : x).ToImmutableArray() };
        Assert.Equal(new[] { "A", "C", "D" }, Exhaustive(g2, input)!.Nodes);
        Assert.Equal(2, Floyd(g2, input));
        var zero = g with
        {
            Edges = g.Edges.Select(x => x with { Cost = new(0) }).ToImmutableArray()
                .Add(StackerRouteTests.Edge("BA", "B", "A", 0)).Add(StackerRouteTests.Edge("AA", "A", "A", 0))
        };
        Assert.Equal(new[] { "A", "B", "D" }, Exhaustive(zero, input)!.Nodes);
        Assert.Equal(0, Floyd(zero, input));
        Assert.Equal<string>(["A", "B", "D"], DijkstraPlanner.Plan(Topology.Create(zero), input).NodeIds);
        Assert.Null(Exhaustive(g with { Edges = [] }, input));
        Assert.Equal(double.PositiveInfinity, Floyd(g with { Edges = [] }, input));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-013")]
    public void Stacker_counterexample_shrinker_retains_one_edge_minimal_reproducible_failure()
    {
        var g = StackerRouteTests.G1() with
        {
            Nodes = StackerRouteTests.G1().Nodes.Select(x => x with { IsDevicePoint = false }).ToImmutableArray()
        };
        static bool SyntheticFailure(TopologyDefinition candidate) => candidate.Edges.Any(x => x.Id == "AB");
        var minimal = Shrink(g, SyntheticFailure);
        Assert.Equal("AB", Assert.Single(minimal.Edges).Id);
        Assert.False(SyntheticFailure(minimal with { Edges = [] }));
        Assert.Equal(Topology.Create(minimal).Hash, Topology.Create(Shrink(g, SyntheticFailure)).Hash);
    }

    private static TopologyDefinition Generate(Random random, int trial)
    {
        var g = StackerRouteTests.G1();
        var nodes = Enumerable.Range(0, random.Next(2, 7))
            .Select(i => new RouteNode(((char)('A' + i)).ToString(), "ST1", "AISLE1", false, ["R1"])).ToImmutableArray();
        var edges = new List<RouteEdge>();
        foreach (var (a, b) in nodes.SelectMany(a => nodes.Select(b => (a.Id, b.Id))))
        {
            if (random.Next(3) != 0) continue;
            var edge = StackerRouteTests.Edge($"E{edges.Count:D3}", a, b, trial % 4 == 0 ? 0 : random.Next(5),
                random.Next(2) == 0 ? "S1" : "S2", random.Next(2) == 0 ? "CRANE1" : "CRANE2") with
            {
                Direction = random.Next(2) == 0 ? TravelDirection.Forward : TravelDirection.Reverse,
                MaxLoad = random.Next(2) == 0 ? 50 : 100,
                RequiredCapabilities = random.Next(4) == 0 ? ["deep"] : [],
                CommandMapping = random.Next(4) == 0 ? "transfer" : "travel",
                Forbidden = random.Next(15) == 0
            };
            edges.Add(edge);
            if (random.Next(8) == 0) edges.Add(edge with { Id = $"E{edges.Count:D3}" });
        }
        return g with { Nodes = nodes, Edges = edges.ToImmutableArray() };
    }

    // Test-only references deliberately enumerate paths / relax a matrix, never call production
    // validation, filtering, tie comparison or routing to compute expected results.
    private static bool Allowed(TopologyDefinition map, RouteEdge edge, PlanRequest input) =>
        edge.DeviceCode == input.Device.DeviceId
        && !edge.Forbidden && !input.ClosedEdgeIds.Contains(edge.Id) && input.Device.Load <= edge.MaxLoad
        && input.Device.AllowedDirections.Contains(edge.Direction)
        && edge.RequiredCapabilities.All(input.Device.Capabilities.Contains)
        && !edge.ResourceIds.Any(input.BlockedResourceIds.Contains)
        && !map.Nodes.Where(x => x.Id == edge.From || x.Id == edge.To).SelectMany(x => x.ResourceIds).Any(input.BlockedResourceIds.Contains);

    private static OraclePath? Exhaustive(TopologyDefinition map, PlanRequest input)
    {
        if (map.Nodes.Single(x => x.Id == input.Start).ResourceIds.Any(input.BlockedResourceIds.Contains)) return null;
        OraclePath? best = null;
        var nodes = new List<string> { input.Start };
        var path = new List<RouteEdge>();
        void Enumerate(string at, double cost)
        {
            if (at == input.Goal)
            {
                var candidate = new OraclePath(cost, nodes.ToArray(), path.ToArray());
                if (best is null || Compare(candidate, best) < 0) best = candidate;
                return;
            }
            foreach (var edge in map.Edges)
            {
                if (edge.From != at || nodes.Contains(edge.To) || !Allowed(map, edge, input)) continue;
                path.Add(edge);
                nodes.Add(edge.To);
                Enumerate(edge.To, cost + edge.Cost.Base + edge.Cost.Congestion + edge.Cost.Energy);
                nodes.RemoveAt(nodes.Count - 1);
                path.RemoveAt(path.Count - 1);
            }
        }
        Enumerate(input.Start, 0);
        return best;
    }

    private static int Compare(OraclePath a, OraclePath b)
    {
        var result = a.Cost.CompareTo(b.Cost);
        if (result != 0) return result;
        result = a.Edges.Length.CompareTo(b.Edges.Length);
        if (result != 0) return result;
        for (var i = 0; i < a.Nodes.Length; i++)
        {
            result = string.CompareOrdinal(a.Nodes[i], b.Nodes[i]);
            if (result != 0) return result;
        }
        for (var i = 0; i < a.Edges.Length; i++)
        {
            result = string.CompareOrdinal(a.Edges[i].Id, b.Edges[i].Id);
            if (result != 0) return result;
        }
        return 0;
    }

    private static double Floyd(TopologyDefinition map, PlanRequest input)
    {
        var names = map.Nodes.Select(x => x.Id).ToArray();
        var matrix = new double[names.Length, names.Length];
        for (var i = 0; i < names.Length; i++)
            for (var j = 0; j < names.Length; j++)
                matrix[i, j] = i == j && !map.Nodes[i].ResourceIds.Any(input.BlockedResourceIds.Contains) ? 0 : double.PositiveInfinity;
        foreach (var edge in map.Edges)
            if (Allowed(map, edge, input))
            {
                var i = Array.IndexOf(names, edge.From);
                var j = Array.IndexOf(names, edge.To);
                matrix[i, j] = Math.Min(matrix[i, j], edge.Cost.Base + edge.Cost.Congestion + edge.Cost.Energy);
            }
        for (var k = 0; k < names.Length; k++)
            for (var i = 0; i < names.Length; i++)
                for (var j = 0; j < names.Length; j++)
                    matrix[i, j] = Math.Min(matrix[i, j], matrix[i, k] + matrix[k, j]);
        return matrix[Array.IndexOf(names, input.Start), Array.IndexOf(names, input.Goal)];
    }

    private static bool Matches(RoutePlan result, OraclePath? expected) => expected is null
        ? result.Status is PlanStatus.NoPath or PlanStatus.Blocked
        : result.Status == PlanStatus.Planned && result.Cost.Total == expected.Cost
            && result.NodeIds.SequenceEqual(expected.Nodes) && result.Edges.Select(x => x.EdgeId).SequenceEqual(expected.Edges.Select(x => x.Id));

    private static void SaveFailure(TopologyDefinition draft, PlanRequest input, int seed, int trial)
    {
        PlanRequest Restrict(TopologyDefinition candidate) => input with
        {
            ClosedEdgeIds = input.ClosedEdgeIds.Where(id => candidate.Edges.Any(x => x.Id == id)).ToImmutableArray()
        };
        bool Fails(TopologyDefinition candidate)
        {
            var request = Restrict(candidate);
            var expected = Exhaustive(candidate, request);
            return !Matches(DijkstraPlanner.Plan(Topology.Create(candidate), request), expected)
                || (expected?.Cost ?? double.PositiveInfinity) != Floyd(candidate, request);
        }
        var minimal = Shrink(draft, Fails);
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Counterexamples"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"stacker-seed-{seed}-trial-{trial}-{input.Start}-{input.Goal}.json"),
            JsonSerializer.Serialize(new
            {
                Seed = seed,
                Trial = trial,
                Planner = DijkstraPlanner.Version,
                MapHash = Topology.Create(minimal).Hash,
                Map = minimal,
                Input = Restrict(minimal),
                Expected = Exhaustive(minimal, Restrict(minimal)),
                Actual = DijkstraPlanner.Plan(Topology.Create(minimal), Restrict(minimal)),
                Floyd = Floyd(minimal, Restrict(minimal))
            }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    private static TopologyDefinition Shrink(TopologyDefinition draft, Func<TopologyDefinition, bool> fails)
    {
        if (!fails(draft)) throw new ArgumentException("Shrinking requires a reproducing failure.", nameof(fails));
        var minimal = draft;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in minimal.Edges.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var candidate = minimal with { Edges = minimal.Edges.Where(x => x.Id != edge.Id).ToImmutableArray() };
                if (!fails(candidate)) continue;
                minimal = candidate;
                changed = true;
                break;
            }
        }
        return minimal;
    }
}
