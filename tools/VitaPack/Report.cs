using Shepherd.Assets;

namespace Shepherd.VitaPack;

sealed partial class Baker
{
    static string MB(double b) => $"{b / 1048576.0,8:F2} MiB";

    void Report(PackWriter w)
    {
        Say("");
        Say($"=== pack {Path.Combine(Program.OutDir, VitaPackFormat.PackFileName)} ===");
        Say($"size {MB(w.FileBytes)} = header {w.HeaderBytes} B + object records {MB(w.RecordBytes)} (value trees {MB(w.TreeBytes)}, raw {MB(w.RawBytes)}) + texture images {MB(w.ImageBytes)} + TOC {MB(w.TocBytes)}");
        Say($"objects {_entries.Count} ({_entries.Count(e => !e.Closure)} recorded + {_entries.Count(e => e.Closure)} closure) in {w.Files} files, {w.Bundles} bundles; {w.ShapeCount} struct shapes; strings {MB(w.StringBytes)} ({w.SharedStrings} shared value strings, {w.SharedRefs} shared uses, {w.InlineStrings} inline)");
        Say($"meshes with inlined vertex streams: {MeshesInlined} ({MB(MeshBytesInlined)}); absent PPtr targets recorded: {_absentObjects.Count} objects, {_absentFiles.Count} files");
        Say("");
        Say("bytes by category (records incl. headers / raw / images):");
        foreach (var kv in w.ByCategory.OrderByDescending(k => k.Value.tree + k.Value.raw + k.Value.image))
            Say($"  {kv.Key,-32} n={kv.Value.n,6}  records {MB(kv.Value.tree)}  raw {MB(kv.Value.raw)}  images {MB(kv.Value.image)}");
        // textures
        Say("");
        var drawn = new HashSet<(string, long)>(_rec.TexUse.Keys);
        long gpuAll = 0, gpuDrawn = 0, srcAll = 0;
        int exact = 0, mipDirect = 0, reenc = 0;
        foreach (var t in _tex)
        {
            if (t.Out == null) continue;
            long g = GpuBytes(t); gpuAll += g; srcAll += t.SourceBytes;
            if (drawn.Contains((t.E.File.Name, t.E.Obj.PathId))) gpuDrawn += g;
            if (!t.TopDown) exact++; else if (double.IsNaN(t.Psnr)) mipDirect++; else reenc++;
        }
        var ps = _tex.Where(t => !double.IsNaN(t.Psnr)).Select(t => t.Psnr).ToList();
        Say($"textures: {_tex.Count} ({exact} exact, {mipDirect} DXT blocks taken as stored / from Unity's own mip, {reenc} re-encoded by the in-house encoder); " +
            $"pack images {MB(_tex.Sum(t => (long)(t.Out?.Length ?? 0)))} (source images read: {MB(srcAll)})");
        Say($"GPU bytes if all pack textures were resident: {MB(gpuAll)}; the {drawn.Count} the recorded boot drew: {MB(gpuDrawn)} (exact SDF/LUT ones as their GL upload: alpha8 / RGBA8 / DXT as stored)");
        if (ps.Count > 0) Say($"re-encode quality vs the resampled RGBA: PSNR mean {ps.Average():F1} dB, min {ps.Min():F1} dB, median {ps.OrderBy(x => x).ElementAt(ps.Count / 2):F1} dB (n={ps.Count})");
        Say("");
        Say("drawn textures (recorded boot), largest GPU first:");
        Say($"  {"name",-48} {"source",-14} {"size",-11} -> {"pack",-6} {"size",-10} {"GPU",9}  how");
        foreach (var t in _tex.Where(t => drawn.Contains((t.E.File.Name, t.E.Obj.PathId))).OrderByDescending(GpuBytes))
            Say($"  {Trim(t.Name, 48),-48} {(TextureFormat)t.Fmt,-14} {t.W + "x" + t.H,-11} -> {Fmt(t),-6} {t.OutW + "x" + t.OutH,-10} {MB(GpuBytes(t))}  {t.How}{(double.IsNaN(t.Psnr) ? "" : $" PSNR {t.Psnr:F1}")}");
        Say("");
        Say("exact textures by reason:");
        foreach (var g in _tex.Where(t => t.Exact != null && t.Stub == null).GroupBy(t => t.Exact).OrderByDescending(g => g.Count()))
            Say($"  {g.Count(),5}  {g.Key}  ({MB(g.Sum(t => (long)(t.Out?.Length ?? 0)))})");
        Say("largest exact textures (pack bytes):");
        foreach (var t in _tex.Where(t => t.Exact != null && t.Stub == null && t.Out != null).OrderByDescending(t => t.Out.Length).Take(16))
            Say($"  {Trim(t.Name, 56),-56} {(TextureFormat)t.Fmt,-12} {t.W + "x" + t.H,-10} {MB(t.Out.Length)}  {(drawn.Contains((t.E.File.Name, t.E.Obj.PathId)) ? "drawn" : "not drawn")}  {t.Exact}");
        Say("");
        Say("all textures by source format:");
        foreach (var g in _tex.GroupBy(t => (TextureFormat)t.Fmt).OrderByDescending(g => g.Sum(GpuBytes)))
            Say($"  {g.Key,-14} n={g.Count(),4}  source {MB(g.Sum(t => t.SourceBytes))}  pack {MB(g.Sum(t => (long)(t.Out?.Length ?? 0)))}  GPU {MB(g.Sum(GpuBytes))}");
        var failed = _tex.Where(t => t.How?.StartsWith("FAILED") == true).ToList();
        if (failed.Count > 0) { Say($"FAILED textures: {failed.Count}"); foreach (var t in failed) Say($"  {t.Name}: {t.How}"); }
        var errs = _entries.Where(e => e.Tree == null && e.Raw == null).GroupBy(e => ClassIds.Name(e.Obj.ClassId) + ": " + e.TreeError).OrderByDescending(g => g.Count()).ToList();
        if (errs.Count > 0) { Say("objects stored as index entries only (no tree, no raw):"); foreach (var g in errs.Take(20)) Say($"  {g.Count(),6}  {g.Key}"); }
        Say("");
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    static string Fmt(TexJob t) => t.Out == null ? "-" : t.TopDown ? (t.OutFormat == 10 ? "DXT1" : t.OutFormat == 12 ? "DXT5" : ((TextureFormat)t.OutFormat).ToString()) : "exact";
    /// <summary>What the GL backend keeps resident for this image: DXT as stored; exact Alpha8 = 1 B/px (SDF atlases go up as
    /// GL_ALPHA); exact DXT1/DXT5 as blocks; anything else RGBA8.</summary>
    static long GpuBytes(TexJob t)
    {
        if (t.Out == null) return 0;
        var f = (TextureFormat)t.OutFormat; long px = (long)t.OutW * t.OutH;
        if (f == TextureFormat.DXT1) return (t.OutW + 3) / 4 * ((t.OutH + 3) / 4) * 8L;
        if (f == TextureFormat.DXT5) return (t.OutW + 3) / 4 * ((t.OutH + 3) / 4) * 16L;
        if (!t.TopDown && f is TextureFormat.DXT1Crunched) return (t.OutW + 3) / 4 * ((t.OutH + 3) / 4) * 8L;
        if (!t.TopDown && f is TextureFormat.DXT5Crunched) return (t.OutW + 3) / 4 * ((t.OutH + 3) / 4) * 16L;
        if (f == TextureFormat.Alpha8) return px;
        return px * 4;
    }
}
