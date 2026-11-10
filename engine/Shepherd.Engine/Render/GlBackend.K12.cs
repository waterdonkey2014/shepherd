using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>Kit #12 track menusa (vita/notes/kit12-menusa.md), SHEPHERD_K12_UIREPLAY=1: recorded command lists for fixed screens + incremental UI
/// re-batching (IK12UiReplay; SceneRenderer.UiCache.cs opens and closes the recording around the fast UI pass).
/// - The UI pass starts on an empty batcher (K12UiBegin flushes what is pending), so its vertices, indices and batches are a flush of their
///   own. Every UI draw is recorded with the batcher's state before it (vertex / index / batch counts, the last batch's index count, the
///   chunk base).
/// - A draw MATCHES last frame's draw at the same position when it comes from a retained block that is unchanged since last frame (the
///   block array is the key: a block reused with every input bit-identical has the bytes it had when it was last written), with the same
///   texture handle, blend, sdf flag, vertex and index counts and clip. While every draw so far matches, nothing is queued.
/// - At the first draw that does not match, last frame's vertices, indices and batches up to that position are taken as this frame's (they
///   are the bytes and batches those same draws made -- one array copy instead of a draw-by-draw rebuild), and the rest of the pass runs
///   exactly as before (each draw recorded).
/// - Anything the recording cannot follow STOPS it for the pass (K12Break): a texture or target made / removed during the pass, a flush of
///   queued draws inside the pass (its offsets end there), a backdrop (grab) draw (it flushes, and its batch needs Flush's backdrop state),
///   an owner-local UI draw. The draws so far are queued first (in order), the rest of the pass runs as before, and NOTHING is kept: the next
///   pass records from scratch.
/// - At the end: every draw matched and the count is last frame's (no texture made or removed since the recording, the same target) ->
///   last frame's batches are issued again from the vertex / index buffer pair they were uploaded into (no vertex copy, no upload; the same
///   GL state calls and glDrawElements in the same order as a flush of those batches). Otherwise (not stopped) the pass is flushed into the
///   other buffer of the pair and kept (the live arrays swap with the kept ones: no copy) for the next frame.
/// The GPU sees the same vertex bytes and the same draws in the same order; only uploads (and the CPU's per-draw work) are left out.
/// Kept memory (switch on only): a second batcher array set (the live set's sizes: Vertex[16384] + ushort[49152] + Batch[256] to start, ~0.6 MiB
/// on the Vita, more if the live set grew), one GL vertex / index buffer pair holding a pass each, and the last recording's keys (the blocks
/// of last frame's draws; nothing older: a recording's keys are cleared when it is replaced or stopped).
/// SHEPHERD_K12_UICHECK=1: matched draws are also built as before; on a replayed frame the built vertices / indices / batches are compared with
/// the kept ones, and at every prefix take the built prefix (counts, chunk base, vertex bytes, indices, batches) is compared with the kept
/// prefix the take would copy (a difference is logged and counted). Negative controls (test only): SHEPHERD_K12_NEGCTL=replay writes a
/// wrong colour (K12Ui.NegR) into the kept vertex 0 of every replayed frame (re-uploaded), =prefix into the kept vertex 0 at every prefix
/// take before it is copied or compared -- the replay / prefix check and the picture check must fail.</summary>
public sealed unsafe partial class GlBackend
{
    struct K12Call { public object Key; public uint TexH, Blend; public bool Sdf, ClipOn; public int Nv, Ni, Cx, Cy, Cw, Ch, VOff, IOff, BOff, LastNi, Chunk; }
    K12Call[] _kLast, _kCur; int _kLastN, _kCurN, _kCurHi;   // (made on the first UI pass with the switch on: nothing with it off)
    bool _kLastOk, _kOn, _kMatching, _kBroken;
    /// <summary>Bumped whenever the texture set changes (a texture or target made or removed): the kept batches hold texture records.</summary>
    int _kEpoch;
    int _kEpochStart, _kLastEpoch;
    Tex _kLastTarget;
    Vertex[] _kV; ushort[] _kI; Batch[] _kB; int _kNv, _kNi, _kNb, _kChunk;   // (made at the first keep, the live arrays' sizes)
    uint[] _kVbo, _kIbo; int _kBuf = -1;   // (made at the first keep)

    public void K12UiBegin()
    {
        if (_nb > 0 || _npb > 0) Flush();   // (the UI pass's batches are a flush of their own)
        _kLast ??= new K12Call[64]; _kCur ??= new K12Call[64];
        _kOn = true; _kCurN = 0; _kBroken = false; _kEpochStart = _kEpoch;
        _kMatching = _kLastOk && _kBuf >= 0 && _kLastEpoch == _kEpoch;
        K12Ui.LastReplay = 0;
    }

    void K12Record(object key, uint texH, uint blend, bool sdf, int nv, int ni, in ViewState vs)
    {
        if (_kCurN == _kCur.Length) Array.Resize(ref _kCur, _kCur.Length * 2);
        ref var c = ref _kCur[_kCurN++];
        c.Key = key; c.TexH = texH; c.Blend = blend; c.Sdf = sdf; c.Nv = nv; c.Ni = ni;
        c.ClipOn = vs.On; c.Cx = vs.X; c.Cy = vs.Y; c.Cw = vs.W; c.Ch = vs.H;
        c.VOff = _nv; c.IOff = _ni; c.BOff = _nb; c.LastNi = _nb > 0 ? _b[_nb - 1].Ni : 0; c.Chunk = _chunkBase;
    }
    readonly struct ViewState { public readonly bool On; public readonly int X, Y, W, H; public ViewState(Tex v) { On = v.ClipOn; X = v.Cx; Y = v.Cy; W = v.Cw; H = v.Ch; } }

    public void K12Draw(object key, bool unchanged, IntPtr texH, uint blend, bool sdf, Span<Vertex> v, Span<int> idx)
    {
        if (!_kOn || _kBroken) { DrawCore(texH, blend, sdf, v, idx, default); return; }   // (stopped: the rest of the pass runs as before)
        if (_kEpoch != _kEpochStart || ShaderPolicy.IsGrab(blend))
        {   // a texture / target made or removed during the pass, or a backdrop draw (its flush and its batch's backdrop state): stop
            K12Break(); DrawCore(texH, blend, sdf, v, idx, default); return;
        }
        var vs = new ViewState(_view);
        int k = _kCurN;
        if (_kMatching)
        {
            if (unchanged && key != null && k < _kLastN)
            {
                ref var l = ref _kLast[k];
                if (ReferenceEquals(l.Key, key) && l.TexH == (uint)texH && l.Blend == blend && l.Sdf == sdf && l.Nv == v.Length && l.Ni == idx.Length
                    && l.ClipOn == vs.On && (!vs.On || (l.Cx == vs.X && l.Cy == vs.Y && l.Cw == vs.W && l.Ch == vs.H)))
                {
                    if (_kCurN == _kCur.Length) Array.Resize(ref _kCur, _kCur.Length * 2);
                    _kCur[_kCurN++] = l;   // (the batcher's state before it = last frame's: the same draws so far)
                    if (!K12Ui.Check) { _cur.DrawsIn++; _cur.Tris += idx.Length / 3; return; }
                    DrawCore(texH, blend, sdf, v, idx, default);   // (check mode: built as before too; compared at the take / the end)
                    return;
                }
            }
            K12Take(k);
        }
        K12Record(key, (uint)texH, blend, sdf, v.Length, idx.Length, vs);
        DrawCore(texH, blend, sdf, v, idx, default);
    }

    /// <summary>Last frame's batcher state before its draw k (k = its draw count: the end of its pass) taken as this frame's -- this frame's draws
    /// 0..k-1 are last frame's. Check mode built them already: the built prefix is compared with the kept one instead of copied.</summary>
    void K12Take(int k)
    {
        _kMatching = false;   // (first: the target bind below may flush, and Flush takes the recording while matching)
        if (k == 0) return;
        K12Ui.ReplayDiffer++; K12Ui.ReplayPrefixDraws += k;   // (incremental: the first k draws were last frame's)
        int vo, io, bo, ln, ch;
        if (k < _kLastN) { ref var l = ref _kLast[k]; vo = l.VOff; io = l.IOff; bo = l.BOff; ln = l.LastNi; ch = l.Chunk; }
        else { vo = _kNv; io = _kNi; bo = _kNb; ln = _kNb > 0 ? _kB[_kNb - 1].Ni : 0; ch = _kChunk; }
        if (K12Ui.NegPrefix && vo > 0) _kV[0].color.r = K12Ui.NegR;   // (the negative control of the prefix check: a wrong kept colour)
        if (K12Ui.Check) { K12ComparePrefix(k, vo, io, bo, ln, ch); return; }
        if (_target == null) BindTarget(_main[_final]);   // (what DrawCore's first draw does; the batcher is empty, so its flush does nothing)
        EnsureCapacity(vo, io);
        Array.Copy(_kV, 0, _v, 0, vo); Array.Copy(_kI, 0, _i, 0, io);
        if (_b.Length < bo) Array.Resize(ref _b, Math.Max(bo, _b.Length * 2));
        Array.Copy(_kB, 0, _b, 0, bo); if (bo > 0) _b[bo - 1].Ni = ln;
        for (int i = 0; i < bo; i++) _b[i].Tex.UseGen = _gen;
        _nv = vo; _ni = io; _nb = bo; _chunkBase = ch;
    }

    public void K12Break()
    {
        if (!_kOn) return;
        if (_kMatching) K12Take(_kCurN);
        _kBroken = true;
    }

    public void K12UiEnd()
    {
        if (!_kOn) return;
        int hi = Math.Max(_kCurHi, _kCurN);   // (_kCur's entries that may hold a key)
        bool replay = _kMatching && !_kBroken && _kCurN == _kLastN && _kEpoch == _kEpochStart && (_target ?? _main[_final]) == _kLastTarget;
        if (replay)
        {
            _kOn = false; _kMatching = false; _kCurHi = hi;   // (_kCur holds last frame's keys: nothing more is kept alive)
            if (K12Ui.Check) K12Compare();
            _nb = 0; _nv = _ni = _chunkBase = 0;   // (check mode's own build is not drawn)
            K12Replay();
            return;
        }
        if (_kMatching) K12Take(_kCurN);   // every draw matched, but last frame had more: its state before draw _kCurN is this pass's
        _kOn = false;
        if (_kBroken)
        {   // stopped: nothing is kept, and neither recording keeps a block alive past this pass (only this pass's own keys, until the next)
            K12Keys(_kLast, 0, _kLastN); _kLastN = 0; K12Keys(_kCur, _kCurN, hi); _kCurHi = _kCurN;
            _kLastOk = false; Flush(); return;
        }
        K12Keys(_kCur, _kCurN, hi); _kCurHi = _kCurN;   // (keys of an earlier, longer pass)
        K12KeepFlush();
    }
    /// <summary>Clears a[from..to)'s keys (the block arrays a recording holds: a replaced recording must not keep dropped blocks alive).</summary>
    static void K12Keys(K12Call[] a, int from, int to) { for (int i = from; i < to; i++) a[i].Key = null; }

    /// <summary>Flush's work for the UI pass's batches, into the kept buffer pair; the arrays and the recording are kept for the next frame.</summary>
    void K12KeepFlush()
    {
        if (_npb > 0) ProjFlush();
        if (_kVbo == null) { _kVbo = new uint[2]; _kIbo = new uint[2]; fixed (uint* pv = _kVbo) Gl.GenBuffers(2, pv); fixed (uint* pi = _kIbo) Gl.GenBuffers(2, pi); }
        int j = _kBuf < 0 ? 0 : _kBuf ^ 1;
        if (_nb > 0)
        {
            _cur.Flushes++; _cur.VertexBytes += (long)_nv * Stride + _ni * 2L;
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _kVbo[j]);
            fixed (Vertex* pv = _v) Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_nv * Stride, pv, Gl.STREAM_DRAW);
            Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _kIbo[j]);
            fixed (ushort* pi = _i) Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, (nint)_ni * 2, pi, Gl.STREAM_DRAW);
            K12Issue(_kVbo[j], _b, _nb);
            _cur.DrawsOut += _nb; _cur.Batches += _nb;
        }
        _kLastTarget = _target ?? _main[_final];
        _kV ??= new Vertex[_v.Length]; _kI ??= new ushort[_i.Length]; _kB ??= new Batch[_b.Length];
        (_v, _kV) = (_kV, _v); (_i, _kI) = (_kI, _i); (_b, _kB) = (_kB, _b);
        _kNv = _nv; _kNi = _ni; _kNb = _nb; _kChunk = _chunkBase;
        int oldN = _kLastN;
        (_kLast, _kCur) = (_kCur, _kLast); _kLastN = _kCurN; _kCurN = 0;
        K12Keys(_kCur, 0, oldN); _kCurHi = 0;   // (the replaced recording: its keys would keep dropped blocks alive)
        _kBuf = j; _kLastEpoch = _kEpoch; _kLastOk = true;
        _vaValid = 0; _vaUsed = false;
        _nb = 0; _nv = _ni = _chunkBase = 0; _gen++;
        K12Ui.ReplayRecorded++;
    }

    /// <summary>Last frame's UI batches again, from the buffer pair they were uploaded into.</summary>
    void K12Replay()
    {
        if (K12Ui.NegReplay && _kNv > 0)
        {   // the negative control: a wrong colour in the kept vertex 0, uploaded where the replay reads it (a constant: the same wrong bytes
            // on every replay, never the original's)
            _kV[0].color.r = K12Ui.NegR;
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _kVbo[_kBuf]);
            fixed (Vertex* pv = _kV) Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_kNv * Stride, pv, Gl.STREAM_DRAW);
        }
        if (_kNb > 0)
        {
            if (_target == null) BindTarget(_main[_final]);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _kVbo[_kBuf]);
            Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _kIbo[_kBuf]);
            for (int i = 0; i < _kNb; i++) _kB[i].Tex.UseGen = _gen;
            K12Issue(_kVbo[_kBuf], _kB, _kNb);
            _cur.DrawsOut += _kNb; _cur.Batches += _kNb;
        }
        _gen++;
        K12Ui.ReplayFrames++; K12Ui.ReplayDraws += _kLastN; K12Ui.ReplayVerts += _kNv; K12Ui.LastReplay = 1;
    }

    /// <summary>Flush's batch loop (the same state calls and draws, in the same order) over batches whose vertices / indices are in the bound
    /// buffer pair (vb bound as ARRAY_BUFFER). UI batches carry no added-colour stream (K12Draw queues plain draws only); a backdrop batch never
    /// reaches here (a grab draw stops the recording), its state block is Flush's all the same.</summary>
    void K12Issue(uint vb, Batch[] bs, int nb)
    {
        _attrBase = int.MinValue; _addBase = int.MinValue;
        for (int k = 0; k < nb; k++)
        {
            ref var b = ref bs[k];
            if (b.Tex.FrameSeen != _frame) { b.Tex.FrameSeen = _frame; _cur.Textures++; }
            UseProg(_byId[b.Prog]);
            BindTex0(b.Tex);
            int wrap = b.Wrap ? 1 : 0;
            if (b.Tex.Wrap != wrap) { int m = b.Wrap ? Gl.REPEAT : Gl.CLAMP_TO_EDGE; Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, m); Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, m); b.Tex.Wrap = wrap; }
            SetBlend(b.Blend);
            SetClip(b.ClipOn, b.Cx, b.Cy, b.Cw, b.Ch);
            if (b.Prog == PGrab && _grabTex != null)
            {   // (Flush's: the backdrop copied for this draw on unit 1, and its mode)
                Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, _grabTex.Id); Gl.ActiveTexture(Gl.TEXTURE0);
                U4(_grab, _grab.UMode, ShaderPolicy.GrabMode(b.Blend), ShaderPolicy.GrabAdds(b.Blend) ? 1f : 0f, 0f, 0f);
            }
            if (b.Base != _attrBase) Attribs(b.Base);
            if (_addOn && _addBase != int.MinValue) { Gl.VertexAttribPointer(3, 4, Gl.FLOAT, 0, Stride, (void*)((nint)b.Base * Stride + 8)); _addBase = int.MinValue; }
            Gl.DrawElements(Gl.TRIANGLES, b.Ni, Gl.UNSIGNED_SHORT, (void*)((nint)b.I0 * 2));
        }
    }

    /// <summary>Every field of two batches the draws read; y's index count taken as yNi (a prefix's last batch is cut to the take's count).</summary>
    static bool K12SameBatch(in Batch x, in Batch y, int yNi) =>
        x.Tex == y.Tex && x.Blend == y.Blend && x.Prog == y.Prog && x.Wrap == y.Wrap && x.ClipOn == y.ClipOn && x.Base == y.Base && x.I0 == y.I0 && x.Ni == yNi
        && (!x.ClipOn || (x.Cx == y.Cx && x.Cy == y.Cy && x.Cw == y.Cw && x.Ch == y.Ch));

    /// <summary>Check mode, a replayed frame: this frame's own build (the live arrays) against the kept ones.</summary>
    void K12Compare()
    {
        K12Ui.ReplayChecked++;
        string why = null;
        if (_nv != _kNv || _ni != _kNi || _nb != _kNb) why = $"counts {_nv}/{_ni}/{_nb} vs kept {_kNv}/{_kNi}/{_kNb}";
        else
        {
            fixed (Vertex* a = _v, b = _kV) if (_nv > 0 && !K12Ui.SameWords((uint*)a, (uint*)b, _nv * 8)) why = "vertex bytes";
            if (why == null) for (int i = 0; i < _ni; i++) if (_i[i] != _kI[i]) { why = $"index {i}"; break; }
            if (why == null) for (int i = 0; i < _nb; i++) if (!K12SameBatch(in _b[i], in _kB[i], _kB[i].Ni)) { why = $"batch {i}"; break; }
        }
        if (why != null) { K12Ui.ReplayCheckDiff++; if (K12Ui.ReplayCheckDiff <= 20) Log.Warn($"[k12ui] replay check: frame {_frame}: the replayed UI batches differ from this frame's own build ({why})"); }
    }

    /// <summary>Check mode, a prefix take (this frame's draws 0..k-1 are last frame's): this frame's own build of them (the live arrays) against
    /// what the take copies -- the kept counts, chunk base, vertex bytes, indices and batches before last frame's draw k (its last batch cut to
    /// the recorded index count).</summary>
    void K12ComparePrefix(int k, int vo, int io, int bo, int ln, int ch)
    {
        K12Ui.PrefixChecked++;
        int why = 0, at = 0;   // (K12Ui.PrefixDiff names the codes)
        if (_nv != vo || _ni != io || _nb != bo || _chunkBase != ch) why = 1;
        else
        {
            fixed (Vertex* a = _v, b = _kV) if (vo > 0 && !K12Ui.SameWords((uint*)a, (uint*)b, vo * 8)) why = 2;
            if (why == 0) for (int i = 0; i < io; i++) if (_i[i] != _kI[i]) { why = 3; at = i; break; }
            if (why == 0) for (int i = 0; i < bo; i++) if (!K12SameBatch(in _b[i], in _kB[i], i == bo - 1 ? ln : _kB[i].Ni)) { why = 4; at = i; break; }
        }
        if (why != 0) K12Ui.PrefixDiff(_frame, k, why, at, _nv, _ni, _nb, _chunkBase, vo, io, bo, ch);
    }
}
