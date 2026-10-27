namespace Shepherd.Engine.Render;

/// <summary>DXT1 (BC1) / DXT5 (BC3) helpers for the compressed upload path (GlBackend, SHEPHERD_GL_DXT=1).
/// Unity stores texture rows bottom-up; Shepherd's textures are top-down (uv.y is flipped once, in SceneRenderer.Emit), so the
/// blocks are flipped vertically in the compressed domain: block rows reversed, and the 4 pixel rows inside every block
/// reversed (BC1: one index byte per row; BC3 alpha: 12 bits of 3-bit indices per row). Exact for heights that are a
/// multiple of 4; other heights are left to the RGBA path.</summary>
[ProfileOnly]
public static class DxtBlocks
{
    public const int UnityDXT1 = 10, UnityDXT5 = 12;
    public static int BlockBytes(int unityFormat) => unityFormat == UnityDXT1 ? 8 : unityFormat == UnityDXT5 ? 16 : 0;
    public static uint GlFormat(int unityFormat) => unityFormat == UnityDXT1 ? Gl.COMPRESSED_RGBA_S3TC_DXT1 : Gl.COMPRESSED_RGBA_S3TC_DXT5;   // BC1 with punch-through alpha, like Bcn.DecodeBC1
    public static bool CanFlip(int unityFormat, int w, int h) => BlockBytes(unityFormat) > 0 && w > 0 && h > 0 && w % 4 == 0 && h % 4 == 0;

    /// <summary>Mip-0 blocks (Unity order, bottom-up) → a new top-down block array.</summary>
    public static byte[] FlipY(ReadOnlySpan<byte> src, int w, int h, int unityFormat)
    {
        int bb = BlockBytes(unityFormat), bw = w / 4, bh = h / 4, row = bw * bb;
        var dst = new byte[row * bh];
        for (int by = 0; by < bh; by++)
        {
            src.Slice(by * row, row).CopyTo(dst.AsSpan((bh - 1 - by) * row, row));
            for (int bx = 0; bx < bw; bx++)
            {
                var b = dst.AsSpan((bh - 1 - by) * row + bx * bb, bb);
                if (bb == 16) { FlipAlpha(b.Slice(0, 8)); FlipColor(b.Slice(8, 8)); }
                else FlipColor(b);
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
