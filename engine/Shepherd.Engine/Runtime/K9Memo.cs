using System.Reflection;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #9 engine, lookup memos (SHEPHERD_K9_MEMO; Runtime/K9Engine.cs). Two kinds, both exact:
/// <list type="bullet">
/// <item>Message methods: what a reflection walk over a type's metadata finds for (type, name) is a function of the type, which never changes
/// at run time -- found once, then answered from the memo (the same MethodInfo the walk returns; a walk that throws is not memoized, so it
/// throws again as before).</item>
/// <item>Result boxes: an icall's result is boxed into the argument array the rewritten Unity stub passed and unboxed by the stub at once
/// (Icalls.Ret&lt;T&gt;); nothing keeps, compares or writes a result box, and the engine never writes into a box, so a box holding the same
/// bits can serve again: a small direct-mapped table per value type keeps the last box per hash slot; a hit needs equal bits (+0 / -0 and
/// NaN payloads differ) and reads them from the kept box itself, so a table box that someone wrote into is never handed out for a value it no
/// longer holds. A miss boxes as before and keeps the new box. (The int boxes, the bool boxes and the per-site float BoxCaches trust what
/// they stored: they rest on the premise alone.)</item>
/// </list>
/// =check (SHEPHERD_K9_MEMO=check): the message methods are re-walked and compared; every result box is a new one (kit #8's answer) and the
/// premise the shared boxes rest on is checked instead (review fix -- e4's check never looked at the boxes): see <see cref="Fresh"/>.</summary>
[ProfileOnly]
public static class K9Memo
{
    public static readonly bool On = K9Engine.Memo;
    static readonly bool Check = K9Engine.MemoCheck;

    // ---------------- message methods ----------------
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Dictionary<(Type, string), MethodInfo> _send = new(), _invoke = new();
    static readonly Dictionary<Type, MethodInfo> _rectDim = new();

    /// <summary>Core.Send's lookup: on each class from t up to MonoBehaviour, the first DeclaredOnly method named <paramref name="name"/> with at
    /// most one parameter (GetMethods order), the most derived class's first.</summary>
    public static MethodInfo SendTarget(Type t, string name)
    {
        if (!Check && _send.TryGetValue((t, name), out var m)) { K9Engine.MemoMethodHits++; return m; }   // (main thread: the game's messages)
        var w = SendWalk(t, name);
        if (Check && _send.TryGetValue((t, name), out m) && !Same(m, w)) K9Engine.Diff("SendMessage target", t.Name + "." + name);
        _send[(t, name)] = w; K9Engine.MemoMethodMisses++;
        return w;
    }
    static MethodInfo SendWalk(Type t, string name)
    {
        for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
        {
            var mi = c.GetMethods(Any | BindingFlags.DeclaredOnly).FirstOrDefault(q => q.Name == name && q.GetParameters().Length <= 1);
            if (mi != null) return mi;
        }
        return null;
    }

    /// <summary>Invokes.FindMethod (Invoke / InvokeRepeating / StartCoroutine(string)): the first class from t up whose DeclaredOnly
    /// GetMethod(name) finds one (main thread).</summary>
    public static MethodInfo InvokeTarget(Type t, string name)
    {
        if (!Check && _invoke.TryGetValue((t, name), out var m)) { K9Engine.MemoMethodHits++; return m; }
        MethodInfo w = null;
        for (var c = t; c != null; c = c.BaseType) { w = c.GetMethod(name, Any | BindingFlags.DeclaredOnly); if (w != null) break; }
        if (Check && _invoke.TryGetValue((t, name), out m) && !Same(m, w)) K9Engine.Diff("Invoke target", t.Name + "." + name);
        _invoke[(t, name)] = w; K9Engine.MemoMethodMisses++;
        return w;
    }
    static bool Same(MethodInfo a, MethodInfo b) => ReferenceEquals(a, b) || (a != null && b != null && a.MethodHandle == b.MethodHandle);

    /// <summary>SceneRenderer.NotifyRect's lookup: the first class from t up to MonoBehaviour declaring a parameterless
    /// OnRectTransformDimensionsChange.</summary>
    public static MethodInfo RectDimTarget(Type t)
    {
        if (!Check && _rectDim.TryGetValue(t, out var m)) { K9Engine.MemoMethodHits++; return m; }
        MethodInfo w = null;
        for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
        {
            w = c.GetMethod("OnRectTransformDimensionsChange", Any | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (w != null) break;
        }
        if (Check && _rectDim.TryGetValue(t, out m) && !Same(m, w)) K9Engine.Diff("OnRectTransformDimensionsChange", t.Name);
        _rectDim[t] = w; K9Engine.MemoMethodMisses++;
        return w;
    }

    // ---------------- result boxes ----------------
    const int Slots = 512;
    static readonly object[] _v3 = new object[Slots], _q = new object[Slots], _v2 = new object[Slots / 2];
    static object[] _ints = new object[256];
    static uint Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);
    static int Slot(uint a, uint b, uint c, uint d, int n) => (int)(((a * 0x9E3779B1u) ^ (b * 0x85EBCA77u) ^ (c * 0xC2B2AE3Du) ^ (d * 0x27D4EB2Fu)) >> 16) & (n - 1);

    /// <summary>A box of v: a kept one with the same bits, or a new one (kept).</summary>
    public static object Box(Vector3 v)
    {
        if (!On) return v;
        uint x = Bits(v.x), y = Bits(v.y), z = Bits(v.z);
        if (Check) return Fresh(v, KV3, x, y, z);
        int i = Slot(x, y, z, 0, Slots);
        var k = _v3[i]; if (k is Vector3 o && Bits(o.x) == x && Bits(o.y) == y && Bits(o.z) == z) { K9Engine.BoxShared++; return k; }
        object b = v; _v3[i] = b; return b;
    }
    public static object Box(Quaternion v)
    {
        if (!On) return v;
        uint x = Bits(v.x), y = Bits(v.y), z = Bits(v.z), w = Bits(v.w);
        if (Check) return Fresh(v, KQ, x, y, z, w);
        int i = Slot(x, y, z, w, Slots);
        var k = _q[i]; if (k is Quaternion o && Bits(o.x) == x && Bits(o.y) == y && Bits(o.z) == z && Bits(o.w) == w) { K9Engine.BoxShared++; return k; }
        object b = v; _q[i] = b; return b;
    }
    public static object Box(Vector2 v)
    {
        if (!On) return v;
        uint x = Bits(v.x), y = Bits(v.y);
        if (Check) return Fresh(v, KV2, x, y);
        int i = Slot(x, y, 0, 0, Slots / 2);
        var k = _v2[i]; if (k is Vector2 o && Bits(o.x) == x && Bits(o.y) == y) { K9Engine.BoxShared++; return k; }
        object b = v; _v2[i] = b; return b;
    }
    /// <summary>A box of a small non-negative int (Shader.PropertyToID's ids are dense from 1): one kept box per value up to 65535.</summary>
    public static object Int(int v)
    {
        if (!On || v < 0 || v > 65535) return v;
        if (Check) return Fresh(v, KI, (uint)v);
        var t = _ints;
        if (v >= t.Length) { Array.Resize(ref _ints, Math.Max(v + 1, t.Length * 2)); t = _ints; }
        var b = t[v];
        if (b != null) { K9Engine.BoxShared++; return b; }
        return t[v] = v;
    }
    /// <summary>A bool box (the shared True / False of Runtime/Boxes.cs) -- kit #8's new box with the row off (and in =check, checked).</summary>
    public static object Bool(bool v) => !On ? v : Check ? Fresh(v, KB, v ? 1u : 0u) : Boxes.Of(v);
    /// <summary>A float result through a per-site BoxCache (Runtime/Boxes.cs: one box per changed value) -- kit #8's new box with the row off (and
    /// in =check, checked). Review fix: Time.unscaledTime and Camera.aspect went to their BoxCache directly, past the check.</summary>
    public static object Float(BoxCache c, float v) => !On ? v : Check ? Fresh(v, KF, Bits(v)) : c.Of(v);

    // ---------------- =check (review fix): the premise of the shared result boxes, checked ----------------
    // The shared boxes rest on one premise: nothing writes into a result box once it is handed out (nor keeps one and relies on its identity).
    // =check hands out a NEW box for every result (kit #8's answer: the run sees kit #8's boxes) and keeps the last RingN of them with the bits
    // each was made with; when an entry leaves the ring (RingN results later) and at the end line (K9Engine.Summary) the box must still hold
    // those bits. A box written in place in between -- a pooled argument array's out slot written into, a hook, a typed twin of another track
    // -- is a difference: with the boxes shared that write would have changed the value every other holder of the box reads. The shared True /
    // False of Runtime/Boxes.cs (kit #8's own paths hand them out too) are checked at the same moments. Not covered: a holder relying on a box's
    // identity (a reference comparison) -- nothing in the engine does; a box written into more than RingN results after it was handed out.
    const int RingN = 256;
    const byte KV3 = 1, KQ = 2, KV2 = 3, KI = 4, KB = 5, KF = 6;
    static readonly object[] _ring = Check ? new object[RingN] : null;
    static readonly uint[] _ringBits = Check ? new uint[RingN * 4] : null;
    static readonly byte[] _ringKind = Check ? new byte[RingN] : null;
    static int _ringAt; static bool _sharedBad;
    /// <summary>=check: b (a new box) handed out and remembered with its bits.</summary>
    static object Fresh(object b, byte kind, uint x, uint y = 0, uint z = 0, uint w = 0)
    {
        lock (_ring)
        {
            int i = _ringAt; _ringAt = (i + 1) & (RingN - 1);
            if (_ring[i] != null) Verify(i);
            _ring[i] = b; _ringKind[i] = kind; int j = i * 4; _ringBits[j] = x; _ringBits[j + 1] = y; _ringBits[j + 2] = z; _ringBits[j + 3] = w;
        }
        return b;
    }
    static void Verify(int i)
    {
        object b = _ring[i]; int j = i * 4; uint x = _ringBits[j], y = _ringBits[j + 1], z = _ringBits[j + 2], w = _ringBits[j + 3]; byte k = _ringKind[i];
        bool same = k switch
        {
            KV3 => b is Vector3 v && Bits(v.x) == x && Bits(v.y) == y && Bits(v.z) == z,
            KQ => b is Quaternion q && Bits(q.x) == x && Bits(q.y) == y && Bits(q.z) == z && Bits(q.w) == w,
            KV2 => b is Vector2 u && Bits(u.x) == x && Bits(u.y) == y,
            KI => b is int n && (uint)n == x,
            KB => b is bool t && (t ? 1u : 0u) == x,
            _ => b is float f && Bits(f) == x,
        };
        _ring[i] = null; K9Engine.BoxChecked++;
        if (!same) K9Engine.Diff("result box written in place after it was handed out", k switch { KV3 => "Vector3", KQ => "Quaternion", KV2 => "Vector2", KI => "int", KB => "bool", _ => "float" });
        if (!_sharedBad && (!(bool)Boxes.True || (bool)Boxes.False)) { _sharedBad = true; K9Engine.Diff("result box written in place after it was handed out", "the shared True / False (Runtime/Boxes.cs)"); }
    }
    /// <summary>=check, the end line: every box still in the ring verified.</summary>
    internal static void VerifyRing()
    {
        if (!Check) return;
        lock (_ring) for (int i = 0; i < RingN; i++) if (_ring[i] != null) Verify(i);
    }
}
