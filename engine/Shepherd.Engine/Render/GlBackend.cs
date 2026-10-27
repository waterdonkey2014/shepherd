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
public sealed unsafe class GlBackend : IRenderBackend, ICompressedUpload, ICompressedUploadPtr, IGpuStylizer, IAlphaUpload, IAlphaUploadPtr, IGpuPost, IAddDraw, IWorldPassSplit, IGpuMesh
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

    sealed class Prog { public string Name; public uint Id; public int UXform, UScreen, UPat, ULut1, ULut2, UMode, UVig, UVigCol; public int Tw = -1, Th = -1; public double CompileMs; public int[] UP; }
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
        foreach (var (name, vs, fs, def) in Programs)
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
                    cache?.Failed(name);   // (a set with a refused program is never written to the cache)
                    var why = e.Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("[gl]")).FirstOrDefault() ?? e.Message;
                    if (why.Length > 160) why = why[..160] + "...";
                    if (name == "grab") Log.Warn($"[gl] grab program unavailable, BlendModes backdrop draws will be skipped: {e.Message}");
                    else (name is "lut" or "sprite+ADD" ? litFail : styFail).Add($"'{name}' ({why})");
                    continue;
                }
            }
            switch (name) { case "sprite": _sprite = p; break; case "sprite+SDF": _sdf = p; break; case "copy": _copy = p; break; case "stylizer": _sty1 = p; break; case "grab": _grab = p; break; case "lut": _lut = p; break; case "sprite+ADD": _add = p; break; default: _sty2 = p; break; }
        }
        if (litFail.Count > 0)
        {
            _lut = null; _add = null;
            var why = $"the shader compiler rejected {string.Join(", ", litFail)}";
            Log.Warn($"[gl] FALLBACK: {why} -- lighting, light mask and colour grading are off for this run (the unlit path)");
            DerivedRender.LitPathOff(why);
        }
        if (styFail.Count > 0) { _sty1 = null; _sty2 = null; Log.Warn($"[gl] FALLBACK: the shader compiler rejected {string.Join(", ", styFail)} -- the Stylizer pass is off for this run"); }
        _byId[PSprite] = _sprite; _byId[PSdf] = _sdf; _byId[PGrab] = _grab; _byId[PAdd] = _add;
        var built = new[] { _sprite, _sdf, _copy, _sty1, _sty2, _grab, _lut, _add }.Where(p => p != null).ToArray();
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
    /// <summary>Default fragment precision for every program: highp. The Stylizer needs it (floor(c*16) near 1.0); the sprite
    /// shaders get it too so uvs into 4096-texel atlases keep sub-texel precision should a driver honour mediump as fp16.
    /// SHEPHERD_GL_FRAG_PRECISION=mediump to experiment (essl100 dialect only).</summary>
    /// <summary>SHEPHERD_GL_FAIL=&lt;program&gt;[,&lt;program&gt;...]: those programs' fragment shaders get an #error line, so the driver's compiler
    /// rejects them for real -- the test of the run-time fallback a Vita compiler refusal takes (fid1-fixup Defaults).</summary>
    static readonly HashSet<string> FailTest = new((Environment.GetEnvironmentVariable("SHEPHERD_GL_FAIL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
                bool def = t.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1] == variant;
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
        foreach (var (name, vs, fs, variant) in Programs)
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
        foreach (var (name, vsf, fsf, variant) in Programs)
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
        _tex[id] = t; t.Bytes = (long)w * h * bpp; t.Alpha8 = bpp == 1;
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
        _tex[id] = t; t.Bytes = size; t.Compressed = true; _cur.Uploads++; _cur.UploadBytes += size; CompressedUploads++; CompressedBytes += size; TotalUploads++; TotalUploadBytes += size;
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
        if (t.UseGen == _gen && _nb > 0) Flush();      // a pending batch still samples it (SDL flushes in this case too)
        if (_curTex == t) _curTex = null;
        uint id = t.Id; Gl.DeleteTextures(1, &id);
        if (t.Fbo != 0) { uint f = t.Fbo; Gl.DeleteFramebuffers(1, &f); }
        _tex.Remove(id);
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
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, t.Fbo);
        Gl.Viewport(0, 0, t.W, t.H); _tw = t.W; _th = t.H; _target = t; _clipDirty = true;
    }
    void BindTex0(Tex t) { if (_curTex == t) return; Gl.BindTexture(Gl.TEXTURE_2D, t.Id); _curTex = t; }

    // ---------------- frame ----------------
    public void Begin(float r, float g, float b, float a)
    {
        Flush();
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
    public void DrawAdd(IntPtr texH, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add) { if (_add == null) DrawCore(texH, blend, false, v, idx, default); else DrawCore(texH, blend, false, v, idx, add); }
    void DrawCore(IntPtr texH, uint blend, bool sdf, Span<Vertex> v, Span<int> idx, Span<FColor> add)
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
        bool hasAdd = !add.IsEmpty && !grab; if (hasAdd) prog = PAdd;
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
            if (_grabTex != null) { uint id = _grabTex.Id, f = _grabTex.Fbo; Gl.DeleteTextures(1, &id); Gl.DeleteFramebuffers(1, &f); _tex.Remove(id); if (_curTex == _grabTex) _curTex = null; }
            _grabTex = Target(src.W, src.H, linear: false);
        }
        if (_grabVbo == 0) { uint b; Gl.GenBuffers(1, &b); _grabVbo = b; }
        float[] qx = { ix0, ix1, ix1, ix0 }, qy = { iy0, iy0, iy1, iy1 };
        for (int k = 0; k < 4; k++) { _grabQuad[k].position.x = qx[k]; _grabQuad[k].position.y = qy[k]; _grabQuad[k].color.r = _grabQuad[k].color.g = _grabQuad[k].color.b = _grabQuad[k].color.a = 1f; _grabQuad[k].tex_coord.x = qx[k] / src.W; _grabQuad[k].tex_coord.y = qy[k] / src.H; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _grabTex.Fbo); Gl.Viewport(0, 0, src.W, src.H);
        SetClip(false, 0, 0, 0, 0); _clipDirty = true; SetBlend(BLENDMODE_NONE);
        if (_curProg != _copy) { Gl.UseProgram(_copy.Id); _curProg = _copy; }
        Gl.Uniform4f(_copy.UXform, 2f / src.W, 2f / src.H, -1f, -1f); _copy.Tw = _copy.Th = -2;
        BindTex0(src);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _grabVbo); fixed (Vertex* p = _grabQuad) Gl.BufferData(Gl.ARRAY_BUFFER, 4 * Stride, p, Gl.STREAM_DRAW);
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _quadIdx);
        Attribs(0); _attrBase = int.MinValue;
        Gl.DrawElements(Gl.TRIANGLES, 6, Gl.UNSIGNED_SHORT, null);
        BindTex0(_white);   // never leave the render target bound on unit 0 while drawing into it
        _target = null; BindTarget(src);
        _cur.Grabs++; _cur.DrawsOut++; _grabFrame = _frame;
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
        if (p.Tw != _tw || p.Th != _th) { Gl.Uniform4f(p.UXform, 2f / _tw, 2f / _th, -1f, -1f); p.Tw = _tw; p.Th = _th; }
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
            UseProg(_byId[b.Prog]);
            BindTex0(b.Tex);
            int wrap = b.Wrap ? 1 : 0;
            if (b.Tex.Wrap != wrap) { int m = b.Wrap ? Gl.REPEAT : Gl.CLAMP_TO_EDGE; Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, m); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, m); b.Tex.Wrap = wrap; }
            SetBlend(b.Blend);
            SetClip(b.ClipOn, b.Cx, b.Cy, b.Cw, b.Ch);
            if (b.Prog == PGrab && _grabTex != null)
            {   // the backdrop copied for this draw on unit 1, and its mode
                Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, _grabTex.Id); Gl.ActiveTexture(Gl.TEXTURE0);
                Gl.Uniform4f(_grab.UMode, ShaderPolicy.GrabMode(b.Blend), ShaderPolicy.GrabAdds(b.Blend) ? 1f : 0f, 0f, 0f);
            }
            if (b.Base != _attrBase) Attribs(b.Base);
            if (b.Prog == PAdd)
            {   // attribute 3 = this flush's added-colour stream from the batch's base vertex
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
    public int GpuProgram(string name, string vertexFile, string fragmentFile)
    {
        _gpuProgs ??= new List<Prog> { null }; _meshes ??= new List<MeshBuf>();
        for (int k = 1; k < _gpuProgs.Count; k++) if (_gpuProgs[k].Name == name) return _gpuProgs[k].Id == 0 ? 0 : k;
        int id = 0;
        if (!HasSource(vertexFile) || !HasSource(fragmentFile)) Log.Info($"[gl] program '{name}' not built: no source for {vertexFile} / {fragmentFile} ({DerivedRender.Status})");
        else
        {
            try
            {
                var p = Build(name, vertexFile, fragmentFile, null, FragPrecision, null);
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
        for (int k = 0; k < uniforms.Length && k < p.UP.Length; k++) if (p.UP[k] >= 0) { var u = uniforms[k]; Gl.Uniform4f(p.UP[k], u.r, u.g, u.b, u.a); }
        if (bt.FrameSeen != _frame) { bt.FrameSeen = _frame; _cur.Textures++; }
        BindTex0(bt);
        if (bt.Wrap != 0) { Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE); bt.Wrap = 0; }
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
        _cur.DrawsOut++; _cur.FullScreenPasses++;
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
        var src = _main[_final]; var dst = _main[1 - _final];
        BindTarget(dst);
        SetBlend(BLENDMODE_NONE); SetClip(false, 0, 0, 0, 0); _clipDirty = true;
        UseProg(_lut);
        Gl.Uniform4f(_lut.UScreen, W, H, a.Exposure, a.Hdr ? 1f : 0f);
        Gl.Uniform4f(_lut.ULut1, mask != null ? (float)W / mask.W * a.MaskScaleX : 0f, mask != null ? (float)H / mask.H * a.MaskScaleY : 0f, a.Linear && a.Hdr ? 1f : 0f, a.Linear ? 1f : 0f);
        Gl.Uniform4f(_lut.UVig, a.VigSettings.x, a.VigSettings.y, a.VigSettings.z, a.VigSettings.w);
        Gl.Uniform4f(_lut.UVigCol, a.VigColor.x, a.VigColor.y, a.VigColor.z, a.Vignette ? 1f : 0f);
        Gl.Uniform4f(_lut.UPat, a.VigCenter.x, a.VigCenter.y, 0f, 0f);
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
        Gl.Uniform4f(p.UScreen, W, H, Math.Max(1, a.Block), a.Intensity);
        Gl.Uniform4f(p.UPat, a.LayersW, a.LayersH, 0, 0);
        Gl.Uniform4f(p.ULut1, a.Lut1W, a.Lut1H, 0, 0);
        if (l2 != null) Gl.Uniform4f(p.ULut2, a.Lut2W, a.Lut2H, 0, a.PaletteLerp);
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
        Flush(); _cur.CopyTo(Stats); _cur.Reset(); _frame++;
        // polish round 2: the backdrop copy target is freed after 120 frames without a grab draw (the menu has one, on the frame it lands)
        if (_grabTex != null && _frame - _grabFrame > 120) { uint id = _grabTex.Id, f = _grabTex.Fbo; Gl.DeleteTextures(1, &id); Gl.DeleteFramebuffers(1, &f); _tex.Remove(id); if (_curTex == _grabTex) _curTex = null; _grabTex = null; }
    }
    int _grabFrame;
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
        Gl.Uniform4f(_copy.UXform, 1, 1, 0, 0); _copy.Tw = _copy.Th = -2;
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
