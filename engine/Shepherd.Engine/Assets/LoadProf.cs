using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace Shepherd.Engine.Assets;

#if !SHEPHERD_EBOOT

/// <summary>Device kit #10, track load (vita/notes/kit10-load.md): the loader's own profile -- exclusive time per zone of a scene load and an
/// Instantiate (tree decode, each Create kind, field population, the game's ctors / OnAfterDeserialize / Awake + OnEnable, the clone's tree
/// and field copy), counts and allocation, one line per scene load / per 50 clones and a summary at the end. Measurement only:
/// SHEPHERD_K10_LOADPROF=1 (off: one static readonly test per zone, no clock read). It never changes what is loaded or in which order.
/// [ProfileOnly]: its literals stay out of the diet's readable-key scan (Assets/Diet.cs).</summary>
[ProfileOnly]
public static class LoadProf
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_K10_LOADPROF") == "1";
    public const int Scene = 0, CreateGo = 1, CreateTr = 2, CreateMono = 3, CreateOther = 4, CreateAsset = 5, Tree = 6, Populate = 7, GameAfter = 8,
        GameCtor = 9, Activate = 10, CloneTree = 11, CloneFields = 12, CloneProps = 13, CloneActivate = 14, Resolve = 15, Program = 16, Clone = 17, Drain = 18,
        CreateSprite = 19, CreateTexture = 20, CreateMaterial = 21, CreateShader = 22, CreateMesh = 23, CreateText = 24, AnimInit = 25, AnimCtrl = 26, AnimClip = 27, AnimBind = 28, NestedCtor = 29, NestedAfter = 30, N = 31;
    static readonly string[] Names = { "scene (rest)", "create GameObject", "create Transform", "create MonoBehaviour", "create built-in", "create asset", "tree decode",
        "populate", "game OnAfterDeserialize", "game ctor", "activate (Awake/OnEnable...)", "clone tree", "clone fields", "clone props", "clone activate", "resolve", "field programs", "clone (rest)", "drain (rest)",
        "create Sprite", "create Texture", "create Material", "create Shader", "create Mesh", "create TextAsset", "animator init", "animator controller decode", "animation clip decode", "animator bind", "game ctor (nested objects)", "game OnAfterDeserialize (nested)" };
    static readonly long[] _excl = new long[N], _cnt = new long[N];
    static readonly long[] _sExcl = new long[N], _sCnt = new long[N];   // since the last report
    static readonly int[] _stack = new int[256]; static readonly long[] _t0 = new long[256], _child = new long[256]; static int _sp;
    static long _clones, _cloneObjs, _scenes;
    static long _a0, _alloc, _sAlloc;   // main-thread allocation inside the outermost zones (the loader's entry points, the game code they run included)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Conditional("SHEPHERD_LOADPROF")] public static void Enter(int z)
    {
        if (!On) return;
        if (_sp >= _stack.Length) { _sp++; return; }
        if (_sp == 0) _a0 = GC.GetAllocatedBytesForCurrentThread();
        _stack[_sp] = z; _t0[_sp] = Stopwatch.GetTimestamp(); _child[_sp] = 0; _sp++;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Conditional("SHEPHERD_LOADPROF")] public static void Exit(int z)
    {
        if (!On) return;
        _sp--;
        if (_sp >= _stack.Length || _sp < 0) { if (_sp < 0) _sp = 0; return; }
        long now = Stopwatch.GetTimestamp(), total = now - _t0[_sp], ex = total - _child[_sp]; int zz = _stack[_sp];
        _excl[zz] += ex; _cnt[zz]++; _sExcl[zz] += ex; _sCnt[zz]++;
        if (_sp > 0) _child[_sp - 1] += total;
        else { long a = GC.GetAllocatedBytesForCurrentThread() - _a0; _alloc += a; _sAlloc += a; }
    }
    [Conditional("SHEPHERD_LOADPROF")] public static void CloneDone(int objs) { if (!On) return; _clones++; _cloneObjs += objs; }
    // ---- per script type: the populate step's inclusive time (fields, nested objects, the references it creates) ----
    static readonly Dictionary<Type, (long t, int n)> _byType = new();
    public static long Now() => On ? Stopwatch.GetTimestamp() : 0;
    [Conditional("SHEPHERD_LOADPROF")] public static void TypeDone(Type t, long t0) { if (!On || t == null) return; var e = _byType.TryGetValue(t, out var v) ? v : default; _byType[t] = (e.t + Stopwatch.GetTimestamp() - t0, e.n + 1); }
    static string TopTypes() => string.Join(", ", _byType.OrderByDescending(kv => kv.Value.t).Take(15).Select(kv => $"{kv.Key.Name} {Ms(kv.Value.t):F1} ms x{kv.Value.n}"));
    static double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
    static string Row(long[] ex, long[] cnt)
    {
        var sb = new System.Text.StringBuilder(); long tot = 0; for (int i = 0; i < N; i++) tot += ex[i];
        sb.Append("total ").Append(Ms(tot).ToString("F1")).Append(" ms");
        for (int i = 0; i < N; i++) if (cnt[i] > 0) sb.Append(" | ").Append(Names[i]).Append(' ').Append(Ms(ex[i]).ToString("F1")).Append(" ms x").Append(cnt[i]);
        return sb.ToString();
    }
    /// <summary>After a scene's instantiation (and activation): the zones since the previous report (clones in between included).</summary>
    [Conditional("SHEPHERD_LOADPROF")] public static void SceneDone(string name)
    {
        if (!On) return;
        _scenes++;
        Log.Info($"[k10 loadprof] scene '{name}': {Row(_sExcl, _sCnt)} | allocated {_sAlloc / 1048576.0:F2} MiB");
        Array.Clear(_sExcl); Array.Clear(_sCnt); _sAlloc = 0;
    }
    public static string Summary() => !On ? null : $"[k10 loadprof] all ({_scenes} scenes, {_clones} Instantiate calls, {_cloneObjs} objects cloned): {Row(_excl, _cnt)} | allocated {_alloc / 1048576.0:F2} MiB\n[k10 loadprof] populate by type (inclusive): {TopTypes()}";
}
#else
/// <summary>The eboot's stand-in (kit #10: the loader profile is a measurement tool -- Mac builds and SHEPHERD_ENGINE_TOOLS=1 eboots carry it;
/// in an eboot every zone call is compiled out: [Conditional], SHEPHERD_LOADPROF undefined).</summary>
public static class LoadProf
{
    public const bool On = false;
    public const int Scene = 0, CreateGo = 1, CreateTr = 2, CreateMono = 3, CreateOther = 4, CreateAsset = 5, Tree = 6, Populate = 7, GameAfter = 8,
        GameCtor = 9, Activate = 10, CloneTree = 11, CloneFields = 12, CloneProps = 13, CloneActivate = 14, Resolve = 15, Program = 16, Clone = 17, Drain = 18,
        CreateSprite = 19, CreateTexture = 20, CreateMaterial = 21, CreateShader = 22, CreateMesh = 23, CreateText = 24, AnimInit = 25, AnimCtrl = 26, AnimClip = 27, AnimBind = 28, NestedCtor = 29, NestedAfter = 30, N = 31;
    [Conditional("SHEPHERD_LOADPROF")] public static void Enter(int z) { }
    [Conditional("SHEPHERD_LOADPROF")] public static void Exit(int z) { }
    [Conditional("SHEPHERD_LOADPROF")] public static void CloneDone(int objs) { }
    [Conditional("SHEPHERD_LOADPROF")] public static void TypeDone(Type t, long t0) { }
    [Conditional("SHEPHERD_LOADPROF")] public static void SceneDone(string name) { }
    public static long Now() => 0;
    public static string Summary() => null;
}
#endif
