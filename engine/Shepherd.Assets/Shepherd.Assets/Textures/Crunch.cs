// Managed port of the decoder half of Unity's crunch fork (crnlib v1.04, Unity-Technologies/crunch@unity,
// inc/crn_decomp.h, ZLIB license, (C) 2010-2017 Richard Geldreich, Jr. and Binomial LLC).
// Only what Texture2D DXT1Crunched / DXT5Crunched needs: header, Huffman models, palettes, DXT1/DXT5(+DXT5A/DXN) level unpack.

using System.Buffers.Binary;

namespace Shepherd.Assets;

public static class Crunch
{
    public enum CrnFormat { DXT1 = 0, DXT3 = 1, DXT5 = 2, DXT5_CCxY = 3, DXT5_xGxR = 4, DXT5_xGBR = 5, DXT5_AGBR = 6, DXN_XY = 7, DXN_YX = 8, DXT5A = 9, ETC1 = 10, ETC2 = 11, ETC2A = 12, ETC1S = 13, ETC2AS = 14 }

    public sealed class Header
    {
        public int HeaderSize, DataSize, Width, Height, Levels, Faces, Flags;
        public CrnFormat Format;
        public (int ofs, int size, int num) ColorEndpoints, ColorSelectors, AlphaEndpoints, AlphaSelectors;
        public int TablesSize, TablesOfs;
        public int[] LevelOfs;
    }

    private static int BE(ReadOnlySpan<byte> d, int ofs, int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | d[ofs + i];
        return v;
    }

    public static Header ReadHeader(ReadOnlySpan<byte> d)
    {
        if (d.Length < 74 || d[0] != (byte)'H' || d[1] != (byte)'x') throw new InvalidDataException("not a crunch (.crn) stream");
        var h = new Header
        {
            HeaderSize = BE(d, 2, 2), DataSize = BE(d, 6, 4), Width = BE(d, 12, 2), Height = BE(d, 14, 2),
            Levels = d[16], Faces = d[17], Format = (CrnFormat)d[18], Flags = BE(d, 19, 2),
            ColorEndpoints = (BE(d, 33, 3), BE(d, 36, 3), BE(d, 39, 2)),
            ColorSelectors = (BE(d, 41, 3), BE(d, 44, 3), BE(d, 47, 2)),
            AlphaEndpoints = (BE(d, 49, 3), BE(d, 52, 3), BE(d, 55, 2)),
            AlphaSelectors = (BE(d, 57, 3), BE(d, 60, 3), BE(d, 63, 2)),
            TablesSize = BE(d, 65, 2), TablesOfs = BE(d, 67, 3),
        };
        if (d.Length < h.DataSize) throw new InvalidDataException($"crunch stream truncated ({d.Length} < {h.DataSize})");
        h.LevelOfs = new int[h.Levels];
        for (int i = 0; i < h.Levels; i++) h.LevelOfs[i] = BE(d, 70 + i * 4, 4);
        return h;
    }

    /// <summary>Unpack one mip level of a crunch stream to raw DXT blocks (face 0). Returns (blocks, format).</summary>
    public static (byte[] blocks, CrnFormat format, int width, int height) UnpackLevel(ReadOnlySpan<byte> crn, int level = 0)
    {
        var u = new Unpacker(crn);
        return u.Unpack(level);
    }

    // ------------------------------------------------------------------ Huffman

    private const int MaxExpectedCodeSize = 16, MaxSupportedSyms = 8192, MaxTableBits = 11;

    private sealed class Model
    {
        public int TotalSyms;
        public byte[] CodeSizes = Array.Empty<byte>();
        public uint[] MaxCodes = new uint[MaxExpectedCodeSize + 1];
        public int[] ValPtrs = new int[MaxExpectedCodeSize + 1];
        public uint[] Lookup;
        public ushort[] Sorted;
        public int TableBits, TableMaxCode, DecodeStartCodeSize, MinCodeSize, MaxCodeSize;

        public bool Prepare()
        {
            TotalSyms = CodeSizes.Length;
            int tableBits = 0;
            if (TotalSyms > 16) tableBits = Math.Min(1 + CeilLog2(TotalSyms), MaxTableBits);
            return Init(tableBits);
        }

        private static int CeilLog2(int v)
        {
            int l = 0;
            for (int t = v; t > 1; t >>= 1) l++;
            if (v > (1 << l)) l++;
            return l;
        }

        private bool Init(int tableBits)
        {
            int numSyms = TotalSyms;
            if (numSyms == 0 || tableBits > MaxTableBits) return false;
            var numCodes = new uint[MaxExpectedCodeSize + 1];
            for (int i = 0; i < numSyms; i++) if (CodeSizes[i] != 0) numCodes[CodeSizes[i]]++;
            var minCodes = new uint[MaxExpectedCodeSize];
            var sortedPos = new uint[MaxExpectedCodeSize + 1];
            uint curCode = 0, totalUsed = 0;
            int maxCodeSize = 0, minCodeSize = int.MaxValue;
            for (int i = 1; i <= MaxExpectedCodeSize; i++)
            {
                uint n = numCodes[i];
                if (n == 0) MaxCodes[i - 1] = 0;
                else
                {
                    minCodeSize = Math.Min(minCodeSize, i);
                    maxCodeSize = Math.Max(maxCodeSize, i);
                    minCodes[i - 1] = curCode;
                    uint mc = curCode + n - 1;
                    MaxCodes[i - 1] = 1 + ((mc << (16 - i)) | (uint)((1 << (16 - i)) - 1));
                    ValPtrs[i - 1] = (int)totalUsed;
                    sortedPos[i] = totalUsed;
                    curCode += n;
                    totalUsed += n;
                }
                curCode <<= 1;
            }
            Sorted = new ushort[Math.Max(1, totalUsed)];
            MinCodeSize = minCodeSize; MaxCodeSize = maxCodeSize;
            for (int i = 0; i < numSyms; i++)
            {
                int c = CodeSizes[i];
                if (c != 0) Sorted[sortedPos[c]++] = (ushort)i;
            }
            if (tableBits <= MinCodeSize) tableBits = 0;
            TableBits = tableBits;
            if (tableBits != 0)
            {
                Lookup = new uint[1 << tableBits];
                Array.Fill(Lookup, uint.MaxValue);
                for (int codesize = 1; codesize <= tableBits; codesize++)
                {
                    if (numCodes[codesize] == 0) continue;
                    int fillsize = tableBits - codesize;
                    int fillnum = 1 << fillsize;
                    uint minCode = minCodes[codesize - 1];
                    uint k = MaxCodes[codesize - 1];
                    uint maxCode = k == 0 ? uint.MaxValue : (k - 1) >> (16 - codesize);
                    int valPtr = ValPtrs[codesize - 1];
                    for (uint code = minCode; code <= maxCode; code++)
                    {
                        int sym = Sorted[valPtr + (int)(code - minCode)];
                        for (int j = 0; j < fillnum; j++)
                            Lookup[j + (int)(code << fillsize)] = (uint)sym | ((uint)codesize << 16);
                    }
                }
            }
            for (int i = 0; i < MaxExpectedCodeSize; i++) ValPtrs[i] -= (int)minCodes[i];
            TableMaxCode = 0;
            DecodeStartCodeSize = MinCodeSize;
            if (tableBits != 0)
            {
                int i;
                for (i = tableBits; i >= 1; i--)
                    if (numCodes[i] != 0) { TableMaxCode = (int)MaxCodes[i - 1]; break; }
                if (i >= 1)
                {
                    DecodeStartCodeSize = tableBits + 1;
                    for (int j = tableBits + 1; j <= maxCodeSize; j++)
                        if (numCodes[j] != 0) { DecodeStartCodeSize = j; break; }
                }
            }
            MaxCodes[MaxExpectedCodeSize] = uint.MaxValue;
            ValPtrs[MaxExpectedCodeSize] = 0xFFFFF;
            return true;
        }
    }

    private static readonly byte[] MostProbableCodelengthCodes = { 17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16 };

    private ref struct Codec
    {
        private ReadOnlySpan<byte> _buf;
        private int _next;
        private uint _bitBuf;
        private int _bitCount;

        public void Start(ReadOnlySpan<byte> all, int ofs, int size)
        {
            if (size <= 0 || ofs < 0 || ofs + size > all.Length) throw new InvalidDataException("crunch: bad segment");
            _buf = all.Slice(ofs, size);
            _next = 0; _bitBuf = 0; _bitCount = 0;
        }

        public uint GetBits(int n)
        {
            while (_bitCount < n)
            {
                uint c = _next < _buf.Length ? _buf[_next++] : 0u;
                _bitCount += 8;
                _bitBuf |= c << (32 - _bitCount);
            }
            uint result = n == 0 ? 0 : _bitBuf >> (32 - n);
            _bitBuf = n == 32 ? 0 : _bitBuf << n;
            _bitCount -= n;
            return result;
        }

        public uint DecodeBits(int n)
        {
            if (n == 0) return 0;
            if (n > 16) { uint a = GetBits(n - 16); uint b = GetBits(16); return (a << 16) | b; }
            return GetBits(n);
        }

        public uint Decode(Model m)
        {
            if (_bitCount < 24)
            {
                if (_bitCount < 16)
                {
                    uint c0 = _next < _buf.Length ? _buf[_next++] : 0u;
                    uint c1 = _next < _buf.Length ? _buf[_next++] : 0u;
                    _bitCount += 16;
                    _bitBuf |= ((c0 << 8) | c1) << (32 - _bitCount);
                }
                else
                {
                    uint c = _next < _buf.Length ? _buf[_next++] : 0u;
                    _bitCount += 8;
                    _bitBuf |= c << (32 - _bitCount);
                }
            }
            uint k = (_bitBuf >> 16) + 1;
            uint sym; int len;
            if (k <= m.TableMaxCode)
            {
                uint t = m.Lookup[_bitBuf >> (32 - m.TableBits)];
                sym = t & 0xFFFF;
                len = (int)(t >> 16);
            }
            else
            {
                len = m.DecodeStartCodeSize;
                while (k > m.MaxCodes[len - 1]) len++;
                int valPtr = m.ValPtrs[len - 1] + (int)(_bitBuf >> (32 - len));
                if ((uint)valPtr >= (uint)m.TotalSyms) throw new InvalidDataException("crunch: corrupt Huffman stream");
                sym = m.Sorted[valPtr];
            }
            _bitBuf <<= len;
            _bitCount -= len;
            return sym;
        }

        public Model ReceiveModel()
        {
            var model = new Model();
            int total = (int)DecodeBits(14); // total_bits(cMaxSupportedSyms = 8192)
            if (total == 0) return model;
            model.CodeSizes = new byte[total];
            int toSend = (int)DecodeBits(5);
            if (toSend < 1 || toSend > 21) throw new InvalidDataException("crunch: bad code length count");
            var dm = new Model { CodeSizes = new byte[21] };
            for (int i = 0; i < toSend; i++) dm.CodeSizes[MostProbableCodelengthCodes[i]] = (byte)DecodeBits(3);
            if (!dm.Prepare()) throw new InvalidDataException("crunch: bad code length model");
            int ofs = 0;
            while (ofs < total)
            {
                int remaining = total - ofs;
                uint code = Decode(dm);
                if (code <= 16) model.CodeSizes[ofs++] = (byte)code;
                else if (code == 17) { int len = (int)DecodeBits(3) + 3; if (len > remaining) throw new InvalidDataException("crunch: zero run"); ofs += len; }
                else if (code == 18) { int len = (int)DecodeBits(7) + 11; if (len > remaining) throw new InvalidDataException("crunch: zero run"); ofs += len; }
                else if (code == 19 || code == 20)
                {
                    int len = code == 19 ? (int)DecodeBits(2) + 3 : (int)DecodeBits(6) + 7;
                    if (ofs == 0 || len > remaining) throw new InvalidDataException("crunch: repeat run");
                    byte prev = model.CodeSizes[ofs - 1];
                    if (prev == 0) throw new InvalidDataException("crunch: repeat of zero");
                    for (int e = ofs + len; ofs < e;) model.CodeSizes[ofs++] = prev;
                }
                else throw new InvalidDataException("crunch: bad code length code");
            }
            if (!model.Prepare()) throw new InvalidDataException("crunch: bad model");
            return model;
        }
    }

    // ------------------------------------------------------------------ unpacker

    private static readonly byte[] Dxt5FromLinear = { 0, 2, 3, 4, 5, 6, 7, 1 };

    private sealed class Unpacker
    {
        private readonly byte[] _data;
        private readonly Header _h;
        private Model _refDm;
        private readonly Model[] _endpointDm = new Model[2], _selectorDm = new Model[2];
        private uint[] _colorEndpoints = Array.Empty<uint>(), _colorSelectors = Array.Empty<uint>();
        private ushort[] _alphaEndpoints = Array.Empty<ushort>(), _alphaSelectors = Array.Empty<ushort>();

        public Unpacker(ReadOnlySpan<byte> crn)
        {
            _h = ReadHeader(crn);
            _data = crn.Slice(0, _h.DataSize).ToArray();
            if (_h.Format is CrnFormat.ETC1 or CrnFormat.ETC2 or CrnFormat.ETC2A or CrnFormat.ETC1S or CrnFormat.ETC2AS)
                throw new NotSupportedException($"crunch ETC format {_h.Format} not supported");
            InitTables();
            DecodePalettes();
        }

        private void InitTables()
        {
            var c = new Codec();
            c.Start(_data, _h.TablesOfs, _h.TablesSize);
            _refDm = c.ReceiveModel();
            if (_h.ColorEndpoints.num == 0 && _h.AlphaEndpoints.num == 0) throw new InvalidDataException("crunch: no endpoints");
            if (_h.ColorEndpoints.num != 0) { _endpointDm[0] = c.ReceiveModel(); _selectorDm[0] = c.ReceiveModel(); }
            if (_h.AlphaEndpoints.num != 0) { _endpointDm[1] = c.ReceiveModel(); _selectorDm[1] = c.ReceiveModel(); }
        }

        private void DecodePalettes()
        {
            if (_h.ColorEndpoints.num != 0)
            {
                // color endpoints
                var c = new Codec();
                c.Start(_data, _h.ColorEndpoints.ofs, _h.ColorEndpoints.size);
                var dm0 = c.ReceiveModel();
                var dm1 = c.ReceiveModel();
                int n = _h.ColorEndpoints.num;
                _colorEndpoints = new uint[n];
                uint a = 0, b = 0, cc = 0, d = 0, e = 0, f = 0;
                for (int i = 0; i < n; i++)
                {
                    a = (a + c.Decode(dm0)) & 31;
                    b = (b + c.Decode(dm1)) & 63;
                    cc = (cc + c.Decode(dm0)) & 31;
                    d = (d + c.Decode(dm0)) & 31;
                    e = (e + c.Decode(dm1)) & 63;
                    f = (f + c.Decode(dm0)) & 31;
                    _colorEndpoints[i] = cc | (b << 5) | (a << 11) | (f << 16) | (e << 21) | (d << 27);
                }
                // color selectors
                c.Start(_data, _h.ColorSelectors.ofs, _h.ColorSelectors.size);
                var dm = c.ReceiveModel();
                int ns = _h.ColorSelectors.num;
                _colorSelectors = new uint[ns];
                uint s = 0;
                for (int i = 0; i < ns; i++)
                {
                    for (int j = 0; j < 32; j += 4) s ^= c.Decode(dm) << j;
                    _colorSelectors[i] = ((s ^ (s << 1)) & 0xAAAAAAAA) | ((s >> 1) & 0x55555555);
                }
            }
            if (_h.AlphaEndpoints.num != 0)
            {
                var c = new Codec();
                c.Start(_data, _h.AlphaEndpoints.ofs, _h.AlphaEndpoints.size);
                var dm = c.ReceiveModel();
                int n = _h.AlphaEndpoints.num;
                _alphaEndpoints = new ushort[n];
                uint a = 0, b = 0;
                for (int i = 0; i < n; i++)
                {
                    a = (a + c.Decode(dm)) & 255;
                    b = (b + c.Decode(dm)) & 255;
                    _alphaEndpoints[i] = (ushort)(a | (b << 8));
                }
                // alpha selectors
                c.Start(_data, _h.AlphaSelectors.ofs, _h.AlphaSelectors.size);
                var sdm = c.ReceiveModel();
                _alphaSelectors = new ushort[_h.AlphaSelectors.num * 3];
                Span<byte> fromLinear = stackalloc byte[64];
                for (int i = 0; i < 64; i++) fromLinear[i] = (byte)(Dxt5FromLinear[i & 7] | (Dxt5FromLinear[i >> 3] << 3));
                uint s0Lin = 0, s1Lin = 0;
                for (int i = 0; i < _alphaSelectors.Length;)
                {
                    uint s0 = 0, s1 = 0;
                    for (int j = 0; j < 24; j += 6)
                    {
                        s0Lin ^= c.Decode(sdm) << j;
                        s0 |= (uint)fromLinear[(int)((s0Lin >> j) & 0x3F)] << j;
                    }
                    for (int j = 0; j < 24; j += 6)
                    {
                        s1Lin ^= c.Decode(sdm) << j;
                        s1 |= (uint)fromLinear[(int)((s1Lin >> j) & 0x3F)] << j;
                    }
                    _alphaSelectors[i++] = (ushort)s0;
                    _alphaSelectors[i++] = (ushort)((s0 >> 16) | (s1 << 8));
                    _alphaSelectors[i++] = (ushort)(s1 >> 8);
                }
            }
        }

        public (byte[] blocks, CrnFormat format, int width, int height) Unpack(int level)
        {
            if (level < 0 || level >= _h.Levels) throw new ArgumentOutOfRangeException(nameof(level));
            int cur = _h.LevelOfs[level];
            int next = level + 1 < _h.Levels ? _h.LevelOfs[level + 1] : _data.Length;
            int width = Math.Max(_h.Width >> level, 1), height = Math.Max(_h.Height >> level, 1);
            int bx = (width + 3) >> 2, by = (height + 3) >> 2;
            bool eightByte = _h.Format is CrnFormat.DXT1 or CrnFormat.DXT5A;
            int blockSize = eightByte ? 8 : 16;
            var outBuf = new byte[bx * by * blockSize];
            var c = new Codec();
            c.Start(_data, cur, next - cur);
            switch (_h.Format)
            {
                case CrnFormat.DXT1: UnpackDxt1(ref c, outBuf, bx, by); break;
                case CrnFormat.DXT5: case CrnFormat.DXT5_CCxY: case CrnFormat.DXT5_xGBR: case CrnFormat.DXT5_AGBR: case CrnFormat.DXT5_xGxR:
                    UnpackDxt5(ref c, outBuf, bx, by); break;
                case CrnFormat.DXT5A: UnpackDxt5a(ref c, outBuf, bx, by); break;
                case CrnFormat.DXN_XY: case CrnFormat.DXN_YX: UnpackDxn(ref c, outBuf, bx, by); break;
                default: throw new NotSupportedException($"crunch format {_h.Format}");
            }
            return (outBuf, _h.Format, width, height);
        }

        private struct BufEl { public uint EndpointReference, ColorIdx, Alpha0Idx, Alpha1Idx; }

        private void UnpackDxt1(ref Codec c, byte[] dst, int ow, int oh)
        {
            int numColor = _colorEndpoints.Length;
            int width = (ow + 1) & ~1, height = (oh + 1) & ~1;
            var buf = new BufEl[width];
            uint colorIdx = 0, refGroup = 0;
            for (int y = 0; y < height; y++)
            {
                bool visible = y < oh;
                for (int x = 0; x < width; x++)
                {
                    visible = visible && x < ow;
                    if ((y & 1) == 0 && (x & 1) == 0) refGroup = c.Decode(_refDm) & 0xFF;
                    ref var b = ref buf[x];
                    uint endpointRef;
                    if ((y & 1) != 0) endpointRef = b.EndpointReference;
                    else
                    {
                        endpointRef = refGroup & 3; refGroup >>= 2;
                        b.EndpointReference = refGroup & 3; refGroup >>= 2;
                    }
                    if (endpointRef == 0)
                    {
                        colorIdx += c.Decode(_endpointDm[0]);
                        if (colorIdx >= numColor) colorIdx -= (uint)numColor;
                        b.ColorIdx = colorIdx;
                    }
                    else if (endpointRef == 1) b.ColorIdx = colorIdx;
                    else colorIdx = b.ColorIdx;
                    uint selIdx = c.Decode(_selectorDm[0]);
                    if (visible)
                    {
                        var o = dst.AsSpan((y * ow + x) * 8);
                        BinaryPrimitives.WriteUInt32LittleEndian(o, _colorEndpoints[colorIdx]);
                        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], _colorSelectors[selIdx]);
                    }
                }
            }
        }

        private void UnpackDxt5(ref Codec c, byte[] dst, int ow, int oh)
        {
            int numColor = _colorEndpoints.Length, numAlpha = _alphaEndpoints.Length;
            int width = (ow + 1) & ~1, height = (oh + 1) & ~1;
            var buf = new BufEl[width];
            uint colorIdx = 0, alphaIdx = 0, refGroup = 0;
            for (int y = 0; y < height; y++)
            {
                bool visible = y < oh;
                for (int x = 0; x < width; x++)
                {
                    visible = visible && x < ow;
                    if ((y & 1) == 0 && (x & 1) == 0) refGroup = c.Decode(_refDm) & 0xFF;
                    ref var b = ref buf[x];
                    uint endpointRef;
                    if ((y & 1) != 0) endpointRef = b.EndpointReference;
                    else
                    {
                        endpointRef = refGroup & 3; refGroup >>= 2;
                        b.EndpointReference = refGroup & 3; refGroup >>= 2;
                    }
                    if (endpointRef == 0)
                    {
                        colorIdx += c.Decode(_endpointDm[0]);
                        if (colorIdx >= numColor) colorIdx -= (uint)numColor;
                        b.ColorIdx = colorIdx;
                        alphaIdx += c.Decode(_endpointDm[1]);
                        if (alphaIdx >= numAlpha) alphaIdx -= (uint)numAlpha;
                        b.Alpha0Idx = alphaIdx;
                    }
                    else if (endpointRef == 1) { b.ColorIdx = colorIdx; b.Alpha0Idx = alphaIdx; }
                    else { colorIdx = b.ColorIdx; alphaIdx = b.Alpha0Idx; }
                    uint cSel = c.Decode(_selectorDm[0]);
                    uint aSel = c.Decode(_selectorDm[1]);
                    if (visible)
                    {
                        var o = dst.AsSpan((y * ow + x) * 16);
                        int s = (int)aSel * 3;
                        BinaryPrimitives.WriteUInt32LittleEndian(o, _alphaEndpoints[alphaIdx] | ((uint)_alphaSelectors[s] << 16));
                        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], _alphaSelectors[s + 1] | ((uint)_alphaSelectors[s + 2] << 16));
                        BinaryPrimitives.WriteUInt32LittleEndian(o[8..], _colorEndpoints[colorIdx]);
                        BinaryPrimitives.WriteUInt32LittleEndian(o[12..], _colorSelectors[cSel]);
                    }
                }
            }
        }

        private void UnpackDxt5a(ref Codec c, byte[] dst, int ow, int oh)
        {
            int numAlpha = _alphaEndpoints.Length;
            int width = (ow + 1) & ~1, height = (oh + 1) & ~1;
            var buf = new BufEl[width];
            uint alphaIdx = 0, refGroup = 0;
            for (int y = 0; y < height; y++)
            {
                bool visible = y < oh;
                for (int x = 0; x < width; x++)
                {
                    visible = visible && x < ow;
                    if ((y & 1) == 0 && (x & 1) == 0) refGroup = c.Decode(_refDm) & 0xFF;
                    ref var b = ref buf[x];
                    uint endpointRef;
                    if ((y & 1) != 0) endpointRef = b.EndpointReference;
                    else
                    {
                        endpointRef = refGroup & 3; refGroup >>= 2;
                        b.EndpointReference = refGroup & 3; refGroup >>= 2;
                    }
                    if (endpointRef == 0)
                    {
                        alphaIdx += c.Decode(_endpointDm[1]);
                        if (alphaIdx >= numAlpha) alphaIdx -= (uint)numAlpha;
                        b.Alpha0Idx = alphaIdx;
                    }
                    else if (endpointRef == 1) b.Alpha0Idx = alphaIdx;
                    else alphaIdx = b.Alpha0Idx;
                    uint aSel = c.Decode(_selectorDm[1]);
                    if (visible)
                    {
                        var o = dst.AsSpan((y * ow + x) * 8);
                        int s = (int)aSel * 3;
                        BinaryPrimitives.WriteUInt32LittleEndian(o, _alphaEndpoints[alphaIdx] | ((uint)_alphaSelectors[s] << 16));
                        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], _alphaSelectors[s + 1] | ((uint)_alphaSelectors[s + 2] << 16));
                    }
                }
            }
        }

        private void UnpackDxn(ref Codec c, byte[] dst, int ow, int oh)
        {
            int numAlpha = _alphaEndpoints.Length;
            int width = (ow + 1) & ~1, height = (oh + 1) & ~1;
            var buf = new BufEl[width];
            uint a0 = 0, a1 = 0, refGroup = 0;
            for (int y = 0; y < height; y++)
            {
                bool visible = y < oh;
                for (int x = 0; x < width; x++)
                {
                    visible = visible && x < ow;
                    if ((y & 1) == 0 && (x & 1) == 0) refGroup = c.Decode(_refDm) & 0xFF;
                    ref var b = ref buf[x];
                    uint endpointRef;
                    if ((y & 1) != 0) endpointRef = b.EndpointReference;
                    else
                    {
                        endpointRef = refGroup & 3; refGroup >>= 2;
                        b.EndpointReference = refGroup & 3; refGroup >>= 2;
                    }
                    if (endpointRef == 0)
                    {
                        a0 += c.Decode(_endpointDm[1]); if (a0 >= numAlpha) a0 -= (uint)numAlpha; b.Alpha0Idx = a0;
                        a1 += c.Decode(_endpointDm[1]); if (a1 >= numAlpha) a1 -= (uint)numAlpha; b.Alpha1Idx = a1;
                    }
                    else if (endpointRef == 1) { b.Alpha0Idx = a0; b.Alpha1Idx = a1; }
                    else { a0 = b.Alpha0Idx; a1 = b.Alpha1Idx; }
                    uint s0 = c.Decode(_selectorDm[1]);
                    uint s1 = c.Decode(_selectorDm[1]);
                    if (visible)
                    {
                        var o = dst.AsSpan((y * ow + x) * 16);
                        int p0 = (int)s0 * 3, p1 = (int)s1 * 3;
                        BinaryPrimitives.WriteUInt32LittleEndian(o, _alphaEndpoints[a0] | ((uint)_alphaSelectors[p0] << 16));
                        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], _alphaSelectors[p0 + 1] | ((uint)_alphaSelectors[p0 + 2] << 16));
                        BinaryPrimitives.WriteUInt32LittleEndian(o[8..], _alphaEndpoints[a1] | ((uint)_alphaSelectors[p1] << 16));
                        BinaryPrimitives.WriteUInt32LittleEndian(o[12..], _alphaSelectors[p1 + 1] | ((uint)_alphaSelectors[p1 + 2] << 16));
                    }
                }
            }
        }
    }
}
