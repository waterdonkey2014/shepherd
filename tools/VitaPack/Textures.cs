using System.Text.RegularExpressions;
using Shepherd.Assets;

namespace Shepherd.VitaPack;

/// <summary>One Texture2D: classification (exact vs transcoded) and its pack image.</summary>
sealed class TexJob
{
    public readonly Entry E; readonly Baker _b;
    public readonly string Name; public readonly int W, H, Fmt, Mips; public readonly TextureDecoder.TextureSource Src;
    public string Exact;
    /// <summary>Stage 1 language trim (Trims.cs): a font atlas the pack's language never draws -> a 4x4 transparent DXT5 image.</summary>
    public string Stub; public long SourceBytesIfExact;
    public byte[] Out; public int OutFormat, OutW, OutH; public bool TopDown; public string How; public double Psnr = double.NaN;
    public long SourceBytes;

    public TexJob(Baker b, Entry e)
    {
        _b = b; E = e; var t = e.Tree;
        Name = t.Str("m_Name") ?? ""; W = t.Int("m_Width"); H = t.Int("m_Height"); Fmt = t.Int("m_TextureFormat"); Mips = Math.Max(1, t.Int("m_MipCount"));
        Src = TextureDecoder.Source(t);
    }

    TextureFormat F => (TextureFormat)Fmt;
    bool DxtFamily => F is TextureFormat.DXT1 or TextureFormat.DXT5 or TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched;
    bool Crunched => F is TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched;
    static int Round4(int v) => (v + 3) & ~3;

    byte[] Data(bool top)
    {
        lock (_b.DbLock) return TextureDecoder.GetImageData(_b.Db, E.File, Src, topMipOnly: top);
    }

    public void Process()
    {
        if (W <= 0 || H <= 0) { Exact ??= "empty (runtime-filled)"; How = "no image"; return; }
        if (Stub != null)
        {   // one all-zero DXT5 block: alpha 0, colour 0. The tree keeps m_Width/m_Height/m_TextureFormat (the game and TMP see the
            // real atlas); the GPU image is reduced, as for every transcoded texture, and the SDF upload keeps its (zero) alpha
            SourceBytesIfExact = TextureDecoder.Mip0Size(F, W, H);
            Out = new byte[16]; OutFormat = 12; OutW = 4; OutH = 4; TopDown = true; How = "stub: " + Stub; return;
        }
        if (Exact != null)
        {
            Out = Data(top: true); SourceBytes = Out.Length;
            OutFormat = Fmt; OutW = W; OutH = H; TopDown = false; How = "exact: " + Exact; return;
        }
        int k = Math.Min(W, H) >= 32 ? 1 : 0;
        while (Math.Max(W >> k, H >> k) > Program.MaxSide) k++;
        int dw = Math.Max(1, W >> k), dh = Math.Max(1, H >> k);
        var all = Data(top: false); SourceBytes = all.Length;
        int levels = Mips;
        if (Crunched) levels = Math.Min(levels, Crunch.ReadHeader(all).Levels);
        if (DxtFamily && levels > k && dw % 4 == 0 && dh % 4 == 0 && TryBlocks(all, k, out var blocks, out bool bc3))
        {   // Unity's own mip k, still in its block format: no RGBA round trip
            Out = DxtEncoder.FlipY(blocks, dw, dh, bc3); OutFormat = bc3 ? 12 : 10; OutW = dw; OutH = dh; TopDown = true;
            How = k == 0 ? $"{F} blocks as stored, flipped" : $"{F} mip {k} blocks as stored (no re-encode), flipped"; return;
        }
        if (k == 0) { Exact = "small, kept as stored"; Out = Data(top: true); OutFormat = Fmt; OutW = W; OutH = H; TopDown = false; How = "exact: " + Exact; return; }
        int tw = Round4(dw), th = Round4(dh);
        int j = Math.Min(k, levels - 1);
        while (j > 0 && ((W >> j) < tw || (H >> j) < th)) j--;
        int sw = Math.Max(1, W >> j), sh = Math.Max(1, H >> j);
        var rgba = DecodeLevel(all, j, sw, sh);
        bool resampled = sw != tw || sh != th;
        if (resampled) rgba = Resampler.Resize(rgba, sw, sh, tw, th);
        bool opaque = true; for (int i = 3; i < rgba.Length && opaque; i += 4) opaque = rgba[i] == 255;
        Out = DxtEncoder.Encode(rgba, tw, th, bc3: !opaque); OutFormat = opaque ? 10 : 12; OutW = tw; OutH = th; TopDown = true;
        Psnr = PsnrOf(rgba, Out, tw, th, opaque);
        How = $"{F} {(j > 0 ? $"mip {j}" : "mip 0")} {(resampled ? $"Kaiser {sw}x{sh}->{tw}x{th}" : "as is")} -> {(opaque ? "DXT1" : "DXT5")} (in-house encoder)";
    }

    bool TryBlocks(byte[] all, int level, out byte[] blocks, out bool bc3)
    {
        blocks = null; bc3 = false;
        if (Crunched)
        {
            var (bl, cf, cw, ch) = Crunch.UnpackLevel(all, level);
            if (cw != Math.Max(1, W >> level) || ch != Math.Max(1, H >> level)) return false;
            if (cf == Crunch.CrnFormat.DXT1) { blocks = bl; return true; }
            if (cf is Crunch.CrnFormat.DXT5 or Crunch.CrnFormat.DXT5_AGBR or Crunch.CrnFormat.DXT5_CCxY or Crunch.CrnFormat.DXT5_xGBR or Crunch.CrnFormat.DXT5_xGxR) { blocks = bl; bc3 = true; return true; }
            return false;
        }
        long off = 0; for (int i = 0; i < level; i++) off += TextureDecoder.Mip0Size(F, Math.Max(1, W >> i), Math.Max(1, H >> i));
        long len = TextureDecoder.Mip0Size(F, Math.Max(1, W >> level), Math.Max(1, H >> level));
        if (off + len > all.Length) return false;
        blocks = all.AsSpan((int)off, (int)len).ToArray(); bc3 = F == TextureFormat.DXT5; return true;
    }

    byte[] DecodeLevel(byte[] all, int level, int w, int h)
    {
        if (Crunched)
        {
            var (bl, cf, cw, ch) = Crunch.UnpackLevel(all, level);
            var tf = cf switch
            {
                Crunch.CrnFormat.DXT1 => TextureFormat.DXT1, Crunch.CrnFormat.DXT5A => TextureFormat.BC4,
                Crunch.CrnFormat.DXN_XY or Crunch.CrnFormat.DXN_YX => TextureFormat.BC5, _ => TextureFormat.DXT5,
            };
            return TextureDecoder.Decode(bl, cw, ch, tf);
        }
        long off = 0; for (int i = 0; i < level; i++) off += TextureDecoder.Mip0Size(F, Math.Max(1, W >> i), Math.Max(1, H >> i));
        return TextureDecoder.Decode(all.AsSpan((int)off), w, h, F);
    }

    static double PsnrOf(byte[] reference, byte[] blocks, int w, int h, bool opaque)
    {
        var dec = TextureDecoder.Decode(blocks, w, h, opaque ? TextureFormat.DXT1 : TextureFormat.DXT5);
        TextureDecoder.FlipRows(dec, w, h);   // Decode assumes Unity's bottom-up rows; the blocks are top-down
        double se = 0; int n = 0;
        for (int i = 0; i < dec.Length; i++) { if (opaque && (i & 3) == 3) continue; double d = dec[i] - reference[i]; se += d * d; n++; }
        double mse = se / Math.Max(1, n);
        return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}

sealed partial class Baker
{
    readonly List<TexJob> _tex = new();
    public IReadOnlyList<TexJob> TexJobs => _tex;
    static readonly Regex StylizerScript = new("Stylizer|Palette|Dither|Pattern", RegexOptions.IgnoreCase);

    void Textures()
    {
        _tex.AddRange(_entries.Where(e => e.Tex != null).Select(e => e.Tex));
        var byObj = _tex.ToDictionary(t => (t.E.File, t.E.Obj.PathId));
        void Mark(SerializedFile from, PPtr p, string why)
        {
            if (p.IsNull) return;
            var (rf, ro) = _db.Resolve(from, p);
            if (!ro.IsNull && byObj.TryGetValue((rf, ro.PathId), out var t)) t.Exact ??= why;
        }
        // recorded uses
        foreach (var t in _tex)
            if (_rec.TexUse.TryGetValue((t.E.File.Name, t.E.Obj.PathId), out var use))
            {
                // "sdf" alone is not a reason: the engine also draws TextMeshPro/Sprite icon atlases through its SDF path
                // (its test is the shader name containing "TextMeshPro"); real font atlases are caught by the rules below
                if (use.Contains("cpu")) t.Exact ??= "pixels read on the CPU (Stylizer LUT/pattern)";
            }
        // static rules for textures the boot never drew
        var scripts = new Dictionary<string, int>();
        foreach (var e in _entries)
        {
            if (e.Tree == null) continue;
            if (e.Obj.ClassId == ClassIds.MonoBehaviour)
            {
                var name = _db.GetMonoScript(e.File, e.Obj)?.ClassName ?? "";
                if (name == "TMP_FontAsset")
                {
                    if (e.Tree["m_AtlasTextures"] is List<object> l) foreach (var p in l.OfType<PPtr>()) Mark(e.File, p, "TMP font atlas");
                    if (e.Tree["atlas"] is PPtr ap) Mark(e.File, ap, "TMP font atlas");
                    if (StubReason(e.Tree.Str("m_Name") ?? "") is { } why)
                    {
                        var ps = (e.Tree["m_AtlasTextures"] as List<object> ?? new List<object>()).OfType<PPtr>().ToList();
                        if (e.Tree["atlas"] is PPtr ap2) ps.Add(ap2);
                        foreach (var p in ps) { if (p.IsNull) continue; var (rf, ro) = _db.Resolve(e.File, p); if (!ro.IsNull && byObj.TryGetValue((rf, ro.PathId), out var st)) st.Stub ??= why; }
                    }
                }
                else if (StylizerScript.IsMatch(name))
                {
                    var ps = new List<PPtr>(); TypeTreeReader.CollectPPtrs(e.Tree, ps);
                    foreach (var p in ps) Mark(e.File, p, $"referenced by {name} (Stylizer palette/pattern)");
                    scripts[name] = scripts.GetValueOrDefault(name) + 1;
                }
            }
            else if (e.Obj.ClassId == 128 && e.Tree["m_Texture"] is PPtr fp) Mark(e.File, fp, "font texture");
            else if (e.Obj.ClassId == 21 && e.Tree["m_Shader"] is PPtr sp && _db.ResolveRead(e.File, sp).data is { } sh &&
                     ((sh["m_ParsedForm"] as TypeTreeObject)?.Str("m_Name") ?? sh.Str("m_Name") ?? "") is var sn && sn.Contains("Distance Field"))
            {
                if (e.Tree["m_SavedProperties"] is TypeTreeObject props && props["m_TexEnvs"] is List<object> envs)
                    foreach (var te in envs.OfType<TypeTreeObject>()) if (te["second"] is TypeTreeObject s && s["m_Texture"] is PPtr tp) Mark(e.File, tp, $"texture of a TMP material ({sn})");
            }
        }
        foreach (var t in _tex)
        {
            if (t.Exact != null) continue;
            if ((TextureFormat)t.Fmt == TextureFormat.Alpha8) t.Exact = "Alpha8 (masks / SDF atlases), kept exact";
            else if (!TextureDecoder.IsSupported((TextureFormat)t.Fmt)) t.Exact = "format the engine cannot decode, kept as stored";
        }
        int k11Luts = K11ExactLuts();   // kit #11 gpu2 (K11Exact.cs): LUT strips kept as stored
        if (k11Luts > 0) Say($"kit #11: {k11Luts} colour-grading LUT strip(s) kept as stored (SHEPHERD_K11_PACK_LUT=0 = kit #10's classification)");
        Say($"textures: {_tex.Count}; exact (not downscaled, not re-encoded): {_tex.Count(t => t.Exact != null)}; Stylizer-ish scripts seen: {string.Join(", ", scripts.Select(kv => kv.Key + " x" + kv.Value))}");
        foreach (var t in _tex.Where(t => t.Exact == null && _rec.TexUse.TryGetValue((t.E.File.Name, t.E.Obj.PathId), out var u) && u.Contains("sdf")))
            Say($"  note: '{t.Name}' ({(TextureFormat)t.Fmt} {t.W}x{t.H}) is drawn through the engine's SDF path but is not a font atlas (TextMeshPro/Sprite icons): transcoded; its GL upload keeps the alpha of the reduced image");
        int done = 0;
        Parallel.ForEach(_tex.OrderByDescending(t => (long)t.W * t.H), new ParallelOptions { MaxDegreeOfParallelism = Program.Jobs }, t =>
        {
            try { t.Process(); }
            catch (Exception ex)
            {
                Say($"  WARNING texture '{t.Name}' {t.W}x{t.H} {(TextureFormat)t.Fmt}: {ex.Message} -> kept exact");
                try { t.Exact = "transcode failed, kept as stored"; t.Process(); } catch (Exception ex2) { t.How = "FAILED: " + ex2.Message; }
            }
            int n = Interlocked.Increment(ref done); if (n % 50 == 0) Console.WriteLine($"  textures {n}/{_tex.Count}");
        });
    }
}
