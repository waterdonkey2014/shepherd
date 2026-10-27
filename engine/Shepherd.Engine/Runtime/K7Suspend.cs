using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #7, track probes -- P3, console-style suspend / resume (vita/notes/kit7-probes.md; the roadmap's "P3 console-style
/// suspend/resume": "no 1/3 s jump after resume; re-apply clocks"). On the Vita a PS-button suspend or standby stops every thread of the app;
/// if the process clock (the Stopwatch's timestamp, sceKernelGetProcessTimeWide) goes on meanwhile -- the owner's P3 test decides it: no SUSP
/// line = it stopped too, and this is inert -- the play clock's next frame sees the whole gap and Unity's clamp turns it into one 1/3 s step of
/// game time with up to ~16 FixedUpdate catch-up steps. The host books every such gap (host.c k7_susp_book: its watchdog ticks every 250 ms;
/// a tick, or this poll, that finds the last tick more than 1.5 s old = the app was suspended) and answers the total booked through
/// shepherd_vita_k6 op 4. Here, once a frame on the PLAY clock only, before the frame's time is read: newly booked time is added to
/// <see cref="Seconds"/>, which the FRAME DELTA leaves out (Time.BeginFrame) -- Time.time / unscaledTime and the fixed steps resume where they
/// stopped. realtimeSinceStartup is NOT changed (kit #7 review): it keeps counting the suspension, as Unity's does across an application pause
/// -- the game's own DOTween relies on that (its OnApplicationPause adds the paused span of realtimeSinceStartup back to its own unscaled
/// clock) -- and it stays monotonic however a suspension falls inside a frame. (Unity's pause messages themselves are not sent by Shepherd:
/// the engine track's row; until then realtime timers see the suspension as kit #6 did.) The frame clock (scripted runs, every identity
/// check) never polls. <c>SHEPHERD_K7_SUSPEND=0</c> = kit #6 (no poll: Unity's one clamped frame after a resume). The Mac (no host): only with
/// <c>SHEPHERD_K7_SIM_SUSPEND=&lt;frame&gt;:&lt;ms&gt;</c> (a test stand-in: at that frame the engine sleeps ms and the stand-in books
/// them, as the host would book a real suspension); otherwise nothing is polled.</summary>
[ProfileOnly]
public static class K7Suspend
{
    static readonly bool Off = Environment.GetEnvironmentVariable("SHEPHERD_K7_SUSPEND") == "0";
    static int _state;           // 0 = not decided yet, 1 = polling, -1 = off (switch, the frame clock, no host / stand-in, or the host said -1)
    static int _seenMs;
    /// <summary>The suspended time booked so far (seconds): left out of the play clock's frame delta (not out of <see cref="Time.Realtime"/>).</summary>
    public static double Seconds;
    public static int Count;

    /// <summary>Time.BeginFrame, play clock: one host call a frame (a P/Invoke of one mutex and a clock read; GC-safe).</summary>
    public static void Poll()
    {
#if !SHEPHERD_EBOOT
        if (SimFrame >= 0 && Time.FrameCount == SimFrame && !_simSlept)
        {   // the Mac test's suspension happens whether P3 is on or off (off: the next frame's delta takes it all, clamped as kit #6)
            _simSlept = true;
            Log.Info("[k7] P3 (Mac stand-in for the host) a suspension of " + SimMs + " ms now (the thread sleeps; the host would book it)");
            System.Threading.Thread.Sleep(SimMs);
            _simBooked = SimMs;
        }
#endif
        if (_state < 0) return;
        if (_state == 0)
        {
            _state = Off || !Time.Wall || !(Diag.VitaHost.On || SimFrame >= 0) ? -1 : 1;
            if (_state < 0) return;
            Log.Info("[k7] P3 console-style suspend / resume ON (the play clock's frame delta leaves a suspension out; realtimeSinceStartup keeps it, as Unity's; " +
                     "SHEPHERD_K7_SUSPEND=0 = kit #6)" + (Diag.VitaHost.On ? "" : " -- the Mac stand-in, SHEPHERD_K7_SIM_SUSPEND"));
        }
        int ms = Host();
        if (ms < 0) { _state = -1; Log.Info("[k7] P3: the host has it off (SHEPHERD_K7_SUSPEND=0 or a headless app): Unity's clamp as kit #6"); return; }
        if (ms == _seenMs) return;
        double gap = (ms - _seenMs) / 1000.0;
        _seenMs = ms; Seconds += gap; Count++;
        Log.Info("[k7] P3: " + gap.ToString("F1") + " s of suspension left out of the play clock at frame " + Time.FrameCount + " (#" + Count +
                 "; this frame's delta excludes it; realtimeSinceStartup keeps it: " + Time.Realtime.ToString("F2") + " s; " + Seconds.ToString("F1") + " s in all)");
    }

    static int Host() =>
#if SHEPHERD_EBOOT
        HostNative();
#else
        Diag.VitaHost.On ? HostNative() : Sim();
#endif
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int HostNative() => shepherd_vita_k6(4, 0);
    [DllImport("__Internal")]
    static extern int shepherd_vita_k6(int op, int arg);

    static readonly int SimFrame = SimArg(0), SimMs = SimArg(1);
    static int SimArg(int i)
    {
#if SHEPHERD_EBOOT
        return -1;
#else
        var s = Environment.GetEnvironmentVariable("SHEPHERD_K7_SIM_SUSPEND");
        var p = s?.Split(':');
        return p != null && p.Length == 2 && int.TryParse(p[i], out var v) && v >= 0 ? v : -1;
#endif
    }
#if !SHEPHERD_EBOOT
    static int _simBooked; static bool _simSlept;
    /// <summary>The Mac stand-in for the host (test only): at frame SimFrame the app "is suspended" for SimMs (Poll: the engine's thread really
    /// sleeps that long, so the Stopwatch moves) and the booked total grows by it, as host.c would book a real suspension.</summary>
    static int Sim() => _simBooked;
#endif
}
