namespace Shepherd.Assets.Compression;

/// <summary>Raw LZ4 block decoder (the format Unity uses for LZ4 / LZ4HC chunks).</summary>
public static class Lz4
{
    /// <summary>Decodes a raw LZ4 block. Returns the number of bytes written; throws on malformed input.</summary>
    public static unsafe int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        fixed (byte* sBase = src)
        fixed (byte* dBase = dst)
        {
            byte* s = sBase, sEnd = sBase + src.Length;
            byte* d = dBase, dEnd = dBase + dst.Length;
            while (s < sEnd)
            {
                uint token = *s++;
                // literals
                long litLen = token >> 4;
                if (litLen == 15)
                {
                    uint b;
                    do
                    {
                        if (s >= sEnd) throw new InvalidDataException("lz4: truncated literal length");
                        b = *s++; litLen += b;
                    } while (b == 255);
                }
                if (litLen > 0)
                {
                    if (s + litLen > sEnd || d + litLen > dEnd) throw new InvalidDataException("lz4: literal overrun");
                    Buffer.MemoryCopy(s, d, dEnd - d, litLen);
                    s += litLen; d += litLen;
                }
                if (s >= sEnd) break; // last sequence has only literals
                if (s + 2 > sEnd) throw new InvalidDataException("lz4: truncated offset");
                int offset = s[0] | (s[1] << 8);
                s += 2;
                if (offset == 0 || d - offset < dBase) throw new InvalidDataException("lz4: bad offset");
                long matchLen = token & 15;
                if (matchLen == 15)
                {
                    uint b;
                    do
                    {
                        if (s >= sEnd) throw new InvalidDataException("lz4: truncated match length");
                        b = *s++; matchLen += b;
                    } while (b == 255);
                }
                matchLen += 4;
                if (d + matchLen > dEnd) throw new InvalidDataException("lz4: match overrun");
                byte* m = d - offset;
                if (offset >= matchLen)
                {
                    Buffer.MemoryCopy(m, d, dEnd - d, matchLen);
                    d += matchLen;
                }
                else
                {
                    // overlapping copy, byte by byte (or in offset-sized strides)
                    for (long i = 0; i < matchLen; i++) d[i] = m[i];
                    d += matchLen;
                }
            }
            return (int)(d - dBase);
        }
    }
}
