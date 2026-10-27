// Kit #7 tests track: MACHINE-DRAFTED probe candidates (see D_Objects.cs). Area: data-only engine APIs the game reaches -- AnimationCurve
// evaluation, string hashes, geometry utilities, Bounds / Plane / Rect, RectOffset, Gradient, LayerMask names.
using System;
using UnityEngine;

namespace ShepherdOracle.Drafted
{

    /// <summary>AnimationCurve.Evaluate (native in Unity: Hermite segments, weighted tangents, pre / post wrap modes) at seeded times.</summary>
    public static class D_Curve
    {
        public static void Register() { Suites.All["d-data-curve"] = Case; }
        static void Case(Rng r, Out o)
        {
            int n = 1 + r.Int(5); var keys = new Keyframe[n]; float t = r.Exact(-20);
            for (int i = 0; i < n; i++)
            {
                t += 0.05f + r.Unit() * 2f;
                var k = new Keyframe(t, Gen.FM(r) * 0.01f, r.Chance(10) ? float.PositiveInfinity : r.Exact(-21), r.Chance(10) ? float.PositiveInfinity : r.Exact(-21));
                if (r.Chance(30)) { k.weightedMode = (WeightedMode)r.Int(4); k.inWeight = r.Unit(); k.outWeight = r.Unit(); }
                keys[i] = k;
            }
            var c = new AnimationCurve(keys);
            c.preWrapMode = r.Pick(new[] { WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong, WrapMode.ClampForever, WrapMode.Default });
            c.postWrapMode = r.Pick(new[] { WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong, WrapMode.ClampForever, WrapMode.Default });
            o.I("in.n", n); o.I("in.pre", (int)c.preWrapMode); o.I("in.post", (int)c.postWrapMode); o.I("length", c.length);
            float t0 = keys[0].time, t1 = keys[n - 1].time;
            for (int i = 0; i < 12; i++)
            {
                float x = i < 2 ? (i == 0 ? t0 : t1) : t0 - 1f + (t1 - t0 + 2f) * r.Unit();
                try { o.Raw("eval" + i, Out.H(x) + "->" + Out.H(c.Evaluate(x))); } catch (Exception e) { o.Exc("eval" + i, e); }
            }
            try { var lin = AnimationCurve.Linear(0f, Gen.FM(r), 1f + r.Unit(), Gen.FM(r)); o.F("linear", lin.Evaluate(r.Unit())); } catch (Exception e) { o.Exc("linear", e); }
            try { var ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f); o.F("ease", ease.Evaluate(r.Unit())); } catch (Exception e) { o.Exc("ease", e); }
            try { int idx = c.AddKey(t0 + (t1 - t0) * r.Unit(), r.Unit()); o.I("addKey", idx); o.I("lengthAfter", c.length); } catch (Exception e) { o.Exc("addKey", e); }
        }
    }

    /// <summary>Animator.StringToHash (CRC32-style in Unity) and Shader.PropertyToID stability for seeded strings.</summary>
    public static class D_Hashes
    {
        public static void Register() { Suites.All["d-data-hashes"] = Case; }
        static readonly string[] Common = { "Base Layer.Idle", "Speed", "isRunning", "_MainTex", "_Color", "Attack", "", "Run Blend", "a", "Base Layer" };
        static void Case(Rng r, Out o)
        {
            string s = r.Chance(40) ? r.Pick(Common) : Gen.S(r, 20);
            o.S("in.s", s);
            try { o.I("StringToHash", Animator.StringToHash(s)); } catch (Exception e) { o.Exc("StringToHash", e); }
            try { int a = Shader.PropertyToID(s), b = Shader.PropertyToID(s); o.B("PropertyToIDStable", a == b); o.B("PropertyToIDNonZero", a != 0 || s.Length == 0); } catch (Exception e) { o.Exc("PropertyToID", e); }
        }
    }

    /// <summary>GeometryUtility: CalculateFrustumPlanes from a seeded projection * view, TestPlanesAABB with seeded bounds, CalculateBounds.</summary>
    public static class D_Geometry
    {
        public static void Register() { Suites.All["d-data-geometry"] = Case; }
        static void Case(Rng r, Out o)
        {
            var proj = r.Bool() ? Matrix4x4.Perspective(20f + r.Unit() * 100f, 0.5f + r.Unit() * 2f, 0.1f, 50f + r.Unit() * 500f) : Matrix4x4.Ortho(-5f, 5f, -3f, 3f, 0.1f, 100f);
            var view = Matrix4x4.TRS(new Vector3(r.Exact(-18), r.Exact(-18), r.Exact(-18)), Quaternion.Euler(r.Int(360), r.Int(360), 0f), Vector3.one).inverse;
            Plane[] planes = null;
            try { planes = GeometryUtility.CalculateFrustumPlanes(proj * view); for (int i = 0; i < 6; i++) o.Raw("plane" + i, DW.V(planes[i].normal) + "|" + Out.H(planes[i].distance)); } catch (Exception e) { o.Exc("planes", e); }
            for (int i = 0; i < 6 && planes != null; i++)
            {
                var b = new Bounds(new Vector3(r.Exact(-17), r.Exact(-17), r.Exact(-17)), new Vector3(r.Unit() * 10f, r.Unit() * 10f, r.Unit() * 10f));
                try { o.B("aabb" + i, GeometryUtility.TestPlanesAABB(planes, b)); } catch (Exception e) { o.Exc("aabb" + i, e); }
            }
            try
            {
                var pts = new Vector3[3 + r.Int(4)]; for (int i = 0; i < pts.Length; i++) pts[i] = new Vector3(Gen.FM(r), Gen.FM(r), Gen.FM(r));
                var tb = GeometryUtility.CalculateBounds(pts, Matrix4x4.TRS(Vector3.one, Quaternion.Euler(0, r.Int(360), 0), Vector3.one));
                o.Raw("calcBounds", DW.V(tb.center) + "|" + DW.V(tb.size));
            }
            catch (Exception e) { o.Exc("calcBounds", e); }
        }
    }

    /// <summary>Bounds / Plane / Rect / Ray maths (managed and native parts): Encapsulate, Intersects, Contains, ClosestPoint, SqrDistance,
    /// IntersectRay, Plane.Raycast / GetSide / ClosestPointOnPlane, Rect.Overlaps / Contains / NormalizedToPoint.</summary>
    public static class D_Shapes
    {
        public static void Register() { Suites.All["d-data-shapes"] = Case; }
        static Vector3 P(Rng r) => new Vector3(Gen.FM(r) * 0.01f, Gen.FM(r) * 0.01f, Gen.FM(r) * 0.01f);
        static void Case(Rng r, Out o)
        {
            var a = new Bounds(P(r), new Vector3(r.Unit() * 4f, r.Unit() * 4f, r.Unit() * 4f)); var b = new Bounds(P(r), new Vector3(r.Unit() * 4f, r.Unit() * 4f, r.Unit() * 4f)); var p = P(r);
            o.B("intersects", a.Intersects(b)); o.B("contains", a.Contains(p)); o.Raw("closest", DW.V(a.ClosestPoint(p))); o.F("sqrDist", a.SqrDistance(p));
            var e = a; e.Encapsulate(b); o.Raw("encap", DW.V(e.min) + "|" + DW.V(e.max)); e.Expand(r.Unit()); o.Raw("expand", DW.V(e.size));
            var ray = new Ray(P(r), new Vector3(r.Exact(-23), r.Exact(-23), r.Exact(-23)));
            float d; o.Raw("rayBounds", (a.IntersectRay(ray, out d) ? "1:" : "0:") + Out.H(d));
            var pl = new Plane(new Vector3(r.Exact(-23), r.Exact(-23), r.Exact(-23)), r.Exact(-20));
            o.Raw("plane", DW.V(pl.normal) + "|" + Out.H(pl.distance)); o.B("side", pl.GetSide(p)); o.F("dist", pl.GetDistanceToPoint(p)); o.Raw("cpop", DW.V(pl.ClosestPointOnPlane(p)));
            float rd; o.Raw("rayPlane", (pl.Raycast(ray, out rd) ? "1:" : "0:") + Out.H(rd));
            var pl3 = new Plane(P(r), P(r), P(r)); o.Raw("plane3", DW.V(pl3.normal) + "|" + Out.H(pl3.distance));
            var ra = new Rect(Gen.FM(r), Gen.FM(r), Gen.FM(r), Gen.FM(r)); var rb = new Rect(Gen.FM(r), Gen.FM(r), Gen.FM(r), Gen.FM(r));
            o.B("overlaps", ra.Overlaps(rb)); o.B("overlapsNeg", ra.Overlaps(rb, true)); o.B("rcontains", ra.Contains(new Vector2(p.x * 100f, p.y * 100f)));
            var np = Rect.NormalizedToPoint(ra, new Vector2(r.Unit(), r.Unit())); o.Raw("n2p", Out.H(np.x) + "," + Out.H(np.y));
            var pn = Rect.PointToNormalized(ra, new Vector2(p.x * 100f, p.y * 100f)); o.Raw("p2n", Out.H(pn.x) + "," + Out.H(pn.y));
            o.S("rectToString", ra.ToString()); o.S("boundsToString", a.ToString());
        }
    }

    /// <summary>RectOffset (native object in Unity) and Gradient.Evaluate (native: blend / fixed modes, key limits).</summary>
    public static class D_OffsetGradient
    {
        public static void Register() { Suites.All["d-data-offsetgradient"] = Case; }
        static void Case(Rng r, Out o)
        {
            var ro = new RectOffset(r.Int(50) - 10, r.Int(50), r.Int(50), r.Int(50) - 10);
            o.Raw("ro", ro.left + "," + ro.right + "," + ro.top + "," + ro.bottom + "|" + ro.horizontal + "," + ro.vertical);
            ro.left = r.Int(100) - 50; ro.bottom = r.Int(100);
            var rect = new Rect(Gen.FM(r), Gen.FM(r), r.Unit() * 200f, r.Unit() * 200f);
            var add = ro.Add(rect); var rem = ro.Remove(rect);
            o.Raw("add", Out.H(add.x) + "," + Out.H(add.y) + "," + Out.H(add.width) + "," + Out.H(add.height)); o.Raw("remove", Out.H(rem.x) + "," + Out.H(rem.width));
            o.S("roToString", ro.ToString());
            var g = new Gradient(); int nc = 1 + r.Int(4), na = 1 + r.Int(4);
            var ck = new GradientColorKey[nc]; for (int i = 0; i < nc; i++) ck[i] = new GradientColorKey(new Color(r.Unit(), r.Unit(), r.Unit()), r.Unit());
            var ak = new GradientAlphaKey[na]; for (int i = 0; i < na; i++) ak[i] = new GradientAlphaKey(r.Unit(), r.Unit());
            g.SetKeys(ck, ak); if (r.Chance(40)) g.mode = GradientMode.Fixed;
            o.I("keys", g.colorKeys.Length * 10 + g.alphaKeys.Length);
            for (int i = 0; i < 6; i++) { float t = i == 0 ? 0f : i == 1 ? 1f : r.Unit(); o.Raw("g" + i, Out.H(t) + "->" + DW.C(g.Evaluate(t))); }
        }
    }

    /// <summary>LayerMask: names from the project's layers (Unity's tag manager vs the game's: both CotL's), GetMask, NameToLayer.</summary>
    public static class D_Layers
    {
        public static void Register() { Suites.All["d-data-layers"] = Case; }
        static void Case(Rng r, Out o)
        {
            int l = r.Int(32);
            o.S("LayerToName", LayerMask.LayerToName(l));
            o.I("NameToLayerDefault", LayerMask.NameToLayer("Default")); o.I("NameToLayerUI", LayerMask.NameToLayer("UI")); o.I("NameToLayerBogus", LayerMask.NameToLayer("Bogus" + l));
            o.I("GetMask", LayerMask.GetMask("Default", "UI", "Ignore Raycast"));
            LayerMask m = 1 << l; o.I("mask", m.value);
        }
    }
}
