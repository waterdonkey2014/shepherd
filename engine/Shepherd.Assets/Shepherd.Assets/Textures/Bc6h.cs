namespace Shepherd.Assets;

/// <summary>BC6H placeholder (HDR; not used by the Main Menu closure).</summary>
internal static class Bc6h
{
    internal static void DecodeBlockUnsigned(ReadOnlySpan<byte> block, Span<byte> tile)
        => throw new NotSupportedException("BC6H decoding not implemented");
}
