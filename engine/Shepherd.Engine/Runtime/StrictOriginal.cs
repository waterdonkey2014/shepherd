namespace Shepherd.Engine.Runtime;

/// <summary>Kit #7 tests track (vita/notes/shepherd-charter.md, "the 'strict original' switch"): SHEPHERD_STRICT_ORIGINAL=1 forces every option
/// where Shepherd deliberately behaves unlike the PC original back to the PC behaviour, for test runs (a fidelity A/B: anything that still
/// differs from the original with this on is a fidelity bug, not an option). Applied first thing in Main, before any switch is read: each
/// listed switch is set in the process environment (overriding the env file) and logged once. The list (each the PC's behaviour):
///   SHEPHERD_K6_TOGGLES=0       no PS Vita Settings rows on the Graphics tab (4th CPU Core, CPU Speed, the clock readout) -- the PC has none
///   SHEPHERD_K6_FULLSCREEN=0    the Graphics tab's FullScreen Mode row selectable as on the PC (the kit #6 default greys it, fixed at FullScreen)
///   SHEPHERD_TOUCH=0            the front touch screen is not the game's mouse (the PC's mouse is a mouse; the Vita's touch is an addition)
///   SHEPHERD_UNITY_RANDOM=1     UnityEngine.Random is Unity's own xorshift128 generator (Icalls/UnityRandom.cs; the kit #6 default is
///                               System.Random: a fidelity bug the differential fuzzing found, opt-in until the owner decides)
///   SHEPHERD_K7_SUSPEND=0       (kit #7 integration) a console suspend reaches the game as one long frame clamped by Unity, as a PC stall
///                               does (the kit #7 default, P3, leaves the suspension out of the play clock's frame delta: a Vita behaviour);
///                               the host reads SHEPHERD_STRICT_ORIGINAL=1 itself before Main (k7_host.inc.c k7_config): its P3 watch is off too
///   SHEPHERD_PC_LINKS=1         (kit #8 play) the Main / Pause menus' Discord button and the Main Menu's two ads shown, as on the PC
///   SHEPHERD_K8_REAR=0          (kit #8 play) the rear touch panel is not read (a Vita addition, like touch-as-mouse)
///   SHEPHERD_K8_PACING=0        (kit #8 play) no G3 display lock (the PC's frames come as fast as they are made)
/// Kept as they are (already the original's behaviour, or not an option): SHEPHERD_VIDEO_LENGTHS (the Devolver splash plays its real
/// length), SHEPHERD_K6_STABLE_ORDER (Unity's per-order update lists), the display cap (the panel's one mode is the device), the
/// single-language pack (SHEPHERD_LANGUAGE: memory; the game's own language choice is unchanged within it), every performance switch
/// (identical output by their own gates). Not reachable by a switch: the runtime's float evaluation (Unity's player Mono keeps float
/// intermediates in double precision, Shepherd's Mono 8 / CoreCLR round each float op to float32 -- kit7-tests.md, an AOT compile-time
/// choice for the runtime track). Off (unset / 0): nothing is touched.</summary>
[ProfileOnly]   // (reads no property-bag keys: its literals stay out of the V3 diet's readable-key scan, Assets/Diet.cs)
public static class StrictOriginal
{
    // name, value, why -- flat (no tuple: no new value-type generic instantiation in the full-AOT eboots)
    static readonly string[] Pc =
    {
        "SHEPHERD_K6_TOGGLES", "0", "no PS Vita Settings rows",
        "SHEPHERD_K6_FULLSCREEN", "0", "FullScreen Mode selectable as on the PC",
        "SHEPHERD_TOUCH", "0", "the touch screen is not the mouse",
        "SHEPHERD_UNITY_RANDOM", "1", "Unity's own Random generator",
        "SHEPHERD_K7_SUSPEND", "0", "a suspension is one clamped frame, as a PC stall",
        "SHEPHERD_PC_LINKS", "1", "the Discord button and the menu ads shown",
        "SHEPHERD_K8_REAR", "0", "the rear touch panel is not L2 / R2 / L3 / R3",
        "SHEPHERD_K8_PACING", "0", "frames as fast as they come, no display lock",
    };
    public static string Applied;
    /// <summary>Program.Main, first statement (Log is not open yet: the lines are kept for <see cref="Report"/>).</summary>
    public static void Apply()
    {
        if (Environment.GetEnvironmentVariable("SHEPHERD_STRICT_ORIGINAL") != "1") return;
        var sb = new System.Text.StringBuilder("[strict] SHEPHERD_STRICT_ORIGINAL=1: the PC original's behaviour forced for this test run:");
        for (int i = 0; i + 2 < Pc.Length; i += 3)
        {
            string name = Pc[i], value = Pc[i + 1], why = Pc[i + 2];
            var was = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            sb.Append(' ').Append(name).Append('=').Append(value).Append(" (").Append(why).Append(was != null && was != value ? "; the env file said " + was : "").Append(");");
        }
        Applied = sb.ToString();
    }
    /// <summary>Boot.Run, once the log is open.</summary>
    public static void Report() { if (Applied != null) Log.Info(Applied); }
}
