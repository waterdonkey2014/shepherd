using System.Reflection;
namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>Kit #7 tests track (vita/notes/kit7-tests.md "The combat gate flake explained"): the game's second worker thread. The game's
/// spine-unity (Massive Monster's build) updates skeletons on a background thread: SkeletonAnimation.Update (GlobalMultithread, on by
/// default) hands ProcessFrame -- skeleton.Update, AnimationState.Update / Apply, UpdateWorldTransform, and for visible skeletons the mesh --
/// to DOGames.Threading's DispatchThread (one thread spinning on a ConcurrentQueue) and only BaseLateUpdate waits for it. Everything the
/// main thread does in between -- the rest of the Update loop, coroutines, a second Update before the first finished ("ignoring attempt
/// to queue a skeleton on thread more than once"), bone readers (SkeletonUtilityBone, hitboxes, attachment points) -- sees the skeleton
/// at whatever point the thread has reached: host timing decides. SHEPHERD_TEST_SPINE (Mac test runs only: not in the eboots, refused
/// under the play clock):
///   join   after every script callback the engine makes (the hook sites: kit7-tests.md 4.2 -- Awake / Start / the phase messages, every
///          coroutine step, Invoke / InvokeRepeating, SendMessage / Broadcast, AnimationEvents, 2D trigger messages, native messages,
///          async completions, the synchronization context's tasks, the render-time and video / bundle callbacks; a callback that throws
///          too) that handed the dispatch thread work, the main thread waits until the thread has run everything queued so far (a
///          sentinel job at the queue's end: one consumer, first in first out). = the schedule "the background thread finishes each job
///          before the main thread runs its next script" -- one the original produces whenever that thread is fast -- independent of
///          host timing; the multithreaded code path itself (state.multiThreaded, the mesh made in ProcessFrame) is the game's, unchanged.
/// Review fixes (kit7-tests.md "Review fixes"):
///   - the GAME creates the thread: DispatchThread's static constructor makes the singleton, starts the thread and logs "Dispatch Thread
///     Created". Reading Instance / _backgroundThreadQueue before the game touched the type ran that constructor at frame 0 (808 frames
///     early on the idle route, the game's Debug.Log before MMLogger's start). Nothing here touches the type until the engine's
///     Debug.Log icall has seen that line (OnGameLog): the constructor runs where the game runs it.
///   - "work was handed" is decided by an enqueue mark (the queue's tail segment + its tail index, read on the main thread, which is the
///     only enqueuer) instead of "the queue is empty and the thread holds no job": the thread's TryDequeue advances the head (IsEmpty is
///     true) before it writes the job into _backgroundThreadAction (still null), so a check inside that window skipped the join while the
///     job ran. Every callback after which the mark moved now joins.
///   - an internal failure of this switch never reaches the game's callback: After() catches it, logs one line and turns the switch off.
/// Off (unset / 0): nothing touched. One [test-spine] summary line at the end.</summary>
[ProfileOnly]
public static class TestSpine
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_TEST_SPINE") is "join";
    static bool _bound, _bindFailed, _refused, _off;
    static volatile bool _gameInit;   // the game's own DispatchThread..cctor ran (its "Dispatch Thread Created" log line reached the engine)
    static int _initFrame = -1, _bindFrame = -1;
    static string _offWhy;
    static Type _dtT, _evT;
    static FieldInfo _queue, _action; static PropertyInfo _instance; static MethodInfo _dispatch;
    // (compiled accessors: the check runs after every script callback, ~2,000 a frame; reflection made the combat drive 3.6x slower)
    static Func<object> _getInstance; static Func<bool> _queueEmpty; static Func<object, object> _getAction; static Action<Delegate> _dispatchD;
    static Func<object> _tailSeg; static Func<object, int> _tailIdx;   // the enqueue mark: ConcurrentQueue<T>._tail, ._headAndTail.Tail
    static object _markSeg; static int _markTail;                        // the mark after the last completed join (null: none yet -> join)
    static object _lastSeen;
    static long _checks, _early, _joins, _markOnly, _spins, _otherThread;
    static int _mainTid = -1;   // the thread of the first check (Lifecycle's: the main thread); a hook reached on another thread is skipped
    static double _waitMs;
    sealed class Sentinel { public volatile bool Done; public void Run() => Done = true; }

    /// <summary>The engine's Debug.Log icall (Icalls/AppIcalls.cs, Mac build): the game's DispatchThread static constructor logs this
    /// line right after it made the singleton and started the thread -- from here on reading the type runs no game code.</summary>
    public static void OnGameLog(string msg)
    {
        if (_gameInit || msg == null || !msg.Contains("Dispatch Thread Created")) return;
        _initFrame = Runtime.Time.FrameCount; _gameInit = true;
    }

    /// <summary>Lifecycle and the other callback sites: after a script callback or a coroutine step. Never throws.</summary>
    public static void After()
    {
        if (!On || _off) return;
        try { Check(); }
        catch (Exception e) { _off = true; _offWhy = $"{e.GetType().Name}: {e.Message}"; Log.Warn($"[test-spine] off after an internal error (the game's callback is not affected): {_offWhy}"); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Check()
    {
        if (_refused) return;
        int tid = Environment.CurrentManagedThreadId;
        if (_mainTid < 0) _mainTid = tid; else if (tid != _mainTid) { _otherThread++; return; }
        if (Runtime.Time.Wall) { _refused = _off = true; Log.Warn("[test-spine] refused: the play clock is gameplay (the game's threaded skeleton timing is kept); SHEPHERD_TEST_SPINE is for frame-clock test runs"); return; }
        if (!_bound)
        {
            if (!_gameInit) { _early++; return; }   // the game has not created its dispatch thread: nothing can be queued, and the type stays untouched
            if (!Bind()) return;
        }
        _checks++;
        var inst = _getInstance(); if (inst == null) return;
        bool enq = true;
        if (_tailSeg != null) { var seg = _tailSeg(); enq = !ReferenceEquals(seg, _markSeg) || _tailIdx(seg) != _markTail; }
        bool queued = !_queueEmpty();
        var act = _getAction(inst);
        bool busy = act != null && !ReferenceEquals(act, _lastSeen);
        // nothing to wait for: nothing enqueued since the last completed join, the queue is empty and the thread holds no job
        if (!enq && !queued && !busy) return;
        if (!queued && !busy) _markOnly++;   // (work was handed but the thread looks idle: done already -- or inside TryDequeue's window)
        var s = new Sentinel(); var del = Delegate.CreateDelegate(_evT, s, typeof(Sentinel).GetMethod(nameof(Sentinel.Run))!);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); int spins = 0;
        _dispatchD(del);
        while (!s.Done)
        {
            if (++spins > 50_000_000) { _off = true; _offWhy = "the dispatch thread did not reach the sentinel (50M yields)"; Log.Warn($"[test-spine] off: {_offWhy}"); return; }
            Thread.Yield();
        }
        _lastSeen = del; _joins++; _spins += spins;
        _waitMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // the mark now covers every job queued so far, the sentinel included (the main thread is the only enqueuer -- the game's jobs,
        // SkeletonAnimation.ProcessFrame, enqueue nothing -- and it was waiting here)
        if (_tailSeg != null) { _markSeg = _tailSeg(); _markTail = _tailIdx(_markSeg); }
    }

    static bool Bind()
    {
        if (_bindFailed) return false;
        _dtT ??= AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "DOGames.Threading").Select(a => a.GetType("DispatchThread", false)).FirstOrDefault(t => t != null);
        if (_dtT == null) { _bindFailed = true; Log.Warn("[test-spine] off: 'Dispatch Thread Created' was logged but DOGames.Threading's DispatchThread is not loaded"); return false; }
        const BindingFlags A = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _queue = _dtT.GetField("_backgroundThreadQueue", A); _action = _dtT.GetField("_backgroundThreadAction", A);
        _instance = _dtT.GetProperty("Instance", A); _dispatch = _dtT.GetMethod("Dispatch", A); _evT = _dtT.GetNestedType("DispatchEvent", A);
        if (_queue == null || _action == null || _instance == null || _dispatch == null || _evT == null) { _bindFailed = true; Log.Warn("[test-spine] off: DispatchThread's members are not the expected ones"); return false; }
        _getInstance = System.Linq.Expressions.Expression.Lambda<Func<object>>(System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Property(null, _instance), typeof(object))).Compile();
        var qf = System.Linq.Expressions.Expression.Field(null, _queue);
        _queueEmpty = System.Linq.Expressions.Expression.Lambda<Func<bool>>(System.Linq.Expressions.Expression.Property(qf, "IsEmpty")).Compile();
        var p = System.Linq.Expressions.Expression.Parameter(typeof(object));
        _getAction = System.Linq.Expressions.Expression.Lambda<Func<object, object>>(System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(p, _dtT), _action), typeof(object)), p).Compile();
        var d = System.Linq.Expressions.Expression.Parameter(typeof(Delegate));
        _dispatchD = System.Linq.Expressions.Expression.Lambda<Action<Delegate>>(System.Linq.Expressions.Expression.Call(_dispatch, System.Linq.Expressions.Expression.Convert(d, _evT)), d).Compile();
        // the enqueue mark (System.Private.CoreLib internals of .NET 8's ConcurrentQueue<T>: _tail (the segment enqueues go to) and its
        // _headAndTail.Tail (+1 per enqueue; a new segment when one fills; + FreezeOffset when one is frozen) -- any change = an enqueue
        // (or a freeze: an extra join, harmless). Not there (another runtime): every check joins, slower but never skipping.
        string markWhy = null;
        try
        {
            var qt = _queue.FieldType; var tailF = qt.GetField("_tail", BindingFlags.Instance | BindingFlags.NonPublic);
            var segT = tailF?.FieldType; var hatF = segT?.GetField("_headAndTail", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var tF = hatF?.FieldType.GetField("Tail", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (tailF == null || hatF == null || tF == null || tF.FieldType != typeof(int)) markWhy = "ConcurrentQueue's _tail / _headAndTail.Tail not found";
            else
            {
                var ts = System.Linq.Expressions.Expression.Lambda<Func<object>>(System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(qf, tailF), typeof(object))).Compile();
                var sp = System.Linq.Expressions.Expression.Parameter(typeof(object));
                var ti = System.Linq.Expressions.Expression.Lambda<Func<object, int>>(System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(sp, segT), hatF), tF), sp).Compile();
                _ = ti(ts());   // (proves both run: a visibility refusal throws here, not in a game callback)
                _tailSeg = ts; _tailIdx = ti;
            }
        }
        catch (Exception e) { markWhy = $"{e.GetType().Name}: {e.Message}"; _tailSeg = null; _tailIdx = null; }
        _bound = true; _bindFrame = Runtime.Time.FrameCount;
        Log.Info($"[test-spine] join: bound to DOGames.Threading DispatchThread ({_dtT.Assembly.GetName().Version}) after the game created it (its line at frame {_initFrame})" +
                 (markWhy == null ? "; enqueue mark: ConcurrentQueue tail" : $"; NO enqueue mark ({markWhy}): every callback joins"));
        return true;
    }

    public static string Summary() => !On ? null : _refused ? "[test-spine] refused (play clock)" :
        $"[test-spine] join: {(_bound ? $"bound at frame {_bindFrame} (the game created its DispatchThread at frame {_initFrame})" : _gameInit ? "not bound" : "the game never created its DispatchThread")}" +
        $"{(_off && _offWhy != null ? $"; OFF: {_offWhy}" : "")}; {_checks} callbacks checked ({_early} before the thread existed), {_joins} joins ({_markOnly} seen only by the enqueue mark; {_waitMs:F1} ms waiting, {_spins} yields){(_otherThread > 0 ? $"; {_otherThread} hooks on another thread skipped" : "")}";
}
#endif
