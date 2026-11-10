namespace Shepherd.Engine.Render;

/// <summary>Kit #11 track gpu2 (vita/notes/kit11-gpu2.md): the switches of the passes / lighting / effects rows in the GL backend. Every row
/// has its own env-file switch; <c>SHEPHERD_K11_GPU2=0</c> turns them all off (= kit #10's backend, call for call).
/// - <c>SHEPHERD_K11_LUTBLEND</c>=1 (OPT-IN since the review: no visible transition has been image-diffed yet): the post effect's LUT blend
///   (the game's Amplify effect blends its LUT pair into a 1024x32 "blend cache" while its BlendAmount is not 0 -- the lighting transitions);
///   unset = kit #10's unblended LUTs (a cut instead of the game's fade). With it on, the blend program is built with the boot program set
///   (and kept in the gxp cache with it), so the first transition never waits for the shader compiler.
/// - <c>SHEPHERD_K11_LUTCACHE</c> (default on while the blend is on): the blended LUTs are kept while their inputs (the four LUT textures and
///   the amount) are unchanged; the game rebuilds them every frame. 0 = rebuilt every frame (the game's way: the same pixels, two more small
///   passes a frame).
/// - <c>SHEPHERD_K11_WATERCOLOR</c>=1 (OPT-IN, owner pending): UI materials whose look is an owner-local program (IUiLooks: the confirmation
///   dialog's / Verify Files window's "Watercolor/Watercolor-UI" panels, drawn before as their white 4x4 texture -- the "white panels") are
///   drawn by that program (RenderKit.SubmitGpuMeshTex), built with the boot program set. Unset = kit #10's white quads.
/// - <c>SHEPHERD_K11_PASSLOG</c>=1: one [k11] line per 300 frames with the pass counters (render-target switches, full-screen / copy
///   pixels); the counters themselves are always kept (RenderStats) and cost two integer adds per pass.
/// The literals (switch names, the texture-settings key below) live in this [ProfileOnly] type, so the V3 diet's readable-key scan
/// (Assets/Diet.cs) is kit #10's: a key read through <see cref="WrapUKey"/> is a field load at the read site, not a string literal there.</summary>
[ProfileOnly]
public static class K11Gpu
{
    static string Env(string n) => Environment.GetEnvironmentVariable(n);
    /// <summary>SHEPHERD_K11_GPU2=0: every gpu2 row off (kit #10).</summary>
    public static readonly bool Master = Env("SHEPHERD_K11_GPU2") != "0";
    public static readonly bool LutBlend = Master && Env("SHEPHERD_K11_LUTBLEND") == "1";
    public static readonly bool LutCache = LutBlend && Env("SHEPHERD_K11_LUTCACHE") != "0";
    public static readonly bool PassLog = Master && Env("SHEPHERD_K11_PASSLOG") == "1";   // (integration review: under the master switch too)
    public static readonly bool WatercolorUi = Master && Env("SHEPHERD_K11_WATERCOLOR") == "1";
    /// <summary>The texture-settings key the opt-in UI programs read for their samplers' addressing (Assets/Loader.cs; only with
    /// <see cref="WatercolorUi"/>). A static field, not a const: a const would put the literal back into the Loader's IL.</summary>
    public static readonly string WrapUKey = "m_WrapU";
    public static string Describe() => !Master ? "off (SHEPHERD_K11_GPU2=0: kit #10)"
        : $"LUT blend {(LutBlend ? "ON" : "off (SHEPHERD_K11_LUTBLEND=1)")}, blended LUTs kept while unchanged {(LutCache ? "on" : "off")}, owner-local UI programs (white panels) {(WatercolorUi ? "ON" : "off (SHEPHERD_K11_WATERCOLOR=1)")}";
}
