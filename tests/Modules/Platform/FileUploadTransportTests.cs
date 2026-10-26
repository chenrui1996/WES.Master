using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wes.Host.Platform;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Production stream boundaries, retained-buffer clearing, and strict deployment input without network substitutes.</summary>
public sealed class FileUploadTransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    [Trait("CaseId", "HTTP-FILE-03")]
    public async Task ShortReadsPreserveExactContentAndDisposeClearsRetainedMemory(long? length)
    {
        using var source = new ObservedStream(7);
        var body = await FileUploadBody.ReadAsync(source, length, CancellationToken.None);
        var retained = body.Content;
        Assert.Equal("aaaaaaa"u8.ToArray(), retained.ToArray());
        body.Dispose();
        body.Dispose();
        Assert.All(retained.ToArray(), value => Assert.Equal(0, value));
        Assert.All(source.Buffers, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        Assert.Throws<ObjectDisposedException>(() => body.Content);
        Assert.False(source.WasDisposed);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("io")]
    [InlineData("mismatch")]
    [Trait("CaseId", "HTTP-FILE-03")]
    public async Task FailedReadClearsAllObservedBuffersWithoutOwningRequestStream(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new ObservedStream(7, failure == "cancel" ? cancellation : null, failure == "io");
        var task = FileUploadBody.ReadAsync(source, failure == "mismatch" ? 8 : null, cancellation.Token);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (failure == "io") await Assert.ThrowsAsync<IOException>(() => task);
        else await Assert.ThrowsAsync<BadHttpRequestException>(() => task);
        Assert.NotEmpty(source.Buffers);
        Assert.All(source.Buffers, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        Assert.False(source.WasDisposed);
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-03")]
    public async Task UnknownLengthOversizeReadsExactlyMPlusOneNeverTheRestAndClearsEveryAllocation()
    {
        using var source = new ObservedStream(30_000_000, maximumChunk: 65536);
        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            FileUploadBody.ReadAsync(source, null, CancellationToken.None));
        Assert.Equal(413, error.StatusCode);
        Assert.Equal(20_971_521, source.ReadCount);
        Assert.All(source.Buffers, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-03")]
    public async Task DeclaredOversizeIsRejectedBeforeReadingAnyByte()
    {
        using var source = new ObservedStream(30_000_000);
        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            FileUploadBody.ReadAsync(source, 20_971_521, CancellationToken.None));
        Assert.Equal(413, error.StatusCode);
        Assert.Equal(0, source.ReadCount);
    }

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("ScannerAddress", "192.0.2.1")]
    [InlineData("ScannerAddress", "localhost")]
    [InlineData("ScannerPort", "0")]
    [InlineData("ScannerPort", "65536")]
    [InlineData("StorageRoot", "relative")]
    [InlineData("Unknown", "true")]
    [InlineData("Enabled:Nested", "true")]
    [Trait("CaseId", "HTTP-FILE-01")]
    public void DisabledDeploymentStillRejectsMalformedSuppliedValues(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [$"Wes:Files:{key}"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => PlatformFileSettings.Load(configuration, new FixtureEnvironment()));
    }

    [Theory]
    [InlineData("missing-identity")]
    [InlineData("missing-root")]
    [InlineData("missing-address")]
    [InlineData("missing-port")]
    [InlineData("public-child")]
    [InlineData("public-parent")]
    [InlineData("ops-child")]
    [Trait("CaseId", "HTTP-FILE-01")]
    public void EnabledDeploymentRequiresIndependentPrivateStorageAndEveryExplicitCapability(string failure)
    {
        var environment = new FixtureEnvironment();
        var values = new Dictionary<string, string?>
        {
            ["Wes:Files:Enabled"] = "true",
            ["Wes:Identity:Enabled"] = "true",
            ["Wes:Files:StorageRoot"] = Path.Combine(Path.GetTempPath(), "wes-test-private"),
            ["Wes:Files:ScannerAddress"] = "127.0.0.1",
            ["Wes:Files:ScannerPort"] = "3310",
            ["Wes:Web:Root"] = Path.Combine(Path.GetTempPath(), "wes-ops-public")
        };
        if (failure == "missing-identity") values.Remove("Wes:Identity:Enabled");
        if (failure == "missing-root") values.Remove("Wes:Files:StorageRoot");
        if (failure == "missing-address") values.Remove("Wes:Files:ScannerAddress");
        if (failure == "missing-port") values.Remove("Wes:Files:ScannerPort");
        if (failure == "public-child") values["Wes:Files:StorageRoot"] = Path.Combine(environment.ContentRootPath, "private");
        if (failure == "public-parent") values["Wes:Files:StorageRoot"] = Path.GetTempPath();
        if (failure == "ops-child") values["Wes:Files:StorageRoot"] = Path.Combine(values["Wes:Web:Root"]!, "private");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<InvalidOperationException>(() => PlatformFileSettings.Load(configuration, environment));
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-01")]
    public void ConfigurationKeysFollowDotNetCaseInsensitiveSemanticsAndFormattingCannotRevealPaths()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["wes:files:enabled"] = "true",
            ["wes:identity:enabled"] = "true",
            ["wes:files:storageroot"] = Path.Combine(Path.GetTempPath(), "wes-test-private"),
            ["wes:files:scanneraddress"] = "::1",
            ["wes:files:scannerport"] = "65535"
        }).Build();
        var settings = PlatformFileSettings.Load(configuration, new FixtureEnvironment());
        Assert.True(settings.Enabled);
        Assert.DoesNotContain(settings.StorageRoot!, settings.ToString(), StringComparison.Ordinal);
    }

    private sealed class FixtureEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Wes.Host";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "wes-test-host");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ObservedStream(int total, CancellationTokenSource? cancel = null, bool fail = false,
        int maximumChunk = 3) : Stream
    {
        public List<Memory<byte>> Buffers { get; } = [];
        public int ReadCount { get; private set; }
        public bool WasDisposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Buffers.Add(buffer);
            if (ReadCount > 0 && fail) throw new IOException("synthetic request failure");
            var count = Math.Min(Math.Min(buffer.Length, maximumChunk), total - ReadCount);
            buffer.Span[..count].Fill((byte)'a');
            ReadCount += count;
            if (ReadCount > 0 && cancel is not null) cancel.Cancel();
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
