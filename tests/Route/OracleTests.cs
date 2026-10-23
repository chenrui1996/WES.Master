using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wes.Wcs.FourWay.Routing;
using Xunit;

namespace Wes.Route.Tests;

public sealed class OracleTests
{
    [Fact]
    [Trait("CaseId", "DTC-ROUTE-013")]
    public void Independent_oracles_match_hand_checked_weighted_and_zero_cycle_fixtures()
    {
        var g = RouteFixtures.G1();
        var result = SmallGraphOracle.Exhaustive(g, DijkstraTests.Request());
        Assert.NotNull(result);
        Assert.Equal(new[] { "A", "B", "D" }, result.Nodes);
        Assert.Equal(2, result.Cost);
        Assert.Equal(2, SmallGraphOracle.Floyd(g, DijkstraTests.Request()));
        var g2 = g with { Edges = g.Edges.Select(x => x.Id == "BD" ? x with { Cost = new(4) } : x).ToImmutableArray() };
        Assert.Equal(new[] { "A", "C", "D" }, SmallGraphOracle.Exhaustive(g2, DijkstraTests.Request())!.Nodes);
        var zero = g with
        {
            Edges = g.Edges.Select(x => x with { Cost = new(0) }).ToImmutableArray()
                .Add(RouteFixtures.Edge("BA", "B", "A", 0)).Add(RouteFixtures.Edge("AA", "A", "A", 0))
        };
        Assert.Equal(new[] { "A", "B", "D" }, SmallGraphOracle.Exhaustive(zero, DijkstraTests.Request())!.Nodes);
        Assert.Equal(0, SmallGraphOracle.Floyd(zero, DijkstraTests.Request()));
        Assert.Null(SmallGraphOracle.Exhaustive(g with { Edges = [] }, DijkstraTests.Request()));
        Assert.Equal(double.PositiveInfinity, SmallGraphOracle.Floyd(g with { Edges = [] }, DijkstraTests.Request()));
    }

    [Fact]
    [Trait("CaseId", "DTC-ROUTE-013")]
    public void Counterexample_shrinker_is_deterministic_and_one_edge_minimal()
    {
        var g = RouteFixtures.G1();
        var request = DijkstraTests.Request();
        static bool SyntheticDefect(TopologyDefinition candidate) => candidate.Edges.Any(x => x.Id == "AB");
        var minimal = SmallGraphOracle.Minimize(g, request, SyntheticDefect);
        Assert.Equal("AB", Assert.Single(minimal.Edges).Id);
        Assert.False(SyntheticDefect(minimal with { Edges = [] }));
        Assert.Equal(Topology.Create(minimal).Hash, Topology.Create(SmallGraphOracle.Minimize(g, request, SyntheticDefect)).Hash);
        Assert.Contains(minimal.Nodes, x => x.Id == request.Start);
        Assert.Contains(minimal.Nodes, x => x.Id == request.Goal);
    }

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
    public void Seeded_small_graphs_match_exhaustive_and_floyd_and_replay_all_pairs(int seed)
    {
        var random = new Random(seed);
        for (var trial = 0; trial < 20; trial++)
        {
            var draft = Generate(random, trial);
            var graph = Topology.Create(draft);
            var reordered = Topology.Create(draft with
            {
                Nodes = draft.Nodes.Reverse().ToImmutableArray(),
                Edges = draft.Edges.Reverse().ToImmutableArray(),
                Resources = draft.Resources.Reverse().ToImmutableArray()
            });
            Assert.Equal(graph.Hash, reordered.Hash);
            var basis = DijkstraTests.Request();
            basis = basis with
            {
                Vehicle = basis.Vehicle with
                {
                    Load = random.Next(0, 101),
                    Capabilities = random.Next(2) == 0 ? ["turn", "lift"] : ["turn"],
                    AllowedDirections = random.Next(2) == 0 ? [TravelDirection.Forward] : [TravelDirection.Forward, TravelDirection.Reverse]
                },
                ClosedEdgeIds = draft.Edges.Where(_ => random.Next(15) == 0).Select(x => x.Id).ToImmutableArray(),
                BlockedResourceIds = random.Next(8) == 0 ? ["RN"] : []
            };
            foreach (var (start, goal) in draft.Nodes.SelectMany(start => draft.Nodes.Select(goal => (start, goal))))
            {
                var input = basis with { Start = start.Id, Goal = goal.Id, Vehicle = basis.Vehicle with { PositionNodeId = start.Id } };
                var expected = SmallGraphOracle.Exhaustive(draft, input);
                var actual = DijkstraPlanner.Plan(graph, input);
                var floyd = SmallGraphOracle.Floyd(draft, input);
                if (!Matches(actual, expected) || (expected?.Cost ?? double.PositiveInfinity) != floyd)
                {
                    SaveFailure(seed, trial, draft, input);
                    Assert.Fail($"seed={seed}, trial={trial}, {start.Id}->{goal.Id}: persisted minimized replay under tests/Route/Counterexamples.");
                }
                if (expected is not null)
                {
                    Assert.Equal(expected.Cost, actual.Cost.Total);
                    Assert.Equal(actual.NodeIds.Length - 1, actual.Edges.Length);
                    Assert.Equal(actual.NodeIds.Length, actual.NodeIds.Distinct(StringComparer.Ordinal).Count());
                    Assert.Equal(actual.Edges.Sum(x => x.Cost.Base), actual.Cost.Base);
                    Assert.Equal(actual.Edges.Sum(x => x.Cost.Congestion), actual.Cost.Congestion);
                    Assert.Equal(actual.Edges.Sum(x => x.Cost.Energy), actual.Cost.Energy);
                    for (var i = 0; i < actual.Edges.Length; i++)
                    {
                        var edge = actual.Edges[i];
                        Assert.Equal(actual.NodeIds[i], edge.From);
                        Assert.Equal(actual.NodeIds[i + 1], edge.To);
                        Assert.Contains(draft.Edges, x => x.Id == edge.EdgeId);
                    }
                    var expectedResources = draft.Nodes.Single(x => x.Id == input.Start).ResourceIds.ToList();
                    foreach (var edge in expected.Edges)
                    {
                        expectedResources.AddRange(edge.ResourceIds);
                        expectedResources.AddRange(draft.Nodes.Single(x => x.Id == edge.To).ResourceIds);
                    }
                    Assert.Equal(expectedResources, actual.Resources.Select(x => x.ResourceId));
                    Assert.Equal(actual.Edges.Select(x => x.EdgeId), actual.Segments.SelectMany(x => x.EdgeIds));
                    Assert.Equal(Enumerable.Range(0, actual.Resources.Length), actual.Resources.Select(x => x.Index));
                }
                else
                {
                    Assert.Contains(actual.Status, new[] { PlanStatus.NoPath, PlanStatus.Blocked });
                    Assert.Empty(actual.Edges);
                    Assert.Empty(actual.NodeIds);
                    Assert.Empty(actual.Resources);
                    Assert.Equal(0, actual.CandidateCount);
                }
                var replayInput = input with
                {
                    ClosedEdgeIds = input.ClosedEdgeIds.Reverse().ToImmutableArray(),
                    Vehicle = input.Vehicle with { Capabilities = input.Vehicle.Capabilities.Reverse().ToImmutableArray() }
                };
                var replay = DijkstraPlanner.Plan(reordered, replayInput);
                Assert.Equal(actual.InputHash, replay.InputHash);
                Assert.Equal(actual.Status, replay.Status);
                Assert.Equal<string>(actual.NodeIds, replay.NodeIds);
                Assert.Equal(actual.Edges.Select(x => x.EdgeId), replay.Edges.Select(x => x.EdgeId));
                Assert.Equal<PlanRejection>(actual.Rejections, replay.Rejections);
            }
        }
    }

    private static TopologyDefinition Generate(Random random, int trial)
    {
        var g = RouteFixtures.G1();
        var count = random.Next(2, 7);
        var names = Enumerable.Range(0, count).Select(i => ((char)('A' + i)).ToString()).ToArray();
        var edges = new List<RouteEdge>();
        foreach (var (from, to) in names.SelectMany(from => names.Select(to => (from, to))))
        {
            if (random.Next(3) == 0)
            {
                // Trials divisible by four force many zero-cost cycles; self/parallel edges remain legal.
                var zero = trial % 4 == 0;
                var edge = RouteFixtures.Edge($"E{edges.Count:D3}", from, to) with
                {
                    Cost = new(zero ? 0 : random.Next(4), zero ? 0 : random.Next(2), zero ? 0 : random.Next(2)),
                    Direction = random.Next(2) == 0 ? TravelDirection.Forward : TravelDirection.Reverse,
                    MaxLoad = random.Next(2) == 0 ? 100 : 50,
                    RequiredCapabilities = random.Next(5) == 0 ? ["lift"] : [],
                    Forbidden = random.Next(12) == 0
                };
                edges.Add(edge);
                if (random.Next(7) == 0) edges.Add(edge with { Id = $"E{edges.Count:D3}" });
            }
        }
        return g with
        {
            Nodes = names.Select(x => new RouteNode(x, "FW1", "L1", false, ["RN"])).ToImmutableArray(),
            Edges = edges.ToImmutableArray(),
            Resources = [g.Resources[0], new("RN", ResourceKind.Node, 1, ["P1"])]
        };
    }

    private static bool Matches(RoutePlan actual, OraclePath? expected) => expected is null
        ? actual.Status is PlanStatus.NoPath or PlanStatus.Blocked
        : actual.Status == PlanStatus.Planned && actual.Cost.Total == expected.Cost
            && actual.NodeIds.SequenceEqual(expected.Nodes) && actual.Edges.Select(x => x.EdgeId).SequenceEqual(expected.Edges.Select(x => x.Id));

    private static void SaveFailure(int seed, int trial, TopologyDefinition map, PlanRequest input)
    {
        bool Fails(TopologyDefinition candidate)
        {
            // Removed edges must also be removed from the request's closure list, not turned into invalid input.
            var request = input with { ClosedEdgeIds = input.ClosedEdgeIds.Where(id => candidate.Edges.Any(x => x.Id == id)).ToImmutableArray() };
            var expected = SmallGraphOracle.Exhaustive(candidate, request);
            return !Matches(DijkstraPlanner.Plan(Topology.Create(candidate), request), expected)
                || (expected?.Cost ?? double.PositiveInfinity) != SmallGraphOracle.Floyd(candidate, request);
        }
        var minimal = SmallGraphOracle.Minimize(map, input, Fails);
        var replay = input with { ClosedEdgeIds = input.ClosedEdgeIds.Where(id => minimal.Edges.Any(x => x.Id == id)).ToImmutableArray() };
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Counterexamples"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"seed-{seed}-trial-{trial}-{input.Start}-{input.Goal}.json"),
            JsonSerializer.Serialize(new
            {
                Seed = seed,
                Trial = trial,
                Planner = DijkstraPlanner.Version,
                MapHash = Topology.Create(minimal).Hash,
                Map = minimal,
                Input = replay,
                Expected = SmallGraphOracle.Exhaustive(minimal, replay),
                Actual = DijkstraPlanner.Plan(Topology.Create(minimal), replay),
                FloydCost = SmallGraphOracle.Floyd(minimal, replay)
            }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }
}
