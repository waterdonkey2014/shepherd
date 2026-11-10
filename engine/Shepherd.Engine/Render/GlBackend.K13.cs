using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

// Kit #13 track looks (Render/K13Looks.cs): the GL backend's perspective-correct world draws (SHEPHERD_K13_PERSP=1).
// A world draw under a perspective camera arrives with each vertex's clip w. The w rides in the CPU batcher's added-colour stream (attribute 3,
// a_add: the ADD program's slot, kept valid for every draw since fidelity round 1), (0, 0, 0, w) for a plain draw, the lit draw's added colour
// with its premultiplied mark folded into the sign of w for a lit one; the batch's program is the sprite program's PERSP variant (sprite.vert:
// gl_Position = (ndc * w, 0, w)), so the GPU interpolates uv and colour perspective-correctly, as Unity's GPU does with its clip-space positions.
// The pixel positions are the CPU's (the same x / y as before); a draw whose every w is 1 never comes here. Two programs, built on first use
// (not in the boot set or the gxp cache: the boot track's set decides that); refused = the draws go on as before (affine), one line says so.
// The attribute bindings (part of the gxp cache's key) are unchanged: the PERSP programs read a_add, which every program already binds.
public sealed unsafe partial class GlBackend
{
    const byte PPersp = 4, PAddPersp = 5;
    Prog _persp, _addPersp; bool _perspTried, _perspOk;
    FColor[] _k13Enc = new FColor[0];
    bool PerspProgs()
    {
        if (_perspTried) return _perspOk;
        _perspTried = true;
        try
        {
            _persp = Build("sprite+PERSP", "sprite.vert", "sprite.frag", "PERSP", FragPrecision, null);
            if (_add != null) _addPersp = Build("sprite+ADD+PERSP", "sprite.vert", "sprite.frag", "ADD+PERSP", FragPrecision, null);
            Log.Info($"[k13] perspective draws: 'sprite+PERSP' {_persp.CompileMs:F1} ms{(_addPersp != null ? $", 'sprite+ADD+PERSP' {_addPersp.CompileMs:F1} ms" : "")} compiled+linked on first use (not in the boot set or the gxp cache)");
            _perspOk = true;
        }
        catch (Exception e)
        {
            Log.Warn($"[k13] FALLBACK: the shader compiler rejected a perspective program ({e.Message.Split('\n')[0]}) -- world draws keep the affine mapping (kit #12)");
            _persp = _addPersp = null; _perspOk = false;
        }
        return _perspOk;
    }
    Prog PerspProg(byte p) => p == PPersp ? _persp : p == PSdfX ? _sdfx : _addPersp;
    /// <summary>(kit #13 integration, bootc x looks: GlBackend.K13LooksBoot) the PERSP programs came with the boot set: settle them as PerspProgs
    /// would have on first use -- both or neither ('sprite+ADD+PERSP' only while the lit path's sprite+ADD stands), one line either way.</summary>
    bool _k13PerspBoot;
    void K13PerspBootDone()
    {
        _perspTried = true;
        if (_add == null) _addPersp = null;
        _perspOk = _persp != null && (_add == null || _addPersp != null);
        if (_perspOk) Log.Info($"[k13] perspective draws: 'sprite+PERSP'{(_addPersp != null ? ", 'sprite+ADD+PERSP'" : "")} built with the boot set (gxp cache)");
        else { _persp = _addPersp = null; Log.Warn("[k13] FALLBACK: the shader compiler rejected a perspective program of the boot set -- world draws keep the affine mapping (kit #12)"); }
    }
    Span<FColor> K13Enc(int n) { if (_k13Enc.Length < n) _k13Enc = new FColor[Math.Max(n, Math.Max(256, _k13Enc.Length * 2))]; return _k13Enc.AsSpan(0, n); }

    // ---- kit #13 looks (SHEPHERD_K13_SDF=1): TMP's distance field exactly: sprite.vert (ADD: the parameter vec4 in a_add) + owner-local k13_sdf.frag ----
    const byte PSdfX = 6;
    Prog _sdfx; bool _sdfxTried;
    bool SdfProg()
    {
        if (_sdfxTried) return _sdfx != null;
        _sdfxTried = true;
        if (!HasSource("k13_sdf.frag")) { Log.Info($"[k13] TMP distance field: no owner-local k13_sdf.frag ({DerivedRender.Status}) -- text drawn as before"); return false; }
        try { _sdfx = Build("sdf-exact", "sprite.vert", "k13_sdf.frag", "ADD", FragPrecision, null); Log.Info($"[k13] TMP distance field: 'sdf-exact' compiled+linked on first use in {_sdfx.CompileMs:F1} ms (not in the boot set or the gxp cache)"); }
        catch (Exception e) { Log.Warn($"[k13] FALLBACK: the shader compiler rejected 'sdf-exact' ({e.Message.Split('\n')[0]}) -- text drawn as before"); _sdfx = null; }
        return _sdfx != null;
    }
    public bool DrawSdfX(IntPtr texH, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> param, Span<Vertex> before, uint beforeBlend)
    {   // (refused: false, and the caller draws `before` itself)
        if (param.Length < v.Length || !SdfProg()) return false;
        DrawCore(texH, blend, false, v, idx, param, PSdfX);
        return true;
    }

    public void DrawW(IntPtr texH, uint blend, Span<Vertex> v, Span<int> idx, ReadOnlySpan<float> w)
    {
        if (w.Length < v.Length || !PerspProgs()) { K13Looks.PerspFallback++; DrawCore(texH, blend, false, v, idx, default); return; }
        var enc = K13Enc(v.Length); K13Looks.EncW(w.Slice(0, v.Length), enc);
        K13Looks.PerspDraws++;
        DrawCore(texH, blend, false, v, idx, enc, PPersp);
    }
    public void DrawAddW(IntPtr texH, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add, ReadOnlySpan<float> w)
    {
        if (_add == null) { DrawCore(texH, blend, false, v, idx, default); return; }   // (DrawAdd's own fallback)
        var enc = K13Enc(v.Length);
        if (w.Length < v.Length || add.Length < v.Length || !PerspProgs() || _addPersp == null || !K13Looks.EncAddW(add.Slice(0, v.Length), w.Slice(0, v.Length), enc))
        { K13Looks.PerspFallback++; DrawCore(texH, blend, false, v, idx, add); return; }
        K13Looks.PerspDraws++;
        DrawCore(texH, blend, false, v, idx, enc, PAddPersp);
    }
    public void DrawEnc(IntPtr texH, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> enc, bool add)
    {
        if (enc.Length < v.Length) { DrawCore(texH, blend, false, v, idx, default); return; }
        if (!PerspProgs() || (add && _addPersp == null))
        {   // refused: the same draw without w (a lit one gets its added colours back)
            K13Looks.PerspFallback++;
            if (!add) { DrawCore(texH, blend, false, v, idx, default); return; }
            if (_add == null) { DrawCore(texH, blend, false, v, idx, default); return; }
            var dec = new FColor[v.Length]; K13Looks.DecAdd(enc.Slice(0, v.Length), dec);
            DrawCore(texH, blend, false, v, idx, dec); return;
        }
        K13Looks.PerspDraws++;
        DrawCore(texH, blend, false, v, idx, enc, add ? PAddPersp : PPersp);
    }
}
