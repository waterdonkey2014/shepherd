using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

// menu-speed, stage "UI geometry cache" (vita/notes/engine-pass-speed.md section 13): the retained UI pass.
//
// The draw stream is exactly the reference pass's (DrawOverlayCanvasesRef: the same roots in the same order, the same AbsoluteDepth
// numbering, the same early-outs, clip events, texture handles, blends and vertex bytes); what changes is how much is recomputed:
// - the inherited CanvasGroup alpha (RenderIcalls.UiAlpha walked every ancestor of every visited CanvasRenderer, with a HashSet test
//   per ancestor) is carried down the traversal as a stack of the groups' factors, and folded leaf-first exactly as UiAlpha folds
//   them (the same multiplications in the same order, the same ignoreParentGroups stop), once per object that holds a group
// - the root canvas (RootCanvasOf walked every ancestor and every component per drawn renderer and per mask) is the traversal's root;
//   its CanvasToScreen / RootLocalToScreen are taken once per root (both are pure for the duration of the pass)
// - activeInHierarchy (an ancestor walk per node) is the parent's (known active) and the node's activeSelf
// - each drawn CanvasRenderer keeps its projected vertices and filtered indices (UiGeom) with every input they are a function of: the
//   mesh arrays (CanvasRenderer.SetMesh / Clear always store fresh arrays, nothing writes them in place), the tint (colour x
//   inherited alpha x material _Color), the 8 matrix elements the projection reads (canvas-to-screen x LocalToWorld: every transform,
//   canvas scale or screen change shows here), the target height, the premultiply flag and the ramp (with its tangents). A block is
//   reused only while all of them are bit-identical, so a reuse hands the backend the bytes a rebuild would. Clip events, texture
//   handles (uploads by version), FX notes and the RT-zero test still run every frame.
// Nothing here relies on uGUI's dirty notifications (SetVerticesDirty, CanvasUpdateRegistry): whatever Unity would rebuild -- a mesh,
// a colour or alpha tween, a CanvasGroup fade, a moved / scaled / re-parented / re-anchored transform, a material or texture swap,
// masks, rect clipping, sorting, enable / disable, text -- changes one of those inputs or the traversal itself, and is rebuilt.
// SHEPHERD_UICACHE=0 runs the reference pass; SHEPHERD_UICACHE_CHECK=1 runs both every frame and compares (Render/UiCacheCheck.cs).
public static partial class SceneRenderer
{
    /// <summary>A CanvasRenderer's cached draw: v[0..Pos.Length) and the filtered indices I[0..Ni), built from exactly these inputs (while Built).</summary>
    internal sealed class UiGeom
    {
        public Vector3[] Pos; public Color32[] Col; public Vector4[] Uv, Tan; public int[] Idx;
        public float TR, TG, TB, TA, M00, M01, M02, M03, M10, M11, M12, M13, Gh; public bool Premul, Ramp;
        public Vertex[] V; public int[] I; public int Ni, Pass; public bool Built;   // Built: V / I hold the block of the inputs above
        /// <summary>Kit #6 fps (FpsCfg.UiTranslate): the block's partial sums (M00 * x + M01 * y) + M02 * z and (M10 * x + M11 * y) + M12 * z per
        /// vertex, kept when the renderer moved by translation only (SxOk: they belong to the block's build).</summary>
        public float[] Sx, Sy; public bool SxOk;
        /// <summary>Kit #6 fps: V / I were taken from MoveBudget (a renderer that only moved), not from BlockBudget.</summary>
        public bool MovePool;
    }
    /// <summary>The renderers holding a UiGeom (dropped once a renderer has not been drawn for <see cref="GeomGrace"/> passes, or is destroyed).</summary>
    static readonly List<NCanvasRenderer> _uiGeoms = new();
    const int GeomGrace = 30;
    /// <summary>Upper bound of the bytes held in blocks (vertices + indices; the idle Main Menu needs ~30 KB, the Credits screen
    /// alone would take ~1.3 MB): beyond it, renderers are built in the shared buffers each pass, as the reference does. Kit #6 fps:
    /// FpsCfg.UiBlockBudget, 2 MiB by default (kit #5: 512 KiB, which left ~100 steady Credits renderers -- ~22,700 vertices -- to be
    /// rebuilt in full every pass with inputs identical to the pass before).</summary>
    static readonly long BlockBudget = FpsCfg.UiBlockBudget;
    static long _blockBytes;
    static long BlockBytes(UiGeom e) => e.V == null ? 0 : e.V.Length * 32L + e.I.Length * 4L;
    static long SBytes(UiGeom e) => e.Sx == null ? 0 : e.Sx.Length * 8L;
    /// <summary>Kit #6 fps: the bytes held for moved renderers (their blocks taken from MoveBudget, every partial-sum array); 0 with FpsCfg.UiTranslate off.</summary>
    static long _moveBytes;
    /// <summary>Kit #6 fps (FpsCfg.UiTranslate): the room for the blocks of renderers that only moved since the pass before (+ every partial-sum
    /// array), apart from BlockBudget (which stays the steady renderers'): e.g. a list scrolled under a mask, rebuilt in full every pass before.</summary>
    const long MoveBudget = 1536 * 1024;
    static int _uiMoves;
    /// <summary>Last UI pass: drawn CanvasRenderers whose block only got new positions (kit #6 fps, FpsCfg.UiTranslate).</summary>
    public static int LastUiMoves;
    static int _uiPass;
    /// <summary>Last UI pass: drawn CanvasRenderers whose cached block was reused / rebuilt.</summary>
    public static int LastUiHits, LastUiMisses;
    static int _uiHits, _uiMisses;

    // the traversal's root canvas and its two matrices (taken on first use, like the reference pass's first CanvasToScreen call)
    static NComponent _uiRoot; static bool _uiCsValid, _uiRlsValid; static Matrix4x4 _uiCs, _uiRls;
    static Matrix4x4 UiCs() { if (!_uiCsValid) { _uiCs = CanvasToScreen(_uiRoot); _uiCsValid = true; } return _uiCs; }
    static Matrix4x4 UiRls() { if (!_uiRlsValid) { _uiRls = RootLocalToScreen(_uiRoot); _uiRlsValid = true; } return _uiRls; }

    // the CanvasGroup factors on the path from the scene root to the current object: _af[0] is the outermost; each object's factors are
    // pushed so that reading from the top gives that object's groups in component order, then its parent's, ... (UiAlpha's order)
    static float[] _af = new float[32]; static bool[] _afStop = new bool[32]; static int _afN; static float _afVal = 1f;
    static int _holderVer = -1, _holderGen;
    static readonly List<NGameObject> _uiAnc = new();
    static readonly List<NBehaviour> _uiRoots = new();
    static int[] _uiRootKeys = new int[16];

    /// <summary>UiAlpha's holder set (the objects of the live CanvasGroup records) as a generation stamp on the objects.</summary>
    static void RefreshHolders()
    {
        var reg = Registry.CanvasGroups; int v = reg.Version;
        if (v == _holderVer) return;
        _holderGen++;
        foreach (var n in reg.Items()) if (n is NComponent c && c.Go != null) c.Go.UiHolderGen = _holderGen;
        _holderVer = v;
    }
    /// <summary>The object's CanvasGroups as UiAlpha meets them (component order, up to and including the first with
    /// ignoreParentGroups), pushed so they read top-down in that order; then the fold of the whole stack.</summary>
    static void PushGroups(NGameObject go)
    {
        int s = _afN; var comps = go.Components;
        for (int k = 0; k < comps.Count; k++)
            if (comps[k] is var c && c.Managed is CanvasGroup)
            {
                if (_afN == _af.Length) { Array.Resize(ref _af, _af.Length * 2); Array.Resize(ref _afStop, _afStop.Length * 2); }
                bool stop = c.Get("ignoreParentGroups") is true;
                _af[_afN] = c.Get("alpha") is float f ? f : 1f; _afStop[_afN] = stop; _afN++;
                if (stop) break;
            }
        if (_afN == s) return;
        Array.Reverse(_af, s, _afN - s); Array.Reverse(_afStop, s, _afN - s);
        float al = 1f;
        for (int i = _afN - 1; i >= 0; i--) { al *= _af[i]; if (_afStop[i]) break; }
        _afVal = al;
    }

    static void DrawOverlayCanvasesFast()
    {
        _uiPass++; _uiHits = _uiMisses = _uiMoves = 0;
        // the reference's roots: the same filters and the same stable order (OrderBy on sortingOrder), without the LINQ pipeline
        var roots = _uiRoots;
        foreach (var n in Registry.Canvases.Items())
        {
            if (n is not NBehaviour cv) continue;
            if (!(!cv.Destroyed && cv.Managed is Canvas && cv.Enabled && cv.Go != null && cv.Go.Scene != 0 && cv.Go.ActiveInHierarchy && RenderIcalls.RootCanvasOf(cv.Go) == cv)) continue;
            if ((cv.Get("renderMode") is { } rm ? Convert.ToInt32(rm) : 0) == 2) continue;
            roots.Add(cv);
        }
        int nr = roots.Count;
        if (_uiRootKeys.Length < nr) _uiRootKeys = new int[nr * 2];
        for (int i = 0; i < nr; i++) { var cv = roots[i]; _uiRootKeys[i] = cv.Get("sortingOrder") is { } so ? Convert.ToInt32(so) : 0; }
        for (int i = 1; i < nr; i++)
        {
            var r = roots[i]; int k = _uiRootKeys[i], j = i - 1;
            while (j >= 0 && _uiRootKeys[j] > k) { roots[j + 1] = roots[j]; _uiRootKeys[j + 1] = _uiRootKeys[j]; j--; }
            roots[j + 1] = r; _uiRootKeys[j + 1] = k;
        }
        try
        {
            if (nr > 0) RefreshHolders();
            for (int i = 0; i < nr; i++) DrawRootFast(roots[i]);
        }
        finally { roots.Clear(); _uiRoot = null; _afN = 0; _afVal = 1f; }
        UiNoClip();
        LastUiHits = _uiHits; LastUiMisses = _uiMisses; LastUiMoves = _uiMoves;
        SweepGeoms();
    }

    static void DrawRootFast(NBehaviour root)
    {
        _uiRoot = root; _uiCsValid = _uiRlsValid = false; _afN = 0; _afVal = 1f;
        // UiAlpha walks past the root canvas up to the scene root: the groups above it come first
        var anc = _uiAnc;
        for (var t = root.Go.Transform.Parent; t != null; t = t.Parent) anc.Add(t.Go);
        for (int i = anc.Count - 1; i >= 0; i--) if (anc[i].UiHolderGen == _holderGen) PushGroups(anc[i]);
        anc.Clear();
        int depth = 0;
        DrawUiTreeFast(root.Go, ref depth, default, true);
    }

    static void DrawUiTreeFast(NGameObject go, ref int depth, MaskClip clip, bool top)
    {
        if (top ? !go.ActiveInHierarchy : !go.ActiveSelf) return;   // below the root: the parent is active (nothing in the pass changes that)
        int saveN = _afN; float saveVal = _afVal;
        if (go.UiHolderGen == _holderGen) PushGroups(go);
        var comps = go.Components;   // (indexed: nothing in the pass changes a component list)
        for (int k = 0; k < comps.Count; k++)
        {
            if (comps[k] is NCanvasRenderer cr && !cr.Destroyed && !cr.Cull && cr.Idx.Length > 0) { cr.AbsoluteDepth = depth++; UiDepths?.Add((cr, cr.AbsoluteDepth)); DrawCanvasRendererFast(cr, clip); }
        }
        var ch = go.Transform.Children;
        if (ch.Count != 0)
        {
            var inner = MaskOfFast(go, clip);
            for (int i = 0; i < ch.Count; i++) DrawUiTreeFast(ch[i].Go, ref depth, inner, false);
        }
        _afN = saveN; _afVal = saveVal;
    }

    /// <summary>MaskOf with the traversal's root (RootCanvasOf of any object under it).</summary>
    static MaskClip MaskOfFast(NGameObject go, MaskClip outer)
    {
        var comps = go.Components;
        for (int q = 0; q < comps.Count; q++)
        {
            if (comps[q] is not NMono mono || mono.Destroyed || !mono.Enabled || mono.Managed is not UnityEngine.UI.Mask) continue;
            bool hasGraphic = false; for (int j = 0; j < comps.Count; j++) if (comps[j] is NMono gm && !gm.Destroyed && gm.Managed is UnityEngine.UI.Graphic) { hasGraphic = true; break; }
            if (!hasGraphic || !go.Transform.IsRect) return outer;
            var m = M.Mul(UiCs(), go.Transform.LocalToWorld);
            var r = go.Transform.RectF;   // hop fix: the rect's fields (xMin = x, xMax = width + x, yMin = y, yMax = height + y), not Rect's getters
            if (HopCheck.On) { var u = go.Transform.Rect; HopCheck.F(HopCheck.MaskRect, r.X, u.xMin); HopCheck.F(HopCheck.MaskRect, r.XMax, u.xMax); HopCheck.F(HopCheck.MaskRect, r.Y, u.yMin); HopCheck.F(HopCheck.MaskRect, r.YMax, u.yMax); }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                float lx = (k & 1) == 0 ? r.X : r.XMax, ly = (k & 2) == 0 ? r.Y : r.YMax;
                float wx = m.m00 * lx + m.m01 * ly + m.m03, wy = m.m10 * lx + m.m11 * ly + m.m13;
                x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); y0 = Math.Min(y0, wy); y1 = Math.Max(y1, wy);
            }
            return outer.And(x0, y0, x1, y1);
        }
        return outer;
    }

    static unsafe bool Same(float a, float b) => *(uint*)&a == *(uint*)&b;

    /// <summary>The clip rect's corners (xMin, yMin, 0) and (xMax, yMax, 0) (hop fix: its fields, xMax = width + x, yMax = height + y; were
    /// new Vector3(cr.ClipRect.xMin, cr.ClipRect.yMin, 0) / (.xMax, .yMax, 0) through Rect's getters and Vector3's constructor).</summary>
    static void ClipCorners(NCanvasRenderer cr, out Vector3 c0, out Vector3 c1)
    {
        var f = Um.Of(cr.ClipRect);
        c0 = Um.V3(f.X, f.Y, 0f); c1 = Um.V3(f.XMax, f.YMax, 0f);
        if (HopCheck.On) { var u = cr.ClipRect; HopCheck.V3(HopCheck.ClipRect, c0, new Vector3(u.xMin, u.yMin, 0)); HopCheck.V3(HopCheck.ClipRect, c1, new Vector3(u.xMax, u.yMax, 0)); }
    }

    /// <summary>The material's reads for this pass (mat.P cannot change during the pass: no game code runs in it).</summary>
    static void UiMatRead(NMaterial mat)
    {
        mat.UiPass = _uiPass; mat.UiTexRead = false; mat.UiTex = null; mat.UiSh = null;
        mat.UiCm0 = mat.P.TryGetValue(ColorMaskId, out var cm) && cm is float cmf && cmf == 0f;
        mat.UiHasColor = mat.P.TryGetValue(ShaderIds.Color, out var mc) && mc is Color; mat.UiColor = mat.UiHasColor ? (Color)mc : default;
    }
    static ShaderInfo _uiDefSh;

    /// <summary>DrawCanvasRenderer with the carried alpha / root and the cached vertex block (the same early-outs, side effects and
    /// backend calls in the same order).</summary>
    static void DrawCanvasRendererFast(NCanvasRenderer cr, MaskClip mask)
    {
        if (UiCacheCfg.Check) UiCacheCheck.Visit(cr, _afVal, _uiRoot);
        var mat = cr.Materials.Length > 0 ? cr.Materials[0] : null;
        if (mat != null) { if (mat.UiPass != _uiPass) UiMatRead(mat); if (mat.UiCm0) return; }   // _ColorMask == 0
        if (mask.Empty) return;
        var tex = cr.Texture;
        if (tex == null && mat != null)
        {   // the reference's mat.P[_MainTex] -> RenderIcalls.Tex, at the first renderer of the pass that needs it (its first touch, if any, happens there)
            if (!mat.UiTexRead) { mat.UiTex = mat.P.TryGetValue(ShaderIds.MainTex, out var mt) ? RenderIcalls.Tex(mt) : null; mat.UiTexRead = true; }
            tex = mat.UiTex;
        }
        float alpha = _afVal;   // = RenderIcalls.UiAlpha(cr.Go)
        var tint = cr.Color; tint.a *= alpha;
        if (mat != null && mat.UiHasColor) { var mcol = mat.UiColor; tint.r *= mcol.r; tint.g *= mcol.g; tint.b *= mcol.b; tint.a *= mcol.a; }   // tint * _Color, component-wise
        if (tint.a <= 0.001f) return;
        var m = M.Mul(UiCs(), cr.Go.Transform.LocalToWorld);   // root = _uiRoot = RenderIcalls.RootCanvasOf(cr.Go), never null here
        var sh = mat == null ? _uiDefSh ??= ShaderOf("UI/Default") : (ShaderInfo)(mat.UiSh ??= ShaderOf(mat.ShaderName));
        uint blend = sh.BlendUi;
        var fx = ShaderPolicy.Ui(mat, out var fb);
        if (tex != null && tex.IsRenderTexture && tex.RtZero && (fx == ShaderPolicy.Kind.Grab || blend is BLENDMODE_BLEND or BLENDMODE_BLEND_PREMULTIPLIED or BLENDMODE_ADD or BLENDMODE_ADD_PREMULTIPLIED))
        { tex.RtSampled = Runtime.Time.FrameCount; _fxRtZero++; return; }
        if (fx == ShaderPolicy.Kind.Grab) { FxDraw(mat, fx, fb, cr.Go); blend = fb; tex ??= WhiteTex(); }
        if (cr.RectClipping && !mask.On)
        {
            var rm = UiRls();
            ClipCorners(cr, out var c0, out var c1);
            var p0 = M.Point(rm, c0); var p1 = M.Point(rm, c1);
            UiClipTo((int)Math.Min(p0.x, p1.x), (int)(G.H - Math.Max(p0.y, p1.y)), (int)Math.Abs(p1.x - p0.x), (int)Math.Abs(p1.y - p0.y));
        }
        else if (mask.On)
        {
            var b = mask;
            if (cr.RectClipping)
            {
                var rm = UiRls();
                ClipCorners(cr, out var c0, out var c1);
                var p0 = M.Point(rm, c0); var p1 = M.Point(rm, c1);
                b = b.And(Math.Min(p0.x, p1.x), Math.Min(p0.y, p1.y), Math.Max(p0.x, p1.x), Math.Max(p0.y, p1.y));
                if (b.Empty) return;
            }
            int cx = (int)MathF.Floor(b.X0), cy = (int)MathF.Floor(G.H - b.Y1), cw = (int)MathF.Ceiling(b.X1) - cx, ch = (int)MathF.Ceiling(G.H - b.Y0) - cy;
            UiClipTo(cx, cy, cw, ch);
        }
        else UiNoClip();
        var pos = cr.Pos; var idx = cr.Idx;
        if (pos == null || idx == null || pos.Length == 0 || idx.Length < 3) return;
        var col = cr.Col; var uv = cr.Uv0; float gh = G.H;
        bool premul = blend == BLENDMODE_BLEND_PREMULTIPLIED;
        var ramp = sh.Ramp ? RampOf(mat) : null;
        bool rp = ramp != null; var tan = cr.Tan; var looks = rp ? DerivedRender.Looks : null;   // (fid1-fixup round 2: the ramp rule is owner-local, a pure function of the vertex, uv and tangent: cacheable)
        var e = cr.UiGeom;
        bool same = e != null && ReferenceEquals(e.Pos, pos) && ReferenceEquals(e.Idx, idx) && ReferenceEquals(e.Col, col) && ReferenceEquals(e.Uv, uv)
            && e.Ramp == rp && (!rp || ReferenceEquals(e.Tan, tan)) && e.Premul == premul && Same(e.Gh, gh)
            && Same(e.TR, tint.r) && Same(e.TG, tint.g) && Same(e.TB, tint.b) && Same(e.TA, tint.a)
            && Same(e.M00, m.m00) && Same(e.M01, m.m01) && Same(e.M02, m.m02) && Same(e.M03, m.m03)
            && Same(e.M10, m.m10) && Same(e.M11, m.m11) && Same(e.M12, m.m12) && Same(e.M13, m.m13);
        if (same && e.Built) { _uiHits++; e.Pass = _uiPass; SubmitIdx(e.V, pos.Length, e.I, e.Ni, ramp ?? tex, blend, sh.Sdf); return; }
        // kit #6 fps (FpsCfg.UiTranslate, needs FpsCfg.InlineVertex): every input but the translation (m03 / m13) the same as the block's build
        // -- a list scrolled under a mask, a sliding panel. With the block's partial sums the new positions are x = Sx + m03, y = gh - (Sy + m13): the build loop's own
        // expressions, evaluated in the same order on the same float32 values, so every byte is a rebuild's; colours, uvs and indices are the block's
        bool moved = !same && FpsCfg.UiTranslate && !rp && e != null && ReferenceEquals(e.Pos, pos) && ReferenceEquals(e.Idx, idx) && ReferenceEquals(e.Col, col) && ReferenceEquals(e.Uv, uv)
            && !e.Ramp && e.Premul == premul && Same(e.Gh, gh)
            && Same(e.TR, tint.r) && Same(e.TG, tint.g) && Same(e.TB, tint.b) && Same(e.TA, tint.a)
            && Same(e.M00, m.m00) && Same(e.M01, m.m01) && Same(e.M02, m.m02) && Same(e.M10, m.m10) && Same(e.M11, m.m11) && Same(e.M12, m.m12);
        if (moved && e.Built && e.SxOk)
        {
            _uiMoves++; e.Pass = _uiPass;
            var bv = e.V; var sxa = e.Sx; var sya = e.Sy; float t03 = m.m03, t13 = m.m13; int n0 = pos.Length;
            for (int i = 0; i < n0; i++) { ref var d = ref bv[i]; d.position.x = sxa[i] + t03; d.position.y = gh - (sya[i] + t13); }
            e.M03 = t03; e.M13 = t13;
            SubmitIdx(bv, n0, e.I, e.Ni, tex, blend, sh.Sdf);   // (ramp ?? tex: no ramp here)
            return;
        }
        _uiMisses++;
        if (e == null) { cr.UiGeom = e = new UiGeom(); _uiGeoms.Add(cr); }
        e.Pass = _uiPass;
        int nv = pos.Length;
        // where to build: the renderer's own block when it is big enough already, or when its inputs are the same as last pass (a
        // second identical pass: it is steady) and the blocks stay within BlockBudget; else the shared buffers, like the reference (a
        // renderer whose mesh changes every pass, e.g. TMP during a layout cascade, never takes a block). Kit #6 fps: a renderer that only
        // moved since last pass (moved) takes a block with its partial sums within BlockBudget + MoveBudget
        Vertex[] v; int[] ib;
        bool fits = e.V != null && e.V.Length >= nv && e.I.Length >= idx.Length;
        bool wantS = moved && FpsCfg.InlineVertex;
        bool inBlock = !fits && same && (e.MovePool ? _blockBytes : _blockBytes - BlockBytes(e)) + nv * 32L + idx.Length * 4L <= BlockBudget;
        bool inMove = !fits && !inBlock && wantS && (e.MovePool ? _moveBytes - BlockBytes(e) : _moveBytes) - SBytes(e) + nv * 40L + idx.Length * 4L <= MoveBudget;
        if (fits || inBlock || inMove)
        {
            if (!fits)
            {
                if (e.MovePool) _moveBytes -= BlockBytes(e); else _blockBytes -= BlockBytes(e);
                e.V = new Vertex[nv]; e.I = new int[idx.Length]; e.MovePool = inMove;
                if (inMove) _moveBytes += BlockBytes(e); else _blockBytes += BlockBytes(e);
            }
            v = e.V; ib = e.I; e.Built = true;
        }
        else
        {
            v = Verts(nv); if (Emit0.IBuf.Length < idx.Length) Emit0.IBuf = new int[Math.Max(idx.Length, Emit0.IBuf.Length * 2)];
            ib = Emit0.IBuf; e.Built = false;
        }
        // the partial sums for the next pass (a moved renderer's own block; the room as above)
        float[] sxs = null, sys = null;
        if (wantS && e.Built)
        {
            if (e.Sx != null && e.Sx.Length >= nv) { sxs = e.Sx; sys = e.Sy; }
            else if (_moveBytes - SBytes(e) + nv * 8L <= MoveBudget)
            { _moveBytes -= SBytes(e); e.Sx = sxs = new float[nv]; e.Sy = sys = new float[nv]; _moveBytes += nv * 8L; }
        }
        e.SxOk = sxs != null;
        // the reference's vertex loop, expression for expression (kit #6 fps, FpsCfg.InlineVertex: Vertex's body written out here, its
        // Color32 -> Color divisions read from B2F -- the same values and stores, no call per vertex)
        if (FpsCfg.InlineVertex)
        {
            var b2f = B2F; float tr = tint.r, tg = tint.g, tb = tint.b, ta = tint.a;
            for (int i = 0; i < nv; i++)
            {
                var p = pos[i];
                float wx = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03, wy = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13;
                if (sxs != null) { sxs[i] = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z; sys[i] = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z; }   // (wx / wy before the translation)
                ref var d = ref v[i];
                float kr, kg, kb, ka;
                if (col != null && i < col.Length) { var k = col[i]; kr = b2f[k.r]; kg = b2f[k.g]; kb = b2f[k.b]; ka = b2f[k.a]; }   // = (float)(int)k.x / 255f
                else { kr = 1f; kg = 1f; kb = 1f; ka = 1f; }
                kr *= tr; kg *= tg; kb *= tb; ka *= ta;
                if (premul) { kr *= ka; kg *= ka; kb *= ka; }
                d.position.x = wx; d.position.y = gh - wy; d.color.r = kr; d.color.g = kg; d.color.b = kb; d.color.a = ka;
                if (uv != null && i < uv.Length) { var u = uv[i]; d.tex_coord.x = u.x; d.tex_coord.y = 1f - u.y; } else { d.tex_coord.x = 0f; d.tex_coord.y = 1f - 0f; }
                if (rp) { if (looks != null) looks.RampVertex(ref v[i], uv, tan, i); else RampShown(ref v[i]); }
            }
        }
        else
            for (int i = 0; i < nv; i++)
            {
                var p = pos[i];
                float wx = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03, wy = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13;
                Vertex(ref v[i], wx, gh - wy, col, uv, i, tint, premul);
                if (rp) { if (looks != null) looks.RampVertex(ref v[i], uv, tan, i); else RampShown(ref v[i]); }
            }
        // Submit's index filter
        int ni = 0;
        for (int k = 0; k < idx.Length; k++) { int ix = idx[k]; if (ix >= 0 && ix < nv) ib[ni++] = ix; }
        e.Ni = ni;
        e.Pos = pos; e.Idx = idx; e.Col = col; e.Uv = uv; e.Tan = rp ? tan : null; e.Ramp = rp; e.Premul = premul; e.Gh = gh;
        e.TR = tint.r; e.TG = tint.g; e.TB = tint.b; e.TA = tint.a;
        e.M00 = m.m00; e.M01 = m.m01; e.M02 = m.m02; e.M03 = m.m03; e.M10 = m.m10; e.M11 = m.m11; e.M12 = m.m12; e.M13 = m.m13;
        SubmitIdx(v, nv, ib, ni, ramp ?? tex, blend, sh.Sdf);
    }

    /// <summary>Drops the blocks of renderers not drawn in the last <see cref="GeomGrace"/> passes (or destroyed), so the cache holds about
    /// what is on screen.</summary>
    static void SweepGeoms()
    {
        int w = 0, cut = _uiPass - GeomGrace;
        for (int i = 0; i < _uiGeoms.Count; i++)
        {
            var cr = _uiGeoms[i]; var e = cr.UiGeom;
            if (e == null || cr.Destroyed || e.Pass < cut) { if (e != null) { if (e.MovePool) _moveBytes -= BlockBytes(e); else _blockBytes -= BlockBytes(e); _moveBytes -= SBytes(e); } cr.UiGeom = null; continue; }
            _uiGeoms[w++] = cr;
        }
        if (w < _uiGeoms.Count) _uiGeoms.RemoveRange(w, _uiGeoms.Count - w);
    }

    // ---------- the UI pass's backend calls (both passes); SHEPHERD_UICACHE_CHECK=1 records them, and silences the reference pass ----------
    /// <summary>UiCacheCheck's recorder of the pass being run (null otherwise) and whether that pass only records.</summary>
    internal static RenderCheck.Rec UiCap; internal static bool UiCapOnly;
    /// <summary>UiCacheCheck: the AbsoluteDepth numbering of the pass being run.</summary>
    internal static List<(NCanvasRenderer cr, int depth)> UiDepths;
    static void UiClipTo(int x, int y, int w, int h)
    {
        if (UiCap != null) { UiCap.Clip(x, y, w, h); if (UiCapOnly) return; }
        G.Clip(x, y, w, h); RenderCheck.Live?.Clip(x, y, w, h);
    }
    static void UiNoClip()
    {
        if (UiCap != null) { UiCap.NoClip(); if (UiCapOnly) return; }
        G.NoClip(); RenderCheck.Live?.NoClip();
    }
    /// <summary>UiCacheCheck: the counters a reference pass run on the side must leave as the real pass left them.</summary>
    internal static (int, int, int, int, int, int, int) UiCounters() => (_fxSkips, _fxBlends, _fxGlows, _fxGrabs, _fxRtZero, LastDraws, LastTris);
    internal static void SetUiCounters((int, int, int, int, int, int, int) c) => (_fxSkips, _fxBlends, _fxGlows, _fxGrabs, _fxRtZero, LastDraws, LastTris) = c;
    internal static void RunFastPass() => DrawOverlayCanvasesFast();
}
