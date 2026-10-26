using System.Security.Cryptography;
using Wes.Platform.Features.Files;

namespace Wes.Host.Platform;

/// <summary>Owned raw upload body. At most M+1 bytes are read to decide the boundary; disposal clears all capacity.</summary>
public sealed class FileUploadBody : IDisposable
{
    private byte[]? _buffer;
    private readonly int _count;
    private FileUploadBody(byte[] buffer, int count) { _buffer = buffer; _count = count; }
    /// <summary>Immutable transport view; caller must dispose after the application finishes its own snapshot.</summary>
    public ReadOnlyMemory<byte> Content => (_buffer ?? throw new ObjectDisposedException(nameof(FileUploadBody))).AsMemory(0, _count);
    /// <summary>Reads bounded short/chunked streams without relying on Content-Length, and never owns the request stream.</summary>
    public static async Task<FileUploadBody> ReadAsync(Stream stream, long? contentLength, CancellationToken ct)
    {
        if (contentLength is < 0) throw new BadHttpRequestException("Invalid upload length.");
        if (contentLength > FileContentValidator.MaximumBytes)
            throw new BadHttpRequestException("Upload size exceeds policy.", StatusCodes.Status413PayloadTooLarge);
        var buffer = new byte[4096];
        var retained = false;
        try
        {
            var count = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (count == buffer.Length)
                {
                    var larger = new byte[Math.Min(buffer.Length * 2, FileContentValidator.MaximumBytes + 1)];
                    buffer.CopyTo(larger, 0);
                    CryptographicOperations.ZeroMemory(buffer);
                    buffer = larger;
                }
                var read = await stream.ReadAsync(buffer.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
                if (count > FileContentValidator.MaximumBytes)
                    throw new BadHttpRequestException("Upload size exceeds policy.", StatusCodes.Status413PayloadTooLarge);
            }
            ct.ThrowIfCancellationRequested();
            if (contentLength.HasValue && count != contentLength.Value)
                throw new BadHttpRequestException("Upload length does not match the request.");
            retained = true;
            return new(buffer, count);
        }
        finally
        {
            if (!retained) CryptographicOperations.ZeroMemory(buffer);
        }
    }
    /// <summary>Clears buffers even when a retained view outlives its request or a response fails.</summary>
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) CryptographicOperations.ZeroMemory(buffer);
    }
}
