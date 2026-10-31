using Shepherd.Assets;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Assets;

/// <summary>V3 memory diet (Mac): stop keeping asset plumbing nothing reads any more. All parts are ON by default; each has
/// an env switch that restores the original behaviour, so a regression can be bisected:
///   SHEPHERD_KEEP_RGBA=1   keep decoded RGBA8 copies of textures (decoded on first touch, kept forever)
///                          — diet: decode only when pixels are needed (GPU upload / CPU read), drop after upload,
///                            keep (pin) only textures whose pixels code reads or writes on the CPU
///   SHEPHERD_KEEP_TREES=1  keep every record's source TypeTree in NObj.Data
///                          — diet: keep only NObj.HasSource; the few late readers re-read from the file (NObj.SourceTree)
///   SHEPHERD_KEEP_FILES=1  read whole SerializedFiles on load and keep them
///                          — diet: parse metadata only; whole-file buffers load on first payload access and are
///                            released after SHEPHERD_FILE_IDLE frames (default 120) without a read (re-read on demand)
///   SHEPHERD_KEEP_PROPS=1  seed property bags with every serialized field
///                          — diet: seed only keys something can read (auto-prop getters, explicit engine reads)
///   SHEPHERD_DIET=0        all of the above at once.
/// SHEPHERD_DIET_LOG=1 logs every file (re)load / release and texture decode.</summary>
public static class Diet
{
    static bool Env(string n) => Environment.GetEnvironmentVariable(n) is "1" or "true" or "yes";
    static readonly bool Off = Environment.GetEnvironmentVariable("SHEPHERD_DIET") is "0" or "false" or "off";
    public static readonly bool KeepRgba = Off || Env("SHEPHERD_KEEP_RGBA");
    public static readonly bool KeepTrees = Off || Env("SHEPHERD_KEEP_TREES");
    public static readonly bool KeepFiles = Off || Env("SHEPHERD_KEEP_FILES");
    public static readonly bool KeepProps = Off || Env("SHEPHERD_KEEP_PROPS");
    public static readonly int FileIdleFrames = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_FILE_IDLE"), out var fi) && fi > 0 ? fi : 120;
    /// <summary>A pristine texture whose pixels had to be decoded again this many times keeps its RGBA from then on
    /// (e.g. code that toggles filterMode every frame would otherwise re-decode every frame).</summary>
    public const int MaxRedecodes = 2;
    static readonly bool Verbose = Env("SHEPHERD_DIET_LOG");

    // ---- counters (MemReport prints them) ----
    public static int TexDecodes, TexRedecodes, TexPinned, TexDecodeFailures, FilesReleased, TreeRereads, PropsKeysDropped, PropsKeysKept;
    public static long TexDecodedBytes, FileBytesReleased;

    public static string Describe() =>
        $"diet: rgba={(KeepRgba ? "KEPT" : "drop-after-upload")} trees={(KeepTrees ? "KEPT" : "dropped")} files={(KeepFiles ? "KEPT" : $"lazy+release after {FileIdleFrames} idle frames")} props={(KeepProps ? "FULL" : "readable keys only")}";

    public static string Counters() =>
        $"diet counters: texture decodes={TexDecodes} ({TexDecodedBytes / 1048576.0:F1} MiB RGBA8, re-decodes={TexRedecodes}, pinned={TexPinned}, failed={TexDecodeFailures}); " +
        $"file buffers: loads={FileLoads} reloads={SerializedFile.Reloads} ({SerializedFile.ReloadedBytes / 1048576.0:F1} MiB) releases={FilesReleased} ({FileBytesReleased / 1048576.0:F1} MiB); " +
        $"sparse object reads={SerializedFile.SparseReads} ({SerializedFile.SparseBytes / 1048576.0:F1} MiB); type trees shared={SerializedFile.TypeTreeShareStats.shared} distinct={SerializedFile.TypeTreeShareStats.distinct}; " +
        $"source-tree re-reads={TreeRereads}; props keys seeded={PropsKeysKept} dropped={PropsKeysDropped}";

    static int FileLoads;
    public static void Init(AssetDatabase db)
    {
        db.LazyFiles = !KeepFiles;
        SerializedFile.ShareTypeTrees = !KeepFiles;
        SerializedFile.OnLoad = (f, n, again) =>
        {
            FileLoads++;
            if (Verbose || again) Log.Info($"[diet] {(again ? "re-read" : "read")} {f.Name} ({n / 1048576.0:F1} MiB){(again ? $" — load #{f.Loads}" : "")}");
        };
        Log.Info(Describe());
    }

    /// <summary>End of every engine frame: advance the file clock and release whole-file buffers / bundle block caches
    /// nothing has read for <see cref="FileIdleFrames"/> frames.</summary>
    public static void EndFrame()
    {
        Runtime.MemLadder.Tick();       // kit #10 memory: the memory-pressure ladder (every 16th frame asks the host; off = one call, then nothing)
        Loader.DropTransientCaches();   // the sprite-atlas trees of this frame's sprite creation
        long now = ++SerializedFile.Clock;
        if (KeepFiles || Loader.Db == null || now % 8 != 0) return;
        var (n, bytes) = Loader.Db.ReleaseIdle(now - FileIdleFrames);
        if (n > 0 || bytes > 0)
        {
            FilesReleased += n; FileBytesReleased += bytes;
            if (Verbose) Log.Info($"[diet] released {n} file buffers + block caches, {bytes / 1048576.0:F1} MiB");
        }
    }

    // ---- textures ----
    /// <summary>Decode a pristine asset texture from its source (no Version bump, nothing stored). Null when it cannot be
    /// decoded; a failure is logged once (like the original first-touch decode) and not retried.</summary>
    public static byte[] DecodeTexture(NTexture t)
    {
        if (t.Src == null || t.DecodeFailed || t.SourceFile is not SerializedFile f) return null;
        try
        {
            var (w, h, rgba, _) = TextureDecoder.DecodeTexture2D(Loader.Db, f, t.Src);
            if (w != t.Width || h != t.Height) { t.Width = w; t.Height = h; }
            if (t.Decodes++ > 0) TexRedecodes++;
            TexDecodes++; TexDecodedBytes += rgba.LongLength;
            if (Verbose) Log.Info($"[diet] decode '{t.Name}' {w}x{h} #{t.Decodes}");
            return rgba;
        }
        catch (Exception e) { t.DecodeFailed = true; TexDecodeFailures++; Log.Warn($"decode texture '{t.Name}': {(e.InnerException ?? e).Message}"); return null; }
    }

    // ---- property bags ----
    static HashSet<string> _readable;
    public static readonly Dictionary<string, (int n, long elems)> DroppedProps = new();
    /// <summary>Could anything read this property-bag key? Auto-prop getter names (any type) plus every string literal in the
    /// engine assembly (all explicit reads use literal keys: n.Get("sprite"), CameraIcalls.F(c, "planeDistance", ..)) --
    /// a deliberately generous over-approximation, computed once from the IL (ldstr operands).</summary>
    public static bool IsReadableKey(string key) => (_readable ??= BuildReadable()).Contains(key);
    static HashSet<string> BuildReadable()
    {
        var set = new HashSet<string>(Impl.AutoProps.GetterKeys, StringComparer.Ordinal);
        // fid1-fixup round 2: read from the engine's assembly file (IlLiterals: the same methods, bytes and string resolution), not through
        // reflection -- Mono keeps every MethodInfo / MethodBody it hands out alive for good (~2,400 of each, with their IL, after this
        // scan). null = the file did not read as expected (or SHEPHERD_DIET_SCAN=reflect): the reflection scan below, as before.
        var file = IlLiterals.ForDiet(typeof(Diet).Assembly, typeof(ProfileOnlyAttribute));
        List<string> refl = null;
        if (file == null || IlLiterals.Both)
        {
            refl = new List<string>();
            const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var t in typeof(Diet).Assembly.GetTypes())
            {
                if (IsProfileOnly(t)) continue;   // V4: profile-only code reads no bag keys (and its bodies must not be resolved here)
                foreach (var m in t.GetMethods(All).Cast<System.Reflection.MethodBase>().Concat(t.GetConstructors(All)))
                {
                    byte[] il; try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { continue; }
                    if (il == null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] == 0x72 && il[i + 4] == 0x70)   // ldstr <user-string token 0x70xxxxxx>
                            try { refl.Add(m.Module.ResolveString(BitConverter.ToInt32(il, i + 1))); } catch { }
                }
            }
            if (file != null) IlLiterals.Compare(file, refl);   // SHEPHERD_DIET_SCAN=both
        }
        int literals = 0;
        foreach (var s in file ?? refl) if (set.Add(s)) literals++;
        Log.Info($"[diet] readable property keys: {Impl.AutoProps.GetterKeys.Count} auto-getter names + {literals} engine string literals");
        return set;
    }
    static bool IsProfileOnly(Type t) { for (; t != null; t = t.DeclaringType) if (t.IsDefined(typeof(ProfileOnlyAttribute), false)) return true; return false; }
    public static void PropDropped(NObj rec, string key)
    {
        PropsKeysDropped++;
        var k = (rec.Managed?.GetType().Name ?? rec.GetType().Name) + "." + key;
        var e = DroppedProps.GetValueOrDefault(k); DroppedProps[k] = (e.n + 1, e.elems);
    }

    // ---- source trees ----
    /// <summary>Re-read a record's source TypeTree from its file (the file buffer is (re)loaded if it was released and then
    /// follows the normal idle release). <paramref name="transient"/>: put a file that was not resident back (diagnostics).</summary>
    public static TypeTreeObject Reread(NObj n, bool transient = false)
    {
        if (!n.HasSource || n.SourceFile is not SerializedFile f || f.GetObject(n.PathId) is not { } o) return null;
        bool wasResident = f.IsResident;
        try { TreeRereads++; return TypeTreeReader.Read(f, o); }
        catch (Exception e) { Log.Warn($"re-read {ClassIds.Name(o.ClassId)} #{n.PathId} in {f.Name}: {e.Message}"); return null; }
        finally { if (transient && !wasResident) f.Release(); }
    }

    /// <summary>The field names CandidateBuiltin-style code would enumerate from the record's source tree, from the type tree
    /// alone (no object bytes read).</summary>
    public static IEnumerable<string> SourceFieldPaths(NObj n)
    {
        if (!n.HasSource || n.SourceFile is not SerializedFile f || f.GetObject(n.PathId) is not { } o) return Array.Empty<string>();
        return TypeTreeReader.FieldPaths(f, o);
    }
}
