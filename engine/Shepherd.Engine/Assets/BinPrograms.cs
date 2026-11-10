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

/// <summary>Device kit #10, track load, rows E3 + asset object snapshots (vita/notes/kit10-load.md): BINARY FIELD PROGRAMS -- the scripts that
/// carry no type tree (stripped MonoBehaviours: the I2 localization sources, Rewired's controller data and maps, ...; the pack keeps them as
/// Unity's own binary field data, rewritten by the bake's trims where needed) filled from those bytes by a program compiled once per script
/// type, instead of Loader.PopulateByReflection's generic walk (per value a chain of reflection tests -- IsEnum, IsPrimitive, typeof checks,
/// typeof(Object).IsAssignableFrom --, a box, FieldInfo.SetValue; per nested object a constructor lookup).
///
/// A program reads exactly what ReadField reads, in its order: SerRules.Fields(type), each field by its declared type -- primitives with
/// ReadPrim's alignment (1- and 2-byte values aligned after, inside arrays not), enums through their underlying type + Enum.ToObject, strings
/// (length, UTF-8, aligned), object references (a PPtr resolved through Loader.Managed: the same object created at the same moment, kept if
/// IsInstanceOfType), the Unity structs field by field (Quaternion not normalized, Bounds = (center, extent x 2)), arrays / lists (count
/// checked as ReadField checks it, the elements read first in order -- align off for the element itself --, then the array / list made and
/// filled), nested classes / structs (depth past 10 throws; struct = Activator.CreateInstance, class = the parameterless constructor -- looked
/// up once per type -- else an uninitialized object; then their fields). AnimationCurve / Gradient throw NotSupportedException, as there. Any
/// exception stops the walk where ReadField's would (the fields before it set, the rest left), with PopulateByReflection's own Warn line.
/// Stores: Mono's field offsets (FieldCopy), else FieldInfo.SetValue. Switch: SHEPHERD_K10_BINPROG=0 = PopulateByReflection.</summary>
static class BinPrograms
{
    public static readonly bool On = Runtime.K10LoadTrack.BinProg;
    public static long Objects, Programs, Fields, NestedObjects; public static long Stopped;
    static bool Offsets => FieldCopy.On;
    static readonly bool TraceSet = Environment.GetEnvironmentVariable("SHEPHERD_TRACE") is { Length: > 0 };

    enum K : byte { Bool, U8, I8, I16, U16, Char, I32, U32, F32, I64, U64, F64, Enum, String, UObj, Vec2, Vec3, Vec4, Quat, Color, Color32, Rect, LayerMask, V2I, V3I, Bounds, RectOffset, Bytes, ByteList, Array, List, Curve, Nested, Bad }
    sealed class Conv
    {
        public Type T; public K Kind; public K Under; public Conv Elem;   // Under: an enum's underlying primitive kind
        public Type LastType; public bool LastInst;                       // UObj: IsInstanceOfType per runtime type
        public ConstructorInfo Ctor; public bool CtorLooked; public Op[] Ops;   // Nested
    }
    sealed class Op { public FieldInfo Fi; public int Off = -1; public Conv C; }
    static readonly Dictionary<Type, Conv> Convs = new();

    static Conv ConvOf(Type t)
    {
        if (Convs.TryGetValue(t, out var c)) return c;
        c = new Conv { T = t }; Convs[t] = c;
        if (t.IsEnum) { c.Kind = K.Enum; c.Under = Prim(Enum.GetUnderlyingType(t)); if (c.Under == K.Bad) c.Kind = K.Bad; }
        else if (t.IsPrimitive) c.Kind = Prim(t);
        else if (t == typeof(string)) c.Kind = K.String;
        else if (typeof(UObject).IsAssignableFrom(t)) c.Kind = K.UObj;
        else if (t == typeof(Vector2)) c.Kind = K.Vec2; else if (t == typeof(Vector3)) c.Kind = K.Vec3; else if (t == typeof(Vector4)) c.Kind = K.Vec4;
        else if (t == typeof(Quaternion)) c.Kind = K.Quat; else if (t == typeof(Color)) c.Kind = K.Color; else if (t == typeof(Color32)) c.Kind = K.Color32;
        else if (t == typeof(Rect)) c.Kind = K.Rect; else if (t == typeof(LayerMask)) c.Kind = K.LayerMask; else if (t == typeof(Vector2Int)) c.Kind = K.V2I;
        else if (t == typeof(Vector3Int)) c.Kind = K.V3I; else if (t == typeof(Bounds)) c.Kind = K.Bounds; else if (t == typeof(RectOffset)) c.Kind = K.RectOffset;
        else if (t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var et = t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0];
            c.Kind = et == typeof(byte) ? (t.IsArray ? K.Bytes : K.ByteList) : t.IsArray ? K.Array : K.List;
            c.Elem = ConvOf(et);
        }
        else if (t == typeof(AnimationCurve) || t == typeof(Gradient)) c.Kind = K.Curve;
        else c.Kind = K.Nested;
        return c;
    }
    static K Prim(Type t) => t == typeof(bool) ? K.Bool : t == typeof(byte) ? K.U8 : t == typeof(sbyte) ? K.I8 : t == typeof(short) ? K.I16 : t == typeof(ushort) ? K.U16
        : t == typeof(char) ? K.Char : t == typeof(int) ? K.I32 : t == typeof(uint) ? K.U32 : t == typeof(float) ? K.F32 : t == typeof(long) ? K.I64 : t == typeof(ulong) ? K.U64
        : t == typeof(double) ? K.F64 : K.Bad;
    static Op[] OpsOf(Conv c)
    {
        if (c.Ops != null) return c.Ops;
        var fs = SerRules.Fields(c.T); var ops = new Op[fs.Length];
        for (int i = 0; i < fs.Length; i++)
        {
            var op = new Op { Fi = fs[i], C = ConvOf(fs[i].FieldType) };
            if (Offsets && (!c.T.IsValueType || FieldPrograms.BoxStores)) op.Off = FieldCopy.Offset(fs[i]);   // (a struct's fields: only if the boxed-struct probe passed -- kit #10 review)
            ops[i] = op;
        }
        return c.Ops = ops;
    }

    /// <summary>Loader.PopulateByReflection's work; false = not handled (the caller runs it).</summary>
    public static bool TryPopulate(object target, Type type, SerializedFile f, ObjectInfo o)
    {
        if (!On || TraceSet) return false;   // (SHEPHERD_TRACE: PopulateByReflection's field-by-field log)
        var c = ConvOf(type);
        var ops = OpsOf(c);
        Objects++;
        var r = new Rd(f.GetObjectMemory(o));
        try
        {
            r.PPtr(); r.U8(); r.Align(); r.PPtr(); r.Str();   // m_GameObject, m_Enabled, m_Script, m_Name
            for (int i = 0; i < ops.Length; i++) { StoreField(ops[i], target, ref r, f, 0); Fields++; }
        }
        catch (Exception e) { Stopped++; Log.Warn($"reflection-read {type.Name} stopped at byte {r.Pos}/{r.Len}: {e.Message}"); }
        return true;
    }

    // ---------------- stores ----------------
    sealed class RawData { public byte B; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ref byte At(object o, int off) => ref Unsafe.Add(ref Unsafe.As<RawData>(o).B, off);
    static void SetRef(Op op, object target, object v) { if (op.Off >= 0) Unsafe.As<byte, object>(ref At(target, op.Off)) = v; else op.Fi.SetValue(target, v); }
    /// <summary>One field: ReadField(ref r, field type, f, depth) then SetValue -- by the field's kind, typed where the value has no references.</summary>
    static void StoreField(Op op, object target, ref Rd r, SerializedFile f, int depth)
    {
        var c = op.C; int off = op.Off;
        if (off >= 0)
            switch (c.Kind)
            {
                case K.Bool: { byte b = r.U8(); r.Align(); At(target, off) = b != 0 ? (byte)1 : (byte)0; return; }
                case K.U8: case K.I8: { byte b = r.U8(); r.Align(); At(target, off) = b; return; }
                case K.I16: case K.U16: case K.Char: { ushort u = r.U16(); r.Align(); Unsafe.WriteUnaligned(ref At(target, off), u); return; }
                case K.I32: case K.U32: case K.F32: { int v = r.I32(); Unsafe.WriteUnaligned(ref At(target, off), v); return; }   // (a float's bits)
                case K.I64: case K.U64: case K.F64: { long v = r.I64(); Unsafe.WriteUnaligned(ref At(target, off), v); return; }
                case K.Enum: if (c.Under is K.I32 or K.U32) { int v = r.I32(); Unsafe.WriteUnaligned(ref At(target, off), v); return; } break;
                case K.Vec2: { float x = r.F(), y = r.F(); Unsafe.WriteUnaligned(ref At(target, off), new Vector2(x, y)); return; }
                case K.Vec3: { float x = r.F(), y = r.F(), z = r.F(); Unsafe.WriteUnaligned(ref At(target, off), new Vector3(x, y, z)); return; }
                case K.Vec4: { float x = r.F(), y = r.F(), z = r.F(), w = r.F(); Unsafe.WriteUnaligned(ref At(target, off), new Vector4(x, y, z, w)); return; }
                case K.Quat: { float x = r.F(), y = r.F(), z = r.F(), w = r.F(); Unsafe.WriteUnaligned(ref At(target, off), new Quaternion(x, y, z, w)); return; }
                case K.Color: { float x = r.F(), y = r.F(), z = r.F(), w = r.F(); Unsafe.WriteUnaligned(ref At(target, off), new Color(x, y, z, w)); return; }
                case K.Rect: { float x = r.F(), y = r.F(), z = r.F(), w = r.F(); Unsafe.WriteUnaligned(ref At(target, off), new Rect(x, y, z, w)); return; }
                case K.Color32: { uint u = r.U32(); Unsafe.WriteUnaligned(ref At(target, off), new Color32((byte)u, (byte)(u >> 8), (byte)(u >> 16), (byte)(u >> 24))); return; }
                case K.LayerMask: { int v = (int)r.U32(); Unsafe.WriteUnaligned(ref At(target, off), (LayerMask)v); return; }
                case K.String: SetRef(op, target, r.Str()); return;
                case K.UObj: SetRef(op, target, Obj(c, r.PPtr(), f)); return;
                case K.Bytes: case K.Array: case K.List: case K.ByteList: SetRef(op, target, Value(c, ref r, f, depth, true)); return;
                case K.Nested: if (!c.T.IsValueType) { SetRef(op, target, Value(c, ref r, f, depth, true)); return; } break;
            }
        op.Fi.SetValue(target, Value(c, ref r, f, depth, true));
    }
    static object Obj(Conv c, UPPtr p, SerializedFile f)
    {
        var m = Loader.Managed(f, p);
        if (m == null) return null;
        var mt = m.GetType();
        if (!ReferenceEquals(mt, c.LastType)) { c.LastInst = c.T.IsInstanceOfType(m); c.LastType = mt; }
        return c.LastInst ? m : null;
    }

    /// <summary>ReadField(ref r, c.T, f, depth, align) as an object, in its order of tests.</summary>
    static object Value(Conv c, ref Rd r, SerializedFile f, int depth, bool align)
    {
        switch (c.Kind)
        {
            case K.Enum: return Enum.ToObject(c.T, Prim(c.Under, ref r, align));
            case K.Bool: case K.U8: case K.I8: case K.I16: case K.U16: case K.Char: case K.I32: case K.U32: case K.F32: case K.I64: case K.U64: case K.F64: return Prim(c.Kind, ref r, align);
            case K.Bad: throw new NotSupportedException(c.T.IsEnum ? Enum.GetUnderlyingType(c.T).Name : c.T.Name);
            case K.String: return r.Str();
            case K.UObj: return Obj(c, r.PPtr(), f);
            case K.Vec2: return new Vector2(r.F(), r.F());
            case K.Vec3: return new Vector3(r.F(), r.F(), r.F());
            case K.Vec4: return new Vector4(r.F(), r.F(), r.F(), r.F());
            case K.Quat: return new Quaternion(r.F(), r.F(), r.F(), r.F());
            case K.Color: return new Color(r.F(), r.F(), r.F(), r.F());
            case K.Color32: { uint u = r.U32(); return new Color32((byte)u, (byte)(u >> 8), (byte)(u >> 16), (byte)(u >> 24)); }
            case K.Rect: return new Rect(r.F(), r.F(), r.F(), r.F());
            case K.LayerMask: return (LayerMask)(int)r.U32();
            case K.V2I: return new Vector2Int(r.I32(), r.I32());
            case K.V3I: return new Vector3Int(r.I32(), r.I32(), r.I32());
            case K.Bounds: return new Bounds(new Vector3(r.F(), r.F(), r.F()), new Vector3(r.F(), r.F(), r.F()) * 2f);
            case K.RectOffset: return new RectOffset(r.I32(), r.I32(), r.I32(), r.I32());
            case K.Bytes: case K.ByteList:
            {
                int n = r.I32(); if (n < 0 || n > 1_000_000) throw new InvalidDataException($"bad array size {n}");
                var bytes = r.Raw(n); r.Align();
                if (c.Kind == K.Bytes) return bytes;
                var bl = (IList)Activator.CreateInstance(c.T); foreach (var b in bytes) bl.Add(b); return bl;
            }
            case K.Array: case K.List:
            {
                int n = r.I32(); if (n < 0 || n > 1_000_000) throw new InvalidDataException($"bad array size {n}");
                var e = c.Elem;
                if (c.Kind == K.Array)
                {   // (ReadField: every element read first, then the array made and set: typed stores where the element has no references)
                    switch (e.Kind)
                    {
                        case K.I32: { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = r.I32(); r.Align(); return a; }
                        case K.F32: { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.F(); r.Align(); return a; }
                        case K.String: { var a = new string[n]; for (int i = 0; i < n; i++) a[i] = r.Str(); r.Align(); return a; }
                    }
                }
                var items = new object[n];
                for (int i = 0; i < n; i++) { items[i] = Value(e, ref r, f, depth + 1, false); }
                r.Align();
                if (c.Kind == K.Array) { var arr = Array.CreateInstance(e.T, n); if (!e.T.IsValueType) Array.Copy(items, arr, n); else for (int i = 0; i < n; i++) arr.SetValue(items[i], i); return arr; }
                var list = (IList)Activator.CreateInstance(c.T); foreach (var x in items) list.Add(x); return list;
            }
            case K.Curve: throw new NotSupportedException($"{c.T.Name} in stripped data");
        }
        // Nested
        if (depth > 10) throw new InvalidDataException("nesting too deep");
        NestedObjects++;
        object obj;
        var t = c.T;
        if (t.IsValueType) obj = Activator.CreateInstance(t);
        else
        {
            if (!c.CtorLooked) { c.Ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes); c.CtorLooked = true; }
            obj = c.Ctor?.Invoke(null) ?? RuntimeHelpers.GetUninitializedObject(t);
        }
        var ops = OpsOf(c);
        for (int i = 0; i < ops.Length; i++) StoreField(ops[i], obj, ref r, f, depth + 1);
        return obj;
    }
    static object Prim(K k, ref Rd r, bool align)
    {
        switch (k)
        {
            case K.Bool: { var b = r.U8() != 0; if (align) r.Align(); return b; }
            case K.U8: { var x = r.U8(); if (align) r.Align(); return x; }
            case K.I8: { var x = (sbyte)r.U8(); if (align) r.Align(); return x; }
            case K.I16: { var x = (short)r.U16(); if (align) r.Align(); return x; }
            case K.U16: { var x = r.U16(); if (align) r.Align(); return x; }
            case K.Char: { var x = (char)r.U16(); if (align) r.Align(); return x; }
            case K.I32: return r.I32(); case K.U32: return r.U32(); case K.F32: return r.F();
            case K.I64: return r.I64(); case K.U64: return (ulong)r.I64(); case K.F64: return BitConverter.Int64BitsToDouble(r.I64());
            default: throw new NotSupportedException(k.ToString());
        }
    }

    /// <summary>Loader's BinReader, the same reads (an object's bytes in place; Pos relative to the object; Need throws EndOfStreamException).</summary>
    ref struct Rd
    {
        readonly byte[] _b; readonly int _base, _len; public int Pos; public int Len => _len;
        public Rd(ReadOnlyMemory<byte> m)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(m, out var seg)) { _b = seg.Array; _base = seg.Offset; _len = seg.Count; }
            else { _b = m.ToArray(); _base = 0; _len = _b.Length; }
            Pos = 0;
        }
        void Need(int n) { if (Pos + n > _len) throw new EndOfStreamException(); }
        public byte U8() { Need(1); return _b[_base + Pos++]; }
        public byte[] Raw(int n) { Need(n); var r = _b.AsSpan(_base + Pos, n).ToArray(); Pos += n; return r; }
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_b, _base + Pos); Pos += 2; return v; }
        public int I32() { Need(4); var v = BitConverter.ToInt32(_b, _base + Pos); Pos += 4; return v; }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(_b, _base + Pos); Pos += 4; return v; }
        public long I64() { Need(8); var v = BitConverter.ToInt64(_b, _base + Pos); Pos += 8; return v; }
        public float F() { Need(4); var v = BitConverter.ToSingle(_b, _base + Pos); Pos += 4; return v; }
        public void Align() => Pos = (Pos + 3) & ~3;
        public string Str() { int n = I32(); if (n < 0 || Pos + n > _len) throw new InvalidDataException("bad string"); var s = Shepherd.Assets.StringPool.Get(new ReadOnlySpan<byte>(_b, _base + Pos, n), Shepherd.Assets.StringPool.Site.Bin); Pos += n; Align(); return s; }   // kit #13 basec: pooled (SHEPHERD_K13_STRPOOL=0: a new copy)
        public UPPtr PPtr() { int fid = I32(); long pid = I64(); return new UPPtr(fid, pid); }
    }

    public static string Summary() => !On ? null : $"[k10] binary field programs: {Objects} stripped scripts filled, {Fields} fields, {NestedObjects} nested objects, {Stopped} stopped early (as PopulateByReflection would){FieldPrograms.BoxNote()}";
}
