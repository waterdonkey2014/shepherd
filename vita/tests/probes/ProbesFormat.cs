// Kit #7 tests track: string formatting / parsing suites (vita/notes/kit7-tests.md). Unity's players run the game's code on Unity's Mono
// with Mono's class libraries; Shepherd runs it on .NET 8's CoreLib (Mono 8.0.31 on the Vita, CoreCLR on the Mac -- the same managed
// CoreLib code). Number formatting and parsing are CoreLib code, not UnityEngine code, so this is where the two runtimes can disagree
// on text the game shows or saves. Invariant culture throughout, plus the current culture's defaults (both sides run with the invariant /
// en-US default on this Mac).
using System;
using System.Globalization;

namespace ShepherdOracle
{
    public static class ProbesFormat
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static readonly string[] FFormats = { "", "G", "G9", "G17", "R", "F0", "F1", "F2", "F3", "0", "0.0", "0.##", "#,0.00", "N0", "N2", "P0", "P1", "E", "E3", "e2", "0.###E+0", "00.00", "C", "X" };
        static readonly string[] IFormats = { "", "D", "D5", "X", "x8", "N0", "N", "0000", "#,#", "C", "P", "E2", "G", "F2", "00;-00;zero", "+0;-0" };
        static void T(Out o, string l, Func<string> f) { try { o.S(l, f()); } catch (Exception e) { o.Exc(l, e); } }

        public static void Register()
        {
            Suites.All["fmt-float"] = FmtFloat;
            Suites.All["fmt-double"] = FmtDouble;
            Suites.All["fmt-int"] = FmtInt;
            Suites.All["fmt-string"] = FmtString;
            Suites.All["parse"] = Parse;
        }

        static void FmtFloat(Rng r, Out o)
        {
            float f = r.Chance(30) ? r.Pick(new[] { 0.1f, 0.2f, 0.3f, 1f / 3f, 2f / 3f, 0.1f + 0.2f, 100.5f, 1234567.8f, 1e-5f, 123.456f, 0.015f, 2.5f, 3.5f, -0.5f, 1.05f }) : Gen.F(r);
            o.F("in.f", f);
            T(o, "ToString", () => f.ToString());
            T(o, "ToStringIC", () => f.ToString(IC));
            string fmt = r.Pick(FFormats); o.S("in.fmt", fmt);
            T(o, "fmt", () => f.ToString(fmt, IC));
            T(o, "concat", () => "x=" + f);
            T(o, "interp", () => string.Format(IC, "{0} {0:F2} {0,8:0.0}", f));
            T(o, "Convert", () => Convert.ToString(f, IC));
            T(o, "roundtrip", () => { float g = float.Parse(f.ToString("R", IC), IC); return g.Equals(f) ? "same" : Out.H(g); });
        }

        static void FmtDouble(Rng r, Out o)
        {
            double d = r.Chance(30) ? r.Pick(new[] { 0.1, 0.2, 0.1 + 0.2, 1.0 / 3.0, 1e21, 1e-7, 123456789012345678.0, 2.5, 0.125, 1.005 }) : Gen.D(r);
            o.D("in.d", d);
            T(o, "ToString", () => d.ToString());
            string fmt = r.Pick(FFormats); o.S("in.fmt", fmt);
            T(o, "fmt", () => d.ToString(fmt, IC));
            T(o, "Round2", () => Math.Round(d, 2).ToString(IC));
            T(o, "RoundAway", () => Math.Round(d, 1, MidpointRounding.AwayFromZero).ToString("R", IC));
        }

        static void FmtInt(Rng r, Out o)
        {
            int i = Gen.I(r); long l = Gen.L(r);
            o.I("in.i", i); o.I("in.l", l);
            T(o, "ToString", () => i.ToString());
            string fmt = r.Pick(IFormats); o.S("in.fmt", fmt);
            T(o, "fmt", () => i.ToString(fmt, IC)); T(o, "fmtL", () => l.ToString(fmt, IC));
            T(o, "padded", () => string.Format(IC, "[{0,6}|{0,-6}|{1:X}]", i, (uint)i));
            T(o, "byte", () => ((byte)i).ToString("X2", IC));
        }

        static void FmtString(Rng r, Out o)
        {
            string a = Gen.S(r), b = Gen.S(r, 6);
            o.S("in.a", a); o.S("in.b", b);
            T(o, "upper", () => a.ToUpperInvariant()); T(o, "lower", () => a.ToLowerInvariant());
            T(o, "upperCur", () => a.ToUpper()); T(o, "trim", () => a.Trim());
            T(o, "cmpOrd", () => string.CompareOrdinal(a, b).ToString(IC)); T(o, "cmpCur", () => Math.Sign(string.Compare(a, b)).ToString(IC));
            T(o, "cmpIC", () => Math.Sign(string.Compare(a, b, StringComparison.InvariantCulture)).ToString(IC));
            T(o, "idx", () => a.IndexOf(b).ToString(IC)); T(o, "idxOrd", () => a.IndexOf(b, StringComparison.Ordinal).ToString(IC));
            T(o, "replace", () => a.Replace(" ", "_")); T(o, "split", () => string.Join("|", a.Split(' ', ',')));
            T(o, "padL", () => b.PadLeft(8, '*')); T(o, "substr", () => a.Length > 2 ? a.Substring(1, a.Length - 2) : a);
            T(o, "hashOrd", () => (a.Length == 0 ? 0 : (int)a[0]).ToString(IC));
            T(o, "fmtObj", () => string.Format(IC, "{0}-{1}-{2}", a, b.Length, (object)null));
        }

        static void Parse(Rng r, Out o)
        {
            string[] samples = { "0", "-0", "1.5", "1,5", "1e3", "1E-3", " 42 ", "+7", "0x1F", "1_000", "NaN", "Infinity", "-Infinity", "∞", "3.4028236e38", "1e39", "", "abc",
                "12345678901234567890", "-2147483648", "2147483648", "0.1", "1.0000001", ".5", "5.", "١٢", "1 000", "1,000.25", "−5" };
            string s = r.Chance(70) ? r.Pick(samples) : Gen.S(r, 6);
            o.S("in.s", s);
            T(o, "float", () => { float v; return float.TryParse(s, NumberStyles.Float, IC, out v) ? Out.H(v) : "fail"; });
            T(o, "floatAny", () => { float v; return float.TryParse(s, NumberStyles.Any, IC, out v) ? Out.H(v) : "fail"; });
            T(o, "floatCur", () => { float v; return float.TryParse(s, out v) ? Out.H(v) : "fail"; });
            T(o, "double", () => { double v; return double.TryParse(s, NumberStyles.Float, IC, out v) ? Out.H(v) : "fail"; });
            T(o, "int", () => { int v; return int.TryParse(s, NumberStyles.Integer, IC, out v) ? v.ToString(IC) : "fail"; });
            T(o, "intAny", () => { int v; return int.TryParse(s, NumberStyles.Any, IC, out v) ? v.ToString(IC) : "fail"; });
            T(o, "intParse", () => int.Parse(s, IC).ToString(IC));
            T(o, "floatParse", () => Out.H(float.Parse(s, IC)));
            T(o, "bool", () => { bool v; return bool.TryParse(s, out v) ? (v ? "T" : "F") : "fail"; });
            T(o, "convToInt", () => Convert.ToInt32(s, IC).ToString(IC));
        }
    }
}
