using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #6, track toggles (vita/notes/kit6-toggles.md): the owner's own rows in the game's Settings > Graphics tab, made from
/// the tab's own rows so they look, sound, navigate and answer touch exactly like them (the game's DLLs are not changed; the engine clones
/// the rows at run time, right after the tab's GraphicsSettings.Start):
/// <list type="bullet">
/// <item>a section header "PS Vita Settings" (a clone of the tab's "Screen Settings" header) after the game's last row;</item>
/// <item>"4th CPU Core" On / Off (a clone of the VSync toggle row): On = the latency-tolerant workers (pack read-ahead, power / watchdog) on
/// CPU3, the system-reserved core the owner's core-unlock plugin opens (kit #4's 4-core split); Off = every one of them on USER_2, exactly
/// the fallback of a Vita without the plugin. Default On when the host's boot probe found the core (C3 line); without it (no plugin,
/// SHEPHERD_CORE3=0, or an app that pins no thread) the row shows Off, greyed out the way the game greys a row it cannot change, and
/// cannot be turned on. Applied live: the host moves the workers already pinned (a PIN line each, with the read-back);</item>
/// <item>"CPU Speed" 444 MHz / 500 MHz (a clone of the Target Frame Rate selector row): the ARM clock the app asks for. 444 is every
/// build's clock since 2026-10-13; 500 is offered only if the device's plugins let scePowerSetArmClockFrequency(500) take effect -- the
/// first time the rows are built in a session the host asks for 500 and reads the clock back (a CLK line each), then returns to the
/// owner's choice; anything but 500 read back = not available: the row shows 444 MHz, greyed out. Applied live (CLK lines);</item>
/// <item>"CPU Clock" (a clone of the same selector row, not selectable): the ARM clock the host READS BACK now and, on the play clock
/// (SHEPHERD_TIME=wall), the frames per second, refreshed about once a second (realtimeSinceStartup: on the test clock every 60 frames,
/// so scripted runs stay deterministic). One host call a second; the text is set only when a number changes (no per-frame allocation).</item>
/// </list>
/// Labels are English only, in every language (the clones' I2 Localize components are removed, so the game's re-localization never
/// replaces them; the fonts are the rows' own). The game's "Reset All" (square, on any tab) puts these rows back to their defaults too. The choices
/// live in the owner folder's own file vita-settings.txt (ux0:data/shepherd/; never the game's settings.json), written when a row changes
/// and read at boot, before the pack's read-ahead thread is pinned (so a saved "Off" or "500" holds from the start).
/// Switch: SHEPHERD_K6_TOGGLES=0 (env file) = kit #5: no rows, the file never read or written, the host's export never called.
/// The Mac (no Vita host): a stand-in answers for the host -- SHEPHERD_K6_SIM_CORE3=0 = no core-unlock plugin, SHEPHERD_K6_SIM_ARM500=1 = a
/// plugin that lets 500 MHz through (Mac-only code, left out of the eboots).
/// Code size: every byte of AOT code is eboot span (the device's free user memory drops 1 MiB at a time with it), so this class builds its
/// log text by concatenation, not interpolation (each interpolated string inlines its handler), and reaches the game's types through a few
/// reflection helpers.</summary>
[ProfileOnly]   // log text and switch names only: no property-bag key is read here (Assets/Diet.cs)
public static class VitaToggles
{
    /// <summary>The track's switch (on by default under the Vita profile).</summary>
    public static readonly bool On = Profile.Vita && Environment.GetEnvironmentVariable("SHEPHERD_K6_TOGGLES") != "0";
    /// <summary>Kit #6 integration, owner decision 2026-10-19 (vita/notes/roadmap-kits.md, kit #6: "it's a handheld"): the PC rows stay as
    /// the PC game shows them -- Target Frame Rate, Resolution (its single 960 x 544) and every quality setting usable -- except FullScreen
    /// Mode: greyed out the game's way (the way these rows grey: MMSelectable.Interactable, again at every show of the tab and -- review fix
    /// -- every frame while the menu is open) and fixed at FullScreen, its value guarded too (the selector's OnSelectionChanged replaced:
    /// OnFsChanged); Windowed / Borderless can never be selected, and a saved Windowed / Borderless value
    /// (a PC settings file) is shown and applied as FullScreen (SettingsData.Graphics.FullscreenMode = 2, the game's own default, when the
    /// Settings menu opens; the screen itself is always the panel's full screen: Runtime/NativeDisplay.cs). Independent of the rows'
    /// switch. <c>SHEPHERD_K6_FULLSCREEN=0</c> = the row as the PC game shows it (kit #5).</summary>
    public static readonly bool FsFixed = Profile.Vita && Environment.GetEnvironmentVariable("SHEPHERD_K6_FULLSCREEN") != "0";
    /// <summary>Kit #6 integration, owner decision 2026-10-19: is 500 MHz there? asked ONCE PER LAUNCH, at start, together with re-applying a
    /// saved 500 (never on a Settings visit: the once-a-second CPU Clock readout shows a plugin that changes the clock later).
    /// <c>SHEPHERD_K6_ARM500_PROBE=tab</c> = the toggles track's rule (asked when the Graphics tab is first shown in a launch).</summary>
    public static readonly bool ProbeAtStart = Environment.GetEnvironmentVariable("SHEPHERD_K6_ARM500_PROBE") != "tab";
    /// <summary>True while the game's Settings menu is open (from its controller's Start, rows built or not, until <see cref="Tick"/> sees it
    /// gone): <see cref="Tick"/> returns at once otherwise.</summary>
    public static bool Live;

    const string FileName = "vita-settings.txt";
    const string GfxType = "Lamb.UI.SettingsMenu.GraphicsSettings", MenuType = "Lamb.UI.SettingsMenu.UISettingsMenuController";
    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    // host state bits (host.c shepherd_vita_k6): 1 core pinning on, 2 CPU3 open, 4 workers on CPU3 now, 8 the host sets the clocks; 16-27 launch MHz
    const int StPin = 1, StOpen = 2, StOnCpu3 = 4, StClocks = 8;
    /// <summary>Device kit #9, track device (vita/notes/kit9-device.md), owner decision 2026-10-22 ("I'm okay with 500 MHz being the default,
    /// just like the cap unlocker for the 4th core is"): host state bit 28 = this app's CPU Speed defaults to 500 MHz -- the play apps
    /// (host.c SHP_PLAY_APP: 00097 / 00098 / 00095 / 00193 / 00194); the measurement app 00099 stays 444 for the kit-to-kit history. The app
    /// cannot raise the clock above 444 itself: the stock kernel refuses scePowerSetArmClockFrequency(500) (rc 0x802b0000, every kit #6-#8
    /// start), and the owner's plugins export no API an app could call (PSVshell / PSVshellPlus and NoPowerLimits define no exported library:
    /// their module .yml files have only module_start / module_stop; PSVshell hooks the kernel's clock setters and applies a per-title
    /// PROFILE). So the choice stays 500 (saved / shown) and the start asks for it once as before; when it is refused the clock falls back to
    /// 444 silently, and the greyed row says so plainly: "444 MHz (500: PSVshellPlus)" -- or "500 MHz (PSVshellPlus)" when a profile for this
    /// app already runs the cores at ~500 (kit #8's measured clock). A saved cpu_mhz= line still wins. <c>SHEPHERD_K9_ARM500_DEFAULT=0</c>
    /// (host side: the bit is not set) = kit #8 (444 by default).</summary>
    const int St500Default = 1 << 28;
    static bool _core3 = true;     // the owner's choices (vita-settings.txt), defaults = kit #5
    static int _mhz = 444;
    static bool _mhzSaved;         // kit #9: the CPU Speed choice is the owner's (a cpu_mhz= line that counts, or the row changed): it wins over the default
    /// <summary>Kit #9 review fix (finding 5, vita/notes/kit9-device.md "Review fixes"): kit #6-#8 wrote <c>cpu_mhz=</c> with EVERY row change
    /// (444 unless 500 was chosen), and kit #9's first version did the same -- so the 500 default became a "saved" 500 after any toggle (and
    /// <c>SHEPHERD_K9_ARM500_DEFAULT=0</c> no longer gave kit #8's 444), the early restore-backup save froze 444, and the owner's own card (a
    /// vita-settings.txt from kit #6-#8 on every start of the kit #8 logs) would have kept 444 for good. Now, with the kit #9 rule on
    /// (<c>SHEPHERD_K9_ARM500_DEFAULT</c> not 0, every app): the line is written only for a choice made in the CPU Speed row, followed by
    /// <see cref="K9Chosen"/> (a comment: every older build skips it); a <c>cpu_mhz=444</c> without it is kit #6-#8's automatic line, not a
    /// choice (a 500 always is one: 444 was their default). With the switch at 0 the file is read and written exactly as kit #8 did.</summary>
    static readonly bool K9Rules = Environment.GetEnvironmentVariable("SHEPHERD_K9_ARM500_DEFAULT") != "0";
    const string K9Chosen = "# cpu_mhz above: chosen in the CPU Speed row (kit #9)";
    static bool _mhzLegacy;        // kit #9 review: the file's cpu_mhz=444 was kit #6-#8's automatic line (logged once)
    static bool K9Default500 => (_state & St500Default) != 0;
    static int DefaultMhz => K9Default500 ? 500 : 444;
    static int _noteHi = -1;       // kit #9: the plain note shown for the measured clock's class (1 = ~500, 0 = ~444; -1 none)
    /// <summary>Kit #7 (track measure): the "Performance Overlay" row (Render/K7Overlay.cs) -- a clone of the VSync toggle row after the CPU
    /// Clock readout, Off by default, saved as overlay=on|off; the game's Reset All turns it Off. GL apps only (the headless ones draw
    /// nothing). <c>SHEPHERD_K7_OVERLAY_ROW=0</c> (or <c>SHEPHERD_K7=0</c>) = kit #6's four rows; an overlay= line in the file is then left alone.</summary>
    static readonly bool OvlRow = Diag.K7.OverlayRow && Render.Backends.Kind == "gl";
    static bool _overlay;
    /// <summary>Review fix (kit #7 measure): with the row off (SHEPHERD_K7_OVERLAY_ROW=0, SHEPHERD_K7=0, a headless app) the file's overlay=
    /// value as read, written back as it was by <see cref="Save"/> (a kit #6 row change or Reset All used to drop it: the owner's overlay
    /// choice gone at the next default start); null = no such line.</summary>
    static string _ovlKept;
    static Component _ovl; static Selectable _ovlRow;
    /// <summary>Kit #8 (track play, vita/notes/kit8-play.md): three more rows after the Performance Overlay -- "Rear Touch" On / Off (a clone
    /// of the VSync toggle row: the rear panel's corners as L2 / R2 / L3 / R3, On by default; saved rear=on|off), "Frame Pacing" Off / On (the
    /// same: G3 display-locked pacing, Off by default; pacing=on|off) and "Restore Save Backup" (a clone of the Target Frame Rate selector:
    /// "No" or one of the slots' backups, newest first -- restored at the next start, before the game reads anything; restore=&lt;file&gt;|no).
    /// The game's Reset All: On / Off / No. GL apps only. <c>SHEPHERD_K8_ROWS=0</c> (or the feature's own switch, or SHEPHERD_K8_PLAY=0): not
    /// built, the file's lines kept as they are -- and (review fix) the saved rear= / pacing= choices still apply while the feature itself is on
    /// (a saved Rear Touch Off holds without its row; a pending restore= waits for the row).</summary>
    static readonly bool K8Rows = K8Play.On && Environment.GetEnvironmentVariable("SHEPHERD_K8_ROWS") != "0" && Render.Backends.Kind == "gl";
    static readonly bool RearRow = K8Rows && K8Play.Rear, PaceRow = K8Rows && K8Play.Pacing, BakRow = K8Rows && K8Play.SaveBak;
    static bool _rear = true, _pacing; static string _restore;   // the owner's kit #8 choices (defaults: On, Off, no restore)
    static string _k8Kept = "";                                   // rear= / pacing= / restore= lines of a row that is not built: written back as read
    static Component _rearTog, _paceTog, _bakSel; static Selectable _rearRow, _paceRow, _bakRow;
    static string[] _bakItems = Array.Empty<string>();            // the backup selector's files (index 0 = "No")
    /// <summary>Kit #10 (track cache, vita/notes/kit10-cache.md; owner decisions 2026-10-16 / 2026-10-17): four more rows after Restore Save
    /// Backup -- "Cache Size" (a clone of the Target Frame Rate selector: 256 MB / 512 MB / 1 GB / 2 GB, 1 GB by default; saved as
    /// cache_mb=256|512|1024|2048; the learning cache's cap, applied live: least recently used entries out), "Cache In Use" (a readout like
    /// CPU Clock: the cache folder's size, refreshed about once a second), "Clear Cache" (a clone of the selector with no value, confirmed with
    /// the X button like the game's own buttons -- MMSelectable.OnConfirm -- through the game's own confirmation window, as its Reset All:
    /// confirm = everything under ux0:data/shepherd/cache deleted, never saves or settings) and "Verify Files" (the same; it starts the io track's
    /// verify core and shows its progress / result; greyed "not in this build" without it). The game's Reset All puts Cache Size back to 1 GB.
    /// GL apps only. <c>SHEPHERD_K10_CACHE=0</c> (the track's master) or <c>SHEPHERD_K10_ROWS=0</c>: not built, a cache_mb= line kept as read.</summary>
    static readonly bool K10Rows = K10Cache.On && Environment.GetEnvironmentVariable("SHEPHERD_K10_ROWS") != "0" && Render.Backends.Kind == "gl";
    static int _cacheMb = K10Cache.DefaultMb;                           // the owner's Cache Size (default 1 GB)
    static string _k10Kept = "";                                         // a cache_mb= line while the rows are not built: written back as read
    static Component _cacheSel, _useSel, _clearSel, _verifySel; static Selectable _cacheRow, _clearRow, _verifyRow;
    static object _useText, _useHelper, _clearText, _clearHelper, _verifyText, _verifyHelper;
    static string _useShown, _clearShown, _verifyShown;
    static int _clearSeen, _verifyState;   // the Clear Cache state last shown; Verify Files: 1 = started from these rows (its result window is due)
    static int _arm500 = -1;       // 500 MHz this session: -1 not tried yet, 0 not available, 1 available
    static int _state;             // the host's state bits, last answer

    static Component _tog, _speed, _clock;   // the clones' MMToggle / MMHorizontalSelector (game types, through reflection)
    static Selectable _togRow, _speedRow;
    static object _clockText, _clockHelper;   // the readout's two TextMeshProUGUI (MMHorizontalSelector._text / _textHelper)
    static Component _menu;                   // the open Settings menu (its UISettingsMenuController; the Graphics tab if that was not seen)
    static object _gfxData;                   // SettingsManager.Settings.Graphics when the Settings menu opened: replaced = the game's Reset All
    static Assembly _gameAsm;                 // the game's assembly (SettingsManager), from the watched types
    static PropertyInfo _tmpText, _settingsProp;
    static FieldInfo _graphicsField;
    static readonly Transform[] _made = new Transform[12];
    static double _next, _t0;
    static int _f0, _shownMhz = -1, _shownFps = -1;
    static bool _fpsLogged;

    static string OnOff(bool on) => on ? "On" : "Off";
    static object Get(object o, string field) => o.GetType().GetField(field, I).GetValue(o);
    static void SetProp(object o, string prop, object v) => o.GetType().GetProperty(prop, I).SetValue(o, v);
    static object Call(object o, string method, Type arg, object v) => o.GetType().GetMethod(method, I, null, arg == null ? Type.EmptyTypes : new[] { arg }, null)?.Invoke(o, arg == null ? null : new[] { v });

    // ---------------------------------------------------------------- boot

    /// <summary>BootGame, before the asset layer opens the pack (its read-ahead thread asks the host for the worker mask when it starts):
    /// the owner's saved choices, applied through the host when they differ from the defaults (no file, or the defaults: no host call).</summary>
    public static void Boot()
    {
        if (!On) return;
        string text = null;
        try { if (File.Exists(FilePath)) text = File.ReadAllText(FilePath); }
        catch (Exception e) { Log.Warn("[k6] " + FileName + ": not read (" + e.GetBaseException().Message + "): the defaults"); }
        if (text != null) Parse(text);
        if (OvlRow) Render.K7Overlay.SetChoice(_overlay);   // kit #7: the saved overlay choice from the first frame
        if (RearRow || (K8Play.Rear && !_rear)) Impl.VitaPad.RearOn = _rear;   // kit #8: the saved rear touch / frame pacing choices from the first frame
        if (K8Play.Pacing) K8Play.PacingChoice = _pacing;   // (review fix: also without their rows -- SHEPHERD_K8_ROWS=0 -- while the feature is on; VitaPad
                                                            // touched here only as before or for a saved Off)
        _state = Host(0, 0);   // (kit #9 review: read before the restore's Save below -- op 0 only answers, so the order changes nothing else)
        if (!_mhzSaved && K9Default500) _mhz = 500;   // kit #9: 500 MHz by default in the play apps (the bit: host.c; the owner's saved choice wins)
        if (BakRow && _restore != null) { K8Play.RestoreBackup(_restore); _restore = null; Save(); }   // kit #8: a restore chosen last session, before the game reads its saves
        if (K10Cache.On) K10Cache.Configure(_cacheMb);   // kit #10: the learning cache's cap (the saved Cache Size; also without its row)
        Log.Info("[k6] Settings > Graphics > PS Vita Settings rows ON (SHEPHERD_K6_TOGGLES=0: off): " + (text == null ? "no " + FileName + " yet, the defaults" : FileName) +
                 ": " + Choices() + (K9Default500 ? (_mhzSaved ? " (kit #9: 500 MHz is this app's default; the saved choice wins)" : " (kit #9: 500 MHz by default in the play apps; SHEPHERD_K9_ARM500_DEFAULT=0 = 444)") : "") +
                 (_mhzLegacy ? " (kit #9: the file's cpu_mhz=444 was written by kit #6-#8 with every row change, not chosen -- the default applies; a choice in the CPU Speed row is kept)" : "") +
                 " | host: " + StateNote(_state));
        if (!_core3 && (_state & StOnCpu3) != 0) _state = Host(1, 0);   // the workers on USER_2 from the start (the power thread moved now)
        if (ProbeAtStart && (_state & StClocks) != 0)
        {   // (kit #6 integration, owner decision: the launch's one check whether 500 MHz is there -- a saved 500 is that check)
            if (_mhz == 500) TrySpeed(500, _mhzSaved || !K9Default500 ? "the saved choice; also this launch's one check whether 500 MHz is there" : "the default (kit #9); also this launch's one check whether 500 MHz is there");
            else { TrySpeed(500, "is it available? asked once, at start"); if (_arm500 == 1) Host(2, 444); }
        }
        else if (_mhz == 500) TrySpeed(500, "the saved choice");
    }

    static string FilePath => Path.Combine(Program.Root, FileName);
    static string Choices() => "4th CPU Core " + OnOff(_core3) + ", CPU Speed " + _mhz + " MHz" + (OvlRow ? ", Performance Overlay " + OnOff(_overlay) : "") +
        (RearRow ? ", Rear Touch " + OnOff(_rear) : "") + (PaceRow ? ", Frame Pacing " + OnOff(_pacing) : "") + (BakRow ? ", Restore Save Backup " + (_restore ?? "No") : "") + (K10Rows ? ", Cache Size " + MbText(_cacheMb) : "");

    static void Parse(string text)
    {
        bool chosen = false; int mhzLine = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line == K9Chosen) chosen = true;   // kit #9 review (a comment line: skipped below as before)
            int eq = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || eq < 0) continue;
            var key = line.Substring(0, eq).Trim(); var val = line.Substring(eq + 1).Trim();
            if (key == "core3" && (val == "on" || val == "off")) _core3 = val == "on";
            else if (key == "cpu_mhz" && (val == "444" || val == "500")) { _mhz = val == "500" ? 500 : 444; mhzLine = _mhz; }
            else if (key == "overlay" && (val == "on" || val == "off")) { if (OvlRow) _overlay = val == "on"; else _ovlKept = val; }   // kit #7 (the row off: left alone, kept for Save)
            else if (key == "rear" && (val == "on" || val == "off")) { _rear = val == "on"; if (!RearRow) _k8Kept += line + "\n"; }   // kit #8 (applied with or without the row)
            else if (key == "pacing" && (val == "on" || val == "off")) { _pacing = val == "on"; if (!PaceRow) _k8Kept += line + "\n"; }
            else if (key == "restore") { if (BakRow) _restore = val == "no" || val.Length == 0 ? null : val; else _k8Kept += line + "\n"; }
            else if (key == "cache_mb" && int.TryParse(val, out var cmb) && Array.IndexOf(K10Cache.SizesMb, cmb) >= 0) { _cacheMb = cmb; if (!K10Rows) _k10Kept += line + "\n"; }   // kit #10
            else Log.Warn("[k6] " + FileName + ": '" + line + "' ignored (core3=on|off, cpu_mhz=444|500)");
        }
        // kit #9 review (finding 5): a 500 is always a choice; a 444 only with the kit #9 mark (else kit #6-#8 wrote it with any row change)
        _mhzSaved = mhzLine == 500 || (mhzLine == 444 && (chosen || !K9Rules));
        _mhzLegacy = mhzLine == 444 && !_mhzSaved;
    }

    static void Save()
    {
        try
        {   // (review: through the GXP cache's atomic write -- a temp file, read back, renamed over the old one: a power cut leaves the old
            // file or the new one, never a torn one)
            var err = Render.GxpCache.WriteAtomic(FilePath, System.Text.Encoding.ASCII.GetBytes(
                "# Shepherd (PS Vita): Settings > Graphics > PS Vita Settings -- written by the app when a row changes; delete it for the defaults\ncore3=" +
                (_core3 ? "on" : "off") + "\n" + (!K9Rules ? "cpu_mhz=" + _mhz + "\n" : _mhzSaved ? "cpu_mhz=" + _mhz + "\n" + K9Chosen + "\n" : "") + (OvlRow ? "overlay=" + (_overlay ? "on" : "off") + "\n" : _ovlKept != null ? "overlay=" + _ovlKept + "\n" : "") +
                (RearRow ? "rear=" + (_rear ? "on" : "off") + "\n" : "") + (PaceRow ? "pacing=" + (_pacing ? "on" : "off") + "\n" : "") +
                (BakRow ? "restore=" + (_restore ?? "no") + "\n" : "") + _k8Kept + (K10Rows ? "cache_mb=" + _cacheMb.ToString() + "\n" : _k10Kept)));
            if (err != null) { Log.Warn("[k6] " + FileName + ": not written (" + err + ")"); return; }
            Log.Info("[k6] saved " + FileName + ": " + Choices());
        }
        catch (Exception e) { Log.Warn("[k6] " + FileName + ": not written (" + e.GetBaseException().Message + ")"); }
    }

    static string StateNote(int st) =>
        (st & StPin) == 0 ? "core pinning off in this app (the 4th core is never used)" :
        "4th core " + ((st & StOpen) != 0 ? "open (core-unlock plugin)" : "not open") + ", workers on " + ((st & StOnCpu3) != 0 ? "CPU3" : "USER_2") +
        ((st & StClocks) != 0 ? "" : "; clocks not set by the app (SHEPHERD_CLOCKS=0: launch " + ((st >> 16) & 0xfff) + " MHz)");

    /// <summary>Ask the host for <paramref name="mhz"/> and keep it only when the clock reads back as asked; a 500 that does not take
    /// effect marks 500 MHz not available this session and asks for 444 again. Returns the clock read back at the end.</summary>
    static int TrySpeed(int mhz, string why)
    {
        int back = Host(2, mhz);
        if (mhz == 500) _arm500 = back == 500 ? 1 : 0;
        Log.Info("[k6] CPU Speed " + mhz + " MHz (" + why + "): the clock reads back " + back + " MHz" + (back == mhz ? "" : mhz == 500 ?
                 ": 500 MHz is not available on this device (the clock did not take it: no plugin lets the app above 444, or one sets the clocks itself): back to 444" : ""));
        if (back != mhz && mhz == 500) back = Host(2, 444);
        if (back != mhz && mhz == 500 && K9Default500 && _mhz == 500)
            Log.Info("[k9] CPU Speed: 500 MHz is the choice, the clock stays at 444 (the app cannot raise it: no plugin offers an app a way; 500 needs a PSVshellPlus profile for this app) -- the row says so");
        return back;
    }

    // ---------------------------------------------------------------- the rows

    /// <summary>Lifecycle.For (once per type): the Settings menu's controller (its Start: the menu is open, the Reset-All watch begins) and
    /// the Graphics tab (its Start builds the rows).</summary>
    public static bool Watches(Type t) => (On || FsFixed) && (t.FullName == GfxType || t.FullName == MenuType);

    /// <summary>Lifecycle's Start pass, right after the Start of a watched type: UISettingsMenuController (the Settings menu opened) or
    /// GraphicsSettings (the Graphics tab shown for the first time in this Settings menu: the rows). A failure leaves the tab as the game
    /// made it (one WRN line).</summary>
    public static void AfterStart(object o)
    {
        if (o.GetType().FullName == MenuType) { MenuStart((Component)o); return; }
        if (FsFixed) FixFullscreenRow((MonoBehaviour)o);
        if (!On) return;
        try { Build((MonoBehaviour)o); }
        catch (Exception e)
        {
            Log.Warn("[k6] Settings > Graphics: the PS Vita rows were not added (" + e.GetBaseException().Message + ")");
            _tog = _speed = _clock = _ovl = null; _togRow = _speedRow = _ovlRow = null;   // (Live stays: the Reset-All watch goes on while the menu is open)
            _rearTog = _paceTog = _bakSel = null; _rearRow = _paceRow = _bakRow = null;   // kit #8
            K10Forget();                                                                   // kit #10
            foreach (var r in _made) if (r != null) UObject.Destroy(r.gameObject);   // (rows already in the list: gone again)
        }
        Array.Clear(_made);
    }

    /// <summary>The Settings menu opened (its controller's Start, before its first Update -- the game's Reset All runs from that Update, on
    /// any tab): the game's Graphics settings object now, the Reset-All watch on (review fix: kit #6 compared it only from the Graphics
    /// tab's first show, so a Reset All on another tab before it left these rows and the file as they were).</summary>
    static void MenuStart(Component menu)
    {
        if (Live) CheckReset();   // (a menu closed and a new one opened within a second: the old one's Reset All, if any, first)
        _menu = menu; _gameAsm ??= menu.GetType().Assembly;
        if (FsFixed) FixFullscreenData();
        _gfxData = GraphicsData();
        _next = Time.Realtime + 1.0;
        Live = true;
    }

    /// <summary>The game's Reset All (UISettingsMenuController.ResetAll: every tab's Reset, then SaveAndApply; GraphicsSettings.Reset puts a
    /// new object in SettingsManager.Settings.Graphics) since the last look: these choices back to their defaults too (On, 444 MHz), applied
    /// through the host and saved. Called when the rows are built, about once a second while the menu is open, and once more when it is
    /// gone. True = it happened (the rows, if built, must show the defaults).</summary>
    static bool CheckReset()
    {
        if (!On) return false;   // (the FullScreen Mode fix alone: no rows, no file)
        var g = GraphicsData();
        if (g == null || ReferenceEquals(g, _gfxData)) return false;
        bool seen = _gfxData != null; _gfxData = g;
        if (!seen) return false;
        Log.Info("[k6] the game's Reset All: 4th CPU Core and CPU Speed back to their defaults (On, " + DefaultMhz + " MHz)" + (OvlRow ? ", Performance Overlay Off" : "") + (_tog == null ? " (the rows not built yet)" : ""));
        if (!_core3 && (_state & StOpen) != 0) _state = Host(1, 1);
        if (_mhz != DefaultMhz) TrySpeed(DefaultMhz, "Reset All");   // (kit #9: the play apps' default is 500 -- kept as the choice even when refused)
        _core3 = true; _mhz = DefaultMhz; _mhzSaved = false;   // (kit #9 review: back to the default -- not a choice)
        if (OvlRow) { _overlay = false; Render.K7Overlay.SetChoice(false); }
        if (RearRow) { _rear = true; Impl.VitaPad.RearOn = true; }   // kit #8
        if (PaceRow) { _pacing = false; K8Play.PacingChoice = false; }
        if (BakRow) { if (_restore != null) K8Play.Unpin(); _restore = null; }   // (integration review: and the chosen backup's pin)
        if (K10Rows && _cacheMb != K10Cache.DefaultMb) { _cacheMb = K10Cache.DefaultMb; K10Cache.SetCap(_cacheMb); }   // kit #10: Cache Size back to 1 GB
        Save();
        return true;
    }

    static void Build(MonoBehaviour gs)
    {
        var content = (Get(gs, "_scrollRect") as ScrollRect)?.content;
        var fps = Get(gs, "_targetFpsSelector") as Component;     // MMHorizontalSelector (Target Frame Rate)
        var vsync = Get(gs, "_vSyncSwitch") as Component;         // MMToggle (VSync)
        Transform selRow = RowOf(fps, content), togRow = RowOf(vsync, content), head = null;
        for (int i = 0; content != null && i < content.childCount && head == null; i++)
            if (content.GetChild(i).GetComponent<Selectable>() == null) head = content.GetChild(i);   // the first section header ("Screen Settings")
        if (selRow == null || togRow == null || head == null) throw new InvalidOperationException("the tab's scroll content, Target Frame Rate / VSync rows or header not found");
        _tmpText ??= FindTmp(head)?.GetType().GetProperty("text", I);
        string after = content.GetChild(content.childCount - 1).name;

        // built under an inactive holder: nothing wakes (Localize, disablePerPlatform) until the clones are fixed and moved into the list
        var holder = new GameObject("k6 rows", typeof(RectTransform));
        holder.SetActive(false);
        holder.transform.SetParent(gs.transform, false);
        try
        {
            _made[0] = Clone(head, holder, "-- PS Vita Settings --", "PS Vita Settings");
            _made[1] = Clone(togRow, holder, "4th CPU Core", "4th CPU Core");
            _made[2] = Clone(selRow, holder, "CPU Speed", "CPU Speed");
            _made[3] = Clone(selRow, holder, "CPU Clock", "CPU Clock");
            _made[3].GetComponent<Selectable>().enabled = false;   // a readout: never selected, never navigated to, no pointer events
            if (OvlRow) _made[4] = Clone(togRow, holder, "Performance Overlay", "Performance Overlay");   // kit #7
            if (RearRow) _made[5] = Clone(togRow, holder, "Rear Touch", "Rear Touch (L2 R2 L3 R3)");                   // kit #8
            if (PaceRow) _made[6] = Clone(togRow, holder, "Frame Pacing", "Frame Pacing (30 / 20 / 15)");
            if (BakRow) _made[7] = Clone(selRow, holder, "Restore Save Backup", "Restore Save Backup");
            if (K10Rows)
            {   // kit #10 (track cache)
                _made[8] = Clone(selRow, holder, "Cache Size", "Cache Size");
                _made[9] = Clone(selRow, holder, "Cache In Use", "Cache In Use");
                _made[9].GetComponent<Selectable>().enabled = false;   // a readout, like CPU Clock
                _made[10] = Clone(selRow, holder, "Clear Cache", "Clear Cache");
                _made[11] = Clone(selRow, holder, "Verify Files", "Verify Files");
            }
            foreach (var r in _made) r?.SetParent(content, false);
        }
        finally { UObject.Destroy(holder); }

        _tog = _made[1].GetComponentInChildren(vsync.GetType(), true); _togRow = _made[1].GetComponent<Selectable>();
        _speed = _made[2].GetComponentInChildren(fps.GetType(), true); _speedRow = _made[2].GetComponent<Selectable>();
        _clock = _made[3].GetComponentInChildren(fps.GetType(), true);
        if (OvlRow) { _ovl = _made[4].GetComponentInChildren(vsync.GetType(), true); _ovlRow = _made[4].GetComponent<Selectable>(); }
        if (RearRow) { _rearTog = _made[5].GetComponentInChildren(vsync.GetType(), true); _rearRow = _made[5].GetComponent<Selectable>(); }   // kit #8
        if (PaceRow) { _paceTog = _made[6].GetComponentInChildren(vsync.GetType(), true); _paceRow = _made[6].GetComponent<Selectable>(); }
        if (BakRow) { _bakSel = _made[7].GetComponentInChildren(fps.GetType(), true); _bakRow = _made[7].GetComponent<Selectable>(); }
        if (K10Rows) K10Build(fps.GetType());   // kit #10
        _clockText = Get(_clock, "_text"); _clockHelper = Get(_clock, "_textHelper");
        var cs = _made[3].GetComponent<Selectable>();
        Call(cs, "SetNormalTransitionState", null, null);   // the label in the rows' normal colour
        SetProp(_clock, "LocalizeContent", false);
        Call(_clock, "PrefillContent", typeof(string[]), new[] { "" });   // one item: no arrows

        _state = Host(0, 0);
        _gameAsm ??= gs.GetType().Assembly;
        if (_menu == null) _menu = gs;   // (the menu's controller not seen: the tab itself marks the menu open)
        CheckReset();                    // a Reset All on another tab since the menu opened: the defaults, before the rows show them
        if (!ProbeAtStart && (_state & StClocks) != 0 && _arm500 < 0)
        {   // is 500 MHz there at all? asked once a session, then back to the owner's choice (CLK lines) -- SHEPHERD_K6_ARM500_PROBE=tab only:
            // by default the launch asked at start (Boot)
            TrySpeed(500, "is it available? asked once");
            if (_arm500 == 1 && _mhz == 444) Host(2, 444);
        }
        Configure(true);
        _tog.GetType().GetField("OnValueChanged").SetValue(_tog, new Action<bool>(OnCore3));
        _speed.GetType().GetField("OnSelectionChanged").SetValue(_speed, new Action<int>(OnSpeed));
        if (_ovl != null) _ovl.GetType().GetField("OnValueChanged").SetValue(_ovl, new Action<bool>(OnOverlay));
        if (_rearTog != null) _rearTog.GetType().GetField("OnValueChanged").SetValue(_rearTog, new Action<bool>(OnRear));   // kit #8
        if (_paceTog != null) _paceTog.GetType().GetField("OnValueChanged").SetValue(_paceTog, new Action<bool>(OnPacing));
        if (_bakSel != null) _bakSel.GetType().GetField("OnSelectionChanged").SetValue(_bakSel, new Action<int>(OnRestore));
        if (_cacheSel != null) _cacheSel.GetType().GetField("OnSelectionChanged").SetValue(_cacheSel, new Action<int>(OnCacheSize));   // kit #10
        var onShow = gs.GetType().GetField("OnShow", BindingFlags.Instance | BindingFlags.Public);   // UIMenuBase.OnShow: after the tab's OnShowStarted
        onShow?.SetValue(gs, Delegate.Combine((Delegate)onShow.GetValue(gs), new Action(OnTabShow)));
        _t0 = Time.Realtime; _f0 = Time.FrameCount; _next = _t0 + 1.0; _shownMhz = _shownFps = -1;
        Live = true;
        Readout(Host(3, 0), -1);
        Log.Info("[k6] Settings > Graphics: PS Vita Settings rows added after '" + after + "' (clones of '" + head.name + "', '" + togRow.name + "', '" + selRow.name +
                 "'; navigation " + _togRow.navigation.mode + "): 4th CPU Core " + OnOff((bool)Get(_tog, "_value")) + ((_state & StOpen) == 0 ? " (not available)" : "") +
                 ", CPU Speed " + (_k9Shown ?? ((_arm500 == 1 && _mhz == 500 ? "500" : "444") + " MHz" + (_arm500 == 1 ? "" : (_state & StClocks) == 0 ? " (clocks not the app's)" : " (500 not available)"))) +
                 (_ovl != null ? ", Performance Overlay " + OnOff(_overlay) : "") +
                 " | host: " + StateNote(_state));
    }

    /// <summary>The rows' values and greying from the owner's choices and what the host can do (also after the game's Reset All);
    /// <paramref name="show"/>: see <see cref="Grey"/>.</summary>
    static void Configure(bool show)
    {
        SetToggle((_state & StOpen) != 0 && _core3, show);
        if (_ovl != null) SetToggleOf(_ovl, _overlay, show);
        if (_rearTog != null) SetToggleOf(_rearTog, _rear, show);   // kit #8
        if (_paceTog != null) SetToggleOf(_paceTog, _pacing, show);
        if (_bakSel != null) BakItems();
        if (_cacheSel != null) K10Configure();   // kit #10
        bool clocks = (_state & StClocks) != 0, can500 = clocks && _arm500 == 1;
        SetProp(_speed, "LocalizeContent", false);
        SetProp(_speed, "ContentIndex", 0);   // (first: the selector shows _content[index], and a shorter list must not be read past its end)
        Call(_speed, "PrefillContent", typeof(string[]), can500 ? new[] { "444 MHz", "500 MHz" } : new[] { K9Note(clocks) ?? (clocks ? 444 : (_state >> 16) & 0xfff) + " MHz" });
        SetProp(_speed, "ContentIndex", can500 && _mhz == 500 ? 1 : 0);
        Grey(show);
    }

    /// <summary>Kit #9: the greyed CPU Speed row's plain note when 500 MHz is the choice but the app cannot ask for it (null = kit #8's text):
    /// the clock shown (Host op 3: the measured clock once it settled, else the report) says whether a PSVshellPlus profile runs the cores at
    /// ~500 already.</summary>
    static string K9Note(bool clocks)
    {
        if (!K9Default500 || !clocks || _arm500 == 1 || _mhz != 500) { _noteHi = -1; _k9Shown = null; return null; }
        _noteHi = Host(3, 0) >= 490 ? 1 : 0;
        var note = _noteHi == 1 ? "500 MHz (PSVshellPlus)" : "444 MHz (500: PSVshellPlus)";
        if (note != _k9Shown) Log.Info("[k9] CPU Speed row (greyed): '" + note + "'" + (_noteHi == 1 ? " -- a PSVshellPlus profile runs the cores at ~500 (the measured clock)" : " -- 500 MHz needs a PSVshellPlus profile for this app"));
        return _k9Shown = note;
    }
    static string _k9Shown;   // kit #9: the note the row shows now (null = kit #8's text)

    static Transform RowOf(Component c, Transform content)
    {
        if (c == null || content == null) return null;
        var t = c.transform;
        while (t != null && t.parent != content) t = t.parent;
        return t;
    }

    static object FindTmp(Transform t)
    {
        foreach (var c in t.GetComponentsInChildren<Component>(true))
            if (c != null && c.GetType().Name == "TextMeshProUGUI") return c;
        return null;
    }

    /// <summary>A clone of one of the tab's rows under the inactive holder: the I2 Localize components (they would put the game's term back
    /// on every language change) and disablePerPlatform (it hides a row on low environment detail) removed, its label set.</summary>
    static Transform Clone(Transform src, GameObject holder, string name, string label)
    {
        var go = UObject.Instantiate(src.gameObject, holder.transform, false);
        go.name = name;
        foreach (var c in go.GetComponentsInChildren<Component>(true))
            if (c != null && (c.GetType().FullName == "I2.Loc.Localize" || c.GetType().FullName == "disablePerPlatform")) UObject.DestroyImmediate(c);
        _tmpText.SetValue(FindTmp(go.transform), label);
        return go.transform;
    }

    /// <summary>The row's MMSelectable_*.Interactable (the game's own greying: the label in the disabled colour, the control at half alpha,
    /// skipped by navigation), only when it differs.</summary>
    static void SetInteractable(Selectable row, bool on)
    {
        if (row != null && row.interactable != on)
            row.GetType().GetProperty("Interactable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)?.SetValue(row, on);
    }
    /// <summary>The greying the host's answers ask for. Every show of the tab (UIMenuBase.SetActiveStateForMenu) makes all its rows
    /// interactable, and the game then greys its own Target Frame Rate row again in OnShowStarted: these rows do the same from the tab's
    /// OnShow, which the game invokes right after OnShowStarted (and once a second in <see cref="Tick"/>, for the menu's own
    /// SetActiveStateForMenu(true) when a pushed dialog is released). <paramref name="show"/> = the rows' build or a show of the tab: an
    /// available row is made interactable too; otherwise (Tick, Reset All, a CPU Speed change) a row is only greyed, never lit -- a
    /// pushed dialog (Reset All's confirmation) has the menu's SetActiveStateForMenu(false) dim every row, and lighting one again is the
    /// game's own SetActiveStateForMenu(true) at its release (review fix: kit #6's Tick lit them under the dialog).</summary>
    static void Grey(bool show)
    {
        bool tog = (_state & StOpen) != 0, speed = (_state & StClocks) != 0 && _arm500 == 1;
        if (show || !tog) SetInteractable(_togRow, tog);
        if (show || !speed) SetInteractable(_speedRow, speed);
        if (show && _ovlRow != null) SetInteractable(_ovlRow, true);   // kit #7: always available (a GL app)
        if (show) { SetInteractable(_rearRow, true); SetInteractable(_paceRow, true); SetInteractable(_bakRow, true); }   // kit #8: always available
        if (show) { SetInteractable(_cacheRow, true); SetInteractable(_clearRow, true); }   // kit #10: always available
        if (_verifyRow != null && (show || !K10Cache.Verify.Available)) SetInteractable(_verifyRow, K10Cache.Verify.Available);   // (greyed without the io core)
    }
    static void OnTabShow() { if (Live) Grey(true); }

    // ---------------------------------------------------------------- FullScreen Mode (kit #6 integration, owner decision: FsFixed)

    static Component _fsSel;      // the tab's _fullScreenModeSelector (MMHorizontalSelector)
    static Selectable _fsRow;     // its row (MMSelectable_HorizontalSelector)
    const int FsFullScreen = 2;   // the game's _fullscreenModes: 0 Windowed, 1 Borderless Windowed, 2 FullScreen (ScreenUtilities.GetFullScreenMode)
    static bool _fsLogged;

    /// <summary>SettingsData.Graphics.FullscreenMode = FullScreen when it is anything else (a PC settings file); true = it was changed.</summary>
    static bool FixFullscreenData()
    {
        try
        {
            var g = GraphicsData(); var f = g?.GetType().GetField("FullscreenMode");
            if (f == null || f.GetValue(g) is not int v || v == FsFullScreen) return false;
            f.SetValue(g, FsFullScreen);
            Log.Info("[k6] FullScreen Mode: the saved setting " + (v == 0 ? "Windowed" : v == 1 ? "Borderless Windowed" : v.ToString()) + " is shown and applied as FullScreen (a handheld)");
            return true;
        }
        catch (Exception e) { Log.Warn("[k6] FullScreen Mode: the setting not read (" + e.GetBaseException().Message + ")"); return false; }
    }

    /// <summary>The Graphics tab's Start (after the game's Configure set the rows from the settings): FullScreen shown, the row greyed the
    /// game's way, again at every show of the tab (UIMenuBase.OnShow) and every frame while the menu is open (Tick), the selector's value
    /// guarded (OnFsChanged in place of the game's handler).</summary>
    static void FixFullscreenRow(MonoBehaviour gs)
    {
        try
        {
            _gameAsm ??= gs.GetType().Assembly;
            if (_menu == null) { _menu = gs; _next = Time.Realtime + 1.0; Live = true; }   // (the menu's controller not seen: the tab marks the menu open)
            FixFullscreenData();
            _fsSel = Get(gs, "_fullScreenModeSelector") as Component;
            _fsRow = RowOf(_fsSel, (Get(gs, "_scrollRect") as ScrollRect)?.content)?.GetComponent<Selectable>();
            if (_fsSel == null || _fsRow == null) throw new InvalidOperationException("the FullScreen Mode row not found");
            if (_fsSel.GetType().GetProperty("ContentIndex", I)?.GetValue(_fsSel) is int ci && ci != FsFullScreen) SetProp(_fsSel, "ContentIndex", FsFullScreen);
            SetInteractable(_fsRow, false);
            // (kit #6 review fix) the value guarded, not only the greying: a pushed dialog's release (the game's SetActiveStateForMenu(true):
            // Reset All's confirmation, the Resolution dropdown below this row) lights every row of the tab, this one too, until it is
            // greyed again (Tick, every frame) -- a left / right press in that moment ran the game's OnFullscreenModeSelectorValueChanged
            // (combined in the tab's Start), which stores the index and sets Screen.fullScreenMode. That handler is replaced by OnFsChanged.
            _fsSel.GetType().GetField("OnSelectionChanged").SetValue(_fsSel, new Action<int>(OnFsChanged));
            var onShow = gs.GetType().GetField("OnShow", BindingFlags.Instance | BindingFlags.Public);
            onShow?.SetValue(gs, Delegate.Combine((Delegate)onShow.GetValue(gs), new Action(OnTabShowFs)));
            if (!_fsLogged) { _fsLogged = true; Log.Info("[k6] Settings > Graphics: FullScreen Mode greyed out and fixed at FullScreen (Windowed / Borderless never selectable; SHEPHERD_K6_FULLSCREEN=0: the PC row)"); }
        }
        catch (Exception e) { _fsSel = null; _fsRow = null; Log.Warn("[k6] Settings > Graphics: FullScreen Mode left as the game shows it (" + e.GetBaseException().Message + ")"); }
    }
    static void OnTabShowFs() { if (_fsRow != null) SetInteractable(_fsRow, false); }
    /// <summary>(kit #6 review fix) The FullScreen Mode selector's OnSelectionChanged in place of the game's handler. MMHorizontalSelector
    /// raises it from its arrow handlers after moving its index (FullScreen is the last item, so only a left press -- possible only while a
    /// released dialog has relit the row -- gets here, with 0 Windowed or 1 Borderless Windowed): the index goes straight back to FullScreen
    /// (ContentIndex: the arrow handler then draws _content[_contentIndex] = FullScreen), the setting stays FullScreen, the game's handler
    /// (store the index, set Screen.fullScreenMode) never runs, and the row is greyed again at once.</summary>
    static void OnFsChanged(int index)
    {
        if (index != FsFullScreen && _fsSel != null) SetProp(_fsSel, "ContentIndex", FsFullScreen);
        FixFullscreenData();
        if (_fsRow != null) SetInteractable(_fsRow, false);
        if (index != FsFullScreen) Log.Info("[k6] FullScreen Mode: " + (index == 0 ? "Windowed" : index == 1 ? "Borderless Windowed" : index.ToString()) + " refused (a released dialog had made the row selectable): the row back to FullScreen, the setting unchanged (a handheld)");
    }

    /// <summary>MMToggle's value without its OnValueChanged. <paramref name="instant"/> (the rows' build: the clone starts from the VSync
    /// row's handle; a greyed row): drawn at once. Otherwise (Reset All) through MMToggle.Value, as the game's own Configure sets its toggles
    /// after Reset All: a change slides (0.15 s), no change does nothing (review fix: kit #6 snapped).</summary>
    static void SetToggle(bool on, bool instant) => SetToggleOf(_tog, on, instant);
    static void SetToggleOf(Component tog, bool on, bool instant)
    {
        if (!instant) { SetProp(tog, "Value", on); return; }
        tog.GetType().GetField("_value", I).SetValue(tog, on);
        Call(tog, "UpdateState", typeof(bool), true);
    }

    /// <summary>Kit #7: the Performance Overlay row changed (Render/K7Overlay.cs draws from the next frame).</summary>
    static void OnOverlay(bool on)
    {
        _overlay = on;
        Render.K7Overlay.SetChoice(on);
        Log.Info("[k7] Performance Overlay " + OnOff(on) + (Render.K7Overlay.On == on ? "" : " (SHEPHERD_K7_OVERLAY in the env file decides)"));
        Save();
    }

    // ---------------------------------------------------------------- kit #8 rows (track play)

    /// <summary>The backup selector's items: "No" + the slots' backups, newest first (K8Play.SlotBackups), the chosen restore selected.</summary>
    static void BakItems()
    {
        var files = K8Play.SlotBackups();
        if (_restore != null && K8Play.Pinned(_restore))
        {   // (integration review: the chosen backup was pruned from save-backups/ since the choice -- its pinned copy keeps it listed and chosen)
            bool listed = false;
            foreach (var f in files) if (f == _restore) { listed = true; break; }
            if (!listed) { var l = new string[files.Length + 1]; files.CopyTo(l, 0); l[files.Length] = _restore; files = l; }
        }
        var items = new string[files.Length + 1]; _bakItems = new string[files.Length + 1];
        items[0] = files.Length == 0 ? "No (no backups yet)" : "No";
        int sel = 0;
        for (int i = 0; i < files.Length; i++) { items[i + 1] = K8Play.BackupLabel(files[i]); _bakItems[i + 1] = files[i]; if (files[i] == _restore) sel = i + 1; }
        if (sel == 0) _restore = null;   // (a chosen backup that is gone: nothing to restore)
        SetProp(_bakSel, "LocalizeContent", false);
        SetProp(_bakSel, "ContentIndex", 0);
        Call(_bakSel, "PrefillContent", typeof(string[]), items);
        SetProp(_bakSel, "ContentIndex", sel);
    }
    static void OnRear(bool on) { _rear = on; Impl.VitaPad.RearOn = on; Log.Info("[k8] Rear Touch " + OnOff(on) + " (the rear panel's corners as L2 / R2 / L3 / R3)"); Save(); }
    static void OnPacing(bool on) { _pacing = on; K8Play.PacingChoice = on; Log.Info("[k8] Frame Pacing " + OnOff(on) + (on ? " (each area locked at 30 / 20 / 15 fps from its own frame times)" : " (frames as fast as they come, kit #7)")); Save(); }
    static void OnRestore(int index)
    {
        _restore = index > 0 && index < _bakItems.Length ? _bakItems[index] : null;
        K8Play.Pin(_restore);   // (integration review: kept out of the pruning until the restore; null = "No": the old pin removed)
        Log.Info("[k8] Restore Save Backup: " + (_restore == null ? "No" : _restore + " -- restored at the next start, before the game reads its saves (the file it replaces is backed up first)"));
        Save();
    }

    // ---------------------------------------------------------------- kit #10 rows (track cache)

    static string MbText(int mb) => mb >= 1024 ? (mb / 1024).ToString() + " GB" : mb.ToString() + " MB";

    /// <summary>The four kit #10 rows' components and handlers (Build): the selectors' texts (as the CPU Clock readout), the two action rows'
    /// confirm (MMSelectable.OnConfirm + Confirmable: the X button, with the game's confirm sound).</summary>
    static void K10Build(Type selType)
    {
        _cacheSel = _made[8].GetComponentInChildren(selType, true); _cacheRow = _made[8].GetComponent<Selectable>();
        _useSel = _made[9].GetComponentInChildren(selType, true);
        _clearSel = _made[10].GetComponentInChildren(selType, true); _clearRow = _made[10].GetComponent<Selectable>();
        _verifySel = _made[11].GetComponentInChildren(selType, true); _verifyRow = _made[11].GetComponent<Selectable>();
        _useText = Get(_useSel, "_text"); _useHelper = Get(_useSel, "_textHelper");
        _clearText = Get(_clearSel, "_text"); _clearHelper = Get(_clearSel, "_textHelper");
        _verifyText = Get(_verifySel, "_text"); _verifyHelper = Get(_verifySel, "_textHelper");
        Call(_made[9].GetComponent<Selectable>(), "SetNormalTransitionState", null, null);   // (the readout's label in the rows' normal colour)
        foreach (var c in new[] { _useSel, _clearSel, _verifySel }) { SetProp(c, "LocalizeContent", false); Call(c, "PrefillContent", typeof(string[]), new[] { "" }); }   // one item: no arrows
        SetConfirm(_clearRow, new Action(OnClear));
        SetConfirm(_verifyRow, new Action(OnVerify));
        _useShown = _clearShown = _verifyShown = null; _clearSeen = K10Cache.ClearState;
        K10Cache.EnsureScanned();
        K10Refresh();
    }
    static void SetConfirm(Selectable row, Action a)
    {
        row.GetType().GetField("OnConfirm")?.SetValue(row, a);
        row.GetType().GetProperty("Confirmable")?.SetValue(row, true);
    }
    static void K10Configure()
    {
        SetProp(_cacheSel, "LocalizeContent", false);
        SetProp(_cacheSel, "ContentIndex", 0);
        var items = new string[K10Cache.SizesMb.Length];
        for (int i = 0; i < items.Length; i++) items[i] = MbText(K10Cache.SizesMb[i]);
        Call(_cacheSel, "PrefillContent", typeof(string[]), items);
        SetProp(_cacheSel, "ContentIndex", Math.Max(0, Array.IndexOf(K10Cache.SizesMb, _cacheMb)));
    }
    static void K10Forget()
    {
        _cacheSel = _useSel = _clearSel = _verifySel = null; _cacheRow = _clearRow = _verifyRow = null;
        _useText = _useHelper = _clearText = _clearHelper = _verifyText = _verifyHelper = null;
    }
    static void SetText(object a, object b, string s) { _tmpText.SetValue(a, s); _tmpText.SetValue(b, s); }
    /// <summary>About once a second while the rows exist (and right after a change): the readout, Clear Cache's and Verify Files' value texts --
    /// set only when they change.</summary>
    static void K10Refresh()
    {
        var use = K10Cache.InUseText();
        if (use != _useShown) { if (_useShown == null || _useShown == "...") Log.Info("[k10] Cache In Use readout '" + use + "' (Cache Size " + MbText(_cacheMb) + "; refreshed about once a second, logged once per show)"); _useShown = use; SetText(_useText, _useHelper, use); }
        int cs = K10Cache.ClearState;
        string clear = cs == 1 ? "Clearing..." : cs != _clearSeen && cs == 2 ? "Done" : cs != _clearSeen && cs == 3 ? "Done (" + K10Cache.ClearFailed.ToString() + " kept)" : "";
        if (clear != _clearShown) { _clearShown = clear; SetText(_clearText, _clearHelper, clear); }
        string ver;
        if (!K10Cache.Verify.Available) ver = "Not in this build";
        else if (K10Cache.Verify.State == 1) ver = "Checking...";
        else if (K10Cache.Verify.State == 2) { ver = K10Cache.Verify.Ok ? "OK" : K10Cache.Verify.Stopped ? "Stopped" : "Problem found"; if (_verifyState == 1) { _verifyState = 2; VerifyDone(K10Cache.Verify.Result ?? ""); } }
        else ver = "";
        if (ver != _verifyShown) { _verifyShown = ver; SetText(_verifyText, _verifyHelper, ver); }
    }

    static void OnCacheSize(int index)
    {
        if (index < 0 || index >= K10Cache.SizesMb.Length) return;
        _cacheMb = K10Cache.SizesMb[index];
        K10Cache.SetCap(_cacheMb);
        Log.Info("[k10] Cache Size " + MbText(_cacheMb) + " (the learning cache's cap: least recently used entries go first; the card always keeps its free-space margin)");
        Save();
    }

    /// <summary>Clear Cache (X): the game's own confirmation window (its Reset All's: UIManager.ConfirmationWindowTemplate pushed by the Settings
    /// menu); confirmed = the cache folder deleted on the cache worker.</summary>
    static void OnClear()
    {
        if (K10Cache.ClearState == 1) return;
        // (review fix 2026-10-28: the catalog bake the kit staged in cache/json/ is kept -- K10Cache.ClearKeep -- and the amount is what goes)
        var w = PushConfirm("Clear Cache", "Delete Shepherd's cache (" + K10Cache.ClearableText() + " in ux0:data/shepherd/cache)? Saves, settings and the game catalog's bake are kept. The next start rebuilds what it needs, so it may load more slowly once.", false);
        if (w == null) return;
        AddAction(w, "OnConfirm", new Action(ClearConfirmed));
        Log.Info("[k10] Clear Cache: the confirmation shown");
    }
    static void ClearConfirmed() { Log.Info("[k10] Clear Cache: confirmed"); K10Cache.RequestClear(); _clearSeen = -1; if (_useSel != null) K10Refresh(); }

    /// <summary>Verify Files (X): the io track's verify core (K10Cache.Verify), after the game's confirmation window; its progress in the row,
    /// its result in an accept-only window when it ends while the rows are shown.</summary>
    static void OnVerify()
    {
        if (!K10Cache.Verify.Available || K10Cache.Verify.State == 1) return;
        var w = PushConfirm("Verify Files", "Check every game file on the memory card against its stored hash? This reads the whole game once and can take a few minutes; the game keeps running.", false);
        if (w == null) return;
        AddAction(w, "OnConfirm", new Action(VerifyConfirmed));
    }
    static void VerifyConfirmed()
    {
        if (K10Cache.Verify.Begin()) { _verifyState = 1; Log.Info("[k10] Verify Files: started (the io track's core, on the cache worker)"); }
        else Log.Info("[k10] Verify Files: the core did not start");
        if (_useSel != null) K10Refresh();
    }
    static void VerifyDone(string result)
    {
        Log.Info("[k10] Verify Files: " + result);
        PushConfirm("Verify Files", result, true);
    }

    /// <summary>The game's confirmation window, pushed by the open Settings menu (UIMenuBase.Push&lt;T&gt;(UIManager.Instance.ConfirmationWindowTemplate)
    /// then Configure(header, body, acceptOnly) -- what its Reset All does). English only, as every PS Vita row. null = not shown (logged).</summary>
    static object PushConfirm(string header, string body, bool acceptOnly)
    {
        try
        {
            var menu = _menu;
            var ui = _gameAsm?.GetType("Lamb.UI.UIManager");
            var inst = ui?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null);
            var tmpl = inst?.GetType().GetProperty("ConfirmationWindowTemplate", I)?.GetValue(inst);
            if (menu == null || tmpl == null) throw new InvalidOperationException("the Settings menu or the game's confirmation window not found");
            MethodInfo push = null;
            foreach (var mi in menu.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (mi.Name == "Push" && mi.IsGenericMethodDefinition && mi.GetParameters().Length == 1) { push = mi; break; }
            if (push == null) throw new InvalidOperationException("UIMenuBase.Push not found");
            var w = push.MakeGenericMethod(tmpl.GetType()).Invoke(menu, new[] { tmpl });
            w.GetType().GetMethod("Configure", I, null, new[] { typeof(string), typeof(string), typeof(bool) }, null).Invoke(w, new object[] { header, body, acceptOnly });
            return w;
        }
        catch (Exception e) { Log.Warn("[k10] " + header + ": the confirmation window was not shown (" + e.GetBaseException().Message + ")"); return null; }
    }
    static void AddAction(object w, string field, Action a)
    {
        var f = w.GetType().GetField(field, I);
        f.SetValue(w, Delegate.Combine((Delegate)f.GetValue(w), a));
    }

    // ---------------------------------------------------------------- the owner's changes

    static void OnCore3(bool on)
    {
        if ((_state & StOpen) == 0) { SetToggle(false, true); return; }   // (greyed out: the game never calls this then)
        _core3 = on;
        _state = Host(1, on ? 1 : 0);
        Log.Info("[k6] 4th CPU Core " + OnOff(on) + ": " + StateNote(_state));
        Save();
    }

    static void OnSpeed(int index)
    {
        int want = index == 1 ? 500 : 444;
        int back = TrySpeed(want, "the owner's choice");
        if (want == 500 && back != 500) { _mhz = 444; Configure(false); }   // went away since the probe: shown as not available
        else _mhz = want;
        _mhzSaved = true;   // kit #9 review: the owner's choice (written with the kit #9 mark)
        Readout(back, -1);
        Save();
    }

    // ---------------------------------------------------------------- once a second, while the rows exist

    /// <summary>Boot loop, every frame: nothing unless the Settings menu is open; then about once a second (realtimeSinceStartup) the
    /// game's Reset All check (any tab, the rows built or not; once more when the menu is gone) and, while the rows exist, the greying and
    /// the readout.</summary>
    public static void Tick()
    {
        // (kit #6 review fix) FullScreen Mode greyed again EVERY frame while the menu is open, before the once-a-second pacing below: a
        // pushed dialog's release (the game's SetActiveStateForMenu(true): Reset All's confirmation, the Resolution dropdown) lights every
        // row of the tab, and the once-a-second pass alone left this one selectable for up to a second (OnFsChanged guards the value even
        // then). One property read a frame while the Settings menu is open; it only ever greys.
        if (_fsRow != null && _fsRow.interactable) SetInteractable(_fsRow, false);
        double now = Time.Realtime;
        if (now < _next) return;
        _next = now + 1.0;
        bool reset = CheckReset();
        if (_menu == null)
        {   // the Settings menu closed (its last Reset All, if any, taken above)
            Live = false; _menu = null; _tog = _speed = _clock = _ovl = null; _togRow = _speedRow = _ovlRow = null; _clockText = _clockHelper = _gfxData = null;
            _rearTog = _paceTog = _bakSel = null; _rearRow = _paceRow = _bakRow = null;   // kit #8
            K10Forget();                                                                   // kit #10
            _fsSel = null; _fsRow = null;
            if (K10Cache.On) K10Cache.SetWindow(false, false);   // kit #10: the Settings menu's idle window closes
            return;
        }
        if (K10Cache.On) { K10Cache.SetWindow(true, false); K10Cache.FlushSoon(); }   // kit #10: the open Settings menu is an idle window (cache writes)
        if (_clock == null || _tog == null) return;   // the rows: not built yet (the Graphics tab not shown in this menu) or not added
        if (_useSel != null) K10Refresh();   // kit #10: the readout, Clear Cache's / Verify Files' state
        if (reset) Configure(false);
        Grey(false);
        int fps = (int)Math.Round((Time.FrameCount - _f0) / Math.Max(1e-6, now - _t0));
        _t0 = now; _f0 = Time.FrameCount;
        int mhzNow = Host(3, 0);
        Readout(mhzNow, Time.Wall ? fps : -1);
        if (_noteHi >= 0 && (mhzNow >= 490 ? 1 : 0) != _noteHi) Configure(false);   // kit #9: the measured clock settled on the other side: the row's note follows
    }

    static void Readout(int mhz, int fps)
    {
        if (mhz == _shownMhz && fps == _shownFps) return;
        _shownMhz = mhz; _shownFps = fps;
        var s = fps >= 0 ? mhz + " MHz / " + fps + " fps" : mhz + " MHz";
        _tmpText.SetValue(_clockText, s); _tmpText.SetValue(_clockHelper, s);
        if (fps >= 0 && !_fpsLogged) { _fpsLogged = true; Log.Info("[k6] CPU Clock readout '" + s + "' (refreshed about once a second while the rows exist; not logged again)"); }
    }

    /// <summary>SettingsManager.Settings.Graphics (the game's Reset All puts a new object there).</summary>
    static object GraphicsData()
    {
        try
        {
            _settingsProp ??= _gameAsm?.GetType("SettingsManager")?.GetProperty("Settings", BindingFlags.Static | BindingFlags.Public);
            var settings = _settingsProp?.GetValue(null);
            if (settings == null) return null;
            _graphicsField ??= settings.GetType().GetField("Graphics");
            return _graphicsField?.GetValue(settings);
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- the host (host.c shepherd_vita_k6)

    static int Host(int op, int arg) =>
#if SHEPHERD_EBOOT
        HostNative(op, arg);
#else
        Diag.VitaHost.On ? HostNative(op, arg) : Sim(op, arg);
#endif
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int HostNative(int op, int arg) => shepherd_vita_k6(op, arg);
    [DllImport("__Internal")]
    static extern int shepherd_vita_k6(int op, int arg);

#if !SHEPHERD_EBOOT
    // The Mac stand-in for the host's export (no Vita host): a Vita with the core-unlock plugin and a stock 444 MHz kernel by default.
    static bool _simUse = true;
    static int _simArm = 444;
    static int Sim(int op, int arg)
    {
        bool open = Environment.GetEnvironmentVariable("SHEPHERD_K6_SIM_CORE3") != "0", clocks = Environment.GetEnvironmentVariable("SHEPHERD_CLOCKS") != "0";
        if (op == 1) { _simUse = arg != 0; Log.Info($"[k6] (Mac stand-in for the host) 4th CPU Core {(arg != 0 ? "On" : "Off")}: the workers -> {(open && _simUse ? "CPU3" : "USER_2")}"); }
        if (op == 2 && clocks)
        {
            if (arg <= 444 || (arg <= 500 && Environment.GetEnvironmentVariable("SHEPHERD_K6_SIM_ARM500") == "1")) _simArm = arg;
            Log.Info($"[k6] (Mac stand-in for the host) CLK set arm {arg} MHz: read back {_simArm} MHz");
        }
        return op switch
        {
            0 or 1 => StPin | (open ? StOpen : 0) | (open && _simUse ? StOnCpu3 : 0) | (clocks ? StClocks : 0) | (333 << 16),
            2 or 3 => clocks ? _simArm : 333,
            _ => -1,
        };
    }
#endif
}

/// <summary>kit #6 toggles: Native/Objects.cs GiveBack's switch -- the snapshot pool's size class by the snapshot's count, as Rent picks it
/// (the rows' extra transforms put the Graphics tab's transform snapshots in the band where kit #5's length rule re-allocated a buffer at
/// every snapshot: vlx +87 KB of large objects in most scrolling frames). Allocation only, every profile; SHEPHERD_K6_SNAPPOOL=0 = kit #5's
/// rule. A [ProfileOnly] type of its own: the switch's name is no literal of the engine's core types (Assets/Diet.cs), and the pool (used
/// from the first scene on) loads no Unity type through it.</summary>
[ProfileOnly]
public static class K6Cfg
{
    public static readonly bool SnapByCount = Environment.GetEnvironmentVariable("SHEPHERD_K6_SNAPPOOL") != "0";
    /// <summary>kit #6 toggles review: Lifecycle keeps Active in a stable order (behaviours of the same execution order in the order they were
    /// enabled, as Unity's per-order update lists; Lifecycle.SortActiveStable) instead of kit #5's unstable List.Sort. Every profile.
    /// Kit #6 integration: ON by default (vita/notes/kit6-integration.md, "SHEPHERD_K6_STABLE_ORDER"): kit #5's introsort reorders the ~1,800
    /// order-0 behaviours whenever an unrelated object is enabled or destroyed (the toggles review's VLX evidence: animation phases shift
    /// after a Settings visit), which Unity's append-only per-order lists cannot do; the frame-clock references are re-based once anyway
    /// (the Devolver splash decision). <c>SHEPHERD_K6_STABLE_ORDER=0</c> = kit #5's unstable sort.</summary>
    public static readonly bool StableOrder = Environment.GetEnvironmentVariable("SHEPHERD_K6_STABLE_ORDER") != "0";
}
