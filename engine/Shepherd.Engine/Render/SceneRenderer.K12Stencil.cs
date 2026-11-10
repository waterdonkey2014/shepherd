using Shepherd.Engine.Native;
namespace Shepherd.Engine.Render;

// Kit #12 boot track (Render/K12Stencil.cs, SHEPHERD_K12_STENCIL=1): the UI passes' stencil calls. Both passes (the retained one and the
// reference) call these at the same points: the draw's state right after its clip decision, a renderer's pop draws after its children, the
// state off after the pass. Nothing here runs with the switch off.
public static partial class SceneRenderer
{
    /// <summary>The backend's stencil interface (GlBackend; the null backend; the multicore proxy), or null.</summary>
    internal static IK12Stencil StencilBackend => G as IK12Stencil;
    /// <summary>The draw being submitted writes no colour (ColorMask 0): the black-frame watch does not count it.</summary>
    static bool _k12NoColour;
    static K12StencilState _k12Last = K12StencilState.None;

    /// <summary>A draw whose colour mask is 0 is skipped (polish round 1's rule) unless it writes the stencil (a Mask's graphic / unmask draw).</summary>
    static bool K12SkipNoColour(NMaterial smat)
    {
        if (smat == null) return false;
        var s = K12Stencil.Of(smat);
        return s.ColorMask == 0 && !s.Writes;
    }
    /// <summary>The draw's stencil state goes to the backend when it differs from the last one handed over.</summary>
    static void K12StencilFor(NMaterial smat)
    {
        var s = smat != null ? K12Stencil.Of(smat) : K12StencilState.None;
        _k12NoColour = s.ColorMask == 0;
        if (UiCap != null && UiCapOnly) return;   // (SHEPHERD_UICACHE_CHECK's silent reference pass: no backend call)
        if (s.Equals(_k12Last)) return;
        _k12Last = s;
        StencilBackend?.Stencil(s);
    }
    static void K12StencilEnd()
    {
        K12Stencil.PassEnd();   // (review fix: the per-pass state cache goes with the pass)
        _k12NoColour = false;
        if (UiCap != null && UiCapOnly) return;
        if (K13Speed.Masks) K13Speed.PopsNote();   // kit #13 speed: the MASKS row's evidence (a line when the pop state changes; no GL call)
        if (_k12Last.Equals(K12StencilState.None)) return;
        _k12Last = K12StencilState.None;
        StencilBackend?.Stencil(K12StencilState.None);
    }
    /// <summary>The renderers of <paramref name="go"/> with pop materials (a Mask's unmask material): their mesh again with each pop material's
    /// stencil state and colour mask, after the children -- what Unity's canvas does with CanvasRenderer.SetPopMaterial.</summary>
    static void K12PopDraws(NGameObject go, MaskClip clip, bool fast)
    {
        if (K13Speed.Masks) { if (!K12Stencil.AnyPops) { K13Speed.PopSkips++; return; } K13Speed.PopLooks++; }   // kit #13 speed: no renderer holds a pop material now -- nothing to look up under any node (counted: the [k13] line)
        var comps = go.Components;
        for (int k = 0; k < comps.Count; k++)
        {
            if (comps[k] is not NCanvasRenderer cr || cr.Destroyed || cr.Cull || cr.Idx.Length == 0) continue;
            var pops = K12Stencil.Pops(cr); if (pops == null) continue;
            for (int i = 0; i < pops.Length; i++)
            {
                if (pops[i] == null) continue;
                K12Stencil.PopMat = pops[i];
                try { if (fast) DrawCanvasRendererFast(cr, clip); else DrawCanvasRenderer(cr, clip); }
                finally { K12Stencil.PopMat = null; }
            }
        }
    }
}
