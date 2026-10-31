using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Diag;

/// <summary>V6 Stage B (vita/notes/v6-vita-build.md): the bridge to the PS Vita host (vita/shepherd/host.c), which keeps the
/// durable log ux0:data/shepherd/menu.txt. On only when the host sets SHEPHERD_VITA_HOST=1; everywhere else every entry point
/// returns at once and the host export is never bound (the Mono interpreter binds a method's P/Invokes when it transforms the
/// method, so the call lives in a separate never-inlined method that only runs under the host).
/// The engine reports at frame 0 (boot done, before the first frame), at every SHEPHERD_CHECKPOINTS frame (default
/// 0,500,1050,1300), a heartbeat every SHEPHERD_PROGRESS_EVERY frames (default 50) and at the end. Each call carries the
/// managed numbers (GC heap in use, main-thread allocation, minor/major GC counts, live engine objects) and a note (scenes,
/// errors/exceptions so far, the menu selection); the host adds free RAM, the arena, the newlib heap, Mono's counters, GC
/// pauses and wall time. With SHEPHERD_SIMSTATS=...:1300 the 1300 checkpoint comes right after SimStats' forced full GC, so
/// its heap number is the live heap.
/// Stage B (the GL app, vita/notes/m1-gl-app.md): heartbeats also carry the GL numbers (render ms, present ms, draws, resident
/// textures, frames shown, vitaGL's pools), and with SHEPHERD_MENU_WATCH=1 the EventSystem's selection is watched every frame: a pad
/// press (script or the real controls) followed, within 300 frames, by a different selected button is reported as kind 3 --
/// the host's "menu is interactive" PASS line (first 20 such events).</summary>
[ProfileOnly]
public static class VitaHost
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_VITA_HOST") == "1";
    static readonly HashSet<int> Checkpoints = (Environment.GetEnvironmentVariable("SHEPHERD_CHECKPOINTS") ?? "0,500,1050,1300")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var f) ? f : -1).Where(f => f >= 0).ToHashSet();
    static readonly int Every = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_PROGRESS_EVERY"), out var e) && e > 0 ? e : 50;

    /// <summary>Kinds understood by the host.</summary>
    const int Progress = 0, Checkpoint = 1, End = 2, Interactive = 3, RenderAlive = 4, FailLine = 5, K7Kind = 6, K9Kind = 8;
    static readonly bool Watch = On && Environment.GetEnvironmentVariable("SHEPHERD_MENU_WATCH") == "1";
    static string _lastSel, _pendSel;
    static int _seenPress = -1, _seenTouch = -1, _pendFrame = -1, _events;
    static Impl.VitaPad.B _pendBtn;
    static bool _pendTouch;
    static long _lastPresents, _lastPresentTicks;

    /// <summary>Before the first frame (Boot): the frame-0 checkpoint.</summary>
    public static void Start() { if (On && Checkpoints.Contains(0)) Send(0, Checkpoint); }
    /// <summary>After every frame (Boot loop).</summary>
    public static void Frame(int frame)
    {
        if (!On) return;
        if (Watch)
        {
            if (LazyNow) { var ev = LazyAfter(frame); if (ev != null) SendNote(frame, _lzEvTitle ? TitleResponse : Interactive, ev); }   // kit #8: the lazy watch (the same M1 lines)
            else
            {
                _oldEvent = null; WatchMenu(frame);
                if (CheckNow) MenuWatchCheck.Compare(frame, _oldEvent, LazyAfter(frame));   // kit #8: SHEPHERD_K8_MENU_WATCH=check
            }
        }
        if (frame == GcGovProbe.Frame) GcGovProbe.Run();
        if (Runtime.K9Gc.UsedProbe) Runtime.K9Gc.ProbeFrame();   // kit #9 gc: the used-size probe (test only)
        if (Checkpoints.Contains(frame)) Send(frame, Checkpoint);
        else if (frame % Every == 0) Send(frame, Progress);
    }
    /// <summary>After the last frame (Boot): the end checkpoint, whose note carries the selected menu button.</summary>
    public static void Finish(int lastFrame) { if (On) { if (Runtime.K9Gc.UsedProbe) K7Line(Runtime.K9Gc.ProbeLine()); Send(lastFrame, End); } }
    /// <summary>kit #3 review (Render/MtRender.cs, MtBackend.Dispose): the multicore pipeline's render thread did not stop at the end -- the
    /// host then makes no vitaGL call after Main (kind 4).</summary>
    public static void RenderThreadAlive(string why) { if (On) SendNote(Runtime.Time.FrameCount, RenderAlive, why); }
    /// <summary>Kit #6 gpulight review: one host line "FAIL &lt;what&gt;" (kind 5). The host counts it with its own FAIL lines, so the session
    /// ends FAIL END (red screen) -- for a condition the run survives but must not pass with (Render/DerivedRender.cs: a Derived DLL on the
    /// card that this engine does not load). The managed numbers are not sent (any thread may call it).</summary>
    public static void Fail(string what) { if (On) Native(Runtime.Time.FrameCount, FailLine, what, 0, 0, 0, 0, 0); }
    /// <summary>Kit #7 (Diag/K7.cs): one line for the host log, as it is (kind 6: the BOOT numbers); never a frame report.</summary>
    public static void K7Line(string line) { if (On) Native(Runtime.Time.FrameCount, K7Kind, line, 0, 0, 0, 0, 0); }
    /// <summary>Kit #9 gc (Runtime/K9Gc.cs): a scene load starts -- kind 8, "load &lt;scene&gt;" with the main thread's allocation so far, for the
    /// host's per-scene nursery sizing (vita/shepherd/k9_gc.inc.c); never a frame report.</summary>
    public static void K9Scene(string scene) { if (On) Native(Runtime.Time.FrameCount, K9Kind, "load " + scene, 0, GC.GetAllocatedBytesForCurrentThread(), 0, 0, 0); }

    /// <summary>Device kit #9, track device (vita/notes/kit9-device.md) -- the GREEN rule: three of kit #8's device starts ended RED although the
    /// game ran perfectly, because their only press was the one at "Press Any Button": nothing is selected while the title prompt shows, so
    /// the watch had no "before" selection and the menu appearing with a selected button was not counted. Now a press (or touch) made while
    /// NOTHING is selected, followed within 600 frames by a selected button, counts once per start as the menu responding (the title press:
    /// the game's Main Menu opened because of it; device kit #8: Cross at f972, the menu shown at f1005, 'Resume Game' selected by f1040; vlx:
    /// 250 frames from the press to the selection). Only the first such answer counts (a later press with nothing selected -- in play, before a
    /// pause menu -- is not looked at). <c>SHEPHERD_K9_TITLE_PRESS=0</c> = kit #8's watch (only a change of the selected button counts).</summary>
    static readonly bool TitlePress = Environment.GetEnvironmentVariable("SHEPHERD_K9_TITLE_PRESS") != "0";
    const int TitleWindow = 600;
    static bool _pendTitle, _titleDone, _lzPendTitle, _lzTitleDone, _lzEvTitle;
    /// <summary>Kit #9 review, round 2 (finding 2): the title press's answer goes to the host as its own kind (9, not 3): the host counts it as
    /// a menu response for the END line / PASS M1 (the GREEN rule) but does NOT arm the play apps' memory guard with it -- kit #8 armed the
    /// guard at the first menu response, which was always a change of the selected button (a navigation); counted as kind 3 the title press
    /// armed it ~one idle Main Menu earlier (device play.txt: ~6,500 frames from the menu to the first D-pad press).</summary>
    /// (Kit #9 integration: kind 9 -- the device track used 8, which the gc track's scene note (K9Kind) had taken; with both on one host the
    /// scene-note branch ran first and swallowed the title press.)
    const int TitleResponse = 9;

    /// <summary>The selection before a pad press vs after it: a change is the menu responding to the pad. kit #5: a touch (the pointer moving
    /// to the finger, Impl.VitaTouch) counts like a pad press, so a session played by touch is interactive too. Kit #9: the title press (above).</summary>
    static void WatchMenu(int frame)
    {
        if (Impl.VitaPad.PressFrame != _seenPress)
        {
            _seenPress = Impl.VitaPad.PressFrame;
            if (_lastSel != null) { _pendFrame = _seenPress; _pendSel = _lastSel; _pendBtn = Impl.VitaPad.PressButton; _pendTouch = false; _pendTitle = false; }
            else if (TitlePress && !_titleDone && _pendFrame < 0) { _pendFrame = _seenPress; _pendSel = null; _pendBtn = Impl.VitaPad.PressButton; _pendTouch = false; _pendTitle = true; }
        }
        if (Impl.VitaTouch.TouchFrame != _seenTouch)
        {
            _seenTouch = Impl.VitaTouch.TouchFrame;
            if (_lastSel != null) { _pendFrame = _seenTouch; _pendSel = _lastSel; _pendTouch = true; _pendTitle = false; }
            else if (TitlePress && !_titleDone && _pendFrame < 0) { _pendFrame = _seenTouch; _pendSel = null; _pendTouch = true; _pendTitle = true; }
        }
        var sel = MenuProbe.SelectedName();
        if (_pendFrame >= 0)
        {
            if (sel != null && sel != _pendSel)
            {
                if (_pendTitle) _titleDone = true;
                if (++_events <= 20) SendNote(frame, _pendTitle ? TitleResponse : Interactive, _oldEvent = EventNote(_pendSel, sel, _pendTouch, _pendBtn, _pendFrame, frame, _events));
                _pendFrame = -1;
            }
            else if (frame - _pendFrame > (_pendTitle ? TitleWindow : 300)) _pendFrame = -1;
        }
        _lastSel = sel;
    }

    static string EventNote(string pendSel, string sel, bool touch, Impl.VitaPad.B btn, int pendFrame, int frame, int events) =>
        $"'{pendSel ?? "(nothing selected: the title press, kit #9)"}' -> '{sel}' label='{MenuProbe.SelectedLabel() ?? ""}' after {(touch ? $"touch at ({Impl.VitaTouch.TouchAt.x:F0},{Impl.VitaTouch.TouchAt.y:F0})" : $"pad {btn}")} at f{pendFrame} ({frame - pendFrame} frame(s) later), event {events}";
    static string _oldEvent;

    // ---- kit #8 speed track: the lazy menu watch (SHEPHERD_K8_MENU_WATCH; vita/notes/kit8-speed.md) ----
    // The per-frame watch read EventSystem.current's selection after every frame (VLX: ~0.17 ms a frame) to have the selection BEFORE a
    // press when one comes. The lazy watch reads it only on the frame a press / touch is read, right after the pad and touch screen are read
    // and before any game code runs (BeforeFrame) -- the same selection the per-frame watch kept from the previous frame's end (nothing between
    // that frame's end and this point runs game code: the frame's diagnostics, the present, the pacing, the next frame's input read) -- and
    // then after each frame only while a press waits for the menu's answer (<= 300 frames). The same events, the same notes, the same M1 lines.
    static string _lzPre, _lzPendSel;
    static int _lzSeenPress = -1, _lzSeenTouch = -1, _lzPendFrame = -1, _lzEvents;
    static Impl.VitaPad.B _lzPendBtn;
    static bool _lzPendTouch, _lzRan;
    // Kit #8 integration (speed x play): the play track's late input latch (Icalls/VitaPad.cs, SHEPHERD_K8_LATCH, on with the native pad =
    // every GL app) reads the controls -- and so sets VitaPad.PressFrame -- at the frame's FIRST INPUT QUERY, inside the frame's game code,
    // after BeforeFrame has looked. The lazy watch would then never see a press before the game code (its "before" selection stays unset:
    // no M1 event, no menu response, FAIL END). With the latch on, the watch therefore stays kit #7's per-frame watch -- the exact path the
    // play track gated with the latch -- and the lazy watch's ~0.17 ms a frame (VLX) is not saved there; SHEPHERD_K8_LATCH=0 gives the lazy
    // watch back (the speed track's gated path), SHEPHERD_K8_MENU_WATCH=every / SHEPHERD_K8_SPEED=0 the per-frame watch in any case. Resolved
    // at the first watched call (after the pad's first read: VitaPad is initialised by then, no static-init order changes).
    static int _lazyMode = -1;   // -1 unresolved, else bit 1 = lazy, bit 2 = check
    static void ResolveLazy()
    {
        bool latch = Impl.VitaPad.LatchOn;
        _lazyMode = (Runtime.K8Speed.LazyWatch && !latch ? 1 : 0) | (Runtime.K8Speed.WatchCheck && !latch ? 2 : 0);
        if (latch && (Runtime.K8Speed.LazyWatch || Runtime.K8Speed.WatchCheck))
            Log.Info("[k8] menu watch: per frame (kit #7's) -- the late input latch reads the controls inside the frame, after the lazy watch's look (SHEPHERD_K8_LATCH=0 gives the lazy watch back)");
    }
    /// <summary>Kit #8 integration review: the watch as resolved (-1 = not resolved: no menu watch ran in this start; bit 1 lazy, bit 2 the
    /// check) -- K8Speed's end line reports from it, not from the raw SHEPHERD_K8_MENU_WATCH value (with the latch on the check never runs).</summary>
    public static int LazyMode => _lazyMode;
    static bool LazyNow { get { if (_lazyMode < 0) ResolveLazy(); return (_lazyMode & 1) != 0; } }
    static bool CheckNow { get { if (_lazyMode < 0) ResolveLazy(); return (_lazyMode & 2) != 0; } }
    /// <summary>The Boot loop, after the pad / touch read, before the frame's game code.</summary>
    public static void BeforeFrame(int frame)
    {
        if (!Watch || !(LazyNow || CheckNow)) return;
        if (Impl.VitaPad.PressFrame != _lzSeenPress || Impl.VitaTouch.TouchFrame != _lzSeenTouch) _lzPre = _lzRan ? MenuProbe.SelectedName() : null;   // (before the first watched frame the per-frame watch had no selection either)
    }
    /// <summary>After the frame: the lazy watch's event this frame (its note), or null.</summary>
    static string LazyAfter(int frame)
    {
        string ev = null;
        _lzEvTitle = false;
        if (Impl.VitaPad.PressFrame != _lzSeenPress)
        {
            _lzSeenPress = Impl.VitaPad.PressFrame;
            if (_lzPre != null) { _lzPendFrame = _lzSeenPress; _lzPendSel = _lzPre; _lzPendBtn = Impl.VitaPad.PressButton; _lzPendTouch = false; _lzPendTitle = false; }
            else if (TitlePress && !_lzTitleDone && _lzPendFrame < 0) { _lzPendFrame = _lzSeenPress; _lzPendSel = null; _lzPendBtn = Impl.VitaPad.PressButton; _lzPendTouch = false; _lzPendTitle = true; }   // kit #9
        }
        if (Impl.VitaTouch.TouchFrame != _lzSeenTouch)
        {
            _lzSeenTouch = Impl.VitaTouch.TouchFrame;
            if (_lzPre != null) { _lzPendFrame = _lzSeenTouch; _lzPendSel = _lzPre; _lzPendTouch = true; _lzPendTitle = false; }
            else if (TitlePress && !_lzTitleDone && _lzPendFrame < 0) { _lzPendFrame = _lzSeenTouch; _lzPendSel = null; _lzPendTouch = true; _lzPendTitle = true; }   // kit #9
        }
        if (_lzPendFrame >= 0)
        {
            var sel = MenuProbe.SelectedName();
            if (sel != null && sel != _lzPendSel)
            {
                if (_lzPendTitle) _lzTitleDone = true;
                if (++_lzEvents <= 20) { ev = EventNote(_lzPendSel, sel, _lzPendTouch, _lzPendBtn, _lzPendFrame, frame, _lzEvents); _lzEvTitle = _lzPendTitle; }
                _lzPendFrame = -1;
            }
            else if (frame - _lzPendFrame > (_lzPendTitle ? TitleWindow : 300)) _lzPendFrame = -1;
        }
        _lzRan = true;
        return ev;
    }

    /// <summary>The GL part of a heartbeat: this frame's render time, the mean present time since the last heartbeat, draws, resident
    /// GPU textures (targets included), frames shown, vitaGL's pools. Empty for other backends.</summary>
    static string GlNote()
    {
        if (Render.MtRender.Inner(Render.SceneRenderer.G) is not Render.GlBackend gl) return "";   // (SHEPHERD_MT_RENDER=1: the GL backend behind the pipeline)
        Render.MtRender.SyncForRead();   // (SHEPHERD_MT_RENDER=1 with the render thread calling GL: let it finish before reading the backend's state)
        long n = gl.Presents - _lastPresents, t = gl.PresentTicks - _lastPresentTicks;
        _lastPresents = gl.Presents; _lastPresentTicks = gl.PresentTicks;
        var st = gl.Stats;
        return $" | gl: render {Render.SceneRenderer.LastFrameTicks * 1000.0 / Stopwatch.Frequency:F1} ms, present {(n > 0 ? t * 1000.0 / Stopwatch.Frequency / n : 0):F1} ms, draws {st.DrawsIn}->{st.DrawsOut}, textures {gl.ResidentMiB():F1} MiB, {gl.Presents} frames shown"
            + (Render.ShepGl.On ? $" | vgl {Render.ShepGl.Pools()}" : "");
    }

    /// <summary>Kit #8 speed track (SHEPHERD_K8_HEAP): the GC heap sgen measured at its last collection (GC.GetGCMemoryInfo reads the struct
    /// sgen fills: no lock) -- for the 50-frame heartbeats (sent as kind 7: the host labels the number "gc heap@gc") and the M1 notes (the host
    /// does not print a heap for those); GC.GetTotalMemory walks every major-heap block under the GC lock (VLX ~8 ms per call at the menu's
    /// heap). Checkpoints and the end keep GC.GetTotalMemory (the exact heap in use: rare, and what the memory tables read).</summary>
    static long HeapCheap() => GC.GetGCMemoryInfo().HeapSizeBytes;
    const int ProgressAtGc = 7;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SendNote(int frame, int kind, string note) =>
        Native(frame, kind, note, Runtime.K8Speed.CheapHeap ? HeapCheap() : GC.GetTotalMemory(false), GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0), GC.CollectionCount(GC.MaxGeneration), Objects.LiveCount);

    /// <summary>Kit #9: the heartbeat's physics part (Physics/PhysWorld.Stage1.cs Note: steps and phase times since the last heartbeat); none
    /// in a build without the physics runtime (SHEPHERD_PHYS_CODE=0).</summary>
#if SHEPHERD_NO_PHYS
    static string PhysNote() => "";
#else
    static string PhysNote() => Shepherd.Engine.Physics.PhysWorld.Note();
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Send(int frame, int kind)
    {
        string note;
        try
        {
            var scenes = string.Join(",", Scenes.All.Values.Select(s => s.Name));
            if (kind == Progress) note = $"scenes=[{scenes}]" + Runtime.Time.FixedNote() + GlNote() + Shepherd.Audio.ShepAudio.Note() + PhysNote();   // Stage C: audio numbers (empty when silent); kit #3 review: the wall clock's fixed steps
            else
            {
                var (sel, label, buttons) = MenuProbe.Current();
                note = $"selected='{sel ?? "(none)"}' label='{label ?? ""}' buttons={buttons ?? "-"} | scenes=[{scenes}] errors={Log.Errors} exceptions={Log.Exceptions}" + GlNote() + Shepherd.Audio.ShepAudio.Note();
            }
        }
        catch (Exception x) { note = "note failed: " + x.GetBaseException().Message; }
        bool cheap = kind == Progress && Runtime.K8Speed.CheapHeap;   // kit #8: the heartbeat's heap at the last collection (kind 7)
        Native(frame, cheap ? ProgressAtGc : kind, note, cheap ? HeapCheap() : GC.GetTotalMemory(false), GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0), GC.CollectionCount(GC.MaxGeneration), Objects.LiveCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Native(int frame, int kind, string note, long heap, long allocated, int minors, int majors, int liveObjects) =>
        shepherd_vita_engine_checkpoint(frame, kind, note, heap, allocated, minors, majors, liveObjects);

    [DllImport("__Internal")]
    static extern void shepherd_vita_engine_checkpoint(int frame, int kind, [MarshalAs(UnmanagedType.LPUTF8Str)] string note,
        long gcHeapBytes, long allocatedMainThread, int minorGcs, int majorGcs, int liveObjects);

    // ---------------- core pinning (kit 3, vita/notes/engine-pass-speed.md section 14 "Vita mapping") ----------------
    /// <summary>The Vita's user cores (psp2/kernel/cpu.h SCE_KERNEL_CPU_MASK_USER_0 / _1 / _2) and CPU3 (SCE_KERNEL_CPU_MASK_SYSTEM: the
    /// system-reserved 4th core, open to an app only with a core-unlock plugin such as CapUnlocker).</summary>
    public const int CoreUser0 = 0x10000, CoreUser1 = 0x20000, CoreUser2 = 0x40000, Core3 = 0x80000;
    /// <summary>Pin the main thread to USER_0 and the multicore pipeline's render thread to USER_1 (the host pins vitaGL's garbage
    /// collector to USER_2 by the same rule): SHEPHERD_VITA_PIN=1 / 0, unset = on exactly when SHEPHERD_MT_RENDER=1. Only under the Vita
    /// host. Without it every thread keeps the kernel's default affinity (any user core), as before.</summary>
    public static readonly bool Pin = On && (Environment.GetEnvironmentVariable("SHEPHERD_VITA_PIN") is { Length: > 0 } p ? p.Trim() == "1" : Render.MtRender.Requested);
    /// <summary>Boot, before the backend: the main thread to USER_0 (no-op unless <see cref="Pin"/>).</summary>
    public static void PinMain() { if (Pin) PinNative(CoreUser0, "main"); }
    /// <summary>The render thread, first thing on it (no-op unless <see cref="Pin"/>).</summary>
    public static void PinRender() { if (Pin) PinNative(CoreUser1, "shepherd-render"); }
    /// <summary>k4-display, the 4-core split: a LATENCY-TOLERANT worker (the pack read-ahead) to the core the host's boot probe chose for
    /// them -- CPU3 (mask 0x80000) when sceKernelChangeThreadCpuAffinityMask accepted it (the owner's core-unlock plugin), else USER_2
    /// (with the sound and vitaGL's garbage collector) -- one PIN line. Same rule as the others: only when pinning is on.</summary>
    public static void PinWorker(string who) { if (Pin) PinNative(shepherd_vita_worker_mask(), who); }
    [DllImport("__Internal")]
    static extern int shepherd_vita_worker_mask();
    /// <summary>The host changes the calling thread's affinity (sceKernelChangeThreadCpuAffinityMask), reads it back and logs both.
    /// The picture never depends on it: the frame clock, the pipeline's one ordered call sequence and the stream are unchanged.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PinNative(int mask, string who)
    {
        int rc = shepherd_vita_pin(mask, who);
        if (rc != 0) Log.Warn($"[pin] {who}: sceKernelChangeThreadCpuAffinityMask(0x{mask:x}) -> 0x{rc:x8} (the thread keeps its affinity)");
    }
    [DllImport("__Internal")]
    static extern int shepherd_vita_pin(int mask, [MarshalAs(UnmanagedType.LPUTF8Str)] string who);
}

/// <summary>kit #4 (M2) check of the host's GC.Collect governor (vita/shepherd/host.c, SHEPHERD_GC_GOVERN): with SHEPHERD_GCGOV_TEST=&lt;frame&gt;
/// (vlx / device test runs only; it adds two full collections at that frame), GC.Collect() from <see cref="Plain"/>, from <see cref="Governed"/>
/// and from <see cref="Plain"/> again, with the minor / major collection counts around each. Run with
/// SHEPHERD_GC_GOVERN=Shepherd.Engine.Diag.GcGovProbe::Governed the middle one must be a nursery collection (majors +0, minors +1) and the
/// others full ones (majors +1); without a governor (the Mac, SHEPHERD_GC_GOVERN=0) all three are full.</summary>
[ProfileOnly]
public static class GcGovProbe
{
    public static readonly int Frame = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_GCGOV_TEST"), out var f) && f > 0 ? f : -1;
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Plain() => GC.Collect();
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Governed() => GC.Collect();
    public static void Run()
    {
        var gov = Environment.GetEnvironmentVariable("SHEPHERD_GC_GOVERN") ?? "";
        bool expectGoverned = gov.Split(';').Contains("Shepherd.Engine.Diag.GcGovProbe::Governed");
        (int minors, int majors) Step(Action a) { int m0 = GC.CollectionCount(0), M0 = GC.CollectionCount(GC.MaxGeneration); a(); return (GC.CollectionCount(0) - m0, GC.CollectionCount(GC.MaxGeneration) - M0); }
        var p1 = Step(Plain); var g = Step(Governed); var p2 = Step(Plain);
        bool ok = p1.majors == 1 && p2.majors == 1 && (expectGoverned ? g.majors == 0 && g.minors >= 1 : g.majors == 1);
        Log.Info($"[gcgov] {(ok ? "PASS" : "FAIL")} GC.Collect from Plain: +{p1.minors} minor / +{p1.majors} major; from Governed: +{g.minors} / +{g.majors}; Plain again: +{p2.minors} / +{p2.majors} " +
                 $"(expected Governed {(expectGoverned ? "+1 minor / +0 major: the host governs it" : "+1 major: not governed")}; SHEPHERD_GC_GOVERN='{gov}')");
        // k4 review, the growth gate: after the major heap grew by more than half of sgen's allowance (a third of the heap the last major
        // left, at least 16 MiB) since the last major -- 0.6 of it kept alive here, which with the frame's other survivors lands between
        // half the allowance and sgen's own trigger -- a governed call is a FULL collection again (the first GC.Collect of a pause after play)
        bool gate = expectGoverned && Environment.GetEnvironmentVariable("SHEPHERD_GC_GOVERN_GATE") is not ("0" or "off");
        long baseBytes = GC.GetGCMemoryInfo().HeapSizeBytes;
        long target = (long)(0.6 * Math.Max(baseBytes / 3.0, 16.0 * 1048576));
        var keep = new List<byte[]>((int)(target / 4096) + 1);
        for (long got = 0; got < target; got += 4096) keep.Add(new byte[4096]);   // small objects: through the nursery into the major heap
        long grownBytes = GC.GetGCMemoryInfo().HeapSizeBytes - baseBytes;
        var gg = Step(Governed);
        GC.KeepAlive(keep); keep = null;
        var p3 = Step(Plain);
        bool ok2 = p3.majors == 1 && (expectGoverned ? (gate ? gg.majors == 1 : gg.majors == 0) : gg.majors == 1);
        Log.Info($"[gcgov] {(ok2 ? "PASS" : "FAIL")} after {target / 1048576.0:F1} MiB kept alive (the heap at the last collection +{grownBytes / 1048576.0:F1} MiB over the " +
                 $"{baseBytes / 1048576.0:F1} MiB the major left): GC.Collect from Governed +{gg.minors} minor / +{gg.majors} major (expected " +
                 $"{(!expectGoverned ? "+1 major: not governed" : gate ? "+1 major: the growth gate keeps the full collection" : "+0 major: gate off (SHEPHERD_GC_GOVERN_GATE=0)")}); Plain +{p3.minors} / +{p3.majors}");
    }
}

/// <summary>Kit #8 speed track, SHEPHERD_K8_MENU_WATCH=check (test runs): the per-frame menu watch makes the M1 lines (kit #7) and the lazy
/// watch runs beside it; every frame their events (the full notes) are compared. The end line counts them.</summary>
[ProfileOnly]
public static class MenuWatchCheck
{
    static int _frames, _events, _diff;
    public static void Compare(int frame, string perFrame, string lazy)
    {
        _frames++;
        if (perFrame != null || lazy != null) _events++;
        if (perFrame == lazy) return;
        if (++_diff <= 10) Log.Warn("[k8] menu watch check f" + frame + ": per-frame '" + (perFrame ?? "(none)") + "' vs lazy '" + (lazy ?? "(none)") + "'");
    }
    public static string Summary() => "menu watch check: " + _frames + " frames, " + _events + " event frame(s), " + _diff + " difference(s)";
}
