#if !SHEPHERD_EBOOT
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Diag;

/// <summary>Kit #12 boot track (Mac / tools builds only; never in the eboots): SHEPHERD_K12_UIDUMP=&lt;frame&gt;[,&lt;frame&gt;...][:&lt;object name&gt;] writes one
/// log block per listed frame for every active UI subtree rooted at a GameObject of that name (default: every root canvas): per object its
/// components, and per CanvasRenderer its mesh (vertex / index counts, the screen-space bounds of its positions, the uv bounds), colour,
/// texture, material (shader, keywords, every stencil / colour-mask / alpha-clip property uGUI's StencilMaterial sets) and, for an Image,
/// its sprite (name, texture, rect, packing). What the boot screen's rings (a uGUI Mask) and the emblem (MMTransition's pentagramImage)
/// hand the renderer: a reading aid, no frame changes.</summary>
[ProfileOnly]
public static class K12UiDump
{
    static readonly (HashSet<int> frames, string name) Cfg = Parse(Environment.GetEnvironmentVariable("SHEPHERD_K12_UIDUMP"));
    static (HashSet<int>, string) Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        string name = null; int c = s.IndexOf(':');
        if (c >= 0) { name = s[(c + 1)..].Trim(); s = s[..c]; }
        var f = new HashSet<int>();
        foreach (var p in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) if (int.TryParse(p, out var v)) f.Add(v);
        return (f, string.IsNullOrEmpty(name) ? null : name);
    }
    static readonly string TexDir = Environment.GetEnvironmentVariable("SHEPHERD_K12_UIDUMP_TEX") is { Length: > 0 } td ? td : null;
    static readonly string[] Props = { "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask", "_UseUIAlphaClip" };

    static readonly (int frame, string[] terms) Loc = ParseLoc(Environment.GetEnvironmentVariable("SHEPHERD_K12_LOCDUMP"));
    static (int, string[]) ParseLoc(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (-1, null);
        int c = s.IndexOf(':'); if (c < 0 || !int.TryParse(s[..c], out var f)) return (-1, null);
        return (f, s[(c + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
    /// <summary>SHEPHERD_K12_LOCDUMP=&lt;frame&gt;:&lt;term&gt;,... -- every language the game ships (I2's LocalizationManager.GetAllLanguages) and each term's
    /// translation in it (GetTranslation with the language as override): what the title's prompt says in each (a reading aid).</summary>
    static void LocDump()
    {
        var lm = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("I2.Loc.LocalizationManager")).FirstOrDefault(t => t != null);
        if (lm == null) { Log.Info("[k12 locdump] no I2.Loc.LocalizationManager"); return; }
        var all = lm.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public).FirstOrDefault(m => m.Name == "GetAllLanguages" && m.GetParameters().Length == 1)?.Invoke(null, new object[] { true }) as System.Collections.IEnumerable;
        var codes = lm.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public).FirstOrDefault(m => m.Name == "GetLanguageCode" && m.GetParameters().Length == 1);
        var tr = lm.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public).Where(m => m.Name == "GetTranslation").OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
        Log.Info($"[k12 locdump] current language '{lm.GetProperty("CurrentLanguage")?.GetValue(null)}'");
        try
        {   // the TMP sprite assets' sprite names (a glyph for the title's prompt) and whether the prompt's font has U+2715
            foreach (var sa in Objects.All.OfType<NObj>().Select(o => o.Managed).Where(m => m != null && m.GetType().Name == "TMP_SpriteAsset").Distinct().Take(12))
            {
                var names = new List<string>();
                if (sa.GetType().GetProperty("spriteCharacterTable")?.GetValue(sa) is System.Collections.IEnumerable tab)
                    foreach (var ch in tab) names.Add(ch.GetType().GetProperty("name")?.GetValue(ch) as string);
                Log.Info($"[k12 locdump] sprite asset '{(sa as UnityEngine.Object)?.name}': {names.Count} sprites: {string.Join(" ", names.Take(400))}");
            }
            foreach (var fa in Objects.All.OfType<NObj>().Select(o => o.Managed).Where(m => m != null && m.GetType().Name == "TMP_FontAsset").Distinct().Take(20))
            {
                var has = fa.GetType().GetMethods().FirstOrDefault(m => m.Name == "HasCharacter" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(char));
                object r = null; try { r = has?.Invoke(fa, new object[] { '\u2715', false, false }); } catch (Exception e) { r = "EXC " + e.GetBaseException().Message; }
                object r2 = null; try { r2 = has?.Invoke(fa, new object[] { '\u00d7', false, false }); } catch { }
                Log.Info($"[k12 locdump] font asset '{(fa as UnityEngine.Object)?.name}': U+2715 {r}, U+00D7 {r2}");
            }
        }
        catch (Exception e) { Log.Info("[k12 locdump] sprite / font scan: " + e.GetBaseException().Message); }
        foreach (var l in all?.Cast<object>() ?? Enumerable.Empty<object>())
        {
            var lang = l as string; string code = null; try { code = codes?.Invoke(null, new object[] { lang }) as string; } catch { }
            foreach (var t in Loc.terms)
            {
                string v = null;
                try { var ps = tr.GetParameters(); var args = new object[ps.Length]; for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null; args[0] = t; int li = Array.FindIndex(ps, p => p.Name == "overrideLanguage"); if (li >= 0) args[li] = lang; v = tr.Invoke(null, args) as string; }
                catch (Exception e) { v = "EXC " + e.GetBaseException().Message; }
                Log.Info($"[k12 locdump] {lang} ({code}) {t} = \"{v?.Replace("\n", "\\n")}\"");
            }
        }
    }

    public static void After(int frame)
    {
        if (Loc.terms != null && frame == Loc.frame) try { LocDump(); } catch (Exception e) { Log.Exception(e, "k12 locdump"); }
        if (Cfg.frames == null || !Cfg.frames.Contains(frame)) return;
        try
        {
            Log.Info($"[k12 uidump] frame {frame}{(Cfg.name != null ? $" objects named '{Cfg.name}'" : " root canvases")}");
            foreach (var go in Objects.All.OfType<NGameObject>().Where(g => !g.Destroyed && g.Scene != 0 && g.ActiveInHierarchy).ToList())
            {
                if (Cfg.name != null ? go.Name != Cfg.name : !go.Components.Any(c => c is NBehaviour b && b.Managed is Canvas && Impl.RenderIcalls.RootCanvasOf(go) == b)) continue;
                Walk(go, 0);
            }
        }
        catch (Exception e) { Log.Exception(e, "k12 uidump"); }
    }

    static void Walk(NGameObject go, int depth)
    {
        if (depth > 14 || !go.ActiveSelf) return;
        string pad = new(' ', depth * 2);
        var comps = string.Join(",", go.Components.Where(c => c is not NTransform).Select(c => c is NMono m ? (m.Managed?.GetType().Name ?? "?") + (m.Enabled ? "" : "(off)") : c.GetType().Name));
        Log.Info($"[k12 uidump] {pad}{go.Name} [{comps}]");
        foreach (var c in go.Components)
        {
            if (c is NMono m && m.Managed is UnityEngine.UI.Mask mk) Log.Info($"[k12 uidump] {pad}  Mask enabled={m.Enabled} showMaskGraphic={mk.showMaskGraphic}");
            if (c is NMono im && im.Managed is UnityEngine.UI.Image img)
            {
                var sp = img.sprite; var ns = sp != null ? Objects.Get<NSprite>(sp) : null;
                Log.Info($"[k12 uidump] {pad}  Image type={img.type} sprite='{sp?.name}' nsprite={(ns != null ? $"tex={ns.Texture?.Name}({ns.Texture?.Width}x{ns.Texture?.Height}) rect={ns.Rect} texRect={ns.TextureRect} packed={ns.Packed} rot={ns.PackingRotation} verts={ns.Vertices?.Length ?? -1}" : "none")} preserveAspect={img.preserveAspect} fill={img.fillAmount} mat='{img.material?.name}' color={img.color}");
            }
            if (c is not NCanvasRenderer cr || cr.Destroyed) continue;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            var root = Impl.RenderIcalls.RootCanvasOf(cr.Go);
            for (int i = 0; i < cr.Pos.Length; i++)
            {
                var p = cr.Pos[i];
                Matrix4x4 mm = cr.Go.Transform.LocalToWorld;
                var w = mm.MultiplyPoint3x4(p);
                x0 = Math.Min(x0, w.x); x1 = Math.Max(x1, w.x); y0 = Math.Min(y0, w.y); y1 = Math.Max(y1, w.y);
                if (cr.Uv0 != null && i < cr.Uv0.Length) { var u = cr.Uv0[i]; u0 = Math.Min(u0, u.x); u1 = Math.Max(u1, u.x); v0 = Math.Min(v0, u.y); v1 = Math.Max(v1, u.y); }
            }
            var mat = cr.Materials.Length > 0 ? cr.Materials[0] : null;
            string props = mat == null ? "" : string.Join(" ", Props.Where(n => mat.P.ContainsKey(Impl.ShaderIds.Id(n))).Select(n => $"{n}={mat.P[Impl.ShaderIds.Id(n)]}"));
            if (TexDir != null && cr.Texture != null && cr.Texture.Width <= 2048 && cr.Texture.Height <= 2048)
            {   // SHEPHERD_K12_UIDUMP_TEX=<dir>: the texture as the engine decoded it (game-derived: a work folder only, never git)
                var px = Render.RenderKit.Pixels(cr.Texture, keep: false);
                if (px != null) { Directory.CreateDirectory(TexDir); Render.Png.Write(Path.Combine(TexDir, $"{go.Name.Replace('/', '_')}-{cr.Texture.Name}.png"), cr.Texture.Width, cr.Texture.Height, px); }
            }
            Log.Info($"[k12 uidump] {pad}  CR cull={cr.Cull} v={cr.Pos.Length} i={cr.Idx.Length} world=({x0:F1},{y0:F1})-({x1:F1},{y1:F1}) uv=({u0:F3},{v0:F3})-({u1:F3},{v1:F3}) color={cr.Color} alpha={Impl.RenderIcalls.UiAlpha(cr.Go):F3} tex={cr.Texture?.Name}({cr.Texture?.Width}x{cr.Texture?.Height} fmt {cr.Texture?.Format} filt {cr.Texture?.FilterMode} wrap {cr.Texture?.WrapMode}) mats={cr.MaterialCount} shader='{mat?.ShaderName}' kw=[{(mat != null ? string.Join(",", mat.Keywords) : "")}] {props} rectClip={cr.RectClipping}");
        }
        foreach (var c in go.Components)
            if (c is NCanvasRenderer cr2 && !cr2.Destroyed && cr2.Materials.Length > 0 && cr2.Materials[0] is { } m2 && (m2.ShaderName.StartsWith("COTL/") || m2.ShaderName.StartsWith("Watercolor/")))
                Log.Info($"[k12 uidump] {pad}  material '{m2.Name}' state {m2.Shader?.State} props: " + string.Join(" ", m2.P.Select(kv => $"{Impl.ShaderIds.Name(Math.Abs(kv.Key)) ?? kv.Key.ToString()}{(kv.Key < 0 ? "_ST" : "")}={(kv.Value is NTexture tx ? "tex:" + tx.Name + $"({tx.Width}x{tx.Height} wrap {tx.WrapMode})" : kv.Value is UnityEngine.Object uo ? "obj:" + uo.name : kv.Value)}")));
        foreach (var ch in go.Transform.Children) Walk(ch.Go, depth + 1);
    }
}
#endif
