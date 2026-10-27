#if !SHEPHERD_EBOOT
using System.Runtime.CompilerServices;
namespace Shepherd.Assets;

/// <summary>Kit #7 tests track (vita/notes/kit7-tests.md "Seeded thread-schedule testing"): the hook through which a Mac test run puts the
/// engine's own threads -- the pack's read-ahead worker (<see cref="VitaPack"/>) and the multicore render pipeline's render thread
/// (Shepherd.Engine Render/MtRender.cs) -- under a seeded PCT-style scheduler (vita/tests/TestHost: one thread runs at a time and the
/// scheduler alone picks which, at the points below; a failing seed replays exactly). The instrumented code calls these wrappers in place
/// of the plain primitives:
///   <see cref="Point"/>    before a `lock` on the protocol's monitor (the thread holds none of the protocol's locks there)
///   <see cref="Wait"/>     Monitor.Wait (with or without a timeout; called holding the lock, as Monitor.Wait)
///   <see cref="PulseAll"/> Monitor.PulseAll
///   <see cref="EvWait"/> / <see cref="EvSet"/> / <see cref="EvReset"/>  ManualResetEventSlim
///   <see cref="Spawn"/> / <see cref="Leave"/>  a participating thread is about to start / is finishing
/// With no scheduler installed (<see cref="Cur"/> null: every normal run) each wrapper is the plain primitive. Mac only: the eboots are
/// built with SHEPHERD_EBOOT, under which the call sites use the primitives directly (no wrapper, no code: the eboot is kit #6's here).</summary>
public interface ISchedHook
{
    void Point(int site);
    void Spawn(string name);
    void Leave();
    bool Wait(object monitor, int timeoutMs);
    void PulseAll(object monitor);
    bool EvWait(ManualResetEventSlim ev, int timeoutMs);
    void EvSet(ManualResetEventSlim ev);
    void EvReset(ManualResetEventSlim ev);
}

public static class Sched
{
    /// <summary>The installed scheduler (a test host sets it before the threads start; null = plain primitives).</summary>
    public static ISchedHook Cur;
    // sites (stable numbers: a failing seed's trace names them)
    public const int AheadTake = 1, AheadAsk = 2, AheadLoopWait = 3, AheadLoopDone = 4, AheadDispose = 5, AheadStart = 6;
    public const int MtKick = 20, MtJoin = 21, MtCall = 22, MtStop = 23, MtLoop = 24;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Point(int site) { var c = Cur; if (c != null) c.Point(site); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Spawn(string name) { var c = Cur; if (c != null) c.Spawn(name); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Leave() { var c = Cur; if (c != null) c.Leave(); }
    public static bool Wait(object m, int timeoutMs = -1) { var c = Cur; return c != null ? c.Wait(m, timeoutMs) : timeoutMs < 0 ? Monitor.Wait(m) : Monitor.Wait(m, timeoutMs); }
    public static void PulseAll(object m) { var c = Cur; if (c != null) c.PulseAll(m); else Monitor.PulseAll(m); }
    public static bool EvWait(ManualResetEventSlim e, int timeoutMs = -1) { var c = Cur; if (c != null) return c.EvWait(e, timeoutMs); if (timeoutMs < 0) { e.Wait(); return true; } return e.Wait(timeoutMs); }
    public static void EvSet(ManualResetEventSlim e) { var c = Cur; if (c != null) c.EvSet(e); else e.Set(); }
    public static void EvReset(ManualResetEventSlim e) { var c = Cur; if (c != null) c.EvReset(e); else e.Reset(); }
}
#endif
