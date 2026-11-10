using System.Reflection;
using Shepherd.Engine.Native;
using UnityEngine;
using UnityEngine.UI;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #12, track menusb (vita/notes/kit12-menusb.md): the owner's two small-screen rows in Settings > Game > PS Vita
/// (roadmap-kits.md, ideas of 2026-10-17):
/// <list type="bullet">
/// <item><b>Cutscene Bars: On / Off</b> (default On = the original look). Off: only the two bar images of the game's LetterBox (its Top and
/// Bottom: their LetterBox_Top / LetterBox_Bottom image children) are not drawn -- their CanvasRenderers culled, as a RectMask2D culls -- so LetterBox.IsPlaying, the camera's cutscene moves, the
/// hidden HUD, the skip prompt and the subtitles (children of the bars) are all exactly the original's; a frame shows the picture the bars
/// covered. <c>SHEPHERD_K12_BARS=0</c> = no row, bars always drawn (kit #11).</item>
/// <item><b>Camera: Original / Closer / Closest</b> (default Original) + <b>Original Camera in Fights: On</b>: in the PROJECTION only -- the
/// main camera's clip x / y scaled by 1/0.9 (Closer: ~90 % of the view, everything ~11 % bigger) or 1/0.8 (Closest: ~80 %, ~25 % bigger).
/// The camera's transform, field of view, zoom moves and limits are the game's own (CameraFollowTarget reads no projection); what reads
/// the projection follows it (WorldToScreenPoint markers, touch picking, culling -- "off-screen checks simply see fewer things"); a
/// Screen Space - Camera canvas on the main camera keeps its size on screen (PlaceInFrontOf divides by the same factor). Only in gameplay
/// (CameraFollowTarget.Instance exists: not the Main Menu, the title or loading screens; set at once when it appears). In a fight
/// (a closed room with enemies: RoomLockController.DoorsOpen false, Health.team2 not empty; read every 15th frame) the factor eases back to
/// 1 over ~0.5 s and out again after it. Original = the projection untouched (no multiply). <c>SHEPHERD_K12_CAMERA=0</c> = no rows (kit #11).</item>
/// </list>
/// Both under the track's master (<c>SHEPHERD_K12_MENUSB=0</c>) and the kit #12 Settings layout (the rows live in its Game tab section).</summary>
[ProfileOnly]
public static class K12View
{
    static string Env(string n) => Environment.GetEnvironmentVariable(n);
    /// <summary>Every kit #12 menusb switch, read here from the environment (and Profile, which reads nothing of this track): no static
    /// initialiser of this track reads another class that reads back. The first VLX gate failed on such a CYCLE (review fix, finding 11):
    /// VitaToggles.K12GameRows read K12View.BarsRow while K12View.BarsRow read VitaToggles.K12Layout -- whichever class starts initialising
    /// first sees the other's fields still false, and CoreCLR and Mono AOT happen to start them in a different order. A one-way read of
    /// another class's static is safe (that class is initialised first); a cycle is not, on any runtime.
    /// Layout = kit #6's rows on (SHEPHERD_K6_TOGGLES, as VitaToggles.On) and the track's master and SHEPHERD_K12_SETTINGS.</summary>
    public static readonly bool Layout = Profile.Vita && Env("SHEPHERD_K6_TOGGLES") != "0" && Env("SHEPHERD_K12_MENUSB") != "0" && Env("SHEPHERD_K12_SETTINGS") != "0";
    public static readonly bool Desc = Layout && Env("SHEPHERD_K12_DESC") != "0", About = Layout && Env("SHEPHERD_K12_ABOUT") != "0", Plug = Layout && Env("SHEPHERD_K12_PLUGINS_ROW") != "0";
    public static readonly bool BarsRow = Layout && Env("SHEPHERD_K12_BARS") != "0";
    public static readonly bool CamRow = Layout && Env("SHEPHERD_K12_CAMERA") != "0";
    public static bool Any => BarsRow || CamRow;
    public static readonly string[] CamKeys = { "original", "closer", "closest" };
    static readonly float[] CamView = { 1f, 0.9f, 0.8f };
    public static int Cam;                  // 0 Original, 1 Closer, 2 Closest (the owner's choice)
    public static bool Fights = true;       // Original Camera in Fights
    public static bool Bars = true;         // Cutscene Bars drawn
    /// <summary>The projection's x / y factor now (1 = untouched) and the camera it applies to (CameraIcalls.Proj).</summary>
    public static float CamK = 1f;
    public static NObj CamObj;
    static float _target = 1f;
    static bool _playLogged, _fightLogged;
    static bool _fight;
    static Component _lb; static CanvasRenderer _top, _bottom;

    public static string CamName(int i) => i == 1 ? "Closer" : i == 2 ? "Closest" : "Original";
    public static string Choices() => (CamRow ? "Camera " + CamName(Cam) + ", Original Camera in Fights " + (Fights ? "On" : "Off") : "") + (CamRow && BarsRow ? ", " : "") + (BarsRow ? "Cutscene Bars " + (Bars ? "On" : "Off") : "");
    public static void SetCam(int i) { Cam = i < 0 || i > 2 ? 0 : i; }
    public static void SetFights(bool on) { Fights = on; }
    public static void SetBars(bool on) { Bars = on || Render.K13Looks.BarsOnly; ApplyBars(); }   // (kit #13 looks, SHEPHERD_K13_BARS=1: only On)
    /// <summary>VitaToggles.Boot: the saved choices (vita-settings.txt) from the first frame.</summary>
    public static void Apply()
    {
        if (!Any) return;
        if (BarsRow && Render.K13Looks.BarsOnly) Bars = true;   // kit #13 looks (SHEPHERD_K13_BARS=1): a saved Off is read as On
        // (a saved Closer / Closest: applied when the Lamb first appears, at once -- Frame)
        Log.Info("[k12] " + Choices() + " (Settings > Game > PS Vita; SHEPHERD_K12_CAMERA=0 / SHEPHERD_K12_BARS=0: no rows)");
    }

    /// <summary>Every frame (Boot loop), only while a row is not at its default or easing: the camera's factor; the bars' cull kept.</summary>
    public static bool Active => (CamRow && (Cam != 0 || CamK != 1f)) || (BarsRow && !Bars && _lb != null);
    public static void Frame()
    {
        if (BarsRow && !Bars && _lb != null) { if (_top != null && !_top.cull) _top.cull = true; if (_bottom != null && !_bottom.cull) _bottom.cull = true; }
        if (!CamRow) return;
        // the gameplay / fight state: every 15th frame, and AT ONCE when it may be stale (review fix, finding 6) -- this pass did not run
        // last frame (Original with the view back: nothing ran, so a saved state is from an older scene), the choice changed, or the main
        // camera the factor applies to is no longer the main camera (a scene change: the next scene's camera is never zoomed before the check)
        int fc = Time.FrameCount;
        bool poll = Cam != 0 && (fc % 15 == 0 || fc != _lastF + 1 || Cam != _lastCam);
        _lastF = fc; _lastCam = Cam;
        NObj main = CamObj != null ? MainCam() : null;
        if (Cam != 0 && CamObj != null && main != CamObj) poll = true;
        if (poll) { bool had = _play; _play = InPlay(); _fight = _play && Fights && InFight(); if (_play && !had && !_fight) CamK = 1f / CamView[Cam]; }
        float want = Cam == 0 || !_play || _fight ? 1f : 1f / CamView[Cam];
        if (!_play) CamK = 1f;   // (menus, loading screens, the title: the original view -- the row is for gameplay)
        float step = Math.Max(0f, Time.UnscaledDeltaTime) / 0.5f * (1f / CamView[2] - 1f);   // the whole range in ~0.5 s of real time
        if (Cam == 0) CamK = want;   // (Original: at once, no multiply from the next frame)
        else if (CamK < want) CamK = Math.Min(want, CamK + step);
        else if (CamK > want) CamK = Math.Max(want, CamK - step);
        _target = want;
        var co = CamK != 1f ? main ?? MainCam() : null;
        if (co != CamObj || _play != _playLogged || _fight != _fightLogged)
        {   // (one line per change: gameplay starts / ends, a fight starts / ends, the camera record changes)
            _playLogged = _play; _fightLogged = _fight;
            Log.Info("[k12] Camera " + CamName(Cam) + ": " + (!_play ? "not in gameplay (the original view)" : _fight ? "a fight (easing back to the original view)" : "gameplay, the view x" + CamView[Cam].ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                     " -- camera " + (co is NComponent nc ? "'" + nc.Go?.Name + "'" : "none"));
        }
        CamObj = co;
    }
    static int _lastF = -2, _lastCam;
    static NObj MainCam() => Objects.Get<NBehaviour>(K8Speed.Lists ? Lookups.MainCamera() : null) ?? MainSlow();
    static NObj MainSlow()
    {
        foreach (var c in Registry.Cameras.Items()) if (c is NComponent nc && nc.Go?.Tag == "MainCamera") return c;
        return null;
    }

    // a fight: a closed room with enemies (RoomLockController.CloseAll / DoorUp; RoomCompleted opens them)
    static FieldInfo _doors, _locks, _team2, _player; static bool _fightRead, _play;
    /// <summary>Gameplay = the game's gameplay camera exists (CameraFollowTarget.Instance: the base, dungeons, the intro; not the Main Menu,
    /// the title or loading screens); read every 15th frame with the fight state. When it first appears the factor is set at once (no ease
    /// at a scene's start); without it the view is the original.</summary>
    static bool InPlay()
    {
        try
        {
            if (!_fightRead) InFight();
            return _player != null && _player.GetValue(null) is UnityEngine.Object o && o != null;
        }
        catch (Exception) { return false; }
    }
    static bool InFight()
    {
        try
        {
            if (!_fightRead)
            {
                _fightRead = true;
                var asm = _gameAsm;   // (the game's own assembly, from a type the engine runs: a lookup by name can find another copy with its own statics)
                if (asm == null) { _fightRead = false; return false; }
                var rl = asm?.GetType("RoomLockController"); var h = asm?.GetType("Health");
                _doors = rl?.GetField("DoorsOpen", BindingFlags.Static | BindingFlags.Public); _locks = rl?.GetField("RoomLockControllers", BindingFlags.Static | BindingFlags.Public);
                _team2 = h?.GetField("team2", BindingFlags.Static | BindingFlags.Public);
                _player = asm?.GetType("CameraFollowTarget")?.GetField("Instance", BindingFlags.Static | BindingFlags.Public);
                if (_player == null) Log.Warn("[k12] Camera: CameraFollowTarget.Instance not found -- the view stays the original");
                if (_doors == null || _locks == null || _team2 == null) Log.Warn("[k12] Original Camera in Fights: the fight state not found (RoomLockController / Health): never a fight");
            }
            if (_doors == null || _locks == null || _team2 == null) return false;
            return _locks.GetValue(null) is System.Collections.ICollection l && l.Count > 0 && _team2.GetValue(null) is System.Collections.ICollection t && t.Count > 0 && !(bool)_doors.GetValue(null);
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- the LetterBox (Cutscene Bars)

    const string LbType = "LetterBox";
    static Assembly _gameAsm;
    /// <summary>Lifecycle.For (once per type the engine runs): LetterBox is watched (Cutscene Bars); the first game type gives the game's
    /// assembly (the fight / gameplay state is read from it).</summary>
    public static bool Watches(Type t)
    {
        if (_gameAsm == null && CamRow && t.Assembly.GetName().Name == "Assembly-CSharp") _gameAsm = t.Assembly;
        return BarsRow && t.FullName == LbType;
    }
    /// <summary>LetterBox's Start (Lifecycle, through VitaToggles.AfterStart): its Top / Bottom bar images' CanvasRenderers.</summary>
    public static void AfterStart(object o)
    {
        try
        {
            var lb = (Component)o;
#if !SHEPHERD_EBOOT
            if (K12Dump.Dir != null) K12Dump.Dump(lb.transform, Path.Combine(K12Dump.Dir, "letterbox.txt"));
#endif
            var top = lb.GetType().GetField("Top")?.GetValue(lb) as RectTransform; var bottom = lb.GetType().GetField("Bottom")?.GetValue(lb) as RectTransform;
            Graphic ti = BarOf(top), bi = BarOf(bottom);
            if (ti == null || bi == null) { Log.Warn("[k12] Cutscene Bars: the LetterBox's two bar images not found (Top / Bottom: no Graphic of their own and no LetterBox_* image child): the bars stay drawn"); return; }
            _lb = lb; _top = ti.canvasRenderer; _bottom = bi.canvasRenderer;
            ApplyBars();
            Log.Info("[k12] Cutscene Bars: the LetterBox's bars '" + ti.name + "' / '" + bi.name + "' (" + ti.GetType().Name + "; the skip prompt and subtitle untouched) -- " + (Bars ? "drawn" : "not drawn (the row is Off)"));
        }
        catch (Exception e) { Log.Warn("[k12] Cutscene Bars: " + e.GetBaseException().Message + " (the bars stay drawn)"); }
    }
    /// <summary>A LetterBox bar: Top's / Bottom's own Graphic, else its direct child Image with a LetterBox_* sprite (the game's prefab:
    /// Top/Top (1) LetterBox_Top, Bottom/Bottom (1) LetterBox_Bottom; Bottom's other children are the skip prompt and the subtitle).</summary>
    static Graphic BarOf(Transform t)
    {
        if (t == null) return null;
        var own = t.GetComponent<Graphic>(); if (own != null) return own;
        for (int i = 0; i < t.childCount; i++)
            if (t.GetChild(i).GetComponent<Image>() is { } im && im.sprite != null && im.sprite.name.StartsWith("LetterBox")) return im;
        return null;
    }
    static void ApplyBars()
    {
        if (_lb == null || _top == null || _bottom == null) return;
        _top.cull = !Bars; _bottom.cull = !Bars;
    }
}

/// <summary>Kit #12 menusb: the Version history panel's text (roadmap-kits.md, owner 2026-10-31) -- a small English text file made from
/// vita/notes/progress-log.md's "Kit by kit" table by vita/tools/k12_history.py, packed in the VPK (app0:version-history.txt). Read from
/// <c>SHEPHERD_K12_HISTORY=&lt;file&gt;</c> (Mac / tools), else the VPK's copy, and only when the VPK has none (a SHEPHERD_K12_HISTORY_PACK=0
/// build, vlx, which stages no app0 files) a copy in the owner folder -- a stale card file never outlives the build (review fix, finding
/// 14: the card is results only). Lines: "@ version | date | text", newest first.</summary>
[ProfileOnly]
public static class K12History
{
    public sealed class Entry { public string Version, Date, Text; }
    static List<Entry> _e;
    public static List<Entry> Entries()
    {
        if (_e != null) return _e;
        _e = new List<Entry>();
        string text = null, from = null;
        foreach (var p in new[] { Environment.GetEnvironmentVariable("SHEPHERD_K12_HISTORY"), "app0:version-history.txt", Path.Combine(Program.Root, "version-history.txt") })
        {
            if (string.IsNullOrEmpty(p)) continue;
            try { if (File.Exists(p)) { text = File.ReadAllText(p); from = p; break; } } catch (Exception) { }
        }
        if (text == null) { Log.Info("[k12] Version history: no version-history.txt (this build has none): the section is left out"); return _e; }
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            if (!l.StartsWith("@ ")) continue;
            var parts = l.Substring(2).Split(new[] { " | " }, 3, StringSplitOptions.None);
            if (parts.Length == 3) _e.Add(new Entry { Version = parts[0].Trim(), Date = parts[1].Trim(), Text = parts[2].Trim() });
        }
        Log.Info("[k12] Version history: " + _e.Count + " versions from " + from);
        return _e;
    }
    /// <summary>The About Shepherd row's value: Shepherd's version (the newest history entry) and the game version it runs.</summary>
    public static string AboutText()
    {
        var e = Entries();
        return "Shepherd " + (e.Count > 0 ? e[0].Version : "(unknown)") + " / game " + Application.version;
    }
}

/// <summary>Kit #12 menusb: the recommended plugins' info row (owner 2026-10-30: CapUnlocker, PSVshellPlus, Wi-Fi Turbo "for online play";
/// names only). The detection is the boot track's (the taiHEN config read): it calls <see cref="Set"/> once at boot (the agreed call:
/// vita/notes/kit12-menusb.md). Without that call the row shows only what kit #6's host state proves -- the 4th core open =
/// CapUnlocker, the measured clock ~500 = a PSVshellPlus profile -- and "not checked" for the rest: a clock under 490 proves nothing
/// (444 chosen in the CPU Speed row, the measurement app's 444, no profile for this title; review fix, finding 1), nor does Wi-Fi
/// Turbo's absence. The row is refreshed once a second while it exists (the measured clock settles after the tab is built).</summary>
[ProfileOnly]
public static class K12Plugins
{
    /// <summary>Bits from the boot track's detection: 1 CapUnlocker, 2 PSVshellPlus, 4 Wi-Fi Turbo; 256 = the detection ran (the taiHEN config
    /// was read). -1 = not called.</summary>
    public static int Bits = -1;
    public static void Set(int bits) { Bits = bits; }
    static readonly string[] Names = { "CapUnlocker", "PSVshellPlus", "Wi-Fi Turbo" };
    static readonly string[] What = { "lets the game use the fourth CPU core", "the 500 MHz profile", "for online play" };
    /// <summary>1 installed, 0 missing, -1 not checked.</summary>
    static int State(int i)
    {
        if (Bits >= 0 && (Bits & 256) != 0) return (Bits & (1 << i)) != 0 ? 1 : 0;
        if (i == 0) return (VitaToggles.K12State & 2) != 0 ? 1 : 0;        // the 4th core open (kit #6's C3 probe; the host's state bits)
        if (i == 1) return VitaToggles.K12Host(3) >= 490 ? 1 : -1;         // the measured clock (kit #8 / #9): ~500 proves a profile; under 490 proves nothing
        return -1;
    }
    public static string Summary()
    {
        int miss = 0, unk = 0;
        for (int i = 0; i < 3; i++) { int s = State(i); if (s == 0) miss++; else if (s < 0) unk++; }
        return miss == 0 ? (unk == 0 ? "All installed" : "Installed") : miss + " missing";
    }
    public static string Detail()
    {
        var s = "";
        for (int i = 0; i < 3; i++)
        {
            int st = State(i);
            s += (i > 0 ? "  " : "") + Names[i] + " (" + What[i] + "): " + (st > 0 ? "installed." : st == 0 ? "missing." : "not checked.");
        }
        return s;
    }
}
