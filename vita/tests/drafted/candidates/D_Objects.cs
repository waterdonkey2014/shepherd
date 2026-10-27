// Kit #7 tests track: MACHINE-DRAFTED probe candidates (drafted by Claude for the Unity APIs the game's routes reach that no earlier suite
// reaches: vita/notes/kit7-tests.md "AI-written tests behind strict filters"). A candidate survives only if it compiles on both sides,
// gives the same output in repeated Unity runs, reaches engine code no earlier suite reaches and catches a seeded fault in that code
// (vita/tests/drafted/filter.sh). Area: GameObject / Component / Object lookups and orders.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShepherdOracle.Drafted
{
    public class DProbeMarker : MonoBehaviour { public int Id; }
    public class DProbeMarker2 : MonoBehaviour { }


    /// <summary>FindObjectsOfType / FindObjectsByType order (the game iterates the result) for objects made in a seeded order, some inactive / destroyed.</summary>
    public static class D_FindOrder
    {
        public static void Register() { Suites.All["d-obj-findorder"] = Case; }
        static string Sorted(Component[] cs) { var l = new List<string>(); foreach (var c in cs) l.Add(c == null ? "null" : c.gameObject.name); l.Sort(StringComparer.Ordinal); return string.Join(",", l.ToArray()); }
        static void Case(Rng r, Out o)
        {
            var made = new List<GameObject>(); int n = 2 + r.Int(7);
            try
            {
                for (int i = 0; i < n; i++) { var g = new GameObject("f" + i); var m = g.AddComponent<DProbeMarker>(); m.Id = i; made.Add(g); if (r.Chance(25)) g.SetActive(false); }
                if (r.Chance(40)) { int k = r.Int(n); UnityEngine.Object.DestroyImmediate(made[k]); }
                // (first filter round: Unity's UNSORTED order differs between three runs of the same player -- not a fidelity target, so the
                // unsorted results are compared as sets (sorted names) and only the InstanceID-sorted call as an order)
                o.S("FindObjectsOfTypeSet", Sorted(UnityEngine.Object.FindObjectsOfType<DProbeMarker>()));
                o.S("FindObjectsOfTypeInactiveSet", Sorted(UnityEngine.Object.FindObjectsOfType<DProbeMarker>(true)));
                o.S("ByTypeNoneSet", Sorted(UnityEngine.Object.FindObjectsByType<DProbeMarker>(FindObjectsSortMode.None)));
                o.S("ByTypeId", DUtil.Names(UnityEngine.Object.FindObjectsByType<DProbeMarker>(FindObjectsSortMode.InstanceID)));
                var one = UnityEngine.Object.FindObjectOfType<DProbeMarker>(); o.B("FindObjectOfTypeFound", one != null);
            }
            finally { DUtil.Cleanup(made); }
        }
    }

    /// <summary>GetComponents / GetComponentsInChildren / InParent orders and the include-inactive rule.</summary>
    public static class D_ComponentOrder
    {
        public static void Register() { Suites.All["d-obj-components"] = Case; }
        static void Case(Rng r, Out o)
        {
            var made = new List<GameObject>();
            try
            {
                var root = new GameObject("root"); made.Add(root);
                var all = new List<GameObject> { root };
                int n = 3 + r.Int(6);
                for (int i = 0; i < n; i++)
                {
                    var g = new GameObject("c" + i); made.Add(g); g.transform.SetParent(all[r.Int(all.Count)].transform, false); all.Add(g);
                    if (r.Chance(60)) g.AddComponent<DProbeMarker>(); if (r.Chance(30)) g.AddComponent<DProbeMarker2>(); if (r.Chance(20)) g.AddComponent<DProbeMarker>();
                    if (r.Chance(20)) g.SetActive(false);
                }
                var leaf = all[all.Count - 1];
                o.S("InChildren", DUtil.Names(root.GetComponentsInChildren<DProbeMarker>()));
                o.S("InChildrenAll", DUtil.Names(root.GetComponentsInChildren<DProbeMarker>(true)));
                o.S("InParent", DUtil.Names(leaf.GetComponentsInParent<DProbeMarker>()));
                o.S("InParentAll", DUtil.Names(leaf.GetComponentsInParent<DProbeMarker>(true)));
                var first = root.GetComponentInChildren<DProbeMarker>(); o.S("InChildrenFirst", first == null ? "null" : first.name);
                var firstAll = root.GetComponentInChildren<DProbeMarker>(true); o.S("InChildrenFirstAll", firstAll == null ? "null" : firstAll.name);
                var p = leaf.GetComponentInParent<DProbeMarker>(); o.S("InParentFirst", p == null ? "null" : p.name);
                var list = new List<DProbeMarker>(); root.GetComponentsInChildren(true, list); o.I("ListCount", list.Count);
                o.I("Components", leaf.GetComponents<Component>().Length);
                DProbeMarker2 tc; o.B("TryGetComponent", leaf.TryGetComponent(out tc));
            }
            finally { DUtil.Cleanup(made); }
        }
    }

    /// <summary>Tags, layers, names: CompareTag, tag / layer round trips, GameObject.Find paths, FindWithTag on built-in tags, Object.ToString.</summary>
    public static class D_TagsNames
    {
        public static void Register() { Suites.All["d-obj-tags"] = Case; }
        static readonly string[] Tags = { "Untagged", "MainCamera", "Player", "Respawn", "Finish", "EditorOnly", "GameController" };
        static void Case(Rng r, Out o)
        {
            var made = new List<GameObject>();
            try
            {
                var a = new GameObject("tA" + r.Int(100)); made.Add(a); var b = new GameObject("tB"); made.Add(b); b.transform.SetParent(a.transform, false);
                var c = new GameObject("tC"); made.Add(c); c.transform.SetParent(b.transform, false);
                string tag = r.Pick(Tags); try { a.tag = tag; } catch (Exception e) { o.Exc("setTag", e); }
                o.S("tag", a.tag); o.B("CompareTag", a.CompareTag(tag)); o.B("CompareUntagged", b.CompareTag("Untagged"));
                try { o.B("CompareBogus", a.CompareTag("NoSuchTag" + r.Int(9))); } catch (Exception e) { o.Exc("CompareBogus", e); }
                int layer = r.Int(32); a.layer = layer; o.I("layer", a.layer);
                try { b.layer = 40; o.I("layer40", b.layer); } catch (Exception e) { o.Exc("layer40", e); }
                o.S("FindPath", GameObject.Find(a.name + "/tB/tC") == c ? "tC" : "?");
                o.S("FindLeaf", GameObject.Find("tC") == c ? "tC" : "?");
                o.S("FindSlash", GameObject.Find("/" + a.name) == a ? "A" : "?");
                o.S("FindMissing", GameObject.Find("nope") == null ? "null" : "?");
                c.SetActive(false); o.S("FindInactive", GameObject.Find(a.name + "/tB/tC") == null ? "null" : "found");
                var w = GameObject.FindWithTag(tag); o.S("FindWithTag", w == null ? "null" : (w == a ? "A" : w.name));
                o.S("ToString", a.ToString()); o.S("TransformToString", c.transform.ToString()); o.S("CompToString", a.AddComponent<DProbeMarker2>().ToString());
                o.I("hideFlags", (int)a.hideFlags); a.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave; o.I("hideFlags2", (int)a.hideFlags);
                o.S("nameRoundTrip", (a.name = "xéy" + r.Int(5)));
            }
            finally { DUtil.Cleanup(made); }
        }
    }

    /// <summary>Instantiate naming / parenting / component copy; Destroy vs DestroyImmediate visibility; == null semantics.</summary>
    public static class D_Instantiate
    {
        public static void Register() { Suites.All["d-obj-instantiate"] = Case; }
        static void Case(Rng r, Out o)
        {
            var made = new List<GameObject>();
            try
            {
                var src = new GameObject("src" + r.Int(10)); made.Add(src); var m = src.AddComponent<DProbeMarker>(); m.Id = r.Int(1000);
                var kid = new GameObject("kid"); made.Add(kid); kid.transform.SetParent(src.transform, false); kid.transform.localPosition = new Vector3(r.Int(9), 0, 0);
                var c1 = UnityEngine.Object.Instantiate(src); made.Add(c1);
                o.S("name", c1.name); o.I("childCount", c1.transform.childCount); o.S("kidName", c1.transform.GetChild(0).name); o.I("id", c1.GetComponent<DProbeMarker>().Id);
                var c2 = UnityEngine.Object.Instantiate(c1); made.Add(c2); o.S("name2", c2.name);
                var c3 = UnityEngine.Object.Instantiate(m); made.Add(c3.gameObject); o.S("compClone", c3.gameObject.name); o.B("compCloneIsComp", c3 is DProbeMarker);
                var c4 = UnityEngine.Object.Instantiate(src, new Vector3(1, 2, 3), Quaternion.Euler(0, 90, 0)); made.Add(c4);
                o.Raw("pos4", Out.H(c4.transform.position.x) + "," + Out.H(c4.transform.position.y) + "," + Out.H(c4.transform.position.z));
                o.Raw("kid4", Out.H(c4.transform.GetChild(0).position.x) + "," + Out.H(c4.transform.GetChild(0).position.z));
                UnityEngine.Object.DestroyImmediate(c2); o.B("destroyedNull", c2 == null); o.B("destroyedRefNull", ReferenceEquals(c2, null));
                o.B("boolOp", c1 ? true : false); o.S("destroyedName", SafeName(c2));
            }
            finally { DUtil.Cleanup(made); }
        }
        static string SafeName(GameObject g) { try { return g.name; } catch (Exception e) { return "EXC:" + e.GetType().Name; } }
    }

    /// <summary>Behaviour.enabled / isActiveAndEnabled and GameObject.activeInHierarchy under nested activation changes.</summary>
    public static class D_Activation
    {
        public static void Register() { Suites.All["d-obj-activation"] = Case; }
        static void Case(Rng r, Out o)
        {
            var made = new List<GameObject>();
            try
            {
                var chain = new List<GameObject>();
                for (int i = 0; i < 4; i++) { var g = new GameObject("a" + i); made.Add(g); if (i > 0) g.transform.SetParent(chain[i - 1].transform, false); chain.Add(g); }
                var b = chain[3].AddComponent<DProbeMarker>();
                for (int step = 0; step < 10; step++)
                {
                    int k = r.Int(5);
                    if (k < 4) chain[k].SetActive(r.Bool()); else b.enabled = r.Bool();
                    o.Raw("s" + step, k + ":" + (chain[0].activeSelf ? 1 : 0) + (chain[1].activeSelf ? 1 : 0) + (chain[2].activeSelf ? 1 : 0) + (chain[3].activeSelf ? 1 : 0)
                        + "|" + (chain[3].activeInHierarchy ? 1 : 0) + "|" + (b.enabled ? 1 : 0) + (b.isActiveAndEnabled ? 1 : 0));
                }
            }
            finally { DUtil.Cleanup(made); }
        }
    }
}
