using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Render;

/// <summary>SceneRenderer's DXT upload path (SHEPHERD_GL_DXT=1, a backend implementing ICompressedUpload).</summary>
[ProfileOnly]
public static class GlTextures
{
    /// <summary>DXT1/DXT5 asset textures whose pristine blocks are still on disk: upload the blocks (flipped top-down), skip the
    /// CPU decode. Anything else (modified pixels, other formats, heights not a multiple of 4, over the size cap) → Zero.</summary>
    public static IntPtr Compressed(NTexture t, ICompressedUpload cu)
    {
        var sf = t.Src == null ? Shepherd.Assets.TextureFormat.RGBA32 : (Shepherd.Assets.TextureFormat)t.Src.Format;
        bool crunched = sf is Shepherd.Assets.TextureFormat.DXT1Crunched or Shepherd.Assets.TextureFormat.DXT5Crunched;
        int fmt = crunched ? (sf == Shepherd.Assets.TextureFormat.DXT1Crunched ? DxtBlocks.UnityDXT1 : DxtBlocks.UnityDXT5) : (int)sf;
        string why = t.Rgba != null ? "has CPU pixels" : t.Src == null ? "runtime texture" : t.DecodeFailed ? "decode failed" : t.SourceFile is not Shepherd.Assets.SerializedFile ? "no source file"
            : DxtBlocks.BlockBytes(fmt) == 0 ? "format " + sf : !DxtBlocks.CanFlip(fmt, t.Src.Width, t.Src.Height) ? "size not /4"
            : t.Src.Width != t.Width || t.Src.Height != t.Height ? "resized" : null;
        IntPtr h = IntPtr.Zero;
        if (why == null)
            try
            {
                var raw = Shepherd.Assets.TextureDecoder.GetImageData(Assets.Loader.Db, (Shepherd.Assets.SerializedFile)t.SourceFile, t.Src, topMipOnly: true);
                if (crunched)
                {   // crunch unpacks to DXT blocks; the CPU decoder reads every DXT5 variant as plain BC3, and so does this
                    var (bl, cf, cw, ch) = Shepherd.Assets.Crunch.UnpackLevel(raw, 0);
                    fmt = cf == Shepherd.Assets.Crunch.CrnFormat.DXT1 ? DxtBlocks.UnityDXT1 : cf is Shepherd.Assets.Crunch.CrnFormat.DXT5 or Shepherd.Assets.Crunch.CrnFormat.DXT5_AGBR or Shepherd.Assets.Crunch.CrnFormat.DXT5_CCxY
                        or Shepherd.Assets.Crunch.CrnFormat.DXT5_xGBR or Shepherd.Assets.Crunch.CrnFormat.DXT5_xGxR ? DxtBlocks.UnityDXT5 : 0;
                    raw = cw == t.Width && ch == t.Height && fmt != 0 ? bl : null;
                    if (raw == null) why = "crunch payload " + cf;
                }
                if (raw != null)
                {
                    var blocks = DxtBlocks.FlipY(raw, t.Width, t.Height, fmt);
                    h = cu.CreateCompressedTexture(t.Width, t.Height, fmt, blocks, t.FilterMode != 0, () => RenderIcalls.Pixels(t, keep: false));
                    if (h == IntPtr.Zero) why = "over the size cap / upload refused";
                }
            }
            catch (Exception ex) { Log.Warn($"[gl] DXT path '{t.Name}': {ex.Message} -> RGBA"); why = "read failed"; }
        var key = why == null ? $"DXT{(fmt == DxtBlocks.UnityDXT1 ? 1 : 5)} from {sf}" : "RGBA (" + why + ")";
        var c = DxtCensus.TryGetValue(key, out var v) ? v : default; DxtCensus[key] = (c.n + 1, c.px + (long)t.Width * t.Height);
        return h;
    }
    /// <summary>SHEPHERD_GL_DXT=1: which drawn textures went up compressed, and why the others did not (count, texels).</summary>
    public static readonly Dictionary<string, (int n, long px)> DxtCensus = new();
}
