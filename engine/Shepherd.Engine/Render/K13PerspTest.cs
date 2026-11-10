using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>Kit #13 looks: <c>k13persptest</c> (Mac / tools builds only) -- the perspective row's gate without the game. The Main Menu pentagram's
/// quad exactly as the engine projects it (pixel x / y, clip w and uv of its four vertices: SHEPHERD_K13_WDUMP at frame 1300 of the menu route),
/// textured with a synthetic disc of radius 0.35 (in uv, around the centre), is drawn headless at 960x544 through the GL backend twice: with
/// its w (IK13Persp.DrawW: the PERSP program) and without (Draw: kit #12's affine mapping). The disc's pixel extents are read back and compared
/// with the simulation of both mappings (computed here, = vita/notes/kit13-prep-findings.md section 1: perspective-correct 246 px wide, centre
/// -4, rows 405..496; affine 294 px, -30, 416..513), within 1 px. The affine draw is the negative control: it must miss the perspective answer
/// (and hit its own). Also the lit variant (DrawAddW, a zero added colour) and the folded-stream call (DrawEnc). Exit 0 = PASS.</summary>
[ProfileOnly]
public static class K13PerspTest
{
#if !SHEPHERD_EBOOT
    public static bool Handles(string mode) => mode == "k13persptest";
    const int W = 960, H = 544, T = 256; const float R = 0.35f;
    static readonly (float x, float y, float w, float u, float v)[] Q = { (602.055f, 394.812f, 31.26666f, 0, 0), (351.89f, 394.812f, 31.26666f, 1, 0), (206.842f, 533.862f, 14.66394f, 1, 1), (740.248f, 533.862f, 14.66394f, 0, 1) };

    public static int Run()
    {
        int fails = 0;
        var tex = new byte[T * T * 4];
        for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float u = (x + 0.5f) / T - 0.5f, v = (y + 0.5f) / T - 0.5f; bool inside = u * u + v * v <= R * R; int o = (y * T + x) * 4;
                tex[o] = tex[o + 1] = tex[o + 2] = 255; tex[o + 3] = (byte)(inside ? 255 : 0);
            }
        var simP = Sim(true); var simA = Sim(false);
        Log.Info($"[k13persptest] simulation: perspective-correct {Fmt(simP)}; affine {Fmt(simA)}");
        foreach (var (name, mode) in new[] { ("DrawW (perspective)", 1), ("DrawAddW (lit, perspective)", 2), ("DrawEnc (multicore replay)", 3), ("Draw (kit #12, negative control)", 0) })
        {
            using var gl = new GlBackend(W, H, headless: true);
            var th = gl.CreateTexture(T, T, tex, true);
            gl.Begin(0, 0, 0, 1);
            var v = new Vertex[4]; var w = new float[4];
            for (int i = 0; i < 4; i++) { v[i].position.x = Q[i].x; v[i].position.y = Q[i].y; v[i].color.r = v[i].color.g = v[i].color.b = v[i].color.a = 1f; v[i].tex_coord.x = Q[i].u; v[i].tex_coord.y = 1f - Q[i].v; w[i] = Q[i].w; }
            var idx = new[] { 0, 1, 2, 2, 3, 0 };
            var add = new FColor[4];
            switch (mode)
            {
                case 1: gl.DrawW(th, BLENDMODE_BLEND, v, idx, w); break;
                case 2: gl.DrawAddW(th, BLENDMODE_BLEND, v, idx, add, w); break;
                case 3: { var enc = new FColor[4]; K13Looks.EncW(w, enc); gl.DrawEnc(th, BLENDMODE_BLEND, v, idx, enc, false); break; }
                default: gl.Draw(th, BLENDMODE_BLEND, false, v, idx); break;
            }
            var px = gl.ReadPixels(out int rw, out int rh);
            var m = Measure(px, rw, rh);
            var want = mode == 0 ? simA : simP;
            bool hit = Near(m, want), hitP = Near(m, simP);
            bool ok = mode == 0 ? hit && !hitP : hit;   // (the control: its own mapping, and NOT the perspective answer)
            if (!ok) fails++;
            Log.Info($"[k13persptest] {name,-34} {(ok ? "PASS" : "FAIL")}: measured {Fmt(m)} (expected {Fmt(want)}){(mode == 0 ? $"; the perspective answer {(hitP ? "MATCHED (the control failed)" : "missed, as it must")}" : "")}; perspective draws {K13Looks.PerspDraws}, fallbacks {K13Looks.PerspFallback}");
            if (Environment.GetEnvironmentVariable("SHEPHERD_K13_PERSPTEST_PNG") is { Length: > 0 } dir) { Directory.CreateDirectory(dir); Png.Write(Path.Combine(dir, $"persptest-{mode}.png"), rw, rh, px); }
        }
        Log.Info($"[k13persptest] RESULT: {(fails == 0 ? "PASS" : $"FAIL ({fails})")}");
        return fails == 0 ? 0 : 1;
    }
    static string Fmt((float wd, float c, float y0, float y1) m) => $"width {m.wd:0} px, centre {m.c:+0;-0}, rows {m.y0:0}..{m.y1:0}";
    static bool Near((float wd, float c, float y0, float y1) a, (float wd, float c, float y0, float y1) b) => Math.Abs(a.wd - b.wd) <= 1.5f && Math.Abs(a.c - b.c) <= 1f && Math.Abs(a.y0 - b.y0) <= 1.5f && Math.Abs(a.y1 - b.y1) <= 1.5f;
    /// <summary>The disc's extents in a read-back frame (top-down RGBA): pixels with red &gt; 127; width = last - first column + 1, rows likewise.</summary>
    static (float, float, float, float) Measure(byte[] px, int w, int h)
    {
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (px[(y * w + x) * 4] > 127) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) return (0, 0, 0, 0);
        return (x1 - x0 + 1, (x0 + x1 + 1) * 0.5f - W / 2f, y0, y1 + 1);
    }
    /// <summary>The circle of radius R mapped through the two triangles (0,1,2) / (2,3,0): perspective-correct (uv/w, 1/w linear in screen space)
    /// or affine (uv linear in screen space). Extents as Measure reports them (width = max - min, rows min..max).</summary>
    static (float, float, float, float) Sim(bool persp)
    {
        double mnx = double.MaxValue, mxx = double.MinValue, mny = double.MaxValue, mxy = double.MinValue;
        for (int k = 0; k < 4000; k++)
        {
            double a = 2 * Math.PI * k / 4000, u = 0.5 + R * Math.Cos(a), vv = 0.5 + R * Math.Sin(a);
            foreach (var t in new[] { (0, 1, 2), (2, 3, 0) })
            {
                var p = Map(t.Item1, t.Item2, t.Item3, u, vv, persp); if (p == null) continue;
                mnx = Math.Min(mnx, p.Value.x); mxx = Math.Max(mxx, p.Value.x); mny = Math.Min(mny, p.Value.y); mxy = Math.Max(mxy, p.Value.y); break;
            }
        }
        return ((float)(mxx - mnx), (float)((mnx + mxx) / 2 - W / 2.0), (float)mny, (float)mxy);
    }
    static (double x, double y)? Map(int i0, int i1, int i2, double u, double v, bool persp)
    {
        var (a, b, c) = (Q[i0], Q[i1], Q[i2]);
        double wa = persp ? 1.0 / a.w : 1, wb = persp ? 1.0 / b.w : 1, wc = persp ? 1.0 / c.w : 1;
        // sum s_i wi (uv_i - uv) = 0, sum s_i = 1 (Cramer's rule)
        double m00 = (a.u - u) * wa, m01 = (b.u - u) * wb, m02 = (c.u - u) * wc, m10 = (a.v - v) * wa, m11 = (b.v - v) * wb, m12 = (c.v - v) * wc;
        double det = m00 * (m11 - m12) - m01 * (m10 - m12) + m02 * (m10 - m11);
        if (Math.Abs(det) < 1e-12) return null;
        double s0 = (m01 * m12 - m02 * m11) / det, s1 = (m02 * m10 - m00 * m12) / det, s2 = (m00 * m11 - m01 * m10) / det;
        if (s0 < -1e-9 || s1 < -1e-9 || s2 < -1e-9) return null;
        return (s0 * a.x + s1 * b.x + s2 * c.x, s0 * a.y + s1 * b.y + s2 * c.y);
    }
#else
    public static bool Handles(string mode) => false;
    public static int Run() => 1;
#endif
}
