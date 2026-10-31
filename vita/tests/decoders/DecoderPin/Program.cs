using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Shepherd.Assets;
using Shepherd.Assets.Compression;

namespace Shepherd.DecoderPin;

/// <summary>
/// kit #10 fidelity (vita/notes/kit10-fidelity.md "2. Exact decoders"): every decoder Shepherd runs on game data, checked integer-exact against
/// the reference that IS (or stands for) the PC original's decoded output.
///   DecoderPin crunch &lt;game Data dir&gt; &lt;report&gt; &lt;crnref.dylib&gt; [-j N]
///       every DXT1Crunched / DXT5Crunched Texture2D in every bundle, EVERY mip level: Shepherd.Assets.Crunch.UnpackLevel (the managed port the
///       pack builder and the engine's GL path use) against Unity's own crn_decomp.h (crnref.dylib) -- byte for byte.
///   DecoderPin lz4 &lt;game Data dir&gt; &lt;report&gt; &lt;liblz4.dylib&gt; [-j N]
///       every LZ4 / LZ4HC block of every bundle: Shepherd.Assets.Compression.Lz4.Decode against the reference liblz4 (LZ4_decompress_safe).
///   DecoderPin ima &lt;audio bake set dir&gt; &lt;report&gt; &lt;imaref.dylib&gt;
///       every IMA ADPCM take (RIFF/WAVE 0x11): the Vita's decoder (vita/audio/src/sa_codec.c via imaref.dylib) against the bake's own model
///       (tools/FmodBake/Encode.cs ImaDecode) -- every sample, sequentially and from 3 seek points per take (block start, mid-block, the last
///       block); plus the pcm16 / aac-adts take counts (aac: no exact decoder on the Vita side -- the notes).
/// Exit 0 = every comparison identical. The report names assets (owner-local); the summary line is what the notes quote.
/// DECODERPIN_FAULT=1 (the negative test): one bit of every managed decode is flipped before the comparison -- every item must then differ.
/// </summary>
static class Program
{
    static readonly bool Fault = Environment.GetEnvironmentVariable("DECODERPIN_FAULT") == "1";
    static int Main(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: DecoderPin crunch|lz4|ima <in> <report> <reference dylib> [-j N]"); return 2; }
        int jobs = 4;
        for (int i = 4; i + 1 < args.Length; i++) if (args[i] == "-j") jobs = int.Parse(args[i + 1]);
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) =>
            name is "crnref" or "imaref" or "lz4ref" ? NativeLibrary.Load(args[3]) : IntPtr.Zero);
        var sw = Stopwatch.StartNew();
        using var rep = new StreamWriter(args[2]) { AutoFlush = true };
        int rc = args[0] switch
        {
            "crunch" => CrunchPin(args[1], rep, jobs),
            "lz4" => Lz4Pin(args[1], rep, jobs),
            "ima" => ImaPin(args[1], rep),
            _ => 2,
        };
        Say(rep, $"elapsed {sw.Elapsed.TotalSeconds:F1} s; exit {rc}");
        return rc;
    }

    static readonly object Out = new();
    static void Say(StreamWriter rep, string s) { lock (Out) { Console.WriteLine(s); rep.WriteLine(s); } }

    // ------------------------------------------------------------------ crunch

    [DllImport("crnref")] static extern unsafe int crnref_info(byte* data, uint size, out int w, out int h, out int levels, out int faces, out int fmt);
    [DllImport("crnref")] static extern unsafe int crnref_unpack(byte* data, uint size, int level, byte* outp, uint outSize);

    static int CrunchPin(string data, StreamWriter rep, int jobs)
    {
        var bundleDir = Path.Combine(data, "StreamingAssets", "aa", "StandaloneOSX");
        var paths = Directory.GetFiles(bundleDir, "*.bundle").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        long textures = 0, levels = 0, bytes = 0, mismatchLevels = 0, mismatchTextures = 0, errors = 0, skippedFaces = 0, other = 0;
        var formats = new Dictionary<string, long>();
        var seen = new HashSet<string>();   // the same texture (content hash) in two bundles is counted once
        long dupes = 0;
        var lk = new object();
        Say(rep, $"crunch pin: {paths.Length} bundles in {bundleDir}; reference = Unity's crn_decomp.h (crnref.dylib)");
        Parallel.ForEach(Partitioner(paths, 64), new ParallelOptions { MaxDegreeOfParallelism = jobs }, chunk =>
        {
            using var db = new AssetDatabase(data);
            foreach (var bp in chunk)
            {
                IReadOnlyList<SerializedFile> files;
                try { files = db.LoadBundleFiles(bp); }
                catch (Exception e) { lock (lk) errors++; Say(rep, $"ERROR {Path.GetFileName(bp)}: {e.Message}"); continue; }
                foreach (var f in files)
                    foreach (var o in f.ObjectsOfClass(ClassIds.Texture2D))
                    {
                        TypeTreeObject t;
                        try { t = TypeTreeReader.Read(f, o); }
                        catch (Exception e) { lock (lk) errors++; Say(rep, $"ERROR read {Path.GetFileName(bp)} #{o.PathId}: {e.Message}"); continue; }
                        var fmt = (TextureFormat)t.Int("m_TextureFormat");
                        if (fmt is not (TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched)) { lock (lk) other++; continue; }
                        byte[] crn;
                        try { crn = TextureDecoder.GetImageData(db, f, t); }
                        catch (Exception e) { lock (lk) errors++; Say(rep, $"ERROR data {t.Str("m_Name")} ({Path.GetFileName(bp)}): {e.Message}"); continue; }
                        string key = Convert.ToHexString(SHA256.HashData(crn));
                        lock (lk) { if (!seen.Add(key)) { dupes++; continue; } }
                        var (lv, mm, by, fmts, faces, err) = CompareCrunch(crn);
                        lock (lk)
                        {
                            textures++; levels += lv; bytes += by; mismatchLevels += mm; if (mm > 0) mismatchTextures++; if (faces > 1) skippedFaces++;
                            formats[fmts] = formats.GetValueOrDefault(fmts) + 1;
                            if (err != null) errors++;
                        }
                        if (mm > 0 || err != null)
                            Say(rep, $"{(mm > 0 ? "MISMATCH" : "ERROR")} {t.Str("m_Name")} ({Path.GetFileName(bp)} #{o.PathId}) {t.Int("m_Width")}x{t.Int("m_Height")} {fmts}: {mm} of {lv} levels differ{(err != null ? "; " + err : "")}");
                    }
                db.ReleaseIdle(long.MaxValue);
            }
        });
        Say(rep, $"crunch pin: {textures} crunched textures ({dupes} duplicates by content skipped; {other} other Texture2Ds), {levels} mip levels, " +
                 $"{bytes:N0} DXT bytes compared: {mismatchLevels} levels differ in {mismatchTextures} textures; {errors} errors; formats " +
                 string.Join(", ", formats.OrderBy(k => k.Key).Select(k => $"{k.Key} x{k.Value}")) + (skippedFaces > 0 ? $"; {skippedFaces} with >1 face (face 0 only)" : ""));
        return mismatchLevels == 0 && errors == 0 ? 0 : 1;
    }

    static unsafe (int levels, int mismatches, long bytes, string fmt, int faces, string err) CompareCrunch(byte[] crn)
    {
        int w, h, lv, faces, cfmt;
        fixed (byte* p = crn)
            if (crnref_info(p, (uint)crn.Length, out w, out h, out lv, out faces, out cfmt) != 0) return (0, 0, 0, "?", 0, "crn_decomp: no texture info");
        var hdr = Crunch.ReadHeader(crn);
        if (hdr.Width != w || hdr.Height != h || hdr.Levels != lv || (int)hdr.Format != cfmt)
            return (0, 0, 0, hdr.Format.ToString(), faces, $"header differs: managed {hdr.Width}x{hdr.Height} L{hdr.Levels} {hdr.Format}, crn_decomp {w}x{h} L{lv} fmt {cfmt}");
        int mm = 0; long bytes = 0; string err = null;
        for (int l = 0; l < lv; l++)
        {
            byte[] managed;
            try { managed = Crunch.UnpackLevel(crn, l).blocks; }
            catch (Exception e) { err = $"managed level {l}: {e.Message}"; mm++; continue; }
            if (Fault && managed.Length > 0) managed[managed.Length / 2] ^= 1;
            var native = new byte[Math.Max(16, managed.Length)];
            int n;
            fixed (byte* p = crn) fixed (byte* q = native) n = crnref_unpack(p, (uint)crn.Length, l, q, (uint)native.Length);
            if (n < 0) { err = $"crn_decomp level {l}: error {n}"; mm++; continue; }
            bytes += n;
            if (n != managed.Length || !native.AsSpan(0, n).SequenceEqual(managed)) mm++;
        }
        return (lv, mm, bytes, hdr.Format.ToString(), faces, err);
    }

    static IEnumerable<string[]> Partitioner(string[] all, int n)
    {
        for (int i = 0; i < all.Length; i += n) yield return all.Skip(i).Take(n).ToArray();
    }

    // ------------------------------------------------------------------ lz4

    [DllImport("lz4ref")] static extern unsafe int LZ4_decompress_safe(byte* src, byte* dst, int compressedSize, int dstCapacity);

    static int Lz4Pin(string data, StreamWriter rep, int jobs)
    {
        var bundleDir = Path.Combine(data, "StreamingAssets", "aa", "StandaloneOSX");
        var paths = Directory.GetFiles(bundleDir, "*.bundle").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        long blocks = 0, lz4Blocks = 0, stored = 0, inBytes = 0, outBytes = 0, mismatches = 0, errors = 0;
        var kinds = new Dictionary<uint, long>();
        var lk = new object();
        var blocksField = typeof(BundleFile).GetField("_blocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Say(rep, $"lz4 pin: {paths.Length} bundles; reference = liblz4 LZ4_decompress_safe");
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = jobs }, bp =>
        {
            try
            {
                using var b = BundleFile.Open(bp);
                var arr = (Array)blocksField.GetValue(b);
                using var fs = File.OpenRead(bp);
                long lb = 0, l4 = 0, st = 0, ib = 0, ob = 0, mm = 0;
                var lkinds = new Dictionary<uint, long>();
                foreach (var blk in arr)
                {
                    var tp = blk.GetType();
                    long co = (long)tp.GetField("COffset").GetValue(blk);
                    uint us = (uint)tp.GetField("USize").GetValue(blk), cs = (uint)tp.GetField("CSize").GetValue(blk);
                    ushort fl = (ushort)tp.GetField("Flags").GetValue(blk);
                    uint comp = fl & BundleFile.CompressionMask;
                    lb++; lkinds[comp] = lkinds.GetValueOrDefault(comp) + 1;
                    if (comp == 0) { st++; continue; }
                    if (comp is not (2 or 3)) continue;
                    var src = new byte[cs];
                    fs.Position = co; fs.ReadExactly(src);
                    var a = new byte[us]; var r = new byte[us];
                    int na = Lz4.Decode(src, a), nr;
                    if (Fault && us > 0) a[us / 2] ^= 1;
                    unsafe { fixed (byte* s = src) fixed (byte* d = r) nr = LZ4_decompress_safe(s, d, (int)cs, (int)us); }
                    l4++; ib += cs; ob += us;
                    if (na != nr || nr != us || !a.AsSpan().SequenceEqual(r)) { mm++; Say(rep, $"MISMATCH {Path.GetFileName(bp)} block @{co}: managed {na} B, liblz4 {nr} B, expected {us}"); }
                }
                lock (lk)
                {
                    blocks += lb; lz4Blocks += l4; stored += st; inBytes += ib; outBytes += ob; mismatches += mm;
                    foreach (var kv in lkinds) kinds[kv.Key] = kinds.GetValueOrDefault(kv.Key) + kv.Value;
                }
            }
            catch (Exception e) { lock (lk) errors++; Say(rep, $"ERROR {Path.GetFileName(bp)}: {e.Message}"); }
        });
        Say(rep, $"lz4 pin: {blocks} storage blocks (" + string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"type {k.Key} x{k.Value}")) +
                 $"): {lz4Blocks} LZ4 blocks, {inBytes:N0} -> {outBytes:N0} bytes, {mismatches} differ; {stored} stored; {errors} errors");
        return mismatches == 0 && errors == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ IMA ADPCM

    [DllImport("imaref")] static extern unsafe int imaref_decode(byte* data, uint dataBytes, int blockBytes, int fpb, int ch, uint startFrame, short* dst, int frames);
    [DllImport("imaref")] static extern int imaref_fpb(int blockBytes, int ch);

    static int ImaPin(string dir, StreamWriter rep)
    {
        long ima = 0, pcm = 0, aac = 0, otherFiles = 0, samples = 0, seeks = 0, mismatchTakes = 0, errors = 0;
        long aacBytes = 0, imaBytes = 0, pcmBytes = 0;
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Say(rep, $"ima pin: {files.Count} files under {dir}; the Vita's decoder (sa_codec.c via imaref.dylib) vs the bake's model (FmodBake Encode.ImaDecode)");
        foreach (var p in files)
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext is ".aac" or ".adts") { aac++; aacBytes += new FileInfo(p).Length; continue; }
            if (ext != ".wav") { otherFiles++; continue; }
            byte[] b = File.ReadAllBytes(p);
            if (!ParseWav(b, out int fmt, out int ch, out int align, out int spb, out int fact, out int dOff, out int dLen)) { errors++; Say(rep, $"ERROR {Rel(dir, p)}: not a RIFF/WAVE take"); continue; }
            if (fmt == 1) { pcm++; pcmBytes += b.Length; continue; }
            if (fmt != 0x11) { otherFiles++; continue; }
            ima++; imaBytes += b.Length;
            int frames = fact >= 0 ? fact : dLen / align * spb;
            // the bake's model, recovered as integers (ImaDecode returns sample / 32767f: exact round trip for every int16)
            var refF = Shepherd.FmodBake.Encode.ImaDecode(b.AsSpan(dOff, dLen), ch, align, spb, frames);
            var refI = new short[refF.Length];
            for (int i = 0; i < refF.Length; i++) refI[i] = (short)Math.Round(refF[i] * 32767.0);
            if (Fault && refI.Length > 100) refI[100] ^= 1;
            int fpb = imaref_fpb(align, ch);
            if (fpb != spb) { errors++; Say(rep, $"ERROR {Rel(dir, p)}: frames per block {fpb} (Vita) vs {spb} (header)"); continue; }
            var vita = new short[frames * ch];
            int got;
            unsafe { fixed (byte* d = b) fixed (short* o = vita) got = imaref_decode(d + dOff, (uint)dLen, align, spb, ch, 0, o, frames); }
            bool bad = got != frames || !vita.AsSpan().SequenceEqual(refI);
            samples += (long)frames * ch;
            // seek points: a block start, mid-block, inside the last block (sa_adpcm_seek + decode-and-drop, the mixer's loop / random access)
            foreach (int sp in new[] { Math.Min(frames - 1, spb), Math.Min(frames - 1, spb / 2 + 7), Math.Max(0, frames - 37) })
            {
                if (sp < 0 || sp >= frames) continue;
                int n = Math.Min(4096, frames - sp);
                var part = new short[n * ch];
                int g2;
                unsafe { fixed (byte* d = b) fixed (short* o = part) g2 = imaref_decode(d + dOff, (uint)dLen, align, spb, ch, (uint)sp, o, n); }
                seeks++;
                if (g2 != n || !part.AsSpan().SequenceEqual(refI.AsSpan(sp * ch, n * ch))) { bad = true; Say(rep, $"  seek {sp}: differs"); }
            }
            if (bad)
            {
                mismatchTakes++;
                int first = -1; for (int i = 0; i < Math.Min(vita.Length, refI.Length); i++) if (vita[i] != refI[i]) { first = i; break; }
                Say(rep, $"MISMATCH {Rel(dir, p)}: decoded {got} of {frames} frames, first differing sample {first}");
            }
        }
        Say(rep, $"ima pin: {ima} IMA ADPCM takes ({imaBytes / 1048576.0:F1} MiB), {samples:N0} samples + {seeks} seek points compared: {mismatchTakes} takes differ; " +
                 $"{pcm} pcm16 takes ({pcmBytes / 1048576.0:F1} MiB, no decoding); {aac} aac-adts takes ({aacBytes / 1048576.0:F1} MiB: sceAudiodec on the Vita, " +
                 $"no exact reference -- notes); {otherFiles} other files; {errors} errors");
        return mismatchTakes == 0 && errors == 0 ? 0 : 1;
    }

    static string Rel(string dir, string p) => Path.GetRelativePath(dir, p);

    static bool ParseWav(byte[] b, out int fmt, out int ch, out int align, out int spb, out int fact, out int dOff, out int dLen)
    {
        fmt = ch = align = spb = 0; fact = -1; dOff = -1; dLen = 0;
        if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return false;
        int p = 12;
        while (p + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, p, 4); int len = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ")
            {
                fmt = BitConverter.ToUInt16(b, p + 8); ch = BitConverter.ToUInt16(b, p + 10); align = BitConverter.ToUInt16(b, p + 20);
                if (fmt == 0x11 && len >= 20) spb = BitConverter.ToUInt16(b, p + 26);
            }
            else if (id == "fact") fact = BitConverter.ToInt32(b, p + 8);
            else if (id == "data") { dOff = p + 8; dLen = Math.Min(len, b.Length - dOff); }
            p += 8 + len + (len & 1);
        }
        return dOff >= 0 && ch > 0;
    }
}
