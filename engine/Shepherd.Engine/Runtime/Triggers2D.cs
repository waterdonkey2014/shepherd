using System.Collections;
using System.Reflection;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>Fidelity round 1, the first slice of Physics2D (survey item 2): trigger callbacks only -- OnTriggerEnter2D / Stay2D /
/// Exit2D between 2D colliders, with no simulation (nothing moves, nothing is pushed apart). Needed for the look: CotL switches a
/// room's lighting (LUTs, light colour, fog: LightingManagerVolume) from OnTriggerStay2D when the player is inside the room's
/// volume, so the intro's crimson palette never appeared. Unity's rules as implemented:
/// - evaluated once per physics step, after the scripts' FixedUpdate and before the WaitForFixedUpdate coroutines, with
///   Time.InFixedStep set; Enter on the first step a pair overlaps, Stay on every later step, Exit on the first step it no longer
///   does (separated, a collider disabled / deactivated, its body made unsimulated, the pair or its layers ignored)
/// - the project's Physics 2D settings are the game's own (Physics2DSettings.cs, read from the owner's globalgamemanagers; fid1-fixup
///   D3): a pair whose layers do not collide in the layer collision matrix is never considered (tested before any geometry); with
///   callbacksOnDisable off a disabled collider's pairs end without Exit
/// - a pair is considered when at least one collider is a trigger and at least one is attached to a non-static body: a simulated
///   Rigidbody2D (its own or the nearest parent's) whose bodyType is not Static (D4: a Static body pairs like no body, so
///   static-static never pairs); the colliders of a body with simulated = false take no part at all (D4); the two may not be
///   attached to the same body; Physics2D.IgnoreCollision pairs are skipped (D4)
/// - a collider destroyed while touching (D5: callbacksOnDisable): the side that survives gets OnTriggerExit2D at the destroy, with the
///   destroyed collider as the argument while it is still valid; a disabled / deactivated collider's Exit goes to both sides at the
///   next step (unverified against Unity's exact timing: Unity reports it during the deactivation)
/// - the message goes to every MonoBehaviour on each collider's GameObject (and on its body's GameObject when that is another
///   one), enabled or not (Unity sends collision messages to disabled behaviours); a coroutine-shaped handler is started
/// - shapes: CircleCollider2D (radius x the larger of the x/y lossy scales), BoxCollider2D (a rotated, scaled box, edge radius
///   ignored); CapsuleCollider2D as its box; Polygon / Edge / Composite colliders take no part yet (logged once)
/// Cost (D6): shapes and bodies are cached per collider and rebuilt only when their inputs change (the transform chain's stamp, the
/// collider's offset / size / radius, the parent chain or the set of Rigidbody2D records); handlers are cached per type with their
/// parameter count; a step allocates nothing once its tables have grown. SHEPHERD_TRIGGERS2D=0 turns it off.</summary>
public static class Triggers2D
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_TRIGGERS2D") != "0";
    /// <summary>Physics stage 0 (physics-plan-v2 1.7, the matrix A/B): SHEPHERD_TRIGGERS2D=nomatrix tests every layer pair (fid1-fixup D3's
    /// matrix off) -- a check mode that lists, with SHEPHERD_TRIGGERS2D_LOG=1, the trigger pairs the matrix removes. Never a default. (Read
    /// in Physics/PhysConfig.cs: this class's literals are part of the diet's readable-key scan.)</summary>
    static readonly bool NoMatrix = Physics.PhysConfig.TriggersNoMatrix;
    /// <summary>Physics Stage 2 (physics-plan-v2 section 4 Stage 2 acceptance #1, the swap A/B): at SHEPHERD_PHYS=2 the core runs the triggers
    /// (Physics/PhysDispatch.cs) and this pass is not run -- except as a check: SHEPHERD_TRIGGERS2D=shadow runs the pair search beside the core
    /// on the same post-step world and records instead of sending (Physics/PhysShadow.cs compares the two, step by step); =parity is the strict
    /// variant (body-owned colliders at the core's poses, boxes rounded by Box2D's b2_polygonRadius). Never a default; read in PhysConfig
    /// (this class's literals are part of the diet's readable-key scan).</summary>
    public static readonly bool ShadowOn = Physics.PhysConfig.TriggersShadow, Parity = Physics.PhysConfig.TriggersParity;
    static List<(NBehaviour, NBehaviour, int)> _rec;
    static long _coreStamp = -1000;
    static readonly List<(NBehaviour, NBehaviour, int)> _recDestroyed = new();
    const int KNone = 0, KCircle = 1, KBox = 2, KCapsule = 3;
    sealed class Shape
    {
        public NBehaviour C; public int Kind; public NComponent Body; public bool Trigger, Moving; public int Stamp, Layer;
        // the body lookup, valid while the parent chain and the set of Rigidbody2D records (Registry.Bodies2D) are unchanged
        public int BodyVer = -1, ChainLen = -1; public NTransform[] Chain = new NTransform[8];
        // the world shape, valid while the transform chain's stamp and the collider's own values are unchanged
        public long GeoStamp = -1; public Vector2 GOff, GSize; public float GRad;
        public float Cx, Cy, R; public readonly float[] Px = new float[4], Py = new float[4]; public float MinX, MinY, MaxX, MaxY;
    }
    static readonly Dictionary<NBehaviour, Shape> _shapes = new();
    static readonly List<Shape> _live = new(), _moving = new();
    static readonly Dictionary<(Shape, Shape), int> _contacts = new(), _next = new();
    static readonly List<(Shape, Shape)> _ended = new(), _hit = new();
    static readonly HashSet<(Shape, Shape)> _exited = new();
    static readonly HashSet<(int, int)> _ignored = new();
    static readonly List<NBehaviour> _gone = new();
    static int _stamp; static bool _dispatching;
    /// <summary>Counters of the last step: colliders considered, pair tests (shape tests after the layer / body / AABB filters),
    /// callbacks sent, overlapping pairs.</summary>
    public static int LastColliders, LastTests, LastCallbacks, LastPairs;
    /// <summary>Since start: steps run, pair-steps (overlapping pairs summed over the steps), candidate pairs the layer matrix skipped.</summary>
    public static long Steps, PairSteps, MatrixSkips;

    /// <summary>Physics Stage 2's shadow pass (the swap A/B): this step's pair events (collider A, collider B, 0 Enter / 1 Stay / 2 Exit) as Step
    /// would send them, recorded instead; the overlapping pairs (the state) in <paramref name="state"/>. A destroy's Exit (D5) recorded since the
    /// last call comes first.</summary>
    public static List<(NBehaviour, NBehaviour, int)> ShadowStep(List<(NBehaviour, NBehaviour)> state)
    {
        var rec = new List<(NBehaviour, NBehaviour, int)>(_recDestroyed); _recDestroyed.Clear();
        _rec = rec;
        try { Step(); } finally { _rec = null; }
        state.Clear(); foreach (var kv in _contacts) state.Add((kv.Key.Item1.C, kv.Key.Item2.C));
        return rec;
    }

    public static void Step()
    {
        if (!On) return;
        _stamp++; _live.Clear(); _moving.Clear(); int tests = 0, calls = 0;
        foreach (var o in Registry.Colliders2D.Items())
        {
            if (o is not NBehaviour c || c.Destroyed || !c.Enabled || c.Go == null || c.Go.Scene == 0 || !c.Go.ActiveInHierarchy) continue;
            if (!_shapes.TryGetValue(c, out var s)) _shapes[c] = s = new Shape { C = c, Kind = KindOf(c) };
            if (s.Kind == KNone || !Build(s)) continue;
            s.Stamp = _stamp; _live.Add(s); if (s.Moving) _moving.Add(s);
        }
        _next.Clear();
        foreach (var a in _moving)
            foreach (var b in _live)
            {
                if (ReferenceEquals(a, b) || (!a.Trigger && !b.Trigger) || ReferenceEquals(a.Body, b.Body)) continue;
                if (b.Moving && b.C.InstanceId < a.C.InstanceId) continue;   // a pair of two moving colliders: once
                if (!Physics2DSettings.Collide(a.Layer, b.Layer)) { MatrixSkips++; if (!NoMatrix) continue; }   // D3: the project's layer matrix (NoMatrix: counted, not applied)
                if (_ignored.Count > 0 && _ignored.Contains(Key(a.C, b.C))) continue;           // D4: Physics2D.IgnoreCollision
                float sm = Parity ? 2f * Skin : 0f;   // parity: the AABB test grown by both skins (Box2D's fat AABBs never cut a pair within them)
                if (a.MaxX + sm < b.MinX || b.MaxX + sm < a.MinX || a.MaxY + sm < b.MinY || b.MaxY + sm < a.MinY) continue;
                tests++;
                if (!Overlap(a, b)) continue;
                var key = a.C.InstanceId < b.C.InstanceId ? (a, b) : (b, a);
                _next[key] = _contacts.ContainsKey(key) ? 2 : 1;
            }
        // Exit for pairs that ended, then Enter / Stay. Handlers may destroy colliders (Destroying runs then): the tables walked here are
        // never changed during the callbacks; a pair whose collider died in them is dropped below
        _ended.Clear(); foreach (var kv in _contacts) if (!_next.ContainsKey(kv.Key)) _ended.Add(kv.Key);
        _exited.Clear(); _dispatching = true;
        try
        {
            foreach (var (a, b) in _ended)
            {
                if (_exited.Contains((a, b))) continue;
                if ((a.Stamp != _stamp || b.Stamp != _stamp) && !Physics2DSettings.CallbacksOnDisable) continue;   // a collider left the step: Exit only with callbacksOnDisable
                if (Trace.On) Trace.Event(2, a, b);
                calls += Pair(a, b, 2);
            }
            foreach (var kv in _next)
            {
                if (_exited.Contains(kv.Key)) continue;
                if (Trace.On && kv.Value == 1) Trace.Event(0, kv.Key.Item1, kv.Key.Item2);
                calls += Pair(kv.Key.Item1, kv.Key.Item2, kv.Value == 1 ? 0 : 1);
            }
        }
        finally { _dispatching = false; }
        _contacts.Clear();
        foreach (var kv in _next) if (!kv.Key.Item1.C.Destroyed && !kv.Key.Item2.C.Destroyed && !_exited.Contains(kv.Key)) _contacts[kv.Key] = kv.Value;
        if (_shapes.Count > _live.Count * 4 + 256)
        {
            _gone.Clear(); foreach (var kv in _shapes) if (kv.Value.Stamp != _stamp) _gone.Add(kv.Key);
            foreach (var k in _gone) _shapes.Remove(k); _gone.Clear();
        }
        Steps++; PairSteps += _next.Count;
        LastColliders = _live.Count; LastTests = tests; LastCallbacks = calls; LastPairs = _next.Count;
    }

    // ---- D4: Physics2D.IgnoreCollision (Physics2DSettings' icalls) ----
    static (int, int) Key(NBehaviour a, NBehaviour b) => a.InstanceId < b.InstanceId ? (a.InstanceId, b.InstanceId) : (b.InstanceId, a.InstanceId);
    /// <summary>Physics2D.IgnoreCollision(a, b, ignore). A touching pair then ends with Exit at the next step (Unity reports it too).</summary>
    public static void Ignore(NBehaviour a, NBehaviour b, bool ignore) { if (ReferenceEquals(a, b)) return; if (ignore) _ignored.Add(Key(a, b)); else _ignored.Remove(Key(a, b)); Physics.PhysWorld.IgnoreChanged(a, b, ignore); }
    public static bool Ignored(NBehaviour a, NBehaviour b) => _ignored.Contains(Key(a, b));

    // ---- D4: the body properties these rules read, served from the property bag that scene data seeds (Rigidbody2D's auto-property
    // hooks are never bound: the Physics2D module loads after AutoProps.Register, so the game's writes were lost). Collider2D's own
    // setters (isTrigger, offset, radius, size) are still not served: the intro writes radius and isTrigger, so serving them changes
    // its trigger set -- left to the physics round ----
    [Icall("UnityEngine.Rigidbody2D::get_simulated")] static object GetSimulated(object[] a) => Objects.Get(a[0])?.Get("simulated") is not { } v || Convert.ToBoolean(v);
    [Icall("UnityEngine.Rigidbody2D::set_simulated")] static object SetSimulated(object[] a) { Objects.Get(a[0])?.Set("simulated", Convert.ToBoolean(a[1])); return null; }
    [Icall("UnityEngine.Rigidbody2D::get_bodyType")] static object GetBodyType(object[] a) => Enum.ToObject(typeof(RigidbodyType2D), Objects.Get(a[0])?.Get("bodyType") is { } v ? Convert.ToInt32(v) : 0);
    [Icall("UnityEngine.Rigidbody2D::set_bodyType")] static object SetBodyType(object[] a) { Objects.Get(a[0])?.Set("bodyType", Convert.ToInt32(a[1])); return null; }

    // ---- D5: a collider destroyed while touching ----
    /// <summary>Lifecycle.DestroyNow, before the object is deactivated or destroyed: every pair a collider in it (the GameObject's
    /// whole subtree, or the collider itself) is part of ends now. With callbacksOnDisable (the project's setting) the side that survives
    /// hears OnTriggerExit2D, with the dying collider as the argument (it is still valid here); a pair of two dying colliders has
    /// nobody left to tell. Never sends to the dying side (its scripts are being destroyed).</summary>
    internal static void Destroying(NObj n)
    {
        if (_contacts.Count == 0 && !(_dispatching && _next.Count > 0)) return;
        var root = n as NGameObject ?? (n as NTransform)?.Go; var col = root == null ? n as NBehaviour : null;
        if (root == null && col == null) return;
        _hit.Clear();
        foreach (var kv in _contacts) if (Dies(kv.Key.Item1, root, col) || Dies(kv.Key.Item2, root, col)) _hit.Add(kv.Key);
        if (_dispatching) foreach (var kv in _next) if (!_contacts.ContainsKey(kv.Key) && (Dies(kv.Key.Item1, root, col) || Dies(kv.Key.Item2, root, col))) _hit.Add(kv.Key);
        if (_hit.Count == 0) return;
        var hit = _hit.ToArray(); _hit.Clear();   // the callbacks below may destroy more (re-entering here)
        foreach (var key in hit)
        {
            if (!_contacts.Remove(key) && !_dispatching) continue;
            if (_dispatching && !_exited.Add(key)) continue;
            var (a, b) = key; bool da = Dies(a, root, col), db = Dies(b, root, col);
            if (da && db || !Physics2DSettings.CallbacksOnDisable) continue;
            if (Trace.On) Trace.Event(3, a, b);
            if (ShadowOn) { _recDestroyed.Add((a.C, b.C, 2)); continue; }   // physics Stage 2 shadow: the destroy's Exit, recorded for the next step
            if (da) Send(b, a, 2); else Send(a, b, 2);
        }
    }
    static bool Dies(Shape s, NGameObject root, NBehaviour col)
    {
        if (col != null) return ReferenceEquals(s.C, col);
        for (var t = s.C.Go?.Transform; t != null; t = t.Parent) if (ReferenceEquals(t.Go, root)) return true;
        return false;
    }

    /// <summary>SHEPHERD_TRIGGERS2D_LOG=1: one line per Enter / Exit (not Stay): both colliders, their layers and bodies.</summary>
    [ProfileOnly]   // log text only: no property-bag keys (Assets/Diet.cs)
    static class Trace
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_TRIGGERS2D_LOG") == "1";
        static readonly string[] Kinds = { "ENTER", "stay", "EXIT", "EXIT (destroyed)" };
        static string D(Shape s) => $"'{s.C.Go?.Name}' {s.C.Managed?.GetType().Name} L{s.Layer}{(s.Trigger ? " trigger" : "")} body={(s.Body == null ? "-" : (s.Moving ? "" : "static ") + "'" + s.Body.Go?.Name + "'")}";
        public static void Event(int kind, Shape a, Shape b) => Log.Info($"[triggers2d] {Kinds[kind]} {D(a)} x {D(b)}");
    }

    // ---- messages ----
    static readonly string[] Msgs = { "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D" };
    sealed class Handler { public MethodInfo Mi; public bool OneArg; }
    static readonly Dictionary<Type, Handler>[] _handlers = { new(), new(), new() };
    static readonly object[] _arg = new object[1];
    /// <summary>One callback each way: A's GameObject hears about B's collider and B's about A's.</summary>
    static int Pair(Shape a, Shape b, int msg)
    {
        if (_rec != null) { _rec.Add((a.C, b.C, msg)); return 0; }   // physics Stage 2 shadow: recorded, not sent
        return Send(a, b, msg) + Send(b, a, msg);
    }
    static int Send(Shape to, Shape other, int msg)
    {
        if (to.C.Destroyed || other.C.Destroyed || to.C.Managed == null || other.C.Managed == null) return 0;
        int n = Send(to.C.Go, other.C.Managed, msg);
        if (to.Body != null && !ReferenceEquals(to.Body.Go, to.C.Go)) n += Send(to.Body.Go, other.C.Managed, msg);
        return n;
    }
    static int Send(NGameObject go, UnityEngine.Object other, int msg)
    {
        if (go == null || go.Destroyed) return 0;
        int n = 0; var cs = go.Components; var table = _handlers[msg];
        for (int i = 0; i < cs.Count; i++)
        {
            if (cs[i] is not NMono m || m.Destroyed || !m.Awoken || m.Managed == null) continue;
            var t = m.Managed.GetType();
            if (!table.TryGetValue(t, out var h)) table[t] = h = Find(t, Msgs[msg]);
            if (h == null) continue;
            try
            {
                object r;
                if (h.OneArg) { _arg[0] = other; r = h.Mi.Invoke(m.Managed, _arg); _arg[0] = null; } else r = h.Mi.Invoke(m.Managed, null);
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: 2D trigger messages)
#endif
                if (r is IEnumerator e) Coroutines.Start(m, e, h.Mi.Name);
                n++;
            }
            catch (TargetInvocationException e)
            {
                _arg[0] = null; Log.Exception(e.InnerException ?? e, $"{Msgs[msg]} {t.Name} '{go.Name}'");
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE
#endif
            }
        }
        return n;
    }
    /// <summary>The type's handler (its own or inherited, any visibility): the Collider2D overload first, else the parameterless one.</summary>
    static Handler Find(Type t, string msg)
    {
        MethodInfo mi = null;
        for (var c = t; c != null && c != typeof(MonoBehaviour) && mi == null; c = c.BaseType)
            mi = c.GetMethod(msg, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { typeof(Collider2D) }, null)
                 ?? c.GetMethod(msg, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        return mi == null ? null : new Handler { Mi = mi, OneArg = mi.GetParameters().Length == 1 };
    }

    // ---- shapes ----
    static readonly HashSet<string> _warned = new();
    static int KindOf(NBehaviour c)
    {
        var tn = c.Managed.GetType().Name;
        if (tn == "CircleCollider2D") return KCircle;
        if (tn == "BoxCollider2D") return KBox;
        if (tn == "CapsuleCollider2D") { if (_warned.Add(tn)) Log.Info("[triggers2d] CapsuleCollider2D is tested as its box"); return KCapsule; }
        if (_warned.Add(tn)) Log.Info($"[triggers2d] {tn} takes no part in trigger tests yet");
        return KNone;
    }
    /// <summary>The collider's state this step. False: it takes no part (its body is not simulated).</summary>
    static bool Build(Shape s)
    {
        var c = s.C;
        s.Trigger = c.Get("isTrigger") is { } it && Convert.ToBoolean(it);
        s.Layer = c.Go.Layer;
        if (!BodyValid(s)) FindBody(s);
        s.Moving = false;
        if (s.Body != null)
        {
            if (s.Body.Get("simulated") is { } sim && !Convert.ToBoolean(sim)) return false;    // D4: an unsimulated body's colliders are out
            s.Moving = !(s.Body.Get("bodyType") is { } bt && Convert.ToInt32(bt) == 2);           // D4: RigidbodyType2D.Static pairs like no body
        }
        var tr = c.Go.Transform; long stamp = tr.ChainStamp;
        var off = c.Get("offset") is Vector2 o ? o : Vector2.zero;
        Matrix4x4 cm = default;
        bool core = Parity && Physics.PhysWorld.CoreColliderMatrix(c, out cm);   // parity: a body's collider at the core's pose
        if (core) stamp = --_coreStamp;   // (a fresh negative stamp: never the cached one, rebuilt every step)
        if (s.Kind == KCircle)
        {
            float r = c.Get("radius") is float rr ? rr : 0.5f;
            if (stamp == s.GeoStamp && Bits.Same(off, s.GOff) && BitConverter.SingleToInt32Bits(r) == BitConverter.SingleToInt32Bits(s.GRad)) return true;
            var lw = core ? cm : tr.LocalToWorld;
            float sx = new Vector2(lw.m00, lw.m10).magnitude, sy = new Vector2(lw.m01, lw.m11).magnitude;
            s.Cx = lw.m00 * off.x + lw.m01 * off.y + lw.m03; s.Cy = lw.m10 * off.x + lw.m11 * off.y + lw.m13; s.R = r * Math.Max(sx, sy);
            s.MinX = s.Cx - s.R; s.MaxX = s.Cx + s.R; s.MinY = s.Cy - s.R; s.MaxY = s.Cy + s.R;
            s.GeoStamp = stamp; s.GOff = off; s.GRad = r;
            return true;
        }
        var size = c.Get("size") is Vector2 z ? z : Vector2.one;
        if (stamp == s.GeoStamp && Bits.Same(off, s.GOff) && Bits.Same(size, s.GSize)) return true;
        var m = core ? cm : tr.LocalToWorld;
        s.MinX = s.MinY = float.MaxValue; s.MaxX = s.MaxY = float.MinValue;
        for (int k = 0; k < 4; k++)
        {
            float x = off.x + ((k == 1 || k == 2) ? 0.5f : -0.5f) * size.x, y = off.y + (k >= 2 ? 0.5f : -0.5f) * size.y;
            float wx = m.m00 * x + m.m01 * y + m.m03, wy = m.m10 * x + m.m11 * y + m.m13;
            s.Px[k] = wx; s.Py[k] = wy;
            s.MinX = Math.Min(s.MinX, wx); s.MaxX = Math.Max(s.MaxX, wx); s.MinY = Math.Min(s.MinY, wy); s.MaxY = Math.Max(s.MaxY, wy);
        }
        s.GeoStamp = stamp; s.GOff = off; s.GSize = size;
        return true;
    }
    /// <summary>Is the cached body lookup still right: the same parent chain, and no Rigidbody2D bound or destroyed since?</summary>
    static bool BodyValid(Shape s)
    {
        if (s.BodyVer != Registry.Bodies2D.Version) return false;
        int i = 0;
        for (var t = s.C.Go.Transform; t != null; t = t.Parent, i++) if (i >= s.ChainLen || !ReferenceEquals(s.Chain[i], t)) return false;
        return i == s.ChainLen;
    }
    /// <summary>Collider2D.attachedRigidbody: the Rigidbody2D on the collider's GameObject or the nearest parent with one (simulated or
    /// not: Build decides); the parent chain is remembered for <see cref="BodyValid"/>.</summary>
    static void FindBody(Shape s)
    {
        s.Body = null; int i = 0;
        for (var t = s.C.Go.Transform; t != null; t = t.Parent, i++)
        {
            if (i == s.Chain.Length) Array.Resize(ref s.Chain, i * 2);
            s.Chain[i] = t;
            if (s.Body == null) foreach (var c in t.Go.Components) if (c is NComponent b && !b.Destroyed && b.Managed is Rigidbody2D) { s.Body = b; break; }
        }
        s.ChainLen = i; s.BodyVer = Registry.Bodies2D.Version;
    }
    static bool Overlap(Shape a, Shape b)
    {
        if (a.Kind == KCircle && b.Kind == KCircle) { float dx = a.Cx - b.Cx, dy = a.Cy - b.Cy, r = a.R + b.R; return dx * dx + dy * dy <= r * r; }
        if (a.Kind == KCircle) return CircleBox(a, b, Parity ? Skin : 0f);
        if (b.Kind == KCircle) return CircleBox(b, a, Parity ? Skin : 0f);
        if (Parity) return QuadGap(a, b) <= 2f * Skin;   // parity: two rounded boxes touch within both skins
        return !Separated(a, b) && !Separated(b, a);
    }
    /// <summary>Box2D's b2_polygonRadius (b2Settings.h: 2 x b2_linearSlop 0.005): the skin of every Box2D polygon (H-SLOP).</summary>
    const float Skin = 0.01f;
    /// <summary>parity: the gap between two convex quads (0 when they overlap): the smallest point-to-segment distance over both outlines.</summary>
    static float QuadGap(Shape a, Shape b)
    {
        if (!Separated(a, b) && !Separated(b, a)) return 0f;
        float best = float.MaxValue;
        for (int k = 0; k < 2; k++)
        {
            var (p, q) = k == 0 ? (a, b) : (b, a);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    int jn = (j + 1) & 3; float ex = q.Px[jn] - q.Px[j], ey = q.Py[jn] - q.Py[j];
                    float len2 = ex * ex + ey * ey; float t = len2 > 0f ? Math.Clamp(((p.Px[i] - q.Px[j]) * ex + (p.Py[i] - q.Py[j]) * ey) / len2, 0f, 1f) : 0f;
                    float dx = p.Px[i] - (q.Px[j] + t * ex), dy = p.Py[i] - (q.Py[j] + t * ey);
                    best = Math.Min(best, MathF.Sqrt(dx * dx + dy * dy));
                }
        }
        return best;
    }
    /// <summary>Is there an edge normal of p that separates p from q (SAT for two convex quads)?</summary>
    static bool Separated(Shape p, Shape q)
    {
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) & 3; float nx = p.Py[j] - p.Py[i], ny = p.Px[i] - p.Px[j];
            float pmin = float.MaxValue, pmax = float.MinValue, qmin = float.MaxValue, qmax = float.MinValue;
            for (int k = 0; k < 4; k++) { float d = nx * p.Px[k] + ny * p.Py[k]; pmin = Math.Min(pmin, d); pmax = Math.Max(pmax, d); float e = nx * q.Px[k] + ny * q.Py[k]; qmin = Math.Min(qmin, e); qmax = Math.Max(qmax, e); }
            if (pmax < qmin || qmax < pmin) return true;
        }
        return false;
    }
    /// <summary>Circle against a convex quad: the centre inside, or within r of an edge.</summary>
    static bool CircleBox(Shape c, Shape b, float skin = 0f)
    {
        bool inside = true; float best = float.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) & 3; float ex = b.Px[j] - b.Px[i], ey = b.Py[j] - b.Py[i];
            float cross = ex * (c.Cy - b.Py[i]) - ey * (c.Cx - b.Px[i]); if (cross < 0f) inside = false;
            float len2 = ex * ex + ey * ey; float t = len2 > 0f ? Math.Clamp(((c.Cx - b.Px[i]) * ex + (c.Cy - b.Py[i]) * ey) / len2, 0f, 1f) : 0f;
            float dx = c.Cx - (b.Px[i] + t * ex), dy = c.Cy - (b.Py[i] + t * ey); best = Math.Min(best, dx * dx + dy * dy);
        }
        if (!inside)
        {   // the quad may wind the other way (a mirrored transform): inside means all crosses share one sign
            inside = true;
            for (int i = 0; i < 4; i++) { int j = (i + 1) & 3; float cross = (b.Px[j] - b.Px[i]) * (c.Cy - b.Py[i]) - (b.Py[j] - b.Py[i]) * (c.Cx - b.Px[i]); if (cross > 0f) { inside = false; break; } }
        }
        return inside || best <= (c.R + skin) * (c.R + skin);
    }
}
