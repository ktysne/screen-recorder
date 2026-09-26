using System.Buffers.Binary;
using System.IO.Compression;

namespace ScreenRecorder.Core;

public static class PngWriter
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> bgraPixels, PngCompression compression)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("書き込み可能なストリームが必要です。", nameof(output));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var rowBytes = checked(width * 4);
        if (bgraPixels.Length != checked(rowBytes * height)) throw new ArgumentException("画素列の長さが画像サイズと一致しません。", nameof(bgraPixels));

        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, GetCompressionLevel(compression), leaveOpen: true))
        {
            var scanline = new byte[checked(width * 3 + 1)];
            for (var y = 0; y < height; y++)
            {
                scanline[0] = 0;
                var source = bgraPixels.Slice(y * rowBytes, rowBytes);
                for (var x = 0; x < width; x++)
                {
                    var sourceOffset = x * 4;
                    var targetOffset = 1 + x * 3;
                    scanline[targetOffset] = source[sourceOffset + 2];
                    scanline[targetOffset + 1] = source[sourceOffset + 1];
                    scanline[targetOffset + 2] = source[sourceOffset];
                }
                zlib.Write(scanline);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static CompressionLevel GetCompressionLevel(PngCompression compression) => compression switch
    {
        PngCompression.Fast => CompressionLevel.Fastest,
        PngCompression.Standard => CompressionLevel.Optimal,
        PngCompression.Smallest => CompressionLevel.SmallestSize,
        _ => throw new ArgumentOutOfRangeException(nameof(compression))
    };

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crc = uint.MaxValue;
        foreach (var value in type) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        output.Write(length);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 1 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
