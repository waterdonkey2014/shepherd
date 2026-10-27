using System.Reflection;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

public static partial class SceneRenderer
{
    /// <summary>Fidelity round 1 (survey item 8): drawing UnityEngine.U2D.SpriteShapeRenderer. The geometry is the game's own SpriteShape
    /// package output (Icalls/SpriteShapeIcalls.cs); here it is projected like a mesh, one draw per segment: segment 0 (the fill) with
    /// material 0 and the shape's fill texture, every other segment (edge / corner strips) with material 1 (material 0 when there is
    /// only one) and the texture of the sprite it was cut from. SHEPHERD_FX=nospriteshape turns it off (the shapes are not drawn, as before).</summary>
    [ProfileOnly]
    internal static class SpriteShapes
    {
        public static readonly bool On = !Fx.Off("spriteshape");
        static readonly List<SpriteShapeIcalls.Segment> _segs = new();
        static readonly Dictionary<Type, MethodInfo> _will = new();
        static readonly object[] _none = Array.Empty<object>();
        /// <summary>Unity's OnWillRenderObject: sent to the enabled scripts of a renderer's GameObject before a camera draws it (the
        /// SpriteShapeController bakes its mesh there).</summary>
        public static void WillRender(NGameObject go)
        {
            var cs = go.Components;
            for (int i = 0; i < cs.Count; i++)
            {
                if (cs[i] is not NMono m || m.Destroyed || !m.Enabled || !m.Awoken || m.Managed == null) continue;
                var t = m.Managed.GetType();
                if (!_will.TryGetValue(t, out var mi))
                {
                    for (var bt = t; bt != null && bt != typeof(MonoBehaviour) && mi == null; bt = bt.BaseType)
                        mi = bt.GetMethod("OnWillRenderObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    _will[t] = mi;
                }
                if (mi == null) continue;
                try { mi.Invoke(m.Managed, _none); } catch (TargetInvocationException e) { Log.Exception(e.InnerException ?? e, $"OnWillRenderObject {t.Name}"); }
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: OnWillRenderObject)
#endif
            }
        }
        /// <summary>Frustum test of the generated geometry's box (the vertices of every segment).</summary>
        public static unsafe bool Outside(SpriteShapeIcalls.Data d, in Matrix4x4 lw)
        {
            if (d.CullVer != d.Version)
            {
                d.CullVer = d.Version; d.CullEmpty = SpriteShapeIcalls.Segments(d, _segs) == 0;
                if (!d.CullEmpty)
                {
                    var p = SpriteShapeIcalls.Positions(d); Vector3 mn = p[_segs[0].V0], mx = mn;
                    foreach (var s in _segs) for (int i = s.V0; i < s.V0 + s.Vn; i++) { mn = Vector3.Min(mn, p[i]); mx = Vector3.Max(mx, p[i]); }
                    d.CullMin = mn; d.CullMax = mx;
                }
            }
            return d.CullEmpty || BoxOutside(in lw, d.CullMin.x, d.CullMin.y, d.CullMin.z, d.CullMax.x, d.CullMax.y, d.CullMax.z);
        }
        public static unsafe void Draw(SpriteShapeIcalls.Data d, Material[] mats, Color col, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh)
        {
            if (SpriteShapeIcalls.Segments(d, _segs) == 0) return;
            var pos = SpriteShapeIcalls.Positions(d); var uvs = SpriteShapeIcalls.Uvs(d); var ix = SpriteShapeIcalls.Indices(d);
            foreach (var s in _segs)
            {
                var mat = Objects.Get<NMaterial>(mats.Length == 0 ? null : mats[s.Geom == 0 ? 0 : Math.Min(1, mats.Length - 1)]);
                NTexture tex = null;
                if (s.Geom == 0) tex = d.Fill ? Objects.Get<NTexture>(d.Fill) : null;
                else if (s.Sprite >= 0 && s.Sprite < d.Sprites.Length && d.Sprites[s.Sprite] is { } sp && sp) tex = Objects.Get<NSprite>(sp)?.Texture;
                if (tex == null && mat != null && mat.P.TryGetValue(ShaderIds.MainTex, out var mt)) tex = RenderIcalls.Tex(mt);
                if (tex != null) RenderIcalls.Tex(tex.Managed);
                var tint = col; if (mat != null && mat.P.TryGetValue(ShaderIds.Color, out var mc) && mc is Color mcol) tint *= mcol;
                var fx = ShaderPolicy.World(mat, tex != null, false, out var fb);
                if (fx == ShaderPolicy.Kind.Skip) { FxDraw(mat, fx, fb); continue; }
                uint blend = fx == ShaderPolicy.Kind.Normal ? ShaderOf(mat?.ShaderName ?? "Sprites/Default").BlendWorld : fb;
                if (fx != ShaderPolicy.Kind.Normal) FxDraw(mat, fx, fb);
                var e = Emit0;   // (menu-speed: the frame's own emitter's buffers -- the old shared ones; built on the main thread also with SHEPHERD_MT_RENDER=1)
                int nv = s.Vn; var v = e.Verts(nv); var cv = e.ClipBuf(nv); bool outside = false, premul = blend == BLENDMODE_BLEND_PREMULTIPLIED;
                float cr = tint.r, cg = tint.g, cb = tint.b, ca = tint.a; if (premul) { cr *= ca; cg *= ca; cb *= ca; }
                for (int i = 0; i < nv; i++)
                {
                    var p = pos[s.V0 + i]; var u = uvs[s.V0 + i];
                    Project(in lw, in vp, in pr, gh, p.x, p.y, p.z, out var sx, out var sy, ref cv[i], ref outside);
                    ref var o = ref v[i];
                    o.position.x = sx; o.position.y = sy; o.color.r = cr; o.color.g = cg; o.color.b = cb; o.color.a = ca;
                    o.tex_coord.x = u.x; o.tex_coord.y = 1f - u.y;
                }
                if (e.IBuf.Length < s.In) e.IBuf = new int[Math.Max(s.In, e.IBuf.Length * 2)];
                var ib = e.IBuf; int ni = 0; for (int k = 0; k < s.In; k++) { int id = ix[s.I0 + k]; if (id < nv) ib[ni++] = id; }
                if (outside) { ni = e.ClipTriangles(ni, ref nv, in pr, gh); v = e.VBuf; }
                if (fx == ShaderPolicy.Kind.Normal) LitBegin(mat, in lw, false);
                SubmitFiltered(v, nv, ni, tex ?? WhiteTex(), blend, false);
                LitEnd();
            }
        }
    }
}
