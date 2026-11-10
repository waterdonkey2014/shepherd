#if !SHEPHERD_EBOOT
#define SHEPHERD_LOADPROF   // (kit #10: the loader profile's zone calls -- Assets/LoadProf.cs -- are compiled into Mac / engine-tools builds only)
#endif
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Shepherd.Assets;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
using UPPtr = Shepherd.Assets.PPtr;
namespace Shepherd.Engine.Assets;

/// <summary>A PPtr captured together with the file it lives in; resolved on first use.</summary>
public sealed class LazyRef
{
    public readonly SerializedFile File; public readonly UPPtr P;
    public LazyRef(SerializedFile f, UPPtr p) { File = f; P = p; }
    /// <summary>Loader.Managed(File, P). V6 stage 2: a successful resolution is remembered until Loader.Live changes: the lookup is
    /// Db.Resolve (a fixed file + object table walk) then Live[(file, pathId)], so with Live unchanged it finds the same record again
    /// (renderers' "sprite" references were resolved like this every frame). Not remembered while an AssetTrace lookup hook is
    /// installed (the pack recorder must see every lookup) or when the result is null (misses keep their logging).</summary>
    public UObject Resolve()
    {
        if (_epoch == Loader.LiveEpoch && _memo is not null)
        {
            if (Render.RenderCheck.On) { Render.RenderCheck.RefHits++; if (!ReferenceEquals(Loader.Managed(File, P), _memo)) Render.RenderCheck.RefBad++; }
            return _memo;
        }
        var r = Loader.Managed(File, P);
        if (r is not null && AssetTrace.ObjectLookup == null && AssetTrace.ObjectAbsent == null) { _memo = r; _epoch = Loader.LiveEpoch; } else { _memo = null; _epoch = -1; }
        return r;
    }
    UObject _memo; long _epoch = -1;
}

/// <summary>Instantiates Unity serialized data (scenes, prefabs, assets) as live engine objects.</summary>
public static class Loader
{
    public static AssetDatabase Db; public static AddressablesCatalog Catalog;
    /// <summary>(file name, path id) -> the record loaded for it. kit #5: one Dictionary&lt;(string, long), NObj&gt; (the intro's ~148,000 entries:
    /// 4.77 MiB of entries + buckets on 32-bit Mono). kit6 mem L4c (SHEPHERD_LIVE_INDEX=0 = that dictionary): the records in one list in the
    /// order they were added (the dictionary's enumeration order: it never removes), each index entry (ObjectInfo.LivePos) holding its record's
    /// place in that list; a key is the entry of the first SerializedFile loaded under that name (as the name keyed it), other entries of that
    /// name resolve to it by path id, and a path id that file lacks goes to a small map. Get / set / enumeration answer as the dictionary did.</summary>
    static readonly Dictionary<(string, long), NObj> Live = new();
    public static readonly bool LiveIndex = Environment.GetEnvironmentVariable("SHEPHERD_LIVE_INDEX") != "0";
    static readonly List<NObj> _liveOrder = new();
    static readonly Dictionary<string, SerializedFile> _liveOwner = new();
    static Dictionary<(string, long), object> _liveOther;   // (boxed positions: the reference-valued instantiation the engine already has -- no new AOT code)
    /// <summary>Kit #6 integration (the engine track's scene reload removes keys): the order list's free places, last freed first --
    /// Dictionary's free list, so a new key takes the place the dictionary would give it and the enumeration order stays the dictionary's.</summary>
    static int[] _liveFree; static int _liveFreeN;
    /// <summary>The live-record map's values in its enumeration order (diagnostics included).</summary>
#if !SHEPHERD_NO_K13WALKC
    public static IEnumerable<NObj> LiveRecords => !LiveIndex ? Live.Values : _liveFreeN == 0 && _k13Holes == 0 ? _liveOrder : LiveFilled();
    static IEnumerable<NObj> LiveFilled() { foreach (var n in _liveOrder) if (n != null) yield return n; }   // (only after a removal: the free places are null; kit #13 walkc: or a released asset's place)
#else
    public static IEnumerable<NObj> LiveRecords => !LiveIndex ? Live.Values : _liveFreeN == 0 ? _liveOrder : LiveFilled();
    static IEnumerable<NObj> LiveFilled() { foreach (var n in _liveOrder) if (n != null) yield return n; }   // (only after a removal: the free places are null)
#endif
#if !SHEPHERD_K11_TOC
    /// <summary>Where (f, o)'s key keeps its record position: the owner file's index entry, or <see cref="ObjectInfo.None"/> (then the side
    /// map). (Kit #12 walkb: the owner file's table searched in place -- kit #11 built that file's ObjectsByPathId dictionary for it.)</summary>
#else
    /// <summary>Where (f, o)'s key keeps its record position: the owner file's index entry, or null (then the side map).</summary>
#endif
    static ObjectInfo LiveEntry(SerializedFile f, ObjectInfo o)
    {
        if (!_liveOwner.TryGetValue(f.Name, out var owner)) { _liveOwner[f.Name] = f; return o; }
        if (ReferenceEquals(owner, f)) return o;
#if !SHEPHERD_K11_TOC
        return owner.FindQuiet(o.PathId);
#else
        return owner.ObjectsByPathId.TryGetValue(o.PathId, out var oo) ? oo : null;
#endif
    }
    static bool LiveTryGet(SerializedFile f, ObjectInfo o, out NObj n)
    {
        if (!LiveIndex) return Live.TryGetValue((f.Name, o.PathId), out n);
#if !SHEPHERD_K11_TOC
        var e = LiveEntry(f, o); int pos = !e.IsNull ? e.LivePos : _liveOther != null && _liveOther.TryGetValue((f.Name, o.PathId), out var p) ? (int)p : 0;
#else
        var e = LiveEntry(f, o); int pos = e != null ? e.LivePos : _liveOther != null && _liveOther.TryGetValue((f.Name, o.PathId), out var p) ? (int)p : 0;
#endif
#if !SHEPHERD_NO_K13WALKC
        n = pos > 0 ? _liveOrder[pos - 1] : null; return n != null;   // (kit #13 walkc: a released asset's place holds null -- made again on its next Get; kit #12 never has null at a place in use)
#else
        n = pos > 0 ? _liveOrder[pos - 1] : null; return pos > 0;
#endif
    }
    static void LiveSet(SerializedFile f, ObjectInfo o, NObj n)
    {
        if (!LiveIndex) { Live[(f.Name, o.PathId)] = n; return; }
#if !SHEPHERD_K11_TOC
        var e = LiveEntry(f, o); int pos = !e.IsNull ? e.LivePos : _liveOther != null && _liveOther.TryGetValue((f.Name, o.PathId), out var p) ? (int)p : 0;
#else
        var e = LiveEntry(f, o); int pos = e != null ? e.LivePos : _liveOther != null && _liveOther.TryGetValue((f.Name, o.PathId), out var p) ? (int)p : 0;
#endif
#if !SHEPHERD_NO_K13WALKC
        if (pos > 0) { var old = _liveOrder[pos - 1]; _liveOrder[pos - 1] = n; if (_shaderPos != null) ShaderTrack(pos, old, n); if (old == null && _k13Holes > 0) K13Refill(pos, n); return; }   // an existing key: its value replaced in place (the dictionary's overwrite; kit #13 walkc: a released asset made again)
#else
        if (pos > 0) { var old = _liveOrder[pos - 1]; _liveOrder[pos - 1] = n; if (_shaderPos != null) ShaderTrack(pos, old, n); return; }   // an existing key: its value replaced in place (the dictionary's overwrite)
#endif
        if (_liveFreeN > 0) { pos = _liveFree[--_liveFreeN]; _liveOrder[pos - 1] = n; }   // kit #6 integration: a removed key's place, as Dictionary reuses its last freed entry
        else { _liveOrder.Add(n); pos = _liveOrder.Count; }
        if (_shaderPos != null) ShaderTrack(pos, null, n);
#if !SHEPHERD_K11_TOC
        if (!e.IsNull) e.LivePos = pos; else (_liveOther ??= new())[(f.Name, o.PathId)] = pos;
#else
        if (e != null) e.LivePos = pos; else (_liveOther ??= new())[(f.Name, o.PathId)] = pos;
#endif
    }
    /// <summary>Kit #6 integration: (f, o)'s record leaves the map as Dictionary.Remove would take it out (the engine track's scene reload,
    /// written against kit #5's dictionary): its place becomes a free place (null) that the next new key takes, last freed first.</summary>
    static bool LiveRemove(SerializedFile f, ObjectInfo o, out NObj n)
    {
        if (!LiveIndex) { if (!Live.TryGetValue((f.Name, o.PathId), out n)) return false; Live.Remove((f.Name, o.PathId)); return true; }
        var e = LiveEntry(f, o); int pos;
#if !SHEPHERD_K11_TOC
        if (!e.IsNull) { pos = e.LivePos; e.LivePos = 0; }
#else
        if (e != null) { pos = e.LivePos; e.LivePos = 0; }
#endif
        else { pos = _liveOther != null && _liveOther.TryGetValue((f.Name, o.PathId), out var p) ? (int)p : 0; if (pos > 0) _liveOther[(f.Name, o.PathId)] = 0; }
        if (pos <= 0) { n = null; return false; }
        n = _liveOrder[pos - 1]; _liveOrder[pos - 1] = null;
#if !SHEPHERD_NO_K13WALKC
        bool k13hole = n == null && _k13Holes > 0;
        if (k13hole) { _k13Holes--; Runtime.K13Unload.Forget(pos); }   // (kit #13 walkc: the key of a released asset leaves: its reservation goes with it; no record to hand back)
#endif
        if (_shaderPos != null) ShaderTrack(pos, n, null);
        if (_liveFree == null || _liveFreeN == _liveFree.Length) { var a = new int[Math.Max(64, _liveFreeN * 2)]; if (_liveFree != null) Array.Copy(_liveFree, a, _liveFreeN); _liveFree = a; }
        _liveFree[_liveFreeN++] = pos;
#if !SHEPHERD_NO_K13WALKC
        return !k13hole;
#else
        return true;
#endif
    }
    /// <summary>Kit #10 E3 (create programs): the places in the live-record list that hold a shader record, ascending -- CreateShader's search
    /// for the first live shader of a name walked every live record (the menu: ~50,000 per shader asset, 188 shader assets); with the live index
    /// (SHEPHERD_LIVE_INDEX unset) LiveRecords IS that list in place order, so its first shader of the name is the first one at these places.
    /// Built on first use from the list, then kept by LiveSet / LiveRemove.</summary>
    static List<int> _shaderPos;
    static void ShaderTrack(int pos, NObj old, NObj now)
    {
        bool was = old is NShader, isNow = now is NShader;
        if (was == isNow) return;
        int i = _shaderPos.BinarySearch(pos);
        if (was && i >= 0) _shaderPos.RemoveAt(i);
        if (isNow && i < 0) _shaderPos.Insert(~i, pos);
    }
    static List<int> ShaderPositions()
    {
        if (_shaderPos != null) return _shaderPos;
        var l = new List<int>();
        for (int i = 0; i < _liveOrder.Count; i++) if (_liveOrder[i] is NShader) l.Add(i + 1);
        return _shaderPos = l;
    }
#if !SHEPHERD_NO_K13WALKC
    // ---- kit #13 walkc: Unity's UnloadUnusedAssets (Runtime/K13Unload.cs) -- a released asset's place in the live-record list stays its key's
    // place (its index entry keeps the position) and holds null until the asset is asked for again: Get then makes it at the same place, so
    // LiveRecords' order is kit #12's. (No string literal here: Assets/Diet.cs.) ----
    static int _k13Holes;
    internal static int K13Holes => _k13Holes;
    /// <summary>The place of n's own key in the live-record list (1-based), or 0 when n is not the record its key maps to.</summary>
    internal static int K13Pos(NObj n)
    {
        if (!LiveIndex || n.SourceFile is not SerializedFile f) return 0;
#if !SHEPHERD_K11_TOC
        var o = f.FindQuiet(n.PathId); if (o.IsNull) return 0;
        var e = LiveEntry(f, o); int pos = !e.IsNull ? e.LivePos : _liveOther != null && _liveOther.TryGetValue((f.Name, o.PathId), out var p) ? (int)p : 0;
#else
        if (!f.ObjectsByPathId.TryGetValue(n.PathId, out var o)) return 0;
        var e = LiveEntry(f, o); int pos = e != null ? e.LivePos : _liveOther != null && _liveOther.TryGetValue((f.Name, o.PathId), out var p) ? (int)p : 0;
#endif
        return pos > 0 && pos <= _liveOrder.Count && ReferenceEquals(_liveOrder[pos - 1], n) ? pos : 0;
    }
    internal static void K13Hole(int pos) => _liveOrder[pos - 1] = null;
    internal static void K13Unhole(int pos, NObj n) => _liveOrder[pos - 1] = n;
    internal static void K13Released(int count) => _k13Holes += count;   // (no LazyRef memo can hold a released record: its memo would have kept it reached)
    static void K13Refill(int pos, NObj n) { _k13Holes--; Runtime.K13Unload.Refilled(pos, n); }
#endif
    /// <summary>Bumped on every write to <see cref="Live"/> (LazyRef's memo key).</summary>
    public static long LiveEpoch;
    static readonly Queue<Action> Pending = new(); static int _depth;
    public static int LoadingScene; static SerializedFile _loadingFile;
    /// <summary>Only objects from the scene file being instantiated live in that scene; everything referenced from other files
    /// (prefabs, assets) stays in asset space (scene 0) until something Instantiates it.</summary>
    static int SceneOf(SerializedFile f) => f == _loadingFile ? LoadingScene : 0;
    public static readonly List<string> CustomTags = new();
    public static readonly List<string> LayerNames = new();
    /// <summary>Project Settings > Script Execution Order, from every MonoScript's m_ExecutionOrder (full type name -> order).</summary>
    public static readonly Dictionary<string, int> ScriptOrders = new();
    public static readonly List<(string name, uint id)> SortingLayers = new();
    public static int Created, Skipped;

    public static void Init()
    {
        Runtime.K10LoadTrack.Start();   // kit #10 load track: the start line, once (which rows are on; SHEPHERD_K10_LOAD=0: kit #9's loader)
        PackRecorder.Install();   // V3 part 2: SHEPHERD_PACK_RECORD (no-op unset)
        Db = new AssetDatabase(Program.GameData);
        Diet.Init(Db);
        PackAssets.Init(Db);      // V3 part 2: SHEPHERD_PACK serves files/bundles from a Vita pack (no-op unset)
        // the engine's own catalog copy is only counted below (~12 MiB): a pack run and the Vita profile do without it; the
        // game's Addressables keeps its own, and AssetDatabase.LoadScene reads catalog.json lazily if a scene is not in a loaded bundle
        if (!PackAssets.On && !Profile.Vita)
        {
            var aa = Path.Combine(Program.GameData, "StreamingAssets", "aa");
            Catalog = AddressablesCatalog.Load(Path.Combine(aa, "catalog.json"), aa);
        }
        Conv.ResolveRef = (v, t) => v is LazyRef r ? r.Resolve() : v is UObject u ? u : null;
        Conv.FromTree = (v, t) => v is TypeTreeObject tt ? FromTree(tt, t, null, 0) : null;
        RenderIcalls.LoadPixels = DecodePixels;
        Render.SceneRenderer.SortingLayerValue = c => SortingValue(c.Get("sortingLayerID") is { } id ? unchecked((uint)Convert.ToInt64(id)) : 0u);
        LoadTagManager();
        LoadScriptOrders();
        // physics (plan v2 1.4): from Stage 1 the project's physics settings are read and self-tested at boot here; at level 0 / none they stay
        // lazy exactly as in kit #4 (Physics2DSettings at the first trigger candidate; the menus read nothing; physcheck runs the self-test)
        if (Physics.PhysConfig.Level >= 1 && Physics.PhysWorld.Sim) LoadPhysicsSettings();   // (level 1 without a core: Stage 0's lazy read)
#if !SHEPHERD_K11_TOC
        Log.Info($"assets: catalog {Catalog?.InternalIds.Count ?? 0} ids; {SortingLayers.Count} sorting layers; {CustomTags.Count} custom tags; index entries: " +
                 (Shepherd.Assets.ObjectTable.View ? "a view over each file's object table (kit #12 walkb: no object per entry)" : "one object per entry (kit #11's; SHEPHERD_K12_TOC=0)"));
#else
        Log.Info($"assets: catalog {Catalog?.InternalIds.Count ?? 0} ids; {SortingLayers.Count} sorting layers; {CustomTags.Count} custom tags");
#endif
    }

    // ---------------- identity / resolution ----------------
    public static UObject Managed(SerializedFile from, UPPtr p) => p.IsNull ? null : Get(from, p)?.Managed;
    public static NObj Get(SerializedFile from, UPPtr p)
    {
        if (p.IsNull) return null;
        (SerializedFile f, ObjectInfo o) r;
        LoadProf.Enter(LoadProf.Resolve);   // (the profile's calls: compiled out of eboots)
        try { r = Db.Resolve(from, p); } catch (Exception e) { LoadProf.Exit(LoadProf.Resolve); Log.Warn($"resolve {p} from {from?.Name}: {e.Message}"); return null; }
        LoadProf.Exit(LoadProf.Resolve);
#if !SHEPHERD_K11_TOC
        return r.f == null || r.o.IsNull ? null : Get(r.f, r.o);
#else
        return r.f == null || r.o == null ? null : Get(r.f, r.o);
#endif
    }

    public static NObj Get(SerializedFile f, ObjectInfo o)
    {
        if (LiveTryGet(f, o, out var n)) return n;
        _depth++; int pz = LoadProf.On ? ProfZone(o.ClassId) : 0; LoadProf.Enter(pz);
        try { n = Create(f, o); }
        catch (Exception e) { Log.Exception(e, $"load {ClassIds.Name(o.ClassId)} #{o.PathId} in {f.Name}"); n = null; }
        finally { _depth--; LoadProf.Exit(pz); }
        if (_depth == 0) { Drain(); if (LoadDump.On) LoadDump.Point("load " + f.Name); }
        return n;
    }

    static int ProfZone(int classId) => classId switch { 1 => LoadProf.CreateGo, 4 or 224 => LoadProf.CreateTr, 114 => LoadProf.CreateMono, 213 => LoadProf.CreateSprite, 28 or 84 => LoadProf.CreateTexture,
        21 => LoadProf.CreateMaterial, 48 => LoadProf.CreateShader, 43 => LoadProf.CreateMesh, 49 => LoadProf.CreateText, 91 => LoadProf.CreateAsset, _ => LoadProf.CreateOther };
    static void Drain()
    {
        if (LoadProf.On) { LoadProf.Enter(LoadProf.Drain); try { DrainCore(); } finally { LoadProf.Exit(LoadProf.Drain); } }
        else DrainCore();
    }
    static void DrainCore()
    {
        _depth++;
        try { while (Pending.Count > 0) { var a = Pending.Dequeue(); try { a(); } catch (Exception e) { Log.Exception(e, "populate"); } } }
        finally { _depth--; }
    }

    static T Reg<T>(SerializedFile f, ObjectInfo o, T n) where T : NObj { n.SourceFile = f; n.PathId = o.PathId; LiveSet(f, o, n); LiveEpoch++; Created++; if (LoadDump.On) LoadDump.Created(n); return n; }

    static TypeTreeObject Read(SerializedFile f, ObjectInfo o)
    {
        if (!LoadProf.On) return TypeTreeReader.Read(f, o);
        LoadProf.Enter(LoadProf.Tree); try { return TypeTreeReader.Read(f, o); } finally { LoadProf.Exit(LoadProf.Tree); }
    }
    static object V(TypeTreeObject t, string k) => t != null && t.TryGet(k, out var v) ? v : null;
    static int I(TypeTreeObject t, string k, int d = 0) => V(t, k) is { } v ? unchecked((int)Convert.ToInt64(v)) : d;
    static float Fl(TypeTreeObject t, string k, float d = 0) => V(t, k) is { } v ? Convert.ToSingle(v) : d;
    static string S(TypeTreeObject t, string k) => V(t, k) as string ?? "";

    static NObj Create(SerializedFile f, ObjectInfo o)
    {
        switch (o.ClassId)
        {
            case 1: return CreateGameObject(f, o);
            case 4: case 224: return CreateTransform(f, o);
            case 114: return CreateMonoBehaviour(f, o);
            case 28: return CreateTexture(f, o);
            case 84: { var d = Read(f, o); var t = (Texture)Objects.NewManaged(typeof(RenderTexture)); return Reg(f, o, Objects.Bind(t, new NTexture { Name = S(d, "m_Name"), Width = I(d, "m_Width", 256), Height = I(d, "m_Height", 256), IsRenderTexture = true, Loaded = true }, true)); }   // polish round 2: no CPU pixel copy (it is a render target; was w*h*4 zero bytes)
            case 213: return CreateSprite(f, o);
            case 21: return CreateMaterial(f, o);
            case 48: return CreateShader(f, o);
            case 43: return CreateMesh(f, o);
            case 49: return CreateTextAsset(f, o);
            case 91: { TypeTreeObject d = Read(f, o); var rac = Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(RuntimeAnimatorController)), new NObj { Name = S(d, "m_Name") }, true)); rac.SetSource(d); return rac; }
            case 50: case 58: case 60: case 61: case 62: case 64: case 65: case 66: case 68: case 70: case 135: case 136: case 19719996:
                return CreatePhysics(f, o);   // physics stage 0: Rigidbody2D, the 2D / 3D colliders, PhysicsMaterial2D, TilemapCollider2D
            default: return CreateOther(f, o);
        }
    }
    /// <summary>Physics Stage 1 boot: the 2D settings only (Physics2DSettings, the record kit #4's trigger pass reads anyway). TimeManager and
    /// PhysicsManager are read by `physcheck settings` (their values are checked there against the engine's constants): a strict pack recorded
    /// at level 0 does not carry them, and loading them here would be a MISSING line.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] static void LoadPhysicsSettings() => Runtime.Physics2DSettings.EnsureLoaded();
    /// <summary>Physics stage 0 (plan v2 1.4): the bag record exactly as CreateOther makes it (SeedProps, enabled, attach -- the live Triggers2D
    /// and the diet keep working until Stage 2) plus the typed record's fields from the same type tree. Never inlined, so compiling Create
    /// loads no physics type; at SHEPHERD_PHYS=none it is CreateOther itself.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static NObj CreatePhysics(SerializedFile f, ObjectInfo o)
    {
        if (!Physics.PhysConfig.On) return CreateOther(f, o);
        var rec = CreateOther(f, o, Physics.PhysRecords.NewAsset(o.ClassId), out var d);   // components: NewComponentRecord gives the typed record
        if (rec != null && d != null) Physics.PhysRecords.Fill(rec, o.ClassId, d, f);
        return rec;
    }

    // ---------------- GameObject / components ----------------
    static NObj CreateGameObject(SerializedFile f, ObjectInfo o)
    {
        var d = Read(f, o); bool fx = TreeIdx.Use(f);
        var go = Reg(f, o, Objects.Bind((GameObject)Objects.NewManaged(typeof(GameObject)), new NGameObject
        {
            Name = TS(d, fx, TreeIdx.Name, "m_Name"), ActiveSelf = TI(d, fx, TreeIdx.IsActive, "m_IsActive", 1) != 0, Layer = TI(d, fx, TreeIdx.Layer, "m_Layer"), Tag = TagName((uint)TI(d, fx, TreeIdx.Tag, "m_Tag")), Scene = SceneOf(f)
        }, SceneOf(f) == 0));
        go.SetSource(d);
        if (TV(d, fx, TreeIdx.Component, "m_Component") is IList comps)
            foreach (var e in comps)
            {
                var pp = e is TypeTreeObject ct ? TV(ct, fx, TreeIdx.ComponentInner, "component") : e;
                if (pp is not UPPtr p) continue;
                if (Get(f, p) is NComponent c && !go.Components.Contains(c))
                {
                    c.Go = go; c.Name = go.Name; go.Components.Add(c);
                    if (c is Physics.NRigidbody2D or Physics.NCollider2D or Physics.NCollider3D) Physics.PhysWorld.Added(c);   // physics stage 0: the add hook
                }
            }
        var tr = go.Components.FirstOrDefault(c => c is NTransform);
        if (tr == null)
        {
            tr = Objects.Bind(Objects.NewManaged(typeof(Transform)), new NTransform { Go = go, Name = go.Name });
            go.Components.Insert(0, tr);
        }
        else if (go.Components[0] != tr) { go.Components.Remove(tr); go.Components.Insert(0, tr); }
        if (Physics.PhysConfig.On) foreach (var c in go.Components) if (c is Physics.NRigidbody2D or Physics.NCollider2D || (c is Physics.NCollider3D && Physics.PhysWorld.Sim6)) { Physics.PhysWorld.Assembled(go); break; }   // physics stage 0 (Stage 6: 3D colliders' interest bit 4, level 6 only)
        return go;
    }

    static void AttachToGameObject(NComponent c, SerializedFile f, TypeTreeObject d)
    {
        if (TV(d, TreeIdx.Use(f), TreeIdx.GameObject, "m_GameObject") is UPPtr gp && !gp.IsNull && Get(f, gp) is NGameObject go)
        {
            c.Go = go; c.Name = go.Name;
            if (!go.Components.Contains(c)) go.Components.Add(c);
            if (c is Physics.NRigidbody2D or Physics.NCollider2D or Physics.NCollider3D) Physics.PhysWorld.Added(c);   // physics stage 0: the add hook
        }
    }

    static NObj CreateTransform(SerializedFile f, ObjectInfo o)
    {
        var d = Read(f, o); bool rect = o.ClassId == 224; bool fx = TreeIdx.Use(f);
        var t = Reg(f, o, Objects.Bind(Objects.NewManaged(rect ? typeof(RectTransform) : typeof(Transform)), new NTransform { IsRect = rect }, SceneOf(f) == 0));
        t.SetSource(d);
        t.LocalRotation = TQ(TV(d, fx, TreeIdx.LocalRotation, "m_LocalRotation"), fx); t.LocalPosition = TV3(TV(d, fx, TreeIdx.LocalPosition, "m_LocalPosition"), fx); t.LocalScale = TV3(TV(d, fx, TreeIdx.LocalScale, "m_LocalScale"), fx, Vector3.one);
        if (rect)
        {
            t.AnchorMin = TV2(TV(d, fx, TreeIdx.AnchorMin, "m_AnchorMin"), fx); t.AnchorMax = TV2(TV(d, fx, TreeIdx.AnchorMax, "m_AnchorMax"), fx); t.AnchoredPosition = TV2(TV(d, fx, TreeIdx.AnchoredPosition, "m_AnchoredPosition"), fx);
            t.SizeDelta = TV2(TV(d, fx, TreeIdx.SizeDelta, "m_SizeDelta"), fx); t.Pivot = TV2(TV(d, fx, TreeIdx.Pivot, "m_Pivot"), fx, new Vector2(0.5f, 0.5f));
        }
        AttachToGameObject(t, f, d);
        Pending.Enqueue(() =>
        {
            if (TV(d, fx, TreeIdx.Children, "m_Children") is IList kids)
                foreach (var k in kids)
                    if (k is UPPtr kp && Get(f, kp) is NTransform ct && ct.Parent != t) { ct.Parent?.Children.Remove(ct); ct.Parent = t; t.Children.Add(ct); }
        });
        return t;
    }

    static readonly Dictionary<string, Type> ScriptCache = new();
    public static Type ScriptType(string asm, string ns, string cls)
    {
        var key = $"{asm}|{ns}|{cls}";
        if (ScriptCache.TryGetValue(key, out var t)) return t;
        var full = string.IsNullOrEmpty(ns) ? cls : ns + "." + cls; var an = asm != null && asm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? asm[..^4] : asm;
        try { t = Assembly.Load(an).GetType(full); } catch { t = null; }
        t ??= AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(full)).FirstOrDefault(x => x != null);
        if (t == null) Log.Warn($"script type not found: {full} ({asm})");
        return ScriptCache[key] = t;
    }

#if !SHEPHERD_EBOOT
    static readonly HashSet<string> TraceCreate = Environment.GetEnvironmentVariable("SHEPHERD_TRACE_CREATE") is { Length: > 0 } tc ? new(tc.Split(',')) : null;
    static void TraceCreated(Type type, NObj rec, SerializedFile f, ObjectInfo o)
    {
        var st = new System.Diagnostics.StackTrace(2, false).ToString().Replace(Environment.NewLine, " < ").Replace("   at ", "");
        Log.Info($"[create] {type.Name} '{rec.Name}' file={f.Name} pathId={o.PathId} scene={SceneOf(f)} depth={_depth} via {st[..Math.Min(1500, st.Length)]}");
    }
#else
    static readonly HashSet<string> TraceCreate = null;   // kit #5 integration: the kit5 mem diagnostic is not in the eboot (SHEPHERD_ENGINE_TOOLS=1 keeps it)
    static void TraceCreated(Type type, NObj rec, SerializedFile f, ObjectInfo o) { }
#endif
    static NObj CreateMonoBehaviour(SerializedFile f, ObjectInfo o)
    {
        var info = Db.GetMonoScript(f, o); bool fx = TreeIdx.Use(f);
        Type type;
        if (fx && info != null) { if (!_scriptTypes.TryGetValue(info, out type)) _scriptTypes[info] = type = ScriptType(info.AssemblyName, info.Namespace, info.ClassName); }   // kit #10: ScriptType's answer per script (the cached MonoScriptInfo)
        else type = info == null ? null : ScriptType(info.AssemblyName, info.Namespace, info.ClassName);
        if (type == null) { Skipped++; return null; }
        TypeTreeObject d = null; bool fullTree = true;
        try { d = Read(f, o); } catch { fullTree = false; }
        if (d != null && !(fx ? TreeIdx.HasScriptFields(d, type) : HasScriptFields(d, type))) fullTree = false;
        var managed = Objects.NewManaged(type);
        int tk = fx ? TypeKind(type) : 0;
        bool isComponent = fx ? (tk & 1) != 0 : typeof(Component).IsAssignableFrom(type);
        NObj rec = isComponent ? ((tk & 2) != 0 ? new NMono() : Lifecycle.NewComponentRecord(type)) : new NObj();   // (kind bit 2: NewComponentRecord's first case, a MonoBehaviour)
        Reg(f, o, Objects.Bind(managed, rec, !isComponent || SceneOf(f) == 0));
        rec.Name = d != null ? TS(d, fx, TreeIdx.Name, "m_Name") : ""; rec.SetSource(d);
        if (TraceCreate != null && TraceCreate.Contains(type.Name)) TraceCreated(type, rec, f, o);
        if (SpineShare.On && type.Name == "SkeletonDataAsset") SpineShare.Register(managed);   // kit5 L1 prototype (off unless SHEPHERD_SPINE_SHARE=1)
        if (Diag.SpineCats.On && type.Name == "SkeletonDataAsset") Diag.SpineCats.Register(managed);   // kit6 mem L1 measurement (off unless SHEPHERD_SPINE_CATS=1)
        if (rec is NBehaviour b) b.Enabled = d == null || TI(d, fx, TreeIdx.Enabled, "m_Enabled", 1) != 0;
        if (LoadProf.On) { LoadProf.Enter(LoadProf.GameCtor); try { Objects.RunCtor(managed); } finally { LoadProf.Exit(LoadProf.GameCtor); } }
        else Objects.RunCtor(managed);
        if (rec is NComponent c && d != null) AttachToGameObject(c, f, d);
        Pending.Enqueue(() =>
        {
            if (LoadProf.On)
            {
                LoadProf.Enter(LoadProf.Populate); long pt0 = LoadProf.Now();
                try { PopulateStep(managed, type, d, f, o, fullTree); } finally { LoadProf.Exit(LoadProf.Populate); LoadProf.TypeDone(type, pt0); }
                LoadProf.Enter(LoadProf.GameAfter);
                try { AfterStep(managed, type, rec, isComponent); } finally { LoadProf.Exit(LoadProf.GameAfter); }
            }
            else { PopulateStep(managed, type, d, f, o, fullTree); AfterStep(managed, type, rec, isComponent); }
        });
        return rec;
    }
    static void PopulateStep(object managed, Type type, TypeTreeObject d, SerializedFile f, ObjectInfo o, bool fullTree)
    {
        if (fullTree && d != null) { if (!FieldPrograms.TryPopulate(managed, type, d, f)) Populate(managed, type, d, f); }   // kit #10 E3: the field program (SHEPHERD_K10_FIELDPROG=0: Populate)
        else if (!BinPrograms.TryPopulate(managed, type, f, o)) PopulateByReflection(managed, type, f, o);   // kit #10: the binary field program (SHEPHERD_K10_BINPROG=0: the reflection walk)
        if (Profile.Vita) VitaMemory.AfterPopulate(managed, type);   // stage 1: single-language I2 sources
    }
    static void AfterStep(object managed, Type type, NObj rec, bool isComponent)
    {
        if (managed is ISerializationCallbackReceiver scr) { try { scr.OnAfterDeserialize(); } catch (Exception e) { Log.Exception(e, $"OnAfterDeserialize {type.Name}"); } }
        if (!isComponent && managed is ScriptableObject) Lifecycle.ScriptableAwakeEnable(rec);
    }
    internal static bool HasScriptFields(TypeTreeObject d, Type t) { var fs = SerRules.Fields(t); return fs.Length == 0 || fs.Any(x => d.TryGet(x.Name, out _)); }
    // ---- kit #10 E3, create programs (Assets/TreeIdx.cs): the same reads by place for pack trees, the same answers per type / per script ----
    static readonly Dictionary<object, Type> _scriptTypes = new(TreeIdx.ByRef);
    static readonly Dictionary<Type, int> _typeKinds = new();
    /// <summary>bit 1: typeof(Component).IsAssignableFrom(t); bit 2: Lifecycle.NewComponentRecord's first case (typeof(MonoBehaviour).IsAssignableFrom(t)).</summary>
    static int TypeKind(Type t)
    {
        if (_typeKinds.TryGetValue(t, out var k)) return k;
        k = (typeof(Component).IsAssignableFrom(t) ? 1 : 0) | (typeof(MonoBehaviour).IsAssignableFrom(t) ? 2 : 0);
        return _typeKinds[t] = k;
    }
    static object TV(TypeTreeObject d, bool fx, int which, string k) => fx ? TreeIdx.V(d, which) : V(d, k);
    static int TI(TypeTreeObject d, bool fx, int which, string k, int def = 0) { var v = fx ? TreeIdx.V(d, which) : V(d, k); return v is null ? def : v is int i ? i : v is byte b ? b : unchecked((int)Convert.ToInt64(v)); }
    static string TS(TypeTreeObject d, bool fx, int which, string k) => (fx ? TreeIdx.V(d, which) : V(d, k)) as string ?? "";
    static float TFl(TypeTreeObject t, int which, float d = 0) => TreeIdx.V(t, which) is { } v ? (v is float x ? x : Convert.ToSingle(v)) : d;
    static Vector3 TV3(object o, bool fx, Vector3 def = default) => !fx ? V3(o, def) : o is TypeTreeObject t ? new Vector3(TFl(t, TreeIdx.X), TFl(t, TreeIdx.Y), TFl(t, TreeIdx.Z)) : def;
    static Vector2 TV2(object o, bool fx, Vector2 def = default) => !fx ? V2(o, def) : o is TypeTreeObject t ? new Vector2(TFl(t, TreeIdx.X), TFl(t, TreeIdx.Y)) : def;
    static Quaternion TQ(object o, bool fx) => !fx ? Q(o) : o is TypeTreeObject t ? M.QNorm(new Quaternion(TFl(t, TreeIdx.X), TFl(t, TreeIdx.Y), TFl(t, TreeIdx.Z), TFl(t, TreeIdx.W, 1))) : Quaternion.identity;

    static readonly Dictionary<string, Type> BuiltinTypes = new();
    static Type BuiltinType(int classId)
    {
        var name = ClassIds.Name(classId);
        if (BuiltinTypes.TryGetValue(name, out var t)) return t;
        // Vita profile: name lookups instead of GetTypes() over every UnityEngine* assembly (Runtime/VitaMemory.cs)
        t = VitaMemory.Builtin ? VitaMemory.BuiltinType(name, typeof(UObject)) : ScanBuiltin(name);
        return BuiltinTypes[name] = t;
    }
    static Type ScanBuiltin(string name) => AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("UnityEngine"))
        .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null); } })
        .FirstOrDefault(x => x.Name == name && typeof(UObject).IsAssignableFrom(x) && !x.IsAbstract);
    public static void CheckBuiltins() => VitaMemory.CheckBuiltins(ScanBuiltin);

    static NObj CreateOther(SerializedFile f, ObjectInfo o) => CreateOther(f, o, null, out _);
    /// <summary>rec0: a record to bind instead of the default one (physics stage 0: a typed asset record); d: the type tree it was seeded from.</summary>
    static NObj CreateOther(SerializedFile f, ObjectInfo o, NObj rec0, out TypeTreeObject d)
    {
        d = null;
        var type = BuiltinType(o.ClassId);
        if (type == null) { Skipped++; return null; }
        try { d = Read(f, o); } catch { d = null; }
        bool isComponent = TreeIdx.On ? (TypeKind(type) & 1) != 0 : typeof(Component).IsAssignableFrom(type);
        NObj rec = isComponent ? Lifecycle.NewComponentRecord(type) : rec0 ?? new NObj();
        Reg(f, o, Objects.Bind(Objects.NewManaged(type), rec, !isComponent || SceneOf(f) == 0));
        bool fx = TreeIdx.Use(f);
        rec.SetSource(d); rec.Name = d != null ? TS(d, fx, TreeIdx.Name, "m_Name") : "";
        if (d != null) SeedProps(rec, d, f);
        if (rec is NBehaviour b && d != null) b.Enabled = TI(d, fx, TreeIdx.Enabled, "m_Enabled", 1) != 0;
        if (rec is NComponent c && d != null) AttachToGameObject(c, f, d);
        return rec;
    }

    // Unity field name -> scripting property name (the auto-prop key)
    static readonly Dictionary<string, string> Renames = new()
    {
        ["m_Materials"] = "sharedMaterials", ["m_BackGroundColor"] = "backgroundColor", ["near clip plane"] = "nearClipPlane", ["far clip plane"] = "farClipPlane",
        ["field of view"] = "fieldOfView", ["orthographic size"] = "orthographicSize", ["m_NormalizedViewPortRect"] = "rect", ["m_Camera"] = "worldCamera",
        ["m_Mesh"] = "sharedMesh", ["m_Controller"] = "runtimeAnimatorController", ["m_audioClip"] = "clip", ["m_SortingLayerID"] = "sortingLayerID",
        ["m_Enabled"] = "enabled", ["m_Sprite"] = "sprite",
    };
    [ThreadStatic] static PropBag.Pending _seed;
    static void SeedProps(NObj rec, TypeTreeObject d, SerializedFile f)
    {
        if (SeedPrograms.TrySeed(rec, d, f)) return;   // kit #10 E3: the seed program (SHEPHERD_K10_SEEDPROG=0: the walk below)
        // kit6 mem L4a: the kept keys are collected and set as one (NObj.SetMany: the bag a Set per key would leave, built once)
        var kept = PropBag.Shaped ? (_seed ??= new()) : null;
        if (kept != null) { kept.Clear(); try { SeedProps(rec, d, f, kept); } finally { rec.SetMany(kept); kept.Clear(); } }
        else SeedProps(rec, d, f, null);
    }
    static void SeedProps(NObj rec, TypeTreeObject d, SerializedFile f, PropBag.Pending kept)
    {
        foreach (var kv in d)
        {
            var k = kv.Key; if (k is "m_GameObject" or "m_Name" or "m_Script" or "m_ObjectHideFlags" or "m_CorrespondingSourceObject" or "m_PrefabInstance" or "m_PrefabAsset") continue;
            var name = PropName(k);
            // V3 diet: a bulky value (list/array or unrecognised struct) under a key nothing can read -- no auto getter has
            // that name and no engine code mentions it -- is not seeded (Font.m_FontData, ParticleSystem modules, ...).
            // Scalars, Unity structs and object references are always seeded (animation may target any of them).
            if (!Diet.KeepProps && IsBulky(kv.Value) && !Diet.IsReadableKey(name)) { Diet.PropDropped(rec, name); continue; }
            Diet.PropsKeysKept++;
            if (kept != null) kept.Add(name, Plain(kv.Value, f)); else rec.Set(name, Plain(kv.Value, f));
        }
    }
    /// <summary>A Unity field name's scripting property name (the auto-prop key).</summary>
    internal static string PropName(string k) => Renames.TryGetValue(k, out var r) ? r : k.StartsWith("m_") && k.Length > 2 ? char.ToLowerInvariant(k[2]) + k[3..] : k;
    /// <summary>Values Plain() would keep as a list / byte[] / unrecognised TypeTreeObject (the expensive kinds).</summary>
    internal static bool IsBulky(object v) => v is System.Collections.IList ||
        v is TypeTreeObject t && t.TypeName is not ("Vector2f" or "Vector3f" or "Vector4f" or "Quaternionf" or "ColorRGBA" or "Rectf" or "BitField" or "AABB");
    /// <summary>Type-tree value -> a plain engine value (Unity structs recognised by shape, PPtrs -> LazyRef, lists recursively).</summary>
    public static object Plain(object v, SerializedFile f)
    {
        switch (v)
        {
            case UPPtr p: return p.IsNull ? null : new LazyRef(f, p);
            case IList l when v is not byte[]: { var r = new List<object>(l.Count); foreach (var e in l) r.Add(Plain(e, f)); return r; }
            case TypeTreeObject t:
                switch (t.TypeName)
                {
                    case "Vector2f": return V2(t); case "Vector3f": return V3(t); case "Vector4f": return new Vector4(Fl(t, "x"), Fl(t, "y"), Fl(t, "z"), Fl(t, "w"));
                    case "Quaternionf": return Q(t); case "ColorRGBA": return Col(t); case "Rectf": return new Rect(Fl(t, "x"), Fl(t, "y"), Fl(t, "width"), Fl(t, "height"));
                    case "BitField": return I(t, "m_Bits");
                    case "AABB": return new Bounds(V3(V(t, "m_Center")), V3(V(t, "m_Extent")) * 2f);
                }
                return t;
            default: return v;
        }
    }

    // ---------------- MonoBehaviour field population (type tree) ----------------
    public static void Populate(object target, Type type, TypeTreeObject d, SerializedFile f)
    {
        foreach (var fi in SerRules.Fields(type))
        {
            if (!d.TryGet(fi.Name, out var v)) continue;
            try { fi.SetValue(target, ToManaged(v, fi.FieldType, f, 0)); }
            catch (Exception e) { Log.Warn($"populate {type.Name}.{fi.Name}: {e.Message}"); }
        }
    }

    public static object ToManaged(object v, Type t, SerializedFile f, int depth)
    {
        if (depth > 16) return t.IsValueType ? Activator.CreateInstance(t) : null;
        if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
        if (v is UPPtr p)
        {
            if (!typeof(UObject).IsAssignableFrom(t) && !t.IsInterface) return t.IsValueType ? Activator.CreateInstance(t) : null;
            var m = Managed(f, p); return m != null && t.IsInstanceOfType(m) ? m : null;
        }
        if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(v));
        if (t == typeof(bool)) return Convert.ToInt64(v) != 0;
        if (t.IsPrimitive) return Convert.ChangeType(v, t);
        if (t == typeof(string)) return v as string ?? (v is byte[] bs ? System.Text.Encoding.UTF8.GetString(bs) : v.ToString());
        if (t == typeof(byte[]) && v is byte[] raw) return raw;
        if (t.IsArray)
        {
            var et = t.GetElementType();
            if (v is byte[] b && et == typeof(byte)) return b;
            var src = v as IList ?? Array.Empty<object>(); var arr = Array.CreateInstance(et, src.Count);
            for (int i = 0; i < src.Count; i++) arr.SetValue(ToManaged(src[i], et, f, depth + 1), i);
            return arr;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var et = t.GetGenericArguments()[0]; var list = (IList)Activator.CreateInstance(t);
            if (v is byte[] b2 && et == typeof(byte)) { foreach (var x in b2) list.Add(x); return list; }
            if (v is IList src) foreach (var e in src) list.Add(ToManaged(e, et, f, depth + 1));
            return list;
        }
        if (v is TypeTreeObject tt) return FromTree(tt, t, f, depth);
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    static object FromTree(TypeTreeObject t, Type type, SerializedFile f, int depth)
    {
        if (type == typeof(Vector2)) return V2(t); if (type == typeof(Vector3)) return V3(t);
        if (type == typeof(Vector4)) return new Vector4(Fl(t, "x"), Fl(t, "y"), Fl(t, "z"), Fl(t, "w"));
        if (type == typeof(Quaternion)) return Q(t); if (type == typeof(Color)) return Col(t);
        if (type == typeof(Color32)) { if (V(t, "rgba") is { } rgba) { uint u = Convert.ToUInt32(rgba); return new Color32((byte)u, (byte)(u >> 8), (byte)(u >> 16), (byte)(u >> 24)); } return (Color32)Col(t); }
        if (type == typeof(Rect)) return new Rect(Fl(t, "x"), Fl(t, "y"), Fl(t, "width"), Fl(t, "height"));
        if (type == typeof(Vector2Int)) return new Vector2Int(I(t, "x"), I(t, "y"));
        if (type == typeof(Vector3Int)) return new Vector3Int(I(t, "x"), I(t, "y"), I(t, "z"));
        if (type == typeof(LayerMask)) return (LayerMask)I(t, "m_Bits");
        if (type == typeof(Bounds)) return new Bounds(V3(V(t, "m_Center")), V3(V(t, "m_Extent")) * 2f);
        if (type == typeof(Matrix4x4)) { var m = new Matrix4x4(); for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) m[r, c] = Fl(t, $"e{r}{c}"); return m; }
        if (type == typeof(RectOffset)) return new RectOffset(I(t, "m_Left"), I(t, "m_Right"), I(t, "m_Top"), I(t, "m_Bottom"));
        if (type == typeof(AnimationCurve))
        {
            var keys = (V(t, "m_Curve") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>().Select(k =>
            {
                var kf = new Keyframe(Fl(k, "time"), Fl(k, "value"), Fl(k, "inSlope"), Fl(k, "outSlope"));
                kf.weightedMode = (WeightedMode)I(k, "weightedMode"); kf.inWeight = Fl(k, "inWeight", 1f / 3); kf.outWeight = Fl(k, "outWeight", 1f / 3); return kf;
            }).ToArray();
            var curve = new AnimationCurve(keys); curve.preWrapMode = WrapFrom(I(t, "m_PreInfinity", 2)); curve.postWrapMode = WrapFrom(I(t, "m_PostInfinity", 2)); return curve;
        }
        if (type == typeof(Gradient))
        {
            var g = new Gradient(); int nc = I(t, "m_NumColorKeys", 2), na = I(t, "m_NumAlphaKeys", 2);
            var ck = Enumerable.Range(0, Math.Clamp(nc, 0, 8)).Select(i => new GradientColorKey(Col(V(t, $"key{i}") as TypeTreeObject), I(t, $"ctime{i}") / 65535f)).ToArray();
            var ak = Enumerable.Range(0, Math.Clamp(na, 0, 8)).Select(i => new GradientAlphaKey(Col(V(t, $"key{i}") as TypeTreeObject).a, I(t, $"atime{i}") / 65535f)).ToArray();
            g.SetKeys(ck, ak); g.mode = (GradientMode)I(t, "m_Mode"); return g;
        }
        if (type.IsAbstract || type.IsInterface || typeof(UObject).IsAssignableFrom(type)) return null;
        object obj;
        try { obj = type.IsValueType ? Activator.CreateInstance(type) : (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } ctor ? ctor.Invoke(null) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type)); }
        catch { obj = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type); }
        foreach (var fi in SerRules.Fields(type))
            if (t.TryGet(fi.Name, out var fv)) { try { fi.SetValue(obj, ToManaged(fv, fi.FieldType, f, depth + 1)); } catch { } }
        if (obj is ISerializationCallbackReceiver scr) { try { scr.OnAfterDeserialize(); } catch { } }
        return obj;
    }
    static WrapMode WrapFrom(int i) => i switch { 0 => WrapMode.PingPong, 1 => WrapMode.Loop, _ => WrapMode.ClampForever };

    // ---------------- MonoBehaviour population WITHOUT a type tree (stripped files) ----------------
    // Unity's own fallback: the binary layout follows the C# class (base fields first, declaration order).
    static bool Trace;
    static void PopulateByReflection(object target, Type type, SerializedFile f, ObjectInfo o)
    {
        Trace = Environment.GetEnvironmentVariable("SHEPHERD_TRACE") == type.Name;
        var r = new BinReader(f.GetObjectMemory(o));
        try
        {
            r.PPtr(); r.U8(); r.Align(); r.PPtr(); r.Str();   // m_GameObject, m_Enabled, m_Script, m_Name
            foreach (var fi in SerRules.Fields(type)) { if (Trace) Log.Info($"  @{r.Pos,6} {fi.DeclaringType.Name}.{fi.Name} : {fi.FieldType.Name}"); fi.SetValue(target, ReadField(ref r, fi.FieldType, f, 0)); }
        }
        catch (Exception e) { Log.Warn($"reflection-read {type.Name} stopped at byte {r.Pos}/{r.Len}: {e.Message}"); }
    }
    static object ReadField(ref BinReader r, Type t, SerializedFile f, int depth, bool align = true)
    {
        if (t.IsEnum) { var u = Enum.GetUnderlyingType(t); var raw = ReadPrim(ref r, u, align); return Enum.ToObject(t, raw); }
        if (t.IsPrimitive) return ReadPrim(ref r, t, align);
        if (t == typeof(string)) return r.Str();
        if (typeof(UObject).IsAssignableFrom(t)) { var p = r.PPtr(); var m = Managed(f, p); return m != null && t.IsInstanceOfType(m) ? m : null; }
        if (t == typeof(Vector2)) return new Vector2(r.F(), r.F());
        if (t == typeof(Vector3)) return new Vector3(r.F(), r.F(), r.F());
        if (t == typeof(Vector4)) return new Vector4(r.F(), r.F(), r.F(), r.F());
        if (t == typeof(Quaternion)) return new Quaternion(r.F(), r.F(), r.F(), r.F());
        if (t == typeof(Color)) return new Color(r.F(), r.F(), r.F(), r.F());
        if (t == typeof(Color32)) { uint u = r.U32(); return new Color32((byte)u, (byte)(u >> 8), (byte)(u >> 16), (byte)(u >> 24)); }
        if (t == typeof(Rect)) return new Rect(r.F(), r.F(), r.F(), r.F());
        if (t == typeof(LayerMask)) return (LayerMask)(int)r.U32();
        if (t == typeof(Vector2Int)) return new Vector2Int(r.I32(), r.I32());
        if (t == typeof(Vector3Int)) return new Vector3Int(r.I32(), r.I32(), r.I32());
        if (t == typeof(Bounds)) return new Bounds(new Vector3(r.F(), r.F(), r.F()), new Vector3(r.F(), r.F(), r.F()) * 2f);
        if (t == typeof(RectOffset)) return new RectOffset(r.I32(), r.I32(), r.I32(), r.I32());
        if (t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var et = t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0]; int n = r.I32();
            if (n < 0 || n > 1_000_000) throw new InvalidDataException($"bad array size {n}");
            if (et == typeof(byte)) { var bytes = r.Raw(n); r.Align(); if (t.IsArray) return bytes; var bl = (IList)Activator.CreateInstance(t); foreach (var b in bytes) bl.Add(b); return bl; }
            var items = new object[n]; for (int i = 0; i < n; i++) items[i] = ReadField(ref r, et, f, depth + 1, align: false);
            r.Align();
            if (t.IsArray) { var arr = Array.CreateInstance(et, n); for (int i = 0; i < n; i++) arr.SetValue(items[i], i); return arr; }
            var list = (IList)Activator.CreateInstance(t); foreach (var x in items) list.Add(x); return list;
        }
        if (t == typeof(AnimationCurve) || t == typeof(Gradient)) throw new NotSupportedException($"{t.Name} in stripped data");
        if (depth > 10) throw new InvalidDataException("nesting too deep");
        if (Trace && depth < 3) Log.Info($"  {new string(' ', depth * 2)}@{r.Pos,6} struct {t.Name}: {string.Join(", ", SerRules.Fields(t).Select(x => x.FieldType.Name + " " + x.Name))}");
        var obj = t.IsValueType ? Activator.CreateInstance(t) : (t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)?.Invoke(null) ?? System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t));
        foreach (var fi in SerRules.Fields(t)) fi.SetValue(obj, ReadField(ref r, fi.FieldType, f, depth + 1));
        return obj;
    }
    static object ReadPrim(ref BinReader r, Type t, bool align = true)
    {
        if (t == typeof(bool)) { var b = r.U8() != 0; if (align) r.Align(); return b; }
        if (t == typeof(byte)) { var x = r.U8(); if (align) r.Align(); return x; }
        if (t == typeof(sbyte)) { var x = (sbyte)r.U8(); if (align) r.Align(); return x; }
        if (t == typeof(short)) { var x = (short)r.U16(); if (align) r.Align(); return x; }
        if (t == typeof(ushort)) { var x = r.U16(); if (align) r.Align(); return x; }
        if (t == typeof(char)) { var x = (char)r.U16(); if (align) r.Align(); return x; }
        if (t == typeof(int)) return r.I32(); if (t == typeof(uint)) return r.U32(); if (t == typeof(float)) return r.F();
        if (t == typeof(long)) return (long)r.U64(); if (t == typeof(ulong)) return r.U64(); if (t == typeof(double)) return BitConverter.Int64BitsToDouble((long)r.U64());
        throw new NotSupportedException(t.Name);
    }
    struct BinReader
    {
        // the object's bytes in place (an array + its range: no copy); Pos is relative to the object start (Unity aligns
        // relative to the object)
        readonly byte[] _b; readonly int _base, _len; public int Pos; public int Len => _len;
        public BinReader(byte[] b) { _b = b; _base = 0; _len = b.Length; Pos = 0; }
        public BinReader(ReadOnlyMemory<byte> m)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(m, out var seg)) { _b = seg.Array; _base = seg.Offset; _len = seg.Count; }
            else { _b = m.ToArray(); _base = 0; _len = _b.Length; }
            Pos = 0;
        }
        void Need(int n) { if (Pos + n > _len) throw new EndOfStreamException(); }
        public byte U8() { Need(1); return _b[_base + Pos++]; }
        public byte[] Raw(int n) { Need(n); var r = _b.AsSpan(_base + Pos, n).ToArray(); Pos += n; return r; }
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_b, _base + Pos); Pos += 2; return v; }
        public int I32() { Need(4); var v = BitConverter.ToInt32(_b, _base + Pos); Pos += 4; return v; }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(_b, _base + Pos); Pos += 4; return v; }
        public ulong U64() { Need(8); var v = BitConverter.ToUInt64(_b, _base + Pos); Pos += 8; return v; }
        public float F() { Need(4); var v = BitConverter.ToSingle(_b, _base + Pos); Pos += 4; return v; }
        public void Align() => Pos = (Pos + 3) & ~3;
        public string Str() { int n = I32(); if (n < 0 || Pos + n > _len) throw new InvalidDataException("bad string"); var s = Shepherd.Assets.StringPool.Get(new ReadOnlySpan<byte>(_b, _base + Pos, n), Shepherd.Assets.StringPool.Site.Bin); Pos += n; Align(); return s; }   // kit #13 basec: pooled (SHEPHERD_K13_STRPOOL=0: a new copy)
        public UPPtr PPtr() { int fid = I32(); long pid = (long)U64(); return new UPPtr(fid, pid); }
    }

    // ---------------- assets ----------------
    static NObj CreateTexture(SerializedFile f, ObjectInfo o)
    {
        var d = Read(f, o);
        var ts = V(d, "m_TextureSettings") as TypeTreeObject;
        var tex = Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(Texture2D)), new NTexture
        {
            Name = S(d, "m_Name"), Width = I(d, "m_Width"), Height = I(d, "m_Height"), Format = I(d, "m_TextureFormat"), FilterMode = ts != null ? I(ts, "m_FilterMode", 1) : 1,
            WrapMode = Render.K11Gpu.WatercolorUi && ts != null ? I(ts, Render.K11Gpu.WrapUKey, 0) : 0,   // kit #11 gpu2: read only for the opt-in owner-local UI programs (their samplers' addressing); 0 = kit #10 (the key is a field of the [ProfileOnly] K11Gpu: no literal here, the diet's key scan is kit #10's)
            Src = TextureDecoder.Source(d)
        }, true));
        tex.SetSource(d);
        return tex;
    }

    /// <summary>First-touch eager decode (the original behaviour; with the diet only for SHEPHERD_KEEP_RGBA=1, 0-sized and
    /// unsupported-format textures). Same decoder call as before, fed from the texture's source record instead of its kept tree.</summary>
    static bool DecodePixels(NTexture t)
    {
        if (t.Src == null || t.SourceFile is not SerializedFile f) return false;
        try
        {
            var (w, h, rgba, _) = TextureDecoder.DecodeTexture2D(Db, f, t.Src);
            t.Width = w; t.Height = h; t.Rgba = rgba; t.Version++;
            if (rgba.Length > 0) { Diet.TexDecodes++; Diet.TexDecodedBytes += rgba.LongLength; t.Decodes++; }
            return true;
        }
        catch (Exception e) { Log.Warn($"decode texture '{t.Name}': {(e.InnerException ?? e).Message}"); return false; }
    }

    static NObj CreateSprite(SerializedFile f, ObjectInfo o)
    {
        var d = Read(f, o);
        var sp = Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(Sprite)), new NSprite(), true));
        sp.SetSource(d); sp.Name = S(d, "m_Name");
        var rect = V(d, "m_Rect") as TypeTreeObject; sp.Rect = new Rect(Fl(rect, "x"), Fl(rect, "y"), Fl(rect, "width"), Fl(rect, "height"));
        sp.Pivot = V2(V(d, "m_Pivot"), new Vector2(0.5f, 0.5f)); sp.PixelsPerUnit = Fl(d, "m_PixelsToUnits", 100f);
        var b = V(d, "m_Border") as TypeTreeObject; sp.Border = new Vector4(Fl(b, "x"), Fl(b, "y"), Fl(b, "z"), Fl(b, "w"));
        var rd = V(d, "m_RD") as TypeTreeObject;
        // atlas-packed sprites: the real render data lives in the SpriteAtlas, keyed by m_RenderDataKey
        if (V(d, "m_SpriteAtlas") is UPPtr ap && !ap.IsNull)
        {
            try
            {
                var (af, atlas) = AtlasTree(f, ap);
                var key = V(d, "m_RenderDataKey") as TypeTreeObject;
                if (atlas != null && V(atlas, "m_RenderDataMap") is IList map)
                {
                    if (TreeIdx.On && AtlasIndexOf(atlas, map) is { } ix && AtlasKey(key, ix.GuidKeys) is { } ak)
                    {   // kit #10: the atlas's render-data map indexed once (the same first matching entry: KeyEq below)
                        if (ix.Map.TryGetValue(ak, out var hitE)) { rd = V(hitE, "second") as TypeTreeObject; f = af; }
                        _atlasIndexHits++;
                    }
                    else
                        foreach (var e in map.OfType<TypeTreeObject>())
                            if (KeyEq(V(e, "first") as TypeTreeObject, key)) { rd = V(e, "second") as TypeTreeObject; f = af; break; }
                }
            }
            catch (Exception e) { Log.Warn($"sprite atlas for '{sp.Name}': {e.Message}"); }
        }
        if (rd != null)
        {
            sp.Texture = V(rd, "texture") is UPPtr tp ? Get(f, tp) as NTexture : null;
            var tr = V(rd, "textureRect") as TypeTreeObject; sp.TextureRect = new Rect(Fl(tr, "x"), Fl(tr, "y"), Fl(tr, "width"), Fl(tr, "height"));
            sp.TextureRectOffset = V2(V(rd, "textureRectOffset"));
            uint settings = V(rd, "settingsRaw") is { } sr ? Convert.ToUInt32(sr) : 0; sp.Packed = (settings & 1) != 0; sp.PackingRotation = (int)((settings >> 2) & 0xF);
        }
        if (sp.TextureRect.width <= 0) sp.TextureRect = sp.Rect;
        return sp;
    }
    /// <summary>The SpriteAtlas a sprite's m_SpriteAtlas names, decoded once for a run of sprites of the same atlas (a scene
    /// creates its atlas sprites one after another: before, every sprite decoded the whole atlas tree with its render-data
    /// map of all sprites -- 105 MiB of garbage for the Main Menu's 320 atlas sprites). Read-only use, same tree content, so
    /// the sprites get exactly what they got before. Dropped at the end of every scene instantiation and every frame.</summary>
    static (SerializedFile af, long pathId, TypeTreeObject tree)[] _atlasCache = new (SerializedFile, long, TypeTreeObject)[4];
    static int _atlasNext;
    static (SerializedFile file, TypeTreeObject tree) AtlasTree(SerializedFile from, UPPtr ap)
    {
        var (af, ao) = Db.Resolve(from, ap);
#if !SHEPHERD_K11_TOC
        if (ao.IsNull) return (af, null);
#else
        if (ao == null) return (af, null);
#endif
        if (VitaMemory.AtlasCache) foreach (var c in _atlasCache) if (c.tree != null && c.af == af && c.pathId == ao.PathId) return (af, c.tree);
        var (rf, ro, atlas) = Db.ResolveRead(from, ap);
        if (atlas != null) { _atlasCache[_atlasNext] = (rf, ro.PathId, atlas); _atlasNext = (_atlasNext + 1) % _atlasCache.Length; }
        return (rf, atlas);
    }
    public static void DropTransientCaches() { if (_atlasCache[0].tree != null || _atlasCache[1].tree != null || _atlasCache[2].tree != null || _atlasCache[3].tree != null) Array.Clear(_atlasCache); _atlasIndex = default; }

    /// <summary>Kit #10 E3 (create programs): an atlas sprite's render data was found by walking the atlas's m_RenderDataMap with KeyEq for every
    /// sprite (a run of N sprites of one atlas: ~N^2/2 KeyEq calls, each a LINQ walk). The map is indexed once per atlas tree -- the entry kept
    /// for a key is the FIRST entry KeyEq would match -- when KeyEq reduces to plain value equality: every entry's key is (GUID struct, integer),
    /// the GUID structs share one key list (a pack shape) of integer values (no list, no float, no "data" key), and the sprite's own key has
    /// that same key list; then KeyEq(entry, key) holds iff each GUID value Equals the other (same type, same value) and the integers agree,
    /// i.e. iff their canonical strings are equal. Anything else walks the map as before. Dropped with the atlas cache.</summary>
    sealed class AtlasIndex { public TypeTreeObject Atlas; public object GuidKeys; public Dictionary<string, TypeTreeObject> Map; public bool Bad; }
    static AtlasIndex _atlasIndex; static long _atlasIndexHits, _atlasIndexBuilt;
    public static string CreateSummary() => !TreeIdx.On ? null : $"[k10] create programs: {_scriptTypes.Count} script types kept, {_typeKinds.Count} type kinds; {_atlasIndexHits} atlas sprites found through {_atlasIndexBuilt} atlas indexes; shader places {(_shaderPos != null ? _shaderPos.Count.ToString() : "unused")}";
    static AtlasIndex AtlasIndexOf(TypeTreeObject atlas, IList map)
    {
        if (_atlasIndex != null && ReferenceEquals(_atlasIndex.Atlas, atlas)) return _atlasIndex.Bad ? null : _atlasIndex;
        var ix = new AtlasIndex { Atlas = atlas, Map = new Dictionary<string, TypeTreeObject>(StringComparer.Ordinal) };
        _atlasIndex = ix;
        foreach (var e in map.OfType<TypeTreeObject>())
        {
            if (V(e, "first") is not TypeTreeObject a) continue;   // (KeyEq: false for every key)
            if (V(a, "first") is not TypeTreeObject g) { ix.Bad = true; break; }
            if (ix.GuidKeys == null) { ix.GuidKeys = g.Keys; if (g.Keys.Distinct().Count() != g.Keys.Count) { ix.Bad = true; break; } }   // (a repeated key: KeyEq reads its first value only)
            else if (!ReferenceEquals(ix.GuidKeys, g.Keys)) { ix.Bad = true; break; }
            var k = AtlasKey(a, ix.GuidKeys);
            if (k == null) { ix.Bad = true; break; }
            ix.Map.TryAdd(k, e);   // (the first entry of a key: the walk's first match)
        }
        if (ix.GuidKeys == null) ix.Bad = true;
        _atlasIndexBuilt++;
        return ix.Bad ? null : ix;
    }
    /// <summary>A render-data key (GUID struct, integer) as a string that is equal iff KeyEq holds (see AtlasIndex), or null if the key is not of that form.</summary>
    static string AtlasKey(TypeTreeObject key, object guidKeys)
    {
        if (key == null || V(key, "first") is not TypeTreeObject g || !ReferenceEquals(g.Keys, guidKeys)) return null;
        var sb = new System.Text.StringBuilder(48);
        for (int i = 0; i < g.Keys.Count; i++)
        {
            if (g.Keys[i] == "data") return null;
            var v = g.Values[i];
            switch (v)
            {
                case uint u: sb.Append('u').Append(u); break; case int x: sb.Append('i').Append(x); break; case ulong ul: sb.Append('U').Append(ul); break;
                case long l: sb.Append('l').Append(l); break; case ushort us: sb.Append('s').Append(us); break; case short sh: sb.Append('h').Append(sh); break;
                case byte b: sb.Append('b').Append(b); break; case sbyte sb8: sb.Append('y').Append(sb8); break; case null: sb.Append('n'); break;
                default: return null;
            }
            sb.Append(',');
        }
        var sec = V(key, "second");
        long sv; switch (sec) { case null: sv = 0; break; case long l2: sv = l2; break; case int i2: sv = i2; break; case uint u2: sv = u2; break; case ulong u3 when u3 <= long.MaxValue: sv = (long)u3; break; default: return null; }
        return sb.Append('|').Append(sv).ToString();
    }

    static bool KeyEq(TypeTreeObject a, TypeTreeObject b)
    {
        if (a == null || b == null) return false;
        return Same(V(a, "first"), V(b, "first")) && Equals(Convert.ToInt64(V(a, "second") ?? 0L), Convert.ToInt64(V(b, "second") ?? 0L));
        static bool Same(object x, object y) => x is TypeTreeObject tx && y is TypeTreeObject ty ? tx.Keys.All(k => Equals(Norm(V(tx, k)), Norm(V(ty, k)))) && SeqEq(V(tx, "data"), V(ty, "data")) : Equals(x, y);
        static object Norm(object v) => v is IList ? null : v;
        static bool SeqEq(object x, object y) => x is IList lx && y is IList ly ? lx.Cast<object>().Select(Convert.ToInt64).SequenceEqual(ly.Cast<object>().Select(Convert.ToInt64)) : true;
    }

    static NObj CreateMaterial(SerializedFile f, ObjectInfo o)
    {
        var d = Read(f, o);
        var m = Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(Material)), new NMaterial { Name = S(d, "m_Name") }, true)); m.SetSource(d);
        m.Shader = V(d, "m_Shader") is UPPtr sp ? Get(f, sp) as NShader : null; m.Shader ??= RenderIcalls.ShaderNamed("Hidden/Unknown");
        m.RenderQueue = I(d, "m_CustomRenderQueue", -1);
        if (V(d, "m_SavedProperties") is TypeTreeObject props)
        {
            foreach (var e in (V(props, "m_TexEnvs") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>())
            {
                var id = ShaderIds.Id(V(e, "first") as string); var te = V(e, "second") as TypeTreeObject;
                if (V(te, "m_Texture") is UPPtr tp && !tp.IsNull) m.P[id] = Get(f, tp)?.Managed;
                var sc = V2(V(te, "m_Scale"), Vector2.one); var of = V2(V(te, "m_Offset")); m.P[-id] = new Vector4(sc.x, sc.y, of.x, of.y);
            }
            foreach (var e in (V(props, "m_Floats") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>()) m.P[ShaderIds.Id(V(e, "first") as string)] = Convert.ToSingle(V(e, "second"));
            foreach (var e in (V(props, "m_Ints") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>()) m.P[ShaderIds.Id(V(e, "first") as string)] = Convert.ToInt32(V(e, "second"));
            foreach (var e in (V(props, "m_Colors") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>())
            {
                var cid = ShaderIds.Id(V(e, "first") as string);
                if (Render.K13Looks.Shadow && m.P.TryGetValue(cid, out var hid) && hid is float hf) m.P[cid | Render.K13Looks.FloatShadow] = hf;   // kit #13 looks: the float this colour hides (off: no test)
                m.P[cid] = Col(V(e, "second") as TypeTreeObject);
            }
        }
        foreach (var k in (V(d, "m_ValidKeywords") as IList ?? Array.Empty<object>()).OfType<string>()) m.Keywords.Add(k);
        foreach (var k in (S(d, "m_ShaderKeywords")).Split(' ', StringSplitOptions.RemoveEmptyEntries)) m.Keywords.Add(k);
        return m;
    }

    static NObj CreateShader(SerializedFile f, ObjectInfo o)
    {
        string name = "Hidden/Unknown"; Render.ShaderPolicy.State state = null;
        try { var d = Read(f, o); var pf = V(d, "m_ParsedForm") as TypeTreeObject; name = pf != null ? S(pf, "m_Name") : S(d, "m_Name"); state = Render.ShaderPolicy.Parse(pf, name); } catch { }   // polish round 2: + the render state
        if (TreeIdx.On && LiveIndex)
        {   // kit #10: the same first shader of the name, from the shader places (above)
            NShader hit = null;
            foreach (var pos in ShaderPositions()) if (_liveOrder[pos - 1] is NShader s0 && s0.ShaderName == name) { hit = s0; break; }
            if (hit != null) { LiveSet(f, o, hit); hit.State ??= state; LiveEpoch++; if (LoadDump.On) LoadDump.Aliased(f, o, hit); return hit; }   // (the dump: an alias line, kit #10 review)
        }
        else foreach (var n in LiveRecords) if (n is NShader s && s.ShaderName == name) { LiveSet(f, o, s); s.State ??= state; LiveEpoch++; if (LoadDump.On) LoadDump.Aliased(f, o, s); return s; }
        return Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(Shader)), new NShader { Name = name, ShaderName = name, State = state }, true));
    }

    static NObj CreateTextAsset(SerializedFile f, ObjectInfo o)
    {
        // read raw: the m_Script "string" is really bytes (Spine .skel.bytes is binary) — never round-trip through UTF-8
        // (parsed in place: the only copy is the kept bytes; a 1 MiB TextAsset used to be copied 3-4 times on the way. From a
        // pack, the name comes from a small head read and the bytes stay in the pack until someone reads them.)
        string name; byte[] bytes;
        if (f.Pack != null && o.ByteSize > 4096 && VitaMemory.LazyText)
        {
            var head = f.Pack.ReadRawRange(f, o, 0, 4096); int p = 0;
            int nl = BitConverter.ToInt32(head, p); p += 4;
            if (nl < 0 || p + nl + 4 > head.Length) { var all = f.GetObjectBytes(o); (name, bytes) = TextAssetFields(all); }
            else
            {
                name = System.Text.Encoding.UTF8.GetString(head, p, nl); p += nl; p = (p + 3) & ~3;
                int sl = BitConverter.ToInt32(head, p); p += 4;
                // a pack TextAsset keeps no bytes: the few readers (TextAsset.bytes / .text: 11 calls in the menu boot, Spine's
                // skeleton data, read once) get them from the pack on each call (PackAssets.PackBytes), 8.6 MiB less live heap
                var tl = Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(TextAsset)), new NObj { Name = name }, true));
                tl.Set("__bytes", new PackAssets.PackBytes(f, o, p, sl)); return tl;
            }
        }
        else (name, bytes) = TextAssetFields(f.GetObjectBytes(o));
        var t = Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(TextAsset)), new NObj { Name = name }, true));
        t.Set("__bytes", bytes); return t;
    }
    static (string name, byte[] bytes) TextAssetFields(ReadOnlySpan<byte> raw)
    {
        int p = 0;
        int nl = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw[p..]); p += 4; var name = System.Text.Encoding.UTF8.GetString(raw.Slice(p, nl)); p += nl; p = (p + 3) & ~3;
        int sl = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw[p..]); p += 4;
        return (name, raw.Slice(p, Math.Min(sl, raw.Length - p)).ToArray());
    }

    static NObj CreateMesh(SerializedFile f, ObjectInfo o)
    {
        var d = Read(f, o);
        var m = Reg(f, o, Objects.Bind(Objects.NewManaged(typeof(Mesh)), new NMesh { Name = S(d, "m_Name") }, true)); m.SetSource(d);
        try { MeshDecode.Fill(m, d, f); } catch (Exception e) { Log.Warn($"mesh '{m.Name}': {e.Message}"); }
        return m;
    }

    // ---------------- scenes ----------------
    public static int LoadScene(string key, bool activate = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AssetDatabase.SceneLoadResult res = Db.LoadScene(key, eager: true);
        return Instantiate(res.Scene, Path.GetFileNameWithoutExtension(key), key, activate, sw);
    }
    public static int InstantiateSceneFile(SerializedFile f, string name, string path, bool activate) => Instantiate(f, name, path, activate, System.Diagnostics.Stopwatch.StartNew());
    public static void ActivateScene(int handle)
    {
        if (!Scenes.All.TryGetValue(handle, out var scene)) return;
        Runtime.Time.LevelLoadAt = Runtime.Time.Now;
        if (LoadProf.On)
        {
            LoadProf.Enter(LoadProf.Activate);
            try { foreach (var r in scene.Roots.ToArray()) if (r.ActiveSelf && !r.Destroyed) Lifecycle.BecameActive(r); }
            finally { LoadProf.Exit(LoadProf.Activate); LoadProf.SceneDone(scene.Name + " (activated)"); }
        }
        else foreach (var r in scene.Roots.ToArray()) if (r.ActiveSelf && !r.Destroyed) Lifecycle.BecameActive(r);
    }
    public static int LoadLevel0(bool activate = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = Db.GetFile("level0");
        return Instantiate(f, "level0", "level0", activate, sw, buildIndex: 0);
    }
    static int Instantiate(SerializedFile file, string name, string path, bool activate, System.Diagnostics.Stopwatch sw, int buildIndex = -1)
    {
        if (!LoadProf.On) return InstantiateCore(file, name, path, activate, sw, buildIndex);
        LoadProf.Enter(LoadProf.Scene);
        try { return InstantiateCore(file, name, path, activate, sw, buildIndex); }
        finally { LoadProf.Exit(LoadProf.Scene); LoadProf.SceneDone(name); }
    }
    static int InstantiateCore(SerializedFile file, string name, string path, bool activate, System.Diagnostics.Stopwatch sw, int buildIndex)
    {
#if !SHEPHERD_NO_K13WALKC
        Runtime.K13Unload.NoteSceneFile(file);   // (kit #13 walkc: a scene file's records are never released as assets)
#endif
        if (file?.Pack != null) file = PackAssets.SceneFile(Db, file);   // V3 pack: a scene file the pack holds only partly is a loud miss
#if !SHEPHERD_NO_K13WALKC
        Runtime.K13Unload.NoteSceneFile(file);
#endif
        if (K6Engine.SceneReload && file != null && !_sceneFilesLoaded.Add(file.Name)) RetireSceneRecords(file, name);   // kit #6 (b): a reload gets new objects
        var scene = Scenes.Create(name, path); scene.BuildIndex = buildIndex; _loadingFile = file;
        if (Scenes.All.TryGetValue(1, out var boot) && boot.Name == "Shepherd" && boot.Roots.Count == 0 && Scenes.ActiveHandle == 1) Scenes.All.Remove(1);
        LoadingScene = scene.Handle;
        var before = Created;
        _depth++;
        try { foreach (var o in file.Objects) if (o.ClassId == 1) Get(file, o); }
        finally { _depth--; }
        Diag.SimStats.ScenePoint(name, 0);
        Drain();
        if (LoadDump.On) LoadDump.Point("scene " + name);
        DropTransientCaches();
        Diag.SimStats.ScenePoint(name, 1);
        LoadingScene = 0; _loadingFile = null;
        var gos = LiveRecords.OfType<NGameObject>().Where(g => g.Scene == scene.Handle).ToList();
        foreach (var g in gos) if (g.Transform.Parent == null) scene.Roots.Add(g);
        Log.Info($"scene '{name}': {gos.Count} GameObjects, {Created - before} objects created, {scene.Roots.Count} roots ({sw.ElapsedMilliseconds} ms)");
        if (activate)
        {
            Scenes.ActiveHandle = scene.Handle;
            Runtime.Time.LevelLoadAt = Runtime.Time.Now;
            if (LoadProf.On)
            {
                LoadProf.Enter(LoadProf.Activate);
                try { foreach (var r in scene.Roots.ToArray()) if (r.ActiveSelf) Lifecycle.BecameActive(r); }
                finally { LoadProf.Exit(LoadProf.Activate); }
            }
            else foreach (var r in scene.Roots.ToArray()) if (r.ActiveSelf) Lifecycle.BecameActive(r);
        }
        return scene.Handle;
    }

    /// <summary>Kit #6 (b): the scene files instantiated so far (by file name).</summary>
    static readonly HashSet<string> _sceneFilesLoaded = new();
    /// <summary>Kit #6 (b): a scene file is being instantiated again. Unity creates every object of a scene anew on each load; the loader
    /// found the previous load's records under (file, path id) and handed them back (destroyed with their scene, or alive in
    /// DontDestroyOnLoad), so the scene came up empty. Those records leave the table here (they stay what they are: destroyed ones stay
    /// destroyed, a surviving DontDestroyOnLoad root keeps its own objects), and this load creates new ones.</summary>
    static void RetireSceneRecords(SerializedFile file, string name)
    {
        int n = 0, alive = 0;
        foreach (var o in file.Objects)   // (the file's own table: a record of this file sits under one of its path ids)
            if (LiveRemove(file, o, out var r)) { n++; if (!r.Destroyed) alive++; }   // (kit #6 integration: through the mem track's live index)
        LiveEpoch++;
        K6Engine.SceneRetired(name, n, alive);
    }

    static void LoadScriptOrders()
    {
        try
        {
            var f = Db.GetFile("globalgamemanagers.assets"); int n = 0;
            foreach (var o in f.ObjectsOfClass(115))
            {
                var t = TypeTreeReader.Read(f, o); n++;
                if (!t.TryGet("m_ExecutionOrder", out var ov) || Convert.ToInt32(ov) == 0) continue;
                var ns = t.Str("m_Namespace"); var cls = t.Str("m_ClassName");
                ScriptOrders[string.IsNullOrEmpty(ns) ? cls : ns + "." + cls] = Convert.ToInt32(ov);
            }
            Log.Info($"assets: {n} MonoScripts, {ScriptOrders.Count} with a custom execution order: " + string.Join(", ", ScriptOrders.OrderBy(k => k.Value).Take(12).Select(k => $"{k.Key}={k.Value}")));
        }
        catch (Exception e) { Log.Warn($"script orders: {e.Message}"); }
    }

    // ---------------- tags / sorting layers (TagManager in globalgamemanagers, stripped -> parse by hand) ----------------
    static void LoadTagManager()
    {
        try
        {
#if !SHEPHERD_K11_TOC
            var gm = Db.GetFile("globalgamemanagers"); var o = gm.ObjectsOfClass(78).FirstOrDefault(); if (o.IsNull) return;
#else
            var gm = Db.GetFile("globalgamemanagers"); var o = gm.ObjectsOfClass(78).FirstOrDefault(); if (o == null) return;
#endif
            var r = new BinReader(gm.GetObjectBytes(o).ToArray());
            int n = r.I32(); for (int i = 0; i < n; i++) CustomTags.Add(r.Str());
            int nl = r.I32(); for (int i = 0; i < nl; i++) LayerNames.Add(r.Str());
            int ns = r.I32(); int start = r.Pos;
            for (int i = 0; i < ns; i++) { var nm = r.Str(); var id = r.U32(); SortingLayers.Add((nm, id)); }   // 2022.3: (name, uniqueID) — no 'locked'
            _ = start;
        }
        catch (Exception e) { Log.Warn($"TagManager: {e.Message}"); }
        LoadResourceContainer();
    }

    // Resources.Load table: ResourceManager (class 147) m_Container = vector<pair<string path, PPtr<Object>>>
    public static readonly List<(string path, UPPtr ptr)> ResourcePaths = new(); static SerializedFile _gm;
    static void LoadResourceContainer()
    {
        try
        {
#if !SHEPHERD_K11_TOC
            _gm = Db.GetFile("globalgamemanagers"); var o = _gm.ObjectsOfClass(147).FirstOrDefault(); if (o.IsNull) return;
#else
            _gm = Db.GetFile("globalgamemanagers"); var o = _gm.ObjectsOfClass(147).FirstOrDefault(); if (o == null) return;
#endif
            var r = new BinReader(_gm.GetObjectBytes(o).ToArray()); int n = r.I32();
            for (int i = 0; i < n; i++) { var path = r.Str(); var p = r.PPtr(); ResourcePaths.Add((path.ToLowerInvariant(), p)); }
        }
        catch (Exception e) { Log.Warn($"ResourceManager: {e.Message} (parsed {ResourcePaths.Count})"); }
    }
    public static UObject ResourceLoad(string path, Type type)
    {
        path = (path ?? "").Replace('\\', '/').Trim('/').ToLowerInvariant();
        // kit3 (sub-menus): the same entries in the same order (every entry whose path equals, in table order), found by a binary search
        // instead of a walk of the whole table per call: I2 asks Resources.Load for its font overrides once per localized text, ~1,500
        // misses when the Settings screen closes (90 ms of walking on the Mac interpreter, seconds on the Vita)
        var o = ResOrder(); int lo = 0, hi = o.Length;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (string.CompareOrdinal(ResourcePaths[o[mid]].path, path) < 0) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < o.Length && ResourcePaths[o[i]].path == path; i++) { var m = AsType(Managed(_gm, ResourcePaths[o[i]].ptr), type); if (m != null) return m; }
        return null;
    }
    /// <summary>The indices of <see cref="ResourcePaths"/> ordered by path (ordinal), table order among equal paths; rebuilt if the table grew.</summary>
    static int[] _resOrder = Array.Empty<int>();
    static int[] ResOrder()
    {
        if (_resOrder.Length == ResourcePaths.Count) return _resOrder;
        var o = new int[ResourcePaths.Count]; for (int i = 0; i < o.Length; i++) o[i] = i;
        Array.Sort(o, (a, b) => { int c = string.CompareOrdinal(ResourcePaths[a].path, ResourcePaths[b].path); return c != 0 ? c : a.CompareTo(b); });
        return _resOrder = o;
    }
    public static UObject[] ResourceLoadAll(string path, Type type)
    {
        path = (path ?? "").Replace('\\', '/').Trim('/').ToLowerInvariant();
        return ResourcePaths.Where(e => path.Length == 0 || e.path == path || e.path.StartsWith(path + "/")).Select(e => AsType(Managed(_gm, e.ptr), type)).Where(m => m != null).ToArray();
    }
    /// <summary>Unity semantics: asking for a Component type on a prefab returns that component from the prefab root.</summary>
    public static UObject AsType(UObject m, Type type)
    {
        if (m == null) return null;
        if (type == null || type.IsInstanceOfType(m)) return m;
        if (m is GameObject go && (typeof(Component).IsAssignableFrom(type) || type.IsInterface)) return Impl.Core.First(Objects.Get<NGameObject>(go), type);
        return null;
    }
    static readonly string[] BuiltinTags = { "Untagged", "Respawn", "Finish", "EditorOnly", "", "MainCamera", "Player", "GameController" };
    public static string TagName(uint t) => t < BuiltinTags.Length ? BuiltinTags[t] : t >= 20000 && t - 20000 < CustomTags.Count ? CustomTags[(int)(t - 20000)] : "Untagged";
    public static float SortingValue(uint id) { for (int i = 0; i < SortingLayers.Count; i++) if (SortingLayers[i].id == id) return i; return 0; }

    // ---------------- struct helpers ----------------
    static Vector2 V2(object o, Vector2 d = default) => o is TypeTreeObject t ? new Vector2(Fl(t, "x"), Fl(t, "y")) : d;
    static Vector3 V3(object o, Vector3 d = default) => o is TypeTreeObject t ? new Vector3(Fl(t, "x"), Fl(t, "y"), Fl(t, "z")) : d;
    static Quaternion Q(object o) => o is TypeTreeObject t ? M.QNorm(new Quaternion(Fl(t, "x"), Fl(t, "y"), Fl(t, "z"), Fl(t, "w", 1))) : Quaternion.identity;
    static Color Col(TypeTreeObject t) => t == null ? Color.white : V(t, "rgba") is { } rgba ? (Color)new Color32((byte)Convert.ToUInt32(rgba), (byte)(Convert.ToUInt32(rgba) >> 8), (byte)(Convert.ToUInt32(rgba) >> 16), (byte)(Convert.ToUInt32(rgba) >> 24)) : new Color(Fl(t, "r"), Fl(t, "g"), Fl(t, "b"), Fl(t, "a", 1));
}
