using System.Reflection;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>Turns the live scene graph into triangles: per camera (by depth) world sprites/meshes back-to-front, then
/// screen-space UI canvases in hierarchy order. Shaders are approximated as texture × vertex colour × tint with a blend mode.</summary>
public static partial class SceneRenderer
{
    public static IRenderBackend G;
    /// <summary>Wall time of the last Frame() (layout + uGUI rebuild + world + Stylizer + UI + backend flush), in Stopwatch ticks.</summary>
    public static long LastFrameTicks;
    static readonly MethodInfo PreWill = typeof(Canvas).GetMethod("SendPreWillRenderCanvases", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    static readonly MethodInfo Will = typeof(Canvas).GetMethod("SendWillRenderCanvases", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    public static int LastDraws, LastTris;

    public static void Frame()
    {
        if (G == null) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var mt = MtRender.Cur;
        Diag.K7Prof.Push(Diag.K7Prof.ZRWaitRt);   // kit #7 profiler zones (SHEPHERD_K7_PROF; off: one bool test each)
        mt?.FrameStart();   // SHEPHERD_MT_RENDER=1 (Render/MtRender.cs): the render thread has finished the previous frame (and the GL/SDL thread has handed it to the API)
        Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZRLayout);
        LayoutRootCanvases();
        NotifyRectChanges();
        try { PreWill?.Invoke(null, null); Will?.Invoke(null, null); } catch (Exception e) { Log.Exception(e, "Canvas.SendWillRenderCanvases"); }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: Canvas.willRenderCanvases)
#endif
        Diag.K7Prof.Pop();
        LastDraws = 0; LastTris = 0; _culled = 0; Emit0.ClipTris = Emit0.DropTris = 0; _rtCams = _rtSkipped = 0; _fxSkips = _fxBlends = _fxGlows = _fxGrabs = _fxRtZero = 0;
        _mtDefer = mt != null && !RenderCheck.On;   // SHEPHERD_MT_RENDER=1: the screen cameras' vertex builds go to the render thread (RenderCheck's re-run needs this frame's stream now)
        var cams = HotCfg.On ? CameraIcalls.AllCamerasInto(_cams) : CameraIcalls.AllCameras().ToList();   // (menu-speed: the same list, reused)
        if (HotCfg.Check) HotCheck.Seq(HotCheck.Cams, cams, CameraIcalls.AllCameras().ToList());
        // polish round 2 (survey item 4): a camera with a targetTexture renders into that RenderTexture, not onto the screen. Unity
        // renders the off-screen cameras first (by depth), then the on-screen ones; the first on-screen camera's background clears the frame.
        _screenCams.Clear(); _rtCamList.Clear();
        foreach (var cam in cams) { var rt = CameraIcalls.TargetOf(cam); if (rt != null) _rtCamList.Add((cam, rt)); else _screenCams.Add(cam); }
        if (Runtime.Visibility.On) Runtime.Visibility.FrameStart();   // kit #8 speed: SHEPHERD_K8_VISIBILITY=1 (opt-in) -- this frame's visibility
        var first = _screenCams.Count > 0 ? _screenCams[0] : null;
        var bg = first?.Get("backgroundColor") is Color c0 ? c0 : new Color(0.05f, 0.03f, 0.06f, 1);
        G.Begin(bg.r, bg.g, bg.b, 1); _curTarget = IntPtr.Zero;
        BlackFrame.BeginFrame();   // kit #4 M1: is this frame covered (a scene transition's black hold)? read only
        // fidelity round 1: the owner-local render code (Render/DerivedRender.cs), loaded on the first frame; absent = unlit, ungraded,
        // no Stylizer pass. The lit model takes this frame's light, ambient and fog (GL, gameplay scenes only).
        _lit = DerivedRender.Lighting; _post = DerivedRender.Post; _litOn = false;
        _litFrame = _lit != null && _lit.BeginFrame();   // (integration with menu-speed, Api 3: inactive = no per-draw calls into the owner-local module)
        RenderCheck.BeginFrame();   // SHEPHERD_RENDER_CHECK=1 only: record this frame's draw stream
        Diag.K7Prof.Push(Diag.K7Prof.ZRWorld);
        foreach (var (cam, rt) in _rtCamList) { try { RenderToTarget(cam, rt); } catch (Exception e) { Log.Exception(e, $"DrawWorld (target) '{cam.Go?.Name}'"); G.SetTarget(_curTarget = IntPtr.Zero); } }
        foreach (var cam in _screenCams)
        {
            int clear = cam.Get("clearFlags") is { } cf ? Convert.ToInt32(cf) : 1;
            if (cam != first && (clear == 1 || clear == 2)) { var col = cam.Get("backgroundColor") is Color bc ? bc : Color.black; FillScreen(col); }
            mt?.BeginScope();   // SHEPHERD_MT_RENDER=1: an exception when this camera's recorded calls run drops the rest of them, as this try block does
            try { DrawWorld(cam, CameraIcalls.PixelRectF(cam), G.H); } catch (Exception e) { Log.Exception(e, $"DrawWorld '{cam.Go?.Name}'"); }
            mt?.EndScope();
        }
        if (Runtime.Visibility.On) Runtime.Visibility.FrameEnd();   // kit #8 (opt-in): OnBecameInvisible for those no camera kept this frame
        Diag.K7Prof.Pop();
        Diag.K7Prof.Push(Diag.K7Prof.ZRPost);
        // fidelity round 1: the camera's post effects (owner-local): grading settings + light mask (GL). SHEPHERD_MT_RENDER=1: its backend calls
        // (the light mask's target, clear and draws, built here) are recorded in their own scope, like the Stylizer's and the UI pass's
        if (_post != null) { mt?.BeginScope(); try { _post.Prepare(G, _screenCams); } catch (Exception e) { Log.Exception(e, "PostEffects"); } mt?.EndScope(); }
        mt?.BeginScope();
        try { G.EndWorldPass(); } catch (Exception e) { Log.Exception(e, "Stylizer"); }   // SDL: the CPU Stylizer; GL: the post effects + the GPU Stylizer (owner-local; MT: their parameter lookups run here, their passes are recorded)
        mt?.EndScope();
#if !SHEPHERD_NO_K8
        if (Runtime.K8Video.On && Runtime.K8Video.Visible(_screenCams))
        {   // kit #8 (Runtime/K8Video.cs): a playing clip's picture where Unity draws a CameraNearPlane video -- over the top camera's world pass,
            // under the overlay canvases (off, or no picture this frame: no call -- kit #7's frames)
            mt?.BeginScope();
            try { if (_curTarget != IntPtr.Zero) G.SetTarget(_curTarget = IntPtr.Zero); G.NoClip(); Runtime.K8Video.Draw(G, _screenCams); } catch (Exception e) { Log.Exception(e, "K8Video"); }
            mt?.EndScope();
        }
#endif
        Diag.K7Prof.Pop();
        RenderCheck.Live?.Mark(1);
        Diag.K7Prof.Push(Diag.K7Prof.ZRUi);
        mt?.BeginScope();
        try { DrawOverlayCanvases(); } catch (Exception e) { Log.Exception(e, "DrawOverlayCanvases"); }
        mt?.EndScope();
        Diag.K7Prof.Pop();
        if (K7Overlay.On)
        {   // kit #7: the performance overlay over everything (Render/K7Overlay.cs; off: no call -- kit #6's frames)
            mt?.BeginScope();
            try { if (_curTarget != IntPtr.Zero) G.SetTarget(_curTarget = IntPtr.Zero); G.NoClip(); K7Overlay.Draw(G); } catch (Exception e) { Log.Exception(e, "K7Overlay"); }
            mt?.EndScope();
        }
        Diag.K7Prof.Push(Diag.K7Prof.ZREnd);
        if (RenderCheck.HashFile != null && !_mtDefer) RenderCheck.HashFrame(Runtime.Time.FrameCount);   // menu-speed: SHEPHERD_STREAM_HASH (output-identity A/B)
        if (RenderCheck.On) RenderCheck.EndFrame(cams, DerivedRender.Stylizer);   // the pre-stage-2 renderer, re-run and compared
        if (mt == null) { G.EndFrame(); LastClipTris = Emit0.ClipTris; LastDropTris = Emit0.DropTris; }
        else mt.FrameEnd(Runtime.Time.FrameCount, Emit0.ClipTris, Emit0.DropTris, RenderCheck.HashFile != null && _mtDefer);   // G.EndFrame, then the frame goes to the render thread (which sets LastDraws / LastTris / LastClipTris / LastDropTris and hashes the stream when it is done)
        _mtDefer = false;
        Diag.K7Prof.Pop();
        LastCulled = _culled; LastRtCams = _rtCams; LastRtSkipped = _rtSkipped;
        LastFxSkips = _fxSkips; LastFxBlends = _fxBlends; LastFxGlows = _fxGlows; LastFxGrabs = _fxGrabs; LastFxRtZero = _fxRtZero;
        LastFrameTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        BlackFrame.EndFrame();
    }

    // ---------- render targets (polish round 2, survey item 4) ----------
    // Before, a RenderTexture was a size record: a camera's targetTexture was ignored (the RT camera drew onto the screen and its
    // clear filled it), GL.Clear and Graphics.Blit did nothing, and a RawImage showing an RT had no pixels, so it was drawn as an
    // untextured opaque quad -- the intro's full-screen #00FFFF frame (EyeTrailBlit's preview RawImage). Now an RT is a backend
    // render target (GL: an FBO texture; SDL: a target texture), stored top-down like every texture, so drawing into it and
    // sampling it need no flips:
    // - cameras with a target render into it (own clear, pixel rect and aspect from the target size) before the screen cameras
    // - UI and materials sample it like any texture (Handle)
    // - GL.Clear clears RenderTexture.active; Graphics.Blit copies (scale/offset included) into the destination
    // - content knowledge: an RT known to be all (0,0,0,0) is not allocated or cleared until something draws into or reads it,
    //   and an RT camera whose target nothing has sampled for two frames is skipped (it is rendered again once something reads it)
    internal static IntPtr _curTarget;   // what SceneRenderer last bound with G.SetTarget (Zero = the frame's main target)
    static readonly List<NBehaviour> _screenCams = new(), _cams = new();
    static readonly List<(NBehaviour cam, NTexture rt)> _rtCamList = new();
    static int _rtCams, _rtSkipped;
    /// <summary>Last Frame: cameras rendered into a target, and RT cameras skipped because nothing samples their target.</summary>
    public static int LastRtCams, LastRtSkipped;
    /// <summary>The RT's backend target, allocated (cleared to (0,0,0,0)) or re-made at its current size, with any pending clear applied.</summary>
    internal static IntPtr RtHandle(NTexture t)
    {
        if (G == null || t == null || t.Destroyed) return IntPtr.Zero;
        int w = Math.Max(1, t.Width), h = Math.Max(1, t.Height);
        if (t.RtTarget != IntPtr.Zero && (t.RtW != w || t.RtH != h)) { G.DestroyTexture(t.RtTarget); t.RtTarget = IntPtr.Zero; }
        if (t.RtTarget == IntPtr.Zero)
        {
            t.RtTarget = G.CreateTarget(w, h); t.RtW = w; t.RtH = h;
            if (t.RtTarget == IntPtr.Zero) return IntPtr.Zero;
            if (!t.RtClearPending) { t.RtClearPending = true; t.RtClear = default; }   // a new target's content is undefined: start at (0,0,0,0)
        }
        if (t.RtClearPending)
        {
            G.SetTarget(t.RtTarget); G.NoClip(); G.ClearTarget(t.RtClear.r, t.RtClear.g, t.RtClear.b, t.RtClear.a); G.SetTarget(_curTarget);
            RenderCheck.Live?.Target(t.RtTarget); RenderCheck.Live?.ClearTarget(t.RtClear.r, t.RtClear.g, t.RtClear.b, t.RtClear.a); RenderCheck.Live?.Target(_curTarget);
            t.RtClearPending = false;
        }
        return t.RtTarget;
    }
    /// <summary>Release / Destroy: free the target (its content is gone; a later use starts from (0,0,0,0)).</summary>
    public static void ReleaseRt(NTexture t)
    {
        if (t.RtTarget != IntPtr.Zero && G != null) G.DestroyTexture(t.RtTarget);
        t.RtTarget = IntPtr.Zero; t.RtZero = true; t.RtClearPending = false;
    }
    /// <summary>GL.Clear on RenderTexture.active (and a camera's own clear): kept pending until the target is next used.</summary>
    public static void ClearRt(NTexture t, Color c)
    {
        if (t == null || t.Destroyed) return;
        t.RtClearPending = true; t.RtClear = c; t.RtZero = c.r == 0f && c.g == 0f && c.b == 0f && c.a == 0f;
    }
    /// <summary>A texture known to hold only (0,0,0,0): an RT so marked, or a small CPU texture whose pixels are all zero (Texture2D.blackTexture).</summary>
    internal static bool IsZero(NTexture t)
    {
        if (t == null) return false;
        if (t.IsRenderTexture) return t.RtZero;
        var px = t.Rgba; if (px == null || px.Length > 64 * 64 * 4) return false;
        for (int i = 0; i < px.Length; i++) if (px[i] != 0) return false;
        return true;
    }
    static readonly int[] _qi = { 0, 1, 2, 0, 2, 3 };
    static readonly Vertex[] _qv = new Vertex[4];
    /// <summary>Graphics.Blit: dst = src sampled at uv * scale + offset (Unity's uv: bottom-up), a straight copy (blend none). A material's
    /// own shader is not run (the copy is its approximation: vita/notes/polish-round1.md, stage 2). dst null (the back buffer) is not drawn: outside
    /// a camera's OnRenderImage the frame is drawn by the cameras afterwards anyway.</summary>
    public static void Blit(NTexture src, NTexture dst, Vector2 scale, Vector2 offset)
    {
        if (G == null || dst == null || dst.Destroyed || !dst.IsRenderTexture || ReferenceEquals(src, dst)) return;
        if (src == null || IsZero(src)) { ClearRt(dst, default); return; }   // a copy of nothing / of transparent black: a pending clear
        var sh = src.IsRenderTexture ? RtHandle(src) : Handle(src, false);
        if (src.IsRenderTexture) src.RtSampled = Runtime.Time.FrameCount;
        var dh = RtHandle(dst); if (dh == IntPtr.Zero) return;
        if (sh == IntPtr.Zero) { ClearRt(dst, default); return; }
        float w = dst.RtW, h = dst.RtH;
        for (int k = 0; k < 4; k++)
        {
            float x = k == 1 || k == 2 ? w : 0f, y = k >= 2 ? h : 0f, u = x / w, v = 1f - y / h;   // top-down target pixel -> Unity uv
            _qv[k].position.x = x; _qv[k].position.y = y; _qv[k].color.r = _qv[k].color.g = _qv[k].color.b = _qv[k].color.a = 1f;
            _qv[k].tex_coord.x = u * scale.x + offset.x; _qv[k].tex_coord.y = 1f - (v * scale.y + offset.y);
        }
        G.SetTarget(dh); G.NoClip(); G.Draw(sh, BLENDMODE_NONE, false, _qv, _qi); G.SetTarget(_curTarget);
        if (RenderCheck.Live != null && !RenderCheck.On) { RenderCheck.Live.Target(dh); RenderCheck.Live.Draw(sh, BLENDMODE_NONE, false, _qv, _qi); RenderCheck.Live.Target(_curTarget); }
        dst.RtZero = false;
    }
    /// <summary>One off-screen camera: its target bound, cleared by its clear flags (Skybox/Solid colour: the background colour, alpha
    /// included), the world drawn with the target's pixel rect, the screen target restored.</summary>
    static void RenderToTarget(NBehaviour cam, NTexture rt)
    {
        int f = Runtime.Time.FrameCount;
        if (rt.RtSampled < f - 2) { _rtSkipped++; return; }   // nothing has read this target lately: skip (rendered again once something samples it)
        int clear = cam.Get("clearFlags") is { } cf ? Convert.ToInt32(cf) : 1;
        if (clear == 1 || clear == 2) ClearRt(rt, cam.Get("backgroundColor") is Color bc ? bc : Color.black);
        var h = RtHandle(rt); if (h == IntPtr.Zero) return;
        _rtCams++;
        G.SetTarget(_curTarget = h); G.NoClip(); RenderCheck.Live?.Target(h);
        var r = CameraIcalls.ViewRectF(cam); float tw = rt.RtW, th = rt.RtH;
        int d0 = LastDraws;
        var tpr = new RectF(r.X * tw, r.Y * th, r.W * tw, r.H * th);   // (hop fix: was new UnityEngine.Rect(r.x * tw, r.y * th, r.width * tw, r.height * th))
        if (HopCheck.On) { var u = CameraIcalls.ViewRect(cam); HopCheck.R(HopCheck.PixelRect, tpr, new UnityEngine.Rect(u.x * tw, u.y * th, u.width * tw, u.height * th)); }
        bool defer = _mtDefer; _mtDefer = false;   // SHEPHERD_MT_RENDER=1: built now (whether it drew anything decides RtZero below)
        try { DrawWorld(cam, in tpr, th); }
        finally { G.SetTarget(_curTarget = IntPtr.Zero); RenderCheck.Live?.Target(IntPtr.Zero); _mtDefer = defer; }
        if (LastDraws != d0) rt.RtZero = false;
    }

    // ---------- UI ----------
    static void LayoutRootCanvases()
    {
        foreach (var n in Registry.Canvases.Snapshot())   // stage 2: the canvases, in Objects.All order (was a scan of every record)
        {
            if (n is not NBehaviour cv || cv.Destroyed || cv.Managed is not Canvas || cv.Go == null || cv.Go.Scene == 0) continue;
            if (RenderIcalls.RootCanvasOf(cv.Go) != cv) continue;
            LayoutRoot(cv);
        }
    }
    /// <summary>One root canvas: Unity's canvas-driven RectTransform (screen size / scaleFactor, centred, scaled; Camera mode: in front of
    /// the camera). World space canvases are author-placed.</summary>
    static void LayoutRoot(NBehaviour cv)
    {
        int mode = cv.Get("renderMode") is { } rm ? Convert.ToInt32(rm) : 0;
        if (mode == 2) return;   // world space: author-placed
        float s = cv.Get("scaleFactor") is { } sf ? (float)Conv.To(sf, typeof(float)) : 1f; if (s <= 0) s = 1;
        // (menu-speed hop fix: the values by field stores / our own constants, not Unity's Vector2/Vector3 constructors, Vector2.zero and
        // Quaternion.identity: the same floats, set in the same order)
        if (HopCheck.On) LayoutCheck(s);
        var t = cv.Go.Transform; t.IsRect = true; t.AnchorMin = t.AnchorMax = Um.Zero2; t.Pivot = Um.V2(0.5f, 0.5f);
        t.SizeDelta = Um.V2(AppIcalls.ScreenW / s, AppIcalls.ScreenH / s);
        if (mode == 1 && Objects.Get(CanvasCamera(cv, out bool touchPlaced)) is NBehaviour cam && !cam.Destroyed && cam.Go != null
            && ((touchPlaced && TouchPlacementHolds(t, cam, s)) || PlaceInFrontOf(t, cam, cv, s))) { if (touchPlaced) TouchLayouts++; return; }
        t.AnchoredPosition = Um.V2(AppIcalls.ScreenW / 2f, AppIcalls.ScreenH / 2f);
        t.LocalScale = Um.V3(s, s, s); t.LocalRotation = Um.QIdentity; t.LocalPosition = Um.V3(0f, 0f, 0f);
    }

    /// <summary>k4-display (the resolution dropdown drawn at the top-left, device kit #3): Unity drives a root screen-space canvas's
    /// RectTransform AT ONCE -- when the canvas wakes up (scene load, Instantiate) and whenever its scaleFactor changes (CanvasScaler) --
    /// so game code that converts world positions under a canvas it has just instantiated gets the screen mapping. The engine laid root
    /// canvases out only at the start of the next render: until then a new prefab instance kept the prefab's saved root values (a root
    /// canvas prefab is saved with scale 0), so the game's dropdown overlay (it places its list at the dropdown's world position in the same
    /// call as the Instantiate) inverted a zero-scale matrix and its list landed at the canvas origin. These two entry points lay the root
    /// out at Unity's moments; the per-frame pass stays (same values). SHEPHERD_CANVAS_EAGER=0: the old behaviour (per-case switch back).</summary>
    public static readonly bool CanvasEager = Environment.GetEnvironmentVariable("SHEPHERD_CANVAS_EAGER") != "0";
    /// <summary>Object.Instantiate, after the clone is placed and before its Awake / OnEnable: every root canvas in the new hierarchy.</summary>
    public static void LayoutNewRootCanvases(NGameObject root)
    {
        if (!CanvasEager || root == null || root.Scene == 0 || !root.ActiveInHierarchy) return;
        LayoutTree(root.Transform);
    }
    static void LayoutTree(NTransform t)
    {
        if (t?.Go == null || !t.Go.ActiveSelf) return;
        foreach (var c in t.Go.Components)
            if (c is NBehaviour cv && !cv.Destroyed && cv.Managed is Canvas && RenderIcalls.RootCanvasOf(cv.Go) == cv) { LayoutRoot(cv); return; }   // (a root canvas's subtree has no other root)
        foreach (var ch in t.Children) LayoutTree(ch);
    }
    /// <summary>Canvas.scaleFactor set (CanvasScaler.Handle): a root canvas in a scene is laid out with the new factor now.</summary>
    public static void LayoutRootCanvasNow(NComponent c)
    {
        if (!CanvasEager || c is not NBehaviour cv || cv.Destroyed || cv.Managed is not Canvas || cv.Go == null || cv.Go.Scene == 0 || !cv.Go.ActiveInHierarchy) return;
        if (RenderIcalls.RootCanvasOf(cv.Go) == cv) LayoutRoot(cv);
    }

    /// <summary>SHEPHERD_HOPFIX_CHECK=1: LayoutRootCanvases' values against Unity's constructors / constants.</summary>
    static void LayoutCheck(float s)
    {
        HopCheck.V2(HopCheck.Layout, Um.Zero2, Vector2.zero); HopCheck.V2(HopCheck.Layout, Um.V2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        HopCheck.V2(HopCheck.Layout, Um.V2(AppIcalls.ScreenW / s, AppIcalls.ScreenH / s), new Vector2(AppIcalls.ScreenW / s, AppIcalls.ScreenH / s));
        HopCheck.V2(HopCheck.Layout, Um.V2(AppIcalls.ScreenW / 2f, AppIcalls.ScreenH / 2f), new Vector2(AppIcalls.ScreenW / 2f, AppIcalls.ScreenH / 2f));
        HopCheck.V3(HopCheck.Layout, Um.V3(s, s, s), new Vector3(s, s, s)); HopCheck.Q(HopCheck.Layout, Um.QIdentity, Quaternion.identity); HopCheck.V3(HopCheck.Layout, Um.V3(0f, 0f, 0f), new Vector3(0, 0, 0));
    }

    /// <summary>kit #5 touch: a Camera-mode canvas's camera as a scene saves it is a reference the loader resolves lazily (Assets.LazyRef --
    /// Canvas.worldCamera's getter resolves it, so the game sees the camera), and Objects.Get of the unresolved reference is null: such a
    /// canvas is laid out like an overlay canvas, under its parent. The picture does not depend on it (screen-space canvases are drawn
    /// from root-local coordinates, RootLocalToScreen), but the GraphicRaycaster tests its hits in world space with that camera, as Unity
    /// places the canvas: in front of it at planeDistance (Main Menu's buttons sat 544 units in front of a camera whose far plane is 304:
    /// every hit was dropped). While the pointer is on the screen (Impl.VitaTouch.Placed: a touch, from the finger's arrival to the frame
    /// after the release) the reference is resolved and the root is placed as Unity does (PlaceInFrontOf); otherwise the layout is kit
    /// #4's. Not for good: Unity's placement redraws the same picture, but the vertices go through the new world matrices and differ in the
    /// last bits (edge pixels; vita/notes/kit5-touch.md), and a root that follows the main menu's gliding camera voids the UI geometry
    /// cache every frame -- so a session without touch stays bit-identical and costs nothing. SHEPHERD_TOUCH=0: never (kit #4).</summary>
    static object CanvasCamera(NBehaviour cv, out bool touchPlaced)
    {
        var wc = cv.Get("worldCamera");
        // kit #6 (d, SHEPHERD_K6_CANVAS_CAMERA=1, opt-in since the review): the saved reference is always resolved -- the canvas sits in front
        // of its camera as Unity places it, touch or not; the placement is kept while it still matches the picture (TouchPlacementHolds'
        // 0.5-px rule). Off by default: a moving menu camera re-places the canvas each frame and voids the UI geometry cache (K6Engine).
        touchPlaced = (Impl.VitaTouch.Placed || Runtime.K6Engine.CanvasCamera) && wc is Assets.LazyRef;
        return touchPlaced ? ((Assets.LazyRef)wc).Resolve() : wc;
    }
    /// <summary>kit #5 touch: a canvas placed for the pointer (CanvasCamera) keeps its placement while it still matches the picture: its
    /// rect's four corners, through its world matrix and the camera, land within 0.5 px of the screen's corners (where the canvas is drawn).
    /// The main menu camera glides toward the mouse for ~200 frames after every touch (CameraSubtleMovementOnInput: 10 % of the way per
    /// frame); following it in every one of them moves every UI transform of the canvas, which voids the UI geometry cache and the world
    /// matrix memos each frame (VLX: +16 ms per idle-menu frame through the glide). Within 0.5 px nothing a hit test or a conversion
    /// sees changes, and the canvas is re-placed when the camera has moved further.</summary>
    /// <summary>kit #5 touch: Camera-mode root canvases laid out for the pointer / of them kept as they were (TouchPlacementHolds).</summary>
    public static long TouchLayouts, TouchHolds;
    static bool TouchPlacementHolds(NTransform t, NBehaviour cam, float s)
    {
        var m = t.LocalToWorld; float hw = AppIcalls.ScreenW / s * 0.5f, hh = AppIcalls.ScreenH / s * 0.5f;
        for (int i = 0; i < 4; i++)
        {
            float cx = (i & 1) == 0 ? -hw : hw, cy = (i & 2) == 0 ? -hh : hh;
            var sp = CameraIcalls.WorldToScreen(cam, M.Point(m, new Vector3(cx, cy, 0f)));
            float ex = AppIcalls.ScreenW * 0.5f + cx * s, ey = AppIcalls.ScreenH * 0.5f + cy * s;
            if (!(sp.z > 0f) || !(MathF.Abs(sp.x - ex) <= 0.5f) || !(MathF.Abs(sp.y - ey) <= 0.5f)) return false;
        }
        TouchHolds++;
        return true;
    }
    /// <summary>kit #5 touch: every root canvas laid out now (the first touch switches Camera-mode canvases to Unity's placement before the
    /// frame's raycasts; the render pass does the same at its start).</summary>
    public static void LayoutRootCanvasesNow() => LayoutRootCanvases();

    /// <summary>Screen Space - Camera: Unity drives the root canvas to face the camera at planeDistance, scaled so the canvas
    /// exactly covers the view (so UI world positions agree with Camera.WorldToScreenPoint).</summary>
    static bool PlaceInFrontOf(NTransform t, NBehaviour cam, NObj cv, float s)
    {
        var ct = cam.Go.Transform; var camPos = M.Point(ct.LocalToWorld, Vector3.zero); var camRot = ct.Rotation;
        float pd = CameraIcalls.F(cv, "planeDistance", 100f);
        float worldH = CameraIcalls.B(cam, "orthographic", false) ? 2f * CameraIcalls.F(cam, "orthographicSize", 5f) : 2f * pd * MathF.Tan(CameraIcalls.F(cam, "fieldOfView", 60f) * MathF.PI / 360f);
        float scale = worldH * s / AppIcalls.ScreenH; if (!(scale > 0) || float.IsInfinity(scale)) return false;
        var wpos = camPos + camRot * new Vector3(0, 0, pd);
        Vector3 lpos = wpos; var lrot = camRot; float pscale = 1f;
        if (t.Parent != null)
        {
            var pw = t.Parent.LocalToWorld; lpos = M.Point(M.Inverse(pw), wpos);
            var pr = t.Parent.Rotation; lrot = M.QMul(new Quaternion(-pr.x, -pr.y, -pr.z, pr.w), camRot);
            pscale = new Vector3(pw.m00, pw.m10, pw.m20).magnitude; if (pscale <= 0) pscale = 1f;
        }
        t.LocalRotation = lrot; t.LocalScale = Vector3.one * (scale / pscale); t.EffectiveLocalPosition = lpos;
        return true;
    }

    /// <summary>Pixel mapping for a screen-space canvas: root-local (reference units, origin at the canvas centre) * scaleFactor + screen centre.
    /// Independent of where the root sits in the world (Overlay or Camera mode).</summary>
    static readonly Dictionary<NComponent, (int frame, Matrix4x4 m)> _canvasScreen = new();
    static Matrix4x4 RootLocalToScreen(NComponent root)
    {
        float s = root.Get("scaleFactor") is { } sf ? (float)Conv.To(sf, typeof(float)) : 1f; if (s <= 0) s = 1;
        if (HopCheck.On) { HopCheck.V3(HopCheck.Layout, Um.V3(G.W / 2f, G.H / 2f, 0f), new Vector3(G.W / 2f, G.H / 2f, 0)); HopCheck.Q(HopCheck.Layout, Um.QIdentity, Quaternion.identity); HopCheck.V3(HopCheck.Layout, Um.V3(s, s, s), new Vector3(s, s, s)); }
        return M.TRS(Um.V3(G.W / 2f, G.H / 2f, 0f), Um.QIdentity, Um.V3(s, s, s));   // (hop fix: were new Vector3(..), Quaternion.identity)
    }
    /// <summary>kit #5 touch (the pointer's hit test, Icalls/VitaTouch.cs PointInRectangle): the matrix the UI pass draws <paramref name="t"/>
    /// with -- root-local to screen (Unity's screen pixels, y up) x the root's world-to-local x t's local-to-world -- when t is under a
    /// screen-space root canvas (overlay or camera mode); false for a world-space canvas or none. Not CanvasToScreen's per-frame memo: the
    /// raycast runs before the frame's layout, and priming the memo then would hand the UI pass a stale matrix.</summary>
    internal static bool UiScreenMatrix(NTransform t, out Matrix4x4 m)
    {
        m = default;
        if (t?.Go == null) return false;
        int frame = Runtime.Time.FrameCount;
        if (!_uiRootOf.TryGetValue(t, out var e)) { if (_uiRootOf.Count > 4096) _uiRootOf.Clear(); _uiRootOf[t] = e = new UiRootMemo { Frame = -1 }; }
        if (e.Frame != frame) { e.Frame = frame; e.Root = RenderIcalls.RootCanvasOf(t.Go) as NBehaviour; }
        var root = e.Root;
        if (root == null || root.Destroyed || root.Go == null || (root.Get("renderMode") is { } rm ? Convert.ToInt32(rm) : 0) == 2) return false;
        m = M.Mul(M.Mul(RootLocalToScreen(root), root.Go.Transform.WorldToLocal), t.LocalToWorld);
        return true;
    }
    sealed class UiRootMemo { public int Frame; public NBehaviour Root; }   // (a class: the dictionary shares the reference-type code, no new AOT instance)
    static readonly Dictionary<NTransform, UiRootMemo> _uiRootOf = new();
    static Matrix4x4 CanvasToScreen(NComponent root)
    {
        if (_canvasScreen.TryGetValue(root, out var c) && c.frame == Runtime.Time.FrameCount) return c.m;
        var m = M.Mul(RootLocalToScreen(root), root.Go.Transform.WorldToLocal);
        _canvasScreen[root] = (Runtime.Time.FrameCount, m); return m;
    }

    /// <summary>Unity's native side sends OnRectTransformDimensionsChange whenever a RectTransform's rect changes size;
    /// uGUI relies on it to rebuild stretched graphics and layouts.</summary>
    static readonly Dictionary<NTransform, Vector2> LastSize = new();
    static readonly System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
    /// <summary>old == sz (Vector2 ==, Unity's approximate equality) as our own arithmetic (hop fix): Um.Eq.</summary>
    static bool SameSize(Vector2 old, Vector2 sz) { bool e = Um.Eq(old, sz); if (HopCheck.On) HopCheck.Bool(HopCheck.V2Eq, e, old == sz); return e; }
    public static void NotifyRectChanges()
    {
        if (HotCfg.On && NotifyRectChangesQueued()) return;
        NrcTakeQueue().Clear();   // (the full walk covers everything queued so far)
        foreach (var n in Registry.Transforms.Snapshot()) NotifyRect(n);
    }
    /// <summary>One transform of the walk (the loop body as it was). menu-speed (top hotspots): returns true when it sent the message (game
    /// code ran); a transform without a GameObject yet stays queued (its test may pass once it has one).</summary>
    static bool NotifyRect(NObj n)
    {
        if (n is not NTransform t || !t.IsRect || t.Destroyed || t.Go == null || t.Go.Scene == 0) { if (n is NTransform { Go: null, Destroyed: false } q) q.NrcDirty(); return false; }
        if (t.SizeSeen()) return false;   // stage 2: unchanged since its size was last compared here -> the same comparison, the same 'continue'
        var sz = t.Size;
        if (LastSize.TryGetValue(t, out var old) && SameSize(old, sz)) { t.MarkSizeSeen(); return false; }
        LastSize[t] = sz; t.MarkSizeSeen();
        if (!t.Go.ActiveInHierarchy) return false;
        bool sent = false;
        foreach (var c in t.Go.Components.ToArray())
        {
            if (c is not NMono m || m.Destroyed || !m.Awoken) continue;
            for (var ty = m.Managed.GetType(); ty != null && ty != typeof(MonoBehaviour); ty = ty.BaseType)
            {
                var mi = ty.GetMethod("OnRectTransformDimensionsChange", Any | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (mi == null) continue;
                if (!sent) { sent = true; if (_nrcFast) NrcSnap(); }   // (menu-speed: the queued walk's snapshot, taken before game code first runs)
                try { mi.Invoke(m.Managed, null); } catch (Exception e) { Log.Exception(e, "OnRectTransformDimensionsChange"); }
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: OnRectTransformDimensionsChange)
#endif
                break;
            }
        }
        return sent;
    }

    /// <summary>menu-speed: the retained UI pass (SceneRenderer.UiCache.cs) unless SHEPHERD_UICACHE=0, which runs the reference pass
    /// below (the pass as it was); SHEPHERD_UICACHE_CHECK=1 runs both every frame and compares their streams (Render/UiCacheCheck.cs).</summary>
    static void DrawOverlayCanvases()
    {
        if (!UiCacheCfg.On) { DrawOverlayCanvasesRef(); return; }
        if (UiCacheCfg.Check) { UiCacheCheck.Frame(); return; }
        DrawOverlayCanvasesFast();
    }

    /// <summary>The reference UI pass (the pass before menu-speed; its backend calls go through UiClipTo / UiNoClip / SubmitIdx,
    /// which make exactly the old calls unless UiCacheCheck is recording it).</summary>
    internal static void DrawOverlayCanvasesRef()
    {
        var roots = Registry.Canvases.Items().OfType<NBehaviour>().Where(cv => !cv.Destroyed && cv.Managed is Canvas && cv.Enabled && cv.Go != null && cv.Go.Scene != 0 && cv.Go.ActiveInHierarchy && RenderIcalls.RootCanvasOf(cv.Go) == cv)
            .Where(cv => (cv.Get("renderMode") is { } rm ? Convert.ToInt32(rm) : 0) != 2)
            .OrderBy(cv => cv.Get("sortingOrder") is { } so ? Convert.ToInt32(so) : 0).ToList();
        foreach (var root in roots) { int depth = 0; DrawUiTree(root.Go, ref depth, default); }
        UiNoClip();
    }

    /// <summary>Polish round 1: uGUI's stencil Mask, approximated by the mask graphic's screen-space bounding box (the engine has no
    /// stencil buffer): everything under an enabled Mask is scissored to that box, nested masks intersect. Exact for the rectangular
    /// masks of menus (scroll-view viewports, tab frames); a non-rectangular mask sprite clips to its box. Before, nothing was masked.
    /// Rect: y-up screen pixels.</summary>
    readonly struct MaskClip
    {
        public readonly bool On; public readonly float X0, Y0, X1, Y1;
        public MaskClip(float x0, float y0, float x1, float y1) { On = true; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public bool Empty => On && (X1 <= X0 || Y1 <= Y0);
        public MaskClip And(float x0, float y0, float x1, float y1) => !On ? new(x0, y0, x1, y1) : new(Math.Max(X0, x0), Math.Max(Y0, y0), Math.Min(X1, x1), Math.Min(Y1, y1));
    }
    static MaskClip MaskOf(NGameObject go, MaskClip outer)
    {
        foreach (var c in go.Components)
        {
            if (c is not NMono mono || mono.Destroyed || !mono.Enabled || mono.Managed is not UnityEngine.UI.Mask) continue;
            bool hasGraphic = false; foreach (var g in go.Components) if (g is NMono gm && !gm.Destroyed && gm.Managed is UnityEngine.UI.Graphic) { hasGraphic = true; break; }
            if (!hasGraphic || !go.Transform.IsRect) return outer;   // Mask.MaskEnabled(): active and enabled, with a Graphic
            var root = RenderIcalls.RootCanvasOf(go);
            var m = root != null ? M.Mul(CanvasToScreen(root), go.Transform.LocalToWorld) : go.Transform.LocalToWorld;
            var r = go.Transform.Rect;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                float lx = (k & 1) == 0 ? r.xMin : r.xMax, ly = (k & 2) == 0 ? r.yMin : r.yMax;
                float wx = m.m00 * lx + m.m01 * ly + m.m03, wy = m.m10 * lx + m.m11 * ly + m.m13;
                x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); y0 = Math.Min(y0, wy); y1 = Math.Max(y1, wy);
            }
            return outer.And(x0, y0, x1, y1);
        }
        return outer;
    }

    static void DrawUiTree(NGameObject go, ref int depth, MaskClip clip)
    {
        if (!go.ActiveInHierarchy) return;
        foreach (var c in go.Components)
        {
            if (c is NCanvasRenderer cr && !cr.Destroyed && !cr.Cull && cr.Idx.Length > 0) { cr.AbsoluteDepth = depth++; UiDepths?.Add((cr, cr.AbsoluteDepth)); DrawCanvasRenderer(cr, clip); }
        }
        // stage 2: walks the child list in place (was a ToArray copy per node): nothing in the UI pass changes the hierarchy
        var ch = go.Transform.Children;
        if (ch.Count == 0) return;
        var inner = MaskOf(go, clip);   // the Mask's own graphic is drawn under the outer clip; its children under its box
        for (int i = 0; i < ch.Count; i++) DrawUiTree(ch[i].Go, ref depth, inner);
    }

    static readonly int ColorMaskId = ShaderIds.Id("_ColorMask");
    static void DrawCanvasRenderer(NCanvasRenderer cr, MaskClip mask)
    {
        var mat = cr.Materials.Length > 0 ? cr.Materials[0] : null;
        // polish round 1: a material with ColorMask 0 writes no colour (uGUI's Mask with showMaskGraphic off: the graphic only marks
        // the stencil); a box fully masked away draws nothing
        if (mat != null && mat.P.TryGetValue(ColorMaskId, out var cm) && cm is float cmf && cmf == 0f) return;
        if (mask.Empty) return;
        var tex = cr.Texture ?? (mat?.P.TryGetValue(ShaderIds.MainTex, out var mt) == true ? RenderIcalls.Tex(mt) : null);
        float alpha = RenderIcalls.UiAlpha(cr.Go);
        var tint = cr.Color; tint.a *= alpha;
        if (mat != null && mat.P.TryGetValue(ShaderIds.Color, out var mc) && mc is Color mcol) tint *= mcol;
        if (tint.a <= 0.001f) return;
        var root = RenderIcalls.RootCanvasOf(cr.Go);
        var m = root != null ? M.Mul(CanvasToScreen(root), cr.Go.Transform.LocalToWorld) : cr.Go.Transform.LocalToWorld;
        var sh = ShaderOf(mat?.ShaderName ?? "UI/Default");
        // polish round 2: the BlendModes grab shaders (ShaderPolicy), and an RT known to hold only (0,0,0,0) under an alpha-weighted blend,
        // which changes nothing (EyeTrailBlit's preview RawImage before the trail is ever drawn: the intro's #00FFFF frame)
        uint blend = sh.BlendUi;
        var fx = ShaderPolicy.Ui(mat, out var fb);
        if (tex != null && tex.IsRenderTexture && tex.RtZero && (fx == ShaderPolicy.Kind.Grab || blend is BLENDMODE_BLEND or BLENDMODE_BLEND_PREMULTIPLIED or BLENDMODE_ADD or BLENDMODE_ADD_PREMULTIPLIED))
        { tex.RtSampled = Runtime.Time.FrameCount; _fxRtZero++; return; }
        if (fx == ShaderPolicy.Kind.Grab) { FxDraw(mat, fx, fb, cr.Go); blend = fb; tex ??= WhiteTex(); }
        if (cr.RectClipping && !mask.On)
        {
            var rm = root != null ? RootLocalToScreen(root) : Matrix4x4.identity;
            var p0 = M.Point(rm, new Vector3(cr.ClipRect.xMin, cr.ClipRect.yMin, 0)); var p1 = M.Point(rm, new Vector3(cr.ClipRect.xMax, cr.ClipRect.yMax, 0));
            UiClipTo((int)Math.Min(p0.x, p1.x), (int)(G.H - Math.Max(p0.y, p1.y)), (int)Math.Abs(p1.x - p0.x), (int)Math.Abs(p1.y - p0.y));
        }
        else if (mask.On)
        {
            var b = mask;
            if (cr.RectClipping)
            {
                var rm = root != null ? RootLocalToScreen(root) : Matrix4x4.identity;
                var p0 = M.Point(rm, new Vector3(cr.ClipRect.xMin, cr.ClipRect.yMin, 0)); var p1 = M.Point(rm, new Vector3(cr.ClipRect.xMax, cr.ClipRect.yMax, 0));
                b = b.And(Math.Min(p0.x, p1.x), Math.Min(p0.y, p1.y), Math.Max(p0.x, p1.x), Math.Max(p0.y, p1.y));
                if (b.Empty) return;
            }
            int cx = (int)MathF.Floor(b.X0), cy = (int)MathF.Floor(G.H - b.Y1), cw = (int)MathF.Ceiling(b.X1) - cx, ch = (int)MathF.Ceiling(G.H - b.Y0) - cy;
            UiClipTo(cx, cy, cw, ch);
        }
        else UiNoClip();
        // was Emit(..., v => { var w = M.Point(m, v); return new Vector2(w.x, G.H - w.y); }, ...): the same expressions, inline
        var pos = cr.Pos; var idx = cr.Idx;
        if (pos == null || idx == null || pos.Length == 0 || idx.Length < 3) return;
        var v = Verts(pos.Length); var col = cr.Col; var uv = cr.Uv0; float gh = G.H;
        bool premul = blend == BLENDMODE_BLEND_PREMULTIPLIED;
        var ramp = sh.Ramp ? RampOf(mat) : null; var looks = ramp != null ? DerivedRender.Looks : null;
        for (int i = 0; i < pos.Length; i++)
        {
            var p = pos[i];
            float wx = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03, wy = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13;
            Vertex(ref v[i], wx, gh - wy, col, uv, i, tint, premul);
            if (ramp != null) { if (looks != null) looks.RampVertex(ref v[i], uv, cr.Tan, i); else RampShown(ref v[i]); }
        }
        Submit(v, pos.Length, idx, ramp ?? tex, blend, sh.Sdf);
    }

    /// <summary>Polish round 1: the menus' "UI/Watercolor-Background" (Lamb.UI.UIBackgroundMeshRenderer), drawn before as an untextured
    /// white sheet: the panel's colour is its 1-D ramp (_Texture1, clamped), opaque, the vertex rgb not used. How a vertex maps onto the
    /// ramp (the panel's reveal) was read from the material's compiled program, so it is owner-local since fid1-fixup round 2
    /// (DerivedRender.Looks: IShaderLooks.RampVertex); without it <see cref="RampShown"/> draws the fully shown panel. The material is
    /// picked by its shader name (ShaderInfo.Ramp) and its ramp by property name.</summary>
    static NTexture RampOf(NMaterial mat)
    {
        if (mat == null || !mat.P.TryGetValue(RampTexId, out var r)) return null;
        return RenderIcalls.Tex(r);
    }
    static readonly int RampTexId = ShaderIds.Id("_Texture1");
    /// <summary>Without the owner-local rule: the fully shown panel -- its ramp's first texel, opaque (what the menus show once the
    /// panel is in; its reveal is a cut here instead of a wipe).</summary>
    static void RampShown(ref Vertex d)
    {
        d.tex_coord.x = 0f; d.tex_coord.y = 0.5f;
        d.color.r = 1f; d.color.g = 1f; d.color.b = 1f; d.color.a = 1f;
    }

    // ---------- world ----------
    /// <summary>One world draw of a camera pass: its sort keys and what its draw needs (V6 stage 2: was a class + a closure per
    /// renderer per frame; now a reused struct array, filled by the scan in the same order, sorted by the same keys).</summary>
    struct WItem
    {
        public float Layer, Order, Dist; public int Seq;
        public bool Mesh, Fx, Fy, Spine; public NSprite Sprite; public Color Col; public uint Blend; public Matrix4x4 Lw;
        public Vector3[] Pos; public Color32[] MCol; public Vector4[] Uv; public int[][] Subs; public int NSubs; public Material[] Mats; public Dictionary<int, object> Mpb;
        /// <summary>Polish round 2: the sprite's material (FX policy) and the mesh record (the glow approximation's normals / box).</summary>
        public NMaterial Mat; public NMesh MeshRec;
        /// <summary>Kit #7 G6: a mesh draw's GameObject (only the census line names it).</summary>
        public NGameObject Go;
        /// <summary>Polish round 1: the SortingGroups this renderer sorts inside, outermost first (null: none).</summary>
        public SGroup[] Groups;
        /// <summary>Fidelity round 1: a SpriteShapeRenderer's generated geometry (Icalls/SpriteShapeIcalls.cs); null for other renderers.</summary>
        public Impl.SpriteShapeIcalls.Data Shape;
    }
    /// <summary>A SortingGroup as one sort unit of the frame (Unity: its renderers share the group's sorting layer, order and camera
    /// distance against everything outside it, and sort among themselves by their own keys; a nested group is one unit inside
    /// its parent's; sortAtRoot lifts a group to the top level). Seq: the scan index of the first renderer seen in it. Membership is
    /// stamped onto the transforms of each group's subtree (NTransform.SortGroup) once per pass, so a renderer's test is one field
    /// read; groups are rare (one at the Main Menu, ~150 in the intro).</summary>
    internal sealed class SGroup { public NBehaviour B; public float Layer, Order, Dist; public int Seq = -1, Stamp; public SGroup[] Chain; }
    static int _sgStamp;
    static readonly List<(int depth, NBehaviour b)> _sgList = new();
    static Vector3 _sgCam;
    static SGroup MemberOf(NTransform t) => t.SortGroup is SGroup g && g.Stamp == _sgStamp ? g : null;
    /// <summary>Once per pass with groups: every enabled, active SortingGroup in a scene, outermost first; each stamps its subtree (an
    /// inner group then re-stamps its own), so MemberOf gives a transform's innermost group.</summary>
    static void StampGroups()
    {
        _sgStamp++; _sgList.Clear();
        foreach (var n in Registry.SortingGroups.Items())
        {
            if (n is not NBehaviour b || b.Destroyed || !b.Enabled || b.Go == null || b.Go.Scene == 0 || !b.Go.ActiveInHierarchy) continue;
            int d = 0; for (var p = b.Go.Transform.Parent; p != null; p = p.Parent) d++;
            _sgList.Add((d, b));
        }
        _sgList.Sort((x, y) => x.depth.CompareTo(y.depth));   // List.Sort is unstable: ties are siblings' disjoint subtrees, order irrelevant
        foreach (var (_, b) in _sgList)
        {
            var t = b.Go.Transform;
            bool atRoot = b.Get("sortAtRoot") is { } sar && (sar is bool sb ? sb : (bool)Conv.To(sar, typeof(bool)));
            var parent = atRoot || t.Parent == null ? null : MemberOf(t.Parent);
            var g = new SGroup { B = b, Layer = SortingLayerValue(b), Order = SortOrder(b), Dist = SqrDist(t.Position, _sgCam), Stamp = _sgStamp };
            g.Chain = parent == null ? new[] { g } : parent.Chain.Append(g).ToArray();
            Stamp(t, g);
        }
        _sgList.Clear();
    }
    static void Stamp(NTransform t, SGroup g) { t.SortGroup = g; var ch = t.Children; for (int i = 0; i < ch.Count; i++) Stamp(ch[i], g); }
    static WItem[] _wi = new WItem[256]; static int[] _wOrder = new int[256], _wTmp = new int[256]; static int[][] _subsBuf = new int[16][];

    /// <summary>menu-speed hop fix: (a - b).sqrMagnitude as our own arithmetic: Vector3 - (a.x - b.x, ...) then sqrMagnitude
    /// x * x + y * y + z * z (left to right), the same single-precision operations.</summary>
    static float SqrDist(Vector3 a, Vector3 b)
    {
        float d = Um.SqrDist(a, b);
        if (HopCheck.On) HopCheck.F(HopCheck.SortDist, d, (a - b).sqrMagnitude);
        return d;
    }
    /// <summary>Color.white = new Color(1, 1, 1, 1) (hop fix: our own constant).</summary>
    static Color White() { if (HopCheck.On) HopCheck.Col(HopCheck.White, Um.White, Color.white); return Um.White; }

    static void DrawWorld(NBehaviour cam, in RectF pr, float gh)
    {
        int mask = cam.Get("cullingMask") is { } cm ? Convert.ToInt32(cm is UnityEngine.LayerMask lm ? lm.value : cm) : -1;
        var view = CameraIcalls.View(cam); var proj = CameraIcalls.Proj(cam); var vp = M.Mul(proj, view);
        FrustumPlanes(in vp); if (_litFrame) _lit.BeginCamera(in vp);
        if (_mtDefer) MtRender.Cur.NextPass(in vp, in pr, gh);   // SHEPHERD_MT_RENDER=1: this camera pass's view-projection, pixel rect and target height, once
        var camPos = cam.Go.Transform.Position; int n = 0, seq = 0;
        bool groups = Registry.SortingGroups.Count > 0; _sgCam = camPos;
        if (groups) StampGroups();
        foreach (var o in Registry.WorldRenderers.Snapshot())   // the SpriteRenderer / MeshRenderer records, in Objects.All order
        {
            if (o is not NComponent r || r.Destroyed || r.Go == null || r.Go.Scene == 0 || !r.Go.ActiveInHierarchy) continue;
            if ((mask & (1 << r.Go.Layer)) == 0) continue;
            // menu-speed (top hotspots): a renderer record keeps its property-bag reads while its bag is unchanged (SceneRenderer.WorldCache.cs)
            var nr = HotCfg.On ? r as NRenderer : null; var wc = nr == null ? null : nr.WC ??= new WCache();
            if (wc != null ? !wc.Enabled(nr) : r.Get("enabled") is { } en && !(en is bool eb ? eb : (bool)Conv.To(en, typeof(bool)))) continue;   // a boxed bool is what Conv.To returns as is
            var tr = r.Go.Transform;
            int kind = wc != null ? wc.Kind(nr) : r.Managed is SpriteRenderer ? 1 : r.Managed is MeshRenderer ? 2 : 0;   // (WCache.Kind: 3 = SpriteShapeRenderer)
            if (kind == 1)
            {
                NSprite ns; Color col; bool fx, fy; NMaterial mat;
                if (wc != null)
                {
                    ns = wc.NSpriteOf(wc.Sprite(nr)); if (ns == null) continue;
                    TouchTex(ns.Texture); ns.EnsureGeometry();
                    col = wc.Color(nr); wc.Flips(nr, out fx, out fy);
                    mat = wc.Material(nr);
                }
                else
                {
                    var sv = r.Get("sprite"); var sp = sv as Sprite ?? (sv is Assets.LazyRef ? RefTo<Sprite>(sv) : Conv.To(sv, typeof(Sprite)) as Sprite); ns = Objects.Get<NSprite>(sp); if (ns == null) continue;
                    RenderIcalls.Tex(ns.Texture?.Managed); ns.EnsureGeometry();
                    col = r.Get("color") is Color c ? c : White(); fx = Flag(r, "flipX"); fy = Flag(r, "flipY");
                    var shared = RendererIcalls.Shared(r); mat = Objects.Get<NMaterial>(shared.Length > 0 ? shared[0] : null);
                }
                var lw = tr.LocalToWorld;
                if (ns.Vertices == null || ns.Uv == null || ns.Triangles == null) StageTwo.SelectOfNull();   // (the old Select().ToArray() threw here)
                // polish round 2: the renderer's seq and SortingGroup stamp are taken before the frustum test, so culling a renderer leaves the
                // relative order of the others (and a group's position) exactly as before
                int sq = seq++; var grp = groups ? Grouped(tr, sq) : null;
                if (!Diag.K7.CullOff && SpriteOutside(ns, fx, fy, in lw)) { _culled++; continue; }   // (kit #7 SHEPHERD_K7_CULL=off: timing tests only)
                if (Runtime.Visibility.On) Runtime.Visibility.Seen(r);   // kit #8 (opt-in): visible in this camera
                ref var it = ref Item(n++);
                it.Mesh = false; it.Shape = null; it.Sprite = ns; it.Col = col; it.Fx = fx; it.Fy = fy; it.Lw = lw; it.Mat = mat; it.Blend = wc != null ? wc.Blend(mat) : ShaderOf(mat?.ShaderName ?? "Sprites/Default").BlendWorld;
                it.Layer = wc != null ? wc.Layer(nr) : SortingLayerValue(r); it.Order = wc != null ? wc.Order(nr) : SortOrder(r); it.Dist = SqrDist(tr.Position, camPos); it.Seq = sq;
                it.Groups = grp;
            }
            else if (kind == 2)
            {
                var mf = FirstMeshFilter(r.Go); if (mf == null) continue;
                var mesh = Objects.Get<NMesh>(Conv.To(mf.Get("sharedMesh") ?? mf.Get("mesh"), typeof(Mesh))); if (mesh == null || mesh.VertexCount == 0) continue;
                var mats = RendererIcalls.Shared(r); var mpb = r.Get("__mpb") as Dictionary<int, object>;
                var lw = tr.LocalToWorld;
                int sq = seq++; var grp = groups ? Grouped(tr, sq) : null;
                if (!Diag.K7.CullOff && MeshOutside(mesh, in lw)) { _culled++; continue; }
                if (Runtime.Visibility.On) Runtime.Visibility.Seen(r);   // kit #8 (opt-in)
                ref var it = ref Item(n++);
                it.Mesh = true; it.Shape = null; it.Pos = mesh.Pos; it.MCol = mesh.Col; it.Uv = mesh.Uv0; it.Mats = mats; it.Mpb = mpb; it.Lw = lw; it.MeshRec = mesh; it.Go = r.Go;
                it.Spine = SpineSkip.On && SpineSkip.IsSpine(r);   // kit #7 spine: its colour-0 triangles may leave the GPU draw (Render/SpineSkip.cs)
                it.NSubs = mesh.Sub.Count; if (it.Subs == null || it.Subs.Length < it.NSubs) it.Subs = new int[Math.Max(4, it.NSubs)][];
                mesh.Sub.CopyTo(it.Subs);   // the submesh list as it is now (the old code took mesh.Sub.ToArray() here)
                it.Layer = SortingLayerValue(r); it.Order = SortOrder(r); it.Dist = SqrDist(tr.Position, camPos); it.Seq = sq;
                it.Groups = grp;
            }
            else if (SpriteShapes.On && (wc != null ? kind == 3 : Registry.IsSpriteShapeRenderer(r)))
            {   // fidelity round 1: Unity culls a SpriteShapeRenderer by the local AABB the controller sets (SetLocalAABB), calls
                // OnWillRenderObject on the visible one (the controller bakes its geometry there), then draws the segments.
                // (integration with menu-speed: its bag reads -- enabled above, sharedMaterials, color, the sort keys -- through the world-pass
                // cache like a SpriteRenderer's, in the same order, read after OnWillRenderObject as before: game code there may write the
                // bag, which bumps PropsVer and re-reads them; SHEPHERD_HOTSPOT_CHECK=1 compares each with the original expression)
                var lw = tr.LocalToWorld;
                int sq = seq++; var grp = groups ? Grouped(tr, sq) : null;
                var d = Impl.SpriteShapeIcalls.Of(r);
                if (d != null && d.AabbSet && BoxOutside(in lw, d.LocalAabb.min.x, d.LocalAabb.min.y, d.LocalAabb.min.z, d.LocalAabb.max.x, d.LocalAabb.max.y, d.LocalAabb.max.z)) { _culled++; continue; }
                SpriteShapes.WillRender(r.Go);
                d = Impl.SpriteShapeIcalls.Of(r); if (d == null || !d.Prepared) continue;
                if (SpriteShapes.Outside(d, in lw)) { _culled++; continue; }
                if (Runtime.Visibility.On) Runtime.Visibility.Seen(r);   // kit #8 (opt-in)
                ref var it = ref Item(n++);
                it.Mesh = false; it.Sprite = null; it.Shape = d; it.Lw = lw; it.Mats = wc != null ? wc.Shared(nr) : RendererIcalls.Shared(r); it.Col = wc != null ? wc.Color(nr) : r.Get("color") is Color c ? c : White();
                it.Layer = wc != null ? wc.Layer(nr) : SortingLayerValue(r); it.Order = wc != null ? wc.Order(nr) : SortOrder(r); it.Dist = SqrDist(tr.Position, camPos); it.Seq = sq;
                it.Groups = grp;
            }
        }
        if (Runtime.Visibility.On) Runtime.Visibility.AfterCameraCull();   // kit #8 (opt-in): OnBecameVisible for the renderers this camera made visible
        // the old items.OrderBy(Layer).ThenBy(Order).ThenByDescending(Dist).ThenBy(Seq): Seq is unique, so the order is the one total order
        // of these keys under float.CompareTo, whatever sort finds it
        var ord = SortWorld(n);
        for (int k = 0; k < n; k++)
        {
            ref var it = ref _wi[ord[k]];
            if (it.Shape != null) { SpriteShapes.Draw(it.Shape, it.Mats, it.Col, in it.Lw, in vp, in pr, gh); continue; }   // (built here, also with SHEPHERD_MT_RENDER=1)
            if (!it.Mesh)
            {
                var ns = it.Sprite;
                var fx = ShaderPolicy.World(it.Mat, true, false, out var fb);   // polish round 2: the FX policy (Normal = the old draw, unchanged)
                if (fx == ShaderPolicy.Kind.Normal)
                {   // (the common draw: the MT branch inline, so the single-threaded path makes no extra call.) Fidelity round 1: the owner-local
                    // lit model decides per draw (LitBegin); a lit draw is built here, never captured for the render thread -- its shading runs
                    // at the draw, on this thread, and its DrawAdd goes to the backend (MT: recorded, in order)
                    LitBegin(it.Mat, in it.Lw, true);
                    if (_litOn || !_mtDefer || !MtRender.Cur.Sprite(ns.Vertices, ns.Uv, ns.Triangles, it.Fx, it.Fy, in it.Lw, in vp, in pr, gh, ns.Texture, it.Col, it.Blend, ns.GeomShared))
                        Emit0.EmitSprite(ns.Vertices, ns.Uv, ns.Triangles, it.Fx, it.Fy, in it.Lw, in vp, in pr, gh, ns.Texture, it.Col, it.Blend);
                    LitEnd();
                }
                else { FxDraw(it.Mat, fx, fb); if (fx != ShaderPolicy.Kind.Skip) EmitSprite(ns, ns.Vertices, ns.Uv, ns.Triangles, it.Fx, it.Fy, in it.Lw, in vp, in pr, gh, ns.Texture, fx == ShaderPolicy.Kind.Grab ? GrabTint(it.Mat, it.Col) : it.Col, fb); }
            }
            else
            {
                var mats = it.Mats; var mpb = it.Mpb;
                for (int s = 0; s < it.NSubs; s++)
                {
                    var mat = Objects.Get<NMaterial>(mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null);
                    var tex = mpb != null && mpb.TryGetValue(ShaderIds.MainTex, out var mt) && mt is Texture ? RenderIcalls.Tex(mt) : mat != null && mat.P.TryGetValue(ShaderIds.MainTex, out var t) ? RenderIcalls.Tex(t) : null;
                    var tint = mpb != null && mpb.TryGetValue(ShaderIds.Color, out var mc2) && mc2 is Color mcol2 ? mcol2 : mat != null && mat.P.TryGetValue(ShaderIds.Color, out var tc) && tc is Color tcol ? tcol : White();
                    var fx = ShaderPolicy.World(mat, tex != null, it.MeshRec != null, out var fb);
                    // kit #7 G6: the triangles Unity's GPU would cull by the shader's Cull state left out (the same array when nothing is culled)
                    var sub = G6Cull.On && fx != ShaderPolicy.Kind.Skip && fx != ShaderPolicy.Kind.Glow ? G6Cull.Filter(mat, it.MeshRec, s, it.Subs[s], in it.Lw, in vp, camPos, it.Go) : it.Subs[s];
                    switch (fx)
                    {
                        case ShaderPolicy.Kind.Normal:
                        {
                            uint bs = ShaderOf(mat?.ShaderName ?? "").BlendSpine;
                            LitBegin(mat, in it.Lw, false);   // (fidelity round 1: a lit draw is built here, as for sprites)
                            bool skip0 = it.Spine && !_litOn;   // kit #7 spine: never a lit draw (the lit model adds light whatever the vertex colour)
                            // (kit #7 integration: `sub` = the probes track's G6 triangle list -- culled triangles as -1, dropped by Emit's index filter before
                            //  the spine track's colour-0 filter sees the indices)
                            if (_litOn || !_mtDefer || !MtRender.Cur.Mesh(it.Pos, it.MCol, it.Uv, sub, in it.Lw, in vp, in pr, gh, tex, tint, bs, skip0))
                                Emit0.EmitMesh(it.Pos, it.MCol, it.Uv, sub, in it.Lw, in vp, in pr, gh, tex, tint, bs, skip0);
                            LitEnd();
                            break;
                        }
                        case ShaderPolicy.Kind.Skip: FxDraw(mat, fx, fb); break;
                        case ShaderPolicy.Kind.Glow:   // fidelity round 1: the owner-local post effects may draw it (their sphere glow, built and submitted here); else the engine's
                            FxDraw(mat, fx, fb); if (_post == null || !_post.AdditiveGlow(it.MeshRec, mat, mpb, it.Subs[s], in it.Lw, in vp, Um.ToRect(in pr), gh, camPos, tint)) EmitGlow(it.MeshRec, it.Subs[s], in it.Lw, in vp, in pr, gh, tint, camPos, fb); break;
                        default: FxDraw(mat, fx, fb); EmitMesh(it.Pos, it.MCol, it.Uv, sub, in it.Lw, in vp, in pr, gh, tex ?? WhiteTex(), fx == ShaderPolicy.Kind.Grab ? GrabTint(mat, tint) : tint, fb); break;
                    }
                }
            }
        }
        for (int k = 0; k < n; k++) { ref var it = ref _wi[k]; it.Sprite = null; it.Pos = null; it.MCol = null; it.Uv = null; it.Mats = null; it.Mpb = null; it.Groups = null; it.Mat = null; it.MeshRec = null; it.Shape = null; it.Go = null; if (it.Subs != null) Array.Clear(it.Subs); }   // keep nothing alive
    }
    // ---------- polish round 2: FX draws (Render/ShaderPolicy.cs) ----------
    static int _fxSkips, _fxBlends, _fxGlows, _fxGrabs, _fxRtZero;
    /// <summary>Last Frame: FX draws skipped / drawn with a non-default blend / as glows / as backdrop-blend (grab) draws, and draws of an
    /// RT known to hold nothing (skipped: exact under the alpha-weighted blends they use).</summary>
    public static int LastFxSkips, LastFxBlends, LastFxGlows, LastFxGrabs, LastFxRtZero;
    static void FxDraw(NMaterial mat, ShaderPolicy.Kind k, uint blend, NGameObject go = null)
    {
        switch (k) { case ShaderPolicy.Kind.Skip: _fxSkips++; break; case ShaderPolicy.Kind.Blend: _fxBlends++; break; case ShaderPolicy.Kind.Glow: _fxGlows++; break; case ShaderPolicy.Kind.Grab: _fxGrabs++; break; }
        ShaderPolicy.Note(mat, k, blend, go);
    }
    // ---------- fidelity round 1: the lit model's hooks (the model itself is owner-local: Render/DerivedRender.cs ILitShading) ----------
    static ILitShading _lit; static IPostEffects _post; static bool _litOn, _litFrame;
    /// <summary>Last frame's lit draws and fogged vertices (the [render] frame line; 0 without the owner-local lit model).</summary>
    internal static (int Lit, int Fog) ShadingStats => _lit?.LastStats ?? default;
    /// <summary>Before a normal world draw: the lit model decides whether this draw is lit (then SubmitFiltered hands it the triangles).</summary>
    internal static void LitBegin(NMaterial mat, in Matrix4x4 lw, bool sprite) => _litOn = _litFrame && _lit.Begin(mat, in lw, sprite);   // (an inactive frame: Begin would return false)
    internal static void LitEnd() { if (_litOn) { _litOn = false; _lit.End(); } }
    static NTexture _white;
    /// <summary>Texture2D.whiteTexture: what an FX draw with nothing to sample is drawn with, so its blend mode applies (an untextured
    /// draw would use the backends' draw blend, NONE).</summary>
    internal static NTexture WhiteTex() => _white is { Destroyed: false } w ? w : _white = RenderIcalls.SolidTexture("white", 255, 255, 255, 255);
    /// <summary>The source colour of a BlendModes grab draw: the tint, and what the material's own pass multiplies in -- a rule read from
    /// the game's compiled program, so owner-local since fid1-fixup round 2 (DerivedRender.Looks: IShaderLooks.GrabTint); without it, the
    /// tint.</summary>
    static Color GrabTint(NMaterial mat, Color tint) => DerivedRender.Looks is { } l ? l.GrabTint(mat, tint) : tint;
    // ---------- the world pass's draws: built now by Emit0 (as before), or with SHEPHERD_MT_RENDER=1 captured for the render thread ----------
    /// <summary>SHEPHERD_MT_RENDER=1, this frame's screen-camera passes: hand the draw's inputs to MtRender (it declines a draw whose texture
    /// handle is not ready without side effects: that one is built here, in order). (The FX draws below are never lit: LitBegin is only
    /// called for Normal draws.)</summary>
    static bool _mtDefer;
    static void EmitSprite(NSprite ns, Vector2[] verts, Vector2[] uvs, ushort[] tris, bool fx, bool fy, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, NTexture tex, Color tint, uint blend)
    {
        if (_mtDefer && MtRender.Cur.Sprite(verts, uvs, tris, fx, fy, in lw, in vp, in pr, gh, tex, tint, blend, ns.GeomShared)) return;
        Emit0.EmitSprite(verts, uvs, tris, fx, fy, in lw, in vp, in pr, gh, tex, tint, blend);
    }
    static void EmitMesh(Vector3[] pos, Color32[] col, Vector4[] uv, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, NTexture tex, Color tint, uint blend)
    {
        if (_mtDefer && MtRender.Cur.Mesh(pos, col, uv, idx, in lw, in vp, in pr, gh, tex, tint, blend)) return;
        Emit0.EmitMesh(pos, col, uv, idx, in lw, in vp, in pr, gh, tex, tint, blend);
    }
    static void EmitGlow(NMesh mesh, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, Color tint, Vector3 cam, uint blend)
    {
        var pos = mesh?.Pos;
        if (pos == null || idx == null || pos.Length == 0 || idx.Length < 3) return;   // (the glow's own early-out, before Texture2D.whiteTexture is taken)
        var white = WhiteTex();   // (was taken after the vertex loop, which has no side effects: the same order of side effects)
        if (_mtDefer && MtRender.Cur.Glow(pos, mesh.Normals, mesh.CullMin, mesh.CullMax, idx, in lw, in vp, in pr, gh, tint, cam, blend, white)) return;
        Emit0.EmitGlow(pos, mesh.Normals, mesh.CullMin, mesh.CullMax, idx, in lw, in vp, in pr, gh, tint, cam, blend, white);
    }
    static ref WItem Item(int i)
    {
        if (i >= _wi.Length) { Array.Resize(ref _wi, _wi.Length * 2); }
        return ref _wi[i];
    }
    /// <summary>Conv.To(v, typeof(T)) for a LazyRef v (a UObject target): its earlier tests all fail for a LazyRef and have no side
    /// effects, so it comes down to ResolveRef then the instance test.</summary>
    static T RefTo<T>(object v) where T : UnityEngine.Object { var r = Conv.ResolveRef?.Invoke(v, typeof(T)); return r as T; }
    internal static NComponent FirstMeshFilter(NGameObject go) { foreach (var c in go.Components) if (c.Managed is MeshFilter) return c; return null; }   // was Components.FirstOrDefault(c => c.Managed is MeshFilter)
    /// <summary>CameraIcalls.B(r, key, false) without Conv.To's reflection for the common boxed bool (Conv.To returns that box as is).</summary>
    static bool Flag(NComponent r, string key) => r.Get(key) is { } v && (v is bool b ? b : (bool)Conv.To(v, typeof(bool)));
    /// <summary>CameraIcalls.F(r, "sortingOrder", 0): a boxed float is returned as is by Conv.To and a boxed int becomes Convert.ToSingle(int)
    /// = (float)int; anything else still goes through Conv.To.</summary>
    static float SortOrder(NComponent r) => r.Get("sortingOrder") is { } v ? (v is float f ? f : v is int i ? (float)i : v is short h ? (float)h : (float)Conv.To(v, typeof(float))) : 0;   // (m_SortingOrder is serialized as SInt16: Convert.ToSingle(short) = (float)short)

    /// <summary>The sorted order of _wi[0..n) by (Layer, Order) ascending, Dist descending, Seq ascending, each with float.CompareTo's
    /// semantics (NaN first, -0 == +0) — exactly LINQ's comparer chain. Seq is unique, so the comparison is a strict total order and every
    /// correct sort gives the same permutation: last frame's, repaired by insertion when the item count is the same (the idle menu: the
    /// same renderers, a few keys changing), else a merge sort.</summary>
    static int[] SortWorld(int n)
    {
        if (_wOrder.Length < n) { _wOrder = new int[n * 2]; _wTmp = new int[n * 2]; _wPrevN = -1; }
        var o = _wOrder;
        if (n == _wPrevN)
        {   // last frame's order, fixed up by insertion (a few items moved: a few shifts); too many shifts -> the full sort
            int budget = 8 * n + 64;
            for (int i = 1; i < n && budget >= 0; i++) { int x = o[i], j = i - 1; while (j >= 0 && WCmp(o[j], x) > 0) { o[j + 1] = o[j]; j--; budget--; } o[j + 1] = x; }
            if (budget >= 0) { _wPrevN = n; return o; }
        }
        for (int k = 0; k < n; k++) o[k] = k;
        MergeSort(o, _wTmp, 0, n);
        _wPrevN = n; return o;
    }
    static int _wPrevN = -1;
    static int FCmp(float a, float b) => a < b ? -1 : a > b ? 1 : a == b ? 0 : float.IsNaN(a) ? (float.IsNaN(b) ? 0 : -1) : 1;   // float.CompareTo
    static SGroup[] Grouped(NTransform t, int seq)
    {
        var g = MemberOf(t); if (g == null) return null;
        foreach (var x in g.Chain) if (x.Seq < 0) x.Seq = seq;
        return g.Chain;
    }
    static int WCmp(int x, int y)
    {
        ref var a = ref _wi[x]; ref var b = ref _wi[y];
        if (a.Groups != null || b.Groups != null) return GCmp(ref a, ref b);
        int c = FCmp(a.Layer, b.Layer); if (c != 0) return c;
        c = FCmp(a.Order, b.Order); if (c != 0) return c;
        c = FCmp(b.Dist, a.Dist); if (c != 0) return c;   // descending
        return a.Seq < b.Seq ? -1 : a.Seq > b.Seq ? 1 : 0;
    }
    /// <summary>The comparison with SortingGroups: walk both group chains from the top level down; at the first level where the two
    /// differ, compare the two units there (a group or the renderer itself) by the usual keys.</summary>
    static int GCmp(ref WItem a, ref WItem b)
    {
        int na = a.Groups?.Length ?? 0, nb = b.Groups?.Length ?? 0;
        for (int i = 0; ; i++)
        {
            SGroup ga = i < na ? a.Groups[i] : null, gb = i < nb ? b.Groups[i] : null;
            if (ga != null && ReferenceEquals(ga, gb)) continue;
            float la = ga?.Layer ?? a.Layer, lb = gb?.Layer ?? b.Layer, oa = ga?.Order ?? a.Order, ob = gb?.Order ?? b.Order, da = ga?.Dist ?? a.Dist, db = gb?.Dist ?? b.Dist;
            int sa = ga?.Seq ?? a.Seq, sb = gb?.Seq ?? b.Seq;
            int c = FCmp(la, lb); if (c != 0) return c;
            c = FCmp(oa, ob); if (c != 0) return c;
            c = FCmp(db, da); if (c != 0) return c;   // descending
            if (sa != sb) return sa < sb ? -1 : 1;
            if (ga == null && gb != null) return -1;   // a renderer before a group whose first member it is (a stable tie-break)
            if (gb == null && ga != null) return 1;
            return a.Seq < b.Seq ? -1 : a.Seq > b.Seq ? 1 : 0;
        }
    }
    static void MergeSort(int[] a, int[] tmp, int lo, int hi)
    {
        if (hi - lo < 2) return;
        if (hi - lo <= 12) { for (int i = lo + 1; i < hi; i++) { int x = a[i], j = i - 1; while (j >= lo && WCmp(a[j], x) > 0) { a[j + 1] = a[j]; j--; } a[j + 1] = x; } return; }
        int mid = (lo + hi) >> 1;
        MergeSort(a, tmp, lo, mid); MergeSort(a, tmp, mid, hi);
        if (WCmp(a[mid - 1], a[mid]) <= 0) return;
        int p = lo, q = mid, w = lo;
        while (p < mid && q < hi) tmp[w++] = WCmp(a[q], a[p]) < 0 ? a[q++] : a[p++];
        while (p < mid) tmp[w++] = a[p++];
        while (q < hi) tmp[w++] = a[q++];
        Array.Copy(tmp, lo, a, lo, hi - lo);
    }

    public static Func<NComponent, float> SortingLayerValue = _ => 0f;
    /// <summary>Fidelity round 1: CotL's colour space is linear (its lit shaders, light colours and the grading pass run on linear colour;
    /// measured, vita/notes/fidelity-round1.md). The engine keeps sRGB-encoded RGBA8 targets and does the linear maths in the lit-shader and
    /// grading programs (the game's PlayerSettings are stripped, so this is a measured constant, not a read setting).</summary>
    public const bool LinearProject = true;

    /// <summary>What the draws derive from a shader name (Blend's substring tests, the SDF test), once per name string.</summary>
    internal sealed class ShaderInfo { public uint BlendWorld, BlendSpine, BlendUi; public bool Sdf, Ramp; }
    static readonly Dictionary<string, ShaderInfo> _shaders = new(ReferenceEqualityComparer.Instance);
    static ShaderInfo ShaderOf(string shader)
    {
        if (_shaders.TryGetValue(shader, out var si)) return si;
        si = new ShaderInfo { BlendWorld = Blend(shader, false), BlendSpine = Blend(shader, premulSpine: true), BlendUi = Blend(shader, premulSpine: shader.Contains("Spine")), Sdf = shader.Contains("Distance Field") || shader.Contains("TextMeshPro"), Ramp = shader == "UI/Watercolor-Background" };
        if (_shaders.Count < 4096) _shaders[shader] = si;
        return si;
    }
    static uint Blend(string shader, bool premulSpine)
    {
        var s = shader ?? "";
        if (s.Contains("Additive") || s.Contains("Add")) return BLENDMODE_ADD;
        if (s.Contains("Multiply")) return BLENDMODE_MOD;
        if (premulSpine && s.Contains("Spine") && !s.Contains("Straight")) return BLENDMODE_BLEND_PREMULTIPLIED;
        return BLENDMODE_BLEND;
    }

    static void FillScreen(Color c)
    {
        var v = new Vertex[4]; float[] xs = { 0, G.W, G.W, 0 }, ys = { 0, 0, G.H, G.H };
        for (int i = 0; i < 4; i++) v[i] = new Vertex { position = new() { x = xs[i], y = ys[i] }, color = new() { r = c.r, g = c.g, b = c.b, a = 1 } };
        var fi = new[] { 0, 1, 2, 0, 2, 3 };
        G.Draw(IntPtr.Zero, BLENDMODE_NONE, false, v, fi); RenderCheck.Live?.Draw(IntPtr.Zero, BLENDMODE_NONE, false, v, fi);
    }

    // ---------- vertex building (V6 stage 2: was one Emit with a projection delegate per vertex, a new Vertex[] and a LINQ index
    // filter per draw; the arithmetic below is the old delegates' and Emit's, expression for expression) ----------
    // (menu-speed, multicore stage: the world pass's EmitSprite / EmitMesh / EmitGlow and the near/far clipping live in Render/WorldEmit.cs, on
    // an instance that owns its buffers; Emit0 is the frame's own, and its VBuf / IBuf are the UI pass's shared buffers, as before. The
    // owner-local render code reaches them through RenderKit: Emit0's buffers, its clipping and SubmitFiltered.)
    internal static readonly WorldEmitter Emit0 = new();
    internal static Vertex[] Verts(int n) => Emit0.Verts(n);
    internal static Vector4[] ClipBuf(int n) => Emit0.ClipBuf(n);
    /// <summary>Emit's per-vertex colour / uv: c = (col ? (Color)col[i] : white) * tint, premultiplied for BLEND_PREMULTIPLIED; uv flipped.</summary>
    /// <summary>Kit #6 fps (FpsCfg.InlineVertex): (float)(int)b / 255f for every byte b -- the Color32 -> Color conversion of <see cref="Vertex"/>,
    /// filled by that same expression, so a lookup gives its bits. The hot vertex loops (the UI pass, the world mesh build) use it with
    /// Vertex's body written out in the loop (Mono refuses to inline Vertex: over MONO_AGGRESSIVE_INLINELIMIT).</summary>
    internal static readonly float[] B2F = MakeB2F();
    static float[] MakeB2F() { var t = new float[256]; for (int b = 0; b < 256; b++) t[b] = (float)(int)(byte)b / 255f; return t; }
    internal static void Vertex(ref Vertex d, float x, float y, Color32[] col, Vector4[] uv, int i, Color tint, bool premul)
    {
        float cr, cg, cb, ca;
        if (col != null && i < col.Length) { var k = col[i]; cr = (float)(int)k.r / 255f; cg = (float)(int)k.g / 255f; cb = (float)(int)k.b / 255f; ca = (float)(int)k.a / 255f; }   // Color32 -> Color
        else { cr = 1f; cg = 1f; cb = 1f; ca = 1f; }   // Color.white
        cr *= tint.r; cg *= tint.g; cb *= tint.b; ca *= tint.a;   // Color * Color
        if (premul) { cr *= ca; cg *= ca; cb *= ca; }
        d.position.x = x; d.position.y = y; d.color.r = cr; d.color.g = cg; d.color.b = cb; d.color.a = ca;
        if (uv != null && i < uv.Length) { var u = uv[i]; d.tex_coord.x = u.x; d.tex_coord.y = 1f - u.y; } else { d.tex_coord.x = 0f; d.tex_coord.y = 1f - 0f; }
    }
    /// <summary>The world pass's projection: ToScreen(M.Point(lw, p)), behind-camera clamp included. Polish round 2: also hands back the
    /// vertex's clip-space position (before the clamp) and whether it lies outside the near or far plane; sx/sy are the same
    /// expressions as before, so a vertex inside both planes projects bit for bit as it did.</summary>
    internal static void Project(in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, float px, float py, float pz, out float sx, out float sy, ref Vector4 clip, ref bool outside)
    {
        float wx = lw.m00 * px + lw.m01 * py + lw.m02 * pz + lw.m03, wy = lw.m10 * px + lw.m11 * py + lw.m12 * pz + lw.m13, wz = lw.m20 * px + lw.m21 * py + lw.m22 * pz + lw.m23;
        float cx = vp.m00 * wx + vp.m01 * wy + vp.m02 * wz + vp.m03, cy = vp.m10 * wx + vp.m11 * wy + vp.m12 * wz + vp.m13, cw = vp.m30 * wx + vp.m31 * wy + vp.m32 * wz + vp.m33;
        float cz = vp.m20 * wx + vp.m21 * wy + vp.m22 * wz + vp.m23;
        clip.x = cx; clip.y = cy; clip.z = cz; clip.w = cw;
        if (cz + cw < 0f || cw - cz < 0f) outside = true;   // OpenGL clip space (Unity's projection): near z = -w, far z = +w
        if (cw <= 1e-5f) cw = 1e-5f;
        sx = pr.X + (cx / cw * 0.5f + 0.5f) * pr.W; sy = gh - (pr.Y + (cy / cw * 0.5f + 0.5f) * pr.H);   // (pr.x/.width/.y/.height: the same four fields)
    }
    // ---------- polish round 2: frustum culling and near/far clipping (survey item 3) ----------
    // Before, DrawWorld projected every vertex on the CPU and clamped w to 1e-5: a triangle with a vertex behind the camera exploded
    // across the screen (the intro's 'Room Back Sprite(Clone)': bbox +-2e17 px), and every renderer was built, sorted and batched
    // even when it was entirely off screen (1,023 of 1,785 intro sprite draws). Now:
    // - a renderer whose world bounding box (vertex box x LocalToWorld) lies entirely outside one of the camera's six frustum
    //   planes is not drawn (Unity culls per renderer the same way, with its bounds); it still takes its sort seq, so the
    //   remaining draws sort exactly as before
    // - a triangle with a vertex outside the near or far plane is clipped against it in clip space (Sutherland-Hodgman); new
    //   vertices interpolate position, colour and uv linearly in clip space (perspective-correct at the vertices; the 2D
    //   backends interpolate affinely between them, as for every triangle). Triangles entirely inside both planes are untouched:
    //   their vertices, indices and order are the old ones bit for bit.
    static readonly float[] _fp = new float[24];   // the camera's frustum planes (a, b, c, d) x 6, from its view-projection (Gribb-Hartmann)
    static int _culled;   // (the clip / drop counters: WorldEmitter.ClipTris / DropTris)
    /// <summary>Per-frame counters: renderers culled, triangles clipped / dropped against the near-far planes (last Frame).</summary>
    public static int LastCulled, LastClipTris, LastDropTris;
    internal static void FrustumPlanes(in Matrix4x4 m)
    {
        for (int k = 0; k < 6; k++)
        {
            int row = k >> 1; float s = (k & 1) == 0 ? 1f : -1f;   // left/right (row 0), bottom/top (row 1), near/far (row 2)
            float rx = row == 0 ? m.m00 : row == 1 ? m.m10 : m.m20, ry = row == 0 ? m.m01 : row == 1 ? m.m11 : m.m21, rz = row == 0 ? m.m02 : row == 1 ? m.m12 : m.m22, rw = row == 0 ? m.m03 : row == 1 ? m.m13 : m.m23;
            _fp[k * 4] = m.m30 + s * rx; _fp[k * 4 + 1] = m.m31 + s * ry; _fp[k * 4 + 2] = m.m32 + s * rz; _fp[k * 4 + 3] = m.m33 + s * rw;
            _fpa[k * 3] = MathF.Abs(_fp[k * 4]); _fpa[k * 3 + 1] = MathF.Abs(_fp[k * 4 + 1]); _fpa[k * 3 + 2] = MathF.Abs(_fp[k * 4 + 2]);
        }
    }
    static readonly float[] _fpa = new float[18];   // menu-speed (top hotspots): |a|, |b|, |c| of each plane, once per pass (BoxOutside took them per renderer)
    /// <summary>|x| for BoxOutside: x &lt; 0 ? -x : x. menu-speed (top hotspots): Mono's arm64 AOT (no LLVM) builds MathF.Abs through a store and a
    /// reload of the float, ~27 per renderer (6.8 % of the engine-AOT menu frame). It differs from MathF.Abs only on -0 (stays -0) and on a
    /// negative NaN (stays negative): signed zeros can only change the sign of a zero result and NaN stays NaN through every + and *, so the
    /// plane test's &lt; 0 -- false for any zero and any NaN -- gives the same answer (SHEPHERD_HOTSPOT_CHECK=1 compares it with BoxOutsideRef).</summary>
    static float Abs0(float x) => x < 0f ? -x : x;
    /// <summary>The local box [mn, mx] under lw, tested against the frustum: true = entirely outside one plane (NaN: never culled).</summary>
    static bool BoxOutside(in Matrix4x4 lw, float mnx, float mny, float mnz, float mxx, float mxy, float mxz)
    {
        if (!HotCfg.On) return BoxOutsideRef(in lw, mnx, mny, mnz, mxx, mxy, mxz);
        float cx = (mnx + mxx) * 0.5f, cy = (mny + mxy) * 0.5f, cz = (mnz + mxz) * 0.5f, ex = (mxx - mnx) * 0.5f, ey = (mxy - mny) * 0.5f, ez = (mxz - mnz) * 0.5f;
        float wx = lw.m00 * cx + lw.m01 * cy + lw.m02 * cz + lw.m03, wy = lw.m10 * cx + lw.m11 * cy + lw.m12 * cz + lw.m13, wz = lw.m20 * cx + lw.m21 * cy + lw.m22 * cz + lw.m23;
        float hx = Abs0(lw.m00) * ex + Abs0(lw.m01) * ey + Abs0(lw.m02) * ez, hy = Abs0(lw.m10) * ex + Abs0(lw.m11) * ey + Abs0(lw.m12) * ez, hz = Abs0(lw.m20) * ex + Abs0(lw.m21) * ey + Abs0(lw.m22) * ez;
        var fp = _fp; var fa = _fpa; bool r = false;
        for (int k = 0, j = 0; k < 24; k += 4, j += 3)
        {
            float a = fp[k], b = fp[k + 1], c = fp[k + 2];
            if (a * wx + b * wy + c * wz + fp[k + 3] + fa[j] * hx + fa[j + 1] * hy + fa[j + 2] * hz < 0f) { r = true; break; }
        }
        if (HotCfg.Check) HotCheck.Bool(HotCheck.Abs, r, BoxOutsideRef(in lw, mnx, mny, mnz, mxx, mxy, mxz));
        return r;
    }
    /// <summary>BoxOutside as it was (SHEPHERD_HOTSPOTS=0, and the check).</summary>
    static bool BoxOutsideRef(in Matrix4x4 lw, float mnx, float mny, float mnz, float mxx, float mxy, float mxz)
    {
        float cx = (mnx + mxx) * 0.5f, cy = (mny + mxy) * 0.5f, cz = (mnz + mxz) * 0.5f, ex = (mxx - mnx) * 0.5f, ey = (mxy - mny) * 0.5f, ez = (mxz - mnz) * 0.5f;
        float wx = lw.m00 * cx + lw.m01 * cy + lw.m02 * cz + lw.m03, wy = lw.m10 * cx + lw.m11 * cy + lw.m12 * cz + lw.m13, wz = lw.m20 * cx + lw.m21 * cy + lw.m22 * cz + lw.m23;
        float hx = MathF.Abs(lw.m00) * ex + MathF.Abs(lw.m01) * ey + MathF.Abs(lw.m02) * ez, hy = MathF.Abs(lw.m10) * ex + MathF.Abs(lw.m11) * ey + MathF.Abs(lw.m12) * ez, hz = MathF.Abs(lw.m20) * ex + MathF.Abs(lw.m21) * ey + MathF.Abs(lw.m22) * ez;
        for (int k = 0; k < 24; k += 4)
        {
            float a = _fp[k], b = _fp[k + 1], c = _fp[k + 2];
            if (a * wx + b * wy + c * wz + _fp[k + 3] + MathF.Abs(a) * hx + MathF.Abs(b) * hy + MathF.Abs(c) * hz < 0f) return true;
        }
        return false;
    }
    static bool SpriteOutside(NSprite ns, bool fx, bool fy, in Matrix4x4 lw)
    {
        var vs = ns.Vertices; if (vs == null || vs.Length == 0) return false;
        if (!ReferenceEquals(ns.CullOf, vs))
        {
            Vector2 mn = vs[0], mx = vs[0];
            for (int i = 1; i < vs.Length; i++) { var p = vs[i]; if (p.x < mn.x) mn.x = p.x; if (p.y < mn.y) mn.y = p.y; if (p.x > mx.x) mx.x = p.x; if (p.y > mx.y) mx.y = p.y; }
            ns.CullMin = mn; ns.CullMax = mx; ns.CullOf = vs;
        }
        float x0 = fx ? -ns.CullMax.x : ns.CullMin.x, x1 = fx ? -ns.CullMin.x : ns.CullMax.x, y0 = fy ? -ns.CullMax.y : ns.CullMin.y, y1 = fy ? -ns.CullMin.y : ns.CullMax.y;
        return BoxOutside(in lw, x0, y0, 0f, x1, y1, 0f);
    }
    internal static bool MeshOutside(NMesh m, in Matrix4x4 lw)
    {
        if (m.CullVer != m.Version)
        {   // menu-speed hop fix: Vector3.Min / Max as our own arithmetic: Mathf.Min(a, b) = a < b ? a : b, Mathf.Max(a, b) = a > b ? a : b
            // per component (a = the running box, b = the vertex; a NaN operand gives b), the same comparisons in the same order
            var ps = m.Pos; var p0 = ps[0];
            float nx = p0.x, ny = p0.y, nz = p0.z, xx = p0.x, xy = p0.y, xz = p0.z;
            bool chk = HopCheck.On;
            for (int i = 1; i < ps.Length; i++)
            {
                var p = ps[i];
                Vector3 um = default, ux = default; if (chk) { um = Vector3.Min(Um.V3(nx, ny, nz), p); ux = Vector3.Max(Um.V3(xx, xy, xz), p); }
                nx = Um.Min(nx, p.x); ny = Um.Min(ny, p.y); nz = Um.Min(nz, p.z);
                xx = Um.Max(xx, p.x); xy = Um.Max(xy, p.y); xz = Um.Max(xz, p.z);
                if (chk) { HopCheck.V3(HopCheck.MeshMinMax, Um.V3(nx, ny, nz), um); HopCheck.V3(HopCheck.MeshMinMax, Um.V3(xx, xy, xz), ux); }
            }
            m.CullMin = Um.V3(nx, ny, nz); m.CullMax = Um.V3(xx, xy, xz); m.CullVer = m.Version;
        }
        return BoxOutside(in lw, m.CullMin.x, m.CullMin.y, m.CullMin.z, m.CullMax.x, m.CullMax.y, m.CullMax.z);
    }
    /// <summary>Emit's index filter (idx.Where(i => i >= 0 && i &lt; nv)), then the draw (the UI reference pass; WorldEmitter.Submit).</summary>
    static void Submit(Vertex[] v, int nv, int[] idx, NTexture tex, uint blend, bool sdf) => Emit0.Submit(v, nv, idx, tex, blend, sdf);
    /// <summary>The draw of v[0..nv) with the (already filtered) indices ib[0..ni), whole triangles only. menu-speed: ib is Emit0.IBuf, or a
    /// CanvasRenderer's cached index block (SceneRenderer.UiCache.cs).</summary>
    internal static void SubmitIdx(Vertex[] v, int nv, int[] ib, int ni, NTexture tex, uint blend, bool sdf)
    {
        if (ni - ni % 3 < 3) return;
        ni -= ni % 3;
        if (_curTarget == IntPtr.Zero) BlackFrame.Draw(v, nv, ni, tex, blend, G.W, G.H);   // kit #4 M1 (reads the draw, changes nothing; every screen draw: one drawn on top of a cover counts -- text, Spine, any mesh -- k4 review)
        var h = Handle(tex, sdf);
        if (_litOn && !sdf) { LitDraw(v, nv, ib, ni, h, blend); return; }   // fidelity round 1: lit shaders (owner-local; world draws only)
        var vs = new Span<Vertex>(v, 0, nv); var ix = new Span<int>(ib, 0, ni);
        if (UiCap != null) { UiCap.Draw(h, blend, sdf, vs, ix); if (UiCapOnly) return; }   // SHEPHERD_UICACHE_CHECK=1 only
        G.Draw(h, blend, sdf, vs, ix); LastDraws++; LastTris += ni / 3;
        RenderCheck.Live?.Draw(h, blend, sdf, vs, ix);
    }
    /// <summary>kit #7 spine (Render/SpineSkip.cs): a draw whose every triangle was left out -- what SubmitIdx does before the backend gets
    /// it (the early-out, the black-frame watch on the main target), no backend call.</summary>
    internal static void SkippedDraw(Vertex[] v, int nv, int ni, NTexture tex, uint blend)
    {
        if (ni - ni % 3 < 3) return;
        ni -= ni % 3;
        if (_curTarget == IntPtr.Zero) BlackFrame.Draw(v, nv, ni, tex, blend, G.W, G.H);
    }
    /// <summary>Fidelity round 1 (main's SubmitFiltered): the draw of v[0..nv) with Emit0.IBuf[0..ni) -- what the owner-local code's draws go
    /// through (RenderKit.Submit) and SpriteShapes.Draw: built on this thread, submitted like any world draw (the lit path while a lit draw is open).</summary>
    internal static void SubmitFiltered(Vertex[] v, int nv, int ni, NTexture tex, uint blend, bool sdf) => SubmitIdx(v, nv, Emit0.IBuf, ni, tex, blend, sdf);
    /// <summary>Kit #6 gpulight (RenderKit.SubmitGpuMesh): a static-mesh draw whose vertices the GPU makes (IGpuMesh), with SubmitIdx's
    /// bookkeeping: the texture handle (uploaded on demand), the draw counters (LastTris: every triangle of the range, culled ones included)
    /// and the stream-hash record (kind 7). False -- nothing done, the caller builds the draw on the CPU -- when the backend has no such
    /// draw, when the check modes need the CPU stream (SHEPHERD_RENDER_CHECK, SHEPHERD_UICACHE_CHECK), when a lit draw is open, or on the
    /// main target after a cover (BlackFrame reads the vertices of every later screen draw).</summary>
    internal static bool SubmitGpuMesh(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, NTexture tex, uint blend)
    {
        if (G is not IGpuMesh gm || program <= 0 || count < 3 || RenderCheck.On || UiCap != null || _litOn) return false;
        if (_curTarget == IntPtr.Zero && BlackFrame.CoveredSoFar) return false;
        var h = Handle(tex, false);
        gm.DrawGpuMesh(program, data, stride, first, count, uniforms, h, blend); LastDraws++; LastTris += count / 3;
        RenderCheck.Live?.Gpu(h, blend, program, first, count, data, uniforms);
        return true;
    }
    /// <summary>The index buffer of the lit draw being shaded (RenderKit.Indices while ILitShading.Draw runs; else Emit0.IBuf).</summary>
    internal static int[] LitIdx;
    /// <summary>A lit draw: the owner-local model shades v[0..nv) in place and draws it through IAddDraw (the backend: SHEPHERD_MT_RENDER=1, the
    /// proxy records it with its added colours). The draw's index buffer is handed over as RenderKit.Indices (a world draw's is Emit0.IBuf;
    /// passing it explicitly keeps the contract exact for any caller). SHEPHERD_STREAM_HASH: the shaded vertices and indices are hashed as a
    /// lit draw (kind 6; its per-vertex added colours stay inside the owner-local code: MtCheck compares them when the switch is on).</summary>
    static void LitDraw(Vertex[] v, int nv, int[] ib, int ni, IntPtr h, uint blend)
    {
        LitIdx = ib;
        try { _lit.Draw(v, nv, ni, h, blend); }
        finally { LitIdx = null; }
        LastDraws++; LastTris += ni / 3;
        if (RenderCheck.Live is { } live && !RenderCheck.On) live.Lit(h, blend, new Span<Vertex>(v, 0, nv), new Span<int>(ib, 0, ni));
    }

    /// <summary>Handle's side-effect-free part (menu-speed, multicore stage): true with the handle Handle(t, sdf) returns when it would return it
    /// without doing anything else -- no texture, a texture with nothing to upload, or one whose upload is current; false when Handle would
    /// load, upload, or bind / clear a render target. The same tests as the first lines of Handle, in the same order (Handle keeps its own
    /// copy: no extra call on the single-threaded path); SHEPHERD_MT_CHECK=1 calls Handle wherever this said "ready" and compares.</summary>
    internal static bool TryHandle(NTexture t, bool sdf, out IntPtr h)
    {
        h = IntPtr.Zero;
        if (t == null || t.Destroyed) return true;
        if (t.IsRenderTexture || !t.Loaded) return false;
        if ((Assets.Diet.KeepRgba ? t.Rgba == null : t.Rgba == null && (t.Src == null || t.DecodeFailed)) || t.Width <= 0) return true;
        if (sdf ? t.CachedSdf && t.CacheVSdf == t.Version : t.Cached && t.CacheV == t.Version) { h = sdf ? t.CacheHSdf : t.CacheH; return true; }
        return false;
    }
    internal static IntPtr Handle(NTexture t, bool sdf)
    {
        if (t == null || t.Destroyed) return IntPtr.Zero;
        if (t.IsRenderTexture) { t.RtSampled = Runtime.Time.FrameCount; return RtHandle(t); }   // polish round 2: the RT's render target
        if (!t.Loaded) RenderIcalls.Tex(t.Managed);
        // V3 diet: a pristine asset texture has no RGBA copy between uploads -> decode for the upload, then let it go
        if ((Assets.Diet.KeepRgba ? t.Rgba == null : t.Rgba == null && (t.Src == null || t.DecodeFailed)) || t.Width <= 0) return IntPtr.Zero;
        // stage 2: the (texture, sdf) -> (handle, version) cache lives on the record (was a Dictionary<(NTexture, bool), ...>, whose
        // tuple keys were boxed on every lookup under Mono: ~1,800 boxes per menu frame)
        bool found = sdf ? t.CachedSdf : t.Cached; var e = sdf ? (h: t.CacheHSdf, ver: t.CacheVSdf) : (h: t.CacheH, ver: t.CacheV);
        if (found && e.ver == t.Version) return e.h;
        if (Assets.PackRecorder.On) Assets.PackRecorder.Texture(t, sdf ? 1 : 0);
        if (t.Rgba == null && t.Src?.Image is { TopDown: true })   // V3 pack: GPU image (reduced DXT / RGBA), uploaded as stored
        {
            var hp = Assets.PackAssets.Upload(t, sdf, G);
            if (e.h != IntPtr.Zero) G.DestroyTexture(e.h);
            Cache(t, sdf, hp); return hp;
        }
        if (G is ICompressedUpload { Dxt: true } cu && !sdf && GlTextures.Compressed(t, cu) is var hc && hc != IntPtr.Zero)   // SHEPHERD_GL_DXT=1 only
        {
            if (e.h != IntPtr.Zero) G.DestroyTexture(e.h);
            Cache(t, sdf, hc); return hc;
        }
        Assets.PackRecorder.InUpload = true;
        var px = RenderIcalls.Pixels(t, keep: false); Assets.PackRecorder.InUpload = false; if (px == null) return IntPtr.Zero;
        if (e.h != IntPtr.Zero) G.DestroyTexture(e.h);
        // sdf: the backend renders the distance field (SDL bakes it into a copy on upload, GL applies it in the shader)
        var h = G.CreateTexture(t.Width, t.Height, px, t.FilterMode != 0, sdf, owned: !ReferenceEquals(px, t.Rgba));
        Cache(t, sdf, h); return h;
    }
    static void Cache(NTexture t, bool sdf, IntPtr h)
    {
        if (sdf) { t.CachedSdf = true; t.CacheHSdf = h; t.CacheVSdf = t.Version; } else { t.Cached = true; t.CacheH = h; t.CacheV = t.Version; }
    }
}
