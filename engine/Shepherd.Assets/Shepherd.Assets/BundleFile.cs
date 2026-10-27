using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Shepherd.Assets.Compression;

namespace Shepherd.Assets;

public sealed record BundleNode(string Name, long Offset, long Size, uint Flags)
{
    /// <summary>Node flag 0x4: the node is a SerializedFile.</summary>
    public bool IsSerializedFile => (Flags & 4) != 0;
}

/// <summary>
/// UnityFS asset bundle (format 6/7/8). Opens lazily: only the header + blocks info are read on Open;
/// node data is decompressed per storage block on demand (with a small block cache), so ranged reads
/// of .resS streams do not inflate the whole bundle.
/// </summary>
public sealed class BundleFile : IDisposable
{
    public const uint CompressionMask = 0x3F;

    private readonly struct Block
    {
        public readonly long UOffset;   // offset in the uncompressed data stream
        public readonly long COffset;   // offset in the file
        public readonly uint USize, CSize;
        public readonly ushort Flags;
        public Block(long u, long c, uint us, uint cs, ushort f) { UOffset = u; COffset = c; USize = us; CSize = cs; Flags = f; }
    }

    private readonly FileStream _fs;
    private readonly Block[] _blocks;
    private readonly List<BundleNode> _nodes;
    private readonly Dictionary<string, BundleNode> _byName;
    private readonly object _lock = new();
    // tiny LRU of decompressed blocks (blocks are usually 128 KiB LZ4 chunks)
    private readonly LinkedList<(int idx, byte[] data)> _cache = new();
    private const int CacheBlocks = 16;
    /// <summary><see cref="SerializedFile.Clock"/> at the last read (the engine drops idle block caches).</summary>
    public long LastUse;
    public long CachedBytes { get { lock (_lock) { long n = 0; foreach (var e in _cache) n += e.data.Length; return n; } } }
    /// <summary>Drop the decompressed-block cache (it refills on the next read). Returns the bytes released.</summary>
    public long TrimCache() { lock (_lock) { long n = 0; foreach (var e in _cache) n += e.data.Length; _cache.Clear(); return n; } }

    public string Path { get; }
    public IReadOnlyList<BundleNode> Nodes => _nodes;
    public int FormatVersion { get; }
    public string UnityVersion { get; }      // the "player version" field ("5.x.x")
    public string UnityRevision { get; }     // "2022.3.62f2"
    public uint HeaderFlags { get; }
    /// <summary>Histogram of storage-block compression types: 0 none, 1 LZMA, 2 LZ4, 3 LZ4HC.</summary>
    public IReadOnlyDictionary<int, int> BlockCompressionHistogram { get; }
    public int BlockCount => _blocks.Length;

    private BundleFile(string path, FileStream fs, int ver, string uv, string rev, uint flags, Block[] blocks, List<BundleNode> nodes)
    {
        Path = path; _fs = fs; FormatVersion = ver; UnityVersion = uv; UnityRevision = rev; HeaderFlags = flags;
        _blocks = blocks; _nodes = nodes;
        _byName = new Dictionary<string, BundleNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) _byName[n.Name] = n;
        var h = new Dictionary<int, int>();
        foreach (var b in blocks) { int c = b.Flags & 0x3F; h[c] = h.TryGetValue(c, out var k) ? k + 1 : 1; }
        BlockCompressionHistogram = h;
    }

    public static BundleFile Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        try
        {
            return Parse(path, fs);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static string ReadCString(Stream s)
    {
        var sb = new StringBuilder();
        int c;
        while ((c = s.ReadByte()) > 0) sb.Append((char)c);
        if (c < 0) throw new EndOfStreamException();
        return sb.ToString();
    }

    private static void ReadExactly(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf.Slice(off));
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private static BundleFile Parse(string path, FileStream fs)
    {
        string sig = ReadCString(fs);
        if (sig != "UnityFS") throw new InvalidDataException($"{path}: not a UnityFS bundle (signature '{sig}')");
        Span<byte> b4 = stackalloc byte[8];
        ReadExactly(fs, b4[..4]);
        int version = BinaryPrimitives.ReadInt32BigEndian(b4);
        string unityVer = ReadCString(fs);
        string unityRev = ReadCString(fs);
        Span<byte> hdr = stackalloc byte[20];
        ReadExactly(fs, hdr);
        long size = BinaryPrimitives.ReadInt64BigEndian(hdr);
        uint cInfo = BinaryPrimitives.ReadUInt32BigEndian(hdr[8..]);
        uint uInfo = BinaryPrimitives.ReadUInt32BigEndian(hdr[12..]);
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(hdr[16..]);
        if ((flags & 0x400) != 0) throw new NotSupportedException($"{path}: encrypted (UnityCN) bundles are not supported");
        if (version >= 7) fs.Position = (fs.Position + 15) & ~15L;

        byte[] infoC = new byte[cInfo];
        long afterHeader = fs.Position;
        if ((flags & 0x80) != 0) // blocks info at the end of the file
        {
            fs.Position = fs.Length - cInfo;
            ReadExactly(fs, infoC);
            fs.Position = afterHeader;
        }
        else
        {
            ReadExactly(fs, infoC);
        }
        byte[] info = new byte[uInfo];
        DecompressInto(flags & CompressionMask, infoC, info, path + " (blocks info)");

        var r = new SpanReader(info, bigEndian: true);
        r.Bytes(16); // uncompressed data hash
        int blockCount = r.I32();
        var raw = new (uint u, uint c, ushort f)[blockCount];
        for (int i = 0; i < blockCount; i++) raw[i] = (r.U32(), r.U32(), r.U16());
        int nodeCount = r.I32();
        var nodes = new List<BundleNode>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            long off = r.I64(), sz = r.I64();
            uint nf = r.U32();
            string name = r.CString();
            nodes.Add(new BundleNode(name, off, sz, nf));
        }

        if ((flags & 0x200) != 0) fs.Position = (fs.Position + 15) & ~15L; // BlockInfoNeedPaddingAtStart
        long dataStart = (flags & 0x80) != 0 ? afterHeader : fs.Position;

        var blocks = new Block[blockCount];
        long u = 0, c = dataStart;
        for (int i = 0; i < blockCount; i++)
        {
            blocks[i] = new Block(u, c, raw[i].u, raw[i].c, raw[i].f);
            u += raw[i].u; c += raw[i].c;
        }
        if (c > fs.Length) throw new InvalidDataException($"{path}: block table runs past end of file ({c} > {fs.Length})");
        return new BundleFile(path, fs, version, unityVer, unityRev, flags, blocks, nodes);
    }

    internal static void DecompressInto(uint compression, ReadOnlySpan<byte> src, Span<byte> dst, string what)
    {
        switch (compression)
        {
            case 0:
                if (src.Length != dst.Length) throw new InvalidDataException($"{what}: stored size mismatch");
                src.CopyTo(dst);
                break;
            case 1:
                Lzma.DecodeUnity(src, dst);
                break;
            case 2:
            case 3:
                int n = Lz4.Decode(src, dst);
                if (n != dst.Length) throw new InvalidDataException($"{what}: lz4 produced {n} bytes, expected {dst.Length}");
                break;
            default:
                throw new NotSupportedException($"{what}: compression type {compression}");
        }
    }

    public BundleNode GetNode(string name) => _byName.TryGetValue(name, out var n) ? n : null;
    public bool HasNode(string name) => _byName.ContainsKey(name);

    /// <summary>Whole uncompressed node.</summary>
    public byte[] ReadNode(string name)
    {
        var n = GetNode(name) ?? throw new FileNotFoundException($"node '{name}' not in bundle {Path}");
        if (n.Size > int.MaxValue) throw new NotSupportedException($"node {name} is larger than 2 GiB");
        var buf = GC.AllocateUninitializedArray<byte>((int)n.Size);
        ReadRange(n.Offset, buf);
        return buf;
    }

    /// <summary>Read a byte range of a node (offset relative to the node start).</summary>
    public byte[] ReadNodeRange(string name, long offset, long size)
    {
        var n = GetNode(name) ?? throw new FileNotFoundException($"node '{name}' not in bundle {Path}");
        if (offset < 0 || size < 0 || offset + size > n.Size)
            throw new ArgumentOutOfRangeException(nameof(offset), $"range {offset}+{size} outside node {name} ({n.Size} bytes)");
        var buf = GC.AllocateUninitializedArray<byte>(checked((int)size));
        ReadRange(n.Offset + offset, buf);
        return buf;
    }

    /// <summary>Read from the bundle's uncompressed data stream.</summary>
    public void ReadRange(long offset, Span<byte> dest)
    {
        if (dest.Length == 0) return;
        LastUse = SerializedFile.Clock;
        lock (_lock)
        {
            int bi = FindBlock(offset);
            long pos = offset;
            int written = 0;
            while (written < dest.Length)
            {
                if (bi >= _blocks.Length) throw new EndOfStreamException($"{Path}: read past end of data stream");
                ref readonly Block b = ref _blocks[bi];
                long inBlock = pos - b.UOffset;
                int take = (int)Math.Min(b.USize - inBlock, dest.Length - written);
                var slice = dest.Slice(written, take);
                if (inBlock == 0 && take == b.USize && GetCached(bi) is null)
                {
                    DecodeBlock(bi, slice); // whole block lands directly in the destination
                }
                else
                {
                    var data = GetBlock(bi);
                    data.AsSpan((int)inBlock, take).CopyTo(slice);
                }
                written += take; pos += take; bi++;
            }
        }
    }

    private int FindBlock(long offset)
    {
        int lo = 0, hi = _blocks.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (offset < _blocks[mid].UOffset) hi = mid - 1;
            else if (offset >= _blocks[mid].UOffset + _blocks[mid].USize) lo = mid + 1;
            else return mid;
        }
        throw new ArgumentOutOfRangeException(nameof(offset), $"{Path}: offset {offset} outside data stream");
    }

    private byte[] GetCached(int idx)
    {
        for (var n = _cache.First; n != null; n = n.Next)
            if (n.Value.idx == idx)
            {
                _cache.Remove(n); _cache.AddFirst(n);
                return n.Value.data;
            }
        return null;
    }

    private byte[] GetBlock(int idx)
    {
        var hit = GetCached(idx);
        if (hit != null) return hit;
        var data = new byte[_blocks[idx].USize];
        DecodeBlock(idx, data);
        _cache.AddFirst((idx, data));
        while (_cache.Count > CacheBlocks) _cache.RemoveLast();
        return data;
    }

    private void DecodeBlock(int idx, Span<byte> dest)
    {
        ref readonly Block b = ref _blocks[idx];
        uint comp = (uint)(b.Flags & 0x3F);
        _fs.Position = b.COffset;
        if (comp == 0)
        {
            ReadExactly(_fs, dest);
            return;
        }
        var tmp = ArrayPool<byte>.Shared.Rent((int)b.CSize);
        try
        {
            ReadExactly(_fs, tmp.AsSpan(0, (int)b.CSize));
            DecompressInto(comp, tmp.AsSpan(0, (int)b.CSize), dest, $"{Path} block {idx}");
        }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
    }

    public void Dispose()
    {
        lock (_lock) { _fs.Dispose(); _cache.Clear(); }
    }

    public override string ToString() => $"{System.IO.Path.GetFileName(Path)} ({_nodes.Count} nodes, {_blocks.Length} blocks)";
}
