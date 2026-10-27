using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.MonoCompat;

/// <summary>V2 (Shepherd on Mono on the Mac): compatibility hooks that only exist for the Mono *interpreter* in a 64-bit
/// process. CoreCLR and the Mono JIT return from <see cref="Install"/> immediately, so their behaviour is unchanged.
///
/// Problem: Rewired_OSX declares CFIndex (64-bit) CoreFoundation parameters as Int32. The interpreter's fast native-call
/// path passes an Int32 as a whole 64-bit slot with a dirty upper half, so CoreFoundation reads a garbage CFIndex and
/// CFStringCreateWithCharacters faults during Rewired's HID init (SIGSEGV, frame ~0). Fix without touching the game DLL:
/// bind the Default ALC's CoreFoundation imports to cf_interp_shim.c, which re-narrows those
/// arguments and re-exports the rest of CoreFoundation. Opt-in: SHEPHERD_MONO_CFSHIM=&lt;path to libshepherd_cfshim.dylib&gt;
/// (build with MonoCompat/build-cf-shim.sh).</summary>
public static class MonoInterpCompat
{
    public static readonly bool IsMono = Type.GetType("Mono.RuntimeStructs") != null;

    /// <summary>False when the calling code is being interpreted (Mono's interpreter folds the property to 0).
    /// Must be read from code that runs under the same engine as the game (it does: everything is interpreted).</summary>
    public static bool Interpreted => IsMono && !RuntimeFeature.IsDynamicCodeCompiled;

    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(CoreFoundation)] static extern IntPtr CFAllocatorGetDefault();
    // Separate, non-inlined: the interpreter transforms (and may inline) a method's callees before running it, which would
    // bind the P/Invoke before the resolver below is registered.
    [MethodImpl(MethodImplOptions.NoInlining)] static IntPtr Seed() => CFAllocatorGetDefault();

    public static void Install()
    {
        if (!IsMono || IntPtr.Size != 8 || RuntimeFeature.IsDynamicCodeCompiled) return;
        var shim = Environment.GetEnvironmentVariable("SHEPHERD_MONO_CFSHIM");
        if (string.IsNullOrEmpty(shim))
        {
            if (Profile.Vita) return;   // the Vita profile never loads Rewired_OSX (Rewired runs on Unity input): nothing to shim
            Console.Error.WriteLine("[MonoInterpCompat] Mono interpreter detected but SHEPHERD_MONO_CFSHIM is unset: Rewired_OSX's CoreFoundation " +
                                    "P/Invokes will receive garbage CFIndex arguments (expect SIGSEGV in CFStringCreateWithCharacters). Build MonoCompat/build-cf-shim.sh.");
            return;
        }
        if (!NativeLibrary.TryLoad(shim, out var handle)) { Console.Error.WriteLine($"[MonoInterpCompat] cannot load {shim}"); return; }
        // Mono checks the ALC-wide native-library cache (keyed by the DllImport name) BEFORE an assembly's DllImportResolver,
        // and the BCL (System.Security.Cryptography, first save-file read) loads the same CoreFoundation path long before
        // Rewired_OSX does -> a resolver on Rewired_OSX alone is never consulted. So seed the Default ALC's cache entry for
        // that name with the shim: one engine-side P/Invoke, resolved through the engine assembly's own resolver, before
        // anything else touches CoreFoundation. Every later CoreFoundation import in the Default ALC (BCL and game) then
        // binds through the shim; it only re-narrows five CFIndex arguments (identical for any value below 2^31) and
        // re-exports the rest of CoreFoundation.
        NativeLibrary.SetDllImportResolver(typeof(MonoInterpCompat).Assembly, (name, asm, path) => name == CoreFoundation ? handle : IntPtr.Zero);
        var seed = Seed();
        Console.Error.WriteLine($"[MonoInterpCompat] Mono interpreter (64-bit): Default-ALC '{CoreFoundation}' bound to {shim} (seed call ok={seed != IntPtr.Zero})");
    }
}
