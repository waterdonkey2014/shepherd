using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Shepherd.Assets;

/// <summary>
/// The Vita pack (V3 part 2, vita/notes/v3b-pack.md): one flat, versioned, little-endian, seekable file that replaces the
/// bundles / loose serialized files for a known set of objects. Objects keep their identity (file name + PathId, the
/// original externals table, so every PPtr resolves exactly as before); their payloads are stored already in the engine's
/// shape: a value tree in a schema-coded form (no Unity TypeTree, no alignment rules, no per-file metadata), raw bytes only
/// where the engine reads raw bytes, and texture images pre-transcoded for the GPU.
///
/// Layout (all little-endian):
///   header (64 B): magic "SHPK", u16 version, u16 header size, u32 flags, u32 TOC FNV-1a, u64 TOC offset/size,
///                  u64 payload offset/size, u32 objects, u32 files, u32 bundles, u32 strings
///   TOC: strings (u32 n, u32 offsets[n+1], UTF-8 blob) | shapes (u32 n, {u32 type, u16 nf, nf x {u32 key, u8 kind}})
///        | bundles (u32 n, {u32 name, u16 nfiles, u32 file[]}) | files (u32 n, {u32 name, i32 bundle, u16 flags, u16 next,
///        u32 ext[], u32 firstObject, u32 nObjects, u16 nClasses, {i32 classId, u32 objects of that class in the SOURCE file}}) | absent files (u32 n, u32 name[]) | objects (n x 32 B: i64 pathId,
///        u64 offset, u32 size, i32 classId, u16 file, u16 flags, u32 0) | absent objects (u32 n, {u32 file, i64 pathId})
///        | [header flag bit1] properties (u32 n, {u32 key, u32 value}: string ids; the bake parameters, e.g. language=en)
///   payload: object records (first-touch order), then texture images. A record: u32 treeLen, u32 rawLen, u32 imageDescLen
///            (0 or 28), [i32 fileId + i64 pathId: m_Script, flag Script], tree, raw, image descriptor (i32 unityFormat, i32 w,
///            i32 h, u32 flags (bit0 = rows top-down), i32 len, i64 payload offset of the image bytes)
/// Value codec: see <see cref="K"/>. A struct is (varint shape id, fields in shape order); a shape field whose kind is
/// not <see cref="K.Any"/> is written without a tag. Strings: varint v, odd = shared string id v>>1, even = inline UTF-8 of
/// length v>>1.
/// </summary>
public static class VitaPackFormat
{
    public const uint Magic = 0x4B504853;   // "SHPK"
    public const ushort Version = 1;
    public const int HeaderSize = 64;
    public const uint FlagTopDownImages = 1;
    /// <summary>The TOC ends with a properties section (bake parameters). Readers without it stop before it; the version stays 1.</summary>
    public const uint FlagProperties = 2;
    public const string PackFileName = "shepherd.pack";

    // object flags
    public const ushort ObjTree = 1, ObjRaw = 2, ObjScript = 4, ObjImage = 8;
    // file flags
    public const ushort FileLoose = 1;
    // image flags
    public const uint ImageTopDown = 1;

    /// <summary>Value kinds.</summary>
    public enum K : byte
    {
        Null, Bool, I8, U8, Char, I16, U16, I32, U32, I64, U64, F32, F64, Str, PPtr, Bytes, Struct, List,
        Any = 0xFF,
    }

    public static int FixedSize(K k) => k switch
    {
        K.Bool or K.I8 or K.U8 or K.Char => 1, K.I16 or K.U16 => 2, K.I32 or K.U32 or K.F32 => 4, K.I64 or K.U64 or K.F64 => 8,
        K.PPtr => 12, K.Null => 0, _ => -1,
    };

    public static K KindOf(object v) => v switch
    {
        null => K.Null, bool => K.Bool, sbyte => K.I8, byte => K.U8, char => K.Char, short => K.I16, ushort => K.U16, int => K.I32,
        uint => K.U32, long => K.I64, ulong => K.U64, float => K.F32, double => K.F64, string => K.Str, PPtr => K.PPtr, byte[] => K.Bytes,
        TypeTreeObject => K.Struct, List<object> => K.List,
        _ => throw new NotSupportedException($"pack: value type {v.GetType().Name} cannot be stored"),
    };

    public static uint Fnv1a(ReadOnlySpan<byte> d)
    {
        uint h = 2166136261;
        foreach (var b in d) { h ^= b; h *= 16777619; }
        return h;
    }
}

/// <summary>A texture image stored in the pack: the original mip 0 in its Unity format and row order (exact textures:
/// SDF font atlases, Stylizer LUTs / patterns, CPU-read textures), or GPU-ready DXT1/DXT5 blocks, already flipped
/// top-down and usually at reduced size (<see cref="TopDown"/>). Read on demand; nothing is kept.</summary>
public sealed class PackImage
{
    public PackReader Reader { get; }
    public long Offset { get; }
    public int Length { get; }
    public int Format { get; }
    public int Width { get; }
    public int Height { get; }
    public bool TopDown { get; }
    internal PackImage(PackReader r, long offset, int length, int format, int w, int h, bool topDown) { Reader = r; Offset = offset; Length = length; Format = format; Width = w; Height = h; TopDown = topDown; }
    public byte[] Read() => Reader.ReadImage(this);
    /// <summary>Kit #6 (c): bytes [offset, offset + dst.Length) of the image into the caller's buffer (no array is allocated).</summary>
    public void ReadRange(int offset, Span<byte> dst) => Reader.ReadImageRange(this, offset, dst);
    public override string ToString() => $"{(TextureFormat)Format} {Width}x{Height}{(TopDown ? " top-down" : "")} ({Length} B)";
}

public sealed class PackReader : IDisposable
{
    /// <summary>Key under which a decoded Texture2D tree carries its <see cref="PackImage"/> (never a Unity field name).</summary>
    public const string ImageKey = "__packImage";

    public string Path { get; }
    public uint Flags { get; }
    public long PayloadOffset { get; }
    public long PayloadSize { get; }
    public int ObjectCount => _objCount;
    public int FileCount => _files.Length;
    public long TocBytes { get; }
    public double OpenMs { get; }
    /// <summary>Bake parameters (tools/VitaPack): e.g. "language" = the single I2 language kept, "joystick-maps" = excluded.</summary>
    public IReadOnlyDictionary<string, string> Properties => _props;
    readonly Dictionary<string, string> _props = new(StringComparer.Ordinal);

    // ---- stats ----
    public long Reads, BytesRead, WindowHits, TreeDecodes, RawReads, ImageReads, ImageBytes;
    public double ReadMs, DecodeMs;

    readonly SafeFileHandle _h;
    readonly object _lock = new();
#if !SHEPHERD_NO_K8
    readonly uint _tocHash;

    // ---- device kit #8, track boot (Shepherd.Engine Runtime/K8Preload.cs): the idle-window preload's two hooks ----
    /// <summary>A store of pack bytes read ahead of time (the engine's preload, in video memory): <see cref="IPackStore.Take"/> fills
    /// <c>dst</c> with the pack's bytes at <c>offset</c> and answers true, or answers false (the card is read as before). The bytes are the
    /// pack's either way; only when they came off the card changes. Null = none (kit #7).</summary>
    public interface IPackStore { bool Take(long offset, Span<byte> dst); }
    public static IPackStore Store;
    /// <summary>Every read of the pack the readers make on demand (offset, length), in call order -- the read-order map the preload learns
    /// from (the read-ahead worker's speculative reads are not reported). Null = none (kit #7). Called from the reading thread. (An
    /// interface, not a delegate: no generic delegate instantiation in the AOT images -- eboot span.)</summary>
    public interface IPackReadLog { void OnRead(long offset, int length); }
    public static IPackReadLog OnRead;
    public long StoreHits, StoreBytes;
    /// <summary>Which pack this is, for a learned read-order map: the TOC's checksum and the file's size (a re-baked pack is another).</summary>
    public string Identity => _tocHash.ToString("x8") + "-" + (PayloadOffset + PayloadSize).ToString();
    /// <summary>A read for the preload (its own thread): the card, in <see cref="MaxRead"/> pieces, never the store and never reported. False at
    /// the pack's end or on an error.</summary>
    public bool PreloadRead(long offset, Span<byte> dst)
    {
        try
        {
            int done = 0;
            while (done < dst.Length)
            {
                int k = RandomAccess.Read(_h, dst.Slice(done, Math.Min(MaxRead, dst.Length - done)), offset + done);
                if (k <= 0) return false;
                done += k;
            }
            return true;
        }
        catch (Exception) { return false; }
    }
#endif
    // strings: decoded lazily from the blob (keys and type names are decoded with the shapes)
    readonly byte[] _strBlob; readonly int[] _strOfs; readonly string[] _str;
    readonly Shape[] _shapes;
    readonly FileRec[] _files;
    readonly Dictionary<string, int> _fileByName = new(StringComparer.OrdinalIgnoreCase);
    readonly (string name, int[] files)[] _bundles;
    readonly Dictionary<string, int> _bundleByName = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _absentFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<(int file, long pathId)> _absentObjects = new();
    readonly byte[] _objToc; readonly int _objCount;   // the object index (32 B each) until a file is first opened
    static readonly SerializedType[] s_noTypes = Array.Empty<SerializedType>();
    readonly Dictionary<int, SerializedType> _typeOf = new();

    sealed class Shape
    {
        public string Type; public string[] Keys; public VitaPackFormat.K[] Kinds;
        // shared by every decoded struct of this shape (TypeTreeObject's shared-keys mode): one key list per shape instead
        // of one per struct, and the lookup index of a wide struct built once
        public List<string> KeyList; public Dictionary<string, int> Index;
    }
    sealed class FileRec { public string Name; public int Bundle; public ushort Flags; public string[] Externals; public int First, Count; public Dictionary<int, int> SourceClasses; public SerializedFile File; }

    // read-ahead cache: a few windows, least recently used refilled. Records are laid out in the boot's first-touch order, so
    // reads are mostly sequential; the extra windows absorb re-reads (a sprite atlas header read once per sprite, Animator
    // re-reads) without thrashing the sequential one. 8 x 128 KiB = 1 MiB of buffers.
    public static int WindowSize = 128 << 10, WindowCount = 8;
    sealed class Win { public byte[] B; public long Start = -1; public int Len; public long Used; }
    Win[] _wins; long _tick;

    // ---- k4-display: the READ-AHEAD WORKER (latency-tolerant work for the Vita's 4th core; its fallback is USER_2) ----
    /// <summary>On (set by the engine before <see cref="Open"/>; SHEPHERD_PACK_READAHEAD=1, off by default): the windows are aligned to
    /// <see cref="WindowSize"/> from the payload start; a record inside one window is served from it, a record across two aligned
    /// windows is put together from both in a scratch buffer, and every window read on demand asks a worker thread for the next one,
    /// so a reader going through the pack in its first-touch order finds that window already read. The worker reads into its own
    /// buffer, handed to the reader's window when needed (a window being read when the reader needs it is waited for, never read
    /// twice). The bytes every reader gets are the same; only when they come off the card changes. Off = the windows as before.</summary>
    public static bool ReadAhead;
    /// <summary>Called first thing on the worker thread with its name (the engine pins it: the Vita's 4th core when the core-unlock
    /// plugin lets it, else USER_2).</summary>
    public static Action<string> OnReadAheadThread;
    public long AheadReads, AheadBytes, AheadHits, AheadWaits, AheadTimeouts, AheadUnused, Straddles; public double AheadMs, AheadWaitedMs, AheadWaitedMaxMs;
    /// <summary>The longest the reader waits for a window the worker is still reading (SHEPHERD_PACK_AHEAD_WAIT_MS, default 25 ms, about two
    /// 128 KiB card reads; 0 = no bound). Past it the reader reads the window itself (pread is thread-safe; the worker's copy is then left
    /// unused): a preempted worker -- on USER_2 it shares the core with sceAudioOut and shepaudio's IO -- never stalls the main thread for
    /// longer. The bytes are the same either way.</summary>
    /// <summary>Test only (SHEPHERD_PACK_AHEAD_DELAY_MS): the worker sleeps this long before each read -- a slow or preempted worker, so the
    /// bounded wait and the reader's own read of a window still being read ahead run on the Mac.</summary>
    static readonly int s_aheadTestDelayMs = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_PACK_AHEAD_DELAY_MS"), out var adm) ? adm : 0;
    public static int AheadWaitBoundMs = Environment.GetEnvironmentVariable("SHEPHERD_PACK_AHEAD_WAIT_MS") is { } awm && int.TryParse(awm, out var awmv) && awmv >= 0 ? awmv : 25;
    readonly object _ah = new();
    // every buffer of the read-ahead path is made in the constructor, on the opening thread (k4 review: a worker that allocated its
    // 128 KiB buffer when the scheduler let it, and a swap that could hand back null depending on timing, made the large-object allocations,
    // and so the GC schedule and the memory peak, vary from run to run). Neither thread allocates afterwards.
    byte[] _ahBuf, _scratch; long _ahStart = -1, _ahWant = -1; int _ahLen; bool _ahBusy, _ahStop, _ahReady; Thread _ahThread;

    PackReader(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Path = path;
        _h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        var hdr = new byte[VitaPackFormat.HeaderSize];
        ReadExact(0, hdr);
        if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != VitaPackFormat.Magic) throw new InvalidDataException($"{path}: not a Shepherd Vita pack");
        int ver = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(4));
        if (ver != VitaPackFormat.Version) throw new InvalidDataException($"{path}: pack version {ver}, this engine reads {VitaPackFormat.Version}");
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
        uint tocHash = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
#if !SHEPHERD_NO_K8
        _tocHash = tocHash;
#endif
        long tocOff = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(16)), tocSize = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(24));
        PayloadOffset = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(32)); PayloadSize = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(40));
        TocBytes = tocSize;
        var toc = new byte[checked((int)tocSize)];
        ReadExact(tocOff, toc);
        if (VitaPackFormat.Fnv1a(toc) != tocHash) throw new InvalidDataException($"{path}: TOC checksum mismatch (truncated or corrupt pack)");
        var r = new SpanReader(toc, bigEndian: false);
        // strings
        int ns = r.I32(); _strOfs = new int[ns + 1];
        for (int i = 0; i <= ns; i++) _strOfs[i] = r.I32();
        _strBlob = r.Bytes(_strOfs[ns]).ToArray(); _str = new string[ns];
        // shapes
        int nsh = r.I32(); _shapes = new Shape[nsh];
        for (int i = 0; i < nsh; i++)
        {
            var s = new Shape { Type = Str(r.I32()) }; int nf = r.U16(); s.Keys = new string[nf]; s.Kinds = new VitaPackFormat.K[nf];
            for (int k = 0; k < nf; k++) { s.Keys[k] = Str(r.I32()); s.Kinds[k] = (VitaPackFormat.K)r.U8(); }
            s.KeyList = new List<string>(s.Keys);
            if (nf > 8) { s.Index = new Dictionary<string, int>(nf, StringComparer.Ordinal); for (int k = 0; k < nf; k++) s.Index.TryAdd(s.Keys[k], k); }
            _shapes[i] = s;
        }
        // bundles
        int nb = r.I32(); _bundles = new (string, int[])[nb];
        for (int i = 0; i < nb; i++)
        {
            var name = Str(r.I32()); int nf = r.U16(); var fl = new int[nf]; for (int k = 0; k < nf; k++) fl[k] = r.I32();
            _bundles[i] = (name, fl); _bundleByName[name] = i;
        }
        // files
        int nfi = r.I32(); _files = new FileRec[nfi];
        for (int i = 0; i < nfi; i++)
        {
            var f = new FileRec { Name = Str(r.I32()), Bundle = r.I32(), Flags = r.U16() };
            int ne = r.U16(); f.Externals = new string[ne]; for (int k = 0; k < ne; k++) f.Externals[k] = Str(r.I32());
            f.First = r.I32(); f.Count = r.I32();
            int nc = r.U16(); f.SourceClasses = new Dictionary<int, int>(nc); for (int k = 0; k < nc; k++) { int c = r.I32(); f.SourceClasses[c] = r.I32(); }
            _files[i] = f; _fileByName[f.Name] = i;
        }
        int na = r.I32(); for (int i = 0; i < na; i++) _absentFiles.Add(Str(r.I32()));
        _objCount = r.I32(); _objToc = r.Bytes(_objCount * 32).ToArray();
        int nao = r.I32(); for (int i = 0; i < nao; i++) { int fi = r.I32(); long pid = r.I64(); _absentObjects.Add((fi, pid)); }
        if ((Flags & VitaPackFormat.FlagProperties) != 0) { int np = r.I32(); for (int i = 0; i < np; i++) { var k = Str(r.I32()); _props[k] = Str(r.I32()); } }
        if (ReadAhead)
        {   // all of the read-ahead path's buffers, now and on this thread: WindowCount windows + the worker's one + the straddle scratch
            _wins = Enumerable.Range(0, WindowCount).Select(_ => new Win { B = new byte[WindowSize] }).ToArray();
            _ahBuf = new byte[WindowSize]; _scratch = new byte[WindowSize / 2];
        }
        OpenMs = sw.Elapsed.TotalMilliseconds;
    }

    public static PackReader Open(string path)
    {
        if (Directory.Exists(path)) path = System.IO.Path.Combine(path, VitaPackFormat.PackFileName);
        return new PackReader(path);
    }

    string Str(int id)
    {
        if (id < 0) return null;
        var s = _str[id];
        if (s != null) return s;
        s = Encoding.UTF8.GetString(_strBlob, _strOfs[id], _strOfs[id + 1] - _strOfs[id]);
        return _str[id] = string.IsInterned(s) ?? s;
    }

    // ------------------------------------------------------------------ files / bundles

    public bool HasFile(string name) => _fileByName.ContainsKey(name);
    /// <summary>The file was looked for during the recorded boot and does not exist in the game data either.</summary>
    public bool IsAbsentFile(string name) => _absentFiles.Contains(name);
    public bool IsAbsentObject(SerializedFile f, long pathId) => f.PackFileIndex >= 0 && _absentObjects.Contains((f.PackFileIndex, pathId));
    public IEnumerable<string> FileNames => _files.Select(f => f.Name);
    public IEnumerable<string> BundleNames => _bundles.Select(b => b.name);
    public string BundleOf(string fileName) => _fileByName.TryGetValue(fileName, out var i) && _files[i].Bundle >= 0 ? _bundles[_files[i].Bundle].name : null;

    /// <summary>The pack's copy of a serialized file (objects in the pack only), or null when the pack does not have it.</summary>
    public SerializedFile GetFile(string name)
    {
        if (!_fileByName.TryGetValue(name, out var i)) return null;
        return FileAt(i);
    }

    SerializedFile FileAt(int i)
    {
        var fr = _files[i];
        lock (_lock)
        {
            if (fr.File != null) return fr.File;
            var objs = new List<ObjectInfo>(fr.Count);
            for (int k = 0; k < fr.Count; k++)
            {
                var e = _objToc.AsSpan((fr.First + k) * 32, 32);
                int cls = BinaryPrimitives.ReadInt32LittleEndian(e[20..]);
                if (!_typeOf.TryGetValue(cls, out var st)) _typeOf[cls] = st = new SerializedType { ClassId = cls };
                objs.Add(new ObjectInfo
                {
                    PathId = BinaryPrimitives.ReadInt64LittleEndian(e), ByteStart = PayloadOffset + BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                    ByteSize = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]), ClassId = cls, Type = st, TypeIndex = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]),
                });
            }
            var ext = fr.Externals.Select(p => new FileIdentifier { PathName = p }).ToList();
            return fr.File = SerializedFile.FromPack(fr.Name, this, i, ext, objs, fr.SourceClasses);
        }
    }

    /// <summary>Called once per gap found: an enumeration of a class the pack holds only part of (scene GameObjects, AssetBundle
    /// headers, ...). Lookups by PathId report through <see cref="AssetDatabase.PackMiss"/>.</summary>
    public Action<string> Miss { get; set; }
    readonly HashSet<(int, int)> _checked = new();

    /// <summary>Enumerating a file's objects of one class: a gap here is silent otherwise (nobody asks for the missing PathIds).</summary>
    internal void CheckEnumeration(SerializedFile f, int classId, int have)
    {
        int src = f.PackSourceCount(classId);
        if (have >= src) return;
        lock (_checked) if (!_checked.Add((f.PackFileIndex, classId))) return;
        Miss?.Invoke($"{src - have} of the {src} {ClassIds.Name(classId)} objects of {f.Name} (enumerated by class; the pack has {have})");
    }

    /// <summary>The serialized files of a bundle (by bundle file name), in the bundle's node order; null when the pack does not have the bundle.</summary>
    public IReadOnlyList<SerializedFile> BundleFiles(string bundleFileName)
    {
        if (!_bundleByName.TryGetValue(bundleFileName, out var b)) return null;
        return _bundles[b].files.Select(FileAt).ToList();
    }

    // ------------------------------------------------------------------ records

    /// <summary>Bytes of [offset, offset+len) of the pack, from the read-ahead window when possible. Caller holds _lock and
    /// must finish with the span before the next read.</summary>
    ReadOnlySpan<byte> Bytes(long offset, int len)
    {
        _wins ??= Enumerable.Range(0, WindowCount).Select(_ => new Win()).ToArray();
        if (ReadAhead && len <= WindowSize / 2 && offset >= PayloadOffset && offset + len <= PayloadOffset + PayloadSize) return AlignedBytes(offset, len);
        Win lru = _wins[0];
        foreach (var w in _wins)
        {
            if (offset >= w.Start && offset + len <= w.Start + w.Len) { WindowHits++; w.Used = ++_tick; return w.B.AsSpan((int)(offset - w.Start), len); }
            if (w.Used < lru.Used) lru = w;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Reads++;
        ReadOnlySpan<byte> res;
        if (len > WindowSize / 2)
        {
            var b = GC.AllocateUninitializedArray<byte>(len); ReadExact(offset, b); BytesRead += len; res = b;
        }
        else
        {
            lru.B ??= new byte[WindowSize];
            long end = Math.Min(PayloadOffset + PayloadSize, offset + WindowSize);
            int n = (int)Math.Max(len, end - offset);
            ReadExact(offset, lru.B.AsSpan(0, n)); BytesRead += n;
            lru.Start = offset; lru.Len = n; lru.Used = ++_tick; res = lru.B.AsSpan(0, len);
        }
        ReadMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return res;
    }

    /// <summary><see cref="ReadAhead"/>: [offset, offset+len) from the aligned window(s) holding it (len &lt;= WindowSize / 2, so at most two).</summary>
    ReadOnlySpan<byte> AlignedBytes(long offset, int len)
    {
        long k0 = (offset - PayloadOffset) / WindowSize, k1 = (offset + len - 1 - PayloadOffset) / WindowSize;
        var w0 = Window(k0);
        if (k1 == k0) return w0.B.AsSpan((int)(offset - w0.Start), len);
        Straddles++;
        int a = (int)(w0.Start + w0.Len - offset);
        w0.B.AsSpan((int)(offset - w0.Start), a).CopyTo(_scratch);   // (copied before the second window may take w0's slot)
        var w1 = Window(k1);
        w1.B.AsSpan(0, len - a).CopyTo(_scratch.AsSpan(a));
        return _scratch.AsSpan(0, len);
    }
    /// <summary>Aligned window k: resident, or the worker's (read ahead), or read now; then the next one is asked of the worker.</summary>
    Win Window(long k)
    {
        long start = PayloadOffset + k * WindowSize;
        Win lru = _wins[0];
        foreach (var w in _wins)
        {
            if (w.Start == start) { WindowHits++; w.Used = ++_tick; return w; }
            if (w.Used < lru.Used) lru = w;
        }
        int n = (int)Math.Min(WindowSize, PayloadOffset + PayloadSize - start);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lru.Start = -1;
        if (!TakeAhead(start, lru))
        {
            lru.B ??= new byte[WindowSize];
            ReadExact(start, lru.B.AsSpan(0, n)); Reads++; BytesRead += n;
        }
        lru.Start = start; lru.Len = n; lru.Used = ++_tick;
        ReadMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (!AheadSequentialOnly || Resident(start - WindowSize)) AskAhead(start + WindowSize);   // a sequential run: the next window
        return lru;
    }
    /// <summary>The worker has (or is reading) the window at start: wait for it if it is being read (at most <see cref="AheadWaitBoundMs"/>),
    /// then swap its buffer into w (both buffers exist since Open: the swap never hands back null and nothing is allocated).</summary>
    bool TakeAhead(long start, Win w)
    {
#if !SHEPHERD_EBOOT
        Sched.Point(Sched.AheadTake);   // (kit #7 tests: a seeded-schedule point; Mac only)
#endif
        lock (_ah)
        {
            if (_ahBusy && _ahWant == start)
            {
                AheadWaits++;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long limit = AheadWaitBoundMs > 0 ? t0 + AheadWaitBoundMs * System.Diagnostics.Stopwatch.Frequency / 1000 : long.MaxValue;
                while (_ahBusy)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now >= limit) break;
#if SHEPHERD_EBOOT
                    if (AheadWaitBoundMs > 0) Monitor.Wait(_ah, (int)Math.Max(1, (limit - now) * 1000 / System.Diagnostics.Stopwatch.Frequency)); else Monitor.Wait(_ah);
#else
                    // (kit #7 tests: under a seeded scheduler the bounded wait's timeout is the scheduler's choice, not the wall clock's)
                    if (!Sched.Wait(_ah, AheadWaitBoundMs > 0 ? (int)Math.Max(1, (limit - now) * 1000 / System.Diagnostics.Stopwatch.Frequency) : -1) && Sched.Cur != null) break;
#endif
                }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                AheadWaitedMs += ms; if (ms > AheadWaitedMaxMs) AheadWaitedMaxMs = ms;
                if (_ahBusy) { AheadTimeouts++; return false; }   // still being read: the reader reads it itself (the worker's copy goes unused)
            }
            else if (!_ahBusy && _ahWant == start) _ahWant = -1;   // asked for but not started: the reader reads it now (never twice)
            if (_ahStart != start) return false;
            var b = w.B; w.B = _ahBuf; _ahBuf = b;
            _ahStart = -1; AheadHits++;
            return true;
        }
    }
    public static bool AheadSequentialOnly = Environment.GetEnvironmentVariable("SHEPHERD_PACK_AHEAD_SEQ") != "0";
    bool Resident(long start) { foreach (var w in _wins) if (w.Start == start) return true; return false; }
    void AskAhead(long start)
    {
        if (start >= PayloadOffset + PayloadSize) return;
        foreach (var w in _wins) if (w.Start == start) return;
#if !SHEPHERD_EBOOT
        Sched.Point(Sched.AheadAsk);
#endif
        lock (_ah)
        {
            if (_ahStart == start || _ahBusy) return;   // there already, or the worker is reading (one request at a time: the card serves one)
            if (_ahStart >= 0) AheadUnused++;           // a window read ahead and never asked for
            _ahStart = -1; _ahWant = start;
            if (_ahThread == null)
            {
                _ahThread = new Thread(AheadLoop, 256 * 1024) { IsBackground = true, Name = "shepherd-readahead" };
#if !SHEPHERD_EBOOT
                Sched.Spawn("shepherd-readahead");
#endif
                _ahThread.Start();
                // the worker's start-up (thread attach, the pin and its log line) runs while this thread waits: whatever it allocates
                // lands at the same point of the main thread's allocation sequence in every run
#if SHEPHERD_EBOOT
                while (!_ahReady) Monitor.Wait(_ah);
#else
                while (!_ahReady) Sched.Wait(_ah);
#endif
            }
#if SHEPHERD_EBOOT
            Monitor.PulseAll(_ah);
#else
            Sched.PulseAll(_ah);
#endif
        }
    }
    void AheadLoop()
    {
        try { OnReadAheadThread?.Invoke("shepherd-readahead"); } catch { }
#if SHEPHERD_EBOOT
        lock (_ah) { _ahReady = true; Monitor.PulseAll(_ah); }
#else
        Sched.Point(Sched.AheadStart);
        lock (_ah) { _ahReady = true; Sched.PulseAll(_ah); }
#endif
        for (;;)
        {
            long want; byte[] buf;
#if !SHEPHERD_EBOOT
            Sched.Point(Sched.AheadLoopWait);
#endif
            lock (_ah)
            {
#if SHEPHERD_EBOOT
                while (!_ahStop && _ahWant < 0) Monitor.Wait(_ah);
                if (_ahStop) return;
#else
                while (!_ahStop && _ahWant < 0) Sched.Wait(_ah);
                if (_ahStop) { Sched.Leave(); return; }
#endif
                want = _ahWant; _ahBusy = true; buf = _ahBuf;   // (made in the constructor; the reader only ever swaps it for another)
            }
            int n = (int)Math.Min(WindowSize, PayloadOffset + PayloadSize - want);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s_aheadTestDelayMs > 0) Thread.Sleep(s_aheadTestDelayMs);   // (test only: a slow / preempted worker, the bounded wait's path)
            bool ok = true;
#if !SHEPHERD_NO_K8
            try { ReadExact(want, buf.AsSpan(0, n), ahead: true); } catch (Exception) { ok = false; }   // (the reader then reads it itself)
#else
            try { ReadExact(want, buf.AsSpan(0, n)); } catch (Exception) { ok = false; }   // (the reader then reads it itself)
#endif
#if !SHEPHERD_EBOOT
            Sched.Point(Sched.AheadLoopDone);
#endif
            lock (_ah)
            {
                _ahBusy = false; _ahWant = -1;
                if (ok) { _ahStart = want; _ahLen = n; AheadReads++; AheadBytes += n; AheadMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
#if SHEPHERD_EBOOT
                Monitor.PulseAll(_ah);
#else
                Sched.PulseAll(_ah);
#endif
            }
        }
    }

    /// <summary>kit #3 review: at most <see cref="MaxRead"/> per read call. The Vita's card serves one request at a time (~10 MiB/s), and a
    /// several-MiB object read in one call kept shepaudio's IO thread (the music's stream refills, 0.68-1.37 s rings) waiting behind it for
    /// up to ~0.8 s; in 512 KiB pieces its reads get in between. The bytes and the read order are the same.</summary>
    const int MaxRead = 512 * 1024;
#if !SHEPHERD_NO_K8
    void ReadExact(long offset, Span<byte> dst, bool ahead = false)
    {
        if (!ahead)
        {   // kit #8 (K8Preload): every on-demand read is reported for the read-order map (also one the store serves: the map is the
            // scene's whole read order), then the preload's store is asked first
            OnRead?.OnRead(offset, dst.Length);
            var st = Store;
            if (st != null && st.Take(offset, dst)) { lock (_lock) { StoreHits++; StoreBytes += dst.Length; } return; }
        }
#else
    void ReadExact(long offset, Span<byte> dst)
    {
#endif
        int done = 0;
        while (done < dst.Length)
        {
            int k = RandomAccess.Read(_h, dst.Slice(done, Math.Min(MaxRead, dst.Length - done)), offset + done);
            if (k <= 0) throw new EndOfStreamException($"{Path}: read {dst.Length} B at {offset} past end");
            done += k;
        }
    }

    struct Rec { public int Tree, Raw, Image, Script; public int TreeAt => 12 + Script; public int RawAt => TreeAt + Tree; public int ImageAt => RawAt + Raw; }
    static Rec Header(ReadOnlySpan<byte> rec, ObjectInfo o) => new()
    {
        Tree = BinaryPrimitives.ReadInt32LittleEndian(rec), Raw = BinaryPrimitives.ReadInt32LittleEndian(rec[4..]), Image = BinaryPrimitives.ReadInt32LittleEndian(rec[8..]),
        Script = (o.TypeIndex & VitaPackFormat.ObjScript) != 0 ? 12 : 0,
    };

    public static bool HasTree(ObjectInfo o) => (o.TypeIndex & VitaPackFormat.ObjTree) != 0;

    /// <summary>The object's value tree (same shape <see cref="TypeTreeReader.Read(SerializedFile, ObjectInfo)"/> returns), with
    /// a <see cref="PackImage"/> under <see cref="ImageKey"/> for textures.</summary>
    public TypeTreeObject ReadTree(SerializedFile f, ObjectInfo o)
    {
        if (!HasTree(o)) throw new InvalidOperationException($"{f.Name}: no value tree in the pack for {ClassIds.Name(o.ClassId)} #{o.PathId} (raw-only record)");
        lock (_lock)
        {
            var rec = Bytes(o.ByteStart, (int)o.ByteSize);
            var h = Header(rec, o);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var d = new Decoder(this, rec.Slice(h.TreeAt, h.Tree));
            var t = d.Value() as TypeTreeObject ?? throw new InvalidDataException($"pack: record {f.Name} #{o.PathId} is not a struct");
            t.BytesRead = h.Tree;
            if (h.Image >= 28)
            {
                var im = rec.Slice(h.ImageAt, h.Image);
                int fmt = BinaryPrimitives.ReadInt32LittleEndian(im), w = BinaryPrimitives.ReadInt32LittleEndian(im[4..]), hh = BinaryPrimitives.ReadInt32LittleEndian(im[8..]);
                uint fl = BinaryPrimitives.ReadUInt32LittleEndian(im[12..]); int len = BinaryPrimitives.ReadInt32LittleEndian(im[16..]);
                long off = BinaryPrimitives.ReadInt64LittleEndian(im[20..]);
                t.Add(ImageKey, new PackImage(this, PayloadOffset + off, len, fmt, w, hh, (fl & VitaPackFormat.ImageTopDown) != 0));
            }
            TreeDecodes++;
            DecodeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return t;
        }
    }

    /// <summary>The object's raw serialized bytes (only stored for objects the engine reads raw). A fresh array.</summary>
    public byte[] ReadRaw(SerializedFile f, ObjectInfo o)
    {
        if ((o.TypeIndex & VitaPackFormat.ObjRaw) == 0) throw new InvalidOperationException($"{f.Name}: no raw bytes in the pack for {ClassIds.Name(o.ClassId)} #{o.PathId} (the engine read it as a tree when the pack was baked)");
        lock (_lock)
        {
            RawReads++;
            if (o.ByteSize > WindowSize / 2)
            {   // a big record (TextAssets, stripped MonoBehaviours): its header through the window, the raw part straight
                // into the result -- one allocation instead of a record-sized buffer plus a copy
                var hb = Bytes(o.ByteStart, Math.Min((int)o.ByteSize, 24));
                var hh = Header(hb, o);
                var res = GC.AllocateUninitializedArray<byte>(hh.Raw);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ReadExact(o.ByteStart + hh.RawAt, res); Reads++; BytesRead += res.Length;
                ReadMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                return res;
            }
            var rec = Bytes(o.ByteStart, (int)o.ByteSize);
            var h = Header(rec, o);
            return rec.Slice(h.RawAt, h.Raw).ToArray();
        }
    }

    /// <summary>Length of the object's raw bytes (0 when it has none).</summary>
    public int RawLength(ObjectInfo o)
    {
        if ((o.TypeIndex & VitaPackFormat.ObjRaw) == 0) return 0;
        lock (_lock) return Header(Bytes(o.ByteStart, Math.Min((int)o.ByteSize, 24)), o).Raw;
    }

    /// <summary>kit6 mem (L1b): bytes [skip, skip+dst.Length) of the object's raw bytes (clamped to the record) into dst, for a stream over a
    /// big TextAsset (the reader then never holds the whole record in one array); returns how many were copied (0 at the end).</summary>
    public int ReadRawInto(SerializedFile f, ObjectInfo o, int skip, Span<byte> dst)
    {
        if ((o.TypeIndex & VitaPackFormat.ObjRaw) == 0) throw new InvalidOperationException($"{f.Name}: no raw bytes in the pack for {ClassIds.Name(o.ClassId)} #{o.PathId}");
        lock (_lock)
        {
            var hh = Header(Bytes(o.ByteStart, Math.Min((int)o.ByteSize, 24)), o);
            skip = Math.Clamp(skip, 0, hh.Raw); int take = Math.Min(dst.Length, hh.Raw - skip);
            if (take <= 0) return 0;
            if (take > WindowSize / 2) { ReadExact(o.ByteStart + hh.RawAt + skip, dst[..take]); Reads++; BytesRead += take; }
            else Bytes(o.ByteStart + hh.RawAt + skip, take).CopyTo(dst);
            return take;
        }
    }

    /// <summary>[skip, skip+take) of the object's raw bytes (clamped to the record), read straight into a fresh array: a caller
    /// that keeps only part of a big record (a TextAsset's bytes after its name) allocates just that part.</summary>
    public byte[] ReadRawRange(SerializedFile f, ObjectInfo o, int skip, int take)
    {
        if ((o.TypeIndex & VitaPackFormat.ObjRaw) == 0) throw new InvalidOperationException($"{f.Name}: no raw bytes in the pack for {ClassIds.Name(o.ClassId)} #{o.PathId}");
        lock (_lock)
        {
            var hb = Bytes(o.ByteStart, Math.Min((int)o.ByteSize, 24));
            var hh = Header(hb, o);
            skip = Math.Clamp(skip, 0, hh.Raw); take = Math.Clamp(take, 0, hh.Raw - skip);
            var res = GC.AllocateUninitializedArray<byte>(take);
            if (take > 0)
            {
                if (take > WindowSize / 2) { ReadExact(o.ByteStart + hh.RawAt + skip, res); Reads++; BytesRead += take; }
                else Bytes(o.ByteStart + hh.RawAt + skip, take).CopyTo(res);
            }
            RawReads++;
            return res;
        }
    }

    public PPtr ScriptPPtr(ObjectInfo o)
    {
        if ((o.TypeIndex & VitaPackFormat.ObjScript) == 0) return default;
        lock (_lock)
        {
            var rec = Bytes(o.ByteStart, Math.Min((int)o.ByteSize, 24));
            return new PPtr(BinaryPrimitives.ReadInt32LittleEndian(rec[12..]), BinaryPrimitives.ReadInt64LittleEndian(rec[16..]));
        }
    }

    public byte[] ReadImage(PackImage img)
    {
        var b = GC.AllocateUninitializedArray<byte>(img.Length);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ReadExact(img.Offset, b);
        lock (_lock) { ImageReads++; ImageBytes += b.Length; Reads++; BytesRead += b.Length; ReadMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        return b;
    }

    /// <summary>Kit #6 (c): part of an image into the caller's buffer (counted with the image reads: one more read, its bytes).</summary>
    public void ReadImageRange(PackImage img, int offset, Span<byte> dst)
    {
        if (offset < 0 || offset + dst.Length > img.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ReadExact(img.Offset + offset, dst);
        lock (_lock) { if (offset == 0) ImageReads++; ImageBytes += dst.Length; Reads++; BytesRead += dst.Length; ReadMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
    }

    public string Stats() =>
        $"pack {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))}: open {OpenMs:F1} ms (TOC {TocBytes / 1048576.0:F2} MiB, {_objCount} objects in {_files.Length} files, {_bundles.Length} bundles, {_shapes.Length} shapes, {_str.Length} strings); " +
        $"{Reads} reads {BytesRead / 1048576.0:F1} MiB in {ReadMs:F0} ms (window hits {WindowHits}); {TreeDecodes} trees decoded in {DecodeMs:F0} ms; {RawReads} raw; {ImageReads} images {ImageBytes / 1048576.0:F1} MiB; " +
        $"primitive boxes shared {BoxesShared}, new {BoxesNew}" + AheadStats();
    /// <summary><see cref="ReadAhead"/>'s part of <see cref="Stats"/> (empty when off).</summary>
    string AheadStats()
    {
        if (!ReadAhead) return "";
        lock (_ah) return $"; read-ahead (aligned {WindowSize >> 10} KiB windows): {AheadReads} windows {AheadBytes / 1048576.0:F1} MiB read ahead in {AheadMs:F0} ms on the worker, " +
            $"{AheadHits} used ({AheadWaits} waited for: main thread waited {AheadWaitedMs:F1} ms in all, longest {AheadWaitedMaxMs:F1} ms, {AheadTimeouts} past the " +
            $"{(AheadWaitBoundMs > 0 ? AheadWaitBoundMs + " ms" : "(no)")} bound read by the reader itself), {AheadUnused + (_ahStart >= 0 ? 1 : 0)} unused; {Straddles} records across two windows";
    }

#if SHEPHERD_EBOOT
    public void Dispose() { lock (_ah) { _ahStop = true; Monitor.PulseAll(_ah); } _h.Dispose(); }
#else
    public void Dispose() { Sched.Point(Sched.AheadDispose); lock (_ah) { _ahStop = true; Sched.PulseAll(_ah); } _h.Dispose(); }
#endif

    // ------------------------------------------------------------------ value decoder

    /// <summary>Boxes shared by every decoded tree (the value-tree shape boxes every primitive; values are immutable boxes, so
    /// equal values can share one): bool, every 8-bit value, 16/32/64-bit integers in [-128, 1023], and floats through a
    /// direct-mapped cache on their bit pattern (repeated 0, 1, 0.5, colour channels...). Only used under the reader's lock.</summary>
    const int SmallMin = -128, SmallMax = 1023;
    static readonly object BoxTrue = true, BoxFalse = false;
    static readonly object[] BoxU8 = Enumerable.Range(0, 256).Select(i => (object)(byte)i).ToArray();
    static readonly object[] BoxI8 = Enumerable.Range(0, 256).Select(i => (object)(sbyte)(i - 128)).ToArray();
    static readonly object[] BoxChar = Enumerable.Range(0, 256).Select(i => (object)(char)i).ToArray();
    static readonly object[] BoxI32 = Enumerable.Range(SmallMin, SmallMax - SmallMin + 1).Select(i => (object)i).ToArray();
    static readonly object[] BoxU32 = Enumerable.Range(0, SmallMax + 1).Select(i => (object)(uint)i).ToArray();
    static readonly object[] BoxI16 = Enumerable.Range(SmallMin, SmallMax - SmallMin + 1).Select(i => (object)(short)i).ToArray();
    static readonly object[] BoxU16 = Enumerable.Range(0, SmallMax + 1).Select(i => (object)(ushort)i).ToArray();
    static readonly object[] BoxI64 = Enumerable.Range(SmallMin, SmallMax - SmallMin + 1).Select(i => (object)(long)i).ToArray();
    static readonly object[] BoxU64 = Enumerable.Range(0, SmallMax + 1).Select(i => (object)(ulong)i).ToArray();
    readonly object[] _f32 = new object[4096];
    public long BoxesShared, BoxesNew;
    /// <summary>Measurement switches (the engine's SHEPHERD_VITA_MEM=noboxes / nokeys): decode with a fresh box per primitive /
    /// a key list per struct, as before stage 1.</summary>
    public static bool ShareBoxes = true, ShareKeys = true;
    object F32(float v)
    {
        if (!ShareBoxes) { BoxesNew++; return v; }
        int bits = BitConverter.SingleToInt32Bits(v); int slot = (int)((uint)(bits * -1640531535) >> 20);   // 12-bit Fibonacci hash
        if (_f32[slot] is float c && BitConverter.SingleToInt32Bits(c) == bits) { BoxesShared++; return _f32[slot]; }
        BoxesNew++; return _f32[slot] = v;
    }
    object Small(long v, object[] table, int min) { if (ShareBoxes && v >= min && v - min < table.Length) { BoxesShared++; return table[v - min]; } BoxesNew++; return null; }

    ref struct Decoder
    {
        readonly PackReader _p; readonly ReadOnlySpan<byte> _b; int _i;
        public Decoder(PackReader p, ReadOnlySpan<byte> b) { _p = p; _b = b; _i = 0; }

        uint VarU() { uint v = 0; int s = 0; byte x; do { x = _b[_i++]; v |= (uint)(x & 0x7F) << s; s += 7; } while ((x & 0x80) != 0); return v; }

        public object Value() => Read((VitaPackFormat.K)_b[_i++]);

        object Read(VitaPackFormat.K k)
        {
            switch (k)
            {
                case VitaPackFormat.K.Null: return null;
                case VitaPackFormat.K.Bool: if (!ShareBoxes) return _b[_i++] != 0; _p.BoxesShared++; return _b[_i++] != 0 ? BoxTrue : BoxFalse;
                case VitaPackFormat.K.I8: if (!ShareBoxes) return (sbyte)_b[_i++]; _p.BoxesShared++; return BoxI8[(sbyte)_b[_i++] + 128];
                case VitaPackFormat.K.U8: if (!ShareBoxes) return _b[_i++]; _p.BoxesShared++; return BoxU8[_b[_i++]];
                case VitaPackFormat.K.Char: if (!ShareBoxes) return (char)_b[_i++]; _p.BoxesShared++; return BoxChar[_b[_i++]];
                case VitaPackFormat.K.I16: { var v = BinaryPrimitives.ReadInt16LittleEndian(_b[_i..]); _i += 2; return _p.Small(v, BoxI16, SmallMin) ?? v; }
                case VitaPackFormat.K.U16: { var v = BinaryPrimitives.ReadUInt16LittleEndian(_b[_i..]); _i += 2; return _p.Small(v, BoxU16, 0) ?? v; }
                case VitaPackFormat.K.I32: { var v = BinaryPrimitives.ReadInt32LittleEndian(_b[_i..]); _i += 4; return _p.Small(v, BoxI32, SmallMin) ?? v; }
                case VitaPackFormat.K.U32: { var v = BinaryPrimitives.ReadUInt32LittleEndian(_b[_i..]); _i += 4; return _p.Small(v, BoxU32, 0) ?? v; }
                case VitaPackFormat.K.I64: { var v = BinaryPrimitives.ReadInt64LittleEndian(_b[_i..]); _i += 8; return _p.Small(v, BoxI64, SmallMin) ?? v; }
                case VitaPackFormat.K.U64: { var v = BinaryPrimitives.ReadUInt64LittleEndian(_b[_i..]); _i += 8; if (ShareBoxes && v <= SmallMax) { _p.BoxesShared++; return BoxU64[v]; } _p.BoxesNew++; return v; }
                case VitaPackFormat.K.F32: { var v = BinaryPrimitives.ReadSingleLittleEndian(_b[_i..]); _i += 4; return _p.F32(v); }
                case VitaPackFormat.K.F64: { var v = BinaryPrimitives.ReadDoubleLittleEndian(_b[_i..]); _i += 8; return v; }
                case VitaPackFormat.K.Str: return Str();
                case VitaPackFormat.K.PPtr: { int fid = BinaryPrimitives.ReadInt32LittleEndian(_b[_i..]); long pid = BinaryPrimitives.ReadInt64LittleEndian(_b[(_i + 4)..]); _i += 12; return new PPtr(fid, pid); }
                case VitaPackFormat.K.Bytes: { int n = (int)VarU(); var v = _b.Slice(_i, n).ToArray(); _i += n; return v; }
                case VitaPackFormat.K.Struct: return Struct();
                case VitaPackFormat.K.List: return List();
                case VitaPackFormat.K.Any: return Value();
                default: throw new InvalidDataException($"pack: bad value kind {(int)k}");
            }
        }

        string Str()
        {
            uint v = VarU();
            if ((v & 1) != 0) return _p.Str((int)(v >> 1));
            int n = (int)(v >> 1); var s = Encoding.UTF8.GetString(_b.Slice(_i, n)); _i += n; return s;
        }

        TypeTreeObject Struct()
        {
            var sh = _p._shapes[VarU()];
            if (!ShareKeys) { var u = new TypeTreeObject(sh.Type, sh.Keys.Length); for (int i = 0; i < sh.Keys.Length; i++) u.Add(sh.Keys[i], Read(sh.Kinds[i])); return u; }
            var o = new TypeTreeObject(sh.Type, sh.KeyList, sh.Index);
            for (int i = 0; i < sh.Keys.Length; i++) o.AddValue(Read(sh.Kinds[i]));
            return o;
        }

        List<object> List()
        {
            int n = (int)VarU(); var ek = (VitaPackFormat.K)_b[_i++];
            var l = new List<object>(n);
            for (int i = 0; i < n; i++) l.Add(Read(ek));
            return l;
        }
    }
}
