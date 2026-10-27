using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Shepherd.Assets;

/// <summary>Endian-aware forward reader over a span. Position is relative to the span start.</summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _d;
    public int Position;
    public bool BigEndian;

    public SpanReader(ReadOnlySpan<byte> data, bool bigEndian = false)
    {
        _d = data;
        Position = 0;
        BigEndian = bigEndian;
    }

    public readonly int Length => _d.Length;
    public readonly int Remaining => _d.Length - Position;
    public readonly ReadOnlySpan<byte> Data => _d;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Take(int n)
    {
        if ((uint)n > (uint)(_d.Length - Position))
            throw new EndOfStreamException($"read of {n} bytes at {Position} past end ({_d.Length})");
        var s = _d.Slice(Position, n);
        Position += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public sbyte I8() => (sbyte)Take(1)[0];
    public bool Bool() => Take(1)[0] != 0;
    public short I16() { var s = Take(2); return BigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s); }
    public ushort U16() { var s = Take(2); return BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s); }
    public int I32() { var s = Take(4); return BigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s); }
    public uint U32() { var s = Take(4); return BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s); }
    public long I64() { var s = Take(8); return BigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s); }
    public ulong U64() { var s = Take(8); return BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s); }
    public float F32() { var s = Take(4); return BigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s); }
    public double F64() { var s = Take(8); return BigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s); }
    public ReadOnlySpan<byte> Bytes(int n) => Take(n);

    public string CString()
    {
        int end = _d.Slice(Position).IndexOf((byte)0);
        if (end < 0) throw new EndOfStreamException("unterminated string");
        var s = Encoding.UTF8.GetString(_d.Slice(Position, end));
        Position += end + 1;
        return s;
    }

    /// <summary>int32 length-prefixed UTF-8 string (no alignment).</summary>
    public string LenString()
    {
        int len = I32();
        if (len < 0 || len > Remaining) throw new InvalidDataException($"bad string length {len} at {Position - 4}");
        return Encoding.UTF8.GetString(Take(len));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Align(int n = 4) => Position = (Position + (n - 1)) & ~(n - 1);
}
