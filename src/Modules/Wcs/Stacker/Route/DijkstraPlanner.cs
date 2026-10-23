using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wes.Wcs.Stacker.Routing;

/// <summary>Candidate outcomes; none permits movement or releases physical protection.</summary>
public enum PlanStatus { Planned, NoMap, NoPath, Blocked, StaleState, Unsupported, InvalidInput, MapMismatch }

/// <summary>Pack input quality; unknown/conflicting observations never become trusted coordinates.</summary>
public enum SnapshotQuality { Trusted, Unknown, Conflict }

/// <summary>Stacker capability/position snapshot with caller-supplied source and sequencing evidence.</summary>
public sealed record StackerSnapshot(string DeviceId, string PackId, string AisleId, string MapVersionId,
    string StateVersion, string PositionNodeId, TravelDirection Heading, double Load, ImmutableArray<string> Capabilities,
    ImmutableArray<TravelDirection> AllowedDirections, string Source, DateTimeOffset ObservedAt,
    long Generation, long Sequence, SnapshotQuality Quality);

/// <summary>Explicit Pack planning request with pinned artifact/state and dynamic unavailable-resource projection.</summary>
public sealed record PlanRequest(string DecisionId, string TaskId, string LegId, string GoalId,
    string Start, string Goal, StackerSnapshot Device, DateTimeOffset Now, TimeSpan FreshnessLimit,
    string ExpectedSource, long MinimumGeneration, long MinimumSequence, ImmutableArray<string> ClosedEdgeIds,
    ImmutableArray<string> BlockedResourceIds, string Algorithm = "Dijkstra");

/// <summary>Hard-filter/input rejection, not a soft penalty.</summary>
public sealed record PlanRejection(string Reference, string Code);

/// <summary>One ordered resource occurrence including repeated physical identities.</summary>
public sealed record ResourceOccurrence(int Index, string AtNodeId, string? EdgeId, string ResourceId,
    ResourceKind Kind, int Capacity, ImmutableArray<string> PhysicalConflictIds);

/// <summary>Every original edge retains its device/ExeStackCode/command binding for feedback correlation.</summary>
public sealed record PlannedEdge(string EdgeId, string From, string To, EdgeCost Cost,
    string ExeStackCode, string DeviceCode, string CommandMapping);

/// <summary>Contiguous ExeStackCode/device/command group; edge/resource indices preserve original feedback mappings.</summary>
public sealed record ExecutionSegment(int Index, string ExeStackCode, string DeviceCode, string CommandMapping,
    ImmutableArray<string> EdgeIds, ImmutableArray<int> ResourceOccurrenceIndices);

/// <summary>Traversal-order total plus explanatory component totals; IEEE double arithmetic is not decimal measurement.</summary>
public sealed record RouteCost(double Base, double Congestion, double Energy, double Total);

/// <summary>Immutable replayable candidate, never a grant or dispatch envelope.</summary>
public sealed record RoutePlan(PlanStatus Status, string PlannerVersion, string? MapVersionId, string? MapHash,
    string? CostUnit, string InputHash, PlanRequest Input, int CandidateCount, ImmutableArray<string> NodeIds,
    ImmutableArray<PlannedEdge> Edges, RouteCost Cost, ImmutableArray<ResourceOccurrence> Resources,
    ImmutableArray<ExecutionSegment> Segments, ImmutableArray<PlanRejection> Rejections);

/// <summary>Stacker-private nonnegative Dijkstra with cost/hops/ordinal-node/ordinal-edge deterministic ties.</summary>
public static class DijkstraPlanner
{
    /// <summary>Pack replay version, separate from the FourWay implementation.</summary>
    public const string Version = "stacker-dijkstra-2";

    /// <summary>Generate a candidate from a validated pinned artifact; no database, device or reservation effects.</summary>
    public static RoutePlan Plan(Topology? topology, PlanRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Device);
        cancellationToken.ThrowIfCancellationRequested();
        var device = input.Device;
        static ImmutableArray<T> Present<T>(ImmutableArray<T> items) => items.IsDefault ? [] : items;
        var canonical = input with
        {
            ClosedEdgeIds = Present(input.ClosedEdgeIds).Order(StringComparer.Ordinal).ToImmutableArray(),
            BlockedResourceIds = Present(input.BlockedResourceIds).Order(StringComparer.Ordinal).ToImmutableArray(),
            Device = device with
            {
                Capabilities = Present(device.Capabilities).Order(StringComparer.Ordinal).ToImmutableArray(),
                AllowedDirections = Present(device.AllowedDirections).Order().ToImmutableArray()
            }
        };
        var inputHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical,
            new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals })));
        var rejections = ImmutableArray.CreateBuilder<PlanRejection>();
        RoutePlan Fail(PlanStatus status, string code, string reference)
        {
            rejections.Add(new(reference, code));
            return new(status, Version, topology?.Definition.VersionId, topology?.Hash, topology?.Definition.CostUnit,
                inputHash, input, 0, [], [], new(0, 0, 0, 0), [], [],
                rejections.OrderBy(x => x.Reference, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToImmutableArray());
        }
        static bool Missing(string id) => string.IsNullOrWhiteSpace(id) || id.Trim() != id;
        if (new[] { input.DecisionId, input.TaskId, input.LegId, input.GoalId, input.Start, input.Goal, input.ExpectedSource,
            device.DeviceId, device.PackId, device.AisleId, device.MapVersionId, device.StateVersion, device.PositionNodeId, device.Source }.Any(Missing)
            || !double.IsFinite(device.Load) || device.Load < 0 || input.FreshnessLimit <= TimeSpan.Zero
            || input.MinimumGeneration < 0 || input.MinimumSequence < 0 || device.Generation < 0 || device.Sequence < 0
            || input.ClosedEdgeIds.IsDefault || input.BlockedResourceIds.IsDefault || device.Capabilities.IsDefault
            || device.AllowedDirections.IsDefault || input.ClosedEdgeIds.Any(Missing) || input.BlockedResourceIds.Any(Missing)
            || device.Capabilities.Any(Missing) || !Enum.IsDefined(device.Quality) || !Enum.IsDefined(device.Heading)
            || device.AllowedDirections.Any(x => !Enum.IsDefined(x)))
            return Fail(PlanStatus.InvalidInput, "InvalidInput", "request/device");
        if (input.Algorithm != "Dijkstra") return Fail(PlanStatus.Unsupported, "UnsupportedAlgorithm", input.Algorithm);
        if (topology is null) return Fail(PlanStatus.NoMap, "NoMap", device.MapVersionId);
        var map = topology.Definition;
        if (map.VersionId != device.MapVersionId || map.PackId != device.PackId || map.AisleId != device.AisleId)
            return Fail(PlanStatus.MapMismatch, "MapMismatch", device.MapVersionId);
        if (device.Quality != SnapshotQuality.Trusted || device.Source != input.ExpectedSource || input.Now < device.ObservedAt
            || input.Now - device.ObservedAt >= input.FreshnessLimit || device.Generation < input.MinimumGeneration
            || device.Generation == input.MinimumGeneration && device.Sequence < input.MinimumSequence)
            return Fail(PlanStatus.StaleState, "UntrustedSnapshot", device.StateVersion);
        if (device.AllowedDirections.IsEmpty || device.Capabilities.Any(x => !map.Capabilities.Contains(x)))
            return Fail(PlanStatus.Unsupported, "UnsupportedCapability", device.DeviceId);
        if (!map.DeviceCodes.Contains(device.DeviceId))
            return Fail(PlanStatus.Unsupported, "UnknownDevice", device.DeviceId);
        var nodes = map.Nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var edges = map.Edges.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var resources = map.Resources.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (!nodes.ContainsKey(input.Start) || !nodes.ContainsKey(input.Goal) || device.PositionNodeId != input.Start
            || input.ClosedEdgeIds.Any(x => !edges.ContainsKey(x)) || input.BlockedResourceIds.Any(x => !resources.ContainsKey(x)))
            return Fail(PlanStatus.InvalidInput, "UnknownReferenceOrPosition", "request");
        var closed = input.ClosedEdgeIds.ToHashSet(StringComparer.Ordinal);
        var blocked = input.BlockedResourceIds.ToHashSet(StringComparer.Ordinal);
        if (nodes[input.Start].ResourceIds.Any(blocked.Contains))
            return Fail(PlanStatus.Blocked, "BlockedResource", input.Start);
        var available = new Dictionary<string, List<RouteEdge>>(StringComparer.Ordinal);
        foreach (var edge in map.Edges)
        {
            var reasons = new List<string>();
            // This request plans one executor only. Cross-device handoff needs a separate approved contract.
            if (edge.DeviceCode != device.DeviceId) reasons.Add("DeviceMismatch");
            if (edge.Forbidden) reasons.Add("Forbidden");
            if (closed.Contains(edge.Id)) reasons.Add("Closed");
            if (edge.MaxLoad < device.Load) reasons.Add("Load");
            if (!device.AllowedDirections.Contains(edge.Direction)) reasons.Add("Direction");
            if (edge.RequiredCapabilities.Any(x => !device.Capabilities.Contains(x))) reasons.Add("Capability");
            if (edge.ResourceIds.Concat(nodes[edge.From].ResourceIds).Concat(nodes[edge.To].ResourceIds).Any(blocked.Contains))
                reasons.Add("BlockedResource");
            foreach (var reason in reasons) rejections.Add(new(edge.Id, reason));
            if (reasons.Count == 0)
            {
                if (!available.TryGetValue(edge.From, out var outgoing))
                    available.Add(edge.From, outgoing = []);
                outgoing.Add(edge);
            }
        }
        // Strictly increasing hops breaks equal-cost cycles; an ordinal total order avoids queue insertion-order ties.
        var comparer = new LabelComparer();
        var queue = new PriorityQueue<Label, Label>(comparer);
        var first = new Label(0, [input.Start], []);
        var best = new Dictionary<string, Label>(StringComparer.Ordinal) { [input.Start] = first };
        queue.Enqueue(first, first);
        var overflow = false;
        Label? chosen = null;
        while (queue.TryDequeue(out var label, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = label.Nodes[^1];
            if (!ReferenceEquals(best[at], label)) continue;
            if (at == input.Goal)
            {
                chosen = label;
                break;
            }
            if (!available.TryGetValue(at, out var outgoing)) continue;
            foreach (var edge in outgoing)
            {
                var total = label.Cost + edge.Cost.Total;
                if (!double.IsFinite(total))
                {
                    overflow = true;
                    rejections.Add(new(edge.Id, "CostOverflow"));
                    continue;
                }
                var candidate = new Label(total, label.Nodes.Add(edge.To), label.Edges.Add(edge));
                if (!best.TryGetValue(edge.To, out var previous) || comparer.Compare(candidate, previous) < 0)
                {
                    best[edge.To] = candidate;
                    queue.Enqueue(candidate, candidate);
                }
            }
        }
        if (chosen is null)
        {
            if (overflow) return Fail(PlanStatus.Unsupported, "CostOverflow", "path/total");
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
            return Fail(status, status.ToString(), $"{input.Start}->{input.Goal}");
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
        var outputEdges = ImmutableArray.CreateBuilder<PlannedEdge>();
        double baseCost = 0, congestion = 0, energy = 0;
        foreach (var edge in chosen.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstIndex = segments.Count == 0 ? 0 : occurrences.Count;
            AddResources(edge.ResourceIds, edge.From, edge.Id);
            AddResources(nodes[edge.To].ResourceIds, edge.To, null);
            var indices = Enumerable.Range(firstIndex, occurrences.Count - firstIndex).ToImmutableArray();
            outputEdges.Add(new(edge.Id, edge.From, edge.To, edge.Cost, edge.ExeStackCode, edge.DeviceCode, edge.CommandMapping));
            baseCost += edge.Cost.Base;
            congestion += edge.Cost.Congestion;
            energy += edge.Cost.Energy;
            // Only contiguous edges for the same physical executor and semantic command can share a segment.
            // Reusing ExeStackCode after another segment never merges noncontiguous resource/feedback references.
            if (segments.Count > 0 && segments[^1].ExeStackCode == edge.ExeStackCode
                && segments[^1].DeviceCode == edge.DeviceCode && segments[^1].CommandMapping == edge.CommandMapping)
            {
                var previous = segments[^1];
                segments[^1] = previous with
                {
                    EdgeIds = previous.EdgeIds.Add(edge.Id),
                    ResourceOccurrenceIndices = previous.ResourceOccurrenceIndices.AddRange(indices)
                };
            }
            else segments.Add(new(segments.Count, edge.ExeStackCode, edge.DeviceCode, edge.CommandMapping, [edge.Id], indices));
        }
        return new(PlanStatus.Planned, Version, map.VersionId, topology.Hash, map.CostUnit, inputHash, input, 1,
            chosen.Nodes, outputEdges.ToImmutable(), new(baseCost, congestion, energy, chosen.Cost),
            occurrences.ToImmutable(), segments.ToImmutableArray(),
            rejections.OrderBy(x => x.Reference, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToImmutableArray());
    }

    private sealed record Label(double Cost, ImmutableArray<string> Nodes, ImmutableArray<RouteEdge> Edges);

    private sealed class LabelComparer : IComparer<Label>
    {
        public int Compare(Label? a, Label? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;
            var result = a.Cost.CompareTo(b.Cost);
            if (result != 0) return result;
            result = a.Edges.Length.CompareTo(b.Edges.Length);
            if (result != 0) return result;
            for (var i = 0; i < a.Nodes.Length; i++)
            {
                result = StringComparer.Ordinal.Compare(a.Nodes[i], b.Nodes[i]);
                if (result != 0) return result;
            }
            for (var i = 0; i < a.Edges.Length; i++)
            {
                result = StringComparer.Ordinal.Compare(a.Edges[i].Id, b.Edges[i].Id);
                if (result != 0) return result;
            }
            return 0;
        }
    }
}

/// <summary>Stacker-owned deployment projection; active state is not mutated by this kernel.</summary>
public sealed record MapBinding(Topology Topology, bool Active);

/// <summary>Exact selection outcome, not task acceptance or permission to activate/dispatch.</summary>
public sealed record MapResolution(Topology? Topology, string? Error);

/// <summary>Resolve a pinned old task or one active Stacker map without first-row fallback.</summary>
public static class MapSelector
{
    /// <summary>Select exact Pack/aisle/version. Multiple artifacts with one version and different hashes fail closed.</summary>
    public static MapResolution Resolve(IEnumerable<MapBinding> maps, string packId, string aisleId, string? versionId = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(aisleId)
            || versionId is not null && string.IsNullOrWhiteSpace(versionId)) return new(null, "InvalidSelection");
        var candidates = maps.Where(x => x.Topology.Definition.PackId == packId && x.Topology.Definition.AisleId == aisleId).ToArray();
        if (candidates.Length == 0) return new(null, "NoMap");
        if (candidates.GroupBy(x => x.Topology.Definition.VersionId, StringComparer.Ordinal)
            .Any(x => x.Select(m => m.Topology.Hash).Distinct(StringComparer.Ordinal).Count() > 1))
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
