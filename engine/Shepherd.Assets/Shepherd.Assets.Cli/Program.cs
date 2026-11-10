using System.Diagnostics;
using System.Text;
using Shepherd.Assets;

namespace Shepherd.Assets.Cli;

public static class Program
{
    private const string DefaultData =
        "$SHEPHERD_GAME_DATA";

    private static string Data = Environment.GetEnvironmentVariable("COTL_DATA") ?? DefaultData;

    public static int Main(string[] argv)
    {
        var args = argv.ToList();
        int di = args.IndexOf("--data");
        if (di >= 0 && di + 1 < args.Count) { Data = args[di + 1]; args.RemoveRange(di, 2); }
        if (args.Count == 0) return Usage();
        var sw = Stopwatch.StartNew();
        try
        {
            int rc = args[0] switch
            {
                "closure" when args.Count >= 2 => Closure(args[1]),
                "scene" when args.Count >= 2 => Scene(args[1], args.Contains("--no-pptr-scan")),
                "textures" when args.Count >= 3 => Textures(args[1], args[2], IntOpt(args, "--max", 150)),
                "dump" when args.Count >= 3 => Dump(args[1], long.Parse(args[2]), IntOpt(args, "--items", 8)),
                "level0" => Level0(),
                "scan" => Scan(),
                "keys" when args.Count >= 2 => Keys(args[1], IntOpt(args, "--max", 200)),
                "verify" => Verify(args.Skip(1).ToList()),
                "texall" => TexAll(args.Contains("--decode")),
                "ls" when args.Count >= 2 => Ls(args[1], args.Count >= 3 && int.TryParse(args[2], out var cls) ? cls : (int?)null),
                "selftest" => SelfTest(),
                "crndump" when args.Count >= 3 => CrnDump(args[1], args[2], IntOpt(args, "--max", 40)),
                "shadercull" when args.Count >= 2 => ShaderCull(args[1]),
                "phys2d" when args.Count >= 2 => Phys2d(args[1]),
                _ => Usage(),
            };
            Console.Error.WriteLine($"[{args[0]}] done in {sw.Elapsed.TotalSeconds:F2}s, peak working set {MaxRssMiB()} MiB");
            return rc;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e}");
            return 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc")] private static extern int getrusage(int who, long[] usage);

    /// <summary>Peak resident set size (macOS: ru_maxrss is in bytes).</summary>
    private static long MaxRssMiB()
    {
        var u = new long[18];
        try { if (getrusage(0, u) == 0) return (OperatingSystem.IsMacOS() ? u[4] : u[4] * 1024) / (1024 * 1024); } catch { }
        return Environment.WorkingSet / (1024 * 1024);
    }

    private static int IntOpt(List<string> a, string name, int def)
    {
        int i = a.IndexOf(name);
        return i >= 0 && i + 1 < a.Count && int.TryParse(a[i + 1], out var v) ? v : def;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage: Shepherd.Assets.Cli [--data <Data dir>] <command>
              closure <key>                 bundle closure of an Addressables key
              scene <key> [--no-pptr-scan]  load closure, print the scene's GameObject hierarchy + totals
              textures <key> <outdir> [--max N]  decode Texture2Ds reachable from the scene to PNG (default cap 150)
              dump <bundle|file|CAB> <pathId> [--items N]  print one object via its type tree
              level0                        print DATA/level0's hierarchy using harvested fallback type trees
              scan                          bundle survey (compression, node naming, type trees)
              keys <substring>              search catalog keys
              shadercull <out.tsv>          every Shader's main-pass Cull state (kit #7 G6: the owner-local data/shader-cull.tsv)
              phys2d <out.bin>              globalgamemanagers' Physics2DSettings record, as stored (kit #8 physics: the owner-local data/physics2d-settings.bin)
            """);
        return 2;
    }

    // ------------------------------------------------------------------ closure

    private static int Closure(string key)
    {
        using var db = new AssetDatabase(Data);
        var sw = Stopwatch.StartNew();
        var cat = db.Catalog;
        Console.WriteLine($"catalog: {cat.InternalIds.Count} internal ids, {cat.Keys.Count} keys, {cat.EntryCount} entries (parsed in {sw.ElapsedMilliseconds} ms)");
        var locs = cat.Locate(key);
        foreach (var l in locs)
            Console.WriteLine($"location: {l.InternalId}  provider={l.ProviderId}  type={l.ResourceType}  deps={l.Dependencies.Count}");
        // aliases: other keys (GUID / address / label) that locate the same internal id
        var ids = locs.Select(l => l.InternalId).ToHashSet();
        var aliases = cat.StringKeys.Where(k => k != key && cat.Locate(k).Any(l => ids.Contains(l.InternalId) && !l.IsBundle)).Take(10).ToList();
        if (aliases.Count > 0) Console.WriteLine("aliases: " + string.Join(", ", aliases) + $" (GUID alias closure: {cat.BundleClosure(aliases[0]).Count} bundles)");
        var closure = cat.BundleClosure(key);
        long total = 0;
        Console.WriteLine($"closure: {closure.Count} bundles (absolute paths; common prefix {db.BundleDir}/)");
        foreach (var p in closure)
        {
            long len = File.Exists(p) ? new FileInfo(p).Length : -1;
            total += Math.Max(0, len);
            string shown = p.StartsWith(db.BundleDir + "/", StringComparison.Ordinal) ? p[(db.BundleDir.Length + 1)..] : p;
            Console.WriteLine($"  {shown}{(len < 0 ? "  [MISSING]" : $"  ({len / 1024.0:F0} KiB)")}");
        }
        Console.WriteLine($"total on disk: {total / (1024.0 * 1024):F1} MiB");
        return closure.Count > 0 ? 0 : 1;
    }

    private static int Keys(string sub, int max)
    {
        using var db = new AssetDatabase(Data);
        int n = 0;
        foreach (var k in db.Catalog.StringKeys)
            if (k.Contains(sub, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(k); if (++n >= max) break; }
        return 0;
    }

    // ------------------------------------------------------------------ scene

    private sealed record LoadedScene(SerializedFile Scene, SerializedFile Shared, IReadOnlyList<string> Closure, string SceneBundle);

    private static LoadedScene LoadScene(AssetDatabase db, string key, bool verbose = true)
    {
        var sw = Stopwatch.StartNew();
        var r = db.LoadScene(key, eager: true);
        if (verbose)
        {
            long bytes = 0; int files = 0, objects = 0;
            foreach (var bp in r.Closure)
                foreach (var f in db.LoadBundleFiles(bp)) { files++; objects += f.Objects.Count; bytes += f.Length; }
            Console.WriteLine($"loaded {r.Closure.Count} bundles -> {files} serialized files, {objects:N0} objects, {bytes / (1024.0 * 1024):F1} MiB uncompressed in {sw.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"scene bundle: {Path.GetFileName(r.SceneBundlePath)} (AssetBundle.m_SceneHashes: \"{r.ScenePath}\" -> {r.Scene.Name})");
            foreach (var n in db.OpenBundle(r.SceneBundlePath).Nodes) Console.WriteLine($"  node \"{n.Name}\" size={n.Size:N0} flags=0x{n.Flags:X}");
        }
        return new LoadedScene(r.Scene, r.SharedAssets, r.Closure, r.SceneBundlePath);
    }

    private static int Scene(string key, bool noScan)
    {
        using var db = new AssetDatabase(Data);
        var ls = LoadScene(db, key);
        var scene = ls.Scene;
        Console.WriteLine($"scene file: {scene}");
        if (ls.Shared != null) Console.WriteLine($"shared file: {ls.Shared}");
        Console.WriteLine($"externals of scene file ({scene.Externals.Count}):");
        foreach (var e in scene.Externals.Take(12)) Console.WriteLine($"  {e.PathName}");
        if (scene.Externals.Count > 12) Console.WriteLine($"  … {scene.Externals.Count - 12} more");
        Console.WriteLine($"ref types ([SerializeReference]): scene={scene.RefTypes.Count}" +
                          (ls.Shared != null ? $", shared={ls.Shared.RefTypes.Count}" : ""));

        var g = SceneGraph.Build(db, scene);
        Console.WriteLine();
        Console.WriteLine("=== hierarchy ===");
        var sb = new StringBuilder();
        foreach (var r in g.Roots) PrintNode(sb, r, 0);
        Console.Write(sb);
        Console.WriteLine();
        PrintTotals(db, g, ls.Scene, ls.Shared, noScan);
        return 0;
    }

    private static void PrintNode(StringBuilder sb, SceneNode n, int depth)
    {
        sb.Append(' ', depth * 2).Append(n.Name.Length == 0 ? "<unnamed>" : n.Name);
        if (!n.IsActive) sb.Append(" [inactive]");
        sb.Append("  : ");
        sb.Append(string.Join(", ", n.Components.Select(c => c.DisplayName)));
        sb.Append('\n');
        foreach (var c in n.Children) PrintNode(sb, c, depth + 1);
    }

    private static void PrintTotals(AssetDatabase db, SceneGraph.Result g, SerializedFile scene, SerializedFile shared, bool noScan)
    {
        Console.WriteLine("=== totals ===");
        int nodes = g.ByGameObject.Count;
        Console.WriteLine($"GameObjects: {nodes}, roots: {g.Roots.Count}");
        foreach (var f in new[] { scene, shared })
        {
            if (f == null) continue;
            Console.WriteLine($"objects per class in {f.Name} ({f.Objects.Count}):");
            foreach (var grp in f.Objects.GroupBy(o => o.ClassId).OrderByDescending(x => x.Count()))
                Console.WriteLine($"  {grp.Count(),6}  {ClassIds.Name(grp.Key)} ({grp.Key})");
        }
        Console.WriteLine($"MonoBehaviours on scene GameObjects: {g.MonoBehaviours}, MonoScript resolved: {g.MonoScriptsResolved}, unresolved: {g.MonoBehaviours - g.MonoScriptsResolved}");
        int allMb = 0, allMbOk = 0;
        foreach (var f in new[] { scene, shared })
            if (f != null)
                foreach (var o in f.ObjectsOfClass(ClassIds.MonoBehaviour)) { allMb++; if (db.GetMonoScript(f, o) != null) allMbOk++; }
        Console.WriteLine($"all MonoBehaviour objects in the scene file(s): {allMb}, MonoScript resolved: {allMbOk}");
        var scripts = g.ByGameObject.Values.SelectMany(n => n.Components).Where(c => c.Script != null)
            .GroupBy(c => c.Script.AssemblyName).OrderByDescending(x => x.Count());
        Console.WriteLine("scripts by assembly: " + string.Join(", ", scripts.Select(x => $"{x.Key}={x.Count()}")));
        foreach (var w in g.Warnings.Take(20)) Console.WriteLine($"warning: {w}");

        if (noScan) return;
        // full PPtr scan: decode every object of the scene file(s), resolve every pointer
        var sw = Stopwatch.StartNew();
        int total = 0, unresolved = 0, readFail = 0, objects = 0, partial = 0, partialMb = 0;
        var byTarget = new Dictionary<string, int>();
        var failByClass = new Dictionary<string, int>();
        foreach (var f in new[] { scene, shared })
        {
            if (f == null) continue;
            foreach (var o in f.Objects)
            {
                objects++;
                TypeTreeObject t;
                try { t = TypeTreeReader.Read(f, o); }
                catch (Exception e)
                {
                    readFail++;
                    var k = $"{ClassIds.Name(o.ClassId)}: {e.Message.Split('\n')[0]}";
                    failByClass[k] = failByClass.GetValueOrDefault(k) + 1;
                    continue;
                }
                if (t.BytesRead != o.ByteSize) { partial++; if (o.ClassId == ClassIds.MonoBehaviour && !f.EnableTypeTree) partialMb++; }
                var ptrs = new List<PPtr>();
                TypeTreeReader.CollectPPtrs(t, ptrs);
                foreach (var p in ptrs)
                {
                    total++;
                    var (tf, to) = db.Resolve(f, p);
                    if (to.IsNull)
                    {
                        unresolved++;
                        string target = p.FileId == 0 ? f.Name : p.FileId <= f.Externals.Count ? f.Externals[p.FileId - 1].PathName : $"fileId {p.FileId}?";
                        target += db.GetExternalFile(f, p.FileId) == null ? " (file missing)" : " (object missing)";
                        byTarget[target] = byTarget.GetValueOrDefault(target) + 1;
                    }
                }
            }
        }
        Console.WriteLine($"PPtr scan: {objects} objects decoded ({readFail} failed, {partial} with size mismatch{(partialMb > 0 ? $" of which {partialMb} MonoBehaviours read header-only (no script type tree in a stripped file)" : "")}), {total} non-null PPtrs, {unresolved} unresolved ({sw.Elapsed.TotalSeconds:F2}s)");
        foreach (var kv in byTarget.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  unresolved -> {kv.Key}: {kv.Value}");
        foreach (var kv in failByClass.OrderByDescending(x => x.Value).Take(10)) Console.WriteLine($"  read failure x{kv.Value}: {kv.Key}");
    }

    // ------------------------------------------------------------------ textures

    private static int Textures(string key, string outDir, int max)
    {
        using var db = new AssetDatabase(Data);
        Directory.CreateDirectory(outDir);
        // a catalog key -> scene (or any addressable) reachability walk; otherwise a file/bundle/CAB -> its textures
        IReadOnlyList<SerializedFile> roots;
        bool looseMode = db.Catalog.Locate(key).Count == 0;
        if (looseMode)
        {
            roots = OpenTarget(db, key);
            Console.WriteLine($"'{key}' is not a catalog key; walking file(s) {string.Join(", ", roots.Select(r => r.Name))}");
        }
        else
        {
            var ls = LoadScene(db, key);
            roots = new[] { ls.Scene, ls.Shared }.Where(f => f != null).ToList();
        }

        // reachability walk from every object of the root file(s)
        var sw = Stopwatch.StartNew();
        var visited = new HashSet<(string, long)>();
        var queue = new Queue<(SerializedFile, ObjectInfo)>();
        var textures = new List<(SerializedFile f, ObjectInfo o)>();
        foreach (var f in roots)
            foreach (var o in f.Objects) { visited.Add((f.Name, o.PathId)); queue.Enqueue((f, o)); }
        int decoded = 0, walkFail = 0;
        var noExpand = new HashSet<int> { ClassIds.Shader, ClassIds.Mesh, ClassIds.AudioClip, ClassIds.MonoScript, ClassIds.Texture2D, ClassIds.Font, 49 /*TextAsset*/ };
        while (queue.Count > 0)
        {
            var (f, o) = queue.Dequeue();
            if (o.ClassId == ClassIds.Texture2D) textures.Add((f, o));
            if (noExpand.Contains(o.ClassId)) continue;
            if (looseMode && !roots.Contains(f) && textures.Count >= max) continue; // loose-file mode: don't wander the whole game
            TypeTreeObject t;
            try { t = TypeTreeReader.Read(f, o); decoded++; }
            catch { walkFail++; continue; }
            var ptrs = new List<PPtr>();
            TypeTreeReader.CollectPPtrs(t, ptrs);
            foreach (var p in ptrs)
            {
                var (tf, to) = db.Resolve(f, p);
                if (to.IsNull) continue;
                if (visited.Add((tf.Name, to.PathId))) queue.Enqueue((tf, to));
            }
        }
        Console.WriteLine($"reachability: {visited.Count:N0} objects reached ({decoded:N0} decoded, {walkFail} undecodable) in {sw.Elapsed.TotalSeconds:F2}s; {textures.Count} Texture2D reachable");

        var hist = new SortedDictionary<string, int>();
        var fails = new SortedDictionary<string, int>();
        int written = 0, attempted = 0, empty = 0;
        long pixels = 0;
        sw.Restart();
        var manifest = new StringBuilder();
        var failLines = new List<string>();
        // biggest textures first is not useful; keep reachability order (scene-near first)
        foreach (var (f, o) in textures)
        {
            TypeTreeObject t;
            try { t = TypeTreeReader.Read(f, o); }
            catch (Exception e) { fails[$"header read: {e.Message}"] = fails.GetValueOrDefault($"header read: {e.Message}") + 1; continue; }
            var fmt = (TextureFormat)t.Int("m_TextureFormat");
            string fname = $"{fmt} ({(int)fmt})";
            hist[fname] = hist.GetValueOrDefault(fname) + 1;
            if (attempted >= max) continue;
            attempted++;
            string name = t.Str("m_Name") ?? "tex";
            try
            {
                var (w, h, rgba, _) = TextureDecoder.DecodeTexture2D(db, f, t);
                if (w == 0 || h == 0) { empty++; continue; }
                string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
                string file = Path.Combine(outDir, $"{safe}_{(o.PathId & 0xFFFFFFF):x}.png");
                PngWriter.Write(file, w, h, rgba);
                written++;
                pixels += (long)w * h;
                manifest.AppendLine($"{Path.GetFileName(file)}\t{w}x{h}\t{fmt}\t{f.Name}\t{o.PathId}");
            }
            catch (Exception e)
            {
                string k = $"{fmt}: {e.Message}";
                fails[k] = fails.GetValueOrDefault(k) + 1;
                failLines.Add($"  FAIL {name} ({fmt} {t.Int("m_Width")}x{t.Int("m_Height")}, {f.Name} #{o.PathId}): {e.Message}");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.tsv"), manifest.ToString());
        Console.WriteLine($"decoded+wrote {written}/{attempted} attempted (cap {max}; {empty} empty 0x0 runtime textures skipped), {pixels / 1e6:F1} Mpx in {sw.Elapsed.TotalSeconds:F2}s -> {outDir}");
        Console.WriteLine("TextureFormat histogram (all reachable Texture2D):");
        foreach (var kv in hist.OrderByDescending(x => x.Value)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}{(TextureDecoder.IsSupported((TextureFormat)int.Parse(kv.Key.Split('(', ')')[1])) ? "" : "   <-- NOT SUPPORTED")}");
        Console.WriteLine(fails.Count == 0 ? "failures: none" : "failures:");
        foreach (var kv in fails) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        foreach (var l in failLines.Take(30)) Console.WriteLine(l);
        return 0;
    }

    // ------------------------------------------------------------------ dump

    private static IReadOnlyList<SerializedFile> OpenTarget(AssetDatabase db, string target)
    {
        if (target.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            return db.LoadBundleFiles(File.Exists(target) ? target : Path.Combine(db.BundleDir, target))
                .OrderBy(f => f.Name.EndsWith(".sharedAssets", StringComparison.Ordinal) ? 1 : 0).ToList(); // scene file first
        var f = File.Exists(target) && !target.Contains("archive:")
            ? SerializedFile.Load(Path.GetFileName(target), File.ReadAllBytes(target), Path.GetDirectoryName(Path.GetFullPath(target)))
            : db.GetFile(target);
        if (f == null) throw new FileNotFoundException(target);
        db.AddFile(f);
        if (!f.EnableTypeTree) db.EnsureFallbackTypeTrees(f.Types);
        return new[] { f };
    }

    private static int Ls(string target, int? classId)
    {
        using var db = new AssetDatabase(Data);
        foreach (var f in OpenTarget(db, target))
        {
            Console.WriteLine($"{f}  externals={f.Externals.Count} refTypes={f.RefTypes.Count}");
            foreach (var o in f.Objects)
            {
                if (classId != null && o.ClassId != classId) continue;
                string name = "";
                try
                {
                    if (o.ClassId == ClassIds.MonoBehaviour) name = db.GetMonoScript(f, o)?.FullName ?? "?";
                    else if (f.GetTypeTree(o) is { } tt && tt.Count > 1 && tt.Any(n => n.Level == 1 && n.Name == "m_Name"))
                        name = TypeTreeReader.Read(f, o).Str("m_Name") ?? "";
                }
                catch (Exception e) { name = "<" + e.Message + ">"; }
                Console.WriteLine($"  {o.PathId,22}  {ClassIds.Name(o.ClassId),-22} {o.ByteSize,9}  {name}");
            }
        }
        return 0;
    }

    private static int Dump(string target, long pathId, int items)
    {
        using var db = new AssetDatabase(Data);
        IReadOnlyList<SerializedFile> files = OpenTarget(db, target);
        foreach (var f in files)
        {
            var o = f.GetObject(pathId);
            if (o.IsNull) continue;
            Console.WriteLine($"{f.Name} #{pathId}: {ClassIds.Name(o.ClassId)} (class {o.ClassId}), {o.ByteSize} bytes @ {o.ByteStart}");
            var t = TypeTreeReader.Read(f, o);
            if (t.BytesRead != o.ByteSize) Console.WriteLine($"(note: type tree consumed {t.BytesRead} of {o.ByteSize} bytes)");
            if (o.ClassId == ClassIds.MonoBehaviour)
            {
                var s = db.GetMonoScript(f, o);
                Console.WriteLine($"script: {s?.FullName ?? "<unresolved>"} [{s?.AssemblyName}]");
            }
            Console.Write(t.ToPrettyString(items));
            return 0;
        }
        Console.Error.WriteLine($"pathId {pathId} not found in {string.Join(", ", files.Select(f => f.Name))}");
        return 1;
    }

    // ------------------------------------------------------------------ level0

    private static int Level0()
    {
        using var db = new AssetDatabase(Data);
        var level0 = db.GetFile("level0") ?? throw new FileNotFoundException("level0");
        Console.WriteLine($"level0: {level0}");
        Console.WriteLine("externals: " + string.Join(", ", level0.Externals.Select(e => e.PathName)));
        var ggm = db.GetFile("globalgamemanagers.assets");
        var sw = Stopwatch.StartNew();
        var missing = db.EnsureFallbackTypeTrees(level0.Types.Concat(ggm?.Types ?? Array.Empty<SerializedType>()));
        Console.WriteLine($"type-tree index: {db.TypeTreeIndex.Count} classes from bundles ({sw.Elapsed.TotalSeconds:F2}s); " +
                          $"fallbacks registered: {db.HarvestLog.Count}; classes with no bundle type tree: " +
                          (missing.Count == 0 ? "none" : string.Join(", ", missing.Select(c => $"{ClassIds.Name(c)}({c})"))));
        int mbTypes = level0.Types.Count(t => t.ClassId == ClassIds.MonoBehaviour);
        int mbExact = level0.Types.Count(t => t.ClassId == ClassIds.MonoBehaviour && SerializedFile.HasFallbackTypeTree(ClassIds.MonoBehaviour, t.OldTypeHashHex));
        Console.WriteLine($"level0 MonoBehaviour script layouts: {mbTypes}, matched to full bundle type trees by OldTypeHash: {mbExact} (those read completely; the rest header-only)");
        var level0Classes = new HashSet<int>(level0.Types.Select(t => t.ClassId));
        foreach (var (cid, src) in db.HarvestLog.OrderBy(x => x.classId))
            Console.WriteLine($"  {(level0Classes.Contains(cid) ? "L0 " : "   ")}{ClassIds.Name(cid),-28} ({cid,4})  {src}");
        var g = SceneGraph.Build(db, level0);
        Console.WriteLine();
        Console.WriteLine("=== level0 hierarchy ===");
        var sb = new StringBuilder();
        foreach (var r in g.Roots) PrintNode(sb, r, 0);
        Console.Write(sb);
        Console.WriteLine();
        PrintTotals(db, g, level0, null, false);
        return 0;
    }

    // ------------------------------------------------------------------ scan

    private static int Scan()
    {
        using var db = new AssetDatabase(Data);
        var paths = Directory.GetFiles(db.BundleDir, "*.bundle");
        var comp = new Dictionary<int, long>();
        var headerFlags = new Dictionary<string, int>();
        var nodeKinds = new Dictionary<string, int>();
        var versions = new Dictionary<string, int>();
        int typeTreeOn = 0, typeTreeOff = 0, refTypes = 0, sceneBundles = 0, errors = 0;
        var sceneNames = new List<string>();
        var refBundles = new List<string>();
        var sw = Stopwatch.StartNew();
        object l = new();
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 8 }, p =>
        {
            try
            {
                using var b = BundleFile.Open(p);
                var localKinds = new List<string>();
                bool tt = false, scene = false; int rt = 0; string ver = null;
                foreach (var n in b.Nodes)
                {
                    // node naming pattern: prefix up to the first '-' + extension (e.g. "CAB-*.resS", "BuildPlayer-*")
                    int dash = n.Name.IndexOf('-');
                    string kind = (dash > 0 ? n.Name[..(dash + 1)] + "*" : "other:" + n.Name) + Path.GetExtension(n.Name);
                    localKinds.Add($"{kind} flags=0x{n.Flags:X}");
                    if (n.Name.EndsWith(".sharedAssets")) { scene = true; lock (l) sceneNames.Add(n.Name); }
                    if (n.IsSerializedFile)
                    {
                        var sf = AssetDatabase.LoadMetadataOnly(b, n);
                        tt |= sf.EnableTypeTree; rt += sf.RefTypes.Count; ver = $"{sf.UnityVersion} v{sf.FormatVersion} platform {sf.TargetPlatform}";
                        if (sf.RefTypes.Count > 0) lock (l) refBundles.Add($"{Path.GetFileName(p)}:{n.Name} [{string.Join(", ", sf.RefTypes.Select(x => $"{x.NameSpace}.{x.KlassName}"))}]");
                    }
                }
                lock (l)
                {
                    foreach (var kv in b.BlockCompressionHistogram) comp[kv.Key] = comp.GetValueOrDefault(kv.Key) + kv.Value;
                    string hf = $"fmt{b.FormatVersion} {b.UnityVersion}/{b.UnityRevision} flags=0x{b.HeaderFlags:X}";
                    headerFlags[hf] = headerFlags.GetValueOrDefault(hf) + 1;
                    foreach (var k in localKinds) nodeKinds[k] = nodeKinds.GetValueOrDefault(k) + 1;
                    if (ver != null) versions[ver] = versions.GetValueOrDefault(ver) + 1;
                    if (tt) typeTreeOn++; else typeTreeOff++;
                    refTypes += rt;
                    if (scene) sceneBundles++;
                }
            }
            catch (Exception e) { lock (l) { errors++; Console.WriteLine($"ERR {Path.GetFileName(p)}: {e.Message}"); } }
        });
        Console.WriteLine($"{paths.Length} bundles scanned in {sw.Elapsed.TotalSeconds:F2}s, {errors} errors");
        Console.WriteLine("bundle headers: " + string.Join("; ", headerFlags.Select(kv => $"{kv.Key} x{kv.Value}")));
        Console.WriteLine("storage block compression (0 none,1 LZMA,2 LZ4,3 LZ4HC): " + string.Join(", ", comp.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine("serialized files: " + string.Join("; ", versions.Select(kv => $"{kv.Key} x{kv.Value}")));
        Console.WriteLine($"type trees: on={typeTreeOn} off={typeTreeOff}; [SerializeReference] ref types total={refTypes}");
        Console.WriteLine($"scene bundles: {sceneBundles}");
        Console.WriteLine("node kinds:");
        foreach (var kv in nodeKinds.OrderByDescending(x => x.Value).Take(20)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        Console.WriteLine($"scene bundles' .sharedAssets nodes: {sceneNames.Count}");
        foreach (var rb in refBundles) Console.WriteLine($"  [SerializeReference] in {rb}");
        return 0;
    }

    /// <summary>Write crunched textures of a key's closure as .crn plus our managed unpack (.cs.dxt) for diffing against crn_decomp.h.</summary>
    private static int CrnDump(string key, string outDir, int max)
    {
        using var db = new AssetDatabase(Data);
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var bp in db.Catalog.BundleClosure(key))
        {
            foreach (var f in db.LoadBundleFiles(bp))
            {
                foreach (var o in f.ObjectsOfClass(ClassIds.Texture2D))
                {
                    if (n >= max) return 0;
                    var t = TypeTreeReader.Read(f, o);
                    var fmt = (TextureFormat)t.Int("m_TextureFormat");
                    if (fmt is not (TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched)) continue;
                    var data = TextureDecoder.GetImageData(db, f, t);
                    string baseName = Path.Combine(outDir, $"{n:D3}_{fmt}");
                    File.WriteAllBytes(baseName + ".crn", data);
                    try
                    {
                        var (blocks, cf, w, h) = Crunch.UnpackLevel(data, 0);
                        File.WriteAllBytes(baseName + ".cs.dxt", blocks);
                        Console.WriteLine($"{Path.GetFileName(baseName)} {t.Str("m_Name")} {w}x{h} {cf} {data.Length} -> {blocks.Length}");
                    }
                    catch (Exception e) { Console.WriteLine($"{Path.GetFileName(baseName)} {t.Str("m_Name")} FAILED: {e.Message}"); }
                    n++;
                }
            }
        }
        return 0;
    }

    /// <summary>Decode every object of the given bundles (or all bundles) with its type tree; report failures and
    /// objects whose decoded size differs from the stored size.</summary>
    private static int Verify(List<string> targets)
    {
        var bundleDir = Path.Combine(Data, "StreamingAssets", "aa", "StandaloneOSX");
        var paths = targets.Count == 0 || targets[0] == "all"
            ? Directory.GetFiles(bundleDir, "*.bundle")
            : targets.Select(t => File.Exists(t) ? t : Path.Combine(bundleDir, t)).ToArray();
        long objects = 0, bytes = 0, fails = 0, mismatches = 0, refObjs = 0;
        var failByType = new Dictionary<string, int>();
        var sw = Stopwatch.StartNew();
        object l = new();
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 6 }, p =>
        {
            using var b = BundleFile.Open(p);
            foreach (var n in b.Nodes)
            {
                if (!n.IsSerializedFile) continue;
                var f = SerializedFile.Load(n.Name, b.ReadNode(n.Name));
                long o1 = 0, b1 = 0, f1 = 0, m1 = 0, r1 = 0;
                var local = new List<string>();
                foreach (var o in f.Objects)
                {
                    o1++; b1 += o.ByteSize;
                    try
                    {
                        var t = TypeTreeReader.Read(f, o);
                        if (t.BytesRead != o.ByteSize) { m1++; local.Add($"size {ClassIds.Name(o.ClassId)}: read {t.BytesRead} of {o.ByteSize} ({Path.GetFileName(p)} #{o.PathId})"); }
                        if (f.RefTypes.Count > 0 && t.Has("references")) r1++;
                    }
                    catch (Exception e) { f1++; local.Add($"fail {ClassIds.Name(o.ClassId)}: {e.Message.Split('\n')[0]} ({Path.GetFileName(p)} #{o.PathId})"); }
                }
                lock (l)
                {
                    objects += o1; bytes += b1; fails += f1; mismatches += m1; refObjs += r1;
                    foreach (var k in local) { var key = k[..k.IndexOf(':')]; failByType[k] = failByType.GetValueOrDefault(k) + 1; }
                }
            }
        });
        Console.WriteLine($"verified {paths.Length} bundles: {objects:N0} objects, {bytes / (1024.0 * 1024):F0} MiB of object data in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  decode failures: {fails}, size mismatches: {mismatches}, MonoBehaviours with a [SerializeReference] registry: {refObjs}");
        foreach (var kv in failByType.Take(25)) Console.WriteLine($"  {kv.Key}");
        return fails == 0 && mismatches == 0 ? 0 : 1;
    }

    /// <summary>TextureFormat histogram over every Texture2D in every bundle (+ optional full decode as a robustness test).</summary>
    private static int TexAll(bool decode)
    {
        var paths = Directory.GetFiles(Path.Combine(Data, "StreamingAssets", "aa", "StandaloneOSX"), "*.bundle");
        var hist = new SortedDictionary<string, int>();
        var fails = new List<string>();
        long px = 0; int ok = 0;
        object l = new();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 6 }, p =>
        {
            using var db = new AssetDatabase(Data);
            foreach (var f in db.LoadBundleFiles(p))
            {
                foreach (var o in f.ObjectsOfClass(ClassIds.Texture2D))
                {
                    var t = TypeTreeReader.Read(f, o);
                    var fmt = (TextureFormat)t.Int("m_TextureFormat");
                    lock (l) hist[$"{fmt} ({(int)fmt})"] = hist.GetValueOrDefault($"{fmt} ({(int)fmt})") + 1;
                    if (!decode) continue;
                    try
                    {
                        var (w, h, rgba, _) = TextureDecoder.DecodeTexture2D(db, f, t);
                        lock (l) { ok++; px += (long)w * h; }
                    }
                    catch (Exception e) { lock (l) fails.Add($"{t.Str("m_Name")} {fmt} {t.Int("m_Width")}x{t.Int("m_Height")} ({Path.GetFileName(p)}): {e.Message}"); }
                }
            }
        });
        Console.WriteLine($"{hist.Values.Sum()} Texture2D in {paths.Length} bundles ({sw.Elapsed.TotalSeconds:F1}s)" + (decode ? $"; decoded {ok} ({px / 1e6:F0} Mpx), {fails.Count} failed" : ""));
        foreach (var kv in hist.OrderByDescending(x => x.Value)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        foreach (var fl in fails.Take(30)) Console.WriteLine($"  FAIL {fl}");
        return fails.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ phys2d (device kit #8, track physics)
    /// <summary>The project's Physics2DSettings (class 19) from the player's globalgamemanagers, byte for byte as Unity stored it (280 B in
    /// 2022.3): the Vita play apps' engine reads it from the owner folder's data/physics2d-settings.bin at physics level 1+
    /// (Runtime/Physics2DSettings.cs, SHEPHERD_PHYS2D_SETTINGS) -- their strict card pack, recorded with physics off, has no copy.
    /// GAME-DERIVED, owner-local, never in git (stage it like data/video-lengths.tsv).</summary>
    private static int Phys2d(string outPath)
    {
        using var db = new AssetDatabase(Data);
        var gm = db.GetFile("globalgamemanagers");
        var o = gm != null ? gm.ObjectsOfClass(19).FirstOrDefault() : default;
        if (o.IsNull) { Console.Error.WriteLine("phys2d: no Physics2DSettings record in globalgamemanagers"); return 1; }
        var b = gm.GetObjectBytes(o).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
        File.WriteAllBytes(outPath, b);
        Console.WriteLine($"phys2d: globalgamemanagers #{o.PathId}, {b.Length} B -> {outPath} (sha256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b))[..16].ToLowerInvariant()}){(b.Length == 280 ? "" : " -- NOT the 2022.3 size (280 B)")}");
        return b.Length == 280 ? 0 : 1;
    }

    // ------------------------------------------------------------------ shadercull (device kit #7, track probes, G6)
    /// <summary>Every Shader in the game data (the Addressables bundles + the player's *.assets / level files): its first subshader's main
    /// pass -- the first normal pass (not a GrabPass / UsePass) whose LightMode is not a shadow / deferred / additive-light one, the engine's
    /// rule (Render/ShaderPolicy.Parse) -- and that pass's Cull: TSV "name TAB mode (0 Off, 1 Front, 2 Back) TAB the driving property or
    /// empty". The engine reads it from the owner folder's data/shader-cull.tsv when a shader asset carries no culling field (the Vita
    /// pack's reduction): GAME-DERIVED, owner-local, never in git (stage it like data/video-lengths.tsv).</summary>
    private static int ShaderCull(string outPath)
    {
        var notMain = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ShadowCaster", "Meta", "Deferred", "PrepassBase", "PrepassFinal", "ForwardAdd", "MotionVectors", "DepthOnly", "ShadowCollector" };
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        int shaders = 0, noState = 0;
        string Tag(object tags, string key)
        {
            if (tags is TypeTreeObject t && t["tags"] is List<object> l)
                foreach (var kv in l) if (kv is TypeTreeObject p && string.Equals(p.Str("first"), key, StringComparison.OrdinalIgnoreCase)) return p.Str("second");
            return null;
        }
        void One(SerializedFile f, string where)
        {
            foreach (var o in f.ObjectsOfClass(ClassIds.Shader))
            {
                TypeTreeObject t;
                try { t = TypeTreeReader.Read(f, o); } catch (Exception) { continue; }
                if (t["m_ParsedForm"] is not TypeTreeObject pf) continue;
                var name = pf.Str("m_Name"); if (string.IsNullOrEmpty(name)) continue;
                shaders++;
                TypeTreeObject main = null;
                if (pf["m_SubShaders"] is List<object> subs && subs.Count > 0 && subs[0] is TypeTreeObject ss && ss["m_Passes"] is List<object> passes)
                    foreach (var po in passes)
                    {
                        if (po is not TypeTreeObject pass || pass.Long("m_Type") != 0) continue;
                        var lm = Tag(pass["m_Tags"], "LightMode") ?? Tag((pass["m_State"] as TypeTreeObject)?["m_Tags"], "LightMode");
                        if (lm != null && notMain.Contains(lm)) continue;
                        main = pass; break;
                    }
                if (main?["m_State"] is not TypeTreeObject st || st["culling"] is not TypeTreeObject cu) { noState++; continue; }
                var prop = cu.Str("name"); if (prop == "<noninit>") prop = "";
                int mode = (int)cu.Float("val");
                if (!string.IsNullOrEmpty(prop) && pf["m_PropInfo"] is TypeTreeObject pi && pi["m_Props"] is List<object> props)
                    foreach (var pr in props) if (pr is TypeTreeObject pt && pt.Str("m_Name") == prop) mode = (int)pt.Float("m_DefValue[0]");
                var row = mode + "\t" + (prop ?? "");
                if (rows.TryGetValue(name, out var had) && had != row) conflicts.Add(name + ": " + had.Replace("\t", " ") + " vs " + row.Replace("\t", " ") + " (" + where + ")");
                else rows[name] = row;
            }
        }
        using (var db = new AssetDatabase(Data))
            foreach (var p in Directory.GetFiles(db.BundleDir, "*.bundle"))
                try { foreach (var f in db.LoadBundleFiles(p)) One(f, Path.GetFileName(p)); } catch (Exception e) { Console.Error.WriteLine($"  {Path.GetFileName(p)}: {e.Message}"); }
        foreach (var file in Directory.GetFiles(Data).Where(x => x.EndsWith(".assets", StringComparison.Ordinal) || Path.GetFileName(x).StartsWith("level", StringComparison.Ordinal) && !x.Contains('.')))
            try { using var db = new AssetDatabase(Data); foreach (var f in OpenTarget(db, file)) One(f, Path.GetFileName(file)); } catch (Exception e) { Console.Error.WriteLine($"  {Path.GetFileName(file)}: {e.Message}"); }
        using (var w = new StreamWriter(outPath))
        {
            w.WriteLine("# Shepherd shader cull table (kit #7 G6): name TAB Cull mode (0 Off, 1 Front, 2 Back) TAB the material property that drives it");
            w.WriteLine("# GAME-DERIVED, owner-local: made from the owner's install by Shepherd.Assets.Cli shadercull; never in git");
            foreach (var kv in rows) w.WriteLine(kv.Key + "\t" + kv.Value);
        }
        int off = rows.Values.Count(v => v.StartsWith("0")), front = rows.Values.Count(v => v.StartsWith("1")), back = rows.Values.Count(v => v.StartsWith("2"));
        Console.WriteLine($"{shaders} shader objects, {rows.Count} distinct names -> {outPath}: Off {off}, Front {front}, Back {back}; {noState} without a main pass state; {conflicts.Count} name conflict(s) (first kept)");
        foreach (var c in conflicts.Take(20)) Console.WriteLine("  conflict " + c);
        return 0;
    }

    private static int SelfTest()
    {
        var err = typeof(TextureDecoder).Assembly.GetType("Shepherd.Assets.Bc7")!
            .GetMethod("ValidateTables", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, null) as string;
        Console.WriteLine(err ?? "BC7 tables OK");
        return err == null ? 0 : 1;
    }
}
