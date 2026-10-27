using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Shepherd.Assets;

/// <summary>Minimal RGBA8 PNG encoder (filter: Sub per row, zlib via ZLibStream).</summary>
public static class PngWriter
{
    private static readonly uint[] s_crc = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var fs = File.Create(path);
        Write(fs, width, height, rgba);
    }

    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("rgba buffer too small");
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(output, "IHDR", ihdr);

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = width * 4;
            var row = new byte[stride + 1];
            for (int y = 0; y < height; y++)
            {
                var src = rgba.Slice(y * stride, stride);
                row[0] = 1; // Sub
                for (int i = 0; i < 4 && i < stride; i++) row[1 + i] = src[i];
                for (int i = 4; i < stride; i++) row[1 + i] = (byte)(src[i] - src[i - 4]);
                z.Write(row, 0, row.Length);
            }
        }
        Chunk(output, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));
        Chunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(hdr, data.Length);
        Encoding.ASCII.GetBytes(type, hdr[4..]);
        s.Write(hdr);
        s.Write(data);
        uint crc = Crc(0xFFFFFFFFu, hdr[4..]);
        crc = Crc(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }
}
