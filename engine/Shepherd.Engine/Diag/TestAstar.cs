using System.Reflection;
namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>Kit #7 tests track (owner decision 2026-10-20: "deterministic A* path timing YES for test runs only; gameplay keeps the
/// original's threaded timing"; vita/notes/kit7-tests.md "Deterministic A* path timing"). The game's pathfinding (AstarPathfindingProject
/// 4.2.17, AstarPath.threadCount = One) computes paths on its own thread and hands them back in AstarPath.Update on whatever frame the
/// thread happens to finish -- host scheduling decides, so a drive with pathing enemies can diverge run to run (the physics combat gate's
/// flake: physics-stage3.md section 5). SHEPHERD_TEST_ASTAR (Mac test runs only: not in the eboots, and refused under the play clock):
///   join    the path thread works only BETWEEN frames: before each frame's Update loop the main thread releases the switch's pause,
///           waits until the path thread has finished every path asked for so far and sits idle in its queue (ThreadControlQueue: every
///           receiver parked, nothing it may take), then pauses pathfinding again for the rest of the frame (AstarPath's own
///           PausePathfinding lock, the one its graph updates use). So a path asked for in frame N is computed before frame N+1 and comes
///           back in frame N+1's AstarPath.Update -- one frame of latency always (a schedule the original can produce: a path thread that
///           takes the whole frame), independent of host timing. (A first cut that only waited for idle at the frame start left the
///           paths asked for DURING the frame racing AstarPath.Update later in the same frame: 1 of 4 combat drives still diverged.)
///           AstarPath.Update's own ReturnPaths(timeSlice) then returns them as always; it stops after 6+ paths once 1 ms of wall time
///           has passed, so the frames where more than 6 paths were waiting are counted ("time slice exposure") -- non-zero: use 'drain'.
///           The game never blocks on a path (no BlockUntilCalculated / WaitForPath in its code), so the pause cannot stall it
///   drain   join, then the waiting paths are returned right there, all of them (ReturnPaths(false), the call AstarPath itself makes in
///           PerformBlockingActions), before the Update loop: fully deterministic whatever the count
///   hold:&lt;seed&gt;[:&lt;max&gt;]  join, then a seeded schedule holds each finished path back 0..max frames (default 3) before it may return
///           (removed from the return queue and put back later, in the order they finished): one seed = one schedule of a SLOW path
///           thread the original could also have produced. Different seeds = different schedules; the same seed replays exactly
///           (vita/tests/pct/astar-seeds.sh: what the combat flake is)
/// Off (unset / 0): nothing is touched (no reflection, no wait). One [test-astar] summary line at the end.</summary>
[ProfileOnly]
public static class TestAstar
{
    static readonly string Mode = Parse(Environment.GetEnvironmentVariable("SHEPHERD_TEST_ASTAR"), out HoldSeed, out HoldMax);
    static readonly int HoldSeed, HoldMax;
    public static readonly bool On = Mode != null;
    static bool _refused, _bound, _bindFailed;
    static Type _astarT;
    static FieldInfo _active, _proc, _queue, _returnQ, _rqList, _qLock, _qHead, _qBlockedFlag, _qBlocked, _qReceivers;
    static MethodInfo _returnPaths, _pause, _release;
    static object _lock;   // the boxed PathProcessor.GraphUpdateLock this switch holds during a frame (null between frames)
    static object _lockProc;   // the PathProcessor it was taken on (a new AstarPath / processor: the old lock is dropped)
    static long _frames, _joins, _waitSpins, _returned, _exposed, _held, _maxWaiting; static double _waitMs;
    static System.Random _hold;
    static readonly List<(object path, int until)> _heldPaths = new();

    static string Parse(string s, out int seed, out int max)
    {
        seed = 0; max = 3;
        if (string.IsNullOrEmpty(s) || s == "0" || s == "off") return null;
        if (s == "join" || s == "drain") return s;
        if (s.StartsWith("hold:"))
        {
            var p = s.Split(':'); if (p.Length > 1) int.TryParse(p[1], out seed); if (p.Length > 2) int.TryParse(p[2], out max);
            return "hold";
        }
        Log.Warn($"[test-astar] SHEPHERD_TEST_ASTAR='{s}' not understood (join | drain | hold:<seed>[:<max>]): off");
        return null;
    }

    /// <summary>Lifecycle.Frame, right before the Update loop.</summary>
    public static void BeforeUpdates() { if (On) Run(); }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Run()
    {
        if (_refused) return;
        if (Runtime.Time.Wall) { _refused = true; Log.Warn("[test-astar] refused: the play clock is gameplay (the original's threaded path timing is kept); SHEPHERD_TEST_ASTAR is for frame-clock test runs"); return; }
        _frames++;
        if (!_bound && !Bind()) return;
        var astar = _active.GetValue(null);
        if (astar == null || (astar is UnityEngine.Object uo && uo == null)) return;
        var proc = _proc.GetValue(astar); if (proc == null) return;
        var q = _queue.GetValue(proc); var rq = _returnQ.GetValue(astar); if (q == null || rq == null) return;
        // the previous frame's pause ends: the path thread may now take what was asked for during that frame
        if (_lock != null) { if (ReferenceEquals(_lockProc, proc)) _release.Invoke(_lock, null); _lock = null; _lockProc = null; }
        // join: the path thread has taken every queued path and is parked waiting for more
        var lk = _qLock.GetValue(q); long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); int spins = 0;
        while (true)
        {
            bool idle;
            // every receiver parked in Pop's wait loop, and nothing left it could take (an empty queue, or pathfinding paused by the main
            // thread's work items: blocked); a receiver just woken by a Push still counts as parked but sees head != null and !blocked
            lock (lk) idle = (int)_qBlocked.GetValue(q) >= (int)_qReceivers.GetValue(q) && (_qHead.GetValue(q) == null || (bool)_qBlockedFlag.GetValue(q));
            if (idle) break;
            if (++spins > 20_000_000) { Log.Warn("[test-astar] the path thread did not go idle (20M spins): left as it is this frame"); break; }
            Thread.Yield();
        }
        _joins++; _waitSpins += spins; _waitMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // and pauses again until the next frame starts (block: false -- it is idle already)
        _lock = _pause.Invoke(proc, new object[] { false }); _lockProc = proc;
        var queue = _rqList.GetValue(rq) as System.Collections.ICollection;
        int waiting = queue?.Count ?? 0;
        if (Mode == "hold") waiting = Hold(rq, queue);
        if (waiting > _maxWaiting) _maxWaiting = waiting;
        if (Mode == "drain") { if (waiting > 0) { _returnPaths.Invoke(rq, new object[] { false }); _returned += waiting; } }
        else { _returned += waiting; if (waiting > 6) _exposed++; }
    }

    /// <summary>hold:&lt;seed&gt;: the finished paths leave the return queue and come back after their seeded number of frames, in the order
    /// they finished (a path held longer keeps the ones behind it waiting too: one thread finishes them in order).</summary>
    static int Hold(object rq, System.Collections.ICollection queue)
    {
        _hold ??= new System.Random(HoldSeed);
        var dq = queue.GetType().GetMethod("Dequeue"); var enq = queue.GetType().GetMethod("Enqueue");
        lock (queue)
        {
            while (queue.Count > 0) { var p = dq.Invoke(queue, null); _heldPaths.Add((p, (int)_frames + _hold.Next(0, HoldMax + 1))); _held++; }
            int n = 0;
            while (_heldPaths.Count > 0 && _heldPaths[0].until <= _frames) { enq.Invoke(queue, new[] { _heldPaths[0].path }); _heldPaths.RemoveAt(0); n++; }
            return n;
        }
    }

    static bool Bind()
    {
        if (_bindFailed) return false;
        _astarT ??= AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("AstarPath", false)).FirstOrDefault(t => t != null);
        if (_astarT == null) return false;   // not loaded yet (no scene with pathfinding so far): try again next frame
        const BindingFlags A = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            _active = _astarT.GetField("active", A);
            _proc = _astarT.GetField("pathProcessor", A);
            _returnQ = _astarT.GetField("pathReturnQueue", A);
            var procT = _proc.FieldType; _queue = procT.GetField("queue", A);
            var qT = _queue.FieldType;
            _qLock = qT.GetField("lockObj", A); _qHead = qT.GetField("head", A); _qBlockedFlag = qT.GetField("blocked", A);
            _qBlocked = qT.GetField("blockedReceivers", A); _qReceivers = qT.GetField("numReceivers", A);
            var rqT = _returnQ.FieldType; _rqList = rqT.GetField("pathReturnQueue", A); _returnPaths = rqT.GetMethod("ReturnPaths", A);
            _pause = procT.GetMethod("PausePathfinding", A, null, new[] { typeof(bool) }, null);
            _release = _pause?.ReturnType.GetMethod("Release", A);
            if (new object[] { _active, _proc, _returnQ, _queue, _qLock, _qHead, _qBlockedFlag, _qBlocked, _qReceivers, _rqList, _returnPaths, _pause, _release }.Any(x => x == null))
                throw new MissingMemberException("an AstarPath member the switch needs is missing (not A* 4.2.17?)");
            _bound = true;
            Log.Info($"[test-astar] {Mode}{(Mode == "hold" ? $" seed {HoldSeed} max {HoldMax} frames" : "")}: bound to AstarPath {_astarT.Assembly.GetName().Version} (A* {(_astarT.GetField("Version", A)?.GetValue(null))})");
            return true;
        }
        catch (Exception e) { _bindFailed = true; Log.Warn($"[test-astar] off: {e.Message}"); return false; }
    }

    public static string Summary() => !On ? null : _refused ? "[test-astar] refused (play clock)" :
        $"[test-astar] {Mode}{(Mode == "hold" ? $" seed {HoldSeed}" : "")}: {(_bound ? "bound" : "AstarPath never loaded")}; {_frames} frames, {_joins} joins ({_waitMs:F1} ms waiting, {_waitSpins} yields), {_returned} paths returned at the frame start or handed to AstarPath.Update, at most {_maxWaiting} waiting in one frame; time slice exposure {_exposed} frames (> 6 paths waiting){(Mode == "hold" ? $"; {_held} paths held back" : "")}";
}
#else
/// <summary>The eboots (SHEPHERD_EBOOT): the kit #7 Mac test switches are not built in; one end line says so if one was set.</summary>
[ProfileOnly]
public static class TestAstar
{
    public static string Summary()
    {
        string set = null;
        foreach (var v in new[] { "SHEPHERD_TEST_ASTAR", "SHEPHERD_TEST_SPINE", "SHEPHERD_STATE_HASH" }) if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } x && x != "0") set = set == null ? v : set + ", " + v;
        return set == null ? null : "[test] " + set + " ignored: Mac test switches, not in this eboot (gameplay keeps the original's threaded path timing; SHEPHERD_ENGINE_TOOLS=1 builds keep them)";
    }
}
#endif
