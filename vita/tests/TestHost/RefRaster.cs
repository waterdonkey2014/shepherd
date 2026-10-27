using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Shepherd.Engine.Render;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.TestHost;

/// <summary>Kit #7 tests track (vita/notes/kit7-tests.md "One-feature test scenes + a reference renderer"): an IRenderBackend that RECORDS what
/// Shepherd's SceneRenderer hands a backend -- the sorted draw list (texture, blend, clip rectangle, render target, every vertex and index) and
/// every texture's pixels -- and a reference renderer that rasterises exactly that list on the CPU (pixel centres, triangles by edge
/// functions with the top-left rule, point-sampled textures, the engine's blend modes in straight 8-bit-per-channel arithmetic). Its
/// output is compared with what the Unity 2022.3 oracle player rendered for the same scene (probes/Scenes.cs), so a difference is either
/// in the draw list (order, geometry, colour: Shepherd's renderer) or, if the list is right, in the rasteriser's own rules.</summary>
public sealed class RecordingBackend : IRenderBackend
{
    public sealed class Tex { public int W, H; public byte[] Rgba; }
    public sealed class DrawRec { public IntPtr Tex; public uint Blend; public bool Sdf; public Vertex[] V; public int[] Idx; public (int x, int y, int w, int h)? Clip; public IntPtr Target; }
    public readonly Dictionary<IntPtr, Tex> Textures = new();
    public List<DrawRec> Draws = new(); public List<DrawRec> LastFrame = new();
    public (float r, float g, float b, float a) Clear;
    (int, int, int, int)? _clip; IntPtr _target; long _next = 1;
    public RecordingBackend(int w, int h) { W = w; H = h; }
    public string Name => "recording";
    public int W { get; }
    public int H { get; }
    public RenderStats Stats { get; } = new RenderStats();
    public IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf = false, bool owned = false) { var p = (IntPtr)(_next++); Textures[p] = new Tex { W = w, H = h, Rgba = (byte[])rgba?.Clone() }; return p; }
    public void DestroyTexture(IntPtr tex) { Textures.Remove(tex); }
    public IntPtr CreateTarget(int w, int h) { var p = (IntPtr)(_next++); Textures[p] = new Tex { W = w, H = h, Rgba = new byte[w * h * 4] }; return p; }
    public void SetTarget(IntPtr target) { _target = target; }
    public void Begin(float r, float g, float b, float a) { Clear = (r, g, b, a); Draws = new List<DrawRec>(); _clip = null; _target = IntPtr.Zero; }
    public void ClearTarget(float r, float g, float b, float a) { }
    public void Clip(int x, int y, int w, int h) { _clip = (x, y, w, h); }
    public void NoClip() { _clip = null; }
    public void Draw(IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx) => Draws.Add(new DrawRec { Tex = tex, Blend = blend, Sdf = sdf, V = v.ToArray(), Idx = idx.ToArray(), Clip = _clip, Target = _target });
    public void EndWorldPass() { }
    public void EndFrame() { LastFrame = Draws; }
    public void Finish() { }
    public void Present() { }
    public byte[] ReadPixels(out int w, out int h) { w = W; h = H; return Raster(LastFrame); }
    public void Screenshot(string path) { }
    public void Dispose() { }

    public string DrawList(List<DrawRec> draws)
    {
        var sb = new StringBuilder(); var ic = CultureInfo.InvariantCulture;
        sb.Append("# clear ").Append(Clear.r.ToString("R", ic)).Append(',').Append(Clear.g.ToString("R", ic)).Append(',').Append(Clear.b.ToString("R", ic)).Append(',').Append(Clear.a.ToString("R", ic)).Append('\n');
        int i = 0;
        foreach (var d in draws)
        {
            var t = Textures.TryGetValue(d.Tex, out var tx) ? $"{tx.W}x{tx.H}" : d.Tex == IntPtr.Zero ? "none" : "?";
            sb.Append(i++).Append("\ttex ").Append(t).Append("\tblend ").Append(d.Blend).Append(d.Sdf ? " sdf" : "").Append("\tclip ").Append(d.Clip is { } c ? $"{c.x},{c.y},{c.w},{c.h}" : "-").Append(d.Target != IntPtr.Zero ? "\ttarget " + d.Target : "")
              .Append("\tidx ").Append(string.Join(",", d.Idx)).Append("\tv");
            foreach (var v in d.V) sb.Append(' ').Append(v.position.x.ToString("R", ic)).Append(',').Append(v.position.y.ToString("R", ic)).Append(" c").Append(B(v.color.r)).Append(',').Append(B(v.color.g)).Append(',').Append(B(v.color.b)).Append(',').Append(B(v.color.a)).Append(" uv").Append(v.tex_coord.x.ToString("R", ic)).Append(',').Append(v.tex_coord.y.ToString("R", ic));
            sb.Append('\n');
        }
        return sb.ToString();
    }
    static int B(float f) => (int)MathF.Round(Math.Clamp(f, 0f, 1f) * 255f);

    /// <summary>The reference renderer: the draws in order onto a W x H RGBA8 frame cleared to the Begin colour (top row first). Draws into
    /// an off-screen target are skipped (the one-feature scenes use none).</summary>
    public byte[] Raster(List<DrawRec> draws)
    {
        var fb = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { fb[i * 4] = (byte)B(Clear.r); fb[i * 4 + 1] = (byte)B(Clear.g); fb[i * 4 + 2] = (byte)B(Clear.b); fb[i * 4 + 3] = (byte)B(Clear.a); }
        foreach (var d in draws)
        {
            if (d.Target != IntPtr.Zero) continue;
            Textures.TryGetValue(d.Tex, out var tex);
            for (int t = 0; t + 2 < d.Idx.Length; t += 3) Tri(fb, d, tex, d.V[d.Idx[t]], d.V[d.Idx[t + 1]], d.V[d.Idx[t + 2]]);
        }
        return fb;
    }
    void Tri(byte[] fb, DrawRec d, Tex tex, Vertex a, Vertex b, Vertex c)
    {
        double area = (b.position.x - a.position.x) * (c.position.y - a.position.y) - (b.position.y - a.position.y) * (c.position.x - a.position.x);
        if (area == 0) return;
        if (area < 0) { (b, c) = (c, b); area = -area; }
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.position.x, Math.Min(b.position.x, c.position.x)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(a.position.x, Math.Max(b.position.x, c.position.x))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.position.y, Math.Min(b.position.y, c.position.y)))), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(a.position.y, Math.Max(b.position.y, c.position.y))));
        if (d.Clip is { } cl) { x0 = Math.Max(x0, cl.x); y0 = Math.Max(y0, cl.y); x1 = Math.Min(x1, cl.x + cl.w - 1); y1 = Math.Min(y1, cl.y + cl.h - 1); }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double w0 = Edge(b, c, px, py), w1 = Edge(c, a, px, py), w2 = Edge(a, b, px, py);
                if (!Inside(w0, b, c) || !Inside(w1, c, a) || !Inside(w2, a, b)) continue;
                w0 /= area; w1 /= area; w2 /= area;
                double cr = w0 * a.color.r + w1 * b.color.r + w2 * c.color.r, cg = w0 * a.color.g + w1 * b.color.g + w2 * c.color.g, cb = w0 * a.color.b + w1 * b.color.b + w2 * c.color.b, ca = w0 * a.color.a + w1 * b.color.a + w2 * c.color.a;
                if (tex?.Rgba != null)
                {
                    double u = w0 * a.tex_coord.x + w1 * b.tex_coord.x + w2 * c.tex_coord.x, v = w0 * a.tex_coord.y + w1 * b.tex_coord.y + w2 * c.tex_coord.y;
                    int tx = Math.Clamp((int)Math.Floor(u * tex.W), 0, tex.W - 1), ty = Math.Clamp((int)Math.Floor(v * tex.H), 0, tex.H - 1); int o = (ty * tex.W + tx) * 4;
                    cr *= tex.Rgba[o] / 255.0; cg *= tex.Rgba[o + 1] / 255.0; cb *= tex.Rgba[o + 2] / 255.0; ca *= tex.Rgba[o + 3] / 255.0;
                }
                int p = (y * W + x) * 4;
                Blend(fb, p, d.Blend, cr, cg, cb, ca);
            }
    }
    static double Edge(Vertex a, Vertex b, double x, double y) => (b.position.x - a.position.x) * (y - a.position.y) - (b.position.y - a.position.y) * (x - a.position.x);
    /// <summary>The top-left rule (a pixel centre exactly on a shared edge belongs to one triangle only).</summary>
    static bool Inside(double w, Vertex a, Vertex b)
    {
        if (w > 0) return true; if (w < 0) return false;
        double dx = b.position.x - a.position.x, dy = b.position.y - a.position.y;
        return dy > 0 || (dy == 0 && dx < 0);
    }
    static void Blend(byte[] fb, int p, uint mode, double r, double g, double b, double a)
    {
        double dr = fb[p] / 255.0, dg = fb[p + 1] / 255.0, db = fb[p + 2] / 255.0, da = fb[p + 3] / 255.0;
        switch (mode)
        {
            case BLENDMODE_NONE: break;
            case BLENDMODE_BLEND: r = r * a + dr * (1 - a); g = g * a + dg * (1 - a); b = b * a + db * (1 - a); a = a + da * (1 - a); break;
            case BLENDMODE_BLEND_PREMULTIPLIED: r = r + dr * (1 - a); g = g + dg * (1 - a); b = b + db * (1 - a); a = a + da * (1 - a); break;
            case BLENDMODE_ADD: r = dr + r * a; g = dg + g * a; b = db + b * a; a = da; break;
            case BLENDMODE_ADD_PREMULTIPLIED: r = dr + r; g = dg + g; b = db + b; a = da; break;
            case BLENDMODE_MOD: r = dr * r; g = dg * g; b = db * b; a = da; break;
            case BLENDMODE_MUL: r = r * dr + dr * (1 - a); g = g * dg + dg * (1 - a); b = b * db + db * (1 - a); a = da; break;
        }
        fb[p] = (byte)Math.Round(Math.Clamp(r, 0, 1) * 255); fb[p + 1] = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255); fb[p + 2] = (byte)Math.Round(Math.Clamp(b, 0, 1) * 255); fb[p + 3] = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
    }
}
