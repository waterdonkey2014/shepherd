namespace Shepherd.Assets;

/// <summary>BC7 block decoder (all 8 modes), per the D3D11 BC7 specification.</summary>
internal static class Bc7
{
    // mode: subsets, partition bits, rotation bits, index-selection bit, color bits, alpha bits, endpoint pbits, shared pbits, index bits, index2 bits
    private static readonly int[,] Modes =
    {
        { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
        { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
        { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
        { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
        { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
        { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
        { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
        { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
    };

    internal static readonly ushort[] Partition2 =
    {
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    };

    // 3-subset partitions, 16 pixels each, as strings of subset digits
    private static readonly string[] Partition3Rows =
    {
        "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111",
        "0000000011221122", "0011001100220022", "0022002211111111", "0011001122112211",
        "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
        "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220",
        "0001001101121122", "0111001120012200", "0000112211221122", "0022002200221111",
        "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
        "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221",
        "0022110211020022", "0110011020022222", "0011012201220011", "0000200022112221",
        "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
        "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011",
        "0011112222000011", "0101010122222222", "0000000021212121", "0022112200221122",
        "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
        "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112",
        "0222011101110222", "0002111211120002", "0110011001102222", "0000000021122112",
        "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
        "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
    };

    internal static readonly byte[] Anchor2 =
    {
        15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15,
        15, 2, 8, 2, 2, 8, 8,15,  2, 8, 2, 2, 8, 8, 2, 2,
        15,15, 6, 8, 2, 8,15,15,  2, 8, 2, 2, 2,15,15, 6,
         6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15,
    };

    private static readonly byte[] Anchor3a =
    {
         3, 3,15,15, 8, 3,15,15,  8, 8, 6, 6, 6, 5, 3, 3,
         3, 3, 8,15, 3, 3, 6,10,  5, 8, 8, 6, 8, 5,15,15,
         8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,
         3,15, 5, 5, 5, 8, 5,10,  5,10, 8,13,15,12, 3, 3,
    };

    private static readonly byte[] Anchor3b =
    {
        15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8,
        15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
        15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8,
        15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8,
    };

    private static readonly byte[,] Partition3 = BuildP3();

    private static byte[,] BuildP3()
    {
        var p = new byte[64, 16];
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 16; j++) p[i, j] = (byte)(Partition3Rows[i][j] - '0');
        return p;
    }

    private static readonly int[] W2 = { 0, 21, 43, 64 };
    private static readonly int[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    private static readonly int[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    private static int[] Weights(int bits) => bits == 2 ? W2 : bits == 3 ? W3 : W4;

    /// <summary>Self-check of the partition/anchor tables: each anchor must lie in its subset.</summary>
    internal static string ValidateTables()
    {
        for (int p = 0; p < 64; p++)
        {
            if (((Partition2[p] >> Anchor2[p]) & 1) != 1) return $"anchor2 mismatch at partition {p}";
            if ((Partition2[p] & 1) != 0) return $"partition2 {p}: pixel 0 not in subset 0";
            if (Partition3[p, Anchor3a[p]] != 1) return $"anchor3a mismatch at partition {p}";
            if (Partition3[p, Anchor3b[p]] != 2) return $"anchor3b mismatch at partition {p}";
            if (Partition3[p, 0] != 0) return $"partition3 {p}: pixel 0 not in subset 0";
        }
        return null;
    }

    private ref struct Bits
    {
        private readonly ulong _lo, _hi;
        private int _pos;
        public Bits(ReadOnlySpan<byte> b)
        {
            _lo = BitConverter.ToUInt64(b);
            _hi = BitConverter.ToUInt64(b.Slice(8));
            _pos = 0;
        }
        public int Read(int n)
        {
            if (n == 0) return 0;
            ulong v;
            if (_pos >= 64) v = _hi >> (_pos - 64);
            else if (_pos + n <= 64) v = _lo >> _pos;
            else v = (_lo >> _pos) | (_hi << (64 - _pos));
            _pos += n;
            return (int)(v & ((1UL << n) - 1));
        }
    }

    internal static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> tile)
    {
        int mode = 0;
        while (mode < 8 && (block[0] & (1 << mode)) == 0) mode++;
        if (mode == 8) { tile.Clear(); return; } // reserved: transparent black

        var br = new Bits(block);
        br.Read(mode + 1);
        int ns = Modes[mode, 0], pb = Modes[mode, 1], rb = Modes[mode, 2], isb = Modes[mode, 3];
        int cb = Modes[mode, 4], ab = Modes[mode, 5], epb = Modes[mode, 6], spb = Modes[mode, 7];
        int ib = Modes[mode, 8], ib2 = Modes[mode, 9];

        int partition = br.Read(pb);
        int rotation = br.Read(rb);
        int indexSel = br.Read(isb);

        Span<int> ep = stackalloc int[6 * 4]; // [endpoint][channel]
        int nep = ns * 2;
        for (int c = 0; c < 3; c++)
            for (int e = 0; e < nep; e++) ep[e * 4 + c] = br.Read(cb);
        if (ab > 0)
            for (int e = 0; e < nep; e++) ep[e * 4 + 3] = br.Read(ab);

        int colorBits = cb, alphaBits = ab;
        if (epb != 0)
        {
            for (int e = 0; e < nep; e++)
            {
                int p = br.Read(1);
                for (int c = 0; c < 4; c++) ep[e * 4 + c] = (ep[e * 4 + c] << 1) | p;
            }
            colorBits++; if (ab > 0) alphaBits++;
        }
        else if (spb != 0)
        {
            for (int s = 0; s < ns; s++)
            {
                int p = br.Read(1);
                for (int e = s * 2; e < s * 2 + 2; e++)
                    for (int c = 0; c < 4; c++) ep[e * 4 + c] = (ep[e * 4 + c] << 1) | p;
            }
            colorBits++; if (ab > 0) alphaBits++;
        }
        for (int e = 0; e < nep; e++)
        {
            for (int c = 0; c < 3; c++) ep[e * 4 + c] = Expand(ep[e * 4 + c], colorBits);
            ep[e * 4 + 3] = ab > 0 ? Expand(ep[e * 4 + 3], alphaBits) : 255;
        }

        Span<int> subsetOf = stackalloc int[16];
        for (int i = 0; i < 16; i++)
            subsetOf[i] = ns == 1 ? 0 : ns == 2 ? (Partition2[partition] >> i) & 1 : Partition3[partition, i];

        bool IsAnchor(int i) => i == 0
            || (ns == 2 && i == Anchor2[partition])
            || (ns == 3 && (i == Anchor3a[partition] || i == Anchor3b[partition]));

        Span<int> idx1 = stackalloc int[16];
        Span<int> idx2 = stackalloc int[16];
        for (int i = 0; i < 16; i++) idx1[i] = br.Read(IsAnchor(i) ? ib - 1 : ib);
        if (ib2 > 0)
            for (int i = 0; i < 16; i++) idx2[i] = br.Read(i == 0 ? ib2 - 1 : ib2);

        for (int i = 0; i < 16; i++)
        {
            int s = subsetOf[i];
            int e0 = s * 2 * 4, e1 = (s * 2 + 1) * 4;
            int ci, ai, cbits, abits;
            if (ib2 == 0) { ci = ai = idx1[i]; cbits = abits = ib; }
            else if (indexSel == 0) { ci = idx1[i]; cbits = ib; ai = idx2[i]; abits = ib2; }
            else { ci = idx2[i]; cbits = ib2; ai = idx1[i]; abits = ib; }
            var cw = Weights(cbits); var aw = Weights(abits);
            int r = Interp(ep[e0], ep[e1], cw[ci]);
            int g = Interp(ep[e0 + 1], ep[e1 + 1], cw[ci]);
            int b = Interp(ep[e0 + 2], ep[e1 + 2], cw[ci]);
            int a = Interp(ep[e0 + 3], ep[e1 + 3], aw[ai]);
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }
            tile[i * 4] = (byte)r; tile[i * 4 + 1] = (byte)g; tile[i * 4 + 2] = (byte)b; tile[i * 4 + 3] = (byte)a;
        }
    }

    private static int Expand(int v, int bits) => bits >= 8 ? v : (v << (8 - bits)) | (v >> (2 * bits - 8));
    private static int Interp(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;
}
