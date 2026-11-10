using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Shepherd.Assets;

public sealed record CatalogLocation(string InternalId, string ProviderId, string ResolvedPath, string ResourceType,
    IReadOnlyList<CatalogLocation> Dependencies)
{
    public bool IsBundle => ProviderId?.EndsWith("AssetBundleProvider", StringComparison.Ordinal) == true;
    /// <summary>Primary key (address) of the entry, when known.</summary>
    public string PrimaryKey { get; init; }
    /// <summary>Provider data (for bundles: AssetBundleRequestOptions JSON), when present.</summary>
    public string Data { get; init; }
    public override string ToString() => $"{InternalId} [{ResourceType}] via {ProviderId?.Split('.').Last()}";
}

/// <summary>
/// Addressables content catalog (catalog.json, the 1.x "m_KeyDataString" binary layout).
/// </summary>
public sealed class AddressablesCatalog
{
    public const string RuntimePathToken = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";

    private readonly string[] _internalIds;
    private readonly string[] _providerIds;
    private readonly string[] _resourceTypes;
    private readonly object[] _keys;
    private readonly int[][] _buckets;          // per key: entry indices
    private readonly Entry[] _entries;
    private readonly byte[] _extra;
    private readonly Dictionary<object, int> _keyIndex = new();
    private readonly Dictionary<int, CatalogLocation> _locCache = new();
    private readonly Dictionary<string, string> _resolved = new();

    public string RuntimePath { get; }
    public string LocatorId { get; }
    public IReadOnlyList<string> InternalIds => _internalIds;
    public IReadOnlyList<string> ProviderIds => _providerIds;
    public IReadOnlyList<object> Keys => _keys;
    public int EntryCount => _entries.Length;

    private readonly record struct Entry(int InternalId, int Provider, int DepKey, int DepHash, int Data, int PrimaryKey, int ResourceType);

    private AddressablesCatalog(string runtimePath, string locatorId, string[] internalIds, string[] providerIds, string[] resourceTypes,
        object[] keys, int[][] buckets, Entry[] entries, byte[] extra)
    {
        RuntimePath = runtimePath; LocatorId = locatorId;
        _internalIds = internalIds; _providerIds = providerIds; _resourceTypes = resourceTypes;
        _keys = keys; _buckets = buckets; _entries = entries; _extra = extra;
        for (int i = 0; i < keys.Length; i++)
            if (keys[i] != null) _keyIndex.TryAdd(keys[i], i);
    }

    public static AddressablesCatalog Load(string catalogJsonPath, string runtimePath)
    {
        using var fs = File.OpenRead(catalogJsonPath);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;

        string[] Strings(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();

        var internalIds = Strings("m_InternalIds");
        var prefixes = Strings("m_InternalIdPrefixes");
        if (prefixes.Length > 0)
        {
            // "<prefixIndex>#<suffix>" compression of internal ids
            for (int i = 0; i < internalIds.Length; i++)
            {
                var s = internalIds[i];
                int h = s.IndexOf('#');
                if (h > 0 && int.TryParse(s.AsSpan(0, h), out int p) && p >= 0 && p < prefixes.Length)
                    internalIds[i] = prefixes[p] + s[(h + 1)..];
            }
        }
        var providerIds = Strings("m_ProviderIds");
        var resourceTypes = root.TryGetProperty("m_resourceTypes", out var rts)
            ? rts.EnumerateArray().Select(x => x.GetProperty("m_ClassName").GetString()).ToArray()
            : Array.Empty<string>();

        byte[] B64(string name) => root.TryGetProperty(name, out var e) && e.GetString() is { Length: > 0 } s
            ? Convert.FromBase64String(s) : Array.Empty<byte>();
        var keyData = B64("m_KeyDataString");
        var bucketData = B64("m_BucketDataString");
        var entryData = B64("m_EntryDataString");
        var extraData = B64("m_ExtraDataString");

        // buckets
        int bucketCount = BinaryPrimitives.ReadInt32LittleEndian(bucketData);
        var buckets = new int[bucketCount][];
        var keys = new object[bucketCount];
        int off = 4;
        for (int i = 0; i < bucketCount; i++)
        {
            int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(bucketData.AsSpan(off));
            int n = BinaryPrimitives.ReadInt32LittleEndian(bucketData.AsSpan(off + 4));
            off += 8;
            var arr = new int[n];
            for (int j = 0; j < n; j++) arr[j] = BinaryPrimitives.ReadInt32LittleEndian(bucketData.AsSpan(off + j * 4));
            off += n * 4;
            buckets[i] = arr;
            keys[i] = ReadObject(keyData, dataOffset);
        }
        int keyCount = BinaryPrimitives.ReadInt32LittleEndian(keyData);
        if (keyCount != bucketCount) throw new InvalidDataException($"catalog: {keyCount} keys vs {bucketCount} buckets");

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(entryData);
        if (4 + entryCount * 28 != entryData.Length) throw new InvalidDataException($"catalog: entry data size mismatch ({entryData.Length} for {entryCount})");
        var entries = new Entry[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            int b = 4 + i * 28;
            int I(int k) => BinaryPrimitives.ReadInt32LittleEndian(entryData.AsSpan(b + k * 4));
            entries[i] = new Entry(I(0), I(1), I(2), I(3), I(4), I(5), I(6));
        }

        string locator = root.TryGetProperty("m_LocatorId", out var li) ? li.GetString() : null;
        return new AddressablesCatalog(runtimePath, locator, internalIds, providerIds, resourceTypes, keys, buckets, entries, extraData);
    }

    /// <summary>SerializationUtilities.ReadObjectFromByteArray.</summary>
    private static object ReadObject(byte[] d, int i)
    {
        byte type = d[i++];
        switch (type)
        {
            case 0: { int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i)); return StringPool.Intern(Encoding.ASCII.GetString(d, i + 4, len), StringPool.Site.Catalog); }   // kit #13 basec: pooled
            case 1: { int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i)); return StringPool.Intern(Encoding.Unicode.GetString(d, i + 4, len), StringPool.Site.Catalog); }
            case 2: return BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i));
            case 3: return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i));
            case 4: return BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i));
            case 5: { int len = d[i]; return "hash128:" + Encoding.ASCII.GetString(d, i + 1, len); }
            case 6: { int len = d[i]; return "type:" + Encoding.ASCII.GetString(d, i + 1, len); }
            case 7:
            {
                int al = d[i++]; string asm = Encoding.ASCII.GetString(d, i, al); i += al;
                int cl = d[i++]; string cls = Encoding.ASCII.GetString(d, i, cl); i += cl;
                int jl = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i)); i += 4;
                string json = Encoding.Unicode.GetString(d, i, jl);
                return new CatalogJsonObject(asm, cls, json);
            }
            default: return null;
        }
    }

    public sealed record CatalogJsonObject(string Assembly, string ClassName, string Json)
    {
        public override string ToString() => $"{ClassName}: {Json}";
    }

    public string ResolveInternalId(string id)
    {
        if (id == null) return null;
        if (_resolved.TryGetValue(id, out var r)) return r;
        r = id.Replace(RuntimePathToken, RuntimePath);
        if (r != id) r = Path.GetFullPath(r);
        _resolved[id] = r;
        return r;
    }

    private CatalogLocation GetLocation(int entryIndex, HashSet<int> stack)
    {
        if (_locCache.TryGetValue(entryIndex, out var cached)) return cached;
        var e = _entries[entryIndex];
        var deps = new List<CatalogLocation>();
        if (e.DepKey >= 0 && e.DepKey < _buckets.Length && stack.Add(entryIndex))
        {
            foreach (int di in _buckets[e.DepKey])
                if (di != entryIndex) deps.Add(GetLocation(di, stack));
            stack.Remove(entryIndex);
        }
        string id = e.InternalId >= 0 && e.InternalId < _internalIds.Length ? _internalIds[e.InternalId] : null;
        var loc = new CatalogLocation(
            id,
            e.Provider >= 0 && e.Provider < _providerIds.Length ? _providerIds[e.Provider] : null,
            ResolveInternalId(id),
            e.ResourceType >= 0 && e.ResourceType < _resourceTypes.Length ? _resourceTypes[e.ResourceType] : null,
            deps)
        {
            PrimaryKey = e.PrimaryKey >= 0 && e.PrimaryKey < _keys.Length ? _keys[e.PrimaryKey]?.ToString() : null,
            Data = e.Data >= 0 && e.Data < _extra.Length ? ReadObject(_extra, e.Data)?.ToString() : null,
        };
        _locCache[entryIndex] = loc;
        return loc;
    }

    /// <summary>All locations registered under <paramref name="key"/> (address, asset path, label or GUID string).</summary>
    public IReadOnlyList<CatalogLocation> Locate(object key)
    {
        if (key == null) return Array.Empty<CatalogLocation>();
        if (!_keyIndex.TryGetValue(key, out int ki))
        {
            if (key is Guid g) key = g.ToString("N");
            if (key is string s)
            {
                // tolerate "a1b2..." GUIDs in dashed / upper-case form, and case differences in paths
                string norm = s.Replace("-", "").ToLowerInvariant();
                if (!_keyIndex.TryGetValue(norm, out ki))
                {
                    var hit = _keyIndex.Keys.OfType<string>().FirstOrDefault(k => string.Equals(k, s, StringComparison.OrdinalIgnoreCase));
                    if (hit == null || !_keyIndex.TryGetValue(hit, out ki)) return Array.Empty<CatalogLocation>();
                }
            }
            else return Array.Empty<CatalogLocation>();
        }
        var stack = new HashSet<int>();
        return _buckets[ki].Select(ei => GetLocation(ei, stack)).ToList();
    }

    /// <summary>Absolute bundle paths needed to load <paramref name="key"/> (own bundle + transitive deps), deduped, in dependency order.</summary>
    public IReadOnlyList<string> BundleClosure(string key)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<CatalogLocation>(ReferenceEqualityComparer.Instance);
        void Walk(CatalogLocation l)
        {
            if (!visited.Add(l)) return;
            if (l.IsBundle && l.ResolvedPath != null && seen.Add(l.ResolvedPath)) result.Add(l.ResolvedPath);
            foreach (var d in l.Dependencies) Walk(d);
        }
        foreach (var l in Locate(key)) Walk(l);
        return result;
    }

    /// <summary>All string keys (addresses / paths / GUIDs / labels).</summary>
    public IEnumerable<string> StringKeys => _keys.OfType<string>();
}
