// Kit #7 tests track: RectTransform layout suites (vita/notes/kit7-tests.md, differential fuzzing: "layout"). Random parent / child rect
// setups -- anchors (points and stretches, inverted ones), pivots, sizes, anchored positions, offsets, scales and rotations -- and every
// derived value Unity computes natively and Shepherd reimplements: rect, localPosition, offsetMin / Max, sizeDelta after the setters,
// SetSizeWithCurrentAnchors, SetInsetAndSizeFromParentEdge, ForceUpdateRectTransforms, GetLocalCorners / GetWorldCorners, and
// RectTransformUtility's point-in-rect / screen-to-local queries without a camera (an overlay canvas's convention).
using System;
using UnityEngine;

namespace ShepherdOracle
{
    public static class ProbesLayout
    {
        public static void Register() { Suites.All["layout"] = Layout; }
        static string V(Vector3 v) => Out.H(v.x) + "," + Out.H(v.y) + "," + Out.H(v.z);
        static string V2(Vector2 v) => Out.H(v.x) + "," + Out.H(v.y);
        static string R(Rect r) => Out.H(r.x) + "," + Out.H(r.y) + "," + Out.H(r.width) + "," + Out.H(r.height);
        static float A(Rng r) => r.Chance(30) ? r.Pick(new[] { 0f, 0.5f, 1f }) : r.Chance(5) ? r.Pick(new[] { -0.5f, 1.5f }) : r.Unit();
        static float Sz(Rng r) => r.Chance(10) ? r.Pick(new[] { 0f, -10f, 1920f }) : (float)(r.Int(4001) - 1000) / (1 << r.Int(3));
        static void Dump(Out o, string p, RectTransform t)
        {
            o.Raw(p + ".rect", R(t.rect)); o.Raw(p + ".lp", V(t.localPosition)); o.Raw(p + ".ap", V2(t.anchoredPosition)); o.Raw(p + ".sd", V2(t.sizeDelta));
            o.Raw(p + ".omin", V2(t.offsetMin)); o.Raw(p + ".omax", V2(t.offsetMax)); o.Raw(p + ".ap3", V(t.anchoredPosition3D));
            var c = new Vector3[4]; t.GetLocalCorners(c); o.Raw(p + ".lc", V(c[0]) + ";" + V(c[2]));
            t.GetWorldCorners(c); o.Raw(p + ".wc", V(c[0]) + ";" + V(c[1]) + ";" + V(c[2]) + ";" + V(c[3]));
            o.Raw(p + ".pos", V(t.position));
        }
        static void Layout(Rng r, Out o)
        {
            var root = new GameObject("root", typeof(RectTransform)); var child = new GameObject("child", typeof(RectTransform)); var grand = new GameObject("grand", typeof(RectTransform));
            try
            {
                var rt = (RectTransform)root.transform; var ct = (RectTransform)child.transform; var gt = (RectTransform)grand.transform;
                rt.sizeDelta = new Vector2(Sz(r), Sz(r)); rt.pivot = new Vector2(A(r), A(r)); rt.position = new Vector3(Sz(r), Sz(r), 0f);
                if (r.Chance(30)) rt.localScale = new Vector3(r.Exact(-22), r.Exact(-22), 1f);
                if (r.Chance(20)) rt.localRotation = Quaternion.Euler(0f, 0f, r.Int(360));
                ct.SetParent(rt, r.Bool());
                ct.anchorMin = new Vector2(A(r), A(r)); ct.anchorMax = r.Chance(40) ? ct.anchorMin : new Vector2(A(r), A(r)); ct.pivot = new Vector2(A(r), A(r));
                ct.anchoredPosition = new Vector2(Sz(r), Sz(r)); ct.sizeDelta = new Vector2(Sz(r), Sz(r));
                if (r.Chance(25)) ct.localScale = new Vector3(r.Exact(-22), r.Exact(-22), r.Exact(-22));
                gt.SetParent(ct, false); gt.anchorMin = Vector2.zero; gt.anchorMax = Vector2.one; gt.offsetMin = new Vector2(Sz(r) * 0.1f, Sz(r) * 0.1f); gt.offsetMax = new Vector2(-Sz(r) * 0.1f, Sz(r) * 0.1f);
                Dump(o, "root", rt); Dump(o, "child", ct); Dump(o, "grand", gt);
                int k = r.Int(6);
                switch (k)
                {
                    case 0: ct.SetSizeWithCurrentAnchors(r.Bool() ? RectTransform.Axis.Horizontal : RectTransform.Axis.Vertical, Sz(r)); break;
                    case 1: ct.SetInsetAndSizeFromParentEdge((RectTransform.Edge)r.Int(4), Sz(r) * 0.25f, Sz(r)); break;
                    case 2: ct.offsetMin = new Vector2(Sz(r), Sz(r)); break;
                    case 3: ct.offsetMax = new Vector2(Sz(r), Sz(r)); break;
                    case 4: ct.pivot = new Vector2(A(r), A(r)); break;
                    default: ct.anchorMax = new Vector2(A(r), A(r)); break;
                }
                o.I("op", k);
                rt.ForceUpdateRectTransforms();
                Dump(o, "child2", ct); Dump(o, "grand2", gt);
                var sp = new Vector2(Sz(r), Sz(r)); o.Raw("in.sp", V2(sp));
                o.B("contains", RectTransformUtility.RectangleContainsScreenPoint(ct, sp));
                Vector2 lp; bool hit = RectTransformUtility.ScreenPointToLocalPointInRectangle(ct, sp, null, out lp); o.Raw("s2l", (hit ? "1:" : "0:") + V2(lp));
                Vector3 wp; hit = RectTransformUtility.ScreenPointToWorldPointInRectangle(ct, sp, null, out wp); o.Raw("s2w", (hit ? "1:" : "0:") + V(wp));
                o.Raw("pixelAdjust", R(RectTransformUtility.PixelAdjustRect(ct, null)));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
