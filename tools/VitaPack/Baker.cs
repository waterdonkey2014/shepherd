using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using Shepherd.Assets;
using K = Shepherd.Assets.VitaPackFormat.K;

namespace Shepherd.VitaPack;

sealed class Entry
{
    public SerializedFile File; public ObjectInfo Obj; public Recording.Obj Rec; public int Order; public bool Closure;
    public TypeTreeObject Tree; public byte[] Raw; public PPtr? Script; public string TreeError;
    public TexJob Tex;
    public byte[] TreeBytes; public long Offset; public int Size; public ushort Flags;
    public string Category;
    public override string ToString() => $"{File.Name} #{Obj.PathId} {ClassIds.Name(Obj.ClassId)}";
}

sealed partial class Baker
{
    readonly Recording _rec;
    AssetDatabase _db;
    readonly List<Entry> _entries = new();
    readonly Dictionary<(SerializedFile, long), Entry> _byObj = new();
    readonly HashSet<string> _absentFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<(string, long)> _absentObjects = new();
    readonly List<SerializedFile> _fileOrder = new(); readonly HashSet<SerializedFile> _fileSet = new();
    readonly object _dbLock = new();
    static void Say(string s) => Program.Say(s);

    public Baker(Recording rec) { _rec = rec; }

    public void Run()
    {
        var sw = Stopwatch.StartNew();
        _db = new AssetDatabase(Program.DataDir) { LazyFiles = true };
        SerializedFile.ShareTypeTrees = true;
        // bundles in the order the boot loaded them, so every file of a loaded bundle is known by name
        foreach (var b in _rec.BundleOrder) _db.LoadBundleFiles(Path.Combine(_db.BundleDir, b));
        foreach (var a in _rec.AbsentFiles) _absentFiles.Add(a);
        foreach (var a in _rec.AbsentObjects) _absentObjects.Add(a);

        // 1) the recorded set, in first-touch order; tree reads replay the boot's order (stripped files register their
        //    fallback type trees on first use, exactly as in the engine)
        int missing = 0;
        var recSet = new List<(SerializedFile, ObjectInfo, Recording.Obj)>();
        foreach (var ro in _rec.Objects.Values.OrderBy(o => o.Seq))
        {
            var f = _db.GetFile(ro.File); var o = f?.GetObject(ro.PathId);
            if (o == null) { missing++; if (missing <= 10) Say($"  WARNING recorded object not found in the game data: {ro.File} #{ro.PathId}"); continue; }
            recSet.Add((f, o, ro));
        }
        PrepareTrims(recSet.Select(r => (r.Item1, r.Item2)));   // stage 1 (Trims.cs): language, joystick maps
        foreach (var (f, o, ro) in recSet)
        {
            if (Excluded(f, o)) continue;
            var e = Add(f, o, closure: false); e.Rec = ro;
        }
        foreach (var e in _entries.Where(e => e.Rec?.Tree == true).ToList()) Build(e);
        Say($"recorded set: {_entries.Count} objects in {_fileOrder.Count} files ({missing} not found) — trees read in {sw.Elapsed.TotalSeconds:F1} s");

        // 2) payloads for the rest, and the PPtr closure (breadth first)
        int recorded = _entries.Count;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Tree == null && e.Raw == null && e.TreeError == null) Build(e);
            if (Program.Closure == "none") continue;
            // an AssetBundle header lists every asset of its bundle (m_Container): following it would bake whole bundles.
            // Container lookups the boot made are in the recording; a later one that the pack lacks is logged by the engine.
            if (e.Obj.ClassId == 142) continue;
            var ptrs = new List<PPtr>();
            if (e.Tree != null) TypeTreeReader.CollectPPtrs(e.Tree, ptrs);
            if (e.Script is { IsNull: false } sp) ptrs.Add(sp);
            foreach (var p in ptrs) Follow(e.File, p);
        }
        Say($"closure ({Program.Closure}): +{_entries.Count - recorded} objects -> {_entries.Count} objects in {_fileOrder.Count} files; absent targets: {_absentObjects.Count} objects, {_absentFiles.Count} files ({sw.Elapsed.TotalSeconds:F1} s)");
        CheckStreams();

        // 3) textures
        Textures();
        Say($"textures done ({sw.Elapsed.TotalSeconds:F1} s)");
        ReportTrims();

        // 4) write
        var w = new PackWriter(this);
        w.Write(Path.Combine(Program.OutDir, VitaPackFormat.PackFileName));
        Report(w);
    }

    Entry Add(SerializedFile f, ObjectInfo o, bool closure)
    {
        if (_byObj.TryGetValue((f, o.PathId), out var e)) return e;
        e = new Entry { File = f, Obj = o, Order = _entries.Count, Closure = closure };
        _entries.Add(e); _byObj[(f, o.PathId)] = e;
        if (_fileSet.Add(f)) _fileOrder.Add(f);
        return e;
    }

    void Follow(SerializedFile from, PPtr p)
    {
        if (p.IsNull || p.PathId == 0) return;
        var (rf, ro) = _db.Resolve(from, p);
        if (ro != null) { if (!Excluded(rf, ro)) Add(rf, ro, closure: true); return; }
        var ef = _db.GetExternalFile(from, p.FileId);
        if (ef == null)
        {
            if (p.FileId > 0 && p.FileId <= from.Externals.Count) { var n = from.Externals[p.FileId - 1].PathName; _absentFiles.Add(n[(n.LastIndexOf('/') + 1)..]); }
        }
        else { _absentObjects.Add((ef.Name, p.PathId)); if (_fileSet.Add(ef)) _fileOrder.Add(ef); }
    }

    // ------------------------------------------------------------------ payloads

    static bool RawClass(int c) => c is 49 or 78 or 147;   // TextAsset, TagManager, ResourceManager: the engine parses their bytes itself

    void Build(Entry e)
    {
        var f = e.File; var o = e.Obj; int cls = o.ClassId;
        bool raw = RawClass(cls) || e.Rec?.Raw == true;
        if (!RawClass(cls))
        {
            bool has; try { has = f.HasTree(o); } catch (Exception ex) { has = false; e.TreeError = ex.Message; }
            if (has)
                try { e.Tree = TypeTreeReader.Read(f, o); }
                catch (Exception ex) { e.TreeError = (ex.InnerException ?? ex).Message; }
            else e.TreeError ??= "no type tree";
            // stripped MonoBehaviour with only the header layout: the engine reads its script fields from the raw bytes
            if (cls == ClassIds.MonoBehaviour && !f.EnableTypeTree && !SerializedFile.HasFallbackTypeTree(ClassIds.MonoBehaviour, o.Type?.OldTypeHashHex)) raw = true;
            if (e.Tree == null && cls == ClassIds.MonoBehaviour) raw = true;
        }
        if (cls == ClassIds.MonoBehaviour) e.Script = AssetDatabase.ReadMonoBehaviourScriptPPtr(f, o);
        if (raw) e.Raw = f.GetObjectBytes(o).ToArray();
        if (e.Tree != null)
            switch (cls)
            {
                case 48: e.Tree = ReduceShader(e.Tree); break;
                case 142: e.Tree = ReduceAssetBundle(e.Tree); break;
                case 43: InlineMesh(e); break;
                case 28: e.Tex = new TexJob(this, e); StripTexture(e.Tree); break;
            }
        TrimEntry(e);   // stage 1 (Trims.cs): I2 single language, ControllerDataFiles without joystick maps
        e.Category = Category(e);
        if (e.Tree == null && e.Raw == null && e.TreeError != null && e.Rec?.Tree == true) Say($"  WARNING {e}: the boot read a tree but the baker cannot ({e.TreeError})");
    }

    /// <summary>The engine reads a Shader's name (m_ParsedForm.m_Name, else m_Name) and, since polish round 2, its first subshader's render
    /// state (engine Render/ShaderPolicy.Parse): the Queue tag; per pass the type, tags and rtBlend0's src/dst factors and colour mask;
    /// the declared properties' names and first default values. Keep exactly that (no programs, no keywords, no other subshaders).</summary>
    static TypeTreeObject ReduceShader(TypeTreeObject t)
    {
        var r = new TypeTreeObject(t.TypeName, 2);
        if (t["m_ParsedForm"] is TypeTreeObject pf)
        {
            var p = new TypeTreeObject(pf.TypeName, 3); p.Add("m_Name", pf["m_Name"] ?? "");
            if (pf["m_PropInfo"] is TypeTreeObject pi && pi["m_Props"] is List<object> props)
            {
                var kept = new List<object>(props.Count);
                foreach (var o in props) if (o is TypeTreeObject pr) kept.Add(Keep(pr, "m_Name", "m_DefValue[0]"));
                var npi = new TypeTreeObject(pi.TypeName, 1); npi.Add("m_Props", kept); p.Add("m_PropInfo", npi);
            }
            if (pf["m_SubShaders"] is List<object> subs && subs.Count > 0 && subs[0] is TypeTreeObject ss)
            {
                var nss = new TypeTreeObject(ss.TypeName, 2);
                if (ss["m_Tags"] is TypeTreeObject tg) nss.Add("m_Tags", tg);
                if (ss["m_Passes"] is List<object> passes)
                {
                    var kept = new List<object>(passes.Count);
                    foreach (var o in passes)
                    {
                        if (o is not TypeTreeObject pass) continue;
                        var np = Keep(pass, "m_Type", "m_Tags");
                        if (pass["m_State"] is TypeTreeObject st)
                        {
                            var nst = Keep(st, "m_Tags");
                            if (st["rtBlend0"] is TypeTreeObject b) nst.Add("rtBlend0", Keep(b, "srcBlend", "destBlend", "colMask"));
                            np.Add("m_State", nst);
                        }
                        kept.Add(np);
                    }
                    nss.Add("m_Passes", kept);
                }
                p.Add("m_SubShaders", new List<object> { nss });
            }
            r.Add("m_ParsedForm", p);
        }
        if (t.Has("m_Name")) r.Add("m_Name", t["m_Name"]);
        return r;
    }
    static TypeTreeObject Keep(TypeTreeObject src, params string[] keys)
    {
        var r = new TypeTreeObject(src.TypeName, keys.Length);
        foreach (var k in keys) if (src.TryGet(k, out var v)) r.Add(k, v);
        return r;
    }

    /// <summary>The engine reads an AssetBundle header's container, scene hashes, main asset and scene flag (BundleIcalls.Open,
    /// AssetDatabase.LoadScene); m_PreloadTable (every object of the bundle) and the dependency list are dropped.</summary>
    static TypeTreeObject ReduceAssetBundle(TypeTreeObject t)
    {
        var r = new TypeTreeObject(t.TypeName, 6);
        foreach (var k in new[] { "m_Name", "m_Container", "m_MainAsset", "m_SceneHashes", "m_IsStreamedSceneAssetBundle" })
            if (t.Has(k)) r.Add(k, t[k]);
        return r;
    }

    /// <summary>Mesh vertex streams (m_StreamData -> .resS) become inline m_VertexData.m_DataSize bytes (what MeshDecode reads first).</summary>
    void InlineMesh(Entry e)
    {
        var t = e.Tree;
        if (t["m_StreamData"] is not TypeTreeObject sd || TypeTreeObject.ToLong(sd["size"]) <= 0) return;
        if (t["m_VertexData"] is not TypeTreeObject vd) return;
        byte[] data;
        lock (_dbLock) data = _db.ReadStreamedData(e.File, sd.Str("path"), TypeTreeObject.ToLong(sd["offset"]), TypeTreeObject.ToLong(sd["size"]));
        vd.Set("m_DataSize", data);
        ZeroStream(sd);
        MeshesInlined++; MeshBytesInlined += data.Length;
    }
    public int MeshesInlined; public long MeshBytesInlined;

    static void StripTexture(TypeTreeObject t)
    {
        t.Set("image data", Array.Empty<byte>());
        if (t["m_StreamData"] is TypeTreeObject sd) ZeroStream(sd);
    }
    static void ZeroStream(TypeTreeObject sd)
    {
        foreach (var k in new[] { "offset", "size" }) if (sd[k] is { } v) sd.Set(k, Zero(v));
        if (sd.Has("path")) sd.Set("path", "");
    }
    static object Zero(object v) => v switch { int => 0, uint => 0u, long => 0L, ulong => 0UL, short => (short)0, ushort => (ushort)0, _ => v };

    /// <summary>Streams the boot read must all be covered: mesh streams are inlined, texture streams become pack images.</summary>
    void CheckStreams()
    {
        int unknown = 0;
        foreach (var (file, path, off, size) in _rec.Streams)
        {
            bool covered = _entries.Any(e => e.File.Name == file && (e.Obj.ClassId is 28 or 43));
            if (!covered) { unknown++; Say($"  WARNING stream read not covered by a texture/mesh in the pack: {file} {path} @{off}+{size}"); }
        }
        Say($"stream reads in the recording: {_rec.Streams.Count} ({unknown} not attributable to a texture or mesh)");
    }

    static string Category(Entry e) => e.Obj.ClassId switch
    {
        28 => "textures (Texture2D records)", 43 => "meshes", 74 or 91 or 221 or 95 => "animation", 114 => "MonoBehaviours", 115 => "MonoScripts",
        1 or 4 or 224 => "GameObjects+Transforms", 21 => "materials", 48 => "shaders (name + render state)", 213 => "sprites", 687078895 => "sprite atlases",
        49 => "TextAssets (bytes as-is)", 128 => "fonts", 142 => "AssetBundle headers", 83 or 329 => "audio/video clips (no media)", _ => "other components/assets",
    };

    // ------------------------------------------------------------------ verification

    public void VerifyPack()
    {
        var sw = Stopwatch.StartNew();
        using var r = PackReader.Open(Program.OutDir);
        int trees = 0, raws = 0, images = 0, bad = 0;
        foreach (var e in _entries)
        {
            var pf = r.GetFile(e.File.Name); var po = pf?.GetObject(e.Obj.PathId);
            if (po == null || po.ClassId != e.Obj.ClassId) { bad++; if (bad < 10) Say($"  VERIFY FAIL {e}: not in the pack index"); continue; }
            if (e.Tree != null)
            {
                var t = r.ReadTree(pf, po); trees++;
                var img = t[PackReader.ImageKey] as PackImage;
                if (!Same(e.Tree, t, skipImageKey: true, out var why)) { bad++; if (bad < 10) Say($"  VERIFY FAIL {e}: tree differs: {why}"); }
                if (e.Tex?.Out != null)
                {
                    images++;
                    if (img == null || img.Width != e.Tex.OutW || img.Height != e.Tex.OutH || img.Format != e.Tex.OutFormat || !img.Read().AsSpan().SequenceEqual(e.Tex.Out)) { bad++; Say($"  VERIFY FAIL {e}: image differs"); }
                }
            }
            if (e.Raw != null) { raws++; if (!r.ReadRaw(pf, po).AsSpan().SequenceEqual(e.Raw)) { bad++; Say($"  VERIFY FAIL {e}: raw differs"); } }
            if (e.Script is { } sp && r.ScriptPPtr(po) != sp) { bad++; Say($"  VERIFY FAIL {e}: script pptr differs"); }
        }
        foreach (var (name, files) in _rec.Bundles.Values.Select(b => (Path.GetFileName(b.path), b.files)))
        {
            var bf = r.BundleFiles(name);
            if (bf == null || !bf.Select(x => x.Name).SequenceEqual(files, StringComparer.OrdinalIgnoreCase)) { bad++; Say($"  VERIFY FAIL bundle {name}: files differ"); }
        }
        Say($"verify: {trees} value trees identical to the source trees (types, order, values), {raws} raw records, {images} images, {_rec.Bundles.Count} bundle file lists: {(bad == 0 ? "ALL OK" : bad + " FAILURES")} ({sw.Elapsed.TotalSeconds:F1} s)");
        Say(r.Stats());
        if (bad > 0) Environment.ExitCode = 1;
    }

    static bool Same(object a, object b, bool skipImageKey, out string why)
    {
        why = null;
        if (a is TypeTreeObject ta)
        {
            if (b is not TypeTreeObject tb) { why = "struct vs " + b?.GetType().Name; return false; }
            if (ta.TypeName != tb.TypeName) { why = $"type {ta.TypeName} vs {tb.TypeName}"; return false; }
            var kb = tb.Keys.Where(k => !(skipImageKey && k == PackReader.ImageKey)).ToList();
            if (ta.Count != kb.Count) { why = $"{ta.TypeName}: {ta.Count} vs {kb.Count} keys"; return false; }
            for (int i = 0; i < ta.Count; i++)
            {
                if (ta.Keys[i] != kb[i]) { why = $"key {ta.Keys[i]} vs {kb[i]}"; return false; }
                if (!Same(ta.Values[i], tb.Values[i], false, out why)) { why = ta.Keys[i] + "." + why; return false; }
            }
            return true;
        }
        if (a is List<object> la)
        {
            if (b is not List<object> lb || la.Count != lb.Count) { why = "list length"; return false; }
            for (int i = 0; i < la.Count; i++) if (!Same(la[i], lb[i], false, out why)) { why = $"[{i}]" + why; return false; }
            return true;
        }
        if (a is byte[] ba) { if (b is byte[] bb && ba.AsSpan().SequenceEqual(bb)) return true; why = "bytes"; return false; }
        if (a == null) { if (b == null) return true; why = "null vs value"; return false; }
        if (b == null || a.GetType() != b.GetType()) { why = $"{a.GetType().Name} vs {b?.GetType().Name}"; return false; }
        if (a is float fa) { if (BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits((float)b)) return true; why = $"{fa} vs {b}"; return false; }
        if (a is double da) { if (BitConverter.DoubleToInt64Bits(da) == BitConverter.DoubleToInt64Bits((double)b)) return true; why = $"{da} vs {b}"; return false; }
        if (!a.Equals(b)) { why = $"{a} vs {b}"; return false; }
        return true;
    }

    /// <summary>--bench: the same objects decoded through Unity TypeTrees (TypeTreeReader, file buffers already resident) and
    /// from the pack (PackReader value trees), 3 rounds each, single thread, after a warm-up round.</summary>
    public void BenchDecode()
    {
        using var r = PackReader.Open(Program.OutDir);
        var items = _entries.Where(e => e.Tree != null && e.Obj.ClassId is not (48 or 142 or 28 or 43)).ToList();   // classes the baker reshapes are left out
        var packed = items.Select(e => { var pf = r.GetFile(e.File.Name); return (pf, pf.GetObject(e.Obj.PathId)); }).ToList();
        double Time(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
        void Src() { foreach (var e in items) TypeTreeReader.Read(e.File, e.Obj); }
        void Pk() { foreach (var (pf, po) in packed) r.ReadTree(pf, po); }
        Src(); Pk();
        var ts = Enumerable.Range(0, 3).Select(_ => { GC.Collect(); return Time(Src); }).Min();
        var tp = Enumerable.Range(0, 3).Select(_ => { GC.Collect(); return Time(Pk); }).Min();
        long a0 = GC.GetAllocatedBytesForCurrentThread(); Src(); long a1 = GC.GetAllocatedBytesForCurrentThread(); Pk(); long a2 = GC.GetAllocatedBytesForCurrentThread();
        Say($"bench ({items.Count} objects, best of 3, warm): TypeTreeReader {ts:F0} ms ({(a1 - a0) / 1048576.0:F0} MiB allocated) vs pack value trees {tp:F0} ms ({(a2 - a1) / 1048576.0:F0} MiB allocated)");
    }

    // ------------------------------------------------------------------ accessors for the writer / textures
    public IReadOnlyList<Entry> Entries => _entries;
    public IReadOnlyList<SerializedFile> FileOrder => _fileOrder;
    public IReadOnlyCollection<string> AbsentFiles => _absentFiles;
    public IReadOnlyCollection<(string, long)> AbsentObjects => _absentObjects;
    public AssetDatabase Db => _db;
    public object DbLock => _dbLock;
    public Recording Rec => _rec;
}
