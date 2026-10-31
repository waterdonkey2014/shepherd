#if !SHEPHERD_EBOOT
#define SHEPHERD_LOADPROF   // (kit #10: the loader profile's zone calls -- Assets/LoadProf.cs -- are compiled into Mac / engine-tools builds only)
#endif
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Shepherd.Assets;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
using UPPtr = Shepherd.Assets.PPtr;
namespace Shepherd.Engine.Assets;

/// <summary>Device kit #10, track load, row E3 (vita/notes/kit10-load.md): FIELD PROGRAMS -- a MonoBehaviour / ScriptableObject's serialized
/// fields filled from its value tree by a program compiled once per (pack shape, script type), instead of the generic deserializer
/// (Loader.Populate: per field a name lookup in the tree, Loader.ToManaged's chain of reflection type tests, Convert.ChangeType, a box,
/// FieldInfo.SetValue's checks).
///
/// A program is the list Populate would walk: every field of SerRules.Fields(type), in that order, whose name the shape holds (the key's
/// first place, as TypeTreeObject.TryGet finds it), each with the conversion ToManaged would pick for its declared type and the field's
/// store (Mono: the runtime's own field offset, Runtime/FieldCopy.cs; elsewhere FieldInfo.SetValue). Applying it converts and stores the
/// fields one after another in that order, so every PPtr is resolved -- and every object it names is created -- at the same moment and in
/// the same order as before, nested serializable objects are constructed (cached constructor, same fallbacks) and filled in the same order,
/// their OnAfterDeserialize runs at the same point, and an exception leaves the same fields unset (top level: the same Warn line; nested:
/// swallowed, as FromTree). A typed fast path is taken only when the VALUE has exactly the type the field's conversion returns unchanged
/// (an int box for an int field, a float box for a float field, a string, a PPtr for an object field, a {x,y,z} struct of float boxes for a
/// Vector3 ...); anything else -- another source type, a missing key in a struct, depth past 16, Matrix4x4 / AnimationCurve / Gradient /
/// RectOffset, an array that is not a value list -- goes through Loader.ToManaged + FieldInfo.SetValue for that one value, exactly as
/// before. Floats and doubles are stored as their bits.
///
/// Switch: SHEPHERD_K10_FIELDPROG = unset / 1 (on) | 0 (kit #9's Populate) | check (the program stores, then every field whose value needs
/// no construction -- primitives, enums, strings, object references, Unity structs, arrays / lists of those -- is computed again by
/// ToManaged and compared with what the field holds: bits for values, the same reference for objects, element by element; the end line
/// counts differences). Only for value trees of a pack (shared shape keys); the type-tree path of loose files is unchanged.</summary>
public static class FieldPrograms
{
    static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_K10_FIELDPROG") ?? "").Trim().ToLowerInvariant();
    public static readonly bool On = !(Setting is "0" or "off") && !Runtime.K10LoadTrack.AllOff;
    public static readonly bool Check = On && Setting == "check";
    static bool Offsets => FieldCopy.On;   // Mono's field offsets (the kit #5 self test passed); else FieldInfo.SetValue
    /// <summary>Kit #10 review: stores into a BOXED struct by offset (a nested serializable struct is made by Activator.CreateInstance and its
    /// fields stored in the box; BinPrograms does the same) -- the kit #5 self test probes a class only, so a probe struct is boxed and written
    /// through these same stores once, at the first nested struct's program, and read back by reflection: any offset outside the struct, a
    /// value that does not read back, or a byte written beside a field (guard words) keeps every nested struct on FieldInfo.SetValue.</summary>
    internal static bool BoxStores { get { _boxAsked = true; return BoxProbe.Ok; } }
    static bool _boxAsked;
    /// <summary>The end line's note on the probe (empty when no nested struct asked for it).</summary>
    internal static string BoxNote() => !_boxAsked ? "" : BoxProbe.Ok ? ", boxed structs by field offset (probe passed)" : ", boxed structs by FieldInfo.SetValue (probe: " + BoxProbe.Why + ")";

    // ---- counters (the [k10] end line) ----
    public static long Objects, Programs, FastFields, SlowFields, Exceptions, Checked, CheckDiffs, NestedFast, NestedPrograms;
    static string _firstDiff;

    enum K : byte
    {
        Generic, Bool, I8, U8, I16, U16, Char, I32, U32, I64, U64, F32, F64, Enum, String, UObj, Bytes,
        Vec2, Vec3, Vec4, Quat, Color, Rect, Array, List, Nested,
    }
    sealed class Conv
    {
        public Type T; public K Kind; public int Size;   // Size: the value's bytes (primitives / enums: the underlying type)
        public Conv Elem;                                 // arrays / lists: the element conversion
        public bool ElemRef;                              // arrays: a reference-type element (an object[]-compatible store)
        public Type LastType; public bool LastInst;       // UObj: the last runtime type tested with IsInstanceOfType, and its answer
        public object LastKeys; public int I0 = -1, I1 = -1, I2 = -1, I3 = -1, I4 = -1;   // structs: the key list last seen + the components' places
        public ConstructorInfo Ctor; public bool CtorLooked;   // Nested classes: Loader.FromTree's constructor (kept: a function of the type)
        public object NestKeys; public Prog NestProg;          // Nested: the last shape's program
    }
    sealed class Op { public int Key; public FieldInfo Fi; public int Off = -1; public Conv C; }
    sealed class Prog { public Type T; public Op[] Ops; public bool Abstract; }

    // (keys, type) -> program: one entry per shape key list (shared by every struct of that shape), a short list of types behind it
    sealed class Entry { public Type T; public Prog P; public Entry Next; }   // (a linked list: no value-tuple instantiation in the AOT image)
    static readonly Dictionary<object, Entry> Progs = new();   // (a key list: List<string>, reference equality)
    static readonly Dictionary<Type, Conv> Convs = new();

    /// <summary>Loader.Populate's work for a pack value tree; false = not handled here (the caller runs Populate).</summary>
    public static bool TryPopulate(object target, Type type, TypeTreeObject d, SerializedFile f)
    {
        if (!On || f?.Pack == null || !PackReader.ShareKeys) return false;
        var p = ProgOf(d.Keys, type, false);
        Objects++;
        var vals = d.Values; var ops = p.Ops;
        for (int i = 0; i < ops.Length; i++)
        {
            var op = ops[i];
            try { Store(op, target, vals[op.Key], f); }
            catch (Exception e) { Exceptions++; Log.Warn($"populate {type.Name}.{op.Fi.Name}: {e.Message}"); }
        }
        if (Check) CheckFields(p, target, d, f);
        return true;
    }

    static Prog ProgOf(IReadOnlyList<string> keys, Type type, bool nested)
    {
        Progs.TryGetValue(keys, out var head);
        for (var e = head; e != null; e = e.Next) if (ReferenceEquals(e.T, type)) return e.P;
        var p = Compile(keys, type);
        if (nested) NestedPrograms++; else Programs++;
        Progs[keys] = new Entry { T = type, P = p, Next = head };
        return p;
    }
    static Prog Compile(IReadOnlyList<string> keys, Type type)
    {
        var ops = new List<Op>();
        foreach (var fi in SerRules.Fields(type))
        {
            int k = -1; for (int i = 0; i < keys.Count; i++) if (string.Equals(keys[i], fi.Name, StringComparison.Ordinal)) { k = i; break; }   // (TryGet: the first place)
            if (k < 0) continue;
            var op = new Op { Key = k, Fi = fi, C = ConvOf(fi.FieldType) };
            if (Offsets && op.C.Kind != K.Generic && (!type.IsValueType || BoxStores)) op.Off = FieldCopy.Offset(fi);   // (a struct's fields: only if the box probe passed)
            ops.Add(op);
        }
        return new Prog { T = type, Ops = ops.ToArray(), Abstract = type.IsAbstract || type.IsInterface || typeof(UObject).IsAssignableFrom(type) };
    }

    /// <summary>The conversion Loader.ToManaged picks for a declared type, in its own order of tests.</summary>
    static Conv ConvOf(Type t)
    {
        if (Convs.TryGetValue(t, out var c)) return c;
        c = new Conv { T = t };
        Convs[t] = c;   // (before the element / nested types: a recursive type finds itself)
        if (typeof(UObject).IsAssignableFrom(t) || t.IsInterface) c.Kind = K.UObj;
        else if (t.IsEnum)
        {
            var u = Enum.GetUnderlyingType(t); c.Kind = K.Enum;
            c.Size = u == typeof(byte) || u == typeof(sbyte) ? 1 : u == typeof(short) || u == typeof(ushort) ? 2 : u == typeof(int) || u == typeof(uint) ? 4 : u == typeof(long) || u == typeof(ulong) ? 8 : 0;
            if (c.Size == 0) c.Kind = K.Generic;
        }
        else if (t == typeof(bool)) c.Kind = K.Bool;
        else if (t.IsPrimitive)
            c.Kind = t == typeof(sbyte) ? K.I8 : t == typeof(byte) ? K.U8 : t == typeof(short) ? K.I16 : t == typeof(ushort) ? K.U16 : t == typeof(char) ? K.Char
                : t == typeof(int) ? K.I32 : t == typeof(uint) ? K.U32 : t == typeof(long) ? K.I64 : t == typeof(ulong) ? K.U64 : t == typeof(float) ? K.F32 : t == typeof(double) ? K.F64 : K.Generic;
        else if (t == typeof(string)) c.Kind = K.String;
        else if (t == typeof(byte[])) c.Kind = K.Bytes;
        else if (t.IsArray)
        {
            if (t.GetArrayRank() != 1) c.Kind = K.Generic;
            else { var et = t.GetElementType(); c.Kind = K.Array; c.Elem = ConvOf(et); c.ElemRef = !et.IsValueType; }
        }
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) { c.Kind = K.List; c.Elem = ConvOf(t.GetGenericArguments()[0]); }
        else if (t == typeof(Vector2)) c.Kind = K.Vec2;
        else if (t == typeof(Vector3)) c.Kind = K.Vec3;
        else if (t == typeof(Vector4)) c.Kind = K.Vec4;
        else if (t == typeof(Quaternion)) c.Kind = K.Quat;
        else if (t == typeof(Color)) c.Kind = K.Color;
        else if (t == typeof(Rect)) c.Kind = K.Rect;
        else if (t == typeof(Color32) || t == typeof(Vector2Int) || t == typeof(Vector3Int) || t == typeof(LayerMask) || t == typeof(Bounds) || t == typeof(Matrix4x4)
                 || t == typeof(RectOffset) || t == typeof(AnimationCurve) || t == typeof(Gradient)) c.Kind = K.Generic;   // (Loader.FromTree's own cases: kept there)
        else if (t.IsAbstract || t.IsPointer) c.Kind = K.Generic;
        else c.Kind = K.Nested;
        return c;
    }

    // ---------------- raw stores ----------------
    sealed class RawData { public byte B; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ref byte At(object o, int off) => ref Unsafe.Add(ref Unsafe.As<RawData>(o).B, off);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int Bits32(object box) => Unsafe.ReadUnaligned<int>(ref Unsafe.As<RawData>(box).B);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static long Bits64(object box) => Unsafe.ReadUnaligned<long>(ref Unsafe.As<RawData>(box).B);
    static void SetRef(Op op, object target, object v) { if (op.Off >= 0) Unsafe.As<byte, object>(ref At(target, op.Off)) = v; else op.Fi.SetValue(target, v); }
    /// <summary>A value field from a box of exactly the field's type (the bytes SetValue would copy out of it).</summary>
    static void SetBox(Op op, object target, object box, int size)
    {
        if (op.Off < 0) { op.Fi.SetValue(target, box); return; }
        ref byte dst = ref At(target, op.Off);
        switch (size)
        {
            case 1: dst = Unsafe.As<RawData>(box).B; break;
            case 2: Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<short>(ref Unsafe.As<RawData>(box).B)); break;
            case 4: Unsafe.WriteUnaligned(ref dst, Bits32(box)); break;
            default: Unsafe.WriteUnaligned(ref dst, Bits64(box)); break;
        }
    }

    // ---------------- top-level store: ToManaged + SetValue for one field ----------------
    static void Store(Op op, object target, object v, SerializedFile f)
    {
        var c = op.C;
        switch (c.Kind)
        {
            case K.Bool:
                if (v is bool b) { if (op.Off >= 0) At(target, op.Off) = b ? (byte)1 : (byte)0; else op.Fi.SetValue(target, b); FastFields++; return; }
                if (v is byte u8) { bool bb = u8 != 0; if (op.Off >= 0) At(target, op.Off) = bb ? (byte)1 : (byte)0; else op.Fi.SetValue(target, bb); FastFields++; return; }
                break;
            case K.I8: if (v is sbyte) { SetBox(op, target, v, 1); FastFields++; return; } break;
            case K.U8: if (v is byte) { SetBox(op, target, v, 1); FastFields++; return; } break;
            case K.I16: if (v is short) { SetBox(op, target, v, 2); FastFields++; return; } break;
            case K.U16: if (v is ushort) { SetBox(op, target, v, 2); FastFields++; return; } break;
            case K.Char: if (v is char) { SetBox(op, target, v, 2); FastFields++; return; } break;
            case K.I32: if (v is int) { SetBox(op, target, v, 4); FastFields++; return; } break;
            case K.U32: if (v is uint) { SetBox(op, target, v, 4); FastFields++; return; } break;
            case K.F32: if (v is float) { SetBox(op, target, v, 4); FastFields++; return; } break;
            case K.I64: if (v is long) { SetBox(op, target, v, 8); FastFields++; return; } break;
            case K.U64: if (v is ulong) { SetBox(op, target, v, 8); FastFields++; return; } break;
            case K.F64: if (v is double) { SetBox(op, target, v, 8); FastFields++; return; } break;
            case K.Enum:
                if (EnumLong(v, out long l))
                {
                    if (op.Off < 0) op.Fi.SetValue(target, Enum.ToObject(c.T, l));
                    else { ref byte dst = ref At(target, op.Off); switch (c.Size) { case 1: dst = (byte)l; break; case 2: Unsafe.WriteUnaligned(ref dst, (short)l); break; case 4: Unsafe.WriteUnaligned(ref dst, (int)l); break; default: Unsafe.WriteUnaligned(ref dst, l); break; } }
                    FastFields++; return;
                }
                break;
            case K.String: if (v is string || v == null) { SetRef(op, target, v); FastFields++; return; } break;
            case K.Bytes: if (v is byte[] || v == null) { SetRef(op, target, v); FastFields++; return; } break;
            case K.UObj: SetRef(op, target, Obj(c, v, f)); FastFields++; return;
            case K.Vec2: case K.Vec3: case K.Vec4: case K.Quat: case K.Color: case K.Rect:
                if (v is TypeTreeObject tt && StructValue(c, tt, out var sv)) { StoreStruct(op, target, c.Kind, sv); FastFields++; return; }
                break;
            case K.Array:
                if (v is List<object> la) { var arr = MakeArray(c, la, f, 0); SetRef(op, target, arr); FastFields++; return; }
                break;
            case K.List:
                if (v is List<object> ll) { var list = MakeList(c, ll, f, 0); SetRef(op, target, list); FastFields++; return; }
                break;
            case K.Nested:
                if (v is TypeTreeObject nt) { var o = MakeNested(c, nt, f, 0); if (c.T.IsValueType || op.Off < 0) op.Fi.SetValue(target, o); else SetRef(op, target, o); FastFields++; return; }
                break;
        }
        SlowFields++; if (Check) Slow(c.T, v);
        op.Fi.SetValue(target, Loader.ToManaged(v, op.Fi.FieldType, f, 0));
    }
    static readonly Dictionary<string, int> _slow = new();
    static void Slow(Type t, object v) { var k = $"{t.Name}<-{v?.GetType().Name ?? "null"}"; _slow[k] = _slow.TryGetValue(k, out var n) ? n + 1 : 1; }

    /// <summary>Convert.ToInt64(v) for the integer boxes it widens without a check (Enum.ToObject(t, Convert.ToInt64(v)) then keeps the low bytes).</summary>
    static bool EnumLong(object v, out long l)
    {
        switch (v) { case int i: l = i; return true; case uint u: l = u; return true; case byte b: l = b; return true; case sbyte sb: l = sb; return true;
            case short s: l = s; return true; case ushort us: l = us; return true; case long x: l = x; return true; default: l = 0; return false; }
    }
    /// <summary>ToManaged for an object field: a PPtr resolved (and created) through Loader.Managed and kept if it is of the field's type;
    /// any other value is null (ToManaged's tests all fall through to null for a reference type).</summary>
    static object Obj(Conv c, object v, SerializedFile f)
    {
        if (v is not UPPtr p) return null;
        var m = Loader.Managed(f, p);
        if (m == null) return null;
        var mt = m.GetType();
        if (!ReferenceEquals(mt, c.LastType)) { c.LastInst = c.T.IsInstanceOfType(m); c.LastType = mt; }
        return c.LastInst ? m : null;
    }

    // ---------------- Unity structs (Loader.FromTree's V2 / V3 / Q / Col / Rect: Fl = Convert.ToSingle of the key's value, 0 if absent) ----------------
    struct SV { public float A, B, C, D; public bool Rgba; public uint RgbaV; }
    static bool StructValue(Conv c, TypeTreeObject tt, out SV sv)
    {
        sv = default;
        var keys = tt.Keys;
        if (!ReferenceEquals(keys, c.LastKeys))
        {
            c.I0 = c.I1 = c.I2 = c.I3 = c.I4 = -1;
            string a, b, cc, d;
            switch (c.Kind)
            {
                case K.Rect: a = "x"; b = "y"; cc = "width"; d = "height"; break;
                case K.Color: a = "r"; b = "g"; cc = "b"; d = "a"; break;
                default: a = "x"; b = "y"; cc = "z"; d = "w"; break;
            }
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if (c.I0 < 0 && k == a) c.I0 = i; else if (c.I1 < 0 && k == b) c.I1 = i; else if (c.I2 < 0 && k == cc) c.I2 = i; else if (c.I3 < 0 && k == d) c.I3 = i;
                if (c.I4 < 0 && k == "rgba") c.I4 = i;
            }
            c.LastKeys = keys;
        }
        var vals = tt.Values;
        if (c.Kind == K.Color && c.I4 >= 0) { var rv = vals[c.I4]; if (rv == null) { sv.Rgba = false; } else { sv.Rgba = true; sv.RgbaV = Convert.ToUInt32(rv); return true; } }
        sv.A = Fl(vals, c.I0, 0); sv.B = Fl(vals, c.I1, 0);
        switch (c.Kind)
        {
            case K.Vec3: sv.C = Fl(vals, c.I2, 0); break;
            case K.Vec4: case K.Rect: sv.C = Fl(vals, c.I2, 0); sv.D = Fl(vals, c.I3, 0); break;
            case K.Quat: case K.Color: sv.C = Fl(vals, c.I2, 0); sv.D = Fl(vals, c.I3, 1); break;
        }
        return true;
    }
    /// <summary>Loader's Fl: the value's Convert.ToSingle, or the default when the key is absent or its value null.</summary>
    static float Fl(IReadOnlyList<object> vals, int i, float d) => i < 0 || vals[i] is not { } v ? d : v is float x ? x : Convert.ToSingle(v);
    static object StructBox(K k, SV s) => k switch
    {
        K.Vec2 => new Vector2(s.A, s.B), K.Vec3 => new Vector3(s.A, s.B, s.C), K.Vec4 => new Vector4(s.A, s.B, s.C, s.D),
        K.Quat => M.QNorm(new Quaternion(s.A, s.B, s.C, s.D)),
        K.Color => s.Rgba ? (Color)new Color32((byte)s.RgbaV, (byte)(s.RgbaV >> 8), (byte)(s.RgbaV >> 16), (byte)(s.RgbaV >> 24)) : new Color(s.A, s.B, s.C, s.D),
        _ => new Rect(s.A, s.B, s.C, s.D),
    };
    static void StoreStruct(Op op, object target, K k, SV s)
    {
        if (op.Off < 0) { op.Fi.SetValue(target, StructBox(k, s)); return; }
        ref byte dst = ref At(target, op.Off);
        switch (k)
        {
            case K.Vec2: Unsafe.WriteUnaligned(ref dst, new Vector2(s.A, s.B)); break;
            case K.Vec3: Unsafe.WriteUnaligned(ref dst, new Vector3(s.A, s.B, s.C)); break;
            case K.Vec4: Unsafe.WriteUnaligned(ref dst, new Vector4(s.A, s.B, s.C, s.D)); break;
            case K.Quat: Unsafe.WriteUnaligned(ref dst, M.QNorm(new Quaternion(s.A, s.B, s.C, s.D))); break;
            case K.Color: Unsafe.WriteUnaligned(ref dst, s.Rgba ? (Color)new Color32((byte)s.RgbaV, (byte)(s.RgbaV >> 8), (byte)(s.RgbaV >> 16), (byte)(s.RgbaV >> 24)) : new Color(s.A, s.B, s.C, s.D)); break;
            default: Unsafe.WriteUnaligned(ref dst, new Rect(s.A, s.B, s.C, s.D)); break;
        }
    }

    // ---------------- values (ToManaged's result as an object: array / list elements, nested fields) ----------------
    /// <summary>Loader.ToManaged(v, c.T, f, depth) -- the fast paths above, else ToManaged itself.</summary>
    static object Value(Conv c, object v, SerializedFile f, int depth)
    {
        if (depth <= 16)
            switch (c.Kind)
            {
                case K.Bool: if (v is bool) return v; if (v is byte u8) return u8 != 0; break;   // (ToManaged: Convert.ToInt64(v) != 0, a new bool)
                case K.I8: if (v is sbyte) return v; break;
                case K.U8: if (v is byte) return v; break;
                case K.I16: if (v is short) return v; break;
                case K.U16: if (v is ushort) return v; break;
                case K.Char: if (v is char) return v; break;
                case K.I32: if (v is int) return v; break;
                case K.U32: if (v is uint) return v; break;
                case K.F32: if (v is float) return v; break;
                case K.I64: if (v is long) return v; break;
                case K.U64: if (v is ulong) return v; break;
                case K.F64: if (v is double) return v; break;
                case K.Enum: if (EnumLong(v, out long l)) return Enum.ToObject(c.T, l); break;
                case K.String: if (v is string || v == null) return v; break;
                case K.Bytes: if (v is byte[] || v == null) return v; break;
                case K.UObj: return Obj(c, v, f);
                case K.Vec2: case K.Vec3: case K.Vec4: case K.Quat: case K.Color: case K.Rect:
                    if (v is TypeTreeObject tt && StructValue(c, tt, out var sv)) return StructBox(c.Kind, sv);
                    break;
                case K.Array: if (v is List<object> la) return MakeArray(c, la, f, depth); break;
                case K.List: if (v is List<object> ll) return MakeList(c, ll, f, depth); break;
                case K.Nested: if (v is TypeTreeObject nt) return MakeNested(c, nt, f, depth); break;
            }
        SlowFields++; if (Check) Slow(c.T, v);
        return Loader.ToManaged(v, c.T, f, depth);
    }

    /// <summary>ToManaged's array: Array.CreateInstance(element, count), then each element converted (depth + 1) and set in order.</summary>
    static Array MakeArray(Conv c, List<object> src, SerializedFile f, int depth)
    {
        var e = c.Elem; var arr = Array.CreateInstance(e.T, src.Count);
        int n = src.Count;
        switch (e.Kind)
        {
            case K.I32 when depth + 1 <= 16: { var a = (int[])arr; for (int i = 0; i < n; i++) { var x = src[i]; if (x is int v) a[i] = v; else arr.SetValue(Value(e, x, f, depth + 1), i); } return arr; }
            case K.F32 when depth + 1 <= 16: { var a = (float[])arr; for (int i = 0; i < n; i++) { var x = src[i]; if (x is float) Unsafe.As<float, int>(ref a[i]) = Bits32(x); else arr.SetValue(Value(e, x, f, depth + 1), i); } return arr; }
            case K.Bool when depth + 1 <= 16: { var a = (bool[])arr; for (int i = 0; i < n; i++) { var x = src[i]; if (x is bool v) a[i] = v; else arr.SetValue(Value(e, x, f, depth + 1), i); } return arr; }
        }
        if (c.ElemRef) { var a = (object[])arr; for (int i = 0; i < n; i++) a[i] = Value(e, src[i], f, depth + 1); return arr; }   // (the same element type check SetValue makes)
        for (int i = 0; i < n; i++) arr.SetValue(Value(e, src[i], f, depth + 1), i);
        return arr;
    }
    /// <summary>ToManaged's List&lt;T&gt;: Activator.CreateInstance, then IList.Add of each converted element (depth + 1).</summary>
    static IList MakeList(Conv c, List<object> src, SerializedFile f, int depth)
    {
        var list = (IList)Activator.CreateInstance(c.T); var e = c.Elem;
        for (int i = 0; i < src.Count; i++) list.Add(Value(e, src[i], f, depth + 1));
        return list;
    }
    /// <summary>Loader.FromTree's generic case: construct (struct: Activator.CreateInstance; class: the parameterless constructor, else an
    /// uninitialized object; a throwing constructor: an uninitialized object), fill the fields in SerRules order (each exception swallowed),
    /// OnAfterDeserialize (swallowed).</summary>
    static object MakeNested(Conv c, TypeTreeObject tt, SerializedFile f, int depth)
    {
        var type = c.T;
        if (!ReferenceEquals(tt.Keys, c.NestKeys)) { c.NestProg = ProgOf(tt.Keys, type, true); c.NestKeys = tt.Keys; }
        var p = c.NestProg;
        if (p.Abstract) return null;
        NestedFast++;
        object obj;
        LoadProf.Enter(LoadProf.NestedCtor);
        try
        {
            if (type.IsValueType) obj = Activator.CreateInstance(type);
            else
            {
                if (!c.CtorLooked) { c.Ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes); c.CtorLooked = true; }
                obj = c.Ctor is { } ctor ? ctor.Invoke(null) : RuntimeHelpers.GetUninitializedObject(type);
            }
        }
        catch { obj = RuntimeHelpers.GetUninitializedObject(type); }
        LoadProf.Exit(LoadProf.NestedCtor);   // (after a catch-all: always reached)
        var vals = tt.Values; var ops = p.Ops;
        for (int i = 0; i < ops.Length; i++)
        {
            var op = ops[i];
            try { StoreNested(op, obj, vals[op.Key], f, depth + 1); } catch { }
        }
        if (obj is ISerializationCallbackReceiver scr) { LoadProf.Enter(LoadProf.NestedAfter); try { scr.OnAfterDeserialize(); } catch { } LoadProf.Exit(LoadProf.NestedAfter); }
        return obj;
    }
    /// <summary>A nested field: Store's typed paths at this depth (ToManaged(fv, field type, f, depth)), else ToManaged + SetValue.</summary>
    static void StoreNested(Op op, object target, object v, SerializedFile f, int depth)
    {
        if (depth <= 16)
            switch (op.C.Kind)
            {
                case K.Array: if (v is List<object> la) { SetRef(op, target, MakeArray(op.C, la, f, depth)); FastFields++; return; } break;
                case K.List: if (v is List<object> ll) { SetRef(op, target, MakeList(op.C, ll, f, depth)); FastFields++; return; } break;
                case K.Nested:
                    if (v is TypeTreeObject nt) { var o = MakeNested(op.C, nt, f, depth); if (op.C.T.IsValueType || op.Off < 0) op.Fi.SetValue(target, o); else SetRef(op, target, o); FastFields++; return; }
                    break;
                case K.Generic: break;
                default: Store(op, target, v, f); return;   // (scalars, strings, objects, Unity structs: no depth inside them)
            }
        SlowFields++; if (Check) Slow(op.C.T, v);
        op.Fi.SetValue(target, Loader.ToManaged(v, op.Fi.FieldType, f, depth));
    }

    // ---------------- the boxed-struct store probe (kit #10 review) ----------------
    struct ProbeS
    {
        public long G0;   // (guard words around the fields: a store landing beside its field shows up here)
        public bool B; public byte U8; public char C; public short S; public int I; public float F; public long L; public double D; public DayOfWeek E;
        public Vector2 V2; public Vector3 V3; public Quaternion Q; public Color Col; public Rect R; public string Str; public object Obj;
        public long G1, G2, G3, G4;
    }
    [ProfileOnly]   // (its literals -- the probe's field names, messages -- stay out of the diet's readable-key scan: Assets/Diet.cs)
    static class BoxProbe
    {
        internal static string Why;
        internal static readonly bool Ok = Offsets && Run();
        static bool Run()
        {
            long ff = FastFields;   // (the probe's own stores are not counted in the end line)
            try
            {
                var t = typeof(ProbeS); var box = Activator.CreateInstance(t); int size = Unsafe.SizeOf<ProbeS>();
                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public);
                var ops = new Op[fields.Length];
                for (int i = 0; i < fields.Length; i++)
                {
                    var fi = fields[i]; int off = FieldCopy.Offset(fi);
                    int n = fi.FieldType.IsValueType ? FieldCopy.ValueSize(fi.FieldType) : IntPtr.Size;
                    if (off < 0 || n <= 0 || off + n > size) { Why = $"{fi.Name}: offset {off} + {n} outside the {size}-byte struct"; return false; }   // (nothing written yet)
                    ops[i] = new Op { Key = -1, Fi = fi, Off = off, C = fi.FieldType == typeof(object) ? null : ConvOf(fi.FieldType) };   // (Obj: SetRef only)
                }
                var obj = new object(); const string str = "probe";
                foreach (var op in ops)
                    switch (op.Fi.Name)
                    {   // (the programs' own stores: Store's scalar / enum / string paths, StoreStruct, SetRef)
                        case "B": Store(op, box, true, null); break;
                        case "U8": Store(op, box, (byte)0xA5, null); break;
                        case "C": Store(op, box, 'Q', null); break;
                        case "S": Store(op, box, (short)-12345, null); break;
                        case "I": Store(op, box, unchecked((int)0xDEADBEEF), null); break;
                        case "F": Store(op, box, -0f, null); break;
                        case "L": Store(op, box, 0x0123456789ABCDEFL, null); break;
                        case "D": Store(op, box, double.Epsilon, null); break;
                        case "E": Store(op, box, (int)DayOfWeek.Friday, null); break;
                        case "V2": StoreStruct(op, box, K.Vec2, new SV { A = 1.5f, B = -2f }); break;
                        case "V3": StoreStruct(op, box, K.Vec3, new SV { A = 3f, B = float.MaxValue, C = -4.25f }); break;
                        case "Q": StoreStruct(op, box, K.Quat, new SV { A = 0f, B = 0f, C = 0f, D = 1f }); break;
                        case "Col": StoreStruct(op, box, K.Color, new SV { A = 0.1f, B = 0.2f, C = 0.3f, D = 0.4f }); break;
                        case "R": StoreStruct(op, box, K.Rect, new SV { A = 5f, B = 6f, C = 7f, D = 8f }); break;
                        case "Str": Store(op, box, str, null); break;
                        case "Obj": SetRef(op, box, obj); break;
                    }
                var v = (ProbeS)box;
                bool ok = v.B && v.U8 == 0xA5 && v.C == 'Q' && v.S == -12345 && v.I == unchecked((int)0xDEADBEEF) && BitConverter.SingleToInt32Bits(v.F) == BitConverter.SingleToInt32Bits(-0f)
                    && v.L == 0x0123456789ABCDEFL && BitConverter.DoubleToInt64Bits(v.D) == BitConverter.DoubleToInt64Bits(double.Epsilon) && v.E == DayOfWeek.Friday
                    && v.V2.x == 1.5f && v.V2.y == -2f && v.V3.x == 3f && v.V3.y == float.MaxValue && v.V3.z == -4.25f && v.Q.x == 0f && v.Q.y == 0f && v.Q.z == 0f && v.Q.w == 1f
                    && v.Col.r == 0.1f && v.Col.g == 0.2f && v.Col.b == 0.3f && v.Col.a == 0.4f && v.R.x == 5f && v.R.y == 6f && v.R.width == 7f && v.R.height == 8f
                    && ReferenceEquals(v.Str, str) && ReferenceEquals(v.Obj, obj) && v.G0 == 0 && v.G1 == 0 && v.G2 == 0 && v.G3 == 0 && v.G4 == 0;
                if (!ok) { Why = "a value did not read back (or a guard word changed)"; return false; }
                return true;
            }
            catch (Exception e) { Why = e.GetType().Name + ": " + e.Message; return false; }
            finally { FastFields = ff; }
        }
    }

    // ---------------- check mode ----------------
    static void CheckFields(Prog p, object target, TypeTreeObject d, SerializedFile f)
    {
        foreach (var op in p.Ops)
        {
            if (!Comparable(op.C, 0)) continue;
            object want;
            try { want = Loader.ToManaged(d.Values[op.Key], op.Fi.FieldType, f, 0); } catch { continue; }   // (it threw: the field was left as it was both ways)
            Checked++;
            var have = op.Fi.GetValue(target);
            if (!Same(want, have)) { CheckDiffs++; _firstDiff ??= $"{p.T.FullName}.{op.Fi.Name} ({op.Fi.FieldType.Name}): want {Show(want)}, have {Show(have)}"; }
        }
    }
    static bool Comparable(Conv c, int depth) => depth < 3 && c.Kind switch
    {
        K.Generic => false,
        K.Nested => NestedComparable(c, depth),   // (kit #10 review: a nested STRUCT -- no constructor, no game code -- is compared field by field)
        K.Array or K.List => Comparable(c.Elem, depth + 1),
        _ => true,
    };
    /// <summary>A nested struct ToManaged can make again without running game code (Activator.CreateInstance, no OnAfterDeserialize) and whose
    /// serialized fields are all comparable; classes (their constructors are game code) stay out.</summary>
    static bool NestedComparable(Conv c, int depth)
    {
        if (!c.T.IsValueType || typeof(ISerializationCallbackReceiver).IsAssignableFrom(c.T)) return false;
        foreach (var fi in SerRules.Fields(c.T)) if (!Comparable(ConvOf(fi.FieldType), depth + 1)) return false;
        return true;
    }
    static bool Same(object a, object b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is UObject || b is UObject) return ReferenceEquals(a, b);
        if (a.GetType() != b.GetType()) return false;
        if (a is string sa) return sa == (string)b;
        if (a is IList la) { var lb = (IList)b; if (la.Count != lb.Count) return false; for (int i = 0; i < la.Count; i++) if (!Same(la[i], lb[i])) return false; return true; }
        if (a.GetType().IsValueType && Convs.TryGetValue(a.GetType(), out var nc) && nc.Kind == K.Nested)
        {   // (a nested struct: its serialized fields, each compared as above)
            foreach (var fi in SerRules.Fields(nc.T)) if (!Same(fi.GetValue(a), fi.GetValue(b))) return false;
            return true;
        }
        if (a.GetType().IsValueType) return BoxBytesEqual(a, b);
        return Equals(a, b);
    }
    static bool BoxBytesEqual(object a, object b)
    {
        int n = a switch { bool or byte or sbyte => 1, short or ushort or char => 2, int or uint or float => 4, long or ulong or double => 8, Vector2 => 8, Vector3 => 12, Vector4 or Quaternion or Color or Rect => 16, _ => -1 };
        if (n < 0) { if (a.GetType().IsEnum) n = System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(a.GetType())); else return Equals(a, b); }
        ref byte x = ref Unsafe.As<RawData>(a).B, y = ref Unsafe.As<RawData>(b).B;
        for (int i = 0; i < n; i++) if (Unsafe.Add(ref x, i) != Unsafe.Add(ref y, i)) return false;
        return true;
    }
    static string Show(object o) => o switch { null => "null", UObject u => $"{u.GetType().Name}#{Shepherd.Engine.Native.Objects.Get(u)?.InstanceId}", IList l => $"{o.GetType().Name}[{l.Count}]", _ => o.ToString() };

    public static string Summary() => !On ? null :
        $"[k10] field programs: {Objects} objects filled by {Programs} programs ({NestedPrograms} nested), fields {FastFields} typed / {SlowFields} through ToManaged, {NestedFast} nested objects, {Exceptions} field exceptions; stores by {(Offsets ? "field offset" : "FieldInfo.SetValue")}{BoxNote()}" +
        (Check ? $"; check: {Checked} fields compared with ToManaged, {CheckDiffs} difference(s){(_firstDiff != null ? ", first " + _firstDiff : "")}; through ToManaged: {string.Join(", ", _slow.OrderByDescending(kv => kv.Value).Take(14).Select(kv => kv.Key + " x" + kv.Value))}" : "");
}
