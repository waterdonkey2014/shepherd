namespace Shepherd.Engine.Runtime;

/// <summary>kit3 play mode (vita/notes/play-mode.md): which clock drives the game, picked once at boot.
/// <para><b>frame</b> (headless default; scripted runs, pad.txt, identity checks): every frame is exactly 1/60 s of game time and of
/// realtimeSinceStartup (Time.CaptureDeltaTime + Time.VirtualRealtime), so a run's frame timeline never depends on the machine's speed.
/// Exactly the behaviour from before play mode, including the legacy fixed-step loop (at 1/60 s a frame its 5-step cap never binds).
/// On a slow device the game then runs at fps/60 of real time (the device kit #2 menu at ~6 fps: a tenth).</para>
/// <para><b>wall</b> (window default; the owner's play VPKs on the Vita: the GL host sets it): Unity's own clock. Time.unscaledDeltaTime =
/// the real time since the last frame, clamped to Time.maximumDeltaTime (the project's "Maximum Allowed Timestep", 1/3 s in CotL);
/// Time.deltaTime = that x timeScale; realtimeSinceStartup = the wall clock; FixedUpdate every fixedDeltaTime (0.02 s = 50 Hz in CotL)
/// of game time, as many steps per frame as fixedTime needs to catch up with time (Unity's StepFixedTime; the only bound is the clamp
/// on how far time moves in one frame: "Maximum Allowed Timestep"); inside FixedUpdate time / unscaledTime / unscaledDeltaTime are the
/// fixed ones (Icalls/AppIcalls.cs), as Unity documents. Rewired keeps its own wall-clock Stopwatch (RewiredClock only hooks the frame
/// clock). The engine's starting values are CotL's own TimeManager (globalgamemanagers #8: Fixed Timestep 0.02, Maximum Allowed
/// Timestep 0.33333334, Time Scale 1, Maximum Particle Timestep 0.03), so nothing is read at run time.</para>
/// Switch: SHEPHERD_TIME=wall|frame; unset, the Stage B name SHEPHERD_CLOCK=wall still means wall; otherwise window = wall, headless =
/// frame. SHEPHERD_REALTIME=wall (frame clock only, measurement) keeps realtimeSinceStartup on the wall clock.
/// Audio is not on either clock: shepaudio's output runs at the hardware rate on its own thread (Audio/ShepAudio.cs).</summary>
[ProfileOnly]   // log text and switch names only: no property-bag key is read here (Assets/Diet.cs)
public static class PlayClock
{
    /// <summary>Boot (BootGame), before the first frame. Returns true for the wall clock.</summary>
    public static bool Configure(bool window)
    {
        var t = (Environment.GetEnvironmentVariable("SHEPHERD_TIME") ?? "").Trim().ToLowerInvariant();
        bool wall; string why;
        if (t is "wall" or "frame") { wall = t == "wall"; why = "SHEPHERD_TIME=" + t; }
        else
        {
            if (t.Length > 0) Log.Warn($"[time] SHEPHERD_TIME='{t}' is neither wall nor frame: ignored");
            if (Environment.GetEnvironmentVariable("SHEPHERD_CLOCK") == "wall") { wall = true; why = "SHEPHERD_CLOCK=wall, the Stage B name of SHEPHERD_TIME=wall"; }
            else { wall = window; why = window ? "the window-mode default" : "the headless default"; }
        }
        Time.Wall = wall;
        if (!wall)
        {
            Time.CaptureDeltaTime = Time.FrameClockDt; Time.VirtualRealtime = Environment.GetEnvironmentVariable("SHEPHERD_REALTIME") != "wall";   // physics stage 0: SHEPHERD_CAPTURE_DT (unset: 1f / 60f, as before)
            if (Time.CaptureDtNote != null) Log.Info("[time] " + Time.CaptureDtNote);
            Log.Info($"[time] clock FRAME ({why}): deterministic -- every frame is {(Time.CaptureDtNote == null ? "1/60 s" : Time.FrameClockDt.ToString("R") + " s")} of game time" +
                     (Time.VirtualRealtime ? " and of realtimeSinceStartup" : "; realtimeSinceStartup on the wall clock (SHEPHERD_REALTIME=wall)") +
                     ", whatever the frame rate (scripted runs, identity checks); SHEPHERD_TIME=wall plays in real time");
        }
        else
            Log.Info($"[time] clock WALL ({why}): Unity's clock -- deltaTime = real elapsed time clamped to maximumDeltaTime ({Time.MaximumDeltaTime:F4} s) x timeScale, " +
                     $"unscaledDeltaTime the same unscaled, realtimeSinceStartup = wall clock, FixedUpdate every {Time.FixedDeltaTime:F4} s of game time " +
                     $"({1 / Time.FixedDeltaTime:F0} Hz) catching up with Time.time; audio plays on its own real-time output, not on frames");
        return wall;
    }

    /// <summary>Physics stage 0 (physics-plan-v2 C5): SHEPHERD_CAPTURE_DT, the headless frame clock's game time per frame -- unset = 1/60 s
    /// (exactly as before: the same float, 1f / 60f), or "1/30", "1/20" (1f / 30f, 1f / 20f), or seconds ("0.05"). Headless only: the wall clock
    /// ignores it. It paces logic ticks like the game's 30 / 20 fps settings so physics captures replay at the session's pace.</summary>
    public static float CaptureDtFromEnv() => ParseCaptureDt(Environment.GetEnvironmentVariable("SHEPHERD_CAPTURE_DT"));
    public static string CaptureDtNote { get; private set; }
    public static float ParseCaptureDt(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) { CaptureDtNote = null; return 1f / 60f; }
        float v = -1f;
        int slash = s.IndexOf('/');
        if (slash > 0 && float.TryParse(s[..slash], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
            float.TryParse(s[(slash + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0) v = a / b;
        else if (slash < 0 && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c)) v = c;
        if (v > 0f && v <= Time.MaximumDeltaTime) { CaptureDtNote = $"SHEPHERD_CAPTURE_DT={s}: {v:R} s of game time per frame ({1 / v:F2} frames per game second)"; return v; }
        CaptureDtNote = $"SHEPHERD_CAPTURE_DT='{s}' is not 1/N or a time in (0, {Time.MaximumDeltaTime:R}] s: ignored, 1/60 s per frame";
        return 1f / 60f;
    }

    /// <summary>End of the run (wall clock only): game time vs wall time, the frames Unity's clamp shortened, the fixed steps.</summary>
    public static string Summary()
    {
        if (!Time.Wall || Time.WallFrames == 0) return null;
        double wall = Time.Realtime - K7Suspend.Seconds - Time.WallStart;   // (kit #7 P3: WallStart is on the frame clock, without the suspensions)
        return $"[time] wall clock: {Time.WallFrames} frames over {wall:F2} s of wall time ({Time.WallFrames / Math.Max(1e-9, wall):F2} fps); Time.unscaledTime {Time.UnscaledNow:F2} s, " +
               $"Time.time {Time.Now:F2} s; {Time.ClampedFrames} frame(s) longer than maximumDeltaTime ({Time.MaximumDeltaTime:F4} s) clamped as in Unity " +
               $"({Time.ClampedLost:F2} s of wall time not given to the game: loading hitches); FixedUpdate steps {Time.FixedSteps}, fixedTime {Time.FixedNow:F2} s " +
               $"({Time.FixedSteps / Math.Max(1e-9, Time.Now):F2} steps per second of game time; fixedDeltaTime {Time.FixedDeltaTime:F4} s); " +
               $"per frame {Time.FixedSteps / (double)Math.Max(1, Time.FixedFrames):F2} steps (max {Time.FixedMaxSteps}), FixedUpdate " +
               $"{Time.FixedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, Time.FixedFrames):F2} ms per frame";
    }
}
