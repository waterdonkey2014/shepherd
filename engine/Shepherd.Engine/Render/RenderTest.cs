using System.Diagnostics;
using Shepherd.Engine.Native;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary><c>rendertest</c>: backend parity without the game. (1) A synthetic scene of one feature per cell (blend modes,
/// untextured draws, filtering, wrap on POT/NPOT, clip, SDF, render-to-texture, batching, vertex colours) is drawn through
/// SDL and GL from the same draw stream and compared cell by cell. (2) The GPU Stylizer pass against the CPU kernel (both owner-local:
/// DerivedRender.Stylizer; skipped without it) on synthetic frames, LUTs and patterns (incl. layer counts that put pattern values exactly
/// on layer boundaries, two palettes, pixelate). (3) GPU Stylizer cost at 960x544 and 1280x720 (same). Writes runtime/shots/rendertest/.
/// Exit code 0 = all checks inside their tolerance.</summary>
[ProfileOnly]
public static class RenderTest
{
    /// <summary>Boot mode name (the literal lives in a [ProfileOnly] type, like VitaSelfTest's).</summary>
    public static bool Handles(string mode) => mode == "rendertest";
    const int W = 512, H = 384, CW = 128, CH = 96;
    static int _fail;
    static string Dir => Path.Combine(Program.Root, "runtime", "shots", "rendertest");

    public static int Run()
    {
        Directory.CreateDirectory(Dir);
        _fail = 0;
        using (var gl = new GlBackend(W, H, headless: true))
        using (var sdl = new Gfx(W, H, headless: true))
        {
            var tee = new TeeBackend(gl, sdl);
            Scene(tee);
            var a = gl.ReadPixels(out _, out _); var b = sdl.ReadPixels(out _, out _);
            Png.Write(Path.Combine(Dir, "scene-gl.png"), W, H, a); Png.Write(Path.Combine(Dir, "scene-sdl.png"), W, H, b);
            var all = ImageDiff.Compare(a, b, W, H, out var vis); Png.Write(Path.Combine(Dir, "scene-diff.png"), W, H, vis);
            Log.Info($"[rendertest] scene {W}x{H} gl vs sdl: {all}");
            Log.Info($"[rendertest] gl stats: {gl.Stats}");
            for (int c = 0; c < Cells.Length; c++)
            {
                int cx = c % 4 * CW, cy = c / 4 * CH;
                var ca = Crop(a, cx, cy); var cb = Crop(b, cx, cy);
                var d = ImageDiff.Compare(ca, cb, CW, CH, out _);
                var (meanTol, pctTol) = Cells[c].tol;
                bool ok = d.MeanAbs <= meanTol && d.Pct8 <= pctTol;
                if (!ok) _fail++;
                Log.Info($"[rendertest] cell {c,2} {Cells[c].name,-28} {(ok ? "PASS" : "FAIL")} {d}");
            }
            // (4) present: the copy pass into the window's default framebuffer (bottom-up, letterboxed) must reproduce the target
            gl.PresentCopy();   // read back before the swap: after it the back buffer is undefined
            var win = gl.ReadWindow(out int ww, out int wh);
            if (win == null || ww != W || wh != H) Log.Info($"[rendertest] present: window framebuffer {ww}x{wh} not readable at {W}x{H} (hidden window) -- SKIPPED");
            else { var dp = ImageDiff.Compare(win, a, W, H, out _); bool ok = dp.Max == 0; if (!ok) _fail++; Log.Info($"[rendertest] present (default framebuffer, flipped copy) {(ok ? "PASS" : "FAIL")} {dp}"); }
            StylizerChecks(gl);
            DxtChecks(gl);
        }
        using (var gl = new GlBackend(W, H, headless: true))
        using (var sdl = new Gfx(W, H, headless: true)) FxParity(gl, sdl);
        using (var gl = new GlBackend(320, 240, headless: true)) GrabChecks(gl);
        using (var gl2 = new GlBackend(960, 544, headless: true)) StylizerCost(gl2);
        using (var gl3 = new GlBackend(1280, 720, headless: true)) StylizerCost(gl3);
        Log.Info($"[rendertest] {(_fail == 0 ? "ALL PASS" : _fail + " FAILED")}");
        return _fail == 0 ? 0 : 1;
    }

    static byte[] Crop(byte[] src, int x0, int y0)
    {
        var d = new byte[CW * CH * 4];
        for (int y = 0; y < CH; y++) Array.Copy(src, ((y0 + y) * W + x0) * 4, d, y * CW * 4, CW * 4);
        return d;
    }

    // ---------------- (1) scene ----------------
    static readonly (string name, (double mean, double pct) tol)[] Cells =
    {
        ("textured BLEND linear", (1.0, 0.5)), ("textured BLEND nearest", (1.0, 0.5)), ("untextured alpha (draw blend)", (0.5, 0.1)), ("ADD", (1.0, 0.5)),
        ("MOD", (1.0, 0.5)), ("MUL", (1.0, 0.5)), ("BLEND_PREMULTIPLIED", (1.0, 0.5)), ("ADD_PREMULTIPLIED", (1.0, 0.5)),
        ("wrap POT (uv 0..3)", (1.5, 1.0)), ("wrap NPOT (uv -1..2)", (1.5, 1.0)), ("clip rect", (0.5, 0.2)), ("SDF circle x4", (3.0, 3.0)),
        ("render target round trip", (1.0, 0.5)), ("200 quads one texture", (1.0, 0.5)), ("vertex colour gradient", (1.0, 0.2)), ("texture ping-pong (breaks)", (1.0, 0.5)),
    };

    static byte[] Checker(int w, int h, int cell, bool alphaRamp)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            bool c = ((x / cell) + (y / cell)) % 2 == 0; int i = (y * w + x) * 4;
            px[i] = (byte)(c ? 240 : 30); px[i + 1] = (byte)(c ? 180 : 60 + x * 150 / w); px[i + 2] = (byte)(c ? 40 : 200); px[i + 3] = (byte)(alphaRamp ? 255 * y / Math.Max(1, h - 1) : 255);
        }
        return px;
    }
    static Vertex V(float x, float y, float u, float v, float r = 1, float g = 1, float b = 1, float a = 1) =>
        new() { position = new() { x = x, y = y }, color = new() { r = r, g = g, b = b, a = a }, tex_coord = new() { x = u, y = v } };
    static readonly int[] Q = { 0, 1, 2, 0, 2, 3 };
    static void Quad(IRenderBackend g, IntPtr tex, uint blend, float x, float y, float w, float h, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1, float r = 1, float gg = 1, float b = 1, float a = 1, bool sdf = false)
    {
        var v = new[] { V(x, y, u0, v0, r, gg, b, a), V(x + w, y, u1, v0, r, gg, b, a), V(x + w, y + h, u1, v1, r, gg, b, a), V(x, y + h, u0, v1, r, gg, b, a) };
        g.Draw(tex, blend, sdf, v, Q);
    }

    static void Scene(IRenderBackend g)
    {
        var chk = g.CreateTexture(64, 64, Checker(64, 64, 8, true), linear: true);
        var chkN = g.CreateTexture(64, 64, Checker(64, 64, 8, true), linear: false);
        var opaque = g.CreateTexture(32, 32, Checker(32, 32, 4, false), linear: true);
        var npot = g.CreateTexture(48, 40, Checker(48, 40, 6, false), linear: true);
        var sdfPx = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) { float d = 24f - MathF.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f)); int i = (y * 64 + x) * 4; sdfPx[i] = 255; sdfPx[i + 1] = 255; sdfPx[i + 2] = 255; sdfPx[i + 3] = (byte)Math.Clamp(0.5f * 255 + d * 10f, 0, 255); }
        var sdfTex = g.CreateTexture(64, 64, sdfPx, linear: true, sdf: true, owned: false);
        var rt = g.CreateTarget(64, 48);

        g.Begin(0.10f, 0.12f, 0.18f, 1);
        // backdrops: an opaque untextured gradient per cell, so blending has something to work against
        for (int c = 0; c < 16; c++)
        {
            int cx = c % 4 * CW, cy = c / 4 * CH;
            var v = new[] { V(cx, cy, 0, 0, 0.2f, 0.5f, 0.8f), V(cx + CW, cy, 0, 0, 0.9f, 0.4f, 0.2f), V(cx + CW, cy + CH, 0, 0, 0.3f, 0.9f, 0.3f), V(cx, cy + CH, 0, 0, 0.6f, 0.6f, 0.6f) };
            g.Draw(IntPtr.Zero, BLENDMODE_NONE, false, v, Q);
        }
        float X(int c) => c % 4 * CW + 14; float Y(int c) => c / 4 * CH + 10;
        Quad(g, chk, BLENDMODE_BLEND, X(0), Y(0), 100, 76);
        Quad(g, chkN, BLENDMODE_BLEND, X(1), Y(1), 100, 76);
        Quad(g, IntPtr.Zero, BLENDMODE_BLEND, X(2), Y(2), 100, 76, r: 1, gg: 0.2f, b: 0.6f, a: 0.5f);    // untextured: SDL uses the draw blend mode
        Quad(g, opaque, BLENDMODE_ADD, X(3), Y(3), 100, 76, a: 0.6f);
        Quad(g, opaque, BLENDMODE_MOD, X(4), Y(4), 100, 76);
        Quad(g, chk, BLENDMODE_MUL, X(5), Y(5), 100, 76);
        Quad(g, chk, BLENDMODE_BLEND_PREMULTIPLIED, X(6), Y(6), 100, 76, r: 0.7f * 0.7f, gg: 0.9f * 0.7f, b: 0.7f, a: 0.7f);
        Quad(g, opaque, BLENDMODE_ADD_PREMULTIPLIED, X(7), Y(7), 100, 76, r: 0.5f, gg: 0.5f, b: 0.5f, a: 0.5f);
        Quad(g, opaque, BLENDMODE_BLEND, X(8), Y(8), 100, 76, 0, 0, 3, 3);
        Quad(g, npot, BLENDMODE_BLEND, X(9), Y(9), 100, 76, -1, -1, 2, 2);
        g.Clip((int)X(10) + 20, (int)Y(10) + 15, 50, 40);
        Quad(g, chk, BLENDMODE_BLEND, X(10), Y(10), 100, 76);
        g.NoClip();
        Quad(g, sdfTex, BLENDMODE_BLEND, X(11), Y(11), 76, 76, sdf: true, r: 0.1f, gg: 0.1f, b: 0.1f);
        // render target: draw into it, then sample it
        g.SetTarget(rt);
        Quad(g, IntPtr.Zero, BLENDMODE_NONE, 0, 0, 64, 48, r: 0.9f, gg: 0.9f, b: 0.2f);
        Quad(g, chkN, BLENDMODE_BLEND, 8, 4, 40, 30);
        g.SetTarget(IntPtr.Zero);
        Quad(g, rt, BLENDMODE_BLEND, X(12), Y(12), 64 * 1.5f, 48 * 1.5f);
        for (int k = 0; k < 200; k++) Quad(g, opaque, BLENDMODE_BLEND, X(13) + (k % 20) * 5, Y(13) + (k / 20) * 7, 4, 6, a: 0.8f);
        {
            var v = new[] { V(X(14), Y(14), 0, 0, 1, 0, 0), V(X(14) + 100, Y(14), 1, 0, 0, 1, 0), V(X(14) + 100, Y(14) + 76, 1, 1, 0, 0, 1), V(X(14), Y(14) + 76, 0, 1, 1, 1, 1, 0.5f) };
            g.Draw(opaque, BLENDMODE_BLEND, false, v, Q);
        }
        for (int k = 0; k < 20; k++) Quad(g, k % 2 == 0 ? opaque : chk, BLENDMODE_BLEND, X(15) + (k % 10) * 10, Y(15) + (k / 10) * 38, 9, 36);
        g.EndWorldPass();   // no Stylizer in this process: a no-op on both
        g.EndFrame();
    }

    // ---------------- (2) GPU Stylizer vs CPU kernel ----------------
    static readonly int[] Bayer = { 0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26, 12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22, 3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25, 15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21 };
    static byte[] Frame(int w, int h, int seed)
    {
        var r = new Random(seed); var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)(x * 255 / Math.Max(1, w - 1)); px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1)); px[i + 2] = (byte)((x * 7 + y * 3) & 255);
            if (r.Next(4) == 0) { px[i] = (byte)r.Next(256); px[i + 1] = (byte)r.Next(256); px[i + 2] = (byte)r.Next(256); }   // every 8-bit value occurs
            px[i + 3] = 255;
        }
        return px;
    }
    static NTexture LutTex(int layers, int seed)
    {
        var r = new Random(seed); int w = 256, h = layers * 16; var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = (byte)r.Next(256); px[i * 4 + 1] = (byte)r.Next(256); px[i * 4 + 2] = (byte)r.Next(256); px[i * 4 + 3] = 255; }
        return new NTexture { Width = w, Height = h, Rgba = px, Loaded = true };
    }
    static NTexture Pattern(int n)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { int i = (y * n + x) * 4; byte v = (byte)(n == 8 ? Bayer[y * 8 + x] * 4 + 2 : (x * 37 + y * 101) % 256); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        // put exact layer-boundary values in (51/255 = 0.2: on a boundary for 5 layers; 85 = 1/3 for 3; 255 = the top of the range)
        px[0] = 51; px[4] = 85; px[8] = 255; px[12] = 0;
        return new NTexture { Width = n, Height = n, Rgba = px, Loaded = true };
    }

    static void StylizerChecks(GlBackend g)
    {
        var sty = DerivedRender.Stylizer;
        if (sty == null || !GlBackend.HasSource("stylizer.frag")) { Log.Info($"[rendertest] stylizer: owner-local Stylizer not present ({DerivedRender.Status}) -- SKIPPED"); return; }
        var cases = new (string name, int l1, int l2, float lerp, int block, float intensity, int pat)[]
        {
            ("1 palette, 5 layers, bayer 8x8", 5, 0, 0, 1, 1f, 8),
            ("1 palette, 3 layers, 13x13 pattern", 3, 0, 0, 1, 1f, 13),
            ("2 palettes 4+6, lerp .35, intensity .8", 4, 6, 0.35f, 1, 0.8f, 8),
            ("pixelate block 3, 7 layers", 7, 0, 0, 3, 1f, 8),
            ("no pattern, 2 layers, intensity .5", 2, 0, 0, 1, 0.5f, 0),
        };
        int seed = 1;
        foreach (var c in cases)
        {
            var frame = Frame(W, H, seed);
            var l1 = LutTex(c.l1, seed * 10 + 1);
            var l2 = c.l2 > 0 ? LutTex(c.l2, seed * 10 + 2) : null;
            var pat = c.pat > 0 ? Pattern(c.pat) : null;
            // CPU reference
            var cpu = (byte[])frame.Clone();
            sty.Kernel(cpu, W, H, l1, c.l1, l2, c.l2, c.lerp, pat, c.block, c.intensity);
            // GPU: draw the frame into the world target, run the pass, read back
            var ft = g.CreateTexture(W, H, frame, linear: false);
            var h1 = g.CreateTexture(l1.Width, l1.Height, l1.Rgba, linear: false);
            var h2 = l2 != null ? g.CreateTexture(l2.Width, l2.Height, l2.Rgba, linear: false) : IntPtr.Zero;
            var map = sty.LayerMap(pat?.Rgba, pat?.Width ?? 0, pat?.Height ?? 0, c.l1, c.l2);
            int mw = pat?.Width ?? 1, mh = pat?.Height ?? 1;
            var hm = g.CreateTexture(mw, mh, map, linear: false);
            g.Begin(0, 0, 0, 1);
            Quad(g, ft, BLENDMODE_NONE, 0, 0, W, H);
            g.RunStylizer(new GlBackend.StylizeArgs { Lut1 = h1, Lut1W = l1.Width, Lut1H = l1.Height, Lut2 = h2, Lut2W = l2?.Width ?? 0, Lut2H = l2?.Height ?? 0, PaletteLerp = c.lerp, Layers = hm, LayersW = mw, LayersH = mh, Block = c.block, Intensity = c.intensity });
            g.EndFrame();
            var gpu = g.ReadPixels(out _, out _);
            var d = ImageDiff.Compare(gpu, cpu, W, H, out var vis);
            int exact = 0; for (int i = 0; i < W * H; i++) if (gpu[i * 4] == cpu[i * 4] && gpu[i * 4 + 1] == cpu[i * 4 + 1] && gpu[i * 4 + 2] == cpu[i * 4 + 2]) exact++;
            bool ok = d.Max <= 1;
            if (!ok) _fail++;
            Log.Info($"[rendertest] stylizer {c.name,-40} {(ok ? "PASS" : "FAIL")} exact={100.0 * exact / (W * H):F2}% {d}");
            if (!ok || seed == 1) { Png.Write(Path.Combine(Dir, $"sty{seed}-gpu.png"), W, H, gpu); Png.Write(Path.Combine(Dir, $"sty{seed}-cpu.png"), W, H, cpu); Png.Write(Path.Combine(Dir, $"sty{seed}-diff.png"), W, H, vis); }
            foreach (var t in new[] { ft, h1, h2, hm }) g.DestroyTexture(t);
            seed++;
        }
    }

    // ---------------- (5) DXT1/DXT5 compressed upload: block flip + GPU decode vs the engine's CPU decoder ----------------
    static void DxtChecks(GlBackend g)
    {
        if (!g.S3tc) { Log.Info("[rendertest] dxt: no S3TC in this context -- SKIPPED"); return; }
        foreach (var fmt in new[] { DxtBlocks.UnityDXT1, DxtBlocks.UnityDXT5 })
        {
            int w = 64, h = 48, bb = DxtBlocks.BlockBytes(fmt); var r = new Random(fmt);
            var blocks = new byte[w / 4 * (h / 4) * bb]; r.NextBytes(blocks);   // random blocks: both BC1 modes, both BC3 alpha modes
            var cpu = Shepherd.Assets.TextureDecoder.Decode(blocks, w, h, (Shepherd.Assets.TextureFormat)fmt);   // decode + FlipRows = top-down
            var hd = g.CreateCompressedTexture(w, h, fmt, DxtBlocks.FlipY(blocks, w, h, fmt), linear: false);
            if (hd == IntPtr.Zero) { _fail++; Log.Info($"[rendertest] dxt fmt {fmt}: upload FAILED"); continue; }
            g.Begin(0, 0, 0, 0);
            Quad(g, hd, BLENDMODE_NONE, 0, 0, w, h);
            g.EndFrame();
            var all = g.ReadPixels(out _, out _); var gpu = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Array.Copy(all, y * W * 4, gpu, y * w * 4, w * 4);
            var d = ImageDiff.Compare(gpu, cpu, w, h, out _);
            int amax = 0; for (int i = 3; i < gpu.Length; i += 4) amax = Math.Max(amax, Math.Abs(gpu[i] - cpu[i]));
            bool ok = d.Max <= 8 && amax <= 8;   // BC1/BC3 interpolation rounding is implementation-defined; layout errors show up as big diffs
            if (!ok) _fail++;
            Log.Info($"[rendertest] dxt {(fmt == DxtBlocks.UnityDXT1 ? "DXT1" : "DXT5")} {w}x{h} flipped-block upload vs CPU decode {(ok ? "PASS" : "FAIL")} {d} alphaMax={amax} ({blocks.Length} B vs {w * h * 4} B RGBA)");
            g.DestroyTexture(hd);
        }
    }

    // ---------------- (6) polish round 2: composed blends + target clears (GL vs SDL), BlendModes grab draws (GL vs CPU) ----------------
    static readonly (string name, (double mean, double pct) tol)[] FxCells =
    {
        ("composed One One (AddOne)", (1.0, 0.5)), ("composed 2x multiply", (1.0, 0.5)), ("composed screen", (1.0, 0.5)), ("MOD with white tex", (1.0, 0.5)),
        ("target ClearTarget rgba", (1.0, 0.5)), ("target clear then draw", (1.0, 0.5)), ("ADD tinted (glow)", (1.0, 0.5)), ("grab draw (SDL skips)", (0.0, 0.0)),
    };
    static void FxParity(GlBackend gl, Gfx sdl)
    {
        var tee = new TeeBackend(gl, sdl); IRenderBackend g = tee;
        var white = g.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 }, linear: false);
        var chk = g.CreateTexture(64, 64, Checker(64, 64, 8, true), linear: true);
        var opaque = g.CreateTexture(32, 32, Checker(32, 32, 4, false), linear: true);
        var rt = g.CreateTarget(64, 48);
        g.Begin(0.10f, 0.12f, 0.18f, 1);
        for (int c = 0; c < 8; c++)
        {
            int cx = c % 4 * CW, cy = c / 4 * CH;
            var v = new[] { V(cx, cy, 0, 0, 0.2f, 0.5f, 0.8f), V(cx + CW, cy, 0, 0, 0.9f, 0.4f, 0.2f), V(cx + CW, cy + CH, 0, 0, 0.3f, 0.9f, 0.3f), V(cx, cy + CH, 0, 0, 0.6f, 0.6f, 0.6f) };
            g.Draw(IntPtr.Zero, BLENDMODE_NONE, false, v, Q);
        }
        float X(int c) => c % 4 * CW + 14; float Y(int c) => c / 4 * CH + 10;
        Quad(g, opaque, ShaderPolicy.AddOne, X(0), Y(0), 100, 76, r: 0.4f, gg: 0.3f, b: 0.2f);
        Quad(g, chk, ShaderPolicy.Mul2x, X(1), Y(1), 100, 76);
        Quad(g, opaque, ShaderPolicy.Screen, X(2), Y(2), 100, 76, r: 0.8f, gg: 0.6f, b: 0.4f);
        Quad(g, white, BLENDMODE_MOD, X(3), Y(3), 100, 76, r: 0.5f, gg: 0.8f, b: 0.3f);
        g.SetTarget(rt); g.ClearTarget(0.9f, 0.2f, 0.4f, 0.5f); g.SetTarget(IntPtr.Zero);
        Quad(g, rt, BLENDMODE_BLEND, X(4), Y(4), 96, 72);
        g.SetTarget(rt); g.ClearTarget(0, 0, 0, 0); Quad(g, chk, BLENDMODE_BLEND, 4, 4, 56, 40); g.SetTarget(IntPtr.Zero);
        Quad(g, rt, BLENDMODE_BLEND_PREMULTIPLIED, X(5), Y(5), 96, 72);
        Quad(g, white, BLENDMODE_ADD, X(6), Y(6), 100, 76, r: 0.25f, gg: 0.05f, b: 0f, a: 1f);
        Quad(g, chk, ShaderPolicy.GrabFlag | (10u << 8), X(7), Y(7), 100, 76);   // a grab draw: GL draws it, SDL skips it: the cell is compared on GL only (see GrabChecks)
        g.EndWorldPass(); g.EndFrame();
        var a = gl.ReadPixels(out _, out _); var b = sdl.ReadPixels(out _, out _);
        Png.Write(Path.Combine(Dir, "fx-gl.png"), W, H, a); Png.Write(Path.Combine(Dir, "fx-sdl.png"), W, H, b);
        for (int c = 0; c < FxCells.Length; c++)
        {
            int cx = c % 4 * CW, cy = c / 4 * CH;
            var d = ImageDiff.Compare(Crop(a, cx, cy), Crop(b, cx, cy), CW, CH, out _);
            bool grab = c == 7;
            bool ok = grab ? d.Pct8 > 1.0 : d.MeanAbs <= FxCells[c].tol.mean && d.Pct8 <= FxCells[c].tol.pct;   // the grab cell must differ: GL drew it, SDL did not
            if (!ok) _fail++;
            Log.Info($"[rendertest] fx cell {c} {FxCells[c].name,-28} {(ok ? "PASS" : "FAIL")} {d}");
        }
        Log.Info($"[rendertest] fx gl stats: {gl.Stats}");
    }
    /// <summary>Every BlendModes mode through the GL grab program against the same formulas on the CPU (both compositions): a backdrop
    /// gradient is drawn and read back, then one quad per mode (a solid source colour with alpha) is drawn as a grab draw; each
    /// cell's pixels must match the CPU composite of the read-back backdrop within 2 levels (Hard Mix / Darker / Lighter Color are
    /// discontinuous: up to 1 % of a cell may differ at the edge).</summary>
    static void GrabChecks(GlBackend g)
    {
        const int GW = 320, GH = 240, cw = 64, ch = 48;
        var white = g.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 }, linear: false);
        foreach (bool add in new[] { false, true })
        {
            g.Begin(0, 0, 0, 1);
            var bv = new[] { V(0, 0, 0, 0, 0.05f, 0.9f, 0.3f), V(GW, 0, 0, 0, 0.95f, 0.1f, 0.6f), V(GW, GH, 0, 0, 0.4f, 0.55f, 0.97f), V(0, GH, 0, 0, 0.7f, 0.25f, 0.02f) };
            g.Draw(IntPtr.Zero, BLENDMODE_NONE, false, bv, Q);
            g.EndFrame();
            var back = g.ReadPixels(out _, out _);
            g.Begin(0, 0, 0, 1); g.Draw(IntPtr.Zero, BLENDMODE_NONE, false, bv, Q);
            var srcs = new (float r, float gg, float b, float a)[25];
            for (int m = 0; m < 25; m++)
            {
                srcs[m] = (0.15f + 0.03f * m, 0.8f - 0.025f * m, 0.35f + 0.011f * m, m % 3 == 0 ? 1f : 0.62f);
                var s = srcs[m];
                Quad(g, white, ShaderPolicy.GrabFlag | ((uint)m << 8) | (add ? 1u : 0u), m % 5 * cw + 2, m / 5 * ch + 2, cw - 4, ch - 4, r: s.r, gg: s.gg, b: s.b, a: s.a);
            }
            g.EndFrame();
            var got = g.ReadPixels(out _, out _);
            int bad = 0, worst = -1; double worstPct = 0;
            for (int m = 0; m < 25; m++)
            {
                int x0 = m % 5 * cw + 2, y0 = m / 5 * ch + 2, n = 0, off = 0, maxd = 0;
                for (int y = y0; y < y0 + ch - 4; y++) for (int x = x0; x < x0 + cw - 4; x++)
                {
                    int i = (y * GW + x) * 4; var s = srcs[m];
                    var bb = (back[i] / 255.0, back[i + 1] / 255.0, back[i + 2] / 255.0);
                    var r = CpuBlend(m, bb, (s.r, s.gg, s.b));
                    double rr, rg, rb;
                    if (add) { rr = bb.Item1 + Math.Clamp(r.Item1, 0, 1) * s.a; rg = bb.Item2 + Math.Clamp(r.Item2, 0, 1) * s.a; rb = bb.Item3 + Math.Clamp(r.Item3, 0, 1) * s.a; }
                    else { rr = bb.Item1 + (Math.Clamp(r.Item1, 0, 1) - bb.Item1) * s.a; rg = bb.Item2 + (Math.Clamp(r.Item2, 0, 1) - bb.Item2) * s.a; rb = bb.Item3 + (Math.Clamp(r.Item3, 0, 1) - bb.Item3) * s.a; }
                    int d = Math.Max(Math.Abs(got[i] - (int)Math.Round(Math.Clamp(rr, 0, 1) * 255)), Math.Max(Math.Abs(got[i + 1] - (int)Math.Round(Math.Clamp(rg, 0, 1) * 255)), Math.Abs(got[i + 2] - (int)Math.Round(Math.Clamp(rb, 0, 1) * 255))));
                    n++; if (d > 2) off++; maxd = Math.Max(maxd, d);
                }
                double pct = 100.0 * off / n; bool discontinuous = m is 4 or 9 or 16;
                if (pct > (discontinuous ? 1.0 : 0.0)) { bad++; if (pct > worstPct) { worstPct = pct; worst = m; } }
            }
            bool ok = bad == 0; if (!ok) _fail++;
            Png.Write(Path.Combine(Dir, $"grab-{(add ? "add" : "lerp")}.png"), GW, GH, got);
            Log.Info($"[rendertest] grab {(add ? "added (One One)" : "lerp (SrcAlpha)")}: 25 BlendModes modes vs CPU {(ok ? "PASS" : $"FAIL ({bad} modes off, worst {ShaderPolicy.ModeName(worst)} {worstPct:F1} %)")} ; {g.Stats}");
        }
        g.DestroyTexture(white);
    }
    /// <summary>The blend-mode formulas of blend.frag in double precision (the reference for GrabChecks).</summary>
    static (double, double, double) CpuBlend(int m, (double r, double g, double b) B, (double r, double g, double b) S)
    {
        static double Lum((double r, double g, double b) c) => 0.3 * c.r + 0.59 * c.g + 0.11 * c.b;
        static (double, double, double) Clip((double r, double g, double b) c)
        {
            double l = Lum(c), n = Math.Min(Math.Min(c.r, c.g), c.b), x = Math.Max(Math.Max(c.r, c.g), c.b);
            if (n < 0) c = (l + (c.r - l) * l / Math.Max(l - n, 1e-5), l + (c.g - l) * l / Math.Max(l - n, 1e-5), l + (c.b - l) * l / Math.Max(l - n, 1e-5));
            if (x > 1) c = (l + (c.r - l) * (1 - l) / Math.Max(x - l, 1e-5), l + (c.g - l) * (1 - l) / Math.Max(x - l, 1e-5), l + (c.b - l) * (1 - l) / Math.Max(x - l, 1e-5));
            return c;
        }
        static (double, double, double) SetLum((double r, double g, double b) c, double l) { double d = l - Lum(c); return Clip((c.r + d, c.g + d, c.b + d)); }
        static double Sat((double r, double g, double b) c) => Math.Max(Math.Max(c.r, c.g), c.b) - Math.Min(Math.Min(c.r, c.g), c.b);
        static (double, double, double) SetSat((double r, double g, double b) c, double s)
        {
            double mx = Math.Max(Math.Max(c.r, c.g), c.b), mn = Math.Min(Math.Min(c.r, c.g), c.b);
            return mx > mn ? ((c.r - mn) * s / (mx - mn), (c.g - mn) * s / (mx - mn), (c.b - mn) * s / (mx - mn)) : (0, 0, 0);
        }
        static double Burn(double b, double s) => b >= 1 ? 1 : s <= 0 ? 0 : 1 - Math.Min(1, (1 - b) / s);
        static double Dodge(double b, double s) => b <= 0 ? 0 : s >= 1 ? 1 : Math.Min(1, b / (1 - s));
        static double Hard(double b, double s) => s <= 0.5 ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s);
        static double Soft(double b, double s) { double d = b <= 0.25 ? ((16 * b - 12) * b + 4) * b : Math.Sqrt(b); return s <= 0.5 ? b - (1 - 2 * s) * b * (1 - b) : b + (2 * s - 1) * (d - b); }
        static double Vivid(double b, double s) => s <= 0.5 ? Burn(b, 2 * s) : Dodge(b, 2 * (s - 0.5));
        static double Pin(double b, double s) => s <= 0.5 ? Math.Min(b, 2 * s) : Math.Max(b, 2 * (s - 0.5));
        static double Div(double b, double s) => s <= 0 ? (b <= 0 ? 0 : 1) : Math.Min(1, b / s);
        (double, double, double) P(Func<double, double, double> f) => (f(B.r, S.r), f(B.g, S.g), f(B.b, S.b));
        return m switch
        {
            0 => P(Math.Min), 1 => P((b, s) => b * s), 2 => P(Burn), 3 => P((b, s) => Math.Max(b + s - 1, 0)), 4 => Lum(S) < Lum(B) ? S : B,
            5 => P(Math.Max), 6 => P((b, s) => b + s - b * s), 7 => P(Dodge), 8 => P((b, s) => Math.Min(b + s, 1)), 9 => Lum(S) > Lum(B) ? S : B,
            10 => P((b, s) => Hard(s, b)), 11 => P(Soft), 12 => P(Hard), 13 => P(Vivid), 14 => P((b, s) => Math.Clamp(b + 2 * s - 1, 0, 1)),
            15 => P(Pin), 16 => P((b, s) => b + s >= 1 ? 1 : 0), 17 => P((b, s) => Math.Abs(b - s)), 18 => P((b, s) => b + s - 2 * b * s), 19 => P((b, s) => Math.Max(b - s, 0)),
            20 => P(Div), 21 => SetLum(SetSat(S, Sat(B)), Lum(B)), 22 => SetLum(SetSat(B, Sat(S)), Lum(B)), 23 => SetLum(S, Lum(B)), _ => SetLum(B, Lum(S)),
        };
    }

    // ---------------- (3) cost ----------------
    static void StylizerCost(GlBackend g)
    {
        var sty = DerivedRender.Stylizer;
        if (sty == null || !GlBackend.HasSource("stylizer.frag")) { Log.Info($"[rendertest] GPU Stylizer pass {g.W}x{g.H}: owner-local Stylizer not present -- SKIPPED"); return; }
        var l1 = LutTex(8, 7); var l2 = LutTex(8, 8); var pat = Pattern(8);
        var h1 = g.CreateTexture(256, l1.Height, l1.Rgba, false); var h2 = g.CreateTexture(256, l2.Height, l2.Rgba, false);
        var map = sty.LayerMap(pat.Rgba, 8, 8, 8, 8); var hm = g.CreateTexture(8, 8, map, false);
        var ft = g.CreateTexture(g.W, g.H, Frame(g.W, g.H, 3), false);
        foreach (bool two in new[] { false, true })
        {
            double best = double.MaxValue, sum = 0; const int N = 30;
            for (int k = 0; k < N + 3; k++)
            {
                g.Begin(0, 0, 0, 1); Quad(g, ft, BLENDMODE_NONE, 0, 0, g.W, g.H); g.Finish();
                long t0 = Stopwatch.GetTimestamp();
                g.RunStylizer(new GlBackend.StylizeArgs { Lut1 = h1, Lut1W = 256, Lut1H = l1.Height, Lut2 = two ? h2 : IntPtr.Zero, Lut2W = 256, Lut2H = l2.Height, PaletteLerp = 0.5f, Layers = hm, LayersW = 8, LayersH = 8, Block = 1, Intensity = 1 });
                g.Finish();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (k >= 3) { sum += ms; best = Math.Min(best, ms); }
                g.EndFrame();
            }
            Log.Info($"[rendertest] GPU Stylizer pass {g.W}x{g.H} {(two ? "2 palettes" : "1 palette ")}: mean {sum / N:F3} ms, best {best:F3} ms (this Mac's GPU, incl. glFinish round trip)");
        }
    }
}
