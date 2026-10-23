using System.Collections.Immutable;
using Wes.Wcs.FourWay.Routing;

namespace Wes.Route.Tests;

internal sealed record OraclePath(double Cost, string[] Nodes, RouteEdge[] Edges);

// Deliberately no production routing/validation helpers: exhaustive simple paths and a separate
// Floyd-Warshall matrix are practical independent references only for small finite graphs.
internal static class SmallGraphOracle
{
    internal static OraclePath? Exhaustive(TopologyDefinition map, PlanRequest input)
    {
        OraclePath? best = null;
        var nodes = new List<string> { input.Start };
        var path = new List<RouteEdge>();
        if (map.Nodes.Single(x => x.Id == input.Start).ResourceIds.Any(input.BlockedResourceIds.Contains))
            return null;
        void Enumerate(string at, double cost)
        {
            if (at == input.Goal)
            {
                var candidate = new OraclePath(cost, nodes.ToArray(), path.ToArray());
                if (best is null || Better(candidate, best))
                    best = candidate;
                return;
            }
            foreach (var edge in map.Edges)
            {
                if (edge.From != at || nodes.Contains(edge.To) || !Allowed(map, edge, input))
                    continue;
                nodes.Add(edge.To);
                path.Add(edge);
                Enumerate(edge.To, cost + (edge.Cost.Base + edge.Cost.Congestion + edge.Cost.Energy));
                path.RemoveAt(path.Count - 1);
                nodes.RemoveAt(nodes.Count - 1);
            }
        }
        Enumerate(input.Start, 0);
        return best;
    }

    internal static double Floyd(TopologyDefinition map, PlanRequest input)
    {
        var names = map.Nodes.Select(x => x.Id).ToArray();
        var n = names.Length;
        var costs = new double[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                costs[i, j] = i == j && !map.Nodes[i].ResourceIds.Any(input.BlockedResourceIds.Contains) ? 0 : double.PositiveInfinity;
        foreach (var edge in map.Edges)
        {
            if (!Allowed(map, edge, input))
                continue;
            var a = Array.IndexOf(names, edge.From);
            var b = Array.IndexOf(names, edge.To);
            costs[a, b] = Math.Min(costs[a, b], edge.Cost.Base + edge.Cost.Congestion + edge.Cost.Energy);
        }
        for (var k = 0; k < n; k++)
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    costs[i, j] = Math.Min(costs[i, j], costs[i, k] + costs[k, j]);
        return costs[Array.IndexOf(names, input.Start), Array.IndexOf(names, input.Goal)];
    }

    internal static TopologyDefinition Minimize(TopologyDefinition map, PlanRequest input, Func<TopologyDefinition, bool> stillFails)
    {
        if (!stillFails(map))
            throw new ArgumentException("Shrinking requires a reproducible counterexample.", nameof(stillFails));
        var current = map;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in current.Edges.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var reduced = current with { Edges = current.Edges.Where(x => x.Id != edge.Id).ToImmutableArray() };
                if (stillFails(reduced))
                {
                    current = reduced;
                    changed = true;
                    break;
                }
            }
        }
        foreach (var node in current.Nodes.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (node.Id == input.Start || node.Id == input.Goal || current.Edges.Any(x => x.From == node.Id || x.To == node.Id))
                continue;
            var reduced = current with { Nodes = current.Nodes.Where(x => x.Id != node.Id).ToImmutableArray() };
            if (stillFails(reduced))
                current = reduced;
        }
        return current;
    }

    private static bool Allowed(TopologyDefinition map, RouteEdge edge, PlanRequest input)
    {
        if (edge.Forbidden || input.ClosedEdgeIds.Contains(edge.Id))
            return false;
        if (input.Vehicle.Load > edge.MaxLoad || !input.Vehicle.AllowedDirections.Contains(edge.Direction))
            return false;
        foreach (var required in edge.RequiredCapabilities)
            if (!input.Vehicle.Capabilities.Contains(required)) return false;
        foreach (var resource in edge.ResourceIds)
            if (input.BlockedResourceIds.Contains(resource)) return false;
        foreach (var node in map.Nodes)
            if ((node.Id == edge.From || node.Id == edge.To) && node.ResourceIds.Any(input.BlockedResourceIds.Contains))
                return false;
        return true;
    }

    private static bool Better(OraclePath a, OraclePath b)
    {
        if (a.Cost != b.Cost) return a.Cost < b.Cost;
        if (a.Edges.Length != b.Edges.Length) return a.Edges.Length < b.Edges.Length;
        for (var i = 0; i < a.Nodes.Length; i++)
        {
            var result = string.CompareOrdinal(a.Nodes[i], b.Nodes[i]);
            if (result != 0) return result < 0;
        }
        for (var i = 0; i < a.Edges.Length; i++)
        {
            var result = string.CompareOrdinal(a.Edges[i].Id, b.Edges[i].Id);
            if (result != 0) return result < 0;
        }
        return false;
    }
}
