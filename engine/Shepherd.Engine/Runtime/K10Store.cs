using System.Buffers.Binary;
using System.Text;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #10, track cache (vita/notes/kit10-cache.md): THE LEARNING CACHE'S STORE -- one place on the card (the owner folder's
/// cache/, ux0:data/shepherd/cache on the Vita) for every result the engine learns, with the owner's rules (roadmap kit #10, owner 2026-10-16:
/// "gets faster as it saves files, not slower"):
/// <list type="bullet">
/// <item><b>Exact keys.</b> An entry is one file, &lt;kind&gt;/&lt;64-bit hash of its key&gt;.k10, that stores its key IN FULL (the caller's text:
///   engine + game + pack + the kind's inputs, Runtime/K10Cache.cs <c>Key</c>) and is used only when that text is byte for byte this start's;
///   its layout version, kind version, lengths and a 64-bit checksum (IcallManifest.ContentHash) are checked first. Anything else -- missing,
///   damaged, another key -- is a miss (the damaged file is deleted): a stale or missing entry may only make a start slower, never different.</item>
/// <item><b>Verified on first reuse</b> (the GXP cache's verify step, Render/GxpCache.cs): a new entry is UNVERIFIED; the kind that reads it back
///   checks it against what this start makes again (<see cref="Confirm"/> marks it verified, <see cref="Drop"/> deletes it). The flag lives in
///   the index, so an entry file is written once and never rewritten for it.</item>
/// <item><b>Size cap, least recently used out first</b>: every file under cache/ counts (the older kinds too -- gxp, json, icalls, jit-learn,
///   the kit #8 read-order maps); before a write the least recently used ENTRIES (.k10 files) go until the new one fits under <see cref="Cap"/>
///   (Settings > PS Vita Settings > Cache Size); the older kinds are counted, never evicted by age (only Clear Cache removes them).</item>
/// <item><b>Never below the free-space margin</b>: a write that would leave the card with less than <see cref="Margin"/> free evicts first, and
///   is refused if that is not enough.</item>
/// <item><b>Clear</b> (<see cref="Clear(string[], out int)"/>): every file and folder under cache/ except the folders it is told to keep (the
///   engine keeps json/: the catalog bake the kit stages, kit #5's memory fix -- review fix 2026-10-28) -- nothing outside it (saves and
///   vita-settings.txt live elsewhere).</item>
/// <item><b>The index</b> (cache/k10index.tsv, text): one row per file -- size, last use (session x 2^20 + order), flags. Reconciled with the folder
///   on first use (files added, rows of vanished files dropped, this store's own stray temp files -- *.k10.tmp, k10index.tsv.tmp -- deleted;
///   other writers' temp files -- the GXP cache's, gc's gc-nursery.tsv.tmp -- are neither deleted nor counted); written atomically by
///   <see cref="Flush"/> only when it changed. Losing it costs only the order and the verified flags (re-verified on the next reuse).</item>
/// </list>
/// Pure: files and the index only, no engine type (tools/K10CacheTest links this file with IcallManifest.cs for the Mac self-test). Thread-safe
/// (one lock); the engine calls it from the cache worker (Runtime/K10Cache.cs) and reads <see cref="InUse"/> from the Settings rows. Code size:
/// plain arrays, no LINQ, no new generic instantiations beyond Dictionary&lt;string, class&gt; / List&lt;class&gt; (AOT code is eboot span).</summary>
public sealed class K10Store
{
    /// <summary>The card's free space in bytes (the host's devctl on the Vita), -1 = unknown.</summary>
    public interface IFree { long FreeBytes(); }
    public const string Magic = "SHPDK10C";
    public const int Layout = 1;
    public const string Ext = ".k10";
    public const string IndexName = "k10index.tsv";
    const int MaxEntry = 32 << 20;

    sealed class Row { public long Size, Use; public int Flags; }
    const int FEntry = 1, FVerified = 2;

    public readonly string Root;
    readonly IFree _free;
    readonly object _lock = new();
    readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    bool _scanned, _dirty;
    long _session, _seq, _inUse;
    /// <summary>The cap (bytes): Settings > Cache Size (256 MB / 512 MB / 1 GB / 2 GB; default 1 GB).</summary>
    public long Cap;
    /// <summary>The free space the card always keeps (bytes).</summary>
    public long Margin;
    /// <summary>Counters for the log / the Settings readout.</summary>
    public long Hits, Misses, Damaged, Writes, Refused, Evicted, EvictedBytes, Verified;
    /// <summary>Why the last write was refused (null = it was not).</summary>
    public string LastRefusal;
    /// <summary>A line per event (the engine's Log.Info; null = silent).</summary>
    public Action<string> Note;
    /// <summary>A damaged / unusable entry found by <see cref="Get"/> is handed here instead of being deleted at once (the engine: deleted by the
    /// cache worker in the next idle window, <see cref="DropIfUnusable"/> -- a delete is a card write); null = deleted at once.</summary>
    public interface IDefer { void Later(string kind, string key, int kindVersion); }
    public IDefer Defer;

    public K10Store(string root, long cap, long margin, IFree free) { Root = root; Cap = cap; Margin = margin; _free = free; }

    void Say(string s) { var n = Note; if (n != null) n(s); }

    /// <summary>Bytes under cache/ (every file, the index too), after the first reconcile; -1 before it.</summary>
    public long InUse { get { lock (_lock) return _scanned ? _inUse : -1; } }
    public int Files { get { lock (_lock) return _rows.Count; } }
    public bool Scanned { get { lock (_lock) return _scanned; } }

    // ------------------------------------------------------------------ the entry file (little-endian)
    //   "SHPDK10C" | u32 layout | u32 kind version | u32 key bytes | key (UTF-8) | u32 payload bytes | payload |
    //   u64 checksum = IcallManifest.ContentHash of every byte before it. Nothing may follow.

    public static string PathOf(string kind, string key) => kind + "/" + IcallManifest.ContentHash(Encoding.UTF8.GetBytes(key)).ToString("x16") + Ext;

    public static byte[] Encode(string key, int kindVersion, ReadOnlySpan<byte> payload)
    {
        var k = Encoding.UTF8.GetBytes(key);
        var b = new byte[8 + 4 + 4 + 4 + k.Length + 4 + payload.Length + 8];
        Encoding.ASCII.GetBytes(Magic, 0, 8, b, 0);
        int p = 8;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p), Layout); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p), kindVersion); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p), k.Length); p += 4;
        k.CopyTo(b, p); p += k.Length;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p), payload.Length); p += 4;
        payload.CopyTo(b.AsSpan(p)); p += payload.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p), IcallManifest.ContentHash(b.AsSpan(0, p)));
        return b;
    }

    /// <summary>null = usable (the payload in <paramref name="payload"/>), else why not.</summary>
    public static string Decode(byte[] b, string key, int kindVersion, out byte[] payload)
    {
        payload = null;
        if (b == null || b.Length < 8 + 16 + 8 || Encoding.ASCII.GetString(b, 0, 8) != Magic) return "not a cache entry";
        if (b.Length > MaxEntry) return "too big (" + b.Length.ToString() + " bytes)";
        int end = b.Length - 8;
        if (IcallManifest.ContentHash(b.AsSpan(0, end)) != BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(end))) return "its checksum does not match (damaged)";
        int p = 8;
        int lay = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
        if (lay != Layout) return "layout " + lay.ToString() + ", this engine reads " + Layout.ToString();
        int kv = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
        if (kv != kindVersion) return "kind version " + kv.ToString() + ", this engine reads " + kindVersion.ToString();
        int kl = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
        if (kl < 0 || kl > end - p - 4) return "truncated key";
        if (!b.AsSpan(p, kl).SequenceEqual(Encoding.UTF8.GetBytes(key))) return "made for another engine, game, pack or input (its key differs)";
        p += kl;
        int pl = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
        if (pl < 0 || pl != end - p) return "truncated payload";
        payload = b.AsSpan(p, pl).ToArray();
        return null;
    }

    // ------------------------------------------------------------------ the index

    string Full(string rel) => Path.Combine(Root, rel);
    static string Rel(string root, string full)
    {
        var r = full.Substring(root.Length).Replace('\\', '/');
        while (r.Length > 0 && r[0] == '/') r = r.Substring(1);
        return r;
    }

    /// <summary>The index read and reconciled with the folder (once; later calls return at once). Called on the worker before the first write
    /// or eviction, and when the Settings rows first need the size.</summary>
    public void Reconcile()
    {
        lock (_lock)
        {
            if (_scanned) return;
            _rows.Clear(); _inUse = 0;
            // 1. the index as written last (rows of files that still exist keep their order and flags)
            var known = new Dictionary<string, Row>(StringComparer.Ordinal);
            long session = 0;
            try
            {
                var ip = Full(IndexName);
                if (File.Exists(ip))
                    foreach (var raw in File.ReadAllText(ip).Split('\n'))
                    {
                        var line = raw.TrimEnd('\r');
                        if (line.StartsWith("# session=", StringComparison.Ordinal)) { long.TryParse(line.Substring(10), out session); continue; }
                        if (line.Length == 0 || line[0] == '#') continue;
                        var t = line.Split('\t');
                        if (t.Length != 4 || !long.TryParse(t[1], out var size) || !long.TryParse(t[2], out var use) || !int.TryParse(t[3], out var fl)) continue;
                        known[t[0]] = new Row { Size = size, Use = use, Flags = fl };
                    }
            }
            catch (Exception e) { Say("[k10] cache index not read (" + e.GetBaseException().Message + "): rebuilt from the folder"); }
            _session = session + 1; _seq = 0;
            // 2. the folder (every file: the older kinds too)
            int added = 0, strays = 0, resized = 0;
            try
            {
                if (Directory.Exists(Root))
                    foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        var rel = Rel(Root, f);
                        if (rel.EndsWith(".tmp", StringComparison.Ordinal))
                        {   // this store's own temp files only (a write cut short); another writer's (GxpCache, gc's gc-nursery.tsv.tmp, ...) may be
                            // in the middle of its write: neither deleted nor counted (review fix 2026-10-28)
                            if (OwnTemp(rel)) { try { File.Delete(f); strays++; } catch { } }
                            continue;
                        }
                        long len; try { len = new FileInfo(f).Length; } catch { continue; }
                        if (known.TryGetValue(rel, out var r))
                        {   // a file whose size changed since the index: not ours as written -- unverified again
                            if (r.Size != len) { r.Size = len; r.Flags &= ~FVerified; resized++; }
                        }
                        else { r = new Row { Size = len, Use = 0, Flags = rel.EndsWith(Ext, StringComparison.Ordinal) ? FEntry : 0 }; if (rel != IndexName) added++; }
                        _rows[rel] = r; _inUse += len;
                    }
            }
            catch (Exception e) { Say("[k10] cache folder not read (" + e.GetBaseException().Message + ")"); }
            _scanned = true;
            // (the index lists every file but itself: rows of the index that are no longer in the folder = gone; the index is rewritten
            // only when something changed -- a file added or gone, a size changed)
            int gone = 0; foreach (var kv in known) if (!_rows.ContainsKey(kv.Key)) gone++;
            _dirty = added > 0 || gone > 0 || resized > 0;
            Say("[k10] cache: " + _rows.Count.ToString() + " files, " + Mb(_inUse) + " in use of " + Mb(Cap) + " (session " + _session.ToString() + "; " +
                added.ToString() + " not in the index, " + gone.ToString() + " gone, " + resized.ToString() + " changed size, " + strays.ToString() + " stray temp files deleted)");
        }
    }

    /// <summary>A temp file of this store's own writes (an entry's or the index's), never another writer's.</summary>
    public static bool OwnTemp(string rel) => rel.EndsWith(Ext + ".tmp", StringComparison.Ordinal) || rel == IndexName + ".tmp";

    public static string Mb(long b) => b < 0 ? "?" : b >= (1L << 30) && b % (1L << 30) == 0 ? (b >> 30).ToString() + " GB" : (b / 1048576.0).ToString("F1") + " MB";

    void Touch(Row r) { r.Use = (_session << 20) | (++_seq & 0xFFFFF); _dirty = true; }

    /// <summary>The index file, when it changed (the worker, in an idle window). null = written or nothing to write, else why not.</summary>
    public string Flush()
    {
        string text;
        lock (_lock)
        {
            if (!_scanned || !_dirty) return null;
            var sb = new StringBuilder();
            sb.Append("# Shepherd learning cache index (kit #10): path TAB bytes TAB last use TAB flags (1 entry, 2 verified). Delete the cache folder to start over.\n");
            sb.Append("# session=").Append(_session.ToString()).Append('\n');
            foreach (var kv in _rows)
                if (kv.Key != IndexName) sb.Append(kv.Key).Append('\t').Append(kv.Value.Size.ToString()).Append('\t').Append(kv.Value.Use.ToString()).Append('\t').Append(kv.Value.Flags.ToString()).Append('\n');
            text = sb.ToString();
            _dirty = false;
        }
        var b = Encoding.UTF8.GetBytes(text);
        var err = WriteAtomic(Full(IndexName), b);
        lock (_lock)
        {
            if (err != null) { _dirty = true; return err; }
            if (_rows.TryGetValue(IndexName, out var r)) { _inUse += b.Length - r.Size; r.Size = b.Length; }
            else { _rows[IndexName] = new Row { Size = b.Length, Use = long.MaxValue }; _inUse += b.Length; }
        }
        return null;
    }
    public bool Dirty { get { lock (_lock) return _dirty; } }

    // ------------------------------------------------------------------ entries

    /// <summary>An entry's payload, or null (a miss: none, damaged -- deleted -- or another key). <paramref name="verified"/> = it was confirmed
    /// by an earlier reuse; an unverified payload is checked by the caller against what this start makes (<see cref="Confirm"/> / <see cref="Drop"/>).</summary>
    public byte[] Get(string kind, string key, int kindVersion, out bool verified)
    {
        verified = false;
        Reconcile();
        var rel = PathOf(kind, key);
        var full = Full(rel);
        byte[] b;
        try { if (!File.Exists(full)) { lock (_lock) Misses++; return null; } b = File.ReadAllBytes(full); }
        catch (Exception) { lock (_lock) Misses++; return null; }
        var why = Decode(b, key, kindVersion, out var payload);
        var defer = Defer;
        if (why != null)
        {
            lock (_lock)
            {
                Damaged++;
                Say("[k10] cache " + rel + " not used (" + why + "): " + (defer != null ? "deleted in the next idle window" : "deleted"));
                if (defer == null) DeleteLocked(rel);
            }
            if (defer != null) defer.Later(kind, key, kindVersion);
            return null;
        }
        lock (_lock)
        {
            Hits++;
            if (!_rows.TryGetValue(rel, out var r)) { r = new Row { Size = b.Length, Flags = FEntry }; _rows[rel] = r; _inUse += b.Length; }
            Touch(r);
            verified = (r.Flags & FVerified) != 0;
        }
        return payload;
    }

    /// <summary>The entry matched what this start made again: verified from now on.</summary>
    public void Confirm(string kind, string key)
    {
        Reconcile();
        var rel = PathOf(kind, key);
        lock (_lock) if (_rows.TryGetValue(rel, out var r) && (r.Flags & FVerified) == 0) { r.Flags |= FVerified; _dirty = true; Verified++; }
    }

    /// <summary>A deferred delete (<see cref="Defer"/>): the entry deleted only if it is still unusable for this key (a good entry written since
    /// -- the kind made it again -- stays). true = deleted (or already gone).</summary>
    public bool DropIfUnusable(string kind, string key, int kindVersion)
    {
        Reconcile();
        var rel = PathOf(kind, key);
        byte[] b = null;
        try { if (File.Exists(Full(rel))) b = File.ReadAllBytes(Full(rel)); } catch (Exception) { return false; }
        if (b != null && Decode(b, key, kindVersion, out _) == null) return false;
        lock (_lock) DeleteLocked(rel);
        return true;
    }

    /// <summary>The entry did not match (or is no longer wanted): deleted.</summary>
    public void Drop(string kind, string key) { Reconcile(); var rel = PathOf(kind, key); lock (_lock) DeleteLocked(rel); }

    void DeleteLocked(string rel)
    {
        try { File.Delete(Full(rel)); } catch { }
        if (_rows.TryGetValue(rel, out var r)) { _rows.Remove(rel); _inUse -= r.Size; _dirty = true; }
    }

    /// <summary>Writes an entry (UNVERIFIED) when it fits the cap and the free-space margin, evicting the least recently used entries first.
    /// null = written, else why not (nothing changed on the card then, other than evictions).</summary>
    public string Put(string kind, string key, int kindVersion, ReadOnlySpan<byte> payload)
    {
        Reconcile();
        var rel = PathOf(kind, key);
        var b = Encode(key, kindVersion, payload);
        lock (_lock)
        {
            long old = _rows.TryGetValue(rel, out var cur) ? cur.Size : 0, need = b.Length - old;
            var why = MakeRoomLocked(need, rel);
            if (why != null) { Refused++; LastRefusal = why; Say("[k10] cache " + rel + " (" + Mb(b.Length) + ") not written: " + why); return why; }
        }
        var err = WriteAtomic(Full(rel), b);
        lock (_lock)
        {
            if (err != null) { Refused++; LastRefusal = err; Say("[k10] cache " + rel + " not written (" + err + ")"); return err; }
            if (_rows.TryGetValue(rel, out var r)) _inUse -= r.Size; else { r = new Row(); _rows[rel] = r; }
            r.Size = b.Length; r.Flags = FEntry; _inUse += b.Length;
            Touch(r);
            Writes++; LastRefusal = null;
        }
        return null;
    }

    /// <summary>Room for <paramref name="need"/> more bytes under the cap and above the margin (least recently used entries out, never
    /// <paramref name="keep"/>); null = room, else why not.</summary>
    string MakeRoomLocked(long need, string keep)
    {
        if (need > Cap) return "bigger than the cache size (" + Mb(Cap) + ")";
        long free = _free != null ? _free.FreeBytes() : -1, freed = 0;
        while (_inUse + need > Cap || (free >= 0 && free + freed - need < Margin))
        {
            var victim = OldestLocked(keep);
            if (victim == null)
                return _inUse + need > Cap ? "the cache is full (" + Mb(_inUse) + " of " + Mb(Cap) + ", nothing older to remove)"
                                           : "the card would drop below its free-space margin (" + Mb(free + freed) + " free, margin " + Mb(Margin) + ")";
            long sz = _rows[victim].Size;
            DeleteLocked(victim); freed += sz; Evicted++; EvictedBytes += sz;
            Say("[k10] cache: " + victim + " removed (least recently used, " + Mb(sz) + ")");
        }
        return null;
    }
    string OldestLocked(string keep)
    {
        string best = null; long bu = long.MaxValue;
        foreach (var kv in _rows)
            if ((kv.Value.Flags & FEntry) != 0 && kv.Key != keep && kv.Value.Use < bu) { bu = kv.Value.Use; best = kv.Key; }
        return best;
    }

    /// <summary>The cap changed (or the margin): least recently used entries out until the cache fits again. Returns the bytes removed.</summary>
    public long Shrink()
    {
        Reconcile();
        lock (_lock)
        {
            long before = EvictedBytes;
            while (_inUse > Cap) { var v = OldestLocked(null); if (v == null) break; long sz = _rows[v].Size; DeleteLocked(v); Evicted++; EvictedBytes += sz; }
            return EvictedBytes - before;
        }
    }

    /// <summary>Clear Cache: every file and folder under the cache folder deleted (nothing outside it); the index starts over. Returns the
    /// bytes deleted and the files that could not be deleted.</summary>
    public long Clear(out int failed) => Clear(null, out failed);
    /// <summary>Clear Cache keeping the top-level folders in <paramref name="keep"/> (folder names under cache/, e.g. "json": files the kit stages
    /// there are not this cache's to delete); null = everything. The kept files stay counted in <see cref="InUse"/>.</summary>
    public long Clear(string[] keep, out int failed)
    {
        failed = 0; long bytes = 0;
        lock (_lock)
        {
            _rows.Clear(); _inUse = 0;
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        var rel = Rel(Root, f);
                        long len = 0; try { len = new FileInfo(f).Length; } catch { }
                        if (Kept(rel, keep)) { if (!rel.EndsWith(".tmp", StringComparison.Ordinal)) { _rows[rel] = new Row { Size = len }; _inUse += len; } continue; }
                        try { File.Delete(f); bytes += len; } catch { failed++; }
                    }
                    foreach (var d in Directory.GetDirectories(Root, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (Kept(Rel(Root, d) + "/", keep)) continue;
                        try { Directory.Delete(d, true); } catch { failed++; }
                    }
                }
            }
            catch (Exception) { failed++; }
            _scanned = true; _dirty = false;
        }
        return bytes;
    }
    static bool Kept(string rel, string[] keep)
    {
        if (keep == null) return false;
        for (int i = 0; i < keep.Length; i++) if (keep[i].Length > 0 && rel.Length > keep[i].Length && rel[keep[i].Length] == '/' && rel.StartsWith(keep[i], StringComparison.Ordinal)) return true;
        return false;
    }
    /// <summary>The bytes a Clear keeping <paramref name="keep"/> would delete (-1 before the first reconcile).</summary>
    public long Clearable(string[] keep)
    {
        lock (_lock)
        {
            if (!_scanned) return -1;
            long b = 0; foreach (var kv in _rows) if (!Kept(kv.Key, keep)) b += kv.Value.Size;
            return b;
        }
    }

    /// <summary><paramref name="b"/> to <paramref name="path"/>: a temp file next to it, flushed, read back and compared, then renamed over the old
    /// one (= Render/GxpCache.cs WriteAtomic: a power cut leaves the old file or the new one, never a torn one; a failed write removes its temp
    /// file). null = written, else why not.</summary>
    public static string WriteAtomic(string path, byte[] b)
    {
        string tmp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { fs.Write(b); fs.Flush(true); }
            var back = File.ReadAllBytes(tmp);
            if (!back.AsSpan().SequenceEqual(b)) { File.Delete(tmp); return "the card gave back " + back.Length.ToString() + " different bytes"; }
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception e)
        {
            if (tmp != null) try { File.Delete(tmp); } catch { }
            return e.GetBaseException().Message;
        }
    }
}
