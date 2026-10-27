// Kit #7 tests track review fix 5 (vita/notes/kit7-tests.md 9): UnityRandom's float formulas (engine Icalls/UnityRandom.cs) under both float
// evaluation modes. The 923ce39 forms (`t * min + (1 - t) * max`, the sums of squares) match Unity's per-operation float32 results only
// while the runtime rounds every float op (float32 on: today's Vita runtime, CoreCLR); the review forms round each intermediate explicitly
// ((float) = conv.r4) and give the same bits in every mode. Synthetic: Unity's xorshift128 seeded 1..300, 8 Range intervals, 6 values each.
//   U="$UNITY_APPS/Unity.app/Contents/MonoBleedingEdge"; "$U/bin/mono" "$U/lib/mono/4.5/mcs.exe" -out:fpr.exe FpRandom.cs
//   "$U/bin/mono" fpr.exe; "$U/bin/mono" --optimize=-float32 fpr.exe; dotnet exec --runtimeconfig <net8 runtimeconfig> fpr.exe
// 2026-10-20 (MEASURED-M): float32 and CoreCLR: old == new on 14,400 / 14,400 values, both hashes f642b04c67920991; -O=-float32: the old
// forms differ in 3,777 of 14,400 values (hash f64d0c5e86b9fb3a), the new forms still f642b04c67920991.
using System;
static class R {
    static uint s0 = 1, s1, s2, s3;
    static void Init(int seed) { s0 = (uint)seed; s1 = s0 * 1812433253u + 1u; s2 = s1 * 1812433253u + 1u; s3 = s2 * 1812433253u + 1u; }
    static uint Next() { uint t = s0 ^ (s0 << 11); s0 = s1; s1 = s2; s2 = s3; s3 = s3 ^ (s3 >> 19) ^ t ^ (t >> 8); return s3; }
    // kit #7 tests 923ce39 (UnityRandom.cs as committed)
    static float ValueOld() { return (float)(Next() & 0x7FFFFFu) / 8388607f; }
    static float RangeOld(float min, float max) { float t = ValueOld(); return t * min + (1f - t) * max; }
    static float RotLenOld(float x, float y, float z, float w) { return (float)Math.Sqrt(x * x + y * y + z * z + w * w); }
    static float SphOld(float z) { return (float)Math.Sqrt(1f - z * z); }
    // the review fix: every intermediate rounded explicitly (conv.r4)
    static float ValueNew() { return (float)((float)(Next() & 0x7FFFFFu) / 8388607f); }
    static float RangeNew(float min, float max) { float t = ValueNew(); return (float)((float)(t * min) + (float)((float)(1f - t) * max)); }
    static float RotLenNew(float x, float y, float z, float w) { return (float)Math.Sqrt((float)((float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z)) + (float)(w * w))); }
    static float SphNew(float z) { return (float)Math.Sqrt((float)(1f - (float)(z * z))); }
    static int Bits(float f) { return BitConverter.ToInt32(BitConverter.GetBytes(f), 0); }
    static void Main(string[] a) {
        ulong hOld = 1469598103934665603UL, hNew = 1469598103934665603UL; int n = 0, same = 0;
        float[] lo = { 0f, -1f, -10f, 0.1f, -1000f, 3.7f, -0.25f, 1e-3f };
        float[] hi = { 1f, 1f, 10f, 6.28318548f, 1000f, 123.456f, 0.75f, 7f };
        for (int seed = 1; seed <= 300; seed++) {
            for (int k = 0; k < lo.Length; k++) {
                Init(seed); float o1 = RangeOld(lo[k], hi[k]); float o2 = RangeOld(-1f, 1f); float o3 = RangeOld(-1f, 1f); float o4 = RangeOld(-1f, 1f);
                float oL = RotLenOld(o1, o2, o3, o4), oS = SphOld(o2);
                Init(seed); float n1 = RangeNew(lo[k], hi[k]); float n2 = RangeNew(-1f, 1f); float n3 = RangeNew(-1f, 1f); float n4 = RangeNew(-1f, 1f);
                float nL = RotLenNew(n1, n2, n3, n4), nS = SphNew(n2);
                foreach (var f in new[] { o1, o2, o3, o4, oL, oS }) hOld = (hOld ^ (uint)Bits(f)) * 1099511628211UL;
                foreach (var f in new[] { n1, n2, n3, n4, nL, nS }) hNew = (hNew ^ (uint)Bits(f)) * 1099511628211UL;
                n += 6; if (Bits(o1) == Bits(n1)) same++; if (Bits(o2) == Bits(n2)) same++; if (Bits(o3) == Bits(n3)) same++; if (Bits(o4) == Bits(n4)) same++; if (Bits(oL) == Bits(nL)) same++; if (Bits(oS) == Bits(nS)) same++;
            }
        }
        Console.WriteLine("old-formula hash " + hOld.ToString("x16") + "  new-formula hash " + hNew.ToString("x16") + "  old==new on " + same + " of " + n + " values");
    }
}
