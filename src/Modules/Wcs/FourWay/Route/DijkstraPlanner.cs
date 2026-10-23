using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wes.Wcs.FourWay.Routing;

/// <summary>Explicit candidate outcome; none of these outcomes grants movement authority.</summary>
public enum PlanStatus { Planned, NoMap, NoPath, Blocked, StaleState, Unsupported, InvalidInput, MapMismatch }

/// <summary>Conflicting/unknown observations cannot be used as planning truth.</summary>
public enum SnapshotQuality { Trusted, Unknown, Conflict }

/// <summary>Owner-validated capability/state input. Freshness and sequence floors must be supplied, not guessed.</summary>
public sealed record VehicleSnapshot(
    string VehicleId, string PackId, string LayerId, string MapVersionId, string StateVersion,
    string PositionNodeId, TravelDirection Heading, double Load, ImmutableArray<string> Capabilities,
    ImmutableArray<TravelDirection> AllowedDirections, string Source, DateTimeOffset ObservedAt,
    long Generation, long Sequence, SnapshotQuality Quality);

/// <summary>Full replay input. Now/freshness are explicit offline inputs, not site defaults or wall-clock reads.</summary>
public sealed record PlanRequest(
    string DecisionId, string TaskId, string GoalId, string Start, string Goal, VehicleSnapshot Vehicle,
    DateTimeOffset Now, TimeSpan FreshnessLimit, string ExpectedSource, long MinimumGeneration, long MinimumSequence,
    ImmutableArray<string> ClosedEdgeIds, ImmutableArray<string> BlockedResourceIds, string Algorithm = "Dijkstra");

/// <summary>Rejected edge/input diagnostic; code identifies a hard constraint, not a penalty cost.</summary>
public sealed record PlanRejection(string Reference, string Code);

/// <summary>One resource occurrence, including repeated node/edge visits and stable physical identities.</summary>
public sealed record ResourceOccurrence(int Index, string AtNodeId, string? EdgeId, string ResourceId,
    ResourceKind Kind, int Capacity, ImmutableArray<string> PhysicalConflictIds);

/// <summary>FourWay per-edge execution segment; retains resource occurrence indices for feedback mapping.</summary>
public sealed record ExecutionSegment(int Index, string SegmentId, string CommandMapping,
    ImmutableArray<string> EdgeIds, ImmutableArray<int> ResourceOccurrenceIndices);

/// <summary>Explainable edge cost in the artifact's declared unit.</summary>
public sealed record PlannedEdge(string EdgeId, string From, string To, EdgeCost Cost, string SegmentId, string CommandMapping);

/// <summary>Total uses traversal-order summation; component sums explain it without silently rounding floating costs.</summary>
public sealed record RouteCost(double Base, double Congestion, double Energy, double Total);

/// <summary>Immutable replay/diagnostic result; empty candidate arrays on every failure, no grant or controller command.</summary>
public sealed record RoutePlan(
    PlanStatus Status, string PlannerVersion, string? MapVersionId, string? MapHash, string? CostUnit,
    string InputHash, PlanRequest Input, int CandidateCount, ImmutableArray<string> NodeIds,
    ImmutableArray<PlannedEdge> Edges, RouteCost Cost, ImmutableArray<ResourceOccurrence> Resources,
    ImmutableArray<ExecutionSegment> Segments, ImmutableArray<PlanRejection> Rejections);

/// <summary>Pure single-candidate Dijkstra; no persistence, device effects, occupancy ownership or A* heuristic.</summary>
public static class DijkstraPlanner
{
    /// <summary>Replay version changes whenever tie/constraint/output semantics change.</summary>
    public const string Version = "dijkstra-1";

    /// <summary>Return an explicit outcome for a fixed map and immutable input; never synthesize a direct route.</summary>
    public static RoutePlan Plan(Topology? topology, PlanRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Vehicle);
        cancellationToken.ThrowIfCancellationRequested();
        var v = input.Vehicle;
        static ImmutableArray<T> Present<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;
        var replay = input with
        {
            ClosedEdgeIds = Present(input.ClosedEdgeIds).Order(StringComparer.Ordinal).ToImmutableArray(),
            BlockedResourceIds = Present(input.BlockedResourceIds).Order(StringComparer.Ordinal).ToImmutableArray(),
            Vehicle = v with
            {
                Capabilities = Present(v.Capabilities).Order(StringComparer.Ordinal).ToImmutableArray(),
                AllowedDirections = Present(v.AllowedDirections).Order().ToImmutableArray()
            }
        };
        var inputHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(replay,
            new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals })));
        var rejections = ImmutableArray.CreateBuilder<PlanRejection>();
        RoutePlan Failure(PlanStatus status, string code, string reference)
        {
            rejections.Add(new(reference, code));
            return new(status, Version, topology?.Definition.VersionId, topology?.Hash, topology?.Definition.CostUnit,
                inputHash, input, 0, [], [], new(0, 0, 0, 0), [], [], rejections.ToImmutable());
        }
        static bool Missing(string value) => string.IsNullOrWhiteSpace(value) || value != value.Trim();
        if (new[] { input.DecisionId, input.TaskId, input.GoalId, input.Start, input.Goal, input.ExpectedSource,
            v.VehicleId, v.PackId, v.LayerId, v.MapVersionId, v.StateVersion, v.PositionNodeId, v.Source }.Any(Missing)
            || !double.IsFinite(v.Load) || v.Load < 0 || input.FreshnessLimit <= TimeSpan.Zero
            || input.MinimumGeneration < 0 || input.MinimumSequence < 0 || v.Generation < 0 || v.Sequence < 0
            || input.ClosedEdgeIds.IsDefault || input.BlockedResourceIds.IsDefault
            || v.Capabilities.IsDefault || v.AllowedDirections.IsDefault
            || input.ClosedEdgeIds.Any(Missing) || input.BlockedResourceIds.Any(Missing)
            || v.Capabilities.Any(Missing) || !Enum.IsDefined(v.Heading) || !Enum.IsDefined(v.Quality)
            || v.AllowedDirections.Any(x => !Enum.IsDefined(x)))
            return Failure(PlanStatus.InvalidInput, "InvalidInput", "request/snapshot");
        if (input.Algorithm != "Dijkstra")
            return Failure(PlanStatus.Unsupported, "UnsupportedAlgorithm", input.Algorithm);
        if (topology is null)
            return Failure(PlanStatus.NoMap, "NoMap", v.MapVersionId);
        var map = topology.Definition;
        if (map.VersionId != v.MapVersionId || map.PackId != v.PackId || map.LayerId != v.LayerId)
            return Failure(PlanStatus.MapMismatch, "MapMismatch", $"{v.PackId}/{v.LayerId}/{v.MapVersionId}");
        if (v.Quality != SnapshotQuality.Trusted || input.Now < v.ObservedAt || input.Now - v.ObservedAt >= input.FreshnessLimit
            || v.Source != input.ExpectedSource || v.Generation < input.MinimumGeneration
            || (v.Generation == input.MinimumGeneration && v.Sequence < input.MinimumSequence))
            return Failure(PlanStatus.StaleState, "UntrustedSnapshot", v.StateVersion);
        if (v.AllowedDirections.IsEmpty || v.Capabilities.Any(x => !map.Capabilities.Contains(x)))
            return Failure(PlanStatus.Unsupported, "UnsupportedCapability", v.VehicleId);
        var nodes = map.Nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var edges = map.Edges.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var resources = map.Resources.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (!nodes.ContainsKey(input.Start) || !nodes.ContainsKey(input.Goal) || v.PositionNodeId != input.Start
            || input.ClosedEdgeIds.Any(x => !edges.ContainsKey(x))
            || input.BlockedResourceIds.Any(x => !resources.ContainsKey(x)))
            return Failure(PlanStatus.InvalidInput, "UnknownReferenceOrPosition", "request");
        var closed = input.ClosedEdgeIds.ToHashSet(StringComparer.Ordinal);
        var blocked = input.BlockedResourceIds.ToHashSet(StringComparer.Ordinal);
        if (nodes[input.Start].ResourceIds.Any(blocked.Contains))
            return Failure(PlanStatus.Blocked, "BlockedResource", input.Start);
        var available = new Dictionary<string, ImmutableArray<RouteEdge>>(StringComparer.Ordinal);
        foreach (var group in map.Edges.GroupBy(x => x.From, StringComparer.Ordinal))
        {
            var accepted = ImmutableArray.CreateBuilder<RouteEdge>();
            foreach (var edge in group)
            {
                var reasons = new List<string>();
                if (edge.Forbidden) reasons.Add("Forbidden");
                if (closed.Contains(edge.Id)) reasons.Add("Closed");
                if (!v.AllowedDirections.Contains(edge.Direction)) reasons.Add("Direction");
                if (v.Load > edge.MaxLoad) reasons.Add("Load");
                if (edge.RequiredCapabilities.Any(x => !v.Capabilities.Contains(x))) reasons.Add("Capability");
                if (edge.ResourceIds.Concat(nodes[edge.To].ResourceIds).Concat(nodes[edge.From].ResourceIds).Any(blocked.Contains))
                    reasons.Add("BlockedResource");
                foreach (var reason in reasons)
                    rejections.Add(new(edge.Id, reason));
                if (reasons.Count == 0)
                    accepted.Add(edge);
            }
            available[group.Key] = accepted.ToImmutable();
        }

        // Cost, hops, then ordinal node/edge paths form a total order. Hops strictly increase,
        // so zero-cost cycles cannot improve a label. No heuristic or epsilon changes the cost objective.
        var comparer = new LabelComparer();
        var queue = new PriorityQueue<Label, Label>(comparer);
        var first = new Label(0, [input.Start], []);
        var best = new Dictionary<string, Label>(StringComparer.Ordinal) { [input.Start] = first };
        queue.Enqueue(first, first);
        Label? chosen = null;
        var overflow = false;
        while (queue.TryDequeue(out var label, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = label.Nodes[^1];
            if (!ReferenceEquals(best[at], label))
                continue;
            if (at == input.Goal)
            {
                chosen = label;
                break;
            }
            if (!available.TryGetValue(at, out var outgoing))
                continue;
            foreach (var edge in outgoing)
            {
                var cost = label.Cost + edge.Cost.Total;
                if (!double.IsFinite(cost))
                {
                    overflow = true;
                    rejections.Add(new(edge.Id, "CostOverflow"));
                    continue;
                }
                var next = new Label(cost, label.Nodes.Add(edge.To), label.Edges.Add(edge));
                if (!best.TryGetValue(edge.To, out var previous) || comparer.Compare(next, previous) < 0)
                {
                    best[edge.To] = next;
                    queue.Enqueue(next, next);
                }
            }
        }
        if (chosen is null)
        {
            if (overflow)
                return Failure(PlanStatus.Unsupported, "CostOverflow", "path/total");
            var seen = new HashSet<string>(StringComparer.Ordinal) { input.Start };
            var pending = new Queue<string>();
            pending.Enqueue(input.Start);
            var adjacency = map.Edges.GroupBy(x => x.From).ToDictionary(x => x.Key, x => x.Select(e => e.To));
            while (pending.TryDequeue(out var at))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (adjacency.TryGetValue(at, out var next))
                    foreach (var node in next)
                        if (seen.Add(node)) pending.Enqueue(node);
            }
            var status = seen.Contains(input.Goal) ? PlanStatus.Blocked : PlanStatus.NoPath;
            return Failure(status, status.ToString(), $"{input.Start}->{input.Goal}");
        }
        var occurrences = ImmutableArray.CreateBuilder<ResourceOccurrence>();
        void AddResources(ImmutableArray<string> ids, string node, string? edge)
        {
            foreach (var id in ids)
            {
                var resource = resources[id];
                occurrences.Add(new(occurrences.Count, node, edge, id, resource.Kind, resource.Capacity, resource.PhysicalConflictIds));
            }
        }
        AddResources(nodes[input.Start].ResourceIds, input.Start, null);
        var segments = new List<ExecutionSegment>();
        var edgeOutput = ImmutableArray.CreateBuilder<PlannedEdge>();
        double baseCost = 0, congestionCost = 0, energyCost = 0;
        foreach (var edge in chosen.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startIndex = occurrences.Count;
            AddResources(edge.ResourceIds, edge.From, edge.Id);
            AddResources(nodes[edge.To].ResourceIds, edge.To, null);
            edgeOutput.Add(new(edge.Id, edge.From, edge.To, edge.Cost, edge.ExecutionSegmentId, edge.CommandMapping));
            baseCost += edge.Cost.Base;
            congestionCost += edge.Cost.Congestion;
            energyCost += edge.Cost.Energy;
            var indices = Enumerable.Range(segments.Count == 0 ? 0 : startIndex,
                occurrences.Count - (segments.Count == 0 ? 0 : startIndex)).ToImmutableArray();
            segments.Add(new(segments.Count, edge.ExecutionSegmentId, edge.CommandMapping, [edge.Id], indices));
        }
        return new(PlanStatus.Planned, Version, map.VersionId, topology.Hash, map.CostUnit, inputHash, input, 1,
            chosen.Nodes, edgeOutput.ToImmutable(), new(baseCost, congestionCost, energyCost, chosen.Cost),
            occurrences.ToImmutable(), segments.ToImmutableArray(),
            rejections.OrderBy(x => x.Reference, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToImmutableArray());
    }

    private sealed record Label(double Cost, ImmutableArray<string> Nodes, ImmutableArray<RouteEdge> Edges);

    private sealed class LabelComparer : IComparer<Label>
    {
        public int Compare(Label? x, Label? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var result = x.Cost.CompareTo(y.Cost);
            if (result != 0) return result;
            result = x.Edges.Length.CompareTo(y.Edges.Length);
            if (result != 0) return result;
            result = CompareIds(x.Nodes, y.Nodes);
            return result != 0 ? result : CompareIds(x.Edges.Select(e => e.Id), y.Edges.Select(e => e.Id));
        }

        private static int CompareIds(IEnumerable<string> x, IEnumerable<string> y)
        {
            using var a = x.GetEnumerator();
            using var b = y.GetEnumerator();
            while (a.MoveNext() && b.MoveNext())
            {
                var result = StringComparer.Ordinal.Compare(a.Current, b.Current);
                if (result != 0) return result;
            }
            return 0;
        }
    }
}

/// <summary>Pack-owned deployment projection; the kernel never mutates its active flag.</summary>
public sealed record MapBinding(Topology Topology, bool Active);

/// <summary>Map resolution result. An explicit version supports pinned old tasks, not authorization of new tasks.</summary>
public sealed record MapResolution(Topology? Topology, string? Error);

/// <summary>Stateless exact Pack/layer/version selection without first-row fallback.</summary>
public static class MapSelector
{
    /// <summary>Resolve an explicitly pinned version or exactly one active artifact.</summary>
    public static MapResolution Resolve(IEnumerable<MapBinding> maps, string packId, string layerId, string? versionId = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(layerId) || versionId is not null && string.IsNullOrWhiteSpace(versionId))
            return new(null, "InvalidSelection");
        var candidates = maps.Where(x => x.Topology.Definition.PackId == packId && x.Topology.Definition.LayerId == layerId).ToArray();
        if (candidates.Length == 0)
            return new(null, "NoMap");
        if (candidates.GroupBy(x => x.Topology.Definition.VersionId, StringComparer.Ordinal)
            .Any(group => group.Select(x => x.Topology.Hash).Distinct(StringComparer.Ordinal).Count() > 1))
            return new(null, "ConflictingVersionIdentity");
        var matching = candidates.Where(x => versionId is null ? x.Active : x.Topology.Definition.VersionId == versionId).ToArray();
        return matching.Length switch
        {
            0 => new(null, versionId is null ? "NoActiveMap" : "UnknownVersion"),
            1 => new(matching[0].Topology, null),
            _ => new(null, versionId is null ? "AmbiguousActiveMap" : "AmbiguousVersion")
        };
    }
}
