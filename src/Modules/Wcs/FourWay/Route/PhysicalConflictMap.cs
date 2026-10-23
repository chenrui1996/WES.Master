using System.Collections.Immutable;
using System.Text.Json;

namespace Wes.Wcs.FourWay.Routing;

/// <summary>Explicit rename/split/merge lineage; every old and new logical resource must be covered once.</summary>
public sealed record ResourceLineage(ImmutableArray<string> OldResourceIds, ImmutableArray<string> NewResourceIds);

/// <summary>Symbolic coverage/semantic comparison only, never deployment approval or geometry certification.</summary>
public sealed record PhysicalMappingReview(bool CoverageValid, bool SemanticallyCompatible,
    ImmutableArray<ValidationIssue> Issues);

/// <summary>Pack-scoped physical identity tools, independent of version-local resource IDs and runtime occupancy.</summary>
public static class PhysicalConflictMap
{
    /// <summary>Return common physical identities even when map versions or resource names differ. Cross-Pack input is rejected.</summary>
    public static ImmutableArray<string> SharedDomains(Topology left, string leftResourceId, Topology right, string rightResourceId)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Definition.PackId != right.Definition.PackId || left.Definition.PackKind != right.Definition.PackKind)
            throw new ArgumentException("Physical identities are scoped to the owning Pack.");
        var a = left.Definition.Resources.SingleOrDefault(x => x.Id == leftResourceId)
            ?? throw new ArgumentException("Unknown left resource.", nameof(leftResourceId));
        var b = right.Definition.Resources.SingleOrDefault(x => x.Id == rightResourceId)
            ?? throw new ArgumentException("Unknown right resource.", nameof(rightResourceId));
        return a.PhysicalConflictIds.Intersect(b.PhysicalConflictIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>Check exhaustive lineage and unchanged physical-domain unions; semantic changes cannot be called compatible.</summary>
    public static PhysicalMappingReview Review(Topology oldMap, Topology newMap, ImmutableArray<ResourceLineage> lineage)
    {
        ArgumentNullException.ThrowIfNull(oldMap);
        ArgumentNullException.ThrowIfNull(newMap);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        var coverage = true;
        var compatible = true;
        void Error(string code, string reference, bool invalidCoverage)
        {
            issues.Add(new(code, reference));
            compatible = false;
            if (invalidCoverage) coverage = false;
        }
        var old = oldMap.Definition;
        var next = newMap.Definition;
        if (old.PackId != next.PackId || old.PackKind != next.PackKind)
            Error("CrossPackMapping", $"{old.PackId}->{next.PackId}", true);
        if (old.LayerId != next.LayerId || old.CoordinateSystem != next.CoordinateSystem)
            Error("IncompatibleCoordinates", $"{old.LayerId}->{next.LayerId}", false);
        var before = old.Resources.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var after = next.Resources.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var visitedOld = new HashSet<string>(StringComparer.Ordinal);
        var visitedNew = new HashSet<string>(StringComparer.Ordinal);
        if (lineage.IsDefault)
        {
            Error("MissingLineage", "lineage", true);
            lineage = [];
        }
        HashSet<string> Visit(ImmutableArray<string> ids, Dictionary<string, RouteResource> resources, HashSet<string> visited, string side)
        {
            var physical = new HashSet<string>(StringComparer.Ordinal);
            if (ids.IsDefaultOrEmpty)
            {
                Error("EmptyLineage", side, true);
                return physical;
            }
            foreach (var id in ids)
            {
                if (id is null || !resources.TryGetValue(id, out var resource))
                {
                    Error("UnknownLineageResource", $"{side}/{id}", true);
                    continue;
                }
                if (!visited.Add(id))
                    Error("DuplicateLineageResource", $"{side}/{id}", true);
                physical.UnionWith(resource.PhysicalConflictIds);
            }
            return physical;
        }
        foreach (var link in lineage)
        {
            if (link is null)
            {
                Error("MissingLineage", "lineage/null", true);
                continue;
            }
            var a = Visit(link.OldResourceIds, before, visitedOld, "old");
            var b = Visit(link.NewResourceIds, after, visitedNew, "new");
            if (!a.SetEquals(b))
                Error("PhysicalCoverageMismatch", $"{string.Join(",", a.Order())}->{string.Join(",", b.Order())}", true);
            if (!link.OldResourceIds.IsDefault && !link.NewResourceIds.IsDefault
                && link.OldResourceIds.All(x => x is not null && before.ContainsKey(x))
                && link.NewResourceIds.All(x => x is not null && after.ContainsKey(x)))
            {
                if (!ResourceSemantics(link.OldResourceIds, before).SetEquals(ResourceSemantics(link.NewResourceIds, after))
                    || !ExecutionSemantics(old, link.OldResourceIds).SequenceEqual(ExecutionSemantics(next, link.NewResourceIds)))
                    Error("IncompatibleResourceOrExecution", "lineage/semantics", false);
            }
        }
        foreach (var id in before.Keys.Where(x => !visitedOld.Contains(x)))
            Error("MissingOldResource", $"old/{id}", true);
        foreach (var id in after.Keys.Where(x => !visitedNew.Contains(x)))
            Error("MissingNewResource", $"new/{id}", true);
        var oldDomains = old.PhysicalDomains.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var newDomains = next.PhysicalDomains.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (!oldDomains.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(newDomains.Keys))
            Error("PhysicalDomainCatalogChanged", "physicalDomains", false);
        foreach (var id in oldDomains.Keys.Intersect(newDomains.Keys))
            if (oldDomains[id] != newDomains[id])
                Error("IncompatiblePhysicalDomain", $"physicalDomains/{id}", false);
        if (!old.Capabilities.SequenceEqual(next.Capabilities))
            Error("IncompatibleCapabilityCatalog", "capabilities", false);
        return new(coverage, coverage && compatible, issues.ToImmutable());
    }

    private static HashSet<string> ResourceSemantics(ImmutableArray<string> ids, Dictionary<string, RouteResource> resources) =>
        ids.Select(id => JsonSerializer.Serialize(new { resources[id].Kind, resources[id].Capacity })).ToHashSet(StringComparer.Ordinal);

    private static string[] ExecutionSemantics(TopologyDefinition map, ImmutableArray<string> resourceIds)
    {
        var ids = resourceIds.ToHashSet(StringComparer.Ordinal);
        var nodeIds = map.Nodes.Where(x => x.ResourceIds.Any(ids.Contains)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var resources = map.Resources.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var physicalNodes = map.Nodes.ToDictionary(x => x.Id, x => JsonSerializer.Serialize(
            x.ResourceIds.SelectMany(id => resources[id].PhysicalConflictIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        var ambiguous = physicalNodes.Values.GroupBy(x => x, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        string Endpoint(string nodeId)
        {
            var physical = physicalNodes[nodeId];
            // Shared occupancy domains need not identify a unique endpoint. Without unique identity
            // evidence retain the local node ID, conservatively rejecting ambiguous renames.
            return JsonSerializer.Serialize(new { Physical = physical, LocalId = ambiguous.Contains(physical) ? nodeId : null });
        }
        return map.Edges.Where(x => x.ResourceIds.Any(ids.Contains) || nodeIds.Contains(x.From) || nodeIds.Contains(x.To))
            .Select(x => JsonSerializer.Serialize(new
            {
                From = Endpoint(x.From),
                To = Endpoint(x.To),
                x.Capacity,
                x.Direction,
                x.MaxLoad,
                x.RequiredCapabilities,
                x.ExecutionSegmentId,
                x.CommandMapping,
                x.Forbidden
            })).Order(StringComparer.Ordinal).ToArray();
    }
}
