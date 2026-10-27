namespace Shepherd.Assets;

/// <summary>BC1–BC5 (DXT1/3/5, ATI1/2) block decoders. Blocks write a 4x4 RGBA tile (64 bytes).</summary>
internal static class Bcn
{
    internal delegate void BlockFn(ReadOnlySpan<byte> block, Span<byte> tile);

    internal static void DecodeBlocks(ReadOnlySpan<byte> src, byte[] dst, int w, int h, int blockBytes, BlockFn fn)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> tile = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                fn(src.Slice((by * bw + bx) * blockBytes, blockBytes), tile);
                int cw = Math.Min(4, w - bx * 4), ch = Math.Min(4, h - by * 4);
                for (int y = 0; y < ch; y++)
                    tile.Slice(y * 16, cw * 4).CopyTo(dst.AsSpan(((by * 4 + y) * w + bx * 4) * 4, cw * 4));
            }
        }
    }

    private static void ColorBlock(ReadOnlySpan<byte> b, Span<byte> tile, bool allowPunchThrough)
    {
        int c0 = b[0] | (b[1] << 8), c1 = b[2] | (b[3] << 8);
        Span<byte> pal = stackalloc byte[16];
        pal[0] = TextureDecoder.Ex5(c0 >> 11); pal[1] = TextureDecoder.Ex6(c0 >> 5); pal[2] = TextureDecoder.Ex5(c0); pal[3] = 255;
        pal[4] = TextureDecoder.Ex5(c1 >> 11); pal[5] = TextureDecoder.Ex6(c1 >> 5); pal[6] = TextureDecoder.Ex5(c1); pal[7] = 255;
        if (c0 > c1 || !allowPunchThrough)
        {
            for (int c = 0; c < 3; c++)
            {
                pal[8 + c] = (byte)((2 * pal[c] + pal[4 + c] + 1) / 3);
                pal[12 + c] = (byte)((pal[c] + 2 * pal[4 + c] + 1) / 3);
            }
            pal[11] = 255; pal[15] = 255;
        }
        else
        {
            for (int c = 0; c < 3; c++) pal[8 + c] = (byte)((pal[c] + pal[4 + c]) / 2);
            pal[11] = 255;
            pal[12] = pal[13] = pal[14] = 0; pal[15] = 0;
        }
        uint idx = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((idx >> (2 * i)) & 3) * 4;
            tile[i * 4] = pal[k]; tile[i * 4 + 1] = pal[k + 1]; tile[i * 4 + 2] = pal[k + 2]; tile[i * 4 + 3] = pal[k + 3];
        }
    }

    /// <summary>BC3-alpha / BC4 style 8-byte block -> 16 values written at tile[i*4 + channel].</summary>
    private static void AlphaBlock(ReadOnlySpan<byte> b, Span<byte> tile, int channel)
    {
        Span<int> a = stackalloc int[8];
        a[0] = b[0]; a[1] = b[1];
        if (a[0] > a[1])
            for (int k = 1; k <= 6; k++) a[k + 1] = ((7 - k) * a[0] + k * a[1] + 3) / 7;
        else
        {
            for (int k = 1; k <= 4; k++) a[k + 1] = ((5 - k) * a[0] + k * a[1] + 2) / 5;
            a[6] = 0; a[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) tile[i * 4 + channel] = (byte)a[(int)((bits >> (3 * i)) & 7)];
    }

    /// <summary>Only the alpha channel of DXT1 (punch-through, as <see cref="DecodeBC1"/>) or DXT5 blocks, 1 byte per texel, in
    /// the same row order <see cref="DecodeBlocks"/> gives: byte i equals rgba[i*4+3] of the full decode.</summary>
    internal static byte[] DecodeAlpha8(ReadOnlySpan<byte> src, int w, int h, bool bc3)
    {
        var dst = new byte[w * h];
        int bw = (w + 3) / 4, bh = (h + 3) / 4, bb = bc3 ? 16 : 8;
        Span<byte> tile = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var b = src.Slice((by * bw + bx) * bb, bb);
                if (bc3) AlphaBlock(b, tile, 3);
                else
                {
                    int c0 = b[0] | (b[1] << 8), c1 = b[2] | (b[3] << 8);
                    uint idx = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
                    for (int i = 0; i < 16; i++) tile[i * 4 + 3] = (byte)(c0 <= c1 && ((idx >> (2 * i)) & 3) == 3 ? 0 : 255);
                }
                int cw = Math.Min(4, w - bx * 4), ch = Math.Min(4, h - by * 4);
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++) dst[(by * 4 + y) * w + bx * 4 + x] = tile[(y * 4 + x) * 4 + 3];
            }
        return dst;
    }

    /// <summary>Kit #6 (c): <see cref="DecodeAlpha8"/> for the block rows [by0, by0 + rows) only, read from <paramref name="blocks"/> (those
    /// rows' blocks, in order), written into the whole image's <paramref name="dst"/> (w x h, row-major); with <paramref name="flip"/> a
    /// decoded row y lands at h - 1 - y. Strip by strip this writes exactly the bytes DecodeAlpha8 (+ the row flip) gives the whole image.</summary>
    internal static void DecodeAlpha8Rows(ReadOnlySpan<byte> blocks, int w, int h, int by0, int rows, bool bc3, Span<byte> dst, bool flip)
    {
        int bw = (w + 3) / 4, bb = bc3 ? 16 : 8;
        Span<byte> tile = stackalloc byte[64];
        for (int r = 0; r < rows; r++)
        {
            int by = by0 + r;
            for (int bx = 0; bx < bw; bx++)
            {
                var b = blocks.Slice((r * bw + bx) * bb, bb);
                if (bc3) AlphaBlock(b, tile, 3);
                else
                {
                    int c0 = b[0] | (b[1] << 8), c1 = b[2] | (b[3] << 8);
                    uint idx = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
                    for (int i = 0; i < 16; i++) tile[i * 4 + 3] = (byte)(c0 <= c1 && ((idx >> (2 * i)) & 3) == 3 ? 0 : 255);
                }
                int cw = Math.Min(4, w - bx * 4), ch = Math.Min(4, h - by * 4);
                for (int y = 0; y < ch; y++)
                {
                    int row = by * 4 + y; if (flip) row = h - 1 - row;
                    for (int x = 0; x < cw; x++) dst[row * w + bx * 4 + x] = tile[(y * 4 + x) * 4 + 3];
                }
            }
        }
    }

    internal static void DecodeBC1(ReadOnlySpan<byte> b, Span<byte> tile) => ColorBlock(b, tile, true);

    internal static void DecodeBC2(ReadOnlySpan<byte> b, Span<byte> tile)
    {
        ColorBlock(b.Slice(8), tile, false);
        for (int i = 0; i < 16; i++)
        {
            int v = (b[i / 2] >> ((i & 1) * 4)) & 15;
            tile[i * 4 + 3] = (byte)(v * 17);
        }
    }

    internal static void DecodeBC3(ReadOnlySpan<byte> b, Span<byte> tile)
    {
        ColorBlock(b.Slice(8), tile, false);
        AlphaBlock(b, tile, 3);
    }

    internal static void DecodeBC4(ReadOnlySpan<byte> b, Span<byte> tile)
    {
        AlphaBlock(b, tile, 0);
        for (int i = 0; i < 16; i++) { tile[i * 4 + 1] = 0; tile[i * 4 + 2] = 0; tile[i * 4 + 3] = 255; }
    }

    internal static void DecodeBC5(ReadOnlySpan<byte> b, Span<byte> tile)
    {
        AlphaBlock(b, tile, 0);
        AlphaBlock(b.Slice(8), tile, 1);
        for (int i = 0; i < 16; i++) { tile[i * 4 + 2] = 0; tile[i * 4 + 3] = 255; }
    }
}
