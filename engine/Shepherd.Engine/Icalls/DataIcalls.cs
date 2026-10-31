using System.Text;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

/// <summary>TextAsset, AnimationCurve, Gradient — native-backed data types.</summary>
static class DataIcalls
{
    // ---- TextAsset: loader stores raw bytes in Props["__bytes"] ----
    public static byte[] Bytes(object o) => Objects.Get(o)?.Get("__bytes") switch { byte[] b => b, Assets.PackAssets.PackBytes pb => pb.Read(), _ => Array.Empty<byte>() };
    // a pack TextAsset (bytes left in the pack) reads a fresh array: no second copy
    [Icall("UnityEngine.TextAsset::get_bytes")] static object TBytes(object[] a) => Objects.Get(a[0])?.Get("__bytes") is Assets.PackAssets.PackBytes pb ? pb.Read() : (byte[])Bytes(a[0]).Clone();
    [Icall("UnityEngine.TextAsset::GetPreviewBytes")] static object TPreview(object[] a) { var b = Bytes(a[0]); return b.Take(Math.Min(b.Length, (int)a[1])).ToArray(); }
    [Icall("UnityEngine.TextAsset::GetDataSize")] static object TSize(object[] a) => Objects.Get(a[0])?.Get("__bytes") is Assets.PackAssets.PackBytes pb ? (long)pb.Length : (long)Bytes(a[0]).Length;
    [Icall("UnityEngine.TextAsset::GetDataPtr")] static unsafe object TPtr(object[] a)
    {
        var n = Objects.Get(a[0]); if (n == null) return IntPtr.Zero;
        if (n.Get("__pin") is System.Runtime.InteropServices.GCHandle h) return h.AddrOfPinnedObject();
        if (n.Get("__bytes") is Assets.PackAssets.PackBytes lazy) n.Set("__bytes", lazy.Read());   // pinned data must stay: materialize once
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(Bytes(a[0]), System.Runtime.InteropServices.GCHandleType.Pinned); n.Set("__pin", pin); return pin.AddrOfPinnedObject();
    }
    [Icall("UnityEngine.TextAsset::Internal_CreateInstance")] static object TCreate(object[] a)
    {
        var n = Objects.Bind((UObject)a[0], new NObj()); n.Set("__bytes", Encoding.UTF8.GetBytes((string)a[1] ?? "")); return null;
    }

    // ---- AnimationCurve (m_Ptr-keyed) ----
    sealed class Curve { public Keyframe[] Keys = Array.Empty<Keyframe>(); public WrapMode Pre = WrapMode.ClampForever, Post = WrapMode.ClampForever; }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Curve> Curves = new(); static long _next = 0x5000000;   // finalizer thread destroys (see ResourceIcalls.Offsets)
    static readonly System.Reflection.FieldInfo CvPtr = typeof(AnimationCurve).GetField("m_Ptr", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static Curve Cv(object self) => Curves.GetOrAdd((IntPtr)CvPtr.GetValue(self), _ => new Curve());
    public static IntPtr NewCurve(Keyframe[] keys) { var p = new IntPtr(Interlocked.Increment(ref _next)); Curves[p] = new Curve { Keys = Sort(keys ?? Array.Empty<Keyframe>()) }; return p; }
    static Keyframe[] Sort(Keyframe[] k) => k.OrderBy(x => x.time).ToArray();
    [Icall("UnityEngine.AnimationCurve::Internal_Create")] static object CCreate(object[] a) => NewCurve((Keyframe[])a[0]);
    [Icall("UnityEngine.AnimationCurve::Internal_Destroy")] static object CDestroy(object[] a) { Curves.TryRemove((IntPtr)a[0], out _); return null; }
    [Icall("UnityEngine.AnimationCurve::Internal_CopyFrom")] static object CCopy(object[] a) { var d = Cv(a[0]); if (Curves.TryGetValue((IntPtr)a[1], out var s)) { d.Keys = (Keyframe[])s.Keys.Clone(); d.Pre = s.Pre; d.Post = s.Post; } return null; }
    [Icall("UnityEngine.AnimationCurve::Internal_Equals")] static object CEq(object[] a) => Curves.TryGetValue((IntPtr)a[1], out var o) && o.Keys.SequenceEqual(Cv(a[0]).Keys);
    [Icall("UnityEngine.AnimationCurve::GetHashCode")] static object CHash(object[] a) => Cv(a[0]).GetHashCode();
    [Icall("UnityEngine.AnimationCurve::GetKeys")] static object CKeys(object[] a) => (Keyframe[])Cv(a[0]).Keys.Clone();
    [Icall("UnityEngine.AnimationCurve::SetKeys")] static object CSetKeys(object[] a) { Cv(a[0]).Keys = Sort((Keyframe[])a[1] ?? Array.Empty<Keyframe>()); return null; }
    [Icall("UnityEngine.AnimationCurve::get_length")] static object CLen(object[] a) => Cv(a[0]).Keys.Length;
    [Icall("UnityEngine.AnimationCurve::GetKey_Injected")] static object CKey(object[] a) { var k = Cv(a[0]).Keys; int i = (int)a[1]; a[2] = i >= 0 && i < k.Length ? k[i] : default; return null; }
    [Icall("UnityEngine.AnimationCurve::AddKey")] static object CAdd(object[] a) => AddKey(Cv(a[0]), new Keyframe((float)a[1], (float)a[2]));
    [Icall("UnityEngine.AnimationCurve::AddKey_Internal_Injected")] static object CAddK(object[] a) => AddKey(Cv(a[0]), (Keyframe)a[1]);
    static int AddKey(Curve c, Keyframe k) { if (c.Keys.Any(x => Mathf.Approximately(x.time, k.time))) return -1; c.Keys = Sort(c.Keys.Append(k).ToArray()); return Array.FindIndex(c.Keys, x => x.time == k.time); }
    [Icall("UnityEngine.AnimationCurve::MoveKey_Injected")] static object CMove(object[] a) { var c = Cv(a[0]); int i = (int)a[1]; if (i < 0 || i >= c.Keys.Length) return -1; var l = c.Keys.ToList(); l.RemoveAt(i); c.Keys = l.ToArray(); return AddKey(c, (Keyframe)a[2]); }
    [Icall("UnityEngine.AnimationCurve::RemoveKey")] static object CRemove(object[] a) { var c = Cv(a[0]); int i = (int)a[1]; if (i >= 0 && i < c.Keys.Length) { var l = c.Keys.ToList(); l.RemoveAt(i); c.Keys = l.ToArray(); } return null; }
    [Icall("UnityEngine.AnimationCurve::ClearKeys")] static object CClear(object[] a) { Cv(a[0]).Keys = Array.Empty<Keyframe>(); return null; }
    [Icall("UnityEngine.AnimationCurve::SmoothTangents")] static object CSmooth(object[] a) => null;
    // kit #9 fidelity ("curve"): Unity reads Clamp / Once (1) back as ClampForever (8); every other mode as set (the oracle: 600 / 600)
    [Icall("UnityEngine.AnimationCurve::get_preWrapMode")] static object CPre(object[] a) => K9Fidelity.FixCurve && Cv(a[0]).Pre == WrapMode.Clamp ? WrapMode.ClampForever : Cv(a[0]).Pre;
    [Icall("UnityEngine.AnimationCurve::set_preWrapMode")] static object CSetPre(object[] a) { Cv(a[0]).Pre = (WrapMode)a[1]; return null; }
    [Icall("UnityEngine.AnimationCurve::get_postWrapMode")] static object CPost(object[] a) => K9Fidelity.FixCurve && Cv(a[0]).Post == WrapMode.Clamp ? WrapMode.ClampForever : Cv(a[0]).Post;
    [Icall("UnityEngine.AnimationCurve::set_postWrapMode")] static object CSetPost(object[] a) { Cv(a[0]).Post = (WrapMode)a[1]; return null; }
    [Icall("UnityEngine.AnimationCurve::Evaluate")] static object CEval(object[] a) => Evaluate(Cv(a[0]), (float)a[1]);
    static float Evaluate(Curve c, float t)
    {
#if !SHEPHERD_EBOOT
        if (K9Fidelity.FixCurve && CurveTrace != null) { float u = EvaluateUnity(c, t); TraceCurve(c, t, u); return u; }
#endif
        if (K9Fidelity.FixCurve) return EvaluateUnity(c, t);
        return EvaluateK8(c, t);
    }
#if !SHEPHERD_EBOOT
    // kit #9 fidelity, second review (Mac, test only; vita/notes/kit9-fidelity.md section 13): SHEPHERD_K9_CURVE_TRACE=<file> appends one line
    // per AnimationCurve.Evaluate whose "curve" answer differs from kit #8's -- the frame, the region of the Unity model it took (the evidence
    // class: edge / clamp, stepped, wrapped (PingPong / repeat), interior cubic, weighted = kit #8's Hermite), the wrap modes, the time, both
    // answers and every key (floats as bits). It shows which kind of evaluation a moved fixture frame rests on. Not in any eboot.
    static readonly string CurveTrace = Environment.GetEnvironmentVariable("SHEPHERD_K9_CURVE_TRACE") is { Length: > 0 } p ? p : null;
    static readonly object CurveTraceLock = new();
    static string Hb(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8");
    static void TraceCurve(Curve c, float t, float unity)
    {
        float k8 = EvaluateK8(c, t);
        if (BitConverter.SingleToInt32Bits(k8) == BitConverter.SingleToInt32Bits(unity)) return;
        var sb = new StringBuilder();
        sb.Append('f').Append(Shepherd.Engine.Runtime.Time.FrameCount).Append('\t').Append(URegion(c, t)).Append("\tpre=").Append((int)c.Pre).Append(" post=").Append((int)c.Post)
          .Append("\tt=").Append(Hb(t)).Append("\tk8=").Append(Hb(k8)).Append("\tunity=").Append(Hb(unity)).Append("\tkeys=");
        foreach (var k in c.Keys)
            sb.Append(Hb(k.time)).Append(',').Append(Hb(k.value)).Append(',').Append(Hb(k.inTangent)).Append(',').Append(Hb(k.outTangent)).Append(',')
              .Append((int)k.weightedMode).Append(',').Append(Hb(k.inWeight)).Append(',').Append(Hb(k.outWeight)).Append(';');
        lock (CurveTraceLock) System.IO.File.AppendAllText(CurveTrace, sb.Append('\n').ToString());
    }
    /// <summary>Which branch of <see cref="EvaluateUnity"/> answers (the same tests, in the same order).</summary>
    static string URegion(Curve c, float x)
    {
        var k = c.Keys; int n = k.Length; if (n <= 1) return "trivial";
        float t0 = k[0].time, t1 = k[n - 1].time, len = (float)(t1 - t0);
        string wrap = "none";
        if (x < t0) { if (!UClamps(c.Pre)) wrap = c.Pre == WrapMode.PingPong ? "pingpong" : "repeat"; x = UWrap(c.Pre, x, t0, t1, len, true); }
        else if (x > t1 || (x == t1 && !UClamps(c.Post))) { if (!UClamps(c.Post)) wrap = c.Post == WrapMode.PingPong ? "pingpong" : "repeat"; x = UWrap(c.Post, x, t0, t1, len, false); }
        if (x >= t1 || x <= t0) return "edge/clamp (wrap " + wrap + ")";
        int i = 0; while (i < n - 2 && k[i + 1].time <= x) i++;
        if ((k[i].weightedMode & WeightedMode.Out) != 0 || (k[i + 1].weightedMode & WeightedMode.In) != 0) return "weighted (kit #8's Hermite; wrap " + wrap + ")";
        if (!float.IsFinite(k[i].outTangent) || !float.IsFinite(k[i + 1].inTangent)) return "stepped (wrap " + wrap + ")";
        return wrap != "none" ? "wrapped " + wrap + " (per-key Hermite)" : "interior (segment-cache cubic)";
    }
#endif
    static float EvaluateK8(Curve c, float t)
    {
        var k = c.Keys; if (k.Length == 0) return 0; if (k.Length == 1) return k[0].value;
        float t0 = k[0].time, t1 = k[^1].time, len = t1 - t0;
        if (t < t0) t = Wrap(c.Pre, t, t0, len); else if (t > t1) t = Wrap(c.Post, t, t0, len);
        if (t <= t0) return k[0].value; if (t >= t1) return k[^1].value;
        int i = 0; while (i < k.Length - 2 && t > k[i + 1].time) i++;
        var a = k[i]; var b = k[i + 1]; float dt = b.time - a.time; if (dt <= 0) return b.value;
        if (float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent)) return a.value;   // constant step
        float s = (t - a.time) / dt, s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * a.value + (s3 - 2 * s2 + s) * a.outTangent * dt + (-2 * s3 + 3 * s2) * b.value + (s3 - s2) * b.inTangent * dt;
    }
    /// <summary>Kit #9 fidelity (stand-in fix "curve", kit7-tests.md finding 14; vita/notes/kit9-fidelity.md): Unity's native AnimationCurve
    /// evaluation as measured against the oracle players (d-data-curve, seed 2: inputs regenerated exactly in both float modes by
    /// fidelity-work/curve/model.py, 300 / 300): wrap modes Loop AND Default repeat (t - floor(t / len) * len), PingPong mirrors, Clamp / Once /
    /// ClampForever clamp; the post-wrap also applies AT the last key for the non-clamping modes (t1 -> t0); a time at or past the last key
    /// (after the wrap) reads the last key's value and at or before the first key the first's -- a stepped last segment included (kit #8:
    /// Default clamped, t1 evaluated the segment, a stepped last segment answered its left key); a segment whose slope is not finite
    /// (+-inf, NaN) is stepped (kit #8: infinities only); a plain segment is the time-domain cubic of Unity's segment cache
    /// t * (t * (t * c0 + c1) + m1) + v0, t = time - lhs.time, c0 = (d1 + d2 - dy - dy) * (1 / dx^2 / dx), c1 = (3 dy - 2 d1 - d2) / dx^2 as
    /// written below, and a WRAPPED time (Loop / Default / PingPong) the per-key Hermite a p0 + b m0 + c m1 + d p1 (float32 per operation).
    /// Measured (MEASURED-M, the oracle's 1,704 evaluations of curves without weighted keys): 1,514 bit-exact (kit #8: 1,155) -- every edge /
    /// clamp / stepped / PingPong one, interior 368 / 495, repeat 304 / 366 --, 167 within 16 ulp, 23 at 17-4,096 ulp, none above (kit #8: 171
    /// evaluations above 4,096 ulp: the wrong wrap / edge / stepped rules).
    /// Weighted segments (weightedMode In / Out: a Bezier with a root solve in Unity) keep kit #8's Hermite: not modelled yet.</summary>
    static float EvaluateUnity(Curve c, float x)
    {
        var k = c.Keys; int n = k.Length; if (n == 0) return 0f; if (n == 1) return k[0].value;
        float t0 = k[0].time, t1 = k[n - 1].time, len = (float)(t1 - t0);
        bool wrapped = false;
        if (x < t0) { wrapped = !UClamps(c.Pre); x = UWrap(c.Pre, x, t0, t1, len, true); }
        else if (x > t1 || (x == t1 && !UClamps(c.Post))) { wrapped = !UClamps(c.Post); x = UWrap(c.Post, x, t0, t1, len, false); }
        if (x >= t1) return k[n - 1].value;
        if (x <= t0) return k[0].value;
        int i = 0; while (i < n - 2 && k[i + 1].time <= x) i++;
        ref readonly var l = ref k[i]; ref readonly var r = ref k[i + 1];
        if ((l.weightedMode & WeightedMode.Out) != 0 || (r.weightedMode & WeightedMode.In) != 0) return HermiteK8(l, r, x);   // (not modelled: kit #8's)
        if (!float.IsFinite(l.outTangent) || !float.IsFinite(r.inTangent)) return l.value;
        if (wrapped)
        {   // a wrapped time (Loop / Default / PingPong): the per-key Hermite matched the oracle best (PingPong 187 / 187, repeat 304 / 366)
            float hx = (float)(r.time - l.time), ht = 0f, hm1 = 0f, hm2 = 0f;
            if (hx != 0f) { ht = (float)((float)(x - l.time) / hx); hm1 = (float)(l.outTangent * hx); hm2 = (float)(r.inTangent * hx); }
            float t2 = (float)(ht * ht), t3 = (float)(t2 * ht);
            float ha = (float)((float)((float)(2f * t3) - (float)(3f * t2)) + 1f), hb = (float)((float)(t3 - (float)(2f * t2)) + ht);
            float hc = (float)(t3 - t2), hd = (float)((float)(-2f * t3) + (float)(3f * t2));
            return (float)((float)((float)((float)(ha * l.value) + (float)(hb * hm1)) + (float)(hc * hm2)) + (float)(hd * r.value));
        }
        float dx = (float)(r.time - l.time); if (!(dx >= 0.0001f)) dx = 0.0001f;
        float dy = (float)(r.value - l.value), length = (float)(1f / (float)(dx * dx));
        float m1 = l.outTangent, d1 = (float)(m1 * dx), d2 = (float)(r.inTangent * dx);
        float c0 = (float)((float)((float)((float)(d1 + d2) - dy) - dy) * (float)(length / dx));
        float c1 = (float)((float)((float)((float)((float)((float)(dy + dy) + dy) - d1) - d1) - d2) * length);
        float t = (float)(x - l.time);
        return (float)((float)(t * (float)((float)(t * (float)((float)(t * c0) + c1)) + m1)) + l.value);
    }
    static bool UClamps(WrapMode m) => m == WrapMode.Clamp || m == WrapMode.ClampForever;
    static float URepeat(float t, float length) => (float)(t - (float)(MathF.Floor((float)(t / length)) * length));
    static float UWrap(WrapMode m, float x, float t0, float t1, float len, bool pre)
    {
        if (UClamps(m) || !(len > 0f)) return pre ? t0 : t1;
        float d = (float)(x - t0);
        if (m == WrapMode.PingPong) { float q = URepeat(d, (float)(len * 2f)); return (float)((float)(len - MathF.Abs((float)(q - len))) + t0); }
        return (float)(URepeat(d, len) + t0);   // Loop, Default
    }
    static float HermiteK8(in Keyframe a, in Keyframe b, float t)
    {
        float dt = b.time - a.time; if (dt <= 0) return b.value;
        if (float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent)) return a.value;
        float s = (t - a.time) / dt, s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * a.value + (s3 - 2 * s2 + s) * a.outTangent * dt + (-2 * s3 + 3 * s2) * b.value + (s3 - s2) * b.inTangent * dt;
    }
    static float Wrap(WrapMode m, float t, float t0, float len)
    {
        if (len <= 0) return t0;
        return m switch { WrapMode.Loop => t0 + Mathf.Repeat(t - t0, len), WrapMode.PingPong => t0 + Mathf.PingPong(t - t0, len), _ => t };
    }

    // ---- Gradient (m_Ptr-keyed) ----
    sealed class Grad { public GradientColorKey[] C = { new(Color.white, 0), new(Color.white, 1) }; public GradientAlphaKey[] A = { new(1, 0), new(1, 1) }; public GradientMode Mode; public ColorSpace Space = ColorSpace.Uninitialized; }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Grad> Grads = new();   // finalizer thread destroys
    static readonly System.Reflection.FieldInfo GrPtr = typeof(Gradient).GetField("m_Ptr", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static Grad Gr(object self) => Grads.GetOrAdd((IntPtr)GrPtr.GetValue(self), _ => new Grad());
    [Icall("UnityEngine.Gradient::Init")] static object GInit(object[] a) { var p = new IntPtr(Interlocked.Increment(ref _next)); Grads[p] = new Grad(); return p; }
    [Icall("UnityEngine.Gradient::Cleanup")] static object GClean(object[] a) => null;
    [Icall("UnityEngine.Gradient::Internal_Equals")] static object GEq(object[] a) => false;
    [Icall("UnityEngine.Gradient::get_colorKeys")] static object GCk(object[] a) => (GradientColorKey[])Gr(a[0]).C.Clone();
    [Icall("UnityEngine.Gradient::set_colorKeys")] static object GSetCk(object[] a) { Gr(a[0]).C = ((GradientColorKey[])a[1] ?? Array.Empty<GradientColorKey>()).OrderBy(k => k.time).ToArray(); return null; }
    [Icall("UnityEngine.Gradient::get_alphaKeys")] static object GAk(object[] a) => (GradientAlphaKey[])Gr(a[0]).A.Clone();
    [Icall("UnityEngine.Gradient::set_alphaKeys")] static object GSetAk(object[] a) { Gr(a[0]).A = ((GradientAlphaKey[])a[1] ?? Array.Empty<GradientAlphaKey>()).OrderBy(k => k.time).ToArray(); return null; }
    [Icall("UnityEngine.Gradient::SetKeys")] static object GSet(object[] a) { GSetCk(new[] { a[0], a[1] }); GSetAk(new[] { a[0], a[2] }); return null; }
    [Icall("UnityEngine.Gradient::get_mode")] static object GMode(object[] a) => Gr(a[0]).Mode;
    [Icall("UnityEngine.Gradient::set_mode")] static object GSetMode(object[] a) { Gr(a[0]).Mode = (GradientMode)a[1]; return null; }
    [Icall("UnityEngine.Gradient::get_colorSpace")] static object GSpace(object[] a) => Gr(a[0]).Space;
    [Icall("UnityEngine.Gradient::set_colorSpace")] static object GSetSpace(object[] a) { Gr(a[0]).Space = (ColorSpace)a[1]; return null; }
    [Icall("UnityEngine.Gradient::Evaluate_Injected")] static object GEval(object[] a)
    {
        var g = Gr(a[0]); float t = Mathf.Clamp01((float)a[1]);
        Color c = Sample(g.C.Select(k => (k.time, (Vector4)k.color)).ToArray(), t, g.Mode == GradientMode.Fixed);
        float al = Sample(g.A.Select(k => (k.time, new Vector4(k.alpha, 0, 0, 0))).ToArray(), t, g.Mode == GradientMode.Fixed).x;
        c.a = al; a[2] = c; return null;
    }
    static Vector4 Sample((float t, Vector4 v)[] k, float t, bool fixedMode)
    {
        if (k.Length == 0) return Vector4.one; if (t <= k[0].t) return k[0].v; if (t >= k[^1].t) return k[^1].v;
        for (int i = 0; i < k.Length - 1; i++) if (t <= k[i + 1].t) { if (fixedMode) return k[i + 1].v; float s = (t - k[i].t) / Math.Max(1e-6f, k[i + 1].t - k[i].t); return Vector4.Lerp(k[i].v, k[i + 1].v, s); }
        return k[^1].v;
    }
}
