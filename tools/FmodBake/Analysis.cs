namespace Shepherd.FmodBake;

/// <summary>Objective signal checks (nothing here is ever played): peaks, RMS, envelopes, similarity, loop search.</summary>
static class Analysis
{
    public static float Peak(float[] x, int from = 0, int to = -1)
    {
        if (to < 0) to = x.Length;
        float p = 0; for (int i = from; i < to; i++) { float a = Math.Abs(x[i]); if (a > p) p = a; }
        return p;
    }

    public static double Energy(float[] x, int from = 0, int to = -1)
    {
        if (to < 0) to = x.Length;
        double e = 0; for (int i = from; i < to; i++) e += x[i] * (double)x[i];
        return e;
    }

    public static double Rms(float[] x) => x.Length == 0 ? 0 : Math.Sqrt(Energy(x) / x.Length);
    public static double Db(double v) => v <= 1e-12 ? -240 : 20 * Math.Log10(v);

    /// <summary>First stereo frame whose |sample| exceeds thr (frames), or -1.</summary>
    public static int FirstAbove(float[] st, float thr)
    {
        for (int i = 0; i < st.Length; i++) if (Math.Abs(st[i]) > thr) return i / 2;
        return -1;
    }

    /// <summary>One past the last stereo frame whose |sample| exceeds thr (frames), 0 if none.</summary>
    public static int LastAbove(float[] st, float thr)
    {
        for (int i = st.Length - 1; i >= 0; i--) if (Math.Abs(st[i]) > thr) return i / 2 + 1;
        return 0;
    }

    public static float[] Slice(float[] st, int fromFrame, int frames)
    {
        var a = new float[frames * 2];
        int avail = Math.Max(0, Math.Min(frames, st.Length / 2 - fromFrame));
        if (avail > 0) Array.Copy(st, fromFrame * 2, a, 0, avail * 2);
        return a;
    }

    /// <summary>L and R identical to -60 dB (side energy / mid energy) → the take is stored mono.</summary>
    public static bool IsMono(float[] st)
    {
        double side = 0, mid = 0;
        for (int i = 0; i + 1 < st.Length; i += 2)
        {
            double l = st[i], r = st[i + 1];
            side += (l - r) * (l - r); mid += (l + r) * (l + r);
        }
        return mid <= 1e-20 || side / mid < 1e-6;
    }

    /// <summary>Side (L-R) energy relative to mid (L+R) energy, dB; -240 = identical channels.</summary>
    public static double SideToMidDb(float[] st)
    {
        double side = 0, mid = 0;
        for (int i = 0; i + 1 < st.Length; i += 2) { double l = st[i], r = st[i + 1]; side += (l - r) * (l - r); mid += (l + r) * (l + r); }
        return mid <= 1e-20 ? -240 : 10 * Math.Log10(Math.Max(side / mid, 1e-24));
    }

    public static float[] ToMono(float[] st)
    {
        var m = new float[st.Length / 2];
        for (int i = 0; i < m.Length; i++) m[i] = 0.5f * (st[2 * i] + st[2 * i + 1]);
        return m;
    }

    /// <summary>Residual energy of b against a over the common length, relative to a's energy (dB). Length mismatch beyond
    /// the common part counts as residual. -240 = identical.</summary>
    public static double DiffDb(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double ea = 0, ed = 0;
        for (int i = 0; i < n; i++) { ea += a[i] * (double)a[i]; double d = a[i] - b[i]; ed += d * d; }
        for (int i = n; i < a.Length; i++) { ea += a[i] * (double)a[i]; ed += a[i] * (double)a[i]; }
        for (int i = n; i < b.Length; i++) ed += b[i] * (double)b[i];
        if (ea <= 1e-20) return ed <= 1e-20 ? -240 : 0;
        return 10 * Math.Log10(Math.Max(ed / ea, 1e-24));
    }

    /// <summary>RMS envelope in dB per window (frames), stereo input.</summary>
    public static double[] EnvelopeDb(float[] st, int win)
    {
        int frames = st.Length / 2, n = (frames + win - 1) / win;
        var env = new double[n];
        for (int w = 0; w < n; w++)
        {
            int a = w * win, b = Math.Min(frames, a + win);
            double e = 0; for (int f = a; f < b; f++) { double l = st[2 * f], r = st[2 * f + 1]; e += 0.5 * (l * l + r * r); }
            env[w] = Db(Math.Sqrt(e / Math.Max(1, b - a)));
        }
        return env;
    }

    /// <summary>Normalised correlation of x[a..a+n) with x[b..b+n) on a mono mix (stereo input, frames).</summary>
    public static double Corr(float[] st, int a, int b, int n)
    {
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < n; i++)
        {
            double xa = st[2 * (a + i)] + st[2 * (a + i) + 1], xb = st[2 * (b + i)] + st[2 * (b + i) + 1];
            sab += xa * xb; saa += xa * xa; sbb += xb * xb;
        }
        return saa <= 1e-20 || sbb <= 1e-20 ? 0 : sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>Residual (dB) of x[b..b+n) against x[a..a+n), stereo, frames.</summary>
    public static double SegDiffDb(float[] st, int a, int b, int n)
    {
        double ea = 0, ed = 0;
        for (int i = 0; i < 2 * n; i++) { double xa = st[2 * a + i], xb = st[2 * b + i]; ea += xa * xa; ed += (xa - xb) * (xa - xb); }
        if (ea <= 1e-20) return ed <= 1e-20 ? -240 : 0;
        return 10 * Math.Log10(Math.Max(ed / ea, 1e-24));
    }

    // ------------------------------------------------------------------ loop search

    public sealed record LoopFind(bool Exact, int Start, int End, double ResidualDb, int Period);

    /// <summary>
    /// Exact periodicity search: the signal from some frame s on repeats with period P (x[t] == x[t+P], residual below
    /// -50 dB over a window). Candidates for P come from (1) the event's timeline length and (2) the peak of the
    /// autocorrelation of a decimated mono mix; each is refined to the frame. Returns the earliest s that repeats.
    /// </summary>
    public static LoopFind FindExactLoop(float[] st, int timelineFrames, int minPeriod)
    {
        int frames = st.Length / 2;
        int win = Math.Min(Session.Rate, frames / 8);           // comparison window
        if (frames < minPeriod * 2 + win) return null;
        var cands = new List<int>();
        if (timelineFrames >= minPeriod && (long)timelineFrames * 2 + win <= frames) cands.Add(timelineFrames);
        cands.AddRange(AutocorrPeaks(st, minPeriod, (frames - win) / 2, 4));
        LoopFind best = null;
        foreach (int c0 in cands.Distinct())
        {
            // refine: test P in c0 ± 64 frames at full rate on the last window
            int bestP = -1; double bestD = 0;
            int tEnd = frames - win;                 // compare x[tEnd-P .. +win) with x[tEnd .. +win)
            for (int P = Math.Max(minPeriod, c0 - 64); P <= c0 + 64; P++)
            {
                if (tEnd - P < 0) break;
                double d = SegDiffDb(st, tEnd - P, tEnd, win);
                if (bestP < 0 || d < bestD) { bestD = d; bestP = P; }
            }
            if (bestP < 0 || bestD > -50) continue;
            // earliest start s where x[s..s+win) == x[s+P..): scan back in steps of win/4, then refine to the frame
            int s = tEnd - bestP;
            int step = Math.Max(256, win / 4);
            while (s - step >= 0 && SegDiffDb(st, s - step, s - step + bestP, win) < -50) s -= step;
            int lo = Math.Max(0, s - step), hi = s;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (SegDiffDb(st, mid, mid + bestP, Math.Min(win, 4096)) < -50) hi = mid; else lo = mid + 1;
            }
            s = lo;
            // safety margin: the earliest start passes the window test while a decaying intro tail can still differ by a
            // few samples at s itself; 4096 frames later the repeat is exact at the seam too (file grows by 85 ms)
            if (s + 4096 + bestP + 4096 <= frames) s += 4096;
            if (s + bestP > frames) continue;
            double resid = SegDiffDb(st, s, s + bestP, Math.Min(frames - s - bestP, bestP));
            if (resid > -50) continue;
            var f = new LoopFind(true, s, s + bestP, resid, bestP);
            if (best == null || f.End < best.End) best = f;
        }
        return best;
    }

    /// <summary>Lags (frames) of the strongest autocorrelation peaks of a 1/16-decimated mono mix, in [minLag, maxLag].</summary>
    static List<int> AutocorrPeaks(float[] st, int minLag, int maxLag, int count)
    {
        const int D = 16;
        int frames = st.Length / 2, n = frames / D;
        var x = new double[n];
        for (int i = 0; i < n; i++) { double s = 0; for (int k = 0; k < D; k++) { int f = i * D + k; s += st[2 * f] + st[2 * f + 1]; } x[i] = s / D; }
        int lo = Math.Max(1, minLag / D), hi = Math.Min(n - 1, maxLag / D);
        if (hi <= lo) return new List<int>();
        // FFT autocorrelation
        int m = 1; while (m < 2 * n) m <<= 1;
        var re = new double[m]; var im = new double[m];
        double mean = x.Average();
        for (int i = 0; i < n; i++) re[i] = x[i] - mean;
        Fft(re, im, false);
        for (int i = 0; i < m; i++) { re[i] = re[i] * re[i] + im[i] * im[i]; im[i] = 0; }
        Fft(re, im, true);
        var ac = new double[hi + 2];
        for (int l = 0; l <= hi + 1 && l < m; l++) ac[l] = re[l] / Math.Max(1, n - l);   // unbiased
        var peaks = new List<(int lag, double v)>();
        for (int l = lo + 1; l < hi; l++) if (ac[l] > ac[l - 1] && ac[l] >= ac[l + 1] && ac[l] > 0) peaks.Add((l, ac[l]));
        return peaks.OrderByDescending(p => p.v).Take(count).Select(p => p.lag * D).ToList();
    }

    public static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double ur = re[a], ui = im[a];
                    double vr = re[b] * cr - im[b] * ci, vi = re[b] * ci + im[b] * cr;
                    re[a] = ur + vr; im[a] = ui + vi; re[b] = ur - vr; im[b] = ui - vi;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }

    /// <summary>
    /// Crossfaded loop for content that does not repeat exactly (random/async instruments): the file is x[0..B0+M) with the
    /// loop [B0, B0+M); its last F frames are blended with x[B0-F..B0), the material that naturally precedes x[B0], so the
    /// jump from the loop end back to B0 continues the signal (equal-power fade). Returns the new buffer + loop frames.
    /// </summary>
    public static (float[] pcm, int start, int end) CrossfadeLoop(float[] st, int b0, int b1, int fade)
    {
        int M = b1 - b0 - fade;
        if (b0 < fade || M <= fade) throw new ArgumentException("loop too short for the crossfade");
        var o = new float[(b0 + M) * 2];
        Array.Copy(st, 0, o, 0, o.Length);
        for (int k = 0; k < fade; k++)
        {
            double t = (k + 0.5) / fade;
            double gOut = Math.Cos(t * Math.PI / 2), gIn = Math.Sin(t * Math.PI / 2);
            int dst = b0 + M - fade + k, src = b0 - fade + k;
            for (int c = 0; c < 2; c++) o[2 * dst + c] = (float)(st[2 * dst + c] * gOut + st[2 * src + c] * gIn);
        }
        return (o, b0, b0 + M);
    }

    /// <summary>Loop seam check: the sample step across the jump (last loop frame → loop start) relative to the larger of the
    /// natural step into the loop start and the 95th percentile of the steps in the 2048 frames on either side of the seam. ≤ 1 = the seam is no larger than the content's
    /// own large steps (inaudible as a click); much larger = a discontinuity.</summary>
    public static double SeamRatio(float[] st, int start, int end)
    {
        int frames = st.Length / 2;
        if (start <= 0 || end > frames || end - start < 8) return double.NaN;
        var steps = new List<double>();
        void Collect(int a, int b) { for (int f = Math.Max(a, 1); f < b; f++) for (int c = 0; c < 2; c++) steps.Add(Math.Abs(st[2 * f + c] - st[2 * (f - 1) + c])); }
        Collect(start + 1, Math.Min(end, start + 2048));
        Collect(Math.Max(start + 1, end - 2048), end);
        if (steps.Count == 0) return double.NaN;
        steps.Sort();
        double p95 = steps[(int)(steps.Count * 0.95)];
        // the jump lands on x[start]; its natural predecessor is x[start-1], so a loop that starts on a transient is compared
        // with that transient's own step, not only with the surrounding content
        double seam = 0, natural = 0;
        for (int c = 0; c < 2; c++)
        {
            seam = Math.Max(seam, Math.Abs(st[2 * start + c] - st[2 * (end - 1) + c]));
            natural = Math.Max(natural, Math.Abs(st[2 * start + c] - st[2 * (start - 1) + c]));
        }
        double denom = Math.Max(p95, natural);
        return denom <= 1e-9 ? (seam <= 1e-6 ? 0 : double.PositiveInfinity) : seam / denom;
    }

}
