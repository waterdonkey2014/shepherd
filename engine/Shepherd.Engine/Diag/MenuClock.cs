using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Diag;

/// <summary>V6 stage 2: frame numbers relative to the Main Menu scene's arrival, for the headless measurement runs.
/// The boot's scene loads are paced by wall time (the game waits on Time.realtimeSinceStartup), so the frame at which the Main
/// Menu scene lands depends on how fast the frames are: 814 on the interpreter before stage 2, ~1120 after it, ~889 on CoreCLR.
/// A pad script or a measurement window at fixed frames then lands on different screens. "m+N" means N frames after the first
/// frame that ended with the Main Menu scene present; plain numbers stay absolute. Accepted by SHEPHERD_PAD / SHEPHERD_PAD_FILE,
/// SHEPHERD_MENU_PROBE, SHEPHERD_SIMSTATS, and SHEPHERD_STOP_AT=&lt;frame&gt; (end the boot after that frame). The baseline's
/// 1050 / 1300 / 1301..1400 / 1450 are m+236 / m+486 / m+487..m+586 / m+636 (vita/v6/pad-menu-dlc-rel.txt).</summary>
[ProfileOnly]
public static class MenuClock
{
    public readonly struct At
    {
        public readonly int N; public readonly bool Rel;
        /// <summary>Kit #13 bootc: a SCREEN anchor ("@SaveSlots+30": N frames after the game first showed that screen), null = N / m+N.</summary>
        public readonly string Screen;
        public At(int n, bool rel) { N = n; Rel = rel; Screen = null; }
        public At(int n, string screen) { N = n; Rel = false; Screen = screen; }
        /// <summary>The absolute frame, or int.MaxValue while a relative one (or a screen not shown yet) is not known yet.</summary>
        public int Frame => Screen != null ? (ScreenFrame(Screen) is var sf && sf >= 0 ? sf + N : int.MaxValue) : !Rel ? N : MenuFrame >= 0 ? MenuFrame + N : int.MaxValue;
        public override string ToString() => Screen != null ? $"@{Screen}+{N}" : Rel ? $"m+{N}" : N.ToString();
    }
    /// <summary>Device kit #13, track bootc (vita/notes/kit13-bootc.md section 6): SCREEN-STATE WAITS for scripted pads. Kit #12's device A/B pads pressed at
    /// fixed frames after the Main Menu scene; in 2 of 4 cleanup starts the slot press found another screen (the game's DLC slots: the menu's
    /// selection had moved) and the owner pressed by hand. The game announces each menu it shows through its own automation hook, a Debug.Log
    /// line "AutomationClient recived:&lt;menu&gt;_SHOWN" (Main Menu Controller, SaveSlots, DLCSlots ... on every device log); a pad item may now name
    /// that screen: "@SaveSlots+30:cross" presses 30 frames after the game first showed SaveSlots ("@Name" = +0; the name without its spaces, case
    /// ignored: @MainMenuController). The pad and touch scripts take it (ParseAt; review fix: the touch script's parser now does too, and an inline
    /// SHEPHERD_PAD / SHEPHERD_TOUCH_SCRIPT that starts with one is a script, not "@file"); a script without '@' items reads nothing here (one bool).</summary>
    static bool _screensNeeded;
    static readonly Dictionary<string, int> _shown = new(StringComparer.OrdinalIgnoreCase);
    public static int ScreenFrame(string name) => _shown.TryGetValue(name, out var f) ? f : -1;
    /// <summary>The pad / touch scripts' frame: N, m+N, or (kit #13) @Screen[+N].</summary>
    public static bool ParseAt(string s, out At at)
    {
        s = s?.Trim();
        if (s is { Length: > 1 } && s[0] == '@')
        {
            at = default;
            if (!ScreenForm(s, out var name, out var n)) return false;
            _screensNeeded = true; at = new At(n, name); return true;
        }
        return Parse(s, out at);
    }
    /// <summary>Kit #13 bootc review fix: SHEPHERD_PAD / SHEPHERD_TOUCH_SCRIPT = "@&lt;path&gt;" names a file, but an inline script may now start with a
    /// screen wait ("@SaveSlots+30:cross,..."): true when the first item is such a pad item (pad: '@Name[+N]:button' with a button name; touch:
    /// '@Name[+N]:tap|touch|drag:...'), so the value is read as the script itself. A path never parses so ("ux0:data/..." has no button after
    /// its first ':').</summary>
    public static bool InlineScreenItem(string v, bool pad)
    {
        var items = v.Split(pad ? new[] { ',' } : new[] { ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var p = items.Length > 0 ? items[0].Trim().Split(':') : null;
        if (p == null || p.Length < 2 || !ScreenForm(p[0], out _, out _)) return false;   // (no side effect: ParseAt marks the screens as needed)
        return pad ? Enum.TryParse<Impl.VitaPad.B>(p[1], true, out _) : p[1].ToLowerInvariant() is "tap" or "touch" or "drag";
    }
    static bool ScreenForm(string s, out string name, out int n)
    {
        name = null; n = 0; s = s?.Trim();
        if (s is not { Length: > 1 } || s[0] != '@') return false;
        int plus = s.IndexOf('+'); name = plus < 0 ? s[1..] : s[1..plus];
        return name.Length > 0 && (plus < 0 || int.TryParse(s[(plus + 1)..], out n)) && n >= 0;
    }
    /// <summary>Every game Debug.Log line (Icalls/AppIcalls.cs): the screens the game announces, first frame each.</summary>
    public static void OnGameLog(string msg)
    {
        if (!_screensNeeded || msg == null || !msg.StartsWith("AutomationClient recived:", StringComparison.Ordinal) || !msg.EndsWith("_SHOWN", StringComparison.Ordinal)) return;
        var name = msg.Substring(25, msg.Length - 25 - 6).Replace(" ", "");
        if (name.Length == 0 || _shown.ContainsKey(name)) return;
        _shown[name] = Time.FrameCount;
        Log.Info($"[menuclock] f{Time.FrameCount} the game showed '{name}' (@{name}: pad items waiting for it are due from here)");
    }
    public static int MenuFrame = -1;
    // The clock switches (SHEPHERD_TIME=wall|frame, the Stage B alias SHEPHERD_CLOCK=wall, SHEPHERD_REALTIME=wall) moved to
    // Runtime/PlayClock.cs (kit3 play mode). With the wall clock pad script frames land wherever the device's speed puts them: use m+N.
    static bool _needed;
    static readonly At Stop = Parse(Environment.GetEnvironmentVariable("SHEPHERD_STOP_AT"), out var s) ? s : new At(-1, false);

    public static bool Parse(string s, out At at)
    {
        at = default; s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        bool rel = s.StartsWith("m+", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(rel ? s[2..] : s, out var n)) return false;
        if (rel) _needed = true;
        at = new At(n, rel); return true;
    }

    /// <summary>After every boot frame (before the probes that use it).</summary>
    public static void After(int frame)
    {
        if ((_needed || Stop.N >= 0) && MenuFrame < 0 && Scenes.All.Values.Any(x => x.Name == "Main Menu")) { MenuFrame = frame; Log.Info($"[menuclock] Main Menu scene present at frame {frame} (m+0)" + (Time.Wall ? $", wall clock: Time.time {Time.Now:F2} s, realtimeSinceStartup {Time.Realtime:F2} s" : "")); }
        if (Stop.N >= 0 && frame >= Stop.Frame) { Log.Info($"[menuclock] SHEPHERD_STOP_AT={Stop}: stopping after frame {frame}"); Boot.QuitRequested = true; }
    }
}
