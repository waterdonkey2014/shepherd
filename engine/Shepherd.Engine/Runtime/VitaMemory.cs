using System.Collections;
using System.Reflection;
namespace Shepherd.Engine.Runtime;

/// <summary>V6 stage 1 (vita/notes/engine-pass-memory.md): engine-side memory cuts under the Vita profile. Runtime-side only:
/// the game DLLs are not modified; every change replaces a game/Unity allocation or reflection scan with an equivalent one.
/// All parts are on under SHEPHERD_PROFILE=vita; SHEPHERD_VITA_MEM=no&lt;part&gt;[,no&lt;part&gt;...] turns parts off (measurement):
///   saves      InitUnifyGlobal's preallocated save streams (16 MiB + 1 MiB) become growable streams with a small start
///              capacity (seeded before the game's BeforeSceneLoad Startup, which then keeps them)
///   newtonsoft DefaultContractResolver.BuiltInConverters: the XmlNode / DataSet / DataTable / Binary / ExpandoObject
///              converters are replaced by a converter that never matches (EntityKeyMemberConverter: a by-name check), so
///              the settings read no longer loads System.Data.Common / System.Private.Xml / ComponentModel.Primitives just to
///              ask "is SettingsData a DataSet?". Only types of those BCL families would serialize differently; the game has none
///   scans      the all-assembly type scans of BlendModes (GetExtensionMap: GetExportedTypes of every loaded assembly) and
///              PostProcessing (RuntimeUtilities.GetAllAssemblyTypes: GetTypes of every loaded assembly) are pre-seeded with
///              the same query over the assemblies that can hold a match (those that reference the scanning library, and
///              the library itself): the result is identical, but Mono no longer creates a MonoClass for every type of every
///              BCL / Unity module that is loaded
///              The same for Febucci TextAnimator's TAnimBuilder.InitializeGlobalDatabase (two GetTypes() scans over every
///              loaded assembly at the first TextAnimator.Awake): replayed before that Awake with the scans narrowed to the
///              assemblies that reference Assembly-CSharp-firstpass -- only on Mono (CoreCLR's full scan throws a
///              ReflectionTypeLoadException there, which the CoreCLR regressions expect) and only while the method's IL hashes to
///              the build this replay was written from (else the game runs its own)
///   builtin    Loader.BuiltinType: GetType("UnityEngine.&lt;name&gt;" and the Unity sub-namespaces) instead of GetTypes() over
///              every UnityEngine* assembly (SHEPHERD_CHECK_BUILTIN=1 compares both at the end of the run)
///   i2         a single-language pack (pack property "language", or SHEPHERD_LANGUAGE): every I2 LanguageSourceData keeps
///              only that language (normally already done by tools/VitaPack --language; this is the guard for sources the
///              baker did not trim)
///   pool       (stage-2 review) after a JsonUtility.FromJson of a big document, the byte buffers of 1 MiB and more that
///              System.Text.Json rented for it are dropped from ArrayPool&lt;byte&gt;.Shared instead of staying pooled (§ pool below)
///   bootgc     (kit #4 review) when the boot's init is done -- RuntimeInitializeOnLoad, level0, AfterSceneLoad, just before the
///              first frame -- the dead stack under it is zeroed and one full collection runs, so the boot's big garbage (the catalog
///              document and its parse buffers) leaves the heap in every boot, not only when no stale stack word points at it (§ boot GC)</summary>
[ProfileOnly]
public static class VitaMemory
{
    static readonly string[] Off = (Environment.GetEnvironmentVariable("SHEPHERD_VITA_MEM") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static bool Part(string name) => Profile.Vita && !Off.Contains("no" + name);
    public static readonly bool Saves = Part("saves"), Newtonsoft = Part("newtonsoft"), Scans = Part("scans"), Builtin = Part("builtin"), I2 = Part("i2"), Pool = Part("pool"), BootGc = Part("bootgc");
    /// <summary>The scene-load transient cuts that live outside this class, switchable for measurement only (they are on
    /// everywhere): noboxes / nokeys (pack decoder: shared primitive boxes / shared shape key lists), noatlas (Loader's sprite-atlas
    /// tree reuse), nolazytext (pack TextAssets keep their bytes), noalpha8 (SDF uploads of pack images through RGBA8).</summary>
    public static readonly bool AtlasCache = !Off.Contains("noatlas"), LazyText = !Profile.Vita || !Off.Contains("nolazytext"), Alpha8 = !Off.Contains("noalpha8");
    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Profile.StartVita (Program.Main, before Boot).</summary>
    public static void Install()
    {
        if (!Profile.Vita) return;
        if (Off.Contains("noboxes")) Shepherd.Assets.PackReader.ShareBoxes = false;
        if (Off.Contains("nokeys")) Shepherd.Assets.PackReader.ShareKeys = false;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) OnLoad(a);
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => OnLoad(e.LoadedAssembly);
    }

    static void OnLoad(Assembly a)
    {
        string n; try { n = a.GetName().Name; } catch { return; }
        try
        {
            if (n == "Newtonsoft.Json" && Newtonsoft) TrimNewtonsoft(a);
            else if (n == "Unity.Postprocessing.Runtime" && Scans) SeedPostProcessing(a);
        }
        catch (Exception e) { Log.Warn($"[vitamem] {n}: {e.GetBaseException().Message}"); }
    }

    // ------------------------------------------------------------------ RuntimeInit hooks

    /// <summary>Boot, before the first RuntimeInitializeOnLoad phase.</summary>
    public static void BeforeRuntimeInit()
    {
        Log.Info($"[vitamem] parts: saves={Saves} newtonsoft={Newtonsoft} scans={Scans} builtin={Builtin} i2={I2} pool={Pool} bootgc={BootGc}; boxes={Shepherd.Assets.PackReader.ShareBoxes} keys={Shepherd.Assets.PackReader.ShareKeys} atlas={AtlasCache} lazytext={LazyText} alpha8={Alpha8} (SHEPHERD_VITA_MEM=no<part> turns one off)");
        if (SpineReadShare.ModeLine() is { } spine) Log.Info(spine);   // kit6 mem L1
        if (SpineLazySkins.ModeLine() is { } lazy) Log.Info(lazy);     // kit #7 spine L2
        if (SpineClipNative.ModeLine() is { } clip) Log.Info(clip);    // kit #7 spine native clipping
        if (Saves) SeedSaveStreams();
    }

    /// <summary>RuntimeInit.Run, just before each [RuntimeInitializeOnLoadMethod] is invoked.</summary>
    public static void BeforeInitMethod(Type t, MethodInfo m)
    {
        if (Scans && t.FullName == "BlendModes.ComponentExtension") { try { SeedBlendModes(t); } catch (Exception e) { Log.Warn($"[vitamem] BlendModes: {e.GetBaseException().Message}"); } }
    }

    // ------------------------------------------------------------------ saves

    public const int SaveStreamCapacity = 64 << 10, OutputStreamCapacity = 16 << 10;
    static FieldInfo _ser, _outp;
    static void SeedSaveStreams()
    {
        try
        {
            var asm = Assembly.Load("Assembly-CSharp");
            var g = asm.GetType("InitUnifyGlobal"); var nc = asm.GetType("NotClosingMemoryStream");
            _ser = g?.GetField("_serializedMemoryStream", S); _outp = g?.GetField("_outputMemoryStream", S);
            if (_ser == null || _outp == null || nc == null) { Log.Warn("[vitamem] saves: InitUnifyGlobal streams not found; the game keeps its 17 MiB of preallocated buffers"); return; }
            // InitUnifyGlobal.Startup only creates a stream that is still null: it keeps these. new MemoryStream(capacity) is
            // expandable, exactly like the game's new MemoryStream(16777216) -- only the starting capacity differs.
            if (_ser.GetValue(null) == null) _ser.SetValue(null, Activator.CreateInstance(nc, new MemoryStream(SaveStreamCapacity)));
            if (_outp.GetValue(null) == null) _outp.SetValue(null, Activator.CreateInstance(nc, new MemoryStream(OutputStreamCapacity)));
            Log.Info($"[vitamem] saves: InitUnifyGlobal._serializedMemoryStream / _outputMemoryStream seeded as growable streams ({SaveStreamCapacity >> 10} / {OutputStreamCapacity >> 10} KiB to start; the game's own are 16 MiB + 1 MiB)");
        }
        catch (Exception e) { Log.Warn($"[vitamem] saves: {e.GetBaseException().Message}"); }
    }
    /// <summary>End-of-run line: how far the save streams grew (0 use = the start capacity).</summary>
    public static string SaveStreamsSummary()
    {
        if (_ser == null) return null;
        static string D(object s) => s is MemoryStream ms ? $"capacity {(ms.GetType().GetProperty("BaseStream")?.GetValue(ms) as MemoryStream ?? ms).Capacity >> 10} KiB" : "null";
        return $"[vitamem] save streams at exit: serialized {D(_ser.GetValue(null))}, output {D(_outp.GetValue(null))}";
    }

    // ------------------------------------------------------------------ Newtonsoft

    static void TrimNewtonsoft(Assembly a)
    {
        var dcr = a.GetType("Newtonsoft.Json.Serialization.DefaultContractResolver");
        var f = dcr?.GetField("BuiltInConverters", S);
        if (f?.GetValue(null) is not Array arr) { Log.Warn("[vitamem] newtonsoft: DefaultContractResolver.BuiltInConverters not found"); return; }
        object never = null; var replaced = new List<string>();
        foreach (var c in arr) if (c?.GetType().Name == "EntityKeyMemberConverter") never = c;
        if (never == null) { Log.Warn("[vitamem] newtonsoft: no EntityKeyMemberConverter to stand in"); return; }
        for (int i = 0; i < arr.Length; i++)
            if (arr.GetValue(i)?.GetType().Name is ("XmlNodeConverter" or "DataSetConverter" or "DataTableConverter" or "BinaryConverter" or "ExpandoObjectConverter") and var name) { replaced.Add(name); arr.SetValue(never, i); }
        Log.Info($"[vitamem] newtonsoft: built-in converters {string.Join(", ", replaced)} -> never-matching stand-in ({arr.Length} slots)");
    }

    // ------------------------------------------------------------------ all-assembly type scans

    /// <summary>The loaded assemblies that can define a match: <paramref name="lib"/> itself and every assembly that
    /// references it by name (a subclass / attribute use of a type in lib needs an AssemblyRef to lib).</summary>
    static IEnumerable<Assembly> Referencing(Assembly[] all, Assembly lib)
    {
        var name = lib.GetName().Name;
        foreach (var a in all)
        {
            if (a == lib) { yield return a; continue; }
            AssemblyName[] refs; try { refs = a.GetReferencedAssemblies(); } catch { continue; }
            foreach (var r in refs) if (r.Name == name) { yield return a; break; }
        }
    }

    /// <summary>BlendModes.ComponentExtension.GetExtensionMap: the same map (types marked [ExtendedComponent], keyed by the
    /// extended component type, in assembly order) built from the assemblies that reference Elringus.BlendModes.Runtime.</summary>
    static void SeedBlendModes(Type ce)
    {
        var map = ce.GetField("extensionsMap", S);
        if (map == null || map.GetValue(null) != null) return;
        var attr = ce.GetNestedType("ExtendedComponentAttribute", BindingFlags.NonPublic | BindingFlags.Public);
        var prop = attr?.GetProperty("ExtendedComponentType", I);
        if (attr == null || prop == null) { Log.Warn("[vitamem] BlendModes: ExtendedComponentAttribute not found; the game scans every assembly"); return; }
        var all = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray();
        var d = new Dictionary<Type, List<Type>>(); int scanned = 0;
        foreach (var a in Referencing(all, ce.Assembly))
        {
            scanned++;
            foreach (var t in a.GetExportedTypes())
            {
                if (!t.IsDefined(attr, false)) continue;
                var key = (Type)prop.GetValue(t.GetCustomAttribute(attr, false));
                if (!d.TryGetValue(key, out var l)) d.Add(key, l = new List<Type>());
                l.Add(t);
            }
        }
        map.SetValue(null, d);
        Log.Info($"[vitamem] BlendModes extension map seeded: {d.Count} component types, {d.Values.Sum(l => l.Count)} extensions, from {scanned} of {all.Length} assemblies");
    }

    /// <summary>PostProcessing RuntimeUtilities.m_AssemblyTypes: the game's value is GetAssemblies() (taken at its first use)
    /// .SelectMany(GetTypes), re-enumerated on every use. Its only reader is GetAllTypesDerivedFrom&lt;T&gt; (T in
    /// Unity.Postprocessing.Runtime), so the same sequence restricted to the assemblies that can hold a subclass is
    /// equivalent: the snapshot is taken at the first enumeration and kept, like the game's.</summary>
    static void SeedPostProcessing(Assembly pp)
    {
        var ru = pp.GetType("UnityEngine.Rendering.PostProcessing.RuntimeUtilities");
        var f = ru?.GetField("m_AssemblyTypes", S);
        if (f == null) { Log.Warn("[vitamem] PostProcessing: RuntimeUtilities.m_AssemblyTypes not found"); return; }
        if (f.GetValue(null) != null) return;
        f.SetValue(null, new LazyTypes(pp));
        Log.Info("[vitamem] PostProcessing RuntimeUtilities.m_AssemblyTypes seeded (assemblies that reference Unity.Postprocessing.Runtime)");
    }
    sealed class LazyTypes : IEnumerable<Type>
    {
        readonly Assembly _lib; Assembly[] _snap;
        public LazyTypes(Assembly lib) => _lib = lib;
        public IEnumerator<Type> GetEnumerator()
        {
            if (_snap == null)
            {
                var all = AppDomain.CurrentDomain.GetAssemblies();
                _snap = Referencing(all, _lib).ToArray();
                Log.Info($"[vitamem] PostProcessing type scan: {_snap.Length} of {all.Length} assemblies ({string.Join(", ", _snap.Select(a => a.GetName().Name))})");
            }
            foreach (var a in _snap)
            {
                Type[] ts; try { ts = a.GetTypes(); } catch { ts = Array.Empty<Type>(); }   // the game's own catch-all
                foreach (var t in ts) yield return t;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ------------------------------------------------------------------ Febucci TextAnimator's effect database

    static bool _febucciDone = !Scans;
    /// <summary>The replay is a port of a third-party method body, so it is derived code: it is not compiled into Shepherd.Engine
    /// (which ships in every VPK) but into the owner-local assembly Shepherd.Engine.Derived (source engine/Derived/, git-ignored;
    /// vita/shepherd/build-derived.sh -> runtime/derived/; staged into the owner's managed/ folder by stage-owner.sh and into the
    /// sim's Data/Managed by sim.sh stage). It is loaded by name like a game DLL (Vita: the host's preload hook finds it in
    /// ux0:data/shepherd/managed; Mac: Program's Resolving handler). Absent: the game runs its own all-assembly scan (correct;
    /// costs malloc and assemblies on Mono, vita/notes/engine-pass-memory.md). CoreCLR keeps the game's scan without looking.</summary>
    static void FebucciReplay(Assembly fp, bool check)
    {
        if (!MonoCompat.MonoInterpCompat.IsMono) { Log.Info("[vitamem] Febucci: CoreCLR run -- the game's own all-assembly scan (and its ReflectionTypeLoadException) is kept"); return; }
        MethodInfo run;
        try { run = Assembly.Load(new AssemblyName("Shepherd.Engine.Derived")).GetType("Shepherd.Engine.Derived.FebucciReplay", true).GetMethod("Run", BindingFlags.Static | BindingFlags.Public); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException)
        {
            Log.Info($"[vitamem] Febucci: no owner-local Shepherd.Engine.Derived ({e.GetType().Name}) -- the game runs its own all-assembly scan");
            return;
        }
        Log.Info($"[vitamem] Febucci: replay from {run.DeclaringType.Assembly.Location}");
        run.Invoke(null, new object[] { fp, check, (Func<Assembly[], Assembly, IEnumerable<Assembly>>)Referencing, (Func<MethodBase[], ulong>)IlHash,
                                        (Action<string>)Log.Info, (Action<string>)Log.Warn });
    }
    /// <summary>Lifecycle.Awake, before every Awake (vita): replay the database set-up before the first TextAnimator's own.</summary>
    public static void BeforeAwake(object mb)
    {
        if (_febucciDone) return;
        for (var t = mb.GetType(); t != null; t = t.BaseType)
        {
            if (t.FullName != "Febucci.UI.TextAnimator") continue;
            _febucciDone = true;
            try { FebucciReplay(t.Assembly, Environment.GetEnvironmentVariable("SHEPHERD_CHECK_FEBUCCI") == "1"); }
            catch (Exception e) { Log.Warn($"[vitamem] Febucci: {e.GetBaseException().Message}"); }
            return;
        }
    }


    static ulong IlHash(params MethodBase[] ms)
    {
        ulong h = 14695981039346656037;
        foreach (var m in ms) foreach (var b in m?.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>()) { h ^= b; h *= 1099511628211; }
        return h;
    }


    // ------------------------------------------------------------------ Loader.BuiltinType

    static readonly string[] UnityNamespaces = { "UnityEngine", "UnityEngine.Rendering", "UnityEngine.U2D", "UnityEngine.Tilemaps", "UnityEngine.Video",
        "UnityEngine.Audio", "UnityEngine.Playables", "UnityEngine.Animations", "UnityEngine.AI", "UnityEngine.UI", "UnityEngine.EventSystems",
        "UnityEngine.Timeline", "UnityEngine.Experimental.Rendering", "UnityEngine.SceneManagement" };
    static readonly Dictionary<string, Type> FastBuiltins = new();
    /// <summary>The type Loader.BuiltinType's scan would find for a Unity native class name (first match in assembly order of
    /// a non-abstract UnityEngine.Object subclass named <paramref name="name"/> in a UnityEngine* assembly), by name lookups.</summary>
    public static Type BuiltinType(string name, Type uobject)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!a.GetName().Name.StartsWith("UnityEngine")) continue;
            foreach (var ns in UnityNamespaces)
            {
                var t = a.GetType(ns + "." + name, false);
                if (t != null && t.Name == name && uobject.IsAssignableFrom(t) && !t.IsAbstract) return FastBuiltins[name] = t;
            }
        }
        return FastBuiltins[name] = null;
    }
    /// <summary>SHEPHERD_CHECK_BUILTIN=1: at the end of the run, the name lookups against the original GetTypes() scan.</summary>
    public static void CheckBuiltins(Func<string, Type> scan)
    {
        if (Environment.GetEnvironmentVariable("SHEPHERD_CHECK_BUILTIN") != "1" || FastBuiltins.Count == 0) return;
        int same = 0; var diff = new List<string>();
        foreach (var (n, t) in FastBuiltins) { var s = scan(n); if (s == t) same++; else diff.Add($"{n}: lookup {t?.FullName ?? "null"} vs scan {s?.FullName ?? "null"}"); }
        Log.Info($"[vitamem] builtin check: {same} of {FastBuiltins.Count} names identical to the GetTypes() scan{(diff.Count > 0 ? "; DIFFERENT: " + string.Join("; ", diff) : "")}");
    }

    // ------------------------------------------------------------------ I2 localization

    /// <summary>The single language of this build: the pack's "language" property (tools/VitaPack --language), or
    /// SHEPHERD_LANGUAGE. An I2 language Name ("English") or Code ("en").</summary>
    public static string Language => _lang ??= (Environment.GetEnvironmentVariable("SHEPHERD_LANGUAGE") is { Length: > 0 } l ? l : Assets.PackAssets.Property("language")) is { } v && v != "all" ? v : "";
    static string _lang;
    static FieldInfo _mSource, _mTerms, _mLanguages, _tLang, _tFlags, _tTouch, _lName, _lCode;
    static int _sources, _trimmed; static long _termsSeen;

    /// <summary>Loader, right after a MonoBehaviour / ScriptableObject is populated.</summary>
    public static void AfterPopulate(object managed, Type type)
    {
        if (!I2 || Language.Length == 0) return;
        if (type.FullName is not ("I2.Loc.LanguageSourceAsset" or "I2.Loc.LanguageSource")) return;
        try { TrimI2(managed, type); } catch (Exception e) { Log.Warn($"[i2] trim {type.Name}: {e.GetBaseException().Message}"); }
    }

    static void TrimI2(object owner, Type type)
    {
        _mSource ??= type.GetField("mSource", I);
        var src = _mSource?.GetValue(owner); if (src == null) return;
        var st = src.GetType();
        _mTerms ??= st.GetField("mTerms", I); _mLanguages ??= st.GetField("mLanguages", I);
        if (_mTerms?.GetValue(src) is not IList terms || _mLanguages?.GetValue(src) is not IList langs) return;
        _sources++;
        int keep = -1;
        for (int i = 0; i < langs.Count; i++)
        {
            var ld = langs[i]; _lName ??= ld.GetType().GetField("Name", I); _lCode ??= ld.GetType().GetField("Code", I);
            if (string.Equals(_lName.GetValue(ld) as string, Language, StringComparison.OrdinalIgnoreCase) || string.Equals(_lCode.GetValue(ld) as string, Language, StringComparison.OrdinalIgnoreCase)) { keep = i; break; }
        }
        string what = $"{type.Name} '{(owner as UnityEngine.Object)?.name}'";
        if (keep < 0) { Log.Warn($"[i2] {what}: language '{Language}' not among its {langs.Count} languages; left as it is"); return; }
        _termsSeen += terms.Count;
        if (langs.Count == 1) { Log.Info($"[i2] {what}: 1 language ({_lName.GetValue(langs[0])}), {terms.Count} terms: already single-language (the pack's)"); return; }
        int n = langs.Count;
        foreach (var term in terms)
        {
            if (term == null) continue;
            var tt = term.GetType(); _tLang ??= tt.GetField("Languages", I); _tFlags ??= tt.GetField("Flags", I); _tTouch ??= tt.GetField("Languages_Touch", I);
            if (_tLang.GetValue(term) is string[] ls) _tLang.SetValue(term, new[] { keep < ls.Length ? ls[keep] : null });
            if (_tFlags.GetValue(term) is byte[] fs) _tFlags.SetValue(term, new[] { keep < fs.Length ? fs[keep] : (byte)0 });
            if (_tTouch?.GetValue(term) is string[] ts && ts.Length > 0) _tTouch.SetValue(term, new[] { keep < ts.Length ? ts[keep] : null });
        }
        var kept = langs[keep];
        for (int i = langs.Count - 1; i >= 0; i--) if (i != keep) langs.RemoveAt(i);
        _trimmed++;
        Log.Info($"[i2] {what}: {n} languages -> 1 ({_lName.GetValue(kept)}), {terms.Count} terms trimmed by the engine (the pack was not single-language)");
    }
    // ------------------------------------------------------------------ pool

    /// <summary>JsonIcalls.FromJson parses with System.Text.Json's JsonDocument, which rents its UTF-8 copy and its token database
    /// from ArrayPool&lt;byte&gt;.Shared and returns both on Dispose. For the Addressables catalog (ContentCatalogData, 9.9 M chars,
    /// RuntimeInit) that is two 16 MiB arrays: the first goes to the main thread's per-thread slot, the second pushes the first
    /// into a per-core partition. The pool drops them only from its Gen2GcCallback trim on the finalizer thread, by the Mac's (or
    /// the Vita arena's) memory load at that moment and after 10-60 s, so the menu's live heap held 0 or 16 MiB of them run to run
    /// (and on the Vita, where stage 2 left ~no major GCs at the idle menu, possibly 32 MiB for good). A document of at least this
    /// many chars is the trigger; nothing else in the boot pools byte arrays of 1 MiB or more.</summary>
    public const int BigJsonChars = 256 << 10;
    const int LargeBucket = 16;   // SharedArrayPool bucket i holds arrays of 16 << i elements: 16 = 1 MiB
    static FieldInfo _tls, _buckets, _partitions, _arrays, _count; static bool _poolBroken; static int _poolReleases, _poolArrays; static long _poolBytes;

    /// <summary>JsonIcalls.FromJson, after its document is disposed (Pool and json.Length >= BigJsonChars): drops every array of
    /// 1 MiB or more from this thread's slots and from the per-core partitions of ArrayPool&lt;byte&gt;.Shared -- what the pool's own
    /// trim does under high memory pressure (Array.Clear of the slots; partitions emptied under their own lock), now, for the
    /// large buckets only. The pool's contents never change a result: only when the arrays become garbage.</summary>
    public static void ReleasePooledBytes(int chars)
    {
        var (n, bytes) = PooledLargeBytes(drop: true); if (n < 0) return;
        _poolReleases++; _poolArrays += n; _poolBytes += bytes;
        Log.Info($"[vitamem] pool: a JsonUtility document of {chars} chars: dropped {n} pooled byte array(s) of >= 1 MiB ({bytes / 1048576.0:F2} MiB) from ArrayPool<byte>.Shared");
    }
    /// <summary>The byte arrays of 1 MiB or more in ArrayPool&lt;byte&gt;.Shared (this thread's slots + every per-core partition), and
    /// with <paramref name="drop"/> removes them. (-1, 0) when the pool's layout is not the one this was written for (logged once).
    /// SimStats' SHEPHERD_POOL_PROBE=&lt;frames&gt; counts without dropping (diagnostic).</summary>
    public static (int n, long bytes) PooledLargeBytes(bool drop)
    {
        if (_poolBroken) return (-1, 0);
        try
        {
            var pool = System.Buffers.ArrayPool<byte>.Shared; var t = pool.GetType();
            _tls ??= t.GetField("t_tlsBuckets", S); _buckets ??= t.GetField("_buckets", I);
            if (_tls == null || _buckets == null) { _poolBroken = true; Log.Warn($"[vitamem] pool: {t.FullName} has no t_tlsBuckets/_buckets (another BCL build?); left as it is"); return (-1, 0); }
            int n = 0; long bytes = 0;
            if (_tls.GetValue(null) is Array tls)
                for (int i = LargeBucket; i < tls.Length; i++)
                {
                    var slot = tls.GetValue(i); var arr = slot?.GetType().GetField("Array", I)?.GetValue(slot) as byte[];
                    if (arr != null) { n++; bytes += arr.Length; }
                    if (drop) Array.Clear(tls, i, 1);
                }
            if (_buckets.GetValue(pool) is Array buckets)
                for (int i = LargeBucket; i < buckets.Length; i++)
                {
                    var parts = buckets.GetValue(i); if (parts == null) continue;
                    _partitions ??= parts.GetType().GetField("_partitions", I);
                    if (_partitions?.GetValue(parts) is not Array ps) continue;
                    foreach (var p in ps)
                    {
                        if (p == null) continue;
                        _arrays ??= p.GetType().GetField("_arrays", I); _count ??= p.GetType().GetField("_count", I);
                        if (_arrays == null || _count == null) continue;
                        lock (p)   // Partition.TryPush / TryPop / Trim take this lock
                        {
                            if (_arrays.GetValue(p) is byte[][] arrs) { foreach (var x in arrs) if (x != null) { n++; bytes += x.Length; } if (drop) Array.Clear(arrs); }
                            if (drop) _count.SetValue(p, 0);
                        }
                    }
                }
            return (n, bytes);
        }
        catch (Exception e) { _poolBroken = true; Log.Warn($"[vitamem] pool: {e.GetBaseException().Message}; left as it is"); return (-1, 0); }
    }
    /// <summary>JsonIcalls.FromJson, BEFORE a document of at least <see cref="BigJsonChars"/> chars is parsed (device kit #2 review): that
    /// parse is the boot's GC-heap peak -- the document string (the Addressables catalog: ~10 M chars, a 19 MiB string), JsonDocument's
    /// rented UTF-8 copy and token database (2 x 16 MiB) and the filled object graph are live together -- and byte arrays of 1 MiB or
    /// more still pooled from earlier work (Shepherd.Assets' block-decompression buffers, an earlier document's) sat on top of it until
    /// the drop after the parse. They leave ArrayPool&lt;byte&gt;.Shared now, and one full GC frees them and the boot's garbage before the
    /// peak instead of at the max-heap-size cap (where the GL apps' smaller arenas met it). The pool's contents never change a result.</summary>
    public static void BeforeBigDocument(string json)
    {
        int chars = json.Length;
        _bigDoc = new WeakReference(json); _bigDocChars = chars;   // (boot GC: is the document string still reachable at the first frame?)
        var (n, bytes) = PooledLargeBytes(drop: true);
        long before = GC.GetTotalMemory(false); var sw = System.Diagnostics.Stopwatch.StartNew();
        GC.Collect();
        long after = GC.GetTotalMemory(false);
        _preArrays += Math.Max(n, 0); _preBytes += Math.Max(bytes, 0); _preDocs++;
        Log.Info($"[vitamem] pool: before a JsonUtility document of {chars} chars: dropped {Math.Max(n, 0)} pooled byte array(s) of >= 1 MiB ({Math.Max(bytes, 0) / 1048576.0:F2} MiB); full GC {sw.ElapsedMilliseconds} ms, GC heap {before / 1048576.0:F1} -> {after / 1048576.0:F1} MiB");
    }
    static int _preDocs, _preArrays; static long _preBytes;

    // ------------------------------------------------------------------ boot GC

    /// <summary>Boot.BootGame, after RuntimeInit's AfterSceneLoad phase and before the first frame (and before the host's CP0 checkpoint,
    /// which then shows the result): the stale stack under this point cleared, then one full collection (kit #4 review,
    /// vita/CHANGELOG.md 0.4). sgen scans thread stacks conservatively: any word that looks like a pointer into an object keeps it
    /// alive. The boot's init -- RuntimeInit -> the game's interpreted Addressables code -> JsonUtility.FromJson -> System.Text.Json
    /// on the catalog (a 9.9 M-char, ~19 MiB document string and 2 x 16 MiB parse buffers) -- ran in the stack area under BootGame's
    /// frame, and its dead frames keep their old words; the frames of every later GC call sit in that same area with slots they never
    /// write. In about 1 boot in 6 (vlx at the device budgets: a boot came up 16.0 or 19.0 MiB heavier at this point, exactly one
    /// parse buffer or the document string, still "live" after a full collection; kit #3's play.prev.txt on the device: CP0 +16,384
    /// KiB) one such stale word kept the object alive through the first frames, every later major ran at another point, and the
    /// Main Menu arrived with ~20 MiB more GC heap: on the 124 MiB play apps the menu load then peaked at 136-140 of the 145.9 MiB
    /// arena and a 2-4 MiB request found no hole (a red end at or after the menu, a crash, or an OOM in a later load). Whether the stale
    /// word survived depended on the frame layout of whatever ran there (an extra local in this method changed it). With 256 KiB of
    /// that dead area zeroed first (the Mono thread's stack is >= 4 MiB; the boot is a few dozen frames deep; nothing live is below
    /// this frame) the collection frees them in most boots and the frame loop starts from one live heap (vlx: 42.5 MiB) with sgen's
    /// next majors on one schedule (vlx: the arena's peak at the menu load 114-126 MiB, was 126-131 and 136-140). NOT in every boot
    /// (kit #4 revision 2's checks: 4 of 78 vlx boots still kept one; a 2 MiB clearing 1 of 28): a stale word elsewhere -- another
    /// thread's stack, a live frame above this one -- can still hold it. The log says which ("STILL REACHABLE", or B ~58 / ~61 MiB). Cost: one major of
    /// ~45-65 MiB (vlx 30-45 ms; the device's boot majors of that size took 0.1-0.15 s in kit #3). It frees only garbage.</summary>
    public static void AfterBootInit()
    {
        JsonBake.AtFirstFrame();   // device kit #5 B-2: a catalog redirect the game never took is disarmed; the bake's summary line
        if (!BootGc) return;
        long before = GC.GetTotalMemory(false); int majors = GC.CollectionCount(GC.MaxGeneration);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ClearStackBelow();
        GC.Collect();
        sw.Stop();
        string doc = _bigDoc == null ? "" : _bigDoc.IsAlive ? $"; the {_bigDocChars}-char JSON document string is STILL REACHABLE" : $"; the {_bigDocChars}-char JSON document string is gone";
        Log.Info($"[vitamem] boot GC: the stale stack below cleared ({ClearKiB} KiB) and one full collection before the first frame, {sw.ElapsedMilliseconds} ms: GC heap in use {before / 1048576.0:F1} -> {GC.GetTotalMemory(false) / 1048576.0:F1} MiB{doc} ({majors} major collections before it; SHEPHERD_VITA_MEM=nobootgc turns it off)");
    }
    static WeakReference _bigDoc; static int _bigDocChars;
    const int ClearKiB = 256;
    static byte _sink;
    /// <summary>Zeroes <see cref="ClearKiB"/> KiB of stack under the caller's frame and returns (see <see cref="AfterBootInit"/>).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static unsafe void ClearStackBelow()
    {
        const int n = ClearKiB * 1024;
        byte* p = stackalloc byte[n];
        new Span<byte>(p, n).Clear();
        _sink = p[n - 1];
    }

    public static string PoolSummary() => _poolReleases == 0 && _preDocs == 0 ? null : $"[vitamem] pool: before {_preDocs} big JsonUtility document(s): {_preArrays} pooled byte array(s), {_preBytes / 1048576.0:F2} MiB dropped; after {_poolReleases}: {_poolArrays} array(s), {_poolBytes / 1048576.0:F2} MiB dropped";

    public static string I2Summary() => _sources == 0 ? null : $"[i2] language '{Language}': {_sources} source(s), {_termsSeen} terms, {_trimmed} trimmed at load by the engine";
}
