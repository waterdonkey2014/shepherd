using System.Buffers.Binary;
namespace Shepherd.VitaPack;

/// <summary>In-house BC1 (DXT1) / BC3 (DXT5) encoder. Colour: principal-axis range fit, then least-squares endpoint
/// refinement over the chosen indices (3 passes, keeping the best), exact single-colour tables for flat blocks, always
/// 4-colour mode (c0 &gt; c1) so a BC1 block never decodes to punch-through black. BC3 alpha: both interpolation modes,
/// endpoints searched around the block's range. Errors are measured with the same integer palette maths as the engine's
/// CPU decoder (Bcn). Input: top-down RGBA8, w and h multiples of 4. Output: blocks in row-major order starting at the TOP
/// of the image, pixel rows top-first inside a block — the layout the engine uploads (= DxtBlocks.FlipY of Unity data).</summary>
static class DxtEncoder
{
    static readonly byte[] Ex5 = new byte[32], Ex6 = new byte[64];
    static readonly byte[] Q5 = new byte[256], Q6 = new byte[256];
    // single colour: for 8-bit v, endpoints (a, b) whose 2/3·a + 1/3·b interpolant (index 2) is closest to v
    static readonly (byte a, byte b)[] One5 = new (byte, byte)[256], One6 = new (byte, byte)[256];

    static DxtEncoder()
    {
        for (int i = 0; i < 32; i++) Ex5[i] = (byte)((i << 3) | (i >> 2));
        for (int i = 0; i < 64; i++) Ex6[i] = (byte)((i << 2) | (i >> 4));
        for (int v = 0; v < 256; v++)
        {
            Q5[v] = (byte)Enumerable.Range(0, 32).OrderBy(q => Math.Abs(Ex5[q] - v)).First();
            Q6[v] = (byte)Enumerable.Range(0, 64).OrderBy(q => Math.Abs(Ex6[q] - v)).First();
            One5[v] = BestOne(v, Ex5); One6[v] = BestOne(v, Ex6);
        }
    }
    static (byte, byte) BestOne(int v, byte[] ex)
    {
        int best = int.MaxValue; (byte, byte) r = (0, 0);
        for (int a = 0; a < ex.Length; a++)
            for (int b = 0; b < ex.Length; b++)
            {
                int e = Math.Abs((2 * ex[a] + ex[b] + 1) / 3 - v) * 1000 + Math.Abs(a - b);
                if (e < best) { best = e; r = ((byte)a, (byte)b); }
            }
        return r;
    }

    public static byte[] Encode(byte[] rgba, int w, int h, bool bc3)
    {
        if (w % 4 != 0 || h % 4 != 0) throw new ArgumentException($"DXT encode needs sizes that are multiples of 4 ({w}x{h})");
        int bw = w / 4, bh = h / 4, bb = bc3 ? 16 : 8;
        var dst = new byte[bw * bh * bb];
        Parallel.For(0, bh, by =>
        {
            Span<byte> px = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++) rgba.AsSpan(((by * 4 + y) * w + bx * 4) * 4, 16).CopyTo(px.Slice(y * 16, 16));
                var o = dst.AsSpan((by * bw + bx) * bb, bb);
                if (bc3) { EncodeAlpha(px, o[..8]); EncodeColor(px, o.Slice(8, 8)); }
                else EncodeColor(px, o);
            }
        });
        return dst;
    }

    // ------------------------------------------------------------------ colour

    static void Palette(int c0, int c1, Span<int> pal)
    {
        int r0 = Ex5[(c0 >> 11) & 31], g0 = Ex6[(c0 >> 5) & 63], b0 = Ex5[c0 & 31];
        int r1 = Ex5[(c1 >> 11) & 31], g1 = Ex6[(c1 >> 5) & 63], b1 = Ex5[c1 & 31];
        pal[0] = r0; pal[1] = g0; pal[2] = b0; pal[3] = r1; pal[4] = g1; pal[5] = b1;
        pal[6] = (2 * r0 + r1 + 1) / 3; pal[7] = (2 * g0 + g1 + 1) / 3; pal[8] = (2 * b0 + b1 + 1) / 3;
        pal[9] = (r0 + 2 * r1 + 1) / 3; pal[10] = (g0 + 2 * g1 + 1) / 3; pal[11] = (b0 + 2 * b1 + 1) / 3;
    }

    /// <summary>Indices (palette order 0,1,2,3 = c0, c1, 2/3c0+1/3c1, 1/3c0+2/3c1) and the summed squared error.</summary>
    static long Fit(ReadOnlySpan<byte> px, int c0, int c1, Span<byte> idx)
    {
        Span<int> pal = stackalloc int[12];
        Palette(c0, c1, pal);
        long err = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2], best = int.MaxValue, bi = 0;
            for (int k = 0; k < 4; k++)
            {
                int dr = r - pal[k * 3], dg = g - pal[k * 3 + 1], db = b - pal[k * 3 + 2], e = dr * dr + dg * dg + db * db;
                if (e < best) { best = e; bi = k; }
            }
            idx[i] = (byte)bi; err += best;
        }
        return err;
    }

    static int Pack565(float r, float g, float b) =>
        (Q5[Math.Clamp((int)MathF.Round(r), 0, 255)] << 11) | (Q6[Math.Clamp((int)MathF.Round(g), 0, 255)] << 5) | Q5[Math.Clamp((int)MathF.Round(b), 0, 255)];

    static void EncodeColor(ReadOnlySpan<byte> px, Span<byte> o)
    {
        Span<byte> idx = stackalloc byte[16], tmp = stackalloc byte[16];
        int c0, c1;
        bool flat = true;
        for (int i = 1; i < 16 && flat; i++) flat = px[i * 4] == px[0] && px[i * 4 + 1] == px[1] && px[i * 4 + 2] == px[2];
        if (flat)
        {
            var (ra, rb) = One5[px[0]]; var (ga, gb) = One6[px[1]]; var (ba, bbv) = One5[px[2]];
            c0 = (ra << 11) | (ga << 5) | ba; c1 = (rb << 11) | (gb << 5) | bbv;
            Fit(px, c0, c1, idx);
        }
        else
        {
            // principal axis of the 16 colours (power iteration on the covariance)
            float mr = 0, mg = 0, mb = 0;
            for (int i = 0; i < 16; i++) { mr += px[i * 4]; mg += px[i * 4 + 1]; mb += px[i * 4 + 2]; }
            mr /= 16; mg /= 16; mb /= 16;
            float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
            for (int i = 0; i < 16; i++)
            {
                float r = px[i * 4] - mr, g = px[i * 4 + 1] - mg, b = px[i * 4 + 2] - mb;
                crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
            }
            float vr = crr >= cgg && crr >= cbb ? 1 : 0, vg = cgg > crr && cgg >= cbb ? 1 : 0, vb = vr == 0 && vg == 0 ? 1 : 0;
            for (int it = 0; it < 8; it++)
            {
                float nr = crr * vr + crg * vg + crb * vb, ng = crg * vr + cgg * vg + cgb * vb, nb = crb * vr + cgb * vg + cbb * vb;
                float len = MathF.Max(MathF.Max(MathF.Abs(nr), MathF.Abs(ng)), MathF.Abs(nb));
                if (len < 1e-6f) break;
                vr = nr / len; vg = ng / len; vb = nb / len;
            }
            float tmin = float.MaxValue, tmax = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                float t = (px[i * 4] - mr) * vr + (px[i * 4 + 1] - mg) * vg + (px[i * 4 + 2] - mb) * vb;
                tmin = MathF.Min(tmin, t); tmax = MathF.Max(tmax, t);
            }
            float n2 = vr * vr + vg * vg + vb * vb; if (n2 < 1e-12f) n2 = 1;
            c0 = Pack565(mr + vr * tmax / n2, mg + vg * tmax / n2, mb + vb * tmax / n2);
            c1 = Pack565(mr + vr * tmin / n2, mg + vg * tmin / n2, mb + vb * tmin / n2);
            long best = Fit(px, c0, c1, idx);
            // least-squares refinement of the endpoints over the current index assignment
            for (int pass = 0; pass < 3 && best > 0; pass++)
            {
                float A = 0, B = 0, C = 0, xr = 0, xg = 0, xb = 0, yr = 0, yg = 0, yb = 0;
                for (int i = 0; i < 16; i++)
                {
                    float al = idx[i] switch { 0 => 1f, 1 => 0f, 2 => 2f / 3, _ => 1f / 3 }, be = 1 - al;
                    A += al * al; B += al * be; C += be * be;
                    xr += al * px[i * 4]; xg += al * px[i * 4 + 1]; xb += al * px[i * 4 + 2];
                    yr += be * px[i * 4]; yg += be * px[i * 4 + 1]; yb += be * px[i * 4 + 2];
                }
                float det = A * C - B * B;
                if (MathF.Abs(det) < 1e-6f) break;
                float f = 1 / det;
                int n0 = Pack565((C * xr - B * yr) * f, (C * xg - B * yg) * f, (C * xb - B * yb) * f);
                int n1 = Pack565((A * yr - B * xr) * f, (A * yg - B * xg) * f, (A * yb - B * xb) * f);
                if (n0 == c0 && n1 == c1) break;
                long e = Fit(px, n0, n1, tmp);
                if (e >= best) break;
                best = e; c0 = n0; c1 = n1; tmp.CopyTo(idx);
            }
        }
        // 4-colour mode needs c0 > c1: swap the endpoints (and the index meaning) when needed; equal endpoints -> index 0
        if (c0 < c1) { (c0, c1) = (c1, c0); for (int i = 0; i < 16; i++) idx[i] = idx[i] switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 }; }
        else if (c0 == c1) idx.Clear();
        uint bits = 0; for (int i = 0; i < 16; i++) bits |= (uint)idx[i] << (2 * i);
        BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)c0); BinaryPrimitives.WriteUInt16LittleEndian(o[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], bits);
    }

    // ------------------------------------------------------------------ alpha (BC3)

    static long FitAlpha(ReadOnlySpan<byte> px, int a0, int a1, Span<byte> idx)
    {
        Span<int> a = stackalloc int[8];
        a[0] = a0; a[1] = a1;
        if (a0 > a1) for (int k = 1; k <= 6; k++) a[k + 1] = ((7 - k) * a0 + k * a1 + 3) / 7;
        else { for (int k = 1; k <= 4; k++) a[k + 1] = ((5 - k) * a0 + k * a1 + 2) / 5; a[6] = 0; a[7] = 255; }
        long err = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = px[i * 4 + 3], best = int.MaxValue, bi = 0;
            for (int k = 0; k < 8; k++) { int d = v - a[k]; d *= d; if (d < best) { best = d; bi = k; } }
            idx[i] = (byte)bi; err += best;
        }
        return err;
    }

    static void EncodeAlpha(ReadOnlySpan<byte> px, Span<byte> o)
    {
        Span<byte> idx = stackalloc byte[16], tmp = stackalloc byte[16];
        int mn = 255, mx = 0, imn = 255, imx = 0; bool extremes = false;
        for (int i = 0; i < 16; i++)
        {
            int v = px[i * 4 + 3]; mn = Math.Min(mn, v); mx = Math.Max(mx, v);
            if (v == 0 || v == 255) extremes = true; else { imn = Math.Min(imn, v); imx = Math.Max(imx, v); }
        }
        int b0 = mx, b1 = mn; long best;
        if (mn == mx) { b0 = b1 = mn; best = FitAlpha(px, b0, b1, idx); }
        else
        {
            best = FitAlpha(px, mx, mn, idx);
            for (int d0 = 0; d0 <= 3 && best > 0; d0++)
                for (int d1 = 0; d1 <= 3; d1++)
                {
                    int a0 = mx - d0, a1 = mn + d1; if (a0 <= a1 || (d0 == 0 && d1 == 0)) continue;
                    long e = FitAlpha(px, a0, a1, tmp);
                    if (e < best) { best = e; b0 = a0; b1 = a1; tmp.CopyTo(idx); }
                }
            if (extremes && best > 0)
            {   // 6-value mode with explicit 0 and 255 (a0 <= a1)
                int lo = imn > imx ? 0 : imn, hi = imn > imx ? 0 : imx;
                for (int d0 = 0; d0 <= 2; d0++)
                    for (int d1 = 0; d1 <= 2; d1++)
                    {
                        int a0 = Math.Max(0, lo - d0), a1 = Math.Min(255, hi + d1); if (a0 > a1) continue;
                        long e = FitAlpha(px, a0, a1, tmp);
                        if (e < best) { best = e; b0 = a0; b1 = a1; tmp.CopyTo(idx); }
                    }
            }
        }
        o[0] = (byte)b0; o[1] = (byte)b1;
        ulong bits = 0; for (int i = 0; i < 16; i++) bits |= (ulong)idx[i] << (3 * i);
        for (int i = 0; i < 6; i++) o[2 + i] = (byte)(bits >> (8 * i));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Unity mip-0 DXT blocks (bottom-up) -> top-down blocks (the engine's DxtBlocks.FlipY, copied: the baker does not
    /// reference the engine). Exact for heights that are multiples of 4.</summary>
    public static byte[] FlipY(ReadOnlySpan<byte> src, int w, int h, bool bc3)
    {
        int bb = bc3 ? 16 : 8, bw = w / 4, bh = h / 4, row = bw * bb;
        var dst = new byte[row * bh];
        for (int by = 0; by < bh; by++)
        {
            src.Slice(by * row, row).CopyTo(dst.AsSpan((bh - 1 - by) * row, row));
            for (int bx = 0; bx < bw; bx++)
            {
                var b = dst.AsSpan((bh - 1 - by) * row + bx * bb, bb);
                if (bb == 16) { FlipAlpha(b[..8]); FlipColor(b.Slice(8, 8)); } else FlipColor(b);
            }
        }
        return dst;
    }
    static void FlipColor(Span<byte> b) { (b[4], b[7]) = (b[7], b[4]); (b[5], b[6]) = (b[6], b[5]); }
    static void FlipAlpha(Span<byte> b)
    {
        ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        ulong r0 = bits & 0xFFF, r1 = (bits >> 12) & 0xFFF, r2 = (bits >> 24) & 0xFFF, r3 = (bits >> 36) & 0xFFF;
        bits = r3 | (r2 << 12) | (r1 << 24) | (r0 << 36);
        for (int i = 0; i < 6; i++) b[2 + i] = (byte)(bits >> (8 * i));
    }
}

/// <summary>Separable Kaiser-windowed sinc resampler (2 lobes at the destination rate, beta 4) for top-down RGBA8. Colour is
/// filtered premultiplied by alpha (no dark fringes at sprite edges) and un-premultiplied afterwards; texels that end up fully
/// transparent keep the plain filtered colour (what bilinear sampling bleeds in). Edges clamp.</summary>
static class Resampler
{
    const float Beta = 4f;
    static float I0(float x) { float s = 1, t = 1; for (int k = 1; k < 20; k++) { t *= x * x / (4f * k * k); s += t; } return s; }
    static float Sinc(float x) => MathF.Abs(x) < 1e-6f ? 1f : MathF.Sin(MathF.PI * x) / (MathF.PI * x);

    static (int[] start, float[][] w) Weights(int sN, int dN)
    {
        float scale = (float)sN / dN, s = MathF.Max(1f, scale), R = 2f * s, i0b = I0(Beta);
        var start = new int[dN]; var ws = new float[dN][];
        for (int i = 0; i < dN; i++)
        {
            float c = (i + 0.5f) * scale - 0.5f;
            int j0 = (int)MathF.Floor(c - R) + 1, j1 = (int)MathF.Floor(c + R);
            var w = new float[j1 - j0 + 1]; float sum = 0;
            for (int j = j0; j <= j1; j++)
            {
                float d = (j - c) / R; if (MathF.Abs(d) >= 1) { w[j - j0] = 0; continue; }
                float v = Sinc((j - c) / s) * I0(Beta * MathF.Sqrt(1 - d * d)) / i0b;
                w[j - j0] = v; sum += v;
            }
            for (int k = 0; k < w.Length; k++) w[k] /= sum;
            start[i] = j0; ws[i] = w;
        }
        return (start, ws);
    }

    public static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (byte[])src.Clone();
        var (hs, hw) = Weights(sw, dw); var (vs, vw) = Weights(sh, dh);
        const int C = 7;   // premultiplied r g b, alpha, plain r g b
        var mid = new float[sh * dw * C];
        Parallel.For(0, sh, y =>
        {
            for (int x = 0; x < dw; x++)
            {
                float pr = 0, pg = 0, pb = 0, pa = 0, r = 0, g = 0, b = 0; var w = hw[x]; int j0 = hs[x];
                for (int k = 0; k < w.Length; k++)
                {
                    int j = Math.Clamp(j0 + k, 0, sw - 1), p = (y * sw + j) * 4; float wk = w[k], a = src[p + 3] / 255f;
                    pr += wk * src[p] * a; pg += wk * src[p + 1] * a; pb += wk * src[p + 2] * a; pa += wk * a;
                    r += wk * src[p]; g += wk * src[p + 1]; b += wk * src[p + 2];
                }
                int o = (y * dw + x) * C; mid[o] = pr; mid[o + 1] = pg; mid[o + 2] = pb; mid[o + 3] = pa; mid[o + 4] = r; mid[o + 5] = g; mid[o + 6] = b;
            }
        });
        var dst = new byte[dw * dh * 4];
        Parallel.For(0, dh, y =>
        {
            Span<float> acc = stackalloc float[C];
            var w = vw[y]; int j0 = vs[y];
            for (int x = 0; x < dw; x++)
            {
                acc.Clear();
                for (int k = 0; k < w.Length; k++)
                {
                    int j = Math.Clamp(j0 + k, 0, sh - 1), o = (j * dw + x) * C; float wk = w[k];
                    for (int c = 0; c < C; c++) acc[c] += wk * mid[o + c];
                }
                float a = Math.Clamp(acc[3], 0f, 1f); int d = (y * dw + x) * 4;
                if (a > 1f / 512)
                {
                    dst[d] = B(acc[0] / a); dst[d + 1] = B(acc[1] / a); dst[d + 2] = B(acc[2] / a);
                }
                else { dst[d] = B(acc[4]); dst[d + 1] = B(acc[5]); dst[d + 2] = B(acc[6]); }
                dst[d + 3] = B(a * 255f);
            }
        });
        return dst;
    }
    static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
