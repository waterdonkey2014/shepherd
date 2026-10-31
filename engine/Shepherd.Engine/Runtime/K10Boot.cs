using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Render;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #10, track boot (vita/notes/kit10-boot.md): the engine's half of THE BOOT THAT ALWAYS SHOWS SOMETHING. The host
/// (vita/shepherd/k10_boot.inc.c) shows the game's own loading screen -- a recording of it as Shepherd draws it, made from the owner's own copy
/// -- from right after vitaGL's init, while the engine boots; vitaGL's flips are held off the display meanwhile, so the engine's own frames
/// (only what the host flips before the engine's first frame) are not shown. This class tells the host WHEN the game's own frames start --
/// the boot screen must end there, with the game's own loading-to-scene move (the pentagram and the rings go at once, then the game's frame):
/// <list type="bullet">
/// <item>THE GAME'S FIRST FRAME: <see cref="Tick"/>(1) runs after frame 1's update and BEFORE its present, so frame 1 and every frame after it
///   reach the display. Everything the game draws is the game's: level0's own black lead-in (its 2 s wait), the Massive Monster logo (a Spine
///   animation, frames 121-534 on the frame clock), the Devolver video (f535), the controller notice. (Kit #10 WIP 1-4 ended at the Devolver
///   video and so hid the Massive Monster logo, which the game plays BEFORE the video: LoadMainMenu.RunSplashScreens; the vlx GL records draw
///   it in f121-534 -- kit10-boot.md section 2.)</item>
/// <item>fallbacks (never first while the boot loop runs): the game's first VideoPlayer clip starts playing (Icalls/VideoMiscIcalls.cs, before
///   Runtime/K8Video.cs opens it), the first scene after level0, the boot loop's end.</item>
/// </list>
/// Nothing here changes a frame or touches game state: one host call and log lines. At the Main Menu it logs the boot's anatomy (what the
/// screen showed when: the launcher's logo, the black stretches, the boot screen, the game's first content, the Main Menu) -- with a warning
/// when this start ran the fast-TLS probe (kit #9's MT launcher boot +5 %: the probe, not the launcher; kit10-boot.md). Vita GL host only;
/// <c>SHEPHERD_K10_BOOTSCREEN=0</c> = kit #9 (the host shows nothing and this class does nothing).
/// Kit #10 review fix (finding 1, kit10-boot.md "Review fixes"): when the host SKIPPED the screen (never flashed: this console's last one
/// was shorter than the minimum) it still measures how long it would have shown -- this class then reports the game's first frame too
/// (host bit 4), so the host's learned length follows the boot instead of switching the screen off for good.</summary>
[ProfileOnly]
public static unsafe class K10Boot
{
    /// <summary>The host wants the game's first frame: its boot screen is showing (bit 2) or a skipped one is being measured (bit 4) -- read
    /// once, at the engine's first use (the boot's start).</summary>
    public static readonly bool On = Decide();
    /// <summary>The boot screen is on the display (not only measured).</summary>
    static bool _showing;
    static bool Decide()
    {
        if (!Profile.Vita || !ShepGl.On || !Diag.VitaHost.On) return false;
        if (Environment.GetEnvironmentVariable("SHEPHERD_K10_BOOTSCREEN") == "0") return false;
        try { int a = HostApi(); _showing = (a & 2) != 0; return (a & 6) != 0; } catch (Exception) { return false; }
    }
    static bool _ended, _lined;
    static int _endFrame = -1;
    static string _endWhy;
    static double _contentMs = -1;
    /// <summary>One line at the engine's start (Boot.Run): what this class will do.</summary>
    public static string Describe() => "[k10] boot track: " + (On && _showing ? "the boot screen (the game's own loading screen) is showing -- it ends at the game's first frame (every frame of the game's is shown)" :
        On ? "the boot screen is NOT shown in this start (never flashed: this console's last one was too short) -- the game's first frame is reported so the host learns this boot's length" :
        Environment.GetEnvironmentVariable("SHEPHERD_K10_BOOTSCREEN") == "0" ? "boot screen OFF (SHEPHERD_K10_BOOTSCREEN=0: kit #9)" : "no boot screen in this start (the host's K10 line says why; SHEPHERD_K10_BOOTSCREEN=1 shows it)")
        + "; SHEPHERD_K10_BOOTSCREEN=0 = kit #9";

    /// <summary>Icalls/VideoMiscIcalls.cs: a VideoPlayer's clip starts playing (its Playing phase) -- a fallback end (the first frame ends it first).</summary>
    public static void FirstContent(string clip) => End("the game's first video '" + (clip ?? "?") + "' starts", Time.FrameCount);

    /// <summary>The boot loop, after each frame's update and before its present: the end at the game's first frame (frame 1 is presented after
    /// this call) and, once, the boot anatomy at the Main Menu.</summary>
    public static void Tick(int frame)
    {
        if (!On) return;
        if (!_ended && frame >= 1) End("the game's first frame: every frame from here is the game's own", frame);
        if (!_lined) foreach (var s in Scenes.All.Values) if (s.Name == "Main Menu") { BootLine(frame); break; }
    }
    /// <summary>The boot loop ended (Boot.BootGame): a boot screen still showing ends here.</summary>
    public static void Finish(int frame) { if (On && !_ended) End("the boot loop ended (no content seen)", frame); }

    static void End(string why, int frame)
    {
        if (!On || _ended) return;
        _ended = true; _endFrame = frame; _endWhy = why; _contentMs = Diag.K7.Ms(Stopwatch.GetTimestamp());
        try { HostEnd(why, frame); } catch (Exception e) { Log.Warn("[k10] boot screen end: " + e.GetBaseException().Message); }
        Log.Info("[k10] boot screen " + (_showing ? "ended" : "(not shown: never flashed) measured to") + " at f" + frame.ToString() + " (" + why + ") -- the game's own frames from here");
    }

    static string S(int ms) => ms < 0 ? "?" : (ms / 1000.0).ToString("F2");
    static void BootLine(int frame)
    {
        _lined = true;
        var v = new int[12];
        try { fixed (int* p = v) HostInfo(p, v.Length); } catch (Exception) { return; }
        double menuMs = Diag.K7.Ms(Stopwatch.GetTimestamp());
        int logo0 = v[8], logo1 = v[9], rel = v[10], bs0 = v[2], bs1 = v[3];
        // the black stretches the owner saw: before the launcher's logo, between it and the boot screen, after the boot screen
        string black = "";
        if (logo1 >= 0 && bs0 > 0) black += " | black between the launcher's logo and the boot screen " + S(bs0 - logo1) + " s";
        else if (logo1 >= 0 && _contentMs >= 0) black += " | black between the launcher's logo and the game's first content " + S((int)_contentMs - logo1) + " s (no boot screen)";
        string probe = Environment.GetEnvironmentVariable("SHEPHERD_FAST_TLS") == "probe"
            ? " | WARNING SHEPHERD_FAST_TLS=probe: this start's boot times are NOT comparable (every thread-local read takes the checked slow path: kit #9's MT launcher boot +5 % vs kit #8 was this probe -- kit10-boot.md)"
            : "";
        string line = "BOOT anatomy (process time): launcher logo " + (logo0 >= 0 ? S(logo0) + "-" + S(logo1) + " s" : "none") +
                      (rel >= 0 ? " | launcher released " + S(rel) + " s" : "") + " | host main " + S(v[11]) + " s" +
                      " | boot screen " + (v[0] == 2 || v[0] == 1 ? S(bs0) + "-" + S(bs1) + " s (" + v[4].ToString() + " pictures, " + v[5].ToString() + " loops, " + v[6].ToString() + " vitaGL flips held off)"
                          : v[0] == 3 || v[0] == 4 ? "not shown (never flashed: the last one was too short; this boot's length learned, " + S(v[1]) + "-" + S(v[3]) + " s)" : "not shown (state " + v[0].ToString() + ")") +
                      " | the game's first frame " + (_contentMs >= 0 ? (_contentMs / 1000.0).ToString("F2") + " s (f" + _endFrame.ToString() + ": " + _endWhy + ")" : "?") +
                      black + " | the Main Menu " + (menuMs / 1000.0).ToString("F2") + " s (f" + frame.ToString() + ")" + probe;
        Log.Info("[k10] " + line);
        Diag.VitaHost.K7Line("K10 " + line);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static int HostApi() => shepherd_vita_k10_api();
    [MethodImpl(MethodImplOptions.NoInlining)] static void HostEnd(string why, int frame) => shepherd_vita_k10_bootscreen_end(why, frame);
    [MethodImpl(MethodImplOptions.NoInlining)] static int HostInfo(int* p, int n) => shepherd_vita_k10_bootscreen_info(p, n);
    [DllImport("__Internal")] static extern int shepherd_vita_k10_api();
    [DllImport("__Internal")] static extern void shepherd_vita_k10_bootscreen_end([MarshalAs(UnmanagedType.LPUTF8Str)] string why, int frame);
    [DllImport("__Internal")] static extern int shepherd_vita_k10_bootscreen_info(int* info, int n);
}
