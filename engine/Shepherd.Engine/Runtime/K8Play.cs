using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #8, track play (vita/notes/kit8-play.md): the play track's switches and its small engine-side parts. Every part has an
/// env-file switch back to kit #7; <c>SHEPHERD_K8_PLAY=0</c> turns the whole track off (with every part's own switch). Vita profile only.
/// <list type="bullet">
/// <item><b>Hide links that can't work on the Vita</b> (owner 2026-10-21, "for now"): the Main Menu's and the Pause Menu's "Join Discord" button
/// (Application.OpenURL), MenuAdController's ad (OpenURL; its ads come from the web) and MenuDLCAdController's Woolhaven block (the Steam store
/// overlay) are made inactive right after the Awake of the controller that owns them -- invisible, and skipped by the game's navigation (all
/// four are Automatic-navigation Selectables: an inactive Selectable is out of Unity's Selectable list, so no neighbour can land on it). It
/// is the game's own way: the console builds hide the Discord button and the web ad with exactly this, SetActive(false) in
/// disablePerPlatform.Awake (disableOnConsole -- compiled out of the PC build). The Main Menu's "Ads" row itself stays active (the game
/// re-activates it every frame for English); its two cards are what is hidden. <c>SHEPHERD_PC_LINKS=1</c> shows them as the PC game does
/// (kit #7); so does <c>SHEPHERD_STRICT_ORIGINAL=1</c>.</item>
/// <item><b>Controls parity</b> (Icalls/VitaPad.cs): the game's own DualShock 4 map, completed -- Select = the touchpad button (the game's
/// 'Menu'), the right stick, L2 / R2 / L3 / R3 from the rear touch corners (the curse = R2, bleat + fleece ability = R3, track quest = L3).
/// <c>SHEPHERD_K8_PAD=0</c>: kit #7's table. Settings > Graphics > Rear Touch (On by default) turns the rear panel off.</item>
/// <item><b>Late input latch</b> (Icalls/VitaPad.cs): the Vita's controls read at the frame's first input query. <c>SHEPHERD_K8_LATCH=0</c>.</item>
/// <item><b>The measured clock</b> (host k8_play.inc.c): the CPU Clock readout and the performance overlay show the clock the watchdog
/// measured. <c>SHEPHERD_K8_CLK_MEASURED=0</c>.</item>
/// <item><b>G3 display-locked pacing</b> (<see cref="FrameStart"/>; host k8_pace): on the play clock each frame starts on the display's grid of
/// N vblanks, N = 2 / 3 / 4 (30 / 20 / 15 fps) chosen per area (the active scene) from that area's own frame times, so the game's
/// deltaTime equals the time each frame is on screen. Settings > Graphics > Frame Pacing, OFF by default (the owner's choice on the device:
/// at today's frame rates a lock trades frames for evenness -- see kit8-play.md); a frame-time histogram is logged either way.
/// <c>SHEPHERD_K8_PACING=0</c>: no row, never locked (kit #7).</item>
/// </list>
/// Code size: log text by concatenation (an interpolated string inlines its handler in the AOT eboots); game types by name through reflection.</summary>
[ProfileOnly]   // log text and switch names only: no property-bag key is read here (Assets/Diet.cs)
public static class K8Play
{
    /// <summary>The track's master switch (on by default under the Vita profile).</summary>
    public static readonly bool On = Profile.Vita && Environment.GetEnvironmentVariable("SHEPHERD_K8_PLAY") != "0";
    /// <summary>Hide the PC-only link elements (default on; <c>SHEPHERD_PC_LINKS=1</c> shows them, as kit #7).</summary>
    public static readonly bool HideLinks = On && Environment.GetEnvironmentVariable("SHEPHERD_PC_LINKS") != "1";
    /// <summary>Controls parity: the completed DualShock 4 table, the right stick, the rear touch corners (Icalls/VitaPad.cs).</summary>
    public static readonly bool PadMap = On && Environment.GetEnvironmentVariable("SHEPHERD_K8_PAD") != "0";
    /// <summary>The rear touch panel's corners as L2 / R2 / L3 / R3 (with <see cref="PadMap"/>; the Rear Touch row turns it off for a session;
    /// <c>SHEPHERD_K8_REAR=0</c>: never read, no row -- also under SHEPHERD_STRICT_ORIGINAL=1, a Vita addition like touch-as-mouse).</summary>
    public static readonly bool Rear = PadMap && Environment.GetEnvironmentVariable("SHEPHERD_K8_REAR") != "0";
    /// <summary>The late input latch (Icalls/VitaPad.cs).</summary>
    public static readonly bool Latch = On && Environment.GetEnvironmentVariable("SHEPHERD_K8_LATCH") != "0";
    /// <summary>The overlay shows the measured ARM clock (the readout row: the host decides, k8_play.inc.c).</summary>
    public static readonly bool ClockMeasured = On && Environment.GetEnvironmentVariable("SHEPHERD_K8_CLK_MEASURED") != "0";
    /// <summary>G3 frame pacing is available (the Frame Pacing row, Off by default).</summary>
    public static readonly bool Pacing = On && Environment.GetEnvironmentVariable("SHEPHERD_K8_PACING") != "0";
    /// <summary>The Frame Pacing row's choice (vita-settings.txt pacing=on|off; Runtime/VitaToggles.cs). Off by default.</summary>
    public static bool PacingChoice;
    /// <summary>Save backups: the Vita's System.Native keeps them and checks every save write (vita/runtime/system-native/
    /// pal_vita_savebak.inc.c, which reads the same switch); here the Restore Save Backup row's list and the restore at start.</summary>
    public static readonly bool SaveBak = On && Environment.GetEnvironmentVariable("SHEPHERD_K8_SAVEBAK") != "0";

    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string MainMenu = "Lamb.UI.MainMenu.UIMainMenuController", PauseMenu = "Lamb.UI.PauseMenu.UIPauseMenuController";
    static int _hidden;

    /// <summary>Lifecycle.For (once per type): the types whose Awake this track follows.</summary>
    public static bool Watches(Type t) => On && t.FullName is MainMenu or PauseMenu;

    /// <summary>Lifecycle.Awake, right after a watched type's Awake.</summary>
    public static void AfterAwake(object mb)
    {
        if (mb.GetType().FullName == MainMenu) Quality(mb);
        if (!HideLinks) return;
        try
        {
            var name = mb.GetType().FullName;
            Hide(mb.GetType().GetField("_discordButton", I)?.GetValue(mb) as Component, name == MainMenu ? "Main Menu 'Join Discord'" : "Pause Menu 'Join Discord'");
            if (name == MainMenu && mb.GetType().GetField("ad", I)?.GetValue(mb) is Component ads)
                foreach (var c in ads.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    var cn = c == null ? null : c.GetType().FullName;
                    if (cn == "MenuAdController") Hide(c, "Main Menu web ad (MenuAdController)");
                    else if (cn == "MenuDLCAdController") Hide(c, "Main Menu Woolhaven store block (MenuDLCAdController)");
                }
        }
        catch (Exception e) { Log.Warn("[k8] PC-only links: left as the game shows them (" + e.GetBaseException().Message + ")"); }
    }

    // ---------------------------------------------------------------- save backups: the Restore Save Backup row's list, the restore

    /// <summary>The folder the Vita's System.Native keeps the backups in: next to the sandbox save folder (ux0:data/shepherd/save-backups).</summary>
    static string BackupDir => Path.Combine(Path.GetDirectoryName(Impl.AppIcalls.Persistent.TrimEnd('/', '\\')) ?? Program.Root, "save-backups");
    /// <summary>The slots' backups (slot_N.* files), newest first, at most 10: file names in <see cref="BackupDir"/>.</summary>
    public static string[] SlotBackups()
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return Array.Empty<string>();
            var l = new List<string>();
            foreach (var f in Directory.GetFiles(BackupDir, "slot_*")) { var n = Path.GetFileName(f); if (Stamp(n) != null) l.Add(n); }
            l.Sort((a, b) => string.CompareOrdinal(Stamp(b), Stamp(a)));
            if (l.Count > 10) l.RemoveRange(10, l.Count - 10);
            return l.ToArray();
        }
        catch (Exception e) { Log.Warn("[k8] save backups not listed (" + e.GetBaseException().Message + ")"); return Array.Empty<string>(); }
    }
    /// <summary>"slot_0.mp.20261021-140312" -> "20261021-140312" (null: not a backup name).</summary>
    static string Stamp(string n)
    {
        int d = n.LastIndexOf('.');
        if (d < 0 || n.Length - d - 1 < 15 || !char.IsDigit(n[d + 1])) return null;
        var st = n.Substring(d + 1);
        return st.Length >= 15 && st[8] == '-' ? st : null;
    }
    /// <summary>The row's text for a backup: "Slot 1  2026-10-21 14:03".</summary>
    public static string BackupLabel(string n)
    {
        var st = Stamp(n); int us = n.IndexOf('_'), dot = n.IndexOf('.');
        string slot = us >= 0 && dot > us && int.TryParse(n.Substring(us + 1, dot - us - 1), out var k) ? "Slot " + (k + 1) : n.Substring(0, n.Length - st.Length - 1);
        return slot + "  " + st.Substring(0, 4) + "-" + st.Substring(4, 2) + "-" + st.Substring(6, 2) + " " + st.Substring(9, 2) + ":" + st.Substring(11, 2);
    }
    /// <summary>VitaToggles.Boot (the app's start, before the game reads its saves): the backup chosen last session put back -- the slot's
    /// file and, when there is one, the meta_N.json backup of the same save (the game writes meta_N.json right after slot_N.mp: the exact
    /// stamp, else the nearest one within 2 minutes, a later one on a tie -- review fix: not the first of the same minute in directory order,
    /// which is not time order on exFAT, and a save that crossed a minute found none). Through File.Copy: on the Vita the System.Native hook
    /// backs up the file it replaces first (the restore can be undone the same way; the source being read is never pruned) and checks the copy.
    /// Integration review: the copy PINNED when the backup was chosen (<see cref="Pin"/>) is read first -- the saves made between the choice
    /// and this start may have pruned the original from save-backups/ -- and removed after a good restore.</summary>
    public static void RestoreBackup(string name)
    {
        try
        {
            var st = Stamp(name); bool pinned = st != null && Pinned(name);
            var src = Path.Combine(pinned ? PinDir : BackupDir, name);
            if (st == null || !File.Exists(src)) { Log.Warn("[k8] Restore Save Backup: '" + name + "' is gone: nothing restored"); return; }
            var file = name.Substring(0, name.Length - st.Length - 1);
            var saves = Path.Combine(Impl.AppIcalls.Persistent, "saves");
            Directory.CreateDirectory(saves);
            File.Copy(src, Path.Combine(saves, file), true);
            string mname = null, meta = null;
            if (pinned) meta = MetaFor(file, st, PinDir, out mname);
            if (meta == null) meta = MetaFor(file, st, BackupDir, out mname);
            if (meta != null) File.Copy(meta, Path.Combine(saves, mname), true);
            Log.Info("[k8] Restore Save Backup: " + name + " put back as saves/" + file + (meta != null ? " (+ " + Path.GetFileName(meta) + ")" : " (no meta backup of the same save)") +
                     (pinned ? " from the copy pinned at the choice" : "") + " -- the replaced file was backed up first");
            if (pinned) Unpin();
        }
        catch (Exception e) { Log.Warn("[k8] Restore Save Backup: '" + name + "' NOT restored (" + e.GetBaseException().Message + ")" + (Pinned(name) ? "; the chosen backup stays in save-backups/pending-restore/" : "")); }
    }
    /// <summary>The meta_N.json backup in <paramref name="dir"/> that pairs with the slot backup <paramref name="file"/>.<paramref name="st"/>: the
    /// exact stamp, else the nearest within 2 minutes (a later one on a tie); null = none (or not a slot file). <paramref name="mname"/> = "meta_N.json".</summary>
    static string MetaFor(string file, string st, string dir, out string mname)
    {
        mname = null;
        int us = file.IndexOf('_'), dot = file.IndexOf('.');
        if (us < 0 || dot <= us || !Directory.Exists(dir)) return null;
        mname = "meta_" + file.Substring(us + 1, dot - us - 1) + ".json";
        string meta = null;
        long t0 = Secs(st), best = 241;   // (2 x seconds apart, +1 when before the slot's: <= 120 s either way)
        foreach (var f in Directory.GetFiles(dir, mname + ".*"))
        {
            var s = Stamp(Path.GetFileName(f)); if (s == null) continue;
            long dt = Secs(s) - t0, d = s == st ? -1 : dt < 0 ? 1 - 2 * dt : 2 * dt;
            if (d < best) { best = d; meta = f; }
        }
        return meta;
    }

    // Integration review (kit #8): the backup chosen in the Restore row is restored only at the NEXT start, and until then every save of its
    // file makes a new backup and prunes that file's oldest (pal_vita_savebak.inc.c keeps 5) -- the chosen one could be deleted for good
    // before the restore (then "is gone: nothing restored"). So the choice PINS it: a copy (with the meta_N.json backup the restore will pair
    // it with) in save-backups/pending-restore/, a sub-folder that neither the pruning nor the row's list looks into (both read only the files
    // directly in save-backups/). Choosing "No" or another backup, or the game's Reset All, replaces the pin; a good restore removes it.
    // Off with the feature (SHEPHERD_K8_SAVEBAK=0 / SHEPHERD_K8_PLAY=0: no row, no choice, no pin).
    static string PinDir => Path.Combine(BackupDir, "pending-restore");
    /// <summary>VitaToggles.OnRestore: pin the chosen backup (null = "No": only the old pin removed).</summary>
    public static void Pin(string name)
    {
        if (Pinned(name)) return;   // (the same backup again -- also the one whose original was pruned since: its pin is kept as it is)
        Unpin();
        if (name == null) return;
        try
        {
            var st = Stamp(name); var src = Path.Combine(BackupDir, name);
            if (st == null || !File.Exists(src)) return;
            Directory.CreateDirectory(PinDir);
            File.Copy(src, Path.Combine(PinDir, name), true);
            var meta = MetaFor(name.Substring(0, name.Length - st.Length - 1), st, BackupDir, out _);
            if (meta != null) File.Copy(meta, Path.Combine(PinDir, Path.GetFileName(meta)), true);
            Log.Info("[k8] Restore Save Backup: " + name + (meta != null ? " + " + Path.GetFileName(meta) : "") + " pinned in save-backups/pending-restore/ (the saves before the restore cannot prune it)");
        }
        catch (Exception e) { Log.Warn("[k8] Restore Save Backup: '" + name + "' NOT pinned (" + e.GetBaseException().Message + "): a save before the next start may prune it"); }
    }
    /// <summary>The pin removed (its files deleted; the folder stays).</summary>
    public static void Unpin()
    {
        try { if (Directory.Exists(PinDir)) foreach (var f in Directory.GetFiles(PinDir)) File.Delete(f); }
        catch (Exception e) { Log.Warn("[k8] Restore Save Backup: the pinned copy not removed (" + e.GetBaseException().Message + ")"); }
    }
    /// <summary>The chosen backup has its pinned copy.</summary>
    public static bool Pinned(string name)
    {
        try { return name != null && File.Exists(Path.Combine(PinDir, name)); } catch (Exception) { return false; }
    }

    /// <summary>A backup stamp "20261021-140312[-k]" as seconds (for the nearest meta; a name that is no date: far from every other).</summary>
    static long Secs(string s)
    {
        try { return new DateTime(D(s, 0, 4), D(s, 4, 2), D(s, 6, 2)).Ticks / TimeSpan.TicksPerSecond + D(s, 9, 2) * 3600 + D(s, 11, 2) * 60 + D(s, 13, 2); }
        catch (ArgumentException) { return long.MinValue / 4; }
    }
    static int D(string s, int i, int n) { int v = 0; for (int k = i; k < i + n; k++) v = v * 10 + s[k] - '0'; return v; }

    // ---------------------------------------------------------------- the measured clock (the overlay)

    /// <summary>The ARM clock the performance overlay shows: the host's measured one (k8_play.inc.c op 1), -1 off / no host.</summary>
    public static int ClockMhz() => ClockMeasured && Diag.VitaHost.On ? Host(1, 0) : -1;

    // ---------------------------------------------------------------- G3 frame pacing + the frame-time histogram (play clock)

    const double Vblank = 1000.0 / 60.0;          // ms: the Vita's display refreshes at 60 Hz
    const int Window = 30, LoadMs = 250;          // the frames per decision; longer frames (loads) are left out of it
    static readonly int[] _win = new int[Window], _sorted = new int[Window];   // frame times in 0.1 ms (ints: no new generic sort in the AOT image)
    static int _nWin, _lock = 2, _down, _waits, _waitedVb, _changes;
    static string _area;
    static readonly Dictionary<string, int> _locks = new();
    static long _lastEnd;
    static readonly int[] _hist = new int[8];      // frames by their length in vblanks (1..7, 8+): the device's frame-time histogram
    static readonly int[] _histLocked = new int[8];
    static int _histFrames;

    /// <summary>Boot loop, first thing every frame (the play clock on the Vita host only; a no-op otherwise): the last frame's length into the
    /// histogram and, with the Frame Pacing row On, the area's lock and the wait for the frame's vblank.</summary>
    public static void FrameStart(int frame)
    {
        if (!Pacing || !Time.Wall || !Diag.VitaHost.On) return;
        long now = Stopwatch.GetTimestamp();
        bool locked = PacingChoice && Render.Backends.Kind == "gl";
        if (_lastEnd != 0)
        {
            double ms = (now - _lastEnd) * 1000.0 / Stopwatch.Frequency;   // the frame's own time: from the end of its wait to now
            int vb = (int)Math.Ceiling(ms / Vblank - 0.02); (locked ? _histLocked : _hist)[Math.Clamp(vb, 1, 8) - 1]++; _histFrames++;
            if (locked && ms < LoadMs) Note((int)(ms * 10));
        }
        if (locked)
        {
            var area = Scenes.Active?.Name ?? "";
            if (area != _area)
            {
                if (_area != null) _locks[_area] = _lock;
                _area = area; _lock = _locks.TryGetValue(area, out var l) ? l : 2; _nWin = 0; _down = 0;
                Log.Info("[k8] frame pacing: area '" + area + "' locked at " + (60 / _lock) + " fps (" + _lock + " vblanks" + (_locks.ContainsKey(area) ? ", this area's last lock" : ", the first look") + ")");
            }
            int w = Host(3, _lock);
            if (w > 0) { _waits++; _waitedVb += w; }
        }
        _lastEnd = Stopwatch.GetTimestamp();
    }

    /// <summary>A frame's own time into the area's window; every <see cref="Window"/> frames the lock is decided: the fewest vblanks (2 / 3 /
    /// 4) that 90 % of the window's frames fit in -- up at once, down only after two windows in a row agree (no flapping).</summary>
    static void Note(int tenthMs)
    {
        _win[_nWin++] = tenthMs;
        if (_nWin < Window) return;
        _nWin = 0;
        Array.Copy(_win, _sorted, Window); Array.Sort(_sorted);
        int p90 = _sorted[Window * 9 / 10 - 1];
        int need = p90 <= 323 ? 2 : p90 <= 485 ? 3 : 4;   // (2 / 3 vblanks of 16.67 ms with a 3 % margin, in 0.1 ms)
        if (need > _lock || (need < _lock && ++_down >= 2))
        {
            Log.Info("[k8] frame pacing: area '" + _area + "' " + (60 / _lock) + " -> " + (60 / need) + " fps (90 % of the last " + Window + " frames took <= " + (p90 / 10) + " ms)");
            _lock = need; _down = 0; _changes++;
        }
        else if (need >= _lock) _down = 0;
    }

    /// <summary>End of run: the frame-time histogram (play clock) and the pacing counters.</summary>
    public static string Summary()
    {
        if (_histFrames == 0) return null;
        return "[k8] frame times (play clock; frames per length 1..8+ vblanks of 16.7 ms): free " + string.Join(" ", _hist) + "; locked " + string.Join(" ", _histLocked) +
               " | frame pacing " + (PacingChoice ? "ON" : "off") + ": " + _waits + " waits, " + _waitedVb + " vblanks, " + _changes + " lock changes";
    }

    static int Host(int op, int arg) =>
#if SHEPHERD_EBOOT
        HostNative(op, arg);
#else
        Diag.VitaHost.On ? HostNative(op, arg) : -1;
#endif
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int HostNative(int op, int arg) => shepherd_vita_k8(op, arg);
    [DllImport("__Internal")]
    static extern int shepherd_vita_k8(int op, int arg);

    // ---------------------------------------------------------------- max quality: the game's own settings, logged once

    static bool _qualityLogged;
    /// <summary>The Main Menu's Awake (the game has read its settings by then): one line with the game's graphics settings as they are --
    /// the default is the PC's own Ultra preset (SettingsData.GraphicsSettings' constructor), nothing in Shepherd changes them; the PC's own
    /// rows stay the player's way to trade quality for speed (kit8-play.md, "Max quality": which of them the renderer honours today).</summary>
    static void Quality(object mb)
    {
        if (_qualityLogged) return;
        _qualityLogged = true;
        try
        {
            var sm = mb.GetType().Assembly.GetType("SettingsManager")?.GetProperty("Settings", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var g = sm?.GetType().GetField("Graphics")?.GetValue(sm);
            if (g == null) return;
            var sb = new System.Text.StringBuilder("[k8] the game's graphics settings (preset 3 = Ultra, the PC's own default; Shepherd never changes them):");
            foreach (var f in g.GetType().GetFields()) sb.Append(' ').Append(f.Name).Append('=').Append(f.GetValue(g));
            Log.Info(sb.ToString());
        }
        catch (Exception e) { Log.Info("[k8] the game's graphics settings: not read (" + e.GetBaseException().Message + ")"); }
    }

    static void Hide(Component c, string what)
    {
        if (c == null) return;
        var go = c.gameObject;
        if (!go.activeSelf) return;
        go.SetActive(false);
        if (_hidden++ < 8) Log.Info("[k8] hidden (opens something outside the game; SHEPHERD_PC_LINKS=1 shows it): " + what + " '" + go.name + "'");
    }
}
