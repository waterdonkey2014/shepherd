using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Assets;
using Shepherd.Engine.Assets;
using Shepherd.Engine.Render;
namespace Shepherd.Engine.Runtime;
#if !SHEPHERD_NO_K8

/// <summary>Device kit #8, track boot (vita/notes/kit8-boot.md): IDLE-WINDOW PRELOADING (first use) and the MAIN MENU HEAD START, as data only.
/// While the game sits in a scene whose next scene is known -- the opening quotes (QuoteScreen, ~20 s) before 'Game Biome Intro', the Main
/// Menu before what was chosen there last time (New Game: the quotes, then the intro) -- a worker on the spare core reads the next scene's
/// pack data into a store in free VIDEO memory, in the order that scene read it last time; when the scene loads, the pack reader takes those
/// bytes from the store instead of the card. Nothing else changes: no game code runs early, no object is created early, no random number is
/// touched; the bytes the loader gets are the pack's own (only when they came off the card moves), the scene loads when and as the game asks.
/// <list type="bullet">
/// <item><b>The read-order map</b> (a learning cache, owner-local, per pack: ux0:data/shepherd/cache/readorder/): every pack read a scene load
///   makes (Shepherd.Assets PackReader.OnRead: offset, length, in call order, from the load's start until 600 frames after it, at most 64 MiB)
///   is kept as &lt;pack&gt;-&lt;scene&gt;.ro; which scene followed which as &lt;pack&gt;-next.tsv. Written only when they change (temp + rename).</item>
/// <item><b>The prefetch</b>: 60 frames after a scene became active (after the scene a store was read for: once its 600-frame map window is
///   over and that store is let go -- two stores never hold video memory at once), its learned next scene's map -- and, when that next scene is passed
///   through quickly (its own map is under 4 MiB), the one after it (the Main Menu: the quotes, then the intro) -- is read in map order into
///   the store (PackReader.PreloadRead: the card, 512 KiB pieces, so shepaudio's stream refills get in between), up to the budget, by one
///   worker thread on the 4th core (else USER_2), made once (the main thread waits for its start-up: a fixed point of the allocation order)
///   and then waiting for jobs; it allocates nothing while it works. A scene load that starts stops it at once (the loader has the card).</item>
/// <item><b>The store</b> (PackReader.IPackStore): one block of vitaGL's VRAM (shepgl_vram_alloc; main RAM untouched), entries by pack offset
///   (an open-addressing table on plain arrays); a read the store holds (the same offset, a length it covers) is copied out of it and the
///   entry let go; any other read goes to the card as before. Released when its last target scene's map window ends (600 frames after
///   that scene is in), when the next scene's prefetch starts, or when another scene loads instead.</item>
/// </list>
/// Budget SHEPHERD_K8_PRELOAD_MB (default 16, 2..48), never more than the VRAM free beyond 24 MiB (textures keep their room).
/// <c>SHEPHERD_K8_PRELOAD=0</c> (or <c>SHEPHERD_K8_BOOT=0</c>) = kit #7 (no map, no store, no worker). Vita GL host only. Plain arrays and an
/// interface (no new generic collection / delegate instantiations: AOT code is eboot span).</summary>
[ProfileOnly]
public static unsafe class K8Preload
{
    public static readonly bool On = Decide();
    static bool Decide()
    {
        if (!Profile.Vita || !ShepGl.On || !Diag.VitaHost.On || !PackAssets.On) return false;
        if (Environment.GetEnvironmentVariable("SHEPHERD_K8_BOOT") == "0") return false;
        return Environment.GetEnvironmentVariable("SHEPHERD_K8_PRELOAD") != "0";
    }
    static readonly int BudgetMiB = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K8_PRELOAD_MB"), out var b) && b >= 2 && b <= 48 ? b : 16;
    const long MapCap = 64L << 20;
    const int MapWindowFrames = 600, SettleFrames = 60, MaxEntries = 8192, Slots = 16384;

    static string Dir => Path.Combine(Program.Root, "cache", "readorder");
    static string MapPath(string scene) => Path.Combine(Dir, PackAssets.Reader.Identity + "-" + Safe(scene) + ".ro");
    /// <summary>A map is kept per transition (from &gt; to): a pass-through scene reads different data coming from different places.</summary>
    static string MapKey(string from, string to) => from != null ? from + ">" + to : to;
    static string Safe(string s) { var c = s.ToCharArray(); for (int i = 0; i < c.Length; i++) if (!char.IsLetterOrDigit(c[i]) && c[i] != '-' && c[i] != '_') c[i] = '_'; return new string(c); }

    // ---------------- the read-order map (the main thread and the reading threads: under the log's lock) ----------------
    sealed class ReadLog : PackReader.IPackReadLog
    {
        public string Scene; public long[] Off = new long[4096]; public int[] Len = new int[4096]; public int N; public long Bytes; public int End = -1;
        public void OnRead(long off, int len)
        {
            lock (this)
            {
                if (Scene == null || Bytes >= MapCap) return;
                if (N == Off.Length) { var o = new long[N * 2]; Array.Copy(Off, o, N); Off = o; var l = new int[N * 2]; Array.Copy(Len, l, N); Len = l; }
                Off[N] = off; Len[N] = len; N++; Bytes += len;
            }
        }
    }
    static readonly ReadLog _log = new();
    static void SaveMap()
    {
        string scene; int n; byte[] b;
        lock (_log)
        {
            scene = _log.Scene; n = _log.N; _log.Scene = null; _log.End = -1;
            b = n > 0 && scene != null ? new byte[16 + n * 12] : null;
            for (int i = 0; b != null && i < n; i++) { BitConverter.TryWriteBytes(b.AsSpan(16 + i * 12), _log.Off[i]); BitConverter.TryWriteBytes(b.AsSpan(24 + i * 12), _log.Len[i]); }
            long bytes = _log.Bytes; _log.N = 0; _log.Bytes = 0;
            if (b == null) return;
            BitConverter.TryWriteBytes(b.AsSpan(0), 0x4F52384Bu);   // "K8RO"
            BitConverter.TryWriteBytes(b.AsSpan(4), 1);
            BitConverter.TryWriteBytes(b.AsSpan(8), n);
            BitConverter.TryWriteBytes(b.AsSpan(12), Fnv(b.AsSpan(16)));
            _lastSavedBytes = bytes;
        }
        var path = MapPath(scene);
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(b)) return;   // unchanged: no write
            Directory.CreateDirectory(Dir);
            var err = GxpCache.WriteAtomic(path, b);
            Log.Info("[k8] preload: read-order map of '" + scene + "': " + n.ToString() + " reads, " + (_lastSavedBytes / 1048576.0).ToString("F1") + " MiB" + (err == null ? " -> " + path : " NOT written (" + err + ")"));
        }
        catch (Exception e) { Log.Warn("[k8] preload: read-order map of '" + scene + "' not written: " + e.GetBaseException().Message); }
    }
    static long _lastSavedBytes;
    static uint Fnv(ReadOnlySpan<byte> b) { uint h = 2166136261u; foreach (var x in b) { h ^= x; h *= 16777619u; } return h; }
    /// <summary>A learned map (offsets, lengths, the bytes it lists), or nulls (missing / damaged / another version).</summary>
    static bool LoadMap(string scene, out long[] off, out int[] len, out long bytes)
    {
        off = null; len = null; bytes = 0;
        try
        {
            var path = MapPath(scene); if (!File.Exists(path)) return false;
            var b = File.ReadAllBytes(path);
            if (b.Length < 16 || BitConverter.ToUInt32(b, 0) != 0x4F52384Bu || BitConverter.ToInt32(b, 4) != 1) return false;
            int n = BitConverter.ToInt32(b, 8);
            if (n <= 0 || b.Length != 16 + n * 12 || BitConverter.ToUInt32(b, 12) != Fnv(b.AsSpan(16))) return false;
            off = new long[n]; len = new int[n];
            for (int i = 0; i < n; i++) { off[i] = BitConverter.ToInt64(b, 16 + i * 12); len[i] = BitConverter.ToInt32(b, 24 + i * 12); bytes += len[i]; }
            return true;
        }
        catch (Exception) { off = null; len = null; return false; }
    }

    // ---------------- which scene followed which (<pack>-next.tsv: "from TAB to" lines) ----------------
    static string[] _nFrom = new string[16], _nTo = new string[16]; static int _nN = -1;
    static string NextOf(string from)
    {
        if (_nN < 0)
        {
            _nN = 0;
            try
            {
                var path = Path.Combine(Dir, PackAssets.Reader.Identity + "-next.tsv");
                if (File.Exists(path)) foreach (var l in File.ReadAllLines(path)) { var t = l.Split('\t'); if (t.Length == 2 && t[0].Length > 0 && t[1].Length > 0) SetNext(t[0], t[1]); }
            }
            catch (Exception) { }
        }
        for (int i = 0; i < _nN; i++) if (_nFrom[i] == from) return _nTo[i];
        return null;
    }
    static bool SetNext(string from, string to)
    {
        for (int i = 0; i < _nN; i++) if (_nFrom[i] == from) { if (_nTo[i] == to) return false; _nTo[i] = to; return true; }
        if (_nN == _nFrom.Length) { var f = new string[_nN * 2]; Array.Copy(_nFrom, f, _nN); _nFrom = f; var t = new string[_nN * 2]; Array.Copy(_nTo, t, _nN); _nTo = t; }
        _nFrom[_nN] = from; _nTo[_nN] = to; _nN++; return true;
    }
    /// <summary>What followed <paramref name="cur"/> when it was reached from <paramref name="prev"/> (a pass-through scene -- the game's
    /// BufferScene between every two scenes -- leads to different scenes from different places), else what followed it last time.</summary>
    static string Next2(string prev, string cur) => (prev != null ? NextOf(prev + ">" + cur) : null) ?? NextOf(cur);
    static void NoteNext(string prev, string from, string to)
    {
        if (from == null || from == to) return;
        NextOf(from);   // (loads the file once)
        bool changed = SetNext(from, to);
        if (prev != null) changed |= SetNext(prev + ">" + from, to);
        if (!changed) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _nN; i++) sb.Append(_nFrom[i]).Append('\t').Append(_nTo[i]).Append('\n');
            GxpCache.WriteAtomic(Path.Combine(Dir, PackAssets.Reader.Identity + "-next.tsv"), System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        }
        catch (Exception) { }
    }

    // ---------------- the store ----------------
    sealed class Store : PackReader.IPackStore
    {
        public IntPtr Base; public long Cap, Used;
        public readonly long[] Off = new long[MaxEntries], At = new long[MaxEntries]; public readonly int[] Len = new int[MaxEntries]; public readonly bool[] Live = new bool[MaxEntries];
        public readonly long[] Key = new long[Slots]; public readonly int[] Val = new int[Slots];   // open addressing: key = offset + 1 (0 = empty)
        public int N; public string T1, T2;   // the targets (a chain's second may be null)
        public string Next;                   // the scene the store waits for: T1, then T2 once T1 is in, then null (it has served its chain)
        public long Hits, HitBytes;
        static int H(long k) => (int)((uint)((int)k ^ (int)(k >> 32)) * 2654435761u >> 18);   // 14 bits: Slots = 16384
        public int Find(long off)
        {
            long k = off + 1;
            for (int i = H(k), probe = 0; probe < Slots; i = (i + 1) & (Slots - 1), probe++) { if (Key[i] == 0) return -1; if (Key[i] == k) return Val[i]; }
            return -1;
        }
        public void Put(long off, int entry)
        {
            long k = off + 1;
            for (int i = H(k), probe = 0; probe < Slots; i = (i + 1) & (Slots - 1), probe++) if (Key[i] == 0 || Key[i] == k) { Key[i] = k; Val[i] = entry; return; }
        }
        public bool Take(long offset, Span<byte> dst)
        {
            lock (this)
            {
                int i = Find(offset);
                if (i < 0 || !Live[i] || dst.Length > Len[i]) return false;
                new ReadOnlySpan<byte>((void*)(Base + (nint)At[i]), dst.Length).CopyTo(dst);
                Live[i] = false; Hits++; HitBytes += dst.Length;
                return true;
            }
        }
    }
    static Store _store;
    static void Release(string why)
    {
        var s = _store; if (s == null) return;
        StopWorker();
        PackReader.Store = null; _store = null;
        long unused = 0; int unusedN = 0;
        lock (s) for (int i = 0; i < s.N; i++) if (s.Live[i]) { unused += s.Len[i]; unusedN++; }
        if (s.Base != IntPtr.Zero) VramFree(s.Base);
        Log.Info("[k8] preload store for '" + s.T1 + "' released (" + why + "): " + s.N.ToString() + " reads read ahead (" + (s.Used / 1048576.0).ToString("F1") + " MiB), "
            + s.Hits.ToString() + " served the loader (" + (s.HitBytes / 1048576.0).ToString("F1") + " MiB), " + unusedN.ToString() + " unused (" + (unused / 1048576.0).ToString("F1") + " MiB)");
    }

    // ---------------- the worker (one thread, made once; waits for jobs) ----------------
    static Thread _thread; static readonly object _wk = new(); static bool _ready, _go, _busy; static volatile bool _stop;
    static long[] _jobOff; static int[] _jobLen; static int _jobN;
    static void EnsureWorker()
    {
        if (_thread != null) return;
        _thread = new Thread(WorkerLoop, 128 * 1024) { IsBackground = true, Name = "shepherd-preload", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        lock (_wk) while (!_ready) Monitor.Wait(_wk);
    }
    static void WorkerLoop()
    {
        try { Diag.VitaHost.PinWorker("shepherd-preload"); } catch { }
        lock (_wk) { _ready = true; Monitor.PulseAll(_wk); }
        for (;;)
        {
            Store s;
            lock (_wk) { while (!_go) Monitor.Wait(_wk); _go = false; _busy = true; s = _store; }
            long[] off = _jobOff; int[] len = _jobLen; int n = _jobN;
            if (s != null && off != null)
            {
                var reader = PackAssets.Reader;
                for (int i = 0; i < n && !_stop; i++)
                {
                    int l = len[i];
                    if (l <= 0) continue;
                    bool have;
                    lock (s) have = s.Find(off[i]) >= 0;   // read ahead already (an earlier job of the same chain, or a repeated read)
                    if (have) continue;
                    if (s.Used + l > s.Cap || s.N >= MaxEntries) break;
                    long at = s.Used;
                    if (!reader.PreloadRead(off[i], new Span<byte>((void*)(s.Base + (nint)at), l))) break;
                    lock (s) { int k = s.N++; s.Off[k] = off[i]; s.Len[k] = l; s.At[k] = at; s.Live[k] = true; s.Put(off[i], k); s.Used = at + l; }
                }
            }
            lock (_wk) { _busy = false; Monitor.PulseAll(_wk); }
        }
    }
    static void StartJob(long[] off, int[] len, int n)
    {
        EnsureWorker();
        lock (_wk) { _stop = false; _jobOff = off; _jobLen = len; _jobN = n; _go = true; Monitor.PulseAll(_wk); }
    }
    static void StopWorker()
    {
        if (_thread == null) return;
        _stop = true;
        lock (_wk) while (_busy || _go) Monitor.Wait(_wk, 50);
    }

    // ---------------- the scene hooks (Icalls/BundleIcalls.cs LoadSceneAsync's completion; the main thread) ----------------
    static int _prefetchAt = -1; static string _activeScene, _prevScene; static bool _bootSeen;
    /// <summary>A scene load begins (the game asked; the loader is about to instantiate it).</summary>
    public static void OnLoadStart(string scene)
    {
        if (!On || string.IsNullOrEmpty(scene)) return;
        StopWorker();   // the loader has the card now
        if (_log.Scene != null) SaveMap();
        NoteNext(_prevScene, _activeScene, scene);
        if (_store != null && _store.Next != scene) Release("'" + scene + "' loads instead");   // (a store serves only the visit it was read for)
        PackReader.OnRead = _log;
        lock (_log) { _log.Scene = MapKey(_activeScene, scene); _log.N = 0; _log.Bytes = 0; _log.End = -1; }
        _prefetchAt = -1;
    }
    /// <summary>The scene is instantiated and active.</summary>
    public static void OnLoadDone(string scene)
    {
        if (!On || string.IsNullOrEmpty(scene)) return;
        _prevScene = _activeScene; _activeScene = scene;
        lock (_log) _log.End = Time.FrameCount + MapWindowFrames;
        if (_store != null && _store.Next == scene) _store.Next = scene == _store.T1 ? _store.T2 : null;
        // the next prefetch: 60 frames on; after a store's last target, once that scene's map window is over and the store is let go (its
        // late reads are in the map: the two stores never hold video memory at once)
        _prefetchAt = Time.FrameCount + (_store != null && _store.Next == null ? MapWindowFrames + 1 : SettleFrames);
    }
    /// <summary>Each frame (Lifecycle.Frame): the map window's end, the prefetch's start.</summary>
    public static void Tick()
    {
        if (!On) return;
        int f = Time.FrameCount;
        if (!_bootSeen)
        {   // the boot's splash scene (level0, loaded by the boot, not through LoadSceneAsync): the Devolver logo and the controller notice are
            // the boot's long idle window -- what follows it (the BufferScene, then the Main Menu) is prefetched once the boot has settled
            _bootSeen = true; _activeScene ??= "level0"; _prefetchAt = f + 120;
        }
        if (_log.End >= 0 && f >= _log.End)
        {   // the scene's map window is over: its map is kept; a store whose last target is this scene has served it -- its video memory goes
            // back now (not only at the next prefetch / load: the Main Menu can sit for minutes and has no learned next scene on a first run)
            SaveMap();
            if (_store != null && _store.Next == null) Release("'" + _activeScene + "' is in");
        }
        if (_prefetchAt >= 0 && f >= _prefetchAt) { _prefetchAt = -1; Prefetch(); }
    }
    static void Prefetch()
    {
        var target = _activeScene != null ? Next2(_prevScene, _activeScene) : null;
        if (target == null) return;
        if (!LoadMap(MapKey(_activeScene, target), out var off, out var len, out var bytes1)) { Log.Info("[k8] preload: '" + _activeScene + "' -> '" + target + "': no read-order map yet (made on that scene's next load)"); return; }
        int n = off.Length;
        // a short pass-through scene (the quotes before the intro): its next one's map too, while the budget lasts
        string target2 = null;
        var t2 = bytes1 < (4L << 20) ? Next2(_activeScene, target) : null;
        if (t2 != null && t2 != _activeScene && LoadMap(MapKey(target, t2), out var o2, out var l2, out _))
        {
            target2 = t2;
            var oo = new long[n + o2.Length]; Array.Copy(off, oo, n); Array.Copy(o2, 0, oo, n, o2.Length);
            var ll = new int[n + l2.Length]; Array.Copy(len, ll, n); Array.Copy(l2, 0, ll, n, l2.Length);
            off = oo; len = ll; n = oo.Length;
        }
        if (_store != null && _store.Next == target) { StartJob(off, len, n); return; }   // between a chain's two scenes: the same store reads on
        Release("a new target");
        long vramFree = ShepGl.PoolKiB(0, false) * 1024L;
        long cap = Math.Min((long)BudgetMiB << 20, vramFree - (24L << 20));
        if (cap < (2L << 20)) { Log.Info("[k8] preload: '" + target + "': not enough free video memory (" + (vramFree >> 20).ToString() + " MiB free)"); return; }
        var basePtr = VramAlloc((uint)cap);
        if (basePtr == IntPtr.Zero) { Log.Info("[k8] preload: '" + target + "': the video memory block was refused"); return; }
        _store = new Store { Base = basePtr, Cap = cap, T1 = target, T2 = target2, Next = target };
        PackReader.Store = _store;
        Log.Info("[k8] preload: '" + _activeScene + "' -> '" + target + "'" + (target2 != null ? " -> '" + target2 + "'" : "") + ": reading its " + n.ToString() + " pack reads ahead, up to "
            + (cap >> 20).ToString() + " MiB of video memory (" + (vramFree >> 20).ToString() + " MiB free)");
        StartJob(off, len, n);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static IntPtr VramAlloc(uint bytes) => shepgl_vram_alloc(bytes, 256);
    [MethodImpl(MethodImplOptions.NoInlining)] static void VramFree(IntPtr p) => shepgl_vram_free(p);
    [DllImport("__Internal")] static extern IntPtr shepgl_vram_alloc(uint bytes, uint align);
    [DllImport("__Internal")] static extern void shepgl_vram_free(IntPtr p);
}
#endif   // (SHEPHERD_NO_K8, vita/shepherd/build.sh SHEPHERD_K8_ENGINE=0: no kit #8 engine code -- every use is under the same #if)
