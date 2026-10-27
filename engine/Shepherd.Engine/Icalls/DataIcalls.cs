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
    [Icall("UnityEngine.AnimationCurve::get_preWrapMode")] static object CPre(object[] a) => Cv(a[0]).Pre;
    [Icall("UnityEngine.AnimationCurve::set_preWrapMode")] static object CSetPre(object[] a) { Cv(a[0]).Pre = (WrapMode)a[1]; return null; }
    [Icall("UnityEngine.AnimationCurve::get_postWrapMode")] static object CPost(object[] a) => Cv(a[0]).Post;
    [Icall("UnityEngine.AnimationCurve::set_postWrapMode")] static object CSetPost(object[] a) { Cv(a[0]).Post = (WrapMode)a[1]; return null; }
    [Icall("UnityEngine.AnimationCurve::Evaluate")] static object CEval(object[] a) => Evaluate(Cv(a[0]), (float)a[1]);
    static float Evaluate(Curve c, float t)
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
