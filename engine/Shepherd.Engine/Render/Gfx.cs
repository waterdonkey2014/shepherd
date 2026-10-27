using System.Runtime.InteropServices;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>2D triangle rasterizer facade over SDL_Renderer (Metal). Everything draws into a fixed-size target texture,
/// which is then shown in the window (or read back for screenshots when headless). The default render backend and the
/// reference image for the others (IRenderBackend); its behaviour is the pre-V4 renderer's, call for call.</summary>
public sealed unsafe class Gfx : IRenderBackend
{
    public readonly int W, H; public IntPtr Window, Renderer, Target; public bool Headless;
    int IRenderBackend.W => W; int IRenderBackend.H => H;
    public string Name => Backends.SdlName;
    public RenderStats Stats { get; } = new();   // SDL batches internally; only DrawsIn/Tris are known here
    readonly RenderStats _cur = new();
    public Gfx(int w, int h, bool headless, string title = "Shepherd — Cult of the Lamb")
    {
        W = w; H = h; Headless = headless;
        if (!SDL_Init(INIT_VIDEO | INIT_EVENTS | INIT_GAMEPAD)) throw new Exception("SDL_Init: " + Error);
        Window = SDL_CreateWindow(title, w, h, headless ? WINDOW_HIDDEN : WINDOW_RESIZABLE);
        if (Window == IntPtr.Zero) throw new Exception("SDL_CreateWindow: " + Error);
        Renderer = SDL_CreateRenderer(Window, null);
        if (Renderer == IntPtr.Zero) throw new Exception("SDL_CreateRenderer: " + Error);
        SDL_SetRenderVSync(Renderer, headless ? 0 : 1);
        Target = SDL_CreateTexture(Renderer, PIXELFORMAT_ABGR8888, TEXTUREACCESS_TARGET, w, h);
        SDL_SetTextureBlendMode(Target, BLENDMODE_NONE);
    }
    public IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear = true)
    {
        var t = SDL_CreateTexture(Renderer, PIXELFORMAT_ABGR8888, TEXTUREACCESS_STATIC, w, h);
        if (t == IntPtr.Zero) { Log.Warn($"SDL_CreateTexture {w}x{h}: {Error}"); return IntPtr.Zero; }
        fixed (byte* p = rgba) SDL_UpdateTexture(t, null, p, w * 4);
        SDL_SetTextureScaleMode(t, linear ? 1 : 0);
        return t;
    }
    /// <summary>SDL has no shader hook: an SDF atlas is baked on upload (alpha = smoothstep of the distance, rgb white).</summary>
    public IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf, bool owned)
    {
        if (sdf)
        {
            if (!owned) rgba = (byte[])rgba.Clone();
            BakeSdf(rgba);
        }
        return CreateTexture(w, h, rgba, linear);
    }
    public static void BakeSdf(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4) { float d = px[i + 3] / 255f; float a = Math.Clamp((d - 0.44f) / 0.12f, 0f, 1f); a = a * a * (3 - 2 * a); px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = (byte)(a * 255); }
    }
    public void DestroyTexture(IntPtr tex) { if (tex != IntPtr.Zero) SDL_DestroyTexture(tex); }
    public IntPtr CreateTarget(int w, int h)
    {
        var t = SDL_CreateTexture(Renderer, PIXELFORMAT_ABGR8888, TEXTUREACCESS_TARGET, w, h);
        if (t == IntPtr.Zero) Log.Warn($"SDL_CreateTexture {w}x{h}: {Error}");
        return t;
    }
    public void SetTarget(IntPtr target) => SDL_SetRenderTarget(Renderer, target == IntPtr.Zero ? Target : target);
    public void Begin(float r, float g, float b, float a)
    {
        SDL_SetRenderTarget(Renderer, Target); SDL_SetRenderClipRect(Renderer, null); SDL_SetRenderDrawColorFloat(Renderer, r, g, b, a); SDL_RenderClear(Renderer);
    }
    public void ClearTarget(float r, float g, float b, float a) { SDL_SetRenderDrawColorFloat(Renderer, r, g, b, a); SDL_RenderClear(Renderer); }   // SDL_RenderClear ignores the clip rect
    public void Clip(int x, int y, int w, int h) { var rc = new Sdl.Rect { x = x, y = y, w = w, h = h }; SDL_SetRenderClipRect(Renderer, &rc); }
    public void NoClip() => SDL_SetRenderClipRect(Renderer, null);
    public void Draw(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx)
    {
        if (v.Length == 0 || idx.Length == 0) return;
        if (ShaderPolicy.IsGrab(blend)) return;   // polish round 2: a BlendModes backdrop draw needs a shader stage; SDL_Renderer has none
        _cur.DrawsIn++; _cur.Tris += idx.Length / 3;
        if (tex != IntPtr.Zero) SDL_SetTextureBlendMode(tex, blend);
        fixed (Vertex* pv = v) fixed (int* pi = idx) SDL_RenderGeometry(Renderer, tex, pv, v.Length, pi, idx.Length);
    }
    public void Draw(IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx) => Draw(tex, blend, v, idx);   // sdf was baked into tex
    /// <summary>CPU Stylizer (owner-local, DerivedRender): readback (<see cref="ReadTargetRgba"/>), its per-pixel kernel, then
    /// <see cref="BlitOverTarget"/>. Without the owner-local code: nothing (the world pass as drawn).</summary>
    public void EndWorldPass() { _cur.WorldDrawsIn = _cur.DrawsIn; DerivedRender.Stylizer?.Apply(this); }
    /// <summary>The main target's pixels into a reused top-down RGBA8 buffer (null on failure). The buffer is the one
    /// <see cref="BlitOverTarget"/> uploads; MemReport drops it (<see cref="DropCpuPassBuffer"/>).</summary>
    public byte[] ReadTargetRgba(out int w, out int h)
    {
        w = h = 0;
        SDL_SetRenderTarget(Renderer, Target);
        var s = SDL_RenderReadPixels(Renderer, null); if (s == null) return null;
        var c = SDL_ConvertSurface(s, PIXELFORMAT_ABGR8888); SDL_DestroySurface(s);
        int W = c->w, H = c->h; if (_cpuBuf == null || _cpuBuf.Length != W * H * 4) _cpuBuf = new byte[W * H * 4];
        for (int y = 0; y < H; y++) System.Runtime.InteropServices.Marshal.Copy(c->pixels + y * c->pitch, _cpuBuf, y * W * 4, W * 4);
        SDL_DestroySurface(c);
        w = W; h = H; return _cpuBuf;
    }
    /// <summary>Upload a top-down RGBA8 frame (w x h, the size <see cref="ReadTargetRgba"/> returned) into a streaming texture and draw it
    /// over the whole current target, opaque.</summary>
    public void BlitOverTarget(byte[] rgba, int w, int h)
    {
        if (_cpuStream == IntPtr.Zero) { _cpuStream = SDL_CreateTexture(Renderer, PIXELFORMAT_ABGR8888, TEXTUREACCESS_STREAMING, w, h); SDL_SetTextureBlendMode(_cpuStream, BLENDMODE_NONE); SDL_SetTextureScaleMode(_cpuStream, 0); }
        fixed (byte* p = rgba) SDL_UpdateTexture(_cpuStream, null, p, w * 4);
        SDL_RenderTexture(Renderer, _cpuStream, null, null);
    }
    public void DropCpuPassBuffer() => _cpuBuf = null;
    byte[] _cpuBuf; IntPtr _cpuStream;
    public void EndFrame() { _cur.CopyTo(Stats); _cur.Reset(); }   // SDL itself submits on readback/present
    public void Finish()
    {
        SDL_SetRenderTarget(Renderer, Target); var s = SDL_RenderReadPixels(Renderer, null); if (s != null) SDL_DestroySurface(s);
    }
    public void Present()
    {
        SDL_SetRenderTarget(Renderer, IntPtr.Zero);
        SDL_SetRenderDrawColorFloat(Renderer, 0, 0, 0, 1); SDL_RenderClear(Renderer);
        int pw, ph; SDL_GetWindowSizeInPixels(Window, &pw, &ph);
        float s = Math.Min((float)pw / W, (float)ph / H); var dst = new FRect { w = W * s, h = H * s, x = (pw - W * s) / 2, y = (ph - H * s) / 2 };
        SDL_RenderTexture(Renderer, Target, null, &dst);
        SDL_RenderPresent(Renderer);
    }
    public byte[] ReadPixels(out int w, out int h)
    {
        w = h = 0;
        SDL_SetRenderTarget(Renderer, Target);
        var s = SDL_RenderReadPixels(Renderer, null); if (s == null) { Log.Warn("ReadPixels: " + Error); return null; }
        var c = SDL_ConvertSurface(s, PIXELFORMAT_ABGR8888); SDL_DestroySurface(s);
        var buf = new byte[c->w * c->h * 4];
        for (int y = 0; y < c->h; y++) Marshal.Copy(c->pixels + y * c->pitch, buf, y * c->w * 4, c->w * 4);
        w = c->w; h = c->h; SDL_DestroySurface(c);
        return buf;
    }
    public void Screenshot(string path)
    {
        var buf = ReadPixels(out var w, out var h); if (buf == null) return;
        Png.Write(path, w, h, buf);
    }
    public void Dispose() { SDL_Quit(); }
}
