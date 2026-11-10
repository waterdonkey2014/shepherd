#if !SHEPHERD_NO_K14SMALL
using System.Collections;
using System.Reflection;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #14, track small (vita/notes/kit14-small.md, "the game catalog kept out of memory until asked for"). The game's
/// Addressables catalog (StreamingAssets/aa/catalog.json) becomes, at boot, one ResourceLocationMap holding every key (33,651 in the
/// owner's build: GUIDs, addresses, labels, dependency groups) with an array of locations each (353,222 references to 19,777 location
/// objects with their internal-id strings): ~10.5 MiB of the 32-bit GC heap for the whole session (kit #13 census at the intro: 54,114
/// strings 6.73 MiB, 33,648 location arrays 1.97, 19,775 locations 0.91, the dictionary 0.55, ...), while a walk asks for a few hundred keys.
///
/// With the owner's Unity.Addressables.dll rewritten (tools/Rewriter --addr-lazy; Shepherd.Hooks AddrLazy) ContentCatalogData.CreateLocator
/// first asks <see cref="Create"/>. The catalog is then kept compact (the keys' and internal ids' text as Latin-1 bytes under shared
/// directory prefixes, the buckets and entry rows as int / ushort arrays) and the game gets an empty ResourceLocationMap tied to it. The
/// map's Locate first puts the asked key's entry into the map's dictionary, made exactly as the original CreateLocator makes it: the same
/// constructor with the same arguments, the same key / location / array objects shared the same way (one key object per bucket, one
/// location per entry, one array per bucket, the primary key = the key object itself, the internal id string shared by the entries the
/// original gave one string), and the original lookup then runs unchanged. Keys / Locations / Add (anything that sees the whole
/// dictionary or changes it) first put every entry in, in the catalog's order (the dictionary rebuilt in that order), and the map is an
/// ordinary one from then on.
///
/// What the original does at load that has an effect is still done at load, in its order: the provider-suffix rename, every entry's data
/// object (the game's own reader: JsonUtility for the bundles' request options), every entry's ResolveInternalId (the runtime properties'
/// cache), every entry's resource type (SerializedType.Value's cache written back into the catalog's array), the primary key text of a
/// non-string key. Only pure work is deferred: decoding key text, building the location objects, the arrays and the dictionary entries.
/// The game's own pieces (its reader, ExpandInternalId, ResolveInternalId, the in-place SerializedType.Value, the location constructor) come
/// as delegates from the rewritten DLL (AddrLazy.Bind): the engine uses no reflection for them outside the check mode.
/// Anything the compact form cannot hold exactly refuses before the first effect: the original then reads the catalog as before (and fails as before).
/// The catalog's format reader is owner-local (engine/Derived: CatalogProvider, ICatalogReader below; vita/notes/kit14-integration.md): this file holds the store, the lookups and the check.
/// No reader on the card (a shareable build, SHEPHERD_DERIVED=off, an older Derived DLL) = a refusal: the catalog is read as before, and the end line says so.
///
/// Switches (env file): SHEPHERD_K14_CATALOG=1 (default in the Vita profile) | 0 = kit #13's catalog (also SHEPHERD_K14_SMALL=0) | check = lazy, plus
/// the original map built a second time (the hooks bypassed) and every Locate answer, the full key order and, at the end, every key's locations
/// compared with it: every field's value and type, and which objects are shared, in both directions (test only: its own effects run twice).
/// Which DLL the card holds is not a switch: on the interpreted-game apps a kit #13 Unity.Addressables.dll never calls the hooks, so the catalog is read
/// as before; on the game-AOT apps the eboot's images are bound to the rewritten DLL's MVID, so any other DLL is a fatal "doesn't match assembly" at load
/// (the way past it: SHEPHERD_GAME_AOT=0, or a rebuild). The rewritten DLL needs an eboot with Shepherd.Hooks' AddrLazy (SHEPHERD_K14_SMALL_CODE=1).
/// Test only, with check: SHEPHERD_K14_CATALOG_NEGCTL=1 changes one entry's dependency hash, =2 swaps two keys in the whole map's order, =3 gives each
/// location its own internal id string, =4 makes each primary key a copy of its key's text, =5 makes a new location per reference (the check must
/// report each); SHEPHERD_K14_CATALOG_KEYS=1 asks for the whole map at the end.</summary>
/// <summary>The owner-local catalog reader (engine/Derived, loaded by name; never in git, never in a VPK). It walks the catalog's data and reports into the sink, running the game's pieces
/// through the sink in the original's order. Answers null, or a refusal's text while none of the game's effects has run.</summary>
public interface ICatalogReader
{
    int Api { get; }
    string Read(string bucketData, string keyData, string entryData, string extraData, string[] internalIds, string[] prefixes, string[] providerIds, object resourceTypes, int resourceTypeCount, string providerSuffix, ICatalogSink to);
}

/// <summary>What the reader reports (the compact store), and the game's pieces it runs through it. A string answer is a refusal's text, null = taken.</summary>
public interface ICatalogSink
{
    /// <summary>bucketStart: bucket -> its first reference, length buckets + 1 (the store keeps the array).</summary>
    string Sizes(int buckets, int entries, int[] bucketStart, int idCount, int providerCount, int typeCount);
    void Reference(int index, int entry);
    string TextKey(int bucket, string text);
    void OtherKey(int bucket);
    void Entry(int entry, int dependencyBucketPlus1, int dependencyHash, int primaryBucket, int provider);
    object ReadObject(byte[] data, int offset);
    string ExpandId(string[] prefixes, string id);
    string ResolveId(string id);
    Type TypeAt(object resourceTypes, int index);
    void ProviderNames(string[] names);
    void OtherKeyObject(int bucket, object key);
    void EntryData(int entry, object data);
    void EntryId(int entry, string id);
    void EntryPrimary(int entry);
    void EntryType(int entry, int typeIndex, Type type);
}

[ProfileOnly]
public static class K14Catalog
{
    static string Env(string n) => (Environment.GetEnvironmentVariable(n) ?? "").Trim().ToLowerInvariant();
    public static readonly bool Master = Env("SHEPHERD_K14_SMALL") != "0";
    static readonly string Setting = Env("SHEPHERD_K14_CATALOG");
    public static readonly bool On = Profile.Vita && Master && Setting is not ("0" or "off");
    public static readonly bool Check = On && Setting == "check";
    static readonly string NegCtlS = Check ? Env("SHEPHERD_K14_CATALOG_NEGCTL") : "";
    static readonly bool NegCtl = NegCtlS == "1", NegOrder = NegCtlS == "2", NegOwnId = NegCtlS == "3", NegPkCopy = NegCtlS == "4", NegLocCopy = NegCtlS == "5";
    /// <summary>Test only, with check: SHEPHERD_K14_CATALOG_KEYS=1 asks for the whole map (Keys) at the end, so its order check runs on a route
    /// that never asks for it.</summary>
    static readonly bool TestKeys = Check && Env("SHEPHERD_K14_CATALOG_KEYS") == "1";
    [ThreadStatic] static bool t_bypass;
    static readonly List<Store> _stores = new();
    static int _refused; static string _refusedWhy, _installNote = "not installed";

    public static void Install()
    {
        // the read-ahead row (vita/notes/kit14-small.md): the pack's read windows are the first memory to give back -- 3 x 128 KiB instead of 8 on the Vita
        // (VLX, the walk: arena peak -1.0 MiB, card reads 1,055 -> 1,150; the same bytes; 2 -> 1,215, 1 -> 4,554). SHEPHERD_K14_PACKWIN=8 = kit #13
        if (Master) Shepherd.Assets.PackReader.WindowCount = int.TryParse(Env("SHEPHERD_K14_PACKWIN"), out int w) && w >= 1 && w <= 64 ? w : 3;
        if (!On) { _installNote = !Profile.Vita ? "off (not the Vita profile)" : !Master ? "off (SHEPHERD_K14_SMALL=0)" : "off (SHEPHERD_K14_CATALOG=0)"; return; }
        Shepherd.Hooks.AddrLazy.OnCreate = Create; Shepherd.Hooks.AddrLazy.OnAttached = Attached;
        Shepherd.Hooks.AddrLazy.OnEnsure = Ensure; Shepherd.Hooks.AddrLazy.OnFull = Full;
        _installNote = Check ? "on, CHECK mode (the original map built too and compared)" : "on";
    }

    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ---------------------------------------------------------------- the compact text store (Latin-1 leaves under shared directory prefixes)
    sealed class Text
    {
        byte[] _b = new byte[1 << 16]; int _nb;
        int[] _off = new int[1 << 12]; ushort[] _dir = new ushort[1 << 12]; int _n;
        string[] _dirs; readonly List<string> _dirList = new(); Dictionary<string, int> _dirIx = new();
        [ThreadStatic] static char[] t_scratch;
        public int Count => _n;
        /// <summary>A handle, or -1 when the text is not Latin-1 (the caller refuses: the compact form holds Latin-1 only).</summary>
        public int Add(string s)
        {
            for (int k = 0; k < s.Length; k++) if (s[k] > 255) return -1;
            if (IsHex32(s))
            {   // (a GUID: 13,635 of the owner's keys) 16 bytes instead of 32, no directory
                if (_n + 1 >= _off.Length) { Array.Resize(ref _off, _off.Length * 2); Array.Resize(ref _dir, _dir.Length * 2); }
                while (_nb + 16 > _b.Length) Array.Resize(ref _b, _b.Length * 2);
                for (int k = 0; k < 16; k++) _b[_nb + k] = (byte)(Nib(s[2 * k]) << 4 | Nib(s[2 * k + 1]));
                _off[_n] = _nb; _dir[_n] = Hex; _nb += 16; _off[_n + 1] = _nb;
                return _n++;
            }
            int cut = s.LastIndexOf('/') + 1;
            string d = cut > 0 ? s.Substring(0, cut) : "";
            if (!_dirIx.TryGetValue(d, out int di)) { di = _dirList.Count; if (di >= Hex) return -1; _dirIx[d] = di; _dirList.Add(d); }
            if (_n + 1 >= _off.Length) { Array.Resize(ref _off, _off.Length * 2); Array.Resize(ref _dir, _dir.Length * 2); }
            int len = s.Length - cut;
            while (_nb + len > _b.Length) Array.Resize(ref _b, _b.Length * 2);
            for (int k = 0; k < len; k++) _b[_nb + k] = (byte)s[cut + k];
            _off[_n] = _nb; _dir[_n] = (ushort)di; _nb += len; _off[_n + 1] = _nb;
            return _n++;
        }
        public void Trim()
        {
            Array.Resize(ref _b, _nb); Array.Resize(ref _off, _n + 1); Array.Resize(ref _dir, _n);
            _dirs = _dirList.ToArray(); _dirList.Clear(); _dirIx = null;
        }
        public long Bytes { get { long n = _b.Length + 4L * _off.Length + 2L * _dir.Length; foreach (var d in _dirs) n += 16 + 2L * d.Length; return n; } }
        string Dir(int h) => _dirs != null ? _dirs[_dir[h]] : _dirList[_dir[h]];
        const ushort Hex = ushort.MaxValue;   // (the directory slot of a 16-byte GUID leaf)
        static bool IsHex32(string s) { if (s.Length != 32) return false; foreach (char c in s) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false; return true; }
        static int Nib(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
        static char Dig(int n) => (char)(n < 10 ? '0' + n : 'a' + n - 10);
        public string Get(int h)
        {
            var c = t_scratch; if (c == null) t_scratch = c = new char[256];
            if (_dir[h] == Hex) { int g = _off[h]; for (int k = 0; k < 16; k++) { c[2 * k] = Dig(_b[g + k] >> 4); c[2 * k + 1] = Dig(_b[g + k] & 15); } return new string(c, 0, 32); }
            string d = Dir(h); int a = _off[h], n = _off[h + 1] - a, len = d.Length + n;
            if (c.Length < len) t_scratch = c = new char[len];
            d.CopyTo(0, c, 0, d.Length);
            for (int k = 0; k < n; k++) c[d.Length + k] = (char)_b[a + k];
            return new string(c, 0, len);
        }
        public bool Same(int h, string s)
        {
            if (_dir[h] == Hex)
            {
                if (s.Length != 32) return false;
                int g = _off[h];
                for (int k = 0; k < 16; k++) if (s[2 * k] != Dig(_b[g + k] >> 4) || s[2 * k + 1] != Dig(_b[g + k] & 15)) return false;
                return true;
            }
            string d = Dir(h); int a = _off[h], n = _off[h + 1] - a;
            if (s.Length != d.Length + n) return false;
            for (int k = 0; k < d.Length; k++) if (s[k] != d[k]) return false;   // (no string.CompareOrdinal: a trimmed CoreLib runs it in the interpreter)
            for (int k = 0; k < n; k++) if (s[d.Length + k] != (char)_b[a + k]) return false;
            return true;
        }
        public static uint Hash(string s) { uint x = 2166136261; for (int k = 0; k < s.Length; k++) x = (x ^ s[k]) * 16777619; return x; }
    }

    // ---------------------------------------------------------------- one catalog's compact form
    sealed class Store : ICatalogSink
    {
        public object Map; public IDictionary Dict; public bool IsFull, FullNow; public string Name;
        public string[] IdFallback;      // (only after a late refusal: the resolved ids as the original made them)
        public int NBuckets, NEntries;
        public int[] KeyText;            // bucket -> its key's text handle (-1: a non-string key, made at load: NonText)
        public Dictionary<int, object> NonText = new();
        public int[] BStart; public ushort[] BRef16;   // bucket -> its entries
        public int[] EDepHash; public ushort[] EShare, EDep, EPk, EType; public byte[] EProv;   // entry rows (EDep: bucket + 1, 0 = none; EShare: the id's text handle)
        public Dictionary<int, object> EData = new(); public Dictionary<int, string> EPkText = new();
        public string[] Providers; public Type[] TypeTab;
        public Text Keys = new(), Ids = new();
        public ushort[] Table; public int Mask;   // (bucket + 1, 0 = empty)
        public Dictionary<int, object> KeyMemo = new(), ArrMemo = new(), LocMemo = new(); public Dictionary<int, string> IdMemo = new();
        public long Ensures, Hits, Misses, Finds; public int Fulls, NShare; public long CompactBytes; public double BuildMs;
        // check mode
        public object Twin; public IDictionary TwinDict; public long Compared, Diffs; public List<string> DiffLines = new();
        public Dictionary<object, object> TwinKeyObj;   // (equal key -> the object the original's dictionary holds)
        public readonly Dictionary<object, object> MineOf = new(ReferenceEqualityComparer.Instance), TwinOf = new(ReferenceEqualityComparer.Instance);   // twin object <-> ours

        // ---- what the owner-local reader reports (ICatalogSink)
        public List<string> ShareStr = new(); public Dictionary<object, int> ShareOf = new(ReferenceEqualityComparer.Instance);
        public List<Type> TypeList = new(); public int[] TypeSlot; public string RefuseLate;   // (a type's slot in TypeTab + 1, by the catalog's type index)
        public string Sizes(int buckets, int entries, int[] bucketStart, int idCount, int providerCount, int typeCount)
        {
            if (buckets >= ushort.MaxValue || entries > ushort.MaxValue + 1 || idCount >= ushort.MaxValue) return "more keys / entries / ids than the compact rows hold";
            if (providerCount > 255 || typeCount > ushort.MaxValue - 1) return "too many providers / types for the compact rows";
            NBuckets = buckets; NEntries = entries; BStart = bucketStart;
            BRef16 = new ushort[bucketStart[buckets]];
            KeyText = new int[buckets];
            int tsz = 1; while (tsz < 2 * buckets + 2) tsz <<= 1; Table = new ushort[tsz]; Mask = tsz - 1;
            EShare = new ushort[entries]; EDep = new ushort[entries]; EDepHash = new int[entries]; EPk = new ushort[entries]; EProv = new byte[entries]; EType = new ushort[entries];
            TypeSlot = new int[typeCount];
            return null;
        }
        public void Reference(int index, int entry) => BRef16[index] = (ushort)entry;
        public string TextKey(int bucket, string text)
        {
            int h = Keys.Add(text); if (h < 0) return "a key outside the compact text";
            KeyText[bucket] = h;
            int i = (int)(Text.Hash(text) & (uint)Mask);
            for (; Table[i] != 0; i = (i + 1) & Mask) if (Keys.Same(KeyText[Table[i] - 1], text)) return "a key seen twice";
            Table[i] = (ushort)(bucket + 1);
            return null;
        }
        public void OtherKey(int bucket) => KeyText[bucket] = -1;
        public void Entry(int entry, int dependencyBucketPlus1, int dependencyHash, int primaryBucket, int provider)
        { EDep[entry] = (ushort)dependencyBucketPlus1; EDepHash[entry] = dependencyHash; EPk[entry] = (ushort)primaryBucket; EProv[entry] = (byte)provider; }
        public object ReadObject(byte[] data, int offset) => Shepherd.Hooks.AddrLazy.Read(data, offset);
        public string ExpandId(string[] prefixes, string id) => Shepherd.Hooks.AddrLazy.Expand(prefixes, id);
        public string ResolveId(string id) => Shepherd.Hooks.AddrLazy.Resolve(id);
        public Type TypeAt(object types, int index) => Shepherd.Hooks.AddrLazy.TypeValue(types, index);
        public void ProviderNames(string[] names) => Providers = names;
        public void OtherKeyObject(int bucket, object key) => NonText[bucket] = KeyMemo[bucket] = key;
        public void EntryData(int entry, object data) { if (data != null) EData[entry] = data; }
        public void EntryId(int entry, string id)
        {
            if (id == null) { RefuseLate ??= "an internal id resolved to null"; id = NullId; }
            if (!ShareOf.TryGetValue(id, out int cls))
            {   // (one class per string OBJECT the original would share: the text handle; the same handle numbers as the classes)
                cls = ShareStr.Count; ShareOf[id] = cls; ShareStr.Add(id);
                if (Ids.Add(id) != cls && RefuseLate == null) RefuseLate = "an internal id is not Latin-1";
            }
            if (cls > ushort.MaxValue) RefuseLate ??= "too many internal ids";
            EShare[entry] = (ushort)cls;
        }
        public void EntryPrimary(int entry) { if (KeyText[EPk[entry]] < 0) EPkText[entry] = KeyMemo[EPk[entry]]?.ToString(); }
        public void EntryType(int entry, int typeIndex, Type t)
        {
            int slot = TypeSlot[typeIndex] - 1;
            if (slot < 0 || !ReferenceEquals(TypeList[slot], t)) { slot = TypeList.IndexOf(t); if (slot < 0) { slot = TypeList.Count; TypeList.Add(t); } TypeSlot[typeIndex] = slot + 1; }
            EType[entry] = (ushort)slot;
        }

        /// <summary>The map is whole: the compact form is no longer read (the objects stay in the dictionary).</summary>
        public void DropCompact()
        {
            Keys = Ids = null; KeyText = null; Table = null; BStart = null; BRef16 = null; EDepHash = null; EShare = EDep = EPk = EType = null; EProv = null;
            EData = null; EPkText = null; NonText = null; KeyMemo = ArrMemo = LocMemo = null; IdMemo = null;
        }
        public int Find(object key)
        {
            Finds++;
            if (key is string s)
            {
                for (int i = (int)(Text.Hash(s) & (uint)Mask); ; i = (i + 1) & Mask)
                {
                    int v = Table[i]; if (v == 0) return -1;
                    if (Keys.Same(KeyText[v - 1], s)) return v - 1;
                }
            }
            int h = key.GetHashCode();   // a non-string key: the dictionary's rule (the stored key's hash and its Equals) over the few non-string keys
            foreach (var kv in NonText) if (kv.Value.GetHashCode() == h && kv.Value.Equals(key)) return kv.Key;
            return -1;
        }
        public object KeyObj(int b)
        {
            if (KeyMemo.TryGetValue(b, out var o)) return o;
            o = Keys.Get(KeyText[b]); KeyMemo[b] = o; return o;
        }
        public string IdOf(int e)
        {
            int c = EShare[e];
            if (IdMemo.TryGetValue(c, out var s)) return NegOwnId && s != null ? new string(s.ToCharArray()) : s;
            s = IdFallback != null ? (ReferenceEquals(IdFallback[c], NullId) ? null : IdFallback[c]) : Ids.Get(c); IdMemo[c] = s; return s;   // (a share class = its text handle)
        }
        public object Loc(int e)
        {
            if (!NegLocCopy && LocMemo.TryGetValue(e, out var o)) return o;
            object dep = EDep[e] == 0 ? null : KeyObj(EDep[e] - 1);
            EData.TryGetValue(e, out var data);
            string pk = EPkText.TryGetValue(e, out var p) ? p : (string)KeyObj(EPk[e]);   // the original: key.ToString() (a string key: the key itself)
            if (NegPkCopy && pk != null) pk = new string(pk.ToCharArray());
            o = Shepherd.Hooks.AddrLazy.NewLocation(Map, IdOf(e), Providers[EProv[e]], dep, data, EDepHash[e], pk, TypeTab[EType[e]]);
            LocMemo[e] = o; return o;
        }
        public object Arr(int b)
        {
            if (ArrMemo.TryGetValue(b, out var o)) return o;
            int a = BStart[b], n = BStart[b + 1] - a;
            var arr = Shepherd.Hooks.AddrLazy.NewArray(n);
            for (int k = 0; k < n; k++) arr[k] = Loc(BRef16[a + k]);
            ArrMemo[b] = arr; return arr;
        }
    }

    /// <summary>(A late refusal's stand-in for a null id; never seen: ResolveInternalId returns "" for null.)</summary>
    static readonly string NullId = new string('\0', 1);

    // ---------------------------------------------------------------- the owner-local reader (vita/notes/kit14-integration.md: moved out of tracked code)
    /// <summary>The reader's contract version; a provider with another one is not used (the catalog is then read as before).</summary>
    public const int ReaderApi = 1;
    static ICatalogReader _reader; static bool _readerTried; static string _readerNote = "no owner-local catalog reader";

    /// <summary>The owner-local reader (Shepherd.Engine.Derived's CatalogProvider, loaded by name like the render and physics providers), or null: no reader = no compact catalog, the
    /// game reads its catalog as before. A build without engine/Derived (a shareable VPK) stops here, and so does SHEPHERD_DERIVED=off.</summary>
    static ICatalogReader Reader()
    {
        if (_readerTried) return _reader;
        _readerTried = true;
        if (Env("SHEPHERD_DERIVED") is "off" or "0") { _readerNote = "no owner-local catalog reader (SHEPHERD_DERIVED=off)"; return null; }
        try
        {
            var asm = Assembly.Load(new AssemblyName(Render.DerivedRender.AssemblyName));
            var t = asm.GetType("Shepherd.Engine.Derived.CatalogProvider", false);
            if (t == null) { _readerNote = "no owner-local catalog reader (a Derived DLL without one)"; return null; }
            var r = (ICatalogReader)Activator.CreateInstance(t);
            if (r.Api != ReaderApi) { _readerNote = $"no owner-local catalog reader (api {r.Api}, this engine's is {ReaderApi}: rebuild it)"; return null; }
            _reader = r;
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException or MissingMethodException or InvalidCastException or TargetInvocationException)
        { _readerNote = "no owner-local catalog reader (" + e.GetType().Name + ")"; }
        return _reader;
    }

    static object Refuse(string why)
    {
        _refused++; _refusedWhy ??= why;
        Log.Info("[k14] catalog: read as before (" + why + ")");
        return null;
    }

    // ---------------------------------------------------------------- the hooks
    /// <summary>ContentCatalogData.CreateLocator's first step (Shepherd.Hooks.AddrLazy.Create): a compact store, or null (read as before).</summary>
    static object Create(object catalog, string bucketS, string keyS, string entryS, string extraS, string[] ids, string[] prefixes, string[] providers, object types, int typeCount, string locatorId, string providerSuffix)
    {
        if (t_bypass || catalog == null) return null;
        var hooks = Shepherd.Hooks.AddrLazy.Read != null && Shepherd.Hooks.AddrLazy.Expand != null && Shepherd.Hooks.AddrLazy.Resolve != null && Shepherd.Hooks.AddrLazy.TypeValue != null && Shepherd.Hooks.AddrLazy.NewArray != null && Shepherd.Hooks.AddrLazy.NewLocation != null;
        if (!hooks) return Refuse("the rewritten DLL handed over no pieces");
        var reader = Reader(); if (reader == null) return Refuse(_readerNote);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var st = new Store { Name = locatorId ?? "" };
        // the owner-local reader walks the catalog and reports into the store; a refusal here has run none of the game's effects
        string why = reader.Read(bucketS, keyS, entryS, extraS, ids, prefixes, providers, types, typeCount, providerSuffix, st);
        if (why != null) return Refuse(why);
        st.NShare = st.ShareStr.Count; st.TypeTab = st.TypeList.ToArray();
        if (st.RefuseLate != null)
        {   // (never seen; the effects are done: the original's map is made from them now, without running them again)
            Log.Warn("[k14] catalog: " + st.RefuseLate + " -- every entry put in at once (the effects already ran once)");
            st.IdFallback = st.ShareStr.ToArray(); st.FullNow = true;
        }
        st.ShareStr = null; st.ShareOf = null; st.TypeList = null; st.TypeSlot = null;
        st.Keys.Trim(); st.Ids.Trim();
        if (NegCtl && st.NEntries > 0) st.EDepHash[0] ^= 1;
        st.CompactBytes = st.Keys.Bytes + st.Ids.Bytes + 4L * (st.KeyText.Length + st.BStart.Length) + 2L * st.Table.Length + 2L * st.BRef16.Length
                          + st.NEntries * (4L + 2 * 4 + 1) + 40L * (st.EData.Count + st.EPkText.Count + st.NonText.Count);
        st.BuildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lock (_stores) _stores.Add(st);
        if (Check)
        {   // the original map, built again with the hooks bypassed (test only: its effects run a second time)
            t_bypass = true;
            try
            {
                var cl = catalog.GetType().GetMethod("CreateLocator", I, null, new[] { typeof(string) }, null)!;
                st.Twin = cl.Invoke(catalog, new object[] { providerSuffix });
                st.TwinDict = (IDictionary)cl.ReturnType.GetField("locations", I)!.GetValue(st.Twin);
            }
            catch (Exception e) { st.DiffLines.Add("the original map could not be built for the check: " + e.GetType().Name); st.Diffs++; }
            finally { t_bypass = false; }
        }
        return st;
    }

    /// <summary>The map the rewritten CreateLocator made for a store, and its dictionary.</summary>
    static void Attached(object state, object map, object dict)
    {
        var st = (Store)state; st.Map = map; st.Dict = (IDictionary)dict;
        Log.Info($"[k14] catalog '{st.Name}': kept compact -- {st.NBuckets:N0} keys, {st.NEntries:N0} locations, {st.BStart[st.NBuckets]:N0} references, " +
                 $"{st.Keys.Count:N0} key texts + {st.NShare:N0} internal ids in {st.CompactBytes / 1024.0:N0} KiB (built in {st.BuildMs:F0} ms){(Check ? "; CHECK mode" : "")}");
        if (st.FullNow) FullCore(st);
    }

    /// <summary>ResourceLocationMap.Locate's first step: the key's entry put into the dictionary when the catalog holds it.</summary>
    static void Ensure(object state, object key)
    {
        var st = (Store)state;
        if (key == null || st.Dict == null) return;   // (null: the original lookup throws as before)
        lock (st)
        {
            if (st.IsFull) return;
            st.Ensures++;
            int b = st.Find(key);
            if (b < 0) { st.Misses++; if (st.TwinDict != null) CheckKey(st, key, -1); return; }
            if (st.ArrMemo.ContainsKey(b)) { st.Hits++; return; }
            st.Dict.Add(st.KeyObj(b), st.Arr(b));
            if (st.TwinDict != null) CheckKey(st, key, b);
        }
    }

    /// <summary>ResourceLocationMap.Keys / Locations / Add's first step: every entry put in, the dictionary rebuilt in the catalog's order.</summary>
    static void Full(object state) { var st = (Store)state; lock (st) FullCore(st); }

    static void FullCore(Store st)
    {
        if (st.Dict == null || st.IsFull) return;
        st.Fulls++;
        for (int b = 0; b < st.NBuckets; b++) { st.KeyObj(b); st.Arr(b); }
        st.Dict.Clear();
        try { st.Dict.GetType().GetMethod("EnsureCapacity", new[] { typeof(int) })?.Invoke(st.Dict, new object[] { st.NBuckets }); } catch (Exception) { }   // (the original's capacity)
        for (int b0 = 0; b0 < st.NBuckets; b0++)
        {
            int b = NegOrder && b0 < 2 && st.NBuckets > 1 ? 1 - b0 : b0;   // (test only: SHEPHERD_K14_CATALOG_NEGCTL=2, the order check's negative control)
            st.Dict.Add(st.KeyMemo[b], st.ArrMemo[b]);
        }
        st.IsFull = true; st.FullNow = false;
        Log.Info($"[k14] catalog '{st.Name}': the whole map was asked for (Keys / Locations / Add): every key put in, in the catalog's order");
        if (st.TwinDict == null) st.DropCompact();
        if (st.TwinDict != null)
        {
            int i = 0; var tk = new List<object>(); foreach (var k in st.TwinDict.Keys) tk.Add(k);
            foreach (var k in st.Dict.Keys) { if (i >= tk.Count || !Equals(k, tk[i])) { Diff(st, $"key order differs at {i}"); break; } i++; }
            if (i != tk.Count) Diff(st, $"key count {i} vs the original's {tk.Count}");
        }
    }

    // ---------------------------------------------------------------- check mode
    static void Diff(Store st, string what) { st.Diffs++; if (st.DiffLines.Count < 12) st.DiffLines.Add(what); }

    static void CheckKey(Store st, object key, int b)
    {
        st.Compared++;
        bool twinHas = st.TwinDict.Contains(key);
        if ((b >= 0) != twinHas) { Diff(st, $"key '{key}': {(b >= 0 ? "found" : "not found")} here, {(twinHas ? "found" : "not found")} in the original"); return; }
        if (b >= 0) { Pair(st, st.KeyObj(b), TwinKey(st, key), $"key of bucket {b}"); CompareArr(st, b, st.TwinDict[key] as IList); }
    }

    /// <summary>The object the original's dictionary holds for an equal key.</summary>
    static object TwinKey(Store st, object key)
    {
        if (st.TwinKeyObj == null) { st.TwinKeyObj = new(st.TwinDict.Count); foreach (var k in st.TwinDict.Keys) st.TwinKeyObj[k] = k; }
        return st.TwinKeyObj[key];
    }

    /// <summary>The sharing, not only the values: an object of the original stands for exactly one of ours and the other way round, so two slots hold one object
    /// here exactly when they hold one there (a key object, its primary key and dependency key uses; an internal id; a location; an array).</summary>
    static void Pair(Store st, object mine, object twin, string what)
    {
        if (mine == null || twin == null) { if (mine != twin) Diff(st, what + ": null on one side"); return; }
        if (st.MineOf.TryGetValue(twin, out var m)) { if (!ReferenceEquals(m, mine)) Diff(st, what + ": one object in the original, two here"); }
        else st.MineOf[twin] = mine;
        if (st.TwinOf.TryGetValue(mine, out var t)) { if (!ReferenceEquals(t, twin)) Diff(st, what + ": two objects in the original, one here"); }
        else st.TwinOf[mine] = twin;
    }

    static void CompareArr(Store st, int b, IList twin)
    {
        var mine = (IList)st.Arr(b);
        if (twin == null || mine.Count != twin.Count) { Diff(st, $"bucket {b}: {mine.Count} locations vs the original's {twin?.Count}"); return; }
        Pair(st, mine, twin, $"bucket {b}'s array");
        for (int k = 0; k < mine.Count; k++)
        {
            string a = Describe(mine[k]), c = Describe(twin[k]);
            if (a != c) Diff(st, $"bucket {b} location {k}: '{a}' vs the original's '{c}'");
            PairLocation(st, mine[k], twin[k], $"bucket {b} location {k}");
        }
    }

    /// <summary>A location's object references, against the original's: the location itself, its map, and every reference field but the type (a Type has one object).</summary>
    static void PairLocation(Store st, object mine, object twin, string what)
    {
        Pair(st, mine, twin, what);
        if (mine == null || twin == null) return;
        foreach (var f in mine.GetType().GetFields(I))
        {
            if (f.FieldType.IsValueType || f.FieldType == typeof(Type)) continue;
            object a = f.GetValue(mine), c = twin.GetType().GetField(f.Name, I)?.GetValue(twin);
            if (f.Name == "m_Locator") { if (!ReferenceEquals(a, st.Map) || !ReferenceEquals(c, st.Twin)) Diff(st, what + ": not tied to its own map"); continue; }
            Pair(st, a, c, what + " " + f.Name);
        }
    }

    /// <summary>A location's every field but its map (PairLocation checks that one): id, provider, dependency key, data, both hashes, primary key, type. m_HashCode comes from
    /// the strings' per-process hashes: one process, so equal text gives equal values here.</summary>
    static string Describe(object loc)
    {
        if (loc == null) return "null";
        var sb = new System.Text.StringBuilder(loc.GetType().Name);
        foreach (var f in loc.GetType().GetFields(I).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (f.Name == "m_Locator") continue;   // the map (another object by design in check mode)
            var v = f.GetValue(loc);
            sb.Append('|').Append(f.Name).Append('=').Append(v == null ? "null" : v is string s ? "\"" + s + "\"" : v is Type t ? t.AssemblyQualifiedName : DataText(v));
        }
        return sb.ToString();
    }

    static string DataText(object v)
    {
        var t = v.GetType();
        if (t.IsPrimitive) return t.Name + ":" + v;   // (a numeric key: its type too, the text alone would match any integer type)
        var sb = new System.Text.StringBuilder(t.Name).Append('{');
        foreach (var f in t.GetFields(I).OrderBy(f => f.Name, StringComparer.Ordinal)) sb.Append(f.Name).Append(':').Append(f.GetValue(v)).Append(';');
        return sb.Append('}').ToString();
    }

    /// <summary>The end line (Boot's exit): per catalog, what was asked for, and the check's verdict.</summary>
    public static void Summary()
    {
        if (!On && _refused == 0) { if (Profile.Vita) Log.Info("[k14] catalog: " + _installNote); return; }
        lock (_stores)
        {
            if (_stores.Count == 0) Log.Info($"[k14] catalog: {_installNote}; no catalog kept compact{(_refused > 0 ? $" ({_refused} read as before: {_refusedWhy})" : " (a kit #13 Unity.Addressables.dll, or no catalog loaded)")}");
            foreach (var st in _stores)
                lock (st)
                {
                    if (TestKeys && st.Map != null) st.Map.GetType().GetProperty("Keys", I)!.GetValue(st.Map);   // -> the map's prologue -> Full -> the order check
                    if (st.TwinDict != null)
                    {   // the end of a check: every key's locations made here and compared with the original's
                        for (int b = 0; b < st.NBuckets; b++)
                        {
                            var k = st.KeyObj(b); st.Compared++;
                            if (!st.TwinDict.Contains(k)) { Diff(st, $"bucket {b} key '{k}' is not in the original"); continue; }
                            Pair(st, k, TwinKey(st, k), $"key of bucket {b}"); CompareArr(st, b, st.TwinDict[k] as IList);
                        }
                        if (st.TwinDict.Count != st.NBuckets) Diff(st, $"{st.NBuckets} keys vs the original's {st.TwinDict.Count}");
                    }
                    Log.Info($"[k14] catalog '{st.Name}': {st.Ensures:N0} lookups ({st.Hits:N0} already in, {st.Misses:N0} not in this catalog), {st.ArrMemo?.Count ?? st.NBuckets:N0} of {st.NBuckets:N0} keys and {st.LocMemo?.Count ?? st.NEntries:N0} of {st.NEntries:N0} locations made; " +
                             $"{(st.IsFull ? $"the whole map was asked for ({st.Fulls}x)" : "never asked for whole")}; compact form {st.CompactBytes / 1024.0:N0} KiB" +
                             (Check ? $"; CHECK: {st.Compared:N0} answers compared with the original's, {st.Diffs} difference(s) -- {(st.Diffs == 0 ? "PASS" : "FAIL: " + string.Join(" / ", st.DiffLines))}" : ""));
                }
        }
    }
}
#endif
