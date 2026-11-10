using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UTime = Shepherd.Engine.Runtime.Time;
namespace Shepherd.Engine.Impl;

static class AppIcalls
{
    public static int ScreenW = Profile.Vita ? Profile.VitaWidth : 1280, ScreenH = Profile.Vita ? Profile.VitaHeight : 720;   // vita profile: the Vita panel, 960x544
    public static string Persistent => Profile.PersistentOverride ?? Path.Combine(Program.Root, "runtime", "persistent");   // NEVER the user's real CotL save dir
    static System.Random _rng = new(1234); static int _seed = 1234;

    // ---- Debug / logging ----
    [Icall("UnityEngine.DebugLogHandler::Internal_Log")] static object LogI(object[] a)
    {
        var type = Convert.ToInt32(a[0]); var msg = (string)a[2]; var ctx = Objects.Get(a[3]);
        var tag = type switch { 0 => "UER", 1 => "UAS", 2 => "UWR", 4 => "UEX", _ => "ULG" };   // Error, Assert, Warning, Log, Exception
        if (type == 0 || type == 4) Log.Errors++;
        Log.Write(tag, ctx != null ? $"{msg}  [ctx:{ctx.Name}]" : msg, !LogDiet.RingOnly(type));   // kit #8: SHEPHERD_K8_LOG_DIET=ring keeps Log / Warning out of engine.log
        SceneStop.OnGameLog(msg);   // kit #4: the New Game scope ends at the intro's announcement (Runtime/SceneStop.cs)
        Diag.MenuClock.OnGameLog(msg);   // kit #13 bootc: the screens the game announces, for the pad scripts' @Screen waits (nothing unless a script has one)
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.OnGameLog(msg);   // kit #7 tests review fix: SHEPHERD_TEST_SPINE binds only after the game created its DispatchThread
#endif
        return null;
    }
    [Icall("UnityEngine.DebugLogHandler::Internal_LogException")] static object LogEx(object[] a) { Log.Exception((Exception)a[0], "Debug.LogException"); return null; }
    [Icall("UnityEngine.Debug::get_isDebugBuild")] static object IsDebug(object[] a) => false;
    [Icall("UnityEngine.Debug::get_developerConsoleVisible")] static object DevCon(object[] a) => false;
    [Icall("UnityEngine.Debug::get_developerConsoleEnabled")] static object DevConE(object[] a) => false;
    [Icall("UnityEngine.Debug::DrawLine_Injected")] static object DrawLine(object[] a) => null;
    [Icall("UnityEngine.Debug::ExtractStackTraceNoAlloc")] static object ExtractST(object[] a) => 0;
    [Icall("UnityEngine.Debug::get_diagnosticSwitches")] static object DiagSw(object[] a) => null;
    [Icall("UnityEngine.Application::SetLogCallbackDefined")] static object LogCb(object[] a) => null;
    [Icall("UnityEngine.Application::GetStackTraceLogType")] static object STType(object[] a) => StackTraceLogType.None;
    [Icall("UnityEngine.Application::SetStackTraceLogType")] static object SetSTType(object[] a) => null;
    [Icall("UnityEngine.Application::get_stackTraceLogType")] static object STType2(object[] a) => StackTraceLogType.None;

    // ---- Time ----
    // kit3 play mode (Runtime/Time.cs): in the wall clock, inside FixedUpdate time / unscaledTime / unscaledDeltaTime are the fixed ones, as
    // Unity documents (Time.time "returns Time.fixedTime" there); the frame clock keeps the reference timeline's values (UTime.Wall = false)
    static bool WallFixed => UTime.InFixedStep && (UTime.Wall || FrameFixed);
    // physics Stage 3 (plan section 3 "Time fixes", SHEPHERD_FIXEDTIME): the same on the frame clock where Stage 3 runs (Lifecycle keeps the fixed
    // unscaled clock then); off elsewhere: the frame clock's reference timeline exactly as before
    static readonly bool FrameFixed = Shepherd.Engine.Physics.PhysTime.FrameFixed;
    [Icall("UnityEngine.Time::get_time")] static object T_(object[] a) => (float)(WallFixed ? UTime.FixedNow : UTime.Now);
    [Icall("UnityEngine.Time::get_timeAsDouble")] static object Td(object[] a) => WallFixed ? UTime.FixedNow : UTime.Now;
    static readonly Runtime.BoxCache DtBox = new(), FramesBox = new(), RtBox = new(), TsBox = new();   // stage 2: shared boxes (Runtime/Boxes.cs)
    [Icall("UnityEngine.Time::get_deltaTime")] static object Dt(object[] a) => DtBox.Of(UTime.InFixedStep ? UTime.FixedDeltaTime : UTime.DeltaTime);
    [Icall("UnityEngine.Time::get_unscaledDeltaTime")] static object UDt(object[] a) => UdtBox.Of(WallFixed ? UTime.FixedUnscaledDeltaTime : UTime.UnscaledDeltaTime);   // (menu-speed: a box per changed value)
    static readonly Runtime.BoxCache UdtBox = new();
    [Icall("UnityEngine.Time::get_unscaledTime")] static object UT(object[] a) { float v = (float)(WallFixed ? UTime.FixedUnscaledNow : UTime.UnscaledNow); return Runtime.K9Memo.Float(UtBox, v); }   // (kit #9 memo: one box per value)
    static readonly Runtime.BoxCache UtBox = new();
    [Icall("UnityEngine.Time::get_unscaledTimeAsDouble")] static object UTd(object[] a) => WallFixed ? UTime.FixedUnscaledNow : UTime.UnscaledNow;
    [Icall("UnityEngine.Time::get_smoothDeltaTime")] static object SDt(object[] a) => UTime.SmoothDeltaTime;
    [Icall("UnityEngine.Time::get_fixedDeltaTime")] static object FDt(object[] a) => UTime.FixedDeltaTime;
    [Icall("UnityEngine.Time::set_fixedDeltaTime")] static object SetFDt(object[] a) { UTime.FixedDeltaTime = Math.Max(0.0001f, (float)a[0]); return null; }
    [Icall("UnityEngine.Time::get_fixedTime")] static object FT(object[] a) => (float)UTime.FixedNow;
    [Icall("UnityEngine.Time::get_fixedTimeAsDouble")] static object FTd(object[] a) => UTime.FixedNow;
    [Icall("UnityEngine.Time::get_fixedUnscaledDeltaTime")] static object FUDt(object[] a) => UTime.Wall || FrameFixed ? UTime.FixedUnscaledDeltaTime : UTime.FixedDeltaTime;
    [Icall("UnityEngine.Time::get_fixedUnscaledTime")] static object FUT(object[] a) => (float)(UTime.Wall || FrameFixed ? UTime.FixedUnscaledNow : UTime.FixedNow);
    [Icall("UnityEngine.Time::get_fixedUnscaledTimeAsDouble")] static object FUTd(object[] a) => UTime.Wall || FrameFixed ? UTime.FixedUnscaledNow : UTime.FixedNow;
    [Icall("UnityEngine.Time::get_frameCount")] static object Frames(object[] a) => FramesBox.Of(UTime.FrameCount);
    [Icall("UnityEngine.Time::get_renderedFrameCount")] static object RFrames(object[] a) => UTime.RenderedFrames;
    [Icall("UnityEngine.Time::get_realtimeSinceStartup")] static object RT(object[] a) => RtBox.Of((float)UTime.Realtime);
    [Icall("UnityEngine.Time::get_realtimeSinceStartupAsDouble")] static object RTd(object[] a) => UTime.Realtime;
    [Icall("UnityEngine.Time::get_timeScale")] static object TS(object[] a) => TsBox.Of(UTime.TimeScale);
    [Icall("UnityEngine.Time::set_timeScale")] static object SetTS(object[] a) { var v = Math.Max(0, (float)a[0]); if (v != UTime.TimeScale) Log.Info($"Time.timeScale {UTime.TimeScale} -> {v}  ({new System.Diagnostics.StackTrace(2).GetFrames().Take(4).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name).Aggregate((x, y) => x + " < " + y)})"); UTime.TimeScale = v; return null; }
    [Icall("UnityEngine.Time::get_maximumDeltaTime")] static object MDt(object[] a) => UTime.MaximumDeltaTime;
    [Icall("UnityEngine.Time::set_maximumDeltaTime")] static object SetMDt(object[] a) { UTime.MaximumDeltaTime = (float)a[0]; return null; }
    [Icall("UnityEngine.Time::get_maximumParticleDeltaTime")] static object MPDt(object[] a) => 0.03f;
    [Icall("UnityEngine.Time::set_maximumParticleDeltaTime")] static object SetMPDt(object[] a) => null;
    [Icall("UnityEngine.Time::get_timeSinceLevelLoad")] static object TSLL(object[] a) => (float)(UTime.Now - UTime.LevelLoadAt);
    [Icall("UnityEngine.Time::get_timeSinceLevelLoadAsDouble")] static object TSLLd(object[] a) => UTime.Now - UTime.LevelLoadAt;
    [Icall("UnityEngine.Time::get_inFixedTimeStep")] static object InFixed(object[] a) => UTime.InFixedStep;
    [Icall("UnityEngine.Time::get_captureDeltaTime")] static object CapDt(object[] a) => UTime.CaptureDeltaTime;
    [Icall("UnityEngine.Time::set_captureDeltaTime")] static object SetCapDt(object[] a) { UTime.CaptureDeltaTime = (float)a[0]; return null; }

    // ---- Application ----
    [Icall("UnityEngine.Application::get_isPlaying")] static object Playing(object[] a) => Runtime.K9Memo.Bool(true);
    [Icall("UnityEngine.Application::IsPlaying")] static object Playing2(object[] a) => true;
    [Icall("UnityEngine.Application::get_isFocused")] static object Focused(object[] a) => true;
    [Icall("UnityEngine.Application::get_isBatchMode")] static object Batch(object[] a) => false;
    [Icall("UnityEngine.Application::get_isLoadingLevel")] static object Loading(object[] a) => false;
    [Icall("UnityEngine.Application::get_runInBackground")] static object RunBg(object[] a) => Runtime.K9Memo.Bool(true);   // kit #9 E5 MIRROR: while Shepherd.Hooks.Mirror.On the stub answers Mirror.RunInBackground (true) itself (tools/Rewriter/TypedPass.cs) -- change this body and you must change that formula (Runtime/K9Typed.cs binds the mirror only while THIS method is the key's hook; SHEPHERD_K9_MIRROR=check compares them)
    [Icall("UnityEngine.Application::set_runInBackground")] static object SetRunBg(object[] a) => null;
    [Icall("UnityEngine.Application::get_platform")] static object Platform(object[] a) => RuntimePlatform.OSXPlayer;
    [Icall("UnityEngine.Application::get_dataPath")] static object DataPath(object[] a) => Program.GameData;
    [Icall("UnityEngine.Application::get_streamingAssetsPath")] static object Streaming(object[] a) => Path.Combine(Program.GameData, "StreamingAssets");
    [Icall("UnityEngine.Application::get_persistentDataPath")] static object PersistentI(object[] a) { Directory.CreateDirectory(Persistent); return Persistent; }
    [Icall("UnityEngine.Application::get_temporaryCachePath")] static object TempPath(object[] a) { var p = Path.Combine(Program.Root, "runtime", "cache"); Directory.CreateDirectory(p); return p; }
    [Icall("UnityEngine.Application::get_consoleLogPath")] static object ConsoleLog(object[] a) => Path.Combine(Program.LogDir, "engine.log");
    [Icall("UnityEngine.Application::get_unityVersion")] static object UVer(object[] a) => "2022.3.62f2";
    [Icall("UnityEngine.Application::get_unityVersionMaj")] static object UVerMaj(object[] a) => 2022;
    [Icall("UnityEngine.Application::get_unityVersionMin")] static object UVerMin(object[] a) => 3;
    [Icall("UnityEngine.Application::get_unityVersionVer")] static object UVerVer(object[] a) => 62;
    [Icall("UnityEngine.Application::get_version")] static object Ver(object[] a) => "1.5.25";
    [Icall("UnityEngine.Application::get_productName")] static object Product(object[] a) => "Cult Of The Lamb";
    [Icall("UnityEngine.Application::get_companyName")] static object Company(object[] a) => "Massive Monster";
    [Icall("UnityEngine.Application::get_identifier")] static object Ident(object[] a) => "com.massivemonster.cultofthelamb";
    [Icall("UnityEngine.Application::get_buildGUID")] static object Guid_(object[] a) => "00000000000000000000000000000000";
    [Icall("UnityEngine.Application::get_cloudProjectId")] static object Cloud(object[] a) => "";
    [Icall("UnityEngine.Application::get_installerName")] static object Installer(object[] a) => "";
    [Icall("UnityEngine.Application::get_installMode")] static object InstallMode(object[] a) => ApplicationInstallMode.Store;
    [Icall("UnityEngine.Application::get_sandboxType")] static object Sandbox(object[] a) => ApplicationSandboxType.NotSandboxed;
    [Icall("UnityEngine.Application::get_genuine")] static object Genuine(object[] a) => true;
    [Icall("UnityEngine.Application::get_genuineCheckAvailable")] static object GenuineChk(object[] a) => false;
    [Icall("UnityEngine.Application::get_internetReachability")] static object Reach(object[] a) => NetworkReachability.NotReachable;   // keeps analytics/online paths quiet
    [Icall("UnityEngine.Application::get_systemLanguage")] static object Lang(object[] a) => SystemLanguage.English;
    [Icall("UnityEngine.Application::get_targetFrameRate")] static object TFR(object[] a) => UTime.TargetFrameRate;
    [Icall("UnityEngine.Application::set_targetFrameRate")] static object SetTFR(object[] a) { UTime.TargetFrameRate = NativeDisplay.Capped ? NativeDisplay.ClampTargetFrameRate((int)a[0]) : (int)a[0]; return null; }   // k4-display: the panel / lock clamp
    // k4-display: QualitySettings.vSyncCount (was the inert stub: 1, setter ignored): capped = the vblanks per frame the engine really uses
    [Icall("UnityEngine.QualitySettings::get_vSyncCount")] static object VSync(object[] a) => NativeDisplay.Capped ? NativeDisplay.VSyncCount : 1;
    [Icall("UnityEngine.QualitySettings::set_vSyncCount")] static object SetVSync(object[] a) { if (NativeDisplay.Capped) NativeDisplay.SetVSyncCount((int)a[0]); return null; }
    // kit #10 boot track (Runtime/K10Load.cs): the game's priority is kept -- Unity answers what was set and slices the loads by it (kit #9: always
    // Normal, the setter ignored; SHEPHERD_K10_LOADPRIO=0 = that again)
    [Icall("UnityEngine.Application::get_backgroundLoadingPriority")] static object BgPrio(object[] a) => Runtime.K10Load.Mode == 0 ? UnityEngine.ThreadPriority.Normal : (UnityEngine.ThreadPriority)Runtime.K10Load.Priority;
    [Icall("UnityEngine.Application::set_backgroundLoadingPriority")] static object SetBgPrio(object[] a) { if (Runtime.K10Load.Mode != 0) Runtime.K10Load.Priority = Convert.ToInt32(a[0]); return null; }
    [Icall("UnityEngine.Application::get_submitAnalytics")] static object Analytics(object[] a) => false;
    [Icall("UnityEngine.Application::get_isHumanControllingUs")] static object Human(object[] a) => true;
    [Icall("UnityEngine.Application::get_isTestRun")] static object TestRun(object[] a) => false;
    [Icall("UnityEngine.Application::get_absoluteURL")] static object AbsUrl(object[] a) => "";
    [Icall("UnityEngine.Application::HasProLicense")] static object Pro(object[] a) => true;
    [Icall("UnityEngine.Application::HasARGV")] static object HasArgv(object[] a) => false;
    [Icall("UnityEngine.Application::GetValueForARGV")] static object Argv(object[] a) => null;
    [Icall("UnityEngine.Application::OpenURL")] static object OpenUrl(object[] a) { Log.Info($"OpenURL suppressed: {a[0]}"); return null; }
    [Icall("UnityEngine.Application::Quit")] static object Quit(object[] a) { Log.Info("Application.Quit() requested by game code — ignored (close the window to exit)"); return null; }
    [Icall("UnityEngine.Application::CanStreamedLevelBeLoaded")] static object CanLoad(object[] a) => true;
    [Icall("UnityEngine.Application::GetBuildTags")] static object Tags(object[] a) => Array.Empty<string>();
    [Icall("UnityEngine.Application::HasUserAuthorization")] static object UserAuth(object[] a) => true;

    // ---- Screen ----
    [Icall("UnityEngine.Screen::get_width")] static object SW(object[] a) => SwBox.Of(ScreenW);   // (menu-speed: boxes per changed value)
    [Icall("UnityEngine.Screen::get_height")] static object SH(object[] a) => ShBox.Of(ScreenH);
    static readonly Runtime.BoxCache SwBox = new(), ShBox = new();
    [Icall("UnityEngine.Screen::get_dpi")] static object Dpi(object[] a) => 144f;
    // k4-display: the native display cap (Runtime/NativeDisplay.cs; Vita profile): one full-screen 960x544 @ 60 Hz mode, requests logged and kept
    [Icall("UnityEngine.Screen::get_fullScreen")] static object FS(object[] a) => Runtime.K9Memo.Bool(NativeDisplay.Capped);
    [Icall("UnityEngine.Screen::set_fullScreen")] static object SetFS(object[] a) { if (NativeDisplay.Capped) NativeDisplay.SetFullScreen((bool)a[0]); return null; }
    [Icall("UnityEngine.Screen::get_fullScreenMode")] static object FSM(object[] a) => NativeDisplay.Capped ? NativeDisplay.Mode : FullScreenMode.Windowed;
    [Icall("UnityEngine.Screen::set_fullScreenMode")] static object SetFSM(object[] a) { if (NativeDisplay.Capped) NativeDisplay.SetMode((FullScreenMode)a[0]); return null; }
    [Icall("UnityEngine.Screen::SetResolution_Injected")] static object SetRes(object[] a)
    {
        if (NativeDisplay.Capped) NativeDisplay.SetResolution((int)a[0], (int)a[1], (FullScreenMode)a[2]);
        else Log.Info($"Screen.SetResolution({a[0]}x{a[1]}) ignored; window is {ScreenW}x{ScreenH}");
        return null;
    }
    static Resolution Res() { if (NativeDisplay.Capped) return NativeDisplay.Native(); var r = new Resolution(); r.width = ScreenW; r.height = ScreenH; return r; }
    [Icall("UnityEngine.Screen::get_currentResolution_Injected")] static object CurRes(object[] a) { a[0] = Res(); return null; }
    [Icall("UnityEngine.Screen::get_resolutions")] static object Resolutions(object[] a) => new[] { Res() };
    [Icall("UnityEngine.Screen::get_safeArea_Injected")] static object Safe(object[] a) { a[0] = new Rect(0, 0, ScreenW, ScreenH); return null; }
    [Icall("UnityEngine.Screen::get_cutouts")] static object Cutouts(object[] a) => Array.Empty<Rect>();
    [Icall("UnityEngine.Screen::GetScreenOrientation")] static object Orient(object[] a) => ScreenOrientation.LandscapeLeft;
    [Icall("UnityEngine.Screen::get_sleepTimeout")] static object Sleep(object[] a) => -1;
    [Icall("UnityEngine.Screen::set_sleepTimeout")] static object SetSleep(object[] a) => null;
    [Icall("UnityEngine.Screen::get_brightness")] static object Bright(object[] a) => 1f;
    [Icall("UnityEngine.Screen::GetMainWindowPosition_Injected")] static object WinPos(object[] a) { a[0] = Vector2Int.zero; return null; }

    // ---- Random ---- (kit #7 tests track: Unity's own xorshift128 generator, Icalls/UnityRandom.cs -- kit #9 fidelity: ON by default under the
    // Vita profile; SHEPHERD_UNITY_RANDOM=0 = System.Random as kit #8. The Mac build counts the calls for the state hash, Diag/StateHash.cs)
#if SHEPHERD_EBOOT
    static System.Random R => _rng;
    static void Counted() { }
#else
    internal static long RngCalls; internal static int RngSeed => _seed;
    static System.Random R { get { RngCalls++; if (Diag.K11Counters.RngSites) Diag.K11Counters.RngSite(); return _rng; } }
    static void Counted() { RngCalls++; if (Diag.K11Counters.RngSites) Diag.K11Counters.RngSite(); }   // (kit #11 measure: SHEPHERD_K11_COUNTERS=...,rngsites -- the calling method)
#endif
    static readonly bool UR = UnityRandom.On;
    [Icall("UnityEngine.Random::InitState")] static object Init(object[] a) { _seed = (int)a[0]; if (UR) UnityRandom.InitState(_seed); else _rng = new System.Random(_seed); return null; }
    // kit #9 fidelity, second review (section 13; the oracle probe vita/tests/oracle/k9-seed-mpb, both players): Unity's Random.seed getter is
    // the generator's current first state word (state.s0), which every draw moves -- with Unity's generator on, so is this one (kit #8 and
    // SHEPHERD_UNITY_RANDOM=0: the last InitState / set_seed argument)
    [Icall("UnityEngine.Random::get_seed")] static object Seed(object[] a) => UR ? UnityRandom.Seed : _seed;
    [Icall("UnityEngine.Random::set_seed")] static object SetSeed(object[] a) => Init(a);
    [Icall("UnityEngine.Random::get_value")] static object Value(object[] a) { if (UR) { Counted(); return UnityRandom.Value(); } return (float)R.NextDouble(); }
    [Icall("UnityEngine.Random::Range")] static object RangeF(object[] a) { float mn = (float)a[0], mx = (float)a[1]; if (UR) { Counted(); return UnityRandom.Range(mn, mx); } return mn + (float)R.NextDouble() * (mx - mn); }
    [Icall("UnityEngine.Random::RandomRangeInt")] static object RangeI(object[] a) { int mn = (int)a[0], mx = (int)a[1]; if (UR) { Counted(); return UnityRandom.RangeInt(mn, mx); } return mn < mx ? R.Next(mn, mx) : mx < mn ? R.Next(mx + 1, mn + 1) : mn; }
    [Icall("UnityEngine.Random::GetRandomUnitCircle")] static object Circle(object[] a) { if (UR) { Counted(); a[0] = UnityRandom.InsideUnitCircle(); return null; } double t = R.NextDouble() * Math.PI * 2, r = Math.Sqrt(R.NextDouble()); a[0] = new Vector2((float)(Math.Cos(t) * r), (float)(Math.Sin(t) * r)); return null; }
    static double D01() => R.NextDouble();
    static Vector3 OnSphere() { double z = D01() * 2 - 1, t = D01() * Math.PI * 2, r = Math.Sqrt(1 - z * z); return new Vector3((float)(r * Math.Cos(t)), (float)(r * Math.Sin(t)), (float)z); }
    [Icall("UnityEngine.Random::get_onUnitSphere_Injected")] static object OnS(object[] a) { if (UR) { Counted(); a[0] = UnityRandom.OnUnitSphere(); return null; } a[0] = OnSphere(); return null; }
    [Icall("UnityEngine.Random::get_insideUnitSphere_Injected")] static object InS(object[] a) { if (UR) { Counted(); a[0] = UnityRandom.InsideUnitSphere(); return null; } a[0] = OnSphere() * MathF.Cbrt((float)D01()); return null; }
    [Icall("UnityEngine.Random::get_rotation_Injected")] static object RRot(object[] a) { if (UR) { Counted(); a[0] = UnityRandom.Rotation(); return null; } var v = OnSphere(); a[0] = M.QNorm(new Quaternion(v.x, v.y, v.z, (float)D01() * 2 - 1)); return null; }
    [Icall("UnityEngine.Random::get_rotationUniform_Injected")] static object RRotU(object[] a) { if (UR) { Counted(); a[0] = UnityRandom.RotationUniform(); return null; } return RRot(a); }
    [Icall("UnityEngine.Random::get_state_Injected")] static object State(object[] a) { a[0] = UR ? UnityRandom.GetState() : default(UnityEngine.Random.State); return null; }
    [Icall("UnityEngine.Random::set_state_Injected")] static object SetState(object[] a) { if (UR && a[0] is UnityEngine.Random.State st) UnityRandom.SetState(st); return null; }

    // kit #9 track runtime (lever 10; Icalls/K9Reg.cs, Runtime/K9Typed.cs): the typed twin of realtimeSinceStartup (the same expression, no box).
    // The Time getters the engine's state answers alone are E5 mirrors instead (tools/Rewriter/TypedPass.cs: the stub reads Shepherd.Hooks.Mirror,
    // where Runtime/Time.cs keeps that state) -- K9Typed checks that each mirrored key is bound to the hook its formula copies (MirrorHooks).
    internal static void K9(K9Reg r)
    {
        r.Add("UnityEngine.Time::get_realtimeSinceStartup", (Func<float>)(() => (float)UTime.Realtime), RT);
    }
    /// <summary>kit #9 E5: the hook each mirrored getter's formula copies (K9Typed.BindMirrors refuses the mirrors unless each key is bound to it).</summary>
    internal static IEnumerable<(string key, Shepherd.Hooks.IcallImpl hook)> MirrorHooks() => new (string, Shepherd.Hooks.IcallImpl)[]
    {
        ("UnityEngine.Time::get_deltaTime", Dt), ("UnityEngine.Time::get_unscaledDeltaTime", UDt), ("UnityEngine.Time::get_time", T_),
        ("UnityEngine.Time::get_timeScale", TS), ("UnityEngine.Time::get_frameCount", Frames), ("UnityEngine.Time::get_fixedDeltaTime", FDt),
        ("UnityEngine.Time::get_inFixedTimeStep", InFixed),
    };
    /// <summary>kit #9 E5, check mode: each mirrored getter's formula over Shepherd.Hooks.Mirror (= tools/Rewriter/TypedPass.cs's IL) and the
    /// hook's answer, compared at every call while the stubs ask the engine (SHEPHERD_K9_MIRROR=check).</summary>
    /// <summary>kit #9 E5 (review fix): the constant mirrors of this class (key, the hook the stub copies, its answer) -- see RenderIcalls.K9MirrorConsts.</summary>
    internal static IEnumerable<(string key, Shepherd.Hooks.IcallImpl hook, object value)> K9MirrorConsts() => new (string, Shepherd.Hooks.IcallImpl, object)[]
    {
        ("UnityEngine.Application::get_runInBackground", RunBg, true),
    };
    internal static object MirrorFormula(string key)
    {
        var m = typeof(Shepherd.Hooks.Mirror); bool wallFixed = Shepherd.Hooks.Mirror.InFixedStep && (Shepherd.Hooks.Mirror.Wall || Shepherd.Hooks.Mirror.FrameFixed);
        return key switch
        {
            "UnityEngine.Time::get_deltaTime" => Shepherd.Hooks.Mirror.InFixedStep ? Shepherd.Hooks.Mirror.FixedDeltaTime : Shepherd.Hooks.Mirror.DeltaTime,
            "UnityEngine.Time::get_unscaledDeltaTime" => wallFixed ? Shepherd.Hooks.Mirror.FixedUnscaledDeltaTime : Shepherd.Hooks.Mirror.UnscaledDeltaTime,
            "UnityEngine.Time::get_time" => (float)(wallFixed ? Shepherd.Hooks.Mirror.FixedNow : Shepherd.Hooks.Mirror.Now),
            "UnityEngine.Time::get_timeScale" => Shepherd.Hooks.Mirror.TimeScale,
            "UnityEngine.Time::get_frameCount" => Shepherd.Hooks.Mirror.FrameCount,
            "UnityEngine.Time::get_fixedDeltaTime" => Shepherd.Hooks.Mirror.FixedDeltaTime,
            "UnityEngine.Time::get_inFixedTimeStep" => Shepherd.Hooks.Mirror.InFixedStep,
            _ => null
        };
    }
}
