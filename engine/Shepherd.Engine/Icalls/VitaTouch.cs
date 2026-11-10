using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Impl;

/// <summary>kit #5 track touch (vita/notes/kit5-touch.md): the Vita's FRONT touch screen as the game's MOUSE. On the PC the game's menus
/// take the mouse through Unity's legacy Input (the game uses no InputSystem device): Rewired's Unity-input mouse polls
/// Input.GetButton("MouseButton0".."MouseButton6"), Input.GetAxisRaw("MouseAxis1".."MouseAxis3"), mouseScrollDelta / mousePosition /
/// mousePresent once per frame and decides the "last active controller" from them; RewiredStandaloneInputModule (the game's EventSystem
/// module) reads Input.mousePosition / GetMouseButton(Down/Up) and raycasts the canvases (GraphicRaycaster ->
/// RectTransformUtility.PointInRectangle); the game's UINavigatorNew switches the module's mouse input on when the mouse moves 25 px or
/// a mouse button is held, and off again the moment a pad button is pressed; MMButton / MMSelectable / MMScrollRect take hover, click
/// and drag only while the last active controller is not the joystick. A touch drives exactly those reads, as a mouse would:
/// - a finger put down at P: the pointer MOVES to P first (one frame, the button up -- a mouse arrives before it clicks: the hover, the
///   "mouse moved" switch and Rewired's mouse activity happen there), then the left button goes DOWN at P, follows the finger while it
///   is held (a drag: ScrollRect, sliders) and goes UP where the finger leaves the glass;
/// - a tap that touched and left the glass between two engine frames (the host latches it from SceTouch's sample buffer, as the pad's
///   short presses): move to P, down, up -- three frames, the same clicks as a mouse;
/// - in the menus (no player in play: PlayerFarming.playersCount == 0), one frame after the release the pointer leaves the screen (Unity
///   (-1,-1), a mouse moved out of the window): a finger has no hover (Unity's own touch input drops the pointer on release too), nothing
///   stays hovered when a menu changes under it, and every GraphicRaycaster returns before its graphics loop, so the mouse mode costs
///   ~nothing between touches; the mouse mode itself (keyboard prompts, hover and click handling) stays until a pad button gives the pad
///   back by the game's own rule (UINavigatorNew: pad pressed and the mouse not moved -> mouse input off, PlayStation prompts);
/// - with a player in play (gameplay, and the menus opened over it) the pointer RESTS where the finger left, as the PC's mouse rests
///   where it clicked (kit #5 touch review): the game aims at the mouse there (PlayerWeapon's attack / heavy / curse aims,
///   ChoiceIndicator's left / right choice, all through InputManager.General.GetMousePosition while MouseInputActive), and a jump to
///   the corner would be a 25 px "mouse move" re-arming UINavigatorNew's 1.5 s mouse mode with the aim at the bottom-left corner. When
///   the last player is gone (back to the main menu) a resting pointer leaves the screen. SHEPHERD_TOUCH_PARK=always: it leaves after
///   every release, as before the review; SHEPHERD_TOUCH_PARK=never: it rests after every release.
/// Mouse axes: the movement in screen pixels x the Input Manager sensitivity (Rewired's MouseAxis1..3 = 1, Unity's Mouse X / Y = 0.1);
/// no wheel. Input.touchCount stays 0 and touchSupported false (the PC game sees a mouse, not a touch screen). Rear touch: not read.
/// Before the first touch the mouse is where kit #4 had it (Input.mousePosition (0,0,0)) and every read answers as in kit #4; parked at
/// (-1,-1) the main menu camera and the parallax rest as with kit #4's (0,0).
/// Sources (merged, like VitaPad's): the host's front-panel reader (SHEPHERD_VITA_PAD=1: shepherd_vita_touch_read, host.c; report space
/// 1920x1088 -> the 960x544 screen) and a script for tests, SHEPHERD_TOUCH_SCRIPT="items" or "@file" / SHEPHERD_TOUCH_FILE=file, items
/// separated by spaces, ';' or newlines, '#' starts a comment (x,y in screen pixels from the top-left, as on screenshots; F = N or m+N
/// like the pad script):
///   F:tap:x,y                 a tap latched between two frames (as the host reports a quick one)
///   F:touch:x,y[:n]           a finger on the glass at x,y during frames F..F+n-1 (default 1)
///   F:drag:x0,y0:x1,y1[:n]    a finger put down at x0,y0 at frame F, moved in n steps (default 10) to x1,y1, lifted after
/// SHEPHERD_TOUCH=0: none of it -- no touch read, the mouse icalls answer as in kit #4, PointInRectangle stays unimplemented.
/// SHEPHERD_TOUCH_DIAG=1: every pointer step with Rewired's last active controller, the module's mouse switch and the selection, and the
/// module's pointer data (what is under the pointer, entered, pressed, dragged) for 6 frames after it and every 10th frame to 120, with
/// the game's aim inputs (player count, MouseInputActive, the aim point; diagnostics).</summary>
[ProfileOnly]
public static class VitaTouch
{
    public static readonly bool On = Profile.Vita && Environment.GetEnvironmentVariable("SHEPHERD_TOUCH") != "0";
    static readonly bool Native = On && Environment.GetEnvironmentVariable("SHEPHERD_VITA_PAD") == "1";
    static readonly bool DiagOn = On && Environment.GetEnvironmentVariable("SHEPHERD_TOUCH_DIAG") == "1";
    /// <summary>Where the pointer goes after a release (SHEPHERD_TOUCH_PARK): unset = by the game's player count (off the screen with no
    /// player in play, resting where the finger left with one); "always" = off the screen after every release (the behaviour before the
    /// kit #5 touch review: in gameplay the game then aims at the corner for UINavigatorNew's 1.5 s mouse mode); "never" = resting after
    /// every release (the PC's mouse everywhere; tests of the rest where no player can be reached yet).</summary>
    static readonly int ParkMode = !On ? 0 : Environment.GetEnvironmentVariable("SHEPHERD_TOUCH_PARK") switch { "always" => 1, "never" => 2, _ => 0 };
    /// <summary>The last frame a touch began -- the pointer moved to the finger (Diag.VitaHost's menu watch counts it like a pad press: the
    /// hover there can already change the selection) -- and where (screen pixels, top-left origin).</summary>
    public static int TouchFrame = -1;
    public static Vector2 TouchAt;
    /// <summary>While the pointer is on the screen (a touch, until the pointer leaves after the release): Camera-mode root canvases are placed
    /// in front of their camera as Unity does (Render/SceneRenderer.cs CanvasCamera), so the GraphicRaycaster's camera tests see Unity's
    /// geometry; otherwise their layout is kit #4's.</summary>
    public static bool Placed => _phase != Up;

    const int Tap = 0, Touch = 1, Drag = 2;
    sealed class Item { public Diag.MenuClock.At At; public int K, N; public Vector2 A, B; }
    static string _scriptSource = "SHEPHERD_TOUCH_SCRIPT";
    static readonly Item[] Script = On ? Parse(ScriptText()) : Array.Empty<Item>();

    const int Up = 0, Arrived = 1, Down = 2, Released = 3;
    static int _phase;
    static bool _releaseDue;
    static bool _resting;                // Up with the pointer left on the screen where the finger left (a player in play at the release)
    static int _rests, _leaves;
    static Vector2 _pos;                 // screen pixels, origin top-left (the panel's and the screenshots' orientation)
    static int _presses, _taps, _contacts, _nativeReads, _logged, _lastStepFrame = -100;
    static bool _nativeContact;

    static string ScriptText()
    {
        var env = Environment.GetEnvironmentVariable("SHEPHERD_TOUCH_SCRIPT") ?? "";
        var file = Environment.GetEnvironmentVariable("SHEPHERD_TOUCH_FILE");
        if (string.IsNullOrEmpty(file) && env.StartsWith('@') && !Diag.MenuClock.InlineScreenItem(env, false)) { file = env[1..]; env = ""; }   // (kit #13 bootc review fix: "@Screen+N:tap:x,y ..." is a script, not a file)
        if (string.IsNullOrEmpty(file)) return env;
        if (!File.Exists(file)) { Log.Warn("[touch] SHEPHERD_TOUCH_FILE '" + file + "' not found (no scripted touches from a file)"); return env; }
        _scriptSource = env.Length > 0 ? "SHEPHERD_TOUCH_SCRIPT + " + file : file;
        var sb = new System.Text.StringBuilder(env);
        foreach (var line in File.ReadAllLines(file)) { int h = line.IndexOf('#'); sb.Append(' ').Append(h >= 0 ? line[..h] : line); }
        return sb.ToString();
    }

    static Item[] Parse(string s)
    {
        var l = new List<Item>();
        foreach (var item in (s ?? "").Split(new[] { ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = item.Split(':');
            int k = p.Length >= 3 ? p[1].ToLowerInvariant() switch { "tap" => Tap, "touch" => Touch, "drag" => Drag, _ => -1 } : -1;
            if (k >= 0 && Diag.MenuClock.ParseAt(p[0], out var at) && Xy(p[2], out var a))   // (kit #13 bootc review fix: @Screen[+N] too, as the pad script)
            {
                var b = a; int n = k == Drag ? 10 : 1, ni = 3; bool ok = true;
                if (k == Drag) { ok = p.Length >= 4 && Xy(p[3], out b); ni = 4; }
                if (ok && p.Length > ni) ok = k != Tap && p.Length == ni + 1 && int.TryParse(p[ni], out n) && n >= 1;
                if (ok) { l.Add(new Item { At = at, K = k, A = a, B = b, N = n }); continue; }
            }
            Log.Warn("[touch] script: cannot parse '" + item + "' (want F:tap:x,y | F:touch:x,y[:frames] | F:drag:x0,y0:x1,y1[:steps]; F = N, m+N or @Screen[+N]; x,y in screen pixels from the top-left)");
        }
        return l.ToArray();
    }
    static bool Xy(string s, out Vector2 v)
    {
        v = default; var q = s.Split(',');
        if (q.Length != 2 || !float.TryParse(q[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(q[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
        v = new Vector2(x, y); return true;
    }
    static string KindName(int k) => k == Tap ? "tap" : k == Touch ? "touch" : "drag";

    /// <summary>Boot, before the runtime-init callbacks (no-op unless <see cref="On"/>): the mouse names InputState answers through here,
    /// and RectTransformUtility.PointInRectangle (the GraphicRaycaster's hit test; unimplemented before kit #5 -- nothing called it
    /// without a pointer).</summary>
    public static void Install()
    {
        if (!On) return;
        InputState.PointerAxis = MouseAxis;
        InputState.PointerButton = MouseButton;
        int bound = 0;
        for (int i = 0; i < IcallRegistry.Names.Length; i++)
            if (IcallRegistry.Names[i] == "UnityEngine.RectTransformUtility::PointInRectangle_Injected" && Shepherd.Hooks.Icalls.Table[i] == null) { Shepherd.Hooks.Icalls.Table[i] = PointInRectangle; bound++; }
        Log.Info("[touch] front touch screen -> the game's mouse: ON (SHEPHERD_TOUCH=0 turns it off)" + (Native ? "; the host's front-panel reader" : "")
            + (Script.Length > 0 ? "; script: " + Script.Length + " item(s) from " + _scriptSource : "") + "; PointInRectangle bound " + bound
            + (ParkMode == 1 ? "; SHEPHERD_TOUCH_PARK=always: the pointer leaves the screen after every release" : ParkMode == 2 ? "; SHEPHERD_TOUCH_PARK=never: the pointer rests after every release" : ""));
    }

    /// <summary>Unity Input Manager names that read the mouse: Rewired's installer entries (MouseAxis1/2 = movement x/y, MouseAxis3 = the
    /// wheel; sensitivity 1) and Unity's defaults (Mouse X / Mouse Y / Mouse ScrollWheel; sensitivity 0.1). NaN: not a mouse axis.</summary>
    static float MouseAxis(string name)
    {
        if (name == null || name.Length < 7 || name[0] != 'M') return float.NaN;
        switch (name)
        {
            case "MouseAxis1": return InputState.MouseDelta.x;
            case "MouseAxis2": return InputState.MouseDelta.y;
            case "MouseAxis3": return 0f;
            case "Mouse X": return InputState.MouseDelta.x * 0.1f;
            case "Mouse Y": return InputState.MouseDelta.y * 0.1f;
            case "Mouse ScrollWheel": return 0f;
            default: return float.NaN;
        }
    }
    /// <summary>"MouseButton0".."MouseButton6" (Rewired's installer entries, positive button "mouse N"): 1 / 0 for the phase (0 held, 1 down,
    /// 2 up); -1: not a mouse button name.</summary>
    static int MouseButton(string name, int phase)
    {
        if (name == null || name.Length != 12 || !name.StartsWith("MouseButton", StringComparison.Ordinal)) return -1;
        int b = name[11] - '0'; if (b < 0 || b > 6) return -1;
        int bits = phase == 0 ? InputState.MouseHeld : phase == 1 ? InputState.MouseDownBits : InputState.MouseUpBits;
        return (bits >> b) & 1;
    }

    /// <summary>RectTransformUtility.PointInRectangle(screenPoint, rect, cam, offset) -- the GraphicRaycaster's test of every raycast target
    /// on a canvas, every frame while the mouse is in use: is the screen point inside the rect, shrunk by the raycast padding (x left,
    /// y bottom, z right, w top)? Under a screen-space canvas (overlay or camera mode) it is answered in the picture's own mapping: the
    /// matrix the UI pass draws the rect with (Render/SceneRenderer.cs UiScreenMatrix), inverted in the canvas plane -- what Unity's
    /// camera ray computes when the canvas fills the camera's view, and exact for what is drawn, wherever the root sits in the world. A
    /// world-space canvas: Unity's managed ScreenPointToWorldPointInRectangle math (the camera's ray -- one per camera, point, frame and
    /// camera position --, the rect's plane, InverseTransformPoint). (The first version ran the managed chain through five icalls with the
    /// camera's projection rebuilt twice per graphic: ~+50 ms per idle-menu frame under vlx.)</summary>
    static object PointInRectangle(object[] a)
    {
        var sp = (Vector2)a[0]; var pad = (Vector4)a[3];
        var t = Shepherd.Engine.Native.Objects.Get<Shepherd.Engine.Native.NTransform>(a[1]);
        if (t == null || t.Destroyed || t.Go == null) return Boxes.False;
        float lx, ly;
        if (Render.K12Ui.RayMemo ? Render.SceneRenderer.UiScreenMatrixMemo(t, out var m) : Render.SceneRenderer.UiScreenMatrix(t, out m))   // (kit #12 menusa: the memo, off by default)
        {
            float det = m.m00 * m.m11 - m.m01 * m.m10;
            if (!(MathF.Abs(det) > 1e-12f)) return Boxes.False;
            float bx = sp.x - m.m03, by = sp.y - m.m13;
            lx = (bx * m.m11 - m.m01 * by) / det; ly = (m.m00 * by - bx * m.m10) / det;
        }
        else
        {
            Vector3 o, d;
            if (a[2] != null && Shepherd.Engine.Native.Objects.Get<Shepherd.Engine.Native.NComponent>(a[2]) is { Go: not null } cam) RayOf(cam, sp, out o, out d);
            else { o = new Vector3(sp.x, sp.y, -100f); d = new Vector3(0f, 0f, 1f); }
            // Plane(rect.rotation * Vector3.back, rect.position) -> Raycast -> GetPoint, as Unity's managed code (on plain floats)
            var pos = t.Position; var n = Shepherd.Engine.Native.M.Rotate(t.Rotation, new Vector3(0f, 0f, -1f));
            float nl = MathF.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
            if (nl > 1e-5f) { n.x /= nl; n.y /= nl; n.z /= nl; } else n = default;   // Vector3.Normalize
            float dist = -(n.x * pos.x + n.y * pos.y + n.z * pos.z);
            float den = d.x * n.x + d.y * n.y + d.z * n.z, num = -(o.x * n.x + o.y * n.y + o.z * n.z) - dist, enter = 0f;
            bool hit = false;
            if (MathF.Abs(den) >= Math.Max(1e-6f * MathF.Abs(den), float.Epsilon * 8f)) { enter = num / den; hit = enter > 0f; }   // !Mathf.Approximately(den, 0)
            float ex = pos.x - o.x, ey = pos.y - o.y, ez = pos.z - o.z, el = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            bool facing = el > 1e-5f && (ex * n.x + ey * n.y + ez * n.z) / el != 0f;   // Vector3.Dot(Vector3.Normalize(rect.position - ray.origin), normal) != 0
            if (facing && !hit) return Boxes.False;
            var lp = Shepherd.Engine.Native.M.Point(t.WorldToLocal, new Vector3(o.x + d.x * enter, o.y + d.y * enter, o.z + d.z * enter));
            lx = lp.x; ly = lp.y;
        }
        var r = t.RectF;
        float x0 = r.X + pad.x, y0 = r.Y + pad.y, x1 = r.X + r.W - pad.z, y1 = r.Y + r.H - pad.w;
        return Boxes.Of(lx >= x0 && lx < x1 && ly >= y0 && ly < y1);
    }
    static Shepherd.Engine.Native.NComponent _rayCam; static Vector2 _raySp; static int _rayFrame = -1; static long _rayStamp; static Vector3 _rayO, _rayD;
    static void RayOf(Shepherd.Engine.Native.NComponent cam, Vector2 sp, out Vector3 o, out Vector3 d)
    {
        long stamp = cam.Go.Transform.ChainStamp; int frame = Runtime.Time.FrameCount;
        if (!ReferenceEquals(cam, _rayCam) || sp != _raySp || frame != _rayFrame || stamp != _rayStamp)
        {
            var ray = CameraIcalls.ScreenPointToRay(cam, sp);
            _rayCam = cam; _raySp = sp; _rayFrame = frame; _rayStamp = stamp; _rayO = ray.origin; _rayD = ray.direction;
        }
        o = _rayO; d = _rayD;
    }

    /// <summary>Once per frame after VitaPad.Tick (no-op unless <see cref="On"/>): this frame's finger -> one pointer step.</summary>
    public static void Tick(int frame)
    {
        if (!On) return;
        bool contact = false, tap = false; Vector2 cpos = default, tpos = default;
        if (Native) ReadNative(ref contact, ref cpos, ref tap, ref tpos);
        foreach (var it in Script)
        {
            int f = it.At.Frame; if (f == int.MaxValue) continue;
            if (it.K == Tap) { if (frame == f) { tap = true; tpos = it.A; } }
            else if (it.K == Touch) { if (frame >= f && frame < f + it.N) { contact = true; cpos = it.A; } }
            else if (frame >= f && frame <= f + it.N) { contact = true; cpos = Vector2.Lerp(it.A, it.B, (frame - f) / (float)it.N); }
            if (frame == f && _logged < 200)
            {
                _logged++;
                Log.Info("[touch] script " + KindName(it.K) + " at (" + it.A.x.ToString("F0") + "," + it.A.y.ToString("F0") + ")"
                    + (it.K == Drag ? " -> (" + it.B.x.ToString("F0") + "," + it.B.y.ToString("F0") + ") in " + it.N + " steps" : it.K == Touch ? " for " + it.N + " frame(s)" : ""));
            }
        }
        Step(frame, contact, cpos, tap, tpos);
    }

    /// <summary>The pointer's state machine: Up (parked off the screen, resting where the finger left, or kit #4's (0,0,0) before the first
    /// touch) -(finger or tap)-> Arrived (moved there, button up) -> Down (button down; follows the finger) -(finger gone, or a tap's
    /// release)-> Released (button up where it left) -> Up:
    /// - no player in play (the menus): the pointer LEAVES the screen (Unity (-1,-1): a mouse moved out of the window). A finger has no
    ///   hover: Unity's own touch input drops its pointer on release in the same way (the module's RemovePointerData), and with the pointer
    ///   outside the screen every GraphicRaycaster returns before its graphics loop (the viewport test) -- a resting pointer on the screen
    ///   made the game raycast every canvas every frame until a pad button (VLX: +8-13 ms per idle-menu frame), hovered whatever a new menu
    ///   put under it and leaned the main menu camera toward the last touch. Parked at (-1,-1), the camera and the parallax rest as with
    ///   kit #4's mouse at (0,0);
    /// - a player in play (PlayerFarming.playersCount > 0, the count UINavigatorNew's mouse rule branches on): the pointer RESTS there, as
    ///   the PC's mouse after a click. The game aims at the mouse while UINavigatorNew keeps the mouse mode (1.5 s after the last 25 px move
    ///   or click; with the Blunderbuss as long as the mouse is the last active controller): a jump to the corner is such a move, and the
    ///   aim would turn to the bottom-left corner. Resting, Camera-mode canvases keep kit #4's layout (Placed is false: hover on them waits
    ///   for the next touch, which places them before it clicks); every 8th frame the count is read again, and when the last player is gone
    ///   the pointer leaves the screen as above.
    /// A finger already down again at the release's next frame moves there instead of leaving.</summary>
    static void Step(int frame, bool contact, Vector2 cpos, bool tap, Vector2 tpos)
    {
        if (Runtime.K12PressX.TouchHold)
        {   // kit #13 bootc: while the title waits for Press X, the finger reaches the game not at all -- kit #12's pointer MOVE engaged it on the
            // device (Runtime/K12PressX.cs TouchHold); a finger that touched during the wait stays swallowed until it leaves the glass
            bool waiting = Runtime.K12PressX.Waiting;
            if ((contact || tap) && !_k13Swallow && waiting) { _k13Swallow = true; Runtime.K12PressX.Held(); Runtime.K12PressX.SwallowedTouch(frame, false); }
            if (_k13Swallow) { if (!contact) _k13Swallow = false; contact = false; tap = false; }
            if (waiting)
            {   // review fix: the pointer's state machine stands still while the title waits. A finger down (or just released, or a tap) from BEFORE
                // the wait would otherwise step on during it -- Down -> Released (a button-up), then Released -> Up: the pointer leaves the screen,
                // a MOVE, the device's kit #12 start trigger (the game engages on any action's button-down, a mouse axis included: Unify's
                // Engagement / RewiredInputManager.GetGamePadWithAnyAction). Frozen: no event, no move (InputState.Pump zeroed this frame's
                // delta); a button held from before stays held (no new press: the game needs a button-DOWN). A press still pending (Arrived: the
                // finger landed the frame before) is dropped -- the pointer is left as released, so once the wait is over it leaves the screen
                // with no click. After the wait the machine goes on: a swallowed finger reads as gone, so its release and leave come then.
                if (_phase == Arrived) { _phase = Released; _releaseDue = false; Runtime.K12PressX.Held(); }
                return;
            }
        }
        var old = _pos; string what = null; bool wasOn = _phase != Up;
        switch (_phase)
        {
            case Up:
            case Released:
                if (contact) { _pos = cpos; _phase = Arrived; _releaseDue = false; _resting = false; what = "move"; }
                else if (tap) { _pos = tpos; _phase = Arrived; _releaseDue = true; _resting = false; _taps++; what = "move (tap)"; }
                else if (_phase == Released)
                {
                    _phase = Up;
                    if (ParkMode == 1 || (ParkMode == 0 && !PlayerInPlay())) { _pos = Off; _leaves++; what = "leave (off the screen)"; }
                    else { _resting = true; _rests++; what = ParkMode == 2 ? "rest (SHEPHERD_TOUCH_PARK=never)" : "rest (a player in play)"; }
                }
                else if (_resting && ParkMode == 0 && (frame & 7) == 0 && !PlayerInPlay()) { _pos = Off; _resting = false; _leaves++; what = "leave (no player in play now)"; }
                if (_phase == Arrived) { if (TouchFrame < 0) Log.Info("[touch] f" + frame + " first touch"); TouchFrame = frame; TouchAt = _pos; }
                break;
            case Arrived:   // the press where the finger landed (the point aimed at); a finger already moving is followed from the next frame
                if (Runtime.K12PressX.On && Runtime.K12PressX.Waiting) { _phase = Up; Runtime.K12PressX.Held(); what = "held back (kit #12: Press X to Start)"; break; }   // kit #12 boot: no touch press while the title waits
                if (!contact) _releaseDue = true;
                _phase = Down; InputState.MouseDownBits |= 1; InputState.MouseHeld |= 1; _presses++; what = "down";
                break;
            default:
                if (_releaseDue || !contact) { _phase = Released; _releaseDue = false; InputState.MouseHeld &= ~1; InputState.MouseUpBits |= 1; what = "up"; }
                else _pos = cpos;
                break;
        }
        if (wasOn != (_phase != Up)) { _placements++; Render.SceneRenderer.LayoutRootCanvasesNow(); }   // Camera-mode canvases: Unity's placement while the pointer is on the screen
        if (what != null) _lastStepFrame = frame;
        if (TouchFrame < 0) return;   // no touch yet: the mouse stays where kit #4 had it (Input.mousePosition (0,0,0), no movement)
        var d = _pos - old;
        InputState.MouseDelta = new Vector2(d.x, -d.y);   // Unity: +y up
        InputState.Mouse = new Vector3(_pos.x, AppIcalls.ScreenH - _pos.y, 0f);
        if (what != null && (_presses <= 100 || DiagOn))
            Log.Info("[touch] f" + frame + " pointer " + what + " at (" + _pos.x.ToString("F1") + "," + _pos.y.ToString("F1") + ") = mouse (" + InputState.Mouse.x.ToString("F1") + ","
                + InputState.Mouse.y.ToString("F1") + ")" + (DiagOn ? " " + DiagLine() : "") + (_presses == 100 && what == "down" ? " (further steps are not logged)" : ""));
        else if (DiagOn && _phase == Down && (d.x != 0 || d.y != 0)) Log.Info("[touch] f" + frame + " pointer drag to (" + _pos.x.ToString("F1") + "," + _pos.y.ToString("F1") + ") " + DiagLine());
    }
    static int _placements;
    static bool _k13Swallow;   // kit #13 bootc: a finger that touched while the title waited (Press X), swallowed until it leaves the glass
    static Vector2 Off => new Vector2(-1f, AppIcalls.ScreenH + 1f);   // Unity (-1,-1): off the screen, below its left edge

    static FieldInfo _playersCount; static bool _playersLooked;
    /// <summary>Is a player in play -- the game's PlayerFarming.playersCount > 0, the count UINavigatorNew's mouse rule branches on (0: the
    /// menus' branch, no aim). Read at a release and, while the pointer rests, every 8th frame (one boxed int per read). Not found (a test
    /// without the game's assembly): no player.</summary>
    static bool PlayerInPlay()
    {
        if (!_playersLooked)
        {
            _playersLooked = true;
            try { _playersCount = Assembly.Load("Assembly-CSharp").GetType("PlayerFarming")?.GetField("playersCount", S); } catch { }
            if (_playersCount == null || _playersCount.FieldType != typeof(int)) { _playersCount = null; Log.Warn("[touch] PlayerFarming.playersCount not found: the pointer leaves the screen after every release"); }
        }
        try { return _playersCount != null && (int)_playersCount.GetValue(null) > 0; } catch { return false; }
    }

    /// <summary>One read of the host's front-panel reader (flags: 4 = a sample was read, 1 = a finger on the glass at x,y, 2 = a tap
    /// latched since the last read at x,y; x,y in the panel's report space, w,h its size). Never inlined: the P/Invoke is bound only when
    /// the native reader is on.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ReadNative(ref bool contact, ref Vector2 cpos, ref bool tap, ref Vector2 tpos)
    {
        int flags = shepherd_vita_touch_read(out int x, out int y, out int w, out int h);
        if ((flags & 4) == 0) return;
        _nativeReads++;
        if ((flags & 3) == 0) { _nativeContact = false; return; }
        var p = new Vector2(w > 0 ? x * (float)AppIcalls.ScreenW / w : x * 0.5f, h > 0 ? y * (float)AppIcalls.ScreenH / h : y * 0.5f);
        if ((flags & 1) != 0) { contact = true; cpos = p; if (!_nativeContact) _contacts++; _nativeContact = true; }
        else { tap = true; tpos = p; _nativeContact = false; }
    }
    [DllImport("__Internal")] static extern int shepherd_vita_touch_read(out int x, out int y, out int w, out int h);

    /// <summary>End of the run: one summary line (no-op unless <see cref="On"/>).</summary>
    public static void Summary()
    {
        if (On) Log.Info("[touch] summary: " + _presses + " press(es) (" + _taps + " latched tap(s), " + _contacts + " finger contact(s) read), " + _nativeReads
            + " panel reads" + (TouchFrame >= 0 ? ", pointer at (" + _pos.x.ToString("F1") + "," + _pos.y.ToString("F1") + ")" + (_resting ? " resting" : "") + ", left the screen " + _leaves
            + " time(s), rested where it left " + _rests + " time(s); Camera-mode canvases placed for the pointer " + _placements / 2
            + " time(s), " + Render.SceneRenderer.TouchLayouts + " layouts, " + Render.SceneRenderer.TouchHolds + " of them kept within 0.5 px" : ", never touched"));
    }

    // ---- diagnostics (SHEPHERD_TOUCH_DIAG=1): read-only, by reflection where the types are the game's ----
    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object Member(object o, string n) => o?.GetType().GetProperty(n, I)?.GetValue(o) ?? o?.GetType().GetField(n, I)?.GetValue(o);
    static string Name(GameObject g) => g != null ? g.name : "";

    static string DiagLine()
    {
        try
        {
            object last = null, ctrls = null;
            var re = Type.GetType("Rewired.ReInput, Rewired_Core");
            var players = re?.GetProperty("players", S)?.GetValue(null);
            MethodInfo getPlayer = null;
            if (players != null) foreach (var m in players.GetType().GetMethods(I)) if (m.Name == "GetPlayer" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int)) { getPlayer = m; break; }
            ctrls = Member(getPlayer?.Invoke(players, new object[] { 0 }), "controllers");
            if (ctrls != null) foreach (var m in ctrls.GetType().GetMethods(I)) if (m.Name == "GetLastActiveController" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { last = m.Invoke(ctrls, null); break; }
            var es = UnityEngine.EventSystems.EventSystem.current;
            var mod = es != null ? es.currentInputModule : null;
            return "| rewired p0 last=" + Member(last, "type") + " '" + Member(last, "name") + "' | module " + mod?.GetType().Name + " allowMouseInput=" + Member(mod, "allowMouseInput")
                + " | selected='" + (es != null ? Name(es.currentSelectedGameObject) : "") + "'" + DiagGame();
        }
        catch (Exception e) { return "| diag failed: " + e.GetBaseException().Message; }
    }

    /// <summary>The game's aim inputs: PlayerFarming.playersCount (and Instance), InputManager.General.MouseInputActive (the mouse mode
    /// the aim code checks), GetMousePosition(PlayerFarming.Instance) (the point it aims at: Rewired's mouse screenPosition, Unity screen
    /// pixels, bottom-left origin; with no player the general player's mouse), and with a player its state.facingAngle.</summary>
    static string DiagGame()
    {
        try
        {
            var asm = Assembly.Load("Assembly-CSharp"); var pf = asm.GetType("PlayerFarming");
            if (!(pf?.GetField("playersCount", S)?.GetValue(null) is int n)) return " | game: PlayerFarming.playersCount not found";
            var inst = pf.GetField("Instance", S)?.GetValue(null);
            var gen = asm.GetType("InputManager")?.GetProperty("General", S)?.GetValue(null);
            object aim = null;
            if (gen != null) foreach (var m in gen.GetType().GetMethods(I)) if (m.Name == "GetMousePosition" && m.GetParameters().Length == 1) { aim = m.Invoke(gen, new[] { inst }); break; }
            var st = Member(inst, "state");
            return " | game: players=" + n + (inst != null ? "" : " (Instance null)") + " MouseInputActive=" + Member(gen, "MouseInputActive") + " aim=" + (aim is Vector2 v ? "(" + v.x.ToString("F1") + "," + v.y.ToString("F1") + ")" : "?")
                + (st != null ? " facing=" + (Member(st, "facingAngle") is float fa ? fa.ToString("F1") : "?") + " state=" + Member(st, "CURRENT_STATE") : "");
        }
        catch (Exception e) { return " | game diag failed: " + e.GetBaseException().Message; }
    }

    /// <summary>After every frame (no-op unless SHEPHERD_TOUCH_DIAG=1): within 6 frames of a pointer step, the EventSystem module's left-button
    /// pointer data (enter, press, drag, eligible for a click, the raycast), the selection and the game's aim inputs; then every 10th
    /// frame up to 120 frames after it (UINavigatorNew's 1.5 s mouse mode).</summary>
    public static void After(int frame)
    {
        if (!DiagOn || TouchFrame < 0) return;
        int since = frame - _lastStepFrame;
        if (since > 6 && !(since <= 120 && since % 10 == 0)) return;
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current; var mod = es != null ? es.currentInputModule : null;
            FieldInfo f = null; for (var t = mod?.GetType(); t != null && f == null; t = t.BaseType) f = t.GetField("m_PlayerPointerData", I);
            var sb = new System.Text.StringBuilder("[touch] after f").Append(frame).Append(": selected='").Append(es != null ? Name(es.currentSelectedGameObject) : "").Append('\'');
            if (f?.GetValue(mod) is System.Collections.IDictionary d)
                foreach (System.Collections.DictionaryEntry e in d)
                    if (e.Value is System.Collections.IEnumerable arr)
                        foreach (var inner in arr)
                            if (inner is System.Collections.IDictionary dd)
                                foreach (System.Collections.DictionaryEntry pe in dd)
                                    if (pe.Value is UnityEngine.EventSystems.PointerEventData p && pe.Key is int id && id == -1)
                                        sb.Append(" | left: enter='").Append(Name(p.pointerEnter)).Append("' press='").Append(Name(p.pointerPress)).Append("' drag='").Append(Name(p.pointerDrag))
                                          .Append("' dragging=").Append(p.dragging).Append(" eligible=").Append(p.eligibleForClick).Append(" cur='").Append(Name(p.pointerCurrentRaycast.gameObject)).Append('\'');
            sb.Append(" | mouse (").Append(InputState.Mouse.x.ToString("F1")).Append(',').Append(InputState.Mouse.y.ToString("F1")).Append(')').Append(DiagGame());
            Log.Info(sb.ToString());
        }
        catch (Exception e) { Log.Info("[touch] after: diag failed: " + e.GetBaseException().Message); }
    }
}
