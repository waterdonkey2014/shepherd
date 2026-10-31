using System.Buffers.Binary;
using System.Text;
using Shepherd.Assets;
using K = Shepherd.Assets.VitaPackFormat.K;

namespace Shepherd.VitaPack;

/// <summary>Encodes the value trees (schema-coded, see VitaPackFormat) and writes shepherd.pack.</summary>
sealed class PackWriter
{
    readonly Baker _b;
    readonly Dictionary<string, int> _strId = new(StringComparer.Ordinal); readonly List<string> _strs = new();
    readonly Dictionary<string, int> _shapeId = new(StringComparer.Ordinal); readonly List<(int type, (int key, K kind)[] fields)> _shapes = new();
    readonly HashSet<string> _shared = new(StringComparer.Ordinal);
    public long TreeBytes, RawBytes, ImageBytes, RecordBytes, TocBytes, FileBytes, HeaderBytes;
    public int SharedStrings, InlineStrings; public long SharedRefs;
    public readonly Dictionary<string, (int n, long tree, long raw, long image)> ByCategory = new();

    public PackWriter(Baker b) { _b = b; }

    int Str(string s)
    {
        s ??= "";
        if (_strId.TryGetValue(s, out var i)) return i;
        _strId[s] = i = _strs.Count; _strs.Add(s); return i;
    }

    // ------------------------------------------------------------------ string sharing (values that repeat across trees)

    void CountStrings(object v, Dictionary<string, int> n)
    {
        switch (v)
        {
            case string s: n[s] = n.GetValueOrDefault(s) + 1; break;
            case TypeTreeObject t: foreach (var x in t.Values) CountStrings(x, n); break;
            case List<object> l: foreach (var x in l) CountStrings(x, n); break;
        }
    }

    // ------------------------------------------------------------------ value encoder

    sealed class Buf
    {
        public byte[] B = new byte[256]; public int N;
        void Need(int k) { if (N + k > B.Length) Array.Resize(ref B, Math.Max(B.Length * 2, N + k)); }
        public void U8(byte v) { Need(1); B[N++] = v; }
        public void Var(uint v) { Need(5); while (v >= 0x80) { B[N++] = (byte)(v | 0x80); v >>= 7; } B[N++] = (byte)v; }
        public void Raw(ReadOnlySpan<byte> s) { Need(s.Length); s.CopyTo(B.AsSpan(N)); N += s.Length; }
        public Span<byte> Take(int k) { Need(k); var s = B.AsSpan(N, k); N += k; return s; }
    }

    int Shape(TypeTreeObject t, K[] kinds)
    {
        var sb = new StringBuilder(t.TypeName).Append('\u0001');
        for (int i = 0; i < t.Count; i++) sb.Append(t.Keys[i]).Append('\u0002').Append((char)('A' + (int)kinds[i]));
        var key = sb.ToString();
        if (_shapeId.TryGetValue(key, out var id)) return id;
        var fields = new (int, K)[t.Count];
        for (int i = 0; i < t.Count; i++) fields[i] = (Str(t.Keys[i]), kinds[i]);
        _shapeId[key] = id = _shapes.Count; _shapes.Add((Str(t.TypeName), fields));
        return id;
    }

    void Value(Buf b, object v) { var k = VitaPackFormat.KindOf(v); b.U8((byte)k); Body(b, v, k); }

    void Body(Buf b, object v, K k)
    {
        switch (k)
        {
            case K.Null: break;
            case K.Bool: b.U8((bool)v ? (byte)1 : (byte)0); break;
            case K.I8: b.U8((byte)(sbyte)v); break;
            case K.U8: b.U8((byte)v); break;
            case K.Char: { var c = (char)v; if (c > 0xFF) throw new InvalidDataException("char value above 255"); b.U8((byte)c); break; }
            case K.I16: BinaryPrimitives.WriteInt16LittleEndian(b.Take(2), (short)v); break;
            case K.U16: BinaryPrimitives.WriteUInt16LittleEndian(b.Take(2), (ushort)v); break;
            case K.I32: BinaryPrimitives.WriteInt32LittleEndian(b.Take(4), (int)v); break;
            case K.U32: BinaryPrimitives.WriteUInt32LittleEndian(b.Take(4), (uint)v); break;
            case K.I64: BinaryPrimitives.WriteInt64LittleEndian(b.Take(8), (long)v); break;
            case K.U64: BinaryPrimitives.WriteUInt64LittleEndian(b.Take(8), (ulong)v); break;
            case K.F32: BinaryPrimitives.WriteSingleLittleEndian(b.Take(4), (float)v); break;
            case K.F64: BinaryPrimitives.WriteDoubleLittleEndian(b.Take(8), (double)v); break;
            case K.Str:
            {
                var s = (string)v;
                if (_shared.Contains(s)) { b.Var(((uint)Str(s) << 1) | 1); SharedRefs++; }
                else { var u = Encoding.UTF8.GetBytes(s); b.Var((uint)u.Length << 1); b.Raw(u); InlineStrings++; }
                break;
            }
            case K.PPtr: { var p = (PPtr)v; BinaryPrimitives.WriteInt32LittleEndian(b.Take(4), p.FileId); BinaryPrimitives.WriteInt64LittleEndian(b.Take(8), p.PathId); break; }
            case K.Bytes: { var a = (byte[])v; b.Var((uint)a.Length); b.Raw(a); break; }
            case K.Struct:
            {
                var t = (TypeTreeObject)v; var kinds = new K[t.Count];
                for (int i = 0; i < t.Count; i++) kinds[i] = VitaPackFormat.KindOf(t.Values[i]);
                b.Var((uint)Shape(t, kinds));
                for (int i = 0; i < t.Count; i++) Body(b, t.Values[i], kinds[i]);
                break;
            }
            case K.List:
            {
                var l = (List<object>)v; b.Var((uint)l.Count);
                K ek = l.Count == 0 ? K.Null : VitaPackFormat.KindOf(l[0]);
                for (int i = 1; i < l.Count && ek != K.Any; i++) if (VitaPackFormat.KindOf(l[i]) != ek) ek = K.Any;
                b.U8((byte)ek);
                foreach (var e in l) if (ek == K.Any) Value(b, e); else Body(b, e, ek);
                break;
            }
            default: throw new InvalidDataException($"cannot encode kind {k}");
        }
    }

    public byte[] EncodeTree(TypeTreeObject t) { var b = new Buf(); Value(b, t); return b.B.AsSpan(0, b.N).ToArray(); }

    // ------------------------------------------------------------------ file

    public void Write(string path)
    {
        var entries = _b.Entries;
        // 1) shared value strings: repeated (>= 2 uses), short
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries) if (e.Tree != null) CountStrings(e.Tree, counts);
        foreach (var kv in counts) if (kv.Value >= 2 && kv.Key.Length <= 256) { _shared.Add(kv.Key); Str(kv.Key); }
        SharedStrings = _shared.Count;
        // 2) records
        foreach (var e in entries) if (e.Tree != null) e.TreeBytes = EncodeTree(e.Tree);
        using var fs = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);   // (Read: the payload hash reads it back)
        fs.Write(new byte[VitaPackFormat.HeaderSize]);
        long payload = fs.Position;
        // image offsets are known once the records are laid out
        long recEnd = 0;
        foreach (var e in entries)
        {
            e.Flags = 0;
            if (e.TreeBytes != null) e.Flags |= VitaPackFormat.ObjTree;
            if (e.Raw != null) e.Flags |= VitaPackFormat.ObjRaw;
            if (e.Script != null) e.Flags |= VitaPackFormat.ObjScript;
            if (e.Tex?.Out != null) e.Flags |= VitaPackFormat.ObjImage;
            e.Size = 12 + (e.Script != null ? 12 : 0) + (e.TreeBytes?.Length ?? 0) + (e.Raw?.Length ?? 0) + (e.Tex?.Out != null ? 28 : 0);
            e.Offset = recEnd; recEnd += e.Size;
        }
        long imgAt = recEnd; var imageOffsets = new Dictionary<Entry, long>();
        foreach (var e in entries) if (e.Tex?.Out != null) { imageOffsets[e] = imgAt; imgAt += e.Tex.Out.Length; }
        var hdr = new byte[24];
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteInt32LittleEndian(hdr, e.TreeBytes?.Length ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(4), e.Raw?.Length ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(8), e.Tex?.Out != null ? 28 : 0);
            fs.Write(hdr, 0, 12);
            if (e.Script is { } sp) { BinaryPrimitives.WriteInt32LittleEndian(hdr, sp.FileId); BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(4), sp.PathId); fs.Write(hdr, 0, 12); }
            if (e.TreeBytes != null) fs.Write(e.TreeBytes);
            if (e.Raw != null) fs.Write(e.Raw);
            if (e.Tex?.Out != null)
            {
                var d = new byte[28]; var t = e.Tex;
                BinaryPrimitives.WriteInt32LittleEndian(d, t.OutFormat); BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), t.OutW); BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), t.OutH);
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), t.TopDown ? VitaPackFormat.ImageTopDown : 0); BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(16), t.Out.Length);
                BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(20), imageOffsets[e]);
                fs.Write(d);
            }
            TreeBytes += e.TreeBytes?.Length ?? 0; RawBytes += e.Raw?.Length ?? 0; RecordBytes += e.Size;
            var c = ByCategory.GetValueOrDefault(e.Category); ByCategory[e.Category] = (c.n + 1, c.tree + e.Size - (e.Raw?.Length ?? 0), c.raw + (e.Raw?.Length ?? 0), c.image + (e.Tex?.Out?.Length ?? 0));
        }
        if (fs.Position - payload != recEnd) throw new InvalidDataException("record layout mismatch");
        foreach (var e in entries) if (e.Tex?.Out != null) { fs.Write(e.Tex.Out); ImageBytes += e.Tex.Out.Length; }
        long payloadSize = fs.Position - payload;
        // kit #10 io review: the payload's hash as a TOC property (Shepherd.Assets VitaPack2Format.PayloadHashProperty) -- the TOC's checksum,
        // which the engine compares when it picks a shepherd.pk2 made from this file, then covers the payload's bytes too (a re-bake that keeps
        // the TOC's layout is another pack). --no-payload-hash = the bake as before kit #10.
        if (!Program.NoPayloadHash)
        {
            fs.Flush();
            _b.Properties[VitaPack2Format.PayloadHashProperty] = Repack.PayloadHashOfFile(path + ".tmp", payload, payloadSize);
        }
        // 3) TOC
        var toc = Toc();
        long tocOff = fs.Position; fs.Write(toc); TocBytes = toc.Length;
        var h = new byte[VitaPackFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h, VitaPackFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), VitaPackFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), VitaPackFormat.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), VitaPackFormat.FlagTopDownImages | (_b.Properties.Count > 0 ? VitaPackFormat.FlagProperties : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), VitaPackFormat.Fnv1a(toc));
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(16), tocOff); BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), toc.Length);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), payload); BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), payloadSize);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(48), entries.Count); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(52), _files.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(56), _bundles.Count); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(60), _strs.Count);
        fs.Position = 0; fs.Write(h);
        FileBytes = fs.Length; HeaderBytes = h.Length;
        fs.Close();
        File.Move(path + ".tmp", path, overwrite: true);
    }

    readonly List<SerializedFile> _files = new(); readonly List<(string name, List<int> files)> _bundles = new();

    byte[] Toc()
    {
        var db = _b.Db;
        // files: every file with an object in the pack, plus every serialized file of every bundle that has one
        var fileIdx = new Dictionary<SerializedFile, int>();
        void AddFile(SerializedFile f) { if (!fileIdx.ContainsKey(f)) { fileIdx[f] = _files.Count; _files.Add(f); } }
        foreach (var f in _b.FileOrder) AddFile(f);
        var bundleIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void AddBundle(string bpath)
        {
            var name = Path.GetFileName(bpath); if (bundleIdx.ContainsKey(name)) return;
            var files = db.LoadBundleFiles(bpath);
            foreach (var f in files) AddFile(f);
            bundleIdx[name] = _bundles.Count; _bundles.Add((name, files.Select(f => fileIdx[f]).ToList()));
        }
        foreach (var b in _b.Rec.BundleOrder) AddBundle(Path.Combine(db.BundleDir, b));
        foreach (var f in _b.FileOrder.ToList()) if (f.Bundle != null) AddBundle(f.Bundle.Path);
        var byFile = _b.Entries.GroupBy(e => e.File).ToDictionary(g => g.Key, g => g.ToList());
        var t = new Buf();
        void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(t.Take(4), v);
        void U16(int v) => BinaryPrimitives.WriteUInt16LittleEndian(t.Take(2), checked((ushort)v));
        // intern every name before the string section is written
        foreach (var f in _files) { Str(f.Name); foreach (var x in f.Externals) Str(x.PathName); }
        foreach (var b in _bundles) Str(b.name);
        foreach (var a in _b.AbsentFiles) Str(a);
        foreach (var (k, v) in _b.Properties) { Str(k); Str(v); }
        // strings
        var blobs = _strs.Select(s => Encoding.UTF8.GetBytes(s)).ToList();
        I32(_strs.Count); int ofs = 0; I32(0); foreach (var bl in blobs) { ofs += bl.Length; I32(ofs); }
        foreach (var bl in blobs) t.Raw(bl);
        // shapes
        I32(_shapes.Count);
        foreach (var (type, fields) in _shapes) { I32(type); U16(fields.Length); foreach (var (key, kind) in fields) { I32(key); t.U8((byte)kind); } }
        // bundles
        I32(_bundles.Count);
        foreach (var (name, files) in _bundles) { I32(Str(name)); U16(files.Count); foreach (var fi in files) I32(fi); }
        // files + objects (each file's objects in the file's own table order)
        var objOrder = new List<Entry>();
        I32(_files.Count);
        foreach (var f in _files)
        {
            I32(Str(f.Name));
            int bi = f.Bundle != null && bundleIdx.TryGetValue(Path.GetFileName(f.Bundle.Path), out var x) ? x : -1;
            I32(bi); U16(f.Bundle == null ? VitaPackFormat.FileLoose : 0);
            U16(f.Externals.Count); foreach (var e in f.Externals) I32(Str(e.PathName));
            var mine = byFile.GetValueOrDefault(f) ?? new List<Entry>();
            var pos = new Dictionary<long, int>(); for (int i = 0; i < f.Objects.Count; i++) pos[f.Objects[i].PathId] = i;
            mine.Sort((a, b) => pos[a.Obj.PathId].CompareTo(pos[b.Obj.PathId]));
            I32(objOrder.Count); I32(mine.Count); objOrder.AddRange(mine);
            var hist = f.Objects.GroupBy(o => o.ClassId).OrderBy(g => g.Key).ToList();
            U16(hist.Count); foreach (var g in hist) { I32(g.Key); I32(g.Count()); }
        }
        I32(_b.AbsentFiles.Count); foreach (var a in _b.AbsentFiles) I32(Str(a));
        I32(objOrder.Count);
        foreach (var e in objOrder)
        {
            var s = t.Take(32);
            BinaryPrimitives.WriteInt64LittleEndian(s, e.Obj.PathId); BinaryPrimitives.WriteInt64LittleEndian(s[8..], e.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)e.Size); BinaryPrimitives.WriteInt32LittleEndian(s[20..], e.Obj.ClassId);
            BinaryPrimitives.WriteUInt16LittleEndian(s[24..], checked((ushort)fileIdx[e.File])); BinaryPrimitives.WriteUInt16LittleEndian(s[26..], e.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 0);
        }
        var absent = _b.AbsentObjects.Where(a => _files.Any(f => string.Equals(f.Name, a.Item1, StringComparison.OrdinalIgnoreCase))).ToList();
        I32(absent.Count);
        foreach (var (fname, pid) in absent) { I32(_files.FindIndex(f => string.Equals(f.Name, fname, StringComparison.OrdinalIgnoreCase))); BinaryPrimitives.WriteInt64LittleEndian(t.Take(8), pid); }
        if (_b.Properties.Count > 0) { I32(_b.Properties.Count); foreach (var (k, v) in _b.Properties) { I32(Str(k)); I32(Str(v)); } }
        // bytes of the string section and objects were appended in order; done
        StringBytes = blobs.Sum(b => (long)b.Length); ShapeCount = _shapes.Count;
        return t.B.AsSpan(0, t.N).ToArray();
    }
    public long StringBytes; public int ShapeCount;
    public int Files => _files.Count; public int Bundles => _bundles.Count;
}
