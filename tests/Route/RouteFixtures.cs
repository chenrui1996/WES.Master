using System.Collections.Immutable;
using Wes.Wcs.FourWay.Routing;

namespace Wes.Route.Tests;

internal static class RouteFixtures
{
    internal static RouteEdge Edge(string id, string from, string to, double cost = 1) =>
        new(id, from, to, new(cost), 1, TravelDirection.Forward, 100, [], ["R1"], "S1", "move");

    internal static TopologyDefinition G1(string version = "MV1") =>
        new(version, "FW1", PackKind.FourWay, "L1", "local-mm", "test-cost", ["turn", "lift"],
            new[] { "A", "B", "C", "D" }.Select(x => new RouteNode(x, "FW1", "L1", false, ["R1"])).ToImmutableArray(),
            [Edge("AB", "A", "B"), Edge("BD", "B", "D"), Edge("AC", "A", "C"), Edge("CD", "C", "D")],
            [new("R1", ResourceKind.Corridor, 1, ["P1"])],
            [new("P1", 1, false, "geometry-1", "device-1")]);
}
