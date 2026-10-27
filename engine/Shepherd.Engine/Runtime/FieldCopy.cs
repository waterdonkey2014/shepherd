using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #5, track "menus" (vita/notes/kit5-menus.md): Instantiate's serialized-field copy (Cloner) without a
/// FieldInfo.GetValue / SetValue pair per field. Opening Settings clones ~17,800 objects with ~117,000 serialized fields (+~29,000 nested):
/// on Mono every GetValue / SetValue re-checks the field's declaring type (CheckGeneric: DeclaringType, ContainsGenericParameters, ...),
/// boxes a value field and unboxes it again -- half of the Settings opening frame on the Mac interpreter.
///
/// The same copy, by the runtime's own field layout: Mono's RuntimeFieldInfo.GetFieldOffset (the offset the runtime itself uses, from the
/// start of the object's data) is read once per field, and then
/// - a field whose declared type is a built-in value (primitive, enum, decimal, the Unity math structs: no references inside) is copied
///   byte for byte (GetValue + SetValue copied the same bytes through a box);
/// - a field of a reference type is read as a reference (no reflection) and written through a typed reference store, which the runtime
///   compiles with the GC write barrier (as SetValue's store has); a written value whose runtime type differs from the source value's
///   and does not fit the field goes through SetValue (which refuses it, as before);
/// - anything else (a value-type field that needs DeepCopy) keeps GetValue / SetValue.
/// Mono only (CoreCLR has no GetFieldOffset: the Mac default keeps reflection). A start-up self test copies a probe object both ways and
/// compares; if it fails, or the method is missing, every copy stays on reflection. SHEPHERD_FAST_CLONE=0 = kit #4's copy;
/// SHEPHERD_CLONE_CHECK=1 = every fast copy is re-read through FieldInfo and compared (check runs: <see cref="Summary"/>).</summary>
[ProfileOnly]
public static class FieldCopy
{
    static readonly MethodInfo OffsetOf = typeof(FieldInfo).Assembly.GetType("System.Reflection.RuntimeFieldInfo")?.GetMethod("GetFieldOffset", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
    public static readonly bool Check = Environment.GetEnvironmentVariable("SHEPHERD_CLONE_CHECK") == "1";
    public static readonly bool On;   // (set by the static constructor: after every field initializer, the size table included)
    static long _checked, _mismatches; static string _first, _why;

    /// <summary>The field's offset from the start of the object's (or boxed struct's) data, or -1 when it cannot be read.</summary>
    public static int Offset(FieldInfo f)
    {
        try { return OffsetOf?.Invoke(f, null) is int o && o >= 0 ? o : -1; } catch (Exception e) { _why ??= e.GetType().Name + ": " + e.Message; return -1; }
    }
    sealed class RawData { public byte B; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ref byte At(object o, int off) => ref Unsafe.Add(ref Unsafe.As<RawData>(o).B, off);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static object GetRef(object o, int off) => Unsafe.As<byte, object>(ref At(o, off));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void SetRef(object o, int off, object v) => Unsafe.As<byte, object>(ref At(o, off)) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void CopyBytes(object src, object dst, int off, int size) => Unsafe.CopyBlockUnaligned(ref At(dst, off), ref At(src, off), (uint)size);

    /// <summary>The size in bytes of a built-in value field (Cloner's "copied as is" types), or 0 for any other type.</summary>
    public static int ValueSize(Type t)
    {
        if (t.IsEnum) t = Enum.GetUnderlyingType(t);
        if (t.IsPrimitive)
            return Type.GetTypeCode(t) switch
            {
                TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte => 1,
                TypeCode.Char or TypeCode.Int16 or TypeCode.UInt16 => 2,
                TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
                TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Double => 8,
                _ => t == typeof(IntPtr) || t == typeof(UIntPtr) ? IntPtr.Size : 0,
            };
        if (t == typeof(decimal)) return Unsafe.SizeOf<decimal>();
        return Sizes.TryGetValue(t, out var s) ? s : 0;
    }
    static readonly Dictionary<Type, int> Sizes = new()
    {
        [typeof(Vector2)] = Unsafe.SizeOf<Vector2>(), [typeof(Vector3)] = Unsafe.SizeOf<Vector3>(), [typeof(Vector4)] = Unsafe.SizeOf<Vector4>(),
        [typeof(Vector2Int)] = Unsafe.SizeOf<Vector2Int>(), [typeof(Vector3Int)] = Unsafe.SizeOf<Vector3Int>(), [typeof(Quaternion)] = Unsafe.SizeOf<Quaternion>(),
        [typeof(Color)] = Unsafe.SizeOf<Color>(), [typeof(Color32)] = Unsafe.SizeOf<Color32>(), [typeof(Rect)] = Unsafe.SizeOf<Rect>(),
        [typeof(RectInt)] = Unsafe.SizeOf<RectInt>(), [typeof(Bounds)] = Unsafe.SizeOf<Bounds>(), [typeof(BoundsInt)] = Unsafe.SizeOf<BoundsInt>(),
        [typeof(Matrix4x4)] = Unsafe.SizeOf<Matrix4x4>(), [typeof(LayerMask)] = Unsafe.SizeOf<LayerMask>(), [typeof(Hash128)] = Unsafe.SizeOf<Hash128>(),
    };

    /// <summary>A probe class with one field of each kind the fast copy handles, copied by offset and read back through reflection.</summary>
    sealed class Probe
    {
        public bool b; public byte u8; public char c; public short s; public int i; public long l; public float f; public double d; public decimal m;
        public DayOfWeek e; public Vector3 v3; public Color col; public Matrix4x4 mx; public Rect r; public string str; public object obj; public int[] arr;
    }
    static bool SelfTest()
    {
        try
        {
            var src = new Probe { b = true, u8 = 0xA5, c = 'Q', s = -12345, i = unchecked((int)0xDEADBEEF), l = 0x0123456789ABCDEFL, f = -0f, d = double.Epsilon, m = 7.25m,
                e = DayOfWeek.Friday, v3 = new Vector3(1.5f, float.NaN, -2f), col = new Color(0.1f, 0.2f, 0.3f, 0.4f), mx = new Matrix4x4 { m00 = 1f, m03 = 2.5f, m13 = -3f, m22 = float.MaxValue, m33 = 1f },
                r = new Rect(1, 2, 3, 4), str = "probe", obj = new object(), arr = new[] { 1, 2, 3 } };
            var dst = new Probe();
            foreach (var fi in typeof(Probe).GetFields())
            {
                int off = Offset(fi); if (off < 0) { _why ??= "no offset for " + fi.Name; return false; }
                if (fi.FieldType.IsValueType) { int n = ValueSize(fi.FieldType); if (n <= 0) { _why = "no size for " + fi.Name; return false; } CopyBytes(src, dst, off, n); }
                else { if (!ReferenceEquals(GetRef(src, off), fi.GetValue(src))) { _why = $"reference read differs at {fi.Name} (offset {off})"; return false; } SetRef(dst, off, GetRef(src, off)); }
            }
            foreach (var fi in typeof(Probe).GetFields())
            {
                object a = fi.GetValue(src), b = fi.GetValue(dst);
                if (fi.FieldType.IsValueType ? !SameBits(a, b) : !ReferenceEquals(a, b)) { _why = $"copy differs at {fi.Name}"; return false; }
            }
            return true;
        }
        catch (Exception e) { _why = e.GetType().Name + ": " + e.Message; return false; }
    }
    /// <summary>Two boxes of the same value type hold the same bytes.</summary>
    static unsafe bool SameBits(object a, object b)
    {
        if (a == null || b == null || a.GetType() != b.GetType()) return false;
        int n = ValueSize(a.GetType()); if (n <= 0) return Equals(a, b);
        ref byte x = ref Unsafe.As<RawData>(a).B, y = ref Unsafe.As<RawData>(b).B;
        for (int k = 0; k < n; k++) if (Unsafe.Add(ref x, k) != Unsafe.Add(ref y, k)) return false;
        return true;
    }

    /// <summary>SHEPHERD_CLONE_CHECK=1: one field the fast path wrote, compared with what reflection reads from the source (value
    /// fields: the same bytes; a reference copied as is: the same reference; a deep-copied one: what the slow path would store -- the
    /// same runtime type, the remapped clone for an engine object).</summary>
    public static void CheckField(FieldInfo f, object src, object dst, bool deep, object expectRef)
    {
        _checked++;
        object a = f.GetValue(src), b = f.GetValue(dst);
        bool ok = f.FieldType.IsValueType ? SameBits(a, b)
            : !deep ? ReferenceEquals(a, b)
            : a == null ? b == null
            : expectRef != null ? ReferenceEquals(b, expectRef)
            : b != null && b.GetType() == a.GetType();
        if (!ok && _mismatches++ == 0) _first = $"{f.DeclaringType?.Name}.{f.Name} ({f.FieldType.Name})";
    }
    static FieldCopy() { On = MenusCfg.FastClone && OffsetOf != null && SelfTest(); }
    public static string Summary() => !Check ? null
        : $"[k5] clone check: fast field copy {(On ? "on" : "OFF")}, {_checked} fields compared with reflection, {_mismatches} mismatches{(_first != null ? ", first " + _first : "")}";
    public static string StartLine() => $"[k5] Instantiate field copy: {(On ? "by field offset (Mono GetFieldOffset; self test passed)" : MenusCfg.FastClone ? $"reflection ({(OffsetOf == null ? "no RuntimeFieldInfo.GetFieldOffset in this runtime" : "the self test failed: " + _why)})" : "reflection (SHEPHERD_FAST_CLONE=0)")}";
}
