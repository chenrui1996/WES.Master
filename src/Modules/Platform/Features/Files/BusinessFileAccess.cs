using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Files;

/// <summary>Immutable owner/entity reference selected by reviewed composition, not a caller-selected authorization policy.</summary>
public sealed record FileEntityReference
{
    /// <summary>Requires a canonical owner code and a nonempty stable business identity.</summary>
    public FileEntityReference(string owner, Guid entityId)
    {
        Owner = DirectoryValue.Code(owner);
        if (entityId == Guid.Empty) throw new ArgumentException("A business entity identity is required.", nameof(entityId));
        EntityId = entityId;
    }

    /// <summary>Fixed business owner/type code; never a storage path.</summary>
    public string Owner { get; }
    /// <summary>Identity interpreted only by the named business owner.</summary>
    public Guid EntityId { get; }
}

/// <summary>Additional live entity authority; implementations use only the owning module's public application boundary.</summary>
public interface IBusinessFileAuthorization
{
    /// <summary>Revalidates the trusted subject and exact entity on every call; errors and cancellation must propagate.</summary>
    Task<bool> CanReadAsync(TenantContext context, FileEntityReference entity, CancellationToken cancellationToken);
}

/// <summary>Bound-file reads only: exact durable binding, current entity authority and current file authority must all hold.</summary>
public sealed class BusinessFileAccess
{
    private readonly StagingFileAccess _access;

    internal BusinessFileAccess(PlatformDbContext database, FileEntityReference entity, IBusinessFileAuthorization authorization,
        IQuarantineStore storage, IFileScanner scanner, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(authorization);
        _access = new(database, storage, scanner, clock, logs, entity, authorization);
    }

    /// <summary>Returns the same fixed safe metadata projection without paths, digests or unrelated business bindings.</summary>
    public Task<FileAccessResult> ReadAsync(TenantContext context, Guid fileId, CancellationToken ct = default) =>
        _access.ReadAsync(context, fileId, ct);

    /// <summary>Queries only this entity's exact bindings with current entity authority and per-field file scopes before paging.</summary>
    public Task<StagingFilePage> QueryAsync(TenantContext context, int offset = 0, int limit = 25,
        int? state = null, CancellationToken ct = default) =>
        _access.QueryAsync(context, offset, limit, state, ct);

    /// <summary>Reuses verified download/audit/zeroing and rechecks entity and binding authority after storage IO.</summary>
    public Task<FileDownloadResult> DownloadAsync(TenantContext context, Guid fileId, Guid correlation, CancellationToken ct = default) =>
        _access.DownloadAsync(context, fileId, correlation, ct);
}
