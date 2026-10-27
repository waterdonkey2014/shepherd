using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #7, track measure: the HOT-PATH PROFILER (preloader design section 7: first-call trace + safepoint-free sampling +
/// counters) and the LIVE FRAME TIMELINE (Chrome trace-event JSON, opened on the Mac in chrome://tracing or any trace viewer, no
/// download). Opt-in: <c>SHEPHERD_K7_PROF</c> unset / 0 = off (one static bool test per zone: kit #6's frames and timing).
/// <para><c>SHEPHERD_K7_PROF</c> = a comma list: <c>time</c> (each zone and each behaviour's message timed: calls, inclusive / self ms, max),
/// <c>sample</c> (only an id store per zone change; the host's sampler thread -- the Mac: a managed one -- counts the running id ~1000
/// times a second: the cheapest mode, statistical), <c>calls</c> (the host's first-call trace, prof-calls.tsv), <c>zones</c> (no
/// per-behaviour keys: the least overhead), <c>all</c> / <c>1</c> = time + sample + calls; <c>win=&lt;from&gt;:&lt;to&gt;</c> (frames, m+N
/// allowed) = the frames in it get their own rows ("[window]"). Rows per scene (the active scene at each frame's start), written to
/// &lt;logs&gt;/prof.tsv at every scene change and at the end (vita/tools/prof_rank.py ranks them: the named suspects -- the coroutine
/// scheduler, DOTween, Rewired, the FixedUpdate scripts, A* path requests, the SkeletonUtilityBones -- and the top systems).</para>
/// <para><c>SHEPHERD_K7_TRACE=&lt;from&gt;:&lt;to&gt;[:types]</c>: every zone (and with :types every behaviour message and coroutine step) of
/// those frames as Chrome trace events -- main thread, render thread, the host's GC pauses, frame markers -- to
/// &lt;logs&gt;/trace-f&lt;from&gt;-f&lt;to&gt;.json; buffer SHEPHERD_K7_TRACE_CAP events (default 131072 main + 32768 render thread).</para>
/// <para><c>SHEPHERD_K7_STALL=&lt;KiB&gt;</c> (timing tests only): before each candidate loop in every other frame, a KiB-sized buffer is read
/// through (the caches evicted): the rows "cold" / "warm" of each candidate give its STALL SHARE = 1 - warm / cold (memory-bound loops
/// slow down when cold; compute-bound ones do not) -- the "thread only compute-bound work" measurement.</para>
/// Exclusive attribution: a zone or message started inside another pauses its parent's self time (inclusive = self + children).</summary>
[ProfileOnly]
public static unsafe class K7Prof
{
    static readonly string Spec = K7.Master ? Environment.GetEnvironmentVariable("SHEPHERD_K7_PROF") : null;
    static bool Has(string w) { if (Spec == null) return false; foreach (var p in Spec.Split(',')) if (p.Trim() == w) return true; return false; }
    public static readonly bool Timed = Has("time") || Has("all") || Has("1");
    public static readonly bool Sampled = Has("sample") || Has("all") || Has("1");
    /// <summary>Kit #8 speed track (the gameplay engine pass's "&lt;= 64 KiB alloc / frame" target): <c>alloc</c> (with time) adds each zone's and
    /// each behaviour message's own main-thread allocation (GC.GetAllocatedBytesForCurrentThread before / after, children subtracted, as the
    /// self times) -- rows "alloc" in prof.tsv (calls, KiB in the self column). Measurement only.</summary>
    public static readonly bool Allocs = Has("alloc");
    public static readonly bool On = Timed || Sampled || Allocs || TraceSpec() != null || StallSpec() > 0;
    /// <summary>Per-behaviour keys (messages, Start, coroutine steps); not with "zones".</summary>
    public static readonly bool Types = On && !Has("zones");

    // ---------------------------------------------------------------- ids
    public const int ZFrame = 1, ZInput = 2, ZStarts = 3, ZFixed = 4, ZFixedUpd = 5, ZTriggers = 6, ZCoFixed = 7, ZUpdate = 8, ZCoNext = 9, ZSync = 10,
        ZInvokes = 11, ZVideo = 12, ZAnimator = 13, ZLate = 14, ZDestroy = 15, ZAsync = 16, ZRender = 17, ZRLayout = 18, ZRWorld = 19, ZRPost = 20,
        ZRUi = 21, ZREnd = 22, ZCoEnd = 23, ZEngEnd = 24, ZDiag = 25, ZPresent = 26, ZThrottle = 27, ZOverlay = 28, ZRWaitRt = 29, ZSort = 30, ZGc = 31,
        ZRtJob = 40;
    const int FirstType = 64, MaxIds = 64 + 4096, Depth = 64;
    // (everything below is made by Init, at the first zone with the profiler on: off, this class holds only its switches)
    static string[] Names;
    static int _nIds = FirstType;
    static Dictionary<string, int> Ids;
    static bool[] SpineType;
    static readonly string[] PhaseNames = { "FixedUpdate", "Update", "LateUpdate", "Start" };

    static void InitNames()
    {
        Names = new string[MaxIds]; Ids = new Dictionary<string, int>(); SpineType = new bool[MaxIds]; SpineAlt = new(); UtilAlt = new int[MaxIds];
        string[] z = { "-", "frame (rest of the boot loop)", "input", "Start calls", "fixed step", "FixedUpdate loop", "Triggers2D.Step", "coroutines (WaitForFixedUpdate)",
            "Update loop", "coroutines (next frame)", "SynchronizationContext", "Invoke calls", "video update", "Animator update", "LateUpdate loop", "deferred Destroy",
            "async operations", "render (SceneRenderer.Frame)", "render: layout + canvases", "render: world pass", "render: post + Stylizer", "render: UI canvases",
            "render: end / handoff", "coroutines (end of frame)", "engine end of frame", "diagnostics after the frame", "present", "frame pacing (throttle)",
            "k7 overlay", "render: wait for the render thread", "Active list sort (kit #8 zone)", "GC (the world stopped)" };
        for (int i = 0; i < z.Length; i++) Names[i] = z[i];
        Names[ZRtJob] = "render thread: frame job";
        for (int i = 0; i < FirstType; i++) Names[i] ??= "zone " + i;
    }

    /// <summary>The id of a behaviour type's message (Lifecycle keeps it per type and phase).</summary>
    public static int TypeId(Type t, int phase)
    {
        if (!_init) Init();
        var n = t.FullName ?? t.Name;
        int id = Id(n + "." + PhaseNames[phase]);
        // (the game updates most skeletons from its SkeletonAnimationLODManager, one per skeleton, not from SkeletonAnimation.Update: Mac intro profile)
        if (n == "Spine.Unity.SkeletonAnimation" || n == "Spine.Unity.SkeletonMecanim" || n == "Spine.Unity.SkeletonGraphic" || n == "SkeletonAnimationLODManager") SpineType[id] = true;
        return id;
    }
    static int Id(string key)
    {
        if (Ids.TryGetValue(key, out var id)) return id;
        if (_nIds >= MaxIds) return MaxIds - 1;
        id = _nIds++; Names[id] = _nIds == MaxIds ? "(more keys than the profiler holds)" : key; Ids[key] = id; return id;
    }
    /// <summary>Review fix (kit #7 measure): a weak table -- an entry lives only as long as its behaviour's record, so a destroyed skeleton
    /// (dungeon enemies, followers spawned and despawned in a long profiled session) is collected as in the unprofiled run; the strong
    /// Dictionary&lt;NMono, int&gt; kept every skeleton the profiler ever saw (NMono holds Managed and its GameObject record). The value: whether
    /// its GameObject carries a SkeletonUtility (one of two shared boxes). (The engine already uses this table type: Icalls/CameraIcalls.cs.)
    /// The "[+SkeletonUtility]" key is per message id (<see cref="UtilAlt"/>): the table used to keep the first message's key, so a skeleton's
    /// LateUpdate was counted under its Update's "[+SkeletonUtility]" key.</summary>
    static System.Runtime.CompilerServices.ConditionalWeakTable<Native.NMono, object> SpineAlt;
    static readonly object Util = true, NoUtil = false;
    /// <summary>Per message id: its "[+SkeletonUtility]" key id (0 = not made yet).</summary>
    static int[] UtilAlt;
    /// <summary>The SkeletonUtilityBone suspect: a Spine skeleton whose GameObject carries a SkeletonUtility gets its own key ("[+SkeletonUtility]"),
    /// so its per-instance time (the bones' DoUpdate runs inside its update events) is told apart from the plain skeletons'.</summary>
    public static int Spine(int id, Native.NMono m)
    {
        if (!SpineType[id]) return id;
        if (!SpineAlt.TryGetValue(m, out var box))
        {
            bool util = false;
            if (m.Go != null) foreach (var c in m.Go.Components) if (c.Managed != null && c.Managed.GetType().Name == "SkeletonUtility") { util = true; break; }
            SpineAlt.Add(m, box = util ? Util : NoUtil);   // (main thread only; not in the table yet: TryGetValue just said so)
        }
        if (!ReferenceEquals(box, Util)) return id;
        int alt = UtilAlt[id];
        if (alt == 0) UtilAlt[id] = alt = Id(Names[id] + " [+SkeletonUtility]");
        return alt;
    }
    /// <summary>A coroutine's key: its owner's type, its method, " [coroutine]".</summary>
    public static int CoId(object owner, string method, object root) =>
        !_init && Init0() ? 0 : Id((owner?.GetType().FullName ?? "?") + "." + (method ?? root?.GetType().Name ?? "?") + " [coroutine]");

    // ---------------------------------------------------------------- the stack (main thread)
    static int[] _stk;
    static long[] _t0, _child;
    static int _sp, _overflow, _unbalanced;
    static long[] _incl, _self, _max; static int[] _calls;
    static long[] _a0, _achild, _aself;   // kit #8 alloc mode
    static int* _slot;           // the profiler block: [1] frame, [2] main id, [3] render id, [16..] main samples by id, [16 + MaxIds ..] render samples, GC ring
    static bool _cold;           // stall mode: this frame's candidates run cold

    public static void Push(int id)
    {
        if (!On) return;
        if (!_init) Init();
        int sp = _sp++;
        if (sp >= Depth) { _overflow++; return; }
        _stk[sp] = id;
        if (_slot != null) _slot[2] = id;
        if (_cold && IsCandidate(id)) Evict();
        if (Timed || _trOn)
        {
            long t = Stopwatch.GetTimestamp(); _t0[sp] = t; _child[sp] = 0;
            if (_trOn) Tr(t, id, true);
        }
        else _child[sp] = 0;
        if (Allocs) { _a0[sp] = GC.GetAllocatedBytesForCurrentThread(); _achild[sp] = 0; }
    }
    public static void Pop()
    {
        if (!On || !_init) return;
        int sp = --_sp;
        if (sp < 0) { _sp = 0; _unbalanced++; return; }
        if (sp >= Depth) return;
        int id = _stk[sp];
        _calls[id]++;
        if (Allocs) { long a = GC.GetAllocatedBytesForCurrentThread() - _a0[sp]; _aself[id] += a - _achild[sp]; if (sp > 0) _achild[sp - 1] += a; }
        if (Timed || _trOn)
        {
            long t = Stopwatch.GetTimestamp();
            if (_trOn) Tr(t, id, false);
            if (Timed)
            {
                long el = t - _t0[sp];
                _incl[id] += el; _self[id] += el - _child[sp]; if (el > _max[id]) _max[id] = el;
                if (sp > 0) _child[sp - 1] += el;
                if (_stall != null && IsCandidate(id)) _fz[id] += el;   // (this frame's time in the candidate; FrameBegin files it by parity)
            }
        }
        if (_slot != null) _slot[2] = sp > 0 ? _stk[sp - 1] : 0;
    }

    // ---------------------------------------------------------------- the render thread (Render/MtRender.cs: one zone per frame job)
    public static void RtBegin() { if (!On || !_init) return; if (_slot != null) _slot[3] = ZRtJob; if (_trOn) TrRt(Stopwatch.GetTimestamp(), true); }
    public static void RtEnd() { if (!On || !_init) return; if (_slot != null) _slot[3] = 0; if (_trOn) TrRt(Stopwatch.GetTimestamp(), false); }

    // ---------------------------------------------------------------- frames and scenes
    static string _scene; static int _sceneFrames, _sceneFrom, _maxFrameN;
    static long _sceneWall, _maxFrame, _frameT, _alloc0; static double _sceneGc;
    static int[] _sample0;   // the sample counts at the scene's start
    static StringBuilder Out;
    static bool _init;

    static bool Init0() { Init(); return false; }
    static void Init()
    {
        _init = true;
        InitNames(); Out = new StringBuilder();
        _stk = new int[Depth]; _t0 = new long[Depth]; _child = new long[Depth];
        _incl = new long[MaxIds]; _self = new long[MaxIds]; _max = new long[MaxIds]; _calls = new int[MaxIds];
        if (Allocs) { _a0 = new long[Depth]; _achild = new long[Depth]; _aself = new long[MaxIds]; }
        foreach (var p in (Spec ?? "").Split(','))
        {
            var q = p.Trim(); if (!q.StartsWith("win=")) continue;
            var ab = q.Substring(4).Split(':');
            if (ab.Length == 2 && MenuClock.Parse(ab[0], out var a) && MenuClock.Parse(ab[1], out var b)) { WinFromSet = a; WinToSet = b; WinSet = true; }
        }
        if (Sampled)
        {
            if (VitaHost.On) _slot = (int*)K7.HostPtr(1);
            if (_slot == null)
            {   // the Mac (or a host without the block): our own block and a managed sampler thread (it only reads the ids)
                _slot = (int*)Marshal.AllocHGlobal(4 * (16 + MaxIds + 64 + 512)); new Span<int>(_slot, 16 + MaxIds + 64 + 512).Clear();
                var th = new Thread(MacSampler) { IsBackground = true, Name = "k7-sampler" }; th.Start();
            }
            else K7.Host(1, 0);   // the host starts its sampler thread
            _sample0 = new int[MaxIds + 64 + 2];
        }
        else if (VitaHost.On) _slot = (int*)K7.HostPtr(1);   // (the GC ring for the trace)
        InitTrace(); InitStall();
        Log.Info("[k7] hot-path profiler ON: " + (Timed ? "timed" : "") + (Timed && Sampled ? " + " : "") + (Sampled ? "sampled" : "") + (!Timed && !Sampled ? "calls counted" : "") + (Types ? ", per behaviour" : ", zones only") +
                 (WinSet ? ", window " + WinFromSet + ".." + WinToSet : "") + (TraceSpec() != null ? ", Chrome trace " + TraceSpec() : "") + (_stall != null ? ", stall share (" + _stall.Length / 1024 + " KiB eviction)" : "") +
                 " (SHEPHERD_K7_PROF=" + Spec + "; rows -> prof.tsv)");
    }
    static MenuClock.At WinFromSet, WinToSet; static bool WinSet;

    static void MacSampler()
    {
        while (true)
        {
            Thread.Sleep(1);
            int z = _slot[2], r = _slot[3];
            if ((uint)z < MaxIds) _slot[16 + z]++;
            if ((uint)r < 64) _slot[16 + MaxIds + r]++;
            _slot[5]++;
        }
    }

    /// <summary>K7.FrameBegin: the previous frame closes (its zone), the scene key, the trace / stall windows, the next frame opens.</summary>
    public static void FrameBegin(int frame, long now)
    {
        if (!_init) Init();
        if (_sp > 1) { _unbalanced++; while (_sp > 1) Pop(); }   // (an exception went past a zone: unwound here)
        if (_sp == 1) Pop();                                      // the previous frame
        if (_frameT != 0) { long d = now - _frameT; _sceneWall += d; _sceneFrames++; _sceneGc += SimStats.LastGcMs; if (d > _maxFrame) { _maxFrame = d; _maxFrameN = frame - 1; } }
        _frameT = now;
        var key = SceneKey(frame);
        if (key != _scene) { if (_scene != null) Flush(frame - 1); _scene = key; _sceneFrom = frame; Snapshot(); }
        if (_slot != null) _slot[1] = frame;
        TraceFrame(frame);
        if (_stall != null) StallFrame();
        _cold = _stall != null && (frame & 1) == 1;
        Push(ZFrame);
    }

    static string SceneKey(int frame)
    {
        var n = Scenes.All.TryGetValue(Scenes.ActiveHandle, out var s) ? s.Name : "?";
        if (WinSet && frame >= WinFromSet.Frame && frame <= WinToSet.Frame) return "[window " + WinFromSet + ".." + WinToSet + "] " + n;
        return n;
    }

    static void Snapshot()
    {
        Array.Clear(_incl); Array.Clear(_self); Array.Clear(_max); Array.Clear(_calls); if (_aself != null) Array.Clear(_aself);
        if (_stall != null) for (int i = 0; i < Cand.Length; i++) { _coldL[i].Clear(); _warmL[i].Clear(); }
        _sceneWall = 0; _sceneFrames = 0; _sceneGc = 0; _maxFrame = 0; _alloc0 = GC.GetAllocatedBytesForCurrentThread();
        if (_sample0 != null && _slot != null) { for (int i = 0; i < MaxIds + 64; i++) _sample0[i] = _slot[16 + i]; _sample0[MaxIds + 64] = _slot[5]; _sample0[MaxIds + 65] = _slot[6]; }
    }

    static string Ms(long t) => (t * 1000.0 / Stopwatch.Frequency).ToString("F3");

    /// <summary>The scene's rows into prof.tsv (rewritten whole: a crash keeps every scene that ended before it).</summary>
    static void Flush(int lastFrame)
    {
        if (Out.Length == 0)
            Out.Append("# Shepherd hot-path profile (kit #7, Diag/K7Prof.cs): SHEPHERD_K7_PROF=").Append(Spec).Append("; ").Append(VitaHost.On ? "Vita host" : "no Vita host").Append(", ")
               .Append(Time.Wall ? "play clock" : "frame clock").Append("; times in ms (exclusive: self = incl - nested zones / messages); samples ~1/ms\n")
               .Append("scene\tframes\tkind\tname\tcalls\tincl_ms\tself_ms\tmax_ms\tsamples\n");
        string sc = _scene.Replace('\t', ' ');
        int nS = 0, gcS = 0, totS = 0;
        if (_sample0 != null && _slot != null) { totS = _slot[5] - _sample0[MaxIds + 64]; gcS = _slot[6] - _sample0[MaxIds + 65]; }
        Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tscene\tf").Append(_sceneFrom).Append("-f").Append(lastFrame).Append("\t0\t").Append(Ms(_sceneWall)).Append('\t')
           .Append(_sceneGc.ToString("F3")).Append('\t').Append(Ms(_maxFrame)).Append('\t').Append(totS).Append('\n');
        Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tcount\tmain-thread allocation (KiB)\t0\t").Append((GC.GetAllocatedBytesForCurrentThread() - _alloc0) >> 10).Append("\t0\t0\t0\n");
        Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tcount\tlongest frame f").Append(_maxFrameN).Append("\t0\t").Append(Ms(_maxFrame)).Append("\t0\t0\t0\n");
        Census(sc);
        for (int id = 1; id < _nIds; id++)
        {
            int smp = _sample0 != null && _slot != null ? _slot[16 + id] - _sample0[id] : 0;
            if (_calls[id] == 0 && smp == 0) continue;
            nS += smp;
            Out.Append(sc).Append('\t').Append(_sceneFrames).Append('\t').Append(id < FirstType ? "zone" : Names[id].EndsWith(" [coroutine]") ? "coro" : "type").Append('\t')
               .Append(Names[id]).Append('\t').Append(_calls[id]).Append('\t').Append(Ms(_incl[id])).Append('\t').Append(Ms(_self[id])).Append('\t').Append(Ms(_max[id])).Append('\t').Append(smp).Append('\n');
            if (_aself != null && _aself[id] != 0)   // kit #8 alloc mode: the id's own allocation (KiB in the self column)
                Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\talloc\t").Append(Names[id]).Append('\t').Append(_calls[id]).Append("\t0\t").Append(_aself[id] >> 10).Append("\t0\t0\n");
            int ci = _stall != null ? Array.IndexOf(Cand, id) : -1;
            if (ci >= 0 && _coldL[ci].Count > 0 && _warmL[ci].Count > 0)   // medians of the frames' times, cold (evicted first) vs warm; calls = frames
                Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tstall\t").Append(Names[id]).Append('\t').Append(_coldL[ci].Count + _warmL[ci].Count).Append('\t')
                   .Append(Ms(Median(_coldL[ci]))).Append('\t').Append(Ms(Median(_warmL[ci]))).Append("\t0\t0\n");
        }
        if (_sample0 != null && _slot != null)
        {
            Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tsample\tGC (the world stopped)\t0\t0\t0\t0\t").Append(gcS).Append('\n');
            int rt = _slot[16 + MaxIds + ZRtJob] - _sample0[MaxIds + ZRtJob], rtAll = 0;
            for (int i = 0; i < 64; i++) rtAll += _slot[16 + MaxIds + i] - _sample0[MaxIds + i];
            Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tsample\trender thread: frame job (of all its samples)\t").Append(rtAll).Append("\t0\t0\t0\t").Append(rt).Append('\n');
        }
        if (_overflow + _unbalanced > 0) Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tcount\tzone stack overflow / unbalanced (unwound)\t").Append(_overflow).Append("\t0\t0\t0\t").Append(_unbalanced).Append('\n');
        try { File.WriteAllText(Path.Combine(Program.LogDir, "prof.tsv"), Out.ToString()); } catch (Exception e) { Log.Warn("[k7] prof.tsv not written: " + e.GetBaseException().Message); }
        Log.Info("[k7] profile: scene '" + _scene + "' f" + _sceneFrom + "-f" + lastFrame + " (" + _sceneFrames + " frames, " + Ms(_sceneWall) + " ms) -> prof.tsv");
    }

    /// <summary>The named suspects' populations at the scene's end: the active behaviours of the types that matter, the coroutines.</summary>
    static void Census(string sc)
    {
        int bones = 0, utils = 0, skel = 0, astar = 0, seek = 0, tw = 0, rew = 0;
        foreach (var m in Lifecycle.Active)
        {
            var n = m.Managed?.GetType().FullName; if (n == null) continue;
            if (n == "Spine.Unity.SkeletonUtilityBone") bones++; else if (n == "Spine.Unity.SkeletonUtility") utils++;
            else if (n.StartsWith("Spine.Unity.Skeleton")) skel++; else if (n.StartsWith("Pathfinding.")) { if (n == "Pathfinding.Seeker") seek++; else astar++; }
            else if (n.StartsWith("DG.Tweening.")) tw++; else if (n.StartsWith("Rewired.")) rew++;
        }
        void C(string what, int v) => Out.Append(sc).Append('\t').Append(_sceneFrames).Append("\tcount\t").Append(what).Append('\t').Append(v).Append("\t0\t0\t0\t0\n");
        C("active behaviours", Lifecycle.Active.Count); C("coroutines running", Coroutines.Count); C("SkeletonUtilityBone (active)", bones); C("SkeletonUtility (active)", utils);
        C("other Spine.Unity.Skeleton* (active)", skel); C("Pathfinding.* (active, not Seeker)", astar); C("Pathfinding.Seeker (active)", seek); C("DG.Tweening.* (active)", tw); C("Rewired.* (active)", rew);
    }

    public static void End()
    {
        if (!_init) return;
        while (_sp > 0) Pop();
        if (_scene != null) Flush(Time.FrameCount);
        if (_trCap > 0 && !_trWritten && _tn > 0) WriteTrace("the run ended inside the window");
    }

    // ---------------------------------------------------------------- the Chrome trace (SHEPHERD_K7_TRACE)
    static string TraceSpec() => K7.Master ? Environment.GetEnvironmentVariable("SHEPHERD_K7_TRACE") is { Length: > 0 } t && t != "0" ? t : null : null;
    static MenuClock.At _trFrom, _trTo; static bool _trTypes, _trOn, _trWritten;
    static int _trCap, _tn, _rtCap; static volatile int _rn;
    static long[] _tts, _rts; static int[] _tev; static byte[] _rev;
    static void InitTrace()
    {
        var t = TraceSpec(); if (t == null) return;
        var p = t.Split(':');
        if (p.Length < 2 || !MenuClock.Parse(p[0], out _trFrom) || !MenuClock.Parse(p[1], out _trTo)) { Log.Warn("[k7] SHEPHERD_K7_TRACE=" + t + ": want <from>:<to>[:types]"); return; }
        _trTypes = p.Length > 2 && p[2] == "types";
        _trCap = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K7_TRACE_CAP"), out var c) && c > 1000 ? c : 131072; _rtCap = Math.Max(4096, _trCap / 4);
        _tts = new long[_trCap]; _tev = new int[_trCap]; _rts = new long[_rtCap]; _rev = new byte[_rtCap];
    }
    static void TraceFrame(int frame)
    {
        if (_trCap == 0 || _trWritten) return;
        bool inWin = frame >= _trFrom.Frame && frame <= _trTo.Frame;
        if (inWin)
        {   // a frame marker (instant event)
            _trOn = true;
            if (_tn < _trCap) { _tts[_tn] = Stopwatch.GetTimestamp(); _tev[_tn++] = Marker | frame; }
        }
        else if (_trOn) { _trOn = false; WriteTrace("window end"); }
    }
    const int Marker = 0x40000000;   // _tev: a begin = the id, an end = id | int.MinValue, a frame marker = Marker | frame
    static void Tr(long t, int id, bool begin)
    {
        if (!_trTypes && id >= FirstType) return;
        if (_tn >= _trCap) { _trOn = false; WriteTrace("buffer full (SHEPHERD_K7_TRACE_CAP)"); return; }
        _tts[_tn] = t; _tev[_tn++] = begin ? id : id | int.MinValue;
    }
    /// <summary>The render thread's event. Review fix (kit #7 measure): the arrays are read once into locals -- WriteTrace on the main thread
    /// ends the window and drops them (null) while this thread may be past its _trOn test; a local keeps the array it holds alive and its
    /// stores harmless (nobody reads them any more) instead of a NullReferenceException on the render thread (RtBegin / RtEnd sit outside
    /// the frame job's try: it would have ended the run).</summary>
    static void TrRt(long t, bool begin)
    {
        var ts = Volatile.Read(ref _rts); var ev = Volatile.Read(ref _rev);
        int n = _rn;
        if (ts == null || ev == null || (uint)n >= (uint)ts.Length || (uint)n >= (uint)ev.Length) return;
        ts[n] = t; ev[n] = (byte)(begin ? 1 : 0); _rn = n + 1;
    }

    static void WriteTrace(string why)
    {
        _trWritten = true; _trOn = false;
        var path = Path.Combine(Program.LogDir, "trace-f" + _trFrom.Frame + "-f" + _trTo.Frame + ".json");
        try
        {
            double us(long x) => x * 1e6 / Stopwatch.Frequency;
            long t0 = _tn > 0 ? _tts[0] : 0, tEnd = _tn > 0 ? _tts[_tn - 1] : 0;
            using var w = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
            w.Write("{\"displayTimeUnit\":\"ms\",\"otherData\":{\"source\":\"Shepherd kit #7 Diag/K7Prof.cs\",\"why\":\"" + why + "\"},\"traceEvents\":[\n");
            w.Write("{\"name\":\"process_name\",\"ph\":\"M\",\"pid\":1,\"args\":{\"name\":\"Shepherd\"}},{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":1,\"args\":{\"name\":\"main (game + render prep)\"}},");
            w.Write("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":2,\"args\":{\"name\":\"render thread\"}},{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":3,\"args\":{\"name\":\"GC (world stopped)\"}}");
            for (int i = 0; i < _tn; i++)
            {
                int e = _tev[i]; string ts = us(_tts[i] - t0).ToString("F1");
                if (e >= 0 && (e & Marker) != 0) { w.Write(",\n{\"name\":\"f" + (e & ~Marker) + "\",\"ph\":\"i\",\"s\":\"t\",\"pid\":1,\"tid\":1,\"ts\":" + ts + "}"); continue; }
                int id = e & int.MaxValue;
                w.Write(",\n{\"name\":\"" + Esc(Names[id]) + "\",\"ph\":\"" + (e >= 0 ? "B" : "E") + "\",\"pid\":1,\"tid\":1,\"ts\":" + ts + "}");
            }
            int rn = _rn;
            for (int i = 0; i < rn; i++)
                if (_rts[i] >= t0 && _rts[i] <= tEnd + Stopwatch.Frequency) w.Write(",\n{\"name\":\"" + Names[ZRtJob] + "\",\"ph\":\"" + (_rev[i] == 1 ? "B" : "E") + "\",\"pid\":1,\"tid\":2,\"ts\":" + us(_rts[i] - t0).ToString("F1") + "}");
            if (VitaHost.On && _slot != null)
            {   // the host's GC ring (process time in us = Stopwatch / 1000 on the Vita)
                long t0us = (long)us(t0), tEus = (long)us(tEnd);
                for (int i = 0; i < 256; i++)
                {
                    uint st = (uint)_slot[16 + MaxIds + 64 + 2 * i]; int d = _slot[16 + MaxIds + 64 + 2 * i + 1];
                    if (d <= 0) continue;
                    long s = (t0us & ~0xffffffffL) | st; if (s < t0us - 0x80000000L) s += 0x100000000L;
                    if (s < t0us || s > tEus) continue;
                    w.Write(",\n{\"name\":\"GC pause\",\"ph\":\"X\",\"pid\":1,\"tid\":3,\"ts\":" + (s - t0us) + ",\"dur\":" + d + "}");
                }
            }
            w.Write("\n]}\n");
            Log.Info("[k7] Chrome trace: " + _tn + " main-thread events, " + rn + " render-thread events (" + why + ") -> " + path + " (chrome://tracing: Load)");
        }
        catch (Exception e) { Log.Warn("[k7] trace not written: " + e.GetBaseException().Message); }
        _rtCap = 0; _tts = null; _tev = null; Volatile.Write(ref _rts, null); Volatile.Write(ref _rev, null); _trCap = 0;   // (TrRt: its own locals, see there)
    }
    static string Esc(string s) => s.IndexOf('"') < 0 && s.IndexOf('\\') < 0 ? s : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---------------------------------------------------------------- stall share (SHEPHERD_K7_STALL)
    static int StallSpec() => K7.Master && int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K7_STALL"), out var k) && k > 0 ? k : 0;
    static byte[] _stall; static long[] _fz; static List<long>[] _coldL, _warmL; static int _sink;
    /// <summary>The candidate loops: the zones that walk the scene's records every frame.</summary>
    static readonly int[] Cand = { ZUpdate, ZLate, ZFixedUpd, ZCoNext, ZAnimator, ZRLayout, ZRWorld, ZRUi };
    static void InitStall()
    {
        int k = StallSpec(); if (k <= 0) return;
        if (!Timed) { Log.Warn("[k7] SHEPHERD_K7_STALL needs SHEPHERD_K7_PROF=time"); return; }
        _stall = new byte[k * 1024]; _fz = new long[FirstType]; _coldL = new List<long>[Cand.Length]; _warmL = new List<long>[Cand.Length];
        for (int i = 0; i < Cand.Length; i++) { _coldL[i] = new List<long>(); _warmL[i] = new List<long>(); }
    }
    static bool IsCandidate(int id) => id < FirstType && Array.IndexOf(Cand, id) >= 0;
    /// <summary>The frame that just ended: each candidate's time into its parity's list (odd frames ran it cold).</summary>
    static void StallFrame()
    {
        for (int i = 0; i < Cand.Length; i++) { long t = _fz[Cand[i]]; if (t > 0) (_cold ? _coldL[i] : _warmL[i]).Add(t); _fz[Cand[i]] = 0; }
    }
    static long Median(List<long> l) { var a = l.ToArray(); Array.Sort(a); return a[a.Length / 2]; }
    static void Evict() { var b = _stall; int s = 0; for (int i = 0; i < b.Length; i += 32) { s += b[i]; b[i] = (byte)s; } _sink += s; }
}
