// Device kit #12, track walkb (vita/notes/kit12-walkb.md): the assets' index entries as a VIEW over the object table (ObjectInfo = table +
// index; Shepherd.Assets ObjectTable). Four checks, each exits 1 on any wrong answer:
//   synthetic        random tables through the internal makers (reflection), both modes (ObjectTable.View on / off), duplicates and
//                    non-ascending path ids included: Find must return the LAST entry of a path id (kit #11's Dictionary overwrite), every
//                    other path id its one entry, an absent id nothing; every value as written.
//   negctl           the same, but expecting the FIRST entry of a duplicated path id: must FAIL (the check's negative control).
//   pack <pack dir>  a real Vita pack (owner-local, read only), both modes: every entry of every file against a REFERENCE that is kit #11's
//                    own maker (VitaPack.FileAt as kit #11 wrote it, over the reader's TOC bytes -- not the view's readers: review fix), every
//                    value (path id, ByteStart = payload offset + the record's, size, class id, flags as TypeIndex, one SerializedType per
//                    class id); Find for every path id (the reference's last entry of it) and absent probes; the enumerator; the live
//                    position round trip; one digest of every value per mode (equal).
//   packneg <dir>    the pack check's negative controls: the same comparison against three broken references (no payload offset; the size
//                    read 4 bytes late; the flags read from the file-index word) and one broken READER (each opened table's payload offset
//                    zeroed by reflection: the view's own ByteStart wrong) -- each must report wrong values (exit 0 only if all four do).
using Shepherd.Assets; using System.Reflection; using System.Buffers.Binary;
var mode = args.Length > 0 ? args[0] : "synthetic";
if (mode == "pack") return Pack(args[1], null);
if (mode == "packneg") return PackNeg(args[1]);
bool negctl = mode == "negctl";
var t = typeof(ObjectTable);
var write = t.GetMethod("WriteRow", BindingFlags.Static | BindingFlags.NonPublic); var from = t.GetMethod("FromRows", BindingFlags.Static | BindingFlags.NonPublic);
var types = new List<SerializedType> { new() { ClassId = 1 }, new() { ClassId = 4 }, new() { ClassId = 114 } };
var rng = new Random(12); int bad = 0, tables = 0, checks = 0;
foreach (bool view in new[] { true, false })
{
    ObjectTable.View = view;
    for (int rep = 0; rep < 300; rep++)
    {
        int n = rng.Next(0, 400); var rows = new byte[n * 32]; var ids = new long[n];
        for (int i = 0; i < n; i++) { ids[i] = rng.Next(4) == 0 && i > 0 ? ids[rng.Next(i)] : rng.NextInt64(-1000, 100000); write.Invoke(null, new object[] { rows, i, ids[i], (long)i * 10, (uint)i, types[i % 3].ClassId, i % 3 }); }
        var tab = (ObjectTable)from.Invoke(null, new object[] { rows, n, types }); tables++;
        var want = new Dictionary<long, int>(); for (int i = 0; i < n; i++) if (negctl) want.TryAdd(ids[i], i); else want[ids[i]] = i;
        foreach (var kv in want) { var o = tab.Find(kv.Key); checks++; if (o.IsNull || (int)o.ByteSize != kv.Value || o.ByteStart != kv.Value * 10L || o.PathId != kv.Key || o.ClassId != types[kv.Value % 3].ClassId || o.Type != types[kv.Value % 3] || o.TypeIndex != kv.Value % 3) bad++; }
        for (int k = 0; k < 20; k++) { long x = rng.NextInt64(-5000, 200000); if (!want.ContainsKey(x)) { checks++; if (!tab.Find(x).IsNull) bad++; } }
    }
}
Console.WriteLine($"{(negctl ? "NEGATIVE CONTROL (first entry wanted)" : "synthetic")}: {tables} tables, {checks} lookups, {bad} wrong -> {(bad == 0 ? "PASS" : "FAIL")}");
return bad == 0 ? 0 : 1;

// kit #11's maker (vita main's VitaPack.FileAt, verbatim but for the broken variants of packneg): one entry of file record `fr`'s TOC slice
static Ref[] Kit11Maker(byte[] toc, int first, int count, long payloadOffset, string broken)
{
    var r = new Ref[count];
    for (int k = 0; k < count; k++)
    {
        var e = toc.AsSpan((first + k) * 32, 32);
        int cls = BinaryPrimitives.ReadInt32LittleEndian(e[20..]);
        r[k] = new Ref(BinaryPrimitives.ReadInt64LittleEndian(e), (broken == "nobias" ? 0 : payloadOffset) + BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                       BinaryPrimitives.ReadUInt32LittleEndian(e[broken == "size+4" ? 20.. : 16..]), cls, BinaryPrimitives.ReadUInt16LittleEndian(e[broken == "flags@24" ? 24.. : 26..]));
    }
    return r;
}

static int PackNeg(string path)
{
    int caught = 0; string[] variants = { "nobias", "size+4", "flags@24", "reader-nobias" };
    foreach (var v in variants) { int rc = Pack(path, v); Console.WriteLine($"NEGATIVE CONTROL ({(v.StartsWith("reader") ? "the view's reader" : "reference")} broken: {v}): {(rc != 0 ? "FAIL as wanted" : "PASSED -- the check cannot see this break")}"); if (rc != 0) caught++; }
    Console.WriteLine($"packneg: {caught} / {variants.Length} breaks caught -> {(caught == variants.Length ? "PASS" : "FAIL")}");
    return caught == variants.Length ? 0 : 1;
}

static int Pack(string path, string broken)
{
    int rc = 0; string first = null;
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    foreach (bool view in new[] { true, false })
    {
        ObjectTable.View = view;
        int bad = 0, files = 0; long n = 0, finds = 0, absent = 0, valueBad = 0;
        using var pr = PackReader.Open(path);
        // the reader's own TOC bytes and file records (unchanged kit #11 code reads them); the reference is made from them by kit #11's maker
        var toc = (byte[])typeof(PackReader).GetField("_objToc", flags).GetValue(pr);
        var recs = (Array)typeof(PackReader).GetField("_files", flags).GetValue(pr);
        var recT = recs.GetType().GetElementType();
        var fName = recT.GetField("Name"); var fFirst = recT.GetField("First"); var fCount = recT.GetField("Count");
        var typeOf = new Dictionary<int, SerializedType>();   // kit #11: one SerializedType per class id per pack
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var rec in recs)
        {
            var name = (string)fName.GetValue(rec); int fst = (int)fFirst.GetValue(rec), cnt = (int)fCount.GetValue(rec);
            var refs = Kit11Maker(toc, fst, cnt, pr.PayloadOffset, broken);
            var f = pr.GetFile(name); var objs = f.Objects; files++;
            if (broken == "reader-nobias") typeof(ObjectTable).GetField("_bias", flags).SetValue(typeof(SerializedFile).GetField("_table", flags).GetValue(f), 0L);
            if (objs.Count != cnt) { bad++; continue; }
            var last = new Dictionary<long, int>();
            for (int i = 0; i < cnt; i++)
            {
                var o = objs[i]; var w = refs[i]; last[w.PathId] = i; n++;
                if (o.IsNull || o.PathId != w.PathId || o.ByteStart != w.ByteStart || o.ByteSize != w.ByteSize || o.ClassId != w.ClassId || o.TypeIndex != w.TypeIndex || o.Type.ClassId != w.ClassId) { bad++; valueBad++; }
                if (!typeOf.TryGetValue(w.ClassId, out var st)) typeOf[w.ClassId] = o.Type; else if (!ReferenceEquals(st, o.Type)) bad++;
                var b = new byte[28]; BinaryPrimitives.WriteInt64LittleEndian(b, o.PathId); BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), o.ByteStart);
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), o.ByteSize); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), o.ClassId); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), o.TypeIndex); h.AppendData(b);
            }
            foreach (var kv in last) { var g = f.GetObject(kv.Key); finds++; if (g.IsNull || !g.Equals(objs[kv.Value]) || g.PathId != kv.Key) bad++; }
            foreach (var miss in new long[] { long.MinValue, long.MaxValue, -1, 0, 123456789123 }) if (!last.ContainsKey(miss)) { absent++; if (!f.GetObject(miss).IsNull) bad++; }
            int k = 0; foreach (var o in f.Objects) { if (!o.Equals(objs[k])) bad++; k++; }
            if (cnt > 0) { var o = objs[cnt / 2]; int was = o.LivePos; o.LivePos = 77; if (objs[cnt / 2].LivePos != 77) bad++; o.LivePos = was; if (objs[cnt / 2].LivePos != was) bad++; }
        }
        var d = Convert.ToHexString(h.GetHashAndReset())[..16];
        if (first != null && d != first) bad++;
        first ??= d;
        Console.WriteLine($"pack (view={view}{(broken != null ? ", reference broken: " + broken : "")}): {files} files, {n} entries vs kit #11's maker ({valueBad} with a wrong value), {finds} finds, {absent} absent probes, {typeOf.Count} class types, {bad} wrong; values sha256 {d} -> {(bad == 0 ? "PASS" : "FAIL")}");
        if (bad != 0) rc = 1;
    }
    return rc;
}

// one entry as kit #11's maker made it
record struct Ref(long PathId, long ByteStart, uint ByteSize, int ClassId, int TypeIndex);
