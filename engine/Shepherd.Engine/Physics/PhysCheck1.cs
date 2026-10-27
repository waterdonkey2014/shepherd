#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 1's physcheck scenes (physics-plan-v2 section 4 Stage 1 acceptance #1 and #2; section 1.6 acceptance), at SHEPHERD_PHYS=1:
/// <c>physcheck unit1 | render60 | composite</c> (and in <c>all</c>). Each scene is built through the Unity API (new GameObject, AddComponent
/// Rigidbody2D / colliders, rb.MovePosition / rb.position from a FixedUpdate exactly as UnitObject.FixedUpdate calls them) and run by the
/// engine's own frame loop (Lifecycle.Frame), so the whole Stage 1 chain is exercised: hooks -> flush -> native step -> write-back ->
/// interpolation. Properties whose setters Stage 1 does not serve yet (gravityScale, constraints, interpolation, sleep mode, materials,
/// polygon / edge points) are set on the typed records, as scene data would. Scenes sit 200 units apart. Layers are the project's own
/// (Player for the Lamb-shaped bodies, Island for walls), so the layer matrix is exercised too.</summary>
public static partial class PhysCheck
{
    static bool _loaderUp;
    static void Up() { if (!_loaderUp) { Assets.Loader.Init(); _loaderUp = true; } }
    static void Frames(int n) { for (int i = 0; i < n; i++) Lifecycle.Frame(); }
    static NRigidbody2D Rb(GameObject g) => Objects.Get(g.GetComponent<Rigidbody2D>()) as NRigidbody2D;
    static NCollider2D Col(Component c) => Objects.Get(c) as NCollider2D;
    static int Layer(string name, int fallback) { int l = LayerMask.NameToLayer(name); return l >= 0 ? l : fallback; }

    /// <summary>A Lamb-shaped unit: Dynamic, mass 1, gravity scale 0, rotation frozen, Interpolate, never sleeps, a 0.3 circle, layer Player
    /// (Player - Prisoner's own values, physics1/out/intro.txt) with the given material friction (null = no material: Unity's 0.4).</summary>
    static GameObject Lamb(string name, Vector2 at, float? friction = null, int interp = 1)
    {
        var g = new GameObject(name); g.layer = Layer("Player", 14); g.transform.position = new Vector3(at.x, at.y, 0f);
        g.AddComponent<Rigidbody2D>();
        var c = g.AddComponent<CircleCollider2D>(); c.radius = 0.3f;
        var rb = Rb(g); rb.GravityScale = 0f; rb.Constraints = 4; rb.Interpolate = interp; rb.SleepingMode = 0; PhysWorld.Changed(rb);
        if (friction is { } f) { Col(c).Material = new NPhysicsMaterial2D { Friction = f }; PhysWorld.Changed(Col(c)); }
        return g;
    }
    static GameObject Edge(string name, Vector2 at, float[] pts, float friction = 0.4f)
    {
        var g = new GameObject(name); g.layer = Layer("Island", 23); g.transform.position = new Vector3(at.x, at.y, 0f);
        var e = Col(g.AddComponent<EdgeCollider2D>());
        e.Paths = new[] { pts };
        if (friction != 0.4f) e.Material = new NPhysicsMaterial2D { Friction = friction };
        PhysWorld.Changed(e);
        return g;
    }

    static void Unit1()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        float fdt = Runtime.Time.FixedDeltaTime;
        // ---- 1: free-space MovePosition reaches p + h * ((t - p) / h), within rounding of the target ----
        var free = Lamb("P1-free", new Vector2(1000f, 0f));
        var mv = free.AddComponent<PhysMover>(); mv.V = new Vector2(2f, 1f);
        Frames(60);
        Check("1 free-space MovePosition: every step arrives within rounding of its target (|p - t| <= 2e-6)", mv.Steps > 40 && mv.WorstArrival <= 2e-6f,
              $"{mv.Steps} steps, worst |p - t| {mv.WorstArrival:E2}");
        UObject.DestroyImmediate(free);

        // ---- 2: the 45 degree push into a static chain edge: slide / free speed ratio (Lamb 0.4243, frictionless 0.7071, enemy 0.6624) ----
        float Ratio(string tag, float x0, float? lambFric, float edgeFric)
        {
            var e = Edge("P2-edge-" + tag, new Vector2(x0, 0f), new[] { -60f, 0f, 60f, 0f }, edgeFric);
            var l = Lamb("P2-lamb-" + tag, new Vector2(x0 - 20f, 0.31f), lambFric);
            var m = l.AddComponent<PhysMover>(); m.V = new Vector2(5f * 0.70710678f, -5f * 0.70710678f); m.MeasureFrom = 60; m.MeasureTo = 160;
            Frames(160 * 6 / 5 + 10);
            float r = (m.MeasuredDx / (m.MeasuredSteps * fdt)) / 5f;
            Log.Info($"[physcheck]      slide {tag}: {m.MeasuredSteps} steps, dx {m.MeasuredDx:R}, ratio {r:R}, y {l.transform.position.y:R}");
            UObject.DestroyImmediate(l); UObject.DestroyImmediate(e);
            return r;
        }
        float c45 = 0.70710678f, mu = MathF.Sqrt(0.01f * 0.4f);
        float rl = Ratio("lamb", 1200f, null, 0.4f), r0 = Ratio("frictionless", 1400f, 0f, 0f), re = Ratio("enemy", 1600f, 0.01f, 0.4f);
        Check("2 45-degree push into a static chain edge: slide/free = cos45 - 0.4 sin45 = 0.4243 +- 1 % (Lamb, no material: Unity's 0.4)", MathF.Abs(rl - (c45 - 0.4f * c45)) <= 0.01f * 0.4243f, $"{rl:R}");
        Check("2 ... frictionless control 0.7071 +- 1 %", MathF.Abs(r0 - c45) <= 0.01f * c45, $"{r0:R}");
        Check("2 ... enemy material sqrt(0.01 x 0.4) = 0.0632 -> 0.6624 +- 1 %", MathF.Abs(re - (c45 - mu * c45)) <= 0.01f * 0.6624f, $"{re:R}");

        // ---- 3: a circle along a 50-vertex collinear chain: no speed drop at vertices (<= 1e-4: ghost vertices) ----
        var pts = new float[100]; float px = -12.5f; for (int i = 0; i < 50; i++) { pts[2 * i] = px; pts[2 * i + 1] = 0f; px += 0.5f; }
        var ge = Edge("P3-chain", new Vector2(1800f, 0f), pts, 0f);
        var gl = Lamb("P3-lamb", new Vector2(1789f, 0.31f), 0f);
        var gm = gl.AddComponent<PhysMover>(); gm.V = new Vector2(3f, -3f); gm.MeasureFrom = 20; gm.MeasureTo = 400;
        Frames(110);
        float spread = (gm.MaxDx - gm.MinDx) / fdt;
        Check("3 a circle sliding along a 50-vertex collinear chain: no speed drop at the vertices (spread <= 1e-4 u/s)", gm.MeasuredSteps > 50 && spread <= 1e-4f,
              $"{gm.MeasuredSteps} steps over x {gm.FirstX:F2}..{gl.transform.position.x:F2}, per-step dx {gm.MinDx:R}..{gm.MaxDx:R}, spread {spread:E2} u/s");
        UObject.DestroyImmediate(gl); UObject.DestroyImmediate(ge);

        // ---- 4: a circle against the Blocking Door box (4 x 0.5754, no body) stops at radius + skin, within the linear slop ----
        var door = new GameObject("P4-door"); door.layer = Layer("Island", 23); door.transform.position = new Vector3(2000f, 0f, 0f);
        var bx = door.AddComponent<BoxCollider2D>(); bx.size = new Vector2(4f, 0.5754f);
        var dl = Lamb("P4-lamb", new Vector2(2000.3f, 2f));
        var dm = dl.AddComponent<PhysMover>(); dm.V = new Vector2(0f, -3f);
        Frames(150);
        float gap = dl.GetComponent<Rigidbody2D>().position.y - 0.5f * 0.5754f;
        Check("4 a 0.3 circle pushed into a 4 x 0.5754 box stops at radius + b2_polygonRadius 0.01, penetrating at most the linear slop 0.005", gap >= 0.305f - 1e-4f && gap <= 0.31f + 1e-4f,
              $"centre - box top = {gap:R}");
        UObject.DestroyImmediate(dl); UObject.DestroyImmediate(door);

        // ---- 5: 0-step ticks still interpolate; a teleport resets the history ----
        var il = Lamb("P5-lamb", new Vector2(2200f, 0f));
        var im = il.AddComponent<PhysMover>(); im.V = new Vector2(3f, 0f);
        Frames(20);
        int zeroStepFrames = 0, movedOnZero = 0; long sb = PhysWorld.SlotB;
        for (int i = 0; i < 30; i++)
        {
            float x0 = il.transform.position.x; long b0 = PhysWorld.SlotB;
            Frames(1);
            if (PhysWorld.SlotB == b0) { zeroStepFrames++; if (il.transform.position.x > x0) movedOnZero++; }
        }
        Check("5 at 60 fps the 50 Hz steps leave 0-step ticks, and the Interpolate body's transform still moves on every one of them", zeroStepFrames >= 4 && movedOnZero == zeroStepFrames,
              $"{zeroStepFrames} 0-step frames of 30 ({PhysWorld.SlotB - sb} steps), moved on {movedOnZero}");
        im.V = Vector2.zero;
        Frames(3);
        il.GetComponent<Rigidbody2D>().position = new Vector2(2250f, 5f);   // rb.position = p: a body-side teleport
        Frames(2);
        var ip = il.transform.position;
        Check("5 a teleport resets the interpolation history: the transform sits at the teleported pose, no smear from the old one", ip.x == 2250f && ip.y == 5f, $"({ip.x:R}, {ip.y:R})");
        UObject.DestroyImmediate(il);

        // ---- 6 (C6): rb.position = p on a resting body: the Transform shows p after the next step; then it keeps it ----
        var rs = new GameObject("P6-rest"); rs.layer = Layer("Default", 0); rs.transform.position = new Vector3(2400f, 0f, 0f);
        rs.AddComponent<Rigidbody2D>(); rs.AddComponent<CircleCollider2D>();
        var rr = Rb(rs); rr.GravityScale = 0f; PhysWorld.Changed(rr);
        Frames(90);   // (StartAwake, allowed to sleep: asleep by now)
        var rbody = rs.GetComponent<Rigidbody2D>();
        long s0 = PhysWorld.SlotB;
        rbody.position = new Vector2(2410f, 3f);
        var before = rs.transform.position;
        while (PhysWorld.SlotB == s0) Frames(1);
        var after = rs.transform.position;
        Frames(60);
        var later = rs.transform.position;
        Check("6 (C6) rb.position = p on a resting body: the Transform is unchanged until the next step, shows p after it, and keeps it", before.x == 2400f && after.x == 2410f && after.y == 3f &&
              later.x == 2410f && later.y == 3f, $"before ({before.x}, {before.y}), after the step ({after.x}, {after.y}), 60 frames later ({later.x}, {later.y})");
        UObject.DestroyImmediate(rs);

        // ---- 8 (C15): runtime AddComponent<BoxCollider2D>: size (1, 1), offset (0, 0) through the served getters (OverlapPoint: Stage 2) ----
        var hb = new GameObject("P8-helper"); var b8 = hb.AddComponent<BoxCollider2D>();
        Check("8 (C15) runtime AddComponent<BoxCollider2D>: size (1, 1), offset (0, 0) through BoxCollider2D.size / Collider2D.offset (OverlapPoint is Stage 2's)",
              b8.size == Vector2.one && b8.offset == Vector2.zero, $"size {b8.size}, offset {b8.offset}");
        UObject.DestroyImmediate(hb);
        Frames(2);
    }

    /// <summary>Plan 1.6 acceptance (C2): logic at 30 ticks (CAPTURE_DT 1/30), render poses at 60 Hz times. The Interpolate body equals rule 3
    /// bit for bit (recomputed from the step history), moves every display frame; the None body moves on 5 of 6 display frames; the
    /// sprites' (child) final world matrices equal those poses.</summary>
    static void Render60()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 30f;
        var li = Lamb("R-interp", new Vector2(3000f, 0f), null, 1); var ln = Lamb("R-none", new Vector2(3000f, 10f), null, 0);
        var si = new GameObject("R-interp-sprite"); si.transform.SetParent(li.transform, false); si.transform.localPosition = new Vector3(0.1f, 0.2f, -0.5f);
        var sn = new GameObject("R-none-sprite"); sn.transform.SetParent(ln.transform, false); sn.transform.localPosition = new Vector3(0.1f, 0.2f, -0.5f);
        var mi = li.AddComponent<PhysMover>(); mi.V = new Vector2(1.7f, 0f); mi.LogHistory = true;
        var mn = ln.AddComponent<PhysMover>(); mn.V = new Vector2(1.7f, 0f); mn.LogHistory = true;
        Frames(10);
        int frames = 0, iMoves = 0, nMoves = 0, exact = 0, spriteOk = 0, checkedPoses = 0;
        float lastIx = float.NaN, lastNx = float.NaN;
        double fdt = Runtime.Time.FixedDeltaTime;
        for (int tick = 0; tick < 30; tick++)
        {
            Frames(1);
            mi.AppendLatest(); mn.AppendLatest();
            double tau = Runtime.Time.Now;   // this tick's time
            foreach (double tr in new[] { tau - 1.0 / 60.0, tau })   // the in-between display frame, then the tick's own
            {
                int n = PhysWorld.ApplyRenderPoses(tr);
                if (n == 0) continue;
                float ix = li.transform.position.x, nx = ln.transform.position.x;
                // rule 3 recomputed from the step log: j = the last step with T_j <= tr, alpha = (tr - T_j) / fdt
                var (want, _) = mi.Rule3(tr, fdt, true); var (wantN, _) = mn.Rule3(tr, fdt, false);
                checkedPoses += 2;
                if (BitConverter.SingleToInt32Bits(ix) == BitConverter.SingleToInt32Bits(want)) exact++;
                if (BitConverter.SingleToInt32Bits(nx) == BitConverter.SingleToInt32Bits(wantN)) exact++;
                // the sprites' final world matrices: the child's local offset composed under the body's render pose, nothing blended
                var wi = si.transform.position; var wn = sn.transform.position;
                if (wi.x == li.transform.position.x + 0.1f && wn.x == ln.transform.position.x + 0.1f) spriteOk++;
                frames++;
                if (!float.IsNaN(lastIx) && ix != lastIx) iMoves++;
                if (!float.IsNaN(lastNx) && nx != lastNx) nMoves++;
                lastIx = ix; lastNx = nx;
                PhysWorld.RestoreTickPoses();
            }
        }
        int disp = frames - 1;
        Check("render60: the Interpolate body's render pose equals Unity's formula at the render time (rule 3) bit for bit, the None body its stepped pose",
              checkedPoses > 0 && exact == checkedPoses, $"{exact} of {checkedPoses} poses exact");
        Check("render60: the Interpolate body moves on every display frame", iMoves == disp, $"{iMoves} of {disp}");
        Check("render60: the None body moves on 5 of 6 display frames (the 50 Hz beat at 60)", Math.Abs(nMoves * 6 - disp * 5) <= 6, $"{nMoves} of {disp}");
        Check("render60: the sprites' world matrices are the render poses composed with their local offsets (no generic blending)", spriteOk == frames, $"{spriteOk} of {frames}");
        Check("render60: no render time outside the served window", PhysWorld.RenderClamped == 0, $"{PhysWorld.RenderClamped} clamped");
        UObject.DestroyImmediate(li); UObject.DestroyImmediate(ln);
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        Frames(2);
    }

    /// <summary>Stage 1 acceptance #2: the 4 non-stale composites of the plan's oracle (12 paths): Unity's own serialized inputs (m_ColliderPaths)
    /// through this engine's pipeline (Physics/Composite.cs, the game's ClipperLib) against Unity's own output (m_CompositePaths): path counts
    /// and point counts equal, direction equal, Hausdorff <= 1e-4. The cases are read from the owner-local oracle report
    /// (SHEPHERD_PHYSCHECK_COMPOSITE, default $SHEPHERD_WORK/physics1/fidelity/compo-oracle2.txt: bundle, CAB, path id), the data
    /// from the owner's install.</summary>
    static void CompositeOracle()
    {
        Up();
        var list = Environment.GetEnvironmentVariable("SHEPHERD_PHYSCHECK_COMPOSITE") is { Length: > 0 } e ? e : "$SHEPHERD_WORK/physics1/fidelity/compo-oracle2.txt";
        if (!File.Exists(list)) { Log.Info($"[physcheck]      composite: skipped (no oracle list at {list})"); return; }
        var cases = new List<(string bundle, string cab, long pid)>();
        foreach (var line in File.ReadLines(list))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^=== (\S+) (\S+) #(-?\d+)");
            if (m.Success) cases.Add((m.Groups[1].Value, m.Groups[2].Value, long.Parse(m.Groups[3].Value)));
        }
        int done = 0, paths = 0, ok = 0; double worst = 0;
        foreach (var (bundle, cab, pid) in cases)
        {
            var db = Assets.Loader.Db;
            Shepherd.Assets.TypeTreeObject t = null;
            foreach (var f in db.LoadBundleFiles(Path.Combine(db.BundleDir, bundle)))
            {
                if (f.Name != cab) continue;
                foreach (var o in f.Objects) if (o.PathId == pid && o.ClassId == 66) { t = Shepherd.Assets.TypeTreeReader.Read(f, o); break; }
            }
            if (t == null) { Check($"composite {bundle} #{pid}: found in the install", false); continue; }
            var rec = new NCollider2D { Kind = PhysRecords.Kind2D.Composite };
            PhysRecords.Fill(rec, 66, t, null);
            if (rec.ColliderPaths == null || rec.Paths == null) { Check($"composite {bundle} #{pid}: inputs and output present", false); continue; }
            bool u4 = PhysWorld.Sim4;   // Stage 4b: Unity's pipeline, compared bit for bit (every vertex, the start, the path order)
            var r = u4 ? Composite.RunUnity(rec.ColliderPaths, (float)rec.OffsetDistance * 1e7f, (float)rec.VertexDistance * 1e7f)
                       : Composite.Run(rec.ColliderPaths, Math.Round(rec.OffsetDistance * 1e7), Math.Round(rec.VertexDistance * 1e7));
            if (r == null) { Check("composite: the game's ClipperLib binds", false); return; }
            var ours = u4 ? Composite.ToFloatUnity(r) : Composite.ToFloat(r);
            var cmp = Composite.Compare(ours, rec.Paths, out double w, out bool counts, out bool dir);
            bool pass = counts && dir && w <= 1e-4;
            if (u4)
            {
                bool bits = ours.Length == rec.Paths.Length;
                for (int k = 0; bits && k < ours.Length; k++)
                {
                    bits = ours[k].Length == rec.Paths[k].Length;
                    for (int i = 0; bits && i < ours[k].Length; i++) bits = BitConverter.SingleToInt32Bits(ours[k][i]) == BitConverter.SingleToInt32Bits(rec.Paths[k][i]);
                }
                pass = bits;
                Check($"composite {bundle[..8]} #{pid}: {rec.ColliderPaths.Length} input paths -> Unity's m_CompositePaths bit for bit (every vertex, start, path order; Stage 4b)", pass, cmp);
            }
            else Check($"composite {bundle[..8]} #{pid}: {rec.ColliderPaths.Length} input paths -> counts and direction equal, Hausdorff <= 1e-4", pass, cmp);
            done++; paths += rec.Paths.Length; if (pass) ok++; worst = Math.Max(worst, w);
        }
        Check($"composite oracle: {done} composites, {paths} Unity paths reproduced (plan: the 4 non-stale composites, 12 paths{(PhysWorld.Sim4 ? "; bit for bit since Stage 4b" : "")})", done == 4 && ok == 4 && paths == 12,
              $"{ok} of {done} pass, worst Hausdorff {worst:E2}");
    }
}

/// <summary>physcheck's unit driver: rb.MovePosition(rb.position + V * Time.deltaTime) every FixedUpdate, as UnitObject.FixedUpdate does; it
/// records each arrival against its target and, between MeasureFrom and MeasureTo, the per-step x displacement.</summary>
[ProfileOnly]
public sealed class PhysMover : MonoBehaviour
{
    public Vector2 V; public int Steps, MeasureFrom = int.MaxValue, MeasureTo = int.MaxValue, MeasuredSteps; public float WorstArrival, MeasuredDx, MinDx = float.MaxValue, MaxDx = float.MinValue, FirstX = float.NaN;
    public bool LogHistory;
    Rigidbody2D _rb; Vector2 _target; bool _has; float _lastX; double _prevT;
    readonly List<(double t, float x)> _hist = new();
    void FixedUpdate()
    {
        _rb ??= GetComponent<Rigidbody2D>();
        var p = _rb.position;
        if (_has)
        {
            Steps++;
            float e = Math.Max(Math.Abs(p.x - _target.x), Math.Abs(p.y - _target.y)); if (e > WorstArrival) WorstArrival = e;
            if (Steps > MeasureFrom && Steps <= MeasureTo)
            {
                float dx = p.x - _lastX; MeasuredDx += dx; MeasuredSteps++; if (dx < MinDx) MinDx = dx; if (dx > MaxDx) MaxDx = dx;
                if (float.IsNaN(FirstX)) FirstX = _lastX;
            }
            if (LogHistory && (_hist.Count == 0 || _hist[^1].t != _prevT)) _hist.Add((_prevT, p.x));   // the pose the previous step produced, at that step's fixed time
        }
        _prevT = Runtime.Time.FixedNow;
        _lastX = p.x;
        _target = p + V * UnityEngine.Time.deltaTime; _has = true;
        _rb.MovePosition(_target);
    }
    /// <summary>After a tick: the newest step's pose (rb.position, the true pose) at its fixed time, so the log reaches P[k].</summary>
    public void AppendLatest()
    {
        _rb ??= GetComponent<Rigidbody2D>();
        double t = Runtime.Time.FixedNow;
        if (_hist.Count == 0 || _hist[^1].t != t) _hist.Add((t, _rb.position.x));
    }
    /// <summary>Rule 3 recomputed from this body's own step log: the pose at render time tr (lerp of the two last steps up to tr, or the stepped pose).</summary>
    public (float x, int j) Rule3(double tr, double fdt, bool interp)
    {
        int j = -1;
        for (int i = 0; i < _hist.Count; i++) if (_hist[i].t <= tr) j = i;
        if (j < 1) return (float.NaN, j);
        if (!interp) return (_hist[j].x, j);
        float a = (float)((tr - _hist[j].t) / fdt);
        return (_hist[j - 1].x + (_hist[j].x - _hist[j - 1].x) * a, j);
    }
}
#endif
