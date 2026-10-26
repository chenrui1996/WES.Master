using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;

namespace Wes.Platform.Infrastructure.Files;

/// <summary>Bounded clamd INSTREAM adapter over an explicitly configured loopback socket, never a public scanner service.</summary>
public sealed class ClamAvFileScanner : IFileScanner
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClamAvFileScanner> _logger;

    /// <summary>Requires loopback because clamd TCP provides neither authentication nor encryption.</summary>
    public ClamAvFileScanner(IPAddress address, int port, TimeProvider clock, ILogger<ClamAvFileScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (!IPAddress.IsLoopback(address) || port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "An explicit loopback scanner endpoint is required.");
        _address = address;
        _port = port;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FileScanEvidence> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Length is < 1 or > FileContentValidator.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(content), "Scanner input must be bounded by the file policy.");
        var snapshot = content.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(snapshot));
        using var deadline = new CancellationTokenSource(QuarantinedFile.ScanTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var client = new TcpClient(_address.AddressFamily);
            await client.ConnectAsync(_address, _port, linked.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), linked.Token);
            var header = new byte[4];
            for (var offset = 0; offset < snapshot.Length;)
            {
                var count = Math.Min(65536, snapshot.Length - offset);
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)count);
                await stream.WriteAsync(header, linked.Token);
                await stream.WriteAsync(snapshot.AsMemory(offset, count), linked.Token);
                offset += count;
            }
            Array.Clear(header);
            await stream.WriteAsync(header, linked.Token);
            var reply = new byte[1024];
            var single = new byte[1];
            for (var index = 0; index < reply.Length; index++)
            {
                if (await stream.ReadAsync(single, linked.Token) == 0)
                    return Failure(FileScanVerdict.Unavailable, "truncated-reply");
                var value = single[0];
                if (value == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) return Failure(FileScanVerdict.TimedOut, "deadline");
                    var text = Encoding.ASCII.GetString(reply, 0, index);
                    if (text == "stream: OK") return new(FileScanVerdict.Clean, digest, snapshot.Length);
                    if (text.StartsWith("stream: ", StringComparison.Ordinal) && text.EndsWith(" FOUND", StringComparison.Ordinal)
                        && text.Length > "stream:  FOUND".Length)
                    {
                        _logger.LogWarning("File scan rejected content: {FailureCategory}.", "detected");
                        return new(FileScanVerdict.Infected, digest, snapshot.Length);
                    }
                    return Failure(FileScanVerdict.Unavailable, "unrecognized-reply");
                }
                if (value is < 32 or > 126)
                    return Failure(FileScanVerdict.Unavailable, "invalid-reply");
                reply[index] = value;
            }
            return Failure(FileScanVerdict.Unavailable, "reply-limit");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return Failure(FileScanVerdict.TimedOut, "deadline");
        }
        catch (SocketException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(deadline.IsCancellationRequested ? FileScanVerdict.TimedOut : FileScanVerdict.Unavailable, "socket");
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(deadline.IsCancellationRequested ? FileScanVerdict.TimedOut : FileScanVerdict.Unavailable, "transport");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot);
        }

        FileScanEvidence Failure(FileScanVerdict verdict, string category)
        {
            _logger.LogWarning("File scan could not clear quarantine: {FailureCategory}.", category);
            return new(verdict, digest, snapshot.Length);
        }
    }
}
