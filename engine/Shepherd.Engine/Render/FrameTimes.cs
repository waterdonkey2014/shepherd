using System.Diagnostics;
namespace Shepherd.Engine.Render;

/// <summary>Frame-time / draw-count summary for backend comparisons (only when SHEPHERD_RENDER is given explicitly).
/// Per frame: the whole Lifecycle.Frame (logic + render), the render part alone (SceneRenderer.Frame incl. the backend's
/// end-of-frame flush), managed bytes allocated, draws in/out. SHEPHERD_RENDER_STATS=from:to picks the frames (default: the
/// last 100); SHEPHERD_RENDER_SYNC=1 waits for the GPU at the end of every frame (inside the measured time) so GPU work
/// queued by a pipelining backend is charged to its own frame.</summary>
[ProfileOnly]
public sealed class FrameTimes
{
    readonly int _from = -1, _to = int.MaxValue; readonly bool _sync;
    readonly List<(int f, double total, double render, long alloc, int din, int dout)> _rows = new();
    long _alloc0;
    public FrameTimes(int frames, int every)
    {
        if (Environment.GetEnvironmentVariable("SHEPHERD_RENDER_STATS") is string r && r.Split(':') is { Length: 2 } p && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b)) { _from = a; _to = b; }
        _sync = Environment.GetEnvironmentVariable("SHEPHERD_RENDER_SYNC") == "1";
        _alloc0 = GC.GetAllocatedBytesForCurrentThread();
    }
    public void Frame(int i, long t0, IRenderBackend g)
    {
        if (_sync) g.Finish();
        long t1 = Stopwatch.GetTimestamp(), al = GC.GetAllocatedBytesForCurrentThread();
        bool mt = g is MtBackend;   // SHEPHERD_MT_RENDER=1: frame i reaches the backend later; its draw counts are filled in when it has
        if (i >= _from && i <= _to) _rows.Add((i, (t1 - t0) * 1000.0 / Stopwatch.Frequency, SceneRenderer.LastFrameTicks * 1000.0 / Stopwatch.Frequency, al - _alloc0, mt ? -1 : g.Stats.DrawsIn, mt ? -1 : g.Stats.DrawsOut));
        _alloc0 = al;
        if (mt) FillMt((MtBackend)g);
    }
    void FillMt(MtBackend m)
    {
        while (m.TryTakeFrameStats(out var s))
            for (int k = _rows.Count - 1; k >= 0 && _rows[k].f >= s.frame; k--)
                if (_rows[k].f == s.frame) { var r = _rows[k]; r.din = s.din; r.dout = s.dout; _rows[k] = r; break; }
    }
    static string Dist(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray(); if (a.Length == 0) return "n/a";
        double P(double q) => a[Math.Min(a.Length - 1, (int)Math.Round(q * (a.Length - 1)))];
        return $"mean {a.Average():F2} p50 {P(0.5):F2} p95 {P(0.95):F2} max {a[^1]:F2} ms";
    }
    public void Shot(int i, IRenderBackend g) => Log.Info($"[render] frame {i}: {g.Name} {g.Stats} | scene culled={SceneRenderer.LastCulled} clipTris={SceneRenderer.LastClipTris} dropTris={SceneRenderer.LastDropTris} rtCams={SceneRenderer.LastRtCams} (skipped {SceneRenderer.LastRtSkipped}) fx skip={SceneRenderer.LastFxSkips} blend={SceneRenderer.LastFxBlends} glow={SceneRenderer.LastFxGlows} grab={SceneRenderer.LastFxGrabs} rtZero={SceneRenderer.LastFxRtZero}" +
        $" | fidelity lut={PostStats.Lut} maskDraws={PostStats.MaskDraws} (unsupported {PostStats.MaskSkipped}) litDraws={SceneRenderer.ShadingStats.Lit} foggedVerts={SceneRenderer.ShadingStats.Fog} triggers2d colliders={Runtime.Triggers2D.LastColliders} tests={Runtime.Triggers2D.LastTests} callbacks={Runtime.Triggers2D.LastCallbacks} pairs={Runtime.Triggers2D.LastPairs} (since start: {Runtime.Triggers2D.Steps} steps, {Runtime.Triggers2D.PairSteps} pair-steps, {Runtime.Triggers2D.MatrixSkips} layer-matrix skips)");
    static (int Lut, int MaskDraws, int MaskSkipped) PostStats => DerivedRender.Post?.LastStats ?? default;
    public void Summary(IRenderBackend g)
    {
        if (g is MtBackend m) { m.Sync(); FillMt(m); g = m.R; }   // every frame has reached the backend
        var rows = _from >= 0 ? _rows : _rows.Skip(Math.Max(0, _rows.Count - 100)).ToList();
        if (rows.Count == 0) { Log.Info("[render] summary: no frames in the window"); return; }
        Log.Info($"[render] summary {g.Name} {g.W}x{g.H}, frames {rows[0].f}..{rows[^1].f} (n={rows.Count}){(_sync ? ", GPU-synced every frame" : "")}:");
        Log.Info($"[render]   frame total  {Dist(rows.Select(r => r.total))}");
        Log.Info($"[render]   render part  {Dist(rows.Select(r => r.render))}");
        Log.Info($"[render]   draws/frame in {rows.Average(r => r.din):F1} out {rows.Average(r => r.dout):F1}; managed alloc/frame mean {rows.Average(r => r.alloc) / 1024:F1} KiB");
        Log.Info($"[render]   last frame: {g.Stats}");
        if ((g as GlBackend ?? (g as TeeBackend)?.A as GlBackend) is GlBackend gl) Log.Info($"[render]   {gl.TextureSummary()}");
        if (g is NullBackend nb) Log.Info($"[render]   {nb.TextureSummary()}");
        foreach (var kv in GlTextures.DxtCensus.OrderByDescending(k => k.Value.px)) Log.Info($"[render]   dxt census: {kv.Key,-44} {kv.Value.n,4} textures, {kv.Value.px / 1e6,7:F2} Mpx");
    }
}

/// <summary>MemReport's text for non-SDL backends (literals live in a [ProfileOnly] type: the Mac default's diet key scan is unchanged).</summary>
[ProfileOnly]
public static class BenchText
{
    public static string Stylizer(IRenderBackend g, double ms) => $"GPU Stylizer pass ({g.Name} {g.W}x{g.H}, one full-screen fragment pass + glFinish): {ms:F3} ms/call";
    public static string[] SplitNames(IRenderBackend g, int cams) => new[] { "LayoutRootCanvases", "NotifyRectChanges", "Canvas pre/willRender (uGUI rebuild)", $"DrawWorld x{cams} camera(s) (CPU: build vertices + record batches)", $"EndWorldPass ({g.Name}: upload + issue world batches, GPU Stylizer pass)", "DrawOverlayCanvases (CPU)", "Finish (flush UI batches + wait for the GPU)" };
}
