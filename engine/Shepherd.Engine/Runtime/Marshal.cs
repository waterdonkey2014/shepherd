using System.Collections;
using System.Reflection;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;

/// <summary>Value conversion between stored/loaded data and the exact CLR type a hook or field expects.</summary>
public static class Conv
{
    /// <summary>Asset layer installs these: resolve a PPtr-like value / build an object from a type-tree map.</summary>
    public static Func<object, Type, object> ResolveRef;
    public static Func<object, Type, object> FromTree;

    public static object To(object v, Type t)
    {
        if (t == null || t == typeof(void)) return null;
        if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
        if (t.IsInstanceOfType(v)) return v;
        if (t.IsByRef) t = t.GetElementType();
        try
        {
            if (t.IsEnum) return Enum.ToObject(t, System.Convert.ToInt64(v));
            if (t == typeof(bool)) return v is IConvertible ? System.Convert.ToInt64(v) != 0 : v != null;
            if (t.IsPrimitive && v is IConvertible) return System.Convert.ChangeType(v, t);
            if (t == typeof(string)) return v.ToString();
        }
        catch { return t.IsValueType ? Activator.CreateInstance(t) : null; }
        if (t.IsArray && v is IList src && v is not string)
        {
            var et = t.GetElementType(); var arr = Array.CreateInstance(et, src.Count);
            for (int i = 0; i < src.Count; i++) arr.SetValue(To(src[i], et), i);
            return arr;
        }
        if (typeof(UObject).IsAssignableFrom(t) || t.IsInterface) { var r = ResolveRef?.Invoke(v, t); return r != null && t.IsInstanceOfType(r) ? r : null; }
        var built = FromTree?.Invoke(v, t);
        if (built != null) return built;
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}

/// <summary>Unity's field-serialization rules (what Instantiate copies and what scene data fills).</summary>
public static class SerRules
{
    static readonly Dictionary<Type, FieldInfo[]> Cache = new();
    static readonly HashSet<Type> UnityStructs = new()
    {
        typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Vector2Int), typeof(Vector3Int), typeof(Quaternion), typeof(Color), typeof(Color32),
        typeof(Rect), typeof(RectInt), typeof(Bounds), typeof(BoundsInt), typeof(Matrix4x4), typeof(LayerMask), typeof(Hash128),
    };
    public static bool IsBuiltinValue(Type t)
    {
        if (!MenusCfg.TypeMemos) return IsBuiltinValueOf(t);
        if (BuiltinMemo.TryGetValue(t, out var b)) return b;   // kit #5 menus: the answer per type, kept (DeepCopy asks it ~22,000 times opening Settings)
        b = IsBuiltinValueOf(t); BuiltinMemo.TryAdd(t, b); return b;
    }
    static bool IsBuiltinValueOf(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || UnityStructs.Contains(t);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> BuiltinMemo = new();
    public static bool HasAttr(MemberInfo m, string name) => m.GetCustomAttributesData().Any(a => a.AttributeType.Name == name);

    public static FieldInfo[] Fields(Type t)
    {
        if (Cache.TryGetValue(t, out var r)) return r;
        r = FieldsOf(t, SerRulesFast.On);
        if (SerRulesFast.Check) SerRulesFast.Compare(t, r, FieldsOf(t, false));   // SHEPHERD_SERRULES_FAST=check: both ways, every type
        return Cache[t] = r;
    }
    /// <summary>The scan. fast (kit #5 B4, SerRulesFast): one GetCustomAttributesData per field instead of up to three, and IsBcl /
    /// IsSerializableType remembered per assembly / type -- the same three attribute answers and the same type answers, so the same fields
    /// in the same order. Not fast: the kit #4 code, line for line.</summary>
    static FieldInfo[] FieldsOf(Type t, bool fast)
    {
        var list = new List<FieldInfo>();
        var chain = new Stack<Type>();
        for (var c = t; c != null && c != typeof(object) && c != typeof(MonoBehaviour) && c != typeof(ScriptableObject) && c != typeof(Behaviour) && c != typeof(Component) && c != typeof(UObject); c = c.BaseType) chain.Push(c);
        foreach (var c in chain)
            foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsInitOnly || f.IsLiteral || f.IsStatic) continue;
                if (fast)
                {
                    bool nonSer = false, serRef0 = false, serField = false;
                    foreach (var a in f.GetCustomAttributesData())
                        switch (a.AttributeType.Name) { case "NonSerializedAttribute": nonSer = true; break; case "SerializeReference": serRef0 = true; break; case "SerializeField": serField = true; break; }
                    if (nonSer) continue;
                    if (!(f.IsPublic || serField || serRef0)) continue;
                    if (!serRef0 && !IsSerializableTypeMemo(f.FieldType)) continue;
                    list.Add(f);
                    continue;
                }
                if (HasAttr(f, "NonSerializedAttribute")) continue;
                bool serRef = HasAttr(f, "SerializeReference");
                if (!(f.IsPublic || HasAttr(f, "SerializeField") || serRef)) continue;
                if (!serRef && !IsSerializableType(f.FieldType)) continue;
                list.Add(f);
            }
        return list.ToArray();
    }
    static readonly Dictionary<Type, bool> SerializableMemo = new();
    static readonly Dictionary<System.Reflection.Assembly, bool> BclMemo = new();
    /// <summary><see cref="IsSerializableType"/>(t, 0), remembered: a pure function of the type's metadata.</summary>
    static bool IsSerializableTypeMemo(Type t)
    {
        if (!SerializableMemo.TryGetValue(t, out var v)) SerializableMemo[t] = v = IsSerializableType(t, 0, true);
        return v;
    }
    static bool IsBcl(System.Reflection.Assembly a) { var n = a.GetName().Name; return n is "mscorlib" or "netstandard" or "System.Private.CoreLib" || n.StartsWith("System") || n.StartsWith("Microsoft."); }
    static bool IsBclMemo(System.Reflection.Assembly a) { if (!BclMemo.TryGetValue(a, out var v)) BclMemo[a] = v = IsBcl(a); return v; }
    public static bool IsSerializableType(Type t, int depth = 0) => IsSerializableType(t, depth, false);
    static bool IsSerializableType(Type t, int depth, bool memo)
    {
        if (depth > 3) return false;
        if (IsBuiltinValue(t) || typeof(UObject).IsAssignableFrom(t)) return true;
        if (t == typeof(AnimationCurve) || t == typeof(Gradient) || t == typeof(RectOffset) || t.FullName == "UnityEngine.GUIStyle") return true;
        if (t.IsArray) return t.GetArrayRank() == 1 && !t.GetElementType().IsArray && IsSerializableType(t.GetElementType(), depth + 1, memo);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) { var e = t.GetGenericArguments()[0]; return !(e.IsGenericType && e.GetGenericTypeDefinition() == typeof(List<>)) && !e.IsArray && IsSerializableType(e, depth + 1, memo); }
        if (t.IsAbstract || t.IsInterface || t.IsPointer || typeof(Delegate).IsAssignableFrom(t)) return false;
        if (memo ? IsBclMemo(t.Assembly) : IsBcl(t.Assembly)) return false;   // Dictionary<,>, HashSet<>, Exception… are [Serializable] to .NET but never to Unity
        return t.IsSerializable && (t.IsClass || (t.IsValueType && !t.IsPrimitive));
    }
}
