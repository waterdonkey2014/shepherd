using System.Reflection;
namespace Shepherd.TestHost;

/// <summary>Kit #7 tests track: deliberate faults in Shepherd's reimplementation, at the icall seam (Shepherd.Hooks.Icalls.Table), for the
/// drafted probes' mutation check (vita/notes/kit7-tests.md "AI-written tests behind strict filters": a kept probe must catch a seeded
/// fault in the code it reaches). `mutate &lt;icall name&gt;=&lt;op&gt;` wraps that icall's implementation:
///   ulp   the result one float ulp up (a float, the first float of a struct result -- Vector/Quaternion/Color/Matrix/Rect --, an int + 1,
///         a bool negated); for an *_Injected icall whose result is written into its last argument, that argument instead
///   zero  the result (or that out argument) replaced by its type's default
///   skip  the implementation is not called at all (returns null; an out argument keeps what the caller passed)
/// The rest of the engine is unchanged; nothing of this is in any eboot.</summary>
public static class Mutate
{
    public static string Applied;
    public static void Install(string spec)
    {
        var eq = spec.LastIndexOf('='); string name = spec[..eq], op = spec[(eq + 1)..];
        int id = Array.IndexOf(Shepherd.Hooks.Icalls.Names, name);
        if (id < 0) throw new ArgumentException($"no icall named '{name}'");
        var orig = Shepherd.Hooks.Icalls.Table[id];
        if (orig == null) throw new ArgumentException($"icall '{name}' has no implementation to mutate");
        bool injected = name.EndsWith("_Injected") || name.Contains("_Injected(");
        Shepherd.Hooks.Icalls.Table[id] = a =>
        {
            if (op == "skip") return null;
            var r = orig(a);
            if (r != null) return op == "zero" ? Default(r.GetType()) : Bump(r);
            if (injected && a.Length > 0 && a[^1] != null) a[^1] = op == "zero" ? Default(a[^1].GetType()) : Bump(a[^1]);
            return r;
        };
        Applied = $"{name}={op}";
        Shepherd.Engine.Log.Info($"[mutate] {Applied} (icall #{id})");
    }
    static object Default(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;
    static float Up(float f) { if (float.IsNaN(f)) return 0f; int b = BitConverter.SingleToInt32Bits(f); return f == 0f ? float.Epsilon : BitConverter.Int32BitsToSingle(f > 0 ? b + 1 : b - 1); }
    static object Bump(object v)
    {
        switch (v)
        {
            case float f: return Up(f);
            case double d: return d == 0 ? double.Epsilon : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(d) + (d > 0 ? 1 : -1));
            case int i: return i + 1;
            case uint u: return u + 1;
            case long l: return l + 1;
            case bool b: return !b;
            case ushort s: return (ushort)(s + 1);
            case string str: return str + "~";
        }
        var t = v.GetType();
        if (t.IsValueType)
        {
            var fld = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(x => x.FieldType == typeof(float) || x.FieldType == typeof(int));
            if (fld != null) { object box = v; fld.SetValue(box, fld.FieldType == typeof(float) ? Up((float)fld.GetValue(box)) : (int)fld.GetValue(box) + 1); return box; }
        }
        if (v is Array arr && arr.Length > 0 && arr.GetValue(0) is { } e0) { var c = (Array)arr.Clone(); c.SetValue(Bump(e0), 0); return c; }
        return v;
    }
}
