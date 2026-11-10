namespace Shepherd.Engine.Runtime;

/// <summary>Kit #12 boot track (vita/notes/kit12-boot.md): the engine half's switches. SHEPHERD_K12_BOOT=0 is the track's master: every kit #12
/// boot row off = kit #11's behaviour (the host reads the same line for its rows: k12_boot.inc.c). Each row has its own env-file line too; a row
/// is on only when the master is on and its own line (or its default) says so. Every default here is OFF in this kit (the kit is built before
/// kit #11's device run: a default that waits for a device number or an owner A/B stays off -- kit12-boot.md "Waits on the kit #11 run"), the
/// plugins' handoff to menusb's Settings row included (review fix: its bits rest on the clock / 4th-core detection whose thresholds wait on kit
/// #11's measured lines; SHEPHERD_K12_PLUGINS_SET=1 turns it on).</summary>
[ProfileOnly]
public static class K12Boot
{
    /// <summary>SHEPHERD_K12_BOOT: unset / 1 = the rows as their own lines say; 0 = every kit #12 boot row off (kit #11).</summary>
    public static readonly bool Master = Environment.GetEnvironmentVariable("SHEPHERD_K12_BOOT") is not { } m || m.Trim() != "0";
    /// <summary>A row: the master on and its env line "1" (or unset with <paramref name="dflt"/> true); "0" turns it off.</summary>
    public static bool Row(string name, bool dflt)
    {
        if (!Master) return false;
        var v = Environment.GetEnvironmentVariable(name);
        return v is { Length: > 0 } ? v.Trim() == "1" : dflt;
    }
}
