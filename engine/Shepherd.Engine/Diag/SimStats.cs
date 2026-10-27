using System.Diagnostics;
using System.Reflection;
using System.Text;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Diag;

/// <summary>V6 "vita-sim" measurements (vita/notes/v6-mac-sim.md). SHEPHERD_SIMSTATS="&lt;from&gt;:&lt;to&gt;[:&lt;heapFrame&gt;]", e.g. "1301:1400:1300".
/// Deliberately light and native-free beyond what the run already binds (Stopwatch, GC counters, DateTime, one file write at the
/// end): no Process, no MemReport probes, no reflection-heavy census, so the run's native census and assembly list stay the
/// Vita's. Records every frame (wall ms, render part, managed bytes allocated on the main thread, GCs started in it), then:
/// - window from..to: frame time mean/p50/p95/p99/max, allocation per frame, GC frames and the pause estimate
///   (mean of frames with a GC minus mean of frames without; Mono has no pause counter — the exact per-GC pauses come from
///   MONO_LOG_MASK=gc in the census run, vita/v6/sim.sh; kit #6: on the Vita the host sums them from Mono's GC events, GcPause below)
/// - after heapFrame: a forced full GC (timed: a major collection of the menu heap) and the live heap after it
/// - startup: Main entry (UTC, so the runner can add the pre-Main runtime start), asset layer up, frame 1, the Main Menu
///   scene, and the total
/// - assemblies loaded at exit (name, file bytes, location) -> runtime/logs/assemblies.txt
/// Output: [sim] lines in engine.log, runtime/logs/simstats.txt, runtime/logs/simstats-frames.tsv. No-op when unset.</summary>
[ProfileOnly]
public static class SimStats
{
    static readonly MenuClock.At FromAt, ToAt, HeapAtAt = new(-1, false);   // N or m+N (MenuClock: relative to the Main Menu scene's arrival)
    static int From => FromAt.Frame; static int To => ToAt.Frame; static int HeapAt => HeapAtAt.Frame;
    public static bool On { get; }
    /// <summary>SHEPHERD_POOL_PROBE=&lt;frame&gt;[,&lt;frame&gt;...] (with SHEPHERD_SIMSTATS): the big byte arrays ArrayPool&lt;byte&gt;.Shared holds at
    /// those frames (VitaMemory.PooledLargeBytes, counted, not dropped; stage-2 review, engine-pass-speed.md §8).</summary>
    static readonly HashSet<int> PoolProbe = new((Environment.GetEnvironmentVariable("SHEPHERD_POOL_PROBE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var f) ? f : -1));
    static long _main0; static DateTime _mainUtc;
    static long _f0, _a0; static int _gMinor0, _gMajor0;
    static readonly List<(int f, double ms, double render, long alloc, int minor, int major)> Rows = new(2048);
    static readonly List<string> Out = new();
    static double _frame1Ms = -1, _menuSceneMs = -1, _assetsMs = -1; static int _menuSceneFrame = -1;
    static (int minor, int major) _winStart, _winEnd;
    /// <summary>Kit #6 integration (the roadmap's "GC pause timer"): each frame's own GC pause -- the stop-the-world time of every collection
    /// that ran during the frame, summed from Mono's GC events by the Vita host (host.c shepherd_vita_gc_pause_us; elsewhere
    /// GC.GetTotalPauseDuration) -- in simstats-frames.tsv (gc_pause_ms), the window lines and the startup line, so a long frame splits into the
    /// GC's pause and the rest (kit #5's device run could not tell a ~2 s Settings frame's collection from its loading).
    /// <c>SHEPHERD_K6_GC_PAUSE=0</c> = kit #5 (no host call, no column, no line).</summary>
    public static readonly bool GcPause = Environment.GetEnvironmentVariable("SHEPHERD_K6_GC_PAUSE") != "0";
    static uint _pause0; static float[] _gcMs = new float[2048];
    /// <summary>Kit #7 (Diag/K7.cs, Render/K7Overlay.cs): the last frame's GC pause and the run's sum, ms (0 without SHEPHERD_K6_GC_PAUSE).</summary>
    public static float LastGcMs; public static double GcMsSum;
    static int _pauseSrc = -1;   // 1 the Vita host's sum, 2 GC.GetTotalPauseDuration (Mac), 0 none (a runtime without it: zeros)
    static uint PauseUs()
    {
        if (_pauseSrc < 0) { if (VitaHost.On) _pauseSrc = 1; else try { GC.GetTotalPauseDuration(); _pauseSrc = 2; } catch (Exception) { _pauseSrc = 0; } }
        return _pauseSrc == 1 ? shepherd_vita_gc_pause_us() : _pauseSrc == 2 ? (uint)(GC.GetTotalPauseDuration().Ticks / 10) : 0u;
    }
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern uint shepherd_vita_gc_pause_us();

    static SimStats()
    {
        var v = Environment.GetEnvironmentVariable("SHEPHERD_SIMSTATS");
        if (string.IsNullOrEmpty(v)) return;
        var p = v.Split(':');
        if (p.Length >= 2 && MenuClock.Parse(p[0], out var a) && MenuClock.Parse(p[1], out var b) && (a.Rel || a.N > 0) && a.Rel == b.Rel && b.N >= a.N) { FromAt = a; ToAt = b; On = true; }
        if (p.Length >= 3 && MenuClock.Parse(p[2], out var h)) HeapAtAt = h;
    }

    /// <summary>SHEPHERD_SCENE_PEAK=1 (diagnostic, with SHEPHERD_SIMSTATS): a forced full GC + the live heap at the steps of every
    /// scene instantiation (objects created / populated / previous scene unloaded), to see the boot's transient peak. Distorts
    /// GC timing: never in timing or cap runs.</summary>
    public static readonly bool ScenePeak = Environment.GetEnvironmentVariable("SHEPHERD_SCENE_PEAK") == "1";
    public static void ScenePoint(string scene, int step)
    {
        if (!ScenePeak) return;
        long before = GC.GetTotalMemory(false); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        W($"scene '{scene}' {(step == 0 ? "objects created (populate pending)" : step == 1 ? "populated" : "previous scenes unloaded")}: live {MiB(GC.GetTotalMemory(false))} (in use before the GC {MiB(before)}), {Native.Objects.LiveCount} engine records");
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double SinceMain => Ms(Stopwatch.GetTimestamp() - _main0);
    static int Minor => GC.CollectionCount(0);
    static int Major => GC.CollectionCount(GC.MaxGeneration);   // Mono: gen0 counts minors, the max generation counts majors

    /// <summary>First thing in Program.Main.</summary>
    public static void MainEntered() { if (!On) return; _main0 = Stopwatch.GetTimestamp(); _mainUtc = DateTime.UtcNow; }
    public static void AssetsUp() { if (On) _assetsMs = SinceMain; }

    /// <summary>kit4 intro-plan: SHEPHERD_FIXED_TIMING=1 (with SHEPHERD_SIMSTATS, frame clock) times every fixed step (FixedUpdate loop +
    /// trigger callbacks + WaitForFixedUpdate coroutines) inside the window and prints its mean cost per step: the play clock runs ~17 of
    /// them per frame once a frame takes longer than maximumDeltaTime, so the per-step cost decides the slow-frame spiral. Timing only.</summary>
    public static readonly bool FixedTiming = Environment.GetEnvironmentVariable("SHEPHERD_FIXED_TIMING") == "1";
    static int _cur; static long _fxN, _fxTicks;
    public static void NoteFixedStep(long ticks) { if (On && _cur >= From && _cur <= To) { _fxN++; _fxTicks += ticks; } }

    public static void Before(int frame)
    {
        if (!On) return;
        _cur = frame;
        if (frame == From) { _winStart = (Minor, Major); ProfMarks.WindowStart(); Render.MtRender.WindowStart(); }
        _f0 = Stopwatch.GetTimestamp(); _a0 = GC.GetAllocatedBytesForCurrentThread(); _gMinor0 = Minor; _gMajor0 = Major;
        if (GcPause) _pause0 = PauseUs();
    }

    public static void After(int frame)
    {
        if (!On) return;
        long t1 = Stopwatch.GetTimestamp(), a1 = GC.GetAllocatedBytesForCurrentThread();
        float gcMs = GcPause ? (uint)(PauseUs() - _pause0) / 1000f : 0f;
        LastGcMs = gcMs; GcMsSum += gcMs;   // (kit #7: the black box's and the overlay's GC share)
        if (frame == To) ProfMarks.WindowEnd();
        if (frame >= From && frame <= To) Render.MtRender.WindowFrame();
        if (frame == To) Render.MtRender.WindowEnd();   // menu-speed multicore stage: [mt] lines (render thread busy / main thread waits; SHEPHERD_MT_CPU=1 per-thread CPU)
        Rows.Add((frame, Ms(t1 - _f0), Ms(Render.SceneRenderer.LastFrameTicks), a1 - _a0, Minor - _gMinor0, Major - _gMajor0));
        if (GcPause)
        {   // (a plain array beside Rows: no new value-tuple instantiation in the eboot)
            int k = Rows.Count - 1;
            if (k >= _gcMs.Length) { var a = new float[_gcMs.Length * 2]; Array.Copy(_gcMs, a, _gcMs.Length); _gcMs = a; }
            _gcMs[k] = gcMs;
        }
        if (frame == 1) _frame1Ms = SinceMain;
        if (_menuSceneFrame < 0 && Scenes.All.Values.Any(s => s.Name == "Main Menu")) { _menuSceneFrame = frame; _menuSceneMs = SinceMain; }
        if (frame == To) _winEnd = (Minor, Major);
        if (PoolProbe.Contains(frame)) { var (n, b) = VitaMemory.PooledLargeBytes(drop: false); W($"pool probe @ frame {frame}: ArrayPool<byte>.Shared holds {n} byte array(s) of >= 1 MiB ({MiB(b)}); live-or-garbage GC.GetTotalMemory {MiB(GC.GetTotalMemory(false))}"); }
        if (frame == HeapAt) Heap(frame);
        if (frame == To) Window();
    }

    static void W(string s) { Out.Add(s); Log.Info("[sim] " + s); }
    static string MiB(double b) => $"{b / 1048576.0:F2} MiB";
    /// <summary>System.Runtime.JitInfo reads EventPipe runtime counters on Mono, and the Vita runtime is built without EventPipe
    /// (DISABLE_EVENTPIPE): there every JitInfo call throws NotImplementedException (EventPipeInternal.GetRuntimeCounterValue),
    /// which killed the vlx menu run at the heap frame (devfail1, 2026-10-07). "n/a" there; the real count everywhere else.</summary>
    internal static string JitMethodCount()
    {
        try { return System.Runtime.JitInfo.GetCompiledMethodCount(false).ToString(); }
        catch (NotImplementedException) { return "n/a (no EventPipe in this runtime)"; }
    }
    static double P(double[] sorted, double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Round(q * (sorted.Length - 1)))];
    static string Dist(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray(); if (a.Length == 0) return "n/a";
        return $"mean {a.Average():F2} p50 {P(a, 0.5):F2} p95 {P(a, 0.95):F2} p99 {P(a, 0.99):F2} max {a[^1]:F2} ms (n={a.Length})";
    }

    static void Heap(int frame)
    {
        long before = GC.GetTotalMemory(false); int mi0 = Minor, ma0 = Major;
        var sw = Stopwatch.StartNew(); GC.Collect(); sw.Stop();
        double first = sw.Elapsed.TotalMilliseconds;
        GC.WaitForPendingFinalizers();
        sw.Restart(); GC.Collect(); sw.Stop();
        long after = GC.GetTotalMemory(false);
        var gi = GC.GetGCMemoryInfo();
        W($"heap @ frame {frame} (menu idle): before {MiB(before)}; forced full GC #1 {first:F1} ms, #2 {sw.Elapsed.TotalMilliseconds:F1} ms (majors +{Major - ma0}, minors +{Minor - mi0}); " +
          $"LIVE after = {MiB(after)} (GC.GetTotalMemory); GCMemoryInfo heap {MiB(gi.HeapSizeBytes)} committed {MiB(gi.TotalCommittedBytes)} fragmented {MiB(gi.FragmentedBytes)}; " +
          $"live objects (engine records) {Native.Objects.LiveCount}; JIT-compiled methods {JitMethodCount()} (interpreter: runtime-invoke wrappers; on the Vita they come from AOT)");
        // SHEPHERD_HEAPWALK=<marker>: arm the Mac-only heap-census profiler module (vita/v6/tools/heap-walk, sim.sh heapwalk) for one
        // more full collection, after the measurement above: it walks the live heap at that GC (classes, big objects, root paths).
        var walk = Environment.GetEnvironmentVariable("SHEPHERD_HEAPWALK");
        if (!string.IsNullOrEmpty(walk))
        {
            File.WriteAllText(walk, frame.ToString()); GC.Collect(); File.Delete(walk);
            W($"heap walk: one more full GC with {walk} present (the census is the profiler module's)");
        }
        var (log, lags) = Native.Registry.LogState();
        W($"registry bind log at the heap frame: {log} entries, {Native.Registry.LogDestroyed()} of them destroyed records; lists' lag {string.Join(", ", lags.Select(l => $"{l.kind} {l.lag}"))}");
        // SHEPHERD_SIM_HOLD_MS=<ms>: after the forced GC, write runtime/logs/sim-hold and sleep, so vita/v6/sim.sh can take a
        // vmmap/footprint snapshot of the process at the idle menu (the memory run only; never in timing runs).
        if (int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_SIM_HOLD_MS"), out var hold) && hold > 0)
        {
            var marker = Path.Combine(Program.LogDir, "sim-hold");
            File.WriteAllText(marker, frame.ToString()); W($"holding {hold} ms for a memory snapshot ({marker})");
            // the runner ends the hold early by creating <marker>.done (sim.sh hold: after its snapshot)
            var done = marker + ".done"; var hsw = Stopwatch.StartNew();
            while (hsw.ElapsedMilliseconds < hold && !File.Exists(done)) Thread.Sleep(100);
            File.Delete(marker); File.Delete(done);
        }
    }

    static void Window()
    {
        var win = Rows.Where(r => r.f >= From && r.f <= To).ToList();
        if (win.Count == 0) { W("window: no frames"); return; }
        W($"window frames {From}..{To}{(FromAt.Rel ? $" ({FromAt}..{ToAt}; the Main Menu scene arrived at frame {MenuClock.MenuFrame})" : "")} (n={win.Count}), {Render.SceneRenderer.G?.Name} {Render.SceneRenderer.G?.W}x{Render.SceneRenderer.G?.H}, runtime {Runtime()}");
        W($"  frame wall   {Dist(win.Select(r => r.ms))}");
        W($"  render part  {Dist(win.Select(r => r.render))} (SceneRenderer.Frame: layout + uGUI rebuild + vertex build + backend)");
        W($"  logic part   {Dist(win.Select(r => r.ms - r.render))} (frame minus render)");
        var gcf = win.Where(r => r.minor + r.major > 0).ToList(); var nogc = win.Where(r => r.minor + r.major == 0).ToList();
        double est = gcf.Count > 0 && nogc.Count > 0 ? gcf.Average(r => r.ms) - nogc.Average(r => r.ms) : double.NaN;
        W($"  alloc/frame  mean {win.Average(r => r.alloc) / 1024.0:F1} KiB, p95 {P(win.Select(r => (double)r.alloc).OrderBy(x => x).ToArray(), 0.95) / 1024.0:F1} KiB, max {win.Max(r => r.alloc) / 1024.0:F1} KiB (main thread)");
        W($"  GCs          {_winEnd.minor - _winStart.minor} minor + {_winEnd.major - _winStart.major} major in {win.Count} frames; {gcf.Count} frames with a GC; " +
          $"pause estimate {est:F2} ms per GC frame (mean with GC {(gcf.Count > 0 ? gcf.Average(r => r.ms) : double.NaN):F2} vs without {(nogc.Count > 0 ? nogc.Average(r => r.ms) : double.NaN):F2} ms); GC index range minor {_winStart.minor}..{_winEnd.minor} major {_winStart.major}..{_winEnd.major}");
        if (GcPause)
        {
            double tot = 0, mx = 0; int k = 0;
            for (int i = 0; i < Rows.Count; i++) { var r = Rows[i]; if (r.f < From || r.f > To) continue; double g = _gcMs[i]; tot += g; if (g > 0) k++; if (g > mx) mx = g; }
            W("  GC pauses    " + tot.ToString("F1") + " ms in " + win.Count + " frames (each collection's stop-the-world time, Mono's GC events): " + k +
              " frames held one, " + (k > 0 ? (tot / k).ToString("F1") : "0") + " ms each on average, max " + mx.ToString("F1") + " ms");
        }
        if (FixedTiming) W($"  fixed steps  {_fxN} in {win.Count} frames ({_fxN / (double)win.Count:F2} per frame), {Ms(_fxTicks):F2} ms in all, {(_fxN > 0 ? Ms(_fxTicks) / _fxN : 0):F3} ms per step (FixedUpdate + trigger callbacks + WaitForFixedUpdate)");
    }

    static double PauseSum() { double t = 0; for (int i = 0; i < Rows.Count; i++) t += _gcMs[i]; return t; }

    static string Runtime() => MonoCompat.MonoInterpCompat.IsMono
        ? $"Mono {(MonoCompat.MonoInterpCompat.Interpreted ? "interpreter" : "JIT")} (MONO_ENV_OPTIONS='{Environment.GetEnvironmentVariable("MONO_ENV_OPTIONS")}' MONO_THREADS_SUSPEND='{Environment.GetEnvironmentVariable("MONO_THREADS_SUSPEND")}' MONO_GC_PARAMS='{Environment.GetEnvironmentVariable("MONO_GC_PARAMS")}')"
        : $"CoreCLR {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";

    /// <summary>End of the run (Boot): startup summary, the whole-boot table, the assembly list.</summary>
    public static void End()
    {
        if (!On) return;
        try
        {
            W($"startup: Main entered {_mainUtc:yyyy-MM-ddTHH:mm:ss.fff}Z (unix ms {new DateTimeOffset(_mainUtc).ToUnixTimeMilliseconds()}); asset layer up +{_assetsMs:F0} ms; " +
              $"frame 1 done +{_frame1Ms:F0} ms; Main Menu scene present at frame {_menuSceneFrame} (+{_menuSceneMs:F0} ms); run total +{SinceMain:F0} ms over {Rows.Count} frames; " +
              $"all frames {Dist(Rows.Select(r => r.ms))}; allocated over the run {Rows.Sum(r => r.alloc) / 1048576.0:F1} MiB (main thread, frames only); GCs {Minor} minor + {Major} major" + (GcPause ? "; GC pauses in the frames " + PauseSum().ToString("F0") + " ms (stop-the-world)" : ""));
            if (MenuProbe.On) W($"menu: last probe selected='{MenuProbe.LastSelected}' label='{MenuProbe.LastLabel}'");
            var dir = Program.LogDir;
            var sb = new StringBuilder(GcPause ? "frame\tms\trender_ms\talloc_bytes\tminor_gcs\tmajor_gcs\tgc_pause_ms\n" : "frame\tms\trender_ms\talloc_bytes\tminor_gcs\tmajor_gcs\n");
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                sb.Append(r.f).Append('\t').Append(r.ms.ToString("F3")).Append('\t').Append(r.render.ToString("F3")).Append('\t').Append(r.alloc).Append('\t').Append(r.minor).Append('\t').Append(r.major);
                if (GcPause) sb.Append('\t').Append(_gcMs[i].ToString("F3"));   // (kit #6: the frame's GC pause)
                sb.Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "simstats-frames.tsv"), sb.ToString());
            var asm = new StringBuilder("# assemblies loaded at exit: name\tversion\tfile_bytes\tlocation\n");
            long total = 0; int n = 0;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                var loc = a.Location; long len = 0; try { if (!string.IsNullOrEmpty(loc)) len = new FileInfo(loc).Length; } catch { }
                total += len; n++;
                var an = a.GetName(); asm.Append(an.Name).Append('\t').Append(an.Version).Append('\t').Append(len).Append('\t').Append(loc).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "assemblies.txt"), asm.ToString());
            W($"assemblies loaded: {n}, {MiB(total)} of DLL files (IL + metadata; per-assembly IL bytes: vita/v6/tools/AsmSize) -> runtime/logs/assemblies.txt");
            File.WriteAllLines(Path.Combine(dir, "simstats.txt"), Out);
        }
        catch (Exception e) { Log.Exception(e, "SimStats.End"); }
    }
}
