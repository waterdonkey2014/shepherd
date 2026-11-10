namespace Shepherd.Engine.Render;

/// <summary>Kit #12 track menusa (vita/notes/kit12-menusa.md): the UI render path's switches and counters -- menus and text. Every switch is
/// read once, from the app's env file; every one is OFF unless its line says otherwise, so a run without these lines is kit #11's, call for
/// call. SHEPHERD_K12_MENUSA=0 turns every one of them off at once (the switch back, whatever the other lines say).
/// The literals live in a [ProfileOnly] type, so the V3 diet's readable-key scan (Assets/Diet.cs) is unchanged.
/// - SHEPHERD_K12_TEXTCACHE=1: the text-mesh cache, engine half. CanvasRenderer.SetMesh keeps the renderer's current channel array when the
///   incoming mesh's channel is bit for bit the same (no clone, no allocation), so the retained UI pass sees the same array and reuses the
///   renderer's block. TMP / uGUI regenerate a mesh whenever anything marks it dirty -- often with the very same bytes (a layout pass that
///   lands on the same rect, a counter text set to the value it already shows); the bytes handed to the backend are the ones a clone would
///   have produced (equal content), so every draw is unchanged. (TMP's own mesh generation is the game's code and still runs.)
/// - SHEPHERD_K12_COLPATCH=1: patch only changed colour bytes. A retained UI block whose positions, uvs, indices and matrix are the block's
///   own and whose only changed input is its tint (a fade, a CanvasGroup alpha tween, a colour tween) or its per-vertex colours (TMP
///   per-letter colour, with the text cache keeping the other channels' arrays) gets new colours written in place -- the build loop's own
///   colour expressions on the same values -- instead of a full rebuild; positions / uvs / indices / partial sums stay as built.
/// - SHEPHERD_K12_RAYMEMO=1: UI raycasts reused while nothing moves. RectTransformUtility.PointInRectangle (the GraphicRaycaster's test of every
///   raycast target, every frame while the pointer is on the screen) keeps, per rect, the screen matrix it tested with and the inputs it is a
///   function of (the rect's chain stamp, its root canvas and the root's chain stamp, the root's scale factor, the screen size); while they
///   are unchanged the matrix is reused instead of recomputed (two matrix products + a TRS per graphic per frame). The answer is computed
///   from the same matrix bits, so it is the same answer.
/// - SHEPHERD_K12_UIREPLAY=1: recorded command lists for fixed screens + incremental UI re-batching (GL backend only; Render/GlBackend.K12.cs).
///   The UI pass's backend draws go into a recording; when a frame's recording is call for call the last frame's (same blocks with the same
///   content, textures, blends, clips), the backend replays last frame's batches from its kept vertex buffer (no vertex copy, no upload);
///   otherwise the draws run as before. The GPU sees the same vertices and the same draw calls in the same order.
///   GL call stream changes by design (fewer uploads): a GPU-path row, default OFF until device numbers decide (brief).
/// - SHEPHERD_K12_UICHECK=1 (test only): every UIREPLAY frame also builds the batches as before and compares them with the replay's (a fully
///   replayed frame) or with the kept prefix a take would copy (every incremental frame, at the take), and COLPATCH re-runs the full build
///   into a scratch block and compares the bytes; a difference is logged (first 20) and counted.</summary>
[ProfileOnly]
public static class K12Ui
{
    static string E(string k) => Environment.GetEnvironmentVariable(k);
    /// <summary>SHEPHERD_K12_MENUSA=0: every switch of this track off (kit #11).</summary>
    public static readonly bool Master = E("SHEPHERD_K12_MENUSA") != "0";
    public static readonly bool TextCache = Master && E("SHEPHERD_K12_TEXTCACHE") == "1";
    public static readonly bool ColPatch = Master && FpsCfg.InlineVertex && E("SHEPHERD_K12_COLPATCH") == "1";
    public static readonly bool RayMemo = Master && E("SHEPHERD_K12_RAYMEMO") == "1";
    public static readonly bool UiReplay = Master && E("SHEPHERD_K12_UIREPLAY") == "1";
    public static readonly bool Check = Master && E("SHEPHERD_K12_UICHECK") == "1";
    /// <summary>Test only (negative controls of the checks): SHEPHERD_K12_NEGCTL=colpatch writes a wrong colour in every colour patch (the
    /// colour check and the pixel check must fail); =replay writes the wrong red <see cref="NegR"/> into the kept vertex 0 of every replayed
    /// frame and re-uploads it (the replay check, from the second replay of a kept pass on, and the pixel check must fail); =prefix writes it
    /// into the kept vertex 0 at every prefix take, before the take copies it or the check compares it (the prefix check and the pixel check
    /// must fail); =raystale keeps the raycast matrix whatever moved (the touch route's streams must differ).</summary>
    public static readonly string NegCtl = Master ? E("SHEPHERD_K12_NEGCTL") ?? "" : "";
    public static readonly bool NegReplay = NegCtl == "replay", NegPrefix = NegCtl == "prefix";
    /// <summary>The negative controls' wrong red: a constant (writing it twice changes nothing, so a kept copy never turns back into the
    /// original), and not a byte's value k / 255 (95 / 256).</summary>
    public const float NegR = 0.37109375f;
    /// <summary>Any switch of this track on: the [k12ui] counters are kept and logged.</summary>
    public static readonly bool Any = TextCache || ColPatch || RayMemo || UiReplay;

    // ---------------- counters (run totals: the UI pass runs on the main thread, SetMesh / PointInRectangle on the game's thread = main) ----------------
    /// <summary>Text cache: SetMesh calls, and channels kept (Pos, Col, Uv, Tan, Idx) / cloned; meshes whose every channel was kept.</summary>
    public static long SetMeshCalls, SetMeshSame, ChKept, ChCloned, BytesNotCloned;
    /// <summary>Colour patch: blocks patched / their vertices; check mode: patches compared / differing.</summary>
    public static long ColPatches, ColPatchVerts, ColChecked, ColDiff;
    /// <summary>Raycast memo: PointInRectangle calls answered from a kept matrix / computed.</summary>
    public static long RayHits, RayMiss;
    /// <summary>UI replay (backend counts): frames replayed / recorded (kept for the next frame) / passes whose first draws were last frame's
    /// (incremental: a prefix taken) and those draws; draws and vertices replayed; check frames compared / differing.</summary>
    public static long ReplayFrames, ReplayRecorded, ReplayDiffer, ReplayPrefixDraws, ReplayDraws, ReplayVerts, ReplayChecked, ReplayCheckDiff;
    /// <summary>UI replay check mode: prefix takes compared / differing.</summary>
    public static long PrefixChecked, PrefixCheckDiff;
    /// <summary>A prefix take whose kept prefix differs from this frame's own build (GlBackend.K12ComparePrefix's codes: 1 counts or chunk base,
    /// 2 vertex bytes, 3 an index, 4 a batch).</summary>
    internal static void PrefixDiff(int frame, int k, int why, int at, int nv, int ni, int nb, int chunk, int vo, int io, int bo, int ch)
    {
        PrefixCheckDiff++;
        if (PrefixCheckDiff > 20) return;
        string w = why switch { 1 => $"counts {nv}/{ni}/{nb} chunk {chunk} vs kept {vo}/{io}/{bo} chunk {ch}", 2 => "vertex bytes", 3 => $"index {at}", _ => $"batch {at}" };
        Log.Warn($"[k12ui] prefix check: frame {frame}: the kept prefix of {k} draws differs from this frame's own build ({w})");
    }
    /// <summary>Last frame (for the [render] frame line).</summary>
    public static int LastColPatches, LastReplay; internal static int _colPatches;
    internal static void BeginUiPass() { _colPatches = 0; }
    internal static void EndUiPass() { LastColPatches = _colPatches; }

#if !SHEPHERD_EBOOT
    /// <summary>Mac measurement only (compiled out of the eboots): SHEPHERD_K12_FRAMECSV=&lt;file&gt; writes one line per frame (FrameTimes, so only
    /// with SHEPHERD_RENDER given): frame, whole frame ms, render part ms, managed bytes allocated, draws in / out, the UI pass's block hits /
    /// misses / moves, colour patches, replayed (0/1) -- the per-screen windows of the kit #12 menusa timing A/B.</summary>
    public static readonly string FrameCsv = Environment.GetEnvironmentVariable("SHEPHERD_K12_FRAMECSV") is { Length: > 0 } f ? f : null;
    static StreamWriter _csv;
    internal static void Csv(int i, double total, double render, long alloc, int din, int dout)
    {
        _csv ??= new StreamWriter(FrameCsv, false) { AutoFlush = false };
        _csv.WriteLine($"{i}\t{total:F3}\t{render:F3}\t{alloc}\t{din}\t{dout}\t{SceneRenderer.LastUiHits}\t{SceneRenderer.LastUiMisses}\t{SceneRenderer.LastUiMoves}\t{LastColPatches}\t{LastReplay}");
        if ((i & 255) == 0) _csv.Flush();
    }
    internal static void CsvEnd() => _csv?.Flush();
#endif
    static int B(bool b) => b ? 1 : 0;
    /// <summary>The end-of-run line (null with every switch off: the log is kit #11's).</summary>
    public static string Summary() => !Any ? null :
        $"[k12ui] menusa totals: switches textcache={B(TextCache)} colpatch={B(ColPatch)} raymemo={B(RayMemo)} uireplay={B(UiReplay)} check={B(Check)}{(NegCtl.Length > 0 ? " NEGCTL=" + NegCtl : "")}; " +
        $"setmesh calls={SetMeshCalls} all-same={SetMeshSame} channels kept={ChKept} cloned={ChCloned} bytes not cloned={BytesNotCloned}; " +
        $"colour patches={ColPatches} verts={ColPatchVerts} check-differ={ColDiff} checked={ColChecked}; raycast memo hit={RayHits} miss={RayMiss}; " +
        $"replay check-differ={ReplayCheckDiff} checked={ReplayChecked} prefix-check-differ={PrefixCheckDiff} prefix-checked={PrefixChecked} frames={ReplayFrames} recorded={ReplayRecorded} prefix-taken={ReplayDiffer} prefix draws={ReplayPrefixDraws} draws={ReplayDraws} verts={ReplayVerts}";
    /// <summary>The [render] frame line's [k12ui] part (only when a switch is on: the line is kit #11's otherwise).</summary>
    public static string Line() => !Any ? "" : $" | k12ui colpatch={LastColPatches} replay={LastReplay} setmesh-same={SetMeshSame} raymemo={RayHits}/{RayHits + RayMiss}";

    // ---------------- bit-exact array comparison (equal bytes = the clone would hold the same bytes) ----------------
    /// <summary>a[0..n) and b[0..n) hold the same bits (n words of 4 bytes each).</summary>
    internal static unsafe bool SameWords(uint* a, uint* b, int n)
    {
        int i = 0;
        for (; i + 4 <= n; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) return false;
        for (; i < n; i++) if (a[i] != b[i]) return false;
        return true;
    }
    internal static unsafe bool Same(UnityEngine.Vector3[] a, UnityEngine.Vector3[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        if (a.Length == 0) return true;
        fixed (UnityEngine.Vector3* pa = a, pb = b) return SameWords((uint*)pa, (uint*)pb, a.Length * 3);
    }
    internal static unsafe bool Same(UnityEngine.Vector4[] a, UnityEngine.Vector4[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        if (a.Length == 0) return true;
        fixed (UnityEngine.Vector4* pa = a, pb = b) return SameWords((uint*)pa, (uint*)pb, a.Length * 4);
    }
    internal static unsafe bool Same(UnityEngine.Color32[] a, UnityEngine.Color32[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        if (a.Length == 0) return true;
        fixed (UnityEngine.Color32* pa = a, pb = b) return SameWords((uint*)pa, (uint*)pb, a.Length);
    }
    /// <summary>cur holds exactly the submeshes' indices in order (what Flatten would make).</summary>
    internal static unsafe bool SameFlat(int[] cur, List<int[]> subs)
    {
        if (cur == null) return false;
        int n = 0; foreach (var s in subs) n += s.Length;
        if (n != cur.Length) return false;
        int o = 0;
        fixed (int* pc = cur)
            foreach (var s in subs)
            {
                if (s.Length == 0) continue;
                fixed (int* ps = s) if (!SameWords((uint*)(pc + o), (uint*)ps, s.Length)) return false;
                o += s.Length;
            }
        return true;
    }
}
