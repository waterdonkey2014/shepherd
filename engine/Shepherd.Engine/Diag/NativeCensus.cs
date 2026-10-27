using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Diag;

/// <summary>V4 native-resolution census -> runtime/logs/native-resolve.txt. On by default under SHEPHERD_PROFILE=vita
/// (SHEPHERD_NATIVE_CENSUS=0 turns it off); on the Mac default only with SHEPHERD_NATIVE_CENSUS=1. It never changes what
/// gets loaded: every hook only records and returns "not handled", so the runtime's normal probing (and Program.cs's
/// ResolvingUnmanagedDll map) still decides.
/// What it sees:
/// 1. every managed P/Invoke binding: a DllImportResolver on each assembly as it loads (CoreCLR calls it on the first call of
///    each P/Invoke, before probing) -> library + entry point (from the P/Invoke frame on the stack) + assembly + first frame.
///    Not hookable: System.Private.CoreLib (the runtime binds its QCalls/System.Native itself) and assemblies that already
///    have a resolver (MonoInterpCompat under the Mono interpreter).
/// 2. ResolvingUnmanagedDll fallbacks (Program.cs's game-plugin map) and whether they loaded.
/// 3. the dyld image list at exit: ground truth for what native code is mapped in the process (DYLD_PRINT_LIBRARIES
///    equivalent), grouped so game plugins (fmodstudio, resonanceaudio, Rewired_MacOS, steam_api, ...) stand out.
/// Vita rule: managed code may bind only SDL3 and libSystem*/libc-level BCL natives; anything else is a bug to fix or stub.</summary>
[ProfileOnly]
public static class NativeCensus
{
    public static bool Enabled { get; private set; }
    sealed class Bind { public string Lib, Entry, Asm, Caller; public int Frame, Count; }
    static readonly object Gate = new();
    static readonly Dictionary<(string, string, string), Bind> Binds = new();
    static readonly List<string> Notes = new();
    static readonly List<string> Fallbacks = new();
    static readonly List<(int frame, string name)> AsmLoads = new();
    /// <summary>SHEPHERD_ASMLOAD_STACKS=1 (V6): keep the managed stack that triggered each assembly load (section 5), to see which
    /// code drags which assembly in (metadata the Vita keeps resident).</summary>
    static readonly bool AsmStacks = Environment.GetEnvironmentVariable("SHEPHERD_ASMLOAD_STACKS") == "1";
    static readonly Dictionary<string, string> AsmLoadStacks = new();
    static readonly HashSet<Assembly> Hooked = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    static int Frame => Runtime.Time.FrameCount;

    /// <summary>SHEPHERD_NATIVE_CENSUS_STACKS=&lt;substring&gt;[,...] (V6): for bindings whose library name contains one of these,
    /// keep the managed call stack of the first binding (CoreCLR binds per entry point, so per entry point there): which game
    /// code reaches crypto / compression natives. Section 1c.</summary>
    static readonly string[] StackLibs = (Environment.GetEnvironmentVariable("SHEPHERD_NATIVE_CENSUS_STACKS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static readonly Dictionary<string, string> Stacks = new();

    /// <summary>V6, Mono: Mono calls a DllImportResolver once per library, and CoreLib's own P/Invokes (files, threads, time) never
    /// reach one. Mono's DllImport log itemises every entry point instead: run with MONO_LOG_LEVEL=debug, MONO_LOG_MASK=dll (plus
    /// anything else) and MONO_LOG_DEST=&lt;file&gt;, and Write() reads that file (section 1m). Its lines carry wall-clock seconds
    /// only; <see cref="Tick"/> keeps a seconds -> boot-frame map so each entry point gets the frame of its first binding (±1 s).</summary>
    static readonly string MonoLog = MonoCompat.MonoInterpCompat.IsMono && Environment.GetEnvironmentVariable("MONO_LOG_DEST") is { Length: > 0 } d && d != "syslog"
        && (Environment.GetEnvironmentVariable("MONO_LOG_MASK") ?? "").Split(',').Any(m => m.Trim() is "dll" or "all") ? d : null;
    static readonly List<(long sec, int frame)> SecFrames = new();
    /// <summary>Once per boot frame (Boot loop): local-time second -> frame, only when the Mono log census is active.</summary>
    public static void Tick(int frame)
    {
        if (MonoLog == null || !Enabled) return;
        long sec = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
        if (SecFrames.Count == 0 || SecFrames[^1].sec != sec) SecFrames.Add((sec, frame));
    }

    /// <summary>Vita profile network fail-fast for the BCL: binding the native calls that reach the network — connect, bind,
    /// listen, accept, send-to, name resolution (System.Net.Sockets / System.Net.NameResolution -> libSystem.Native) — throws
    /// SocketException(NetworkUnreachable), so Socket.Connect*, Dns.*, HttpClient, ClientWebSocket (SocketIOUnity/Twitch) and
    /// websocket-sharp's TcpClient all fail on the spot without a packet leaving. Creating/closing a socket and the other
    /// natives (event port, option and IPv4/IPv6 probes) bind normally: they are local, and several run in type initializers,
    /// where a throw would become a permanent TypeInitializationException. Fail closed: a binding whose entry point can't be
    /// identified, or whose candidate set includes a network call, is refused.</summary>
    public static bool NetBlock { get; private set; }
    public static int NetBlocked;
    static readonly HashSet<string> NetAssemblies = new(StringComparer.Ordinal) { "System.Net.Sockets", "System.Net.NameResolution" };
    static readonly HashSet<string> NetEntries = new(StringComparer.Ordinal)
    {
        "SystemNative_Connect", "SystemNative_Connectx", "SystemNative_Bind", "SystemNative_Listen", "SystemNative_Accept",
        "SystemNative_SendTo", "SystemNative_SendMessage", "SystemNative_SendFile",
        "SystemNative_GetHostEntryForName", "SystemNative_GetNameInfo", "SystemNative_GetAddrInfo", "SystemNative_GetHostEntryForNameAsync",
    };
    static readonly List<string> Refused = new();

    public static void Install()
    {
        var env = Environment.GetEnvironmentVariable("SHEPHERD_NATIVE_CENSUS");
        Enabled = Profile.Vita ? env != "0" : env == "1";
        NetBlock = Profile.Vita;
        if (!Enabled && !NetBlock) return;
        // Hook runs once BEFORE the AssemblyLoad handler is subscribed (engine-AOT eboots, SHPD00011/09/90/91). Mono raises
        // AssemblyLoad synchronously from inside its type loader: Hook's first call AOT-initialises the method, which resolves
        // the static field that caches the Resolve delegate (the compiler's <>O class); setting up that class's fields loads
        // System.Runtime.InteropServices (DllImportResolver's assembly). With the handler already subscribed, that load re-entered
        // Hook, whose own AOT init then built <>O's vtable while <>O's field setup was still in progress on this thread: Mono's
        // setup-fields recursion guard returned with the field types unresolved -> SIGSEGV in compute_class_bitmap (a NULL field
        // type) 2 s after 'H10 entering Main' under vlx. The interpreter happened to load that assembly earlier. The second pass
        // picks up whatever loaded in between (Hook skips an assembly it has already seen).
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) Hook(a, false);
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Hook(e.LoadedAssembly, true);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) Hook(a, false);
    }

    static void Hook(Assembly a, bool loadedNow)
    {
        if (a.IsDynamic) return;
        var name = a.GetName().Name;
        lock (Gate) { if (!Hooked.Add(a)) return; AsmLoads.Add((loadedNow ? Frame : -1, name)); }
        if (AsmStacks && loadedNow)
        {
            var st = string.Join(" <- ", new StackTrace(2, false).GetFrames().Select(f => f.GetMethod()).Where(m => m != null)
                .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}").Where(x => !x.StartsWith("System.Runtime.Loader.", StringComparison.Ordinal) && !x.StartsWith("System.Reflection.", StringComparison.Ordinal) && !x.StartsWith("System.RuntimeType", StringComparison.Ordinal)).Take(10));
            lock (Gate) AsmLoadStacks[name] = st;
        }
        if (a == typeof(object).Assembly) { Note($"{name}: not hookable (the runtime binds CoreLib's QCalls and libSystem.Native itself; see the dyld images)"); return; }
        try { NativeLibrary.SetDllImportResolver(a, Resolve); }
        catch (InvalidOperationException) { Note($"{name}: already has a DllImportResolver (e.g. MonoInterpCompat): its P/Invokes are not itemised{(NetAssemblies.Contains(name) ? " and NOT network-blocked" : "")}"); }
        catch (Exception e) { Note($"{name}: SetDllImportResolver failed: {e.GetType().Name}: {e.Message}"); }
    }

    static void Note(string s) { lock (Gate) Notes.Add(s); }

    static IntPtr Resolve(string lib, Assembly asm, DllImportSearchPath? searchPath)
    {
        var an = asm.GetName().Name;
        bool net = NetBlock && NetAssemblies.Contains(an);
        string entry = null, caller = null;
        if (Enabled || net) (entry, caller) = Identify(lib);
        if (Enabled) Record(lib, entry, an, caller);
        if (Enabled && StackLibs.Length > 0 && StackLibs.Any(x => lib.Contains(x, StringComparison.OrdinalIgnoreCase)))
            lock (Gate) if (!Stacks.ContainsKey(lib + "!" + entry)) Stacks[lib + "!" + entry] = $"[{Frame,5}] " + string.Join(" <- ", new StackTrace(1, false).GetFrames()
                .Select(f => f.GetMethod()).Where(m => m != null && !(m.DeclaringType?.FullName ?? "").StartsWith("Shepherd.Engine.Diag", StringComparison.Ordinal))
                .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}").Take(24));
        if (net && IsNetCall(entry))
        {
            Interlocked.Increment(ref NetBlocked); lock (Gate) Refused.Add($"[{Frame,5}] {an} {entry} <- {caller}");
            RefuseNet();   // vita: no network, fail fast
        }
        return IntPtr.Zero;   // not handled: normal probing continues
    }
    // Separate and never inlined: SocketException lives in System.Net.Primitives, and the Mono interpreter resolves the types of a
    // method body when it transforms it -> Resolve itself must not reference it, or the census loads that assembly (V6).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void RefuseNet() => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable);

    static bool IsNetCall(string entry) =>
        entry == null || entry == "?" || NetEntries.Contains(entry) ||
        entry.StartsWith("?{", StringComparison.Ordinal) && entry[2..^1].Split('|').Any(e => e == "..." || NetEntries.Contains(e));

    /// <summary>Entry point + caller of the P/Invoke being bound, from the stack. Optimised code (the BCL) inlines the P/Invoke
    /// stub into its caller, so the P/Invoke frame itself may be missing: then the first frame is a LibraryImport wrapper (its
    /// "&lt;Name&gt;g____PInvoke" local function carries the DllImport) or the caller of a classic DllImport (scan its IL).</summary>
    static (string entry, string caller) Identify(string lib)
    {
        try
        {
            var st = new StackTrace(2, false);   // skip Identify + Resolve
            MethodBase pinv = null, first = null;
            for (int i = 0; i < st.FrameCount; i++)
            {
                var m = st.GetFrame(i)?.GetMethod(); if (m == null) continue;
                if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) { pinv ??= m; continue; }
                if ((m.DeclaringType?.Namespace ?? "").StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal)) continue;
                first = m; break;
            }
            if (pinv != null) return (EntryOf(pinv), Name(first));
            if (first == null) return ("?", null);
            var c = Candidates(first, lib);
            return (c.Count == 1 ? c[0] : c.Count == 0 ? "?" : "?{" + string.Join("|", c.Take(4)) + (c.Count > 4 ? "|..." : "") + "}", Name(first));
        }
        catch { return ("?", null); }
    }
    static string Name(MethodBase m) => m == null ? null : $"{m.DeclaringType?.FullName}.{m.Name}";
    static string EntryOf(MethodBase m) { var di = m.GetCustomAttribute<DllImportAttribute>(); return string.IsNullOrEmpty(di?.EntryPoint) ? m.Name : di.EntryPoint; }
    static bool Lib(MethodBase m, string lib) => (m.Attributes & MethodAttributes.PinvokeImpl) != 0 && m.GetCustomAttribute<DllImportAttribute>()?.Value == lib;
    static List<string> Candidates(MethodBase m, string lib)
    {
        var res = new List<string>(); var t = m.DeclaringType; if (t == null) return res;
        const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var x in t.GetMethods(All)) if (x.Name.StartsWith("<" + m.Name + ">", StringComparison.Ordinal) && Lib(x, lib)) res.Add(EntryOf(x));   // LibraryImport
        if (res.Count > 0) return res.Distinct().ToList();
        var il = m.GetMethodBody()?.GetILAsByteArray(); if (il == null) return res;
        var ta = t.IsGenericType ? t.GetGenericArguments() : null; var ma = m.IsGenericMethod ? m.GetGenericArguments() : null;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 || il[i + 4] is not (0x06 or 0x0A)) continue;   // call <MethodDef/MemberRef>
            try { var callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), ta, ma); if (callee != null && Lib(callee, lib)) res.Add(EntryOf(callee)); } catch { }
        }
        return res.Distinct().ToList();
    }

    static void Record(string lib, string entry, string asm, string caller)
    {
        lock (Gate)
        {
            var k = (lib, entry, asm);
            if (Binds.TryGetValue(k, out var b)) { b.Count++; return; }
            Binds[k] = new Bind { Lib = lib, Entry = entry, Asm = asm, Caller = caller, Frame = Frame, Count = 1 };
        }
    }

    /// <summary>Program.cs's ResolvingUnmanagedDll handler (after normal probing failed). path=null: not in the map.</summary>
    public static void Fallback(string name, Assembly asm, string path, IntPtr handle)
    {
        if (!Enabled) return;
        lock (Gate) Fallbacks.Add($"[{Frame,5}] {name}  (from {asm?.GetName().Name}) -> {(path == null ? "not in the plugin map: unresolved" : handle != IntPtr.Zero ? "loaded " + path : "FAILED " + path)}");
    }

    /// <summary>SDL3 + libSystem*/libc-level BCL natives are allowed on the Vita; everything else is a violation.</summary>
    public static string Category(string lib)
    {
        var f = Path.GetFileName(lib);
        if (f.StartsWith("libSDL3", StringComparison.Ordinal)) return "ok:SDL3";
        if (f.StartsWith("libSystem.", StringComparison.Ordinal) || f is "libSystem" or "libc" or "libc.dylib" or "libm" or "libdl") return "ok:libSystem/libc";
        return "VIOLATION";
    }

    sealed class MonoBind { public string Scope, Path, Entry; public int Order, Frame = -1, Lookups; }
    /// <summary>Section 1m: every entry point Mono's DllImport log shows being looked up (MONO_LOG_MASK=dll), by library: the exact
    /// list a static P/Invoke table must provide. "xN" = Mono searched for it N times (once per P/Invoke method declaring it).
    /// Mono logs no found/not-found outcome outside Windows, so each entry is checked here against the exports of the library
    /// it was searched in (NativeLibrary.TryGetExport at exit). Not in the log: GlobalizationNative_* (Mono's built-in
    /// globalization shim answers them before any lookup; invariant mode never calls them) and runtime icalls.
    /// MONO_LOG_HEADER=1 adds the wall-clock second that maps each entry to a boot frame.</summary>
    static List<string> MonoLogSection()
    {
        var o = new List<string>();
        string[] lines;
        try { using var fs = new FileStream(MonoLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var sr = new StreamReader(fs); lines = sr.ReadToEnd().Split('\n'); }
        catch (Exception e) { o.Add($"## 1m. Mono DllImport log {MonoLog}: unreadable ({e.Message})"); o.Add(""); return o; }
        var binds = new Dictionary<(string, string), MonoBind>(); var libsTried = new List<string>(); var libsFailed = new List<string>(); var how = new Dictionary<string, string>();
        string lastTried = null;
        string scope = null, path = null; MonoBind cur = null; int order = 0;
        static string Q(string l, string after) { int i = l.IndexOf(after, StringComparison.Ordinal); if (i < 0) return null; i += after.Length; int j = l.IndexOf('\'', i); return j > i ? l[i..j] : null; }
        int FrameOf(string l)
        {   // "yyyy-MM-dd HH:mm:ss level[d] mono[pid]: ..." (local time)
            if (l.Length < 19 || !DateTime.TryParseExact(l[..19], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)) return -1;
            long sec = t.Ticks / TimeSpan.TicksPerSecond; int f = -1;
            foreach (var (s2, fr) in SecFrames) { if (s2 > sec) break; f = fr; }
            return f < 0 && SecFrames.Count > 0 ? 0 : f;
        }
        foreach (var l in lines)
        {
            if (l.Contains("DllImport attempting to load: '", StringComparison.Ordinal)) { var x = Q(l, "attempting to load: '"); lastTried = x; if (x != null && !libsTried.Contains(x)) libsTried.Add(x); continue; }
            if (l.Contains("Native library found ", StringComparison.Ordinal) && lastTried != null && !how.ContainsKey(lastTried))
            { int k = l.IndexOf("Native library found ", StringComparison.Ordinal); how[lastTried] = l[(k + 21)..].Split(':')[0].Trim(); continue; }
            if (l.Contains("DllImport unable to load library '", StringComparison.Ordinal)) { var x = Q(l, "unable to load library '"); if (x != null && !libsFailed.Contains(x)) libsFailed.Add(x); continue; }
            if (l.Contains("DllImport searching in: '", StringComparison.Ordinal))
            {
                scope = Q(l, "searching in: '"); int k = l.IndexOf("' ('", StringComparison.Ordinal); path = k > 0 ? l[(k + 4)..].TrimEnd().TrimEnd('.').TrimEnd(')').TrimEnd('\'') : null; continue;
            }
            if (l.Contains("Searching for '", StringComparison.Ordinal) && scope != null)
            {
                var e = Q(l, "Searching for '"); if (e == null) continue;
                if (!binds.TryGetValue((scope, e), out cur)) binds[(scope, e)] = cur = new MonoBind { Scope = scope, Path = path, Entry = e, Order = order++, Frame = FrameOf(l) };
                cur.Lookups++; continue;
            }
        }
        var all = binds.Values.ToList();
        var exported = new Dictionary<MonoBind, bool?>();
        foreach (var g in all.GroupBy(b => b.Path ?? b.Scope))
        {
            IntPtr h = IntPtr.Zero; bool open = false;
            try { open = NativeLibrary.TryLoad(g.Key, out h); } catch { }
            foreach (var b in g) exported[b] = open ? NativeLibrary.TryGetExport(h, b.Entry, out _) : null;
        }
        bool hasFrames = SecFrames.Count > 0 && all.Any(b => b.Frame >= 0);
        o.Add($"## 1m. Mono DllImport log ({MonoLog}): {all.Count} entry points in {all.Select(b => b.Scope).Distinct().Count()} libraries; " +
              $"{exported.Values.Count(v => v == false)} not exported by their library, {exported.Values.Count(v => v == null)} unchecked; " +
              (hasFrames ? "[frame] of the first lookup from the log's wall-clock seconds (±1 s)" : "no frames (set MONO_LOG_HEADER=1)"));
        foreach (var g in all.GroupBy(b => b.Scope).OrderBy(g => Category(g.Key) == "VIOLATION" ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            o.Add($"{Category(g.Key),-18} {g.Key}  ({g.Count()} entry points) -> {g.First().Path}{(how.TryGetValue(g.Key, out var hw) ? $"  [found {hw}]" : "")}");
            foreach (var b in g.OrderBy(b => b.Entry, StringComparer.Ordinal))
                o.Add($"    [{b.Frame,5}] {b.Entry,-52} {(exported[b] switch { true => "", false => "NOT EXPORTED", null => "(library not reopened)" })}{(b.Lookups > 1 ? $" (x{b.Lookups})" : "")}");
        }
        o.Add($"    libraries Mono tried to load: {libsTried.Count} [{string.Join(", ", libsTried)}]; failed: {libsFailed.Count} [{string.Join(", ", libsFailed)}]");
        o.Add("");
        return o;
    }

    static readonly string[] GamePlugins = { "fmod", "resonanceaudio", "Rewired_MacOS", "steam_api", "Galaxy", "HotReloadNativeHelper", "lib_burst_generated" };

    [DllImport("/usr/lib/libSystem.B.dylib")] static extern uint _dyld_image_count();
    [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr _dyld_get_image_name(uint index);
    static List<string> DyldImages()
    {
        var l = new List<string>();
        if (!OperatingSystem.IsMacOS()) return l;
        try { uint n = _dyld_image_count(); for (uint i = 0; i < n; i++) { var s = Marshal.PtrToStringUTF8(_dyld_get_image_name(i)); if (s != null) l.Add(s); } }
        catch (Exception e) { l.Add("(dyld enumeration failed: " + e.Message + ")"); }
        return l;
    }

    public static void Write()
    {
        if (!Enabled) return;
        var path = Path.Combine(Program.LogDir, "native-resolve.txt");
        var images = DyldImages();
        List<Bind> binds; List<string> notes, falls; List<(int, string)> loads;
        lock (Gate) { binds = Binds.Values.ToList(); notes = Notes.ToList(); falls = Fallbacks.ToList(); loads = AsmLoads.ToList(); }
        var o = new List<string>
        {
            $"# Shepherd native-resolution census — profile={(Profile.Vita ? "vita" : "default (Mac)")}, frames 0..{Frame}, {Clock.Elapsed.TotalSeconds:F1} s, {DateTime.Now:yyyy-MM-dd HH:mm}",
            $"# runtime {RuntimeInformation.FrameworkDescription}{(MonoCompat.MonoInterpCompat.IsMono ? " (Mono)" : "")}; managed DLLs from {Program.RuntimeManaged}; BCL network block: {(!NetBlock ? "off" : MonoCompat.MonoInterpCompat.IsMono ? "INEFFECTIVE on Mono (its ALC cache answers libSystem.Native before any resolver; on the Vita the block belongs in the System.Native shim)" : $"on ({NetBlocked} bindings refused with SocketException)")}",
            MonoCompat.MonoInterpCompat.IsMono ? "# Mono calls a DllImportResolver once per library (not per entry point): section 1 lists the first binding only; MONO_LOG_MASK=dll (vita/v2 tools/pinvoke_hits.py) itemises" : "#",
            "# Vita rule: managed code binds only SDL3 + libSystem*/libc-level BCL natives.",
            ""
        };
        var violLibs = binds.Where(b => Category(b.Lib) == "VIOLATION").Select(b => b.Lib).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var pluginImages = images.Where(i => GamePlugins.Any(p => i.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        o.Add(violLibs.Count == 0 && pluginImages.Count == 0 && falls.All(f => f.Contains("unresolved"))
            ? "VERDICT: CLEAN — no managed binding outside SDL3/libSystem*/libc, no game native plugin mapped"
            : $"VERDICT: {violLibs.Count} library(ies) outside the Vita rule [{string.Join(", ", violLibs)}]; game plugins mapped: [{string.Join(", ", pluginImages.Select(Path.GetFileName))}]");
        o.Add("");
        o.Add($"## 1. Managed P/Invoke bindings (first bind per library/entry/assembly): {binds.Count} entry points, {binds.Select(b => b.Lib).Distinct().Count()} libraries");
        foreach (var g in binds.GroupBy(b => b.Lib).OrderBy(g => Category(g.Key) == "VIOLATION" ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            o.Add($"{Category(g.Key),-18} {g.Key}  ({g.Count()} entry points; assemblies: {string.Join(", ", g.Select(b => b.Asm).Distinct())})");
            foreach (var b in g.OrderBy(b => b.Frame).ThenBy(b => b.Entry, StringComparer.Ordinal))
                o.Add($"    [{b.Frame,5}] {b.Entry,-46} {b.Asm,-28} {(b.Count > 1 ? "x" + b.Count + " " : "")}{(b.Caller != null ? "<- " + b.Caller : "")}");
        }
        o.Add("");
        List<string> stacks; lock (Gate) stacks = Stacks.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}\n        {k.Value}").ToList();
        if (StackLibs.Length > 0) { o.Add($"## 1c. Call stacks of the first binding, libraries matching [{string.Join(", ", StackLibs)}]: {stacks.Count}"); o.AddRange(stacks.Select(x => "    " + x)); o.Add(""); }
        if (MonoLog != null) o.AddRange(MonoLogSection());
        List<string> refused; lock (Gate) refused = Refused.ToList();
        if (NetBlock) { o.Add($"## 1b. Network bindings refused (vita fail-fast, SocketException): {refused.Count}"); o.AddRange(refused.Select(r => "    " + r)); o.Add(""); }
        o.Add($"## 2. ResolvingUnmanagedDll fallbacks (Program.cs plugin map): {falls.Count}");
        o.AddRange(falls.Select(f => "    " + f));
        o.Add("");
        o.Add($"## 3. dyld images at exit: {images.Count} (game plugins: {pluginImages.Count})");
        string Group(string i) =>
            GamePlugins.Any(p => i.Contains(p, StringComparison.OrdinalIgnoreCase)) ? "0 GAME PLUGIN" :
            i.Contains("/dotnet/") || i.Contains("/.dotnet/") || i.Contains("Microsoft.NETCore.App") || i.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal) ? "1 .NET runtime + BCL natives" :
            i.Contains("libmono-profiler-", StringComparison.Ordinal) ? "1 .NET runtime + BCL natives (+ the Mac-only V6 interp-census profiler)" :
            i.Contains("SDL3") ? "2 SDL3" :
            i.StartsWith("/System/Library/") || i.StartsWith("/usr/lib/") ? "3 macOS system (dependencies of the runtime/SDL3/Metal, or bound above)" : "4 other";
        foreach (var g in images.GroupBy(Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            o.Add($"  {g.Key[2..]}: {g.Count()}");
            if (g.Key[0] != '3') foreach (var i in g) o.Add("    " + i);
        }
        o.Add("");
        o.Add($"## 4. Notes ({notes.Count})");
        o.AddRange(notes.Select(n => "    " + n));
        o.Add("");
        o.Add($"## 5. Assembly loads ({loads.Count}; frame -1 = already loaded when the census started)");
        o.AddRange(loads.Select(l => $"    [{l.Item1,5}] {l.Item2}" + (AsmStacks && AsmLoadStacks.TryGetValue(l.Item2, out var ls) ? "\n            <- " + ls : "")));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, o);
        Log.Info($"[census] {binds.Count} native entry points in {binds.Select(b => b.Lib).Distinct().Count()} libraries; outside the Vita rule: [{string.Join(", ", violLibs)}]; game plugins mapped: {pluginImages.Count} -> {path}");
    }
}
