namespace Shepherd.Engine.Render;

/// <summary>Kit #13 track speed (vita/notes/kit13-speed.md): the switches and counters of the speed rows. Every switch is read once, from the
/// app's env file. SHEPHERD_K13_SPEED=0 turns every one of them off at once (kit #12, call for call, whatever the other lines say).
/// The literals live in a [ProfileOnly] type, so the V3 diet's readable-key scan (Assets/Diet.cs) is unchanged.
/// - SHEPHERD_K13_RECTDIRTY (default ON; =0 = kit #12): dirty-only rect size checks (Unity's rule: OnRectTransformDimensionsChange is sent
///   when a rect's SIZE changes). The engine's NotifyRectChanges compared the size of every rect transform whose world stamp had moved --
///   any position, rotation or scale write anywhere above it queued the whole subtree (a follower walking queues its ~200 transforms), and
///   past 960 queued transforms the walk visited every transform of the registry each frame (99 followers at the base: 5.5 ms a frame on
///   the Mac, MEASURED-M). A rect's size is SizeDelta + (AnchorMax - AnchorMin) x the parent RECT's size (NTransform.SizeF): a function of
///   its own size delta / anchors / IsRect, its parent link and the same inputs of its rect ancestors -- never of a position, rotation,
///   scale, anchored position or pivot (a parent's pivot moves its rect's x / y, not its width / height). With the switch those five
///   writes no longer queue the transform (they still bump its version and stamps: every matrix and rect memo is unchanged), so the queued
///   walk visits only the transforms whose size can have changed. Exact: a transform left out has a size bit-identical to the one it had at
///   its last comparison, so the comparison would have made no change and sent no message. SHEPHERD_HOTSPOT_CHECK=1 checks it every frame
///   (every rect whose size differs from its last compared size, or that was never compared, must be in the walk's set);
///   SHEPHERD_K13_RECTDIRTY=negctl (test only) also drops size-delta writes from the queue: the check must then report mismatches.</summary>
[ProfileOnly]
public static class K13Speed
{
    static string E(string k) => Environment.GetEnvironmentVariable(k);
    /// <summary>SHEPHERD_K13_SPEED=0: every switch of this track off (kit #12).</summary>
    public static readonly bool Master = E("SHEPHERD_K13_SPEED") != "0";
    public static readonly bool RectDirty = Master && E("SHEPHERD_K13_RECTDIRTY") != "0";
    /// <summary>SHEPHERD_K13_MASKS (default ON; =0 = kit #12): the stencil masks' CPU work only where masks are -- the UI pass skips the pop
    /// (unmask) lookups under every node while no renderer holds a pop material (no Mask enabled anywhere). The same draws and GL calls. A
    /// renderer keeps its entry while its Mask is enabled even when it draws nothing (culled, empty mesh), so whether a screen skips is
    /// measured, not assumed: the UI passes that skipped / looked are counted (the [k13] line) and each change of state is logged with its
    /// frame (SceneRenderer.K13PopsNote).</summary>
    public static readonly bool Masks = Master && E("SHEPHERD_K13_MASKS") != "0";
    /// <summary>SHEPHERD_K13_MASKDETACH=1 (OPT-IN, default OFF = kit #12: the stencil buffer stays attached from its first use): the stencil
    /// buffer leaves the main targets after <see cref="MaskIdleFrames"/> frames without a masked draw and comes back with the next masked
    /// draw (cleared before it, as every frame's first masked draw is). Every pixel the same; the GL call stream changes (a detach /
    /// re-attach where the screen changes). OFF by default because it saves nothing on the Vita: vitaGL (the linked libvitaGL.a) gives a
    /// framebuffer with no depth attachment a hidden depth-stencil buffer of its own at its next scene (align32(w) x align32(h) x 4 B, 2,088,960 B
    /// at 960x544, the same surface format as our D24S8 renderbuffer) and puts that hidden buffer on its purge list at the next attach. So a
    /// detached target still draws with a depth-stencil surface (kit #11's targets carried the hidden one), holds ~2 MiB more while
    /// detached (our renderbuffer stays allocated), and every masked screen boundary frees and re-allocates ~2 MiB of GPU memory.</summary>
    public static readonly bool MaskDetach = Master && E("SHEPHERD_K13_MASKDETACH") == "1";
    /// <summary>Frames without a masked draw before the stencil buffer leaves (SHEPHERD_K13_MASKIDLE, default 30: a screen that masks only now
    /// and then does not attach / detach every frame).</summary>
    public static readonly int MaskIdleFrames = int.TryParse(E("SHEPHERD_K13_MASKIDLE"), out var mi) && mi >= 1 ? mi : 30;
    /// <summary>SHEPHERD_K13_PACING=on|off (A/B arms only; unset = the saved Settings row, kit #12): the Frame Pacing row's choice forced for this
    /// start without writing the saved settings, so a scripted env-file pair can compare the menu's frame times with pacing on and off (the
    /// owner presses only the bubble). 1 = on, -1 = off, 0 = the saved row.</summary>
    public static readonly int PacingAb = !Master ? 0 : E("SHEPHERD_K13_PACING") switch { "on" or "1" => 1, "off" or "0" => -1, _ => 0 };
    /// <summary>Test only: the rect check's negative control (size-delta writes not queued either).</summary>
    public static readonly bool RectNegCtl = RectDirty && E("SHEPHERD_K13_RECTDIRTY") == "negctl";

    /// <summary>The end-of-run line (one line; null with every switch off).</summary>
    public static string Summary() => !Master ? null :
        $"[k13] speed: switches rectdirty={B(RectDirty)}{(RectNegCtl ? " NEGATIVE CONTROL" : "")} masks={B(Masks)} maskdetach={B(MaskDetach)}/{MaskIdleFrames} (SHEPHERD_K13_SPEED=0 = kit #12); rect size walks {SceneRenderer.NrcQueuedWalks} queued " +
        $"({SceneRenderer.NrcCollected} transforms visited, {SceneRenderer.NrcRemainders} finished by the full loop after game code ran), {SceneRenderer.NrcFullWalks} full; " +
        $"pose writes not queued {Native.NTransform.PoseWrites}; pop lookups skipped at {PopSkips} of {PopSkips + PopLooks} UI node visits (at the end: {K12Stencil.PopCensus()}); " +
        $"stencil buffer detached {GlBackend.K13Detach}x, attached again {GlBackend.K13Reattach}x";
    static int B(bool b) => b ? 1 : 0;

    /// <summary>Masks on: the UI pass's node visits whose pop lookups were skipped / done (SceneRenderer.K12PopDraws; the [k13] line).</summary>
    internal static long PopSkips, PopLooks;
    static int _popState = -1, _popLines;
    /// <summary>Masks on, the end of each UI pass: one line each time "some renderer holds a pop material" changes (the first 32 changes), with
    /// the engine frame and what the held entries are -- where the MASKS row skips, measured (frames as K7Prof's win=, not re-based).</summary>
    internal static void PopsNote()
    {
        int st = K12Stencil.AnyPops ? 1 : 0;
        if (st == _popState) return;
        _popState = st;
        if (++_popLines > 32) return;
        Log.Info(st == 1 ? $"[k13] masks: from frame {Runtime.Time.FrameCount} the pop lookups run: {K12Stencil.PopCensus()}"
                         : $"[k13] masks: from frame {Runtime.Time.FrameCount} the pop lookups are skipped (no renderer holds a pop material)");
    }
}
