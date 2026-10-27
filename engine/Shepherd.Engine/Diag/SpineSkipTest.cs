using Shepherd.Engine.Render;
using static Shepherd.Engine.Render.Sdl;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>kit #7 spine track, "skip provably invisible pieces" test (Mac only, never in an eboot): `Shepherd.Engine spineskiptest [rounds] [seed]`
/// draws random textured triangle meshes -- a third of their triangles with vertex colour (0, 0, 0, 0), the rest random colours, some
/// sharing vertices with zero ones -- over random backgrounds through the GL backend (and the SDL backend), once whole and once with
/// SpineSkip.Filter's index list, in every blend mode SpineSkip calls a no-op (BLEND, BLEND_PREMULTIPLIED, ADD, ADD_PREMULTIPLIED, MUL), and
/// compares the read-back pixels bit for bit; the excluded modes (NONE, MOD) are drawn the same way to show they DO change (the control).
/// Exit 0 = every no-op mode identical in every round and every control different at least once.</summary>
[ProfileOnly]
public static class SpineSkipTest
{
    public static bool Handles(string mode) => mode == "spineskiptest";
    const int W = 256, H = 192;

    public static int Run(string[] args)
    {
        int rounds = args.Length > 1 ? int.Parse(args[1]) : 200, seed = args.Length > 2 ? int.Parse(args[2]) : 3;
        uint[] noop = { BLENDMODE_BLEND, BLENDMODE_BLEND_PREMULTIPLIED, BLENDMODE_ADD, BLENDMODE_ADD_PREMULTIPLIED, BLENDMODE_MUL };
        uint[] control = { BLENDMODE_NONE, BLENDMODE_MOD };
        int bad = 0, controlsDiffer = 0, compared = 0; long skippedTris = 0;
        using var gl = new GlBackend(W, H, headless: true);
        using var sdl = new Gfx(W, H, headless: true);
        foreach (var (name, g) in new (string, IRenderBackend)[] { ("gl", gl), ("sdl", sdl) })
        {
            var rnd = new Random(seed);
            for (int r = 0; r < rounds; r++)
            {
                // a random texture (some transparent texels) and a mesh
                int tw = 8 << rnd.Next(3), th = 8 << rnd.Next(3); var px = new byte[tw * th * 4]; rnd.NextBytes(px);
                for (int i = 3; i < px.Length; i += 4) if (rnd.Next(4) == 0) px[i] = 0;
                var tex = g.CreateTexture(tw, th, px, false);
                int nv = 6 + rnd.Next(40); var v = new Vertex[nv];
                for (int i = 0; i < nv; i++)
                {
                    v[i].position.x = (float)(rnd.NextDouble() * (W + 40) - 20); v[i].position.y = (float)(rnd.NextDouble() * (H + 40) - 20);
                    v[i].tex_coord.x = (float)rnd.NextDouble(); v[i].tex_coord.y = (float)rnd.NextDouble();
                    if (rnd.Next(3) != 0) { float a = (float)rnd.NextDouble(); v[i].color.r = (float)rnd.NextDouble() * a; v[i].color.g = (float)rnd.NextDouble() * a; v[i].color.b = (float)rnd.NextDouble() * a; v[i].color.a = a; }
                }
                int nt = 4 + rnd.Next(30); var idx = new int[nt * 3]; for (int i = 0; i < idx.Length; i++) idx[i] = rnd.Next(nv);
                var filt = (int[])idx.Clone(); long before = SpineSkip.Tris; int nf = SpineSkip.Filter(v, filt, filt.Length); skippedTris += SpineSkip.Tris - before;
                float br = (float)rnd.NextDouble(), bg = (float)rnd.NextDouble(), bb = (float)rnd.NextDouble();
                foreach (var blend in noop.Concat(control))
                {
                    byte[] Draw(int[] ib, int n)
                    {
                        g.Begin(br, bg, bb, 1);
                        // a textured opaque backdrop quad so dst varies per pixel
                        var q = new Vertex[4]; float[,] c = { { 0, 0, 0, 0 }, { W, 0, 1, 0 }, { W, H, 1, 1 }, { 0, H, 0, 1 } };
                        for (int i = 0; i < 4; i++) q[i] = new Vertex { position = new() { x = c[i, 0], y = c[i, 1] }, tex_coord = new() { x = c[i, 2], y = c[i, 3] }, color = new() { r = 1, g = 1, b = 1, a = 1 } };
                        g.Draw(tex, BLENDMODE_NONE, false, q, new[] { 0, 1, 2, 0, 2, 3 });
                        if (n >= 3) g.Draw(tex, blend, false, v, ib.AsSpan(0, n));
                        g.EndFrame();
                        return g.ReadPixels(out _, out _);
                    }
                    var a = Draw(idx, idx.Length); var b = Draw(filt, nf);
                    bool same = a.AsSpan().SequenceEqual(b);
                    compared++;
                    if (noop.Contains(blend)) { if (!same) { bad++; if (bad <= 10) Log.Warn($"[spineskiptest] {name} round {r} blend {blend}: the skipped draw DIFFERS ({Diffs(a, b)} bytes)"); } }
                    else if (!same) controlsDiffer++;
                }
                g.DestroyTexture(tex);
            }
        }
        Log.Info($"[spineskiptest] {rounds} rounds x 2 backends x 7 blend modes = {compared} comparisons ({skippedTris} triangles of colour 0 left out): no-op modes {bad} differ; controls (NONE / MOD) differing in {controlsDiffer} comparisons");
        return bad == 0 && controlsDiffer > 0 ? 0 : 1;
    }
    static int Diffs(byte[] a, byte[] b) { int n = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n; }
}
#else
[ProfileOnly]
public static class SpineSkipTest { public static bool Handles(string mode) => false; public static int Run(string[] args) => 2; }
#endif
