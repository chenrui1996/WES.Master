using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wes.Wcs.FourWay.Routing;

/// <summary>Pack discriminator; this FourWay-private implementation explicitly rejects Stacker and undefined kinds.</summary>
public enum PackKind { Stacker, FourWay }

/// <summary>Symbolic travel direction, not a controller command or geometric safety assertion.</summary>
public enum TravelDirection { Forward, Reverse, Turn, Lift }

/// <summary>Resource role; physical conflicts are identified independently of this local ID.</summary>
public enum ResourceKind { Node, Edge, Corridor, Intersection, NarrowAisle, Parking, Hoist, HoistPort, ExecutionSegment }

/// <summary>Pack-local physical domain. Revisions/bindings are owner evidence references, not measured geometry.</summary>
public sealed record PhysicalDomain(string Id, int Capacity, bool OpposingSharing, string GeometryRevision, string DeviceBinding);

/// <summary>A logical resource can cover multiple physical domains, which survive map renaming/splitting.</summary>
public sealed record RouteResource(string Id, ResourceKind Kind, int Capacity, ImmutableArray<string> PhysicalConflictIds);

/// <summary>Node occupancy resources occur at every visit. Device points must connect to a mapped edge.</summary>
public sealed record RouteNode(string Id, string PackId, string LayerId, bool IsDevicePoint, ImmutableArray<string> ResourceIds);

/// <summary>Nonnegative optimization components in the map's declared unit; never a constraint penalty.</summary>
public sealed record EdgeCost(double Base, double Congestion = 0, double Energy = 0)
{
    /// <summary>Sum of the components; map validation rejects nonfinite sums.</summary>
    public double Total => Base + Congestion + Energy;
}

/// <summary>Directed executable edge. Every edge has resources and an owner-supplied semantic command mapping.</summary>
public sealed record RouteEdge(
    string Id, string From, string To, EdgeCost Cost, int Capacity, TravelDirection Direction,
    double MaxLoad, ImmutableArray<string> RequiredCapabilities, ImmutableArray<string> ResourceIds,
    string ExecutionSegmentId, string CommandMapping, bool Forbidden = false);

/// <summary>Draft input copied into a validated immutable artifact. No active pointer is owned here.</summary>
public sealed record TopologyDefinition(
    string VersionId, string PackId, PackKind PackKind, string LayerId, string CoordinateSystem, string CostUnit,
    ImmutableArray<string> Capabilities, ImmutableArray<RouteNode> Nodes, ImmutableArray<RouteEdge> Edges,
    ImmutableArray<RouteResource> Resources, ImmutableArray<PhysicalDomain> PhysicalDomains);

/// <summary>Stable diagnostic code and reference chain suitable for an owner projection.</summary>
public sealed record ValidationIssue(string Code, string Reference);

/// <summary>Invalid artifacts cannot be published by this kernel; all discovered diagnostics are retained.</summary>
public sealed class TopologyValidationException(ImmutableArray<ValidationIssue> issues)
    : ArgumentException(string.Join("; ", issues.Select(x => $"{x.Code}:{x.Reference}")))
{
    /// <summary>Validation failures, never repaired by clipping or defaulting input values.</summary>
    public ImmutableArray<ValidationIssue> Issues { get; } = issues;
}

/// <summary>Immutable FourWay-owned artifact, safe for concurrent read-only planning; no shared topology or runtime occupancy.</summary>
public sealed class Topology
{
    /// <summary>Exact validated input, with collections canonically ordered.</summary>
    public TopologyDefinition Definition { get; }

    /// <summary>SHA-256 of canonical content including version, identity and all behavior fields.</summary>
    public string Hash { get; }

    private Topology(TopologyDefinition definition, string hash) => (Definition, Hash) = (definition, hash);

    /// <summary>Validate and freeze a draft or throw with reference diagnostics. No external effects.</summary>
    public static Topology Create(TopologyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string reference) => issues.Add(new(code, reference));
        bool Id(string? value, string reference)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            {
                Error("Required", reference);
                return false;
            }
            return true;
        }
        ImmutableArray<T> Items<T>(ImmutableArray<T> values, string reference) where T : class
        {
            if (values.IsDefault)
            {
                Error("MissingCollection", reference);
                return [];
            }
            if (values.Any(x => x is null))
                Error("NullElement", reference);
            return values.Where(x => x is not null).ToImmutableArray();
        }
        HashSet<string> Ids<T>(ImmutableArray<T> values, Func<T, string> key, string reference)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var id = key(value);
                if (Id(id, reference) && !ids.Add(id))
                    Error("DuplicateId", $"{reference}/{id}");
            }
            return ids;
        }
        void References(ImmutableArray<string> values, HashSet<string> known, string code, string reference, bool required)
        {
            var items = Items(values, reference);
            if (required && items.IsEmpty)
                Error("MissingResourceMapping", reference);
            Ids(items, x => x, reference);
            foreach (var value in items)
                if (!known.Contains(value))
                    Error(code, $"{reference}/{value}");
        }

        Id(definition.VersionId, "map/version");
        Id(definition.PackId, "map/pack");
        Id(definition.LayerId, "map/layer");
        Id(definition.CoordinateSystem, "map/coordinateSystem");
        Id(definition.CostUnit, "map/costUnit");
        if (definition.PackKind != PackKind.FourWay)
            Error("UnsupportedPack", "map/packKind");
        var nodes = Items(definition.Nodes, "nodes");
        var edges = Items(definition.Edges, "edges");
        var resources = Items(definition.Resources, "resources");
        var domains = Items(definition.PhysicalDomains, "physicalDomains");
        var capabilities = Items(definition.Capabilities, "capabilities");
        if (nodes.IsEmpty)
            Error("EmptyGraph", "nodes");
        var nodeIds = Ids(nodes, x => x.Id, "nodes");
        Ids(edges, x => x.Id, "edges");
        var resourceIds = Ids(resources, x => x.Id, "resources");
        var physicalIds = Ids(domains, x => x.Id, "physicalDomains");
        var capabilityIds = Ids(capabilities, x => x, "capabilities");
        foreach (var domain in domains)
        {
            if (domain.Capacity <= 0)
                Error("InvalidCapacity", $"physicalDomains/{domain.Id}/capacity");
            Id(domain.GeometryRevision, $"physicalDomains/{domain.Id}/geometryRevision");
            Id(domain.DeviceBinding, $"physicalDomains/{domain.Id}/deviceBinding");
        }
        foreach (var resource in resources)
        {
            var reference = $"resources/{resource.Id}";
            if (!Enum.IsDefined(resource.Kind))
                Error("InvalidResourceKind", reference);
            if (resource.Capacity <= 0)
                Error("InvalidCapacity", reference);
            if (resource.PhysicalConflictIds.IsDefaultOrEmpty)
                Error("MissingPhysicalMapping", reference);
            References(resource.PhysicalConflictIds, physicalIds, "UnknownPhysicalConflict", reference, true);
            foreach (var domain in domains.Where(x => !resource.PhysicalConflictIds.IsDefault && resource.PhysicalConflictIds.Contains(x.Id)))
                if (resource.Capacity > domain.Capacity)
                    Error("CapacityExceedsPhysical", $"{reference}/{domain.Id}");
        }
        foreach (var node in nodes)
        {
            var reference = $"nodes/{node.Id}";
            if (node.PackId != definition.PackId)
                Error("WrongPack", reference);
            if (node.LayerId != definition.LayerId)
                Error("WrongLayer", reference);
            References(node.ResourceIds, resourceIds, "UnknownResource", reference, true);
            if (node.IsDevicePoint && !edges.Any(x => x.From == node.Id || x.To == node.Id))
                Error("IsolatedDevice", reference);
        }
        foreach (var edge in edges)
        {
            var reference = $"edges/{edge.Id}";
            if (!nodeIds.Contains(edge.From) || !nodeIds.Contains(edge.To))
                Error("MissingEndpoint", $"{reference}/{edge.From}->{edge.To}");
            if (edge.Cost is null || !ValidCost(edge.Cost.Base) || !ValidCost(edge.Cost.Congestion)
                || !ValidCost(edge.Cost.Energy) || !ValidCost(edge.Cost.Total))
                Error("InvalidCost", $"{reference}/cost");
            if (edge.Capacity <= 0)
                Error("InvalidCapacity", $"{reference}/capacity");
            if (!ValidCost(edge.MaxLoad))
                Error("InvalidLoad", $"{reference}/maxLoad");
            if (!Enum.IsDefined(edge.Direction))
                Error("InvalidDirection", $"{reference}/direction");
            References(edge.RequiredCapabilities, capabilityIds, "UnknownCapability", reference, false);
            References(edge.ResourceIds, resourceIds, "UnknownResource", reference, true);
            foreach (var resource in resources.Where(x => !edge.ResourceIds.IsDefault && edge.ResourceIds.Contains(x.Id)))
                if (edge.Capacity > resource.Capacity)
                    Error("CapacityExceedsResource", $"{reference}/resources/{resource.Id}");
            if (!Id(edge.ExecutionSegmentId, $"{reference}/executionSegmentId"))
                Error("UnmappedSegment", reference);
            if (!Id(edge.CommandMapping, $"{reference}/commandMapping"))
                Error("UnmappedCommand", reference);
        }
        if (issues.Count != 0)
            throw new TopologyValidationException(issues.ToImmutable());

        static ImmutableArray<string> Sort(ImmutableArray<string> ids) => ids.Order(StringComparer.Ordinal).ToImmutableArray();
        var canonical = definition with
        {
            Capabilities = Sort(capabilities),
            Nodes = nodes.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => x with { ResourceIds = Sort(x.ResourceIds) }).ToImmutableArray(),
            Edges = edges.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => x with
            {
                RequiredCapabilities = Sort(x.RequiredCapabilities),
                ResourceIds = Sort(x.ResourceIds),
                Cost = new(Normalize(x.Cost.Base), Normalize(x.Cost.Congestion), Normalize(x.Cost.Energy))
            }).ToImmutableArray(),
            Resources = resources.OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => x with { PhysicalConflictIds = Sort(x.PhysicalConflictIds) }).ToImmutableArray(),
            PhysicalDomains = domains.OrderBy(x => x.Id, StringComparer.Ordinal).ToImmutableArray()
        };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
        return new(canonical, hash);
    }

    private static bool ValidCost(double value) => double.IsFinite(value) && value >= 0;
    private static double Normalize(double value) => value == 0 ? 0 : value;
}
