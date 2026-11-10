using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Native;

/// <summary>Native-side record for every live UnityEngine.Object. The managed object's m_CachedPtr points at a real
/// block (kit6 mem L4d: 16 bytes; 32 before) whose int at +8 is the instance id (Unity's GetInstanceID reads it from there).</summary>
public class NObj
{
    // Device kit #11, track walk -- the engine records diet (vita/notes/kit11-walk.md): NObj is 56 bytes on the Vita (was 64), so NComponent
    // is 64 (72), NGameObject and NMono 80 (96: SGen's 96 class -> its 80 class) and NTransform 160 (200). 32-bit Mono's auto layout: the
    // references first, then the other fields in declaration order at their own alignment (PathId's long at an 8-aligned offset; the class
    // rounded to 8) -- vita/notes/kit10-memory.md section 4, kit #9's tools/fields. Two int / reference fields left the record: HideFlags (a
    // byte in the padding after DontDestroy -- review fix: no side table for the values Unity's HideFlags use; any other int in the side
    // table) and Data (the source TypeTree: only with SHEPHERD_KEEP_TREES=1, a test switch; side table). Both are properties with the same
    // values as the fields had (a record never written reads 0 / null). Any field added to NObj, NComponent, NBehaviour, NMono, NGameObject or
    // NTransform must be checked with tools/fields: one more word moves a record a whole size class up. The build switch back to kit #10's
    // layouts: vita/shepherd/build.sh SHEPHERD_K11_RECORDS=0 (-p:ShepherdK11Records=0, compile symbol SHEPHERD_K10_RECORDS) -- a field
    // layout is fixed when Mono loads the class, so it has no env-file switch.
#if SHEPHERD_K10_RECORDS
    public int InstanceId; public IntPtr Ptr; public UObject Managed; public int HideFlags;
#else
    public int InstanceId; public IntPtr Ptr; public UObject Managed;
    /// <summary>Object.hideFlags. Kit #11 walk: 0..254 in the record itself (<see cref="_hf"/>, a byte in NObj's padding: Unity's HideFlags
    /// bits make 0..63); any other int moves the record's value to its <see cref="Cold"/> for good (255 marks that). Reads exactly what was last
    /// written (0 before), as the field did.</summary>
    public int HideFlags
    {
        get => _hf != HfCold ? _hf : ColdOf.TryGetValue(this, out var c) ? c.HideFlags : 0;
        set { if (_hf != HfCold && (uint)value < HfCold) _hf = (byte)value; else { _hf = HfCold; MakeCold().HideFlags = value; } }
    }
#endif
    /// <summary>The object's name. menu-speed (top hotspots): a bound, live GameObject's name is counted in Objects' name filter (GameObject.Find's
    /// exact miss test), so a rename moves it there.</summary>
    public string Name { get => _name; set { if (Slot >= 0 && !Destroyed && this is NGameObject) { Objects.GoNameRemove(_name); Objects.GoNameAdd(value); } _name = value; } }
    string _name = "";
    public bool Destroyed; public bool DontDestroy;
#if !SHEPHERD_K10_RECORDS
    byte _hf;   // kit #11 walk: HideFlags (above) -- at offset 34, in the 2 bytes of padding before Slot: NObj stays 56 bytes
#endif
    /// <summary>Where this record sits in Objects.ById's enumeration order (Registry); -1 before Bind.</summary>
    public int Slot = -1; internal int RegMask;
    /// <summary>The generic property bag (auto get_/set_ icalls): null, or a <see cref="PropBag"/> shaped bag (object[]: slot 0 its shared
    /// <see cref="PropShape"/>, then the values in key order), or a Dictionary&lt;string, object&gt; (kit #5's bag: SHEPHERD_PROPS_SHAPED=0, or a
    /// bag after its first removal). Read and written only through Get / Set / RemoveProp / ReplaceProps / <see cref="PropBag"/>.</summary>
    public object Props;
#if SHEPHERD_K10_RECORDS
    public object Data;                                    // source asset data (TypeTreeObject) when loaded from disk — only kept with SHEPHERD_KEEP_TREES=1; read it through SourceTree()
#else
    /// <summary>Source asset data (TypeTreeObject) when loaded from disk -- only kept with SHEPHERD_KEEP_TREES=1; read it through SourceTree().
    /// Kit #11 walk: kept in <see cref="Cold"/> (null for every record that never got one).</summary>
    public object Data
    {
        get => _dataAny && ColdOf.TryGetValue(this, out var c) ? c.Data : null;
        set { if (_dataAny && ColdOf.TryGetValue(this, out var c)) c.Data = value; else if (value != null) { _dataAny = true; MakeCold().Data = value; } }
    }
#endif
    public bool HasSource;                                 // created from a source TypeTree (SourceFile + PathId name it), whether or not Data is kept
    public object SourceFile;                              // SerializedFile it came from
    public long PathId;
    /// <summary>Record the source TypeTree this record was created from (kept in Data only when the diet keeps trees).</summary>
    public void SetSource(object tree) { HasSource = tree != null; Data = Shepherd.Engine.Assets.Diet.KeepTrees ? tree : null; }
    /// <summary>Copy the source identity (tree, flag, file, path id) from another record (clones).</summary>
    public void CopySource(NObj o) { Data = o.Data; HasSource = o.HasSource; SourceFile = o.SourceFile; PathId = o.PathId; }
    /// <summary>The source TypeTree: the kept one, or re-read from SourceFile/PathId (null when the record has none).</summary>
    public Shepherd.Assets.TypeTreeObject SourceTree(bool transient = false) =>
        Data as Shepherd.Assets.TypeTreeObject ?? (HasSource ? Shepherd.Engine.Assets.Diet.Reread(this, transient) : null);
    public object Get(string k) => PropBag.Get(Props, k);
    // menu-speed (top hotspots): Set, RemoveProp and ReplaceProps are the only writers of Props (its values are immutable boxes and object
    // references: replaced, never written in place); on a renderer's record each bumps NRenderer.PropsVer
    public void Set(string k, object v) { Props = PropBag.Set(Props, k, v); if (this is NRenderer r) r.PropsVer++; }
    public bool RemoveProp(string k) { if (this is NRenderer r) r.PropsVer++; return PropBag.Remove(ref Props, k); }
    /// <summary>A copy of another record's bag (clones) or null; see <see cref="PropBag.Copy"/>.</summary>
    public void ReplaceProps(object bag) { Props = bag; if (this is NRenderer r) r.PropsVer++; }
    /// <summary>Loader.SeedProps: the keys and values in order, as that many Set calls would leave them (one array; PropsVer counted per key).</summary>
    public void SetMany(PropBag.Pending kv) { if (kv.Count == 0) return; Props = PropBag.SetMany(Props, kv); if (this is NRenderer r) r.PropsVer += kv.Count; }
    public override string ToString() => $"{GetType().Name}({Name}#{InstanceId})";

#if !SHEPHERD_K10_RECORDS
    // ---- kit #11 walk: the cold fields' side table (Data; HideFlags only for a value outside 0..254) ----
    /// <summary>A record's rarely set fields, made on its first write that needs it and kept for the record's life (a later write of the
    /// default is stored here too: each property reads exactly what was last written, as the field did). Weakly keyed: it goes with the record.
    /// Each entry is a weak-table handle (on Mono an ephemeron the GC walks at every collection): review fix -- HideFlags no longer makes one
    /// (the walk made 1,071 for it), so only SHEPHERD_KEEP_TREES=1 (a test switch) or an out-of-range hideFlags value does.</summary>
    internal sealed class Cold { public int HideFlags; public object Data; }
    static readonly ConditionalWeakTable<NObj, Cold> ColdOf = new();
    static volatile bool _dataAny;   // false until the first non-null Data: its getter answers null without a lookup
    const byte HfCold = 255;         // _hf's mark: this record's HideFlags is in its Cold
    Cold MakeCold() => ColdOf.GetValue(this, static _ => new Cold());
#endif
}

/// <summary>kit6 mem, lever L4a (vita/notes/kit6-mem.md): the property bags as shaped arrays. A record's bag was a Dictionary&lt;string, object&gt;
/// (on 32-bit Mono a 34-key SpriteRenderer bag: the dictionary object, 37 entries of 16 B and 37 buckets, ~820 B); the intro's 40,645 bags
/// held 21.7 MiB of dictionaries. A shaped bag is one object[]: slot 0 a <see cref="PropShape"/> -- the key sequence, shared by every bag that
/// got the same keys in the same order (all SpriteRenderers seeded from the same type tree) -- then the values (a 34-key bag: 156 B).
/// Exactly a Dictionary's behaviour for what the engine does with a bag: Get / Set (a new key goes last, an existing one keeps its place),
/// enumeration in insertion order, and on the first Remove the bag becomes a Dictionary built in that order (so the free-entry reuse of
/// later inserts is the Dictionary's own). A writer replaces the array reference once (a reader on another thread sees one consistent shape
/// and values) or stores one value slot (as the dictionary did). SHEPHERD_PROPS_SHAPED=0: every bag is a Dictionary (kit #5).</summary>
public static class PropBag
{
    public static readonly bool Shaped = Environment.GetEnvironmentVariable("SHEPHERD_PROPS_SHAPED") != "0";

    public static object Get(object bag, string k)
    {
        if (bag is object[] s) { int i = ((PropShape)s[0]).IndexOf(k); return i > 0 ? s[i] : null; }
        return bag is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v : null;
    }
    public static bool Contains(object bag, string k) => bag is object[] s ? ((PropShape)s[0]).IndexOf(k) > 0 : bag is Dictionary<string, object> d && d.ContainsKey(k);
    public static int Count(object bag) => bag is object[] s ? s.Length - 1 : bag is Dictionary<string, object> d ? d.Count : 0;

    public static object Set(object bag, string k, object v)
    {
        if (bag is object[] s)
        {
            var sh = (PropShape)s[0]; int i = sh.IndexOf(k);
            if (i > 0) { s[i] = v; return s; }
            var t = new object[s.Length + 1]; Array.Copy(s, 1, t, 1, s.Length - 1); t[0] = sh.With(k); t[s.Length] = v; return t;
        }
        if (bag is Dictionary<string, object> d) { d[k] = v; return d; }
        if (!Shaped) return new Dictionary<string, object> { [k] = v };
        return new object[] { PropShape.Empty.With(k), v };
    }

    /// <summary>Keys and values to set in order (two lists of reference types: no new generic code in the eboot).</summary>
    public sealed class Pending
    {
        internal readonly List<string> K = new(); internal readonly List<object> V = new();
        public int Count => K.Count;
        public void Add(string k, object v) { K.Add(k); V.Add(v); }
        public void Clear() { K.Clear(); V.Clear(); }
    }
    public static object SetMany(object bag, Pending kv)
    {
        if (bag is Dictionary<string, object> || !Shaped) { var d = bag as Dictionary<string, object> ?? new Dictionary<string, object>(); for (int i = 0; i < kv.Count; i++) d[kv.K[i]] = kv.V[i]; return d; }
        var s = bag as object[]; var sh = s != null ? (PropShape)s[0] : PropShape.Empty;
        int n = s != null ? s.Length - 1 : 0, add = 0;
        for (int i = 0; i < kv.Count; i++) if (sh.IndexOf(kv.K[i]) < 0) { sh = sh.With(kv.K[i]); add++; }   // the shape the Set sequence would end at
        var t = new object[n + add + 1]; if (s != null) Array.Copy(s, 1, t, 1, n); t[0] = sh;
        for (int i = 0; i < kv.Count; i++) t[sh.IndexOf(kv.K[i])] = kv.V[i];   // a repeated key: the last value, in its first place (as the dictionary)
        return t;
    }

    public static bool Remove(ref object bag, string k)
    {
        if (bag is object[] s)
        {
            if (((PropShape)s[0]).IndexOf(k) < 0) return false;
            var d = ToDictionary(s); bag = d; return d.Remove(k);
        }
        return bag is Dictionary<string, object> dd && dd.Remove(k);
    }

    /// <summary>The bag's entries in enumeration order (insertion order; a Dictionary's own order once it is one).</summary>
    public static IEnumerable<KeyValuePair<string, object>> Entries(object bag)
    {
        if (bag is object[] s) { var keys = ((PropShape)s[0]).Keys; for (int i = 1; i < s.Length; i++) yield return new(keys[i - 1], s[i]); }
        else if (bag is Dictionary<string, object> d) foreach (var kv in d) yield return kv;
    }
    /// <summary>A copy for a clone: `new Dictionary(bag)` (the entries in order, no free entries); a shaped bag's copy shares its shape.</summary>
    public static object Copy(object bag) => bag is object[] s ? s.Clone() : bag is Dictionary<string, object> d ? new Dictionary<string, object>(d) : null;
    static Dictionary<string, object> ToDictionary(object[] s) { var keys = ((PropShape)s[0]).Keys; var d = new Dictionary<string, object>(s.Length - 1); for (int i = 1; i < s.Length; i++) d.Add(keys[i - 1], s[i]); return d; }
}

/// <summary>A key sequence shared by shaped property bags (<see cref="PropBag"/>): the keys in insertion order, a key's slot (1-based), and
/// the shape one more key leads to (made once, then shared). Immutable once made except the transition table (locked).</summary>
public sealed class PropShape
{
    public static readonly PropShape Empty = new(Array.Empty<string>(), null);
    public static int Shapes;
    public readonly string[] Keys;
    readonly Dictionary<string, int> _slot;
    Dictionary<string, PropShape> _next;
    PropShape(string[] keys, Dictionary<string, int> slot) { Keys = keys; _slot = slot ?? new Dictionary<string, int>(); Shapes++; }
    public int IndexOf(string k) => _slot.TryGetValue(k, out var i) ? i : -1;
    public PropShape With(string k)
    {
        lock (this)
        {
            if (_next != null && _next.TryGetValue(k, out var n)) return n;
            var keys = new string[Keys.Length + 1]; Array.Copy(Keys, keys, Keys.Length); keys[Keys.Length] = k;
            var slot = new Dictionary<string, int>(_slot.Count + 1); foreach (var kv in _slot) slot.Add(kv.Key, kv.Value); slot.Add(k, keys.Length);
            n = new PropShape(keys, slot); (_next ??= new Dictionary<string, PropShape>(StringComparer.Ordinal))[k] = n;
            return n;
        }
    }
}

public sealed class NGameObject : NObj
{
    public readonly List<NComponent> Components = new();
    public NTransform Transform => (NTransform)Components[0];
    /// <summary>Kit #9 engine (SHEPHERD_K9_STAMPS): a property (was a field) so that a change bumps the hierarchy epoch the activeInHierarchy
    /// stamps are checked against (<see cref="NTransform.HierEpoch"/>). The value itself is the same bool as before.</summary>
    public bool ActiveSelf { get => _activeSelf; set { if (_activeSelf != value) { _activeSelf = value; if (Components.Count > 0 && Components[0] is NTransform t) t.HierChangedHere(); else NTransform.HierChanged(); } } }
    bool _activeSelf = true;
    // (kit #11 walk, the records diet: IsStatic declared beside _activeSelf -- the two bools share one word before Layer -- so the record is 80
    // bytes, SGen's 80 class; in the old order IsStatic took a word of its own after Layer; SHEPHERD_K10_RECORDS: the old order)
#if SHEPHERD_K10_RECORDS
    public int Layer; public bool IsStatic;
#else
    public bool IsStatic; public int Layer;
#endif
    /// <summary>Kit #8 speed track: a property (was a field) so that a change moves the record in the tag index (Runtime/K8Speed.cs TagIndex;
    /// SHEPHERD_K8_TAGS=0: the index is not kept). The value itself is the same string as before.</summary>
    public string Tag { get => _tag; set { var old = _tag; _tag = value; if (old != value) Runtime.TagIndex.Moved(this, old, value); } }
    string _tag = "Untagged";
    /// <summary>The scene handle (0 = asset space). menu-speed (top hotspots): a change queues the transform for NotifyRectChanges, whose
    /// test reads it.</summary>
    public int Scene { get => _scene; set { if (_scene != value) { _scene = value; if (Components.Count > 0 && Components[0] is NTransform t) t.NrcDirty(); } } }
    int _scene = 1;
    /// <summary>menu-speed (SceneRenderer.UiCache.cs): equals the UI pass's holder generation while a live CanvasGroup record sits on this object.</summary>
    internal int UiHolderGen;
    public bool ActiveInHierarchy { get { if (NTransform.AhOn) return Transform.ChainActive(); for (var t = Transform; t != null; t = t.Parent) if (!t.Go.ActiveSelf) return false; return true; } }
}

public class NComponent : NObj
{
    public NGameObject Go;
    // Kit #9 engine, the objects' sizes (review round 2): on the Vita (32-bit Mono, GC-aware auto layout: references first, then the other fields
    // in declaration order at their own alignment; each class's size rounded to its min_align, 8 here because of NObj's long PathId) NComponent's
    // fields end at 68 and the class rounds to 72 -- 4 bytes of padding every component record already carries. The transform's activeInHierarchy
    // stamp lives there (NTransform below), so no record grows: NTransform stays at SGen's 200-byte size class (the next is 256: the physics
    // track's kit #9 lesson, +3 MiB on the walk), NMono at 96 (next 128). engine-work/tools/fields (a copy of the physics track's) prints the layout.
    /// <summary>Kit #9 engine (SHEPHERD_K9_STAMPS): a transform's activeInHierarchy stamp (<see cref="NTransform.ChainActive"/>); unused (0) on
    /// other components.</summary>
    private protected int _ahStamp;
}
/// <summary>menu-speed (top hotspots): a Renderer's record (Lifecycle.NewComponentRecord), with SceneRenderer's world-pass cache of its
/// property-bag reads (Render/SceneRenderer.WorldCache.cs); otherwise a plain component record.</summary>
public sealed class NRenderer : NComponent
{
    /// <summary>Bumped by every write to the record's property bag (<see cref="NObj.Set"/>, <see cref="NObj.RemoveProp"/>,
    /// <see cref="NObj.ReplaceProps"/>): what a reader derived from the bag stays valid while the version is unchanged.</summary>
    internal int PropsVer;
    internal Shepherd.Engine.Render.SceneRenderer.WCache WC;
}
public class NBehaviour : NComponent
{
    public bool Enabled = true;
    /// <summary>Kit #9 engine (SHEPHERD_K9_WHEEL): an NMono's count of live coroutines / Invoke timers (255 = at least that many, never counted down
    /// again): 0 lets StopCoroutine / StopAllCoroutines / CancelInvoke / IsInvoking skip the search of every live one. Declared here, in the padding
    /// after Enabled (NBehaviour's fields end at 73 and round to 80), so NMono stays 96 bytes on the Vita (in NMono it was 104 = SGen's 128 class).</summary>
    internal byte CoN, InvN;
#if !SHEPHERD_K10_RECORDS
    // Kit #11 walk (the records diet): NMono's four flags are declared here, in the same word as Enabled / CoN / InvN (NBehaviour's fields end
    // at 71 and round to 72 now that NObj is 56), so NMono is 80 bytes (SGen's 80 class; it was 96). They belong to NMono's use only.
    public bool Awoken, Started, IsEnabledCalled;    // (NMono) IsEnabledCalled: OnEnable delivered and not yet matched by OnDisable
    /// <summary>(NMono) kit3 (sub-menus): Lifecycle queued it for Start at least once (only such a behaviour can be in its PendingStart list).</summary>
    public bool StartQueued;
    /// <summary>(NMono) kit #11 walk review fix: what <see cref="NMono.TrigD"/> holds -- 0 nothing yet, 1 the type has no trigger / collision
    /// handlers (Physics/PhysDispatch.cs answers its shared empty table: nothing stored for this behaviour), 2 the bound table is in the side
    /// table. The byte at offset 71, NBehaviour's last byte of padding: the class stays 72 bytes and NMono 80.</summary>
    internal byte TrigK;
#endif
}
public sealed class NMono : NBehaviour
{
#if SHEPHERD_K10_RECORDS
    public bool Awoken, Started, IsEnabledCalled;    // IsEnabledCalled: OnEnable delivered and not yet matched by OnDisable
    // (kit #9 engine: CoN / InvN, the wheel's per-behaviour counts, are declared in NBehaviour: the object's size)
    /// <summary>kit3 (sub-menus): Lifecycle queued it for Start at least once (only such a behaviour can be in its PendingStart list).</summary>
    public bool StartQueued;
#else
    // (kit #9 engine: CoN / InvN, the wheel's per-behaviour counts, are declared in NBehaviour: the object's size; kit #11 walk: Awoken, Started,
    // IsEnabledCalled and StartQueued too)
#endif
    internal object LifecycleMsgs;                   // Lifecycle's message table of the managed type (stage 2)
    internal object PhaseD;                          // menu-speed: Lifecycle's phase-message delegates bound to Managed (made on first use): an Action[3] (FixedUpdate, Update, LateUpdate), or the Action itself when the type has one of them
#if SHEPHERD_K10_RECORDS
    internal object TrigD;                           // physics Stage 2 (Physics/PhysDispatch.cs): the trigger handlers' closed delegates bound to Managed (made on first use)
#else
    /// <summary>physics Stage 2 (Physics/PhysDispatch.cs): the trigger handlers' closed delegates bound to Managed (made on first use). Kit #11
    /// walk (the records diet): kept in a side table, so NMono is 80 bytes; the value read is exactly the one last written (null before).
    /// Review fix: only behaviours whose type HAS handlers get an entry (a weak-table handle: on Mono an ephemeron the GC walks at every
    /// collection, and a lookup per message) -- a type without any is marked in <see cref="NBehaviour.TrigK"/> (no entry, no lookup).</summary>
    internal object TrigD
    {
        get => TrigK == 2 && TrigOf.TryGetValue(this, out var b) ? b.V : null;
        set { if (TrigK == 2 && TrigOf.TryGetValue(this, out var b)) b.V = value; else if (value != null) { TrigOf.GetValue(this, static _ => new TrigBox()).V = value; TrigK = 2; } }
    }
    /// <summary>Kit #11 walk review fix: this behaviour's type has no trigger / collision handlers (PhysDispatch's answer, kept in a byte of
    /// padding instead of a side-table entry holding the shared empty table).</summary>
    internal bool TrigNone { get => TrigK == 1; set { if (value && TrigK == 0) TrigK = 1; } }
    sealed class TrigBox { public object V; }
    static readonly ConditionalWeakTable<NMono, TrigBox> TrigOf = new();
#endif
    public MonoBehaviour MB => (MonoBehaviour)Managed;
}

public class NTransform : NComponent
{
    public readonly List<NTransform> Children = new();
    /// <summary>Transform.hasChanged (get_hasChanged / set_hasChanged). Kit #9 (physics-plan-v2 Stage 7 item 4, decision D5 "lazy hasChanged";
    /// vita/notes/kit9-physics.md): with <see cref="HcLazy"/> (the default; SHEPHERD_LAZY_HASCHANGED=0 = kit #8's eager flags) MarkChanged walks
    /// no subtree for the flag. Each transform keeps ONE long, _hcMark (its last MarkChanged: it and its whole subtree changed then); the few
    /// transforms whose hasChanged was ever WRITTEN keep (clear, self) in a weak side table. The eager flag starts true and only a
    /// write makes it false, so a transform never written reads true; a written one reads true when its last write was true (self) or any
    /// _hcMark on its chain (itself and its ancestors) is newer than its last write (clear) -- exactly the eager flag (the eager MarkChanged sets
    /// the flag on the transform and every descendant, set_hasChanged touches one transform). A parent change rebases the written transforms of
    /// the moved subtree first (each keeps its value: clear = now, self = the value), so marks made before the move by the old or the new
    /// ancestors never count, as eager flags never move with a subtree; with no transform ever written (the usual case: no game DLL reads
    /// hasChanged, physics-plan-v2 1.5 -- TMP only writes it) a parent change costs nothing. MarkChanged's walk now descends only into subtrees with physics interest
    /// (PhysDesc > 0: the dirty hook's targets, in the eager walk's order). SHEPHERD_CHECK_PHYS=lazy keeps the eager flags as well and compares
    /// both values of every transform every frame (<see cref="HcCompareAll"/>, Physics/PhysWorld.cs). Memory: 8 bytes a transform (a 64-bit
    /// clock: no wrap in any session).</summary>
    public bool HasChanged
    {
        get => HcLazy ? HcValue() : _hc;
        set
        {
            if (HcLazy) HcWrite(value);
            if (!HcLazy || HcCheck) _hc = value;
        }
    }
    // (_hc and _hcMark are declared below, beside DrivenProperties and _isRect: see "the object's size" there)
    sealed class HcState { public long Clear; public bool Self; }
    static readonly ConditionalWeakTable<NTransform, HcState> _hcWritten = new();
    static long _hcClock = 1;
    static bool _hcAnyWritten;
    /// <summary>Kit #9: the lazy hasChanged (default on; SHEPHERD_LAZY_HASCHANGED=0 = kit #8's eager MarkChanged walk and flags).</summary>
    public static readonly bool HcLazy = Environment.GetEnvironmentVariable("SHEPHERD_LAZY_HASCHANGED") != "0";
    /// <summary>Kit #9: SHEPHERD_CHECK_PHYS=lazy (or all): the eager flags kept beside the lazy form and compared.</summary>
    public static readonly bool HcCheck = HcCheckAsked();
    static bool HcCheckAsked() { var c = (Environment.GetEnvironmentVariable("SHEPHERD_CHECK_PHYS") ?? "").Trim().ToLowerInvariant(); return HcLazy && (c == "all" || c.Split(',').Contains("lazy") || c.Split(',').Contains("lazyw")); }
    /// <summary>Kit #9 review fix: SHEPHERD_CHECK_PHYS=lazyw (test only; implies lazy): every frame, after the comparison, hasChanged is WRITTEN on a
    /// seeded ~1/32 of the live transforms (true or false, seeded), so the comparison also covers WRITTEN transforms -- the side table, the
    /// ancestors' marks, the parent-change rebase -- on a real scene. The drives' own game code writes and reads none (the walk / combat:
    /// "reads 0, writes 0"), so without this every transform reads true both ways and the comparison cannot fail. No game code reads
    /// hasChanged (physics-plan-v2 1.5), so a drive's results must stay its goldens with it.</summary>
    public static readonly bool HcCheckW = HcCheck && (Environment.GetEnvironmentVariable("SHEPHERD_CHECK_PHYS") ?? "").Trim().ToLowerInvariant().Split(',').Contains("lazyw");
    static uint _hcwSeed = 0x2545F491u;
    /// <summary>SHEPHERD_CHECK_PHYS=lazyw: the seeded writes (above); the count goes into HcCheckWrites.</summary>
    public static void HcCheckWrite(IEnumerable<NObj> transforms)
    {
        if (!HcCheckW) return;
        foreach (var o in transforms)
        {
            if (o is not NTransform t || t.Destroyed) continue;
            _hcwSeed = _hcwSeed * 1664525u + 1013904223u;
            if ((_hcwSeed >> 27) != 0) continue;
            t.HasChanged = (_hcwSeed & 0x10000u) != 0;
            HcCheckWrites++;
        }
    }
    /// <summary>Kit #9 counters (the exit summary): MarkChanged calls, the transforms its walk visited, parent-change rebases and their visits,
    /// hasChanged reads and writes, and (check mode) frames / transforms compared and mismatches.</summary>
    public static long HcMarks, HcVisits, HcRebases, HcRebaseVisits, HcReads, HcWrites, HcCheckFrames, HcChecked, HcMismatches, HcCheckWrites;
    static long HcTick() => ++_hcClock;
    void HcWrite(bool value)
    {
        HcWrites++;
        long t = HcTick();
        var st = _hcWritten.GetValue(this, _ => new HcState());
        _hcAnyWritten = true;
        st.Clear = t; st.Self = value;
    }
    bool HcValue()
    {
        HcReads++;
        return HcValueOf(this);
    }
    static bool HcValueOf(NTransform t0)
    {
        if (!_hcAnyWritten || !_hcWritten.TryGetValue(t0, out var st)) return true;   // never written: the eager flag is true
        if (st.Self) return true;
        long c = st.Clear;
        for (var t = t0; t != null; t = t._parent) if (t._hcMark > c) return true;
        return false;
    }
    /// <summary>Before a parent change (lazy form): every written transform of this subtree keeps its current value, independent of ancestors.</summary>
    void HcRebase()
    {
        if (!_hcAnyWritten) return;
        HcRebases++;
        long anc = 0;
        for (var p = _parent; p != null; p = p._parent) if (p._hcMark > anc) anc = p._hcMark;
        HcRebase(this, anc);
    }
    static void HcRebase(NTransform t, long anc)
    {
        HcRebaseVisits++;
        long m = t._hcMark > anc ? t._hcMark : anc;
        if (_hcWritten.TryGetValue(t, out var st)) { bool v = st.Self || m > st.Clear; st.Clear = HcTick(); st.Self = v; }
        foreach (var c in t.Children) HcRebase(c, m);
    }
    /// <summary>SHEPHERD_CHECK_PHYS=lazy: the lazy value of every live transform against its eager flag (both maintained); returns the mismatches.</summary>
    public static int HcCompareAll(IEnumerable<NObj> transforms)
    {
        if (!HcLazy || !HcCheck) return 0;
        HcCheckFrames++;
        int bad = 0;
        foreach (var o in transforms)
        {
            if (o is not NTransform t || t.Destroyed) continue;
            HcChecked++;
            bool lazy = HcValueOf(t);
            if (lazy != t._hc && bad++ < 4 && HcMismatches + bad <= 8) Log.Warn($"[hc] lazy hasChanged check f{Runtime.Time.FrameCount}: '{t.Go?.Name}' lazy {lazy}, eager {t._hc}");
        }
        HcMismatches += bad;
        return bad;
    }
    public NObj DrivenBy; public int DrivenProperties;
    // Kit #9, the object's size: on the Vita (32-bit Mono, GC-aware auto layout = the reference fields first, then the others in declaration
    // order, each at its own alignment; a long at 8) kit #8's NTransform is 200 bytes, EXACTLY SGen's 200-byte size class (the next one is
    // 256). The lazy hasChanged's long, declared beside the old flag, made it 208 = a 256-byte slot: +56 bytes a transform, +3 MiB on the
    // intro walk's arena peak (VLX, vita/notes/kit9-physics.md section 7d). Declared here instead -- the long right after DrivenProperties (an
    // 8-aligned offset, no padding) and _hc among the bools beside _isRect -- it is 200 again. Any field added to NTransform (or NObj /
    // NComponent) crosses into the 256 class: physics-work/tools/fields prints the layout.
    long _hcMark = 1;   // kit #9 lazy hasChanged (above)
    /// <summary>Polish round 1: SceneRenderer's per-frame SortingGroup membership (its SGroup for this transform, valid only while the
    /// group's frame stamp is the current pass's; otherwise stale and ignored).</summary>
    internal object SortGroup;

    // ---- the transform's own state. V6 stage 2: properties instead of fields, so that a value that really changes (bit for bit)
    // marks the record; the derived matrices below are memoized against those marks (exact: a memo is only used while every input
    // it was computed from is unchanged). RectTransform state (IsRect): xy of LocalPosition is DERIVED from these + the parent rect.
    // (menu-speed hop fix: the initial values are field stores, not Unity's Quaternion.identity / Vector3.one / Vector2 constructor;
    // the same values: (0,0,0,1), (1,1,1), (0.5,0.5), (100,100))
    NTransform _parent; Vector3 _lp; Quaternion _lr = Um.QIdentity; Vector3 _ls = Um.One3;
    bool _isRect; bool _hc = true;
    // ---- kit #11 walk, the records diet (vita/notes/kit11-walk.md): the RectTransform's five vectors (anchorMin, anchorMax, anchoredPosition,
    // sizeDelta, pivot: 40 bytes) in a side object, made on the first write that changes one of them (Bits.Same, as the setters always
    // tested); until then every read answers Unity's defaults from one shared, never-written instance. With NObj at 56 bytes this makes
    // NTransform 160 bytes (SGen's 160 class; 200 before). Only RectTransforms whose values differ from the defaults pay the 48-byte part.
    // SHEPHERD_K11_RECDIET=0 (env file) = every transform makes its part at creation (no shared instance in use: kit #10's eager state).
    // The build switch SHEPHERD_K10_RECORDS (build.sh SHEPHERD_K11_RECORDS=0) = kit #10's record: the five vectors as fields of the transform
    // in kit #10's order and types, named as the part's (_rect / RectW are then the transform itself, so the code below is the same).
#if SHEPHERD_K10_RECORDS
    Vector2 AMin = Um.V2(0.5f, 0.5f), AMax = Um.V2(0.5f, 0.5f), APos, Sd = Um.V2(100f, 100f), Piv = Um.V2(0.5f, 0.5f);
    NTransform _rect => this;
    NTransform RectW => this;
#else
    sealed class RectPart
    {
        public Vector2 AMin = Um.V2(0.5f, 0.5f), AMax = Um.V2(0.5f, 0.5f), APos, Sd = Um.V2(100f, 100f), Piv = Um.V2(0.5f, 0.5f);
    }
    static readonly RectPart RectDefaults = new();
    internal static readonly bool RecDiet = Environment.GetEnvironmentVariable("SHEPHERD_K11_RECDIET") != "0" && Environment.GetEnvironmentVariable("SHEPHERD_K11_WALK") != "0";
    RectPart _rect = RecDiet ? RectDefaults : new RectPart();
    RectPart RectW => ReferenceEquals(_rect, RectDefaults) ? _rect = new RectPart() : _rect;   // the record's own part, for a write
    /// <summary>Kit #11 walk (MemReport): this transform has its own rect part (a RectTransform with non-default values).</summary>
    internal bool HasRectPart => !ReferenceEquals(_rect, RectDefaults);
#endif
    public NTransform Parent { get => _parent; set { if (_parent != value) { if (HcLazy) HcRebase(); if (PhysDesc != 0) Physics.PhysWorld.ParentChanging(this, _parent, value); _parent = value; HierChangedHere(); Changed(); } } }
    // ---- kit #9 engine (SHEPHERD_K9_STAMPS): activeInHierarchy from a stamp ----
    // NGameObject.ActiveInHierarchy walked the chain up to the root on every read (every world renderer per camera pass, every UI node, every
    // lifecycle and icall test). Its answer is a function of the chain's parent links and each link's GameObject.activeSelf only; every write of
    // either goes through NGameObject.ActiveSelf / NTransform.Parent, which bump HierEpoch. A transform keeps the chain's answer with the epoch it
    // was computed at, so while nothing in any hierarchy changed it answers at once, and a walk after a change reuses each ancestor's answer
    // (computed once per epoch): the same value as the walk (an ancestor's answer IS the walk from it; the recursion visits the same links in
    // the same order and stops at the same inactive link). =check walks too, returns the walk's answer and counts differences.
    internal static readonly bool AhOn = Runtime.K9Engine.Stamps;
    static readonly bool AhCheck = Runtime.K9Engine.StampsCheck;
    /// <summary>Bumped by every activeSelf change and parent link change; starts at 1 (never reused: on reaching 2^30 every stamp is cleared and it
    /// restarts at 1).</summary>
    internal static int HierEpoch = 1;
    // The chain's answer with its epoch, in ONE int (epoch << 1 | answer; 0 = none: an epoch is >= 1) -- NComponent._ahStamp, in the padding every
    // component record already has (above): a single store, so a reader never pairs a new epoch with an old answer.
    internal static void HierChanged() { if (++HierEpoch == 1 << 30) Objects.ClearAhStamps(); }
    internal void ClearAhStamp() => _ahStamp = 0;
    /// <summary>A change at this link (its GameObject's activeSelf, its parent link): only this transform's subtree can answer differently. A
    /// transform without children clears just its own stamp; otherwise the epoch moves (every stamp is stale).</summary>
    internal void HierChangedHere() { if (Children.Count == 0) _ahStamp = 0; else HierChanged(); }
    /// <summary>The walk's answer for the chain from this transform up (this transform's GameObject active, and so on up to the root).</summary>
    internal bool ChainActive()
    {
        int e = HierEpoch, st = _ahStamp;
        if (!AhCheck) { if (st >> 1 == e) { Runtime.K9Engine.AhHits++; return (st & 1) != 0; } return Compute(e); }
        bool v = st >> 1 == e ? (st & 1) != 0 : Compute(e), w = true;   // =check: the walk of every read, compared (its links counted: kit #8's work)
        for (var t = this; t != null; t = t.Parent) { Runtime.K9Engine.AhRefLinks++; if (!t.Go.ActiveSelf) { w = false; break; } }
        if (w != v) Runtime.K9Engine.Diff("activeInHierarchy", Name);
        return w;
    }
    /// <summary>This link's answer at epoch e: its GameObject's activeSelf and the parent's answer (the parent's stamp when it has one).</summary>
    bool Compute(int e)
    {
        bool v = Go.ActiveSelf;
        if (v && _parent != null) { int ps = _parent._ahStamp; v = ps >> 1 == e ? (ps & 1) != 0 : _parent.Compute(e); }
        _ahStamp = e << 1 | (v ? 1 : 0); Runtime.K9Engine.AhWalks++;
        return v;
    }
    /// <summary>Physics stage 0 (physics-plan-v2 1.5; Physics/PhysWorld.cs): bit 1 = this transform's GameObject owns a Rigidbody2D, bit 2 = it has
    /// 2D colliders; PhysDesc = the interested transforms in this subtree, itself included (kept through attach / detach / reparent /
    /// destroy; Stage 7 walks MarkChanged only into PhysDesc > 0); PhysDirty = in the sync check's dirty set. Always 0 at SHEPHERD_PHYS=none.</summary>
    public int PhysInterest, PhysDesc; internal bool PhysDirty;
    public Vector3 LocalPosition { get => _lp; set { if (!Bits.Same(_lp, value)) { _lp = value; ChangedPose(); } } }
    public Quaternion LocalRotation { get => _lr; set { if (!Bits.Same(_lr, value)) { _lr = value; ChangedPose(); } } }
    public Vector3 LocalScale { get => _ls; set { if (!Bits.Same(_ls, value)) { _ls = value; ChangedPose(); } } }
    public bool IsRect { get => _isRect; set { if (_isRect != value) { _isRect = value; Changed(); } } }
    public Vector2 AnchorMin { get => _rect.AMin; set { if (!Bits.Same(_rect.AMin, value)) { RectW.AMin = value; Changed(); } } }
    public Vector2 AnchorMax { get => _rect.AMax; set { if (!Bits.Same(_rect.AMax, value)) { RectW.AMax = value; Changed(); } } }
    public Vector2 AnchoredPosition { get => _rect.APos; set { if (!Bits.Same(_rect.APos, value)) { RectW.APos = value; ChangedPose(); } } }
    public Vector2 SizeDelta { get => _rect.Sd; set { if (!Bits.Same(_rect.Sd, value)) { RectW.Sd = value; if (RectNegCtl) ChangedPose(); else Changed(); } } }
    public Vector2 Pivot { get => _rect.Piv; set { if (!Bits.Same(_rect.Piv, value)) { RectW.Piv = value; ChangedPose(); } } }

    // ---- memo (allocated for the transforms whose derived values are asked for, and their ancestors) ----
    sealed class Memo
    {
        public long Ver, WvSelf = -1, WvParentVer = -1, Wv, WvEpoch = -1; public NTransform WvParent;
        public long LocalKey = -1, LtwKey = -1, RectKey = -1, W2lKey = -1, RotKey = -1, LossyKey = -1, SizeSeenKey = -1, PosKey = -1;
        public Matrix4x4 Local, Ltw, W2l; public RectF Rect; public Quaternion Rot; public Vector3 Lossy, Pos;
        public BoxSet Bx;   // menu-speed (top hotspots): icall result boxes, made on first request
    }
    /// <summary>menu-speed (top hotspots): boxed icall results of a transform, each with the memo key it was made for (an icall's out-parameter
    /// box is unboxed by the rewritten Unity stub at once: nothing keeps, compares or writes it, so an unchanged value can share one box).</summary>
    sealed class BoxSet { public object Lossy, Bounds; public long LossyKey = -1, BoundsKey = -1; }
    Memo _m;
    static long _clock;   // one counter for every version and stamp: a value is never reused
    /// <summary>menu-speed (top hotspots): bumped by every state change of any transform (every Changed(), memo or not): while it is unchanged,
    /// no transform's own version and no parent link has changed, so a world stamp validated at this value is still valid.</summary>
    static long _writes;
    void Changed() { _writes++; if (_m != null) _m.Ver = ++_clock; NrcDirty(); }   // without a memo, nothing was derived from this record yet
    /// <summary>Kit #13 speed (Render/K13Speed.cs, SHEPHERD_K13_RECTDIRTY): a write that cannot change any rect's SIZE (local position,
    /// rotation, scale, anchored position, pivot) -- the same version / stamp bump as <see cref="Changed"/>, without NotifyRectChanges' queue
    /// (switch off: <see cref="Changed"/>, kit #12).</summary>
    void ChangedPose() { if (!RectDirty) { Changed(); return; } _writes++; if (_m != null) _m.Ver = ++_clock; PoseWrites++; }
    static readonly bool RectDirty = Shepherd.Engine.Render.K13Speed.RectDirty, RectNegCtl = Shepherd.Engine.Render.K13Speed.RectNegCtl;
    /// <summary>Kit #13 speed: pose writes that did not queue their transform (the [k13] line).</summary>
    internal static long PoseWrites;
    // ---- menu-speed (top hotspots): SceneRenderer.NotifyRectChanges' dirty queue ----
    /// <summary>Transforms that may need NotifyRectChanges' size comparison: queued by every state change (Changed: own state, parent link,
    /// IsRect), by Bind (a new registry member) and by a change of its GameObject's scene; the walk compares the queued transforms and
    /// their subtrees (a descendant's world stamp includes every ancestor) instead of all ~10,700 transforms. NrcQueued: in the queue now;
    /// a transform reached twice by the walk (queued and under a queued ancestor) is merged there.</summary>
    internal static List<NTransform> NrcQueue = new(NrcQueueCap);
    internal bool NrcQueued;
    internal void NrcDirty()
    {
        if (NrcQueued || !HotCfgOn) return;
        if (NrcQueue.Count >= NrcQueueCap) { NrcOverflow = true; return; }   // (more than the walk would collect: its next walk is the full one)
        NrcQueued = true; NrcQueue.Add(this);
    }
    internal const int NrcQueueCap = 960;   // (a queue / candidate array of 960 references stays a small object: 7,680 bytes)
    /// <summary>A transform could not be queued (the queue was full): the next NotifyRectChanges is the full walk.</summary>
    internal static bool NrcOverflow;
    Memo M_() => _m ??= NewMemo();
    // ---- kit #9 engine (SHEPHERD_K9_RECYCLE): a destroyed transform's memo serves the next transform that needs one ----
    // Objects.Kill hands the memo over (the dead record's _m is cleared first: a later read of the dead record makes itself a new one) and
    // NewMemo resets it to exactly a new memo's state (every key -1, Wv 0, no parent, no boxes; the value fields are only read under a matching
    // key). The clock is advanced at the same point as `new Memo { Ver = ++_clock }`, so every version and stamp value is the one kit #8 makes.
    static readonly bool MemoRecycle = Runtime.K9Engine.Recycle;
    static readonly Stack<Memo> _memoFree = new();
    static Memo NewMemo()
    {
        if (!MemoRecycle || _memoFree.Count == 0) return new Memo { Ver = ++_clock };
        var m = _memoFree.Pop(); Runtime.K9Engine.Recycled++;
        m.Ver = ++_clock; m.WvSelf = -1; m.WvParentVer = -1; m.Wv = 0; m.WvEpoch = -1; m.WvParent = null;
        m.LocalKey = m.LtwKey = m.RectKey = m.W2lKey = m.RotKey = m.LossyKey = m.SizeSeenKey = m.PosKey = -1; m.Bx = null;
        return m;
    }
    /// <summary>Objects.Kill: this dead transform's memo goes to the free list (at most 1,024 kept; the purgeable arena may drop them).</summary>
    internal void RecycleMemo() { var m = _m; if (m == null || !MemoRecycle) return; _m = null; if (_memoFree.Count < 1024) _memoFree.Push(m); }
    internal static int PurgeMemos() { int n = _memoFree.Count; _memoFree.Clear(); return n; }
    /// <summary>A stamp that changes whenever this transform's state, any ancestor's state or any parent link on the way to the
    /// root changes: every value derived from the chain (rects, local and world matrices) is a function of exactly that.
    /// menu-speed (top hotspots): a stamp validated since the last transform write is returned as it is -- the chain validation would find
    /// its own version, its parent link and (by the same argument up the chain, every ancestor having been validated in the same call) its
    /// parent's stamp unchanged and return it untouched: ~13,200 recursive calls per idle Main Menu frame -> one per asking call.</summary>
    long WorldStamp()
    {
        var m = M_();
        if (m.WvEpoch == _writes && HotCfgOn)
        {
            if (Shepherd.Engine.Render.HotCfg.Check) Shepherd.Engine.Render.HotCheck.L(Shepherd.Engine.Render.HotCheck.Stamp, m.Wv, WorldStampFull(m));
            return m.Wv;
        }
        return WorldStampFull(m);
    }
    long WorldStampFull(Memo m)
    {
        var p = _parent;
        long pv = p == null ? 0 : p.WorldStamp();
        if (m.WvSelf != m.Ver || !ReferenceEquals(m.WvParent, p) || m.WvParentVer != pv) { m.WvSelf = m.Ver; m.WvParent = p; m.WvParentVer = pv; m.Wv = ++_clock; }
        m.WvEpoch = _writes;
        return m.Wv;
    }
    static readonly bool HotCfgOn = Shepherd.Engine.Render.HotCfg.On;

    /// <summary>fid1-fixup D6: <see cref="WorldStamp"/> for callers that cache something derived from the world matrix (Triggers2D's shapes).</summary>
    internal long ChainStamp => WorldStamp();
    /// <summary>SceneRenderer.NotifyRectChanges: nothing Size reads (own rect fields, the parent rects) changed since <see cref="MarkSizeSeen"/>.</summary>
    internal bool SizeSeen() => _m != null && _m.SizeSeenKey == WorldStamp();
    internal void MarkSizeSeen() => M_().SizeSeenKey = WorldStamp();

    // menu-speed hop fix: the rect arithmetic below is our own, on the fields (the rect memo holds a RectF: Rect's four floats);
    // each line names the Unity operations it reproduces, operand for operand. SHEPHERD_HOPFIX_CHECK=1 recomputes them through Unity.
    public Rect ParentRect => Parent != null && Parent.IsRect ? Parent.Rect : default;
    /// <summary><see cref="ParentRect"/>'s fields (default(Rect) = all +0).</summary>
    internal RectF ParentRectF => _parent != null && _parent._isRect ? _parent.RectF : default;
    /// <summary>SizeDelta + Vector2.Scale(AnchorMax - AnchorMin, ParentRect.size).</summary>
    public Vector2 Size { get { SizeF(out var x, out var y); return Um.V2(x, y); } }
    void SizeF(out float x, out float y)
    {
        var pr = ParentRectF;
        // Vector2 -: (aMax.x - aMin.x, ...); Vector2.Scale(a, b): a.x * b.x with b = rect.size = (m_Width, m_Height); Vector2 +: sd.x + that
        var q = _rect; x = q.Sd.x + (q.AMax.x - q.AMin.x) * pr.W; y = q.Sd.y + (q.AMax.y - q.AMin.y) * pr.H;
        if (HopCheck.On) HopCheck.V2(HopCheck.NtSize, Um.V2(x, y), SizeDelta + Vector2.Scale(AnchorMax - AnchorMin, ParentRect.size));
    }
    /// <summary>The rect (Unity's struct, for the icalls and the reference passes), from <see cref="RectF"/>.</summary>
    public Rect Rect => Um.ToRect(RectF);
    /// <summary>new Rect(-Pivot.x * s.x, -Pivot.y * s.y, s.x, s.y) of s = Size, memoized by the world stamp.</summary>
    internal RectF RectF
    {
        get
        {
            var m = M_(); long k = WorldStamp(); if (m.RectKey == k) return m.Rect;
            SizeF(out var sx, out var sy); var pv = _rect.Piv; var r = new RectF(-pv.x * sx, -pv.y * sy, sx, sy);
            if (HopCheck.On) HopCheck.R(HopCheck.NtRect, r, new Rect(-Pivot.x * sx, -Pivot.y * sy, sx, sy));
            m.Rect = r; m.RectKey = k; return r;
        }
    }
    public Vector3 EffectiveLocalPosition
    {
        get
        {
            if (!IsRect) return LocalPosition;
            var pr = ParentRectF;
            // Mathf.LerpUnclamped(a, b, t) = a + (b - a) * t; then rect.x + rect.width * ax + AnchoredPosition.x (x = m_XMin, width = m_Width)
            var q = _rect; float ax = Um.LerpUnclamped(q.AMin.x, q.AMax.x, q.Piv.x), ay = Um.LerpUnclamped(q.AMin.y, q.AMax.y, q.Piv.y);
            var r = Um.V3(pr.X + pr.W * ax + q.APos.x, pr.Y + pr.H * ay + q.APos.y, _lp.z);
            if (HopCheck.On) HopCheck.V3(HopCheck.NtElp, r, OldEffectiveLocalPosition());
            return r;
        }
        set
        {
            if (!IsRect) { LocalPosition = value; return; }
            var pr = ParentRectF;
            var q = _rect; float ax = Um.LerpUnclamped(q.AMin.x, q.AMax.x, q.Piv.x), ay = Um.LerpUnclamped(q.AMin.y, q.AMax.y, q.Piv.y);
            var ap = Um.V2(value.x - (pr.X + pr.W * ax), value.y - (pr.Y + pr.H * ay));
            if (HopCheck.On)
            {
                var u = ParentRect; float ux = Mathf.LerpUnclamped(AnchorMin.x, AnchorMax.x, Pivot.x), uy = Mathf.LerpUnclamped(AnchorMin.y, AnchorMax.y, Pivot.y);
                HopCheck.V2(HopCheck.NtElpSet, ap, new Vector2(value.x - (u.x + u.width * ux), value.y - (u.y + u.height * uy)));
                HopCheck.V3(HopCheck.NtElpSet, Um.V3(_lp.x, _lp.y, value.z), new Vector3(LocalPosition.x, LocalPosition.y, value.z));
            }
            AnchoredPosition = ap;
            LocalPosition = Um.V3(LocalPosition.x, LocalPosition.y, value.z);
        }
    }
    /// <summary>The getter as it was (Unity's LerpUnclamped, Rect getters and Vector3 constructor): SHEPHERD_HOPFIX_CHECK=1 only.</summary>
    Vector3 OldEffectiveLocalPosition()
    {
        var pr = ParentRect;
        float ax = Mathf.LerpUnclamped(AnchorMin.x, AnchorMax.x, Pivot.x), ay = Mathf.LerpUnclamped(AnchorMin.y, AnchorMax.y, Pivot.y);
        return new Vector3(pr.x + pr.width * ax + AnchoredPosition.x, pr.y + pr.height * ay + AnchoredPosition.y, LocalPosition.z);
    }

    /// <summary>TRS of the local state. Memo key: the record's own version (a plain transform's TRS reads only its own fields), or
    /// the world stamp for a rect transform (its position also reads the parent's rect).</summary>
    public Matrix4x4 LocalMatrix
    {
        get
        {
            var m = M_(); long k = IsRect ? WorldStamp() : m.Ver; if (m.LocalKey == k) return m.Local;
            var r = M.TRS(EffectiveLocalPosition, LocalRotation, LocalScale);
            m.Local = r; m.LocalKey = k; return r;
        }
    }
    /// <summary>The same product as before, parent by parent from the leaf up (Mul(P1.Local, Leaf), then Mul(P2.Local, that), ...:
    /// float products are not associative, so a parent's world matrix is never reused for a child), memoized by the world stamp.</summary>
    public Matrix4x4 LocalToWorld
    {
        get
        {
            var mm = M_(); long k = WorldStamp(); if (mm.LtwKey == k) return mm.Ltw;
            var m = LocalMatrix; for (var p = Parent; p != null; p = p.Parent) m = M.Mul(p.LocalMatrix, m);
            mm.Ltw = m; mm.LtwKey = k; return m;
        }
    }
    public Matrix4x4 WorldToLocal
    {
        get
        {
            var mm = M_(); long k = WorldStamp(); if (mm.W2lKey == k) return mm.W2l;
            var r = M.Inverse(LocalToWorld); mm.W2l = r; mm.W2lKey = k; return r;
        }
    }
    public Quaternion Rotation
    {
        get
        {
            var mm = M_(); long k = WorldStamp(); if (mm.RotKey == k) return mm.Rot;
            var q = LocalRotation; for (var p = Parent; p != null; p = p.Parent) q = M.QMul(p.LocalRotation, q);
            mm.Rot = q; mm.RotKey = k; return q;
        }
    }
    public Vector3 Position
    {
        get
        {
            // menu-speed (top hotspots): memoized by the world stamp (the parent's world matrix and the effective local position -- own state and,
            // for a rect transform, the parent rect -- are functions of the chain it stamps)
            if (HotCfgOn) { var mm = M_(); long k = WorldStamp(); if (mm.PosKey == k) { if (Shepherd.Engine.Render.HotCfg.Check) Shepherd.Engine.Render.HotCheck.V3(Shepherd.Engine.Render.HotCheck.Position, mm.Pos, PositionOf()); return mm.Pos; } var p = PositionOf(); mm.Pos = p; mm.PosKey = k; return p; }
            return PositionOf();
        }
    }
    Vector3 PositionOf()
    {
        if (Parent == null && HopCheck.On) HopCheck.M(HopCheck.Identity, Um.Identity, Matrix4x4.identity);
        return M.Point(Parent?.LocalToWorld ?? Um.Identity, EffectiveLocalPosition);   // (Um.Identity: was Matrix4x4.identity, the same 16 floats)
    }
    public Vector3 LossyScale
    {
        get
        {
            var mm = M_(); long k = WorldStamp(); if (mm.LossyKey == k) return mm.Lossy;
            var s = LocalScale;
            for (var p = Parent; p != null; p = p.Parent)
            {   // Vector3.Scale(p.LocalScale, s) = (a.x * b.x, a.y * b.y, a.z * b.z)
                var n = Um.Scale(p._ls, s);
                if (HopCheck.On) HopCheck.V3(HopCheck.NtLossy, n, Vector3.Scale(p.LocalScale, s));
                s = n;
            }
            mm.Lossy = s; mm.LossyKey = k; return s;
        }
    }
    /// <summary>menu-speed (top hotspots): <see cref="LossyScale"/> boxed, the same box while its memo key is unchanged (Transform.lossyScale:
    /// ~50 boxes per idle Main Menu frame, 17 Spine LOD managers asking every frame).</summary>
    internal object LossyScaleBox()
    {
        var v = LossyScale; if (!HotCfgOn) return v;
        var mm = _m; var bx = mm.Bx ??= new BoxSet();   // (LossyScale made the memo current: LossyKey is this value's key)
        if (bx.LossyKey != mm.LossyKey || bx.Lossy == null) { bx.Lossy = v; bx.LossyKey = mm.LossyKey; }
        if (Shepherd.Engine.Render.HotCfg.Check) Shepherd.Engine.Render.HotCheck.V3(Shepherd.Engine.Render.HotCheck.Boxes, (Vector3)bx.Lossy, v);
        return bx.Lossy;
    }
    /// <summary>menu-speed (top hotspots): f(Position) boxed, the same box while the position's memo key is unchanged (Renderer.bounds: the
    /// engine's Bounds(position, Vector3.one)); f must be a pure function of the position.</summary>
    internal object PositionBox(Func<Vector3, object> f)
    {
        var p = Position; var mm = _m; var bx = mm.Bx ??= new BoxSet();   // (Position made the memo current: PosKey is this value's key)
        if (bx.BoundsKey != mm.PosKey || bx.Bounds == null) { bx.Bounds = f(p); bx.BoundsKey = mm.PosKey; }
        else if (Shepherd.Engine.Render.HotCfg.Check) Shepherd.Engine.Render.HotCheck.Same(Shepherd.Engine.Render.HotCheck.Boxes, bx.Bounds, f(p));
        return bx.Bounds;
    }
    public void SetPosition(Vector3 world) { EffectiveLocalPosition = Parent == null ? world : M.Point(Parent.WorldToLocal, world); MarkChanged(); }
    public void SetRotation(Quaternion world) { LocalRotation = Parent == null ? world : M.QMul(M.QInv(Parent.Rotation), world); MarkChanged(); }
    /// <summary>Transform.hasChanged for this subtree. Physics stage 0's hook (plan 1.5, the corrected guard): an interested transform is
    /// dirtied unless it is the one transform physics itself is writing; its descendants are visited as before (a child body follows by
    /// H-CHILDSYNC = follow). Inert in stage 0 (PhysWorld.Dirty only feeds SHEPHERD_CHECK_PHYS=sync).</summary>
    public void MarkChanged()
    {
        if (!HcLazy) { HcMarks++; MarkEager(); return; }
        HcMarks++;
        _hcMark = HcTick();                   // kit #9: this transform and its subtree changed now (the lazy hasChanged)
        if (HcCheck) HcEagerFlags();          // (SHEPHERD_CHECK_PHYS=lazy: the eager flags too)
        MarkPhys();
    }
    /// <summary>Kit #8's MarkChanged exactly (SHEPHERD_LAZY_HASCHANGED=0): the flag on every transform of the subtree, the dirty hook on each.</summary>
    void MarkEager() { HcVisits++; HasChanged = true; if (PhysInterest != 0 && !ReferenceEquals(this, Physics.PhysWorld.WritingTransform)) Physics.PhysWorld.Dirty(this); foreach (var c in Children) c.MarkEager(); }
    /// <summary>Kit #9: the dirty hook's walk alone -- into the children whose subtree holds an interested transform (PhysDesc > 0); the interested
    /// transforms are reached in the eager walk's order, so the dirty queue is the same.</summary>
    void MarkPhys() { HcVisits++; if (PhysInterest != 0 && !ReferenceEquals(this, Physics.PhysWorld.WritingTransform)) Physics.PhysWorld.Dirty(this); foreach (var c in Children) if (c.PhysDesc > 0) c.MarkPhys(); }
    void HcEagerFlags() { _hc = true; foreach (var c in Children) c.HcEagerFlags(); }
    public NTransform Root { get { var t = this; while (t.Parent != null) t = t.Parent; return t; } }
    public bool IsChildOf(NTransform other) { for (var t = this; t != null; t = t.Parent) if (t == other) return true; return false; }
}

/// <summary>Bit-for-bit equality of float structs (V6 stage 2 memo keys): +0 and -0 differ, a NaN equals the same NaN.</summary>
public static unsafe class Bits
{
    public static bool Same(Vector2 a, Vector2 b) => *(uint*)&a.x == *(uint*)&b.x && *(uint*)&a.y == *(uint*)&b.y;
    public static bool Same(Vector3 a, Vector3 b) => *(uint*)&a.x == *(uint*)&b.x && *(uint*)&a.y == *(uint*)&b.y && *(uint*)&a.z == *(uint*)&b.z;
    public static bool Same(Quaternion a, Quaternion b) => *(uint*)&a.x == *(uint*)&b.x && *(uint*)&a.y == *(uint*)&b.y && *(uint*)&a.z == *(uint*)&b.z && *(uint*)&a.w == *(uint*)&b.w;
}

public static class Objects
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_CachedPtr")] static extern ref IntPtr CachedPtr(UObject o);

    static readonly Dictionary<IntPtr, NObj> ByPtr = new();
    public static readonly Dictionary<int, NObj> ById = new();
    /// <summary>kit6 mem, lever L4b (vita/notes/kit6-mem.md): the record of a native block is found from the block itself instead of the
    /// pointer dictionary ByPtr (the intro's 188,778 live records: 6.19 MiB of entries + buckets on 32-bit Mono). Bind writes the record's index
    /// in <see cref="_byIdx"/> into the block at +12 (Unity's managed code reads only the instance id at +8; the rest of the block is ours);
    /// Get(o) reads it and returns the record only if that record is alive and its block IS o's m_CachedPtr -- the same answer as ByPtr[p] in
    /// every case: a pointer to a freed block that malloc gave to a newer record finds that record (its index is there), any other stale
    /// pointer finds nothing (no live record has that block). A record bound twice keeps its earlier block mapped to it, as ByPtr kept it.
    /// SHEPHERD_PTR_INDEX=0: ByPtr as in kit #5.</summary>
    public static readonly bool PtrIndex = Environment.GetEnvironmentVariable("SHEPHERD_PTR_INDEX") != "0";
    const int IdxAt = 12;
    /// <summary>kit6 mem, lever L4d: the native block is 16 bytes, not 32: the only reads of it are Unity's GetInstanceID (m_CachedPtr + 8,
    /// GetOffsetOfInstanceIDInCPlusPlusObject = 8; a scan of every DLL in the game's Managed folder finds no other use of m_CachedPtr than
    /// GetInstanceID and GetCachedPtr) and the engine's own (+8, and L4b's index at +12). Each live record's block is a newlib allocation:
    /// malloc(16) takes a 24-byte chunk instead of 40 (the intro's 188,778 records: ~2.9 MiB of newlib, the menu's ~48,000: ~0.7 MiB).
    /// SHEPHERD_NOBJ_BLOCK=32: kit #5's size.</summary>
    public static readonly int BlockBytes = Environment.GetEnvironmentVariable("SHEPHERD_NOBJ_BLOCK") == "32" ? 32 : 16;
    static NObj[] _byIdx = new NObj[1024]; static int _idxUsed; static readonly Stack<int> _idxFree = new();
    static Dictionary<IntPtr, NObj> _rebound;
    static int _nextRuntimeId = -2, _nextAssetId = 2;
#if !SHEPHERD_NO_K13WALKC
    public static int LiveCount => ById.Count - _k13Tombs;

    // ---- kit #13 walkc: Unity's UnloadUnusedAssets (Runtime/K13Unload.cs). A released asset record leaves a TOMBSTONE in ById: its key and
    // its entry stay (the shared, never bound, destroyed record below), so no Dictionary entry is freed and every later Bind takes the slot it
    // took in kit #12 (ById's enumeration order -- FindObjectsOfType's -- unchanged). When the game asks for that asset again, the new record
    // takes the tombstone's id and entry back (K13Rekey): Unity keeps a persistent object's instance id across an unload and a reload. Every
    // enumeration of ById already skips destroyed records; LiveCount does not count tombstones. (No string literal here: Assets/Diet.cs.) ----
    internal static readonly NObj K13Tomb = new() { Destroyed = true };
    static int _k13Tombs;
    internal static int K13Tombs => _k13Tombs;
    /// <summary>A candidate leaves ById / the block index for one collection (its entry keeps the tombstone): the block index slot, or -1.</summary>
    internal static int K13Detach(NObj n)
    {
        ById[n.InstanceId] = K13Tomb;   // (an existing key: the entry is written in place, its index unchanged)
        if (!PtrIndex || n.Ptr == IntPtr.Zero) return -1;
        int i; unsafe { i = *(int*)(n.Ptr + IdxAt); }
        if ((uint)i < (uint)_idxUsed && ReferenceEquals(_byIdx[i], n)) { _byIdx[i] = null; return i; }
        return -1;
    }
    /// <summary>The candidate is still reached: back exactly where it was.</summary>
    internal static void K13Restore(NObj n, int idx) { ById[n.InstanceId] = n; if (idx >= 0) _byIdx[idx] = n; }
    /// <summary>The candidate's record and managed object were collected: its tombstone stays in ById, its block index and native block are
    /// freed (nothing can point at the block: its managed object is gone), its name leaves the GameObject name filter.</summary>
    internal static void K13Drop(int idx, IntPtr ptr, string goName)
    {
        _k13Tombs++;
        if (idx >= 0) _idxFree.Push(idx);
        if (goName != null) GoNameRemove(goName);
        if (ptr != IntPtr.Zero) GiveBlock(ptr);
    }
    /// <summary>A released asset made again (Loader.LiveSet refills its live place): the record bound a moment ago under a new id moves to
    /// the tombstone's id and entry. Its fresh entry goes back exactly as Kill frees one (the Dictionary's free list and the slot mirror
    /// agree: the next Bind takes that entry, which is the one it would have taken without this Bind), and the id counter steps back when
    /// that id was the last one handed out -- so ids, entries and slots are kit #12's (a record never released). 0 = done.</summary>
    internal static int K13Rekey(NObj n, int id, int slot)
    {
        if (n.Destroyed || n.Slot < 0 || !ById.TryGetValue(id, out var t) || !ReferenceEquals(t, K13Tomb) || !ById.TryGetValue(n.InstanceId, out var cur) || !ReferenceEquals(cur, n)) return -1;
        int fresh = n.InstanceId;
        if (ById.Remove(fresh)) _freeSlots.Push(n.Slot);
        if (fresh == _nextAssetId) _nextAssetId -= 2; else if (fresh == _nextRuntimeId) _nextRuntimeId += 2;
        ById[id] = n; _k13Tombs--;
        n.InstanceId = id; n.Slot = slot;
        if (n.Ptr != IntPtr.Zero) unsafe { *(int*)(n.Ptr + 8) = id; }
        return 0;
    }
#else
    public static int LiveCount => ById.Count;
#endif

    public static T Bind<T>(UObject managed, T n, bool asset = false) where T : NObj
    {
        if (n.Slot >= 0) SlotsExact = false;   // bound twice: its old id stays in ById, which the slot mirror does not model
        n.InstanceId = asset ? (_nextAssetId += 2) : (_nextRuntimeId -= 2);
        if (PtrIndex && n.Ptr != IntPtr.Zero) (_rebound ??= new())[n.Ptr] = n;   // bound twice: the earlier block keeps finding n (ByPtr never dropped it)
        n.Ptr = BlockRecycle && _freeBlocks.Count > 0 ? TakeBlock() : NewBlock();   // kit #9 engine (SHEPHERD_K9_RECYCLE): a dead record's block first (cleared below, as a new one)
        unsafe { if (BlockSlab) *(long*)(n.Ptr + 8) = 0; else new Span<byte>((void*)n.Ptr, BlockBytes).Clear(); *(int*)(n.Ptr + 8) = n.InstanceId; }
        n.Managed = managed; CachedPtr(managed) = n.Ptr;
        if (PtrIndex)
        {
            int i = _idxFree.Count > 0 ? _idxFree.Pop() : _idxUsed++;
            if (i >= _byIdx.Length) Array.Resize(ref _byIdx, _byIdx.Length * 2);
            _byIdx[i] = n; unsafe { *(int*)(n.Ptr + IdxAt) = i; }
        }
        else ByPtr[n.Ptr] = n;
        int before = ById.Count; ById[n.InstanceId] = n;
        if (ById.Count != before + 1) SlotsExact = false;   // an existing key overwritten in place (ids are unique: not expected)
        n.Slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _slotCount++;   // Dictionary's entry for the new key (Registry)
        Registry.Bound(n);
        if (n is NTransform t) t.NrcDirty();   // menu-speed (top hotspots): a new transform is a NotifyRectChanges candidate
        else if (n is NGameObject) GoNameAdd(n.Name);
        return n;
    }

    // ---- V6 stage 2: Dictionary<int, NObj>'s entry order, mirrored (Registry) ----
    // .NET 8 Dictionary: Remove pushes the freed entry on a LIFO free list; an insert takes the most recently freed entry, else
    // the next unused one at the end; enumeration (ValueCollection.CopyTo / the enumerator) walks the entries in index order;
    // Resize copies them in place. NObj.Slot is that index.
    static int _slotCount; static readonly Stack<int> _freeSlots = new();
    /// <summary>False once ById was changed in a way the mirror does not model; Registry then rebuilds its lists from full scans.</summary>
    public static bool SlotsExact = true;

    public static NObj Get(object o)
    {
        if (o is not UObject u || (object)u == null) return null;
        var p = CachedPtr(u);
        if (p == IntPtr.Zero) return null;
        if (PtrIndex)
        {
            int i; unsafe { i = *(int*)(p + IdxAt); }
            if ((uint)i < (uint)_idxUsed && _byIdx[i] is { } r && r.Ptr == p) return r;
            return _rebound != null && _rebound.TryGetValue(p, out var o2) ? o2 : null;
        }
        return ByPtr.TryGetValue(p, out var n) ? n : null;
    }
    public static T Get<T>(object o) where T : NObj => Get(o) as T;
    public static IntPtr PtrOf(UObject o) => CachedPtr(o);

    const int CtorWays = 16; static readonly Type[] CtorTypes = new Type[CtorWays]; static readonly System.Reflection.ConstructorInfo[] CtorInfos = new System.Reflection.ConstructorInfo[CtorWays];
    /// <summary>kit #5 menus: the constructor of every type RunCtor has seen (the 16-way cache above missed on most of a sub-menu's ~1,000
    /// behaviour types: Settings' opening looked a constructor up 4,227 times, 25 ms of the Mac frame). The same constructor per type.</summary>
    static readonly Dictionary<Type, System.Reflection.ConstructorInfo> CtorAll = new();
    public static UObject NewManaged(Type t) => (UObject)RuntimeHelpers.GetUninitializedObject(t);

    /// <summary>Run the managed parameterless constructor on an already-bound object (field initializers), like Unity.</summary>
    public static void RunCtor(UObject o)
    {
        var t = o.GetType(); int k = RuntimeHelpers.GetHashCode(t) & (CtorWays - 1);   // (menu-speed: the last types' constructors, kept: the lookup
        System.Reflection.ConstructorInfo ctor;                                          //  allocated its candidate list every call)
        if (ReferenceEquals(CtorTypes[k], t)) ctor = CtorInfos[k];
        else if (Runtime.MenusCfg.TypeMemos && CtorAll.TryGetValue(t, out ctor)) { CtorTypes[k] = t; CtorInfos[k] = ctor; }   // kit #5 menus: every type's, kept
        else
        {
            ctor = t.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes); CtorTypes[k] = t; CtorInfos[k] = ctor;
            if (Runtime.MenusCfg.TypeMemos) CtorAll[t] = ctor;
        }
        if (ctor == null) return;
        try { ctor.Invoke(o, null); } catch (Exception e) { Log.Exception(e, $"ctor {o.GetType().FullName}"); }
    }

    public static void Kill(NObj n)
    {
        if (n.Destroyed) return;
        if (n is NGameObject && n.Slot >= 0) GoNameRemove(n.Name);   // (counted at its Bind)
        if (n is NGameObject kg) Runtime.TagIndex.Killed(kg);   // kit #8 speed: out of the tag index
        n.Destroyed = true;
        if (n is NTexture { IsRenderTexture: true } rt) Render.SceneRenderer.ReleaseRt(rt);   // polish round 2: free its render target
        if (n is NComponent && Registry.IsSpriteShapeRenderer(n)) Impl.SpriteShapeIcalls.Forget(n);   // fidelity round 1: its geometry buffers
        if (n is Physics.NRigidbody2D or Physics.NCollider2D or Physics.NCollider3D) Physics.PhysWorld.Destroying(n);   // physics stage 0: the membership hook
        if (n.Managed is not null) CachedPtr(n.Managed) = IntPtr.Zero;
        if (PtrIndex) { int i; unsafe { i = *(int*)(n.Ptr + IdxAt); } if ((uint)i < (uint)_idxUsed && ReferenceEquals(_byIdx[i], n)) { _byIdx[i] = null; _idxFree.Push(i); } }
        else ByPtr.Remove(n.Ptr);
        if (ById.Remove(n.InstanceId)) _freeSlots.Push(n.Slot);
        Registry.Killed(n);
        if (n is NTransform kt) kt.RecycleMemo();   // kit #9 engine (SHEPHERD_K9_RECYCLE)
        GiveBlock(n.Ptr);
        n.Ptr = IntPtr.Zero;
    }
    // ---- kit #9 engine (SHEPHERD_K9_RECYCLE): a dead record's native block serves the next record bound ----
    // A block is only ever read at +8 (the instance id: Unity's GetInstanceID) and +12 (the record index, L4b), both written by Bind after it
    // clears the block, and a dead record's managed object no longer points at it (Kill clears m_CachedPtr first): handing the block to the
    // next Bind is what malloc may do with it anyway (Get's rule already covers a reused block), without the malloc / free pair. At most 8,192
    // blocks wait (196 KiB of newlib); the purgeable arena frees them on a scene load.
    static readonly bool BlockRecycle = Runtime.K9Engine.Recycle || (Runtime.K14Newlib.Blocks && BlockBytes == 16);   // (kit #14: BlockSlab's blocks are never freed: they come back here whatever SHEPHERD_K9_RECYCLE says)
    static readonly Stack<IntPtr> _freeBlocks = new();
    static IntPtr TakeBlock() { Runtime.K9Engine.Recycled++; return _freeBlocks.Pop(); }
    // ---- kit #14 newlib (vita/notes/kit14-newlib.md, SHEPHERD_K14_BLOCKS): 8 bytes per record out of 64-KiB slabs. Only +8 and +12 of a block are ever
    // read (the kit #9 note above; L4d's scan of the game's DLLs), so a block is those two ints: the pointer handed out is the slab position - 8.
    // A malloc(16) took a 24-byte newlib chunk: 190,000 records on the walk, 4.6 MiB; here 1.5 MiB. A freed block waits for the next Bind, whatever
    // SHEPHERD_K9_RECYCLE says (no cap: a slab never goes back). Nothing reads a pointer's value but Get and the managed m_CachedPtr's "is it zero" and + 8. Off = kit #13's malloc.
    static readonly bool BlockSlab = Runtime.K14Newlib.Blocks && BlockBytes == 16;
    const int SlabBytes = 65536;
    static IntPtr _slabNext, _slabEnd;
    static IntPtr NewBlock()
    {
        if (!BlockSlab) return Marshal.AllocHGlobal(BlockBytes);
        if (_slabNext == _slabEnd) { _slabNext = Marshal.AllocHGlobal(SlabBytes); _slabEnd = _slabNext + SlabBytes; }
        var p = _slabNext - 8; _slabNext += 8; return p;
    }
    static void GiveBlock(IntPtr p)
    {
        if (BlockSlab || (BlockRecycle && _freeBlocks.Count < 8192)) _freeBlocks.Push(p); else Marshal.FreeHGlobal(p);
    }
    /// <summary>The purgeable arena: the waiting blocks back to newlib, the snapshot buffers and transform memos dropped (rebuilt on next use).</summary>
    internal static long PurgeCaches(bool all, int frame)
    {
        long bytes = 0;
        if (all) { while (!BlockSlab && _freeBlocks.Count > 0) { Marshal.FreeHGlobal(_freeBlocks.Pop()); bytes += 24; } bytes += NTransform.PurgeMemos() * 368L; }
        lock (SnapPool)
        {
            if (all || frame - _bigRent > 300) while (SnapPool.Count > 0) bytes += 16 + 4L * SnapPool.Pop().Length;
            if (all) while (SmallSnapPool.Count > 0) bytes += 16 + 4L * SmallSnapPool.Pop().Length;
        }
        return bytes;
    }
    static int _bigRent;

    public static IEnumerable<NObj> All => ById.Values;
    /// <summary>Kit #9 engine: the hierarchy epoch reached 2^30 -- every transform's activeInHierarchy stamp is cleared and the epoch restarts at 1
    /// (an epoch value is never reused while a stamp could still hold it).</summary>
    internal static void ClearAhStamps() { foreach (var n in ById.Values) if (n is NTransform t) t.ClearAhStamp(); NTransform.HierEpoch = 1; }

    // ---- menu-speed (top hotspots): GameObject names ----
    /// <summary>A counting filter of the names of the bound, live GameObject records (Bind adds, Kill and a rename remove): 71,424 byte counters,
    /// two per name from its FNV-1a hash (a counter that reaches 255 stays there: "maybe present" for good). Every GameObject that GameObject.Find
    /// can reach is one of those records (a destroyed GameObject is unlinked from its parent / scene with its whole subtree; inside that destruction,
    /// before the unlink, Find does not use the filter: Lifecycle.DestroyDepth), so a name with a zero counter cannot be found: Beffio's Stylizer asks Find("Pixelator") every frame, a miss that walked every active object (the GameObject it makes
    /// under that name lives only part of the frame, so a filter of every name ever seen kept the walk). 70 KiB in nine arrays under the GC's
    /// large-object size, allocated once (a dictionary of the ~10,000 names grew through the boot's peak in large-object space: +1 MiB at the
    /// vita-sim heap-cap edge).</summary>
    const int NameChunk = 7936, NameSlots = 9 * NameChunk;
    static readonly byte[][] GoNameCnt = MakeNameCounters();
    static byte[][] MakeNameCounters() { var c = new byte[9][]; for (int i = 0; i < 9; i++) c[i] = new byte[NameChunk]; return c; }
    static uint NameHash(string n) { uint h = 2166136261; foreach (char c in n) { h ^= c; h *= 16777619; } return h; }
    static ref byte NameSlot(uint i) => ref GoNameCnt[i / NameChunk][i % NameChunk];
    static void Slots2(string n, out uint a, out uint b) { uint h = NameHash(n); a = h % NameSlots; b = (h * 0x9E3779B1u >> 7) % NameSlots; }
    internal static void GoNameAdd(string n)
    {
        if (n == null) return;
        Slots2(n, out var a, out var b); ref var x = ref NameSlot(a); if (x < 255) x++; ref var y = ref NameSlot(b); if (y < 255) y++;
    }
    internal static void GoNameRemove(string n)
    {
        if (n == null) return;
        Slots2(n, out var a, out var b); ref var x = ref NameSlot(a); if (x > 0 && x < 255) x--; ref var y = ref NameSlot(b); if (y > 0 && y < 255) y--;
    }
    /// <summary>Some bound, live GameObject may be named n (false: certainly none is).</summary>
    public static bool AnyGameObjectNamed(string n)
    {
        if (n == null) return false;
        Slots2(n, out var a, out var b); return NameSlot(a) != 0 && NameSlot(b) != 0;
    }

    /// <summary>A copy of <see cref="All"/> for scans whose body may create or destroy records (the per-frame passes:
    /// AnimatorRuntime.UpdateAll, LayoutRootCanvases, NotifyRectChanges, DrawWorld, FindObjectsOfType): the same elements in
    /// the same order as <c>All.ToArray()</c> (both are <c>ById.Values.CopyTo</c>), but in a pooled buffer. V6 Stage A
    /// measured those ToArray copies at 48.6k records x 8 B each, ~2 MiB/frame straight into the large-object space and a
    /// major GC every ~30 frames (vita/notes/v6-mac-sim.md section 3). Use it only as <c>foreach (var n in Objects.Snapshot())</c>:
    /// foreach disposes the enumerator when the loop ends (break and throw included), which clears the used slots (the buffer
    /// keeps nothing alive) and hands the buffer back. A nested scan rents a second buffer.</summary>
    public static Snap Snapshot()
    {
        int n = ById.Count; var a = Rent(n);
        ById.Values.CopyTo(a, 0);
        return new Snap(a, n);
    }
    /// <summary>A pooled copy of a Registry list (the same contract as <see cref="Snapshot"/>).</summary>
    internal static Snap SnapshotOf(List<NObj> items)
    {
        int n = items.Count; var a = Rent(n);
        items.CopyTo(a, 0);
        return new Snap(a, n);
    }
    // two size classes, so the small Registry copies never take (and drop) the big whole-registry buffers
    const int BigSnap = 16384;
    static readonly Stack<NObj[]> SnapPool = new(), SmallSnapPool = new();
    static NObj[] Rent(int n)
    {
        NObj[] a = null; var pool = n >= BigSnap ? SnapPool : SmallSnapPool;
        if (n >= BigSnap) _bigRent = Runtime.Time.FrameCount;   // (kit #9 purgeable arena: a big buffer not asked for in 300 frames is dropped)
        lock (SnapPool) if (pool.Count > 0) a = pool.Pop();
        if (a == null || a.Length < n) a = new NObj[Math.Max(1024, n + n / 4)];   // headroom: the count grows in steps while scenes load
        return a;
    }
    // kit #6 toggles (vita/notes/kit6-toggles.md, "the snapshot pool"): a buffer goes back to the pool its Rent looked in -- chosen by the
    // snapshot's count, as Rent chooses. By its length (kit #5), a copy of 13,108..16,383 records (a small-class Rent, but n + n/4 >= 16,384)
    // went to the big pool and was never found again: a new NObj[n + n/4] (64-87 KiB, large-object space) at every such snapshot -- the
    // Settings menu's open / close and scrolling frames (Mac menu tour: 259 such buffers, 9 with the fix). SHEPHERD_K6_SNAPPOOL=0: by length.
    static void GiveBack(NObj[] a, int n) { Array.Clear(a, 0, n); var pool = (Runtime.K6Cfg.SnapByCount ? n : a.Length) >= BigSnap ? SnapPool : SmallSnapPool; lock (SnapPool) if (pool.Count < 4) pool.Push(a); }

    public readonly struct Snap
    {
        readonly NObj[] _a; readonly int _n;
        internal Snap(NObj[] a, int n) { _a = a; _n = n; }
        public int Count => _n;
        public Enumerator GetEnumerator() => new(_a, _n);
        public struct Enumerator : IDisposable
        {
            NObj[] _a; readonly int _n; int _i;
            internal Enumerator(NObj[] a, int n) { _a = a; _n = n; _i = -1; }
            public NObj Current => _a[_i];
            public bool MoveNext() => ++_i < _n;
            public void Dispose() { var a = _a; _a = null; if (a != null) GiveBack(a, _n); }
        }
    }
}

/// <summary>Math in Unity's conventions (column vectors, left-handed). Kept self-contained so engine code never
/// re-enters our own Quaternion/Matrix hooks. menu-speed (hop fix): results are built with field stores (Um.V3 / Um.Q) instead of
/// Unity's Vector3 / Quaternion constructors (each was an AOT -> interpreter call); the arithmetic is unchanged.</summary>
public static class M
{
    public static Quaternion QMul(Quaternion l, Quaternion r)
    {
        float x = l.w * r.x + l.x * r.w + l.y * r.z - l.z * r.y, y = l.w * r.y + l.y * r.w + l.z * r.x - l.x * r.z,
            z = l.w * r.z + l.z * r.w + l.x * r.y - l.y * r.x, w = l.w * r.w - l.x * r.x - l.y * r.y - l.z * r.z;
        if (HopCheck.On) HopCheck.Q(HopCheck.Quat, Um.Q(x, y, z, w), new Quaternion(x, y, z, w));
        return Um.Q(x, y, z, w);
    }
    public static Quaternion QInv(Quaternion q)
    {
        float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (n < 1e-12f) { if (HopCheck.On) HopCheck.Q(HopCheck.Quat, Um.QIdentity, Quaternion.identity); return Um.QIdentity; }
        float x = -q.x / n, y = -q.y / n, z = -q.z / n, w = q.w / n;
        if (HopCheck.On) HopCheck.Q(HopCheck.Quat, Um.Q(x, y, z, w), new Quaternion(x, y, z, w));
        return Um.Q(x, y, z, w);
    }
    public static Quaternion QNorm(Quaternion q)
    {
        float n = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (n < 1e-12f) { if (HopCheck.On) HopCheck.Q(HopCheck.Quat, Um.QIdentity, Quaternion.identity); return Um.QIdentity; }
        float x = q.x / n, y = q.y / n, z = q.z / n, w = q.w / n;
        if (HopCheck.On) HopCheck.Q(HopCheck.Quat, Um.Q(x, y, z, w), new Quaternion(x, y, z, w));
        return Um.Q(x, y, z, w);
    }
    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f, xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y, xz = q.x * z, yz = q.y * z, wx = q.w * x, wy = q.w * y, wz = q.w * z;
        float rx = (1f - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z, ry = (xy + wz) * v.x + (1f - (xx + zz)) * v.y + (yz - wx) * v.z, rz = (xz - wy) * v.x + (yz + wx) * v.y + (1f - (xx + yy)) * v.z;
        if (HopCheck.On) HopCheck.V3(HopCheck.MRotate, Um.V3(rx, ry, rz), new Vector3(rx, ry, rz));
        return Um.V3(rx, ry, rz);
    }
    public static Matrix4x4 TRS(Vector3 t, Quaternion q, Vector3 s)
    {
        q = QNorm(q);
        var a0 = Um.V3(s.x, 0f, 0f); var a1 = Um.V3(0f, s.y, 0f); var a2 = Um.V3(0f, 0f, s.z);   // (were new Vector3(s.x, 0, 0) ...)
        if (HopCheck.On) { HopCheck.V3(HopCheck.MTrs, a0, new Vector3(s.x, 0, 0)); HopCheck.V3(HopCheck.MTrs, a1, new Vector3(0, s.y, 0)); HopCheck.V3(HopCheck.MTrs, a2, new Vector3(0, 0, s.z)); }
        var c0 = Rotate(q, a0); var c1 = Rotate(q, a1); var c2 = Rotate(q, a2);
        var m = new Matrix4x4();
        m.m00 = c0.x; m.m10 = c0.y; m.m20 = c0.z; m.m01 = c1.x; m.m11 = c1.y; m.m21 = c1.z; m.m02 = c2.x; m.m12 = c2.y; m.m22 = c2.z;
        m.m03 = t.x; m.m13 = t.y; m.m23 = t.z; m.m33 = 1f; return m;
    }
    /// <summary>a * b. The same float arithmetic as the original loop (<c>s = 0; s += a[i,k] * b[k,j]</c> for k = 0..3, left to right,
    /// so every element is bit-identical), on the fields instead of the Matrix4x4 indexer: the indexer's switch was ~800k
    /// interpreted calls per menu frame (V6 stage 2, vita/notes/engine-pass-speed.md).</summary>
    public static Matrix4x4 Mul(Matrix4x4 a, Matrix4x4 b)
    {
        var r = new Matrix4x4();
        r.m00 = 0f + a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
        r.m01 = 0f + a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
        r.m02 = 0f + a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
        r.m03 = 0f + a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
        r.m10 = 0f + a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
        r.m11 = 0f + a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
        r.m12 = 0f + a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
        r.m13 = 0f + a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
        r.m20 = 0f + a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
        r.m21 = 0f + a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
        r.m22 = 0f + a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
        r.m23 = 0f + a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
        r.m30 = 0f + a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30;
        r.m31 = 0f + a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31;
        r.m32 = 0f + a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32;
        r.m33 = 0f + a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33;
        return r;
    }
    public static Vector3 Point(Matrix4x4 m, Vector3 p)
    {
        float x = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03, y = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13, z = m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23;
        if (HopCheck.On) HopCheck.V3(HopCheck.MPoint, Um.V3(x, y, z), new Vector3(x, y, z));
        return Um.V3(x, y, z);
    }
    public static Vector3 Vec(Matrix4x4 m, Vector3 p)
    {
        float x = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z, y = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z, z = m.m20 * p.x + m.m21 * p.y + m.m22 * p.z;
        if (HopCheck.On) HopCheck.V3(HopCheck.MVec, Um.V3(x, y, z), new Vector3(x, y, z));
        return Um.V3(x, y, z);
    }
    public static Matrix4x4 Inverse(Matrix4x4 m)
    {
        // column-major copy (a[i] = m[i % 4, i / 4]) in locals: the same double arithmetic as the array version, no allocation
        double a0 = m.m00, a1 = m.m10, a2 = m.m20, a3 = m.m30, a4 = m.m01, a5 = m.m11, a6 = m.m21, a7 = m.m31, a8 = m.m02, a9 = m.m12, a10 = m.m22, a11 = m.m32, a12 = m.m03, a13 = m.m13, a14 = m.m23, a15 = m.m33;
        double inv0 = a5 * a10 * a15 - a5 * a11 * a14 - a9 * a6 * a15 + a9 * a7 * a14 + a13 * a6 * a11 - a13 * a7 * a10;
        double inv4 = -a4 * a10 * a15 + a4 * a11 * a14 + a8 * a6 * a15 - a8 * a7 * a14 - a12 * a6 * a11 + a12 * a7 * a10;
        double inv8 = a4 * a9 * a15 - a4 * a11 * a13 - a8 * a5 * a15 + a8 * a7 * a13 + a12 * a5 * a11 - a12 * a7 * a9;
        double inv12 = -a4 * a9 * a14 + a4 * a10 * a13 + a8 * a5 * a14 - a8 * a6 * a13 - a12 * a5 * a10 + a12 * a6 * a9;
        double inv1 = -a1 * a10 * a15 + a1 * a11 * a14 + a9 * a2 * a15 - a9 * a3 * a14 - a13 * a2 * a11 + a13 * a3 * a10;
        double inv5 = a0 * a10 * a15 - a0 * a11 * a14 - a8 * a2 * a15 + a8 * a3 * a14 + a12 * a2 * a11 - a12 * a3 * a10;
        double inv9 = -a0 * a9 * a15 + a0 * a11 * a13 + a8 * a1 * a15 - a8 * a3 * a13 - a12 * a1 * a11 + a12 * a3 * a9;
        double inv13 = a0 * a9 * a14 - a0 * a10 * a13 - a8 * a1 * a14 + a8 * a2 * a13 + a12 * a1 * a10 - a12 * a2 * a9;
        double inv2 = a1 * a6 * a15 - a1 * a7 * a14 - a5 * a2 * a15 + a5 * a3 * a14 + a13 * a2 * a7 - a13 * a3 * a6;
        double inv6 = -a0 * a6 * a15 + a0 * a7 * a14 + a4 * a2 * a15 - a4 * a3 * a14 - a12 * a2 * a7 + a12 * a3 * a6;
        double inv10 = a0 * a5 * a15 - a0 * a7 * a13 - a4 * a1 * a15 + a4 * a3 * a13 + a12 * a1 * a7 - a12 * a3 * a5;
        double inv14 = -a0 * a5 * a14 + a0 * a6 * a13 + a4 * a1 * a14 - a4 * a2 * a13 - a12 * a1 * a6 + a12 * a2 * a5;
        double inv3 = -a1 * a6 * a11 + a1 * a7 * a10 + a5 * a2 * a11 - a5 * a3 * a10 - a9 * a2 * a7 + a9 * a3 * a6;
        double inv7 = a0 * a6 * a11 - a0 * a7 * a10 - a4 * a2 * a11 + a4 * a3 * a10 + a8 * a2 * a7 - a8 * a3 * a6;
        double inv11 = -a0 * a5 * a11 + a0 * a7 * a9 + a4 * a1 * a11 - a4 * a3 * a9 - a8 * a1 * a7 + a8 * a3 * a5;
        double inv15 = a0 * a5 * a10 - a0 * a6 * a9 - a4 * a1 * a10 + a4 * a2 * a9 + a8 * a1 * a6 - a8 * a2 * a5;
        double det = a0 * inv0 + a1 * inv4 + a2 * inv8 + a3 * inv12;
        var r = new Matrix4x4(); if (Math.Abs(det) < 1e-20) return r;
        r.m00 = (float)(inv0 / det); r.m10 = (float)(inv1 / det); r.m20 = (float)(inv2 / det); r.m30 = (float)(inv3 / det); r.m01 = (float)(inv4 / det); r.m11 = (float)(inv5 / det); r.m21 = (float)(inv6 / det); r.m31 = (float)(inv7 / det); r.m02 = (float)(inv8 / det); r.m12 = (float)(inv9 / det); r.m22 = (float)(inv10 / det); r.m32 = (float)(inv11 / det); r.m03 = (float)(inv12 / det); r.m13 = (float)(inv13 / det); r.m23 = (float)(inv14 / det); r.m33 = (float)(inv15 / det);
        return r;
    }
    public static Quaternion Euler(Vector3 rad)   // Unity order: Z, then X, then Y (applied as Y*X*Z)
    {
        float cx = MathF.Cos(rad.x * .5f), sx = MathF.Sin(rad.x * .5f), cy = MathF.Cos(rad.y * .5f), sy = MathF.Sin(rad.y * .5f), cz = MathF.Cos(rad.z * .5f), sz = MathF.Sin(rad.z * .5f);
        var qx = Um.Q(sx, 0f, 0f, cx); var qy = Um.Q(0f, sy, 0f, cy); var qz = Um.Q(0f, 0f, sz, cz);   // (were new Quaternion(sx, 0, 0, cx) ...)
        if (HopCheck.On) { HopCheck.Q(HopCheck.Quat, qx, new Quaternion(sx, 0, 0, cx)); HopCheck.Q(HopCheck.Quat, qy, new Quaternion(0, sy, 0, cy)); HopCheck.Q(HopCheck.Quat, qz, new Quaternion(0, 0, sz, cz)); }
        return QMul(QMul(qy, qx), qz);
    }
    public static Vector3 ToEuler(Quaternion q)    // inverse of Euler (YXZ), radians
    {
        q = QNorm(q);
        float sinx = 2f * (q.w * q.x - q.y * q.z); sinx = Math.Clamp(sinx, -1f, 1f);
        float x = MathF.Asin(sinx), y, z;
        if (MathF.Abs(sinx) < 0.9999f) { y = MathF.Atan2(2f * (q.w * q.y + q.x * q.z), 1f - 2f * (q.x * q.x + q.y * q.y)); z = MathF.Atan2(2f * (q.w * q.z + q.x * q.y), 1f - 2f * (q.x * q.x + q.z * q.z)); }
        else { y = MathF.Atan2(-2f * (q.x * q.z - q.w * q.y), 1f - 2f * (q.y * q.y + q.z * q.z)); z = 0; }
        if (HopCheck.On) HopCheck.V3(HopCheck.Vec, Um.V3(x, y, z), new Vector3(x, y, z));
        return Um.V3(x, y, z);
    }
}
