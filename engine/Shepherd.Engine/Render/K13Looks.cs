namespace Shepherd.Engine.Render;

/// <summary>Kit #13 track looks (vita/notes/kit13-looks.md): the fidelity rows of the render path, each behind its own env-file line, all
/// under one master. SHEPHERD_K13_LOOKS=0 = every row off = kit #12's behaviour (no new GL call, no new program, no new id registered).
/// Every engine default here is OFF, so the Mac fixtures and every kit #12 record stay as they are; the kit's defaults table turns the rows on
/// (the owner's rule: what matches the original goes on, each with its switch back).
/// Rows:
/// - SHEPHERD_K13_MASKFIX=1: a UI draw's colour mask and stencil come from what its shader's pass really uses (the pass's ColorMask, fixed or
///   driven by a material float; the Stencil block only on a shader that declares uGUI's stencil properties), not from whatever floats a
///   material happens to carry. Materials keep stale saved properties of shaders they once had: the Settings background and the Verify
///   Files bands carry `_ColorMask` 35.9 on a shader whose pass writes RGBA, so with kit #12's stencil masks they wrote blue + alpha only
///   (the yellow Settings screen, the purple bands). Applies only while the kit #12 stencil masks are on.
/// - SHEPHERD_K13_PERSP=1: perspective-correct texture mapping of world sprites / meshes under a perspective camera (w to the GPU).
/// - SHEPHERD_K13_WCWORLD=1: Watercolor/Watercolor-Sprite on world SpriteRenderers (kit #12's program; owner-local Derived).
/// - SHEPHERD_K13_KEYART=1: the KeyArt/Follower, BillboardDoodle and KeyArt/Candles programs (owner-local Derived).
/// - SHEPHERD_K13_LOGO=1: the menu logo's LogoDesaturate program (owner-local Derived).
/// - SHEPHERD_K13_SDF=1: TextMeshPro's distance-field alpha exactly (per-vertex scale + the material's values).
/// - SHEPHERD_K13_BARS=1: Settings' Cutscene Bars offers only On (the owner's pick until every cutscene is checked).</summary>
[ProfileOnly]
public static class K13Looks
{
    /// <summary>SHEPHERD_K13_LOOKS: unset / 1 = the rows as their own lines say; 0 = every kit #13 looks row off (kit #12).</summary>
    public static readonly bool Master = Environment.GetEnvironmentVariable("SHEPHERD_K13_LOOKS") is not { } m || m.Trim() != "0";
    /// <summary>A row: the master on and its env line "1" (or unset with <paramref name="dflt"/> true); "0" turns it off.</summary>
    public static bool Row(string name, bool dflt)
    {
        if (!Master) return false;
        var v = Environment.GetEnvironmentVariable(name);
        return v is { Length: > 0 } ? v.Trim() == "1" : dflt;
    }
    /// <summary>SHEPHERD_K13_MASKFIX=1: the colour mask / stencil of a UI draw as its shader's pass uses them.</summary>
    public static readonly bool MaskFix = Row("SHEPHERD_K13_MASKFIX", false);
    /// <summary>SHEPHERD_K13_PERSP=1: world draws under a perspective camera hand each vertex's clip w to the GPU (perspective-correct uv and
    /// colour, as Unity's GPU interpolates them). A draw whose every w is exactly 1 (an orthographic camera) is drawn as before.</summary>
    public static readonly bool Persp = Row("SHEPHERD_K13_PERSP", false);
    /// <summary>SHEPHERD_K13_LOGO=1: the logo's "LogoDesaturate" drawn by its own program (owner-local Derived, through IUiLooks), not as texture x colour.</summary>
    public static readonly bool Logo = Row("SHEPHERD_K13_LOGO", false);
    /// <summary>SHEPHERD_K13_WCWORLD=1: world SpriteRenderers with "Watercolor/Watercolor-Sprite" drawn by its program (owner-local Derived, IWorldLooks).</summary>
    public static readonly bool WcWorld = Row("SHEPHERD_K13_WCWORLD", false);
    /// <summary>SHEPHERD_K13_KEYART=1: "KeyArt/Follower", "BillboardDoodle" (world) drawn by their programs (owner-local Derived, IWorldLooks).</summary>
    public static readonly bool KeyArt = Row("SHEPHERD_K13_KEYART", false);
    /// <summary>A material float that a colour of the same name hides in NMaterial.P (the loader's lists: floats, then colours) is kept under
    /// <c>id | FloatShadow</c> -- what a transcribed program reads when its shader declares the float (the logo's _Size1: float 10, colour
    /// (6.13, 4.93, 0, 0)). Only while a row that reads such floats is on: with the rows off the materials are kit #12's.</summary>
    public const int FloatShadow = 0x10000000;
    public static readonly bool Shadow = Logo || WcWorld || KeyArt;
    /// <summary>SHEPHERD_K13_CAMUI=1 (experiment): Screen Space - Camera canvases drawn before the camera's post effects (colour grading, the
    /// Stylizer), as Unity's camera renders them; the overlay canvases after them. The retained UI pass only (UiCacheCfg.On).</summary>
    public static readonly bool CamUi = Row("SHEPHERD_K13_CAMUI", false);
    /// <summary>SHEPHERD_K13_SDF=1: TextMeshPro's distance-field alpha exactly -- each text mesh's second uv channel (TMP's per-glyph scale) is
    /// kept, and the UI pass draws a TMP SDF text with per-vertex parameters (scale, bias, clip, softness) and its material's face colour through
    /// an owner-local program (ITextLooks + IK13Sdf), instead of the fixed smoothstep band of the sprite program's SDF variant (letters with ~1 px
    /// stems vanished at 960x544). A material with an outline, a face / outline texture or an underlay is declined (drawn as before).</summary>
    public static readonly bool Sdf = Row("SHEPHERD_K13_SDF", false);
    static System.Runtime.CompilerServices.ConditionalWeakTable<Native.NMesh, UnityEngine.Vector2[]> _meshUv1;
    static System.Runtime.CompilerServices.ConditionalWeakTable<Native.NCanvasRenderer, UnityEngine.Vector2[]> _crUv1;
    /// <summary>(SHEPHERD_K13_SDF=1) a mesh's second uv channel, kept beside the mesh record (the records keep their layout).</summary>
    internal static void SetMeshUv1(Native.NMesh m, UnityEngine.Vector2[] uv) { if (uv == null) { _meshUv1?.Remove(m); return; } _meshUv1 ??= new(); _meshUv1.AddOrUpdate(m, uv); }
    internal static UnityEngine.Vector2[] MeshUv1(Native.NMesh m) => _meshUv1 != null && _meshUv1.TryGetValue(m, out var u) ? u : null;
    internal static void SetCrUv1(Native.NCanvasRenderer c, UnityEngine.Vector2[] uv) { _crUv1 ??= new(); if (uv == null) _crUv1.Remove(c); else _crUv1.AddOrUpdate(c, uv); }
    internal static UnityEngine.Vector2[] CrUv1(Native.NCanvasRenderer c) => _crUv1 != null && _crUv1.TryGetValue(c, out var u) ? u : null;
    /// <summary>CanvasRenderer.SetMesh: the renderer's copy of the mesh's uv2 (null: none) -- its current array refilled when the length matches
    /// (only the UI pass reads it, within the draw), else a new copy.</summary>
    internal static void CopyCrUv1(Native.NCanvasRenderer c, UnityEngine.Vector2[] src)
    {
        if (src == null) { _crUv1?.Remove(c); return; }
        var cur = CrUv1(c);
        if (cur != null && cur.Length == src.Length) { Array.Copy(src, cur, src.Length); return; }
        SetCrUv1(c, (UnityEngine.Vector2[])src.Clone());
    }
    /// <summary>SHEPHERD_K13_BARS=1: Settings > Game > PS Vita's Cutscene Bars offers only On (the owner's pick 2026-11-05: Off stays hidden until
    /// every cutscene is checked). The row stays; a press leaves it On; a saved Off is read as On but written back as Off by every save (so 0
    /// finds it again) -- except after the game's Reset All, which puts the row back to its default On, as kit #12's Reset All. 0 / unset = kit #12's On / Off row. The row's description strip keeps the owner's verbatim text (his call).</summary>
    public static readonly bool BarsOnly = Row("SHEPHERD_K13_BARS", false);
    /// <summary>Perspective draws handed to a backend / to its fallback (no IK13Persp, or its programs refused) since start.</summary>
    internal static long PerspDraws, PerspFallback, SdfDraws;

    /// <summary>The w stream as the backends carry it: one FColor per vertex in the added-colour stream's slot, (0, 0, 0, w).</summary>
    internal static void EncW(ReadOnlySpan<float> w, Span<Sdl.FColor> dst)
    {
        for (int i = 0; i < w.Length; i++) { ref var d = ref dst[i]; d.r = 0f; d.g = 0f; d.b = 0f; d.a = w[i]; }
    }
    /// <summary>A lit draw's added colours with each vertex's w folded into the fourth component: the premultiplied mark (0 or 1) becomes the
    /// sign of w (w is clamped positive, as SceneRenderer.Project clamps it). False when a mark is neither 0 nor 1 (then drawn without w).</summary>
    internal static bool EncAddW(ReadOnlySpan<Sdl.FColor> add, ReadOnlySpan<float> w, Span<Sdl.FColor> dst)
    {
        for (int i = 0; i < w.Length; i++)
        {
            var a = add[i]; if (a.a != 0f && a.a != 1f) return false;
            ref var d = ref dst[i]; d.r = a.r; d.g = a.g; d.b = a.b; d.a = a.a != 0f ? -w[i] : w[i];
        }
        return true;
    }
    /// <summary>Back from the folded stream to the added colours (a backend whose perspective programs were refused).</summary>
    internal static void DecAdd(ReadOnlySpan<Sdl.FColor> enc, Span<Sdl.FColor> dst)
    {
        for (int i = 0; i < enc.Length; i++) { var e = enc[i]; ref var d = ref dst[i]; d.r = e.r; d.g = e.g; d.b = e.b; d.a = e.a < 0f ? 1f : 0f; }
    }
}

/// <summary>Kit #13 looks (K13Looks.Sdf): a backend that draws a TMP distance-field text with one parameter vec4 per vertex through the owner-local
/// program (GL: "sdf-exact" = sprite.vert's ADD variant + Derived k13_sdf.frag, built on first use; the null backend counts; the multicore
/// proxy records). False = the program is unavailable (the caller draws as before). <paramref name="before"/> / <paramref name="beforeBlend"/>:
/// the same draw as it was before the provider rewrote its colours (the vertices differ only in colour) -- what a deferred backend (the multicore
/// proxy) draws, through the SDF program as before, when the program turns out to be refused at replay.</summary>
public interface IK13Sdf
{
    bool DrawSdfX(IntPtr tex, uint blend, Span<Sdl.Vertex> v, Span<int> idx, Span<Sdl.FColor> param, Span<Sdl.Vertex> before, uint beforeBlend);
}

/// <summary>Kit #13 looks (K13Looks.Persp): a backend that draws with per-vertex clip w (GL: the sprite program's PERSP variants; the null
/// backend counts; the multicore proxy records; the tee forwards). w is in the draw's vertex order, one per vertex, every value &gt; 0.</summary>
public interface IK13Persp
{
    /// <summary>A plain draw (texture x vertex colour) with w.</summary>
    void DrawW(IntPtr tex, uint blend, Span<Sdl.Vertex> v, Span<int> idx, ReadOnlySpan<float> w);
    /// <summary>A lit draw (IAddDraw.DrawAdd) with w.</summary>
    void DrawAddW(IntPtr tex, uint blend, Span<Sdl.Vertex> v, Span<int> idx, Span<Sdl.FColor> add, ReadOnlySpan<float> w);
    /// <summary>The same with the stream already folded (EncW / EncAddW): the multicore replay's call.</summary>
    void DrawEnc(IntPtr tex, uint blend, Span<Sdl.Vertex> v, Span<int> idx, Span<Sdl.FColor> enc, bool add);
}
