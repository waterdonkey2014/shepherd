using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #12 boot track (vita/notes/kit12-boot.md section 3): the recommended plugins' detection, for the PS Vita Settings info row (the
/// menusb track builds the row, its class Runtime.K12Plugins; boot owns the detection). The host detects READ-ONLY (vita/shepherd/
/// k12_boot.inc.c): taiHEN's config (ux0:tai/config.txt when it exists, else ur0:tai/config.txt), the 4th core (its C3 probe), the ARM clock (the
/// system's report or kit #7's measured step). Owner 2026-10-30: CapUnlocker, PSVshellPlus, and Wi-Fi Turbo "for online play"; names only (no
/// logos: none is official, a logo would need its author's OK). <see cref="Read"/> answers null off the Vita GL host (the Mac, 00011).
/// THE SEAM agreed with menusb (its notes "for-boot"): once at boot, after the taiHEN config read, boot calls K12Plugins.Set(bits) -- bit 0
/// CapUnlocker, bit 1 PSVshellPlus, bit 2 Wi-Fi Turbo installed, bit 8 the config was read. <see cref="Handoff"/> does it on the first frame,
/// by name (the row's class lives in menusb's branch: in this branch there is none and the handoff logs that once; merged, it finds the row).
/// (This class was K12Plugins until the seam was agreed: renamed so the two tracks' classes do not collide.)
/// Switches: SHEPHERD_K12_PLUGINS_SET=1 = the handoff (default OFF -- review fix: the bits it hands over rest on the host's clock / 4th-core
/// detection, PSVshellPlus at >= 480 MHz here vs >= 490 in menusb's own fallback, and kit #11's measured clock / 4th-core lines decide what
/// "missing" means: the same wait as the loading screen's names, so a visible Settings row is not fed before them; off = no read, no call,
/// menusb's row shows its own fallback); SHEPHERD_K12_BOOT=0 = every kit #12 boot row off (kit #11: no read, no call).</summary>
[ProfileOnly]
public static unsafe class K12PluginDetect
{
    public static readonly string[] Names = { "CapUnlocker", "PSVshellPlus", "Wi-Fi Turbo" };
    /// <summary>What each one does, in a plain sentence (the info row's text; English, like every PS Vita row).</summary>
    public static readonly string[] Does =
    {
        "Lets the game use the Vita's fourth CPU core for its background work.",
        "Lets the game run the CPU at 500 MHz.",
        "Faster Wi-Fi, for online play.",
    };
    public sealed class Info
    {
        public bool[] Present = new bool[3];
        public int ArmMhz, Core3;   // Core3: 1 open, 0 closed, -1 not probed
        public bool ConfigRead;
        /// <summary>menusb's K12Plugins.Set value: bits 0-2 installed (CapUnlocker, PSVshellPlus, Wi-Fi Turbo), bit 8 the config was read.</summary>
        public int Bits => (Present[0] ? 1 : 0) | (Present[1] ? 2 : 0) | (Present[2] ? 4 : 0) | (ConfigRead ? 256 : 0);
        public override string ToString() => string.Join(", ", Names.Select((n, i) => $"{n} {(Present[i] ? "installed" : "missing")}")) + $"; ARM {ArmMhz} MHz; 4th core {(Core3 > 0 ? "open" : Core3 == 0 ? "closed" : "not probed")}; taiHEN config {(ConfigRead ? "read" : "not readable")}";
    }
    /// <summary>The host's detection now (recomputed per call; the config is read once a start), or null off the Vita GL host.</summary>
    public static Info Read()
    {
        if (!Profile.Vita || !Render.ShepGl.On || !Diag.VitaHost.On) return null;
        try
        {
            var v = new int[6];
            fixed (int* p = v) if (Host(p, 6) < 6) return null;
            return new Info { Present = new[] { v[0] != 0, v[1] != 0, v[2] != 0 }, ArmMhz = v[3], Core3 = v[4], ConfigRead = v[5] != 0 };
        }
        catch (Exception) { return null; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Host(int* p, int n) => shepherd_vita_k12_plugins(p, n);
    [DllImport("__Internal")] static extern int shepherd_vita_k12_plugins(int* info, int n);

    /// <summary>The handoff is still to do (Boot.cs checks this field each frame; false at once off the Vita GL host or with the switch off --
    /// default off until kit #11's run settles the thresholds).</summary>
    public static bool Pending = K12Boot.Row("SHEPHERD_K12_PLUGINS_SET", false) && Profile.Vita && Render.ShepGl.On && Diag.VitaHost.On;
    /// <summary>Once, on the first frame: the detection handed to menusb's row (K12Plugins.Set(int)), found by name.</summary>
    public static void Handoff(int frame)
    {
        Pending = false;
        try
        {
            var info = Read();
            if (info == null) { Log.Info("[k12] plugins: the host gave no detection: nothing handed to the Settings row"); return; }
            var row = typeof(K12PluginDetect).Assembly.GetType("Shepherd.Engine.Runtime.K12Plugins");
            var set = row?.GetMethod("Set", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (set != null) set.Invoke(null, new object[] { info.Bits });
            Log.Info($"[k12] plugins: f{frame} {info} -> bits 0x{info.Bits:x}" + (set != null ? " handed to the Settings row (K12Plugins.Set)" : " (no Settings row in this build: menusb's K12Plugins.Set not present)"));
        }
        catch (Exception e) { Log.Warn("[k12] plugins: the handoff failed: " + e.GetBaseException().Message); }
    }
}
