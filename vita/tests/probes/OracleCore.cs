// Kit #7 tests track (vita/notes/kit7-tests.md): the probe core shared by both sides of the Unity differential tests -- compiled into the
// owner-local Unity 2022.3.62f2 oracle players (vita/tests/oracle: the E1 pattern, batchmode, built-in modules only, no downloads) AND
// into the Mac test host (vita/tests/TestHost, running Shepherd's engine). The same source, the same seeds, the same UnityEngine API on
// both sides; every result is written as exact text (raw float bits in hex, exact strings), so two runs agree only bit for bit.
// Written in-house (no CsCheck / no download): a SplitMix64 generator, value generators biased to edge cases, per-case seeds (any case
// replays alone from (suite, seed, case)), per-case and whole-suite hashes (CsCheck's Check.Hash idea: raw bits, never rounded decimals).
// C# 9 / .NET Standard 2.1 only (Unity 2022.3's compiler): no file-scoped namespaces, no BitConverter.SingleToInt32Bits.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ShepherdOracle
{
    [StructLayout(LayoutKind.Explicit)]
    public struct FBits { [FieldOffset(0)] public float F; [FieldOffset(0)] public uint U; [FieldOffset(0)] public int I; }
    [StructLayout(LayoutKind.Explicit)]
    public struct DBits { [FieldOffset(0)] public double D; [FieldOffset(0)] public ulong U; }

    /// <summary>SplitMix64: integer-only, so every runtime produces the same stream.</summary>
    public sealed class Rng
    {
        ulong _s;
        public Rng(ulong seed) { _s = seed; }
        public ulong Next() { ulong z = (_s += 0x9E3779B97F4A7C15UL); z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; return z ^ (z >> 31); }
        public uint U32() => (uint)(Next() >> 32);
        /// <summary>[0, n) without modulo bias worth caring about for tests (n &lt;= 2^31).</summary>
        public int Int(int n) => n <= 1 ? 0 : (int)((Next() >> 33) % (ulong)n);
        public int Range(int lo, int hiExcl) => lo + Int(hiExcl - lo);
        public bool Bool() => (Next() & 1) != 0;
        public bool Chance(int pct) => Int(100) < pct;
        /// <summary>A float in [lo, hi) from 24 random bits (exact on every runtime: integer to float conversion and one multiply-add in float).</summary>
        public float Unit() => (U32() >> 8) * (1.0f / 16777216.0f);
        /// <summary>An exact float: a signed 24-bit integer times 2^exp (both conversions exact; no rounding anywhere, so no runtime can
        /// produce a different value -- Unity's Mono and CoreCLR may round float expressions differently, the generators must not care).</summary>
        public float Exact(int exp) { int k = (int)(U32() >> 8) - (1 << 23); return k * Pow2(exp); }
        public static float Pow2(int e) { if (e < -126) e = -126; if (e > 127) e = 127; var b = new FBits { U = (uint)(e + 127) << 23 }; return b.F; }
        public T Pick<T>(T[] a) => a[Int(a.Length)];
    }

    /// <summary>Value generators biased toward the cases implementations get wrong (signed zeros, infinities, NaN, subnormals, huge and tiny,
    /// exact integers, angle wraps).</summary>
    public static class Gen
    {
        public static readonly float[] SpecialF = { 0f, -0f, 1f, -1f, 0.5f, -0.5f, 2f, 0.1f, 1e-7f, -1e-7f, 1e-38f, 1.17549435e-38f, 1.401298e-45f, 3.4028235e38f, -3.4028235e38f,
            float.PositiveInfinity, float.NegativeInfinity, float.NaN, 90f, 180f, 270f, 360f, -90f, -180f, 720f, 3.14159274f, 6.28318548f, 1.57079637f, 0.99999994f, 1.00000012f, 16777216f, 16777217f, 1e10f, -1e10f };
        public static float Special(Rng r) => r.Pick(SpecialF);
        public static float AnyBits(Rng r) { var b = new FBits { U = r.U32() }; return b.F; }
        /// <summary>The default float: 60% a range-scaled uniform, 15% an exact small integer or half-integer, 15% a special, 10% raw bits.</summary>
        public static float F(Rng r)
        {
            int k = r.Int(100);
            if (k < 15) return Special(r);
            if (k < 25) return AnyBits(r);
            if (k < 40) return (r.Int(4001) - 2000) / (float)(1 << r.Int(4));
            int[] exps = { -23, -20, -16, -13, -6, -33 };   // magnitudes ~1, 8, 128, 1024, 131072, 0.001
            return r.Exact(r.Pick(exps));
        }
        /// <summary>A "finite and moderate" float (most game values): uniform in +-1000, sometimes an integer.</summary>
        public static float FM(Rng r) => r.Chance(20) ? r.Int(721) - 360 : r.Exact(-13);
        public static float F01(Rng r) => r.Chance(10) ? r.Pick(new[] { 0f, 1f, 0.5f, -0.1f, 1.1f }) : r.Unit();
        public static double D(Rng r)
        {
            int k = r.Int(100);
            if (k < 10) return r.Pick(new[] { 0.0, -0.0, 1.0, -1.0, 0.1, 1e-300, 4.9406564584124654e-324, double.MaxValue, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1e15, 123456789.123456789 });
            if (k < 20) { var b = new DBits { U = r.Next() }; return b.D; }
            if (k < 40) return (r.Int(2000001) - 1000000) / 1000.0;
            return F(r);
        }
        public static int I(Rng r)
        {
            int k = r.Int(100);
            if (k < 10) return r.Pick(new[] { 0, 1, -1, int.MaxValue, int.MinValue, 255, 256, 1023, 1024, 65535, 65536, -2147483647 });
            if (k < 30) return (int)r.U32();
            return r.Int(2001) - 1000;
        }
        public static long L(Rng r) => r.Chance(20) ? r.Pick(new[] { 0L, 1L, -1L, long.MaxValue, long.MinValue, 4294967296L }) : (long)r.Next();
        static readonly string Alpha = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 _-.,:;!?\"'\\/{}[]<>\t\néü中☃";
        public static string S(Rng r, int maxLen = 16) { int n = r.Int(maxLen + 1); var sb = new StringBuilder(n); for (int i = 0; i < n; i++) sb.Append(Alpha[r.Int(Alpha.Length)]); return sb.ToString(); }
    }

    /// <summary>One suite's output: "&lt;case&gt;\t&lt;label&gt;\t&lt;value&gt;" lines, plus a running per-case hash and a suite hash over every line
    /// (FNV-1a 64 of the exact text).</summary>
    public sealed class Out
    {
        readonly StringBuilder _sb = new StringBuilder(1 << 16);
        readonly TextWriter _w;
        public int Case; public long Lines;
        /// <summary>The last label written and a counter of writes (a host's watchdog reads them to name a hanging case).</summary>
        public volatile string LastLabel = ""; public long Writes;
        ulong _caseHash = 14695981039346656037UL, _suiteHash = 14695981039346656037UL;
        public Out(TextWriter w) { _w = w; }
        static ulong Fnv(ulong h, string s) { foreach (char c in s) { h = (h ^ (byte)c) * 1099511628211UL; h = (h ^ (byte)(c >> 8)) * 1099511628211UL; } return h; }
        public void Raw(string label, string value)
        {
            string line = Case.ToString(CultureInfo.InvariantCulture) + "\t" + label + "\t" + value;
            _caseHash = Fnv(_caseHash, line); _suiteHash = Fnv(_suiteHash, line); Lines++; LastLabel = label; System.Threading.Interlocked.Increment(ref Writes);
            _w.Write(line); _w.Write('\n');
        }
        public void BeginCase(int i) { Case = i; _caseHash = 14695981039346656037UL; }
        public void EndCase(bool writeHash) { if (writeHash) _w.Write(Case.ToString(CultureInfo.InvariantCulture) + "\t#case\t" + _caseHash.ToString("x16") + "\n"); }
        public ulong SuiteHash => _suiteHash;
        // ---- exact encodings ----
        public static string H(float f) { var b = new FBits { F = f }; return b.U.ToString("x8"); }
        public static string H(double d) { var b = new DBits { D = d }; return b.U.ToString("x16"); }
        public static string Esc(string s)
        {
            if (s == null) return "<null>";
            var sb = new StringBuilder(s.Length + 2); sb.Append('"');
            foreach (char c in s) { if (c == '\\') sb.Append("\\\\"); else if (c == '\t') sb.Append("\\t"); else if (c == '\n') sb.Append("\\n"); else if (c == '\r') sb.Append("\\r"); else if (c < 32 || c > 126) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); }
            sb.Append('"'); return sb.ToString();
        }
        public void F(string label, float v) => Raw(label, H(v));
        public void D(string label, double v) => Raw(label, H(v));
        public void I(string label, long v) => Raw(label, v.ToString(CultureInfo.InvariantCulture));
        public void B(string label, bool v) => Raw(label, v ? "1" : "0");
        public void S(string label, string v) => Raw(label, Esc(v));
        public void Exc(string label, Exception e) => Raw(label, "EXC:" + e.GetType().Name);
    }

    public delegate void CaseFn(Rng r, Out o);

    /// <summary>The registry: suites by name. A suite runs N cases; case i draws from Rng(Seed(seed, suite, i)).</summary>
    public static class Suites
    {
        public static readonly Dictionary<string, CaseFn> All = new Dictionary<string, CaseFn>();
        /// <summary>Suites whose cases end with a per-case hash line (the stateful model-based suites).</summary>
        public static readonly HashSet<string> Hashed = new HashSet<string>();
        static readonly List<Action> _registrars = new List<Action>();
        public static void Register(Action registrar) { _registrars.Add(registrar); }
        static bool _init;
        public static void Init()
        {
            if (_init) return; _init = true;
            ProbesMath.Register(); ProbesFormat.Register(); ProbesJson.Register(); ProbesTransform.Register(); ProbesLayout.Register();
            // the machine-drafted probes (vita/tests/drafted): every type in namespace ShepherdOracle.Drafted with a static Register()
            foreach (var t in typeof(Suites).Assembly.GetTypes())
                if (t.Namespace == "ShepherdOracle.Drafted" && t.GetMethod("Register", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) is System.Reflection.MethodInfo m)
                    m.Invoke(null, null);
            foreach (var a in _registrars) a();
        }
        public static ulong Seed(ulong seed, string suite, int i)
        {
            ulong h = 14695981039346656037UL; foreach (char c in suite) h = (h ^ c) * 1099511628211UL;
            var r = new Rng(seed ^ h ^ ((ulong)i * 0xD1B54A32D192ED03UL)); return r.Next();
        }
        /// <summary>Run one suite: N cases from case `first`. Exceptions inside a case are recorded as a value (both sides must agree).</summary>
        public static ulong Run(string suite, ulong seed, int first, int n, TextWriter w, Action<string> log)
        {
            Init();
            CaseFn fn;
            if (!All.TryGetValue(suite, out fn)) { log("unknown suite '" + suite + "'"); return 0; }
            var o = new Out(w); Current = o; CurrentSuite = suite;
            w.Write("#suite\t" + suite + "\tseed\t" + seed.ToString(CultureInfo.InvariantCulture) + "\tfirst\t" + first + "\tn\t" + n + "\n");
            bool hashed = Hashed.Contains(suite);
            for (int i = first; i < first + n; i++)
            {
                o.BeginCase(i);
                var r = new Rng(Seed(seed, suite, i));
                try { fn(r, o); } catch (Exception e) { o.Raw("!case", "EXC:" + e.GetType().Name + ":" + Out.Esc(e.Message)); }
                o.EndCase(hashed);
            }
            w.Write("#end\t" + suite + "\tlines\t" + o.Lines + "\thash\t" + o.SuiteHash.ToString("x16") + "\n");
            return o.SuiteHash;
        }
        /// <summary>The suite running now (for a host's hang watchdog).</summary>
        public static volatile Out Current; public static volatile string CurrentSuite;
        public static IEnumerable<string> Names() { Init(); var l = new List<string>(All.Keys); l.Sort(StringComparer.Ordinal); return l; }
    }
}
