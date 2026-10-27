using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>k4-display, the NATIVE DISPLAY CAP (device kit #3: Settings > Graphics let the owner pick windowed / borderless modes and frame
/// rates the Vita cannot show). The display the engine reports to the game is the Vita panel's one fixed mode, as Unity reports it on a
/// fixed-screen platform:
/// - Screen.resolutions / Screen.currentResolution: the panel only, 960x544 @ 60 Hz (the game's resolution list = Screen.resolutions)
/// - Screen.SetResolution: keeps 960x544 full screen; any other request is logged and ignored
/// - Screen.fullScreen / Screen.fullScreenMode: always full screen (FullScreenWindow, the mode a fixed panel reports); setters logged + ignored
/// - Application.targetFrameRate / QualitySettings.vSyncCount: clamped to the panel's 60 Hz and the engine's frame lock (<see cref="LockHz"/>)
/// One place holds the cap: <see cref="Capped"/> (on in the Vita profile) and the mode below. A future TV / streaming mode lifts it here
/// (offer its own modes instead of the panel's); SHEPHERD_DISPLAY_CAP=0 is the switch back to the previous behaviour (a 960x544 list
/// without a refresh rate, windowed, every value stored as set). The Mac default (1280x720 window) is never capped: unchanged.</summary>
public static class NativeDisplay
{
    /// <summary>The panel: OLED PCH-1000 and LCD PCH-2000 are both 960x544 at 60 Hz.</summary>
    public const int Width = Profile.VitaWidth, Height = Profile.VitaHeight, RefreshHz = 60;
    public static readonly bool Capped = Profile.Vita && Environment.GetEnvironmentVariable("SHEPHERD_DISPLAY_CAP") != "0";
    /// <summary>The engine's frame lock in Hz (0 = none: frames are paced by the panel's vblank only). The planned locked 30 / 20
    /// (vita/PLAN.md frame pacing) sets it; Application.targetFrameRate and vSyncCount then report it.</summary>
    public static int LockHz;
    /// <summary>The fastest rate the game can get: the panel's, or the lock's when one is set.</summary>
    public static int MaxHz => LockHz > 0 && LockHz < RefreshHz ? LockHz : RefreshHz;
    /// <summary>The mode a fixed full-screen panel reports (Unity's fixed-screen platforms: FullScreenWindow).</summary>
    public const FullScreenMode Mode = FullScreenMode.FullScreenWindow;

    public static Resolution Native()
    {
        var r = new Resolution(); r.width = Width; r.height = Height;
        r.refreshRateRatio = new RefreshRate { numerator = RefreshHz, denominator = 1 };
        return r;
    }

    // ---- what the game asked for (the end-of-run [display] line; each distinct request is also logged once when made) ----
    static int _setRes, _setMode, _setTfr, _setVsync;
    static readonly HashSet<string> _seen = new();
    static void Note(string what) { lock (_seen) if (_seen.Add(what)) Log.Info($"[display] {what}"); }

    public static void SetResolution(int w, int h, FullScreenMode mode)
    {
        _setRes++;
        Note(w == Width && h == Height && mode == Mode ? $"Screen.SetResolution({w}x{h}, {mode}): the native mode"
            : $"Screen.SetResolution({w}x{h}, {mode}) -> kept {Width}x{Height} {Mode} (native display cap)");
    }
    public static void SetMode(FullScreenMode mode) { _setMode++; if (mode != Mode) Note($"Screen.fullScreenMode = {mode} -> kept {Mode} (native display cap)"); }
    public static void SetFullScreen(bool fs) { _setMode++; if (!fs) Note($"Screen.fullScreen = false -> kept {Mode} (native display cap)"); }

    /// <summary>Application.targetFrameRate: Unity's "as fast as the platform allows" (&lt;= 0) and anything above the panel / lock
    /// become the panel's (or the lock's) rate; a lower request is kept.</summary>
    public static int ClampTargetFrameRate(int requested)
    {
        _setTfr++;
        int v = requested <= 0 || requested > MaxHz ? MaxHz : requested;
        if (v != requested) Note($"Application.targetFrameRate = {requested} -> {v} (the {RefreshHz} Hz panel{(LockHz > 0 ? $", lock {LockHz}" : "")})");
        return v;
    }
    /// <summary>QualitySettings.vSyncCount: the panel always waits for its vblank (no tearing, no free-running): the vblanks per frame
    /// the engine actually uses, 1 (60 Hz) or the lock's (2 = 30, 3 = 20).</summary>
    public static int VSyncCount => RefreshHz / MaxHz;
    public static void SetVSyncCount(int requested) { _setVsync++; if (requested != VSyncCount) Note($"QualitySettings.vSyncCount = {requested} -> {VSyncCount} (the {RefreshHz} Hz panel waits for its vblank{(LockHz > 0 ? $", lock {LockHz}" : "")})"); }

    public static string Summary() => !Capped ? null
        : $"[display] native cap {Width}x{Height} @ {RefreshHz} Hz {Mode}, lock {(LockHz > 0 ? LockHz + " Hz" : "none")}: game requests SetResolution {_setRes}, full-screen mode {_setMode}, targetFrameRate {_setTfr}, vSyncCount {_setVsync} (all kept at the native mode; SHEPHERD_DISPLAY_CAP=0 lifts the cap)";
}
