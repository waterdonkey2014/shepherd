using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 2 (physics-plan-v2 section 4 "Stage 2"): the world at SHEPHERD_PHYS=2 -- Stage 1's plus the trigger colliders as
/// sensors in the core, patch P1's world flags (H-TRIGPHASE=final: sensors evaluated after the solve; H-KINTRIG=all-but-static: kinematic
/// triggers -- both E1's, always on at level 2; the flags-off path stays for T1's proof only), contact creation serials (H-CBORDER=serial),
/// H-KINSLEEP=sleep (E1: kinematic bodies sleep -- per solver job under Unity's multithreading, H-MTSLEEP, not implemented), the shape -> collider map the
/// dispatcher (Physics/PhysDispatch.cs) and the queries (Physics/Physics2DQueries.cs) resolve the core's handles with. At level 1 and below
/// nothing here is reached (<see cref="Sim2"/> is false): the world flags are Stage 1's, triggers stay with Runtime/Triggers2D.</summary>
public static partial class PhysWorld
{
    /// <summary>Level 2 or above and the core binds (a level-2 run without the core is Stage 0, as level 1 is).</summary>
    public static readonly bool Sim2 = Sim && PhysConfig.Level >= 2;
    /// <summary>The P1 flags and contact serials of the world at level 2 (0 below: Stage 1's world exactly).</summary>
    static readonly int WorldFlags2 = !Sim2 ? 0 : ShepPhys.WorldContactSerials | ShepPhys.WorldTriggerPhase | ShepPhys.WorldKinematicTriggers;

    public static int StepIndex => _stepIndex;
    internal static ShepPhys Core2 => _up ? _core : null;
    internal static int World2 => _world;

    // ---------------- shape handle -> collider ----------------
    static readonly Dictionary<int, NCollider2D> _shapeOwner = new();
    static readonly List<int> _retired = new();
    /// <summary>The collider a core shape handle belongs to (a shape destroyed since the last phase still resolves until that phase drained its
    /// End events).</summary>
    public static NCollider2D ColliderOfShape(int h) => _shapeOwner.TryGetValue(h, out var c) ? c : null;
    static void Retire(ShapeState ps) { foreach (var h in ps.Handles) _retired.Add(h); }
    /// <summary>After a phase's drain: the destroyed shapes' handles leave the map.</summary>
    internal static void RetireShapes()
    {
        foreach (var h in _retired) _shapeOwner.Remove(h);
        _retired.Clear();
    }
    internal static unsafe bool MirrorAwake(BodyState b) => (_core.MirrorAt(b.Mirror)->Flags & ShepPhys.MfAwake) != 0;

    // ---------------- membership hooks for the dispatcher ----------------
    /// <summary>A GameObject became inactive: its colliders leave the world, their Exits inside the call (E1: H-EXITSYNC=sync).</summary>
    static void LeavingGo(NGameObject go) { foreach (var c in go.Components) if (c is NCollider2D k && k.PS != null) PhysDispatch.Leaving(k); }
    /// <summary>Rigidbody2D.simulated = false: every collider attached to it leaves the world at the next flush -- E1's one deferred case: the Exits
    /// come at the next phase, and simulated set back before it gives no event.</summary>
    internal static void LeavingBody(NRigidbody2D b)
    {
        if (!Sim2 || b.PB == null) return;
        foreach (var s in b.PB.Shapes) if (s.Rec != null) PhysDispatch.Leaving(s.Rec, deferred: true);
    }
    /// <summary>Object.Destroy (not DestroyImmediate) of a GameObject or a 2D collider, at the call (Lifecycle.Destroy, delay 0): Unity takes the
    /// colliders out of the physics world at once -- their Exits inside the Destroy call, the GameObject still active, the collider reading as
    /// disabled (E1's ExitSync lanes 3, 5, 8) -- and destroys the objects at the end of the frame. Here: each collider of the GameObject's subtree
    /// (or the collider) is marked <see cref="NCollider2D.DestroyPending"/> (out of the world at the next flush), its pairs' Exits delivered now.
    /// Level 2 and above only (below, nothing changes: Triggers2D's D5 sends the Exit at the destroy, as before).</summary>
    internal static void DestroyRequested(NObj n)
    {
        if (!Sim2) return;
        switch (n)
        {
            case NCollider2D c: Pend(c); break;
            case NGameObject go when go.Transform is { } t && t.PhysDesc != 0: PendTree(t); break;
        }
        static void PendTree(NTransform t)
        {
            if (t.PhysInterest != 0) foreach (var c in t.Go.Components) if (c is NCollider2D k) Pend(k);
            foreach (var ch in t.Children) if (ch.PhysDesc != 0) PendTree(ch);
        }
        static void Pend(NCollider2D c)
        {
            if (c.Destroyed || c.DestroyPending) return;
            c.DestroyPending = true; DestroyPends++;
            if (c.PS != null) { PhysDispatch.Leaving(c); Queue(c); }
        }
    }
    public static long DestroyPends;
    /// <summary>Collider2D.isTrigger changed on a collider in the world (H-RECREATE).</summary>
    internal static void TriggerFlipped(NCollider2D c) { if (Sim2 && c.PS != null) PhysDispatch.Recreated(c); }

    // ---------------- query helpers (Physics/Physics2DQueries.cs) ----------------
    /// <summary>The collider's core shapes after the autoSync flush (empty when it is not in the world).</summary>
    internal static ReadOnlySpan<int> ShapesOf(NCollider2D c)
    {
        FlushIfNeeded();
        var ps = c?.PS;
        return ps == null || ps.Count == 0 ? ReadOnlySpan<int>.Empty : new ReadOnlySpan<int>(ps.Handles, 0, ps.Count);
    }

    /// <summary>SHEPHERD_TRIGGERS2D=parity (the swap A/B's strict run): a body's collider at the core's pose -- the world 2x3 of its shape, T(x, y)
    /// R(angle) of the body's mirror pose times the body-space affine the shape was built with (R(-theta_b) body2x2 rel, Stage 1's KeyOf).</summary>
    internal static unsafe bool CoreColliderMatrix(NBehaviour b, out Matrix4x4 m)
    {
        m = PG.Identity;   // kit #9 glue math rule
        if (!Sim2 || b is not NCollider2D c || c.PS?.Body is not { } pb || pb.Handle <= 0) return false;
        var mir = _core.MirrorAt(pb.Mirror);
        var k = c.PS.Key;
        float th = _core.Atan2(k.Body2[2], k.Body2[0]), co = _core.Cos(th), si = _core.Sin(th);
        float q00 = co * k.Body2[0] + si * k.Body2[2], q01 = co * k.Body2[1] + si * k.Body2[3];
        float q10 = -si * k.Body2[0] + co * k.Body2[2], q11 = -si * k.Body2[1] + co * k.Body2[3];
        float a00 = q00 * k.Rel[0] + q01 * k.Rel[3], a01 = q00 * k.Rel[1] + q01 * k.Rel[4], a02 = q00 * k.Rel[2] + q01 * k.Rel[5];
        float a10 = q10 * k.Rel[0] + q11 * k.Rel[3], a11 = q10 * k.Rel[1] + q11 * k.Rel[4], a12 = q10 * k.Rel[2] + q11 * k.Rel[5];
        float c2 = _core.Cos(mir->Angle), s2 = _core.Sin(mir->Angle);
        m.m00 = c2 * a00 - s2 * a10; m.m01 = c2 * a01 - s2 * a11; m.m03 = c2 * a02 - s2 * a12 + mir->X;
        m.m10 = s2 * a00 + c2 * a10; m.m11 = s2 * a01 + c2 * a11; m.m13 = s2 * a02 + c2 * a12 + mir->Y;
        return true;
    }
    /// <summary>The body pose the core has for a collider's body, against its transform's: true when they differ (a child body lagging its
    /// parent's write-back by one step, H-CHILDSYNC=follow -- the swap A/B's class 3).</summary>
    internal static unsafe bool BodyLagsTransform(NCollider2D c)
    {
        if (!Sim2 || c.PS?.Body is not { } pb || pb.Handle <= 0 || pb.T == null || pb.T.Parent == null) return false;
        var mir = _core.MirrorAt(pb.Mirror); var p = pb.T.Position;
        return MathF.Abs(mir->X - p.x) > 1e-6f || MathF.Abs(mir->Y - p.y) > 1e-6f;
    }

    /// <summary>SHEPHERD_PHYS_ASTARDUMP=&lt;file&gt; (Stage 2 acceptance #5, the A* intro sanity; owner-local: game-derived geometry): at the end of the run,
    /// the game's own grid graph after the game's own scans (GenerateRoom.SetAStar sized it from the Room Transform composite's bounds, served by the
    /// core from Stage 2 on): width, depth, node size, centre, the collision settings, and every node's position and walkable bit. Read by
    /// reflection from AstarPath.active (fields only, nothing of the game called).</summary>
    internal static void DumpAstar()
    {
        var path = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_ASTARDUMP");
        if (string.IsNullOrEmpty(path) || !Sim2) return;
        try
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var tA = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("AstarPath", false)).FirstOrDefault(t => t != null);
            var active = tA?.GetField("active", F)?.GetValue(null);
            if (active == null) { Log.Info("[phys] A* dump: no AstarPath.active"); return; }
            var data = tA.GetField("data", F).GetValue(active);
            var gg = data.GetType().GetField("<gridGraph>k__BackingField", F)?.GetValue(data) ?? data.GetType().GetProperty("gridGraph", F)?.GetValue(data);
            if (gg == null) { Log.Info("[phys] A* dump: no grid graph"); return; }
            var tg = gg.GetType();
            object G(object o, string f) { for (var t = o.GetType(); t != null; t = t.BaseType) { var fi = t.GetField(f, F); if (fi != null) return fi.GetValue(o); } return null; }
            var col = G(gg, "collision");
            var nodes = G(gg, "nodes") as Array;
            using var w = new StreamWriter(path, false);
            var center = (Vector3)G(gg, "center");
            w.WriteLine($"# A* grid graph at the end of the run (frame {Runtime.Time.FrameCount}): width {G(gg, "width")} depth {G(gg, "depth")} nodeSize {G(gg, "nodeSize")} center {center.x:R},{center.y:R},{center.z:R} " +
                        $"collision: check {G(col, "collisionCheck")} use2D {G(col, "use2D")} type {G(col, "type")} diameter {G(col, "diameter")} mask {((LayerMask)G(col, "mask")).value} finalRadius {G(col, "finalRadius")}");
            int walk = 0, n = 0;
            if (nodes != null)
                foreach (var node in nodes)
                {
                    if (node == null) continue;
                    var pos = G(node, "position"); uint flags = (uint)G(node, "flags");
                    int x = (int)G(pos, "x"), y = (int)G(pos, "y"), z = (int)G(pos, "z");
                    bool ok = (flags & 1) != 0; if (ok) walk++; n++;
                    w.WriteLine($"{x * 0.001f:R}\t{y * 0.001f:R}\t{z * 0.001f:R}\t{(ok ? 1 : 0)}");
                }
            Log.Info($"[phys] A* dump -> {path}: {n} nodes, {walk} walkable");
        }
        catch (Exception e) { Log.Error($"[phys] A* dump failed: {e.GetBaseException().Message}"); }
    }

    /// <summary>SHEPHERD_PHYS_RAYPROBE=1 (Stage 2 acceptance #4, "the guards' line-of-sight rays hit walls"; the walk-wall drive casts none of its
    /// own after the room exists): at the end of the run, from every active unit named "Enemy ..." toward the Lamb, Physics2D.Raycast through the
    /// real rewritten module (the legacy 3-icall path UnitObject's LOS uses) against Island + Obstacles; one log line each (owner-local).</summary>
    internal static void RayProbe()
    {
        if (Environment.GetEnvironmentVariable("SHEPHERD_PHYS_RAYPROBE") != "1" || !Sim2) return;
        var lamb = Objects.All.OfType<NGameObject>().FirstOrDefault(g => !g.Destroyed && g.Name == "Player - Prisoner(Clone)" && g.ActiveInHierarchy);
        if (lamb == null) { Log.Info("[phys] ray probe: no Lamb"); return; }
        var lp = lamb.Transform.Position; int mask = (1 << 8) | (1 << 23);
        foreach (var g in Objects.All.OfType<NGameObject>().Where(g => !g.Destroyed && g.ActiveInHierarchy && g.Name.StartsWith("Enemy ") && g.Transform.Parent != null).OrderBy(g => g.InstanceId))
        {
            var p = g.Transform.Position; var d = new Vector2(lp.x - p.x, lp.y - p.y); float dist = d.magnitude;
            var h = Physics2D.Raycast(new Vector2(p.x, p.y), d, dist, mask);
            Log.Info($"[phys] ray probe: '{g.Name}' ({p.x:R},{p.y:R}) -> the Lamb ({lp.x:R},{lp.y:R}) dist {dist:R}: {(h.collider != null ? $"hit '{h.collider.name}' at {h.distance:R} point ({h.point.x:R},{h.point.y:R}) normal ({h.normal.x:R},{h.normal.y:R})" : "clear")}");
        }
    }

    static string Summary2()
    {
        int sensors = 0, solids = 0;
        foreach (var kv in _shapeOwner) { if (kv.Value.IsTrigger) sensors++; else solids++; }
        return $"[phys] stage 2 world: flags 0x{(1 | 4 | WorldFlags2):x} (P1 trigger phase on (H-TRIGPHASE=final), kinematic triggers on (H-KINTRIG=all-but-static), contact serials on), " +
               $"H-KINSLEEP=sleep; Destroy-time leaves {DestroyPends}; shapes now: {sensors} sensor + {solids} solid fixtures; queries {Physics2DQueries.Summary()}";
    }
}
