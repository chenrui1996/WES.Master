using System.Collections.Immutable;
using System.Text.Json;

namespace Wes.Platform.Domain;

/// <summary>Minimal owner authorization evidence for membership replay: role references, never request bodies or grant snapshots.</summary>
public sealed class DirectoryDelegationRequirement
{
    private DirectoryDelegationRequirement() { }
    internal DirectoryDelegationRequirement(Guid tenant, Guid actor, Guid operation, IEnumerable<Guid> roles)
    {
        var required = roles.Distinct().Order().ToArray();
        if (tenant == Guid.Empty || actor == Guid.Empty || operation == Guid.Empty || required.Any(id => id == Guid.Empty))
            throw new ArgumentException("Delegation evidence requires trusted owner identities.");
        TenantId = tenant; ActorId = actor; OperationId = operation;
        RoleIdsData = JsonSerializer.Serialize(required);
    }
    /// <summary>Trusted tenant ownership.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Original authenticated command actor.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Actor-scoped command UUID, with the same retention as its receipt.</summary>
    public Guid OperationId { get; private set; }
    private string RoleIdsData { get; set; } = "[]";
    /// <summary>Original affected role identities; their current rules must still be delegable on replay.</summary>
    public ImmutableArray<Guid> RoleIds => (JsonSerializer.Deserialize<Guid[]>(RoleIdsData)
        ?? throw new InvalidOperationException("Stored delegation references are invalid.")).ToImmutableArray();
}
