using System.Diagnostics;
using Shepherd.Engine.Runtime;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>Device kit #7, track measure: the PERFORMANCE OVERLAY -- a few lines drawn small in the top-left corner over the game, so the
/// owner sees a scene's numbers while testing: frames per second, the frame split (main core = the frame minus its waits for the render
/// thread and, where the main thread swaps, the swap; render thread = its busy time per frame without the swap; GPU = the swap's wait:
/// vitaGL's swap returns when the GPU and the display are ready -- see Refresh for where the swap runs), free memory (the Vita's free
/// user memory and the GC heap), the GC's pause per frame, and the boot numbers (time to splash, total boot, work left after the splash). Refreshed every 0.5 s of wall time (also on the frame clock: the numbers are the
/// device's real ones); one draw call of
/// ~150 quads from the engine's own 5x7 font (made here, not a game asset).
/// On / off: the "Performance Overlay" row of Settings > Graphics > PS Vita Settings (Runtime/VitaToggles.cs; saved in vita-settings.txt;
/// off by default), or <c>SHEPHERD_K7_OVERLAY=1</c> / <c>=0</c> in the env file (forces it). Off: SceneRenderer makes no call (kit #6's
/// frames). GL backend only (the headless apps draw nothing).</summary>
[ProfileOnly]
public static class K7Overlay
{
    static readonly string Forced = Diag.K7.Master ? Environment.GetEnvironmentVariable("SHEPHERD_K7_OVERLAY") : "0";
    static bool _choice;
    /// <summary>Drawn this frame (the row's choice unless the env file forces it).</summary>
    public static bool On;
#if SHEPHERD_EBOOT
    static void Decide() => On = Diag.K7.Master && Backends.Kind == "gl" && (Forced == "1" || (Forced != "0" && _choice));
#else
    // kit #11 measure (Diag/K11Heat.cs, SHEPHERD_K11_HEAT=...,overlay; Mac / tools builds): the heat map's tiles are drawn from here too
    static bool _k7;
    static void Decide() { _k7 = Diag.K7.Master && Backends.Kind == "gl" && (Forced == "1" || (Forced != "0" && _choice)); On = _k7 || (Diag.K11Heat.Overlay && Backends.Kind == "gl"); }
#endif
    static K7Overlay() => Decide();
    /// <summary>The row (and vita-settings.txt at boot): the owner's choice.</summary>
    public static void SetChoice(bool on) { _choice = on; Decide(); }

    // the font: 5 x 7 glyphs, one byte per row (bit 4 = left), made for this overlay
    const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ./-:%";
    static readonly byte[] Font = {
        0x0E,0x11,0x13,0x15,0x19,0x11,0x0E, 0x04,0x0C,0x04,0x04,0x04,0x04,0x0E, 0x0E,0x11,0x01,0x02,0x04,0x08,0x1F, 0x1F,0x02,0x04,0x02,0x01,0x11,0x0E,
        0x02,0x06,0x0A,0x12,0x1F,0x02,0x02, 0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E, 0x06,0x08,0x10,0x1E,0x11,0x11,0x0E, 0x1F,0x01,0x02,0x04,0x08,0x08,0x08,
        0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E, 0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C,
        0x0E,0x11,0x11,0x1F,0x11,0x11,0x11, 0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E, 0x0E,0x11,0x10,0x10,0x10,0x11,0x0E, 0x1C,0x12,0x11,0x11,0x11,0x12,0x1C,
        0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F, 0x1F,0x10,0x10,0x1E,0x10,0x10,0x10, 0x0E,0x11,0x10,0x17,0x11,0x11,0x0F, 0x11,0x11,0x11,0x1F,0x11,0x11,0x11,
        0x0E,0x04,0x04,0x04,0x04,0x04,0x0E, 0x07,0x02,0x02,0x02,0x02,0x12,0x0C, 0x11,0x12,0x14,0x18,0x14,0x12,0x11, 0x10,0x10,0x10,0x10,0x10,0x10,0x1F,
        0x11,0x1B,0x15,0x15,0x11,0x11,0x11, 0x11,0x11,0x19,0x15,0x13,0x11,0x11, 0x0E,0x11,0x11,0x11,0x11,0x11,0x0E, 0x1E,0x11,0x11,0x1E,0x10,0x10,0x10,
        0x0E,0x11,0x11,0x11,0x15,0x12,0x0D, 0x1E,0x11,0x11,0x1E,0x14,0x12,0x11, 0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E, 0x1F,0x04,0x04,0x04,0x04,0x04,0x04,
        0x11,0x11,0x11,0x11,0x11,0x11,0x0E, 0x11,0x11,0x11,0x11,0x11,0x0A,0x04, 0x11,0x11,0x11,0x15,0x15,0x15,0x0A, 0x11,0x11,0x0A,0x04,0x0A,0x11,0x11,
        0x11,0x11,0x11,0x0A,0x04,0x04,0x04, 0x1F,0x01,0x02,0x04,0x08,0x10,0x1F,
        0x00,0x00,0x00,0x00,0x00,0x0C,0x0C, 0x00,0x01,0x02,0x04,0x08,0x10,0x00, 0x00,0x00,0x00,0x1F,0x00,0x00,0x00, 0x00,0x0C,0x0C,0x00,0x0C,0x0C,0x00,
        0x18,0x19,0x02,0x04,0x08,0x13,0x03 };
    const int AtlasW = 256, AtlasH = 8, Scale = 2, CellW = 6, Lines = 3;
    static IntPtr _tex;
    static readonly char[][] _text = { new char[64], new char[64], new char[64] };
    static readonly int[] _len = new int[Lines];
    static Vertex[] _v = new Vertex[4 * 200]; static int[] _i = new int[6 * 200]; static int _nq;
    static double _next; static int _f0; static double _t0;
    static long _rt0, _wait0, _prs0, _api0, _frame0Ticks; static double _gc0;

    static IntPtr Atlas(IRenderBackend g)
    {
        var px = new byte[AtlasW * AtlasH * 4];
        for (int c = 0; c < Chars.Length; c++)
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 5; x++)
                    if ((Font[c * 7 + y] & (0x10 >> x)) != 0) { int o = (y * AtlasW + c * CellW + x) * 4; px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 255; }
        for (int y = 0; y < AtlasH; y++) for (int x = AtlasW - 6; x < AtlasW; x++) { int o = (y * AtlasW + x) * 4; px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 255; }   // a white block: the backdrop
        return g.CreateTexture(AtlasW, AtlasH, px, false, false, true);
    }

    /// <summary>SceneRenderer.Frame, after the UI canvases (the main target, no clip): the overlay's draw (its text refreshed every 0.5 s).</summary>
    public static void Draw(IRenderBackend g)
    {
#if !SHEPHERD_EBOOT
        if (Diag.K11Heat.Overlay) { Diag.K11Heat.DrawOverlay(g); if (!_k7) return; }
#endif
        Diag.K7Prof.Push(Diag.K7Prof.ZOverlay);
        long t0 = Stopwatch.GetTimestamp();
        if (_tex == IntPtr.Zero) _tex = Atlas(g);
        double now = t0 / (double)Stopwatch.Frequency;   // (the wall clock: the frame clock's realtime is virtual)
        bool refresh = now >= _next;
        if (refresh) { Refresh(now); Build(); _next = now + 0.5; }
        if (_nq > 0) g.Draw(_tex, BLENDMODE_BLEND, false, _v.AsSpan(0, 4 * _nq), _i.AsSpan(0, 6 * _nq));
        int us = (int)((Stopwatch.GetTimestamp() - t0) * 1000000 / Stopwatch.Frequency);   // its own cost, for the end line (no allocation)
        if (refresh) { _nRef++; _refUs += us; } else { _drawUs[_nDraw & 1023] = us; _nDraw++; }
        Diag.K7Prof.Pop();
    }
    static readonly int[] _drawUs = new int[1024]; static int _nDraw, _nRef, _mode; static long _refUs;   // _mode at the last refresh: 0 single-threaded, 1 MT main submits, 2 MT render thread submits (the pipeline is gone at Summary)
    /// <summary>Boot.Run's end (K7.End): the overlay's own cost -- the median of its plain frames (the draw) and the mean of its refresh
    /// frames (every 0.5 s: the numbers, the text, the quads) -- the roadmap's "costs &lt; 0.3 ms" check, read on the device.</summary>
    public static void Summary()
    {
        if (_nDraw + _nRef == 0) return;
        int n = Math.Min(_nDraw, 1024); var a = new int[n]; Array.Copy(_drawUs, a, n); Array.Sort(a);
        Log.Info("[k7] performance overlay: " + (_nDraw + _nRef) + " frames drawn; its own cost per frame: median " + (n > 0 ? a[n / 2] : 0) + " us (the draw; the last " + n +
                 " plain frames), refresh frames (every 0.5 s) " + (_nRef > 0 ? _refUs / _nRef : 0) + " us each on average (" + _nRef + "); target < 300 us on the device; the last numbers shown: '" +
                 new string(_text[0], 0, _len[0]) + "' (" + (_mode == 0 ? "single-threaded render" : _mode == 2 ? "MT, the render thread submits + swaps" : "MT, the main thread submits + swaps") + ")");
    }

    // ---------------------------------------------------------------- the numbers
    static void Refresh(double now)
    {
        int frames = Time.FrameCount - _f0; double dt = now - _t0;
        var mt = MtRender.Cur; var gl = MtRender.Inner(SceneRenderer.G) as GlBackend;
        long rt = mt?.WorkerTicksK7 ?? 0, wait = mt?.WaitTicksK7 ?? 0, api = mt?.ApiTicksK7 ?? 0, prs = gl?.PresentTicks ?? 0, ft = Diag.K7.FrameTicksSum;
        double gc = Diag.SimStats.GcMsSum;
        if (_f0 > 0 && frames > 0)
        {
            double f = frames, k = 1000.0 / Stopwatch.Frequency;
            double frameMs = (ft - _frame0Ticks) * k / f, rtMs = (rt - _rt0) * k / f, waitMs = (wait - _wait0) * k / f, prsMs = (prs - _prs0) * k / f, apiMs = (api - _api0) * k / f;
            // review fix (kit #7 measure): where the swap runs decides what it is inside. Single-threaded, and the MT pipeline with the main
            // thread submitting (SDL / GL on the Mac): the swap is main-thread time outside its waits -> CPU = frame - waits - swap, RT = the
            // render thread's builds. The MT pipeline with the render thread submitting (GL on the shepgl host: the Vita's 00098 / 00095,
            // MtRender.Wrap's default) records Present and the render thread runs it inside its frame job: the swap is already inside the
            // render thread's busy time and inside the main thread's wait for it -> CPU = frame - waits (subtracting the swap again showed a
            // GPU-bound picture: e.g. main 30 ms busy, render thread 20 + 18 ms swap read CPU ~12), RT = its busy time minus the swap (the
            // GPU column). A sync point (texture uploads during a load, a Screenshot, game code that needs the backend now) joins the frame
            // job (counted in the waits) and then has the render thread run the pending commands -- a recorded Present among them when the
            // sync point comes before the next frame's end -- outside a job while the main thread waits in MtBackend.OnApi: that wait is
            // not main-thread work either and the call is render-thread time -> CPU = frame - waits - OnApi waits, RT = job time + OnApi
            // time - the swaps, wherever each swap ran. (VLX 00098 with VLX_SWAP_DELAY_US=15000 checked against its Chrome trace: CPU 20.6 /
            // RT 20.5 / GPU 18.9 vs the trace's frame - waits 20.2, jobs 40.4 ms; the old formula read CPU ~0-2.)
            bool worker = mt != null && mt.WorkerSubmits; _mode = mt == null ? 0 : worker ? 2 : 1;
            double cpuMs = worker ? frameMs - waitMs - apiMs : frameMs - waitMs - prsMs, rtShown = worker ? rtMs + apiMs - prsMs : rtMs;
            int p = 0; var t = _text[0];
            Num(t, ref p, frames / Math.Max(1e-6, dt), 1); Put(t, ref p, " FPS  CPU "); Num(t, ref p, Math.Max(0, cpuMs), 1);
            Put(t, ref p, " RT "); Num(t, ref p, Math.Max(0, rtShown), 1); Put(t, ref p, " GPU "); Num(t, ref p, prsMs, 1); Put(t, ref p, " MS"); _len[0] = p;
            p = 0; t = _text[1];
            int free = Diag.VitaHost.On ? Diag.K7.Host(2, 0) : -1;
            if (free >= 0) { Put(t, ref p, "FREE "); Num(t, ref p, free / 1024.0, 1); Put(t, ref p, "M  "); }
            // (the heap sgen measured at its last collection -- GC.GetGCMemoryInfo reads the struct sgen fills, no lock -- not
            //  GC.GetTotalMemory, which walks every major-heap block under the GC lock: VLX 8 ms per refresh with it)
            Put(t, ref p, "HEAP "); Num(t, ref p, GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0, 1); Put(t, ref p, "M  GC "); Num(t, ref p, (gc - _gc0) / f, 1); Put(t, ref p, " MS/F");
            // kit #8 play: the ARM clock as MEASURED (kit #7 extras X2: PSVshellPlus ran 497 MHz while the system said 444); the system's report
            // until a measurement settled, and wherever nothing is measured (SHEPHERD_K8_CLK_MEASURED=0: no clock here, kit #7's line)
            int mhz = K8Play.ClockMhz(); if (mhz > 0) { Put(t, ref p, "  CPU "); Num(t, ref p, mhz, 0); Put(t, ref p, "MHZ"); }
            _len[1] = p;
        }
        {
            int p = 0; var t = _text[2];
            if (Diag.K7.TotalMs >= 0)
            {
                Put(t, ref p, "BOOT SPLASH "); if (Diag.K7.TimeToSplashMs >= 0) Num(t, ref p, Diag.K7.TimeToSplashMs / 1000, 1); else Put(t, ref p, "-");
                Put(t, ref p, "S TOTAL "); Num(t, ref p, Diag.K7.TotalMs / 1000, 1); Put(t, ref p, "S LEFT ");
                if (Diag.K7.WorkLeftMs >= 0) Num(t, ref p, Diag.K7.WorkLeftMs / 1000, 1); else Put(t, ref p, "-"); Put(t, ref p, "S");
            }
            _len[2] = p;
        }
        _f0 = Time.FrameCount; _t0 = now; _rt0 = rt; _wait0 = wait; _prs0 = prs; _api0 = api; _frame0Ticks = ft; _gc0 = gc;
    }
    static void Put(char[] t, ref int p, string s) { for (int i = 0; i < s.Length && p < t.Length; i++) t[p++] = s[i]; }
    /// <summary>v with <paramref name="dec"/> decimals (0..1), no allocation.</summary>
    static void Num(char[] t, ref int p, double v, int dec)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) { Put(t, ref p, "-"); return; }
        if (v < 0) { Put(t, ref p, "-"); v = -v; }
        long x = (long)Math.Round(v * (dec == 1 ? 10 : 1));
        long ip = dec == 1 ? x / 10 : x; int start = p;
        do { if (p < t.Length) t[p++] = (char)('0' + ip % 10); ip /= 10; } while (ip > 0);
        Array.Reverse(t, start, p - start);
        if (dec == 1 && p + 1 < t.Length) { t[p++] = '.'; t[p++] = (char)('0' + x % 10); }
    }

    // ---------------------------------------------------------------- the quads
    static void Build()
    {
        _nq = 0;
        int maxLen = 0; for (int l = 0; l < Lines; l++) maxLen = Math.Max(maxLen, _len[l]);
        if (maxLen == 0) return;
        float x0 = 8, y0 = 8, lh = 9 * Scale, cw = CellW * Scale;
        Quad(x0 - 4, y0 - 4, x0 + maxLen * cw + 2, y0 + Lines * lh + 2, AtlasW - 3.5f, 3.5f, AtlasW - 2.5f, 4.5f, 0f, 0f, 0f, 0.55f);   // the backdrop
        for (int l = 0; l < Lines; l++)
            for (int k = 0; k < _len[l]; k++)
            {
                int c = Chars.IndexOf(_text[l][k]); if (c < 0) continue;
                float x = x0 + k * cw, y = y0 + l * lh;
                Quad(x, y, x + 5 * Scale, y + 7 * Scale, c * CellW, 0, c * CellW + 5, 7, 0.85f, 1f, 0.55f, 1f);
            }
    }
    static void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, float r, float g, float b, float a)
    {
        if (_nq >= _v.Length / 4) return;
        int o = 4 * _nq; float su = 1f / AtlasW, sv = 1f / AtlasH;
        var col = new FColor { r = r, g = g, b = b, a = a };
        _v[o] = new Vertex { position = new FPoint { x = x0, y = y0 }, color = col, tex_coord = new FPoint { x = u0 * su, y = v0 * sv } };
        _v[o + 1] = new Vertex { position = new FPoint { x = x1, y = y0 }, color = col, tex_coord = new FPoint { x = u1 * su, y = v0 * sv } };
        _v[o + 2] = new Vertex { position = new FPoint { x = x1, y = y1 }, color = col, tex_coord = new FPoint { x = u1 * su, y = v1 * sv } };
        _v[o + 3] = new Vertex { position = new FPoint { x = x0, y = y1 }, color = col, tex_coord = new FPoint { x = u0 * su, y = v1 * sv } };
        int j = 6 * _nq; _i[j] = o; _i[j + 1] = o + 1; _i[j + 2] = o + 2; _i[j + 3] = o; _i[j + 4] = o + 2; _i[j + 5] = o + 3;
        _nq++;
    }
}
