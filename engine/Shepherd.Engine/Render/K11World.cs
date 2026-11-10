using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>Kit #11 track gpu1 (vita/notes/kit11-gpu1.md): the world geometry path's switches and counters. Every switch is read once, from
/// the app's env file; every one is OFF unless its line says otherwise, so a run without these lines is kit #10's, call for call.
/// SHEPHERD_K11_GPU1=0 turns every one of them off at once (the switch back, whatever the other lines say).
/// The literals live in a [ProfileOnly] type, so the V3 diet's readable-key scan (Assets/Diet.cs) is unchanged.
/// - SHEPHERD_K11_UNIFORMS=1: shader values sent only when they change (GL keeps a program's uniform values between draws, so a
///   value equal to the one the program already holds is not sent again; the GPU sees the same values).
/// - SHEPHERD_K11_NOPIX=1: draws that cannot change a pixel are skipped -- every vertex fully transparent under a blend that then leaves
///   the pixel as it was, or no pixel-sampling position inside the draw's bounds (see <see cref="NoPixel"/>).
/// - SHEPHERD_K11_REUSE=1: the world renderer's change signature + world-vertex reuse: a world draw whose inputs are bit for bit the
///   last frame's (same camera, matrix, tint, geometry arrays and version) hands the backend last frame's vertices without rebuilding them.
/// - SHEPHERD_K11_GPUPROJ=1: GPU-side projection (the GL backend's projection program; world-space vertices, the camera on the GPU) for the
///   world draws it can take; not identical to the CPU path to the pixel (float rounding on another processor): owner decision.
/// - SHEPHERD_K11_TIE=unity: the sprite tie order like Unity's (orthographic cameras sort by depth along the view axis, so sprites at one
///   depth tie and keep their creation order, the later on top).
/// - SHEPHERD_K11_PACK=1: the GPU path's vertex data in the smallest format the GPU converts exactly (checked per buffer).</summary>
[ProfileOnly]
public static class K11World
{
    static string E(string k) => Environment.GetEnvironmentVariable(k);
    /// <summary>SHEPHERD_K11_GPU1=0: every switch of this track off (kit #10).</summary>
    public static readonly bool Master = E("SHEPHERD_K11_GPU1") != "0";
    public static readonly bool Uniforms = Master && E("SHEPHERD_K11_UNIFORMS") == "1";
    public static readonly bool NoPix = Master && E("SHEPHERD_K11_NOPIX") == "1";
    public static readonly bool Reuse = Master && E("SHEPHERD_K11_REUSE") == "1";
    public static readonly bool GpuProj = Master && E("SHEPHERD_K11_GPUPROJ") == "1";
    public static readonly bool Tie = Master && E("SHEPHERD_K11_TIE") == "unity";
    public static readonly bool Pack = Master && GpuProj && E("SHEPHERD_K11_PACK") == "1";
    /// <summary>Any switch of this track on: the [k11] counters are kept and logged.</summary>
    public static readonly bool Any = Uniforms || NoPix || Reuse || GpuProj || Tie;

    // ---------------- counters (last frame; reset at SceneRenderer.Frame's start; the backend's live in RenderStats) ----------------
    public static int ReuseHits, ReuseMiss, ReuseVerts, ProjDraws, ProjVerts, ProjCpu, ProjRetained, NoPixBuild, OrthoCams;
    internal static int _reuseHits, _reuseMiss, _reuseVerts, _projDraws, _projVerts, _projCpu, _projRetained, _noPixBuild, _orthoCams;
    internal static void BeginFrame() { _reuseHits = _reuseMiss = _reuseVerts = _projDraws = _projVerts = _projCpu = _projRetained = _noPixBuild = _orthoCams = 0; }
    internal static void EndFrame()
    {
        ReuseHits = _reuseHits; ReuseMiss = _reuseMiss; ReuseVerts = _reuseVerts; ProjDraws = _projDraws; ProjVerts = _projVerts; ProjCpu = _projCpu; ProjRetained = _projRetained;
        NoPixBuild = _noPixBuild; OrthoCams = _orthoCams;
        _tFrames++; _tReuseHits += _reuseHits; _tReuseMiss += _reuseMiss; _tReuseVerts += _reuseVerts; _tProjDraws += _projDraws; _tProjCpu += _projCpu; _tProjRetained += _projRetained;
        _tNoPixBuild += _noPixBuild; _tOrthoCams += _orthoCams;
        if (Reuse || GpuProj) SceneRenderer.K11Age(Runtime.Time.FrameCount);   // the kept passes of destroyed / idle cameras let go (SceneRenderer.K11Reuse.cs)
    }

    // ---------------- run totals: the end-of-run [k11] line (Boot), so a run without shots (the vlx gates) still shows what acted ----------------
    internal static long _tKeeps, _tPassesAged;   // (counted where they happen: SceneRenderer.K11Reuse.cs / K11Proj.cs)
    static long _tFrames, _tReuseHits, _tReuseMiss, _tReuseVerts, _tProjDraws, _tProjCpu, _tProjRetained, _tNoPixBuild, _tOrthoCams, _tUniSent, _tUniSkipped, _tNoPixAlpha, _tNoPixCentre, _tBackendFrames;
    /// <summary>The GL backend's frame end (its render thread under SHEPHERD_MT_RENDER=1: the only writer of these totals).</summary>
    internal static void AddBackend(RenderStats s)
    {
        _tBackendFrames++; _tUniSent += s.K11UniSent; _tUniSkipped += s.K11UniSkipped; _tNoPixAlpha += s.K11NoPixAlpha; _tNoPixCentre += s.K11NoPixCentre;
    }
    /// <summary>The end-of-run line (null with every switch off: the log is kit #10's).</summary>
    public static string Summary() => !Any ? null :
        $"[k11] gpu1 totals: switches uniforms={B(Uniforms)} nopix={B(NoPix)} reuse={B(Reuse)} gpuproj={B(GpuProj)} pack={B(Pack)} tie={B(Tie)}; {_tFrames} scene frames: reuse hit={_tReuseHits} miss={_tReuseMiss} verts not projected={_tReuseVerts} copies kept={_tKeeps}; camera passes dropped (destroyed / idle)={_tPassesAged}; " +
        $"proj draws={_tProjDraws} retained={_tProjRetained} cpu fallback={_tProjCpu}; nopix builds skipped={_tNoPixBuild}; orthographic camera passes={_tOrthoCams}; {_tBackendFrames} GL backend frames: uniforms sent={_tUniSent} " +
        $"not sent={_tUniSkipped}, nopix transparent={_tNoPixAlpha} no sampling position={_tNoPixCentre}; kept now {SceneRenderer.K11ReuseBytes() / 1024} KiB";
    static int B(bool b) => b ? 1 : 0;
    /// <summary>The [render] frame line's [k11] part (only when a switch is on: the line is kit #10's otherwise).</summary>
    public static string Line(RenderStats s) => !Any ? "" :
        $" | k11 uniforms sent={s.K11UniSent} skipped={s.K11UniSkipped} nopix alpha={s.K11NoPixAlpha} centre={s.K11NoPixCentre} build={NoPixBuild} reuse hit={ReuseHits} miss={ReuseMiss} verts={ReuseVerts} proj draws={ProjDraws} verts={ProjVerts} cpu={ProjCpu} retained={ProjRetained} orthoCams={OrthoCams} up={s.UploadBytes + s.VertexBytes}" + (Reuse ? $" reuseKiB={SceneRenderer.K11ReuseBytes() / 1024}" : "");

    // ---------------- draws that cannot change a pixel (SHEPHERD_K11_NOPIX=1) ----------------
    /// <summary>The blends under which a fragment of alpha 0 (and, for the premultiplied ones, colour 0) leaves the target exactly as it was:
    /// BLEND (src alpha, 1 - src alpha; alpha one, 1 - src alpha), ADD (src alpha, one; alpha zero, one) need alpha 0; BLEND_PREMULTIPLIED and
    /// ADD_PREMULTIPLIED (one, ...) need colour 0 as well. MOD / MUL / NONE / composed / grab modes write something even then: never skipped.</summary>
    internal static bool ClearBlend(uint blend, out bool needRgb0)
    {
        needRgb0 = blend == BLENDMODE_BLEND_PREMULTIPLIED || blend == BLENDMODE_ADD_PREMULTIPLIED;
        return blend == BLENDMODE_BLEND || blend == BLENDMODE_ADD || needRgb0;
    }
    /// <summary>The draw's vertices are all fully transparent (and colour 0 where the blend needs it): every fragment the sprite program makes
    /// then has alpha texel.a x 0 = 0 (the vertex colours are interpolated from equal zeros: zero), so the blend keeps the pixel. NaN or a
    /// non-zero value anywhere: false.</summary>
    internal static bool AllClear(ReadOnlySpan<Vertex> v, bool needRgb0)
    {
        foreach (ref readonly var x in v)
        {
            if (x.color.a != 0f) return false;
            if (needRgb0 && (x.color.r != 0f || x.color.g != 0f || x.color.b != 0f)) return false;
        }
        return true;
    }
    /// <summary>The margin around a draw's bounds within which a pixel-sampling position still counts as covered: wider than any rasterizer's
    /// sub-pixel snap (GL asks for at least 4 sub-pixel bits: 1/32 px of snap error) and the float error of the pixel -> clip -> window mapping.</summary>
    const float Margin = 0.125f;
    /// <summary>No pixel can be written by a draw whose bounds [x0, x1] x [y0, y1] (target pixels, top-down) hold no sampling position inside
    /// the drawable area [lx, hx) x [ly, hy) (the target, cut by the clip rect). The sampling positions tested are every multiple of 1/2 px
    /// -- the pixel centres under GL's convention (k + 1/2) AND under the integer convention -- so the rule holds whichever the GPU uses.
    /// NaN anywhere: false (drawn).
    /// SINGLE-SAMPLE targets only: a multisampled target (tile MSAA) has sample positions off the pixel centres, so a draw that misses every
    /// centre can still cover a sample and change the resolved pixel -- the caller must not use this rule on a multisampled target. The rule
    /// is exact on the Mac's GL; the SGX's raster convention and vitaGL's scissor are not measured on the device yet (vlx records calls only).</summary>
    internal static bool NoPixel(float x0, float x1, float y0, float y1, float lx, float hx, float ly, float hy)
    {
        if (float.IsNaN(x0 + x1 + y0 + y1)) return false;
        return NoSample(x0, x1, lx, hx) || NoSample(y0, y1, ly, hy);
    }
    static bool NoSample(float a0, float a1, float lo, float hi)
    {
        // Kit #13 speed (the X1 fix): plain comparisons instead of MathF.Max / MathF.Min. Kit #11's device run found those two CoreLib calls
        // running in the INTERPRETER (left out of the trimmed CoreLib AOT image, not inlined into this method: ~4 interpreter entries per
        // small draw, +16.5-17.5 us per draw on the device). The same decisions: a0 / a1 are never NaN here (NoPixel returns first) and lo /
        // hi are finite target / clip sizes, so max / min of the pair is the larger / smaller operand (an infinite bound included); a -0
        // where MathF.Max would give +0 compares equal in everything below.
        float s = a0 - Margin, l = lo - 0.5f; if (l > s) s = l;   // max(a0 - Margin, lo - 1/2) (lo - 1/2: an integer-convention centre at lo's edge)
        float e = a1 + Margin; if (hi < e) e = hi;   // min(a1 + Margin, hi)
        if (!(s <= e)) return true;   // outside the drawable area altogether
        // A call-free fast path (most sprites are wider than a pixel): a computed e - s >= 1 means the real interval is longer than 1/2 px
        // (one rounding of a subtraction is far below a factor 2), so [s, e] holds a multiple of 1/2 and the test below would say false too.
        if (e - s >= 1f) return false;
        return MathF.Ceiling(s * 2f) > MathF.Floor(e * 2f);   // no multiple of 1/2 in [s, e]
    }
#if !SHEPHERD_EBOOT
    /// <summary>Kit #13 speed, Mac only (not in the eboots): SHEPHERD_K13_NOSAMPLE_CHECK=1 runs this at boot -- kit #12's NoSample (MathF.Max /
    /// Min) against the fix on every combination of edge values (+-0, +-1/2 steps, the margin's neighbours, clip edges, huge / tiny /
    /// infinite values) and 4,000,000 random ones; =negctl compares against a deliberately wrong variant (fast path at 1/4 px) to show the
    /// check catches a difference. Logs one line and returns the number of differences.</summary>
    internal static long NoSampleBruteCheck(bool negctl)
    {
        static bool K12(float a0, float a1, float lo, float hi)
        {
            float s = MathF.Max(a0 - Margin, lo - 0.5f), e = MathF.Min(a1 + Margin, hi);
            if (!(s <= e)) return true;
            return MathF.Ceiling(s * 2f) > MathF.Floor(e * 2f);
        }
        static bool Wrong(float a0, float a1, float lo, float hi)
        {
            float s = a0 - Margin, l = lo - 0.5f; if (l > s) s = l;
            float e = a1 + Margin; if (hi < e) e = hi;
            if (!(s <= e)) return true;
            if (e - s >= 0.25f) return false;
            return MathF.Ceiling(s * 2f) > MathF.Floor(e * 2f);
        }
        var ed = new List<float> { 0f, -0f, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, float.Epsilon, -float.Epsilon, 1e-7f, 3e38f, -3e38f, 960f, 544f, 1e7f, 16777216f, 16777217f };
        foreach (var b in new[] { 0f, 1f, 2f, 7f, 100f, 543f, 544f, 959f, 960f })
            for (int k = -6; k <= 6; k++)
            {
                float v = b + k * 0.125f; ed.Add(v); ed.Add(-v); ed.Add(BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) + 1)); ed.Add(BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) - 1));
                float h = b + k * 0.5f; ed.Add(h); ed.Add(h + Margin); ed.Add(h - Margin);
            }
        var bounds = new[] { (0f, 960f), (0f, 544f), (10.5f, 200.25f), (0f, 0f), (100f, 100.5f), (-0f, 1f), (0f, 1e7f) };
        long n = 0, bad = 0;
        Func<float, float, float, float, bool> f = negctl ? Wrong : NoSample;
        foreach (var (lo, hi) in bounds) foreach (var a0 in ed) foreach (var a1 in ed)
        {
            if (float.IsNaN(a0 + a1)) continue;   // (NoPixel returns before NoSample on a NaN sum)
            n++; if (K12(a0, a1, lo, hi) != f(a0, a1, lo, hi)) bad++;
        }
        var r = new System.Random(13);
        for (int i = 0; i < 4_000_000; i++)
        {
            float sc = (i & 3) switch { 0 => 2f, 1 => 20f, 2 => 1200f, _ => 1e6f };
            float a0 = (float)(r.NextDouble() * sc - sc / 4), a1 = a0 + (float)(r.NextDouble() * ((i & 4) != 0 ? 1.5 : sc));
            float lo = (i & 8) != 0 ? 0f : (float)Math.Floor(r.NextDouble() * 100), hi = lo + (float)Math.Floor(r.NextDouble() * 900);
            n++; if (K12(a0, a1, lo, hi) != f(a0, a1, lo, hi)) bad++;
        }
        Log.Info($"[k13] NoSample brute check{(negctl ? " (NEGATIVE CONTROL: a wrong variant)" : "")}: {n} cases, {bad} decisions differ from kit #12's");
        return bad;
    }
#endif

    // ---------------- the sprite tie order (SHEPHERD_K11_TIE=unity) ----------------
    /// <summary>The world pass's distance key for a renderer at p: Unity's default transparency sort -- a perspective camera sorts by the
    /// distance to the camera's position (the engine's squared distance, as before), an orthographic camera by the distance along its view
    /// direction (so renderers at one depth tie, and the tie keeps their creation order: the later drawn on top). fwd = the camera's forward.</summary>
    /// <summary>The creation order of a record from its instance id (Native/Objects.cs Bind, Unity's scheme): records bound from loaded data
    /// take +2, +4, ... and records made at run time -2, -4, ... in the order they were made. The key: loaded records first, in load order,
    /// then run-time ones in creation order (the order between the two groups is not pinned by a test scene: kit7-tests 4.7's scenes are all
    /// made at run time).</summary>
    internal static long CreKey(int id) => id > 0 ? id : (1L << 32) - (long)id;
    internal static float OrthoDepth(Vector3 p, Vector3 cam, Vector3 fwd)
    {
        float dx = p.x - cam.x, dy = p.y - cam.y, dz = p.z - cam.z;
        return dx * fwd.x + dy * fwd.y + dz * fwd.z;
    }
}
