namespace Shepherd.Engine.Runtime;

/// <summary>V6 stage 2: shared boxes for icalls that return a bool many times per frame (an icall result is unboxed by the rewritten
/// Unity stub at once; nothing keeps or compares the box), instead of a new box per call.</summary>
public static class Boxes
{
    public static readonly object True = true, False = false;
    public static object Of(bool b) => b ? True : False;
}

/// <summary>A per-call-site box of the last float / int value returned (Time.deltaTime, realtimeSinceStartup, timeScale, frameCount are
/// asked tens of times per frame with the same value): a new box only when the bits change.</summary>
public sealed class BoxCache
{
    object _f; int _fbits; object _i; int _ival;
    public object Of(float v) { int b = BitConverter.SingleToInt32Bits(v); if (_f == null || b != _fbits) { _f = v; _fbits = b; } return _f; }
    public object Of(int v) { if (_i == null || v != _ival) { _i = v; _ival = v; } return _i; }
}

/// <summary>menu-speed (top hotspots): a pooled copy of a list for a loop whose body may change it -- what list.ToArray() gave (the same
/// elements in the same order) -- handed back (cleared) after the loop; a nested loop takes its own copy. Main thread only (the lifecycle and
/// native-message loops: the object and component lists are main-thread state).</summary>
internal static class Pooled<T> where T : class
{
    static readonly Stack<T[]> Free = new();
    public static T[] Copy(List<T> l, out int n)
    {
        n = l.Count;
        if (n > MaxKept) { var big = new T[n]; l.CopyTo(big); return big; }   // (a long list: a copy of its own, as ToArray made)
        var a = Free.Count > 0 ? Free.Pop() : null;
        if (a == null || a.Length < n) a = new T[Math.Max(16, n + 8)];
        l.CopyTo(a); return a;
    }
    public static void Back(T[] a, int n) { Array.Clear(a, 0, n); if (a.Length <= MaxKept + 8 && Free.Count < 16) Free.Push(a); }
    /// <summary>Only arrays well under the GC's large-object size are kept (a kept large object pins a 1 MiB large-object section: the boot's
    /// heap peak felt one through the vita-sim heap-cap edge).</summary>
    const int MaxKept = 384;
}
