#if !SHEPHERD_EBOOT
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #12 (track menusb), Mac / tools only: a dump of a UI tree (names, active flags, components, rect transforms, text and
/// font style, image sprite names) to a file, to read the game's prefabs (the Settings menu's tabs, rows, headers, dividers) while the
/// engine runs them. <c>SHEPHERD_K12_DUMP=&lt;dir&gt;</c> + <c>SHEPHERD_K12_DUMP_FRAMES=f1,f2,...</c>: the open Settings menu's tree at each
/// listed frame. Never in an eboot (the whole file is Mac-only).</summary>
public static class K12Dump
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("SHEPHERD_K12_DUMP");
    static readonly HashSet<int> Frames = Parse(Environment.GetEnvironmentVariable("SHEPHERD_K12_DUMP_FRAMES"));
    static HashSet<int> Parse(string s)
    {
        var h = new HashSet<int>();
        if (s != null) foreach (var p in s.Split(',')) if (int.TryParse(p.Trim(), out var f)) h.Add(f);
        return h;
    }

    /// <summary>Called every frame while the Settings menu is open (VitaToggles.Tick): dumps <paramref name="root"/> at the listed frames.</summary>
    public static void Tick(Component root)
    {
        if (Dir == null || root == null || !Frames.Contains(Time.FrameCount)) return;
        Dump(root.transform, Path.Combine(Dir, "settings-f" + Time.FrameCount + ".txt"));
    }

    public static void Dump(Transform root, string path)
    {
        var sb = new StringBuilder();
        Walk(root, 0, sb);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, sb.ToString());
        Log.Info("[k12] dump " + path);
    }

    static void Walk(Transform t, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).Append(t.gameObject.activeSelf ? "+ " : "- ").Append(t.name);
        if (t is RectTransform rt)
            sb.Append(" @(").Append(rt.anchoredPosition.x.ToString("F0")).Append(',').Append(rt.anchoredPosition.y.ToString("F0")).Append(") sz(")
              .Append(rt.sizeDelta.x.ToString("F0")).Append(',').Append(rt.sizeDelta.y.ToString("F0")).Append(") a(")
              .Append(rt.anchorMin.x.ToString("F2")).Append(',').Append(rt.anchorMin.y.ToString("F2")).Append('-')
              .Append(rt.anchorMax.x.ToString("F2")).Append(',').Append(rt.anchorMax.y.ToString("F2")).Append(") r(")
              .Append(rt.rect.width.ToString("F0")).Append('x').Append(rt.rect.height.ToString("F0")).Append(')');
        sb.Append(" [");
        foreach (var c in t.GetComponents<Component>())
        {
            if (c == null || c is Transform) continue;
            var ty = c.GetType();
            sb.Append(ty.Name);
            if (c is Behaviour b && !b.enabled) sb.Append("(off)");
            if (ty.Name == "TextMeshProUGUI" || ty.Name == "TextMeshPro")
            {
                string Prop(string n) { try { return ty.GetProperty(n, BindingFlags.Instance | BindingFlags.Public)?.GetValue(c)?.ToString(); } catch { return "?"; } }
                var txt = Prop("text") ?? ""; if (txt.Length > 60) txt = txt.Substring(0, 60) + "...";
                sb.Append("{\"").Append(txt.Replace("\n", "\\n")).Append("\" style=").Append(Prop("fontStyle")).Append(" size=").Append(Prop("fontSize"))
                  .Append(" col=").Append(Prop("color")).Append(" font=").Append(Prop("font")).Append(" align=").Append(Prop("alignment")).Append('}');
            }
            else if (c is Image im) sb.Append("{").Append(im.sprite != null ? im.sprite.name : "-").Append(" col=").Append(im.color.ToString()).Append(" type=").Append(im.type).Append('}');
            else if (ty.Name == "LayoutElement" || ty.Name == "VerticalLayoutGroup" || ty.Name == "HorizontalLayoutGroup")
            {
                try
                {
                    if (c is LayoutElement le) sb.Append("{h=").Append(le.preferredHeight).Append(" minh=").Append(le.minHeight).Append('}');
                    if (c is HorizontalOrVerticalLayoutGroup lg) sb.Append("{sp=").Append(lg.spacing).Append(" pad=").Append(lg.padding?.top).Append('/').Append(lg.padding?.bottom).Append('}');
                }
                catch { }
            }
            sb.Append(' ');
        }
        sb.Append("]\n");
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
    }
}
#endif
