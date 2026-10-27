using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Diag;

/// <summary>V6: menu state as log lines, for runs without an image (SHEPHERD_RENDER=null). SHEPHERD_MENU_PROBE="f1,f2,..." logs,
/// after each listed frame:
///   [menu] frame F: selected='DLC' label='DLC' path='MainMenu/.../DLC'
///   [menu]   buttons (k): Play | *DLC* | Settings | ...        (the selected one's sibling Selectables with their labels, in order)
///   [menu]   texts (n): 'Press Any Button' ...                 (active TextMeshPro texts on screen, up to 16)
/// Read-only: only the EventSystem's current selection and component fields are read. No-op when unset.</summary>
[ProfileOnly]
public static class MenuProbe
{
    static readonly List<MenuClock.At> Frames = (Environment.GetEnvironmentVariable("SHEPHERD_MENU_PROBE") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => MenuClock.Parse(x, out var f) ? f : new MenuClock.At(-1, false)).Where(f => f.N > 0 || f.Rel).ToList();   // N or m+N (MenuClock)
    public static bool On => Frames.Count > 0;
    public static string LastSelected, LastLabel;
    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void After(int frame)
    {
        bool hit = false; foreach (var f in Frames) if (f.Frame == frame) hit = true;
        if (!hit) return;
        try { Probe(frame); } catch (Exception e) { Log.Info($"[menu] frame {frame}: probe failed: {e.GetBaseException().Message}"); }
    }

    static string Text(NMono m) { try { return m.Managed.GetType().GetProperty("text", I)?.GetValue(m.Managed) as string; } catch { return null; } }
    static bool IsTmp(NMono m) => m.Managed != null && m.Managed.GetType().Name.StartsWith("TextMeshPro", StringComparison.Ordinal);
    /// <summary>Rich-text tags dropped, newlines as spaces (no Regex: the probe must not load assemblies the run would not).</summary>
    static string Clean(string s)
    {
        if (s == null) return null;
        var sb = new System.Text.StringBuilder(s.Length); int depth = 0;
        foreach (var c in s) { if (c == '<') depth++; else if (c == '>' && depth > 0) depth--; else if (depth == 0) sb.Append(c is '\n' or '\r' ? ' ' : c); }
        return sb.ToString().Trim();
    }
    static NGameObject Selected(UnityEngine.EventSystems.EventSystem es)
    {
        var sel = es != null ? es.currentSelectedGameObject : null;
        return sel != null ? Objects.Get<NGameObject>(sel) : null;
    }
    /// <summary>The selected object's sibling Selectables with their labels, the selected one as *label*.</summary>
    static (int n, string list)? Buttons(NGameObject nsel)
    {
        if (nsel?.Transform.Parent is not { } parent) return null;
        var sibs = parent.Children.Select(c => c.Go).Where(g => g.ActiveInHierarchy && g.Components.OfType<NMono>().Any(m => m.Managed is UnityEngine.UI.Selectable)).ToList();
        return (sibs.Count, string.Join(" | ", sibs.Select(g => { var l = Label(g) ?? g.Name; return g == nsel ? $"*{l}*" : l; })));
    }
    /// <summary>V6 Stage B (the Vita host's checkpoint lines): the current selection, its label and its buttons, now.</summary>
    public static (string selected, string label, string buttons) Current()
    {
        try
        {
            var nsel = Selected(UnityEngine.EventSystems.EventSystem.current);
            var b = Buttons(nsel);
            return (nsel?.Name, nsel != null ? Label(nsel) : null, b is { } x ? $"({x.n}) {x.list}" : null);
        }
        catch (Exception e) { return (null, null, "probe failed: " + e.GetBaseException().Message); }
    }

    /// <summary>Stage B (VitaHost's per-frame menu watch): the selected object's name only, and its label on demand.</summary>
    public static string SelectedName() { try { return Selected(UnityEngine.EventSystems.EventSystem.current)?.Name; } catch { return null; } }
    public static string SelectedLabel() { try { var n = Selected(UnityEngine.EventSystems.EventSystem.current); return n != null ? Label(n) : null; } catch { return null; } }

    /// <summary>The first non-empty active TMP text at or under the object.</summary>
    static string Label(NGameObject go) =>
        Scenes.Walk(go).Where(g => g.ActiveInHierarchy).SelectMany(g => g.Components).OfType<NMono>().Where(m => !m.Destroyed && m.Enabled && IsTmp(m))
            .Select(m => Clean(Text(m))).FirstOrDefault(t => !string.IsNullOrEmpty(t));
    static string PathOf(NGameObject go) { var l = new List<string>(); for (var t = go.Transform; t != null; t = t.Parent) l.Add(t.Go.Name); l.Reverse(); return string.Join("/", l); }

    static void Probe(int frame)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var nsel = Selected(es);
        LastSelected = nsel?.Name; LastLabel = nsel != null ? Label(nsel) : null;
        Log.Info($"[menu] frame {frame}: selected='{nsel?.Name ?? "(none)"}' label='{LastLabel ?? ""}' path='{(nsel != null ? PathOf(nsel) : "")}' eventSystem={(es != null ? "yes" : "no")}");
        if (Buttons(nsel) is { } b) Log.Info($"[menu]   buttons ({b.n}): " + b.list);
        var texts = Objects.All.OfType<NMono>().Where(m => !m.Destroyed && m.Enabled && m.Go != null && m.Go.Scene != 0 && m.Go.ActiveInHierarchy && IsTmp(m))
            .Select(m => Clean(Text(m))).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        Log.Info($"[menu]   texts ({texts.Count}): " + string.Join(" ", texts.Take(16).Select(t => $"'{(t.Length > 40 ? t[..40] + "…" : t)}'")));
    }
}
