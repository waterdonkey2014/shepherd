using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>The renderer's contract with a GPU API (V4 part 2, vita/notes/v4-renderer.md). SceneRenderer turns the scene graph
/// into indexed 2D triangles in target pixels (top-down, y down) and hands them over here; a backend only rasterizes.
/// Semantics are SDL_Renderer's, because the SDL path is the reference image:
/// - vertex colour already carries the tint (and, for <see cref="BLENDMODE_BLEND_PREMULTIPLIED"/>, the premultiplied alpha)
///   so consecutive draws stay mergeable; textures are straight-alpha RGBA8, top-down
/// - <c>blend</c> is an SDL_BlendMode; an untextured draw uses the renderer's draw blend mode (NONE), like SDL_RenderGeometry
/// - texture addressing is SDL's AUTO: a draw whose uvs leave [0,1] wraps, otherwise it clamps
/// - <c>sdf</c>: the texture is a TextMeshPro distance-field atlas (alpha = distance); the backend renders alpha =
///   smoothstep over [0.44, 0.56] with white rgb (SDL: baked into the texture on upload; GL: in the fragment shader)
/// Frame order: Begin → world draws → EndWorldPass (Stylizer post-effect) → UI draws → EndFrame → Present / ReadPixels.</summary>
public interface IRenderBackend : IDisposable
{
    string Name { get; }
    int W { get; }
    int H { get; }
    /// <summary>Upload straight-alpha RGBA8 pixels (top-down). <paramref name="owned"/>: the caller lets the backend modify
    /// <paramref name="rgba"/> in place (it is a temporary buffer, not the texture's kept copy).</summary>
    IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf = false, bool owned = false);
    void DestroyTexture(IntPtr tex);
    /// <summary>An offscreen render target (FBO render-to-texture). The handle is also a texture handle for <see cref="Draw"/>.</summary>
    IntPtr CreateTarget(int w, int h);
    /// <summary>Route draws to <paramref name="target"/>; <see cref="IntPtr.Zero"/> = the frame's main target.</summary>
    void SetTarget(IntPtr target);
    /// <summary>Start a frame: main target bound, clip off, cleared to the colour.</summary>
    void Begin(float r, float g, float b, float a);
    /// <summary>Polish round 2: clear the current target (main or <see cref="SetTarget"/>) to the colour, alpha included, ignoring the clip
    /// (SDL_RenderClear's semantics). Used for camera targets' own clears, GL.Clear and RenderTexture contents.</summary>
    void ClearTarget(float r, float g, float b, float a);
    void Clip(int x, int y, int w, int h);
    void NoClip();
    void Draw(IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx);
    /// <summary>The world pass is complete: the camera's post effects and the scene's Stylizer pass, both owner-local (DerivedRender;
    /// without them nothing happens here but a flush). UI draws follow, unstylized.</summary>
    void EndWorldPass();
    /// <summary>All of the frame's draws have been issued (a batching backend flushes here).</summary>
    void EndFrame();
    /// <summary>Wait until the GPU has finished everything issued so far (benchmarks only).</summary>
    void Finish();
    void Present();
    /// <summary>The main target's pixels: top-down RGBA8, w*h*4 bytes. Null on failure.</summary>
    byte[] ReadPixels(out int w, out int h);
    void Screenshot(string path);
    /// <summary>Counters of the last completed frame (see <see cref="RenderStats"/>).</summary>
    RenderStats Stats { get; }
}

/// <summary>Optional: a backend that takes DXT1/DXT5 blocks as they are (GL with S3TC; vitaGL on the Vita). Enabled with
/// SHEPHERD_GL_DXT=1. <paramref name="topDownBlocks"/> come from <see cref="DxtBlocks.FlipY"/>; <paramref name="rgba"/> decodes the
/// same texture for a backend that cannot (the SDL half of compare mode). Zero = not taken, the caller uploads RGBA.</summary>
public interface ICompressedUpload
{
    bool Dxt { get; }
    IntPtr CreateCompressedTexture(int w, int h, int unityFormat, byte[] topDownBlocks, bool linear, Func<byte[]> rgba);
}

/// <summary>A backend that can take an SDF atlas as its distance channel alone (1 B/px), the way it cuts an RGBA8 SDF upload down
/// to GL_ALPHA anyway. Returns IntPtr.Zero when it would not cut (e.g. GL core / SHEPHERD_GL_SDF=bake|rgba / over the size cap):
/// the caller then uploads RGBA8 as before.</summary>
public interface IAlphaUpload
{
    IntPtr CreateSdfAlpha8Texture(int w, int h, byte[] a8, bool linear);
}

/// <summary>Kit #6 (c): the compressed upload from blocks in native memory (the caller owns and frees them after the call returns): the
/// pack's DXT image is read outside the GC heap (Assets/PackAssets.cs), so a 2048 x 2048 texture's 4 MiB never lands in the Mono arena.</summary>
public interface ICompressedUploadPtr
{
    IntPtr CreateCompressedTexture(int w, int h, int unityFormat, IntPtr topDownBlocks, int length, bool linear);
}

/// <summary>Kit #6 (c): the same upload from w x h bytes in native memory (the caller owns and frees them after the call returns): the SDF
/// atlas's distance channel is decoded outside the GC heap (Assets/PackAssets.cs), so its 4 MiB never lands in the Mono arena.</summary>
public interface IAlphaUploadPtr
{
    IntPtr CreateSdfAlpha8Texture(int w, int h, IntPtr a8, bool linear);
}

#if !SHEPHERD_NO_K8
/// <summary>Kit #8 boot track (Runtime/K8Video.cs, the real video): a texture whose pixels the hardware video decoder writes straight into its
/// memory -- GL on the Vita host: a vitaGL RGBA texture made with no data and its own memory pointer (vglGetTexDataPointer, shepgl_tex_data).
/// <paramref name="data"/> = IntPtr.Zero when the backend has none (the caller then draws nothing: kit #7's black picture).</summary>
public interface IVideoTexture
{
    IntPtr CreateVideoTexture(int w, int h, out IntPtr data);
}
#endif

/// <summary>A backend that runs the Stylizer as one full-screen GPU pass (GL; the null backend only counts it). The owner-local Stylizer
/// (DerivedRender.Stylizer) gathers the live component's parameters, uploads the LUTs and the layer map through the backend, then calls
/// this. GL: a no-op without the owner-local shader.</summary>
public interface IGpuStylizer : IRenderBackend
{
    void RunStylizer(in GlBackend.StylizeArgs a);
}

/// <summary>Fidelity round 1: a backend that can add a per-vertex colour after the texture x vertex-colour product (GL's sprite+ADD
/// program; the null backend counts it): out = c x A + B, with A and B per vertex (the owner-local lit model's multiplier and added
/// colour). a = 1 marks a premultiplied draw (the added colour is then scaled by the fragment's alpha).</summary>
public interface IAddDraw
{
    bool SupportsAdd { get; }
    void DrawAdd(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add);
}

/// <summary>Kit #6 (track gpulight, vita/notes/kit6-gpulight.md): a static mesh whose per-vertex work runs in a vertex program instead of
/// on the CPU (GL; the null backend only counts it). The caller hands the same immutable float array every time (its GPU copy is made on
/// the first draw and kept until <see cref="ReleaseGpuMesh"/>); attribute k of a vertex is floats [4k, 4k + 4) of its <c>stride</c> floats
/// (bound to a_pos, a_color, a_uv, a_add), vertices [first, first + count) are drawn as triangles, the program's vec4 uniforms u_p0 ..
/// u_p&lt;n-1&gt; get <c>uniforms</c> and u_xform the bound target's pixel-to-clip mapping (as sprite.vert's), the texture is on unit 0 (Zero:
/// white; the untextured draw blend applies, as for Draw), in order with the other draws and under the current clip. The programs are
/// built on first use, outside the boot program set and the gxp cache.</summary>
public interface IGpuMesh
{
    /// <summary>The program built from these two shader files (the engine's Render/Shaders or the owner-local assembly's): built on the first
    /// call (SHEPHERD_MT_RENDER=1: a sync point), then remembered; 0 = not available (no source, or the driver's compiler refused it: one log
    /// line), the caller draws on the CPU.</summary>
    int GpuProgram(string name, string vertexFile, string fragmentFile);
    void DrawGpuMesh(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr tex, uint blend);
    /// <summary>Kit #11 gpu2 (contract Api 5): the same draw with two more textures, on units 1 and 2 (samplers u_tex1 / u_tex2; Zero: white),
    /// and each texture's addressing: wrap bit k = 1 repeats texture k (0 = clamped, as DrawGpuMesh). A program's u_tex1 / u_tex2 get their
    /// units when it is built.</summary>
    void DrawGpuMeshTex(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr tex, IntPtr tex1, IntPtr tex2, int wrap, uint blend);
    /// <summary>The array will not be drawn again: its GPU copy is freed.</summary>
    void ReleaseGpuMesh(float[] data);
}

/// <summary>Kit #12 menusa (SHEPHERD_K12_UIREPLAY=1, Render/K12Ui.cs): recorded command lists for fixed screens + incremental UI re-batching.
/// SceneRenderer's fast UI pass opens a recording (K12UiBegin), hands every UI draw to K12Draw with a key -- the renderer's retained block
/// (its vertex array) when the draw's vertices are that block, else null -- and whether the block is unchanged since last frame (reused, no
/// write this frame), and closes it (K12UiEnd). A draw matches last frame's draw at the same position when it is unchanged and has the same
/// key, texture, blend, counts and clip. While every draw so far matches, the backend queues nothing; at the end it replays last frame's batches from the
/// vertex buffer it kept (no copy, no upload) -- or, at the first draw that differs, takes last frame's batches up to that point as its own
/// and goes on as before. K12Break: something the recording cannot follow (an owner-local UI draw) -- the rest of the pass runs as before
/// and nothing is kept.</summary>
public interface IK12UiReplay
{
    void K12UiBegin();
    void K12Draw(object key, bool unchanged, IntPtr texH, uint blend, bool sdf, Span<Vertex> v, Span<int> idx);
    void K12Break();
    void K12UiEnd();
}

/// <summary>Kit #11 gpu1 (SHEPHERD_K11_GPUPROJ=1, Render/SceneRenderer.K11Proj.cs): world draws whose projection runs on the GPU. The vertices
/// carry WORLD x / y in position (z in the parallel span), colour and uv as the CPU path makes them; the pass carries the camera's
/// view-projection, pixel rect and target height. Consecutive draws of one pass batch like Draw's; draw order with Draw / the other calls is
/// kept. False = not taken (too many vertices, no program): nothing happened, the caller builds the draw on the CPU.</summary>
public interface IK11Proj
{
    /// <summary>The projection program (built on first use, outside the boot set and the gxp cache; a refusal: false for the run).</summary>
    bool ProjAvailable { get; }
    bool DrawProj(IntPtr tex, uint blend, Span<Vertex> v, Span<float> z, Span<int> idx, in K11ProjPass pass);
}
/// <summary>A camera pass as the projection program needs it (SceneRenderer.Project's inputs besides the vertex).</summary>
public struct K11ProjPass
{
    public UnityEngine.Matrix4x4 Vp; public float X, Y, W, H, Gh;
}
/// <summary>Fidelity round 1: a backend that runs the colour-grading post pass on the GPU (GL; the null backend only counts it).
/// The owner-local post effects (DerivedRender.Post) gather the camera's grading settings and its light mask, then call this from
/// EndWorldPass. GL: false without the owner-local shader.</summary>
public interface IGpuPost : IRenderBackend
{
    bool RunLut(in LutArgs a);
}
/// <summary>The grading pass: shadow LUT, lit LUT, light mask (a render target; Zero = none, everything takes the shadow LUT), the
/// exposure and whether the camera is HDR (then exposure, dither and the above-1 boost apply). MaskScale: the part of the mask target
/// that maps onto the screen (1, 1 when the mask is screen-sized).</summary>
public struct LutArgs
{
    public IntPtr Lut1, Lut2, Mask; public float Exposure, MaskScaleX, MaskScaleY; public bool Hdr, Linear;
    /// <summary>PPv2's classic vignette, applied after the grading in the same pass: settings (intensity x3, smoothness x5, roundness',
    /// rounded), colour (linear in a linear project), centre.</summary>
    public bool Vignette; public UnityEngine.Vector4 VigSettings, VigColor; public UnityEngine.Vector2 VigCenter;
    /// <summary>Kit #11 gpu2 (contract Api 5): the effect blends its LUT pair while its blend amount is not 0 -- shadow = lerp(Lut1, Lut1Blend,
    /// BlendAmount), lit = lerp(Lut2, Lut2Blend, BlendAmount), each made as an RGBA8 texture of the LUTs' size before the grading samples
    /// it (GL: GlBackend.RunLut, kept while its inputs are unchanged). Blend false (an Api-4 provider never sets it) = the LUTs as given.
    /// Lut2BlendFrom (review fix): the highlight LUT the blend starts from when it is not Lut2 -- the effect blends from the identity strip
    /// when it has no highlight LUT, while the unblended grading (kit #10's, also every fallback) uses Lut2 = Lut1 there; Zero = Lut2.</summary>
    public bool Blend; public IntPtr Lut1Blend, Lut2Blend, Lut2BlendFrom; public float BlendAmount;
}

/// <summary>A GPU-Stylizer backend's EndWorldPass without the post passes: the flush and the world counters. EndWorldPass = EndWorldPassBase,
/// then the owner-local post effects (DerivedRender.Post.ApplyGpu: the grading pass, RunLut) and Stylizer (DerivedRender.Stylizer.ApplyGpu:
/// RunStylizer), in that order. SHEPHERD_MT_RENDER=1 (Render/MtRender.cs) runs their parameter lookups on the main thread, when the frame is
/// recorded, and the rest where the backend runs.</summary>
public interface IWorldPassSplit
{
    void EndWorldPassBase();
}

/// <summary>Per-frame counters. DrawsIn = Draw() calls that reached the backend; DrawsOut = GPU draw calls actually issued
/// (after batching, plus full-screen passes). Breaks = why a draw could not join the previous batch.</summary>
[ProfileOnly]   // opt-in backend code: kept out of the V3 diet's key scan (Profile.cs), so the Mac default's retained keys are unchanged
public sealed class RenderStats
{
    public int DrawsIn, DrawsOut, Tris, Batches, Flushes, FullScreenPasses, Uploads, WrapDraws, WrapNpotDraws, EmptyClipDraws, Chunks, Textures, WorldDrawsIn, WorldBatches;
    /// <summary>Polish round 2: target clears (ClearTarget) and grab copies (the BlendModes backdrop copies, GL only).</summary>
    public int Clears, Grabs;
    /// <summary>Kit #11 gpu2 (GL): render-target switches this frame (each starts a new GXM scene on the Vita: the tile memory is written out
    /// and the next target's work begins), and the pixels the full-screen passes and copies wrote (present copy, grading, Stylizer, grab
    /// copies, blended LUTs) -- the pass / bandwidth counters of the kit #11 report.</summary>
    public int TargetSwitches; public long PassPixels;
    /// <summary>Kit #11 gpu1 (Render/K11World.cs; 0 with its switches off): uniform values sent / not sent again (SHEPHERD_K11_UNIFORMS), draws
    /// skipped as fully transparent / covering no sampling position (SHEPHERD_K11_NOPIX), GPU-projected draws (SHEPHERD_K11_GPUPROJ).</summary>
    public int K11UniSent, K11UniSkipped, K11NoPixAlpha, K11NoPixCentre, K11ProjDraws;
    public long UploadBytes, VertexBytes;
    public readonly int[] Breaks = new int[(int)BreakReason.Count];
    public void Reset() { DrawsIn = DrawsOut = Tris = Batches = Flushes = FullScreenPasses = Uploads = WrapDraws = WrapNpotDraws = EmptyClipDraws = Chunks = Textures = WorldDrawsIn = WorldBatches = Clears = Grabs = TargetSwitches = 0; UploadBytes = VertexBytes = PassPixels = 0; Array.Clear(Breaks); K11UniSent = K11UniSkipped = K11NoPixAlpha = K11NoPixCentre = K11ProjDraws = 0; }
    public void CopyTo(RenderStats o)
    {
        o.DrawsIn = DrawsIn; o.DrawsOut = DrawsOut; o.Tris = Tris; o.Batches = Batches; o.Flushes = Flushes; o.FullScreenPasses = FullScreenPasses; o.Uploads = Uploads;
        o.WrapDraws = WrapDraws; o.WrapNpotDraws = WrapNpotDraws; o.EmptyClipDraws = EmptyClipDraws; o.Chunks = Chunks; o.UploadBytes = UploadBytes; o.VertexBytes = VertexBytes; Array.Copy(Breaks, o.Breaks, Breaks.Length);
        o.Textures = Textures; o.WorldDrawsIn = WorldDrawsIn; o.WorldBatches = WorldBatches; o.Clears = Clears; o.Grabs = Grabs; o.TargetSwitches = TargetSwitches; o.PassPixels = PassPixels;
        o.K11UniSent = K11UniSent; o.K11UniSkipped = K11UniSkipped; o.K11NoPixAlpha = K11NoPixAlpha; o.K11NoPixCentre = K11NoPixCentre; o.K11ProjDraws = K11ProjDraws;
    }
    public override string ToString() =>
        $"draws in={DrawsIn} out={DrawsOut} (batches={Batches} [world {WorldDrawsIn}->{WorldBatches}, ui {DrawsIn - WorldDrawsIn}->{Batches - WorldBatches}], full-screen={FullScreenPasses}, flushes={Flushes}, textures={Textures}) tris={Tris} vtxKiB={VertexBytes / 1024} " +
        $"breaks[{string.Join(" ", Enumerable.Range(0, (int)BreakReason.Count).Where(i => Breaks[i] > 0).Select(i => $"{(BreakReason)i}={Breaks[i]}"))}] " +
        $"wrap={WrapDraws} (npot {WrapNpotDraws}) emptyClip={EmptyClipDraws} uploads={Uploads} ({UploadBytes / 1024} KiB)" + (Clears + Grabs > 0 ? $" clears={Clears} grabs={Grabs}" : "");
}

public enum BreakReason { First, Texture, Blend, Shader, Clip, Wrap, Chunk, Count }

/// <summary>Backend selection: <c>SHEPHERD_RENDER</c> unset (or <c>sdl</c>) = the SDL_Renderer path (Metal on the Mac) — the
/// default, unchanged; <c>gl</c> = the GLES2-subset GL backend (vitaGL-shaped, GPU Stylizer); <c>compare</c> = both at once
/// from the same draw stream (GL is the main image; screenshots also write the SDL image and log the difference);
/// <c>null</c> = draw nothing, no SDL (V6 headless runs: Render/NullBackend.cs). Any explicit value also turns on the [render] stats lines.</summary>
[ProfileOnly]
public static class Backends
{
    public static string SdlName => "sdl";
    public static readonly string Kind = (Environment.GetEnvironmentVariable("SHEPHERD_RENDER") ?? "").Trim().ToLowerInvariant();
    /// <summary>SHEPHERD_RENDER was given (any value): log per-frame render stats and the frame-time summary.</summary>
    public static bool Explicit => Kind.Length > 0;
    /// <summary>SHEPHERD_RENDER=null (V6), or the GL backend on the native shepgl host (Stage B, SHEPHERD_GL_HOST=shepgl): no SDL in the
    /// process at all. InputState.Pump skips SDL_PollEvent (input = VitaPad's native SceCtrl reader / SHEPHERD_PAD / SHEPHERD_PAD_FILE),
    /// and no backend creates an SDL window or context.</summary>
    public static readonly bool NoSdl = Kind == "null" || (Kind == "gl" && ShepGl.On);
    /// <summary>Stage B: the GL backend on the shepgl host draws to a real display (the Vita panel; the Mac rehearsal's stand-in FBO), so
    /// the boot loop presents every frame although it runs headless (deterministic frame clock, no SDL window).</summary>
    public static readonly bool PresentHeadless = Kind == "gl" && ShepGl.On;
    public static IRenderBackend Create(int w, int h, bool headless, string title = "Shepherd — Cult of the Lamb")
    {
        switch (Kind)
        {
            case "": case "sdl": return new Gfx(w, h, headless, title);
            case "gl": return new GlBackend(w, h, headless, title);
            case "compare": return new TeeBackend(new GlBackend(w, h, headless, title), new Gfx(w, h, true, title + " (SDL reference)"));
            case "null":
                if (!headless) throw new InvalidOperationException("SHEPHERD_RENDER=null is headless only (no window, no SDL): drop the 'window' argument");
                return new NullBackend(w, h);
            default:
                Console.Error.WriteLine($"SHEPHERD_RENDER='{Kind}' is not a known backend (known: sdl, gl, compare, null); using sdl");
                return new Gfx(w, h, headless, title);
        }
    }
}
