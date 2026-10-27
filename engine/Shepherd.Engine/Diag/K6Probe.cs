using System.Reflection;
using Shepherd.Engine.Native;
using UObject = UnityEngine.Object;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>Kit #6 engine track, a test driver (off unless set; not in the eboots): SHEPHERD_K6_PROBE="&lt;frame&gt;:&lt;action&gt;[;...]" runs
/// game code at a frame, by reflection only (no game type is compiled in), so the intro's later stages can be reached without the
/// Lamb walking (physics is off in this track's runs). Actions:
///   call &lt;Type&gt;.&lt;Method&gt;   -- a parameterless method on the first live object of that type (inactive ones too), or a static one
///   hit                      -- Health.DealDamage(1, &lt;an enemy swordsman&gt;, its position) on the player (PlayerFarming.Instance):
///                               what an enemy's first hit on the Lamb does (the dodge tutorial on a new save)
///   watch &lt;n&gt;                -- every frame for n frames: Time.timeScale, MMTransition.IsPlaying, the active scene, the player
///   state                    -- one such line now
/// Frame numbers may be m+N (after the Main Menu scene's arrival) as in the pad script.</summary>
[ProfileOnly]
public static class K6Probe
{
    static readonly List<(string at, string action)> Items = Parse(Environment.GetEnvironmentVariable("SHEPHERD_K6_PROBE"));
    static int _watchUntil = -1;
    static List<(string, string)> Parse(string s)
    {
        var l = new List<(string, string)>();
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int c = part.IndexOf(':'); if (c > 0) l.Add((part[..c].Trim(), part[(c + 1)..].Trim()));
        }
        return l;
    }
    static int FrameOf(string at) => at.StartsWith("m+") ? (MenuClock.MenuFrame >= 0 && int.TryParse(at[2..], out var m) ? MenuClock.MenuFrame + m : -1) : int.TryParse(at, out var f) ? f : -1;

    public static void After(int frame)
    {
        if (Items.Count == 0) return;
        foreach (var (at, action) in Items)
            if (FrameOf(at) == frame)
                try { Run(frame, action); } catch (Exception e) { Log.Exception(e, "[k6probe] " + action); }
        if (frame <= _watchUntil) State(frame);
    }

    static Type FindType(string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(name, false); if (t != null) return t;
            if (a.GetName().Name == "Assembly-CSharp") foreach (var x in a.GetTypes()) if (x.Name == name) return x;
        }
        return null;
    }
    static object FirstOf(Type t)
    {
        foreach (var n in Objects.All.ToArray()) if (!n.Destroyed && n.Managed != null && t.IsInstanceOfType(n.Managed)) return n.Managed;
        return null;
    }
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    static void Run(int frame, string action)
    {
        var w = action.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (w[0])
        {
            case "call":
            {
                int dot = w[1].LastIndexOf('.'); var t = FindType(w[1][..dot]); var name = w[1][(dot + 1)..];
                var m = t?.GetMethod(name, Any, null, Type.EmptyTypes, null);
                if (m == null) { Log.Warn($"[k6probe] f{frame}: no method {w[1]}"); return; }
                var target = m.IsStatic ? null : FirstOf(t);
                if (!m.IsStatic && target == null) { Log.Warn($"[k6probe] f{frame}: no live {t.Name}"); return; }
                Log.Info($"[k6probe] f{frame}: call {w[1]}" + (target is UObject u ? $" on '{u.name}'" : ""));
                m.Invoke(target, null);
                break;
            }
            case "hit": Hit(frame); break;
            case "watch": _watchUntil = frame + (w.Length > 1 && int.TryParse(w[1], out var n) ? n : 60); State(frame); break;
            case "state": State(frame); break;
            case "ui": Ui(frame, w.Length > 1 ? w[1] : ""); break;
            case "mesh": Mesh(frame, w.Length > 1 ? w[1] : ""); break;
            case "tex": Tex(frame, w.Length > 1 ? w[1] : ""); break;
            case "objs": Objs(frame, w.Length > 1 ? w[1] : ""); break;
            default: Log.Warn($"[k6probe] f{frame}: unknown action '{action}'"); break;
        }
    }

    /// <summary>The player's Health.DealDamage from an enemy swordsman (the intro's first fight): Health's own rule then shows the dodge
    /// tutorial on a save that has not shown it (UIDodgePromptTutorial.Play with that attacker).</summary>
    static void Hit(int frame)
    {
        var pf = FindType("PlayerFarming"); var inst = pf?.GetField("Instance", Any)?.GetValue(null) ?? pf?.GetProperty("Instance", Any)?.GetValue(null);
        if (inst is not UnityEngine.Component player) { Log.Warn($"[k6probe] f{frame}: hit: no PlayerFarming.Instance"); return; }
        var es = FindType("EnemySwordsman"); var enemy = es == null ? null : FirstOf(es) as UnityEngine.Component;
        if (enemy == null) { Log.Warn($"[k6probe] f{frame}: hit: no EnemySwordsman"); return; }
        var ht = FindType("Health"); var health = player.GetComponent(ht);
        var deal = ht.GetMethods(BindingFlags.Instance | BindingFlags.Public).First(m => m.Name == "DealDamage" && m.GetParameters().Length == 7);
        var args = deal.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        args[0] = 1f; args[1] = enemy.gameObject; args[2] = enemy.transform.position;
        Log.Info($"[k6probe] f{frame}: hit: '{player.name}' Health.DealDamage(1, '{enemy.gameObject.name}') ...");
        var r = deal.Invoke(health, args);
        Log.Info($"[k6probe] f{frame}: hit: DealDamage returned {r}; Time.timeScale {UnityEngine.Time.timeScale}");
    }

    /// <summary>"ui &lt;root canvas name&gt;": every CanvasRenderer under it (active ones), its size, colour, alpha, texture and material (shader,
    /// keywords, properties by id -> name where the shader id table knows it).</summary>
    static void Ui(int frame, string canvas)
    {
        foreach (var cv in Objects.All.OfType<NBehaviour>().Where(b => b.Managed is UnityEngine.Canvas && !b.Destroyed && b.Go?.Name == canvas).ToList())
            foreach (var g in Runtime.Scenes.Walk(cv.Go))
            {
                if (!g.ActiveInHierarchy) continue;
                foreach (var c in g.Components.OfType<NCanvasRenderer>())
                {
                    var t = g.Transform; var mat = c.Materials.Length > 0 ? c.Materials[0] : null;
                    var props = mat == null ? "" : string.Join(" ", mat.P.Select(kv => $"{Impl.ShaderIds.Name(kv.Key) ?? kv.Key.ToString()}={Fmt(kv.Value)}"));
                    Log.Info($"[k6probe] f{frame}: ui '{canvas}' {PathOf(t)} size={t.Size} tris={c.Idx.Length / 3} color={c.Color} alpha={Impl.RenderIcalls.UiAlpha(g):F2} tex={c.Texture?.Name}({c.Texture?.Width}x{c.Texture?.Height}) " +
                             $"mat='{mat?.Name}' shader={mat?.ShaderName} kw=[{(mat == null ? "" : string.Join(",", mat.Keywords))}] {props}");
                }
            }
    }
    /// <summary>"mesh &lt;GameObject name&gt;": the CanvasRenderer mesh of every active object of that name (positions, colours, uv0).</summary>
    static void Mesh(int frame, string go)
    {
        foreach (var c in Objects.All.OfType<NCanvasRenderer>().Where(c => !c.Destroyed && c.Go != null && c.Go.Name == go && c.Go.ActiveInHierarchy).ToList())
            for (int i = 0; i < c.Pos.Length; i++)
                Log.Info($"[k6probe] f{frame}: mesh '{PathOf(c.Go.Transform)}' v{i} pos={c.Pos[i]} col={(c.Col != null && i < c.Col.Length ? c.Col[i].ToString() : "-")} uv0={(c.Uv0 != null && i < c.Uv0.Length ? c.Uv0[i].ToString() : "-")}");
    }
    /// <summary>"tex &lt;texture name&gt;": size and the first row of pixels (RGBA hex), when the texture has CPU pixels.</summary>
    static void Tex(int frame, string name)
    {
        foreach (var t in Objects.All.OfType<NTexture>().Where(t => !t.Destroyed && t.Name == name).ToList())
        {
            Impl.RenderIcalls.LoadPixels?.Invoke(t);
            var px = t.Rgba; int n = px == null ? 0 : Math.Min(t.Width, 256);
            Log.Info($"[k6probe] f{frame}: tex '{name}' {t.Width}x{t.Height} row0 " + (px == null ? "(no CPU pixels)" : string.Join(" ", Enumerable.Range(0, n).Select(i => $"{px[i * 4]:x2}{px[i * 4 + 1]:x2}{px[i * 4 + 2]:x2}{px[i * 4 + 3]:x2}"))));
        }
    }
    /// <summary>"objs &lt;Type&gt;": every live behaviour of that type: its object path, active/enabled, world position, and its serialized fields.</summary>
    static void Objs(int frame, string type)
    {
        var t = FindType(type); if (t == null) { Log.Warn($"[k6probe] f{frame}: no type {type}"); return; }
        foreach (var n in Objects.All.ToArray())
        {
            if (n.Destroyed || n.Managed == null || !t.IsInstanceOfType(n.Managed) || n is not NComponent c || c.Go == null) continue;
            var fields = string.Join(" ", t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => { object v; try { v = f.GetValue(n.Managed); } catch { v = "?"; } return $"{f.Name}={Fmt(v)}"; }));
            Log.Info($"[k6probe] f{frame}: {type} '{PathOf(c.Go.Transform)}' scene {c.Go.Scene} active={c.Go.ActiveInHierarchy} enabled={(n as NBehaviour)?.Enabled} pos={c.Go.Transform.Position} {fields}");
        }
    }
    static string PathOf(NTransform t) => t.Parent == null ? t.Go.Name : PathOf(t.Parent) + "/" + t.Go.Name;
    static string Fmt(object v) => v switch { UObject u => $"<{u.GetType().Name} '{u.name}'>", float f => f.ToString("0.###"), _ => v?.ToString() };

    static void State(int frame)
    {
        var mmt = FindType("MMTools.MMTransition"); var pf = FindType("PlayerFarming");
        var inst = pf?.GetField("Instance", Any)?.GetValue(null) ?? pf?.GetProperty("Instance", Any)?.GetValue(null);
        Log.Info($"[k6probe] f{frame}: timeScale {UnityEngine.Time.timeScale} MMTransition.IsPlaying {mmt?.GetField("IsPlaying", Any)?.GetValue(null)} " +
                 $"scene '{Runtime.Scenes.Active.Name}' player {(inst is UObject u && u != null ? "'" + u.name + "'" : "none")}");
    }
}
#else
[ProfileOnly]
public static class K6Probe
{
    public static void After(int frame)
    {
        if (frame == 1 && Environment.GetEnvironmentVariable("SHEPHERD_K6_PROBE") is { Length: > 0 }) Log.Warn("[k6probe] SHEPHERD_K6_PROBE ignored: the test driver is not in this eboot (SHEPHERD_ENGINE_TOOLS=1 builds keep it)");
    }
}
#endif
