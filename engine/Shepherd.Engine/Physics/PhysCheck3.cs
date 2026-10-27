#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 3's physcheck scenes (physics-plan-v2 section 4 Stage 3 acceptance #1, #2, #3), at SHEPHERD_PHYS=3: <c>physcheck unit3</c>
/// (and in <c>all</c>). Built through the real Unity API (rb.velocity, rb.drag, rb.AddForce, rb.rotation, rb.MoveRotation, rb.constraints,
/// Physics2D.IgnoreCollision, OnCollision*2D handlers reading Collision2D) and run by the engine's frame loop: hooks -> flush -> the native step ->
/// write-back -> the dispatcher's collision group. Materials (no PhysicsMaterial2D setter at this stage) and edge points (Stage 4's setter) are set
/// on the typed records, as scene data would. The hypothesis-dependent scenes compute their expectation from the selected variant
/// (SHEPHERD_PHYS_H), so check-phys.sh runs them under each. The force-rule scene runs its frames at 0.01 s (0-step ticks), the rest at 1/60.
/// Scenes sit 300 u apart (from x = 8000). No static method here names a Unity physics type in its signature (IcallRegistry's scan).</summary>
public static partial class PhysCheck
{
    static GameObject Body3(string name, Vector2 at, float mass = 1f, int type = 0, float radius = 0.3f, int constraints = 4)
    {
        var g = new GameObject(name); g.layer = L; g.transform.position = new Vector3(at.x, at.y, 0f);
        g.AddComponent<Rigidbody2D>();
        var c = g.AddComponent<CircleCollider2D>(); c.radius = radius;
        var rb = Rb(g); rb.BodyType = type; rb.GravityScale = 0f; rb.Constraints = constraints; rb.SleepingMode = 0; rb.Mass = mass; PhysWorld.Changed(rb);
        return g;
    }
    static void Material3(Component c, float friction, float bounciness) { var k = Col(c); k.Material = new NPhysicsMaterial2D { Friction = friction, Bounciness = bounciness }; PhysWorld.Changed(k); }
    static Rigidbody2D Rb2(GameObject g) => g.GetComponent<Rigidbody2D>();

    static void Unit3()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        PhysCollRec.Ev.Clear(); PhysTrigRec.Ev.Clear();
        float fdt = Runtime.Time.FixedDeltaTime;
        Log.Info($"[physcheck]      unit3 under E1's rules: H-DAMP={Var("DAMP")} H-MAXTRANS={Var("MAXTRANS")} H-MAXROT={Var("MAXROT")} H-MOVEDRAG={Var("MOVEDRAG")} H-CHAIN={Var("CHAIN")} H-RELVEL={Var("RELVEL")} H-RELVELSIGN={Var("RELVELSIGN")} " +
                 $"H-NORMAL={Var("NORMAL")} H-CHILDSYNC={Var("CHILDSYNC")} H-TILT={Var("TILT")}; time fixes {(PhysTime.FrameFixed ? "on" : "off")}, step rule {(PhysTime.FrameStepCap == int.MaxValue ? "unity" : PhysTime.FrameStepCap.ToString())}");

        // ---- 1a: velocity 25, drag 10 -> 2.5 u (H-DAMP=pade; E1 Damp: 25 -> 20.8333 after a step, travel 2.49999881) ----
        var d = Body3("U3-drag", new Vector2(0f, -3000f));   // (x near 0: the decaying steps stay far above float spacing)
        var dp = d.AddComponent<PhysAt>(); float dx0 = float.NaN;
        dp.At = 3; dp.Do = () => { var rb = Rb2(d); rb.drag = 10f; rb.velocity = new Vector2(25f, 0f); dx0 = rb.position.x; };
        Steps(400);
        float travel = Rb2(d).position.x - dx0, dragNow = Rb2(d).drag;
        Check("1 velocity = 25, drag 10: travel 2.5 u (Box2D's Pade damping, H-DAMP=pade)", MathF.Abs(travel - 2.5f) <= 1e-3f && dragNow == 10f, $"travelled {travel:R}, drag reads {dragNow}");
        UObject.DestroyImmediate(d); Steps(2);

        // ---- 1b: AddForce(500) in FixedUpdate on 1 kg -> +10 u/s on that step (Force h / m), cleared after ----
        var f = Body3("U3-force", new Vector2(8300f, 0f));
        var fp = f.AddComponent<PhysAt>(); var fpr = f.AddComponent<PhysProbe>(); var fv = new List<float>();
        fp.At = 3; fp.Do = () => Rb2(f).AddForce(new Vector2(500f, 0f));
        fpr.Each = () => fv.Add(Rb2(f).velocity.x);
        Steps(6);
        // FixedUpdate reads before each step: [0, 0, 0 (the 3rd: AddForce after the probe? order by component), ...] -- find the first non-zero
        int fi = fv.FindIndex(v => v != 0f);
        Check("1 AddForce(500) on 1 kg in FixedUpdate: +10 u/s from the step that follows it, and no more after (forces cleared)", fi >= 0 && fv[fi] == 10f && fv.Skip(fi).All(v => v == 10f),
              $"velocity.x per FixedUpdate: {string.Join(" ", fv)}");
        UObject.DestroyImmediate(f); Steps(2);

        // ---- 1c: restitution 1 bounces above b2_velocityThreshold (1 u/s) and not below ----
        float Bounce(string tag, float x, float speed)
        {
            var wall = Static2("U3-floor-" + tag, new Vector2(x, 0f)); var wc = Box2(wall, new Vector2(4f, 1f), false); Material3(wc, 0f, 1f);
            var b = Body3("U3-ball-" + tag, new Vector2(x, 1.2f), 1f, 0, 0.5f); Material3(b.GetComponent<CircleCollider2D>(), 0f, 1f);
            var bp = b.AddComponent<PhysAt>(); bp.At = 2; bp.Do = () => Rb2(b).velocity = new Vector2(0f, -speed);
            Steps(80);
            float vy = Rb2(b).velocity.y;
            UObject.DestroyImmediate(wall); UObject.DestroyImmediate(b); Steps(2);
            return vy;
        }
        float vHi = Bounce("hi", 8600f, 5f), vLo = Bounce("lo", 8650f, 0.5f);
        Check("1 restitution 1: a 5 u/s approach rebounds (> 4.5 u/s); a 0.5 u/s one (below the 1 u/s threshold) does not (|v| < 0.1)", vHi > 4.5f && MathF.Abs(vLo) < 0.1f, $"after: {vHi:R} / {vLo:R}");

        // ---- 1d: H-MAXTRANS=per-step (E1 MaxTransRot L0, patch P2): 300 u/s moves 6 u a step unclamped and keeps its velocity (stock Box2D: 2 u, 100 u/s) ----
        var k = Body3("U3-clamp", new Vector2(8900f, 0f));
        var kp = k.AddComponent<PhysAt>(); var kpr = k.AddComponent<PhysProbe>(); float kx = float.NaN; int kn = 0; var kxs = new List<float>();
        kp.At = 3; kp.Do = () => { Rb2(k).velocity = new Vector2(300f, 0f); kx = Rb2(k).position.x; };
        kpr.Each = () => { if (!float.IsNaN(kx) && kn++ < 3) kxs.Add(Rb2(k).position.x); };
        Steps(6);
        float firstStep = kxs.Count >= 2 ? kxs[1] - kx : float.NaN;   // (kxs[0]: the probe of the FixedUpdate that set it, before the step)
        Check("1 the translation clamp is per step (H-MAXTRANS=per-step, patch P2: Physics2DSettings' 100 u a step): velocity 300 -> 6 u in a step, the velocity kept",
              MathF.Abs(firstStep - 6.0f) <= 1e-4f && Rb2(k).velocity.x == 300f, $"first step {firstStep:R} u, velocity after {Rb2(k).velocity.x:R}");
        UObject.DestroyImmediate(k); Steps(2);

        // ---- 1d': H-MAXROT=per-step (E1 MaxTransRot L4): a free body spun at 5000 deg/s turns 99.9 deg in a step (angular drag 0.05), beyond stock Box2D's 90 ----
        var rot = Body3("U3-spin", new Vector2(8950f, 0f), 1f, 0, 0.3f, 0);   // (rotation free)
        var rotp = rot.AddComponent<PhysAt>(); var rotpr = rot.AddComponent<PhysProbe>(); float r0 = float.NaN; var rots = new List<float>();
        rotp.At = 3; rotp.Do = () => { var rb = Rb2(rot); rb.angularVelocity = 5000f; r0 = rb.rotation; };
        rotpr.Each = () => { if (!float.IsNaN(r0) && rots.Count < 3) rots.Add(Rb2(rot).rotation); };
        Steps(5);
        float firstTurn = rots.Count >= 2 ? rots[1] - r0 : float.NaN;
        Check("1 the rotation clamp is per step (H-MAXROT=per-step, patch P2: 360 deg a step): 5000 deg/s turns 99.9 deg in a step (E1: 99.9000931)",
              MathF.Abs(firstTurn - 99.9001f) <= 2e-3f, $"first step {firstTurn:R} deg");
        UObject.DestroyImmediate(rot); Steps(2);

        // ---- 1d'': H-MOVEDRAG=drag-off-gravity-on (E1 MoveVel L1 / L2): MovePosition +0.03 a step on a body with drag 10 and gravity scale 1 moves exactly 0.03,
        //       falls h^2 g = 0.003924 a step (the project's gravity), and keeps its velocity 0 ----
        var mvb = Body3("U3-movegrav", new Vector2(-80f, 20f));   // (near the origin: 0.03 steps far above the float spacing)
        { var rb0 = Rb(mvb); rb0.GravityScale = 1f; rb0.LinearDrag = 10f; PhysWorld.Changed(rb0); }
        var mvp = mvb.AddComponent<PhysProbe>(); var mvy = new List<(float x, float y, float vx, float vy)>();
        mvp.Each = () => { var rb = Rb2(mvb); mvy.Add((rb.position.x, rb.position.y, rb.velocity.x, rb.velocity.y)); rb.MovePosition(rb.position + new Vector2(0.03f, 0f)); };
        Steps(6);
        bool mvOk = mvy.Count >= 5 && Enumerable.Range(2, mvy.Count - 2).All(i => MathF.Abs(mvy[i].x - mvy[i - 1].x - 0.03f) <= 2e-5f && MathF.Abs(mvy[i - 1].y - mvy[i].y - 0.003924f) <= 2e-6f && mvy[i].vx == 0f && mvy[i].vy == 0f);
        float gy = Runtime.Physics2DSettings.Values.GravityY;
        Check($"1 MovePosition with drag 10 and gravity scale 1 (H-MOVEDRAG=drag-off-gravity-on): +0.03 a step exactly, a fall of h^2 g = {0.0004f * -gy:R} a step, velocity 0 after",
              gy == -9.81f && mvOk, $"gravity {gy}; per step: {string.Join(" ", mvy.Select(v => $"({v.x:R},{v.y:R};{v.vx},{v.vy})"))}");
        UObject.DestroyImmediate(mvb); Steps(2);

        // ---- 1e: DeadBodySliding's read: OnCollisionEnter2D sees the post-solve velocity; the Collision2D's fields ----
        var dw = Static2("U3-wall", new Vector2(9201f, 0f)); var dwc = Box2(dw, new Vector2(1f, 4f), false); Material3(dwc, 0f, 1f);
        var db = Body3("U3-dead", new Vector2(9200f, 0f), 1f, 0, 0.3f); Material3(db.GetComponent<CircleCollider2D>(), 0f, 1f);
        var drec = db.AddComponent<PhysCollRec>();
        var dbp = db.AddComponent<PhysAt>(); dbp.At = 2; dbp.Do = () => Rb2(db).velocity = new Vector2(5f, 0f);
        Steps(30);
        var de = PhysCollRec.Of("U3-dead").FirstOrDefault(e => e.kind == "enter");
        const float wantRel = -5f;   // H-RELVEL=pre-solve, H-RELVELSIGN=other-minus-self (E1 RelVel): other (the wall, at rest) minus self (+5)
        Check("1 DeadBodySliding: OnCollisionEnter2D reads the post-solve velocity.x (bounced: -5)", de.kind == "enter" && MathF.Abs(de.vx + 5f) < 1e-3f, $"velocity.x in the handler {de.vx:R}");
        Check("1 the Collision2D: collider = the wall, otherCollider = self, gameObject = the wall's (no body), 1 contact between the ball and the wall's face (Box2D's manifold point at the post-step poses), normal (-1, 0) into this one (H-NORMAL=toward-self), " +
              "relativeVelocity -5 (H-RELVEL=pre-solve, H-RELVELSIGN=other-minus-self)",
              de.other == "U3-wall" && de.self2 == "U3-dead" && de.go == "U3-wall" && de.count == 1 && de.px > 9200.3f && de.px <= 9200.51f && de.ny == 0f && de.nx == -1f &&
              MathF.Abs(de.rvx - wantRel) < 1e-3f,
              $"collider {de.other}, otherCollider {de.self2}, gameObject {de.go}, contacts {de.count}, point ({de.px:R},{de.py:R}), normal ({de.nx},{de.ny}), relativeVelocity ({de.rvx:R},{de.rvy:R})");
        UObject.DestroyImmediate(dw); UObject.DestroyImmediate(db); Steps(2);

        // ---- 1f: Enter once, a Stay every step while touching, the Exit (0 contacts) when it leaves ----
        var sf = Static2("U3-shelf", new Vector2(9500f, 0f)); Box2(sf, new Vector2(4f, 1f), false);
        var sb = Body3("U3-rest", new Vector2(9500f, 0.85f)); sb.AddComponent<PhysCollRec>();
        var sp = sb.AddComponent<PhysProbe>(); int sn = 0;
        sp.Each = () => { sn++; Rb2(sb).velocity = sn <= 20 ? new Vector2(0f, -1f) : new Vector2(0f, 3f); };
        Steps(30);
        var sev = PhysCollRec.Of("U3-rest");
        int sEnter = sev.Count(e => e.kind == "enter"), sExit = sev.Count(e => e.kind == "exit"), sStay = sev.Count(e => e.kind == "stay");
        var sx = sev.FirstOrDefault(e => e.kind == "exit");
        var stays = sev.Where(e => e.kind == "stay").Select(e => e.step).ToList();
        bool contiguous = stays.Count > 0 && stays.Last() - stays.First() + 1 == stays.Count;
        Check("1 a body pressed onto a box: one Enter, a Stay on every step while touching, one Exit with 0 contacts when it lifts off",
              sEnter == 1 && sExit == 1 && sStay >= 10 && contiguous && sx.count == 0, $"{sEnter} Enter, {sStay} Stays (steps {stays.FirstOrDefault()}..{stays.LastOrDefault()}), {sExit} Exit with {sx.count} contacts");
        UObject.DestroyImmediate(sf); UObject.DestroyImmediate(sb); Steps(2);

        // ---- 1g: Physics2D.IgnoreCollision: two bodies pass through each other (no callback); the control collides ----
        int Pass(string tag, float x, bool ignore)
        {
            var a = Body3("U3-ig-a-" + tag, new Vector2(x, 0f)); var b = Body3("U3-ig-b-" + tag, new Vector2(x + 2f, 0f)); a.AddComponent<PhysCollRec>();
            if (ignore) Physics2D.IgnoreCollision(a.GetComponent<CircleCollider2D>(), b.GetComponent<CircleCollider2D>(), true);
            var ap = a.AddComponent<PhysProbe>(); ap.Each = () => { Rb2(a).velocity = new Vector2(3f, 0f); Rb2(b).velocity = new Vector2(-3f, 0f); };
            Steps(30);
            int n = PhysCollRec.Of("U3-ig-a-" + tag).Count(e => e.kind == "enter");
            float ax = Rb2(a).position.x;
            UObject.DestroyImmediate(a); UObject.DestroyImmediate(b); Steps(2);
            return n * 1000 + (ax > x + 1.5f ? 1 : 0);   // (passing through: a ends near x + 1.8; colliding: it stops near x + 0.7)
        }
        int ig = Pass("on", 9800f, true), ctl = Pass("off", 9850f, false);
        Check("1 Physics2D.IgnoreCollision(a, b): they pass through (no Enter); without it they collide", ig == 1 && ctl / 1000 == 1 && ctl % 1000 == 0, $"ignored: {ig / 1000} Enter, passed {ig % 1000 == 1}; control: {ctl / 1000} Enter, passed {ctl % 1000 == 1}");

        // ---- 1h: rotation sets, MoveRotation, FreezePositionX, mass / gravity scale setters ----
        var r = Body3("U3-rot", new Vector2(10100f, 0f), 1f, 0, 0.3f, 0);
        var rp = r.AddComponent<PhysAt>(); rp.At = 2; rp.Do = () => Rb2(r).rotation = 90f;
        Steps(4);
        float rz = r.transform.eulerAngles.z, rrb = Rb2(r).rotation;
        var rp2 = r.AddComponent<PhysAt>(); rp2.At = 1; rp2.Do = () => Rb2(r).MoveRotation(45f);
        Steps(3);
        float mrz = Rb2(r).rotation;
        Check("1 rb.rotation = 90 (a teleport: the transform follows after the next step); rb.MoveRotation(45) arrives in one step", MathF.Abs(rrb - 90f) < 1e-3f && MathF.Abs(rz - 90f) < 1e-2f && MathF.Abs(mrz - 45f) < 1e-3f,
              $"rotation {rrb:R}, transform z {rz:R}; after MoveRotation {mrz:R}");
        UObject.DestroyImmediate(r); Steps(2);
        var fz = Body3("U3-freezex", new Vector2(10400f, 0f));
        Rb2(fz).constraints = RigidbodyConstraints2D.FreezePositionX | RigidbodyConstraints2D.FreezeRotation;
        var fzp = fz.AddComponent<PhysProbe>(); fzp.Each = () => Rb2(fz).velocity = new Vector2(5f, 5f);
        Steps(10);
        Check("1 rb.constraints = FreezePositionX: pushed with (5, 5) it keeps x and moves in y", Rb2(fz).position.x == 10400f && Rb2(fz).position.y > 0.8f && Rb2(fz).constraints == (RigidbodyConstraints2D.FreezePositionX | RigidbodyConstraints2D.FreezeRotation),
              $"position {Rb2(fz).position}");
        UObject.DestroyImmediate(fz); Steps(2);

        // ---- 2 (C7): a kinematic hitbox under a moving dynamic parent: follow = at the parent's pose at each flush; frozen = held in world place ----
        var hp = Body3("U3-parent", new Vector2(10700f, 0f));
        var hc = new GameObject("U3-hitbox"); hc.layer = L; hc.transform.SetParent(hp.transform, false); hc.transform.localPosition = new Vector3(1f, 0f, 0f);
        hc.AddComponent<Rigidbody2D>(); var hrb = Rb(hc); hrb.BodyType = 1; hrb.GravityScale = 0f; hrb.SleepingMode = 0; PhysWorld.Changed(hrb);
        var hcc = hc.AddComponent<CircleCollider2D>(); hcc.radius = 0.5f; hcc.isTrigger = true;
        var hm = hp.AddComponent<PhysMover>(); hm.V = new Vector2(3f, 0f);
        var hpr = hp.AddComponent<PhysProbe>(); var hd = new List<float>(); var hx = new List<float>();
        hpr.Each = () => { var cr = Rb2(hc); hd.Add(MathF.Abs(cr.position.x - hc.transform.position.x)); hx.Add(cr.position.x); };
        long cm0 = PhysWorld.ChildMisses;
        Steps(20);
        bool hOk = hd.Skip(2).All(v => v <= PhysWorld.SyncTol) && PhysWorld.ChildMisses == cm0;
        Check("2 (C7) a kinematic hitbox under a moving parent: at its parent-relative pose at every flush (H-CHILDSYNC=follow, E1 ChildSync; sync check 0 misses)",
              hOk, $"|core - transform| per FixedUpdate: max {hd.Skip(2).DefaultIfEmpty(0f).Max():R}; core x first / last {hx.FirstOrDefault():R} / {hx.LastOrDefault():R}; " +
                   $"{(PhysConfig.CheckSync ? $"sync check misses +{PhysWorld.ChildMisses - cm0}" : "(SHEPHERD_CHECK_PHYS=sync off: the child check not running)")}");
        UObject.DestroyImmediate(hp); Steps(2);

        // ---- 3 (C16): a swipe at 135 degrees, localScale.y = -1 (Swipe.Init's mirror): it hits the target on the mirrored side, misses the other ----
        float X = 11000f;
        var tgtM = Body3("U3-target-mirrored", Vec(X, 0f) + Rot(135f, new Vector2(1f, -0.6f)), 1f, 0, 0.1f);
        var tgtU = Body3("U3-target-unmirrored", Vec(X, 0f) + Rot(135f, new Vector2(1f, 0.6f)), 1f, 0, 0.1f);
        var sw = new GameObject("U3-swipe"); sw.layer = L; sw.SetActive(false);
        sw.transform.position = new Vector3(X, 0f, 0f); sw.transform.eulerAngles = new Vector3(0f, 0f, 135f);
        sw.transform.localScale = new Vector3(1f, -1f, 1f);
        var pc = sw.AddComponent<PolygonCollider2D>(); pc.isTrigger = true;
        var pcr = Col(pc); pcr.Paths = new[] { new[] { 0.6f, 0.2f, 1.4f, 0.2f, 1.4f, 1.0f, 0.6f, 1.0f } };   // a box above the local x axis (y 0.2..1.0)
        PhysWorld.Changed(pcr);
        sw.AddComponent<PhysTrigRec>();
        Steps(2);
        sw.SetActive(true);
        Steps(3);
        var swe = PhysTrigRec.Of("U3-swipe").Where(e => e.kind == "enter").Select(e => e.other).ToList();
        Check("3 (C16) a swipe at 135 degrees mirrored (localScale.y = -1, the polygon re-wound): hits the target on the mirrored side, misses the other",
              swe.Contains("U3-target-mirrored") && !swe.Contains("U3-target-unmirrored"), $"Enters: {string.Join(", ", swe)}");
        UObject.DestroyImmediate(tgtM); UObject.DestroyImmediate(tgtU); UObject.DestroyImmediate(sw); Steps(2);

        // ---- H-CHAIN: a body meets an edge from its back (above an edge drawn left to right): two-sided stops it, one-sided lets it through ----
        var ce = Edge("U3-edge", new Vector2(11300f, 0f), new[] { -3f, 0f, 3f, 0f });
        ce.layer = L;
        var cb = Body3("U3-chainball", new Vector2(11300f, 1f), 1f, 0, 0.25f);
        var cbp = cb.AddComponent<PhysProbe>(); cbp.Each = () => Rb2(cb).velocity = new Vector2(0f, -5f);
        Steps(30);
        float cy = Rb2(cb).position.y;
        Check("H-CHAIN=two-sided (E1 ChainSide): a body pushed into an edge from its back (above a left-to-right edge) is stopped on it",
              cy > 0.2f && cy < 0.3f, $"y after {cy:R}");
        UObject.DestroyImmediate(ce); UObject.DestroyImmediate(cb); Steps(2);

        // ---- H-TILT (R13's shape): a BouncerSprite-like circle (r 0.428, offset (0, 0.145)) under a 60-degree X tilt: the full matrix halves the
        //      offset's y (cos 60: centre y 0.0725, the circle scaled by the larger projected axis), 2D TRS ignores the tilt (centre y 0.145) ----
        var tg = Static2("U3-tilt", new Vector2(-50f, -50f)); tg.transform.eulerAngles = new Vector3(60f, 0f, 0f);   // (near the origin: float spacing far below the tolerance)
        var tc = tg.AddComponent<CircleCollider2D>(); tc.radius = 0.428f; tc.offset = new Vector2(0f, 0.145f);
        Steps(2);
        var tb = ((Collider2D)tc).bounds;
        const float wantY = 0.0725f;
        Check("H-TILT=matrix (E1 Tilt: centre y 0.0725000054): a circle (r 0.428, offset (0, 0.145)) under a 60-degree X tilt: centre y 0.0725 (the full matrix: offset y x cos 60), radius 0.428",
              MathF.Abs(tb.center.y - (-50f + wantY)) < 1e-4f && MathF.Abs(tb.extents.x - 0.428f) < 1e-4f, $"bounds centre ({tb.center.x:R},{tb.center.y:R}), extents ({tb.extents.x:R},{tb.extents.y:R})");
        UObject.DestroyImmediate(tg); Steps(2);

        // ---- 1 (section 3): a force added outside FixedUpdate waits for the next step; over 0-step ticks it carries over and two ticks' forces add ----
        Runtime.Time.CaptureDeltaTime = 0.01f;
        var g = Body3("U3-carry", new Vector2(11600f, 0f));
        var gu = g.AddComponent<PhysUpdateAt>(); var gv = new List<(int frame, int step, float vx)>();
        int adds = 0;
        gu.Each = () =>
        {   // Update: add 100 N on two consecutive ticks starting at a tick whose next tick takes no step (0.01 s ticks: steps alternate, so a
            // tick that took a step is followed by one that takes none -- observed, not inferred from the accumulator's phase, which depends on
            // the frames the scenes before this one ran)
            bool stepped = gv.Count > 0 && PhysWorld.StepIndex != gv[^1].step;
            gv.Add((Runtime.Time.FrameCount, PhysWorld.StepIndex, Rb2(g).velocity.x));
            if (adds == 0 && gv.Count > 4 && stepped) { Rb2(g).AddForce(new Vector2(100f, 0f)); adds = 1; return; }
            if (adds == 1) { Rb2(g).AddForce(new Vector2(100f, 0f)); adds = 2; }
        };
        Frames(12);
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        var firstMove = gv.FindIndex(v => v.vx != 0f);
        bool carry = firstMove > 0 && gv[firstMove].vx == 4f;   // (100 + 100) N x 0.02 s / 1 kg: both ticks' forces in one step
        Check("1 (section 3) AddForce from Update waits for the next step; a tick without a step carries it, and the two ticks' forces add (+4 u/s in one step)", carry,
              string.Join(" ", gv.Select(v => $"f{v.frame}:s{v.step}:{v.vx:R}")));
        UObject.DestroyImmediate(g); Steps(2);

        if (PhysCollRec.Ev.Count > 0) File.WriteAllLines(Path.Combine(Program.LogDir, "physcheck-unit3-events.txt"), PhysCollRec.Ev.Select(e => $"{e.step}\t{e.self}\t{e.kind}\t{e.other}\t{e.count}\t{e.vx:R}"));
    }
    static Vector2 Vec(float x, float y) => new Vector2(x, y);
    static Vector2 Rot(float deg, Vector2 v) { float a = deg * Mathf.Deg2Rad, c = MathF.Cos(a), s = MathF.Sin(a); return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y); }
}

/// <summary>physcheck unit3's recorder: every collision message (Collision2D read as the game reads it), the step, the body's velocity at the call.</summary>
[ProfileOnly]
public sealed class PhysCollRec : MonoBehaviour
{
    public static readonly List<(int step, string self, string kind, string other, string self2, string go, int count, float px, float py, float nx, float ny, float rvx, float rvy, float vx)> Ev = new();
    public static List<(int step, string self, string kind, string other, string self2, string go, int count, float px, float py, float nx, float ny, float rvx, float rvy, float vx)> Of(string self) => Ev.Where(e => e.self == self).ToList();
    void Rec(string kind, Collision2D c)
    {
        var rb = GetComponent<Rigidbody2D>();
        var cps = c.contacts;
        var p = cps.Length > 0 ? cps[0] : default;
        Ev.Add((PhysWorld.StepIndex - 1, name, kind, c.collider != null ? c.collider.name : "null", c.otherCollider != null ? c.otherCollider.name : "null", c.gameObject != null ? c.gameObject.name : "null",
                c.contactCount, p.point.x, p.point.y, p.normal.x, p.normal.y, c.relativeVelocity.x, c.relativeVelocity.y, rb != null ? rb.velocity.x : float.NaN));
    }
    void OnCollisionEnter2D(Collision2D c) => Rec("enter", c);
    void OnCollisionStay2D(Collision2D c) => Rec("stay", c);
    void OnCollisionExit2D(Collision2D c) => Rec("exit", c);
}

/// <summary>physcheck unit3: runs Each in every FixedUpdate (before that step's physics).</summary>
[ProfileOnly]
public sealed class PhysProbe : MonoBehaviour
{
    public Action Each;
    void FixedUpdate() => Each?.Invoke();
}

/// <summary>physcheck unit3: runs Each in every Update (after the tick's fixed steps).</summary>
[ProfileOnly]
public sealed class PhysUpdateAt : MonoBehaviour
{
    public Action Each;
    void Update() => Each?.Invoke();
}
#endif
