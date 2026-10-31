using System.Diagnostics;
namespace Shepherd.Engine.Runtime;

public static class Time
{
    // Kit #9 (track runtime, E5 zero-hop mirrors; vita/notes/kit9-runtime.md): the state the mirrored Time getters read lives in
    // Shepherd.Hooks.Mirror -- ONE copy, read and written here through these properties (same initial values, same arithmetic), and read
    // by the rewritten Unity stubs directly when Mirror.On. With SHEPHERD_K9_MIRROR=0 the stubs ask the engine as in kit #8.
    public static int FrameCount { get => Shepherd.Hooks.Mirror.FrameCount; set => Shepherd.Hooks.Mirror.FrameCount = value; }
    public static int RenderedFrames;
    public static float DeltaTime { get => Shepherd.Hooks.Mirror.DeltaTime; set => Shepherd.Hooks.Mirror.DeltaTime = value; }
    public static float UnscaledDeltaTime { get => Shepherd.Hooks.Mirror.UnscaledDeltaTime; set => Shepherd.Hooks.Mirror.UnscaledDeltaTime = value; }
    public static float TimeScale { get => Shepherd.Hooks.Mirror.TimeScale; set => Shepherd.Hooks.Mirror.TimeScale = value; }
    public static float FixedDeltaTime { get => Shepherd.Hooks.Mirror.FixedDeltaTime; set => Shepherd.Hooks.Mirror.FixedDeltaTime = value; }
    public static float SmoothDeltaTime = 1f / 60f, MaximumDeltaTime = 1f / 3f, CaptureDeltaTime;
    public static double Now { get => Shepherd.Hooks.Mirror.Now; set => Shepherd.Hooks.Mirror.Now = value; }
    public static double FixedNow { get => Shepherd.Hooks.Mirror.FixedNow; set => Shepherd.Hooks.Mirror.FixedNow = value; }
    public static double UnscaledNow, FixedUnscaledNow, LevelLoadAt;
    public static bool InFixedStep { get => Shepherd.Hooks.Mirror.InFixedStep; set => Shepherd.Hooks.Mirror.InFixedStep = value; }
    public static int TargetFrameRate = -1;
    static readonly Stopwatch Sw = Stopwatch.StartNew();
    /// <summary>Time.realtimeSinceStartup. V6 stage 2: in headless boots (Boot sets <see cref="VirtualRealtime"/> next to CaptureDeltaTime)
    /// it counts frames instead of the wall clock -- CaptureDeltaTime per frame, a 60 fps machine -- so the game's realtime waits
    /// (MMTransition's WaitForSecondsRealtime, DOTween's independent updates) take the same number of frames however fast or slow
    /// the engine runs: the boot's frame timeline no longer depends on engine speed (a faster engine had landed the Main Menu
    /// hundreds of frames later). Window mode keeps the wall clock; SHEPHERD_REALTIME=wall restores it headless.</summary>
    public static double Realtime => VirtualRealtime ? _virtualRealtime : Sw.Elapsed.TotalSeconds;
    public static bool VirtualRealtime;
    static double _virtualRealtime;
    static double _last = -1;

    /// <summary>kit3 play mode (vita/notes/play-mode.md): the game clock is the wall clock (Unity's), chosen once at boot by
    /// <see cref="PlayClock.Configure"/> (which also documents both clocks). False = the deterministic frame clock.</summary>
    public static bool Wall { get => Shepherd.Hooks.Mirror.Wall; set => Shepherd.Hooks.Mirror.Wall = value; }
    /// <summary>Wall mode: the fixed-step bookkeeping Unity keeps next to fixedTime (Time.fixedUnscaledDeltaTime).</summary>
    public static float FixedUnscaledDeltaTime { get => Shepherd.Hooks.Mirror.FixedUnscaledDeltaTime; set => Shepherd.Hooks.Mirror.FixedUnscaledDeltaTime = value; }
    internal static long WallFrames, ClampedFrames, FixedSteps; internal static double ClampedLost, WallStart = -1;

    /// <summary>Physics stage 0 (physics-plan-v2 C5): the headless frame clock's game time per frame, SHEPHERD_CAPTURE_DT (parsed by
    /// <see cref="PlayClock.ParseCaptureDt"/>; unset = 1f / 60f exactly as before). The text lives in PlayClock ([ProfileOnly]): Time's own
    /// string literals are part of the V3 diet's readable-key scan, so this class gains none.</summary>
    public static float FrameClockDt => _frameDt ??= PlayClock.CaptureDtFromEnv();
    static float? _frameDt;
    public static string CaptureDtNote => PlayClock.CaptureDtNote;
    public static float ParseCaptureDt(string s) => PlayClock.ParseCaptureDt(s);

    public static void BeginFrame()
    {
        if (Wall) K7Suspend.Poll();   // kit #7 P3: a suspension booked since the last frame (play clock only)
        if (VirtualRealtime) _virtualRealtime += CaptureDeltaTime > 0 ? CaptureDeltaTime : 1.0 / 60;
        var rt = Realtime - K7Suspend.Seconds;   // kit #7 P3: the frame clock leaves the booked suspensions out (0 unless one was booked); realtimeSinceStartup keeps them
        double raw = _last < 0 ? 1.0 / 60 : rt - _last; _last = rt;
        if (CaptureDeltaTime > 0) raw = CaptureDeltaTime;
        UnscaledDeltaTime = (float)Math.Min(raw, MaximumDeltaTime);
        DeltaTime = UnscaledDeltaTime * TimeScale;
        SmoothDeltaTime = SmoothDeltaTime * 0.8f + DeltaTime * 0.2f;
        Now += DeltaTime; UnscaledNow += UnscaledDeltaTime; FrameCount++;
        if (Wall)
        {
            if (WallStart < 0) WallStart = rt;
            WallFrames++;
            if (raw > MaximumDeltaTime) { ClampedFrames++; ClampedLost += raw - MaximumDeltaTime; }
        }
    }

    /// <summary>kit #3 review, wall mode: the fixed steps each frame ran (Unity's StepFixedTime grants as many as game time needs: ~3.8 a
    /// frame at 13 fps, 16-17 after a frame clamped at maximumDeltaTime) and the time they took, for the heartbeat (<see cref="FixedNote"/>,
    /// per window) and the end summary (PlayClock.Summary). The frame clock's runs are never touched (identity).</summary>
    internal static long FixedTicks, FixedMaxSteps, FixedFrames; static long _winSteps, _winFrames, _winTicks, _winMax;
    /// <summary>Lifecycle.Frame, wall mode: one frame's fixed steps and their time (Stopwatch ticks).</summary>
    public static void NoteFixedFrame(int steps, long ticks)
    {
        FixedTicks += ticks; FixedFrames++; if (steps > FixedMaxSteps) FixedMaxSteps = steps;
        _winSteps += steps; _winFrames++; _winTicks += ticks; if (steps > _winMax) _winMax = steps;
    }
    /// <summary>The heartbeat part since the previous one (wall mode only; empty on the frame clock): mean / max fixed steps per frame and the
    /// FixedUpdate time per frame -- with the frame clock's ~0.83 steps per frame (00099) the comparison needs it.</summary>
    public static string FixedNote()
    {
        if (!Wall || _winFrames == 0) return "";
        var r = $" | fixed: {_winSteps / (double)_winFrames:F1} steps/frame (max {_winMax}), {_winTicks * 1000.0 / Stopwatch.Frequency / _winFrames:F1} ms/frame";
        _winSteps = _winFrames = _winTicks = _winMax = 0;
        return r;
    }

    /// <summary>Wall mode, Lifecycle.Frame: Unity's StepFixedTime -- true while fixedTime + fixedDeltaTime &lt;= time; advances
    /// fixedTime (and fixedUnscaledTime by fixedDeltaTime / timeScale, Unity's fixedUnscaledDeltaTime) for the step it grants.</summary>
    public static bool StepFixedTime()
    {
        if (FixedNow + FixedDeltaTime > Now) return false;
        FixedNow += FixedDeltaTime;
        FixedUnscaledDeltaTime = TimeScale > 0 ? FixedDeltaTime / TimeScale : FixedDeltaTime;
        FixedUnscaledNow += FixedUnscaledDeltaTime;
        FixedSteps++;
        return true;
    }
}
