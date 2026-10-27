#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 2's swap A/B (physics-plan-v2 section 4 "Stage 2" acceptance #1): with SHEPHERD_TRIGGERS2D=shadow (or =parity) at
/// SHEPHERD_PHYS=2, every phase runs Runtime/Triggers2D's own pair search beside the core on the same post-step world -- recording, never
/// sending -- and compares the two, pair by pair and step by step. Comparing in one process (rather than two runs) keeps both sides on the
/// same game state: the run diverges from a level-1 run as soon as the queries answer, so two separate runs could not be compared step by
/// step after that.
/// <para>The comparison is on the touching state (which collider pairs overlap after the step: the events are its transitions) and on the
/// events themselves. Every state difference falls into one of the plan's classes, each counted and logged, or it is UNCLASSIFIED (a
/// failure):</para>
/// <list type="number">
/// <item>new shapes: a polygon / edge / composite collider, or a capsule (Triggers2D tests none of them, or a capsule as its box);</item>
/// <item>the order key (the same events in another order: Triggers2D's discovery order vs H-CBORDER);</item>
/// <item>child bodies (H-CHILDSYNC=follow): a collider whose body's core pose lags its transform (the parent's write-back moved it);</item>
/// <item>polygon skin (H-SLOP): a box pair touching in the core within Box2D's 0.01 skin per box (the exact gap in (0, 0.01 n]);</item>
/// <item>Static-type bodies (Triggers2D already treats a Static-type body as not moving: expected 0);</item>
/// <item>exact-touch ties of circle pairs (an exact gap of 0 at the tolerance).</item>
/// </list>
/// Two more classes this engine shows, logged apart: "destroy exit timing" (Triggers2D's D5 sends a destroyed collider's Exit at the
/// destroy, the core at the next phase: H-EXITSYNC=deferred; recorded at the next phase here, so they match), and "layer filter"
/// (includeLayers / excludeLayers, which only the core applies). The parity run (Triggers2D with the core's poses for body-owned colliders
/// and Box2D's skin on boxes) must leave only classes 1, 2 and 6.</summary>
[ProfileOnly]   // log text only
public static class PhysShadow
{
    public static long Steps, StateDiffs, EventDiffSteps, OrderOnlySteps, Unclassified, EventsCore, EventsT2;
    public static readonly long[] Classes = new long[9];   // 1..6 the plan's, 7 include / exclude layer filter, 8 sleeping bodies
    static readonly string[] ClassNames = { "", "new shapes", "order key", "child bodies (H-CHILDSYNC)", "polygon skin (H-SLOP)", "Static-type bodies", "exact-touch ties",
                                            "include/exclude layers", "sleeping bodies (the awake rule)" };
    static readonly StreamWriter _log = Open();
    static StreamWriter Open()
    {
        var p = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_SHADOWLOG");
        if (string.IsNullOrEmpty(p)) return null;
        try { return new StreamWriter(p, false) { AutoFlush = false }; } catch { return null; }
    }
    static readonly List<(NBehaviour, NBehaviour)> _t2State = new();
    static readonly HashSet<(int, int)> _core = new(), _t2 = new();
    static readonly Dictionary<(int, int), (NCollider2D, NCollider2D)> _who = new();
    static (int, int) K(NBehaviour a, NBehaviour b) => a.InstanceId < b.InstanceId ? (a.InstanceId, b.InstanceId) : (b.InstanceId, a.InstanceId);

    /// <summary>One phase: the core's events (in delivery order) against Triggers2D's, and the touching states.</summary>
    public static void Compare(List<(NCollider2D a, NCollider2D b, int kind)> coreEvents, int step)
    {
        Steps++;
        var t2Events = Triggers2D.ShadowStep(_t2State);
        EventsCore += coreEvents.Count; EventsT2 += t2Events.Count;
        _core.Clear(); _t2.Clear(); _who.Clear();
        foreach (var p in PhysDispatch.Pairs) if (p.Sensor && p.Count > 0) { var k = K(p.A, p.B); _core.Add(k); _who[k] = (p.A, p.B); }
        foreach (var (a, b) in _t2State) { var k = K(a, b); _t2.Add(k); if (!_who.ContainsKey(k) && a is NCollider2D ca && b is NCollider2D cb) _who[k] = (ca, cb); }
        int diffs = 0;
        foreach (var k in _core) if (!_t2.Contains(k)) { diffs++; Classify(k, true, step); }
        foreach (var k in _t2) if (!_core.Contains(k)) { diffs++; Classify(k, false, step); }
        StateDiffs += diffs;
        // the events: as sets of (pair, kind), and their order when the sets agree
        var ce = coreEvents.Select(e => (K(e.a, e.b), e.kind)).ToList();
        var te = t2Events.Select(e => (K(e.Item1, e.Item2), e.Item3)).ToList();
        bool sameSet = ce.Count == te.Count && new HashSet<((int, int), int)>(ce).SetEquals(te);
        if (!sameSet) EventDiffSteps++;
        // class 2, the order key: the events both sides deliver, each side's order of them
        var common = new HashSet<((int, int), int)>(ce); common.IntersectWith(te);
        if (common.Count > 1 && !ce.Where(common.Contains).SequenceEqual(te.Where(common.Contains))) { OrderOnlySteps++; Classes[2]++; if (OrderOnlySteps <= 20) Log($"{step}	order key	common {common.Count}	core: {string.Join(" ", ce.Where(common.Contains).Select(e => $"{e.Item1.Item1}x{e.Item1.Item2}:{e.Item2}"))}	T2: {string.Join(" ", te.Where(common.Contains).Select(e => $"{e.Item1.Item1}x{e.Item1.Item2}:{e.Item2}"))}"); }
    }

    static unsafe void Classify((int, int) k, bool coreOnly, int step)
    {
        if (!_who.TryGetValue(k, out var w)) { Unclassified++; Log($"{step}\tUNCLASSIFIED\t{(coreOnly ? "core" : "t2")}-only\t#{k.Item1} x #{k.Item2} (records gone)"); return; }
        var (a, b) = w;
        int cls = 0; string why = "";
        bool Shape(NCollider2D c) => c.Kind is not (PhysRecords.Kind2D.Circle or PhysRecords.Kind2D.Box);
        if (Shape(a) || Shape(b)) cls = 1;
        else if ((a.IncludeLayers | a.ExcludeLayers | b.IncludeLayers | b.ExcludeLayers) != 0) cls = 7;
        else if (StaticType(a) || StaticType(b)) cls = 5;
        else if (PhysWorld.BodyLagsTransform(a) || PhysWorld.BodyLagsTransform(b)) cls = 3;
        else if (!PhysDispatch.PairAwake(a, b)) cls = 8;   // Box2D's awake rule (P1's final pass shares it): a pair whose non-static bodies all sleep is not re-evaluated
        else
        {
            float gap = Gap(a, b);
            int boxes = (a.Kind == PhysRecords.Kind2D.Box ? 1 : 0) + (b.Kind == PhysRecords.Kind2D.Box ? 1 : 0);
            why = $"exact gap {gap:R}";
            if (boxes == 0 && MathF.Abs(gap) <= 2e-5f) cls = 6;
            else if (boxes > 0 && coreOnly && gap > 0f && gap <= 0.01f * boxes + 2e-5f) cls = 4;
            else if (boxes > 0 && !coreOnly && MathF.Abs(gap) <= 2e-5f) cls = 4;   // a tie at the box's exact edge (Triggers2D's <=, Box2D's strict test)
        }
        if (cls == 0) Unclassified++; else Classes[cls]++;
        Log($"{step}\t{(cls == 0 ? "UNCLASSIFIED" : ClassNames[cls])}\t{(coreOnly ? "core" : "t2")}-only\t{PhysDispatch.Desc(a)} x {PhysDispatch.Desc(b)}\t{why}");
    }
    static bool StaticType(NCollider2D c) => PhysWorld.AttachedRecord(c) is { } r && r.BodyType == 2;
    static float Gap(NCollider2D a, NCollider2D b)
    {
        var core = PhysWorld.Core2; var sa = PhysWorld.ShapesOf(a); var sb = PhysWorld.ShapesOf(b);
        if (core == null || sa.IsEmpty || sb.IsEmpty) return float.NaN;
        return core.UDistance(PhysWorld.World2, sa, sb, out var d) > 0 ? d : float.NaN;
    }
    static void Log(string s) => _log?.WriteLine(s);
    public static void Flush() => _log?.Flush();

    public static string Summary()
    {
        Flush();
        var cls = string.Join(", ", Enumerable.Range(1, 8).Select(i => $"{i} {ClassNames[i]} {Classes[i]}"));
        return $"[phys] swap A/B ({(Triggers2D.Parity ? "PARITY: core poses for body colliders, Box2D's skin on boxes" : "shadow: Triggers2D as at Stage 1")}): {Steps} steps, " +
               $"events core {EventsCore} / Triggers2D {EventsT2}; state differences {StateDiffs} -- {cls}; UNCLASSIFIED {Unclassified}; steps whose event sets differ {EventDiffSteps}, " +
               $"steps whose common events come in another order {OrderOnlySteps}";
    }
}
#endif
