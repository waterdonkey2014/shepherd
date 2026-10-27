// Kit #7 tests track: differential fuzzing suites for Unity's maths (vita/notes/kit7-tests.md "Differential fuzzing against Unity").
// Each case draws its inputs from its own seeded Rng and writes every result's raw bits; the Unity 2022.3 player and Shepherd must agree on
// every line. Covers the managed maths (identical IL on both sides: a difference there is a runtime / compiler difference) and the native
// ones Shepherd reimplements as icalls (Quaternion.Euler / eulerAngles / AngleAxis / LookRotation / FromToRotation / Inverse / Slerp / Lerp,
// Matrix4x4 TRS / inverse / determinant / Perspective / Ortho / LookAt / rotation / lossyScale, Vector3 Slerp / RotateTowards /
// OrthoNormalize, Mathf.PerlinNoise / ClosestPowerOfTwo / NextPowerOfTwo / IsPowerOfTwo / GammaToLinearSpace / LinearToGammaSpace /
// CorrelatedColorTemperatureToRGB / FloatToHalf / HalfToFloat, UnityEngine.Random).
using System;
using UnityEngine;

namespace ShepherdOracle
{
    public static class ProbesMath
    {
        static Vector2 V2(Rng r) => new Vector2(Gen.FM(r), Gen.FM(r));
        static Vector3 V3(Rng r) => r.Chance(8) ? new Vector3(Gen.F(r), Gen.F(r), Gen.F(r)) : new Vector3(Gen.FM(r), Gen.FM(r), Gen.FM(r));
        static Vector3 Dir(Rng r) => r.Chance(10) ? r.Pick(new[] { Vector3.up, Vector3.down, Vector3.forward, Vector3.right, Vector3.zero, new Vector3(0f, 0f, -1f) }) : new Vector3(r.Exact(-23), r.Exact(-23), r.Exact(-23));
        static Vector4 V4(Rng r) => new Vector4(Gen.FM(r), Gen.FM(r), Gen.FM(r), Gen.FM(r));
        static Quaternion Q(Rng r)
        {
            int k = r.Int(10);
            if (k == 0) return Quaternion.identity;
            if (k == 1) return new Quaternion(Gen.F(r), Gen.F(r), Gen.F(r), Gen.F(r));   // not normalised, specials
            var q = new Quaternion(r.Exact(-23), r.Exact(-23), r.Exact(-23), r.Exact(-23));
            if (k < 8) { float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w); if (m > 0f) q = new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m); }
            return q;
        }
        static void W(Out o, string l, Vector2 v) { o.Raw(l, Out.H(v.x) + "," + Out.H(v.y)); }
        static void W(Out o, string l, Vector3 v) { o.Raw(l, Out.H(v.x) + "," + Out.H(v.y) + "," + Out.H(v.z)); }
        static void W(Out o, string l, Vector4 v) { o.Raw(l, Out.H(v.x) + "," + Out.H(v.y) + "," + Out.H(v.z) + "," + Out.H(v.w)); }
        static void W(Out o, string l, Quaternion q) { o.Raw(l, Out.H(q.x) + "," + Out.H(q.y) + "," + Out.H(q.z) + "," + Out.H(q.w)); }
        static void W(Out o, string l, Color c) { o.Raw(l, Out.H(c.r) + "," + Out.H(c.g) + "," + Out.H(c.b) + "," + Out.H(c.a)); }
        static void W(Out o, string l, Matrix4x4 m)
        {
            var sb = new System.Text.StringBuilder(150);
            for (int i = 0; i < 16; i++) { if (i > 0) sb.Append(','); sb.Append(Out.H(m[i])); }
            o.Raw(l, sb.ToString());
        }
        /// <summary>Run f, writing its result or the exception's type.</summary>
        static void T(Out o, string l, Func<float> f) { try { o.F(l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<int> f) { try { o.I(l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<bool> f) { try { o.B(l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<Vector2> f) { try { W(o, l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<Vector3> f) { try { W(o, l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<Vector4> f) { try { W(o, l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<Quaternion> f) { try { W(o, l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<Matrix4x4> f) { try { W(o, l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<Color> f) { try { W(o, l, f()); } catch (Exception e) { o.Exc(l, e); } }
        static void T(Out o, string l, Func<string> f) { try { o.S(l, f()); } catch (Exception e) { o.Exc(l, e); } }

        public static void Register()
        {
            Suites.All["mathf"] = Mathf_;
            Suites.All["mathf-native"] = MathfNative;
            Suites.All["vec2"] = Vec2;
            Suites.All["vec3"] = Vec3;
            Suites.All["vec4"] = Vec4;
            Suites.All["quat"] = Quat;
            Suites.All["matrix"] = Matrix;
            Suites.All["color"] = Col;
            Suites.All["random"] = Rand;
        }

        static void Mathf_(Rng r, Out o)
        {
            float a = Gen.F(r), b = Gen.F(r), t = Gen.F01(r), m = Gen.FM(r), n = Gen.FM(r);
            o.F("in.a", a); o.F("in.b", b); o.F("in.t", t); o.F("in.m", m); o.F("in.n", n);
            T(o, "Sin", () => Mathf.Sin(a)); T(o, "Cos", () => Mathf.Cos(a)); T(o, "Tan", () => Mathf.Tan(a));
            T(o, "Asin", () => Mathf.Asin(t)); T(o, "Acos", () => Mathf.Acos(t)); T(o, "Atan", () => Mathf.Atan(a)); T(o, "Atan2", () => Mathf.Atan2(a, b));
            T(o, "Sqrt", () => Mathf.Sqrt(a)); T(o, "Abs", () => Mathf.Abs(a)); T(o, "Min", () => Mathf.Min(a, b)); T(o, "Max", () => Mathf.Max(a, b));
            T(o, "Pow", () => Mathf.Pow(a, b)); T(o, "Exp", () => Mathf.Exp(m * 0.01f)); T(o, "Log", () => Mathf.Log(a)); T(o, "LogP", () => Mathf.Log(a, b)); T(o, "Log10", () => Mathf.Log10(a));
            T(o, "Ceil", () => Mathf.Ceil(a)); T(o, "Floor", () => Mathf.Floor(a)); T(o, "Round", () => Mathf.Round(a));
            T(o, "CeilToInt", () => Mathf.CeilToInt(m)); T(o, "FloorToInt", () => Mathf.FloorToInt(m)); T(o, "RoundToInt", () => Mathf.RoundToInt(m)); T(o, "RoundHalf", () => Mathf.RoundToInt(Mathf.Floor(m) + 0.5f));
            T(o, "Sign", () => Mathf.Sign(a)); T(o, "Clamp", () => Mathf.Clamp(a, Mathf.Min(m, n), Mathf.Max(m, n))); T(o, "Clamp01", () => Mathf.Clamp01(a));
            T(o, "ClampI", () => Mathf.Clamp((int)m, -100, 100));
            T(o, "Lerp", () => Mathf.Lerp(m, n, t)); T(o, "LerpU", () => Mathf.LerpUnclamped(m, n, a)); T(o, "LerpAngle", () => Mathf.LerpAngle(m, n, t));
            T(o, "MoveTowards", () => Mathf.MoveTowards(m, n, Mathf.Abs(b))); T(o, "MoveTowardsAngle", () => Mathf.MoveTowardsAngle(m, n, t * 90f));
            T(o, "SmoothStep", () => Mathf.SmoothStep(m, n, t)); T(o, "Gamma", () => Mathf.Gamma(t, 1f, 2.2f)); T(o, "Approximately", () => Mathf.Approximately(a, b) ? 1 : 0);
            T(o, "Repeat", () => Mathf.Repeat(m, Mathf.Abs(n) + 1f)); T(o, "PingPong", () => Mathf.PingPong(m, Mathf.Abs(n) + 1f)); T(o, "InverseLerp", () => Mathf.InverseLerp(m, n, a));
            T(o, "DeltaAngle", () => Mathf.DeltaAngle(m, n));
            float vel = r.Exact(-20); float sd = 0f;
            T(o, "SmoothDamp", () => { sd = Mathf.SmoothDamp(m, n, ref vel, t + 0.01f, 1000f, 1f / 60f); return sd; }); o.F("SmoothDamp.vel", vel);
            float vel2 = 0f;
            T(o, "SmoothDampAngle", () => Mathf.SmoothDampAngle(m, n, ref vel2, 0.3f, float.PositiveInfinity, 0.02f)); o.F("SmoothDampAngle.vel", vel2);
        }

        static void MathfNative(Rng r, Out o)
        {
            float a = Gen.F(r), x = r.Exact(-16), y = r.Exact(-16); int i = Gen.I(r); float t = Gen.F01(r);
            o.F("in.a", a); o.F("in.x", x); o.F("in.y", y); o.I("in.i", i); o.F("in.t", t);
            T(o, "PerlinNoise", () => Mathf.PerlinNoise(x, y));
            T(o, "PerlinNoiseA", () => Mathf.PerlinNoise(a, x));
            T(o, "ClosestPowerOfTwo", () => Mathf.ClosestPowerOfTwo(i)); T(o, "NextPowerOfTwo", () => Mathf.NextPowerOfTwo(i)); T(o, "IsPowerOfTwo", () => Mathf.IsPowerOfTwo(i));
            T(o, "GammaToLinear", () => Mathf.GammaToLinearSpace(t)); T(o, "LinearToGamma", () => Mathf.LinearToGammaSpace(t));
            T(o, "GammaToLinearA", () => Mathf.GammaToLinearSpace(a)); T(o, "LinearToGammaA", () => Mathf.LinearToGammaSpace(a));
            T(o, "CCT", () => Mathf.CorrelatedColorTemperatureToRGB(1000f + Mathf.Abs(x) * 40f));
            T(o, "FloatToHalf", () => (int)Mathf.FloatToHalf(a)); T(o, "HalfToFloat", () => Mathf.HalfToFloat((ushort)i));
            T(o, "HalfRound", () => Mathf.HalfToFloat(Mathf.FloatToHalf(a)));
        }

        static void Vec2(Rng r, Out o)
        {
            Vector2 a = V2(r), b = V2(r); float t = Gen.F01(r), s = Gen.FM(r);
            W(o, "in.a", a); W(o, "in.b", b); o.F("in.t", t); o.F("in.s", s);
            T(o, "normalized", () => a.normalized); T(o, "magnitude", () => a.magnitude); T(o, "sqrMagnitude", () => a.sqrMagnitude);
            T(o, "Dot", () => Vector2.Dot(a, b)); T(o, "Angle", () => Vector2.Angle(a, b)); T(o, "SignedAngle", () => Vector2.SignedAngle(a, b));
            T(o, "Distance", () => Vector2.Distance(a, b)); T(o, "ClampMagnitude", () => Vector2.ClampMagnitude(a, Mathf.Abs(s)));
            T(o, "Lerp", () => Vector2.Lerp(a, b, t)); T(o, "MoveTowards", () => Vector2.MoveTowards(a, b, Mathf.Abs(s)));
            T(o, "Reflect", () => Vector2.Reflect(a, b.normalized)); T(o, "Perpendicular", () => Vector2.Perpendicular(a)); T(o, "Scale", () => Vector2.Scale(a, b));
            Vector2 vel = Vector2.zero; T(o, "SmoothDamp", () => Vector2.SmoothDamp(a, b, ref vel, 0.25f, 50f, 1f / 30f)); W(o, "SmoothDamp.vel", vel);
            T(o, "ToString", () => a.ToString()); T(o, "ToStringF3", () => a.ToString("F3"));
            T(o, "eq", () => a == b); T(o, "eqNear", () => a == a + new Vector2(1e-6f, 0f));
        }

        static void Vec3(Rng r, Out o)
        {
            Vector3 a = V3(r), b = V3(r), c = V3(r); float t = Gen.F01(r), s = Gen.FM(r);
            W(o, "in.a", a); W(o, "in.b", b); W(o, "in.c", c); o.F("in.t", t); o.F("in.s", s);
            T(o, "normalized", () => a.normalized); T(o, "magnitude", () => a.magnitude); T(o, "Normalize", () => Vector3.Normalize(a));
            T(o, "Dot", () => Vector3.Dot(a, b)); T(o, "Cross", () => Vector3.Cross(a, b)); T(o, "Angle", () => Vector3.Angle(a, b)); T(o, "SignedAngle", () => Vector3.SignedAngle(a, b, c));
            T(o, "Distance", () => Vector3.Distance(a, b)); T(o, "ClampMagnitude", () => Vector3.ClampMagnitude(a, Mathf.Abs(s)));
            T(o, "Lerp", () => Vector3.Lerp(a, b, t)); T(o, "LerpU", () => Vector3.LerpUnclamped(a, b, s * 0.01f));
            T(o, "Slerp", () => Vector3.Slerp(a, b, t)); T(o, "SlerpU", () => Vector3.SlerpUnclamped(a, b, s * 0.01f));
            T(o, "RotateTowards", () => Vector3.RotateTowards(a, b, t * 3.1415927f, Mathf.Abs(s)));
            T(o, "MoveTowards", () => Vector3.MoveTowards(a, b, Mathf.Abs(s)));
            T(o, "Project", () => Vector3.Project(a, b)); T(o, "ProjectOnPlane", () => Vector3.ProjectOnPlane(a, b)); T(o, "Reflect", () => Vector3.Reflect(a, b.normalized));
            T(o, "Scale", () => Vector3.Scale(a, b)); T(o, "Min", () => Vector3.Min(a, b)); T(o, "Max", () => Vector3.Max(a, b));
            Vector3 n1 = a, t1 = b; T(o, "OrthoNormalize", () => { Vector3.OrthoNormalize(ref n1, ref t1); return n1; }); W(o, "OrthoNormalize.t", t1);
            Vector3 n2 = a, t2 = b, b2 = c; T(o, "OrthoNormalize3", () => { Vector3.OrthoNormalize(ref n2, ref t2, ref b2); return b2; }); W(o, "OrthoNormalize3.n", n2); W(o, "OrthoNormalize3.t", t2);
            Vector3 vel = Vector3.zero; T(o, "SmoothDamp", () => Vector3.SmoothDamp(a, b, ref vel, 0.3f, 100f, 1f / 60f)); W(o, "SmoothDamp.vel", vel);
            T(o, "ToString", () => a.ToString()); T(o, "ToStringF1", () => a.ToString("F1"));
            T(o, "eq", () => a == b); T(o, "eqNear", () => a == a + new Vector3(0f, 2e-6f, 0f)); T(o, "hash", () => a.GetHashCode());
        }

        static void Vec4(Rng r, Out o)
        {
            Vector4 a = V4(r), b = V4(r); float t = Gen.F01(r);
            W(o, "in.a", a); W(o, "in.b", b); o.F("in.t", t);
            T(o, "normalized", () => a.normalized); T(o, "magnitude", () => a.magnitude); T(o, "Dot", () => Vector4.Dot(a, b)); T(o, "Lerp", () => Vector4.Lerp(a, b, t));
            T(o, "Project", () => Vector4.Project(a, b)); T(o, "MoveTowards", () => Vector4.MoveTowards(a, b, t * 10f)); T(o, "ToString", () => a.ToString());
        }

        static void Quat(Rng r, Out o)
        {
            Quaternion a = Q(r), b = Q(r); Vector3 e = V3(r), d1 = Dir(r), d2 = Dir(r), v = V3(r); float t = Gen.F01(r), ang = Gen.FM(r);
            W(o, "in.a", a); W(o, "in.b", b); W(o, "in.e", e); W(o, "in.d1", d1); W(o, "in.d2", d2); W(o, "in.v", v); o.F("in.t", t); o.F("in.ang", ang);
            T(o, "Euler", () => Quaternion.Euler(e)); T(o, "Euler3", () => Quaternion.Euler(e.x, e.y, e.z));
            T(o, "eulerAngles", () => a.eulerAngles); T(o, "eulerRound", () => Quaternion.Euler(e).eulerAngles);
            T(o, "AngleAxis", () => Quaternion.AngleAxis(ang, d1));
            T(o, "ToAngleAxis", () => { float an; Vector3 ax; a.ToAngleAxis(out an, out ax); return new Vector4(an, ax.x, ax.y, ax.z); });
            T(o, "LookRotation", () => Quaternion.LookRotation(d1)); T(o, "LookRotationUp", () => Quaternion.LookRotation(d1, d2));
            T(o, "FromToRotation", () => Quaternion.FromToRotation(d1, d2));
            T(o, "Inverse", () => Quaternion.Inverse(a)); T(o, "Slerp", () => Quaternion.Slerp(a, b, t)); T(o, "SlerpU", () => Quaternion.SlerpUnclamped(a, b, ang * 0.01f));
            T(o, "Lerp", () => Quaternion.Lerp(a, b, t)); T(o, "LerpU", () => Quaternion.LerpUnclamped(a, b, ang * 0.01f));
            T(o, "RotateTowards", () => Quaternion.RotateTowards(a, b, Mathf.Abs(ang)));
            T(o, "Angle", () => Quaternion.Angle(a, b)); T(o, "Dot", () => Quaternion.Dot(a, b));
            T(o, "mul", () => a * b); T(o, "mulV", () => a * v); T(o, "normalized", () => a.normalized); T(o, "Normalize", () => Quaternion.Normalize(a));
            T(o, "eq", () => a == b); T(o, "eqSelf", () => a == a); T(o, "ToString", () => a.ToString());
            T(o, "SetFromTo", () => { var q = Quaternion.identity; q.SetFromToRotation(d1, d2); return q; });
            T(o, "SetLook", () => { var q = Quaternion.identity; q.SetLookRotation(d1, d2); return q; });
        }

        static void Matrix(Rng r, Out o)
        {
            Vector3 p = V3(r), s = r.Chance(15) ? new Vector3(Gen.F(r), Gen.F(r), Gen.F(r)) : new Vector3(r.Exact(-20), r.Exact(-20), r.Exact(-20)), v = V3(r), up = Dir(r), tgt = V3(r);
            Quaternion q = Q(r);
            float fov = 1f + r.Unit() * 170f, aspect = 0.25f + r.Unit() * 4f, near = 0.01f + r.Unit(), far = near + 1f + r.Unit() * 1000f;
            W(o, "in.p", p); W(o, "in.s", s); W(o, "in.v", v); W(o, "in.q", q); W(o, "in.up", up); W(o, "in.tgt", tgt); o.F("in.fov", fov); o.F("in.aspect", aspect); o.F("in.near", near); o.F("in.far", far);
            Matrix4x4 m = Matrix4x4.identity;
            T(o, "TRS", () => { m = Matrix4x4.TRS(p, q, s); return m; });
            T(o, "inverse", () => m.inverse); T(o, "determinant", () => m.determinant); T(o, "transpose", () => m.transpose);
            T(o, "rotation", () => m.rotation); T(o, "lossyScale", () => m.lossyScale); T(o, "isIdentity", () => m.isIdentity); T(o, "ValidTRS", () => m.ValidTRS());
            T(o, "MultiplyPoint", () => m.MultiplyPoint(v)); T(o, "MultiplyPoint3x4", () => m.MultiplyPoint3x4(v)); T(o, "MultiplyVector", () => m.MultiplyVector(v));
            T(o, "mul", () => m * Matrix4x4.TRS(v, Quaternion.Inverse(q), Vector3.one)); T(o, "mulV4", () => m * new Vector4(v.x, v.y, v.z, 1f));
            T(o, "Perspective", () => Matrix4x4.Perspective(fov, aspect, near, far)); T(o, "Ortho", () => Matrix4x4.Ortho(-aspect, aspect, -1f, 1f, near, far));
            T(o, "Frustum", () => Matrix4x4.Frustum(-aspect, aspect, -1f, 1f, near, far));
            T(o, "LookAt", () => Matrix4x4.LookAt(p, tgt, up)); T(o, "Rotate", () => Matrix4x4.Rotate(q)); T(o, "Scale", () => Matrix4x4.Scale(s)); T(o, "Translate", () => Matrix4x4.Translate(p));
            T(o, "Inverse3DAffine", () => { Matrix4x4 res = Matrix4x4.zero; bool ok = Matrix4x4.Inverse3DAffine(m, ref res); return ok ? res : Matrix4x4.zero; });
            T(o, "TransformPlane", () => { var pl = m.TransformPlane(new Plane(up.sqrMagnitude > 0f ? up : Vector3.up, 1.5f)); return new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance); });
            T(o, "decompose", () => { var fp = Matrix4x4.Perspective(fov, aspect, near, far).decomposeProjection; return new Vector4(fp.left, fp.right, fp.zNear, fp.zFar); });
        }

        static void Col(Rng r, Out o)
        {
            var c = new Color(Gen.F01(r), Gen.F01(r), Gen.F01(r), Gen.F01(r)); var c2 = new Color(r.Unit(), r.Unit(), r.Unit(), r.Unit());
            float h = Gen.F01(r), s = Gen.F01(r), v = Gen.F01(r), t = Gen.F01(r);
            W(o, "in.c", c); W(o, "in.c2", c2); o.F("in.h", h); o.F("in.s", s); o.F("in.v", v); o.F("in.t", t);
            T(o, "HSVToRGB", () => Color.HSVToRGB(h, s, v)); T(o, "HSVToRGBhdr", () => Color.HSVToRGB(h, s, v * 4f, true)); T(o, "HSVToRGBnohdr", () => Color.HSVToRGB(h, s, v * 4f, false));
            T(o, "RGBToHSV", () => { float hh, ss, vv; Color.RGBToHSV(c, out hh, out ss, out vv); return new Vector3(hh, ss, vv); });
            T(o, "Lerp", () => Color.Lerp(c, c2, t)); T(o, "LerpU", () => Color.LerpUnclamped(c, c2, t * 3f - 1f));
            T(o, "linear", () => c.linear); T(o, "gamma", () => c.gamma); T(o, "grayscale", () => c.grayscale); T(o, "maxColorComponent", () => c.maxColorComponent);
            T(o, "c32", () => { Color32 k = c; return new Vector4(k.r, k.g, k.b, k.a); }); T(o, "fromC32", () => (Color)(Color32)c2);
            T(o, "Html", () => ColorUtility.ToHtmlStringRGBA(c)); T(o, "HtmlRGB", () => ColorUtility.ToHtmlStringRGB(c2));
            string html = r.Pick(new[] { "#FF8000", "#ff800080", "red", "#F80", "#F808", "nope", "#GG0000", "", "#12345", "magenta", "#FFFFFFFF0", "lightblue", "darkblue", "teal" });
            o.S("in.html", html);
            T(o, "ParseHtml", () => { Color k; bool ok = ColorUtility.TryParseHtmlString(html, out k); return ok ? k : new Color(-1f, -1f, -1f, -1f); });
            T(o, "ToString", () => c.ToString()); T(o, "mul", () => c * c2); T(o, "div", () => c / (t + 0.5f));
        }

        static void Rand(Rng r, Out o)
        {
            int seed = Gen.I(r); o.I("in.seed", seed);
            try
            {
                UnityEngine.Random.InitState(seed);
                var st = UnityEngine.Random.state;
                o.Raw("state0", JsonUtility.ToJson(st));
                for (int i = 0; i < 4; i++) o.F("value" + i, UnityEngine.Random.value);
                int lo = r.Int(200) - 100, hi = lo + r.Int(300) - 50;
                o.I("in.lo", lo); o.I("in.hi", hi);
                for (int i = 0; i < 4; i++) o.I("rangeI" + i, UnityEngine.Random.Range(lo, hi));
                float flo = Gen.FM(r), fhi = Gen.FM(r); o.F("in.flo", flo); o.F("in.fhi", fhi);
                for (int i = 0; i < 4; i++) o.F("rangeF" + i, UnityEngine.Random.Range(flo, fhi));
                W(o, "insideUnitCircle", UnityEngine.Random.insideUnitCircle); W(o, "insideUnitSphere", UnityEngine.Random.insideUnitSphere);
                W(o, "onUnitSphere", UnityEngine.Random.onUnitSphere); W(o, "rotation", UnityEngine.Random.rotation); W(o, "rotationUniform", UnityEngine.Random.rotationUniform);
                W(o, "ColorHSV", UnityEngine.Random.ColorHSV());
                var mid = UnityEngine.Random.state; float x1 = UnityEngine.Random.value; UnityEngine.Random.state = mid; float x2 = UnityEngine.Random.value;
                o.F("restore.x1", x1); o.F("restore.x2", x2);
                UnityEngine.Random.state = st; o.F("restore.first", UnityEngine.Random.value);
                o.Raw("state1", JsonUtility.ToJson(UnityEngine.Random.state));
            }
            catch (Exception e) { o.Exc("random", e); }
        }
    }
}
