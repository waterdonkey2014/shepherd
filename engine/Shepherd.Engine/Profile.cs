namespace Shepherd.Engine;

/// <summary>Engine profiles. Default (SHEPHERD_PROFILE unset): the Mac engine, unchanged. <c>SHEPHERD_PROFILE=vita</c> (V4):
/// the Mac engine shaped like the Vita target (vita/notes/v4-profile.md):
/// - managed DLLs from runtime/ManagedVita (Rewriter --vita: FMOD P/Invokes -> silent engine stubs, Shepherd.Audio.Fmod)
/// - 960x544 screen + render target
/// - network fail-fast: UnityWebRequest -> ConnectionError
/// - VideoPlayer finishes at once
/// - gamepad / virtual Vita pad (SHEPHERD_PAD) -> the Unity KeyCodes the game's Rewired keyboard map reads; Rewired on its
///   Unity-input path (no Rewired_OSX native input, no Rewired_MacOS)
/// - no game native plugins (fmodstudio, resonanceaudio, Rewired_MacOS, steam_api)
/// - native-resolution census on by default (runtime/logs/native-resolve.txt)</summary>
[ProfileOnly]
public static class Profile
{
    public static readonly string Name = (Environment.GetEnvironmentVariable("SHEPHERD_PROFILE") ?? "").Trim().ToLowerInvariant();
    public static readonly bool Vita = Name == "vita";
    public const int VitaWidth = 960, VitaHeight = 544;
    public static string ManagedDir => Vita ? "ManagedVita" : "Managed";
    /// <summary>SHEPHERD_MANAGED=&lt;dir&gt; (V6): the rewritten DLLs + icalls.json come from this directory instead of
    /// runtime/ManagedVita (or runtime/Managed). The Vita layout: one ux0:data/shepherd/... folder holding ManagedVita laid over the
    /// game's Managed DLLs; the vita-sim run points it (and SHEPHERD_GAME_DATA/Managed) at the same merged folder.</summary>
    public static readonly string ManagedOverride = Environment.GetEnvironmentVariable("SHEPHERD_MANAGED") is { Length: > 0 } m ? m : null;
    /// <summary>SHEPHERD_PERSISTENT=&lt;dir&gt; (V6 Stage B): Application.persistentDataPath (the sandbox saves) instead of
    /// &lt;root&gt;/runtime/persistent. The Vita host points it at ux0:data/shepherd/persistent. Never the user's real CotL save dir.</summary>
    public static readonly string PersistentOverride = Environment.GetEnvironmentVariable("SHEPHERD_PERSISTENT") is { Length: > 0 } pd ? pd : null;

    /// <summary>Vita startup (after the native census/network gate is installed). HttpClient's default proxy would be the
    /// macOS system proxy, read through CFNetwork/CoreFoundation (Mac-only natives) on the first request: make it "direct".
    /// V6: only once System.Net.Http is actually loaded (by whatever is about to use HttpClient). Setting it eagerly loaded
    /// System.Net.Http + WebProxy + Primitives + Uri + RegularExpressions (~1.3 MB of IL, metadata the Vita keeps resident) at
    /// frame 0 although nothing in the menu boot uses HttpClient.</summary>
    public static void StartVita()
    {
        Runtime.VitaMemory.Install();   // stage 1 memory cuts (Runtime/VitaMemory.cs): assembly-load hooks
        Runtime.SpineReadShare.Install();   // kit6 mem L1: Spine arrays shared during the read (the owner's --spine-share spine-unity.dll calls in); SHEPHERD_SPINE_READSHARE=0 = kit #5
        Runtime.SpineLazySkins.Install();   // kit #7 spine L2: a skin's attachments read when first used (the owner's --spine-lazyskin spine-unity.dll calls in); SHEPHERD_SPINE_LAZYSKIN=0 = kit #6
        Runtime.SpineClipNative.Install();  // kit #7 spine: Spine clipping through the native port (the owner's --spine-clip spine-unity.dll calls in); SHEPHERD_SPINE_CLIP=0 = kit #6
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "System.Net.Http")) { DirectProxy(); return; }
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => { if (e.LoadedAssembly.GetName().Name == "System.Net.Http") DirectProxy(); };
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]   // its body references System.Net.Http
    static void DirectProxy() => System.Net.Http.HttpClient.DefaultProxy = new System.Net.WebProxy();

    /// <summary>Startup checks (stderr). Returns false when the profile cannot run.</summary>
    public static bool Check(string runtimeManaged)
    {
        if (Name.Length > 0 && !Vita) Console.Error.WriteLine($"SHEPHERD_PROFILE='{Name}' is not a known profile (known: vita); running the default Mac engine");
        if (Vita && !File.Exists(Path.Combine(runtimeManaged, "icalls.json")))
        {
            Console.Error.WriteLine($"SHEPHERD_PROFILE=vita needs {runtimeManaged}: run tools/Rewriter <game Managed> runtime/ManagedVita --vita");
            return false;
        }
        return true;
    }
}

/// <summary>Marks profile code (and the profile census) that reads no property-bag keys. The V3 diet's "readable property keys" scan
/// (Assets/Diet.cs BuildReadable) skips these types, so adding profile code changes neither the Mac default's retained
/// keys nor its assembly-load order (reading their method bodies would resolve FMOD local types -> load FMODUnity early).
/// None of them reads property-bag keys.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ProfileOnlyAttribute : Attribute { }
