using System.Security.Claims;

namespace Wes.Platform.Features.Tenancy;

/// <summary>A trusted actor/tenant context. No implicit shared tenant or client-supplied role set exists.</summary>
public sealed record TenantContext(Guid TenantId, Guid ActorId, Guid SessionId)
{
    /// <summary>Reads exactly one authenticated identity with unambiguous tenant, subject and session UUID claims.</summary>
    public static TenantContext FromValidatedPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identities = principal.Identities.ToArray();
        if (identities.Length != 1 || !identities[0].IsAuthenticated)
        {
            throw new UnauthorizedAccessException("One validated identity is required.");
        }

        return new TenantContext(Read(identities[0], "tenantId"), Read(identities[0], "sub"), Read(identities[0], "sid"));
    }

    private static Guid Read(ClaimsIdentity identity, string type)
    {
        var claims = identity.FindAll(type).ToArray();
        if (claims.Length != 1 || !Guid.TryParseExact(claims[0].Value, "D", out var value) || value == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Validated identity requires unambiguous nonempty UUID owner claims.");
        }

        return value;
    }
}
