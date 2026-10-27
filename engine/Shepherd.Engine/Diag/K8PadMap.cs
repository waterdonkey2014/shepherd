#if !SHEPHERD_EBOOT
using System.Reflection;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #8, track play (vita/notes/kit8-play.md, "Controls parity"): a Mac-only measurement -- the game's own Rewired maps
/// as the Vita pad sees them. SHEPHERD_K8_PADMAP=&lt;frame&gt;[,&lt;frame&gt;] logs, at that frame, every controller map of Rewired's player 0
/// (the joystick the Vita pad is, the keyboard): category / layout / enabled and each element -> action (name, descriptive name, axis
/// contribution), plus every action of the game's data. The output is derived game data: it stays in the run's engine.log (the work dir,
/// never git). Reflection only; no game DLL is changed. Not in the eboots.</summary>
[ProfileOnly]
public static class K8PadMap
{
    static readonly HashSet<int> At = Parse(Environment.GetEnvironmentVariable("SHEPHERD_K8_PADMAP"));
    static HashSet<int> Parse(string s) { var h = new HashSet<int>(); foreach (var p in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) if (int.TryParse(p, out var f)) h.Add(f); return h; }
    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object P(object o, string n) => o?.GetType().GetProperty(n, I)?.GetValue(o) ?? o?.GetType().GetField(n, I)?.GetValue(o);

    /// <summary>SHEPHERD_K8_PADMAP_PROBE=&lt;frame&gt;: which Rewired element each Unity joystick button / axis is (the game's hardware map for
    /// the Vita pad's virtual DualShock 4): from that frame, every 4 frames one Unity button (Joystick1Button0..19) is pressed for one
    /// frame, then each of the 8 axes is pushed to +1 for one frame; the frame after, the joystick's elements that read non-rest are logged.</summary>
    static readonly int ProbeAt = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K8_PADMAP_PROBE"), out var pa) ? pa : -1;
    public static void Before(int frame)
    {
        if (ProbeAt < 0 || frame < ProbeAt) return;
        int k = (frame - ProbeAt) / 4, ph = (frame - ProbeAt) % 4;
        if (k < 20) { if (ph == 0) Impl.InputState.Press(350 + k); else if (ph == 1) Impl.InputState.Release(350 + k); }
        else if (k < 28) { int a = k - 20; var ax = Impl.InputState.JoyAxes; if (ph == 0) { _rest = ax[a]; ax[a] = 1f; } else if (ph == 2) ax[a] = _rest; }
    }
    static float _rest;
    static void Probe(int frame)
    {
        if (ProbeAt < 0 || frame < ProbeAt) return;
        int k = (frame - ProbeAt) / 4, ph = (frame - ProbeAt) % 4;
        if (k >= 28 || ph != 0) return;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rewired_Core");
            var ctrls = asm?.GetType("Rewired.ReInput")?.GetProperty("controllers", S)?.GetValue(null);
            var hits = new List<string>();
            if (P(ctrls, "Joysticks") is System.Collections.IEnumerable js)
                foreach (var j in js.Cast<object>())
                {
                    if (P(j, "Buttons") is System.Collections.IEnumerable bs) foreach (var b in bs.Cast<object>()) if (P(b, "value") is true) hits.Add("button '" + P(P(b, "elementIdentifier"), "name") + "'");
                    if (P(j, "Axes") is System.Collections.IEnumerable xs) foreach (var x in xs.Cast<object>()) if (P(x, "value") is float v && Math.Abs(v) > 0.01f) hits.Add("axis '" + P(P(x, "elementIdentifier"), "name") + "'=" + v.ToString("0.##"));
                }
            Log.Info("[k8padmap] probe " + (k < 20 ? "Unity Joystick1Button" + k : "Unity Joy1Axis" + (k - 20 + 1) + " = +1") + " (frame " + frame + "): " + (hits.Count == 0 ? "no element" : string.Join(", ", hits)));
        }
        catch (Exception e) { Log.Info("[k8padmap] probe: " + e.GetBaseException().Message); }
    }

    /// <summary>SHEPHERD_K8_LINKS_PROBE=&lt;frame&gt;[,...]: the hierarchy around the PC-only link elements (the Main / Pause menus' Discord
    /// button, MenuAdController, MenuDLCAdController, the Main Menu's 'ad' group): each one's path, active state, components, children, and
    /// every active Selectable whose explicit navigation points at one of them (Mac measurement for kit8-play.md "Hide links").</summary>
    static readonly HashSet<int> LinksAt = Parse(Environment.GetEnvironmentVariable("SHEPHERD_K8_LINKS_PROBE"));
    static string PathOf(NTransform t) { var s = t.Go.Name; for (var p = t.Parent; p != null; p = p.Parent) s = p.Go.Name + "/" + s; return s; }
    static string Comps(NGameObject g) => string.Join(",", g.Components.Where(c => c is not NTransform).Select(c => c.Managed?.GetType().Name ?? "?"));
    static void Links(int frame)
    {
        if (LinksAt.Count == 0 || !LinksAt.Contains(frame)) return;
        try
        {
            var targets = new List<(string what, NGameObject go)>();
            foreach (var m in Objects.All.OfType<NMono>())
            {
                if (m.Destroyed || m.Managed == null || m.Go == null) continue;
                var tn = m.Managed.GetType().FullName;
                if (tn is "MenuAdController" or "MenuDLCAdController") targets.Add((tn, m.Go));
                if (tn is "Lamb.UI.MainMenu.UIMainMenuController" or "Lamb.UI.PauseMenu.UIPauseMenuController")
                {
                    if (P(m.Managed, "_discordButton") is UnityEngine.Component d && Objects.Get(d) is NBehaviour nb && nb.Go != null) targets.Add((tn + "._discordButton", nb.Go));
                    if (P(m.Managed, "ad") is UnityEngine.Component a && Objects.Get(a) is NBehaviour na && na.Go != null) targets.Add((tn + ".ad", na.Go));
                }
            }
            foreach (var (what, go) in targets)
            {
                Log.Info("[k8links] f" + frame + " " + what + ": '" + PathOf(go.Transform) + "' activeSelf=" + go.ActiveSelf + " inHierarchy=" + go.ActiveInHierarchy + " [" + Comps(go) + "] children: " +
                         string.Join(" | ", go.Transform.Children.Select(c => c.Go.Name + (c.Go.ActiveSelf ? "" : "(off)") + "[" + Comps(c.Go) + "]")));
                if (go.Components.Select(c => c.Managed).OfType<UnityEngine.UI.Selectable>().FirstOrDefault() is { } hs)
                    Log.Info("[k8links]   its navigation: " + hs.navigation.mode + " up=" + hs.navigation.selectOnUp?.name + " down=" + hs.navigation.selectOnDown?.name + " left=" + hs.navigation.selectOnLeft?.name + " right=" + hs.navigation.selectOnRight?.name);
            }
            foreach (var s in UnityEngine.UI.Selectable.allSelectablesArray)
            {
                var n = s.navigation; if (n.mode != UnityEngine.UI.Navigation.Mode.Explicit) continue;
                foreach (var (what, go) in targets)
                    foreach (var (dir, sel) in new[] { ("up", n.selectOnUp), ("down", n.selectOnDown), ("left", n.selectOnLeft), ("right", n.selectOnRight) })
                        if (sel != null && Objects.Get(sel) is NBehaviour ns && ns.Go != null && ns.Go.Transform.IsChildOf(go.Transform))
                            Log.Info("[k8links]   explicit nav: '" + s.name + "' " + dir + " -> '" + sel.name + "' (inside " + what + ")");
            }
            Log.Info("[k8links] f" + frame + " active selectables: " + string.Join(", ", UnityEngine.UI.Selectable.allSelectablesArray.Select(s => s.name + ":" + s.navigation.mode)));
        }
        catch (Exception e) { Log.Info("[k8links] " + e.GetBaseException().Message); }
    }

    /// <summary>SHEPHERD_K8_PADMAP_WATCH=1: every frame whose set of pressed joystick elements (Rewired's view of the virtual DualShock 4)
    /// or of active player-0 actions changed, one line -- the Mac test of the kit #8 table (a pad script pressing select / l2 / r2 / l3 /
    /// r3 / the right stick must show Touchpad Button / L2 / R2 / Left Stick Button / Right Stick Button / Right Stick X|Y and their actions).</summary>
    static readonly bool Watch = Environment.GetEnvironmentVariable("SHEPHERD_K8_PADMAP_WATCH") == "1";
    static string _lastWatch = "";
    static void WatchFrame(int frame)
    {
        if (!Watch) return;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rewired_Core");
            var re = asm?.GetType("Rewired.ReInput");
            if (re == null || re.GetProperty("isReady", S)?.GetValue(null) is not true) return;
            var hits = new List<string>();
            if (P(re.GetProperty("controllers", S)!.GetValue(null), "Joysticks") is System.Collections.IEnumerable js)
                foreach (var j in js.Cast<object>())
                {
                    if (P(j, "Buttons") is System.Collections.IEnumerable bs) foreach (var b in bs.Cast<object>()) if (P(b, "value") is true) hits.Add(P(P(b, "elementIdentifier"), "name") + "");
                    if (P(j, "Axes") is System.Collections.IEnumerable xs) foreach (var x in xs.Cast<object>()) if (P(x, "value") is float v && Math.Abs(v) > 0.3f) hits.Add(P(P(x, "elementIdentifier"), "name") + "=" + v.ToString("0.#"));
                }
            var players = re.GetProperty("players", S)!.GetValue(null);
            var p0 = players.GetType().GetMethod("GetPlayer", new[] { typeof(int) })!.Invoke(players, new object[] { 0 });
            var acts = new List<string>();
            if (P(re.GetProperty("mapping", S)!.GetValue(null), "Actions") is System.Collections.IEnumerable al)
            {
                var gb = p0.GetType().GetMethod("GetButton", new[] { typeof(int) }); var ga = p0.GetType().GetMethod("GetAxis", new[] { typeof(int) });
                foreach (var a in al.Cast<object>())
                {
                    int id = (int)P(a, "id");
                    if (P(a, "type")?.ToString() == "Button") { if (gb?.Invoke(p0, new object[] { id }) is true) acts.Add(P(a, "name") + ""); }
                    else if (ga?.Invoke(p0, new object[] { id }) is float v && Math.Abs(v) > 0.3f) acts.Add(P(a, "name") + "=" + v.ToString("0.#"));
                }
            }
            var line = "elements [" + string.Join(", ", hits) + "] actions [" + string.Join(", ", acts) + "]";
            if (line != _lastWatch) { _lastWatch = line; Log.Info("[k8padmap] f" + frame + " " + line); }
        }
        catch (Exception e) { Log.Info("[k8padmap] watch: " + e.GetBaseException().Message); }
    }

    public static void After(int frame)
    {
        Probe(frame);
        Links(frame);
        WatchFrame(frame);
        if (At.Count == 0 || !At.Contains(frame)) return;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rewired_Core");
            var re = asm?.GetType("Rewired.ReInput");
            if (re == null || re.GetProperty("isReady", S)?.GetValue(null) is not true) { Log.Info("[k8padmap] Rewired not ready at frame " + frame); return; }
            var mapping = re.GetProperty("mapping", S)!.GetValue(null);
            var getAction = mapping.GetType().GetMethod("GetAction", new[] { typeof(int) });
            string An(int id) { var a = getAction?.Invoke(mapping, new object[] { id }); return a == null ? "a" + id : P(a, "name") + "/'" + P(a, "descriptiveName") + "'"; }
            if (P(mapping, "Actions") is System.Collections.IEnumerable acts)
                Log.Info("[k8padmap] actions: " + string.Join(" ", acts.Cast<object>().Select(a => "a" + P(a, "id") + "=" + P(a, "name") + "(cat" + P(a, "categoryId") + "," + P(a, "type") + ")")));
            var players = re.GetProperty("players", S)!.GetValue(null);
            var p0 = players.GetType().GetMethod("GetPlayer", new[] { typeof(int) })!.Invoke(players, new object[] { 0 });
            var maps = P(P(p0, "controllers"), "maps");
            var ct = asm.GetType("Rewired.ControllerType")!;
            foreach (var kind in new[] { "Joystick", "Keyboard" })
            {
                var all = maps.GetType().GetMethods(I).FirstOrDefault(m => m.Name == "GetAllMaps" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == ct)
                    ?.Invoke(maps, new[] { Enum.Parse(ct, kind) }) as System.Collections.IEnumerable;
                if (all == null) { Log.Info("[k8padmap] " + kind + ": no maps"); continue; }
                foreach (var m in all.Cast<object>())
                {
                    var em = P(m, "AllMaps") as System.Collections.IEnumerable;
                    Log.Info("[k8padmap] " + kind + " map cat" + P(m, "categoryId") + " lay" + P(m, "layoutId") + " en=" + P(m, "enabled") + " controller='" + P(P(m, "controller"), "name") + "' hw='" + P(P(m, "controller"), "hardwareName") + "': " +
                             (em == null ? "?" : string.Join(" | ", em.Cast<object>().Select(e => P(e, "elementIdentifierName") + "#" + P(e, "elementIdentifierId") + (kind == "Keyboard" ? "(" + P(e, "keyCode") + ")" : "") +
                                 " " + P(e, "axisContribution") + "/" + P(e, "axisRange") + (P(e, "invert") is true ? " inv" : "") + " -> " + An((int)P(e, "actionId")) + (P(e, "enabled") is false ? " (off)" : "")))));
                }
            }
            // the joystick's element identifiers (what its hardware map calls each button / axis)
            var ctrls = re.GetProperty("controllers", S)!.GetValue(null);
            if (P(ctrls, "Joysticks") is System.Collections.IEnumerable js)
                foreach (var j in js.Cast<object>())
                    if (P(j, "ElementIdentifiers") is System.Collections.IEnumerable ids)
                        Log.Info("[k8padmap] joystick '" + P(j, "name") + "' elements: " + string.Join(" ", ids.Cast<object>().Select(x => P(x, "id") + "=" + P(x, "name"))));
        }
        catch (Exception e) { Log.Info("[k8padmap] " + e.GetBaseException().Message); }
    }
}
#endif
