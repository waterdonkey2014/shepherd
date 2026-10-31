using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Impl;

public static class CameraIcalls
{
    static NBehaviour C(object o) => Objects.Get<NBehaviour>(o);
    public static IEnumerable<NBehaviour> AllCameras() =>
        Registry.Cameras.Items().OfType<NBehaviour>().Where(b => !b.Destroyed && b.Managed is Camera && b.Enabled && b.Go != null && b.Go.ActiveInHierarchy && b.Go.Scene != 0)
            .OrderBy(b => Conv.To(b.Get("depth"), typeof(float)) is float d ? d : 0f);
    /// <summary>menu-speed (top hotspots): AllCameras().ToList() into a reused list (SceneRenderer.Frame, every frame): the same filter in the
    /// registry's order, the same depth keys, a stable insertion sort under float.CompareTo (OrderBy's order); more than a few cameras: AllCameras.</summary>
    public static List<NBehaviour> AllCamerasInto(List<NBehaviour> into)
    {
        into.Clear(); _depths.Clear();
        foreach (var n in Registry.Cameras.Items())
        {
            if (n is not NBehaviour b || b.Destroyed || b.Managed is not Camera || !b.Enabled || b.Go == null || !b.Go.ActiveInHierarchy || b.Go.Scene == 0) continue;
            into.Add(b); _depths.Add(Conv.To(b.Get("depth"), typeof(float)) is float d ? d : 0f);
        }
        if (into.Count > 16) { into.Clear(); into.AddRange(AllCameras()); return into; }
        for (int i = 1; i < into.Count; i++)
        {
            float k = _depths[i]; var x = into[i]; int j = i - 1;
            while (j >= 0 && _depths[j].CompareTo(k) > 0) { _depths[j + 1] = _depths[j]; into[j + 1] = into[j]; j--; }
            _depths[j + 1] = k; into[j + 1] = x;
        }
        return into;
    }
    static readonly List<float> _depths = new();
    public static float F(NObj n, string k, float d) => n.Get(k) is { } v ? (float)Conv.To(v, typeof(float)) : d;
    public static bool B(NObj n, string k, bool d) => n.Get(k) is { } v ? (bool)Conv.To(v, typeof(bool)) : d;
    public static Rect ViewRect(NObj c) => Um.ToRect(ViewRectF(c));
    /// <summary>menu-speed hop fix: the viewport rect's fields (the "rect" prop, else new Rect(0, 0, 1, 1)), read without Unity's getters.</summary>
    internal static RectF ViewRectF(NObj c)
    {
        if (c.Get("rect") is Rect r) return Um.Of(r);
        var d = new RectF(0f, 0f, 1f, 1f);
        if (HopCheck.On) HopCheck.R(HopCheck.ViewRect, d, new Rect(0, 0, 1, 1));
        return d;
    }
    /// <summary>The camera's pixel rect: its viewport rect over its target texture's size (polish round 2; Unity's pixelRect follows the
    /// target), or over the screen.</summary>
    public static Rect PixelRect(NObj c) => Um.ToRect(PixelRectF(c));
    /// <summary><see cref="PixelRect"/>'s fields: new Rect(r.x * W, r.y * H, r.width * W, r.height * H) of the viewport rect r, as our own
    /// field arithmetic (menu-speed hop fix: no Rect getters / constructor).</summary>
    internal static RectF PixelRectF(NObj c)
    {
        var r = ViewRectF(c); float W = AppIcalls.ScreenW, H = AppIcalls.ScreenH;
        if (TargetOf(c) is { } rt) { W = Math.Max(1, rt.Width); H = Math.Max(1, rt.Height); }
        var p = new RectF(r.X * W, r.Y * H, r.W * W, r.H * H);
        if (HopCheck.On) { var u = c.Get("rect") is Rect ur ? ur : new Rect(0, 0, 1, 1); HopCheck.R(HopCheck.PixelRect, p, new Rect(u.x * W, u.y * H, u.width * W, u.height * H)); }
        return p;
    }
    /// <summary>Polish round 2: the camera's targetTexture (the serialized m_TargetTexture or what a script assigned; an asset reference is
    /// resolved here), or null when it renders to the screen.</summary>
    public static NTexture TargetOf(NObj c)
    {
        var v = c?.Get("targetTexture"); if (v == null) return null;
        var tex = v as Texture;
        if (tex is null && Conv.To(v, typeof(RenderTexture)) is RenderTexture r) { tex = r; c.Set("targetTexture", r); }   // an asset reference, resolved once
        var t = Objects.Get<NTexture>(tex);
        return t is { IsRenderTexture: true, Destroyed: false } ? t : null;
    }
    public static float Aspect(NObj c) { if (c.Get("__aspect") is float a) return a; var p = PixelRectF(c); return p.H > 0 ? p.W / p.H : 16f / 9f; }
    public static Matrix4x4 View(NComponent c) { var m = c.Go.Transform.WorldToLocal; m.m20 = -m.m20; m.m21 = -m.m21; m.m22 = -m.m22; m.m23 = -m.m23; return m; }   // row 2 negated (fields, not the indexer)
    public static Matrix4x4 Proj(NObj c)
    {
        if (c.Get("__proj") is Matrix4x4 custom) return custom;
        float n = F(c, "nearClipPlane", 0.3f), f = F(c, "farClipPlane", 1000f), asp = Aspect(c);
        if (B(c, "orthographic", false)) { float s = F(c, "orthographicSize", 5f); return MathIcalls.Ortho(-s * asp, s * asp, -s, s, n, f); }
        return MathIcalls.Perspective(F(c, "fieldOfView", 60f), asp, n, f);
    }
    public static Vector3 WorldToScreen(NComponent c, Vector3 w)
    {
        var v = M.Point(View(c), w); var p = Proj(c);
        float cx = p.m00 * v.x + p.m01 * v.y + p.m02 * v.z + p.m03, cy = p.m10 * v.x + p.m11 * v.y + p.m12 * v.z + p.m13, cw = p.m30 * v.x + p.m31 * v.y + p.m32 * v.z + p.m33;
        if (MathF.Abs(cw) < 1e-8f) cw = 1e-8f;
        var pr = PixelRectF(c); return new Vector3(pr.X + (cx / cw * 0.5f + 0.5f) * pr.W, pr.Y + (cy / cw * 0.5f + 0.5f) * pr.H, -v.z);
    }
    public static Vector3 ScreenToWorld(NComponent c, Vector3 s)
    {
        var pr = PixelRectF(c); float nx = (s.x - pr.X) / pr.W * 2f - 1f, ny = (s.y - pr.Y) / pr.H * 2f - 1f;
        var inv = M.Inverse(M.Mul(Proj(c), View(c)));
        // find the clip-space point at eye depth s.z: solve along the ray through near/far
        Vector3 Unproj(float z) { var q = new Vector4(nx, ny, z, 1); var r = inv * q; return new Vector3(r.x / r.w, r.y / r.w, r.z / r.w); }
        var a = Unproj(-1f); var b = Unproj(1f); var camPos = c.Go.Transform.Position; var fwd = M.Rotate(c.Go.Transform.Rotation, Vector3.forward);
        float da = Vector3.Dot(a - camPos, fwd), db = Vector3.Dot(b - camPos, fwd); float t = Math.Abs(db - da) < 1e-6f ? 0 : (s.z - da) / (db - da);
        return Vector3.LerpUnclamped(a, b, t);
    }
    static NComponent Cam(object o) => Objects.Get<NComponent>(o);
    [Icall("UnityEngine.Camera::get_main")] static object Main(object[] a) => K8Speed.Lists ? Lookups.MainCamera() : AllCameras().FirstOrDefault(c => c.Go.Tag == "MainCamera")?.Managed;   // kit #8 speed: the same camera without the LINQ query (Runtime/K8Speed.cs Lookups)
    [Icall("UnityEngine.Camera::get_current")] static object Current(object[] a) => AllCameras().FirstOrDefault()?.Managed;
    [Icall("UnityEngine.Camera::GetAllCamerasCount")] static object Count(object[] a) => AllCameras().Count();
    [Icall("UnityEngine.Camera::GetAllCamerasImpl")] static object All(object[] a) { var arr = (Camera[])a[0]; int i = 0; foreach (var c in AllCameras()) { if (i >= arr.Length) break; arr[i++] = (Camera)c.Managed; } return i; }
    [Icall("UnityEngine.Camera::get_aspect")] static object GetAspect(object[] a) { float v = Aspect(C(a[0])); return Runtime.K9Memo.Float(AspectBox, v); }   // (kit #9 memo: one box per value)
    static readonly Runtime.BoxCache AspectBox = new();
    [Icall("UnityEngine.Camera::set_aspect")] static object SetAspect(object[] a) { C(a[0])?.Set("__aspect", (float)a[1]); return null; }
    [Icall("UnityEngine.Camera::ResetAspect")] static object ResetAspect(object[] a) { C(a[0])?.RemoveProp("__aspect"); return null; }
    [Icall("UnityEngine.Camera::get_pixelWidth")] static object PW(object[] a) => PwBox.Of((int)PixelRectF(C(a[0])).W);   // (menu-speed: a box per changed value)
    [Icall("UnityEngine.Camera::get_pixelHeight")] static object PH(object[] a) => PhBox.Of((int)PixelRectF(C(a[0])).H);
    static readonly Runtime.BoxCache PwBox = new(), PhBox = new();
    [Icall("UnityEngine.Camera::get_scaledPixelWidth")] static object SPW(object[] a) => (int)PixelRectF(C(a[0])).W;
    [Icall("UnityEngine.Camera::get_scaledPixelHeight")] static object SPH(object[] a) => (int)PixelRectF(C(a[0])).H;
    [Icall("UnityEngine.Camera::get_pixelRect_Injected")] static object PRect(object[] a) { a[1] = PixelRect(C(a[0])); return null; }
    [Icall("UnityEngine.Camera::set_pixelRect_Injected")] static object SetPRect(object[] a) { var r = (Rect)a[1]; C(a[0])?.Set("rect", new Rect(r.x / AppIcalls.ScreenW, r.y / AppIcalls.ScreenH, r.width / AppIcalls.ScreenW, r.height / AppIcalls.ScreenH)); return null; }
    [Icall("UnityEngine.Camera::get_projectionMatrix_Injected")] static object GetProj(object[] a) { a[1] = Proj(C(a[0])); return null; }
    [Icall("UnityEngine.Camera::set_projectionMatrix_Injected")] static object SetProj(object[] a) { C(a[0])?.Set("__proj", (Matrix4x4)a[1]); return null; }
    [Icall("UnityEngine.Camera::ResetProjectionMatrix")] static object ResetProj(object[] a) { C(a[0])?.RemoveProp("__proj"); return null; }
    [Icall("UnityEngine.Camera::get_nonJitteredProjectionMatrix_Injected")] static object GetNJProj(object[] a) => GetProj(a);
    [Icall("UnityEngine.Camera::get_worldToCameraMatrix_Injected")] static object GetView(object[] a) { a[1] = View(Cam(a[0])); return null; }
    [Icall("UnityEngine.Camera::get_cameraToWorldMatrix_Injected")] static object GetCamToWorld(object[] a) { a[1] = M.Inverse(View(Cam(a[0]))); return null; }
    [Icall("UnityEngine.Camera::get_cullingMatrix_Injected")] static object GetCull(object[] a) { var c = Cam(a[0]); a[1] = M.Mul(Proj(c), View(c)); return null; }
    [Icall("UnityEngine.Camera::WorldToScreenPoint_Injected")] static object W2S(object[] a) { a[3] = WorldToScreen(Cam(a[0]), (Vector3)a[1]); return null; }
    [Icall("UnityEngine.Camera::WorldToViewportPoint_Injected")] static object W2V(object[] a) { var c = Cam(a[0]); var s = WorldToScreen(c, (Vector3)a[1]); var pr = PixelRectF(c); a[3] = new Vector3((s.x - pr.X) / pr.W, (s.y - pr.Y) / pr.H, s.z); return null; }
    [Icall("UnityEngine.Camera::ScreenToWorldPoint_Injected")] static object S2W(object[] a) { a[3] = ScreenToWorld(Cam(a[0]), (Vector3)a[1]); return null; }
    [Icall("UnityEngine.Camera::ViewportToWorldPoint_Injected")] static object V2W(object[] a) { var c = Cam(a[0]); var v = (Vector3)a[1]; var pr = PixelRectF(c); a[3] = ScreenToWorld(c, new Vector3(pr.X + v.x * pr.W, pr.Y + v.y * pr.H, v.z)); return null; }
    [Icall("UnityEngine.Camera::ScreenToViewportPoint_Injected")] static object S2V(object[] a) { var pr = PixelRectF(C(a[0])); var s = (Vector3)a[1]; a[2] = new Vector3((s.x - pr.X) / pr.W, (s.y - pr.Y) / pr.H, s.z); return null; }
    [Icall("UnityEngine.Camera::ViewportToScreenPoint_Injected")] static object V2S(object[] a) { var pr = PixelRectF(C(a[0])); var v = (Vector3)a[1]; a[2] = new Vector3(pr.X + v.x * pr.W, pr.Y + v.y * pr.H, v.z); return null; }
    [Icall("UnityEngine.Camera::ScreenPointToRay_Injected")] static object S2Ray(object[] a) { a[3] = ScreenPointToRay(Cam(a[0]), (Vector2)a[1]); return null; }
    /// <summary>Camera.ScreenPointToRay's answer (the icall's body; kit #5 touch: also the pointer's hit test, Icalls/VitaTouch.cs).</summary>
    public static Ray ScreenPointToRay(NComponent c, Vector2 s)
    {
        var o = ScreenToWorld(c, new Vector3(s.x, s.y, F(c, "nearClipPlane", 0.3f))); var far = ScreenToWorld(c, new Vector3(s.x, s.y, F(c, "nearClipPlane", 0.3f) + 1f));
        return new Ray(o, (far - o).normalized);
    }
    [Icall("UnityEngine.Camera::ViewportPointToRay_Injected")] static object V2Ray(object[] a) { var pr = PixelRectF(C(a[0])); var v = (Vector2)a[1]; a[1] = new Vector2(pr.X + v.x * pr.W, pr.Y + v.y * pr.H); return S2Ray(a); }
    [Icall("UnityEngine.Camera::get_stereoEnabled")] static object Stereo(object[] a) => false;
    [Icall("UnityEngine.Camera::get_targetTexture")] static object Target(object[] a) => TargetOf(C(a[0]))?.Managed;
    [Icall("UnityEngine.Camera::get_activeTexture")] static object ActiveTex(object[] a) => TargetOf(C(a[0]))?.Managed;
    [Icall("UnityEngine.Camera::get_actualRenderingPath")] static object Path(object[] a) => RenderingPath.Forward;
    [Icall("UnityEngine.Camera::get_allowHDR")] static object Hdr(object[] a) => false;
    [Icall("UnityEngine.Camera::get_allowMSAA")] static object Msaa(object[] a) => false;
    [Icall("UnityEngine.Camera::GetFilterMode")] static object Filter(object[] a) => 0;
    // Command buffers are not executed (the renderer draws without them). Kit #6 (b, SHEPHERD_K6_CMDBUF): the camera keeps the list of the
    // buffers added to it per event, as Unity's does, so GetCommandBuffers / commandBufferCount answer what was added and not removed
    // (StencilBufferRender.OnDestroy walks GetCommandBuffers: it was a missing icall returning null, an NRE in every intro unload).
    sealed class CbEntry { public int Evt; public UnityEngine.Rendering.CommandBuffer Cb; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, List<CbEntry>> _cbs = new();
    [Icall("UnityEngine.Camera::get_commandBufferCount")] static object Cbs(object[] a) => K6Engine.CmdBuf && a[0] != null && _cbs.TryGetValue(a[0], out var l) ? l.Count : 0;
    [Icall("UnityEngine.Camera::AddCommandBufferImpl")] static object AddCb(object[] a)
    {
        if (K6Engine.CmdBuf && a[0] != null && a[2] is UnityEngine.Rendering.CommandBuffer cb) _cbs.GetValue(a[0], _ => new List<CbEntry>()).Add(new CbEntry { Evt = Convert.ToInt32(a[1]), Cb = cb });
        return null;
    }
    /// <summary>Removes the entries of event <paramref name="evt"/> (all events when -1) whose buffer is <paramref name="cb"/> (any when null).</summary>
    static object RemoveCbs(object cam, int evt, object cb)
    {
        if (!K6Engine.CmdBuf || cam == null || !_cbs.TryGetValue(cam, out var l)) return null;
        for (int i = l.Count - 1; i >= 0; i--) if ((evt < 0 || l[i].Evt == evt) && (cb == null || ReferenceEquals(l[i].Cb, cb))) l.RemoveAt(i);
        return null;
    }
    [Icall("UnityEngine.Camera::RemoveCommandBufferImpl")] static object RemCb(object[] a) => a[2] == null ? null : RemoveCbs(a[0], Convert.ToInt32(a[1]), a[2]);
    [Icall("UnityEngine.Camera::RemoveCommandBuffers")] static object RemCbs(object[] a) => RemoveCbs(a[0], Convert.ToInt32(a[1]), null);
    [Icall("UnityEngine.Camera::RemoveAllCommandBuffers")] static object RemAllCbs(object[] a) => RemoveCbs(a[0], -1, null);
    /// <summary>Camera.GetCommandBuffers(evt): bound by K6Engine.Install only with SHEPHERD_K6_CMDBUF on (off: a missing icall, as in kit #5).</summary>
    public static object GetCbs(object[] a)
    {
        int e = Convert.ToInt32(a[1]), n = 0;
        if (a[0] == null || !_cbs.TryGetValue(a[0], out var l)) return Array.Empty<UnityEngine.Rendering.CommandBuffer>();
        foreach (var x in l) if (x.Evt == e) n++;
        var r = new UnityEngine.Rendering.CommandBuffer[n]; n = 0;
        foreach (var x in l) if (x.Evt == e) r[n++] = x.Cb;
        return r;
    }
    [Icall("UnityEngine.Camera::Render")] static object RenderNow(object[] a) => null;
    [Icall("UnityEngine.Camera::GetFrustumPlaneSizeAt_Injected")] static object Frustum(object[] a)
    {
        var c = C(a[0]); float d = (float)a[1]; float asp = Aspect(c);
        float h = B(c, "orthographic", false) ? F(c, "orthographicSize", 5f) * 2f : 2f * d * MathF.Tan(F(c, "fieldOfView", 60f) * 0.5f * Mathf.Deg2Rad);
        a[2] = new Vector2(h * asp, h); return null;
    }
    [Icall("UnityEngine.Camera::SetupCurrent")] static object SetupCur(object[] a) => null;

    // kit #9 track runtime (lever 10; Icalls/K9Reg.cs, Runtime/K9Typed.cs): the typed twins of the hooks above -- the same expressions, no boxes
    internal static void K9(K9Reg r)
    {
        r.Add("UnityEngine.Camera::get_pixelWidth", (Func<object, int>)(s => (int)PixelRectF(C(s)).W), PW);
        r.Add("UnityEngine.Camera::get_pixelHeight", (Func<object, int>)(s => (int)PixelRectF(C(s)).H), PH);
    }
}
