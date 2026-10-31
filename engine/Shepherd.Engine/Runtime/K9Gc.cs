namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #9, track gc (vita/notes/kit9-gc.md), the engine's half of two rows.
/// <list type="bullet">
/// <item><b>Per-scene nursery sizing</b>: when a scene's load starts (Icalls/BundleIcalls.cs LoadSceneAsync) the host hears the scene's name and
///   the main thread's allocation so far (Diag/VitaHost.cs kind 8); the host (vita/shepherd/k9_gc.inc.c) keeps each scene's allocation history in
///   its learning cache and gives sgen the nursery size it learned for that scene. Only when collections happen changes, never what the game
///   computes. On only with the host's switch (env file SHEPHERD_GC_NURSERY=scene, not SHEPHERD_K9_GC=0) on the Vita host; off, nothing is
///   called (kit #8).</item>
/// <item><b>The used-size probe</b> (test only, SHEPHERD_GC_USED_PROBE=1): GC.GetTotalMemory(false) once every frame, timed -- the per-frame
///   caller the sgen used-size fix (SHEPHERD_GC_USED) is for -- and one host line at the end with the calls' cost. Off by default.</item>
/// </list></summary>
[ProfileOnly]
public static class K9Gc
{
    static readonly bool K9 = Profile.Vita && Diag.VitaHost.On && Environment.GetEnvironmentVariable("SHEPHERD_K9_GC") != "0";
    public static readonly bool SceneNursery = K9 && Environment.GetEnvironmentVariable("SHEPHERD_GC_NURSERY") == "scene";
    public static readonly bool UsedProbe = K9 && Environment.GetEnvironmentVariable("SHEPHERD_GC_USED_PROBE") == "1";
    public static void OnLoadStart(string scene) { if (SceneNursery) Diag.VitaHost.K9Scene(scene); }

    static long _n, _ticks, _max, _last;
    /// <summary>Diag/VitaHost.Frame, with the probe on: one timed GC.GetTotalMemory(false).</summary>
    public static void ProbeFrame()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _last = GC.GetTotalMemory(false);
        long d = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _n++; _ticks += d; if (d > _max) _max = d;
    }
    /// <summary>Diag/VitaHost.Finish, with the probe on: the host line.</summary>
    public static string ProbeLine()
    {
        double us(long t) => t * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return $"K9 GC used-size probe (SHEPHERD_GC_USED_PROBE=1): {_n} GC.GetTotalMemory(false) calls (one per frame), mean {(_n > 0 ? us(_ticks) / _n : 0):F0} us, max {us(_max):F0} us, total {us(_ticks) / 1000:F1} ms; the last answer {_last / 1024} KiB";
    }
}
