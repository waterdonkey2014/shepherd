using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #10, track cache (vita/notes/kit10-cache.md): the LEARNING CACHE FRAMEWORK in the engine -- every app (the store, the
/// worker, the Settings API; the learning kinds that fill it are the launcher apps' Runtime/K10Learn.cs).
/// <list type="bullet">
/// <item><b>The store</b> (<see cref="Store"/>, Runtime/K10Store.cs): ux0:data/shepherd/cache/, exact keys (<see cref="Key"/>: engine + game +
///   pack + the kind's inputs), verified on first reuse, the size cap (Settings > PS Vita Settings > Cache Size: 256 MB / 512 MB / 1 GB
///   default / 2 GB) with least-recently-used eviction, never below the free-space margin (<c>SHEPHERD_K10_CACHE_MARGIN_MB</c>, default 256;
///   the card's free space from the host, vita/shepherd/k10_cache.inc.c), results only.</item>
/// <item><b>The worker</b> ("shepherd-cache", pinned as a latency-tolerant worker: the 4th core, else USER_2): every card write of the
///   framework (entries, the index, eviction, Clear Cache) and the learning kinds' preparation runs here, one job at a time. It exists only
///   while it has a job it may run: it is started when one is queued (or a window opens over waiting writes) and ENDS when none is left, so
///   the 1 MiB interpreter stack a thread that runs interpreted code takes from the arena (interp.c INTERP_STACK_SIZE) goes back between jobs
///   instead of being held all session (review fix 2026-10-28: the c5 MEMSUM lines showed +1..2 MiB of arena in every run with the worker).
///   <b>The idle-window rule</b> (the write discipline's owner rule, 2026-10-18: a card write can freeze reads for tens to hundreds of ms):
///   a job marked <see cref="Job.Write"/> runs only in an idle window (<see cref="Window"/>: the Settings menu open; in the launcher apps also
///   the logos, the quotes, the BufferScene, the Main Menu -- never during a scene load or gameplay), or -- the size cap of the rule -- at the
///   end of the next scene load once more than <see cref="PendingCap"/> bytes wait; the game's own saves are never delayed (not ours).</item>
/// <item><b>The Settings API</b> (Runtime/VitaToggles.cs rows): <see cref="SetCap"/>, <see cref="InUseText"/>, <see cref="RequestClear"/>
///   (game-style confirmation in the row; deletes only cache/), <see cref="Verify"/> (Verify Files: the io track's core, by name).</item>
/// </list>
/// <c>SHEPHERD_K10_CACHE=0</c> (env file) = kit #9 exactly: no rows, no worker, no file read or written by this code. Vita profile only.
/// Code size: log text by concatenation, plain arrays, an abstract job class (no delegates: no new generic instantiations -- eboot span).</summary>
[ProfileOnly]
public static class K10Cache
{
    /// <summary>The track's master switch (on by default under the Vita profile).</summary>
    public static readonly bool On = Profile.Vita && Environment.GetEnvironmentVariable("SHEPHERD_K10_CACHE") != "0";
    /// <summary>Settings > Cache Size choices (MB) and the default (owner 2026-10-16: 1 GB).</summary>
    public static readonly int[] SizesMb = { 256, 512, 1024, 2048 };
    public const int DefaultMb = 1024;
    static int _capMb = DefaultMb;
    public static int CapMb => _capMb;
    static readonly long MarginBytes = (long)(int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K10_CACHE_MARGIN_MB"), out var m) && m >= 0 && m <= 65536 ? m : 256) << 20;
    /// <summary>Test only (any build): pretend the card has this many MB free (the margin's tests on the Mac and under vlx).</summary>
    static readonly long FreeOverride = long.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K10_FREE_MB"), out var fo) && fo >= 0 ? fo << 20 : -1;
    /// <summary>Pending write bytes that force a write at the end of the next scene load (the write discipline's size cap).</summary>
    public const long PendingCap = 4L << 20;
    public static string Dir => Path.Combine(Program.Root, "cache");

    static K10Store _store;
    /// <summary>The store (made on first use; reconciled with the folder on the worker before its first write).</summary>
    public static K10Store Store
    {
        get
        {
            if (_store != null) return _store;
            lock (_wk) { if (_store == null) { var s = new K10Store(Dir, (long)_capMb << 20, MarginBytes, FreeSpace.I); s.Note = Note; s.Defer = DeferDelete.I; _store = s; } }
            return _store;
        }
    }
    static void Note(string s) => Log.Info(s);
    /// <summary>A damaged entry seen by a read (any thread, also outside a window): deleted by the worker in the next idle window -- a delete is
    /// a card write (review fix 2026-10-28) -- and only if it is still unusable then.</summary>
    sealed class DeferDelete : K10Store.IDefer
    {
        public static readonly DeferDelete I = new();
        public void Later(string kind, string key, int kindVersion) => Enqueue(new DamagedJob { Kind = kind, Key = key, V = kindVersion, Write = true, Bytes = 512 });
    }
    sealed class DamagedJob : Job { public string Kind, Key; public int V; public override void Run() => Store.DropIfUnusable(Kind, Key, V); }

    // ------------------------------------------------------------------ keys (exact: engine + game + pack + inputs)

    static string _engineId, _gameId;
    /// <summary>The engine build: Shepherd.Engine's module version id + the owner's Derived DLL's (ports of game code), if loaded.</summary>
    public static string EngineId => _engineId ??= typeof(K10Cache).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "+" + ModuleOf("Shepherd.Engine.Derived");
    /// <summary>The game build: Assembly-CSharp's and Assembly-CSharp-firstpass's module version ids ("none" before they are loaded: such a
    /// key is not cached, see <see cref="Key"/>).</summary>
    public static string GameId
    {
        get
        {
            if (_gameId != null) return _gameId;
            string a = ModuleOf("Assembly-CSharp"), b = ModuleOf("Assembly-CSharp-firstpass");
            var id = a + "+" + b;
            if (a != "none") _gameId = id;
            return id;
        }
    }
    static string ModuleOf(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            if (asm.GetName().Name == name) return asm.ManifestModule.ModuleVersionId.ToString("N");
        return "none";
    }
    /// <summary>An entry's full key: kind and its version, the engine, the game, the pack (its identity; "-" for a kind that reads no pack
    /// data) and the kind's own inputs. Stored in full in the entry and compared byte for byte (K10Store.Decode).</summary>
    public static string Key(string kind, int version, string pack, string inputs) =>
        "shepherd learning cache 1|kind=" + kind + "|v=" + version.ToString() + "|engine=" + EngineId + "|game=" + GameId + "|pack=" + pack + "|" + inputs;

    // ------------------------------------------------------------------ the card's free space (host devctl; the Mac: the volume)

    sealed class FreeSpace : K10Store.IFree
    {
        public static readonly FreeSpace I = new();
        public long FreeBytes()
        {
            if (FreeOverride >= 0) return FreeOverride;
#if SHEPHERD_EBOOT
            long mb = HostNative(0, 0);
            return mb < 0 ? -1 : mb << 20;
#else
            if (Diag.VitaHost.On) { long mb = HostNative(0, 0); return mb < 0 ? -1 : mb << 20; }
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Program.Root)) ?? "/").AvailableFreeSpace; } catch (Exception) { return -1; }
#endif
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int HostNative(int op, int arg) => shepherd_vita_k10_cache(op, arg);
    [DllImport("__Internal")] static extern int shepherd_vita_k10_cache(int op, int arg);

    // ------------------------------------------------------------------ the worker + the idle window

    /// <summary>One piece of work for the cache worker. <see cref="Write"/>: it writes to the card -- runs only in an idle window (or at a
    /// load's end once more than <see cref="PendingCap"/> bytes wait). <see cref="Bytes"/>: about how much it writes.</summary>
    public abstract class Job
    {
        public bool Write; public long Bytes;
        public abstract void Run();
    }
    static readonly object _wk = new();
    static Job[] _q = new Job[32]; static int _qh, _qn;
    static Thread _thread;
    static long _pending;
    static volatile bool _window, _forced;
    /// <summary>Times the worker was started this session (it ends when no job it may run is left).</summary>
    public static int Starts;
    /// <summary>True while an idle window is open (set by the main thread every frame the cache ticks).</summary>
    public static bool Window => _window;
    /// <summary>A write job may write now: a window is open, or the size cap forced the writes at a load's end.</summary>
    public static bool WriteOk => _window || _forced;
    /// <summary>Jobs waiting / write bytes waiting (log, overlay).</summary>
    public static int Queued { get { lock (_wk) return _qn; } }

    /// <summary>Queue a job (any thread). The worker is started when the job may run now (a read job, or a write in a window).</summary>
    public static void Enqueue(Job j)
    {
        lock (_wk)
        {
            if (_qn == _q.Length) { var n = new Job[_q.Length * 2]; for (int i = 0; i < _qn; i++) n[i] = _q[(_qh + i) % _q.Length]; _q = n; _qh = 0; }
            _q[(_qh + _qn) % _q.Length] = j; _qn++;
            if (j.Write) _pending += j.Bytes;
            StartLocked();
            Monitor.PulseAll(_wk);
        }
    }
    /// <summary>(under the lock) A job it may run is waiting and no worker exists: one is started.</summary>
    static void StartLocked()
    {
        if (_thread != null) return;
        bool runnable = false;
        for (int i = 0; i < _qn && !runnable; i++) { var c = _q[(_qh + i) % _q.Length]; runnable = !c.Write || _window || _forced; }
        if (!runnable) return;
        _thread = new Thread(Loop, 128 * 1024) { IsBackground = true, Name = "shepherd-cache", Priority = ThreadPriority.BelowNormal };
        Starts++;
        _thread.Start();
    }
    /// <summary>The window's state for this frame (main thread): <paramref name="open"/> = an idle window; <paramref name="loadEnd"/> = a scene
    /// load just ended (the size cap's safe moment).</summary>
    public static void SetWindow(bool open, bool loadEnd)
    {
        bool forced = loadEnd && Interlocked.Read(ref _pending) > PendingCap;
        if (open == _window && !forced) return;
        bool changed = open != _window;
        lock (_wk) { _window = open; _forced = forced; StartLocked(); Monitor.PulseAll(_wk); }
        if (changed) HostWindow(open);
        if (changed && !open && Verify.State == 1) Verify.StopWanted = true;   // (Verify Files stops when its window closes: a scene load / the menu left)
    }
    /// <summary>The window's state for the host's own writers too (vita/shepherd/k10_cache.inc.c op 2: <c>shepherd_k10_window()</c> -- the
    /// learning records the runtime / host write, e.g. the aot track's aot-learn.tsv, can wait for the same windows). One call per change.</summary>
    static void HostWindow(bool open)
    {
#if SHEPHERD_EBOOT
        HostNative(2, open ? 1 : 0);
#else
        if (Diag.VitaHost.On) HostNative(2, open ? 1 : 0);
#endif
    }
    // (c5 documented a WaitIdle / StopReads pair "before a scene load takes the card" that nothing called: removed in the review, 2026-10-28.
    // A job already running when a window closes finishes -- the jobs are small: a few-hundred-byte entry, the index, a save hashed; a
    // Continue manifest whose window closed while its save was hashed writes in the next window; Verify Files stops, see Verify.)
    static void Loop()
    {
        try { Diag.VitaHost.PinWorker("shepherd-cache"); } catch { }
        for (;;)
        {
            Job j = null;
            lock (_wk)
            {
                for (int i = 0; i < _qn; i++)
                {
                    var c = _q[(_qh + i) % _q.Length];
                    if (!c.Write || _window || _forced)
                    {   // the first runnable job in order (reads / preparation at once; writes in a window): taken out of the ring
                        j = c;
                        for (int k = i; k > 0; k--) _q[(_qh + k) % _q.Length] = _q[(_qh + k - 1) % _q.Length];
                        _q[_qh] = null; _qh = (_qh + 1) % _q.Length; _qn--;
                        if (c.Write) _pending -= c.Bytes;
                        break;
                    }
                }
                if (j == null)
                {   // nothing it may run now: the worker ENDS (its interpreter stack goes back to the arena); the next runnable job starts a new one
                    if (_qn == 0) _forced = false;
                    _thread = null;
                    Monitor.PulseAll(_wk);
                    return;
                }
            }
            try { j.Run(); }
            catch (Exception e) { Log.Warn("[k10] cache worker: " + j.GetType().Name + " failed (" + e.GetBaseException().Message + ")"); }
            lock (_wk) { if (_qn == 0) _forced = false; }
        }
    }

    static bool _flushQueued;
    sealed class FlushOnce : Job { public override void Run() { _flushQueued = false; var err = Store.Flush(); if (err != null) Log.Warn("[k10] cache index not written (" + err + ")"); } }
    /// <summary>The index written in the next window, once (any thread).</summary>
    public static void FlushSoon()
    {
        if (!On || _flushQueued || _store == null || !_store.Dirty) return;
        _flushQueued = true; Enqueue(new FlushOnce { Write = true, Bytes = 4096 });
    }

    // ------------------------------------------------------------------ the Settings rows' API (Runtime/VitaToggles.cs)

    /// <summary>The saved Cache Size (vita-settings.txt cache_mb=), at boot.</summary>
    public static void Configure(int mb)
    {
        if (Array.IndexOf(SizesMb, mb) < 0) mb = DefaultMb;
        _capMb = mb;
        if (_store != null) _store.Cap = (long)mb << 20;
    }
    sealed class ShrinkJob : Job
    {
        public override void Run()
        {
            long b = Store.Shrink();
            Log.Info("[k10] Cache Size " + K10Store.Mb(Store.Cap) + ": " + (b > 0 ? K10Store.Mb(b) + " of least recently used entries removed, " : "nothing to remove, ") + K10Store.Mb(Store.InUse) + " in use");
            var err = Store.Flush(); if (err != null) Log.Warn("[k10] cache index not written (" + err + ")");
        }
    }
    /// <summary>The Cache Size row changed: the cap now; least recently used entries out on the worker (the Settings menu is a window).</summary>
    public static void SetCap(int mb)
    {
        Configure(mb);
        Store.Cap = (long)_capMb << 20;
        Enqueue(new ShrinkJob());   // (not a window job: the player asked; it runs while the menu is open)
    }

    sealed class ScanJob : Job { public override void Run() { Store.Reconcile(); _scanDone = true; } }
    static volatile bool _scanDone, _scanQueued;
    /// <summary>The folder's size known (the Settings rows' first show): reconciled now on the main thread under the test clock (the readout is
    /// then the same at the same frame in every run), on the worker otherwise (the readout shows "..." until it is done).</summary>
    public static void EnsureScanned()
    {
        if (_scanDone || _scanQueued) return;
        if (!Time.Wall) { Store.Reconcile(); _scanDone = true; return; }
        _scanQueued = true; Enqueue(new ScanJob());
    }
    /// <summary>The readout's text: "1.2 MB" (or "..." while the first reconcile runs).</summary>
    public static string InUseText()
    {
        var s = _store; long b = s == null ? -1 : s.InUse;
        return b < 0 ? "..." : K10Store.Mb(b);
    }

    /// <summary>The cache/ folders Clear Cache keeps (review fix 2026-10-28): json/ = the catalog bake the kit stages there (kit #5's memory fix:
    /// boot arena peak 105 -> 66 MiB; a start without it parses the 9.9 MB catalog and bakes it again -- the high-memory boot no gate ran after
    /// a Clear). <c>SHEPHERD_K10_CLEAR_KEEP=none</c> = c5's Clear (everything); <c>=a,b</c> = those folders.</summary>
    public static readonly string[] ClearKeep = ParseKeep(Environment.GetEnvironmentVariable("SHEPHERD_K10_CLEAR_KEEP"));
    static string[] ParseKeep(string v)
    {
        if (v == null) return new[] { "json" };
        v = v.Trim();
        if (v.Length == 0 || v == "none" || v == "0") return null;
        var t = v.Split(',');
        for (int i = 0; i < t.Length; i++) t[i] = t[i].Trim().Trim('/');
        return t;
    }
    /// <summary>What Clear Cache would delete, for its confirmation ("..." before the folder is known).</summary>
    public static string ClearableText()
    {
        var s = _store; long b = s == null ? -1 : s.Clearable(ClearKeep);
        return b < 0 ? "..." : K10Store.Mb(b);
    }

    /// <summary>Clear Cache's state: 0 idle, 1 clearing, 2 done (<see cref="ClearedBytes"/>), 3 done with files left (<see cref="ClearFailed"/>).</summary>
    public static volatile int ClearState;
    public static long ClearedBytes; public static int ClearFailed;
    /// <summary>Bumped by every Clear Cache: the learning kinds drop what they hold in memory (so the session does not write it back).</summary>
    public static volatile int Generation;
    sealed class ClearJob : Job
    {
        public override void Run()
        {
            long b = Store.Clear(ClearKeep, out int failed);
            ClearedBytes = b; ClearFailed = failed; Generation++;
            Log.Info("[k10] Clear Cache: " + K10Store.Mb(b) + " deleted from " + Dir + (failed > 0 ? " (" + failed.ToString() + " file(s) could not be deleted)" : "") + " -- saves and settings untouched" +
                (ClearKeep != null ? "; kept " + string.Join("/, ", ClearKeep) + "/ (" + K10Store.Mb(Store.InUse) + ": what the kit staged)" : "") + "; the next start rebuilds what it needs");
            ClearState = failed > 0 ? 3 : 2;
        }
    }
    /// <summary>Clear Cache confirmed in the game's confirmation window: everything under cache/ deleted on the worker.</summary>
    public static void RequestClear()
    {
        if (ClearState == 1) return;
        ClearState = 1;
        Enqueue(new ClearJob());   // (the player asked: not held for a window)
    }

    // ------------------------------------------------------------------ Verify Files (the io track's verify core, by name)

    /// <summary>The io track's verify-files core (kit #10 io: the pack's integrity hashes), found by name so this row builds without it: the
    /// public static method <c>Shepherd.Engine.Runtime.K10Io.VerifyFiles(IVerifyProgress progress, bool deep)</c> returning a result with
    /// <c>string Summary()</c> and <c>bool Ok</c> (io's shape as of its kit #10 work; a null progress is accepted). It runs here on the cache
    /// worker (the row asks; never the main thread); the row shows "Checking..." and then the summary. Absent = the row is greyed ("not in this
    /// build"). <b>Stop</b> (review fix 2026-10-28): when the window it was started in closes (a scene load starts, or the eboot apps' Settings
    /// menu is left) <see cref="StopWanted"/> is set; a progress object answers io's <c>Step(done, total)</c> with it -- the type
    /// <c>Shepherd.Engine.Runtime.K10VerifyStop</c> (implements io's IVerifyProgress, so it exists only once io's core is merged: the
    /// integration adds the 6-line file cache-work/integration/K10VerifyStop.cs), found by name like the core; without it the check cannot be
    /// stopped (this branch alone has no core at all: the row is greyed).</summary>
    public static class Verify
    {
        static bool _looked; static MethodInfo _run; static Type _stopType;
        /// <summary>Set when the check's window closes: the progress object's Step answers false (io's core then returns Stopped).</summary>
        public static volatile bool StopWanted;
        /// <summary>The progress object's last report (bytes checked / total).</summary>
        public static long Done, Total;
        /// <summary>The progress object's answer to io's Step (K10VerifyStop): keep going?</summary>
        public static bool Step(long done, long total) { Done = done; Total = total; return !StopWanted; }
        public static bool Available { get { Look(); return _run != null; } }
        /// <summary>0 idle, 1 running, 2 done (<see cref="Result"/>, <see cref="Ok"/>).</summary>
        public static volatile int State;
        public static string Result; public static bool Ok;
        static void Look()
        {
            if (_looked) return; _looked = true;
            try
            {
                var t = typeof(K10Cache).Assembly.GetType("Shepherd.Engine.Runtime.K10Io");
                if (t == null) return;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "VerifyFiles" && m.GetParameters().Length >= 1) { _run = m; break; }
                _stopType = typeof(K10Cache).Assembly.GetType("Shepherd.Engine.Runtime.K10VerifyStop");
            }
            catch (Exception) { _run = null; }
        }
        sealed class VerifyJob : Job
        {
            public override void Run()
            {
                try
                {
                    var ps = _run.GetParameters(); var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                        args[i] = ps[i].ParameterType == typeof(bool) ? false   // (deep: no)
                                : _stopType != null && ps[i].ParameterType.IsAssignableFrom(_stopType) ? Activator.CreateInstance(_stopType) : null;   // (progress: the stop, when merged)
                    var r = _run.Invoke(null, args);
                    Result = r?.GetType().GetMethod("Summary", Type.EmptyTypes)?.Invoke(r, null) as string ?? "done";
                    Ok = r?.GetType().GetField("Ok")?.GetValue(r) is true;
                    Stopped = r?.GetType().GetField("Stopped")?.GetValue(r) is true;
                }
                catch (Exception e) { Result = "could not run (" + e.GetBaseException().Message + ")"; Ok = false; }
                Log.Info("[k10] Verify Files: " + Result);
                State = 2;
            }
        }
        /// <summary>The last check was stopped (its window closed) before it finished.</summary>
        public static bool Stopped;
        public static bool Begin()
        {
            if (!Available || State == 1) return false;
            State = 1; Result = null; StopWanted = false; Stopped = false;
            Enqueue(new VerifyJob());
            return true;
        }
    }

    /// <summary>A summary line for the end of the session's log (counts only).</summary>
    public static string Summary()
    {
        if (!On) return null;
#if !SHEPHERD_NO_K8
        if (K10Learn.On) Log.Info(K10Learn.Summary());
#endif
        var s = _store;
        if (s == null) return "[k10] learning cache ON (not used this session)";
        return "[k10] learning cache: " + K10Store.Mb(s.InUse) + " in use of " + K10Store.Mb(s.Cap) + ", " + s.Files.ToString() + " files; hits " + s.Hits.ToString() + ", misses " + s.Misses.ToString() +
            ", damaged " + s.Damaged.ToString() + ", verified " + s.Verified.ToString() + ", written " + s.Writes.ToString() + ", refused " + s.Refused.ToString() + ", evicted " + s.Evicted.ToString() + " (" + K10Store.Mb(s.EvictedBytes) + ")" +
            "; the worker started " + Starts.ToString() + " time(s)";
    }
}
