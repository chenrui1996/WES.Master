using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Files;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed class ClamAvFileScannerTests
{
    [Theory]
    [InlineData("stream: OK\0", FileScanVerdict.Clean)]
    [InlineData("stream: test-signature FOUND\0", FileScanVerdict.Infected)]
    [InlineData("stream: database unavailable ERROR\0", FileScanVerdict.Unavailable)]
    [InlineData("unrelated: OK\0", FileScanVerdict.Unavailable)]
    [InlineData("stream: not OK\0", FileScanVerdict.Unavailable)]
    [InlineData("stream: OK", FileScanVerdict.Unavailable)]
    public async Task UsesFramedStreamAndOnlyExactCleanReply(string reply, FileScanVerdict expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var data = Encoding.UTF8.GetBytes(new string('x', 70000));
        var server = ReceiveAsync(listener, data, Encoding.ASCII.GetBytes(reply));
        var scanner = Scanner(((IPEndPoint)listener.LocalEndpoint).Port);
        var evidence = await scanner.ScanAsync(data, CancellationToken.None);

        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, evidence.Verdict);
        Assert.Equal(data.Length, evidence.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), evidence.Sha256);
    }

    [Fact]
    public async Task OversizedOrNonAsciiReplyCannotClearQuarantine()
    {
        foreach (var reply in new[] { new string('A', 1025) + "\0", "stream: \u00ff FOUND\0" })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var data = "plain text"u8.ToArray();
            var server = ReceiveAsync(listener, data, Encoding.UTF8.GetBytes(reply));
            var evidence = await Scanner(((IPEndPoint)listener.LocalEndpoint).Port).ScanAsync(data, CancellationToken.None);
            Assert.Equal(FileScanVerdict.Unavailable, evidence.Verdict);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RefusedConnectionIsUnavailableNotClean()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var evidence = await Scanner(port).ScanAsync("text"u8.ToArray(), CancellationToken.None);
        Assert.Equal(FileScanVerdict.Unavailable, evidence.Verdict);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Scanner(1).ScanAsync("text"u8.ToArray(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineAndCallerCancellationStopAnActiveScanWithoutClearance(bool cancelCaller)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clock = new DeadlineClock();
        var scanner = new ClamAvFileScanner(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port,
            clock, NullLogger<ClamAvFileScanner>.Instance);
        using var cancellation = new CancellationTokenSource();
        var scan = scanner.ScanAsync("text"u8.ToArray(), cancellation.Token);
        using var connection = await listener.AcceptTcpClientAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller) cancellation.Cancel();
        else clock.Fire();

        if (cancelCaller)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(5)));
        else
            Assert.Equal(FileScanVerdict.TimedOut, (await scan.WaitAsync(TimeSpan.FromSeconds(5))).Verdict);
        Assert.Equal(TimeSpan.FromSeconds(60), clock.DueTime);
        Assert.True(clock.Timer.Disposed);
    }

    [Fact]
    public async Task EvidenceAndStreamUseSnapshotEvenIfCallerMutatesOriginalBuffer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var data = "original content"u8.ToArray();
        var expected = data.ToArray();
        var server = ReceiveAsync(listener, expected, "stream: OK\0"u8.ToArray());
        var scan = Scanner(((IPEndPoint)listener.LocalEndpoint).Port).ScanAsync(data, CancellationToken.None);
        Array.Fill(data, (byte)'x');
        var evidence = await scan;
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), evidence.Sha256);
        Assert.Equal(FileScanVerdict.Clean, evidence.Verdict);
    }

    [Theory]
    [InlineData("192.0.2.1", 3310)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    public void EndpointMustBeExplicitLocalAndValid(string address, int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClamAvFileScanner(IPAddress.Parse(address), port, TimeProvider.System, NullLogger<ClamAvFileScanner>.Instance));
    }

    [Fact]
    public async Task RejectsUnboundedOrEmptyInputBeforeOpeningConnection()
    {
        var scanner = Scanner(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scanner.ScanAsync(Array.Empty<byte>(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scanner.ScanAsync(new byte[QuarantinedFile.MaximumBytes + 1], CancellationToken.None));
    }

    private static ClamAvFileScanner Scanner(int port) =>
        new(IPAddress.Loopback, port, TimeProvider.System, NullLogger<ClamAvFileScanner>.Instance);

    private static async Task ReceiveAsync(TcpListener listener, byte[] expected, byte[] reply)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        await using var stream = client.GetStream();
        var command = new byte[10];
        await stream.ReadExactlyAsync(command, deadline.Token);
        Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));
        using var body = new MemoryStream();
        var header = new byte[4];
        while (true)
        {
            await stream.ReadExactlyAsync(header, deadline.Token);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size == 0) break;
            Assert.InRange(size, 1u, 65536u);
            var bytes = new byte[size];
            await stream.ReadExactlyAsync(bytes, deadline.Token);
            await body.WriteAsync(bytes, deadline.Token);
        }
        Assert.Equal(expected, body.ToArray());
        await stream.WriteAsync(reply, deadline.Token);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan DueTime { get; private set; }
        public DeadlineTimer Timer { get; } = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return Timer;
        }
        public void Fire() => _callback!(_state);
    }

    private sealed class DeadlineTimer : ITimer
    {
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
