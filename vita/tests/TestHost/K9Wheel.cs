using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using EngTime = Shepherd.Engine.Runtime.Time;
namespace Shepherd.TestHost;

/// <summary>Kit #9 engine review fixes (vita/notes/kit9-engine.md "Review fixes"): `k9wheel` -- the timer wheel's heap compaction and the check
/// modes' new compares on the engine itself (no game boot, no pack: engine records made by the test). Run it once per switch setting; it reads
/// the switches the engine read (Runtime/K9Engine.cs) and states what each part must show under them:
/// <list type="bullet">
/// <item>A, paused churn: Time.timeScale 0 and a behaviour that, every frame, cancels and re-adds an Invoke and stops and restarts a
/// WaitForSeconds coroutine (the review's scenario). Items / All hold one live entry in every mode; the wheel's two heaps stay small with the
/// compaction (SHEPHERD_K9_WHEEL_COMPACT on) and grow by one entry a frame without it. Then time runs again: the Invoke fires once and the
/// coroutine steps once in every mode.</item>
/// <item>B, a destroyed behaviour's sleeping Invoke (Destroy(component) does not cancel its timers; kit #8's next tick does): out of Items within
/// a tick (wheel off) / a housekeeping cycle (compaction on); kept until its time without the compaction.</item>
/// <item>C, the checker checked (SHEPHERD_K9_WHEEL=check): the behaviour's coroutine / timer counts are zeroed by hand while it owns a live
/// coroutine and timer -- IsInvoking, CancelInvoke and StopAllCoroutines must each count a difference and answer as kit #8 (true, cancelled,
/// stopped); with the wheel on and no check the same corruption gives the wrong answers (which is what the check is there to catch).</item>
/// <item>D, the find checks (SHEPHERD_K9_FIND=check): one record removed by hand from the per-type list and from the shader records --
/// Resources.FindObjectsOfTypeAll, Object.FindObjectsOfType and Shader.Find must each count a difference and return the scan's answer; with
/// the find row on and no check the index's (wrong) answer comes back.</item>
/// <item>E (review round 2), All's order across the Seq renumbering: 40 coroutines stepping every frame, the oldest stopped and a new one started
/// between frames; every frame's steps in start order (SHEPHERD_K9_SEQ_WRAP=50 makes the renumbering happen every ~10 frames).</item>
/// <item>F (review round 3), the black-frame collection's release (K9House.BeforeCollect, no frame in between): a destroyed component's
/// sleeping Invoke leaves Items there (compaction on) -- kit #8 / no compaction keep it until the next tick / its time; a finished coroutine
/// left in the WaitForEndOfFrame list while All is empty (no pass prunes it) leaves it there. The released behaviour (no Update, so kit #8's
/// per-phase Update cache does not hold it either) is collected by that collection; kit #8 keeps it until its next tick.</item>
/// <item>G (review round 3), the result boxes (SHEPHERD_K9_MEMO): a box handed out is written into in place by hand -- =check counts the
/// difference once 256 more results have passed (and hands out new boxes); with the shared boxes the table never serves that box for a value
/// it no longer holds (its hit test reads the box), and the write itself goes unnoticed (what the check is for).</item>
/// <item>H (review round 3), the find lists' cap on growth: a type's list made at 10 records, then 4,100 more records -- the next find
/// drops the list (the type is scanned from then on); every answer is the scan's in every mode.</item>
/// <item>I (review round 3), the joint end-of-frame classification: lists for a base and a derived type, 4 frames of 150 new records (some
/// destroyed inside the frame, some between frames) -- every find = the live records; =check: the scan's answers element by element.</item>
/// </list>
/// Prints one line per part and "RESULT: PASS" / "RESULT: FAIL"; exit 0 = PASS.</summary>
public static class K9Wheel
{
    static readonly BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int Heap(Type t) => (int)t.GetField("_heapN", Any)!.GetValue(null)!;
    static int Count(Type t, string list) => ((ICollection)t.GetField(list, Any)!.GetValue(null)!).Count;
    static void Frames(int n) { for (int i = 0; i < n; i++) Lifecycle.Frame(); }
    static int _fail;
    static void Expect(bool ok, string what) { if (!ok) { _fail++; Console.WriteLine("  FAIL: " + what); } }

    /// <summary>Part F: a behaviour with a sleeping Invoke (100 s) destroyed as a component (Destroy(component) does not cancel its timers; kit #8's
    /// next tick does), then the black-frame collection's release with no frame in between. A weak reference to it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference DestroyedWithSleepingInvoke(out int before, out int destroyed)
    {
        var v = new GameObject("k9-f").AddComponent<K9Sleeper>(); Frames(2);   // (no Update: kit #8's per-phase Update cache, Lifecycle._phase, would hold it a frame)
        before = Count(typeof(Invokes), "Items");
        v.Invoke("Hide", 100f); Frames(1);
        UnityEngine.Object.Destroy(v); Frames(1);
        destroyed = Count(typeof(Invokes), "Items");
        K9House.BeforeCollect();
        return new WeakReference(v);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string[] args)
    {
        EngTime.CaptureDeltaTime = 1f / 60f;
        bool wheel = K9Engine.Wheel, compact = K9Engine.WheelCompact, wcheck = K9Engine.WheelCheck, find = K9Engine.Find, fcheck = K9Engine.FindCheck;
        Console.WriteLine($"k9wheel: wheel {(wheel ? wcheck ? "check" : "on" : "off")}, compaction {(compact ? "on" : "off")}, find {(find ? fcheck ? "check" : "on" : "off")}");
        Type co = typeof(Coroutines), inv = typeof(Invokes);

        // ---- A: paused churn ----
        var p = new GameObject("k9-pauser").AddComponent<K9Pauser>(); Frames(2);
        EngTime.TimeScale = 0f; p.Churn = true; Frames(1000);
        int hi = Heap(inv), hc = Heap(co), items = Count(inv, "Items"), all = Count(co, "All");
        p.Churn = false; EngTime.TimeScale = 1f; Frames(200);
        Console.WriteLine($"A paused churn, 1000 frames: Invoke heap {hi}, coroutine heap {hc}, Items {items}, All {all}; after 200 running frames: Hide fired {p.Hides}, coroutine stepped {p.Steps}");
        Expect(items == 1 && all == 1, "A: one live Invoke / coroutine");
        if (!wheel) Expect(hi == 0 && hc == 0, "A: wheel off -> heaps unused");
        else if (compact) Expect(hi <= 17 && hc <= 17, "A: compaction -> heaps bounded by one housekeeping cycle");
        else Expect(hi >= 999 && hc >= 999, "A: no compaction -> one dead entry a frame (the review's finding)");
        Expect(p.Hides == 1 && p.Steps == 1, "A: the Invoke fires once, the coroutine steps once");

        // ---- B: a destroyed behaviour's sleeping Invoke ----
        var v = new GameObject("k9-victim").AddComponent<K9Pauser>(); Frames(2);
        int i0 = Count(inv, "Items"), h0 = Heap(inv);
        v.Invoke("Hide", 100f); Frames(1);
        UnityEngine.Object.Destroy(v); Frames(1);
        int iAfter1 = Count(inv, "Items");
        Frames(17);
        int iAfter = Count(inv, "Items"), hAfter = Heap(inv);
        Console.WriteLine($"B destroyed owner: Items {i0} -> {iAfter1} a frame after the destroy -> {iAfter} after 17 more; Invoke heap {h0} -> {hAfter}");
        if (!wheel || compact) Expect(iAfter == i0 && hAfter == h0, "B: the dead timer leaves Items and the heap");
        else Expect(iAfter == i0 + 1, "B: no compaction -> the dead timer waits for its time (100 s)");

        // ---- C: the wheel checker, counts corrupted by hand ----
        var c = new GameObject("k9-check").AddComponent<K9Pauser>(); Frames(2);
        c.StartCoroutine(c.Wait()); c.Invoke("Hide", 0.5f); Frames(1);
        var nm = Objects.Get(c) as NMono; var fCo = typeof(NBehaviour).GetField("CoN", Any)!; var fInv = typeof(NBehaviour).GetField("InvN", Any)!;   // (review round 2: declared in NBehaviour)
        byte con = (byte)fCo.GetValue(nm)!, invn = (byte)fInv.GetValue(nm)!; fCo.SetValue(nm, (byte)0); fInv.SetValue(nm, (byte)0);
        long d0 = K9Engine.Diffs;
        bool isInv = c.IsInvoking("Hide"); c.CancelInvoke("Hide"); c.StopAllCoroutines();
        long dC = K9Engine.Diffs - d0;
        Frames(200);
        Console.WriteLine($"C counts zeroed (were CoN {con}, InvN {invn}): IsInvoking {isInv}, check differences {dC}; after 200 frames Hide fired {c.Hides}, coroutine stepped {c.Steps}");
        if (!wheel || wcheck) Expect(isInv && c.Hides == 0 && c.Steps == 0, "C: kit #8's answers (true, cancelled, stopped)");
        else Expect(!isInv && c.Hides == 1 && c.Steps == 1, "C: wheel without check -> the corrupted count's answers");
        if (wcheck) Expect(dC == 3, "C: three differences counted");

        // ---- D: the find checks, an index entry removed by hand ----
        var all0 = Resources.FindObjectsOfTypeAll<K9Pauser>();   // (builds the per-type list in the modes that keep one)
        var s1 = Shader.Find("K9Test/Wheel");
        if (find)
        {
            var reg = typeof(Registry).GetMethod("ForType", Any)!.Invoke(null, new object[] { typeof(K9Pauser), EngTime.FrameCount }) as Registry;
            reg?.Items().RemoveAt(0);
            Registry.Shaders.Items().Remove(Objects.Get(s1));
        }
        long d1 = K9Engine.Diffs;
        var all1 = Resources.FindObjectsOfTypeAll<K9Pauser>(); var of1 = UnityEngine.Object.FindObjectsOfType<K9Pauser>(true); var s2 = Shader.Find("K9Test/Wheel");
        long dD = K9Engine.Diffs - d1;
        Console.WriteLine($"D one index entry removed: FindObjectsOfTypeAll {all0.Length} -> {all1.Length}, FindObjectsOfType(true) {of1.Length}, Shader.Find same record {ReferenceEquals(s1, s2)}, check differences {dD}");
        if (!find || fcheck) Expect(all1.Length == all0.Length && of1.Length == all0.Length && ReferenceEquals(s1, s2), "D: the scan's answers");
        else Expect(all1.Length == all0.Length - 1 && of1.Length == all0.Length - 1 && !ReferenceEquals(s1, s2), "D: find without check -> the corrupted index's answers");
        if (fcheck) Expect(dD == 3, "D: three differences counted");

        // ---- F (review round 3): the black-frame collection's release, no frame in between ----
        var wr = DestroyedWithSleepingInvoke(out int fI0, out int fI1);
        int fI2 = Count(inv, "Items");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        bool fAlive = wr.IsAlive;
        Frames(1); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        bool fAlive2 = wr.IsAlive;
        var grp = (System.Collections.IList[])co.GetField("_grp", Any)!.GetValue(null)!;
        var eo = new GameObject("k9-eof").AddComponent<K9Orderer>(); Frames(1);
        eo.StartCoroutine(eo.EofThenEnd()); Frames(4);
        int g2a = grp[2].Count, allN = Count(co, "All");
        K9House.BeforeCollect();
        int g2b = grp[2].Count;
        Console.WriteLine($"F before the black-frame collection: Items {fI0} -> {fI1} (destroyed owner's sleeping Invoke) -> {fI2} after BeforeCollect, owner collectable {!fAlive} (a frame later {!fAlive2}); EndOfFrame list {g2a} -> {g2b} (All {allN})");
        if (wheel && compact) Expect(fI2 == fI0 && !fAlive && !fAlive2 && g2b == 0 && g2a == 1 && allN == 0, "F: released before the collection (Items, the group list) and the behaviour collected by it");
        else if (wheel) Expect(fI2 == fI0 + 1 && fAlive && fAlive2 && g2b == g2a, "F: no compaction -> kept (the e2 wheel: until the timer's time)");
        else Expect(fI2 == fI0 + 1 && fAlive && !fAlive2 && g2a == 0, "F: wheel off -> kit #8 (Items holds it until the next tick, then released; no group lists)");

        // ---- G (review round 3): the result boxes' premise ----
        long dG0 = K9Engine.Diffs;
        object gb = K9Memo.Box(new Vector3(1, 2, 3));
        Unsafe.Unbox<Vector3>(gb) = new Vector3(7, 8, 9);   // the fault: a result box written into after it was handed out
        object gAgain = K9Memo.Box(new Vector3(1, 2, 3));
        for (int i = 0; i < 300; i++) K9Memo.Box(new Vector3(i, 0.5f, 0));
        long dG = K9Engine.Diffs - dG0;
        var gv = (Vector3)gAgain;
        Console.WriteLine($"G a result box written in place: the next Box(1,2,3) holds ({gv.x},{gv.y},{gv.z}), same box {ReferenceEquals(gb, gAgain)}; check differences {dG}");
        Expect(gv == new Vector3(1, 2, 3) && !ReferenceEquals(gb, gAgain), "G: the written box is never served for (1,2,3)");
        if (K9Engine.MemoCheck) Expect(dG == 1, "G: =check counts the in-place write once");
        else Expect(dG == 0, "G: no check -> nothing counts it");

        // ---- H (review round 3): the find lists' cap on growth ----
        var few = new List<GameObject>(); for (int i = 0; i < 10; i++) few.Add(new GameObject("k9-many").AddComponent<K9Many>().gameObject);
        Frames(1);
        int hA = UnityEngine.Object.FindObjectsOfType<K9Many>().Length, hl0 = Registry.TypedLists;
        for (int i = 0; i < 4100; i++) few.Add(new GameObject("k9-many").AddComponent<K9Many>().gameObject);
        Frames(1);
        long dH0 = K9Engine.Diffs;
        int hB = UnityEngine.Object.FindObjectsOfType<K9Many>().Length, hl1 = Registry.TypedLists;
        int hC = UnityEngine.Object.FindObjectsOfType<K9Many>().Length, hl2 = Registry.TypedLists;
        long dH = K9Engine.Diffs - dH0;
        foreach (var g in few) UnityEngine.Object.Destroy(g);
        Frames(2);
        int hD = UnityEngine.Object.FindObjectsOfType<K9Many>().Length;
        Console.WriteLine($"H a type's list past the cap: finds {hA} -> {hB} -> {hC} -> {hD} (after destroying them); type lists {hl0} -> {hl1} -> {hl2}; check differences {dH}");
        Expect(hA == 10 && hB == 4110 && hC == 4110 && hD == 0, "H: the scan's answers");
        if (find && !fcheck) Expect(hl1 == hl0 - 1 && hl2 == hl1, "H: the over-cap list dropped at its next use, and not made again");
        if (fcheck) Expect(dH == 0, "H: no difference");
        if (!K9Engine.Phase) Expect(K9Engine.PhaseSlices == 0, "PHASE=0: no housekeeping cycle");

        // ---- I (review round 3): the joint end-of-frame classification -- related types, records bound and destroyed inside one frame ----
        int ib0 = UnityEngine.Object.FindObjectsOfType<K9Base>().Length, id0 = UnityEngine.Object.FindObjectsOfType<K9Derived>().Length;   // (both lists made)
        long dI0 = K9Engine.Diffs; var made = new List<MonoBehaviour>(); int bad2 = 0;
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < 150; i++)
            {
                var g = new GameObject("k9-i");
                made.Add(i % 3 == 0 ? g.AddComponent<K9Derived>() : i % 3 == 1 ? g.AddComponent<K9Base>() : g.AddComponent<K9Many>());
                if (i % 7 == 0) UnityEngine.Object.DestroyImmediate(g);   // destroyed inside the frame: in the bind log, not a member
            }
            Frames(1);
            int lb = 0, ld = 0; foreach (var m in made) if (m != null) { if (m is K9Base) lb++; if (m is K9Derived) ld++; }
            var fb = UnityEngine.Object.FindObjectsOfType<K9Base>(); var fd = UnityEngine.Object.FindObjectsOfType<K9Derived>();
            if (fb.Length != lb + ib0 || fd.Length != ld + id0) bad2++;
            foreach (var m in made.Take(40)) if (m != null) UnityEngine.Object.Destroy(m.gameObject);   // some destroyed between frames
            made.RemoveRange(0, Math.Min(40, made.Count));
        }
        long dI = K9Engine.Diffs - dI0;
        foreach (var m in made) if (m != null) UnityEngine.Object.Destroy(m.gameObject);
        Frames(1);
        Console.WriteLine($"I joint classification (base / derived lists, 4 rounds of 150 records, some destroyed in the frame): {4 - bad2} / 4 rounds' finds = the live counts; check differences {dI}");
        Expect(bad2 == 0, "I: every find = the live records");
        if (fcheck) Expect(dI == 0, "I: the scan's answers, element by element");

        // ---- E: All's order across the Seq renumbering (review round 2: Seq is an int, renumbered before it would wrap) ----
        // 40 coroutines step every frame; between frames the oldest is stopped and a new one started (appended to All). Every frame's steps must
        // come in start order. Run with SHEPHERD_K9_SEQ_WRAP=50 (and the wheel on, no check) the counter is renumbered every ~10 frames.
        var ord = new GameObject("k9-order").AddComponent<K9Orderer>(); Frames(1);
        var live = new List<int>(); var routines = new Dictionary<int, Coroutine>(); int next = 0, bad = 0;
        for (; next < 40; next++) { routines[next] = ord.StartCoroutine(ord.Loop(next)); live.Add(next); }
        long rn0 = K9Engine.WheelRenumbers;
        for (int f = 0; f < 100; f++)
        {
            ord.Log.Clear(); Frames(1);
            if (!ord.Log.SequenceEqual(live) && bad++ < 3) Console.WriteLine($"  E frame {f}: stepped [{string.Join(",", ord.Log.Take(12))}...] expected [{string.Join(",", live.Take(12))}...]");
            ord.StopCoroutine(routines[live[0]]); routines.Remove(live[0]); live.RemoveAt(0);
            routines[next] = ord.StartCoroutine(ord.Loop(next)); live.Add(next); next++;
        }
        long rn = K9Engine.WheelRenumbers - rn0;
        Console.WriteLine($"E start order over 100 frames (40 live, one stopped + one started a frame): {100 - bad} / 100 frames in start order; Seq renumberings {rn}");
        Expect(bad == 0, "E: every frame's coroutine steps in start order");
        if (wheel && !wcheck && Environment.GetEnvironmentVariable("SHEPHERD_K9_SEQ_WRAP") is { Length: > 0 }) Expect(rn >= 5, "E: the low wrap renumbered the order several times");

        Console.WriteLine("[k9] " + (K9Engine.Summary() ?? "engine track off"));
        Console.WriteLine(K9Engine.Describe());
        Console.WriteLine(_fail == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({_fail})");
        return _fail == 0 ? 0 : 1;
    }
}

/// <summary>Part E's behaviour: Loop(id) logs its id at every step (one step a frame). Part F: EofThenEnd waits for the end of a frame, then a
/// frame, then ends (it stays in the WaitForEndOfFrame list after it moved on).</summary>
public sealed class K9Orderer : MonoBehaviour
{
    public readonly List<int> Log = new();
    public IEnumerator Loop(int id) { while (true) { Log.Add(id); yield return null; } }
    public IEnumerator EofThenEnd() { yield return new WaitForEndOfFrame(); yield return null; }
}
/// <summary>Part F's behaviour: a timer target and no Update.</summary>
public sealed class K9Sleeper : MonoBehaviour { void Hide() { } }
/// <summary>Part H's behaviour (many instances).</summary>
public sealed class K9Many : MonoBehaviour { }
/// <summary>Part I's related types.</summary>
public class K9Base : MonoBehaviour { }
public sealed class K9Derived : K9Base { }

/// <summary>The test behaviour: Churn = every frame CancelInvoke + Invoke(2 s) and StopAllCoroutines + StartCoroutine(a 2 s wait).</summary>
public sealed class K9Pauser : MonoBehaviour
{
    public bool Churn; public int Hides, Steps;
    void Update() { if (!Churn) return; CancelInvoke("Hide"); Invoke("Hide", 2f); StopAllCoroutines(); StartCoroutine(Wait()); }
    void Hide() => Hides++;
    public IEnumerator Wait() { yield return new WaitForSeconds(2f); Steps++; }
}
