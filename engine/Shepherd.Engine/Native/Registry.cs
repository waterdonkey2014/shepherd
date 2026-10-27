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
    public enum Kind { Transforms, Cameras, Canvases, Animators, WorldRenderers, Stylizers, CanvasGroups, SortingGroups, Lights, Colliders2D, Bodies2D }
    public static readonly Registry Transforms = new(Kind.Transforms), Cameras = new(Kind.Cameras), Canvases = new(Kind.Canvases),
        Animators = new(Kind.Animators), WorldRenderers = new(Kind.WorldRenderers), Stylizers = new(Kind.Stylizers), CanvasGroups = new(Kind.CanvasGroups),
        SortingGroups = new(Kind.SortingGroups),   // polish round 1: SceneRenderer's world sort (none in the menu scenes)
        Lights = new(Kind.Lights),                 // fidelity round 1: the lit shaders' main directional light (SceneRenderer.Shading)
        Colliders2D = new(Kind.Colliders2D),       // fidelity round 1: trigger callbacks (Runtime/Triggers2D.cs)
        Bodies2D = new(Kind.Bodies2D);             // fid1-fixup D6: Triggers2D's body cache (its Version: a Rigidbody2D bound or destroyed)
    static readonly Registry[] All = { Transforms, Cameras, Canvases, Animators, WorldRenderers, Stylizers, CanvasGroups, SortingGroups, Lights, Colliders2D, Bodies2D };

    public readonly Kind Of; readonly int _bit;
    readonly List<NObj> _items = new();   // live members, ascending Slot
    int _cursor;                           // next entry of the bind log this list has not classified (meaningful once _used)
    bool _used;                            // read at least once: classifies the log from then on (else left out of Trim)
    /// <summary>Bumped whenever the member set changes (a user keeps something derived from the members).</summary>
    public int Version { get { Drain(); return _version; } }
    int _version;
    Registry(Kind k) { Of = k; _bit = 1 << (int)k; }
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
        if (drop == Log.Count) Log.Clear(); else Log.RemoveRange(0, drop);
        _logBase = min;
    }
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
        foreach (var r in All) if (r._used) r.Drain();
        Trim();
    }
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
        foreach (var n in _items) n.RegMask &= ~_bit;
        _items.Clear(); _version++;
        foreach (var n in Objects.All) if (Is(n)) { _items.Add(n); n.RegMask |= _bit; }
        _cursor = _logBase + Log.Count;
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
}
