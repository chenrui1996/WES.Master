using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Directory;

/// <summary>Optional request-local operation identity, never trusted actor, authorization or caller-supplied digest.</summary>
public sealed record DirectoryOperation(Guid OperationId);

/// <summary>Module-local receipt primitives. Call only within an explicit clean owner transaction after current authorization.</summary>
public static class DirectoryCommandReceipts
{
    /// <summary>Ordinary safe results expire after exactly 90 days; domain unique keys never expire with them.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Hashes only named nonsecret owner arguments; callers must canonicalize unordered collections first.</summary>
    public static string Fingerprint(DirectoryCommandKind kind, params object?[] nonsecretValues)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Kind = kind.ToString(), Values = nonsecretValues })));
    }

    internal static object[] CanonicalRules(IEnumerable<PermissionGrant> rules) => rules.Select(rule => new
    {
        rule.Permission,
        rule.Scope,
        Departments = rule.Departments.Order().ToArray(),
        Fields = rule.Fields.Order(StringComparer.Ordinal).ToArray()
    }).OrderBy(rule => JsonSerializer.Serialize(rule), StringComparer.Ordinal).Cast<object>().ToArray();

    /// <summary>Reads an actor-scoped unexpired result; caller must reauthorize its current target before returning any fields.</summary>
    public static async Task<DirectoryCommandReceipt?> FindAsync(PlatformDbContext database, TenantContext context,
        DirectoryOperation operation, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Guard(database, context, operation);
        var actor = context.ActorId;
        var id = operation.OperationId;
        var receipt = await database.CommandReceipts
            .Where(ScopeExpressions.MatchIds<DirectoryCommandReceipt>(x => x.ActorId, [actor]))
            .SingleOrDefaultAsync(ScopeExpressions.MatchIds<DirectoryCommandReceipt>(x => x.OperationId, [id]), cancellationToken);
        if (receipt is not null && receipt.CreatedAt + Retention <= now)
        {
            var proof = await database.CommandDelegations
                .Where(ScopeExpressions.MatchIds<DirectoryDelegationRequirement>(x => x.ActorId, [actor]))
                .SingleOrDefaultAsync(ScopeExpressions.MatchIds<DirectoryDelegationRequirement>(x => x.OperationId, [id]), cancellationToken);
            if (proof is not null) database.CommandDelegations.Remove(proof);
            database.CommandReceipts.Remove(receipt);
            // Flush the old principal before reusing its key: otherwise its FK cascade can remove newly staged evidence.
            // This remains inside the caller's transaction and rolls back with any later command failure.
            await database.SaveChangesAsync(cancellationToken);
            return null;
        }
        return receipt;
    }

    /// <summary>Stages only a committed-safe changed result; SaveChanges and commit remain the owner's atomic responsibility.</summary>
    public static void Stage(PlatformDbContext database, TenantContext context, DirectoryOperation operation,
        DirectoryCommandKind kind, string fingerprint, Guid resultId, string resultVersion, DateTimeOffset now)
    {
        Guard(database, context, operation);
        if (!Enum.IsDefined(kind) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)
            || resultId == Guid.Empty || !Guid.TryParse(resultVersion, out _))
            throw new ArgumentException("A safe nonsecret receipt result is required.");
        database.CommandReceipts.Add(new DirectoryCommandReceipt(database.TenantId, context.ActorId,
            operation.OperationId, kind, fingerprint, resultId, resultVersion, now));
    }

    /// <summary>Stages at most 200 expired receipt removals for this tenant; caller saves/commits in its owner transaction.</summary>
    public static async Task<int> RemoveExpiredAsync(PlatformDbContext database, DateTimeOffset now, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Receipt retention requires an explicit owner transaction.");
        var cutoff = now - Retention;
        var receipts = await database.CommandReceipts.Where(x => x.CreatedAt <= cutoff)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.ActorId).ThenBy(x => x.OperationId)
            .Take(limit).ToListAsync(cancellationToken);
        database.CommandReceipts.RemoveRange(receipts);
        return receipts.Count;
    }

    private static void Guard(PlatformDbContext database, TenantContext context, DirectoryOperation operation)
    {
        if (operation.OperationId == Guid.Empty || context.ActorId == Guid.Empty || context.TenantId != database.TenantId)
            throw new ArgumentException("A trusted tenant/actor and operation UUID are required.");
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Receipts must share the explicit business/audit owner transaction.");
    }
}
