using System.Collections.Concurrent;
using System.Text;

namespace Shepherd.Assets;

public sealed class TypeTreeNode
{
    public int Level;
    public string Type;
    public string Name;
    public int ByteSize;
    public int Index;
    public int MetaFlag;
    public int Version;
    public byte TypeFlags;        // bit0 = array, bit1 = ref-type (managed reference)
    public ulong RefTypeHash;

    public bool AlignAfter => (MetaFlag & 0x4000) != 0;
    public override string ToString() => $"{new string(' ', Level * 2)}{Type} {Name} (size {ByteSize}, flags 0x{MetaFlag:X})";
}

public sealed class SerializedType
{
    public int ClassId;
    public bool IsStripped;
    public short ScriptTypeIndex = -1;
    public byte[] ScriptId;       // 16 bytes (MonoBehaviour / ref types)
    public byte[] OldTypeHash;    // 16 bytes
    public List<TypeTreeNode> Nodes; // null when the file has no type trees
    public int[] TypeDependencies;
    public string KlassName, NameSpace, AsmName; // [SerializeReference] ref types only

    public string ScriptIdHex => ScriptId == null ? null : Convert.ToHexString(ScriptId).ToLowerInvariant();
    public string OldTypeHashHex => OldTypeHash == null ? null : Convert.ToHexString(OldTypeHash).ToLowerInvariant();
}

#if SHEPHERD_K11_TOC
// Kit #12 walkb review fix: the build switch SHEPHERD_K12_TOC_CODE=0 (vita/shepherd/build.sh -> -p:ShepherdK12TocCode=0 -> compile symbol
// SHEPHERD_K11_TOC) compiles kit #11's index entries, verbatim (one ObjectInfo object per entry, kit #11's callers): kit #11's eboot span
// as well as its memory. The view's struct costs AOT code (its generic instantiations) whatever the env file says. Default: the view (#else).
public sealed class ObjectInfo
{
    // Device kit #11, track walk (the engine records diet; vita/notes/kit11-walk.md): 40 bytes on the Vita (was 48). 32-bit Mono's auto
    // layout puts the reference first (Type at 8), then the other fields in declaration order at their own alignment: TypeIndex fills the
    // 4 bytes before the first long (PathId at 16, ByteStart at 24, ByteSize at 32, LivePos at 36). ClassId was a copy of Type.ClassId in
    // both makers (SerializedFile's object table, VitaPack's TOC): it is read from Type now. 148,226 entries on the intro walk: -1.1 MiB.
    // The build switch back (Shepherd.Engine's build.sh SHEPHERD_K11_RECORDS=0: compile symbol SHEPHERD_K10_RECORDS) = kit #10's 48-byte entry.
#if SHEPHERD_K10_RECORDS
    public long PathId;
    public long ByteStart;     // absolute offset in the file data (data offset already added)
    public uint ByteSize;
    public int TypeIndex;
    public int ClassId;
    public SerializedType Type;
#else
    public int TypeIndex;
    public long PathId;
    public long ByteStart;     // absolute offset in the file data (data offset already added)
    public uint ByteSize;
    public SerializedType Type;
    /// <summary>The object's Unity class id: its <see cref="Type"/>'s (every maker sets Type; the field it replaces always held the same value).</summary>
    public int ClassId => Type.ClassId;
#endif
    /// <summary>kit6 mem L4c (Shepherd.Engine Assets/Loader.cs): 1 + this object's position in the loader's ordered list of live records, 0
    /// = none (the loader's (file name, path id) map kept on the index entry itself; on 32-bit Mono it fills the entry's alignment pad).</summary>
    public int LivePos;
    public override string ToString() => $"{ClassIds.Name(ClassId)} #{PathId}";
}

#else
/// <summary>Device kit #12, track walkb (the load-only index entries as a TOC view; vita/notes/kit12-walkb.md): one entry of a serialized file's
/// object table -- a VIEW (its table + its index: 8 bytes, no object of its own) over the table's 32-byte records. A pack file's table is a
/// slice of the pack's TOC itself (which the reader keeps whole anyway: <see cref="PackReader"/>'s object TOC), so an opened file costs no
/// per-object memory -- kit #11 made one 40-byte ObjectInfo object per entry (148,226 on the intro walk: 5.7 MiB of GC heap + their lists),
/// alive as long as the file. Every value is read from the record exactly as kit #11's makers copied it (PathId, ByteStart = the pack's
/// payload offset + the record's, ByteSize, the class id, the pack's flags in TypeIndex); the loader's live-record position
/// (<see cref="LivePos"/>) lives in the table (an int per entry, made when the first record of the file is registered).
/// Absent = <see cref="None"/> (<see cref="IsNull"/>), where kit #11 returned null. Two views of the same entry are equal.
/// SHEPHERD_K12_TOC=0 (env file; or SHEPHERD_K12_WALKB=0) = kit #11's memory: every entry made as its own 40-byte object when the file opens
/// (<see cref="ObjectEntry"/>, kit #11's ObjectInfo layout), the views reading those objects.</summary>
public struct ObjectInfo : IEquatable<ObjectInfo>
{
    readonly ObjectTable _t;
    readonly int _i;
    internal ObjectInfo(ObjectTable t, int i) { _t = t; _i = i; }
    /// <summary>No entry (kit #11's null).</summary>
    public static ObjectInfo None => default;
    public bool IsNull => _t == null;
    internal ObjectTable Table => _t;
    internal int Index => _i;
    public long PathId => _t.PathId(_i);
    /// <summary>Absolute offset in the file data (data offset already added); for a pack file: in the pack.</summary>
    public long ByteStart => _t.ByteStart(_i);
    public uint ByteSize => _t.ByteSize(_i);
    public int TypeIndex => _t.TypeIndex(_i);
    public SerializedType Type => _t.Type(_i);
    /// <summary>The object's Unity class id (= its <see cref="Type"/>'s: every maker sets them equal).</summary>
    public int ClassId => _t.ClassId(_i);
    /// <summary>kit6 mem L4c (Shepherd.Engine Assets/Loader.cs): 1 + this object's position in the loader's ordered list of live records, 0
    /// = none (the loader's (file name, path id) map kept on the index entry itself -- in the table since kit #12).</summary>
    public int LivePos { get => _t.GetLive(_i); set => _t.SetLive(_i, value); }
    public bool Equals(ObjectInfo o) => ReferenceEquals(_t, o._t) && _i == o._i;
    public override bool Equals(object obj) => obj is ObjectInfo o && Equals(o);
    public override int GetHashCode() => _t == null ? 0 : (_t.Id * 397) ^ _i;
    public override string ToString() => _t == null ? "(no object)" : $"{ClassIds.Name(ClassId)} #{PathId}";
}

/// <summary>SHEPHERD_K12_TOC=0 (kit #11's memory): one table entry as its own object -- kit #11's ObjectInfo, field for field (40 bytes on the
/// Vita; the build switch SHEPHERD_K11_RECORDS=0 = kit #10's 48-byte layout).</summary>
public sealed class ObjectEntry
{
#if SHEPHERD_K10_RECORDS
    public long PathId;
    public long ByteStart;
    public uint ByteSize;
    public int TypeIndex;
    public int ClassId;
    public SerializedType Type;
#else
    public int TypeIndex;
    public long PathId;
    public long ByteStart;
    public uint ByteSize;
    public SerializedType Type;
#endif
    public int LivePos;
}

/// <summary>A serialized file's object table (kit #12 walkb): <see cref="Count"/> records of 32 bytes in <see cref="Toc"/> from byte
/// <c>first * 32</c> -- the pack TOC's layout (i64 pathId, u64 offset, u32 size, i32 classId, u16 file, u16 flags, u32 0); a loose file's
/// own table is written in the same layout when it is parsed (offset absolute, the type index in the last word). Read-only after it is made,
/// except the live positions.</summary>
public sealed class ObjectTable : IReadOnlyList<ObjectInfo>
{
    /// <summary>The view (kit #12) or kit #11's per-entry objects (SHEPHERD_K12_TOC=0 or SHEPHERD_K12_WALKB=0), for every table made from now on.</summary>
    public static bool View = Environment.GetEnvironmentVariable("SHEPHERD_K12_TOC") != "0" && Environment.GetEnvironmentVariable("SHEPHERD_K12_WALKB") != "0";
    static int s_ids;
    internal readonly int Id = Interlocked.Increment(ref s_ids);
    readonly byte[] _toc;
    readonly int _base;          // byte offset of the first record
    readonly long _bias;         // added to every record's offset (the pack's payload offset; 0 for a loose file)
    readonly bool _loose;        // a loose file: TypeIndex = the last word, Type = the file's type list
    readonly List<SerializedType> _types;           // loose files
    readonly Func<int, SerializedType> _typeOfClass; // pack files: the reader's one SerializedType per class id
    int[] _live;
    readonly ObjectEntry[] _mat; // SHEPHERD_K12_TOC=0
    /// <summary>null when the path ids ascend strictly (binary search over the records), else the entries' indexes ordered by path id, one per
    /// path id -- the LAST entry of a path id, as kit #11's Dictionary&lt;long, ObjectInfo&gt; kept it (no dictionary: no generic code for it in
    /// the AOT image either).</summary>
    readonly int[] _sorted;
    public int Count { get; }
    /// <summary>The records' sizes added up.</summary>
    public long TotalBytes { get; }
    /// <summary>The largest record end (ByteStart + ByteSize).</summary>
    public long MaxEnd { get; }

    ObjectTable(byte[] toc, int first, int count, long bias, bool loose, List<SerializedType> types, Func<int, SerializedType> typeOfClass)
    {
        _toc = toc; _base = checked(first * 32); Count = count; _bias = bias; _loose = loose; _types = types; _typeOfClass = typeOfClass;
        if ((long)_base + (long)count * 32 > toc.Length) throw new InvalidDataException($"object table: {count} records from {first} do not fit a {toc.Length}-byte table");
        long prev = long.MinValue, total = 0, maxEnd = 0;
        bool asc = true;
        for (int i = 0; i < count; i++)
        {
            long p = PathIdRaw(i);
            if (asc && i > 0 && p <= prev) asc = false;
            prev = p;
            uint sz = ByteSizeRaw(i);
            total += sz;
            maxEnd = Math.Max(maxEnd, ByteStartRaw(i) + sz);
        }
        TotalBytes = total; MaxEnd = maxEnd;
        if (!asc) _sorted = SortedIndex();
        if (!View)
        {
            _mat = new ObjectEntry[count];
            for (int i = 0; i < count; i++)
            {
                var e = new ObjectEntry { PathId = PathIdRaw(i), ByteStart = ByteStartRaw(i), ByteSize = ByteSizeRaw(i), TypeIndex = TypeIndexRaw(i) };
                e.Type = TypeRaw(i);
#if SHEPHERD_K10_RECORDS
                e.ClassId = ClassIdRaw(i);
#endif
                _mat[i] = e;
            }
        }
    }

    /// <summary>A pack file's table: the pack TOC's records [first, first + count), offsets relative to <paramref name="payloadOffset"/>.</summary>
    internal static ObjectTable FromPackToc(byte[] toc, int first, int count, long payloadOffset, Func<int, SerializedType> typeOfClass) =>
        new(toc, first, count, payloadOffset, false, null, typeOfClass);

    /// <summary>A loose file's table: <paramref name="count"/> records the parser wrote with <see cref="WriteRow"/> (the TOC layout).</summary>
    internal static ObjectTable FromRows(byte[] rows, int count, List<SerializedType> types) => new(rows, 0, count, 0, true, types, null);
    internal static void WriteRow(byte[] rows, int i, long pathId, long byteStart, uint byteSize, int classId, int typeIndex)
    {
        var e = rows.AsSpan(i * 32, 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(e, pathId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(e[8..], byteStart);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e[16..], byteSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(e[20..], classId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(e[28..], typeIndex);
    }

    /// <summary>The entries' indexes by (path id, index), a stable bottom-up merge sort (no generic code), then one per path id: the last.</summary>
    int[] SortedIndex()
    {
        int n = Count;
        var keys = new long[n]; var a = new int[n]; var b = new int[n];
        for (int i = 0; i < n; i++) { keys[i] = PathIdRaw(i); a[i] = i; }
        for (int w = 1; w < n; w *= 2)
        {
            for (int lo = 0; lo < n; lo += 2 * w)
            {
                int mid = Math.Min(lo + w, n), hi = Math.Min(lo + 2 * w, n), x = lo, y = mid, k = lo;
                while (x < mid && y < hi) b[k++] = keys[a[y]] < keys[a[x]] ? a[y++] : a[x++];   // (ties: the left run first -- stable)
                while (x < mid) b[k++] = a[x++];
                while (y < hi) b[k++] = a[y++];
            }
            var t = a; a = b; b = t;
        }
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (i + 1 < n && keys[a[i + 1]] == keys[a[i]]) continue;   // (a later entry of the same path id follows: it wins, as the dictionary's overwrite)
            a[m++] = a[i];
        }
        if (m < n) Array.Resize(ref a, m);
        return a;
    }

    long PathIdRaw(int i) => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(_toc, _base + i * 32, 8));
    long ByteStartRaw(int i) => _bias + System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(_toc, _base + i * 32 + 8, 8));
    uint ByteSizeRaw(int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_toc, _base + i * 32 + 16, 4));
    int ClassIdRaw(int i) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(_toc, _base + i * 32 + 20, 4));
    int TypeIndexRaw(int i) => _loose ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(_toc, _base + i * 32 + 28, 4))
                                      : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_toc, _base + i * 32 + 26, 2));
    SerializedType TypeRaw(int i) => _loose ? _types[TypeIndexRaw(i)] : _typeOfClass(ClassIdRaw(i));

    internal long PathId(int i) => _mat != null ? _mat[i].PathId : PathIdRaw(i);
    internal long ByteStart(int i) => _mat != null ? _mat[i].ByteStart : ByteStartRaw(i);
    internal uint ByteSize(int i) => _mat != null ? _mat[i].ByteSize : ByteSizeRaw(i);
#if SHEPHERD_K10_RECORDS
    internal int ClassId(int i) => _mat != null ? _mat[i].ClassId : ClassIdRaw(i);
#else
    internal int ClassId(int i) => _mat != null ? _mat[i].Type.ClassId : ClassIdRaw(i);
#endif
    internal int TypeIndex(int i) => _mat != null ? _mat[i].TypeIndex : TypeIndexRaw(i);
    internal SerializedType Type(int i) => _mat != null ? _mat[i].Type : TypeRaw(i);
    internal int GetLive(int i) => _mat != null ? _mat[i].LivePos : _live != null ? _live[i] : 0;
    internal void SetLive(int i, int v)
    {
        if (_mat != null) { _mat[i].LivePos = v; return; }
        if (_live == null) { if (v == 0) return; _live = new int[Count]; }
        _live[i] = v;
    }

    public ObjectInfo this[int index] => (uint)index < (uint)Count ? new ObjectInfo(this, index) : throw new ArgumentOutOfRangeException(nameof(index));
    /// <summary>The entry with this path id, or <see cref="ObjectInfo.None"/>.</summary>
    public ObjectInfo Find(long pathId)
    {
        if (_sorted != null)
        {
            int l = 0, h = _sorted.Length - 1;
            while (l <= h)
            {
                int mid = (l + h) >> 1; long p = PathId(_sorted[mid]);
                if (p == pathId) return new ObjectInfo(this, _sorted[mid]);
                if (p < pathId) l = mid + 1; else h = mid - 1;
            }
            return default;
        }
        int lo = 0, hi = Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1; long p = PathId(mid);
            if (p == pathId) return new ObjectInfo(this, mid);
            if (p < pathId) lo = mid + 1; else hi = mid - 1;
        }
        return default;
    }
    public Enumerator GetEnumerator() => new(this);
    IEnumerator<ObjectInfo> IEnumerable<ObjectInfo>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public struct Enumerator : IEnumerator<ObjectInfo>
    {
        readonly ObjectTable _t; int _i;
        internal Enumerator(ObjectTable t) { _t = t; _i = -1; }
        public ObjectInfo Current => new(_t, _i);
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++_i < _t.Count;
        public void Reset() => _i = -1;
        public void Dispose() { }
    }
}
#endif

public sealed class FileIdentifier
{
    public string PathName;
    public Guid Guid;
    public int Type;
    public string TempEmpty;
    /// <summary>The file name the external refers to (last path segment): "CAB-xxx", "unity default resources", ...</summary>
    public string FileName => PathName == null ? null : PathName[(PathName.LastIndexOf('/') + 1)..];
    public override string ToString() => PathName;
}

public readonly record struct PPtr(int FileId, long PathId)
{
    public bool IsNull => FileId == 0 && PathId == 0;
    public override string ToString() => $"PPtr({FileId}, {PathId})";
}

public readonly record struct ScriptReference(int FileIndex, long PathId);

/// <summary>
/// Unity SerializedFile (format versions 17..22+ are what this is tested with; 22 = Unity 2022.3).
/// Object payloads are sliced out of the whole-file buffer on demand. <see cref="Load"/> keeps that buffer for the file's
/// lifetime; <see cref="LoadLazy"/> parses only the metadata and (re)loads the whole buffer on the first payload access
/// after it was <see cref="Release"/>d (V3 memory diet: whole-file buffers are not kept once nothing reads them).
/// </summary>
public sealed class SerializedFile
{
    private static readonly ConcurrentDictionary<int, List<TypeTreeNode>> s_fallback = new();

    private byte[] _data;                       // whole file (or, for LoadMetadata files, header + metadata); null while released
    private readonly Func<byte[]> _reload;      // LoadLazy only: produces the whole file again
    private readonly Func<long, int, byte[]> _readRange;   // LoadLazy only: reads one object's bytes without the whole file
    private int _sparseReads;
    /// <summary>Lazy files: while the whole buffer is not resident, up to this many single-object reads go straight to the
    /// file/bundle (a handful of reads -- an AssetBundle header, one Resources.Load -- must not pull in a 60 MiB file);
    /// the next one loads the whole buffer. The count restarts after every release.</summary>
    public static int SparseReadLimit = 128;
    public static long SparseReads, SparseBytes;
    private readonly object _dataLock = new();
    private long _length, _maxObjectEnd, _headerFileSize;
    /// <summary>Host-advanced clock (the engine bumps it once per frame); <see cref="LastUse"/> = clock at the last payload access.</summary>
    public static long Clock;
    public long LastUse;
    /// <summary>Whole-file (re)loads of lazy files after the first one, and their bytes (diagnostics).</summary>
    public static int Reloads; public static long ReloadedBytes;
    public int Loads { get; private set; }
    /// <summary>Called after a lazy file's buffer was loaded: (file, bytes, isReload).</summary>
    public static Action<SerializedFile, int, bool> OnLoad;
    private readonly List<SerializedType> _types = new();
#if !SHEPHERD_K11_TOC
    private ObjectTable _table;   // kit #12 walkb: the object table (a view over the pack's TOC, or the parsed table of a loose file)
#else
    private readonly List<ObjectInfo> _objects = new();
    private Dictionary<long, ObjectInfo> _byPathId;   // null when _objects is strictly ascending by PathId (binary search instead)
#endif
    private readonly List<FileIdentifier> _externals = new();
    private readonly List<SerializedType> _refTypes = new();
    private readonly List<ScriptReference> _scriptTypes = new();

    public string Name { get; }
    public string UnityVersion { get; private set; }
    public int TargetPlatform { get; private set; }
    public bool EnableTypeTree { get; private set; }
    public uint FormatVersion { get; private set; }
    public bool BigEndianData { get; private set; }
    public long DataOffset { get; private set; }
    public string UserInformation { get; private set; }
    public IReadOnlyList<SerializedType> Types => _types;
#if !SHEPHERD_K11_TOC
    public IReadOnlyList<ObjectInfo> Objects => _table;
    /// <summary>The file's object with this path id, or <see cref="ObjectInfo.None"/> -- without <see cref="AssetTrace"/>'s lookup hooks (kit #11's
    /// ObjectsByPathId.TryGetValue; kit #12 walkb: the table's own search, no dictionary per file).</summary>
    public ObjectInfo FindQuiet(long pathId) => _table.Find(pathId);
#else
    public IReadOnlyList<ObjectInfo> Objects => _objects;
    public IReadOnlyDictionary<long, ObjectInfo> ObjectsByPathId => _byPathId ??= BuildIndex();
    private Dictionary<long, ObjectInfo> BuildIndex() { var d = new Dictionary<long, ObjectInfo>(_objects.Count); foreach (var o in _objects) d[o.PathId] = o; return d; }
#endif
    public IReadOnlyList<FileIdentifier> Externals => _externals;
    public IReadOnlyList<SerializedType> RefTypes => _refTypes;
    public IReadOnlyList<ScriptReference> ScriptTypes => _scriptTypes;

    /// <summary>Directory searched for loose .resS / .resource streams (loose files only).</summary>
    public string ResSearchDir { get; }
    /// <summary>Bundle the file came from (null for loose files). Set by AssetDatabase.</summary>
    public BundleFile Bundle { get; internal set; }
    /// <summary>Size of the whole file (whether or not its buffer is resident).</summary>
    public int Length => (int)_length;
    /// <summary>Bytes of the whole-file buffer currently held in memory (0 while a lazy file is released).</summary>
    public int ResidentBytes => Volatile.Read(ref _data)?.Length ?? 0;
    public bool IsResident => Volatile.Read(ref _data) != null;
    /// <summary>True when the buffer can be dropped and re-read later (<see cref="LoadLazy"/>).</summary>
    public bool IsReleasable => _reload != null;

    private readonly bool _metadataOnly;

    private SerializedFile(string name, byte[] data, string resSearchDir, bool metadataOnly = false, Func<byte[]> reload = null, Func<long, int, byte[]> readRange = null)
    {
        Name = name; _data = data; ResSearchDir = resSearchDir; _metadataOnly = metadataOnly; _reload = reload; _readRange = readRange;
    }

    /// <summary>V3 memory diet: files kept by an AssetDatabase share identical type trees (one node list per distinct
    /// layout instead of one per file). The lists are never mutated after parsing, so sharing is invisible to readers.</summary>
    public static bool ShareTypeTrees;
    private bool _share;
    /// <summary>Type-tree lists that turned out identical to one already loaded (shared) / distinct lists kept.</summary>
    public static (int shared, int distinct) TypeTreeShareStats => (TypeTreeShare.Shared, TypeTreeShare.Distinct);

    /// <summary>V3 part 2: non-null for a file served from a Vita pack (<see cref="FromPack"/>): no type trees, no buffer; payloads
    /// come from the pack (value trees through <see cref="TypeTreeReader.Read(SerializedFile, ObjectInfo)"/>, raw bytes where
    /// the engine reads raw bytes).</summary>
    public PackReader Pack { get; private set; }
    /// <summary>Index of this file in its pack (-1 for ordinary files).</summary>
    public int PackFileIndex { get; private set; } = -1;

    /// <summary>Pack files: how many objects of a class the SOURCE file had (the pack may hold a subset). Enumerating a class
    /// the pack holds only partly (scene GameObjects, AssetBundle headers) would be a silent gap, so it is reported.</summary>
    public int PackSourceCount(int classId) => _packSourceClasses != null && _packSourceClasses.TryGetValue(classId, out var n) ? n : 0;
    private Dictionary<int, int> _packSourceClasses;

#if !SHEPHERD_K11_TOC
    internal static SerializedFile FromPack(string name, PackReader pack, int index, List<FileIdentifier> externals, ObjectTable objects, Dictionary<int, int> sourceClasses)
#else
    internal static SerializedFile FromPack(string name, PackReader pack, int index, List<FileIdentifier> externals, List<ObjectInfo> objects, Dictionary<int, int> sourceClasses)
#endif
    {
        var f = new SerializedFile(name, null, null, metadataOnly: true) { Pack = pack, PackFileIndex = index, _packSourceClasses = sourceClasses };
        f.FormatVersion = 22; f.EnableTypeTree = false; f.UnityVersion = "pack";
        f._externals.AddRange(externals);
#if !SHEPHERD_K11_TOC
        f._table = objects;
        f._length += objects.TotalBytes;
#else
        f._objects.Capacity = objects.Count;
        foreach (var o in objects)
        {
            if (f._byPathId == null && f._objects.Count > 0 && o.PathId <= f._objects[^1].PathId) f._byPathId = f.BuildIndex();
            f._objects.Add(o);
            if (f._byPathId != null) f._byPathId[o.PathId] = o;
            f._length += o.ByteSize;
        }
#endif
        return f;
    }

    /// <summary>Whether <see cref="TypeTreeReader.Read(SerializedFile, ObjectInfo)"/> can decode this object (own or fallback
    /// type tree; for a pack file: a stored value tree).</summary>
    public bool HasTree(ObjectInfo o) => Pack != null ? PackReader.HasTree(o) : GetTypeTree(o) != null;

    public static SerializedFile Load(string name, byte[] data, string resSearchDir = null)
    {
        var f = new SerializedFile(name, data, resSearchDir);
        f._share = ShareTypeTrees;
        f.Parse();
        f._length = data.Length;
        return f;
    }

    /// <summary>Parse from header + metadata only (<see cref="MetadataLength"/> bytes); the whole file is produced by
    /// <paramref name="loadAll"/> on the first payload access, and again after every <see cref="Release"/>.</summary>
    public static SerializedFile LoadLazy(string name, byte[] headerAndMetadata, long fullLength, Func<byte[]> loadAll, string resSearchDir = null, Func<long, int, byte[]> readRange = null)
    {
        var f = new SerializedFile(name, headerAndMetadata, resSearchDir, metadataOnly: true, reload: loadAll ?? throw new ArgumentNullException(nameof(loadAll)), readRange: readRange);
        f._share = ShareTypeTrees;
        f.Parse();
        f._data = null; f._length = fullLength;
        if (f._headerFileSize > fullLength) throw new InvalidDataException($"{name}: header says {f._headerFileSize} bytes, have {fullLength}");
        if (f._maxObjectEnd > fullLength) throw new InvalidDataException($"{name}: an object extends past end of file");
        return f;
    }

    /// <summary>The whole-file buffer, (re)loading a released lazy file.</summary>
    private byte[] Data()
    {
        LastUse = Clock;
        var d = Volatile.Read(ref _data);
        if (d != null) return d;
        if (_reload == null) return _data;   // metadata-only file: payload reads fail as before
        lock (_dataLock)
        {
            d = _data;
            if (d != null) return d;
            d = _reload();
            if (d.Length < _length) throw new InvalidDataException($"{Name}: reload produced {d.Length} bytes, expected {_length}");
            bool again = Loads++ > 0;
            if (again) { Interlocked.Increment(ref Reloads); Interlocked.Add(ref ReloadedBytes, d.Length); }
            Volatile.Write(ref _data, d);
        }
        OnLoad?.Invoke(this, d.Length, Loads > 1);
        return d;
    }

    /// <summary>Drop the whole-file buffer of a lazy file (it is re-read on the next payload access). Spans handed out
    /// earlier stay valid (they keep their array alive). Returns the bytes released.</summary>
    public int Release()
    {
        if (_reload == null) return 0;
        lock (_dataLock)
        {
            var d = _data; if (d == null) return 0;
            _data = null; _sparseReads = 0; return d.Length;
        }
    }

    /// <summary>Bytes needed to parse the metadata (header + types + object table + externals), from the first 48 bytes.</summary>
    public static long MetadataLength(ReadOnlySpan<byte> header48)
    {
        var r = new SpanReader(header48, bigEndian: true);
        uint metadataSize = r.U32(); r.U32(); uint version = r.U32(); r.U32(); r.U8(); r.Bytes(3);
        if (version >= 22) { metadataSize = r.U32(); return 48 + metadataSize; }
        return 20 + metadataSize;
    }

    /// <summary>Parse only the metadata (types, object table, externals); object payloads are unavailable.</summary>
    public static SerializedFile LoadMetadata(string name, byte[] headerAndMetadata)
    {
        var f = new SerializedFile(name, headerAndMetadata, null, metadataOnly: true);
        f.Parse();
        f._length = headerAndMetadata.Length;
        return f;
    }

    private static readonly ConcurrentDictionary<string, List<TypeTreeNode>> s_fallbackByHash = new();

    public static void RegisterFallbackTypeTree(int classId, List<TypeTreeNode> nodes) => s_fallback[classId] = nodes;

    /// <summary>Exact-layout fallback, used before the per-class one: for a stripped type whose OldTypeHash matches
    /// (e.g. a MonoBehaviour script whose full type tree was found in some bundle).</summary>
    public static void RegisterFallbackTypeTree(int classId, string oldTypeHashHex, List<TypeTreeNode> nodes) =>
        s_fallbackByHash[$"{classId}:{oldTypeHashHex}"] = nodes;

    public static bool HasFallbackTypeTree(int classId, string oldTypeHashHex) =>
        oldTypeHashHex != null && s_fallbackByHash.ContainsKey($"{classId}:{oldTypeHashHex}");
    public static bool HasFallbackTypeTree(int classId) => s_fallback.ContainsKey(classId);
    public static IReadOnlyCollection<int> FallbackClassIds => s_fallback.Keys.ToArray();

    /// <summary>Type tree for an object: its own, or the registered fallback for its class when stripped.</summary>
    /// <summary>Called once, the first time an object of this (stripped) file asks for a type tree
    /// (AssetDatabase uses it to harvest fallbacks from bundles lazily).</summary>
    public Action<SerializedFile> MissingTypeTreeHandler { get; set; }
    private int _missingHandled;

    public List<TypeTreeNode> GetTypeTree(ObjectInfo o)
    {
        if (Pack != null) return null;   // a pack stores value trees, not type trees (HasTree / TypeTreeReader.Read handle it)
        if (o.Type?.Nodes is { Count: > 0 } n) return n;
        // first stripped lookup: let the handler register this file's fallbacks (incl. exact-hash script layouts)
        if (MissingTypeTreeHandler != null && Interlocked.Exchange(ref _missingHandled, 1) == 0) MissingTypeTreeHandler(this);
        return LookupFallback(o);
    }

    private static List<TypeTreeNode> LookupFallback(ObjectInfo o)
    {
        if (o.Type?.OldTypeHash != null && !s_fallbackByHash.IsEmpty &&
            s_fallbackByHash.TryGetValue($"{o.ClassId}:{o.Type.OldTypeHashHex}", out var exact)) return exact;
        return s_fallback.TryGetValue(o.ClassId, out var fb) ? fb : null;
    }

    public ReadOnlySpan<byte> GetObjectBytes(ObjectInfo o) { var d = ObjectData(o, out int off); return d.AsSpan(off, Pack != null ? d.Length : (int)o.ByteSize); }
    public ReadOnlyMemory<byte> GetObjectMemory(ObjectInfo o) { var d = ObjectData(o, out int off); return d.AsMemory(off, Pack != null ? d.Length : (int)o.ByteSize); }

    /// <summary>The array holding an object's bytes and their offset in it: the whole-file buffer, or (sparse access to a
    /// released lazy file) a fresh array with just this object.</summary>
    private byte[] ObjectData(ObjectInfo o, out int offset)
    {
        AssetTrace.ObjectRead?.Invoke(this, o, AssetTrace.Current);
        if (Pack != null) { LastUse = Clock; offset = 0; return Pack.ReadRaw(this, o); }
        if (_reload != null && _readRange != null && Volatile.Read(ref _data) == null && Interlocked.Increment(ref _sparseReads) <= SparseReadLimit)
        {
            LastUse = Clock;
            var b = _readRange(o.ByteStart, (int)o.ByteSize);
            Interlocked.Increment(ref SparseReads); Interlocked.Add(ref SparseBytes, b.Length);
            offset = 0; return b;
        }
        offset = (int)o.ByteStart; return Data();
    }

#if !SHEPHERD_K11_TOC
    /// <summary>The file's object with this path id, or <see cref="ObjectInfo.None"/> (<see cref="ObjectInfo.IsNull"/>; kit #11: null).</summary>
    public ObjectInfo GetObject(long pathId)
    {
        var o = _table.Find(pathId);
        if (AssetTrace.ObjectLookup != null && !o.IsNull) AssetTrace.ObjectLookup(this, o);
        if (AssetTrace.ObjectAbsent != null && o.IsNull) AssetTrace.ObjectAbsent(this, pathId);
        return o;
    }
#else
    public ObjectInfo GetObject(long pathId)
    {
        var o = Find(pathId);
        if (AssetTrace.ObjectLookup != null && o != null) AssetTrace.ObjectLookup(this, o);
        if (AssetTrace.ObjectAbsent != null && o == null) AssetTrace.ObjectAbsent(this, pathId);
        return o;
    }

    private ObjectInfo Find(long pathId)
    {
        if (_byPathId != null) return _byPathId.TryGetValue(pathId, out var o) ? o : null;
        int lo = 0, hi = _objects.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1; long p = _objects[mid].PathId;
            if (p == pathId) return _objects[mid];
            if (p < pathId) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }
#endif

    public IEnumerable<ObjectInfo> ObjectsOfClass(int classId)
    {
        if (Pack != null)
        {
#if !SHEPHERD_K11_TOC
            var l = new List<ObjectInfo>(); foreach (var o in _table) if (o.ClassId == classId) l.Add(o);
#else
            var l = new List<ObjectInfo>(); foreach (var o in _objects) if (o.ClassId == classId) l.Add(o);
#endif
            Pack.CheckEnumeration(this, classId, l.Count);
            return l;
        }
        return Iterate(classId);
    }
    private IEnumerable<ObjectInfo> Iterate(int classId)
    {
#if !SHEPHERD_K11_TOC
        foreach (var o in _table) if (o.ClassId == classId) yield return o;
#else
        foreach (var o in _objects) if (o.ClassId == classId) yield return o;
#endif
    }

    private void Parse()
    {
        var r = new SpanReader(_data, bigEndian: true);
        uint metadataSize = r.U32();
        long fileSize = r.U32();
        FormatVersion = r.U32();
        DataOffset = r.U32();
        if (FormatVersion < 9) throw new NotSupportedException($"{Name}: serialized file version {FormatVersion} too old");
        if (FormatVersion > 0x100 || metadataSize == 0 && FormatVersion < 22)
            throw new InvalidDataException($"{Name}: not a serialized file (version {FormatVersion})");
        bool big = r.U8() != 0;
        r.Bytes(3);
        if (FormatVersion >= 22)
        {
            metadataSize = r.U32();
            fileSize = r.I64();
            DataOffset = r.I64();
            r.I64(); // unknown / reserved
        }
        BigEndianData = big;
        _headerFileSize = fileSize;
        if (!_metadataOnly && fileSize > _data.Length) throw new InvalidDataException($"{Name}: header says {fileSize} bytes, have {_data.Length}");
        r.BigEndian = big;

        UnityVersion = r.CString();
        TargetPlatform = r.I32();
        EnableTypeTree = FormatVersion >= 13 ? r.Bool() : true;

        int typeCount = r.I32();
        for (int i = 0; i < typeCount; i++) _types.Add(ReadType(ref r, isRefType: false));

        int objectCount = r.I32();
#if !SHEPHERD_K11_TOC
        var rows = new byte[checked(objectCount * 32)];   // (kit #12 walkb: written straight into the table's layout)
#else
        _objects.Capacity = objectCount;
#endif
        for (int i = 0; i < objectCount; i++)
        {
#if !SHEPHERD_K11_TOC
            long pathId;
            if (FormatVersion >= 14) { r.Align(4); pathId = r.I64(); }
            else pathId = r.I32();
            long byteStart = (FormatVersion >= 22 ? r.I64() : r.U32()) + DataOffset;
            uint byteSize = r.U32();
            int typeIndex = r.I32();
#else
            var o = new ObjectInfo();
            if (FormatVersion >= 14) { r.Align(4); o.PathId = r.I64(); }
            else o.PathId = r.I32();
            o.ByteStart = (FormatVersion >= 22 ? r.I64() : r.U32()) + DataOffset;
            o.ByteSize = r.U32();
            o.TypeIndex = r.I32();
#endif
            if (FormatVersion < 17) throw new NotSupportedException($"{Name}: pre-2017 serialized files not supported");
#if !SHEPHERD_K11_TOC
            int classId = _types[typeIndex].ClassId;   // (kit #11: o.Type = _types[o.TypeIndex] -- an index out of range fails here, as it did)
            if (!_metadataOnly && byteStart + byteSize > _data.Length)
                throw new InvalidDataException($"{Name}: object {pathId} extends past end of file");
            _maxObjectEnd = Math.Max(_maxObjectEnd, byteStart + byteSize);
            ObjectTable.WriteRow(rows, i, pathId, byteStart, byteSize, classId, typeIndex);
#else
            o.Type = _types[o.TypeIndex];   // (ClassId = Type.ClassId: kit #11 walk)
#if SHEPHERD_K10_RECORDS
            o.ClassId = o.Type.ClassId;
#endif
            if (!_metadataOnly && o.ByteStart + o.ByteSize > _data.Length)
                throw new InvalidDataException($"{Name}: object {o.PathId} extends past end of file");
            _maxObjectEnd = Math.Max(_maxObjectEnd, o.ByteStart + o.ByteSize);
            if (_byPathId == null && _objects.Count > 0 && o.PathId <= _objects[^1].PathId) _byPathId = BuildIndex();   // not strictly ascending: hash index
            _objects.Add(o);
            if (_byPathId != null) _byPathId[o.PathId] = o;
#endif
        }
#if !SHEPHERD_K11_TOC
        _table = ObjectTable.FromRows(rows, objectCount, _types);
#endif

        if (FormatVersion >= 11)
        {
            int scriptCount = r.I32();
            for (int i = 0; i < scriptCount; i++)
            {
                int fileIndex = r.I32();
                long id;
                if (FormatVersion >= 14) { r.Align(4); id = r.I64(); } else id = r.I32();
                _scriptTypes.Add(new ScriptReference(fileIndex, id));
            }
        }

        int extCount = r.I32();
        for (int i = 0; i < extCount; i++)
        {
            var e = new FileIdentifier();
            if (FormatVersion >= 6) e.TempEmpty = r.CString();
            if (FormatVersion >= 5)
            {
                e.Guid = new Guid(r.Bytes(16));
                e.Type = r.I32();
            }
            e.PathName = r.CString();
            _externals.Add(e);
        }

        if (FormatVersion >= 20)
        {
            int refCount = r.I32();
            for (int i = 0; i < refCount; i++) _refTypes.Add(ReadType(ref r, isRefType: true));
        }
        if (FormatVersion >= 5) UserInformation = r.CString();
    }

    private SerializedType ReadType(ref SpanReader r, bool isRefType)
    {
        var t = new SerializedType { ClassId = r.I32() };
        if (FormatVersion >= 16) t.IsStripped = r.Bool();
        if (FormatVersion >= 17) t.ScriptTypeIndex = r.I16();
        if (FormatVersion >= 13)
        {
            if ((isRefType && t.ScriptTypeIndex >= 0) ||
                (FormatVersion < 16 && t.ClassId < 0) ||
                (FormatVersion >= 16 && t.ClassId == 114))
                t.ScriptId = r.Bytes(16).ToArray();
            t.OldTypeHash = r.Bytes(16).ToArray();
        }
        if (EnableTypeTree)
        {
            if (FormatVersion < 12 && FormatVersion != 10) throw new NotSupportedException("legacy type tree format");
            t.Nodes = ReadTypeTreeBlob(ref r);
            if (!isRefType && t.Nodes.Count > 0) ClassIds.Learn(t.ClassId, t.Nodes[0].Type);
            if (FormatVersion >= 21)
            {
                if (isRefType)
                {
                    t.KlassName = r.CString();
                    t.NameSpace = r.CString();
                    t.AsmName = r.CString();
                }
                else
                {
                    int n = r.I32();
                    t.TypeDependencies = new int[n];
                    for (int i = 0; i < n; i++) t.TypeDependencies[i] = r.I32();
                }
            }
        }
        return t;
    }

    private List<TypeTreeNode> ReadTypeTreeBlob(ref SpanReader r)
    {
        int nodeCount = r.I32();
        int stringSize = r.I32();
        if (nodeCount < 0 || nodeCount > 1_000_000 || stringSize < 0) throw new InvalidDataException($"{Name}: bad type tree header");
        var nodes = new List<TypeTreeNode>(nodeCount);
        var typeOffs = new uint[nodeCount];
        var nameOffs = new uint[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            var n = new TypeTreeNode
            {
                Version = r.U16(),
                Level = r.U8(),
                TypeFlags = r.U8(),
            };
            typeOffs[i] = r.U32();
            nameOffs[i] = r.U32();
            n.ByteSize = r.I32();
            n.Index = r.I32();
            n.MetaFlag = r.I32();
            if (FormatVersion >= 19) n.RefTypeHash = r.U64();
            nodes.Add(n);
        }
        var strings = r.Bytes(stringSize);
        for (int i = 0; i < nodeCount; i++)
        {
            nodes[i].Type = TypeTreeStrings.Get(strings, typeOffs[i]);
            nodes[i].Name = TypeTreeStrings.Get(strings, nameOffs[i]);
        }
        return _share ? TypeTreeShare.Share(nodes) : nodes;
    }

#if !SHEPHERD_K11_TOC
    public override string ToString() => $"{Name} (v{FormatVersion}, {UnityVersion}, {_table?.Count ?? 0} objects, typetree={EnableTypeTree})";
#else
    public override string ToString() => $"{Name} (v{FormatVersion}, {UnityVersion}, {_objects.Count} objects, typetree={EnableTypeTree})";
#endif
}

/// <summary>Process-wide pool of distinct type trees (see <see cref="SerializedFile.ShareTypeTrees"/>). Weak: the pool
/// never keeps a tree alive by itself (the files using it do).</summary>
internal static class TypeTreeShare
{
    private static readonly Dictionary<int, List<WeakReference<List<TypeTreeNode>>>> s_pool = new();
    public static int Shared, Distinct;

    public static List<TypeTreeNode> Share(List<TypeTreeNode> nodes)
    {
        var h = new HashCode(); h.Add(nodes.Count);
        foreach (var n in nodes) { h.Add(n.Level); h.Add(n.Type); h.Add(n.Name); h.Add(n.ByteSize); h.Add(n.Index); h.Add(n.MetaFlag); h.Add(n.Version); h.Add(n.TypeFlags); h.Add(n.RefTypeHash); }
        int key = h.ToHashCode();
        lock (s_pool)
        {
            if (!s_pool.TryGetValue(key, out var bucket)) s_pool[key] = bucket = new List<WeakReference<List<TypeTreeNode>>>(1);
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                if (!bucket[i].TryGetTarget(out var c)) { bucket.RemoveAt(i); continue; }
                if (Same(c, nodes)) { Shared++; return c; }
            }
            bucket.Add(new WeakReference<List<TypeTreeNode>>(nodes)); Distinct++;
            return nodes;
        }
    }

    private static bool Same(List<TypeTreeNode> a, List<TypeTreeNode> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            TypeTreeNode x = a[i], y = b[i];
            if (x.Level != y.Level || x.ByteSize != y.ByteSize || x.Index != y.Index || x.MetaFlag != y.MetaFlag || x.Version != y.Version ||
                x.TypeFlags != y.TypeFlags || x.RefTypeHash != y.RefTypeHash || !string.Equals(x.Type, y.Type, StringComparison.Ordinal) ||
                !string.Equals(x.Name, y.Name, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}

/// <summary>Type tree string resolution (local buffer or Unity's common string table).</summary>
internal static class TypeTreeStrings
{
    private static readonly Dictionary<uint, string> s_common = BuildCommon();
    private static readonly ConcurrentDictionary<string, string> s_intern = new();

    public static string Get(ReadOnlySpan<byte> local, uint offset)
    {
        if ((offset & 0x80000000) == 0)
        {
            if (offset >= local.Length) return $"<bad:{offset}>";
            var s = local.Slice((int)offset);
            int end = s.IndexOf((byte)0);
            if (end < 0) end = s.Length;
            var str = Encoding.UTF8.GetString(s.Slice(0, end));
            return s_intern.GetOrAdd(str, str);
        }
        uint o = offset & 0x7FFFFFFF;
        return s_common.TryGetValue(o, out var c) ? c : $"<common:{o}>";
    }

    // Unity's CommonString table (2019.1+ list; offsets are computed from the concatenated, NUL-separated list).
    private const string Common =
        "AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA\0Component\0data\0deque\0double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0GameObject\0Generic Mono\0GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0map\0Matrix4x4f\0MdFour\0MonoBehaviour\0MonoScript\0m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0m_Enabled\0m_ExtensionPtr\0m_GameObject\0m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_Name\0m_ObjectHideFlags\0m_PrefabInternal\0m_PrefabParentObject\0m_Script\0m_StaticEditorFlags\0m_Type\0m_Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0PPtr<Material>\0PPtr<MonoBehaviour>\0PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0Rectf\0RectInt\0RectOffset\0second\0set\0short\0size\0SInt16\0SInt32\0SInt64\0SInt8\0staticvector\0string\0TextAsset\0TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0UInt64\0UInt8\0unsigned int\0unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0Vector4f\0m_ScriptingClassIdentifier\0Gradient\0Type*\0int2_storage\0int3_storage\0BoundsInt\0m_CorrespondingSourceObject\0m_PrefabInstance\0m_PrefabAsset\0FileSize\0Hash128\0RenderingLayerMask\0";

    private static Dictionary<uint, string> BuildCommon()
    {
        var d = new Dictionary<uint, string>();
        uint off = 0;
        foreach (var s in Common.Split('\0'))
        {
            if (s.Length == 0) continue;
            d[off] = s;
            off += (uint)s.Length + 1;
        }
        return d;
    }
}
