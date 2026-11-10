using UnityEngine;
namespace Shepherd.Engine.Native;

public sealed class NMesh : NObj
{
    public Vector3[] Pos = Array.Empty<Vector3>(); public Color32[] Col; public Vector4[] Uv0; public Vector3[] Normals;
    /// <summary>Tangents, kept only when one carries data in z or w beyond uGUI's default (1,0,0,-1) (polish round 1: a few UI shaders read
    /// tangent.z as a per-vertex parameter, e.g. UI/Watercolor-Background's reveal offset); null otherwise, so the common UI mesh
    /// keeps no extra array.</summary>
    public Vector4[] Tan;
    public List<int[]> Sub = new() { Array.Empty<int>() }; public List<int> SubTopology = new() { 0 };
    public Bounds Bounds; public bool BoundsSet; public int Version;
    /// <summary>Polish round 2: SceneRenderer's frustum-culling box, the min/max of Pos as of Version CullVer (vertex data, not the
    /// game-settable Bounds: never culls something that has vertices on screen).</summary>
    internal int CullVer = int.MinValue; internal Vector3 CullMin, CullMax;
    public int VertexCount => Pos.Length;
    /// <summary>Bounds of Pos. menu-speed hop fix: was Vector3.Min / Max per vertex and new Bounds((mn + mx) * 0.5f, mx - mn), now our own
    /// arithmetic with the same results: Vector3.Min(a, b) = (Mathf.Min(a.x, b.x), ...) with Mathf.Min(a, b) = a &lt; b ? a : b (Max: a &gt; b ? a : b;
    /// a NaN operand gives b), from Pos[0] over every vertex including the first (as the foreach did); centre (mn + mx) * 0.5f, size mx - mn.</summary>
    public void Recalc()
    {
        if (Pos.Length == 0) { Bounds = default; return; }
        var ps = Pos; var p0 = ps[0];
        float nx = p0.x, ny = p0.y, nz = p0.z, xx = p0.x, xy = p0.y, xz = p0.z;
        bool chk = HopCheck.On;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            Vector3 um = default, ux = default; if (chk) { um = Vector3.Min(Um.V3(nx, ny, nz), p); ux = Vector3.Max(Um.V3(xx, xy, xz), p); }
            nx = Um.Min(nx, p.x); ny = Um.Min(ny, p.y); nz = Um.Min(nz, p.z);
            xx = Um.Max(xx, p.x); xy = Um.Max(xy, p.y); xz = Um.Max(xz, p.z);
            if (chk) { HopCheck.V3(HopCheck.RecalcMinMax, Um.V3(nx, ny, nz), um); HopCheck.V3(HopCheck.RecalcMinMax, Um.V3(xx, xy, xz), ux); }
        }
        var vmn = Um.V3(nx, ny, nz); var vmx = Um.V3(xx, xy, xz); var c = Um.Mid(vmn, vmx); var sz = Um.Sub(vmx, vmn);   // (mn + mx) * 0.5f, mx - mn
        Bounds = Um.ToBounds(c, sz);
        if (chk) { var mn = Um.V3(nx, ny, nz); var mx = Um.V3(xx, xy, xz); HopCheck.Bnd(HopCheck.RecalcBounds, Bounds, new Bounds((mn + mx) * 0.5f, mx - mn)); }
    }
}

public sealed class NTexture : NObj
{
    public int Width, Height, Format = 4; public byte[] Rgba;   // top-down RGBA8, straight alpha; with the V3 diet null while only the GPU copy exists
    public bool Loaded; public IntPtr Sdl; public int SdlVersion = -1, Version; public int FilterMode = 1, WrapMode;
    public bool IsRenderTexture;
    /// <summary>kit #4 (M1): one of the engine's solid colour textures (RenderIcalls.SolidTexture: Texture2D.whiteTexture & co.), and its alpha.</summary>
    internal bool Solid; internal byte SolidAlpha;
    /// <summary>Polish round 2 (survey item 4): a RenderTexture's backend render target (allocated on first use at RtW x RtH, re-made
    /// when the size changes, freed on Release/Destroy), the frame it was last sampled, and what is known of its content: RtZero =
    /// every texel is (0,0,0,0) (new, cleared to Color.clear, or blitted from a transparent-black texture); a clear requested by
    /// GL.Clear / Blit is kept pending (RtClearPending, RtClear) until the target is next used, so an RT nothing reads costs nothing.</summary>
    internal IntPtr RtTarget; internal int RtW, RtH, RtSampled = int.MinValue; internal bool RtZero = true, RtClearPending; internal Color RtClear;
    /// <summary>Asset textures: where the pristine pixels come from (re-decodable). Null once Rgba is the only copy
    /// (runtime textures, or after SetPixels/Reinitialize wrote into it).</summary>
    public Shepherd.Assets.TextureDecoder.TextureSource Src;
    public bool DecodeFailed; public int Decodes;
    /// <summary>SceneRenderer.Handle's upload cache (stage 2: was a dictionary keyed by (texture, sdf)): the backend handle and the
    /// Version it was uploaded at, for the plain and the SDF upload.</summary>
    internal IntPtr CacheH, CacheHSdf; internal int CacheV, CacheVSdf; internal bool Cached, CachedSdf;
}

public sealed class NShader : NObj
{
    public string ShaderName = "Hidden/Unknown"; public int RenderQueue = 3000;
    /// <summary>Polish round 2: the main pass's render state read from the shader asset (Render/ShaderPolicy.cs); null for a shader made at run time.</summary>
    public Shepherd.Engine.Render.ShaderPolicy.State State;
}

public sealed class NMaterial : NObj
{
    public NShader Shader; public readonly Dictionary<int, object> P = new(); public readonly HashSet<string> Keywords = new(); public int RenderQueue = -1;
    public string ShaderName => Shader?.ShaderName ?? "";
    /// <summary>menu-speed: what the UI pass read from this material during its pass number UiPass (no game code runs during a pass, so
    /// P is constant within it): _ColorMask == 0, _Color, _MainTex (read when first needed) and the shader info.</summary>
    internal int UiPass; internal bool UiCm0, UiHasColor, UiTexRead; internal Color UiColor; internal NTexture UiTex; internal object UiSh;
}

public sealed class NSprite : NObj
{
    public NTexture Texture; public Rect Rect, TextureRect; public Vector2 Pivot; public float PixelsPerUnit = 100f; public Vector4 Border;
    public Vector2 TextureRectOffset; public bool Packed; public int PackingRotation;
    public Vector2[] Vertices; public Vector2[] Uv; public ushort[] Triangles;   // sprite-local units (vertices) + texture uv (0..1, bottom-up like Unity)
    /// <summary>Polish round 2: SceneRenderer's frustum-culling box of <see cref="Vertices"/> (the array it was computed from: a new
    /// geometry array is a new reference).</summary>
    internal Vector2[] CullOf; internal Vector2 CullMin, CullMax;
    /// <summary>menu-speed, multicore stage: game code holds (or has held) a reference to the geometry arrays -- the getters return them
    /// as they are, and OverrideGeometry keeps the caller's triangle array -- so they may change in place. The engine itself only ever
    /// replaces them. SHEPHERD_MT_RENDER=1 copies such a sprite's arrays when it captures a draw; the others are captured by reference.</summary>
    internal bool GeomShared;
    /// <summary>The getters / OverrideGeometry hand game code the arrays: <see cref="GeomShared"/>. The first time, with SHEPHERD_MT_RENDER=1, wait for
    /// the render thread before game code gets them: it may still be building a frame that captured these arrays by reference (they were not
    /// shared then), and a write into them now would reach that build. Every later capture copies them.</summary>
    internal void Share() { if (GeomShared) return; GeomShared = true; Render.MtRender.Cur?.Sync(); }
    public void EnsureGeometry()
    {
        if (Vertices != null) return;
        // default quad covering Rect, pivot-relative, in units
        float w = Rect.width / PixelsPerUnit, h = Rect.height / PixelsPerUnit, px = Pivot.x * w, py = Pivot.y * h;
        Vertices = new[] { new Vector2(-px, -py), new Vector2(w - px, -py), new Vector2(w - px, h - py), new Vector2(-px, h - py) };
        var tr = TextureRect; float tw = Texture?.Width ?? 1, th = Texture?.Height ?? 1;
        Uv = new[] { new Vector2(tr.xMin / tw, tr.yMin / th), new Vector2(tr.xMax / tw, tr.yMin / th), new Vector2(tr.xMax / tw, tr.yMax / th), new Vector2(tr.xMin / tw, tr.yMax / th) };
        Triangles = new ushort[] { 0, 1, 2, 2, 3, 0 };
    }
    // (menu-speed hop fix: the texture rect's fields -- xMin = x, xMax = width + x, yMin = y, yMax = height + y -- and field stores, not
    // Rect's getters and Vector4's constructor; the same divisions)
    public Vector4 OuterUV
    {
        get
        {
            float tw = Texture?.Width ?? 1, th = Texture?.Height ?? 1; var r = Um.Of(TextureRect);
            var v = Um.V4(r.X / tw, r.Y / th, r.XMax / tw, r.YMax / th);
            if (HopCheck.On) { var u = TextureRect; HopCheck.V4(HopCheck.Vec, v, new Vector4(u.xMin / tw, u.yMin / th, u.xMax / tw, u.yMax / th)); }
            return v;
        }
    }
    public Vector4 InnerUV
    {
        get
        {
            float tw = Texture?.Width ?? 1, th = Texture?.Height ?? 1; var r = Um.Of(TextureRect);
            var v = Um.V4((r.X + Border.x) / tw, (r.Y + Border.y) / th, (r.XMax - Border.z) / tw, (r.YMax - Border.w) / th);
            if (HopCheck.On) { var u = TextureRect; HopCheck.V4(HopCheck.Vec, v, new Vector4((u.xMin + Border.x) / tw, (u.yMin + Border.y) / th, (u.xMax - Border.z) / tw, (u.yMax - Border.w) / th)); }
            return v;
        }
    }
}

/// <summary>CanvasRenderer native state: a snapshot of the mesh uGUI/TMP handed over, plus material/texture/clip.</summary>
public sealed class NCanvasRenderer : NComponent
{
    public Vector3[] Pos = Array.Empty<Vector3>(); public Color32[] Col; public Vector4[] Uv0; public int[] Idx = Array.Empty<int>(); public Vector4[] Tan;
    public Color Color = Color.white; public NMaterial[] Materials = new NMaterial[1]; public int MaterialCount = 1; public NTexture Texture; public NTexture AlphaTexture;
    // (kit #11 walk, the records diet: the three bools declared together so they share one word -- 144 bytes on the Vita, one of the play
    // apps' extra slot sizes (kit #10's SHEPHERD_K10_SIZES list); in the old order ClipRect sat between them: 152 bytes in a 160-byte slot)
#if SHEPHERD_K10_RECORDS
    public bool RectClipping; public Rect ClipRect; public bool Cull; public bool CullTransparent; public int AbsoluteDepth;   // (kit #10's order: build.sh SHEPHERD_K11_RECORDS=0)
#else
    public bool RectClipping; public bool Cull; public bool CullTransparent; public Rect ClipRect; public int AbsoluteDepth;
#endif
    /// <summary>menu-speed: the UI pass's cached vertex / index block of this renderer and the exact inputs it was built from
    /// (Render/SceneRenderer.UiCache.cs); null while the renderer is not drawn.</summary>
    internal Shepherd.Engine.Render.SceneRenderer.UiGeom UiGeom;
}
