using Shepherd.Assets;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>Polish round 2 (survey item 5): what a draw's shader does to the frame, read from the shader asset's own serialized render
/// state (m_ParsedForm: the main pass's blend factors and colour mask, its queue tag, a GrabPass before it, the declared properties)
/// plus the material (property-driven blend factors / colour mask, BlendModes keywords). The engine runs no game shader: every
/// draw is texture x vertex colour with a blend mode. Before, every material drew as texture x tint, so custom FX shaders with no
/// texture (volumetric light spheres, volumetric fog, cloud shadows, the ForceShadowAttenuation helper quad) came out as opaque
/// fills. The decisions (vita/notes/polish-round1.md, stage 2, section S3):
/// - <b>Normal</b>: everything the old renderer drew right (alpha blend, premultiplied, opaque/alpha-tested) keeps the old path
///   unchanged -- every shader the Main Menu uses lands here, so the menu image cannot change.
/// - <b>Skip</b>: the main pass writes no colour (ColorMask 0: Hidden/ForwardBaseDummy, a shadow-attenuation helper), or blends
///   Zero/One (a no-op), or the shader is a blended custom FX shader with no texture bound -- its output is computed in the shader
///   (noise, depth fade, rims) and texture x tint has nothing to stand in for it (VolumetricFog, ScreenSpaceOverlay_CloudShadow).
/// - <b>Blend</b>: a fixed-function blend that is not the default: additive (SrcAlpha One / One One), multiply (DstColor Zero /
///   Zero SrcColor), 2x multiply (DstColor SrcColor), screen (OneMinusDstColor One / One OneMinusSrcColor). These reproduce the
///   shader's blending exactly; the colour is still texture x tint.
/// - <b>Glow</b>: an additive shader with no texture on a mesh (COTL/StencilLighting_Volumetric, the torch light spheres): drawn
///   additively with a per-vertex falloff (outward direction . view)^2 x tint, a soft disc instead of a flat one.
/// - <b>Grab</b>: the BlendModes package's SelfWithScreen shaders (Hidden/BlendModes/*/Grab, UnifiedGrab, Framebuffer; the mode is the
///   material's BLENDMODES_MODE_* keyword): the GL backend copies the backdrop under the draw and composites with the mode's
///   formula (lerp by alpha, or added when the shader's pass blends One One like DecalSpriteLighting); the SDL backend has no
///   shader stage and skips them. Distortion-style GrabPass shaders (KeyArt/Candles, FireGrabPass, ...) stay Normal.
/// UI draws use only the Grab rule (and the RT-content skip in SceneRenderer); their other shaders keep the old UI path.</summary>
[ProfileOnly]
public static class ShaderPolicy
{
    public enum Kind : byte { Normal, Skip, Blend, Glow, Grab }

    /// <summary>The render state of a shader asset's main pass (null on a shader record made at run time).</summary>
    public sealed class State
    {
        public int Queue = -1; public bool GrabBefore, HasMainTex, BlendModesGrab;
        public byte Src = 1, Dst = 0, ColMask = 15;   // UnityEngine.Rendering.BlendMode factors (One, Zero); colour mask RGBA = 15
        public int SrcProp, DstProp, ColMaskProp;      // ShaderIds of the material floats that drive them (0 = fixed in the shader)
        /// <summary>Kit #7 G6 (Render/G6Cull.cs): the main pass's Cull -- 0 Off, 1 Front, 2 Back, 255 unknown (no culling field in the asset and
        /// no cull-table row: drawn as before); CullProp = the material float that drives it; CullFrom 1 = the shader asset, 2 = the cull table.</summary>
        public byte Cull = 255, CullFrom; public int CullProp;
        public bool Dynamic => SrcProp != 0 || DstProp != 0 || ColMaskProp != 0;
        public override string ToString() => $"cull={(Cull == 255 ? "?" : Cull.ToString())}{(CullProp != 0 ? "[" + ShaderIds.Name(CullProp) + "]" : "")} queue={Queue} blend={Src}{(SrcProp != 0 ? "[" + ShaderIds.Name(SrcProp) + "]" : "")}/{Dst}{(DstProp != 0 ? "[" + ShaderIds.Name(DstProp) + "]" : "")} colMask={ColMask}{(ColMaskProp != 0 ? "[" + ShaderIds.Name(ColMaskProp) + "]" : "")} grab={GrabBefore} mainTex={HasMainTex}{(BlendModesGrab ? " blendmodes-grab" : "")}";
    }

    static readonly HashSet<string> NotMainPass = new(StringComparer.OrdinalIgnoreCase) { "ShadowCaster", "Meta", "Deferred", "PrepassBase", "PrepassFinal", "ForwardAdd", "MotionVectors", "DepthOnly", "ShadowCollector" };
    static readonly string[] GrabVariants = { "Grab", "UnifiedGrab", "Framebuffer", "GrabMasked", "UnifiedGrabMasked", "FramebufferMasked" };

    /// <summary>The main pass of the first subshader: the first normal pass (not a GrabPass, not a UsePass) whose LightMode is not a
    /// shadow / deferred / additive-light pass. From the shader asset's m_ParsedForm (a bundle object or its pack reduction).</summary>
    public static State Parse(TypeTreeObject pf, string name)
    {
        if (pf == null) return null;
        var st = new State();
        try
        {
            Dictionary<string, float> defs = null;
            if (pf["m_PropInfo"] is TypeTreeObject pi && pi["m_Props"] is List<object> props)
                foreach (var p in props)
                {
                    if (p is not TypeTreeObject pt || pt.Str("m_Name") is not { } pn) continue;
                    if (pn == "_MainTex") st.HasMainTex = true;
                    (defs ??= new())[pn] = pt.Float("m_DefValue[0]");   // a property-driven factor's value when the material lacks it
                }
            if (pf["m_SubShaders"] is not List<object> subs || subs.Count == 0 || subs[0] is not TypeTreeObject ss) return st;
            st.Queue = Queue(TagValue(ss["m_Tags"], "Queue"));
            TypeTreeObject main = null; bool grab = false;
            if (ss["m_Passes"] is List<object> passes)
                foreach (var po in passes)
                {
                    if (po is not TypeTreeObject pass) continue;
                    long type = pass.Long("m_Type");
                    if (type == 2) { grab = true; continue; }   // GrabPass
                    if (type != 0) continue;                       // UsePass: its state lives in another shader
                    var lm = TagValue(pass["m_Tags"], "LightMode") ?? TagValue((pass["m_State"] as TypeTreeObject)?["m_Tags"], "LightMode");
                    if (lm != null && NotMainPass.Contains(lm)) continue;
                    main = pass; break;
                }
            st.GrabBefore = grab && main != null;
            if (main?["m_State"] is TypeTreeObject s && s["rtBlend0"] is TypeTreeObject b)
            {
                (st.Src, st.SrcProp) = Val(b["srcBlend"], 1, defs); (st.Dst, st.DstProp) = Val(b["destBlend"], 0, defs); (st.ColMask, st.ColMaskProp) = Val(b["colMask"], 15, defs);
            }
            // kit #7 G6: the main pass's Cull (Unity's default Back when the shader says nothing: the serialized state always carries it; the
            // Vita pack's reduction drops it -- G6Cull.Complete then asks the owner-local cull table)
            if (main?["m_State"] is TypeTreeObject sc && sc["culling"] is TypeTreeObject cu) { (st.Cull, st.CullProp) = Val(cu, 2, defs); st.CullFrom = 1; }
            if (name.StartsWith("Hidden/BlendModes/", StringComparison.Ordinal) && Array.IndexOf(GrabVariants, name[(name.LastIndexOf('/') + 1)..]) >= 0) st.BlendModesGrab = true;
        }
        catch (Exception e) { Log.Warn($"[shader] '{name}': render state not readable ({e.GetType().Name}); drawn as before"); return null; }
        if (G6Cull.On) G6Cull.Complete(st, name);
        return st;
    }
    /// <summary>A SerializedShaderFloatValue: its fixed value, or (name set) the material float that drives it, whose shader default
    /// (m_PropInfo) stands in when the material does not have it -- what Unity does.</summary>
    static (byte v, int prop) Val(object o, byte dflt, Dictionary<string, float> defs)
    {
        if (o is not TypeTreeObject t) return (dflt, 0);
        var n = t.Str("name"); bool driven = !string.IsNullOrEmpty(n) && n != "<noninit>";
        float v = driven ? (defs != null && defs.TryGetValue(n, out var d) ? d : dflt) : t.Float("val");
        return ((byte)Math.Clamp((int)v, 0, 255), driven ? ShaderIds.Id(n) : 0);
    }
    static string TagValue(object tags, string key)
    {
        if (tags is TypeTreeObject t && t["tags"] is List<object> l)
            foreach (var kv in l) if (kv is TypeTreeObject p && string.Equals(p.Str("first"), key, StringComparison.OrdinalIgnoreCase)) return p.Str("second");
        return null;
    }
    static int Queue(string q)
    {
        if (string.IsNullOrEmpty(q)) return -1;
        q = q.Trim(); int i = 0; while (i < q.Length && char.IsLetter(q[i])) i++;
        int b = q[..i].ToLowerInvariant() switch { "background" => 1000, "geometry" => 2000, "alphatest" => 2450, "geometrylast" => 2500, "transparent" => 3000, "overlay" => 4000, "" => 0, _ => -1 };
        if (b < 0) return -1;
        return int.TryParse(q[i..].Replace("+", ""), out var off) ? b + off : b;
    }

    // ---------- blend modes beyond SDL's named ones ----------
    // SDL3's SDL_ComposeCustomBlendMode encoding (SDL_COMPOSE_BLENDMODE), which SDL_SetTextureBlendMode takes as is and GlBackend decodes:
    // colour op | src colour factor << 4 | dst colour factor << 8 | alpha op << 16 | src alpha factor << 20 | dst alpha factor << 24.
    // Factors: ZERO 1, ONE 2, SRC_COLOR 3, ONE_MINUS_SRC_COLOR 4, SRC_ALPHA 5, ONE_MINUS_SRC_ALPHA 6, DST_COLOR 7, ONE_MINUS_DST_COLOR 8,
    // DST_ALPHA 9, ONE_MINUS_DST_ALPHA 10; operation ADD 1. Only ADD is used (GLES2 without glBlendEquation, as the GL backend binds it).
    public static uint Compose(uint srcC, uint dstC, uint srcA, uint dstA) => 1u | (srcC << 4) | (dstC << 8) | (1u << 16) | (srcA << 20) | (dstA << 24);
    public static readonly uint AddOne = Compose(2, 2, 1, 2);       // One One: dst + src (destination alpha kept)
    public static readonly uint Mul2x = Compose(7, 3, 1, 2);        // DstColor SrcColor: 2 src dst
    public static readonly uint Screen = Compose(8, 2, 1, 2);       // OneMinusDstColor One: src + dst - src dst
    public static bool IsComposed(uint b) => b > 0xFF && (b & 0xF0000000u) == 0;
    /// <summary>A BlendModes grab draw: GrabFlag | mode (BlendModes.BlendMode, 0..24) &lt;&lt; 8 | 1 when the result is added (pass One One).</summary>
    public const uint GrabFlag = 0x40000000u;
    public static bool IsGrab(uint b) => (b & 0xF0000000u) == GrabFlag;
    public static int GrabMode(uint b) => (int)((b >> 8) & 0xFF);
    public static bool GrabAdds(uint b) => (b & 1) != 0;

    static readonly string[] Modes = { "DARKEN", "MULTIPLY", "COLORBURN", "LINEARBURN", "DARKERCOLOR", "LIGHTEN", "SCREEN", "COLORDODGE", "LINEARDODGE", "LIGHTERCOLOR", "OVERLAY", "SOFTLIGHT", "HARDLIGHT", "VIVIDLIGHT", "LINEARLIGHT", "PINLIGHT", "HARDMIX", "DIFFERENCE", "EXCLUSION", "SUBTRACT", "DIVIDE", "HUE", "SATURATION", "COLOR", "LUMINOSITY" };
    public static string ModeName(int m) => m >= 0 && m < Modes.Length ? Modes[m] : "?";
    static int ModeOf(NMaterial m)
    {
        foreach (var k in m.Keywords)
            if (k.StartsWith("BLENDMODES_MODE_", StringComparison.Ordinal)) { int i = Array.IndexOf(Modes, k[16..]); if (i >= 0) return i; }
        return -1;
    }
    static float F(NMaterial m, int id, float d) => id != 0 && m.P.TryGetValue(id, out var v) ? v switch { float f => f, int i => i, _ => d } : d;

    /// <summary>A world draw (SpriteRenderer, MeshRenderer submesh): what to do with it, and the blend mode for Blend / Glow / Grab.</summary>
    public static Kind World(NMaterial mat, bool textured, bool hasMesh, out uint blend)
    {
        blend = 0;
        var st = mat?.Shader?.State;
        if (st == null) return Kind.Normal;
        if (st.BlendModesGrab) return GrabOf(mat, st, out blend);
        int src = st.Src, dst = st.Dst, cm = st.ColMask;
        if (st.Dynamic) { src = (int)F(mat, st.SrcProp, src); dst = (int)F(mat, st.DstProp, dst); cm = (int)F(mat, st.ColMaskProp, cm); }
        if ((cm & 15) == 0) return Kind.Skip;                                 // writes no colour
        if (src == 0 && dst == 1) return Kind.Skip;                           // Blend Zero One: leaves the target as it is
        switch (src, dst)
        {
            case (1, 1): blend = AddOne; break;                               // One One
            case (5, 1): blend = BLENDMODE_ADD; break;                        // SrcAlpha One
            case (2, 0): case (0, 3): blend = BLENDMODE_MOD; break;           // DstColor Zero / Zero SrcColor
            case (2, 3): case (3, 2): blend = Mul2x; break;                   // DstColor SrcColor
            case (4, 1): case (1, 6): blend = Screen; break;                  // OneMinusDstColor One / One OneMinusSrcColor
        }
        if (blend != 0)
        {
            if (textured) return Kind.Blend;
            return (blend == AddOne || blend == BLENDMODE_ADD) && hasMesh ? Kind.Glow : Kind.Skip;
        }
        // the default blends: alpha (SrcAlpha OneMinusSrcAlpha), premultiplied (One OneMinusSrcAlpha), opaque / alpha-tested (One Zero)
        if (!textured && !(src == 1 && dst == 0)) return Kind.Skip;            // a blended custom shader with nothing to sample: its output is not texture x tint
        return Kind.Normal;
    }
    /// <summary>A UI draw: only the BlendModes grab shaders change.</summary>
    public static Kind Ui(NMaterial mat, out uint blend)
    {
        blend = 0;
        var st = mat?.Shader?.State;
        return st != null && st.BlendModesGrab ? GrabOf(mat, st, out blend) : Kind.Normal;
    }
    static Kind GrabOf(NMaterial mat, State st, out uint blend)
    {
        blend = 0;
        int mode = ModeOf(mat); if (mode < 0) return Kind.Normal;            // no mode keyword yet: the plain variant's alpha blend
        blend = GrabFlag | ((uint)mode << 8) | (st.Src == 1 && st.Dst == 1 ? 1u : 0u);
        return Kind.Grab;
    }

    /// <summary>SHEPHERD_FX_LOG=1: one line per (shader, kind) the first time a world or UI draw meets it.</summary>
    static readonly bool LogOn = Environment.GetEnvironmentVariable("SHEPHERD_FX_LOG") == "1";
    static readonly HashSet<(NShader, Kind)> _logged = new();
    public static void Note(NMaterial mat, Kind k, uint blend, NGameObject go = null)
    {
        if (!LogOn || mat?.Shader is not { } sh || !_logged.Add((sh, k))) return;
        Log.Info($"[fx] '{sh.ShaderName}' -> {k}{(k == Kind.Grab ? $" {ModeName(GrabMode(blend))}{(GrabAdds(blend) ? " (added)" : "")}" : k == Kind.Blend || k == Kind.Glow ? $" blend 0x{blend:X}" : "")} ({sh.State}) material '{mat.Name}'{(go != null ? $" on '{go.Name}'" : "")}");
    }
}
