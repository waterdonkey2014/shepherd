using Shepherd.Engine.Native;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

// Kit #11 track gpu1 (vita/notes/kit11-gpu1.md): GPU-SIDE PROJECTION, SHEPHERD_K11_GPUPROJ=1 (default off: owner decision -- the picture is
// not identical to the CPU path's to the pixel, see the note's image diffs). A normal, unlit, textured world draw on this thread is built here
// in WORLD space -- the local-to-world step of SceneRenderer.Project, the colours, uvs, index filter and Spine colour-0 filter exactly as
// WorldEmitter builds them -- and handed to the backend's projection program (IK11Proj), which repeats the rest of Project on the GPU with the
// camera pass's view-projection, pixel rect and target height. The CPU path (WorldEmitter, unchanged) still builds:
// - any draw whose renderer's box is not inside the near and far planes by a safe margin (the CPU clips those triangles in clip space; the
//   test is in double with a margin far above float rounding, so every vertex sent here is one the CPU would not have clipped);
// - lit draws (the owner-local model shades the CPU's screen-space vertices), untextured draws and solid-colour textures (the black-frame
//   watch reads cover candidates' screen corners), any draw after a cover this frame, SHEPHERD_MT_RENDER=1 (captured for the render thread),
//   the check modes, and backends without the program (null, SDL, compare mode).
// RETAINED: with SHEPHERD_K11_REUSE=1 as well, the world-space vertices of a draw whose renderer did not change (the change signature without
// the camera: the camera is on the GPU now) are kept and sent again however the camera moves. Copied only once the signature held for a
// frame (a moving or animated draw costs no copy per frame); the passes age out with their cameras (SceneRenderer.K11Reuse.cs K11Age).
public static partial class SceneRenderer
{
    static bool _k11ProjOn; static K11ProjPass _k11pp; static K11Pass _k11ProjCur;
    static Dictionary<NBehaviour, K11Pass> _k11ProjPasses;   // (both made on first use: nothing exists with the switches off)
    static float[] _k11z;

    /// <summary>DrawWorld's start: whether this camera pass may use the projection program, and its pass record.</summary>
    static void K11ProjBegin(NBehaviour cam, in Matrix4x4 vp, in RectF pr, float gh)
    {
        _k11ProjCur = null;
        _k11ProjOn = !_mtDefer && !RenderCheck.On && UiCap == null && G is IK11Proj kp && kp.ProjAvailable;
        if (!_k11ProjOn) return;
        _k11pp = new K11ProjPass { Vp = vp, X = pr.X, Y = pr.Y, W = pr.W, H = pr.H, Gh = gh };
        if (!K11World.Reuse) return;
        var d = _k11ProjPasses ??= new Dictionary<NBehaviour, K11Pass>();
        if (!d.TryGetValue(cam, out var ps)) { if (d.Count >= 4) d.Clear(); d[cam] = ps = new K11Pass(); }
        ps.N = 0; ps.Frame = Runtime.Time.FrameCount; _k11ProjCur = ps;
    }
    static void K11ProjEnd()
    {
        var ps = _k11ProjCur; _k11ProjCur = null; if (ps == null) return;
        for (int k = ps.N; k < ps.E.Length && ps.E[k] != null && ps.E[k].R != null; k++) ps.E[k].Release();
    }
    static float[] K11Z(int n) { if (_k11z == null || _k11z.Length < n) _k11z = new float[Math.Max(n, _k11z == null ? 1024 : _k11z.Length * 2)]; return _k11z; }

    /// <summary>The local box [mn, mx] under lw lies inside the camera's near (z + w >= 0) and far (w - z >= 0) planes with a margin of 1e-4 of
    /// the terms' magnitude (double; NaN / infinity: false), so no vertex of the draw is one the CPU path would clip.</summary>
    static bool K11InsideNearFar(in Matrix4x4 lw, in Matrix4x4 vp, float mnx, float mny, float mnz, float mxx, float mxy, float mxz)
    {
        double cx = ((double)mnx + mxx) * 0.5, cy = ((double)mny + mxy) * 0.5, cz = ((double)mnz + mxz) * 0.5, ex = ((double)mxx - mnx) * 0.5, ey = ((double)mxy - mny) * 0.5, ez = ((double)mxz - mnz) * 0.5;
        double wx = lw.m00 * cx + lw.m01 * cy + lw.m02 * cz + lw.m03, wy = lw.m10 * cx + lw.m11 * cy + lw.m12 * cz + lw.m13, wz = lw.m20 * cx + lw.m21 * cy + lw.m22 * cz + lw.m23;
        double hx = Math.Abs((double)lw.m00) * ex + Math.Abs((double)lw.m01) * ey + Math.Abs((double)lw.m02) * ez;
        double hy = Math.Abs((double)lw.m10) * ex + Math.Abs((double)lw.m11) * ey + Math.Abs((double)lw.m12) * ez;
        double hz = Math.Abs((double)lw.m20) * ex + Math.Abs((double)lw.m21) * ey + Math.Abs((double)lw.m22) * ez;
        for (int k = 0; k < 2; k++)
        {
            double s = k == 0 ? 1.0 : -1.0;
            double a = vp.m30 + s * vp.m20, b = vp.m31 + s * vp.m21, c = vp.m32 + s * vp.m22, d = vp.m33 + s * vp.m23;
            double ext = Math.Abs(a) * hx + Math.Abs(b) * hy + Math.Abs(c) * hz;
            double val = a * wx + b * wy + c * wz + d - ext, mag = Math.Abs(a * wx) + Math.Abs(b * wy) + Math.Abs(c * wz) + Math.Abs(d) + ext;
            if (!(val > 1e-4 * mag + 1e-6)) return false;
        }
        return true;
    }
    /// <summary>A texture the projection path may sample: present and not a solid-colour texture (those draws stay where the black-frame watch
    /// reads their screen corners).</summary>
    static bool K11ProjTex(NTexture t) => t != null && !t.Solid;
    static bool K11ProjCovered() => _curTarget == IntPtr.Zero && BlackFrame.CoveredSoFar;

    /// <summary>The finished world-space draw to the backend (false: not taken, the caller builds it on the CPU).</summary>
    static bool K11ProjSubmit(Vertex[] v, float[] z, int nv, int[] ib, int ni, NTexture tex, uint blend)
    {
        if (ni - ni % 3 < 3) return true;   // (SubmitIdx's early-out: nothing drawn)
        ni -= ni % 3;
        var h = Handle(tex, false);
        var vs = new Span<Vertex>(v, 0, nv); var zs = new Span<float>(z, 0, nv); var ix = new Span<int>(ib, 0, ni);
        if (!((IK11Proj)G).DrawProj(h, blend, vs, zs, ix, in _k11pp)) return false;
        LastDraws++; LastTris += ni / 3; K11World._projDraws++; K11World._projVerts += nv;
        RenderCheck.Live?.Proj(h, blend, vs, zs, ix, in _k11pp);
        return true;
    }

    /// <summary>A normal unlit sprite draw on the GPU path (EmitSprite's arithmetic up to the world position). False: the CPU builds it.</summary>
    static bool K11ProjSprite(ref WItem it, NSprite ns, in Matrix4x4 vp)
    {
        var tex = ns.Texture;
        if (!K11ProjTex(tex) || K11ProjCovered()) return false;
        Vector2[] verts = ns.Vertices, uvs = ns.Uv; ushort[] tris = ns.Triangles;
        int nv = verts.Length;
        if (nv == 0 || tris.Length < 3) return true;   // EmitSprite draws nothing
        if (nv > 65536 || !ReferenceEquals(ns.CullOf, verts)) { K11World._projCpu++; return false; }
        float x0 = it.Fx ? -ns.CullMax.x : ns.CullMin.x, x1 = it.Fx ? -ns.CullMin.x : ns.CullMax.x, y0 = it.Fy ? -ns.CullMax.y : ns.CullMin.y, y1 = it.Fy ? -ns.CullMin.y : ns.CullMax.y;
        if (!K11InsideNearFar(in it.Lw, in vp, x0, y0, 0f, x1, y1, 0f)) { K11World._projCpu++; return false; }
        K11Entry e = null;
        if (_k11ProjCur != null)
        {   // retained: the same renderer, arrays, flips, matrix, tint and blend as the kept draw -> its world-space vertices again
            e = _k11ProjCur.Next();
            bool src = ReferenceEquals(e.R, it.R) && e.Sub == -1 && ReferenceEquals(e.Src, ns) && ReferenceEquals(e.A0, verts) && ReferenceEquals(e.A1, uvs) && ReferenceEquals(e.A2, tris) && !ns.GeomShared;
            bool same = src && e.Fx == it.Fx && e.Fy == it.Fy && e.Blend == it.Blend && ReferenceEquals(e.Tex, tex) && SameM12(in e.Lw, in it.Lw) && SameC(in e.Tint, in it.Col);
            if (same && e.Has)
            {
                if (!K11ProjSubmit(e.V, e.Z, e.Nv, e.I, e.Ni, tex, it.Blend)) return false;
                K11World._projRetained++; return true;
            }
            e.R = it.R; e.Sub = -1; e.Src = ns; e.A0 = verts; e.A1 = uvs; e.A2 = tris; e.A3 = null; e.Fx = it.Fx; e.Fy = it.Fy; e.Blend = it.Blend; e.Tex = tex; e.Lw = it.Lw; e.Tint = it.Col;
            e.Keeping = same;   // (copied once the signature held for a frame: a moving or animated draw keeps no copy)
            if (!same) e.LetGo();
        }
        var v = Emit0.Verts(nv); var z = K11Z(nv); var lw = it.Lw; var tint = it.Col; bool fx = it.Fx, fy = it.Fy, premul = it.Blend == BLENDMODE_BLEND_PREMULTIPLIED;
        for (int i = 0; i < nv; i++)
        {
            var p = verts[i]; float px = fx ? -p.x : p.x, py = fy ? -p.y : p.y, pz = 0;
            ref var d = ref v[i];
            d.position.x = lw.m00 * px + lw.m01 * py + lw.m02 * pz + lw.m03; d.position.y = lw.m10 * px + lw.m11 * py + lw.m12 * pz + lw.m13; z[i] = lw.m20 * px + lw.m21 * py + lw.m22 * pz + lw.m23;   // (Project's first line)
            float cr = 1f * tint.r, cg = 1f * tint.g, cb = 1f * tint.b, ca = 1f * tint.a;   // Color.white * tint (EmitSprite)
            if (premul) { cr *= ca; cg *= ca; cb *= ca; }
            d.color.r = cr; d.color.g = cg; d.color.b = cb; d.color.a = ca;
            if (i < uvs.Length) { var u = uvs[i]; d.tex_coord.x = u.x; d.tex_coord.y = 1f - u.y; } else { d.tex_coord.x = 0f; d.tex_coord.y = 1f - 0f; }
        }
        var ib = Emit0.IBuf; if (ib.Length < tris.Length) ib = Emit0.IBuf = new int[Math.Max(tris.Length, ib.Length * 2)];
        int ni = 0; for (int k = 0; k < tris.Length; k++) { int ix = tris[k]; if (ix >= 0 && ix < nv) ib[ni++] = ix; }
        if (e != null && e.Keeping) K11Keep(e, v, z, nv, ib, ni);
        return K11ProjSubmit(v, z, nv, ib, ni, tex, it.Blend);
    }

    /// <summary>A normal unlit mesh submesh draw on the GPU path (EmitMesh's arithmetic up to the world position, its Spine colour-0 filter).</summary>
    static bool K11ProjMesh(ref WItem it, int s, int[] sub, NTexture tex, Color tint, uint bs, bool skip0, in Matrix4x4 vp)
    {
        if (!K11ProjTex(tex) || K11ProjCovered()) return false;
        var m = it.MeshRec; var pos = it.Pos; var col = it.MCol; var uv = it.Uv;
        if (pos == null || sub == null || pos.Length == 0 || sub.Length < 3) return true;   // EmitMesh draws nothing
        if (m == null || m.CullVer != m.Version || pos.Length > 65536) { K11World._projCpu++; return false; }
        if (!K11InsideNearFar(in it.Lw, in vp, m.CullMin.x, m.CullMin.y, m.CullMin.z, m.CullMax.x, m.CullMax.y, m.CullMax.z)) { K11World._projCpu++; return false; }
        K11Entry e = null;
        if (_k11ProjCur != null)
        {
            e = _k11ProjCur.Next();
            bool own = ReferenceEquals(sub, it.Subs[s]) && s < m.Sub.Count && ReferenceEquals(sub, m.Sub[s]) && ReferenceEquals(pos, m.Pos) && ReferenceEquals(col, m.Col) && ReferenceEquals(uv, m.Uv0);
            bool src = own && ReferenceEquals(e.R, it.R) && e.Sub == s && ReferenceEquals(e.Src, m) && e.Ver == m.Version && ReferenceEquals(e.A0, pos) && ReferenceEquals(e.A1, col) && ReferenceEquals(e.A2, uv) && ReferenceEquals(e.A3, sub);
            bool same = src && e.Skip0 == skip0 && e.Blend == bs && ReferenceEquals(e.Tex, tex) && SameM12(in e.Lw, in it.Lw) && SameC(in e.Tint, in tint);
            if (same && e.Has)
            {
                if (e.State == 2) { K11World._projRetained++; return true; }   // (every triangle left out by the Spine filter: nothing drawn)
                if (!K11ProjSubmit(e.V, e.Z, e.Nv, e.I, e.Ni, tex, bs)) return false;
                K11World._projRetained++; return true;
            }
            e.R = it.R; e.Sub = s; e.Src = m; e.A0 = pos; e.A1 = col; e.A2 = uv; e.A3 = sub; e.Ver = m.Version; e.Skip0 = skip0; e.Blend = bs; e.Tex = tex; e.Lw = it.Lw; e.Tint = tint;
            e.Keeping = same; e.State = 0;
            if (!same) e.LetGo();
        }
        int nv = pos.Length; var v = Emit0.Verts(nv); var z = K11Z(nv); var lw = it.Lw; bool premul = bs == BLENDMODE_BLEND_PREMULTIPLIED;
        float tr = tint.r, tg = tint.g, tb = tint.b, ta = tint.a; var b2f = B2F; bool inl = FpsCfg.InlineVertex;
        for (int i = 0; i < nv; i++)
        {
            var p = pos[i];
            ref var d = ref v[i];
            float wx = lw.m00 * p.x + lw.m01 * p.y + lw.m02 * p.z + lw.m03, wy = lw.m10 * p.x + lw.m11 * p.y + lw.m12 * p.z + lw.m13, wz = lw.m20 * p.x + lw.m21 * p.y + lw.m22 * p.z + lw.m23;
            if (inl)
            {   // EmitMeshCore's InlineVertex body (the same values as SceneRenderer.Vertex)
                float kr, kg, kb, ka;
                if (col != null && i < col.Length) { var k = col[i]; kr = b2f[k.r]; kg = b2f[k.g]; kb = b2f[k.b]; ka = b2f[k.a]; } else { kr = 1f; kg = 1f; kb = 1f; ka = 1f; }
                kr *= tr; kg *= tg; kb *= tb; ka *= ta;
                if (premul) { kr *= ka; kg *= ka; kb *= ka; }
                d.color.r = kr; d.color.g = kg; d.color.b = kb; d.color.a = ka;
                if (uv != null && i < uv.Length) { var u = uv[i]; d.tex_coord.x = u.x; d.tex_coord.y = 1f - u.y; } else { d.tex_coord.x = 0f; d.tex_coord.y = 1f - 0f; }
            }
            else Vertex(ref d, 0f, 0f, col, uv, i, tint, premul);
            d.position.x = wx; d.position.y = wy; z[i] = wz;
        }
        var ib = Emit0.IBuf; if (ib.Length < sub.Length) ib = Emit0.IBuf = new int[Math.Max(sub.Length, ib.Length * 2)];
        int ni = 0; for (int k = 0; k < sub.Length; k++) { int ix = sub[k]; if (ix >= 0 && ix < nv) ib[ni++] = ix; }
        if (skip0 && SpineSkip.On && SpineSkip.NoOpBlend(bs))
        {   // kit #7 spine: the colour-0 triangles leave the draw (WorldEmitter.Out's filter on the same colours)
            int nf = SpineSkip.Filter(v, ib, ni);
            if (nf < 3) { if (e != null && e.Keeping) { e.State = 2; e.Has = true; } return true; }
            ni = nf;
        }
        if (e != null && e.Keeping) { K11Keep(e, v, z, nv, ib, ni); e.State = 1; }
        return K11ProjSubmit(v, z, nv, ib, ni, tex, bs);
    }
    static void K11Keep(K11Entry e, Vertex[] v, float[] z, int nv, int[] ib, int ni)
    {
        e.Fit(nv, ni, true);
        Array.Copy(v, e.V, nv); Array.Copy(z, e.Z, nv); Array.Copy(ib, e.I, ni); e.Nv = nv; e.Ni = ni; e.Has = true; K11World._tKeeps++;
    }
}
