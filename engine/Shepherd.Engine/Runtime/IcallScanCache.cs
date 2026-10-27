using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #5 B4 (track boot, vita/notes/kit5-boot.md): IcallRegistry's hook list without the reflection scan. The registry
/// found its ~1,270 [Icall] methods by asking every engine type for its static methods (GetTypes + GetMethods over ~2,500 statics +
/// IsDefined + the attribute decode: ~330 of ~400-460 ms of "[Icall] scan + bind" under vlx, ~0.6 s of the device's 873 ms). The list --
/// each hook method's metadata token and name and its key, in bind order -- depends only on the engine build (its module's MVID) and
/// the profile, so it is written once (owner folder cache/icalls/scan-&lt;MVID&gt;-&lt;profile&gt;.bin) and read back by later starts: each token
/// resolved (Module.ResolveMethod) and checked against the stored name; then the same Bind calls in the same order (the same table, the
/// same "no such hook" lines). The scan also LOADS assemblies (GetTypes / GetMethods resolve the engine's signatures: e.g. the Unity web
/// request module, whose DownloadHandlerFile properties AutoProps then finds): the file lists them in load order and a cached start
/// loads them first, so every later step sees the same assemblies (the icall table dump is identical). Engine-only data (assembly
/// names, method tokens, hook keys; no game content). Vita profile only.
/// A damaged file is replaced by the next scan's list (a WRN at that start).
/// SHEPHERD_ICALL_SCAN = unset/1 (the cache) | 0 (scan every start, no file) | check (scan, and compare with the file: an end line).</summary>
[ProfileOnly]
public static class IcallScanCache
{
    static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_ICALL_SCAN") ?? "").Trim().ToLowerInvariant();
    static bool Off => Setting is "0" or "off";
    static bool Check => Setting == "check";
    const string Magic = "SHPDISCN"; const int Version = 1;
    static string PathFor(Module m) => System.IO.Path.Combine(Program.Root, "cache", "icalls", "scan-" + m.ModuleVersionId.ToString("N") + "-" + (Profile.Name.Length > 0 ? Profile.Name : "default") + ".bin");

    /// <summary>The bind list from the file, or null (no file / another build / damaged / a token that does not resolve to its method;
    /// switched off; check mode): the caller scans.</summary>
    public static List<(MethodInfo m, string key)> Load(Module mod)
    {
        if (Off || Check) return null;
        var path = PathFor(mod);
        var sw = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(path)) return null;
            var b = File.ReadAllBytes(path);
            var why = Decode(b, mod, out var list, out var asms, load: true);
            if (why != null) { _pending = "[Icall] scan cache " + System.IO.Path.GetFileName(path) + " refused (" + why + "): scanned"; _replace = !_decodeLoaded; return null; }
            _pending = "[Icall] hook list from cache/icalls/" + System.IO.Path.GetFileName(path) + ": " + list.Count.ToString() + " hooks read + resolved and " + asms.Count.ToString() + " assemblies loaded as the scan did, in " + sw.ElapsedMilliseconds.ToString() + " ms (no type scan; SHEPHERD_ICALL_SCAN=0 scans)";
            return list;
        }
        catch (Exception e) { _pending = $"[Icall] scan cache: {e.GetBaseException().Message}: scanned"; _replace = !_decodeLoaded; return null; }
    }
    static string _pending;
    /// <summary>The file was refused before any of its assemblies was loaded (a damaged file: the checksum covers every byte), so this
    /// start's scan sees the assembly loads a clean scan sees and its list replaces the file (kit #5 review: a damaged file used to stay,
    /// and every later start warned and scanned). A file refused after its assemblies were loaded stays (the scan would miss them).</summary>
    static bool _replace, _decodeLoaded;

    /// <summary>After the binds: the pending line of <see cref="Load"/>; after a scan (<paramref name="binds"/> not null) the file is written
    /// when there is none, or (check mode) compared with the scan.</summary>
    /// <summary>Right after a scan: the assemblies it loaded, in load order.</summary>
    public static void ScanDone(Assembly[] before)
    {
        var had = new HashSet<Assembly>(before ?? Array.Empty<Assembly>());
        _loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !had.Contains(a)).Select(a => a.FullName).ToList();
    }
    static List<string> _loaded;
    public static void After(Module mod, List<(MethodInfo m, string key)> binds)
    {
        if (_pending != null) { if (_pending.Contains("refused") || _pending.Contains(": scanned")) Log.Warn(_pending); else Log.Info(_pending); _pending = null; }
        if (Off || binds == null) return;
        var path = PathFor(mod);
        var loaded = _loaded ?? new List<string>();
        try
        {
            if (Check && File.Exists(path))
            {
                var why = Decode(File.ReadAllBytes(path), mod, out var cached, out var asms, load: false);
                bool same = why == null && cached.Count == binds.Count && asms.SequenceEqual(loaded);
                for (int i = 0; same && i < binds.Count; i++) same = cached[i].m == binds[i].m && cached[i].key == binds[i].key;
                Log.Info("[Icall] scan cache check: the scan's " + binds.Count.ToString() + " hooks and " + loaded.Count.ToString() + " assembly loads " + (same ? "are" : "are NOT") + " the file's (" + (why ?? (cached.Count.ToString() + " hooks, " + asms.Count.ToString() + " loads")) + "), in order");
                return;
            }
            if (File.Exists(path) && !_replace) return;
            var err = Render.GxpCache.WriteAtomic(path, Encode(mod, binds, loaded));
            Log.Info(err == null ? (_replace ? "[Icall] hook list rewritten to cache/icalls/" : "[Icall] hook list written to cache/icalls/") + System.IO.Path.GetFileName(path) + " (" + binds.Count.ToString() + " hooks, " + loaded.Count.ToString() + " assemblies the scan loaded: " + string.Join(", ", loaded.Select(n => n.Split(',')[0])) + "): the next starts skip the scan" : "[Icall] the hook list could not be written (" + err + ")");
        }
        catch (Exception e) { Log.Warn($"[Icall] scan cache: {e.GetBaseException().Message}"); }
    }

    // "SHPDISCN" | u32 version | 16 bytes MVID | u32 + UTF-8 profile | u32 assemblies, each u32 + UTF-8 full name (load order) | u32 hooks |
    // per hook: i32 token, u32 + UTF-8 name, u32 + UTF-8 key | u64 IcallManifest.ContentHash of every byte before it
    static byte[] Encode(Module mod, List<(MethodInfo m, string key)> binds, List<string> loaded)
    {
        var ms = new MemoryStream();
        void U32(int v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); ms.Write(t); }
        void Str(string s) { var x = Encoding.UTF8.GetBytes(s); U32(x.Length); ms.Write(x); }
        ms.Write(Encoding.ASCII.GetBytes(Magic)); U32(Version); ms.Write(mod.ModuleVersionId.ToByteArray()); Str(Profile.Name);
        U32(loaded.Count); foreach (var a in loaded) Str(a);
        U32(binds.Count);
        foreach (var (m, key) in binds) { U32(m.MetadataToken); Str(m.Name); Str(key); }
        Span<byte> h = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(h, IcallManifest.ContentHash(ms.GetBuffer().AsSpan(0, (int)ms.Length))); ms.Write(h);
        return ms.ToArray();
    }
    /// <summary>null = the file is this build's (list and the assembly names filled); with <paramref name="load"/> the listed assemblies are
    /// loaded first, in order (what the scan did), then the tokens resolved.</summary>
    static string Decode(byte[] b, Module mod, out List<(MethodInfo m, string key)> list, out List<string> asms, bool load)
    {
        list = null; asms = new List<string>();
        if (b.Length < 8 + 4 + 16 + 4 + 4 + 8 || Encoding.ASCII.GetString(b, 0, 8) != Magic) return "not a scan cache file";
        if (IcallManifest.ContentHash(b.AsSpan(0, b.Length - 8)) != BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b.Length - 8))) return "its checksum does not match (damaged)";
        int pos = 8, end = b.Length - 8;
        int U32() { if (pos + 4 > end) throw new FormatException("truncated"); int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos)); pos += 4; return v; }
        string Str() { int n = U32(); if (n < 0 || n > end - pos) throw new FormatException("truncated string"); var s = Encoding.UTF8.GetString(b, pos, n); pos += n; return s; }
        try
        {
            if (U32() != Version) return "another version";
            if (pos + 16 > end || !b.AsSpan(pos, 16).SequenceEqual(mod.ModuleVersionId.ToByteArray())) return "made by another engine build";
            pos += 16;
            if (Str() != Profile.Name) return "made under another profile";
            int na = U32(); if (na < 0 || na > 1000) return "assembly count";
            for (int i = 0; i < na; i++) asms.Add(Str());
            if (load) { _decodeLoaded = asms.Count > 0; foreach (var a in asms) Assembly.Load(new AssemblyName(a)); }
            int n = U32(); if (n < 0 || n > 100000) return "hook count";
            var l = new List<(MethodInfo, string)>(n);
            for (int i = 0; i < n; i++)
            {
                int token = U32(); string name = Str(), key = Str();
                if (mod.ResolveMethod(token) is not MethodInfo m || m.Name != name || !m.IsStatic) return "token 0x" + token.ToString("x8") + " is not " + name;
                l.Add((m, key));
            }
            if (pos != end) return "bytes after the list";
            list = l;
            return null;
        }
        catch (Exception e) { return e.GetBaseException().Message; }
    }
}
