using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Assets;
using Shepherd.Engine.Native;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #13 walkc (vita/notes/kit13-walkc.md, "Unity's UnloadUnusedAssets"): the release half of Unity's Resources.UnloadUnusedAssets
/// and of a Single scene load (Unity unloads unused assets as part of every non-additive scene load). Until kit #12 the engine's registries
/// (Objects.ById, the block index, Loader's live-record list, the Registry lists, the tag sets) kept every asset record -- and through it the
/// managed object -- for the whole session, so the menu's prefabs, sprites, materials and texts stayed after the menu was gone
/// (vita/notes/kit13-prep-findings.md section 4).
///
/// Unity's rule: an asset (a persistent object: it came from a file and can be read again) that nothing reaches -- no scene object, no
/// script field, no static -- is unloaded; what the game reaches stays. Here, inside a full collection the engine already makes (the
/// covered-frame collection at a scene change and at the game's covered UnloadUnusedAssets call), with the row on:
/// 1. the candidates: every live record of an asset that came from a file and that its key in the live-record list maps to (so the loader
///    makes it again, from the pack, the next time anything asks for it), in asset space (not a scene's object, not DontDestroyOnLoad), not
///    a shader / bundle / render target / solid texture / physics record, of a managed type with no finalizer and (a ScriptableObject)
///    no OnDisable / OnDestroy;
/// 2. for that one collection the registries let go of them (ById keeps a tombstone in the entry, the block index and the live place
///    hold null, the Registry lists are emptied, a tagged one leaves its tag's set) and each record is tied to its managed object as an
///    ephemeron (ConditionalWeakTable): a record lives exactly as long as anything other than the registries reaches it OR its managed
///    object -- the game's references, every scene record's fields, every engine cache;
/// 3. after the collection a record still reached goes back exactly where it was; a collected one is gone with its managed object, its
///    native block, its GPU textures, and its live place keeps its key (null until asked for again).
/// The release never touches anything the game can reach: whatever it can reach the collector keeps. What changes is when an asset is made:
/// again, from the pack, on its next use (as Unity reads it again) -- with its old instance id and its old ById entry (Unity keeps a
/// persistent object's id; the engine's enumeration order stays kit #12's). Unity's own difference shows too: an asset the game changed at
/// run time and then let go of comes back as the file has it.
/// Switches (env file): SHEPHERD_K13_UNLOAD=1 turns the release on -- it is OPT-IN in every profile (unset / 0 = off = kit #12's collections;
/// see <see cref="On"/> and the note), SHEPHERD_K13_WALKC=0 (this track off = kit #12). Test only: SHEPHERD_K13_UNLOAD_LOG=1 (the released
/// records by kind), SHEPHERD_K13_UNLOAD_MIDGC=1 / SHEPHERD_K13_UNLOAD_ARMLATE=1 (see <see cref="MidGc"/>).</summary>
[ProfileOnly]
public static class K13Unload
{
    public static readonly bool Master = Environment.GetEnvironmentVariable("SHEPHERD_K13_WALKC") != "0";
    /// <summary>SHEPHERD_K13_UNLOAD=1 (OPT-IN since the measurements: kit13-walkc.md): the ephemeron test of every asset record inside the covered
    /// collections. Default off: on the Vita (VLX) it released ~200 records on the walk for ~0.5-1.3 s of bookkeeping (VLX) per collection.</summary>
    public static readonly bool On = Master && Assets.Loader.LiveIndex && Environment.GetEnvironmentVariable("SHEPHERD_K13_UNLOAD") == "1";
    static readonly bool ByKind = Environment.GetEnvironmentVariable("SHEPHERD_K13_UNLOAD_LOG") == "1";
    /// <summary>Test only (vlx): SHEPHERD_K13_UNLOAD_MIDGC=1 makes a full collection halfway through the detach loop -- the window a collection
    /// started by another thread's allocation (or by this loop's own) can land in; with the array armed first it must change nothing (the same
    /// records released, the same records). SHEPHERD_K13_UNLOAD_ARMLATE=1 arms the array only after the loop (the branch's earlier order), the
    /// negative control: then the mid-loop collection frees detached records whose managed objects the game still holds.</summary>
    static readonly bool MidGc = Environment.GetEnvironmentVariable("SHEPHERD_K13_UNLOAD_MIDGC") == "1";
    static readonly bool ArmLate = Environment.GetEnvironmentVariable("SHEPHERD_K13_UNLOAD_ARMLATE") == "1";
    /// <summary>SHEPHERD_K13_ASYNC=1|0 (default ON): AsyncOps lets a finished operation go when the game does (Runtime/Lifecycle.cs).</summary>
    public static readonly bool Async = Master && Environment.GetEnvironmentVariable("SHEPHERD_K13_ASYNC") != "0";
    /// <summary>SHEPHERD_K13_STALE=1|0 (default ON): at every covered collection the engine's caches let go of destroyed records -- the destroyed
    /// animators' runtimes (AnimatorRuntime.All) and the post effects' last layer record (see <see cref="DropStale"/>). Exact (each cache is only
    /// read for live records); cheap (no scan of the records).</summary>
    public static readonly bool Stale = Master && Environment.GetEnvironmentVariable("SHEPHERD_K13_STALE") != "0";
    public static int AnimPurged;
    public static int Passes, Released, Kept, Remade, RemadeMissed; public static long ReleasedTotal; static double _ms, _gcMs;

    static readonly HashSet<SerializedFile> SceneFiles = new();
    /// <summary>Loader.InstantiateCore: a scene file (its records belong to a scene, never to the assets).</summary>
    public static void NoteSceneFile(SerializedFile f) { if (On && f != null) SceneFiles.Add(f); }

    /// <summary>A full collection (the callers' GC.Collect). With the row on, the release of unreached assets happens inside it. Returns a short
    /// note for the caller's log line (null with the row off).</summary>
    public static string Collect()
    {
        if (Stale) { AnimPurged += Anim.AnimatorRuntime.K13Purge(); DropStale(); }   // (kit #13 walkc: the caches' destroyed records -- before the collection)
        if (!On) { GC.Collect(); return null; }
        if (!Objects.SlotsExact) { GC.Collect(); return " [k13 unload: skipped -- a record was bound twice (the slot mirror is not exact)]"; }
        var sw = Stopwatch.StartNew();
        // 1. the candidates (ById's order)
        var cand = new List<NObj>(); var cpos = new List<int>();
        foreach (var n in Objects.ById.Values) { int p = Candidate(n); if (p > 0) { cand.Add(n); cpos.Add(p); } }
        int m = cand.Count;
        var id = new int[m]; var slot = new int[m]; var idx = new int[m]; var pos = cpos.ToArray(); var ptr = new IntPtr[m];
        var goName = new string[m]; var tex = new IntPtr[2 * m]; var tagged = new bool[m]; var kind = ByKind ? new string[m] : null;
        var h = new GCHandle[m];
        cpos = null;
        int done = 0, kept = 0, rel = 0; double prep = 0; var gsw = new Stopwatch(); Dictionary<string, int> byKind = kind != null ? new() : null;
        Registry[] regs = null;
#if !SHEPHERD_EBOOT
        var pathsKept = Diag.K13Paths.Want(Time.FrameCount) ? new List<NObj>() : null;   // (test only, Mac: SHEPHERD_K13_PATHS)
#endif
        try
        {
            // 2. the registries let go for one collection; each record lives while its managed object does (or anything reaches it)
            regs = Registry.K13Detach();
            // (the array is handed to the collector BEFORE the first record is detached: the runtime's ephemeron type has no GC map -- an entry
            // in an unregistered array neither keeps its record alive nor follows it when a collection moves it -- and any collection can land
            // inside this loop: another thread's allocation, near the heap's cap a major one)
            var tie = new Tie(m);
            if (!ArmLate) tie.Arm();
            for (int i = 0; i < m; i++)
            {
                var n = cand[i];
                id[i] = n.InstanceId; slot[i] = n.Slot; ptr[i] = n.Ptr;
                if (kind != null) kind[i] = n.Managed.GetType().Name;
                if (n is NGameObject g) goName[i] = g.Name;
                else if (n is NTexture t) { if (t.Cached) tex[2 * i] = t.CacheH; if (t.CachedSdf) tex[2 * i + 1] = t.CacheHSdf; }
                // (tracks resurrection: the collector clears a short weak handle BEFORE it revives what objects awaiting finalization reach and
                // runs the ephemeron passes again -- a record kept by that second pass would read as released while it and its managed object live)
                h[i] = GCHandle.Alloc(n, GCHandleType.WeakTrackResurrection);
                tie.Add(i, n.Managed, TieTo(n));
                idx[i] = Objects.K13Detach(n);
                Assets.Loader.K13Hole(pos[i]);
                if (n is NGameObject g2) tagged[i] = TagIndex.K13Out(g2);
                cand[i] = null;
                done = i + 1;
                if (MidGc && i == m / 2) GC.Collect();   // (test only: SHEPHERD_K13_UNLOAD_MIDGC)
            }
            cand = null;
            prep = sw.Elapsed.TotalMilliseconds;
            // 3. the collection
            if (ArmLate) tie.Arm();
            gsw.Start();
            GC.Collect();
            gsw.Stop();
            tie.KeepAlive();
        }
        finally
        {
            // 4. back, or gone (also after a failure part-way: every record detached so far is restored or dropped)
            var g0 = Render.SceneRenderer.G;
            for (int i = 0; i < done; i++)
            {
                var n = h[i].Target as NObj; h[i].Free();
                if (n != null)
                {
                    Objects.K13Restore(n, idx[i]); Assets.Loader.K13Unhole(pos[i], n);
                    if (tagged[i]) TagIndex.K13In((NGameObject)n);
                    kept++;
#if !SHEPHERD_EBOOT
                    pathsKept?.Add(n);
#endif
                    continue;
                }
                Objects.K13Drop(idx[i], ptr[i], goName[i]);
                if (g0 != null) { if (tex[2 * i] != IntPtr.Zero) g0.DestroyTexture(tex[2 * i]); if (tex[2 * i + 1] != IntPtr.Zero) g0.DestroyTexture(tex[2 * i + 1]); }
                Reserve(pos[i], id[i], slot[i]);
                if (byKind != null) byKind[kind[i]] = (byKind.TryGetValue(kind[i], out var c) ? c : 0) + 1;
                rel++;
            }
            Assets.Loader.K13Released(rel);
            if (regs != null) Registry.K13Rebuild(regs);
        }
        sw.Stop();
#if !SHEPHERD_EBOOT
        if (pathsKept != null) try { Diag.K13Paths.Run(pathsKept, new HashSet<NObj>(pathsKept)); } catch (Exception e) { Log.Info($"[k13paths] failed: {e.GetType().Name} {e.Message} at {e.StackTrace?.Replace("\n", " | ")}"); }
#endif
        double book = sw.Elapsed.TotalMilliseconds - gsw.Elapsed.TotalMilliseconds;
        Passes++; Released += rel; Kept += kept; ReleasedTotal += rel; _ms += book; _gcMs += gsw.Elapsed.TotalMilliseconds;
        if (byKind != null) Log.Info($"[k13] unload: released by kind: {string.Join(", ", byKind.OrderByDescending(kv => kv.Value).Take(24).Select(kv => $"{kv.Key} {kv.Value}"))}");
        return $" [k13 unload ({(Tie.Mono ? "one ephemeron array" : "ConditionalWeakTable")}): {m} asset records tested, {rel} released, {kept} still reached; {prep:F0} + {book - prep:F0} ms of bookkeeping around the collection; tombstones {Objects.K13Tombs}]";
    }

    /// <summary>Destroyed records that a single-record cache of the engine still holds -- with them the whole destroyed scene their Go /
    /// Components / Children links reach (Unity frees a destroyed object). The one found (SHEPHERD_K13_DEAD, Diag/K13Paths.cs: 13,419 destroyed
    /// records, 60,644 objects at the quote screen): the post effects' last PostProcessLayer record (the Derived DLL's PostEffects._layer),
    /// which keeps the Main Menu's destroyed camera -- and through it the destroyed menu -- until the next camera with a layer is drawn, i.e.
    /// through the intro's load. Exact: that field is read only right after the frame's camera search wrote it (Prepare -> Setup), so a
    /// destroyed value is never read; it is set to null, as at the session's start. Looked up by name in the Derived DLL (no Derived change);
    /// absent = nothing to do. Counted in the summary line.</summary>
    static FieldInfo _postLayer; static bool _postLooked; public static int StaleDropped;
    static void DropStale()
    {
        if (!_postLooked)
        {
            _postLooked = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Shepherd.Engine.Derived")
                    _postLayer = a.GetType("Shepherd.Engine.Derived.Render.PostEffects")?.GetField("_layer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (_postLayer != null && !typeof(NObj).IsAssignableFrom(_postLayer.FieldType)) _postLayer = null;
        }
        if (_postLayer != null && _postLayer.GetValue(null) is NObj { Destroyed: true }) { _postLayer.SetValue(null, null); StaleDropped++; }
    }

    /// <summary>The ephemerons for one collection. On Mono (the Vita) ONE array of the runtime's own ephemeron entries, registered once
    /// (`GC.register_ephemeron_array`, the mechanism Mono's ConditionalWeakTable is built on): VLX showed the ConditionalWeakTable making one
    /// small ephemeron array per entry -- 54,710 of them in one release (`SHEPHERD_K10_CENSUS`), each processed on its own by sgen -- so the
    /// collection took ~9x as long and their arrays stayed until the next collection. Where the runtime has no such entry point (CoreCLR on
    /// the Mac) a ConditionalWeakTable, as before. Either way: entry i's value lives while its key does.</summary>
    sealed class Tie
    {
        static readonly Type EphT = typeof(object).Assembly.GetType("System.Runtime.Ephemeron");
        const BindingFlags EF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo EphKey = EphT?.GetField("Key", EF) ?? EphT?.GetField("key", EF);       // (the Vita's CoreLib: Key / Value)
        static readonly FieldInfo EphVal = EphT?.GetField("Value", EF) ?? EphT?.GetField("value", EF);
        static readonly MethodInfo Reg = typeof(GC).GetMethod("register_ephemeron_array", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        public static readonly bool Mono = EphT != null && EphKey != null && EphVal != null && Reg != null && Environment.GetEnvironmentVariable("SHEPHERD_K13_UNLOAD_CWT") != "1";
        readonly Array _arr; readonly Pair[] _pairs; readonly ConditionalWeakTable<UObject, NObj> _cwt;
        /// <summary>The runtime's ephemeron entry's layout (two references, Key then Value: checked against its fields before use), so the array
        /// is filled through a typed view instead of three reflection calls per entry (VLX: ~1.9 s for 54k entries that way).</summary>
        struct Pair { public object Key, Value; }
        static readonly bool SameLayout = Mono && EphT.GetFields(EF).Length == 2 && EphKey.FieldType == typeof(object) && EphVal.FieldType == typeof(object) &&
            System.Runtime.InteropServices.Marshal.OffsetOf(EphT, EphKey.Name) == IntPtr.Zero;
        public Tie(int m) { if (Mono) { _arr = Array.CreateInstance(EphT, m); if (SameLayout) _pairs = Unsafe.As<Pair[]>(_arr); } else _cwt = new(); }
        public void Add(int i, UObject key, NObj value)
        {
            if (_cwt != null) { _cwt.Add(key, value); return; }
            if (_pairs != null) { _pairs[i].Key = key; _pairs[i].Value = value; return; }
            var box = Activator.CreateInstance(EphT); EphKey.SetValue(box, key); EphVal.SetValue(box, value); _arr.SetValue(box, i);   // (the boxed entry is copied in)
        }
        /// <summary>Before the first entry is written (Mono's own ephemeron users write into registered arrays too): the array is handed to the
        /// collector, which holds its entries as ephemerons from now on -- it keeps a value whose key lives and updates both when it moves them.</summary>
        public void Arm() { if (_arr != null) Reg.Invoke(null, new object[] { _arr }); }
        public void KeepAlive() { GC.KeepAlive(_arr); GC.KeepAlive(_cwt); }
    }

    /// <summary>What a candidate's managed object keeps alive: for a GameObject or a component, the root record of its hierarchy (a prefab's
    /// records reach each other both ways -- Parent / Children, Go / Components -- so they live and die together, and with every member's
    /// managed object tied to the same root the collector's ephemeron passes settle a whole prefab in one step instead of one per link of the
    /// chain); for any other asset, the record itself. Either way: the record lives while its managed object does.</summary>
    static NObj TieTo(NObj n)
    {
        var go = n is NGameObject g ? g : n is NComponent c ? c.Go : null;
        if (go == null || go.Components.Count == 0 || go.Components[0] is not NTransform t) return n;
        if (n is NComponent && !go.Components.Contains((NComponent)n)) return n;   // (only where the root's links do reach n: else n itself)
        int hops = 0;
        while (t.Parent != null && hops++ < 4096) { if (!t.Parent.Children.Contains(t)) return n; t = t.Parent; }
        return t.Go ?? n;
    }

    /// <summary>The live place (1-based) of a record the release may test, or 0.</summary>
    static int Candidate(NObj n)
    {
        if (n.Destroyed || n.DontDestroy || n.InstanceId <= 0 || !n.HasSource || n.Managed is null) return 0;
        if (n.SourceFile is not SerializedFile f || SceneFiles.Contains(f)) return 0;
        switch (n)
        {
            case NGameObject g: if (g.Scene != 0) return 0; break;
            case NComponent c:
                if (c.Go == null || c.Go.Scene != 0 || c.Go.DontDestroy) return 0;
                // (a physics record in asset space is in no physics world: InWorld / Phys3D need a scene -- Kill's Destroying hook has nothing to
                // undo for it; a record still queued for the next flush is reached through the queue and stays)
                break;
            case NShader: return 0;                                   // (Shader.Find answers from the live shader records)
            case NTexture t: if (t.IsRenderTexture || t.Solid || t.RtTarget != IntPtr.Zero) return 0; break;
            case Impl.NAssetBundle: return 0;
        }
        if (!TypeOk(n.Managed.GetType())) return 0;
        return Assets.Loader.K13Pos(n);
    }

    static readonly HashSet<Type> _ok = new(), _no = new();
    /// <summary>A managed type whose objects may go without a call: no finalizer (it would run on a released object), and for a
    /// ScriptableObject no OnDisable / OnDestroy (Unity calls them when it unloads one; the release cannot).</summary>
    static bool TypeOk(Type t)
    {
        if (_ok.Contains(t)) return true;
        if (_no.Contains(t)) return false;
        bool ok = true, so = typeof(ScriptableObject).IsAssignableFrom(t);
        const BindingFlags D = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var b = t; ok && b != null && b != typeof(UObject) && b != typeof(ScriptableObject) && b != typeof(MonoBehaviour); b = b.BaseType)
        {
            if (b.GetMethod("Finalize", D, null, Type.EmptyTypes, null) != null) ok = false;
            else if (so && (b.GetMethod("OnDisable", D, null, Type.EmptyTypes, null) != null || b.GetMethod("OnDestroy", D, null, Type.EmptyTypes, null) != null)) ok = false;
        }
        (ok ? _ok : _no).Add(t);
        return ok;
    }

    // ---- the reservations: a released asset's live place -> its old instance id and ById entry (its tombstone) ----
    static readonly Dictionary<int, int> _resAt = new();
    static int[] _resId = new int[256], _resSlot = new int[256]; static int _resN; static readonly Stack<int> _resFree = new();
    static void Reserve(int pos, int id, int slot)
    {
        int k = _resFree.Count > 0 ? _resFree.Pop() : _resN++;
        if (k >= _resId.Length) { Array.Resize(ref _resId, _resId.Length * 2); Array.Resize(ref _resSlot, _resSlot.Length * 2); }
        _resId[k] = id; _resSlot[k] = slot; _resAt[pos] = k;
    }
    /// <summary>Loader.LiveSet: a released asset was made again at its place -- the new record takes the old id and entry back.</summary>
    public static void Refilled(int pos, NObj n)
    {
        if (!_resAt.TryGetValue(pos, out var k)) { RemadeMissed++; return; }
        _resAt.Remove(pos); _resFree.Push(k);
        if (Objects.K13Rekey(n, _resId[k], _resSlot[k]) == 0) Remade++; else RemadeMissed++;
    }
    /// <summary>Loader.LiveRemove: a released asset's key left the live-record list (its tombstone stays in ById).</summary>
    public static void Forget(int pos) { if (_resAt.TryGetValue(pos, out var k)) { _resAt.Remove(pos); _resFree.Push(k); } }

    public static string Summary() => !Master ? null : !On ? $"[k13] unload: the asset release off (SHEPHERD_K13_UNLOAD=1 turns it on); caches' destroyed records {(Stale ? $"dropped: {AnimPurged} animators' runtimes, {StaleDropped} post-effects layer records" : "kept (SHEPHERD_K13_STALE=0)")}; finished async operations {(Async ? $"let go: {AsyncOps.K13Forgotten}" : "kept (SHEPHERD_K13_ASYNC=0)")}" :
        $"[k13] unload: {Passes} releases inside the covered collections, {ReleasedTotal} asset records released ({Kept} tests still reached), {Remade} made again with their old id ({RemadeMissed} not), {AnimPurged} destroyed animators' runtimes dropped, {StaleDropped} destroyed records dropped from the post effects' cache, {AsyncOps.K13Forgotten} finished async operations let go{(Async ? "" : " (SHEPHERD_K13_ASYNC=0: kept)")}, tombstones now {Objects.K13Tombs}, live places released {Assets.Loader.K13Holes}; bookkeeping {_ms:F0} ms, the collections themselves {_gcMs:F0} ms";
}
