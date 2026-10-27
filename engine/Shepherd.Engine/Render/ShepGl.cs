using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Render;

/// <summary>Stage B (vita/notes/m1-gl-app.md): GlBackend's host layer on the PS Vita. SHEPHERD_GL_HOST=shepgl (set by the Vita GL host,
/// vita/shepherd/host.c built with SHEPHERD_GL) replaces the SDL3 window/context: the native host has already called vitaGL's
/// vglInitExtended (vita/gltest/shepgl, shepgl_init) after sizing the Mono arena, so the engine only binds:
/// - the 51 entry points of Gl.cs through <c>shepgl_proc</c>, an export of the eboot's static P/Invoke table (__Internal); the
///   functions are resolved at link time and called through Gl's unmanaged function pointers as on the Mac
/// - <c>shepgl_attach</c> (the Mac rehearsal's CGL context is current per thread; a no-op on vitaGL), <c>shepgl_swap</c> (vglSwapBuffers),
///   <c>shepgl_has_extension</c> (S3TC), <c>shepgl_display_fbo</c> (0 on the Vita: the display), <c>shepgl_state</c>
/// - <c>shepgl_abi_probe</c> once, through the same <c>delegate* unmanaged</c> path Gl uses: (1, .5, .25, 2, 4, 1) must give 51.75, which proves
///   float arguments reach VFP registers on the interpreter's native-call path before ClearColor/Uniform4f depend on it.
/// No SDL is loaded in that mode (Backends.NoSdl): input comes from VitaPad's native SceCtrl reader and the pad script.
/// The Mac rehearsal (vita/shepherd/mac-check/run-gl.sh) runs the same host on Mac Mono with shepgl_mac.c (CGL legacy context).
/// Every P/Invoke sits in a never-inlined method: the Mono interpreter binds a method's P/Invokes when it transforms it, so nothing
/// here is bound unless the shepgl host is in use.</summary>
[ProfileOnly]
public static unsafe class ShepGl
{
    /// <summary>SHEPHERD_GL_HOST=shepgl: GlBackend binds to the native host's vitaGL context instead of creating an SDL one.</summary>
    public static readonly bool On = (Environment.GetEnvironmentVariable("SHEPHERD_GL_HOST") ?? "").Trim().ToLowerInvariant() == "shepgl";

    [MethodImpl(MethodImplOptions.NoInlining)] public static IntPtr Proc(string name) => shepgl_proc(name);
    [MethodImpl(MethodImplOptions.NoInlining)] public static int State() => shepgl_state();
    [MethodImpl(MethodImplOptions.NoInlining)] public static string Status() => Marshal.PtrToStringUTF8(shepgl_status()) ?? "";
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool Attach() => shepgl_attach() != 0;
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Swap() => shepgl_swap();
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool HasExtension(string name) => shepgl_has_extension(name) != 0;
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint DisplayFbo() => shepgl_display_fbo();
    /// <summary>vitaGL pool free/total in KiB (0 vram, 1 ram, 2 phycont, 3 cdlg, 4 newlib); 0 on the Mac shim.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint PoolKiB(int pool, bool total) => shepgl_pool_kib(pool, total ? 1 : 0);
#if !SHEPHERD_NO_K8
    /// <summary>Kit #8 (the real video): texture <paramref name="name"/>'s own memory (vglGetTexDataPointer; the binding restored), where the
    /// hardware decoder writes a picture; IntPtr.Zero on the Mac shim.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] public static IntPtr TexData(uint name) => shepgl_tex_data(name);
    [DllImport("__Internal")] static extern IntPtr shepgl_tex_data(uint name);
#endif

    // ---- GXP cache (device kit #5, Render/GxpCache.cs; vita/gltest/shepgl/shepgl.h) ----
    /// <summary>The native identity line a cached shader depends on (GL stack builds, libshacccg, compiler options), or null (shepgl_gxp_why).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GxpIdentity(out int ms)
    {
        var buf = new byte[2048]; int t = 0, n;
        fixed (byte* p = buf) n = shepgl_gxp_identity(p, buf.Length, &t);
        ms = t;
        return n > 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, n) : null;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static string GxpWhy() => Marshal.PtrToStringUTF8(shepgl_gxp_why()) ?? "";
    /// <summary>A compiled, linked shader as vitaGL serializes it (type 0 vertex, 1 fragment), or null (shepgl_gxp_why). Reads only.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[] GxpSave(uint program, uint shader, int type)
    {
        var buf = new byte[64 << 10];
        for (int pass = 0; pass < 2; pass++)
        {
            int n;
            fixed (byte* p = buf) n = shepgl_gxp_save(program, shader, type, p, buf.Length);
            if (n > 0) return buf.AsSpan(0, n).ToArray();
            if (n == 0) return null;
            buf = new byte[-n];   // more room asked for (vitaGL's bound for this program)
        }
        return null;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool GxpCheck(byte[] blob, int type) { fixed (byte* p = blob) return shepgl_gxp_check(p, blob.Length, type) == 1; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static bool GxpLoad(uint shader, int type, byte[] blob) { fixed (byte* p = blob) return shepgl_gxp_load(shader, type, p, blob.Length) == 1; }

    /// <summary>The float-ABI probe through Gl's calling path (an unmanaged function pointer, no marshalling). Returns the value;
    /// the caller compares it with 51.75.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float AbiProbe()
    {
        var p = shepgl_proc("shepgl_abi_probe");
        if (p == IntPtr.Zero) return float.NaN;
        var f = (delegate* unmanaged<int, float, float, float, float, byte, float>)p;
        return f(1, 0.5f, 0.25f, 2f, 4f, 1);
    }

    /// <summary>The pool line for heartbeats: "vram 12.3/104.0 ram 2.1/9.0 phycont .. MiB used/total" (empty on the Mac shim).</summary>
    public static string Pools()
    {
        var sb = new System.Text.StringBuilder();
        string[] names = { "vram", "ram", "phycont", "cdlg", "newlib" };
        for (int i = 0; i < 4; i++)
        {
            uint tot = PoolKiB(i, true); if (tot == 0) continue;
            uint free = PoolKiB(i, false);
            sb.Append(sb.Length > 0 ? " " : "").Append($"{names[i]} {(tot - Math.Min(free, tot)) / 1024.0:F1}/{tot / 1024.0:F1}");
        }
        return sb.Length > 0 ? sb.Append(" MiB used/total").ToString() : "";
    }

    [DllImport("__Internal")] static extern IntPtr shepgl_proc([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("__Internal")] static extern int shepgl_state();
    [DllImport("__Internal")] static extern IntPtr shepgl_status();
    [DllImport("__Internal")] static extern int shepgl_attach();
    [DllImport("__Internal")] static extern void shepgl_swap();
    [DllImport("__Internal")] static extern int shepgl_has_extension([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("__Internal")] static extern uint shepgl_display_fbo();
    [DllImport("__Internal")] static extern uint shepgl_pool_kib(int pool, int total);
    [DllImport("__Internal")] static extern int shepgl_gxp_identity(byte* out_, int cap, int* ms);
    [DllImport("__Internal")] static extern IntPtr shepgl_gxp_why();
    [DllImport("__Internal")] static extern int shepgl_gxp_save(uint program, uint shader, int type, byte* buf, int cap);
    [DllImport("__Internal")] static extern int shepgl_gxp_check(byte* buf, int len, int type);
    [DllImport("__Internal")] static extern int shepgl_gxp_load(uint shader, int type, byte* buf, int len);
}
