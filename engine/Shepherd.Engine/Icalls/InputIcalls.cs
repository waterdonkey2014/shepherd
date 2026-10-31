using Shepherd.Engine.Render;
using UnityEngine;
namespace Shepherd.Engine.Impl;

/// <summary>Legacy Input fed from SDL events (keyboard, mouse, first gamepad). Pumped once per frame by the host loop.</summary>
public static unsafe class InputState
{
    static readonly HashSet<int> Held = new(), Down = new(), Up = new();
    static readonly float[] Axes = new float[6];   // left x/y, right x/y, LT, RT
    public static Vector3 Mouse; public static bool QuitRequested;
    static IntPtr _pad;
    /// <summary>kit #5 touch (Icalls/VitaTouch.cs): the mouse the game reads -- buttons held (bit i = Unity mouse button i), this frame's
    /// presses / releases (cleared by <see cref="Pump"/>), this frame's movement in screen pixels (Unity: +y up). Only VitaTouch writes
    /// them; without it they stay 0 and every mouse icall answers as before (no button, no movement, <see cref="Mouse"/> at the origin).</summary>
    public static int MouseHeld, MouseDownBits, MouseUpBits;
    public static Vector2 MouseDelta;
    /// <summary>kit #5 touch: the Input Manager axis / button names that read the mouse (Rewired's "MouseAxis1..3" / "MouseButton0..6",
    /// Unity's "Mouse X" / "Mouse Y" / "Mouse ScrollWheel"), answered by VitaTouch when it is on: NaN / -1 = not a mouse name. Null (the
    /// default, SHEPHERD_TOUCH=0, every non-Vita run): those names fall through to the code below, as before.</summary>
    public static Func<string, float> PointerAxis;
    public static Func<string, int, int> PointerButton;

    public static int MapKey(uint sdlKey) => sdlKey switch
    {
        >= 'a' and <= 'z' => (int)sdlKey, >= '0' and <= '9' => (int)sdlKey, 13 => 13, 27 => 27, 8 => 8, 9 => 9, 32 => 32,
        0x4000004F => 275, 0x40000050 => 276, 0x40000051 => 274, 0x40000052 => 273,             // right, left, down, up
        0x400000E1 => 304, 0x400000E5 => 303, 0x400000E0 => 306, 0x400000E4 => 305,             // shifts, ctrls
        >= 0x4000003A and <= 0x40000045 => 282 + (int)(sdlKey - 0x4000003A),                       // F1..F12
        _ => -1
    };
    // SDL gamepad buttons: 0 south(A) 1 east(B) 2 west(X) 3 north(Y) 4 back 5 guide 6 start 7 lstick 8 rstick 9 LB 10 RB 11 up 12 down 13 left 14 right
    static int PadKey(int b) => b switch { 0 => 330, 1 => 331, 2 => 332, 3 => 333, 9 => 334, 10 => 335, 4 => 336, 6 => 337, 7 => 338, 8 => 339, 11 => 1011, 12 => 1012, 13 => 1013, 14 => 1014, _ => -1 };

    public static void Pump()
    {
        Down.Clear(); Up.Clear();
        MouseDownBits = MouseUpBits = 0; MouseDelta = default;   // kit #5 touch: a press / release / movement lasts one frame
        if (Backends.NoSdl) return;   // V6 SHEPHERD_RENDER=null: no SDL in the process; input comes from SHEPHERD_PAD / SHEPHERD_PAD_FILE (VitaPad)
        PumpSdl();
    }
    // Separate and never inlined: the Mono interpreter binds a method's P/Invokes when it transforms it, so SDL_PollEvent is
    // bound only if this method ever runs (never with SHEPHERD_RENDER=null).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void PumpSdl()
    {
        Sdl.Event e;
        while (Sdl.SDL_PollEvent(&e))
        {
            switch (e.type)
            {
                case Sdl.EVENT_QUIT: QuitRequested = true; break;
                case Sdl.EVENT_KEY_DOWN: { int k = MapKey(e.key_key); if (k >= 0 && Held.Add(k)) Down.Add(k); break; }
                case Sdl.EVENT_KEY_UP: { int k = MapKey(e.key_key); if (k >= 0 && Held.Remove(k)) Up.Add(k); break; }
                case Sdl.EVENT_GAMEPAD_ADDED: if (_pad == IntPtr.Zero) { _pad = Sdl.SDL_OpenGamepad(e.which); Log.Info("gamepad connected"); } break;
                case Sdl.EVENT_GAMEPAD_BUTTON_DOWN: { if (Profile.Vita) { VitaPad.SdlButton(e.gbutton, true); break; } int k = PadKey(e.gbutton); if (k >= 0 && Held.Add(k)) Down.Add(k); break; }
                case Sdl.EVENT_GAMEPAD_BUTTON_UP: { if (Profile.Vita) { VitaPad.SdlButton(e.gbutton, false); break; } int k = PadKey(e.gbutton); if (k >= 0 && Held.Remove(k)) Up.Add(k); break; }
                case Sdl.EVENT_GAMEPAD_AXIS_MOTION: if (Profile.Vita) { VitaPad.SdlAxis(e.gaxis, e.gvalue / 32767f); break; } if (e.gaxis < 6) Axes[e.gaxis] = Math.Abs(e.gvalue / 32767f) < 0.2f ? 0 : e.gvalue / 32767f; break;
            }
        }
    }
    /// <summary>Synthetic input for automated tests.</summary>
    public static void Press(int unityKey) { if (Held.Add(unityKey)) Down.Add(unityKey); }
    public static void Release(int unityKey) { if (Held.Remove(unityKey)) Up.Add(unityKey); }

    public static bool Key(int k) => Held.Contains(k);
    /// <summary>Kit #8 late input latch (Icalls/VitaPad.cs Latch): the first input query of a frame reads the Vita's controls; one static
    /// int read otherwise.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static void Q() { if (LatchOn && VitaPad.Pending) VitaPad.Latch(); }
    /// <summary>(VitaPad -- a [ProfileOnly] type -- is touched only when the latch can be on: the Vita profile with the native pad.)</summary>
    static readonly bool LatchOn = Profile.Vita && Runtime.K8Play.Latch && Environment.GetEnvironmentVariable("SHEPHERD_VITA_PAD") == "1";
    /// <summary>Polish round 1 (Vita profile): Unity input's joystick 1 -- named by <see cref="JoyName"/> (null: no joystick), axes
    /// "Joy1Axis1".."Joy1Axis8" (Rewired's Unity-input names) read from here, buttons as KeyCode.Joystick1Button0.. (350..) in the
    /// key set. VitaPad fills both.</summary>
    public static string JoyName;
    public static readonly float[] JoyAxes = new float[8];
    /// <summary>"Joy1AxisN" (1-based): the virtual joystick's axis; any other joystick's axis reads 0. NaN: not a joystick axis name.</summary>
    static float JoyAxis(string name)
    {
        if (name == null || !name.StartsWith("Joy", StringComparison.Ordinal)) return float.NaN;
        int ax = name.IndexOf("Axis", 3, StringComparison.Ordinal);
        if (ax < 0 || !int.TryParse(name.AsSpan(3, ax - 3), out var joy) || !int.TryParse(name.AsSpan(ax + 4), out var n)) return float.NaN;
        return JoyName != null && joy == 1 && n >= 1 && n <= JoyAxes.Length ? JoyAxes[n - 1] : 0f;
    }
    /// <summary>GetAxisRaw: keyboard axes are -1/0/1 (rounded); a joystick axis is its raw analog value, as in Unity.</summary>
    public static float AxisRaw(string name) { float j = JoyAxis(name); return !float.IsNaN(j) ? j : PointerAxis != null && PointerAxis(name) is var m && !float.IsNaN(m) ? m : MathF.Round(Axis(name)); }
    public static float Axis(string name)
    {
        float j = JoyAxis(name); if (!float.IsNaN(j)) return j;
        if (PointerAxis != null && PointerAxis(name) is var m && !float.IsNaN(m)) return m;   // kit #5 touch: a mouse axis is its movement (GetAxis = GetAxisRaw)
        float v = name switch
        {
            "Horizontal" => (Key(275) || Key('d') ? 1 : 0) - (Key(276) || Key('a') ? 1 : 0) + Axes[0] + (Key(1014) ? 1 : 0) - (Key(1013) ? 1 : 0),
            "Vertical" => (Key(273) || Key('w') ? 1 : 0) - (Key(274) || Key('s') ? 1 : 0) - Axes[1] + (Key(1011) ? 1 : 0) - (Key(1012) ? 1 : 0),
            _ => 0
        };
        return Math.Clamp(v, -1f, 1f);
    }
    static readonly int[] BSubmit = { 13, 32, 330 }, BCancel = { 27, 331 }, BFire1 = { 306, 330 }, BFire2 = { 308, 331 }, BJump = { 32, 330 };
    static int[] Button(string name) => name switch
    {
        "Submit" => BSubmit, "Cancel" => BCancel, "Fire1" => BFire1, "Fire2" => BFire2, "Jump" => BJump,
        _ => Array.Empty<int>()
    };
    /// <summary>Any of the button's keys in the phase's set (stage 2: a loop over shared key arrays; was a new array + closure per call).</summary>
    public static bool Button(string name, int phase)
    {
        if (PointerButton != null && PointerButton(name, phase) is var mb && mb >= 0) return mb != 0;   // kit #5 touch: "MouseButton0".. (Rewired's mouse)
        var set = phase == 0 ? Held : phase == 1 ? Down : Up;
        foreach (var k in Button(name)) if (set.Contains(k)) return true;
        return false;
    }

    // polish round 1: shared result boxes (Runtime.Boxes; the stub unboxes at once) -- Rewired polls its joystick's 20 buttons and
    // 29 axes through these every frame under the Vita profile's virtual joystick
    [Icall("UnityEngine.Input::GetKeyInt")] static object GetKey(object[] a) { Q(); return Runtime.Boxes.Of(Held.Contains(KeyInt(a[0]))); }
    [Icall("UnityEngine.Input::GetKeyDownInt")] static object GetKeyDown(object[] a) { Q(); return Runtime.Boxes.Of(Down.Contains(KeyInt(a[0]))); }
    [Icall("UnityEngine.Input::GetKeyUpInt")] static object GetKeyUp(object[] a) { Q(); return Runtime.Boxes.Of(Up.Contains(KeyInt(a[0]))); }
    /// <summary>Convert.ToInt32 of the boxed KeyCode (menu-speed: the enum's int read directly; Convert.ToInt32 on an enum box boxes its value again).</summary>
    static int KeyInt(object o) => o is KeyCode k ? (int)k : Convert.ToInt32(o);
    static readonly object Zero = 0f, PlusOne = 1f, MinusOne = -1f;
    static object Box(float v) => v == 0f && !float.IsNegative(v) ? Zero : v == 1f ? PlusOne : v == -1f ? MinusOne : v;
    [Icall("UnityEngine.Input::get_anyKey")] static object AnyKey(object[] a) { Q(); return Held.Count > 0; }
    [Icall("UnityEngine.Input::get_anyKeyDown")] static object AnyKeyDown(object[] a) { Q(); return Down.Count > 0; }
    // kit #5 touch: the mouse buttons VitaTouch drives (0 without it: false, as before)
    [Icall("UnityEngine.Input::GetMouseButton")] static object MB(object[] a) => Runtime.Boxes.Of(MouseBit(MouseHeld, a[0]));
    [Icall("UnityEngine.Input::GetMouseButtonDown")] static object MBD(object[] a) => Runtime.Boxes.Of(MouseBit(MouseDownBits, a[0]));
    [Icall("UnityEngine.Input::GetMouseButtonUp")] static object MBU(object[] a) => Runtime.Boxes.Of(MouseBit(MouseUpBits, a[0]));
    static bool MouseBit(int bits, object button) => bits != 0 && button is int b && (uint)b < 31u && (bits >> b & 1) != 0;
    [Icall("UnityEngine.Input::get_mousePosition_Injected")] static object MPos(object[] a) { a[0] = Mouse; return null; }
    [Icall("UnityEngine.Input::get_mouseScrollDelta_Injected")] static object MScroll(object[] a) { a[0] = Vector2.zero; return null; }
    [Icall("UnityEngine.Input::get_mousePresent")] static object MPresent(object[] a) => Runtime.Boxes.True;   // kit #9 E5 MIRROR: while Shepherd.Hooks.Mirror.On the stub answers Mirror.MousePresent (true) itself (tools/Rewriter/TypedPass.cs) -- change this body and you must change that formula (Runtime/K9Typed.cs binds the mirror only while THIS method is the key's hook; SHEPHERD_K9_MIRROR=check compares them)
    [Icall("UnityEngine.Input::get_touchCount")] static object Touches(object[] a) => IntZero;   // kit #9 E5 MIRROR: while Shepherd.Hooks.Mirror.On the stub answers Mirror.TouchCount (0) itself (tools/Rewriter/TypedPass.cs) -- change this body and you must change that formula (Runtime/K9Typed.cs binds the mirror only while THIS method is the key's hook; SHEPHERD_K9_MIRROR=check compares them)
    static readonly object IntZero = 0;   // (menu-speed: one int box)
    [Icall("UnityEngine.Input::get_touchSupported")] static object TouchSup(object[] a) => false;
    [Icall("UnityEngine.Input::get_inputString")] static object InStr(object[] a) => "";
    [Icall("UnityEngine.Input::GetJoystickNames")] static object JoyNames(object[] a) => Profile.Vita ? (JoyName != null ? new[] { JoyName } : Array.Empty<string>())   // vita: VitaPad's virtual joystick (or, with SHEPHERD_VITA_PAD_KEYS=1, none: the pad arrives as keys)
        : _pad != IntPtr.Zero ? new[] { "Gamepad" } : Array.Empty<string>();
    [Icall("UnityEngine.Input::ResetInputAxes")] static object ResetAxes(object[] a) => null;
    [Icall("UnityEngine.Input::CheckDisabled")] static object Disabled(object[] a) => false;
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetAxis")] static object GetAxis(object[] a) { Q(); return Box(Axis((string)a[0])); }
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetAxisRaw")] static object GetAxisRaw(object[] a) { Q(); return Box(AxisRaw((string)a[0])); }
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetButton")] static object GetButton(object[] a) { Q(); return Runtime.Boxes.Of(Button((string)a[0], 0)); }   // (menu-speed: the shared bool boxes)
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetButtonDown")] static object GetButtonDown(object[] a) { Q(); return Runtime.Boxes.Of(Button((string)a[0], 1)); }
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetButtonUp")] static object GetButtonUp(object[] a) { Q(); return Runtime.Boxes.Of(Button((string)a[0], 2)); }
    static string Utf8(object p, object n) => System.Text.Encoding.UTF8.GetString((byte*)(IntPtr)p, (int)n);
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetAxis__Unmanaged")] static object GetAxisU(object[] a) { Q(); return Box(Axis(Utf8(a[0], a[1]))); }
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetAxisRaw__Unmanaged")] static object GetAxisRawU(object[] a) { Q(); return Box(AxisRaw(Utf8(a[0], a[1]))); }
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetButton__Unmanaged")] static object GetButtonU(object[] a) { Q(); return Button(Utf8(a[0], a[1]), 0); }
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetButtonDown__Unmanaged")] static object GetButtonDownU(object[] a) { Q(); return (byte)(Button(Utf8(a[0], a[1]), 1) ? 1 : 0); }
    [Icall("UnityEngine.Internal.InputUnsafeUtility::GetButtonUp__Unmanaged")] static object GetButtonUpU(object[] a) { Q(); return Button(Utf8(a[0], a[1]), 2); }
    [Icall("UnityEngine.Cursor::get_visible")] static object CurVis(object[] a) => true;
    [Icall("UnityEngine.Cursor::set_visible")] static object SetCurVis(object[] a) => null;
    [Icall("UnityEngine.Cursor::get_lockState")] static object CurLock(object[] a) => CursorLockMode.None;
    [Icall("UnityEngine.Cursor::set_lockState")] static object SetCurLock(object[] a) => null;
    [Icall("UnityEngine.Cursor::SetCursor_Injected")] static object SetCursor(object[] a) => null;

    // kit #9 track runtime (lever 10; Icalls/K9Reg.cs, Runtime/K9Typed.cs): the typed twins of the hooks above -- the same expressions (the late
    // input latch Q() first, as each hook), no object[] / boxes (Box() and the shared bool boxes hold exactly these values).
    internal static void K9(K9Reg r)
    {
        r.Add("UnityEngine.Internal.InputUnsafeUtility::GetAxis", (Func<string, float>)(n => { Q(); return Axis(n); }), GetAxis);
        r.Add("UnityEngine.Internal.InputUnsafeUtility::GetAxisRaw", (Func<string, float>)(n => { Q(); return AxisRaw(n); }), GetAxisRaw);
        r.Add("UnityEngine.Internal.InputUnsafeUtility::GetButton", (Func<string, bool>)(n => { Q(); return Button(n, 0); }), GetButton);
        r.Add("UnityEngine.Input::GetKeyInt", (Func<int, bool>)(k => { Q(); return Held.Contains(k); }), GetKey);   // KeyInt(boxed KeyCode) = its int
        r.Add("UnityEngine.Input::GetMouseButton", (Func<int, bool>)(b => MouseHeld != 0 && (uint)b < 31u && (MouseHeld >> b & 1) != 0), MB);   // MouseBit(MouseHeld, (object)b)
    }
    /// <summary>kit #9 E5 (review fix): the constant mirrors of this class (key, the hook the stub copies, its answer) -- see RenderIcalls.K9MirrorConsts.</summary>
    internal static IEnumerable<(string key, Shepherd.Hooks.IcallImpl hook, object value)> K9MirrorConsts() => new (string, Shepherd.Hooks.IcallImpl, object)[]
    {
        ("UnityEngine.Input::get_mousePresent", MPresent, true), ("UnityEngine.Input::get_touchCount", Touches, 0),
    };
}
