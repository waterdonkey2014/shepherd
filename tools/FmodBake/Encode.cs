using System.Diagnostics;
using System.Text;

namespace Shepherd.FmodBake;

/// <summary>
/// Take containers (fully specified in vita/notes/sound-bake.md §Containers):
///  - pcm16:     canonical RIFF/WAVE, exactly 44-byte header ('fmt ' 16 B, format 1), then interleaved s16le.
///  - ima-adpcm: RIFF/WAVE, exactly 60-byte header ('fmt ' 20 B: format 0x11, cbSize 2, samplesPerBlock; 'fact' 4 B = frames;
///               'data'), Microsoft IMA ADPCM blocks: blockAlign = 1024 * channels, 2041 frames per block.
///  - aac-adts:  raw ADTS AAC-LC from macOS afconvert; the decoded stream starts with EncoderDelay priming frames.
/// </summary>
static class Encode
{
    public const int ImaBlockPerChannel = 1024;
    public static int ImaFramesPerBlock(int ch) => (ImaBlockPerChannel * ch - 4 * ch) * 8 / (4 * ch) + 1;   // 2041

    public static short[] ToPcm16(float[] x)
    {
        var o = new short[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double v = Math.Round(x[i] * 32767.0);
            o[i] = (short)Math.Clamp(v, -32768, 32767);
        }
        return o;
    }

    public static int ClipCount(float[] x) { int c = 0; foreach (var v in x) if (v > 1f || v < -1f) c++; return c; }

    public static byte[] WavPcm16(short[] pcm, int ch, int rate)
    {
        using var ms = new MemoryStream(44 + pcm.Length * 2);
        using var w = new BinaryWriter(ms);
        int data = pcm.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate);
        w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] WavFloat32(float[] x, int ch, int rate)
    {
        using var ms = new MemoryStream(44 + x.Length * 4);
        using var w = new BinaryWriter(ms);
        int data = x.Length * 4;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)3); w.Write((short)ch); w.Write(rate);
        w.Write(rate * ch * 4); w.Write((short)(ch * 4)); w.Write((short)32);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        foreach (var s in x) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Reads PCM16 / float32 / IMA ADPCM WAV (any chunk order) back to float, for verification.</summary>
    public static (float[] pcm, int ch, int rate, int frames, int format) ReadWav(byte[] b)
    {
        if (Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") throw new InvalidDataException("not RIFF/WAVE");
        int p = 12, fmt = 0, ch = 0, rate = 0, bits = 0, align = 0, spb = 0, fact = -1, dataOff = -1, dataLen = 0;
        while (p + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, p, 4); int len = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ")
            {
                fmt = BitConverter.ToUInt16(b, p + 8); ch = BitConverter.ToUInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12);
                align = BitConverter.ToUInt16(b, p + 20); bits = BitConverter.ToUInt16(b, p + 22);
                if (fmt == 0x11 && len >= 20) spb = BitConverter.ToUInt16(b, p + 26);
                if (fmt == 0xFFFE && len >= 40) fmt = BitConverter.ToUInt16(b, p + 8 + 24);   // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID's first 2 bytes
            }
            else if (id == "fact") fact = BitConverter.ToInt32(b, p + 8);
            else if (id == "data") { dataOff = p + 8; dataLen = len; }
            p += 8 + len + (len & 1);
        }
        if (dataOff < 0) throw new InvalidDataException("no data chunk");
        float[] o;
        if (fmt == 1 && bits == 16)
        {
            o = new float[dataLen / 2];
            for (int i = 0; i < o.Length; i++) o[i] = BitConverter.ToInt16(b, dataOff + 2 * i) / 32767f;
        }
        else if (fmt == 3 && bits == 32)
        {
            o = new float[dataLen / 4];
            for (int i = 0; i < o.Length; i++) o[i] = BitConverter.ToSingle(b, dataOff + 4 * i);
        }
        else if (fmt == 0x11) o = ImaDecode(b.AsSpan(dataOff, dataLen), ch, align, spb, fact);
        else throw new InvalidDataException($"unsupported WAV format {fmt}/{bits}");
        return (o, ch, rate, o.Length / Math.Max(1, ch), fmt);
    }

    // ------------------------------------------------------------------ IMA ADPCM (Microsoft block layout)

    static readonly int[] StepTable = {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132,
        7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };
    static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    struct ImaState { public int Pred, Index; }

    static int ImaDecodeNibble(ref ImaState s, int nib)
    {
        int step = StepTable[s.Index];
        int diff = step >> 3;
        if ((nib & 4) != 0) diff += step;
        if ((nib & 2) != 0) diff += step >> 1;
        if ((nib & 1) != 0) diff += step >> 2;
        if ((nib & 8) != 0) diff = -diff;
        s.Pred = Math.Clamp(s.Pred + diff, -32768, 32767);
        s.Index = Math.Clamp(s.Index + IndexTable[nib], 0, 88);
        return s.Pred;
    }

    static int ImaEncodeNibble(ref ImaState s, int sample)
    {
        int step = StepTable[s.Index];
        int diff = sample - s.Pred, nib = 0;
        if (diff < 0) { nib = 8; diff = -diff; }
        if (diff >= step) { nib |= 4; diff -= step; }
        if (diff >= step >> 1) { nib |= 2; diff -= step >> 1; }
        if (diff >= step >> 2) { nib |= 1; }
        ImaDecodeNibble(ref s, nib);   // track the decoder exactly
        return nib;
    }

    /// <summary>Encodes interleaved s16 to Microsoft IMA ADPCM blocks; the last block is zero-padded (fact = true frames).
    /// Per block and channel the header carries the exact first sample and the carried-over step index.</summary>
    public static byte[] ImaWav(short[] pcm, int ch, int rate)
    {
        int frames = pcm.Length / ch, spb = ImaFramesPerBlock(ch), align = ImaBlockPerChannel * ch;
        int blocks = (frames + spb - 1) / spb;
        var data = new byte[blocks * align];
        var st = new ImaState[ch];
        // initial index: pick from the first frames' typical step so the first block does not spend samples ramping up
        for (int c = 0; c < ch; c++) st[c].Index = InitialIndex(pcm, ch, c);
        for (int bI = 0; bI < blocks; bI++)
        {
            int f0 = bI * spb, o = bI * align;
            for (int c = 0; c < ch; c++)
            {
                short first = Sample(pcm, ch, c, f0, frames);
                st[c].Pred = first;
                BitConverter.GetBytes(first).CopyTo(data, o + 4 * c);
                data[o + 4 * c + 2] = (byte)st[c].Index; data[o + 4 * c + 3] = 0;
            }
            int p = o + 4 * ch;
            for (int g = 0; g < (spb - 1) / 8; g++)
                for (int c = 0; c < ch; c++)
                {
                    for (int k = 0; k < 8; k += 2)
                    {
                        int fa = f0 + 1 + g * 8 + k;
                        int lo = ImaEncodeNibble(ref st[c], Sample(pcm, ch, c, fa, frames));
                        int hi = ImaEncodeNibble(ref st[c], Sample(pcm, ch, c, fa + 1, frames));
                        data[p++] = (byte)(lo | (hi << 4));
                    }
                }
        }
        using var ms = new MemoryStream(60 + data.Length);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(52 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(20); w.Write((short)0x11); w.Write((short)ch); w.Write(rate);
        w.Write((int)((long)rate * align / spb)); w.Write((short)align); w.Write((short)4); w.Write((short)2); w.Write((short)spb);
        w.Write(Encoding.ASCII.GetBytes("fact")); w.Write(4); w.Write(frames);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    static short Sample(short[] pcm, int ch, int c, int f, int frames) => f < frames ? pcm[f * ch + c] : (short)0;

    static int InitialIndex(short[] pcm, int ch, int c)
    {
        int frames = pcm.Length / ch, n = Math.Min(frames, 64);
        double d = 0; for (int f = 1; f < n; f++) d += Math.Abs(pcm[f * ch + c] - pcm[(f - 1) * ch + c]);
        d = n > 1 ? d / (n - 1) : 0;
        int idx = 0; while (idx < 88 && StepTable[idx] < d) idx++;
        return idx;
    }

    public static float[] ImaDecode(ReadOnlySpan<byte> data, int ch, int align, int spb, int frames)
    {
        int blocks = data.Length / align;
        if (frames < 0) frames = blocks * spb;
        var o = new float[frames * ch];
        var st = new ImaState[ch];
        for (int bI = 0; bI < blocks; bI++)
        {
            int f0 = bI * spb, p = bI * align;
            for (int c = 0; c < ch; c++)
            {
                st[c].Pred = BitConverter.ToInt16(data.Slice(p + 4 * c, 2));
                st[c].Index = Math.Clamp((int)data[p + 4 * c + 2], 0, 88);
                if (f0 < frames) o[f0 * ch + c] = st[c].Pred / 32767f;
            }
            int q = p + 4 * ch;
            for (int g = 0; g < (spb - 1) / 8; g++)
                for (int c = 0; c < ch; c++)
                    for (int k = 0; k < 8; k += 2)
                    {
                        byte by = data[q++];
                        int fa = f0 + 1 + g * 8 + k;
                        int a = ImaDecodeNibble(ref st[c], by & 15), b2 = ImaDecodeNibble(ref st[c], by >> 4);
                        if (fa < frames) o[fa * ch + c] = a / 32767f;
                        if (fa + 1 < frames) o[(fa + 1) * ch + c] = b2 / 32767f;
                    }
        }
        return o;
    }

    // ------------------------------------------------------------------ AAC (ADTS) via macOS afconvert (built in)

    public static string Tmp = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();

    /// <summary>Float PCM → ADTS AAC-LC at the given bitrate. afconvert only converts files; it never plays anything.</summary>
    public static byte[] AacAdts(float[] x, int ch, int rate, int bitrate)
    {
        Directory.CreateDirectory(Tmp);
        string id = Guid.NewGuid().ToString("N");
        string inp = Path.Combine(Tmp, $"fb-{id}.wav"), outp = Path.Combine(Tmp, $"fb-{id}.aac");
        try
        {
            File.WriteAllBytes(inp, WavFloat32(x, ch, rate));
            Run("/usr/bin/afconvert", "-f", "adts", "-d", "aac", "-b", bitrate.ToString(), "-q", "127", inp, outp);
            return File.ReadAllBytes(outp);
        }
        finally { TryDelete(inp); TryDelete(outp); }
    }

    /// <summary>ADTS → float PCM with Apple's decoder (verification only: delay measurement and quality checks).</summary>
    public static (float[] pcm, int ch) DecodeAac(byte[] adts)
    {
        string id = Guid.NewGuid().ToString("N");
        string inp = Path.Combine(Tmp, $"fb-{id}.aac"), outp = Path.Combine(Tmp, $"fb-{id}-dec.wav");
        try
        {
            File.WriteAllBytes(inp, adts);
            Run("/usr/bin/afconvert", "-f", "WAVE", "-d", "LEF32", inp, outp);
            var (pcm, ch, _, _, _) = ReadWav(File.ReadAllBytes(outp));
            return (pcm, ch);
        }
        finally { TryDelete(inp); TryDelete(outp); }
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TMPDIR"] = Tmp;
        using var p = Process.Start(psi);
        string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} exit {p.ExitCode}: {err.Trim()}");
    }

    /// <summary>ADTS frame count (each frame = 1024 PCM frames for AAC-LC) and a sanity check of the stream.</summary>
    public static (int frames, int sampleRateIndex, int channels, int profile) AdtsInfo(byte[] a)
    {
        int p = 0, n = 0, sri = -1, chc = -1, prof = -1;
        while (p + 7 <= a.Length)
        {
            if (a[p] != 0xFF || (a[p + 1] & 0xF0) != 0xF0) throw new InvalidDataException($"ADTS sync lost at {p}");
            prof = (a[p + 2] >> 6) & 3; sri = (a[p + 2] >> 2) & 15; chc = ((a[p + 2] & 1) << 2) | (a[p + 3] >> 6);
            int len = ((a[p + 3] & 3) << 11) | (a[p + 4] << 3) | (a[p + 5] >> 5);
            if (len < 7) throw new InvalidDataException("bad ADTS frame length");
            p += len; n++;
        }
        if (p != a.Length) throw new InvalidDataException("trailing bytes after the last ADTS frame");
        return (n, sri, chc, prof);
    }
}
