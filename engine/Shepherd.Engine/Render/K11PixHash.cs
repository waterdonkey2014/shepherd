#if !SHEPHERD_EBOOT
namespace Shepherd.Engine.Render;

/// <summary>Kit #11 gpu1 (Mac measurement only; compiled out of the eboots): SHEPHERD_K11_PIXHASH=&lt;file&gt; writes one line per frame,
/// "&lt;frame&gt;\t&lt;64-bit hash of the main target's RGBA8 pixels&gt;", read back after the frame is complete. Two runs of a route whose lines agree
/// drew the same picture in every frame; a differing line names a frame to take a shot of (SHEPHERD_SHOT_FRAMES) and count its pixels.</summary>
[ProfileOnly]
public static class K11PixHash
{
    static readonly string Path0 = Environment.GetEnvironmentVariable("SHEPHERD_K11_PIXHASH");
    public static readonly bool On = !string.IsNullOrEmpty(Path0);
    static StreamWriter _w;
    public static void Frame(int i, IRenderBackend g)
    {
        if (g is MtBackend m) { m.Sync(); g = m.R; }
        var px = g.ReadPixels(out _, out _); if (px == null) return;
        ulong h = 14695981039346656037UL;
        var u = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(px.AsSpan(0, px.Length & ~7));
        foreach (var x in u) { h ^= x; h *= 1099511628211UL; h ^= h >> 29; }
        _w ??= new StreamWriter(Path0, false) { AutoFlush = true };
        _w.Write(i); _w.Write('\t'); _w.WriteLine(h.ToString("x16"));
    }
}
#endif
