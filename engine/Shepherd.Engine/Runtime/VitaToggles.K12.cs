using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #12, track menusb (vita/notes/kit12-menusb.md): the Settings layout the owner decided on 2026-10-17 (roadmap-kits.md
/// "Settings layout"), built from the game's own objects at run time like kit #6's rows (no game DLL changed, no derived code):
/// <list type="bullet">
/// <item>a new <b>'PS Vita' tab</b> after Controls: a clone of the Graphics tab's button (its look, animator, sounds) and of the Graphics tab's
/// panel (scroll list, scrollbar, show / hide, the game's Start of that clone never runs: it would prefill the game's rows and set the
/// accessibility text scale); the clone's own rows are kept switched off (they are the templates). Kit #6-#11's rows move there, in
/// sections with the tab's own header style (Performance, Storage, Testing, Controls, About), plus About Shepherd, the recommended plugins
/// and the version history. They are built at the tab's FIRST SHOW (as Unity defers a hidden tab's Start);</item>
/// <item>rows that extend an existing kind of setting at the END of their tab under a <b>'PS Vita' header</b> (the tab's section header
/// style) with the game's own divider art above it (the Settings title's two TileDivider flourishes, back to back): Game -- Camera,
/// Original Camera in Fights, Cutscene Bars (K12View). Graphics' Vita rows (Shadows, Animations, Dynamic Resolution) are not built: their
/// features do not exist yet (kit #13+); the Controls tab is a controller picture with no list, so Rear Touch stays in the PS Vita tab;</item>
/// <item>a <b>description strip</b> at the bottom of the panel, in the game's italic style (the section header's FiraSans text, italic,
/// the title's cream colour, the value texts' size), on the menu's own background colour, shown ONLY while a Vita row is highlighted, with
/// the owner's texts verbatim (Desc); an empty layout object at the end of each list keeps the last row clear of it;</item>
/// <item>the game's Reset All resets the Vita rows too (CheckReset -> K12Reset).</item>
/// </list>
/// Switches (env file): <c>SHEPHERD_K12_SETTINGS=0</c> (or the track's master <c>SHEPHERD_K12_MENUSB=0</c>) = kit #11's layout exactly (the rows
/// at the end of Graphics under 'PS Vita Settings'); <c>SHEPHERD_K12_DESC=0</c> = no strip; <c>SHEPHERD_K12_ABOUT=0</c> = no About / history;
/// <c>SHEPHERD_K12_PLUGINS_ROW=0</c> = no plugins row; the Game tab's rows follow K12View's switches.</summary>
public static partial class VitaToggles
{
    static string Env(string n) => Environment.GetEnvironmentVariable(n);
    /// <summary>The host's kit #6 answers for K12Plugins (op 3 the clock now) and its state bits as kit #6 last read them.</summary>
    internal static int K12Host(int op) => Host(op, 0);
    internal static int K12State => _state;
    /// <summary>The kit #12 Settings layout (needs kit #6's rows: SHEPHERD_K6_TOGGLES=0 turns it off too). The switches live in K12View,
    /// read from the environment there; these are properties, so no static initialiser here reads K12View (a cycle between two static
    /// initialisers is what broke the first VLX gate -- see K12View.Layout).</summary>
    public static bool K12Layout => K12View.Layout;
    static bool K12Desc => K12View.Desc;
    static bool K12About => K12View.About;
    static bool K12Plug => K12View.Plug;
    static bool K12GameRows => K12View.BarsRow || K12View.CamRow;

    /// <summary>The owner's description texts (2026-10-17), verbatim, by row name (the rows' GameObject names). Rows without one show no strip.</summary>
    static readonly Dictionary<string, string> Desc = new()
    {
        ["Power Mode"] = "Sets how hard the Vita works. Performance runs at the highest frame rate each scene can hold; Battery-saver locks 30 fps at lower clock speeds so the battery lasts longer.",
        ["4th CPU Core"] = "Unlocks the Vita's fourth processor core. Background work like loading and preparing upcoming areas runs on it, leaving more power for the game. Requires plugin.",
        ["CPU Speed"] = "Sets the processor's clock speed. 500 MHz makes the game run faster and uses more battery; 444 MHz is the Vita's standard maximum. 500 MHz requires plugin.",
        ["CPU Clock"] = "Shows the processor's current speed and the game's frame rate.",
        ["Freeze When Screen Dims"] = "Pauses drawing while your Vita's power saving has the screen dimmed. The game keeps running in the background, the last picture stays on screen, and any button wakes it.",
        ["Cache Size"] = "Sets how much memory card space the game may use for speed-up files. These files make loading faster the more you play.",
        ["Clear Cache"] = "Deletes the speed-up files. Saves and settings are never touched, and the files rebuild as you play.",
        ["Verify Files"] = "Checks every game file on your memory card for damage and reports any that need repairing.",
        ["Restore Save Backup"] = "Lists the last five versions of this save slot. Choosing one brings it back.",
        ["Performance Overlay"] = "Shows the frame rate, frame timings and free memory in a corner of the screen. Meant for testing.",
        ["Shadows"] = "Chooses how shadows are drawn. Original draws them exactly like the PC; Optimized draws each object's shadow from its outline, which looks nearly the same and is lighter on the Vita.",
        ["Animations"] = "Chooses how often characters animate. Full animates everyone at full speed; Original matches the PC, which animates followers far from the camera at your cult less often, and the minions summoned in Leshy's boss fight at their lowest rate.",
        ["Dynamic Resolution"] = "Lets the game lower its resolution slightly in the heaviest moments, never below 75 %, to keep the frame rate steady. Menus and text always stay sharp.",
        ["Camera"] = "Sets how close the camera sits. Closer and Closest make everything bigger on the Vita's screen; Original shows the same view as the PC.",
        ["Original Camera in Fights"] = "Returns the camera to its original distance during combat, so the whole room stays in view.",
        ["Cutscene Bars"] = "Shows or hides the black bars during cutscenes. Hiding them shows more of the picture on the Vita's screen.",
        ["Rear Touch"] = "Turns corners of the rear touch pad into L2, R2, L3 and R3, the buttons the Vita doesn't have.",
    };

    static Component _k12Panel;          // the PS Vita tab's panel (the Graphics panel's clone: a GraphicsSettings whose Start never runs)
    static Component _k12Game;           // the Game tab's panel (GameSettings)
    static Transform _k12Tab;            // the PS Vita tab's button
    static bool _k12Built, _k12GameBuilt;
    static readonly List<Transform> _k12Made = new();          // kit #12's own objects in the lists (headers, rows, spacers), destroyed on failure
    static readonly Dictionary<Selectable, string> _k12Text = new();   // highlighted row -> its strip text
    static GameObject _k12Strip; static object _k12StripText; static Selectable _k12Shown; static string _k12StripNow;
    static Transform _k12OrnL, _k12OrnR;                       // the Settings title's TileDivider flourishes (the PS Vita headers' divider art)
    static Component _camSel, _fightTog, _barsTog; static Selectable _camRow, _fightRow, _barsRow;
    static Component _aboutSel, _plugSel; static Selectable _plugRow; static string _plugNow;
    static PropertyInfo _navInst, _navCur;
    const float StripH = 112f;           // the strip's height in canvas units (the panel is 960 x 789); the lists' end spacers are as tall

    // ---------------------------------------------------------------- the menu opened: the PS Vita tab + the Game tab's section + the strip

    /// <summary>UISettingsMenuController's Start (VitaToggles.MenuStart): the PS Vita tab (button + panel, rows at its first show), the Game tab's
    /// PS Vita section (at its first show), the description strip (hidden). A failure removes what was made and leaves kit #11's layout
    /// (the rows then go to the Graphics tab as before: _k12Panel stays null).</summary>
    static void K12MenuStart(Component menu)
    {
        _k12Built = _k12GameBuilt = false; _k12Panel = _k12Game = null; _k12Tab = null; _k12Strip = null; _k12Shown = null; _k12StripNow = null;
        _k12Text.Clear(); _k12Made.Clear();
        GameObject holder = null, tabGo = null, panelGo = null; Component navF = null; FieldInfo tabsF0 = null; Array tabs0 = null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var gfx = Get(menu, "_graphicsSettings") as Component; _k12Game = Get(menu, "_gameSettings") as Component;
            Component nav = null;
            foreach (var c in menu.GetComponentsInChildren<MonoBehaviour>(true)) if (c != null && c.GetType().Name == "SettingsTabNavigatorBase") { nav = c; break; }
            if (gfx == null || nav == null) throw new InvalidOperationException("the Graphics tab or the tab navigator not found");
            var tabsF = nav.GetType().GetField("_tabs", I); var tabs = (Array)tabsF.GetValue(nav);
            Component gfxTab = null; Transform last = null;
            foreach (Component t in tabs) { if (t == null) continue; last = t.transform; if (ReferenceEquals(Get(t, "_menu"), gfx)) gfxTab = t; }
            if (gfxTab == null || last == null) throw new InvalidOperationException("the Graphics tab's button not found");
            _k12OrnL = _k12OrnR = null;
            foreach (var im in menu.GetComponentsInChildren<Image>(true))
                if (im != null && im.sprite != null) { if (im.sprite.name == "TileDivider_Left") _k12OrnL ??= im.transform; else if (im.sprite.name == "TileDivider_Right") _k12OrnR ??= im.transform; }

            holder = new GameObject("k12 holder", typeof(RectTransform)); holder.SetActive(false); holder.transform.SetParent(menu.transform, false);
            tabGo = UObject.Instantiate(gfxTab.gameObject, holder.transform, false); tabGo.name = "PS Vita Button";
            panelGo = K12ClonePanel(gfx, holder.transform); panelGo.name = "PS Vita Settings";
            if (!tabGo.activeSelf) tabGo.SetActive(true);
            if (!panelGo.activeSelf) panelGo.SetActive(true);   // (wakes when moved into the menu: its Awake hides it, as the game's own panels)
            foreach (var c in tabGo.GetComponentsInChildren<Component>(true)) if (c != null && c.GetType().FullName == "I2.Loc.Localize") UObject.DestroyImmediate(c);
            _tmpText ??= FindTmp(tabGo.transform)?.GetType().GetProperty("text", I);
            _tmpText.SetValue(FindTmp(tabGo.transform), "PS Vita");
            var panel = panelGo.GetComponent(gfx.GetType());
            var nm = Native.Objects.Get<Native.NMono>(panel);
            if (nm == null) throw new InvalidOperationException("the clone's record not found");
            nm.Started = true;   // the clone's GraphicsSettings.Start never runs (it prefills the game's rows and sets the accessibility text scale)
            var content = (Get(panel, "_scrollRect") as ScrollRect)?.content;
            if (content == null) throw new InvalidOperationException("the clone's scroll list not found");
            K12Templates(gfx, panel, content);   // three templates (header, Target Frame Rate, VSync) in the clone's list, switched off
            var tab = tabGo.GetComponent(gfxTab.GetType());
            tab.GetType().GetField("_menu", I).SetValue(tab, panel);   // (the clone's button would still open the Graphics panel)
            panelGo.transform.SetParent(gfx.transform.parent, false); panelGo.transform.SetAsLastSibling();
            tabGo.transform.SetParent(last.parent, false); tabGo.transform.SetSiblingIndex(last.GetSiblingIndex() + 1);
            var arr = Array.CreateInstance(tabs.GetType().GetElementType(), tabs.Length + 1);
            Array.Copy(tabs, arr, tabs.Length); arr.SetValue(tab, tabs.Length); tabsF.SetValue(nav, arr);
            navF = nav; tabsF0 = tabsF; tabs0 = tabs;   // (a failure below puts the game's own tab list back)
            var navRec = Native.Objects.Get<Native.NMono>(nav);
            if (navRec != null && navRec.Started) K12WireTab(nav, tab, panel);   // (the navigator's Start already ran: wire the new tab as it does)
            AddOnShow(panel, new Action(K12TabShow));
            _k12Panel = panel; _k12Tab = tabGo.transform;
            if (_k12Game != null && K12GameRows)
            {
                if (_k12Game.gameObject.activeInHierarchy) K12GameShow(); else AddOnShow(_k12Game, new Action(K12GameShow));
            }
            if (K12Desc) K12MakeStrip(gfx);
            Log.Info("[k12] Settings: the PS Vita tab added after '" + last.name + "' (" + (tabs.Length + 1) + " tabs; its rows at its first show; " + Ms(t0) + ")" +
                     (K12GameRows ? "; the Game tab's PS Vita section at its first show" : "") + (K12Desc ? "; the description strip" : "") +
                     " (SHEPHERD_K12_SETTINGS=0: kit #11's layout)");
        }
        catch (Exception e)
        {
            Log.Warn("[k12] Settings: the PS Vita tab was not added (" + e.GetBaseException().Message + "): kit #11's layout");
            if (tabsF0 != null) tabsF0.SetValue(navF, tabs0);
            if (tabGo != null && tabGo.transform.parent != holder?.transform) UObject.Destroy(tabGo);
            if (panelGo != null && panelGo.transform.parent != holder?.transform) UObject.Destroy(panelGo);
            _k12Panel = null; _k12Tab = null;
        }
        finally { if (holder != null) UObject.Destroy(holder); }
    }

    /// <summary>Milliseconds since a Stopwatch timestamp, as log text (the [k12] build lines: what each part of the menu's build cost).</summary>
    static string Ms(long t0) => ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms";
    static long _k12T;

    /// <summary>The Graphics panel cloned WITHOUT its rows (kit #12's first VLX gate: the whole panel, ~27 rows, cost 0.2-0.3 s VLX on the
    /// Settings open frame): the rows of the game's (hidden, never-shown-yet) Graphics list are moved to an inactive holder for the
    /// Instantiate and straight back, in the same order (inactive objects: nothing wakes, no layout runs; the list is laid out when the tab
    /// first shows, as before).</summary>
    static GameObject K12ClonePanel(Component gfx, Transform holder)
    {
        var src = (Get(gfx, "_scrollRect") as ScrollRect)?.content;
        if (src == null) throw new InvalidOperationException("the Graphics tab's scroll list not found");
        var park = new GameObject("k12 park", typeof(RectTransform)); park.SetActive(false); park.transform.SetParent(holder, false);
        int n = src.childCount; var kids = new Transform[n];
        for (int i = 0; i < n; i++) kids[i] = src.GetChild(i);
        try
        {
            for (int i = 0; i < n; i++) kids[i].SetParent(park.transform, false);
            return UObject.Instantiate(gfx.gameObject, holder, false);
        }
        finally
        {
            for (int i = 0; i < n; i++) kids[i].SetParent(src, false);   // (back in their order: the list is the game's again)
            UObject.Destroy(park);
        }
    }

    /// <summary>The clone's own templates: the Graphics list's first section header, its Target Frame Rate and VSync rows, cloned into the
    /// clone's list and switched off; the clone's _targetFpsSelector / _targetFpsSelectable / _vSyncSwitch point at them (its OnShowStarted
    /// touches two of them; Build clones from them, as kit #6 cloned from the game's rows).</summary>
    static void K12Templates(Component gfx, Component panel, Transform content)
    {
        var src = (Get(gfx, "_scrollRect") as ScrollRect).content;
        var fps = Get(gfx, "_targetFpsSelector") as Component; var vs = Get(gfx, "_vSyncSwitch") as Component;
        Transform head = FirstHeader(src), sel = RowOf(fps, src), tog = RowOf(vs, src);
        if (head == null || sel == null || tog == null) throw new InvalidOperationException("the Graphics list's header / Target Frame Rate / VSync rows not found");
        foreach (var t in new[] { head, sel, tog })
        {
            var c = UObject.Instantiate(t.gameObject, content, false); c.name = t.name;
            if (c.activeSelf) c.SetActive(false);
        }
        var cSel = content.GetChild(1); var cTog = content.GetChild(2);
        panel.GetType().GetField("_targetFpsSelector", I).SetValue(panel, cSel.GetComponentInChildren(fps.GetType(), true));
        var selable = Get(gfx, "_targetFpsSelectable") as Component;
        if (selable != null) panel.GetType().GetField("_targetFpsSelectable", I).SetValue(panel, cSel.GetComponent(selable.GetType()));
        panel.GetType().GetField("_vSyncSwitch", I).SetValue(panel, cTog.GetComponentInChildren(vs.GetType(), true));
    }

    static void AddOnShow(Component menuBase, Action a)
    {
        var f = menuBase.GetType().GetField("OnShow", BindingFlags.Instance | BindingFlags.Public);
        f.SetValue(menuBase, Delegate.Combine((Delegate)f.GetValue(menuBase), a));
    }

    /// <summary>The navigator's Start already ran (it configures the tabs it holds then): the new tab gets what that Start gives each tab --
    /// Configure(), its press -> TransitionTo(tab), and its panel's hide clears the navigator's selection inside it.</summary>
    static void K12WireTab(Component nav, Component tab, Component panel)
    {
        Call(tab, "Configure", null, null);
        var tr = nav.GetType().GetMethod("TransitionTo", BindingFlags.Instance | BindingFlags.Public);
        var press = tab.GetType().GetField("OnTabPressed", BindingFlags.Instance | BindingFlags.Public);
        press.SetValue(tab, Delegate.Combine((Delegate)press.GetValue(tab), new Action(() => tr.Invoke(nav, new object[] { tab }))));
        // (review fix, finding 7) and the per-tab hide the navigator's Start adds (MMTabNavigatorBase.Start): the selection is cleared when
        // it is inside the tab being hidden, so a tab with no default selectable (Controls) never leaves a hidden PS Vita row selected
        var hide = panel.GetType().GetField("OnHide", BindingFlags.Instance | BindingFlags.Public);
        hide.SetValue(panel, Delegate.Combine((Delegate)hide.GetValue(panel), new Action(() => K12HideClear(panel))));
        Log.Info("[k12] Settings: the tab navigator had started -- the PS Vita tab wired as its Start wires a tab");
    }
    static MethodInfo _navClear;
    /// <summary>The navigator's per-tab OnHide (MMTabNavigatorBase.Start): UINavigatorNew.Clear() when its selection is inside the hidden tab.</summary>
    static void K12HideClear(Component panel)
    {
        try
        {
            if (!K12NavFound()) return;
            var nav = _navInst.GetValue(null);
            if (_navCur.GetValue(nav) is Component cur && cur != null && cur.transform.IsChildOf(panel.transform))
                (_navClear ??= nav.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null))?.Invoke(nav, null);
        }
        catch (Exception e) { Log.Warn("[k12] Settings: the PS Vita tab's hide could not clear the selection (" + e.GetBaseException().Message + ")"); }
    }
    /// <summary>UINavigatorNew.Instance / CurrentSelectable, looked up once (the strip and the tab's hide).</summary>
    static bool K12NavFound()
    {
        if (_navInst == null)
        {
            var nt = _gameAsm?.GetType("src.UINavigator.UINavigatorNew");
            _navInst = nt?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            _navCur = nt?.GetProperty("CurrentSelectable", BindingFlags.Instance | BindingFlags.Public);
        }
        return _navInst != null && _navCur != null;
    }

    /// <summary>The PS Vita tab's first show (its OnShow, right after OnShowStarted): kit #6-#11's rows built into it (Build, kit #12 order).</summary>
    static void K12TabShow()
    {
        if (_k12Built || _k12Panel == null) { if (Live && _tog != null) Grey(true); return; }
        _k12Built = true;
        _k12T = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Build((MonoBehaviour)_k12Panel, true);
            // the game selected the tab's default in SetActiveStateForMenu(true), BEFORE this first OnShow made the rows: its own
            // ActivateNavigation once more, now that _defaultSelectable is the first row (later shows go the game's way unchanged)
            if ((Get(_k12Panel, "_canvasGroup") as CanvasGroup)?.interactable == true)
            {
                var t = _k12Panel.GetType(); MethodInfo an = null;
                for (; t != null && an == null; t = t.BaseType) an = t.GetMethod("ActivateNavigation", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                an?.Invoke(_k12Panel, null);
            }
        }
        catch (Exception e)
        {
            Log.Warn("[k12] Settings > PS Vita: the rows were not added (" + e.GetBaseException().Message + "): the tab stays empty for this visit");
            _tog = _speed = _clock = _ovl = null; _togRow = _speedRow = _ovlRow = null; _rearTog = _paceTog = _bakSel = null; _rearRow = _paceRow = _bakRow = null;
            K10Forget();
            foreach (var r in _made) if (r != null) UObject.Destroy(r.gameObject);
            K12ForgetTab();
        }
        Array.Clear(_made);
    }

    /// <summary>A failed PS Vita tab build (review fix, finding 8): only that tab's own objects go (the Game tab's PS Vita section and its
    /// handlers stay), and the clone's default selection is emptied -- the clone was made with the Graphics rows parked outside it, so its
    /// _defaultSelectable / _defaultSelectableFallbacks still name the game's real (hidden) Graphics rows, which the game's ActivateNavigation
    /// would select on every later show of the tab (only K12AfterBuild rewrites them).</summary>
    static void K12ForgetTab()
    {
        if (_k12Panel == null) return;
        var pt = _k12Panel.transform;
        for (int i = _k12Made.Count - 1; i >= 0; i--)
        {
            var t = _k12Made[i];
            if (t != null && !t.IsChildOf(pt)) continue;
            if (t != null) UObject.Destroy(t.gameObject);
            _k12Made.RemoveAt(i);
        }
        _k12HistRows.Clear(); _aboutSel = _plugSel = null; _plugRow = null;
        var bt = _k12Panel.GetType();
        while (bt != null && bt.GetField("_defaultSelectable", I | BindingFlags.DeclaredOnly) == null) bt = bt.BaseType;
        bt?.GetField("_defaultSelectable", I | BindingFlags.DeclaredOnly)?.SetValue(_k12Panel, null);
        var fb = bt?.GetField("_defaultSelectableFallbacks", I | BindingFlags.DeclaredOnly);
        if (fb != null) fb.SetValue(_k12Panel, Array.CreateInstance(fb.FieldType.GetElementType(), 0));
    }

    /// <summary>Build's kit #12 part: the PS Vita tab's sections, in order, every row moved into the list. _made[] holds kit #6-#11's rows
    /// (Build sets them up afterwards as before); the headers / new rows / end spacer go to _k12Made.</summary>
    static void K12Sections(GameObject holder, Transform head, Transform togRow, Transform selRow, Transform content)
    {
        void Add(Transform t) { if (t == null) return; t.SetParent(content, false); }
        void Head(string label) { var h = K12Header(head, holder, label, false); _k12Made.Add(h); Add(h); }
        Head("Performance");
        Add(_made[1]); Add(_made[2]); Add(_made[3]); Add(_made[6]);   // 4th CPU Core, CPU Speed, CPU Clock, Frame Pacing (kit #8)
        if (_made[7] != null || _made[8] != null)
        {
            Head("Storage");
            Add(_made[8]); Add(_made[9]); Add(_made[10]); Add(_made[11]); Add(_made[7]);   // Cache Size, Cache In Use, Clear Cache, Verify Files, Restore Save Backup
        }
        if (_made[4] != null) { Head("Testing"); Add(_made[4]); }   // Performance Overlay (kit #7)
        if (_made[5] != null) { Head("Controls"); Add(_made[5]); }   // Rear Touch (kit #8): the Controls tab has no list
        if (K12About || K12Plug)
        {
            Head("About");
            if (K12About) { var a = Clone(selRow, holder, "About Shepherd", "About Shepherd"); _k12Made.Add(a); Add(a); }
            if (K12Plug) { var p = Clone(selRow, holder, "Recommended Plugins", "Recommended Plugins"); _k12Made.Add(p); Add(p); }
            if (K12About)
            {
                var hist = K12History.Entries();
                if (hist.Count > 0) Head("Version History");
                foreach (var en in hist)
                {
                    var r = Clone(selRow, holder, "History " + en.Version, "Version " + en.Version);
                    _k12Made.Add(r); Add(r); r.SetAsLastSibling();
                    _k12HistRows.Add((r, en));
                }
            }
        }
        if (K12Desc) { var sp = K12Spacer(holder); _k12Made.Add(sp); Add(sp); }   // (only with the strip: review fix, finding 10)
    }
    static readonly List<(Transform row, K12History.Entry en)> _k12HistRows = new();

    /// <summary>After Build (kit #12 order): the new rows' texts (readouts with no arrows) and the strip's texts for every row of the tab.</summary>
    internal static void K12AfterBuild(Type selType)
    {
        foreach (var t in _k12Made)
        {
            if (t == null) continue;
            var s = t.GetComponent<Selectable>();
            if (t.name == "About Shepherd") { _aboutSel = t.GetComponentInChildren(selType, true); Readonly(_aboutSel, s, K12History.AboutText()); }
            else if (t.name == "Recommended Plugins") { _plugSel = t.GetComponentInChildren(selType, true); _plugRow = s; _plugNow = K12Plugins.Detail(); Readonly(_plugSel, s, K12Plugins.Summary()); if (s != null) _k12Text[s] = _plugNow; }
        }
        foreach (var (row, en) in _k12HistRows)
        {
            var sel = row.GetComponentInChildren(selType, true); var s = row.GetComponent<Selectable>();
            Readonly(sel, s, en.Date);
            if (s != null) _k12Text[s] = en.Text;
        }
        _k12HistRows.Clear();
        // readouts are highlightable under kit #12 (their description / text shows in the strip): the clones of the Target Frame Rate row
        // come greyed (the game greys it while VSync is on) -- lit once here; every later show of the tab lights them the game's way.
        // Only this tab's rows (review fix, finding 9): _k12Made also holds the Game tab's section, whose Original Camera in Fights row
        // stays greyed while Camera is Original
        var pt = _k12Panel.transform;
        foreach (var t in _k12Made) if (t != null && t.IsChildOf(pt) && t.GetComponent<Selectable>() is { } rs) SetInteractable(rs, true);
        if (_made[3] != null) SetInteractable(_made[3].GetComponent<Selectable>(), true);
        foreach (var r in _made) if (r != null && Desc.TryGetValue(r.name, out var d) && r.GetComponent<Selectable>() is { } s) _k12Text[s] = d;
        // the tab's first selection (UIMenuBase.ActivateNavigation, after OnShow): the first row, else the first interactable one in list order
        // (the clone's own _defaultSelectable is its switched-off FullScreen Mode row)
        var content = (Get(_k12Panel, "_scrollRect") as ScrollRect)?.content; var order = new List<Selectable>();
        for (int i = 0; content != null && i < content.childCount; i++)
            if (content.GetChild(i).gameObject.activeSelf && content.GetChild(i).GetComponent<Selectable>() is { enabled: true } s2) order.Add(s2);
        if (order.Count > 0)
        {
            var bt = _k12Panel.GetType();
            while (bt != null && bt.GetField("_defaultSelectable", I | BindingFlags.DeclaredOnly) == null) bt = bt.BaseType;
            bt?.GetField("_defaultSelectable", I | BindingFlags.DeclaredOnly)?.SetValue(_k12Panel, order[0]);
            var fb = bt?.GetField("_defaultSelectableFallbacks", I | BindingFlags.DeclaredOnly);
            if (fb != null) { var arr = Array.CreateInstance(fb.FieldType.GetElementType(), order.Count); for (int i = 0; i < order.Count; i++) arr.SetValue(order[i], i); fb.SetValue(_k12Panel, arr); }
        }
        Log.Info("[k12] Settings > PS Vita: rows added (" + _k12Made.Count + " kit #12 objects; " + _k12Text.Count + " rows with a description; the tab's first show built them in " + Ms(_k12T) + ")");
    }
    /// <summary>Tick, once a second while the PS Vita tab's rows exist (review fix, finding 1): the plugins row follows the measured clock
    /// (it settles after the tab is built) -- the value and the strip's text, only when the text changes.</summary>
    static void K12PlugRefresh()
    {
        var d = K12Plugins.Detail();
        if (d == _plugNow) return;
        _plugNow = d;
        Readonly(_plugSel, null, K12Plugins.Summary());
        if (_plugRow != null) { _k12Text[_plugRow] = d; if (ReferenceEquals(_k12Shown, _plugRow)) _k12Shown = null; }   // (the strip re-reads it)
        Log.Info("[k12] Recommended Plugins: " + d);
    }
    static void Readonly(Component sel, Selectable row, string value)
    {
        if (sel == null) return;
        if (row != null) Call(row, "SetNormalTransitionState", null, null);
        SetProp(sel, "LocalizeContent", false);
        Call(sel, "PrefillContent", typeof(string[]), new[] { value });   // one item: no arrows
    }

    /// <summary>A PS Vita section header: a clone of the tab's section header row (its font, colour, italic) with the game's divider art
    /// above the text when <paramref name="divider"/> (the Settings title's two TileDivider flourishes, back to back, at three quarters of
    /// their size).</summary>
    static Transform K12Header(Transform head, GameObject holder, string label, bool divider)
    {
        var h = Clone(head, holder, "-- " + label + " --", label);
        if (divider && _k12OrnL != null && _k12OrnR != null)
        {
            for (int side = 0; side < 2; side++)
            {
                var o = UObject.Instantiate((side == 0 ? _k12OrnL : _k12OrnR).gameObject, h, false);
                o.name = side == 0 ? "Divider Left" : "Divider Right";
                if (!o.activeSelf) o.SetActive(true);
                var rt = (RectTransform)o.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(side == 0 ? 1f : 0f, 0.5f);
                rt.sizeDelta = new Vector2(45f, 21f);
                rt.anchoredPosition = new Vector2(side == 0 ? -2f : 2f, -24f);
            }
        }
        return h;
    }

    /// <summary>An empty layout object as tall as the strip at the end of a list (the last row can scroll clear of the strip). Empty layout
    /// objects draw nothing (kit #6's bisect: they move no animation phase either).</summary>
    static Transform K12Spacer(GameObject holder)
    {
        var go = new GameObject("k12 strip spacer", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(holder.transform, false);
        var le = go.GetComponent<LayoutElement>(); le.minHeight = le.preferredHeight = StripH;
        return go.transform;
    }

    // ---------------------------------------------------------------- the Game tab's PS Vita section

    /// <summary>The Game tab's first show (or the menu's start when it is already shown): 'PS Vita' header + Camera (a clone of Gamepad
    /// Prompts' selector row), Original Camera in Fights and Cutscene Bars (clones of Show Follower Names' toggle row), the end spacer.</summary>
    static void K12GameShow()
    {
        if (_k12GameBuilt || _k12Game == null) return;
        _k12GameBuilt = true;
        GameObject holder = null; var made = new List<Transform>(); long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var gfx = Get(_menu, "_graphicsSettings") as Component;
            var head = FirstHeader((Get(gfx, "_scrollRect") as ScrollRect)?.content);
            var content = _k12Game.GetComponentInChildren<ScrollRect>(true)?.content;
            var sel = Get(_k12Game, "_gamepadPrompts") as Component; var tog = Get(_k12Game, "_showFollowerNames") as Component;
            Transform selRow = RowOf(sel, content), togRow = RowOf(tog, content);
            if (head == null || content == null || selRow == null || togRow == null) throw new InvalidOperationException("the Game tab's list / rows or a section header not found");
            _tmpText ??= FindTmp(head)?.GetType().GetProperty("text", I);
            holder = new GameObject("k12 game rows", typeof(RectTransform)); holder.SetActive(false); holder.transform.SetParent(_k12Game.transform, false);
            made.Add(K12Header(head, holder, "PS Vita", true));
            if (K12View.CamRow)
            {
                var c = Clone(selRow, holder, "Camera", "Camera"); made.Add(c);
                var f = Clone(togRow, holder, "Original Camera in Fights", "Original Camera in Fights"); made.Add(f);
            }
            if (K12View.BarsRow) made.Add(Clone(togRow, holder, "Cutscene Bars", "Cutscene Bars"));
            if (K12Desc) made.Add(K12Spacer(holder));   // (only with the strip: review fix, finding 10)
            foreach (var t in made) t.SetParent(content, false);   // (they wake here, in the shown tab)
            foreach (var t in made)
            {
                var s = t.GetComponent<Selectable>();
                if (t.name == "Camera") { _camSel = t.GetComponentInChildren(sel.GetType(), true); _camRow = s; }
                else if (t.name == "Original Camera in Fights") { _fightTog = t.GetComponentInChildren(tog.GetType(), true); _fightRow = s; }
                else if (t.name == "Cutscene Bars") { _barsTog = t.GetComponentInChildren(tog.GetType(), true); _barsRow = s; }
                if (s != null && Desc.TryGetValue(t.name, out var d)) _k12Text[s] = d;
            }
            _k12Made.AddRange(made);
            K12Configure(true);
            if (_camSel != null) _camSel.GetType().GetField("OnSelectionChanged").SetValue(_camSel, new Action<int>(OnCamera));
            if (_fightTog != null) _fightTog.GetType().GetField("OnValueChanged").SetValue(_fightTog, new Action<bool>(OnFights));
            if (_barsTog != null) _barsTog.GetType().GetField("OnValueChanged").SetValue(_barsTog, new Action<bool>(OnBars));
            AddOnShow(_k12Game, new Action(() => { if (_camSel != null) K12Grey(true); }));
            Log.Info("[k12] Settings > Game: PS Vita section added (" + K12View.Choices() + "; " + Ms(t0) + ")");
        }
        catch (Exception e)
        {
            Log.Warn("[k12] Settings > Game: the PS Vita section was not added (" + e.GetBaseException().Message + ")");
            foreach (var t in made) if (t != null) UObject.Destroy(t.gameObject);
            _camSel = _fightTog = _barsTog = null; _camRow = _fightRow = _barsRow = null;
        }
        finally { if (holder != null) UObject.Destroy(holder); }
    }
    static Transform FirstHeader(Transform content)
    {
        for (int i = 0; content != null && i < content.childCount; i++) if (content.GetChild(i).GetComponent<Selectable>() == null) return content.GetChild(i);
        return null;
    }

    /// <summary>The Game tab's Vita rows from the choices (the build: drawn at once; Reset All: the game's own slide).</summary>
    static void K12Configure(bool show)
    {
        if (_camSel != null)
        {
            SetProp(_camSel, "LocalizeContent", false);
            SetProp(_camSel, "ContentIndex", 0);
            Call(_camSel, "PrefillContent", typeof(string[]), new[] { "Original", "Closer", "Closest" });
            SetProp(_camSel, "ContentIndex", K12View.Cam);
        }
        if (_fightTog != null) SetToggleOf(_fightTog, K12View.Fights, show);
        if (_barsTog != null) SetToggleOf(_barsTog, K12View.Bars, show);
        K12Grey(show);
    }
    /// <summary>'Original Camera in Fights' greyed while Camera is Original (it changes nothing then), the game's way (as Target Frame Rate is
    /// greyed while VSync is on).</summary>
    static void K12Grey(bool show)
    {
        bool f = K12View.Cam != 0;
        if (_fightRow != null && (show || !f)) SetInteractable(_fightRow, f);
        if (show) { SetInteractable(_camRow, true); SetInteractable(_barsRow, true); }
    }
    static void OnCamera(int i) { K12View.SetCam(i); K12Grey(true); Log.Info("[k12] Camera " + K12View.CamName(i)); Save(); }
    static void OnFights(bool on) { K12View.SetFights(on); Log.Info("[k12] Original Camera in Fights " + OnOff(on)); Save(); }
    static void OnBars(bool on)
    {
        if (!on && Render.K13Looks.BarsOnly)
        {   // kit #13 looks (SHEPHERD_K13_BARS=1): only On is offered -- the press leaves the row On (drawn at once), nothing is saved
            if (_barsTog != null) SetToggleOf(_barsTog, true, true);
            Log.Info("[k13] Cutscene Bars: only On is offered (SHEPHERD_K13_BARS=1; Off stays hidden until every cutscene is checked)");
            return;
        }
        K12View.SetBars(on); Log.Info("[k12] Cutscene Bars " + OnOff(on)); Save();
    }

    // ---------------------------------------------------------------- the description strip

    /// <summary>The strip: a plain quad in the menu's background colour (22, 22, 22: the panel's flat backdrop) across the bottom of the panel
    /// area, with a clone of the tab's section-header text (FiraSans) restyled italic, cream (the Settings title's colour), the value texts'
    /// size 20, centred, wrapping. Hidden until a Vita row is highlighted.</summary>
    static void K12MakeStrip(Component gfx)
    {
        var head = FirstHeader((Get(gfx, "_scrollRect") as ScrollRect)?.content);
        var area = gfx.transform.parent;   // the panels' container (960 x 789)
        var src = head != null ? FindTmp(head) as Component : null;
        if (src == null || area == null) { Log.Warn("[k12] description strip: no header text to clone"); return; }
        var go = new GameObject("PS Vita Description", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.SetActive(false);
        go.transform.SetParent(area, false); go.transform.SetAsLastSibling();
        var rt = (RectTransform)go.transform; rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(0f, StripH); rt.anchoredPosition = Vector2.zero;
        var im = go.GetComponent<Image>(); im.color = new Color(22f / 255f, 22f / 255f, 22f / 255f, 1f); im.raycastTarget = false;
        var txt = UObject.Instantiate(src.gameObject, go.transform, false); txt.name = "Text";
        foreach (var c in txt.GetComponentsInChildren<Component>(true)) if (c != null && c.GetType().FullName == "I2.Loc.Localize") UObject.DestroyImmediate(c);
        if (!txt.activeSelf) txt.SetActive(true);
        var trt = (RectTransform)txt.transform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.pivot = new Vector2(0.5f, 0.5f);
        trt.offsetMin = new Vector2(48f, 8f); trt.offsetMax = new Vector2(-48f, -8f);
        var tmp = FindTmp(txt.transform); var ty = tmp.GetType();
        void P(string n, object v) { var p = ty.GetProperty(n, BindingFlags.Instance | BindingFlags.Public); if (p != null) p.SetValue(tmp, p.PropertyType.IsEnum ? Enum.ToObject(p.PropertyType, v) : v); }
        P("fontStyle", 2);              // FontStyles.Italic
        P("fontSize", 20f);
        P("color", new Color(0.996f, 0.941f, 0.827f, 1f));
        P("alignment", 514);            // TextAlignmentOptions.Center
        P("enableWordWrapping", true);
        P("raycastTarget", false);
        _tmpText.SetValue(tmp, "");
        _k12Strip = go; _k12StripText = tmp;
    }

    /// <summary>Every frame while the Settings menu is open: the highlighted selectable (UINavigatorNew.CurrentSelectable); one of the Vita rows
    /// with a text -> the strip shows it; anything else -> hidden.</summary>
    static void K12Strip()
    {
        try
        {
            if (_navInst == null && !K12NavFound()) { _k12Strip = null; Log.Warn("[k12] description strip: UINavigatorNew not found -- no strip"); return; }
            var cur = _navCur.GetValue(_navInst.GetValue(null)) as Selectable;
            if (ReferenceEquals(cur, _k12Shown)) { if (_k12Strip.activeSelf && cur != null) K12ClearOfStrip(cur); return; }
            _k12Settle = 0;
            _k12Shown = cur;
            string text = null;
            if (cur != null && _k12Text.TryGetValue(cur, out var t) && cur.gameObject.activeInHierarchy) text = t;
            if (text == null) { if (_k12Strip.activeSelf) _k12Strip.SetActive(false); return; }
            if (!ReferenceEquals(text, _k12StripNow)) { _tmpText.SetValue(_k12StripText, text); _k12StripNow = text; K12StripFit(text); }
            if (!_k12Strip.activeSelf) _k12Strip.SetActive(true);
        }
        catch (Exception e) { Log.Warn("[k12] description strip: off (" + e.GetBaseException().Message + ")"); if (_k12Strip != null) _k12Strip.SetActive(false); _k12Strip = null; }
    }

    const float StripMax = 320f;         // the strip's tallest (a long version-history entry): ~12 lines at size 20, 40 % of the panel
    static MethodInfo _prefVals; static bool _fitOff;
    /// <summary>The strip as tall as its text needs, never under <see cref="StripH"/> (every owner text fits that: four lines) nor over
    /// <see cref="StripMax"/> (review fix, finding 5: a version-history entry is shown whole -- what's new, what was fixed, the numbers --
    /// instead of cut at 300 characters). TMP's own GetPreferredValues at the text area's width; once per highlight change.</summary>
    static void K12StripFit(string text)
    {
        float h = StripH;
        if (!_fitOff)
        {
            try
            {
                var tmp = _k12StripText;
                _prefVals ??= tmp.GetType().GetMethod("GetPreferredValues", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(float), typeof(float) }, null);
                float w = ((RectTransform)_k12Strip.transform).rect.width - 96f;   // (the text's side offsets, 48 each)
                if (_prefVals != null && w > 0f && _prefVals.Invoke(tmp, new object[] { text, w, 0f }) is Vector2 v && v.y > 0f && !float.IsInfinity(v.y))
                    h = Math.Min(StripMax, Math.Max(StripH, v.y + 16f));
            }
            catch (Exception e) { _fitOff = true; Log.Warn("[k12] description strip: the text's height not measured (" + e.GetBaseException().Message + "): the strip keeps its height"); }
        }
        var rt = (RectTransform)_k12Strip.transform;
        if (rt.sizeDelta.y != h) rt.sizeDelta = new Vector2(0f, h);
    }

    static float _k12LastY = float.NaN; static int _k12Settle; static MethodInfo _doScroll;
    /// <summary>The highlighted Vita row kept clear of the strip: the game's MMScrollRect keeps a selected row half a row inside the viewport,
    /// which the strip (~1.1 rows) would still cover; once the list has stopped moving (the game's own 0.2 s scroll done: the content's
    /// position unchanged for 2 frames), the list is scrolled up by the overlap through the game's own DoScrollTo (its ease, 0.2 s), never
    /// past the end (the end spacer makes room for the last row). Only while the strip shows, i.e. only for Vita rows.</summary>
    static void K12ClearOfStrip(Selectable sel)
    {
        var sr = sel.GetComponentInParent<ScrollRect>(); var content = sr != null ? sr.content : null;
        if (content == null || sr.viewport == null) return;
        float y = content.anchoredPosition.y;
        if (y != _k12LastY) { _k12LastY = y; _k12Settle = 0; return; }
        if (++_k12Settle != 2) return;
        var rt = (RectTransform)sel.transform; var st = (RectTransform)_k12Strip.transform;
        float rowBottom = rt.position.y - rt.rect.height * 0.5f * rt.lossyScale.y, stripTop = st.position.y + st.rect.height * st.lossyScale.y;
        if (rowBottom >= stripTop - 0.01f * st.lossyScale.y) return;
        float max = Math.Max(0f, content.rect.height - sr.viewport.rect.height);
        float to = Math.Min(max, y + (stripTop - rowBottom) / Math.Max(1e-6f, content.lossyScale.y));
        if (to <= y) return;
        _doScroll ??= sr.GetType().GetMethod("DoScrollTo", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Vector2), typeof(Vector2), typeof(float) }, null);
        if (_doScroll != null && _doScroll.Invoke(sr, new object[] { new Vector2(content.anchoredPosition.x, to), content.anchoredPosition, 0.2f }) is System.Collections.IEnumerator e) sr.StartCoroutine(e);
        else content.anchoredPosition = new Vector2(content.anchoredPosition.x, to);
    }

    // ---------------------------------------------------------------- Reset All, the file, forgetting

    static void K12Reset()
    {
        if (!K12View.Any) return;
        K12View.SetCam(0); K12View.SetFights(true); K12View.SetBars(true);
        _barsSavedOff = false;   // (kit #13 integration review: Reset All puts the row back to its default On, so the save writes "bars=on" as kit #12's does)
        if (_camSel != null || _barsTog != null) K12Configure(false);
    }
    static string _k12Kept = "";   // camera= / camera_fights= / bars= lines while kit #12's rows are off: written back as read
    static bool K12Parse(string key, string val, string line)
    {
        if (key != "camera" && key != "camera_fights" && key != "bars") return false;
        if (!K12View.Any) { _k12Kept += line + "\n"; return true; }
        if (key == "camera") { int i = Array.IndexOf(K12View.CamKeys, val); if (i >= 0 && K12View.CamRow) K12View.Cam = i; else if (!K12View.CamRow) _k12Kept += line + "\n"; }
        else if (key == "camera_fights" && (val == "on" || val == "off")) { if (K12View.CamRow) K12View.Fights = val == "on"; else _k12Kept += line + "\n"; }
        else if (key == "bars" && (val == "on" || val == "off")) { if (K12View.BarsRow) { K12View.Bars = val == "on" || Render.K13Looks.BarsOnly; _barsSavedOff = val == "off"; } else _k12Kept += line + "\n"; }   // (kit #13 looks: only On)
        return true;
    }
    /// <summary>Kit #13 looks (SHEPHERD_K13_BARS=1): the saved choice was Off. The bars are drawn (only On is offered) but the save keeps writing
    /// "bars=off", so switching the row back (SHEPHERD_K13_BARS=0) finds the owner's choice as he left it -- every save except after the game's
    /// Reset All, which puts the row back to its default On (K12Reset clears this), exactly as kit #12's Reset All does.</summary>
    static bool _barsSavedOff;
    static string K12SaveLines() =>
        (K12View.CamRow ? "camera=" + K12View.CamKeys[K12View.Cam] + "\ncamera_fights=" + (K12View.Fights ? "on" : "off") + "\n" : "") +
        (K12View.BarsRow ? "bars=" + (K12View.Bars && !(Render.K13Looks.BarsOnly && _barsSavedOff) ? "on" : "off") + "\n" : "") + _k12Kept;

    /// <summary>The menu closed (<paramref name="destroy"/> false: the objects go with it) or a build failed (true: kit #12's objects destroyed).</summary>
    static void K12ForgetRows(bool destroy)
    {
        if (destroy) foreach (var t in _k12Made) if (t != null) UObject.Destroy(t.gameObject);
        _k12Made.Clear(); _k12HistRows.Clear();
        if (!destroy) { _k12Text.Clear(); _k12Strip = null; _k12StripText = null; _k12Shown = null; _k12StripNow = null; _k12Panel = null; _k12Game = null; _k12Tab = null; _k12Built = _k12GameBuilt = false; }
        _camSel = _fightTog = _barsTog = null; _camRow = _fightRow = _barsRow = null; _aboutSel = _plugSel = null; _plugRow = null; _plugNow = null;
    }
}
