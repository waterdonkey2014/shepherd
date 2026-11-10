using System.Reflection;
using Shepherd.Engine.Native;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>The engine's side of the owner-local render code. Some render features are ports of the game's or a third party's compiled
/// shaders (vita/notes/fidelity-round1.md, "Hygiene"): the lit-shader model of the game's sprite/mesh shaders, its stencil-light mask and
/// dual-LUT colour grading (with their GLSL), and the dithering image effect behind the Stylizer component (CPU kernel and GLSL). That
/// code is derived content, so it lives only in the owner-local assembly Shepherd.Engine.Derived (engine/Derived/, git-ignored; built
/// by vita/shepherd/build-derived.sh, staged next to the game's DLLs by stage-owner.sh / sim.sh stage; on the Mac found in
/// &lt;root&gt;/runtime/derived). It is never in git, never in Shepherd.Engine.dll and never in a VPK (vita/shepherd/check_vpk.py 3b).
/// The engine defines only the interfaces below, the typed accessors of <see cref="RenderKit"/> and the generic GL plumbing (the
/// programs, IAddDraw, IGpuPost, IGpuStylizer). The assembly is loaded by name on first use (Assembly.Load, as
/// Runtime/VitaMemory.cs FebucciReplay does) and its provider must report a contract this engine loads (<see cref="MinApi"/>..<see cref="Api"/>).
/// Without it (every shareable build) the engine draws as before those features existed: unlit, ungraded, the plain EmitGlow for the
/// volumetric glows, no Stylizer pass, and the GL backend compiles only the tracked programs. SHEPHERD_DERIVED=off ignores an
/// installed assembly (A/B runs).</summary>
[ProfileOnly]   // no property-bag keys here (RenderKit holds the reads), and no FMOD types in its bodies
public static class DerivedRender
{
    /// <summary>The contract version. Bump it whenever an interface below or a <see cref="RenderKit"/> member changes shape or
    /// meaning, or is added: the provider compiles the value it was built against into itself; a provider outside
    /// <see cref="MinApi"/>..Api is not loaded (<see cref="NotLoaded"/>).</summary>
    public const int Api = 6;   // 6 (kit #13 looks): + IWorldLooks / IDerivedRenderWorld / WorldSprite (owner-local programs of world SpriteRenderers, opt-in)
                                // 5 (kit #11 gpu2): + LutArgs.Blend / Lut1Blend / Lut2Blend / BlendAmount (the post effect's blended LUT pair; an Api-4 provider never sets them),
                                //   IGpuMesh.DrawGpuMeshTex / RenderKit.SubmitGpuMeshTex, IUiLooks / IDerivedRenderUi (owner-local UI programs, opt-in)
                                // 4 (kit #6 gpulight): + RenderKit.GpuProgram / SubmitGpuMesh / ReleaseGpuMesh (IGpuMesh: static meshes drawn by a vertex program)
                                // 2 (fid1-fixup round 2): + IDerivedRender.Looks; 3 (integration with menu-speed): the engine types the provider
                                // compiles against changed shape (NGameObject.Scene became a property, SceneRenderer's draw buffers moved
                                // to its WorldEmitter): a provider built against an Api-2 engine failed per frame (MissingMethodException in
                                // the Stylizer lookup) instead of being ignored here; rebuilt (build-derived.sh) it reports 3. Also 3:
                                // ILitShading.BeginFrame returns whether the model is active this frame
    /// <summary>Kit #6 gpulight review: the oldest provider contract this engine still loads. Api 4 only ADDED engine members (IGpuMesh,
    /// RenderKit.GpuProgram / SubmitGpuMesh / ReleaseGpuMesh / CurrentTarget, BlackFrame.CoveredSoFar) and changed none, so an Api-3
    /// provider (kit #5's Derived DLL) binds and runs exactly kit #5's path: it never calls the new members, so its light spheres stay on
    /// the CPU (MEASURED-M: lit, graded, Stylizer on, stream = kit #5's). Before, an Api-3 DLL on the card was refused: the app ran unlit,
    /// ungraded and without the Stylizer pass behind one WRN line that no gate looked at. A bump that changes or removes anything a
    /// provider uses must raise MinApi to the new Api.</summary>
    public const int MinApi = 3;
    /// <summary>The loaded provider's own contract (0: none loaded).</summary>
    public static int ProviderApi { get; private set; }
    public const string AssemblyName = "Shepherd.Engine.Derived";
    static bool _tried; static IDerivedRender _p;
    /// <summary>The loaded provider, or null (no assembly, SHEPHERD_DERIVED=off, or a provider this engine does not load).</summary>
    public static IDerivedRender Provider { get { if (!_tried) Load(); return _p; } }
    // Api 3 (integ step 3): the provider's members are read once, when it is loaded (they were read through the interface on every use:
    // several calls into the owner-local module per frame, which the engine-AOT image makes as AOT -> interpreter transitions)
    static ILitShading _lighting; static IPostEffects _post; static IStylizer _stylizer; static IShaderLooks _looks;
    public static ILitShading Lighting { get { if (!_tried) Load(); return _litOff == null ? _lighting : null; } }
    public static IPostEffects Post { get { if (!_tried) Load(); return _litOff == null ? _post : null; } }
    static string _litOff;
    /// <summary>fid1-fixup (Vita defaults): the GL backend could not build a program the lit path needs (a driver's compiler refused
    /// lut or sprite+ADD at run time): no lit model and no post effects for the rest of the run -- the unlit path, the engine's
    /// EmitGlow for the volumetric glows, as without the owner-local assembly. The Stylizer is unaffected. GlBackend logs the one line.</summary>
    public static void LitPathOff(string why) { _litOff ??= why; Status = $"lit path off ({why})"; }
    public static IStylizer Stylizer { get { if (!_tried) Load(); return _stylizer; } }
    /// <summary>fid1-fixup round 2: the per-material colour rules read from the game's compiled programs (IShaderLooks), or null.</summary>
    public static IShaderLooks Looks { get { if (!_tried) Load(); return _looks; } }
    /// <summary>Kit #11 gpu2 (Api 5): the provider's owner-local UI programs (IDerivedRenderUi.UiLooks), or null -- also null unless
    /// SHEPHERD_K11_WATERCOLOR=1 (opt-in) or with an older provider.</summary>
    public static IUiLooks UiLooks { get { if (!K11Gpu.WatercolorUi && !K12Looks && !K13Looks.Logo) return null; if (!_tried) Load(); return _uiLooks; } }   // (kit #13 looks: + the logo's program)   // (switches off: nothing loaded or asked here)
    /// <summary>Kit #12 boot (SHEPHERD_K12_LOOKS=1, opt-in; vita/notes/kit12-boot.md section 1): the provider's kit #12 UI programs -- the boot loading
    /// screen's emblem (Watercolor/Watercolor-Sprite) and its rings / crown outline (COTL/FX_ParticleMaster_A_UI, two variants), transcribed from the
    /// game's compiled shaders (owner-local). The provider's claims check this and SHEPHERD_K11_WATERCOLOR each for their own shaders.</summary>
    public static readonly bool K12Looks = Runtime.K12Boot.Row("SHEPHERD_K12_LOOKS", false);
    static IUiLooks _uiLooks;
    /// <summary>Kit #13 looks (Api 6): the provider's owner-local world-sprite programs (IDerivedRenderWorld.WorldLooks), or null -- also null unless
    /// SHEPHERD_K13_WCWORLD=1 or SHEPHERD_K13_KEYART=1 (opt-in) or with an older provider.</summary>
    public static IWorldLooks WorldLooks { get { if (!K13Looks.WcWorld && !K13Looks.KeyArt) return null; if (!_tried) Load(); return _worldLooks; } }
    static IWorldLooks _worldLooks;
    /// <summary>Kit #13 looks (Api 6): the provider's TextMeshPro distance-field rule (IDerivedRenderText.TextLooks), or null -- also null unless
    /// SHEPHERD_K13_SDF=1 (opt-in) or with an older provider.</summary>
    public static ITextLooks TextLooks { get { if (!K13Looks.Sdf) return null; if (!_tried) Load(); return _textLooks; } }
    static ITextLooks _textLooks;
    /// <summary>A GLSL body the provider embeds (GlBackend.ShaderSource's third source), or null.</summary>
    public static string Shader(string file) => Provider?.Shader(file);
    /// <summary>One line for the logs: what was loaded (or why not).</summary>
    public static string Status { get; private set; } = "not loaded yet";

    static void Load()
    {
        _tried = true;
        if ((Environment.GetEnvironmentVariable("SHEPHERD_DERIVED") ?? "").Trim().ToLowerInvariant() is "off" or "0")
        { Status = "off (SHEPHERD_DERIVED)"; Log.Info("[derived] render: SHEPHERD_DERIVED=off -- unlit, ungraded, no Stylizer pass"); return; }
        Assembly asm;
        try { asm = Assembly.Load(new System.Reflection.AssemblyName(AssemblyName)); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            Status = "absent";
            Log.Info($"[derived] render: no owner-local Shepherd.Engine.Derived ({e.GetType().Name}) -- unlit, ungraded, no Stylizer pass");
            return;
        }
        try
        {
            var t = asm.GetType("Shepherd.Engine.Derived.RenderProvider", false);
            if (t == null) { NotLoaded("no provider", $"{asm.Location} has no render provider (built before the split): rebuild it (vita/shepherd/build-derived.sh)"); return; }
            var p = (IDerivedRender)Activator.CreateInstance(t);
            int api = p.Api;
            if (api < MinApi || api > Api) { NotLoaded($"api {api} not in {MinApi}..{Api}", $"provider API {api}, this engine loads {MinApi}..{Api}: rebuild it (vita/shepherd/build-derived.sh)"); return; }
            var (lit, post, sty, looks) = (p.Lighting, p.Post, p.Stylizer, p.Looks);   // (all or nothing: a member that throws leaves the provider unused)
            _p = p; _lighting = lit; _post = post; _stylizer = sty; _looks = looks; ProviderApi = api; Status = $"loaded (api {api})";
            if (api >= 5 && p is IDerivedRenderUi pu) _uiLooks = pu.UiLooks;   // kit #11 gpu2 (an Api-5 provider may add it)
            if (api >= 6 && (K13Looks.WcWorld || K13Looks.KeyArt) && p is IDerivedRenderWorld pw) _worldLooks = pw.WorldLooks;   // kit #13 looks (only asked with a row on)
            if (api >= 6 && K13Looks.Sdf && p is IDerivedRenderText pt) _textLooks = pt.TextLooks;   // kit #13 looks
            // (kit #6 gpulight review: the provider's own api -- this line printed the engine's before; vlx-run / read-logs.sh read it)
            Log.Info($"[derived] render: provider api {api} from {asm.Location} (lighting {(lit != null ? "on" : "-")}, post {(post != null ? "on" : "-")}, stylizer {(sty != null ? "on" : "-")})"
                + (api < Api ? $" -- older than this engine's api {Api}: it runs as with an api-{api} engine (it never calls the later additions)" : ""));
        }
        catch (Exception e) when (e is TypeLoadException or MissingMethodException or MissingFieldException or InvalidCastException or TargetInvocationException)
        {
            NotLoaded("incompatible", $"the owner-local provider does not fit this engine ({e.GetBaseException().Message}): rebuild it");
        }
    }

    /// <summary>Kit #6 gpulight review: an installed Derived DLL this engine does not load (a contract outside MinApi..Api, no provider,
    /// or one that does not bind): the run goes on unlit, ungraded, with the engine's EmitGlow and no Stylizer pass. On the Mac one WRN
    /// line (A/B runs with another DLL are deliberate there). Under the Vita host (device, vlx) it is a stale or wrong DLL on the card:
    /// an ERR line and the host's FAIL line ("FAIL derived: ..."), so the session ends FAIL END with the red screen instead of looking
    /// like a pass. SHEPHERD_DERIVED_STRICT=0 (env file): the one WRN line there too (kit #5's behaviour).</summary>
    static void NotLoaded(string status, string why)
    {
        Status = status;
        var msg = $"[derived] render: {why} -- unlit, ungraded, no Stylizer pass";
        if (!Diag.VitaHost.On || Environment.GetEnvironmentVariable("SHEPHERD_DERIVED_STRICT") == "0") { Log.Warn(msg); return; }
        Log.Error(msg);
        Diag.VitaHost.Fail($"derived: the card's Shepherd.Engine.Derived.dll is not loaded ({status}; this engine loads render api {MinApi}..{Api}): the app runs unlit, ungraded, without the Stylizer pass -- copy the kit's DLL to ux0:data/shepherd/managed (push-kit.sh)");
    }
}

/// <summary>What Shepherd.Engine.Derived.RenderProvider implements (a public parameterless constructor). Any member may be null. Api 3: the
/// four members are read once, when the provider is loaded (each must return the same object every time).</summary>
public interface IDerivedRender
{
    int Api { get; }
    ILitShading Lighting { get; }
    IPostEffects Post { get; }
    IStylizer Stylizer { get; }
    /// <summary>Api 2: per-material colour rules (null: the engine's generic stand-ins).</summary>
    IShaderLooks Looks { get; }
    /// <summary>The GLSL body of an owner-local shader file (GlBackend.Programs entries marked owner-local), or null.</summary>
    string Shader(string file);
}

/// <summary>The lit model of world draws. SceneRenderer calls BeginFrame once per frame, BeginCamera per camera, and Begin before each
/// normal world draw; when Begin returns true the draw's triangles go to <see cref="Draw"/> instead of the backend, then End.</summary>
public interface ILitShading
{
    /// <summary>Api 3: true = the model may light draws this frame; false = every Begin this frame would return false (and BeginCamera do
    /// nothing), so the engine makes no other call of this interface until the next frame (the menus: no per-draw calls into this module).</summary>
    bool BeginFrame();
    void BeginCamera(in Matrix4x4 viewProjection);
    /// <summary>True: this draw is lit (until <see cref="End"/>).</summary>
    bool Begin(NMaterial mat, in Matrix4x4 localToWorld, bool sprite);
    void End();
    /// <summary>Draw v[0..nv) with RenderKit.Indices[0..ni) (clip positions in RenderKit.ClipPositions), texture handle, blend.</summary>
    void Draw(Vertex[] v, int nv, int ni, IntPtr tex, uint blend);
    /// <summary>Last frame: lit draws, fogged vertices.</summary>
    (int Lit, int Fog) LastStats { get; }
}

/// <summary>The gameplay camera's post effects (grading with a light mask) and the main camera's additive volumetric glows.</summary>
public interface IPostEffects
{
    /// <summary>After the screen cameras drew the world, before EndWorldPass.</summary>
    void Prepare(IRenderBackend g, List<NBehaviour> screenCams);
    /// <summary>From the backend's EndWorldPass (GL; the null backend counts).</summary>
    void ApplyGpu(IGpuPost g);
    /// <summary>A Glow-kind mesh draw: true = drawn here; false = the engine's EmitGlow draws it.</summary>
    bool AdditiveGlow(NMesh mesh, NMaterial mat, Dictionary<int, object> mpb, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, UnityEngine.Rect pr, float gh, Vector3 camPos, Color tint);
    /// <summary>Last frame: grading passes, light-mask draws, unsupported lighting-layer draws.</summary>
    (int Lut, int MaskDraws, int MaskSkipped) LastStats { get; }
}

/// <summary>fid1-fixup round 2 (Api 2): two single-material colour rules of polish round 1 that were read from the game's compiled
/// programs and so are derived content (vita/PLAN.md Hygiene; hygiene verifier): the colour a BlendModes grab draw is drawn with, and how a
/// ramp panel (the menus' "UI/Watercolor-Background" backdrop, whose colour is its 1-D ramp texture) maps a vertex onto its ramp. The
/// engine picks the materials by name and passes the ramp texture; without a provider it draws the grab draw with its tint and a ramp
/// panel as its ramp's first texel, opaque (the fully shown panel; its reveal is then a cut instead of a wipe).</summary>
public interface IShaderLooks
{
    /// <summary>The source colour of a BlendModes grab draw of this material (the tint, and whatever the material's pass multiplies in).</summary>
    Color GrabTint(NMaterial mat, Color tint);
    /// <summary>One vertex of a ramp panel (its own uv0 / tangent arrays, index i): rewrite d's texture coordinate and colour for the
    /// ramp lookup (the texture bound is the ramp).</summary>
    void RampVertex(ref Vertex d, Vector4[] uv, Vector4[] tan, int i);
}

/// <summary>Kit #11 gpu2 (Api 5): UI materials whose look the game computes in its own shader -- per-pixel noise, ramps, clips that no
/// texture x colour rule can stand in for (the confirmation dialog's "Watercolor/Watercolor-UI" panels, drawn before as their white 4x4
/// texture). The engine asks once per shader name (ShaderInfo.Custom); a claimed UI draw is handed over with the engine's own vertices and
/// drawn by the provider (RenderKit.SubmitGpuMeshTex), or, when Draw answers false, drawn as before.</summary>
public interface IUiLooks
{
    bool Claims(string shaderName);
    /// <summary>v[0..nv) with idx[0..ni): the UI pass's vertices (target pixels; colour = vertex colour x tint, straight unless the blend
    /// is premultiplied; tex_coord = (u, 1 - v) of the mesh's uv0), the main texture, the blend. True = drawn here.</summary>
    bool Draw(NMaterial mat, Vertex[] v, int nv, int[] idx, int ni, NTexture tex, uint blend);
}
/// <summary>Kit #11 gpu2 (Api 5): what a provider implements, besides IDerivedRender, to hand the engine its UI programs.</summary>
public interface IDerivedRenderUi
{
    IUiLooks UiLooks { get; }
}

/// <summary>Kit #13 looks (Api 6): one world SpriteRenderer draw whose shader the provider claims -- the sprite's own geometry and the camera pass,
/// so the provider builds its vertices as the game's vertex program places them (a billboard turns them to the camera) and colours them by its
/// fragment program. Mpb = the renderer's property block (per-renderer values over the material's), or null.</summary>
public struct WorldSprite
{
    public NMaterial Mat; public NSprite Sprite; public Color Tint; public bool FlipX, FlipY; public Dictionary<int, object> Mpb;
    public Matrix4x4 Lw, Vp, View; public UnityEngine.Rect Pr; public float Gh; public Vector3 CamPos; public NTexture Tex; public uint Blend;
}
/// <summary>Kit #13 looks (Api 6): world materials whose look the game computes in its own shader (the Main Menu's shrine "Watercolor/Watercolor-Sprite",
/// its crowd "KeyArt/Follower", the candles' "BillboardDoodle"). The engine asks once per shader name; a claimed draw is handed over in the world
/// pass's draw order and drawn by the provider (RenderKit.SubmitGpuMeshTex) or queued: the engine calls <see cref="Flush"/> before the next draw the
/// provider did not take and at the end of each camera pass, so consecutive draws of one material can go to the GPU as one draw. Sprite answers
/// false = drawn as before. <see cref="PassEnd"/>: called at the end of every world camera pass while a row is on (after the last Flush), whether
/// or not the provider took a draw -- a provider that keeps per-frame copies of its draws (and their GPU copies) lets go of the ones no frame has
/// drawn for a while there (the Main Menu's crowd batch after the menu).</summary>
public interface IWorldLooks
{
    bool Claims(string shaderName);
    bool Sprite(in WorldSprite d);
    bool Pending { get; }
    void Flush();
    void PassEnd();
}
/// <summary>Kit #13 looks (Api 6): TextMeshPro's distance-field look exactly (SHEPHERD_K13_SDF=1). For one TMP SDF UI draw: the engine's vertices
/// (a copy: target pixels; colour = the mesh colour x the CanvasRenderer's tint, premultiplied when the UI blend is; tex_coord = the atlas uv),
/// the mesh's own colours, its second uv channel (TMP's per-glyph scale), the CanvasRenderer's tint (colour x inherited alpha, without the
/// material's _Color) and the blend the engine will draw with: BLEND_PREMULTIPLIED, TMP's own One / OneMinusSrcAlpha (a provider declines any
/// other). The provider rewrites the colours, premultiplied (COLOR0 with the material's face colour folded in), and fills one parameter vec4 per
/// vertex for the owner-local fragment program ("k13_sdf.frag", built by the backend); false = drawn as before.</summary>
public interface ITextLooks
{
    bool Sdf(NMaterial mat, Vertex[] v, int nv, Color32[] col, Vector2[] uv1, Color tint, uint blend, Span<FColor> param);
}
public interface IDerivedRenderText
{
    ITextLooks TextLooks { get; }
}
/// <summary>Kit #13 looks (Api 6): what a provider implements, besides IDerivedRender, to hand the engine its world programs.</summary>
public interface IDerivedRenderWorld
{
    IWorldLooks WorldLooks { get; }
}

/// <summary>The Stylizer component's image effect on the world pass (SDL: CPU; GL / null: one GPU pass).</summary>
public interface IStylizer
{
    void Apply(Gfx g);
    void ApplyGpu(IGpuStylizer g);
    /// <summary>The Stylizer component the last pass found (RenderCheck compares it with its own lookup).</summary>
    NMono LastFound { get; }
    /// <summary>rendertest: the CPU reference, in place on a top-down RGBA8 frame (lut2 null = one palette, pattern null = none).</summary>
    void Kernel(byte[] rgba, int w, int h, NTexture lut1, int layers1, NTexture lut2, int layers2, float lerp, NTexture pattern, int block, float intensity);
    /// <summary>rendertest: the GPU pass's layer map for a pattern's pixels (null = no pattern).</summary>
    byte[] LayerMap(byte[] patternRgba, int pw, int ph, int layers1, int layers2);
    /// <summary>MemReport's micro-benchmark of the CPU kernel.</summary>
    void KernelBench(Action<string> write);
}
