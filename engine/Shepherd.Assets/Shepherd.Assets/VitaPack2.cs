using System.Buffers.Binary;

namespace Shepherd.Assets;

/// <summary>
/// Device kit #10, track io (vita/notes/kit10-io.md): the CHUNKED Vita pack, version 2 (<c>shepherd.pk2</c>, written by tools/VitaPack
/// --repack from a version-1 pack). The LOGICAL file is the version-1 pack exactly -- the same TOC bytes, the same payload bytes at the same
/// offsets (records, then texture images), so every object, record, PackImage offset and read-order map means the same -- but the payload
/// is stored as independently decodable CHUNKS: the records region in fixed tiles, each texture image in its own tiles (so byte-identical
/// images share one stored copy), each tile stored raw, LZ4 or zstd (the codec with the best estimated device time: the card's rate against
/// the decoder's), placed on the card in the order a recorded boot first read them (visit order), each starting on the card's block
/// alignment, each with a hash of its stored bytes (Verify files) and of its decoded bytes (the decoder's own check). Lossless: the builder
/// decodes every chunk again and compares it with the version-1 bytes before the pack is kept.
///
/// Layout (little-endian):
///   header (128 B): 0 magic "SHPK" | 4 u16 version 2 | 6 u16 header size 128 | 8 u32 flags (version 1's: top-down images, properties)
///     | 12 u32 FNV-1a of the decoded TOC (= version 1's) | 16 u64 version 1's TOC offset | 24 u64 decoded TOC size | 32 u64 payload offset
///     (LOGICAL, = version 1's) | 40 u64 payload size (LOGICAL) | 48 u32 objects | 52 u32 files | 56 u32 bundles | 60 u32 strings (= version 1's)
///     | 64 u32 chunks | 68 u32 chunk entry size (40) | 72 u64 chunk table offset | 80 u32 chunk table bytes | 84 u32 FNV-1a of the chunk table
///     | 88 u64 TOC offset (stored) | 96 u32 TOC stored length | 100 u8 TOC codec | 101..103 0 | 104 u32 alignment | 108 u32 chunk tile size
///     | 112 u64 version 1 file size | 120 u32 FNV-1a of header bytes 0..119 | 124 u32 0
///   chunk table (sorted by logical offset, covering the payload with no gap): n x 40 B
///     0 u64 logical offset (from the payload start) | 8 u32 logical length | 12 u32 stored length | 16 u64 stored offset (in the file)
///     | 24 u8 codec | 25 u8 0 | 26 u16 flags (1 image tile, 2 a copy of an earlier chunk's stored bytes) | 28 u32 XXH32 of the stored bytes
///     | 32 u32 XXH32 of the decoded bytes | 36 u32 0
///   then the stored TOC, then the stored chunks (visit order), each at a multiple of the alignment.
/// </summary>
public static class VitaPack2Format
{
    public const ushort Version = 2;
    public const int HeaderSize = 128, EntrySize = 40;
    public const string FileName = "shepherd.pk2";
    public const byte CodecStored = 0, CodecLz4 = 1, CodecZstd = 2;
    public const ushort ChunkImage = 1, ChunkCopy = 2;
    public static string CodecName(byte c) => c switch { CodecStored => "stored", CodecLz4 => "lz4", CodecZstd => "zstd", _ => "codec" + c.ToString() };
    /// <summary>Kit #10 io review: the TOC property a bake writes (tools/VitaPack PackWriter, from kit #10 on): a hash of the version-1 payload
    /// ("xxh32-1m:" + 16 hex: XXH32 with seeds 0 and 1 over the list of the payload's per-1-MiB XXH32s). It sits in the TOC, so the TOC's
    /// checksum -- which <see cref="CheckMadeFrom"/> compares -- covers the payload's content too: a re-bake whose bytes changed but whose TOC
    /// layout did not (same objects, same sizes) has another TOC checksum, and a shepherd.pk2 of the old bake is refused at open. A pack baked
    /// before kit #10 has no such property: the reader then also compares a few sampled chunks of the version-1 file with the chunk table's
    /// decoded hashes at open (<see cref="PackReader.V2MadeFromSamples"/>), and stage-owner.sh STAGE_PK2 checks every chunk (tools/VitaPack
    /// --made-from).</summary>
    public const string PayloadHashProperty = "payload-hash";

    /// <summary>Kit #10 io: throws (InvalidDataException) unless the chunked pack at <paramref name="v2Path"/> was made from the version-1 file at
    /// <paramref name="v1Path"/>: its header's version-1 fields (flags, the TOC's checksum / offset / size, the payload's offset / size, the counts:
    /// bytes 8..63) and the version-1 file size it records must be that file's. A shepherd.pk2 left on the card beside a re-baked shepherd.pack
    /// is then refused and the new pack read (slower, never different). Two header reads at open, no managed allocation. (Review: the header
    /// alone says nothing about the payload's bytes when a re-bake keeps the TOC's layout -- the TOC's <see cref="PayloadHashProperty"/> does,
    /// and a pack without it gets the sampled check in <see cref="PackReader.Open"/>.)</summary>
    public static void CheckMadeFrom(string v2Path, string v1Path)
    {
        Span<byte> a = stackalloc byte[HeaderSize], b = stackalloc byte[VitaPackFormat.HeaderSize];
        long v1Size; int na, nb;
        using (var h = File.OpenHandle(v2Path, FileMode.Open, FileAccess.Read, FileShare.Read)) na = RandomAccess.Read(h, a, 0);
        using (var h = File.OpenHandle(v1Path, FileMode.Open, FileAccess.Read, FileShare.Read)) { v1Size = RandomAccess.GetLength(h); nb = RandomAccess.Read(h, b, 0); }
        if (na != HeaderSize || nb != VitaPackFormat.HeaderSize || !a.Slice(8, 56).SequenceEqual(b.Slice(8, 56)) || BinaryPrimitives.ReadInt64LittleEndian(a.Slice(112)) != v1Size)
            throw new InvalidDataException($"{v2Path} was made from another {VitaPackFormat.PackFileName} (its TOC {BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(12)):x8}, " +
                $"{BinaryPrimitives.ReadInt64LittleEndian(a.Slice(112))} B; this pack's TOC {BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12)):x8}, {v1Size} B)");
    }

    /// <summary>XXH32 (Yann Collet's xxHash, 32-bit, seed 0): the chunk hashes. Small, fast on the Cortex-A9 (4 bytes per round), the same
    /// values on the Mac and the Vita.</summary>
    public static uint Xxh32(ReadOnlySpan<byte> p, uint seed = 0)
    {
        const uint P1 = 2654435761u, P2 = 2246822519u, P3 = 3266489917u, P4 = 668265263u, P5 = 374761393u;
        int n = p.Length, i = 0; uint h;
        if (n >= 16)
        {
            uint v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            int lim = n - 16;
            do
            {
                v1 = Rotl(v1 + BinaryPrimitives.ReadUInt32LittleEndian(p[i..]) * P2, 13) * P1; i += 4;
                v2 = Rotl(v2 + BinaryPrimitives.ReadUInt32LittleEndian(p[i..]) * P2, 13) * P1; i += 4;
                v3 = Rotl(v3 + BinaryPrimitives.ReadUInt32LittleEndian(p[i..]) * P2, 13) * P1; i += 4;
                v4 = Rotl(v4 + BinaryPrimitives.ReadUInt32LittleEndian(p[i..]) * P2, 13) * P1; i += 4;
            } while (i <= lim);
            h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
        }
        else h = seed + P5;
        h += (uint)n;
        while (i + 4 <= n) { h = Rotl(h + BinaryPrimitives.ReadUInt32LittleEndian(p[i..]) * P3, 17) * P4; i += 4; }
        while (i < n) { h = Rotl(h + p[i] * P5, 11) * P1; i++; }
        h ^= h >> 15; h *= P2; h ^= h >> 13; h *= P3; h ^= h >> 16;
        return h;
    }
    static uint Rotl(uint x, int r) => (x << r) | (x >> (32 - r));

    /// <summary>Sorts <paramref name="key"/>[0..n) ascending and moves <paramref name="item"/> with it (heapsort: no generic instantiation in the
    /// AOT images, no allocation).</summary>
    public static void SortBy(long[] key, int[] item, int n)
    {
        for (int i = n / 2 - 1; i >= 0; i--) Sift(key, item, i, n);
        for (int end = n - 1; end > 0; end--)
        {
            (key[0], key[end]) = (key[end], key[0]); (item[0], item[end]) = (item[end], item[0]);
            Sift(key, item, 0, end);
        }
    }
    static void Sift(long[] key, int[] item, int i, int n)
    {
        for (;;)
        {
            int c = 2 * i + 1; if (c >= n) return;
            if (c + 1 < n && (key[c + 1] > key[c] || (key[c + 1] == key[c] && item[c + 1] > item[c]))) c++;
            if (key[c] < key[i] || (key[c] == key[i] && item[c] <= item[i])) return;
            (key[i], key[c]) = (key[c], key[i]); (item[i], item[c]) = (item[c], item[i]);
            i = c;
        }
    }

    /// <summary>One chunk table entry.</summary>
    public struct Chunk
    {
        public long Logical; public int LogicalLen, StoredLen; public long Stored; public byte Codec; public ushort Flags; public uint StoredHash, LogicalHash;
        public static Chunk Read(ReadOnlySpan<byte> e) => new()
        {
            Logical = BinaryPrimitives.ReadInt64LittleEndian(e), LogicalLen = BinaryPrimitives.ReadInt32LittleEndian(e[8..]), StoredLen = BinaryPrimitives.ReadInt32LittleEndian(e[12..]),
            Stored = BinaryPrimitives.ReadInt64LittleEndian(e[16..]), Codec = e[24], Flags = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]),
            StoredHash = BinaryPrimitives.ReadUInt32LittleEndian(e[28..]), LogicalHash = BinaryPrimitives.ReadUInt32LittleEndian(e[32..]),
        };
        public void Write(Span<byte> e)
        {
            e[..EntrySize].Clear();
            BinaryPrimitives.WriteInt64LittleEndian(e, Logical); BinaryPrimitives.WriteInt32LittleEndian(e[8..], LogicalLen); BinaryPrimitives.WriteInt32LittleEndian(e[12..], StoredLen);
            BinaryPrimitives.WriteInt64LittleEndian(e[16..], Stored); e[24] = Codec; BinaryPrimitives.WriteUInt16LittleEndian(e[26..], Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(e[28..], StoredHash); BinaryPrimitives.WriteUInt32LittleEndian(e[32..], LogicalHash);
        }
    }
}

/// <summary>The decoders a chunked pack needs, supplied by the host program: on the Vita the host's native LZ4 / zstd (Shepherd.Engine
/// Runtime/K10Io.cs over vita/shepherd/k10_io.inc.c), on the Mac the installed libraries (the engine's K10Io, the builder's NativeCodecs).
/// An interface, not a delegate (no generic delegate instantiation in the AOT images: eboot span). <see cref="Decode"/> returns the bytes
/// written (must equal dst.Length for a valid chunk) or a negative error. <paramref name="ctx"/>: which thread calls -- 0 the reading
/// thread, 1 the decode-ahead worker, 2 the preload's thread, 3 Verify files -- one decoder state each, so no two threads share one (review:
/// K10Io.VerifyFiles runs one Verify at a time, and the host's zstd takes a context per call -- a second caller of a busy ctx gets its own).</summary>
public interface IPackDecoder
{
    unsafe int Decode(byte codec, byte* src, int srcLen, byte* dst, int dstLen, int ctx);
    string Describe();
}

/// <summary>Kit #10 io: the logical reads of a run, in order (offset, length: what the readers asked for, record by record) -- the input of
/// tools/VitaPack --repack --order (the visit-order layout). Null = none (every normal run). The engine sets it with SHEPHERD_K10_READLOG.</summary>
public interface IPackVisitLog { void OnVisit(long offset, int length); }

/// <summary>Kit #10 io: I/O PRIORITY LANES. A read the player waits on (the reading thread's on-demand pack reads) is FOREGROUND; the
/// decode-ahead worker's and the preload's reads are BACKGROUND and do not start while a foreground read is in flight or waiting (each
/// background read is at most one 128 KiB card request, so a foreground read waits behind at most one of them). The card serves one request
/// at a time; this only orders them. Off = kit #9 (no gate).</summary>
public static class IoLanes
{
    public static bool On = true;
    /// <summary>The longest a background read waits for the foreground lane to clear (then it goes anyway: a long foreground burst never
    /// starves the worker for good).</summary>
    public static int MaxWaitMs = 40;
    static int _fg; static readonly object _l = new();
    public static long BgWaits, FgReads; public static double BgWaitedMs;
    /// <summary>When the last foreground read ended (Stopwatch ticks): the write discipline's idle test (Shepherd.Engine Runtime/K10Io.cs).</summary>
    public static long LastFgTicks;
    public static void FgEnter() { if (On) { Interlocked.Increment(ref _fg); FgReads++; } }
    public static void FgExit() { if (On) { LastFgTicks = System.Diagnostics.Stopwatch.GetTimestamp(); if (Interlocked.Decrement(ref _fg) == 0) lock (_l) Monitor.PulseAll(_l); } }
    public static void BgGate()
    {
        if (!On || Volatile.Read(ref _fg) == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), lim = t0 + MaxWaitMs * System.Diagnostics.Stopwatch.Frequency / 1000;
        lock (_l)
        {
            while (Volatile.Read(ref _fg) > 0)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp(); if (now >= lim) break;
                Monitor.Wait(_l, (int)Math.Max(1, (lim - now) * 1000 / System.Diagnostics.Stopwatch.Frequency));
            }
            BgWaits++; BgWaitedMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }
}

/// <summary>Kit #10 io, the version-2 reader (see <see cref="VitaPack2Format"/>). Everything above <c>Bytes</c> / <c>ReadExact</c> is the
/// version-1 reader unchanged; here the logical bytes come from decoded chunks:
/// <list type="bullet">
/// <item>a FIXED PAGE POOL (<see cref="V2Pages"/> pages of one tile each, native memory: nothing in the GC heap, nothing allocated after
///   Open) holds decoded chunks; a record inside one chunk is served from its page, a record across chunks is put together in a scratch page,
///   a big read (images, big raw records) is decoded straight into the caller's buffer (no page, no copy) -- runs of chunks that sit next to
///   each other on the card in one read;</item>
/// <item>the B-4 DECODE-AHEAD WORKER (<see cref="V2DecodeAhead"/>; its thread pinned like the k4 read-ahead: the Vita's 4th core, else
///   USER_2) reads the chunks that follow on the card (the visit order: what the recorded run read next) in 128 KiB card requests and decodes
///   them into free pages, up to <see cref="V2AheadPages"/> pages ahead of the reader; the reader takes a page the worker is still filling
///   (bounded wait, then reads it itself) or decoded already (no card, no decode on the reading thread);</item>
/// <item>DEPENDENCY-PARALLEL LOADS (<see cref="Prefetch"/>): when a bundle opens, the chunks of all its files' records are queued for the
///   worker as one batch, so a load's dependencies are read and decoded side by side, ahead of the instantiation that walks them;</item>
/// <item>the worker's and the preload's card reads are background reads (<see cref="IoLanes"/>).</item>
/// </list>
/// The bytes every reader gets are the version-1 pack's; only when and where they are read and decoded changes.</summary>
public sealed unsafe partial class PackReader
{
    /// <summary>The host's decoders (Shepherd.Engine Runtime/K10Io.cs; tools/VitaPack NativeCodecs). Null = version-2 packs cannot be opened.</summary>
    public static IPackDecoder ChunkDecoder;
    /// <summary><see cref="Open"/> with a folder takes shepherd.pk2 when present (the engine: SHEPHERD_K10_PACK, default on with SHEPHERD_K10_IO).</summary>
    public static bool PreferV2;
    /// <summary>The codecs <see cref="ChunkDecoder"/> can decode, as a bit mask (1 &lt;&lt; codec; default stored + LZ4 + zstd): a chunked pack
    /// that uses another (a zstd pack on a device build without zstd) is refused at open -- the host then opens the version-1 file.</summary>
    public static int V2CodecMask = (1 << VitaPack2Format.CodecStored) | (1 << VitaPack2Format.CodecLz4) | (1 << VitaPack2Format.CodecZstd);
    /// <summary>The page pool, the ahead budget, the decode-ahead worker and dependency prefetch (set before Open).</summary>
    public static int V2Pages = 16, V2AheadPages = 11; public static bool V2DecodeAhead = true, V2Prefetch = true;
    /// <summary>Kit #10 io review: which decodes are checked against the chunk table's XXH32 of the decoded bytes (the engine's
    /// SHEPHERD_K10_HASHCHECK): 0 none (the first kit #10 build), 1 (default) the background threads' decodes -- the decode-ahead worker and
    /// the preload, on spare cores -- and, once a chunk failed there, every later decode of that chunk (the reading thread's too: a damaged
    /// chunk then ends in a clean error naming it, never in wrong bytes), 2 every decode and raw read (the reading thread pays the hashing).</summary>
    public static int V2HashCheck = 1;
    /// <summary>Kit #10 io review: the decode-ahead worker follows the reader into a new run of chunks BEHIND its furthest position (four card
    /// reads in a row, in separate requests, each right after the other on the card) -- the first build followed only forward moves, so one
    /// read of a chunk stored at the tail (outside the recorded visit order) left the worker reading ahead there for the rest of the session.
    /// False = the first build.</summary>
    public static bool V2AheadRuns = true;
    /// <summary>(<see cref="V2AheadRuns"/>: how many card reads must continue the first one before the worker moves back. 3 = four reads in a
    /// row: on the recorded New Game route (its read log replayed on the Mac, 5 runs) 1 moved the worker back 9-12 times (re-reads of two
    /// neighbouring chunks), 2 one or two times, 3 never -- so the visit order's own route runs as in the first build -- while a walk after one
    /// tail read is still followed at once: 1,586 vs 231 chunks of decode-ahead used, menu pack, medians of 5)</summary>
    public static int V2AheadRunNeed = 3;
    /// <summary>Kit #10 io review: a shepherd.pk2 beside a shepherd.pack whose TOC has no <see cref="VitaPack2Format.PayloadHashProperty"/> (a
    /// pack baked before kit #10) gets this many of its chunks compared with the version-1 file at open (each one card read of at most a tile;
    /// spread over the payload: the first and the last chunk among them). 0 = none (the header check only).</summary>
    public static int V2MadeFromSamples = 4;
    /// <summary>The visit log (SHEPHERD_K10_READLOG), both versions.</summary>
    public static IPackVisitLog VisitLog;
    /// <summary>Called first thing on the decode-ahead worker with its name (the engine pins it).</summary>
    public static Action<string> OnDecodeAheadThread;
    /// <summary>A bulk copy hook (the engine's DMA option, K10Io); null = memcpy. Only ever given buffers in native memory or pinned.</summary>
    public interface ICopier { void Copy(byte* dst, byte* src, int n, bool dstVram); }
    public static ICopier Copier;

    bool _v2; uint _cTableHash; int _tile, _align, _nChunks;
    long[] _cLog, _cAt; int[] _cLen, _cSLen; byte[] _cCodec; uint[] _cSHash, _cLHash; ushort[] _cFlags;
    int[] _rankOf, _byRank; int _nRanks;    // physical order: the distinct stored chunks by stored offset; a copy shares its original's rank
    bool[] _cSuspect;                        // review: a chunk whose decoded bytes failed their hash once (every later decode of it is checked)
    public bool IsChunked => _v2;
    public int ChunkCount => _nChunks;
    public long StoredFileBytes { get; private set; }
    /// <summary>Kit #10 io review: how <see cref="Open"/> made sure this shepherd.pk2 was made from the shepherd.pack beside it (the [k10] line).</summary>
    public string MadeFrom { get; private set; }

    // pages (native), their state; _pg guards the page table and the worker's queue (the reader also holds _lock)
    readonly object _pg = new();
    byte* _pgMem; int _nPg; int[] _pgChunk; byte[] _pgState, _pgSrc; long[] _pgUsed; bool[] _pgAhead, _pgAbandon; int _pin = -1, _lastChunk = -1; long _pgTick;
    /// <summary>A page decoded ahead that the reader has not used after this many of its page accesses is stale: no longer counted against
    /// the ahead budget, and the worker may reuse it (a prediction that did not come true never blocks the next one).</summary>
    public static int V2AheadAgeTicks = 128;   // (ticks: the reader's page lookups that left its held page; reads inside it do not tick)
    const byte SrcSeq = 1, SrcBatch = 2;
    const byte PgReady = 0, PgWorker = 1, PgReader = 2;
    byte* _stMain, _stWork, _scratch2, _stPre, _tilePre; const int RunMax = 128 << 10;
    // the worker
    Thread _waThread; bool _waStop, _waReady; int _waRank = -1, _readerRank = -1; int[] _batch, _wRun, _wPages; int _batchHead, _batchN; long _waGen;
    int _lastMissEnd = int.MinValue, _runLen; long _req, _lastMissReq = -1;   // review (V2AheadRuns): the stored rank right after the reading thread's previous card read, its request
    // stats
    public long V2CardReads, V2StoredBytes, V2Decoded, V2DecodedBytes, V2PageHits, V2PageMisses, V2AheadDecoded, V2AheadUsed, V2AheadWaits, V2AheadTimeouts,
        V2Direct, V2DirectBytes, V2Prefetched, V2PrefetchCalls, V2Straddles, V2PreloadReads, V2AheadBatch, V2WorkerReads, V2WorkerBytes, V2FastHits,
        V2HashChecked, V2HashBad, V2AheadReanchors;
    public double V2ReadMs, V2DecodeMs, V2AheadMs, V2AheadWaitedMs, V2DecodeAheadMs;

    /// <summary>Reads the version-2 header's tables; returns the decoded TOC (the version-1 TOC bytes).</summary>
    byte[] V2Open(byte[] hdr64, long tocSize)
    {
        if (ChunkDecoder == null) throw new InvalidDataException($"{Path}: a chunked (version 2) pack needs the host's decoders");
        var h = new byte[VitaPack2Format.HeaderSize]; ReadPhys(0, h);
        if (BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(6)) != VitaPack2Format.HeaderSize || VitaPackFormat.Fnv1a(h.AsSpan(0, 120)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(120)))
            throw new InvalidDataException($"{Path}: version-2 header checksum mismatch");
        _v2 = true;
        _nChunks = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(64));
        if (BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(68)) != VitaPack2Format.EntrySize) throw new InvalidDataException($"{Path}: chunk entry size");
        long tAt = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(72)); int tBytes = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(80));
        _cTableHash = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(84));
        long tocAt = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(88)); int tocStored = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(96)); byte tocCodec = h[100];
        _align = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(104)); _tile = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(108));
        if (tBytes != _nChunks * VitaPack2Format.EntrySize || _tile <= 0 || _tile > (1 << 20)) throw new InvalidDataException($"{Path}: chunk table size");
        // review: every stored range must lie inside the file -- the header, the table and the TOC sit at the front, so a copy cut short (an
        // interrupted push, a full card) would otherwise open cleanly and fail with an EndOfStream mid-session; refused here, the host reads
        // the version-1 file instead (slower, never different)
        StoredFileBytes = RandomAccess.GetLength(_h);
        if (tAt < VitaPack2Format.HeaderSize || tAt + tBytes > StoredFileBytes || tocAt < 0 || tocStored <= 0 || tocAt + tocStored > StoredFileBytes)
            throw new InvalidDataException($"{Path}: the chunk table or the TOC lies past the file's end ({StoredFileBytes} B): a truncated copy");
        var t = new byte[tBytes]; ReadPhys(tAt, t);
        if (VitaPackFormat.Fnv1a(t) != _cTableHash) throw new InvalidDataException($"{Path}: chunk table checksum mismatch (truncated or corrupt pack)");
        _cLog = new long[_nChunks]; _cAt = new long[_nChunks]; _cLen = new int[_nChunks]; _cSLen = new int[_nChunks]; _cCodec = new byte[_nChunks];
        _cSHash = new uint[_nChunks]; _cLHash = new uint[_nChunks]; _cFlags = new ushort[_nChunks];
        int maxStored = 0; long expect = 0;
        for (int i = 0; i < _nChunks; i++)
        {
            var c = VitaPack2Format.Chunk.Read(t.AsSpan(i * VitaPack2Format.EntrySize));
            if (c.Logical != expect || c.LogicalLen <= 0 || c.LogicalLen > _tile || c.StoredLen <= 0) throw new InvalidDataException($"{Path}: chunk {i} does not continue the payload");
            if (c.Stored < VitaPack2Format.HeaderSize || c.Stored + c.StoredLen > StoredFileBytes)
                throw new InvalidDataException($"{Path}: chunk {i} is stored at {c.Stored}+{c.StoredLen}, past the file's end ({StoredFileBytes} B): a truncated copy");
            _cLog[i] = c.Logical; _cLen[i] = c.LogicalLen; _cSLen[i] = c.StoredLen; _cAt[i] = c.Stored; _cCodec[i] = c.Codec; _cSHash[i] = c.StoredHash; _cLHash[i] = c.LogicalHash; _cFlags[i] = c.Flags;
            expect += c.LogicalLen; if (c.StoredLen > maxStored) maxStored = c.StoredLen;
            if (c.Codec == VitaPack2Format.CodecStored && c.StoredLen != c.LogicalLen) throw new InvalidDataException($"{Path}: stored chunk {i} length");
        }
        if (expect != PayloadSize) throw new InvalidDataException($"{Path}: the chunks cover {expect} of {PayloadSize} payload bytes");
        int used = 1 << tocCodec; for (int i = 0; i < _nChunks; i++) used |= 1 << _cCodec[i];
        if ((used & ~V2CodecMask) != 0) throw new InvalidDataException($"{Path}: the pack uses a codec this build cannot decode (codecs 0x{used:x}, this build 0x{V2CodecMask:x}: a zstd pack needs a SHEPHERD_K10_ZSTD build)");
        if (maxStored > RunMax) throw new InvalidDataException($"{Path}: a chunk is stored in {maxStored} bytes (the reader's run buffer holds {RunMax})");
        // physical order: distinct stored offsets ascending (a copy shares its original's stored bytes and rank)
        var at = new long[_nChunks]; var idx = new int[_nChunks];
        for (int i = 0; i < _nChunks; i++) { at[i] = _cAt[i]; idx[i] = i; }
        VitaPack2Format.SortBy(at, idx, _nChunks);
        _rankOf = new int[_nChunks]; _byRank = new int[_nChunks]; _nRanks = 0;
        for (int j = 0; j < _nChunks; j++)
        {
            if (j > 0 && at[j] == at[j - 1]) { _rankOf[idx[j]] = _nRanks - 1; continue; }
            _byRank[_nRanks] = idx[j]; _rankOf[idx[j]] = _nRanks; _nRanks++;
        }
        _cSuspect = new bool[_nChunks];
        // buffers: all now, on the opening thread (k4 review: nothing allocated afterwards, so the GC schedule does not move)
        _nPg = Math.Max(4, V2Pages);
        _pgMem = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal((IntPtr)((long)_nPg * _tile));
        _pgChunk = new int[_nPg]; _pgState = new byte[_nPg]; _pgSrc = new byte[_nPg]; _pgUsed = new long[_nPg]; _pgAhead = new bool[_nPg]; _pgAbandon = new bool[_nPg];
        for (int i = 0; i < _nPg; i++) _pgChunk[i] = -1;
        _stMain = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(RunMax);
        _scratch2 = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(_tile);
        if (V2DecodeAhead) { _stWork = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(RunMax); _batch = new int[4096]; _wRun = new int[8]; _wPages = new int[8]; }
        // the TOC
        var toc = new byte[checked((int)tocSize)];
        if (tocStored > RunMax || tocCodec == VitaPack2Format.CodecStored)
        {
            var st = new byte[tocStored]; ReadPhys(tocAt, st);
            if (tocCodec == VitaPack2Format.CodecStored) { if (tocStored != toc.Length) throw new InvalidDataException($"{Path}: TOC length"); st.CopyTo(toc, 0); }
            else fixed (byte* s = st) fixed (byte* d = toc) if (ChunkDecoder.Decode(tocCodec, s, tocStored, d, toc.Length, 0) != toc.Length) throw new InvalidDataException($"{Path}: the TOC does not decode");
        }
        else
        {
            ReadPhys(tocAt, new Span<byte>(_stMain, tocStored));
            fixed (byte* d = toc) if (ChunkDecoder.Decode(tocCodec, _stMain, tocStored, d, toc.Length, 0) != toc.Length) throw new InvalidDataException($"{Path}: the TOC does not decode");
        }
        return toc;
    }

    /// <summary>Kit #10 io review: this chunked pack was opened beside the version-1 file <paramref name="v1Path"/> (the header check passed):
    /// make sure it was made from THAT file's bytes. A TOC with <see cref="VitaPack2Format.PayloadHashProperty"/> already binds the payload
    /// (the header check compared the TOC's checksum); without it (a pack baked before kit #10) <see cref="V2MadeFromSamples"/> chunks spread
    /// over the payload (the first and the last among them) are read from the version-1 file and compared with the table's decoded hashes --
    /// a re-bake that re-encoded its images or changed many records is caught, a change of a few values may not be (stage-owner.sh STAGE_PK2
    /// checks every chunk). Throws InvalidDataException on a mismatch. Native scratch only; foreground reads on the opening thread.</summary>
    void V2CheckMadeFrom(string v1Path)
    {
        if (Properties.ContainsKey(VitaPack2Format.PayloadHashProperty)) { MadeFrom = "made from this shepherd.pack: its TOC (with the payload's hash) and header match"; return; }
        int n = Math.Min(V2MadeFromSamples, _nChunks);
        if (n <= 0) { MadeFrom = "made from this shepherd.pack: header match (a pack baked before kit #10; sampled check off)"; return; }
        using var h1 = File.OpenHandle(v1Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        for (int k = 0; k < n; k++)
        {
            int c = n == 1 ? 0 : (int)((long)k * (_nChunks - 1) / (n - 1));
            var dst = new Span<byte>(_scratch2, _cLen[c]);
            IoLanes.FgEnter();
            try
            {
                int done = 0;
                while (done < dst.Length)
                {
                    int m = RandomAccess.Read(h1, dst[done..], PayloadOffset + _cLog[c] + done);
                    if (m <= 0) throw new InvalidDataException($"{Path} was made from another {VitaPackFormat.PackFileName}: that file ends before chunk {c} (logical {_cLog[c]}+{_cLen[c]})");
                    done += m;
                }
            }
            finally { IoLanes.FgExit(); }
            if (VitaPack2Format.Xxh32(dst) != _cLHash[c])
                throw new InvalidDataException($"{Path} was made from another {VitaPackFormat.PackFileName}: chunk {c} (logical {_cLog[c]}+{_cLen[c]}) differs from that file's bytes (same TOC and size: a re-bake without a new shepherd.pk2)");
        }
        MadeFrom = $"made from this shepherd.pack: header match + {n} sampled chunks agree (a pack baked before kit #10: no payload hash in its TOC)";
    }

    void V2Dispose()
    {
        if (!_v2) return;
        lock (_pg) { _waStop = true; Monitor.PulseAll(_pg); }
        _waThread?.Join(2000);
        // (the native buffers are left to the process when the worker did not stop in time)
        if (_waThread == null || !_waThread.IsAlive)
        {
            FreeN(ref _pgMem); FreeN(ref _stMain); FreeN(ref _stWork); FreeN(ref _scratch2); FreeN(ref _stPre); FreeN(ref _tilePre);
        }
    }
    static void FreeN(ref byte* p) { if (p != null) { System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)p); p = null; } }

    /// <summary>Physical bytes of the file (the card), in MaxRead pieces. The reading thread's are foreground reads.</summary>
    void ReadPhys(long offset, Span<byte> dst)
    {
        int done = 0;
        while (done < dst.Length)
        {
            int k = RandomAccess.Read(_h, dst.Slice(done, Math.Min(MaxRead, dst.Length - done)), offset + done);
            if (k <= 0) throw new EndOfStreamException($"{Path}: read {dst.Length} B at {offset} past end");
            done += k;
        }
    }

    int FindChunk(long rel)
    {
        int lo = 0, hi = _nChunks - 1;
        while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (_cLog[mid] <= rel) lo = mid; else hi = mid - 1; }
        return lo;
    }

    byte* Page(int i) => _pgMem + (long)i * _tile;

    /// <summary>Chunk <paramref name="c"/> decoded from its stored bytes at <paramref name="src"/> into <paramref name="dst"/> (its logical length).</summary>
    void DecodeChunk(int c, byte* src, byte* dst, int ctx)
    {
        if (_cCodec[c] == VitaPack2Format.CodecStored) Buffer.MemoryCopy(src, dst, _cLen[c], _cLen[c]);
        else
        {
            int n = ChunkDecoder.Decode(_cCodec[c], src, _cSLen[c], dst, _cLen[c], ctx);
            if (n != _cLen[c]) throw new InvalidDataException($"{Path}: chunk {c} ({VitaPack2Format.CodecName(_cCodec[c])}, logical {_cLog[c]}+{_cLen[c]}) did not decode ({n}): run Verify files");
        }
        CheckChunk(c, dst, ctx);
    }

    /// <summary>Kit #10 io review (<see cref="V2HashCheck"/>): chunk c's logical bytes at p against the table's XXH32 of the decoded bytes. A
    /// damaged stored chunk can decode to the right length with wrong bytes (an LZ4 match offset that stays in bounds spreads one flipped bit
    /// over up to a tile): the background threads check every chunk they decode (spare cores), and a chunk that failed once is checked on
    /// every later decode, the reading thread's included -- so it ends in this exception (the game's load sees an error naming the chunk),
    /// never in wrong bytes. ctx: 0 the reading thread, 1 the worker, 2 the preload.</summary>
    void CheckChunk(int c, byte* p, int ctx)
    {
        int m = V2HashCheck;
        if (m <= 0 || (m == 1 && ctx == 0 && !_cSuspect[c])) return;
        if (VitaPack2Format.Xxh32(new ReadOnlySpan<byte>(p, _cLen[c])) == _cLHash[c]) { V2HashChecked++; return; }   // (stats: a benign race between threads)
        _cSuspect[c] = true; V2HashBad++;
        throw new InvalidDataException($"{Path}: chunk {c} ({VitaPack2Format.CodecName(_cCodec[c])}, logical {_cLog[c]}+{_cLen[c]}, card offset {_cAt[c]}) decoded to bytes that differ from its hash (a damaged card or copy): run Verify files");
    }

    // ------------------------------------------------------------------ the reading thread (holds _lock)

    /// <summary>The page holding chunk c, filled now if needed (the K8 store, else the card); the page stays valid until the next read.</summary>
    int ReaderPage(int c, bool report)
    {
        int v;
        lock (_pg)
        {
            if (_pin >= 0) _pgUsed[_pin] = ++_pgTick;   // (the held page was used until now: reads inside it took the lock-free path)
            for (;;)
            {
                int i = -1;
                for (int j = 0; j < _nPg; j++) if (_pgChunk[j] == c && !_pgAbandon[j]) { i = j; break; }
                if (i < 0) break;
                if (_pgState[i] == PgReady)
                {
                    _pgUsed[i] = ++_pgTick; _pin = i; V2PageHits++;
                    if (_pgAhead[i]) { _pgAhead[i] = false; _pgSrc[i] = 0; V2AheadUsed++; if (_rankOf[c] > _readerRank) _readerRank = _rankOf[c]; Monitor.PulseAll(_pg); if (report) ReportChunk(c); }
                    return i;
                }
                if (_pgState[i] != PgWorker) { _pgAbandon[i] = true; break; }   // (defensive: no other state can hold a chunk here)
                // the worker is filling it: wait (bounded), else read it ourselves (its copy is dropped when it finishes)
                V2AheadWaits++;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), lim = AheadWaitBoundMs > 0 ? t0 + AheadWaitBoundMs * System.Diagnostics.Stopwatch.Frequency / 1000 : long.MaxValue;
                bool timedOut = false;
                while (_pgChunk[i] == c && _pgState[i] == PgWorker)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp(); if (now >= lim) { timedOut = true; break; }
                    Monitor.Wait(_pg, lim == long.MaxValue ? 50 : (int)Math.Max(1, (lim - now) * 1000 / System.Diagnostics.Stopwatch.Frequency));
                }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; V2AheadWaitedMs += ms;
                if (timedOut) { _pgAbandon[i] = true; V2AheadTimeouts++; break; }
            }
            v = Victim(false);
            _pgChunk[v] = c; _pgState[v] = PgReader; _pgAhead[v] = false; _pgAbandon[v] = false; _pgSrc[v] = 0;
        }
        V2PageMisses++;
        try { FillFromCardOrStore(c, Page(v), report); }
        catch { lock (_pg) { _pgChunk[v] = -1; _pgState[v] = PgReady; Monitor.PulseAll(_pg); } throw; }
        lock (_pg) { _pgState[v] = PgReady; _pgUsed[v] = ++_pgTick; _pin = v; Monitor.PulseAll(_pg); }
        AskAheadAfter(_rankOf[c], NoteMiss(c, c));
        return v;
    }

    void ReportChunk(int c)
    {
#if !SHEPHERD_NO_K8
        OnRead?.OnRead(PayloadOffset + _cLog[c], _cLen[c]);
#endif
    }

    /// <summary>Chunk c into dst (its logical length): the K8 preload's store when it holds it, else the card + the decoder (reading thread).</summary>
    void FillFromCardOrStore(int c, byte* dst, bool report)
    {
#if !SHEPHERD_NO_K8
        if (report)
        {
            long abs = PayloadOffset + _cLog[c];
            OnRead?.OnRead(abs, _cLen[c]);
            var st = Store;
            if (st != null && st.Take(abs, new Span<byte>(dst, _cLen[c]))) { StoreHits++; StoreBytes += _cLen[c]; return; }
        }
#endif
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        IoLanes.FgEnter();
        try
        {
            if (_cCodec[c] == VitaPack2Format.CodecStored) ReadPhys(_cAt[c], new Span<byte>(dst, _cLen[c]));
            else ReadPhys(_cAt[c], new Span<byte>(_stMain, _cSLen[c]));
        }
        finally { IoLanes.FgExit(); }
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        V2CardReads++; V2StoredBytes += _cSLen[c]; Reads++; BytesRead += _cSLen[c];
        V2ReadMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; ReadMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (_cCodec[c] != VitaPack2Format.CodecStored)
        {
            DecodeChunk(c, _stMain, dst, 0);
            V2Decoded++; V2DecodedBytes += _cLen[c]; V2DecodeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        else CheckChunk(c, dst, 0);   // (review: a raw chunk read straight into the page -- checked as a decode would be)
    }

    /// <summary>A page to fill (under _pg). The reader: an empty page, else the least recently used ready page that is not decoded-ahead,
    /// else the oldest decoded-ahead one. The worker: an empty page, else the least recently used ready, used page outside the reader's two
    /// most recent; -1 when none (the worker waits).</summary>
    int Victim(bool worker)
    {
        int best = -1; long bu = long.MaxValue; int bestAhead = -1; long ba = long.MaxValue;
        long recent = _pgTick - 2;   // the reader's two most recently used pages stay (re-reads: an atlas header per sprite)
        for (int j = 0; j < _nPg; j++)
        {
            if (_pgState[j] != PgReady || j == _pin) continue;
            if (_pgChunk[j] < 0 || _pgAbandon[j]) return j;
            if (_pgAhead[j] && _pgTick - _pgUsed[j] < V2AheadAgeTicks) { if (_pgUsed[j] < ba) { ba = _pgUsed[j]; bestAhead = j; } continue; }
            if (worker && _pgUsed[j] > recent) continue;
            if (_pgUsed[j] < bu) { bu = _pgUsed[j]; best = j; }
        }
        if (best >= 0 || worker) return best;
        if (bestAhead >= 0) return bestAhead;
        throw new InvalidOperationException("pack: no free page (every page is being filled)");
    }

    ReadOnlySpan<byte> V2Bytes(long offset, int len)
    {
        if (offset < PayloadOffset || offset + len > PayloadOffset + PayloadSize) throw new ArgumentOutOfRangeException(nameof(offset));
        long rel = offset - PayloadOffset;
        // the common case: inside the chunk of the page the reader holds pinned (the worker never takes the pinned page and only this thread
        // re-pins, so no lock is needed to read it)
        int lc = _lastChunk, pin = _pin;
        if (lc >= 0 && pin >= 0 && rel >= _cLog[lc] && rel + len <= _cLog[lc] + _cLen[lc] && _pgChunk[pin] == lc && _pgState[pin] == PgReady)
        { V2FastHits++; return new ReadOnlySpan<byte>(Page(pin) + (rel - _cLog[lc]), len); }
        int c = FindChunk(rel); _req++;
        if (rel + len <= _cLog[c] + _cLen[c])
        {
            int p = ReaderPage(c, true);
            _lastChunk = c;
            return new ReadOnlySpan<byte>(Page(p) + (rel - _cLog[c]), len);
        }
        _lastChunk = -1;
        if (len > WindowSize / 2 || len > _tile)
        {   // a big record: a fresh array, as version 1 (its bytes decoded straight into it)
            var b = GC.AllocateUninitializedArray<byte>(len); V2ReadLogical(offset, b, report: true); return b;
        }
        // across chunks (len <= one tile): put together in the scratch page
        V2Straddles++;
        int done = 0;
        while (done < len)
        {
            int k = FindChunk(rel + done);
            int p = ReaderPage(k, true);
            int from = (int)(rel + done - _cLog[k]), n = Math.Min(len - done, _cLen[k] - from);
            Buffer.MemoryCopy(Page(p) + from, _scratch2 + done, n, n);
            done += n;
        }
        return new ReadOnlySpan<byte>(_scratch2, len);
    }

    /// <summary>Logical [offset, offset + dst.Length) into dst: chunks a page holds are copied; whole chunks inside dst are decoded straight into
    /// it, runs of them that sit together on the card in one read; a chunk only partly inside goes through a page. Reading thread.</summary>
    void V2ReadLogical(long offset, Span<byte> dst, bool report = false)
    {
        lock (_lock) V2ReadLogicalLocked(offset, dst, report);
    }
    void V2ReadLogicalLocked(long offset, Span<byte> dst, bool report)
    {
        if (report)
        {
#if !SHEPHERD_NO_K8
            OnRead?.OnRead(offset, dst.Length);
            var st = Store;
            if (st != null && st.Take(offset, dst)) { StoreHits++; StoreBytes += dst.Length; return; }
#endif
        }
        if (dst.Length == 0) return;
        if (offset < PayloadOffset || offset + dst.Length > PayloadOffset + PayloadSize) throw new EndOfStreamException($"{Path}: read {dst.Length} B at {offset} past the payload");
        _req++;
        long rel = offset - PayloadOffset; int lastRank = -1; bool lastRun = false;
        fixed (byte* d0 = dst)
        {
            int done = 0;
            while (done < dst.Length)
            {
                int k = FindChunk(rel + done);
                int from = (int)(rel + done - _cLog[k]), n = Math.Min(dst.Length - done, _cLen[k] - from);
                int resident = TakeResident(k);
                if (resident >= 0 || from != 0 || n != _cLen[k])
                {
                    int p = resident >= 0 ? resident : ReaderPage(k, false);
                    Buffer.MemoryCopy(Page(p) + from, d0 + done, n, n);
                    done += n; lastRank = _rankOf[k]; lastRun = false; continue;
                }
                // a run of whole chunks: logically consecutive, not resident, stored back to back (alignment padding only), <= RunMax stored
                int k2 = k; long runEnd = _cAt[k] + _cSLen[k]; int runLogical = _cLen[k];
                while (k2 + 1 < _nChunks && done + runLogical + _cLen[k2 + 1] <= dst.Length)
                {
                    int nk = k2 + 1; long gap = _cAt[nk] - runEnd;
                    if (gap < 0 || gap >= _align || _cAt[nk] + _cSLen[nk] - _cAt[k] > RunMax || ResidentPage(nk) >= 0) break;
                    runEnd = _cAt[nk] + _cSLen[nk]; runLogical += _cLen[nk]; k2 = nk;
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool direct = k2 == k && _cCodec[k] == VitaPack2Format.CodecStored;
                IoLanes.FgEnter();
                try
                {
                    if (direct) ReadPhys(_cAt[k], new Span<byte>(d0 + done, _cLen[k]));   // a raw chunk straight into the caller's buffer
                    else ReadPhys(_cAt[k], new Span<byte>(_stMain, (int)(runEnd - _cAt[k])));
                }
                finally { IoLanes.FgExit(); }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                V2CardReads++; V2StoredBytes += runEnd - _cAt[k]; Reads++; BytesRead += runEnd - _cAt[k];
                V2ReadMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; ReadMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (!direct)
                    for (int j = k; j <= k2; j++)
                    {
                        DecodeChunk(j, _stMain + (_cAt[j] - _cAt[k]), d0 + done, 0);
                        if (_cCodec[j] != VitaPack2Format.CodecStored) { V2Decoded++; V2DecodedBytes += _cLen[j]; }
                        done += _cLen[j];
                    }
                else { CheckChunk(k, d0 + done, 0); done += _cLen[k]; }
                V2DecodeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                V2Direct += k2 - k + 1; V2DirectBytes += runLogical; lastRank = _rankOf[k2]; lastRun = NoteMiss(k, k2);
            }
        }
        if (lastRank >= 0) AskAheadAfter(lastRank, lastRun);
    }

    int ResidentPage(int c)
    {
        lock (_pg) for (int j = 0; j < _nPg; j++) if (_pgChunk[j] == c && _pgState[j] == PgReady && !_pgAbandon[j]) return j;
        return -1;
    }
    /// <summary>A ready page holding chunk c, pinned (the worker never takes it while it is copied from) and marked used; -1 when none.</summary>
    int TakeResident(int c)
    {
        lock (_pg)
        {
            if (_pin >= 0) _pgUsed[_pin] = ++_pgTick;
            for (int j = 0; j < _nPg; j++)
                if (_pgChunk[j] == c && _pgState[j] == PgReady && !_pgAbandon[j])
                {
                    _pin = j; _pgUsed[j] = ++_pgTick; V2PageHits++;
                    if (_pgAhead[j]) { _pgAhead[j] = false; _pgSrc[j] = 0; V2AheadUsed++; if (_rankOf[c] > _readerRank) _readerRank = _rankOf[c]; Monitor.PulseAll(_pg); }
                    return j;
                }
        }
        return -1;
    }

    /// <summary>Test / tool: logical bytes through the reader as the engine reads them (the reader's lock held).</summary>
    public void ReadLogicalForTest(long offset, Span<byte> dst)
    {
        lock (_lock)
        {
            if (!_v2) { int done = 0; while (done < dst.Length) { int n = Math.Min(WindowSize / 2, dst.Length - done); Bytes(offset + done, n).CopyTo(dst[done..]); done += n; } return; }
            if (dst.Length <= _tile / 2) V2Bytes(offset, dst.Length).CopyTo(dst); else V2ReadLogical(offset, dst);
        }
    }

    // ------------------------------------------------------------------ the decode-ahead worker

    /// <summary>Kit #10 io (dependency-parallel loads): the chunks holding these files' records, queued for the worker as one batch (stored
    /// order) -- called when a bundle opens, before its objects are walked. No-op for a version-1 pack or with the worker off.</summary>
    public void Prefetch(IReadOnlyList<SerializedFile> files)
    {
        if (!_v2 || !V2DecodeAhead || !V2Prefetch || files == null) return;
        int added = 0;
        lock (_pg)
        {
            for (int fi = 0; fi < files.Count; fi++)
            {
                var f = files[fi]; if (f == null || f.Pack != this) continue;
                var objs = f.Objects; if (objs.Count == 0) continue;
                long lo = long.MaxValue, hi = 0;
                for (int i = 0; i < objs.Count; i++) { var o = objs[i]; if (o.ByteStart < lo) lo = o.ByteStart; if (o.ByteStart + o.ByteSize > hi) hi = o.ByteStart + o.ByteSize; }
                if (hi <= lo) continue;
                // a file's records sit together in the records region (one bake, first-touch order): its span of chunks
                for (int c = FindChunk(lo - PayloadOffset); c < _nChunks && _cLog[c] < hi - PayloadOffset; c++)
                {
                    if ((_cFlags[c] & VitaPack2Format.ChunkImage) != 0) break;
                    if (_batchN >= _batch.Length) break;
                    _batch[(_batchHead + _batchN) % _batch.Length] = c; _batchN++; added++;
                }
            }
            if (added > 0) { V2PrefetchCalls++; V2Prefetched += added; EnsureWorker(); Monitor.PulseAll(_pg); }
        }
    }

    /// <summary>Kit #10 io review: the reading thread read chunks [c0, c1] (stored back to back) from the card; true when that read is the
    /// <see cref="V2AheadRunNeed"/>-th in a row to continue the previous card read made by an EARLIER request (each starting at most two ranks after
    /// the previous one ended) -- the reader is walking a run there. A chunk
    /// that is a dedup copy (its rank is its original's, wherever that is stored) says nothing about where the reader is and is left out, and
    /// card reads inside one request (a big image read again) never make a run on their own. Reading thread only (it holds _lock).</summary>
    bool NoteMiss(int c0, int c1)
    {
        if ((_cFlags[c0] & VitaPack2Format.ChunkCopy) != 0) return false;
        int first = _rankOf[c0];
        bool cont = _lastMissReq >= 0 && _lastMissReq != _req && first >= _lastMissEnd && first <= _lastMissEnd + 2;
        _runLen = cont ? _runLen + 1 : 0;
        _lastMissEnd = _rankOf[c1] + 1; _lastMissReq = _req;
        return _runLen >= V2AheadRunNeed;
    }

    /// <summary>After the reader needed the chunk at stored rank r: the worker goes on from rank r + 1 (the visit order's next chunks).
    /// <paramref name="run"/>: the read was a card read continuing the reader's previous one (<see cref="NoteMiss"/>).</summary>
    void AskAheadAfter(int r, bool run)
    {
        if (!V2DecodeAhead || r + 1 >= _nRanks) return;
        lock (_pg)
        {
            // a re-read of an older chunk (an atlas header again, a record evicted and read back) does not move the worker back -- review: unless
            // the reader is walking a new run there (V2AheadRunNeed: e.g. back in the visit-order region after one read of a chunk stored at the
            // tail); the old position's predictions keep their pages and age out as before (the reader may come back to them)
            if (r < _readerRank - 2)
            {
                if (!V2AheadRuns || !run) return;
                _readerRank = r; V2AheadReanchors++;
            }
            if (r > _readerRank) _readerRank = r;
            if (_waRank == r + 1) return;
            // already ahead of the reader in this run: keep going
            if (_waRank > r + 1 && _waRank - (r + 1) <= V2AheadPages + 8) return;
            _waRank = r + 1; _waGen++;
            EnsureWorker();
            Monitor.PulseAll(_pg);
        }
    }

    void EnsureWorker()
    {
        if (_waThread != null) return;
        // (review: 128 KiB of stack, as the K8 preload's thread that runs the same read + decode path; vlx's stack watch: deepest use 5-6 KiB)
        _waThread = new Thread(WorkerLoop, 128 * 1024) { IsBackground = true, Name = "shepherd-decode" };
        _waThread.Start();
        while (!_waReady) Monitor.Wait(_pg);   // (under _pg: its start-up runs while this thread waits -- a fixed point of the allocation order)
    }

    int AheadCount(byte src)
    {
        int n = 0;
        for (int j = 0; j < _nPg; j++)
            if (_pgSrc[j] == src && (_pgState[j] == PgWorker || (_pgAhead[j] && _pgState[j] == PgReady && _pgTick - _pgUsed[j] < V2AheadAgeTicks))) n++;
        return n;
    }
    bool Resident(int c) { for (int j = 0; j < _nPg; j++) if (_pgChunk[j] == c && !_pgAbandon[j]) return true; return false; }

    void WorkerLoop()
    {
        try { OnDecodeAheadThread?.Invoke("shepherd-decode"); } catch { }
        lock (_pg) { _waReady = true; Monitor.PulseAll(_pg); }
        var run = _wRun; var pages = _wPages;
        for (;;)
        {
            int n = 0;
            lock (_pg)
            {
                for (;;)
                {
                    if (_waStop) return;
                    n = 0;
                    int batchBudget = Math.Min(3, V2AheadPages / 3), seqBudget = V2AheadPages - batchBudget;
                    {
                        // next work: the sequential run in stored order (the visit order: what the recorded run read next), up to its budget and
                        // no further than its budget past the reader; else the batch (a bundle's records: a load's dependencies), a small budget
                        int first = -1; bool fromSeq = false;
                        if (AheadCount(SrcSeq) < seqBudget && _waRank >= 0 && _waRank <= _readerRank + seqBudget + 1)
                            while (_waRank < _nRanks && _waRank <= _readerRank + seqBudget + 1) { int c = _byRank[_waRank]; if (!Resident(c)) { first = c; fromSeq = true; break; } _waRank++; }
                        if (first < 0 && AheadCount(SrcBatch) < batchBudget)
                            while (_batchN > 0) { int c = _batch[_batchHead]; _batchHead = (_batchHead + 1) % _batch.Length; _batchN--; if (!Resident(c)) { first = c; break; } }
                        byte src = fromSeq ? SrcSeq : SrcBatch; int budget = fromSeq ? seqBudget : batchBudget;
                        if (first >= 0)
                        {
                            int v = Victim(true);
                            if (v >= 0)
                            {
                                run[0] = first; pages[0] = v; n = 1;
                                _pgChunk[v] = first; _pgState[v] = PgWorker; _pgAhead[v] = false; _pgAbandon[v] = false; _pgSrc[v] = src;
                                // more chunks that follow on the card in the same request
                                int r = _rankOf[first] + 1; long end = _cAt[first] + _cSLen[first];
                                while (n < run.Length && r < _nRanks && AheadCount(src) < budget && (!fromSeq || r <= _readerRank + seqBudget + 1))
                                {
                                    int c = _byRank[r]; long gap = _cAt[c] - end;
                                    if (Resident(c) || gap < 0 || gap >= _align || _cAt[c] + _cSLen[c] - _cAt[first] > RunMax) break;
                                    int v2 = Victim(true); if (v2 < 0) break;
                                    run[n] = c; pages[n] = v2; n++; end = _cAt[c] + _cSLen[c];
                                    _pgChunk[v2] = c; _pgState[v2] = PgWorker; _pgAhead[v2] = false; _pgAbandon[v2] = false; _pgSrc[v2] = src;
                                    r++;
                                }
                                if (fromSeq) _waRank = _rankOf[run[n - 1]] + 1;
                                break;
                            }
                        }
                    }
                    Monitor.Wait(_pg, 100);
                }
            }
            // the card (a background read), outside the lock; then each chunk decoded and handed over on its own (a reader waiting for the run's
            // first chunk does not wait for the whole run)
            int f = run[0]; long from = _cAt[f], to = _cAt[run[n - 1]] + _cSLen[run[n - 1]];
            bool ok = true; long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), t1 = t0;
            try
            {
                IoLanes.BgGate();
                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ReadPhys(from, new Span<byte>(_stWork, (int)(to - from)));
            }
            catch (Exception) { ok = false; }
            t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_pg) { V2AheadMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; V2WorkerReads++; V2WorkerBytes += to - from; }
            for (int i = 0; i < n; i++)
            {
                int p = pages[i]; bool good = ok;
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (good) try { DecodeChunk(run[i], _stWork + (_cAt[run[i]] - from), Page(p), 1); } catch (Exception) { good = false; }
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_pg)
                {
                    V2DecodeAheadMs += (t3 - t2) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (!good || _pgAbandon[p]) { _pgChunk[p] = -1; _pgAbandon[p] = false; _pgState[p] = PgReady; _pgAhead[p] = false; _pgSrc[p] = 0; }
                    else { _pgState[p] = PgReady; _pgAhead[p] = true; _pgUsed[p] = ++_pgTick; V2AheadDecoded++; if (_pgSrc[p] == SrcBatch) V2AheadBatch++; }
                    Monitor.PulseAll(_pg);
                }
            }
        }
    }

    // ------------------------------------------------------------------ the K8 preload's reads (its own thread: decoder context 2)

    bool V2PreloadRead(long offset, Span<byte> dst)
    {
        try
        {
            if (offset < PayloadOffset || offset + dst.Length > PayloadOffset + PayloadSize) return false;
            lock (_pg) { if (_stPre == null) { _stPre = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(RunMax); _tilePre = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(_tile); } }
            long rel = offset - PayloadOffset; int done = 0;
            fixed (byte* d0 = dst)
                while (done < dst.Length)
                {
                    int k = FindChunk(rel + done);
                    int from = (int)(rel + done - _cLog[k]), n = Math.Min(dst.Length - done, _cLen[k] - from);
                    IoLanes.BgGate();
                    ReadPhys(_cAt[k], new Span<byte>(_stPre, _cSLen[k]));
                    DecodeChunk(k, _stPre, _tilePre, 2);   // (decoded in RAM: the decoder reads back what it wrote -- never from video memory)
                    var cp = Copier;
                    if (cp != null && n >= (64 << 10)) cp.Copy(d0 + done, _tilePre + from, n, true); else Buffer.MemoryCopy(_tilePre + from, d0 + done, n, n);
                    done += n; V2PreloadReads++;
                }
            return true;
        }
        catch (Exception) { return false; }
    }

    long V2ReleasePreloadStaging()
    {   // (kit #10 integration review: the K8 preload's store given back at the memory ladder's step 1 takes these with it -- native memory)
        lock (_pg)
        {
            if (_stPre == null) return 0;
            FreeN(ref _stPre); FreeN(ref _tilePre);
            return RunMax + (long)_tile;
        }
    }

    string V2Stats()
    {
        if (!_v2) return "";
        lock (_pg)
            return $"; chunked (version 2: {_nChunks} chunks, tile {_tile >> 10} KiB, {StoredFileBytes / 1048576.0:F1} MiB on the card for {PayloadSize / 1048576.0:F1} MiB of payload; decoders {ChunkDecoder?.Describe()}): " +
                $"reading thread: {V2CardReads} card reads {V2StoredBytes / 1048576.0:F1} MiB ({V2ReadMs:F0} ms), {V2Decoded} chunks {V2DecodedBytes / 1048576.0:F1} MiB decoded there ({V2DecodeMs:F0} ms); worker: {V2WorkerReads} card reads {V2WorkerBytes / 1048576.0:F1} MiB; " +
 $"pages {_nPg} x {_tile >> 10} KiB: {V2FastHits} reads from the held page, {V2PageHits} page hits / {V2PageMisses} fills, {V2Straddles} records across chunks, {V2Direct} chunks ({V2DirectBytes / 1048576.0:F1} MiB) decoded straight into big reads; " +
                $"decode-ahead {(V2DecodeAhead ? "on" : "off")}: {V2AheadDecoded} chunks decoded ahead ({V2AheadBatch} of them from the prefetch batch) ({V2AheadMs:F0} ms card + {V2DecodeAheadMs:F0} ms decode on the worker), {V2AheadUsed} used, " +
                $"{V2AheadWaits} waited for ({V2AheadWaitedMs:F1} ms, {V2AheadTimeouts} past the bound), {V2AheadReanchors} new runs behind followed; prefetch {V2PrefetchCalls} bundles / {V2Prefetched} chunks queued; " +
                $"decoded-hash checks (mode {V2HashCheck}) {V2HashChecked} ok, {V2HashBad} BAD; " +
                $"lanes {(IoLanes.On ? "on" : "off")}: {IoLanes.BgWaits} background reads held back {IoLanes.BgWaitedMs:F0} ms; preload {V2PreloadReads} chunk reads";
    }
}

/// <summary>Progress of <see cref="PackVerify"/> (an interface, not a delegate: eboot span). Return false to stop.</summary>
public interface IVerifyProgress { bool Step(long done, long total); }

/// <summary>
/// Kit #10 io: the VERIFY FILES core (the PS Vita Settings row is the cache track's; it calls Shepherd.Engine Runtime/K10Io.VerifyFiles,
/// which calls this). A version-2 pack: the header's and the chunk table's checksums, the stored TOC decoded and checked against the TOC
/// checksum, then every stored chunk read in card order in big sequential reads and its XXH32 compared with the table's (deep: also decoded
/// and the decoded bytes' XXH32 compared -- the decoder's own check). A version-1 pack: its TOC checksum, and the payload against a
/// <c>shepherd.pack.xxh</c> beside it when one exists (tools/VitaPack --repack writes it: XXH32 per 1 MiB block). The read rate of every
/// 8 MiB stretch is kept: a failing card or adapter, or a badly fragmented file, shows as a slow stretch. Reads only; nothing is written.
/// </summary>
public static unsafe class PackVerify
{
    public sealed class Result
    {
        public string Path, Kind; public bool Ok, HeaderOk = true, TocOk, TableOk = true, Stopped; public int Chunks, Bad, BadDecode, Blocks;
        public long StoredBytes, LogicalBytes; public double Ms, SlowestMiBs, MedianMiBs; public string FirstBad, Error;
        public string Summary() =>
            (Error != null ? "ERROR " + Error + "; " : "") + $"{Kind}: {(Ok ? "OK" : Stopped ? "STOPPED" : "FAILED")} -- header {(HeaderOk ? "ok" : "BAD")}, chunk table {(TableOk ? "ok" : "BAD")}, TOC {(TocOk ? "ok" : "BAD")}, " +
            (Chunks > 0 ? $"{Chunks - Bad} of {Chunks} chunks ok{(BadDecode > 0 ? $", {BadDecode} decode BAD" : "")}" : Blocks > 0 ? $"{Blocks - Bad} of {Blocks} 1 MiB blocks ok" : "no payload hashes (version 1 without shepherd.pack.xxh)") +
            (FirstBad != null ? $", first bad: {FirstBad}" : "") + $"; {StoredBytes / 1048576.0:F1} MiB read in {Ms / 1000:F1} s ({(Ms > 0 ? StoredBytes / 1048576.0 / (Ms / 1000) : 0):F1} MiB/s; " +
            $"8 MiB stretches: median {MedianMiBs:F1}, slowest {SlowestMiBs:F1} MiB/s)";
    }

    const int Big = 1 << 20;
    /// <summary>Kit #10 io review: with the I/O lanes on, Verify files reads the card in pieces of this size, each after the foreground lane
    /// cleared (IoLanes.BgGate) -- a Verify run from the Settings row or at a first boot is a BACKGROUND reader: a read the player waits on
    /// queues behind at most one of its pieces (the first build read 1 MiB at a time, ungated). Lanes off = 1 MiB pieces, ungated.</summary>
    const int Piece = 128 << 10;

    public static Result Run(string path, IPackDecoder dec, bool deep, IVerifyProgress progress)
    {
        var r = new Result { Path = path };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // (the read rate of every 8 MiB stretch; the time a piece waited behind the player's reads is not the card's and is left out)
        var rates = new double[4096]; int nr = 0; long winBytes = 0, winWait = 0; long winT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        void Rate(long bytes)
        {
            winBytes += bytes;
            if (winBytes < (8L << 20)) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp(); double s = (now - winT0 - winWait) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (nr < rates.Length && s > 0) rates[nr++] = winBytes / 1048576.0 / s;
            winBytes = 0; winWait = 0; winT0 = now;
        }
        // review: every big buffer in native memory (newlib), given back at the end -- the first build put the decoded TOC (2.85 MiB), the
        // stored TOC (1.2 MiB) and the chunk table in the GC heap (large objects, reachable mid-session from the Settings row: the arena is the
        // device's wall)
        byte* buf = null, tile = null, tbl = null, tst = null, tdec = null;
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            long len = RandomAccess.GetLength(h);
            var hdr = new byte[VitaPack2Format.HeaderSize];
            if (RandomAccess.Read(h, hdr, 0) < 64 || BinaryPrimitives.ReadUInt32LittleEndian(hdr) != VitaPackFormat.Magic) { r.HeaderOk = false; r.Kind = "not a Shepherd pack"; return r; }
            int ver = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(4));
            uint tocHash = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12)); long tocSize = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(24));
            buf = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(Big);
            if (ver == VitaPack2Format.Version)
            {
                r.Kind = "chunked pack (version 2)";
                if (VitaPackFormat.Fnv1a(hdr.AsSpan(0, 120)) != BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(120))) { r.HeaderOk = false; return r; }
                int n = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(64)); long tAt = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(72)); int tBytes = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(80));
                long tocAt = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(88)); int tocStored = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(96)); byte tocCodec = hdr[100];
                int tileSize = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(108));
                if (n <= 0 || tBytes != n * VitaPack2Format.EntrySize || tAt < 0 || tAt + tBytes > len || tileSize <= 0 || tileSize > Big) { r.TableOk = false; return r; }
                tbl = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(tBytes); ReadBg(h, tAt, new Span<byte>(tbl, tBytes), ref winWait);
                if (VitaPackFormat.Fnv1a(new ReadOnlySpan<byte>(tbl, tBytes)) != BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(84))) { r.TableOk = false; return r; }
                // the TOC (both buffers given back before the chunk pass)
                if (tocAt < 0 || tocStored <= 0 || tocAt + tocStored > len || tocSize <= 0 || tocSize > int.MaxValue) r.FirstBad ??= "the TOC lies past the file's end (truncated copy?)";
                else
                {
                    tst = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(tocStored); ReadBg(h, tocAt, new Span<byte>(tst, tocStored), ref winWait); r.StoredBytes += tocStored;
                    tdec = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal((IntPtr)tocSize);
                    int got = tocCodec == VitaPack2Format.CodecStored ? (tocStored == tocSize ? CopyAll(tst, tdec, tocStored) : -1) : dec != null ? dec.Decode(tocCodec, tst, tocStored, tdec, (int)tocSize, 3) : -1;
                    r.TocOk = got == tocSize && VitaPackFormat.Fnv1a(new ReadOnlySpan<byte>(tdec, (int)tocSize)) == tocHash;
                    FreeN(ref tst); FreeN(ref tdec);
                }
                // the chunks, in card order (distinct stored offsets), in big reads (entries read from the native table as they are needed)
                var order = new long[n]; var idx = new int[n];
                for (int i = 0; i < n; i++) { order[i] = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(tbl + (long)i * VitaPack2Format.EntrySize + 16, 8)); idx[i] = i; }
                VitaPack2Format.SortBy(order, idx, n);
                VitaPack2Format.Chunk E(int i) => VitaPack2Format.Chunk.Read(new ReadOnlySpan<byte>(tbl + (long)i * VitaPack2Format.EntrySize, VitaPack2Format.EntrySize));
                if (deep) tile = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal(tileSize);
                long total = 0; for (int j = 0; j < n; j++) if (j == 0 || order[j] != order[j - 1]) total += E(idx[j]).StoredLen;
                long done = 0; int jj = 0;
                while (jj < n)
                {
                    // a run of chunks inside one big read
                    long from = order[jj]; int k = jj; long to = from;
                    while (k < n && order[k] + E(idx[k]).StoredLen - from <= Big) { to = Math.Max(to, order[k] + E(idx[k]).StoredLen); k++; }
                    if (k == jj) { r.Bad++; r.FirstBad ??= $"chunk {idx[jj]} stored length {E(idx[jj]).StoredLen}"; jj++; continue; }
                    if (from < 0 || to > len) { r.Bad += k - jj; r.FirstBad ??= $"chunk {idx[jj]} past the file's end (truncated copy?)"; break; }
                    ReadBg(h, from, new Span<byte>(buf, (int)(to - from)), ref winWait); Rate(to - from);
                    for (int j = jj; j < k; j++)
                    {
                        if (j > jj && order[j] == order[j - 1]) continue;   // (a copy: its original's bytes, checked once)
                        var c = E(idx[j]);
                        r.Chunks++; r.StoredBytes += c.StoredLen; r.LogicalBytes += c.LogicalLen; done += c.StoredLen;
                        var sb = new ReadOnlySpan<byte>(buf + (c.Stored - from), c.StoredLen);
                        if (VitaPack2Format.Xxh32(sb) != c.StoredHash) { r.Bad++; r.FirstBad ??= $"chunk {idx[j]} (logical {c.Logical}+{c.LogicalLen}, card offset {c.Stored}): stored bytes differ"; continue; }
                        if (deep)
                        {
                            int m = c.LogicalLen > tileSize ? -1 : c.Codec == VitaPack2Format.CodecStored ? (c.StoredLen == c.LogicalLen ? CopyAll(buf + (c.Stored - from), tile, c.LogicalLen) : -1) : dec != null ? dec.Decode(c.Codec, buf + (c.Stored - from), c.StoredLen, tile, c.LogicalLen, 3) : -1;
                            if (m != c.LogicalLen || VitaPack2Format.Xxh32(new ReadOnlySpan<byte>(tile, c.LogicalLen)) != c.LogicalHash) { r.BadDecode++; r.FirstBad ??= $"chunk {idx[j]} (logical {c.Logical}+{c.LogicalLen}): decoded bytes differ"; }
                        }
                    }
                    jj = k;
                    if (progress != null && !progress.Step(done, total)) { r.Stopped = true; break; }
                }
                r.Ok = r.HeaderOk && r.TableOk && r.TocOk && r.Bad == 0 && r.BadDecode == 0 && !r.Stopped;
            }
            else if (ver == VitaPackFormat.Version)
            {
                r.Kind = "pack (version 1)";
                long tocAt = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(16));
                if (tocAt < 0 || tocSize <= 0 || tocSize > int.MaxValue || tocAt + tocSize > len) { r.TocOk = false; r.FirstBad = "the TOC is past the file's end (truncated copy?)"; return r; }
                tdec = (byte*)System.Runtime.InteropServices.Marshal.AllocHGlobal((IntPtr)tocSize); ReadBg(h, tocAt, new Span<byte>(tdec, (int)tocSize), ref winWait); r.StoredBytes += tocSize;
                r.TocOk = VitaPackFormat.Fnv1a(new ReadOnlySpan<byte>(tdec, (int)tocSize)) == tocHash;
                FreeN(ref tdec);
                var side = path + ".xxh";
                if (File.Exists(side))
                {
                    var sx = File.ReadAllBytes(side);
                    if (sx.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(sx) != 0x48584B53u || BinaryPrimitives.ReadInt64LittleEndian(sx.AsSpan(4)) != len) { r.Error = "shepherd.pack.xxh belongs to another file"; }
                    else
                    {
                        int nb = (int)((len + Big - 1) / Big);
                        if (sx.Length != 16 + nb * 4) r.Error = "shepherd.pack.xxh is damaged";
                        else
                            for (int b = 0; b < nb; b++)
                            {
                                long off = (long)b * Big; int m = (int)Math.Min(Big, len - off);
                                ReadBg(h, off, new Span<byte>(buf, m), ref winWait); Rate(m); r.StoredBytes += m; r.Blocks++;
                                if (VitaPack2Format.Xxh32(new ReadOnlySpan<byte>(buf, m)) != BinaryPrimitives.ReadUInt32LittleEndian(sx.AsSpan(16 + b * 4))) { r.Bad++; r.FirstBad ??= $"1 MiB block {b} (file offset {off})"; }
                                if (progress != null && !progress.Step(off + m, len)) { r.Stopped = true; break; }
                            }
                    }
                }
                r.Ok = r.TocOk && r.Bad == 0 && r.Error == null && !r.Stopped;
            }
            else { r.HeaderOk = false; r.Kind = "pack version " + ver.ToString(); }
        }
        catch (Exception e) { r.Error = e.GetBaseException().Message; r.Ok = false; }
        finally
        {
            FreeN(ref buf); FreeN(ref tile); FreeN(ref tbl); FreeN(ref tst); FreeN(ref tdec);
            r.Ms = sw.Elapsed.TotalMilliseconds;
            if (nr > 0)
            {   // (insertion sort: a few dozen stretches)
                for (int i = 1; i < nr; i++) { double x = rates[i]; int j = i - 1; while (j >= 0 && rates[j] > x) { rates[j + 1] = rates[j]; j--; } rates[j + 1] = x; }
                r.MedianMiBs = rates[nr / 2]; r.SlowestMiBs = rates[0];
            }
        }
        return r;
    }

    static void FreeN(ref byte* p) { if (p != null) { System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)p); p = null; } }
    /// <summary>[off, off + dst.Length) of the file, as background reads (see <see cref="Piece"/>); the time spent waiting behind foreground
    /// reads is added to <paramref name="waitTicks"/>.</summary>
    static void ReadBg(Microsoft.Win32.SafeHandles.SafeFileHandle h, long off, Span<byte> dst, ref long waitTicks)
    {
        bool lanes = IoLanes.On; int piece = lanes ? Piece : Big; int done = 0;
        while (done < dst.Length)
        {
            if (lanes) { long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); IoLanes.BgGate(); waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
            int k = RandomAccess.Read(h, dst.Slice(done, Math.Min(piece, dst.Length - done)), off + done);
            if (k <= 0) throw new EndOfStreamException($"read past the end at {off + done}");
            done += k;
        }
    }

    /// <summary>The version-1 sidecar (tools/VitaPack): "SKXH" | u64 file length | u32 block size | u32 XXH32 per 1 MiB block.</summary>
    public static byte[] Sidecar(string v1Path)
    {
        using var h = File.OpenHandle(v1Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        long len = RandomAccess.GetLength(h); int nb = (int)((len + Big - 1) / Big);
        var o = new byte[16 + nb * 4]; BinaryPrimitives.WriteUInt32LittleEndian(o, 0x48584B53u); BinaryPrimitives.WriteInt64LittleEndian(o.AsSpan(4), len); BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(12), Big);
        var b = new byte[Big];
        for (int i = 0; i < nb; i++) { long off = (long)i * Big; int m = (int)Math.Min(Big, len - off); ReadAll(h, off, b.AsSpan(0, m)); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(16 + i * 4), VitaPack2Format.Xxh32(b.AsSpan(0, m))); }
        return o;
    }

    static int CopyAll(byte* s, byte* d, int n) { Buffer.MemoryCopy(s, d, n, n); return n; }
    static void ReadAll(Microsoft.Win32.SafeHandles.SafeFileHandle h, long off, Span<byte> dst)
    {
        int done = 0;
        while (done < dst.Length) { int k = RandomAccess.Read(h, dst[done..], off + done); if (k <= 0) throw new EndOfStreamException($"read past the end at {off + done}"); done += k; }
    }
}
