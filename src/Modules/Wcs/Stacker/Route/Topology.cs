using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wes.Wcs.Stacker.Routing;

/// <summary>Stacker motion primitives are owner-defined semantics, not PLC instructions or geometric guarantees.</summary>
public enum TravelDirection { Forward, Reverse, Turn, Lift }

/// <summary>Pack-private resource roles include device execution segments and double-deep location groups.</summary>
public enum ResourceKind { Node, Corridor, Intersection, ExecutionSegment, LocationGroup }

/// <summary>Stable Pack-local physical identity; owner evidence must support geometry, capacity and device binding.</summary>
public sealed record PhysicalDomain(string Id, int Capacity, bool OpposingSharing, string GeometryRevision, string DeviceBinding);

/// <summary>Logical resource mapped to physical domains independently of MapVersion.</summary>
public sealed record RouteResource(string Id, ResourceKind Kind, int Capacity, ImmutableArray<string> PhysicalConflictIds);

/// <summary>Stacker device/location point with explicit Pack/aisle identity and occupancy mapping.</summary>
public sealed record RouteNode(string Id, string PackId, string AisleId, bool IsDevicePoint, ImmutableArray<string> ResourceIds);

/// <summary>Finite nonnegative optimization terms; never used to override constraints.</summary>
public sealed record EdgeCost(double Base, double Congestion = 0, double Energy = 0)
{
    /// <summary>Optimization total in the map's declared cost unit.</summary>
    public double Total => Base + Congestion + Energy;
}

/// <summary>Directed Stacker leg mapped to a concrete device and ExeStackCode. Neither is a dispatch authorization.</summary>
public sealed record RouteEdge(string Id, string From, string To, EdgeCost Cost, int Capacity,
    TravelDirection Direction, double MaxLoad, ImmutableArray<string> RequiredCapabilities,
    ImmutableArray<string> ResourceIds, string ExeStackCode, string DeviceCode, string CommandMapping, bool Forbidden = false);

/// <summary>Pack-private immutable draft. A Stacker aisle is not a FourWay layer or a shared topology.</summary>
public sealed record TopologyDefinition(string VersionId, string PackId, string AisleId,
    string CoordinateSystem, string CostUnit, ImmutableArray<string> Capabilities, ImmutableArray<RouteNode> Nodes,
    ImmutableArray<RouteEdge> Edges, ImmutableArray<RouteResource> Resources, ImmutableArray<PhysicalDomain> PhysicalDomains,
    ImmutableArray<string> DeviceCodes);

/// <summary>Stable failure code with an object/reference chain for Pack diagnostics.</summary>
public sealed record ValidationIssue(string Code, string Reference);

/// <summary>Invalid artifacts are rejected without repairs or partial publication.</summary>
public sealed class TopologyValidationException(ImmutableArray<ValidationIssue> issues)
    : ArgumentException(string.Join("; ", issues.Select(x => $"{x.Code}:{x.Reference}")))
{
    /// <summary>All discovered validation issues.</summary>
    public ImmutableArray<ValidationIssue> Issues { get; } = issues;
}

/// <summary>Immutable Stacker-owned topology; no global active map, occupancy, reservation or other Pack reference.</summary>
public sealed class Topology
{
    /// <summary>Canonical validated artifact.</summary>
    public TopologyDefinition Definition { get; }
    /// <summary>SHA-256 content identity including version, Pack and all constraints/mappings.</summary>
    public string Hash { get; }

    private Topology(TopologyDefinition definition, string hash) => (Definition, Hash) = (definition, hash);

    /// <summary>Validate and freeze input or throw reference diagnostics; no external effects.</summary>
    public static Topology Create(TopologyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string reference) => errors.Add(new(code, reference));
        bool Id(string? id, string reference)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Trim() != id)
            {
                Error("Required", reference);
                return false;
            }
            return true;
        }
        ImmutableArray<T> Items<T>(ImmutableArray<T> items, string reference) where T : class
        {
            if (items.IsDefault)
            {
                Error("MissingCollection", reference);
                return [];
            }
            if (items.Any(x => x is null)) Error("NullElement", reference);
            return items.Where(x => x is not null).ToImmutableArray();
        }
        HashSet<string> Ids<T>(ImmutableArray<T> items, Func<T, string> key, string reference)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var id = key(item);
                if (Id(id, reference) && !ids.Add(id)) Error("DuplicateId", $"{reference}/{id}");
            }
            return ids;
        }
        void References(ImmutableArray<string> input, HashSet<string> known, string code, string reference, bool required)
        {
            var items = Items(input, reference);
            Ids(items, x => x, reference);
            if (required && items.IsEmpty) Error("MissingResourceMapping", reference);
            foreach (var id in items)
                if (!known.Contains(id)) Error(code, $"{reference}/{id}");
        }
        Id(definition.VersionId, "map/version");
        Id(definition.PackId, "map/pack");
        Id(definition.AisleId, "map/aisle");
        Id(definition.CoordinateSystem, "map/coordinateSystem");
        Id(definition.CostUnit, "map/costUnit");
        var nodes = Items(definition.Nodes, "nodes");
        var edges = Items(definition.Edges, "edges");
        var resources = Items(definition.Resources, "resources");
        var domains = Items(definition.PhysicalDomains, "physicalDomains");
        var capabilities = Items(definition.Capabilities, "capabilities");
        var devices = Items(definition.DeviceCodes, "devices");
        if (nodes.IsEmpty) Error("EmptyGraph", "nodes");
        var nodeIds = Ids(nodes, x => x.Id, "nodes");
        Ids(edges, x => x.Id, "edges");
        var resourceIds = Ids(resources, x => x.Id, "resources");
        var domainIds = Ids(domains, x => x.Id, "physicalDomains");
        var capabilityIds = Ids(capabilities, x => x, "capabilities");
        var deviceIds = Ids(devices, x => x, "devices");
        foreach (var domain in domains)
        {
            if (domain.Capacity <= 0) Error("InvalidCapacity", $"physicalDomains/{domain.Id}");
            Id(domain.GeometryRevision, $"physicalDomains/{domain.Id}/geometryRevision");
            Id(domain.DeviceBinding, $"physicalDomains/{domain.Id}/deviceBinding");
        }
        foreach (var resource in resources)
        {
            var reference = $"resources/{resource.Id}";
            if (!Enum.IsDefined(resource.Kind)) Error("InvalidResourceKind", reference);
            if (resource.Capacity <= 0) Error("InvalidCapacity", reference);
            if (resource.PhysicalConflictIds.IsDefaultOrEmpty) Error("MissingPhysicalMapping", reference);
            References(resource.PhysicalConflictIds, domainIds, "UnknownPhysicalConflict", reference, true);
            foreach (var domain in domains.Where(x => !resource.PhysicalConflictIds.IsDefault && resource.PhysicalConflictIds.Contains(x.Id)))
                if (resource.Capacity > domain.Capacity) Error("CapacityExceedsPhysical", $"{reference}/{domain.Id}");
        }
        foreach (var node in nodes)
        {
            var reference = $"nodes/{node.Id}";
            if (node.PackId != definition.PackId) Error("WrongPack", reference);
            if (node.AisleId != definition.AisleId) Error("WrongAisle", reference);
            References(node.ResourceIds, resourceIds, "UnknownResource", reference, true);
            if (node.IsDevicePoint && !edges.Any(x => x.From == node.Id || x.To == node.Id))
                Error("IsolatedDevice", reference);
        }
        foreach (var edge in edges)
        {
            var reference = $"edges/{edge.Id}";
            if (!nodeIds.Contains(edge.From) || !nodeIds.Contains(edge.To)) Error("MissingEndpoint", reference);
            if (edge.Cost is null || !Finite(edge.Cost.Base) || !Finite(edge.Cost.Congestion)
                || !Finite(edge.Cost.Energy) || !Finite(edge.Cost.Total)) Error("InvalidCost", reference);
            if (edge.Capacity <= 0) Error("InvalidCapacity", reference);
            if (!Finite(edge.MaxLoad)) Error("InvalidLoad", reference);
            if (!Enum.IsDefined(edge.Direction)) Error("InvalidDirection", reference);
            References(edge.RequiredCapabilities, capabilityIds, "UnknownCapability", reference, false);
            References(edge.ResourceIds, resourceIds, "UnknownResource", reference, true);
            foreach (var resource in resources.Where(x => !edge.ResourceIds.IsDefault && edge.ResourceIds.Contains(x.Id)))
                if (edge.Capacity > resource.Capacity) Error("CapacityExceedsResource", $"{reference}/{resource.Id}");
            if (!Id(edge.ExeStackCode, $"{reference}/exeStackCode")) Error("UnmappedExeStack", reference);
            if (!Id(edge.DeviceCode, $"{reference}/deviceCode")) Error("UnmappedDevice", reference);
            if (!deviceIds.Contains(edge.DeviceCode)) Error("UnknownDevice", $"{reference}/devices/{edge.DeviceCode}");
            if (!Id(edge.CommandMapping, $"{reference}/commandMapping")) Error("UnmappedCommand", reference);
        }
        if (errors.Count != 0) throw new TopologyValidationException(errors.ToImmutable());
        static ImmutableArray<string> Sort(ImmutableArray<string> ids) => ids.Order(StringComparer.Ordinal).ToImmutableArray();
        static double Zero(double value) => value == 0 ? 0 : value;
        var canonical = definition with
        {
            Capabilities = Sort(capabilities),
            DeviceCodes = Sort(devices),
            Nodes = nodes.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => x with { ResourceIds = Sort(x.ResourceIds) }).ToImmutableArray(),
            Edges = edges.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => x with
            {
                ResourceIds = Sort(x.ResourceIds),
                RequiredCapabilities = Sort(x.RequiredCapabilities),
                Cost = new(Zero(x.Cost.Base), Zero(x.Cost.Congestion), Zero(x.Cost.Energy))
            }).ToImmutableArray(),
            Resources = resources.OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => x with { PhysicalConflictIds = Sort(x.PhysicalConflictIds) }).ToImmutableArray(),
            PhysicalDomains = domains.OrderBy(x => x.Id, StringComparer.Ordinal).ToImmutableArray()
        };
        return new(canonical, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))));
    }

    private static bool Finite(double value) => double.IsFinite(value) && value >= 0;
}
