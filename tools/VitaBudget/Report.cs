using System.Text;
using System.Text.Json;
using Shepherd.Assets;

namespace VitaBudget;

/// <summary>Builds the Vita budget report from out/survey.json, out/loose.json, out/reach-*.json (+ optional lsof-derived opened-bundle list).</summary>
public static partial class Report
{
    static readonly StringBuilder Sb = new();
    static void W(string s = "") { Sb.Append(s).Append('\n'); }
    static string MB(long b) => (b / 1048576.0).ToString("F1");
    static string Fmt(int f) => Enum.IsDefined(typeof(TextureFormat), f) ? ((TextureFormat)f).ToString() : $"fmt{f}";

    // ---- texture size models ---------------------------------------------------------------------------
    static long Blocks(int w, int h) => (long)((w + 3) / 4) * ((h + 3) / 4);
    /// <summary>GPU-resident bytes of mip 0 in the format the PC build ships (crunched -> its DXT target; BC7 = 1 B/px).</summary>
    public static long GpuMip0(TexRec t)
    {
        var f = (TextureFormat)t.Fmt; int w = t.W, h = t.H;
        long px = (long)w * h;
        long one = f switch
        {
            TextureFormat.DXT1 or TextureFormat.DXT1Crunched or TextureFormat.BC4 or TextureFormat.ETC_RGB4 or TextureFormat.ETC_RGB4Crunched => Blocks(w, h) * 8,
            TextureFormat.DXT3 or TextureFormat.DXT5 or TextureFormat.DXT5Crunched or TextureFormat.BC5 or TextureFormat.BC7 or TextureFormat.BC6H or TextureFormat.ETC2_RGBA8Crunched => Blocks(w, h) * 16,
            TextureFormat.Alpha8 or TextureFormat.R8 => px,
            TextureFormat.RGB565 or TextureFormat.ARGB4444 or TextureFormat.RGBA4444 or TextureFormat.R16 or TextureFormat.RHalf => px * 2,
            TextureFormat.RGB24 => px * 3,
            TextureFormat.RGBAHalf => px * 8,
            TextureFormat.RGBAFloat => px * 16,
            _ => px * 4,
        };
        return one * Math.Max(1, t.Images);
    }
    static double MipFactor(TexRec t) => t.Mips > 1 ? 4.0 / 3.0 : 1.0;
    /// <summary>Vita-native target at a given downscale divisor (1 = full res, 2 = half, 4 = quarter), capped at 4096 (GXM_TEX_MAX_SIZE):
    /// DXT1-family stays 0.5 B/px; DXT5/BC7/RGBA32/ARGB32 -> DXT5 (UBC3) 1 B/px; RGB24 -> DXT1 0.5 B/px; Alpha8 stays 1 B/px (U8);
    /// RGB565/4444 stay 2 B/px. Mips kept if present.</summary>
    public static long VitaBytes(TexRec t, int div)
    {
        int w = Math.Max(1, t.W / div), h = Math.Max(1, t.H / div);
        while (w > 4096 || h > 4096) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        var f = (TextureFormat)t.Fmt;
        long px = (long)w * h;
        long one = f switch
        {
            TextureFormat.DXT1 or TextureFormat.DXT1Crunched or TextureFormat.RGB24 or TextureFormat.BC4 => Blocks(w, h) * 8,
            TextureFormat.Alpha8 or TextureFormat.R8 => px,
            TextureFormat.RGB565 or TextureFormat.ARGB4444 or TextureFormat.RGBA4444 => px * 2,
            _ => Blocks(w, h) * 16,
        };
        return (long)(one * Math.Max(1, t.Images) * MipFactor(t));
    }

    // ---- catalog helpers ---------------------------------------------------------------------------------
    sealed class Cat
    {
        public Dictionary<string, List<string>> AssetsByBundle = new(StringComparer.Ordinal);
        public List<(string scene, string key, List<string> closure)> Scenes = new();
    }

    static Cat LoadCatalog(AssetDatabase db)
    {
        var c = new Cat();
        var cat = db.Catalog;
        var sceneSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in cat.StringKeys.ToList())
            foreach (var l in cat.Locate(k))
            {
                if (l.IsBundle || l.InternalId == null) continue;
                if (l.Dependencies.Count > 0 && l.Dependencies[0].IsBundle && assetSeen.Add(l.InternalId))
                {
                    var bn = Path.GetFileName(l.Dependencies[0].ResolvedPath);
                    if (!c.AssetsByBundle.TryGetValue(bn, out var lst)) c.AssetsByBundle[bn] = lst = new List<string>();
                    lst.Add(l.InternalId);
                }
                if (l.InternalId.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && sceneSeen.Add(l.InternalId))
                    c.Scenes.Add((l.InternalId, k, cat.BundleClosure(k).Select(Path.GetFileName).ToList()));
            }
        c.Scenes.Sort((a, b) => string.CompareOrdinal(a.scene, b.scene));
        return c;
    }

    static string Label(Cat c, string bundle)
    {
        if (!c.AssetsByBundle.TryGetValue(bundle, out var l) || l.Count == 0) return "(no addressable entries)";
        if (l.Count == 1) return "1 asset: " + l[0];
        // dominant folders (3 levels) by entry count
        var top = l.GroupBy(x => string.Join('/', x.Split('/').Take(Math.Min(4, x.Split('/').Length - 1))))
            .OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key}/ ×{g.Count()}");
        return $"{l.Count} assets: {string.Join("; ", top)}";
    }

    // ---- MonoBehaviour categorization via the survey's MonoScript table ------------------------------------
    static Dictionary<string, ScriptRec> Scripts;
    static string MbCategory(string key)
    {
        if (Scripts != null && Scripts.TryGetValue(key, out var s))
        {
            string full = string.IsNullOrEmpty(s.Ns) ? s.Cls : s.Ns + "." + s.Cls;
            if (full.StartsWith("Spine", StringComparison.Ordinal)) return "animation";
            if (full is "TMPro.TMP_FontAsset" or "TMPro.TMP_SpriteAsset") return "fonts";
            return "other";
        }
        return "other";
    }
    static string MbName(string key) => Scripts != null && Scripts.TryGetValue(key, out var s) ? (string.IsNullOrEmpty(s.Ns) ? s.Cls : s.Ns + "." + s.Cls) : "?" + key;

    static Dictionary<string, long> Categories(BundleRec r)
    {
        var d = new Dictionary<string, long>(r.Cat);
        foreach (var kv in r.Mb) { var c = MbCategory(kv.Key); d[c] = d.TryGetValue(c, out var v) ? v + kv.Value[1] : kv.Value[1]; }
        return d;
    }
    static void Merge(Dictionary<string, long> into, Dictionary<string, long> d) { foreach (var kv in d) into[kv.Key] = into.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value; }
    static readonly string[] CatOrder = { "textures", "audio", "meshes", "animation", "scenes", "shaders", "fonts", "video", "other", "streamed-unattributed" };
    static string CatLine(Dictionary<string, long> d) => string.Join("  ", CatOrder.Where(d.ContainsKey).Select(k => $"{k}={MB(d[k])}"));

    // ----------------------------------------------------------------------------------------------------------
    public static int Run(string data, string outDir, string openedTsv)
    {
        var survey = JsonSerializer.Deserialize<Survey>(File.ReadAllText(Path.Combine(outDir, "survey.json")), Program.Json);
        var loose = File.Exists(Path.Combine(outDir, "loose.json"))
            ? JsonSerializer.Deserialize<List<BundleRec>>(File.ReadAllText(Path.Combine(outDir, "loose.json")), Program.Json) : new List<BundleRec>();
        var reaches = Directory.GetFiles(outDir, "reach-*.json").Select(p => JsonSerializer.Deserialize<ReachResult>(File.ReadAllText(p), Program.Json))
            .ToDictionary(r => r.Label);
        Scripts = survey.Bundles.Concat(loose).SelectMany(b => b.Scripts).GroupBy(s => $"{s.Cab}:{s.Pid}").ToDictionary(g => g.Key, g => g.First());
        var byName = survey.Bundles.ToDictionary(b => b.Name);
        using var db = new AssetDatabase(data);
        var cat = LoadCatalog(db);
        var shaderNames = survey.Bundles.Concat(loose).SelectMany(b => b.Shaders).GroupBy(s => $"{s.Cab}:{s.Pid}").ToDictionary(g => g.Key, g => g.First().Name ?? "?");

        // ================================================================ 1. totals
        W("=================== 1. DATA TOTALS ===================");
        long disk = survey.Bundles.Sum(b => b.Disk), unc = survey.Bundles.Sum(b => b.Uncomp);
        W($"Addressables bundles: {survey.Bundles.Count} files, {MB(disk)} MiB on disk, {MB(unc)} MiB decompressed (ratio {unc / (double)disk:F2}x); parse errors {survey.Bundles.Count(b => b.Err != null)}, detail-read errors {survey.Bundles.Sum(b => b.DetailErrors)}");
        var comp = new Dictionary<string, int>();
        foreach (var b in survey.Bundles) foreach (var kv in b.Comp) comp[kv.Key] = comp.GetValueOrDefault(kv.Key) + kv.Value;
        W($"  storage blocks by compression (0 none,1 LZMA,2 LZ4,3 LZ4HC): {string.Join(", ", comp.OrderBy(k => k.Key).Select(kv => $"{kv.Key}:{kv.Value}"))}");
        W($"  serialized-file bytes {MB(survey.Bundles.Sum(b => b.SerBytes))} MiB, .resS {MB(survey.Bundles.Sum(b => b.ResS))} MiB, .resource {MB(survey.Bundles.Sum(b => b.Resource))} MiB");
        var gcat = new Dictionary<string, long>();
        foreach (var b in survey.Bundles) Merge(gcat, Categories(b));
        W($"  by category (decompressed MiB): {CatLine(gcat)}");
        W($"  scene bundles: {survey.Bundles.Count(b => b.IsScene)}");
        var dirFiles = new (string label, string path)[]
        {
            ("Managed/ (203 DLLs)", Path.Combine(data, "Managed")),
            ("StreamingAssets/MMVideoPlayer (mp4)", Path.Combine(data, "StreamingAssets", "MMVideoPlayer")),
        };
        foreach (var (lbl, p) in dirFiles)
            if (Directory.Exists(p)) W($"  {lbl}: {MB(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length))} MiB");
        var banks = Directory.GetFiles(Path.Combine(data, "StreamingAssets"), "*.bank");
        W($"  FMOD banks: {banks.Length} files, {MB(banks.Sum(f => new FileInfo(f).Length))} MiB");
        W("Loose Data files (serialized + .resS/.resource side files):");
        foreach (var l in loose)
        {
            var lc = Categories(l);
            W($"  {l.Name,-28} disk {MB(l.Disk),8} MiB  (serialized {MB(l.SerBytes)}, .resS {MB(l.ResS)}, .resource {MB(l.Resource)})  tex={l.Tex.Count} audio={l.Audio.Count} shaders={l.Shaders.Count}  {CatLine(lc)}");
        }
        W();

        // ================================================================ 2. top bundles
        W("=================== 2. TOP 40 BUNDLES (decompressed) ===================");
        W($"{"bundle",-40} {"disk",8} {"decomp",8}  {"top category",-22} label");
        foreach (var b in survey.Bundles.OrderByDescending(b => b.Uncomp).Take(40))
        {
            var c = Categories(b); var top = c.OrderByDescending(kv => kv.Value).FirstOrDefault();
            W($"{b.Name,-40} {MB(b.Disk),8} {MB(b.Uncomp),8}  {top.Key + " " + MB(top.Value),-22} {(b.IsScene ? "[scene] " : "")}{Label(cat, b.Name)}");
        }
        W();
        // category totals by top-level asset folder (uses the addressable entries of each bundle)
        W("Decompressed bytes by asset folder (bundle attributed to the folder of its addressable entries):");
        var byFolder = new Dictionary<string, (long disk, long unc, int n)>();
        foreach (var b in survey.Bundles)
        {
            string lbl = cat.AssetsByBundle.TryGetValue(b.Name, out var l) && l.Count > 0 ? string.Join('/', l[0].Split('/').Take(3)) : "(dependency-only bundles)";
            var e = byFolder.GetValueOrDefault(lbl); byFolder[lbl] = (e.disk + b.Disk, e.unc + b.Uncomp, e.n + 1);
        }
        foreach (var kv in byFolder.OrderByDescending(k => k.Value.unc).Take(25)) W($"  {MB(kv.Value.unc),8} MiB decomp {MB(kv.Value.disk),8} MiB disk {kv.Value.n,5} bundles  {kv.Key}");
        W();

        // ================================================================ 3. scenes
        W("=================== 3. SCENES: catalog bundle closures ===================");
        W($"{"scene",-48} {"bundles",7} {"disk MiB",9} {"decomp",9} {"tex decomp",10} {"own scene bundle",-14}");
        foreach (var (scene, key, closure) in cat.Scenes)
        {
            var recs = closure.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
            var cc = new Dictionary<string, long>(); foreach (var r in recs) Merge(cc, Categories(r));
            var own = recs.FirstOrDefault(r => r.IsScene && cat.AssetsByBundle.TryGetValue(r.Name, out var l) && l.Contains(scene, StringComparer.OrdinalIgnoreCase));
            W($"{scene.Replace("Assets/Scenes/", ""),-48} {recs.Count,7} {MB(recs.Sum(r => r.Disk)),9} {MB(recs.Sum(r => r.Uncomp)),9} {MB(cc.GetValueOrDefault("textures")),10} {(own != null ? $"{MB(own.Uncomp)} MiB" : "?"),-14}");
        }
        // shared core: bundles in every scene closure
        var all = cat.Scenes.Select(s => s.closure.ToHashSet()).ToList();
        var core = all.Skip(1).Aggregate(new HashSet<string>(all[0]), (acc, s) => { acc.IntersectWith(s); return acc; });
        W($"bundles common to ALL {cat.Scenes.Count} scene closures: {core.Count}, disk {MB(core.Sum(n => byName[n].Disk))} MiB, decompressed {MB(core.Sum(n => byName[n].Uncomp))} MiB");
        var coreCat = new Dictionary<string, long>(); foreach (var n in core) Merge(coreCat, Categories(byName[n]));
        W($"  common core by category: {CatLine(coreCat)}");
        W();

        // ================================================================ 4. boot + main menu
        W("=================== 4. BOOT + MAIN MENU ===================");
        var mm = cat.Scenes.FirstOrDefault(s => s.scene.EndsWith("/Main Menu.unity"));
        var buf = cat.Scenes.FirstOrDefault(s => s.scene.EndsWith("/BufferScene.unity"));
        var mmUnion = mm.closure.Union(buf.closure ?? new List<string>()).ToHashSet();
        W($"static closure BufferScene ∪ Main Menu: {mmUnion.Count} bundles, disk {MB(mmUnion.Sum(n => byName[n].Disk))} MiB, decompressed {MB(mmUnion.Sum(n => byName[n].Uncomp))} MiB");
        var mmc = new Dictionary<string, long>(); foreach (var n in mmUnion) Merge(mmc, Categories(byName[n]));
        W($"  by category: {CatLine(mmc)}");
        if (openedTsv != null && File.Exists(openedTsv))
        {
            var opened = File.ReadAllLines(openedTsv).Select(l => l.Split('\t')).Where(p => p.Length == 2 && p[1].EndsWith(".bundle") && p[1].Contains("/StreamingAssets/aa/"))
                .Select(p => (frame: int.Parse(p[0]), name: Path.GetFileName(p[1]))).ToList();
            void Set(string what, IEnumerable<string> names)
            {
                var ns = names.Where(byName.ContainsKey).ToHashSet();
                var cc = new Dictionary<string, long>(); foreach (var n in ns) Merge(cc, Categories(byName[n]));
                W($"  {what}: {ns.Count} bundles, disk {MB(ns.Sum(n => byName[n].Disk))} MiB, decompressed {MB(ns.Sum(n => byName[n].Uncomp))} MiB; in static MM closure: {ns.Count(mmUnion.Contains)}");
                W($"      {CatLine(cc)}");
            }
            W("dynamic (engine run, lsof of open bundle handles, sampled 1/s; frame = first sample it was seen):");
            Set("first seen before frame 966 (boot: splash/title, before BufferScene)", opened.Where(o => o.frame < 966).Select(o => o.name));
            Set("opened by frame 1300 (Main Menu interactive)", opened.Select(o => o.name));
            Set("opened at frames 966..1300 only (Main Menu load)", opened.Where(o => o.frame >= 966).Select(o => o.name));
            var notOpened = mmUnion.Where(n => !opened.Any(o => o.name == n)).ToList();
            W($"  static-closure bundles never opened by frame 1300: {notOpened.Count} (decompressed {MB(notOpened.Sum(n => byName[n].Uncomp))} MiB)");
            var dynOnly = opened.Select(o => o.name).Distinct().Where(n => !mmUnion.Contains(n)).ToList();
            W($"  opened bundles NOT in the static closure (runtime Addressables loads): {dynOnly.Count} (decompressed {MB(dynOnly.Sum(n => byName.TryGetValue(n, out var r) ? r.Uncomp : 0))} MiB)");
            foreach (var n in dynOnly.OrderByDescending(n => byName.TryGetValue(n, out var r) ? r.Uncomp : 0).Take(12))
                W($"      {n} {MB(byName[n].Uncomp)} MiB  {Label(cat, n)}");
        }
        W();

        // ================================================================ 5. reach sets
        W("=================== 5. PPtr-REACHABLE CONTENT (what a Unity scene load actually pulls in) ===================");
        W("textures: shipped = bytes as stored (crunch is tiny); GPU = mip0 in its GPU format (crunch->DXT, BC7=1B/px); RGBA8 = engine's current NTexture.Rgba cost;");
        W("          Vita@1 / @1/2 / @1/4 = DXT1/DXT5/U8 targets (BC7, RGBA32 -> DXT5; RGB24 -> DXT1), 4096 cap, mips kept if present");
        foreach (var r in reaches.Values.OrderBy(r => r.Label))
        {
            var R = r.Rec; var cc = Categories(R);
            W($"[{r.Label}] {r.Key}{(r.Seeds?.Count > 0 ? $" + {r.SeededAssets} seeded addressables ({string.Join(" | ", r.Seeds.Select(s => s.Replace("Assets/", "")))})" : "")}");
            W($"   objects {r.Visited} in {r.PerFile.Count} files (closure {r.Closure.Count} bundles); serialized object bytes {MB(R.Cls.Sum(kv => kv.Value[1]))} MiB; unresolved PPtrs {r.Unresolved}, read errors {r.ReadErrors}");
            W($"   by category: {CatLine(cc)}");
            var T = R.Tex;
            W($"   textures n={T.Count}: shipped {MB(T.Sum(t => t.Stored))}  GPU {MB(T.Sum(GpuMip0))}  RGBA8 {MB(T.Sum(t => t.Rgba8))}  | Vita@1 {MB(T.Sum(t => VitaBytes(t, 1)))}  @1/2 {MB(T.Sum(t => VitaBytes(t, 2)))}  @1/4 {MB(T.Sum(t => VitaBytes(t, 4)))} MiB");
            foreach (var g in T.GroupBy(t => Fmt(t.Fmt)).OrderByDescending(g => g.Sum(GpuMip0)))
                W($"      {g.Key,-14} n={g.Count(),4} shipped {MB(g.Sum(t => t.Stored)),7}  GPU {MB(g.Sum(GpuMip0)),7}  RGBA8 {MB(g.Sum(t => t.Rgba8)),8}  Vita@1/2 {MB(g.Sum(t => VitaBytes(t, 2))),6}");
            W($"   >2048 px textures: {T.Count(t => t.W > 2048 || t.H > 2048)}; >4096 (exceed GXM max): {T.Count(t => t.W > 4096 || t.H > 4096)}");
            W($"   meshes n={R.Mesh.Count} verts={R.Mesh.Sum(m => (long)m.Verts)}; audio clips n={R.Audio.Count}; materials n={R.Mats.Count}; shaders n={R.Shaders.Count} ({R.Shaders.Select(s => s.Name).Distinct().Count()} distinct)");
            var topMb = R.Mb.OrderByDescending(kv => kv.Value[1]).Take(8).Select(kv => $"{MbName(kv.Key)} {kv.Value[0]}x {MB(kv.Value[1])}");
            W($"   top MonoBehaviour data (non-scene): {string.Join("; ", topMb)}");
            var texRef = (r.Refs ?? new()).Where(x => x.Cls == 28).GroupBy(x => $"{x.Cab}:{x.Pid}").ToDictionary(g => g.Key, g => g.First());
            W($"   top 12 textures by GPU bytes (first referrer in the BFS, depth from scene):");
            foreach (var t in T.OrderByDescending(GpuMip0).Take(12))
                W($"      {t.W,5}x{t.H,-5} {Fmt(t.Fmt),-13} mips={t.Mips,-2} GPU {MB(GpuMip0(t)),6}  shipped {MB(t.Stored),6}  '{t.Name}'  <- {(texRef.TryGetValue($"{t.Cab}:{t.Pid}", out var tr) ? $"{tr.Referrer} d{tr.Depth}" : "?")}");
            if (texRef.Count > 0)
            {
                var byRef = T.GroupBy(t => texRef.TryGetValue($"{t.Cab}:{t.Pid}", out var x) ? x.Referrer : "?").OrderByDescending(g => g.Sum(t => VitaBytes(t, 2))).Take(8);
                W($"   Vita@1/2 bytes by first referrer: {string.Join("; ", byRef.Select(g => $"{g.Key} n={g.Count()} {MB(g.Sum(t => VitaBytes(t, 2)))}"))}");
            }
            W();
        }

        // ================================================================ 6. texture formats (global)
        W("=================== 6. TEXTURE FORMATS (all bundles + loose) ===================");
        var allTex = survey.Bundles.SelectMany(b => b.Tex).Concat(loose.SelectMany(l => l.Tex)).ToList();
        W($"textures: {allTex.Count} ({allTex.Select(t => (t.Name, t.W, t.H, t.Fmt)).Distinct().Count()} distinct by name+dims+format — duplicates come from Addressables copying shared deps)");
        W($"{"format",-14} {"count",6} {"shipped MiB",12} {"GPU MiB",9} {"RGBA8 MiB",10} {"Vita@1/2",9}  readable");
        foreach (var g in allTex.GroupBy(t => Fmt(t.Fmt)).OrderByDescending(g => g.Count()))
            W($"{g.Key,-14} {g.Count(),6} {MB(g.Sum(t => t.Stored)),12} {MB(g.Sum(GpuMip0)),9} {MB(g.Sum(t => t.Rgba8)),10} {MB(g.Sum(t => VitaBytes(t, 2))),9}  {g.Count(t => t.Readable)}");
        W($"{"TOTAL",-14} {allTex.Count,6} {MB(allTex.Sum(t => t.Stored)),12} {MB(allTex.Sum(GpuMip0)),9} {MB(allTex.Sum(t => t.Rgba8)),10} {MB(allTex.Sum(t => VitaBytes(t, 2))),9}");
        W($"dimension histogram (max side): " + string.Join(", ", allTex.GroupBy(t => Math.Max(t.W, t.H) switch { > 8192 => ">8192", > 4096 => "4097-8192", > 2048 => "2049-4096", > 1024 => "1025-2048", > 512 => "513-1024", _ => "<=512" })
            .OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        W();
        W("30 largest textures (by GPU mip0 bytes; dedup by name+dims):");
        foreach (var t in allTex.GroupBy(t => (t.Name, t.W, t.H, t.Fmt)).Select(g => g.First()).OrderByDescending(GpuMip0).Take(30))
            W($"  {t.W,5}x{t.H,-5} {Fmt(t.Fmt),-13} mips={t.Mips,-2} GPU {MB(GpuMip0(t)),6}  shipped {MB(t.Stored),6}  '{t.Name}'  [{(t.B.StartsWith("reach") ? "" : t.B[..Math.Min(8, t.B.Length)])} {(byName.ContainsKey(t.B) ? Label(cat, t.B) : t.B)}]");
        W();

        // ================================================================ 7. shaders
        W("=================== 7. SHADERS ===================");
        if (reaches.TryGetValue("mainmenu", out var mmr))
        {
            W("Main Menu: shaders reached from the scene, with the materials that use them and who references those materials");
            var R = mmr.Rec;
            var shByKey = R.Shaders.GroupBy(s => $"{s.Cab}:{s.Pid}").ToDictionary(g => g.Key, g => g.First());
            var matRef = (mmr.Refs ?? new()).Where(x => x.Cls == 21).ToDictionary(x => $"{x.Cab}:{x.Pid}", x => x);
            var shRef = (mmr.Refs ?? new()).Where(x => x.Cls == 48).GroupBy(x => $"{x.Cab}:{x.Pid}").ToDictionary(g => g.Key, g => g.First());
            var rows = R.Shaders.GroupBy(s => s.Name ?? "?").Select(g =>
            {
                var keys = g.Select(s => $"{s.Cab}:{s.Pid}").ToHashSet();
                var mats = R.Mats.Where(m => keys.Contains($"{m.ShCab}:{m.ShPid}")).ToList();
                var referrers = mats.Select(m => matRef.TryGetValue($"{m.Cab}:{m.Pid}", out var x) ? x.Referrer : "?")
                    .GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => $"{x.Key}×{x.Count()}").Take(3);
                var direct = g.Select(s => shRef.TryGetValue($"{s.Cab}:{s.Pid}", out var x) ? x.Referrer : null).Where(x => x != null).Distinct().Take(2);
                var first = g.First();
                return (name: g.Key, mats: mats.Count, minDepth: mats.Select(m => matRef.TryGetValue($"{m.Cab}:{m.Pid}", out var x) ? x.Depth : 99).DefaultIfEmpty(99).Min(),
                        refs: string.Join(", ", referrers), direct: string.Join(", ", direct), first);
            }).OrderBy(x => Classify(x.name)).ThenByDescending(x => x.mats).ToList();
            W($"{"class",-9} {"shader",-52} {"mats",4} {"passes",6} {"props",5} {"kw",3}  material referrers (first BFS parent) / shader referrer");
            foreach (var x in rows)
                W($"{Classify(x.name),-9} {x.name,-52} {x.mats,4} {x.first.Passes,6} {x.first.Props,5} {x.first.Kw,3}  {(x.mats > 0 ? x.refs : "(no material) <- " + x.direct)}");
            W($"Main Menu summary: {rows.Count} distinct shaders reached; with >=1 material: {rows.Count(r => r.mats > 0)}; by class: " +
              string.Join(", ", rows.Where(r => r.mats > 0).GroupBy(r => Classify(r.name)).Select(g => $"{g.Key}={g.Count()}")));
            W();
        }
        var allMats = survey.Bundles.SelectMany(b => b.Mats).Concat(loose.SelectMany(l => l.Mats)).ToList();
        var matsPerShader = allMats.GroupBy(m => m.ShCab == null ? "(null)" : shaderNames.GetValueOrDefault($"{m.ShCab}:{m.ShPid}", "?unresolved"))
            .ToDictionary(g => g.Key, g => g.Select(m => (m.Name, m.Cab)).Distinct().Count());
        var distinctShaders = survey.Bundles.SelectMany(b => b.Shaders).Concat(loose.SelectMany(l => l.Shaders)).GroupBy(s => s.Name ?? "?").ToList();
        W($"whole game: {distinctShaders.Count} distinct shader names ({survey.Bundles.Sum(b => b.Shaders.Count)} Shader objects in bundles — duplicated per bundle), {allMats.Count} material objects");
        W("by class: " + string.Join(", ", distinctShaders.GroupBy(g => Classify(g.Key)).Select(g => $"{g.Key}={g.Count()}")));
        W("top 40 shaders by distinct materials:");
        foreach (var kv in matsPerShader.OrderByDescending(k => k.Value).Take(40)) W($"  {Classify(kv.Key),-9} {kv.Value,5}  {kv.Key}");
        W();

        // ================================================================ 8. FMOD banks
        W("=================== 8. FMOD BANKS (StreamingAssets/*.bank) ===================");
        var bk = banks.Select(f => (name: Path.GetFileName(f), size: new FileInfo(f).Length)).ToList();
        string Group(string n) => n.StartsWith("Master") ? "Master (always)" : n.StartsWith("DLC") ? "DLC (Woolhaven/Unholy Alliance etc.)"
            : n.StartsWith("Dungeon") ? "Dungeon1-4 music" : n.StartsWith("Dialogue") ? "Dialogue" : n.StartsWith("Basemusic") ? "Base music"
            : n.StartsWith("Music") ? "Music (menu/general)" : n.StartsWith("Comic") ? "Comic" : n.StartsWith("Atmos") ? "Atmos" : "other";
        foreach (var g in bk.GroupBy(b => Group(b.name)).OrderByDescending(g => g.Sum(b => b.size)))
            W($"  {g.Key,-38} {g.Count(),3} files {MB(g.Sum(b => b.size)),8} MiB   (.streams {MB(g.Where(b => b.name.Contains(".streams.")).Sum(b => b.size))}, .assets {MB(g.Where(b => b.name.Contains(".assets.")).Sum(b => b.size))}, metadata {MB(g.Where(b => !b.name.Contains(".streams.") && !b.name.Contains(".assets.")).Sum(b => b.size))})");
        W($"  TOTAL {bk.Count} files {MB(bk.Sum(b => b.size))} MiB");
        foreach (var b in bk.Where(b => b.name.StartsWith("Master") || b.name.StartsWith("Music.")).OrderBy(b => b.name)) W($"    {b.name,-34} {MB(b.size),8} MiB");
        W();

        // ================================================================ 9. diet
        W("=================== 9. DIET TABLE (Vita: 256 MiB MAIN by default / 365 MiB with ATTRIBUTE2=12, 112 MiB CDRAM, GXM max texture 4096) ===================");
        void Diet(string label, params ReachResult[] rs)
        {
            if (rs.Length == 0 || rs.Any(x => x == null)) return;
            var r = rs[0];
            var T = rs.SelectMany(x => x.Rec.Tex).GroupBy(t => $"{t.Cab}:{t.Pid}").Select(g => g.First()).ToList();
            long obj = rs.Sum(x => x.Rec.Cls.Sum(kv => kv.Value[1]));
            long over4k = T.Where(t => t.W > 4096 || t.H > 4096).Sum(GpuMip0);
            long bc7 = T.Where(t => t.Fmt == (int)TextureFormat.BC7).Sum(GpuMip0);
            long crunch = T.Where(t => t.Fmt is (int)TextureFormat.DXT5Crunched or (int)TextureFormat.DXT1Crunched).Sum(GpuMip0);
            long raw = T.Where(t => t.Fmt is (int)TextureFormat.RGBA32 or (int)TextureFormat.RGB24 or (int)TextureFormat.ARGB32).Sum(GpuMip0);
            W($"[{label}]  (from {string.Join(" + ", rs.Select(x => "reach-" + x.Label))}; {T.Count} distinct textures)");
            W($"   textures if all reachable are resident: RGBA8(engine today) {MB(T.Sum(t => t.Rgba8))} -> GPU-native {MB(T.Sum(GpuMip0))} -> Vita full {MB(T.Sum(t => VitaBytes(t, 1)))} -> 1/2 {MB(T.Sum(t => VitaBytes(t, 2)))} -> 1/4 {MB(T.Sum(t => VitaBytes(t, 4)))} MiB");
            W($"      of GPU-native: crunched {MB(crunch)} (must be transcoded crunch->DXT at load or offline), BC7 {MB(bc7)} (no BC7 on SGX: re-encode DXT5), RGBA32/RGB24 {MB(raw)} (re-encode DXT5/DXT1), >4096px {MB(over4k)} (must downscale)");
            var clos = rs.SelectMany(x => x.Closure).Distinct().ToList();
            W($"   serialized objects reached: {rs.Sum(x => x.Visited)} objects, {MB(obj)} MiB in {rs.SelectMany(x => x.PerFile.Keys).Distinct().Count()} files (vs catalog bundle closure {clos.Count} bundles = {MB(clos.Where(byName.ContainsKey).Sum(n => byName[n].Uncomp))} MiB decompressed)");
        }
        Diet("Boot: level0 + sharedassets0 (DontDestroyOnLoad managers persist into the menu)", reaches.GetValueOrDefault("level0"));
        Diet("Main-Menu milestone: Main Menu scene alone", reaches.GetValueOrDefault("mainmenu"));
        Diet("Main-Menu milestone: level0 ∪ Main Menu (resident together)", reaches.GetValueOrDefault("level0"), reaches.GetValueOrDefault("mainmenu"));
        Diet("Gameplay: Game Biome Intro (first playable)", reaches.GetValueOrDefault("intro"));
        Diet("Gameplay: Dungeon1 scene only", reaches.GetValueOrDefault("dungeon1"));
        Diet("Gameplay: Dungeon1 + all Darkwood rooms/enemies (upper bound)", reaches.GetValueOrDefault("dungeon1full"));
        Diet("Gameplay: Base Biome 1 (cult base)", reaches.GetValueOrDefault("base1"));
        W();

        var path = Path.Combine(outDir, "report.txt");
        File.WriteAllText(path, Sb.ToString());
        Console.Write(Sb.ToString());
        Console.Error.WriteLine($"wrote {path}");
        return 0;
    }

    /// <summary>simple = maps onto the engine's existing texture×colour×blend path (sprites/UI/unlit/particles);
    /// sdf = TMP distance field (alpha-test/smoothstep, needs a small dedicated fragment shader);
    /// spine = Spine skeleton family (premultiplied, optional tint-black);
    /// post = full-screen post / hidden helpers (drop or replace on Vita);
    /// custom = hand-authored game/Amplify/Shadero/BlendModes shaders that need a hand-written GLSL (or a documented approximation).</summary>
    public static string Classify(string n)
    {
        n ??= "?";
        if (n.StartsWith("TextMeshPro/") || n == "GUI/Text Shader") return "sdf";
        if (n.StartsWith("Spine/") && !n.Contains("Sprite/")) return "spine";
        if (n.StartsWith("Hidden/PostProcessing") || n.StartsWith("Hidden/Post FX") || n.StartsWith("Hidden/NoiseAndGrain") || n.StartsWith("Beffio/")
            || n is "ShadowCasterPass" or "Hidden/ScreenSpaceOverlayDummy" or "Hidden/ForwardBaseDummy" || n.StartsWith("Hidden/Internal") || n.StartsWith("Hidden/BlitCopy")) return "post";
        if (n is "Sprites/Default" or "UI/Default" or "UI/Default Font" or "Unlit/Texture" or "Unlit/Transparent" or "Unlit/Color" or "Sprites/Mask" or "UI/Unlit/Transparent"
            || n.StartsWith("Mobile/Particles/") || n.StartsWith("Legacy Shaders/Particles/") || n == "Particles/Standard Unlit" || n == "Spine/Sprite/Unlit"
            || n == "Custom/Sprites/Colorized" || n == "COTL/UI/SolidColour" || n == "UI/TranslucentImage") return "simple";
        return "custom";
    }
}
