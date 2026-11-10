#if !SHEPHERD_EBOOT
using Shepherd.Engine.Render;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #11, track measure: the GREEN HOLE CHECK (roadmap kit #11, "green hole check (debug)"; from Final Fantasy VII's field
/// tiles). A debug view for the reuse caches -- the image cache, dirty rectangles and the per-tile prefix cache: each region a cache is
/// about to redraw is first filled with pure green (an opaque overwrite), and every pixel still exactly (0, 255, 0) at the frame's end is
/// a hole: a pixel no redrawn draw covered. The caches call <see cref="BeforeRedraw"/> once per region, on the main target, before they
/// redraw it; at the end of the frame the region's pixels are read back (GL) and counted (per region and per 32 x 32 tile, the first frames
/// with holes saved as PNG with the holes marked).
/// <para><b>What it cannot see</b> (it is the quick visual view, not the gate): a hole that a kept translucent draw blends over, and any
/// region redrawn before EndWorldPass (the post pass -- the LUT grading, the Stylizer -- recolours it), are no longer pure green, so they
/// are not counted; a region filled in the world pass logs a warning once. The gate for a reuse cache is the image comparison: the heat
/// map's pixel mode writes each frame's image hash (k11-heat.tsv), and <c>k11_report.py heat-diff</c> compares the cache-off and cache-on
/// runs frame by frame (0 differing frames = 0 differing pixels).</para>
/// <para>No cache exists yet (kit #11): until one calls in, the check has nothing to fill. <c>SHEPHERD_K11_GREEN=selftest</c> runs the
/// tool on the GL backend at frame 2 with a synthetic scene: a cached image restored + a dirty rectangle redrawn with the right draws =
/// 0 green and 0 pixels differing from a full redraw; the negative control (the background and the moved draw left out) = green exactly
/// on the region's pixels that no kept draw covers, and the wrong pixels under kept translucent draws counted apart as the blind spot
/// (they differ from the full redraw, they are not green). The self-test draws over frame 2's image (test only).</para>
/// A real picture can contain pure green: the baseline is the same check with the cache off (no regions = nothing counted).
/// Switch: <c>SHEPHERD_K11_GREEN=1</c> (the check) or <c>selftest</c>; unset = off (<see cref="BeforeRedraw"/> returns at once, no draw).</summary>
[ProfileOnly]
public static class K11Green
{
    static readonly string Spec = K11Measure.Master ? (Environment.GetEnvironmentVariable("SHEPHERD_K11_GREEN") ?? "").Trim() : "";
    public static readonly bool On = Spec is "1" or "check" or "selftest";
    static readonly bool SelfTestOn = Spec == "selftest";
    public static string Describe() => On ? Spec : "off";
    const int T = 32;

    static readonly List<(int x, int y, int w, int h)> _regions = new();
    static IntPtr _white;
    static long _frames, _regionsTotal, _pxTotal, _greenTotal, _holeFrames, _worldRegions; static int _pngs;
    static bool _worldEnded, _warned;
    /// <summary>RenderCheck.BeginFrame: a new frame's world pass begins.</summary>
    public static void FrameBegin() => _worldEnded = false;
    /// <summary>RenderCheck's EndWorldPass mark (after the post pass): regions from here on are not recoloured.</summary>
    public static void WorldPassEnded() => _worldEnded = true;
    static StreamWriter _tsv;

    /// <summary>For a reuse cache: call before redrawing the main-target region (x, y, w, h). With the check on, the region is filled with
    /// pure green (and remembered for the end-of-frame check); off, nothing happens.</summary>
    public static void BeforeRedraw(IRenderBackend g, int x, int y, int w, int h)
    {
        if (!On || g == null || w <= 0 || h <= 0) return;
        if (!_worldEnded)
        {
            _worldRegions++;
            if (!_warned) { _warned = true; Log.Warn("[k11] green check: a region redrawn in the world pass -- the post pass recolours it, so its holes are not pure green at the frame's end and are NOT counted; gate with the image comparison (SHEPHERD_K11_HEAT=pixel + k11_report.py heat-diff)"); }
        }
        Fill(g, x, y, w, h, new FColor { r = 0, g = 1, b = 0, a = 1 });
        _regions.Add((x, y, w, h));
    }

    static void Fill(IRenderBackend g, float x, float y, float w, float h, FColor c)
    {
        if (_white == IntPtr.Zero) _white = g.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 }, false, false, true);
        var v = new Vertex[4]; var uv = new FPoint { x = 0.5f, y = 0.5f };
        v[0] = new Vertex { position = new FPoint { x = x, y = y }, color = c, tex_coord = uv };
        v[1] = new Vertex { position = new FPoint { x = x + w, y = y }, color = c, tex_coord = uv };
        v[2] = new Vertex { position = new FPoint { x = x + w, y = y + h }, color = c, tex_coord = uv };
        v[3] = new Vertex { position = new FPoint { x = x, y = y + h }, color = c, tex_coord = uv };
        g.Draw(_white, BLENDMODE_NONE, false, v, new[] { 0, 1, 2, 0, 2, 3 });
    }

    static bool IsGreen(byte[] px, int o) => px[o] == 0 && px[o + 1] == 255 && px[o + 2] == 0;

    /// <summary>The end of the frame (after its draws, before Present): count the green left in this frame's regions.</summary>
    public static void After(int frame, IRenderBackend g)
    {
        if (SelfTestOn && frame == 2) SelfTest(g);
        if (_regions.Count == 0) return;
        var px = g.ReadPixels(out int w, out int h);
        _frames++;
        if (px == null) { Log.Warn("[k11] green check: the backend has no pixels (GL only)"); _regions.Clear(); return; }
        int green = 0, pxIn = 0; var tiles = new SortedDictionary<int, int>();
        var seen = new HashSet<int>();
        foreach (var (rx, ry, rw, rh) in _regions)
            for (int y = Math.Max(0, ry); y < Math.Min(h, ry + rh); y++)
                for (int x = Math.Max(0, rx); x < Math.Min(w, rx + rw); x++)
                {
                    int p = y * w + x; if (!seen.Add(p)) continue;
                    pxIn++;
                    if (IsGreen(px, p * 4)) { green++; int t = (y / T) * ((w + T - 1) / T) + x / T; tiles[t] = (tiles.TryGetValue(t, out var n) ? n : 0) + 1; }
                }
        _regionsTotal += _regions.Count; _pxTotal += pxIn; _greenTotal += green;
        _tsv ??= new StreamWriter(K11Measure.OutFile("k11-green.tsv"), false) { AutoFlush = true };
        if (_tsv.BaseStream.Position == 0) _tsv.WriteLine("frame\tscene\tregions\tregion_px\tgreen_px\tholes_by_tile(tile:px)");
        _tsv.WriteLine($"{frame}\t{K11Measure.Scene}\t{_regions.Count}\t{pxIn}\t{green}\t{string.Join(" ", tiles.Take(40).Select(kv => $"{kv.Key}:{kv.Value}"))}");
        if (green > 0)
        {
            _holeFrames++;
            Log.Warn($"[k11] green check frame {frame}: {green} green px left in {_regions.Count} redrawn region(s) ({tiles.Count} tiles) -- pixels no redrawn draw covered (a draw the reuse proof left out; holes under translucent draws are not counted)");
            if (_pngs++ < 8) Mark(px, w, h, $"k11-green-f{frame:D5}.png");
        }
        _regions.Clear();
    }

    static void Mark(byte[] px, int w, int h, string name)
    {
        var img = (byte[])px.Clone();
        for (int p = 0; p < w * h; p++) { int o = p * 4; if (IsGreen(px, o)) { img[o] = 255; img[o + 1] = 0; img[o + 2] = 255; } else { img[o] = (byte)(img[o] / 3); img[o + 1] = (byte)(img[o + 1] / 3); img[o + 2] = (byte)(img[o + 2] / 3); } img[o + 3] = 255; }
        try { Render.Png.Write(K11Measure.OutFile(name), w, h, img); } catch (Exception e) { Log.Warn($"[k11] green png: {e.Message}"); }
    }

    // ---------------------------------------------------------------- the self-test (GL, frame 2; test only)
    static readonly (float x, float y, float w, float h, FColor c)[] Scene =
    {
        (60, 60, 300, 200, new FColor { r = 0.9f, g = 0.2f, b = 0.2f, a = 0.8f }), (280, 120, 260, 220, new FColor { r = 0.2f, g = 0.3f, b = 0.9f, a = 0.7f }),
        (520, 40, 200, 380, new FColor { r = 0.9f, g = 0.8f, b = 0.1f, a = 0.9f }), (120, 300, 420, 160, new FColor { r = 0.6f, g = 0.1f, b = 0.7f, a = 0.6f }),
        (700, 260, 200, 200, new FColor { r = 0.1f, g = 0.7f, b = 0.7f, a = 1.0f }),
    };
    static readonly FColor Bg = new() { r = 0.08f, g = 0.06f, b = 0.12f, a = 1 };
    const int Moved = 1; const float Dx = 70, Dy = 40;

    static void DrawScene(IRenderBackend g, bool moved, Func<int, bool> include = null, bool background = true)
    {
        if (background) Fill(g, 0, 0, g.W, g.H, Bg);
        for (int i = 0; i < Scene.Length; i++)
        {
            if (include != null && !include(i)) continue;
            var (x, y, w, h, c) = Scene[i]; if (moved && i == Moved) { x += Dx; y += Dy; }
            var v = new Vertex[4]; var uv = new FPoint { x = 0.5f, y = 0.5f };
            v[0] = new Vertex { position = new FPoint { x = x, y = y }, color = c, tex_coord = uv }; v[1] = new Vertex { position = new FPoint { x = x + w, y = y }, color = c, tex_coord = uv };
            v[2] = new Vertex { position = new FPoint { x = x + w, y = y + h }, color = c, tex_coord = uv }; v[3] = new Vertex { position = new FPoint { x = x, y = y + h }, color = c, tex_coord = uv };
            g.Draw(_white, BLENDMODE_BLEND, false, v, new[] { 0, 1, 2, 0, 2, 3 });
        }
    }

    static bool Overlaps(int i, (int x, int y, int w, int h) r, bool moved)
    {
        var (x, y, w, h, _) = Scene[i]; if (moved && i == Moved) { x += Dx; y += Dy; }
        return x < r.x + r.w && x + w > r.x && y < r.y + r.h && y + h > r.y;
    }

    static void SelfTest(IRenderBackend g)
    {
        if (g.ReadPixels(out _, out _) == null) { Log.Info("[k11] green self-test: SKIPPED (the backend has no pixels: GL only)"); return; }
        if (_white == IntPtr.Zero) _white = g.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 }, false, false, true);
        _worldEnded = true;   // (after the frame: nothing here is in the world pass)
        g.SetTarget(IntPtr.Zero); g.NoClip();
        // frame A (the cached image) and frame B (the reference: a full redraw with one rectangle moved)
        DrawScene(g, false); var a = g.ReadPixels(out int w, out int h);
        DrawScene(g, true); var b = g.ReadPixels(out _, out _);
        // the dirty rectangle: the moved draw's old + new bounds, whole pixels
        var (mx, my, mw, mh, _) = Scene[Moved];
        var dirty = ((int)mx, (int)my, (int)(mw + Dx), (int)(mh + Dy));
        var cached = g.CreateTexture(w, h, (byte[])a.Clone(), false, false, true);
        int greenOk, diffOk, greenBad, diffBad, expectBad = 0, blindBad = 0;
        // (1) the right redraw: the cached image, green over the dirty rectangle, then -- clipped to it -- the background and every draw over it
        Restore(g, cached, w, h); _regions.Clear(); BeforeRedraw(g, dirty.Item1, dirty.Item2, dirty.Item3, dirty.Item4);
        g.Clip(dirty.Item1, dirty.Item2, dirty.Item3, dirty.Item4); DrawScene(g, true, i => Overlaps(i, dirty, true)); g.NoClip();
        var c1 = g.ReadPixels(out _, out _); (greenOk, diffOk) = Count(c1, b, w, h, dirty);
        // (2) the negative control: the background and the moved draw left out of the redraw (a proof that missed them)
        Restore(g, cached, w, h); _regions.Clear(); BeforeRedraw(g, dirty.Item1, dirty.Item2, dirty.Item3, dirty.Item4);
        g.Clip(dirty.Item1, dirty.Item2, dirty.Item3, dirty.Item4); DrawScene(g, true, i => i != Moved && Overlaps(i, dirty, true), background: false); g.NoClip();
        var c2 = g.ReadPixels(out _, out _); (greenBad, diffBad) = Count(c2, b, w, h, dirty);
        for (int y = dirty.Item2; y < dirty.Item2 + dirty.Item4; y++)   // the expected holes: dirty pixels no kept draw covers
            for (int x = dirty.Item1; x < dirty.Item1 + dirty.Item3; x++)
            {
                bool cov = false;
                for (int i = 0; i < Scene.Length && !cov; i++) { if (i == Moved) continue; var (sx, sy, sw, sh, _) = Scene[i]; cov = x >= sx && x < sx + sw && y >= sy && y < sy + sh; }
                if (!cov) expectBad++;
                else { int o = (y * w + x) * 4; if (c2[o] != b[o] || c2[o + 1] != b[o + 1] || c2[o + 2] != b[o + 2]) blindBad++; }   // a wrong pixel a kept (translucent) draw blended over: not green
            }
        _regions.Clear();
        g.DestroyTexture(cached);
        // PASS = the tool does what it says: the right redraw leaves no green and no difference; the negative control's green is exactly the
        // uncovered holes; every other wrong pixel is under a kept translucent draw (the blind spot, counted -- the image comparison sees it)
        bool pass = greenOk == 0 && diffOk == 0 && greenBad > 0 && greenBad == expectBad && diffBad == greenBad + blindBad;
        Log.Info($"[k11] green self-test: {(pass ? "PASS" : "FAIL")} -- right redraw: {greenOk} green px, {diffOk} px differ from the full redraw; negative control (background + moved draw left out): {diffBad} px wrong, of them {greenBad} green (expected {expectBad}: no kept draw covers them) and {blindBad} NOT seen by the green test (under kept translucent draws: the image comparison catches them); dirty rectangle {dirty}");
        if (!pass) Mark(c2, w, h, "k11-green-selftest-fail.png");
    }

    static void Restore(IRenderBackend g, IntPtr tex, int w, int h)
    {
        var v = new Vertex[4]; var c = new FColor { r = 1, g = 1, b = 1, a = 1 };
        v[0] = new Vertex { position = new FPoint { x = 0, y = 0 }, color = c, tex_coord = new FPoint { x = 0, y = 0 } };
        v[1] = new Vertex { position = new FPoint { x = w, y = 0 }, color = c, tex_coord = new FPoint { x = 1, y = 0 } };
        v[2] = new Vertex { position = new FPoint { x = w, y = h }, color = c, tex_coord = new FPoint { x = 1, y = 1 } };
        v[3] = new Vertex { position = new FPoint { x = 0, y = h }, color = c, tex_coord = new FPoint { x = 0, y = 1 } };
        g.Draw(tex, BLENDMODE_NONE, false, v, new[] { 0, 1, 2, 0, 2, 3 });
    }

    static (int green, int diff) Count(byte[] img, byte[] reference, int w, int h, (int x, int y, int w, int h) r)
    {
        int green = 0, diff = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            if (img[o] != reference[o] || img[o + 1] != reference[o + 1] || img[o + 2] != reference[o + 2]) diff++;
            if (x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h && IsGreen(img, o)) green++;
        }
        return (green, diff);
    }

    public static void End()
    {
        if (!On) return;
        _tsv?.Dispose(); _tsv = null;
        Log.Info($"[k11] green check: {_frames} frame(s) with redrawn regions, {_regionsTotal} region(s) ({_worldRegions} in the world pass: not checkable here), {_pxTotal} px checked, {_greenTotal} green px left ({_holeFrames} frame(s) with holes)" +
                 (_frames == 0 ? " -- no reuse cache called in (none exists yet in kit #11): the self-test is the tool's proof" : ""));
    }
}
#endif
