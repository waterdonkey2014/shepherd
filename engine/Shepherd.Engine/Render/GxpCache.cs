using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
namespace Shepherd.Engine.Render;

/// <summary>Device kit #5 (track boot, vita/notes/kit5-boot.md): the compiled shaders kept on the owner's card, so vitaGL does not
/// compile GlBackend's 8 programs with libshacccg at every start (device kit #4: 7.0-7.7 s of the ~66-76 s to the first frame).
/// Only with the Vita GL host (SHEPHERD_GL_HOST=shepgl); <c>SHEPHERD_GXP_CACHE=0</c> (env file) = kit #4 exactly: no file, no extra call.
///
/// One file holds the whole program set, keyed by everything a compiled shader depends on: the exact text of every vertex and fragment
/// shader handed to the driver (prelude + variant, as Build makes it), the attribute bindings, and the native side's identity line
/// (<see cref="ShepGl.GxpIdentity"/>: the GL stack's builds, each libshacccg.suprx by size + hash, vitaGL's live compiler options and
/// semantic mode). The file stores that key in full and is used only when it is byte-for-byte the key of this start; its checksum, its
/// lengths and every shader blob (shepgl_gxp_check: the GXP header, sceGxmProgramCheck, the type, the uniform indices) are checked first.
/// All or nothing: a start either loads every program of the set from the file or compiles every one from source exactly as kit #4
/// (the same calls in the same order), so the binaries always come from one complete compile sequence.
///
/// A start is one of:
/// - miss (no usable file): compile as before; after each link the two compiled shaders are read back (vglGetShaderBinary, read-only)
///   and, when every program built, the file is written (temp file, read back, then renamed over the old one) as UNVERIFIED;
/// - verify (the file exists but this device has not confirmed it, or <c>SHEPHERD_GXP_CACHE=verify</c>): compile as before and compare
///   what the compiler gave with the file, shader by shader. All identical = the file is marked VERIFIED; any difference = the file
///   is replaced by this compile's output (unverified again) and a WRN says which;
/// - hit (a verified file): no compile; each shader object gets its program through vitaGL's glShaderBinary, then is linked exactly
///   as a compiled one. So the first start that draws with cached binaries comes after this console proved them identical to its
///   compiler's own output. A shader that the native side refuses to load falls back to compiling that program from source (WRN) and
///   the file is dropped (the next start compiles the full set again).
/// Every case writes one line: "[gl] gxp cache: hit / miss / verify ... / refused ... N ms".</summary>
[ProfileOnly]
sealed class GxpCache
{
    public enum Kind { Miss, Verify, Hit }
    public Kind State { get; private set; }
    /// <summary>One program of the set: its name and the exact vertex / fragment texts handed to the driver (GlBackend.Programs order).
    /// (A class, not a value tuple: collections of value tuples cost the eboot their own AOT instantiations; kit #5 review.)</summary>
    public sealed class Src
    {
        public readonly string Name, Vs, Fs;
        public Src(string name, string vs, string fs) { Name = name; Vs = vs; Fs = fs; }
    }
    /// <summary>The program set of this start, in GlBackend.Programs order.</summary>
    readonly List<Src> _progs;
    readonly string _key, _path;
    byte[][] _file;                 // the file's blobs (verify / hit): [2i] vertex, [2i + 1] fragment of program i
    readonly byte[][] _compiled;    // this start's (miss / verify), the same layout; null = not read back
    readonly List<string> _problems = new();
    readonly Stopwatch _sw;
    readonly int _identityMs;
    string _why;   // why the file was not usable (miss), for the log line
    int _loaded;

    /// <summary>The cache's file layout version and magic; a file of another version is not read (it is replaced on the next miss).</summary>
    const string Magic = "SHPDGXPC";
    const int Version = 1;
    const int FlagVerified = 1;
    const int MaxFile = 8 << 20;
    public static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_GXP_CACHE") ?? "").Trim().ToLowerInvariant();
    public static bool Off => Setting is "0" or "off" or "no" or "false";
    static bool AlwaysVerify => Setting == "verify";
    /// <summary>The cache folder: the owner folder's cache/gxp (ux0:data/shepherd/cache/gxp on the Vita).</summary>
    public static string Dir => Path.Combine(Program.Root, "cache", "gxp");

    GxpCache(List<Src> progs, string key, string path, int identityMs, Stopwatch sw)
    { _progs = progs; _key = key; _path = path; _identityMs = identityMs; _sw = sw; _compiled = new byte[progs.Count * 2][]; }

    // (kit #5 review: the log lines below are plain concatenations with ToString() -- the same text as the interpolated strings they
    // replace (same culture, same formats), without the interpolation handler's inlined code: ~0.35 KiB of AOT code per literal part)

    /// <summary>Called by GlBackend before it builds its programs. null = no cache this start (off, not the shepgl host, the
    /// fallback test, or the native side has no identity: logged); otherwise the start's state is decided here.</summary>
    public static GxpCache Begin(Func<List<Src>> set, (uint loc, string name)[] attributes, bool failTest)
    {
        if (!ShepGl.On) return null;
        if (Off) { Log.Info("[gl] gxp cache: off (SHEPHERD_GXP_CACHE=0): every program compiled from source, as in kit #4"); return null; }
        if (failTest) { Log.Info("[gl] gxp cache: off for this start (SHEPHERD_GL_FAIL: the compiler-refusal test)"); return null; }
        var sw = Stopwatch.StartNew();
        string identity;
        int ms;
        try { identity = ShepGl.GxpIdentity(out ms); }
        catch (Exception e) { Log.Warn("[gl] gxp cache: unavailable (" + e.GetBaseException().Message + "): compiled from source"); return null; }
        if (identity == null) { Log.Info("[gl] gxp cache: unavailable (" + ShepGl.GxpWhy() + "): compiled from source"); return null; }
        var progs = set();
        string key = Key(identity, progs, attributes);
        string path = Path.Combine(Dir, "programs-" + IcallManifest.ContentHash(Encoding.UTF8.GetBytes(key)).ToString("x16") + ".gxpc");
        string why = null; bool verified = false; byte[][] file = null;
        var c = new GxpCache(progs, key, path, ms, sw);
        try
        {
            if (!File.Exists(path)) why = "no cache file yet";
            else
            {
                var b = File.ReadAllBytes(path);
                why = Decode(b, key, progs, out file, out verified);
            }
        }
        catch (Exception e) { why = "the file could not be read (" + e.GetBaseException().Message + ")"; }
        if (why == null)
            for (int i = 0; i < progs.Count; i++)
            {
                if (!ShepGl.GxpCheck(file[2 * i], 0)) { why = progs[i].Name + "'s vertex shader refused (" + ShepGl.GxpWhy() + ")"; break; }
                if (!ShepGl.GxpCheck(file[2 * i + 1], 1)) { why = progs[i].Name + "'s fragment shader refused (" + ShepGl.GxpWhy() + ")"; break; }
            }
        if (why != null) { c._why = why; c.State = Kind.Miss; return c; }
        c._file = file;
        c.State = verified && !AlwaysVerify ? Kind.Hit : Kind.Verify;
        return c;
    }

    /// <summary>Everything the compiled set depends on, as one text: the native identity, the attribute bindings and every shader text.</summary>
    static string Key(string identity, List<Src> progs, (uint loc, string name)[] attributes)
    {
        var sb = new StringBuilder();
        sb.Append("shepherd gxp cache v").Append(Version).Append('\n').Append(identity).Append('\n').Append("attributes");
        foreach (var (loc, name) in attributes) sb.Append(' ').Append(loc).Append('=').Append(name);
        sb.Append('\n');
        foreach (var p in progs)
            sb.Append("program ").Append(p.Name).Append('\n').Append("vertex ").Append(p.Vs.Length).Append('\n').Append(p.Vs).Append('\n')
              .Append("fragment ").Append(p.Fs.Length).Append('\n').Append(p.Fs).Append('\n');
        return sb.ToString();
    }

    /// <summary>The index of a program of the set, -1 if it is not one.</summary>
    int Index(string name)
    {
        for (int i = 0; i < _progs.Count; i++) if (string.Equals(_progs[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The file's shader blobs for a program (hit only).</summary>
    public bool Blobs(string name, out byte[] vs, out byte[] fs)
    {
        vs = fs = null;
        int i = State == Kind.Hit ? Index(name) : -1;
        if (i < 0) return false;
        vs = _file[2 * i]; fs = _file[2 * i + 1]; return true;
    }
    /// <summary>Hit: a program's two shaders loaded.</summary>
    public void Loaded() => _loaded++;
    /// <summary>Hit: the native side refused a cached shader at load time: that program was compiled from source instead and the file
    /// is dropped at the end of the set (the next start compiles every program and writes a new one).</summary>
    public void LoadRefused(string name, string why) { _problems.Add(name + ": " + why); Log.Warn("[gl] gxp cache: " + name + "'s cached shader refused at load (" + why + "): that program compiled from source; the file is dropped"); }

    /// <summary>Miss / verify: after a program linked, its two compiled shaders as vitaGL serializes them (read-only calls).</summary>
    public void Collect(string name, uint prog, uint vs, uint fs)
    {
        if (State == Kind.Hit) return;
        var bv = ShepGl.GxpSave(prog, vs, 0); var bf = bv == null ? null : ShepGl.GxpSave(prog, fs, 1);
        if (bv == null || bf == null) { _problems.Add(name + ": " + (bv == null ? "vertex" : "fragment") + " shader not readable (" + ShepGl.GxpWhy() + ")"); return; }
        int i = Index(name);
        if (i < 0) { _problems.Add(name + ": not in the program set"); return; }
        _compiled[2 * i] = bv; _compiled[2 * i + 1] = bf;
    }
    /// <summary>A program of the set did not build (an optional program the compiler refused): no file is written this start.</summary>
    public void Failed(string name) => _problems.Add(name + ": did not build");

    /// <summary>After the set: write / verify / report. One log line.</summary>
    public void End()
    {
        int n = _progs.Count, got = 0;
        for (int i = 0; i < n; i++) if (_compiled[2 * i] != null) got++;
        string file = Path.GetFileName(_path), of = " of " + n.ToString();
        switch (State)
        {
            case Kind.Hit:
                if (_problems.Count > 0) { TryDelete(); Log.Warn("[gl] gxp cache: hit with problems -- " + string.Join("; ", _problems) + "; " + file + " dropped (" + _sw.ElapsedMilliseconds.ToString() + " ms)"); }
                else Log.Info("[gl] gxp cache: hit -- " + _loaded.ToString() + of + " programs loaded from " + file + " (verified on this console) and linked, " + _sw.ElapsedMilliseconds.ToString() + " ms incl. the identity " + _identityMs.ToString() + " ms; SHEPHERD_GXP_CACHE=0 compiles them as in kit #4");
                return;
            case Kind.Miss:
            {
                string head = "[gl] gxp cache: miss (" + _why + ") -- ";
                if (_problems.Count > 0 || got != n) { Log.Info(head + "NOT written: " + NotRead(got, of) + " (" + _sw.ElapsedMilliseconds.ToString() + " ms)"); return; }
                var err = Write(verified: false, _compiled, out long bytes);
                if (err != null) Log.Warn(head + "compiled from source; writing " + file + " failed: " + err);
                else Log.Info(head + n.ToString() + " programs compiled from source as before; written " + file + " (" + Kib(bytes) + " KiB, unverified: the next start compiles again and compares), identity " + _identityMs.ToString() + " ms, " + _sw.ElapsedMilliseconds.ToString() + " ms in all");
                return;
            }
            default:   // verify
            {
                const string head = "[gl] gxp cache: verify -- ";
                if (_problems.Count > 0 || got != n) { Log.Warn(head + "not completed: " + NotRead(got, of) + "; the file is left as it is (" + _sw.ElapsedMilliseconds.ToString() + " ms)"); return; }
                var diff = new List<string>(); int same = 0;
                for (int i = 0; i < n; i++)
                {
                    byte[] cv = _compiled[2 * i], cf = _compiled[2 * i + 1], fv = _file[2 * i], ff = _file[2 * i + 1];
                    bool v = cv.AsSpan().SequenceEqual(fv), f = cf.AsSpan().SequenceEqual(ff);
                    if (v && f) same++;
                    else diff.Add(_progs[i].Name + " (" + (v ? "" : "vertex " + cv.Length.ToString() + " vs " + fv.Length.ToString() + " B") + (!v && !f ? ", " : "") + (f ? "" : "fragment " + cf.Length.ToString() + " vs " + ff.Length.ToString() + " B") + ")");
                }
                string sameOf = same.ToString() + of;
                if (diff.Count == 0)
                {
                    if (AlwaysVerify) { Log.Info(head + sameOf + " programs: the compiler's output is byte-identical to " + file + " (SHEPHERD_GXP_CACHE=verify: compiled every start), " + _sw.ElapsedMilliseconds.ToString() + " ms"); return; }
                    var err = Write(verified: true, _file, out long bytes);
                    if (err != null) Log.Warn(head + sameOf + " programs byte-identical, but marking " + file + " verified failed: " + err);
                    else Log.Info(head + sameOf + " programs: the compiler's output is byte-identical to " + file + "; marked verified (" + Kib(bytes) + " KiB): the next starts load it instead of compiling (" + _sw.ElapsedMilliseconds.ToString() + " ms)");
                }
                else
                {
                    var err = Write(verified: false, _compiled, out _);
                    Log.Warn(head + "the compiler's output DIFFERS from " + file + " for " + string.Join(", ", diff) + " (" + sameOf + " identical); " + (err == null ? "replaced by this start's output (unverified: the next start compares again)" : "replacing it failed: " + err));
                }
                return;
            }
        }
    }
    /// <summary>Why a miss / verify set is incomplete: the problems, else how many programs were read back.</summary>
    string NotRead(int got, string of) => _problems.Count > 0 ? string.Join("; ", _problems) : got.ToString() + of + " programs read back";
    static string Kib(long bytes) => (bytes / 1024.0).ToString("F1");

    void TryDelete() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    /// <summary>The file, written to a temp file, read back and compared, then renamed over the old one. null = done, else why not.</summary>
    string Write(bool verified, byte[][] blobs, out long bytes)
    {
        bytes = 0;
        byte[] b;
        try { b = Encode(_key, _progs, blobs, verified); }
        catch (Exception e) { return e.GetBaseException().Message; }
        bytes = b.Length;
        return WriteAtomic(_path, b);
    }
    /// <summary><paramref name="b"/> to <paramref name="path"/>: a temp file next to it (flushed to the card), read back and compared, then
    /// renamed over the old file. A start that stops half-way leaves the old file or none (a stray .tmp is overwritten next time); a write
    /// that fails (e.g. a full card) removes its temp file (kit #5 review).</summary>
    public static string WriteAtomic(string path, byte[] b)
    {
        string tmp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { fs.Write(b); fs.Flush(true); }
            var back = File.ReadAllBytes(tmp);
            if (!back.AsSpan().SequenceEqual(b)) { File.Delete(tmp); return "the card gave back " + back.Length.ToString() + " different bytes"; }
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception e)
        {
            if (tmp != null) try { File.Delete(tmp); } catch { }
            return e.GetBaseException().Message;
        }
    }

#if SHEPHERD_EBOOT
    // ------------------------------------------------------------------ (the Mac self-test is not in the Vita eboot: kit #5 review)
    public static bool Handles(string mode) => false;
    public static int SelfTest() => 2;
#else
    // ------------------------------------------------------------------ Mac self-test of the file (boot gxpcachetest; not in the Vita eboot)
    public static bool Handles(string mode) => mode == "gxpcachetest";
    /// <summary>`Shepherd.Engine boot gxpcachetest` (the Mac; left out of the Vita eboot, SHEPHERD_EBOOT): the file layout -- round trip,
    /// the verified flag, a key of other shaders, every single-byte change and every truncation of a file refused without an exception,
    /// wrong magic / version / program list, bytes after the end -- the atomic write (new file, over an old one, a stray temp file, a
    /// failed write), and ContentHash against the 64-bit formula. Exit 0 = all PASS.</summary>
    public static int SelfTest()
    {
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) pass++; else fail++; Log.Info($"[gxptest] {(ok ? "PASS" : "FAIL")} {what}"); }
        var rnd = new Random(5);
        byte[] Blob(int n) { var x = new byte[n]; rnd.NextBytes(x); return x; }
        var set = new List<Src> { new("sprite", "#version 100\nvoid main(){}\n", "#version 100\nprecision highp float;\nvoid main(){}\n"), new("copy", "v", "f"), new("grab", "vv", "ff") };
        var attrs = new[] { (0u, "a_pos"), (1u, "a_color") };
        string key = Key("identity line", set, attrs);
        var blobs = new byte[set.Count * 2][];
        for (int i = 0; i < set.Count; i++) { blobs[2 * i] = Blob(300 + set[i].Name.Length * 17); blobs[2 * i + 1] = Blob(500 + set[i].Name.Length * 13); }
        bool Same(byte[][] a, byte[][] b) { if (a == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (!a[i].AsSpan().SequenceEqual(b[i])) return false; return true; }
        foreach (var verified in new[] { false, true })
        {
            var file = Encode(key, set, blobs, verified);
            var why = Decode(file, key, set, out var got, out bool v);
            Check(why == null && v == verified && Same(got, blobs), $"round trip ({file.Length} bytes, verified={verified}): {why ?? "every blob back"}");
        }
        var f0 = Encode(key, set, blobs, false);
        string Try(byte[] b, string k = null, List<Src> ps = null)
        {
            try { return Decode(b, k ?? key, ps ?? set, out _, out _); }
            catch (Exception e) { return "THREW " + e.GetType().Name; }
        }
        Check(Try(f0, Key("identity line 2", set, attrs))?.Contains("key differs") == true, "another identity (compiler / options / GL stack) is refused");
        var set2 = new List<Src> { set[0], new(set[1].Name, set[1].Vs, set[1].Fs + " "), set[2] };
        Check(Try(f0, Key("identity line", set2, attrs), set2)?.Contains("key differs") == true, "a changed shader text is refused");
        Check(Try(f0, Key("identity line", set, new[] { (0u, "a_pos"), (2u, "a_color") }))?.Contains("key differs") == true, "other attribute bindings are refused");
        int flips = 0, flipsBad = 0;
        for (int i = 0; i < f0.Length; i++)
            foreach (byte m in new byte[] { 1, 0x80 })
            {
                var c = (byte[])f0.Clone(); c[i] ^= m; flips++;
                var r = Try(c); if (r == null || r.StartsWith("THREW")) flipsBad++;
            }
        Check(flipsBad == 0, $"every single-bit change refused: {flips - flipsBad} of {flips}");
        int cuts = 0, cutsBad = 0;
        for (int n = 0; n < f0.Length; n++) { cuts++; var r = Try(f0[..n]); if (r == null || r.StartsWith("THREW")) cutsBad++; }
        Check(cutsBad == 0, $"every truncation refused: {cuts - cutsBad} of {cuts}");
        var longer = new byte[f0.Length + 1]; f0.CopyTo(longer, 0);
        Check(Try(longer) != null, "a byte after the end is refused");
        Check(Try(Encode(key, set.GetRange(0, 2), blobs[..4], false)) != null, "a file with fewer programs is refused");
        var swappedSet = new List<Src> { set[1], set[0], set[2] };
        var swapped = new[] { blobs[2], blobs[3], blobs[0], blobs[1], blobs[4], blobs[5] };
        Check(Try(Encode(key, swappedSet, swapped, false)) != null, "programs in another order are refused");
        var badVer = (byte[])f0.Clone(); badVer[8] = 2; var hv = IcallManifest.ContentHash(badVer.AsSpan(0, badVer.Length - 8)); BinaryPrimitives.WriteUInt64LittleEndian(badVer.AsSpan(badVer.Length - 8), hv);
        Check(Try(badVer)?.Contains("version") == true, "another layout version is refused (checksum fixed up)");
        Check(Try(new byte[] { 1, 2, 3 }) != null && Try(Array.Empty<byte>()) != null, "tiny / empty files are refused");
        // IcallManifest.ContentHash (32-bit arithmetic, kit #5) == the 64-bit FNV-1a formula, every length class and random content
        int hashBad = 0, hashN = 0;
        for (int n = 0; n < 300; n++)
            for (int rep = 0; rep < 4; rep++)
            {
                var x = new byte[n]; rnd.NextBytes(x); if (rep == 1) Array.Fill(x, (byte)0xFF); if (rep == 2) Array.Clear(x);
                hashN++; if (IcallManifest.ContentHash(x) != IcallManifest.ContentHashReference(x)) hashBad++;
            }
        { var big = new byte[(3 << 20) + 3]; rnd.NextBytes(big); hashN++; if (IcallManifest.ContentHash(big) != IcallManifest.ContentHashReference(big)) hashBad++; }
        Check(hashBad == 0, $"ContentHash (32-bit arithmetic) == the 64-bit FNV-1a formula on {hashN - hashBad} of {hashN} buffers (0..299 bytes x 4 patterns, 3 MiB + 3)");
        var dir = Path.Combine(Path.GetTempPath(), $"shepherd-gxptest-{Environment.ProcessId}");
        try
        {
            var path = Path.Combine(dir, "sub", "programs-test.gxpc");
            Check(WriteAtomic(path, f0) == null && File.ReadAllBytes(path).AsSpan().SequenceEqual(f0) && !File.Exists(path + ".tmp"), "atomic write: a new file (and its folder)");
            var f1 = Encode(key, set, blobs, true);
            File.WriteAllBytes(path + ".tmp", new byte[] { 9, 9, 9 });
            Check(WriteAtomic(path, f1) == null && File.ReadAllBytes(path).AsSpan().SequenceEqual(f1) && !File.Exists(path + ".tmp"), "atomic write: over an old file, a stray temp file replaced");
            // a write that fails after its temp file was created (the target is a folder: the rename throws) leaves no temp file behind
            var dpath = Path.Combine(dir, "adir.gxpc"); Directory.CreateDirectory(dpath); File.WriteAllBytes(Path.Combine(dpath, "x"), new byte[] { 1 });
            Check(WriteAtomic(dpath, f0) != null && !File.Exists(dpath + ".tmp"), "atomic write: a failed write removes its temp file");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Log.Info($"[gxptest] {pass} PASS, {fail} FAIL");
        return fail == 0 ? 0 : 1;
    }
#endif

    // ------------------------------------------------------------------ the file (little-endian)
    //   "SHPDGXPC" | u32 version | u32 flags (1 = verified on this console) | u32 key bytes | key (UTF-8) | u32 programs |
    //   per program: u32 name bytes, name, u32 vertex blob bytes, blob, u32 fragment blob bytes, blob |
    //   u64 checksum = IcallManifest.ContentHash of every byte before it. Nothing may follow.

    /// <summary>The file for a program set (also the Mac self-test's writer): <paramref name="blobs"/>[2i] / [2i + 1] = program i's vertex /
    /// fragment blob.</summary>
    public static byte[] Encode(string key, List<Src> progs, byte[][] blobs, bool verified)
    {
        var ms = new MemoryStream();
        void U32(int v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); ms.Write(t); }
        void Bytes(byte[] x) { U32(x.Length); ms.Write(x); }
        ms.Write(Encoding.ASCII.GetBytes(Magic)); U32(Version); U32(verified ? FlagVerified : 0);
        Bytes(Encoding.UTF8.GetBytes(key)); U32(progs.Count);
        for (int i = 0; i < progs.Count; i++) { Bytes(Encoding.UTF8.GetBytes(progs[i].Name)); Bytes(blobs[2 * i]); Bytes(blobs[2 * i + 1]); }
        Span<byte> h = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(h, IcallManifest.ContentHash(ms.GetBuffer().AsSpan(0, (int)ms.Length))); ms.Write(h);
        return ms.ToArray();
    }

    /// <summary>Reads a file: null = usable (its blobs in <paramref name="blobs"/>, [2i] / [2i + 1] per program), else why not. The key must
    /// be exactly <paramref name="key"/> and the programs exactly <paramref name="progs"/>' names in that order.</summary>
    public static string Decode(byte[] b, string key, List<Src> progs, out byte[][] blobs, out bool verified)
    {
        verified = false; blobs = null;
        if (b.Length > MaxFile) return "the file is " + b.Length.ToString() + " bytes (more than " + MaxFile.ToString() + ")";
        if (b.Length < 8 + 4 * 4 + 8 || Encoding.ASCII.GetString(b, 0, 8) != Magic) return "not a gxp cache file";
        ulong want = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b.Length - 8));
        if (IcallManifest.ContentHash(b.AsSpan(0, b.Length - 8)) != want) return "its checksum does not match (damaged)";
        int pos = 8, end = b.Length - 8;
        bool Int(out int v) { v = 0; if (pos + 4 > end) return false; v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos)); pos += 4; return true; }
        bool Bytes(out byte[] x) { x = null; if (!Int(out int n) || n < 0 || n > end - pos) return false; x = b.AsSpan(pos, n).ToArray(); pos += n; return true; }
        if (!Int(out int ver) || ver != Version) return "layout version " + ver.ToString() + ", this engine reads " + Version.ToString();
        if (!Int(out int flags)) return "truncated";
        if (!Bytes(out var k)) return "truncated key";
        if (Encoding.UTF8.GetString(k) != key) return "made for other shaders, another compiler or other options (its key differs)";
        if (!Int(out int count) || count != progs.Count) return count.ToString() + " programs, this start builds " + progs.Count.ToString();
        var got = new byte[count * 2][];
        for (int i = 0; i < count; i++)
        {
            if (!Bytes(out var nb) || !Bytes(out got[2 * i]) || !Bytes(out got[2 * i + 1])) return "truncated program list";
            string name = Encoding.UTF8.GetString(nb);
            if (name != progs[i].Name) return "program " + i.ToString() + " is '" + name + "', this start builds '" + progs[i].Name + "'";
        }
        if (pos != end) return (end - pos).ToString() + " unexplained bytes before the checksum";
        blobs = got;
        verified = (flags & FlagVerified) != 0;
        return null;
    }
}
