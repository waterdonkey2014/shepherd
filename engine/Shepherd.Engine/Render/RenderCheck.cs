using System.Runtime.InteropServices;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>SHEPHERD_RENDER_CHECK=1 (V6 stage 2, check runs only; vita/notes/engine-pass-speed.md): proves, frame by frame and in
/// the same process, that the optimized SceneRenderer hands the backend exactly what the pre-stage-2 renderer would have.
/// Every frame, the draw stream the real pass sent (each Draw: texture handle, blend, sdf flag, every vertex's 32 bytes, every
/// index; each Clip / NoClip / FillScreen) is recorded, then this class re-runs the PRE-stage-2 code — a verbatim copy of its
/// DrawWorld / DrawOverlayCanvases / Emit / AllCameras / the Stylizer lookup, on its own copies of the old math (the Matrix4x4
/// indexer loop, the array inverse, an uncached TRS chain) and the old full scans of Objects.All — into a recorder only, and
/// the two streams are compared bit for bit. The world/UI passes run no game code, so both see the same scene state; texture
/// handles come from the same cache (SceneRenderer.Handle). Mismatches: [rendercheck] lines (the first 20 in full);
/// <see cref="Summary"/> at exit. Slow (the whole old renderer once more per frame): never in timing runs.</summary>
[ProfileOnly]
public static class RenderCheck
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_RENDER_CHECK") == "1";
    static long _frames, _draws, _verts, _bad, _camBad, _styBad, _styChecks;

    // ---------------- the recorder ----------------
    public sealed class Rec
    {
        public readonly List<(int kind, IntPtr tex, uint blend, bool sdf, Vertex[] v, int[] idx, int x, int y, int w, int h)> E = new();
#if SHEPHERD_EBOOT
        public void Draw(IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx) => E.Add((0, tex, blend, sdf, v.ToArray(), idx.ToArray(), 0, 0, 0, 0));
        public void Clip(int x, int y, int w, int h) => E.Add((1, IntPtr.Zero, 0, false, null, null, x, y, w, h));
        public void NoClip() => E.Add((2, IntPtr.Zero, 0, false, null, null, 0, 0, 0, 0));
        public void Mark(int what) => E.Add((3, IntPtr.Zero, 0, false, null, null, what, 0, 0, 0));   // 1 = EndWorldPass
        /// <summary>Render-target operations, recorded for the stream hash only (the pre-stage-2 comparison never had them).</summary>
        public void Target(IntPtr t) { if (!On) E.Add((4, t, 0, false, null, null, 0, 0, 0, 0)); }
        public void ClearTarget(float r, float g, float b, float a) { if (!On) E.Add((5, IntPtr.Zero, 0, false, null, null, BitConverter.SingleToInt32Bits(r), BitConverter.SingleToInt32Bits(g), BitConverter.SingleToInt32Bits(b), BitConverter.SingleToInt32Bits(a))); }
        /// <summary>Integration with fidelity round 1: a lit draw (the owner-local model's DrawAdd), for the stream hash only: its shaded vertices
        /// and indices (kind 6; the per-vertex added colours stay inside the owner-local code).</summary>
        public void Lit(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx) { if (!On) E.Add((6, tex, blend, false, v.ToArray(), idx.ToArray(), 0, 0, 0, 0)); }
#else
        // the same recorder; kit #11 measure (Diag/K11Census.cs): the pixel-contribution census reads the target back right after each event of a
        // sampled frame's live stream (SHEPHERD_K11_CENSUS; Mac / tools builds only -- the eboots keep the lines above)
        public void Draw(IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx) { E.Add((0, tex, blend, sdf, v.ToArray(), idx.ToArray(), 0, 0, 0, 0)); K11(0, tex, blend, sdf, v, idx, idx.Length, 0, 0, 0, 0); }
        public void Clip(int x, int y, int w, int h) { E.Add((1, IntPtr.Zero, 0, false, null, null, x, y, w, h)); K11(1, IntPtr.Zero, 0, false, default, default, 0, x, y, w, h); }
        public void NoClip() { E.Add((2, IntPtr.Zero, 0, false, null, null, 0, 0, 0, 0)); K11(2, IntPtr.Zero, 0, false, default, default, 0, 0, 0, 0, 0); }
        public void Mark(int what) { E.Add((3, IntPtr.Zero, 0, false, null, null, what, 0, 0, 0)); K11(3, IntPtr.Zero, 0, false, default, default, 0, what, 0, 0, 0); }   // 1 = EndWorldPass
        /// <summary>Render-target operations, recorded for the stream hash only (the pre-stage-2 comparison never had them).</summary>
        public void Target(IntPtr t) { if (!On) { E.Add((4, t, 0, false, null, null, 0, 0, 0, 0)); K11(4, t, 0, false, default, default, 0, 0, 0, 0, 0); } }
        public void ClearTarget(float r, float g, float b, float a) { if (!On) { E.Add((5, IntPtr.Zero, 0, false, null, null, BitConverter.SingleToInt32Bits(r), BitConverter.SingleToInt32Bits(g), BitConverter.SingleToInt32Bits(b), BitConverter.SingleToInt32Bits(a))); K11(5, IntPtr.Zero, 0, false, default, default, 0, 0, 0, 0, 0); } }
        /// <summary>Integration with fidelity round 1: a lit draw (the owner-local model's DrawAdd), for the stream hash only: its shaded vertices
        /// and indices (kind 6; the per-vertex added colours stay inside the owner-local code).</summary>
        public void Lit(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx) { if (!On) { E.Add((6, tex, blend, false, v.ToArray(), idx.ToArray(), 0, 0, 0, 0)); K11(6, tex, blend, false, v, idx, idx.Length, 0, 0, 0, 0); } }
        void K11(int kind, IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx, int nIdx, int x, int y, int w, int h)
        {
            if (Diag.K11Census.Live && ReferenceEquals(this, Live)) Diag.K11Census.Event(kind, tex, blend, sdf, v, idx, nIdx, x, y, w, h);
            if (kind == 3 && x == 1 && Diag.K11Green.On && ReferenceEquals(this, Live)) Diag.K11Green.WorldPassEnded();   // (the green check's phase: a region redrawn before this is graded by the post pass)
        }
#endif
        /// <summary>Kit #6 gpulight: a static-mesh draw (IGpuMesh), for the stream hash only (kind 7): program, first vertex, count, the data
        /// array as a first-seen id, and the uniforms' bits as the index list.</summary>
        public void Gpu(IntPtr tex, uint blend, int program, int first, int count, float[] data, ReadOnlySpan<Sdl.FColor> u)
        {
            if (On) return;
            var bits = new int[u.Length * 4];
            for (int k = 0; k < u.Length; k++) { bits[k * 4] = BitConverter.SingleToInt32Bits(u[k].r); bits[k * 4 + 1] = BitConverter.SingleToInt32Bits(u[k].g); bits[k * 4 + 2] = BitConverter.SingleToInt32Bits(u[k].b); bits[k * 4 + 3] = BitConverter.SingleToInt32Bits(u[k].a); }
            int id = _dataIds.IndexOf(data) + 1; if (id == 0) { _dataIds.Add(data); id = _dataIds.Count; }
            E.Add((7, tex, blend, false, null, bits, program, first, count, id));
#if !SHEPHERD_EBOOT
            K11(7, tex, blend, false, default, default, count, program, first, count, id);
#endif
        }
        /// <summary>Kit #11 gpu2 (review fix): DrawGpuMeshTex's two extra textures and wrap bits, right after its kind-7 record (kind 9, one per
        /// texture: the handle as a first-seen id, the wrap bits, the unit). Only that opt-in draw records them: kit #10's streams are unchanged.</summary>
        public void GpuTex(IntPtr tex1, IntPtr tex2, int wrap) { if (On) return; E.Add((9, tex1, (uint)wrap, false, null, null, 1, 0, 0, 0)); E.Add((9, tex2, (uint)wrap, false, null, null, 2, 0, 0, 0)); }
        static readonly List<float[]> _dataIds = new();   // (first-seen ids; stream-hash runs only)
        /// <summary>Kit #11 gpu1: a GPU-projected world draw (IK11Proj), for the stream hash only (kind 8): its world-space vertices, then as the
        /// index list its indices followed by the bits of every vertex's world z and of the pass (view-projection, pixel rect, target height).</summary>
        public void Proj(IntPtr tex, uint blend, Span<Vertex> v, Span<float> z, Span<int> idx, in K11ProjPass p)
        {
            if (On) return;
            var bits = new int[idx.Length + z.Length + 21]; int o = 0;
            foreach (var i in idx) bits[o++] = i;
            foreach (var f in z) bits[o++] = BitConverter.SingleToInt32Bits(f);
            for (int k = 0; k < 16; k++) bits[o++] = BitConverter.SingleToInt32Bits(p.Vp[k]);
            bits[o++] = BitConverter.SingleToInt32Bits(p.X); bits[o++] = BitConverter.SingleToInt32Bits(p.Y); bits[o++] = BitConverter.SingleToInt32Bits(p.W); bits[o++] = BitConverter.SingleToInt32Bits(p.H); bits[o++] = BitConverter.SingleToInt32Bits(p.Gh);
            E.Add((8, tex, blend, false, v.ToArray(), bits, idx.Length, 0, 0, 0));
#if !SHEPHERD_EBOOT
            K11(8, tex, blend, false, v, idx, idx.Length, 0, 0, 0, 0);   // (integration review: the census reads its pixels -- DrawProj ran just before; ReadPixels flushes its batch)
#endif
        }
        /// <summary>Kit #13 looks (K13Looks.Persp): a perspective draw's clip w, for the stream hash only -- the bits appended to the index list of the
        /// draw recorded at <paramref name="at"/> (-1: the last one; a lit draw's kind 6 too), the same on the single-threaded and the multicore
        /// paths. Only a perspective draw has them: with the switch off every stream is kit #12's.</summary>
        public void Persp(ReadOnlySpan<float> w, int at = -1)
        {
            if (On || E.Count == 0) return;
            int i = at < 0 ? E.Count - 1 : at; var e = E[i]; if (e.idx == null) return;
            var bits = new int[e.idx.Length + w.Length]; Array.Copy(e.idx, bits, e.idx.Length);
            for (int k = 0; k < w.Length; k++) bits[e.idx.Length + k] = BitConverter.SingleToInt32Bits(w[k]);
            e.idx = bits; E[i] = e;
        }
        /// <summary>Kit #13 looks (SDF exact): the last draw's per-vertex parameters, recorded as Persp records floats (r, g, b, a per vertex).</summary>
        public void PerspF(ReadOnlySpan<Sdl.FColor> p)
        {
            if (On || E.Count == 0) return;
            int i = E.Count - 1; var e = E[i]; if (e.idx == null) return;
            var bits = new int[e.idx.Length + p.Length * 4]; Array.Copy(e.idx, bits, e.idx.Length);
            for (int k = 0, o = e.idx.Length; k < p.Length; k++, o += 4)
            { var c = p[k]; bits[o] = BitConverter.SingleToInt32Bits(c.r); bits[o + 1] = BitConverter.SingleToInt32Bits(c.g); bits[o + 2] = BitConverter.SingleToInt32Bits(c.b); bits[o + 3] = BitConverter.SingleToInt32Bits(c.a); }
            e.idx = bits; E[i] = e;
        }
        /// <summary>SHEPHERD_MT_RENDER=1: the slot of a draw the render thread builds later (kind 99 = none: the build drew nothing; not hashed).</summary>
        public int Placeholder() { E.Add((99, IntPtr.Zero, 0, false, null, null, 0, 0, 0, 0)); return E.Count - 1; }
        public void Fill(int at, IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx) => E[at] = (0, tex, blend, sdf, v.ToArray(), idx.ToArray(), 0, 0, 0, 0);
    }
    /// <summary>The live pass's stream for the current frame (SceneRenderer records into it while <see cref="On"/>).</summary>
    public static Rec Live;
#if SHEPHERD_EBOOT
    public static void BeginFrame() { if (On) Live = new Rec(); else if (HashFile != null) Live ??= new Rec(); }
#else
    public static void BeginFrame()
    {
        if (On) Live = new Rec(); else if (HashFile != null) Live ??= new Rec();
        if (Diag.K11Census.On) Diag.K11Census.FrameBegin();   // kit #11 measure: a census frame's baseline readback (after Begin's clear)
        if (Diag.K11Green.On) Diag.K11Green.FrameBegin();     // kit #11 measure: the green check's world-pass phase
    }
#endif

    // ---------------- menu-speed: SHEPHERD_STREAM_HASH=<file> ----------------
    /// <summary>SHEPHERD_STREAM_HASH=&lt;file&gt; (output-identity A/B between engine builds; check runs only): every frame, an FNV-1a 64 hash of the
    /// whole stream the renderer handed the backend -- each draw (texture handle as a first-seen id, blend, sdf, every vertex's 32 bytes, every index), each
    /// clip / no-clip / FillScreen / EndWorldPass mark, and the render-target binds / clears / Blit draws (also those issued by game code
    /// between frames, counted into the next frame) -- appended as "frame, events, draws, vertices, hash" to the file. Two builds
    /// whose files are identical handed the backend bit-identical input on every frame. Without the pre-stage-2 comparison (that is
    /// SHEPHERD_RENDER_CHECK=1; both can run together).</summary>
#if SHEPHERD_EBOOT
    public static readonly string HashFile = Environment.GetEnvironmentVariable("SHEPHERD_STREAM_HASH") is { Length: > 0 } hf ? hf : null;
#else
    // kit #11 measure (Diag/K11Measure.cs; Mac / tools builds): the heat map and the census read the same per-frame stream -- with one of them
    // on and no file asked for, HashFile is "" (the stream is recorded and handed to them, no file is written)
    public static readonly string HashFile = Environment.GetEnvironmentVariable("SHEPHERD_STREAM_HASH") is { Length: > 0 } hf ? hf : Diag.K11Measure.StreamTap ? "" : null;
#endif
    static StreamWriter _hashOut;
    /// <summary>Backend handles as first-seen ids: SDL's are heap pointers (different every run), GL's are object names.</summary>
    static readonly Dictionary<IntPtr, int> _texIds = new();
    static int TexId(IntPtr t) { if (t == IntPtr.Zero) return 0; if (!_texIds.TryGetValue(t, out var id)) _texIds[t] = id = _texIds.Count + 1; return id; }
    public static void HashFrame(int frame)
    {
        if (HashFile == null || Live == null) return;
        HashRec(frame, Live);
        if (!On) Live = new Rec();
    }
    /// <summary>The hash line of one frame's stream. SHEPHERD_MT_RENDER=1: called by whichever thread completes the frame (in frame order), with
    /// the frame's recorder detached from <see cref="Live"/> when the frame was recorded and its placeholders filled.</summary>
    public static unsafe void HashRec(int frame, Rec rec)
    {
#if !SHEPHERD_EBOOT
        if (Diag.K11Measure.StreamTap) Diag.K11Measure.Stream(frame, rec);   // kit #11 measure: the heat map / census get the frame's stream
        if (HashFile.Length == 0) return;
#endif
        ulong h = 14695981039346656037UL; long draws = 0, verts = 0; int events = 0;
        static ulong Mix(ulong h, ulong v) { for (int i = 0; i < 8; i++) { h = (h ^ (v & 0xff)) * 1099511628211UL; v >>= 8; } return h; }
        foreach (var e in rec.E)
        {
            if (e.kind == 99) continue;   // an MT placeholder whose build drew nothing
            events++;
            h = Mix(h, (ulong)e.kind); h = Mix(h, (ulong)TexId(e.tex)); h = Mix(h, e.blend); h = Mix(h, e.sdf ? 1UL : 0UL);
            h = Mix(h, (uint)e.x); h = Mix(h, (uint)e.y); h = Mix(h, (uint)e.w); h = Mix(h, (uint)e.h);
            if (e.v != null)
            {
                draws++; verts += e.v.Length; h = Mix(h, (ulong)e.v.Length);
                fixed (Vertex* pv = e.v) { byte* b = (byte*)pv; int n = e.v.Length * sizeof(Vertex); for (int i = 0; i < n; i++) h = (h ^ b[i]) * 1099511628211UL; }
            }
            if (e.idx != null) { h = Mix(h, (ulong)e.idx.Length); foreach (var i in e.idx) h = Mix(h, (uint)i); }
        }
        _hashOut ??= new StreamWriter(HashFile, false) { AutoFlush = true };
        _hashOut.WriteLine($"{frame}\t{events}\t{draws}\t{verts}\t{h:x16}");
    }

    /// <summary>After the real frame's draws: replay the old renderer and compare.</summary>
    public static void EndFrame(List<NBehaviour> newCams, IStylizer stylizer)
    {
        if (!On || Live == null) return;
        var live = Live; Live = null; _frames++;
        try
        {
            var oldCams = OldAllCameras().ToList();
            if (!oldCams.SequenceEqual(newCams)) { _camBad++; Note($"camera list differs: new [{string.Join(", ", newCams.Select(c => c.Go?.Name))}] old [{string.Join(", ", oldCams.Select(c => c.Go?.Name))}]"); }
            if (stylizer != null)   // (the Stylizer pass is owner-local: without it there is no new lookup to compare)
            {
                var newStylizer = stylizer.LastFound; var oldSty = OldFindStylizer(); _styChecks++;
                if (!ReferenceEquals(oldSty, newStylizer)) { _styBad++; Note($"Stylizer lookup differs: new '{newStylizer?.Go?.Name}' old '{oldSty?.Go?.Name}'"); }
            }
            var rec = new Rec(); _canvasScreen.Clear();
            var first = oldCams.FirstOrDefault();
            foreach (var cam in oldCams)
            {
                int clear = cam.Get("clearFlags") is { } cf ? Convert.ToInt32(cf) : 1;
                if (cam != first && (clear == 1 || clear == 2)) { var col = cam.Get("backgroundColor") is Color bc ? bc : Color.black; OldFillScreen(rec, col); }
                try { OldDrawWorld(rec, cam); } catch (Exception e) { Note($"old DrawWorld threw {e.GetType().Name}: {e.Message}"); }
            }
            rec.Mark(1);
            try { OldDrawOverlayCanvases(rec); } catch (Exception e) { Note($"old DrawOverlayCanvases threw {e.GetType().Name}: {e.Message}"); }
            Compare(live, rec);
            CheckTransforms();
        }
        catch (Exception e) { _bad++; Note($"check failed: {e.GetType().Name}: {e.Message}"); }
    }

    static void Compare(Rec a, Rec b)
    {
        int n = Math.Min(a.E.Count, b.E.Count);
        for (int i = 0; i < n; i++)
        {
            var x = a.E[i]; var y = b.E[i];
            if (x.kind == 0) { _draws++; _verts += x.v.Length; }
            string why = x.kind != y.kind ? $"event kind {x.kind} vs {y.kind}"
                : x.kind == 0 ? (x.tex != y.tex ? "texture" : x.blend != y.blend ? "blend" : x.sdf != y.sdf ? "sdf"
                    : !MemoryMarshal.AsBytes(x.v.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(y.v.AsSpan())) ? $"vertices ({x.v.Length} vs {y.v.Length}{FirstVertexDiff(x.v, y.v)})"
                    : !x.idx.AsSpan().SequenceEqual(y.idx) ? $"indices ({x.idx.Length} vs {y.idx.Length})" : null)
                : x.kind == 1 ? (x.x != y.x || x.y != y.y || x.w != y.w || x.h != y.h ? "clip rect" : null)
                : x.kind == 3 ? (x.x != y.x ? "mark" : null) : null;
            if (why != null) { _bad++; Note($"frame {Runtime.Time.FrameCount}: event #{i} of {a.E.Count}/{b.E.Count} differs: {why}"); return; }
        }
        if (a.E.Count != b.E.Count) { _bad++; Note($"frame {Runtime.Time.FrameCount}: {a.E.Count} events (new) vs {b.E.Count} (old)"); }
    }
    static string FirstVertexDiff(Vertex[] a, Vertex[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var p = a[i]; var q = b[i];
            if (!MemoryMarshal.AsBytes(new Span<Vertex>(ref p)).SequenceEqual(MemoryMarshal.AsBytes(new Span<Vertex>(ref q))))
                return $"; first at #{i}: new ({p.position.x:R},{p.position.y:R} c {p.color.r:R},{p.color.g:R},{p.color.b:R},{p.color.a:R} uv {p.tex_coord.x:R},{p.tex_coord.y:R}) old ({q.position.x:R},{q.position.y:R} c {q.color.r:R},{q.color.g:R},{q.color.b:R},{q.color.a:R} uv {q.tex_coord.x:R},{q.tex_coord.y:R})";
        }
        return "";
    }
    /// <summary>Every live transform's memoized derived values (Native/Objects.cs NTransform) against the old uncached formulas,
    /// bit for bit: world matrix, its inverse, rect, rotation, lossy scale, position.</summary>
    static void CheckTransforms()
    {
        foreach (var n in Registry.Transforms.Items())
        {
            if (n is not NTransform t) continue;
            _tChecks++;
            string why = !SameM(t.LocalToWorld, OldLocalToWorld(t)) ? "LocalToWorld" : !SameM(t.WorldToLocal, OldWorldToLocal(t)) ? "WorldToLocal"
                : !SameM(t.LocalMatrix, OldLocalMatrix(t)) ? "LocalMatrix" : !Same(t.Rect, OldRect(t)) ? "Rect" : !Bits.Same(t.Rotation, OldRotation(t)) ? "Rotation"
                : !Bits.Same(t.LossyScale, OldLossyScale(t)) ? "LossyScale" : !Bits.Same(t.Position, OldPosition(t)) ? "Position" : null;
            if (why != null) { _tBad++; Note($"frame {Runtime.Time.FrameCount}: transform '{t.Name}' #{t.InstanceId}: memoized {why} differs from the old formula"); }
        }
    }
    static long _tChecks, _tBad;
    static unsafe bool SameM(Matrix4x4 a, Matrix4x4 b) { float* x = &a.m00, y = &b.m00; for (int i = 0; i < 16; i++) if (((uint*)x)[i] != ((uint*)y)[i]) return false; return true; }
    static bool Same(UnityEngine.Rect a, UnityEngine.Rect b) => Bits.Same(a.position, b.position) && Bits.Same(a.size, b.size);
    static Quaternion OldRotation(NTransform t) { var q = t.LocalRotation; for (var p = t.Parent; p != null; p = p.Parent) q = M.QMul(p.LocalRotation, q); return q; }
    static Vector3 OldLossyScale(NTransform t) { var s = t.LocalScale; for (var p = t.Parent; p != null; p = p.Parent) s = Vector3.Scale(p.LocalScale, s); return s; }
    static void Note(string s) { if (++_notes <= 20) Log.Info("[rendercheck] " + s); }
    static int _notes;

    public static string Summary() => !On ? null :
        $"[rendercheck] {_frames} frames re-rendered by the pre-stage-2 renderer and compared: {_draws} draws, {_verts} vertices; {_bad} stream mismatches, {_camBad} camera-list mismatches, {_styBad} of {_styChecks} Stylizer lookups differ; transform memos: {_tChecks} checked against the old formulas, {_tBad} differ; LazyRef memo hits re-resolved: {RefHits}, {RefBad} differ";

    // ======================= the pre-stage-2 renderer, verbatim (G.* -> rec.*, Objects/M -> the old scans and math) =======================
    static IEnumerable<NBehaviour> OldAllCameras() =>
        Objects.All.OfType<NBehaviour>().Where(b => !b.Destroyed && b.Managed is Camera && b.Enabled && b.Go != null && b.Go.ActiveInHierarchy && b.Go.Scene != 0)
            .OrderBy(b => Conv.To(b.Get("depth"), typeof(float)) is float d ? d : 0f);
    static NMono OldFindStylizer() => Objects.All.OfType<NMono>().FirstOrDefault(m => !m.Destroyed && m.Enabled && m.Managed.GetType().Name == "Stylizer" && m.Go.ActiveInHierarchy && m.Go.Scene != 0);

    static readonly Dictionary<NComponent, (int frame, Matrix4x4 m)> _canvasScreen = new();
    static Matrix4x4 RootLocalToScreen(NComponent root)
    {
        var G = SceneRenderer.G;
        float s = root.Get("scaleFactor") is { } sf ? (float)Conv.To(sf, typeof(float)) : 1f; if (s <= 0) s = 1;
        return OldTRS(new Vector3(G.W / 2f, G.H / 2f, 0), Quaternion.identity, new Vector3(s, s, s));
    }
    static Matrix4x4 CanvasToScreen(NComponent root)
    {
        if (_canvasScreen.TryGetValue(root, out var c) && c.frame == Runtime.Time.FrameCount) return c.m;
        var m = OldMul(RootLocalToScreen(root), OldWorldToLocal(root.Go.Transform));
        _canvasScreen[root] = (Runtime.Time.FrameCount, m); return m;
    }

    static void OldDrawOverlayCanvases(Rec rec)
    {
        var roots = Objects.All.OfType<NBehaviour>().Where(cv => !cv.Destroyed && cv.Managed is Canvas && cv.Enabled && cv.Go != null && cv.Go.Scene != 0 && cv.Go.ActiveInHierarchy && RenderIcalls.RootCanvasOf(cv.Go) == cv)
            .Where(cv => (cv.Get("renderMode") is { } rm ? Convert.ToInt32(rm) : 0) != 2 && !SceneRenderer.CameraCanvasHidden(cv))   // kit #13 basec: the live passes' Camera-mode rule (SHEPHERD_K13_CAMCANVAS; off: every root, as before)
            .OrderBy(cv => cv.Get("sortingOrder") is { } so ? Convert.ToInt32(so) : 0).ToList();
        foreach (var root in roots) { int depth = 0; DrawUiTree(rec, root.Go, ref depth); }
        rec.NoClip();
    }
    static void DrawUiTree(Rec rec, NGameObject go, ref int depth)
    {
        if (!go.ActiveInHierarchy) return;
        foreach (var c in go.Components)
        {
            if (c is NCanvasRenderer cr && !cr.Destroyed && !cr.Cull && cr.Idx.Length > 0) { depth++; DrawCanvasRenderer(rec, cr); }   // (AbsoluteDepth is the live pass's to set)
        }
        foreach (var ch in go.Transform.Children.ToArray()) DrawUiTree(rec, ch.Go, ref depth);
    }
    static void DrawCanvasRenderer(Rec rec, NCanvasRenderer cr)
    {
        var G = SceneRenderer.G;
        var mat = cr.Materials.Length > 0 ? cr.Materials[0] : null;
        var tex = cr.Texture ?? (mat?.P.TryGetValue(ShaderIds.MainTex, out var mt) == true ? RenderIcalls.Tex(mt) : null);
        float alpha = OldUiAlpha(cr.Go);
        var tint = cr.Color; tint.a *= alpha;
        if (mat != null && mat.P.TryGetValue(ShaderIds.Color, out var mc) && mc is Color mcol) tint *= mcol;
        if (tint.a <= 0.001f) return;
        var root = RenderIcalls.RootCanvasOf(cr.Go);
        var m = root != null ? OldMul(CanvasToScreen(root), OldLocalToWorld(cr.Go.Transform)) : OldLocalToWorld(cr.Go.Transform);
        string shader = mat?.ShaderName ?? "UI/Default";
        bool sdf = shader.Contains("Distance Field") || shader.Contains("TextMeshPro");
        if (cr.RectClipping)
        {
            var rm = root != null ? RootLocalToScreen(root) : Matrix4x4.identity;
            var p0 = M.Point(rm, new Vector3(cr.ClipRect.xMin, cr.ClipRect.yMin, 0)); var p1 = M.Point(rm, new Vector3(cr.ClipRect.xMax, cr.ClipRect.yMax, 0));
            rec.Clip((int)Math.Min(p0.x, p1.x), (int)(G.H - Math.Max(p0.y, p1.y)), (int)Math.Abs(p1.x - p0.x), (int)Math.Abs(p1.y - p0.y));
        }
        else rec.NoClip();
        Emit(rec, cr.Pos, cr.Col, cr.Uv0, cr.Idx, v => { var w = M.Point(m, v); return new Vector2(w.x, G.H - w.y); }, tex, tint, Blend(shader, premulSpine: shader.Contains("Spine")), sdf);
    }

    static float OldUiAlpha(NGameObject go)
    {
        float al = 1f;
        for (var t = go?.Transform; t != null; t = t.Parent)
            foreach (var c in t.Go.Components)
                if (c.Managed is CanvasGroup)
                {
                    al *= c.Get("alpha") is float f ? f : 1f;
                    if (c.Get("ignoreParentGroups") is true) return al;
                }
        return al;
    }
    /// <summary>LazyRef's remembered resolutions, re-resolved and compared on every hit while the check runs (Assets/Loader.cs).</summary>
    public static long RefHits, RefBad;

    sealed class Item { public float Layer, Order, Dist; public int Seq; public Action Draw; }
    static void OldDrawWorld(Rec rec, NBehaviour cam)
    {
        var G = SceneRenderer.G;
        int mask = cam.Get("cullingMask") is { } cm ? Convert.ToInt32(cm is UnityEngine.LayerMask lm ? lm.value : cm) : -1;
        var view = OldView(cam); var proj = CameraIcalls.Proj(cam); var vp = OldMul(proj, view);
        var pr = CameraIcalls.PixelRect(cam);
        Vector2 ToScreen(Vector3 w, out bool behind)
        {
            float cx = vp.m00 * w.x + vp.m01 * w.y + vp.m02 * w.z + vp.m03, cy = vp.m10 * w.x + vp.m11 * w.y + vp.m12 * w.z + vp.m13, cw = vp.m30 * w.x + vp.m31 * w.y + vp.m32 * w.z + vp.m33;
            behind = cw <= 1e-5f; if (behind) cw = 1e-5f;
            return new Vector2(pr.x + (cx / cw * 0.5f + 0.5f) * pr.width, G.H - (pr.y + (cy / cw * 0.5f + 0.5f) * pr.height));
        }
        var camPos = OldPosition(cam.Go.Transform); var items = new List<Item>(); int seq = 0;
        foreach (var n in Objects.Snapshot())
        {
            if (n is not NComponent r || r.Destroyed || r.Go == null || r.Go.Scene == 0 || !r.Go.ActiveInHierarchy) continue;
            if ((mask & (1 << r.Go.Layer)) == 0) continue;
            if (r.Get("enabled") is { } en && !(bool)Conv.To(en, typeof(bool))) continue;
            var tr = r.Go.Transform;
            if (r.Managed is SpriteRenderer)
            {
                var sp = Conv.To(r.Get("sprite"), typeof(Sprite)) as Sprite; var ns = Objects.Get<NSprite>(sp); if (ns == null) continue;
                RenderIcalls.Tex(ns.Texture?.Managed); ns.EnsureGeometry();
                var col = r.Get("color") is Color c ? c : Color.white; bool fx = CameraIcalls.B(r, "flipX", false), fy = CameraIcalls.B(r, "flipY", false);
                var mat = Objects.Get<NMaterial>(RendererIcalls.Shared(r).FirstOrDefault());
                var lw = OldLocalToWorld(tr); var verts = ns.Vertices.Select(v => new Vector3(fx ? -v.x : v.x, fy ? -v.y : v.y, 0)).ToArray();
                var uv = ns.Uv.Select(u => new Vector4(u.x, u.y, 0, 0)).ToArray(); var idx = ns.Triangles.Select(i => (int)i).ToArray();
                items.Add(new Item
                {
                    Layer = SceneRenderer.SortingLayerValue(r), Order = CameraIcalls.F(r, "sortingOrder", 0), Dist = (OldPosition(tr) - camPos).sqrMagnitude, Seq = seq++,
                    Draw = () => Emit(rec, verts, null, uv, idx, v => ToScreen(M.Point(lw, v), out _), ns.Texture, col, Blend(mat?.ShaderName ?? "Sprites/Default", false), false)
                });
            }
            else if (r.Managed is MeshRenderer)
            {
                var mf = r.Go.Components.FirstOrDefault(c => c.Managed is MeshFilter); if (mf == null) continue;
                var mesh = Objects.Get<NMesh>(Conv.To(mf.Get("sharedMesh") ?? mf.Get("mesh"), typeof(Mesh))); if (mesh == null || mesh.VertexCount == 0) continue;
                var mats = RendererIcalls.Shared(r); var mpb = r.Get("__mpb") as Dictionary<int, object>;
                var lw = OldLocalToWorld(tr); var snapshotPos = mesh.Pos; var snapshotCol = mesh.Col; var snapshotUv = mesh.Uv0; var subs = mesh.Sub.ToArray();
                items.Add(new Item
                {
                    Layer = SceneRenderer.SortingLayerValue(r), Order = CameraIcalls.F(r, "sortingOrder", 0), Dist = (OldPosition(tr) - camPos).sqrMagnitude, Seq = seq++,
                    Draw = () =>
                    {
                        for (int s = 0; s < subs.Length; s++)
                        {
                            var mat = Objects.Get<NMaterial>(mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null);
                            var tex = mpb != null && mpb.TryGetValue(ShaderIds.MainTex, out var mt) && mt is Texture ? RenderIcalls.Tex(mt) : mat != null && mat.P.TryGetValue(ShaderIds.MainTex, out var t) ? RenderIcalls.Tex(t) : null;
                            var tint = mpb != null && mpb.TryGetValue(ShaderIds.Color, out var mc2) && mc2 is Color mcol2 ? mcol2 : mat != null && mat.P.TryGetValue(ShaderIds.Color, out var tc) && tc is Color tcol ? tcol : Color.white;
                            Emit(rec, snapshotPos, snapshotCol, snapshotUv, subs[s], v => ToScreen(M.Point(lw, v), out _), tex, tint, Blend(mat?.ShaderName ?? "", premulSpine: true), false);
                        }
                    }
                });
            }
        }
        foreach (var it in items.OrderBy(i => i.Layer).ThenBy(i => i.Order).ThenByDescending(i => i.Dist).ThenBy(i => i.Seq)) it.Draw();
    }

    static uint Blend(string shader, bool premulSpine)
    {
        var s = shader ?? "";
        if (s.Contains("Additive") || s.Contains("Add")) return BLENDMODE_ADD;
        if (s.Contains("Multiply")) return BLENDMODE_MOD;
        if (premulSpine && s.Contains("Spine") && !s.Contains("Straight")) return BLENDMODE_BLEND_PREMULTIPLIED;
        return BLENDMODE_BLEND;
    }
    static void OldFillScreen(Rec rec, Color c)
    {
        var G = SceneRenderer.G;
        var v = new Vertex[4]; float[] xs = { 0, G.W, G.W, 0 }, ys = { 0, 0, G.H, G.H };
        for (int i = 0; i < 4; i++) v[i] = new Vertex { position = new() { x = xs[i], y = ys[i] }, color = new() { r = c.r, g = c.g, b = c.b, a = 1 } };
        rec.Draw(IntPtr.Zero, BLENDMODE_NONE, false, v, new[] { 0, 1, 2, 0, 2, 3 });
    }
    static void Emit(Rec rec, Vector3[] pos, Color32[] col, Vector4[] uv, int[] idx, Func<Vector3, Vector2> project, NTexture tex, Color tint, uint blend, bool sdf)
    {
        if (pos == null || idx == null || pos.Length == 0 || idx.Length < 3) return;
        var v = new Vertex[pos.Length];
        bool premul = blend == BLENDMODE_BLEND_PREMULTIPLIED;
        for (int i = 0; i < pos.Length; i++)
        {
            var p = project(pos[i]); var c = col != null && i < col.Length ? (Color)col[i] : Color.white; c *= tint;
            if (premul) { c.r *= c.a; c.g *= c.a; c.b *= c.a; }
            var u = uv != null && i < uv.Length ? uv[i] : Vector4.zero;
            v[i] = new Vertex { position = new() { x = p.x, y = p.y }, color = new() { r = c.r, g = c.g, b = c.b, a = c.a }, tex_coord = new() { x = u.x, y = 1f - u.y } };
        }
        var ix = idx.Where(i => i >= 0 && i < pos.Length).ToArray(); if (ix.Length - ix.Length % 3 < 3) return;
        if (ix.Length % 3 != 0) Array.Resize(ref ix, ix.Length - ix.Length % 3);
        rec.Draw(SceneRenderer.Handle(tex, sdf), blend, sdf, v, ix);
    }

    // ---------------- the pre-stage-2 math (Native/Objects.cs M + NTransform) ----------------
    static Matrix4x4 OldMul(Matrix4x4 a, Matrix4x4 b)
    {
        var r = new Matrix4x4();
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) { float s = 0; for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j]; r[i, j] = s; }
        return r;
    }
    static Matrix4x4 OldInverse(Matrix4x4 m)
    {
        var a = new double[16]; for (int i = 0; i < 16; i++) a[i] = m[i % 4, i / 4];
        var inv = new double[16];
        inv[0] = a[5] * a[10] * a[15] - a[5] * a[11] * a[14] - a[9] * a[6] * a[15] + a[9] * a[7] * a[14] + a[13] * a[6] * a[11] - a[13] * a[7] * a[10];
        inv[4] = -a[4] * a[10] * a[15] + a[4] * a[11] * a[14] + a[8] * a[6] * a[15] - a[8] * a[7] * a[14] - a[12] * a[6] * a[11] + a[12] * a[7] * a[10];
        inv[8] = a[4] * a[9] * a[15] - a[4] * a[11] * a[13] - a[8] * a[5] * a[15] + a[8] * a[7] * a[13] + a[12] * a[5] * a[11] - a[12] * a[7] * a[9];
        inv[12] = -a[4] * a[9] * a[14] + a[4] * a[10] * a[13] + a[8] * a[5] * a[14] - a[8] * a[6] * a[13] - a[12] * a[5] * a[10] + a[12] * a[6] * a[9];
        inv[1] = -a[1] * a[10] * a[15] + a[1] * a[11] * a[14] + a[9] * a[2] * a[15] - a[9] * a[3] * a[14] - a[13] * a[2] * a[11] + a[13] * a[3] * a[10];
        inv[5] = a[0] * a[10] * a[15] - a[0] * a[11] * a[14] - a[8] * a[2] * a[15] + a[8] * a[3] * a[14] + a[12] * a[2] * a[11] - a[12] * a[3] * a[10];
        inv[9] = -a[0] * a[9] * a[15] + a[0] * a[11] * a[13] + a[8] * a[1] * a[15] - a[8] * a[3] * a[13] - a[12] * a[1] * a[11] + a[12] * a[3] * a[9];
        inv[13] = a[0] * a[9] * a[14] - a[0] * a[10] * a[13] - a[8] * a[1] * a[14] + a[8] * a[2] * a[13] + a[12] * a[1] * a[10] - a[12] * a[2] * a[9];
        inv[2] = a[1] * a[6] * a[15] - a[1] * a[7] * a[14] - a[5] * a[2] * a[15] + a[5] * a[3] * a[14] + a[13] * a[2] * a[7] - a[13] * a[3] * a[6];
        inv[6] = -a[0] * a[6] * a[15] + a[0] * a[7] * a[14] + a[4] * a[2] * a[15] - a[4] * a[3] * a[14] - a[12] * a[2] * a[7] + a[12] * a[3] * a[6];
        inv[10] = a[0] * a[5] * a[15] - a[0] * a[7] * a[13] - a[4] * a[1] * a[15] + a[4] * a[3] * a[13] + a[12] * a[1] * a[7] - a[12] * a[3] * a[5];
        inv[14] = -a[0] * a[5] * a[14] + a[0] * a[6] * a[13] + a[4] * a[1] * a[14] - a[4] * a[2] * a[13] - a[12] * a[1] * a[6] + a[12] * a[2] * a[5];
        inv[3] = -a[1] * a[6] * a[11] + a[1] * a[7] * a[10] + a[5] * a[2] * a[11] - a[5] * a[3] * a[10] - a[9] * a[2] * a[7] + a[9] * a[3] * a[6];
        inv[7] = a[0] * a[6] * a[11] - a[0] * a[7] * a[10] - a[4] * a[2] * a[11] + a[4] * a[3] * a[10] + a[8] * a[2] * a[7] - a[8] * a[3] * a[6];
        inv[11] = -a[0] * a[5] * a[11] + a[0] * a[7] * a[9] + a[4] * a[1] * a[11] - a[4] * a[3] * a[9] - a[8] * a[1] * a[7] + a[8] * a[3] * a[5];
        inv[15] = a[0] * a[5] * a[10] - a[0] * a[6] * a[9] - a[4] * a[1] * a[10] + a[4] * a[2] * a[9] + a[8] * a[1] * a[6] - a[8] * a[2] * a[5];
        double det = a[0] * inv[0] + a[1] * inv[4] + a[2] * inv[8] + a[3] * inv[12];
        var r = new Matrix4x4(); if (Math.Abs(det) < 1e-20) return r;
        for (int i = 0; i < 16; i++) r[i % 4, i / 4] = (float)(inv[i] / det);
        return r;
    }
    static Quaternion OldQNorm(Quaternion q) { float n = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w); return n < 1e-12f ? Quaternion.identity : new(q.x / n, q.y / n, q.z / n, q.w / n); }
    static Vector3 OldRotate(Quaternion q, Vector3 v)
    {
        float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f, xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y, xz = q.x * z, yz = q.y * z, wx = q.w * x, wy = q.w * y, wz = q.w * z;
        return new((1f - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z, (xy + wz) * v.x + (1f - (xx + zz)) * v.y + (yz - wx) * v.z, (xz - wy) * v.x + (yz + wx) * v.y + (1f - (xx + yy)) * v.z);
    }
    static Matrix4x4 OldTRS(Vector3 t, Quaternion q, Vector3 s)
    {
        q = OldQNorm(q);
        var c0 = OldRotate(q, new Vector3(s.x, 0, 0)); var c1 = OldRotate(q, new Vector3(0, s.y, 0)); var c2 = OldRotate(q, new Vector3(0, 0, s.z));
        var m = new Matrix4x4();
        m.m00 = c0.x; m.m10 = c0.y; m.m20 = c0.z; m.m01 = c1.x; m.m11 = c1.y; m.m21 = c1.z; m.m02 = c2.x; m.m12 = c2.y; m.m22 = c2.z;
        m.m03 = t.x; m.m13 = t.y; m.m23 = t.z; m.m33 = 1f; return m;
    }
    static UnityEngine.Rect OldParentRect(NTransform t) => t.Parent != null && t.Parent.IsRect ? OldRect(t.Parent) : default;
    static Vector2 OldSize(NTransform t) { var pr = OldParentRect(t); return t.SizeDelta + Vector2.Scale(t.AnchorMax - t.AnchorMin, pr.size); }
    static UnityEngine.Rect OldRect(NTransform t) { var s = OldSize(t); return new UnityEngine.Rect(-t.Pivot.x * s.x, -t.Pivot.y * s.y, s.x, s.y); }
    static Vector3 OldEffectiveLocalPosition(NTransform t)
    {
        if (!t.IsRect) return t.LocalPosition;
        var pr = OldParentRect(t);
        float ax = Mathf.LerpUnclamped(t.AnchorMin.x, t.AnchorMax.x, t.Pivot.x), ay = Mathf.LerpUnclamped(t.AnchorMin.y, t.AnchorMax.y, t.Pivot.y);
        return new Vector3(pr.x + pr.width * ax + t.AnchoredPosition.x, pr.y + pr.height * ay + t.AnchoredPosition.y, t.LocalPosition.z);
    }
    static Matrix4x4 OldLocalMatrix(NTransform t) => OldTRS(OldEffectiveLocalPosition(t), t.LocalRotation, t.LocalScale);
    static Matrix4x4 OldLocalToWorld(NTransform t) { var m = OldLocalMatrix(t); for (var p = t.Parent; p != null; p = p.Parent) m = OldMul(OldLocalMatrix(p), m); return m; }
    static Matrix4x4 OldWorldToLocal(NTransform t) => OldInverse(OldLocalToWorld(t));
    static Vector3 OldPosition(NTransform t) => M.Point(t.Parent != null ? OldLocalToWorld(t.Parent) : Matrix4x4.identity, OldEffectiveLocalPosition(t));
    static Matrix4x4 OldView(NComponent c) { var m = OldWorldToLocal(c.Go.Transform); for (int i = 0; i < 4; i++) m[2, i] = -m[2, i]; return m; }
}
