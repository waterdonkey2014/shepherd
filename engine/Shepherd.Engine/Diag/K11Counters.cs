#if !SHEPHERD_EBOOT
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #11, track measure: ENGINE COUNTERS the later kits need (roadmap kit #11 experimental track + kit #13's caching / task
/// notes), each logged per frame (k11-counters.tsv) and summarised ([k11] counters lines, k11-exceptions.tsv):
/// <list type="bullet">
/// <item><b>random draws per frame</b> -- UnityEngine.Random calls (the engine's Random icalls; the Mac build counts them for the state hash:
/// Icalls/AppIcalls.cs RngCalls); kit #13's "keep the every-frame rollers" and caching rules read this.</item>
/// <item><b>followers re-deciding tasks</b> -- FollowerBrain.AllBrains read at the end of every frame: the population, the brains still
/// flagged ShouldReconsiderTask (TimeManager.Simulate's reconsider pass clears the flags it handles in the same call, so a flag seen at a
/// frame boundary is one that pass did not take), and the brains whose CurrentTask object changed since the last frame (every re-decision
/// that hands out a task makes a new task object; a forced swap counts too) -- the re-decide frequency kit #13's exact re-decide scan
/// wants. The exact per-pass count needs a probe inside Simulate (not built: it is game code).</item>
/// <item><b>exceptions thrown and caught, per method</b> -- every first-chance exception (the throwing method and type, on the frame it
/// was thrown) paired with the runtime's catch event (the method whose handler caught it: the game's own try / catch, or the engine's
/// per-callback catch); a throw costs far more on the Vita than on a PC, so each frequent one is a candidate for an exact fix.</item>
/// </list>
/// Reads only: no game code runs (the brains' properties are auto-property getters and fields; reading FollowerBrain's static list may
/// run its type initializer early -- collection allocations only, from the game's decomp -- the fixtures run with this on to show no
/// frame moves). Mac only (CoreCLR's runtime events; not in the eboots). Switch: <c>SHEPHERD_K11_COUNTERS=1</c> (all) or a list of
/// rng / redecide / exc; unset = off.</summary>
[ProfileOnly]
public static class K11Counters
{
    static readonly string Spec = K11Measure.Master ? (Environment.GetEnvironmentVariable("SHEPHERD_K11_COUNTERS") ?? "").Trim() : "";
    static readonly string[] Parts = Spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static readonly bool On = Spec.Length > 0 && Spec != "0";
    static readonly bool All = Parts.Contains("1") || Parts.Contains("all");
    static readonly bool Rng = On && (All || Parts.Contains("rng") || Parts.Contains("rngsites")), Redecide = On && (All || Parts.Contains("redecide")), Exc = On && (All || Parts.Contains("exc"));
    /// <summary>SHEPHERD_K11_COUNTERS=...,rngsites: each random draw's calling method (the first frame outside the engine and Unity's
    /// modules), counted per method -> k11-rng-sites.tsv (a stack walk per draw: slow, opt-in).</summary>
    public static readonly bool RngSites = On && Parts.Contains("rngsites");
    public static string Describe() => On ? string.Join("+", new[] { Rng ? "rng" : null, Redecide ? "redecide" : null, Exc ? "exc" : null }.Where(x => x != null)) : "off";

    struct FrameRow { public int Frame; public string Scene; public long Rng; public int Brains, Flagged, TaskChanges, Thrown; }
    static readonly List<FrameRow> _rows = new();
    static long _rng0;
    static int _frame;

    public static void Start()
    {
        if (!On) return;
        if (Exc)
        {
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            try { _listener = new RuntimeEvents(); } catch (Exception e) { Log.Warn($"[k11] counters: no runtime exception events ({e.Message}): catches not attributed"); }
        }
    }

    public static void Before(int frame)
    {
        _frame = frame;
        if (Rng) _rng0 = Impl.AppIcalls.RngCalls;
    }

    public static void After(int frame)
    {
        var r = new FrameRow { Frame = frame, Scene = K11Measure.Scene };
        if (Rng) r.Rng = Impl.AppIcalls.RngCalls - _rng0;
        if (Redecide) SampleBrains(ref r);
        if (Exc) r.Thrown = Interlocked.Exchange(ref _thrownThisFrame, 0);
        _rows.Add(r);
    }

    // ---------------------------------------------------------------- random draws by calling method (opt-in)
    sealed class Site { public long N; public int First = int.MaxValue, Last, Frames, LastFrame = -1; }
    static readonly Dictionary<string, Site> _sites = new();
    public static void RngSite()
    {
        string who = "?";
        try
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            for (int i = 0; i < st.FrameCount; i++)
            {
                var m = st.GetFrame(i)?.GetMethod(); var t = m?.DeclaringType; if (t == null) continue;
                var a = t.Assembly.GetName().Name ?? "";
                if (a.StartsWith("Shepherd.") || a.StartsWith("UnityEngine") || a.StartsWith("System") || a == "mscorlib" || a == "netstandard") continue;
                who = $"{t.FullName}::{m.Name}"; break;
            }
        }
        catch { }
        if (!_sites.TryGetValue(who, out var s)) _sites[who] = s = new Site();
        s.N++; if (_frame < s.First) s.First = _frame; if (_frame > s.Last) s.Last = _frame; if (s.LastFrame != _frame) { s.Frames++; s.LastFrame = _frame; }
    }

    // ---------------------------------------------------------------- followers (reflection over the game's FollowerBrain)
    static bool _brainInit; static FieldInfo _allBrains, _curTask; static PropertyInfo _flag;
    static readonly Dictionary<object, object> _lastTask = new(ReferenceEqualityComparer.Instance);
    static void SampleBrains(ref FrameRow r)
    {
        if (!_brainInit)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null) return;   // (not loaded yet: try again next frame)
            _brainInit = true;
            var t = asm.GetType("FollowerBrain");
            if (t == null) { Log.Warn("[k11] counters: no FollowerBrain type -- re-decide counters off"); return; }
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _allBrains = t.GetField("AllBrains", S); _flag = t.GetProperty("ShouldReconsiderTask", I); _curTask = t.GetField("_currentTask", I);
            if (_allBrains == null || _flag == null || _curTask == null) Log.Warn($"[k11] counters: FollowerBrain members not found (AllBrains {_allBrains != null}, ShouldReconsiderTask {_flag != null}, _currentTask {_curTask != null})");
        }
        if (_allBrains == null || _flag == null) return;
        if (_allBrains.GetValue(null) is not IList list) return;
        int n = list.Count, flagged = 0, changed = 0;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++)
        {
            var b = list[i]; if (b == null) continue; seen.Add(b);
            if (_flag.GetValue(b) is true) flagged++;
            if (_curTask != null)
            {
                var task = _curTask.GetValue(b);
                if (_lastTask.TryGetValue(b, out var old) && !ReferenceEquals(old, task)) changed++;
                _lastTask[b] = task;
            }
        }
        if (_lastTask.Count > seen.Count) foreach (var k in _lastTask.Keys.Where(k => !seen.Contains(k)).ToList()) _lastTask.Remove(k);
        r.Brains = n; r.Flagged = flagged; r.TaskChanges = changed;
    }

    // ---------------------------------------------------------------- exceptions
    static int _thrownThisFrame;
    sealed class ExcStat { public long Thrown, Caught; public int First = int.MaxValue, Last; public readonly Dictionary<string, long> Catchers = new(); }
    static readonly ConcurrentDictionary<string, ExcStat> _exc = new();
    static readonly ConcurrentDictionary<ulong, ConcurrentQueue<(string key, int frame)>> _byThread = new();
    static readonly ConcurrentDictionary<ulong, (string key, int frame)> _current = new();
    static long _firstChance, _throwEvents, _catchEvents, _unpaired;
    static readonly int[] _caughtPerFrame = new int[1 << 16];
    static RuntimeEvents _listener;
    [ThreadStatic] static bool _inHandler;

    [DllImport("libSystem.dylib")] static extern int pthread_threadid_np(IntPtr thread, out ulong id);
    static ulong OsTid() { try { return OperatingSystem.IsMacOS() && pthread_threadid_np(IntPtr.Zero, out var id) == 0 ? id : (ulong)Environment.CurrentManagedThreadId; } catch { return (ulong)Environment.CurrentManagedThreadId; } }

    [ThreadStatic] static bool _inListener;
    static void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (_inHandler || _inListener) return;
        _inHandler = true;
        try
        {
            var ex = e.Exception; var m = ex.TargetSite;
            string thrower = m == null ? "?" : $"{m.DeclaringType?.FullName ?? "?"}::{m.Name}";
            string key = $"{thrower}\t{ex.GetType().FullName}";
            int f = _frame;
            var st = _exc.GetOrAdd(key, _ => new ExcStat());
            lock (st) { st.Thrown++; if (f < st.First) st.First = f; if (f > st.Last) st.Last = f; }
            Interlocked.Increment(ref _firstChance);
            if (Environment.CurrentManagedThreadId == _mainThread) Interlocked.Increment(ref _thrownThisFrame);
            if (_listener != null) _byThread.GetOrAdd(OsTid(), _ => new ConcurrentQueue<(string, int)>()).Enqueue((key, f));
        }
        catch { }
        finally { _inHandler = false; }
    }
    static readonly int _mainThread = Environment.CurrentManagedThreadId;

    /// <summary>The runtime's own exception events (thrown, catch-start with the catching method), delivered in order per thread on the
    /// event dispatch thread a little later.</summary>
    sealed class RuntimeEvents : EventListener
    {
        protected override void OnEventSourceCreated(EventSource s) { if (s.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(s, EventLevel.Informational, (EventKeywords)0x8000); }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            _inListener = true;
            try
            {
                var n = e.EventName; if (n == null) return;
                ulong tid = (ulong)e.OSThreadId;
                if (n == "ExceptionThrown_V1")
                {
                    Interlocked.Increment(ref _throwEvents);
                    if (_byThread.TryGetValue(tid, out var q) && q.TryDequeue(out var k)) _current[tid] = k; else { _current.TryRemove(tid, out _); Interlocked.Increment(ref _unpaired); }
                }
                else if (n == "ExceptionCatchStart")
                {
                    if (e.Payload == null || e.Payload.Count < 3) return;
                    var catcher = e.Payload[2] as string ?? "?";
                    if (catcher.Contains("K11Counters")) return;   // (our own handlers: never counted)
                    Interlocked.Increment(ref _catchEvents);
                    if (!_current.TryRemove(tid, out var k)) return;
                    var st = _exc.GetOrAdd(k.key, _ => new ExcStat());
                    lock (st) { st.Caught++; st.Catchers[catcher] = (st.Catchers.TryGetValue(catcher, out var c) ? c : 0) + 1; }
                    if (k.frame >= 0 && k.frame < _caughtPerFrame.Length) Interlocked.Increment(ref _caughtPerFrame[k.frame]);
                }
            }
            catch { }
            finally { _inListener = false; }
        }
    }

    // ---------------------------------------------------------------- the end
    public static void End()
    {
        if (!On) return;
        if (Exc && _listener != null)
        {   // the runtime's events arrive a little later: wait (at most 3 s) until every throw has its event
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Interlocked.Read(ref _throwEvents) < Interlocked.Read(ref _firstChance) && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }
        using (var w = new StreamWriter(K11Measure.OutFile("k11-counters.tsv"), false))
        {
            w.WriteLine("frame\tscene\trng_draws\tbrains\tflagged_at_frame_end\ttask_changes\texc_thrown_main\texc_caught");
            foreach (var r in _rows) w.WriteLine($"{r.Frame}\t{r.Scene}\t{(Rng ? r.Rng : -1)}\t{(Redecide ? r.Brains : -1)}\t{(Redecide ? r.Flagged : -1)}\t{(Redecide ? r.TaskChanges : -1)}\t{(Exc ? r.Thrown : -1)}\t{(Exc && _listener != null && r.Frame < _caughtPerFrame.Length ? _caughtPerFrame[r.Frame] : -1)}");
        }
        // per scene
        foreach (var g in _rows.GroupBy(r => r.Scene))
        {
            var rs = g.ToList(); int n = rs.Count;
            string rng = Rng ? $"random draws {rs.Sum(r => r.Rng)} (per frame mean {rs.Average(r => r.Rng):F2}, max {rs.Max(r => r.Rng)}, frames with any {rs.Count(r => r.Rng > 0)})" : "";
            string fol = Redecide ? $"; followers max {rs.Max(r => r.Brains)}, flagged at frame end in {rs.Count(r => r.Flagged > 0)} frames (max {rs.Max(r => r.Flagged)}), task changes {rs.Sum(r => r.TaskChanges)} in {rs.Count(r => r.TaskChanges > 0)} frames" : "";
            string exc = Exc ? $"; exceptions thrown (main) {rs.Sum(r => r.Thrown)} in {rs.Count(r => r.Thrown > 0)} frames" : "";
            Log.Info($"[k11] counters '{g.Key}': {n} frames, {rng}{fol}{exc}");
        }
        if (RngSites)
        {
            using var w = new StreamWriter(K11Measure.OutFile("k11-rng-sites.tsv"), false);
            w.WriteLine("caller\tdraws\tframes_with\tfirst_frame\tlast_frame");
            foreach (var (k, st) in _sites.OrderByDescending(kv => kv.Value.N)) w.WriteLine($"{k}\t{st.N}\t{st.Frames}\t{st.First}\t{st.Last}");
            Log.Info($"[k11] random draws by caller: {_sites.Count} methods; top: " + string.Join("; ", _sites.OrderByDescending(kv => kv.Value.N).Take(5).Select(kv => $"{kv.Key} x{kv.Value.N} in {kv.Value.Frames} frames")));
        }
        if (Exc)
        {
            using var w = new StreamWriter(K11Measure.OutFile("k11-exceptions.tsv"), false);
            w.WriteLine("thrower\ttype\tthrown\tcaught\tfirst_frame\tlast_frame\tcatchers(method:count)");
            foreach (var (k, st) in _exc.OrderByDescending(kv => kv.Value.Thrown))
                w.WriteLine($"{k}\t{st.Thrown}\t{st.Caught}\t{st.First}\t{st.Last}\t{string.Join(" | ", st.Catchers.OrderByDescending(c => c.Value).Select(c => $"{c.Key}:{c.Value}"))}");
            Log.Info($"[k11] exceptions: {_firstChance} thrown ({_exc.Count} thrower/type pairs), runtime events: {_throwEvents} thrown / {_catchEvents} catch starts{(_unpaired > 0 ? $", {_unpaired} unpaired" : "")}{(_listener == null ? " (no runtime events: catches not attributed)" : "")}; top: " +
                     string.Join("; ", _exc.OrderByDescending(kv => kv.Value.Thrown).Take(5).Select(kv => $"{kv.Key.Replace('\t', ' ')} x{kv.Value.Thrown}")));
        }
        _listener?.Dispose();
    }
}
#endif
