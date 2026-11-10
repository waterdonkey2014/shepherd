using System.Diagnostics;
using System.Runtime.InteropServices;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>GLES2-subset render backend (SHEPHERD_RENDER=gl), shaped for the Vita's vitaGL (vita/notes/v4-renderer.md):
/// - only OpenGL ES 2.0 entry points (Gl.cs); GLSL ES 1.00 shader bodies from Render/Shaders, compiled at run time behind a
///   per-dialect prelude (macOS legacy 2.1 = glsl120 default, 3.2 core = glsl150 with SHEPHERD_GL_CORE=1, GLES2 = essl100)
/// - a batcher: consecutive draws with the same texture / blend / shader / clip / wrap merge into one glDrawElements; the
///   frame's vertices and 16-bit indices are recorded and uploaded once per flush (≈2 per frame: world, UI)
/// - render-to-texture: the world pass draws into FBO 0; the GPU Stylizer pass reads it into FBO 1; UI draws on top;
///   no readback per frame. All offscreen targets are stored top-down (row 0 = top), like uploaded textures
/// - SDL_Renderer semantics (IRenderBackend) so the SDL path's image is the reference.
/// The context comes from SDL3 (SDL_GL_*) on the Mac; with SHEPHERD_GL_HOST=shepgl (Stage B, the Vita GL host) it is the native host's
/// vitaGL context, bound through vita/gltest/shepgl (Render/ShepGl.cs): no SDL in the process, ES 1.00 dialect, swap = vglSwapBuffers.</summary>
[ProfileOnly]
public sealed unsafe partial class GlBackend : IRenderBackend, ICompressedUpload, ICompressedUploadPtr, IGpuStylizer, IAlphaUpload, IAlphaUploadPtr, IGpuPost, IAddDraw, IWorldPassSplit, IGpuMesh, IK11Proj, IK12Stencil, IK12UiReplay, IK13Persp, IK13Sdf
#if !SHEPHERD_NO_K8
    , IVideoTexture
#endif
{
    public string Name => "gl";
    public int W { get; }
    public int H { get; }
    public IntPtr Window, Context;
    public readonly string Dialect;            // shader prelude in use
    public readonly int MaxTextureSize, TexCap;
    public readonly bool S3tc;
    /// <summary>SHEPHERD_GL_DXT=1 (and S3TC present): DXT1/DXT5 asset textures go up as their blocks, no CPU decode, 4-8x less GPU memory.</summary>
    public bool Dxt => S3tc && DxtEnv;
    static readonly bool DxtEnv = Environment.GetEnvironmentVariable("SHEPHERD_GL_DXT") == "1";
    public RenderStats Stats { get; } = new();
    readonly RenderStats _cur = new();
    readonly bool _headless, _core, _shep;
    /// <summary>The window's framebuffer: 0 (SDL's default framebuffer, the Vita display) or the Mac rehearsal's stand-in FBO.</summary>
    readonly uint _displayFbo;
    /// <summary>Present() calls and their total time (copy pass + swap, incl. any vsync wait) since start: the Vita host's heartbeat.</summary>
    public long Presents, PresentTicks;
    const int Stride = 32;                     // Sdl.Vertex: pos 2f @0, colour 4f @8, uv 2f @24
    const uint DrawBlendUntextured = BLENDMODE_NONE;   // SDL_RenderGeometry without a texture uses the renderer draw blend mode (default NONE)

    sealed class Tex
    {
        public uint Id, Fbo; public int W, H; public bool Linear, Npot, Baked, Compressed, Alpha8; public long Bytes; public int Wrap = -1; public int UseGen = -1, FrameSeen = -1;
        public bool ClipOn; public int Cx, Cy, Cw, Ch;   // per-target clip state (SDL keeps a view per render target)
    }
    readonly Dictionary<uint, Tex> _tex = new();
    readonly Tex[] _main = new Tex[2]; int _final;     // _main[_final] holds the frame so far
    Tex _white, _target, _view;                        // _view: whose clip state applies (main targets share _main[0]'s)

    sealed class Prog
    {
        public string Name; public uint Id; public int UXform, UScreen, UPat, ULut1, ULut2, UMode, UVig, UVigCol; public int Tw = -1, Th = -1; public double CompileMs; public int[] UP; public bool TexUnits;   // (TexUnits: kit #11 gpu2)
        /// <summary>Kit #11 gpu1 (SHEPHERD_K11_UNIFORMS=1): the uniform locations this program has received a value for, in first-use order
        /// (ULoc[0..UN), at most 32: a slot each), and the vec4 each slot last received, as float bits; null until the first value (made on first
        /// use). Slots are found by the location's value, so the cache works whatever numbering the GL gives (small indices on the Mac, one global
        /// counter under vlx, vitaGL's own on the device). Every Uniform4f this backend issues goes through <see cref="GlBackend.U4"/>.</summary>
        public int[] ULoc, UVal; public int UN; public uint USet;
    }
    const byte PSprite = 0, PSdf = 1, PGrab = 2;
    Prog _sprite, _sdf, _copy, _sty1, _sty2, _grab, _lut, _add;
    const byte PAdd = 3;
    readonly Prog[] _byId = new Prog[4];

    struct Batch { public Tex Tex; public uint Blend; public byte Prog; public bool Wrap, ClipOn; public int Cx, Cy, Cw, Ch, Base, I0, Ni; }
    Batch[] _b = new Batch[256]; int _nb;
    Vertex[] _v = new Vertex[16384]; int _nv;
    ushort[] _i = new ushort[49152]; int _ni;
    int _chunkBase, _gen, _frame;
    const int Ring = 4; readonly uint[] _vbo = new uint[Ring], _ibo = new uint[Ring]; int _ring;
    uint _quadPx, _quadNdc, _quadIdx, _vao;

    // GL state mirror
    Prog _curProg; Tex _curTex; uint _curBlend = 0xFFFFFFFF; bool _scissorOn; int _sx = -1, _sy = -1, _sw = -1, _sh = -1; bool _clipDirty = true; int _attrBase = int.MinValue; int _tw, _th;
    readonly HashSet<string> _warned = new();

    public GlBackend(int w, int h, bool headless, string title = "Shepherd — Cult of the Lamb")
    {
        W = w; H = h; _headless = headless;
        _shep = ShepGl.On;
        _core = !_shep && Environment.GetEnvironmentVariable("SHEPHERD_GL_CORE") == "1";
        var missing = _shep ? BindShepGl() : CreateSdlContext(w, h, headless, title);
        if (missing.Count > 0) throw new Exception("GL entry points missing: " + string.Join(", ", missing));
        if (_shep) _displayFbo = ShepGl.DisplayFbo();
        Dialect = Environment.GetEnvironmentVariable("SHEPHERD_GL_DIALECT") is { Length: > 0 } d ? d : _shep ? "essl100" : _core ? "glsl150" : "glsl120";
        if (_core) { uint vao; Gl.GenVertexArrays(1, &vao); Gl.BindVertexArray(vao); _vao = vao; }
        MaxTextureSize = Gl.Int(Gl.MAX_TEXTURE_SIZE);
        S3tc = _shep ? ShepGl.HasExtension("GL_EXT_texture_compression_s3tc") : SdlHasExtension("GL_EXT_texture_compression_s3tc");
        int cap = Environment.GetEnvironmentVariable("SHEPHERD_GL_MAXTEX") is string mt && int.TryParse(mt, out var m) && m > 0 ? m : Profile.Vita ? 4096 : MaxTextureSize;
        TexCap = Math.Min(cap, MaxTextureSize);
        Log.Info($"[gl] context: {Gl.Str(Gl.VERSION)} | {Gl.Str(Gl.RENDERER)} | GLSL {Gl.Str(Gl.SHADING_LANGUAGE_VERSION)} | dialect {Dialect} | max texture {MaxTextureSize}, cap {TexCap} | S3TC {(S3tc ? "yes" : "no")}{(Dxt ? ", DXT upload ON" : "")} | {w}x{h}{(_shep ? $" | host shepgl, display fbo {_displayFbo}" : headless ? " headless" : "")}");
        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.DITHER); Gl.Disable(Gl.STENCIL_TEST);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4); Gl.PixelStorei(Gl.PACK_ALIGNMENT, 4);

        var sw = Stopwatch.StartNew(); var absent = new List<string>(); var litFail = new List<string>(); var styFail = new List<string>();
        // kit #5 (Render/GxpCache.cs): the Vita GL host keeps the compiled set on the card; null = compile every program from source (as before)
        var cache = _shep ? GxpCache.Begin(ProgramSet, Attributes, FailTest.Count > 0) : null;
        foreach (var (name, vs, fs, def) in BootSet)   // (kit #11 gpu2 review fix: = Programs unless a gpu2 row that draws with its own program is on)
        {
            Prog p;
            if (!HasSource(vs) || !HasSource(fs)) { absent.Add(name); continue; }   // an owner-local program without the owner-local assembly
            if (name is "sprite" or "sprite+SDF" or "copy") p = Build(name, vs, fs, def, FragPrecision, cache);   // the core: a refusal is fatal, as before
            else
            {   // optional programs: a driver's compiler (vitaGL's GLSL -> SceShaccCg on the Vita) may refuse one at run time. The game then runs
                // without that feature -- never a crash -- and one line says so (below). grab (polish round 2): BlendModes backdrop draws are
                // skipped as on SDL. lut / sprite+ADD (fidelity round 1; fid1-fixup Defaults): the lit path as a whole -- lit model, light mask,
                // grading -- is turned off, so a partly lit frame never shows. stylizer*: the Stylizer pass is off.
                try { p = Build(name, vs, fs, def, FragPrecision, cache); }
                catch (Exception e)
                {
                    cache?.Failed(name);   // (kit #12: a set with a refused program is never written; kit #13 integration: an empty slot, GxpCache)
                    var why = e.Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("[gl]")).FirstOrDefault() ?? e.Message;
                    if (why.Length > 160) why = why[..160] + "...";
                    if (name == "grab") Log.Warn($"[gl] grab program unavailable, BlendModes backdrop draws will be skipped: {e.Message}");
                    else if (name == LerpName) { _lerpTried = true; Log.Warn($"[k11] FALLBACK: the shader compiler rejected the LUT blend program ({why}) -- graded with the unblended LUTs (kit #10)"); }
                    else if (name == WcUiName) { GpuBootProgram(null); Log.Warn($"[k11] FALLBACK: the shader compiler rejected '{name}' ({why}) -- its panels are drawn as before (kit #10)"); }
                    else if (K13BootProgram(name, null)) Log.Warn($"[k13] FALLBACK: the shader compiler rejected '{name}' ({why}) -- refused as on its first use in kit #12 (its draws as kit #12's refusal)");
                    else (name is "lut" or "sprite+ADD" ? litFail : styFail).Add($"'{name}' ({why})");
                    continue;
                }
            }
            switch (name) { case "sprite": _sprite = p; break; case "sprite+SDF": _sdf = p; break; case "copy": _copy = p; break; case "stylizer": _sty1 = p; break; case "grab": _grab = p; break; case "lut": _lut = p; break; case "sprite+ADD": _add = p; break;
                case LerpName: LerpReady(p); break; case WcUiName: GpuBootProgram(p); _wcBoot = p; break;   // kit #11 gpu2 (opt-in rows only)
                default: if (!K13BootProgram(name, p)) _sty2 = p; break; }
        }
        if (litFail.Count > 0)
        {
            _lut = null; _add = null;
            var why = $"the shader compiler rejected {string.Join(", ", litFail)}";
            Log.Warn($"[gl] FALLBACK: {why} -- lighting, light mask and colour grading are off for this run (the unlit path)");
            DerivedRender.LitPathOff(why);
        }
        if (styFail.Count > 0) { _sty1 = null; _sty2 = null; Log.Warn($"[gl] FALLBACK: the shader compiler rejected {string.Join(", ", styFail)} -- the Stylizer pass is off for this run"); }
        if (_k13PerspBoot) K13PerspBootDone();   // (kit #13 integration: the PERSP programs built with the boot set; after the lit path's verdict)
        _byId[PSprite] = _sprite; _byId[PSdf] = _sdf; _byId[PGrab] = _grab; _byId[PAdd] = _add;
        var built = new[] { _sprite, _sdf, _copy, _sty1, _sty2, _grab, _lut, _add, _lerp, _wcBoot }.Concat(_k13Boot ?? Enumerable.Empty<Prog>()).Where(p => p != null).ToArray();
        Log.Info($"[gl] {built.Length} programs {(cache?.State == GxpCache.Kind.Hit ? "loaded from the gxp cache and linked" : "compiled+linked")} in {sw.Elapsed.TotalMilliseconds:F1} ms ({string.Join(", ", built.Select(p => $"{p.Name} {p.CompileMs:F1}"))})");
        cache?.End();
        if (absent.Count > 0) Log.Info($"[gl] owner-local programs not built ({string.Join(", ", absent)}): no Shepherd.Engine.Derived shaders ({DerivedRender.Status})");
        if (Environment.GetEnvironmentVariable("SHEPHERD_GL_ESSL_CHECK") == "1") EsslCheck();

        fixed (uint* pv = _vbo) Gl.GenBuffers(Ring, pv);
        fixed (uint* pi = _ibo) Gl.GenBuffers(Ring, pi);
        BuildQuads();
        var white = new byte[] { 255, 255, 255, 255 };
        _white = Upload(1, 1, white, false);
        _main[0] = Target(w, h, false); _main[1] = Target(w, h, false);
        _view = _main[0]; BindTarget(_main[0]);
        var err = Gl.Errors(); if (err.Length > 0) Log.Warn("[gl] init errors:" + err);
    }

    /// <summary>The Mac path: an SDL3 window + GL context (2.1 legacy, or 3.2 core with SHEPHERD_GL_CORE=1). Never inlined: the Mono
    /// interpreter binds a method's P/Invokes when it transforms it, so on the Vita (no SDL in the eboot) none of these is bound.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    List<string> CreateSdlContext(int w, int h, bool headless, string title)
    {
        if (!SDL_Init(INIT_VIDEO | INIT_EVENTS | INIT_GAMEPAD)) throw new Exception("SDL_Init: " + Error);
        SDL_GL_ResetAttributes();
        SDL_GL_SetAttribute(GL_RED_SIZE, 8); SDL_GL_SetAttribute(GL_GREEN_SIZE, 8); SDL_GL_SetAttribute(GL_BLUE_SIZE, 8); SDL_GL_SetAttribute(GL_ALPHA_SIZE, 8);
        SDL_GL_SetAttribute(GL_DEPTH_SIZE, 0); SDL_GL_SetAttribute(GL_STENCIL_SIZE, 0); SDL_GL_SetAttribute(GL_DOUBLEBUFFER, 1);
        if (_core)
        {
            SDL_GL_SetAttribute(GL_CONTEXT_MAJOR_VERSION, 3); SDL_GL_SetAttribute(GL_CONTEXT_MINOR_VERSION, 2);
            SDL_GL_SetAttribute(GL_CONTEXT_PROFILE_MASK, GL_CONTEXT_PROFILE_CORE); SDL_GL_SetAttribute(GL_CONTEXT_FLAGS, GL_CONTEXT_FORWARD_COMPATIBLE_FLAG);
        }
        else { SDL_GL_SetAttribute(GL_CONTEXT_MAJOR_VERSION, 2); SDL_GL_SetAttribute(GL_CONTEXT_MINOR_VERSION, 1); }
        Window = SDL_CreateWindow(title, w, h, WINDOW_OPENGL | (headless ? WINDOW_HIDDEN : WINDOW_RESIZABLE));
        if (Window == IntPtr.Zero) throw new Exception("SDL_CreateWindow(OPENGL): " + Error);
        Context = SDL_GL_CreateContext(Window);
        if (Context == IntPtr.Zero) throw new Exception("SDL_GL_CreateContext: " + Error);
        SDL_GL_MakeCurrent(Window, Context);
        SDL_GL_SetSwapInterval(headless ? 0 : 1);
        return Gl.Load(n => SDL_GL_GetProcAddress(n), _core);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool SdlHasExtension(string name) => SDL_GL_ExtensionSupported(name);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void SdlWindowPixels(out int pw, out int ph) { int x, y; SDL_GetWindowSizeInPixels(Window, &x, &y); pw = x; ph = y; }
    /// <summary>The window's size in pixels: the SDL window, or the shepgl display (the Vita panel = W x H, 960x544).</summary>
    void DisplaySize(out int pw, out int ph) { if (_shep) { pw = W; ph = H; } else SdlWindowPixels(out pw, out ph); }

    /// <summary>Stage B, SHEPHERD_GL_HOST=shepgl: the native host owns the vitaGL context (it initialized it after sizing the Mono
    /// arena: vita/notes/m1-gl-app.md). Check it is up, make it current on this thread (the Mac rehearsal's CGL context), prove the
    /// float ABI of the native-call path, then bind the 51 entry points from the eboot's static table.</summary>
    List<string> BindShepGl()
    {
        int st = ShepGl.State();
        if (st == 0) throw new Exception("[gl] SHEPHERD_GL_HOST=shepgl, but the native host has no GL context (shepgl_init was not called or failed): " + ShepGl.Status());
        if (st < 0) throw new Exception("[gl] SHEPHERD_GL_HOST=shepgl: the GL context is up but vitaGL has no shader compiler (libshacccg.suprx not loaded): " + ShepGl.Status());
        if (!ShepGl.Attach()) throw new Exception("[gl] shepgl_attach failed: the host's GL context cannot be made current on this thread");
        float probe = ShepGl.AbiProbe();
        if (probe != 51.75f) throw new Exception($"[gl] shepgl_abi_probe(1, 0.5, 0.25, 2, 4, 1) returned {probe}, expected 51.75: float arguments do not reach the native side through delegate* unmanaged (VFP ABI), so ClearColor/Uniform4f would be wrong");
        Log.Info($"[gl] host shepgl: {ShepGl.Status()} | float ABI probe {probe} ok");
        var missing = Gl.Load(n => ShepGl.Proc(n), core: false);
        Log.Info($"[gl] leaf state setters {(Gl.NoTrans ? "without" : "with")} the GC transition (SHEPHERD_GL_NOTRANS={Environment.GetEnvironmentVariable("SHEPHERD_GL_NOTRANS") ?? "unset"}; kit #5 R2, 0 = kit #4)");
        return missing;
    }

    // ---------------- shaders ----------------
    /// <summary>The programs: (name, vertex file, fragment file, variant). Variants are resolved here (Expand), so the driver only ever
    /// sees #version + one precision statement + plain GLSL ES 1.00 (no #define/#ifdef for vitaGL to handle). The stylizer and lut
    /// fragment shaders are owner-local (the owner-local assembly embeds them, see <see cref="ShaderSource"/>): without it those three
    /// programs are not built, and their passes are not run (nothing calls them: DerivedRender has no stylizer / post effects).</summary>
    public static readonly (string name, string vs, string fs, string variant)[] Programs =
    {
        ("sprite", "sprite.vert", "sprite.frag", null), ("sprite+SDF", "sprite.vert", "sprite.frag", "SDF"), ("copy", "post.vert", "copy.frag", null),
        ("stylizer", "post.vert", "stylizer.frag", null), ("stylizer+TWO_PALETTES", "post.vert", "stylizer.frag", "TWO_PALETTES"),
        ("grab", "sprite.vert", "blend.frag", "GRAB"),   // polish round 2: BlendModes backdrop draws (ShaderPolicy.Kind.Grab)
        ("lut", "post.vert", "lut.frag", null),          // fidelity round 1: dual-LUT colour grading with a light mask (owner-local)
        ("sprite+ADD", "sprite.vert", "sprite.frag", "ADD"),   // fidelity round 1: lit sprites with an added colour (IAddDraw; the lit model is owner-local)
    };
    /// <summary>Kit #11 gpu2 (review fix): the programs built at start and kept in the gxp cache -- <see cref="Programs"/>, plus the program of
    /// each opt-in gpu2 row that draws with its own (the LUT blend's; the owner-local Watercolor-UI panels', which RenderKit.GpuProgram then
    /// finds by name), so that row's first use never waits for the shader compiler on the device (kit #10's logs: ~0.3 s for the smallest
    /// program, "copy"; seconds for the larger ones). Turning such a row on or off changes the set, so the gxp cache misses once (one full
    /// compile) and stores the new set. With those rows off it is Programs itself: kit #10's set, cache key and GL calls.</summary>
    static readonly (string name, string vs, string fs, string variant)[] BootSet = MakeBootSet();
    const string LerpName = "lerp", WcUiName = "watercolor-ui";
    static (string name, string vs, string fs, string variant)[] MakeBootSet()
    {
        // (kit #13 bootc: decided HERE, not read from static fields declared below -- BootSet's initializer runs first, in textual order)
        bool k13 = Environment.GetEnvironmentVariable("SHEPHERD_K13_BOOTSET") != "0" && Environment.GetEnvironmentVariable("SHEPHERD_K13_BOOTC") != "0";
        bool K13Looks = k13 && DerivedRender.K12Looks, K13Clip = k13 && K12Stencil.On;
        int nl = k13 && LooksBootSet ? K13LooksBoot(null, 0) : 0;   // (kit #13 integration, bootc x looks: looks' own programs with their rows on)
        int n = Programs.Length + (K11Gpu.LutBlend ? 1 : 0) + (K11Gpu.WatercolorUi ? 1 : 0) + (K13Looks ? 3 : 0) + (K13Clip ? 1 : 0) + nl;
        if (n == Programs.Length) return Programs;
        var set = new (string name, string vs, string fs, string variant)[n]; Array.Copy(Programs, set, Programs.Length); int k = Programs.Length;
        if (K11Gpu.LutBlend) set[k++] = (LerpName, "post.vert", "lerp.frag", null);
        if (K11Gpu.WatercolorUi) set[k++] = (WcUiName, "wc_ui.vert", "wc_ui.frag", null);   // (the owner-local provider asks for it by this name)
        if (K13Looks) { set[k++] = ("k12-wc-sprite", "k12_ui.vert", "k12_wc_sprite.frag", null); set[k++] = ("k12-fx-polar", "k12_ui.vert", "k12_fx_polar.frag", null); set[k++] = ("k12-fx-vcolor", "k12_ui.vert", "k12_fx_vcolor.frag", null); }
        if (K13Clip) set[k++] = ("k12-clip", "sprite.vert", "k12clip.frag", null);
        if (nl > 0) k += K13LooksBoot(set, k);
        return set;
    }
    /// <summary>Device kit #13 integration (vita/notes/kit13-integration.md, bootc x looks): LOOKS' NEW PROGRAMS IN THE BOOT SET, the same rule as
    /// bootc's for kit #12's: with its row on, each joins the boot set (built at start; from the gxp cache on every later start) instead of being
    /// compiled on first use on every start (kit #12's device logs: 0.3-1.2 s a program). 'sprite+PERSP' / 'sprite+ADD+PERSP' (PERSP), 'k13-wc-world'
    /// (WCWORLD), 'k13-follower' / 'k13-doodle' (KEYART), 'k13-logo' (LOGO), 'sdf-exact' (SDF) -- the names, files and variants looks' first-use
    /// paths build (GlBackend.K13.cs; the owner-local provider asks RenderKit.GpuProgram by name), so the GL programs are the same; only WHEN they
    /// are made changes (and the set changes the gxp cache's key: one full compile on the first start, a verify on the second).
    /// SHEPHERD_K13_LOOKS_BOOTSET=0 (or bootc's SHEPHERD_K13_BOOTSET=0 / SHEPHERD_K13_BOOTC=0) = looks' own rule: built on first use.
    /// Writes the entries at set[k..] (set null: only counts) and returns how many.</summary>
    static int K13LooksBoot((string name, string vs, string fs, string variant)[] set, int k)
    {
        int k0 = k;
        void Add(string name, string vs, string fs, string variant) { if (set != null) set[k] = (name, vs, fs, variant); k++; }
        if (K13Looks.Persp) { Add("sprite+PERSP", "sprite.vert", "sprite.frag", "PERSP"); Add("sprite+ADD+PERSP", "sprite.vert", "sprite.frag", "ADD+PERSP"); }
        if (K13Looks.WcWorld) Add("k13-wc-world", "k13_world.vert", "k13_wc_world.frag", null);
        if (K13Looks.KeyArt) { Add("k13-follower", "k13_world.vert", "k13_follower.frag", null); Add("k13-doodle", "k13_world.vert", "k13_doodle.frag", null); }
        if (K13Looks.Logo) Add("k13-logo", "k12_ui.vert", "k13_logo.frag", null);
        if (K13Looks.Sdf) Add("sdf-exact", "sprite.vert", "k13_sdf.frag", "ADD");
        return k - k0;
    }
    /// <summary>SHEPHERD_K13_LOOKS_BOOTSET: unset / 1 = looks' programs join the boot set with their rows on (the integration's default); 0 = looks'
    /// first-use rule. (Read inside MakeBootSet's call chain only: a static of another class, initialized on first use.)</summary>
    static bool LooksBootSet => Environment.GetEnvironmentVariable("SHEPHERD_K13_LOOKS_BOOTSET") != "0";
    /// <summary>Device kit #13, track bootc (vita/notes/kit13-bootc.md section 3): KIT #12'S NEW PROGRAMS IN THE BOOT SET. Kit #12's device logs: the
    /// loading screen's own looks (owner-local, Render/DerivedRender.cs K12Looks: 'k12-wc-sprite' 1.22 s, 'k12-fx-polar' 0.61 s, 'k12-fx-vcolor'
    /// 0.45 s at New Game) and the stencil masks' alpha clip ('k12-clip', 0.31 s at boot) were compiled on EVERY start: built on first use, outside
    /// the boot set and so outside the gxp cache. With their rows on they now join the boot set (built at start; from the gxp cache on every start
    /// after the first): RenderKit.GpuProgram finds the looks' programs by name (as kit #11's 'watercolor-ui'), the stencil pass takes the clip
    /// program. The texts are the same Build gives them on first use (variant none, the same precision), so the GL programs are the same; only
    /// WHEN they are made changes (the set changes the gxp cache's key: one full compile, on the first start). SHEPHERD_K13_BOOTSET=0 (or
    /// SHEPHERD_K13_BOOTC=0) = kit #12's set (built on first use).</summary>
    List<Prog> _k13Boot;
    /// <summary>A kit #13 boot-set program built (or refused: p null); false = not one of them.</summary>
    bool K13BootProgram(string name, Prog p)
    {
        if (name == "k12-clip") { _clipTried = true; _clipProg = p; }
        else if (name == "sprite+PERSP") { _k13PerspBoot = true; _persp = p; }   // (kit #13 integration: looks' programs in the boot set; K13PerspBootDone settles them)
        else if (name == "sprite+ADD+PERSP") { _k13PerspBoot = true; _addPersp = p; }
        else if (name == "sdf-exact") { _sdfxTried = true; _sdfx = p; }   // (refused: text drawn as before, as a first-use refusal)
        else if (name is "k12-wc-sprite" or "k12-fx-polar" or "k12-fx-vcolor" or "k13-wc-world" or "k13-follower" or "k13-doodle" or "k13-logo")
        {
            _gpuProgs ??= new List<Prog> { null }; _meshes ??= new List<MeshBuf>();
            if (p != null) { p.UP = new int[16]; for (int k = 0; k < 16; k++) p.UP[k] = Gl.UniformLoc(p.Id, "u_p" + k); }
            _gpuProgs.Add(p ?? new Prog { Name = name });
        }
        else return false;
        if (p != null) (_k13Boot ??= new List<Prog>()).Add(p);
        return true;
    }
    /// <summary>Default fragment precision for every program: highp. The Stylizer needs it (floor(c*16) near 1.0); the sprite
    /// shaders get it too so uvs into 4096-texel atlases keep sub-texel precision should a driver honour mediump as fp16.
    /// SHEPHERD_GL_FRAG_PRECISION=mediump to experiment (essl100 dialect only).</summary>
    /// <summary>SHEPHERD_GL_FAIL=&lt;program&gt;[,&lt;program&gt;...]: those programs' fragment shaders get an #error line, so the driver's compiler
    /// rejects them for real -- the test of the run-time fallback a Vita compiler refusal takes (fid1-fixup Defaults).</summary>
    static readonly HashSet<string> FailTest = new((Environment.GetEnvironmentVariable("SHEPHERD_GL_FAIL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    /// <summary>Kit #13 integration, TEST ONLY: SHEPHERD_K13_GXP_REFUSETEST=&lt;program&gt; -- that optional program throws a compiler refusal whenever it
    /// would be compiled from source, with the gxp cache ON (SHEPHERD_GL_FAIL turns the cache off, and vlx's GL accepts every shader), so the
    /// cache's empty-slot rule (GxpCache) can be gated under vlx.</summary>
    static readonly string RefuseTest = Environment.GetEnvironmentVariable("SHEPHERD_K13_GXP_REFUSETEST");
    static readonly string FragPrecision = Environment.GetEnvironmentVariable("SHEPHERD_GL_FRAG_PRECISION") is { Length: > 0 } fp ? fp : "highp";
    /// <summary>Resolve #ifdef/#ifndef/#else/#endif against the one variant name and drop full-line // comments.</summary>
    public static string Expand(string src, string variant)
    {
        var sb = new System.Text.StringBuilder(); var stack = new Stack<(bool outer, bool cond)>(); bool on = true;
        foreach (var raw in src.Split('\n'))
        {
            var l = raw.TrimEnd('\r'); var t = l.TrimStart();
            if (t.StartsWith("#ifdef ") || t.StartsWith("#ifndef "))
            {
                var vn = t.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                bool def = vn == variant || (variant != null && variant.Contains('+') && Array.IndexOf(variant.Split('+'), vn) >= 0);   // (kit #13 looks: "ADD+PERSP" = both; every older variant is one name)
                bool cond = t.StartsWith("#ifdef ") ? def : !def;
                stack.Push((on, cond)); on = on && cond; continue;
            }
            if (t.StartsWith("#else")) { var (outer, cond) = stack.Peek(); on = outer && !cond; continue; }
            if (t.StartsWith("#endif")) { on = stack.Pop().outer; continue; }
            if (!on || t.StartsWith("//") || t.Length == 0) continue;
            sb.Append(l).Append('\n');
        }
        return sb.ToString();
    }
    /// <summary>The per-dialect prelude: the only text that differs between the Mac and the Vita.</summary>
    string Prelude(bool fragment, string precision) => Dialect switch
    {
        "essl100" => "#version 100\n" + (fragment ? $"precision {precision} float;\n" : "precision highp float;\n"),
        "glsl150" => fragment ? "#version 150\n#define varying in\n#define texture2D texture\nout vec4 shepherd_FragColor;\n#define gl_FragColor shepherd_FragColor\n"
                              : "#version 150\n#define attribute in\n#define varying out\n",
        _ => "#version 120\n",
    };
    /// <summary>A shader file's text: SHEPHERD_GL_SHADERS=&lt;dir&gt; (iterate without rebuilding), else the engine's embedded
    /// Render/Shaders, else the owner-local assembly's (DerivedRender.Shader: the derived shaders live only there). Throws when none has it.</summary>
    public static string ShaderSource(string file) => TrySource(file) ?? throw new Exception("missing shader " + file + " (not embedded in the engine; owner-local Shepherd.Engine.Derived: " + DerivedRender.Status + ")");
    static string TrySource(string file)
    {
        var dir = Environment.GetEnvironmentVariable("SHEPHERD_GL_SHADERS");
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, file))) return File.ReadAllText(Path.Combine(dir, file));
        using (var s = typeof(GlBackend).Assembly.GetManifestResourceStream("Shepherd.Shaders." + file))
            if (s != null) { using var r = new StreamReader(s); return r.ReadToEnd(); }
        return DerivedRender.Shader(file);
    }
    /// <summary>Whether <see cref="ShaderSource"/> can supply the file (an owner-local one only with the owner-local assembly).</summary>
    public static bool HasSource(string file) => TrySource(file) != null;
    static uint Compile(uint kind, string src, string what, out string log)
    {
        uint s = Gl.CreateShader(kind);
        var b = System.Text.Encoding.ASCII.GetBytes(src);
        fixed (byte* p = b) { byte* pp = p; int len = b.Length; Gl.ShaderSource(s, 1, &pp, &len); }
        Gl.CompileShader(s);
        int ok = 0, n = 0; Gl.GetShaderiv(s, Gl.COMPILE_STATUS, &ok); Gl.GetShaderiv(s, Gl.INFO_LOG_LENGTH, &n);
        log = "";
        if (n > 1) { var buf = new byte[n]; fixed (byte* pb = buf) Gl.GetShaderInfoLog(s, n, null, pb); log = System.Text.Encoding.UTF8.GetString(buf, 0, n - 1).Trim(); }
        if (ok == 0) { Gl.DeleteShader(s); return 0; }
        return s;
    }
    /// <summary>The exact text handed to the driver for one shader (also what SHEPHERD_GL_DUMP_SHADERS writes out).</summary>
    public string FinalSource(string file, string variant, bool fragment, string precision) => Prelude(fragment, precision) + Expand(ShaderSource(file), variant);
    /// <summary>The gxp cache's program set: every program with both sources, the exact texts Build hands the driver, in Programs order.</summary>
    List<GxpCache.Src> ProgramSet()
    {
        var set = new List<GxpCache.Src>();
        foreach (var (name, vs, fs, variant) in BootSet)
            if (HasSource(vs) && HasSource(fs)) set.Add(new GxpCache.Src(name, FinalSource(vs, variant, false, FragPrecision), FinalSource(fs, variant, true, FragPrecision)));
        return set;
    }
    /// <summary>The attribute bindings every program gets before it links (also part of the gxp cache's key).</summary>
    static readonly (uint loc, string name)[] Attributes = { (0u, "a_pos"), (1u, "a_color"), (2u, "a_uv"), (3u, "a_add") };
    Prog Build(string name, string vsFile, string fsFile, string variant, string precision, GxpCache cache)
    {
        var t0 = Stopwatch.GetTimestamp();
        string vsrc = FinalSource(vsFile, variant, false, precision), fsrc = FinalSource(fsFile, variant, true, precision);
        if (Environment.GetEnvironmentVariable("SHEPHERD_GL_DUMP_SHADERS") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, $"{name}.{Dialect}.vert"), vsrc); File.WriteAllText(Path.Combine(dump, $"{name}.{Dialect}.frag"), fsrc);
        }
        if (FailTest.Contains(name)) fsrc += "#error SHEPHERD_GL_FAIL: this program is refused on purpose (fallback test)\n";   // a real compile error
        uint vs = 0, fs = 0;
        if (cache != null && cache.Blobs(name, out var bv, out var bf))
        {   // gxp cache hit: the shader objects get the verified compiled programs (vitaGL glShaderBinary) instead of a compile
            if (bv.Length == 0) throw new Exception("refused by this console's shader compiler before (an empty slot in the gxp cache): not compiled again");   // (kit #13 integration)
            vs = Gl.CreateShader(Gl.VERTEX_SHADER);
            if (ShepGl.GxpLoad(vs, 0, bv)) fs = Gl.CreateShader(Gl.FRAGMENT_SHADER);
            if (fs == 0 || !ShepGl.GxpLoad(fs, 1, bf))
            {
                var why = ShepGl.GxpWhy();
                Gl.DeleteShader(vs); if (fs != 0) Gl.DeleteShader(fs);
                vs = fs = 0; cache.LoadRefused(name, why);
            }
            else cache.Loaded();
        }
        if (vs == 0)
        {
            if (name == RefuseTest) throw new Exception("SHEPHERD_K13_GXP_REFUSETEST: refused on purpose (TEST ONLY)");
            vs = Compile(Gl.VERTEX_SHADER, vsrc, vsFile, out var vlog);
            if (vs == 0) throw new Exception($"[gl] {name}: {vsFile} failed to compile:\n{vlog}");
            fs = Compile(Gl.FRAGMENT_SHADER, fsrc, fsFile, out var flog);
            if (fs == 0) throw new Exception($"[gl] {name}: {fsFile} failed to compile:\n{flog}");
            if (vlog.Length > 0 || flog.Length > 0) Log.Info($"[gl] {name} compile log: {vlog} {flog}".TrimEnd());
        }
        uint p = Gl.CreateProgram(); Gl.AttachShader(p, vs); Gl.AttachShader(p, fs);
        foreach (var (loc, a) in Attributes) { var ab = System.Text.Encoding.ASCII.GetBytes(a + "\0"); fixed (byte* pa = ab) Gl.BindAttribLocation(p, loc, pa); }
        Gl.LinkProgram(p);
        int ok = 0; Gl.GetProgramiv(p, Gl.LINK_STATUS, &ok);
        if (ok == 0)
        {
            int n = 0; Gl.GetProgramiv(p, Gl.INFO_LOG_LENGTH, &n); var buf = new byte[Math.Max(1, n)]; fixed (byte* pb = buf) Gl.GetProgramInfoLog(p, buf.Length, null, pb);
            throw new Exception($"[gl] {name}: link failed: {System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0')}");
        }
        cache?.Collect(name, p, vs, fs);   // gxp cache miss / verify: the compiled shaders read back (vitaGL state is only read)
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        var pr = new Prog { Name = name, Id = p, UXform = Gl.UniformLoc(p, "u_xform"), UScreen = Gl.UniformLoc(p, "u_screen"), UPat = Gl.UniformLoc(p, "u_pat_info"), ULut1 = Gl.UniformLoc(p, "u_lut1_info"), ULut2 = Gl.UniformLoc(p, "u_lut2_info"), UMode = Gl.UniformLoc(p, "u_mode"), UVig = Gl.UniformLoc(p, "u_vig"), UVigCol = Gl.UniformLoc(p, "u_vig_color") };
        Gl.UseProgram(p);
        foreach (var (u, unit) in new[] { ("u_tex", 0), ("u_src", 0), ("u_layers", 1), ("u_lut1", 2), ("u_lut2", 3), ("u_grab", 1), ("u_mask", 1) }) { int l = Gl.UniformLoc(p, u); if (l >= 0) Gl.Uniform1i(l, unit); }
        Gl.UseProgram(0); _curProg = null;
        pr.CompileMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return pr;
    }
    /// <summary>SHEPHERD_GL_ESSL_CHECK=1: also try every shader with the GLSL ES 1.00 prelude (what vitaGL is fed). A desktop
    /// driver may refuse #version 100 outright; that is reported, not fatal. The real gate is vitaGL on the device.</summary>
    void EsslCheck()
    {
        foreach (var (name, vsf, fsf, variant) in BootSet)
        {
            if (!HasSource(vsf) || !HasSource(fsf)) { Log.Info($"[gl] essl100 check {name}: skipped (owner-local shader not present)"); continue; }
            foreach (var (file, frag) in new[] { (vsf, false), (fsf, true) })
                foreach (var prec in frag ? new[] { "highp", "mediump" } : new[] { "highp" })
                {
                    var src = "#version 100\n" + $"precision {prec} float;\n" + Expand(ShaderSource(file), variant);
                    uint s = Compile(frag ? Gl.FRAGMENT_SHADER : Gl.VERTEX_SHADER, src, file, out var log);
                    Log.Info($"[gl] essl100 check {name} {file} {prec}: {(s != 0 ? "OK" : "REFUSED")} {log.Replace('\n', ' ')}");
                    if (s != 0) Gl.DeleteShader(s);
                }
        }
    }

    // ---------------- textures and targets ----------------
    static bool Pot(int v) => v > 0 && (v & (v - 1)) == 0;
    Tex Upload(int w, int h, byte[] rgba, bool linear, uint format = Gl.RGBA, int bpp = 4)
    {
        if (rgba != null) fixed (byte* p = rgba) return Upload(w, h, p, linear, format, bpp);
        return Upload(w, h, (byte*)null, linear, format, bpp);
    }
    /// <summary>The upload itself, from a pointer (kit #6 (c): also native memory); the same GL calls and bytes as before.</summary>
    Tex Upload(int w, int h, byte* data, bool linear, uint format, int bpp)
    {
        uint id; Gl.GenTextures(1, &id);
        var t = new Tex { Id = id, W = w, H = h, Linear = linear, Npot = !Pot(w) || !Pot(h), Wrap = 0 };
        BindTex0(t);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, linear ? Gl.LINEAR : Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, linear ? Gl.LINEAR : Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        if (bpp != 4) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)format, w, h, 0, format, Gl.UNSIGNED_BYTE, data);
        if (bpp != 4) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        _tex[id] = t; t.Bytes = (long)w * h * bpp; t.Alpha8 = bpp == 1; _kEpoch++;   // (kit #12 menusa: the texture set changed)
        _cur.Uploads++; _cur.UploadBytes += t.Bytes; TotalUploads++; TotalUploadBytes += t.Bytes;
        return t;
    }
    public IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf = false, bool owned = false)
    {
        if (w <= 0 || h <= 0 || rgba == null || rgba.Length < w * h * 4) return IntPtr.Zero;
        int ow = w, oh = h;
        while (w > TexCap || h > TexCap) { rgba = Half(rgba, w, h, out w, out h); }   // the Vita's 4096 limit: box-filter down (uvs are normalized)
        if (w != ow || h != oh) Warn("cap" + ow + "x" + oh, $"[gl] texture {ow}x{oh} exceeds the {TexCap} cap: uploaded at {w}x{h}");
        if (sdf && BakeSdf) { if (!owned && w == ow) rgba = (byte[])rgba.Clone(); Gfx.BakeSdf(rgba); }   // diagnostics: SDL's CPU bake
        Tex t;
        if (sdf && !BakeSdf && !_core && SdfMode != "rgba")
        {   // the SDF program reads .a only: keep just the distance channel (GL_ALPHA, 1 B/px; GLES2/vitaGL and legacy GL have it, 3.2 core does not)
            var a8 = new byte[w * h]; for (int i = 0; i < a8.Length; i++) a8[i] = rgba[i * 4 + 3];
            t = Upload(w, h, a8, linear, Gl.ALPHA, 1);
        }
        else t = Upload(w, h, rgba, linear);
        t.Baked = sdf && BakeSdf;
        var e = Gl.Errors(); if (e.Length > 0) Warn("upload" + e, $"[gl] texture upload {w}x{h}:{e}");
        return (IntPtr)t.Id;   // sdf atlases are uploaded raw: the SDF program applies the curve
    }
#if !SHEPHERD_NO_K8
    /// <summary>Kit #8 (IVideoTexture, Runtime/K8Video.cs): an RGBA texture made with no data (the GL calls of an upload, data NULL), its memory
    /// handed out for the hardware decoder to write into (shepgl_tex_data on the Vita host; zero elsewhere: no video).</summary>
    public IntPtr CreateVideoTexture(int w, int h, out IntPtr data)
    {
        data = IntPtr.Zero;
        if (!ShepGl.On || w <= 0 || h <= 0 || w > TexCap || h > TexCap) return IntPtr.Zero;
        var t = Upload(w, h, (byte*)null, true, Gl.RGBA, 4);
        data = ShepGl.TexData(t.Id);
        return (IntPtr)t.Id;
    }
#endif
    /// <summary>IAlphaUpload: an SDF atlas given as its distance channel; the same texture CreateTexture(.., sdf: true) makes from RGBA8.</summary>
    public IntPtr CreateSdfAlpha8Texture(int w, int h, byte[] a8, bool linear)
    {
        if (w <= 0 || h <= 0 || a8 == null || a8.Length < w * h || w > TexCap || h > TexCap || BakeSdf || _core || SdfMode == "rgba") return IntPtr.Zero;
        var t = Upload(w, h, a8, linear, Gl.ALPHA, 1);
        var e = Gl.Errors(); if (e.Length > 0) Warn("upload" + e, $"[gl] texture upload {w}x{h}:{e}");
        return (IntPtr)t.Id;
    }
    /// <summary>IAlphaUploadPtr (kit #6 (c)): the same texture from the distance channel in native memory.</summary>
    public IntPtr CreateSdfAlpha8Texture(int w, int h, IntPtr a8, bool linear)
    {
        if (w <= 0 || h <= 0 || a8 == IntPtr.Zero || w > TexCap || h > TexCap || BakeSdf || _core || SdfMode == "rgba") return IntPtr.Zero;
        var t = Upload(w, h, (byte*)a8, linear, Gl.ALPHA, 1);
        var e = Gl.Errors(); if (e.Length > 0) Warn("upload" + e, $"[gl] texture upload {w}x{h}:{e}");
        return (IntPtr)t.Id;
    }
    /// <summary>SHEPHERD_GL_SDF=bake: bake distance fields on upload like SDL does (exact SDL parity for text) instead of the SDF shader.</summary>
    static readonly string SdfMode = Environment.GetEnvironmentVariable("SHEPHERD_GL_SDF") ?? "";
    static readonly bool BakeSdf = SdfMode == "bake";
    public IntPtr CreateCompressedTexture(int w, int h, int unityFormat, byte[] blocks, bool linear, Func<byte[]> rgba = null)
    {
        if (blocks == null) return IntPtr.Zero;
        fixed (byte* p = blocks) return CompressedUpload(w, h, unityFormat, p, blocks.Length, linear);
    }
    /// <summary>ICompressedUploadPtr (kit #6 (c)): the same upload from native memory.</summary>
    public IntPtr CreateCompressedTexture(int w, int h, int unityFormat, IntPtr blocks, int length, bool linear) =>
        blocks == IntPtr.Zero ? IntPtr.Zero : CompressedUpload(w, h, unityFormat, (byte*)blocks, length, linear);
    /// <summary>The compressed upload itself (the GL calls of kit #5's array version, from a pointer).</summary>
    IntPtr CompressedUpload(int w, int h, int unityFormat, byte* blocks, int length, bool linear)
    {
        int bb = DxtBlocks.BlockBytes(unityFormat);
        if (!S3tc || bb == 0 || w > TexCap || h > TexCap || length < (w + 3) / 4 * ((h + 3) / 4) * bb) return IntPtr.Zero;
        uint id; Gl.GenTextures(1, &id);
        var t = new Tex { Id = id, W = w, H = h, Linear = linear, Npot = !Pot(w) || !Pot(h), Wrap = 0 };
        BindTex0(t);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, linear ? Gl.LINEAR : Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, linear ? Gl.LINEAR : Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        int size = (w + 3) / 4 * ((h + 3) / 4) * bb;
        Gl.CompressedTexImage2D(Gl.TEXTURE_2D, 0, DxtBlocks.GlFormat(unityFormat), w, h, 0, size, blocks);
        var e = Gl.Errors();
        if (e.Length > 0) { Warn("dxt" + e, $"[gl] compressed upload {w}x{h} fmt {unityFormat}:{e} -> RGBA fallback"); Gl.DeleteTextures(1, &id); _curTex = null; return IntPtr.Zero; }
        _tex[id] = t; _kEpoch++; t.Bytes = size; t.Compressed = true; _cur.Uploads++; _cur.UploadBytes += size; CompressedUploads++; CompressedBytes += size; TotalUploads++; TotalUploadBytes += size;
        return (IntPtr)id;
    }
    public long CompressedUploads, CompressedBytes, TotalUploads, TotalUploadBytes;
    /// <summary>Resident GPU memory of every texture and render target this backend holds, in MiB (Stage B heartbeat).</summary>
    public double ResidentMiB() { long b = 0; foreach (var t in _tex.Values) b += t.Bytes; return b / 1048576.0; }
    /// <summary>Resident GPU textures right now (excluding the two main targets and the 1x1 white).</summary>
    public string TextureSummary()
    {
        long rgba = 0, dxt = 0, a8 = 0; int nr = 0, nd = 0, na = 0, nt = 0;
        foreach (var t in _tex.Values) { if (t == _white || t == _main[0] || t == _main[1]) continue; if (t.Fbo != 0) nt++; if (t.Compressed) { nd++; dxt += t.Bytes; } else if (t.Alpha8) { na++; a8 += t.Bytes; } else { nr++; rgba += t.Bytes; } }
        var top = string.Join(", ", _tex.Values.Where(t => !t.Compressed && !t.Alpha8 && t != _white && t != _main[0] && t != _main[1]).OrderByDescending(t => t.Bytes).Take(4).Select(t => $"{t.W}x{t.H}{(t.Baked ? " sdf-baked" : "")}"));
        return $"resident textures: {nr} RGBA8 = {rgba / 1048576.0:F1} MiB (largest {top}), {na} SDF alpha8 = {a8 / 1048576.0:F1} MiB, {nd} DXT = {dxt / 1048576.0:F1} MiB ({nt} extra targets); main targets 2 x {W * H * 4 / 1048576.0:F1} MiB; uploaded since start: {TotalUploads} textures, {TotalUploadBytes / 1048576.0:F1} MiB ({CompressedUploads} compressed, {CompressedBytes / 1048576.0:F1} MiB)";
    }
    static byte[] Half(byte[] src, int w, int h, out int nw, out int nh)
    {
        nw = Math.Max(1, w / 2); nh = Math.Max(1, h / 2); var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
            for (int c = 0; c < 4; c++)
            {
                int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1), y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                dst[(y * nw + x) * 4 + c] = (byte)((src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c] + 2) / 4);
            }
        return dst;
    }
    public void DestroyTexture(IntPtr tex)
    {
        if (tex == IntPtr.Zero || !_tex.TryGetValue((uint)tex, out var t) || t == _white) return;
        if (_kOn) K12Break();   // kit #12 menusa (UIREPLAY): the recording's draws so far are queued first (they may sample it)
        if (t.UseGen == _gen && (_nb > 0 || _npb > 0)) Flush();      // a pending batch still samples it (SDL flushes in this case too; kit #11 gpu1: a projected one too)
        if (_curTex == t) _curTex = null;
        uint id = t.Id;
        if (t.Fbo != 0) FreeTarget(id, t.Fbo); else Gl.DeleteTextures(1, &id);
        _tex.Remove(id); _kEpoch++;
        if (_bc1 != null || _bc2 != null || _bkA1 != null) BlendForget(t);   // kit #11 gpu2 (opt-in LUT blend): no GL call
    }
    // Kit #13 fix (vita/notes/kit13-fix.md; device kit #13 STEP 1): a render target's memory stays the GPU's for some frames after its last draw. vitaGL
    // binds a framebuffer lazily: the scene that fills a target ends at the next draw on another framebuffer, and glDeleteFramebuffers frees a deleted
    // target's texture at once when vgl_framecount - last_frame > 4 -- last_frame moves when a texture is sampled, never when it is rendered into. So
    // a target drawn and released in one frame (the game's cursor: a Blit into a RenderTexture, Release) was freed BEFORE its scene reached the GPU, and
    // the GPU's pixels landed on a free chunk of the VRAM pool: at the next pick of that chunk the mspace died on them. The two deletes now wait
    // SHEPHERD_K13_TARGETHOLD frames (default 5: four swaps pass first, as long as vitaGL's own purge list keeps a sampled texture; 0 = at once, kit #13 as
    // shipped). Every draw stays as it was: only the delete calls move, and the block stays taken for those frames. All GL calls here run on the backend's
    // thread, in order.
    static readonly int TargetHold = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K13_TARGETHOLD"), out var hold) && hold >= 0 ? hold : 5;
    sealed class HeldTarget { public uint Id, Fbo; public int Frame; public HeldTarget Next; }
    HeldTarget _held, _heldLast;
    void FreeTarget(uint id, uint fbo)
    {
        if (TargetHold == 0) { Gl.DeleteTextures(1, &id); Gl.DeleteFramebuffers(1, &fbo); return; }
        var h = new HeldTarget { Id = id, Fbo = fbo, Frame = _frame + TargetHold };
        if (_heldLast != null) _heldLast.Next = h; else _held = h;
        _heldLast = h;
    }
    void FreeHeld(bool all)
    {
        while (_held != null && (all || _frame >= _held.Frame))
        {
            uint id = _held.Id, f = _held.Fbo; Gl.DeleteTextures(1, &id); Gl.DeleteFramebuffers(1, &f);
            _held = _held.Next; if (_held == null) _heldLast = null;
        }
    }
    Tex Target(int w, int h, bool linear)
    {
        var t = Upload(w, h, null, linear);
        uint fbo; Gl.GenFramebuffers(1, &fbo); t.Fbo = fbo;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, t.Id, 0);
        var st = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (st != Gl.FRAMEBUFFER_COMPLETE) throw new Exception($"[gl] FBO {w}x{h} incomplete: 0x{st:X4}");
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _target?.Fbo ?? _displayFbo);
        return t;
    }
    /// <summary>Sampled with linear filtering, like an SDL target texture (SDL's default scale mode). The two main targets are NEAREST (the Stylizer reads texel centres).</summary>
    public IntPtr CreateTarget(int w, int h) { Flush(); var t = Target(w, h, linear: true); return (IntPtr)t.Id; }
    public void SetTarget(IntPtr target)
    {
        var t = target == IntPtr.Zero ? _main[_final] : _tex.TryGetValue((uint)target, out var tt) && tt.Fbo != 0 ? tt : null;
        if (t == null) { Warn("badtarget", "[gl] SetTarget: not a render target"); return; }
        BindTarget(t); _view = target == IntPtr.Zero ? _main[0] : t;
    }
    void BindTarget(Tex t)
    {
        if (_target == t) return;
        Flush();
        _cur.TargetSwitches++;   // kit #11 gpu2: a new GXM scene on the Vita (counted only; the calls are kit #10's)
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, t.Fbo);
        Gl.Viewport(0, 0, t.W, t.H); _tw = t.W; _th = t.H; _target = t; _clipDirty = true;
    }
    void BindTex0(Tex t) { if (_curTex == t) return; Gl.BindTexture(Gl.TEXTURE_2D, t.Id); _curTex = t; }

    // ---------------- frame ----------------
    public void Begin(float r, float g, float b, float a)
    {
        Flush();
        if (_stOn) StencilReset();   // kit #12 boot: a state left on (never by the UI pass, which ends with it off) goes off before the frame
        if (K13Speed.MaskDetach) K13StencilDetachIdle();   // kit #13 speed: the stencil buffer off the main targets after frames without a masked draw (off: no call)
        _final = 0; _view = _main[0]; BindTarget(_main[0]);
        _view.ClipOn = false;
        if (_scissorOn) { Gl.Disable(Gl.SCISSOR_TEST); _scissorOn = false; }
        Gl.ClearColor(r, g, b, a); Gl.Clear(Gl.COLOR_BUFFER_BIT);
        _clipDirty = true;
    }
    /// <summary>Polish round 2: clear the bound target (main or SetTarget's), scissor off, alpha included.</summary>
    public void ClearTarget(float r, float g, float b, float a)
    {
        if (_target == null) BindTarget(_main[_final]);
        Flush();
        if (_scissorOn) { Gl.Disable(Gl.SCISSOR_TEST); _scissorOn = false; }
        Gl.ClearColor(r, g, b, a); Gl.Clear(Gl.COLOR_BUFFER_BIT);
        _clipDirty = true; _cur.Clears++;
    }
    public void Clip(int x, int y, int w, int h) { _view.ClipOn = true; _view.Cx = x; _view.Cy = y; _view.Cw = w; _view.Ch = h; }
    public void NoClip() => _view.ClipOn = false;

    public void Draw(IntPtr texH, uint blend, bool sdf, Span<Vertex> v, Span<int> idx) => DrawCore(texH, blend, sdf, v, idx, default);
    /// <summary>Fidelity round 1: a draw whose per-vertex additive colours go through the sprite+ADD program (falls back to Draw when
    /// that program is unavailable).</summary>
    public bool SupportsAdd => _add != null;
    public void DrawAdd(IntPtr texH, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add)
    {
        if (SceneRenderer.LitW is { } lw && K13Looks.Persp) { DrawAddW(texH, blend, v, idx, add, lw); return; }   // kit #13 looks: a lit draw under a perspective camera (null: as before)
        if (_add == null) DrawCore(texH, blend, false, v, idx, default); else DrawCore(texH, blend, false, v, idx, add);
    }
    /// <summary>(kit #13 looks: <paramref name="pprog"/> = PPersp / PAddPersp -- <paramref name="add"/> then carries the folded w stream; 0 = as before)</summary>
    void DrawCore(IntPtr texH, uint blend, bool sdf, Span<Vertex> v, Span<int> idx, Span<FColor> add, byte pprog = 0)
    {
        if (v.Length == 0 || idx.Length == 0 || idx.Length % 3 != 0) return;       // SDL_RenderGeometry refuses these too
        Tex t = null;
        if (texH != IntPtr.Zero && !_tex.TryGetValue((uint)texH, out t)) return;  // unknown texture: SDL fails the call
        int nv = v.Length;
        foreach (var i in idx) if ((uint)i >= (uint)nv) return;                     // SDL: "indices out of bounds"
        uint bl = t == null ? DrawBlendUntextured : blend;
        var bt = t ?? _white;
        byte prog = t != null && sdf && !t.Baked ? PSdf : PSprite;
        bool grab = ShaderPolicy.IsGrab(bl); if (grab) { if (_grab == null) return; prog = PGrab; }   // polish round 2: a BlendModes backdrop draw
        bool hasAdd = !add.IsEmpty && !grab; if (hasAdd) prog = pprog != 0 && !sdf ? pprog : PAdd;
        if (K11World.NoPix && SkipNoPixel(bl, grab, hasAdd, v)) return;   // kit #11 gpu1: a draw that cannot change a pixel (off: no call)
        if (_npb > 0) ProjFlush();   // kit #11 gpu1: GPU-projected draws queued before this one go first
        bool wrap = false;
        if (t != null) foreach (ref readonly var vx in v) { float uu = vx.tex_coord.x, vv = vx.tex_coord.y; if (uu < 0f || vv < 0f || uu > 1f || vv > 1f) { wrap = true; break; } }   // SDL_TEXTURE_ADDRESS_AUTO
        var view = _view;
        _cur.DrawsIn++; _cur.Tris += idx.Length / 3;
        if (wrap) { _cur.WrapDraws++; if (bt.Npot) _cur.WrapNpotDraws++; }
        if (view.ClipOn && (view.Cw <= 0 || view.Ch <= 0)) _cur.EmptyClipDraws++;
        if (_target == null) BindTarget(_main[_final]);
        if (grab && !GrabBackdrop(v, idx)) { _cur.DrawsIn--; _cur.Tris -= idx.Length / 3; return; }   // entirely outside the target
        if (nv > 65536) { DrawUnindexed(bt, bl, prog, wrap, view, v, idx); return; }   // (an ADD draw this big would lose its added colour: none exists)
        if (_nv - _chunkBase + nv > 65536) { _chunkBase = _nv; _cur.Chunks++; }
        EnsureCapacity(nv, idx.Length);
        bool merged = false;
        if (_nb > 0)
        {
            ref var last = ref _b[_nb - 1];
            var why = last.Base != _chunkBase ? BreakReason.Chunk : last.Tex != bt ? BreakReason.Texture : last.Blend != bl ? BreakReason.Blend : last.Prog != prog ? BreakReason.Shader
                : last.Wrap != wrap ? BreakReason.Wrap : last.ClipOn != view.ClipOn || (view.ClipOn && (last.Cx != view.Cx || last.Cy != view.Cy || last.Cw != view.Cw || last.Ch != view.Ch)) ? BreakReason.Clip : BreakReason.Count;
            if (why == BreakReason.Count) { last.Ni += idx.Length; merged = true; }
            else _cur.Breaks[(int)why]++;
        }
        else _cur.Breaks[(int)BreakReason.First]++;
        if (!merged)
        {
            if (_nb == _b.Length) Array.Resize(ref _b, _b.Length * 2);
            _b[_nb++] = new Batch { Tex = bt, Blend = bl, Prog = prog, Wrap = wrap, ClipOn = view.ClipOn, Cx = view.Cx, Cy = view.Cy, Cw = view.Cw, Ch = view.Ch, Base = _chunkBase, I0 = _ni, Ni = idx.Length };
        }
        bt.UseGen = _gen;
        if (hasAdd)
        {   // the added colours ride in a parallel stream: zeros up to here (vertices of plain draws), then this draw's
            if (_va.Length < _nv + nv) Array.Resize(ref _va, Math.Max(_va.Length * 2, _nv + nv));
            if (_vaValid < _nv) Array.Clear(_va, _vaValid, _nv - _vaValid);
            add.Slice(0, nv).CopyTo(_va.AsSpan(_nv)); _vaValid = _nv + nv; _vaUsed = true;
        }
        v.CopyTo(_v.AsSpan(_nv));
        int rebase = _nv - _chunkBase;
        for (int k = 0; k < idx.Length; k++) _i[_ni + k] = (ushort)(idx[k] + rebase);
        _nv += nv; _ni += idx.Length;
    }
    /// <summary>Kit #11 gpu1, SHEPHERD_K11_NOPIX=1 (Render/K11World.cs): true = this draw is not queued, because no pixel of the bound target can
    /// change: (a) every vertex fully transparent (colour 0 too for the premultiplied blends) under a blend that then keeps the pixel -- not for
    /// the backdrop (grab) or lit (ADD) programs, whose output is more than texel x vertex colour; or (b) a small draw (at most 16 vertices: the
    /// bounds loop stays cheap) whose bounds, cut to the target and the clip rect, hold no pixel-sampling position. The texture was already
    /// resolved and uploaded by the caller (SceneRenderer.SubmitIdx), the stream recorder and the black-frame watch already saw the draw.
    /// (b) holds for single-sample targets only (K11World.NoPixel): every target this backend makes today is single-sample; a multisampled
    /// target (tile MSAA, the AA-setting row) must turn (b) off for its draws.</summary>
    bool SkipNoPixel(uint bl, bool grab, bool hasAdd, Span<Vertex> v)
    {
        if (!grab && !hasAdd && K11World.ClearBlend(bl, out bool rgb0) && K11World.AllClear(v, rgb0)) { _cur.K11NoPixAlpha++; return true; }
        if (v.Length > 16) return false;
        float x0 = v[0].position.x, x1 = x0, y0 = v[0].position.y, y1 = y0;
        for (int i = 1; i < v.Length; i++) { var q = v[i].position; if (q.x < x0) x0 = q.x; if (q.x > x1) x1 = q.x; if (q.y < y0) y0 = q.y; if (q.y > y1) y1 = q.y; }
        var tg = _target ?? _main[_final]; var view = _view;
        float lx = 0, ly = 0, hx = tg.W, hy = tg.H;
        if (view.ClipOn) { lx = Math.Max(lx, view.Cx); ly = Math.Max(ly, view.Cy); hx = Math.Min(hx, view.Cx + Math.Max(0, view.Cw)); hy = Math.Min(hy, view.Cy + Math.Max(0, view.Ch)); }
        if (!K11World.NoPixel(x0, x1, y0, y1, lx, hx, ly, hy)) return false;
        _cur.K11NoPixCentre++; return true;
    }
    /// <summary>A single draw with more vertices than 16-bit indices reach: expand it into pieces of whole triangles.</summary>
    void DrawUnindexed(Tex bt, uint bl, byte prog, bool wrap, Tex view, Span<Vertex> v, Span<int> idx)
    {
        Warn("bigdraw", $"[gl] draw with {v.Length} vertices > 65536: expanded to unindexed pieces");
        const int Piece = 65535 / 3 * 3;
        var tmpV = new Vertex[Math.Min(Piece, idx.Length)]; var tmpI = new int[tmpV.Length];
        for (int s = 0; s < idx.Length; s += Piece)
        {
            int n = Math.Min(Piece, idx.Length - s);
            for (int k = 0; k < n; k++) { tmpV[k] = v[idx[s + k]]; tmpI[k] = k; }
            _cur.DrawsIn--; _cur.Tris -= n / 3;   // the caller already counted the whole draw once
            Draw((IntPtr)(bt == _white ? 0 : bt.Id), bl, prog == PSdf, tmpV.AsSpan(0, n), tmpI.AsSpan(0, n));
        }
    }
    FColor[] _va = new FColor[4096]; int _vaValid; bool _vaUsed;
    readonly uint[] _abo = new uint[Ring]; int _addBase = int.MinValue; bool _addOn;   // _addBase: where attribute 3 points (MinValue = the main buffer's colours)
    void EnsureCapacity(int nv, int ni)
    {
        if (_nv + nv > _v.Length) Array.Resize(ref _v, Math.Max(_v.Length * 2, _nv + nv));
        if (_ni + ni > _i.Length) Array.Resize(ref _i, Math.Max(_i.Length * 2, _ni + ni));
    }

    void SetBlend(uint b)
    {
        if (b == _curBlend) return;
        if (b == BLENDMODE_NONE) Gl.Disable(Gl.BLEND);
        else
        {
            if (_curBlend == BLENDMODE_NONE || _curBlend == 0xFFFFFFFF) Gl.Enable(Gl.BLEND);
            switch (b)   // SDL3's definitions (SDL_blendmode.h), colour and alpha factors separately
            {
                case BLENDMODE_BLEND_PREMULTIPLIED: Gl.BlendFuncSeparate(Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA, Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA); break;
                case BLENDMODE_ADD: Gl.BlendFuncSeparate(Gl.SRC_ALPHA, Gl.ONE, Gl.ZERO, Gl.ONE); break;
                case BLENDMODE_ADD_PREMULTIPLIED: Gl.BlendFuncSeparate(Gl.ONE, Gl.ONE, Gl.ZERO, Gl.ONE); break;
                case BLENDMODE_MOD: Gl.BlendFuncSeparate(Gl.ZERO, Gl.SRC_COLOR, Gl.ZERO, Gl.ONE); break;
                case BLENDMODE_MUL: Gl.BlendFuncSeparate(Gl.DST_COLOR, Gl.ONE_MINUS_SRC_ALPHA, Gl.ZERO, Gl.ONE); break;
                default:
                    // polish round 2: SDL-composed custom modes (ShaderPolicy.Compose; operation ADD only) and the BlendModes backdrop draws
                    if (ShaderPolicy.IsComposed(b)) Gl.BlendFuncSeparate(Factor(b >> 4), Factor(b >> 8), Factor(b >> 20), Factor(b >> 24));
                    else if (ShaderPolicy.IsGrab(b) && ShaderPolicy.GrabAdds(b)) Gl.BlendFuncSeparate(Gl.ONE, Gl.ONE, Gl.ZERO, Gl.ONE);
                    else Gl.BlendFuncSeparate(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA, Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA);   // BLEND (and the grab draws' lerp)
                    break;
            }
        }
        _curBlend = b;
    }
    /// <summary>An SDL_BlendFactor (low 4 bits) as the GL factor.</summary>
    static uint Factor(uint f) => (f & 0xF) switch
    {
        1 => Gl.ZERO, 2 => Gl.ONE, 3 => Gl.SRC_COLOR, 4 => Gl.ONE_MINUS_SRC_COLOR, 5 => Gl.SRC_ALPHA, 6 => Gl.ONE_MINUS_SRC_ALPHA,
        7 => Gl.DST_COLOR, 8 => Gl.ONE_MINUS_DST_COLOR, 9 => Gl.DST_ALPHA, 10 => Gl.ONE_MINUS_DST_ALPHA, _ => Gl.ONE,
    };

    // ---------------- polish round 2: BlendModes backdrop ("GrabPass") ----------------
    // A grab draw needs the target's pixels under it. Before the draw: flush everything queued (so the backdrop includes it), then
    // copy the draw's bounding box of the bound target into a scratch target of the same size (one quad through the copy program:
    // two FBO switches, no glCopyTexSubImage2D, nothing outside the GLES2 entry points the Vita host binds), then queue the draw
    // with the blend-mode program reading that copy on unit 1. Each grab draw is its own batch with its own copy (Unity's unnamed
    // GrabPass grabs per object, too). Costs: the box's pixels twice and 2 render-target switches per grab draw.
    Tex _grabTex; uint _grabVbo;
    readonly Vertex[] _grabQuad = new Vertex[4];
    bool GrabBackdrop(Span<Vertex> v, Span<int> idx)
    {
        var src = _target; if (src == null) return false;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var i in idx) { var p = v[i].position; if (p.x < x0) x0 = p.x; if (p.x > x1) x1 = p.x; if (p.y < y0) y0 = p.y; if (p.y > y1) y1 = p.y; }
        int ix0 = Math.Max(0, (int)MathF.Floor(x0)), iy0 = Math.Max(0, (int)MathF.Floor(y0)), ix1 = Math.Min(src.W, (int)MathF.Ceiling(x1)), iy1 = Math.Min(src.H, (int)MathF.Ceiling(y1));
        if (!(ix1 > ix0 && iy1 > iy0)) return false;
        Flush();
        if (_grabTex == null || _grabTex.W != src.W || _grabTex.H != src.H)
        {
            if (_grabTex != null) { uint id = _grabTex.Id; FreeTarget(id, _grabTex.Fbo); _tex.Remove(id); _kEpoch++; if (_curTex == _grabTex) _curTex = null; }   // (kit #13 fix: it may have been drawn this frame)
            _grabTex = Target(src.W, src.H, linear: false);
        }
        if (_grabVbo == 0) { uint b; Gl.GenBuffers(1, &b); _grabVbo = b; }
        float[] qx = { ix0, ix1, ix1, ix0 }, qy = { iy0, iy0, iy1, iy1 };
        for (int k = 0; k < 4; k++) { _grabQuad[k].position.x = qx[k]; _grabQuad[k].position.y = qy[k]; _grabQuad[k].color.r = _grabQuad[k].color.g = _grabQuad[k].color.b = _grabQuad[k].color.a = 1f; _grabQuad[k].tex_coord.x = qx[k] / src.W; _grabQuad[k].tex_coord.y = qy[k] / src.H; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _grabTex.Fbo); Gl.Viewport(0, 0, src.W, src.H);
        SetClip(false, 0, 0, 0, 0); _clipDirty = true; SetBlend(BLENDMODE_NONE);
        if (_curProg != _copy) { Gl.UseProgram(_copy.Id); _curProg = _copy; }
        U4(_copy, _copy.UXform, 2f / src.W, 2f / src.H, -1f, -1f); _copy.Tw = _copy.Th = -2;
        BindTex0(src);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _grabVbo); fixed (Vertex* p = _grabQuad) Gl.BufferData(Gl.ARRAY_BUFFER, 4 * Stride, p, Gl.STREAM_DRAW);
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _quadIdx);
        Attribs(0); _attrBase = int.MinValue;
        Gl.DrawElements(Gl.TRIANGLES, 6, Gl.UNSIGNED_SHORT, null);
        BindTex0(_white);   // never leave the render target bound on unit 0 while drawing into it
        _target = null; BindTarget(src);
        _cur.Grabs++; _cur.DrawsOut++; _grabFrame = _frame;
        _cur.TargetSwitches++; _cur.PassPixels += (long)(ix1 - ix0) * (iy1 - iy0);   // kit #11 gpu2 counters (BindTarget counted the switch back)
        return true;
    }

    void SetClip(bool on, int x, int y, int w, int h)
    {
        if (!on) { if (_scissorOn || _clipDirty) { Gl.Disable(Gl.SCISSOR_TEST); _scissorOn = false; } _clipDirty = false; return; }
        w = Math.Max(0, w); h = Math.Max(0, h);
        if (!_scissorOn || _clipDirty) { Gl.Enable(Gl.SCISSOR_TEST); _scissorOn = true; }
        if (_clipDirty || x != _sx || y != _sy || w != _sw || h != _sh) { Gl.Scissor(x, y, w, h); _sx = x; _sy = y; _sw = w; _sh = h; }   // targets are top-down: y as given
        _clipDirty = false;
    }
    void UseProg(Prog p)
    {
        if (_curProg != p) { Gl.UseProgram(p.Id); _curProg = p; }
        if (p.Tw != _tw || p.Th != _th) { U4(p, p.UXform, 2f / _tw, 2f / _th, -1f, -1f); p.Tw = _tw; p.Th = _th; }
    }
    /// <summary>Every vec4 uniform this backend sets (the program p is the one in use). Kit #11 gpu1, SHEPHERD_K11_UNIFORMS=1: a value bit for bit
    /// equal to the one p's location already holds is not sent again -- GL keeps each program's uniform values across draws and program
    /// switches (the u_xform cache above relies on it since v4, on the device too), so the GPU reads the same values. The location's slot is
    /// found by its value (Prog.ULoc), never assumed to be a small index; -1 (GL's "no such uniform", which GL ignores) and a program's 33rd
    /// location onwards are always sent. Off: the call as before.</summary>
    void U4(Prog p, int loc, float x, float y, float z, float w)
    {
        if (K11World.Uniforms && loc != -1)
        {
            var ls = p.ULoc ??= new int[32]; int n = p.UN, sl = 0;
            while (sl < n && ls[sl] != loc) sl++;
            if (sl == n && n < 32) { ls[n] = loc; p.UN = n + 1; }   // (a location seen for the first time: its slot; its first value is sent)
            if (sl < 32)
            {
                var c = p.UVal ??= new int[128]; int o = sl * 4, bx = BitConverter.SingleToInt32Bits(x), by = BitConverter.SingleToInt32Bits(y), bz = BitConverter.SingleToInt32Bits(z), bw = BitConverter.SingleToInt32Bits(w);
                uint bit = 1u << sl;
                if ((p.USet & bit) != 0 && c[o] == bx && c[o + 1] == by && c[o + 2] == bz && c[o + 3] == bw) { _cur.K11UniSkipped++; return; }
                c[o] = bx; c[o + 1] = by; c[o + 2] = bz; c[o + 3] = bw; p.USet |= bit; _cur.K11UniSent++;
            }
        }
        Gl.Uniform4f(loc, x, y, z, w);
    }
    void Attribs(int baseVertex)
    {
        byte* o = (byte*)(baseVertex * (nint)Stride);
        Gl.EnableVertexAttribArray(0); Gl.EnableVertexAttribArray(1); Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, 0, Stride, o);
        Gl.VertexAttribPointer(1, 4, Gl.FLOAT, 0, Stride, o + 8);
        Gl.VertexAttribPointer(2, 2, Gl.FLOAT, 0, Stride, o + 24);
        // fidelity round 1: once the ADD program has run, attribute 3 stays enabled (no glDisableVertexAttribArray among the 51 entry
        // points the Vita host binds); programs without a_add never read it, but an enabled array must still point at data covering
        // every index a draw uses -- the vertex buffer just bound (its colour field) always does
        if (_addOn) { Gl.VertexAttribPointer(3, 4, Gl.FLOAT, 0, Stride, o + 8); _addBase = int.MinValue; }
        _attrBase = baseVertex;
    }

    void Flush()
    {
        if (_kOn && (_kMatching || _nb > 0 || _npb > 0)) K12Break();   // kit #12 menusa (UIREPLAY): a flush inside the UI pass queues the recording's draws so far first and stops the recording (its offsets end here)
        if (_npb > 0) ProjFlush();   // kit #11 gpu1: GPU-projected batches pending (only one kind is ever pending: draw order)
        if (_nb == 0) { _nv = _ni = _chunkBase = 0; return; }
        _cur.Flushes++; _cur.VertexBytes += (long)_nv * Stride + _ni * 2L;
        uint vb = _vbo[_ring], ib = _ibo[_ring]; _ring = (_ring + 1) % Ring;
        Gl.BindBuffer(Gl.ARRAY_BUFFER, vb);
        fixed (Vertex* pv = _v) Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_nv * Stride, pv, Gl.STREAM_DRAW);
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, ib);
        fixed (ushort* pi = _i) Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, (nint)_ni * 2, pi, Gl.STREAM_DRAW);
        uint ab = 0;
        if (_vaUsed)
        {   // fidelity round 1: the added-colour stream of this flush (16 B per vertex, up to the last ADD draw's vertices)
            if (_abo[0] == 0) fixed (uint* pa = _abo) Gl.GenBuffers(Ring, pa);
            ab = _abo[(_ring + Ring - 1) % Ring];
            Gl.BindBuffer(Gl.ARRAY_BUFFER, ab);
            fixed (FColor* pa = _va) Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_vaValid * 16, pa, Gl.STREAM_DRAW);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, vb);
            _cur.VertexBytes += (long)_vaValid * 16;
        }
        _attrBase = int.MinValue; _addBase = int.MinValue;
        for (int k = 0; k < _nb; k++)
        {
            ref var b = ref _b[k];
            if (b.Tex.FrameSeen != _frame) { b.Tex.FrameSeen = _frame; _cur.Textures++; }
            UseProg(_stClip && b.Prog == PSprite && _clipProg != null ? _clipProg : b.Prog >= PPersp ? PerspProg(b.Prog) : _byId[b.Prog]);   // (kit #12 boot: a Mask graphic's alpha clip; off: the program as before; kit #13 looks: the PERSP programs)
            BindTex0(b.Tex);
            int wrap = b.Wrap ? 1 : 0;
            if (b.Tex.Wrap != wrap) { int m = b.Wrap ? Gl.REPEAT : Gl.CLAMP_TO_EDGE; Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, m); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, m); b.Tex.Wrap = wrap; }
            SetBlend(b.Blend);
            SetClip(b.ClipOn, b.Cx, b.Cy, b.Cw, b.Ch);
            if (b.Prog == PGrab && _grabTex != null)
            {   // the backdrop copied for this draw on unit 1, and its mode
                Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, _grabTex.Id); Gl.ActiveTexture(Gl.TEXTURE0);
                U4(_grab, _grab.UMode, ShaderPolicy.GrabMode(b.Blend), ShaderPolicy.GrabAdds(b.Blend) ? 1f : 0f, 0f, 0f);
            }
            if (b.Base != _attrBase) Attribs(b.Base);
            if (b.Prog == PAdd || b.Prog >= PPersp)
            {   // attribute 3 = this flush's added-colour stream from the batch's base vertex (kit #13 looks: the PERSP programs' w stream too)
                if (!_addOn) { Gl.EnableVertexAttribArray(3); _addOn = true; }
                if (b.Base != _addBase) { Gl.BindBuffer(Gl.ARRAY_BUFFER, ab); Gl.VertexAttribPointer(3, 4, Gl.FLOAT, 0, 16, (void*)((nint)b.Base * 16)); Gl.BindBuffer(Gl.ARRAY_BUFFER, vb); _addBase = b.Base; }
            }
            else if (_addOn && _addBase != int.MinValue) { Gl.VertexAttribPointer(3, 4, Gl.FLOAT, 0, Stride, (void*)((nint)b.Base * Stride + 8)); _addBase = int.MinValue; }
            Gl.DrawElements(Gl.TRIANGLES, b.Ni, Gl.UNSIGNED_SHORT, (void*)((nint)b.I0 * 2));
        }
        _vaValid = 0; _vaUsed = false;
        _cur.DrawsOut += _nb; _cur.Batches += _nb;
        _nb = 0; _nv = _ni = _chunkBase = 0; _gen++;
    }

    // ---------------- kit #6 gpulight: static meshes drawn by a vertex program (IGpuMesh) ----------------
    // A draw here is its own GL draw: the batches queued so far are flushed first (draw order is kept), then the program, its uniforms, the
    // texture (clamped: these meshes keep their uvs in [0,1]), the blend and the current view's clip are set as Flush sets them for a batch,
    // the attributes point into the mesh's static buffer (made on its first draw), and one glDrawElements runs over a shared sequential
    // 16-bit index buffer. Afterwards the next Flush re-points the attributes at its own buffer (_attrBase / _addBase reset). Only the 51
    // entry points the Vita host binds are used (Uniform4f per uniform: no Uniform4fv there).
    // (the state is made on the first GpuProgram call: nothing is allocated, and no generic type is instantiated, in a run that never asks
    //  -- the menus; a Dictionary keyed by float[] built with the backend cost ~0.3 MiB of newlib at boot in the runtime's metadata, VLX)
    sealed class MeshBuf { public float[] Data; public uint Vbo; public long Bytes; }
    List<MeshBuf> _meshes;   // a handful (the intro: one sphere mesh): looked up by reference
    List<Prog> _gpuProgs;    // [0] = null: id 0 = not available
    uint _seqIbo; int _seqLen;
    MeshBuf FindMesh(float[] data) { foreach (var m in _meshes) if (ReferenceEquals(m.Data, data)) return m; return null; }
    public int GpuProgram(string name, string vertexFile, string fragmentFile) => GpuProgramV(name, vertexFile, fragmentFile, null);
    /// <summary>(kit #13 looks: with a shader variant -- the GPU projection's PERSP program)</summary>
    int GpuProgramV(string name, string vertexFile, string fragmentFile, string variant)
    {
        _gpuProgs ??= new List<Prog> { null }; _meshes ??= new List<MeshBuf>();
        for (int k = 1; k < _gpuProgs.Count; k++) if (_gpuProgs[k].Name == name) return _gpuProgs[k].Id == 0 ? 0 : k;
        int id = 0;
        if (!HasSource(vertexFile) || !HasSource(fragmentFile)) Log.Info($"[gl] program '{name}' not built: no source for {vertexFile} / {fragmentFile} ({DerivedRender.Status})");
        else
        {
            try
            {
                var p = Build(name, vertexFile, fragmentFile, variant, FragPrecision, null);
                p.UP = new int[16]; for (int k = 0; k < 16; k++) p.UP[k] = Gl.UniformLoc(p.Id, "u_p" + k);
                _gpuProgs.Add(p); id = _gpuProgs.Count - 1;
                Log.Info($"[gl] program '{name}' ({vertexFile} + {fragmentFile}) compiled+linked on first use in {p.CompileMs:F1} ms (not in the boot set or the gxp cache)");
            }
            catch (Exception e)
            {
                var why = e.Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("[gl]")).FirstOrDefault() ?? e.Message;
                Log.Warn($"[gl] FALLBACK: the shader compiler rejected '{name}' ({(why.Length > 160 ? why[..160] + "..." : why)}) -- its draws stay on the CPU for this run");
            }
        }
        if (id == 0) _gpuProgs.Add(new Prog { Name = name });   // (remembered as refused: Id 0)
        return id;
    }
    public void DrawGpuMesh(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr texH, uint blend)
        => DrawGpuMeshCore(program, data, stride, first, count, uniforms, texH, null, null, 0, blend);
    /// <summary>Kit #11 gpu2: DrawGpuMesh with textures on units 1 and 2 and per-texture addressing (IGpuMesh.DrawGpuMeshTex).</summary>
    public void DrawGpuMeshTex(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr texH, IntPtr tex1, IntPtr tex2, int wrap, uint blend)
    {
        Tex t1 = null, t2 = null;
        if (tex1 != IntPtr.Zero && !_tex.TryGetValue((uint)tex1, out t1)) return;
        if (tex2 != IntPtr.Zero && !_tex.TryGetValue((uint)tex2, out t2)) return;
        DrawGpuMeshCore(program, data, stride, first, count, uniforms, texH, t1 ?? _white, t2 ?? _white, wrap, blend);
    }
    void SetWrap(Tex t, bool repeat)
    {
        int w = repeat ? 1 : 0; if (t.Wrap == w) return;
        int m = repeat ? Gl.REPEAT : Gl.CLAMP_TO_EDGE; Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, m); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, m); t.Wrap = w;
    }
    void DrawGpuMeshCore(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr texH, Tex x1, Tex x2, int wrap, uint blend)
    {
        if (_gpuProgs == null || program <= 0 || program >= _gpuProgs.Count || _gpuProgs[program].Id == 0 || data == null || stride < 4 || stride > 16 || (stride & 3) != 0
            || first < 0 || count < 3 || count > 65535 || (long)(first + count) * stride > data.Length) return;
        Tex t = null;
        if (texH != IntPtr.Zero && !_tex.TryGetValue((uint)texH, out t)) return;   // unknown texture: as Draw
        var p = _gpuProgs[program];
        Flush();
        if (_target == null) BindTarget(_main[_final]);
        var view = _view; var bt = t ?? _white;
        _cur.DrawsIn++; _cur.Tris += count / 3; _cur.Breaks[(int)BreakReason.Shader]++;
        var mb = FindMesh(data);
        if (mb == null)
        {
            uint b; Gl.GenBuffers(1, &b); mb = new MeshBuf { Data = data, Vbo = b, Bytes = (long)data.Length * 4 }; _meshes.Add(mb);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, b); fixed (float* pd = data) Gl.BufferData(Gl.ARRAY_BUFFER, (nint)data.Length * 4, pd, Gl.STATIC_DRAW);
            _cur.Uploads++; _cur.UploadBytes += mb.Bytes; _cur.VertexBytes += mb.Bytes;
        }
        if (_seqLen < count)
        {
            int n = Math.Max(count, Math.Min(65535, Math.Max(4096, _seqLen * 2)));
            var seq = new ushort[n]; for (int i = 0; i < n; i++) seq[i] = (ushort)i;
            if (_seqIbo == 0) { uint b; Gl.GenBuffers(1, &b); _seqIbo = b; }
            Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _seqIbo); fixed (ushort* ps = seq) Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, (nint)n * 2, ps, Gl.STATIC_DRAW);
            _seqLen = n;
        }
        UseProg(p);
        for (int k = 0; k < uniforms.Length && k < p.UP.Length; k++) if (p.UP[k] >= 0) { var u = uniforms[k]; U4(p, p.UP[k], u.r, u.g, u.b, u.a); }
        if (bt.FrameSeen != _frame) { bt.FrameSeen = _frame; _cur.Textures++; }
        if (x1 != null && !p.TexUnits)
        {   // kit #11 gpu2: the extra samplers' units, set on the program's first DrawGpuMeshTex (a kit #6 program never makes these calls)
            p.TexUnits = true; int u1 = Gl.UniformLoc(p.Id, "u_tex1"), u2 = Gl.UniformLoc(p.Id, "u_tex2");
            Gl.UseProgram(p.Id); if (u1 >= 0) Gl.Uniform1i(u1, 1); if (u2 >= 0) Gl.Uniform1i(u2, 2); _curProg = p;
        }
        if (x1 != null)
        {   // kit #11 gpu2: the extra textures (their wrap set while bound, then unit 0 again; never a render target bound while drawing into it)
            Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, x2.Id); SetWrap(x2, (wrap & 4) != 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, x1.Id); SetWrap(x1, (wrap & 2) != 0);
            Gl.ActiveTexture(Gl.TEXTURE0); _curTex = null;
            if (x1.FrameSeen != _frame) { x1.FrameSeen = _frame; _cur.Textures++; }
            if (x2.FrameSeen != _frame) { x2.FrameSeen = _frame; _cur.Textures++; }
            x1.UseGen = x2.UseGen = _gen;
        }
        BindTex0(bt);
        if (x1 != null) SetWrap(bt, (wrap & 1) != 0);
        else if (bt.Wrap != 0) { Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE); bt.Wrap = 0; }
        SetBlend(t == null ? DrawBlendUntextured : blend);
        SetClip(view.ClipOn, view.Cx, view.Cy, view.Cw, view.Ch);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, mb.Vbo);
        int na = stride / 4, sb = stride * 4; byte* o = (byte*)((nint)first * sb);
        for (uint a = 0; a < 4; a++)
        {
            if (a < na) { Gl.EnableVertexAttribArray(a); Gl.VertexAttribPointer(a, 4, Gl.FLOAT, 0, sb, o + a * 16); }
            else if (a == 3 && _addOn) Gl.VertexAttribPointer(3, 4, Gl.FLOAT, 0, sb, o);   // enabled since an ADD draw: it must cover this draw's vertices
        }
        if (na == 4) _addOn = true;
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _seqIbo);
        Gl.DrawElements(Gl.TRIANGLES, count, Gl.UNSIGNED_SHORT, null);
        _attrBase = int.MinValue; _addBase = int.MinValue;   // the next Flush binds its own buffers and re-points every attribute
        bt.UseGen = _gen; _cur.DrawsOut++; _cur.Batches++;
        GpuMeshDraws++;
    }
    public long GpuMeshDraws;
    public void ReleaseGpuMesh(float[] data)
    {
        var mb = data == null || _meshes == null ? null : FindMesh(data); if (mb == null) return;
        _meshes.Remove(mb); uint b = mb.Vbo; Gl.DeleteBuffers(1, &b);
    }

    // ---------------- kit #11 gpu1: the world pass's projection on the GPU (IK11Proj, SHEPHERD_K11_GPUPROJ=1) ----------------
    // A second batcher beside the CPU one, for world-space vertices: its own vertex / index streams and buffer ring, batches that merge on the
    // same texture / blend / wrap / clip / camera pass, flushed through the projection program (Shaders/k11proj.vert + sprite.frag) with the
    // pass's view-projection rows, pixel rect and target height as uniforms. Only one of the two batchers ever has draws pending (a draw of
    // the other kind flushes first), so the draw order is the one the calls came in. Nothing here exists until the first DrawProj.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct PV { public float X, Y, Z; public FColor C; public float U, V; }   // 36 B
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct PV8 { public float X, Y, Z; public uint C; public float U, V; }  // 24 B (SHEPHERD_K11_PACK)
    struct PBatch { public Tex Tex; public uint Blend; public bool Wrap, ClipOn; public int Cx, Cy, Cw, Ch, Base, I0, Ni, Pass; }
    PV[] _pv; PV8[] _pv8; ushort[] _pi; PBatch[] _pb; K11ProjPass[] _pp; int _npv, _npi, _npb, _npp, _pChunk, _projProg = -1;
    uint[] _pvbo, _pibo; int _pring;
    public bool ProjAvailable
    {
        get
        {
            if (_projProg < 0)
            {
                _projProg = K13Looks.Persp ? GpuProgramV("k11proj+PERSP", "k11proj.vert", "sprite.frag", "PERSP") : GpuProgram("k11proj", "k11proj.vert", "sprite.frag");   // (kit #13 looks: the clip w to the GPU)
                if (_projProg > 0)
                {
                    _pv = new PV[4096]; _pi = new ushort[8192]; _pb = new PBatch[64]; _pp = new K11ProjPass[8];
                    _pvbo = new uint[Ring]; _pibo = new uint[Ring]; fixed (uint* a = _pvbo) Gl.GenBuffers(Ring, a); fixed (uint* b = _pibo) Gl.GenBuffers(Ring, b);
                }
            }
            return _projProg > 0;
        }
    }
    static bool SamePass(in K11ProjPass a, in K11ProjPass b) =>
        SameBits(in a.Vp, in b.Vp) && BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.W) == BitConverter.SingleToInt32Bits(b.W) && BitConverter.SingleToInt32Bits(a.H) == BitConverter.SingleToInt32Bits(b.H) && BitConverter.SingleToInt32Bits(a.Gh) == BitConverter.SingleToInt32Bits(b.Gh);
    static bool SameBits(in UnityEngine.Matrix4x4 a, in UnityEngine.Matrix4x4 b) =>
        B(a.m00) == B(b.m00) && B(a.m01) == B(b.m01) && B(a.m02) == B(b.m02) && B(a.m03) == B(b.m03) && B(a.m10) == B(b.m10) && B(a.m11) == B(b.m11) && B(a.m12) == B(b.m12) && B(a.m13) == B(b.m13)
        && B(a.m20) == B(b.m20) && B(a.m21) == B(b.m21) && B(a.m22) == B(b.m22) && B(a.m23) == B(b.m23) && B(a.m30) == B(b.m30) && B(a.m31) == B(b.m31) && B(a.m32) == B(b.m32) && B(a.m33) == B(b.m33);
    static int B(float f) => BitConverter.SingleToInt32Bits(f);
    public bool DrawProj(IntPtr texH, uint blend, Span<Vertex> v, Span<float> z, Span<int> idx, in K11ProjPass pass)
    {
        if (!ProjAvailable || v.Length > 65536 || z.Length < v.Length) return false;
        if (v.Length == 0 || idx.Length == 0 || idx.Length % 3 != 0) return true;   // (Draw's refusals: nothing drawn)
        Tex t = null;
        if (texH != IntPtr.Zero && !_tex.TryGetValue((uint)texH, out t)) return true;
        int nv = v.Length;
        foreach (var i in idx) if ((uint)i >= (uint)nv) return true;
        uint bl = t == null ? DrawBlendUntextured : blend; var bt = t ?? _white;
        if (ShaderPolicy.IsGrab(bl)) return false;
        if (K11World.NoPix && K11World.ClearBlend(bl, out bool rgb0) && K11World.AllClear(v, rgb0)) { _cur.K11NoPixAlpha++; return true; }
        bool wrap = false;
        if (t != null) foreach (ref readonly var vx in v) { float uu = vx.tex_coord.x, vv = vx.tex_coord.y; if (uu < 0f || vv < 0f || uu > 1f || vv > 1f) { wrap = true; break; } }
        if (_nb > 0) Flush();   // CPU batches queued before this draw go first
        if (_target == null) BindTarget(_main[_final]);
        var view = _view;
        _cur.DrawsIn++; _cur.Tris += idx.Length / 3; _cur.K11ProjDraws++;
        if (wrap) { _cur.WrapDraws++; if (bt.Npot) _cur.WrapNpotDraws++; }
        if (_npp == 0 || !SamePass(in _pp[_npp - 1], in pass)) { if (_npp == _pp.Length) ProjFlush(); _pp[_npp++] = pass; }
        if (_npv - _pChunk + nv > 65536) _pChunk = _npv;
        if (_npv + nv > _pv.Length) Array.Resize(ref _pv, Math.Max(_pv.Length * 2, _npv + nv));
        if (_npi + idx.Length > _pi.Length) Array.Resize(ref _pi, Math.Max(_pi.Length * 2, _npi + idx.Length));
        bool merged = false;
        if (_npb > 0)
        {
            ref var last = ref _pb[_npb - 1];
            if (last.Base == _pChunk && last.Tex == bt && last.Blend == bl && last.Wrap == wrap && last.Pass == _npp - 1 && last.ClipOn == view.ClipOn
                && (!view.ClipOn || (last.Cx == view.Cx && last.Cy == view.Cy && last.Cw == view.Cw && last.Ch == view.Ch))) { last.Ni += idx.Length; merged = true; }
        }
        if (!merged)
        {
            if (_npb == _pb.Length) Array.Resize(ref _pb, _pb.Length * 2);
            _pb[_npb++] = new PBatch { Tex = bt, Blend = bl, Wrap = wrap, ClipOn = view.ClipOn, Cx = view.Cx, Cy = view.Cy, Cw = view.Cw, Ch = view.Ch, Base = _pChunk, I0 = _npi, Ni = idx.Length, Pass = _npp - 1 };
        }
        bt.UseGen = _gen;
        for (int i = 0; i < nv; i++) { ref readonly var s = ref v[i]; ref var d = ref _pv[_npv + i]; d.X = s.position.x; d.Y = s.position.y; d.Z = z[i]; d.C = s.color; d.U = s.tex_coord.x; d.V = s.tex_coord.y; }
        int rebase = _npv - _pChunk;
        for (int k = 0; k < idx.Length; k++) _pi[_npi + k] = (ushort)(idx[k] + rebase);
        _npv += nv; _npi += idx.Length;
        return true;
    }
    /// <summary>SHEPHERD_K11_PACK=1: every colour component of the flush is exactly 0 or 1, so the colours go up as normalized bytes (0 -> 0.0, 255
    /// -> 1.0: the two values every GPU converts exactly) -- 24 bytes a vertex instead of 36, the same values in the program.</summary>
    bool ProjPackable()
    {
        if (!K11World.Pack) return false;
        for (int i = 0; i < _npv; i++) { ref var c = ref _pv[i].C; if ((c.r != 0f && c.r != 1f) || (c.g != 0f && c.g != 1f) || (c.b != 0f && c.b != 1f) || (c.a != 0f && c.a != 1f)) return false; }
        return true;
    }
    void ProjFlush()
    {
        if (_npb == 0) { _npv = _npi = _pChunk = _npp = 0; return; }
        _cur.Flushes++;
        uint vb = _pvbo[_pring], ib = _pibo[_pring]; _pring = (_pring + 1) % Ring;
        bool pack = ProjPackable(); int stride = pack ? 24 : 36;
        Gl.BindBuffer(Gl.ARRAY_BUFFER, vb);
        if (pack)
        {
            if (_pv8 == null || _pv8.Length < _npv) _pv8 = new PV8[Math.Max(_npv, _pv.Length)];
            for (int i = 0; i < _npv; i++) { ref var s = ref _pv[i]; ref var d = ref _pv8[i]; d.X = s.X; d.Y = s.Y; d.Z = s.Z; d.U = s.U; d.V = s.V; d.C = (s.C.r != 0f ? 0xFFu : 0u) | (s.C.g != 0f ? 0xFF00u : 0u) | (s.C.b != 0f ? 0xFF0000u : 0u) | (s.C.a != 0f ? 0xFF000000u : 0u); }
            fixed (PV8* p = _pv8) Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_npv * 24, p, Gl.STREAM_DRAW);
        }
        else fixed (PV* p = _pv) Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_npv * 36, p, Gl.STREAM_DRAW);
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, ib);
        fixed (ushort* p = _pi) Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, (nint)_npi * 2, p, Gl.STREAM_DRAW);
        _cur.VertexBytes += (long)_npv * stride + _npi * 2L;
        var pg = _gpuProgs[_projProg]; int abase = int.MinValue;
        for (int k = 0; k < _npb; k++)
        {
            ref var b = ref _pb[k];
            if (b.Tex.FrameSeen != _frame) { b.Tex.FrameSeen = _frame; _cur.Textures++; }
            UseProg(pg);
            ref var ps = ref _pp[b.Pass]; var m = ps.Vp;
            U4(pg, pg.UP[0], m.m00, m.m01, m.m02, m.m03); U4(pg, pg.UP[1], m.m10, m.m11, m.m12, m.m13); U4(pg, pg.UP[2], m.m30, m.m31, m.m32, m.m33);
            U4(pg, pg.UP[3], ps.X, ps.Y, ps.W, ps.H); U4(pg, pg.UP[4], ps.Gh, 0f, 0f, 0f);
            BindTex0(b.Tex);
            int wrap = b.Wrap ? 1 : 0;
            if (b.Tex.Wrap != wrap) { int wm = b.Wrap ? Gl.REPEAT : Gl.CLAMP_TO_EDGE; Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, wm); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, wm); b.Tex.Wrap = wrap; }
            SetBlend(b.Blend);
            SetClip(b.ClipOn, b.Cx, b.Cy, b.Cw, b.Ch);
            if (b.Base != abase)
            {
                byte* o = (byte*)((nint)b.Base * stride);
                Gl.EnableVertexAttribArray(0); Gl.EnableVertexAttribArray(1); Gl.EnableVertexAttribArray(2);
                Gl.VertexAttribPointer(0, 3, Gl.FLOAT, 0, stride, o);
                if (pack) Gl.VertexAttribPointer(1, 4, Gl.UNSIGNED_BYTE, 1, stride, o + 12); else Gl.VertexAttribPointer(1, 4, Gl.FLOAT, 0, stride, o + 12);
                Gl.VertexAttribPointer(2, 2, Gl.FLOAT, 0, stride, o + (pack ? 16 : 28));
                if (_addOn) Gl.VertexAttribPointer(3, 4, Gl.FLOAT, 0, stride, o);   // (enabled since an ADD draw: it must cover these vertices)
                abase = b.Base;
            }
            Gl.DrawElements(Gl.TRIANGLES, b.Ni, Gl.UNSIGNED_SHORT, (void*)((nint)b.I0 * 2));
        }
        _cur.DrawsOut += _npb; _cur.Batches += _npb;
        _attrBase = int.MinValue; _addBase = int.MinValue;   // the next Flush binds its own buffers and re-points every attribute
        _npb = 0; _npv = _npi = _pChunk = _npp = 0; _gen++;
    }

    /// <summary>Stage B insurance, SHEPHERD_GL_PRESENT_FLIP=1: present the frame upside down. GL's default framebuffer is bottom-up and
    /// the present quad flips for it; should a device show the game upside down (vita/gltest's orientation lines would say so),
    /// this env_gl.txt line fixes the display without a rebuild. Off by default; screenshots are not affected.</summary>
    static readonly bool PresentFlip = Environment.GetEnvironmentVariable("SHEPHERD_GL_PRESENT_FLIP") == "1";
    void BuildQuads()
    {
        var px = new Vertex[4]; var ndc = new Vertex[4];
        float[] qx = { 0, W, W, 0 }, qy = { 0, 0, H, H };
        float[] nx = { -1, 1, 1, -1 }, ny = { -1, -1, 1, 1 }, tu = { 0, 1, 1, 0 }, tv = PresentFlip ? new float[] { 0, 0, 1, 1 } : new float[] { 1, 1, 0, 0 };   // bottom-left of the window = last (bottom) row of a top-down target
        for (int i = 0; i < 4; i++)
        {
            px[i] = new Vertex { position = new() { x = qx[i], y = qy[i] }, color = new() { r = 1, g = 1, b = 1, a = 1 }, tex_coord = new() { x = qx[i], y = qy[i] } };
            ndc[i] = new Vertex { position = new() { x = nx[i], y = ny[i] }, color = new() { r = 1, g = 1, b = 1, a = 1 }, tex_coord = new() { x = tu[i], y = tv[i] } };
        }
        ushort[] q = { 0, 1, 2, 0, 2, 3 };
        uint a, b, c; Gl.GenBuffers(1, &a); Gl.GenBuffers(1, &b); Gl.GenBuffers(1, &c); _quadPx = a; _quadNdc = b; _quadIdx = c;
        Gl.BindBuffer(Gl.ARRAY_BUFFER, a); fixed (Vertex* p = px) Gl.BufferData(Gl.ARRAY_BUFFER, 4 * Stride, p, Gl.STATIC_DRAW);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, b); fixed (Vertex* p = ndc) Gl.BufferData(Gl.ARRAY_BUFFER, 4 * Stride, p, Gl.STATIC_DRAW);
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, c); fixed (ushort* p = q) Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, 12, p, Gl.STATIC_DRAW);
    }
    void DrawQuad(uint vbo)
    {
        Gl.BindBuffer(Gl.ARRAY_BUFFER, vbo); Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _quadIdx);
        Attribs(0); _attrBase = int.MinValue;   // the next Flush re-points the attributes at its own buffer
        Gl.DrawElements(Gl.TRIANGLES, 6, Gl.UNSIGNED_SHORT, null);
        _cur.DrawsOut++; _cur.FullScreenPasses++; _cur.PassPixels += (long)W * H;
    }

    /// <summary>World pass done: the camera's post effects in component order -- the colour grading (RunLut) then the GPU Stylizer
    /// (RunStylizer); the owner-local code (DerivedRender) gathers their parameters. Without it: nothing (the world pass as drawn).</summary>
    public void EndWorldPass() { EndWorldPassBase(); DerivedRender.Post?.ApplyGpu(this); DerivedRender.Stylizer?.ApplyGpu(this); }
    public void EndWorldPassBase() { Flush(); _cur.WorldDrawsIn = _cur.DrawsIn; _cur.WorldBatches = _cur.Batches; }

    /// <summary>Whether the grading program exists (built with the backend, never changed afterwards): RunLut's first test. The multicore
    /// pipeline's proxy answers RunLut with it (Render/MtRender.cs).</summary>
    internal bool HasLutProgram => _lut != null;
    /// <summary>Fidelity round 1: one full-screen colour-grading pass, _main[_final] → the other main target (the owner-local lut.frag).</summary>
    public bool RunLut(in LutArgs a)
    {
        if (_lut == null || !_tex.TryGetValue((uint)a.Lut1, out var l1) || !_tex.TryGetValue((uint)a.Lut2, out var l2)) return false;
        Tex mask = a.Mask != IntPtr.Zero && _tex.TryGetValue((uint)a.Mask, out var mt) ? mt : null;
        Flush();
        if (a.Blend && K11Gpu.LutBlend) BlendLuts(in a, ref l1, ref l2);   // kit #11 gpu2: the blended pair (kept while unchanged)
        var src = _main[_final]; var dst = _main[1 - _final];
        BindTarget(dst);
        SetBlend(BLENDMODE_NONE); SetClip(false, 0, 0, 0, 0); _clipDirty = true;
        UseProg(_lut);
        U4(_lut, _lut.UScreen, W, H, a.Exposure, a.Hdr ? 1f : 0f);
        U4(_lut, _lut.ULut1, mask != null ? (float)W / mask.W * a.MaskScaleX : 0f, mask != null ? (float)H / mask.H * a.MaskScaleY : 0f, a.Linear && a.Hdr ? 1f : 0f, a.Linear ? 1f : 0f);
        U4(_lut, _lut.UVig, a.VigSettings.x, a.VigSettings.y, a.VigSettings.z, a.VigSettings.w);
        U4(_lut, _lut.UVigCol, a.VigColor.x, a.VigColor.y, a.VigColor.z, a.Vignette ? 1f : 0f);
        U4(_lut, _lut.UPat, a.VigCenter.x, a.VigCenter.y, 0f, 0f);
        Gl.ActiveTexture(Gl.TEXTURE0 + 3); Gl.BindTexture(Gl.TEXTURE_2D, l2.Id);
        Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, l1.Id);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, (mask ?? _black ?? (_black = Upload(1, 1, new byte[] { 0, 0, 0, 0 }, false))).Id);
        Gl.ActiveTexture(Gl.TEXTURE0); _curTex = null; BindTex0(src);
        DrawQuad(_quadPx);
        BindTex0(_white);
        _final = 1 - _final; _view = _main[0];
        return true;
    }
    Tex _black;

    // ---------------- kit #11 gpu2: the post effect's blended LUT pair (opt-in: SHEPHERD_K11_LUTBLEND=1) ----------------
    // The game's effect (while its blend amount is not 0: the lighting transitions) first blends each LUT with its blend partner into an RGBA8
    // texture of the LUT's size -- texel for texel a + t (b - a), the 8-bit result stored -- and grades with those. It does that every frame;
    // here the two blended textures are kept and rebuilt only when one of the four LUTs or the amount changes (SHEPHERD_K11_LUTCACHE=0: every
    // frame, as the game). The blend program is built with the boot set (BootSet). Anything unexpected (a missing texture, sizes that differ,
    // the program refused, a blend target that cannot be made) grades with the LUTs as given -- kit #10's pass.
    Prog _lerp, _wcBoot; bool _lerpTried; int _lerpT1 = -1, _lerpP0 = -1; uint _lerpVbo; int _lerpVw, _lerpVh;
    // The kept pair and its key. Review fix: the key is the four LUTs' texture RECORDS, not their GL names -- vitaGL hands a freed name out
    // again, so a scene's new LUTs could get the old ones' names (and the intro blends at exactly 1.0 twice); a record is never reused (every
    // upload makes a new one), DestroyTexture drops a key that names a freed one, and a render target among the inputs is never kept (its
    // pixels can change under the same record).
    Tex _bc1, _bc2; bool _bcValid; Tex _bkA1, _bkB1, _bkA2, _bkB2; int _bkT;
    /// <summary>Blended-LUT rebuilds (two small passes each) and frames that reused the kept pair.</summary>
    public long LutBlendBuilds, LutBlendReuses;
    void BlendLuts(in LutArgs a, ref Tex l1, ref Tex l2)
    {
        if (!_tex.TryGetValue((uint)a.Lut1Blend, out var b1) || !_tex.TryGetValue((uint)a.Lut2Blend, out var b2)) { Warn("lutblend-tex", "[k11] LUT blend: a blend LUT is not a texture of this backend -- graded with the LUTs as given"); return; }
        var f2 = l2;   // the highlight blend's start: Lut2, or the identity strip the provider names when the effect has no highlight LUT
        if (a.Lut2BlendFrom != IntPtr.Zero && !_tex.TryGetValue((uint)a.Lut2BlendFrom, out f2)) { Warn("lutblend-tex", "[k11] LUT blend: a blend LUT is not a texture of this backend -- graded with the LUTs as given"); return; }
        int w = l1.W, h = l1.H;
        if (b1.W != w || b1.H != h || f2.W != w || f2.H != h || b2.W != w || b2.H != h) { Warn("lutblend-size", $"[k11] LUT blend: the four LUTs differ in size ({l1.W}x{l1.H}, {b1.W}x{b1.H}, {f2.W}x{f2.H}, {b2.W}x{b2.H}) -- graded with the LUTs as given"); return; }
        int tb = BitConverter.SingleToInt32Bits(a.BlendAmount);
        bool keep = K11Gpu.LutCache && l1.Fbo == 0 && b1.Fbo == 0 && f2.Fbo == 0 && b2.Fbo == 0;
        if (keep && _bcValid && _bkA1 == l1 && _bkB1 == b1 && _bkA2 == f2 && _bkB2 == b2 && _bkT == tb)
        { l1 = _bc1; l2 = _bc2; LutBlendReuses++; return; }
        if (!LerpProgram()) return;
        _bcValid = false;
        if (_bc1 == null || _bc2 == null || _bc1.W != w || _bc1.H != h)
        {
            DropBlendTargets();
            _bc1 = BlendTarget(w, h); if (_bc1 != null) _bc2 = BlendTarget(w, h);   // LINEAR + clamped, like the LUT textures
            if (_bc2 == null) { DropBlendTargets(); return; }   // (BlendTarget said why) -- graded with the LUTs as given
        }
        if (_lerpVbo == 0 || _lerpVw != w || _lerpVh != h)
        {   // the quad over the w x h target, uvs 0..1 top-down (target pixel centres land on the LUTs' texel centres)
            var q = new Vertex[4]; float[] qx = { 0, w, w, 0 }, qy = { 0, 0, h, h };
            for (int k = 0; k < 4; k++) q[k] = new Vertex { position = new() { x = qx[k], y = qy[k] }, color = new() { r = 1, g = 1, b = 1, a = 1 }, tex_coord = new() { x = qx[k] / w, y = qy[k] / h } };
            if (_lerpVbo == 0) { uint vb; Gl.GenBuffers(1, &vb); _lerpVbo = vb; }
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _lerpVbo); fixed (Vertex* p = q) Gl.BufferData(Gl.ARRAY_BUFFER, 4 * Stride, p, Gl.STATIC_DRAW);
            _lerpVw = w; _lerpVh = h;
        }
        LerpPass(_bc1, l1, b1, a.BlendAmount);
        LerpPass(_bc2, f2, b2, a.BlendAmount);
        _bcValid = keep; LutBlendBuilds++;
        if (keep) { _bkA1 = l1; _bkB1 = b1; _bkA2 = f2; _bkB2 = b2; _bkT = tb; } else _bkA1 = _bkB1 = _bkA2 = _bkB2 = null;
        l1 = _bc1; l2 = _bc2;
    }
    /// <summary>A blend target: Target's GL calls, but an incomplete framebuffer (e.g. no video memory left on the memory-tight walk) is
    /// undone and gives null -- the frame is graded with the LUTs as given -- instead of an exception out of the post pass.</summary>
    Tex BlendTarget(int w, int h)
    {
        var t = Upload(w, h, null, true);
        uint fbo; Gl.GenFramebuffers(1, &fbo); t.Fbo = fbo;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, t.Id, 0);
        var st = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _target?.Fbo ?? _displayFbo);
        if (st == Gl.FRAMEBUFFER_COMPLETE) return t;
        Warn("lutblend-fbo", $"[k11] LUT blend: its {w}x{h} target is incomplete (0x{st:X4}) -- graded with the LUTs as given");
        DestroyTexture((IntPtr)t.Id);
        return null;
    }
    void DropBlendTargets()
    {
        _bcValid = false; var x = _bc1; var y = _bc2; _bc1 = _bc2 = null;
        if (x != null) DestroyTexture((IntPtr)x.Id);
        if (y != null) DestroyTexture((IntPtr)y.Id);
    }
    /// <summary>DestroyTexture's hook: a freed texture drops the kept pair's key (and a freed blend target the pair).</summary>
    void BlendForget(Tex t)
    {
        if (t == _bkA1 || t == _bkB1 || t == _bkA2 || t == _bkB2) { _bcValid = false; _bkA1 = _bkB1 = _bkA2 = _bkB2 = null; }
        if (t == _bc1) { _bc1 = null; _bcValid = false; }
        if (t == _bc2) { _bc2 = null; _bcValid = false; }
    }
    void LerpPass(Tex dst, Tex a, Tex b, float t)
    {
        BindTarget(dst);
        SetBlend(BLENDMODE_NONE); SetClip(false, 0, 0, 0, 0); _clipDirty = true;
        UseProg(_lerp);
        U4(_lerp, _lerpP0, t, 0f, 0f, 0f);   // (kit #11 integration: every vec4 uniform through U4 -- gpu1's cache rule)
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, b.Id);
        Gl.ActiveTexture(Gl.TEXTURE0); _curTex = null; BindTex0(a);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _lerpVbo); Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _quadIdx);
        Attribs(0); _attrBase = int.MinValue;
        Gl.DrawElements(Gl.TRIANGLES, 6, Gl.UNSIGNED_SHORT, null);
        _cur.DrawsOut++; _cur.PassPixels += (long)dst.W * dst.H;
        BindTex0(_white);
    }
    /// <summary>The boot set built the blend program: its second sampler's unit and its parameter's location.</summary>
    void LerpReady(Prog p)
    {
        _lerp = p; _lerpTried = true;
        _lerpT1 = Gl.UniformLoc(p.Id, "u_tex1"); _lerpP0 = Gl.UniformLoc(p.Id, "u_p0");
        Gl.UseProgram(p.Id); if (_lerpT1 >= 0) Gl.Uniform1i(_lerpT1, 1); Gl.UseProgram(0); _curProg = null;
    }
    /// <summary>The blend program: built with the boot set while the row is on; built here only if the boot set did not try it.</summary>
    bool LerpProgram()
    {
        if (_lerpTried) return _lerp != null;
        _lerpTried = true;
        try
        {
            LerpReady(Build(LerpName, "post.vert", "lerp.frag", null, FragPrecision, null));
            Log.Info($"[k11] LUT blend program compiled+linked on first use in {_lerp.CompileMs:F1} ms (the boot set did not build it)");
        }
        catch (Exception e)
        {
            _lerp = null;
            var why = e.Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("[gl]")).FirstOrDefault() ?? e.Message;
            Log.Warn($"[k11] FALLBACK: the shader compiler rejected the LUT blend program ({(why.Length > 160 ? why[..160] + "..." : why)}) -- graded with the unblended LUTs (kit #10)");
        }
        return _lerp != null;
    }
    /// <summary>A static-mesh program the boot set built (the opt-in Watercolor-UI one; null = refused): GpuProgram finds it by name, so its
    /// first panel never waits for the compiler (a refused one is remembered: its draws stay as before).</summary>
    void GpuBootProgram(Prog p)
    {
        _gpuProgs ??= new List<Prog> { null }; _meshes ??= new List<MeshBuf>();
        if (p != null) { p.UP = new int[16]; for (int k = 0; k < 16; k++) p.UP[k] = Gl.UniformLoc(p.Id, "u_p" + k); }
        _gpuProgs.Add(p ?? new Prog { Name = WcUiName });
    }

    public struct StylizeArgs
    {
        public IntPtr Lut1, Lut2, Layers; public int Lut1W, Lut1H, Lut2W, Lut2H, LayersW, LayersH, Block; public float Intensity, PaletteLerp;
    }
    /// <summary>One full-screen pass: _main[_final] (world) → the other main target, which then holds the frame.</summary>
    public void RunStylizer(in StylizeArgs a)
    {
        if (_sty1 == null || !_tex.TryGetValue((uint)a.Lut1, out var l1) || !_tex.TryGetValue((uint)a.Layers, out var lm)) return;
        Tex l2 = a.Lut2 != IntPtr.Zero && _tex.TryGetValue((uint)a.Lut2, out var t2) ? t2 : null;
        Flush();
        var src = _main[_final]; var dst = _main[1 - _final];
        BindTarget(dst);
        SetBlend(BLENDMODE_NONE); SetClip(false, 0, 0, 0, 0); _clipDirty = true;
        var p = l2 != null ? _sty2 : _sty1;
        UseProg(p);
        U4(p, p.UScreen, W, H, Math.Max(1, a.Block), a.Intensity);
        U4(p, p.UPat, a.LayersW, a.LayersH, 0, 0);
        U4(p, p.ULut1, a.Lut1W, a.Lut1H, 0, 0);
        if (l2 != null) U4(p, p.ULut2, a.Lut2W, a.Lut2H, 0, a.PaletteLerp);
        Gl.ActiveTexture(Gl.TEXTURE0 + 3); Gl.BindTexture(Gl.TEXTURE_2D, (l2 ?? l1).Id);
        Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, l1.Id);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, lm.Id);
        Gl.ActiveTexture(Gl.TEXTURE0); _curTex = null; BindTex0(src);
        DrawQuad(_quadPx);
        BindTex0(_white);   // never leave a render target's texture bound while drawing into it
        _final = 1 - _final; _view = _main[0];
    }

    public void EndFrame()
    {
        Flush(); _cur.CopyTo(Stats); if (K11World.Any) K11World.AddBackend(_cur); _cur.Reset(); _frame++;
        if (_held != null) FreeHeld(false);   // kit #13 fix
        if (K11Gpu.PassLog) PassLog();
        // polish round 2: the backdrop copy target is freed after 120 frames without a grab draw (the menu has one, on the frame it lands)
        if (_grabTex != null && _frame - _grabFrame > 120) { uint id = _grabTex.Id, f = _grabTex.Fbo; Gl.DeleteTextures(1, &id); Gl.DeleteFramebuffers(1, &f); _tex.Remove(id); _kEpoch++; if (_curTex == _grabTex) _curTex = null; _grabTex = null; }
    }
    int _grabFrame;
    /// <summary>Kit #11 gpu2, SHEPHERD_K11_PASSLOG=1: per 300 frames, the mean render-target switches (+1 for the present's display bind) and
    /// full-screen / copy pixels a frame, the grab copies and the blended-LUT rebuilds / reuses.</summary>
    long _plSw, _plPx, _plGrab, _plN, _plFs;
    void PassLog()
    {
        var st = Stats; _plSw += st.TargetSwitches + 1; _plPx += st.PassPixels; _plGrab += st.Grabs; _plFs += st.FullScreenPasses; _plN++;
        if (_frame % 300 != 0 || _plN == 0) return;
        Log.Info($"[k11] passes f{_frame - _plN + 1}-{_frame}: {(double)_plSw / _plN:F2} render-target switches a frame (incl. the present), {(double)_plFs / _plN:F2} full-screen passes, {(double)_plGrab / _plN:F2} grab copies, {(double)_plPx / _plN / (W * H):F2} screens of pass pixels a frame (incl. the present copy), LUT blend builds {LutBlendBuilds} / reuses {LutBlendReuses}");
        _plSw = _plPx = _plGrab = _plFs = _plN = 0;
    }
    public void Finish() { Flush(); Gl.Finish(); }

    public void Present()
    {
        long t0 = Stopwatch.GetTimestamp();
        PresentCopy();
        if (_shep) ShepGl.Swap(); else SwapSdl();
        Presents++; PresentTicks += Stopwatch.GetTimestamp() - t0;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void SwapSdl() => SDL_GL_SwapWindow(Window);
    /// <summary>The final frame, letterboxed into the window's default framebuffer (bottom-up): one copy pass. No swap.</summary>
    public void PresentCopy()
    {
        Flush();
        DisplaySize(out int pw, out int ph);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _displayFbo); _target = null;
        SetClip(false, 0, 0, 0, 0); _clipDirty = true;
        Gl.Viewport(0, 0, pw, ph); Gl.ClearColor(0, 0, 0, 1); Gl.Clear(Gl.COLOR_BUFFER_BIT);
        float s = Math.Min((float)pw / W, (float)ph / H); int dw = (int)(W * s), dh = (int)(H * s);
        Gl.Viewport((pw - dw) / 2, (ph - dh) / 2, dw, dh);
        SetBlend(BLENDMODE_NONE);
        _tw = _th = -1; if (_curProg != _copy) { Gl.UseProgram(_copy.Id); _curProg = _copy; }
        U4(_copy, _copy.UXform, 1, 1, 0, 0); _copy.Tw = _copy.Th = -2;
        var f = _main[_final]; BindTex0(f);
        bool scaled = dw != W || dh != H;
        if (scaled) { Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR); }
        DrawQuad(_quadNdc);
        if (scaled) { Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST); }
        BindTex0(_white);
    }

    public byte[] ReadPixels(out int w, out int h)
    {
        Flush();
        var prev = _target; var f = _main[_final];
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, f.Fbo);
        var buf = new byte[W * H * 4];
        fixed (byte* p = buf) Gl.ReadPixels(0, 0, W, H, Gl.RGBA, Gl.UNSIGNED_BYTE, p);   // top-down target: rows come out top-down
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, prev?.Fbo ?? _displayFbo);
        var e = Gl.Errors(); if (e.Length > 0) Warn("read" + e, "[gl] ReadPixels:" + e);
        w = W; h = H; return buf;
    }
    public void Screenshot(string path) { var buf = ReadPixels(out var w, out var h); if (buf != null) Png.Write(path, w, h, buf); }
    /// <summary>Diagnostics (rendertest): what the last Present() put in the window's default framebuffer, top-down.</summary>
    public byte[] ReadWindow(out int w, out int h)
    {
        Flush();
        DisplaySize(out int pw, out int ph); w = pw; h = ph;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _displayFbo);
        var buf = new byte[pw * ph * 4];
        fixed (byte* p = buf) Gl.ReadPixels(0, 0, pw, ph, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _target?.Fbo ?? _displayFbo);
        var e = Gl.Errors(); if (e.Length > 0) { Log.Warn("[gl] ReadWindow:" + e); return null; }
        var row = new byte[pw * 4];   // the default framebuffer is bottom-up
        for (int y = 0; y < ph / 2; y++) { Array.Copy(buf, y * pw * 4, row, 0, pw * 4); Array.Copy(buf, (ph - 1 - y) * pw * 4, buf, y * pw * 4, pw * 4); Array.Copy(row, 0, buf, (ph - 1 - y) * pw * 4, pw * 4); }
        return buf;
    }
    /// <summary>Diagnostics (fidelity round 1, SHEPHERD_FX_DUMP): an offscreen target's pixels, top-down RGBA8.</summary>
    public byte[] ReadTarget(IntPtr target, out int w, out int h)
    {
        w = h = 0; if (!_tex.TryGetValue((uint)target, out var t) || t.Fbo == 0) return null;
        Flush(); var prev = _target;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, t.Fbo);
        var buf = new byte[t.W * t.H * 4];
        fixed (byte* p = buf) Gl.ReadPixels(0, 0, t.W, t.H, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, prev?.Fbo ?? _displayFbo);
        w = t.W; h = t.H; return buf;
    }
    /// <summary>Diagnostics: the world pass as the Stylizer saw it (only while the frame's Stylizer pass ran, else null).</summary>
    public byte[] ReadWorldPass()
    {
        if (_final != 1) return null;
        Flush(); var prev = _target;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _main[0].Fbo);
        var buf = new byte[W * H * 4];
        fixed (byte* p = buf) Gl.ReadPixels(0, 0, W, H, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, prev?.Fbo ?? _displayFbo);
        return buf;
    }

    void Warn(string key, string msg) { if (_warned.Add(key)) Log.Warn(msg); }

    public void Dispose()
    {
        try
        {
            FreeHeld(true);   // kit #13 fix
            foreach (var t in _tex.Values.ToArray()) { uint id = t.Id; Gl.DeleteTextures(1, &id); if (t.Fbo != 0) { uint f = t.Fbo; Gl.DeleteFramebuffers(1, &f); } }
            fixed (uint* pv = _vbo) Gl.DeleteBuffers(Ring, pv);
            fixed (uint* pi = _ibo) Gl.DeleteBuffers(Ring, pi);
            if (_meshes != null) foreach (var mb in _meshes) { uint b = mb.Vbo; Gl.DeleteBuffers(1, &b); }   // kit #6 gpulight
            if (_seqIbo != 0) { uint b = _seqIbo; Gl.DeleteBuffers(1, &b); }
            if (!_shep) DisposeSdl();   // shepgl: the context belongs to the native host
        }
        catch { }
        if (!_shep) QuitSdl();
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void DisposeSdl()
    {
        if (Context != IntPtr.Zero) SDL_GL_DestroyContext(Context);
        if (Window != IntPtr.Zero) SDL_DestroyWindow(Window);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void QuitSdl() => SDL_Quit();
}
