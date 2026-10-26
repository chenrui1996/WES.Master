using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Infrastructure.Files;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed class LocalQuarantineStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wes-quarantine-test-" + Guid.NewGuid().ToString("N"));

    public LocalQuarantineStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WritesAndReadsExactTenantLocalBytesWithoutOriginalFileName()
    {
        var store = Store();
        var tenant = Guid.NewGuid();
        var id = Guid.NewGuid();
        var data = "test data"u8.ToArray();
        var evidence = await store.WriteAsync(tenant, id, data, CancellationToken.None);
        var read = await store.ReadVerifiedAsync(tenant, id, evidence.Sha256, evidence.ByteCount, CancellationToken.None);

        Assert.Equal(data, read);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), evidence.Sha256);
        Assert.Equal(data.Length, evidence.ByteCount);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, tenant.ToString("N"))));
        await Assert.ThrowsAsync<QuarantineStorageException>(() =>
            store.ReadVerifiedAsync(Guid.NewGuid(), id, evidence.Sha256, evidence.ByteCount, CancellationToken.None));
    }

    [Fact]
    public async Task ObjectIdentityCannotBeOverwrittenAndOtherTenantCanUseSameIdentity()
    {
        var store = Store();
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = Guid.NewGuid();
        var evidence = await store.WriteAsync(tenant, id, "old"u8.ToArray(), CancellationToken.None);
        await Assert.ThrowsAsync<QuarantineStorageException>(() =>
            store.WriteAsync(tenant, id, "new"u8.ToArray(), CancellationToken.None));
        await store.WriteAsync(other, id, "new"u8.ToArray(), CancellationToken.None);
        Assert.Equal("old"u8.ToArray(),
            await store.ReadVerifiedAsync(tenant, id, evidence.Sha256, evidence.ByteCount, CancellationToken.None));
    }

    [Fact]
    public async Task TamperingAndTruncationAreExplicitIntegrityFailures()
    {
        var store = Store();
        var tenant = Guid.NewGuid();
        var id = Guid.NewGuid();
        var evidence = await store.WriteAsync(tenant, id, "data"u8.ToArray(), CancellationToken.None);
        var path = Path.Combine(_root, tenant.ToString("N"), id.ToString("N"));
        await File.WriteAllTextAsync(path, "evil");
        var error = await Assert.ThrowsAsync<QuarantineStorageException>(() =>
            store.ReadVerifiedAsync(tenant, id, evidence.Sha256, evidence.ByteCount, CancellationToken.None));
        Assert.Equal(QuarantineStorageFailure.Integrity, error.Failure);
        Assert.DoesNotContain(_root, error.ToString());
        Assert.Null(error.InnerException);
        await File.WriteAllTextAsync(path, "x");
        await Assert.ThrowsAsync<QuarantineStorageException>(() =>
            store.ReadVerifiedAsync(tenant, id, evidence.Sha256, evidence.ByteCount, CancellationToken.None));
    }

    [Fact]
    public async Task DeletionIsTenantLocalAndRepeatableButNotAnAuthorizationCheck()
    {
        var store = Store();
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = Guid.NewGuid();
        var evidence = await store.WriteAsync(other, id, "data"u8.ToArray(), CancellationToken.None);
        await store.DeleteAsync(tenant, id, CancellationToken.None);
        Assert.Equal("data"u8.ToArray(),
            await store.ReadVerifiedAsync(other, id, evidence.Sha256, evidence.ByteCount, CancellationToken.None));
        await store.DeleteAsync(other, id, CancellationToken.None);
        await store.DeleteAsync(other, id, CancellationToken.None);
        await Assert.ThrowsAsync<QuarantineStorageException>(() =>
            store.ReadVerifiedAsync(other, id, evidence.Sha256, evidence.ByteCount, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAndInvalidIdentitiesHaveNoStorageEffect()
    {
        var store = Store();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), "data"u8.ToArray(), cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteAsync(Guid.Empty, Guid.NewGuid(), "data"u8.ToArray(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteAsync(Guid.NewGuid(), Guid.Empty, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task EnforcesMaximumActualBytesAndNoZeroObjects()
    {
        var store = Store();
        var tenant = Guid.NewGuid();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.WriteAsync(tenant, id, Array.Empty<byte>(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.WriteAsync(tenant, id, new byte[20971521], CancellationToken.None));
        var data = new byte[20971520];
        var evidence = await store.WriteAsync(tenant, id, data, CancellationToken.None);
        Assert.Equal(data,
            await store.ReadVerifiedAsync(tenant, id, evidence.Sha256, evidence.ByteCount, CancellationToken.None));
    }

    [Fact]
    public void RequiresExistingExplicitAbsoluteRoot()
    {
        Assert.Throws<ArgumentException>(() => new LocalQuarantineStore("relative", NullLogger<LocalQuarantineStore>.Instance));
        var error = Assert.Throws<QuarantineStorageException>(() =>
            new LocalQuarantineStore(Path.Combine(_root, "absent"), NullLogger<LocalQuarantineStore>.Instance));
        Assert.DoesNotContain(_root, error.ToString());
    }

    private LocalQuarantineStore Store() => new(_root, NullLogger<LocalQuarantineStore>.Instance);

    public void Dispose() => Directory.Delete(_root, true);
}
