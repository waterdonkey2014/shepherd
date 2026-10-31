using System.Text;
using Shepherd.Assets;
using Shepherd.Engine.Assets;
namespace Shepherd.Engine.Runtime;
#if !SHEPHERD_NO_K8

/// <summary>Device kit #10, track cache (vita/notes/kit10-cache.md): THE LEARNING KINDS of the launcher apps (with kit #8's preload,
/// Runtime/K8Preload.cs; the eboot apps leave both out: SHEPHERD_NO_K8, eboot span). Everything goes through the learning cache's store
/// (Runtime/K10Store.cs: exact keys = engine + game + this pack + the kind's inputs, verified on first reuse, the cap, the margin) and every
/// card write waits for an idle window (Runtime/K10Cache.cs). Data only: no game code runs early, no object is made early, no random number
/// is touched; a stale or missing entry only makes a load slower.
/// <list type="bullet">
/// <item><b>Read-order maps</b> (kind "readorder", one per scene transition) and the next-scene table (kind "next") -- kit #8's, now in the
///   store: a map read back unverified is sanity-checked (every read inside the pack) and serves as a hint; when that scene's map window ends
///   the new recording is compared with it -- identical = verified, else replaced.</item>
/// <item><b>Preload hit statistics</b> (kind "pstats", counts only; owner 2026-10-16): per prediction (from -&gt; target) how often it was made and
///   came true, bytes read ahead / served / unused; per map read (kind "ruse") how many prefetches in a row it went unused. A prediction that
///   came true less than half of 4+ times is not prepared; a read unused 3 times in a row is skipped -- the memory goes to what you use.</item>
/// <item><b>Per-scene high-water marks</b> (kind "hwm"): each transition's reads and bytes, the engine's live objects and the managed heap at
///   the load's end. Pools pre-sized from them before the load: the read-order map's arrays, the preload store's video memory (exactly what
///   the prefetch reads, not the whole budget).</item>
/// <item><b>Continue manifests</b> (kind "continue", keyed by the save file's 64-bit hash; owner 2026-10-16): when the game writes a slot
///   (slot_N.mp in the save folder changes: its name / size / time looked at every 300 frames in a window and at every scene load's start),
///   the pack reads of the scene it was saved in -- its LOAD's reads: from the load's start to 600 frames after it is in, the read-order map's
///   window, what a Continue load reads by the time the manifest is checked (review fix 2026-10-28); up to 16,384 reads / 64 MiB -- are kept
///   under that save's hash; the slot's previous manifest is dropped. At the Main Menu each slot file's hash is known again (its name / size /
///   time stamp from kind "savestamp", else the file read and hashed -- never parsed) and, when the learned next scene is a slot's scene, the
///   head start reads that slot's manifest (the newest save first). A stale or missing manifest only means a slower load.</item>
/// <item><b>The idle-window scheduler</b>: the windows are the boot's logos (level0), the BufferScene, the quotes, the Main Menu (and the
///   Settings menu: Runtime/VitaToggles.cs), never a scene load or gameplay; the cache's writes, the save hashing and the index wait for one,
///   or for the end of the next load once 4 MiB wait (the write discipline's cap). The prefetch's reads keep kit #8's timing.</item>
/// </list>
/// <c>SHEPHERD_K10_LEARN=0</c> = kit #9's preload exactly (maps in cache/readorder/, no statistics, no manifests); each part also has its own:
/// <c>SHEPHERD_K10_STATS=0</c>, <c>SHEPHERD_K10_HWM=0</c>, <c>SHEPHERD_K10_CONTINUE=0</c>, <c>SHEPHERD_K10_IDLE=0</c> (writes at once, as kit #9).
/// Code size: plain arrays, text payloads, log text by concatenation, no LINQ / lambdas / new generic instantiations (AOT code is span).</summary>
[ProfileOnly]
public static class K10Learn
{
    static string Env(string k) => Environment.GetEnvironmentVariable(k);
    public static readonly bool On = K8Preload.On && K10Cache.On && Env("SHEPHERD_K10_LEARN") != "0";
    public static readonly bool Stats = On && Env("SHEPHERD_K10_STATS") != "0";
    public static readonly bool Hwm = On && Env("SHEPHERD_K10_HWM") != "0";
    public static readonly bool Continue = On && Env("SHEPHERD_K10_CONTINUE") != "0";
    public static readonly bool Idle = On && Env("SHEPHERD_K10_IDLE") != "0";
    const int VMap = 1, VNext = 1, VStats = 1, VUse = 1, VHwm = 2, VCont = 1, VStamp = 1;   // (hwm 2: the marks are the highest seen, not the last)

    static string _pack;
    static string Pack => _pack ??= PackAssets.Reader.Identity;
    static K10Store S => K10Cache.Store;
    static string Key(string kind, int v, string inputs) => K10Cache.Key(kind, v, Pack, inputs);
    static int _gen = -1;   // the Clear Cache generation the in-memory tables belong to

    /// <summary>A write for the cache worker (in an idle window): the entry put, or -- for a map -- compared with the stored one first; for
    /// high-water marks (<see cref="Max"/>) merged with the stored ones (each the highest seen), written only when one grew.</summary>
    sealed class PutJob : K10Cache.Job
    {
        public string Kind, Inputs; public int V; public byte[] Payload; public bool Compare, Max; public string What;
        public override void Run()
        {
            if (K10Cache.Generation != _jobGen) return;   // (cleared since it was queued: not written back)
            var key = Key(Kind, V, Inputs);
            var want = Payload;
            if (Compare)
            {
                var old = S.Get(Kind, key, V, out bool ver);
                if (Max && old != null) want = MaxMarks(old, Payload);
                if (old != null && old.AsSpan().SequenceEqual(want))
                {
                    if (!ver) { S.Confirm(Kind, key); Log.Info("[k10] " + What + (Max ? ": within the stored marks -- verified" : ": the same as the stored entry -- verified")); }
                    return;
                }
                if (old != null) Log.Info("[k10] " + What + (Max ? ": above the stored marks -- raised (" + Encoding.UTF8.GetString(want) + ")" : ": differs from the stored entry -- replaced"));
                if (old != null && Kind == "readorder") DropRuse(Inputs, old);   // (the replaced map's read-use streaks are keyed by its bytes: never matched again)
            }
            S.Put(Kind, key, V, want);
        }
        public int _jobGen;
    }
    static void Put(string kind, int v, string inputs, byte[] payload, bool compare, string what, bool max = false)
    {
        var j = new PutJob { Kind = kind, V = v, Inputs = inputs, Payload = payload, Compare = compare, Max = max, What = what, Write = Idle, Bytes = payload.Length + 256, _jobGen = K10Cache.Generation };
        K10Cache.Enqueue(j);
    }
    /// <summary>"k=v k=v ..." marks: each the higher of this visit's and the stored one (this visit's keys, in its order; anything unreadable =
    /// this visit's marks as they are).</summary>
    static byte[] MaxMarks(byte[] stored, byte[] cur)
    {
        var o = Encoding.UTF8.GetString(stored).Split(' '); var c = Encoding.UTF8.GetString(cur).Split(' ');
        var sb = new StringBuilder();
        for (int i = 0; i < c.Length; i++)
        {
            int e = c[i].IndexOf('=');
            if (e <= 0 || !long.TryParse(c[i].Substring(e + 1), out var v)) return cur;
            var k = c[i].Substring(0, e + 1);
            for (int j = 0; j < o.Length; j++) if (o[j].StartsWith(k, StringComparison.Ordinal) && long.TryParse(o[j].Substring(k.Length), out var ov) && ov > v) v = ov;
            if (i > 0) sb.Append(' ');
            sb.Append(k).Append(v.ToString());
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
    sealed class DropJob : K10Cache.Job { public string Kind, Key; public override void Run() => S.Drop(Kind, Key); }
    /// <summary>(the cache worker, in a window) A map that is replaced or dropped: its read-use entry (kind "ruse", keyed by the map's exact bytes)
    /// can never match again -- deleted now, not left for the LRU (review fix 2026-10-28).</summary>
    static void DropRuse(string mapInputs, byte[] oldMap)
    {
        var inputs = mapInputs + "|bytes=" + IcallManifest.ContentHash(oldMap).ToString("x16");
        S.Drop("ruse", Key("ruse", VUse, inputs)); RuseSet(inputs, null);
    }
    /// <summary>An entry deleted in the next idle window (a delete is a card write).</summary>
    static void DropSoon(string kind, string key) => K10Cache.Enqueue(new DropJob { Kind = kind, Key = key, Write = Idle, Bytes = 512 });
    static byte[] Get(string kind, int v, string inputs, out bool verified) { verified = false; try { return S.Get(kind, Key(kind, v, inputs), v, out verified); } catch (Exception) { return null; } }

    static void Fresh()
    {   // (main thread) a Clear Cache since the statistics were loaded: what this session learned before it is dropped (the next start rebuilds)
        if (_gen == K10Cache.Generation) return;
        _gen = K10Cache.Generation; _stats = null; _statsDirty = false;
    }

    // ------------------------------------------------------------------ read-order maps + the next-scene table (K8Preload)

    /// <summary>A learned map's file bytes (K8Preload's "K8RO" layout), or null. Sanity: every read inside the pack's payload.</summary>
    public static byte[] MapBytes(string mapKey)
    {
        var b = Get("readorder", VMap, "map=" + mapKey, out _);
        if (b == null || b.Length < 16) return null;
        var r = PackAssets.Reader; long lo = r.PayloadOffset, hi = r.PayloadOffset + r.PayloadSize;
        int n = BitConverter.ToInt32(b, 8);
        if (n <= 0 || b.Length != 16 + n * 12) return null;
        for (int i = 0; i < n; i++)
        {
            long o = BitConverter.ToInt64(b, 16 + i * 12); int l = BitConverter.ToInt32(b, 24 + i * 12);
            if (l <= 0 || o < lo || o + l > hi)
            {
                Log.Info("[k10] read-order map '" + mapKey + "': a read outside the pack -- not used");
                DropSoon("readorder", Key("readorder", VMap, "map=" + mapKey)); DropSoon("ruse", Key("ruse", VUse, UseInputs(mapKey, b)));
                return null;
            }
        }
        return b;
    }
    /// <summary>The map recorded this time: compared with the stored one (verified when identical) or put (in an idle window).</summary>
    public static void SaveMap(string mapKey, byte[] b, int reads, long bytes)
    {
        Put("readorder", VMap, "map=" + mapKey, b, true, "read-order map '" + mapKey + "' (" + reads.ToString() + " reads, " + (bytes / 1048576.0).ToString("F1") + " MiB)");
        if (Hwm) NoteHwm(mapKey, reads, bytes);
    }
    public static string NextText() { var b = Get("next", VNext, "next", out _); return b == null ? null : Encoding.UTF8.GetString(b); }
    public static void SaveNext(string text) => Put("next", VNext, "next", Encoding.UTF8.GetBytes(text), false, "next scenes");

    // ------------------------------------------------------------------ preload hit statistics (counts only)

    sealed class Stat { public string Key; public int Pred, Hit; public long Read, Served, Unused; }
    static Stat[] _stats; static int _statN; static bool _statsDirty;
    static Stat StatOf(string key)
    {
        Fresh();
        if (_stats == null)
        {
            _stats = new Stat[32]; _statN = 0;
            var b = Get("pstats", VStats, "stats", out _);
            if (b != null)
                foreach (var line in Encoding.UTF8.GetString(b).Split('\n'))
                {
                    var t = line.Split('\t');
                    if (t.Length != 6 || !int.TryParse(t[1], out var p) || !int.TryParse(t[2], out var h) || !long.TryParse(t[3], out var rd) || !long.TryParse(t[4], out var sv) || !long.TryParse(t[5], out var un)) continue;
                    Add(new Stat { Key = t[0], Pred = p, Hit = h, Read = rd, Served = sv, Unused = un });
                }
        }
        for (int i = 0; i < _statN; i++) if (_stats[i].Key == key) return _stats[i];
        return Add(new Stat { Key = key });
    }
    static Stat Add(Stat s) { if (_statN == _stats.Length) Array.Resize(ref _stats, _statN * 2); _stats[_statN++] = s; return s; }
    static void SaveStats()
    {
        if (!_statsDirty || _stats == null) return;
        _statsDirty = false;
        var sb = new StringBuilder();
        for (int i = 0; i < _statN; i++) { var s = _stats[i]; sb.Append(s.Key).Append('\t').Append(s.Pred.ToString()).Append('\t').Append(s.Hit.ToString()).Append('\t').Append(s.Read.ToString()).Append('\t').Append(s.Served.ToString()).Append('\t').Append(s.Unused.ToString()).Append('\n'); }
        Put("pstats", VStats, "stats", Encoding.UTF8.GetBytes(sb.ToString()), false, "preload statistics");
    }

    static string _predKey, _predTarget;   // the prediction in flight: its stats key and the scene it expects next
    /// <summary>Should the prefetch prepare <paramref name="target"/> from <paramref name="from"/>? No when it came true less than half of 4+
    /// times. True = the prediction is counted (its outcome at the next scene load).</summary>
    public static bool Predict(string from, string target)
    {
        if (!Stats) return true;
        var key = from + ">" + target;
        var s = StatOf(key);
        bool go = !(s.Pred >= 4 && s.Hit * 2 < s.Pred);
        // counted either way (a prediction not prepared still learns whether it came true, so it is prepared again once it does);
        // halved at 16 so the last ~16 outcomes decide
        if (s.Pred >= 16) { s.Pred /= 2; s.Hit /= 2; }
        s.Pred++; _statsDirty = true; _predKey = key; _predTarget = target;
        if (!go) Log.Info("[k10] preload: '" + key + "' not prepared (came true " + s.Hit.ToString() + " of " + (s.Pred - 1).ToString() + " times)");
        return go;
    }
    /// <summary>A scene load begins: the prediction in flight came true or not.</summary>
    static void Outcome(string scene)
    {
        if (_predKey == null) return;
        var s = StatOf(_predKey);
        if (scene == _predTarget) s.Hit++;
        _statsDirty = true; _predKey = _predTarget = null;
    }

    // per map read: prefetches in a row it went unused (kind "ruse", keyed by the map's exact bytes). Kept in memory for the session once read
    // (review fix 2026-10-28: no card read on the main thread in a gameplay frame -- the streaks are read there only in a menu-like scene, and
    // merged / written by the cache worker in a window)
    static string UseInputs(string mapKey, byte[] map) => "map=" + mapKey + "|bytes=" + IcallManifest.ContentHash(map).ToString("x16");
    static readonly object _ruk = new();
    static string[] _ruK = new string[16]; static byte[][] _ruV = new byte[16][]; static int _ruN, _ruGen = -1;   // (an empty array = known: none stored)
    static byte[] RuseMem(string inputs)
    {
        lock (_ruk)
        {
            if (_ruGen != K10Cache.Generation) { _ruGen = K10Cache.Generation; Array.Clear(_ruK); Array.Clear(_ruV); _ruN = 0; }   // (cleared: start over)
            for (int i = 0; i < _ruN; i++) if (_ruK[i] == inputs) return _ruV[i];
            return null;
        }
    }
    static void RuseSet(string inputs, byte[] v)
    {
        lock (_ruk)
        {
            if (_ruGen != K10Cache.Generation) { _ruGen = K10Cache.Generation; Array.Clear(_ruK); Array.Clear(_ruV); _ruN = 0; }
            for (int i = 0; i < _ruN; i++)
                if (_ruK[i] == inputs)
                {
                    if (v != null) { _ruV[i] = v; return; }
                    _ruN--; _ruK[i] = _ruK[_ruN]; _ruV[i] = _ruV[_ruN]; _ruK[_ruN] = null; _ruV[_ruN] = null; return;
                }
            if (v == null) return;
            if (_ruN == _ruK.Length) { Array.Resize(ref _ruK, _ruN * 2); Array.Resize(ref _ruV, _ruN * 2); }
            _ruK[_ruN] = inputs; _ruV[_ruN] = v; _ruN++;
        }
    }
    sealed class RuseLoadJob : K10Cache.Job
    {   // (in a window) a map's streaks read into memory for its next prefetch this session
        public string Inputs; public int _jobGen;
        public override void Run() { if (K10Cache.Generation != _jobGen || RuseMem(Inputs) != null) return; RuseSet(Inputs, Get("ruse", VUse, Inputs, out _) ?? Array.Empty<byte>()); }
    }
    /// <summary>The map's reads the prefetch should read: those not unused 3 prefetches in a row. Returns the kept count (arrays compacted).
    /// In a gameplay scene only what this session already holds in memory is used (no card read on the main thread there; the streaks are
    /// read in the next window for the map's next prefetch).</summary>
    public static int Filter(string mapKey, byte[] map, long[] off, int[] len, int n)
    {
        if (!Stats) return n;
        var inputs = UseInputs(mapKey, map);
        var u = RuseMem(inputs);
        if (u == null)
        {
            if (!IsIdleScene(_active))
            {
                K10Cache.Enqueue(new RuseLoadJob { Inputs = inputs, _jobGen = K10Cache.Generation, Write = Idle, Bytes = 0 });
                Log.Info("[k10] preload: '" + mapKey + "': read use not known yet this session (gameplay: no card read on the main thread) -- every read prepared");
                return n;
            }
            u = Get("ruse", VUse, inputs, out _) ?? Array.Empty<byte>();   // (a menu-like scene: read now, as the map itself was)
            RuseSet(inputs, u);
        }
        if (u.Length != n) return n;
        int k = 0, skipped = 0;
        for (int i = 0; i < n; i++) { if (u[i] >= 3) { skipped++; continue; } off[k] = off[i]; len[k] = len[i]; k++; }
        if (skipped > 0) Log.Info("[k10] preload: '" + mapKey + "': " + skipped.ToString() + " of " + n.ToString() + " reads skipped (unused in the last 3 prefetches)");
        return k;
    }
    /// <summary>The prediction's bytes when its store was let go (read ahead / served the loader / unused).</summary>
    public static void Bytes(string predKey, long read, long served, long unused)
    {
        if (!Stats || predKey == null) return;
        var s = StatOf(predKey); s.Read += read; s.Served += served; s.Unused += unused; _statsDirty = true;
    }
    /// <summary>One map's verdicts when the store was let go, per read of the map file: 0 = not read ahead (no verdict), 1 = served the
    /// loader, 2 = read ahead and unused -> the streaks (kind "ruse"), merged and written by the cache worker in a window (the store is let go
    /// on the main thread, often in gameplay: no card access there).</summary>
    public static void Verdicts(string mapKey, byte[] map, byte[] verdict)
    {
        if (!Stats || map == null || verdict == null || verdict.Length == 0) return;
        K10Cache.Enqueue(new VerdictJob { Inputs = UseInputs(mapKey, map), What = mapKey, V = verdict, _jobGen = K10Cache.Generation, Write = Idle, Bytes = verdict.Length + 256 });
    }
    sealed class VerdictJob : K10Cache.Job
    {
        public string Inputs, What; public byte[] V; public int _jobGen;
        public override void Run()
        {
            if (K10Cache.Generation != _jobGen) return;
            var m = RuseMem(Inputs);
            var u = m != null && m.Length == V.Length ? (byte[])m.Clone() : m == null ? Get("ruse", VUse, Inputs, out _) : null;
            if (u == null || u.Length != V.Length) u = new byte[V.Length];
            bool changed = false;
            for (int i = 0; i < V.Length; i++)
            {
                if (V[i] == 0) continue;
                byte nv = V[i] == 2 ? (byte)Math.Min(255, u[i] + 1) : (byte)0;
                if (nv != u[i]) { u[i] = nv; changed = true; }
            }
            RuseSet(Inputs, u);
            if (changed) S.Put("ruse", Key("ruse", VUse, Inputs), VUse, u);
        }
    }

    // ------------------------------------------------------------------ per-scene high-water marks

    /// <summary>The reads the transition made last time (0 = unknown): the read log's arrays are sized to it before the load.</summary>
    public static int ExpectedReads(string mapKey)
    {
        if (!Hwm) return 0;
        var b = Get("hwm", VHwm, "map=" + mapKey, out _);
        if (b == null) return 0;
        foreach (var kv in Encoding.UTF8.GetString(b).Split(' '))
            if (kv.StartsWith("reads=", StringComparison.Ordinal) && int.TryParse(kv.Substring(6), out var n) && n > 0 && n <= 1 << 20) return n;
        return 0;
    }
    static void NoteHwm(string mapKey, int reads, long bytes)
    {
        // the HIGHEST seen (kind version 2): each mark is raised when a visit goes above it and never lowered -- the heap at a load's end moves
        // with GC timing, so "the last visit's" was rewritten nearly every start; rounded up (live objects to 5,000s, the heap to 4 MiB) so an
        // unchanged scene stays within its marks and is not written again
        var text = "reads=" + reads.ToString() + " bytes=" + bytes.ToString() + " live=" + ((_liveAtLoad + 4999) / 5000 * 5000).ToString() + " heapMiB=" + (((_heapAtLoad >> 20) + 3) / 4 * 4).ToString();
        Put("hwm", VHwm, "map=" + mapKey, Encoding.UTF8.GetBytes(text), true, "high-water marks of '" + mapKey + "' (" + text + ")", true);
    }
    static int _liveAtLoad; static long _heapAtLoad;

    // ------------------------------------------------------------------ the scene hooks (K8Preload, main thread) + the idle window

    static string _active; static int _loadEndAt = -1, _nextPoll, _visitEndAt = -1;
    static readonly string[] IdleScenes = { "level0", "BufferScene", "QuoteScreen", "Main Menu" };
    /// <summary>Test only (vlx): this scene counts as a gameplay scene (no window; a save there makes a manifest) -- the kit routes reach no
    /// gameplay scene on the launchers' card, so the manifest path is tested with the quotes as one.</summary>
    static readonly string TestPlayScene = Env("SHEPHERD_K10_TEST_PLAYSCENE");
    static bool IsIdleScene(string s)
    {
        if (s == null) return true;
        if (TestPlayScene != null && s == TestPlayScene) return false;
        for (int i = 0; i < IdleScenes.Length; i++) if (IdleScenes[i] == s) return true;
        return false;
    }
    public static void OnLoadStart(string scene)
    {
        if (!On) return;
        Outcome(scene);
        if (Continue)
        {
            if (_mUsed != null && _active != _mScene && scene != _mScene && scene != "BufferScene") _mUsed = null;   // (the head start was not taken)
            CheckManifest(); PollSaves();   // (the scene being left: its manifest checked, a save it made kept -- the game often saves right before it changes scene)
            // then this load's reads are kept for a gameplay scene (from its load's start: the load reads most of it); the menus / quotes /
            // BufferScene between keep the last one's (a save at a transition -- the active scene is then the BufferScene -- or on the way
            // back to the menu belongs to the scene played)
            if (!IsIdleScene(scene)) { VisitReset(scene); _visitOn = true; } else _visitOn = false;
            _visitEndAt = -1;
        }
        K10Cache.SetWindow(false, false);
    }
    public static void OnLoadDone(string scene)
    {
        if (!On) return;
        _active = scene; _loadEndAt = Time.FrameCount + 60;
        if (Hwm) { _liveAtLoad = Native.Objects.LiveCount; _heapAtLoad = GC.GetTotalMemory(false); }
        if (Continue && scene == "Main Menu") K10Cache.Enqueue(new SlotsJob());
        if (Continue && _mUsed != null && scene == _mScene) _mCheckAt = Time.FrameCount + 600;   // (the manifest checked at the scene's map window end, or when it is left)
        if (Continue && _visitOn && scene == _visitScene) _visitEndAt = Time.FrameCount + 600;   // (the visit's reads end with the map window: a manifest = the scene's load)
        SaveStats();
    }
    /// <summary>Every frame (K8Preload.Tick): the window; the save poll; statistics written when they changed.</summary>
    public static void Tick()
    {
        if (!On) return;
        int f = Time.FrameCount;
        bool loading = _loadEndAt >= 0 && f < _loadEndAt;
        bool open = !loading && (IsIdleScene(_active) || VitaToggles.Live);
        K10Cache.SetWindow(open || !Idle, _loadEndAt >= 0 && f == _loadEndAt);
        if (_mCheckAt >= 0 && f >= _mCheckAt) CheckManifest();
        if (_visitEndAt >= 0 && f >= _visitEndAt) { _visitEndAt = -1; _visitOn = false; }   // (the reads kept stay until the next gameplay scene's load)
        if (f >= _nextPoll)
        {   // the save poll: every 300 frames in a window (a few file stats -- reads only; the manifest itself waits for a window), and at every
            // scene load's start. Not in gameplay (review fix 2026-10-28): a save made there is seen at the next load's start or window -- where its
            // manifest is written anyway, with the same reads (the visit's reads end with its map window) -- so gameplay has no card access and no
            // worker. Test only: with SHEPHERD_K10_TEST_PLAYSCENE (the vlx manifest test, whose route ends inside its "gameplay" scene) the
            // gameplay poll runs on the cache worker every 600 frames, as c5 did.
            _nextPoll = f + (open ? 300 : 600);
            if (Continue) { if (open) PollSaves(); else if (TestPlayScene != null && !_pollQueued) { _pollQueued = true; K10Cache.Enqueue(new PollJob()); } }
            if (open) { SaveStats(); K10Cache.FlushSoon(); }
        }
    }

    // ------------------------------------------------------------------ Continue manifests

    // this visit's pack reads (main thread + the readers: under the lock), deduplicated by offset (open addressing on plain arrays)
    static readonly object _vk = new();
    static long[] _vOff = new long[1024]; static int[] _vLen = new int[1024]; static int _vN; static long _vBytes;
    static long[] _vKey = new long[4096];
    const int VisitMax = 16384; const long VisitBytes = 64L << 20;
    static string _visitScene; static volatile bool _visitOn;   // the gameplay scene whose reads are kept (set under _vk); reads are added while it is active
    static void VisitReset(string scene) { lock (_vk) { _vN = 0; _vBytes = 0; Array.Clear(_vKey); _visitScene = scene; } }
    /// <summary>Every on-demand pack read (K8Preload's read log forwards it; the reading thread).</summary>
    public static void OnRead(long off, int len)
    {
        if (!Continue || !_visitOn) return;
        lock (_vk)
        {
            if (_vN >= VisitMax || _vBytes >= VisitBytes) return;
            long k = off + 1; int m = _vKey.Length - 1;
            for (int i = (int)((uint)((int)k ^ (int)(k >> 32)) * 2654435761u) & m; ; i = (i + 1) & m)
            {
                if (_vKey[i] == k) return;
                if (_vKey[i] == 0) { _vKey[i] = k; break; }
            }
            if (_vN == _vOff.Length) { Array.Resize(ref _vOff, _vN * 2); Array.Resize(ref _vLen, _vN * 2); }
            _vOff[_vN] = off; _vLen[_vN] = len; _vN++; _vBytes += len;
            if (_vN * 2 > _vKey.Length)
            {   // grow the table (rehash)
                var old = _vKey; _vKey = new long[old.Length * 2]; int mm = _vKey.Length - 1;
                for (int j = 0; j < _vN; j++) { long kk = _vOff[j] + 1; for (int i = (int)((uint)((int)kk ^ (int)(kk >> 32)) * 2654435761u) & mm; ; i = (i + 1) & mm) if (_vKey[i] == 0) { _vKey[i] = kk; break; } }
            }
        }
    }

    /// <summary>The game's save folder; test only: <c>SHEPHERD_K10_TEST_SAVES=&lt;folder under the owner folder&gt;</c> watches another one (the
    /// game never reads it: a vlx test changes a file there while a run plays).</summary>
    static string SavesDir => Env("SHEPHERD_K10_TEST_SAVES") is { Length: > 0 } t ? Path.Combine(Program.Root, t) : Path.Combine(Impl.AppIcalls.Persistent, "saves");
    static string[] _seenName = new string[8]; static long[] _seenSize = new long[8], _seenTime = new long[8]; static int _seenN = -1;
    static readonly object _pk = new();   // the poll's state (the main thread at a load's start / in a window; the cache worker in the test's gameplay poll)
    static volatile bool _pollQueued;
    sealed class PollJob : K10Cache.Job { public override void Run() { _pollQueued = false; PollSaves(); } }   // (test only; reads only: runs at once)
    /// <summary>The slot files' name / size / time stamp against the last look (a few stats: in an idle window or at a load's start on the main
    /// thread; test only, in the test's gameplay scene on the cache worker); a changed slot = the game saved it: its manifest job is queued (hash + this visit's reads,
    /// written in the next window).</summary>
    static void PollSaves() { lock (_pk) PollLocked(); }
    static void PollLocked()
    {
        string[] files;
        try { if (!Directory.Exists(SavesDir)) return; files = Directory.GetFiles(SavesDir, "slot_*.mp"); }
        catch (Exception) { return; }
        bool first = _seenN < 0; if (first) _seenN = 0;
        foreach (var f in files)
        {
            long size, time;
            try { var fi = new FileInfo(f); size = fi.Length; time = fi.LastWriteTimeUtc.Ticks; } catch (Exception) { continue; }
            var name = Path.GetFileName(f);
            int i = 0; while (i < _seenN && _seenName[i] != name) i++;
            if (i < _seenN && _seenSize[i] == size && _seenTime[i] == time) continue;
            if (i == _seenN) { if (_seenN == _seenName.Length) { Array.Resize(ref _seenName, _seenN * 2); Array.Resize(ref _seenSize, _seenN * 2); Array.Resize(ref _seenTime, _seenN * 2); } _seenN++; }
            _seenName[i] = name; _seenSize[i] = size; _seenTime[i] = time;
            if (first) continue;   // (the files as found at the start: nothing to keep)
            long[] o; int[] l; int n; string scene;
            lock (_vk)
            {
                scene = _visitScene;   // (no gameplay scene yet: nothing to keep)
                n = scene == null ? 0 : _vN; o = new long[n]; l = new int[n]; Array.Copy(_vOff, o, n); Array.Copy(_vLen, l, n);
            }
            if (n == 0) continue;
            K10Cache.Enqueue(new ManifestJob { Path = f, Name = name, Size = size, Time = time, Scene = scene, Off = o, Len = l, N = n, Write = Idle, Bytes = n * 12L + 512 });
        }
    }

    static string _stamps; static int _stampGen = -1; static bool _stampsVerified;   // "name TAB size TAB ticks TAB hash" lines (kind "savestamp"); the cache worker's only
    static string StampsText()
    {
        if (_stampGen != K10Cache.Generation) { _stampGen = K10Cache.Generation; _stamps = null; }
        if (_stamps == null) { var b = Get("savestamp", VStamp, "stamps", out _stampsVerified); _stamps = b == null ? "" : Encoding.UTF8.GetString(b); }
        return _stamps;
    }
    static string StampHash(string name, long size, long time)
    {
        var pre = name + "\t" + size.ToString() + "\t" + time.ToString() + "\t";
        foreach (var line in StampsText().Split('\n')) if (line.StartsWith(pre, StringComparison.Ordinal)) return line.Substring(pre.Length);
        return null;
    }
    /// <summary>The slot's stamp set to <paramref name="hash"/>; returns the hash its stamp held before (null = none). <paramref name="now"/>:
    /// written at once (a window job); else in the next window (the SlotsJob runs at once, outside a window: reads only -- review fix 2026-10-28).</summary>
    static string SetStamp(string name, long size, long time, string hash, bool now)
    {
        var sb = new StringBuilder(); string prev = null;
        foreach (var line in StampsText().Split('\n'))
        {
            if (line.Length == 0) continue;
            if (!line.StartsWith(name + "\t", StringComparison.Ordinal)) { sb.Append(line).Append('\n'); continue; }
            int t = line.LastIndexOf('\t'); if (t > 0) prev = line.Substring(t + 1);
        }
        sb.Append(name).Append('\t').Append(size.ToString()).Append('\t').Append(time.ToString()).Append('\t').Append(hash).Append('\n');
        _stamps = sb.ToString();
        if (now) S.Put("savestamp", Key("savestamp", VStamp, "stamps"), VStamp, Encoding.UTF8.GetBytes(_stamps));
        else K10Cache.Enqueue(new StampJob { _jobGen = K10Cache.Generation, Write = Idle, Bytes = _stamps.Length + 256 });
        return prev;
    }
    sealed class StampJob : K10Cache.Job
    {   // (in a window) the stamps as they are now
        public int _jobGen;
        public override void Run() { if (K10Cache.Generation != _jobGen || _stampGen != K10Cache.Generation || _stamps == null) return; S.Put("savestamp", Key("savestamp", VStamp, "stamps"), VStamp, Encoding.UTF8.GetBytes(_stamps)); }
    }
    /// <summary>Another slot's stamp still names <paramref name="hash"/> (two slots with the same bytes share one manifest).</summary>
    static bool StampHolds(string hash, string exceptName)
    {
        foreach (var line in StampsText().Split('\n'))
            if (line.Length > 0 && !line.StartsWith(exceptName + "\t", StringComparison.Ordinal) && line.EndsWith("\t" + hash, StringComparison.Ordinal)) return true;
        return false;
    }
    /// <summary>The slot's previous save's manifest (its stamp changed): never matched again -- dropped (review fix 2026-10-28: replaced
    /// manifests were left for the LRU, so the cache and every start's reconcile grew with each save).</summary>
    static void DropPrevious(string name, string prev, string cur, bool now)
    {
        if (prev == null || prev == cur || StampHolds(prev, name)) return;
        var key = Key("continue", VCont, "save=" + prev);
        if (now) S.Drop("continue", key); else DropSoon("continue", key);
        Log.Info("[k10] Continue manifest of " + name + "'s previous save " + prev + " dropped");
    }
    /// <summary>The slot file's 64-bit content hash (read whole, never parsed); null if it changed while being read or cannot be read.</summary>
    static string HashFile(string path, long size)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length != size) return null;
            return IcallManifest.ContentHash(b).ToString("x16");
        }
        catch (Exception) { return null; }
    }

    sealed class ManifestJob : K10Cache.Job
    {
        public string Path, Name, Scene, Hash; public long Size, Time; public long[] Off; public int[] Len; public int N;
        public override void Run()
        {
            var h = Hash ?? HashFile(Path, Size);
            if (h == null) { Log.Info("[k10] Continue manifest for " + Name + ": the save changed while it was read -- next time"); return; }
            if (Write && !K10Cache.WriteOk) { Hash = h; K10Cache.Enqueue(this); return; }   // (the window closed while the save was hashed: written in the next one)
            var nb = Encoding.UTF8.GetBytes(Scene); var sb = Encoding.UTF8.GetBytes(Name);
            var b = new byte[12 + nb.Length + sb.Length + N * 12];
            BitConverter.TryWriteBytes(b.AsSpan(0), nb.Length); nb.CopyTo(b, 4);
            BitConverter.TryWriteBytes(b.AsSpan(4 + nb.Length), sb.Length); sb.CopyTo(b, 8 + nb.Length);
            int p = 8 + nb.Length + sb.Length;
            BitConverter.TryWriteBytes(b.AsSpan(p), N); p += 4;
            long bytes = 0;
            for (int i = 0; i < N; i++) { BitConverter.TryWriteBytes(b.AsSpan(p), Off[i]); BitConverter.TryWriteBytes(b.AsSpan(p + 8), Len[i]); p += 12; bytes += Len[i]; }
            var err = S.Put("continue", Key("continue", VCont, "save=" + h), VCont, b);
            var prev = SetStamp(Name, Size, Time, h, true);
            if (err == null) DropPrevious(Name, prev, h, true);
            Log.Info("[k10] Continue manifest for " + Name + " (save " + h + "): '" + Scene + "', " + N.ToString() + " reads, " + (bytes / 1048576.0).ToString("F1") + " MiB" + (err == null ? "" : " -- not written (" + err + ")"));
        }
    }

    // the Main Menu's slots: hash (stamp or read) -> manifest; the head start picks the newest save whose scene is the learned next one
    sealed class Slot { public string Name, Scene, Hash; public long Time; public long[] Off; public int[] Len; public int N; }
    static Slot[] _slots; static volatile bool _slotsReady;
    sealed class SlotsJob : K10Cache.Job
    {
        public override void Run()
        {
            _slotsReady = false;
            string[] files;
            try { files = Directory.Exists(SavesDir) ? Directory.GetFiles(SavesDir, "slot_*.mp") : new string[0]; } catch (Exception) { return; }
            var list = new Slot[files.Length]; int k = 0, stampOk = 0; bool stampBad = false;
            foreach (var f in files)
            {
                long size, time; try { var fi = new FileInfo(f); size = fi.Length; time = fi.LastWriteTimeUtc.Ticks; } catch (Exception) { continue; }
                var name = System.IO.Path.GetFileName(f);
                var h = StampHash(name, size, time);
                if (h != null && !_stampsVerified)
                {   // verified on first reuse: the stamp's hash against the file's real one (read whole, never parsed)
                    var real = HashFile(f, size);
                    if (real != null && real != h)
                    {
                        Log.Info("[k10] save stamp of " + name + " was wrong (" + h + " vs " + real + "): rewritten in the next window");
                        DropPrevious(name, SetStamp(name, size, time, real, false), real, false); h = real; stampBad = true;
                    }
                    else if (real != null) stampOk++;
                }
                h ??= HashFile(f, size);
                if (h == null) continue;
                var b = Get("continue", VCont, "save=" + h, out bool ver);
                var sl = b == null ? null : Parse(b);
                if (sl == null) continue;
                sl.Hash = h; sl.Time = time; list[k++] = sl;
                Log.Info("[k10] Continue manifest for " + name + " (save " + h + (ver ? ", verified" : ", unverified") + "): '" + sl.Scene + "', " + sl.N.ToString() + " reads");
            }
            Array.Resize(ref list, k);
            if (stampOk > 0 && !stampBad && !_stampsVerified) { S.Confirm("savestamp", Key("savestamp", VStamp, "stamps")); _stampsVerified = true; }
            _slots = list; _slotsReady = true;
        }
    }
    static Slot Parse(byte[] b)
    {
        try
        {
            int nl = BitConverter.ToInt32(b, 0); var scene = Encoding.UTF8.GetString(b, 4, nl);
            int sl = BitConverter.ToInt32(b, 4 + nl); var name = Encoding.UTF8.GetString(b, 8 + nl, sl);
            int p = 8 + nl + sl; int n = BitConverter.ToInt32(b, p); p += 4;
            if (n <= 0 || b.Length != p + n * 12) return null;
            var s = new Slot { Name = name, Scene = scene, N = n, Off = new long[n], Len = new int[n] };
            for (int i = 0; i < n; i++) { s.Off[i] = BitConverter.ToInt64(b, p); s.Len[i] = BitConverter.ToInt32(b, p + 8); p += 12; }
            return s;
        }
        catch (Exception) { return null; }
    }
    /// <summary>The head start's reads for <paramref name="scene"/> from the newest save whose manifest names it (null = none / not ready):
    /// K8Preload.Prefetch, from a menu-like scene (the Main Menu's Continue; the quotes / BufferScene on the way), uses them in place of the
    /// learned map of that scene's load.</summary>
    public static bool FromMenu(string active) => IsIdleScene(active);
    public static bool ManifestFor(string scene, out long[] off, out int[] len, out string slot)
    {
        off = null; len = null; slot = null;
        if (!Continue || !_slotsReady || _slots == null) return false;
        Slot best = null;
        foreach (var s in _slots) if (s.Scene == scene && (best == null || s.Time > best.Time)) best = s;
        if (best == null) return false;
        var r = PackAssets.Reader; long lo = r.PayloadOffset, hi = r.PayloadOffset + r.PayloadSize;
        for (int i = 0; i < best.N; i++) if (best.Len[i] <= 0 || best.Off[i] < lo || best.Off[i] + best.Len[i] > hi) return false;
        off = best.Off; len = best.Len; slot = best.Name + " (save " + best.Hash + ")";
        _mUsed = best; _mScene = scene;
        return true;
    }

    // verified on first reuse: a manifest the head start used is compared with what its scene really read (this visit's reads) when the scene
    // is left -- 90 % or more of its reads were read = confirmed, else it is dropped (stale: it only cost a slower load)
    static Slot _mUsed; static string _mScene; static int _mCheckAt = -1;
    static bool VisitHas(long off)
    {
        long k = off + 1; int m = _vKey.Length - 1;
        for (int i = (int)((uint)((int)k ^ (int)(k >> 32)) * 2654435761u) & m; ; i = (i + 1) & m) { if (_vKey[i] == k) return true; if (_vKey[i] == 0) return false; }
    }
    static void CheckManifest()
    {
        var u = _mUsed;
        if (u == null || _active != _mScene) return;
        _mUsed = null; _mCheckAt = -1;
        int hit = 0;
        lock (_vk) for (int i = 0; i < u.N; i++) if (VisitHas(u.Off[i])) hit++;
        var key = Key("continue", VCont, "save=" + u.Hash);
        bool ok = hit * 10 >= u.N * 9;
        if (ok) S.Confirm("continue", key); else DropSoon("continue", key);
        Log.Info("[k10] Continue manifest of " + u.Name + " (save " + u.Hash + "): " + hit.ToString() + " of " + u.N.ToString() + " reads were read by '" + _mScene + "' -- " + (ok ? "verified" : "dropped (stale)"));
    }

    public static string Summary()
    {
        if (!On) return "[k10] learning kinds off (SHEPHERD_K10_LEARN=0 / SHEPHERD_K10_CACHE=0: kit #9's preload)";
        var sb = new StringBuilder("[k10] learning: preload statistics");
        if (_stats != null) for (int i = 0; i < _statN; i++) { var s = _stats[i]; sb.Append(" | ").Append(s.Key).Append(' ').Append(s.Hit.ToString()).Append('/').Append(s.Pred.ToString()).Append(" served ").Append((s.Served / 1048576.0).ToString("F1")).Append(" MiB unused ").Append((s.Unused / 1048576.0).ToString("F1")); }
        else sb.Append(" none yet");
        return sb.ToString();
    }
}
#endif
