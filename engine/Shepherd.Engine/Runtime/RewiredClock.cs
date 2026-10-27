using System.Reflection;
namespace Shepherd.Engine.Runtime;

/// <summary>Stage-2 review (vita/notes/engine-pass-speed.md §7): Rewired on the headless frame-counting clock.
/// Headless boots count Time.realtimeSinceStartup in frames (<see cref="Time.VirtualRealtime"/>), but Rewired keeps its own
/// realtime: ReInput's time helper reads Rewired.Utils.Classes.Utility.Stopwatch.Global, a System.Diagnostics.Stopwatch (wall
/// time). That time is Rewired's unscaledTime / unscaledDeltaTime, and the delta drives its keyboard digital-axis simulation
/// (sensitivity x delta per frame), which the game's menu navigation reads (UINavigatorNew: |axis| &gt; 0.2). On the fast
/// GL + pack path the first frame of a press sometimes stayed under 0.2, so the selection moved one frame later in some runs
/// and not in others. Rewired has its own frame-clock mode for this (UnityStopwatch, elapsed = Time.realtimeSinceStartup: what it
/// uses on Xbox One); right after a Rewired InputManager's Awake has created ReInput's time helper, the helper's stopwatch is
/// set to UnityStopwatch.Global, so every Rewired time (input timing, digital axes, its UI input module's repeat delays, its
/// timers) follows the same clock as the game. Window mode / SHEPHERD_REALTIME=wall: untouched (wall clock everywhere);
/// SHEPHERD_REWIRED_CLOCK=wall keeps only Rewired on the wall clock (measurement: the pre-review behaviour).
/// Runtime-side reflection only (no game DLL edit); a layout this was not written for is logged and left alone.</summary>
[ProfileOnly]
public static class RewiredClock
{
    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Dictionary<Type, bool> IsInputManager = new();
    static object _patched; static int _patches; static bool _broken = Environment.GetEnvironmentVariable("SHEPHERD_REWIRED_CLOCK") == "wall";

    /// <summary>Lifecycle.Awake, after a MonoBehaviour's Awake (only while <see cref="Time.VirtualRealtime"/>).</summary>
    public static void AfterAwake(object mb)
    {
        if (_broken || mb == null) return;
        var t = mb.GetType();
        if (!IsInputManager.TryGetValue(t, out var im))
        {
            im = false; for (var b = t; b != null; b = b.BaseType) if (b.FullName == "Rewired.InputManager_Base") { im = true; break; }
            IsInputManager[t] = im;
        }
        if (im) Patch(t);
    }

    static void Patch(Type inputManager)
    {
        try
        {
            Type b = inputManager; while (b != null && b.FullName != "Rewired.InputManager_Base") b = b.BaseType;
            var asm = b.Assembly;
            var re = asm.GetType("Rewired.ReInput"); var swBase = asm.GetType("Rewired.Utils.Classes.Utility.StopwatchBase"); var unity = asm.GetType("Rewired.Utils.Classes.Utility.UnityStopwatch");
            if (re == null || swBase == null || unity == null) { Broken("Rewired.ReInput / StopwatchBase / UnityStopwatch not found"); return; }
            // the time helper: ReInput's static field whose type holds the StopwatchBase (the only one in this Rewired build)
            object helper = null; FieldInfo swField = null; int found = 0;
            foreach (var f in re.GetFields(S))
            {
                if (f.FieldType.IsPrimitive || f.FieldType == typeof(string)) continue;
                var sf = f.FieldType.GetFields(I).FirstOrDefault(x => x.FieldType == swBase); if (sf == null) continue;
                found++; helper = f.GetValue(null); swField = sf;
            }
            if (found != 1) { Broken($"{found} ReInput fields hold a StopwatchBase (expected 1)"); return; }
            if (helper == null || ReferenceEquals(helper, _patched)) return;   // ReInput not initialized by this Awake / already done
            var global = unity.GetProperty("Global", S)?.GetValue(null);
            if (global == null) { Broken("UnityStopwatch.Global not found"); return; }
            var before = swField.GetValue(helper);
            swField.SetValue(helper, global);
            _patched = helper; _patches++;
            Log.Info($"[rewired-clock] ReInput's time helper: {before?.GetType().Name ?? "null"} -> UnityStopwatch.Global (Rewired's realtime follows Time.realtimeSinceStartup, the headless frame clock) at frame {Time.FrameCount}");
        }
        catch (Exception e) { Broken(e.GetBaseException().Message); }
    }
    static void Broken(string why) { _broken = true; Log.Warn($"[rewired-clock] left on the wall clock: {why}"); }
    public static string Summary() => _patches == 0 ? null : $"[rewired-clock] {_patches} ReInput time helper(s) on the frame clock";
}
