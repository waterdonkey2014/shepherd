using System.IO.Compression;
namespace Shepherd.Engine.Render;

public static class Png
{
    static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
    public static void Write(string path, int w, int h, byte[] rgba, bool flipY = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13]; Be(ihdr, 0, (uint)w); Be(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); int sy = flipY ? h - 1 - y : y; z.Write(rgba, sy * w * 4, w * 4); }
        Chunk(fs, "IDAT", raw.ToArray()); Chunk(fs, "IEND", Array.Empty<byte>());
    }
    static void Be(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var hdr = new byte[8]; Be(hdr, 0, (uint)data.Length); for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
        s.Write(hdr); s.Write(data);
        uint c = 0xFFFFFFFF; foreach (var b in hdr.AsSpan(4)) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8); foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var cb = new byte[4]; Be(cb, 0, ~c); s.Write(cb);
    }
}
