#if !SHEPHERD_EBOOT
using System.Runtime.InteropServices;
using Shepherd.Engine.Render;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #11, track measure: the PIXEL-CONTRIBUTION CENSUS (roadmap kit #11, "pixel-contribution census"; from Ori and the Will
/// of the Wisps). Offline, on the Mac's GL backend, over recorded routes (the fixtures' pad files): on each sampled frame the main target is
/// read back after every draw, so each draw's IMMEDIATE contribution (the pixels it changed when drawn) is exact, and a per-pixel owner map
/// gives its FINAL one. Classes (per draw on the main target, in the game's order):
/// <list type="bullet">
/// <item><b>noop</b> -- changed no pixel when drawn (with why: off-screen / clipped away, every vertex alpha 0 under a blend that then
/// cannot change the destination, covered (below), or none of these: transparent texels / the same value) -- an exact skip only where the
/// reason is provable from the draw's own data and the later opaque draws (the first three);</item>
/// <item><b>hidden</b> -- changed pixels, and its whole FOOTPRINT (every pixel its geometry can reach: the vertices' bounds, one pixel
/// wider, clipped) lies under later opaque overwrites -- a clear, a full-target opaque draw, or an opaque axis-aligned rectangle (a draw
/// without blending or an untextured fill: the GL backend draws both without blending) whose whole pixels are counted -- in the same
/// pass. Across the post pass (EndWorldPass may filter neighbouring pixels) only a full-target cover counts. Then no texel it could ever
/// have can show: the opaque-panel rule, an exact skip rule; the killer draw is named;</item>
/// <item><b>overwritten</b> -- every pixel it changed THIS frame was changed again by a later opaque overwrite (same pass, or a full-target
/// one), but its footprint is not proven covered: no final contribution on this frame only -- not a skip rule (other texels, or the
/// same draw a frame later, may land outside the overwrite);</item>
/// <item><b>blended-over</b> -- some of its pixels survive (no opaque overwrite after it), but it is the last writer of none: later
/// draws blended over every one (its value lives on through the blend);</item>
/// <item><b>visible</b> -- the last writer of final pixels (how many: a small number is reported, never cut -- the census finds exact rules,
/// it never drops a draw that merely looks negligible). The post pass takes no pixel's ownership (it transforms the whole picture).</item>
/// </list>
/// The post pass (the world's grading / Stylizer at EndWorldPass) is a row of its own ("post", a blending writer). Draws into off-screen
/// targets are counted apart (they matter when sampled). It reads the target back after every event (a flush each: the backend's batches
/// split there) and draws nothing; that the census frames' final images are unchanged by it was checked on the Mac's GL against a run without
/// it (vita/notes/kit11-measure.md, "Review fixes").
/// gpu1's GPU-projected world draws (stream kind 8, opt-in SHEPHERD_K11_GPUPROJ=1) get a row of their own (category world-proj): their
/// vertices are world-space, so the footprint is the whole target and no coverage is claimed (never a killer) -- conservative, exact pixels.
/// Switch: <c>SHEPHERD_K11_CENSUS=&lt;frames&gt;</c> (a list "a,b", "a-b", "every:N" or "every:N@a-b"; "0" = off), GL and single-threaded rendering;
/// rows k11-census.tsv, per scene k11-census-sum.tsv + [k11] census lines.</summary>
[ProfileOnly]
public static class K11Census
{
    static readonly string Spec = K11Measure.Master ? (Environment.GetEnvironmentVariable("SHEPHERD_K11_CENSUS") ?? "").Trim() : "";
    // (kit #11 integration review: "0" is no frame list -- off, so a leftover "SHEPHERD_K11_CENSUS=0" never turns the census on)
    public static readonly bool On = Spec.Length > 0 && Spec != "0";
    static readonly Func<int, bool> At = K11Measure.Frames(Spec);
    public static string Describe() => On ? Spec : "off";
    /// <summary>The census is reading this frame (RenderCheck.Rec calls in after each event).</summary>
    public static bool Live;
    static int _loopFrame;
    static bool _refused;

    sealed class Row
    {
        public int Seq, Kind, Verts, Tris, Changed, Owned, Survives, BboxArea; public uint Blend; public bool Sdf, Ui, Kill; public IntPtr Tex; public string Cls, Why, TexName, Cat;
        public List<int> Px; public int Killer = -1;
        public int F0x, F0y, F1x, F1y;          // the footprint: every pixel the draw can touch (a superset; empty = F1x < F0x)
        public int C0x, C0y, C1x, C1y = -1;     // an opaque overwrite's guaranteed coverage (a subset; empty = C1y < C0y)
        public bool KillFull, Cover;            // the coverage is the whole target; the footprint lies under later opaque coverage
        public bool HasFoot => F1x >= F0x && F1y >= F0y;
        public bool HasCov => C1x >= C0x && C1y >= C0y;
    }
    static readonly List<Row> _rows = new();
    static byte[] _prev; static int _w, _h; static int[] _owner, _lastKill, _lastKillPre; static int _postRow = -1;
    static IntPtr _target; static bool _afterMark, _clipOn; static int _cx, _cy, _cw, _ch, _offscreen;
    static StreamWriter _tsv;
    sealed class Acc { public string Name; public int Frames, Draws, Offscreen; public readonly Dictionary<string, int> Cls = new(); public readonly Dictionary<string, int> NoopTex = new(), HiddenTex = new(), Killers = new(), VisSmall = new(); public long ChangedPx, OwnedPx; }
    static readonly List<Acc> _accs = new();

    /// <summary>Boot's frame loop, before the frame (the loop's frame number).</summary>
    public static void BeginLoop(int frame) => _loopFrame = frame;

    /// <summary>RenderCheck.BeginFrame (after Begin's clear, before any draw): start a census frame -- the baseline readback.</summary>
    public static void FrameBegin()
    {
        Live = false;
        if (!On || !At(_loopFrame)) return;
        var g = SceneRenderer.G;
        if (MtRender.Cur != null || g is not (GlBackend or TeeBackend))
        {
            if (!_refused) { _refused = true; Log.Warn($"[k11] census: needs the GL backend with single-threaded rendering (backend {g?.Name}{(MtRender.Cur != null ? ", SHEPHERD_MT_RENDER=1" : "")}) -- off"); }
            return;
        }
        _prev = g.ReadPixels(out _w, out _h); if (_prev == null) return;
        _owner = new int[_w * _h]; Array.Fill(_owner, -1); _lastKill = new int[_w * _h]; Array.Fill(_lastKill, -1);
        _rows.Clear(); _target = IntPtr.Zero; _afterMark = false; _clipOn = false; _offscreen = 0; _postRow = -1; _lastKillPre = null;
        Live = true;
    }

    /// <summary>RenderCheck.Rec, right after each recorded event (the backend already has it).</summary>
    public static void Event(int kind, IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> ix, int nIdx, int x, int y, int w, int h)
    {
        if (!Live) return;
        switch (kind)
        {
            case 1: _clipOn = true; _cx = x; _cy = y; _cw = w; _ch = h; return;
            case 2: _clipOn = false; return;
            case 4: _target = tex; return;
            case 3: if (x != 1) return; _afterMark = true; break;   // the post pass: a row of its own
            case 5: if (_target != IntPtr.Zero) return; break;
            case 0: case 6: case 7: case 8: if (_target != IntPtr.Zero) { _offscreen++; return; } break;   // (8: gpu1's GPU-projected world draw)
            default: return;
        }
        var g = SceneRenderer.G;
        var cur = g.ReadPixels(out _, out _); if (cur == null) { Live = false; return; }
        var a = MemoryMarshal.Cast<byte, uint>(_prev); var b = MemoryMarshal.Cast<byte, uint>(cur);
        var r = new Row { Seq = _rows.Count, Kind = kind, Blend = blend, Sdf = sdf, Ui = _afterMark && kind != 3, Tex = tex, Verts = v.Length, Tris = nIdx / 3, Px = new List<int>() };
        r.Kill = kind == 5 || ((kind == 0 || kind == 6) && (blend == BLENDMODE_NONE || tex == IntPtr.Zero));
        int idx = _rows.Count;
        for (int p = 0; p < a.Length; p++)
            if (a[p] != b[p])
            {
                r.Px.Add(p);
                if (kind != 3) _owner[p] = idx;   // (the post pass transforms the whole picture: it takes no pixel's ownership)
                if (r.Kill) _lastKill[p] = idx;
            }
        r.Changed = r.Px.Count;
        if (kind == 3) { _postRow = idx; _lastKillPre = (int[])_lastKill.Clone(); }   // (kills before the post pass, kept apart: a partial cover after it does not hide what the post pass may spread)
        // the footprint (a superset) and an opaque overwrite's coverage (a subset), in whole pixels
        int tx0 = 0, ty0 = 0, tx1 = _w - 1, ty1 = _h - 1;
        if (_clipOn && kind != 5) { tx0 = Math.Max(0, _cx); ty0 = Math.Max(0, _cy); tx1 = Math.Min(_w - 1, _cx + _cw - 1); ty1 = Math.Min(_h - 1, _cy + _ch - 1); }   // (a clear ignores the clip: GlBackend.ClearTarget)
        r.F0x = 0; r.F0y = 0; r.F1x = _w - 1; r.F1y = _h - 1;   // (a GPU mesh, a GPU-projected draw (world-space vertices), the post pass, a clear: the whole target)
        if (kind is 0 or 6 && v.Length > 0)
        {
            float bx0 = float.MaxValue, by0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue;
            foreach (var q in v) { var p = q.position; bx0 = Math.Min(bx0, p.x); bx1 = Math.Max(bx1, p.x); by0 = Math.Min(by0, p.y); by1 = Math.Max(by1, p.y); }
            if (float.IsFinite(bx0) && float.IsFinite(by0) && float.IsFinite(bx1) && float.IsFinite(by1))
            {
                r.F0x = (int)Math.Max(tx0, Math.Floor(Math.Max(-1e6f, bx0)) - 1); r.F0y = (int)Math.Max(ty0, Math.Floor(Math.Max(-1e6f, by0)) - 1);
                r.F1x = (int)Math.Min(tx1, Math.Ceiling(Math.Min(1e6f, bx1))); r.F1y = (int)Math.Min(ty1, Math.Ceiling(Math.Min(1e6f, by1)));
            }
            else { r.F0x = tx0; r.F0y = ty0; r.F1x = tx1; r.F1y = ty1; }
            if (r.Kill && IsRect(v, ix, out var rx0, out var ry0, out var rx1, out var ry1))
            {   // every pixel square wholly inside the rectangle (its centre is inside: rasterized), clipped
                r.C0x = (int)Math.Max(tx0, Math.Ceiling(Math.Max(-1e6f, rx0))); r.C0y = (int)Math.Max(ty0, Math.Ceiling(Math.Max(-1e6f, ry0)));
                r.C1x = (int)Math.Min(tx1, Math.Floor(Math.Min(1e6f, rx1)) - 1); r.C1y = (int)Math.Min(ty1, Math.Floor(Math.Min(1e6f, ry1)) - 1);
            }
        }
        if (kind == 5) { r.C0x = 0; r.C0y = 0; r.C1x = _w - 1; r.C1y = _h - 1; }
        r.KillFull = r.HasCov && r.C0x == 0 && r.C0y == 0 && r.C1x == _w - 1 && r.C1y == _h - 1;
        // why a draw changed nothing (from its own data)
        if (kind is 0 or 6 && v.Length > 0)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue; bool allA0 = true;
            foreach (var q in v) { var p = q.position; x0 = Math.Min(x0, p.x); x1 = Math.Max(x1, p.x); y0 = Math.Min(y0, p.y); y1 = Math.Max(y1, p.y); if (q.color.a != 0) allA0 = false; }
            float cx0 = 0, cy0 = 0, cx1 = _w, cy1 = _h; if (_clipOn) { cx0 = _cx; cy0 = _cy; cx1 = _cx + _cw; cy1 = _cy + _ch; }
            float ix0 = Math.Max(x0, cx0), iy0 = Math.Max(y0, cy0), ix1 = Math.Min(x1, cx1), iy1 = Math.Min(y1, cy1);
            r.BboxArea = ix1 > ix0 && iy1 > iy0 ? (int)Math.Min(int.MaxValue, (ix1 - ix0) * (iy1 - iy0)) : 0;
            bool blends = blend is BLENDMODE_BLEND or BLENDMODE_ADD or BLENDMODE_BLEND_PREMULTIPLIED or BLENDMODE_ADD_PREMULTIPLIED;
            r.Why = r.BboxArea == 0 ? "offscreen-or-clipped" : allA0 && tex != IntPtr.Zero && blend is BLENDMODE_BLEND or BLENDMODE_ADD ? "alpha0" : allA0 && blends ? "alpha0?" : "texels-or-same";
        }
        else r.Why = kind == 3 ? "post" : kind == 7 ? "gpu-mesh" : kind == 8 ? "gpu-proj" : "clear";
        _rows.Add(r);
        _prev = cur;
    }

    /// <summary>An axis-aligned rectangle drawn as two triangles that share its diagonal (4 corner vertices, 6 indices): the shape whose
    /// coverage is known without rasterizing.</summary>
    static bool IsRect(Span<Vertex> v, Span<int> ix, out float x0, out float y0, out float x1, out float y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (v.Length != 4 || ix.Length != 6) return false;
        float ax = v[0].position.x, ay = v[0].position.y, bx = ax, by = ay;
        foreach (var q in v) { if (q.position.x != ax) bx = q.position.x; if (q.position.y != ay) by = q.position.y; }
        if (bx == ax || by == ay) return false;
        Span<int> corner = stackalloc int[4]; int mask = 0;
        for (int i = 0; i < 4; i++)
        {
            var p = v[i].position; int cx = p.x == ax ? 0 : p.x == bx ? 1 : -1, cy = p.y == ay ? 0 : p.y == by ? 1 : -1;
            if (cx < 0 || cy < 0) return false;
            corner[i] = cy * 2 + cx; mask |= 1 << corner[i];
        }
        if (mask != 15) return false;
        foreach (var k in ix) if (k < 0 || k > 3) return false;
        if (ix[0] == ix[1] || ix[1] == ix[2] || ix[0] == ix[2] || ix[3] == ix[4] || ix[4] == ix[5] || ix[3] == ix[5]) return false;
        int ma = 6 - ix[0] - ix[1] - ix[2], mb = 6 - ix[3] - ix[4] - ix[5];   // the vertex each triangle leaves out
        if ((corner[ma] ^ corner[mb]) != 3) return false;                    // left-out corners diagonal = the triangles share the other diagonal
        x0 = Math.Min(ax, bx); x1 = Math.Max(ax, bx); y0 = Math.Min(ay, by); y1 = Math.Max(ay, by);
        return true;
    }

    /// <summary>The frame's stream is complete (RenderCheck.HashFrame): classify the frame's rows.</summary>
    public static void Stream(int frame, RenderCheck.Rec rec)
    {
        if (!Live) return;
        Live = false;
        var scene = K11Measure.Scene;
        var acc = _accs.LastOrDefault(); if (acc == null || acc.Name != scene) { acc = new Acc { Name = scene }; _accs.Add(acc); }
        acc.Frames++; acc.Offscreen += _offscreen;
        foreach (var r in _rows)   // (names first: a hidden row names its killer, a later row)
        {
            r.TexName = r.Kind == 3 ? "(post)" : r.Kind == 5 ? "(clear)" : r.Tex == IntPtr.Zero ? "(untextured)" : K11Tex.Name(K11Tex.Find(r.Tex, frame));
            r.Cat = r.Kind == 3 ? "post" : r.Kind == 5 ? "clear" : r.Kind == 6 ? "world-lit" : r.Kind == 7 ? "world-mesh" : r.Kind == 8 ? "world-proj" : r.Tex == IntPtr.Zero ? "fill" : r.Ui ? (r.Sdf ? "ui-text" : "ui") : r.Sdf ? "world-text" : "world";
        }
        // the cover rule, from the last row back: a full-target opaque overwrite after a row covers it in any pass; the pass's opaque
        // rectangles cover it only in the same pass (the post pass may spread a pixel to its neighbours)
        int nr = _rows.Count; var fullAfter = new int[nr]; int fa = -1;
        for (int i = nr - 1; i >= 0; i--) { fullAfter[i] = fa; if (_rows[i].KillFull) fa = i; }
        var cov = new bool[_w * _h]; bool anyCov = false;
        for (int i = nr - 1; i >= 0; i--)
        {
            var r = _rows[i];
            r.Cover = r.HasFoot && (fullAfter[i] >= 0 || (anyCov && Covered(cov, r)));
            if (r.Kill && r.HasCov && !r.KillFull) { for (int y = r.C0y; y <= r.C1y; y++) Array.Fill(cov, true, y * _w + r.C0x, r.C1x - r.C0x + 1); anyCov = true; }
            if (i == _postRow) { Array.Clear(cov); anyCov = false; }
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            var kills = _postRow >= 0 && i < _postRow ? _lastKillPre : _lastKill;   // (a pixel's last opaque overwrite in this row's pass)
            int owned = 0, survives = 0; bool allKilled = r.Px.Count > 0; var killers = new Dictionary<int, int>();
            foreach (var p in r.Px)
            {
                if (_owner[p] == i) owned++;
                int k = kills[p] > i ? kills[p] : fullAfter[i];
                if (k > i) killers[k] = (killers.TryGetValue(k, out var n) ? n : 0) + 1; else { allKilled = false; survives++; }
            }
            r.Owned = owned; r.Survives = survives;
            r.Cls = r.Changed == 0 ? "noop" : r.Cover ? "hidden" : allKilled ? "overwritten" : owned == 0 ? "blended-over" : "visible";
            if (r.Cls == "noop" && r.Cover && r.Why is not ("offscreen-or-clipped" or "alpha0")) r.Why = "covered";
            if (r.Cls is "hidden" or "overwritten") r.Killer = killers.Count > 0 ? killers.OrderByDescending(kv => kv.Value).First().Key : fullAfter[i] >= 0 ? fullAfter[i] : CoverKiller(i);
            acc.Draws++; acc.Cls[r.Cls] = (acc.Cls.TryGetValue(r.Cls, out var c) ? c : 0) + 1;
            acc.ChangedPx += r.Changed; acc.OwnedPx += r.Owned;
            string tk = $"{r.Cat}:{r.TexName}";
            if (r.Cls == "noop") { var key = $"{r.Why}|{tk}"; acc.NoopTex[key] = (acc.NoopTex.TryGetValue(key, out var n1) ? n1 : 0) + 1; }
            if (r.Cls == "hidden" && r.Killer >= 0) { acc.HiddenTex[tk] = (acc.HiddenTex.TryGetValue(tk, out var n2) ? n2 : 0) + 1; var kr = _rows[r.Killer]; var kk = $"{kr.Cat}:{kr.TexName}"; acc.Killers[kk] = (acc.Killers.TryGetValue(kk, out var n3) ? n3 : 0) + 1; }
            if (r.Cls == "visible" && r.Owned < 16) acc.VisSmall[tk] = (acc.VisSmall.TryGetValue(tk, out var n4) ? n4 : 0) + 1;
        }
        _tsv ??= new StreamWriter(K11Measure.OutFile("k11-census.tsv"), false);
        if (_tsv.BaseStream.Position == 0) _tsv.WriteLine("frame\tscene\tseq\tkind\tcat\ttexture\tblend\tverts\ttris\tbbox_px\tchanged_px\tsurvives_px\towned_px\tclass\twhy\tkiller_seq");
        foreach (var r in _rows)
            _tsv.WriteLine($"{_loopFrame}\t{scene}\t{r.Seq}\t{r.Kind}\t{r.Cat}\t{r.TexName}\t0x{r.Blend:X}\t{r.Verts}\t{r.Tris}\t{r.BboxArea}\t{r.Changed}\t{r.Survives}\t{r.Owned}\t{r.Cls}\t{(r.Cls == "noop" ? r.Why : "-")}\t{(r.Killer >= 0 ? r.Killer : -1)}");
        _tsv.Flush();
        Log.Info($"[k11] census frame {_loopFrame} '{scene}': {_rows.Count} main-target rows ({string.Join(", ", _rows.GroupBy(r => r.Cls).Select(gr => $"{gr.Key} {gr.Count()}"))}), {_offscreen} off-screen-target draws");
        _rows.Clear(); _prev = null; _owner = null; _lastKill = null; _lastKillPre = null; _postRow = -1;
    }

    static bool Covered(bool[] cov, Row r)
    {
        for (int y = r.F0y; y <= r.F1y; y++) { int o = y * _w; for (int x = r.F0x; x <= r.F1x; x++) if (!cov[o + x]) return false; }
        return true;
    }

    /// <summary>A covered row with no pixel-level killer (its pixels were overwritten with the same values): the first later opaque rectangle
    /// of its pass that overlaps its footprint.</summary>
    static int CoverKiller(int i)
    {
        var r = _rows[i];
        for (int j = i + 1; j < _rows.Count; j++)
        {
            if (_postRow >= 0 && i < _postRow && j > _postRow) break;
            var k = _rows[j];
            if (k.Kill && k.HasCov && k.C0x <= r.F1x && k.C1x >= r.F0x && k.C0y <= r.F1y && k.C1y >= r.F0y) return j;
        }
        return -1;
    }

    public static void After(int frame) { }

    public static void End()
    {
        if (!On) return;
        _tsv?.Dispose(); _tsv = null;
        using var w = new StreamWriter(K11Measure.OutFile("k11-census-sum.tsv"), false);
        w.WriteLine("scene\tframes\tdraws\tnoop\thidden\tblended_over\tvisible\tvisible_lt16px\toffscreen_target_draws\tchanged_px\towned_px\ttop_noop(why|cat:tex:n)\ttop_hidden(cat:tex:n)\ttop_killers(cat:tex:draws_hidden)\toverwritten");
        foreach (var a in _accs)
        {
            int C(string k) => a.Cls.TryGetValue(k, out var n) ? n : 0;
            string Top(Dictionary<string, int> d, int k = 6) => string.Join(" ", d.OrderByDescending(kv => kv.Value).Take(k).Select(kv => $"{kv.Key.Replace(' ', '_')}:{kv.Value}"));
            w.WriteLine($"{a.Name}\t{a.Frames}\t{a.Draws}\t{C("noop")}\t{C("hidden")}\t{C("blended-over")}\t{C("visible")}\t{a.VisSmall.Values.Sum()}\t{a.Offscreen}\t{a.ChangedPx}\t{a.OwnedPx}\t{Top(a.NoopTex)}\t{Top(a.HiddenTex)}\t{Top(a.Killers)}\t{C("overwritten")}");
            Log.Info($"[k11] census '{a.Name}': {a.Frames} frame(s), {a.Draws} draws: noop {C("noop")}, hidden {C("hidden")}, overwritten (this frame only) {C("overwritten")}, blended over {C("blended-over")}, visible {C("visible")} ({a.VisSmall.Values.Sum()} under 16 px); off-screen-target draws {a.Offscreen}; killers {Top(a.Killers, 3)}");
        }
        if (_accs.Count == 0) Log.Info("[k11] census: no frame sampled" + (_refused ? " (needs GL + single-threaded rendering)" : ""));
    }
}
#endif
