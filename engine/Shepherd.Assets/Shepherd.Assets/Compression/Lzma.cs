namespace Shepherd.Assets.Compression;

/// <summary>LZMA placeholder: no LZMA-compressed block occurs in the CotL macOS build (all 3,421 bundles scanned).</summary>
public static class Lzma
{
    /// <summary>Unity LZMA block = 5-byte props header + raw LZMA stream (no size field).</summary>
    public static void DecodeUnity(ReadOnlySpan<byte> src, Span<byte> dst)
        => throw new NotSupportedException("LZMA-compressed bundle blocks are not supported (none exist in this game build)");
}
