using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #7, track measure (vita/notes/kit7-measure.md): the engine's half of the black box, the boot numbers, the test
/// hooks and the host bridge; the profiler is <see cref="K7Prof"/>, the overlay Render/K7Overlay.cs. Switches (env file):
/// <list type="bullet">
/// <item><c>SHEPHERD_K7=0</c>: the whole track off = kit #6 (the host reads the same switch: no block, no ring, no thread, no line).</item>
/// <item><c>SHEPHERD_K7_BOOT</c> (default on): the BOOT line every boot -- TIME TO SPLASH (process start -> the Devolver logo's first
/// frame: the eboot, Mono and the engine start with nothing on screen), the splash's own time, TOTAL boot (process start -> the Main
/// Menu scene, the logo included) and WORK LEFT AFTER THE SPLASH (the real time from the logo's end to the Main Menu beyond the game
/// time its own sequence spends there: 0 = the boot is as fast as the original's own splash allows); in engine.log, the host log (K7
/// BOOT) and boot-history.tsv (one row per boot). <c>=0</c>: none.</item>
/// <item><c>SHEPHERD_K7_BLACKBOX</c> (default on): each frame's wall time, GC pause and allocation into the host's black-box ring
/// (vita/shepherd/k7_measure.h writes blackbox*.txt); <c>=0</c>: none.</item>
/// <item><c>SHEPHERD_K7_BB_TEST=crash@N|hang@N|exc@N|fail@N</c> (tests only): at frame N a real crash (host), a 150-s hang of the main
/// thread (the watchdog's STALL?), an unhandled exception (the host's red END) or an engine FAIL line (FAIL END).</item>
/// </list>
/// Nothing here changes a frame: it reads clocks and counters, and writes only to the host's blocks and to log files.</summary>
[ProfileOnly]
public static unsafe class K7
{
    static string Env(string k) => Environment.GetEnvironmentVariable(k);
    public static readonly bool Master = Env("SHEPHERD_K7") != "0";
    public static readonly bool BootOn = Master && Env("SHEPHERD_K7_BOOT") != "0";
    public static readonly bool BlackBox = Master && Env("SHEPHERD_K7_BLACKBOX") != "0";
    /// <summary>SHEPHERD_K7_CULL=off (timing tests only, never a default): SceneRenderer.DrawWorld skips the per-renderer frustum test of
    /// sprites and meshes (they are drawn and the GPU clips them; SpriteShapes keep theirs: it decides OnWillRenderObject). Pixels are the
    /// same, the draw stream is not.</summary>
    public static readonly bool CullOff = Master && Env("SHEPHERD_K7_CULL") == "off";
    /// <summary>SHEPHERD_K7_OVERLAY_ROW (default on): the "Performance Overlay" row in Settings > Graphics > PS Vita Settings (GL apps).</summary>
    public static readonly bool OverlayRow = Master && Env("SHEPHERD_K7_OVERLAY_ROW") != "0";

    static int* _bb;               // the host's black-box block (k7_measure.h: [1] head, [3] last frame, [8..] 256 x frame, us, gc us, alloc KiB)
    static int* _bbMac;            // (the Mac: our own, dumped at an unhandled exception)
    static long _f0, _a0, _main0;
    static int _test, _testFrame = -1;
    static bool _init;

    /// <summary>Program.Main, first thing: the Mac's boot clock (the Vita's is the process time).</summary>
    public static void MainEntered() { if (Master) _main0 = Stopwatch.GetTimestamp(); }
    /// <summary>Milliseconds since the process started (the Vita: Stopwatch = the process time in ns, pal_vita.c) or, elsewhere, since Main.</summary>
    public static double Ms(long ts) => (VitaHost.On ? ts : ts - _main0) * 1000.0 / Stopwatch.Frequency;

    static void Init()
    {
        _init = true;
        if (VitaHost.On)
        {
            if (BlackBox) _bb = (int*)HostPtr(0);
            if (BootOn) _hostMainMs = Host(4, 0);
        }
        else if (BlackBox)
        {
            _bbMac = (int*)Marshal.AllocHGlobal(4 * (8 + 4 * 256)); new Span<int>(_bbMac, 8 + 4 * 256).Clear(); _bbMac[2] = 256; _bb = _bbMac;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => MacDump("an unhandled " + e.ExceptionObject?.GetType().Name);
        }
        var t = Env("SHEPHERD_K7_BB_TEST");
        if (Master && t is { Length: > 0 } && t.IndexOf('@') is var at && at > 0 && int.TryParse(t.Substring(at + 1), out var f))
        {
            var k = t.Substring(0, at);
            _test = k == "crash" ? 1 : k == "hang" ? 2 : k == "exc" ? 3 : k == "fail" ? 4 : 0; _testFrame = f;
            Log.Info("[k7] black-box test armed: " + t + (_test == 0 ? " (unknown kind: crash|hang|exc|fail)" : ""));
        }
    }

    /// <summary>Boot loop, first thing of every frame: the previous frame into the black box, the boot numbers, the profiler's frame.</summary>
    public static void FrameBegin(int frame)
    {
        if (!Master) return;
        if (!_init) Init();
        long now = Stopwatch.GetTimestamp(), a = GC.GetAllocatedBytesForCurrentThread();
        if (_bb != null)
        {
            if (_f0 != 0)
            {
                int i = 8 + 4 * (_bb[1] & 255);
                _bb[i] = frame - 1; _bb[i + 1] = (int)((now - _f0) * 1000000 / Stopwatch.Frequency); _bb[i + 2] = (int)(SimStats.LastGcMs * 1000); _bb[i + 3] = (int)((a - _a0) >> 10);
                _bb[1]++;
            }
            _bb[3] = frame;
        }
        if (_f0 != 0) FrameTicksSum += now - _f0;
        _f0 = now; _a0 = a;
        if (BootOn && !_menuDone) BootFrame(frame, now);
        if (K7Prof.On) K7Prof.FrameBegin(frame, now);
        if (frame == _testFrame) RunTest();
    }

    static void RunTest()
    {
        Log.Info("[k7] black-box test " + Env("SHEPHERD_K7_BB_TEST") + " now");
        switch (_test)
        {
            case 1: if (VitaHost.On) Host(90, 0); else Environment.FailFast("k7 test crash"); break;
            case 2: { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 150000) Thread.Sleep(200); Log.Info("[k7] black-box test: the 150-s hang is over"); break; }
            case 3: throw new InvalidOperationException("k7 black-box test: an unhandled exception (SHEPHERD_K7_BB_TEST=exc)");
            case 4: VitaHost.Fail("k7 black-box test (SHEPHERD_K7_BB_TEST=fail)"); if (!VitaHost.On) MacDump("the k7 fail test"); break;
        }
    }

    // ---------------------------------------------------------------- the Mac's black box (no host): frames only

    static void MacDump(string why)
    {
        try
        {
            if (_bbMac == null) return;
            var sb = new System.Text.StringBuilder("# Shepherd black box (kit #7, the Mac: frames only; the Vita host's has the log rings and memory): " + why + "\n-- frames: frame\twall_ms\tgc_pause_ms\talloc_kib\n");
            int head = _bbMac[1];
            for (int k = Math.Max(0, head - 256); k < head; k++) { int i = 8 + 4 * (k & 255); sb.Append('f').Append(_bbMac[i]).Append('\t').Append((_bbMac[i + 1] / 1000.0).ToString("F2")).Append('\t').Append((_bbMac[i + 2] / 1000.0).ToString("F2")).Append('\t').Append(_bbMac[i + 3]).Append('\n'); }
            File.WriteAllText(Path.Combine(Program.LogDir, "blackbox.txt"), sb.ToString());
        }
        catch (Exception) { }
    }

    // ---------------------------------------------------------------- boot numbers

    static int _hostMainMs = -1;
    static double _splash0 = -1, _splash1 = -1, _splashGt0, _splashGt1, _clip;
    static int _splashF0 = -1;
    static bool _menuDone;
    public static double AssetsUpMs = -1, Frame1Ms = -1;

    /// <summary>Icalls/VideoMiscIcalls.cs: the Devolver logo's clip started (its first frame presented) / reached its end.</summary>
    public static void Splash(string clip, bool start, double length)
    {
        if (!BootOn || _menuDone || clip == null || clip.IndexOf("Devolver", StringComparison.Ordinal) < 0) return;
        double ms = Ms(Stopwatch.GetTimestamp());
        if (start && _splash0 < 0) { _splash0 = ms; _splashGt0 = Time.UnscaledNow; _splashF0 = Time.FrameCount; _clip = length; }
        if (!start && _splash0 >= 0 && _splash1 < 0) { _splash1 = ms; _splashGt1 = Time.UnscaledNow; }
    }

    static void BootFrame(int frame, long now)
    {
        if (frame == 2) Frame1Ms = Ms(now);
        foreach (var s in Scenes.All.Values) if (s.Name == "Main Menu") { BootLine(frame - 1, Ms(now)); return; }
    }

    static string S(double ms) => (ms / 1000.0).ToString("F2");

    static void BootLine(int menuFrame, double menuMs)
    {
        _menuDone = true;
        bool seen = _splash0 >= 0, ended = _splash1 >= 0;
        double end = ended ? _splash1 : seen ? _splash0 : -1, gtEnd = ended ? _splashGt1 : _splashGt0;
        double after = end >= 0 ? menuMs - end : -1, afterGt = end >= 0 ? (Time.UnscaledNow - gtEnd) * 1000 : -1;
        double left = end >= 0 ? Math.Max(0, after - afterGt) : -1;
        string clock = Time.Wall ? "play clock" : "frame clock";
        TimeToSplashMs = seen ? _splash0 : -1; TotalMs = menuMs; WorkLeftMs = left;
        string line = "BOOT time to splash " + (seen ? S(_splash0) + " s (f" + _splashF0 + ")" : "n/a (no Devolver logo seen)") +
                      " | splash " + (ended ? S(_splash1 - _splash0) + " s (clip " + _clip.ToString("F2") + " s)" : seen ? "0 s (ended at once)" : "-") +
                      " | after the splash " + (end >= 0 ? S(after) + " s (game time " + S(afterGt) + " s)" : "-") +
                      " | WORK LEFT AFTER THE SPLASH " + (left >= 0 ? S(left) + " s" : "n/a") +
                      " | TOTAL " + S(menuMs) + " s (the Main Menu scene at f" + menuFrame + ")" +
                      " | phases: host main " + (_hostMainMs >= 0 ? S(_hostMainMs) : "?") + " s, engine Main " + (VitaHost.On && _main0 != 0 ? S(Ms(_main0)) : "0") +
                      " s, asset layer " + (AssetsUpMs >= 0 ? S(AssetsUpMs) : "?") + " s, frame 1 " + (Frame1Ms >= 0 ? S(Frame1Ms) : "?") + " s | " + clock +
                      (VitaHost.On ? ", times since the process started" : ", times since Main (no Vita host)");
        Log.Info("[k7] " + line);
        VitaHost.K7Line("K7 " + line);
        try
        {
            var p = Path.Combine(Program.LogDir, "boot-history.tsv");
            bool hdr = !File.Exists(p);
            File.AppendAllText(p, (hdr ? "utc\ttime_to_splash_s\tsplash_s\tclip_s\tafter_splash_s\tafter_game_s\twork_left_s\ttotal_s\tmenu_frame\tclock\n" : "") +
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\t" + (seen ? S(_splash0) : "") + "\t" + (ended ? S(_splash1 - _splash0) : seen ? "0" : "") + "\t" +
                (seen ? _clip.ToString("F2") : "") + "\t" + (end >= 0 ? S(after) : "") + "\t" + (end >= 0 ? S(afterGt) : "") + "\t" + (left >= 0 ? S(left) : "") + "\t" +
                S(menuMs) + "\t" + menuFrame + "\t" + (Time.Wall ? "wall" : "frame") + "\n");
        }
        catch (Exception e) { Log.Warn("[k7] boot-history.tsv not written: " + e.GetBaseException().Message); }
    }
    /// <summary>The frames' wall time so far, Stopwatch ticks (the overlay's frame split).</summary>
    public static long FrameTicksSum;
    /// <summary>The boot numbers once the Main Menu arrived (the overlay shows them), ms; -1 = not known.</summary>
    public static double TimeToSplashMs = -1, TotalMs = -1, WorkLeftMs = -1;

    /// <summary>Boot: the asset layer is up (a boot phase of the BOOT line).</summary>
    public static void AssetsUp() { if (BootOn) AssetsUpMs = Ms(Stopwatch.GetTimestamp()); }
    /// <summary>Boot.Run's end: the profiler's tables.</summary>
    public static void End() { if (K7Prof.On) K7Prof.End(); if (Render.K7Overlay.On) Render.K7Overlay.Summary(); }

    // ---------------------------------------------------------------- the host (vita/shepherd/k7_measure.h)

    [MethodImpl(MethodImplOptions.NoInlining)] internal static int Host(int op, int arg) => shepherd_vita_k7(op, arg);
    [MethodImpl(MethodImplOptions.NoInlining)] internal static IntPtr HostPtr(int which) => shepherd_vita_k7_ptr(which);
    [DllImport("__Internal")] static extern int shepherd_vita_k7(int op, int arg);
    [DllImport("__Internal")] static extern IntPtr shepherd_vita_k7_ptr(int which);
}
