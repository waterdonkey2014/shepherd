#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 4 step 4a's checks (physics-plan-v2 section 4 "Stage 4"; tier T2): <c>physcheck unit4</c> -- the path icalls through the
/// real rewritten UnityEngine.Physics2DModule (PolygonCollider2D / EdgeCollider2D / CompositeCollider2D) against the numbers the Unity 2022.3 oracle
/// printed for the same primitive scenes (physics-work/e1, scenes Paths4a / Paths4a2: a macOS Mono player with CotL's settings): the getters' and
/// setters' contracts, the stored points, exceptions, the shapes queries see, Unity's chain vertex filter, the composite's path space and offset,
/// Synchronous composites, the refused usedByComposite. The composite pipeline's own vertices (Unity cuts each corner 2.93e-5 along an edge) and
/// its path / start-vertex order are Stage 4b's (H-STARTVTX): compared here as counts, direction and a Hausdorff distance <= 1e-4.
/// No method here names a Unity physics type in its signature.</summary>
public static partial class PhysCheck
{
    static Vector2[] Sq4(float x0, float y0, float x1, float y1) => new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };
    static bool Same4(Vector2[] a, Vector2[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (BitConverter.SingleToInt32Bits(a[i].x) != BitConverter.SingleToInt32Bits(b[i].x) || BitConverter.SingleToInt32Bits(a[i].y) != BitConverter.SingleToInt32Bits(b[i].y)) return false;
        return true;
    }
    static string P4(Vector2[] a) => a == null ? "null" : $"{a.Length}: " + string.Join(" ", a.Select(v => $"({v.x:R},{v.y:R})"));
    static string Thrown4(Action f) { try { f(); return "none"; } catch (Exception e) { return e.GetType().Name; } }
    static float[][] Fl4(params Vector2[][] ps) => ps.Select(p => p.SelectMany(v => new[] { v.x, v.y }).ToArray()).ToArray();
    static Vector2[][] CompPaths4(Component comp)
    {
        var cc = (CompositeCollider2D)comp;   // (the physics type only inside the body: IcallRegistry's scan reads signatures)
        int n = cc.pathCount; var r = new Vector2[n][];
        for (int i = 0; i < n; i++) { var a = new Vector2[cc.GetPathPointCount(i)]; cc.GetPath(i, a); r[i] = a; }
        return r;
    }
    static string CompareUnity4(Vector2[][] ours, Vector2[][] unity, out bool pass)
    {
        var s = Composite.Compare(Fl4(ours), Fl4(unity), out double w, out bool counts, out bool dir);
        pass = counts && dir && w <= 1e-4;
        return s;
    }
    static GameObject Body4(string name, Vector2 at, Vector3 scale, float rotZ = 0f)
    {
        var g = new GameObject(name); g.transform.position = new Vector3(at.x, at.y, 0f); g.transform.localScale = scale; g.transform.rotation = Quaternion.Euler(0f, 0f, rotZ);
        g.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Static;
        return g;
    }
    static Component Sub4(GameObject body, string name, Vector2 local, Vector2[] pts, bool used = true)
    {
        var c = new GameObject(name); c.transform.SetParent(body.transform, false); c.transform.localPosition = new Vector3(local.x, local.y, 0f);
        var p = c.AddComponent<PolygonCollider2D>(); p.points = pts; p.usedByComposite = used;
        return p;
    }
    static Component Comp4(GameObject g, bool manual)
    {
        var cc = g.AddComponent<CompositeCollider2D>();
        if (manual) cc.generationType = CompositeCollider2D.GenerationType.Manual;
        return cc;
    }
    static float RayY4(float x, float y0, float dist = 2f)
    {
        var h = Physics2D.Raycast(new Vector2(x, y0), Vector2.down, dist);
        return h.collider != null ? h.point.y : float.NaN;
    }

    static void Unit4()
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 50f;
        // ================= PolygonCollider2D (Paths4a P1-P21, Paths4a2 P_shrink_grow) =================
        {
            var p = new GameObject("U4 P1").AddComponent<PolygonCollider2D>(); p.transform.position = new Vector3(20000f, 0f, 0f);
            var sq = Sq4(0, 0, 2, 1);
            p.points = sq;
            Check("U4 P2 points = a square: one path, points / GetPath(0) bit for bit, GetTotalPointCount 4",
                  p.pathCount == 1 && Same4(p.points, sq) && Same4(p.GetPath(0), sq) && p.GetTotalPointCount() == 4, $"{p.pathCount} paths, {P4(p.points)}");
            p.pathCount = 3;
            Check("U4 P3 pathCount = 3: paths 1 and 2 empty, path 0 kept, total 4", p.pathCount == 3 && p.GetPath(1).Length == 0 && p.GetPath(2).Length == 0 && Same4(p.points, sq) && p.GetTotalPointCount() == 4);
            var tri = new[] { new Vector2(3, 0), new Vector2(4, 0), new Vector2(3.5f, 1) };
            p.SetPath(1, tri); p.SetPath(2, Sq4(5, 0, 6, 1));
            Check("U4 P4 SetPath(1 / 2): total 11, path 1 = the triangle", p.GetTotalPointCount() == 11 && Same4(p.GetPath(1), tri));
            p.points = Sq4(0, 0, 1, 1);
            Check("U4 P5 points = with 3 paths: one path left (Unity: pathCount 1)", p.pathCount == 1 && Same4(p.points, Sq4(0, 0, 1, 1)));
            p.pathCount = 2; p.SetPath(1, Sq4(3, 0, 4, 1));
            string e6 = Thrown4(() => p.SetPath(5, Sq4(7, 0, 8, 1)));
            Check("U4 P6 SetPath(5) on 2 paths: nothing, no exception (Unity logs 'Failed setting path. Index is out of bounds.')", e6 == "none" && p.pathCount == 2 && Same4(p.GetPath(1), Sq4(3, 0, 4, 1)), e6);
            p.pathCount = 1;
            Check("U4 P7 pathCount = 1: GetPath(1) throws ArgumentOutOfRangeException (the wrapper)", p.pathCount == 1 && Thrown4(() => p.GetPath(1)) == "ArgumentOutOfRangeException");
            p.pathCount = 0;
            bool z0 = p.points.Length == 0 && p.GetTotalPointCount() == 0;
            p.pathCount = 1;
            Check("U4 P8 pathCount = 0: points is an empty array, total 0; back to 1: path 0 empty", z0 && p.pathCount == 1 && p.GetPath(0).Length == 0);
            p.points = sq; p.points = null; bool n1 = Same4(p.points, sq);
            p.points = new Vector2[0]; bool n2 = Same4(p.points, sq);
            p.SetPath(0, new Vector2[0]); bool n3 = Same4(p.points, sq);
            p.SetPath(0, new List<Vector2>()); bool n4 = p.pathCount == 1 && p.GetPath(0).Length == 0;
            Check("U4 P9 / P10b points = null or empty: nothing; SetPath(0, empty array): nothing; SetPath(0, empty list): the path emptied", n1 && n2 && n3 && n4, $"{n1} {n2} {n3} {n4}");
            string t1 = Thrown4(() => p.SetPath(0, (Vector2[])null)), t2 = Thrown4(() => p.SetPath(0, (List<Vector2>)null)), t3 = Thrown4(() => p.GetPath(0, (List<Vector2>)null)),
                   t4 = Thrown4(() => p.SetPath(-1, sq)), t5 = Thrown4(() => p.GetPath(-1));
            Check("U4 P9 SetPath(null array / list), GetPath(i, null list): ArgumentNullException; SetPath(-1) / GetPath(-1): ArgumentOutOfRangeException",
                  t1 == "ArgumentNullException" && t2 == "ArgumentNullException" && t3 == "ArgumentNullException" && t4 == "ArgumentOutOfRangeException" && t5 == "ArgumentOutOfRangeException", $"{t1} {t2} {t3} {t4} {t5}");
            var two = new[] { new Vector2(0, 0), new Vector2(1, 0) }; p.points = two;
            bool s2 = p.pathCount == 1 && Same4(p.points, two) && PhysWorld.ShapesOf(Col(p)).IsEmpty;
            p.points = new[] { new Vector2(0, 0) }; bool s1 = p.GetTotalPointCount() == 1;
            Check("U4 P10 points of 2 / 1 point: stored as given, no shape", s2 && s1);
            var raw = new[] { new Vector2(0, 0), new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 0.001f), new Vector2(2, 1), new Vector2(0, 1), new Vector2(0, 0) };
            p.points = raw;
            Check("U4 P11 duplicates, a 0.001 step and the closing point stored raw (7 points)", Same4(p.points, raw) && p.GetTotalPointCount() == 7);
            var src = Sq4(0, 0, 1, 1); p.points = src; src[0] = new Vector2(-5, -5); var got = p.points; got[1] = new Vector2(9, 9);
            Check("U4 P15 set copies, get copies (new arrays every call)", Same4(p.points, Sq4(0, 0, 1, 1)) && !ReferenceEquals(p.GetPath(0), p.GetPath(0)) && !ReferenceEquals(p.points, p.points));
            var exact = new[] { new Vector2(0.1f, 0.2f), new Vector2(1.0000001f, 0.3f), new Vector2(0.7f, 1.3333334f), new Vector2(-0.0f, 1e-8f) };
            p.points = exact;
            Check("U4 P17 the floats kept bit for bit (-0.0 included)", Same4(p.points, exact), P4(p.points));
            var l = new List<Vector2> { Vector2.one, Vector2.one, Vector2.one, Vector2.one, Vector2.one, Vector2.one };
            p.SetPath(0, new List<Vector2>(Sq4(0, 0, 1.5f, 0.5f)));
            int r14 = p.GetPath(0, l);
            Check("U4 P13 / P14 SetPath(List); GetPath(0, a prefilled list): cleared, 4 points, returns 4", r14 == 4 && l.Count == 4 && Same4(l.ToArray(), Sq4(0, 0, 1.5f, 0.5f)));
            p.pathCount = 3; p.SetPath(1, Sq4(2, 0, 3, 1)); p.SetPath(2, Sq4(4, 0, 5, 1)); p.pathCount = 2; p.pathCount = 3;
            Check("U4 P_shrink_grow 3 -> 2 -> 3: path 2 comes back empty", p.pathCount == 3 && p.GetPath(2).Length == 0 && Same4(p.GetPath(1), Sq4(2, 0, 3, 1)));
            // P21: the shape follows SetPath at once for queries
            var q = new GameObject("U4 P21").AddComponent<PolygonCollider2D>(); q.transform.position = new Vector3(20000f, 60f, 0f);
            q.points = Sq4(0, 0, 1, 1);
            var before = Physics2D.OverlapPoint(new Vector2(20002.5f, 60.5f));
            q.SetPath(0, Sq4(0, 0, 3, 1));
            var after = Physics2D.OverlapPoint(new Vector2(20002.5f, 60.5f));
            q.pathCount = 2; q.SetPath(1, Sq4(5, 0, 6, 1));
            var p1 = Physics2D.OverlapPoint(new Vector2(20005.5f, 60.5f));
            q.pathCount = 1;
            var p1s = Physics2D.OverlapPoint(new Vector2(20005.5f, 60.5f));
            Check("U4 P21 OverlapPoint sees SetPath, a new path and a shrink at once", before == null && after == q && p1 == q && p1s == null);
        }
        // ================= EdgeCollider2D (Paths4a E1-E11, Paths4a2's filter) =================
        {
            var e = new GameObject("U4 E1").AddComponent<EdgeCollider2D>(); e.transform.position = new Vector3(21000f, 0f, 0f);
            Check("U4 E1 new edge: (-0.5, 0) (0.5, 0), pointCount 2, edgeCount 1", Same4(e.points, new[] { new Vector2(-0.5f, 0), new Vector2(0.5f, 0) }) && e.pointCount == 2 && e.edgeCount == 1);
            var three = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(2, 1) };
            e.points = three;
            bool k2 = e.pointCount == 3 && e.edgeCount == 2 && Same4(e.points, three);
            string x3 = Thrown4(() => e.points = new[] { new Vector2(5, 5) }), x4 = Thrown4(() => e.points = new Vector2[0]), x5 = Thrown4(() => e.points = null);
            Check("U4 E2-E5 three points stored; 1 point, none or null: nothing, no exception", k2 && Same4(e.points, three) && x3 == "none" && x4 == "none" && x5 == "none", $"{x3} {x4} {x5}");
            var dups = new[] { new Vector2(0, 0), new Vector2(0, 0), new Vector2(1, 0), new Vector2(1.001f, 0), new Vector2(2, 1), new Vector2(2, 1) };
            e.points = dups;
            Check("U4 E6 duplicates and a 0.001 step stored raw: pointCount 6, edgeCount 5", Same4(e.points, dups) && e.pointCount == 6 && e.edgeCount == 5);
            bool ok9 = e.SetPoints(new List<Vector2> { new Vector2(0, 0), new Vector2(3, 0) });
            bool bad9 = e.SetPoints(new List<Vector2> { new Vector2(9, 9) });
            var gl = new List<Vector2> { Vector2.one, Vector2.one, Vector2.one, Vector2.one, Vector2.one };
            int gr = e.GetPoints(gl);
            Check("U4 E9 SetPoints(2) true, SetPoints(1) false and nothing; GetPoints(a prefilled list) clears, returns 2", ok9 && !bad9 && gr == 2 && gl.Count == 2 && e.pointCount == 2 && Same4(e.points, new[] { new Vector2(0, 0), new Vector2(3, 0) }));
            var es = new[] { new Vector2(0, 0), new Vector2(1, 0) }; e.points = es; es[0] = new Vector2(-9, -9); var eg = e.points; eg[1] = new Vector2(9, 9);
            Check("U4 E11 set copies, get copies", Same4(e.points, new[] { new Vector2(0, 0), new Vector2(1, 0) }));
            // E10: a ray sees the new points at once
            var e10 = new GameObject("U4 E10").AddComponent<EdgeCollider2D>(); e10.transform.position = new Vector3(21000f, 20f, 0f);
            e10.points = new[] { new Vector2(-1, 0), new Vector2(1, 0) };
            var h0 = Physics2D.Raycast(new Vector2(21003f, 25f), Vector2.down, 20f);
            e10.points = new[] { new Vector2(2, 1), new Vector2(4, 1) };
            var h1 = Physics2D.Raycast(new Vector2(21003f, 25f), Vector2.down, 20f);
            Check("U4 E10 Raycast sees set_points at once (distance 4)", h0.collider == null && h1.collider == e10 && h1.distance == 4f, $"{h1.distance:R}");
            // the chain filter (Paths4a2): (0, 0), (d, 0), (d + 1, 1) -- the ray at x = d / 2 meets y = 0 when (d, 0) stayed
            // (at x = 0: the filter measures world distances, and a ground edge's world points away from 0 lose the digits that decide it -- Paths4a2 E_far)
            static float Edge(string name, float y0, float d, float scale)
            {
                var g = new GameObject(name); g.transform.position = new Vector3(0f, y0, 0f); g.transform.localScale = new Vector3(scale, scale, 1f);
                var c = g.AddComponent<EdgeCollider2D>(); c.points = new[] { new Vector2(0, 0), new Vector2(d, 0), new Vector2(d + 1f, 1f) };
                return RayY4(0.5f * d * scale, y0 + 0.5f) - y0;
            }
            float yd = Edge("U4 Ed", 1000f, 0.0070887f, 1f), yk = Edge("U4 Ek", 1010f, 0.00708875f, 1f), ys = Edge("U4 Es", 1020f, 0.0036f, 2f), yn = Edge("U4 En", 1030f, 0.0035f, 2f);
            Check("U4 E-filter Unity's open-chain vertex filter: 0.0070887 dropped (the ray meets the slanted segment), 0.00708875 kept (y 0); world distances: 0.0036 x 2 kept, 0.0035 x 2 dropped",
                  yd > 0.003f && yd < 0.004f && yk == 0f && ys == 0f && yn > 0.003f && yn < 0.004f, $"{yd:R} {yk:R} {ys:R} {yn:R}");
            var e2 = new GameObject("U4 E7").AddComponent<EdgeCollider2D>(); e2.transform.position = new Vector3(21050f, 40f, 0f);
            e2.points = new[] { new Vector2(0, 0), new Vector2(0.004f, 0) };
            Check("U4 E7 two points 0.004 apart: stored, no shape (Unity's GetShapes 0)", e2.pointCount == 2 && PhysWorld.ShapesOf(Col(e2)).IsEmpty);
        }
        // ================= CompositeCollider2D (Paths4a C1-C14, Paths4a2 S / Y / L) =================
        {
            // C2: Manual, two disjoint squares
            var g2 = Body4("U4 C2", new Vector2(22000f, 0f), Vector3.one);
            var c2 = (CompositeCollider2D)Comp4(g2, true);
            var a2 = (PolygonCollider2D)Sub4(g2, "U4 C2 a", Vector2.zero, Sq4(0, 0, 2, 1)); var b2 = (PolygonCollider2D)Sub4(g2, "U4 C2 b", Vector2.zero, Sq4(5, 0, 6, 1));
            int before = c2.pathCount;
            c2.GenerateGeometry();
            var unity2 = new[] { new[] { new Vector2(5.99997091f, 0), new Vector2(5.99997091f, 1), new Vector2(5, 0.999970734f), new Vector2(5.00002909f, 0) },
                                 new[] { new Vector2(1.99997079f, 0), new Vector2(1.99997079f, 1), new Vector2(0, 0.999970734f), new Vector2(2.93000012E-05f, 0) } };
            var ours2 = CompPaths4(c2);
            string cmp2 = CompareUnity4(ours2, unity2, out bool pass2);
            Check("U4 C2 a Manual composite: 0 paths before GenerateGeometry; after: Unity's 2 paths -- counts, direction, Hausdorff <= 1e-4 (the vertices and order: 4b)",
                  before == 0 && c2.pathCount == 2 && c2.pointCount == 8 && pass2, cmp2);
            var small = new Vector2[2]; int rs = c2.GetPath(0, small);
            var big = Enumerable.Repeat(new Vector2(9, 9), 8).ToArray(); int rb = c2.GetPath(0, big);
            var cl = Enumerable.Repeat(new Vector2(7, 7), 7).ToList(); int rl = c2.GetPath(0, cl);
            int re = c2.GetPath(0, new Vector2[0]);
            Check("U4 C3 GetPath(array): copies min(length, points) and returns it, the rest untouched; GetPath(List): cleared, 4",
                  rs == 2 && rb == 4 && big[4] == new Vector2(9, 9) && big[7] == new Vector2(9, 9) && Same4(big.Take(4).ToArray(), ours2[0]) && rl == 4 && cl.Count == 4 && re == 0, $"{rs} {rb} {rl} {re}");
            Check("U4 C9 GetPathPointCount(-1) / (pathCount), GetPath(pathCount, a): ArgumentOutOfRangeException (the wrapper); GetPath(0, null): ArgumentNullException",
                  Thrown4(() => c2.GetPathPointCount(-1)) == "ArgumentOutOfRangeException" && Thrown4(() => c2.GetPathPointCount(2)) == "ArgumentOutOfRangeException" &&
                  Thrown4(() => c2.GetPath(2, new Vector2[4])) == "ArgumentOutOfRangeException" && Thrown4(() => c2.GetPath(0, (Vector2[])null)) == "ArgumentNullException");
            a2.SetPath(0, Sq4(0, 0, 3, 1));
            bool stale = Same4(CompPaths4(c2)[1], ours2[1]);
            b2.enabled = false;
            bool stale2 = c2.pathCount == 2;
            c2.GenerateGeometry();
            Check("U4 C2 Manual: a sub-collider's SetPath / disable change nothing until GenerateGeometry; then 1 path", stale && stale2 && c2.pathCount == 1 && c2.GetPathPointCount(0) == 4);
            // C7: the geometry type -- Manual takes it at GenerateGeometry, the paths are the outlines either way
            var g7 = Body4("U4 C7m", new Vector2(22100f, 0f), Vector3.one);
            var c7 = (CompositeCollider2D)Comp4(g7, true);
            Sub4(g7, "U4 C7m a", Vector2.zero, new[] { new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 1), new Vector2(1, 1), new Vector2(1, 2), new Vector2(0, 2) });
            c7.GenerateGeometry(); var o7 = CompPaths4(c7);
            c7.geometryType = CompositeCollider2D.GeometryType.Polygons; int gNo = Col(c7).GenGeometry;
            c7.GenerateGeometry(); var p7 = CompPaths4(c7); int gYes = Col(c7).GenGeometry;
            Check("U4 C7m Manual: set_geometryType takes effect at GenerateGeometry; the paths (6 points) are the outline in both types",
                  gNo == 0 && gYes == 1 && o7.Length == 1 && o7[0].Length == 6 && Same4(o7[0], p7[0]) && c7.geometryType == CompositeCollider2D.GeometryType.Polygons);
            // S: the path space -- the body's scale baked in, the composite's offset added to the output
            static Vector2[][] Space(string name, Vector2 at, Vector3 scale, Vector2 offset, Vector2 polyOffset)
            {
                var g = Body4(name, at, scale);
                var cs = (CompositeCollider2D)Comp4(g, true); cs.offset = offset;
                var pc = (PolygonCollider2D)Sub4(g, name + " a", Vector2.zero, Sq4(0, 0, 1, 1)); pc.offset = polyOffset;
                cs.GenerateGeometry();
                return CompPaths4(cs);
            }
            var sOff = Space("U4 S off", new Vector2(22200f, 0f), new Vector3(2, 1, 1), new Vector2(0.5f, 0.25f), Vector2.zero);
            string cmpS = CompareUnity4(sOff, new[] { new[] { new Vector2(2.49997091f, 0.25f), new Vector2(2.49997091f, 1.25f), new Vector2(0.5f, 1.24997067f), new Vector2(0.500029325f, 0.25f) } }, out bool passS);
            float yS = RayY4(22200f + 1.5f, 2f, 3f);
            Check("U4 S_scale2x1_off a (2, 1)-scaled body with offset (0.5, 0.25): Unity's path (scale baked, offset unscaled) within 1e-4; the outline's top edge at world y 1.25",
                  passS && MathF.Abs(yS - 1.25f) <= 1e-4f, cmpS + $"; ray y {yS:R}");
            var sMir = Space("U4 S mirror", new Vector2(22210f, 0f), new Vector3(-1, 1, 1), new Vector2(0.5f, 0f), Vector2.zero);
            string cmpM = CompareUnity4(sMir, new[] { new[] { new Vector2(0.499970704f, 0), new Vector2(0.499970704f, 1), new Vector2(-0.5f, 0.999970734f), new Vector2(-0.499970734f, 0) } }, out bool passM);
            Check("U4 S_mirror_off a mirrored body: the path mirrored, counter-clockwise, the offset not mirrored (within 1e-4)", passM, cmpM);
            var sZero = Space("U4 S zero", new Vector2(22220f, 0f), Vector3.one, Vector2.zero, Vector2.zero);
            var sOdd = Space("U4 S odd", new Vector2(22230f, 0f), Vector3.one, new Vector2(0.123456789f, 0.987654321f), Vector2.zero);
            bool addOk = sZero.Length == 1 && sOdd.Length == 1 && sOdd[0].Length == sZero[0].Length;
            for (int i = 0; addOk && i < sZero[0].Length; i++) addOk = Same4(new[] { sOdd[0][i] }, new[] { new Vector2(sZero[0][i].x + 0.123456789f, sZero[0][i].y + 0.987654321f) });
            Check("U4 S_unit_off_odd the composite offset is one float add on the output (= the zero-offset path + offset, bit for bit)", addOk, P4(sOdd.FirstOrDefault()));
            // C4: offsets, a tilted / scaled body, a rotated / scaled child -- at the oracle's own position (300, 5): since Stage 4b the inputs go
            // through world space as Unity's do (M = inverse(T R of the body) x the child's localToWorld), so the body's position decides their last bits
            var g4 = Body4("U4 C4", new Vector2(300f, 5f), new Vector3(2, 1, 1), 30f);
            var c4 = (CompositeCollider2D)Comp4(g4, true); c4.offset = new Vector2(0.5f, 0.25f);
            var p4a = (PolygonCollider2D)Sub4(g4, "U4 C4 a", new Vector2(1, 0), Sq4(0, 0, 1, 1)); p4a.offset = new Vector2(0.1f, 0.2f);
            var p4b = (PolygonCollider2D)Sub4(g4, "U4 C4 b", new Vector2(5, 0), Sq4(0, 0, 1, 1)); p4b.transform.localRotation = Quaternion.Euler(0, 0, 90); p4b.transform.localScale = new Vector3(1, 3, 1);
            c4.GenerateGeometry();
            var unity4 = new[] { new[] { new Vector2(4.49999905f, 0.2500287f), new Vector2(10.5f, 0.250029296f), new Vector2(10.4999704f, 1.25000012f), new Vector2(4.70000029f, 1.25002897f),
                                         new Vector2(4.69997072f, 1.45000017f), new Vector2(2.69999981f, 1.44997072f), new Vector2(2.70002913f, 0.449999988f), new Vector2(4.49999905f, 0.449970722f) } };
            string cmp4 = CompareUnity4(CompPaths4(c4), unity4, out bool pass4);
            Check("U4 C4 a 30-degree (2, 1)-scaled body, offset (0.5, 0.25), a child at (1, 0) with polygon offset (0.1, 0.2) and a 90-degree (1, 3)-scaled child: Unity's 8-point path within 1e-4", pass4, cmp4);
            // C1 / Y: Synchronous (the runtime default)
            var gy = Body4("U4 Y", new Vector2(22400f, 0f), Vector3.one);
            var cy = (CompositeCollider2D)Comp4(gy, false);
            int y0 = cy.pathCount;
            var py = (PolygonCollider2D)Sub4(gy, "U4 Y a", Vector2.zero, Sq4(0, 0, 2, 1), used: false);
            int y1 = cy.pathCount;
            py.usedByComposite = true; int y2 = cy.pathCount;
            py.SetPath(0, Sq4(0, 0, 3, 1)); var yp = CompPaths4(cy);
            string cmpY = CompareUnity4(yp, new[] { new[] { new Vector2(2.99997091f, 0), new Vector2(2.99997091f, 1), new Vector2(0, 0.999970734f), new Vector2(2.93000012E-05f, 0) } }, out bool passY);
            Check("U4 C1 Synchronous: 0 paths empty and with an unused child; usedByComposite = true -> 1 path at once; SetPath -> the new outline at once (Unity's within 1e-4)",
                  y0 == 0 && y1 == 0 && y2 == 1 && passY && cy.generationType == CompositeCollider2D.GenerationType.Synchronous, cmpY);
            py.transform.localPosition = new Vector3(0.5f, 0f, 0f);
            bool notYet = Same4(CompPaths4(cy)[0], yp[0]);
            Physics2D.SyncTransforms();
            var ym = CompPaths4(cy);
            string cmpYm = CompareUnity4(ym, new[] { new[] { new Vector2(3.49997091f, 0), new Vector2(3.49997091f, 1), new Vector2(0.5f, 0.999970734f), new Vector2(0.500029325f, 0) } }, out bool passYm);
            Check("U4 Y a moved sub-collider: not in the paths until the transform sync (Physics2D.SyncTransforms), then Unity's", notYet && passYm, cmpYm);
            py.usedByComposite = false; int u0 = cy.pathCount; py.usedByComposite = true; int u1 = cy.pathCount;
            py.enabled = false; int d0 = cy.pathCount; py.enabled = true; int d1 = cy.pathCount;
            Check("U4 C1 Synchronous: usedByComposite off / on and the sub-collider disabled / enabled change the paths at once (0, 1, 0, 1)", u0 == 0 && u1 == 1 && d0 == 0 && d1 == 1);
            cy.geometryType = CompositeCollider2D.GeometryType.Polygons;
            Check("U4 C7s Synchronous: set_geometryType regenerates at once (Polygons pieces)", Col(cy).GenGeometry == 1);
            // Y_flag_first: a used collider with no composite on its body is a normal collider; a runtime composite generates at AddComponent
            var gf = Body4("U4 Yf", new Vector2(22500f, 0f), Vector3.one);
            Sub4(gf, "U4 Yf a", Vector2.zero, Sq4(0, 0, 2, 1));
            var hitF = Physics2D.OverlapPoint(new Vector2(22501f, 0.5f));
            var cf = (CompositeCollider2D)Comp4(gf, false);
            int fAdded = cf.pathCount;
            Physics2D.SyncTransforms();
            var hitF2 = Physics2D.OverlapPoint(new Vector2(22501f, 0.5f));
            Check("U4 Y_flag_first usedByComposite with no composite: a normal collider (OverlapPoint hits it); AddComponent<CompositeCollider2D> -> 1 path at once, the polygon's own shape gone",
                  hitF != null && fAdded == 1 && hitF2 == null);
            // C11-C14: the flag refused on circles, edges, capsules
            var gr = Body4("U4 R", new Vector2(22600f, 0f), Vector3.one);
            var ci = new GameObject("U4 R circle"); ci.transform.SetParent(gr.transform, false); var cic = ci.AddComponent<CircleCollider2D>(); cic.usedByComposite = true;
            var ed = new GameObject("U4 R edge"); ed.transform.SetParent(gr.transform, false); var edc = ed.AddComponent<EdgeCollider2D>(); edc.usedByComposite = true;
            var ca = new GameObject("U4 R capsule"); ca.transform.SetParent(gr.transform, false); var cac = ca.AddComponent<CapsuleCollider2D>(); cac.usedByComposite = true;
            var bx = new GameObject("U4 R box"); bx.transform.SetParent(gr.transform, false); var bxc = bx.AddComponent<BoxCollider2D>(); bxc.usedByComposite = true;
            Check("U4 C11-C14 usedByComposite refused on a circle / edge / capsule (stays false), taken on a box",
                  !Col(cic).UsedByComposite && !Col(edc).UsedByComposite && !Col(cac).UsedByComposite && Col(bxc).UsedByComposite);
            // L: Unity's loop vertex filter on an outline (a 0.003 notch: its vertices within b2_linearSlop of the last kept are dropped)
            var gl = Body4("U4 L", new Vector2(22700f, 0f), Vector3.one);
            var cl3 = (CompositeCollider2D)Comp4(gl, true);
            Sub4(gl, "U4 L a", Vector2.zero, Sq4(0, 0, 2, 1)); Sub4(gl, "U4 L b", Vector2.zero, Sq4(1, 0.5f, 1.003f, 1.003f));
            cl3.GenerateGeometry();
            int nl = cl3.GetPathPointCount(0);
            float yL = RayY4(22701.0015f, 1.5f);
            Check("U4 L_Outlines_0.003 the path keeps the notch (8 points, as Unity's) but the loop shape drops it: a ray at x 1.0015 meets y 1 (1.003 without the filter)",
                  cl3.pathCount == 1 && nl == 8 && MathF.Abs(yL - 1f) <= 1e-4f, $"{nl} points, ray y {yL:R}");
        }
    }
}
#endif
