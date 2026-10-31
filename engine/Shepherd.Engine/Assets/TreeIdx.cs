using System.Runtime.CompilerServices;
using Shepherd.Assets;
namespace Shepherd.Engine.Assets;

/// <summary>Device kit #10, track load, row E3 (create programs): the places of the keys the loader's Create* steps read, found once per pack
/// shape. A pack value tree's key list is its shape's, shared by every struct of that shape (PackReader.ShareKeys), so "the value of key k"
/// is "the value at k's first place in that list" -- exactly what TypeTreeObject.TryGet returns (its index: the first place; its linear
/// scan: the first match). Only for pack trees with shared keys (else every object has its own list): <see cref="Use"/>.</summary>
static class TreeIdx
{
    public const int Name = 0, Enabled = 1, GameObject = 2, IsActive = 3, Layer = 4, Tag = 5, Component = 6, LocalRotation = 7, LocalPosition = 8, LocalScale = 9,
        Children = 10, AnchorMin = 11, AnchorMax = 12, AnchoredPosition = 13, SizeDelta = 14, Pivot = 15, X = 16, Y = 17, Z = 18, W = 19, ComponentInner = 20, N = 21;
    static readonly string[] Names = { "m_Name", "m_Enabled", "m_GameObject", "m_IsActive", "m_Layer", "m_Tag", "m_Component", "m_LocalRotation", "m_LocalPosition", "m_LocalScale",
        "m_Children", "m_AnchorMin", "m_AnchorMax", "m_AnchoredPosition", "m_SizeDelta", "m_Pivot", "x", "y", "z", "w", "component" };
    sealed class RefEq : IEqualityComparer<object> { public new bool Equals(object a, object b) => ReferenceEquals(a, b); public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o); }
    internal static readonly IEqualityComparer<object> ByRef = new RefEq();
    static readonly Dictionary<object, int[]> Cache = new(ByRef);
    static object _lastKeys; static int[] _last;

    public static readonly bool On = Runtime.K10LoadTrack.CreateProg;
    /// <summary>The fast path applies: a pack tree whose keys are its shape's (shared).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Use(SerializedFile f) => On && f?.Pack != null && PackReader.ShareKeys;

    /// <summary>Loader.HasScriptFields (a script field's name among the keys, or no script fields at all): a function of the shape's keys and
    /// the type, kept per (key list, type).</summary>
    public static bool HasScriptFields(TypeTreeObject d, Type t)
    {
        var keys = d.Keys;
        HasFields.TryGetValue(keys, out var head);
        for (var e = head; e != null; e = e.Next) if (ReferenceEquals(e.T, t)) return e.V;
        bool v = Loader.HasScriptFields(d, t); HasFields[keys] = new HF { T = t, V = v, Next = head }; return v;
    }
    sealed class HF { public Type T; public bool V; public HF Next; }   // (reference types only: no new generic instantiation in the AOT image)
    static readonly Dictionary<object, HF> HasFields = new(ByRef);

    /// <summary>TypeTreeObject.TryGet(Names[which]) as a value (null when absent), by place.</summary>
    public static object V(TypeTreeObject t, int which)
    {
        if (t == null) return null;
        var keys = t.Keys;
        int[] idx;
        if (ReferenceEquals(keys, _lastKeys)) idx = _last;
        else
        {
            if (!Cache.TryGetValue(keys, out idx)) { idx = new int[N]; Array.Fill(idx, -2); Cache[keys] = idx; }
            _lastKeys = keys; _last = idx;
        }
        int i = idx[which];
        if (i == -2)
        {
            i = -1; var name = Names[which];
            for (int k = 0; k < keys.Count; k++) if (string.Equals(keys[k], name, StringComparison.Ordinal)) { i = k; break; }
            idx[which] = i;
        }
        return i < 0 ? null : t.Values[i];
    }
}
