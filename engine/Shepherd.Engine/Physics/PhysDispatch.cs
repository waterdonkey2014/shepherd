using System.Collections;
using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 2 (physics-plan-v2 sections 1.7, 1.8, 3 step 7; section 4 "Stage 2"): the one dispatcher. At SHEPHERD_PHYS=2 the
/// core evaluates every trigger pair (patch P1: after the solve, H-TRIGPHASE; kinematic triggers, H-KINTRIG) and this class turns its
/// contact events into OnTriggerEnter2D / Stay2D / Exit2D -- Runtime/Triggers2D.cs's pair search is not run (never both). Grown from
/// Triggers2D.Send, keeping its semantics: the collider's GameObject plus its body's GameObject when that is another one (H-BODYMSG),
/// disabled behaviours included, coroutine-shaped handlers started, Awoken required; MethodInfo.Invoke replaced by closed delegates cached
/// per component (NMono.TrigD), and per-GameObject listener masks that skip pairs nobody listens to (never an event).
/// <list type="bullet">
/// <item><b>Pairs.</b> The core reports shape contacts (a decomposed polygon or a composite's chain has several shapes); a collider pair is
/// touching while any of its shape contacts is. Each phase compares the pair's state with the last phase's: Enter, Stay (every step while
/// touching; H-SLEEPSTAY=stop: not while all its non-static bodies sleep), Exit -- so a shape rebuilt between phases (a moved body-less
/// collider, a radius change) is a Stay, as the contact continues.</item>
/// <item><b>Order</b> (C8): H-KINDORDER=grouped (all trigger callbacks, then the collision group -- Stage 3 delivers that one; Stage 2 counts
/// its pairs only), H-CBORDER orders within a group: serial (the pair's first contact's creation serial, ascending), state (Exits, Enters,
/// Stays, each by serial), b2list (serial descending: Box2D's list, newest first), ids (collider ids) -- E1 left H-CBORDER open (Unity keeps
/// a persistent touching list, `list`, not implemented). Per pair: A's GameObject(s), then B's (A = the first contact's fixture A).</item>
/// <item><b>Exits on disable</b> (callbacksOnDisable; E1: H-EXITSYNC=sync, the only rule since E1): SetActive(false), enabled = false,
/// Destroy / DestroyImmediate of the GameObject or of the collider deliver the collider's pairs' Exits <b>inside the call</b>, both sides, the
/// leaving side included, with the live collider as the argument; its shapes leave the core at the next flush (queries see that at once).
/// Called from inside the callback phase, the Exits are appended to the end of the current kind's walk. Only Rigidbody2D.simulated = false
/// waits: its pairs end in the core at the next flush and their Exits come at the next phase -- and simulated set back before that phase is
/// no event at all (the Stays go on). A sync Exit followed by a re-enable in the same FixedUpdate gives the Enter at the step (E1's
/// H-REENABLE=exit+enter, which follows from the sync Exit: no rule of its own).</item>
/// <item><b>Recreation</b> (C9; E1: H-RECREATE=silent): an isTrigger flip or a layer change of a collider in the world that leaves the pair
/// touching is silent -- the pair goes on (Stays, in its new kind from that step); a layer change that ends the touch gives the Exit at the
/// step. <b>Re-entrancy</b>: structural changes made by handlers are queued and applied at the next flush; E1: H-DROPQUEUED=deliver -- the
/// phase's queued Enter / Stay of a collider a handler disabled earlier in the phase are still delivered (to it too), its Exit after them.</item>
/// <item><b>Reporting filter</b> (H-CAPTURE=report-only): a side hears about the other only when the other's layer is in its
/// callbackLayers (the 249 Hittable triggers carry 0x4016B01); a pair one of whose colliders leaves the other's layer out of its
/// contactCaptureLayers is silent on both sides (E1's Capture scene).</item>
/// <item><b>Checks</b>: SHEPHERD_PHYS_EVENTLOG=&lt;file&gt; (every delivered pair event, one line each); SHEPHERD_TRIGGERS2D=shadow|parity runs
/// Triggers2D's pair search beside the core without sending and classifies every difference (the swap A/B, plan Stage 2 acceptance #1);
/// SHEPHERD_PHYS_NOMASKS=1 (listener masks off: identical events, the masks only skip work).</item>
/// </list></summary>
[ProfileOnly]   // reads no property-bag key (typed records only); log text
public static class PhysDispatch
{
    // ---------------- hypothesis switches (Appendix A): since E1 only H-CBORDER is open here (the others have Unity's rule only) ----------------
    static readonly int CbOrder = Hypotheses.Get("CBORDER") switch { "state" => 1, "b2list" => 2, "ids" => 3, _ => 0 };
    public static readonly bool NoMasks = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_NOMASKS") == "1";
    /// <summary>physcheck room: deliveries are counted (those reaching a listener by the type tables), no handler is called.</summary>
    public static bool CountOnly;
    public static long StaysListened;
    /// <summary>Physics Stage 3: the collision group's counters (pair events, Stays not sent while asleep, handler calls).</summary>
    public static long CEnters, CStays, CExits, CStaysSlept, CSent;
    /// <summary>Does the type handle OnTriggerStay2D (the room counters' Stay listeners)?</summary>
    public static bool HandlesStay(Type t) => (InfoOf(t).Mask & (1 << KStay)) != 0;

    public const int KEnter = 0, KStay = 1, KExit = 2;
    /// <summary>Physics Stage 3: the collision messages' mask bits are the trigger ones shifted by this (a kind k of a solid pair is message 3 + k).</summary>
    public const int KColl = 3;
    static readonly string[] Msgs = { "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D", "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D" };
    static readonly string[] KindNames = { "ENTER", "stay", "EXIT", "C-ENTER", "c-stay", "C-EXIT" };
    /// <summary>Physics Stage 3: the collision group is delivered (level 3 with a core); below, solid pairs are tracked and counted only.</summary>
    static bool Coll => PhysWorld.Sim3;

    /// <summary>One collider pair of one kind (trigger / solid): its touching shape contacts, its order key, whether it was touching at the last
    /// phase (an Enter was delivered and no Exit yet).</summary>
    public sealed class Pair
    {
        public NCollider2D A, B; public bool Sensor; public int Count; public long Serial; public bool Reported;
        public long SuppressedStays; public int LastPhase;
        internal bool WasReported;   // Reported at the start of this phase (the recreation carry-over reads the other kind's)
    }
    static readonly Dictionary<(int, int, bool), Pair> _pairs = new();
    static readonly List<Pair> _gone = new();
    /// <summary>The phase window: markers set between two phases carry the id of the coming phase.</summary>
    public static int Window { get; private set; } = 1;
    struct Deliv { public Pair P; public int Kind; public long Key; public int Seq; }
    static readonly List<Deliv> _list = new();
    static readonly Dictionary<string, long> _slept = new();   // the pairs whose Stays stopped because their bodies slept (the summary)
    static ShepPhys.Event2[] _ev = new ShepPhys.Event2[256];
    static bool _dispatching;

    // counters (the exit summary)
    public static long Phases, EventsIn, Unresolved, Enters, Stays, Exits, StaysSlept, Sent, MaskSkips, CallbackLayerSkips, CaptureSkips, SyncExits,
                       AppendedExits, DeferredLeaves, CancelledExits, Recreations, CarriedOver, SolidPairsSeen, HandlerExceptions;
    public static int PeakPairs;

    static (int, int, bool) Key(NCollider2D a, NCollider2D b, bool sensor) => a.InstanceId < b.InstanceId ? (a.InstanceId, b.InstanceId, sensor) : (b.InstanceId, a.InstanceId, sensor);

    // ---------------- hooks (PhysWorld's membership hooks, level >= 2) ----------------
    /// <summary>A collider leaves the world (disabled, deactivated, destroyed): E1's rule (H-EXITSYNC=sync) -- its reported pairs' Exits now, inside
    /// the call; from inside the callback phase they are appended to the end of the current kind's walk instead. <paramref name="deferred"/>
    /// (Rigidbody2D.simulated = false, the one deferred case E1 found) only marks it: the pairs end in the core at the next flush and their Exits come
    /// at the next phase, or nothing at all if simulated is set back before it. Its shapes leave the core at the next flush either way.</summary>
    public static void Leaving(NCollider2D c, bool deferred = false)
    {
        c.LeftMark = Window;
        if (deferred) { DeferredLeaves++; return; }
        if (_pairs.Count == 0) return;
        _sync.Clear();
        foreach (var p in _pairs.Values) if ((p.Sensor || Coll) && p.Reported && (ReferenceEquals(p.A, c) || ReferenceEquals(p.B, c))) _sync.Add(p);
        if (_sync.Count > 1) _sync.Sort((x, y) => x.Serial.CompareTo(y.Serial));   // (several pairs: the order key's base order)
        foreach (var p in _sync)
        {
            p.Reported = false;
            if (_dispatching) { AppendExit(p); continue; }
            SyncExits++; if (p.Sensor) Exits++; else CExits++;
            Log1(p, KExit); Deliver(p, KExit);
        }
    }
    static readonly List<Pair> _sync = new();
    /// <summary>Inside the callback phase: the Exit goes to the end of its kind's walk (a trigger pair's before the collision group; a collision
    /// pair's at the end), and never before the delivery being made now (E1's DropQueued scene: the disabled trigger's queued Enters first).</summary>
    static void AppendExit(Pair p)
    {
        AppendedExits++;
        int at = _list.Count;
        if (p.Sensor) { at = 0; while (at < _list.Count && _list[at].P.Sensor) at++; }
        if (at <= _cur) at = _cur + 1;
        _list.Insert(at, new Deliv { P = p, Kind = KExit, Key = 0, Seq = -1 });
    }
    static int _cur = -1;
    /// <summary>set_isTrigger / a layer change of a collider in the world: its pairs are flagged for recreation (H-RECREATE).</summary>
    public static void Recreated(NCollider2D c) { c.RecreateMark = Window; Recreations++; }

    // ---------------- the phase (slot B, after the step and write-back; InFixedStep still set) ----------------
    /// <summary>Drains the core's events into the pair table, then compiles and delivers this step's trigger callbacks.</summary>
    public static void Phase(ShepPhys core, int world, int step)
    {
        Phases++;
        Drain(core, world);
        if (PhysConfig.CheckBrute) BruteCheck(core, world, step);
        PhysWorld.RetireShapes();
        int w = Window;
        _list.Clear(); _gone.Clear();
        int seq = 0;
        foreach (var p in _pairs.Values) p.WasReported = p.Reported;
        foreach (var p in _pairs.Values)
        {
            if (!p.Sensor && !Coll) { p.Reported = p.Count > 0; if (p.Count == 0) _gone.Add(p); continue; }   // below level 3 the collision group is tracked (the recreation carry-over), not delivered
            bool now = p.Count > 0, was = p.WasReported;
            bool recreated = p.A.RecreateMark == w || p.B.RecreateMark == w;
            if (was && now)
            {   // (a recreated pair still touching in its kind, a body re-simulated before this phase: the pair goes on -- E1's RECREATE / ReEnable lane 2)
                if (p.A.LeftMark == w || p.B.LeftMark == w) CancelledExits++;
                if (Awake(p)) Add(p, KStay, ref seq);
                else { p.SuppressedStays++; if (p.Sensor) StaysSlept++; else CStaysSlept++; var sk = $"{Desc(p.A)} x {Desc(p.B)}"; _slept[sk] = _slept.GetValueOrDefault(sk) + 1; }
            }
            else if (!was && now)
            {
                // H-RECREATE=silent: an isTrigger flip moved the pair to the other kind -- the touch goes on as a Stay of the new kind (no Enter)
                if (recreated && Other(p) is { WasReported: true } o) { CarriedOver++; o.Reported = false; Add(p, KStay, ref seq); }
                else Add(p, KEnter, ref seq);
                p.Reported = true;
            }
            else if (was && !now)
            {
                if (!(recreated && Other(p) is { } o && o.Count > 0)) Add(p, KExit, ref seq);   // (carried over to the other kind: silent)
                p.Reported = false;
            }
            if (!now && !p.Reported) _gone.Add(p);
            p.LastPhase = w;
        }
        foreach (var p in _gone) _pairs.Remove(Key(p.A, p.B, p.Sensor));
        if (_pairs.Count > PeakPairs) PeakPairs = _pairs.Count;
        _list.Sort(Compare);
#if !SHEPHERD_EBOOT
        if (Triggers2D.ShadowOn) PhysShadow.Compare(_list.Where(d => d.P.Sensor).Select(d => (d.P.A, d.P.B, d.Kind)).ToList(), step);   // (the trigger group only: Stage 3 adds collisions)
#endif
        Window = w + 1;
        _dispatching = true;
        _masks.Clear();
        try
        {
            for (_cur = 0; _cur < _list.Count; _cur++)   // (by index: a handler's disable appends Exits to this walk, AppendExit)
            {
                var d = _list[_cur];
                var p = d.P;
                if (!p.Sensor) { if (d.Kind == KEnter) CEnters++; else if (d.Kind == KStay) CStays++; else CExits++; }
                else if (d.Kind == KEnter) Enters++; else if (d.Kind == KStay) Stays++; else Exits++;
                Log1(p, d.Kind, step);   // (H-DROPQUEUED=deliver: a collider a handler disabled earlier in this phase still gets its queued events)
                Deliver(p, d.Kind);
            }
        }
        finally { _dispatching = false; _cur = -1; }
    }
    static void Add(Pair p, int kind, ref int seq)
    {
        long key = CbOrder switch
        {
            1 => ((long)(kind == KExit ? 0 : kind == KEnter ? 1 : 2) << 48) | p.Serial,
            2 => -p.Serial,
            3 => ((long)Math.Min(p.A.InstanceId, p.B.InstanceId) << 32) ^ (uint)Math.Max(p.A.InstanceId, p.B.InstanceId),
            _ => p.Serial
        };
        _list.Add(new Deliv { P = p, Kind = kind, Key = key, Seq = seq++ });
    }
    static int Compare(Deliv a, Deliv b)
    {
        // H-KINDORDER=grouped (E1): every trigger callback, then every collision callback (Stage 3 delivers the second group)
        if (a.P.Sensor != b.P.Sensor) return a.P.Sensor ? -1 : 1;
        int c = a.Key.CompareTo(b.Key);
        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
    }
    /// <summary>Is one of the pair's non-static bodies awake (H-SLEEPSTAY)? A collider without a body (or on a Static-type one) is static.</summary>
    static bool Awake(Pair p) => BodyAwake(p.A) || BodyAwake(p.B);
    /// <summary>Is the pair evaluated by the core this step: one of its colliders on a non-static body that is awake (Box2D's awake rule, shared by
    /// P1's final pass)? The swap A/B's "sleeping bodies" class.</summary>
    internal static bool PairAwake(NCollider2D a, NCollider2D b) => BodyAwake(a) || BodyAwake(b);
    static bool BodyAwake(NCollider2D c) => c.PS?.Body is { } b && b.Handle > 0 && b.Def.Type != ShepPhys.BodyStatic && PhysWorld.MirrorAwake(b);
    /// <summary>The same collider pair's record of the other kind (trigger / solid): an isTrigger flip moves a touching pair between them.</summary>
    static Pair Other(Pair p) => _pairs.TryGetValue(Key(p.A, p.B, !p.Sensor), out var o) ? o : null;

    static void Drain(ShepPhys core, int world)
    {
        while (true)
        {
            int n = core.Events2(world, _ev);
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                ref var e = ref _ev[i];
                EventsIn++;
                var a = PhysWorld.ColliderOfShape(e.ShapeA); var b = PhysWorld.ColliderOfShape(e.ShapeB);
                if (a == null || b == null || ReferenceEquals(a, b)) { Unresolved++; continue; }
                bool sensor = (e.Flags & ShepPhys.EvfSensor) != 0;
                var k = Key(a, b, sensor);
                if (!_pairs.TryGetValue(k, out var p))
                {
                    if (e.Kind != ShepPhys.EvBegin) { Unresolved++; continue; }   // an End for a pair never begun (its Begin was before the world existed)
                    _pairs[k] = p = new Pair { A = a, B = b, Sensor = sensor, Serial = e.Serial };
                    if (!sensor) SolidPairsSeen++;
                }
                if (e.Kind == ShepPhys.EvBegin) p.Count++; else if (p.Count > 0) p.Count--;
            }
            if (n < _ev.Length) break;
        }
    }

    // ---------------- SHEPHERD_CHECK_PHYS=brute: the core's touching trigger pairs against a brute force (no broadphase) ----------------
    public static long BruteSteps, BrutePairsCore, BruteDiffs;
    static readonly int[] _bo = new int[6];
    static void BruteCheck(ShepPhys core, int world, int step)
    {
        int d = core.UBrutePairs(world, _bo);
        BruteSteps++; BrutePairsCore += _bo[0];
        if (d == 0) return;
        BruteDiffs += d;
        if (BruteDiffs <= 20) Log.Error($"[phys] FAIL phys brute step {step}: core {_bo[0]} touching sensor pairs, brute force {_bo[1]}: {_bo[2]} missing, {_bo[3]} extra; first: " +
                                        $"{(PhysWorld.ColliderOfShape(_bo[4]) is { } a ? Desc(a) : _bo[4].ToString())} x {(PhysWorld.ColliderOfShape(_bo[5]) is { } b ? Desc(b) : _bo[5].ToString())}");
    }

    // ---------------- delivery ----------------
    static void Deliver(Pair p, int kind)
    {
        int msg = p.Sensor ? kind : KColl + kind;   // (Stage 3: a solid pair's kind is a collision message)
        if (CountOnly) { if (((SideMask(p.A) | SideMask(p.B)) & (1 << msg)) != 0 && kind == KStay && p.Sensor) StaysListened++; return; }
        // contactCaptureLayers (E1's Capture scene): a collider leaving the other's layer out of it silences the pair on both sides
        if ((((p.A.ContactCaptureLayers >> ((p.B.Go?.Layer ?? 0) & 31)) & 1) == 0) || (((p.B.ContactCaptureLayers >> ((p.A.Go?.Layer ?? 0) & 31)) & 1) == 0)) { CaptureSkips++; return; }
        if (!NoMasks && ((SideMask(p.A) | SideMask(p.B)) & (1 << msg)) == 0) { MaskSkips++; return; }
        SendSide(p, p.A, p.B, msg);
        SendSide(p, p.B, p.A, msg);
    }
    static void SendSide(Pair p, NCollider2D to, NCollider2D other, int msg)
    {
        if (to.Destroyed || to.Managed == null || other.Managed == null) return;
        int ol = (other.Go?.Layer ?? 0) & 31;
        if (((to.CallbackLayers >> ol) & 1) == 0) { CallbackLayerSkips++; return; }   // H-CAPTURE=report-only: callbackLayers
        if (!NoMasks && (SideMask(to) & (1 << msg)) == 0) { MaskSkips++; return; }
        // the argument: the other collider (triggers), or a new Collision2D for this side (Stage 3; reuseCollisionCallbacks = 0)
        object arg = msg < KColl ? other.Managed : Collisions.Build(to, other, msg == KColl + KExit);
        Send(to.Go, arg, msg);
        if (PhysWorld.AttachedRecord(to) is { } body && body.Go != null && !ReferenceEquals(body.Go, to.Go)) Send(body.Go, arg, msg);   // H-BODYMSG=collider+body (E1)
    }
    static void Send(NGameObject go, object arg, int msg)   // (no Unity physics type in a static signature: IcallRegistry's scan)
    {
        if (go == null || go.Destroyed) return;
        var cs = go.Components;
        for (int i = 0; i < cs.Count; i++)
        {
            if (cs[i] is not NMono m || m.Destroyed || !m.Awoken || m.Managed == null) continue;
            var h = Handlers(m);
            var d = h?[msg];
            if (d == null) continue;
            try { d.Invoke(m, arg); Sent++; if (msg >= KColl) CSent++; }
            catch (Exception e)
            {
                HandlerExceptions++; Log.Exception(e is TargetInvocationException t ? t.InnerException ?? e : e, $"{Msgs[msg]} {m.Managed.GetType().Name} '{go.Name}'");
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests' SHEPHERD_TEST_SPINE (kit #8: physics' callback site, threw; vita/tests/pct/spine-hooks.py)
#endif
            }
        }
    }

    // ---- handlers: per type the methods, per component closed delegates (NMono.TrigD) ----
    /// <summary>A bound handler: a closed delegate over the component (MethodInfo.Invoke only when a delegate cannot be made).</summary>
    sealed class Bound
    {
        public Action<Collider2D> A1; public Action A0; public Func<Collider2D, IEnumerator> C1; public Func<IEnumerator> C0; public MethodInfo Mi; public bool OneArg;
        public Action<Collision2D> K1; public Func<Collision2D, IEnumerator> KC1;   // physics Stage 3: the collision handlers' closed delegates
        public void Invoke(NMono m, object arg)
        {
            IEnumerator e = null;
            if (A1 != null) A1((Collider2D)arg); else if (K1 != null) K1((Collision2D)arg); else if (A0 != null) A0();
            else if (C1 != null) e = C1((Collider2D)arg); else if (KC1 != null) e = KC1((Collision2D)arg); else if (C0 != null) e = C0();
            else { var r = Mi.Invoke(m.Managed, OneArg ? new object[] { arg } : null); e = r as IEnumerator; }
#if !SHEPHERD_EBOOT
            if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests' SHEPHERD_TEST_SPINE (kit #8: physics' 2D trigger / collision messages; vita/tests/pct/spine-hooks.py)
#endif
            if (e != null) Coroutines.Start(m, e, Mi.Name);
        }
    }
    sealed class TypeInfo { public MethodInfo[] Mi = new MethodInfo[6]; public int Mask; }
    static readonly Dictionary<Type, TypeInfo> _types = new();
    static TypeInfo InfoOf(Type t)
    {
        if (_types.TryGetValue(t, out var ti)) return ti;
        ti = new TypeInfo();
        for (int k = 0; k < 6; k++) { ti.Mi[k] = Find(t, Msgs[k], k >= KColl ? typeof(Collision2D) : typeof(Collider2D)); if (ti.Mi[k] != null) ti.Mask |= 1 << k; }
        return _types[t] = ti;
    }
    /// <summary>The type's handler (its own or inherited, any visibility): the Collider2D overload first, else the parameterless one
    /// (Triggers2D.Find's rule).</summary>
    static MethodInfo Find(Type t, string msg, Type argType)
    {
        MethodInfo mi = null;
        for (var c = t; c != null && c != typeof(MonoBehaviour) && mi == null; c = c.BaseType)
            mi = c.GetMethod(msg, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { argType }, null)
                 ?? c.GetMethod(msg, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        return mi;
    }
    static Bound[] Handlers(NMono m)
    {
        if (m.TrigD is Bound[] b) return b;
        var ti = InfoOf(m.Managed.GetType());
        if (ti.Mask == 0) { m.TrigD = _none; return _none; }
        b = new Bound[6];
        for (int k = 0; k < 6; k++)
        {
            var mi = ti.Mi[k]; if (mi == null) continue;
            var x = new Bound { Mi = mi, OneArg = mi.GetParameters().Length == 1 };
            try
            {
                bool co = typeof(IEnumerator).IsAssignableFrom(mi.ReturnType);
                if (x.OneArg && k >= KColl) { if (mi.ReturnType == typeof(void)) x.K1 = (Action<Collision2D>)Delegate.CreateDelegate(typeof(Action<Collision2D>), m.Managed, mi); else if (co && mi.ReturnType == typeof(IEnumerator)) x.KC1 = (Func<Collision2D, IEnumerator>)Delegate.CreateDelegate(typeof(Func<Collision2D, IEnumerator>), m.Managed, mi); }
                else if (x.OneArg) { if (mi.ReturnType == typeof(void)) x.A1 = (Action<Collider2D>)Delegate.CreateDelegate(typeof(Action<Collider2D>), m.Managed, mi); else if (co && mi.ReturnType == typeof(IEnumerator)) x.C1 = (Func<Collider2D, IEnumerator>)Delegate.CreateDelegate(typeof(Func<Collider2D, IEnumerator>), m.Managed, mi); }
                else { if (mi.ReturnType == typeof(void)) x.A0 = (Action)Delegate.CreateDelegate(typeof(Action), m.Managed, mi); else if (co && mi.ReturnType == typeof(IEnumerator)) x.C0 = (Func<IEnumerator>)Delegate.CreateDelegate(typeof(Func<IEnumerator>), m.Managed, mi); }
            }
            catch (Exception) { /* MethodInfo.Invoke stays the fallback */ }
            b[k] = x;
        }
        m.TrigD = b;
        return b;
    }
    static readonly Bound[] _none = new Bound[6];

    // ---- listener masks: per GameObject (this phase), which trigger messages any of its awoken behaviours handles ----
    static readonly Dictionary<NGameObject, int> _masks = new();
    static int GoMask(NGameObject go)
    {
        if (go == null || go.Destroyed) return 0;
        if (_dispatching && _masks.TryGetValue(go, out var m)) return m;
        int mask = 0;
        foreach (var c in go.Components) if (c is NMono n && !n.Destroyed && n.Managed != null) mask |= InfoOf(n.Managed.GetType()).Mask;
        if (_dispatching) _masks[go] = mask;
        return mask;
    }
    /// <summary>The messages one side of a pair can hear: its collider's GameObject and (H-BODYMSG) its body's.</summary>
    static int SideMask(NCollider2D c)
    {
        int m = GoMask(c.Go);
        if (PhysWorld.AttachedRecord(c) is { } b && b.Go != null && !ReferenceEquals(b.Go, c.Go)) m |= GoMask(b.Go);
        return m;
    }

    // ---------------- the event log (SHEPHERD_PHYS_EVENTLOG) ----------------
    static readonly StreamWriter _log = OpenLog();
    static StreamWriter OpenLog()
    {
        var p = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_EVENTLOG");
        if (string.IsNullOrEmpty(p) || PhysConfig.Level < 2) return null;
        try { return new StreamWriter(p, false) { AutoFlush = false }; } catch (Exception e) { Log.Error($"[phys] SHEPHERD_PHYS_EVENTLOG: {e.Message}"); return null; }
    }
    static void Log1(Pair p, int kind, int step = -1)
    {
        if (_log == null) return;
        _log.WriteLine($"{(step >= 0 ? step : PhysWorld.StepIndex)}\t{Runtime.Time.FrameCount}\t{KindNames[p.Sensor ? kind : KColl + kind]}\t{p.Serial}\t{Desc(p.A)}\t{Desc(p.B)}");   // (a solid pair: C-ENTER / c-stay / C-EXIT)
    }
    public static string Desc(NCollider2D c) => $"{c.Go?.Name}#{c.InstanceId}:{c.Kind}:L{(c.Go?.Layer ?? 0)}{(c.IsTrigger ? ":T" : "")}";
    public static void Flush() => _log?.Flush();
#if !SHEPHERD_EBOOT
    static string ShadowSummary() => Triggers2D.ShadowOn ? "\n" + PhysShadow.Summary() : "";   // the swap A/B (Physics/PhysShadow.cs: Mac-only)
#else
    static string ShadowSummary() => "";
#endif

    public static string Summary() =>
        $"[phys] stage 2 dispatch: {Phases} phases, {EventsIn} core events ({Unresolved} unresolved), pairs now {_pairs.Count} (peak {PeakPairs}); trigger callbacks: " +
        $"{Enters} Enter, {Stays} Stay, {Exits} Exit pair events ({StaysSlept} Stays not sent: all bodies asleep, H-SLEEPSTAY), {Sent} handler calls, {MaskSkips} skipped by listener masks" +
        $"{(NoMasks ? " (masks OFF)" : "")}, {CallbackLayerSkips} by callbackLayers (H-CAPTURE), {CaptureSkips} by contactCaptureLayers, {SyncExits} sync exits (+{AppendedExits} appended inside the phase), " +
        $"{DeferredLeaves} deferred leaves (simulated) / {CancelledExits} cancelled, {Recreations} recreations ({CarriedOver} carried over silently), " +
        $"{HandlerExceptions} handler exceptions; solid pairs seen {SolidPairsSeen} (their callbacks: {(Coll ? "delivered, Stage 3" : "Stage 3")}); hypotheses " +
        string.Join(" ", new[] { "TRIGPHASE", "KINTRIG", "KINSLEEP", "SLEEPSTAY", "KINISLAND", "EXITSYNC", "REENABLE", "BODYMSG", "KINDORDER", "CBORDER", "RECREATE", "DROPQUEUED", "CAPTURE", "TIE" }.Select(n => $"H-{n}={Hypotheses.Get(n)}")) +
        ShadowSummary() +
        (PhysConfig.CheckBrute ? $"\n[phys] brute check: {BruteSteps} steps, {BrutePairsCore} touching trigger pairs compared, {BruteDiffs} differences; queries: {Physics2DQueries.BruteSummary()}" : "") +
        (_slept.Count > 0 ? $"\n[phys] stage 2 dispatch: {_slept.Count} pairs whose Stays stopped while their bodies slept (H-KINSLEEP={Hypotheses.Get("KINSLEEP")}, H-SLEEPSTAY={Hypotheses.Get("SLEEPSTAY")}), most: " +
            string.Join("; ", _slept.OrderByDescending(k => k.Value).Take(40).Select(k => $"{k.Key} {k.Value}")) : "");

    /// <summary>Physics Stage 3: the collision group's counters (PhysWorld.Summary3).</summary>
    public static string Summary3() =>
        $"collision callbacks: {CEnters} Enter, {CStays} Stay, {CExits} Exit pair events ({CStaysSlept} Stays not sent: all bodies asleep), {CSent} handler calls; " +
        $"Collision2D built {Collisions.Built} ({Collisions.Points} contact points, {Collisions.Exits} for exits)";

    /// <summary>The pairs touching now (physcheck, the room counters).</summary>
    public static IEnumerable<Pair> Pairs => _pairs.Values;
}
