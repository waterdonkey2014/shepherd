using System.Runtime.CompilerServices;
using Shepherd.Assets;
namespace Shepherd.TestHost;

/// <summary>Kit #7 tests track (vita/notes/kit7-tests.md "Seeded thread-schedule testing"): a PCT scheduler (Burckhardt et al. 2010, the
/// randomized scheduler AWS Shuttle ships) over the engine's own threads -- the main thread, the render thread (Render/MtRender.cs) and
/// the pack's read-ahead worker (Shepherd.Assets VitaPack) -- through the hooks in Shepherd.Assets/Sched.cs. Exactly one participating
/// thread runs at a time (the token holder); the others are parked inside the scheduler; only the scheduler decides who runs next, at
/// the instrumented points (before a lock on the protocol's monitor, Monitor.Wait, ManualResetEventSlim.Wait). PCT: every thread gets a
/// random distinct priority (d .. d+n-1), the highest-priority runnable thread runs, and at d-1 random steps of the run the running thread
/// drops to priority i (0 .. d-2) -- which finds any ordering bug of depth d with probability >= 1/(n k^(d-1)). Timed waits are runnable
/// as "the timeout fires" (an explored choice, not the wall clock). The same (seed, d, k) gives the same decisions: a failing seed replays
/// exactly. Threads outside the model (GC, the game's A* path thread, audio) still run freely: combine with SHEPHERD_TEST_ASTAR=join.
/// Deadlock (no runnable thread, no timed waiter) and a stall (nobody arrives for 60 s) are reported with the state of every thread.</summary>
public sealed class Pct : ISchedHook
{
    enum St { Running, Ready, WaitMon, WaitEv, Done }
    sealed class T
    {
        public int Id, Thread; public string Name; public int Prio; public St St; public object On; public bool Timed, TimedOut; public int TimeoutMs;
        public readonly SemaphoreSlim Go = new(0);
        public override string ToString() => $"#{Id} {Name} prio {Prio} {St}{(On != null ? $" on {On.GetType().Name}@{RuntimeHelpers.GetHashCode(On):x}" : "")}{(Timed ? " (timed)" : "")}";
    }
    readonly object _g = new();
    readonly Dictionary<int, T> _byThread = new();
    readonly List<T> _all = new();
    readonly List<T> _arrived = new();
    T _cur;
    int _pending;
    string _pendingName;
    readonly ShepherdOracle.Rng _rng;
    readonly long[] _change;
    int _nextChange;
    public long Steps, Switches, Timeouts, Decisions;
    public ulong TraceHash = 14695981039346656037UL;
    public readonly int Seed, Depth; public readonly long K;
    public string Failure;
    public readonly List<string> Trace = new();
    public bool KeepTrace;
    /// <summary>Timed waits up to this long are explored as "the timeout fires" whenever the waiter has the highest priority.</summary>
    public int ShortWaitMs = 100;
    /// <summary>The chance (%) that a timed waiter outranking every runnable thread times out now rather than letting the best of them run.</summary>
    public int TimeoutChancePct = 50;
    void ReleaseAll() { foreach (var t in _all) if (t != _cur && t.St != St.Done) { t.St = St.Ready; t.Go.Release(); } }

    public Pct(int seed, int depth, long k)
    {
        Seed = seed; Depth = Math.Max(1, depth); K = Math.Max(1, k);
        _rng = new ShepherdOracle.Rng((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
        _change = new long[Depth - 1];
        for (int i = 0; i < _change.Length; i++) _change[i] = 1 + (long)(_rng.Next() % (ulong)K);
        Array.Sort(_change);
    }

    T Me()
    {
        int tid = Environment.CurrentManagedThreadId;
        if (_byThread.TryGetValue(tid, out var t)) return t;
        // a new participant: a random priority above the change-point band (d-1 .. d+n-1 range, distinct by construction: random in a wide band + id)
        t = new T { Id = _all.Count, Thread = tid, Name = _pendingName ?? Thread.CurrentThread.Name ?? $"t{tid}", Prio = Depth + (int)(_rng.Next() % 1_000_000) * 16 + _all.Count };
        _byThread[tid] = t; _all.Add(t);
        if (_cur == null) { _cur = t; t.St = St.Running; Note($"start {t.Name}"); return t; }
        // arrived while someone else holds the token: park as ready (a spawned thread's first point)
        t.St = St.Ready; if (_pending > 0) _pending--; _pendingName = null;
        Monitor.PulseAll(_g);
        _arrived.Add(t);   // (noted by the next decision, after every pending arrival: the arrival races the holder's own notes)
        return t;
    }

    void Note(string s)
    {
        foreach (char c in s) TraceHash = (TraceHash ^ c) * 1099511628211UL;
        if (KeepTrace && Trace.Count < 200_000) Trace.Add($"{Steps}\t{s}");
    }

    /// <summary>The holder's decision: who runs next. Called with _g held; returns the chosen task (may be the caller).</summary>
    T Choose(T me)
    {
        // a spawned thread that has not reached its first point yet must be in the model before anyone chooses
        long t0 = Environment.TickCount64;
        while (_pending > 0)
        {
            if (!Monitor.Wait(_g, 1000) && Environment.TickCount64 - t0 > 60_000) { Fail($"stall: a spawned thread never reached its first point ({_pendingName})"); _pending = 0; }
        }
        if (_arrived.Count > 0) { _arrived.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name)); foreach (var a in _arrived) Note($"arrive {a.Name} prio {a.Prio}"); _arrived.Clear(); }
        Steps++; Decisions++;
        if (_nextChange < _change.Length && Steps >= _change[_nextChange]) { me.Prio = _nextChange; Note($"change {me.Name} -> {_nextChange}"); _nextChange++; }
        // runnable threads and short timed waits (<= ShortWaitMs: a timeout the hardware really produces, e.g. the read-ahead's 25 ms
        // bound) compete by priority; a long timed wait (the render thread's 1 s / 9 s shutdown waits) fires only when nothing else can run
        T best = null;
        foreach (var t in _all)
        {
            bool eligible = t.St is St.Ready or St.Running || ((t.St is St.WaitMon or St.WaitEv) && t.Timed && t.TimeoutMs <= ShortWaitMs);
            if (eligible && (best == null || t.Prio > best.Prio)) best = t;
        }
        // a timeout is an event, not a thread: when the top candidate is a timed waiter and some thread can run, a seeded coin decides
        // whether the timeout fires now or that thread runs first (both orders happen on hardware; pure priority order would make the
        // waiter's timeout win every time it outranks the thread it waits for)
        if (best != null && (best.St == St.WaitMon || best.St == St.WaitEv) && _rng.Int(100) >= TimeoutChancePct)
        {
            T run = null; foreach (var t in _all) if ((t.St == St.Ready || t.St == St.Running) && (run == null || t.Prio > run.Prio)) run = t;
            if (run != null) best = run;
        }
        if (best == null)
            foreach (var t in _all) if ((t.St is St.WaitMon or St.WaitEv) && t.Timed && (best == null || t.Prio > best.Prio)) best = t;
        if (best == null)
        {
            Fail("deadlock: no runnable thread and no timed waiter -- " + string.Join("; ", _all.Select(x => x.ToString())));
            ReleaseAll();
            return me;   // the rest of the run is unscheduled: the failure is recorded, every parked thread continues
        }
        if (best.St is St.WaitMon or St.WaitEv) { best.TimedOut = true; best.St = St.Ready; Timeouts++; Note($"timeout {best.Name}"); }
        return best;
    }

    void Fail(string why) { if (Failure == null) { Failure = $"seed {Seed} d {Depth} k {K} step {Steps}: {why}"; Console.Error.WriteLine("[pct] FAIL " + Failure); } }

    /// <summary>Hand the token to `next` (unless it is me) and park until it comes back.</summary>
    void SwitchTo(T me, T next)
    {
        if (next == me) { me.St = St.Running; return; }
        Switches++; Note($"{me.Name} -> {next.Name}");
        next.St = St.Running; _cur = next;
        next.Go.Release();
        Monitor.Exit(_g);
        try { me.Go.Wait(); }
        finally { Monitor.Enter(_g); }
        me.St = St.Running; _cur = me;
    }

    bool Unscheduled => Failure != null && Failure.Contains("deadlock");

    public void Point(int site)
    {
        lock (_g)
        {
            var me = Me(); if (Unscheduled) return;
            if (me.St != St.Running) Park(me);   // (a freshly arrived thread waits for its first turn)
            me.St = St.Ready; Note($"point {site} {me.Name}");
            SwitchTo(me, Choose(me));
        }
    }
    /// <summary>A thread that is not the holder (just arrived): wait for the token.</summary>
    void Park(T me)
    {
        Monitor.Exit(_g);
        try { me.Go.Wait(); } finally { Monitor.Enter(_g); }
        me.St = St.Running; _cur = me;
    }
    public void Spawn(string name) { lock (_g) { var me = Me(); _pending++; _pendingName = name; Note($"spawn {name} by {me.Name}"); } }
    public void Leave()
    {
        lock (_g)
        {
            var me = Me(); me.St = St.Done; Note($"leave {me.Name}");
            if (Unscheduled || _cur != me) return;
            var next = Choose(me);
            if (next != me) { Switches++; next.St = St.Running; _cur = next; next.Go.Release(); } else _cur = null;
        }
    }
    public bool Wait(object m, int timeoutMs)
    {
        lock (_g)
        {
            var me = Me();
            if (Unscheduled) return timeoutMs < 0 ? Monitor.Wait(m) : Monitor.Wait(m, timeoutMs);
            if (me.St != St.Running) Park(me);
            me.St = St.WaitMon; me.On = m; me.Timed = timeoutMs >= 0; me.TimeoutMs = timeoutMs; me.TimedOut = false; Note($"wait {me.Name}{(me.Timed ? " timed" : "")}");
            Monitor.Exit(m);
            try { SwitchTo(me, Choose(me)); }
            finally { Monitor.Exit(_g); Monitor.Enter(m); Monitor.Enter(_g); }
            me.On = null; bool timedOut = me.TimedOut; me.TimedOut = false; me.Timed = false;
            return !timedOut;
        }
    }
    public void PulseAll(object m)
    {
        lock (_g)
        {
            var me = Me(); if (Unscheduled) { Monitor.PulseAll(m); return; }
            foreach (var t in _all) if (t.St == St.WaitMon && ReferenceEquals(t.On, m)) { t.St = St.Ready; t.On = null; Note($"pulse {t.Name}"); }
        }
    }
    public bool EvWait(ManualResetEventSlim ev, int timeoutMs)
    {
        lock (_g)
        {
            var me = Me();
            if (Unscheduled) { Monitor.Exit(_g); try { if (timeoutMs < 0) { ev.Wait(); return true; } return ev.Wait(timeoutMs); } finally { Monitor.Enter(_g); } }
            if (me.St != St.Running) Park(me);
            while (true)
            {
                if (ev.IsSet) { me.St = St.Ready; Note($"evpass {me.Name}"); SwitchTo(me, Choose(me)); return true; }
                me.St = St.WaitEv; me.On = ev; me.Timed = timeoutMs >= 0; me.TimeoutMs = timeoutMs; me.TimedOut = false; Note($"evwait {me.Name}{(me.Timed ? " timed" : "")}");
                SwitchTo(me, Choose(me));
                me.On = null; me.Timed = false;
                if (me.TimedOut) { me.TimedOut = false; return ev.IsSet; }
                if (ev.IsSet) return true;   // woken by a Set (and not reset since): through
            }
        }
    }
    public void EvSet(ManualResetEventSlim ev)
    {
        lock (_g)
        {
            var me = Me(); ev.Set(); if (Unscheduled) return;
            foreach (var t in _all) if (t.St == St.WaitEv && ReferenceEquals(t.On, ev)) { t.St = St.Ready; Note($"set {t.Name}"); }
        }
    }
    public void EvReset(ManualResetEventSlim ev) { lock (_g) { Me(); ev.Reset(); } }

    /// <summary>The end of a test: the calling thread (the holder) gives every other participant the token until each has left (its
    /// priority drops below all others, so PCT picks them first); a thread that cannot finish is a failure.</summary>
    public void Drain()
    {
        T me; lock (_g) { me = Me(); _nextChange = _change.Length; }   // (no more change points: one could lift the drainer above the others again)
        for (int i = 0; i < 100_000; i++)
        {
            lock (_g) { me.Prio = int.MinValue; if (_all.All(t => t == me || t.St == St.Done) || Unscheduled) return; }
            Point(-1);
        }
        Fail("drain: a participant never left: " + string.Join("; ", _all.Select(x => x.ToString())));
    }

    public string Summary() => $"[pct] seed {Seed} d {Depth} k {K}: {Steps} steps, {Switches} switches, {Timeouts} timeouts chosen, {_all.Count} threads ({string.Join(", ", _all.Select(t => t.Name))}), trace {TraceHash:x16}{(Failure != null ? " FAIL " + Failure : "")}";
}
