// Kit #7 tests track: helpers shared by the machine-drafted probes (no suite of its own; drafted/filter.sh never rejects it).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShepherdOracle.Drafted
{
    public static class DUtil
    {
        public static void Cleanup(List<GameObject> l) { foreach (var g in l) if (g != null && g.transform.parent == null) UnityEngine.Object.DestroyImmediate(g); foreach (var g in l) if (g != null) UnityEngine.Object.DestroyImmediate(g); }
        public static string Names(IEnumerable<Component> cs) { var l = new List<string>(); foreach (var c in cs) l.Add(c == null ? "null" : c.gameObject.name); return string.Join(",", l.ToArray()); }
    }
    public static class DW
    {
        public static string V(Vector3 v) => Out.H(v.x) + "," + Out.H(v.y) + "," + Out.H(v.z);
        public static string V(Vector4 v) => Out.H(v.x) + "," + Out.H(v.y) + "," + Out.H(v.z) + "," + Out.H(v.w);
        public static string C(Color c) => Out.H(c.r) + "," + Out.H(c.g) + "," + Out.H(c.b) + "," + Out.H(c.a);
    }
}
