using System.Reflection;
namespace Shepherd.TestHost;

/// <summary>Kit #7 tests track (vita/notes/kit7-tests.md "Power-cut tests for every cache write"; from Amazon S3 ShardStore's crash-injected
/// property tests): every cache the engine writes to the owner's card -- the GXP program cache (Render/GxpCache.cs, cache/gxp), the icall
/// hook list (Runtime/IcallScanCache.cs, cache/icalls), the catalog bake and its token (Runtime/JsonBake.cs, cache/json) -- goes through
/// GxpCache.WriteAtomic (temp file, flush, read back, rename) and a self-checking format (magic, version, structure, a 64-bit content hash).
/// The reference model of each cache is a dictionary-sized one: "the file holds version OLD, or NEW, or nothing usable (rebuild)". For
/// random operation sequences (write NEW over OLD, write over nothing, write the same again, read) a power cut / app kill is injected at
/// EVERY step of the write protocol, and for the step that writes bytes at every prefix length (all of them for small files, the page
/// boundaries + the structure's own boundaries + random cuts for big ones), plus the states a non-atomic card file system (the Vita's
/// exFAT) can leave after a cut even past the rename (the new name with torn data: a prefix of NEW then zeros / garbage / OLD's tail,
/// single flipped bits, a lost tail). After each cut the engine's own reader runs on the files left behind. Required: it returns
/// exactly OLD or exactly NEW, or refuses (the engine rebuilds) -- never anything else, never an exception escaping. PowerCut also checks
/// the protocol order itself (the temp file is never read as the cache; a stray .tmp is ignored).
///   powercut &lt;out dir&gt; [seed] [ops]      -&gt; &lt;out dir&gt;/powercut.txt</summary>
public static class PowerCut
{
    const BindingFlags A = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    sealed class CacheKind
    {
        public string Name;
        public Func<int, byte[]> Make;              // a valid file for "version" v (different v = different content)
        public Func<byte[], string> Read;           // the engine's reader: null = accepted, else why refused
        public Func<byte[], int> Identify;          // which version an ACCEPTED file is (-1 = none of the made ones)
        public long Cuts, Accepted, Refused, Bad, Exceptions; public List<string> BadCases = new();
    }

    public static int Run(string[] args)
    {
        string outDir = args.Length > 1 ? args[1] : "."; int seed = args.Length > 2 ? int.Parse(args[2]) : 1; int ops = args.Length > 3 ? int.Parse(args[3]) : 40;
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        var kinds = new List<CacheKind> { Gxp(), Scan(), Bake() };
        var rng = new ShepherdOracle.Rng((ulong)seed * 0x9E3779B97F4A7C15UL);
        foreach (var k in kinds)
        {
            if (k.Make == null) { log.Add($"{k.Name}: not testable here ({k.Read})"); continue; }
            var made = new Dictionary<int, byte[]>();
            byte[] Get(int v) => made.TryGetValue(v, out var b) ? b : made[v] = k.Make(v);
            int cur = -1;   // the model: the version on the card (-1 none)
            for (int op = 0; op < ops; op++)
            {
                int next = rng.Chance(15) ? cur : rng.Int(4);   // write a new version (sometimes the same again)
                var oldB = cur >= 0 ? Get(cur) : null; var newB = Get(next < 0 ? 0 : next);
                foreach (var state in CrashStates(oldB, newB, rng))
                {
                    k.Cuts++;
                    // the engine reads only the cache's own name; a stray temp file must not matter (it is overwritten next time)
                    var file = state.final;
                    if (file == null) { k.Refused++; continue; }   // no file: a miss (rebuild) -- the model's "nothing"
                    string why; int id;
                    try { why = k.Read(file); }
                    catch (Exception e) { k.Exceptions++; why = "EXC"; if (k.BadCases.Count < 8) k.BadCases.Add($"{state.what}: reader threw {e.GetType().Name}: {e.Message}"); continue; }
                    if (why != null) { k.Refused++; continue; }
                    k.Accepted++;
                    id = k.Identify(file);
                    bool ok = (id >= 0 && made.TryGetValue(id, out var mb) && mb.AsSpan().SequenceEqual(file)) && (id == cur || id == next || (oldB != null && file.AsSpan().SequenceEqual(oldB)) || file.AsSpan().SequenceEqual(newB));
                    if (!ok) { k.Bad++; if (k.BadCases.Count < 8) k.BadCases.Add($"{state.what}: ACCEPTED a file that is neither the old nor the new version ({file.Length} B)"); }
                }
                cur = next;   // the write completed (the last state of the sequence)
            }
            log.Add($"{k.Name}: {k.Cuts} power cuts injected over {ops} writes (files {string.Join("/", made.Values.Select(b => b.Length).Distinct().Take(4))} B); after each the engine's reader: {k.Accepted} accepted (each exactly the old or the new version), {k.Refused} refused or absent (rebuild), {k.Bad} WRONG accepts, {k.Exceptions} exceptions");
            foreach (var c in k.BadCases) log.Add("   " + c);
        }
        // the protocol itself, on a real file: WriteAtomic leaves the old file intact when the temp write fails, and a stray .tmp is ignored
        log.Add(ProtocolCheck(outDir));
        bool pass = kinds.All(k => k.Bad == 0 && k.Exceptions == 0) && log.Last().StartsWith("protocol: PASS");
        log.Add("RESULT: " + (pass ? "PASS" : "FAIL"));
        File.WriteAllLines(Path.Combine(outDir, "powercut.txt"), log);
        foreach (var l in log) Console.WriteLine(l);
        return pass ? 0 : 1;
    }

    /// <summary>Every file state a cut can leave at the cache's own name during "write NEW over OLD" (WriteAtomic's steps, then the
    /// non-atomic file system's extra states). The temp file's own states never reach the reader (it reads only the final name).</summary>
    static IEnumerable<(string what, byte[] final)> CrashStates(byte[] oldB, byte[] newB, ShepherdOracle.Rng r)
    {
        // 1-4: before the rename -- the temp file being created / written / flushed / read back: the final name still holds OLD (or nothing)
        yield return ("cut before the temp file exists", oldB);
        yield return ("cut while writing the temp file", oldB);
        yield return ("cut after the flush, before the read-back", oldB);
        yield return ("cut after the read-back, before the rename", oldB);
        // 5: after the rename (atomic on POSIX / the Mac): NEW
        yield return ("cut right after the rename", newB);
        // 6: a card file system whose rename lands before the data (exFAT): the final name with torn NEW data
        foreach (int n in Cuts(newB.Length, r)) yield return ($"rename landed, data torn at {n} of {newB.Length} B (rest zero)", Torn(newB, n, null));
        foreach (int n in Cuts(newB.Length, r).Where((_, i) => i % 3 == 0)) yield return ($"rename landed, data torn at {n} (rest garbage)", Torn(newB, n, r));
        if (oldB != null) foreach (int n in Cuts(Math.Min(newB.Length, oldB.Length), r).Where((_, i) => i % 3 == 0)) yield return ($"in-place overwrite torn at {n} (OLD's tail)", Mix(newB, oldB, n));
        foreach (int n in Cuts(newB.Length, r).Where((_, i) => i % 2 == 0)) yield return ($"lost tail: the file is {n} B", newB.AsSpan(0, n).ToArray());
        for (int i = 0; i < 64; i++) { var b = (byte[])newB.Clone(); int at = r.Int(b.Length); b[at] ^= (byte)(1 << r.Int(8)); yield return ($"one bit flipped at {at}", b); }
        // done: NEW
        yield return ("the write completed", newB);
    }
    /// <summary>Cut points: every length for files up to 2 KiB; else the first and last 512, every 512 boundary (card sectors / pages), and 300 random.</summary>
    static IEnumerable<int> Cuts(int len, ShepherdOracle.Rng r)
    {
        if (len <= 2048) { for (int i = 0; i < len; i++) yield return i; yield break; }
        var set = new SortedSet<int>();
        for (int i = 0; i < 512; i++) { set.Add(i); set.Add(len - 1 - i); }
        for (int i = 512; i < len; i += 512) set.Add(i);
        for (int i = 0; i < 300; i++) set.Add(r.Int(len));
        foreach (var x in set) yield return x;
    }
    static byte[] Torn(byte[] b, int n, ShepherdOracle.Rng r) { var t = new byte[b.Length]; Array.Copy(b, t, n); if (r != null) for (int i = n; i < t.Length; i++) t[i] = (byte)r.Int(256); return t; }
    static byte[] Mix(byte[] nw, byte[] old, int n) { var t = (byte[])old.Clone(); if (t.Length < nw.Length) Array.Resize(ref t, nw.Length); Array.Copy(nw, t, n); return t; }

    // ---------------- the caches ----------------
    static Type EngineType(string name) => typeof(Shepherd.Engine.Program).Assembly.GetType(name, true);
    static CacheKind Gxp()
    {
        var t = EngineType("Shepherd.Engine.Render.GxpCache"); var srcT = t.GetNestedType("Src", A);
        var encode = t.GetMethod("Encode", A); var decode = t.GetMethod("Decode", A);
        var listT = typeof(List<>).MakeGenericType(srcT);
        object Progs(int v)
        {
            var l = (System.Collections.IList)Activator.CreateInstance(listT);
            for (int i = 0; i < 8; i++) l.Add(Activator.CreateInstance(srcT, A, null, new object[] { "prog" + i, "vs" + i, "fs" + i }, null));
            return l;
        }
        string Key(int v) => "key: the shaders' text + the GL stack identity, version " + (v / 2);   // (versions 0/1 share a key, 2/3 another)
        byte[][] Blobs(int v) { var r = new ShepherdOracle.Rng((ulong)v + 77); var b = new byte[16][]; for (int i = 0; i < 16; i++) { b[i] = new byte[300 + r.Int(80)]; for (int j = 0; j < b[i].Length; j++) b[i][j] = (byte)r.Int(256); } return b; }
        var progs = Progs(0);
        return new CacheKind
        {
            Name = "gxp program cache (cache/gxp/programs-*.gxpc)",
            Make = v => (byte[])encode.Invoke(null, new object[] { Key(v), Progs(v), Blobs(v), (v & 1) == 1 }),
            // the reader is asked for the key this start would compute: it is the key of the version being written (the old file of
            // another key is refused as "made for other shaders" -- a rebuild, which is also correct)
            Read = b => { var a = new object[] { b, null, progs, null, null }; foreach (int v in new[] { 0, 2 }) { a[1] = Key(v); var why = (string)decode.Invoke(null, a); if (why == null) return null; } return "refused"; },
            Identify = b => { for (int v = 0; v < 4; v++) if (((byte[])encode.Invoke(null, new object[] { Key(v), Progs(v), Blobs(v), (v & 1) == 1 })).AsSpan().SequenceEqual(b)) return v; return -1; },
        };
    }
    static CacheKind Scan()
    {
        var t = EngineType("Shepherd.Engine.Runtime.IcallScanCache");
        var encode = t.GetMethod("Encode", A); var decode = t.GetMethod("Decode", A);
        var mod = typeof(Shepherd.Engine.Program).Module;
        // a hook list made of real engine methods (the reader resolves each token and checks its name)
        var ms = typeof(Shepherd.Engine.Program).Assembly.GetType("Shepherd.Engine.Impl.MathIcalls")!.GetMethods(A).Where(m => m.IsStatic && m.DeclaringType!.Name == "MathIcalls").Take(40).ToList();
        var tupT = typeof(ValueTuple<MethodInfo, string>); var listT = typeof(List<ValueTuple<MethodInfo, string>>);
        List<(MethodInfo, string)> Binds(int v) => ms.Take(20 + v * 5).Select((m, i) => (m, "UnityEngine.Mathf::K" + i + "v" + v)).ToList();
        List<string> Loaded(int v) => Enumerable.Range(0, v + 1).Select(i => "Asm" + i + ", Version=1.0.0.0").ToList();
        return new CacheKind
        {
            Name = "icall hook list (cache/icalls/scan-<MVID>-<profile>.bin)",
            Make = v => (byte[])encode.Invoke(null, new object[] { mod, Binds(v), Loaded(v) }),
            Read = b => { var a = new object[] { b, mod, null, null, false }; return (string)decode.Invoke(null, a); },
            Identify = b => { for (int v = 0; v < 4; v++) if (((byte[])encode.Invoke(null, new object[] { mod, Binds(v), Loaded(v) })).AsSpan().SequenceEqual(b)) return v; return -1; },
        };
    }
    static CacheKind Bake()
    {
        var jb = EngineType("Shepherd.Engine.Runtime.JsonBake"); var bakeT = jb.GetNestedType("Bake", A);
        var decode = bakeT.GetMethod("Decode", A); var encode = bakeT.GetMethod("Encode", A);
        // a real bake (the owner folder's catalog.jbake: owner-local, read only) re-encoded per version with a different document hash
        var src = Environment.GetEnvironmentVariable("POWERCUT_BAKE") ?? "$SHEPHERD_WORK/kit6/kit-build/owner/shepherd/cache/json/catalog.jbake";
        if (!File.Exists(src)) return new CacheKind { Name = "catalog bake (cache/json/catalog.jbake)", Read = _ => "no bake file at " + src };
        var orig = File.ReadAllBytes(src);
        var dargs = new object[] { orig, null }; var bake = decode.Invoke(null, dargs);
        if (bake == null) return new CacheKind { Name = "catalog bake", Read = _ => "the owner's bake did not decode: " + dargs[1] };
        var hashF = bakeT.GetField("DocHash", A);
        ulong h0 = (ulong)hashF.GetValue(bake);
        var cache = new Dictionary<int, byte[]>();
        byte[] Make(int v) { if (cache.TryGetValue(v, out var c)) return c; hashF.SetValue(bake, h0 ^ (ulong)v); var e = new object[] { 0, 0 }; return cache[v] = (byte[])encode.Invoke(bake, e); }
        return new CacheKind
        {
            Name = $"catalog bake (cache/json/catalog.jbake, the owner's real {orig.Length / 1048576.0:F1} MiB bake)",
            Make = Make,
            Read = b => { var a = new object[] { b, null }; return decode.Invoke(null, a) != null ? null : (string)a[1] ?? "refused"; },
            Identify = b => { for (int v = 0; v < 4; v++) if (Make(v).AsSpan().SequenceEqual(b)) return v; return -1; },
        };
    }

    /// <summary>WriteAtomic on real files: (a) a write into a read-only folder fails and leaves the old file untouched with no temp
    /// left behind; (b) a stray .tmp next to the cache does not change what the cache's own name holds; (c) a write replaces the old
    /// file completely.</summary>
    static string ProtocolCheck(string dir)
    {
        var w = EngineType("Shepherd.Engine.Render.GxpCache").GetMethod("WriteAtomic", A);
        var d = Path.Combine(dir, "protocol"); if (Directory.Exists(d)) { foreach (var f in Directory.GetFiles(d)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(d, true); }
        Directory.CreateDirectory(d);
        var p = Path.Combine(d, "c.bin"); var old = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(p, old);
        File.WriteAllBytes(p + ".tmp", new byte[] { 9, 9 });                          // (b) a stray temp from an earlier cut
        var err = (string)w.Invoke(null, new object[] { p, new byte[] { 5, 6, 7 } });   // (c)
        bool c = err == null && File.ReadAllBytes(p).AsSpan().SequenceEqual(new byte[] { 5, 6, 7 }) && !File.Exists(p + ".tmp");
        // (a) the folder made read-only: the temp cannot be created -> an error, the old file intact
        var ro = Path.Combine(d, "ro"); Directory.CreateDirectory(ro); var rp = Path.Combine(ro, "c.bin"); File.WriteAllBytes(rp, old);
        File.SetAttributes(rp, FileAttributes.ReadOnly); var dirInfo = new DirectoryInfo(ro);
        string err2;
        try { System.Diagnostics.Process.Start("chmod", new[] { "555", ro })!.WaitForExit(); err2 = (string)w.Invoke(null, new object[] { rp, new byte[] { 8 } }); }
        finally { System.Diagnostics.Process.Start("chmod", new[] { "755", ro })!.WaitForExit(); File.SetAttributes(rp, FileAttributes.Normal); }
        bool a = err2 != null && File.ReadAllBytes(rp).AsSpan().SequenceEqual(old) && !File.Exists(rp + ".tmp");
        return $"protocol: {(a && c ? "PASS" : "FAIL")} -- a failed write (read-only folder: '{err2}') left the old file intact and no temp: {a}; a stray .tmp was replaced and the new file is complete: {c}";
    }
}
