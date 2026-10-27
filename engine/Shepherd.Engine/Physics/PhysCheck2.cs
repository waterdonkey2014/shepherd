#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 2's physcheck scenes (physics-plan-v2 section 4 Stage 2 acceptance #2 and #3), at SHEPHERD_PHYS=2:
/// <c>physcheck unit2 | queries</c> (and in <c>all</c>). Built through the Unity API (GameObjects, AddComponent, isTrigger, SetActive,
/// rb.position, rb.MovePosition from FixedUpdate) and run by the engine's frame loop, so the whole chain is exercised: hooks -> flush -> the
/// native step with P1 -> the drain -> the dispatcher -> the handlers. Since E1 the scenes check Unity's rules only (the losing variants are
/// deleted; physics-work/e1's CSVs are the evidence, named per scene); the E1 block (H-M) adds the rules E1 measured. Scenes sit 300 u apart.
/// No static method here names a Unity physics type in its signature (IcallRegistry's scan): colliders travel as Component.</summary>
public static partial class PhysCheck
{
    const int L = 0;   // the Default layer (the scenes need a layer pair the project's matrix lets collide: checked)
    static string Var(string n) => Hypotheses.Get(n);

    /// <summary>A body: Dynamic (type 0) or Kinematic (1), gravity scale 0, rotation frozen; never sleeps unless asked (sleepMode 1 = StartAwake).</summary>
    static GameObject Body2(string name, Vector2 at, int type = 0, int sleepMode = 0)
    {
        var g = new GameObject(name); g.layer = L; g.transform.position = new Vector3(at.x, at.y, 0f);
        g.AddComponent<Rigidbody2D>();
        var rb = Rb(g); rb.BodyType = type; rb.GravityScale = 0f; rb.Constraints = 4; rb.SleepingMode = sleepMode; PhysWorld.Changed(rb);
        return g;
    }
    static Component Circle2(GameObject g, float r, bool trigger)
    {
        var c = g.AddComponent<CircleCollider2D>(); c.radius = r; if (trigger) c.isTrigger = true; return c;
    }
    static Component Box2(GameObject g, Vector2 size, bool trigger)
    {
        var c = g.AddComponent<BoxCollider2D>(); c.size = size; if (trigger) c.isTrigger = true; return c;
    }
    static GameObject Static2(string name, Vector2 at) { var g = new GameObject(name); g.layer = L; g.transform.position = new Vector3(at.x, at.y, 0f); return g; }
    static PhysTrigRec Rec(GameObject g) => g.AddComponent<PhysTrigRec>();
    static int StepNow => PhysWorld.StepIndex;   // in a FixedUpdate: the step about to run; in a phase handler: that step + 1
    static void Steps(int n) { long b0 = PhysWorld.SlotB; while (PhysWorld.SlotB - b0 < n) Lifecycle.Frame(); }

    static void Unit2()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        PhysTrigRec.Ev.Clear();
        Check("unit2: the project's layer matrix lets Default collide with Default (the scenes' layer)", Runtime.Physics2DSettings.Collide(L, L));
        Log.Info($"[physcheck]      unit2 under E1's rules: H-TRIGPHASE={Var("TRIGPHASE")} H-KINTRIG={Var("KINTRIG")} H-KINSLEEP={Var("KINSLEEP")} H-SLEEPSTAY={Var("SLEEPSTAY")} " +
                 $"H-EXITSYNC={Var("EXITSYNC")} H-RECREATE={Var("RECREATE")} H-DROPQUEUED={Var("DROPQUEUED")} H-CBORDER={Var("CBORDER")}");

        // ---- A: exactly N Stays for N steps; RoomLockController's 6th Stay at 0.12 s ----
        var ab = Body2("A-body", new Vector2(3000f, 0f)); Circle2(ab, 0.3f, false);
        var at = Static2("A-trig", new Vector2(3000f, 0f)); Box2(at, new Vector2(2f, 2f), true); Rec(at);
        Steps(40);
        var aev = PhysTrigRec.Of("A-trig");
        int aEnter = aev.FirstOrDefault(e => e.kind == "enter").step, lastStep = StepNow - 1;
        var aStays = aev.Where(e => e.kind == "stay").Select(e => e.step).ToList();
        bool oneEach = aStays.Count == lastStep - aEnter && aStays.SequenceEqual(Enumerable.Range(aEnter + 1, lastStep - aEnter));
        Check("A exactly one Stay per step after the Enter (N Stays for N steps)", aev.Count(e => e.kind == "enter") == 1 && oneEach,
              $"Enter at step {aEnter}, {aStays.Count} Stays over steps {aEnter + 1}..{lastStep}");
        Check("A RoomLockController's count: the 6th Stay comes 0.12 s (6 steps) after the Enter", aStays.Count >= 6 && (aStays[5] - aEnter) * Runtime.Time.FixedDeltaTime is > 0.1199f and < 0.1201f,
              aStays.Count >= 6 ? $"6th Stay at step {aStays[5]} = +{(aStays[5] - aEnter) * Runtime.Time.FixedDeltaTime:F3} s" : "fewer than 6 Stays");
        UObject.DestroyImmediate(ab); UObject.DestroyImmediate(at); Steps(2);

        // ---- B (C1): motion entry, teleport entry, a new trigger fixture ----
        var bm = Body2("B-mover", new Vector2(3299f, 0f)); Circle2(bm, 0.25f, false);
        var bt = Static2("B-trig", new Vector2(3301f, 0f)); Box2(bt, new Vector2(0.5f, 2f), true); var brec = Rec(bt);
        float enterX = float.NaN; brec.Hook = (kind, other) => { if (kind == "enter" && float.IsNaN(enterX)) enterX = bm.GetComponent<Rigidbody2D>().position.x; };
        var mover = bm.AddComponent<PhysMover>(); mover.V = new Vector2(5f, 0f);
        Steps(30);
        // overlap (Box2D, with the box's 0.01 skin) first true at the step that brings the centre past 3300.75 - 0.25 - 0.01 = 3300.49
        float edge = 3300.49f, rel = enterX - edge;
        bool motionOk = rel >= 0f && rel < 0.1f;
        Check("B (C1) a body at 0.1 u/step into a trigger: Begin on the step whose integration made the overlap (H-TRIGPHASE=final; E1 TrigPhaseMotion: step 21)", motionOk,
              $"centre at the Enter {enterX:R}, {rel:F3} past the overlap threshold");
        UObject.DestroyImmediate(bm); UObject.DestroyImmediate(bt); Steps(2);

        var tb = Body2("B-tele", new Vector2(3300f, 10f)); Circle2(tb, 0.25f, false);
        var tt = Static2("B-trig2", new Vector2(3305f, 10f)); Box2(tt, new Vector2(1f, 1f), true); Rec(tt);
        var tp = tb.AddComponent<PhysAt>(); int tpStep = -1;
        tp.At = 5; tp.Do = () => { tpStep = StepNow; tb.GetComponent<Rigidbody2D>().position = new Vector2(3305f, 10f); };
        Steps(12);
        int tEnter = PhysTrigRec.Of("B-trig2").FirstOrDefault(e => e.kind == "enter").step;
        Check("B (C1) a teleport onto a trigger (rb.position in FixedUpdate): Begin on that step (final; E1 TrigPhaseTeleport: the act step)",
              tpStep >= 0 && tEnter == tpStep, $"teleport before step {tpStep}, Enter at step {tEnter}");
        UObject.DestroyImmediate(tb); UObject.DestroyImmediate(tt); Steps(2);

        var nb = Body2("B-new", new Vector2(3310f, 20f)); Circle2(nb, 0.25f, false); Rec(nb);
        var nt = Static2("B-newtrig", new Vector2(3310f, 20f)); var np = nt.AddComponent<PhysAt>(); int nStep = -1;
        np.At = 5; np.Do = () => { nStep = StepNow; var c = nt.AddComponent<CircleCollider2D>(); c.radius = 0.5f; c.isTrigger = true; };
        Steps(12);
        int nEnter = PhysTrigRec.Of("B-new").FirstOrDefault(e => e.kind == "enter").step;
        Check("B (C1) a trigger fixture created in FixedUpdate: Begin on that step under both phases", nStep >= 0 && nEnter == nStep, $"created before step {nStep}, Enter at step {nEnter}");
        UObject.DestroyImmediate(nb); UObject.DestroyImmediate(nt); Steps(2);

        // ---- C: a Kinematic swipe x a static grass trigger ----
        var cs = Body2("C-swipe", new Vector2(3600f, 0f), 1, 1); Circle2(cs, 0.5f, true);
        var cg = Static2("C-grass", new Vector2(3600.3f, 0f)); Circle2(cg, 0.3f, true); Rec(cg);
        Steps(10);
        int cEnters = PhysTrigRec.Of("C-grass").Count(e => e.kind == "enter");
        Check("C a Kinematic swipe trigger over a static grass trigger: fires (H-KINTRIG=all-but-static; E1 KinTrig)", cEnters == 1, $"{cEnters} Enter(s)");
        UObject.DestroyImmediate(cs); UObject.DestroyImmediate(cg); Steps(2);

        // ---- D (C4): a kinematic trigger at rest over a static trigger: Stays stop after 25 steps (sleep + stop; E1 KinSleep lane 0) ----
        {
            var dk = Body2("D-kin", new Vector2(3900f, 0f), 1, 1); Box2(dk, new Vector2(1f, 1f), true);
            var ds = Static2("D-static", new Vector2(3900f, 0f)); Box2(ds, new Vector2(2f, 2f), true); Rec(ds);
            int d0 = StepNow;
            Steps(60);
            var dev = PhysTrigRec.Of("D-static");
            int dEnter = dev.FirstOrDefault(e => e.kind == "enter").step, dStays = dev.Count(e => e.kind == "stay"), dLast = dev.Where(e => e.kind == "stay").Select(e => e.step).DefaultIfEmpty(-1).Max();
            bool dOk = dLast >= 0 && dLast - d0 <= 25 && dStays < 30;
            Check("D (C4) a kinematic trigger at rest over a static trigger: Stays stop once the body sleeps, 25 steps (H-KINSLEEP=sleep + H-SLEEPSTAY=stop)", dOk,
                  $"Enter at step {dEnter}, {dStays} Stays, the last at step {dLast} (+{dLast - d0} from the start)");
            UObject.DestroyImmediate(dk); UObject.DestroyImmediate(ds); Steps(2);
        }

        // ---- E: the Exit after SetActive(false); two Enters for a two-collider body; a trigger-trigger pair ----
        var eb = Body2("E-body", new Vector2(4200f, 0f)); Circle2(eb, 0.3f, false); Rec(eb);
        var et = Static2("E-trig", new Vector2(4200f, 0f)); Box2(et, new Vector2(2f, 2f), true);
        var ep = eb.AddComponent<PhysAt>(); int eStep = -1;
        ep.At = 6; ep.Do = () => { eStep = StepNow; et.SetActive(false); };
        Steps(14);
        var eev = PhysTrigRec.Of("E-body");
        var eExit = eev.FirstOrDefault(e => e.kind == "exit");
        Check("E the Exit after SetActive(false) at the call (H-EXITSYNC=sync; E1 ExitSync lane 0), to the other side, with the trigger as its argument",
              eExit.kind == "exit" && eExit.other == "E-trig" && eExit.step == eStep - 1 && !eExit.inPhase && eev.Count(e => e.kind == "stay" && e.step >= eStep) == 0,
              $"deactivated before step {eStep}; Exit at step {eExit.step} (in a phase: {eExit.inPhase})");
        UObject.DestroyImmediate(eb); UObject.DestroyImmediate(et); Steps(2);

        var e2 = Body2("E2-body", new Vector2(4250f, 0f)); Circle2(e2, 0.2f, false);
        var e2c = new GameObject("E2-child"); e2c.layer = L; e2c.transform.SetParent(e2.transform, false); e2c.transform.localPosition = new Vector3(0.3f, 0f, 0f); Circle2(e2c, 0.2f, false);
        var e2t = Static2("E2-trig", new Vector2(4253f, 0f)); Box2(e2t, new Vector2(1f, 2f), true); Rec(e2t);
        var e2m = e2.AddComponent<PhysMover>(); e2m.V = new Vector2(5f, 0f);
        Steps(40);
        var e2ev = PhysTrigRec.Of("E2-trig").Where(e => e.kind == "enter").Select(e => e.other).ToList();
        Check("E two Enters for a two-collider body (one per collider pair)", e2ev.Count == 2 && e2ev.Contains("E2-body") && e2ev.Contains("E2-child"), string.Join(", ", e2ev));
        UObject.DestroyImmediate(e2); UObject.DestroyImmediate(e2t); Steps(2);

        var e3 = Body2("E3-body", new Vector2(4300f, 0f)); Circle2(e3, 0.3f, true); Rec(e3);
        var e3t = Static2("E3-trig", new Vector2(4300.2f, 0f)); Box2(e3t, new Vector2(1f, 1f), true); Rec(e3t);
        Steps(4);
        Check("E a trigger-trigger pair (a body's trigger in a static trigger): Enter both ways",
              PhysTrigRec.Of("E3-body").Count(e => e.kind == "enter" && e.other == "E3-trig") == 1 && PhysTrigRec.Of("E3-trig").Count(e => e.kind == "enter" && e.other == "E3-body") == 1);
        UObject.DestroyImmediate(e3); UObject.DestroyImmediate(e3t); Steps(2);

        // ---- F (C9): isTrigger flipped on a touching pair; a collider deactivated by an earlier handler of the same phase ----
        var fb = Body2("F-body", new Vector2(4600f, 0.75f)); Circle2(fb, 0.3f, false); Rec(fb);
        var fx = Static2("F-box", new Vector2(4600f, 0f)); var fbox = Box2(fx, new Vector2(2f, 1f), false);
        var fm = fb.AddComponent<PhysMover>(); fm.V = new Vector2(0f, -1f);   // pressed onto the box's top: a touching solid pair
        Steps(20);
        var fp = fx.AddComponent<PhysAt>(); int fStep = -1;
        fp.At = 2; fp.Do = () => { fStep = StepNow; ((Collider2D)fbox).isTrigger = true; };
        Steps(8);
        var fev = PhysTrigRec.Of("F-body").Where(e => e.kind == "enter" && e.other == "F-box").ToList();
        var fst = PhysTrigRec.Of("F-body").Where(e => e.kind == "stay" && e.other == "F-box").Select(e => e.step).ToList();
        Check("F (C9) isTrigger flipped on a touching solid pair: no Enter, the touch goes on as trigger Stays from that step (H-RECREATE=silent; E1 Recreate lane 0: TStay from the flip's step)",
              fev.Count == 0 && fst.Count > 0 && fst[0] == fStep, $"flipped before step {fStep}; trigger Enters {string.Join(",", fev.Select(e => e.step))}; first trigger Stay {(fst.Count > 0 ? fst[0] : -1)}");
        UObject.DestroyImmediate(fb); UObject.DestroyImmediate(fx); Steps(2);

        var ga = Body2("F2-a", new Vector2(4700f, 10f)); Circle2(ga, 0.3f, false);
        var gb = Body2("F2-b", new Vector2(4700f, 12f)); Circle2(gb, 0.3f, false);
        var gt = Static2("F2-trig", new Vector2(4705f, 0f)); Box2(gt, new Vector2(2f, 2f), true); var grec = Rec(gt);
        string firstIn = null;
        grec.Hook = (kind, other) => { if (kind == "enter" && firstIn == null) { firstIn = other; (other == "F2-a" ? gb : ga).SetActive(false); } };   // deactivates the other one, mid-phase
        var gp = gt.AddComponent<PhysAt>(); int gStep = -1;
        gp.At = 3; gp.Do = () => { gStep = StepNow; ga.GetComponent<Rigidbody2D>().position = new Vector2(4705f, 0.4f); gb.GetComponent<Rigidbody2D>().position = new Vector2(4705f, -0.4f); };
        Steps(10);
        var gev = PhysTrigRec.Of("F2-trig");
        string other2 = firstIn == "F2-a" ? "F2-b" : "F2-a";
        int gFirst = gev.Where(e => e.kind == "enter").Select(e => e.step).DefaultIfEmpty(-1).Min();   // (the teleports' entry step)
        int gEnters = gev.Count(e => e.kind == "enter" && e.step == gFirst), gExits = gev.Count(e => e.kind == "exit" && e.other == other2 && e.step == gFirst && e.inPhase);
        int gLastEnter = gev.FindLastIndex(e => e.kind == "enter" && e.step == gFirst), gExitAt = gev.FindIndex(e => e.kind == "exit" && e.other == other2);
        Check("F (C9) a collider deactivated by a handler earlier in the same phase: its queued Enter still delivered, its Exit appended later in the same phase (H-DROPQUEUED=deliver, H-EXITSYNC=sync; E1 DropQueued)",
              firstIn != null && gEnters == 2 && gExits == 1 && gExitAt > gLastEnter, $"teleported before step {gStep}; first Enter {firstIn} at step {gFirst}; Enters at that step: {gEnters}; Exits of {other2} in that phase: {gExits} (event #{gExitAt}, last Enter #{gLastEnter})");
        UObject.DestroyImmediate(ga); UObject.DestroyImmediate(gb); UObject.DestroyImmediate(gt); Steps(2);

        // ---- E1 (physics-plan-v2 section 5 / T6; vita/notes/physics-ledger.md): the rules E1 measured that the scenes above do not reach ----
        // H: Rigidbody2D.simulated = false is the one deferred leave (E1 ExitSync lane 2): its Exit at the step; false -> true in one FixedUpdate is no event (ReEnable lane 2)
        var hsb = Body2("H-body", new Vector2(5300f, 0f)); Circle2(hsb, 0.3f, false); Rec(hsb);
        var ht = Static2("H-trig", new Vector2(5300f, 0f)); Box2(ht, new Vector2(2f, 2f), true);
        var hsb2 = Body2("H2-body", new Vector2(5310f, 0f)); Circle2(hsb2, 0.3f, false); Rec(hsb2);
        var ht2 = Static2("H2-trig", new Vector2(5310f, 0f)); Box2(ht2, new Vector2(2f, 2f), true);
        var hp = hsb.AddComponent<PhysAt>(); int hStep = -1;
        hp.At = 6; hp.Do = () => { hStep = StepNow; hsb.GetComponent<Rigidbody2D>().simulated = false; var r2 = hsb2.GetComponent<Rigidbody2D>(); r2.simulated = false; r2.simulated = true; };
        Steps(12);
        var hx = PhysTrigRec.Of("H-body").FirstOrDefault(e => e.kind == "exit"); var h2 = PhysTrigRec.Of("H2-body");
        Check("H (E1 ExitSync lane 2) Rigidbody2D.simulated = false: the Exit comes at the step, in its phase (the one deferred leave)", hx.kind == "exit" && hx.step == hStep && hx.inPhase,
              $"unsimulated before step {hStep}; Exit at step {hx.step} (in a phase: {hx.inPhase})");
        Check("H (E1 ReEnable lane 2) simulated false -> true in one FixedUpdate: no Exit, no second Enter, the Stays go on through that step",
              h2.Count(e => e.kind == "exit") == 0 && h2.Count(e => e.kind == "enter") == 1 && h2.Any(e => e.kind == "stay" && e.step == hStep),
              $"Exits {h2.Count(e => e.kind == "exit")}, Enters {h2.Count(e => e.kind == "enter")}, a Stay at step {hStep}: {h2.Any(e => e.kind == "stay" && e.step == hStep)}");
        UObject.DestroyImmediate(hsb); UObject.DestroyImmediate(ht); UObject.DestroyImmediate(hsb2); UObject.DestroyImmediate(ht2); Steps(2);

        // I: Object.Destroy(GameObject) takes the collider out of the world at the call (E1 ExitSync lane 3): the Exit inside the call to both sides,
        // the doomed GameObject still active; queries no longer see it
        var ib = Body2("I-body", new Vector2(5400f, 0f)); Circle2(ib, 0.3f, false); Rec(ib);
        var it = Static2("I-trig", new Vector2(5400f, 0f)); Box2(it, new Vector2(2f, 2f), true); var irec = Rec(it);
        bool iActiveAtExit = false; irec.Hook = (k, o) => { if (k == "exit") iActiveAtExit = it != null && it.activeInHierarchy; };
        var ip = ib.AddComponent<PhysAt>(); int iStep = -1; bool iSeenBefore = false, iSeenAfter = true;
        ip.At = 6; ip.Do = () => { iStep = StepNow; iSeenBefore = Physics2D.OverlapPoint(new Vector2(5400.6f, 0.6f)) != null; UObject.Destroy(it); iSeenAfter = Physics2D.OverlapPoint(new Vector2(5400.6f, 0.6f)) != null; };
        Steps(8);
        var ix = PhysTrigRec.Of("I-body").FirstOrDefault(e => e.kind == "exit"); var ixs = PhysTrigRec.Of("I-trig").FirstOrDefault(e => e.kind == "exit");
        Check("I (E1 ExitSync lane 3) Destroy(GameObject): the Exit inside the call, to both sides (the doomed one while still active), the collider gone from queries at once",
              ix.kind == "exit" && ix.step == iStep - 1 && !ix.inPhase && ixs.kind == "exit" && !ixs.inPhase && iActiveAtExit && iSeenBefore && !iSeenAfter,
              $"destroyed before step {iStep}; body's Exit at step {ix.step} (in a phase: {ix.inPhase}), trigger's {ixs.kind} (active then: {iActiveAtExit}); OverlapPoint before / after {iSeenBefore} / {iSeenAfter}");
        UObject.DestroyImmediate(ib); Steps(2);

        // J: enabled false -> true in one FixedUpdate (E1 ReEnable lane 0): the Exit at the disable, the Enter at that step (H-REENABLE=exit+enter)
        var jb = Body2("J-body", new Vector2(5500f, 0f)); Circle2(jb, 0.3f, false); Rec(jb);
        var jt = Static2("J-trig", new Vector2(5500f, 0f)); var jc = (Collider2D)Box2(jt, new Vector2(2f, 2f), true);
        var jp = jb.AddComponent<PhysAt>(); int jStep = -1;
        jp.At = 6; jp.Do = () => { jStep = StepNow; jc.enabled = false; jc.enabled = true; };
        Steps(8);
        var jev = PhysTrigRec.Of("J-body");
        Check("J (E1 ReEnable lane 0) enabled false -> true in one FixedUpdate: the Exit at the disable, the Enter in that step's phase, no Stay at that step",
              jev.Any(e => e.kind == "exit" && e.step == jStep - 1 && !e.inPhase) && jev.Any(e => e.kind == "enter" && e.step == jStep && e.inPhase) && !jev.Any(e => e.kind == "stay" && e.step == jStep),
              string.Join(" ", jev.Where(e => e.step >= jStep - 1 && e.step <= jStep + 1).Select(e => $"{e.kind}@{e.step}{(e.inPhase ? "" : "(call)")}")));
        UObject.DestroyImmediate(jb); UObject.DestroyImmediate(jt); Steps(2);

        // K: a layer change (E1 Recreate lanes 2 / 3): to a layer that still collides, silent (the Stays go on); to one that no longer does, the Exit at the step
        bool kLayersOk = Runtime.Physics2DSettings.Collide(L, 1) && !Runtime.Physics2DSettings.Collide(L, 10);
        var kb = Body2("K-body", new Vector2(5600f, 0f)); Circle2(kb, 0.3f, false); Rec(kb);
        var kt = Static2("K-trig", new Vector2(5600f, 0f)); Box2(kt, new Vector2(2f, 2f), true);
        var kb2 = Body2("K2-body", new Vector2(5610f, 0f)); Circle2(kb2, 0.3f, false); Rec(kb2);
        var kt2 = Static2("K2-trig", new Vector2(5610f, 0f)); Box2(kt2, new Vector2(2f, 2f), true);
        var kp = kb.AddComponent<PhysAt>(); int kStep = -1;
        kp.At = 6; kp.Do = () => { kStep = StepNow; kt.layer = 10; kt2.layer = 1; };
        Steps(8);
        var kev = PhysTrigRec.Of("K-body"); var k2 = PhysTrigRec.Of("K2-body");
        Check("K (E1 Recreate lanes 2 / 3) a layer change: to a non-colliding layer (10), the Exit at the step; to a still-colliding one (1), no Exit / Enter, the Stays go on",
              kLayersOk && kev.Any(e => e.kind == "exit" && e.step == kStep && e.inPhase) && k2.Count(e => e.kind == "exit") == 0 && k2.Count(e => e.kind == "enter") == 1 && k2.Any(e => e.kind == "stay" && e.step == kStep),
              $"layers 0x1 / 0x10 collide: {Runtime.Physics2DSettings.Collide(L, 1)} / {Runtime.Physics2DSettings.Collide(L, 10)}; K Exit at {kev.FirstOrDefault(e => e.kind == "exit").step}; K2 Exits {k2.Count(e => e.kind == "exit")}, Enters {k2.Count(e => e.kind == "enter")}");
        UObject.DestroyImmediate(kb); UObject.DestroyImmediate(kt); UObject.DestroyImmediate(kb2); UObject.DestroyImmediate(kt2); Steps(2);

        // L: contactCaptureLayers leaving the other's layer out silences the pair on both sides (E1 Capture: S.contactCaptureLayers = 0)
        var lb = Body2("L-body", new Vector2(5700f, 0f)); Circle2(lb, 0.3f, false); Rec(lb);
        var lt = Static2("L-trig", new Vector2(5700f, 0f)); var lc = (Collider2D)Box2(lt, new Vector2(2f, 2f), true); Rec(lt);
        Col(lc).ContactCaptureLayers = 0u;   // (the record field: the game sets none at run time)
        Steps(6);
        Check("L (E1 Capture) contactCaptureLayers = 0 on one collider: no callback on either side", PhysTrigRec.Of("L-body").Count == 0 && PhysTrigRec.Of("L-trig").Count == 0,
              $"{PhysTrigRec.Of("L-body").Count} / {PhysTrigRec.Of("L-trig").Count} events");
        UObject.DestroyImmediate(lb); UObject.DestroyImmediate(lt); Steps(2);

        // ---- G (C15, Stage 1's deferred half): runtime AddComponent<BoxCollider2D>: OverlapPoint at (0.49, 0) true, (0.51, 0) false ----
        var hb = Static2("G-helper", new Vector2(5000f, 0f)); var hbox = (Collider2D)Box2(hb, Vector2.one, false);
        bool in49 = hbox.OverlapPoint(new Vector2(5000.49f, 0f)), in51 = hbox.OverlapPoint(new Vector2(5000.51f, 0f));
        Check("G (C15) runtime AddComponent<BoxCollider2D>: Collider2D.OverlapPoint at (0.49, 0) true, at (0.51, 0) false", in49 && !in51, $"{in49} / {in51}");
        UObject.DestroyImmediate(hb); Steps(2);
        if (PhysTrigRec.Ev.Count > 0) File.WriteAllLines(Path.Combine(Program.LogDir, "physcheck-unit2-events.txt"), PhysTrigRec.Ev.Select(e => $"{e.step}\t{e.self}\t{e.kind}\t{e.other}\t{e.inPhase}"));
    }

    /// <summary>Stage 2 acceptance #3: the query contract through the real rewritten modules (Physics2D / PhysicsScene2D / Collider2D's managed
    /// wrappers -> icalls -> the core): the legacy 3-icall pattern and every census overload's canonical fields.</summary>
    static void Queries()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        float X = 6000f;
        var qb = Static2("Q-box", new Vector2(X + 5f, 0f)); var qbox = (Collider2D)Box2(qb, new Vector2(2f, 1f), false);
        var qb2 = Static2("Q-box2", new Vector2(X + 12f, 0f)); Box2(qb2, new Vector2(1f, 1f), false);
        var qc = Static2("Q-circle", new Vector2(X, 5f)); Circle2(qc, 0.5f, false);
        var qt = Static2("Q-trig", new Vector2(X + 10f, 10f)); var qtrig = (Collider2D)Circle2(qt, 1f, true);
        Steps(2);
        int mask = 1 << L;
        int Id(string n) => Array.IndexOf(IcallRegistry.Names, n);
        int iQht = Id("UnityEngine.Physics2D::get_queriesHitTriggers"), iCc = Id("UnityEngine.ContactFilter2D::CheckConsistency_Injected"), iRay = Id("UnityEngine.PhysicsScene2D::Raycast_Internal_Injected");
        long h0 = Hit(iQht), h1 = Hit(iCc), h2 = Hit(iRay);
        var r = Physics2D.Raycast(new Vector2(X, 0f), Vector2.right, 20f, mask);
        Check("Q1 Physics2D.Raycast(Vector2, Vector2, float, int): the legacy 3-icall pattern (queriesHitTriggers, CheckConsistency, Raycast_Internal) once each",
              Hit(iQht) - h0 == 1 && Hit(iCc) - h1 == 1 && Hit(iRay) - h2 == 1, $"+{Hit(iQht) - h0} / +{Hit(iCc) - h1} / +{Hit(iRay) - h2}");
        Check("Q1 ... and a correct RaycastHit2D: the box's face (distance 4, point (4, 0), normal (-1, 0), centroid = point, fraction 0.2, collider)",
              r.collider == qbox && Near(r.distance, 4f) && Near(r.point.x, X + 4f) && r.normal == Vector2.left && r.centroid == r.point && Near(r.fraction, 0.2f) && (bool)r,
              $"collider {r.collider?.name}, distance {r.distance:R}, point {r.point}, normal {r.normal}, centroid {r.centroid}, fraction {r.fraction:R}");
        var all = Physics2D.RaycastAll(new Vector2(X - 10f, 0f), Vector2.right, 40f, mask);
        Check("Q2 RaycastAll: every hit, one per collider, sorted by distance", all.Length == 2 && all[0].collider == qbox && all[1].collider?.name == "Q-box2" && Near(all[0].distance, 14f) && Near(all[1].distance, 21.5f),
              string.Join(", ", all.Select(h => $"{h.collider?.name} {h.distance:R}")));
        var arr = new RaycastHit2D[1];
        int na = Physics2D.RaycastNonAlloc(new Vector2(X - 10f, 0f), Vector2.right, arr, 40f, mask);
        Check("Q3 RaycastNonAlloc into a 1-element array: the count stored (1), the closest hit", na == 1 && arr[0].collider == qbox, $"{na}, {arr[0].collider?.name}");
        var lc = Physics2D.Linecast(new Vector2(X, 0f), new Vector2(X + 10f, 0f), mask);
        Check("Q4 Linecast: the box, distance 4 along the segment", lc.collider == qbox && Near(lc.distance, 4f), $"{lc.collider?.name} {lc.distance:R}");
        bool sic = Physics2D.queriesStartInColliders;
        var si = Physics2D.Raycast(new Vector2(X + 5f, 0f), Vector2.right, 20f, mask);
        Check($"Q5 a ray starting inside the box (queriesStartInColliders {sic}): {(sic ? "the box at distance 0, the point at the origin, the normal -direction (H-STARTNORMAL)" : "the box is ignored")}",
              sic ? si.collider == qbox && si.distance == 0f && si.point == new Vector2(X + 5f, 0f) && si.normal == Vector2.left : si.collider != qbox,
              $"{si.collider?.name} {si.distance:R} {si.point} {si.normal}");
        var cc = Physics2D.CircleCast(new Vector2(X, 0f), 0.5f, Vector2.right, 20f, mask);
        Check("Q6 CircleCast r 0.5 (E1, H-CASTSLOP=toi): the centroid stops at Box2D's TOI target, 0.5 + 0.01 skin - 0.015 before the face (distance 3.505), the point midway between the surfaces (3.9975), the normal",
              cc.collider == qbox && Near(cc.distance, 3.505f, 2e-4f) && Near(cc.centroid.x, X + 3.505f, 1e-3f) &&
              Near(cc.point.x, X + 3.9975f, 1e-3f) && Near(cc.normal.x, -1f, 1e-4f), $"{cc.collider?.name} {cc.distance:R} centroid {cc.centroid} point {cc.point} normal {cc.normal}");
        var zc = Physics2D.CircleCast(new Vector2(X + 5f, 0.7f), 0.3f, Vector2.zero, 0f, mask);
        Check("Q7 CircleCast with a zero direction and distance (the game's overlap test): the overlapping box at distance 0", zc.collider == qbox && zc.distance == 0f, $"{zc.collider?.name} {zc.distance:R}");
        var bc = Physics2D.BoxCast(new Vector2(X, 0f), new Vector2(1f, 1f), 0f, Vector2.right, 20f, mask);
        Check("Q8 BoxCast 1 x 1 (E1: both skins, target 0.005): distance 3.495", bc.collider == qbox && Near(bc.distance, 3.495f, 2e-4f), $"{bc.collider?.name} {bc.distance:R}");
        var oc = Physics2D.OverlapCircle(new Vector2(X + 6.205f, 0f), 0.2f, mask);
        var oc2 = Physics2D.OverlapCircle(new Vector2(X + 6.215f, 0f), 0.2f, mask);
        Check("Q9 OverlapCircle keeps Box2D's 0.01 skin (H-QSKIN): 0.005 off the face overlaps, 0.015 off does not", oc == qbox && oc2 == null, $"{oc?.name} / {oc2?.name}");
        var op = Physics2D.OverlapPoint(new Vector2(X + 5.99f, 0f), mask); var op2 = Physics2D.OverlapPoint(new Vector2(X + 6.005f, 0f), mask);
        Check("Q9 OverlapPoint without skin: inside 0.01 from the face yes, 0.005 outside no", op == qbox && op2 == null, $"{op?.name} / {op2?.name}");
        var oa = Physics2D.OverlapCircleAll(new Vector2(X + 8f, 0f), 4f, mask);
        var ob = Physics2D.OverlapBoxAll(new Vector2(X + 8f, 0f), new Vector2(12f, 2f), 0f, mask);
        Check("Q10 OverlapCircleAll / OverlapBoxAll: both boxes, each once", oa.Length == 2 && ob.Length == 2 && oa.Contains(qbox) && ob.Contains(qbox), $"{oa.Length} / {ob.Length}");
        var oarr = new Collider2D[1];
        Check("Q10 OverlapCircleNonAlloc into a 1-element array (A*'s any-hit form): 1", Physics2D.OverlapCircleNonAlloc(new Vector2(X + 8f, 0f), 4f, oarr, mask) == 1 && oarr[0] != null);
        bool qht = Physics2D.queriesHitTriggers;
        var tr1 = Physics2D.Raycast(new Vector2(X + 10f, 5f), Vector2.up, 10f, mask);
        var nf = new ContactFilter2D { useTriggers = false }; nf.SetLayerMask(mask);
        var tres = new List<RaycastHit2D>();
        int tn = Physics2D.Raycast(new Vector2(X + 10f, 5f), Vector2.up, nf, tres, 10f);
        Check($"Q11 triggers: the legacy ray {(qht ? "hits" : "misses")} the trigger (queriesHitTriggers {qht}); a ContactFilter2D with useTriggers false misses it",
              (qht ? tr1.collider == qtrig && Near(tr1.distance, 4f) : tr1.collider == null) && tn == 0, $"{tr1.collider?.name} {tr1.distance:R}; filtered {tn}");
        var b = qbox.bounds;
        Check("Q12 Collider2D.bounds: the box's world AABB without skin, z = the transform's", b.center == new Vector3(X + 5f, 0f, 0f) && b.size == new Vector3(2f, 1f, 0f), $"center {b.center} size {b.size}");
        var cp = Physics2D.ClosestPoint(new Vector2(X + 8f, 0f), qbox); var cpi = qbox.ClosestPoint(new Vector2(X + 5f, 0.2f));
        Check("Q13 ClosestPoint: the face point outside -- with Box2D's 0.01 skin (E1: 1.01 off a face at 1) -- the point itself inside", Near(cp.x, X + 6.01f, 1e-3f) && cp.y == 0f && cpi == new Vector2(X + 5f, 0.2f), $"{cp} / {cpi}");
        Check("Q14 Collider2D.OverlapPoint: inside / outside", qbox.OverlapPoint(new Vector2(X + 4.5f, 0.4f)) && !qbox.OverlapPoint(new Vector2(X + 3.9f, 0f)));
        var gbody = Body2("Q-guest", new Vector2(X + 10f, 10.3f)); var gcol = (Collider2D)Circle2(gbody, 0.3f, false);
        Steps(2);
        var cl = new List<Collider2D>();
        int gn = qtrig.GetContacts(cl);
        Check("Q15 GetContacts (colliders only): the last step's touching contacts of the trigger", gn == 1 && cl.Count == 1 && cl[0] == gcol, $"{gn}: {string.Join(", ", cl.Select(x => x?.name))}");
        Check("Q15 IsTouching / IsTouchingLayers", qtrig.IsTouching(gcol) && qtrig.IsTouchingLayers(mask) && !qbox.IsTouching(gcol));
        // the rest of the census overloads (plan section 2 "Stage 2"): CircleCastAll, CapsuleCast, OverlapBox (array), OverlapPoint (array),
        // OverlapCollider (list), a depth filter, excludeLayers, an Outlines composite's OverlapPoint (the union's inside)
        var cca = Physics2D.CircleCastAll(new Vector2(X - 10f, 0f), 0.25f, Vector2.right, 40f, mask);
        Check("Q16 CircleCastAll (E1 TOI distances): both boxes, sorted, one hit each", cca.Length == 2 && cca[0].collider == qbox && Near(cca[0].distance, 13.755f, 1e-3f) && Near(cca[1].distance, 21.255f, 1e-3f),
              string.Join(", ", cca.Select(h => $"{h.collider?.name} {h.distance:R}")));
        var cap = Physics2D.CapsuleCast(new Vector2(X, 0f), new Vector2(0.5f, 1.5f), CapsuleDirection2D.Vertical, 0f, Vector2.right, 20f, mask);
        Check("Q17 CapsuleCast (vertical 0.5 x 1.5, E1 TOI): its side stops 0.25 + 0.01 - 0.015 before the face (distance 3.755)", cap.collider == qbox && Near(cap.distance, 3.755f, 1e-3f), $"{cap.collider?.name} {cap.distance:R}");
        var barr = new Collider2D[4];
        int nb = Physics2D.OverlapBoxNonAlloc(new Vector2(X + 8f, 0f), new Vector2(12f, 2f), 0f, barr, mask);
        var parr = new Collider2D[4];
        int np2 = Physics2D.OverlapPointNonAlloc(new Vector2(X + 5f, 0.1f), parr, mask);
        Check("Q18 OverlapBoxNonAlloc / OverlapPointNonAlloc: the counts stored and the colliders", nb == 2 && barr.Take(2).Contains(qbox) && np2 == 1 && parr[0] == qbox, $"{nb} / {np2}");
        var oc3 = new List<Collider2D>();
        var ocf = new ContactFilter2D().NoFilter();
        int noc = Physics2D.OverlapCollider(qbox, ocf, oc3);
        Check("Q19 OverlapCollider (list): the colliders overlapping the box, itself excluded (none here)", noc == 0 && oc3.Count == 0, $"{noc}");
        var deep = Static2("Q-deep", new Vector2(X + 20f, 0f)); deep.transform.position = new Vector3(X + 20f, 0f, 5f); var deepc = (Collider2D)Box2(deep, Vector2.one, false);
        Steps(1);
        var dfi = new ContactFilter2D(); dfi.SetLayerMask(mask); dfi.SetDepth(-1f, 1f);
        var dres = new List<RaycastHit2D>();
        int dn = Physics2D.Raycast(new Vector2(X + 15f, 0f), Vector2.right, dfi, dres, 10f);
        var dall = Physics2D.Raycast(new Vector2(X + 15f, 0f), Vector2.right, 10f, mask);
        Check("Q20 a depth filter (ContactFilter2D.SetDepth(-1, 1)): a collider at z 5 is skipped; the legacy ray (-inf, inf) hits it", dn == 0 && dall.collider == deepc, $"filtered {dn}, legacy {dall.collider?.name}");
        deepc.excludeLayers = 1 << 3;
        Check("Q21 Collider2D.excludeLayers set / get", deepc.excludeLayers.value == 1 << 3, $"{deepc.excludeLayers.value}");
        UObject.DestroyImmediate(deep);
        UObject.DestroyImmediate(gbody); UObject.DestroyImmediate(qb); UObject.DestroyImmediate(qb2); UObject.DestroyImmediate(qc); UObject.DestroyImmediate(qt);
        Steps(2);
    }
    static long Hit(int id) => id >= 0 && id < Shepherd.Hooks.Icalls.Hits.Length ? Shepherd.Hooks.Icalls.HitCount(id) : -1;   // (kit #8 merge: kit #6 fps counts in 32-bit counters first; HitCount = both)
    static bool Near(float a, float b, float tol = 1e-4f) => MathF.Abs(a - b) <= tol;
}

/// <summary>physcheck unit2's recorder: every trigger message it hears, with the step (PhysWorld.StepIndex - 1 inside a phase), and an optional
/// hook (the DROPQUEUED scene deactivates a collider from it).</summary>
[ProfileOnly]
public sealed class PhysTrigRec : MonoBehaviour
{
    public static readonly List<(int step, string self, string kind, string other, bool inPhase)> Ev = new();
    public Action<string, string> Hook;
    public static List<(int step, string self, string kind, string other, bool inPhase)> Of(string self) => Ev.Where(e => e.self == self).ToList();
    void Rec(string kind, Collider2D other)
    {
        string o = other != null ? other.name : (ReferenceEquals(other, null) ? "null" : "destroyed");
        bool inPhase = Runtime.Time.InFixedStep && PhysWorld.SlotBInside;
        Ev.Add((PhysWorld.StepIndex - 1, name, kind, o, inPhase));
        Hook?.Invoke(kind, o);
    }
    void OnTriggerEnter2D(Collider2D other) => Rec("enter", other);
    void OnTriggerStay2D(Collider2D other) => Rec("stay", other);
    void OnTriggerExit2D(Collider2D other) => Rec("exit", other);
}

/// <summary>physcheck unit2's timer: runs Do in its At-th FixedUpdate (before that step's physics).</summary>
[ProfileOnly]
public sealed class PhysAt : MonoBehaviour
{
    public int At; public Action Do; int _n;
    void FixedUpdate() { if (++_n == At) Do?.Invoke(); }
}
#endif
