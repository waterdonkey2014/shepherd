using System.Buffers.Binary;

namespace Shepherd.Assets;

/// <summary>
/// CPU decode of Texture2D payloads to RGBA32. Output rows are top-down (Unity stores bottom-up; we flip),
/// alpha is straight as stored. Single-channel formats follow GPU sampling semantics: R8/R16/BC4/RHalf/RFloat
/// give (r,0,0,255), RG formats/BC5 give (r,g,0,255); Alpha8 gives (255,255,255,a).
/// </summary>
public static class TextureDecoder
{
    /// <summary>Everything a Texture2D decode reads from its type tree (so the tree itself need not be kept):
    /// size, format, inline "image data" (null/empty when streamed) and the m_StreamData range.</summary>
    public sealed record TextureSource(int Width, int Height, int Format, byte[] Inline, string StreamPath, long StreamOffset, long StreamSize)
    {
        public long EncodedSize => Image != null ? Image.Length : Inline is { Length: > 0 } ? Inline.Length : StreamSize;
        /// <summary>V3 pack: the image as stored in the pack (exact original mip 0, or GPU-ready top-down DXT at reduced size).</summary>
        public PackImage Image { get; init; }
    }

    /// <summary>A pack GPU image (reduced, transcoded) was decoded for a CPU reader. The pack keeps every texture the recorded boot
    /// read on the CPU exact; this firing means a reader the recording did not see (the engine logs it).</summary>
    public static Action<TextureSource> ReducedImageDecoded;

    public static TextureSource Source(TypeTreeObject tex2d)
    {
        var sd = tex2d.Obj("m_StreamData");
        return new TextureSource(tex2d.Int("m_Width"), tex2d.Int("m_Height"), tex2d.Int("m_TextureFormat"), tex2d.Bytes("image data"),
            sd?.Str("path"), sd?.Long("offset") ?? 0, sd?.Long("size") ?? 0) { Image = tex2d[PackReader.ImageKey] as PackImage };
    }

    public static (int width, int height, byte[] rgba, int format) DecodeTexture2D(AssetDatabase db, SerializedFile file, TypeTreeObject tex2d) =>
        DecodeTexture2D(db, file, Source(tex2d));

    public static (int width, int height, byte[] rgba, int format) DecodeTexture2D(AssetDatabase db, SerializedFile file, TextureSource src)
    {
        int w = src.Width, h = src.Height;
        int format = src.Format;
        // 0x0 textures are runtime-filled (e.g. dynamic "Font Texture"): nothing to decode
        if (w <= 0 || h <= 0) return (0, 0, Array.Empty<byte>(), format);
        if (src.Image is { TopDown: true } gi)
        {
            // pack GPU image (reduced size, rows already top-down): decode, then scale back up to the texture's own size so CPU
            // readers see the dimensions the game knows (GPU uploads take the image as it is: SceneRenderer -> PackAssets.Upload)
            ReducedImageDecoded?.Invoke(src);
            var px = Decode(gi.Read(), gi.Width, gi.Height, (TextureFormat)gi.Format);
            FlipRows(px, gi.Width, gi.Height);
            return (w, h, gi.Width == w && gi.Height == h ? px : Resize(px, gi.Width, gi.Height, w, h), format);
        }
        var data = GetImageData(db, file, src, topMipOnly: true);
        var rgba = Decode(data, w, h, (TextureFormat)format);
        return (w, h, rgba, format);
    }

    /// <summary>Raw (still encoded) image bytes: inline "image data", or the m_StreamData range from .resS.
    /// With <paramref name="topMipOnly"/> the read is limited to mip 0 for formats with a known size.</summary>
    public static byte[] GetImageData(AssetDatabase db, SerializedFile file, TypeTreeObject tex2d, bool topMipOnly = false) =>
        GetImageData(db, file, Source(tex2d), topMipOnly);

    public static byte[] GetImageData(AssetDatabase db, SerializedFile file, TextureSource src, bool topMipOnly = false)
    {
        int w = src.Width, h = src.Height;
        var fmt = (TextureFormat)src.Format;
        if (src.Image != null) return src.Image.Read();
        var inline = src.Inline;
        if (inline is { Length: > 0 }) return inline;
        string path = src.StreamPath;
        if (string.IsNullOrEmpty(path)) throw new InvalidDataException("texture has neither inline image data nor a stream path");
        long offset = src.StreamOffset, size = src.StreamSize;
        if (topMipOnly)
        {
            long mip0 = Mip0Size(fmt, w, h);
            if (mip0 > 0 && mip0 < size) size = mip0;
        }
        return db.ReadStreamedData(file, path, offset, size);
    }

    /// <summary>Byte size of mip 0, or -1 when unknown/variable (crunched).</summary>
    public static long Mip0Size(TextureFormat f, int w, int h)
    {
        long bw = (w + 3) / 4, bh = (h + 3) / 4;
        return f switch
        {
            TextureFormat.Alpha8 or TextureFormat.R8 or TextureFormat.R8_SIGNED => (long)w * h,
            TextureFormat.ARGB4444 or TextureFormat.RGBA4444 or TextureFormat.RGB565 or TextureFormat.R16 or TextureFormat.RHalf or TextureFormat.RG16 => (long)w * h * 2,
            TextureFormat.RGB24 => (long)w * h * 3,
            TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32 or TextureFormat.RGHalf or TextureFormat.RFloat or TextureFormat.RG32 or TextureFormat.RGB9e5Float => (long)w * h * 4,
            TextureFormat.RGB48 => (long)w * h * 6,
            TextureFormat.RGBAHalf or TextureFormat.RGFloat or TextureFormat.RGBA64 => (long)w * h * 8,
            TextureFormat.RGBAFloat => (long)w * h * 16,
            TextureFormat.DXT1 or TextureFormat.BC4 or TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB or TextureFormat.ETC2_RGBA1 or TextureFormat.EAC_R => bw * bh * 8,
            TextureFormat.DXT3 or TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC6H or TextureFormat.BC7 or TextureFormat.ETC2_RGBA8 or TextureFormat.EAC_RG => bw * bh * 16,
            _ => -1,
        };
    }

    public static bool IsSupported(TextureFormat f) => f switch
    {
        TextureFormat.Alpha8 or TextureFormat.ARGB4444 or TextureFormat.RGB24 or TextureFormat.RGBA32 or TextureFormat.ARGB32 or
        TextureFormat.RGB565 or TextureFormat.R16 or TextureFormat.DXT1 or TextureFormat.DXT3 or TextureFormat.DXT5 or TextureFormat.RGBA4444 or
        TextureFormat.BGRA32 or TextureFormat.RHalf or TextureFormat.RGHalf or TextureFormat.RGBAHalf or TextureFormat.RFloat or
        TextureFormat.RGFloat or TextureFormat.RGBAFloat or TextureFormat.BC4 or TextureFormat.BC5 or TextureFormat.BC7 or
        TextureFormat.RG16 or TextureFormat.R8 or TextureFormat.RG32 or TextureFormat.RGB48 or TextureFormat.RGBA64 or
        TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched => true,
        _ => false,
    };

    /// <summary>Decode one mip-0 image (Unity bottom-up row order in, top-down RGBA out).</summary>
    public static byte[] Decode(ReadOnlySpan<byte> src, int w, int h, TextureFormat f)
    {
        if (w <= 0 || h <= 0) throw new InvalidDataException($"bad texture size {w}x{h}");
        long need = Mip0Size(f, w, h);
        if (need > 0 && src.Length < need) throw new InvalidDataException($"{f} {w}x{h}: need {need} bytes, have {src.Length}");
        var dst = new byte[w * h * 4];
        switch (f)
        {
            case TextureFormat.RGBA32: src[..dst.Length].CopyTo(dst); break;
            case TextureFormat.ARGB32: Px(src, dst, 4, (s, d) => { d[0] = s[1]; d[1] = s[2]; d[2] = s[3]; d[3] = s[0]; }); break;
            case TextureFormat.BGRA32: Px(src, dst, 4, (s, d) => { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3]; }); break;
            case TextureFormat.RGB24: Px(src, dst, 3, (s, d) => { d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = 255; }); break;
            case TextureFormat.Alpha8: Px(src, dst, 1, (s, d) => { d[0] = d[1] = d[2] = 255; d[3] = s[0]; }); break;
            case TextureFormat.R8: Px(src, dst, 1, (s, d) => { d[0] = s[0]; d[1] = d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.RG16: Px(src, dst, 2, (s, d) => { d[0] = s[0]; d[1] = s[1]; d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.R16: Px(src, dst, 2, (s, d) => { d[0] = s[1]; d[1] = d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.RG32: Px(src, dst, 4, (s, d) => { d[0] = s[1]; d[1] = s[3]; d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.RGB48: Px(src, dst, 6, (s, d) => { d[0] = s[1]; d[1] = s[3]; d[2] = s[5]; d[3] = 255; }); break;
            case TextureFormat.RGBA64: Px(src, dst, 8, (s, d) => { d[0] = s[1]; d[1] = s[3]; d[2] = s[5]; d[3] = s[7]; }); break;
            case TextureFormat.RGB565:
                Px(src, dst, 2, (s, d) =>
                {
                    int v = s[0] | (s[1] << 8);
                    d[0] = Ex5(v >> 11); d[1] = Ex6(v >> 5); d[2] = Ex5(v); d[3] = 255;
                });
                break;
            case TextureFormat.ARGB4444:
                Px(src, dst, 2, (s, d) =>
                {
                    int v = s[0] | (s[1] << 8);
                    d[0] = (byte)(((v >> 8) & 15) * 17); d[1] = (byte)(((v >> 4) & 15) * 17); d[2] = (byte)((v & 15) * 17); d[3] = (byte)(((v >> 12) & 15) * 17);
                });
                break;
            case TextureFormat.RGBA4444:
                Px(src, dst, 2, (s, d) =>
                {
                    int v = s[0] | (s[1] << 8);
                    d[0] = (byte)(((v >> 12) & 15) * 17); d[1] = (byte)(((v >> 8) & 15) * 17); d[2] = (byte)(((v >> 4) & 15) * 17); d[3] = (byte)((v & 15) * 17);
                });
                break;
            case TextureFormat.RHalf: Px(src, dst, 2, (s, d) => { d[0] = F2B((float)BinaryPrimitives.ReadHalfLittleEndian(s)); d[1] = d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.RGHalf: Px(src, dst, 4, (s, d) => { d[0] = F2B((float)BinaryPrimitives.ReadHalfLittleEndian(s)); d[1] = F2B((float)BinaryPrimitives.ReadHalfLittleEndian(s[2..])); d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.RGBAHalf:
                Px(src, dst, 8, (s, d) => { for (int c = 0; c < 4; c++) d[c] = F2B((float)BinaryPrimitives.ReadHalfLittleEndian(s[(c * 2)..])); });
                break;
            case TextureFormat.RFloat: Px(src, dst, 4, (s, d) => { d[0] = F2B(BinaryPrimitives.ReadSingleLittleEndian(s)); d[1] = d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.RGFloat: Px(src, dst, 8, (s, d) => { d[0] = F2B(BinaryPrimitives.ReadSingleLittleEndian(s)); d[1] = F2B(BinaryPrimitives.ReadSingleLittleEndian(s[4..])); d[2] = 0; d[3] = 255; }); break;
            case TextureFormat.RGBAFloat:
                Px(src, dst, 16, (s, d) => { for (int c = 0; c < 4; c++) d[c] = F2B(BinaryPrimitives.ReadSingleLittleEndian(s[(c * 4)..])); });
                break;
            case TextureFormat.DXT1: Bcn.DecodeBlocks(src, dst, w, h, 8, Bcn.DecodeBC1); break;
            case TextureFormat.DXT3: Bcn.DecodeBlocks(src, dst, w, h, 16, Bcn.DecodeBC2); break;
            case TextureFormat.DXT5: Bcn.DecodeBlocks(src, dst, w, h, 16, Bcn.DecodeBC3); break;
            case TextureFormat.BC4: Bcn.DecodeBlocks(src, dst, w, h, 8, Bcn.DecodeBC4); break;
            case TextureFormat.BC5: Bcn.DecodeBlocks(src, dst, w, h, 16, Bcn.DecodeBC5); break;
            case TextureFormat.BC7: Bcn.DecodeBlocks(src, dst, w, h, 16, Bc7.DecodeBlock); break;
            case TextureFormat.BC6H: Bcn.DecodeBlocks(src, dst, w, h, 16, Bc6h.DecodeBlockUnsigned); break;
            case TextureFormat.DXT1Crunched:
            case TextureFormat.DXT5Crunched:
            {
                var (blocks, cf, cw, chh) = Crunch.UnpackLevel(src, 0);
                if (cw != w || chh != h) throw new InvalidDataException($"crunch header {cw}x{chh} != texture {w}x{h}");
                switch (cf)
                {
                    case Crunch.CrnFormat.DXT1: Bcn.DecodeBlocks(blocks, dst, w, h, 8, Bcn.DecodeBC1); break;
                    case Crunch.CrnFormat.DXT5: case Crunch.CrnFormat.DXT5_AGBR: case Crunch.CrnFormat.DXT5_CCxY:
                    case Crunch.CrnFormat.DXT5_xGBR: case Crunch.CrnFormat.DXT5_xGxR:
                        Bcn.DecodeBlocks(blocks, dst, w, h, 16, Bcn.DecodeBC3); break;
                    case Crunch.CrnFormat.DXT5A: Bcn.DecodeBlocks(blocks, dst, w, h, 8, Bcn.DecodeBC4); break;
                    case Crunch.CrnFormat.DXN_XY: case Crunch.CrnFormat.DXN_YX: Bcn.DecodeBlocks(blocks, dst, w, h, 16, Bcn.DecodeBC5); break;
                    default: throw new NotSupportedException($"crunch payload format {cf}");
                }
                break;
            }
            default:
                throw new NotSupportedException($"texture format {f} ({(int)f}) not supported");
        }
        FlipRows(dst, w, h);
        return dst;
    }

    private delegate void PixelFn(ReadOnlySpan<byte> s, Span<byte> d);

    private static void Px(ReadOnlySpan<byte> src, byte[] dst, int bpp, PixelFn fn)
    {
        int n = dst.Length / 4;
        for (int i = 0; i < n; i++) fn(src.Slice(i * bpp, bpp), dst.AsSpan(i * 4, 4));
    }

    internal static byte Ex5(int v) { v &= 31; return (byte)((v << 3) | (v >> 2)); }
    internal static byte Ex6(int v) { v &= 63; return (byte)((v << 2) | (v >> 4)); }
    private static byte F2B(float f) => float.IsNaN(f) ? (byte)0 : (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    /// <summary>Bilinear resize of top-down RGBA8 (texel centres aligned; used to give CPU readers of a reduced pack image the
    /// texture's own size).</summary>
    public static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            float fy = Math.Clamp((y + 0.5f) * sh / dh - 0.5f, 0, sh - 1); int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1); float wy = fy - y0;
            for (int x = 0; x < dw; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * sw / dw - 0.5f, 0, sw - 1); int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1); float wx = fx - x0;
                for (int c = 0; c < 4; c++)
                {
                    float a = src[(y0 * sw + x0) * 4 + c] * (1 - wx) + src[(y0 * sw + x1) * 4 + c] * wx;
                    float b = src[(y1 * sw + x0) * 4 + c] * (1 - wx) + src[(y1 * sw + x1) * 4 + c] * wx;
                    dst[(y * dw + x) * 4 + c] = (byte)Math.Clamp((int)(a * (1 - wy) + b * wy + 0.5f), 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>The alpha channel alone (1 B/px) of DXT1/DXT5 blocks, rows as <see cref="Decode"/> gives them: what an SDF upload
    /// keeps, without the 4 B/px RGBA8 intermediate (a 2048x2048 atlas: 4 MiB instead of 16 + 4).</summary>
    public static byte[] DecodeAlpha8(ReadOnlySpan<byte> src, int w, int h, TextureFormat f)
    {
        if (f is not (TextureFormat.DXT5 or TextureFormat.DXT1) || w <= 0 || h <= 0) return null;
        var a8 = Bcn.DecodeAlpha8(src, w, h, f == TextureFormat.DXT5);
        FlipRowsAlpha8(a8, w, h);   // as Decode does
        return a8;
    }
    /// <summary>Kit #6 (c): the alpha8 of block rows [by0, by0 + rows) of a DXT5 / DXT1 image into the whole image's <paramref name="dst"/>
    /// (see Bcn.DecodeAlpha8Rows); false for another format. <paramref name="flip"/> as DecodeAlpha8's own row flip (then a top-down image's
    /// second flip cancels it: pass the net flip).</summary>
    public static bool DecodeAlpha8Rows(ReadOnlySpan<byte> blocks, int w, int h, TextureFormat f, int by0, int rows, Span<byte> dst, bool flip)
    {
        if (f is not (TextureFormat.DXT5 or TextureFormat.DXT1) || w <= 0 || h <= 0 || dst.Length < w * h) return false;
        Bcn.DecodeAlpha8Rows(blocks, w, h, by0, rows, f == TextureFormat.DXT5, dst, flip);
        return true;
    }
    public static void FlipRowsAlpha8(byte[] a8, int w, int h)
    {
        var tmp = new byte[w];
        for (int y = 0; y < h / 2; y++)
        {
            var a = a8.AsSpan(y * w, w); var b = a8.AsSpan((h - 1 - y) * w, w);
            a.CopyTo(tmp); b.CopyTo(a); tmp.CopyTo(b);
        }
    }

    public static void FlipRows(byte[] rgba, int w, int h)
    {
        int stride = w * 4;
        var tmp = new byte[stride];
        for (int y = 0; y < h / 2; y++)
        {
            var a = rgba.AsSpan(y * stride, stride);
            var b = rgba.AsSpan((h - 1 - y) * stride, stride);
            a.CopyTo(tmp); b.CopyTo(a); tmp.CopyTo(b);
        }
    }
}
