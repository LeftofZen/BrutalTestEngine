using System.Buffers.Binary;
using System.IO.Compression;

namespace BrutalTestEngine.Engine;

/// <summary>
/// A minimal PNG writer, enough to hand a captured frame to a consumer that expects an image.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than pulled in as a dependency because the requirement is one call: 8-bit RGBA,
/// no interlacing, no metadata, no palette. A PNG under those constraints is a signature, one header chunk,
/// one compressed data chunk, one end chunk, and a CRC per chunk — around eighty lines. An imaging library
/// would add megabytes of native payload for the same output.
/// </para>
/// <para>
/// The compression is the framework's own zlib (<see cref="ZLibStream"/>), which is why chunk sizes are
/// computed rather than streamed: a PNG chunk carries its uncompressed and compressed lengths in its header,
/// and those precede the data.
/// </para>
/// </remarks>
internal static class PngEncoder
{
    /// <summary>The eight bytes every PNG starts with.</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Compresses <paramref name="data"/> as a zlib stream, which is what a PNG's IDAT holds.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);

        using (var deflate = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(data);

        return output.ToArray();
    }

    /// <summary>
    /// Builds a complete 8-bit RGBA PNG from already-compressed image data.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="compressed">The zlib-compressed scanlines, each prefixed by its filter byte.</param>
    public static byte[] Build(int width, int height, byte[] compressed)
    {
        using var png = new MemoryStream(compressed.Length + 128);

        png.Write(Signature);

        // IHDR: width, height, bit depth 8, colour type 6 (RGBA), then deflate/interlace/compression flags
        // that are all zero in every PNG written today.
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;

        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed);
        WriteChunk(png, "IEND"u8, []);

        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        destination.Write(length);

        destination.Write(type);
        destination.Write(data);

        // The CRC covers the chunk's type and data but not its length.
        var crc = Crc32.Compute(type, data);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        destination.Write(checksum);
    }

    /// <summary>
    /// The CRC-32 variant PNG requires, which reverses both the polynomial and the shift direction relative
    /// to the more common CRC-32/BZIP2.
    /// </summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = uint.MaxValue;

            crc = Accumulate(crc, first);
            crc = Accumulate(crc, second);

            return crc ^ uint.MaxValue;
        }

        private static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];

            for (var i = 0; i < table.Length; i++)
            {
                var value = (uint)i;

                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;

                table[i] = value;
            }

            return table;
        }
    }
}
