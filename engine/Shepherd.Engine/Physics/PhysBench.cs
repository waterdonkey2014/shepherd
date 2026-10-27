#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 section 4, "Benchmarks, split in two"; C3 / C19): the engine's managed <c>physbench</c> mode --
/// only what a pure C app cannot measure, the managed glue chain, through the real rewritten Unity modules of this run (CoreCLR, the Mono
/// JIT, the Mono interpreter of the V6 sim, or full AOT + interpreter). The native floors (step costs, CALL floors, native A*, fork-join)
/// are PhysBench's (vita/physics/physbench, SHPD00012) and are not repeated here. Cases:
/// <list type="number">
/// <item>one stub icall round trip, today's form: <c>Rigidbody2D.position</c> (get) and <c>MovePosition</c> on a real record, through the
/// rewritten stub, <c>object[]</c> + box, Icalls.Call and an L1-shaped bench implementation (resolve the record, read / write a mirror
/// slot through the 32-byte block's +12 handle) bound to the real icall ids for the measurement only;</item>
/// <item>the same through a hand-written typed stub + Hooks-shaped fast path (the Stage 7 shape: no object[], no box, no engine frame);</item>
/// <item>one managed -> native transition (<c>sp_nop</c> through the binding's function pointer);</item>
/// <item>one transform visit of MarkChanged (a 16-deep chain); the bytes allocated per stub call;</item>
/// <item>a FindObjectFromInstanceID round trip (the C14 hop behind every RaycastHit2D / Collision2D accessor);</item>
/// <item>one closed-delegate dispatch into a trivial handler, and one MethodInfo.Invoke dispatch (today's Triggers2D.Send);</item>
/// <item>base dispatch: 34 / 67 / 112 Stay deliveries per logic tick (shape level certain / shape level upper bound / AABB-level per L30
/// tick, plan 1.8) into a SwayInWind-shaped handler (its early-outs and CompareTag), by Invoke and by delegate;</item>
/// <item>one A* node query through the game's own GraphCollision.Check (use2D: Physics2D.OverlapCircle -> PhysicsScene2D ->
/// OverlapCircleArray icall), down to one native transition (the broadphase-direct query itself is Stage 2's; its native floor is
/// PhysBench's, 145 ns on the Mac);</item>
/// <item>one ClipperLib union of a composite's inputs (the game's Pathfinding.ClipperLib through cached reflection, the verified
/// pipeline OffsetPolygons(+500, jtMiter, 2) -> Union(NonZero) -> OffsetPolygons(-500) -> CleanPolygons(5000)), inputs from an owner-local
/// dump named by SHEPHERD_PHYSBENCH_COMPOSITE (skipped without it: nothing of the game's is in the repository).</item>
/// </list>
/// Output: one table in the log ("[physbench] ..."), raw samples to &lt;logs&gt;/physbench-raw.tsv. Median of 5 runs of N each, ns per
/// operation (MEASURED-M). Silent (no FMOD, SHEPHERD_AUDIO_OUT=null). No method signature names a Unity physics type (IcallRegistry's
/// start-up scan), and no lambda captures one.</summary>
[ProfileOnly]   // bench text only; reads no property-bag key (Assets/Diet.cs)
public static unsafe class PhysBench
{
    const int Reps = 5;
    static StreamWriter _raw;
    static readonly List<(string name, double med, double min, string note)> Rows = new();
    static float* _mirror;          // the SoA stand-in: pose x, y per slot at [2 * slot], MovePosition targets at [1024 + 2 * slot]
    static double _sink;

    static double Measure(string name, int n, Action<int> body, string note = null)
    {
        body(Math.Max(1, Math.Min(n / 10, 2000)));   // warm-up (JIT / interpreter transform / AOT trampolines)
        var s = new double[Reps];
        for (int r = 0; r < Reps; r++)
        {
            long t0 = Stopwatch.GetTimestamp(); body(n);
            s[r] = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / n;
        }
        _raw?.WriteLine($"{name}\t{n}\t{string.Join("\t", s.Select(x => x.ToString("F2")))}");
        var o = s.OrderBy(x => x).ToArray();
        Rows.Add((name, o[Reps / 2], o[0], note));
        Log.Info($"[physbench] {name,-78} {o[Reps / 2],10:F1} ns  (min {o[0]:F1}){(note != null ? "  " + note : "")}");
        return o[Reps / 2];
    }

    public static int Run(string[] args)
    {
        int scale = args.Length > 1 && int.TryParse(args[1], out var sc) && sc > 0 ? sc : 1;
        bool mono = Type.GetType("Mono.RuntimeStructs") != null;
        string mode = !mono ? "CoreCLR" : (Environment.GetEnvironmentVariable("MONO_ENV_OPTIONS") ?? "") switch
        { var m when m.Contains("full-aot-interp") => "Mono full AOT + interpreter", var m when m.Contains("interpreter") => "Mono interpreter", _ => "Mono JIT" };
        Log.Info($"[physbench] runtime {RuntimeInformation.FrameworkDescription} ({mode}), {RuntimeInformation.ProcessArchitecture}, profile '{Profile.Name}', scale {scale}; MEASURED-M, ns per operation, median of {Reps}");
        Directory.CreateDirectory(Program.LogDir);
        _raw = new StreamWriter(Path.Combine(Program.LogDir, "physbench-raw.tsv")) { AutoFlush = true };
        _raw.WriteLine($"# physbench raw: {RuntimeInformation.FrameworkDescription} ({mode}); case, N, {Reps} runs in ns/op");
        _mirror = (float*)NativeMemory.AllocZeroed(4096 * sizeof(float));
        var core = ShepPhys.Core;
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        int fails = 0;
        try { Cases(core, scale); }
        catch (Exception e) { Log.Exception(e, "physbench"); fails++; }
        _raw.Dispose(); _raw = null;
        Log.Info($"[physbench] DONE {Rows.Count} cases ({mode}); sink {_sink:G3}");
        return fails;
    }

    // ---------------- the cases ----------------
    static object _rb, _col, _go, _hostGo;   // Rigidbody2D / Collider2D / GameObject as plain object (no physics type in any field or signature)
    static int IdOf(string key) { var n = IcallRegistry.Names; for (int i = 0; i < n.Length; i++) if (n[i] == key || n[i].StartsWith(key + "(", StringComparison.Ordinal)) return i; return -1; }

    static void Cases(IPhysCore core, int scale)
    {
        int N = 200_000 * scale;
        MakeObjects();
        // 1-2: stub icall round trip, today's form vs the typed stub + fast path
        int idGet = IdOf("UnityEngine.Rigidbody2D::get_position_Injected"), idMove = IdOf("UnityEngine.Rigidbody2D::MovePosition_Injected");
        var tbl = Shepherd.Hooks.Icalls.Table;
        var keepGet = idGet >= 0 ? tbl[idGet] : null; var keepMove = idMove >= 0 ? tbl[idMove] : null;
        if (idGet >= 0) tbl[idGet] = BenchGetPosition;
        if (idMove >= 0) tbl[idMove] = BenchMovePosition;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        Measure("rb.position get: today's stub (object[] + box + Icalls.Call + L1)", N, GetPositionToday);
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        Measure("rb.MovePosition: today's stub", N, MovePositionToday);
        long a2 = GC.GetAllocatedBytesForCurrentThread();
        Measure("rb.position get: typed stub + Hooks fast path (Stage 7 shape)", N, GetPositionTyped);
        Measure("rb.MovePosition: typed stub + Hooks fast path", N, MovePositionTyped);
        long a3 = GC.GetAllocatedBytesForCurrentThread();
        int calls = (Math.Max(1, Math.Min(N / 10, 2000)) + Reps * N);
        Log.Info($"[physbench] allocation per call: rb.position (today) {(a1 - a0) / (double)calls:F1} B, MovePosition (today) {(a2 - a1) / (double)calls:F1} B, " +
                 $"the two typed fast paths {(a3 - a2) / (double)(2 * calls):F1} B{(a1 == a0 ? " (this runtime reports no per-thread allocation counter)" : "")}");
        _raw.WriteLine($"alloc_bytes_per_call\trb.position.today\t{(a1 - a0) / (double)calls:F2}\tMovePosition.today\t{(a2 - a1) / (double)calls:F2}\ttyped\t{(a3 - a2) / (double)(2 * calls):F2}");
        if (idGet >= 0) tbl[idGet] = keepGet; if (idMove >= 0) tbl[idMove] = keepMove;
        // 3: managed -> native
        if (core != null) { _core = core; Measure("managed -> native transition: sp_nop through the binding's function pointer", N, NopLoop); }
        else Log.Info("[physbench] sp_nop: skipped (libshepphys did not bind: " + ShepPhys.Status + ")");
        // 4: MarkChanged over a 16-deep chain (per transform visit)
        var chain = MakeChain(16);
        Measure("one MarkChanged transform visit (16-deep chain, no physics interest)", N / 16, k => { var t = chain; for (int i = 0; i < k; i++) t.MarkChanged(); }, "per call of 16 visits / 16 below");
        Rows[^1] = (Rows[^1].name, Rows[^1].med / 16, Rows[^1].min / 16, "per visit");
        Log.Info($"[physbench]   -> per transform visit {Rows[^1].med:F1} ns");
        // 5: FindObjectFromInstanceID
        var find = (Func<int, UObject>)Delegate.CreateDelegate(typeof(Func<int, UObject>), typeof(UObject).GetMethod("FindObjectFromInstanceID", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        int iid = ((UObject)_col).GetInstanceID();
        Measure("Object.FindObjectFromInstanceID round trip (stub + icall + registry)", N, k => { for (int i = 0; i < k; i++) if (find(iid) is null) _sink++; });
        // 6: dispatch
        var h = new StayHandler { Other = (Component)_col }; var mi = typeof(StayHandler).GetMethod("OnTriggerStay2D", BindingFlags.Instance | BindingFlags.NonPublic);
        var del = (Action<Component>)Delegate.CreateDelegate(typeof(Action<Component>), h, mi);
        var args1 = new object[] { _col };
        Measure("dispatch: closed delegate into a trivial Stay handler", N, k => { var c = (Component)_col; for (int i = 0; i < k; i++) del(c); });
        Measure("dispatch: MethodInfo.Invoke (today's Triggers2D.Send)", N / 4, k => { for (int i = 0; i < k; i++) mi.Invoke(h, args1); });
        // 7: base dispatch per logic tick (plan 1.8: 34 certain shape-level pairs, 67 the shape-level upper bound, 112 AABB-level per L30 tick)
        var sway = new SwayShaped { CollideWithTag = "Player" }; var smi = typeof(SwayShaped).GetMethod("OnTriggerStay2D", BindingFlags.Instance | BindingFlags.NonPublic);
        var sdel = (Action<Component>)Delegate.CreateDelegate(typeof(Action<Component>), sway, smi);
        foreach (int pairs in new[] { 34, 67, 112 })
        {
            int p = pairs;
            Measure($"base dispatch: {p} Stay deliveries (SwayInWind-shaped handler) by Invoke, per tick", Math.Max(1, N / 200), k => { for (int i = 0; i < k; i++) for (int j = 0; j < p; j++) smi.Invoke(sway, args1); });
            Measure($"base dispatch: {p} Stay deliveries by closed delegate, per tick", Math.Max(1, N / 50), k => { var c = (Component)_col; for (int i = 0; i < k; i++) for (int j = 0; j < p; j++) sdel(c); });
        }
        // 8: an A* node query through the game's GraphCollision (2D) down to one native transition
        AStarCase(core, N / 4);
        // 9: one ClipperLib union of a composite's inputs
        ClipperCase();
        UObject.DestroyImmediate((UObject)_hostGo);
    }

    static IPhysCore _core;
    static void NopLoop(int k) { var c = _core; int x = 0; for (int i = 0; i < k; i++) x += c.Nop(); _sink += x; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeObjects()
    {
        var go = new GameObject("physbench"); _hostGo = go; _go = go;
        var rb = go.AddComponent<Rigidbody2D>(); _rb = rb;
        _col = go.AddComponent<CircleCollider2D>();
        var rec = Objects.Get(rb); *(int*)(rec.Ptr + 12) = 7;   // the mirror slot (plan 1.3: +12 of the 32-byte block holds the handle)
        _mirror[14] = 1.5f; _mirror[15] = 2.5f;
    }
    static NTransform MakeChain(int n)
    {
        var root = new GameObject("chain0"); var t = root.transform;
        for (int i = 1; i < n; i++) { var g = new GameObject("chain" + i); g.transform.SetParent(t, false); t = g.transform; }
        root.transform.SetParent(((GameObject)_hostGo).transform, false);
        return Objects.Get<NTransform>(root.transform);
    }

    // ---- 1: today's chain (the rewritten stub does object[] + box; Icalls.Call; this L1 resolves the record and reads the mirror) ----
    static object BenchGetPosition(object[] a)
    {
        var n = Objects.Get(a[0]); if (n == null) return null;
        int slot = *(int*)(n.Ptr + 12);
        a[1] = Um.V2(_mirror[2 * slot], _mirror[2 * slot + 1]);
        return null;
    }
    static object BenchMovePosition(object[] a)
    {
        var n = Objects.Get(a[0]); if (n == null) return null;
        int slot = *(int*)(n.Ptr + 12); var p = (Vector2)a[1];
        _mirror[1024 + 2 * slot] = p.x; _mirror[1024 + 2 * slot + 1] = p.y;
        return null;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void GetPositionToday(int k) { var rb = (Rigidbody2D)_rb; float acc = 0; for (int i = 0; i < k; i++) acc += rb.position.x; _sink += acc; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void MovePositionToday(int k) { var rb = (Rigidbody2D)_rb; var p = Um.V2(1f, 2f); for (int i = 0; i < k; i++) rb.MovePosition(p); }
    // ---- 2: the Stage 7 shape: a typed stub calls the Hooks-side reader directly (no object[], no box, no Icalls.Call, no record lookup) ----
    [MethodImpl(MethodImplOptions.NoInlining)] static void GetPositionTyped(int k) { var rb = (UObject)_rb; float acc = 0; for (int i = 0; i < k; i++) acc += TypedGetPosition(rb).x; _sink += acc; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void MovePositionTyped(int k) { var rb = (UObject)_rb; var p = Um.V2(1f, 2f); for (int i = 0; i < k; i++) TypedMovePosition(rb, p); }
    static Vector2 TypedGetPosition(UObject rb) { HooksGetPosition(Objects.PtrOf(rb), out var x, out var y); return Um.V2(x, y); }
    static void TypedMovePosition(UObject rb, Vector2 p) => HooksMovePosition(Objects.PtrOf(rb), p.x, p.y);
    static void HooksGetPosition(IntPtr cached, out float x, out float y) { int slot = *(int*)(cached + 12); x = _mirror[2 * slot]; y = _mirror[2 * slot + 1]; }
    static void HooksMovePosition(IntPtr cached, float x, float y) { int slot = *(int*)(cached + 12); _mirror[1024 + 2 * slot] = x; _mirror[1024 + 2 * slot + 1] = y; }

    // ---- 6 / 7: handlers ----
    sealed class StayHandler { public Component Other; public int N; void OnTriggerStay2D(Component other) { if (ReferenceEquals(other, Other)) N++; } }
    /// <summary>The shape of SwayInWind.OnTriggerStay2D -> Colliding (plan 1.8: records only if !swayOnly && !PerformanceMode && (collideWithTag
    /// empty or other.CompareTag(collideWithTag))), with its CompareTag icall -- written here, not the game's type.</summary>
    sealed class SwayShaped
    {
        public bool SwayOnly, PerformanceMode; public string CollideWithTag; public int Recorded;
        void OnTriggerStay2D(Component other)
        {
            if (!SwayOnly && !PerformanceMode && (string.IsNullOrEmpty(CollideWithTag) || other.CompareTag(CollideWithTag))) Recorded++;
        }
    }

    // ---- 8: A* node query (the game's GraphCollision.Check, use2D) ----
    static object BenchOverlapCircleArray(object[] a) { int r = _core?.Nop() ?? 0; return r; }
    static object BenchCheckConsistency(object[] a) => null;
    static void AStarCase(IPhysCore core, int n)
    {
        Type gc = null;
        try { gc = Assembly.Load("AstarPathfindingProject").GetType("Pathfinding.GraphCollision"); } catch (Exception e) { Log.Info("[physbench] A* case skipped: " + e.GetBaseException().Message); return; }
        var gt = gc?.Assembly.GetType("Pathfinding.Util.GraphTransform");
        if (gc == null || gt == null) { Log.Info("[physbench] A* case skipped: GraphCollision / GraphTransform not found"); return; }
        var coll = Activator.CreateInstance(gc);
        gc.GetField("use2D").SetValue(coll, true); gc.GetField("collisionCheck").SetValue(coll, true); gc.GetField("diameter").SetValue(coll, 1f);
        gc.GetField("type").SetValue(coll, Enum.ToObject(gc.GetField("type").FieldType, 1));   // ColliderType.Sphere: the OverlapCircle path
        var transform = Activator.CreateInstance(gt, Matrix4x4.identity);
        gc.GetMethod("Initialize").Invoke(coll, new[] { transform, (object)1f });
        var check = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), coll, gc.GetMethod("Check", new[] { typeof(Vector3) }));
        int idOv = IdOf("UnityEngine.PhysicsScene2D::OverlapCircleArray_Internal_Injected"), idCc = IdOf("UnityEngine.ContactFilter2D::CheckConsistency_Injected");
        var tbl = Shepherd.Hooks.Icalls.Table;
        var keepOv = idOv >= 0 ? tbl[idOv] : null; var keepCc = idCc >= 0 ? tbl[idCc] : null;
        if (idOv >= 0) tbl[idOv] = BenchOverlapCircleArray;
        if (idCc >= 0 && keepCc == null) tbl[idCc] = BenchCheckConsistency;
        long hOv0 = idOv >= 0 ? Shepherd.Hooks.Icalls.HitCount(idOv) : 0, hCc0 = idCc >= 0 ? Shepherd.Hooks.Icalls.HitCount(idCc) : 0;   // (kit #8 merge: HitCount = kit #6 fps's 32-bit counters + Hits)
        _core = core;
        var pos = Um.V3(3f, 4f, 0f);
        Measure("A* node: GraphCollision.Check (use2D) -> Physics2D.OverlapCircle -> OverlapCircleArray icall -> sp_nop", n,
                k => { for (int i = 0; i < k; i++) if (!check(pos)) _sink++; }, core == null ? "no native transition (libshepphys did not bind)" : null);
        long calls = Math.Max(1, Math.Min(n / 10, 2000)) + Reps * (long)n;
        Log.Info($"[physbench]   -> per node: OverlapCircleArray icall {(idOv >= 0 ? (Shepherd.Hooks.Icalls.HitCount(idOv) - hOv0) / (double)calls : -1):F2}x, CheckConsistency icall {(idCc >= 0 ? (Shepherd.Hooks.Icalls.HitCount(idCc) - hCc0) / (double)calls : -1):F2}x");
        if (idOv >= 0) tbl[idOv] = keepOv; if (idCc >= 0) tbl[idCc] = keepCc;
    }

    // ---- 9: ClipperLib union of a composite's inputs (owner-local dump; the game's Pathfinding.ClipperLib) ----
    static void ClipperCase()
    {
        var dump = Environment.GetEnvironmentVariable("SHEPHERD_PHYSBENCH_COMPOSITE");
        if (string.IsNullOrEmpty(dump) || !File.Exists(dump)) { Log.Info("[physbench] ClipperLib case skipped: SHEPHERD_PHYSBENCH_COMPOSITE (an owner-local m_ColliderPaths dump) not set"); return; }
        Assembly asm;
        try { asm = Assembly.Load("Pathfinding.ClipperLib"); } catch (Exception e) { Log.Info("[physbench] ClipperLib case skipped: " + e.GetBaseException().Message); return; }
        var tPt = asm.GetType("Pathfinding.ClipperLib.IntPoint"); var tClip = asm.GetType("Pathfinding.ClipperLib.Clipper");
        var tJoin = asm.GetType("Pathfinding.ClipperLib.JoinType"); var tPoly = asm.GetType("Pathfinding.ClipperLib.PolyType");
        var tCt = asm.GetType("Pathfinding.ClipperLib.ClipType"); var tFill = asm.GetType("Pathfinding.ClipperLib.PolyFillType");
        var tPath = typeof(List<>).MakeGenericType(tPt); var tPaths = typeof(List<>).MakeGenericType(tPath);
        // the inputs: every "[n] [count]" path of IntPoint { X=.., Y=.. } lines under m_ColliderPaths (up to m_CompositePaths)
        var paths = (System.Collections.IList)Activator.CreateInstance(tPaths); System.Collections.IList cur = null; int pts = 0;
        bool inInputs = false;
        foreach (var line in File.ReadLines(dump))
        {
            var s = line.Trim();
            if (s.StartsWith("m_ColliderPaths:")) inInputs = true;
            else if (s.StartsWith("m_CompositePaths:")) break;
            if (!inInputs) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\[\d+\] \[\d+\]$")) { cur = (System.Collections.IList)Activator.CreateInstance(tPath); paths.Add(cur); continue; }
            var m = System.Text.RegularExpressions.Regex.Match(s, @"IntPoint \{ X=(-?\d+), Y=(-?\d+) \}");
            if (m.Success && cur != null) { cur.Add(Activator.CreateInstance(tPt, long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value))); pts++; }
        }
        if (paths.Count == 0) { Log.Info("[physbench] ClipperLib case skipped: no IntPoint paths in " + dump); return; }
        var offset = tClip.GetMethod("OffsetPolygons", new[] { tPaths, typeof(double), tJoin, typeof(double) });
        var clean = tClip.GetMethod("CleanPolygons", new[] { tPaths, typeof(double) });
        var addPaths = tClip.GetMethod("AddPaths", new[] { tPaths, tPoly, typeof(bool) });
        var exec = tClip.GetMethod("Execute", new[] { tCt, tPaths, tFill, tFill });
        if (offset == null || clean == null || addPaths == null || exec == null) { Log.Info("[physbench] ClipperLib case skipped: an API member is missing"); return; }
        var ctor = tClip.GetConstructor(Type.EmptyTypes) ?? tClip.GetConstructor(new[] { typeof(int) });   // Clipper(int initOptions = 0)
        var ctorArgs = ctor?.GetParameters().Length == 1 ? new object[] { 0 } : Array.Empty<object>();
        if (ctor == null) { Log.Info("[physbench] ClipperLib case skipped: no Clipper constructor"); return; }
        object miter = Enum.Parse(tJoin, "jtMiter"), subject = Enum.Parse(tPoly, "ptSubject"), union = Enum.Parse(tCt, "ctUnion"), nonZero = Enum.Parse(tFill, "pftNonZero");
        int outPaths = 0, outPts = 0;
        Measure($"ClipperLib composite pipeline ({paths.Count} input paths, {pts} points): offset +500, union, offset -500, clean 5000", 200, k =>
        {
            for (int i = 0; i < k; i++)
            {
                var grown = offset.Invoke(null, new[] { paths, (object)500.0, miter, (object)2.0 });
                var c = ctor.Invoke(ctorArgs); addPaths.Invoke(c, new[] { grown, subject, (object)true });
                var sol = (System.Collections.IList)Activator.CreateInstance(tPaths); exec.Invoke(c, new[] { union, sol, nonZero, nonZero });
                var shrunk = offset.Invoke(null, new[] { (object)sol, -500.0, miter, 2.0 });
                var done = (System.Collections.IList)clean.Invoke(null, new[] { shrunk, (object)5000.0 });
                outPaths = done.Count; outPts = 0; foreach (System.Collections.IList p in done) outPts += p.Count;
            }
        }, "per pipeline run");
        Log.Info($"[physbench]   -> output {outPaths} paths, {outPts} points");
    }
}
#endif
