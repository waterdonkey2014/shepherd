using System.Collections;
using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine;

public static class Boot
{
    public static bool QuitRequested;
    public static int Run(string[] args)
    {
        Log.Open(Path.Combine(Program.LogDir, "engine.log"));
#if !SHEPHERD_EBOOT
        // kit #13 speed (Mac only): the NOPIX fix's brute check -- "k13nosample" (exit 0 = every decision = kit #12's) / "k13nosample negctl"
        if (args.FirstOrDefault() == "k13nosample") { long bad = Render.K11World.NoSampleBruteCheck(args.Length > 1 && args[1] == "negctl"); Log.Flush(); return bad == 0 ? 0 : 1; }
#endif
        Runtime.StrictOriginal.Report();   // kit #7 tests: the [strict] line (SHEPHERD_STRICT_ORIGINAL=1 only)
        Log.Info(Runtime.K8Speed.Describe());   // kit #8 speed track: which rows are on
        Log.Info(Runtime.K9Engine.Describe());  // kit #9 engine track: which rows are on
        Log.Info(Runtime.K9Fidelity.Describe());   // kit #9 fidelity track: Unity's Random + the stand-in fixes (SHEPHERD_K9_FIDELITY=0: kit #8)
        if (Profile.Vita) Log.Info(Runtime.K10Boot.Describe());   // kit #10 boot track: the boot screen (the game's own loading screen) and when it ends
        if (Runtime.K12Boot.Master && Environment.GetEnvironmentVariable("SHEPHERD_K12_BOOTSEQ") is { Length: > 0 }) Log.Info(Runtime.K12BootSeq.Describe());   // kit #12 boot: the owner's boot sequence (only when its line is in the env file)
        Log.Info(Runtime.K10Load.Describe());   // kit #10 boot track: loading priority (Unity's background-loading slice; play clock)
        Shepherd.Assets.StringPool.Enable(Runtime.K13Basec.StrPool, !Runtime.K13Basec.StrPoolBoot);   // kit #13 basec: the asset readers' string pool (before any asset is read; from the first frame's end unless =boot; tools never pool)
        Log.Info(Runtime.K13Basec.Describe());   // kit #13 basec: which arm of every row runs
        IcallRegistry.Init(Path.Combine(Program.RuntimeManaged, "icalls.json"));
        var mode = args.FirstOrDefault() ?? "smoke";
        int rc = mode switch { "smoke" => Smoke(), "gfxtest" => GfxTest(), "uitest" => UiTest(), "boot" => BootGame(args), "nativetest" => NativeTest(), "respaths" => ResPaths(args), "dumpobj" => DumpObj(args), "shaderdump" => ShaderDump(args), _ => PhysCheckHandles(mode) ? PhysCheckRun(args) : Diag.TriggerTest.Handles(mode) ? Diag.TriggerTest.Run() : Diag.SpineClipFuzz.Handles(mode) ? Diag.SpineClipFuzz.Run(args) : Diag.SpineSkipTest.Handles(mode) ? Diag.SpineSkipTest.Run(args) : Diag.VitaSelfTest.Handles(mode) ? Diag.VitaSelfTest.Run() : Render.RenderTest.Handles(mode) ? Render.RenderTest.Run() : Render.K12UiTest.Handles(mode) ? Render.K12UiTest.Run() : Render.K13PerspTest.Handles(mode) ? Render.K13PerspTest.Run() : Render.GxpCache.Handles(mode) ? Render.GxpCache.SelfTest() : Unknown(mode) };   // "vitatest"
        if (Profile.Vita) VitaSummary(); else if (Impl.NetIcalls.Created > 0) Log.Info(Impl.NetIcalls.Summary());
        IcallRegistry.Report(Path.Combine(Program.LogDir, "missing-icalls.txt"));
#if !SHEPHERD_EBOOT
        Diag.K9IcallAlloc.Write(Program.LogDir);   // kit #9 engine: SHEPHERD_K9_ICALL_ALLOC (Mac measurement)
#endif
        Diag.SimStats.End();         // V6 SHEPHERD_SIMSTATS: startup/window summary + assembly list; no-op when unset
        Diag.K7.End();               // kit #7: the profiler's last scene + the Chrome trace (SHEPHERD_K7_PROF / _TRACE); no-op when unset
#if !SHEPHERD_EBOOT
        Diag.K11Measure.End();       // kit #11 measure: the heat map / green check / census / counters summaries (SHEPHERD_K11_*; Mac / tools builds); no-op when unset
#endif
        Diag.NativeCensus.Write();   // runtime/logs/native-resolve.txt; no-op unless enabled (vita / SHEPHERD_NATIVE_CENSUS=1)
        if (Assets.PackAssets.On) Assets.PackAssets.Report();   // SHEPHERD_PACK
        if (Assets.PackRecorder.On) Assets.PackRecorder.Close(); // SHEPHERD_PACK_RECORD
        if (Shepherd.Audio.Silence.Summary() is { } silence) Log.Info(silence);   // FMOD's own mute state (Mac default)
        if (Native.RegistryCheck.Summary() is { } regcheck) Log.Info(regcheck);    // SHEPHERD_CHECK_REG=1 (stage 2)
        if (Render.RenderCheck.Summary() is { } rendercheck) Log.Info(rendercheck);   // SHEPHERD_RENDER_CHECK=1 (stage 2)
        if (Runtime.K13Basec.Summary() is { } k13basec) Log.Info(k13basec);   // kit #13 basec: what the rows did (null with SHEPHERD_K13_BASEC=0)
        if (Render.MtCheck.Summary() is { } mtcheck) Log.Info(mtcheck);              // SHEPHERD_MT_RENDER=1 SHEPHERD_MT_CHECK=1 (menu-speed, multicore stage)
        if (Render.UiCacheCheck.Summary() is { } uicache) Log.Info(uicache);   // SHEPHERD_UICACHE_CHECK=1 (menu-speed)
        if (Native.HopCheck.Summary() is { } hopfix) Log.Info(hopfix);   // SHEPHERD_HOPFIX_CHECK=1 (menu-speed hop fix)
        if (Render.HotCheck.Summary() is { } hot) Log.Info(hot);   // SHEPHERD_HOTSPOT_CHECK=1 (menu-speed, top hotspots)
        if (Render.K13Speed.Summary() is { } k13speed) Log.Info(k13speed);   // kit #13 speed: what its rows did (one line)
        if (Runtime.RewiredClock.Summary() is { } rewiredClock) Log.Info(rewiredClock);   // headless: Rewired on the frame clock
        if (Diag.Throttle.Summary() is { } throttle) Log.Info(throttle);   // SHEPHERD_FRAME_MS (measurement)
        if (Runtime.SerRulesFast.Summary() is { } serrules) Log.Info(serrules);   // SHEPHERD_SERRULES_FAST=check (kit #5 B4)
        if (Runtime.PlayClock.Summary() is { } clock) Log.Info(clock);   // kit3 play mode: the wall clock's game time vs wall time, clamped frames, fixed steps
        if (Render.G6Cull.Summary() is { } g6) Log.Info(g6);   // kit #7 G6: mesh triangles culled by their shader's Cull state
        if (Render.K11World.Summary() is { } k11) Log.Info(k11);   // kit #11 gpu1: what its switches did over the run (no switch on: no line)
        if (Render.K12Ui.Summary() is { } k12ui) Log.Info(k12ui);   // kit #12 menusa: what its switches did over the run (no switch on: no line)
#if !SHEPHERD_EBOOT
        Render.K12Ui.CsvEnd();   // kit #12 menusa: SHEPHERD_K12_FRAMECSV (Mac measurement only)
#endif
        if (Runtime.BlackFrameGc.Summary() is { } blackgc) Log.Info(blackgc);   // kit #4 M1: UnloadUnusedAssets' collections on covered frames
#if !SHEPHERD_NO_K13WALKC
        if (Runtime.K13Unload.Summary() is { } k13u) Log.Info(k13u);   // kit #13 walkc: the asset releases
#endif
        if (Runtime.MenusCfg.MissSummary() is { } misses) Log.Info(misses);   // kit #5 menus: the Resources misses logged once each
        if (Runtime.FieldCopy.Summary() is { } cloneCheck) Log.Info(cloneCheck);   // kit #5 menus: SHEPHERD_CLONE_CHECK=1
        Diag.SpineCats.Summary();   // kit6 mem L1 measurement (SHEPHERD_SPINE_CATS=1)
        Runtime.SpineReadShare.Summary();   // kit6 mem L1: totals (+ the guard with SHEPHERD_SPINE_READSHARE_CHECK=...,end); nothing when no handler is installed
        Runtime.SpineLazySkins.Summary();   // kit #7 spine L2: lazy skins (waited / read later / never read); nothing when no handler is installed or nothing called in
        Runtime.SpineClipNative.Summary();  // kit #7 spine: native clipping totals (+ the check mode's comparisons); nothing when nothing clipped
#if !SHEPHERD_NO_K14SMALL
        Runtime.K14Catalog.Summary();       // kit #14 small: the lazy catalog -- what was asked for (+ the check mode's verdict); the Vita profile only
#endif
        if (Render.SpineSkip.Summary() is { } spineSkip) Log.Info(spineSkip);   // kit #7 spine: colour-0 Spine triangles left out of the GPU draws (none: no line)
        Diag.SpineBake.Summary();           // kit #7 spine: the bake check's totals (SHEPHERD_SPINE_BAKE_CHECK; Mac test tool); nothing otherwise
        Log.Info(Runtime.K6Engine.Summary());   // kit #6 engine track: which items are on
        if (Diag.TestAstar.Summary() is { } testAstar) Log.Info(testAstar);   // kit #7 tests: SHEPHERD_TEST_ASTAR (eboots: one line if a Mac test switch was set)
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.Summary() is { } testSpine) Log.Info(testSpine);   // kit #7 tests: SHEPHERD_TEST_SPINE
#endif
        if (Runtime.K8Speed.Summary() is { } k8) Log.Info(k8);   // kit #8 speed track: the tag index, skipped empty messages, the log diet
        if (Runtime.K9Engine.Summary() is { } k9) Log.Info(k9);   // kit #9 engine track: what each row did (+ the check modes' differences)
        if (Runtime.K10Load.Summary() is { } k10l) Log.Info(k10l);   // kit #10 boot track: loading priority's extra passes
        if (Runtime.K9Typed.Summary() is { } k9t) Log.Info(k9t);   // kit #9 runtime track: the typed / mirror check modes' verdict
        if (Runtime.K10Cache.Summary() is { } k10) Log.Info(k10);   // kit #10 cache track: the learning cache (hits, writes, evictions) + the learning kinds' statistics
#if !SHEPHERD_NO_PHYS
        if (Physics.PhysConfig.On || Physics.PhysConfig.Asked < 0) PhysExit();   // physics stage 0: what the groundwork built and saw (one line; more with SHEPHERD_CHECK_PHYS)
#endif
        Log.Info($"done: {Log.Errors} errors, {Log.Exceptions} exceptions, {Objects.LiveCount} live objects");
        Log.Flush();   // kit #8: the batched engine.log (no-op when flushed per line)
        return rc;
    }
#if !SHEPHERD_EBOOT && !SHEPHERD_NO_PHYS
    static bool PhysCheckHandles(string mode) => Physics.PhysCheck.Handles(mode);   // physics: physcheck / physbench (Physics/PhysCheck.cs)
    static int PhysCheckRun(string[] args) => Physics.PhysCheck.Run(args);
#else
    static bool PhysCheckHandles(string mode) => false;   // kit #8: the physics checks are Mac-only (SHEPHERD_ENGINE_TOOLS=1 keeps them in an eboot); none in a SHEPHERD_PHYS_CODE=0 build
    static int PhysCheckRun(string[] args) => 0;
#endif
#if !SHEPHERD_NO_PHYS   // (kit #8 review: SHEPHERD_PHYS_CODE=0 builds have no physics runtime and no exit line, as kit #7)
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void PhysExit()
    {
        Physics.PhysWorld.Shutdown();   // physics Stage 1: the trace closed, the outputs flushed
        Log.Info(Physics.PhysWorld.Summary());
        if (Physics.PhysConfig.Check.Length > 0) Log.Info(Physics.PhysGuard.Describe());
    }
#endif
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void VitaSummary()
    {
        Impl.VitaPad.DumpRewired(); Impl.VitaTouch.Summary(); Log.Info(Shepherd.Audio.Fmod.Summary()); Log.Info(Impl.NetIcalls.Summary());
        foreach (var s in new[] { VitaMemory.SaveStreamsSummary(), VitaMemory.I2Summary(), VitaMemory.PoolSummary(), NativeDisplay.Summary(), Impl.VitaPad.LatchSummary(), K8Play.Summary() }) if (s != null) Log.Info(s);
        Assets.Loader.CheckBuiltins();
    }
    static unsafe int GfxTest()
    {
        using var g = Render.Backends.Create(640, 360, headless: true);
        var px = new byte[64 * 64 * 4]; for (int i = 0; i < 64 * 64; i++) { int x = i % 64, y = i / 64; bool c = ((x / 8) + (y / 8)) % 2 == 0; px[i * 4] = (byte)(c ? 255 : 40); px[i * 4 + 1] = (byte)(c ? 200 : 40); px[i * 4 + 2] = 60; px[i * 4 + 3] = 255; }
        var tex = g.CreateTexture(64, 64, px, false);
        g.Begin(0.1f, 0.05f, 0.1f, 1);
        var v = new Render.Sdl.Vertex[4]; float[,] q = { { 120, 60, 0, 0 }, { 520, 60, 1, 0 }, { 520, 300, 1, 1 }, { 120, 300, 0, 1 } };
        for (int i = 0; i < 4; i++) v[i] = new Render.Sdl.Vertex { position = new() { x = q[i, 0], y = q[i, 1] }, color = new() { r = 1, g = 1, b = 1, a = 1 }, tex_coord = new() { x = q[i, 2], y = q[i, 3] } };
        var idx = new[] { 0, 1, 2, 0, 2, 3 };
        g.Draw(tex, Render.Sdl.BLENDMODE_BLEND, false, v, idx);
        var path = Path.Combine(Program.Root, "runtime", "shots", "gfxtest.png"); g.Screenshot(path); Log.Info("wrote " + path); return 0;
    }
    static int UiTest()
    {
        Runtime.Time.CaptureDeltaTime = Runtime.Time.FrameClockDt;   // physics stage 0: SHEPHERD_CAPTURE_DT (unset: 1f / 60f, as before)
        using var g = Render.Backends.Create(1280, 720, headless: true); Render.SceneRenderer.G = g;
        var camGo = new GameObject("Main Camera"); camGo.tag = "MainCamera"; var cam = camGo.AddComponent<Camera>(); cam.backgroundColor = new Color(0.12f, 0.06f, 0.14f);
        var canvasGo = new GameObject("Canvas"); var canvas = canvasGo.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080);
        UnityEngine.UI.Image Box(string n, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size, Color c)
        {
            var go = new GameObject(n, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform; rt.anchorMin = aMin; rt.anchorMax = aMax; rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.AddComponent<UnityEngine.UI.Image>(); img.color = c; return img;
        }
        Box("Backdrop", canvasGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.35f, 0.08f, 0.1f, 0.6f));
        var panel = Box("Panel", canvasGo.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(700, 520), new Color(0.95f, 0.85f, 0.65f));
        string[] items = { "Continue", "New Game", "Settings", "Credits", "Quit" };
        for (int i = 0; i < items.Length; i++)
            Box(items[i], panel.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -70 - i * 90), new Vector2(520, 70), i == 0 ? new Color(0.8f, 0.1f, 0.15f) : new Color(0.25f, 0.12f, 0.18f));
        Lifecycle.Render = Render.SceneRenderer.Frame;
        for (int i = 0; i < 5; i++) Lifecycle.Frame();
        Log.Info($"canvas scaleFactor={canvas.scaleFactor} draws={Render.SceneRenderer.LastDraws} tris={Render.SceneRenderer.LastTris}");
        var path = Path.Combine(Program.Root, "runtime", "shots", "uitest.png"); g.Screenshot(path); Log.Info("wrote " + path); return 0;
    }
    /// <summary>Boot the real game: level0 -> LoadMainMenu drives itself. args: boot [frames] [shotEvery] [window]</summary>
    static int BootGame(string[] args)
    {
        int frames = args.Length > 1 ? int.Parse(args[1]) : 600, every = args.Length > 2 ? int.Parse(args[2]) : 120;
        if (frames <= 0) frames = int.MaxValue;   // 0 = run until the window closes
        bool window = args.Length > 3 && args[3] == "window";
        Diag.VitaHost.PinMain();        // the Vita: main thread -> USER_0 when pinning is on (SHEPHERD_VITA_PIN; default = SHEPHERD_MT_RENDER=1)
        if (Profile.Vita) VitaToggles.Boot();   // kit #6: Settings > Graphics > PS Vita Settings, the saved choices (4th CPU Core, CPU Speed) before the pack's read-ahead starts
        using var g = Render.MtRender.Wrap(Render.Backends.Create(Impl.AppIcalls.ScreenW, Impl.AppIcalls.ScreenH, headless: !window)); Render.SceneRenderer.G = g;   // 1280x720; vita profile 960x544; SHEPHERD_RENDER picks the backend; SHEPHERD_MT_RENDER=1 the multicore pipeline (Render/MtRender.cs)
        var rstats = Render.Backends.Explicit ? new Render.FrameTimes(frames, every) : null;
        Runtime.PlayClock.Configure(window);   // kit3 play mode: SHEPHERD_TIME=frame (headless default: 1/60 s per frame of game time and, stage 2, realtime) | wall (window default, the play VPKs: Unity's clock); logged
        Shepherd.Audio.Silence.Configure(headless: !window);       // Mac default: FMOD muted at the engine level in headless runs
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assets.Loader.Init();
        Log.Info($"asset layer up in {sw.ElapsedMilliseconds} ms"); Diag.SimStats.AssetsUp(); Diag.K7.AssetsUp();
        Lifecycle.Render = Render.SceneRenderer.Frame;
        Lifecycle.InstallSyncContext();
        if (Profile.Vita) VitaMemory.BeforeRuntimeInit();   // stage 1: save streams seeded before InitUnifyGlobal.Startup
        if (Profile.Vita) Impl.VitaPad.InstallJoystick();   // polish round 1: the pad is a Unity-input joystick to Rewired, from before its init
        Runtime.K6Engine.Install();                          // kit #6 engine track: late icall hooks of the items that are on
        if (Profile.Vita) Impl.VitaTouch.Install();         // kit #5: the front touch screen as the game's mouse (SHEPHERD_TOUCH=0: off)
        RuntimeInit.Run(RuntimeInit.SubsystemRegistration, RuntimeInit.AfterAssembliesLoaded, RuntimeInit.BeforeSplashScreen, RuntimeInit.BeforeSceneLoad);
        Assets.Loader.LoadLevel0();
        RuntimeInit.Run(RuntimeInit.AfterSceneLoad);
        var shots = Diag.ShotPlan.Dir(Path.Combine(Program.Root, "runtime", "shots", "boot")); Directory.CreateDirectory(shots);   // Stage B: SHEPHERD_SHOT_DIR
        // SHEPHERD_PRESS="frame:unityKeyCode,frame:key" -> synthetic key presses (held 6 frames)
        var presses = (Environment.GetEnvironmentVariable("SHEPHERD_PRESS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':')).Select(p => (f: int.Parse(p[0]), k: int.Parse(p[1]))).ToList();
        if (Profile.Vita) VitaMemory.AfterBootInit();   // kit #4 review: the boot's garbage collected at one fixed point (part bootgc; before CP0)
        Diag.VitaHost.Start();          // V6 Stage B: frame-0 checkpoint for the Vita host's log; no-op unless SHEPHERD_VITA_HOST=1
        int last = 0;
        for (int i = 1; i <= frames && !QuitRequested && !Impl.InputState.QuitRequested; i++)
        {
            last = i;
            if (K8Play.Pacing) K8Play.FrameStart(i);   // kit #8 play: the frame-time histogram + G3 pacing (play clock on the Vita host; the Frame Pacing row; no-op otherwise)
            Diag.K7.FrameBegin(i);      // kit #7: the black box's frame ring, the boot numbers, the profiler's frame (SHEPHERD_K7=0: nothing)
            Diag.SimStats.Before(i);    // V6 SHEPHERD_SIMSTATS; no-op when unset
#if !SHEPHERD_EBOOT
            Diag.K11Measure.Before(i);  // kit #11 measure (SHEPHERD_K11_*; Mac / tools builds): the counters' frame start, the census's frame number; no-op when unset
#endif
            Diag.NativeCensus.Tick(i);  // V6: frame map for Mono's DllImport log (only with MONO_LOG_DEST + MONO_LOG_MASK=dll)
            Diag.K7Prof.Push(Diag.K7Prof.ZInput);
            Impl.InputState.Pump();
            if (Profile.Vita) Impl.VitaPad.Tick(i);   // gamepad + SHEPHERD_PAD -> keys
            if (Profile.Vita) Impl.VitaTouch.Tick(i); // kit #5: front touch + SHEPHERD_TOUCH_SCRIPT -> the mouse
#if !SHEPHERD_EBOOT
            Diag.K8PadMap.Before(i);    // kit #8 play: SHEPHERD_K8_PADMAP_PROBE (Mac measurement; not in the eboots); no-op when unset
            Diag.K12BootRec.Before(i);  // kit #12 boot: SHEPHERD_K12_REC_EMBLEM=<sprite> the boot screen recordings' emblem (Mac tools; not in the eboots); no-op when unset
#endif
            foreach (var (pf, pk) in presses) { if (i == pf) { Impl.InputState.Press(pk); Log.Info($"[input] press {pk}"); } if (i == pf + 6) Impl.InputState.Release(pk); }
            Diag.VitaHost.BeforeFrame(i);   // kit #8: the lazy menu watch's selection before a press (SHEPHERD_K8_MENU_WATCH); no-op unless the menu watch is on
            Diag.K7Prof.Pop();
#if !SHEPHERD_EBOOT
            if (Diag.TestWarp.On) Diag.TestWarp.Before(i);   // kit #13 basec: SHEPHERD_TEST_QUICKSTART / _DOOR / _FOLLOWERS / _DUMP (Mac tests; not in the release eboots, refused under the Vita host); no call when unset
#endif
            Diag.MemReport.Before(i);   // SHEPHERD_MEMREPORT=<frame>[:<window>]; no-op when unset
            long ft0 = rstats != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            Lifecycle.Frame();
            if (Runtime.K12BootSeq.On) Runtime.K12BootSeq.Tick(i);   // kit #12 boot: the owner's boot sequence -- the Main Menu reached ends the hold (before K10Boot's end; off: no call)
            if (Runtime.K10Boot.On) Runtime.K10Boot.Tick(i);   // kit #10 boot track: the boot screen ends at the game's first frame (before its present) + the boot anatomy line (no frame change)
            if (Runtime.K12PluginDetect.Pending) Runtime.K12PluginDetect.Handoff(i);   // kit #12 boot: the recommended plugins' detection handed to menusb's Settings row once (Vita GL host only; SHEPHERD_K12_PLUGINS_SET=1, default off: no call)
            rstats?.Frame(i, ft0, g);
            Diag.K7Prof.Push(Diag.K7Prof.ZDiag);
            Diag.MenuClock.After(i);    // stage 2: m+N frames (relative to the Main Menu scene) for the probes below; no-op unless used
            Diag.SimStats.After(i);
            Diag.MemReport.After(i);
            Diag.ArrayShare.After(i);   // kit4 intro-plan EXPERIMENT: SHEPHERD_EXP_ARRAYSHARE=<frame>:<types>; no-op when unset
            Runtime.SpineShare.After(i); // kit5 mem L1 prototype: SHEPHERD_SPINE_SHARE=1 (+ _CHECK=<frames>); no-op when unset
            Diag.SpineDup.After(i);     // kit5 mem L0 diagnostic: SHEPHERD_SPINEDUP=<frame>[,...]; no-op when unset
            Diag.SpineCats.After(i);    // kit6 mem L1 measurement: SHEPHERD_SPINE_CATS=1; no-op when unset
            Runtime.SpineReadShare.After(i);   // kit6 mem L1 guard: SHEPHERD_SPINE_READSHARE_CHECK=<frames>; no-op when unset
            Diag.SpineCensus.After(i);  // kit #7 spine: SHEPHERD_SPINE_CENSUS=<frames> (Mac measurement; not in the eboots); no-op when unset
            Diag.AnimCensus.After(i);   // kit #7 spine: SHEPHERD_ANIM_CENSUS=<frames> (Mac measurement; not in the eboots); no-op when unset
            Diag.SpineBake.After(i);    // kit #7 spine: SHEPHERD_SPINE_BAKE=<dir> / SHEPHERD_SPINE_BAKE_CHECK=<dir> (Mac, test-only; not in the eboots); no-op when unset
            Diag.RecCensus.After(i);    // kit6 mem L4 measurement: SHEPHERD_RECCENSUS=<frame>[,...]; no-op when unset
            Diag.K6Probe.After(i);      // kit #6 engine track: SHEPHERD_K6_PROBE test driver (not in the eboots); no-op when unset
            Diag.MenuProbe.After(i);    // V6 SHEPHERD_MENU_PROBE: selected button / labels as log lines; no-op when unset
#if !SHEPHERD_EBOOT
            Diag.StateHash.After(i);    // kit #7 tests: SHEPHERD_STATE_HASH per-frame state checksums (Mac only; not in the eboots)
            Diag.K11Measure.After(i);   // kit #11 measure (SHEPHERD_K11_*; Mac / tools builds): the frame's heat map / green check / counters, before Present; no-op when unset
            Diag.K8PadMap.After(i);     // kit #8 play: SHEPHERD_K8_PADMAP=<frame> the game's Rewired maps (Mac measurement; not in the eboots); no-op when unset
            Diag.K12UiDump.After(i);    // kit #12 boot: SHEPHERD_K12_UIDUMP=<frames>[:<object>] a UI subtree's meshes / materials / sprites (Mac measurement; not in the eboots); no-op when unset
#if !SHEPHERD_NO_K13WALKC
            Diag.K13Bags.Frame(i);      // kit #13 walkc: SHEPHERD_K13_BAGS=<frame> the engine-side data of asset / scene records and the intro's halves (Mac measurement); no-op when unset
            Diag.K13Paths.DeadFrame(i); // kit #13 walkc: SHEPHERD_K13_DEAD=<frame> destroyed records the engine's statics still reach (Mac measurement); no-op when unset
            Diag.K13Paths.DeadSizeFrame(i); // kit #13 walkc: SHEPHERD_K13_DEADSIZE=<frame> what only destroyed records' links hold (Mac measurement); no-op when unset
#endif
#endif
            if (Profile.Vita) Impl.VitaTouch.After(i);   // kit #5: SHEPHERD_TOUCH_DIAG=1 pointer lines; no-op otherwise
            if (VitaToggles.Live) VitaToggles.Tick();    // kit #6: the PS Vita Settings rows' clock readout, about once a second while they exist
#if !SHEPHERD_NO_K12MENUSB
            if (K12View.Active) K12View.Frame();         // kit #12: Camera Closer / Closest (the projection's factor, fights), the Cutscene Bars' cull
#endif
            Diag.VitaHost.Frame(i);     // V6 Stage B: checkpoints + heartbeat to the Vita host; no-op unless SHEPHERD_VITA_HOST=1
            if (presses.Any(x => i >= x.f - 1 && i <= x.f + 8)) ProbeInput(i);
            Diag.K7Prof.Pop();
            Diag.K7Prof.Push(Diag.K7Prof.ZPresent);
            if (window || Render.Backends.PresentHeadless) g.Present();   // Stage B: the shepgl host's display is real
            Diag.K7Prof.Pop();
#if !SHEPHERD_EBOOT
            if (Render.K11PixHash.On) Render.K11PixHash.Frame(i, g);   // kit #11 gpu1: SHEPHERD_K11_PIXHASH per-frame picture hashes (Mac only; not in the eboots)
#endif
            if ((every > 0 && (i % every == 0 || i == 1)) || Diag.ShotPlan.Extra(i)) { g.Screenshot(Path.Combine(shots, $"f{i:D5}.png")); Log.Info($"frame {i}: t={UnityEngine.Time.time:F2}s draws={Render.SceneRenderer.LastDraws} tris={Render.SceneRenderer.LastTris} live={Objects.LiveCount} scenes=[{string.Join(", ", Scenes.All.Values.Select(x => x.Name + ":" + x.Roots.Count))}] coroutines={Coroutines.Count}"); rstats?.Shot(i, g); }
            Diag.K7Prof.Push(Diag.K7Prof.ZThrottle);
            Diag.Throttle.After();      // kit3 play mode: SHEPHERD_FRAME_MS=<ms> pads every frame to that much wall time (the device's frame rate on the Mac); no-op unless set
            Diag.K7Prof.Pop();
            Runtime.LogDiet.EndFrame(); // kit #8: engine.log's lines of this frame in one write with SHEPHERD_K8_LOG_DIET=batch / ring (opt-in); the default flushes per line (kit #7)
        }
        if (Runtime.K10Boot.On) Runtime.K10Boot.Finish(last);   // kit #10 boot track: a boot screen still showing ends with the loop
        Diag.VitaHost.Finish(last);     // V6 Stage B: the end checkpoint (selected menu button); no-op unless SHEPHERD_VITA_HOST=1
        rstats?.Summary(g);
        if (Runtime.K12PressX.Summary() is { } k12px) Log.Info(k12px);   // kit #12 boot: Press X to Start's count (only when on)
        DumpUi();
        return 0;
    }
    static void DumpTree(NTransform t, int depth)
    {
        if (depth > 9 || !t.Go.ActiveSelf) return;
        var comps = string.Join(",", t.Go.Components.Where(c => c is not NTransform).Select(c => c.Managed?.GetType().Name ?? "?").Where(n => n is not "CanvasRenderer"));
        var wp = (Vector3)t.Managed.GetType().GetProperty("position")!.GetValue(t.Managed)!;
        Log.Info($"ui {new string(' ', depth * 2)}{t.Go.Name} [{comps}] " + (t.IsRect ? $"aMin={t.AnchorMin} aMax={t.AnchorMax} piv={t.Pivot} aPos={t.AnchoredPosition} sd={t.SizeDelta} size={t.Size} lp={t.EffectiveLocalPosition} wp={wp:F2}" : $"lp={t.LocalPosition} wp={wp:F2}") + $" s={t.LocalScale}");
        foreach (var cg in t.Go.Components.Select(c => c.Managed).OfType<CanvasGroup>())
            Log.Info($"ui {new string(' ', depth * 2)}  ^ canvasGroup enabled={cg.enabled} interactable={cg.interactable} alpha={cg.alpha:F2} blocks={cg.blocksRaycasts} ignoreParent={cg.ignoreParentGroups}");
        foreach (var sel in t.Go.Components.Select(c => c.Managed).OfType<UnityEngine.UI.Selectable>())
        {
            const System.Reflection.BindingFlags I = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var st = typeof(UnityEngine.UI.Selectable);
            Log.Info($"ui {new string(' ', depth * 2)}  ^ selectable interactable={sel.interactable} groupsAllow={st.GetField("m_GroupsAllowInteraction", I)?.GetValue(sel)} state={st.GetProperty("currentSelectionState", I)?.GetValue(sel)} transition={sel.transition} selectedGO={(UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject?.name ?? "null")}");
        }
        foreach (var c in t.Children) DumpTree(c, depth + 1);
    }

    static bool _mapsDumped;
    static void ProbeInput(int frame)
    {
        try
        {
            const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, I = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Rewired_Core"); var re = asm.GetType("Rewired.ReInput");
            object P(object o, string n) => o?.GetType().GetProperty(n, I)?.GetValue(o) ?? o?.GetType().GetField(n, I)?.GetValue(o);
            object Call(object o, string n, params object[] a) => o?.GetType().GetMethods(I).FirstOrDefault(m => m.Name == n && m.GetParameters().Length == a.Length && (a.Length == 0 || m.GetParameters()[0].ParameterType.IsInstanceOfType(a[0]) || m.GetParameters()[0].ParameterType.IsEnum))?.Invoke(o, a);
            var kb = P(re.GetProperty("controllers", S)!.GetValue(null), "Keyboard");
            var p0 = re.GetProperty("players", S)!.GetValue(null) is var ph ? ph!.GetType().GetMethod("GetPlayer", new[] { typeof(int) })!.Invoke(ph, new object[] { 0 }) : null;
            var maps = Call(P(P(p0, "controllers"), "maps"), "GetAllMaps", Enum.ToObject(asm.GetType("Rewired.ControllerType")!, 0)) as System.Collections.IEnumerable;
            var ms = maps == null ? "?" : string.Join(" ", maps.Cast<object>().Select(m => $"[cat{P(m, "categoryId")} lay{P(m, "layoutId")} en={P(m, "enabled")} n={P(m, "elementMapCount")}]"));
            if (maps != null && !_mapsDumped) { _mapsDumped = true; foreach (var m in maps.Cast<object>()) { var em = P(m, "AllMaps") as System.Collections.IEnumerable ?? Call(m, "GetElementMaps") as System.Collections.IEnumerable;
                Log.Info($"[probe] map cat{P(m, "categoryId")}: " + (em == null ? "?" : string.Join(" ", em.Cast<object>().Select(e => $"{P(e, "keyCode")}->a{P(e, "actionId")}{(P(e, "enabled") is false ? "(off)" : "")}")))); } }
            Log.Info($"[probe {frame}] kb.anyButton={Call(kb, "GetAnyButton")} kb.anyDown={Call(kb, "GetAnyButtonDown")} kb.Return={Call(kb, "GetKey", KeyCode.Return)} p0.any={Call(p0, "GetAnyButton")} p0.anyDown={Call(p0, "GetAnyButtonDown")} kbMaps={ms}");
        }
        catch (Exception e) { Log.Info($"[probe {frame}] " + e.GetBaseException().Message); }
    }

    public static void DumpRewired()
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rewired_Core");
            if (asm == null) { Log.Info("rewired: Rewired_Core not loaded"); return; }
            var re = asm.GetType("Rewired.ReInput"); const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Log.Info($"rewired: isReady={re?.GetProperty("isReady", S)?.GetValue(null)}");
            foreach (var m in Objects.All.OfType<NMono>().Where(m => !m.Destroyed && m.Managed != null && (m.Managed.GetType().Namespace ?? "").StartsWith("Rewired")))
                Log.Info($"rewired: mb {m.Managed.GetType().FullName} go='{m.Go?.Name}' scene={m.Go?.Scene} active={m.Go?.ActiveInHierarchy} en={m.Enabled}");
            const System.Reflection.BindingFlags I = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object P(object o, string n) => o?.GetType().GetProperty(n, I)?.GetValue(o) ?? o?.GetType().GetField(n, I)?.GetValue(o);
            var players = re.GetProperty("players", S)?.GetValue(null);
            Log.Info($"rewired: players type={players?.GetType().FullName} methods={string.Join(",", players?.GetType().GetMethods(I).Select(m => m.Name + "/" + m.GetParameters().Length).Distinct().Take(30) ?? Array.Empty<string>())}");
            int pc = P(players, "playerCount") is int pcv ? pcv : P(players, "allPlayerCount") is int apc ? apc : 0;
            var getP = players?.GetType().GetMethods(I).FirstOrDefault(m => m.Name == "GetPlayer" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
            var sysP = P(players, "SystemPlayer");
            foreach (var pl in Enumerable.Range(0, pc).Select(i => getP?.Invoke(players, new object[] { i })).Append(sysP).Where(x => x != null)) { var c = P(pl, "controllers"); Log.Info($"rewired: player id={P(pl, "id")} name={P(pl, "name")} playing={P(pl, "isPlaying")} hasKeyboard={P(c, "hasKeyboard")} hasMouse={P(c, "hasMouse")} joysticks={P(c, "joystickCount")}"); }
            var ctrls = re.GetProperty("controllers", S)?.GetValue(null);
            Log.Info($"rewired: keyboard={P(ctrls, "Keyboard") != null} joystickCount={P(ctrls, "joystickCount")}");
            foreach (var m in Objects.All.OfType<NMono>().Where(m => !m.Destroyed && m.Managed?.GetType().FullName is "Unify.Engagement" or "Unify.Input.ControllerReconnect"))
                Log.Info($"unify: {m.Managed.GetType().Name} go='{m.Go?.Name}' scene={m.Go?.Scene} active={m.Go?.ActiveInHierarchy} en={m.Enabled} state={P(m.Managed, "state")} action='{P(m.Managed, "engagementAction")}'");
            var ua = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "UnifyLibrary");
            Log.Info($"unify: GlobalAllowEngagement={ua?.GetType("Unify.Engagement")?.GetProperty("GlobalAllowEngagement", S)?.GetValue(null)} ControllerReconnect.IsVisible={ua?.GetType("Unify.Input.ControllerReconnect")?.GetProperty("IsVisible", S)?.GetValue(null)} isSuspending={ua?.GetType("Unify.UnifyManager")?.GetField("isSuspending", S)?.GetValue(null) ?? ua?.GetType("Unify.UnifyManager")?.GetProperty("isSuspending", S)?.GetValue(null)} isPaused={ua?.GetType("Unify.UnifyManager")?.GetField("isPaused", S)?.GetValue(null) ?? ua?.GetType("Unify.UnifyManager")?.GetProperty("isPaused", S)?.GetValue(null)} nextSlot={ua?.GetType("Unify.UserHelper")?.GetMethod("GetNextOpenPlayerSlot", S)?.Invoke(null, null)}");
        }
        catch (Exception e) { Log.Info("rewired probe: " + e.GetBaseException().Message); }
    }

    public static void DumpUi()
    {
        Log.Info("=== UI diagnostics ===");
        DumpRewired();
        if (Environment.GetEnvironmentVariable("SHEPHERD_UITREE") is string tree)
            foreach (var cv in Objects.All.OfType<NBehaviour>().Where(b => b.Managed is Canvas && !b.Destroyed && b.Go?.Name == tree)) DumpTree(cv.Go.Transform, 0);
        foreach (var cv in Objects.All.OfType<NBehaviour>().Where(b => b.Managed is Canvas && !b.Destroyed && b.Go != null && b.Go.Scene != 0))
        {
            bool root = Impl.RenderIcalls.RootCanvasOf(cv.Go) == cv; if (!root) continue;
            var crs = Scenes.Walk(cv.Go).SelectMany(g => g.Components).OfType<NCanvasRenderer>().ToList();
            int withMesh = crs.Count(c => c.Idx.Length > 0), visible = crs.Count(c => c.Idx.Length > 0 && c.Go.ActiveInHierarchy && !c.Cull);
            Log.Info($"canvas '{cv.Go.Name}' scene={Scenes.All.GetValueOrDefault(cv.Go.Scene)?.Name} active={cv.Go.ActiveInHierarchy} enabled={cv.Enabled} mode={cv.Get("renderMode")} order={cv.Get("sortingOrder")} scale={cv.Get("scaleFactor")} alpha={Impl.RenderIcalls.UiAlpha(cv.Go):F2} renderers={crs.Count} withMesh={withMesh} visibleWithMesh={visible}");
            foreach (var c in crs.Where(c => c.Idx.Length > 0 && c.Go.ActiveInHierarchy).Take(6))
                Log.Info($"    '{c.Go.Name}' tris={c.Idx.Length / 3} color={c.Color} alpha={Impl.RenderIcalls.UiAlpha(c.Go):F2} tex={c.Texture?.Name}({c.Texture?.Width}x{c.Texture?.Height}) mat={(c.Materials.Length > 0 ? c.Materials[0]?.ShaderName : null)}");
        }
        var tmps = Objects.All.OfType<NMono>().Where(m => !m.Destroyed && m.Managed.GetType().Name.StartsWith("TextMeshPro") && m.Go.ActiveInHierarchy && m.Go.Scene != 0).ToList();
        Log.Info($"TMP texts active: {tmps.Count}; with CanvasRenderer mesh: {tmps.Count(t => t.Go.Components.OfType<NCanvasRenderer>().Any(c => c.Idx.Length > 0))}");
        foreach (var t in tmps.Take(8))
        {
            string text = null; try { text = (string)t.Managed.GetType().GetProperty("text")?.GetValue(t.Managed); } catch { }
            var cr = t.Go.Components.OfType<NCanvasRenderer>().FirstOrDefault();
            Log.Info($"    TMP '{t.Go.Name}' text='{text?.Replace("\n", " ")[..Math.Min(40, text?.Length ?? 0)]}' tris={(cr?.Idx.Length ?? 0) / 3} tex={cr?.Texture?.Name}({cr?.Texture?.Width}x{cr?.Texture?.Height}) mat={(cr?.Materials.Length > 0 ? cr.Materials[0]?.ShaderName : null)} alpha={Impl.RenderIcalls.UiAlpha(t.Go):F2} color={cr?.Color}");
        }
        var tmp0 = tmps.FirstOrDefault(t => t.Go.Name == "Press Any Button") ?? tmps.FirstOrDefault();
        if (tmp0 != null)
        {
            var ty = tmp0.Managed.GetType();
            object G(string n) { try { return ty.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tmp0.Managed); } catch (Exception e) { return "EXC " + (e.InnerException ?? e).Message; } }
            var fa = ty.GetProperty("font")?.GetValue(tmp0.Managed); var ft = fa?.GetType();
            object F(string n) { try { return ft?.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(fa) ?? ft?.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(fa); } catch (Exception e) { return "EXC " + (e.InnerException ?? e).Message; } }
            int Cnt(object o) => o is System.Collections.ICollection c ? c.Count : -1;
            Log.Info($"    font asset: mode={F("atlasPopulationMode")} chars={Cnt(F("characterTable"))} glyphs={Cnt(F("glyphTable"))} lookup={Cnt(F("characterLookupTable"))} atlases={Cnt(F("atlasTextures"))} faceInfo.pointSize={(F("faceInfo") is { } fi ? fi.GetType().GetProperty("pointSize")?.GetValue(fi) : null)} sourceFont={F("sourceFontFile")}");
            var ti = ty.GetProperty("textInfo")?.GetValue(tmp0.Managed); Log.Info($"    textInfo.characterCount={ti?.GetType().GetField("characterCount")?.GetValue(ti)} pageCount={ti?.GetType().GetField("pageCount")?.GetValue(ti)}");
            Log.Info($"    TMP probe '{tmp0.Go.Name}': font={G("font")} fontSharedMaterial={G("fontSharedMaterial")} canvas={G("canvas")} isActiveAndEnabled={G("isActiveAndEnabled")}");
            try { ty.GetMethod("ForceMeshUpdate", new[] { typeof(bool), typeof(bool) })?.Invoke(tmp0.Managed, new object[] { true, true }); var cr = tmp0.Go.Components.OfType<NCanvasRenderer>().FirstOrDefault(); Log.Info($"    after ForceMeshUpdate: tris={(cr?.Idx.Length ?? 0) / 3}"); }
            catch (Exception e) { Log.Exception(e, "TMP ForceMeshUpdate"); }
        }
        var sty = Objects.All.OfType<NMono>().FirstOrDefault(m => m.Managed.GetType().Name == "Stylizer");
        if (sty != null)
        {
            var o = sty.Managed; var t = o.GetType(); var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var fn in new[] { "Dither", "Pixelate", "EffectIntensity", "UseSecondPalette", "_lerpPalette", "_pixelScale", "UseColorMask", "Grain" }) Log.Info($"    Stylizer.{fn} = {t.GetField(fn, bf)?.GetValue(o)}");
            foreach (var pn in new[] { "_palette", "_palette2" })
            {
                var pal = t.GetField(pn, bf)?.GetValue(o); var cols = pal?.GetType().GetField("Colors")?.GetValue(pal) as System.Collections.IList;
                Log.Info($"    {pn} '{(pal as UnityEngine.Object)?.name}' mixed={pal?.GetType().GetField("MixedColorCount")?.GetValue(pal)} colors=[{string.Join(" ", cols?.Cast<Color>().Select(c => "#" + ColorUtility.ToHtmlStringRGB(c)) ?? Array.Empty<string>())}]");
            }
            var pat = t.GetField("_pattern", bf)?.GetValue(o); Log.Info($"    pattern={(pat as UnityEngine.Object)?.name} patternTex={(t.GetField("_patternTexture", bf)?.GetValue(o) as UnityEngine.Object)?.name}");
        }
        foreach (var cam in Impl.CameraIcalls.AllCameras()) Log.Info($"camera '{cam.Go.Name}' depth={cam.Get("depth")} clear={cam.Get("clearFlags")} ortho={cam.Get("orthographic")} target={cam.Get("targetTexture")} mask={cam.Get("cullingMask")}");
    }
    static int ResPaths(string[] args)
    {
        Assets.Loader.Init(); var q = (args.Length > 1 ? args[1] : "").ToLowerInvariant();
        Log.Info($"{Assets.Loader.ResourcePaths.Count} resource entries");
        foreach (var (p, ptr) in Assets.Loader.ResourcePaths.Where(e => e.path.Contains(q)).Take(40)) Log.Info($"  {p}  -> {ptr}");
        return 0;
    }
    static int DumpObj(string[] a)
    {
        Log.Echo = false; Assets.Loader.Init();
        var files = Assets.Loader.Db.LoadBundleFiles(a[1]); var f = files.First(x => x.Name.EndsWith(a[2]));
        var o = f.GetObject(long.Parse(a[3])); var t = Shepherd.Assets.TypeTreeReader.Read(f, o);
        Console.WriteLine($"{f.Name} #{a[3]} class {o.ClassId}"); Console.Write(t.ToPrettyString(a.Length > 4 ? int.Parse(a[4]) : 6)); return 0;
    }
    /// <summary>Find a Shader by name across the bundles and extract its compiled program blobs (Metal source on macOS).</summary>
    static int ShaderDump(string[] a)
    {
        Log.Echo = false; Assets.Loader.Init(); var want = a[1]; var outDir = Path.Combine(Program.Root, "runtime", "shaders"); Directory.CreateDirectory(outDir);
        foreach (var b in Directory.GetFiles(Path.Combine(Program.GameData, "StreamingAssets", "aa", "StandaloneOSX"), "*.bundle"))
        {
            IReadOnlyList<Shepherd.Assets.SerializedFile> files; try { files = Assets.Loader.Db.LoadBundleFiles(b); } catch { continue; }
            foreach (var f in files)
                foreach (var o in f.ObjectsOfClass(48))
                {
                    Shepherd.Assets.TypeTreeObject d; try { d = Shepherd.Assets.TypeTreeReader.Read(f, o); } catch { continue; }
                    var pf = d["m_ParsedForm"] as Shepherd.Assets.TypeTreeObject; var name = pf?["m_Name"] as string;
                    if (name != want) continue;
                    var blob = d["compressedBlob"] as byte[]; var offs = (d["offsets"] as System.Collections.IList)?.Cast<object>().ToList(); var clen = (d["compressedLengths"] as System.Collections.IList)?.Cast<object>().ToList(); var dlen = (d["decompressedLengths"] as System.Collections.IList)?.Cast<object>().ToList();
                    Console.WriteLine($"found '{name}' in {Path.GetFileName(b)}: blob={blob?.Length} platforms={(d["platforms"] as System.Collections.IList)?.Count}");
                    for (int i = 0; i < (offs?.Count ?? 0); i++)
                    {
                        // per platform: lists of per-tier offsets/lengths (nested in 2022)
                        static List<object> Flat(object x) => x is System.Collections.IList l ? l.Cast<object>().SelectMany(Flat).ToList() : x is Shepherd.Assets.TypeTreeObject t ? t.SelectMany(kv => Flat(kv.Value)).ToList() : new List<object> { x };
                        var oo = Flat(offs[i]); var cc = Flat(clen[i]); var dd = Flat(dlen[i]);
                        for (int k = 0; k < oo.Count; k++)
                        {
                            int off = Convert.ToInt32(oo[k]), cl = Convert.ToInt32(cc[k]), dl = Convert.ToInt32(dd[k]);
                            var dst = new byte[dl]; Shepherd.Assets.Compression.Lz4.Decode(blob.AsSpan(off, cl), dst);
                            var path = Path.Combine(outDir, $"{name.Replace('/', '_')}_{i}_{k}.bin"); File.WriteAllBytes(path, dst);
                            Console.WriteLine($"  platform {i} tier {k}: {dl} bytes -> {path}");
                        }
                    }
                    return 0;
                }
        }
        Console.WriteLine("not found"); return 1;
    }
    static int NativeTest()
    {
        var p = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Program.GameData)), "PlugIns", "fmodstudio.bundle", "Contents", "MacOS", "fmodstudio");
        try { var h = System.Runtime.InteropServices.NativeLibrary.Load(p); Log.Info($"loaded fmodstudio OK: {h}"); }
        catch (Exception e) { Log.Error($"load failed: {e.Message}"); }
        return 0;
    }
    static int Unknown(string m) { Log.Error($"unknown mode {m}"); return 2; }

    static int Smoke()
    {
        Runtime.Time.CaptureDeltaTime = Runtime.Time.FrameClockDt;   // physics stage 0: SHEPHERD_CAPTURE_DT (unset: 1f / 60f, as before)
        var root = new GameObject("Root");
        root.transform.position = new Vector3(1, 2, 3);
        var child = new GameObject("Child"); child.transform.SetParent(root.transform, false);
        child.transform.localPosition = new Vector3(0, 1, 0);
        root.transform.rotation = Quaternion.Euler(0, 90, 0);
        Debug.Log($"child world pos = {child.transform.position} (expect (1.0, 3.0, 3.0)); child local = {child.transform.localPosition}");
        Debug.Log($"root euler = {root.transform.eulerAngles} (expect (0.0, 90.0, 0.0)); forward = {root.transform.forward}");
        var probe = root.AddComponent<SmokeProbe>();
        var got = root.GetComponent<SmokeProbe>();
        Debug.Log($"GetComponent<T> fast path returns the same object: {ReferenceEquals(got, probe)}; id={probe.GetInstanceID()} bool={(bool)probe}");
        var clone = UObject.Instantiate(root);
        Debug.Log($"clone: '{clone.name}' children={clone.transform.childCount} probe.copied={clone.GetComponent<SmokeProbe>().serializedValue}");
        for (int i = 0; i < 90; i++) Lifecycle.Frame();
        UObject.Destroy(clone);
        Lifecycle.Frame();
        Debug.Log($"after Destroy: clone == null -> {clone == null}; frames={Runtime.Time.FrameCount} t={UnityEngine.Time.time:F2}");
        return 0;
    }
}

public class SmokeProbe : MonoBehaviour
{
    public int serializedValue = 7; int _frames;
    void Awake() { serializedValue++; Debug.Log($"SmokeProbe.Awake on '{name}' (value {serializedValue})"); }
    void Start() { Debug.Log($"SmokeProbe.Start on '{name}'"); StartCoroutine(Co()); }
    void Update() => _frames++;
    IEnumerator Co()
    {
        Debug.Log($"co[{name}] step 1 t={UnityEngine.Time.time:F2}");
        yield return new WaitForSeconds(0.5f);
        Debug.Log($"co[{name}] step 2 after 0.5s: t={UnityEngine.Time.time:F2} updates={_frames}");
        yield return StartCoroutine(Inner());
        Debug.Log($"co[{name}] done");
    }
    IEnumerator Inner() { yield return null; Debug.Log($"inner coroutine ran on '{name}'"); }
    void OnDestroy() => Debug.Log($"SmokeProbe.OnDestroy on '{name}'");
}
