using Shepherd.Assets;

namespace Shepherd.VitaPack;

/// <summary>Kit #11 track gpu2 (vita/notes/kit11-gpu2.md), "a second check before dropping texture detail": a colour-grading LUT strip is
/// kept exactly as stored. The pack halves (and DXT-encodes) every texture of 32 px or more on its short side that no rule marks exact; for
/// a LUT strip that is not a softer picture but other colours -- the grading reads it as a 32x32x32 colour cube laid out as a 1024x32 strip
/// (64 likewise; a 16-strip is under 32 px and is kept anyway), so a 512x16 copy gives every graded pixel a different, blockier colour
/// (kit #10's intro pack: the lighting LUTs that the game swaps in at run time -- seen by no recorded boot, named by no Stylizer script --
/// went to 512x16 DXT1, PSNR 28-42 dB).
/// A strip is told by its shape AND its content (review fix: the shape alone would also keep, e.g., a 4096x64 sprite sheet as stored, 1 MiB
/// instead of a 64 KiB halved copy): w = h x h with h 16, 32 or 64, not Crunch-compressed, every texel opaque, and the texels' luma following
/// the luma of their own cube coordinates (Pearson r of at least <see cref="MinLumaFit"/>, either row order) -- a grading LUT maps darker
/// inputs to darker outputs whatever it does to hue and range (r = 1 for the identity strip), a sprite sheet's texels have nothing to do with
/// their place in a cube. Every kept strip is listed with its pack and texture-memory bytes against the halved copy's, and so is every strip
/// of that shape the content test turns away (reduced as before). SHEPHERD_K11_PACK_LUT=0 = kit #10's classification (the switch back).</summary>
sealed partial class Baker
{
    static readonly bool K11LutExact = Environment.GetEnvironmentVariable("SHEPHERD_K11_PACK_LUT") != "0";
    const double MinLumaFit = 0.5;
    static bool IsLutStrip(int w, int h) => (h == 16 || h == 32 || h == 64) && w == h * h;
    /// <summary>Marks the LUT strips no earlier rule kept exact; returns how many.</summary>
    int K11ExactLuts()
    {
        if (!K11LutExact) return 0;
        int n = 0; long pack = 0, packHalf = 0, gpu = 0, gpuHalf = 0;
        foreach (var t in _tex)
        {
            var f = (TextureFormat)t.Fmt;
            if (t.Exact != null || t.Stub != null || !IsLutStrip(t.W, t.H) || f.ToString().EndsWith("Crunched")) continue;
            double fit = LutLumaFit(t);
            if (!(fit >= MinLumaFit))
            {
                Say($"  kit #11: '{t.Name}' ({f} {t.W}x{t.H}) has a LUT strip's shape but not its content ({(double.IsNaN(fit) ? "not opaque, or not decodable" : $"luma fit {fit:F2} < {MinLumaFit}")}): reduced as before");
                continue;
            }
            long st = TextureDecoder.Mip0Size(f, t.W, t.H), half = t.H >= 32 ? (long)(t.W / 2 / 4) * (t.H / 2 / 4) * 8 : st;   // the halved copy: opaque DXT1 (h 16: never halved)
            long g = f is TextureFormat.DXT1 or TextureFormat.DXT5 ? st : (long)t.W * t.H * 4;                                    // the engine uploads non-DXT strips as RGBA8
            t.Exact = $"a colour-grading LUT strip (w = h x h, luma fit {fit:F2}): kept as stored (kit #11)"; n++;
            pack += st; packHalf += half; gpu += g; gpuHalf += half;
            Say($"  kit #11: '{t.Name}' ({f} {t.W}x{t.H}, luma fit {fit:F2}) kept as stored: {st / 1024.0:F1} KiB in the pack, {g / 1024.0:F1} KiB of texture memory (the halved copy: {half / 1024.0:F1} KiB each)");
        }
        if (n > 0) Say($"kit #11: the kept LUT strips cost {(pack - packHalf) / 1024.0:F1} KiB more pack and {(gpu - gpuHalf) / 1024.0:F1} KiB more texture memory than their halved copies (when all are resident)");
        return n;
    }
    /// <summary>Pearson r between each texel's luma and the luma of its cube coordinates (red = x mod h, blue = x / h, green = the row, in
    /// either order: the better of the two); NaN when a texel is not opaque or the image does not decode.</summary>
    double LutLumaFit(TexJob t)
    {
        byte[] rgba;
        try
        {
            byte[] data; lock (DbLock) data = TextureDecoder.GetImageData(Db, t.E.File, t.Src, topMipOnly: true);
            rgba = TextureDecoder.Decode(data, t.W, t.H, (TextureFormat)t.Fmt);
        }
        catch (Exception) { return double.NaN; }
        int w = t.W, h = t.H;
        if (rgba == null || rgba.Length < w * h * 4) return double.NaN;
        double s = 0, ss = 0, s0 = 0, ss0 = 0, sp0 = 0, s1 = 0, ss1 = 0, sp1 = 0, n = (double)w * h, d = h - 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (rgba[i + 3] != 255) return double.NaN;
                double lo = 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];
                double rb = 0.299 * (x % h) / d + 0.114 * (x / h) / d, l0 = rb + 0.587 * y / d, l1 = rb + 0.587 * (d - y) / d;
                s += lo; ss += lo * lo; s0 += l0; ss0 += l0 * l0; sp0 += lo * l0; s1 += l1; ss1 += l1 * l1; sp1 += lo * l1;
            }
        static double R(double n, double sa, double saa, double sb, double sbb, double sab)
        {
            double va = saa - sa * sa / n, vb = sbb - sb * sb / n;
            return va <= 1e-9 || vb <= 1e-9 ? 0 : (sab - sa * sb / n) / Math.Sqrt(va * vb);
        }
        return Math.Max(R(n, s, ss, s0, ss0, sp0), R(n, s, ss, s1, ss1, sp1));
    }
}
