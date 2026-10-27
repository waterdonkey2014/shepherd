namespace Shepherd.Engine.Render;

/// <summary>SHEPHERD_RENDER=compare: one draw stream, two backends. A (GL) is the main image; B (SDL) is the reference, rendered
/// from exactly the same scene state, so the difference is the backends' and nothing else (no run-to-run animation noise).
/// Screenshots write &lt;f&gt;.png (A), &lt;f&gt;-sdl.png (B), &lt;f&gt;-diff.png (|A-B| x8) and log a [compare] line.</summary>
[ProfileOnly]
public sealed class TeeBackend : IRenderBackend, ICompressedUpload
{
    public readonly IRenderBackend A, B;
    readonly List<(IntPtr a, IntPtr b)> _h = new() { (IntPtr.Zero, IntPtr.Zero) };
    readonly Stack<int> _free = new();
    public TeeBackend(IRenderBackend a, IRenderBackend b) { A = a; B = b; Log.Info($"[render] compare mode: {a.Name} (main image) + {b.Name} (reference)"); }
    public string Name => $"compare({A.Name}+{B.Name})";
    public int W => A.W;
    public int H => A.H;
    public RenderStats Stats => A.Stats;
    IntPtr Add(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero && b == IntPtr.Zero) return IntPtr.Zero;
        int i; if (_free.Count > 0) { i = _free.Pop(); _h[i] = (a, b); } else { i = _h.Count; _h.Add((a, b)); }
        return (IntPtr)i;
    }
    (IntPtr a, IntPtr b) Map(IntPtr h) { int i = (int)h; return i > 0 && i < _h.Count ? _h[i] : (IntPtr.Zero, IntPtr.Zero); }
    // A first: B (SDL) may bake an SDF atlas into an owned buffer in place
    public IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf = false, bool owned = false)
    {
        var hd = Add(A.CreateTexture(w, h, rgba, linear, sdf, false), B.CreateTexture(w, h, rgba, linear, sdf, owned));
        if (Bisect > 0) _info[hd] = $"{w}x{h}{(linear ? " linear" : " nearest")}{(sdf ? " sdf" : "")}";
        return hd;
    }
    // SHEPHERD_COMPARE_BISECT=<frame>: at that frame, read both images back after every world-pass draw and log each draw that
    // adds pixels differing by more than 8 levels (diagnostics; slow).
    static readonly int Bisect = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_COMPARE_BISECT"), out var bf) ? bf : 0;
    static readonly bool BisectUi = Environment.GetEnvironmentVariable("SHEPHERD_COMPARE_BISECT_UI") == "1";   // also the UI draws after the Stylizer
    readonly Dictionary<IntPtr, string> _info = new();
    int _bisectDraw, _bisectPrev; bool _worldDone;
    public void DestroyTexture(IntPtr tex) { var (a, b) = Map(tex); A.DestroyTexture(a); B.DestroyTexture(b); if ((int)tex > 0 && (int)tex < _h.Count) { _h[(int)tex] = default; _free.Push((int)tex); } }
    public IntPtr CreateTarget(int w, int h) => Add(A.CreateTarget(w, h), B.CreateTarget(w, h));
    public bool Dxt => A is ICompressedUpload c && c.Dxt;
    /// <summary>A takes the blocks; B (SDL) gets the decoded RGBA of the same texture.</summary>
    public IntPtr CreateCompressedTexture(int w, int h, int unityFormat, byte[] blocks, bool linear, Func<byte[]> rgba)
    {
        var a = ((ICompressedUpload)A).CreateCompressedTexture(w, h, unityFormat, blocks, linear, null);
        if (a == IntPtr.Zero) return IntPtr.Zero;
        var px = rgba?.Invoke(); if (px == null) { A.DestroyTexture(a); return IntPtr.Zero; }
        var hd = Add(a, B.CreateTexture(w, h, px, linear, false, true));
        if (Bisect > 0) _info[hd] = $"{w}x{h}{(linear ? " linear" : " nearest")} dxt{unityFormat}";
        return hd;
    }
    public void SetTarget(IntPtr target) { var (a, b) = Map(target); A.SetTarget(a); B.SetTarget(b); }
    public void Begin(float r, float g, float b, float a) { A.Begin(r, g, b, a); B.Begin(r, g, b, a); }
    public void ClearTarget(float r, float g, float b, float a) { A.ClearTarget(r, g, b, a); B.ClearTarget(r, g, b, a); }
    public void Clip(int x, int y, int w, int h) { A.Clip(x, y, w, h); B.Clip(x, y, w, h); }
    public void NoClip() { A.NoClip(); B.NoClip(); }
    public void Draw(IntPtr tex, uint blend, bool sdf, Span<Sdl.Vertex> v, Span<int> idx)
    {
        var (a, b) = Map(tex); A.Draw(a, blend, sdf, v, idx); B.Draw(b, blend, sdf, v, idx);
        if (Bisect > 0 && Runtime.Time.FrameCount == Bisect && (!_worldDone || BisectUi)) BisectStep(tex, blend, sdf, v, idx);
    }
    void BisectStep(IntPtr tex, uint blend, bool sdf, Span<Sdl.Vertex> v, Span<int> idx)
    {
        _bisectDraw++;
        var pa = A.ReadPixels(out int w, out int h); var pb = B.ReadPixels(out _, out _);
        var d = ImageDiff.Compare(pa, pb, w, h, out _);
        int n8 = (int)Math.Round(d.Pct8 * w * h / 100.0);
        if (n8 != _bisectPrev)
        {
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue, c0 = float.MaxValue, c1 = float.MinValue, x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var q in v) { u0 = Math.Min(u0, q.tex_coord.x); u1 = Math.Max(u1, q.tex_coord.x); v0 = Math.Min(v0, q.tex_coord.y); v1 = Math.Max(v1, q.tex_coord.y); c0 = Math.Min(c0, Math.Min(q.color.r, Math.Min(q.color.g, Math.Min(q.color.b, q.color.a)))); c1 = Math.Max(c1, Math.Max(q.color.r, Math.Max(q.color.g, Math.Max(q.color.b, q.color.a)))); x0 = Math.Min(x0, q.position.x); x1 = Math.Max(x1, q.position.x); y0 = Math.Min(y0, q.position.y); y1 = Math.Max(y1, q.position.y); }
            Log.Info($"[bisect] draw {_bisectDraw}: >8 px {_bisectPrev} -> {n8} (meanAbs {d.MeanAbs:F3} max {d.Max}) tex={(_info.TryGetValue(tex, out var ti) ? ti : tex == IntPtr.Zero ? "none" : "?")} blend=0x{blend:X} sdf={sdf} verts={v.Length} tris={idx.Length / 3} pos=({x0:F1},{y0:F1})-({x1:F1},{y1:F1}) uv=({u0:F4},{v0:F4})-({u1:F4},{v1:F4}) col=[{c0:F3},{c1:F3}]");
            _bisectPrev = n8;
        }
    }
    public void EndWorldPass() { A.EndWorldPass(); B.EndWorldPass(); if (Bisect > 0 && Runtime.Time.FrameCount == Bisect) _worldDone = true; }
    public void EndFrame() { A.EndFrame(); B.EndFrame(); }
    public void Finish() { A.Finish(); B.Finish(); }
    public void Present() => A.Present();
    public byte[] ReadPixels(out int w, out int h) => A.ReadPixels(out w, out h);
    public void Screenshot(string path)
    {
        var pa = A.ReadPixels(out int aw, out int ah); var pb = B.ReadPixels(out int bw, out int bh);
        if (pa != null) Png.Write(path, aw, ah, pa);
        var stem = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path));
        if (pb != null) Png.Write(stem + "-sdl.png", bw, bh, pb);
        if (pa == null || pb == null || aw != bw || ah != bh) { Log.Warn($"[compare] {Path.GetFileName(path)}: cannot compare ({aw}x{ah} vs {bw}x{bh})"); return; }
        var d = ImageDiff.Compare(pa, pb, aw, ah, out var diffImg);
        Png.Write(stem + "-diff.png", aw, ah, diffImg);
        Log.Info($"[compare] {Path.GetFileName(path)} {A.Name} vs {B.Name}: {d}");
        if (Environment.GetEnvironmentVariable("SHEPHERD_COMPARE_WORLD") == "1" && A is GlBackend gl && gl.ReadWorldPass() is byte[] wp) Png.Write(stem + "-world-gl.png", aw, ah, wp);   // SDL's: SHEPHERD_DEBUG_STY=<frame> -> shots/sty/pre.png
    }
    public void Dispose() { A.Dispose(); B.Dispose(); }
}

/// <summary>RGB image difference: mean absolute difference (levels, over all channels and pixels), share of pixels whose largest
/// channel difference exceeds 8 and 24 levels, the maximum, and the bounding box of the >8 pixels. Alpha is reported apart.</summary>
[ProfileOnly]
public sealed record ImageDiff(double MeanAbs, double Pct8, double Pct24, int Max, int AlphaPixels, (int x0, int y0, int x1, int y1) Bbox, int Pixels)
{
    public static ImageDiff Compare(byte[] a, byte[] b, int w, int h, out byte[] vis)
    {
        vis = new byte[w * h * 4]; long sum = 0; int n8 = 0, n24 = 0, max = 0, na = 0; int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, m = 0;
                for (int c = 0; c < 3; c++) { int d = Math.Abs(a[i + c] - b[i + c]); sum += d; if (d > m) m = d; vis[i + c] = (byte)Math.Min(255, d * 8); }
                vis[i + 3] = 255;
                if (a[i + 3] != b[i + 3]) na++;
                if (m > max) max = m;
                if (m > 24) n24++;
                if (m > 8) { n8++; if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
            }
        return new ImageDiff(sum / (3.0 * w * h), 100.0 * n8 / (w * h), 100.0 * n24 / (w * h), max, na, (x0, y0, x1, y1), w * h);
    }
    public override string ToString() => $"meanAbs={MeanAbs:F3} >8={Pct8:F3}% >24={Pct24:F3}% max={Max} alphaDiffPx={AlphaPixels}" + (Bbox.x1 >= 0 ? $" bbox(>8)=({Bbox.x0},{Bbox.y0})-({Bbox.x1},{Bbox.y1})" : "");
}
