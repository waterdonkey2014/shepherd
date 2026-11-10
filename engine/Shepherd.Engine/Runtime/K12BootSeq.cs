using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Render;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #12 boot track (vita/notes/kit12-boot.md section 2): THE BOOT SEQUENCE THE OWNER'S WAY, the engine's half (owner decisions
/// 2026-10-29 / 30: "the Devolver video, then the Massive Monster animation, then the loading screen until the menu"; a documented difference from
/// the original, his call). The launcher plays the Devolver splash and then the Massive Monster clip (vita/shepherd/launcher.c, SHEPHERD_K12_BOOTSEQ=1)
/// before the engine is up; the host's loading screen (k10_boot.inc.c, the boot-seq emblem set with the controller icon in the corner) then stays
/// on the display until the Main Menu. So that nothing plays twice, this class skips the game's own splash (LoadMainMenu.RunSplashScreens: a 2 s
/// black, the Massive Monster logo, the Devolver video, the controller notice for 3.5 s) -- WITHOUT changing the game's code or its order:
/// <list type="bullet">
/// <item>in that one coroutine (its iterator type), a WaitForSeconds resumes on the next frame and the logo's animation wait
///   (SkeletonGraphic.YieldForAnimation, a nested enumerator) is not waited for -- every statement of the coroutine still runs, in order;</item>
/// <item>the Devolver clip (MMVideoPlayer.Play) is a 0-second clip: it starts and reaches its end in the same video update, so the game's own
///   callback ends its wait -- no decoder opens (the launcher showed it);</item>
/// <item>the splash's sounds (the logo's pre-roll, the Devolver sting, the controller notice's ding) play at volume 0 until the Main Menu: the
///   launcher played the first two with the clips, and the notice is never seen;</item>
/// <item>the game then plays its own transition to the Main Menu (MMTransition: its loading screen, hidden behind the host's), and the host's
///   loading screen ends on the frame the game's own transition switches its pentagram off (its fade-out: the game's loading-to-scene move);
///   fallbacks (review fix: real time, not frames -- 7200 frames were 4-6 minutes at the Vita's menu rate, and nothing ended a hold whose Main
///   Menu scene never loaded): 30 s of real time after the Main Menu scene loaded with the pentagram still read as shown, 300 s of real time
///   from the engine's first frame without the Main Menu scene, and the boot loop's end. A fallback ends only the hold: the host's screen goes
///   and the game's own frames show (its own transition / loading screen), the game's splash waits and sounds are its own again.</item>
/// </list>
/// On only when the HOST says the launcher showed both clips and its loading screen holds the display (shepherd_vita_k12_api bit 1) -- a start
/// without them (the eboot apps, a missing clip, SHEPHERD_K10_BOOTSCREEN=0) runs the game's own splash as kit #11. SHEPHERD_K12_BOOTSEQ=1 (default
/// OFF in this kit: the owner's device A/B first), SHEPHERD_STRICT_ORIGINAL=1 keeps the game's own order, SHEPHERD_K12_BOOT=0 = every kit #12 boot row
/// off. Mac / vlx tests: SHEPHERD_K12_BOOTSEQ_TEST=1 = on without the host (the game side alone).</summary>
[ProfileOnly]
public static unsafe class K12BootSeq
{
    static string _why = "";   // (declared before On: its initializer runs first, Decide then sets it)
    public static readonly bool On = Decide();
    static bool Decide()
    {
        if (!K12Boot.Row("SHEPHERD_K12_BOOTSEQ", false)) { _why = "off (SHEPHERD_K12_BOOTSEQ=1: the owner's boot sequence)"; return false; }
        if (Environment.GetEnvironmentVariable("SHEPHERD_STRICT_ORIGINAL") == "1") { _why = "off: SHEPHERD_STRICT_ORIGINAL=1 keeps the game's own order"; return false; }
        if (Environment.GetEnvironmentVariable("SHEPHERD_K12_BOOTSEQ_TEST") == "1") { _why = "ON (SHEPHERD_K12_BOOTSEQ_TEST=1: the game side without the host)"; return true; }
        if (!Profile.Vita || !ShepGl.On || !Diag.VitaHost.On) { _why = "off: not the Vita host"; return false; }
        try
        {
            int a = HostApi();
            if ((a & 1) == 0) { _why = "off in this start: the host did not show the sequence (the launcher's clips / the loading screen -- its K12 line says why)"; return false; }
            _why = "ON: the launcher showed the Devolver splash and the Massive Monster clip, the host's loading screen holds the display until the Main Menu";
            return true;
        }
        catch (Exception) { _why = "off: the host has no kit #12 boot sequence"; return false; }
    }
    public static string Describe() => "[k12] boot sequence " + _why;

    static bool _menu, _sawMenuScene;
    static int _menuSceneFrame = -1, _endFrame = -1;
    /// <summary>The fallbacks' clock: real time (Stopwatch ticks), started at the first Tick / at the Main Menu scene.</summary>
    static long _t0, _tMenuScene;
    const double PentagramFallbackS = 30, NoMenuFallbackS = 300;
    static double Since(long t) => (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
    /// <summary>The game's splash is being skipped and the host's loading screen is to be kept up (until the Main Menu).</summary>
    public static bool HoldsBootScreen => On && !_menu;

    /// <summary>Runtime/Lifecycle.cs: the coroutine whose root is this enumerator is the game's splash (LoadMainMenu.RunSplashScreens).</summary>
    public static bool IsSplash(IEnumerator root) => root != null && (root.GetType().FullName?.StartsWith("LoadMainMenu+<RunSplashScreens>", StringComparison.Ordinal) ?? false);
    static int _waitsSkipped, _nestedSkipped;
    public static void WaitSkipped() => _waitsSkipped++;
    public static void NestedSkipped() => _nestedSkipped++;
    /// <summary>Icalls/VideoMiscIcalls.cs: the clip the launcher already showed -- a 0-second clip here, no decoder.</summary>
    public static bool SkipClip(string key) => HoldsBootScreen && key == "Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4";

    // ---- the splash's sounds at volume 0 until the Main Menu (Audio/ShepAudio.cs Create asks) ----
    static readonly string[] MutedPaths = { "event:/music/mm_pre_roll/mm_pre_roll", "event:/music/devolver_pre_roll/DevolverSplash", "event:/ui/glass_ball_ding" };
    static int[] _mutedEv;
    public static bool Muted(int ev)
    {
        if (!HoldsBootScreen) return false;
        _mutedEv ??= MutedPaths.Select(Shepherd.Audio.ShepAudio.FindEvent).ToArray();
        return Array.IndexOf(_mutedEv, ev) >= 0;
    }

    /// <summary>The boot loop, every frame after its update and before K10Boot.Tick: the Main Menu reached (the game's transition switched its
    /// pentagram off in the Main Menu scene) ends the hold.</summary>
    public static void Tick(int frame)
    {
        if (!On || _menu) return;
        if (_t0 == 0) _t0 = Stopwatch.GetTimestamp();
        if (!_sawMenuScene) { foreach (var s in Scenes.All.Values) if (s.Name == "Main Menu") { _sawMenuScene = true; _menuSceneFrame = frame; _tMenuScene = Stopwatch.GetTimestamp(); break; } }
        if (!_sawMenuScene)
        {
            if (Since(_t0) < NoMenuFallbackS) return;   // (a fallback, never expected: the boot did not reach the Main Menu scene)
            _menu = true; _endFrame = frame;
            Log.Warn($"[k12] boot sequence: FALLBACK at f{frame}: no Main Menu scene {NoMenuFallbackS:0} s (real time) after the engine's first frame -- the hold ends here: the host's loading screen goes, " +
                     $"the game's own frames show and its splash waits / sounds are its own again ({_waitsSkipped} wait(s) resumed early, {_nestedSkipped} animation wait(s) not waited for so far)");
            return;
        }
        bool pentagram = PentagramShown();
        if (frame == _menuSceneFrame || (frame - _menuSceneFrame) % 600 == 0) Log.Info($"[k12] boot sequence: f{frame} the Main Menu scene is loaded; the game's transition {(_tr == null ? "not found" : pentagram ? "still shows its pentagram" : "shows no pentagram")}");
        if (pentagram && Since(_tMenuScene) < PentagramFallbackS) return;   // (a fallback, never expected: real time, so the device's menu rate does not stretch it)
        _menu = true; _endFrame = frame;
        Log.Info($"[k12] boot sequence: the Main Menu at f{frame} ({(pentagram ? $"FALLBACK: the transition's pentagram still read as shown {PentagramFallbackS:0} s (real time) after the Main Menu scene loaded" : "the game's transition switched its pentagram off: its own loading-to-scene move")}); " +
                 $"the game's splash was skipped ({_waitsSkipped} wait(s) resumed on the next frame, {_nestedSkipped} animation wait(s) not waited for, the Devolver clip 0 s), its sounds at volume 0 until here; the host's loading screen ends now");
    }
    /// <summary>The game's transition (MMTools.MMTransition, kept as the game's own singleton) shows its pentagram.</summary>
    static object _tr; static Type _trT; static FieldInfo _pentF, _trF;
    static bool PentagramShown()
    {
        try
        {
            if (_trT == null)
            {
                foreach (var m in Objects.All.OfType<NMono>()) if (!m.Destroyed && m.Managed?.GetType().FullName == "MMTools.MMTransition") { _trT = m.Managed.GetType(); break; }
                if (_trT == null) return false;
                _trF = _trT.GetField("mmTransition", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _pentF = _trT.GetField("pentagramImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            _tr = _trF?.GetValue(null);   // the game's own live transition (MMTransition.mmTransition)
            if (_tr == null) return false;
            return _pentF?.GetValue(_tr) is UnityEngine.UI.Image img && img.enabled && Objects.Get(img) is NComponent c && c.Go != null && c.Go.ActiveInHierarchy;
        }
        catch (Exception) { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static int HostApi() => shepherd_vita_k12_api();
    [DllImport("__Internal")] static extern int shepherd_vita_k12_api();
}
