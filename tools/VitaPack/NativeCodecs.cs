using System.Reflection;
using System.Runtime.InteropServices;

namespace Shepherd.VitaPack;

/// <summary>Device kit #10, track io: the pack builder's encoders -- the Mac's own, already installed zstd 1.5.7 and LZ4 1.10 (Homebrew:
/// /opt/homebrew/lib/libzstd.dylib, liblz4.dylib; nothing is downloaded) -- and the same libraries' decoders for the builder's own read-back
/// check. The device decodes with vitasdk's libzstd.a (the same zstd 1.5.7) and the host's LZ4 block decoder (vita/shepherd/k10_io.inc.c); the
/// Mac engine with these (Runtime/K10Io.cs). Lossless either way: every chunk is decoded again and compared byte for byte before a pack is kept.</summary>
static unsafe class NativeCodecs
{
    static readonly string[] ZstdPaths = { "/opt/homebrew/lib/libzstd.1.dylib", "/opt/homebrew/lib/libzstd.dylib", "/usr/local/lib/libzstd.dylib", "libzstd" };
    static readonly string[] Lz4Paths = { "/opt/homebrew/lib/liblz4.1.dylib", "/opt/homebrew/lib/liblz4.dylib", "/usr/local/lib/liblz4.dylib", "liblz4" };
    static bool _resolver;
    public static void Init()
    {
        if (_resolver) return; _resolver = true;
        NativeLibrary.SetDllImportResolver(typeof(NativeCodecs).Assembly, (name, asm, path) =>
        {
            string[] tries = name == "zstd" ? ZstdPaths : name == "lz4" ? Lz4Paths : null;
            if (tries == null) return IntPtr.Zero;
            foreach (var t in tries) if (NativeLibrary.TryLoad(t, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    [DllImport("zstd")] static extern nuint ZSTD_compress(byte* dst, nuint cap, byte* src, nuint n, int level);
    [DllImport("zstd")] static extern nuint ZSTD_compressBound(nuint n);
    [DllImport("zstd")] static extern uint ZSTD_isError(nuint code);
    [DllImport("zstd")] static extern nuint ZSTD_decompress(byte* dst, nuint cap, byte* src, nuint n);
    [DllImport("zstd")] static extern uint ZSTD_versionNumber();
    [DllImport("zstd")] static extern IntPtr ZSTD_createCCtx();
    [DllImport("zstd")] static extern nuint ZSTD_freeCCtx(IntPtr c);
    [DllImport("zstd")] static extern nuint ZSTD_CCtx_setParameter(IntPtr c, int param, int value);
    [DllImport("zstd")] static extern nuint ZSTD_compress2(IntPtr c, byte* dst, nuint cap, byte* src, nuint n);
    [DllImport("lz4")] static extern int LZ4_compress_HC(byte* src, byte* dst, int n, int cap, int level);
    [DllImport("lz4")] static extern int LZ4_compressBound(int n);
    [DllImport("lz4")] static extern int LZ4_decompress_safe(byte* src, byte* dst, int n, int cap);
    [DllImport("lz4")] static extern int LZ4_versionNumber();

    public static string Versions() { Init(); uint z = ZSTD_versionNumber(); int l = LZ4_versionNumber(); return $"zstd {z / 10000}.{z / 100 % 100}.{z % 100}, lz4 {l / 10000}.{l / 100 % 100}.{l % 100}"; }

    // zstd parameters (zstd.h ZSTD_cParameter): ZSTD_c_compressionLevel 100, ZSTD_c_checksumFlag 201, ZSTD_c_contentSizeFlag 200, ZSTD_c_dictIDFlag 202
    [ThreadStatic] static IntPtr t_cctx;
    /// <summary>zstd frame of <paramref name="src"/> at <paramref name="level"/>, content size in the header, no checksum (the pack's own chunk
    /// hashes cover it), no dictionary id.</summary>
    public static byte[] Zstd(ReadOnlySpan<byte> src, int level)
    {
        Init();
        if (t_cctx == IntPtr.Zero) t_cctx = ZSTD_createCCtx();
        ZSTD_CCtx_setParameter(t_cctx, 100, level); ZSTD_CCtx_setParameter(t_cctx, 200, 1); ZSTD_CCtx_setParameter(t_cctx, 201, 0); ZSTD_CCtx_setParameter(t_cctx, 202, 0);
        var dst = new byte[(int)ZSTD_compressBound((nuint)src.Length)];
        fixed (byte* s = src) fixed (byte* d = dst)
        {
            nuint r = ZSTD_compress2(t_cctx, d, (nuint)dst.Length, s, (nuint)src.Length);
            if (ZSTD_isError(r) != 0) throw new InvalidOperationException("zstd compress failed");
            return dst.AsSpan(0, (int)r).ToArray();
        }
    }
    public static byte[] Lz4Hc(ReadOnlySpan<byte> src, int level)
    {
        Init();
        var dst = new byte[LZ4_compressBound(src.Length)];
        fixed (byte* s = src) fixed (byte* d = dst)
        {
            int r = LZ4_compress_HC(s, d, src.Length, dst.Length, level);
            if (r <= 0) throw new InvalidOperationException("lz4 compress failed");
            return dst.AsSpan(0, r).ToArray();
        }
    }
    public static int ZstdDecode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Init();
        fixed (byte* s = src) fixed (byte* d = dst)
        {
            nuint r = ZSTD_decompress(d, (nuint)dst.Length, s, (nuint)src.Length);
            return ZSTD_isError(r) != 0 ? -1 : (int)r;
        }
    }
    public static int Lz4Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Init();
        fixed (byte* s = src) fixed (byte* d = dst) return LZ4_decompress_safe(s, d, src.Length, dst.Length);
    }
}
