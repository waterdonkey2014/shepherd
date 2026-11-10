using static Shepherd.Engine.Render.Sdl;   // constants and the Vertex layout only: this backend never calls SDL
namespace Shepherd.Engine.Render;

/// <summary>SHEPHERD_RENDER=null (V6, vita/notes/v6-mac-sim.md): a backend that draws nothing. Everything above the GPU runs
/// exactly as with the GL backend — layout, uGUI rebuilds, DrawWorld/overlay vertex building, texture decode/upload choices
/// (DXT blocks straight from the pack, SDF atlases cut to alpha8), the Stylizer's per-frame parameter lookup and its layer map —
/// and so does the GL backend's CPU-side batching (same merge key, same break reasons, vertices copied into one frame array,
/// indices rebased to 16 bit), so draws in/out and the per-frame CPU work match what the Vita's vitaGL backend will do.
/// Skipped: every GL call, the GPU work, readback and present. Screenshots are no-ops (null mode has no image: prove menu
/// state with SHEPHERD_MENU_PROBE). No SDL at all: Backends.NoSdl makes InputState.Pump skip SDL_PollEvent, so input comes
/// only from SHEPHERD_PAD / SHEPHERD_PAD_FILE. Headless only. SHEPHERD_NULL_DXT=0 refuses DXT (RGBA8 decode path, like GL without
/// SHEPHERD_GL_DXT).</summary>
[ProfileOnly]
public sealed class NullBackend : IRenderBackend, ICompressedUpload, ICompressedUploadPtr, IGpuStylizer, IAlphaUpload, IAlphaUploadPtr, IGpuPost, IAddDraw, IWorldPassSplit, IGpuMesh, IK12Stencil, IK13Persp, IK13Sdf
{
    public string Name => "null";
    public int W { get; }
    public int H { get; }
    public RenderStats Stats { get; } = new();
    readonly RenderStats _cur = new();
    public bool Dxt { get; } = Environment.GetEnvironmentVariable("SHEPHERD_NULL_DXT") != "0";
    /// <summary>GL's texture cap under the Vita profile (4096). Bigger textures are counted (GL would box-filter them down).</summary>
    public readonly int TexCap = Environment.GetEnvironmentVariable("SHEPHERD_GL_MAXTEX") is string mt && int.TryParse(mt, out var m) && m > 0 ? m : 4096;
    const int Stride = 32;

    enum Kind : byte { Rgba8, Alpha8, Dxt, Target }
    sealed class Tex { public int W, H; public long Bytes; public Kind Kind; public bool Npot; public int UseGen = -1, FrameSeen = -1; public bool ClipOn; public int Cx, Cy, Cw, Ch; }
    readonly Dictionary<IntPtr, Tex> _tex = new();
    long _next = 1;
    readonly Tex _white, _main;   // the 1x1 white texture untextured draws batch on, and the frame's main target (its clip state is the main view's)
    Tex _view, _target;

    struct Batch { public Tex Tex; public uint Blend; public byte Prog; public bool Wrap, ClipOn; public int Cx, Cy, Cw, Ch, Base, I0, Ni; }
    Batch[] _b = new Batch[256]; int _nb;
    Vertex[] _v = new Vertex[16384]; int _nv;
    ushort[] _i = new ushort[49152]; int _ni;
    int _chunkBase, _gen, _frame;
    public long TotalUploads, TotalUploadBytes, CompressedUploads, CompressedBytes, OverCap, Frames;

    public NullBackend(int w, int h)
    {
        W = w; H = h;
        _white = new Tex { W = 1, H = 1, Bytes = 4, Kind = Kind.Rgba8 };
        _main = new Tex { W = w, H = h, Bytes = (long)w * h * 4, Kind = Kind.Target };
        _view = _target = _main;
        Log.Info($"[null] render backend: draws nothing, no SDL; {w}x{h}, DXT uploads {(Dxt ? "taken (as GL+SHEPHERD_GL_DXT=1)" : "refused (SHEPHERD_NULL_DXT=0)")}, texture cap {TexCap}");
    }

    IntPtr Add(Tex t) { var h = (IntPtr)_next++; _tex[h] = t; _cur.Uploads++; _cur.UploadBytes += t.Bytes; TotalUploads++; TotalUploadBytes += t.Bytes; return h; }
    static bool Pot(int v) => v > 0 && (v & (v - 1)) == 0;

    public IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf = false, bool owned = false)
    {
        if (w <= 0 || h <= 0 || rgba == null || rgba.Length < w * h * 4) return IntPtr.Zero;
        if (w > TexCap || h > TexCap) { OverCap++; Warn("cap" + w + "x" + h, $"[null] texture {w}x{h} exceeds the {TexCap} cap (GL would box-filter it down)"); }
        if (sdf)
        {   // GL keeps only the distance channel (GL_ALPHA, 1 B/px): same CPU cut here, then dropped
            var a8 = new byte[w * h]; for (int i = 0; i < a8.Length; i++) a8[i] = rgba[i * 4 + 3];
            return Add(new Tex { W = w, H = h, Bytes = a8.Length, Kind = Kind.Alpha8, Npot = !Pot(w) || !Pot(h) });
        }
        return Add(new Tex { W = w, H = h, Bytes = (long)w * h * 4, Kind = Kind.Rgba8, Npot = !Pot(w) || !Pot(h) });
    }

    public IntPtr CreateSdfAlpha8Texture(int w, int h, byte[] a8, bool linear)
    {
        if (w <= 0 || h <= 0 || a8 == null || a8.Length < w * h || w > TexCap || h > TexCap) return IntPtr.Zero;
        return Add(new Tex { W = w, H = h, Bytes = (long)w * h, Kind = Kind.Alpha8, Npot = !Pot(w) || !Pot(h) });
    }
    /// <summary>IAlphaUploadPtr (kit #6 (c)): counted as the array upload is.</summary>
    public IntPtr CreateSdfAlpha8Texture(int w, int h, IntPtr a8, bool linear)
    {
        if (w <= 0 || h <= 0 || a8 == IntPtr.Zero || w > TexCap || h > TexCap) return IntPtr.Zero;
        return Add(new Tex { W = w, H = h, Bytes = (long)w * h, Kind = Kind.Alpha8, Npot = !Pot(w) || !Pot(h) });
    }

    public IntPtr CreateCompressedTexture(int w, int h, int unityFormat, byte[] blocks, bool linear, Func<byte[]> rgba) =>
        blocks == null ? IntPtr.Zero : CompressedCount(w, h, unityFormat, blocks.Length);
    /// <summary>ICompressedUploadPtr (kit #6 (c)): counted as the array upload is.</summary>
    public IntPtr CreateCompressedTexture(int w, int h, int unityFormat, IntPtr blocks, int length, bool linear) =>
        blocks == IntPtr.Zero ? IntPtr.Zero : CompressedCount(w, h, unityFormat, length);
    IntPtr CompressedCount(int w, int h, int unityFormat, int length)
    {
        int bb = DxtBlocks.BlockBytes(unityFormat);
        int size = (w + 3) / 4 * ((h + 3) / 4) * bb;
        if (!Dxt || bb == 0 || w > TexCap || h > TexCap || length < size) return IntPtr.Zero;
        CompressedUploads++; CompressedBytes += size;
        return Add(new Tex { W = w, H = h, Bytes = size, Kind = Kind.Dxt, Npot = !Pot(w) || !Pot(h) });
    }

    public void DestroyTexture(IntPtr tex)
    {
        if (tex == IntPtr.Zero || !_tex.TryGetValue(tex, out var t)) return;
        if (t.UseGen == _gen && _nb > 0) Flush();   // GL flushes when a pending batch still samples it
        _tex.Remove(tex);
    }

    public IntPtr CreateTarget(int w, int h) { Flush(); return Add(new Tex { W = w, H = h, Bytes = (long)w * h * 4, Kind = Kind.Target, Npot = !Pot(w) || !Pot(h) }); }
    public void SetTarget(IntPtr target)
    {
        var t = target == IntPtr.Zero ? _main : _tex.TryGetValue(target, out var tt) && tt.Kind == Kind.Target ? tt : null;
        if (t == null) { Warn("badtarget", "[null] SetTarget: not a render target"); return; }
        if (_target != t) { Flush(); _target = t; }
        _view = target == IntPtr.Zero ? _main : t;
    }

    public void Begin(float r, float g, float b, float a) { Flush(); _target = _view = _main; _main.ClipOn = false; }
    public void ClearTarget(float r, float g, float b, float a) { Flush(); _cur.Clears++; }
    public void Clip(int x, int y, int w, int h) { _view.ClipOn = true; _view.Cx = x; _view.Cy = y; _view.Cw = w; _view.Ch = h; }
    public void NoClip() => _view.ClipOn = false;
    /// <summary>Kit #12 boot (IK12Stencil, SHEPHERD_K12_STENCIL=1): counted only (the null backend draws nothing).</summary>
    public int StencilStates;
    public void Stencil(in K12StencilState s) => StencilStates++;
    /// <summary>Kit #13 looks (IK13Persp, SHEPHERD_K13_PERSP=1): GlBackend's CPU side -- the draw as Draw / DrawAdd queue it, plus its w stream's 16 bytes a vertex.</summary>
    public long PerspDraws;
    public void DrawW(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, ReadOnlySpan<float> w) { PerspDraws++; Draw(tex, blend, false, v, idx); _cur.VertexBytes += v.Length * 16L; }
    public void DrawAddW(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add, ReadOnlySpan<float> w) { PerspDraws++; DrawAdd(tex, blend, v, idx, add); }
    public void DrawEnc(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> enc, bool add) { PerspDraws++; Draw(tex, blend, false, v, idx); _cur.VertexBytes += v.Length * 16L; }
    /// <summary>Kit #13 looks (IK13Sdf): counted, queued as the SDF draw it replaces, plus its parameter stream's 16 bytes a vertex.</summary>
    public long SdfDraws;
    public bool DrawSdfX(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> param, Span<Vertex> before, uint beforeBlend) { SdfDraws++; Draw(tex, blend, true, v, idx); _cur.VertexBytes += v.Length * 16L; return true; }

    /// <summary>GlBackend.Draw's CPU side, call for call: the same validation, wrap test, merge key and break reasons, and the
    /// same copy of the vertices into the frame array with 16-bit index rebasing. No GL state, no upload.</summary>
    public void Draw(IntPtr texH, uint blend, bool sdf, Span<Vertex> v, Span<int> idx)
    {
        if (v.Length == 0 || idx.Length == 0 || idx.Length % 3 != 0) return;
        Tex t = null;
        if (texH != IntPtr.Zero && !_tex.TryGetValue(texH, out t)) return;
        int nv = v.Length;
        foreach (var i in idx) if ((uint)i >= (uint)nv) return;
        uint bl = t == null ? BLENDMODE_NONE : blend;
        var bt = t ?? _white;
        byte prog = (byte)(t != null && sdf ? 1 : 0);
        if (ShaderPolicy.IsGrab(bl)) { prog = 2; Flush(); _cur.Grabs++; _cur.DrawsOut++; }   // polish round 2: GL flushes and copies the backdrop first
        else if (_addDraw) prog = 3;   // fidelity round 1: GL's sprite+ADD program
        bool wrap = false;
        if (t != null) foreach (ref readonly var vx in v) { float uu = vx.tex_coord.x, vv = vx.tex_coord.y; if (uu < 0f || vv < 0f || uu > 1f || vv > 1f) { wrap = true; break; } }
        var view = _view;
        _cur.DrawsIn++; _cur.Tris += idx.Length / 3;
        if (wrap) { _cur.WrapDraws++; if (bt.Npot) _cur.WrapNpotDraws++; }
        if (view.ClipOn && (view.Cw <= 0 || view.Ch <= 0)) _cur.EmptyClipDraws++;
        if (nv > 65536) { Warn("bigdraw", $"[null] draw with {nv} vertices > 65536 (GL expands it into unindexed pieces)"); _cur.Batches++; _cur.DrawsOut++; return; }
        if (_nv - _chunkBase + nv > 65536) { _chunkBase = _nv; _cur.Chunks++; }
        if (_nv + nv > _v.Length) Array.Resize(ref _v, Math.Max(_v.Length * 2, _nv + nv));
        if (_ni + idx.Length > _i.Length) Array.Resize(ref _i, Math.Max(_i.Length * 2, _ni + idx.Length));
        bool merged = false;
        if (_nb > 0)
        {
            ref var last = ref _b[_nb - 1];
            var why = last.Base != _chunkBase ? BreakReason.Chunk : last.Tex != bt ? BreakReason.Texture : last.Blend != bl ? BreakReason.Blend : last.Prog != prog ? BreakReason.Shader
                : last.Wrap != wrap ? BreakReason.Wrap : last.ClipOn != view.ClipOn || (view.ClipOn && (last.Cx != view.Cx || last.Cy != view.Cy || last.Cw != view.Cw || last.Ch != view.Ch)) ? BreakReason.Clip : BreakReason.Count;
            if (why == BreakReason.Count) { last.Ni += idx.Length; merged = true; }
            else _cur.Breaks[(int)why]++;
        }
        else _cur.Breaks[(int)BreakReason.First]++;
        if (!merged)
        {
            if (_nb == _b.Length) Array.Resize(ref _b, _b.Length * 2);
            _b[_nb++] = new Batch { Tex = bt, Blend = bl, Prog = prog, Wrap = wrap, ClipOn = view.ClipOn, Cx = view.Cx, Cy = view.Cy, Cw = view.Cw, Ch = view.Ch, Base = _chunkBase, I0 = _ni, Ni = idx.Length };
        }
        bt.UseGen = _gen;
        v.CopyTo(_v.AsSpan(_nv));
        int rebase = _nv - _chunkBase;
        for (int k = 0; k < idx.Length; k++) _i[_ni + k] = (ushort)(idx[k] + rebase);
        _nv += nv; _ni += idx.Length;
    }

    /// <summary>GL: two glBufferData + one glDrawElements per batch. Here: the counters only.</summary>
    void Flush()
    {
        if (_nb == 0) { _nv = _ni = _chunkBase = 0; return; }
        _cur.Flushes++; _cur.VertexBytes += (long)_nv * Stride + _ni * 2L;
        _cur.Batches += _nb; _cur.DrawsOut += _nb;
        for (int k = 0; k < _nb; k++) { var bt = _b[k].Tex; if (bt.FrameSeen != _frame) { bt.FrameSeen = _frame; _cur.Textures++; } }   // distinct textures per frame, as GL counts them
        _nb = 0; _nv = _ni = _chunkBase = 0; _gen++;
    }

    public void EndWorldPass() { EndWorldPassBase(); DerivedRender.Post?.ApplyGpu(this); DerivedRender.Stylizer?.ApplyGpu(this); }
    public void EndWorldPassBase() { Flush(); _cur.WorldDrawsIn = _cur.DrawsIn; _cur.WorldBatches = _cur.Batches; }
    /// <summary>Fidelity round 1, GL: one full-screen colour-grading pass. Here: counted (the Vita sim sees the pass's cost in the counters).</summary>
    public bool RunLut(in LutArgs a)
    {
        if (!_tex.ContainsKey(a.Lut1) || !_tex.ContainsKey(a.Lut2)) return false;
        Flush(); _cur.DrawsOut++; _cur.FullScreenPasses++; LutPasses++; return true;
    }
    public long LutPasses;
    /// <summary>Fidelity round 1: an ADD draw batches like any draw (GL: its own program, so a shader break) plus 16 B/vertex of upload.</summary>
    public bool SupportsAdd => true;
    public void DrawAdd(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add) { _addDraw = true; try { Draw(tex, blend, false, v, idx); } finally { _addDraw = false; } _cur.VertexBytes += v.Length * 16L; }
    bool _addDraw;
    /// <summary>Kit #6 gpulight, GL: a program built on first use (here: available whenever GL would have the sources), then one GL draw per
    /// call (the queued batches flushed first) and the mesh's static buffer uploaded once. Here: counted, as GL counts it.</summary>
    public int GpuProgram(string name, string vertexFile, string fragmentFile)
    {
        _gpuNames ??= new List<string>(); _gpuMeshes ??= new List<float[]>();
        int k = _gpuNames.IndexOf(name); if (k < 0) { _gpuNames.Add(name); k = _gpuNames.Count - 1; }
        return GlBackend.HasSource(vertexFile) && GlBackend.HasSource(fragmentFile) ? k + 1 : 0;
    }
    List<string> _gpuNames; List<float[]> _gpuMeshes;   // (made on the first call: nothing in a run that never asks)
    /// <summary>Kit #11 gpu2: DrawGpuMesh with two more textures -- counted as one, like GL's single draw.</summary>
    public void DrawGpuMeshTex(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr tex, IntPtr tex1, IntPtr tex2, int wrap, uint blend)
        => DrawGpuMesh(program, data, stride, first, count, uniforms, tex, blend);
    public void DrawGpuMesh(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr tex, uint blend)
    {
        if (_gpuMeshes == null || program <= 0 || data == null || count < 3 || (tex != IntPtr.Zero && !_tex.ContainsKey(tex))) return;
        Flush();
        _cur.DrawsIn++; _cur.DrawsOut++; _cur.Batches++; _cur.Tris += count / 3; _cur.Breaks[(int)BreakReason.Shader]++;
        var bt = tex == IntPtr.Zero ? _white : _tex[tex]; if (bt.FrameSeen != _frame) { bt.FrameSeen = _frame; _cur.Textures++; }
        bool seen = false; foreach (var m in _gpuMeshes) if (ReferenceEquals(m, data)) { seen = true; break; }
        if (!seen) { _gpuMeshes.Add(data); _cur.Uploads++; _cur.UploadBytes += data.Length * 4L; _cur.VertexBytes += data.Length * 4L; }
        GpuMeshDraws++;
    }
    public long GpuMeshDraws;
    public void ReleaseGpuMesh(float[] data) { if (data == null || _gpuMeshes == null) return; for (int i = 0; i < _gpuMeshes.Count; i++) if (ReferenceEquals(_gpuMeshes[i], data)) { _gpuMeshes.RemoveAt(i); return; } }
    /// <summary>GL: one full-screen fragment pass. Here: counted.</summary>
    public void RunStylizer(in GlBackend.StylizeArgs a)
    {
        if (!_tex.ContainsKey(a.Lut1) || !_tex.ContainsKey(a.Layers)) return;
        Flush(); _cur.DrawsOut++; _cur.FullScreenPasses++; StylizerPasses++;
    }
    public long StylizerPasses;
    public void EndFrame() { Flush(); _cur.CopyTo(Stats); _cur.Reset(); Frames++; _frame++; }
    public void Finish() => Flush();
    public void Present() { }
    public byte[] ReadPixels(out int w, out int h) { w = h = 0; return null; }
    public void Screenshot(string path) => Warn("shot", "[null] screenshots are no-ops with SHEPHERD_RENDER=null (use SHEPHERD_MENU_PROBE for menu state)");
    public void Dispose() { }

    readonly HashSet<string> _warned = new();
    void Warn(string key, string msg) { if (_warned.Add(key)) Log.Info(msg); }

    /// <summary>What GL would hold resident right now (excluding the main target and the white texture).</summary>
    public string TextureSummary()
    {
        long rgba = 0, dxt = 0, a8 = 0, tg = 0; int nr = 0, nd = 0, na = 0, nt = 0;
        foreach (var t in _tex.Values) switch (t.Kind) { case Kind.Dxt: nd++; dxt += t.Bytes; break; case Kind.Alpha8: na++; a8 += t.Bytes; break; case Kind.Target: nt++; tg += t.Bytes; break; default: nr++; rgba += t.Bytes; break; }
        return $"resident textures (as GL would hold them): {nr} RGBA8 = {rgba / 1048576.0:F1} MiB, {na} SDF alpha8 = {a8 / 1048576.0:F1} MiB, {nd} DXT = {dxt / 1048576.0:F1} MiB, {nt} extra targets = {tg / 1048576.0:F1} MiB; " +
               $"main targets 2 x {W * H * 4 / 1048576.0:F1} MiB; uploaded since start: {TotalUploads} textures, {TotalUploadBytes / 1048576.0:F1} MiB ({CompressedUploads} compressed, {CompressedBytes / 1048576.0:F1} MiB); over the cap: {OverCap}; Stylizer passes: {StylizerPasses}";
    }
}
