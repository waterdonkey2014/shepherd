// kit #9 fidelity, second review (vita/notes/kit9-fidelity.md section 13.1): the curve evaluation behind the tour fixture's frame f2240.
// SHEPHERD_K9_CURVE_TRACE showed the "curve" fix moves it through ONE curve: keys (0,0, tangents 0) and (1,1, tangents 0), unweighted,
// pre / post ClampForever (an ease-in-out), at t = k/12 -- an interior segment-cache-cubic evaluation, the model's 1-16 ulp region. This
// probe asks Unity's player for that curve's answers at the traced times and over sweeps (k/12, k/60, k/240, 1,000 uniform), floats as bits.
// Loaded by OracleMain -ilsuite (run.sh).
using System;
using System.Text;
using UnityEngine;

public static class ShepherdConformance
{
    static string H(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static float F(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    public static int Run(string dir, StringBuilder sb)
    {
        var c = new AnimationCurve(new Keyframe(0f, 0f, 0f, 0f), new Keyframe(1f, 1f, 0f, 0f));
        c.preWrapMode = WrapMode.ClampForever; c.postWrapMode = WrapMode.ClampForever;
        var ez = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        var k = c.keys; sb.Append("keys");
        foreach (var x in k) sb.Append('\t').Append(H(x.time)).Append(',').Append(H(x.value)).Append(',').Append(H(x.inTangent)).Append(',').Append(H(x.outTangent)).Append(',').Append((int)x.weightedMode).Append(',').Append(H(x.inWeight)).Append(',').Append(H(x.outWeight));
        sb.Append('\n');
        int n = 0;
        foreach (uint b in new uint[] { 0x3daaaaab, 0x3f2aaaab, 0x3f555556, 0x3f6aaaab })
        { float t = F(b); sb.Append("trace\t").Append(H(t)).Append('\t').Append(H(c.Evaluate(t))).Append('\t').Append(H(ez.Evaluate(t))).Append('\n'); n++; }
        foreach (int d in new[] { 12, 60, 240 })
            for (int i = 0; i <= d; i++) { float t = (float)i / (float)d; sb.Append("k/").Append(d).Append('\t').Append(H(t)).Append('\t').Append(H(c.Evaluate(t))).Append('\t').Append(H(ez.Evaluate(t))).Append('\n'); n++; }
        ulong s = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < 1000; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            float t = (float)((s >> 40) * (1.0 / 16777216.0));
            sb.Append("u\t").Append(H(t)).Append('\t').Append(H(c.Evaluate(t))).Append('\t').Append(H(ez.Evaluate(t))).Append('\n'); n++;
        }
        return n;
    }
}
