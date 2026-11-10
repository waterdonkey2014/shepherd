namespace Shepherd.Engine.Render;

// Kit #12 boot track (Render/K12Stencil.cs, SHEPHERD_K12_STENCIL=1): GlBackend's stencil buffer for uGUI's Masks. Nothing here runs, and nothing
// is allocated, until the UI pass hands over its first stencil state (the switch off: never) -- every kit #11 GL record is unchanged.
//  - the stencil: ONE renderbuffer (D24S8, the main targets' size, ~2 MiB of video memory on the Vita) made on first use and attached to the main
//    target the UI pass draws into (both main targets share it: only one is drawn into at a time), as GL_DEPTH_STENCIL_ATTACHMENT (GLES2 + the
//    packed-depth-stencil extension: vitaGL takes it); a driver that refuses that name (the Mac's legacy 2.1 context) gets it on the depth and
//    the stencil attachments. The depth test stays off. A target that is not a main target (a camera's render texture) gets no stencil: its
//    masked draws are drawn unmasked, one line says so.
//  - cleared once a frame, before the frame's first masked draw (scissor off, write mask 0xFF): Unity's camera clear; uGUI's unmask draws bring
//    every pixel back to 0 after each Mask, so one clear a frame is Unity's state at every Mask.
//  - a state = glStencilFunc (Unity's CompareFunction), glStencilOp (KEEP, KEEP, the pass operation), glStencilMask, glColorMask; a state with
//    alpha clip draws sprite batches with k12clip.frag (sprite.frag's plain path + uGUI's alpha clip), built on first use.
public sealed unsafe partial class GlBackend
{
    K12StencilState _st = K12StencilState.None;
    bool _stOn, _stClip, _stFailed, _clipTried, _stWarned;
    uint _stRb, _stFb0, _stFb1;
    int _stCleared = -1, _stDraws;
    Prog _clipProg;
    // kit #13 speed (K13Speed.MaskDetach, SHEPHERD_K13_MASKDETACH=1, opt-in): the stencil buffer attached only while masks are drawn
    int _stUsed = -1; bool _stPair;
    /// <summary>Kit #13 speed: re-attachments after a detach, detaches (the [k13] line).</summary>
    internal static long K13Reattach, K13Detach;

    public void Stencil(in K12StencilState s)
    {
        if (s.Equals(_st)) return;
        Flush();   // (the batches queued so far keep the state they were queued under)
        if (_target == null) BindTarget(_main[_final]);
        bool test = !s.TestOff;
        if (test && !StencilTarget(_target)) test = false;   // (no stencil on this target: drawn unmasked, one line)
        if (test)
        {
            if (!_stOn) Gl.Enable(Gl.STENCIL_TEST);
            Gl.StencilFunc(Cmp(s.Comp), s.Ref, s.ReadMask);
            Gl.StencilOp(Gl.KEEP, Gl.KEEP, Op(s.Op));
            Gl.StencilMask(s.WriteMask);
            _stDraws++;
        }
        else if (_stOn) Gl.Disable(Gl.STENCIL_TEST);
        if (s.ColorMask != _st.ColorMask) Gl.ColorMask((byte)((s.ColorMask >> 3) & 1), (byte)((s.ColorMask >> 2) & 1), (byte)((s.ColorMask >> 1) & 1), (byte)(s.ColorMask & 1));
        _stOn = test;
        _stClip = s.Clip && ClipProgram();
        _st = s;
    }
    /// <summary>Begin: a state left on goes off (the UI pass always ends with it off: a safety net).</summary>
    void StencilReset()
    {
        if (_stOn) Gl.Disable(Gl.STENCIL_TEST);
        if (_st.ColorMask != 15) Gl.ColorMask(1, 1, 1, 1);
        _stOn = false; _stClip = false; _st = K12StencilState.None;
    }
    static uint Cmp(byte c) => c switch { 1 => Gl.NEVER, 2 => Gl.LESS, 3 => Gl.EQUAL, 4 => Gl.LEQUAL, 5 => Gl.GREATER, 6 => Gl.NOTEQUAL, 7 => Gl.GEQUAL, _ => Gl.ALWAYS };
    static uint Op(byte o) => o switch { 1 => Gl.ZERO, 2 => Gl.REPLACE, 3 => Gl.INCR, 4 => Gl.DECR, 5 => Gl.INVERT, 6 => Gl.INCR_WRAP, 7 => Gl.DECR_WRAP, _ => Gl.KEEP };

    /// <summary>Kit #13 speed (SHEPHERD_K13_MASKDETACH=1, opt-in; Render/K13Speed.cs): the frame's start (Begin, before anything is drawn) --
    /// after <see cref="K13Speed.MaskIdleFrames"/> frames without a masked draw the stencil buffer leaves the main targets; the next masked
    /// draw attaches it again (and clears it before that draw, as every frame's first masked draw does). The stencil is read only by masked
    /// draws and the depth test is never on, so every pixel is the same. On the Vita a detached target does NOT draw without a depth-stencil
    /// surface: vitaGL gives it a hidden one of the same size and format at its next scene and frees that one at the next attach (K13Speed's
    /// MaskDetach comment) -- so the switch saves no GPU work there and costs ~2 MiB while detached; it stays opt-in.</summary>
    void K13StencilDetachIdle()
    {
        if ((_stFb0 == 0 && _stFb1 == 0) || _frame - _stUsed <= K13Speed.MaskIdleFrames) return;
        for (int i = 0; i < 2; i++)
        {
            var t = _main[i]; if (t == null || (t.Fbo != _stFb0 && t.Fbo != _stFb1)) continue;
            BindTarget(t);
            if (_stPair) { Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, 0); Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.STENCIL_ATTACHMENT, Gl.RENDERBUFFER, 0); }
            else Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.RENDERBUFFER, 0);
        }
        _stFb0 = _stFb1 = 0; _stCleared = -1; K13Detach++;
    }
    /// <summary>The bound target has the stencil buffer (made / attached on first use) and it was cleared this frame.</summary>
    bool StencilTarget(Tex t)
    {
        if (_stFailed) return false;
        if (K13Speed.MaskDetach) _stUsed = _frame;
        if (t != _main[0] && t != _main[1])
        {
            if (!_stWarned) { _stWarned = true; Log.Info($"[k12] stencil masks: a masked UI draw into a {t.W}x{t.H} render target (not the main target): drawn unmasked (one line a run)"); }
            return false;
        }
        if (_stRb == 0)
        {
            uint rb; Gl.GenRenderbuffers(1, &rb); _stRb = rb;
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, rb);
            Gl.RenderbufferStorage(Gl.RENDERBUFFER, Gl.DEPTH24_STENCIL8, t.W, t.H);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, 0);
        }
        if (t.Fbo != _stFb0 && t.Fbo != _stFb1)
        {
            Gl.GetError();   // (drain: the attachment below is judged by its own error)
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.RENDERBUFFER, _stRb);
            string how = "depth-stencil attachment";
            if (Gl.GetError() != Gl.NO_ERROR)
            {   // (a GL 2.1 context without GL_DEPTH_STENCIL_ATTACHMENT: the packed buffer on both attachments)
                Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, _stRb);
                Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.STENCIL_ATTACHMENT, Gl.RENDERBUFFER, _stRb);
                how = "depth + stencil attachments"; _stPair = true;
            }
            var st = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            if (st != Gl.FRAMEBUFFER_COMPLETE || Gl.GetError() != Gl.NO_ERROR)
            {
                Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, 0);
                Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.STENCIL_ATTACHMENT, Gl.RENDERBUFFER, 0);
                _stFailed = true;
                Log.Warn($"[k12] FALLBACK: the main target with a stencil buffer is incomplete (0x{st:X4}) -- uGUI Masks are drawn unmasked for this run");
                return false;
            }
            if (_stFb0 == 0) _stFb0 = t.Fbo; else _stFb1 = t.Fbo;
            if (K13Detach == 0) Log.Info($"[k12] stencil masks: a {t.W}x{t.H} D24S8 renderbuffer on main target fbo {t.Fbo} ({how})"); else K13Reattach++;   // (kit #13 speed: a re-attachment after a detach is counted, not logged)
        }
        if (_stCleared != _frame)
        {
            if (_scissorOn) { Gl.Disable(Gl.SCISSOR_TEST); _scissorOn = false; }
            Gl.StencilMask(0xFF);
            Gl.Clear(Gl.STENCIL_BUFFER_BIT);
            _stCleared = _frame; _clipDirty = true;
        }
        return true;
    }
    /// <summary>The alpha-clip copy of the sprite program (k12clip.frag), built once; false = unavailable (the clip is then not applied: one line).</summary>
    bool ClipProgram()
    {
        if (_clipProg != null) return true;
        if (_clipTried) return false;
        _clipTried = true;
        try
        {
            _clipProg = Build("k12-clip", "sprite.vert", "k12clip.frag", null, FragPrecision, null);
            Log.Info($"[k12] stencil masks: the alpha-clip program compiled+linked on first use in {_clipProg.CompileMs:F1} ms (not in the boot set or the gxp cache)");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"[k12] FALLBACK: the shader compiler rejected the alpha-clip program ({e.Message.Split('\n')[0]}) -- Mask graphics write the stencil over their whole quad");
            return false;
        }
    }
}
