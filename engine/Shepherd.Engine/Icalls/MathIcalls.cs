using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Impl;

static class MathIcalls
{
    public static Quaternion LookRotation(Vector3 fwd, Vector3 up)
    {
        var f = fwd.normalized; if (f.sqrMagnitude < 1e-12f) return Quaternion.identity;
        var r = Vector3.Cross(up, f); if (r.sqrMagnitude < 1e-12f) r = Vector3.Cross(Mathf.Abs(f.y) < 0.99f ? Vector3.up : Vector3.right, f);
        r = r.normalized; var u = Vector3.Cross(f, r);
        float m00 = r.x, m01 = u.x, m02 = f.x, m10 = r.y, m11 = u.y, m12 = f.y, m20 = r.z, m21 = u.z, m22 = f.z;
        float tr = m00 + m11 + m22; Quaternion q;
        if (tr > 0) { float s = MathF.Sqrt(tr + 1f) * 2f; q = new((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s); }
        else if (m00 > m11 && m00 > m22) { float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f; q = new(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s); }
        else if (m11 > m22) { float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f; q = new((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s); }
        else { float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f; q = new((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s); }
        return M.QNorm(q);
    }
    static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        float d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        if (d < 0) { b = new(-b.x, -b.y, -b.z, -b.w); d = -d; }
        if (d > 0.9995f) return M.QNorm(new(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t));
        float th = MathF.Acos(d), s = MathF.Sin(th), wa = MathF.Sin((1 - t) * th) / s, wb = MathF.Sin(t * th) / s;
        return new(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb);
    }
    static Quaternion Lerp(Quaternion a, Quaternion b, float t)
    {
        if (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0) b = new(-b.x, -b.y, -b.z, -b.w);
        return M.QNorm(new(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t));
    }
    [Icall("UnityEngine.Quaternion::AngleAxis_Injected")] static object AngleAxis(object[] a) { a[2] = TransformIcalls.AxisAngle((Vector3)a[1], (float)a[0] * Mathf.Deg2Rad); return null; }
    [Icall("UnityEngine.Quaternion::FromToRotation_Injected")] static object FromTo(object[] a)
    {
        var f = ((Vector3)a[0]).normalized; var t = ((Vector3)a[1]).normalized; float d = Vector3.Dot(f, t);
        if (d > 0.999999f) { a[2] = Quaternion.identity; return null; }
        if (d < -0.999999f) { var ax = Vector3.Cross(Vector3.right, f); if (ax.sqrMagnitude < 1e-6f) ax = Vector3.Cross(Vector3.up, f); a[2] = TransformIcalls.AxisAngle(ax, MathF.PI); return null; }
        var c = Vector3.Cross(f, t); a[2] = M.QNorm(new Quaternion(c.x, c.y, c.z, 1f + d)); return null;
    }
    [Icall("UnityEngine.Quaternion::Internal_FromEulerRad_Injected")] static object FromEuler(object[] a) { a[1] = M.Euler((Vector3)a[0]); return null; }
    [Icall("UnityEngine.Quaternion::Internal_ToEulerRad_Injected")] static object ToEuler(object[] a) { a[1] = M.ToEuler((Quaternion)a[0]); return null; }
    [Icall("UnityEngine.Quaternion::Internal_ToAxisAngleRad_Injected")] static object ToAxisAngle(object[] a)
    {
        var q = M.QNorm((Quaternion)a[0]); float ang = 2f * MathF.Acos(Math.Clamp(q.w, -1f, 1f)); float s = MathF.Sqrt(Math.Max(0, 1 - q.w * q.w));
        a[1] = s < 1e-6f ? Vector3.right : new Vector3(q.x / s, q.y / s, q.z / s); a[2] = ang; return null;
    }
    [Icall("UnityEngine.Quaternion::Inverse_Injected")] static object Inverse(object[] a) { a[1] = M.QInv((Quaternion)a[0]); return null; }
    [Icall("UnityEngine.Quaternion::Lerp_Injected")] static object QLerp(object[] a) { a[3] = Lerp((Quaternion)a[0], (Quaternion)a[1], Mathf.Clamp01((float)a[2])); return null; }
    [Icall("UnityEngine.Quaternion::LerpUnclamped_Injected")] static object QLerpU(object[] a) { a[3] = Lerp((Quaternion)a[0], (Quaternion)a[1], (float)a[2]); return null; }
    [Icall("UnityEngine.Quaternion::Slerp_Injected")] static object QSlerp(object[] a) { a[3] = Slerp((Quaternion)a[0], (Quaternion)a[1], Mathf.Clamp01((float)a[2])); return null; }
    [Icall("UnityEngine.Quaternion::SlerpUnclamped_Injected")] static object QSlerpU(object[] a) { a[3] = Slerp((Quaternion)a[0], (Quaternion)a[1], (float)a[2]); return null; }
    [Icall("UnityEngine.Quaternion::LookRotation_Injected")] static object Look(object[] a) { a[2] = LookRotation((Vector3)a[0], (Vector3)a[1]); return null; }

    [Icall("UnityEngine.Matrix4x4::TRS_Injected")] static object TRS(object[] a) { a[3] = M.TRS((Vector3)a[0], (Quaternion)a[1], (Vector3)a[2]); return null; }
    [Icall("UnityEngine.Matrix4x4::Inverse_Injected")] static object Inv(object[] a) { a[1] = M.Inverse((Matrix4x4)a[0]); return null; }
    [Icall("UnityEngine.Matrix4x4::Inverse3DAffine_Injected")] static object InvAff(object[] a) { a[1] = M.Inverse((Matrix4x4)a[0]); return true; }
    [Icall("UnityEngine.Matrix4x4::Transpose_Injected")] static object Transpose(object[] a) { var m = (Matrix4x4)a[0]; var r = new Matrix4x4(); for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) r[i, j] = m[j, i]; a[1] = r; return null; }
    [Icall("UnityEngine.Matrix4x4::GetDeterminant_Injected")] static object Det(object[] a)
    {
        var m = (Matrix4x4)a[0];
        return m.m00 * (m.m11 * (m.m22 * m.m33 - m.m23 * m.m32) - m.m12 * (m.m21 * m.m33 - m.m23 * m.m31) + m.m13 * (m.m21 * m.m32 - m.m22 * m.m31))
             - m.m01 * (m.m10 * (m.m22 * m.m33 - m.m23 * m.m32) - m.m12 * (m.m20 * m.m33 - m.m23 * m.m30) + m.m13 * (m.m20 * m.m32 - m.m22 * m.m30))
             + m.m02 * (m.m10 * (m.m21 * m.m33 - m.m23 * m.m31) - m.m11 * (m.m20 * m.m33 - m.m23 * m.m30) + m.m13 * (m.m20 * m.m31 - m.m21 * m.m30))
             - m.m03 * (m.m10 * (m.m21 * m.m32 - m.m22 * m.m31) - m.m11 * (m.m20 * m.m32 - m.m22 * m.m30) + m.m12 * (m.m20 * m.m31 - m.m21 * m.m30));
    }
    public static Matrix4x4 Ortho(float l, float r, float b, float t, float n, float f)
    {
        var m = Matrix4x4.identity; m.m00 = 2f / (r - l); m.m11 = 2f / (t - b); m.m22 = -2f / (f - n);
        m.m03 = -(r + l) / (r - l); m.m13 = -(t + b) / (t - b); m.m23 = -(f + n) / (f - n); return m;
    }
    public static Matrix4x4 Perspective(float fovDeg, float aspect, float n, float f)
    {
        float c = 1f / MathF.Tan(fovDeg * Mathf.Deg2Rad * .5f); var m = new Matrix4x4();
        m.m00 = c / aspect; m.m11 = c; m.m22 = -(f + n) / (f - n); m.m23 = -2f * f * n / (f - n); m.m32 = -1f; return m;
    }
    [Icall("UnityEngine.Matrix4x4::Ortho_Injected")] static object OrthoI(object[] a) { a[6] = Ortho((float)a[0], (float)a[1], (float)a[2], (float)a[3], (float)a[4], (float)a[5]); return null; }
    [Icall("UnityEngine.Matrix4x4::Perspective_Injected")] static object PerspI(object[] a) { a[4] = Perspective((float)a[0], (float)a[1], (float)a[2], (float)a[3]); return null; }
    [Icall("UnityEngine.Matrix4x4::Frustum_Injected")] static object Frustum(object[] a)
    {
        float l = (float)a[0], r = (float)a[1], b = (float)a[2], t = (float)a[3], n = (float)a[4], f = (float)a[5]; var m = new Matrix4x4();
        m.m00 = 2 * n / (r - l); m.m02 = (r + l) / (r - l); m.m11 = 2 * n / (t - b); m.m12 = (t + b) / (t - b); m.m22 = -(f + n) / (f - n); m.m23 = -2 * f * n / (f - n); m.m32 = -1; a[6] = m; return null;
    }
    [Icall("UnityEngine.Matrix4x4::LookAt_Injected")] static object LookAtM(object[] a) { a[3] = M.TRS((Vector3)a[0], LookRotation((Vector3)a[1] - (Vector3)a[0], (Vector3)a[2]), Vector3.one); return null; }
    [Icall("UnityEngine.Matrix4x4::GetRotation_Injected")] static object GetRot(object[] a) { var m = (Matrix4x4)a[0]; a[1] = LookRotation(new Vector3(m.m02, m.m12, m.m22), new Vector3(m.m01, m.m11, m.m21)); return null; }
    [Icall("UnityEngine.Matrix4x4::GetLossyScale_Injected")] static object GetScale(object[] a) { var m = (Matrix4x4)a[0]; a[1] = new Vector3(new Vector3(m.m00, m.m10, m.m20).magnitude, new Vector3(m.m01, m.m11, m.m21).magnitude, new Vector3(m.m02, m.m12, m.m22).magnitude); return null; }
    [Icall("UnityEngine.Matrix4x4::IsIdentity_Injected")] static object IsId(object[] a) => ((Matrix4x4)a[0]) == Matrix4x4.identity;
    [Icall("UnityEngine.Matrix4x4::ValidTRS_Injected")] static object Valid(object[] a) => true;
    [Icall("UnityEngine.Matrix4x4::CompareApproximately_Injected")] static object Cmp(object[] a) { var x = (Matrix4x4)a[0]; var y = (Matrix4x4)a[1]; float th = (float)a[2]; for (int i = 0; i < 16; i++) if (MathF.Abs(x[i] - y[i]) > th) return false; return true; }

    [Icall("UnityEngine.Mathf::ClosestPowerOfTwo")] static object Cp2(object[] a) { int v = (int)a[0]; if (v <= 0) return 0; int p = 1; while (p < v) p <<= 1; return (p - v) > (v - (p >> 1)) ? p >> 1 : p; }
    [Icall("UnityEngine.Mathf::NextPowerOfTwo")] static object Np2(object[] a) { int v = (int)a[0]; if (v <= 0) return 0; int p = 1; while (p < v) p <<= 1; return p; }
    [Icall("UnityEngine.Mathf::IsPowerOfTwo")] static object IsP2(object[] a) { int v = (int)a[0]; return v > 0 && (v & (v - 1)) == 0; }
    [Icall("UnityEngine.Mathf::GammaToLinearSpace")] static object G2L(object[] a) { float c = (float)a[0]; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
    [Icall("UnityEngine.Mathf::LinearToGammaSpace")] static object L2G(object[] a) { float c = (float)a[0]; return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f; }
    [Icall("UnityEngine.Mathf::FloatToHalf")] static object F2H(object[] a) => BitConverter.HalfToUInt16Bits((Half)(float)a[0]);
    [Icall("UnityEngine.Mathf::HalfToFloat")] static object H2F(object[] a) => (float)BitConverter.UInt16BitsToHalf((ushort)a[0]);
    [Icall("UnityEngine.Mathf::PerlinNoise")] static object Perlin(object[] a) => Noise.Perlin((float)a[0], (float)a[1]);
    [Icall("UnityEngine.Mathf::PerlinNoise1D")] static object Perlin1(object[] a) => Noise.Perlin((float)a[0], 0f);
    [Icall("UnityEngine.Mathf::CorrelatedColorTemperatureToRGB_Injected")] static object Kelvin(object[] a) { a[1] = Color.white; return null; }

    [Icall("UnityEngine.Vector3::Slerp_Injected")] static object V3Slerp(object[] a) { a[3] = SlerpV((Vector3)a[0], (Vector3)a[1], Mathf.Clamp01((float)a[2])); return null; }
    [Icall("UnityEngine.Vector3::SlerpUnclamped_Injected")] static object V3SlerpU(object[] a) { a[3] = SlerpV((Vector3)a[0], (Vector3)a[1], (float)a[2]); return null; }
    static Vector3 SlerpV(Vector3 a, Vector3 b, float t)
    {
        float la = a.magnitude, lb = b.magnitude; if (la < 1e-6f || lb < 1e-6f) return Vector3.LerpUnclamped(a, b, t);
        var q = Slerp(Quaternion.identity, FromToQ(a / la, b / lb), t); return M.Rotate(q, a / la) * Mathf.LerpUnclamped(la, lb, t);
    }
    static Quaternion FromToQ(Vector3 f, Vector3 t) { var o = new object[3]; o[0] = f; o[1] = t; FromTo(o); return (Quaternion)o[2]; }
    [Icall("UnityEngine.Vector3::RotateTowards_Injected")] static object RotTowards(object[] a)
    {
        var cur = (Vector3)a[0]; var tgt = (Vector3)a[1]; float maxRad = (float)a[2], maxMag = (float)a[3];
        float ang = Vector3.Angle(cur, tgt) * Mathf.Deg2Rad; float t = ang < 1e-6f ? 1f : Mathf.Min(1f, maxRad / ang);
        var dir = SlerpV(cur.normalized, tgt.normalized, t).normalized; float mag = Mathf.MoveTowards(cur.magnitude, tgt.magnitude, maxMag);
        a[4] = dir * mag; return null;
    }
    [Icall("UnityEngine.Vector3::OrthoNormalize2")] static object Ortho2(object[] a) { var n = ((Vector3)a[0]).normalized; var t = (Vector3)a[1]; t = (t - Vector3.Dot(t, n) * n).normalized; a[0] = n; a[1] = t; return null; }
    [Icall("UnityEngine.Vector3::OrthoNormalize3")] static object Ortho3(object[] a)
    {
        var n = ((Vector3)a[0]).normalized; var t = (Vector3)a[1]; t = (t - Vector3.Dot(t, n) * n).normalized; var b = (Vector3)a[2];
        b = (b - Vector3.Dot(b, n) * n - Vector3.Dot(b, t) * t).normalized; a[0] = n; a[1] = t; a[2] = b; return null;
    }
}

static class Noise
{
    static readonly int[] P = BuildP();
    static int[] BuildP() { var r = new System.Random(0); var p = Enumerable.Range(0, 256).OrderBy(_ => r.Next()).ToArray(); return p.Concat(p).ToArray(); }
    static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
    static float Grad(int h, float x, float y) { h &= 7; float u = h < 4 ? x : y, v = h < 4 ? y : x; return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -2f * v : 2f * v); }
    public static float Perlin(float x, float y)
    {
        int xi = (int)MathF.Floor(x) & 255, yi = (int)MathF.Floor(y) & 255; x -= MathF.Floor(x); y -= MathF.Floor(y);
        float u = Fade(x), v = Fade(y);
        int aa = P[P[xi] + yi], ab = P[P[xi] + yi + 1], ba = P[P[xi + 1] + yi], bb = P[P[xi + 1] + yi + 1];
        float r = Lerp(v, Lerp(u, Grad(aa, x, y), Grad(ba, x - 1, y)), Lerp(u, Grad(ab, x, y - 1), Grad(bb, x - 1, y - 1)));
        return Math.Clamp((r + 1f) * 0.5f, 0f, 1f);
    }
    static float Lerp(float t, float a, float b) => a + t * (b - a);
}
