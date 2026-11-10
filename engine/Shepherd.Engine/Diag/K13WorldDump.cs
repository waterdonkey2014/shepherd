#if !SHEPHERD_EBOOT
using Shepherd.Engine.Native;
using Shepherd.Engine.Render;
using UnityEngine;
namespace Shepherd.Engine.Diag;

/// <summary>Kit #13 looks (Mac / tools builds only; never in the eboots): SHEPHERD_K13_WDUMP=&lt;frame&gt;[,&lt;frame&gt;...]:&lt;name&gt;[|&lt;name&gt;...] logs, in the
/// world pass of each listed frame, every drawn world renderer whose GameObject name contains one of the names (case-insensitive; "*" = all):
/// its kind, material (shader, keywords, every property), sprite / mesh, colour, the local-to-world matrix, and for a sprite its vertices
/// projected with the pass's camera (pixel x / y and clip w) with their uvs -- what the perspective row and the shader transcriptions read.
/// A reading aid: no frame changes.</summary>
[ProfileOnly]
public static class K13WorldDump
{
    static readonly (HashSet<int> frames, string[] names) Cfg = Parse(Environment.GetEnvironmentVariable("SHEPHERD_K13_WDUMP"));
    public static readonly bool On = Cfg.frames != null;
    static (HashSet<int>, string[]) Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        int c = s.IndexOf(':'); var names = c >= 0 ? s[(c + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : new[] { "*" };
        var f = new HashSet<int>();
        foreach (var p in (c >= 0 ? s[..c] : s).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) if (int.TryParse(p, out var v)) f.Add(v);
        return (f, names);
    }
    public static bool Want(NGameObject go)
    {
        if (!Cfg.frames.Contains(Runtime.Time.FrameCount) || go == null) return false;
        foreach (var n in Cfg.names) if (n == "*" || go.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    static string Path(NTransform t) { var s = t.Go?.Name ?? "?"; for (var p = t.Parent; p != null; p = p.Parent) s = (p.Go?.Name ?? "?") + "/" + s; return s; }
    public static string Mat(NMaterial m)
    {
        if (m == null) return "material none";
        var sb = new System.Text.StringBuilder($"material '{m.Name}' shader '{m.ShaderName}' kw=[{string.Join(" ", m.Keywords)}] state {m.Shader?.State}");
        foreach (var kv in m.P.OrderBy(kv => Impl.ShaderIds.Name(kv.Key), StringComparer.Ordinal))
        {
            var v = kv.Value switch { float f => f.ToString("0.#####"), Color c => $"({c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###})", Vector4 q => $"({q.x:0.###},{q.y:0.###},{q.z:0.###},{q.w:0.###})", Texture t => $"tex '{t.name}'", null => "null", var o => o.ToString() };
            sb.Append($" {Impl.ShaderIds.Name(kv.Key)}={v}");
        }
        return sb.ToString();
    }
    public static void Sprite(NComponent r, NSprite ns, NMaterial mat, Color col, bool fx, bool fy, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh)
    {
        Log.Info($"[k13 wdump] f{Runtime.Time.FrameCount} sprite '{Path(r.Go.Transform)}' layer {r.Go.Layer} sprite '{ns.Name}' tex '{ns.Texture?.Name}' {ns.Texture?.Width}x{ns.Texture?.Height} ppu {ns.PixelsPerUnit} rect {ns.Rect} colour {col} flip {fx}/{fy}");
        Log.Info($"[k13 wdump]   {Mat(mat)}");
        Log.Info($"[k13 wdump]   lw [{lw.m00:0.#####} {lw.m01:0.#####} {lw.m02:0.#####} {lw.m03:0.#####} | {lw.m10:0.#####} {lw.m11:0.#####} {lw.m12:0.#####} {lw.m13:0.#####} | {lw.m20:0.#####} {lw.m21:0.#####} {lw.m22:0.#####} {lw.m23:0.#####}] pixel rect ({pr.X},{pr.Y},{pr.W},{pr.H}) gh {gh}");
        if (ns.Vertices == null) return;
        bool outside = false;
        for (int i = 0; i < ns.Vertices.Length; i++)
        {
            var p = ns.Vertices[i]; var c = default(Vector4);
            SceneRenderer.Project(in lw, in vp, in pr, gh, fx ? -p.x : p.x, fy ? -p.y : p.y, 0, out var sx, out var sy, ref c, ref outside);
            var u = i < ns.Uv.Length ? ns.Uv[i] : default;
            Log.Info($"[k13 wdump]   v{i} local ({p.x:0.#####},{p.y:0.#####}) px ({sx:0.###},{sy:0.###}) clip w {c.w:0.#####} uv ({u.x:0.#####},{u.y:0.#####})");
        }
        Log.Info($"[k13 wdump]   tris [{string.Join(",", ns.Triangles ?? Array.Empty<ushort>())}]");
    }
    public static void Ui(NCanvasRenderer cr, NMaterial mat)
    {
        Log.Info($"[k13 wdump] f{Runtime.Time.FrameCount} ui '{Path(cr.Go.Transform)}' vertices {cr.Pos?.Length} colour {cr.Color} tex '{cr.Texture?.Name}'");
        Log.Info($"[k13 wdump]   {Mat(mat)}");
        Raw(mat);
    }
    /// <summary>The material's saved property lists as the asset has them (SHEPHERD_KEEP_TREES=1: the trees are kept): a float and a colour of
    /// the same name are one entry in NMaterial.P (the colour wins), the shader reads the one its declaration names.</summary>
    static void Raw(NMaterial mat)
    {
        if (mat?.Data is not Shepherd.Assets.TypeTreeObject d || d["m_SavedProperties"] is not Shepherd.Assets.TypeTreeObject p) return;
        foreach (var list in new[] { "m_Floats", "m_Ints", "m_Colors" })
            if (p[list] is System.Collections.IList l)
                Log.Info($"[k13 wdump]   raw {list}: " + string.Join(" ", l.OfType<Shepherd.Assets.TypeTreeObject>().Select(e => $"{e["first"]}={(e["second"] is Shepherd.Assets.TypeTreeObject c ? $"({c["r"]},{c["g"]},{c["b"]},{c["a"]})" : e["second"])}")));
    }
    public static void Mesh(NComponent r, NMesh mesh, object[] mats)
    {
        Log.Info($"[k13 wdump] f{Runtime.Time.FrameCount} mesh '{Path(r.Go.Transform)}' layer {r.Go.Layer} vertices {mesh?.VertexCount} submeshes {mesh?.Sub.Count}");
        for (int k = 0; k < (mats?.Length ?? 0); k++) Log.Info($"[k13 wdump]   [{k}] {Mat(Objects.Get<NMaterial>(mats[k]))}");
    }
}
#endif
