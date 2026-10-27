using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shepherd.Assets;

namespace VitaBudget;

/// <summary>
/// VitaBudget — READ-ONLY content inventory of the CotL data and "diet" sizing for a PS Vita build.
///   survey [--dop N]            all Addressables bundles -> out/survey.json
///   loose                       loose Data files (level0, resources.assets, sharedassets0, ...) -> out/loose.json
///   reach &lt;sceneKey&gt; &lt;label&gt;    PPtr-reachability from a scene (what Unity would actually load) -> out/reach-&lt;label&gt;.json
///   report [--opened tsv]       tables from the cached json -> stdout + out/report.txt
/// Outputs stay local in out/ (derived data: never publish or share).
/// </summary>
public static class Program
{
    const string DefaultData = "$SHEPHERD_GAME_DATA";
    static string Data = Environment.GetEnvironmentVariable("COTL_DATA") ?? DefaultData;
    static string Out = Environment.GetEnvironmentVariable("VITABUDGET_OUT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "src", "shepherd", "tools", "VitaBudget", "out");

    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = false };

    public static int Main(string[] argv)
    {
        var a = argv.ToList();
        if (a.Count == 0) { Console.Error.WriteLine("usage: survey | loose | reach <key> <label> | report [--opened tsv]"); return 2; }
        Directory.CreateDirectory(Out);
        var sw = Stopwatch.StartNew();
        int rc;
        switch (a[0])
        {
            case "survey":
            {
                int dop = Opt(a, "--dop", 4);
                using var db = new AssetDatabase(Data);
                var s = Scanner.Run(Data, db.BundleDir, dop);
                File.WriteAllText(Path.Combine(Out, "survey.json"), JsonSerializer.Serialize(s, Json));
                Console.Error.WriteLine($"survey: {s.Bundles.Count} bundles, errors {s.Bundles.Count(b => b.Err != null)}, detail errors {s.Bundles.Sum(b => b.DetailErrors)}");
                rc = 0; break;
            }
            case "loose": rc = Loose(); break;
            case "fmod": rc = FmodSettings(); break;
            case "reach" when a.Count >= 3: rc = Reach(a[1], a[2], Multi(a, "--seed")); break;
            case "report": rc = Report.Run(Data, Out, StrOpt(a, "--opened")); break;
            default: Console.Error.WriteLine("bad args"); rc = 2; break;
        }
        Console.Error.WriteLine($"[{a[0]}] {sw.Elapsed.TotalSeconds:F1}s, peak ws {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MiB");
        return rc;
    }

    static int Opt(List<string> a, string n, int d) { int i = a.IndexOf(n); return i >= 0 && i + 1 < a.Count && int.TryParse(a[i + 1], out var v) ? v : d; }
    static string StrOpt(List<string> a, string n) { int i = a.IndexOf(n); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
    static List<string> Multi(List<string> a, string n) { var r = new List<string>(); for (int i = 0; i + 1 < a.Count; i++) if (a[i] == n) r.Add(a[i + 1]); return r; }

    // ------------------------------------------------------------------ loose files

    static readonly string[] LooseNames =
    {
        "level0", "globalgamemanagers", "globalgamemanagers.assets", "sharedassets0.assets", "resources.assets",
        "unity default resources", "unity_builtin_extra",
    };

    static int Loose()
    {
        using var db = new AssetDatabase(Data);
        var recs = new List<BundleRec>();
        foreach (var n in LooseNames)
        {
            var rec = new BundleRec { Name = n };
            try
            {
                var f = db.GetFile(n);
                if (f == null) { rec.Err = "not found"; recs.Add(rec); continue; }
                rec.SerBytes = f.Length; rec.Uncomp = f.Length;
                string path = new[] { Path.Combine(Data, n), Path.Combine(Data, "Resources", n), Path.Combine(Path.GetDirectoryName(Data)!, n) }.FirstOrDefault(File.Exists);
                rec.Disk = path != null ? new FileInfo(path).Length : 0;
                foreach (var side in new[] { ".resS", ".resource" })
                {
                    // resources.assets.resS / sharedassets0.assets.resS / resources.resource / sharedassets0.resource
                    var p1 = path + side; var p2 = path != null ? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + side) : null;
                    foreach (var p in new[] { p1, p2 }.Where(x => x != null).Distinct())
                        if (File.Exists(p)) { long l = new FileInfo(p).Length; if (side == ".resS") rec.ResS += l; else rec.Resource += l; rec.OtherNodes += 0; }
                }
                rec.Uncomp += rec.ResS + rec.Resource; rec.Disk += rec.ResS + rec.Resource;
                Scanner.SurveyFile(f, n == "level0", rec);
                Console.Error.WriteLine($"  {n}: {f.Objects.Count} objects, typetree={f.EnableTypeTree}, detail errors {rec.DetailErrors}");
            }
            catch (Exception e) { rec.Err = e.Message; }
            recs.Add(rec);
        }
        File.WriteAllText(Path.Combine(Out, "loose.json"), JsonSerializer.Serialize(recs, Json));
        return 0;
    }

    // ------------------------------------------------------------------ FMOD settings (bank load policy; key presence only, never its value)

    static int FmodSettings()
    {
        using var db = new AssetDatabase(Data);
        foreach (var fn in new[] { "resources.assets", "globalgamemanagers.assets", "sharedassets0.assets" })
        {
            var f = db.GetFile(fn);
            if (f == null) continue;
            foreach (var o in f.ObjectsOfClass(ClassIds.MonoBehaviour))
            {
                var ms = db.GetMonoScript(f, o);
                if (ms == null || ms.Namespace != "FMODUnity" || ms.ClassName != "Settings") continue;
                Console.WriteLine($"{fn} #{o.PathId}: {ms.FullName} ({o.ByteSize} bytes) typetree={(f.GetTypeTree(o) != null)}");
                try
                {
                    var t = TypeTreeReader.Read(f, o);
                    foreach (var kv in t)
                    {
                        if (kv.Key.Contains("Key", StringComparison.OrdinalIgnoreCase) && kv.Value is string sk) { Console.WriteLine($"  {kv.Key}: <{(sk.Length > 0 ? $"set, {sk.Length} chars" : "empty")}>"); continue; }
                        string v = kv.Value switch { List<object> l => $"[{l.Count}] " + string.Join(", ", l.Take(12).Select(x => x is TypeTreeObject to ? to.ToString() : x?.ToString())), TypeTreeObject to => to.ToString(), var x => x?.ToString() };
                        Console.WriteLine($"  {kv.Key}: {(v?.Length > 300 ? v[..300] + "…" : v)}");
                    }
                }
                catch (Exception e) { Console.WriteLine($"  read failed: {e.Message}"); }
                // header-only tree -> raw scan of length-prefixed strings; only bank names / paths are echoed (anything else
                // could be the bank encryption key, so it is reported by length only)
                var bankNames = Directory.GetFiles(Path.Combine(Data, "StreamingAssets"), "*.bank")
                    .Select(p => Path.GetFileName(p).Replace(".streams.bank", "").Replace(".assets.bank", "").Replace(".strings.bank", "").Replace(".bank", ""))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var d = f.GetObjectBytes(o).ToArray();
                int listed = 0; var other = new List<int>();
                for (int i = 0; i + 4 <= d.Length; i += 4)
                {
                    int len = BitConverter.ToInt32(d, i);
                    if (len <= 0 || len > 256 || i + 4 + len > d.Length) continue;
                    var span = d.AsSpan(i + 4, len);
                    if (span.ToArray().Any(c => c < 0x20 || c > 0x7e)) continue;
                    string str = Encoding.ASCII.GetString(span);
                    if (bankNames.Contains(str) || str.Contains('/') || str.Contains('\\') || str.StartsWith("Platform") || str.Length <= 3)
                    { Console.WriteLine($"  @{i,5} str[{len}] \"{str}\""); listed++; }
                    else other.Add(len);
                    i += ((len + 3) & ~3);
                }
                Console.WriteLine($"  other strings (not echoed): {other.Count} with lengths [{string.Join(",", other)}]");
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ reachability

    static readonly HashSet<int> Leaf = new() { 28, 43, 83, 115, 49, 89, 117, 187, 329, 213 /* Sprite: read below */ };

    /// <summary>Seeds: every object of the scene file, plus (optional) every addressable asset whose internal id starts with
    /// one of <paramref name="seedPrefixes"/> (runtime-loaded content such as dungeon room pieces / enemies), located through its
    /// bundle's AssetBundle.m_Container.</summary>
    static int Reach(string key, string label, List<string> seedPrefixes)
    {
        using var db = new AssetDatabase(Data);
        var sw = Stopwatch.StartNew();
        // "level0" = the loose boot scene (Data/level0 + sharedassets0.assets), not an Addressables scene
        var sl = key == "level0"
            ? new AssetDatabase.SceneLoadResult("level0", db.GetFile("level0"), db.GetFile("sharedassets0.assets"), null, Array.Empty<string>())
            : db.LoadScene(key, eager: true);
        var closureNames = sl.Closure.Select(Path.GetFileName).ToHashSet();
        Console.Error.WriteLine($"reach {key}: closure {sl.Closure.Count} bundles loaded in {sw.ElapsedMilliseconds} ms; scene file {sl.Scene.Name} ({sl.Scene.Objects.Count} objects)");
        var rec = new BundleRec { Name = "reach:" + label };
        var q = new Queue<(SerializedFile f, ObjectInfo o)>();
        var seen = new HashSet<(string, long)>();
        foreach (var o in sl.Scene.Objects) if (seen.Add((sl.Scene.Name, o.PathId))) q.Enqueue((sl.Scene, o));
        var seededAssets = new List<string>();
        if (seedPrefixes.Count > 0)
        {
            var cat = db.Catalog;
            var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // internal id -> own bundle path
            foreach (var k in cat.StringKeys.ToList())
                foreach (var l in cat.Locate(k))
                {
                    if (l.IsBundle || l.InternalId == null || l.Dependencies.Count == 0 || !l.Dependencies[0].IsBundle) continue;
                    if (!seedPrefixes.Any(p => l.InternalId.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                    want.TryAdd(l.InternalId, l.Dependencies[0].ResolvedPath);
                    foreach (var bp in cat.BundleClosure(k)) closureNames.Add(Path.GetFileName(bp));
                }
            foreach (var grp in want.GroupBy(kv => kv.Value))
            {
                var files = db.LoadBundleFiles(grp.Key);
                var paths = grp.Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                    foreach (var abo in f.ObjectsOfClass(ClassIds.AssetBundle))
                    {
                        var ab = TypeTreeReader.Read(f, abo);
                        foreach (var e in (ab.List("m_Container") ?? new List<object>()).OfType<TypeTreeObject>())
                        {
                            if (e.Str("first") is not { } path || !paths.Contains(path)) continue;
                            var ptr = e.Obj("second")?.PPtr("asset") ?? default;
                            var (tf, to) = db.Resolve(f, ptr);
                            if (to == null) continue;
                            seededAssets.Add(path);
                            if (seen.Add((tf.Name, to.PathId))) q.Enqueue((tf, to));
                        }
                    }
            }
            Console.Error.WriteLine($"  seeded {seededAssets.Distinct(StringComparer.OrdinalIgnoreCase).Count()} of {want.Count} addressable assets matching {string.Join(" | ", seedPrefixes)}");
        }
        var pp = new List<PPtr>();
        int noTree = 0, readErr = 0, unresolved = 0, visited = 0;
        var depth = new Dictionary<(string, long), int>();
        var refs = new List<MatRef>();
        string Describe(SerializedFile f, ObjectInfo o) =>
            o.ClassId == ClassIds.MonoBehaviour ? (db.GetMonoScript(f, o)?.FullName ?? "MonoBehaviour?") : ClassIds.Name(o.ClassId);
        var perFile = new Dictionary<string, long[]>();          // file -> [objects, bytes]
        while (q.Count > 0)
        {
            var (f, o) = q.Dequeue();
            visited++;
            int dep = depth.TryGetValue((f.Name, o.PathId), out var dd) ? dd : 0;
            Scanner.Account(f, o, ReferenceEquals(f, sl.Scene), rec);
            string fk = f.Bundle != null ? Path.GetFileName(f.Bundle.Path) : f.Name;
            if (!perFile.TryGetValue(fk, out var pf)) perFile[fk] = pf = new long[2];
            pf[0]++; pf[1] += o.ByteSize;
            if (o.ClassId != 213 && Leaf.Contains(o.ClassId)) continue;
            if (f.GetTypeTree(o) == null) { noTree++; continue; }
            TypeTreeObject t;
            try { t = TypeTreeReader.Read(f, o); } catch { readErr++; continue; }
            pp.Clear();
            TypeTreeReader.CollectPPtrs(t, pp);
            foreach (var p in pp)
            {
                if (p.IsNull || p.PathId == 0) continue;
                var (tf, to) = db.Resolve(f, p);
                if (to == null) { unresolved++; continue; }
                if (seen.Add((tf.Name, to.PathId)))
                {
                    depth[(tf.Name, to.PathId)] = dep + 1;
                    q.Enqueue((tf, to));
                    if (to.ClassId is 21 or 48 or 28)
                        refs.Add(new MatRef { Cab = tf.Name, Pid = to.PathId, Cls = to.ClassId, Depth = dep + 1, Referrer = Describe(f, o), ReferrerInScene = ReferenceEquals(f, sl.Scene) });
                }
            }
            if (visited % 20000 == 0) Console.Error.WriteLine($"  visited {visited}, queue {q.Count}, {sw.Elapsed.TotalSeconds:F0}s");
        }
        // which bundles had reachable objects, and which were opened outside the catalog closure
        var loaded = db.LoadedBundles.Select(b => Path.GetFileName(b.Path)).ToHashSet();
        var outside = loaded.Where(n => !closureNames.Contains(n)).ToList();
        var result = new ReachResult
        {
            Key = key, Label = label, SceneFile = sl.Scene.Name, SceneBundle = sl.SceneBundlePath == null ? null : Path.GetFileName(sl.SceneBundlePath),
            Closure = sl.Closure.Select(Path.GetFileName).ToList(), LoadedOutsideClosure = outside,
            Visited = visited, NoTypeTree = noTree, ReadErrors = readErr, Unresolved = unresolved,
            PerFile = perFile, Rec = rec, MissingExternals = db.MissingExternals.ToList(),
            Seeds = seedPrefixes, Refs = refs, SeededAssets = seededAssets.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        };
        File.WriteAllText(Path.Combine(Out, $"reach-{label}.json"), JsonSerializer.Serialize(result, Json));
        Console.Error.WriteLine($"reach {label}: visited {visited} objects in {perFile.Count} files ({sw.Elapsed.TotalSeconds:F1}s); no-tree {noTree}, read errors {readErr}, unresolved {unresolved}; bundles loaded outside closure: {outside.Count}");
        return 0;
    }
}

public sealed class ReachResult
{
    public string Key, Label, SceneFile, SceneBundle;
    public List<string> Closure, LoadedOutsideClosure, MissingExternals, Seeds;
    public int SeededAssets;
    public List<MatRef> Refs;
    public int Visited, NoTypeTree, ReadErrors, Unresolved;
    public Dictionary<string, long[]> PerFile;
    public BundleRec Rec;
}

/// <summary>First referrer (BFS parent) of a reached Material / Shader.</summary>
public sealed class MatRef { public string Cab, Referrer; public long Pid; public int Cls, Depth; public bool ReferrerInScene; }
