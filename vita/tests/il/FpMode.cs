// Kit #7 tests track: float evaluation-mode probes (vita/notes/kit7-tests.md "the float evaluation finding"). Each fp_* returns the raw
// bits of a float / double result. A runtime that rounds every float operation to float32 (CoreCLR; Mono with -O=float32) and one that
// keeps float intermediates in double precision until a store (Mono without float32 -- what the Unity players measured) give different
// bits wherever an intermediate product or sum is not exact in float32; FMA contraction (one rounding of a*b+c) gives a third pattern.
// The inputs come from static fields (no constant folding by the C# compiler).
using System;

public static class FpModeTests
{
    static float A = -159f, B = 47.2928466796875f, C = 42f, D = 225.99755859375f, E = 1e-8f, F = 3f, G = 0.1f, H = 16777217f, I = 1.0000001f, J = 0.3f;
    static double DA = 0.1, DB = 0.2, DC = 1e16, DD = 1.0;
    static int Bits(float f) { return BitConverter.ToInt32(BitConverter.GetBytes(f), 0); }
    static int Lo(double d) { return (int)BitConverter.DoubleToInt64Bits(d); }
    static float Ident(float x) { return x; }
    public static int fp_dot2() { return Bits(A * B + C * D); }                      // the Vector2.Dot case of the oracle
    public static int fp_dot2_locals() { float p = A * B; float q = C * D; return Bits(p + q); }
    public static int fp_dot2_calls() { return Bits(Ident(A * B) + Ident(C * D)); }
    public static int fp_fma_shape() { return Bits(A * B + C); }                         // fma(A,B,C) vs (A*B)+C
    public static int fp_chain() { return Bits(G * F * F * F + G); }
    public static int fp_cancel() { return Bits((H + I) - H); }
    public static int fp_div() { return Bits(F / J * J); }
    public static int fp_mixed() { return Bits(G * (float)DA); }
    public static int fp_cmp() { return (A * B + C * D) == 1972.3348388671875f ? 1 : 0; }   // a branch on an unrounded intermediate
    public static int fp_cmp_stored() { float s = A * B + C * D; return s == 1972.3348388671875f ? 1 : 0; }
    public static int fp_tiny() { return Bits(E * E * 1e20f); }                         // a float32 underflow that double keeps
    public static int fp_conv() { return (int)(G * 1e9f); }
    public static int fp_sqrt() { return Bits((float)Math.Sqrt(A * B + C * D)); }
    public static int fp_lerp() { float t = 0.3f; return Bits(A + (D - A) * t); }
    public static int fp_dsum() { return Lo(DA + DB); }
    public static int fp_dbig() { return Lo((DC + DD) - DC); }
    public static int fp_sin() { return Bits((float)Math.Sin(B)); }
    public static int fp_atan2() { return Bits((float)Math.Atan2(B, A)); }
    public static int fp_pow() { return Bits((float)Math.Pow(G, J)); }
    public static int fp_f2i_nan() { float z = 0f; return (int)(z / z); }
    public static int fp_f2i_big() { return (int)(H * 1000f); }
    public static int fp_f2u() { return (int)(uint)(-F); }
}
