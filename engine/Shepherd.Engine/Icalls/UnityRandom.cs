using UnityEngine;
namespace Shepherd.Engine.Impl;

/// <summary>Kit #7 tests track, a differential-fuzzing finding (vita/notes/kit7-tests.md "Differential fuzzing against Unity"): Shepherd's
/// UnityEngine.Random icalls (Icalls/AppIcalls.cs) draw from System.Random (Knuth's subtractive generator) and Random.state reads back
/// zeros / ignores writes, so every seeded sequence the game makes (Random.InitState(seed) then Range / value -- seeded layouts, the
/// game's own save / restore of Random.state around them) differs from the original's. Unity's generator, as measured bit for bit in
/// the Unity 2022.3.62f2 oracle players (arm64 and x64, 300 seeds each, vita/tests/probes ProbesMath "random"):
///   InitState(seed): s0 = seed, s1 = s0 * 1812433253 + 1, s2 = s1 * 1812433253 + 1, s3 = s2 * 1812433253 + 1   (uint, 300 / 300)
///   next: xorshift128 -- t = s0 ^ (s0 &lt;&lt; 11); s0 = s1; s1 = s2; s2 = s3; s3 = s3 ^ (s3 &gt;&gt; 19) ^ t ^ (t &gt;&gt; 8)
///   value = (float)(next &amp; 0x7FFFFF) / 8388607f                                                         (1200 / 1200)
///   Range(int min, int max) = min + next % (max - min) for min &lt; max, min - next % (min - max) for max &lt; min, else min (1200 / 1200)
///   Range(float min, float max) = t * min + (1 - t) * max, t = value, in float32                            (400 / 400)
///   insideUnitCircle: theta = Range(0, 2 pi), r = sqrt(Range(0, 1)), (r cos theta, r sin theta)             (219 / 300 bit-exact
///                     in the Python model, the rest the model's own cos / sin rounding)
///   state = (s0, s1, s2, s3) as ints, get and set exactly
///   onUnitSphere: z = Range(-1, 1), a = Range(0, 2 pi), r = sqrt(1 - z z), (r cos a, r sin a, z)            (300 / 300 within 2e-6)
///   insideUnitSphere: onUnitSphere * cbrt(value)  (3 draws)                                                  (draw count 300 / 300)
///   rotation: (Range(-1, 1) x 4) normalised, negated when w &lt; 0                                              (300 / 300 within 3e-6)
///   rotationUniform: Shoemake with u1..u3 = Range(0, 1): (a1 sin 2pi u2, a1 cos 2pi u2, a2 sin 2pi u3, a2 cos 2pi u3), a1 = sqrt(1 - u1),
///                    a2 = sqrt(u1); negated when w &lt; 0                                                      (300 / 300 within 3e-6)
/// (the trigonometric results are this platform's MathF cos / sin -- Unity's own differ by an ulp between its arm64 and x64 players).
/// Review fix (kit7-tests.md "Review fixes"): every float intermediate is rounded explicitly ((float) casts = conv.r4), so the results are
/// Unity's per-operation float32 ones whatever the runtime's float evaluation -- with float32 on (today's Vita runtime, CoreCLR) the casts
/// change no bit; with intermediates kept in double (Mono -O=-float32, the runtime track's fidelity fix) the old `t * min + (1 - t) * max`
/// differed in 3,777 of 14,400 values (MEASURED-M, Unity's Mono: vita/tests/il/FpRandom.cs) and the cast form in none.
/// SHEPHERD_UNITY_RANDOM=1 (env file) turns it on; off (default) = kit #6's System.Random,
/// so every kit #6 record holds. Default-on needs the owner's decision: it changes every frame where the game draws random numbers.</summary>
[ProfileOnly]   // (reads no property-bag keys: its literals stay out of the V3 diet's readable-key scan, Assets/Diet.cs)
public static class UnityRandom
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_UNITY_RANDOM") == "1";
    static uint _s0, _s1, _s2, _s3;
    static UnityRandom() { InitState(1234); }   // (the engine's deterministic default seed, as AppIcalls' System.Random(1234))
    public static void InitState(int seed) { _s0 = (uint)seed; _s1 = _s0 * 1812433253u + 1u; _s2 = _s1 * 1812433253u + 1u; _s3 = _s2 * 1812433253u + 1u; }
    public static uint Next()
    {
        uint t = _s0 ^ (_s0 << 11);
        _s0 = _s1; _s1 = _s2; _s2 = _s3;
        _s3 = _s3 ^ (_s3 >> 19) ^ t ^ (t >> 8);
        return _s3;
    }
    public static float Value() => (float)((float)(Next() & 0x7FFFFFu) / 8388607f);
    public static float Range(float min, float max) { float t = Value(); return (float)((float)(t * min) + (float)((float)(1f - t) * max)); }
    public static int RangeInt(int min, int max)
    {
        if (min < max) return (int)((uint)min + Next() % ((uint)max - (uint)min));
        if (max < min) return (int)((uint)min - Next() % ((uint)min - (uint)max));
        return min;
    }
    public static Vector2 InsideUnitCircle()
    {
        float theta = Range(0f, 6.28318548f), r = MathF.Sqrt(Range(0f, 1f));
        return new Vector2((float)(r * MathF.Cos(theta)), (float)(r * MathF.Sin(theta)));
    }
    public static Vector3 OnUnitSphere()
    {
        float z = Range(-1f, 1f), a = Range(0f, 6.28318548f), r = MathF.Sqrt((float)(1f - (float)(z * z)));
        return new Vector3((float)(r * MathF.Cos(a)), (float)(r * MathF.Sin(a)), z);
    }
    public static Vector3 InsideUnitSphere() { var v = OnUnitSphere(); float k = MathF.Cbrt(Value()); return new Vector3((float)(v.x * k), (float)(v.y * k), (float)(v.z * k)); }
    public static Quaternion Rotation()
    {
        float x = Range(-1f, 1f), y = Range(-1f, 1f), z = Range(-1f, 1f), w = Range(-1f, 1f);
        float m = MathF.Sqrt((float)((float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z)) + (float)(w * w)));
        var q = new Quaternion((float)(x / m), (float)(y / m), (float)(z / m), (float)(w / m));
        return q.w < 0f ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;   // (Unity returns the w >= 0 one of the pair: 300 / 300)
    }
    public static Quaternion RotationUniform()
    {
        float u1 = Range(0f, 1f), u2 = Range(0f, 1f), u3 = Range(0f, 1f), a1 = MathF.Sqrt((float)(1f - u1)), a2 = MathF.Sqrt(u1);
        float p2 = (float)(6.28318548f * u2), p3 = (float)(6.28318548f * u3);
        var q = new Quaternion((float)(a1 * MathF.Sin(p2)), (float)(a1 * MathF.Cos(p2)), (float)(a2 * MathF.Sin(p3)), (float)(a2 * MathF.Cos(p3)));
        return q.w < 0f ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;
    }
    public static UnityEngine.Random.State GetState()
    {
        object st = default(UnityEngine.Random.State);   // (its fields are private: s0..s3, ints, as Unity serializes them)
        var t = typeof(UnityEngine.Random.State); const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        t.GetField("s0", F)?.SetValue(st, (int)_s0); t.GetField("s1", F)?.SetValue(st, (int)_s1); t.GetField("s2", F)?.SetValue(st, (int)_s2); t.GetField("s3", F)?.SetValue(st, (int)_s3);
        return (UnityEngine.Random.State)st;
    }
    public static void SetState(UnityEngine.Random.State s)
    {
        var t = typeof(UnityEngine.Random.State); const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        object b = s;
        _s0 = (uint)(int)(t.GetField("s0", F)?.GetValue(b) ?? 0); _s1 = (uint)(int)(t.GetField("s1", F)?.GetValue(b) ?? 0);
        _s2 = (uint)(int)(t.GetField("s2", F)?.GetValue(b) ?? 0); _s3 = (uint)(int)(t.GetField("s3", F)?.GetValue(b) ?? 0);
    }
}
