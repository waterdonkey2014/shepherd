using System.Reflection;
using System.Runtime.Loader;
namespace Shepherd.Engine;

public static class Program
{
    public static string GameData = Environment.GetEnvironmentVariable("SHEPHERD_GAME_DATA")
        ?? "$SHEPHERD_GAME_DATA";
    public static string GameManaged => Profile.ManagedOverride ?? Path.Combine(GameData, "Managed");   // V6: SHEPHERD_MANAGED is the one merged folder (the Vita layout)
    public static string Root = Environment.GetEnvironmentVariable("SHEPHERD_ROOT") ?? Environment.CurrentDirectory;
    /// <summary>engine.log, simstats*, native-resolve.txt, missing-icalls.txt, assemblies.txt, audio-events.tsv, memreport-*: runtime/logs,
    /// or SHEPHERD_LOG_DIR (the Vita hosts give each app its own folder, runtime/logs/&lt;app&gt;, so one app no longer truncates the
    /// previous app's logs on the card; device kit #2 review). Unset (the Mac, every runner script) = runtime/logs, as before.</summary>
    public static string LogDir = Environment.GetEnvironmentVariable("SHEPHERD_LOG_DIR") is { Length: > 0 } ld ? ld : Path.Combine(Root, "runtime", "logs");
    /// <summary>Rewritten game/Unity DLLs + icalls.json. The Vita profile uses its own set (tools/Rewriter --vita: FMOD
    /// P/Invokes -> engine stubs); runtime/Managed, with real FMOD, stays the Mac default.</summary>
    public static string RuntimeManaged => Profile.ManagedOverride ?? Path.Combine(Root, "runtime", Profile.ManagedDir);

    static bool IsBcl(string n) => n is "mscorlib" or "netstandard" || n.StartsWith("System") || n.StartsWith("Microsoft.") || n.StartsWith("Mono.");

    public static int Main(string[] args)
    {
        Runtime.StrictOriginal.Apply();          // kit #7 tests: SHEPHERD_STRICT_ORIGINAL=1 forces the PC behaviour (before any switch is read); no-op unset
        Diag.SimStats.MainEntered();             // V6 startup clock; no-op unless SHEPHERD_SIMSTATS is set
        Diag.K7.MainEntered();                   // kit #7: the boot numbers' clock off the Vita (SHEPHERD_K7=0: nothing)
        MonoCompat.MonoInterpCompat.Install();   // no-op unless running on the Mono interpreter (V2) and SHEPHERD_MONO_CFSHIM is set
        if (!Profile.Check(RuntimeManaged)) return 2;
        Diag.NativeCensus.Install();             // vita: on by default (+ BCL network fail-fast); Mac default: only with SHEPHERD_NATIVE_CENSUS=1
        if (Profile.Vita) Profile.StartVita();
        // Must be installed before ANY Unity type is touched — Boot lives in another class so its JIT happens after this.
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (IsBcl(name.Name)) return null;
            foreach (var dir in new[] { RuntimeManaged, GameManaged })
            {
                var p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
            }
            // the owner-local Shepherd.Engine.Derived (vita/shepherd/build-derived.sh output; the Vita / sim layouts keep it in the
            // managed folder above): only that name is taken from runtime/derived
            if (name.Name == Render.DerivedRender.AssemblyName && Path.Combine(Root, "runtime", "derived", name.Name + ".dll") is var dp && File.Exists(dp))
                return ctx.LoadFromAssemblyPath(dp);
            return null;
        };
        // The game's own native plugins are universal (x86_64+arm64) -> load them straight from its PlugIns folder.
        // steam_api is deliberately NOT resolved: the engine must never talk to the user's Steam client.
        // The Vita profile resolves none of them: FMOD is stubbed by Rewriter pass 3, Rewired runs on Unity input.
        var plugins = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(GameData)), "PlugIns");
        var natives = Profile.Vita ? new Dictionary<string, string>() : new Dictionary<string, string>
        {
            ["fmodstudio"] = Path.Combine(plugins, "fmodstudio.bundle", "Contents", "MacOS", "fmodstudio"),
            ["resonanceaudio"] = Path.Combine(plugins, "resonanceaudio.bundle", "Contents", "MacOS", "resonanceaudio"),
            ["Rewired_MacOS"] = Path.Combine(plugins, "Rewired_MacOS.bundle"),
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) =>
        {
            var key = Path.GetFileNameWithoutExtension(name).Replace("lib", "");
            if (natives.TryGetValue(key, out var path) && File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out var h)) { Diag.NativeCensus.Fallback(name, asm, path, h); return h; }
            Diag.NativeCensus.Fallback(name, asm, null, IntPtr.Zero);
            return IntPtr.Zero;
        };
        return Boot.Run(args);
    }
}
