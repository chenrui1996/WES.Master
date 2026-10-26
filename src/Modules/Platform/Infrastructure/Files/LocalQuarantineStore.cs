using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Wes.Platform.Features.Files;

namespace Wes.Platform.Infrastructure.Files;

/// <summary>Finite failures safe to map without exposing storage paths or exception messages.</summary>
public enum QuarantineStorageFailure
{
    /// <summary>The configured store or requested object cannot be accessed.</summary>
    Unavailable,
    /// <summary>The object does not match its durable identity, or a link/path boundary is unsafe.</summary>
    Integrity
}

/// <summary>Safe storage failure, deliberately without a raw provider exception or path.</summary>
public sealed class QuarantineStorageException(QuarantineStorageFailure failure) : Exception("Quarantine storage operation failed.")
{
    /// <summary>Safe reason category; never a raw filesystem message.</summary>
    public QuarantineStorageFailure Failure { get; } = failure;
}

/// <summary>Private bounded local store; the deployment must restrict root ACLs and keep it outside all static web roots.</summary>
public sealed class LocalQuarantineStore : IQuarantineStore
{
    private readonly string _root;
    private readonly ILogger<LocalQuarantineStore> _logger;

    /// <summary>Requires an existing absolute deployment root without reparse points in its ancestry.</summary>
    public LocalQuarantineStore(string root, ILogger<LocalQuarantineStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("An explicit absolute private storage root is required.", nameof(root));
        _root = Path.GetFullPath(root);
        _logger = logger;
        try
        {
            if (!Directory.Exists(_root)) throw Failure(QuarantineStorageFailure.Unavailable);
            EnsureDirectoryChain();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Failure(QuarantineStorageFailure.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<QuarantineStorageEvidence> WriteAsync(Guid tenant, Guid file, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentities(tenant, file);
        if (content.Length is < 1 or > FileContentValidator.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(content));
        var snapshot = content.ToArray();
        try
        {
            var path = ObjectPath(tenant, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureNoLink(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(snapshot, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            return new(Convert.ToHexString(SHA256.HashData(snapshot)), snapshot.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(QuarantineStorageFailure.Unavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadVerifiedAsync(Guid tenant, Guid file, string sha256, long byteCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentities(tenant, file);
        if (byteCount is < 1 or > FileContentValidator.MaximumBytes
            || sha256 is null || sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterLower(character)))
            throw new ArgumentException("Valid owner length and SHA256 evidence are required.");
        byte[]? content = null;
        var verified = false;
        try
        {
            var path = ObjectPath(tenant, file);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != byteCount) throw Failure(QuarantineStorageFailure.Integrity);
            content = new byte[(int)byteCount];
            await stream.ReadExactlyAsync(content, cancellationToken);
            if (Convert.ToHexString(SHA256.HashData(content)) != sha256)
                throw Failure(QuarantineStorageFailure.Integrity);
            cancellationToken.ThrowIfCancellationRequested();
            verified = true;
            return content;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(QuarantineStorageFailure.Unavailable);
        }
        finally
        {
            if (!verified && content is not null) CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid tenant, Guid file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentities(tenant, file);
        try
        {
            var path = ObjectPath(tenant, file);
            if (Directory.Exists(Path.GetDirectoryName(path))) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Failure(QuarantineStorageFailure.Unavailable);
        }
        return Task.CompletedTask;
    }

    private string ObjectPath(Guid tenant, Guid file)
    {
        EnsureDirectoryChain();
        var directory = Path.Combine(_root, tenant.ToString("N"));
        var path = Path.Combine(directory, file.ToString("N"));
        if (Directory.Exists(directory)) EnsureNoLink(directory);
        if (File.Exists(path) || Directory.Exists(path)) EnsureNoLink(path);
        return path;
    }

    private void EnsureDirectoryChain()
    {
        for (var directory = new DirectoryInfo(_root); directory is not null; directory = directory.Parent)
            EnsureNoLink(directory.FullName);
    }

    private void EnsureNoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Failure(QuarantineStorageFailure.Integrity);
    }

    private static void ValidateIdentities(Guid tenant, Guid file)
    {
        if (tenant == Guid.Empty || file == Guid.Empty)
            throw new ArgumentException("Explicit trusted tenant and server file identities are required.");
    }

    private QuarantineStorageException Failure(QuarantineStorageFailure failure)
    {
        _logger.LogWarning("Quarantine storage failed: {FailureCategory}.", failure);
        return new(failure);
    }
}
