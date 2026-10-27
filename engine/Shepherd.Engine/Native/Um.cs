using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
namespace Shepherd.Engine.Native;

/// <summary>menu-speed, stage "hop fix": Unity's struct constructors, constants and small operators, written as our own arithmetic on
/// the structs' fields. Unity's modules run in the interpreter and the engine-AOT image may not inline their methods (the foreign-code
/// rule: no Unity or game method body is compiled into the engine image), so every call the engine made to, say, Rect.x or
/// Vector3.Min was an AOT -> interpreter transition (~9,900 per idle Main Menu frame). Each helper here performs the same IEEE
/// single-precision operations as the Unity method it stands for, on the same operands, in the same order (read from
/// UnityEngine.CoreModule's IL; noted per helper), so results are bit-identical -- NaN, infinities and -0 included. Nothing here is
/// Unity's code: it is the arithmetic, on public fields (Vector2/3/4, Quaternion, Color, Matrix4x4) or on mirror structs of the same
/// sequential layout (Rect, Bounds and Plane keep their fields private; the mirrors are checked at startup by <see cref="UmLayout"/>,
/// and if that check ever fails Unity's own accessors are used instead). SHEPHERD_HOPFIX_CHECK=1 compares the replaced operations with
/// the Unity methods at run time (<see cref="HopCheck"/>).</summary>
public static unsafe class Um
{
    // ---- constructors: field stores (Vector2/3/4, Quaternion and Color's constructors store their arguments, nothing else) ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 V2(float x, float y) { Vector2 v; v.x = x; v.y = y; return v; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 V3(float x, float y, float z) { Vector3 v; v.x = x; v.y = y; v.z = z; return v; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector4 V4(float x, float y, float z, float w) { Vector4 v; v.x = x; v.y = y; v.z = z; v.w = w; return v; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Quaternion Q(float x, float y, float z, float w) { Quaternion q; q.x = x; q.y = y; q.z = z; q.w = w; return q; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Color C(float r, float g, float b, float a) { Color c; c.r = r; c.g = g; c.b = b; c.a = a; return c; }

    // ---- constants: the values Unity's static constructors store (Vector3.zeroVector (0,0,0), oneVector (1,1,1), Vector2.zeroVector,
    // Quaternion.identityQuaternion (0,0,0,1), Color.white = new Color(1,1,1,1), Matrix4x4.identityMatrix: 1 on the diagonal, +0 elsewhere) ----
    public static Vector2 Zero2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => V2(0f, 0f); }
    public static Vector3 Zero3 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => V3(0f, 0f, 0f); }
    public static Vector3 One3 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => V3(1f, 1f, 1f); }
    public static Quaternion QIdentity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Q(0f, 0f, 0f, 1f); }
    public static Color White { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => C(1f, 1f, 1f, 1f); }
    public static Matrix4x4 Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { var m = new Matrix4x4(); m.m00 = 1f; m.m11 = 1f; m.m22 = 1f; m.m33 = 1f; return m; } }

    // ---- operators ----
    /// <summary>Vector2 == Vector2: dx = lhs.x - rhs.x, dy = lhs.y - rhs.y; dx * dx + dy * dy &lt; kEpsilon * kEpsilon (the float
    /// 1e-5f * 1e-5f = 9.9999994E-11f, bits 0x2EDBE6FE: the literal in Unity's IL).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool Eq(Vector2 lhs, Vector2 rhs) { float dx = lhs.x - rhs.x, dy = lhs.y - rhs.y; return dx * dx + dy * dy < 9.9999994E-11f; }

    /// <summary>Mathf.Min(a, b) = a &lt; b ? a : b (a NaN operand gives b); Vector3.Min / Max apply it per component (lhs = a).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Min(float a, float b) => a < b ? a : b;
    /// <summary>Mathf.Max(a, b) = a &gt; b ? a : b (a NaN operand gives b).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Max(float a, float b) => a > b ? a : b;
    /// <summary>Mathf.LerpUnclamped(a, b, t) = a + (b - a) * t.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
    /// <summary>Vector3 a - b = (a.x - b.x, a.y - b.y, a.z - b.z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Sub(Vector3 a, Vector3 b) => V3(a.x - b.x, a.y - b.y, a.z - b.z);
    /// <summary>(a + b) * 0.5f: Vector3 + then Vector3 * float = ((a.x + b.x) * 0.5f, ...).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Mid(Vector3 a, Vector3 b) => V3((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, (a.z + b.z) * 0.5f);
    /// <summary>Vector3.Scale(a, b) = (a.x * b.x, a.y * b.y, a.z * b.z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 Scale(Vector3 a, Vector3 b) => V3(a.x * b.x, a.y * b.y, a.z * b.z);
    /// <summary>(a - b).sqrMagnitude: Vector3 - per component, then x * x + y * y + z * z (left to right).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float SqrDist(Vector3 a, Vector3 b) { float x = a.x - b.x, y = a.y - b.y, z = a.z - b.z; return x * x + y * y + z * z; }
    /// <summary>a + (b - a) * t for Vector4 (Vector4 -, * float, + per component: a.x + (b.x - a.x) * t).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector4 Lerp4(Vector4 a, Vector4 b, float t) => V4(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t);

    // ---- Rect / Bounds / Plane through their mirrors ----
    /// <summary>A Rect's fields (m_XMin, m_YMin, m_Width, m_Height = x, y, width, height).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static RectF Of(Rect r) => UmLayout.Ok ? *(RectF*)&r : new RectF(r.x, r.y, r.width, r.height);
    /// <summary>new Rect(x, y, width, height) (the constructor stores its four arguments).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Rect ToRect(in RectF f) { if (!UmLayout.Ok) return new Rect(f.X, f.Y, f.W, f.H); var c = f; return *(Rect*)&c; }
    /// <summary>A Bounds' fields (m_Center, m_Extents = center, extents).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static BoundsF Of(Bounds b) => UmLayout.Ok ? *(BoundsF*)&b : new BoundsF(b.center, b.extents);
    /// <summary>new Bounds(center, size): m_Center = center, m_Extents = size * 0.5f (Vector3 * float: each component times 0.5f).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Bounds ToBounds(Vector3 center, Vector3 size)
    {
        if (!UmLayout.Ok) return new Bounds(center, size);
        var f = new BoundsF(center, V3(size.x * 0.5f, size.y * 0.5f, size.z * 0.5f)); return *(Bounds*)&f;
    }
    /// <summary>A Plane's fields (m_Normal, m_Distance = normal, distance).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static PlaneF Of(Plane p) => UmLayout.Ok ? *(PlaneF*)&p : new PlaneF(p.normal, p.distance);
    /// <summary>A Plane holding exactly this normal and distance (no normalization: only with UmLayout.Ok).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Plane ToPlaneRaw(Vector3 n, float d) { var f = new PlaneF(n, d); return *(Plane*)&f; }
}

/// <summary>Rect's layout: [StructLayout(Sequential)] m_XMin, m_YMin, m_Width, m_Height (all float32).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RectF
{
    public float X, Y, W, H;
    public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    /// <summary>Rect.xMax = m_Width + m_XMin (that operand order).</summary>
    public readonly float XMax { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => W + X; }
    /// <summary>Rect.yMax = m_Height + m_YMin.</summary>
    public readonly float YMax { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => H + Y; }
}
/// <summary>Bounds' layout: m_Center, m_Extents (Vector3 each).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BoundsF { public Vector3 C, E; public BoundsF(Vector3 c, Vector3 e) { C = c; E = e; } }
/// <summary>Plane's layout: m_Normal (Vector3), m_Distance (float32).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlaneF { public Vector3 N; public float D; public PlaneF(Vector3 n, float d) { N = n; D = d; } }

/// <summary>Startup check that the mirror structs map Unity's Rect / Bounds / Plane field for field (sizes, then a read and a write
/// round trip with distinct values, compared bit for bit through Unity's own constructors and getters: a few interpreted calls,
/// once). <see cref="Ok"/> false = Um uses Unity's accessors (same values, just the transitions).</summary>
[ProfileOnly]
public static class UmLayout
{
    public static readonly bool Ok = Probe(out Why);
    public static readonly string Why;
    static unsafe bool Probe(out string why)
    {
        try
        {
            why = sizeof(Rect) != sizeof(RectF) || sizeof(Rect) != 16 ? "Rect size" : sizeof(Bounds) != sizeof(BoundsF) || sizeof(Bounds) != 24 ? "Bounds size"
                : sizeof(Plane) != sizeof(PlaneF) || sizeof(Plane) != 16 ? "Plane size" : null;
            if (why != null) return Fail(ref why);
            var r = new Rect(1.5f, -2.5f, 3.25f, 4.75f); var rf = *(RectF*)&r;
            if (!S(rf.X, r.x) || !S(rf.Y, r.y) || !S(rf.W, r.width) || !S(rf.H, r.height) || !S(rf.X, 1.5f) || !S(rf.H, 4.75f)) { why = "Rect read"; return Fail(ref why); }
            var wf = new RectF(5.5f, 6.5f, -7.5f, 8.5f); var w = *(Rect*)&wf;
            if (!S(w.x, 5.5f) || !S(w.y, 6.5f) || !S(w.width, -7.5f) || !S(w.height, 8.5f)) { why = "Rect write"; return Fail(ref why); }
            var b = new Bounds(new Vector3(1f, 2f, 3f), new Vector3(10f, 12f, 14f)); var bf = *(BoundsF*)&b;
            Vector3 bc = b.center, be = b.extents;
            if (!S(bf.C.x, bc.x) || !S(bf.C.y, bc.y) || !S(bf.C.z, bc.z) || !S(bf.E.x, be.x) || !S(bf.E.y, be.y) || !S(bf.E.z, be.z) || !S(bf.E.z, 7f)) { why = "Bounds read"; return Fail(ref why); }
            var bw = new BoundsF(new Vector3(-1f, -2f, -3f), new Vector3(4f, 5f, 6f)); var b2 = *(Bounds*)&bw;
            Vector3 b2c = b2.center, b2e = b2.extents;
            if (!S(b2c.x, -1f) || !S(b2c.y, -2f) || !S(b2c.z, -3f) || !S(b2e.x, 4f) || !S(b2e.y, 5f) || !S(b2e.z, 6f)) { why = "Bounds write"; return Fail(ref why); }
            var p = new Plane(new Vector3(3f, 4f, 0f), 7f); var pf = *(PlaneF*)&p; Vector3 pn = p.normal;
            if (!S(pf.N.x, pn.x) || !S(pf.N.y, pn.y) || !S(pf.N.z, pn.z) || !S(pf.D, p.distance) || !S(pf.D, 7f)) { why = "Plane read"; return Fail(ref why); }
            var pw = new PlaneF(new Vector3(1.25f, 2.5f, 3.75f), -4.5f); var p2 = *(Plane*)&pw; Vector3 p2n = p2.normal;
            if (!S(p2n.x, 1.25f) || !S(p2n.y, 2.5f) || !S(p2n.z, 3.75f) || !S(p2.distance, -4.5f)) { why = "Plane write"; return Fail(ref why); }
            return true;
        }
        catch (Exception e) { why = e.GetType().Name + ": " + e.Message; return Fail(ref why); }
    }
    static bool S(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool Fail(ref string why) { Log.Info($"[hopfix] Unity struct layout check failed ({why}): Rect/Bounds/Plane are read through Unity's accessors"); return false; }
}
