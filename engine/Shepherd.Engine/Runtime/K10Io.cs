using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Assets;
using Shepherd.Engine.Assets;
namespace Shepherd.Engine.Runtime;

/// <summary>
/// Device kit #10, track io (vita/notes/kit10-io.md): LOADING I/O. Every row keeps the bytes the engine gets exactly the pack's; only when
/// and where they are read, decoded and written changes.
/// <list type="bullet">
/// <item><b>The chunked pack</b> (Shepherd.Assets VitaPack2.cs, built by tools/VitaPack --repack): <c>pack/shepherd.pk2</c> beside the
///   version-1 <c>shepherd.pack</c> is read instead -- LZ4-compressed chunks (zstd too in a SHEPHERD_K10_ZSTD build), visit-order layout,
///   block-aligned, deduplicated, hashed -- through a fixed native page pool, with the B-4 decode-ahead worker and dependency-parallel
///   prefetch. The decoders: the host's (vita/shepherd/k10_io.inc.c: LZ4, and zstd from vitasdk's libzstd.a in a SHEPHERD_K10_ZSTD build)
///   under the Vita host, else managed LZ4 (Shepherd.Assets Lz4) + the Mac's installed libzstd.</item>
/// <item><b>I/O priority lanes</b> (Shepherd.Assets IoLanes): the reading thread's pack reads go first; the decode-ahead worker's and the
///   preload's reads wait for them (at most one 128 KiB card request in front of a foreground read).</item>
/// <item><b>Write discipline</b>: OUR results-only cache files whose writers never read them back or delete them in the same session --
///   Render/GxpCache.WriteAtomic writes under cache/gxp/ (the GXP shader cache), cache/icalls/ (the icall scan cache) and cache/readorder/
///   (the preload's read-order maps; a map read again in the same session before it is written is the previous one: only the preload's
///   timing can differ) -- are kept in RAM and written by a background writer in
///   an idle window -- review: a scene load, the game paused (timeScale 0: pause / settings menus, and the boot's scene change to the menu), the boot's
///   Main Menu; never mid-play (the first build's "pack idle" test fired during play; SHEPHERD_K10_WRITE_WINDOWS=pack keeps it) -- and at the
///   end of the session; each write is background I/O (behind the player's pack reads). The game's own saves and settings never pass through here (they are written by the game's code exactly when it
///   writes them); nor do the logs and the black box (their per-line / 5 s writes are kit #7 / #8's crash evidence; batching them is the
///   owner's pending LOG_DIET decision). Never deferred: cache/json/ (the catalog bake's token file is read back by the game in the same
///   session, and its bake can be deleted in the same session -- a later write would bring it back), anything outside cache/.</item>
/// <item><b>Verify files</b> (<see cref="VerifyFiles"/>, for the cache track's PS Vita Settings row): the pack's integrity hashes.</item>
/// <item><b>DMA</b> (SHEPHERD_K10_DMA=1, opt-in): copies into video memory of 64 KiB and more by sceDmacMemcpy (the preload's chunk copies into
///   its video-memory store); kit #7's device probe measured sceDmacMemcpy RAM->VRAM at 312 MiB/s vs memcpy's 324, so it is off by default
///   (vitaGL already uploads compressed textures with sceDmacMemcpy); <c>probe</c> times both on the device and logs them.</item>
/// </list>
/// Switches (env file): <c>SHEPHERD_K10_IO=0</c> = kit #9 (the version-1 pack, no lanes, writes as before); per row: SHEPHERD_K10_PACK=0
/// (the version-1 file), SHEPHERD_K10_AHEAD=0 (no decode-ahead worker), SHEPHERD_K10_PREFETCH=0, SHEPHERD_K10_PAGES=&lt;n&gt;,
/// SHEPHERD_K10_AHEAD_PAGES=&lt;n&gt;, SHEPHERD_K10_LANES=0, SHEPHERD_K10_WRITES=0, SHEPHERD_K10_DMA=1|probe, SHEPHERD_K10_READLOG=&lt;file&gt;
/// (test: every logical pack read, for --repack --order), SHEPHERD_K10_TIMING=1 (stats: the reading threads' time in the pack layer).
/// Review fixes (vita/notes/kit10-io.md "Review fixes"): SHEPHERD_K10_HASHCHECK=0|1|all (decoded-hash checks: off = the first build, 1 =
/// the background threads + chunks that failed once, all = every decode), SHEPHERD_K10_AHEAD_RUNS=0 (the worker follows forward moves only:
/// the first build), SHEPHERD_K10_MADEFROM_SAMPLES=&lt;n&gt; (sampled chunks for a pack baked before kit #10, default 4; 0 = header only),
/// SHEPHERD_K10_WRITE_WINDOWS=pack (the first build's write windows).
/// </summary>
[ProfileOnly]
public static unsafe class K10Io
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_K10_IO") != "0";
    static bool Sw(string name, bool def) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() is "1" or "on" or "yes" or "true" : def;
    static int Int(string name, int def, int lo, int hi) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v >= lo && v <= hi ? v : def;
    public static bool Writes { get; private set; }
    static bool _refused;
    static string _dmaMode = "0";

    /// <summary>PackAssets.Init, before the pack is opened.</summary>
    public static void ConfigurePack()
    {
        if (!On) { IoLanes.On = false; return; }
        // (stats only, opt-in: the reading threads' whole time in the pack layer, both pack versions -- two clock reads per record read, ~100k
        // per boot, so never by default)
        PackReader.LayerTiming = Sw("SHEPHERD_K10_TIMING", false);
        PackReader.PreferV2 = Sw("SHEPHERD_K10_PACK", true);
        PackReader.ChunkDecoder = Diag.VitaHost.On ? new VitaDecoder() : MacDecoders();
        // (no zstd in this build -- or SHEPHERD_K10_NOZSTD=1, test: the fallback to the version-1 file on the Mac)
        if ((Diag.VitaHost.On && (Host(0, null, 0, null, 0, 0) & 2) == 0) || Environment.GetEnvironmentVariable("SHEPHERD_K10_NOZSTD") == "1") PackReader.V2CodecMask &= ~(1 << VitaPack2Format.CodecZstd);
        PackReader.V2DecodeAhead = Sw("SHEPHERD_K10_AHEAD", true);
        PackReader.V2Prefetch = Sw("SHEPHERD_K10_PREFETCH", true);
        PackReader.V2Pages = Int("SHEPHERD_K10_PAGES", 16, 4, 64);
        PackReader.V2AheadPages = Int("SHEPHERD_K10_AHEAD_PAGES", Math.Max(1, PackReader.V2Pages - 5), 1, PackReader.V2Pages - 3);
        PackReader.OnDecodeAheadThread = Diag.VitaHost.PinWorker;
        // review fixes (vita/notes/kit10-io.md "Review fixes"): decoded-hash checks (0 none = the first build, 1 the background threads +
        // chunks that failed once, 2 / all every decode), the worker following the reader into a new run behind it, the sampled made-from check
        // of a pack baked before kit #10 (0 = header only)
        var hc = Environment.GetEnvironmentVariable("SHEPHERD_K10_HASHCHECK");
        PackReader.V2HashCheck = hc is { Length: > 0 } ? (hc.Trim() is "all" or "2" ? 2 : hc.Trim() is "0" or "off" ? 0 : 1) : 1;
        PackReader.V2AheadRuns = Sw("SHEPHERD_K10_AHEAD_RUNS", true);
        PackReader.V2MadeFromSamples = Int("SHEPHERD_K10_MADEFROM_SAMPLES", 4, 0, 64);
        IoLanes.On = Sw("SHEPHERD_K10_LANES", true);
        Writes = Sw("SHEPHERD_K10_WRITES", true);
        // review: the write discipline's windows -- "game" (default): a scene load, the game paused (pause / settings menus, and CotL's own
        // boot's level0 -> Main Menu change runs at timeScale 0), the boot's Main Menu; never mid-play. "pack" = the first build's rule (the
        // pack idle 2 s and paused, or the pending set 20 s old / 256 KiB, or 120 s old -- which fired during play, when the pack is idle)
        _packRule = Environment.GetEnvironmentVariable("SHEPHERD_K10_WRITE_WINDOWS") is { } ww && ww.Trim() == "pack";
        if (Writes && !_packRule) lock (_wl) StartWriter();   // (from the start: it must see the Main Menu arrive -- see AtBootMenu)
        _dmaMode = Environment.GetEnvironmentVariable("SHEPHERD_K10_DMA") is { Length: > 0 } dm ? dm.Trim() : "0";
        if (Diag.VitaHost.On && (_dmaMode == "1" || _dmaMode == "probe" || _dmaMode == "check") && (Host(0, null, 0, null, 0, 0) & 4) != 0) PackReader.Copier = new DmaCopier();
        if (Environment.GetEnvironmentVariable("SHEPHERD_K10_READLOG") is { Length: > 0 } rl)
            try { PackReader.VisitLog = new ReadLog(rl); } catch (Exception e) { Log.Warn("[k10] read log not opened: " + e.GetBaseException().Message); }
    }

    /// <summary>PackAssets.Init: the pack -- a chunked one this build cannot read (a damaged header, a codec it lacks) is reported and the
    /// version-1 file beside it opened instead (as kit #9; never a crash for a card holding the wrong shepherd.pk2).</summary>
    public static PackReader OpenPack(string dir)
    {
        try { return PackReader.Open(dir); }
        catch (Exception e) when (On && PackReader.PreferV2 && Directory.Exists(dir) && File.Exists(System.IO.Path.Combine(dir, VitaPackFormat.PackFileName)))
        {
            Log.Warn("[k10] " + System.IO.Path.Combine(dir, VitaPack2Format.FileName) + " not used: " + e.GetBaseException().Message + " -- the version-1 pack is read instead");
            PackReader.PreferV2 = false; _refused = true;
            return PackReader.Open(dir);
        }
    }

    /// <summary>The line after the pack opened (PackAssets.Init).</summary>
    public static string Describe(PackReader r)
    {
        if (!On) return "[k10] io: OFF (SHEPHERD_K10_IO=0: kit #9's pack reading and writes)";
        return "[k10] io: " + (r.IsChunked ? "chunked pack " + System.IO.Path.GetFileName(r.Path) + " (" + r.ChunkCount.ToString() + " chunks, " + (r.StoredFileBytes / 1048576.0).ToString("F1") + " MiB on the card for "
                + (r.PayloadSize / 1048576.0).ToString("F1") + " MiB of payload; decoders " + PackReader.ChunkDecoder?.Describe() + "; pages " + PackReader.V2Pages.ToString() + ", decode-ahead "
                + (PackReader.V2DecodeAhead ? PackReader.V2AheadPages.ToString() + " pages" + (PackReader.V2AheadRuns ? " (follows new runs)" : " (forward only)") : "off") + ", prefetch " + (PackReader.V2Prefetch ? "on" : "off")
                + ", decoded-hash checks " + (PackReader.V2HashCheck == 0 ? "off" : PackReader.V2HashCheck == 1 ? "background" : "all") + (r.MadeFrom != null ? "; " + r.MadeFrom : "") + ")"
            : "version-1 pack (" + (_refused ? "shepherd.pk2 refused: the WRN line above" : PackReader.PreferV2 ? "no shepherd.pk2 beside it" : "SHEPHERD_K10_PACK=0") + ")")
            + "; lanes " + (IoLanes.On ? "on" : "off") + "; write discipline " + (Writes ? (_packRule ? "on (the first build's pack-idle windows)" : "on (cache/ writes at scene loads, pauses, the Main Menu)") : "off") + "; DMA " + (PackReader.Copier != null ? _dmaMode : "off");
    }

    // ------------------------------------------------------------------ decoders

    [DllImport("__Internal")] static extern int shepherd_vita_k10(int op, byte* a, int an, byte* b, int bn, int ctx);
    [MethodImpl(MethodImplOptions.NoInlining)] static int Host(int op, byte* a, int an, byte* b, int bn, int ctx) => shepherd_vita_k10(op, a, an, b, bn, ctx);

    /// <summary>The Vita host's decoders (k10_io.inc.c): op 1 LZ4, op 2 zstd (a SHEPHERD_K10_ZSTD build; else -2), one zstd context per ctx.</summary>
    sealed class VitaDecoder : IPackDecoder
    {
        readonly int _caps = Host(0, null, 0, null, 0, 0);
        public int Decode(byte codec, byte* src, int srcLen, byte* dst, int dstLen, int ctx)
            => codec == VitaPack2Format.CodecLz4 ? Host(1, dst, dstLen, src, srcLen, ctx) : codec == VitaPack2Format.CodecZstd ? Host(2, dst, dstLen, src, srcLen, ctx) : -1;
        public string Describe() => "host LZ4" + ((_caps & 2) != 0 ? " + zstd" : " (no zstd in this build)");
    }

    static IPackDecoder MacDecoders()
    {
#if !SHEPHERD_EBOOT
        return new MacDecoder();
#else
        return null;
#endif
    }
#if !SHEPHERD_EBOOT
    /// <summary>The Mac: the managed LZ4 decoder (Shepherd.Assets) and the installed libzstd (Homebrew), loaded by path.</summary>
    sealed class MacDecoder : IPackDecoder
    {
        static readonly delegate* unmanaged<byte*, nuint, byte*, nuint, nuint> s_zd = Load("ZSTD_decompress");
        static readonly delegate* unmanaged<nuint, uint> s_ze = (delegate* unmanaged<nuint, uint>)LoadPtr("ZSTD_isError");
        static delegate* unmanaged<byte*, nuint, byte*, nuint, nuint> Load(string name) => (delegate* unmanaged<byte*, nuint, byte*, nuint, nuint>)LoadPtr(name);
        static IntPtr LoadPtr(string name)
        {
            foreach (var p in new[] { "/opt/homebrew/lib/libzstd.1.dylib", "/opt/homebrew/lib/libzstd.dylib", "/usr/local/lib/libzstd.dylib" })
                if (NativeLibrary.TryLoad(p, out var h) && NativeLibrary.TryGetExport(h, name, out var f)) return f;
            return IntPtr.Zero;
        }
        public int Decode(byte codec, byte* src, int srcLen, byte* dst, int dstLen, int ctx)
        {
            if (codec == VitaPack2Format.CodecLz4)
                try { return Shepherd.Assets.Compression.Lz4.Decode(new ReadOnlySpan<byte>(src, srcLen), new Span<byte>(dst, dstLen)); } catch (InvalidDataException) { return -1; }
            if (codec == VitaPack2Format.CodecZstd && s_zd != null) { nuint r = s_zd(dst, (nuint)dstLen, src, (nuint)srcLen); return s_ze(r) != 0 ? -1 : (int)r; }
            return -1;
        }
        public string Describe() => "managed LZ4" + (s_zd != null ? " + libzstd" : " (no libzstd found)");
    }
#endif

    // ------------------------------------------------------------------ DMA (opt-in)

    sealed class DmaCopier : PackReader.ICopier
    {
        public long Dma, Cpu, Mismatch; public double DmaMs, CpuMs;
        public void Copy(byte* dst, byte* src, int n, bool dstVram)
        {
            if (!dstVram) { Buffer.MemoryCopy(src, dst, n, n); return; }   // (only copies into video memory: no cache to invalidate there)
            if (_dmaMode == "probe" && ((Dma + Cpu) & 1) == 1)
            {   // probe: every other copy by the CPU, both timed (the device A/B)
                long t = System.Diagnostics.Stopwatch.GetTimestamp(); Buffer.MemoryCopy(src, dst, n, n); CpuMs += Ms(t); Cpu++; return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Host(3, dst, n, src, n, 0) != 1) { Buffer.MemoryCopy(src, dst, n, n); Cpu++; CpuMs += Ms(t0); return; }
            DmaMs += Ms(t0); Dma++;
            // (review: probe compares its DMA copies too, after the timing -- the owner's one device A/B then also proves the bytes; vlx's
            // sceDmacMemcpy is a plain copy, so its check can never fail and says nothing about the device's DMA or cache coherency)
            if ((_dmaMode == "check" || _dmaMode == "probe") && new ReadOnlySpan<byte>(dst, n).SequenceCompareTo(new ReadOnlySpan<byte>(src, n)) != 0) { Mismatch++; Buffer.MemoryCopy(src, dst, n, n); }
        }
        static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public override string ToString() => "DMA " + _dmaMode + ": " + Dma.ToString() + " copies by sceDmacMemcpy (" + DmaMs.ToString("F1") + " ms), " + Cpu.ToString() + " by the CPU (" + CpuMs.ToString("F1") + " ms)" + (_dmaMode == "check" || _dmaMode == "probe" ? ", " + Mismatch.ToString() + " mismatches" : "");
    }

    // ------------------------------------------------------------------ the read log (test: SHEPHERD_K10_READLOG)

    sealed class ReadLog : IPackVisitLog
    {
        readonly StreamWriter _w; public long N;
        public ReadLog(string path) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))); _w = new StreamWriter(path, false) { AutoFlush = false }; _w.WriteLine("# kit #10 io: logical pack reads (offset, length) in order -- tools/VitaPack --repack --order"); }
        public void OnVisit(long offset, int length) { lock (_w) { _w.Write(offset); _w.Write('\t'); _w.Write(length); _w.Write('\n'); N++; } }
        public void Close() { lock (_w) _w.Flush(); }
    }

    // ------------------------------------------------------------------ write discipline

    static readonly object _wl = new();
    static string[] _wPath = new string[16]; static byte[][] _wData = new byte[16][]; static int _wN; static long _wBytes, _wFirstTicks;
    static Thread _writer; static bool _wStop, _packRule;
    public static long Deferred, DeferredBytes, Flushed, FlushedBytes, FlushRuns, Failures; static string _lastError;
    static string _cacheDir;
    /// <summary>The cache/ subfolders whose files are deferred (their writers never read them back or delete them in the same session).</summary>
    static readonly string[] Deferrable = { "gxp", "icalls", "readorder" };

    /// <summary>Render/GxpCache.WriteAtomic: a results-only cache file under the card's cache/ folder is taken (kept in RAM, written in an idle
    /// window; a later write of the same file replaces the pending bytes) -> true, the caller is done (null = no error yet; a failure is
    /// logged when it is written). Anything else (another folder, the switch off) -> false: written now, as before.</summary>
    public static bool Defer(string path, byte[] bytes)
    {
        if (!Writes || path == null || bytes == null) return false;
        _cacheDir ??= System.IO.Path.GetFullPath(System.IO.Path.Combine(Program.Root, "cache")) + System.IO.Path.DirectorySeparatorChar;
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch (Exception) { return false; }
        if (!full.StartsWith(_cacheDir, StringComparison.Ordinal)) return false;
        bool ok = false;
        foreach (var d in Deferrable) if (string.CompareOrdinal(full, _cacheDir.Length, d, 0, d.Length) == 0 && full.Length > _cacheDir.Length + d.Length && (full[_cacheDir.Length + d.Length] == '/' || full[_cacheDir.Length + d.Length] == '\\')) ok = true;
        if (!ok) return false;
        lock (_wl)
        {
            if (_wStop) return false;
            int i = 0; while (i < _wN && _wPath[i] != full) i++;
            if (i == _wN)
            {
                if (_wN == _wPath.Length) { Array.Resize(ref _wPath, _wN * 2); Array.Resize(ref _wData, _wN * 2); }
                _wPath[_wN] = full; _wN++;
            }
            else _wBytes -= _wData[i].Length;
            _wData[i] = bytes; _wBytes += bytes.Length; Deferred++; DeferredBytes += bytes.Length;
            if (_wFirstTicks == 0) _wFirstTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            StartWriter();
            Monitor.PulseAll(_wl);
        }
        return true;
    }

    /// <summary>The background writer (under _wl). Review: 64 KiB of stack (vlx's stack watch: its deepest use 9 KiB).</summary>
    static void StartWriter()
    {
        if (_writer != null) return;
        _writer = new Thread(WriterLoop, 64 * 1024) { IsBackground = true, Name = "shepherd-writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    /// <summary>A pending write's bytes (what the file will hold once written), or null: for a reader of the same file in the same session.</summary>
    public static byte[] Pending(string path)
    {
        if (!Writes || path == null) return null;
        string full; try { full = System.IO.Path.GetFullPath(path); } catch (Exception) { return null; }
        lock (_wl) for (int i = 0; i < _wN; i++) if (_wPath[i] == full) return _wData[i];
        return null;
    }

    static void WriterLoop()
    {
        try { Diag.VitaHost.PinWorker("shepherd-writer"); } catch { }
        for (;;)
        {
            int n;
            lock (_wl)
            {
                if (_wStop) return;
                Monitor.Wait(_wl, _wN > 0 && !_packRule ? 100 : 250);   // (pending: look often -- a scene load on the Mac can be short)
                n = _wN;
            }
            if (!_packRule) AtBootMenu();   // (the Main Menu's scene is noted when it arrives, pending writes or not)
            if (n == 0) continue;
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
            double idle = (now - Volatile.Read(ref IoLanes.LastFgTicks)) / (double)f;
            double age = (now - Volatile.Read(ref _wFirstTicks)) / (double)f;
            string why = _packRule ? PackRuleWindow(idle, age) : Window(idle, age >= 120.0 || Volatile.Read(ref _wBytes) >= (256 << 10));
            if (why != null) Flush(why, gate: true);
        }
    }

    /// <summary>Review (the default): the write discipline's idle windows, the roadmap's -- loading screens and menus, never mid-play:
    /// a scene being built (Assets/Loader.LoadingScene), the game paused (timeScale 0: the pause and settings menus; CotL also runs its
    /// boot's level0 -> Main Menu change at timeScale 0 -- the vlx logs; not New Game's), the boot's Main Menu; each at the first gap of 0.25 s in the player's pack reads
    /// (the Main Menu: 2 s). The size cap (256 KiB pending) or a 120 s old write only drops those gaps to 0 / 0.25 s -- still inside a window
    /// (the first build's caps wrote during play). Writes made during play wait for the next window, or the end of the session (results-only
    /// files: a write lost to a power-off makes the next start slower, never different). Writer thread: reads only plain fields.</summary>
    static string Window(double idle, bool capped)
    {
        double gap = capped ? 0 : 0.25;
        if (Assets.Loader.LoadingScene != 0 && idle >= gap) return "window: a scene load";
        if (Time.TimeScale == 0f && idle >= gap) return "window: the game paused (a menu or a scene transition)";
        if (AtBootMenu() && idle >= (capped ? 0.25 : 2.0)) return "window: the Main Menu";
        return null;
    }
    /// <summary>The first build's rule (SHEPHERD_K10_WRITE_WINDOWS=pack, test / A-B): the pack idle 2 s and the game paused, or the pending set 20 s
    /// old or 256 KiB big, or 120 s old at the next 0.25 s gap.</summary>
    static string PackRuleWindow(double idle, double age)
    {
        bool paused = Time.TimeScale == 0f;
        if ((idle >= 2.0 && (paused || age >= 20.0 || _wBytes >= (256 << 10))) || (age >= 120.0 && idle >= 0.25))
            return paused ? "idle window (paused)" : age >= 120.0 ? "cap: 120 s" : _wBytes >= (256 << 10) ? "cap: 256 KiB" : "idle window (20 s)";
        return null;
    }

    static int _menuHandle = int.MinValue; static bool _menuLooked;
    /// <summary>True while the scene active when the boot reached the Main Menu (Diag/K7's BOOT line: the "Main Menu" scene present) is still
    /// the active scene -- the player has not started or continued a game yet. Its handle is taken by the writer when it first sees the BOOT
    /// line's time, and only within 1.5 s of it (a later look could take a scene the player already went on to: then no menu window, writes
    /// wait for a load or a pause). Off when K7's boot numbers are off (SHEPHERD_K7_BOOT=0).</summary>
    static bool AtBootMenu()
    {
        if (_menuHandle == int.MinValue)
        {
            if (_menuLooked) return false;
            double t = Diag.K7.TotalMs; if (t < 0) return false;
            _menuLooked = true;
            int h = Scenes.ActiveHandle;
            if (Diag.K7.Ms(System.Diagnostics.Stopwatch.GetTimestamp()) - t > 1500) { Log.Info("[k10] write discipline: the Main Menu was seen late -- no menu window this session"); return false; }
            _menuHandle = h;
        }
        return Scenes.ActiveHandle == _menuHandle;
    }

    /// <summary>Every pending write now (the writer's idle window, or the end of the session -- PackAssets.Report). Review: the writer's writes
    /// are background I/O (each after IoLanes.BgGate: a pack read the player waits on goes first).</summary>
    public static void Flush(string why) => Flush(why, gate: false);
    static void Flush(string why, bool gate)
    {
        string[] p; byte[][] d; int n;
        lock (_wl)
        {
            n = _wN; if (n == 0) return;
            p = new string[n]; d = new byte[n][];
            Array.Copy(_wPath, p, n); Array.Copy(_wData, d, n);
            for (int i = 0; i < n; i++) { _wPath[i] = null; _wData[i] = null; }
            _wN = 0; _wBytes = 0; _wFirstTicks = 0; FlushRuns++;
        }
        long bytes = 0; int bad = 0;
        for (int i = 0; i < n; i++)
        {
            if (gate) IoLanes.BgGate();
            var err = Render.GxpCache.WriteNow(p[i], d[i]);
            if (err != null) { bad++; Failures++; _lastError = System.IO.Path.GetFileName(p[i]) + ": " + err; Log.Warn("[k10] cache write " + p[i] + " failed: " + err); }
            else { Flushed++; FlushedBytes += d[i].Length; bytes += d[i].Length; }
        }
        Log.Info("[k10] write discipline: " + n.ToString() + " cache file(s), " + (bytes / 1024.0).ToString("F1") + " KiB written (" + why + ")" + (bad > 0 ? ", " + bad.ToString() + " failed" : ""));
    }

    /// <summary>The end of the session (PackAssets.Report): pending writes now, the read log closed, the lines.</summary>
    public static void End()
    {
        if (!On) return;
        lock (_wl) _wStop = true;
        Flush("end of the session");
        (PackReader.VisitLog as ReadLog)?.Close();
        Log.Info("[k10] io end: write discipline " + (Writes ? Deferred.ToString() + " deferred cache writes (" + (DeferredBytes / 1024.0).ToString("F1") + " KiB) -> " + Flushed.ToString() + " written in " + FlushRuns.ToString() + " run(s), " + Failures.ToString() + " failed" + (_lastError != null ? " (last: " + _lastError + ")" : "") : "off")
            + "; lanes " + (IoLanes.On ? IoLanes.FgReads.ToString() + " foreground reads, " + IoLanes.BgWaits.ToString() + " background reads held back " + IoLanes.BgWaitedMs.ToString("F0") + " ms" : "off")
            + (PackReader.Copier != null ? "; " + PackReader.Copier.ToString() : "") + (PackReader.VisitLog is ReadLog rl ? "; read log " + rl.N.ToString() + " reads" : ""));
    }

    // ------------------------------------------------------------------ Verify files

    /// <summary>Verify files (the cache track's PS Vita Settings row calls this, off the main thread): the pack in use -- a chunked pack's
    /// header, chunk table, TOC and every chunk's stored-bytes hash (deep: decoded and the decoded hash too); a version-1 pack's TOC and its
    /// shepherd.pack.xxh blocks when present. Reads only. The result's Summary() is the line to show; Ok false names the first bad chunk.</summary>
    public static PackVerify.Result VerifyFiles(IVerifyProgress progress, bool deep = false)
    {
        var path = PackAssets.Reader?.Path;
        if (path == null) return new PackVerify.Result { Kind = "no pack open", Error = "no pack" };
        // review: one run at a time -- two at once (the Settings row pressed twice, a first-boot run plus a manual one) would share decoder
        // context 3 (one zstd state in a SHEPHERD_K10_ZSTD build: a data race), double the card reads and the native buffers
        if (!Monitor.TryEnter(_verifyLock)) return new PackVerify.Result { Path = path, Kind = "verify files", Error = "a Verify files run is already in progress" };
        try
        {
            long ilz = K13Ilz ? K13IlzBytes() : 0;   // (kit #13 bootc review fix: the compressed code counted into the window's progress from the start)
            var wrap = ilz > 0 && progress != null ? new K13Progress(progress, ilz) : null;
            var r = PackVerify.Run(path, PackReader.ChunkDecoder, deep, (IVerifyProgress)wrap ?? progress);
            if (K13Ilz && !r.Stopped) K13VerifyIlz(r, progress, wrap?.PackTotal ?? 0, ilz);
            Log.Info("[k10] verify files: " + path + ": " + r.Summary());
            return r;
        }
        finally { Monitor.Exit(_verifyLock); }
    }
    static readonly object _verifyLock = new();

    /// <summary>Device kit #13, track bootc (vita/notes/kit13-bootc.md section 8): Verify Files also reads every compressed code file in full -- the
    /// VPK's own DLLs in compressed-IL form (app0:managed-ilz/: kit #12's VPKILZ, read by default) and the card's (managed-ilz/: kit #10) -- each
    /// against its header and its original (host.c k13_bootc.inc.c op 1: the check the load does once and then remembers, done again, ignoring
    /// what it remembered). The result line names them ("+ N compressed code files"); a bad one makes the result a problem (its pieces counted with
    /// the pack's, the first bad one named). GL apps on the Vita host only (the Mac has no managed-ilz/). SHEPHERD_K13_VERIFY_ILZ=0 (or
    /// SHEPHERD_K13_BOOTC=0) = kit #12's Verify Files (the pack only).</summary>
    static readonly bool K13Ilz = Render.ShepGl.On && Environment.GetEnvironmentVariable("SHEPHERD_K13_VERIFY_ILZ") != "0" && Environment.GetEnvironmentVariable("SHEPHERD_K13_BOOTC") != "0";
    /// <summary>Review fix: the compressed code part reports progress and can be stopped. The first build read every form in one host call after
    /// the pack's progress had reached its end -- the window sat at 100 % for the larger part of the run (VLX: 98 of 137 MiB) and the window's
    /// stop waited for it. Now the forms' bytes (host op 2: sizes only) are counted into the total from the start, and they are checked a few
    /// files per host call with the window's progress moved (and its stop honoured) between calls. Same files, same checks, same result line.</summary>
    const int K13IlzBatch = 4;
    sealed class K13Progress : IVerifyProgress
    {
        readonly IVerifyProgress _in; readonly long _extra; public long PackTotal;
        public K13Progress(IVerifyProgress inner, long extra) { _in = inner; _extra = extra; }
        public bool Step(long done, long total) { PackTotal = total; return _in.Step(done, total + _extra); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe long K13IlzBytes()
    {
        try
        {
            int* io = stackalloc int[4]; io[0] = 3; io[1] = io[2] = io[3] = 0;
            return shepherd_vita_k13_bootc(2, io, null, 0) == 0 && io[0] > 0 ? io[2] * 1024L : 0;
        }
        catch (Exception) { return 0; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe void K13VerifyIlz(PackVerify.Result r, IVerifyProgress progress, long packTotal, long ilzTotal)
    {
        try
        {
            int* io = stackalloc int[4];
            byte* buf = stackalloc byte[256];
            int files = 0, bad = 0, calls = 0, steps = 0; long kib = 0, ms = 0, lastDone = 0; string firstBad = null; bool stopped = false;
            int stopTest = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K13_VERIFY_STOPTEST"), out var st) ? st : 0;   // TEST ONLY: the window closes after this many host calls
            while (true)
            {
                io[0] = 3; io[1] = files; io[2] = K13IlzBatch; io[3] = 0; buf[0] = 0;
                if (shepherd_vita_k13_bootc(1, io, buf, 256) != 0) break;   // (no compressed-IL runtime: the pack's result as it is)
                int got = io[0];
                if (got <= 0) break;
                calls++; files += got; bad += io[1]; kib += io[2]; ms += io[3];
                if (io[1] > 0 && firstBad == null) firstBad = Marshal.PtrToStringAnsi((IntPtr)buf);
                if (stopTest > 0 && calls == stopTest) K10Cache.Verify.StopWanted = true;   // (TEST ONLY: as the window's close sets it)
                if (progress == null) continue;
                lastDone = packTotal + Math.Min(kib * 1024L, ilzTotal); steps++;
                if (!progress.Step(lastDone, packTotal + ilzTotal)) { stopped = true; break; }
            }
            if (files <= 0) return;   // (no such files: the pack's result as it is)
            Log.Info("[k13] verify files: " + files.ToString() + " compressed code file(s) in " + calls.ToString() + " host call(s) of up to 4; the window's progress moved "
                + steps.ToString() + " time(s) during them, to " + (lastDone / 1024).ToString() + " of " + ((packTotal + ilzTotal) / 1024).ToString() + " KiB (the pack's "
                + (packTotal / 1024).ToString() + " + the compressed code's " + (ilzTotal / 1024).ToString() + ")" + (stopped ? " -- STOPPED (the window closed)" : ""));
            r.Kind += $" + {files} compressed code file(s) (managed-ilz/: {(bad == 0 ? "all ok" : bad + " BAD")}{(stopped ? ", stopped" : "")})";
            r.StoredBytes += kib * 1024L; r.Ms += ms;
            if (bad > 0)
            {
                r.Ok = false; r.Bad += bad; r.Chunks += files;
                r.FirstBad ??= firstBad;
            }
            if (stopped) { r.Stopped = true; r.Ok = false; }
        }
        catch (Exception e) { Log.Warn("[k13] verify files: the compressed code files were not checked: " + e.GetBaseException().Message); }
    }
    [DllImport("__Internal")] static extern unsafe int shepherd_vita_k13_bootc(int op, int* io, byte* buf, int cap);
}
