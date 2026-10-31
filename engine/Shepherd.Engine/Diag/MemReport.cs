#if !SHEPHERD_EBOOT   // kit #7 measure: a Mac planning tool, left out of the eboots (Diag/MemReport.Eboot.cs; SHEPHERD_ENGINE_TOOLS=1 keeps it)
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Shepherd.Assets;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Diag;

/// <summary>Opt-in budget instrumentation (PS Vita planning). SHEPHERD_MEMREPORT=&lt;frame&gt;[:&lt;window&gt;]
///   after boot frame N completes: memory census (GC heap, RSS, textures, meshes, objects, bundles, assemblies, JIT);
///   frames N+1..N+window: per-phase frame timing taken from *outside* the engine (execution-order probe behaviours,
///   a SynchronizationContext post, an Invoke, a wrapper around the public Lifecycle.Render delegate and Canvas
///   pre/willRenderCanvases handlers) so no engine code path changes; then Stylizer / object-scan micro-benchmarks.
/// Unset = every entry point returns immediately.</summary>
public static unsafe partial class MemReport
{
    static readonly int At = -1, Window = 100;
    static readonly bool Drop;   // SHEPHERD_MEMREPORT=N:W:drop -> destructive heap attribution at the very end (run must end at N+W)
    static readonly bool CensusOn;   // SHEPHERD_MEMREPORT=N:W:census -> object-graph heap census (MemReport.Census.cs) at the end of the window
    static MemReport()
    {
        var v = Environment.GetEnvironmentVariable("SHEPHERD_MEMREPORT");
        if (string.IsNullOrEmpty(v)) return;
        var p = v.Split(':');
        if (int.TryParse(p[0], out var a)) At = a;
        if (p.Length > 1 && int.TryParse(p[1], out var w) && w > 0) Window = w;
        Drop = p.Skip(2).Contains("drop"); CensusOn = p.Skip(2).Contains("census");
    }

    // ---- frame marks (Stopwatch ticks); index = end of the phase it closes ----
    internal const int MBegin = 0, MUpdFirst = 1, MUpdLast = 2, MSync = 3, MInvoke = 4, MLateFirst = 5, MLateLast = 6, MRenderStart = 7, MPreWill = 8, MWillEnd = 9, MRenderEnd = 10, MEnd = 11, NMarks = 12;
    static readonly string[] PhaseNames =
    {
        "", "pre: Time.BeginFrame + Start() + FixedUpdate()", "Update() (all MonoBehaviours)", "coroutines resume (NextFrame/Seconds/Async)",
        "SyncContext tasks + Invokes.Tick", "VideoIcalls.Tick + AnimatorRuntime.UpdateAll", "LateUpdate() (all MonoBehaviours)", "deferred Destroy + AsyncOps.Tick",
        "Render: LayoutRootCanvases + NotifyRectChanges", "Render: uGUI canvas rebuild (willRenderCanvases)", "Render: DrawWorld + StylizerPass.Apply + DrawOverlayCanvases",
        "EndOfFrame coroutines + bookkeeping",
    };
    internal static readonly long[] Mark = new long[NMarks];
    internal static bool InWindow, InRender;
    static readonly double[] PhaseSum = new double[NMarks];
    static readonly int[] PhaseMissing = new int[NMarks];
    static readonly List<double> FrameMs = new();
    static readonly List<double> RenderMs = new();
    static readonly List<bool> GcFrame = new();   // did a collection start inside this frame (any generation)?
    // CPU time per frame (macOS clock_gettime_nsec_np): main thread, and the whole process (all threads incl. Parallel.For
    // workers and GC). Much less sensitive than wall time when other jobs load the machine.
    static readonly List<double> ThreadCpuMs = new(), ProcCpuMs = new();
    static ulong _tcpu0, _pcpu0;
    static readonly bool CpuClocks = OperatingSystem.IsMacOS();
    [DllImport("libc")] static extern ulong clock_gettime_nsec_np(int clockId);   // CLOCK_PROCESS_CPUTIME_ID=12, CLOCK_THREAD_CPUTIME_ID=16
    // Not inlined: the Mono interpreter binds every P/Invoke of a method when it transforms it, and Before/After run (and return
    // at once) every frame even with SHEPHERD_MEMREPORT unset -> keep this Mac-only libc import out of their bodies (V6 census).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static ulong CpuNs(int clockId) => clock_gettime_nsec_np(clockId);
    static int _gcBefore;
    static int GcCount() => GC.CollectionCount(0) + GC.CollectionCount(GC.MaxGeneration);   // Mono: gen0 = minors only, majors counted separately
    static SynchronizationContext _ctx;
    // Runtime flavor (V2: Shepherd on Mono). Mono's CoreLib throws PlatformNotSupportedException for a non-default LOH
    // compaction mode and for GC.GetGCMemoryInfo(GCKind), and GC.GetTotalPauseDuration() is always 0 there -> those go through
    // these helpers; under CoreCLR they are exactly the previous calls.
    internal static readonly bool IsMono = MonoCompat.MonoInterpCompat.IsMono;
    static void CompactLohOnce() { if (!IsMono) System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce; }
    static GCMemoryInfo FullGcInfo() => IsMono ? GC.GetGCMemoryInfo() : GC.GetGCMemoryInfo(GCKind.FullBlocking);
    static string RuntimeFlavor() => IsMono
        ? $"Mono ({RuntimeInformation.FrameworkDescription}; dynamic code compiled={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled} [false = this code is interpreted]; MONO_ENV_OPTIONS='{Environment.GetEnvironmentVariable("MONO_ENV_OPTIONS")}' MONO_THREADS_SUSPEND='{Environment.GetEnvironmentVariable("MONO_THREADS_SUSPEND") ?? "(default: hybrid)"}' MONO_GC_PARAMS='{Environment.GetEnvironmentVariable("MONO_GC_PARAMS")}')"
        : $"CoreCLR ({RuntimeInformation.FrameworkDescription})";
    static long _alloc0, _allocMain0, _gcPause0Ticks; static int _gc00, _gc10, _gc20; static long[] _hits0; static Dictionary<string, int> _types0;
    static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    static string _outPath;
    static readonly StringBuilder Out = new();

    internal static void Hit(int k) { if (InWindow) Mark[k] = Stopwatch.GetTimestamp(); }
    internal static void PostSync() { if (InWindow && _ctx != null) _ctx.Post(_ => Hit(MSync), null); }

    public static void Before(int frame)
    {
        if (At < 0) return;
        InWindow = frame > At && frame <= At + Window;
        if (!InWindow) return;
        Array.Clear(Mark); _gcBefore = GcCount(); if (CpuClocks) { _pcpu0 = CpuNs(12); _tcpu0 = CpuNs(16); } Mark[MBegin] = Stopwatch.GetTimestamp();
    }

    public static void After(int frame)
    {
        if (At < 0) return;
        if (InWindow)
        {
            Mark[MEnd] = Stopwatch.GetTimestamp();
            if (CpuClocks) { ThreadCpuMs.Add((CpuNs(16) - _tcpu0) / 1e6); ProcCpuMs.Add((CpuNs(12) - _pcpu0) / 1e6); }
            long prev = Mark[MBegin];
            for (int k = 1; k < NMarks; k++)
            {
                if (Mark[k] == 0) { PhaseMissing[k]++; continue; }   // its time is attributed to the next present mark
                PhaseSum[k] += (Mark[k] - prev) * TickMs; prev = Mark[k];
            }
            FrameMs.Add((Mark[MEnd] - Mark[MBegin]) * TickMs); GcFrame.Add(GcCount() != _gcBefore);
            if (Mark[MRenderStart] != 0 && Mark[MRenderEnd] != 0) RenderMs.Add((Mark[MRenderEnd] - Mark[MRenderStart]) * TickMs);
            InWindow = false;
        }
        try
        {
            if (frame == At) { Census(frame); InstallProbes(); WindowStart(); Flush(); }
            // kit4 intro-plan: each bench in its own catch (a stale bench -- RenderSplitBench's reflection call into DrawWorld, whose
            // signature changed -- used to abort the window before the heap census ran); reported as a WRN line, not an EXC
            if (frame == At + Window) { WindowReport(frame); Bench1(Bench); Bench1(RenderSplitBench); Bench1(UpdateSplitBench); Bench1(StylizerBench); Flush(); if (CensusOn) { HeapCensus(frame); Flush(); } if (Drop) { DropAttribution(); Flush(); if (CensusOn) { W("(census of what the destructive drop left behind:)"); HeapCensus(frame, light: true); Flush(); } } }
        }
        catch (Exception e) { Log.Exception(e, "MemReport"); }
    }

    static void W(string s) { Out.AppendLine(s); Log.Info("[mem] " + s); }
    static void Flush()
    {
        _outPath ??= Path.Combine(Program.LogDir, $"memreport-{At}.txt");
        File.WriteAllText(_outPath, Out.ToString());
    }
    static string MB(double b) => $"{b / (1024.0 * 1024.0):F2} MiB";

    // =====================================================================================================
    static void Census(int frame)
    {
        W($"===== Shepherd MemReport @ frame {frame} (t={UnityEngine.Time.time:F2}s) =====");
        W($"runtime: {RuntimeFlavor()}");
        W($"host: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} cores={Environment.ProcessorCount} .NET {Environment.Version} gc={(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} concurrent={AppContext.TryGetSwitch("System.GC.Concurrent", out var conc) && conc}");

        // ---- process / GC (before any forced collection) ----
        var gi = GC.GetGCMemoryInfo();
        var proc = Process.GetCurrentProcess();
        W($"GC.GetTotalMemory(false)      = {MB(GC.GetTotalMemory(false))}");
        W($"GCMemoryInfo.HeapSizeBytes    = {MB(gi.HeapSizeBytes)} (fragmented {MB(gi.FragmentedBytes)}, committed {MB(gi.TotalCommittedBytes)}, promoted {MB(gi.PromotedBytes)})");
        for (int g = 0; g < gi.GenerationInfo.Length; g++) W($"   gen{(g < 3 ? g.ToString() : g == 3 ? "LOH" : "POH")} sizeAfter={MB(gi.GenerationInfo[g].SizeAfterBytes)} fragAfter={MB(gi.GenerationInfo[g].FragmentationAfterBytes)}");
        W($"Environment.WorkingSet (RSS)  = {MB(Environment.WorkingSet)}  peak={MB(proc.PeakWorkingSet64)} private={MB(proc.PrivateMemorySize64)} threads={proc.Threads.Count}");
        W($"GC collections so far gen0/1/2 = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}; total allocated so far = {MB(GC.GetTotalAllocatedBytes(false))}; total GC pause = {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms");
        // JitInfo needs EventPipe on Mono; the Vita runtime has none and throws NotImplementedException (see SimStats.JitMethodCount)
        try { W($"JIT: methods compiled={System.Runtime.JitInfo.GetCompiledMethodCount(false)} IL bytes={System.Runtime.JitInfo.GetCompiledILBytes(false):N0} jit time={System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds:F0} ms"); }
        catch (NotImplementedException) { W("JIT: n/a (System.Runtime.JitInfo needs EventPipe, which this runtime is built without)"); }

        // ---- forced full compacting GC -> live managed heap ----
        CompactLohOnce();
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
        var gl = FullGcInfo();
        W($"after forced full compacting GC: GetTotalMemory={MB(GC.GetTotalMemory(false))} HeapSizeBytes={MB(gl.HeapSizeBytes)} (fragmented {MB(gl.FragmentedBytes)} => live {MB(gl.HeapSizeBytes - gl.FragmentedBytes)}) committed={MB(gl.TotalCommittedBytes)} RSS={MB(Environment.WorkingSet)}");
        for (int g = 0; g < gl.GenerationInfo.Length; g++) W($"   gen{(g < 3 ? g.ToString() : g == 3 ? "LOH" : "POH")} sizeAfter={MB(gl.GenerationInfo[g].SizeAfterBytes)}");

        var all = Objects.All.Where(n => !n.Destroyed).ToList();
        W($"live NObj records: {all.Count} (Objects.LiveCount={Objects.LiveCount})");
#if !SHEPHERD_NO_PHYS
        W(Shepherd.Engine.Physics.PhysWorld.MemLine());   // kit #9 (physics-plan-v2 Stage 7 item 7): the physics core's memory and step times
#endif

        // ---- textures ----
        var texs = all.OfType<NTexture>().ToList();
        long rgba = 0, inlineRetained = 0, srcEncAll = 0, srcEncDecoded = 0, fullIfAll = 0; int decoded = 0, rts = 0;
        var fmtHist = new Dictionary<string, (int n, long px, long enc, long rgba)>();
        long EncSize(NTexture t)
        {
            if (t.Data is not TypeTreeObject d) return t.Src?.EncodedSize ?? 0;   // V3 diet: trees are not kept; the texture's source record has the size
            if (d.TryGet("m_CompleteImageSize", out var cis) && cis != null) { var v = TypeTreeObject.ToLong(cis); if (v > 0) return v; }
            if (d.TryGet("m_StreamData", out var sd) && sd is TypeTreeObject s && s.TryGet("size", out var sz)) return TypeTreeObject.ToLong(sz);
            return 0;
        }
        foreach (var t in texs)
        {
            long b = t.Rgba?.LongLength ?? 0; rgba += b; if (b > 0) decoded++; if (t.IsRenderTexture) rts++;
            fullIfAll += (long)Math.Max(0, t.Width) * Math.Max(0, t.Height) * 4;
            if (t.Data is TypeTreeObject d && d.TryGet("image data", out var id) && id is byte[] ib) inlineRetained += ib.LongLength;
            else if (t.Src?.Inline is { } sib) inlineRetained += sib.LongLength;
            long enc = EncSize(t); srcEncAll += enc; if (b > 0) srcEncDecoded += enc;
            var key = t.IsRenderTexture ? "RenderTexture" : t.HasSource ? ((UnityEngine.TextureFormat)t.Format).ToString() : "runtime(" + ((UnityEngine.TextureFormat)t.Format) + ")";
            var h = fmtHist.GetValueOrDefault(key); fmtHist[key] = (h.n + 1, h.px + (long)t.Width * t.Height, h.enc + enc, h.rgba + b);
        }
        W($"--- textures: {texs.Count} NTexture records, {decoded} decoded to RGBA8, {rts} render textures");
        W($"   decoded RGBA8 bytes (NTexture.Rgba)        = {MB(rgba)}");
        W($"   RGBA8 if every record were decoded         = {MB(fullIfAll)}");
        W($"   source-encoded size (m_CompleteImageSize): all={MB(srcEncAll)} decoded-only={MB(srcEncDecoded)}");
        W($"   inline 'image data' bytes retained (NTexture.Data tree or NTexture.Src) = {MB(inlineRetained)}");
        W($"   {Assets.Diet.Describe()}");
        W($"   {Assets.Diet.Counters()}");
        foreach (var kv in fmtHist.OrderByDescending(k => k.Value.rgba))
            W($"   format {kv.Key,-22} n={kv.Value.n,5} px={kv.Value.px,12:N0} encoded={MB(kv.Value.enc),12} rgba8-decoded={MB(kv.Value.rgba)}");
        W("   top 30 textures by decoded bytes:");
        foreach (var t in texs.Where(t => t.Rgba != null).OrderByDescending(t => t.Rgba.LongLength).Take(30))
            W($"     {MB(t.Rgba.LongLength),11}  {t.Width,5}x{t.Height,-5} {(UnityEngine.TextureFormat)t.Format,-10} enc={MB(EncSize(t)),10}  '{t.Name}'");

        // GPU-side copies held by the SDL renderer (SceneRenderer.TexCache): what has actually been drawn
        try
        {
            var f = typeof(Render.SceneRenderer).GetField("TexCache", BindingFlags.Static | BindingFlags.NonPublic);
            if (f?.GetValue(null) is IDictionary cache)
            {
                long gpu = 0; int n = 0; var drawn = new HashSet<NTexture>();
                foreach (var k in cache.Keys) { var tk = (System.Runtime.CompilerServices.ITuple)k; if (tk[0] is NTexture nt) { n++; gpu += (long)nt.Width * nt.Height * 4; drawn.Add(nt); } }
                long drawnEnc = drawn.Sum(EncSize); long drawnPx = drawn.Sum(t => (long)t.Width * t.Height);
                W($"   SDL GPU textures (drawn at least once): {n} entries / {drawn.Count} distinct, {MB(gpu)} RGBA8; distinct px={drawnPx:N0}; their source-encoded size={MB(drawnEnc)}");
                W($"   -> drawn set at DXT5 (1 B/px) full-res={MB(drawnPx)}, half-res={MB(drawnPx / 4.0)}; DXT1 (0.5 B/px) half-res={MB(drawnPx / 8.0)}; RGBA8 half-res={MB(drawnPx)}");
                foreach (var t in drawn.OrderByDescending(t => (long)t.Width * t.Height).Take(15)) W($"       drawn {t.Width,5}x{t.Height,-5} {(UnityEngine.TextureFormat)t.Format,-10} '{t.Name}'");
            }
        }
        catch (Exception e) { W("   TexCache probe failed: " + e.Message); }
        W($"   framebuffer target {Render.SceneRenderer.G?.W}x{Render.SceneRenderer.G?.H} RGBA8 = {MB((Render.SceneRenderer.G?.W ?? 0) * (Render.SceneRenderer.G?.H ?? 0) * 4.0)} (+ same again for the Stylizer streaming texture and readback buffer)");

        // ---- meshes / geometry ----
        long meshB = 0; int meshN = 0, meshV = 0;
        foreach (var m in all.OfType<NMesh>())
        {
            meshN++; meshV += m.Pos.Length;
            meshB += m.Pos.LongLength * 12 + (m.Col?.LongLength ?? 0) * 4 + (m.Uv0?.LongLength ?? 0) * 16 + (m.Normals?.LongLength ?? 0) * 12 + m.Sub.Sum(s => (long)(s?.Length ?? 0) * 4);
        }
        var crs = all.OfType<NCanvasRenderer>().ToList();
        long crB = crs.Sum(c => c.Pos.LongLength * 12 + (c.Col?.LongLength ?? 0) * 4 + (c.Uv0?.LongLength ?? 0) * 16 + c.Idx.LongLength * 4);
        var sprites = all.OfType<NSprite>().ToList();
        long spB = sprites.Sum(s => (s.Vertices?.LongLength ?? 0) * 8 + (s.Uv?.LongLength ?? 0) * 8 + (s.Triangles?.LongLength ?? 0) * 2);
        W($"--- geometry: NMesh {meshN} ({meshV:N0} verts) = {MB(meshB)}; CanvasRenderer {crs.Count} (withMesh={crs.Count(c => c.Idx.Length > 0)}, active+mesh={crs.Count(c => c.Idx.Length > 0 && c.Go != null && c.Go.ActiveInHierarchy)}) = {MB(crB)}; NSprite {sprites.Count} geometry = {MB(spB)}");
        W($"   last frame: draws={Render.SceneRenderer.LastDraws} tris={Render.SceneRenderer.LastTris}");

        // ---- object census ----
        var gos = all.OfType<NGameObject>().ToList();
        W($"--- objects: GameObjects={gos.Count} (activeInHierarchy={gos.Count(g => g.ActiveInHierarchy)}, scene!=0={gos.Count(g => g.Scene != 0)}); records by kind: " +
          string.Join(" ", all.GroupBy(n => n.GetType().Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
        _types0 = all.GroupBy(n => n.Managed?.GetType().FullName ?? n.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
        W("   top 30 managed types:");
        foreach (var kv in _types0.OrderByDescending(k => k.Value).Take(30)) W($"     {kv.Value,7}  {kv.Key}");
        var monos = all.OfType<NMono>().ToList();
        W($"   MonoBehaviours: {monos.Count} (enabled+active={monos.Count(m => m.Enabled && m.Go != null && m.Go.ActiveInHierarchy)}); Lifecycle.Active={Lifecycle.Active.Count} " +
          $"withUpdate={Lifecycle.Active.Count(m => HasMsg(m, "Update"))} withLateUpdate={Lifecycle.Active.Count(m => HasMsg(m, "LateUpdate"))} withFixedUpdate={Lifecycle.Active.Count(m => HasMsg(m, "FixedUpdate"))}; coroutines={Coroutines.Count}");
        var skel = all.Where(n => n.Managed != null && n.Managed.GetType().Name.StartsWith("Skeleton")).GroupBy(n => n.Managed.GetType().FullName).OrderByDescending(g => g.Count()).ToList();
        W($"   Spine (types named Skeleton*): {skel.Sum(g => g.Count())} total -> " + string.Join("; ", skel.Select(g => $"{g.Key}={g.Count()} (active={g.Count(n => n is NComponent c && c.Go != null && c.Go.ActiveInHierarchy)})")));
        var anims = all.OfType<NBehaviour>().Where(b => b.Managed is Animator).ToList();
        W($"   Animators: {anims.Count} (enabled+active={anims.Count(b => b.Enabled && b.Go != null && b.Go.ActiveInHierarchy && b.Go.Scene != 0)}); Canvas={all.Count(n => n.Managed is Canvas)}");
        var withTree = all.Where(n => n.Data is TypeTreeObject).ToList();
        W($"   records retaining source TypeTree (NObj.Data): {withTree.Count}, root BytesRead sum = {MB(withTree.Sum(n => (long)((TypeTreeObject)n.Data).BytesRead))} (serialized size; managed tree is several x larger); records with a source (HasSource) = {all.Count(n => n.HasSource)}");
        {
            int bags = 0; long keys = 0; foreach (var n in all) if (n.Props != null) { bags++; keys += PropBag.Count(n.Props); }
            W($"   property bags: {bags} records, {keys} keys; diet did not seed {Assets.Diet.PropsKeysDropped} bulky unreadable keys: " +
              string.Join(", ", Assets.Diet.DroppedProps.OrderByDescending(x => x.Value.n).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} x{x.Value.n}")));
        }

        // ---- bundles / serialized files ----
        var abs = all.OfType<NAssetBundle>().ToList();
        long abFile = abs.Sum(a => File.Exists(a.Path) ? new FileInfo(a.Path).Length : 0);
        W($"--- AssetBundle objects loaded: {abs.Count}, files on disk = {MB(abFile)}");
        foreach (var a in abs.OrderByDescending(a => File.Exists(a.Path) ? new FileInfo(a.Path).Length : 0).Take(25)) W($"     {MB(File.Exists(a.Path) ? new FileInfo(a.Path).Length : 0),11}  {a.Name}");
        var db = Assets.Loader.Db;
        var bfs = db.LoadedBundles.ToList();
        long bfDisk = bfs.Sum(b => File.Exists(b.Path) ? new FileInfo(b.Path).Length : 0), lru = 0;
        var cacheF = typeof(BundleFile).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var b in bfs) if (cacheF?.GetValue(b) is IEnumerable lst) foreach (var e in lst) if (e is System.Runtime.CompilerServices.ITuple tu && tu[1] is byte[] blk) lru += blk.LongLength;
        var sfs = db.LoadedFiles.Concat(abs.SelectMany(a => a.Files ?? Array.Empty<SerializedFile>())).Distinct().ToList();
        W($"   AssetDatabase: {bfs.Count} BundleFiles open (disk {MB(bfDisk)}, decompressed-block LRU in RAM {MB(lru)}); {sfs.Count} SerializedFiles, whole-file buffers resident = {MB(sfs.Sum(s => (long)s.ResidentBytes))} in {sfs.Count(s => s.ResidentBytes > 0)} files (of {MB(sfs.Sum(s => (long)s.Length))} total; {sfs.Count(s => s.IsReleasable)} releasable)");
        foreach (var s in sfs.OrderByDescending(s => s.Length).Take(12)) W($"     {MB(s.Length),11}  {s.Name} ({s.Objects.Count} objects){(s.IsReleasable ? $" resident={MB(s.ResidentBytes)} loads={s.Loads}" : "")}");

        // ---- assemblies ----
        var asms = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)).Select(a => (a, len: new FileInfo(a.Location).Length)).ToList();
        string Cat(Assembly a) { var l = a.Location; return l.StartsWith(Program.RuntimeManaged) ? "runtime/Managed (rewritten Unity+game)" : l.StartsWith(Program.GameManaged) ? "game Managed (unmodified)" : a.GetName().Name!.StartsWith("Shepherd") ? "Shepherd engine" : "BCL/.NET"; }
        W($"--- assemblies loaded: {asms.Count}, total file size {MB(asms.Sum(x => x.len))}");
        foreach (var g in asms.GroupBy(x => Cat(x.a))) W($"   {g.Key,-40} n={g.Count(),4} {MB(g.Sum(x => x.len))}");
        foreach (var x in asms.Where(x => Cat(x.a) != "BCL/.NET").OrderByDescending(x => x.len).Take(15)) W($"     {MB(x.len),11}  {x.a.GetName().Name}");

        // ---- FMOD (native heap, read-only stats) ----
        try
        {
            var mt = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("FMOD.Memory"); } catch { return null; } }).FirstOrDefault(t => t != null);
            var gs = mt?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetStats");
            if (gs != null) { var args = new object[gs.GetParameters().Length]; if (args.Length > 2) args[2] = true; var r = gs.Invoke(null, args); W($"--- FMOD.Memory.GetStats -> {r}: current={MB(Convert.ToInt64(args[0]))} max={MB(Convert.ToInt64(args[1]))}"); }
            else W("--- FMOD.Memory type not found");
        }
        catch (Exception e) { W("--- FMOD stats failed: " + (e.InnerException ?? e).Message); }
        W($"--- icalls: {IcallRegistry.Implemented} implemented of {Shepherd.Hooks.Icalls.Table.Length}; hits so far {Shepherd.Hooks.Icalls.HitCounts().Sum():N0}");
    }

    static bool HasMsg(NMono m, string name)
    {
        for (var c = m.Managed?.GetType(); c != null && c != typeof(MonoBehaviour) && c != typeof(Behaviour); c = c.BaseType)
            if (c.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null) return true;
        return false;
    }

    // =====================================================================================================
    static void InstallProbes()
    {
        _ctx = SynchronizationContext.Current;
        var go = new GameObject("__ShepherdDiagProbe");
        go.AddComponent<DiagProbeFirst>(); go.AddComponent<DiagProbeLast>();
        var orig = Lifecycle.Render;
        Lifecycle.Render = () => { Hit(MRenderStart); InRender = true; try { orig?.Invoke(); } finally { InRender = false; Hit(MRenderEnd); } };
        var pre = typeof(Canvas).GetEvent("preWillRenderCanvases", BindingFlags.Static | BindingFlags.Public);
        if (pre != null) pre.AddEventHandler(null, Delegate.CreateDelegate(pre.EventHandlerType!, typeof(MemReport).GetMethod(nameof(OnPreWill), BindingFlags.Static | BindingFlags.NonPublic)!));
        Canvas.willRenderCanvases += OnWillEnd;
        var probe = Objects.Get<NMono>(go.GetComponent<DiagProbeLast>());
        if (probe != null) Invokes.Add(probe, nameof(DiagProbeLast.DiagInvoke), 0f, 1e-6f);
        W($"probes installed: syncCtx={_ctx?.GetType().Name ?? "none"} preWillRenderCanvases={(pre != null)} invokeProbe={(probe != null)}");
    }
    static void OnPreWill() { if (InRender) Hit(MPreWill); }
    static void OnWillEnd() { if (InRender) Hit(MWillEnd); }

    static void WindowStart()
    {
        _alloc0 = GC.GetTotalAllocatedBytes(false); _allocMain0 = GC.GetAllocatedBytesForCurrentThread();
        _gc00 = GC.CollectionCount(0); _gc10 = GC.CollectionCount(1); _gc20 = GC.CollectionCount(2);
        _gcPause0Ticks = GC.GetTotalPauseDuration().Ticks;
        _hits0 = Shepherd.Hooks.Icalls.HitCounts();
    }

    static void WindowReport(int frame)
    {
        int n = FrameMs.Count;
        W($"===== timing window frames {At + 1}..{frame} ({n} frames, headless, Mac {Environment.ProcessorCount} cores) =====");
        if (n == 0) return;
        var sorted = FrameMs.OrderBy(x => x).ToList();
        double avg = FrameMs.Average();
        W($"frame wall time (Lifecycle.Frame only): avg={avg:F2} ms  min={sorted[0]:F2}  median={sorted[n / 2]:F2}  p95={sorted[Math.Min(n - 1, (int)(n * 0.95))]:F2}  max={sorted[^1]:F2}  (= {1000 / avg:F0} fps-equivalent)");
        for (int k = 1; k < NMarks; k++)
            W($"   {PhaseSum[k] / n,8:F3} ms  {100 * PhaseSum[k] / FrameMs.Sum(),5:F1}%  {PhaseNames[k]}{(PhaseMissing[k] > 0 ? $"   [mark missing in {PhaseMissing[k]} frames -> time folded into next phase]" : "")}");
        if (RenderMs.Count > 0) W($"   Render total (Lifecycle.Render delegate): avg={RenderMs.Average():F3} ms over {RenderMs.Count} frames");
        long alloc = GC.GetTotalAllocatedBytes(false) - _alloc0, allocMain = GC.GetAllocatedBytesForCurrentThread() - _allocMain0;
        W($"allocations: {MB(alloc / (double)n)}/frame all threads ({MB(allocMain / (double)n)}/frame main thread); GCs in window gen0/1/2 = {GC.CollectionCount(0) - _gc00}/{GC.CollectionCount(1) - _gc10}/{GC.CollectionCount(2) - _gc20}; GC pause in window = {(GC.GetTotalPauseDuration().Ticks - _gcPause0Ticks) / 10000.0:F1} ms");
        if (ThreadCpuMs.Count > 0)
        {
            var tc = ThreadCpuMs.OrderBy(x => x).ToList(); var pc = ProcCpuMs.OrderBy(x => x).ToList();
            W($"frame CPU time: main thread avg={ThreadCpuMs.Average():F2} ms median={tc[tc.Count / 2]:F2} p95={tc[Math.Min(tc.Count - 1, (int)(tc.Count * 0.95))]:F2}; whole process avg={ProcCpuMs.Average():F2} ms median={pc[pc.Count / 2]:F2} (all threads: Parallel.For workers, GC, finalizer)");
        }
        {   // runtime-agnostic GC pause estimate (Mono's GetTotalPauseDuration is always 0): frames in which a collection ran vs the median clean frame
            var clean = FrameMs.Where((_, i) => !GcFrame[i]).OrderBy(x => x).ToList(); var dirty = FrameMs.Where((_, i) => GcFrame[i]).ToList();
            double cm = clean.Count > 0 ? clean[clean.Count / 2] : 0;
            W($"GC frames: {dirty.Count}/{n} frames had a collection; median clean frame={cm:F2} ms, avg GC frame={(dirty.Count > 0 ? dirty.Average() : 0):F2} ms -> est. pause/GC-frame={(dirty.Count > 0 ? dirty.Average() - cm : 0):F2} ms, est. GC share={(dirty.Count > 0 ? (dirty.Sum() - dirty.Count * cm) / FrameMs.Sum() * 100 : 0):F1}% of frame time; clean-frame avg={(clean.Count > 0 ? clean.Average() : 0):F2} ms");
        }
        var hits = Shepherd.Hooks.Icalls.HitCounts(); long totalHits = 0; var top = new List<(long d, string name)>();
        for (int i = 0; i < hits.Length && i < _hits0.Length; i++) { long d = hits[i] - _hits0[i]; if (d <= 0) continue; totalHits += d; top.Add((d, Shepherd.Hooks.Icalls.Names.Length > i ? Shepherd.Hooks.Icalls.Names[i] : "#" + i)); }
        W($"icalls: {totalHits / (double)n:N0} per frame ({top.Count} distinct). top 15:");
        foreach (var (d, name) in top.OrderByDescending(x => x.d).Take(15)) W($"     {d / (double)n,10:N0}/frame  {name}");
        var types1 = Objects.All.Where(o => !o.Destroyed).GroupBy(o => o.Managed?.GetType().FullName ?? o.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
        var growth = types1.Select(kv => (kv.Key, d: kv.Value - _types0.GetValueOrDefault(kv.Key))).Where(x => x.d != 0).OrderByDescending(x => Math.Abs(x.d)).Take(8).ToList();
        W($"live NObj growth over window: {types1.Values.Sum() - _types0.Values.Sum():+#;-#;0} -> " + string.Join(", ", growth.Select(g => $"{g.Key} {g.d:+#;-#;0}")));
        W($"RSS at window end = {MB(Environment.WorkingSet)}; GC.GetTotalMemory(false) = {MB(GC.GetTotalMemory(false))}");
        var (fb, rf, bc) = Assets.Loader.Db.ResidentBytes();
        W($"{Assets.Diet.Counters()}; at window end: {rf} whole-file buffers resident = {MB(fb)}, bundle block caches = {MB(bc)}");
    }

    // =====================================================================================================
    static void Bench1(Action bench) { try { bench(); } catch (Exception e) { MemReportKit4.BenchFailed(bench, e, W); } }   // kit4 intro-plan

    /// <summary>Run after the timing window (the frame loop is done measuring): standalone costs of the CPU Stylizer pass
    /// (as shipped: GPU readback + Parallel.For), the readback alone, a single-thread replica of its per-pixel kernel,
    /// and one Objects.All.ToArray() scan (the engine does several of these per frame).</summary>
    static void Bench()
    {
        var gb = Render.SceneRenderer.G; if (gb == null) return;
        W("===== micro-benchmarks (after the window) =====");
        const int N = 20; var sw = new Stopwatch();
        if (Render.MtRender.Cur != null) W("Stylizer micro-benchmark skipped: SHEPHERD_MT_RENDER=1 (the backend runs behind the multicore pipeline; run the report without it)");
        else if (gb is not Render.Gfx g)
        {   // GL backend: the GPU Stylizer pass, waited for
            gb.EndWorldPass(); gb.Finish();
            sw.Restart(); for (int i = 0; i < N; i++) { gb.EndWorldPass(); gb.Finish(); } sw.Stop();
            W(Render.BenchText.Stylizer(gb, sw.Elapsed.TotalMilliseconds / N));
        }
        else if (Render.DerivedRender.Stylizer is { } sty)
        {
        sty.Apply(g);   // warm
        sw.Restart(); for (int i = 0; i < N; i++) sty.Apply(g); sw.Stop();
        W($"StylizerPass.Apply as shipped ({g.W}x{g.H}, readback + Parallel.For on {Environment.ProcessorCount} cores + upload): {sw.Elapsed.TotalMilliseconds / N:F3} ms/call");
        sw.Restart();
        for (int i = 0; i < N; i++)
        {
            SDL_SetRenderTarget(g.Renderer, g.Target);
            var s = SDL_RenderReadPixels(g.Renderer, null); if (s == null) break;
            var c = SDL_ConvertSurface(s, PIXELFORMAT_ABGR8888); SDL_DestroySurface(s); SDL_DestroySurface(c);
        }
        sw.Stop();
        W($"   of which GPU readback+convert alone: {sw.Elapsed.TotalMilliseconds / N:F3} ms/call");
        }
        if (Render.DerivedRender.Stylizer is { } ks) ks.KernelBench(W);   // the owner-local kernel's single-thread replica
        sw.Restart(); int cnt = 0; for (int i = 0; i < 50; i++) { var arr = Objects.All.ToArray(); foreach (var o in arr) if (o is NComponent) cnt++; } sw.Stop();
        W($"Objects.All.ToArray()+type-test scan over {Objects.LiveCount} records: {sw.Elapsed.TotalMilliseconds / 50:F3} ms each (engine does >=5 such scans per frame: LayoutRootCanvases, NotifyRectChanges, DrawWorld/camera, AnimatorRuntime.UpdateAll, StylizerPass lookup, DrawOverlayCanvases)");
    }

    /// <summary>Re-runs SceneRenderer.Frame's steps one by one (private methods via reflection) to split the in-frame
    /// "DrawWorld + Stylizer + Overlay" span. Same calls, same order; only done after the timing window.</summary>
    static void RenderSplitBench()
    {
        var g = Render.SceneRenderer.G; if (g == null) return;
        if (Render.MtRender.Cur != null) { W("===== render split skipped: SHEPHERD_MT_RENDER=1 (its standalone replay of the frame's steps would record into the pipeline's command list; run the report without it) ====="); return; }
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var sr = typeof(Render.SceneRenderer);
        var layout = sr.GetMethod("LayoutRootCanvases", S); var drawWorld = sr.GetMethod("DrawWorld", S); var overlay = sr.GetMethod("DrawOverlayCanvases", S);
        var preWill = typeof(Canvas).GetMethod("SendPreWillRenderCanvases", S); var will = typeof(Canvas).GetMethod("SendWillRenderCanvases", S);
        var cams = CameraIcalls.AllCameras().ToList();
        string[] names = g is Render.Gfx
            ? new[] { "LayoutRootCanvases", "NotifyRectChanges", "Canvas pre/willRender (uGUI rebuild)", $"DrawWorld x{cams.Count} camera(s) (CPU: build vertices + queue SDL_RenderGeometry)", "StylizerPass.Apply (readback flushes GPU world work)", "DrawOverlayCanvases (CPU)", "final GPU flush (readback) for overlay draws" }
            : Render.BenchText.SplitNames(g, cams.Count);
        const int N = 20; var acc = new double[names.Length]; var ts = new long[names.Length + 1];
        for (int i = 0; i < N + 2; i++)
        {
            ts[0] = Stopwatch.GetTimestamp();
            layout?.Invoke(null, null); ts[1] = Stopwatch.GetTimestamp();
            Render.SceneRenderer.NotifyRectChanges(); ts[2] = Stopwatch.GetTimestamp();
            preWill?.Invoke(null, null); will?.Invoke(null, null); ts[3] = Stopwatch.GetTimestamp();
            g.Begin(0, 0, 0, 1);
            foreach (var cam in cams) drawWorld?.Invoke(null, new object[] { cam }); ts[4] = Stopwatch.GetTimestamp();
            g.EndWorldPass(); ts[5] = Stopwatch.GetTimestamp();   // SDL: StylizerPass.Apply; GL: GPU pass
            overlay?.Invoke(null, null); ts[6] = Stopwatch.GetTimestamp();
            g.Finish(); ts[7] = Stopwatch.GetTimestamp();          // SDL: full-target readback (as before); GL: flush + glFinish
            if (i >= 2) for (int k = 0; k < names.Length; k++) acc[k] += (ts[k + 1] - ts[k]) * TickMs;
        }
        W($"===== render split (standalone replay of SceneRenderer.Frame steps, avg of {N}) =====");
        for (int k = 0; k < names.Length; k++) W($"   {acc[k] / N,8:F3} ms  {names[k]}");
        W($"   {acc.Sum() / N,8:F3} ms  total; draws/frame={Render.SceneRenderer.LastDraws / (N + 2)}");
    }

    /// <summary>Replays Update()/LateUpdate() over Lifecycle.Active (same order, same reflection Invoke as Lifecycle.Call)
    /// timing each call, grouped by MonoBehaviour type; plus AnimatorRuntime.UpdateAll standalone. Only after the window.</summary>
    static void UpdateSplitBench()
    {
        const BindingFlags B = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        MethodInfo Find(Type t, string name) { for (var c = t; c != null && c != typeof(MonoBehaviour) && c != typeof(Behaviour); c = c.BaseType) { var m = c.GetMethod(name, B, null, Type.EmptyTypes, null); if (m != null) return m; } return null; }
        const int N = 10;
        foreach (var msg in new[] { "Update", "LateUpdate" })
        {
            var per = new Dictionary<string, (double ms, int calls)>(); double total = 0;
            var list = Lifecycle.Active.Where(m => m.Started && m.IsEnabledCalled && !m.Destroyed && m.Managed != null && !(m.Managed is DiagProbeFirst || m.Managed is DiagProbeLast))
                .Select(m => (m, mi: Find(m.Managed.GetType(), msg))).Where(x => x.mi != null).ToList();
            for (int i = 0; i < N; i++)
                foreach (var (m, mi) in list)
                {
                    if (m.Destroyed) continue;
                    long a = Stopwatch.GetTimestamp();
                    try { mi.Invoke(m.Managed, null); } catch { }
                    double ms = (Stopwatch.GetTimestamp() - a) * TickMs; total += ms;
                    var k = m.Managed.GetType().FullName ?? "?"; var e = per.GetValueOrDefault(k); per[k] = (e.ms + ms, e.calls + 1);
                }
            W($"===== {msg}() replay by type ({list.Count} behaviours, avg of {N} replays): {total / N:F3} ms/frame =====");
            foreach (var kv in per.OrderByDescending(x => x.Value.ms).Take(15)) W($"   {kv.Value.ms / N,8:F3} ms  {100 * kv.Value.ms / Math.Max(total, 1e-9),5:F1}%  x{kv.Value.calls / N,-4} {kv.Key}");
        }
        var sw = Stopwatch.StartNew(); for (int i = 0; i < N; i++) Anim.AnimatorRuntime.UpdateAll(); sw.Stop();
        W($"AnimatorRuntime.UpdateAll standalone: {sw.Elapsed.TotalMilliseconds / N:F3} ms/call");
    }

    /// <summary>Beffio Stylizer.Update anatomy: what its serialized flags are, how many Pixelator/Quad objects exist, and the
    /// standalone cost of each engine call SetUpPixelator makes (Find, AddComponent&lt;Camera&gt;, CreatePrimitive, DestroyImmediate),
    /// with the NObj-record growth each call leaves behind. Only after the window (creates + destroys a few objects).</summary>
    static void StylizerBench()
    {
        var sts = Objects.All.OfType<NMono>().Where(m => !m.Destroyed && m.Managed?.GetType().FullName == "Beffio.Dithering.Stylizer").ToList();
        W($"===== Stylizer.Update anatomy (after the window): {sts.Count} Stylizer components: " + string.Join("; ", sts.Select(m => $"'{m.Go?.Name}' enabled={m.Enabled} active={m.Go?.ActiveInHierarchy} inUpdateSet={Lifecycle.Active.Contains(m)}")) + " =====");
        var st = sts.FirstOrDefault(m => m.Enabled && m.Go != null && m.Go.ActiveInHierarchy && m.IsEnabledCalled);
        if (st == null) { W("stylizer bench: no enabled+active Stylizer"); return; }
        var stTree = st.SourceTree(transient: true);   // diet: re-read without leaving the file resident (the census follows)
        string Ser(string f) => stTree is TypeTreeObject d && d.TryGet(f, out var v) ? (v?.ToString() ?? "null") : "(absent)";
        W($"   serialized on '{st.Go?.Name}': Pixelate={Ser("Pixelate")} Dither={Ser("Dither")} _pixelScale={Ser("_pixelScale")}; live field Pixelate={F(st.Managed, "Pixelate")}; enabled={st.Enabled} activeInHierarchy={st.Go?.ActiveInHierarchy}");
        int Named(string n) => Objects.All.Count(o => o is NGameObject g && !g.Destroyed && g.Name == n);
        W($"   live GameObjects named 'Pixelator'={Named("Pixelator")} 'Quad'={Named("Quad")}; Stylizer fields: pixelator={(F(st.Managed, "pixelator") as UnityEngine.Object) != null} quad={(F(st.Managed, "quad") as UnityEngine.Object) != null} ortoCamera={(F(st.Managed, "ortoCamera") as UnityEngine.Object) != null} cam={(F(st.Managed, "cam") as UnityEngine.Object) != null}");
        Dictionary<string, int> Kinds() => Objects.All.Where(o => !o.Destroyed).GroupBy(o => o.GetType().Name).ToDictionary(g => g.Key, g => g.Count());
        string Delta(Dictionary<string, int> a, Dictionary<string, int> b, int n) => string.Join(" ", b.Select(kv => (kv.Key, d: kv.Value - a.GetValueOrDefault(kv.Key))).Where(x => x.d != 0).Select(x => $"{x.Key}{(x.d > 0 ? "+" : "")}{x.d / (double)n:0.##}")) is { Length: > 0 } s ? s : "none";
        void Time(string what, int n, Action a)
        {
            try
            {
                a();   // warm (JIT)
                var k0 = Kinds(); var sw = Stopwatch.StartNew(); for (int i = 0; i < n; i++) a(); sw.Stop(); var k1 = Kinds();
                W($"   {sw.Elapsed.TotalMilliseconds * 1000 / n,9:F1} us  {what}   [records/call: {Delta(k0, k1, n)}]");
            }
            catch (Exception e) { W($"   {what}: {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}"); }
        }
        Time("GameObject.Find(\"Pixelator\")", 50, () => GameObject.Find("Pixelator"));
        Time("Shader.Find(\"Unlit/Texture\")", 50, () => Shader.Find("Unlit/Texture"));
        Time("new GameObject + AddComponent<Camera> + DestroyImmediate", 20, () => { var go = new GameObject("__bench"); go.AddComponent<Camera>(); UnityEngine.Object.DestroyImmediate(go); });
        Time("CreatePrimitive(Quad) + DestroyImmediate(collider) + DestroyImmediate(go)", 20, () => { var q = GameObject.CreatePrimitive(PrimitiveType.Quad); UnityEngine.Object.DestroyImmediate(q.GetComponent<Collider>()); UnityEngine.Object.DestroyImmediate(q); });
        var upd = st.Managed.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Time("Stylizer.Update() as shipped (whole SetUpPixelator + DestroyImmediate cycle)", 20, () => upd.Invoke(st.Managed, null));
    }

    static long LiveHeap()
    {
        CompactLohOnce();
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
        _lastTotalMem = GC.GetTotalMemory(false);
        var gi = FullGcInfo(); _lastFrag = gi.FragmentedBytes;
        return gi.HeapSizeBytes;
    }
    static long _lastTotalMem, _lastFrag;
    // HeapSizeBytes keeps freed space as (gen0) fragmentation after these drops (205.9 MiB by the last step on 2026-10-02), so the
    // live figure is HeapSizeBytes - fragmentation; GC.GetTotalMemory(false) is shown only for comparison (it is not live bytes here).
    static string TM => $" [fragmented {MB(_lastFrag)} => live {MB(FullGcInfo().HeapSizeBytes - _lastFrag)}; GC.GetTotalMemory={MB(_lastTotalMem)}]";

    /// <summary>Opt-in (":drop"): attribute the live heap by releasing, in turn, decoded RGBA buffers, retained source
    /// TypeTrees, and whole-file SerializedFile byte[]s, measuring the live heap after each. Destroys engine state, so it
    /// only runs as the very last thing (the boot run must end at frame N+window).</summary>
    static void DropAttribution()
    {
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        W("===== destructive heap attribution (end of run; engine state discarded after this) =====");
        // every record the engine still references: live ones + destroyed ones kept by registries (they still carry Data/Props/SourceFile)
        var recs = new HashSet<NObj>(Objects.All);
        void AddKeys(Type t, string field, bool values) { if (t.GetField(field, S)?.GetValue(null) is IDictionary d) foreach (var x in values ? d.Values : d.Keys) if (x is NObj n) recs.Add(n); else if (x is System.Runtime.CompilerServices.ITuple tu && tu[0] is NObj n2) recs.Add(n2); }
        foreach (var n in Assets.Loader.LiveRecords) recs.Add(n); AddKeys(typeof(Render.SceneRenderer), "LastSize", false); AddKeys(typeof(Render.SceneRenderer), "_canvasScreen", false);
        AddKeys(typeof(Anim.AnimatorRuntime), "All", false);
        foreach (var ct in typeof(Anim.AnimatorRuntime).Assembly.GetTypes().Where(t => t.Namespace == "Shepherd.Engine.Anim")) AddKeys(ct, "Cache", false);
        int dead = recs.Count(n => n.Destroyed);
        W($"records referenced by engine registries: {recs.Count} ({dead} of them destroyed but still retained by Loader.Live / SceneRenderer.LastSize / anim caches; {recs.Count(n => n.Destroyed && n.SourceFile != null)} still pin a SerializedFile)");
        long h0 = LiveHeap(); W($"live heap baseline                                   = {MB(h0)}  RSS={MB(Environment.WorkingSet)}" + TM);
        foreach (var t in recs.OfType<NTexture>()) t.Rgba = null;
        (Render.SceneRenderer.G as Render.Gfx)?.DropCpuPassBuffer();
        long h1 = LiveHeap(); W($"after dropping NTexture.Rgba (+Stylizer buffer)      = {MB(h1)}  (RGBA8 buffers = {MB(h0 - h1)})" + TM);
        foreach (var n in recs) n.Data = null;
        long h2a = LiveHeap(); W($"after dropping NObj.Data source TypeTrees           = {MB(h2a)}  (retained TypeTrees = {MB(h1 - h2a)})" + TM);
        int lazy = 0, bags = 0; foreach (var n in recs) if (n.Props != null) { bags++; lazy += PropBag.Entries(n.Props).Count(x => x.Value is Assets.LazyRef); n.ReplaceProps(null); }
        long h2 = LiveHeap(); W($"after dropping NObj.Props bags ({bags} bags, {lazy} top-level LazyRef->SerializedFile) = {MB(h2)}  (property bags = {MB(h2a - h2)})" + TM);
        var db = Assets.Loader.Db;
        var (fileBytes, weak) = FileWeakRefs(db, recs);   // separate frame: Debug-build JIT keeps hidden LINQ temporaries alive to method end
        foreach (var n in recs) n.SourceFile = null;
        foreach (var ab in recs.OfType<NAssetBundle>()) { ab.Files = null; ab.Owner = null; }
        foreach (var fn in new[] { "_files", "_bundleFiles", "_extCache", "_nodeOwner" }) (typeof(AssetDatabase).GetField(fn, I)?.GetValue(db) as IDictionary)?.Clear();
        var cacheF = typeof(BundleFile).GetField("_cache", I);
        foreach (var b in db.LoadedBundles) { var c = cacheF?.GetValue(b); c?.GetType().GetMethod("Clear")?.Invoke(c, null); }
        foreach (var fn in new[] { "_loadingFile", "_gm" }) typeof(Assets.Loader).GetField(fn, S)?.SetValue(null, null);
        long h3 = LiveHeap();
        W($"after releasing SerializedFiles + bundle block LRUs   = {MB(h3)}  (freed {MB(h2 - h3)}; {weak.Count(x => x.IsAlive)}/{weak.Count} files still reachable elsewhere, {MB(fileBytes)} file bytes total)" + TM);
        W($"=> residual managed heap (game state + Unity managed objects + engine records + caches) = {MB(h3)}; RSS now {MB(Environment.WorkingSet)}");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (long bytes, List<WeakReference> weak) FileWeakRefs(AssetDatabase db, HashSet<NObj> recs)
    {
        var set = new HashSet<SerializedFile>(db.LoadedFiles);
        foreach (var ab in recs.OfType<NAssetBundle>()) if (ab.Files != null) foreach (var f in ab.Files) set.Add(f);
        long bytes = 0; var weak = new List<WeakReference>(set.Count);
        foreach (var f in set) { bytes += f.Length; weak.Add(new WeakReference(f)); }
        set.Clear(); return (bytes, weak);
    }

    static object F(object o, string n) { if (o == null) return null; var t = o.GetType(); const BindingFlags B = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic; return t.GetField(n, B)?.GetValue(o) ?? t.GetProperty(n, B)?.GetValue(o); }

}

/// <summary>Runs first in Update/LateUpdate (lowest execution order).</summary>
[DefaultExecutionOrder(int.MinValue)]
public sealed class DiagProbeFirst : MonoBehaviour
{
    void Update() => MemReport.Hit(MemReport.MUpdFirst);
    void LateUpdate() => MemReport.Hit(MemReport.MLateFirst);
}

/// <summary>Runs last in Update/LateUpdate (highest execution order); posts to the sync context and owns the Invoke probe.</summary>
[DefaultExecutionOrder(int.MaxValue)]
public sealed class DiagProbeLast : MonoBehaviour
{
    void Update() { MemReport.Hit(MemReport.MUpdLast); MemReport.PostSync(); }
    void LateUpdate() => MemReport.Hit(MemReport.MLateLast);
    internal void DiagInvoke() => MemReport.Hit(MemReport.MInvoke);
}
#endif
