using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>What the owner-local render code (<see cref="DerivedRender"/>, contract <see cref="DerivedRender.Api"/>) may use of the engine
/// besides its public types: SceneRenderer's draw path (its vertex / clip / index buffers, projection, near-far clipping, the batched
/// submit that also routes lit draws), texture handles, the shader globals, and typed reads of the property bags. Its draws therefore go
/// through exactly the engine's own path. The property-bag keys are read here, as literals of this (non-profile) class, so the V3 diet's
/// readable-key scan (Assets/Diet.cs BuildReadable, which looks only at Shepherd.Engine) keeps every key the owner-local code reads; that
/// code reads no bag key itself. Changing a member here changes the contract: bump <see cref="DerivedRender.Api"/>.</summary>
public static class RenderKit
{
    // ---- SceneRenderer's draw path ----
    /// <summary>The frame's backend (SceneRenderer.G).</summary>
    public static IRenderBackend Backend => SceneRenderer.G;
    /// <summary>The shared vertex buffer, at least n long (SceneRenderer's own: its contents are the current draw's).</summary>
    public static Vertex[] Verts(int n) => SceneRenderer.Verts(n);
    /// <summary>The shared clip-position buffer, at least n long (written by <see cref="Project"/>).</summary>
    public static Vector4[] ClipBuf(int n) => SceneRenderer.ClipBuf(n);
    /// <summary>The current vertex / clip-position / index buffers as they are now (ClipTriangles may have replaced them). (Integration with
    /// menu-speed: the buffers are the frame's own world emitter's, SceneRenderer.Emit0 -- the same shared buffers as before, and the ones
    /// every draw built on the main thread uses also with SHEPHERD_MT_RENDER=1; while ILitShading.Draw runs, Indices is that draw's index
    /// buffer.)</summary>
    public static Vertex[] Vertices => SceneRenderer.Emit0.VBuf;
    public static Vector4[] ClipPositions => SceneRenderer.Emit0.CBuf;
    public static int[] Indices => SceneRenderer.LitIdx ?? SceneRenderer.Emit0.IBuf;
    /// <summary>The shared index buffer, grown to at least n.</summary>
    public static int[] IndexBuffer(int n) { var e = SceneRenderer.Emit0; if (e.IBuf.Length < n) e.IBuf = new int[Math.Max(n, e.IBuf.Length * 2)]; return e.IBuf; }
    /// <summary>The world pass's projection of a local point (screen x/y in target pixels, clip position, outside the near/far planes).</summary>
    public static void Project(in Matrix4x4 lw, in Matrix4x4 vp, UnityEngine.Rect pr, float gh, float px, float py, float pz, out float sx, out float sy, ref Vector4 clip, ref bool outside)
    {   // (menu-speed: the engine's projection takes the rect's four fields as a RectF; the same floats)
        var r = Um.Of(pr);
        SceneRenderer.Project(in lw, in vp, in r, gh, px, py, pz, out sx, out sy, ref clip, ref outside);
    }
    /// <summary>Indices[0..ni) over Vertices/ClipPositions[0..nv) clipped against the near and far planes; returns the new index count.</summary>
    public static int ClipTriangles(int ni, ref int nv, UnityEngine.Rect pr, float gh) { var r = Um.Of(pr); return SceneRenderer.Emit0.ClipTriangles(ni, ref nv, in r, gh); }
    /// <summary>Draw v[0..nv) with Indices[0..ni) like any world draw (counters, render check, the lit path while a lit draw is open).</summary>
    public static void Submit(Vertex[] v, int nv, int ni, NTexture tex, uint blend) => SceneRenderer.SubmitFiltered(v, nv, ni, tex, blend, false);
    /// <summary>Set the frustum planes the culling tests use (a camera's view-projection).</summary>
    public static void FrustumPlanes(in Matrix4x4 vp) => SceneRenderer.FrustumPlanes(in vp);
    /// <summary>The mesh's culling box under lw lies outside a frustum plane (also refreshes the box, see <see cref="CullBox"/>).</summary>
    public static bool MeshOutside(NMesh m, in Matrix4x4 lw) => SceneRenderer.MeshOutside(m, in lw);
    /// <summary>The mesh's culling box as of its last <see cref="MeshOutside"/> test.</summary>
    public static void CullBox(NMesh m, out Vector3 min, out Vector3 max) { min = m.CullMin; max = m.CullMax; }
    public static NComponent FirstMeshFilter(NGameObject go) => SceneRenderer.FirstMeshFilter(go);
    /// <summary>Route draws to a render target (Zero = the frame's main target), keeping SceneRenderer's record of the bound target.</summary>
    public static void SetTarget(IntPtr target) => SceneRenderer.G.SetTarget(SceneRenderer._curTarget = target);
    /// <summary>The backend handle of a texture (uploaded on demand) / of a RenderTexture's target.</summary>
    public static IntPtr Handle(NTexture t) => SceneRenderer.Handle(t, false);
    public static IntPtr RtHandle(NTexture t) => SceneRenderer.RtHandle(t);
    public static bool LinearProject => SceneRenderer.LinearProject;
    /// <summary>Kit #6 gpulight (Api 4): the static-mesh program built from these shader files (the owner-local assembly's or the engine's),
    /// built on first use; 0 = this backend cannot (no IGpuMesh: SDL, compare mode), no source, or the driver refused it.</summary>
    public static int GpuProgram(string name, string vertexFile, string fragmentFile) => SceneRenderer.G is IGpuMesh gm ? gm.GpuProgram(name, vertexFile, fragmentFile) : 0;
    /// <summary>Kit #6 gpulight (Api 4): draw vertices [first, first + count) of a static mesh (IGpuMesh: the same immutable array every time)
    /// with the program and its vec4 uniforms, like any world draw (counters, stream hash). False: not drawn -- build it on the CPU.</summary>
    public static bool SubmitGpuMesh(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, NTexture tex, uint blend)
        => SceneRenderer.SubmitGpuMesh(program, data, stride, first, count, uniforms, tex, blend);
    /// <summary>Kit #6 gpulight (Api 4): the target draws go to now (Zero = the frame's main target): SceneRenderer's record (check runs).</summary>
    public static IntPtr CurrentTarget => SceneRenderer._curTarget;
    /// <summary>Kit #6 gpulight (Api 4): the array will not be drawn again (its GPU copy is freed).</summary>
    public static void ReleaseGpuMesh(float[] data) { if (SceneRenderer.G is IGpuMesh gm && data != null) gm.ReleaseGpuMesh(data); }

    // ---- textures and shader globals ----
    /// <summary>The texture record of a Texture (loaded on first touch).</summary>
    public static NTexture Tex(object texture) => RenderIcalls.Tex(texture);
    /// <summary>Top-down RGBA8 pixels; keep = pin them on the record (a CPU reader).</summary>
    public static byte[] Pixels(NTexture t, bool keep) => RenderIcalls.Pixels(t, keep);
    public static bool GlobalColor(int id, out Color c) => RenderIcalls.GlobalColor(id, out c);
    public static bool GlobalVector(int id, out Vector4 v) => RenderIcalls.GlobalVector(id, out v);
    public static float GlobalFloat(int id, float fallback) => RenderIcalls.GlobalFloat(id, fallback);
    /// <summary>RenderSettings.ambientLight.</summary>
    public static Color Ambient => RenderIcalls.Ambient;

    // ---- property-bag reads (the defaults are the components' when the key is absent) ----
    /// <summary>Light.type (default Directional = 1).</summary>
    public static int LightType(NBehaviour light) => light.Get("type") is { } t ? Convert.ToInt32(t) : 1;
    public static Color LightColor(NBehaviour light) => light.Get("color") is Color c ? c : Color.white;
    public static float LightIntensity(NBehaviour light) => light.Get("intensity") is float f ? f : 1f;
    /// <summary>Camera.allowHDR (true when unset).</summary>
    public static bool CameraAllowHdr(NBehaviour cam) => cam.Get("allowHDR") is not bool hdr || hdr;
    /// <summary>Camera.cullingMask, or <paramref name="fallback"/> when unset.</summary>
    public static int CullingMask(NBehaviour cam, int fallback) => cam.Get("cullingMask") is { } cm ? Convert.ToInt32(cm is LayerMask lm ? lm.value : cm) : fallback;
    /// <summary>Renderer.enabled (true when unset).</summary>
    public static bool RendererEnabled(NComponent r) => !(r.Get("enabled") is { } en && !(en is bool eb ? eb : (bool)Conv.To(en, typeof(bool))));
    /// <summary>A MeshFilter's sharedMesh (else mesh) record.</summary>
    public static NMesh SharedMesh(NComponent meshFilter) => Objects.Get<NMesh>(Conv.To(meshFilter.Get("sharedMesh") ?? meshFilter.Get("mesh"), typeof(Mesh)));
    /// <summary>A renderer's MaterialPropertyBlock values (shader property id -> value), or null.</summary>
    public static Dictionary<int, object> PropertyBlock(NComponent r) => r.Get("__mpb") as Dictionary<int, object>;
}
