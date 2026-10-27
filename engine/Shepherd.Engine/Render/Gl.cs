using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Render;

/// <summary>The OpenGL ES 2.0 subset GlBackend uses — nothing outside GLES2 core except the two desktop-only entries marked
/// (VAO for a 3.2 core context) and S3TC (EXT_texture_compression_s3tc, which vitaGL exposes). Entry points are resolved
/// through a host-supplied GetProcAddress (Mac: SDL_GL_GetProcAddress; Vita: vglGetProcAddress or a static table) and called
/// through unmanaged function pointers: no delegates, no marshalling, no per-call allocation.</summary>
[ProfileOnly]
public static unsafe class Gl
{
    public const uint DEPTH_BUFFER_BIT = 0x100, COLOR_BUFFER_BIT = 0x4000, TRIANGLES = 0x0004;
    public const uint BLEND = 0x0BE2, SCISSOR_TEST = 0x0C11, CULL_FACE = 0x0B44, DEPTH_TEST = 0x0B71, DITHER = 0x0BD0, STENCIL_TEST = 0x0B90;
    public const uint ZERO = 0, ONE = 1, SRC_COLOR = 0x0300, ONE_MINUS_SRC_COLOR = 0x0301, SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303, DST_ALPHA = 0x0304, ONE_MINUS_DST_ALPHA = 0x0305, DST_COLOR = 0x0306, ONE_MINUS_DST_COLOR = 0x0307;
    public const uint TEXTURE_2D = 0x0DE1, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
    public const int NEAREST = 0x2600, LINEAR = 0x2601, REPEAT = 0x2901, CLAMP_TO_EDGE = 0x812F;
    public const uint ALPHA = 0x1906, RGBA = 0x1908, UNSIGNED_BYTE = 0x1401, UNSIGNED_SHORT = 0x1403, FLOAT = 0x1406, TEXTURE0 = 0x84C0;
    public const uint FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5, FRAMEBUFFER_BINDING = 0x8CA6;
    public const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STREAM_DRAW = 0x88E0, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, INFO_LOG_LENGTH = 0x8B84;
    public const uint VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02, SHADING_LANGUAGE_VERSION = 0x8B8C, EXTENSIONS = 0x1F03;
    public const uint MAX_TEXTURE_SIZE = 0x0D33, MAX_TEXTURE_IMAGE_UNITS = 0x8872, MAX_VARYING_VECTORS = 0x8DFC, MAX_FRAGMENT_UNIFORM_VECTORS = 0x8DFD, MAX_VARYING_FLOATS = 0x8B4B, MAX_FRAGMENT_UNIFORM_COMPONENTS = 0x8B49;
    public const uint UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05, NO_ERROR = 0;
    public const uint COMPRESSED_RGB_S3TC_DXT1 = 0x83F0, COMPRESSED_RGBA_S3TC_DXT1 = 0x83F1, COMPRESSED_RGBA_S3TC_DXT3 = 0x83F2, COMPRESSED_RGBA_S3TC_DXT5 = 0x83F3;

    /// <summary>Device kit #5 (track runtime, R2; vita/notes/kit5-runtime.md): the 17 leaf state setters below (viewport / scissor /
    /// clear colour, enable / disable, blend function, texture / buffer / framebuffer / program binds, texture and pixel-store
    /// parameters, vertex attributes, the two uniforms) are called through a <c>SuppressGCTransition</c> pointer: no coop GC-safe
    /// transition around each call (mono_threads_enter/exit_gc_safe_region: ~1,200 ARM32 instructions per call, VLX count; 150 of the
    /// idle menu's 220 GL calls per frame). In vitaGL each of them only writes the context's state (no wait, lock, allocation or GPU
    /// call: the library's call graph, checked when this was added), so the calling thread stays in managed (running) state for a few
    /// hundred instructions and a stop-the-world GC still meets it at its next safepoint. Draws, clears, buffer / texture uploads,
    /// swaps and everything else keep the transition. The GL calls and their arguments are the same either way (vlx glrec identical).
    /// SHEPHERD_GL_NOTRANS=1 / 0; unset = on with the Vita host's vitaGL context (SHEPHERD_GL_HOST=shepgl), off on the Mac (SDL / CGL,
    /// whose drivers may lock). 0 = every call with the transition, as kit #4.</summary>
    public static bool NoTrans;

    public static delegate* unmanaged<uint, byte*> GetString;
    public static delegate* unmanaged<uint, int*, void> GetIntegerv;
    public static delegate* unmanaged<uint> GetError;
    static delegate* unmanaged<int, int, int, int, void> _viewport;
    static delegate* unmanaged[SuppressGCTransition]<int, int, int, int, void> _viewportS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Viewport(int x, int y, int w, int h) { if (NoTrans) _viewportS(x, y, w, h); else _viewport(x, y, w, h); }
    static delegate* unmanaged<int, int, int, int, void> _scissor;
    static delegate* unmanaged[SuppressGCTransition]<int, int, int, int, void> _scissorS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Scissor(int x, int y, int w, int h) { if (NoTrans) _scissorS(x, y, w, h); else _scissor(x, y, w, h); }
    static delegate* unmanaged<float, float, float, float, void> _clearColor;
    static delegate* unmanaged[SuppressGCTransition]<float, float, float, float, void> _clearColorS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ClearColor(float r, float g, float b, float a) { if (NoTrans) _clearColorS(r, g, b, a); else _clearColor(r, g, b, a); }
    public static delegate* unmanaged<uint, void> Clear;
    static delegate* unmanaged<uint, void> _enable;
    static delegate* unmanaged[SuppressGCTransition]<uint, void> _enableS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Enable(uint cap) { if (NoTrans) _enableS(cap); else _enable(cap); }
    static delegate* unmanaged<uint, void> _disable;
    static delegate* unmanaged[SuppressGCTransition]<uint, void> _disableS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Disable(uint cap) { if (NoTrans) _disableS(cap); else _disable(cap); }
    static delegate* unmanaged<uint, uint, uint, uint, void> _blendFuncSeparate;
    static delegate* unmanaged[SuppressGCTransition]<uint, uint, uint, uint, void> _blendFuncSeparateS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void BlendFuncSeparate(uint srcRgb, uint dstRgb, uint srcA, uint dstA) { if (NoTrans) _blendFuncSeparateS(srcRgb, dstRgb, srcA, dstA); else _blendFuncSeparate(srcRgb, dstRgb, srcA, dstA); }
    public static delegate* unmanaged<int, uint*, void> GenTextures;
    public static delegate* unmanaged<int, uint*, void> DeleteTextures;
    static delegate* unmanaged<uint, uint, void> _bindTexture;
    static delegate* unmanaged[SuppressGCTransition]<uint, uint, void> _bindTextureS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void BindTexture(uint target, uint texture) { if (NoTrans) _bindTextureS(target, texture); else _bindTexture(target, texture); }
    static delegate* unmanaged<uint, void> _activeTexture;
    static delegate* unmanaged[SuppressGCTransition]<uint, void> _activeTextureS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ActiveTexture(uint unit) { if (NoTrans) _activeTextureS(unit); else _activeTexture(unit); }
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void> CompressedTexImage2D;
    static delegate* unmanaged<uint, uint, int, void> _texParameteri;
    static delegate* unmanaged[SuppressGCTransition]<uint, uint, int, void> _texParameteriS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void TexParameteri(uint target, uint pname, int value) { if (NoTrans) _texParameteriS(target, pname, value); else _texParameteri(target, pname, value); }
    static delegate* unmanaged<uint, int, void> _pixelStorei;
    static delegate* unmanaged[SuppressGCTransition]<uint, int, void> _pixelStoreiS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void PixelStorei(uint pname, int value) { if (NoTrans) _pixelStoreiS(pname, value); else _pixelStorei(pname, value); }
    public static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers;
    public static delegate* unmanaged<int, uint*, void> DeleteFramebuffers;
    static delegate* unmanaged<uint, uint, void> _bindFramebuffer;
    static delegate* unmanaged[SuppressGCTransition]<uint, uint, void> _bindFramebufferS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void BindFramebuffer(uint target, uint fbo) { if (NoTrans) _bindFramebufferS(target, fbo); else _bindFramebuffer(target, fbo); }
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    public static delegate* unmanaged<int, uint*, void> GenBuffers;
    public static delegate* unmanaged<int, uint*, void> DeleteBuffers;
    static delegate* unmanaged<uint, uint, void> _bindBuffer;
    static delegate* unmanaged[SuppressGCTransition]<uint, uint, void> _bindBufferS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void BindBuffer(uint target, uint buffer) { if (NoTrans) _bindBufferS(target, buffer); else _bindBuffer(target, buffer); }
    public static delegate* unmanaged<uint, nint, void*, uint, void> BufferData;
    static delegate* unmanaged<uint, int, uint, byte, int, void*, void> _vertexAttribPointer;
    static delegate* unmanaged[SuppressGCTransition]<uint, int, uint, byte, int, void*, void> _vertexAttribPointerS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void VertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, void* ptr) { if (NoTrans) _vertexAttribPointerS(index, size, type, normalized, stride, ptr); else _vertexAttribPointer(index, size, type, normalized, stride, ptr); }
    static delegate* unmanaged<uint, void> _enableVertexAttribArray;
    static delegate* unmanaged[SuppressGCTransition]<uint, void> _enableVertexAttribArrayS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void EnableVertexAttribArray(uint index) { if (NoTrans) _enableVertexAttribArrayS(index); else _enableVertexAttribArray(index); }
    public static delegate* unmanaged<uint, int, uint, void*, void> DrawElements;
    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, void> DeleteShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, uint, int*, void> GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    static delegate* unmanaged<uint, void> _useProgram;
    static delegate* unmanaged[SuppressGCTransition]<uint, void> _useProgramS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void UseProgram(uint program) { if (NoTrans) _useProgramS(program); else _useProgram(program); }
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    static delegate* unmanaged<int, int, void> _uniform1i;
    static delegate* unmanaged[SuppressGCTransition]<int, int, void> _uniform1iS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Uniform1i(int location, int v) { if (NoTrans) _uniform1iS(location, v); else _uniform1i(location, v); }
    static delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    static delegate* unmanaged[SuppressGCTransition]<int, float, float, float, float, void> _uniform4fS;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Uniform4f(int location, float x, float y, float z, float w) { if (NoTrans) _uniform4fS(location, x, y, z, w); else _uniform4f(location, x, y, z, w); }
    public static delegate* unmanaged<void> Finish;
    public static delegate* unmanaged<void> Flush;
    // desktop core profile only (not GLES2): a VAO must be bound
    public static delegate* unmanaged<int, uint*, void> GenVertexArrays;
    public static delegate* unmanaged<uint, void> BindVertexArray;

    /// <summary>Resolve every entry point. <paramref name="getProc"/> returns 0 for unknown names; FBO entries fall back to the
    /// EXT names (macOS legacy 2.1 contexts expose both). Returns the names that could not be resolved (empty = all good).</summary>
    public static List<string> Load(Func<string, IntPtr> getProc, bool core)
    {
        NoTrans = Environment.GetEnvironmentVariable("SHEPHERD_GL_NOTRANS") is { Length: > 0 } nt ? nt.Trim() == "1" : ShepGl.On;
        var missing = new List<string>();
        IntPtr P(string name, bool required = true)
        {
            var p = getProc(name);
            if (p == IntPtr.Zero && name.Contains("Framebuffer")) p = getProc(name + "EXT");
            if (p == IntPtr.Zero && required) missing.Add(name);
            return p;
        }
        GetString = (delegate* unmanaged<uint, byte*>)P("glGetString");
        GetIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
        GetError = (delegate* unmanaged<uint>)P("glGetError");
        _viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport"); _viewportS = (delegate* unmanaged[SuppressGCTransition]<int, int, int, int, void>)_viewport;
        _scissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor"); _scissorS = (delegate* unmanaged[SuppressGCTransition]<int, int, int, int, void>)_scissor;
        _clearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor"); _clearColorS = (delegate* unmanaged[SuppressGCTransition]<float, float, float, float, void>)_clearColor;
        Clear = (delegate* unmanaged<uint, void>)P("glClear");
        _enable = (delegate* unmanaged<uint, void>)P("glEnable"); _enableS = (delegate* unmanaged[SuppressGCTransition]<uint, void>)_enable;
        _disable = (delegate* unmanaged<uint, void>)P("glDisable"); _disableS = (delegate* unmanaged[SuppressGCTransition]<uint, void>)_disable;
        _blendFuncSeparate = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glBlendFuncSeparate"); _blendFuncSeparateS = (delegate* unmanaged[SuppressGCTransition]<uint, uint, uint, uint, void>)_blendFuncSeparate;
        GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        _bindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture"); _bindTextureS = (delegate* unmanaged[SuppressGCTransition]<uint, uint, void>)_bindTexture;
        _activeTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture"); _activeTextureS = (delegate* unmanaged[SuppressGCTransition]<uint, void>)_activeTexture;
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        CompressedTexImage2D = (delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void>)P("glCompressedTexImage2D");
        _texParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri"); _texParameteriS = (delegate* unmanaged[SuppressGCTransition]<uint, uint, int, void>)_texParameteri;
        _pixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei"); _pixelStoreiS = (delegate* unmanaged[SuppressGCTransition]<uint, int, void>)_pixelStorei;
        ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        _bindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer"); _bindFramebufferS = (delegate* unmanaged[SuppressGCTransition]<uint, uint, void>)_bindFramebuffer;
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        DeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        _bindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer"); _bindBufferS = (delegate* unmanaged[SuppressGCTransition]<uint, uint, void>)_bindBuffer;
        BufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)P("glBufferData");
        _vertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer"); _vertexAttribPointerS = (delegate* unmanaged[SuppressGCTransition]<uint, int, uint, byte, int, void*, void>)_vertexAttribPointer;
        _enableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray"); _enableVertexAttribArrayS = (delegate* unmanaged[SuppressGCTransition]<uint, void>)_enableVertexAttribArray;
        DrawElements = (delegate* unmanaged<uint, int, uint, void*, void>)P("glDrawElements");
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        _useProgram = (delegate* unmanaged<uint, void>)P("glUseProgram"); _useProgramS = (delegate* unmanaged[SuppressGCTransition]<uint, void>)_useProgram;
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        _uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i"); _uniform1iS = (delegate* unmanaged[SuppressGCTransition]<int, int, void>)_uniform1i;
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f"); _uniform4fS = (delegate* unmanaged[SuppressGCTransition]<int, float, float, float, float, void>)_uniform4f;
        Finish = (delegate* unmanaged<void>)P("glFinish");
        Flush = (delegate* unmanaged<void>)P("glFlush");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays", required: core);
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray", required: core);
        return missing;
    }

    public static string Str(uint name) { var p = GetString(name); return p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? ""; }
    public static int Int(uint name) { int v = 0; GetIntegerv(name, &v); return v; }
    public static int UniformLoc(uint prog, string name)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = b) return GetUniformLocation(prog, p);
    }
    /// <summary>Drain glGetError; returns "" when clean, else the codes (0x0500 INVALID_ENUM, 0x0501 VALUE, 0x0502 OPERATION, 0x0505 OOM, 0x0506 INVALID_FRAMEBUFFER_OPERATION).</summary>
    public static string Errors()
    {
        string s = ""; for (int i = 0; i < 8; i++) { uint e = GetError(); if (e == NO_ERROR) break; s += $" 0x{e:X4}"; }
        return s;
    }
}
