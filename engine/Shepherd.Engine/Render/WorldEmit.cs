using Shepherd.Engine.Native;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>The world pass's vertex build (menu-speed, stage "multicore render-prep pipeline"; was SceneRenderer's static EmitSprite /
/// EmitMesh / EmitGlow / ClipTriangles / ClipPoly / Lerp and the shared buffers, moved here unchanged, expression for expression). An
/// instance owns its scratch buffers and clip counters, so the vertex build can run on two threads:
/// - <see cref="SceneRenderer.Emit0"/>: the frame's own pass (single-threaded rendering, and every draw SHEPHERD_MT_RENDER=1 does not
///   defer); its <see cref="VBuf"/> / <see cref="IBuf"/> are also the UI pass's shared buffers, as before; its draws go to
///   SceneRenderer.SubmitIdx (texture handle, backend, counters, stream recorder), as before
/// - the MT replay's own instance (Render/MtRender.cs): the same code on inputs captured when the frame was recorded; its draws go to the
///   replay's sink (the texture handle was resolved at record time).
/// The arithmetic, the index filtering, the near/far clipping and the order of everything are the old code's; only where the buffers
/// live and where a finished draw goes are parameters.</summary>
internal sealed class WorldEmitter
{
    public Vertex[] VBuf; public int[] IBuf;
    Vector4[] _cvbuf;
    /// <summary>The clip positions of the current draw (RenderKit.ClipPositions: the owner-local code reads them, after ClipTriangles too).</summary>
    internal Vector4[] CBuf => _cvbuf;
    int[] _ctri;
    /// <summary>The frame's own emitter: the shared buffers' sizes as they always were (4096 vertices, 8192 indices).</summary>
    public WorldEmitter() : this(4096, 8192, 1024) { }
    /// <summary>The MT replay's: smaller to start (they grow like the others; one world draw at a time, ~500 vertices at the menu).</summary>
    public WorldEmitter(int verts, int indices, int clipTris) { VBuf = new Vertex[verts]; IBuf = new int[indices]; _cvbuf = new Vector4[verts]; _ctri = new int[clipTris]; }
    readonly int[] _polyA = new int[12], _polyB = new int[12];
    /// <summary>Triangles clipped / dropped against the near-far planes since the counters were last reset.</summary>
    public int ClipTris, DropTris;
    /// <summary>Null: finished draws go to SceneRenderer.SubmitIdx. Else the MT replay's sink.</summary>
    internal MtRender.Sink Rx;

    /// <summary>The scratch buffers' size (the [mt] lines).</summary>
    public long Bytes => VBuf.Length * 32L + IBuf.Length * 4L + _cvbuf.Length * 16L + _ctri.Length * 4L;
    public Vertex[] Verts(int n) { if (VBuf.Length < n) VBuf = new Vertex[Math.Max(n, VBuf.Length * 2)]; return VBuf; }
    internal Vector4[] ClipBuf(int n) { if (_cvbuf.Length < n) _cvbuf = new Vector4[Math.Max(n, _cvbuf.Length * 2)]; return _cvbuf; }

    void Out(Vertex[] v, int nv, int[] ib, int ni, NTexture tex, uint blend, bool sdf)
    {
        if (_skip0 && !sdf)
        {   // kit #7 spine (Render/SpineSkip.cs): a Spine draw's triangles of colour 0 leave the GPU draw (none left: no draw call; the
            // black-frame watch still sees the draw, as SubmitIdx would show it)
            int nf = SpineSkip.Filter(v, ib, ni);
            if (nf < 3) { if (Rx == null) SceneRenderer.SkippedDraw(v, nv, ni, tex, blend); return; }
            ni = nf;
        }
        if (Rx == null) SceneRenderer.SubmitIdx(v, nv, ib, ni, tex, blend, sdf);
        else Rx.Draw(v, nv, ib, ni, blend, sdf);
    }
    /// <summary>kit #7 spine: set for the duration of an EmitMesh whose draw may leave out its colour-0 triangles (Render/SpineSkip.cs).</summary>
    bool _skip0;
    /// <summary>Emit's index filter (idx.Where(i => i >= 0 && i &lt; nv)), then the draw.</summary>
    public void Submit(Vertex[] v, int nv, int[] idx, NTexture tex, uint blend, bool sdf)
    {
        if (IBuf.Length < idx.Length) IBuf = new int[Math.Max(idx.Length, IBuf.Length * 2)];
        var ib = IBuf; int ni = 0; for (int k = 0; k < idx.Length; k++) { int ix = idx[k]; if (ix >= 0 && ix < nv) ib[ni++] = ix; }
        Out(v, nv, ib, ni, tex, blend, sdf);
    }

    public void EmitSprite(Vector2[] verts, Vector2[] uvs, ushort[] tris, bool fx, bool fy, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, NTexture tex, Color tint, uint blend)
    {
        int nv = verts.Length;
        if (nv == 0 || tris.Length < 3) return;
        var v = Verts(nv); var cv = ClipBuf(nv); bool premul = blend == BLENDMODE_BLEND_PREMULTIPLIED, outside = false;
        for (int i = 0; i < nv; i++)
        {
            var p = verts[i];
            SceneRenderer.Project(in lw, in vp, in pr, gh, fx ? -p.x : p.x, fy ? -p.y : p.y, 0, out var sx, out var sy, ref cv[i], ref outside);
            ref var d = ref v[i];
            float cr = 1f * tint.r, cg = 1f * tint.g, cb = 1f * tint.b, ca = 1f * tint.a;   // Color.white * tint
            if (premul) { cr *= ca; cg *= ca; cb *= ca; }
            d.position.x = sx; d.position.y = sy; d.color.r = cr; d.color.g = cg; d.color.b = cb; d.color.a = ca;
            if (i < uvs.Length) { var u = uvs[i]; d.tex_coord.x = u.x; d.tex_coord.y = 1f - u.y; } else { d.tex_coord.x = 0f; d.tex_coord.y = 1f - 0f; }   // uv (u.x, u.y, 0, 0)
        }
        // the old (int) copy of the ushort triangles, then Emit's filter
        if (IBuf.Length < tris.Length) IBuf = new int[Math.Max(tris.Length, IBuf.Length * 2)];
        var ib = IBuf; int ni = 0; for (int k = 0; k < tris.Length; k++) { int ix = tris[k]; if (ix >= 0 && ix < nv) ib[ni++] = ix; }
        if (outside) { ni = ClipTriangles(ni, ref nv, in pr, gh); v = VBuf; }
        Out(v, nv, IBuf, ni, tex, blend, false);
    }

    public void EmitMesh(Vector3[] pos, Color32[] col, Vector4[] uv, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, NTexture tex, Color tint, uint blend, bool skipZero = false)
    {
        if (pos == null || idx == null || pos.Length == 0 || idx.Length < 3) return;
        _skip0 = skipZero && SpineSkip.On && SpineSkip.NoOpBlend(blend) && !RenderCheck.On;   // kit #7 spine: a Spine skeleton's unlit draw (SceneRenderer.DrawWorld decides)
        try { EmitMeshCore(pos, col, uv, idx, in lw, in vp, in pr, gh, tex, tint, blend); }
        finally { _skip0 = false; }
    }
    void EmitMeshCore(Vector3[] pos, Color32[] col, Vector4[] uv, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, NTexture tex, Color tint, uint blend)
    {
        var v = Verts(pos.Length); var cv = ClipBuf(pos.Length); bool premul = blend == BLENDMODE_BLEND_PREMULTIPLIED, outside = false;
        if (FpsCfg.InlineVertex)
        {   // kit #6 fps: SceneRenderer.Vertex's body written out here, its divisions read from SceneRenderer.B2F (the same values and stores)
            var b2f = SceneRenderer.B2F; float tr = tint.r, tg = tint.g, tb = tint.b, ta = tint.a;
            for (int i = 0; i < pos.Length; i++)
            {
                var p = pos[i];
                SceneRenderer.Project(in lw, in vp, in pr, gh, p.x, p.y, p.z, out var sx, out var sy, ref cv[i], ref outside);
                ref var d = ref v[i];
                float kr, kg, kb, ka;
                if (col != null && i < col.Length) { var k = col[i]; kr = b2f[k.r]; kg = b2f[k.g]; kb = b2f[k.b]; ka = b2f[k.a]; }   // = (float)(int)k.x / 255f
                else { kr = 1f; kg = 1f; kb = 1f; ka = 1f; }
                kr *= tr; kg *= tg; kb *= tb; ka *= ta;
                if (premul) { kr *= ka; kg *= ka; kb *= ka; }
                d.position.x = sx; d.position.y = sy; d.color.r = kr; d.color.g = kg; d.color.b = kb; d.color.a = ka;
                if (uv != null && i < uv.Length) { var u = uv[i]; d.tex_coord.x = u.x; d.tex_coord.y = 1f - u.y; } else { d.tex_coord.x = 0f; d.tex_coord.y = 1f - 0f; }
            }
        }
        else
            for (int i = 0; i < pos.Length; i++)
            {
                var p = pos[i];
                SceneRenderer.Project(in lw, in vp, in pr, gh, p.x, p.y, p.z, out var sx, out var sy, ref cv[i], ref outside);
                SceneRenderer.Vertex(ref v[i], sx, sy, col, uv, i, tint, premul);
            }
        if (!outside) { Submit(v, pos.Length, idx, tex, blend, false); return; }
        if (IBuf.Length < idx.Length) IBuf = new int[Math.Max(idx.Length, IBuf.Length * 2)];
        var ib = IBuf; int nv = pos.Length, ni = 0; for (int k = 0; k < idx.Length; k++) { int ix = idx[k]; if (ix >= 0 && ix < nv) ib[ni++] = ix; }
        ni = ClipTriangles(ni, ref nv, in pr, gh);
        Out(VBuf, nv, IBuf, ni, tex, blend, false);
    }

    /// <summary>Glow: an additive mesh with no texture (the volumetric light spheres) as a soft disc: per vertex, the tint x f^2 where f is
    /// the cosine between the vertex's outward direction and the direction to the camera (bright where the sphere faces the camera,
    /// nothing at its rim), drawn with the white texture and the additive blend. The outward direction is the mesh normal when the
    /// mesh has normals (asset meshes are decoded without them), else the direction from the mesh's box centre -- exact for a sphere.
    /// (pos, nrm, cmn, cmx: the mesh's vertices, normals and culling box; white: Texture2D.whiteTexture, taken by the caller.)</summary>
    public void EmitGlow(Vector3[] pos, Vector3[] nrm, Vector3 cmn, Vector3 cmx, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, Color tint, Vector3 cam, uint blend, NTexture white)
    {
        if (pos == null || idx == null || pos.Length == 0 || idx.Length < 3) return;
        // (the culling box is current: MeshOutside ran for this renderer this pass)
        var ctr = Um.Mid(cmn, cmx);   // (CullMin + CullMax) * 0.5f (hop fix: our own arithmetic)
        bool chk = HopCheck.On; if (chk) HopCheck.V3(HopCheck.Vec, ctr, (cmn + cmx) * 0.5f);
        int nv = pos.Length; var v = Verts(nv); var cv = ClipBuf(nv); bool outside = false;
        for (int i = 0; i < nv; i++)
        {
            var p = pos[i];
            SceneRenderer.Project(in lw, in vp, in pr, gh, p.x, p.y, p.z, out var sx, out var sy, ref cv[i], ref outside);
            float wx = lw.m00 * p.x + lw.m01 * p.y + lw.m02 * p.z + lw.m03, wy = lw.m10 * p.x + lw.m11 * p.y + lw.m12 * p.z + lw.m13, wz = lw.m20 * p.x + lw.m21 * p.y + lw.m22 * p.z + lw.m23;
            var n = nrm != null && i < nrm.Length ? nrm[i] : Um.Sub(p, ctr);   // (p - ctr)
            if (chk && !(nrm != null && i < nrm.Length)) HopCheck.V3(HopCheck.Vec, n, p - ctr);
            float nx = lw.m00 * n.x + lw.m01 * n.y + lw.m02 * n.z, ny = lw.m10 * n.x + lw.m11 * n.y + lw.m12 * n.z, nz = lw.m20 * n.x + lw.m21 * n.y + lw.m22 * n.z;
            float vx = cam.x - wx, vy = cam.y - wy, vz = cam.z - wz;
            float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz) * MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            float f = nl > 1e-12f ? Math.Max(0f, (nx * vx + ny * vy + nz * vz) / nl) : 0f; f *= f;
            ref var d = ref v[i];
            d.position.x = sx; d.position.y = sy; d.color.r = tint.r * f; d.color.g = tint.g * f; d.color.b = tint.b * f; d.color.a = tint.a * f;
            d.tex_coord.x = 0.5f; d.tex_coord.y = 0.5f;
        }
        if (IBuf.Length < idx.Length) IBuf = new int[Math.Max(idx.Length, IBuf.Length * 2)];
        var ib = IBuf; int ni = 0; for (int k = 0; k < idx.Length; k++) { int ix = idx[k]; if (ix >= 0 && ix < nv) ib[ni++] = ix; }
        if (outside) ni = ClipTriangles(ni, ref nv, in pr, gh);
        Out(VBuf, nv, IBuf, ni, white, blend, false);
    }

    /// <summary>IBuf[0..ni) (whole triangles of the current draw, vertices VBuf/_cvbuf[0..nv)) clipped against the near and far planes:
    /// kept triangles keep their indices; crossing ones are replaced by their clipped polygon's fan over new vertices appended at nv;
    /// triangles entirely beyond one plane are dropped. Returns the new index count (written back into IBuf).</summary>
    internal int ClipTriangles(int ni, ref int nv, in RectF pr, float gh)
    {
        ni -= ni % 3;
        if (_ctri.Length < ni * 3) _ctri = new int[Math.Max(ni * 3, _ctri.Length * 2)];
        int w = 0;
        for (int k = 0; k < ni; k += 3)
        {
            int i0 = IBuf[k], i1 = IBuf[k + 1], i2 = IBuf[k + 2];
            Vector4 a = _cvbuf[i0], b = _cvbuf[i1], c = _cvbuf[i2];
            float n0 = a.z + a.w, n1 = b.z + b.w, n2 = c.z + c.w, f0 = a.w - a.z, f1 = b.w - b.z, f2 = c.w - c.z;
            if (n0 >= 0f && n1 >= 0f && n2 >= 0f && f0 >= 0f && f1 >= 0f && f2 >= 0f) { _ctri[w++] = i0; _ctri[w++] = i1; _ctri[w++] = i2; continue; }
            if ((n0 < 0f && n1 < 0f && n2 < 0f) || (f0 < 0f && f1 < 0f && f2 < 0f) || float.IsNaN(n0 + n1 + n2 + f0 + f1 + f2)) { DropTris++; continue; }
            _polyA[0] = i0; _polyA[1] = i1; _polyA[2] = i2;
            int np = ClipPoly(_polyA, 3, _polyB, true, ref nv, in pr, gh);
            np = ClipPoly(_polyB, np, _polyA, false, ref nv, in pr, gh);
            if (np < 3) { DropTris++; continue; }
            ClipTris++;
            if (_ctri.Length < w + (np - 2) * 3) Array.Resize(ref _ctri, Math.Max(_ctri.Length * 2, w + (np - 2) * 3));
            for (int j = 1; j + 1 < np; j++) { _ctri[w++] = _polyA[0]; _ctri[w++] = _polyA[j]; _ctri[w++] = _polyA[j + 1]; }
        }
        if (IBuf.Length < w) IBuf = new int[Math.Max(w, IBuf.Length * 2)];
        Array.Copy(_ctri, IBuf, w);
        return w;
    }
    /// <summary>One Sutherland-Hodgman step of the polygon src[0..n) against the near (z + w >= 0) or far (w - z >= 0) plane.</summary>
    int ClipPoly(int[] src, int n, int[] dst, bool near, ref int nv, in RectF pr, float gh)
    {
        int m = 0;
        for (int j = 0; j < n; j++)
        {
            int p = src[j], q = src[(j + 1) % n];
            Vector4 cp = _cvbuf[p], cq = _cvbuf[q];
            float dp = near ? cp.z + cp.w : cp.w - cp.z, dq = near ? cq.z + cq.w : cq.w - cq.z;
            if (dp >= 0f) dst[m++] = p;
            if ((dp >= 0f) != (dq >= 0f)) dst[m++] = Lerp(p, q, dp / (dp - dq), ref nv, in pr, gh);
        }
        return m;
    }
    /// <summary>A new vertex at nv: p + t (q - p) in clip space, colour and uv likewise, projected like Project.</summary>
    int Lerp(int p, int q, float t, ref int nv, in RectF pr, float gh)
    {
        int i = nv++;
        if (VBuf.Length <= i) Array.Resize(ref VBuf, VBuf.Length * 2);
        if (_cvbuf.Length <= i) Array.Resize(ref _cvbuf, _cvbuf.Length * 2);
        // _cvbuf[p] + (_cvbuf[q] - _cvbuf[p]) * t as our own arithmetic (hop fix; Vector4 -, * float, + per component: a.x + (b.x - a.x) * t)
        var ca = _cvbuf[p]; var cb = _cvbuf[q];
        var c = Um.Lerp4(ca, cb, t);
        if (HopCheck.On) HopCheck.V4(HopCheck.Vec, c, _cvbuf[p] + (_cvbuf[q] - _cvbuf[p]) * t);
        _cvbuf[i] = c;
        ref var a = ref VBuf[p]; ref var b = ref VBuf[q]; ref var d = ref VBuf[i];
        d.color.r = a.color.r + (b.color.r - a.color.r) * t; d.color.g = a.color.g + (b.color.g - a.color.g) * t; d.color.b = a.color.b + (b.color.b - a.color.b) * t; d.color.a = a.color.a + (b.color.a - a.color.a) * t;
        d.tex_coord.x = a.tex_coord.x + (b.tex_coord.x - a.tex_coord.x) * t; d.tex_coord.y = a.tex_coord.y + (b.tex_coord.y - a.tex_coord.y) * t;
        float cw = c.w <= 1e-5f ? 1e-5f : c.w;
        d.position.x = pr.X + (c.x / cw * 0.5f + 0.5f) * pr.W; d.position.y = gh - (pr.Y + (c.y / cw * 0.5f + 0.5f) * pr.H);
        return i;
    }
}
