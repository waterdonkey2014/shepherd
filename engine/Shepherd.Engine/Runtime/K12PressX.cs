using System.Reflection;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #12 boot track (vita/notes/kit12-boot.md section 2): "PRESS ✕ TO START" at the title (owner 2026-10-30: "X skips, X starts" --
/// no accidental start from another button, a stick or the touch screen). The game starts its session on ANY input (MainMenuController's intro
/// waits for SessionManager.HasStarted; the Unify library engages the player on any press while Engagement.GlobalAllowEngagement is set), so
/// while the title waits for that start, Shepherd's input layer passes only Cross (Icalls/VitaPad.cs: every other button and both sticks
/// held back; Icalls/VitaTouch.cs: no new touch press) -- the game's own code still decides everything; and the title's prompt
/// ('Press Any Button', the game's UI/PressAnyButtonToStart term) reads "Press ✕ to Start" in the game's current language, the ✕ drawn
/// with the game's own PlayStation button font (ControlMappings: 'PS4-Filled SDF', Cross = U+E900, the glyph the game's own prompts use).
/// The translations are OUR OWN, one per language the game ships (I2's language list), each written from the English sentence "Press ✕ to
/// Start" alone -- never from the game's text (review fix: the first drafts edited the game's own sentences, game-derived text; replaced) -- a
/// native speaker should check them (kit12-boot.md "Needs the owner"). The prompt's font has no ✕ character, so without the button font
/// registered it falls back to the letter X (one line says so).
/// Switch: SHEPHERD_K12_PRESSX=1, or on with SHEPHERD_K12_BOOTSEQ=1 unless SHEPHERD_K12_PRESSX=0 (default OFF in this kit). SHEPHERD_STRICT_ORIGINAL=1
/// keeps any button and the game's text. SHEPHERD_K12_BOOT=0 = every kit #12 boot row off.</summary>
[ProfileOnly]
public static class K12PressX
{
    public static readonly bool On = Decide();
    static bool Decide()
    {
        if (!K12Boot.Master || Environment.GetEnvironmentVariable("SHEPHERD_STRICT_ORIGINAL") == "1") return false;
        var px = Environment.GetEnvironmentVariable("SHEPHERD_K12_PRESSX");
        if (px is { Length: > 0 }) return px.Trim() == "1";
        return K12Boot.Row("SHEPHERD_K12_BOOTSEQ", false);
    }

    /// <summary>language (I2's name) -> the prompt, {X} = the Cross glyph. Our own translations of "Press {X} to Start" (written from the English
    /// sentence only; to be checked by native speakers).</summary>
    static readonly Dictionary<string, string> Text = new()
    {
        ["English"] = "Press {X} to Start",
        ["Japanese"] = "{X}ボタンでスタート",
        ["Russian"] = "Нажмите {X} для начала",
        ["French"] = "Appuyez sur {X} pour commencer",
        ["French (Canada)"] = "Appuyez sur {X} pour commencer",
        ["German"] = "Drücke {X}, um zu starten",
        ["Spanish"] = "Pulsa {X} para comenzar",
        ["Portuguese (Brazil)"] = "Pressione {X} para iniciar",
        ["Chinese (Simplified)"] = "按{X}键开始",
        ["Chinese (Traditional)"] = "按{X}鍵開始",
        ["Korean"] = "시작하려면 {X} 버튼을 누르세요",
        ["Italian"] = "Premi {X} per cominciare",
        ["Dutch"] = "Druk op {X} om te starten",
        ["Turkish"] = "Başlamak için {X} tuşuna basın",
        ["Arabic"] = "اضغط على {X} للبدء",
    };
    public static IReadOnlyDictionary<string, string> Drafts => Text;

    static readonly BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static PropertyInfo _allow, _session, _started, _lang; static FieldInfo _allowF, _sessionF;
    static bool _bound, _bindFailed;
    static int _frame = -1; static bool _waiting, _wasWaiting;
    static int _held, _texts;
    static bool _said;

    /// <summary>The game's three types by NAME (review fix: the first version walked GetTypes() of every loaded assembly -- Mono then made a
    /// MonoClass and a RuntimeType for every type of the BCL, Unity, game and plugin assemblies and loaded 17 more assemblies to resolve them:
    /// VLX +5.9 MiB of newlib and +4-7 MiB of live GC heap on 00193 / 00098 with Press X on, the cost VitaMemory's 'scans' part removes from the
    /// game's own scans). Assembly.GetType(name) is a name lookup: only the type asked for is loaded. The names are the ones the scan found
    /// (Unify.Engagement and Unify.SessionManager in UnifyLibrary, I2.Loc.LocalizationManager in Assembly-CSharp); every loaded assembly is
    /// asked, first match wins, as the scan did.</summary>
    static Type Find(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch (Exception) { }
        }
        return null;
    }
    static void Bind()
    {
        _bound = true;
        var eng = Find("Unify.Engagement");
        if (eng != null) { _allow = eng.GetProperty("GlobalAllowEngagement", S); _allowF = _allow == null ? eng.GetField("GlobalAllowEngagement", S) : null; }
        var sm = Find("Unify.SessionManager");
        if (sm != null)
        {
            _session = sm.GetProperty("instance", S) ?? sm.GetProperty("Instance", S); _sessionF = _session == null ? (sm.GetField("instance", S) ?? sm.GetField("Instance", S)) : null;
            _started = sm.GetProperty("HasStarted", I);
        }
        _lang = Find("I2.Loc.LocalizationManager")?.GetProperty("CurrentLanguage", S);
        _bindFailed = (_allow == null && _allowF == null) || (_session == null && _sessionF == null) || _started == null;
        Log.Info("[k12] Press X to Start: " + (_bindFailed ? "NOT bound (the game's Unify.Engagement / Unify.SessionManager were not found): any button starts, as the original" : "bound (name lookups) -- while the title waits for its start, only Cross reaches the game"));
    }
    /// <summary>The title is waiting for its start (computed once a frame).</summary>
    public static bool Waiting
    {
        get
        {
            if (!On) return false;
            int f = Time.FrameCount;
            if (f == _frame) return _waiting;
            _frame = f; _waiting = false;
            if (!_bound) Bind();
            if (_bindFailed) return false;
            try
            {
                bool allow = (_allow?.GetValue(null) ?? _allowF?.GetValue(null)) is true;
                var sm = _session?.GetValue(null) ?? _sessionF?.GetValue(null);
                _waiting = allow && sm != null && _started.GetValue(sm) is false;
            }
            catch (Exception) { _waiting = false; }
            if (_waiting != _wasWaiting) { _wasWaiting = _waiting; Log.Info($"[k12] Press X to Start: f{f} the title {(_waiting ? "waits for its start (only Cross passes)" : "no longer waits")}"); }
            if (_waiting) Prompt();
            return _waiting;
        }
    }
    /// <summary>The title's prompt in the current language, while waiting (the game's Localize component may set its own text again: re-applied).</summary>
    static NMono _tmp; static string _last;
    static void Prompt()
    {
        try
        {
            if (_tmp == null || _tmp.Destroyed)
            {
                _tmp = null;
                foreach (var m in Objects.All.OfType<NMono>()) if (!m.Destroyed && m.Go?.Name == "Press Any Button" && m.Managed?.GetType().Name == "TextMeshProUGUI") { _tmp = m; break; }
                if (_tmp == null) return;
            }
            var lang = _lang?.GetValue(null) as string ?? "English";
            if (!Text.TryGetValue(lang, out var t)) t = Text["English"];
            string glyph = Glyph();
            string want = t.Replace("{X}", glyph);
            var tp = _tmp.Managed.GetType().GetProperty("text");
            if (tp?.GetValue(_tmp.Managed) as string == want) return;
            tp?.SetValue(_tmp.Managed, want); _texts++;
            if (!_said || want != _last) { _said = true; _last = want; Log.Info($"[k12] Press X to Start: the title's prompt in {lang}: \"{t}\" ({(glyph.StartsWith("<font") ? "the Cross glyph of the game's PS4-Filled SDF button font" : "the letter X: the button font is not loaded")})"); }
        }
        catch (Exception e) { if (!_said) { _said = true; Log.Warn("[k12] Press X to Start: the prompt was not set: " + e.GetBaseException().Message); } }
    }
    static string _glyph;
    /// <summary>The Cross glyph as TMP text: the game's PS4 button font registered with TMP's material reference manager (so a font tag finds it).</summary>
    static string Glyph()
    {
        if (_glyph != null) return _glyph;
        object fa = null;
        foreach (var o in Objects.All.OfType<NObj>()) if (o.Managed is UnityEngine.Object uo && uo != null && uo.GetType().Name == "TMP_FontAsset" && uo.name == "PS4-Filled SDF") { fa = uo; break; }
        if (fa == null) return "X";   // (not loaded yet: asked again next frame)
        try
        {
            var mrm = fa.GetType().Assembly.GetType("TMPro.MaterialReferenceManager");
            mrm?.GetMethod("AddFontAsset", S, new[] { fa.GetType() })?.Invoke(null, new[] { fa });
            _glyph = "<font=\"PS4-Filled SDF\"></font>";
        }
        catch (Exception) { _glyph = "X"; }
        return _glyph;
    }
    public static void Held() => _held++;
    public static string Summary() => On ? $"[k12] Press X to Start: {_held} input frame(s) held back while the title waited, the prompt set {_texts} time(s)"
        + (TouchHold ? $"; kit #13: {_k13Touches} touch(es) and {_k13Rear} rear-panel press(es) swallowed whole" : "") : null;

    /// <summary>Device kit #13, track bootc (vita/notes/kit13-bootc.md section 4): ONLY CROSS STARTS -- the owner's kit #12 run: "the touch screen and the
    /// rear touchpad also start the game". Kit #12 held back the touch PRESS but let the pointer MOVE to the finger first (Icalls/VitaTouch.cs's
    /// Arrived step): on the device that move alone engaged the game (k12-step20: the pointer moved at f1729, Rewired assigned player 0 in that
    /// frame, the title stopped waiting at f1732, with the press held back at f1730). Now, while the title waits, a finger on the front panel
    /// reaches the game not at all (no move, no press: the pointer stays where it was, its movement 0), and the rear panel's corners never enter
    /// the pad's buttons (Icalls/VitaPad.cs) -- and a finger or corner that touched during the wait stays swallowed until it leaves the glass, so a
    /// touch held through Cross's start does not click the first menu. Every other button and the sticks stay held back as in kit #12; Cross alone
    /// starts. SHEPHERD_K13_PRESSX_TOUCH=0 (or SHEPHERD_K13_BOOTC=0) = kit #12's rule; strict original (Press X off) keeps the game's own rule.</summary>
    public static readonly bool TouchHold = On && Environment.GetEnvironmentVariable("SHEPHERD_K13_PRESSX_TOUCH") != "0" && Environment.GetEnvironmentVariable("SHEPHERD_K13_BOOTC") != "0";
    static int _k13Touches, _k13Rear;
    public static void SwallowedTouch(int frame, bool rear)
    {
        if (rear) _k13Rear++; else _k13Touches++;
        if (_k13Touches + _k13Rear <= 20) Log.Info($"[k13] Press X to Start: f{frame} a {(rear ? "rear-panel corner" : "touch")} while the title waits -- swallowed (only Cross starts)");
    }
}
