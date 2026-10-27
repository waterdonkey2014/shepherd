using System.Runtime.InteropServices;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Render;

/// <summary>SHEPHERD_UICACHE_CHECK=1 (check runs only; slow): every frame the retained UI pass runs for real (to the backend, the stream
/// hash and RenderCheck as usual) while its stream is recorded, then the reference pass (DrawOverlayCanvasesRef, the pass as it was) is
/// run again on the same scene state into a recorder only (no backend draw / clip calls; the FX and draw counters are restored), and
/// the two are compared event by event: every Draw (texture handle, blend, sdf, all 32 bytes of every vertex, every index) and every
/// Clip / NoClip, plus the AbsoluteDepth numbering (renderer and value, in order). Per visited renderer it also compares the carried
/// alpha with RenderIcalls.UiAlpha (bit for bit) and the traversal's root with RenderIcalls.RootCanvasOf. Mismatches: [uicache]
/// lines (the first 20 in full); <see cref="Summary"/> at exit.</summary>
[ProfileOnly]
public static class UiCacheCheck
{
    static long _frames, _events, _draws, _verts, _bad, _depthBad, _alphaChecks, _alphaBad, _rootBad, _hits, _misses, _moves, _depths;
    static readonly RenderCheck.Rec _live = new(), _ref = new();
    static readonly List<(NCanvasRenderer cr, int depth)> _liveDepths = new(), _refDepths = new();

    public static void Frame()
    {
        _frames++;
        _live.E.Clear(); _ref.E.Clear(); _liveDepths.Clear(); _refDepths.Clear();
        SceneRenderer.UiCap = _live; SceneRenderer.UiCapOnly = false; SceneRenderer.UiDepths = _liveDepths;
        try { SceneRenderer.RunFastPass(); }   // the real pass: an exception goes to SceneRenderer.Frame as usual
        finally { SceneRenderer.UiCap = null; SceneRenderer.UiDepths = null; }
        _hits += SceneRenderer.LastUiHits; _misses += SceneRenderer.LastUiMisses; _moves += SceneRenderer.LastUiMoves;
        var counters = SceneRenderer.UiCounters();
        SceneRenderer.UiCap = _ref; SceneRenderer.UiCapOnly = true; SceneRenderer.UiDepths = _refDepths;
        try { SceneRenderer.DrawOverlayCanvasesRef(); }
        catch (Exception e) { _bad++; Note($"frame {Runtime.Time.FrameCount}: reference pass threw {e.GetType().Name}: {e.Message}"); }
        finally { SceneRenderer.UiCap = null; SceneRenderer.UiCapOnly = false; SceneRenderer.UiDepths = null; SceneRenderer.SetUiCounters(counters); }
        foreach (var (cr, d) in _liveDepths) cr.AbsoluteDepth = d;   // the real pass's numbering stays (equal unless reported below)
        Compare();
        _live.E.Clear(); _ref.E.Clear(); _liveDepths.Clear(); _refDepths.Clear();   // keep nothing alive
    }

    /// <summary>Called by the retained pass for every renderer it visits (before its early-outs).</summary>
    public static void Visit(NCanvasRenderer cr, float alpha, NComponent root)
    {
        _alphaChecks++;
        float want = RenderIcalls.UiAlpha(cr.Go);
        if (BitConverter.SingleToInt32Bits(want) != BitConverter.SingleToInt32Bits(alpha)) { _alphaBad++; Note($"frame {Runtime.Time.FrameCount}: '{cr.Go?.Name}' carried alpha {alpha:R} vs UiAlpha {want:R}"); }
        if (!ReferenceEquals(RenderIcalls.RootCanvasOf(cr.Go), root)) { _rootBad++; Note($"frame {Runtime.Time.FrameCount}: '{cr.Go?.Name}' traversal root '{root?.Go?.Name}' vs RootCanvasOf '{RenderIcalls.RootCanvasOf(cr.Go)?.Go?.Name}'"); }
    }

    static void Compare()
    {
        var a = _live.E; var b = _ref.E;
        _events += a.Count; _depths += _liveDepths.Count;
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.kind == 0) { _draws++; _verts += x.v.Length; }
            string why = x.kind != y.kind ? $"event kind {x.kind} vs {y.kind}"
                : x.kind == 0 ? (x.tex != y.tex ? "texture" : x.blend != y.blend ? "blend" : x.sdf != y.sdf ? "sdf"
                    : !MemoryMarshal.AsBytes(x.v.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(y.v.AsSpan())) ? $"vertices ({x.v.Length} vs {y.v.Length})"
                    : !x.idx.AsSpan().SequenceEqual(y.idx) ? $"indices ({x.idx.Length} vs {y.idx.Length})" : null)
                : x.kind == 1 ? (x.x != y.x || x.y != y.y || x.w != y.w || x.h != y.h ? "clip rect" : null) : null;
            if (why != null) { _bad++; Note($"frame {Runtime.Time.FrameCount}: UI event #{i} of {a.Count}/{b.Count} differs: {why}"); return; }
        }
        if (a.Count != b.Count) { _bad++; Note($"frame {Runtime.Time.FrameCount}: {a.Count} UI events (retained) vs {b.Count} (reference)"); return; }
        bool depthOk = _liveDepths.Count == _refDepths.Count;
        for (int i = 0; depthOk && i < _liveDepths.Count; i++) depthOk = ReferenceEquals(_liveDepths[i].cr, _refDepths[i].cr) && _liveDepths[i].depth == _refDepths[i].depth;
        if (!depthOk) { _depthBad++; Note($"frame {Runtime.Time.FrameCount}: AbsoluteDepth numbering differs ({_liveDepths.Count} vs {_refDepths.Count} renderers)"); }
    }
    static void Note(string s) { if (++_notes <= 20) Log.Info("[uicache] " + s); }
    static int _notes;

    public static string Summary() => !UiCacheCfg.Check ? null :
        $"[uicache] {_frames} frames: retained UI pass vs reference pass: {_events} events ({_draws} draws, {_verts} vertices), {_bad} stream mismatches, " +
        $"{_depths} AbsoluteDepth assignments, {_depthBad} numbering mismatches; carried alpha {_alphaChecks} checked, {_alphaBad} differ; roots {_rootBad} differ; " +
        $"vertex blocks reused {_hits}, rebuilt {_misses}, moved {_moves} (kit #6 fps: new positions only)";
}
