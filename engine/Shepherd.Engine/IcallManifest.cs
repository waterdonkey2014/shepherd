using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
namespace Shepherd.Engine;

/// <summary>One row of the Rewriter's hook manifest (runtime/ManagedVita/icalls.json), reduced to the fields the engine reads at boot:
/// the key the [Icall] attributes bind by, and what AutoProps / InertStubs look at. Row index = icall id.</summary>
public readonly struct IcallRow
{
    public readonly string Key, Type, Name, Ret;
    public readonly bool Static;
    public readonly int ParamCount;
    public IcallRow(string key, string type, string name, string ret, bool isStatic, int paramCount)
    { Key = key; Type = type; Name = name; Ret = ret; Static = isStatic; ParamCount = paramCount; }
}

/// <summary>kit #4 boot (B-1): the hook manifest, from icalls.json or from its bake, icalls.bin.
///
/// The device spent 32.7 s (launch clock, 2026-10-09 diag run) in IcallRegistry.Init; on the Mac's Mono interpreter half of that phase
/// is System.Text.Json parsing the 3.7 MB manifest (interpreted on the Vita too: System.Text.Json is not among the AOT modules), and
/// most of the rest is reading its fields back out of the JsonDocument. icalls.bin holds the same rows -- the same strings, the same
/// order -- in a flat binary form read with one call and no parser. It is a pure re-encoding of icalls.json (derived from the game's
/// DLLs, so it lives where icalls.json lives: runtime/ManagedVita on the Mac, ux0:data/shepherd/managed on the Vita, never in git or a
/// VPK) and is made by tools/IcallBake (vita/v6/sim.sh stage and vita/shepherd/stage-owner.sh run it next to the icalls.json they copy).
///
/// Format v2 (little-endian): "SHPICALL" | u32 version | u32 rows | i64 json length | u64 json content hash (<see cref="ContentHash"/>; v1: a
/// byte-wise FNV-1a 64) | u32 strings | u32 blob bytes
/// | u32 total file length | u32 reserved (0) || string lengths (u32 x strings) | UTF-8 blob || rows: key, type, name, ret (u32 string
/// indexes), u32 param count, u32 flags (bit 0: static; bit 1: the key is type + "::" + name + the key string, the Rewriter's own
/// key rule, so only an overload's "(param types)" suffix is stored -- half the file).
/// The engine uses a bake only when it was made from THIS icalls.json: same length and same content hash (k4 review: the length alone
/// accepted a bake made from another json of the same length -- two rows' keys swapped, a re-run Rewriter, a json copied to the card
/// without its bake -- and hooks would have bound to the wrong ids). That costs one read of the json (one call, ~0.35 s on the card)
/// and a word-wise hash (~1 M steps), not the parse. SHEPHERD_ICALL_BAKE=0 always parses icalls.json; =verify also parses it and
/// compares every row.</summary>
public static class IcallManifest
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("SHPICALL");
    public const int Version = 2, HeaderSize = 48;
    public static string BakePath(string jsonPath) => Path.Combine(Path.GetDirectoryName(jsonPath) ?? ".", "icalls.bin");

    /// <summary>The rows of icalls.json (one read of the whole file, then System.Text.Json).</summary>
    public static IcallRow[] FromJson(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new IcallRow[doc.RootElement.GetArrayLength()]; int i = 0;
        foreach (var r in doc.RootElement.EnumerateArray())
            rows[i++] = new IcallRow(r.GetProperty("key").GetString(), r.GetProperty("type").GetString(), r.GetProperty("name").GetString(),
                r.GetProperty("ret").GetString(), r.GetProperty("static").GetBoolean(), r.GetProperty("params").GetArrayLength());
        return rows;
    }

    /// <summary>The json's content hash: FNV-1a 64 over 32-bit little-endian words (4-byte steps: a quarter of a byte-wise FNV's work, and
    /// only aligned 32-bit loads, which the Vita's A9 does unaligned or not), the tail bytes one by one, then the length. Computed with
    /// 32-bit arithmetic only (<see cref="Fnv64"/>, kit #5): the same value as the 64-bit formula.</summary>
    public static ulong ContentHash(ReadOnlySpan<byte> b)
    {
        var f = Fnv64.Start;
        int n4 = b.Length & ~3;
        if (BitConverter.IsLittleEndian)   // (every target: a plain word loop over the array, no per-step slice)
            f.Words(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(b[..n4]));
        else
            for (int i = 0; i < n4; i += 4) f.Word(BinaryPrimitives.ReadUInt32LittleEndian(b[i..]));
        for (int i = n4; i < b.Length; i++) f.Word(b[i]);
        f.Word((uint)b.Length, (uint)((ulong)b.Length >> 32));
        return f.Value;
    }
#if !SHEPHERD_EBOOT
    /// <summary>The 64-bit formula ContentHash computes (the reference the Mac self-test compares <see cref="Fnv64"/> with; not in the Vita
    /// eboot, kit #5 review).</summary>
    public static ulong ContentHashReference(ReadOnlySpan<byte> b)
    {
        ulong h = 14695981039346656037UL;
        int n4 = b.Length & ~3;
        for (int i = 0; i < n4; i += 4) h = (h ^ BinaryPrimitives.ReadUInt32LittleEndian(b[i..])) * 1099511628211UL;
        for (int i = n4; i < b.Length; i++) h = (h ^ b[i]) * 1099511628211UL;
        return (h ^ (ulong)b.Length) * 1099511628211UL;
    }
#endif

    /// <summary>The bake of <paramref name="rows"/>, stamped with the length and <see cref="ContentHash"/> of the json they came from.</summary>
    public static byte[] Write(IcallRow[] rows, ReadOnlySpan<byte> json)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal); var strings = new List<string>();
        int S(string s) { s ??= ""; if (!index.TryGetValue(s, out var k)) { index[s] = k = strings.Count; strings.Add(s); } return k; }
        static string Stem(in IcallRow r) => r.Type + "::" + r.Name;
        var rowIdx = rows.Select(r => r.Key != null && r.Type != null && r.Name != null && r.Key.StartsWith(Stem(r), StringComparison.Ordinal)
            ? (k: S(r.Key[Stem(r).Length..]), t: S(r.Type), n: S(r.Name), rt: S(r.Ret), f: 2u)
            : (k: S(r.Key), t: S(r.Type), n: S(r.Name), rt: S(r.Ret), f: 0u)).ToArray();
        var enc = strings.Select(s => Encoding.UTF8.GetBytes(s)).ToArray();
        int blob = enc.Sum(e => e.Length);
        int total = HeaderSize + strings.Count * 4 + blob + rows.Length * 24;
        var o = new byte[total]; var w = o.AsSpan();
        Magic.CopyTo(w);
        BinaryPrimitives.WriteUInt32LittleEndian(w[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(w[12..], (uint)rows.Length);
        BinaryPrimitives.WriteInt64LittleEndian(w[16..], json.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(w[24..], ContentHash(json));
        BinaryPrimitives.WriteUInt32LittleEndian(w[32..], (uint)strings.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(w[36..], (uint)blob);
        BinaryPrimitives.WriteUInt32LittleEndian(w[40..], (uint)total);
        int p = HeaderSize;
        foreach (var e in enc) { BinaryPrimitives.WriteUInt32LittleEndian(w[p..], (uint)e.Length); p += 4; }
        foreach (var e in enc) { e.CopyTo(w[p..]); p += e.Length; }
        for (int i = 0; i < rows.Length; i++)
        {
            var (k, t, n, rt, f) = rowIdx[i];
            BinaryPrimitives.WriteUInt32LittleEndian(w[p..], (uint)k); BinaryPrimitives.WriteUInt32LittleEndian(w[(p + 4)..], (uint)t);
            BinaryPrimitives.WriteUInt32LittleEndian(w[(p + 8)..], (uint)n); BinaryPrimitives.WriteUInt32LittleEndian(w[(p + 12)..], (uint)rt);
            BinaryPrimitives.WriteUInt32LittleEndian(w[(p + 16)..], (uint)rows[i].ParamCount);
            BinaryPrimitives.WriteUInt32LittleEndian(w[(p + 20)..], (rows[i].Static ? 1u : 0u) | f);
            p += 24;
        }
        if (p != total) throw new InvalidOperationException($"icalls.bin writer: {p} of {total} bytes");
        return o;
    }

    /// <summary>The rows of a bake, or null with <paramref name="why"/> when it is not one this engine reads or was not made from a json
    /// of <paramref name="jsonLength"/> bytes (-1: do not check). <paramref name="jsonFnv"/>: the recorded <see cref="ContentHash"/> of that json.</summary>
    public static IcallRow[] Read(byte[] b, long jsonLength, out ulong jsonFnv, out string why)
    {
        jsonFnv = 0; why = null;
        var s = (ReadOnlySpan<byte>)b;
        if (s.Length < HeaderSize || !s[..8].SequenceEqual(Magic)) { why = "not an icalls.bin"; return null; }
        uint ver = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        if (ver != Version) { why = $"version {ver}, this engine reads {Version}"; return null; }
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        long jl = BinaryPrimitives.ReadInt64LittleEndian(s[16..]); jsonFnv = BinaryPrimitives.ReadUInt64LittleEndian(s[24..]);
        int ns = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[32..]), blob = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[36..]);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(s[40..]);
        if (total != (uint)s.Length || (long)HeaderSize + (long)ns * 4 + blob + (long)n * 24 != s.Length) { why = $"truncated or damaged ({s.Length} bytes, header says {total})"; return null; }
        if (jsonLength >= 0 && jl != jsonLength) { why = $"stale: made from a {jl}-byte icalls.json, this one has {jsonLength} bytes"; return null; }
        var str = new string[ns]; int lp = HeaderSize, bp = HeaderSize + ns * 4;
        for (int i = 0; i < ns; i++)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[lp..]); lp += 4;
            if (len < 0 || bp + len > HeaderSize + ns * 4 + blob) { why = "damaged string table"; return null; }
            str[i] = Encoding.UTF8.GetString(b, bp, len); bp += len;
        }
        var rows = new IcallRow[n]; int rp = bp;
        for (int i = 0; i < n; i++, rp += 24)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(s[rp..]), t = BinaryPrimitives.ReadUInt32LittleEndian(s[(rp + 4)..]);
            uint nm = BinaryPrimitives.ReadUInt32LittleEndian(s[(rp + 8)..]), rt = BinaryPrimitives.ReadUInt32LittleEndian(s[(rp + 12)..]);
            if (k >= ns || t >= ns || nm >= ns || rt >= ns) { why = $"damaged row {i}"; return null; }
            uint fl = BinaryPrimitives.ReadUInt32LittleEndian(s[(rp + 20)..]);
            rows[i] = new IcallRow((fl & 2) != 0 ? string.Concat(str[t], "::", str[nm], str[k]) : str[k], str[t], str[nm], str[rt], (fl & 1) != 0,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(s[(rp + 16)..]));
        }
        return rows;
    }

    /// <summary>First difference between two row sets, or null when they are equal field by field.</summary>
    public static string Diff(IcallRow[] a, IcallRow[] b)
    {
        if (a.Length != b.Length) return $"{a.Length} vs {b.Length} rows";
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i]; var y = b[i];
            if (!string.Equals(x.Key, y.Key, StringComparison.Ordinal) || !string.Equals(x.Type, y.Type, StringComparison.Ordinal) ||
                !string.Equals(x.Name, y.Name, StringComparison.Ordinal) || !string.Equals(x.Ret, y.Ret, StringComparison.Ordinal) ||
                x.Static != y.Static || x.ParamCount != y.ParamCount)
                return $"row {i}: '{x.Key}' vs '{y.Key}'";
        }
        return null;
    }
}

/// <summary>FNV-1a 64 (offset basis 14695981039346656037, prime 2^40 + 0x1B3) over 32-bit steps, with 32-bit arithmetic only (device kit #5,
/// vita/notes/kit5-boot.md). The Vita's AOT code (Mono without LLVM, ARM32) multiplies two 64-bit integers through a runtime helper
/// call (__emul_lmul) at every step, which made icalls.json's content check cost ~0.6 s of kit #4's 944 ms "read + hashed" on the device.
/// h * prime mod 2^64 = h * 0x1B3 + (h &lt;&lt; 40): with h = hi:lo, lo * 0x1B3 is two 16-bit partial products (each below 2^25), the shift
/// adds lo &lt;&lt; 8 to the high word, and hi * 0x1B3 is a plain 32-bit product. Bit for bit the 64-bit formula's value
/// (GxpCache.SelfTest, Mac: random buffers of every length class against IcallManifest.ContentHashReference).</summary>
public struct Fnv64
{
    public uint Lo, Hi;
    public static Fnv64 Start => new() { Lo = 0x84222325u, Hi = 0xCBF29CE4u };
    public readonly ulong Value => (ulong)Hi << 32 | Lo;
    /// <summary>h = (h ^ (hi:w)) * prime.</summary>
    public void Word(uint w, uint whi = 0)
    {
        uint lo = Lo ^ w, hi = Hi ^ whi;
        uint p0 = (lo & 0xFFFFu) * 0x1B3u, p1 = (lo >> 16) * 0x1B3u, nlo = p0 + (p1 << 16);
        Hi = (p1 >> 16) + (nlo < p0 ? 1u : 0u) + hi * 0x1B3u + (lo << 8);
        Lo = nlo;
    }
    /// <summary><see cref="Word"/> for each word, with the state in locals (the loop the boot's hashes spend their time in).</summary>
    public void Words(ReadOnlySpan<uint> w)
    {
        uint lo = Lo, hi = Hi;
        for (int i = 0; i < w.Length; i++)
        {
            lo ^= w[i];
            uint p0 = (lo & 0xFFFFu) * 0x1B3u, p1 = (lo >> 16) * 0x1B3u, nlo = p0 + (p1 << 16);
            hi = (p1 >> 16) + (nlo < p0 ? 1u : 0u) + hi * 0x1B3u + (lo << 8);
            lo = nlo;
        }
        Lo = lo; Hi = hi;
    }
    /// <summary><see cref="Words(ReadOnlySpan{uint})"/>, also OR-ing every word into <paramref name="any"/> (a plain-ASCII check in the same pass).</summary>
    public void Words(ReadOnlySpan<uint> w, ref uint any)
    {
        uint lo = Lo, hi = Hi, o = any;
        for (int i = 0; i < w.Length; i++)
        {
            uint x = w[i]; o |= x; lo ^= x;
            uint p0 = (lo & 0xFFFFu) * 0x1B3u, p1 = (lo >> 16) * 0x1B3u, nlo = p0 + (p1 << 16);
            hi = (p1 >> 16) + (nlo < p0 ? 1u : 0u) + hi * 0x1B3u + (lo << 8);
            lo = nlo;
        }
        Lo = lo; Hi = hi; any = o;
    }
}
