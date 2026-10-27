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
        public At(int n, bool rel) { N = n; Rel = rel; }
        /// <summary>The absolute frame, or int.MaxValue while a relative one is not known yet.</summary>
        public int Frame => !Rel ? N : MenuFrame >= 0 ? MenuFrame + N : int.MaxValue;
        public override string ToString() => Rel ? $"m+{N}" : N.ToString();
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
