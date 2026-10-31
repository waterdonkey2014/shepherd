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
///   SHEPHERD_K8_LATCH=0         (kit #10 fidelity, missed in kit #8) the controls read at the frame's start, as the PC's input update does (the
///                               kit #8 default reads them late in the frame for less input lag: a Vita behaviour)
///   SHEPHERD_K10_ROWS=0         (kit #10 cache) no Cache Size / Cache In Use / Clear Cache / Verify Files rows (PS Vita rows; also gone with
///                               SHEPHERD_K6_TOGGLES=0 -- listed so the strict line names every Vita row switch)
///   SHEPHERD_K9_FLOATPC=1       (kit #10 fidelity) the PC's float evaluation (kit #9's FloatPc-rewritten game DLLs) where the card holds
///                               managed-floatpc/ (none: a WARN line, unchanged); the host reads SHEPHERD_STRICT_ORIGINAL=1 itself before
///                               Main (host.c k9_floatpc_config): the assemblies load before this runs
///   SHEPHERD_K10_FPSCR=pc       (kit #10 fidelity) the PC's float rules on every thread the game's code runs on: FZ 0 (denormals kept), DN 0
///                               (NaN payloads kept), round to nearest -- the Vita starts every thread with FZ 1 DN 1; the host reads
///                               SHEPHERD_STRICT_ORIGINAL=1 itself (k10_fp.inc.c k10_fp_config): the threads exist before Main
///   SHEPHERD_K10_BOOTSCREEN=0   (kit #10 integration: the boot track's loading screen during the engine boot) no loading screen before the
///                               game's first frame -- the PC shows none; the host draws it before Main, so it reads SHEPHERD_STRICT_ORIGINAL=1
///                               itself (k10_boot.inc.c k10_bootscreen_start: strict = not shown, whatever the env file says)
/// Kit #10 fidelity review fix -- the switch back to kit #9's strict run: SHEPHERD_K10_STRICT=0 applies kit #9's list only (the eight kit #7 / #8
/// entries above; the host then leaves FloatPc and the float rules out too). The two host-read entries (SHEPHERD_K9_FLOATPC, SHEPHERD_K10_FPSCR)
/// yield to an explicit line in the env file (0 included -- e.g. strict on a game-AOT app whose card holds managed-floatpc/, keeping the owner
/// modules registered): the host followed that line before Main, so it is kept and logged, not overwritten.
/// Kept as they are (already the original's behaviour, or not an option): SHEPHERD_VIDEO_LENGTHS (the Devolver splash plays its real
/// length), SHEPHERD_K6_STABLE_ORDER (Unity's per-order update lists), the display cap (the panel's one mode is the device), the
/// single-language pack (SHEPHERD_LANGUAGE: memory; the game's own language choice is unchanged within it), every performance switch
/// (identical output by their own gates). The runtime's float evaluation (Unity's player Mono keeps float intermediates in double precision,
/// Shepherd's Mono 8 / CoreCLR round each float op to float32 -- kit7-tests.md) is reachable only through kit #9's IL rewrite (the
/// SHEPHERD_K9_FLOATPC folder, above). Off (unset / 0): nothing is touched.</summary>
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
        "SHEPHERD_K8_LATCH", "0", "the controls read at the frame's start, as the PC's input update",
        "SHEPHERD_K10_ROWS", "0", "no Cache Size / Clear Cache / Verify Files rows",
        "SHEPHERD_K9_FLOATPC", "1", "the PC's float evaluation where the card holds managed-floatpc/ (the host read it before Main)",
        "SHEPHERD_K10_FPSCR", "pc", "the PC's float rules on every thread: FZ 0 DN 0 (the host set them before Main)",
        "SHEPHERD_K10_BOOTSCREEN", "0", "no loading screen during the engine boot, as the PC (the host read strict before Main)",
    };
    const int K9Entries = 8;   // the kit #7 / #8 entries = kit #9's strict list (SHEPHERD_K10_STRICT=0 stops here)
    public static string Applied;
    /// <summary>Program.Main, first statement (Log is not open yet: the lines are kept for <see cref="Report"/>).</summary>
    public static void Apply()
    {
        if (Environment.GetEnvironmentVariable("SHEPHERD_STRICT_ORIGINAL") != "1") return;
        var sb = new System.Text.StringBuilder("[strict] SHEPHERD_STRICT_ORIGINAL=1: the PC original's behaviour forced for this test run:");
        bool k9List = Environment.GetEnvironmentVariable("SHEPHERD_K10_STRICT") == "0";
        int end = k9List ? K9Entries * 3 : Pc.Length;
        for (int i = 0; i + 2 < end; i += 3)
        {
            string name = Pc[i], value = Pc[i + 1], why = Pc[i + 2];
            var was = Environment.GetEnvironmentVariable(name);
            if ((name == "SHEPHERD_K9_FLOATPC" || name == "SHEPHERD_K10_FPSCR") && !string.IsNullOrEmpty(was))
            {   // (host-read before Main: the env file's own line won there -- kept, not overwritten)
                sb.Append(' ').Append(name).Append('=').Append(was).Append(" (kept: the env file's line wins over strict's ").Append(value).Append(");");
                continue;
            }
            Environment.SetEnvironmentVariable(name, value);
            sb.Append(' ').Append(name).Append('=').Append(value).Append(" (").Append(why).Append(was != null && was != value ? "; the env file said " + was : "").Append(");");
        }
        if (k9List) sb.Append(" SHEPHERD_K10_STRICT=0: kit #9's strict list (the kit #10 entries are not applied).");
        Applied = sb.ToString();
    }
    /// <summary>Boot.Run, once the log is open.</summary>
    public static void Report() { if (Applied != null) Log.Info(Applied); }
}
