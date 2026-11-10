using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UnityEngine.Video;
using UObject = UnityEngine.Object;
using Time = Shepherd.Engine.Runtime.Time;
namespace Shepherd.Engine.Impl;

/// <summary>Stand-in VideoPlayer: "plays" for a short fixed time, then stops and raises loopPointReached, so splash
/// sequences that wait on a video advance. (Real MP4 decode via AVFoundation can replace this later.)</summary>
public static class VideoIcalls
{
    public static float StandInSeconds = Profile.Vita ? 0f : 2f;   // vita profile: no video on the Vita — every clip "finishes" on the frame it starts
    static readonly MethodInfo LoopPoint = typeof(VideoPlayer).GetMethod("InvokeLoopPointReachedCallback_Internal", BindingFlags.Static | BindingFlags.NonPublic);
    static readonly MethodInfo Started = typeof(VideoPlayer).GetMethod("InvokeStartedCallback_Internal", BindingFlags.Static | BindingFlags.NonPublic);
    static readonly MethodInfo Prepared = typeof(VideoPlayer).GetMethod("InvokePrepareCompletedCallback_Internal", BindingFlags.Static | BindingFlags.NonPublic);
    static readonly HashSet<NObj> Playing = new();
    static NObj V(object o) => Objects.Get(o);
    static double Start(NObj v) => v.Get("__start") is double d ? d : -1;
    static bool IsPlaying(NObj v) => v != null && Playing.Contains(v) && Time.Now - Start(v) < StandInSeconds;

    /// <summary>Called once per frame by the player loop: finish stand-in playback and fire events.</summary>
    public static void Tick()
    {
        if (K6Engine.Video) { K6Tick(); return; }
        foreach (var v in Playing.ToArray())
        {
            if (v.Destroyed) { Playing.Remove(v); continue; }
            if (Time.Now - Start(v) >= StandInSeconds)
            {
                Playing.Remove(v); v.Set("__frame", (long)(StandInSeconds * 30));
                Log.Info($"VideoPlayer '{v.Name}': stand-in playback of '{v.Get("url") ?? v.Get("clip")}' finished");
                if (Diag.K7.BootOn) Diag.K7.Splash((v.Get("url") ?? (v.Get("clip") as UObject)?.name)?.ToString(), false, StandInSeconds);
                if (!(v.Get("isLooping") is true)) try { LoopPoint?.Invoke(null, new[] { v.Managed }); } catch (Exception e) { Log.Exception(e, "loopPointReached"); }
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: VideoPlayer events)
#endif
            }
        }
    }
    [Icall("UnityEngine.Video.VideoPlayer::Play")] static object Play(object[] a)
    {
        if (K6Engine.Video) return K6Play(a);
        var v = V(a[0]); if (v == null) return null; v.Set("__start", Time.Now); v.Set("__frame", 0L); Playing.Add(v);
        Log.Info($"VideoPlayer.Play '{v.Get("url") ?? (v.Get("clip") as UObject)?.name}' (stand-in {StandInSeconds}s)");
        if (K10Boot.On) K10Boot.FirstContent((v.Get("url") ?? (v.Get("clip") as UObject)?.name)?.ToString());   // kit #10: a fallback end of the boot screen (the game's first frame ends it first; kit #5's video path too)
        if (Diag.K7.BootOn) Diag.K7.Splash((v.Get("url") ?? (v.Get("clip") as UObject)?.name)?.ToString(), true, StandInSeconds);   // kit #7 (kit #5's video path)
        try { Prepared?.Invoke(null, new[] { a[0] }); Started?.Invoke(null, new[] { a[0] }); } catch (Exception e) { Log.Exception(e, "video started"); }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: VideoPlayer events)
#endif
        return null;
    }
    [Icall("UnityEngine.Video.VideoPlayer::Stop")] static object Stop(object[] a) { var v = V(a[0]); if (K6Engine.Video) { K6Stop(v); return null; } if (v != null) Playing.Remove(v); return null; }
    [Icall("UnityEngine.Video.VideoPlayer::Pause")] static object Pause(object[] a) { if (K6Engine.Video) { K6Pause(V(a[0])); return null; } return Stop(a); }
    [Icall("UnityEngine.Video.VideoPlayer::Prepare")] static object Prepare(object[] a)
    {
        if (K6Engine.Video) { K6Prepare(V(a[0])); return null; }
        try { Prepared?.Invoke(null, new[] { a[0] }); } catch { }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: VideoPlayer events)
#endif
        return null;
    }
    [Icall("UnityEngine.Video.VideoPlayer::get_isPlaying")] static object GetPlaying(object[] a) => K6Engine.Video ? St(V(a[0]))?.Phase is VPhase.Preparing or VPhase.Playing : IsPlaying(V(a[0]));
    [Icall("UnityEngine.Video.VideoPlayer::get_isPaused")] static object GetPaused(object[] a) => K6Engine.Video && St(V(a[0]))?.Phase == VPhase.Paused;
    [Icall("UnityEngine.Video.VideoPlayer::get_isPrepared")] static object GetPrepared(object[] a) => !K6Engine.Video || St(V(a[0]))?.Prepared == true;
    [Icall("UnityEngine.Video.VideoPlayer::get_frame")] static object Frame(object[] a)
    {
        var v = V(a[0]); if (K6Engine.Video) return K6Frame(v);
        if (v == null) return 0L; if (IsPlaying(v)) return (long)((Time.Now - Start(v)) * 30) + 1; return v.Get("__frame") is long f ? f : 0L;
    }
    [Icall("UnityEngine.Video.VideoPlayer::get_frameCount")] static object FrameCount(object[] a) => K6Engine.Video ? (ulong)(St(V(a[0]))?.Frames ?? 0) : (ulong)(StandInSeconds * 30);
    [Icall("UnityEngine.Video.VideoPlayer::get_time")] static object VTime(object[] a)
    {
        if (K6Engine.Video) return St(V(a[0])) is { } st ? Math.Min(st.Elapsed(), st.Length) : 0.0;
        var v = V(a[0]); return v == null || Start(v) < 0 ? 0.0 : Math.Min(StandInSeconds, Time.Now - Start(v));
    }
    [Icall("UnityEngine.Video.VideoPlayer::get_length")] static object Length(object[] a) => K6Engine.Video ? St(V(a[0]))?.Length ?? 0.0 : (double)StandInSeconds;
    [Icall("UnityEngine.Video.VideoPlayer::get_frameRate")] static object Fps(object[] a) => K6Engine.Video ? (float)(St(V(a[0]))?.Rate ?? 0.0) : 30f;
    [Icall("UnityEngine.Video.VideoPlayer::get_texture")] static object Tex(object[] a) => null;
    [Icall("UnityEngine.Video.VideoPlayer::get_width")] static object W(object[] a) => 1920u;
    [Icall("UnityEngine.Video.VideoPlayer::get_height")] static object H(object[] a) => 1080u;

    // ---------------- kit #6 (a): Unity's VideoPlayer timing (SHEPHERD_K6_VIDEO; K6Engine.Video) ----------------
    // Unity runs the video update in PostLateUpdate.UpdateVideo (after every LateUpdate; the player loop's own order) and raises the
    // VideoPlayer's events from there, never inside Play: Play / Prepare only request. The stand-in keeps that order -- prepareCompleted,
    // then started (the first frame is "presented"), and loopPointReached when the playhead reaches the clip's end -- on a clip clock that
    // follows the player's timeReference (Freerun, the default: real time, unclamped -- Time.Realtime -- so a clip runs on while timeScale is 0
    // as the intro's does and takes its length in real time at any frame rate; InternalTime: game time) and playbackSpeed. The clip's length is the media's: frames and rate from the owner-local table
    // SHEPHERD_VIDEO_LENGTHS (vita/tools/video_lengths.py: the game's own videos, never in git), looked up by the URL's file name or the
    // clip's name; a clip the table does not list keeps the profile's stand-in length (Vita 0 s: it ends in the video update of the frame it
    // starts in, as before; Mac 2 s). Kit #5 (SHEPHERD_K6_VIDEO=0) raised prepareCompleted and started inside Play and loopPointReached
    // before LateUpdate, with the profile's length for every clip: the intro's 0-s clip ended while the transition before it was still
    // fading out, the game started its next transition on top of it, and timeScale stayed 0 (vita/notes/kit6-engine.md).
    enum VPhase { Idle, Preparing, Playing, Paused, Ended }
    sealed class VState { public VPhase Phase; public bool Prepared, PrepareEvent, Listed; public double Start = -1, Held, Length, Rate = 30; public long Frames; public NObj V; public int TimeRef;
        // review fix: Freerun is the media's own real-time clock, so a clip takes its length in real time however long the frames are:
        // Time.Realtime (the unclamped wall clock; on the frame clock VirtualRealtime, which adds the same 1/60 s per frame as
        // UnscaledNow did, so frame-clock runs are unchanged). UnscaledNow is clamped at maximumDeltaTime (1/3 s) per frame: at the
        // device's ~2 fps in the intro the 13-s clip took ~19.5 s. InternalTime: game time.
        public double Clock() => TimeRef == 1 ? Time.Now : Time.Realtime;
        public double Elapsed() => Phase == VPhase.Playing ? Held + (Clock() - Start) * Speed() : Held;
        public double Speed() => V?.Get("playbackSpeed") is { } ps ? Convert.ToDouble(ps) : 1.0; }
    static readonly Dictionary<NObj, VState> States = new();
    static VState St(NObj v) => v != null && States.TryGetValue(v, out var s) ? s : null;
    static VState Ensure(NObj v)
    {
        if (!States.TryGetValue(v, out var s)) States[v] = s = new VState { V = v };
        s.TimeRef = v.Get("timeReference") is { } tr ? Convert.ToInt32(tr) : 0;
        if (K12BootSeq.On && K12BootSeq.SkipClip(ClipKey(v))) { s.Frames = 0; s.Rate = 30; s.Length = 0; s.Listed = false; Log.Info("[k12] boot sequence: '" + ClipKey(v) + "' is a 0-second clip (the launcher showed it): it ends in the video update it starts in"); }   // kit #12 boot
        else if (K6Engine.VideoLength(ClipKey(v)) is { } l) { s.Frames = l.Frames; s.Rate = l.Rate; s.Length = l.Frames / l.Rate; s.Listed = true; }
        else { s.Rate = 30; s.Length = StandInSeconds; s.Frames = (long)(StandInSeconds * 30); s.Listed = false; }
        return s;
    }
    /// <summary>The clip's name for the length table: the URL's file name without extension, else the VideoClip's name.</summary>
    static string ClipKey(NObj v)
    {
        if (v.Get("url") is string u && u.Length > 0) return System.IO.Path.GetFileNameWithoutExtension(u.Replace('\\', '/'));
        var c = v.Get("clip") ?? v.Get("videoClip");   // (the setter's key, else the serialized m_VideoClip)
        return (c as UObject)?.name ?? (Conv.ResolveRef?.Invoke(c, typeof(VideoClip)) as UObject)?.name;
    }
    static object K6Play(object[] a)
    {
        var v = V(a[0]); if (v == null) return null;
        var s = Ensure(v);
        if (s.Phase == VPhase.Paused) { s.Phase = VPhase.Playing; s.Start = s.Clock(); }   // resume where it was paused
        else if (s.Phase != VPhase.Playing && s.Phase != VPhase.Preparing) { s.Phase = VPhase.Preparing; s.Held = 0; s.Start = -1; s.PrepareEvent = !s.Prepared; }
        Log.Info("VideoPlayer.Play '" + ClipKey(v) + "' (Unity timing: " + s.Length + " s" + (s.Listed ? " from the length table" : ", the stand-in's (not in the length table)") + (s.TimeRef == 1 ? ", game time)" : ", real time)"));
        return null;
    }
    static void K6Stop(NObj v)
    {
        if (St(v) is { } s) { s.Phase = VPhase.Idle; s.Held = 0; s.Start = -1; }
#if !SHEPHERD_NO_K8
        K8Video.Stop(v);   // (kit #8: the real video's stream closes with it)
#endif
    }
    static void K6Pause(NObj v) { if (St(v) is { } s && s.Phase == VPhase.Playing) { s.Held = s.Elapsed(); s.Phase = VPhase.Paused; } }
    static void K6Prepare(NObj v) { if (v == null) return; var s = Ensure(v); if (!s.Prepared) s.PrepareEvent = true; }
    static object K6Frame(NObj v)
    {
        var s = St(v); if (s == null || s.Phase is VPhase.Idle or VPhase.Preparing) return -1L;   // Unity: -1 until a frame is available
        if (s.Phase == VPhase.Ended) return Math.Max(0L, s.Frames - 1);
        return Math.Min(Math.Max(0L, s.Frames - 1), (long)Math.Floor(s.Elapsed() * s.Rate));
    }
    static void Fire(MethodInfo m, NObj v, string what)
    {
        try { m?.Invoke(null, new[] { v.Managed }); } catch (Exception e) { Log.Exception(e, what); }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: VideoPlayer events)
#endif
    }
    /// <summary>PostLateUpdate.UpdateVideo (Lifecycle.Frame calls it after LateUpdate when K6Engine.Video is on).</summary>
    static void K6Tick()
    {
        if (States.Count == 0) return;
        var all = new VState[States.Count]; States.Values.CopyTo(all, 0);
        foreach (var s in all)
        {
            var v = s.V;
#if !SHEPHERD_NO_K8
            if (v.Destroyed) { States.Remove(v); K8Video.Stop(v); continue; }
#else
            if (v.Destroyed) { States.Remove(v); continue; }
#endif
            if (s.PrepareEvent) { s.PrepareEvent = false; s.Prepared = true; Fire(Prepared, v, "prepareCompleted"); }
#if !SHEPHERD_NO_K8
            // kit #8 review fix: the real video's clip opens BEFORE the clip's clock starts (Unity plays a prepared clip from its first frame): the
            // open reads the .shv into VRAM on this thread, and on the wall clock (the play apps) that read ran on the clip's own clock, so its
            // first ~15 frames were decoded in one burst and never shown. On the frame clock (vlx) Clock() is the frame's either way.
            if (s.Phase == VPhase.Preparing) { s.Prepared = true; s.Phase = VPhase.Playing; if (K10Boot.On) K10Boot.FirstContent(ClipKey(v)); if (K8Video.On && !(K12BootSeq.On && K12BootSeq.SkipClip(ClipKey(v)))) K8Video.Start(v, ClipKey(v)); s.Start = s.Clock(); s.Held = 0; if (Diag.K7.BootOn) Diag.K7.Splash(ClipKey(v), true, s.Length); Fire(Started, v, "started"); }   // (kit #12 boot: no decoder for a skipped clip)
            if (K8Video.On && s.Phase == VPhase.Playing) K8Video.Frame(v, (long)K6Frame(v));   // kit #8: the picture of this frame's clip time (the frame get_frame answers)
#else
            if (s.Phase == VPhase.Preparing) { s.Prepared = true; s.Phase = VPhase.Playing; if (K10Boot.On) K10Boot.FirstContent(ClipKey(v)); s.Start = s.Clock(); s.Held = 0; if (Diag.K7.BootOn) Diag.K7.Splash(ClipKey(v), true, s.Length); Fire(Started, v, "started"); }   // (kit #10: the boot screen's fallback end in the eboot apps too)
#endif
            if (s.Phase == VPhase.Playing && s.Elapsed() >= s.Length)
            {
                bool loop = (v.Get("isLooping") ?? v.Get("looping")) is true;   // (the setter's key, else the serialized m_Looping)
#if !SHEPHERD_NO_K8
                if (loop) { s.Held = 0; s.Start = s.Clock(); } else { s.Held = s.Length; s.Phase = VPhase.Ended; K8Video.Stop(v); }
#else
                if (loop) { s.Held = 0; s.Start = s.Clock(); } else { s.Held = s.Length; s.Phase = VPhase.Ended; }
#endif
                Log.Info("VideoPlayer '" + v.Name + "': '" + ClipKey(v) + "' reached its end (" + s.Length + " s)" + (loop ? ", looping" : ""));
                if (Diag.K7.BootOn) Diag.K7.Splash(ClipKey(v), false, s.Length);   // kit #7: the boot numbers (the Devolver logo only)
                Fire(LoopPoint, v, "loopPointReached");
            }
        }
    }
    // the VideoPlayer properties the stand-in reads (missing icalls in kit #5: AutoProps never saw VideoModule); bound by K6Engine.Install
    public static object SetUrl(object[] a) { if (V(a[0]) is { } v) v.Set("url", a[1] as string ?? ""); return null; }
    public static object GetUrl(object[] a) => V(a[0])?.Get("url") as string ?? "";
    public static object SetClip(object[] a) { if (V(a[0]) is { } v) v.Set("clip", a[1]); return null; }
    public static object GetClip(object[] a) => V(a[0])?.Get("clip") is { } c ? (c as UObject ?? Conv.ResolveRef?.Invoke(c, typeof(VideoClip))) : null;
    public static object SetSource(object[] a) { if (V(a[0]) is { } v) v.Set("source", Convert.ToInt32(a[1])); return null; }
    public static object GetSource(object[] a) => (VideoSource)(V(a[0])?.Get("source") is { } x ? Convert.ToInt32(x) : 0);
    public static object SetSpeed(object[] a)
    {
        var v = V(a[0]); if (v == null) return null;
        if (St(v) is { Phase: VPhase.Playing } s) { s.Held = s.Elapsed(); s.Start = s.Clock(); }   // the speed applies from now on
        v.Set("playbackSpeed", Convert.ToSingle(a[1])); return null;
    }
    public static object GetSpeed(object[] a) => V(a[0])?.Get("playbackSpeed") is { } x ? Convert.ToSingle(x) : 1f;
    public static object SetLooping(object[] a) { if (V(a[0]) is { } v) v.Set("isLooping", a[1] is true); return null; }
    public static object GetLooping(object[] a) => (V(a[0]) is { } v ? v.Get("isLooping") ?? v.Get("looping") : null) is true;

    // ---------------- misc render plumbing ----------------
    [Icall("UnityEngine.ScalableBufferManager::get_widthScaleFactor")] static object WScale(object[] a) => 1f;
    [Icall("UnityEngine.ScalableBufferManager::get_heightScaleFactor")] static object HScale(object[] a) => 1f;
    [Icall("UnityEngine.ScalableBufferManager::ResizeBuffers")] static object Resize(object[] a) => null;
    [Icall("UnityEngine.FrameTimingManager::CaptureFrameTimings")] static object Capture(object[] a) => null;
    [Icall("UnityEngine.FrameTimingManager::GetLatestTimings")] static object Timings(object[] a) => 0u;
    [Icall("UnityEngine.GeometryUtility::Internal_ExtractPlanes_Injected")] static object Planes(object[] a)
    {
        var planes = (Plane[])a[0]; var m = (Matrix4x4)a[1]; if (planes == null || planes.Length < 6) return null;
        if (!UmLayout.Ok) { OldPlanes(planes, m); return null; }
        // menu-speed hop fix: the same planes as our own arithmetic. Row k of m (GetRow) is (mk0, mk1, mk2, mk3); the six vectors are
        // r3 + r0, r3 - r0, r3 + r1, r3 - r1, r3 + r2, r3 - r2 (Vector4 +/-, per component, r3's first); len = n.magnitude =
        // (float)Math.Sqrt((double)(x * x + y * y + z * z)); new Plane(n / len, w / len) stores Vector3.Normalize(n / len) -- the
        // magnitude again, then (v / mag) if mag > 1e-5f else (0,0,0) -- and w / len; new Plane(Vector3.up, 0) stores (0/1, 1/1, 0/1) and 0.
        for (int i = 0; i < 6; i++)
        {
            int row = i >> 1; bool add = (i & 1) == 0;
            float ax = row == 0 ? m.m00 : row == 1 ? m.m10 : m.m20, ay = row == 0 ? m.m01 : row == 1 ? m.m11 : m.m21, az = row == 0 ? m.m02 : row == 1 ? m.m12 : m.m22, aw = row == 0 ? m.m03 : row == 1 ? m.m13 : m.m23;
            float x = add ? m.m30 + ax : m.m30 - ax, y = add ? m.m31 + ay : m.m31 - ay, z = add ? m.m32 + az : m.m32 - az, w = add ? m.m33 + aw : m.m33 - aw;
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            Plane pl;
            if (len > 1e-8f)
            {
                float nx = x / len, ny = y / len, nz = z / len, d = w / len;
                float mag = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                pl = mag > 1e-5f ? Um.ToPlaneRaw(Um.V3(nx / mag, ny / mag, nz / mag), d) : Um.ToPlaneRaw(Um.Zero3, d);
            }
            else { float up = (float)Math.Sqrt(0f * 0f + 1f * 1f + 0f * 0f); pl = Um.ToPlaneRaw(Um.V3(0f / up, 1f / up, 0f / up), 0f); }   // Normalize(Vector3.up), d = 0
            planes[i] = pl;
        }
        if (HopCheck.On) { var u = new Plane[6]; OldPlanes(u, m); for (int i = 0; i < 6; i++) HopCheck.Pl(HopCheck.Planes, planes[i], u[i]); }
        return null;
    }
    /// <summary>The extraction as it was (Unity's GetRow, Vector4 +/-, magnitude, Plane constructor): without the layout mirrors, and the
    /// SHEPHERD_HOPFIX_CHECK=1 reference.</summary>
    static void OldPlanes(Plane[] planes, Matrix4x4 m)
    {
        Vector4 r0 = m.GetRow(0), r1 = m.GetRow(1), r2 = m.GetRow(2), r3 = m.GetRow(3);
        Vector4[] p = { r3 + r0, r3 - r0, r3 + r1, r3 - r1, r3 + r2, r3 - r2 };
        for (int i = 0; i < 6; i++) { var n = new Vector3(p[i].x, p[i].y, p[i].z); float len = n.magnitude; planes[i] = len > 1e-8f ? new Plane(n / len, p[i].w / len) : new Plane(Vector3.up, 0); }
    }
    [Icall("UnityEngine.Hash128::Parse_Injected")] static object Hash(object[] a) { a[1] = default(Hash128); return null; }
    [Icall("UnityEngine.SystemInfo::GetCompatibleFormat")] static object CompatFmt(object[] a) => a[0];
    // polish round 2 (survey item 4): Graphics.Blit copies into the destination RenderTexture (Render/SceneRenderer.cs Blit; a material's
    // own shader is not run: the copy stands in for it) and, like Unity, leaves the destination as RenderTexture.active
    static NTexture T(object o) => Objects.Get<NTexture>(o);
    static object BlitTo(object src, object dst, Vector2 scale, Vector2 offset) { Render.SceneRenderer.Blit(T(src), T(dst), scale, offset); _activeRT = Objects.Get(dst); return null; }
    [Icall("UnityEngine.Graphics::Blit2")] static object Blit(object[] a) => BlitTo(a[0], a[1], Vector2.one, Vector2.zero);
    [Icall("UnityEngine.Graphics::Blit3")] static object Blit3(object[] a) => BlitTo(a[0], a[1], Vector2.one, Vector2.zero);
    [Icall("UnityEngine.Graphics::Blit4_Injected")] static object Blit4(object[] a) => BlitTo(a[0], a[1], (Vector2)a[2], (Vector2)a[3]);
    [Icall("UnityEngine.Graphics::Blit5_Injected")] static object Blit5(object[] a) => BlitTo(a[0], a[1], (Vector2)a[2], (Vector2)a[3]);
    [Icall("UnityEngine.Graphics::Internal_BlitMaterial5")] static object BlitMat5(object[] a) => BlitTo(a[0], a[1], Vector2.one, Vector2.zero);
    [Icall("UnityEngine.Graphics::Internal_BlitMaterial6")] static object BlitMat6(object[] a) => BlitTo(a[0], a[1], Vector2.one, Vector2.zero);
    [Icall("UnityEngine.Graphics::Internal_BlitMultiTap4")] static object BlitTap4(object[] a) => BlitTo(a[0], a[1], Vector2.one, Vector2.zero);
    [Icall("UnityEngine.Graphics::Internal_BlitMultiTap5")] static object BlitTap5(object[] a) => BlitTo(a[0], a[1], Vector2.one, Vector2.zero);
    /// <summary>GL.Clear: clears RenderTexture.active (kept pending until the target is used). With no active RT it would clear the back
    /// buffer, which the cameras overwrite when they draw the frame: nothing to do.</summary>
    [Icall("UnityEngine.GL::GLClear_Injected")] static object GLClear(object[] a)
    {
        if (a[1] is true && _activeRT is NTexture { IsRenderTexture: true } rt) Render.SceneRenderer.ClearRt(rt, (Color)a[2]);
        return null;
    }
    [Icall("UnityEngine.Texture2D::ReadPixelsImpl_Injected")] static object ReadPixels(object[] a) => null;
    static NObj _activeRT;
    [Icall("UnityEngine.RenderTexture::Internal_Create")] static object RtCreate(object[] a) { Objects.Bind((UObject)a[0], new NTexture { IsRenderTexture = true, Loaded = true, Width = 256, Height = 256 }); return null; }
    [Icall("UnityEngine.RenderTexture::GetActive")] static object RtActive(object[] a) => _activeRT?.Managed;
    [Icall("UnityEngine.RenderTexture::SetActive")] static object RtSetActive(object[] a) { _activeRT = Objects.Get(a[0]); return null; }
    [Icall("UnityEngine.RenderTexture::Create")] static object RtCreate2(object[] a) => true;
    [Icall("UnityEngine.RenderTexture::IsCreated")] static object RtIsCreated(object[] a) => true;
    [Icall("UnityEngine.RenderTexture::Release")] static object RtRelease(object[] a) { if (T(a[0]) is { } t) Render.SceneRenderer.ReleaseRt(t); return null; }
    [Icall("UnityEngine.RenderTexture::DiscardContents")] static object RtDiscard(object[] a) => null;
    [Icall("UnityEngine.RenderTexture::get_width")] static object RtW(object[] a) => Objects.Get<NTexture>(a[0])?.Width ?? 256;
    [Icall("UnityEngine.RenderTexture::set_width")] static object RtSetW(object[] a) { var t = Objects.Get<NTexture>(a[0]); if (t != null) t.Width = (int)a[1]; return null; }
    [Icall("UnityEngine.RenderTexture::get_height")] static object RtH(object[] a) => Objects.Get<NTexture>(a[0])?.Height ?? 256;
    [Icall("UnityEngine.RenderTexture::set_height")] static object RtSetH(object[] a) { var t = Objects.Get<NTexture>(a[0]); if (t != null) t.Height = (int)a[1]; return null; }
    [Icall("UnityEngine.RenderTexture::GetTemporary_Internal_Injected")] static object RtTemp(object[] a)
    {
        var d = (RenderTextureDescriptor)a[0]; var rt = (RenderTexture)Objects.NewManaged(typeof(RenderTexture));
        Objects.Bind(rt, new NTexture { IsRenderTexture = true, Loaded = true, Width = d.width, Height = d.height }); return rt;
    }
    [Icall("UnityEngine.RenderTexture::ReleaseTemporary")] static object RtRelTemp(object[] a) => null;
    // polish round 2: render-texture formats, so descriptors validate (StencilLighting_MaskEffect's RenderTextureDescriptor(w, h, Default, 16)
    // threw "graphicsFormat and depthStencilFormat cannot both be None"). Every colour target is RGBA8 on the backends; the format is kept
    // only to be read back.
    static readonly Dictionary<NObj, int> _rtFormat = new();
    [Icall("UnityEngine.RenderTexture::SetColorFormat")] static object RtSetFmt(object[] a) { if (Objects.Get(a[0]) is { } n) _rtFormat[n] = Convert.ToInt32(a[1]); return null; }
    [Icall("UnityEngine.RenderTexture::GetColorFormat")] static object RtGetFmt(object[] a) => (UnityEngine.Experimental.Rendering.GraphicsFormat)(Objects.Get(a[0]) is { } n && _rtFormat.TryGetValue(n, out var f) ? f : 8);
    [Icall("UnityEngine.RenderTexture::SetMipMapCount")] static object RtSetMips(object[] a) => null;
    [Icall("UnityEngine.RenderTexture::SetSRGBReadWrite")] static object RtSetSrgb(object[] a) => null;
    /// <summary>Unity's mapping of the legacy RenderTextureFormat to a GraphicsFormat (8-bit formats: sRGB when asked).</summary>
    [Icall("UnityEngine.Experimental.Rendering.GraphicsFormatUtility::GetGraphicsFormat_Native_RenderTextureFormat")]
    static object GfxFmtRt(object[] a)
    {
        bool srgb = a[1] is true;
        int g = Convert.ToInt32(a[0]) switch
        {
            0 or 7 => srgb ? 4 : 8,        // ARGB32, Default -> R8G8B8A8_SRGB / _UNorm
            20 => srgb ? 57 : 59,          // BGRA32 -> B8G8R8A8
            2 or 9 => 48,                  // ARGBHalf, DefaultHDR -> R16G16B16A16_SFloat
            11 => 52,                      // ARGBFloat -> R32G32B32A32_SFloat
            4 => 69, 5 => 67, 6 => 72,     // RGB565, ARGB4444, ARGB1555
            8 => 78,                       // ARGB2101010 -> A2R10G10B10_UNormPack32
            10 => 24,                      // ARGB64 -> R16G16B16A16_UNorm
            12 => 50, 13 => 46, 14 => 49, 15 => 45, 16 => 5, 22 => 74, 25 => 6, 28 => 21,   // RGFloat, RGHalf, RFloat, RHalf, R8, RGB111110Float, RG16, R16
            1 or 3 => 0,                   // Depth, Shadowmap: no colour format
            _ => 8,
        };
        return (UnityEngine.Experimental.Rendering.GraphicsFormat)g;
    }
    [Icall("UnityEngine.Experimental.Rendering.GraphicsFormatUtility::GetDepthStencilFormatFromBitsLegacy_Native")]
    static object DepthFmt(object[] a) { int b = Convert.ToInt32(a[0]); return (UnityEngine.Experimental.Rendering.GraphicsFormat)(b <= 0 ? 0 : b <= 16 ? 90 : 92); }   // None, D16_UNorm, D24_UNorm_S8_UInt
    [Icall("UnityEngine.Experimental.Rendering.GraphicsFormatUtility::GetDepthBits")]
    static object DepthBits(object[] a) => Convert.ToInt32(a[0]) switch { 90 or 151 => 16, 91 or 92 => 24, 93 or 94 => 32, _ => 0 };
    static bool IsDepthFmt(object f) => Convert.ToInt32(f) is (>= 90 and <= 94) or 142 or 143 or 151;
    [Icall("UnityEngine.Experimental.Rendering.GraphicsFormatUtility::IsDepthFormat")] static object IsDepth(object[] a) => IsDepthFmt(a[0]);
    [Icall("UnityEngine.Experimental.Rendering.GraphicsFormatUtility::IsStencilFormat")] static object IsStencil(object[] a) => Convert.ToInt32(a[0]) is 92 or 94 or 95 or 151;
    [Icall("UnityEngine.Experimental.Rendering.GraphicsFormatUtility::IsDepthStencilFormat")] static object IsDepthStencil(object[] a) => IsDepthFmt(a[0]) || Convert.ToInt32(a[0]) == 95;
    [Icall("UnityEngine.Experimental.Rendering.GraphicsFormatUtility::IsSRGBFormat")]
    static object IsSrgb(object[] a) => Convert.ToInt32(a[0]) is 4 or 57 or 1 or 2 or 3;
    /// <summary>SystemInfo.GetGraphicsFormat(DefaultFormat): LDR / Video = RGBA8 (the project is in gamma space), HDR = RGBA16F, depth = D24S8, shadow = D16.</summary>
    [Icall("UnityEngine.SystemInfo::GetGraphicsFormat")]
    static object SysGfxFmt(object[] a) => (UnityEngine.Experimental.Rendering.GraphicsFormat)(Convert.ToInt32(a[0]) switch { 1 => 48, 2 => 92, 3 => 90, _ => 8 });
    [Icall("UnityEngine.RenderTexture::GetDescriptor_Injected")] static object RtDesc(object[] a) { var t = Objects.Get<NTexture>(a[0]); var d = new RenderTextureDescriptor(t?.Width ?? 256, t?.Height ?? 256); a[1] = d; return null; }
    [Icall("UnityEngine.RenderTexture::SetRenderTextureDescriptor_Injected")] static object RtSetDesc(object[] a) { var t = Objects.Get<NTexture>(a[0]); var d = (RenderTextureDescriptor)a[1]; if (t != null) { t.Width = d.width; t.Height = d.height; } return null; }
}
