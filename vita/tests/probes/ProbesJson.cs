// Kit #7 tests track: seeded property-based tests of JsonUtility (vita/notes/kit7-tests.md "Seeded property-based tests vs Unity"):
// random instances of serializable shapes -> ToJson (compact and pretty), FromJson back, ToJson again, FromJsonOverwrite onto a
// pre-filled object; random JSON text (valid, reordered, missing / extra / wrongly typed fields, whitespace) -> FromJson. Every output
// line is exact (the JSON text itself; floats as raw bits), so the suite hash equals Unity's only if Shepherd's JsonUtility (an engine
// reimplementation) writes and reads exactly what Unity's native one does.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ShepherdOracle
{
    public enum JEnum { Zero, One, Two, Big = 1000 }
    [Serializable] public class JInner { public int i; public float f; public string s; public JEnum e; }
    [Serializable] public struct JStruct { public int a; public float b; public Vector2 v; }
    [Serializable] public class JAll
    {
        public int i; public long l; public float f; public double d; public bool b; public string s; public byte by; public short sh; public uint ui; public char c;
        public JEnum e; public Vector2 v2; public Vector3 v3; public Vector4 v4; public Quaternion q; public Color col; public Color32 c32; public Rect rect; public Bounds bounds;
        public Vector2Int v2i; public Vector3Int v3i; public RectInt ri; public LayerMask mask; public AnimationCurve curve; public Gradient grad;
        public int[] ia; public List<float> fl; public string[] sa; public JInner inner; public JInner[] inners; public List<JInner> innerList; public JStruct st; public JStruct[] sts;
        [SerializeField] private int priv; public int Priv { get { return priv; } set { priv = value; } }
        [NonSerialized] public int skipped; public int Prop { get; set; }
        public Dictionary<string, int> dict;   // never serialized by Unity
    }
    [Serializable] public class JSmall { public int a; public float b; public string c; public int[] d; public JInner e; }

    public static class ProbesJson
    {
        public static void Register()
        {
            Suites.All["json-roundtrip"] = RoundTrip; Suites.Hashed.Add("json-roundtrip");
            Suites.All["json-parse"] = ParseText; Suites.Hashed.Add("json-parse");
        }

        static JInner Inner(Rng r) => r.Chance(15) ? null : new JInner { i = Gen.I(r), f = Gen.F(r), s = r.Chance(10) ? null : Gen.S(r, 8), e = (JEnum)r.Pick(new[] { 0, 1, 2, 1000, 7, -1 }) };
        static JAll Make(Rng r)
        {
            var a = new JAll
            {
                i = Gen.I(r), l = Gen.L(r), f = Gen.F(r), d = Gen.D(r), b = r.Bool(), s = r.Chance(10) ? null : Gen.S(r), by = (byte)r.Int(256), sh = (short)Gen.I(r), ui = r.U32(), c = (char)r.Pick(new[] { 'a', 'Z', '"', '\\', '\n', (char)0x4e2d, (char)0 }),
                e = (JEnum)r.Pick(new[] { 0, 1, 2, 1000, 5 }), v2 = new Vector2(Gen.F(r), Gen.FM(r)), v3 = new Vector3(Gen.FM(r), Gen.F(r), Gen.FM(r)), v4 = new Vector4(Gen.FM(r), 1f, Gen.F(r), 0f),
                q = new Quaternion(r.Unit(), r.Unit(), r.Unit(), r.Unit()), col = new Color(r.Unit(), Gen.F(r), 0.5f, 1f), c32 = new Color32((byte)r.Int(256), (byte)r.Int(256), 7, 255),
                rect = new Rect(Gen.FM(r), Gen.FM(r), Gen.FM(r), Gen.FM(r)), bounds = new Bounds(new Vector3(Gen.FM(r), 0f, 1f), new Vector3(2f, Gen.FM(r), 3f)),
                v2i = new Vector2Int(Gen.I(r), 3), v3i = new Vector3Int(1, Gen.I(r), -2), ri = new RectInt(1, 2, Gen.I(r), 4), mask = Gen.I(r), Prop = 5, skipped = 9,
            };
            if (r.Chance(60)) { a.curve = new AnimationCurve(new Keyframe(0f, Gen.FM(r)), new Keyframe(r.Unit() + 0.5f, Gen.FM(r), r.Unit(), r.Unit())); }
            if (r.Chance(40)) { a.grad = new Gradient(); a.grad.SetKeys(new[] { new GradientColorKey(Color.red, 0f), new GradientColorKey(new Color(r.Unit(), 0.25f, 1f), r.Unit()) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(r.Unit(), 1f) }); }
            if (r.Chance(80)) { a.ia = new int[r.Int(5)]; for (int k = 0; k < a.ia.Length; k++) a.ia[k] = Gen.I(r); }
            if (r.Chance(70)) { a.fl = new List<float>(); int n = r.Int(4); for (int k = 0; k < n; k++) a.fl.Add(Gen.F(r)); }
            if (r.Chance(60)) { a.sa = new string[r.Int(3)]; for (int k = 0; k < a.sa.Length; k++) a.sa[k] = r.Chance(20) ? null : Gen.S(r, 5); }
            a.inner = Inner(r);
            if (r.Chance(50)) { a.inners = new JInner[r.Int(3)]; for (int k = 0; k < a.inners.Length; k++) a.inners[k] = Inner(r); }
            if (r.Chance(40)) { a.innerList = new List<JInner>(); a.innerList.Add(Inner(r)); }
            a.st = new JStruct { a = Gen.I(r), b = Gen.F(r), v = new Vector2(1f, Gen.FM(r)) };
            if (r.Chance(30)) a.sts = new[] { new JStruct { a = 1 }, new JStruct { b = Gen.F(r) } };
            a.Priv = Gen.I(r);
            if (r.Chance(30)) a.dict = new Dictionary<string, int> { { "k", 1 } };
            return a;
        }
        static string Describe(JAll a)
        {
            if (a == null) return "<null>";
            var sb = new StringBuilder();
            sb.Append(a.i).Append('|').Append(a.l).Append('|').Append(Out.H(a.f)).Append('|').Append(Out.H(a.d)).Append('|').Append(a.b).Append('|').Append(Out.Esc(a.s)).Append('|').Append(a.by).Append('|').Append(a.sh).Append('|').Append(a.ui).Append('|').Append((int)a.c);
            sb.Append('|').Append((int)a.e).Append('|').Append(Out.H(a.v2.x)).Append(',').Append(Out.H(a.v2.y)).Append('|').Append(Out.H(a.v3.x)).Append(',').Append(Out.H(a.v3.y)).Append(',').Append(Out.H(a.v3.z));
            sb.Append('|').Append(Out.H(a.q.w)).Append('|').Append(Out.H(a.col.g)).Append('|').Append(a.c32.r).Append('|').Append(Out.H(a.rect.width)).Append('|').Append(Out.H(a.bounds.size.y)).Append('|').Append(a.v2i.x).Append('|').Append(a.v3i.y).Append('|').Append(a.ri.width).Append('|').Append(a.mask.value);
            sb.Append("|curve:").Append(a.curve == null ? "null" : a.curve.length + ":" + (a.curve.length > 1 ? Out.H(a.curve.keys[1].value) + "," + Out.H(a.curve.keys[1].inTangent) : ""));
            sb.Append("|grad:").Append(a.grad == null ? "null" : a.grad.colorKeys.Length + "," + a.grad.alphaKeys.Length + "," + (int)a.grad.mode);
            sb.Append("|ia:").Append(a.ia == null ? "null" : string.Join(",", Array.ConvertAll(a.ia, x => x.ToString())));
            sb.Append("|fl:").Append(a.fl == null ? "null" : string.Join(",", a.fl.ConvertAll(x => Out.H(x)).ToArray()));
            sb.Append("|sa:").Append(a.sa == null ? "null" : string.Join(",", Array.ConvertAll(a.sa, Out.Esc)));
            sb.Append("|inner:").Append(a.inner == null ? "null" : a.inner.i + "," + Out.H(a.inner.f) + "," + Out.Esc(a.inner.s) + "," + (int)a.inner.e);
            sb.Append("|inners:").Append(a.inners == null ? "null" : a.inners.Length.ToString()).Append("|innerList:").Append(a.innerList == null ? "null" : a.innerList.Count.ToString());
            sb.Append("|st:").Append(a.st.a).Append(',').Append(Out.H(a.st.b)).Append("|sts:").Append(a.sts == null ? "null" : a.sts.Length.ToString());
            sb.Append("|priv:").Append(a.Priv).Append("|skipped:").Append(a.skipped).Append("|prop:").Append(a.Prop).Append("|dict:").Append(a.dict == null ? "null" : "set");
            return sb.ToString();
        }
        static void T(Out o, string l, Func<string> f) { try { o.S(l, f()); } catch (Exception e) { o.Exc(l, e); } }

        static void RoundTrip(Rng r, Out o)
        {
            var a = Make(r);
            string j1 = null, jp = null; JAll back = null;
            T(o, "describe", () => Describe(a));
            T(o, "toJson", () => j1 = JsonUtility.ToJson(a));
            T(o, "toJsonPretty", () => jp = JsonUtility.ToJson(a, true));
            T(o, "fromJson", () => { back = JsonUtility.FromJson<JAll>(j1); return Describe(back); });
            T(o, "toJson2", () => JsonUtility.ToJson(back));
            T(o, "fromPretty", () => Describe(JsonUtility.FromJson<JAll>(jp)));
            T(o, "overwrite", () => { var x = Make(new Rng(7)); JsonUtility.FromJsonOverwrite(j1, x); return Describe(x); });
            T(o, "small", () => { var s = new JSmall { a = Gen.I(r), b = Gen.F(r), c = Gen.S(r, 4), d = r.Chance(50) ? new[] { 1, 2 } : null, e = Inner(r) }; return JsonUtility.ToJson(s); });
            T(o, "inner", () => JsonUtility.ToJson(a.inner));
            T(o, "struct", () => JsonUtility.ToJson(a.st));
            T(o, "vector", () => JsonUtility.ToJson(a.v3));
            T(o, "null", () => JsonUtility.ToJson(null));
        }

        static void ParseText(Rng r, Out o)
        {
            // random JSON text for JSmall: fields in random order, some missing, some extra, some of the wrong type, odd whitespace / numbers
            var parts = new List<string>();
            string[] ints = { "1", "-7", "2147483647", "2147483648", "1.9", "-0", "1e2", "\"5\"", "true", "null", "0x10", "007", "[]" };
            string[] floats = { "1.5", "-0.0", "1e-46", "3.5e38", "NaN", "\"Infinity\"", "1", ".5", "5.", "1E+2", "\"1.25\"", "null", "{}" };
            string[] strs = { "\"x\"", "\"\"", "\"a\\\"b\"", "\"\\u4e2d\"", "\"\\n\\t\"", "null", "5", "\"\\ud800\"", "true" };
            string[] arrs = { "[1,2,3]", "[]", "null", "[1.5]", "[\"a\"]", "5", "[1,[2]]", "[ 1 , 2 ]" };
            string[] objs = { "{\"i\":3,\"f\":0.5,\"s\":\"q\",\"e\":2}", "{}", "null", "{\"i\":\"no\"}", "[]", "{\"e\":1000,\"zz\":1}" };
            if (r.Chance(85)) parts.Add("\"a\":" + r.Pick(ints));
            if (r.Chance(85)) parts.Add("\"b\":" + r.Pick(floats));
            if (r.Chance(85)) parts.Add("\"c\":" + r.Pick(strs));
            if (r.Chance(70)) parts.Add("\"d\":" + r.Pick(arrs));
            if (r.Chance(70)) parts.Add("\"e\":" + r.Pick(objs));
            if (r.Chance(30)) parts.Add("\"extra\":" + r.Pick(objs));
            if (r.Chance(10)) parts.Add("\"a\":" + r.Pick(ints));   // duplicate key
            for (int k = parts.Count - 1; k > 0; k--) { int j = r.Int(k + 1); var t = parts[k]; parts[k] = parts[j]; parts[j] = t; }
            string ws = r.Pick(new[] { "", " ", "\n  ", "\t" });
            string json = r.Chance(5) ? r.Pick(new[] { "", "null", "[]", "{", "{\"a\":1", "garbage", "{\"a\":1}x" }) : "{" + ws + string.Join("," + ws, parts.ToArray()) + ws + "}";
            o.S("in.json", json);
            T(o, "fromJson", () => { var s = JsonUtility.FromJson<JSmall>(json); return s == null ? "<null>" : s.a + "|" + Out.H(s.b) + "|" + Out.Esc(s.c) + "|" + (s.d == null ? "null" : string.Join(",", Array.ConvertAll(s.d, x => x.ToString()))) + "|" + (s.e == null ? "null" : s.e.i + "," + Out.H(s.e.f) + "," + Out.Esc(s.e.s) + "," + (int)s.e.e); });
            T(o, "rewrite", () => JsonUtility.ToJson(JsonUtility.FromJson<JSmall>(json)));
        }
    }
}
