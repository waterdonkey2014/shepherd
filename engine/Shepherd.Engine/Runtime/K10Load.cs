using System.Diagnostics;
using System.Reflection;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #10, track boot (vita/notes/kit10-boot.md): LOADING PRIORITY -- Unity's background loading time slice.
/// Unity integrates finished asynchronous loads on the main thread for a slice of each frame set by
/// <c>Application.backgroundLoadingPriority</c> (Low 2 ms, BelowNormal 4, Normal 10, High 50); the game sets High around its asset groups
/// (Lamb.UI.UIManager: LoadMainMenuAssets, LoadPersistentGameAssets, ... -- every one behind its loading screen) and Normal again after. The
/// engine's AsyncOps.Tick completed every ready operation ONCE per frame: an operation that a completion started (Addressables' dependency
/// chains -- catalog, bundle, asset) waited for the next frame, a whole frame (the game's Update, the loading screen drawn) per link, ~100 ms
/// each on the device while the loading screen runs. Now, while the slice lasts, the Tick takes the operations that became ready in the same
/// frame too (another pass, as Unity's integration loop does until its slice is spent): the slice is the game's own priority, and High (50
/// ms) while the game's loading screen shows (MMTools.MMTransition.IsPlaying) -- the screen then runs at ~20 fps while loading, its fades
/// still timed in real time (the game times them with unscaled time). Nothing is completed that was not ready; the order within a pass is
/// the Tick's own; game code runs where it always does (completion events inside the Tick, await continuations at the sync context's point).
/// What moves is only WHICH FRAME a ready link completes in -- what a faster machine does on PC -- so the frame-clock routes (tests: every
/// frame is 1/60 s, no real time) keep kit #9's one pass per frame unless a test forces it:
/// <list type="bullet">
/// <item><c>SHEPHERD_K10_LOADPRIO</c> unset = on under the PLAY clock (the device apps' clock), off under the frame clock (kit #9's records);</item>
/// <item><c>=0</c> = kit #9 everywhere (and the priority getter answers Normal again, as kit #9);</item>
/// <item><c>=on</c> = the time slices under the frame clock too (timing tests; not deterministic); <c>=all</c> = every ready link in the same frame,
/// no time limit (deterministic: the vlx route gates of the feature).</item>
/// </list>
/// <c>Application.backgroundLoadingPriority</c>'s getter now answers what the game set (Unity's behaviour; kit #9 always said Normal -- no game
/// code reads it: only Assembly-CSharp's setters reference it).
/// Kit #10 review fix (finding 8, kit10-boot.md "Review fixes"): the loading screen's field (MMTools.MMTransition.IsPlaying, a public static
/// bool in Assembly-CSharp) is looked up again -- at most once a frame -- until it is found (the first lookup could come before the game's
/// assembly is loaded and was then cached as "none" for the run, silently leaving the 50 ms slice off), and the end line says whether it
/// was found and in how many frames the loading screen showed, so the device's first run of this path reports itself.</summary>
public static class K10Load
{
    const int Off = 0, Play = 1, On = 2, All = 3;
    public static readonly int Mode = Environment.GetEnvironmentVariable("SHEPHERD_K10_LOADPRIO") switch { "0" => Off, "on" => On, "all" => All, _ => Play };
    /// <summary>The game's Application.backgroundLoadingPriority (UnityEngine.ThreadPriority: Low 0, BelowNormal 1, Normal 2, High 4).</summary>
    public static int Priority = 2;
    public static bool Active => Mode == All || Mode == On || (Mode == Play && Time.Wall);
    static long _t0;
    static int _frames, _passes, _ops, _maxPasses, _screenFrames;
    static double _extraMs;
    static FieldInfo _isPlaying;
    static int _lookedFrame = -1, _foundFrame = -1, _looks, _seenFrames, _seenFrame = -1;
    static bool _asmSeen;
    static bool LoadingScreen()
    {
        if (_isPlaying == null && _lookedFrame != Time.FrameCount)   // (review fix, finding 8: again each frame until found -- never cached as "none")
        {
            _lookedFrame = Time.FrameCount; _looks++;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "Assembly-CSharp") { _asmSeen = true; _isPlaying = asm.GetType("MMTools.MMTransition")?.GetField("IsPlaying", BindingFlags.Public | BindingFlags.Static); break; }
            if (_isPlaying != null) _foundFrame = Time.FrameCount;
        }
        bool on;
        try { on = _isPlaying != null && _isPlaying.GetValue(null) is true; } catch (Exception) { on = false; }
        if (on && _seenFrame != Time.FrameCount) { _seenFrame = Time.FrameCount; _seenFrames++; }
        return on;
    }
    static double SliceMs(bool screen) => screen ? 50 : Priority switch { 0 => 2, 1 => 4, 4 => 50, _ => 10 };
    /// <summary>AsyncOps.Tick, before its first pass.</summary>
    public static void Begin() => _t0 = Stopwatch.GetTimestamp();
    /// <summary>After pass number <paramref name="passes"/> completed <paramref name="done"/> operations: one more pass?</summary>
    public static bool More(int passes, int done)
    {
        if (done <= 0) return false;
        if (passes >= 64) return false;   // (a safety cap: a chain that never ends)
        if (Mode == All) return true;
        bool screen = LoadingScreen();
        double ms = (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;
        if (ms >= SliceMs(screen)) return false;
        if (screen && passes == 1) _screenFrames++;
        return true;
    }
    /// <summary>AsyncOps.Tick, after its last pass: the frame's extra passes (statistics only).</summary>
    public static void End(int passes, int extraOps)
    {
        if (passes <= 1) return;
        _frames++; _passes += passes - 1; _ops += extraOps; if (passes > _maxPasses) _maxPasses = passes;
        _extraMs += (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;
    }
    public static string Describe() => "[k10] loading priority: " + (Mode == Off ? "OFF (SHEPHERD_K10_LOADPRIO=0: kit #9's one pass a frame)" : Mode == All ? "ALL (test: every ready link in the same frame)" :
        Mode == On ? "on under every clock (SHEPHERD_K10_LOADPRIO=on: timing test)"
        // (review fix, finding 8: this line is written before the clock is chosen -- Time.Wall said "frame clock" on a wall-clock run; the choice is made each frame)
        : "on under the play clock (the device apps' clock): Unity's time slice -- the game's priority, 50 ms while its loading screen shows; off under the frame clock (kit #9's records; SHEPHERD_K10_LOADPRIO=all / on tests it)");
    public static string Summary() => _frames == 0 && _looks == 0 ? null : "[k10] loading priority: " + _frames.ToString() + " frame(s) took ready links in more passes (" + _passes.ToString() + " extra pass(es), "
        + _ops.ToString() + " operation(s) completed a frame or more earlier; at most " + _maxPasses.ToString() + " passes in a frame; " + _screenFrames.ToString() + " of the frames under the loading screen's 50 ms slice)"
        + (Mode == All ? "" : "; the loading screen's field MMTools.MMTransition.IsPlaying " + (_isPlaying != null ? "found at f" + _foundFrame.ToString() + " (" + _looks.ToString() + " lookup(s)), the screen seen in " + _seenFrames.ToString() + " frame(s) with ready links"
            : "NOT FOUND (" + _looks.ToString() + " lookup(s); Assembly-CSharp " + (_asmSeen ? "loaded: the field is missing -- the 50 ms slice never applies" : "never seen") + ")"));
}
