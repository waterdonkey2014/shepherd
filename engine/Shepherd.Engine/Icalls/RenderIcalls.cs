using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

public static class ShaderIds
{
    static readonly Dictionary<string, int> ToId = new(); static readonly List<string> ToName = new() { "" };
    public static int Id(string n) { if (n == null) return 0; if (!ToId.TryGetValue(n, out var i)) { i = ToName.Count; ToName.Add(n); ToId[n] = i; } return i; }
    public static string Name(int id) => id > 0 && id < ToName.Count ? ToName[id] : null;
    public static readonly int MainTex = Id("_MainTex"), Color = Id("_Color");
}

static class RenderIcalls
{
    static T N<T>(object o) where T : NObj => Objects.Get<T>(o);
    /// <summary>Asset layer hook: decode a texture's pixels on first use.</summary>
    public static Func<NTexture, bool> LoadPixels;
    public static NShader ShaderNamed(string name)
    {
        // kit #9 engine (SHEPHERD_K9_FIND): the shader records only (Registry.Shaders: the live NShader records in Objects.All's order) -- the scan's first match
        if (K9Engine.Find && !K9Engine.FindCheck) { K9Engine.FindIndexed++; foreach (var n in Registry.Shaders.Items()) if (n is NShader s && s.ShaderName == name) return s; }
        else if (K9Engine.FindCheck)
        {
            // =check (review fix): the scan's first match (returned) against the shader records' first match
            NShader hit = null, idx = null;
            foreach (var n in Objects.All) if (n is NShader s && s.ShaderName == name) { hit = s; break; }
            foreach (var n in Registry.Shaders.Items()) if (n is NShader s && s.ShaderName == name) { idx = s; break; }
            K9Engine.CheckShader(name, hit, idx); K9Engine.FindScans++;
            if (hit != null) return hit;
        }
        else foreach (var n in Objects.All) if (n is NShader s && s.ShaderName == name) return s;
        var sh = (Shader)Objects.NewManaged(typeof(Shader)); return Objects.Bind(sh, new NShader { Name = name, ShaderName = name }, asset: true);
    }
    public static NMaterial NewMaterial(NShader shader, string name = "")
    {
        var m = (Material)Objects.NewManaged(typeof(Material)); return Objects.Bind(m, new NMaterial { Shader = shader, Name = name });
    }
    public static NTexture SolidTexture(string name, byte r, byte g, byte b, byte a)
    {
        foreach (var n in Objects.All) if (n is NTexture t && t.Name == name && t.Width == 4) return t;
        var tex = (Texture2D)Objects.NewManaged(typeof(Texture2D));
        var px = new byte[4 * 4 * 4]; for (int i = 0; i < 16; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
        return Objects.Bind(tex, new NTexture { Name = name, Width = 4, Height = 4, Rgba = px, Loaded = true, Solid = true, SolidAlpha = a }, asset: true);
    }

    // ---------------- Shader ----------------
    [Icall("UnityEngine.Shader::PropertyToID")] static object PropId(object[] a) => K9Memo.Int(ShaderIds.Id((string)a[0]));
    [Icall("UnityEngine.Shader::Find")] static object Find(object[] a) => ShaderNamed((string)a[0]).Managed;
    [Icall("UnityEngine.Shader::FindBuiltin")] static object FindB(object[] a) => ShaderNamed((string)a[0]).Managed;
    [Icall("UnityEngine.Shader::get_isSupported")] static object Supported(object[] a) => true;
    [Icall("UnityEngine.Shader::get_renderQueue")] static object SQueue(object[] a) => N<NShader>(a[0])?.RenderQueue ?? 3000;
    [Icall("UnityEngine.Shader::get_maximumLOD")] static object MaxLod(object[] a) => 1000;
    [Icall("UnityEngine.Shader::get_globalMaximumLOD")] static object GMaxLod(object[] a) => 1000;
    [Icall("UnityEngine.Shader::get_passCount")] static object Passes(object[] a) => 1;
    [Icall("UnityEngine.Shader::get_subshaderCount")] static object Subs(object[] a) => 1;
    [Icall("UnityEngine.Shader::get_globalRenderPipeline")] static object GRP(object[] a) => "";
    [Icall("UnityEngine.Shader::FindPropertyIndex")] static object PropIdx(object[] a) => -1;
    [Icall("UnityEngine.Shader::EnableKeyword")] static object GEnable(object[] a) => null;
    [Icall("UnityEngine.Shader::DisableKeyword")] static object GDisable(object[] a) => null;
    [Icall("UnityEngine.Shader::IsKeywordEnabled")] static object GKw(object[] a) => false;
    static readonly Dictionary<int, object> Globals = new();
    [Icall("UnityEngine.Shader::SetGlobalFloatImpl")] static object GSetF(object[] a) { Globals[(int)a[0]] = a[1]; return null; }
    [Icall("UnityEngine.Shader::SetGlobalVectorImpl_Injected")] static object GSetV(object[] a) { Globals[(int)a[0]] = a[1]; return null; }
    [Icall("UnityEngine.Shader::SetGlobalColorImpl_Injected")] static object GSetC(object[] a) { Globals[(int)a[0]] = a[1]; return null; }
    [Icall("UnityEngine.Shader::SetGlobalTextureImpl")] static object GSetT(object[] a) { Globals[(int)a[0]] = a[1]; return null; }
    [Icall("UnityEngine.Shader::SetGlobalMatrixImpl_Injected")] static object GSetM(object[] a) { Globals[(int)a[0]] = a[1]; return null; }
    [Icall("UnityEngine.Shader::SetGlobalIntImpl")] static object GSetI(object[] a) { Globals[(int)a[0]] = a[1]; return null; }
    /// <summary>Polish round 2: a global shader float (Shader.SetGlobalFloat) for the renderer's FX approximations; d when unset.</summary>
    internal static float GlobalFloat(int id, float d) => Globals.TryGetValue(id, out var v) && v is float f ? f : v is int i ? i : d;
    /// <summary>Fidelity round 1: a global colour / vector (SetGlobalColor stores a Color, SetGlobalVector a Vector4); false when unset.</summary>
    internal static bool GlobalColor(int id, out Color c)
    {
        if (Globals.TryGetValue(id, out var v)) { if (v is Color cc) { c = cc; return true; } if (v is Vector4 q) { c = new Color(q.x, q.y, q.z, q.w); return true; } }
        c = default; return false;
    }
    internal static bool GlobalVector(int id, out Vector4 q)
    {
        if (Globals.TryGetValue(id, out var v)) { if (v is Vector4 vv) { q = vv; return true; } if (v is Color c) { q = new Vector4(c.r, c.g, c.b, c.a); return true; } }
        q = default; return false;
    }
    [Icall("UnityEngine.Shader::GetGlobalFloatImpl")] static object GGetF(object[] a) => Globals.TryGetValue((int)a[0], out var v) ? Conv.To(v, typeof(float)) : 0f;
    [Icall("UnityEngine.Shader::GetGlobalTextureImpl")] static object GGetT(object[] a) => Globals.TryGetValue((int)a[0], out var v) ? v : null;

    // ---------------- RenderSettings (fidelity round 1) ----------------
    /// <summary>RenderSettings.ambientLight (= ambientSkyColor, Unity's alias): the flat ambient term of the game's lit shaders. Before, the
    /// icalls were missing (LightingManager sets it on every lighting transition). Starts at Unity's default sky ambient.</summary>
    internal static Color Ambient = new Color(0.212f, 0.227f, 0.259f, 1f);
    [Icall("UnityEngine.RenderSettings::get_ambientLight_Injected")] static object GetAmbient(object[] a) { a[0] = Ambient; return null; }
    [Icall("UnityEngine.RenderSettings::set_ambientLight_Injected")] static object SetAmbient(object[] a) { Ambient = (Color)a[0]; return null; }
    [Icall("UnityEngine.RenderSettings::get_ambientSkyColor_Injected")] static object GetSky(object[] a) { a[0] = Ambient; return null; }
    [Icall("UnityEngine.RenderSettings::set_ambientSkyColor_Injected")] static object SetSky(object[] a) { Ambient = (Color)a[0]; return null; }

    // ---------------- Material ----------------
    [Icall("UnityEngine.Material::CreateWithShader")] static object MatShader(object[] a) { Objects.Bind((UObject)a[0], new NMaterial { Shader = N<NShader>(a[1]) ?? ShaderNamed("Hidden/Unknown") }); return null; }
    [Icall("UnityEngine.Material::CreateWithMaterial")] static object MatCopy(object[] a)
    {
        var src = N<NMaterial>(a[1]); var m = Objects.Bind((UObject)a[0], new NMaterial { Shader = src?.Shader, Name = src?.Name ?? "", RenderQueue = src?.RenderQueue ?? -1 });
        if (src != null) { foreach (var kv in src.P) m.P[kv.Key] = kv.Value; foreach (var k in src.Keywords) m.Keywords.Add(k); }
        return null;
    }
    [Icall("UnityEngine.Material::CreateWithString")] static object MatStr(object[] a) { Objects.Bind((UObject)a[0], new NMaterial { Shader = ShaderNamed("Hidden/Unknown") }); return null; }
    [Icall("UnityEngine.Material::get_shader")] static object MShader(object[] a) => N<NMaterial>(a[0])?.Shader?.Managed;
    [Icall("UnityEngine.Material::set_shader")] static object MSetShader(object[] a) { var m = N<NMaterial>(a[0]); if (m != null) m.Shader = N<NShader>(a[1]); return null; }
    [Icall("UnityEngine.Material::get_renderQueue")] static object MQueue(object[] a) { var m = N<NMaterial>(a[0]); return m == null ? 3000 : m.RenderQueue >= 0 ? m.RenderQueue : m.Shader?.RenderQueue ?? 3000; }
    [Icall("UnityEngine.Material::get_rawRenderQueue")] static object MRawQueue(object[] a) => N<NMaterial>(a[0])?.RenderQueue ?? -1;
    [Icall("UnityEngine.Material::set_renderQueue")] static object MSetQueue(object[] a) { var m = N<NMaterial>(a[0]); if (m != null) m.RenderQueue = (int)a[1]; return null; }
    static object Get(object self, int id) { var m = N<NMaterial>(self); return m != null && m.P.TryGetValue(id, out var v) ? v : null; }
    static object Set(object self, int id, object v) { var m = N<NMaterial>(self); if (m != null) m.P[id] = v; return null; }
    [Icall("UnityEngine.Material::GetTextureImpl")] static object MGetTex(object[] a) => Get(a[0], (int)a[1]) as Texture;
    [Icall("UnityEngine.Material::SetTextureImpl")] static object MSetTex(object[] a) => Set(a[0], (int)a[1], a[2]);
    [Icall("UnityEngine.Material::GetColorImpl_Injected")] static object MGetCol(object[] a) { var v = Get(a[0], (int)a[1]); a[2] = v is Color c ? c : v is Vector4 q ? (Color)q : Color.white; return null; }
    [Icall("UnityEngine.Material::SetColorImpl_Injected")] static object MSetCol(object[] a) => Set(a[0], (int)a[1], a[2]);
    [Icall("UnityEngine.Material::GetVectorImpl_Injected")] static object MGetVec(object[] a) { var v = Get(a[0], (int)a[1]); a[2] = v is Vector4 q ? q : v is Color c ? (Vector4)c : Vector4.zero; return null; }
    [Icall("UnityEngine.Material::SetVectorImpl_Injected")] static object MSetVec(object[] a) => Set(a[0], (int)a[1], a[2]);
    [Icall("UnityEngine.Material::GetFloatImpl")] static object MGetF(object[] a) => Conv.To(Get(a[0], (int)a[1]) ?? 0f, typeof(float));
    [Icall("UnityEngine.Material::SetFloatImpl")] static object MSetF(object[] a) => Set(a[0], (int)a[1], a[2]);
    [Icall("UnityEngine.Material::GetIntImpl")] static object MGetI(object[] a) => Conv.To(Get(a[0], (int)a[1]) ?? 0, typeof(int));
    [Icall("UnityEngine.Material::SetIntImpl")] static object MSetI(object[] a) => Set(a[0], (int)a[1], a[2]);
    [Icall("UnityEngine.Material::GetMatrixImpl_Injected")] static object MGetM(object[] a) { a[2] = Get(a[0], (int)a[1]) is Matrix4x4 m ? m : Matrix4x4.identity; return null; }
    [Icall("UnityEngine.Material::SetMatrixImpl_Injected")] static object MSetM(object[] a) => Set(a[0], (int)a[1], a[2]);
    [Icall("UnityEngine.Material::GetTextureScaleAndOffsetImpl_Injected")] static object MGetSO(object[] a) { a[2] = Get(a[0], -(int)a[1]) is Vector4 v ? v : new Vector4(1, 1, 0, 0); return null; }
    [Icall("UnityEngine.Material::SetTextureOffsetImpl_Injected")] static object MSetOff(object[] a) { var cur = Get(a[0], -(int)a[1]) is Vector4 v ? v : new Vector4(1, 1, 0, 0); var o = (Vector2)a[2]; return Set(a[0], -(int)a[1], new Vector4(cur.x, cur.y, o.x, o.y)); }
    [Icall("UnityEngine.Material::SetTextureScaleImpl_Injected")] static object MSetScale(object[] a) { var cur = Get(a[0], -(int)a[1]) is Vector4 v ? v : new Vector4(1, 1, 0, 0); var s = (Vector2)a[2]; return Set(a[0], -(int)a[1], new Vector4(s.x, s.y, cur.z, cur.w)); }
    [Icall("UnityEngine.Material::HasProperty")] static object MHas(object[] a) => true;   // most game code guards SetX with HasProperty; say yes
    // kit #9 E5 MIRROR (MHas above): while Shepherd.Hooks.Mirror.On the stub answers Mirror.MaterialHasProperty (true) itself (tools/Rewriter/TypedPass.cs) -- change this body and you must change that formula (Runtime/K9Typed.cs binds the mirror only while THIS method is the key's hook; SHEPHERD_K9_MIRROR=check compares them)
    [Icall("UnityEngine.Material::HasFloatImpl")] static object MHasF(object[] a) => true;
    [Icall("UnityEngine.Material::HasTextureImpl")] static object MHasT(object[] a) => true;
    [Icall("UnityEngine.Material::HasVectorImpl")] static object MHasV(object[] a) => true;
    [Icall("UnityEngine.Material::HasIntImpl")] static object MHasI(object[] a) => true;
    [Icall("UnityEngine.Material::EnableKeyword")] static object MKwOn(object[] a) { N<NMaterial>(a[0])?.Keywords.Add((string)a[1]); return null; }
    [Icall("UnityEngine.Material::DisableKeyword")] static object MKwOff(object[] a) { N<NMaterial>(a[0])?.Keywords.Remove((string)a[1]); return null; }
    [Icall("UnityEngine.Material::IsKeywordEnabled")] static object MKwQ(object[] a) => N<NMaterial>(a[0])?.Keywords.Contains((string)a[1]) ?? false;
    [Icall("UnityEngine.Material::GetShaderKeywords")] static object MKws(object[] a) => N<NMaterial>(a[0])?.Keywords.ToArray() ?? Array.Empty<string>();
    [Icall("UnityEngine.Material::SetShaderKeywords")] static object MSetKws(object[] a) { var m = N<NMaterial>(a[0]); if (m != null) { m.Keywords.Clear(); foreach (var k in (string[])a[1] ?? Array.Empty<string>()) m.Keywords.Add(k); } return null; }
    [Icall("UnityEngine.Material::CopyPropertiesFromMaterial")] static object MCopyProps(object[] a) { var d = N<NMaterial>(a[0]); var s = N<NMaterial>(a[1]); if (d != null && s != null) foreach (var kv in s.P) d.P[kv.Key] = kv.Value; return null; }
    [Icall("UnityEngine.Material::GetTagImpl")] static object MTag(object[] a) => (string)a[3];
    [Icall("UnityEngine.Material::get_passCount")] static object MPasses(object[] a) => 1;
    [Icall("UnityEngine.Material::SetOverrideTag")] static object MOverrideTag(object[] a) => null;
    [Icall("UnityEngine.Material::GetTexturePropertyNameIDs")] static object MTexIds(object[] a) => N<NMaterial>(a[0])?.P.Where(kv => kv.Value is Texture).Select(kv => kv.Key).ToArray() ?? Array.Empty<int>();
    [Icall("UnityEngine.Material::GetDefaultMaterial")] static object DefMat(object[] a) => NewMaterial(ShaderNamed("Sprites/Default"), "Default-Material").Managed;
    [Icall("UnityEngine.Material::GetDefaultParticleMaterial")] static object DefPMat(object[] a) => NewMaterial(ShaderNamed("Particles/Standard Unlit"), "Default-Particle").Managed;
    [Icall("UnityEngine.Material::GetDefaultLineMaterial")] static object DefLMat(object[] a) => NewMaterial(ShaderNamed("Sprites/Default"), "Default-Line").Managed;
    [Icall("UnityEngine.Material::GetFirstPropertyNameIdByAttribute")] static object FirstByAttr(object[] a) => (a[1] is ShaderPropertyFlags f ? (int)f : Convert.ToInt32(a[1])) switch { 4 => ShaderIds.MainTex, 8 => ShaderIds.Color, _ => -1 };
    [Icall("UnityEngine.Material::ComputeCRC")] static object MCrc(object[] a) => Objects.Get(a[0])?.InstanceId ?? 0;

    // ---------------- Texture / Texture2D ----------------
    public static NTexture Tex(object o) { var t = N<NTexture>(o); if (t != null && !t.Loaded) Touch(t); return t; }
    /// <summary>First touch of a texture. Original behaviour (and with the diet for anything it cannot defer: 0-sized,
    /// unsupported-format or source-less textures): decode now and keep the RGBA. Diet: only record what that decode would
    /// have changed (Version++); pixels are decoded when they are needed (<see cref="Pixels"/>).</summary>
    static void Touch(NTexture t)
    {
        t.Loaded = true;
        if (Shepherd.Engine.Assets.Diet.KeepRgba || t.Src == null || t.Width <= 0 || t.Height <= 0 || !Shepherd.Assets.TextureDecoder.IsSupported((Shepherd.Assets.TextureFormat)t.Format))
            LoadPixels?.Invoke(t);
        else t.Version++;
    }
    /// <summary>The texture's RGBA8 pixels (top-down), decoding a pristine asset texture whose copy was dropped. keep=true
    /// (CPU readers/writers) stores them in Rgba for good; keep=false (GPU upload) hands out a temporary buffer, unless
    /// the texture keeps being re-decoded (then it is pinned too). Null when there are none.</summary>
    public static byte[] Pixels(NTexture t, bool keep)
    {
        if (t == null) return null;
        if (!t.Loaded) Touch(t);
        if (Shepherd.Engine.Assets.PackRecorder.On && !Shepherd.Engine.Assets.PackRecorder.InUpload) Shepherd.Engine.Assets.PackRecorder.Texture(t, 2);
        if (t.Rgba != null || Shepherd.Engine.Assets.Diet.KeepRgba) return t.Rgba;   // SHEPHERD_KEEP_RGBA=1: the first-touch decode was the only one
        var px = Shepherd.Engine.Assets.Diet.DecodeTexture(t); if (px == null) return null;
        if (keep || t.Decodes > Shepherd.Engine.Assets.Diet.MaxRedecodes) { t.Rgba = px; Shepherd.Engine.Assets.Diet.TexPinned++; }
        return px;
    }
    /// <summary>Before CPU writes: make sure Rgba holds the current pixels; from now on it is the only copy.</summary>
    static NTexture Writable(NTexture t) { if (t != null) { if (t.Rgba == null) Pixels(t, keep: true); t.Src = null; } return t; }
    [Icall("UnityEngine.Texture::GetDataWidth")] static object TW(object[] a) => N<NTexture>(a[0])?.Width ?? 0;
    [Icall("UnityEngine.Texture::GetDataHeight")] static object TH(object[] a) => N<NTexture>(a[0])?.Height ?? 0;
    [Icall("UnityEngine.Texture::GetDimension")] static object TDim(object[] a) => TextureDimension.Tex2D;
    [Icall("UnityEngine.Texture::get_isReadable")] static object TReadable(object[] a) => true;
    [Icall("UnityEngine.Texture::get_texelSize_Injected")] static object TTexel(object[] a) { var t = N<NTexture>(a[0]); a[1] = t == null || t.Width == 0 ? Vector2.one : new Vector2(1f / t.Width, 1f / t.Height); return null; }
    [Icall("UnityEngine.Texture::get_filterMode")] static object TFilter(object[] a) => (FilterMode)(N<NTexture>(a[0])?.FilterMode ?? 1);
    [Icall("UnityEngine.Texture::set_filterMode")] static object TSetFilter(object[] a) { var t = N<NTexture>(a[0]); if (t != null) { t.FilterMode = Convert.ToInt32(a[1]); t.Version++; } return null; }
    [Icall("UnityEngine.Texture::GetNativeTexturePtr")] static object TNative(object[] a) => Objects.Get(a[0])?.Ptr ?? IntPtr.Zero;
    [Icall("UnityEngine.Texture::get_mipmapCount")] static object TMips(object[] a) => 1;
    [Icall("UnityEngine.Texture::IncrementUpdateCount")] static object TInc(object[] a) { var t = N<NTexture>(a[0]); if (t != null) t.Version++; return null; }
    [Icall("UnityEngine.Texture::get_updateCount")] static object TUpd(object[] a) => (uint)(N<NTexture>(a[0])?.Version ?? 0);
    [Icall("UnityEngine.Texture2D::Internal_CreateImpl")] static object T2Create(object[] a)
    {
        int w = (int)a[1], h = (int)a[2]; Objects.Bind((UObject)a[0], new NTexture { Width = w, Height = h, Rgba = new byte[Math.Max(1, w * h * 4)], Loaded = true, Name = (string)a[8] ?? "" });
        return true;
    }
    [Icall("UnityEngine.Texture2D::get_format")] static object T2Format(object[] a) => (TextureFormat)(N<NTexture>(a[0])?.Format ?? 4);
    [Icall("UnityEngine.Texture2D::get_isReadable")] static object T2Readable(object[] a) => true;
    [Icall("UnityEngine.Texture2D::get_whiteTexture")] static object White(object[] a) => SolidTexture("white", 255, 255, 255, 255).Managed;
    // polish round 2: Unity's built-in "black" texture is transparent black, RGBA (0,0,0,0) (ShaderLab's "black" default; EyeTrailBlit clears its
    // trail RTs by blitting it, and its preview RawImage must then show nothing). It was opaque black.
    [Icall("UnityEngine.Texture2D::get_blackTexture")] static object Black(object[] a) => SolidTexture("black", 0, 0, 0, 0).Managed;
    [Icall("UnityEngine.Texture2D::get_grayTexture")] static object Gray(object[] a) => SolidTexture("gray", 128, 128, 128, 255).Managed;
    [Icall("UnityEngine.Texture2D::get_linearGrayTexture")] static object LGray(object[] a) => SolidTexture("lineargray", 128, 128, 128, 255).Managed;
    [Icall("UnityEngine.Texture2D::get_normalTexture")] static object NormalT(object[] a) => SolidTexture("normal", 128, 128, 255, 255).Managed;
    [Icall("UnityEngine.Texture2D::get_redTexture")] static object Red(object[] a) => SolidTexture("red", 255, 0, 0, 255).Managed;
    [Icall("UnityEngine.Texture2D::ApplyImpl")] static object T2Apply(object[] a) { var t = N<NTexture>(a[0]); if (t != null) t.Version++; return null; }
    [Icall("UnityEngine.Texture2D::ReinitializeImpl")] static object T2Reinit(object[] a) { var t = N<NTexture>(a[0]); if (t != null) { t.Width = (int)a[1]; t.Height = (int)a[2]; t.Rgba = new byte[t.Width * t.Height * 4]; t.Src = null; t.Version++; } return true; }
    [Icall("UnityEngine.Texture2D::ReinitializeWithFormatImpl")] static object T2Reinit2(object[] a) => T2Reinit(a);
    [Icall("UnityEngine.Texture2D::ReinitializeWithTextureFormatImpl")] static object T2Reinit3(object[] a) => T2Reinit(a);
    static int RowOf(NTexture t, int y) => t.Height - 1 - y;   // Unity y is bottom-up; our buffer is top-down
    [Icall("UnityEngine.Texture2D::SetAllPixels32")] static object T2SetAll32(object[] a)
    {
        var t = N<NTexture>(a[0]); var c = (Color32[])a[1]; if (t == null || c == null) return null;
        Writable(t);
        for (int y = 0; y < t.Height; y++) for (int x = 0; x < t.Width; x++) { int s = y * t.Width + x; if (s >= c.Length) break; int d = (RowOf(t, y) * t.Width + x) * 4; t.Rgba[d] = c[s].r; t.Rgba[d + 1] = c[s].g; t.Rgba[d + 2] = c[s].b; t.Rgba[d + 3] = c[s].a; }
        return null;
    }
    [Icall("UnityEngine.Texture2D::SetPixelsImpl")] static object T2SetPixels(object[] a)
    {
        var t = N<NTexture>(a[0]); int x0 = (int)a[1], y0 = (int)a[2], w = (int)a[3], h = (int)a[4]; var c = (Color[])a[5]; if (t == null || c == null) return null;
        Writable(t);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int tx = x0 + x, ty = y0 + y; if (tx < 0 || ty < 0 || tx >= t.Width || ty >= t.Height) continue; var col = (Color32)c[y * w + x]; int d = (RowOf(t, ty) * t.Width + tx) * 4; t.Rgba[d] = col.r; t.Rgba[d + 1] = col.g; t.Rgba[d + 2] = col.b; t.Rgba[d + 3] = col.a; }
        return null;
    }
    [Icall("UnityEngine.Texture2D::SetPixelImpl_Injected")] static object T2SetPixel(object[] a)
    {
        var t = N<NTexture>(a[0]); int x = (int)a[3], y = (int)a[4]; if (t == null || x < 0 || y < 0 || x >= t.Width || y >= t.Height) return null;
        Writable(t);
        var col = (Color32)(Color)a[5]; int d = (RowOf(t, y) * t.Width + x) * 4; t.Rgba[d] = col.r; t.Rgba[d + 1] = col.g; t.Rgba[d + 2] = col.b; t.Rgba[d + 3] = col.a; return null;
    }
    [Icall("UnityEngine.Texture2D::GetPixels32")] static object T2Get32(object[] a)
    {
        var t = Tex(a[0]); if (Pixels(t, keep: true) == null) return Array.Empty<Color32>();
        var r = new Color32[t.Width * t.Height]; for (int y = 0; y < t.Height; y++) for (int x = 0; x < t.Width; x++) { int s = (RowOf(t, y) * t.Width + x) * 4; r[y * t.Width + x] = new Color32(t.Rgba[s], t.Rgba[s + 1], t.Rgba[s + 2], t.Rgba[s + 3]); }
        return r;
    }
    [Icall("UnityEngine.Texture2D::GetPixelImpl_Injected")] static object T2GetPixel(object[] a)
    {
        var t = Tex(a[0]); int x = (int)a[3], y = (int)a[4]; if (Pixels(t, keep: true) == null || t.Width == 0) { a[5] = Color.clear; return null; }
        x = Math.Clamp(x, 0, t.Width - 1); y = Math.Clamp(y, 0, t.Height - 1); int s = (RowOf(t, y) * t.Width + x) * 4;
        a[5] = (Color)new Color32(t.Rgba[s], t.Rgba[s + 1], t.Rgba[s + 2], t.Rgba[s + 3]); return null;
    }

    // ---------------- Sprite ----------------
    static NSprite S(object o) { var s = N<NSprite>(o); if (s?.Texture != null) Tex(s.Texture.Managed); return s; }
    [Icall("UnityEngine.Sprite::get_texture")] static object SpTex(object[] a) => S(a[0])?.Texture?.Managed;
    [Icall("UnityEngine.Sprite::get_rect_Injected")] static object SpRect(object[] a) { a[1] = S(a[0])?.Rect ?? default; return null; }
    [Icall("UnityEngine.Sprite::GetTextureRect_Injected")] static object SpTexRect(object[] a) { a[1] = S(a[0])?.TextureRect ?? default; return null; }
    [Icall("UnityEngine.Sprite::GetTextureRectOffset_Injected")] static object SpTexOff(object[] a) { a[1] = S(a[0])?.TextureRectOffset ?? default; return null; }
    [Icall("UnityEngine.Sprite::get_pivot_Injected")] static object SpPivot(object[] a)
    {   // (hop fix: the rect's fields and field stores; was new Vector2(s.Pivot.x * s.Rect.width, s.Pivot.y * s.Rect.height), Vector2.zero)
        var s = S(a[0]); if (s == null) { a[1] = Um.Zero2; return null; }
        var r = Um.Of(s.Rect); var v = Um.V2(s.Pivot.x * r.W, s.Pivot.y * r.H); a[1] = v;
        if (HopCheck.On) HopCheck.V2(HopCheck.Vec, v, new Vector2(s.Pivot.x * s.Rect.width, s.Pivot.y * s.Rect.height));
        return null;
    }
    [Icall("UnityEngine.Sprite::get_pixelsPerUnit")] static object SpPpu(object[] a) => S(a[0])?.PixelsPerUnit ?? 100f;
    [Icall("UnityEngine.Sprite::get_spriteAtlasTextureScale")] static object SpScale(object[] a) => 1f;
    [Icall("UnityEngine.Sprite::get_border_Injected")] static object SpBorder(object[] a) { a[1] = S(a[0])?.Border ?? default; return null; }
    [Icall("UnityEngine.Sprite::GetPadding_Injected")] static object SpPad(object[] a)
    {
        var s = S(a[0]); if (s == null) { a[1] = Vector4.zero; return null; }
        // (hop fix: the rects' fields and field stores; was new Vector4(o.x, o.y, s.Rect.width - s.TextureRect.width - o.x, ...))
        var o = s.TextureRectOffset; var r = Um.Of(s.Rect); var tr = Um.Of(s.TextureRect); var v = Um.V4(o.x, o.y, r.W - tr.W - o.x, r.H - tr.H - o.y); a[1] = v;
        if (HopCheck.On) HopCheck.V4(HopCheck.Vec, v, new Vector4(o.x, o.y, s.Rect.width - s.TextureRect.width - o.x, s.Rect.height - s.TextureRect.height - o.y));
        return null;
    }
    [Icall("UnityEngine.Sprite::GetOuterUVs_Injected")] static object SpOuter(object[] a) { a[1] = S(a[0])?.OuterUV ?? default; return null; }
    [Icall("UnityEngine.Sprite::GetInnerUVs_Injected")] static object SpInner(object[] a) { a[1] = S(a[0])?.InnerUV ?? default; return null; }
    [Icall("UnityEngine.Sprite::GetPacked")] static object SpPacked(object[] a) => (S(a[0])?.Packed ?? false) ? 1 : 0;
    [Icall("UnityEngine.Sprite::GetPackingRotation")] static object SpRot(object[] a) => S(a[0])?.PackingRotation ?? 0;
    [Icall("UnityEngine.Sprite::GetPackingMode")] static object SpMode(object[] a) => 0;
    // (menu-speed: GeomShared marks a sprite whose geometry arrays game code can now write in place: Render/MtRender.cs copies those)
    [Icall("UnityEngine.Sprite::get_vertices")] static object SpVerts(object[] a) { var s = S(a[0]); s?.EnsureGeometry(); s?.Share(); return s?.Vertices ?? Array.Empty<Vector2>(); }
    [Icall("UnityEngine.Sprite::get_uv")] static object SpUv(object[] a) { var s = S(a[0]); s?.EnsureGeometry(); s?.Share(); return s?.Uv ?? Array.Empty<Vector2>(); }
    [Icall("UnityEngine.Sprite::get_triangles")] static object SpTris(object[] a) { var s = S(a[0]); s?.EnsureGeometry(); s?.Share(); return s?.Triangles ?? Array.Empty<ushort>(); }
    [Icall("UnityEngine.Sprite::get_bounds_Injected")] static object SpBounds(object[] a)
    {
        var s = S(a[0]); if (s == null) { a[1] = default(Bounds); return null; }
        float w = s.Rect.width / s.PixelsPerUnit, h = s.Rect.height / s.PixelsPerUnit;
        a[1] = new Bounds(new Vector3((0.5f - s.Pivot.x) * w, (0.5f - s.Pivot.y) * h, 0), new Vector3(w, h, 0.2f)); return null;
    }
    [Icall("UnityEngine.Sprite::get_associatedAlphaSplitTexture")] static object SpAlpha(object[] a) => null;
    [Icall("UnityEngine.Sprite::GetSecondaryTextureCount")] static object SpSec(object[] a) => 0;
    [Icall("UnityEngine.Sprite::GetPhysicsShapeCount")] static object SpPhys(object[] a) => 0;
    [Icall("UnityEngine.Sprite::CreateSprite_Injected")] static object SpCreate(object[] a)
    {
        var tex = Tex(a[0]); var rect = (Rect)a[1]; var pivot = (Vector2)a[2];
        var sp = (Sprite)Objects.NewManaged(typeof(Sprite));
        Objects.Bind(sp, new NSprite { Texture = tex, Rect = rect, TextureRect = rect, Pivot = pivot, PixelsPerUnit = (float)a[3], Border = (Vector4)a[6] });
        return sp;
    }
    [Icall("UnityEngine.Sprite::CreateSpriteWithoutTextureScripting_Injected")] static object SpCreate2(object[] a)
    {
        var sp = (Sprite)Objects.NewManaged(typeof(Sprite)); var rect = (Rect)a[0];
        Objects.Bind(sp, new NSprite { Texture = Tex(a[3]), Rect = rect, TextureRect = rect, Pivot = (Vector2)a[1], PixelsPerUnit = (float)a[2] }); return sp;
    }
    [Icall("UnityEngine.Sprite::OverrideGeometry")] static object SpOverride(object[] a)
    {
        var s = N<NSprite>(a[0]); if (s == null) return null; var v = (Vector2[])a[1]; var t = (ushort[])a[2]; s.Share();   // (keeps the caller's t)
        // OverrideGeometry vertices are in pixel space relative to the sprite rect
        s.Vertices = v.Select(p => new Vector2((p.x - s.Pivot.x * s.Rect.width) / s.PixelsPerUnit, (p.y - s.Pivot.y * s.Rect.height) / s.PixelsPerUnit)).ToArray();
        float tw = s.Texture?.Width ?? 1, th = s.Texture?.Height ?? 1; s.Uv = v.Select(p => new Vector2((s.TextureRect.x + p.x) / tw, (s.TextureRect.y + p.y) / th)).ToArray(); s.Triangles = t; return null;
    }

    // ---------------- Mesh ----------------
    static NMesh Me(object o) => N<NMesh>(o);
    [Icall("UnityEngine.Mesh::Internal_Create")] static object MeshCreate(object[] a) { Objects.Bind((UObject)a[0], new NMesh()); return null; }
    // kit #9 fidelity (stand-in fix "mesh", kit7-tests.md finding 10; the oracle's d-asset-mesh 300 / 300): Unity's Clear() keeps the mesh's
    // bounds (kit #8: they fell back to the -- now empty -- vertices, a zero box); a mesh whose vertices are assigned while it has none gets
    // its bounds recalculated at once (Unity: "RecalculateBounds is automatically invoked if no vertices have been assigned"); a new mesh's
    // index format is UInt16 and reads back what was set (kit #8: always UInt32, set ignored). Off: kit #8.
    [Icall("UnityEngine.Mesh::ClearImpl")] static object MeshClear(object[] a)
    {
        var m = Me(a[0]);
        if (m != null)
        {
            if (K9Fidelity.FixMesh && !m.BoundsSet) { m.Recalc(); m.BoundsSet = true; }   // (the bounds as of now stay what bounds reads)
            m.Pos = Array.Empty<Vector3>(); m.Col = null; m.Uv0 = null; m.Normals = null; m.Tan = null; m.Sub = new() { Array.Empty<int>() }; m.SubTopology = new() { 0 }; m.Version++;
            if (Render.K13Looks.Sdf) Render.K13Looks.SetMeshUv1(m, null);   // kit #13 looks: the kept uv2 goes with the other channels (off: no call)
        }
        return null;
    }
    [Icall("UnityEngine.Mesh::get_vertexCount")] static object MeshVC(object[] a) => Me(a[0])?.VertexCount ?? 0;
    [Icall("UnityEngine.Mesh::get_subMeshCount")] static object MeshSubs(object[] a) => SubsBox.Of(Me(a[0])?.Sub.Count ?? 1);   // (menu-speed: a box per changed count)
    static readonly BoxCache SubsBox = new();
    [Icall("UnityEngine.Mesh::set_subMeshCount")] static object MeshSetSubs(object[] a)
    {
        var m = Me(a[0]); int n = Math.Max(1, (int)a[1]); if (m == null) return null;
        while (m.Sub.Count < n) { m.Sub.Add(Array.Empty<int>()); m.SubTopology.Add(0); }
        while (m.Sub.Count > n) { m.Sub.RemoveAt(m.Sub.Count - 1); m.SubTopology.RemoveAt(m.SubTopology.Count - 1); }
        return null;
    }
    [Icall("UnityEngine.Mesh::get_canAccess")] static object MeshAccess(object[] a) => Boxes.True;   // kit #9 E5 MIRROR: while Shepherd.Hooks.Mirror.On the stub answers Mirror.MeshCanAccess (true) itself (tools/Rewriter/TypedPass.cs) -- change this body and you must change that formula (Runtime/K9Typed.cs binds the mirror only while THIS method is the key's hook; SHEPHERD_K9_MIRROR=check compares them)
    [Icall("UnityEngine.Mesh::get_isReadable")] static object MeshReadable(object[] a) => true;
    [Icall("UnityEngine.Mesh::MarkDynamicImpl")] static object MeshDyn(object[] a) => null;
    [Icall("UnityEngine.Mesh::MarkModified")] static object MeshMod(object[] a) => null;
    [Icall("UnityEngine.Mesh::UploadMeshDataImpl")] static object MeshUpload(object[] a) => null;
    [Icall("UnityEngine.Mesh::RecalculateBoundsImpl")] static object MeshRB(object[] a) { Me(a[0])?.Recalc(); return null; }
    [Icall("UnityEngine.Mesh::RecalculateNormalsImpl")] static object MeshRN(object[] a) => null;
    [Icall("UnityEngine.Mesh::RecalculateTangentsImpl")] static object MeshRT(object[] a) => null;
    [Icall("UnityEngine.Mesh::OptimizeImpl")] static object MeshOpt(object[] a) => null;
    [Icall("UnityEngine.Mesh::get_bounds_Injected")] static object MeshBounds(object[] a) { var m = Me(a[0]); if (m != null && !m.BoundsSet) m.Recalc(); a[1] = m?.Bounds ?? default; return null; }
    [Icall("UnityEngine.Mesh::set_bounds_Injected")] static object MeshSetBounds(object[] a) { var m = Me(a[0]); if (m != null) { m.Bounds = (Bounds)a[1]; m.BoundsSet = true; } return null; }
    [Icall("UnityEngine.Mesh::get_indexFormat")] static object MeshIdxFmt(object[] a) => !K9Fidelity.FixMesh ? IndexFormat.UInt32 : Me(a[0])?.Get("indexFormat") is IndexFormat f ? f : IndexFormat.UInt16;
    [Icall("UnityEngine.Mesh::set_indexFormat")] static object MeshSetIdxFmt(object[] a) { if (K9Fidelity.FixMesh) Me(a[0])?.Set("indexFormat", Conv.To(a[1], typeof(IndexFormat))); return null; }
    [Icall("UnityEngine.Mesh::HasVertexAttribute")] static object MeshHasAttr(object[] a)
    {
        var m = Me(a[0]); if (m == null) return false;
        return (VertexAttribute)a[1] switch { VertexAttribute.Position => true, VertexAttribute.Color => m.Col != null, VertexAttribute.TexCoord0 => m.Uv0 != null, VertexAttribute.Normal => m.Normals != null, _ => false };
    }
    [Icall("UnityEngine.Mesh::GetVertexAttributeDimension")] static object MeshAttrDim(object[] a) => (VertexAttribute)a[1] switch { VertexAttribute.Position or VertexAttribute.Normal => 3, VertexAttribute.TexCoord0 => 2, _ => 4 };

    /// <summary>A channel array of exactly <paramref name="count"/> elements for the mesh to own: its current one when the length matches
    /// (V6 stage 2: the animated Spine meshes re-send every channel every frame; nothing keeps a mesh's channel arrays beyond the
    /// current frame — readers get clones, CanvasRenderer.SetMesh clones, DrawWorld reads them within its pass), else a new one.</summary>
    static T[] Owned<T>(T[] cur, int count) => cur != null && cur.Length == count && count > 0 ? cur : new T[count];
    static void CopyChannel(NMesh m, VertexAttribute ch, Array src, int start, int count)
    {
        if (src == null) { if (ch == VertexAttribute.Color) m.Col = null; else if (ch == VertexAttribute.TexCoord0) m.Uv0 = null; else if (ch == VertexAttribute.Tangent) m.Tan = null; else if (ch == VertexAttribute.TexCoord1 && Render.K13Looks.Sdf) Render.K13Looks.SetMeshUv1(m, null); return; }   // (kit #13 looks: uv2 cleared too; off: as before)
        count = Math.Max(0, Math.Min(count, src.Length - start));
        // typed copies where the old element loop (src.GetValue(i) boxed every element) gives the same values; start >= 0 keeps the
        // old loop's exception for a negative start
        switch (ch)
        {
            case VertexAttribute.Position:
            {
                bool wasEmpty = m.Pos.Length == 0;
                var r = Owned(m.Pos, count);
                if (src is Vector3[] v3s && start >= 0) Array.Copy(v3s, start, r, 0, count); else for (int i = 0; i < count; i++) r[i] = src.GetValue(start + i) is Vector3 v ? v : default;
                m.Pos = r;
                if (wasEmpty && m.BoundsSet && K9Fidelity.FixMesh) m.Recalc();   // kit #9 "mesh": vertices on an empty mesh recalculate its bounds (Unity)
                break;
            }
            case VertexAttribute.Normal:
            {
                var r = Owned(m.Normals, count);
                if (src is Vector3[] v3s && start >= 0) Array.Copy(v3s, start, r, 0, count); else for (int i = 0; i < count; i++) r[i] = src.GetValue(start + i) is Vector3 v ? v : default;
                m.Normals = r; break;
            }
            case VertexAttribute.Color:
            {
                var r = Owned(m.Col, count);
                if (src is Color32[] c32) Array.Copy(c32, start, r, 0, count);
                else if (src is Color[] cf) for (int i = 0; i < count; i++) r[i] = cf[start + i];
                else Array.Clear(r);   // (a fresh array was all zero)
                m.Col = r; break;
            }
            case VertexAttribute.TexCoord0:
            {
                var r = Owned(m.Uv0, count);
                // menu-speed hop fix: the elements by field stores (were new Vector4(v2.x, v2.y, 0, 0) / new Vector4(v3.x, v3.y, v3.z, 0): an
                // interpreted constructor call per vertex)
                bool chk = HopCheck.On;
                if (start >= 0 && src is Vector2[] a2) for (int i = 0; i < count; i++) { var v2 = a2[start + i]; r[i] = Um.V4(v2.x, v2.y, 0f, 0f); if (chk) HopCheck.V4(HopCheck.CopyV4, r[i], new Vector4(v2.x, v2.y, 0, 0)); }
                else if (start >= 0 && src is Vector3[] a3) for (int i = 0; i < count; i++) { var v3 = a3[start + i]; r[i] = Um.V4(v3.x, v3.y, v3.z, 0f); if (chk) HopCheck.V4(HopCheck.CopyV4, r[i], new Vector4(v3.x, v3.y, v3.z, 0)); }
                else if (start >= 0 && src is Vector4[] a4) Array.Copy(a4, start, r, 0, count);
                else for (int i = 0; i < count; i++) r[i] = src.GetValue(start + i) switch { Vector2 v2 => new Vector4(v2.x, v2.y, 0, 0), Vector3 v3 => new Vector4(v3.x, v3.y, v3.z, 0), Vector4 v4 => v4, _ => default };
                m.Uv0 = r; break;
            }
            case VertexAttribute.TexCoord1 when Render.K13Looks.Sdf:
            {   // kit #13 looks (SHEPHERD_K13_SDF=1): TMP's per-glyph scale rides in uv2 (x: the face / outline uv pack, y: the scale, < 0 = bold);
                // the mesh's own array is reused when the length matches (as Owned does for the other channels: readers get copies; written out
                // here, not Owned<Vector2>: no new generic instance over a value type in the eboot's AOT code)
                var cur1 = Render.K13Looks.MeshUv1(m); var r = cur1 != null && cur1.Length == count && count > 0 ? cur1 : new Vector2[count];
                if (start >= 0 && src is Vector2[] b2) Array.Copy(b2, start, r, 0, count);
                else for (int i = 0; i < count; i++) r[i] = src.GetValue(start + i) switch { Vector2 v2 => v2, Vector3 v3 => new Vector2(v3.x, v3.y), Vector4 v4 => new Vector2(v4.x, v4.y), _ => default };
                Render.K13Looks.SetMeshUv1(m, r); break;
            }
            case VertexAttribute.Tangent:
            {
                // kept only when some tangent differs in z/w from uGUI's default (1,0,0,-1) (see NMesh.Tan); start >= 0 as above
                bool data = false;
                if (src is Vector4[] t4 && start >= 0) { for (int i = 0; i < count && !data; i++) { var t = t4[start + i]; data = t.z != 0f || t.w != -1f; } }
                if (!data) { m.Tan = null; break; }
                var r = Owned(m.Tan, count); Array.Copy((Vector4[])src, start, r, 0, count);
                m.Tan = r; break;
            }
        }
        m.Version++;
    }
    [Icall("UnityEngine.Mesh::SetArrayForChannelImpl")] static object MeshSetCh(object[] a) { var m = Me(a[0]); if (m != null) CopyChannel(m, (VertexAttribute)a[1], (Array)a[4], (int)a[6], (int)a[7]); return null; }
    [Icall("UnityEngine.Mesh::SetNativeArrayForChannelImpl")] static unsafe object MeshSetChNative(object[] a)
    {
        var m = Me(a[0]); if (m == null) return null; var ch = (VertexAttribute)a[1]; var fmt = (VertexAttributeFormat)a[2]; int dim = (int)a[3]; var p = (IntPtr)a[4]; int start = (int)a[6], count = (int)a[7];
        Array arr = ch switch
        {
            VertexAttribute.Position or VertexAttribute.Normal => new Span<Vector3>((void*)p, start + count).ToArray(),
            VertexAttribute.Color when fmt == VertexAttributeFormat.UNorm8 => new Span<Color32>((void*)p, start + count).ToArray(),
            VertexAttribute.Color => new Span<Color>((void*)p, start + count).ToArray(),
            VertexAttribute.TexCoord0 when dim == 2 => new Span<Vector2>((void*)p, start + count).ToArray(),
            VertexAttribute.TexCoord0 when dim == 3 => new Span<Vector3>((void*)p, start + count).ToArray(),
            VertexAttribute.TexCoord0 => new Span<Vector4>((void*)p, start + count).ToArray(),
            VertexAttribute.Tangent when dim == 4 && fmt == VertexAttributeFormat.Float32 => new Span<Vector4>((void*)p, start + count).ToArray(),
            _ => null
        };
        if (arr != null) CopyChannel(m, ch, arr, start, count); return null;
    }
    [Icall("UnityEngine.Mesh::GetAllocArrayFromChannelImpl")] static object MeshGetCh(object[] a)
    {
        var m = Me(a[0]); if (m == null) return null; var ch = (VertexAttribute)a[1]; var fmt = (VertexAttributeFormat)a[2]; int dim = (int)a[3];
        return ch switch
        {
            VertexAttribute.Position => (Vector3[])m.Pos.Clone(),
            VertexAttribute.Normal => m.Normals?.Clone() ?? new Vector3[m.VertexCount],
            VertexAttribute.Color when fmt == VertexAttributeFormat.UNorm8 => m.Col?.Clone() ?? new Color32[0],
            VertexAttribute.Color => m.Col?.Select(c => (Color)c).ToArray() ?? new Color[0],
            VertexAttribute.TexCoord0 when dim == 2 => m.Uv0?.Select(v => new Vector2(v.x, v.y)).ToArray() ?? new Vector2[0],
            VertexAttribute.TexCoord0 when dim == 3 => m.Uv0?.Select(v => new Vector3(v.x, v.y, v.z)).ToArray() ?? new Vector3[0],
            VertexAttribute.TexCoord0 => m.Uv0?.Clone() ?? new Vector4[0],
            _ => dim == 2 ? new Vector2[0] : dim == 3 ? new Vector3[0] : new Vector4[0]
        };
    }
    [Icall("UnityEngine.Mesh::GetArrayFromChannelImpl")] static object MeshGetChInto(object[] a)
    {
        var src = (Array)MeshGetCh(a); var dst = (Array)a[4]; if (src != null && dst != null) Array.Copy(src, dst, Math.Min(src.Length, dst.Length)); return null;
    }
    static int[] ToInts(Array arr, int start, int count, int[] reuse = null)
    {
        count = Math.Max(0, Math.Min(count, arr.Length - start)); var r = arr is int[] || arr is ushort[] ? Owned(reuse, count) : new int[count];   // (reuse only where no element can fail half way)
        if (arr is int[] ia) Array.Copy(ia, start, r, 0, count); else if (arr is ushort[] us) for (int i = 0; i < count; i++) r[i] = us[start + i]; else for (int i = 0; i < count; i++) r[i] = Convert.ToInt32(arr.GetValue(start + i));
        return r;
    }
    // SetIndicesImpl(this, submesh, topology, indicesFormat, Array indices, arrayStart, arraySize, calculateBounds, baseVertex)
    [Icall("UnityEngine.Mesh::SetIndicesImpl")] static object MeshSetIdx(object[] a) => SetIdx(Me(a[0]), (int)a[1], a[2] is MeshTopology mt ? (int)mt : Convert.ToInt32(a[2]), a[4] as Array, (int)a[5], (int)a[6], (bool)a[7], (int)a[8]);
    static object SetIdx(NMesh m, int sub, int topo, Array arr, int start, int size, bool calc, int bv)
    {
        if (m == null) return null;
        if (sub < 0) { m.Sub.Clear(); m.SubTopology.Clear(); sub = 0; }   // -1 = "whole mesh as one submesh" (Mesh.triangles setter)
        while (m.Sub.Count <= sub) m.Sub.Add(Array.Empty<int>());
        while (m.SubTopology.Count < m.Sub.Count) m.SubTopology.Add(0);
        var idx = arr != null ? ToInts(arr, start, size, m.Sub[sub]) : Array.Empty<int>();   // (the submesh's own array when the length matches: see Owned)
        if (bv != 0) for (int i = 0; i < idx.Length; i++) idx[i] += bv;
        m.Sub[sub] = idx; m.SubTopology[sub] = topo; m.Version++; if (calc) m.Recalc(); return null;
    }
    [Icall("UnityEngine.Mesh::SetIndicesNativeArrayImpl")] static unsafe object MeshSetIdxNative(object[] a)
    {
        bool u16 = Convert.ToInt32(a[3]) == 0; var p = (IntPtr)a[4]; int start = (int)a[5], size = (int)a[6];
        Array arr = u16 ? new Span<ushort>((void*)p, start + size).ToArray() : new Span<int>((void*)p, start + size).ToArray();
        return SetIdx(Me(a[0]), (int)a[1], Convert.ToInt32(a[2]), arr, start, size, (bool)a[7], (int)a[8]);
    }
    [Icall("UnityEngine.Mesh::GetIndicesImpl")] static object MeshGetIdx(object[] a) { var m = Me(a[0]); int s = (int)a[1]; return m == null ? Array.Empty<int>() : s < 0 ? m.Sub.SelectMany(x => x).ToArray() : s < m.Sub.Count ? (int[])m.Sub[s].Clone() : Array.Empty<int>(); }
    [Icall("UnityEngine.Mesh::GetTrianglesImpl")] static object MeshGetTris(object[] a) => MeshGetIdx(a);
    [Icall("UnityEngine.Mesh::GetIndexCountImpl")] static object MeshIdxCount(object[] a) { var m = Me(a[0]); int s = (int)a[1]; return (uint)(m == null ? 0 : s < 0 ? m.Sub.Sum(x => x.Length) : s < m.Sub.Count ? m.Sub[s].Length : 0); }
    [Icall("UnityEngine.Mesh::GetTrianglesCountImpl")] static object MeshTriCount(object[] a) => MeshIdxCount(a);
    [Icall("UnityEngine.Mesh::GetIndexStartImpl")] static object MeshIdxStart(object[] a) => 0u;
    [Icall("UnityEngine.Mesh::GetBaseVertexImpl")] static object MeshBaseV(object[] a) => 0u;
    [Icall("UnityEngine.Mesh::GetTopologyImpl")] static object MeshTopo(object[] a) { var m = Me(a[0]); int s = (int)a[1]; return (MeshTopology)(m != null && s < m.SubTopology.Count ? m.SubTopology[s] : 0); }
    [Icall("UnityEngine.Mesh::GetTotalIndexCount")] static object MeshTotal(object[] a) => (uint)(Me(a[0])?.Sub.Sum(s => s.Length) ?? 0);

    // ---------------- NoAllocHelpers (List<T> internals) ----------------
    static readonly Dictionary<Type, System.Reflection.FieldInfo> ItemsField = new();
    static System.Reflection.FieldInfo Items(Type t) { if (!ItemsField.TryGetValue(t, out var f)) ItemsField[t] = f = t.GetField("_items", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); return f; }
    [Icall("UnityEngine.NoAllocHelpers::ExtractArrayFromList")] static object ExtractArr(object[] a) => a[0] == null ? null : Items(a[0].GetType())?.GetValue(a[0]);
    [Icall("UnityEngine.NoAllocHelpers::Internal_ResizeList")] static object ResizeList(object[] a)
    {
        var list = a[0]; int size = (int)a[1]; if (list == null) return null; var t = list.GetType();
        var arr = (Array)Items(t).GetValue(list); if (arr.Length < size) { var na = Array.CreateInstance(arr.GetType().GetElementType(), size); Array.Copy(arr, na, arr.Length); Items(t).SetValue(list, na); }
        t.GetField("_size", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(list, size);
        var ver = t.GetField("_version", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); if (ver != null) ver.SetValue(list, (int)ver.GetValue(list) + 1);
        return null;
    }
    [Icall("UnityEngine.UISystemProfilerApi::BeginSample")] static object UiBegin(object[] a) => null;   // kit #9 E5 MIRROR: while Shepherd.Hooks.Mirror.On the stub answers (an empty body) itself (tools/Rewriter/TypedPass.cs) -- change this body and you must change that formula (Runtime/K9Typed.cs binds the mirror only while THIS method is the key's hook; SHEPHERD_K9_MIRROR=check compares them)
    [Icall("UnityEngine.UISystemProfilerApi::EndSample")] static object UiEnd(object[] a) => null;   // kit #9 E5 MIRROR: while Shepherd.Hooks.Mirror.On the stub answers (an empty body) itself (tools/Rewriter/TypedPass.cs) -- change this body and you must change that formula (Runtime/K9Typed.cs binds the mirror only while THIS method is the key's hook; SHEPHERD_K9_MIRROR=check compares them)
    [Icall("UnityEngine.UISystemProfilerApi::AddMarker")] static object UiMarker(object[] a) => null;

    // ---------------- CanvasRenderer ----------------
    static NCanvasRenderer CR(object o) => N<NCanvasRenderer>(o);
    [Icall("UnityEngine.CanvasRenderer::SetMesh")] static object CRSetMesh(object[] a)
    {
        var c = CR(a[0]); var m = Me(a[1]); if (c == null) return null;
        if (m == null) { c.Pos = Array.Empty<Vector3>(); c.Idx = Array.Empty<int>(); return null; }
        if (Render.K12Ui.TextCache) { SetMeshKept(c, m); if (Render.K13Looks.Sdf) Render.K13Looks.CopyCrUv1(c, Render.K13Looks.MeshUv1(m)); return null; }   // kit #12 menusa (off: kit #11's clones below); kit #13 looks: + uv2
        c.Pos = (Vector3[])m.Pos.Clone(); c.Col = (Color32[])m.Col?.Clone(); c.Uv0 = (Vector4[])m.Uv0?.Clone(); c.Tan = (Vector4[])m.Tan?.Clone(); c.Idx = Flatten(m.Sub);
        if (Render.K13Looks.Sdf) Render.K13Looks.CopyCrUv1(c, Render.K13Looks.MeshUv1(m));   // kit #13 looks (off: no call)
        return null;
    }
    /// <summary>Kit #12 menusa, the text-mesh cache's engine half (SHEPHERD_K12_TEXTCACHE=1, Render/K12Ui.cs): SetMesh as above, except that a
    /// channel whose incoming bytes equal the renderer's current array keeps that array (the clone would hold the same bytes). The renderer's
    /// arrays are the engine's own: nothing writes them in place (SetMesh / Clear store whole arrays; the UI pass only reads them), so a kept
    /// array still holds exactly what it held. Same null / empty rules as the clones: a null channel stays null, an empty one is empty.</summary>
    static void SetMeshKept(NCanvasRenderer c, NMesh m)
    {
        Render.K12Ui.SetMeshCalls++;
        int kept = 0;
        if (Render.K12Ui.Same(m.Pos, c.Pos)) kept++; else { c.Pos = (Vector3[])m.Pos.Clone(); Render.K12Ui.ChCloned++; }
        if (m.Col == null) c.Col = null; else if (Render.K12Ui.Same(m.Col, c.Col)) kept++; else { c.Col = (Color32[])m.Col.Clone(); Render.K12Ui.ChCloned++; }
        if (m.Uv0 == null) c.Uv0 = null; else if (Render.K12Ui.Same(m.Uv0, c.Uv0)) kept++; else { c.Uv0 = (Vector4[])m.Uv0.Clone(); Render.K12Ui.ChCloned++; }
        if (m.Tan == null) c.Tan = null; else if (Render.K12Ui.Same(m.Tan, c.Tan)) kept++; else { c.Tan = (Vector4[])m.Tan.Clone(); Render.K12Ui.ChCloned++; }
        if (Render.K12Ui.SameFlat(c.Idx, m.Sub)) kept++; else { c.Idx = Flatten(m.Sub); Render.K12Ui.ChCloned++; }
        int want = 2 + (m.Col != null ? 1 : 0) + (m.Uv0 != null ? 1 : 0) + (m.Tan != null ? 1 : 0);
        Render.K12Ui.ChKept += kept;
        if (kept == want)
        {
            Render.K12Ui.SetMeshSame++;
            Render.K12Ui.BytesNotCloned += c.Pos.Length * 12L + (c.Col?.Length ?? 0) * 4L + (c.Uv0?.Length ?? 0) * 16L + (c.Tan?.Length ?? 0) * 16L + c.Idx.Length * 4L;
        }
    }
    /// <summary>m.Sub.SelectMany(s => s).ToArray() without the iterators (stage 2): the submeshes' indices in order.</summary>
    static int[] Flatten(List<int[]> subs)
    {
        if (subs.Count == 1) return subs[0].Length == 0 ? Array.Empty<int>() : (int[])subs[0].Clone();
        int n = 0; foreach (var s in subs) n += s.Length;
        if (n == 0) return Array.Empty<int>();
        var r = new int[n]; int o = 0; foreach (var s in subs) { Array.Copy(s, 0, r, o, s.Length); o += s.Length; }
        return r;
    }
    [Icall("UnityEngine.CanvasRenderer::Clear")] static object CRClear(object[] a) { var c = CR(a[0]); if (c != null) { c.Pos = Array.Empty<Vector3>(); c.Idx = Array.Empty<int>(); c.Texture = null; } return null; }
    [Icall("UnityEngine.CanvasRenderer::SetColor_Injected")] static object CRSetColor(object[] a) { var c = CR(a[0]); if (c != null) c.Color = (Color)a[1]; return null; }
    [Icall("UnityEngine.CanvasRenderer::GetColor_Injected")] static object CRGetColor(object[] a) { a[1] = CR(a[0])?.Color ?? Color.white; return null; }
    [Icall("UnityEngine.CanvasRenderer::SetMaterial")] static object CRSetMat(object[] a)
    {
        var c = CR(a[0]); if (c == null) return null; int i = (int)a[2]; if (c.Materials.Length <= i) Array.Resize(ref c.Materials, i + 1);
        c.Materials[i] = N<NMaterial>(a[1]); return null;
    }
    [Icall("UnityEngine.CanvasRenderer::GetMaterial")] static object CRGetMat(object[] a) { var c = CR(a[0]); int i = (int)a[1]; return c != null && i < c.Materials.Length ? c.Materials[i]?.Managed : null; }
    // kit #12 boot (Render/K12Stencil.cs, SHEPHERD_K12_STENCIL=1): the pop materials (a Mask's unmask material) are kept and drawn after the
    // Mask's children; with the switch off these stay the no-ops they were (kit #11)
    [Icall("UnityEngine.CanvasRenderer::SetPopMaterial")] static object CRSetPop(object[] a) { if (Render.K12Stencil.On) Render.K12Stencil.SetPop(CR(a[0]), N<NMaterial>(a[1]), (int)a[2]); return null; }
    [Icall("UnityEngine.CanvasRenderer::GetPopMaterial")] static object CRGetPop(object[] a) => Render.K12Stencil.On ? Render.K12Stencil.GetPop(CR(a[0]), (int)a[1])?.Managed : null;
    [Icall("UnityEngine.CanvasRenderer::get_materialCount")] static object CRMatCount(object[] a) => CR(a[0])?.MaterialCount ?? 0;
    [Icall("UnityEngine.CanvasRenderer::set_materialCount")] static object CRSetMatCount(object[] a) { var c = CR(a[0]); if (c != null) { c.MaterialCount = (int)a[1]; if (c.Materials.Length < c.MaterialCount) Array.Resize(ref c.Materials, c.MaterialCount); } return null; }
    [Icall("UnityEngine.CanvasRenderer::get_popMaterialCount")] static object CRPopCount(object[] a) => Render.K12Stencil.On ? Render.K12Stencil.PopCount(CR(a[0])) : 0;
    [Icall("UnityEngine.CanvasRenderer::set_popMaterialCount")] static object CRSetPopCount(object[] a) { if (Render.K12Stencil.On) Render.K12Stencil.SetPopCount(CR(a[0]), (int)a[1]); return null; }
    [Icall("UnityEngine.CanvasRenderer::SetTexture")] static object CRSetTex(object[] a) { var c = CR(a[0]); if (c != null) c.Texture = Tex(a[1]); return null; }
    [Icall("UnityEngine.CanvasRenderer::SetAlphaTexture")] static object CRSetAlphaTex(object[] a) { var c = CR(a[0]); if (c != null) c.AlphaTexture = Tex(a[1]); return null; }
    [Icall("UnityEngine.CanvasRenderer::EnableRectClipping_Injected")] static object CRClipOn(object[] a) { var c = CR(a[0]); if (c != null) { c.RectClipping = true; c.ClipRect = (Rect)a[1]; } return null; }
    [Icall("UnityEngine.CanvasRenderer::DisableRectClipping")] static object CRClipOff(object[] a) { var c = CR(a[0]); if (c != null) c.RectClipping = false; return null; }
    [Icall("UnityEngine.CanvasRenderer::get_hasRectClipping")] static object CRHasClip(object[] a) => CR(a[0])?.RectClipping ?? false;
    [Icall("UnityEngine.CanvasRenderer::get_cull")] static object CRCull(object[] a) => CR(a[0])?.Cull ?? false;
    [Icall("UnityEngine.CanvasRenderer::set_cull")] static object CRSetCull(object[] a) { var c = CR(a[0]); if (c != null) c.Cull = (bool)a[1]; return null; }
    [Icall("UnityEngine.CanvasRenderer::get_cullTransparentMesh")] static object CRCullT(object[] a) => CR(a[0])?.CullTransparent ?? false;
    [Icall("UnityEngine.CanvasRenderer::set_cullTransparentMesh")] static object CRSetCullT(object[] a) { var c = CR(a[0]); if (c != null) c.CullTransparent = (bool)a[1]; return null; }
    [Icall("UnityEngine.CanvasRenderer::get_hasMoved")] static object CRMoved(object[] a) => true;
    [Icall("UnityEngine.CanvasRenderer::get_absoluteDepth")] static object CRDepth(object[] a) => CR(a[0])?.AbsoluteDepth ?? 0;
    [Icall("UnityEngine.CanvasRenderer::get_relativeDepth")] static object CRRelDepth(object[] a) => CR(a[0])?.AbsoluteDepth ?? 0;
    [Icall("UnityEngine.CanvasRenderer::GetInheritedAlpha")] static object CRAlpha(object[] a) => UiAlpha(CR(a[0])?.Go);
    public static float UiAlpha(NGameObject go)
    {
        float al = 1f;
        // V6 stage 2: only the objects that hold a CanvasGroup have their components walked (the others contribute nothing; same
        // order, same products). A live object's CanvasGroups are exactly the live CanvasGroup records whose Go it is: a destroyed
        // component leaves its object's list (DestroyNow) or dies with its object, which is detached from the hierarchy first.
        var holders = GroupHolders();
        for (var t = go?.Transform; t != null; t = t.Parent)
        {
            var comps = t.Go.Components;   // (dereferenced as before)
            if (!holders.Contains(t.Go)) continue;
            foreach (var c in comps)
                if (c.Managed is CanvasGroup)
                {
                    al *= c.Get("alpha") is float f ? f : 1f;
                    if (c.Get("ignoreParentGroups") is true) return al;
                }
        }
        return al;
    }
    static readonly HashSet<NGameObject> _holders = new(); static int _holdersVer = -1;
    static HashSet<NGameObject> GroupHolders()
    {
        var reg = Registry.CanvasGroups; int v = reg.Version;
        if (v == _holdersVer) return _holders;
        _holders.Clear(); foreach (var n in reg.Items()) if (n is NComponent c && c.Go != null) _holders.Add(c.Go);
        _holdersVer = v; return _holders;
    }
    // kit3 (sub-menus): Unity's UIVertexHelper free functions behind CanvasRenderer.SplitUIVertexStreams / AddUIVertexStream /
    // CreateUIVertexStream, which uGUI's VertexHelper uses for GetUIVertexStream / AddUIVertexTriangleStream / AddUIVertexStream and so
    // every BaseMeshEffect (Shadow, Outline, the game's own mesh effects). Missing, GetUIVertexStream returned an empty list and the
    // effect's Clear + AddUIVertexTriangleStream left the graphic with no geometry at all (the Settings screen's outlined texts).
    // Native semantics: every destination list is RESIZED to the source count and overwritten (not appended to); a component list
    // shorter than the index stream (the overloads that pass a new, empty uv2S/uv3S) contributes zeros.
    [Icall("UnityEngine.CanvasRenderer::SplitUIVertexStreamsInternal")] static object CRSplitStreams(object[] a)
    {
        if (a[0] is not List<UIVertex> verts) return null;
        var v = CollectionsMarshal.AsSpan(verts); int n = v.Length;
        var pos = Sized<Vector3>(a[1], n); var col = Sized<Color32>(a[2], n);
        var uv0 = Sized<Vector4>(a[3], n); var uv1 = Sized<Vector4>(a[4], n); var uv2 = Sized<Vector4>(a[5], n); var uv3 = Sized<Vector4>(a[6], n);
        var nrm = Sized<Vector3>(a[7], n); var tan = Sized<Vector4>(a[8], n);
        for (int i = 0; i < n; i++)
        {
            ref readonly var x = ref v[i];
            if (pos.Length != 0) pos[i] = x.position; if (col.Length != 0) col[i] = x.color;
            if (uv0.Length != 0) uv0[i] = x.uv0; if (uv1.Length != 0) uv1[i] = x.uv1; if (uv2.Length != 0) uv2[i] = x.uv2; if (uv3.Length != 0) uv3[i] = x.uv3;
            if (nrm.Length != 0) nrm[i] = x.normal; if (tan.Length != 0) tan[i] = x.tangent;
        }
        return null;
    }
    /// <summary>The triangle-list indices of a split stream: 0, 1, ..., verts.Count - 1 (the list is resized to that count).</summary>
    [Icall("UnityEngine.CanvasRenderer::SplitIndicesStreamsInternal")] static object CRSplitIndices(object[] a)
    {
        if (a[0] is not List<UIVertex> verts) return null;
        var idx = Sized<int>(a[1], verts.Count);
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        return null;
    }
    /// <summary>One UIVertex per index (a triangle list): verts is resized to indices.Count and verts[i] = the components at indices[i].</summary>
    [Icall("UnityEngine.CanvasRenderer::CreateUIVertexStreamInternal")] static object CRCreateStream(object[] a)
    {
        if (a[0] is not List<UIVertex> verts || a[9] is not List<int> indices) return null;
        var ix = CollectionsMarshal.AsSpan(indices); int n = ix.Length;
        var v = Sized<UIVertex>(verts, n);
        var pos = Span<Vector3>(a[1]); var col = Span<Color32>(a[2]);
        var uv0 = Span<Vector4>(a[3]); var uv1 = Span<Vector4>(a[4]); var uv2 = Span<Vector4>(a[5]); var uv3 = Span<Vector4>(a[6]);
        var nrm = Span<Vector3>(a[7]); var tan = Span<Vector4>(a[8]);
        for (int i = 0; i < n; i++)
        {
            int k = ix[i];
            v[i] = new UIVertex
            {
                position = At(pos, k), color = At(col, k), uv0 = At(uv0, k), uv1 = At(uv1, k), uv2 = At(uv2, k), uv3 = At(uv3, k),
                normal = At(nrm, k), tangent = At(tan, k),
            };
        }
        return null;
    }
    /// <summary>The list (when it is a List&lt;T&gt;) resized to <paramref name="n"/> elements, as a span over its storage; else empty.</summary>
    static Span<T> Sized<T>(object list, int n)
    {
        if (list is not List<T> l) return default;
        CollectionsMarshal.SetCount(l, n);
        return CollectionsMarshal.AsSpan(l);
    }
    static Span<T> Span<T>(object list) => list is List<T> l ? CollectionsMarshal.AsSpan(l) : default;
    static T At<T>(Span<T> s, int i) => (uint)i < (uint)s.Length ? s[i] : default;
    [Icall("UnityEngine.CanvasRenderer::get_clippingSoftness_Injected")] static object CRSoft(object[] a) { a[1] = Vector2.zero; return null; }
    [Icall("UnityEngine.CanvasRenderer::set_clippingSoftness_Injected")] static object CRSetSoft(object[] a) => null;

    // ---------------- Canvas ----------------
    public static NComponent RootCanvasOf(NGameObject go)
    {
        NComponent root = null;
        for (var t = go?.Transform; t != null; t = t.Parent) foreach (var c in t.Go.Components) if (c.Managed is Canvas && !c.Destroyed) root = c;
        return root;
    }
    [Icall("UnityEngine.Canvas::get_isRootCanvas")] static object CIsRoot(object[] a) { var c = N<NComponent>(a[0]); return Boxes.Of(c != null && RootCanvasOf(c.Go) == c); }
    [Icall("UnityEngine.Canvas::get_rootCanvas")] static object CRoot(object[] a) { var c = N<NComponent>(a[0]); return RootCanvasOf(c?.Go)?.Managed; }
    [Icall("UnityEngine.Canvas::GetDefaultCanvasMaterial")] static object CDefMat(object[] a) => _uiDefault ??= NewMaterial(ShaderNamed("UI/Default"), "Default UI Material").Managed;
    [Icall("UnityEngine.Canvas::GetETC1SupportedCanvasMaterial")] static object CEtcMat(object[] a) => CDefMat(a);
    [Icall("UnityEngine.Canvas::GetDefaultCanvasTextMaterial")] static object CTextMat(object[] a) => _uiText ??= NewMaterial(ShaderNamed("UI/Default Font"), "Default UI Text").Managed;
    static UObject _uiDefault, _uiText;
    [Icall("UnityEngine.Canvas::get_pixelRect_Injected")] static object CPixRect(object[] a) { a[1] = new Rect(0, 0, AppIcalls.ScreenW, AppIcalls.ScreenH); return null; }
    [Icall("UnityEngine.Canvas::get_renderingDisplaySize_Injected")] static object CDisp(object[] a) { a[1] = Um.V2(AppIcalls.ScreenW, AppIcalls.ScreenH); if (HopCheck.On) HopCheck.V2(HopCheck.Vec, (Vector2)a[1], new Vector2(AppIcalls.ScreenW, AppIcalls.ScreenH)); return null; }   // (hop fix: was new Vector2(..))
    [Icall("UnityEngine.Canvas::UpdateCanvasRectTransform")] static object CUpdRect(object[] a) => null;
    // k4-display: the property bag as before (AutoProps' get/set), plus Unity's immediate root layout on a new factor (SceneRenderer.LayoutRootCanvasNow)
    [Icall("UnityEngine.Canvas::get_scaleFactor")] static object CScale(object[] a) => Objects.Get(a[0])?.Get("scaleFactor") is { } v ? Conv.To(v, typeof(float)) : 1f;
    [Icall("UnityEngine.Canvas::set_scaleFactor")] static object CSetScale(object[] a) { var c = N<NComponent>(a[0]); c?.Set("scaleFactor", a[1]); Render.SceneRenderer.LayoutRootCanvasNow(c); return null; }
    [Icall("UnityEngine.Canvas::get_renderOrder")] static object CRenderOrder(object[] a) => 0;
    // kit #9 fidelity (stand-in fix "canvas", kit7-tests.md finding 17; Runtime/K9Fidelity.cs): what a NESTED canvas reports, as Unity's
    // native getters do (the oracle's d-ui-canvas: 300 / 300) -- sortingOrder = the nearest canvas up the chain that is the root or overrides
    // sorting (its own stored value is not read while it does not override), renderMode = the root's effective mode (Screen Space - Camera
    // with no camera at all reads Screen Space - Overlay), referencePixelsPerUnit = the root's, pixelPerfect = the nearest canvas up the chain
    // that is the root or overrides pixelPerfect. Kit #8 read each canvas's own property bag (AutoProps): the switch-off path below. The
    // stored values are untouched (the renderer reads the bags as before); only what scripts read changes. A camera reference the scene
    // saved (a LazyRef) counts as a camera without being resolved here (no load from a getter); a saved reference to a missing object
    // would still read Screen Space - Camera (Unity: Overlay) -- noted in kit9-fidelity.md.
    static NComponent ParentCanvas(NComponent c)
    {
        for (var t = c?.Go?.Transform?.Parent; t != null; t = t.Parent) foreach (var p in t.Go.Components) if (p.Managed is Canvas && !p.Destroyed) return p;
        return null;
    }
    static NComponent Inherit(NComponent c, string overrideKey)
    {
        for (int guard = 0; c != null && guard < 4096; guard++) { var p = ParentCanvas(c); if (p == null || (bool)Conv.To(c.Get(overrideKey), typeof(bool))) return c; c = p; }
        return c;
    }
    [Icall("UnityEngine.Canvas::get_sortingOrder")] static object CSortOrder(object[] a)
    {
        if (!K9Fidelity.FixCanvas) return AutoProps.Read(a[0], "sortingOrder", typeof(int));
        var c = N<NComponent>(a[0]); return c == null ? AutoProps.Read(a[0], "sortingOrder", typeof(int)) : AutoProps.Read(Inherit(c, "overrideSorting").Managed, "sortingOrder", typeof(int));
    }
    [Icall("UnityEngine.Canvas::get_pixelPerfect")] static object CPixelPerfect(object[] a)
    {
        if (!K9Fidelity.FixCanvas) return AutoProps.Read(a[0], "pixelPerfect", typeof(bool));
        var c = N<NComponent>(a[0]); return c == null ? AutoProps.Read(a[0], "pixelPerfect", typeof(bool)) : AutoProps.Read(Inherit(c, "overridePixelPerfect").Managed, "pixelPerfect", typeof(bool));
    }
    [Icall("UnityEngine.Canvas::get_referencePixelsPerUnit")] static object CRefPpu(object[] a)
    {
        if (!K9Fidelity.FixCanvas) return AutoProps.Read(a[0], "referencePixelsPerUnit", typeof(float));
        var c = N<NComponent>(a[0]); var root = c == null ? null : RootCanvasOf(c.Go) ?? c;
        return AutoProps.Read(root?.Managed ?? a[0], "referencePixelsPerUnit", typeof(float));
    }
    [Icall("UnityEngine.Canvas::get_renderMode")] static object CRenderMode(object[] a)
    {
        if (!K9Fidelity.FixCanvas) return AutoProps.Read(a[0], "renderMode", typeof(RenderMode));
        var c = N<NComponent>(a[0]); var root = c == null ? null : RootCanvasOf(c.Go) ?? c;
        var m = AutoProps.Read(root?.Managed ?? a[0], "renderMode", typeof(RenderMode));
        return m is RenderMode rm && rm == RenderMode.ScreenSpaceCamera && root != null && root.Get("worldCamera") == null ? RenderMode.ScreenSpaceOverlay : m;
    }
    [Icall("UnityEngine.Canvas::SetExternalCanvasEnabled")] static object CExt(object[] a) => null;

    // kit #9 track runtime (lever 10; Icalls/K9Reg.cs, Runtime/K9Typed.cs): the typed twins of the hooks above -- the same expressions, no
    // object[] / boxes (a result goes back as Icalls.Ret<T> would give it: GetFloatImpl's Conv.To result through 'is float').
    internal static void K9(K9Reg r)
    {
        r.Add("UnityEngine.CanvasRenderer::get_cull", (Func<object, bool>)(s => CR(s)?.Cull ?? false), CRCull);
        r.Add("UnityEngine.Canvas::get_isRootCanvas", (Func<object, bool>)(s => { var c = N<NComponent>(s); return c != null && RootCanvasOf(c.Go) == c; }), CIsRoot);
        r.Add("UnityEngine.Mesh::get_subMeshCount", (Func<object, int>)(s => Me(s)?.Sub.Count ?? 1), MeshSubs);
        r.Add("UnityEngine.Material::GetFloatImpl", (Func<object, int, float>)((s, id) => Conv.To(Get(s, id) ?? 0f, typeof(float)) is float v ? v : 0f), MGetF);
        r.Add("UnityEngine.Sprite::get_rect_Injected", (Shepherd.Hooks.TypedIcalls.DV4)((object s, out float x, out float y, out float w, out float h) =>
            { var rc = S(s)?.Rect ?? default; x = rc.x; y = rc.y; w = rc.width; h = rc.height; }), SpRect);
    }
    /// <summary>kit #9 E5 (review fix): the constant / no-op mirrors of this class -- each key, the hook whose answer the stub copies, and that
    /// answer (Runtime/K9Typed.cs binds the mirrors only while each key is bound to this very method, and compares the answer in check mode).</summary>
    internal static IEnumerable<(string key, Shepherd.Hooks.IcallImpl hook, object value)> K9MirrorConsts() => new (string, Shepherd.Hooks.IcallImpl, object)[]
    {
        ("UnityEngine.Mesh::get_canAccess", MeshAccess, true), ("UnityEngine.Material::HasProperty", MHas, true),
        ("UnityEngine.UISystemProfilerApi::BeginSample", UiBegin, null), ("UnityEngine.UISystemProfilerApi::EndSample", UiEnd, null),
    };
}
