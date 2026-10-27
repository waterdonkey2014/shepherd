// Kit #7 tests track: seeded stateful (model-based) tests of the Transform hierarchy (vita/notes/kit7-tests.md "Seeded property-based
// tests vs Unity", CsCheck's model-based idea, written in-house). Each case is a random sequence of 12-40 operations on up to 8
// GameObjects -- create, SetParent (world position kept or not; cycles), parent =, SetSiblingIndex (any index), SetAsFirst/LastSibling,
// DestroyImmediate (with its children), local / world position / rotation / scale writes (zero and negative scales too), Translate,
// Rotate, RotateAround, LookAt, SetPositionAndRotation, euler writes, DetachChildren, SetActive, Instantiate (with and without a parent) --
// and after EVERY operation every live object's observable state is written: parent, sibling index, child count, world / local position,
// rotation, lossy scale, local euler angles, TransformPoint / InverseTransformPoint, activeInHierarchy, the clone's name. Unity's player
// is the model: Shepherd (whose Transform is an engine reimplementation) must write the same bits. A failing case replays from its seed;
// its first differing operation is the minimal reproduction (the sequence before it agrees).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShepherdOracle
{
    public static class ProbesTransform
    {
        public static void Register() { Suites.All["transform-model"] = Model; Suites.Hashed.Add("transform-model"); Suites.All["transform-api"] = Api; }

        static string V(Vector3 v) => Out.H(v.x) + "," + Out.H(v.y) + "," + Out.H(v.z);
        static string Qs(Quaternion q) => Out.H(q.x) + "," + Out.H(q.y) + "," + Out.H(q.z) + "," + Out.H(q.w);
        static Vector3 V3(Rng r) => r.Chance(10) ? new Vector3(Gen.F(r), Gen.FM(r), Gen.FM(r)) : new Vector3(Gen.FM(r), Gen.FM(r), Gen.FM(r));
        static Vector3 S3(Rng r) { if (r.Chance(10)) return r.Pick(new[] { Vector3.zero, new Vector3(-1f, 1f, 1f), new Vector3(1f, 0f, 1f), new Vector3(2f, 2f, 2f) }); return new Vector3(r.Exact(-21), r.Exact(-21), r.Exact(-21)); }
        static Quaternion Q(Rng r) { if (r.Chance(10)) return Quaternion.identity; return Quaternion.Euler(r.Int(720) - 360, r.Exact(-15), r.Int(360)); }

        static void Observe(Out o, List<GameObject> live, int step)
        {
            for (int k = 0; k < live.Count; k++)
            {
                var g = live[k]; var t = g.transform;
                string line;
                try
                {
                    line = (t.parent == null ? "-" : t.parent.name) + "|" + t.GetSiblingIndex() + "|" + t.childCount + "|" + V(t.position) + "|" + V(t.localPosition) + "|" + Qs(t.rotation) + "|" + Qs(t.localRotation)
                        + "|" + V(t.lossyScale) + "|" + V(t.localScale) + "|" + V(t.localEulerAngles) + "|" + V(t.TransformPoint(new Vector3(1f, 2f, 3f))) + "|" + V(t.InverseTransformPoint(new Vector3(-4f, 0.5f, 9f)))
                        + "|" + V(t.TransformDirection(Vector3.forward)) + "|" + (g.activeInHierarchy ? 1 : 0) + "|" + (g.activeSelf ? 1 : 0) + "|" + t.root.name + "|" + t.hierarchyCount;
                }
                catch (Exception e) { line = "EXC:" + e.GetType().Name; }
                o.Raw("s" + step + "." + g.name, line);
            }
        }

        static void Model(Rng r, Out o)
        {
            var live = new List<GameObject>(); int made = 0; int steps = 12 + r.Int(29);
            try
            {
                for (int step = 0; step < steps; step++)
                {
                    live.RemoveAll(x => x == null);
                    int op = live.Count == 0 ? 0 : r.Int(22);
                    GameObject a = live.Count > 0 ? live[r.Int(live.Count)] : null, b = live.Count > 0 ? live[r.Int(live.Count)] : null;
                    string desc;
                    try
                    {
                        switch (op)
                        {
                            case 0: case 21:
                                if (live.Count >= 8) { desc = "skip"; break; }
                                var g = new GameObject("g" + made++); live.Add(g); desc = "new " + g.name; break;
                            case 1: { bool keep = r.Bool(); var p = r.Chance(20) ? null : b.transform; a.transform.SetParent(p, keep); desc = "SetParent " + a.name + " " + (p == null ? "null" : p.name) + " " + keep; break; }
                            case 2: { var p = r.Chance(20) ? null : b.transform; a.transform.parent = p; desc = "parent= " + a.name + " " + (p == null ? "null" : p.name); break; }
                            case 3: { int i = r.Int(8) - 2; a.transform.SetSiblingIndex(i); desc = "SetSiblingIndex " + a.name + " " + i; break; }
                            case 4: if (r.Bool()) { a.transform.SetAsFirstSibling(); desc = "First " + a.name; } else { a.transform.SetAsLastSibling(); desc = "Last " + a.name; } break;
                            case 5: desc = "DestroyImmediate " + a.name; UnityEngine.Object.DestroyImmediate(a); break;
                            case 6: { var v = V3(r); a.transform.localPosition = v; desc = "localPosition " + a.name + " " + V(v); break; }
                            case 7: { var q = Q(r); a.transform.localRotation = q; desc = "localRotation " + a.name + " " + Qs(q); break; }
                            case 8: { var s = S3(r); a.transform.localScale = s; desc = "localScale " + a.name + " " + V(s); break; }
                            case 9: { var v = V3(r); a.transform.position = v; desc = "position " + a.name + " " + V(v); break; }
                            case 10: { var q = Q(r); a.transform.rotation = q; desc = "rotation " + a.name + " " + Qs(q); break; }
                            case 11: { var v = V3(r); var sp = r.Bool() ? Space.Self : Space.World; a.transform.Translate(v, sp); desc = "Translate " + a.name + " " + V(v) + " " + sp; break; }
                            case 12: { var e = new Vector3(r.Int(360), r.Exact(-16), r.Int(90)); var sp = r.Bool() ? Space.Self : Space.World; a.transform.Rotate(e, sp); desc = "Rotate " + a.name + " " + V(e) + " " + sp; break; }
                            case 13: { var pt = V3(r); var ax = r.Pick(new[] { Vector3.up, Vector3.forward, new Vector3(1f, 1f, 0f), Vector3.zero }); float an = r.Int(720) - 360; a.transform.RotateAround(pt, ax, an); desc = "RotateAround " + a.name + " " + V(pt) + " " + V(ax) + " " + an; break; }
                            case 14: if (r.Bool()) { a.transform.LookAt(b.transform); desc = "LookAt " + a.name + " " + b.name; } else { var pt = V3(r); a.transform.LookAt(pt, r.Bool() ? Vector3.up : Vector3.forward); desc = "LookAtP " + a.name + " " + V(pt); } break;
                            case 15: { var v = V3(r); var q = Q(r); a.transform.SetPositionAndRotation(v, q); desc = "SetPositionAndRotation " + a.name + " " + V(v) + " " + Qs(q); break; }
                            case 16: { var e = new Vector3(r.Int(720) - 360, r.Exact(-15), r.Int(181) - 90); if (r.Bool()) { a.transform.eulerAngles = e; desc = "eulerAngles " + a.name + " " + V(e); } else { a.transform.localEulerAngles = e; desc = "localEulerAngles " + a.name + " " + V(e); } break; }
                            case 17: a.transform.DetachChildren(); desc = "DetachChildren " + a.name; break;
                            case 18: { bool on = r.Chance(40); a.SetActive(on); desc = "SetActive " + a.name + " " + on; break; }
                            case 19:
                            {
                                if (live.Count >= 8) { desc = "skip"; break; }
                                GameObject c;
                                if (r.Bool()) { c = UnityEngine.Object.Instantiate(a); desc = "Instantiate " + a.name + " -> '" + c.name + "'"; }
                                else { bool keep = r.Bool(); c = UnityEngine.Object.Instantiate(a, b.transform, keep); desc = "InstantiateP " + a.name + " " + b.name + " " + keep + " -> '" + c.name + "'"; }
                                c.name = "g" + made++; desc += " as " + c.name; live.Add(c);
                                // the clone's children (named like the originals' children): renamed so names stay unique, in hierarchy order
                                var kids = c.GetComponentsInChildren<Transform>(true);
                                foreach (var kt in kids) if (kt != c.transform) { kt.gameObject.name = "g" + made++; if (live.Count < 32) live.Add(kt.gameObject); }
                                break;
                            }
                            default: { var pt = V3(r); a.transform.localPosition += pt * 0.5f; a.transform.localScale = Vector3.Scale(a.transform.localScale, new Vector3(-1f, 1f, 1f)); desc = "mirror " + a.name; break; }
                        }
                    }
                    catch (Exception e) { desc = "op " + op + " EXC:" + e.GetType().Name; }
                    o.Raw("op" + step, desc);
                    live.RemoveAll(x => x == null);
                    Observe(o, live, step);
                }
            }
            finally
            {
                foreach (var g in live) if (g != null && g.transform.parent == null) UnityEngine.Object.DestroyImmediate(g);
                foreach (var g in live) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            }
        }

        /// <summary>Single-shot API checks on a small fixed hierarchy with random values (fast to diagnose).</summary>
        static void Api(Rng r, Out o)
        {
            var root = new GameObject("root"); var mid = new GameObject("mid"); var leaf = new GameObject("leaf");
            try
            {
                mid.transform.SetParent(root.transform, false); leaf.transform.SetParent(mid.transform, false);
                root.transform.SetPositionAndRotation(V3(r), Q(r)); root.transform.localScale = S3(r);
                mid.transform.localPosition = V3(r); mid.transform.localRotation = Q(r); mid.transform.localScale = S3(r);
                leaf.transform.localPosition = V3(r);
                var lt = leaf.transform; var v = V3(r);
                o.Raw("pos", V(lt.position)); o.Raw("rot", Qs(lt.rotation)); o.Raw("lossy", V(lt.lossyScale)); o.Raw("euler", V(lt.eulerAngles));
                o.Raw("right", V(lt.right)); o.Raw("up", V(lt.up)); o.Raw("fwd", V(lt.forward));
                o.Raw("TP", V(lt.TransformPoint(v))); o.Raw("ITP", V(lt.InverseTransformPoint(v))); o.Raw("TV", V(lt.TransformVector(v))); o.Raw("ITV", V(lt.InverseTransformVector(v)));
                o.Raw("TD", V(lt.TransformDirection(v))); o.Raw("ITD", V(lt.InverseTransformDirection(v)));
                var m = lt.localToWorldMatrix; var w = lt.worldToLocalMatrix; var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 16; i++) sb.Append(Out.H(m[i])).Append(i < 15 ? "," : ""); o.Raw("l2w", sb.ToString()); sb.Length = 0;
                for (int i = 0; i < 16; i++) sb.Append(Out.H(w[i])).Append(i < 15 ? "," : ""); o.Raw("w2l", sb.ToString());
                var up = new Vector3(0f, 1f, 0f); lt.right = V3(r); o.Raw("setRight", Qs(lt.rotation)); lt.up = V3(r); o.Raw("setUp", Qs(lt.rotation)); lt.forward = V3(r); o.Raw("setFwd", Qs(lt.rotation));
                o.B("IsChildOf", lt.IsChildOf(root.transform)); o.S("Find", root.transform.Find("mid/leaf") == lt ? "leaf" : "?"); o.I("childCount", root.transform.childCount);
                lt.SetParent(null, true); o.Raw("detached.pos", V(lt.position)); o.Raw("detached.lossy", V(lt.lossyScale)); o.Raw("detached.lrot", Qs(lt.localRotation));
                lt.SetParent(mid.transform, true); o.Raw("reattached.lpos", V(lt.localPosition)); o.Raw("reattached.lscale", V(lt.localScale));
                var go = new GameObject("x"); go.transform.SetParent(root.transform); go.transform.SetSiblingIndex(0); o.I("sibling", go.transform.GetSiblingIndex()); o.I("midSibling", mid.transform.GetSiblingIndex());
            }
            finally { UnityEngine.Object.DestroyImmediate(root); if (leaf != null) UnityEngine.Object.DestroyImmediate(leaf); }
        }
    }
}
