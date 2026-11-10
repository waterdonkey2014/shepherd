using Shepherd.Engine.Native;
namespace Shepherd.Engine.Render;

/// <summary>Kit #12 boot track (vita/notes/kit12-boot.md section 1): uGUI's stencil Masks drawn with a real stencil buffer, as Unity draws them.
///
/// Before (polish round 1): every enabled uGUI Mask was approximated by its graphic's screen-space bounding box (a scissor), exact for the
/// rectangular masks of menus but a SQUARE for a round one -- the boot loading screen's two rotating rings ('Radial Bars': a radially filled
/// circle as the Mask's graphic, the scrolling-noise ProgressBarInner under it) drew as two white squares.
///
/// Now (SHEPHERD_K12_STENCIL=1, opt-in): the UI pass reads the stencil state the game's own uGUI code put on each material -- uGUI's
/// StencilMaterial makes every masked material with _Stencil / _StencilComp / _StencilOp / _StencilReadMask / _StencilWriteMask / _ColorMask
/// and, for a stencil-writing material, _UseUIAlphaClip (the UNITY_UI_ALPHACLIP keyword: fragments with alpha below 0.001 are discarded, so
/// a mask sprite's transparent texels write no stencil) -- and hands it to the backend with each draw; the Mask's "unmask" material (the
/// CanvasRenderer's pop material, CanvasRenderer.SetPopMaterial: stored here while the switch is on) is drawn after the Mask's children with
/// the same mesh, as Unity's canvas does, so the stencil returns to 0 for the next mask. The scissor box is then not used for Masks (RectMask2D
/// keeps its own rect clip, as in Unity). The backend clears the stencil once a frame before its first masked draw.
///
/// GL (GlBackend, IK12Stencil): the 8 new entry points of Gl.cs (glStencilFunc / Op / Mask, glColorMask, the renderbuffer calls) -- the Vita
/// host's static table (vita/gltest/shepgl: 59 entries), its ABI check and vlx's recorder know them -- a D24S8 renderbuffer attached to the two
/// main targets on first use, and an alpha-clip copy of the sprite program (Render/Shaders/k12clip.frag, built on first use outside the boot
/// set and the gxp cache). SHEPHERD_MT_RENDER=1 records the state like a clip. The null backend counts.
///
/// Switch: SHEPHERD_K12_STENCIL=1 (default OFF: every kit #11 record unchanged -- no new GL call, no new program, no new allocation; the
/// in-game default waits for the owner's A/B: a round mask now looks like the PC's instead of a square, a documented difference that matches
/// the original). The boot screen's recordings (vita/tools/bootscreen_record.sh) are made with it on. SHEPHERD_K12_BOOT=0 = every kit #12
/// boot row off.</summary>
[ProfileOnly]
public static class K12Stencil
{
    /// <summary>SHEPHERD_K12_STENCIL=1 and the kit #12 boot master on.</summary>
    public static readonly bool On = Runtime.K12Boot.Row("SHEPHERD_K12_STENCIL", false);
    /// <summary>The backend draws the stencil (set once the backend exists: Active = On and the backend has IK12Stencil).</summary>
    public static bool Active => On && SceneRenderer.StencilBackend != null;

    // ---- the pop materials (CanvasRenderer.SetPopMaterial): only stored while the switch is on ----
    static Dictionary<NCanvasRenderer, NMaterial[]> _pop;
    public static void SetPop(NCanvasRenderer cr, NMaterial m, int index)
    {
        if (cr == null || index < 0 || index > 7) return;
        _pop ??= new Dictionary<NCanvasRenderer, NMaterial[]>();
        if (!_pop.TryGetValue(cr, out var a) || a.Length <= index) { var b = new NMaterial[index + 1]; if (a != null) Array.Copy(a, b, a.Length); _pop[cr] = a = b; }
        a[index] = m;
    }
    public static NMaterial GetPop(NCanvasRenderer cr, int index) => _pop != null && cr != null && _pop.TryGetValue(cr, out var a) && index >= 0 && index < a.Length ? a[index] : null;
    public static void SetPopCount(NCanvasRenderer cr, int n)
    {
        if (cr == null) return;
        if (n <= 0) { _pop?.Remove(cr); return; }
        _pop ??= new Dictionary<NCanvasRenderer, NMaterial[]>();
        if (!_pop.TryGetValue(cr, out var a)) _pop[cr] = new NMaterial[n];
        else if (a.Length != n) { var b = new NMaterial[n]; Array.Copy(a, b, Math.Min(a.Length, n)); _pop[cr] = b; }
    }
    public static int PopCount(NCanvasRenderer cr) => _pop != null && cr != null && _pop.TryGetValue(cr, out var a) ? a.Length : 0;
    /// <summary>Kit #13 speed (K13Speed.Masks): some renderer holds pop materials now (a Mask is enabled somewhere); false = every pop lookup
    /// of the pass would find nothing.</summary>
    public static bool AnyPops => _pop != null && _pop.Count > 0;
    /// <summary>Kit #13 speed: what the held entries are (K13Speed.PopsNote's lines and the [k13] line; never per frame).</summary>
    internal static string PopCensus()
    {
        int n = 0, dead = 0, cull = 0, empty = 0;
        if (_pop != null) foreach (var kv in _pop) { n++; var cr = kv.Key; if (cr.Destroyed) dead++; else if (cr.Cull) cull++; else if (cr.Idx.Length == 0) empty++; }
        return $"{n} renderer(s) hold pop materials: {dead} destroyed, {cull} culled, {empty} with no mesh, {n - dead - cull - empty} drawable";
    }
    /// <summary>The renderer's pop materials, or null (none / switch off).</summary>
    public static NMaterial[] Pops(NCanvasRenderer cr)
    {
        if (_pop == null || !_pop.TryGetValue(cr, out var a)) return null;
        if (cr.Destroyed) { _pop.Remove(cr); return null; }   // (a destroyed renderer's entry goes when the pass meets it)
        return a;
    }
    /// <summary>A destroyed renderer's entry goes (the sweep of the UI pass).</summary>
    public static void Forget(NCanvasRenderer cr) => _pop?.Remove(cr);

    /// <summary>The draw being submitted is a renderer's pop draw: its stencil state and colour mask come from this material.</summary>
    [ThreadStatic] internal static NMaterial PopMat;

    // ---- the stencil state of a material (the last one remembered within a UI pass: P changes only through game code, never during a pass) ----
    /// <summary>(a class of its own: the property names get their ids only when a masked draw is first read -- reading K12Stencil.On with the
    /// switch off registers no name, so the engine's id numbering stays kit #11's)</summary>
    static class Ids
    {
        internal static readonly int Ref = Impl.ShaderIds.Id("_Stencil"), Comp = Impl.ShaderIds.Id("_StencilComp"), Op = Impl.ShaderIds.Id("_StencilOp"),
            Read = Impl.ShaderIds.Id("_StencilReadMask"), Write = Impl.ShaderIds.Id("_StencilWriteMask"), ColorMask = Impl.ShaderIds.Id("_ColorMask"),
            Clip = Impl.ShaderIds.Id("_UseUIAlphaClip");
    }
    static int I(NMaterial m, int id, int d) => m.P.TryGetValue(id, out var v) ? v switch { float f => (int)f, int i => i, double x => (int)x, _ => d } : d;
    // review fix (finding 8): the comment above promised a cache and there was none -- every draw read its material twice (its colour-mask skip,
    // then its state: 7 property lookups + a keyword lookup each). Now the last material read is remembered with its state, so a draw's second
    // ask and every following draw with the same material (uGUI's shared default and mask materials come in runs) cost one pointer compare.
    // Forgotten after every UI pass (SceneRenderer.K12StencilEnd -> PassEnd) and on a new frame (game code may change P between passes). A
    // per-pass table for alternating materials was tried and dropped: Dictionary<NMaterial, K12StencilState> instantiated the generic dictionary
    // over a struct -- +29.6 KB of eboot text and +17.8 KB of AOT tables on every app (VLX b8 builds: 00097's span crossed a 64 KiB step).
    // The same states as before -- the same GL calls and records; nothing is allocated.
    static NMaterial _lastM; static K12StencilState _lastS; static int _lastFrame = int.MinValue;
    /// <summary>The state a draw with this material has (Unity's ShaderLab Stencil block + ColorMask, as uGUI's StencilMaterial sets them).</summary>
    public static K12StencilState Of(NMaterial m)
    {
        if (m == null) return K12StencilState.None;
        int f = Runtime.Time.FrameCount;
        if (ReferenceEquals(m, _lastM) && f == _lastFrame) return _lastS;
        _lastS = Read(m); _lastM = m; _lastFrame = f;
        return _lastS;
    }
    /// <summary>The UI pass ended (game code may change materials before the next one): the remembered state goes.</summary>
    public static void PassEnd() => _lastM = null;
    static K12StencilState Read(NMaterial m)
    {
        int comp = I(m, Ids.Comp, 8), op = I(m, Ids.Op, 0), r = I(m, Ids.Ref, 0), rd = I(m, Ids.Read, 255), wr = I(m, Ids.Write, 255), cm = I(m, Ids.ColorMask, 15);
        bool clip = I(m, Ids.Clip, 0) != 0 || m.Keywords.Contains("UNITY_UI_ALPHACLIP");
        // kit #13 looks (K13Looks.MaskFix): what the shader's pass really uses. Its ColorMask is the pass state's (fixed, or the material float
        // that drives it); its Stencil block exists only on a shader with uGUI's stencil properties (StencilMaterial.Add's HasProperty test:
        // the engine's HasProperty says yes to everything, so a copy may exist that Unity would not have made). A material keeps the saved
        // floats of shaders it once had: 'Watercolor_Short_B' carries _ColorMask 35.9 on a pass that writes RGBA (kit 12: blue + alpha only).
        if (K13Looks.MaskFix && m.Shader?.State is { } st)
        {
            cm = st.ColMaskProp != 0 ? I(m, st.ColMaskProp, st.ColMask) : st.ColMask;
            if (!st.DeclStencil) { comp = 8; op = 0; r = 0; rd = 255; wr = 255; }
        }
        return new K12StencilState((byte)Math.Clamp(comp, 0, 8), (byte)Math.Clamp(op, 0, 7), (byte)r, (byte)rd, (byte)wr, (byte)(cm & 15), clip);
    }
    /// <summary>The state of a draw with this material writes the stencil (then a ColorMask 0 draw must still be drawn).</summary>
    public static bool Writes(NMaterial m) { var s = Of(m); return !s.TestOff || s.Writes; }

    public static string Describe() => $"[k12] stencil masks ON (SHEPHERD_K12_STENCIL=1): uGUI Masks drawn with the stencil state of their materials (StencilMaterial) and their pop (unmask) materials, " +
        $"instead of the graphic's bounding box; backend {(SceneRenderer.StencilBackend != null ? "has IK12Stencil" : "has NO stencil: Masks keep the bounding box")}";
}

/// <summary>Kit #12 boot: one draw's stencil state and colour mask. Comp: Unity's CompareFunction (0 Disabled, 1 Never .. 8 Always); Op: Unity's
/// StencilOp (0 Keep, 1 Zero, 2 Replace, 3 IncrSat, 4 DecrSat, 5 Invert, 6 IncrWrap, 7 DecrWrap) -- the pass operation; the fail and depth-fail
/// operations are Keep (uGUI never sets them); ColorMask: Unity's ColorWriteMask (8 R, 4 G, 2 B, 1 A); Clip: discard alpha &lt; 0.001.</summary>
public readonly struct K12StencilState : IEquatable<K12StencilState>
{
    public readonly byte Comp, Op, Ref, ReadMask, WriteMask, ColorMask; public readonly bool Clip;
    public K12StencilState(byte comp, byte op, byte r, byte read, byte write, byte colorMask, bool clip) { Comp = comp; Op = op; Ref = r; ReadMask = read; WriteMask = write; ColorMask = colorMask; Clip = clip; }
    /// <summary>No stencil test, every colour written, no clip (the default UI material; the state before any mask).</summary>
    public static readonly K12StencilState None = new(8, 0, 0, 255, 255, 15, false);
    /// <summary>The test always passes and nothing is written: the stencil test can stay disabled.</summary>
    public bool TestOff => (Comp == 8 || Comp == 0) && !Writes;
    public bool Writes => Op != 0 && WriteMask != 0;
    public bool Plain => TestOff && ColorMask == 15 && !Clip;
    public bool Equals(K12StencilState o) => Comp == o.Comp && Op == o.Op && Ref == o.Ref && ReadMask == o.ReadMask && WriteMask == o.WriteMask && ColorMask == o.ColorMask && Clip == o.Clip;
    public override bool Equals(object o) => o is K12StencilState s && Equals(s);
    public override int GetHashCode() => Pack1() ^ (Pack2() << 7);
    /// <summary>Two ints for the multicore recorder: comp | op &lt;&lt; 4 | ref &lt;&lt; 8 | read &lt;&lt; 16 | write &lt;&lt; 24, colour mask | clip &lt;&lt; 4.</summary>
    public int Pack1() => Comp | Op << 4 | Ref << 8 | ReadMask << 16 | WriteMask << 24;
    public int Pack2() => ColorMask | (Clip ? 16 : 0);
    public static K12StencilState Unpack(int a, int b) => new((byte)(a & 15), (byte)((a >> 4) & 15), (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24), (byte)(b & 15), (b & 16) != 0);
    public override string ToString() => $"comp {Comp} op {Op} ref {Ref} read {ReadMask} write {WriteMask} colour {ColorMask}{(Clip ? " clip" : "")}";
}

/// <summary>Kit #12 boot: a backend that draws with a stencil buffer (GL; the null backend counts; the multicore proxy records). The state applies
/// to the draws that follow until the next call; <see cref="K12StencilState.None"/> ends it.</summary>
public interface IK12Stencil
{
    void Stencil(in K12StencilState s);
}
