using UnityEngine;
namespace Shepherd.Engine.Native;

/// <summary>V6 stage 2 (vita/notes/engine-pass-speed.md): maintained lists of the records the per-frame passes look for, so
/// they stop scanning every live record (~48k at the menu, several scans per frame: LayoutRootCanvases, NotifyRectChanges,
/// DrawWorld per camera, DrawOverlayCanvases, AllCameras, the Stylizer lookup, AnimatorRuntime.UpdateAll).
///
/// Each list holds exactly the live records of one kind, in <b>the order <see cref="Objects.All"/> enumerates them</b>, so a
/// pass that walks a list instead of the whole registry visits the same records in the same order (and its sorts break ties
/// the same way). That order is Dictionary&lt;int, NObj&gt;'s entry order, which <see cref="Objects"/> mirrors in
/// <see cref="NObj.Slot"/>: .NET 8's Dictionary puts a new key in the most recently freed entry (a LIFO free list) or, with none
/// free, at the end; enumeration walks the entries in index order; Resize keeps the indices. ById is only changed by
/// Objects.Bind / Kill (fresh unique ids, never overwritten, never cleared), so the mirror is exact; if a record were ever
/// bound twice the mirror stops being trusted (<see cref="Objects.SlotsExact"/>) and every list is rebuilt from a full scan
/// (the old behaviour). SHEPHERD_CHECK_REG=1 compares every list against the full scan at every use (RegistryCheck).
///
/// Records are classified lazily: Bind only appends to a log, and a list classifies the new records the first time it is
/// used after them, so each type test (Camera, Canvas, ...) first runs where the old scans first ran it.
///
/// fid1-fixup D2 (vita/notes/fidelity-round1.md): the log must not outlive a frame. It holds every record bound since the slowest
/// list last classified, destroyed records included, so a list read only on some code paths (Lights: lit GL frames only) held
/// the whole log alive on every other path (+2.2 MiB live at the menu: fidverify/summary.md D2). The rule now, for every list
/// present and future: a list takes part in the log only once it has been read (<see cref="_used"/>); until then it is left out
/// of Trim's minimum and its first read rebuilds it from a full scan (the same members in the same order: the slot mirror's
/// premise), so its type test still first runs where it is first needed. From its first read on, <see cref="EndFrame"/>
/// classifies the frame's new records into it at the end of every frame, read or not, and the log is emptied.</summary>
public sealed class Registry
{
    public enum Kind { Transforms, Cameras, Canvases, Animators, WorldRenderers, Stylizers, CanvasGroups, SortingGroups, Lights, Colliders2D, Bodies2D, Shaders, Typed }
    public static readonly Registry Transforms = new(Kind.Transforms), Cameras = new(Kind.Cameras), Canvases = new(Kind.Canvases),
        Animators = new(Kind.Animators), WorldRenderers = new(Kind.WorldRenderers), Stylizers = new(Kind.Stylizers), CanvasGroups = new(Kind.CanvasGroups),
        SortingGroups = new(Kind.SortingGroups),   // polish round 1: SceneRenderer's world sort (none in the menu scenes)
        Lights = new(Kind.Lights),                 // fidelity round 1: the lit shaders' main directional light (SceneRenderer.Shading)
        Colliders2D = new(Kind.Colliders2D),       // fidelity round 1: trigger callbacks (Runtime/Triggers2D.cs)
        Bodies2D = new(Kind.Bodies2D),             // fid1-fixup D6: Triggers2D's body cache (its Version: a Rigidbody2D bound or destroyed)
        Shaders = new(Kind.Shaders);               // kit #9 engine (SHEPHERD_K9_FIND): Shader.Find's records (never read with the row off: inert)
    /// <summary>Every list (kit #9 engine: not readonly -- the per-type lists of <see cref="ForType"/> join and leave it). Kit #9 review fix: the
    /// shader records join only with the find row on (SHEPHERD_K9_FIND): with it off the array is kit #8's eleven lists, so Drain's one-Trim-per-
    /// round cadence (All.Length drains) and RegistryCheck's log state are kit #8's too.</summary>
    static Registry[] All = Runtime.K9Engine.Find
        ? new[] { Transforms, Cameras, Canvases, Animators, WorldRenderers, Stylizers, CanvasGroups, SortingGroups, Lights, Colliders2D, Bodies2D, Shaders }
        : new[] { Transforms, Cameras, Canvases, Animators, WorldRenderers, Stylizers, CanvasGroups, SortingGroups, Lights, Colliders2D, Bodies2D };

    public readonly Kind Of; readonly int _bit;
    readonly Type _type; int _lastRead;   // kit #9 engine: a per-type list's type, and the frame it was last asked for
    readonly byte _gate;                  // kit #9 review fix: a per-type list's exact prefilter (TypedIs): 1 = t is a MonoBehaviour class, 2 = a Component class, 0 = none
    bool _overCap;                        // kit #9 review fix: a per-type list grew past TypedCap (dropped at its next use or the next slot 0; the type is then scanned)
    readonly List<NObj> _items = new();   // live members, ascending Slot
    int _cursor;                           // next entry of the bind log this list has not classified (meaningful once _used)
    bool _used;                            // read at least once: classifies the log from then on (else left out of Trim)
    /// <summary>Bumped whenever the member set changes (a user keeps something derived from the members).</summary>
    public int Version { get { Drain(); return _version; } }
    int _version;
    Registry(Kind k, Type t = null, int bit = -1)
    {
        Of = k; _bit = 1 << (bit < 0 ? (int)k : bit); _type = t;
        if (t != null && !t.IsInterface) _gate = typeof(MonoBehaviour).IsAssignableFrom(t) ? (byte)1 : typeof(Component).IsAssignableFrom(t) ? (byte)2 : (byte)0;
    }
    public int Count { get { Drain(); return _items.Count; } }

    // ---- the bind log (records bound since the slowest list last drained) ----
    static readonly List<NObj> Log = new();
    static int _logBase;                   // Log[0] is bind number _logBase

    internal static void Bound(NObj n)
    {
        Log.Add(n);
        if (Log.Count >= 262144) { foreach (var r in All) if (r._used) r.Drain(); Trim(); }   // one frame binding this many: classify now so the log can be trimmed
    }
    /// <summary>Drops the log entries every list in use has classified (each Drain of the lists makes one Trim per round of drains;
    /// <see cref="EndFrame"/> one per frame). A list never read yet does not hold the log: its first read rebuilds it.</summary>
    static void Trim()
    {
        int end = _logBase + Log.Count, min = end;
        foreach (var r in All) if (r._used) min = Math.Min(min, r._cursor);
        int drop = min - _logBase; if (drop <= 0) return;
        if (drop == Log.Count) { Log.Clear(); if (LogTrim && Log.Capacity > LogKeepMax) Log.Capacity = LogKeep; } else Log.RemoveRange(0, drop);
        _logBase = min;
    }
    /// <summary>Kit #11 walk (the intro's load peak; vita/notes/kit11-walk.md): a frame that binds a scene's worth of records (the intro's load:
    /// ~250 k) grows the bind log to 262,144 entries -- a 1 MiB array that List.Clear kept for the rest of the session. When the log is empty
    /// and its array is above 256 KiB it is cut back to 32 KiB (the log's contents and order are untouched: only its spare room goes).
    /// SHEPHERD_K11_TRIM=0 (or SHEPHERD_K11_WALK=0) = kit #10 (the array kept).</summary>
    static readonly bool LogTrim = Environment.GetEnvironmentVariable("SHEPHERD_K11_TRIM") != "0" && Environment.GetEnvironmentVariable("SHEPHERD_K11_WALK") != "0";
    const int LogKeepMax = 65536, LogKeep = 8192;
    /// <summary>fid1-fixup D2: the end of every frame (Lifecycle.Frame). Every list read at least once classifies the frame's new
    /// records (exactly what its next read would do: the members, their order and the type tests are the same), then the log is
    /// emptied, so no list can keep destroyed records alive past a frame however rarely it is read. Without the slot mirror every
    /// read rebuilds from a full scan anyway, so the log is just dropped.</summary>
    public static void EndFrame()
    {
        if (Log.Count == 0) return;
        if (!Objects.SlotsExact)
        {
            _logBase += Log.Count; Log.Clear();
            foreach (var r in All) r._cursor = _logBase;
            return;
        }
        if (_typed.Count > 0 && Runtime.K9Engine.FindJoint)
        {   // kit #9 review fix: the per-type lists' classification of the frame's records in one pass (timed on the Mac only: TypedTicks)
#if !SHEPHERD_EBOOT
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            DrainTypedJoint();
#if !SHEPHERD_EBOOT
            Runtime.K9Engine.TypedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
#endif
        }
#if !SHEPHERD_EBOOT
        else if (_typed.Count > 0)
        {   // (Mac only, SHEPHERD_K9_FIND_JOINT=0: the same drains in the same order, the per-type lists' share timed)
            foreach (var r in All)
            {
                if (!r._used) continue;
                if (r.Of != Kind.Typed) { r.Drain(); continue; }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); r.Drain(); Runtime.K9Engine.TypedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }
            Trim(); return;
        }
#endif
        foreach (var r in All) if (r._used) r.Drain();   // (after the joint pass the per-type lists have nothing left: each drain only counts towards the Trim cadence, as before)
        Trim();
    }
#if !SHEPHERD_NO_K13WALKC
    /// <summary>Kit #13 walkc (Runtime/K13Unload.cs): for one collection no list may hold an asset record the release tests (a list would keep it
    /// alive): every list is emptied and the bind log dropped -- the state the slot mirror's full-scan path starts from -- and the lists that had
    /// been read are rebuilt from the full scan right after the collection (<see cref="K13Rebuild"/>): the same members in the same order,
    /// minus the released records (their tombstones are destroyed). Returns the lists to rebuild.</summary>
    internal static Registry[] K13Detach()
    {
        int k = 0; foreach (var r in All) if (r._used) k++;
        var used = new Registry[k]; k = 0;
        foreach (var r in All) { if (r._used) used[k++] = r; foreach (var n in r._items) n.RegMask &= ~r._bit; r._items.Clear(); r._version++; }
        _logBase += Log.Count; Log.Clear();
        foreach (var r in All) r._cursor = _logBase;
        return used;
    }
    internal static void K13Rebuild(Registry[] used) { foreach (var r in used) if (r._used) r.Rebuild(); }
#endif
    internal static void Killed(NObj n)
    {
        if (n.RegMask == 0) return;
        foreach (var r in All) if ((n.RegMask & r._bit) != 0) r.Remove(n);
        n.RegMask = 0;
    }

    bool Is(NObj n) => Of switch
    {
        Kind.Transforms => n is NTransform,
        Kind.Cameras => IsCamera(n),
        Kind.Canvases => IsCanvas(n),
        Kind.Animators => IsAnimator(n),
        Kind.WorldRenderers => IsWorldRenderer(n),
        Kind.Stylizers => IsStylizer(n),
        Kind.SortingGroups => IsSortingGroup(n),
        Kind.Lights => IsLight(n),
        Kind.Colliders2D => IsCollider2D(n),
        Kind.Bodies2D => IsBody2D(n),
        Kind.Shaders => n is NShader,
        Kind.Typed => n.Managed is { } m && TypedIs(m),
        _ => IsCanvasGroup(n),
    };
    // one type test per method: the interpreter / JIT resolves a method's type tokens when it compiles it, so each test loads its
    // Unity module no earlier than the scan it replaces did
    static bool IsCamera(NObj n) => n is NBehaviour && n.Managed is Camera;
    static bool IsCanvas(NObj n) => n is NBehaviour && n.Managed is Canvas;
    static bool IsAnimator(NObj n) => n is NBehaviour && n.Managed is Animator;
    static bool IsWorldRenderer(NObj n) => n is NComponent && (n.Managed is SpriteRenderer || n.Managed is MeshRenderer || IsSpriteShapeRenderer(n));
    /// <summary>Fidelity round 1: UnityEngine.U2D.SpriteShapeRenderer, tested by name (as IsStylizer) so the scan does not load the
    /// SpriteShape module earlier than the game does.</summary>
    internal static bool IsSpriteShapeRenderer(NObj n)
    {
        if (n.Managed is null) return false;
        var t = n.Managed.GetType();   // fid1-fixup D2: per type, not per record (it runs for every component bound and killed)
        return TypeFlag(t, SsrKnown, SsrYes, static t => t.Name == "SpriteShapeRenderer");
    }
    /// <summary>fid1-fixup round 2: the by-name type tests' results, one entry per type for both tests (was a dictionary per test).</summary>
    static readonly Dictionary<Type, byte> _typeFlags = new();
    const byte SsrKnown = 1, SsrYes = 2, ColKnown = 4, ColYes = 8;
    static bool TypeFlag(Type t, byte known, byte yes, Func<Type, bool> test)
    {
        lock (_typeFlags)
        {
            _typeFlags.TryGetValue(t, out var f);
            if ((f & known) == 0) { f |= known; if (test(t)) f |= yes; _typeFlags[t] = f; }
            return (f & yes) != 0;
        }
    }
    static bool IsCanvasGroup(NObj n) => n is NComponent && n.Managed is CanvasGroup;
    static bool IsSortingGroup(NObj n) => n is NBehaviour && n.Managed is UnityEngine.Rendering.SortingGroup;
    static bool IsLight(NObj n) => n is NBehaviour && n.Managed is Light;
    /// <summary>UnityEngine.Rigidbody2D (a Component). First used by Triggers2D once a collider exists, so the Physics2D module is loaded by then.</summary>
    static bool IsBody2D(NObj n) => n is NComponent && n.Managed is Rigidbody2D;
    /// <summary>UnityEngine.Collider2D and its subclasses, by name along the base chain (cached per type), so classifying does not load
    /// the Physics2D module before the game does.</summary>
    static bool IsCollider2D(NObj n)
    {
        if (n is not NBehaviour || n.Managed is null) return false;
        return TypeFlag(n.Managed.GetType(), ColKnown, ColYes, static t =>
        {
            for (var b = t; b != null; b = b.BaseType) if (b.Name == "Collider2D" && b.Namespace == "UnityEngine") return true;
            return false;
        });
    }
    static bool IsStylizer(NObj n) => n is NMono && n.Managed is not null && n.Managed.GetType().Name == "Stylizer";   // StylizerPass.Find's test

    void Drain()
    {
        if (!Objects.SlotsExact) { Rebuild(); return; }
        if (!_used) { _used = true; Rebuild(); return; }   // first read: the log may already be trimmed past what this list never saw
        int end = _logBase + Log.Count;
        for (int i = _cursor; i < end; i++)
        {
            var n = Log[i - _logBase];
            if (n.Destroyed || (n.RegMask & _bit) != 0 || !Is(n)) continue;
            Insert(n); n.RegMask |= _bit;
        }
        _cursor = end;
        if (Of == Kind.Typed && _items.Count > TypedCap) _overCap = true;   // kit #9 review fix: the cap holds after the list was made, too
        if (Log.Count > 0 && ++_drains >= All.Length) { _drains = 0; Trim(); }   // keep the log short within a frame too: it holds destroyed records alive
    }
    static int _drains;
    void Insert(NObj n)
    {
        int lo = 0, hi = _items.Count;
        _version++;
        if (hi == 0 || _items[hi - 1].Slot < n.Slot) { _items.Add(n); return; }   // the common case: a fresh entry at the end
        while (lo < hi) { int mid = (lo + hi) >> 1; if (_items[mid].Slot < n.Slot) lo = mid + 1; else hi = mid; }
        _items.Insert(lo, n);
    }
    void Remove(NObj n)
    {
        _version++;
        int lo = 0, hi = _items.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1; int s = _items[mid].Slot;
            if (s == n.Slot) { if (ReferenceEquals(_items[mid], n)) _items.RemoveAt(mid); else _items.Remove(n); return; }
            if (s < n.Slot) lo = mid + 1; else hi = mid - 1;
        }
        _items.Remove(n);
    }
    /// <summary>Slot mirror not trusted: the members are the full scan's, in its order (the pre-stage-2 behaviour).</summary>
    void Rebuild()
    {
        long s0 = Runtime.K9Engine.TypedSeen, t0 = Runtime.K9Engine.TypedTests;   // (kit #9 review fix: a per-type list's build is counted apart)
        foreach (var n in _items) n.RegMask &= ~_bit;
        _items.Clear(); _version++;
        foreach (var n in Objects.All) if (Is(n)) { _items.Add(n); n.RegMask |= _bit; }
        _cursor = _logBase + Log.Count;
        if (Of == Kind.Typed)
        {
            if (_items.Count > TypedCap) _overCap = true;
            Runtime.K9Engine.TypedBuilds++; Runtime.K9Engine.TypedBuilt += Runtime.K9Engine.TypedSeen - s0;   // a build = one scan (kit #8's cost per find)
            Runtime.K9Engine.TypedSeen = s0; Runtime.K9Engine.TypedTests = t0;
        }
    }

    /// <summary>The members, in Objects.All order, as a pooled copy (the same contract as <see cref="Objects.Snapshot"/>: use it
    /// only in a foreach; records created during the loop are not visited, records destroyed during it still are).</summary>
    public Objects.Snap Snapshot()
    {
        Drain();
        if (RegistryCheck.On) RegistryCheck.Compare(this, _items);
        return Objects.SnapshotOf(_items);
    }
    /// <summary>The live member list itself (read-only use, no record created or destroyed while it is walked).</summary>
    public List<NObj> Items()
    {
        Drain();
        if (RegistryCheck.On) RegistryCheck.Compare(this, _items);
        return _items;
    }
    /// <summary>For RegistryCheck (which formats it: no string literals in this non-profile class, see Assets/Diet.cs): the bind log's
    /// length and each list's lag behind its end.</summary>
    internal static (int log, (Kind kind, int lag)[] lags) LogState() => (Log.Count, All.Select(r => (r.Of, r._used ? _logBase + Log.Count - r._cursor : -1)).ToArray());   // -1: never read
    /// <summary>For SimStats' heap line: how many records in the bind log are destroyed (held alive by it until the next Trim).</summary>
    internal static int LogDestroyed() { int d = 0; foreach (var n in Log) if (n.Destroyed) d++; return d; }
    /// <summary>menu-speed (top hotspots): n is a member of this list (after a Drain: Items / Snapshot / Count).</summary>
    internal bool Has(NObj n) => (n.RegMask & _bit) != 0;
    /// <summary>For RegistryCheck: the membership test.</summary>
    internal bool Member(NObj n) => Is(n);

    // ---- kit #9 engine (SHEPHERD_K9_FIND): per-type lists, the find indexes ----
    // Object.FindObjectsOfType(t) / FindObjectsByType / Resources.FindObjectsOfTypeAll scanned every live record (~190k in the intro) with a
    // reflection type test. A per-type list holds exactly the live records whose managed object is a t, in Objects.All's order -- the
    // registry's own rules (bind log, slot order, removed at Kill, rebuilt from a full scan when the slot mirror is not trusted) -- so a find
    // that walks it instead visits the scan's matches in the scan's order. Up to 20 types at a time (RegMask bits 12..31); a list nobody asked
    // for in 600 frames leaves (its next use rebuilds it from a full scan: the same members). A type matching more than TypedCap records is not
    // indexed (memory, and the per-frame classification below): checked when the list is made and (review fix) whenever it grows -- a list
    // past the cap is dropped at its next use or the next slot 0 of the housekeeping cycle, and the type is scanned from then on (kit #8).
    // Cost (review fix, counted in the [k9] end line): from its first read on, a list classifies every record bound (Registry.EndFrame, the D2
    // rule) -- at the end of a frame all lists in one joint pass (DrainTypedJoint, below: one mask lookup per record); a read inside a frame
    // drains its own list, where TypedIs rejects every record of another class family with one class test before the reflection test.
    static readonly Dictionary<Type, Registry> _typed = new();
    static readonly HashSet<Type> _tooBroad = new();
    static int _freeBits = unchecked((int)0xFFFFF000);
    const int TypedCap = 4096;
    public static int TypedLists => _typed.Count;
    /// <summary>Kit #9 review fix: exactly `t.IsInstanceOfType(m)` -- an instance of a MonoBehaviour (Component) class is a MonoBehaviour
    /// (Component), so the class test rejects every other record without the reflection call (FindAllFast's own filter); an object of exactly
    /// type t needs no reflection either.</summary>
    bool TypedIs(object m)
    {
        Runtime.K9Engine.TypedSeen++;
        if (_gate == 1 ? m is not MonoBehaviour : _gate == 2 && m is not Component) return false;
        Runtime.K9Engine.TypedTests++;
        return m.GetType() == _type || _type.IsInstanceOfType(m);
    }
    // Review fix (the classification's cost at a scene load: every live list re-tested every record bound -- 14 lists x the menu load's 18.5 k
    // records): EndFrame classifies the frame's records for every per-type list in ONE pass over the log. A list's test (IsInstanceOfType)
    // depends only on the record's runtime type, so it is computed once per (runtime type, list) and kept as a bit mask per runtime type
    // (cleared whenever a list is made or dropped); each record costs one lookup. Each list gets exactly the records its own Drain would insert
    // -- from its cursor on, live, not yet a member, its bit set -- and Insert keeps slot order: the members and their order are the same.
    // SHEPHERD_K9_FIND_JOINT=0 (test knob): each list drains itself (c0b7463).
    static readonly Registry[] _byBit = new Registry[32];
    static readonly Dictionary<Type, int> _typeMask = new();
    static int TypeMask(object m)
    {
        var t = m.GetType();
        if (_typeMask.TryGetValue(t, out int k)) return k;
        foreach (var r in _typed.Values) { Runtime.K9Engine.TypedTests++; if (r._type.IsInstanceOfType(m)) k |= r._bit; }
        _typeMask[t] = k; return k;
    }
    static void DrainTypedJoint()
    {
        int end = _logBase + Log.Count, from = end;
        foreach (var r in _typed.Values) if (r._used && r._cursor < from) from = r._cursor;
        for (int a = from; a < end; a++)
        {
            var n = Log[a - _logBase];
            Runtime.K9Engine.TypedSeen++;
            if (n.Destroyed || n.Managed is not { } m) continue;
            for (int mask = TypeMask(m); mask != 0; mask &= mask - 1)
            {
                var r = _byBit[System.Numerics.BitOperations.TrailingZeroCount(mask)];
                if (r == null || !r._used || a < r._cursor || (n.RegMask & r._bit) != 0) continue;
                r.Insert(n); n.RegMask |= r._bit;
            }
        }
        foreach (var r in _typed.Values) if (r._used) { r._cursor = end; if (r._items.Count > TypedCap) r._overCap = true; }
    }
    /// <summary>The list for type t (made on first use), or null: no free bit, or t matches too many records.</summary>
    internal static Registry ForType(Type t, int frame)
    {
        if (_typed.TryGetValue(t, out var r))
        {
            if (!r._overCap) { r._lastRead = frame; return r; }
            _tooBroad.Add(t); r.DropTyped(); return null;   // (review fix: it grew past the cap since it was made -- scanned from now on)
        }
        if (_freeBits == 0 || _tooBroad.Contains(t)) return null;
        int b = System.Numerics.BitOperations.TrailingZeroCount(_freeBits); _freeBits &= ~(1 << b);
        r = new Registry(Kind.Typed, t, b) { _lastRead = frame };
        var a = new Registry[All.Length + 1]; Array.Copy(All, a, All.Length); a[^1] = r; All = a;
        _typed[t] = r; _byBit[b] = r; _typeMask.Clear();
        if (r.Count > TypedCap) { _tooBroad.Add(t); r.DropTyped(); return null; }   // (Count: the first read builds it)
        return r;
    }
    /// <summary>The housekeeping cycle: per-type lists not asked for in <paramref name="idle"/> frames leave (members unmarked, bit freed); so does
    /// (review fix) a list that grew past the cap, and its type is scanned from then on.</summary>
    internal static void AgeTyped(int frame, int idle)
    {
        if (_typed.Count == 0) return;
        List<Registry> old = null;
        foreach (var r in _typed.Values) if (r._overCap || frame - r._lastRead > idle) (old ??= new()).Add(r);
        if (old != null) foreach (var r in old) { if (r._overCap) _tooBroad.Add(r._type); r.DropTyped(); }
    }
    void DropTyped()
    {
        foreach (var n in _items) n.RegMask &= ~_bit;
        _items.Clear(); _used = false;
        int k = Array.IndexOf(All, this);
        if (k >= 0) { var a = new Registry[All.Length - 1]; Array.Copy(All, a, k); Array.Copy(All, k + 1, a, k, All.Length - k - 1); All = a; }
        _typed.Remove(_type); _freeBits |= _bit;
        _byBit[System.Numerics.BitOperations.TrailingZeroCount(_bit)] = null; _typeMask.Clear();
        Trim();
    }
}
