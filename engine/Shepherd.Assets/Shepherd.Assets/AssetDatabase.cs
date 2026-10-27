using System.Buffers.Binary;

namespace Shepherd.Assets;

/// <summary>
/// Ties bundles, serialized files and their externals together. Everything is loaded lazily and cached;
/// externals of the form "archive:/CAB-x/CAB-x" are located through an index of every bundle's directory
/// (built on first miss: header + blocks-info only, ~3.4k bundles).
/// </summary>
public sealed class AssetDatabase : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SerializedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BundleFile> _bundles = new(StringComparer.Ordinal);            // abs path -> bundle
    private readonly Dictionary<string, List<SerializedFile>> _bundleFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BundleFile> _nodeOwner = new(StringComparer.OrdinalIgnoreCase); // node name -> loaded bundle
    private Dictionary<string, string> _nodeIndex;                                                    // node name -> bundle path (all bundles)
    private readonly Dictionary<(SerializedFile, int), SerializedFile> _extCache = new();
    private readonly HashSet<string> _missingExternals = new(StringComparer.OrdinalIgnoreCase);

    public string DataDir { get; }
    /// <summary>DATA/StreamingAssets/aa</summary>
    public string AddressablesDir { get; }
    /// <summary>DATA/StreamingAssets/aa/StandaloneOSX</summary>
    public string BundleDir { get; }
    public IEnumerable<SerializedFile> LoadedFiles => _files.Values;
    public IEnumerable<BundleFile> LoadedBundles => _bundles.Values;
    public IReadOnlyCollection<string> MissingExternals => _missingExternals;
    /// <summary>Count of Resolve() calls that could not be satisfied.</summary>
    public int UnresolvedCount => _unresolved;
    private int _unresolved;

    public AssetDatabase(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        AddressablesDir = Path.Combine(DataDir, "StreamingAssets", "aa");
        BundleDir = Path.Combine(AddressablesDir, "StandaloneOSX");
        if (!Directory.Exists(BundleDir))
        {
            // any other platform folder under aa/
            var alt = Directory.Exists(AddressablesDir)
                ? Directory.GetDirectories(AddressablesDir).FirstOrDefault(d => Directory.EnumerateFiles(d, "*.bundle").Any())
                : null;
            if (alt != null) BundleDir = alt;
        }
    }

    private AddressablesCatalog _catalog;
    public AddressablesCatalog Catalog => _catalog ??= AddressablesCatalog.Load(Path.Combine(AddressablesDir, "catalog.json"), AddressablesDir);

    // ------------------------------------------------------------------ bundles

    public BundleFile OpenBundle(string bundlePath)
    {
        bundlePath = Path.GetFullPath(bundlePath);
        lock (_lock)
        {
            if (_bundles.TryGetValue(bundlePath, out var b)) return b;
            b = BundleFile.Open(bundlePath);
            _bundles[bundlePath] = b;
            foreach (var n in b.Nodes) _nodeOwner.TryAdd(n.Name, b);
            return b;
        }
    }

    /// <summary>Opens the bundle and loads every serialized file in it; returns the primary one
    /// (for scene bundles: the scene file, not its ".sharedAssets" companion). Idempotent.</summary>
    public SerializedFile LoadBundle(string bundlePath)
    {
        var all = LoadBundleFiles(bundlePath);
        return all.FirstOrDefault(f => !f.Name.EndsWith(".sharedAssets", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
    }

    /// <summary>All serialized files of a bundle (scene bundles have two per scene: "CAB-x" (the scene) and "CAB-x.sharedAssets").</summary>
    public IReadOnlyList<SerializedFile> LoadBundleFiles(string bundlePath)
    {
        bundlePath = Path.GetFullPath(bundlePath);
        lock (_lock)
        {
            if (_bundleFiles.TryGetValue(bundlePath, out var list)) return list;
            if (Pack != null)
            {
                var pl = Pack.BundleFiles(Path.GetFileName(bundlePath));
                // a bundle whose AssetBundle header did not make it into the pack cannot be opened from it (no container,
                // no scene table): report it and, on the Mac, open the game's own copy of its files instead
                if (pl != null && pl.Any(pf => pf.PackSourceCount(ClassIds.AssetBundle) > pf.Objects.Count(o => o.ClassId == ClassIds.AssetBundle)))
                {
                    PackMiss?.Invoke($"bundle {Path.GetFileName(bundlePath)}: its AssetBundle header is not in the pack");
                    if (PackFallback && File.Exists(bundlePath))
                    {
                        var ob = OpenBundle(bundlePath);
                        list = ob.Nodes.Where(n => n.IsSerializedFile).Select(n => OriginalFile(n.Name)).Where(x => x != null).ToList();
                        _bundleFiles[bundlePath] = list; return list;
                    }
                }
                if (pl != null)
                {
                    list = new List<SerializedFile>(pl);
                    foreach (var pf in pl) _files.TryAdd(pf.Name, pf);
                    _bundleFiles[bundlePath] = list;
                    AssetTrace.BundleLoaded?.Invoke(bundlePath, list);
                    return list;
                }
                PackMiss?.Invoke($"bundle {Path.GetFileName(bundlePath)}");
                if (!PackFallback) { _bundleFiles[bundlePath] = list = new List<SerializedFile>(); return list; }
            }
            var b = OpenBundle(bundlePath);
            list = new List<SerializedFile>();
            foreach (var n in b.Nodes)
            {
                if (!n.IsSerializedFile) continue;
                if (_files.TryGetValue(n.Name, out var existing)) { list.Add(existing); continue; }
                var sf = LazyFiles ? LoadLazyNode(b, n) : SerializedFile.Load(n.Name, b.ReadNode(n.Name));
                sf.Bundle = b;
                _files[n.Name] = sf;
                list.Add(sf);
            }
            _bundleFiles[bundlePath] = list;
            AssetTrace.BundleLoaded?.Invoke(bundlePath, list);
            return list;
        }
    }

    /// <summary>The bundle path that contains a node ("CAB-x", "CAB-x.resS", "CAB-x.sharedAssets", ...), or null.
    /// Builds the index of all bundles' directories on first use (~0.3 s).</summary>
    public string FindBundleForNode(string nodeName)
    {
        lock (_lock)
        {
            if (_nodeOwner.TryGetValue(nodeName, out var b)) return b.Path;
            EnsureNodeIndex();
            return _nodeIndex.TryGetValue(nodeName, out var p) ? p : null;
        }
    }

    public IReadOnlyDictionary<string, string> NodeIndex { get { lock (_lock) { EnsureNodeIndex(); return _nodeIndex; } } }

    private void EnsureNodeIndex()
    {
        if (_nodeIndex != null) return;
        var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(BundleDir))
        {
            var paths = Directory.GetFiles(BundleDir, "*.bundle");
            var results = new (string path, string[] nodes)[paths.Length];
            Parallel.For(0, paths.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    using var bf = BundleFile.Open(paths[i]);
                    results[i] = (paths[i], bf.Nodes.Select(n => n.Name).ToArray());
                }
                catch { results[i] = (paths[i], Array.Empty<string>()); }
            });
            foreach (var (path, nodes) in results)
                foreach (var n in nodes) idx.TryAdd(n, path);
        }
        _nodeIndex = idx;
    }

    // ------------------------------------------------------------------ files

    /// <summary>Loaded file by name, or loads it: loose files (level0, sharedassets0.assets, unity default resources, ...)
    /// or a bundle node (CAB-xxx) found through the bundle index.</summary>
    public SerializedFile GetFile(string cabOrFileName)
    {
        if (string.IsNullOrEmpty(cabOrFileName)) return null;
        string name = cabOrFileName[(cabOrFileName.LastIndexOf('/') + 1)..];
        lock (_lock)
        {
            if (_files.TryGetValue(name, out var f)) return f;
            if (_missingExternals.Contains(name)) return null;
            if (Pack != null)
            {
                var pf = Pack.GetFile(name);
                if (pf != null) { _files[name] = pf; return pf; }
                if (Pack.IsAbsentFile(name)) { _missingExternals.Add(name); AssetTrace.FileAbsent?.Invoke(name); return null; }
                PackMiss?.Invoke($"file {name}");
                if (!PackFallback) { _missingExternals.Add(name); return null; }
            }

            foreach (var cand in LooseCandidates(cabOrFileName, name))
            {
                if (!File.Exists(cand)) continue;
                f = LazyFiles ? LoadLazyLoose(name, cand) : SerializedFile.Load(name, File.ReadAllBytes(cand), Path.GetDirectoryName(cand));
                _files[name] = f;
                if (!f.EnableTypeTree && AutoHarvestFallbacks) f.MissingTypeTreeHandler = sf => EnsureFallbackTypeTrees(sf.Types);
                return f;
            }

            var bundle = FindBundleForNode(name);
            if (bundle != null)
            {
                LoadBundleFiles(bundle);
                if (_files.TryGetValue(name, out f)) return f;
            }
            _missingExternals.Add(name);
            AssetTrace.FileAbsent?.Invoke(name);
            return null;
        }
    }

    // ------------------------------------------------------------------ Vita pack (V3 part 2, vita/notes/v3b-pack.md)

    /// <summary>When set, serialized files and bundles come from this pack (same names, PathIds and externals). Anything the
    /// pack lacks is reported through <see cref="PackMiss"/> and then, only with <see cref="PackFallback"/>, loaded from the
    /// game data as before (Mac); without it, a miss resolves to nothing (Vita).</summary>
    public PackReader Pack { get; set; }
    public bool PackFallback { get; set; }
    /// <summary>"file X" / "bundle X" / "object X #pathId (from Y)" / "stream ...": one call per miss (callers dedupe).</summary>
    public Action<string> PackMiss { get; set; }
    private readonly Dictionary<string, SerializedFile> _origFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>With <see cref="PackFallback"/>: the game's own copy of a file the pack has only part of; else null.</summary>
    public SerializedFile PackOriginal(string name) => Pack != null && PackFallback ? OriginalFile(name) : null;

    /// <summary>Pack fallback (Mac only): the game's own copy of a file the pack has only part of.</summary>
    private SerializedFile OriginalFile(string name)
    {
        lock (_lock)
        {
            if (_origFiles.TryGetValue(name, out var f)) return f;
            foreach (var cand in LooseCandidates(name, name))
                if (File.Exists(cand)) { f = LoadLazyLoose(name, cand); break; }
            if (f == null && FindBundleForNode(name) is { } bp)
            {
                var b = OpenBundle(bp); var n = b.Nodes.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (n != null) { f = LoadLazyNode(b, n); f.Bundle = b; }
            }
            if (f != null && !f.EnableTypeTree && AutoHarvestFallbacks) f.MissingTypeTreeHandler = sf => EnsureFallbackTypeTrees(sf.Types);
            return _origFiles[name] = f;
        }
    }

    private IEnumerable<string> LooseCandidates(string original, string name)
    {
        if (Path.IsPathRooted(original)) yield return original;
        if (original.StartsWith("archive:", StringComparison.OrdinalIgnoreCase)) yield break;
        yield return Path.Combine(DataDir, name);
        yield return Path.Combine(DataDir, "Resources", name);
        // macOS players keep "unity default resources" next to Data/
        var parent = Path.GetDirectoryName(DataDir);
        if (parent != null) yield return Path.Combine(parent, name);
    }

    /// <summary>V3 memory diet: parse only each file's metadata on load and read the whole-file buffer on the first
    /// payload access; <see cref="ReleaseIdle"/> drops buffers nothing has read lately (they are re-read on demand).
    /// Default false = whole files are read on load and kept (the original behaviour).</summary>
    public bool LazyFiles { get; set; }

    private static SerializedFile LoadLazyNode(BundleFile b, BundleNode n)
    {
        var head = b.ReadNodeRange(n.Name, 0, Math.Min(48, n.Size));
        long len = Math.Min(SerializedFile.MetadataLength(head), n.Size);
        var sf = SerializedFile.LoadLazy(n.Name, b.ReadNodeRange(n.Name, 0, len), n.Size, () => b.ReadNode(n.Name), readRange: (off, size) => b.ReadNodeRange(n.Name, off, size));
        return sf;
    }

    private static SerializedFile LoadLazyLoose(string name, string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        long size = fs.Length;
        byte[] Prefix(long n)
        {
            var buf = GC.AllocateUninitializedArray<byte>(checked((int)Math.Min(n, size)));
            fs.Position = 0; int read = 0;
            while (read < buf.Length) { int k = fs.Read(buf, read, buf.Length - read); if (k <= 0) throw new EndOfStreamException(path); read += k; }
            return buf;
        }
        var meta = Prefix(SerializedFile.MetadataLength(Prefix(48)));
        return SerializedFile.LoadLazy(name, meta, size, () => File.ReadAllBytes(path), Path.GetDirectoryName(path), readRange: (off, n) => ReadFileRange(path, off, n));
    }

    private static byte[] ReadFileRange(string path, long offset, int size)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        if (offset + size > fs.Length) throw new EndOfStreamException($"{path}: range {offset}+{size} past end ({fs.Length})");
        var buf = GC.AllocateUninitializedArray<byte>(size);
        fs.Position = offset; int read = 0;
        while (read < size) { int k = fs.Read(buf, read, size - read); if (k <= 0) throw new EndOfStreamException(path); read += k; }
        return buf;
    }

    /// <summary>Drop the whole-file buffers of lazy files and the block caches of bundles not read since
    /// <paramref name="olderThan"/> (a <see cref="SerializedFile.Clock"/> value). Returns (files released, bytes).</summary>
    public (int files, long bytes) ReleaseIdle(long olderThan)
    {
        int n = 0; long bytes = 0;
        lock (_lock)
        {
            foreach (var f in _files.Values)
                if (f.LastUse < olderThan && f.IsResident) { int b = f.Release(); if (b > 0) { n++; bytes += b; } }
            foreach (var b in _bundles.Values)
                if (b.LastUse < olderThan) bytes += b.TrimCache();
        }
        return (n, bytes);
    }

    /// <summary>Whole-file buffers currently resident (all loaded files) and bundle block-cache bytes.</summary>
    public (long fileBytes, int residentFiles, long blockCache) ResidentBytes()
    {
        lock (_lock)
        {
            long fb = 0, bc = 0; int rf = 0;
            foreach (var f in _files.Values) { int r = f.ResidentBytes; if (r > 0) { fb += r; rf++; } }
            foreach (var b in _bundles.Values) bc += b.CachedBytes;
            return (fb, rf, bc);
        }
    }

    /// <summary>Register an already-loaded file (e.g. one loaded by hand) so PPtrs into it resolve.</summary>
    public void AddFile(SerializedFile f) { lock (_lock) _files[f.Name] = f; }

    public SerializedFile GetExternalFile(SerializedFile from, int fileId)
    {
        if (fileId == 0) return from;
        lock (_lock)
        {
            if (_extCache.TryGetValue((from, fileId), out var f)) return f;
            if (fileId < 0 || fileId > from.Externals.Count) return null;
            var ext = from.Externals[fileId - 1];
            f = GetFile(ext.PathName);
            _extCache[(from, fileId)] = f;
            return f;
        }
    }

    /// <summary>Follow a PPtr. Returns (null, null) when the pointer is null or its target file/object is unavailable
    /// (use <see cref="GetExternalFile"/> to tell a missing file from a missing object).</summary>
    public (SerializedFile file, ObjectInfo obj) Resolve(SerializedFile from, PPtr p)
    {
        if (p.IsNull || p.PathId == 0) return (null, null);
        var f = GetExternalFile(from, p.FileId);
        var o = f?.GetObject(p.PathId);
        if (o == null && f?.Pack != null && !f.Pack.IsAbsentObject(f, p.PathId))
        {
            PackMiss?.Invoke($"object {f.Name} #{p.PathId} (from {from?.Name})");
            if (PackFallback && OriginalFile(f.Name) is { } orig && orig.GetObject(p.PathId) is { } oo) return (orig, oo);
        }
        if (o == null) { Interlocked.Increment(ref _unresolved); return (null, null); }
        return (f, o);
    }

    /// <summary>Resolve + decode in one step (null when unavailable or when no type tree exists).</summary>
    public (SerializedFile file, ObjectInfo obj, TypeTreeObject data) ResolveRead(SerializedFile from, PPtr p)
    {
        var (f, o) = Resolve(from, p);
        if (o == null) return (f, null, null);
        if (!f.HasTree(o)) return (f, o, null);
        return (f, o, TypeTreeReader.Read(f, o));
    }

    // ------------------------------------------------------------------ scenes

    public sealed record SceneLoadResult(string ScenePath, SerializedFile Scene, SerializedFile SharedAssets,
        string SceneBundlePath, IReadOnlyList<string> Closure);

    /// <summary>
    /// Load an Addressables scene: opens its bundle closure (all serialized files parsed when <paramref name="eager"/>)
    /// and finds the scene file through the scene bundle's AssetBundle.m_SceneHashes (scene path -> "CAB-…";
    /// the companion "CAB-….sharedAssets" holds the scene's own assets).
    /// </summary>
    public SceneLoadResult LoadScene(string sceneKey, bool eager = true)
    {
        var locs = Catalog.Locate(sceneKey);
        if (locs.Count == 0) throw new KeyNotFoundException($"'{sceneKey}' is not in the Addressables catalog");
        string scenePath = locs.FirstOrDefault(l => !l.IsBundle)?.InternalId ?? sceneKey;
        var closure = Catalog.BundleClosure(sceneKey);
        foreach (var bp in closure)
        {
            if (eager) LoadBundleFiles(bp); else OpenBundle(bp);
        }
        foreach (var bp in closure)
        {
            if (Pack != null ? !LoadBundleFiles(bp).Any(f => f.Name.EndsWith(".sharedAssets", StringComparison.Ordinal))
                             : !OpenBundle(bp).Nodes.Any(n => n.Name.EndsWith(".sharedAssets", StringComparison.Ordinal))) continue;
            foreach (var f in LoadBundleFiles(bp))
            {
                foreach (var o in f.ObjectsOfClass(ClassIds.AssetBundle))
                {
                    var ab = TypeTreeReader.Read(f, o);
                    if (!ab.Bool("m_IsStreamedSceneAssetBundle") || ab.List("m_SceneHashes") is not { } hashes) continue;
                    foreach (var h in hashes.OfType<TypeTreeObject>())
                    {
                        if (!string.Equals(h.Str("first"), scenePath, StringComparison.OrdinalIgnoreCase)) continue;
                        string cab = h.Str("second");
                        var scene = GetFile(cab) ?? throw new InvalidDataException($"scene file {cab} missing from {bp}");
                        return new SceneLoadResult(scenePath, scene, GetFile(cab + ".sharedAssets"), bp, closure);
                    }
                }
            }
        }
        throw new InvalidDataException($"no scene bundle in the closure of '{sceneKey}' lists {scenePath} in m_SceneHashes");
    }

    // ------------------------------------------------------------------ streamed data

    /// <summary>Bytes of a streamed resource (.resS / .resource): "archive:/CAB-x/CAB-x.resS" inside bundles,
    /// or a path relative to the loose file's directory.</summary>
    public byte[] ReadStreamedData(SerializedFile from, string path, long offset, long size)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("empty stream path");
        AssetTrace.StreamRead?.Invoke(from, path, offset, size);
        if (from?.Pack != null)
        {   // a baked pack inlines every stream the engine reads (mesh vertex data) and stores texture images itself
            PackMiss?.Invoke($"stream {path} @{offset}+{size} (from {from.Name})");
            if (!PackFallback) throw new FileNotFoundException($"streamed data '{path}' is not in the pack (from {from.Name})");
        }
        string name = path[(path.LastIndexOf('/') + 1)..];
        BundleFile bundle = null;
        if (from?.Bundle != null && from.Bundle.HasNode(name)) bundle = from.Bundle;
        else
        {
            var bp = FindBundleForNode(name);
            if (bp != null) bundle = OpenBundle(bp);
        }
        if (bundle != null) return bundle.ReadNodeRange(name, offset, size);

        var candidates = new List<string>();
        if (from?.ResSearchDir != null) { candidates.Add(Path.Combine(from.ResSearchDir, path)); candidates.Add(Path.Combine(from.ResSearchDir, name)); }
        candidates.Add(Path.Combine(DataDir, path));
        candidates.Add(Path.Combine(DataDir, name));
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            using var fs = new FileStream(c, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            if (offset + size > fs.Length) throw new EndOfStreamException($"{c}: range {offset}+{size} past end ({fs.Length})");
            var buf = GC.AllocateUninitializedArray<byte>(checked((int)size));
            fs.Position = offset;
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) throw new EndOfStreamException(c);
                read += n;
            }
            return buf;
        }
        throw new FileNotFoundException($"streamed data '{path}' not found (from {from?.Name})");
    }

    // ------------------------------------------------------------------ scripts / helpers

    /// <summary>MonoScript (Namespace, ClassName, Assembly) of a MonoBehaviour, or null if unresolved.
    /// Works on stripped files too (reads the fixed MonoBehaviour header).</summary>
    public MonoScriptInfo GetMonoScript(SerializedFile file, ObjectInfo mb)
    {
        var scriptPtr = ReadMonoBehaviourScriptPPtr(file, mb);
        var (sf, so) = Resolve(file, scriptPtr);
        if (so == null) return null;
        lock (_lock)
        {
            if (_scriptCache.TryGetValue((sf, so.PathId), out var cached)) return cached;
        }
        MonoScriptInfo info = null;
        if (sf.HasTree(so))
        {
            var t = TypeTreeReader.Read(sf, so);
            info = new MonoScriptInfo(t.Str("m_ClassName"), t.Str("m_Namespace"), t.Str("m_AssemblyName"), so.PathId, sf.Name);
        }
        lock (_lock) _scriptCache[(sf, so.PathId)] = info;
        return info;
    }

    private readonly Dictionary<(SerializedFile, long), MonoScriptInfo> _scriptCache = new();

    /// <summary>m_Script of a MonoBehaviour without a full type-tree read:
    /// layout is PPtr m_GameObject (12) + UInt8 m_Enabled (aligned to 4) + PPtr m_Script.</summary>
    public static PPtr ReadMonoBehaviourScriptPPtr(SerializedFile file, ObjectInfo mb)
    {
        if (file.Pack != null) { AssetTrace.ObjectRead?.Invoke(file, mb, AssetTrace.Access.Script); return file.Pack.ScriptPPtr(mb); }
        var prev = AssetTrace.Current; AssetTrace.Current = AssetTrace.Access.Script;
        try
        {
            var d = file.GetObjectBytes(mb);
            if (d.Length < 28) return default;
            bool be = file.BigEndianData;
            int fid = be ? BinaryPrimitives.ReadInt32BigEndian(d[16..]) : BinaryPrimitives.ReadInt32LittleEndian(d[16..]);
            long pid = be ? BinaryPrimitives.ReadInt64BigEndian(d[20..]) : BinaryPrimitives.ReadInt64LittleEndian(d[20..]);
            return new PPtr(fid, pid);
        }
        finally { AssetTrace.Current = prev; }
    }

    /// <summary>For stripped loose files (level0, globalgamemanagers.assets, unity default resources, ...): the first time
    /// one of their objects needs a missing type tree, harvest fallbacks for all of the file's classes from the bundles
    /// (builds <see cref="TypeTreeIndex"/> once, ~1.5-2 s). Default true.</summary>
    public bool AutoHarvestFallbacks { get; set; } = true;

    /// <summary>Make sure fallback type trees exist for these types' classes. Uses the bundle type-tree index
    /// (built once, in parallel, from every bundle's metadata) and prefers a tree whose OldTypeHash matches.
    /// Returns the class IDs that could not be found.</summary>
    public IReadOnlyCollection<int> EnsureFallbackTypeTrees(IEnumerable<SerializedType> types)
    {
        lock (_lock)
        {
            var missing = new List<int>();
            var typeList = types.ToList();
            RegisterExactMonoBehaviourTrees(typeList.Where(t => t.ClassId == ClassIds.MonoBehaviour && t.OldTypeHash != null));
            foreach (var t in typeList)
            {
                if (SerializedFile.HasFallbackTypeTree(t.ClassId)) continue;
                var idx = TypeTreeIndex;
                if (!idx.TryGetValue(t.ClassId, out var variants)) { if (!missing.Contains(t.ClassId)) missing.Add(t.ClassId); continue; }
                var pick = variants.FirstOrDefault(v => v.Hash == t.OldTypeHashHex) ?? variants[0];
                var nodes = t.ClassId == ClassIds.MonoBehaviour ? MonoBehaviourHeader(pick.Nodes) : pick.Nodes;
                if (nodes == null) continue;
                SerializedFile.RegisterFallbackTypeTree(t.ClassId, nodes);
                HarvestLog.Add((t.ClassId, $"{Path.GetFileName(pick.Bundle)} hash={pick.Hash}{(pick.Hash == t.OldTypeHashHex ? " (hash match)" : t.ClassId == ClassIds.MonoBehaviour ? " (header only)" : " (HASH DIFFERS)")}"));
            }
            return missing;
        }
    }

    /// <summary>Stripped MonoBehaviour types carry the script layout hash; when a bundle has a MonoBehaviour type with
    /// the same hash, its full type tree reads the stripped object completely (all script fields).</summary>
    private void RegisterExactMonoBehaviourTrees(IEnumerable<SerializedType> mbTypes)
    {
        var want = mbTypes.Select(t => t.OldTypeHashHex).Where(h => !SerializedFile.HasFallbackTypeTree(ClassIds.MonoBehaviour, h)).ToHashSet();
        if (want.Count == 0) return;
        _ = TypeTreeIndex; // builds _mbHashToBundle
        foreach (var grp in want.Where(_mbHashToBundle.ContainsKey).GroupBy(h => _mbHashToBundle[h]))
        {
            using var b = BundleFile.Open(grp.Key);
            var hashes = grp.ToHashSet();
            foreach (var n in b.Nodes.Where(n => n.IsSerializedFile))
                foreach (var t in LoadMetadataOnly(b, n).Types)
                    if (t.ClassId == ClassIds.MonoBehaviour && t.Nodes is { Count: > 0 } && hashes.Remove(t.OldTypeHashHex))
                    {
                        SerializedFile.RegisterFallbackTypeTree(ClassIds.MonoBehaviour, t.OldTypeHashHex, t.Nodes);
                        ExactScriptTrees++;
                    }
        }
    }

    /// <summary>Number of stripped MonoBehaviour layouts matched to a full type tree from a bundle (by OldTypeHash).</summary>
    public int ExactScriptTrees { get; private set; }
    private readonly Dictionary<string, string> _mbHashToBundle = new();

    public IReadOnlyCollection<int> EnsureFallbackTypeTrees(IEnumerable<int> classIds) =>
        EnsureFallbackTypeTrees(classIds.Select(c => new SerializedType { ClassId = c }));

    public sealed record TypeTreeVariant(string Hash, List<TypeTreeNode> Nodes, string Bundle);
    private Dictionary<int, List<TypeTreeVariant>> _typeTreeIndex;

    /// <summary>classId -> distinct type trees (by OldTypeHash) found in the bundles. MonoBehaviour keeps one variant.</summary>
    public IReadOnlyDictionary<int, List<TypeTreeVariant>> TypeTreeIndex
    {
        get
        {
            lock (_lock)
            {
                if (_typeTreeIndex != null) return _typeTreeIndex;
                var paths = Directory.Exists(BundleDir) ? Directory.GetFiles(BundleDir, "*.bundle") : Array.Empty<string>();
                Array.Sort(paths, StringComparer.Ordinal);
                // (classId, hash) -> (bundle index, nodes); dedupe while scanning, keep the first bundle in path order
                var found = new System.Collections.Concurrent.ConcurrentDictionary<(int, string), (int bi, List<TypeTreeNode> nodes)>();
                var mbHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
                Parallel.For(0, paths.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    try
                    {
                        using var b = BundleFile.Open(paths[i]);
                        foreach (var n in b.Nodes)
                        {
                            if (!n.IsSerializedFile) continue;
                            var sf = LoadMetadataOnly(b, n);
                            foreach (var t in sf.Types)
                            {
                                if (t.Nodes is not { Count: > 0 }) continue;
                                // MonoBehaviour trees are per-script: one shared key for the header; script layouts are
                                // only remembered as hash -> bundle and loaded on demand
                                if (t.ClassId == ClassIds.MonoBehaviour) mbHashes.AddOrUpdate(t.OldTypeHashHex, i, (_, old) => Math.Min(old, i));
                                var key = (t.ClassId, t.ClassId == ClassIds.MonoBehaviour ? "" : t.OldTypeHashHex);
                                found.AddOrUpdate(key, (i, t.Nodes), (_, old) => old.bi <= i ? old : (i, t.Nodes));
                            }
                        }
                    }
                    catch { }
                });
                foreach (var kv in mbHashes) _mbHashToBundle[kv.Key] = paths[kv.Value];
                var idx = new Dictionary<int, List<TypeTreeVariant>>();
                foreach (var kv in found.OrderBy(x => x.Value.bi))
                {
                    var (cid, hash) = kv.Key;
                    if (!idx.TryGetValue(cid, out var list)) idx[cid] = list = new List<TypeTreeVariant>();
                    string h = cid == ClassIds.MonoBehaviour ? null : hash;
                    list.Add(new TypeTreeVariant(h, kv.Value.nodes, paths[kv.Value.bi]));
                }
                return _typeTreeIndex = idx;
            }
        }
    }

    /// <summary>What auto-harvesting registered: (classId, "bundle hash=...").</summary>
    public List<(int classId, string source)> HarvestLog { get; } = new();

    /// <summary>Parse only the metadata of a serialized-file node (cheap: no object payloads are read).</summary>
    public static SerializedFile LoadMetadataOnly(BundleFile b, BundleNode n)
    {
        var head = b.ReadNodeRange(n.Name, 0, Math.Min(48, n.Size));
        long len = Math.Min(SerializedFile.MetadataLength(head), n.Size);
        return SerializedFile.LoadMetadata(n.Name, b.ReadNodeRange(n.Name, 0, len));
    }

    private static List<TypeTreeNode> MonoBehaviourHeader(List<TypeTreeNode> nodes)
    {
        int nameIdx = nodes.FindIndex(n => n.Level == 1 && n.Name == "m_Name");
        if (nameIdx < 0) return null;
        int end = nameIdx + 1;
        while (end < nodes.Count && nodes[end].Level > 1) end++;
        return nodes.GetRange(0, end);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var b in _bundles.Values) b.Dispose();
            _bundles.Clear();
        }
    }
}

public sealed record MonoScriptInfo(string ClassName, string Namespace, string AssemblyName, long PathId, string FileName)
{
    public string FullName => string.IsNullOrEmpty(Namespace) ? ClassName : $"{Namespace}.{ClassName}";
    public override string ToString() => FullName;
}
