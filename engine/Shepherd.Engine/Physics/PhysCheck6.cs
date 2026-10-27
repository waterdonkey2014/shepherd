#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 6's checks (physics-plan-v2 section 4 "Stage 6" acceptance; tier T2): <c>physcheck unit6</c> -- the island-ground ray
/// through the real rewritten UnityEngine.PhysicsModule (Physics.Raycast / RaycastNonAlloc / the bool form, MeshCollider.sharedMesh, Collider,
/// Box / Sphere colliders) on synthetic geometry; and <c>physcheck room3d:&lt;scene&gt;</c> -- a scene from the owner's install (read only, no
/// game code run, as `room`): its 3D colliders listed, the ground plane's pose and hit heights, the stairs' and ElevatedGround's hit heights
/// against the analytic mesh height (double precision), and 10^6 random +z rays: the fast path against the general path (every ray) and the
/// brute force (every 100th), bit for bit. Synthetic meshes only in unit6 (no game data); room3d's outputs are game-derived and stay in the
/// owner-local log. No method here names a Unity physics type in its signature (IcallRegistry's start-up scan).</summary>
public static partial class PhysCheck
{
    static readonly float W0 = BitConverter.Int32BitsToSingle(0x3F3504F4);   // 0.7071068: the -90 degree X rotation's |x| = w (the GroundCollisionPlane's shape)

    static Mesh U6Plane(int n = 10)
    {
        var v = new Vector3[(n + 1) * (n + 1)];
        var t = new int[6 * n * n];
        for (int j = 0, k = 0; j <= n; j++) for (int i = 0; i <= n; i++) v[k++] = new Vector3(-5f + 10f * i / n, 0f, -5f + 10f * j / n);
        for (int j = 0, k = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + n + 1, c = a + 1, d = b + 1;
                t[k++] = a; t[k++] = b; t[k++] = c; t[k++] = b; t[k++] = d; t[k++] = c;   // +y fronts (Unity's Plane)
            }
        var m = new Mesh { name = "U6 plane" }; m.vertices = v; m.triangles = t; return m;
    }
    static Mesh U6Quad(float z, float hs)   // facing -z
    {
        var m = new Mesh { name = "U6 quad" };
        m.vertices = new[] { new Vector3(-hs, -hs, z), new Vector3(hs, -hs, z), new Vector3(-hs, hs, z), new Vector3(hs, hs, z) };
        m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        return m;
    }
    static Mesh U6Cube()   // Unity's Cube shape: 24 vertices, outward fronts, [-0.5, 0.5]^3
    {
        var v = new List<Vector3>(); var t = new List<int>();
        for (int a = 0; a < 3; a++)
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                int u = (a + 1) % 3, w = (a + 2) % 3; if (sgn < 0) (u, w) = (w, u);
                int b = v.Count;
                for (int c = 0; c < 4; c++) { var p = Vector3.zero; p[a] = 0.5f * sgn; p[u] = (c & 1) != 0 ? 0.5f : -0.5f; p[w] = (c & 2) != 0 ? 0.5f : -0.5f; v.Add(p); }
                t.AddRange(new[] { b, b + 1, b + 2, b + 1, b + 3, b + 2 });
            }
        var m = new Mesh { name = "U6 cube" }; m.vertices = v.ToArray(); m.triangles = t.ToArray(); return m;
    }
    /// <summary>Physics.Raycast with a hit (the game's form), as plain values.</summary>
    static (bool hit, Vector3 p, Vector3 n, float d, int face, int id, bool mesh) Cast(Vector3 o, int mask, int qti = 0, float max = float.PositiveInfinity)
    {
        bool h = UnityEngine.Physics.Raycast(o, Vector3.forward, out var r, max, mask, (QueryTriggerInteraction)qti);
        return (h, r.point, r.normal, r.distance, r.triangleIndex, r.colliderInstanceID, h && r.collider != null && r.collider.gameObject.GetComponent<MeshCollider>() != null);
    }
    static int Id(Component c) => c.GetInstanceID();
    static string F(float f) => $"{f:R} (0x{BitConverter.SingleToInt32Bits(f):x8})";

    static void Unit6()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        Log.Info($"[physcheck]      unit6 under H-RAYPOINT={Hypotheses.Get("RAYPOINT")} H-NONALLOC={Hypotheses.Get("NONALLOC")}; queriesHitTriggers {Phys3D.QueriesHitTriggers}, queriesHitBackfaces {Phys3D.QueriesHitBackfaces}");
        int ground = ~2;   // UnitObject's groundCheckMask: everything but layer 1 (TransparentFX)
        int ox = 20000;    // the scenes sit far from anything else of this run

        // ---- 1: the GroundCollisionPlane's shape: Unity's Plane at scale (100, 1, 100) under a -90 degree X rotation with w = 0.7071068 ----
        var gp = new GameObject("U6-ground") { layer = 17 };
        gp.transform.position = new Vector3(ox, 0f, 0f);
        gp.transform.rotation = new Quaternion(-W0, 0f, 0f, W0);
        gp.transform.localScale = new Vector3(100f, 1f, 100f);
        var gmc = gp.AddComponent<MeshCollider>(); gmc.sharedMesh = U6Plane();
        float k = 2f * (W0 * W0 - 0.5f);   // the plane's z per unit of y (its own float tilt: w^2 - 1/2 is not 0 in float)
        var c400 = Cast(new Vector3(ox + 33.3f, 400f, 0f) + Vector3.back * 10f, ground);
        var c0 = Cast(new Vector3(ox + 33.3f, 0f, 0f) + Vector3.back * 10f, ground);
        var cneg = Cast(new Vector3(ox - 71.7f, -250f, 0f) + Vector3.back * 10f, ground);
        Check("unit6 ground plane: UnitObject's ray (from z - 10 along +z, mask ~2) hits the MeshCollider (RaycastHit.collider, GetComponent<MeshCollider>()), normal (0, 0, -1), a triangle index in [0, 200)",
              c400.hit && c400.id == Id(gmc) && c400.mesh && Mathf.Abs(c400.n.z + 1f) < 1e-6f && c400.face >= 0 && c400.face < 200, $"id {c400.id} vs {Id(gmc)}, normal {c400.n}, face {c400.face}");
        // (H-RAYPOINT=bary, E1's: the point from the triangle, exact to the tilt -- E1 RayPlane: Unity's own hit z at (0, 400) is 4.76837231e-05)
        const float tol = 1e-9f, tol0 = 1e-12f;
        Check($"unit6 ground plane: the hit z is the plane's own tilt (y x 2(w^2 - 1/2) = y x 1.19e-7, within {tol:G2}), not 0: z at y 400 / 0 / -250 = " + $"{F(c400.p.z)} / {F(c0.p.z)} / {F(cneg.p.z)}",
              Mathf.Abs(c400.p.z - 400f * k) <= tol && c400.p.z != 0f && Mathf.Abs(cneg.p.z + 250f * k) <= tol && c0.hit && Mathf.Abs(c0.p.z) <= tol0,
              $"k {k:R}; distances {c400.d:R} / {c0.d:R}; z at y 0 is {(BitConverter.SingleToInt32Bits(c0.p.z) == 0 ? "+0.0" : BitConverter.SingleToInt32Bits(c0.p.z) == unchecked((int)0x80000000) ? "-0.0" : "nonzero")}");

        // ---- 2: the mask: a TransparentFX (layer 1) plane 0.02 above (BiomeConstants' ParticleCollider shape) ----
        var pc = new GameObject("U6-particles") { layer = 1 };
        pc.transform.position = new Vector3(ox, 0f, -0.02f); pc.transform.rotation = new Quaternion(-W0, 0f, 0f, W0); pc.transform.localScale = new Vector3(50f, 1f, 50f);
        var pmc = pc.AddComponent<MeshCollider>(); pmc.sharedMesh = U6Plane();
        var m1 = Cast(new Vector3(ox + 5f, 7f, -10f), ground); var m2 = Cast(new Vector3(ox + 5f, 7f, -10f), -1);
        Check("unit6 mask: groundCheckMask (~2) skips the layer-1 plane and hits the ground; mask -1 hits the layer-1 plane first (the closest)",
              m1.hit && m1.id == Id(gmc) && m2.hit && m2.id == Id(pmc), $"{m1.id} / {m2.id}");

        // ---- 3: ElevatedGround: a Cube mesh (10 x 4 x 0.1) at z -1 over the ground ----
        var eg = new GameObject("U6-elevated");
        eg.transform.position = new Vector3(ox + 100f, 50f, -1f); eg.transform.localScale = new Vector3(10f, 4f, 0.1f);
        var emc = eg.AddComponent<MeshCollider>(); emc.sharedMesh = U6Cube();
        var e1 = Cast(new Vector3(ox + 101f, 50.5f, -10f), ground);
        Check("unit6 ElevatedGround: the cube's -z face is hit first (z -1.05, a MeshCollider)", e1.hit && e1.id == Id(emc) && e1.mesh && Mathf.Abs(e1.p.z + 1.05f) < 1e-6f, $"z {e1.p.z:R}");

        // ---- 4: a BoxCollider over the ground: the closest hit is not a MeshCollider (the game then keeps z) ----
        var bx = new GameObject("U6-box"); bx.transform.position = new Vector3(ox + 140f, 50f, -2f);
        var bc = bx.AddComponent<BoxCollider>(); bc.size = new Vector3(2f, 2f, 2f);
        var b1 = Cast(new Vector3(ox + 140.5f, 50.25f, -10f), ground);
        Check("unit6 BoxCollider: hit first at its -z face (z -3), RaycastHit.collider is the BoxCollider, GetComponent<MeshCollider>() null, triangleIndex -1",
              b1.hit && b1.id == Id(bc) && !b1.mesh && Mathf.Abs(b1.p.z + 3f) < 1e-5f && b1.face == -1, $"z {b1.p.z:R}, face {b1.face}");

        // ---- 5 / 6: a trigger sphere; a ray starting inside it ----
        var sp = new GameObject("U6-trigger"); sp.transform.position = new Vector3(ox + 160f, 50f, -3f);
        var sc = sp.AddComponent<SphereCollider>(); sc.radius = 0.5f; sc.isTrigger = true;
        var t0 = Cast(new Vector3(ox + 160f, 50f, -10f), ground, 0); var t1 = Cast(new Vector3(ox + 160f, 50f, -10f), ground, 1); var t2 = Cast(new Vector3(ox + 160f, 50f, -10f), ground, 2);
        var ti = Cast(new Vector3(ox + 160f, 50f, -3.1f), ground, 0);
        Check("unit6 triggers: UseGlobal (queriesHitTriggers true) and Collide hit the trigger sphere (z -3.5), Ignore passes to the ground",
              t0.hit && t0.id == Id(sc) && Mathf.Abs(t0.p.z + 3.5f) < 1e-5f && t2.id == Id(sc) && t1.hit && t1.id == Id(gmc), $"{t0.id} {t1.id} {t2.id}");
        Check("unit6 a ray starting inside the sphere does not report it (Unity's Physics.Raycast rule): the ground", ti.hit && ti.id == Id(gmc), $"{ti.id}");

        // ---- 7: OnGround's Island-layer ray: no 3D collider on Island -> false, by the layers-present early-out ----
        Span<int> st0 = stackalloc int[16], st1 = stackalloc int[16];
        Phys3D.Core.Scene3DStats(Phys3D.Scene, st0);
        bool island = UnityEngine.Physics.Raycast(new Vector3(ox + 5f, 7f, 0f), Vector3.forward, out _, float.PositiveInfinity, LayerMask.GetMask("Island"));
        Phys3D.Core.Scene3DStats(Phys3D.Scene, st1);
        int expect = Phys3D.CheckRayOn ? 3 : 1;   // (SHEPHERD_CHECK_PHYS=ray3d re-runs every ray on the general path and by brute force: both early-out too)
        Check("unit6 OnGround: Physics.Raycast on the Island layer is false, answered by the layers-present early-out", !island && st1[4] == st0[4] + expect, $"early-outs {st0[4]} -> {st1[4]} (expected +{expect})");

        // ---- 8 / 9: RaycastNonAlloc (two stacked quads over the ground) and the bool form ----
        var qa = new GameObject("U6-quad-a"); qa.transform.position = new Vector3(ox + 200f, 50f, -2f); var qma = qa.AddComponent<MeshCollider>(); qma.sharedMesh = U6Quad(0f, 2f);
        var qb = new GameObject("U6-quad-b"); qb.transform.position = new Vector3(ox + 200f, 50f, -1f); var qmb = qb.AddComponent<MeshCollider>(); qmb.sharedMesh = U6Quad(0f, 2f);
        var one = new RaycastHit[1]; var four = new RaycastHit[4];
        var o8 = new Vector3(ox + 200.5f, 50.5f, -10f);
        int n1 = UnityEngine.Physics.RaycastNonAlloc(o8, Vector3.forward, one, 10f, ground);
        int n4 = UnityEngine.Physics.RaycastNonAlloc(o8, Vector3.forward, four, 20f, ground);
        bool sorted = n4 == 3 && four[0].colliderInstanceID == Id(qma) && four[1].colliderInstanceID == Id(qmb) && four[2].colliderInstanceID == Id(gmc);
        bool closestFirst = Hypotheses.Get("NONALLOC") != "closest" || (n1 == 1 && one[0].colliderInstanceID == Id(qma));
        Check($"unit6 RaycastNonAlloc (10 u, Follower's form): a 1-element array gets 1 hit{(Hypotheses.Get("NONALLOC") == "closest" ? ", the closest" : "")}; a 4-element array (20 u) 3 hits" +
              (Hypotheses.Get("NONALLOC") == "closest" ? " by distance (quad a, quad b, ground)" : " (collider order)"),
              n1 == 1 && closestFirst && n4 == 3 && (Hypotheses.Get("NONALLOC") != "closest" || sorted), $"{n1} / {n4}: {string.Join(", ", four.Take(Math.Max(n4, 0)).Select(h => h.colliderInstanceID))}");
        bool b9a = UnityEngine.Physics.Raycast(o8, Vector3.forward, 20f, ground), b9b = UnityEngine.Physics.Raycast(new Vector3(ox + 200.5f, 50.5f, -10f), Vector3.forward, 5f, ground);
        Check("unit6 the bool form (RaycastTest): true within 20 u, false within 5 u", b9a && !b9b);

        // ---- 10: the lazy sync (autoSyncTransforms): transform moves, enabled, SetActive, Destroy -- no SyncTransforms call ----
        eg.transform.position = new Vector3(ox + 100f, 50f, -1.5f);
        var e2 = Cast(new Vector3(ox + 101f, 50.5f, -10f), ground);
        emc.enabled = false; var e3 = Cast(new Vector3(ox + 101f, 50.5f, -10f), ground);
        emc.enabled = true; var e4 = Cast(new Vector3(ox + 101f, 50.5f, -10f), ground);
        eg.SetActive(false); var e5 = Cast(new Vector3(ox + 101f, 50.5f, -10f), ground);
        eg.SetActive(true); var e6 = Cast(new Vector3(ox + 101f, 50.5f, -10f), ground);
        Check("unit6 lazy sync: a transform move is seen by the next ray (z -1.55); enabled = false / true and SetActive(false / true) take the collider out and back",
              e2.id == Id(emc) && Mathf.Abs(e2.p.z + 1.55f) < 1e-6f && e3.id == Id(gmc) && e4.id == Id(emc) && e5.id == Id(gmc) && e6.id == Id(emc), $"z {e2.p.z:R}; {e3.id} {e4.id} {e5.id} {e6.id}");

        // ---- 11: sharedMesh: cooked at the assignment; edits reach the collider only when assigned again; null: no shape ----
        var am = U6Quad(0f, 2f);
        var ag = new GameObject("U6-assign"); ag.transform.position = new Vector3(ox + 240f, 50f, -2f); var amc = ag.AddComponent<MeshCollider>(); amc.sharedMesh = am;
        var a1 = Cast(new Vector3(ox + 240.5f, 50.5f, -10f), ground);
        var vv = am.vertices; for (int i = 0; i < vv.Length; i++) vv[i].z += 1f; am.vertices = vv;
        var a2 = Cast(new Vector3(ox + 240.5f, 50.5f, -10f), ground);
        amc.sharedMesh = am; var a3 = Cast(new Vector3(ox + 240.5f, 50.5f, -10f), ground);
        amc.sharedMesh = null; var a4 = Cast(new Vector3(ox + 240.5f, 50.5f, -10f), ground);
        Check("unit6 sharedMesh: z -2 as assigned; the Mesh moved by +1 still answers -2 (cooked at assignment); assigned again -1; null: the ground",
              a1.id == Id(amc) && Mathf.Abs(a1.p.z + 2f) < 1e-6f && a2.id == Id(amc) && Mathf.Abs(a2.p.z + 2f) < 1e-6f && a3.id == Id(amc) && Mathf.Abs(a3.p.z + 1f) < 1e-6f && a4.id == Id(gmc),
              $"{a1.p.z:R} {a2.p.z:R} {a3.p.z:R} {a4.id}");

        // ---- 12: a mirrored plane (scale (-100, 1, 100)) is still hit from -z ----
        var ng = new GameObject("U6-mirrored"); ng.transform.position = new Vector3(ox + 600f, 0f, -0.5f); ng.transform.rotation = new Quaternion(-W0, 0f, 0f, W0); ng.transform.localScale = new Vector3(-100f, 1f, 100f);
        var nmc = ng.AddComponent<MeshCollider>(); nmc.sharedMesh = U6Plane();
        var n12 = Cast(new Vector3(ox + 637.5f, -12.25f, -10f), ground);
        Check("unit6 a mirrored (negative-scale) plane: hit from -z (vertex-space culling keeps its front)", n12.hit && n12.id == Id(nmc), $"{n12.id} z {n12.p.z:R}");

        // ---- 13: gravity, the default scene, the project's 3D settings ----
        var g3 = UnityEngine.Physics.gravity;
        string ds = UnityEngine.Physics.defaultPhysicsScene.ToString();
        Check("unit6 Physics.gravity = (0, 0, -9.81) (C17: the PhysicsManager's raw bytes), Physics.defaultPhysicsScene = handle 0", g3 == new Vector3(0f, 0f, -9.81f) && ds == "(0)", $"{g3} {ds}");
        PhysSettings.Load();
        var pm = PhysSettings.PhysicsManager;
        Check("unit6 the 3D settings a game run uses (queriesHitTriggers true, queriesHitBackfaces false, autoSyncTransforms true, gravity (0, 0, -9.81)) equal the install's PhysicsManager",
              pm.Bytes == 272 && pm.QueriesHitTriggers && !pm.QueriesHitBackfaces && pm.AutoSyncTransforms && BitConverter.SingleToInt32Bits(pm.GravityZ) == BitConverter.SingleToInt32Bits(-9.81f) && pm.GravityX == 0f && pm.GravityY == 0f,
              $"{PhysSettings.Manager3DSource}: triggers {pm.QueriesHitTriggers}, backfaces {pm.QueriesHitBackfaces}, autoSync {pm.AutoSyncTransforms}, gravity ({pm.GravityX:R}, {pm.GravityY:R}, {pm.GravityZ:R})");

        // ---- 14: bounds, ClosestPoint ----
        var bb = bc.bounds; var cp = bc.ClosestPoint(new Vector3(ox + 150f, 50f, -2f)); var cs = sc.ClosestPoint(new Vector3(ox + 160f, 60f, -3f));
        Check("unit6 Collider.bounds of the 2 x 2 x 2 box (centre, size 2); ClosestPoint on the box (its +x face) and on the sphere (its +y pole)",
              (bb.center - new Vector3(ox + 140f, 50f, -2f)).sqrMagnitude < 1e-8f && (bb.size - new Vector3(2f, 2f, 2f)).sqrMagnitude < 1e-8f &&
              (cp - new Vector3(ox + 141f, 50f, -2f)).sqrMagnitude < 1e-6f && (cs - new Vector3(ox + 160f, 50.5f, -3f)).sqrMagnitude < 1e-6f, $"{bb} {cp} {cs}");

        // ---- 15: the +z fast path against the general path and the brute force, on this scene through the core (100,000 rays) ----
        var (rays, diffs, hits) = FastVsGeneral(ox - 600f, -600f, ox + 700f, 600f, -10f, 100000, 7u);
        Check("unit6 100,000 random +z rays over the unit scene: the fast path = the general path (every ray) = the brute force (every 100th), bit for bit", diffs == 0 && hits > 1000, $"{rays} rays, {hits} hits, {diffs} differences");

        foreach (var g in new[] { gp, pc, eg, bx, sp, qa, qb, ag, ng }) UObject.DestroyImmediate(g);
        var gone = Cast(new Vector3(ox + 33.3f, 400f, -10f), -1);
        Check("unit6 the scene's colliders destroyed: nothing is hit", !gone.hit);
    }

    /// <summary>n random +z rays over [x0, x1] x [y0, y1] from z0 through the core: the fast path (the game's query) against the general path
    /// (every ray) and the brute force (every 100th), the 44 bytes of each hit compared.</summary>
    static (int rays, int diffs, int hits) FastVsGeneral(float x0, float y0, float x1, float y1, float z0, int n, uint seed)
    {
        Phys3D.Sync();
        var core = Phys3D.Core; int scene = Phys3D.Scene;
        Span<ShepPhys.Ray3Hit> a = stackalloc ShepPhys.Ray3Hit[1], b = stackalloc ShepPhys.Ray3Hit[1];
        int diffs = 0, hits = 0;
        uint s = seed;
        float R() { s = s * 1664525u + 1013904223u; return (s >> 8) * (1f / 16777216f); }
        for (int i = 0; i < n; i++)
        {
            float x = x0 + (x1 - x0) * R(), y = y0 + (y1 - y0) * R();
            var q = new ShepPhys.Ray3Query { Ox = x, Oy = y, Oz = z0, Dz = 1f, MaxDistance = float.PositiveInfinity, LayerMask = 0xFFFFFFFFu, Flags = ShepPhys.R3fTriggers };
            int na = core.Ray3D(scene, q, a);
            foreach (int f in i % 100 == 0 ? new[] { ShepPhys.R3fGeneral, ShepPhys.R3fBrute } : new[] { ShepPhys.R3fGeneral })
            {
                var qg = q; qg.Flags |= f;
                int nb = core.Ray3D(scene, qg, b);
                if (na != nb || (na > 0 && !Same44(a[0], b[0]))) diffs++;
            }
            if (na > 0) hits++;
        }
        return (n, diffs, hits);
    }
    static unsafe bool Same44(in ShepPhys.Ray3Hit a, in ShepPhys.Ray3Hit b)
    {
        fixed (ShepPhys.Ray3Hit* pa = &a) fixed (ShepPhys.Ray3Hit* pb = &b) return new ReadOnlySpan<byte>(pa, 44).SequenceEqual(new ReadOnlySpan<byte>(pb, 44));
    }

    // ---------------- room3d:<scene> ----------------
    static string Path3(NTransform t) { var parts = new List<string>(); for (; t != null; t = t.Parent) parts.Add(t.Go?.Name ?? "?"); parts.Reverse(); return string.Join("/", parts); }
    static string Bits(Vector3 v) => $"({v.x:R}, {v.y:R}, {v.z:R}) [{BitConverter.SingleToInt32Bits(v.x):x8} {BitConverter.SingleToInt32Bits(v.y):x8} {BitConverter.SingleToInt32Bits(v.z):x8}]";
    static string Bits(Quaternion q) => $"({q.x:R}, {q.y:R}, {q.z:R}, {q.w:R}) [{BitConverter.SingleToInt32Bits(q.x):x8} {BitConverter.SingleToInt32Bits(q.y):x8} {BitConverter.SingleToInt32Bits(q.z):x8} {BitConverter.SingleToInt32Bits(q.w):x8}]";

    static void Room3D(string spec)
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        var name = spec.Split(':')[0];
        string key = name.StartsWith("Assets/") ? name : $"Assets/Scenes/{name}.unity";
        Log.Info($"[physcheck]      room3d: loading '{key}' (activate: no -- no Awake / OnEnable / Start / Update; ProBuilder's run-time meshes are therefore absent)");
        Log.Info($"[physcheck]      room3d: {typeof(MeshCollider).Assembly.GetName().Name} loaded before the scene");
        int handle;
        try { handle = Assets.Loader.LoadScene(key, activate: false); }
        catch (Exception e) { Check($"room3d {name}: the scene loads from the install", false, e.Message); return; }
        var cols = Objects.All.OfType<NCollider3D>().Where(c => !c.Destroyed && c.Go != null && c.Go.Scene == handle).ToList();
        if (!Phys3D.EnsureScene()) { Check($"room3d {name}: the 3D scene comes up", false); return; }
        Phys3D.Sync();
        int inScene = cols.Count(c => c.S3 != null);
        Check($"room3d {name}: the scene loads, its 3D colliders registered and synced", cols.Count > 0, $"{cols.Count} 3D colliders, {inScene} in the scene (active, enabled, with a shape)");
        foreach (var c in cols)
        {
            var t = c.Go.Transform; var nm = c.Kind == PhysRecords.Kind3D.Mesh ? Phys3D.MeshOf(c) : null;
            Log.Info($"[physcheck]      room3d collider {c.Kind} '{Path3(t)}' layer {c.Go.Layer} trig {c.IsTrigger} active {c.Go.ActiveInHierarchy} enabled {c.Enabled} in-scene {c.S3 != null}" +
                     (c.Kind == PhysRecords.Kind3D.Mesh ? $" mesh '{nm?.Name ?? "<null>"}' v {nm?.VertexCount ?? 0} t {(c.S3?.Mesh?.Tris ?? 0)}" : $" size ({c.SizeX}, {c.SizeY}, {c.SizeZ}) r {c.Radius} h {c.Height}") +
                     $" pos {Bits(t.Position)} rot {Bits(t.Rotation)} scale {Bits(t.LossyScale)}");
        }
        var core = Phys3D.Core; int scene = Phys3D.Scene;
        Span<ShepPhys.Ray3Hit> h1 = stackalloc ShepPhys.Ray3Hit[1], h2 = stackalloc ShepPhys.Ray3Hit[1];

        // ---- the ground plane (the intro: Game Prefab/Biome Manager/GroundCollisionPlane): its pose and hit heights (bary = E1's; the native `ray` point logged beside it) ----
        foreach (var c in cols.Where(c => c.S3 != null && c.Go.Name == "GroundCollisionPlane"))
        {
            var t = c.Go.Transform;
            for (var p = t; p != null; p = p.Parent) Log.Info($"[physcheck]      room3d chain '{p.Go?.Name}': local pos {Bits(p.LocalPosition)} rot {Bits(p.LocalRotation)} scale {Bits(p.LocalScale)}");
            var lines = new List<string>(); int nonzero = 0;
            foreach (var (x, y) in new[] { (0f, 0f), (-6.5f, 12.25f), (3.1f, 25.8f), (-20f, -14.4f), (40.2f, 60.7f), (0.5f, 300f) })
            {
                var q = new ShepPhys.Ray3Query { Ox = x, Oy = y, Oz = -10f, Dz = 1f, MaxDistance = float.PositiveInfinity, LayerMask = unchecked((uint)~2), Flags = ShepPhys.R3fTriggers };
                int na = core.Ray3D(scene, q, h1); q.Flags |= ShepPhys.R3fPointRay; int nb = core.Ray3D(scene, q, h2);
                if (na > 0 && h1[0].User == c.InstanceId && BitConverter.SingleToInt32Bits(h1[0].Pz) != 0) nonzero++;
                lines.Add($"({x}, {y}): {(na > 0 && h1[0].User == c.InstanceId ? $"bary z {F(h1[0].Pz)}, ray z {F(nb > 0 ? h2[0].Pz : float.NaN)}, t {h1[0].Distance:R}, face {h1[0].Face}" : na > 0 ? $"another collider (#{h1[0].User})" : "no hit")}");
            }
            Log.Info($"[physcheck]      room3d GroundCollisionPlane rays from z -10 (mask ~2): {string.Join("; ", lines)}");
            Check($"room3d {name}: the GroundCollisionPlane is in the scene; a unit's ray meets it at a z of its own float tilt (nonzero away from y = 0)", nonzero > 0,
                  $"{nonzero} of 6 sample points give z != +0.0; the plane's world rotation {Bits(t.Rotation)}");
        }

        // ---- the stairs and ElevatedGround (the base): hit z against the analytic mesh height (double precision) ----
        int checkedRays = 0, others = 0, misses = 0; double worst = 0; string worstAt = "";
        foreach (var c in cols.Where(c => c.S3 != null && c.Kind == PhysRecords.Kind3D.Mesh && (c.Go.Name.Contains("Stairs") || c.Go.Name.Contains("ElevatedGround"))))
        {
            var d = c.S3.Def; var ms = c.S3.Mesh;
            var (mn, mx) = WorldBox(d, ms);
            for (int j = 0; j < 32; j++)
                for (int i = 0; i < 32; i++)
                {
                    float x = mn.x + (mx.x - mn.x) * (i + 0.5f) / 32f, y = mn.y + (mx.y - mn.y) * (j + 0.5f) / 32f;
                    var q = new ShepPhys.Ray3Query { Ox = x, Oy = y, Oz = mn.z - 10f, Dz = 1f, MaxDistance = float.PositiveInfinity, LayerMask = unchecked((uint)~2), Flags = ShepPhys.R3fTriggers };
                    if (core.Ray3D(scene, q, h1) == 0) { misses++; continue; }
                    if (h1[0].User != c.InstanceId) { others++; continue; }
                    double za = AnalyticZ(d, ms, h1[0].Face, x, y);
                    double e = Math.Abs(h1[0].Pz - za);
                    checkedRays++;
                    if (e > worst) { worst = e; worstAt = $"'{Path3(c.Go.Transform)}' ({x:R}, {y:R}): hit z {h1[0].Pz:R}, analytic {za:R}"; }
                }
        }
        if (checkedRays > 0)
            Check($"room3d {name}: stairs and ElevatedGround: the hit z equals the analytic mesh height (double precision, the hit triangle's plane at the ray's x, y) within 1e-6",
                  worst <= 1e-6, $"{checkedRays} rays on those colliders ({others} met another collider first, {misses} none); worst |z - analytic| {worst:E3}{(worstAt.Length > 0 ? " at " + worstAt : "")}");
        else Log.Info($"[physcheck]      room3d {name}: no Stairs / ElevatedGround MeshCollider in the scene");

        // ---- 10^6 random +z rays over the scene's 3D colliders: fast = general (every ray) = brute (every 100th) ----
        // the play area: the union of the colliders smaller than 300 u (the ground / particle planes are 500-1000 u wide and would take the rays),
        // else the whole union clipped to +-200 u; the rays start 10 u in front of the lowest collider
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z0 = float.MaxValue;
        float ax0 = float.MaxValue, ay0 = float.MaxValue, ax1 = float.MinValue, ay1 = float.MinValue;
        foreach (var c in cols.Where(c => c.S3 != null))
        {
            var (mn, mx) = WorldBox(c.S3.Def, c.S3.Mesh);
            z0 = Math.Min(z0, mn.z);
            ax0 = Math.Min(ax0, mn.x); ay0 = Math.Min(ay0, mn.y); ax1 = Math.Max(ax1, mx.x); ay1 = Math.Max(ay1, mx.y);
            if (mx.x - mn.x > 300f || mx.y - mn.y > 300f) continue;
            x0 = Math.Min(x0, mn.x); y0 = Math.Min(y0, mn.y); x1 = Math.Max(x1, mx.x); y1 = Math.Max(y1, mx.y);
        }
        if (!(x1 - x0 >= 1f && y1 - y0 >= 1f)) { x0 = Math.Max(ax0, -200f); y0 = Math.Max(ay0, -200f); x1 = Math.Min(ax1, 200f); y1 = Math.Min(ay1, 200f); }
        if (inScene > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (rays, diffs, hits) = FastVsGeneral(x0, y0, x1, y1, Math.Max(z0, -1e4f) - 10f, 1000000, 99u);
            Check($"room3d {name}: 10^6 random +z rays over [{x0:F1}, {x1:F1}] x [{y0:F1}, {y1:F1}]: the fast path = the general path (every ray) = the brute force (every 100th), bit for bit",
                  diffs == 0, $"{rays} rays, {hits} hits, {diffs} differences ({sw.Elapsed.TotalSeconds:F1} s incl. the comparisons)");
        }
        if (inScene > 0)
        {   // the native cost of one ground ray (MEASURED-M: the managed loop + one call into the core per ray; the icall glue comes on top)
            Span<ShepPhys.Ray3Hit> tb = stackalloc ShepPhys.Ray3Hit[1];
            foreach (var (label, fl) in new[] { ("+z fast path", 0), ("general path", ShepPhys.R3fGeneral) })
            {
                uint sd = 5u; long acc = 0; var sw2 = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 200000; i++)
                {
                    sd = sd * 1664525u + 1013904223u; float x = x0 + (x1 - x0) * ((sd >> 8) * (1f / 16777216f));
                    sd = sd * 1664525u + 1013904223u; float y = y0 + (y1 - y0) * ((sd >> 8) * (1f / 16777216f));
                    var q = new ShepPhys.Ray3Query { Ox = x, Oy = y, Oz = Math.Max(z0, -1e4f) - 10f, Dz = 1f, MaxDistance = float.PositiveInfinity, LayerMask = unchecked((uint)~2), Flags = ShepPhys.R3fTriggers | fl };
                    acc += core.Ray3D(scene, q, tb);
                }
                Log.Info($"[physcheck]      room3d {name}: {label}: 200,000 ground rays (mask ~2) in {sw2.Elapsed.TotalMilliseconds:F1} ms = {sw2.Elapsed.TotalMilliseconds * 1000.0 / 200000:F3} us per ray (MEASURED-M, incl. the managed loop and the call; {acc} hits)");
            }
        }
        Log.Info(Phys3D.Summary());
    }
    /// <summary>A collider's world box from its synced definition (a mesh: its vertices through the pose and scale; a primitive: a generous box).</summary>
    static (Vector3 mn, Vector3 mx) WorldBox(in ShepPhys.Col3Def d, Phys3D.MeshState ms)
    {
        var q = new Quaternion(d.Qx, d.Qy, d.Qz, d.Qw); var p = new Vector3(d.Px, d.Py, d.Pz); var s = new Vector3(d.Sx, d.Sy, d.Sz);
        if (d.Kind == ShepPhys.C3Mesh && ms != null)
        {
            Vector3 mn = Vector3.positiveInfinity, mx = Vector3.negativeInfinity;
            foreach (var v in ms.Src.Pos) { var w = q * Vector3.Scale(v, s) + p; mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w); }
            return (mn, mx);
        }
        float r = Math.Max(Math.Max(Math.Abs(d.Ex), Math.Abs(d.Ey)), Math.Abs(d.Ez)) * Math.Max(Math.Max(Math.Abs(s.x), Math.Abs(s.y)), Math.Abs(s.z));
        var cc = q * Vector3.Scale(new Vector3(d.Cx, d.Cy, d.Cz), s) + p;
        return (cc - new Vector3(r, r, r), cc + new Vector3(r, r, r));
    }
    /// <summary>The analytic height: the hit triangle (the cooked order: every Triangles submesh concatenated) through the collider's pose and
    /// scale in double precision, intersected with the vertical line at (x, y).</summary>
    static double AnalyticZ(in ShepPhys.Col3Def din, Phys3D.MeshState ms, int face, double x, double y)
    {
        var d = din;   // (a copy: the local function below captures it)
        var m = ms.Src; int at = 0; int[] idx = null; int k = 0;
        for (int sub = 0; sub < m.Sub.Count && idx == null; sub++)
        {
            if (sub < m.SubTopology.Count && m.SubTopology[sub] != 0) continue;
            int n = m.Sub[sub].Length / 3;
            if (face < at + n) { idx = m.Sub[sub]; k = (face - at) * 3; }
            at += n;
        }
        if (idx == null) return double.NaN;
        double qx = d.Qx, qy = d.Qy, qz = d.Qz, qw = d.Qw;
        double[] W(Vector3 v)
        {
            // PhysX's PxQuat::rotate in double, on the quaternion as stored: 2 (w^2 - 1/2) v + 2 w (q x v) + 2 (q . v) q. (The Rodrigues form
            // v + 2w (q x v) + 2 q x (q x v) agrees only for |q| = 1; the stored quaternions are unit to ~1e-7, which moves a 15 u point ~3e-6:
            // the geometry PhysX builds is this form's.)
            double vx = 2.0 * (v.x * (double)d.Sx), vy = 2.0 * (v.y * (double)d.Sy), vz = 2.0 * (v.z * (double)d.Sz);
            double w2 = qw * qw - 0.5, dot2 = qx * vx + qy * vy + qz * vz;
            return new[] { vx * w2 + (qy * vz - qz * vy) * qw + qx * dot2 + d.Px, vy * w2 + (qz * vx - qx * vz) * qw + qy * dot2 + d.Py, vz * w2 + (qx * vy - qy * vx) * qw + qz * dot2 + d.Pz };
        }
        var a = W(m.Pos[idx[k]]); var b = W(m.Pos[idx[k + 1]]); var c = W(m.Pos[idx[k + 2]]);
        double e1x = b[0] - a[0], e1y = b[1] - a[1], e1z = b[2] - a[2], e2x = c[0] - a[0], e2y = c[1] - a[1], e2z = c[2] - a[2];
        double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
        return a[2] - (nx * (x - a[0]) + ny * (y - a[1])) / nz;
    }
}
#endif
