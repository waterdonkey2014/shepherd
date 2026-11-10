#if !SHEPHERD_EBOOT
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Render;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #11, track measure: the FRAME CHANGE HEAT MAP (roadmap kit #11, "frame change heat map"; the 'skip pixels that didn't
/// change' idea -- frame-to-frame coherence). Each finished frame is compared with the previous one per 32 x 32 tile of the screen, two ways:
/// <list type="bullet">
/// <item><b>stream</b> (every backend: the Mac null backend, vlx, the device): the tile's PROOF signature -- the ordered list of every
/// main-target event whose screen bounds touch the tile (each draw's texture record + version, blend, sdf, clip and every vertex and index
/// byte; a sampled render target's content as drawn; clears; the post pass), from the frame's recorded draw stream
/// (Render/RenderCheck.Rec). Equal signatures = the same commands drew that tile = what a proof-based image cache, dirty rectangles or the
/// per-tile prefix cache may reuse. Conservative where the stream cannot tell: a texture handle with no engine record (a backend-internal
/// texture) counts as changed every frame; a texture's version counts only once it was seen equal at two analyses in a row (the stream
/// keeps the handle, not the version a draw used -- an update after the draw shows at the next analysis); a backend-internal render target
/// is trusted only when this frame's stream wrote it before the draw sampled it, and a handle an ordinary texture owns now is never read
/// as an old target (GL recycles texture names); a GPU-mesh draw (kind 7), an unknown event kind and kit #8's real-video picture have no
/// screen bounds in the stream and cover every tile (their changer share is an upper bound: the "bounded" column leaves them out). Kit #11
/// integration: gpu1's GPU-projected world draw (kind 8) is bounded by projecting its world vertices with its pass (ProjBounds); gpu2's
/// extra-texture records (kind 9) fold into the kind-7 draw before them.
/// Kit #8's video picture (Runtime/K8Video.cs: the Vita GL host's launcher apps) is drawn straight to the backend, never into the stream:
/// a frame in which it drew counts as changed on every tile. The post pass's own parameters are not in the stream (see the pixel
/// check).</item>
/// <item><b>pixel</b> (GL: the Mac and the device): the tile's final RGBA bytes. Equal = the real ceiling of any reuse. A tile the stream
/// calls unchanged but whose pixels changed is a PROOF VIOLATION (the reuse would have shown a wrong pixel: the image a per-tile cache
/// would show -- last frame's pixels where the proof says unchanged -- compared with the real frame, the roadmap's "0 differing pixels"
/// check, before the cache exists); stream-changed but pixel-unchanged is a MISS (reuse the proof cannot see).</item>
/// </list>
/// Logged: per frame (k11-heat.tsv), per scene (engine.log [k11] heat lines + k11-heat-scenes.tsv): % of tiles unchanged, how long tiles
/// stay unchanged (closed runs by length), the global draw prefix (the draw-prefix cache's hit point), the per-tile reusable prefix, and
/// what keeps tiles changing (the categories and textures of the draws new this frame over each changed tile), and with pixels a hash of the
/// whole image per frame (two runs' images compared frame by frame: <c>k11_report.py heat-diff</c>, a reuse cache's "0 differing pixels"
/// gate). GL: an on-screen overlay
/// (Render/K7Overlay.cs draws it after the UI; red = changed, magenta = proof violation, yellow = miss; it shows the previous frame's
/// result). Measurement only: it never changes what is drawn (the overlay aside, which is a debug view).
/// Switch: <c>SHEPHERD_K11_HEAT=stream|pixel[,overlay][,pxevery=N][,png=&lt;frames&gt;]</c> (pixel = stream + pixel); unset = off.</summary>
[ProfileOnly]
public static class K11Heat
{
    const int T = 32;
    static readonly string Spec = K11Measure.Master ? (Environment.GetEnvironmentVariable("SHEPHERD_K11_HEAT") ?? "").Trim() : "";
    static readonly string[] Parts = Spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    /// <summary>1 = stream, 3 = stream + pixel.</summary>
    public static readonly int Mode = Parts.Contains("pixel") || Parts.Contains("both") ? 3 : Parts.Contains("stream") || Parts.Contains("1") || Parts.Contains("overlay") ? 1 : 0;
    public static bool On => Mode != 0;
    public static bool PixelMode => (Mode & 2) != 0;
    public static readonly bool Overlay = Mode != 0 && Parts.Contains("overlay");
    static readonly int PxEvery = Math.Max(1, Opt("pxevery", 1));
    static readonly Func<int, bool> PngAt = K11Measure.Frames(Parts.FirstOrDefault(p => p.StartsWith("png="))?[4..]);
    static int Opt(string k, int d) { var p = Parts.FirstOrDefault(x => x.StartsWith(k + "=")); return p != null && int.TryParse(p[(k.Length + 1)..], out var v) ? v : d; }
    public static string Describe() => !On ? "off" : (PixelMode ? "stream+pixel" : "stream") + (Overlay ? "+overlay" : "") + (PxEvery > 1 ? $" pxevery={PxEvery}" : "");
    /// <summary>The frames whose pixels are read: every frame, or with pxevery=N the pairs (N-1, 0 mod N) -- also the overlay's readback.</summary>
    static bool WantPx(int frame) => PixelMode && (PxEvery == 1 || frame % PxEvery == 0 || frame % PxEvery == PxEvery - 1);

    // ---------------------------------------------------------------- the frame's stream (MT: from the render thread)
    static readonly object _lk = new();
    static readonly List<(int frame, RenderCheck.Rec rec)> _pending = new();
    public static void Stream(int frame, RenderCheck.Rec rec) { lock (_lk) _pending.Add((frame, rec)); }

    // categories of what keeps tiles changing
    public enum Cat : byte { WorldSprite, WorldLit, WorldMesh, WorldText, Fill, Ui, UiText, SamplesTarget, Post, Clear, UnknownTex, Other, Video, Count }
    /// <summary>Categories whose draws have no screen bounds in the stream (drawn as whole-screen: their changer share is an upper bound).</summary>
    static bool Unbounded(Cat c) => c is Cat.WorldMesh or Cat.Other or Cat.Video;
    struct D { public ulong H; public Cat C; public int X0, Y0, X1, Y1; public IntPtr Tex; public bool New, Ub; }

    static int _w, _h, _tw, _th, _n;
    static List<ulong>[] _tl, _tlPrev;
    static ulong[] _sig, _sigPrev, _sigB, _sigBPrev, _hb; static int[] _nb;   // (B = the bounded draws only)
    static bool _havePrev;
    static readonly List<D> _draws = new();
    static List<ulong> _dseq = new(), _dseqPrev = new();
    static HashSet<ulong> _set = new(), _setPrev = new();
    static readonly Dictionary<IntPtr, (ulong h, int seq)> _targets = new();   // render target -> content hash + the analysis that last wrote it (persists across frames)
    static int _seq;   // analyses so far (one per frame's stream; under SHEPHERD_MT_RENDER=1 an After can analyse 0 or 2)
    static readonly Dictionary<int, (int seen, int ver, int run)> _verRun = new();   // texture record -> the last analysis it was seen at, its version then, analyses in a row at that version
    static bool[] _chS, _chP;
    static int[] _runS, _runP;
    static ulong[] _px, _pxPrev; static int _pxFrame = -10, _pxPrevFrame = -10;
    static byte[] _framePx; static int _framePxFrame = -1;
    static int _lastFrame;
    static StreamWriter _tsv;

    sealed class Acc
    {
        public string Name; public int Frames, PxFrames, VideoFrames; public long UnS, UnB, UnP, Viol, Miss, AllUnS, AllUnP, AllChS, ViolFrames, MainDraws, PrefixDraws, TileDraws, TilePrefix, NoNewTiles, UnknownDraws;
        public readonly long[] HistS = new long[16], HistP = new long[16]; public readonly long[] CatTiles = new long[(int)Cat.Count];
        public readonly Dictionary<string, long> TexTiles = new();
        public double PrefixShareSum;
    }
    static readonly List<Acc> _accs = new(); static Acc _acc;

    static void Init(IRenderBackend g)
    {
        _w = g.W; _h = g.H; _tw = (_w + T - 1) / T; _th = (_h + T - 1) / T; _n = _tw * _th;
        _tl = new List<ulong>[_n]; _tlPrev = new List<ulong>[_n];
        for (int i = 0; i < _n; i++) { _tl[i] = new(); _tlPrev[i] = new(); }
        _sig = new ulong[_n]; _sigPrev = new ulong[_n]; _sigB = new ulong[_n]; _sigBPrev = new ulong[_n]; _hb = new ulong[_n]; _nb = new int[_n]; _chS = new bool[_n]; _chP = new bool[_n]; _runS = new int[_n]; _runP = new int[_n];
        _px = new ulong[_n]; _pxPrev = new ulong[_n];
        _tsv = new StreamWriter(K11Measure.OutFile("k11-heat.tsv"), false) { AutoFlush = false };
        _tsv.WriteLine($"# k11 heat map: {_tw} x {_th} tiles of {T} px over {_w} x {_h}; mode {Describe()}");
        _tsv.WriteLine("frame\trframe\tscene\tmain_draws\ttiles\tchanged_stream\tchanged_pixel\tviolations\tmisses\tglobal_prefix\ttile_draws_changed\ttile_prefix_changed\tunknown_draws\ttop_changer\tchanged_stream_bounded\tvideo\timage_hash");
        Log.Info($"[k11] heat map on: {_tw} x {_th} tiles of {T} px ({_w} x {_h}), {Describe()}");
#if !SHEPHERD_NO_K8
        if (Runtime.K8Video.On) Log.Info($"[k11] heat map: kit #8's real video is on -- its picture is not in the draw stream; a frame it drew counts as changed on every tile{(_videoDrawn == null ? " (its draw counter was not found: every frame counts as changed while it is on)" : "")}");
#endif
    }

    // ---------------------------------------------------------------- per frame
    public static void After(int frame, IRenderBackend g)
    {
        if (g == null) return;
        if (_tl == null) Init(g);
        _lastFrame = frame;
        List<(int, RenderCheck.Rec)> list;
        bool wantPx = WantPx(frame);
        if (VideoDrew()) _videoPending = true;
        byte[] px = null;
        if (wantPx)
        {
            if (_framePxFrame == frame) px = _framePx;   // overlay mode: read before the overlay's quads
            else px = g.ReadPixels(out _, out _);       // (a sync point under SHEPHERD_MT_RENDER=1: the frame's stream is complete after it)
        }
        _framePx = null;
        lock (_lk) { list = new(_pending); _pending.Clear(); }
        var name = K11Measure.Scene;
        if (_acc == null || _acc.Name != name) { _acc = new Acc { Name = name }; _accs.Add(_acc); }
        bool vid = _videoPending; if (list.Count > 0) _videoPending = false;   // (MT: every stream this After analyses gets it -- conservative)
        for (int k = 0; k < list.Count; k++)
        {
            var (rf, rec) = list[k];
            Analyse(frame, rf, rec, k == list.Count - 1 ? px : null, vid);
        }
    }

    // kit #8's real video (Runtime/K8Video.cs, the Vita GL host only): SceneRenderer hands its picture straight to the backend, so the stream
    // never holds it. Its count of frames drawn (a private field of that file: read here, never written) says whether a picture was drawn
    // since the last After; if the field cannot be read, every frame counts as drawn while the video is on (conservative).
#if !SHEPHERD_NO_K8
    static readonly System.Reflection.FieldInfo _videoDrawn = Runtime.K8Video.On
        ? typeof(Runtime.K8Video).GetField("_drawn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) : null;
    static int _videoSeen;
    static bool VideoDrew()
    {
        if (!Runtime.K8Video.On) return false;
        int n;
        try { if (_videoDrawn == null || _videoDrawn.GetValue(null) is not int v) return true; n = v; } catch (Exception) { return true; }
        bool d = n != _videoSeen; _videoSeen = n; return d;
    }
#else
    static bool VideoDrew() => false;
#endif
    static bool _videoPending;

    /// <summary>Overlay mode (K7Overlay.Draw, after the UI, before this frame's stream is complete): the pixels read now (without the overlay),
    /// then the tiles of the last analysed frame drawn over the image.</summary>
    public static void DrawOverlay(IRenderBackend g)
    {
        if (WantPx(_lastFrame + 1)) { _framePx = g.ReadPixels(out _, out _); _framePxFrame = _lastFrame + 1; }   // (only on the frames After reads: pxevery holds here too)
        if (_tl == null || !_havePrev) return;
        if (_white == IntPtr.Zero) _white = g.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 }, false, false, true);
        int nq = 0;
        for (int t = 0; t < _n; t++)
        {
            bool s = _chS[t], p = PixelMode ? _chP[t] : s;
            if (!s && !p) continue;
            var c = !s && p ? new FColor { r = 1, g = 0, b = 1, a = 0.6f } : s && !p ? new FColor { r = 1, g = 1, b = 0, a = 0.3f } : new FColor { r = 1, g = 0.1f, b = 0.1f, a = 0.35f };
            int x = (t % _tw) * T, y = (t / _tw) * T;
            Quad(ref nq, x + 1, y + 1, Math.Min(_w, x + T) - 1, Math.Min(_h, y + T) - 1, c);
        }
        if (nq > 0) g.Draw(_white, BLENDMODE_BLEND, false, _ov.AsSpan(0, nq * 4), _oi.AsSpan(0, nq * 6));
    }
    static IntPtr _white;
    static Vertex[] _ov = new Vertex[4 * 600]; static int[] _oi = new int[6 * 600];
    static void Quad(ref int nq, float x0, float y0, float x1, float y1, FColor c)
    {
        if (nq * 4 + 4 > _ov.Length) return;
        int o = nq * 4;
        _ov[o] = new Vertex { position = new FPoint { x = x0, y = y0 }, color = c, tex_coord = new FPoint { x = 0.5f, y = 0.5f } };
        _ov[o + 1] = new Vertex { position = new FPoint { x = x1, y = y0 }, color = c, tex_coord = new FPoint { x = 0.5f, y = 0.5f } };
        _ov[o + 2] = new Vertex { position = new FPoint { x = x1, y = y1 }, color = c, tex_coord = new FPoint { x = 0.5f, y = 0.5f } };
        _ov[o + 3] = new Vertex { position = new FPoint { x = x0, y = y1 }, color = c, tex_coord = new FPoint { x = 0.5f, y = 0.5f } };
        int j = nq * 6; _oi[j] = o; _oi[j + 1] = o + 1; _oi[j + 2] = o + 2; _oi[j + 3] = o; _oi[j + 4] = o + 2; _oi[j + 5] = o + 3;
        nq++;
    }

    /// <summary>A sampled texture's part of the proof. The engine's record first (a GL texture name is recycled once freed: a handle an ordinary
    /// texture owns now is never read as the render target it once was); a render-texture record carries its target's content as drawn; a
    /// handle no record owns is a backend-internal target only if this stream wrote it before this draw, else unknown (changed every frame).</summary>
    static ulong TexKey(IntPtr tex, int frame, out bool unknown, out bool target)
    {
        unknown = target = false;
        if (tex == IntPtr.Zero) return 0;
        var t = K11Tex.Find(tex, frame);
        if (t != null)
        {
            if (tex != t.RtTarget) _targets.Remove(tex);   // (an old target's content under a recycled name: gone)
            ulong k = VerKey(t);
            if (t.IsRenderTexture) { target = true; if (t.RtTarget != IntPtr.Zero && _targets.TryGetValue(t.RtTarget, out var rh)) k = K11Measure.Mix(k, rh.h); }
            return k;
        }
        if (_targets.TryGetValue(tex, out var th) && th.seq == _seq) { target = true; return K11Measure.Mix(0x7A, th.h); }
        unknown = true; return K11Measure.Mix(0xDEAD, (ulong)_seq);   // unknown: changed at every analysis (conservative)
    }

    /// <summary>A texture record's id + version, counted as unchanged only once the version was seen equal at two analyses in a row: the stream
    /// keeps the handle, not the version the draw used, so a version read at analysis N bounds the draws of frames N and N+1 -- equal at
    /// N-1, N and N+1 = the same texels drawn in N and N+1. Otherwise the key is unique to this analysis (the tile counts as changed).</summary>
    static ulong VerKey(NTexture t)
    {
        ulong k = K11Measure.Mix(K11Measure.Mix(0x7E, (ulong)(uint)t.InstanceId), (ulong)(uint)t.Version);
        _verRun.TryGetValue(t.InstanceId, out var s);
        if (s.seen != _seq || s.run == 0)
        {
            int run = s.run > 0 && s.seen == _seq - 1 && s.ver == t.Version ? s.run + 1 : 1;
            _verRun[t.InstanceId] = s = (_seq, t.Version, run);
        }
        return s.run >= 2 ? k : K11Measure.Mix(k, 0xF00D000000000000UL ^ (ulong)(uint)_seq);
    }

    static unsafe ulong DrawHash(int kind, ulong tk, uint blend, bool sdf, bool clipOn, int cx, int cy, int cw, int ch, Vertex[] v, int[] idx)
    {
        ulong h = K11Measure.Mix((ulong)kind, tk); h = K11Measure.Mix(h, blend | (sdf ? 0x100000000UL : 0));
        if (clipOn) h = K11Measure.Mix(K11Measure.Mix(h, (ulong)(uint)cx | ((ulong)(uint)cy << 32)), (ulong)(uint)cw | ((ulong)(uint)ch << 32));
        if (v != null) h = K11Measure.Hash(MemoryMarshal.AsBytes(v.AsSpan()), h);
        if (idx != null) h = K11Measure.Hash(MemoryMarshal.AsBytes(idx.AsSpan()), h);
        return h;
    }

    /// <summary>Kit #11 integration (measure's ask for gpu1's stream kind 8, RenderCheck.Rec.Proj): a GPU-projected world draw's screen bounds,
    /// computed as Shaders/k11proj.vert does -- clip x / y / w from the view-projection's rows 0, 1 and 3, w clamped at 1e-5, the camera's pixel
    /// rect and the target height. The record: v = the world x / y, bits = the indices (ni), each vertex's world z, the 16 matrix entries
    /// (Matrix4x4's column order), then X, Y, W, H, Gh. False (= the whole screen, the safe answer) when anything is missing or not finite.</summary>
    static bool ProjBounds(Vertex[] v, int[] b, int ni, out float x0, out float y0, out float x1, out float y1)
    {
        x0 = y0 = float.MaxValue; x1 = y1 = float.MinValue;
        if (v == null || b == null || v.Length == 0 || ni < 0 || b.Length < ni + v.Length + 21) return false;
        int mz = ni + v.Length; float F(int i) => BitConverter.Int32BitsToSingle(b[i]);
        float M(int r, int c) => F(mz + c * 4 + r);
        float px = F(mz + 16), py = F(mz + 17), pw = F(mz + 18), ph = F(mz + 19), gh = F(mz + 20);
        for (int i = 0; i < v.Length; i++)
        {
            float wx = v[i].position.x, wy = v[i].position.y, wz = F(ni + i);
            float cx = M(0, 0) * wx + M(0, 1) * wy + M(0, 2) * wz + M(0, 3);
            float cy = M(1, 0) * wx + M(1, 1) * wy + M(1, 2) * wz + M(1, 3);
            float cw = M(3, 0) * wx + M(3, 1) * wy + M(3, 2) * wz + M(3, 3);
            if (cw <= 1e-5f) cw = 1e-5f;
            float sx = px + (cx / cw * 0.5f + 0.5f) * pw, sy = gh - (py + (cy / cw * 0.5f + 0.5f) * ph);
            if (!float.IsFinite(sx) || !float.IsFinite(sy)) return false;
            if (sx < x0) x0 = sx; if (sx > x1) x1 = sx; if (sy < y0) y0 = sy; if (sy > y1) y1 = sy;
        }
        return true;
    }

    static void Analyse(int frame, int rframe, RenderCheck.Rec rec, byte[] px, bool vid)
    {
        _seq++;
        if ((_seq & 1023) == 0) Prune();
        // 1. the frame's main-target events with their tiles
        _draws.Clear();
        bool vidAdded = false;
        var vd = new D { H = K11Measure.Mix(0x71DE0, (ulong)(uint)_seq), C = Cat.Video, X0 = 0, Y0 = 0, X1 = _tw - 1, Y1 = _th - 1, Ub = true };   // (kit #8's picture: drawn after EndWorldPass, before the UI mark)
        IntPtr target = IntPtr.Zero; bool clipOn = false, afterMark = false; int cx = 0, cy = 0, cw = 0, ch = 0, unknownDraws = 0;
        foreach (var e in rec.E)
        {
            switch (e.kind)
            {
                case 99: continue;
                case 1: clipOn = true; cx = e.x; cy = e.y; cw = e.w; ch = e.h; continue;
                case 2: clipOn = false; continue;
                case 4: target = e.tex; continue;
                case 3:
                    if (e.x == 1)
                    {
                        if (vid && !vidAdded) { _draws.Add(vd); vidAdded = true; }
                        afterMark = true; _draws.Add(new D { H = 0x9057, C = Cat.Post, X0 = 0, Y0 = 0, X1 = _tw - 1, Y1 = _th - 1 });
                    }
                    continue;
                case 5:
                {
                    ulong hc = K11Measure.Mix(K11Measure.Mix(5, (ulong)(uint)e.x | ((ulong)(uint)e.y << 32)), (ulong)(uint)e.w | ((ulong)(uint)e.h << 32));
                    if (target != IntPtr.Zero) { _targets[target] = (hc, _seq); continue; }
                    _draws.Add(new D { H = hc, C = Cat.Clear, X0 = 0, Y0 = 0, X1 = _tw - 1, Y1 = _th - 1 }); continue;
                }
                case 9:   // kit #11 integration (gpu2's DrawGpuMeshTex): its extra textures belong to the kind-7 draw just before -- folded into that draw's signature
                {
                    ulong tk9 = K11Measure.Mix(TexKey(e.tex, frame, out _, out _), (ulong)e.blend | ((ulong)(uint)e.x << 32));
                    if (target != IntPtr.Zero) { _targets[target] = (K11Measure.Mix(_targets.TryGetValue(target, out var o9) ? o9.h : 0, tk9), _seq); continue; }
                    if (_draws.Count > 0 && _draws[^1].C == Cat.WorldMesh) { var l9 = _draws[^1]; l9.H = K11Measure.Mix(l9.H, tk9); _draws[^1] = l9; continue; }
                    _draws.Add(new D { H = K11Measure.Mix(9, tk9), C = Cat.Other, X0 = 0, Y0 = 0, X1 = _tw - 1, Y1 = _th - 1, Ub = true }); continue;
                }
                case 0: case 6: case 7: case 8:   // (kind 8 = gpu1's GPU-projected world draw: bounds from its world vertices and pass, ProjBounds)
                {
                    ulong tk = TexKey(e.tex, frame, out bool unk, out bool samplesTarget);
                    ulong h = e.kind == 7
                        ? K11Measure.Mix(DrawHash(7, tk, e.blend, false, clipOn, cx, cy, cw, ch, null, e.idx), K11Measure.Mix(K11Measure.Mix((ulong)(uint)e.x, (ulong)(uint)e.y), K11Measure.Mix((ulong)(uint)e.w, (ulong)(uint)e.h)))
                        : DrawHash(e.kind, tk, e.blend, e.sdf, clipOn, cx, cy, cw, ch, e.v, e.idx);
                    if (target != IntPtr.Zero) { _targets[target] = (K11Measure.Mix(_targets.TryGetValue(target, out var old) ? old.h : 0, h), _seq); continue; }
                    if (unk) unknownDraws++;
                    Cat c = unk ? Cat.UnknownTex : samplesTarget ? Cat.SamplesTarget : e.kind == 6 ? Cat.WorldLit : e.kind == 7 ? Cat.WorldMesh
                        : e.tex == IntPtr.Zero ? Cat.Fill : afterMark ? (e.sdf ? Cat.UiText : Cat.Ui) : e.sdf ? Cat.WorldText : Cat.WorldSprite;
                    var d = new D { H = h, C = c, Tex = e.tex, Ub = e.kind == 7 };   // (a GPU mesh's vertices are not in the stream: whole screen)
                    // the draw's screen bounds (whole pixels, one pixel of filter / raster reach), clipped
                    float x0 = 0, y0 = 0, x1 = _w, y1 = _h;
                    if (e.kind == 8) { if (!ProjBounds(e.v, e.idx, e.x, out x0, out y0, out x1, out y1)) { x0 = 0; y0 = 0; x1 = _w; y1 = _h; } }
                    else if (e.kind != 7 && e.v != null && e.v.Length > 0)
                    {
                        x0 = y0 = float.MaxValue; x1 = y1 = float.MinValue;
                        foreach (var q in e.v) { var p = q.position; if (p.x < x0) x0 = p.x; if (p.x > x1) x1 = p.x; if (p.y < y0) y0 = p.y; if (p.y > y1) y1 = p.y; }
                        if (float.IsNaN(x0) || float.IsNaN(y0) || float.IsNaN(x1) || float.IsNaN(y1)) { x0 = 0; y0 = 0; x1 = _w; y1 = _h; }
                    }
                    int ix0 = (int)Math.Max(0, Math.Floor(Math.Max(-1e6f, x0)) - 1), iy0 = (int)Math.Max(0, Math.Floor(Math.Max(-1e6f, y0)) - 1);
                    int ix1 = (int)Math.Min(_w - 1, Math.Ceiling(Math.Min(1e6f, x1)) + 1), iy1 = (int)Math.Min(_h - 1, Math.Ceiling(Math.Min(1e6f, y1)) + 1);
                    if (clipOn) { ix0 = Math.Max(ix0, cx); iy0 = Math.Max(iy0, cy); ix1 = Math.Min(ix1, cx + cw - 1); iy1 = Math.Min(iy1, cy + ch - 1); }
                    if (ix1 < ix0 || iy1 < iy0) { d.X0 = 0; d.X1 = -1; d.Y0 = 0; d.Y1 = -1; }
                    else { d.X0 = ix0 / T; d.Y0 = iy0 / T; d.X1 = ix1 / T; d.Y1 = iy1 / T; }
                    _draws.Add(d); continue;
                }
                default:
                    _draws.Add(new D { H = K11Measure.Mix((ulong)e.kind, (ulong)(uint)_seq), C = Cat.Other, X0 = 0, Y0 = 0, X1 = _tw - 1, Y1 = _th - 1, Ub = true }); continue;
            }
        }
        if (vid && !vidAdded) _draws.Add(vd);
        // 2. per-tile lists and signatures; the global sequence
        for (int i = 0; i < _n; i++) { _tl[i].Clear(); _hb[i] = 0x52; _nb[i] = 0; }
        _dseq.Clear(); _set.Clear();
        for (int k = 0; k < _draws.Count; k++)
        {
            var d = _draws[k];
            _dseq.Add(d.H); _set.Add(d.H);
            d.New = !_setPrev.Contains(d.H); _draws[k] = d;
            for (int ty = d.Y0; ty <= d.Y1; ty++) for (int tx = d.X0; tx <= d.X1; tx++) { int t = ty * _tw + tx; _tl[t].Add(d.H); if (!d.Ub) { _hb[t] = K11Measure.Mix(_hb[t], d.H); _nb[t]++; } }
        }
        for (int i = 0; i < _n; i++) { ulong h = 0x51; foreach (var x in _tl[i]) h = K11Measure.Mix(h, x); _sig[i] = K11Measure.Mix(h, (ulong)_tl[i].Count); _sigB[i] = K11Measure.Mix(_hb[i], (ulong)_nb[i]); }
        // 3. pixels
        bool pxNow = false; ulong imageHash = 0;
        if (px != null && px.Length >= _w * _h * 4)
        {
            for (int ty = 0; ty < _th; ty++)
                for (int tx = 0; tx < _tw; tx++)
                {
                    ulong h = 0xCBF29CE484222325UL; int x0 = tx * T, x1 = Math.Min(_w, x0 + T);
                    for (int y = ty * T; y < Math.Min(_h, ty * T + T); y++) h = K11Measure.Hash(px.AsSpan((y * _w + x0) * 4, (x1 - x0) * 4), h);
                    _px[ty * _tw + tx] = h;
                }
            pxNow = true; _pxPrevFrame = _pxFrame; _pxFrame = rframe;   // (the stream's frame: under SHEPHERD_MT_RENDER=1 the pixels belong to the last stream analysed)
            imageHash = 0x1A6E; for (int t = 0; t < _n; t++) imageHash = K11Measure.Mix(imageHash, _px[t]);
            if (PngAt(frame)) Png(frame, px);
        }
        bool pxCompare = pxNow && _pxPrevFrame == rframe - 1;
        // 4. compare with the previous frame
        int chS = 0, chB = 0, chP = 0, viol = 0, miss = 0, gprefix = 0, tileDraws = 0, tilePrefix = 0, noNew = 0;
        var catTiles = new int[(int)Cat.Count];
        if (_havePrev)
        {
            int m = Math.Min(_dseq.Count, _dseqPrev.Count); while (gprefix < m && _dseq[gprefix] == _dseqPrev[gprefix]) gprefix++;
            for (int t = 0; t < _n; t++)
            {
                bool s = _sig[t] != _sigPrev[t]; _chS[t] = s; if (_sigB[t] != _sigBPrev[t]) chB++;
                bool p = pxCompare ? _px[t] != _pxPrev[t] : s; _chP[t] = p;
                if (s) chS++;
                if (pxCompare) { if (p) chP++; if (!s && p) viol++; if (s && !p) miss++; }
                if (s)
                {
                    var a = _tl[t]; var b = _tlPrev[t]; int pf = 0, mm = Math.Min(a.Count, b.Count); while (pf < mm && a[pf] == b[pf]) pf++;
                    tileDraws += a.Count; tilePrefix += pf;
                    _runS[t] = Close(_acc.HistS, _runS[t]);
                }
                else _runS[t]++;
                if (pxCompare) { if (p) _runP[t] = Close(_acc.HistP, _runP[t]); else _runP[t]++; }
            }
            // what keeps tiles changing: the draws new this frame over each changed tile
            var covered = new bool[_n];
            foreach (var d in _draws)
            {
                if (!d.New) continue;
                int n = 0;
                for (int ty = d.Y0; ty <= d.Y1; ty++) for (int tx = d.X0; tx <= d.X1; tx++) { int t = ty * _tw + tx; if (_chS[t]) { n++; covered[t] = true; } }
                if (n == 0) continue;
                catTiles[(int)d.C] += n;
                if (d.Tex != IntPtr.Zero) { var key = $"{d.C}:{K11Tex.Name(K11Tex.Find(d.Tex, frame))}"; _acc.TexTiles[key] = (_acc.TexTiles.TryGetValue(key, out var o) ? o : 0) + n; }
            }
            for (int t = 0; t < _n; t++) if (_chS[t] && !covered[t]) noNew++;
            // per scene
            var a2 = _acc; a2.Frames++; a2.UnS += _n - chS; a2.UnB += _n - chB; if (chS == 0) a2.AllUnS++; if (chS == _n) a2.AllChS++; if (vid) a2.VideoFrames++;
            if (pxCompare) { a2.PxFrames++; a2.UnP += _n - chP; if (chP == 0) a2.AllUnP++; a2.Viol += viol; a2.Miss += miss; if (viol > 0) a2.ViolFrames++; }
            a2.MainDraws += _dseq.Count; a2.PrefixDraws += gprefix; a2.PrefixShareSum += _dseq.Count > 0 ? (double)gprefix / _dseq.Count : 1;
            a2.TileDraws += tileDraws; a2.TilePrefix += tilePrefix; a2.NoNewTiles += noNew; a2.UnknownDraws += unknownDraws;
            for (int c = 0; c < catTiles.Length; c++) a2.CatTiles[c] += catTiles[c];
            int top = 0; for (int c = 1; c < catTiles.Length; c++) if (catTiles[c] > catTiles[top]) top = c;
            _tsv.WriteLine($"{frame}\t{rframe}\t{_acc.Name}\t{_dseq.Count}\t{_n}\t{chS}\t{(pxCompare ? chP : -1)}\t{(pxCompare ? viol : -1)}\t{(pxCompare ? miss : -1)}\t{gprefix}\t{tileDraws}\t{tilePrefix}\t{unknownDraws}\t{(chS == 0 ? "-" : catTiles[top] > 0 ? ((Cat)top).ToString() : noNew > 0 ? "removed/reordered" : "-")}\t{chB}\t{(vid ? 1 : 0)}\t{(pxNow ? imageHash.ToString("x16") : "-")}");
        }
        else { Array.Fill(_chS, true); Array.Fill(_chP, true); }
        // 5. this frame becomes the previous one
        (_tl, _tlPrev) = (_tlPrev, _tl); (_sig, _sigPrev) = (_sigPrev, _sig); (_sigB, _sigBPrev) = (_sigBPrev, _sigB); (_dseq, _dseqPrev) = (_dseqPrev, _dseq); (_set, _setPrev) = (_setPrev, _set);
        // (the lists were swapped: _tlPrev now holds this frame's)
        if (pxNow) (_px, _pxPrev) = (_pxPrev, _px);
        _havePrev = true;
    }

    /// <summary>Drop what can no longer matter: version runs not seen at the last two analyses (they restart anyway), targets not written for
    /// 600 analyses (a record-owned one then loses its content key once: one more "changed" frame, conservative).</summary>
    static void Prune()
    {
        foreach (var k in _verRun.Where(kv => kv.Value.seen < _seq - 2).Select(kv => kv.Key).ToList()) _verRun.Remove(k);
        foreach (var k in _targets.Where(kv => kv.Value.seq < _seq - 600).Select(kv => kv.Key).ToList()) _targets.Remove(k);
    }

    static int Close(long[] hist, int run)
    {
        if (run > 0) { int b = 0; while ((1 << (b + 1)) <= run && b < hist.Length - 1) b++; hist[b]++; }
        return 0;
    }

    static void Png(int frame, byte[] px)
    {
        try
        {
            var img = (byte[])px.Clone();
            for (int t = 0; t < _n; t++)
            {
                bool s = _chS[t], p = _chP[t]; if (!s && !p) continue;
                byte r = 255, g = (byte)(!s && p ? 0 : s && !p ? 255 : 30), b = (byte)(!s && p ? 255 : 30);
                int x0 = (t % _tw) * T, y0 = (t / _tw) * T;
                for (int y = y0; y < Math.Min(_h, y0 + T); y++) for (int x = x0; x < Math.Min(_w, x0 + T); x++) { int o = (y * _w + x) * 4; img[o] = (byte)((img[o] + r) / 2); img[o + 1] = (byte)((img[o + 1] + g) / 2); img[o + 2] = (byte)((img[o + 2] + b) / 2); }
            }
            Render.Png.Write(K11Measure.OutFile($"k11-heat-f{frame:D5}.png"), _w, _h, img);
        }
        catch (Exception e) { Log.Warn($"[k11] heat png f{frame}: {e.Message}"); }
    }

    // ---------------------------------------------------------------- the end
    public static void End()
    {
        if (_tl == null) return;
        if (_acc != null) for (int t = 0; t < _n; t++) { _runS[t] = Close(_acc.HistS, _runS[t]); _runP[t] = Close(_acc.HistP, _runP[t]); }   // (open runs counted at their length so far)
        _tsv?.Flush(); _tsv?.Dispose(); _tsv = null;
        using var w = new StreamWriter(K11Measure.OutFile("k11-heat-scenes.tsv"), false);
        w.WriteLine("scene\tframes\tpx_frames\ttiles\tunchanged_stream_pct\tunchanged_pixel_pct\tframes_all_unchanged_stream\tframes_all_unchanged_pixel\tframes_all_changed_stream\tviolation_tiles\tviolation_frames\tmiss_tiles\tglobal_prefix_pct\ttile_prefix_pct\tno_new_draw_tiles\tunknown_draws\truns_stream(1,2,4,8..)\truns_pixel(1,2,4,8..)\tchangers(cat:tiles)\ttop_textures(cat:name:tiles)\tunchanged_stream_bounded_pct\tvideo_frames");
        foreach (var a in _accs)
        {
            if (a.Frames == 0) continue;
            double us = 100.0 * a.UnS / ((double)a.Frames * _n), up = a.PxFrames > 0 ? 100.0 * a.UnP / ((double)a.PxFrames * _n) : -1;
            double gp = 100.0 * a.PrefixShareSum / a.Frames, tp = a.TileDraws > 0 ? 100.0 * a.TilePrefix / a.TileDraws : 0;
            string hs = string.Join(",", a.HistS.Reverse().SkipWhile(x => x == 0).Reverse()), hp = string.Join(",", a.HistP.Reverse().SkipWhile(x => x == 0).Reverse());
            double ub = 100.0 * a.UnB / ((double)a.Frames * _n);
            string cats = string.Join(" ", Enumerable.Range(0, (int)Cat.Count).Where(c => a.CatTiles[c] > 0).OrderByDescending(c => a.CatTiles[c]).Select(c => $"{(Cat)c}{(Unbounded((Cat)c) ? "*" : "")}:{a.CatTiles[c]}"));
            string texs = string.Join(" ", a.TexTiles.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key.Replace(' ', '_')}:{kv.Value}"));
            w.WriteLine($"{a.Name}\t{a.Frames}\t{a.PxFrames}\t{_n}\t{us:F2}\t{(up < 0 ? "-" : up.ToString("F2"))}\t{a.AllUnS}\t{(a.PxFrames > 0 ? a.AllUnP.ToString() : "-")}\t{a.AllChS}\t{(a.PxFrames > 0 ? a.Viol.ToString() : "-")}\t{(a.PxFrames > 0 ? a.ViolFrames.ToString() : "-")}\t{(a.PxFrames > 0 ? a.Miss.ToString() : "-")}\t{gp:F1}\t{tp:F1}\t{a.NoNewTiles}\t{a.UnknownDraws}\t{hs}\t{hp}\t{cats}\t{texs}\t{ub:F2}\t{a.VideoFrames}");
            Log.Info($"[k11] heat '{a.Name}': {a.Frames} frames, tiles unchanged stream {us:F1}% (bounded draws only {ub:F1}%)" + (a.PxFrames > 0 ? $" pixel {up:F1}% (proof violations {a.Viol} tiles in {a.ViolFrames} frames, misses {a.Miss})" : "") +
                     $", frames fully unchanged {a.AllUnS}, global draw prefix {gp:F1}%, per-tile prefix {tp:F1}% of changed tiles' draws{(a.VideoFrames > 0 ? $", {a.VideoFrames} frames with the real-video picture (every tile changed)" : "")}; changers {cats} (* = no bounds in the stream: whole screen, an upper bound)");
        }
        Log.Info($"[k11] heat map: per-frame rows k11-heat.tsv, per-scene k11-heat-scenes.tsv (texture map rebuilds {K11Tex.Rebuilds})");
    }
}
#endif
