using System.Runtime.CompilerServices;
using Shepherd.Engine.Native;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

// Kit #11 track gpu1 (vita/notes/kit11-gpu1.md): the world renderer's CHANGE SIGNATURE and WORLD-VERTEX REUSE, SHEPHERD_K11_REUSE=1 (default
// off). A normal, unlit world draw built on this thread (single-threaded rendering) is a pure function of its inputs: the geometry arrays, the
// world matrix, the camera's view-projection / pixel rect / target height, the tint, the blend and the Spine colour-0 rule. Each camera pass
// keeps one entry per draw, in draw order, holding those inputs (the signature) and the finished draw (vertices and indices after the index
// filter, the near/far clipping and the Spine filter: what WorldEmitter.Out handed SubmitIdx). When the next frame's draw at the same place
// has a signature equal BIT FOR BIT (floats by their bits, arrays by reference -- the engine only ever replaces a sprite's arrays unless game
// code holds them, GeomShared, and a mesh's arrays change only through the icalls, which bump its Version), the kept draw goes to SubmitIdx
// again: the same vertices and indices the build would have made, without the projection. The camera settling is what makes the hits: any
// change of the view-projection starts a new epoch and every entry of that pass rebuilds.
// Kept output memory: a draw's output is copied only when its WHOLE signature (camera epoch included) was already the same in the frame
// before -- a still draw under a still camera, replayed from the next frame on. A draw that moves, animates (an animated Spine mesh: a new
// version every frame) or whose camera moves keeps only its signature: no copy per frame while nothing can hit. Kept arrays are sized to the
// draw (a slot never keeps a large draw's full-size copy under a smaller one; large copies are let go when the entry stops keeping).
// Entries stay with their camera: the passes of destroyed cameras, and of cameras with no pass for 300 frames, are dropped at the frame's
// end with everything they hold (K11Age, kit #9's _camOrder rule); at most 4 cameras are kept (a fifth clears them). Not used: lit draws
// (the owner-local model shades the shared buffers in place), SHEPHERD_MT_RENDER=1 (the builds run on the render thread from captured
// inputs), the check modes (they want the stream rebuilt), FX draws, SpriteShapes, glows.
public static partial class SceneRenderer
{
    internal sealed class K11Entry
    {
        // the signature
        public NComponent R; public int Sub; public object Src, A0, A1, A2, A3; public int Ver, Epoch; public bool Fx, Fy, Skip0; public uint Blend; public NTexture Tex;
        public Matrix4x4 Lw; public Color Tint;
        // the output: State 0 = nothing drawn, 1 = drawn (SubmitIdx), 2 = every triangle left out (SkippedDraw); Has = the output is current
        public bool Has, Keeping; public byte State; public Vertex[] V = Array.Empty<Vertex>(); public int[] I = Array.Empty<int>(); public int Nv, Ni, Clip, Drop;
        /// <summary>The GPU path's kept world z per vertex (SceneRenderer.K11Proj.cs; null for the CPU path's entries).</summary>
        public float[] Z;
        /// <summary>Kit #13 looks (K13Looks.Persp): the kept draw's clip w per vertex (HasW), so a replay is drawn perspective-correctly as its
        /// build was (SubmitIdxW); null / HasW false = an affine draw (every w 1, or the row off: never set).</summary>
        public float[] W; public bool HasW;
        /// <summary>WorldEmitter.Out's finished draw (Keeping: copied; else only its state is noted and Has stays false). <paramref name="w"/>:
        /// the draw's clip w (kit #13 looks, a perspective draw), else null.</summary>
        public void Keep(Vertex[] v, int nv, int[] ib, int ni, bool drawn, float[] w = null)
        {
            State = drawn ? (byte)1 : (byte)2;
            if (!Keeping) return;
            Fit(nv, ni, false);
            Array.Copy(v, V, nv); Array.Copy(ib, I, ni); Nv = nv; Ni = ni; Has = true; K11World._tKeeps++;
            HasW = w != null;
            if (HasW) { if (W == null || W.Length < nv || W.Length - nv > Math.Max(nv, Slack)) W = new float[nv]; Array.Copy(w, W, nv); }
        }
        /// <summary>The kept arrays sized for nv vertices / ni indices (and z with the GPU path): grown when short, replaced when longer than
        /// the need by more than max(need, Slack) -- a slot where a large still draw once settled does not keep that copy under a smaller one.</summary>
        internal void Fit(int nv, int ni, bool z)
        {
            if (V.Length < nv || V.Length - nv > Math.Max(nv, Slack)) V = new Vertex[nv];
            if (I.Length < ni || I.Length - ni > Math.Max(ni, Slack)) I = new int[ni];
            if (z && (Z == null || Z.Length < nv || Z.Length - nv > Math.Max(nv, Slack))) Z = new float[nv];
        }
        const int Slack = 256;
        /// <summary>An entry that keeps nothing this frame: its kept copy is dead, so arrays larger than Slack are let go (small ones stay for the
        /// entry's next keep: no allocations when the draw list only shifts).</summary>
        internal void LetGo()
        {
            Has = false;
            if (V.Length > Slack) V = Array.Empty<Vertex>();
            if (I.Length > Slack) I = Array.Empty<int>();
            if (Z != null && Z.Length > Slack) Z = null;
            HasW = false; if (W != null && W.Length > Slack) W = null;
        }
        /// <summary>An entry past its pass's last draw: lets go of everything it points at (renderer, records, arrays, texture) and of large kept arrays.</summary>
        internal void Release() { R = null; Src = A0 = A1 = A2 = A3 = null; Tex = null; LetGo(); }
        public long Bytes => V.Length * 32L + I.Length * 4L + (Z?.Length ?? 0) * 4L + (W?.Length ?? 0) * 4L;
    }
    sealed class K11Pass
    {
        public Matrix4x4 Vp; public RectF Pr; public float Gh; public int Epoch; public bool Set;
        /// <summary>The frame of the camera's last pass (K11Age drops passes idle for 300 frames).</summary>
        public int Frame;
        public K11Entry[] E = new K11Entry[64]; public int N;
        public K11Entry Next()
        {
            if (N == E.Length) Array.Resize(ref E, E.Length * 2);
            return E[N++] ??= new K11Entry();
        }
    }
    static Dictionary<NBehaviour, K11Pass> _k11Pass;   // (made on the first pass with the switch on: nothing exists with it off)
    static K11Pass _k11Cur;

    /// <summary>DrawWorld's start: this camera pass's entries (null: reuse is off for this pass).</summary>
    static void K11Begin(NBehaviour cam, in Matrix4x4 vp, in RectF pr, float gh)
    {
        _k11Cur = null;
        if (_mtDefer || RenderCheck.On || UiCap != null) return;
        var d = _k11Pass ??= new Dictionary<NBehaviour, K11Pass>();
        if (!d.TryGetValue(cam, out var ps))
        {
            if (d.Count >= 4) d.Clear();   // (a fifth live camera: every kept pass goes; destroyed / idle cameras' passes already went in K11Age)
            d[cam] = ps = new K11Pass();
        }
        ps.Frame = Runtime.Time.FrameCount;
        if (!ps.Set || !SameM16(in ps.Vp, in vp) || !SameF(ps.Pr.X, pr.X) || !SameF(ps.Pr.Y, pr.Y) || !SameF(ps.Pr.W, pr.W) || !SameF(ps.Pr.H, pr.H) || !SameF(ps.Gh, gh))
        { ps.Vp = vp; ps.Pr = pr; ps.Gh = gh; ps.Epoch++; ps.Set = true; }
        ps.N = 0; _k11Cur = ps;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static bool SameF(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    /// <summary>The twelve entries Project reads of a world matrix, bit for bit.</summary>
    static bool SameM12(in Matrix4x4 a, in Matrix4x4 b) =>
        SameF(a.m00, b.m00) && SameF(a.m01, b.m01) && SameF(a.m02, b.m02) && SameF(a.m03, b.m03) && SameF(a.m10, b.m10) && SameF(a.m11, b.m11) && SameF(a.m12, b.m12) && SameF(a.m13, b.m13)
        && SameF(a.m20, b.m20) && SameF(a.m21, b.m21) && SameF(a.m22, b.m22) && SameF(a.m23, b.m23);
    static bool SameM16(in Matrix4x4 a, in Matrix4x4 b) => SameM12(in a, in b) && SameF(a.m30, b.m30) && SameF(a.m31, b.m31) && SameF(a.m32, b.m32) && SameF(a.m33, b.m33);
    static bool SameC(in Color a, in Color b) => SameF(a.r, b.r) && SameF(a.g, b.g) && SameF(a.b, b.b) && SameF(a.a, b.a);

    /// <summary>DrawWorld's end: entries past this pass's last draw let go of what they point at (renderers, meshes, textures of a frame with
    /// more draws). With K11Age (passes of destroyed or idle cameras), a destroyed object stays referenced at most until the end of the frame
    /// its camera stops drawing it -- or 300 frames when its camera lives on but stops drawing.</summary>
    static void K11End()
    {
        var ps = _k11Cur; if (ps == null) return;
        for (int k = ps.N; k < ps.E.Length && ps.E[k] != null && ps.E[k].R != null; k++) ps.E[k].Release();
        _k11Cur = null;
    }
    /// <summary>K11World.EndFrame (a REUSE or GPUPROJ switch on): the passes of destroyed cameras and of cameras with no pass for 300 frames
    /// are dropped with every entry they hold (kit #9's AgeCamOrders rule for _camOrder).</summary>
    internal static void K11Age(int frame) { AgeK11(_k11Pass, frame); AgeK11(_k11ProjPasses, frame); }
    static void AgeK11(Dictionary<NBehaviour, K11Pass> d, int frame)
    {
        if (d == null || d.Count == 0) return;
        List<NBehaviour> old = null;
        foreach (var kv in d) if (kv.Key.Destroyed || frame - kv.Value.Frame > 300) (old ??= new()).Add(kv.Key);
        if (old != null) foreach (var c in old) { d.Remove(c); K11World._tPassesAged++; }
    }
    /// <summary>A kept draw handed to SubmitIdx again (or SkippedDraw), with the near/far counters it added when it was built.</summary>
    static void K11Replay(K11Entry e, NTexture tex, uint blend)
    {
        if (e.State == 1) { if (e.HasW) SubmitIdxW(e.V, e.Nv, e.I, e.Ni, tex, blend, e.W); else SubmitIdx(e.V, e.Nv, e.I, e.Ni, tex, blend, false); }   // (kit #13 looks: a perspective draw replays with its w)
        else if (e.State == 2) SkippedDraw(e.V, e.Nv, e.Ni, tex, blend);
        Emit0.ClipTris += e.Clip; Emit0.DropTris += e.Drop;
        K11World._reuseHits++; K11World._reuseVerts += e.Nv;
    }

    /// <summary>A normal unlit sprite draw (Emit0.EmitSprite's inputs) through the pass's entry at this place.</summary>
    static void K11Sprite(ref WItem it, NSprite ns, in Matrix4x4 vp, in RectF pr, float gh)
    {
        var ps = _k11Cur; var e = ps.Next();
        bool src = ReferenceEquals(e.R, it.R) && e.Sub == -1 && ReferenceEquals(e.Src, ns) && ReferenceEquals(e.A0, ns.Vertices) && ReferenceEquals(e.A1, ns.Uv) && ReferenceEquals(e.A2, ns.Triangles) && !ns.GeomShared;
        bool same = src && e.Epoch == ps.Epoch && e.Fx == it.Fx && e.Fy == it.Fy && e.Blend == it.Blend && ReferenceEquals(e.Tex, ns.Texture) && SameM12(in e.Lw, in it.Lw) && SameC(in e.Tint, in it.Col);
        if (same && e.Has) { K11Replay(e, ns.Texture, it.Blend); return; }
        e.R = it.R; e.Sub = -1; e.Src = ns; e.A0 = ns.Vertices; e.A1 = ns.Uv; e.A2 = ns.Triangles; e.A3 = null; e.Ver = 0; e.Epoch = ps.Epoch;
        e.Fx = it.Fx; e.Fy = it.Fy; e.Skip0 = false; e.Blend = it.Blend; e.Tex = ns.Texture; e.Lw = it.Lw; e.Tint = it.Col;
        e.State = 0; e.Keeping = same;   // (copied only when the whole signature held since the frame before: a still draw under a still camera; GeomShared fails src)
        if (!same) e.LetGo();
        int c0 = Emit0.ClipTris, d0 = Emit0.DropTris;
        Emit0.K11Rec = e;
        try { Emit0.EmitSprite(ns.Vertices, ns.Uv, ns.Triangles, it.Fx, it.Fy, in it.Lw, in vp, in pr, gh, ns.Texture, it.Col, it.Blend); }
        finally { Emit0.K11Rec = null; }
        e.Clip = Emit0.ClipTris - c0; e.Drop = Emit0.DropTris - d0;
        if (src && e.State == 0) e.Has = true;   // (nothing to draw: kept as such)
        K11World._reuseMiss++;
    }

    /// <summary>A normal unlit mesh submesh draw (Emit0.EmitMesh's inputs) through the pass's entry at this place. Only the mesh's own index
    /// array (not a culled copy) is ever reused: a copy's contents are not covered by the mesh's version.</summary>
    static void K11Mesh(ref WItem it, int s, int[] sub, NTexture tex, Color tint, uint bs, bool skip0, in Matrix4x4 vp, in RectF pr, float gh)
    {
        var ps = _k11Cur; var e = ps.Next(); var m = it.MeshRec;
        bool own = m != null && ReferenceEquals(sub, it.Subs[s]) && s < m.Sub.Count && ReferenceEquals(sub, m.Sub[s]) && ReferenceEquals(it.Pos, m.Pos) && ReferenceEquals(it.MCol, m.Col) && ReferenceEquals(it.Uv, m.Uv0);
        bool src = own && ReferenceEquals(e.R, it.R) && e.Sub == s && ReferenceEquals(e.Src, m) && e.Ver == m.Version && ReferenceEquals(e.A0, it.Pos) && ReferenceEquals(e.A1, it.MCol) && ReferenceEquals(e.A2, it.Uv) && ReferenceEquals(e.A3, sub);
        bool same = src && e.Epoch == ps.Epoch && e.Skip0 == skip0 && e.Blend == bs && ReferenceEquals(e.Tex, tex) && SameM12(in e.Lw, in it.Lw) && SameC(in e.Tint, in tint);
        if (same && e.Has) { K11Replay(e, tex, bs); return; }
        e.R = it.R; e.Sub = s; e.Src = m; e.A0 = it.Pos; e.A1 = it.MCol; e.A2 = it.Uv; e.A3 = sub; e.Ver = m?.Version ?? 0; e.Epoch = ps.Epoch;
        e.Fx = e.Fy = false; e.Skip0 = skip0; e.Blend = bs; e.Tex = tex; e.Lw = it.Lw; e.Tint = tint;
        e.State = 0; e.Keeping = same;   // (copied only when the whole signature held since the frame before: animated meshes, moving draws and a moving camera keep no copy)
        if (!same) e.LetGo();
        int c0 = Emit0.ClipTris, d0 = Emit0.DropTris;
        Emit0.K11Rec = e;
        try { Emit0.EmitMesh(it.Pos, it.MCol, it.Uv, sub, in it.Lw, in vp, in pr, gh, tex, tint, bs, skip0); }
        finally { Emit0.K11Rec = null; }
        e.Clip = Emit0.ClipTris - c0; e.Drop = Emit0.DropTris - d0;
        if (src && e.State == 0) e.Has = true;
        K11World._reuseMiss++;
    }
    /// <summary>The kept draws' memory (managed arrays), for the [k11] line.</summary>
    internal static long K11ReuseBytes()
    {
        long b = 0;
        if (_k11Pass != null) foreach (var ps in _k11Pass.Values) for (int k = 0; k < ps.E.Length; k++) if (ps.E[k] != null) b += ps.E[k].Bytes;
        if (_k11ProjPasses != null) foreach (var ps in _k11ProjPasses.Values) for (int k = 0; k < ps.E.Length; k++) if (ps.E[k] != null) b += ps.E[k].Bytes;
        return b;
    }
}
