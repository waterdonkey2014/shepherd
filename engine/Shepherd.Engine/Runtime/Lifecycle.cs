using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Shepherd.Engine.Native;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;

/// <summary>Unity's script lifecycle + player loop: Awake/OnEnable/Start/Update/LateUpdate/FixedUpdate/OnDisable/OnDestroy,
/// coroutines, Invoke, deferred Destroy. Exceptions in callbacks are logged and swallowed, like Unity.</summary>
public static class Lifecycle
{
    sealed class Msgs
    {
        public MethodInfo Awake, OnEnable, Start, Update, LateUpdate, FixedUpdate, OnDisable, OnDestroy, OnApplicationQuit;
        public int Order;
        public bool K6;   // kit #6 toggles: the Graphics tab (Runtime/VitaToggles.cs adds its rows right after this type's Start)
        public bool K8;   // kit #8 play: a type whose Awake the track follows (Runtime/K8Play.cs: the PC-only links)
        public int[] K7;  // kit #7 profiler: this type's ids per phase (FixedUpdate, Update, LateUpdate, Start); made on first use with the profiler on
        public int Empty; // kit #8 speed: the phase messages dropped because their IL is only 'ret' (bits: 1 FixedUpdate, 2 Update, 4 LateUpdate)
    }
    static readonly Dictionary<Type, Msgs> MsgCache = new();
    static readonly string[] Names = { "Awake", "OnEnable", "Start", "Update", "LateUpdate", "FixedUpdate", "OnDisable", "OnDestroy", "OnApplicationQuit" };

    static Msgs For(Type t)
    {
        if (MsgCache.TryGetValue(t, out var m)) return m;
        m = new Msgs();
        foreach (var name in Names)
        {
            MethodInfo found = null;
            for (var c = t; c != null && c != typeof(MonoBehaviour) && c != typeof(ScriptableObject) && c != typeof(Behaviour); c = c.BaseType)
            {
                found = c.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (found != null) break;
            }
            typeof(Msgs).GetField(name).SetValue(m, found);
        }
        m.Empty = K8Speed.DropEmpty(ref m.FixedUpdate, ref m.Update, ref m.LateUpdate);   // kit #8 speed: an empty phase message is never called (SHEPHERD_K8_SKIP_EMPTY=0: kit #7)
        var deo = t.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "DefaultExecutionOrder");
        if (deo != null) m.Order = (int)deo.GetType().GetProperty("order")!.GetValue(deo)!;
        if (t.FullName != null && Assets.Loader.ScriptOrders.TryGetValue(t.FullName.Replace('+', '.'), out var so)) m.Order = so;   // project setting wins
        m.K6 = VitaToggles.Watches(t);   // (the rows' switch and the FullScreen Mode fix's: Watches is false with both off)
        m.K8 = K8Play.Watches(t);        // kit #8 play (false with SHEPHERD_K8_PLAY=0 / SHEPHERD_PC_LINKS=1)
        return MsgCache[t] = m;
    }

    /// <summary>For(m.Managed.GetType()), remembered on the record (a record's managed type never changes; MsgCache entries are never
    /// replaced): the per-frame Update/LateUpdate/FixedUpdate loops asked the type dictionary once per behaviour per phase.</summary>
    static Msgs For(NMono m) => (Msgs)(m.LifecycleMsgs ??= For(m.Managed.GetType()));
    /// <summary>A copy of <see cref="Active"/> for one phase's loop (the old Active.ToArray(): the loop body may enable/disable
    /// behaviours), in a buffer kept per phase; cleared after the loop so it keeps nothing alive.</summary>
    static int Snapshot(ref NMono[] buf)
    {
        int n = Active.Count;
        if (buf == null || buf.Length < n) buf = new NMono[Math.Max(256, n + n / 4)];
        Active.CopyTo(buf); return n;
    }
    static NMono[] _fixedBuf, _updateBuf, _lateBuf;
    static Action[] _fixedD, _updateD, _lateD;
    // ---- menu-speed (top hotspots): per-phase member lists ----
    // Each phase loop visited every Active behaviour (~5,230 per idle Main Menu frame over the three phases) and called Call(m, null, ...) for the
    // ~4,600 whose type has no such message (Call returns at once: no effect). The phase's snapshot now holds only Active's members whose type
    // has the message, in Active's order -- kept per phase and rebuilt from Active whenever Active changed (every Add / Remove / Sort bumps
    // _activeVer) -- so the loop makes the same calls on the same behaviours in the same order, with the same per-visit tests (Started,
    // IsEnabledCalled, Destroyed). SHEPHERD_HOTSPOTS=0: the whole-Active snapshot as before.
    const int PhFixed = 0, PhUpdate = 1, PhLate = 2;
    static int _activeVer;
    // (arrays sized n + n/4 like the phase buffers, not Lists: a List's doubling would put the LateUpdate list -- ~600 at the menu -- in large-object space)
    static readonly NMono[][] _phase = { new NMono[64], new NMono[64], new NMono[64] };
    static readonly Action[][] _phaseD = { new Action[64], new Action[64], new Action[64] };   // parallel to _phase: each member's message as a bound delegate (null: Invoke)
    static readonly int[] _phaseN = new int[3], _phaseVer = { -1, -1, -1 }, _phaseSkip = new int[3];   // _phaseSkip: kit #8, Active's members whose phase message was dropped as empty (counted only)
    static MethodInfo PhaseMsg(Msgs g, int ph) => ph == PhFixed ? g.FixedUpdate : ph == PhUpdate ? g.Update : g.LateUpdate;
    static int PhaseSnapshot(int ph, ref NMono[] buf, ref Action[] dbuf)
    {
        if (!Shepherd.Engine.Render.HotCfg.On) return Snapshot(ref buf);
        if (_phaseVer[ph] != _activeVer)
        {   // (For(m): computed for every member by its Enable)
            int c = 0;
            if (!K8Speed.SkipEmpty) foreach (var m in Active) { if (PhaseMsg(For(m), ph) != null) c++; }
            else
            {   // kit #8 speed (review fix): the skipped-call count from the same pass (it was a third pass over Active, for a log number only)
                int bit = 1 << ph, sk = 0;
                foreach (var m in Active) { var g = For(m); if (PhaseMsg(g, ph) != null) c++; else if ((g.Empty & bit) != 0) sk++; }
                _phaseSkip[ph] = sk;
            }
            if (_phase[ph].Length < c) { _phase[ph] = new NMono[c + c / 4]; _phaseD[ph] = new Action[c + c / 4]; }
            var a = _phase[ph]; var ad = _phaseD[ph]; int k = 0;
            foreach (var m in Active) { var mi = PhaseMsg(For(m), ph); if (mi != null) { a[k] = m; ad[k++] = PhaseDelegate(m, mi, ph); } }
            Array.Clear(a, k, _phaseN[ph] > k ? _phaseN[ph] - k : 0); Array.Clear(ad, k, _phaseN[ph] > k ? _phaseN[ph] - k : 0);
            _phaseN[ph] = k; _phaseVer[ph] = _activeVer;
        }
        K8Speed.SkippedCalls += _phaseSkip[ph];
        int n = _phaseN[ph];
        if (buf == null || buf.Length < n) buf = new NMono[Math.Max(256, n + n / 4)];
        if (dbuf == null || dbuf.Length < n) dbuf = new Action[Math.Max(256, n + n / 4)];
        Array.Copy(_phase[ph], buf, n); Array.Copy(_phaseD[ph], dbuf, n);
        if (Shepherd.Engine.Render.HotCfg.Check) PhaseCheck(ph, buf, n);
        return n;
    }
    // ---- menu-speed (top hotspots): the phase messages through bound delegates ----
    // MethodInfo.Invoke validated the arguments and the target and went through the runtime-invoke wrapper for each of the ~600 messages per idle
    // Main Menu frame (~9 % of the engine-AOT frame before the interpreted callee ran). A message the loop would invoke -- the method For(type)
    // found, an instance method without parameters returning void -- is now called through an Action bound to the behaviour's managed object
    // (made once per behaviour and phase: CreateDelegate on a virtual method binds the target's override, as Invoke dispatches), checked to
    // still be bound to that object at each call. Exceptions: Invoke wrapped them in a TargetInvocationException, which Log.Exception
    // unwraps; the delegate's are the inner exception itself, and the one frame its stack trace has in addition (CallD, where it is caught) is
    // the one Log.Exception leaves out (HotCfg.IsCallFrame), so the logged lines are the same. Anything else (a coroutine-returning message, a
    // method CreateDelegate refuses) keeps Invoke.
    static readonly Action NoDelegate = () => { };
    static Action PhaseDelegate(NMono m, MethodInfo mi, int ph)
    {
        if (!Shepherd.Engine.Render.HotCfg.Delegates) return null;
        var g = For(m); bool one = (g.FixedUpdate != null ? 1 : 0) + (g.Update != null ? 1 : 0) + (g.LateUpdate != null ? 1 : 0) == 1;   // (the usual case: one phase message, kept as the Action itself)
        var d = one ? m.PhaseD as Action : (m.PhaseD as Action[])?[ph];
        if (d == null)
        {
            d = NoDelegate;
            if (!mi.IsStatic && mi.ReturnType == typeof(void) && !mi.ContainsGenericParameters && mi.GetParameters().Length == 0 && m.Managed != null)
                try { d = Delegate.CreateDelegate(typeof(Action), m.Managed, mi, false) as Action ?? NoDelegate; } catch (Exception) { d = NoDelegate; }
            if (one) m.PhaseD = d; else ((Action[])(m.PhaseD ??= new Action[3]))[ph] = d;
        }
        return ReferenceEquals(d, NoDelegate) || !ReferenceEquals(d.Target, m.Managed) ? null : d;
    }
    /// <summary>Call(n, m, what) for a phase message with its bound delegate d (null: Invoke, as Call).</summary>
    static void CallD(NMono n, MethodInfo m, Action d, string what)
    {
        if (d == null || !ReferenceEquals(d.Target, n.Managed)) { Call(n, m, what); return; }
        if (m == null || n.Destroyed) return;
        try { d(); }
        catch (Exception e) { Log.Exception(e, $"{what} {n.Managed?.GetType().FullName} '{n.Name}'"); }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (Mac test runs only)
#endif
    }
    /// <summary>Kit #7 (Diag/K7Prof.cs, SHEPHERD_K7_PROF): the profiler's id of m's message in phase ph (3 = Start).</summary>
    static int K7Id(NMono m, int ph)
    {
        var g = For(m); var a = g.K7 ??= new[] { -1, -1, -1, -1 };
        if (a[ph] < 0) a[ph] = Diag.K7Prof.TypeId(m.Managed.GetType(), ph);
        return Diag.K7Prof.Spine(a[ph], m);
    }
    static void PhaseCheck(int ph, NMono[] buf, int n) => Shepherd.Engine.Render.HotCheck.PhaseList(Active, buf, n, m => PhaseMsg(For(m), ph) != null);   // (its own method: the closure is made only in check runs)

    public static readonly List<NMono> Active = new();       // enabled && activeInHierarchy (Update set)
    static readonly List<NMono> PendingStart = new();
    static List<NMono> _startsOrig;
    static void StartsCheck(NMono[] starts, int n) => _startsOrig = starts.Take(n).ToArray().OrderBy(x => For(x.Managed.GetType()).Order).ToList();
    const int StartsSmall = 32; static readonly NMono[] _starts = new NMono[StartsSmall]; static readonly int[] _startKeys = new int[StartsSmall];
    // kit #8 speed (lever 2, "no LINQ re-sort"): the same path for any number of pending Starts, in buffers kept between frames (grown, never
    // shrunk). In the intro thousands of behaviours sit in PendingStart for good -- pooled objects enabled then disabled before their first Start
    // (ObjectPool's startup pools): the >32 path re-sorted all of them with PendingStart.ToArray() + OrderBy every frame (Mac intro window:
    // 157 MiB of allocation per 1,000 frames, 0.88 ms a frame). Kept in their sorted order from frame to frame, a stable insertion sort passes
    // over them in one comparison each; a long reorder (a scene's many new behaviours) falls back to the stable merge sort. Same order as
    // OrderBy (stable, by Order), the same loop. SHEPHERD_K8_LISTS=0: kit #7's OrderBy path above 32.
    static NMono[] _startsBig = Array.Empty<NMono>(); static int[] _startKeysBig = Array.Empty<int>();
    static bool _activeDirty;

    static void Call(NObj n, MethodInfo m, string what)
    {
        if (m == null || n.Destroyed) return;
        try
        {
            var r = m.Invoke(n.Managed, null);
#if !SHEPHERD_EBOOT
            if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (Mac test runs only)
#endif
            if (r is IEnumerator e && n is NMono mono) Coroutines.Start(mono, e, m.Name);
        }
        catch (Exception e)
        {
            Log.Exception(e, $"{what} {n.Managed?.GetType().FullName} '{n.Name}'");
#if !SHEPHERD_EBOOT
            if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: a callback that threw after handing work)
#endif
        }
    }

    // ---------- activation ----------
    public static void Awake(NMono m) { if (m.Awoken) return; m.Awoken = true; if (TraceAwake != null) TraceAwakeLine(m); if (Profile.Vita) { Impl.VitaPad.BeforeAwake(m.Managed); VitaMemory.BeforeAwake(m.Managed); } var g = For(m.Managed.GetType()); Call(m, g.Awake, "Awake"); if (Time.VirtualRealtime) RewiredClock.AfterAwake(m.Managed); if (g.K8) K8Play.AfterAwake(m.Managed); }
    /// <summary>kit5 mem (diagnostic, off unless set): SHEPHERD_TRACE_AWAKE=&lt;type short name&gt;[,...] logs each Awake of those types with the
    /// GameObject path, its activeInHierarchy, every UnityEngine.Object field's source file / path id, and the caller.</summary>
#if !SHEPHERD_EBOOT
    static readonly HashSet<string> TraceAwake = Environment.GetEnvironmentVariable("SHEPHERD_TRACE_AWAKE") is { Length: > 0 } ta ? new(ta.Split(',')) : null;
#else
    static readonly HashSet<string> TraceAwake = null;   // kit #5 integration: the diagnostic is not in the eboot (SHEPHERD_ENGINE_TOOLS=1 keeps it)
    static void TraceAwakeLine(NMono m) { }
#endif
#if !SHEPHERD_EBOOT
    static void TraceAwakeLine(NMono m)
    {
        var t = m.Managed.GetType(); if (!TraceAwake.Contains(t.Name)) return;
        try
        {
            var path = new System.Text.StringBuilder(); for (var tr = m.Go?.Components.FirstOrDefault(c => c is NTransform) as NTransform; tr != null; tr = tr.Parent) path.Insert(0, "/" + tr.Name);
            var refs = new System.Text.StringBuilder();
            for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                foreach (var fi in c.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    if (typeof(UnityEngine.Object).IsAssignableFrom(fi.FieldType) && fi.GetValue(m.Managed) is UnityEngine.Object u && Objects.Get(u) is { } r && r.SourceFile != null)
                        refs.Append($" {fi.Name}='{r.Name}'@{(r.SourceFile as Shepherd.Assets.SerializedFile)?.Name}#{r.PathId}");
            var st = new System.Diagnostics.StackTrace(2, false).ToString().Replace(Environment.NewLine, " < ").Replace("   at ", "");
            Log.Info($"[awake] {t.Name} go='{path}' activeInHierarchy={m.Go?.ActiveInHierarchy} scene={m.Go?.Scene}{refs} via {st[..Math.Min(900, st.Length)]}");
        }
        catch (Exception e) { Log.Warn($"[awake] trace: {e.Message}"); }
    }
#endif
    public static void Enable(NMono m)
    {
        if (m.IsEnabledCalled || m.Destroyed) return;
        m.IsEnabledCalled = true; Active.Add(m); _activeDirty = true; _activeVer++;
        if (!m.Started) { PendingStart.Add(m); m.StartQueued = true; }
        Call(m, For(m.Managed.GetType()).OnEnable, "OnEnable");
    }
    public static void Disable(NMono m)
    {
        if (!m.IsEnabledCalled) return;
        m.IsEnabledCalled = false; Active.Remove(m); _activeVer++;
        Call(m, For(m.Managed.GetType()).OnDisable, "OnDisable");
    }

    /// <summary>The GameObject (or an ancestor) just became active in hierarchy: Awake + OnEnable everything under it.</summary>
    // (menu-speed: the loops below iterate pooled copies -- Pooled<T>.Copy, what List.ToArray() gave -- instead of a new array each)
    public static void BecameActive(NGameObject go)
    {
        if (go.Transform.PhysInterest != 0) Physics.PhysWorld.Activated(go);   // physics stage 0: the activation hook (an empty facade)
        var cs = Pooled<NComponent>.Copy(go.Components, out int n);
        try { for (int i = 0; i < n; i++) if (cs[i] is NMono m && !m.Destroyed) { Awake(m); if (m.Enabled && !m.Destroyed && go.ActiveInHierarchy) Enable(m); } }
        finally { Pooled<NComponent>.Back(cs, n); }
        var ch = Pooled<NTransform>.Copy(go.Transform.Children, out int k);
        try { for (int i = 0; i < k; i++) if (ch[i].Go.ActiveSelf) BecameActive(ch[i].Go); }
        finally { Pooled<NTransform>.Back(ch, k); }
    }
    public static void BecameInactive(NGameObject go)
    {
        if (go.Transform.PhysInterest != 0) Physics.PhysWorld.Deactivated(go);   // physics stage 0: the activation hook (an empty facade)
        var cs = Pooled<NComponent>.Copy(go.Components, out int n);
        try { for (int i = 0; i < n; i++) if (cs[i] is NMono m) { Disable(m); Coroutines.StopAll(m); } }
        finally { Pooled<NComponent>.Back(cs, n); }
        var ch = Pooled<NTransform>.Copy(go.Transform.Children, out int k);
        try { for (int i = 0; i < k; i++) if (ch[i].Go.ActiveSelf) BecameInactive(ch[i].Go); }
        finally { Pooled<NTransform>.Back(ch, k); }
    }
    public static void SetActive(NGameObject go, bool v)
    {
        if (go.ActiveSelf == v) return;
        bool before = go.ActiveInHierarchy; go.ActiveSelf = v; bool after = go.ActiveInHierarchy;
        if (!before && after) BecameActive(go); else if (before && !after) BecameInactive(go);
    }
    public static void SetEnabled(NBehaviour b, bool v)
    {
        if (b.Enabled == v) return; b.Enabled = v;
        if (b is Physics.NCollider2D) Physics.PhysWorld.Enabled(b, v);   // physics stage 0: the enable hook (an empty facade)
        if (b is NMono m && m.Go != null && m.Go.ActiveInHierarchy && m.Awoken) { if (v) Enable(m); else Disable(m); }
    }

    // ---------- creation ----------
    public static NGameObject NewGameObject(GameObject managed, string name, bool rect = false)
    {
        var go = Objects.Bind(managed, new NGameObject { Name = name ?? "New Game Object" });
        var tType = rect ? typeof(RectTransform) : typeof(Transform);
        var tr = Objects.Bind(Objects.NewManaged(tType), new NTransform { Go = go, Name = go.Name, IsRect = rect });
        go.Components.Add(tr); Scenes.AddRoot(go);
        return go;
    }

    public static NComponent NewComponentRecord(Type t) =>
        typeof(MonoBehaviour).IsAssignableFrom(t) ? new NMono() :
        typeof(CanvasRenderer).IsAssignableFrom(t) ? new NCanvasRenderer() :
        typeof(Transform).IsAssignableFrom(t) ? new NTransform { IsRect = typeof(RectTransform).IsAssignableFrom(t) } :
        (Physics.PhysConfig.On ? Physics.PhysRecords.New(t) : null) ??   // physics stage 0 (plan 1.4): typed records for Rigidbody2D / colliders, by name
        (typeof(Behaviour).IsAssignableFrom(t) ? new NBehaviour() :
        typeof(Renderer).IsAssignableFrom(t) ? new NRenderer() : new NComponent());   // menu-speed: NRenderer = NComponent + the world pass's bag cache

    /// <summary>AddComponent: bind first, then run the managed ctor (field initializers), then Awake/OnEnable if active.</summary>
    public static Component AddComponent(NGameObject go, Type t, bool runCallbacks = true)
    {
        if (t == null || t.IsAbstract || t.IsInterface) { Log.Error($"AddComponent: bad type {t}"); return null; }
        if (typeof(Transform).IsAssignableFrom(t))
        {
            if (t == typeof(RectTransform) && !go.Transform.IsRect) return ConvertToRect(go);
            return (Component)go.Transform.Managed;
        }
        foreach (var req in RequiredComponents(t))
        {
            bool has = false; foreach (var c in go.Components) if (req.IsInstanceOfType(c.Managed)) { has = true; break; }   // (was Components.Any(closure))
            if (!has) AddComponent(go, req, runCallbacks);
        }
        var managed = Objects.NewManaged(t);
        var rec = Objects.Bind(managed, NewComponentRecord(t)); rec.Go = go; rec.Name = go.Name;
        go.Components.Add(rec);
        if (rec is Physics.NCollider2D fit && Physics.PhysConfig.Level >= 1 && Physics.PhysWorld.Sim) Physics.PhysRecords.AutoFit(fit, go);   // physics since E1: H-AUTOFIT=on (level 1+ with a core; kit #8 review: without one = stage 0 = level 0, as the logs say)
        if (rec is Physics.NRigidbody2D or Physics.NCollider2D or Physics.NCollider3D) { Physics.PhysRecords.NoteRuntime(); Physics.PhysWorld.Added(rec); Physics.PhysWorld.AddedAtRuntime(rec); }   // physics stage 0: the add hook (Stage 4a: a runtime composite)
        Objects.RunCtor(managed);
        if (runCallbacks && rec is NMono m && go.ActiveInHierarchy) { Awake(m); if (m.Enabled && go.ActiveInHierarchy) Enable(m); }
        return (Component)managed;
    }

    static Component ConvertToRect(NGameObject go)
    {
        var old = go.Transform; var managed = Objects.NewManaged(typeof(RectTransform));
        var rt = Objects.Bind(managed, new NTransform { Go = go, Name = go.Name, IsRect = true, Parent = old.Parent, LocalPosition = old.LocalPosition, LocalRotation = old.LocalRotation, LocalScale = old.LocalScale });
        rt.Children.AddRange(old.Children); foreach (var c in rt.Children) c.Parent = rt;
        if (old.Parent != null) { int i = old.Parent.Children.IndexOf(old); old.Parent.Children[i] = rt; }
        go.Components[0] = rt;
        if (old.PhysInterest != 0 || old.PhysDesc != 0) Physics.PhysWorld.TransformReplaced(old, rt);   // physics stage 0: the interest moves to the new transform
        Objects.Kill(old);
        return (Component)managed;
    }

    static readonly Dictionary<Type, Type[]> RequiredCache = new();
    /// <summary>[RequireComponent] closure inputs of t, cached per type (attribute reflection allocated on every AddComponent).</summary>
    static Type[] RequiredComponents(Type t)
    {
        if (!RequiredCache.TryGetValue(t, out var r)) RequiredCache[t] = r = RequiredComponentsUncached(t).ToArray();
        return r;
    }
    static IEnumerable<Type> RequiredComponentsUncached(Type t)
    {
        for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
            foreach (var a in c.GetCustomAttributes(false).Where(a => a.GetType().Name == "RequireComponent"))
                foreach (var f in new[] { "m_Type0", "m_Type1", "m_Type2" })
                {
                    var v = a.GetType().GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(a) as Type;
                    if (v != null && v != t) yield return v;
                }
    }

    // ---------- destruction ----------
    static readonly List<(NObj n, double at)> DestroyQueue = new();
    /// <summary>menu-speed (top hotspots): &gt; 0 while a GameObject's subtree is being destroyed. DestroyTree kills the records (their names leave
    /// GameObject.Find's name filter) before the root is unlinked from its parent / scene, so in that window a destroyed GameObject is still
    /// reachable by Find's walk; Find takes the filter's shortcut only at 0.</summary>
    internal static int DestroyDepth;
    public static void Destroy(NObj n, float delay)
    {
        if (n == null || n.Destroyed) return;
        DestroyQueue.Add((n, Time.Now + Math.Max(0, delay)));
        if (delay <= 0f && Physics.PhysConfig.Level >= 2 && Physics.PhysWorld.Sim2) Physics.PhysWorld.DestroyRequested(n);   // physics Stage 2 since E1: Unity's colliders leave the world at the Destroy call (level 2+ only)
    }
    public static void DestroyNow(NObj n)
    {
        if (n == null || n.Destroyed) return;
        Triggers2D.Destroying(n);   // fid1-fixup D5: a touching collider in it: the surviving side hears OnTriggerExit2D now
        switch (n)
        {
            case NGameObject go:
                if (go.ActiveInHierarchy) BecameInactive(go);
                if (go.Transform.PhysDesc != 0) Physics.PhysWorld.SubtreeGone(go.Transform);   // physics stage 0: the ancestors lose the subtree's interested transforms
                DestroyDepth++;   // menu-speed (top hotspots): see DestroyDepth
                try
                {
                    DestroyTree(go);
                    var tr = go.Transform; if (tr.Parent != null) tr.Parent.Children.Remove(tr); else Scenes.RemoveRoot(go);
                }
                finally { DestroyDepth--; }
                break;
            case NTransform t: DestroyNow(t.Go); break;
            case NComponent c:
                if (c is NMono m) { Disable(m); Coroutines.StopAll(m); if (m.Awoken) Call(m, For(m.Managed.GetType()).OnDestroy, "OnDestroy"); PendingStart.Remove(m); }
                c.Go?.Components.Remove(c); Objects.Kill(c);
                break;
            default:
                if (n.Managed is ScriptableObject) { Call(n, For(n.Managed.GetType()).OnDisable, "OnDisable"); Call(n, For(n.Managed.GetType()).OnDestroy, "OnDestroy"); }
                Objects.Kill(n); break;
        }
    }
    static void DestroyTree(NGameObject go)
    {
        if (K6Engine.DestroyOrder) { WillDestroyTree(go); KillTree(go); return; }   // kit #6 (b): Unity's two passes
        var ch = Pooled<NTransform>.Copy(go.Transform.Children, out int k);
        try { for (int i = 0; i < k; i++) DestroyTree(ch[i].Go); }
        finally { Pooled<NTransform>.Back(ch, k); }
        var cs = Pooled<NComponent>.Copy(go.Components, out int n);
        try
        {
            for (int i = 0; i < n; i++)
                if (cs[i] is NMono m)
                {
                    Coroutines.StopAll(m); Invokes.CancelAll(m); if (m.Awoken) Call(m, For(m.Managed.GetType()).OnDestroy, "OnDestroy");
                    // kit3 (sub-menus): both removals were unconditional linear scans, and a destroyed hierarchy's behaviours are almost never in
                    // either list: closing the Settings screen (17,984 behaviours against ~5,000 Active and a few hundred PendingStart) made
                    // 19.4 M comparisons, a 645 ms frame on the Mac interpreter (~40 s on the Vita). A behaviour is in Active only while
                    // IsEnabledCalled (Enable sets it and adds; Disable clears it and removes; only this line removes without clearing, and the
                    // behaviour is destroyed then). It is in PendingStart only if it was ever queued (Enable sets StartQueued when it adds) and
                    // not started -- except one enabled again during the Start pass that then starts it, which the next pass drops unseen
                    // (started); left there, it changes no call.
                    if (m.StartQueued && !m.Started) PendingStart.Remove(m);
                    if (m.IsEnabledCalled) Active.Remove(m);
                    _activeVer++;
                }
        }
        finally { Pooled<NComponent>.Back(cs, n); }
        cs = Pooled<NComponent>.Copy(go.Components, out n);
        try { for (int i = 0; i < n; i++) Objects.Kill(cs[i]); }
        finally { Pooled<NComponent>.Back(cs, n); }
        Objects.Kill(go);
    }

    /// <summary>Kit #6 (b): the first of Unity's two passes over a destroyed hierarchy (read from the game's UnityPlayer: GameObject's
    /// will-destroy step marks the object as being destroyed and calls each component's will-destroy -- a MonoBehaviour's OnDestroy -- in
    /// component order, then recurses into the children: pre-order, parent first). Nothing is deleted in this pass, so every OnDestroy
    /// still finds the whole hierarchy: BiomeConstants.OnDestroy reaches its particle child, StencilBufferRender its own Camera. Kit #5
    /// (SHEPHERD_K6_DESTROY_ORDER=0) deleted each child subtree before its parent's OnDestroy ran.</summary>
    static void WillDestroyTree(NGameObject go)
    {
        var cs = Pooled<NComponent>.Copy(go.Components, out int n);
        try
        {
            for (int i = 0; i < n; i++)
                if (cs[i] is NMono m && !m.Destroyed)
                {
                    Coroutines.StopAll(m); Invokes.CancelAll(m); if (m.Awoken) Call(m, For(m.Managed.GetType()).OnDestroy, "OnDestroy");
                    if (m.StartQueued && !m.Started) PendingStart.Remove(m);   // (DestroyTree's rules, unchanged)
                    if (m.IsEnabledCalled) Active.Remove(m);
                    _activeVer++;
                }
        }
        finally { Pooled<NComponent>.Back(cs, n); }
        var ch = Pooled<NTransform>.Copy(go.Transform.Children, out int k);
        try { for (int i = 0; i < k; i++) if (!ch[i].Go.Destroyed) WillDestroyTree(ch[i].Go); }
        finally { Pooled<NTransform>.Back(ch, k); }
    }
    /// <summary>Kit #6 (b): the second pass deletes in kit #5's order (each child subtree, then the object's components, then the object):
    /// the records leave the registry's slot order exactly as before.</summary>
    static void KillTree(NGameObject go)
    {
        var ch = Pooled<NTransform>.Copy(go.Transform.Children, out int k);
        try { for (int i = 0; i < k; i++) KillTree(ch[i].Go); }
        finally { Pooled<NTransform>.Back(ch, k); }
        var cs = Pooled<NComponent>.Copy(go.Components, out int n);
        try { for (int i = 0; i < n; i++) Objects.Kill(cs[i]); }
        finally { Pooled<NComponent>.Back(cs, n); }
        Objects.Kill(go);
    }

    // ---------- ScriptableObject ----------
    public static void ScriptableAwakeEnable(NObj so)
    {
        var msgs = For(so.Managed.GetType()); Call(so, msgs.Awake, "Awake"); Call(so, msgs.OnEnable, "OnEnable");
    }

    // ---------- the frame ----------
    public static Action Render;
    static readonly MethodInfo SyncInit = typeof(MonoBehaviour).Assembly.GetType("UnityEngine.UnitySynchronizationContext")?.GetMethod("InitializeSynchronizationContext", BindingFlags.Static | BindingFlags.NonPublic);
    static readonly MethodInfo SyncExec = typeof(MonoBehaviour).Assembly.GetType("UnityEngine.UnitySynchronizationContext")?.GetMethod("ExecuteTasks", BindingFlags.Static | BindingFlags.NonPublic);
    public static void InstallSyncContext() { try { SyncInit?.Invoke(null, null); Log.Info($"UnitySynchronizationContext installed: {SynchronizationContext.Current?.GetType().Name}"); } catch (Exception e) { Log.Exception(e, "UnitySynchronizationContext"); } }
    static double _fixedAcc;
    /// <summary>Physics Stage 1 (slot C, H-INTERP): (Time.time - fixedTime) / fixedDeltaTime -- the frame clock's remainder, or the wall clock's.</summary>
    internal static double FixedAlpha => Time.Wall ? (Time.Now - Time.FixedNow) / Time.FixedDeltaTime : _fixedAcc / Time.FixedDeltaTime;
    public static void Frame()
    {
        Time.BeginFrame();
#if !SHEPHERD_NO_K8
        if (K8Preload.On) K8Preload.Tick();   // kit #8 boot track: the read-order map's window / the idle-window preload (data only)
#endif
        bool k7 = Diag.K7Prof.Types;   // kit #7: per-behaviour keys of the profiler (off: SHEPHERD_K7_PROF unset)
        Diag.K7Prof.Push(Diag.K7Prof.ZStarts);
        // Start (before first Update)
        if (PendingStart.Count > 0 && (!Shepherd.Engine.Render.HotCfg.On || (PendingStart.Count > StartsSmall && !K8Speed.Lists)))
        {
            var starts = PendingStart.ToArray(); PendingStart.Clear();
            foreach (var m in starts.OrderBy(x => For(x.Managed.GetType()).Order))
            {
                if (m.Destroyed || m.Started || !m.IsEnabledCalled) { if (!m.Started && !m.Destroyed) PendingStart.Add(m); continue; }
                var g = For(m.Managed.GetType()); m.Started = true;
                if (k7) Diag.K7Prof.Push(K7Id(m, 3));
                Call(m, g.Start, "Start");
                if (k7) Diag.K7Prof.Pop();
                if (g.K6) VitaToggles.AfterStart(m.Managed);
            }
        }
        else if (PendingStart.Count > 0)
        {   // menu-speed (top hotspots): the same for a few behaviours (every idle frame: the game makes objects per frame) without LINQ's
            // buffers: the copy, every key computed first in element order (as OrderBy does), a stable insertion sort (OrderBy is stable), the same loop
            int n = PendingStart.Count; var starts = _starts; var keys = _startKeys;
            if (n > StartsSmall)
            {   // kit #8 (SHEPHERD_K8_LISTS): the kept, grown buffers
                if (_startsBig.Length < n) { _startsBig = new NMono[Math.Max(256, n + n / 4)]; _startKeysBig = new int[_startsBig.Length]; }
                starts = _startsBig; keys = _startKeysBig;
            }
            PendingStart.CopyTo(starts); PendingStart.Clear();
            for (int i = 0; i < n; i++) keys[i] = For(starts[i]).Order;   // (For(m): the record's cached message table = For(its type))
            if (Shepherd.Engine.Render.HotCfg.Check) StartsCheck(starts, n);   // (the OrderBy order of the unsorted copy, computed before the sort)
            long moves = 0, budget = 8L * n + 4096;
            for (int i = 1; i < n; i++)
            {
                int k = keys[i]; if (keys[i - 1] <= k) continue;
                var x = starts[i]; int j = i - 1;
                while (j >= 0 && keys[j] > k) { keys[j + 1] = keys[j]; starts[j + 1] = starts[j]; j--; }
                keys[j + 1] = k; starts[j + 1] = x;
                if ((moves += i - 1 - j) > budget) { MergeSortStable(starts, keys, n); break; }   // (stable: the same order; a long reorder only)
            }
            if (Shepherd.Engine.Render.HotCfg.Check) Shepherd.Engine.Render.HotCheck.Seq(Shepherd.Engine.Render.HotCheck.Starts, new ArraySegment<NMono>(starts, 0, n), _startsOrig);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var m = starts[i];
                    if (m.Destroyed || m.Started || !m.IsEnabledCalled) { if (!m.Started && !m.Destroyed) PendingStart.Add(m); continue; }
                    var g = For(m.Managed.GetType()); m.Started = true;
                    if (k7) Diag.K7Prof.Push(K7Id(m, 3));
                    Call(m, g.Start, "Start");
                    if (k7) Diag.K7Prof.Pop();
                    if (g.K6) VitaToggles.AfterStart(m.Managed);
                }
            }
            finally { Array.Clear(starts, 0, n); }
        }
        if (_activeDirty)
        {   // kit #8 speed: the profiler's "Active list sort" zone (30) -- only with the track on (review fix: SHEPHERD_K8_SPEED=0 = kit #7's prof.tsv,
            // the sort's time in "Start calls" again); no try/finally, as every kit #7 zone: K7Prof.FrameBegin unwinds a zone an exception skipped
            bool zs = K8Speed.Track; if (zs) Diag.K7Prof.Push(Diag.K7Prof.ZSort);
            if (K6Cfg.StableOrder) SortActiveStable(); else Active.Sort((a, b) => For(a.Managed.GetType()).Order.CompareTo(For(b.Managed.GetType()).Order)); _activeDirty = false; _activeVer++;
            if (zs) Diag.K7Prof.Pop();
        }
        Diag.K7Prof.Pop();
        if (PhysSlots) Physics.PhysWorld.EarlyUpdate();   // physics stage 0, loop slot A (plan section 3): Stage 1 restores interpolated bodies' true poses here
        // FixedUpdate at fixed rate
        if (!Time.Wall)
        {   // frame clock (Runtime/Time.cs): the reference timeline of the scripted runs, unchanged (at 1/60 s per frame the cap never binds)
            _fixedAcc += Time.DeltaTime; int steps = 0, done = 0;
            long k0 = FixedSteps.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;   // kit #8: the per-scene steps / frame (counted and timed only)
            while (_fixedAcc >= Time.FixedDeltaTime && steps++ < FrameStepCap)   // physics Stage 3: Unity's rule (no cap) where Stage 3 runs; 5 otherwise (SHEPHERD_PHYS_STEPCAP)
            {
                _fixedAcc -= Time.FixedDeltaTime; Time.FixedNow += Time.FixedDeltaTime;
                if (FrameFixedTime) { Time.FixedUnscaledDeltaTime = Time.TimeScale > 0 ? Time.FixedDeltaTime / Time.TimeScale : Time.FixedDeltaTime; Time.FixedUnscaledNow += Time.FixedUnscaledDeltaTime; }   // (Stage 3 time fixes: the fixed unscaled clock)
                long fx0 = Diag.SimStats.FixedTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;   // kit4 intro-plan diag (timing only)
                Diag.K7Prof.Push(Diag.K7Prof.ZFixed); FixedStep(); Diag.K7Prof.Pop(); done++;
                if (Diag.SimStats.FixedTiming) Diag.SimStats.NoteFixedStep(System.Diagnostics.Stopwatch.GetTimestamp() - fx0);
            }
            if (FixedSteps.On) FixedSteps.Note(done, System.Diagnostics.Stopwatch.GetTimestamp() - k0);
        }
        else
        {   // kit3 play mode: Unity's StepFixedTime, as many 50 Hz steps as time needs (kit #3 review: counted and timed for the heartbeat)
            long f0 = System.Diagnostics.Stopwatch.GetTimestamp(); int steps = 0;
            while (Time.StepFixedTime()) { Diag.K7Prof.Push(Diag.K7Prof.ZFixed); FixedStep(); Diag.K7Prof.Pop(); steps++; }
            long ft = System.Diagnostics.Stopwatch.GetTimestamp() - f0;
            Time.NoteFixedFrame(steps, ft);
            if (FixedSteps.On) FixedSteps.Note(steps, ft);   // kit #8: the per-scene steps / frame
        }
        if (PhysSlots) Physics.PhysWorld.Update();   // physics stage 0, loop slot C: Stage 1's interpolation (slot C) and render-pose snapshot

        Shepherd.Audio.Silence.BeforeUpdates();   // Mac default, headless: mute FMOD before RuntimeManager.Update's first Studio update
#if !SHEPHERD_EBOOT
        Diag.TestAstar.BeforeUpdates();          // kit #7 tests: SHEPHERD_TEST_ASTAR (Mac test runs only; not in the eboots)
#endif
        Diag.K7Prof.Push(Diag.K7Prof.ZUpdate);
        int nu = PhaseSnapshot(PhUpdate, ref _updateBuf, ref _updateD); var ud = Shepherd.Engine.Render.HotCfg.On ? _updateD : null;
        for (int i = 0; i < nu; i++) { var m = _updateBuf[i]; if (m.Started && m.IsEnabledCalled) { if (k7) Diag.K7Prof.Push(K7Id(m, PhUpdate)); if (ud != null) CallD(m, For(m).Update, ud[i], "Update"); else Call(m, For(m).Update, "Update"); if (k7) Diag.K7Prof.Pop(); } }
        Array.Clear(_updateBuf, 0, nu); if (ud != null) Array.Clear(ud, 0, nu);
        Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZCoNext); Coroutines.Resume(Coroutines.Wait.NextFrame); Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZSync);
        try { SyncExec?.Invoke(null, null); } catch (Exception e) { Log.Exception(e, "UnitySynchronizationContext.ExecuteTasks"); }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: the synchronization context's tasks)
#endif
        Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZInvokes); Invokes.Tick(); Diag.K7Prof.Pop();
        if (!K6Engine.Video) { Diag.K7Prof.Push(Diag.K7Prof.ZVideo); Impl.VideoIcalls.Tick(); Diag.K7Prof.Pop(); }   // kit #5's place (before LateUpdate)
        Diag.K7Prof.Push(Diag.K7Prof.ZAnimator); Anim.AnimatorRuntime.UpdateAll(); Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZLate);
        int nl = PhaseSnapshot(PhLate, ref _lateBuf, ref _lateD); var lde = Shepherd.Engine.Render.HotCfg.On ? _lateD : null;
        for (int i = 0; i < nl; i++) { var m = _lateBuf[i]; if (m.Started && m.IsEnabledCalled) { if (k7) Diag.K7Prof.Push(K7Id(m, PhLate)); if (lde != null) CallD(m, For(m).LateUpdate, lde[i], "LateUpdate"); else Call(m, For(m).LateUpdate, "LateUpdate"); if (k7) Diag.K7Prof.Pop(); } }
        Array.Clear(_lateBuf, 0, nl); if (lde != null) Array.Clear(lde, 0, nl);
        Diag.K7Prof.Pop();
        if (K6Engine.Video) { Diag.K7Prof.Push(Diag.K7Prof.ZVideo); Impl.VideoIcalls.Tick(); Diag.K7Prof.Pop(); }    // kit #6 (a): Unity's PostLateUpdate.UpdateVideo, after every LateUpdate
        // deferred destroys
        if (DestroyQueue.Count > 0)
        {
            Diag.K7Prof.Push(Diag.K7Prof.ZDestroy);
            if (K8Speed.Lists) DueDestroys();   // kit #8 speed (lever 2 lists): the same objects destroyed in the same order, no LINQ / closures
            else
            {
                var due = DestroyQueue.Where(d => d.at <= Time.Now).ToList();
                DestroyQueue.RemoveAll(d => d.at <= Time.Now);
                foreach (var d in due) DestroyNow(d.n);
            }
            Diag.K7Prof.Pop();
        }
        Diag.K7Prof.Push(Diag.K7Prof.ZAsync); AsyncOps.Tick(); Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZRender);
        try { Render?.Invoke(); } catch (Exception e) { Log.Exception(e, "Render"); }
        Diag.K7Prof.Pop();
        Time.RenderedFrames++;
        Diag.K7Prof.Push(Diag.K7Prof.ZCoEnd); Coroutines.Resume(Coroutines.Wait.EndOfFrame); Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZEngEnd);
        Assets.Diet.EndFrame();   // V3 diet: release whole-file buffers nothing read lately
        Registry.EndFrame();      // fid1-fixup D2: every list in use classifies this frame's records; the bind log never outlives a frame
        Diag.K7Prof.Pop();
    }

    /// <summary>Kit #8 speed: the deferred destroys due now -- the queue's entries with at &lt;= Time.Now taken out in queue order (the rest keep
    /// their order), then destroyed in that order: what Where(..).ToList() + RemoveAll(..) + foreach did (Time.Now does not change in between;
    /// a Destroy called from an OnDestroy here appends to the queue, as before).</summary>
    static readonly List<NObj> _due = new();
    static void DueDestroys()
    {
        double now = Time.Now; int w = 0, n = DestroyQueue.Count;
        for (int r = 0; r < n; r++) { var d = DestroyQueue[r]; if (d.at <= now) _due.Add(d.n); else DestroyQueue[w++] = d; }
        DestroyQueue.RemoveRange(w, n - w);
        try { for (int i = 0; i < _due.Count; i++) DestroyNow(_due[i]); }
        finally { _due.Clear(); }
    }

    /// <summary>One fixed step (fixedTime already advanced by the caller): FixedUpdate on every behaviour, the step's trigger
    /// callbacks, then the WaitForFixedUpdate coroutines.</summary>
    static void FixedStep()
    {
        Time.InFixedStep = true;
        bool k7 = Diag.K7Prof.Types;
        Diag.K7Prof.Push(Diag.K7Prof.ZFixedUpd);
        int nf = PhaseSnapshot(PhFixed, ref _fixedBuf, ref _fixedD); var fd = Shepherd.Engine.Render.HotCfg.On ? _fixedD : null;
        for (int i = 0; i < nf; i++) { var m = _fixedBuf[i]; if (m.Started && m.IsEnabledCalled) { if (k7) Diag.K7Prof.Push(K7Id(m, PhFixed)); if (fd != null) CallD(m, For(m).FixedUpdate, fd[i], "FixedUpdate"); else Call(m, For(m).FixedUpdate, "FixedUpdate"); if (k7) Diag.K7Prof.Pop(); } }
        Array.Clear(_fixedBuf, 0, nf); if (fd != null) Array.Clear(fd, 0, nf);
        Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZTriggers);
        if (PhysSlots) Physics.PhysWorld.FixedStep();   // physics stage 0, loop slot B (plan section 3): calls Triggers2D.Step() exactly as below (level 0); the core's step from level 1 (inside kit #7's triggers zone)
        else Triggers2D.Step();   // fidelity round 1: the physics step's trigger callbacks (no simulation yet), after the step's FixedUpdate loop
        Diag.K7Prof.Pop();
        Time.InFixedStep = false;
        Diag.K7Prof.Push(Diag.K7Prof.ZCoFixed); Coroutines.Resume(Coroutines.Wait.FixedUpdate); Diag.K7Prof.Pop();
    }

    public static void Quit() { foreach (var m in Active.ToArray()) Call(m, For(m.Managed.GetType()).OnApplicationQuit, "OnApplicationQuit"); }

    // ---------- kit #6 toggles review: Active in a stable order (kit #6 integration: default ON; SHEPHERD_K6_STABLE_ORDER=0 = kit #5's sort) ----------
    // Unity keeps its update lists per execution order and only appends to them (a behaviour enabled goes to the end of its order's list, one
    // disabled leaves it): behaviours of the same order run in the order they were enabled. Active gets the same Add / Remove, but Frame put it
    // in order with List.Sort -- introsort, not stable: ~1,800 of the menu's ~1,850 active behaviours share order 0, so their order after each
    // sort depends on everything Active held at that moment, and an unrelated object enabled or destroyed (a Settings tab's extra rows, a
    // dropdown opened and closed) can swap e.g. the UI navigator and DOTween's component and shift later tweens by a frame
    // (vita/notes/kit6-toggles.md, "Review fixes"). The stable sort keeps equal orders in Active's order = the order they were enabled in.
    // (Toggles review: off by default, as it changes kit #5's own frames and Unity's order among equal orders is inferred from its update
    // lists, not checked against a capture of the game. Kit #6 integration: on by default -- the frame-clock references are re-based once
    // anyway for the Devolver splash, and an order that depends on unrelated objects' history is an engine artifact either way; the
    // Unity editor check E1 (kit #7) is where the tie order gets verified.)
    static NMono[] _ordA; static int[] _ordK;
    static void SortActiveStable()
    {
        int n = Active.Count;
        if (_ordA == null || _ordA.Length < n) { _ordA = new NMono[Math.Max(256, n + n / 4)]; _ordK = new int[_ordA.Length]; }
        var a = _ordA; var k = _ordK; Active.CopyTo(a);
        for (int i = 0; i < n; i++) k[i] = For(a[i]).Order;
        // insertion sort (stable; Active is sorted but for the behaviours enabled since the last sort, at its end: ~n steps), a merge sort
        // when that would be long (a big activation of mixed orders)
        long moves = 0, budget = 8L * n + 4096; bool moved = false;
        for (int i = 1; i < n; i++)
        {
            int key = k[i]; if (k[i - 1] <= key) continue;
            var x = a[i]; int j = i - 1; moved = true;
            while (j >= 0 && k[j] > key) { k[j + 1] = k[j]; a[j + 1] = a[j]; j--; }
            k[j + 1] = key; a[j + 1] = x;
            if ((moves += i - 1 - j) > budget) { MergeSortStable(a, k, n); break; }
        }
        if (moved) for (int i = 0; i < n; i++) Active[i] = a[i];
        Array.Clear(a, 0, n);
    }
    static void MergeSortStable(NMono[] a, int[] k, int n)
    {   // bottom-up; equal keys keep their order (the left run's first)
        var b = new NMono[n]; var kb = new int[n];
        for (int w = 1; w < n; w *= 2)
        {
            for (int lo = 0; lo < n; lo += 2 * w)
            {
                int mid = Math.Min(lo + w, n), hi = Math.Min(lo + 2 * w, n), i = lo, j = mid, o = lo;
                while (i < mid && j < hi) { if (k[j] < k[i]) { b[o] = a[j]; kb[o++] = k[j++]; } else { b[o] = a[i]; kb[o++] = k[i++]; } }
                while (i < mid) { b[o] = a[i]; kb[o++] = k[i++]; }
                while (j < hi) { b[o] = a[j]; kb[o++] = k[j++]; }
            }
            Array.Copy(b, a, n); Array.Copy(kb, k, n);
        }
    }
    /// <summary>Physics stage 0: the loop slots A / B / C are entered (SHEPHERD_PHYS >= 0, the default); <c>none</c> = kit #4's loop.</summary>
    static readonly bool PhysSlots = Physics.PhysConfig.On;
    static int FrameStepCap => Physics.PhysTime.FrameStepCap;   // physics Stage 3 (Physics/PhysTime.cs)
    static bool FrameFixedTime => Physics.PhysTime.FrameFixed;
}

public static class Coroutines
{
    public enum Wait { NextFrame, Seconds, FixedUpdate, EndOfFrame, Coroutine, Async, Done }
    public sealed class State
    {
        public NMono Owner; public readonly Stack<IEnumerator> Stack = new(); public IEnumerator Root; public string Method;
        public Wait Kind; public double ResumeAt; public State WaitingOn; public AsyncOperation Async; public Coroutine Handle; public bool Done;
        /// <summary>Kit #7: the profiler's id of this coroutine (owner type + method), made on its first timed step.</summary>
        public int K7Id = -1;
        /// <summary>The frame (Time.FrameCount) of the step that produced the current wait.</summary>
        public int YieldFrame;
    }
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_Seconds")] static extern ref float Seconds(WaitForSeconds w);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_Ptr")] static extern ref IntPtr CoPtr(Coroutine c);

    static readonly List<State> All = new();
    static readonly Dictionary<IntPtr, State> ByHandle = new();
    static long _nextHandle = 0x1000;

    public static Coroutine Start(NMono owner, IEnumerator e, string method = null)
    {
        if (e == null) return null;
        var s = new State { Owner = owner, Root = e, Method = method }; s.Stack.Push(e);
        var h = (Coroutine)RuntimeHelpers.GetUninitializedObject(typeof(Coroutine));
        var ptr = new IntPtr(_nextHandle++); CoPtr(h) = ptr; s.Handle = h; ByHandle[ptr] = s;
        if (Diag.K7Prof.Types) StepK7(s); else Step(s);   // Unity runs the first step synchronously (kit #7: timed as the coroutine's own key)
        if (!s.Done) All.Add(s);
        return h;
    }

    /// <summary>Kit #7 (SHEPHERD_K7_PROF): Step under the coroutine's own profiler key (owner type + method).</summary>
    static void StepK7(State s)
    {
        Diag.K7Prof.Push(s.K7Id >= 0 ? s.K7Id : s.K7Id = Diag.K7Prof.CoId(s.Owner?.Managed, s.Method, s.Root));
        Step(s);
        Diag.K7Prof.Pop();
    }
    static void Step(State s)
    {
        s.YieldFrame = Time.FrameCount;
        for (int guard = 0; guard < 10000; guard++)
        {
            if (s.Owner != null && s.Owner.Destroyed) { s.Done = true; return; }
            var top = s.Stack.Peek(); bool more;
            try { more = top.MoveNext(); }
            catch (Exception e)
            {
                Log.Exception(e, $"coroutine {s.Owner?.Managed?.GetType().Name}.{s.Method}"); s.Done = true; s.Kind = Wait.Done;
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: a step that threw)
#endif
                return;
            }
#if !SHEPHERD_EBOOT
            if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (Mac test runs only)
#endif
            if (!more) { s.Stack.Pop(); if (s.Stack.Count == 0) { s.Done = true; s.Kind = Wait.Done; return; } continue; }
            var y = top.Current;
            switch (y)
            {
                case null: s.Kind = Wait.NextFrame; return;
                case WaitForSeconds w: s.Kind = Wait.Seconds; s.ResumeAt = Time.Now + Seconds(w); return;
                case WaitForFixedUpdate: s.Kind = Wait.FixedUpdate; return;
                case WaitForEndOfFrame: s.Kind = Wait.EndOfFrame; return;
                case Coroutine c:
                    if (ByHandle.TryGetValue(CoPtr(c), out var other) && !other.Done) { s.Kind = Wait.Coroutine; s.WaitingOn = other; return; }
                    continue;
                case AsyncOperation op:
                    if (AsyncOps.IsDone(op)) continue;
                    s.Kind = Wait.Async; s.Async = op; return;
                case IEnumerator nested: s.Stack.Push(nested); continue;
                default: s.Kind = Wait.NextFrame; return;
            }
        }
        Log.Warn($"coroutine {s.Method} looped 10000 times without yielding; parked");
        s.Kind = Wait.NextFrame;
    }

    public static void Resume(Wait phase)
    {
        if (All.Count == 0) return;
        var arr = Pooled<State>.Copy(All, out int na);   // (menu-speed: was All.ToArray(); the same copy, pooled)
        try { for (int ia = 0; ia < na; ia++) ResumeOne(arr[ia], phase); }
        finally { Pooled<State>.Back(arr, na); }
        All.RemoveAll(s => s.Done);
    }
    static void ResumeOne(State s, Wait phase)
    {
        {
            if (s.Done) return;
            // Unity: `yield return null` (and WaitForSeconds) continue after the Update functions of a LATER frame, never in the frame
            // that yielded -- a coroutine started from Update/Awake/Start (or a button handler) used to resume in the same frame's
            // coroutine phase, ahead of the Start() of objects created in that frame (polish round 1: the Settings menu's DoShow fired
            // OnShow before its tab navigator's Start had subscribed, so no tab was ever shown).
            bool go = s.Kind switch
            {
                Wait.NextFrame => phase == Wait.NextFrame && s.YieldFrame != Time.FrameCount,
                Wait.Seconds => phase == Wait.NextFrame && s.YieldFrame != Time.FrameCount && Time.Now >= s.ResumeAt,
                Wait.FixedUpdate => phase == Wait.FixedUpdate,
                Wait.EndOfFrame => phase == Wait.EndOfFrame,
                Wait.Coroutine => phase == Wait.NextFrame && (s.WaitingOn == null || s.WaitingOn.Done),
                Wait.Async => phase == Wait.NextFrame && AsyncOps.IsDone(s.Async),
                _ => false
            };
            if (go) { if (Diag.K7Prof.Types) StepK7(s); else Step(s); }
        }
    }

    public static void StopAll(NMono m) { foreach (var s in All) if (s.Owner == m) s.Done = true; }
    public static void Stop(NMono m, IEnumerator e) { foreach (var s in All) if (s.Owner == m && s.Root == e) s.Done = true; }
    public static void Stop(NMono m, string method) { foreach (var s in All) if (s.Owner == m && s.Method == method) s.Done = true; }
    public static void Stop(Coroutine c) { if (c != null && ByHandle.TryGetValue(CoPtr(c), out var s)) s.Done = true; }
    public static int Count => All.Count;
}

public static class Invokes
{
    sealed class Item { public NMono Owner; public string Method; public double At; public float Repeat; public bool Cancelled; }
    static readonly List<Item> Items = new();
    public static void Add(NMono m, string method, float time, float repeat) => Items.Add(new Item { Owner = m, Method = method, At = Time.Now + time, Repeat = repeat });
    public static void Cancel(NMono m, string method) { foreach (var i in Items) if (i.Owner == m && (method == null || i.Method == method)) i.Cancelled = true; }
    public static void CancelAll(NMono m) => Cancel(m, null);
    public static bool IsInvoking(NMono m, string method) => Items.Any(i => !i.Cancelled && i.Owner == m && (method == null || i.Method == method));
    static Item[] _tickBuf = Array.Empty<Item>();
    public static void Tick()
    {
        int n = Items.Count; if (_tickBuf.Length < n) _tickBuf = new Item[Math.Max(16, n * 2)];
        Items.CopyTo(_tickBuf); var snap = _tickBuf;   // (was Items.ToArray(); cleared below)
        for (int k = 0; k < n; k++)
        {
            var i = snap[k];
            if (i.Cancelled || i.Owner.Destroyed) { i.Cancelled = true; continue; }
            if (Time.Now < i.At || !i.Owner.Go.ActiveInHierarchy) continue;
            var mi = FindMethod(i.Owner.Managed.GetType(), i.Method);
            try { mi?.Invoke(i.Owner.Managed, null); } catch (Exception e) { Log.Exception(e, $"Invoke {i.Method}"); }
#if !SHEPHERD_EBOOT
            if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: Invoke / InvokeRepeating targets)
#endif
            if (i.Repeat > 0) i.At += i.Repeat; else i.Cancelled = true;
        }
        Array.Clear(snap, 0, n);
        Items.RemoveAll(i => i.Cancelled);
    }
    public static MethodInfo FindMethod(Type t, string name)
    {
        for (var c = t; c != null; c = c.BaseType)
        {
            var m = c.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (m != null) return m;
        }
        return null;
    }
}

/// <summary>Our AsyncOperation bookkeeping (scene/bundle/asset loads complete via callbacks we control).</summary>
public static class AsyncOps
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_Ptr")] public static extern ref IntPtr OpPtr(AsyncOperation c);
    public sealed class Op { public AsyncOperation Managed; public bool Done; public float Progress; public bool AllowActivation = true; public int Priority; public Func<bool> Poll; public Action OnComplete; public object Result; }
    static readonly Dictionary<IntPtr, Op> ByPtr = new();
    static long _next = 0x10000;
    public static Op Of(AsyncOperation a) => a != null && ByPtr.TryGetValue(OpPtr(a), out var o) ? o : null;
    public static bool IsDone(AsyncOperation a) => Of(a)?.Done ?? true;
    public static T Create<T>(Func<bool> poll = null, Action onComplete = null, object result = null) where T : AsyncOperation
    {
        var a = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        var p = new IntPtr(_next++); OpPtr(a) = p;
        ByPtr[p] = new Op { Managed = a, Poll = poll, OnComplete = onComplete, Result = result };
        return a;
    }
    /// <summary>Unity: reading an asset-bundle request's result before it is done (AssetBundleRequest.asset / allAssets,
    /// AssetBundleCreateRequest.assetBundle) stalls until the load is integrated, completes the request there and then, and fires its
    /// completed event before the getter returns. Addressables' WaitForCompletion relies on exactly this (BundledAssetProvider polls
    /// m_RequestOperation.asset; AssetBundleResource reads .assetBundle) -- with completion only at the end-of-frame Tick, every
    /// synchronous Addressables load failed (vita/notes/gameplay-survey.md item 1, experiment E1). Ops whose work is not ready
    /// (Poll false) or whose activation is held stay pending, as before.</summary>
    public static void CompleteNow(AsyncOperation a)
    {
        var o = Of(a);
        if (o == null || o.Done || !o.AllowActivation || (o.Poll != null && !o.Poll())) return;
        o.Done = true; o.Progress = 1f;
        try { o.OnComplete?.Invoke(); } catch (Exception e) { Log.Exception(e, "async onComplete"); }
        try { InvokeCompletionEvent(o.Managed); } catch (Exception e) { Log.Exception(e, "AsyncOperation.completed"); }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: AsyncOperation.completed handlers)
#endif
    }
    static Op[] _tickBuf = Array.Empty<Op>();
    public static void Tick()
    {
        int n = ByPtr.Count; if (_tickBuf.Length < n) _tickBuf = new Op[Math.Max(16, n * 2)];
        ByPtr.Values.CopyTo(_tickBuf, 0); var snap = _tickBuf;   // (was ByPtr.Values.ToArray(): the same elements, the same order)
        for (int k = 0; k < n; k++)
        {
            var o = snap[k];
            if (o.Done) continue;
            if (!o.AllowActivation) { o.Progress = Math.Max(o.Progress, 0.9f); continue; }
            if (o.Poll == null || o.Poll())
            {
                o.Done = true; o.Progress = 1f;
                try { o.OnComplete?.Invoke(); } catch (Exception e) { Log.Exception(e, "async onComplete"); }
                try { InvokeCompletionEvent(o.Managed); } catch (Exception e) { Log.Exception(e, "AsyncOperation.completed"); }
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: AsyncOperation.completed handlers)
#endif
            }
        }
        Array.Clear(snap, 0, n);
    }
    static void InvokeCompletionEvent(AsyncOperation a)
    {
        var m = typeof(AsyncOperation).GetMethod("InvokeCompletionEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        m?.Invoke(a, null);
    }
}

public static class Scenes
{
    public sealed class Info { public int Handle; public string Name, Path; public int BuildIndex = -1; public readonly List<NGameObject> Roots = new(); public bool Loaded = true; }
    public static readonly Dictionary<int, Info> All = new() { [1] = new Info { Handle = 1, Name = "Shepherd", Path = "Shepherd" } };
    public static int ActiveHandle = 1, DontDestroyHandle = -1;
    static int _next = 2;
    public static Info Active => All[ActiveHandle];
    public static Info Create(string name, string path) { var s = new Info { Handle = _next++, Name = name, Path = path }; All[s.Handle] = s; return s; }
    public static void AddRoot(NGameObject go, int scene = 0)
    {
        if (scene == 0) scene = ActiveHandle;
        go.Scene = scene; if (All.TryGetValue(scene, out var s) && !s.Roots.Contains(go)) s.Roots.Add(go);
    }
    public static void RemoveRoot(NGameObject go) { if (All.TryGetValue(go.Scene, out var s)) s.Roots.Remove(go); }
    public static void DontDestroyOnLoad(NGameObject go)
    {
        if (DontDestroyHandle < 0) { var s = Create("DontDestroyOnLoad", "DontDestroyOnLoad"); DontDestroyHandle = s.Handle; }
        var root = go.Transform.Root.Go; RemoveRoot(root); AddRoot(root, DontDestroyHandle); SetSceneRecursive(root, DontDestroyHandle);
    }
    public static void SetSceneRecursive(NGameObject go, int scene) { go.Scene = scene; foreach (var c in go.Transform.Children) SetSceneRecursive(c.Go, scene); }
    public static IEnumerable<NGameObject> AllGameObjects()
    {
        foreach (var s in All.Values.ToArray()) foreach (var r in s.Roots.ToArray()) foreach (var g in Walk(r)) yield return g;
    }
    public static IEnumerable<NGameObject> Walk(NGameObject go)
    {
        yield return go;
        foreach (var c in go.Transform.Children.ToArray()) foreach (var g in Walk(c.Go)) yield return g;
    }
}
