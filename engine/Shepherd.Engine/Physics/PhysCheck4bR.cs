#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 4 step 4b's review checks (vita/notes/physics-stage4.md section 11): <c>physcheck unit4br</c> at levels 4 and 6.
/// <list type="bullet">
/// <item><b>The body frame</b>: polygon colliders (on the body and on a turned child), Polygons-mode and Outlines-mode composites on bodies that
/// are Y-flipped (0, 180, 0), tilted (30, 0, 45), mirrored (-1, 1), Z-rotated, Y-flipped and Z-rotated, tilted about Y under a (2, 1) scale --
/// the core's fixtures, placed at the core's body pose, must lie on the collider's localToWorld image (vertices within 1e-4; the outlines by a ray
/// from above against the image's top edge). Unity's body and its body-space shapes share one angle, so its world shape at creation is that
/// image whatever the angle is. Before the review the pieces / outlines were built in Unity's frame (angle 2 atan2(qz, qw)) and placed at the
/// core's angle (atan2(m10, m00)) with only a mirror sign: the Y-flipped body's shapes came out point-reflected, the tilted body's turned 4.1
/// degrees.</item>
/// <item><b>Synchronous composites on a moving body</b>: a kinematic body moved and turned by its velocities for 50 steps does not regenerate its
/// runtime composite (nor rebuild its fixtures); a sub-collider moved relative to the body does (Paths4a2 Y); the generation memo stays bounded.</item>
/// <item><b>useDelaunayMesh</b>: counted (and logged once) as built without Unity's Delaunay step.</item>
/// </list>
/// These checks hold with or without the owner-local decomposer (without it, Stage 1's native decomposition places polygons through the core's
/// own affine). No method here names a Unity physics type in its signature.</summary>
public static partial class PhysCheck
{
    static readonly Vector2[] L4r = { new(0, 0), new(2, 0), new(2, 1), new(1, 1), new(1, 3), new(0, 3) };

    static GameObject Body4r(string name, Vector2 at, Vector3 euler, Vector3 scale, bool kinematic = false)
    {
        var g = new GameObject(name); g.transform.position = new Vector3(at.x, at.y, 0f); g.transform.rotation = Quaternion.Euler(euler); g.transform.localScale = scale;
        var rb = g.AddComponent<Rigidbody2D>(); rb.bodyType = kinematic ? RigidbodyType2D.Kinematic : RigidbodyType2D.Static; rb.gravityScale = 0f;
        return g;
    }
    static Component Poly4r(GameObject go, Vector2[] pts, bool used = false)
    {
        var p = go.AddComponent<PolygonCollider2D>(); p.points = pts; p.usedByComposite = used; return p;
    }
    static GameObject Child4r(GameObject parent, string name, Vector2 local, float rotZ)
    {
        var c = new GameObject(name); c.transform.SetParent(parent.transform, false); c.transform.localPosition = new Vector3(local.x, local.y, 0f);
        c.transform.localRotation = Quaternion.Euler(0f, 0f, rotZ);
        return c;
    }
    /// <summary>The localToWorld image of points (x, y) of a GameObject's local space (the collider offset is zero here).</summary>
    static List<Vector2> Image4r(GameObject go, Vector2[] pts)
    {
        var m = go.transform.localToWorldMatrix; var r = new List<Vector2>();
        foreach (var p in pts) r.Add(new Vector2((float)((double)m.m00 * p.x + (double)m.m01 * p.y + m.m03), (float)((double)m.m10 * p.x + (double)m.m11 * p.y + m.m13)));
        return r;
    }
    /// <summary>The world vertices of a collider's polygon fixtures: Box2D's body-space vertices at the core's body pose (the ground body: as they are).</summary>
    static List<Vector2> CoreWorld4r(Component col, GameObject bodyGo)
    {
        float x = 0f, y = 0f, a = 0f;
        if (bodyGo != null && !PhysWorld.CorePose(Objects.Get(bodyGo.GetComponent<Rigidbody2D>()) as NRigidbody2D, out x, out y, out a)) return new List<Vector2>();
        double ca = Math.Cos(a), sa = Math.Sin(a); var r = new List<Vector2>();
        foreach (var p in PhysWorld.ShapePolygons(Col(col))) foreach (var v in p) r.Add(new Vector2((float)(ca * v.x - sa * v.y + x), (float)(sa * v.x + ca * v.y + y)));
        return r;
    }
    static double D4r(Vector2 a, Vector2 b) => Math.Max(Math.Abs((double)a.x - b.x), Math.Abs((double)a.y - b.y));
    /// <summary>Every fixture vertex within tol of an expected vertex and every expected vertex within tol of a fixture vertex (max-norm).</summary>
    static string Match4r(List<Vector2> ours, List<Vector2> expect, double tol, out bool ok)
    {
        double worst = 0; int missing = 0;
        foreach (var o in ours) worst = Math.Max(worst, expect.Count == 0 ? double.MaxValue : expect.Min(e => D4r(o, e)));
        foreach (var e in expect) if (ours.Count == 0 || ours.Min(o => D4r(o, e)) > tol) missing++;
        ok = ours.Count > 0 && worst <= tol && missing == 0;
        string first = ours.Count > 0 ? $"; first fixture vertex ({ours[0].x:R},{ours[0].y:R}), expected near ({expect[0].x:R},{expect[0].y:R})" : "";
        return $"{ours.Count} fixture vertices, worst {worst:E2} from the image, {missing} of {expect.Count} image vertices unmatched{first}";
    }
    /// <summary>The top of a closed polygon on the vertical line x = x0 (the highest edge crossing), NaN if none.</summary>
    static float Top4r(List<Vector2> poly, float x0)
    {
        double best = double.NaN;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector2 a = poly[j], b = poly[i];
            if ((a.x <= x0) == (b.x <= x0) && a.x != x0 && b.x != x0) continue;
            if (a.x == b.x) continue;
            double t = ((double)x0 - a.x) / ((double)b.x - a.x); if (t < 0 || t > 1) continue;
            double yy = a.y + t * ((double)b.y - a.y);
            if (double.IsNaN(best) || yy > best) best = yy;
        }
        return (float)best;
    }

    static void Unit4bReview()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 50f;
        bool dec = DerivedPhysics.Decomposer != null;
        Log.Info($"[physcheck]      unit4br: the owner-local decomposer: {DerivedPhysics.Status}" + (dec ? "" : " -- polygons take Stage 1's native decomposition (the core's own affine)"));
        var made = new List<GameObject>();
        // ---- 1. the body frame: Unity's pieces / outlines mapped into the core's body space (UnityToCoreBody) ----
        var cases = new (string tag, Vector3 euler, Vector3 scale)[]
        {
            ("yflip", new Vector3(0f, 180f, 0f), Vector3.one),
            ("tilt30x_45z", new Vector3(30f, 0f, 45f), Vector3.one),
            ("mirror", Vector3.zero, new Vector3(-1f, 1f, 1f)),
            ("rot30z", new Vector3(0f, 0f, 30f), Vector3.one),
            ("yflip_30z", new Vector3(0f, 180f, 30f), Vector3.one),
            ("tilt40y_20z_scaled", new Vector3(0f, 40f, 20f), new Vector3(2f, 1f, 1f)),
        };
        long mapped0 = PhysWorld.FrameMapped;
        var built = new List<(string tag, GameObject bp, Component pOwn, GameObject child, Component pChild, GameObject bc, Component cp, GameObject cpIn, GameObject bo, Component co, GameObject coIn)>();
        for (int k = 0; k < cases.Length; k++)
        {
            var (tag, eu, sc) = cases[k];
            float x = -100f - 20f * k;   // (near the origin: the world floats' spacing stays below 2e-5, far under the 1e-4 tolerance)
            // (a) a polygon on the body and (b) one on a turned child
            var bp = Body4r($"U4br {tag} poly", new Vector2(x, -100f), eu, sc); made.Add(bp);
            var pOwn = Poly4r(bp, L4r);
            var ch = Child4r(bp, $"U4br {tag} poly child", new Vector2(0.5f, 0.25f), 10f); var pChild = Poly4r(ch, L4r);
            // (c) a Polygons-mode composite (pieces) and (d) an Outlines-mode composite (a chain loop through the shape affine)
            var bc = Body4r($"U4br {tag} compP", new Vector2(x, -90f), eu, sc); made.Add(bc);
            var cp = (CompositeCollider2D)Comp4(bc, true); cp.geometryType = CompositeCollider2D.GeometryType.Polygons;
            var cpIn = Child4r(bc, $"U4br {tag} compP in", new Vector2(0.25f, 0.5f), 0f); Poly4r(cpIn, L4r, used: true);
            cp.GenerateGeometry();
            var bo = Body4r($"U4br {tag} compO", new Vector2(x, -80f), eu, sc); made.Add(bo);
            var co = (CompositeCollider2D)Comp4(bo, true);
            var coIn = Child4r(bo, $"U4br {tag} compO in", new Vector2(0.25f, 0.5f), 0f); Poly4r(coIn, L4r, used: true);
            co.GenerateGeometry();
            built.Add((tag, bp, pOwn, ch, pChild, bc, cp, cpIn, bo, co, coIn));
        }
        PhysWorld.Flush();
        int framePass = 0, frameChecks = 0;
        foreach (var b in built)
        {
            var ours = CoreWorld4r(b.pOwn, b.bp); ours.AddRange(CoreWorld4r(b.pChild, b.bp));
            var exp = Image4r(b.bp, L4r); exp.AddRange(Image4r(b.child, L4r));
            string r = Match4r(ours, exp, 1e-4, out bool ok);
            Check($"U4br {b.tag}: PolygonCollider2D on the body and on a turned child -- the core's fixtures on the localToWorld image", ok, r);
            frameChecks++; if (ok) framePass++;
            var oursP = CoreWorld4r(b.cp, b.bc); var expP = Image4r(b.cpIn, L4r);
            string rp = Match4r(oursP, expP, 1e-4, out bool okp);
            Check($"U4br {b.tag}: a Polygons-mode composite -- its pieces on the image of its input (within Unity's 2.93e-5 corner cut)", okp, rp);
            frameChecks++; if (okp) framePass++;
            // the outline: rays from above at the image of two interior points' x, against the image's top there
            var expO = Image4r(b.coIn, L4r);
            float yTop = expO.Max(v => v.y) + 5f; string ro = ""; bool oko = true;
            foreach (var q in Image4r(b.coIn, new[] { new Vector2(0.5f, 2f), new Vector2(1.5f, 0.5f) }))
            {
                float want = Top4r(expO, q.x), got = RayY4(q.x, yTop, 30f);
                bool hit = !float.IsNaN(got) && MathF.Abs(got - want) <= 1e-3f; oko &= hit;
                ro += $"x {q.x:R}: ray {got:R}, image top {want:R}; ";
            }
            Check($"U4br {b.tag}: an Outlines-mode composite -- the chain loop's top edge where the image's is (rays from above)", oko, ro.TrimEnd(' ', ';'));
            frameChecks++; if (oko) framePass++;
        }
        Log.Info($"[physcheck]      unit4br: the body frame {framePass} / {frameChecks} (decompositions mapped from Unity's body frame to the core's: {PhysWorld.FrameMapped - mapped0}; decomposer {(dec ? "on" : "off")})");
        foreach (var g in made) UObject.DestroyImmediate(g);
        made.Clear();
        Frames(2);

        // ---- 2. a Synchronous composite on a moving body: no regeneration while the body moves and turns; a relative move regenerates ----
        {
            var g = Body4r("U4br sync body", new Vector2(23300f, 0f), Vector3.zero, Vector3.one, kinematic: true); made.Add(g);
            var a = Child4r(g, "U4br sync a", new Vector2(1f, 0f), 0f); var ab = a.AddComponent<BoxCollider2D>(); ab.size = new Vector2(1f, 1f); ab.usedByComposite = true;
            var bch = Child4r(g, "U4br sync b", new Vector2(0f, 1f), 15f); Poly4r(bch, L4r, used: true);
            var cc = g.AddComponent<CompositeCollider2D>();   // (Synchronous: generates at AddComponent)
            PhysWorld.Flush();
            int paths0 = cc.pathCount;
            long gen0 = Composite.Generations, reg0 = PhysWorld.SyncRegens; var ps0 = Col(cc).PS;
            var rb = g.GetComponent<Rigidbody2D>(); rb.velocity = new Vector2(3f, 1f); rb.angularVelocity = 45f;
            var p0 = rb.position; float r0 = rb.rotation;
            Steps(50);
            var p1 = rb.position; float r1 = rb.rotation;
            bool moved = (p1 - p0).magnitude > 1f && MathF.Abs(r1 - r0) > 10f;
            bool still = Composite.Generations == gen0 && PhysWorld.SyncRegens == reg0 && ReferenceEquals(Col(cc).PS, ps0);
            Check("U4br sync: a kinematic body moved and turned by its velocities for 50 steps -- its runtime (Synchronous) composite is not regenerated and its fixtures not rebuilt",
                  paths0 > 0 && moved && still,
                  $"{paths0} path(s); moved {(p1 - p0).magnitude:R} u, turned {r1 - r0:R} deg; generations +{Composite.Generations - gen0}, Synchronous regenerations +{PhysWorld.SyncRegens - reg0}, fixtures {(ReferenceEquals(Col(cc).PS, ps0) ? "kept" : "rebuilt")}");
            rb.velocity = Vector2.zero; rb.angularVelocity = 0f; Steps(1);
            var before = CompPaths4(cc); long reg1 = PhysWorld.SyncRegens;
            a.transform.localPosition = new Vector3(1f, -0.5f, 0f);   // (the box's bottom edge, the outline's lowest, from y -0.5 to -1 in body space)
            Physics2D.SyncTransforms();
            var after = CompPaths4(cc);
            static float MinY(Vector2[][] ps) => ps.Length == 0 ? float.NaN : ps.Min(p => p.Length == 0 ? float.MaxValue : p.Min(v => v.y));
            float yb = MinY(before), ya = MinY(after);
            bool changed = PhysWorld.SyncRegens == reg1 + 1 && after.Length == before.Length && MathF.Abs(yb + 0.5f) <= 1e-3f && MathF.Abs(ya + 1f) <= 1e-3f;
            Check("U4br sync: a sub-collider moved relative to the body regenerates at the transform sync (Paths4a2 Y)", changed,
                  $"Synchronous regenerations +{PhysWorld.SyncRegens - reg1}, paths {before.Length} -> {after.Length}, lowest y {yb:R} -> {ya:R}");
            // the memo is bounded: 100 Manual generations with distinct inputs
            var gm = Body4r("U4br memo", new Vector2(23400f, 0f), Vector3.zero, Vector3.one); made.Add(gm);
            var cm = (CompositeCollider2D)Comp4(gm, true); var am = Child4r(gm, "U4br memo a", Vector2.zero, 0f); Poly4r(am, L4r, used: true);
            int peak = 0;
            for (int i = 0; i < 100; i++) { am.transform.localPosition = new Vector3(0.01f * i, 0f, 0f); cm.GenerateGeometry(); peak = Math.Max(peak, Composite.MemoEntries); }
            Check("U4br memo: 100 generations with distinct inputs keep the generation memo at or below its bound (64 entries)", peak <= 64 && peak > 0, $"peak {peak} entries");
            foreach (var x in made) UObject.DestroyImmediate(x);
            made.Clear();
            Frames(2);
        }

        // ---- 3. useDelaunayMesh: Unity's Delaunay step is not modelled -- counted (and logged once) when the provider decomposes such a collider ----
        {
            var g = new GameObject("U4br delaunay"); g.transform.position = new Vector3(23500f, 0f, 0f); made.Add(g);
            var pc = Poly4r(g, L4r); Col(pc).UseDelaunayMesh = true;   // (its setter is not bound: the record, as a scene would load it)
            long d0 = PhysWorld.DelaunayIgnored;
            PhysWorld.Flush();
            long dn = PhysWorld.DelaunayIgnored - d0;
            Check("U4br useDelaunayMesh: a polygon with the flag is counted as built without Unity's Delaunay step (with the decomposer; Stage 1's path without it)",
                  dec ? dn == 1 : dn == 0, $"counted {dn}, decomposer {(dec ? "on" : "off")}");
            foreach (var x in made) UObject.DestroyImmediate(x);
            made.Clear();
            Frames(2);
        }
    }
}
#endif
