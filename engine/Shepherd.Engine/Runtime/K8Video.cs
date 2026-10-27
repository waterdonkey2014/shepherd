using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Render;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;
#if !SHEPHERD_NO_K8

/// <summary>Device kit #8, track boot (vita/notes/kit8-boot.md): REAL VIDEO PLAYBACK -- the game's VideoPlayer clips through the Vita's
/// hardware H.264 decoder, drawn where Unity draws them. Kits #5-#7 only kept a clip's timing (Icalls/VideoMiscIcalls.cs, kit #6's timing:
/// prepareCompleted / started / loopPointReached at the clip's own length) and drew nothing, so the Devolver logo was a black screen with
/// its sound. Now, when a VideoPlayer starts playing a clip that has an owner-local .shv (ux0:data/shepherd/data/video/&lt;clip&gt;.shv, made
/// at pack time by vita/tools/splash_pack.py from the owner's own copy of the clip):
/// <list type="bullet">
/// <item>the host opens the clip (vita/shepherd/k8_boot.inc.c shepherd_vita_video_open: the clip and the decoder's work area in vitaGL's
///   VRAM) and the backend makes a ring of three RGBA textures whose own memory the decoder writes (IVideoTexture);</item>
/// <item>each engine frame, in the video update (PostLateUpdate.UpdateVideo, where kit #6's timing already runs), the frame the VideoPlayer's
///   clock is at (the same frame number get_frame answers: floor(elapsed x rate), Unity's skipOnDrop) is decoded into the next texture of the
///   ring (the frames between are decoded too: H.264 needs them);</item>
/// <item>the picture is drawn as Unity draws a CameraNearPlane video: after the frame's world pass (the target camera is the top one -- the
///   MMVideoPlayer prefab's camera has depth 100 and clears to black; its own Stylizer is disabled), before the overlay canvases (the skip
///   prompt), Fit Inside the target camera's pixel rect (the clip's picture area from the .shv: 960 x 540 on the 960 x 544 screen, the bars
///   black), with the camera alpha (m_TargetCameraAlpha) as the vertex alpha;</item>
/// <item>when the clip ends, is stopped or its VideoPlayer is destroyed (kit #6's Ended / K6Stop), the stream is closed and the textures
///   destroyed: the next frame shows what Unity's does after Stop -- no picture.</item>
/// </list>
/// Only CameraNearPlane VideoPlayers are drawn (the game plays every clip through the MMVideoPlayer prefab, which uses it); any other render
/// mode keeps kit #7's timing-only stand-in. A clip without a .shv, a refused file, no VRAM or a decoder error: one WARN line, kit #7's
/// black picture. The timing and the events stay kit #6's -- only the picture is added.
/// <c>SHEPHERD_K8_VIDEO=1</c> turns it on (the launcher apps' host sets it unless the env file says otherwise: their device run is where the
/// decoder is exercised first); unset / <c>0</c> (and <c>SHEPHERD_K8_BOOT=0</c>) = kit #7. Vita GL host only (the null backend / the Mac:
/// never).</summary>
[ProfileOnly]
public static unsafe class K8Video
{
    public static readonly bool On = Decide();
    static bool Decide()
    {
        if (!Profile.Vita || !ShepGl.On || !Diag.VitaHost.On) return false;
        if (Environment.GetEnvironmentVariable("SHEPHERD_K8_BOOT") == "0") return false;
        if (Environment.GetEnvironmentVariable("SHEPHERD_K8_VIDEO") != "1") return false;
        try { return HostApi() > 0; } catch (Exception) { return false; }
    }
    /// <summary>Kit #8 review fix: the clips real video plays by default -- only those a kit route has reached and checked under vlx (the Devolver
    /// logo at level0). The intro's crown -> rebirth clip plays at the memory-critical intro scene and no route reaches it yet, so it stays black
    /// (kit #7's picture) until <c>SHEPHERD_K8_VIDEO_CLIPS</c> names it (a comma list of clip names, or <c>all</c>).</summary>
    const string CheckedClips = "Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4";
    static readonly string Clips = Environment.GetEnvironmentVariable("SHEPHERD_K8_VIDEO_CLIPS") is { Length: > 0 } c ? c : CheckedClips;
    static bool ClipOn(string clip)
    {
        if (Clips == "all") return true;
        foreach (var n in Clips.Split(',')) if (n.Trim() == clip) return true;
        return false;
    }
    const int Ring = 3;
    sealed class Stream
    {
        public NObj V; public string Clip; public int Frames, Num, Den, W, H, CX, CY, CW, CH;
        public readonly IntPtr[] Tex = new IntPtr[Ring], Data = new IntPtr[Ring];
        public int Slot = -1; public long Shown = -1; public bool Failed; public int Draws;
    }
    static Stream _cur;
    static int _opened, _drawn;

    /// <summary>The VideoPlayer <paramref name="v"/> started playing <paramref name="clip"/> (kit #6's Playing phase began).</summary>
    public static void Start(NObj v, string clip)
    {
        if (!On || v == null || string.IsNullOrEmpty(clip)) return;
        if (_cur != null) Close();
        int mode = v.Get("renderMode") is { } rm ? Convert.ToInt32(rm) : -1;
        if (mode != 1) { Log.Info("[k8] video '" + clip + "': render mode " + mode.ToString() + " is not CameraNearPlane: timing only (kit #7)"); return; }
        if (!ClipOn(clip)) { Log.Info("[k8] video '" + clip + "': not in SHEPHERD_K8_VIDEO_CLIPS (only clips a kit route has checked play by default; =all or a list of names turns more on): drawn black (kit #7)"); return; }
        string path;
        try { path = Path.Combine(Program.Root, "data", "video", clip + ".shv"); if (!File.Exists(path)) { Log.Info("[k8] video '" + clip + "': no " + path + ": drawn black (kit #7)"); return; } }
        catch (Exception) { return; }
        var info = new int[9];
        int id;
        fixed (int* p = info) id = HostOpen(path, p, info.Length);
        if (id <= 0) { Log.Warn("[k8] video '" + clip + "': the host did not open it (" + id.ToString() + "): drawn black (kit #7; the host's K8 video line says why)"); return; }
        var s = new Stream { V = v, Clip = clip, Frames = info[0], Num = info[1], Den = info[2], W = info[3], H = info[4], CX = info[5], CY = info[6], CW = info[7], CH = info[8] };
        var vt = SceneRenderer.G as IVideoTexture;
        for (int k = 0; k < Ring && vt != null; k++)
        {
            s.Tex[k] = vt.CreateVideoTexture(s.W, s.H, out s.Data[k]);
            if (s.Tex[k] == IntPtr.Zero || s.Data[k] == IntPtr.Zero) { vt = null; }
        }
        _cur = s;
        if (vt == null) { s.Failed = true; Log.Warn("[k8] video '" + clip + "': no video texture from the backend: drawn black (kit #7)"); Close(); return; }
        _opened++;
        Log.Info("[k8] video '" + clip + "': real playback (" + s.Frames.ToString() + " frames at " + s.Num.ToString() + "/" + s.Den.ToString() + ", " + s.W.ToString() + "x" + s.H.ToString()
            + ", picture " + s.CW.ToString() + "x" + s.CH.ToString() + " at " + s.CX.ToString() + "," + s.CY.ToString() + "; 3 textures)");
    }
    /// <summary>The video update: the clip frame the VideoPlayer is at this engine frame (-1 = none yet).</summary>
    public static void Frame(NObj v, long frame)
    {
        var s = _cur;
        if (s == null || s.V != v || s.Failed || frame < 0 || frame == s.Shown) return;
        int slot = (s.Slot + 1) % Ring;
        int r = HostDecode(1, (int)Math.Min(frame, int.MaxValue), s.Data[slot], s.W);
        if (r < 0) { s.Failed = true; Log.Warn("[k8] video '" + s.Clip + "': the decoder stopped at frame " + frame.ToString() + " (" + r.ToString() + "): the rest is drawn black (kit #7)"); return; }
        s.Slot = slot; s.Shown = frame;
    }
    /// <summary>The VideoPlayer stopped, ended or was destroyed (null: any).</summary>
    public static void Stop(NObj v) { if (_cur != null && (v == null || _cur.V == v)) Close(); }
    static void Close()
    {
        var s = _cur; _cur = null;
        if (s == null) return;
        HostClose(1);
        var g = SceneRenderer.G;
        for (int k = 0; k < Ring; k++) if (s.Tex[k] != IntPtr.Zero) g?.DestroyTexture(s.Tex[k]);
        if (s.Draws > 0) Log.Info("[k8] video '" + s.Clip + "': drawn in " + s.Draws.ToString() + " frames (last clip frame " + s.Shown.ToString() + ")");
    }
    /// <summary>SceneRenderer.Frame, after the world pass and before the overlay canvases: the current picture, where Unity draws a
    /// CameraNearPlane video -- when its VideoPlayer is enabled on an active object and its target camera rendered this frame.</summary>
    /// <summary>Is there a picture to draw this frame (the frame's draw calls change only then)?</summary>
    internal static bool Visible(List<NBehaviour> screenCams)
    {
        var s = _cur;
        if (s == null || s.Failed || s.Slot < 0) return false;
        var v = s.V as NBehaviour;
        if (v == null || v.Destroyed || !v.Enabled || v.Go == null || !v.Go.ActiveInHierarchy) return false;
        var cam = TargetCamera(v, screenCams);
        return cam != null && screenCams.Contains(cam);
    }
    internal static void Draw(IRenderBackend g, List<NBehaviour> screenCams)
    {
        var s = _cur;
        if (g == null || !Visible(screenCams)) return;
        var v = (NBehaviour)s.V;
        var cam = TargetCamera(v, screenCams);
        var rect = CameraIcalls.PixelRectF(cam);   // (X, Y from the bottom, W, H: Unity's pixelRect)
        float alpha = v.Get("targetCameraAlpha") is { } ta ? Convert.ToSingle(ta) : 1f;
        // Fit Inside (VideoAspectRatio 2, the prefab's): the picture's aspect inside the camera rect, centred
        float pa = (float)s.CW / s.CH, ra = rect.W / rect.H, dw, dh;
        if (ra > pa) { dh = rect.H; dw = dh * pa; } else { dw = rect.W; dh = dw / pa; }
        float x0 = rect.X + (rect.W - dw) * 0.5f, x1 = x0 + dw;
        float yTop = g.H - (rect.Y + rect.H), y0 = yTop + (rect.H - dh) * 0.5f, y1 = y0 + dh;   // screen y down
        float u0 = (float)s.CX / s.W, u1 = (float)(s.CX + s.CW) / s.W, v0 = (float)s.CY / s.H, v1 = (float)(s.CY + s.CH) / s.H;
        var vx = _vx;   // (the frame's quad: reused, nothing allocated per frame)
        V(ref vx[0], x0, y0, u0, v0, alpha); V(ref vx[1], x1, y0, u1, v0, alpha); V(ref vx[2], x1, y1, u1, v1, alpha); V(ref vx[3], x0, y1, u0, v1, alpha);
        g.Draw(s.Tex[s.Slot], alpha >= 1f ? BLENDMODE_NONE : BLENDMODE_BLEND, false, vx, _idx);
        s.Draws++; _drawn++;
    }
    static readonly Vertex[] _vx = new Vertex[4];
    static readonly int[] _idx = { 0, 1, 2, 0, 2, 3 };
    static void V(ref Vertex v, float x, float y, float u, float t, float a)
    {
        v.position.x = x; v.position.y = y; v.color.r = 1; v.color.g = 1; v.color.b = 1; v.color.a = a; v.tex_coord.x = u; v.tex_coord.y = t;
    }
    /// <summary>The VideoPlayer's target camera as Unity sees it. A prefab instance (the MMVideoPlayer prefab is instantiated from Resources)
    /// keeps its serialized m_TargetCamera as the loader's lazy reference to the PREFAB's camera (Runtime/Cloner.cs remaps only resolved
    /// objects); Unity's Instantiate points such a reference inside the prefab at the clone's own object -- so a camera that is not in a
    /// scene is mapped here the way Unity's would be: the same path from the hierarchy's root in the clone (memoized per VideoPlayer).</summary>
    static NBehaviour TargetCamera(NBehaviour v, List<NBehaviour> screenCams)
    {
        if (_camFor == v && _camMemo != null && !_camMemo.Destroyed && screenCams.Contains(_camMemo)) return _camMemo;
        var c = v.Get("targetCamera");
        var cam = c as Camera ?? (Conv.ResolveRef?.Invoke(c, typeof(Camera)) as Camera);
        var n = cam != null ? Objects.Get<NBehaviour>(cam) : null;
        if (n != null && !screenCams.Contains(n) && n.Go != null && v.Go != null && CloneCounterpart(n, v.Go) is { } cp && screenCams.Contains(cp)) n = cp;   // (the clone's own camera)
        _camFor = v; _camMemo = n;
        return n;
    }
    static NBehaviour _camFor, _camMemo;
    static NBehaviour CloneCounterpart(NBehaviour assetCam, NGameObject inClone)
    {
        var path = new int[64]; int depth = 0;   // child indices from the asset camera's root down to it (reversed)
        for (var t = assetCam.Go.Transform; t.Parent != null && depth < path.Length; t = t.Parent) path[depth++] = t.Parent.Children.IndexOf(t);
        var root = inClone.Transform; while (root.Parent != null) root = root.Parent;
        var at = root;
        for (int d = depth - 1; d >= 0; d--) { int i = path[d]; if (i < 0 || i >= at.Children.Count) return null; at = at.Children[i]; }
        int k = assetCam.Go.Components.IndexOf(assetCam);
        return k >= 0 && k < at.Go.Components.Count ? at.Go.Components[k] as NBehaviour : null;
    }
    /// <summary>The track's end-of-run line.</summary>
    public static string Summary() => On ? "[k8] real video ON (clips: " + Clips + "): " + _opened.ToString() + " clip(s) opened, " + _drawn.ToString() + " frame(s) drawn" : "[k8] real video off (SHEPHERD_K8_VIDEO=1 turns it on)";

    [MethodImpl(MethodImplOptions.NoInlining)] static int HostApi() => shepherd_vita_video_api();
    [MethodImpl(MethodImplOptions.NoInlining)] static int HostOpen(string path, int* info, int n) => shepherd_vita_video_open(path, info, n);
    [MethodImpl(MethodImplOptions.NoInlining)] static int HostDecode(int id, int frame, IntPtr dst, int pitch) => shepherd_vita_video_decode(id, frame, dst, pitch);
    [MethodImpl(MethodImplOptions.NoInlining)] static void HostClose(int id) => shepherd_vita_video_close(id);
    [DllImport("__Internal")] static extern int shepherd_vita_video_api();
    [DllImport("__Internal")] static extern int shepherd_vita_video_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int* info, int n);
    [DllImport("__Internal")] static extern int shepherd_vita_video_decode(int id, int frame, IntPtr dst, int pitch);
    [DllImport("__Internal")] static extern void shepherd_vita_video_close(int id);
}
#endif   // (SHEPHERD_NO_K8, vita/shepherd/build.sh SHEPHERD_K8_ENGINE=0: no kit #8 engine code -- every use is under the same #if)
