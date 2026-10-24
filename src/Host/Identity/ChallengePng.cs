using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Wes.Host.Identity;

/// <summary>Small platform-independent raster renderer; the response contains pixels, never plaintext answers or SVG text.</summary>
public static class ChallengePng
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly string[] Glyphs =
    [
        "01110/10001/10001/11111/10001/10001/10001", "11110/10001/10001/11110/10001/10001/11110",
        "01111/10000/10000/10000/10000/10000/01111", "11110/10001/10001/10001/10001/10001/11110",
        "11111/10000/10000/11110/10000/10000/11111", "11111/10000/10000/11110/10000/10000/10000",
        "01111/10000/10000/10111/10001/10001/01111", "10001/10001/10001/11111/10001/10001/10001",
        "00111/00010/00010/00010/10010/10010/01100", "10001/10010/10100/11000/10100/10010/10001",
        "10000/10000/10000/10000/10000/10000/11111", "10001/11011/10101/10101/10001/10001/10001",
        "10001/11001/10101/10011/10001/10001/10001", "11110/10001/10001/11110/10000/10000/10000",
        "01110/10001/10001/10001/10101/10010/01101", "11110/10001/10001/11110/10100/10010/10001",
        "01111/10000/10000/01110/00001/00001/11110", "11111/00100/00100/00100/00100/00100/00100",
        "10001/10001/10001/10001/10001/10001/01110", "10001/10001/10001/10001/10001/01010/00100",
        "10001/10001/10001/10101/10101/11011/10001", "10001/10001/01010/00100/01010/10001/10001",
        "10001/10001/01010/00100/00100/00100/00100", "11111/00001/00010/00100/01000/10000/11111",
        "01110/10001/00001/00010/00100/01000/11111", "11110/00001/00001/01110/00001/00001/11110",
        "00010/00110/01010/10010/11111/00010/00010", "11111/10000/10000/11110/00001/00001/11110",
        "01110/10000/10000/11110/10001/10001/01110", "11111/00001/00010/00100/01000/01000/01000",
        "01110/10001/10001/01110/10001/10001/01110", "01110/10001/10001/01111/00001/00001/01110"
    ];

    /// <summary>Renders only the server-issued six-character alphabet, with cryptographic jitter/noise and no metadata chunks.</summary>
    public static byte[] Render(string answer)
    {
        if (answer.Length != 6 || answer.Any(character => !Alphabet.Contains(character)))
            throw new ArgumentException("A raster challenge requires a supported server answer.");
        const int width = 220, height = 64;
        var pixels = Enumerable.Repeat((byte)245, width * height * 3).ToArray();
        void Dot(int x, int y, byte color)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            var index = (y * width + x) * 3;
            pixels[index] = color; pixels[index + 1] = (byte)Math.Min(255, color + 15); pixels[index + 2] = (byte)Math.Min(255, color + 30);
        }
        for (var index = 0; index < 900; index++)
            Dot(RandomNumberGenerator.GetInt32(width), RandomNumberGenerator.GetInt32(height), (byte)RandomNumberGenerator.GetInt32(120, 220));
        for (var character = 0; character < 6; character++)
        {
            var glyph = Glyphs[Alphabet.IndexOf(answer[character])].Split('/');
            var left = 12 + character * 34 + RandomNumberGenerator.GetInt32(-3, 4);
            var top = RandomNumberGenerator.GetInt32(10, 24);
            var slant = RandomNumberGenerator.GetInt32(-2, 3);
            for (var y = 0; y < 7; y++)
                for (var x = 0; x < 5; x++)
                    if (glyph[y][x] == '1')
                        for (var dy = 0; dy < 4; dy++)
                            for (var dx = 0; dx < 4; dx++) Dot(left + x * 4 + dx + (y * slant / 3), top + y * 4 + dy, 35);
        }
        using var result = new MemoryStream();
        result.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(result, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels.AsSpan(y * width * 3, width * 3));
            }
        Chunk(result, "IDAT"u8, compressed.ToArray());
        Chunk(result, "IEND"u8, []);
        return result.ToArray();
    }
    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, data.Length);
        output.Write(integer); output.Write(type); output.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in type) crc = Update(crc, value);
        foreach (var value in data) crc = Update(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        output.Write(integer);
    }
    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320;
        return crc;
    }
}
