using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Impl;

/// <summary>Vita profile input (V4; polish round 1). The Vita has a gamepad and nothing else, and Rewired runs on its Unity-input path
/// (ConfigVars.alwaysUseUnityInput: no Rewired_OSX native input, no IOKit/CoreFoundation/Rewired_MacOS). Since polish round 1 the pad
/// is a Unity-input JOYSTICK that the game's Rewired data identifies as a DualShock 4 (see <see cref="AsKeys"/>): the game's own
/// joystick maps and PlayStation prompts. SHEPHERD_VITA_PAD_KEYS=1 restores V4's routing: the pad as the Unity KeyCodes the game's
/// keyboard map reads (<see cref="Map"/>), no joystick. Sources:
/// - an SDL gamepad (SDL3 on the Vita exposes the built-in pad as one: cross=SOUTH, circle=EAST, square=WEST, triangle=NORTH);
/// - a scripted virtual pad, SHEPHERD_PAD="frame:button[:holdFrames],..." (hold default 6 frames, like SHEPHERD_PRESS), e.g.
///   SHEPHERD_PAD="1050:cross,1400:down". Buttons: cross circle square triangle up down left right (d-pad) l r start select, and
///   lup ldown lleft lright (the left stick, full deflection).
/// - Stage B, SHEPHERD_VITA_PAD=1 (the Vita GL host): the built-in controls themselves, read once per frame through the host's
///   shepherd_vita_pad_read (SceCtrl, analog mode): the buttons by their SceCtrl bits, the left stick (as keys: the d-pad, threshold 0.5),
///   L trigger/L1 = l, R trigger/R1 = r. Holding START+SELECT (the host times 1.5 s) ends the boot cleanly (the END log line).
///   Script and native presses merge, so pad.txt still drives a run with nobody at the controls.
/// The key table (and why) is in vita/notes/v4-profile.md; the joystick in vita/notes/polish-round1.md.</summary>
[ProfileOnly]
public static class VitaPad
{
    /// <summary>LUp/LDown/LLeft/LRight (polish round 1): the left stick pushed fully, for pad scripts (movement in gameplay: the game's
    /// joystick maps put Move on the left stick and Track Quest / navigation on the d-pad). As keys they are W/S/A/D like the d-pad.</summary>
    /// <summary>Kit #8 play (appended: the old values keep their numbers): L2 / R2 / L3 / R3 -- the rear touch corners on the device, script
    /// buttons for tests -- and RUp / RDown / RLeft / RRight, the right stick pushed fully (script-only).</summary>
    public enum B { Cross, Circle, Square, Triangle, Up, Down, Left, Right, L, R, Start, Select, LUp, LDown, LLeft, LRight, L2, R2, L3, R3, RUp, RDown, RLeft, RRight }
    // Unity KeyCodes: Tab 9, Escape 27, Space 32, A 97, D 100, E 101, K 107, Q 113, R 114, S 115, W 119, LeftShift 304.
    // Actions are the game's own keyboard map (Rewired categories: 0 = gameplay, 1 = UI); see vita/notes/v4-profile.md.
    public static readonly Dictionary<B, int[]> Map = new()
    {
        [B.Cross] = new[] { 32, 101 },   // Space + E: engage ("Press Any Button"); UI Confirm (E); gameplay Interact/AdvanceDialogue (E) + Dodge (Space)
        [B.Circle] = new[] { 27 },       // Escape: UI Cancel / CancelBinding; gameplay Pause
        [B.Square] = new[] { 107 },      // K: Attack
        [B.Triangle] = new[] { 304 },    // LeftShift: HeavyAttack
        [B.Up] = new[] { 119 }, [B.Down] = new[] { 115 }, [B.Left] = new[] { 97 }, [B.Right] = new[] { 100 },   // W S A D: Vertical/Horizontal, UI_Vertical/UI_Horizontal (d-pad + left stick)
        [B.L] = new[] { 113 },           // Q: UI PageLeft; gameplay Ability (curse)
        [B.R] = new[] { 114 },           // R: UI PageRight; gameplay Interact 2
        [B.Start] = new[] { 27 },        // Escape: Pause
        [B.Select] = new[] { 9 },        // Tab: Menu
        [B.LUp] = new[] { 119 }, [B.LDown] = new[] { 115 }, [B.LLeft] = new[] { 97 }, [B.LRight] = new[] { 100 },   // script-only stick: as the d-pad
        // kit #8 play (the keyboard map's keys for the actions the DualShock 4 map puts there): R2 Ability = Q, L3 TrackQuest = T, R3 Bleat = B;
        // L2 (only the photo mode's camera focus / sticker rotation, which have no single key) and the right stick: no key
        [B.L2] = Array.Empty<int>(), [B.R2] = new[] { 113 }, [B.L3] = new[] { 116 }, [B.R3] = new[] { 98 },
        [B.RUp] = Array.Empty<int>(), [B.RDown] = Array.Empty<int>(), [B.RLeft] = Array.Empty<int>(), [B.RRight] = Array.Empty<int>(),
    };
    static readonly HashSet<B> SdlHeld = new();
    static readonly HashSet<B> NativeHeld = new(), PrevWant = new();
    static readonly bool Native = Environment.GetEnvironmentVariable("SHEPHERD_VITA_PAD") == "1";
    static int _nativeLogged;
    /// <summary>The last frame a pad button went down (any source) and which one: Diag.VitaHost's menu watch reads these.</summary>
    public static int PressFrame = -1;
    public static B PressButton;
    // SceCtrl button bits (psp2common/ctrl.h) -> pad buttons; L/R: the Vita's triggers (LTRIGGER/RTRIGGER) or a PS TV pad's L1/R1
    static readonly (uint mask, B b)[] NativeMap =
    {
        (0x4000, B.Cross), (0x2000, B.Circle), (0x8000, B.Square), (0x1000, B.Triangle), (0x0010, B.Up), (0x0040, B.Down), (0x0080, B.Left),
        (0x0020, B.Right), (0x0100 | 0x0400, B.L), (0x0200 | 0x0800, B.R), (0x0008, B.Start), (0x0001, B.Select),
    };
    static readonly HashSet<int> Owned = new();   // keys currently held because of the pad
    static float _lx, _ly;
    static string _scriptSource = "SHEPHERD_PAD";
    /// <summary>Kit #6 integration (owner decision: the Devolver splash plays its 6.27 s, frame-clock references re-based once): the Vita host
    /// sets SHEPHERD_PAD_SHIFT=&lt;frames&gt; on the frame clock when the splash plays, and every ABSOLUTE frame of the pad script (pad.txt's
    /// 1050 / 1400) moves by it; m+N frames (after the Main Menu's arrival) never move. Unset / 0 = kit #5.</summary>
    static readonly int PadShift = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_PAD_SHIFT"), out var ps) && ps > 0 && ps < 100000 ? ps : 0;   // (declared before Script: static fields initialize in textual order)
    static readonly List<(Diag.MenuClock.At frame, B b, int hold)> Script = Parse(PadScript());

    /// <summary>The pad script: SHEPHERD_PAD="frame:button[:hold],...", and/or a file (V6 headless runs, the Vita host's
    /// ux0:data/shepherd/pad.txt): SHEPHERD_PAD_FILE=&lt;path&gt; or SHEPHERD_PAD=@&lt;path&gt;. The file holds the same items separated by
    /// commas, spaces or newlines; '#' starts a comment. Both sources together are merged.</summary>
    static string PadScript()
    {
        var env = Environment.GetEnvironmentVariable("SHEPHERD_PAD") ?? "";
        var file = Environment.GetEnvironmentVariable("SHEPHERD_PAD_FILE");
        if (string.IsNullOrEmpty(file) && env.StartsWith('@') && !Diag.MenuClock.InlineScreenItem(env, true)) { file = env[1..]; env = ""; }   // (kit #13 bootc review fix: "@SaveSlots+30:cross,..." is a script, not a file)
        if (string.IsNullOrEmpty(file)) return env;
        if (!File.Exists(file)) { Console.Error.WriteLine($"SHEPHERD_PAD_FILE: '{file}' not found (no pad events from a file)"); return env; }
        var items = File.ReadAllLines(file).Select(l => l.Split('#')[0]).SelectMany(l => l.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        _scriptSource = env.Length > 0 ? $"SHEPHERD_PAD + {file}" : file;
        return string.Join(",", items.Prepend(env).Where(x => x.Length > 0));
    }

    static List<(Diag.MenuClock.At, B, int)> Parse(string s)
    {
        var l = new List<(Diag.MenuClock.At, B, int)>();
        foreach (var item in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split(':');
            if (p.Length >= 2 && Diag.MenuClock.ParseAt(p[0], out var f) && Enum.TryParse<B>(p[1], true, out var b))   // (kit #13 bootc: @Screen[+N] too)
            {
                if (PadShift != 0 && !f.Rel && f.Screen == null) f = new Diag.MenuClock.At(f.N + PadShift, false);   // (kit #13 bootc: a screen wait is never shifted)
                l.Add((f, b, p.Length > 2 && int.TryParse(p[2], out var h) ? Math.Max(1, h) : 6));
            }
            else Console.Error.WriteLine($"SHEPHERD_PAD: cannot parse '{item}' (want frame:button[:holdFrames]; frame = N or m+N, N frames after the Main Menu scene arrived)");
        }
        return l;
    }

    /// <summary>SDL gamepad button (SDL3 numbering) — called by InputState.Pump under the profile instead of its JoystickButton mapping.</summary>
    public static void SdlButton(int sdlButton, bool down)
    {
        B? b = sdlButton switch { 0 => B.Cross, 1 => B.Circle, 2 => B.Square, 3 => B.Triangle, 4 => B.Select, 6 => B.Start, 9 => B.L, 10 => B.R, 11 => B.Up, 12 => B.Down, 13 => B.Left, 14 => B.Right, _ => null };
        if (b is { } x) { if (down) SdlHeld.Add(x); else SdlHeld.Remove(x); }
    }
    /// <summary>Left stick -> d-pad directions (threshold 0.5).</summary>
    public static void SdlAxis(int axis, float v) { if (axis == 0) _lx = v; else if (axis == 1) _ly = v; }

    /// <summary>Polish round 1: by default the pad is a JOYSTICK to Rewired -- a Unity-input joystick named like a DualShock 4 on macOS,
    /// which the game's own Rewired data identifies ("Sony DualShock 4", fallback_OSX: buttons Square 0 / Cross 1 / Circle 2 / Triangle 3 /
    /// L1 4 / R1 5 / Share 8 / Options 9, d-pad on axes 7 and 8, left stick on axes 1 and 2 (y down), L2/R2 on axes 5 and 6 resting at -1).
    /// Rewired then reads it through the game's joystick maps, and the game's prompts (MMControlPrompt: the last active controller's
    /// map, ControlUtilities.GetCurrentInputType: "Sony" + "DualShock 4" -> the PS4 glyph font) show PlayStation glyphs instead of
    /// keyboard keycaps. SHEPHERD_VITA_PAD_KEYS=1 keeps the V4 behaviour: the pad pressed as keyboard keys (Map), no joystick.</summary>
    public static readonly bool AsKeys = Environment.GetEnvironmentVariable("SHEPHERD_VITA_PAD_KEYS") == "1";
    public const string JoystickName = "Sony Computer Entertainment Wireless Controller";
    static readonly Dictionary<B, int> JoyButton = K8Table(new() { [B.Square] = 0, [B.Cross] = 1, [B.Circle] = 2, [B.Triangle] = 3, [B.L] = 4, [B.R] = 5, [B.Select] = 8, [B.Start] = 9 });
    /// <summary>Kit #8 play, controls parity (vita/notes/kit8-play.md; the game's own DualShock 4 map, read from its Rewired data with
    /// SHEPHERD_K8_PADMAP): Select is the DualShock 4's TOUCHPAD BUTTON (Unity button 13: gameplay 'Menu' -- the pause / map menu --, UI
    /// 'CancelBinding'), not Share (button 8, which the game binds to nothing: kit #7's Select did nothing); L3 / R3 are buttons 10 / 11
    /// (gameplay TrackQuest; Bleat + FleeceAbility; UI Unbind); L2 / R2 the trigger axes 5 / 6 (R2 = Ability, the curse; the photo mode's
    /// camera focus / sticker rotation on both); the right stick axes 3 / 4 (Horizontal / Vertical_Secondary, the photo camera's tilt).
    /// On the device L2 / R2 / L3 / R3 come from the rear touch panel's corners (the host's shepherd_vita_k8_pad; Settings > Graphics >
    /// Rear Touch), the right stick from the Vita's own. SHEPHERD_K8_PAD=0 (or SHEPHERD_K8_PLAY=0): kit #7's table and no right stick /
    /// rear touch.</summary>
    static Dictionary<B, int> K8Table(Dictionary<B, int> d) { if (Runtime.K8Play.PadMap) { d[B.Select] = 13; d[B.L3] = 10; d[B.R3] = 11; } return d; }
    static readonly bool PadK8 = Runtime.K8Play.PadMap;
    /// <summary>Kit #8 play, Settings > Graphics > Rear Touch (Runtime/VitaToggles.cs; vita-settings.txt rear=on|off): the rear panel's
    /// corners as L2 / R2 / L3 / R3. Default On.</summary>
    public static bool RearOn = Runtime.K8Play.Rear;
    static float _rx, _ry;
    const int Joystick1Button0 = 350;   // UnityEngine.KeyCode.Joystick1Button0 (Rewired reads joystick n's button b as 350 + 20 (n - 1) + b)
    /// <summary>Boot, before the runtime-init callbacks: the joystick exists before Rewired initializes (it lists Unity joysticks then).</summary>
    public static void InstallJoystick()
    {
        if (AsKeys) return;
        InputState.JoyName = JoystickName;
        for (int i = 0; i < InputState.JoyAxes.Length; i++) InputState.JoyAxes[i] = i is 4 or 5 ? -1f : 0f;   // triggers at rest
    }

    /// <summary>Kit #8 play, LATE INPUT LATCH (the classic console trick; vita/notes/kit8-play.md): with the Vita's own controls (the native
    /// pad) the controls are read when the game first ASKS for input in a frame -- Rewired's update, the first Input query -- instead of at
    /// the frame's start: the frame's Start pass, its FixedUpdate steps (2-4 a frame at the Vita's frame rates) and anything else before the
    /// game's input update no longer age the sample. The game still sees ONE input state per frame (the read happens once; nothing reads input
    /// before it). A frame that asks for no input at all leaves its read pending: it happens at the next frame's start, after InputState.Pump,
    /// exactly where kit #7 read (review fix: an end-of-frame read put a press into Down just before the next Pump cleared it, so a GetKeyDown /
    /// anyKeyDown reader in the next frame never saw it). Pad scripts and SDL pads run exactly as before (their state is the same whenever it is
    /// applied). SHEPHERD_K8_LATCH=0 (or SHEPHERD_K8_PLAY=0): read at the frame's start (kit #7).</summary>
    static readonly bool LateLatch = Native && Runtime.K8Play.Latch;
    /// <summary>Kit #8 integration: the late latch is on (the pad is read inside the frame, not before its game code) -- Diag/VitaHost.cs's
    /// lazy menu watch (speed track) needs the press known before the frame's game code, so it keeps the per-frame watch while this is on.</summary>
    public static bool LatchOn => LateLatch;
    static int _pendFrame;
    /// <summary>A frame's read is pending (the late latch; InputState's icalls ask before every query).</summary>
    public static bool Pending => _pendFrame != 0;
    static long _pendTicks;
    static int _latchedAtQuery, _latchedNext;
    static readonly List<int> _latchUs = new();
    /// <summary>Once per frame after InputState.Pump: the wanted pad buttons -> wanted keys -> Press/Release the difference (kit #8: with the
    /// late latch, at the frame's first input query instead -- <see cref="Latch"/>).</summary>
    public static void Tick(int frame)
    {
        if (LateLatch) { if (_pendFrame != 0) { _latchedNext++; TickNow(_pendFrame); } _pendFrame = frame; _pendTicks = System.Diagnostics.Stopwatch.GetTimestamp(); return; }   // (a frame without an input query: its read here, as kit #7)
        TickNow(frame);
    }
    /// <summary>Kit #8 late latch: the pending frame's read, at the first input query (InputState's icalls). A no-op when nothing is pending.</summary>
    public static void Latch()
    {
        if (_pendFrame == 0) return;
        int f = _pendFrame; _pendFrame = 0;
        _latchedAtQuery++; if (_latchUs.Count < 4096) _latchUs.Add((int)((System.Diagnostics.Stopwatch.GetTimestamp() - _pendTicks) * 1000000 / System.Diagnostics.Stopwatch.Frequency));
        TickNow(f);
    }
    /// <summary>End of run: where the late latch read the controls.</summary>
    public static string LatchSummary()
    {
        if (!LateLatch || _latchedAtQuery + _latchedNext == 0) return null;
        var a = _latchUs.ToArray(); Array.Sort(a);
        return "[k8] late input latch: " + _latchedAtQuery + " reads at the first input query, median " + (a.Length > 0 ? a[a.Length / 2] : 0) + " us after the frame's start; " + _latchedNext + " at the next frame's start (no input query in the frame)";
    }
    static void TickNow(int frame)
    {
        if (frame == 1 && Script.Count > 0) Log.Info($"[pad] script: {Script.Count} events from {_scriptSource}: {string.Join(",", Script.Select(x => $"{x.frame}:{x.b}" + (x.hold != 6 ? $":{x.hold}" : "")))}");
        if (Native) ReadNative(frame);
        // menu-speed (top hotspots): reused sets when that is what a new one would be (an empty start: a cleared HashSet then enumerates in
        // insertion order exactly as a new one; a copy of a non-empty SdlHeld keeps its free-list layout, so that case still copies)
        var want = SdlHeld.Count == 0 && Render.HotCfg.On ? Cleared(_want) : new HashSet<B>(SdlHeld);
        if (Native) want.UnionWith(NativeHeld);
        if (AsKeys)
        {
            if (_lx < -0.5f) want.Add(B.Left); if (_lx > 0.5f) want.Add(B.Right);
            if (_ly < -0.5f) want.Add(B.Up); if (_ly > 0.5f) want.Add(B.Down);   // SDL: +y is down
        }
        foreach (var (at, b, hold) in Script)
        {
            int f = at.Frame; if (f == int.MaxValue) continue;   // relative to a Main Menu that has not arrived yet
            if (frame >= f && frame < f + hold) want.Add(b);
            if (frame == f) Log.Info($"[pad] {b} -> keys {string.Join("+", Map[b])} (held {hold} frames)");
        }
        bool k12hold = Runtime.K12PressX.On && Runtime.K12PressX.Waiting;   // kit #12 boot: "Press X to Start" -- while the title waits, only Cross (and no stick) reaches the game
        if (k12hold && (want.Count > (want.Contains(B.Cross) ? 1 : 0) || Math.Abs(_lx) > 0.2f || Math.Abs(_ly) > 0.2f || Math.Abs(_rx) > 0.2f || Math.Abs(_ry) > 0.2f))
        { bool x = want.Contains(B.Cross); want.Clear(); if (x) want.Add(B.Cross); Runtime.K12PressX.Held(); }
        foreach (var b in want) if (!PrevWant.Contains(b)) { PressFrame = frame; PressButton = b; }
        PrevWant.Clear(); PrevWant.UnionWith(want);
        HashSet<int> keys;
        if (AsKeys) keys = new HashSet<int>(want.SelectMany(b => Map[b]));
        else
        {
            keys = Render.HotCfg.On ? Cleared(_keys) : new HashSet<int>();
            foreach (var b in want) if (JoyButton.TryGetValue(b, out var jb)) keys.Add(Joystick1Button0 + jb);
            var ax = InputState.JoyAxes;
            if (PadK8)
            {   // kit #8: the right stick (axes 3 / 4: Unity +y down, as the left), the triggers (axes 5 / 6: -1 at rest, +1 pressed)
                ax[2] = Math.Clamp(_rx + (want.Contains(B.RRight) ? 1f : 0f) - (want.Contains(B.RLeft) ? 1f : 0f), -1f, 1f);
                ax[3] = Math.Clamp(_ry + (want.Contains(B.RDown) ? 1f : 0f) - (want.Contains(B.RUp) ? 1f : 0f), -1f, 1f);
                ax[4] = want.Contains(B.L2) ? 1f : -1f;
                ax[5] = want.Contains(B.R2) ? 1f : -1f;
            }
            ax[0] = Math.Clamp(_lx + (want.Contains(B.LRight) ? 1f : 0f) - (want.Contains(B.LLeft) ? 1f : 0f), -1f, 1f);   // left stick (Unity: +y down;
            ax[1] = Math.Clamp(_ly + (want.Contains(B.LDown) ? 1f : 0f) - (want.Contains(B.LUp) ? 1f : 0f), -1f, 1f);       // Rewired's map inverts it)
            ax[6] = (want.Contains(B.Right) ? 1f : 0f) - (want.Contains(B.Left) ? 1f : 0f);   // d-pad x
            ax[7] = (want.Contains(B.Down) ? 1f : 0f) - (want.Contains(B.Up) ? 1f : 0f);      // d-pad y (+down)
            if (k12hold) { ax[0] = 0f; ax[1] = 0f; if (PadK8) { ax[2] = 0f; ax[3] = 0f; ax[4] = -1f; ax[5] = -1f; } }   // (kit #12: the sticks and triggers at rest while the title waits)
        }
        ReleaseNotIn(keys);
        foreach (var k in keys) if (Owned.Add(k)) InputState.Press(k);
    }
    static readonly HashSet<B> _want = new(); static readonly HashSet<int> _keys = new(); static readonly List<int> _release = new();
    static HashSet<T> Cleared<T>(HashSet<T> h) { h.Clear(); return h; }
    /// <summary>foreach (var k in Owned.Where(k => !keys.Contains(k)).ToList()) { Owned.Remove(k); InputState.Release(k); } -- the same keys in the
    /// same order (menu-speed: into a reused list, without the closure).</summary>
    static void ReleaseNotIn(HashSet<int> keys)
    {
        _release.Clear();
        foreach (var k in Owned) if (!keys.Contains(k)) _release.Add(k);
        foreach (var k in _release) { Owned.Remove(k); InputState.Release(k); }
        _release.Clear();
    }

    /// <summary>Stage B: one SceCtrl read from the host (buttons, left stick centred at 0 in -128..127, flags: 1 = START+SELECT held
    /// long enough to quit, 2 = a sample was read, 4 = the host asks to end the session: kit #3's memory guard, its FAIL MEM line says why).
    /// Since the kit #3 review the host reports a button pressed and released between two frames as down for one frame (host.c,
    /// pad_latch.h). Never inlined: the P/Invoke is bound only when the native pad is on.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ReadNative(int frame)
    {
        int flags = shepherd_vita_pad_read(out uint buttons, out int lx, out int ly);
        if ((flags & 4) != 0 && !Boot.QuitRequested) { Log.Warn($"[pad] the host ends the session after frame {frame} (its memory guard: the FAIL MEM line in the host log)"); Boot.QuitRequested = true; }
        if ((flags & 2) == 0) return;
        foreach (var (mask, b) in NativeMap)
        {
            bool down = (buttons & mask) != 0;
            if (down && NativeHeld.Add(b) && _nativeLogged < 200) { _nativeLogged++; Log.Info($"[pad] native {b} down at frame {frame}{(_nativeLogged == 200 ? " (further native presses are not logged)" : "")}"); }
            else if (!down) NativeHeld.Remove(b);
        }
        _lx = lx / 127f; _ly = ly / 127f;   // SceCtrl: y grows downwards, like SDL's axis
        if (PadK8) ReadK8(frame);
        if ((flags & 1) != 0 && !Boot.QuitRequested) { Log.Info($"[pad] START+SELECT held: ending the boot after frame {frame}"); Boot.QuitRequested = true; }
    }
    [DllImport("__Internal")] static extern int shepherd_vita_pad_read(out uint buttons, out int lx, out int ly);
    /// <summary>Kit #8 play: the right stick of the same sample and the rear panel's corners (bit 1 L2, 2 R2, 4 L3, 8 R3; the host latches a
    /// corner tapped between two frames, like the buttons).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ReadK8(int frame)
    {
        shepherd_vita_k8_pad(RearOn ? 1 : 0, out int rx, out int ry, out uint rear);
        _rx = rx / 127f; _ry = ry / 127f;
        foreach (var (bit, b) in RearMap)
        {
            bool down = (rear & bit) != 0;
            if (Runtime.K12PressX.TouchHold)
            {   // kit #13 bootc: while the title waits for Press X, a rear corner never becomes a button; one touched during the wait stays swallowed until released
                if (!down) _k13RearSwallow &= ~bit;
                else if ((_k13RearSwallow & bit) != 0) continue;
                else if (Runtime.K12PressX.Waiting) { _k13RearSwallow |= bit; NativeHeld.Remove(b); Runtime.K12PressX.Held(); Runtime.K12PressX.SwallowedTouch(frame, true); continue; }
            }
            if (down && NativeHeld.Add(b) && _nativeLogged < 200) { _nativeLogged++; Log.Info("[pad] native " + b + " (rear touch) down at frame " + frame); }
            else if (!down) NativeHeld.Remove(b);
        }
    }
    static readonly (uint bit, B b)[] RearMap = { (1, B.L2), (2, B.R2), (4, B.L3), (8, B.R3) };
    static uint _k13RearSwallow;   // kit #13 bootc: rear corners touched while the title waited (Press X), swallowed until released
    [DllImport("__Internal")] static extern int shepherd_vita_k8_pad(int rearOn, out int rx, out int ry, out uint rear);

    static readonly Dictionary<Type, Type> RewiredBase = new();   // kit #5 menus: type -> its Rewired.InputManager_Base base type, or null
    /// <summary>Before Awake of every MonoBehaviour (vita only): put Rewired on its Unity-input path.</summary>
    public static void BeforeAwake(object mb)
    {
        // kit #5 menus: the base-type walk (FullName per base: a runtime string each) once per type, its answer kept: Settings' opening
        // wakes ~5,400 behaviours (45 ms of the Mac frame). SHEPHERD_K5_MENUS=0: the walk every time, as kit #4.
        var mt = mb.GetType();
        if (Runtime.MenusCfg.TypeMemos)
        {
            if (!RewiredBase.TryGetValue(mt, out var hit)) { hit = null; for (var b = mt; b != null; b = b.BaseType) if (b.FullName == "Rewired.InputManager_Base") { hit = b; break; } RewiredBase[mt] = hit; }
            if (hit == null) return;
        }
        for (var t = mt; t != null; t = t.BaseType)
        {
            if (t.FullName != "Rewired.InputManager_Base") continue;
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var ud = t.GetField("_userData", I)?.GetValue(mb);
                var cv = ud?.GetType().GetProperty("ConfigVars", I)?.GetValue(ud) ?? ud?.GetType().GetField("configVars", I)?.GetValue(ud);
                var f = cv?.GetType().GetField("alwaysUseUnityInput", I);
                if (f == null) { Log.Warn("[pad] Rewired ConfigVars.alwaysUseUnityInput not found: Rewired will try its native OSX input"); return; }
                f.SetValue(cv, true);
                Log.Info($"[pad] Rewired '{mb.GetType().FullName}': ConfigVars.alwaysUseUnityInput = true (Unity-input path: keyboard via Input.GetKey, no Rewired_OSX native input)");
            }
            catch (Exception e) { Log.Exception(e, "[pad] Rewired config"); }
            return;
        }
    }

    /// <summary>End-of-run diagnostics (vita only): Rewired's input source + the action names behind the keys the pad presses.</summary>
    public static void DumpRewired()
    {
        try
        {
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var re = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rewired_Core")?.GetType("Rewired.ReInput");
            if (re == null) return;
            Log.Info($"[pad] Rewired: usingUnityInput={re.GetProperty("usingUnityInput", S)?.GetValue(null)} alwaysUseUnityInput={(re.GetProperty("configuration", S)?.GetValue(null) is { } c ? c.GetType().GetProperty("alwaysUseUnityInput", I)?.GetValue(c) : "?")}");
            if (re.GetProperty("mapping", S)?.GetValue(null) is { } mapping && mapping.GetType().GetProperty("Actions", I)?.GetValue(mapping) is System.Collections.IEnumerable acts)
                Log.Info("[pad] Rewired actions: " + string.Join(" ", acts.Cast<object>().Select(x => $"a{x.GetType().GetProperty("id")?.GetValue(x)}={x.GetType().GetProperty("name")?.GetValue(x)}")));
        }
        catch (Exception e) { Log.Info("[pad] rewired dump: " + e.GetBaseException().Message); }
    }
}
