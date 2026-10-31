using System.Diagnostics;
using System.Reflection;
using Shepherd.Hooks;
namespace Shepherd.Engine;

/// <summary>Marks a static `object F(object[] a)` as the implementation of one Unity native hook. Key = manifest key,
/// or "Type::Method" to cover every overload. a[0] is `this` for instance hooks; write ref/out results back into a[i].
/// Profile = "vita": registered only under that SHEPHERD_PROFILE, after (and over) the default hooks; never bound otherwise.</summary>
/// Phys = n (physics Stage 1+, physics-plan-v2 Appendix A): bound only when SHEPHERD_PHYS is at level n or above and the core binds, in a last
/// pass (over the default and profile hooks); otherwise the table is exactly what it was without the hook (missing / auto-prop / inert).
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class IcallAttribute : Attribute { public readonly string Key; public string Profile; public int Phys; public IcallAttribute(string key) => Key = key; }

public static class IcallRegistry
{
    public static string[] Names = Array.Empty<string>();
    public static readonly Dictionary<string, int> MissingHits = new();
    public static int Implemented;
    /// <summary>Where the rows came from (icalls.bin / icalls.json, and why), for the log.</summary>
    public static string Source = "";

    /// <summary>kit #4 boot (B-1): SHEPHERD_ICALL_BAKE = unset/1 (icalls.bin when it was made from this icalls.json: its length and content
    /// hash, else icalls.json) | 0 (always icalls.json, the pre-kit-4 input) | verify (also parse the json and compare every row). The
    /// content hash's cost is on the icalls line ("read + hashed in N ms").</summary>
    static readonly string BakeMode = (Environment.GetEnvironmentVariable("SHEPHERD_ICALL_BAKE") ?? "1").Trim().ToLowerInvariant();

    static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The manifest rows: from icalls.bin next to <paramref name="manifestPath"/> when it is there and was made from this
    /// icalls.json (same length and content hash: one read of the json, no parse), else from icalls.json itself (read in one call; it
    /// used to be read in 4 KiB pieces, 915 calls on the device's card).</summary>
    public static IcallRow[] LoadRows(string manifestPath)
    {
        var bin = IcallManifest.BakePath(manifestPath);
        byte[] json = null;
        if (BakeMode != "0" && BakeMode != "off" && File.Exists(bin))
        {
            long jsonLen = new FileInfo(manifestPath).Length;
            var rows = IcallManifest.Read(File.ReadAllBytes(bin), jsonLen, out var hash, out var why);
            if (rows != null)
            {
                long th = Stopwatch.GetTimestamp();
                json = File.ReadAllBytes(manifestPath);
                ulong have = IcallManifest.ContentHash(json);
                double hashMs = Ms(th);
                if (have != hash) why = $"stale: made from another {jsonLen}-byte icalls.json (content hash {hash:x16}, this one {have:x16})";
                else if (BakeMode == "verify" && IcallManifest.Diff(IcallManifest.FromJson(json), rows) is { } d) why = "verify: rows differ: " + d;
                if (why != null) rows = null;
                else Source = BakeMode == "verify" ? "icalls.bin (verified against icalls.json: content hash and every row)"
                    : $"icalls.bin ({new FileInfo(bin).Length} bytes, made from this icalls.json: {jsonLen} bytes, content hash {have:x16}, read + hashed in {hashMs:F0} ms)";
            }
            if (rows != null) return rows;
            Log.Warn($"[Icall] {bin}: {why}; parsing icalls.json instead");
            Source = "icalls.json (the bake was refused: " + why + ")";
        }
        else Source = BakeMode is "0" or "off" ? "icalls.json (SHEPHERD_ICALL_BAKE=0)" : "icalls.json (no icalls.bin next to it: tools/IcallBake makes one)";
        return IcallManifest.FromJson(json ?? File.ReadAllBytes(manifestPath));
    }

    public static void Init(string manifestPath)
    {
        long t0 = Stopwatch.GetTimestamp();
        var rows = LoadRows(manifestPath);
        double tRows = Ms(t0); long t1 = Stopwatch.GetTimestamp();
        Names = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++) Names[i] = rows[i].Key;
        Icalls.Init(Names.Length, Names);
        // key -> id; an [Icall] key that is no row's key binds every overload of that name ("Type::Method" for the rows whose keys
        // carry a signature: the Rewriter adds "(param types)" only to overloaded names, so only those rows need the base-name map)
        var exact = new Dictionary<string, int>(Names.Length); var byBase = new Dictionary<string, List<int>>();
        for (int i = 0; i < Names.Length; i++)
        {
            exact[Names[i]] = i;
            var b = Names[i]; int p = b.IndexOf('(');
            if (p > 0) (byBase.TryGetValue(b = b[..p], out var l) ? l : byBase[b] = new List<int>()).Add(i);
        }
        double tNames = Ms(t1); long t2 = Stopwatch.GetTimestamp();
        // the hooks to bind, in bind order: every [Icall] method's keys in scan order, then the profile's hooks over them. Under the Vita
        // profile the list can come from Runtime/IcallScanCache.cs (kit #5 B4: the engine module's MVID, method tokens + keys; no scan)
        var binds = Profile.Vita ? Runtime.IcallScanCache.Load(typeof(IcallRegistry).Module) : null;
        List<(MethodInfo m, int level, string key)> physHooks = null;   // physics Stage 1+ (k6-physics): the [Icall(Phys = n)] hooks
        bool scanned = binds == null;
        if (binds == null)
        {
            var loadedBefore = Profile.Vita ? AppDomain.CurrentDomain.GetAssemblies() : null;   // (the scan loads assemblies: the cache replays them)
            binds = new List<(MethodInfo m, string key)>(); var profiled = new List<(MethodInfo m, string key)>();
            physHooks = new List<(MethodInfo m, int level, string key)>();
            foreach (var t in typeof(IcallRegistry).Assembly.GetTypes())
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!m.IsDefined(typeof(IcallAttribute), true)) continue;   // (the attribute decode below only for the ~1,200 hooks of ~2,500 static methods)
                    foreach (var a in m.GetCustomAttributes<IcallAttribute>())
                    {
                        if (a.Phys > 0) { physHooks.Add((m, a.Phys, a.Key)); continue; }   // physics level hooks: never in the cached list; bound last, at their level (below)
                        if (a.Profile != null) { if (a.Profile == Profile.Name) profiled.Add((m, a.Key)); continue; }   // profile hooks: second pass
                        binds.Add((m, a.Key));
                    }
                }
            binds.AddRange(profiled);
            if (Profile.Vita) Runtime.IcallScanCache.ScanDone(loadedBefore);   // (the assemblies the scan loaded; no I/O, no log)
            if (Physics.PhysConfig.Level >= 1 && Physics.PhysHooks.Outside(physHooks) is { } outside) Log.Warn(outside);
        }
        foreach (var (m, key) in binds) Bind(m, key);
        // the cache's file and log line after the binds (a log line before them made the console's first write -- and its assembly load
        // -- come earlier than in a scanned start)
        if (Profile.Vita) Runtime.IcallScanCache.After(typeof(IcallRegistry).Module, scanned ? binds : null);
        // physics Stage 1+ (k8-physics merge of k6-physics): the level's hooks bound last, only at their level and only with a core (the order
        // k6-physics bound them: after every other hook, in scan order). Level 0 / none never asks for the core and binds none of them. A
        // cached start (no scan) reads them from the physics icall classes alone (Physics.PhysHooks: the same methods in the same order).
        if (Physics.PhysConfig.Level >= 1 && Physics.PhysWorld.Sim)
            foreach (var (m, level, key) in physHooks ?? Physics.PhysHooks.Scan())
                if (Physics.PhysConfig.Level >= level) Bind(m, key);
        void Bind(MethodInfo m, string key)
        {
            var del = (IcallImpl)Delegate.CreateDelegate(typeof(IcallImpl), m);
            var ids = exact.TryGetValue(key, out var id) ? new List<int> { id } : byBase.GetValueOrDefault(key);
            if (ids == null) { Log.Warn($"[Icall] no such hook: {key}"); return; }
            foreach (var i in ids) { if (Icalls.Table[i] == null) Implemented++; Icalls.Table[i] = del; }
        }
        double tBind = Ms(t2); long t3 = Stopwatch.GetTimestamp();
        Shepherd.Engine.Impl.AutoProps.Register(rows);
        double tAuto = Ms(t3); long t4 = Stopwatch.GetTimestamp();
        Shepherd.Engine.Impl.InertStubs.Register(rows);
        double tInert = Ms(t4);
        Icalls.OnMissing = i => { lock (MissingHits) MissingHits[Names[i]] = 1; Log.Write("MIS", Names[i]); };
        Runtime.K9Typed.Bind();   // kit #9 track runtime: the typed twins (lever 10, D6) and the E5 mirrors, over the hooks bound above
        Log.Info($"icalls: {Implemented} implemented of {Names.Length}");
        Log.Info($"icalls: rows from {Source}; registry up in {Ms(t0):F0} ms (rows {tRows:F0}, names {tNames:F0}, [Icall] scan + bind {tBind:F0}, auto-props {tAuto:F0}, inert stubs {tInert:F0})");
        if (Environment.GetEnvironmentVariable("SHEPHERD_ICALL_DUMP") is { Length: > 0 } dump) Dump(dump);
#if !SHEPHERD_EBOOT
        Diag.K9IcallAlloc.Wrap();   // kit #9 engine: SHEPHERD_K9_ICALL_ALLOC=<from>:<to> (Mac measurement; unset: nothing wrapped)
#endif
    }

    /// <summary>SHEPHERD_ICALL_DUMP=&lt;file&gt; (kit #4 check): one line per id after Init -- what fills the slot: nothing, an [Icall] method,
    /// or a registry closure (AutoProps / InertStubs) with its captured values. Two runs whose dumps are equal bound the same table.</summary>
    public static void Dump(string path)
    {
        static string Val(object v) => v switch { null => "null", Type t => "type:" + t.AssemblyQualifiedName, string s => "\"" + s + "\"", _ => v.GetType().FullName + ":" + v };
        // a closure's captured values, nested display classes flattened (which class a value sits in is the compiler's choice)
        static IEnumerable<string> Captures(object target, int depth)
        {
            if (target == null || depth > 4) yield break;
            foreach (var f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var v = f.GetValue(target);
                if (v != null && v.GetType().Name.Contains("<>")) { foreach (var c in Captures(v, depth + 1)) yield return c; }
                else if (v is not Delegate) yield return f.Name + "=" + Val(v);   // (not the compiler's cached-delegate fields)
            }
        }
        var lines = new List<string>(Names.Length);
        for (int i = 0; i < Names.Length; i++)
        {
            var d = Icalls.Table[i];
            string what;
            if (d == null) what = "-";
            else if (d.Method.IsDefined(typeof(IcallAttribute), true)) what = "icall " + d.Method.DeclaringType.FullName + "::" + d.Method.Name;
            else
            {
                // a closure: its method's role (the lambda's name without the compiler's ordinals) and the values it captured
                var role = System.Text.RegularExpressions.Regex.Replace(d.Method.DeclaringType.FullName + "::" + d.Method.Name, @"(DisplayClass|b__|c__)\d+(_\d+)?", "$1#");
                var fields = string.Join(" ", Captures(d.Target, 0).OrderBy(x => x, StringComparer.Ordinal));
                what = "closure " + role + " " + fields;
            }
            lines.Add($"{i}\t{Names[i]}\t{what}");
        }
        File.WriteAllLines(path, lines);
        Log.Info($"icalls: table dump -> {path}");
    }

    public static void Report(string path)
    {
        var lines = new List<string>(); var hits = Icalls.HitCounts();   // (kit #6 fps: the counts as one array, Icalls.Hits + the 32-bit counters)
        for (int i = 0; i < Names.Length; i++)
            if (hits[i] > 0 && Icalls.Table[i] == null) lines.Add($"{hits[i],9}  {Names[i]}");
        lines.Sort((a, b) => string.CompareOrdinal(b, a));
        File.WriteAllLines(path, lines);
        var all = new List<string>();
        for (int i = 0; i < Names.Length; i++) if (hits[i] > 0) all.Add($"{hits[i],9}  {(Icalls.Table[i] == null ? "-" : "+")} {Names[i]}");
        all.Sort((a, b) => string.CompareOrdinal(b, a));
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(path)!, "icall-hits.txt"), all);
        Log.Info($"missing icalls hit: {lines.Count} (see {path})");
    }
}
