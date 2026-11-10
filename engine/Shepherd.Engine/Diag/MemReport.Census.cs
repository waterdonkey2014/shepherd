#if !SHEPHERD_EBOOT   // kit #7 measure: a Mac planning tool, left out of the eboots (Diag/MemReport.Eboot.cs; SHEPHERD_ENGINE_TOOLS=1 keeps it)
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Assets;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Diag;

/// <summary>Heap census (SHEPHERD_MEMREPORT=N:W:census): walks the live managed object graph from every root we can enumerate
/// (static fields of all loaded types, closed generic types discovered on the way), with a reference-equality visited set,
/// and sizes every object twice:
///   64-bit CoreCLR  — the real allocation: max(24, align8(16 + RuntimeHelpers.GetRawObjectDataSize(o)))
///                     (16 = object header/syncblock 8 + MethodTable* 8; raw data = MT.BaseSize-16 + length*ComponentSize).
///   32-bit Mono     — projected with dotnet/runtime Mono's rules (src/mono/mono/metadata/object-internals.h, class-init.c):
///                     MonoObject {MonoVTable* vtable; MonoThreadsSync* synchronisation} = 8 B; refs/IntPtr = 4 B;
///                     MonoArray = MonoObject + MonoArrayBounds* bounds + uint32 max_length = 16 B before vector[]
///                     (vector is mono_64bitaligned_t); multi-dim arrays append rank x MonoArrayBounds{length,lower_bound} (8 B);
///                     MonoString = MonoObject + int32 length + (length+1) UTF-16 chars;
///                     class fields (auto layout => gc_aware_layout): pass 0 = fields holding references (aligned >= 4) in
///                     declaration order, pass 1 = the rest in declaration order, each at its natural alignment; derived
///                     fields start at parent->instance_size; instance_size rounded to min_align; value types keep
///                     declaration order (no gc-aware pass); int64/double align 8 (ARM EABI); SGen rounds every
///                     allocation to SGEN_ALLOC_ALIGN = 8. A second 32-bit figure also applies SGen's major-heap block
///                     size classes (ms_calculate_block_obj_sizes, 16 KiB blocks, factor 2^(1/3)) and LOS (>8000 B,
///                     modelled as align4096(size+16)) — where long-lived objects actually live after promotion.
/// Attribution: (a) per root = bytes first reached from it (exclusive, in walk order: game statics, Unity statics, engine
/// statics, BCL statics, then late-discovered generic statics) plus RETAINED = total minus a re-walk without that root;
/// (b) per owner = the nearest UnityEngine.Object (by type), engine record field (NObj subclass.field, split by record
/// type and live/destroyed/unregistered), SerializedFile/BundleFile, or "static-held" above the object; (c) retained by the
/// Mac-only asset plumbing (NTexture.Rgba, NObj.Data, NObj.Props, NObj.SourceFile, SerializedFile/BundleFile) = total minus
/// re-walks that do not follow those edges — the non-destructive, order-independent version of :drop.
/// Reflection objects (Type/MemberInfo/Assembly/Module) are counted but not traversed. Objects in .NET 8's frozen segments
/// (string literals, RuntimeType; GC.GetGeneration == int.MaxValue) are counted for the Mono projection (Mono keeps them in
/// the GC heap) but reported separately for the CoreCLR GC-heap reconciliation.
/// Game/Unity static constructors are never run by the census (see Discover; one did crash the process when triggered);
/// only BCL closed-generic types with a .cctor are read normally.
/// Runs at the end of the timing window (after every measurement), ~45 s in Release, several minutes in Debug.</summary>
public static partial class MemReport
{
    // runtime internals, bound lazily in HeapCensus so nothing here runs (or can fail) when the census is not requested
    static Func<object, nuint> RawDataSize;
    static MethodInfo RfhGetValue;

    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static int A(int v, int a) => (v + a - 1) & ~(a - 1);
    static long A(long v, long a) => (v + a - 1) & ~(a - 1);

    // ---------------------------------------------------------------- layout model
    /// <summary>Value-type layout under both ABIs + which fields hold references (for walking boxed copies).</summary>
    sealed class VInfo
    {
        public int Size32, Align32, Size64Rule, Align64Rule; public bool HasRefs;
        public FieldInfo[] Refs = Array.Empty<FieldInfo>(); public (FieldInfo f, VInfo v)[] Structs = Array.Empty<(FieldInfo, VInfo)>();
    }
    /// <summary>Per runtime type: projected 32-bit size (class instance / boxed value / array element), how to walk it, tallies.</summary>
    sealed class TInfo
    {
        public Type T; public string Name; public bool Leaf, IsString, IsArray, IsUObj, IsNObj, IsAssetFile;
        public int Inst32, Inst64Rule, MinAlign32; public bool Box;   // class instance size (with header) / boxed value type
        public FieldInfo[] Refs = Array.Empty<FieldInfo>(); public (FieldInfo f, VInfo v)[] Structs = Array.Empty<(FieldInfo, VInfo)>();
        public int[] RefOwner, StructOwner;                                     // owner label per field (NObj / asset-file records)
        public int Elem32; public bool ElemRef; public VInfo ElemV; public bool SzArray; public int Rank;
        public int OwnerSelf = -1;                                             // owner label this object (and its children) switch to
        public bool Named;                                                     // SHEPHERD_CENSUS_NAMED: owner label per instance (name)
        public long N, B64, B32, B32Sg, Frozen64, Real64;
    }
    static int NamedOwner(TInfo ti, object o) => OwnerId(MemReportKit4.NamedLabel(ti.Name, o), "UObj " + ti.Name);   // kit4: SHEPHERD_CENSUS_NAMED
    static readonly Dictionary<Type, VInfo> VCache = new();
    static readonly Dictionary<Type, TInfo> TCache = new();

    static bool IsRefType(Type t) => !t.IsValueType && !t.IsPointer && !t.IsByRef && !t.IsFunctionPointer && !t.IsUnmanagedFunctionPointer;
    static FieldInfo[] InstanceFields(Type t) => t.GetFields(Inst).OrderBy(f => f.MetadataToken).ToArray();

    static (int size, int align) Prim(Type t, bool x64)
    {
        if (t.IsEnum) t = Enum.GetUnderlyingType(t);
        if (t.IsPointer || t.IsFunctionPointer || t.IsUnmanagedFunctionPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return x64 ? (8, 8) : (4, 4);
        switch (Type.GetTypeCode(t))
        {
            case TypeCode.Boolean: case TypeCode.Byte: case TypeCode.SByte: return (1, 1);
            case TypeCode.Char: case TypeCode.Int16: case TypeCode.UInt16: return (2, 2);
            case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Single: return (4, 4);
            case TypeCode.Int64: case TypeCode.UInt64: case TypeCode.Double: return (8, 8);   // ARM EABI aligns 64-bit scalars to 8
        }
        return (-1, -1);
    }
    /// <summary>(size, align) of a field of type ft, and whether it holds references.</summary>
    static (int size, int align, bool refs) FieldShape(Type ft, bool x64)
    {
        if (IsRefType(ft)) return x64 ? (8, 8, true) : (4, 4, true);
        var p = Prim(ft, x64); if (p.size > 0) return (p.size, p.align, false);
        var v = VOf(ft); return x64 ? (v.Size64Rule, v.Align64Rule, v.HasRefs) : (v.Size32, v.Align32, v.HasRefs);
    }

    static VInfo VOf(Type t)
    {
        if (VCache.TryGetValue(t, out var v)) return v;
        VCache[t] = v = new VInfo { Size32 = 1, Align32 = 1, Size64Rule = 1, Align64Rule = 1 };   // provisional (recursion guard)
        var fs = InstanceFields(t);
        var refs = new List<FieldInfo>(); var structs = new List<(FieldInfo, VInfo)>();
        foreach (var f in fs)
        {
            var ft = f.FieldType;
            if (IsRefType(ft)) { refs.Add(f); v.HasRefs = true; }
            else if (ft.IsValueType && !ft.IsPrimitive && !ft.IsEnum && Prim(ft, false).size < 0) { var iv = VOf(ft); if (iv.HasRefs) { structs.Add((f, iv)); v.HasRefs = true; } }
        }
        v.Refs = refs.ToArray(); v.Structs = structs.ToArray();
        var sl = t.StructLayoutAttribute; int pack = sl?.Pack ?? 0; int minSize = sl?.Size ?? 0;
        foreach (var x64 in new[] { false, true })
        {
            int real = 0, minAlign = 1;
            if (sl != null && sl.Value == LayoutKind.Explicit)
            {
                foreach (var f in fs) { var (s, a, _) = FieldShape(f.FieldType, x64); int off = f.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0; real = Math.Max(real, off + s); minAlign = Math.Max(minAlign, pack > 0 ? Math.Min(pack, a) : a); }
            }
            else
                foreach (var f in fs)
                {
                    var (s, a, hr) = FieldShape(f.FieldType, x64); if (pack > 0) a = Math.Min(pack, a); if (hr) a = Math.Max(a, x64 ? 8 : 4);
                    real = A(real, a) + s; minAlign = Math.Max(minAlign, a);
                }
            int size = Math.Max(1, A(Math.Max(real, minSize), minAlign));
            if (x64) { v.Size64Rule = size; v.Align64Rule = minAlign; } else { v.Size32 = size; v.Align32 = minAlign; }
        }
        return v;
    }

    /// <summary>Mono 32-bit class instance size (header included) and min_align, recursively over the base chain.</summary>
    static readonly Dictionary<Type, (int size, int minAlign)> MonoClassCache = new();
    static (int size, int minAlign) MonoClass(Type t)
    {
        if (t == null || t == typeof(object)) return (8, 1);
        if (MonoClassCache.TryGetValue(t, out var r)) return r;
        var (real, minAlign) = MonoClass(t.BaseType);
        var fs = InstanceFields(t); var sl = t.StructLayoutAttribute;
        bool explicitL = sl != null && sl.Value == LayoutKind.Explicit, gcAware = sl == null || sl.Value == LayoutKind.Auto;
        int instance = real;
        if (explicitL)
        {
            foreach (var f in fs) { var (s, a, _) = FieldShape(f.FieldType, false); int off = f.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0; instance = Math.Max(instance, real + off + s); minAlign = Math.Max(minAlign, a); }
        }
        else
            for (int pass = 0; pass < (gcAware ? 2 : 1); pass++)
            {
                foreach (var f in fs)
                {
                    var (s, a, hr) = FieldShape(f.FieldType, false);
                    if (gcAware && (hr ? pass == 1 : pass == 0)) continue;
                    if (hr) a = Math.Max(a, 4);
                    minAlign = Math.Max(minAlign, a);
                    real = A(real, a) + s;
                }
                instance = Math.Max(instance, real); instance = A(instance, minAlign);
            }
        if (sl != null && sl.Size > 0) instance = Math.Max(instance, 8 + sl.Size);
        return MonoClassCache[t] = (A(instance, minAlign), minAlign);
    }

    /// <summary>CoreCLR 64-bit class layout by rule (only to validate the field machinery against the real MethodTable size):
    /// parent data first; then this class's references, then the rest by descending size; BaseSize = max(24, align8(16+data)).</summary>
    static readonly Dictionary<Type, int> ClrDataCache = new();
    static int ClrData(Type t)
    {
        if (t == null || t == typeof(object)) return 0;
        if (ClrDataCache.TryGetValue(t, out var r)) return r;
        int real = ClrData(t.BaseType);
        var sl = t.StructLayoutAttribute;
        var fs = InstanceFields(t).Select(f => (f, sh: FieldShape(f.FieldType, true))).ToList();
        if (sl != null && sl.Value == LayoutKind.Explicit) { int end = real; foreach (var (f, sh) in fs) end = Math.Max(end, real + (f.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0) + sh.size); real = end; }
        else
        {
            var order = sl != null && sl.Value == LayoutKind.Sequential ? fs : fs.Where(x => IsRefType(x.f.FieldType)).Concat(fs.Where(x => !IsRefType(x.f.FieldType)).OrderByDescending(x => x.sh.align).ThenByDescending(x => x.sh.size)).ToList();
            foreach (var (f, sh) in order) real = A(real, sh.align) + sh.size;
        }
        return ClrDataCache[t] = real;
    }

    const int ReadNormal = 0, ReadNoInit = 1;
    /// <summary>Static reference field value WITHOUT running the class constructor (CoreCLR 8: RuntimeFieldHandle.GetValue with
    /// domainInitialized=true skips the class-init check). Non-generic types only — generic dynamic statics may be unallocated.</summary>
    static object NoInitStatic(FieldInfo f) => RfhGetValue.Invoke(null, new object[] { f, null, f.FieldType, f.DeclaringType, true });

    static readonly Type UObjType = typeof(UnityEngine.Object);
    static TInfo TOf(Type t)
    {
        if (TCache.TryGetValue(t, out var ti)) return ti;
        TCache[t] = ti = new TInfo { T = t, Name = Pretty(t) };
        if (typeof(MemberInfo).IsAssignableFrom(t) || typeof(Assembly).IsAssignableFrom(t) || typeof(Module).IsAssignableFrom(t) || typeof(ParameterInfo).IsAssignableFrom(t)) ti.Leaf = true;
        if (t == typeof(string)) { ti.IsString = true; ti.Leaf = true; return ti; }
        if (t.IsArray)
        {
            ti.IsArray = true; ti.SzArray = t.IsSZArray; ti.Rank = t.GetArrayRank(); var et = t.GetElementType()!;
            ti.ElemRef = IsRefType(et);
            if (ti.ElemRef) ti.Elem32 = 4;
            else { var p = Prim(et, false); if (p.size > 0) ti.Elem32 = p.size; else { ti.ElemV = VOf(et); ti.Elem32 = ti.ElemV.Size32; } }
            if (!ti.ElemRef && (ti.ElemV == null || !ti.ElemV.HasRefs)) ti.Leaf = true;
            return ti;
        }
        if (t.IsValueType)
        {   // a boxed value
            ti.Box = true; var p = Prim(t, false);
            if (p.size > 0) { ti.Inst32 = 8 + p.size; ti.Leaf = true; return ti; }
            var v = VOf(t); ti.Inst32 = 8 + v.Size32; ti.Refs = v.Refs; ti.Structs = v.Structs; ti.Leaf |= !v.HasRefs;
            ti.Inst64Rule = Math.Max(24, A(16 + v.Size64Rule, 8));
            return ti;
        }
        (ti.Inst32, ti.MinAlign32) = MonoClass(t);
        if (typeof(Delegate).IsAssignableFrom(t)) ti.Inst32 = 64;   // MonoMulticastDelegate: MonoObject + 12 pointers + 2 MonoBoolean + MonoArray* delegates (object-internals.h), not CoreLib's 4 fields
        ti.Inst64Rule = Math.Max(24, A(16 + ClrData(t), 8));
        var refs = new List<FieldInfo>(); var structs = new List<(FieldInfo, VInfo)>();
        for (var c = t; c != null && c != typeof(object); c = c.BaseType)
            foreach (var f in InstanceFields(c))
            {
                var ft = f.FieldType;
                if (IsRefType(ft)) refs.Add(f);
                else if (ft.IsValueType && Prim(ft, false).size < 0 && !ft.IsByRefLike) { var iv = VOf(ft); if (iv.HasRefs) structs.Add((f, iv)); }
            }
        ti.Refs = refs.ToArray(); ti.Structs = structs.ToArray();
        ti.IsUObj = UObjType.IsAssignableFrom(t);
        ti.IsNObj = typeof(NObj).IsAssignableFrom(t);
        ti.IsAssetFile = t == typeof(SerializedFile) || t == typeof(BundleFile);
        if (ti.IsUObj) { ti.OwnerSelf = OwnerId("UObj " + ti.Name); ti.Named = MemReportKit4.NamedTypes.Contains(t.Name); }
        if (ti.IsAssetFile) ti.OwnerSelf = OwnerId(t.Name + " (whole-file / block buffers)");
        if (ti.IsNObj)
        {
            ti.OwnerSelf = OwnerId("engine NObj records (shallow)");
            ti.RefOwner = ti.Refs.Select(f => OwnerId($"{f.DeclaringType!.Name}.{f.Name}")).ToArray();
            ti.StructOwner = ti.Structs.Select(s => OwnerId($"{s.f.DeclaringType!.Name}.{s.f.Name}")).ToArray();
        }
        // late roots: statics of closed generic types only become visible once we meet an instance
        for (var c = t; c != null; c = c.BaseType) if (c.IsConstructedGenericType && GenericStaticsSeen.Add(c)) PendingGenericStatics.Enqueue(c);
        return ti;
    }
    static string Pretty(Type t)
    {
        if (t.IsArray) return Pretty(t.GetElementType()!) + (t.IsSZArray ? "[]" : $"[{new string(',', t.GetArrayRank() - 1)}]");
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        var n = (t.Namespace != null ? t.Namespace + "." : "") + t.Name; int i = n.IndexOf('`'); if (i > 0) n = n[..i];
        if (t.IsNested && t.DeclaringType != null) n = Pretty(t.DeclaringType) + "+" + t.Name.Split('`')[0];
        return n + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
    }

    // ---------------------------------------------------------------- SGen major-heap size classes (32-bit, 16 KiB blocks)
    static int[] _sgClasses;
    static long Sgen(long size)
    {
        if (size > 8000) return A(size + 16, 4096);   // LOS
        if (_sgClasses == null)
        {
            const int blockFree = 16384 - 288;   // MS_BLOCK_SKIP = align16(sizeof(MSBlockInfo) + 64 mark words) on 32-bit (approx.)
            var l = new List<int>(); int last = 0;
            for (int s = 8; s <= 32; s += 8) { l.Add(s); last = s; }
            double target = last, factor = Math.Pow(2.0, 1.0 / 3);
            do { int count = (int)Math.Floor(blockFree / target); int s = Math.Min((blockFree / count) & ~7, 8000); if (s != last) { l.Add(s); last = s; } target *= factor; } while (last < 8000);
            _sgClasses = l.ToArray();
        }
        foreach (var c in _sgClasses) if (c >= size) return c;
        return size;
    }

    // ---------------------------------------------------------------- walk state
    static readonly List<string> OwnerNames = new(); static readonly Dictionary<string, int> OwnerIds = new();
    static readonly List<int> OwnerBase = new();   // owner id -> the id cut sets are expressed in (variants of one engine field share it)
    static int OwnerId(string s, string cutKey = null)
    {
        if (!OwnerIds.TryGetValue(s, out var i)) { OwnerIds[s] = i = OwnerNames.Count; OwnerNames.Add(s); OwnerBase.Add(i); if (cutKey != null && cutKey != s) OwnerBase[i] = OwnerId(cutKey); }
        return i;
    }
    /// <summary>Per-instance label for the big engine-record fields (Data/Props/SourceFile/Rgba): which record type holds it and
    /// whether that record is live in Objects.ById, destroyed, or alive-but-unregistered.</summary>
    static int RecordFieldOwner(NObj n, int baseId)
    {
        string state = n.Destroyed ? "destroyed" : Objects.ById.TryGetValue(n.InstanceId, out var reg) && ReferenceEquals(reg, n) ? "live" : "not in Objects.ById";
        return OwnerId($"{OwnerNames[baseId]}  [{n.GetType().Name}, {state}]", OwnerNames[baseId]);
    }
    static readonly HashSet<Type> GenericStaticsSeen = new(); static readonly Queue<Type> PendingGenericStatics = new();
    sealed class Tally { public long N, B64, B32, B32Sg; public void Add(long b64, long b32, long sg) { N++; B64 += b64; B32 += b32; B32Sg += sg; } }

    sealed class WalkResult
    {
        public long N, B64, B32, B32Sg, FrozenN, Frozen64, StrN, StrB32, PrimArrN, PrimArr64, PrimArr32, BigN, Big64, DupStrN, DupStrB32, ReadErrors;
        public List<Tally> Roots = new(); public Dictionary<int, Tally> Owners = new(); public Dictionary<Type, Tally> Types = new();
        public Dictionary<(int, Type), Tally> Joint;
        public long DupArrN, DupArrB32; public Dictionary<int, (long n, long b32)> DupArrByOwner;   // SHEPHERD_CENSUS_DUPARRAYS
        public Dictionary<string, (long n, long b32)> DupStrBySite; public Dictionary<string, (int n, long b32)> DupStrTop;   // kit #13 basec: SHEPHERD_CENSUS_DUPSTRINGS
    }
    sealed class Root { public string Label; public object Value; public VInfo V; }

    /// <summary>One graph walk from the recorded roots. skipRoot: roots left out; cut: owner labels whose edges are not followed
    /// (NObj fields) / whose objects are not counted (asset files). detail: per-type / per-owner tallies + string dedupe.
    /// discover: read statics and append roots while walking (first walk only).</summary>
    static WalkResult Walk(List<Root> roots, Func<int, bool> skipRoot, HashSet<int> cut, bool detail, Action<Action<Root>> discover, int capacity, bool joint = false)
    {
        var r = new WalkResult { Joint = joint ? new() : null };
        var visited = new HashSet<object>(capacity, ReferenceEqualityComparer.Instance);
        var stack = new Stack<(object o, int owner)>(1 << 16);
        var strDup = detail ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
        var arrDup = detail && MemReportKit4.DupArrays ? new HashSet<(Type, int, ulong, int)>() : null; if (arrDup != null) r.DupArrByOwner = new();
        // kit #13 basec (SHEPHERD_CENSUS_DUPSTRINGS=<n>): a duplicate string's site = the type.field (or array / boxed struct) that reached it
        var strSite = detail && MemReportKit4.DupStrings > 0 ? new Dictionary<object, string>(ReferenceEqualityComparer.Instance) : null;
        if (strSite != null) { r.DupStrBySite = new(); r.DupStrTop = new(StringComparer.Ordinal); }
        string site = null;
        int staticOwner = OwnerId("static-held (no UnityEngine.Object / engine record above it)");
        Tally rt = null;
        void Push(object o, int owner) { if (o != null && visited.Add(o)) { stack.Push((o, owner)); if (strSite != null && (o is string || o is object[])) strSite[o] = site ?? "(root)"; } }
        void PushStruct(object box, VInfo v, int owner, int depth)
        {
            if (box == null || depth > 16) return;
            foreach (var f in v.Refs) { object x; try { x = f.GetValue(box); } catch { r.ReadErrors++; continue; } Push(x, owner); }
            foreach (var (f, iv) in v.Structs) { object x; try { x = f.GetValue(box); } catch { r.ReadErrors++; continue; } PushStruct(x, iv, owner, depth + 1); }
        }
        void Drain()
        {
            while (stack.Count > 0)
            {
                var (o, owner) = stack.Pop();
                var ti = TOf(o.GetType());
                if (ti.OwnerSelf >= 0) { if (cut != null && cut.Contains(OwnerBase[ti.OwnerSelf])) continue; owner = ti.Named ? NamedOwner(ti, o) : ti.OwnerSelf; }
                long s64 = Math.Max(24L, A((long)RawDataSize(o) + 16, 8L)), s32;
                if (ti.IsString)
                {
                    int len = ((string)o).Length; s32 = A(12 + 2 * (len + 1), 8); r.StrN++; r.StrB32 += s32;
                    if (detail && len <= 512)
                    {
                        ref var c = ref CollectionsMarshal.GetValueRefOrAddDefault(strDup, (string)o, out bool had); if (had) { r.DupStrN++; r.DupStrB32 += s32; } c++;
                        if (had && strSite != null)
                        {
                            var st = strSite.GetValueOrDefault(o) ?? "?"; var cs = r.DupStrBySite.GetValueOrDefault(st); r.DupStrBySite[st] = (cs.n + 1, cs.b32 + s32);
                            var ct = r.DupStrTop.GetValueOrDefault((string)o); r.DupStrTop[(string)o] = (ct.n + 1, ct.b32 + s32);
                        }
                    }
                }
                else if (ti.IsArray)
                {
                    long n = ((Array)o).LongLength; s32 = ti.SzArray ? A(16 + n * ti.Elem32, 8) : A(A(16 + n * ti.Elem32, 4) + ti.Rank * 8, 8);
                    if (ti.Leaf && !ti.ElemRef)
                    {
                        r.PrimArrN++; r.PrimArr64 += s64; r.PrimArr32 += s32;
                        if (arrDup != null && ti.SzArray && n > 0 && MemReportKit4.ArrayKey(o) is var k && k.ok && !arrDup.Add((ti.T, k.len, k.h1, k.h2)))
                        { r.DupArrN++; r.DupArrB32 += s32; var cur = r.DupArrByOwner.GetValueOrDefault(owner); r.DupArrByOwner[owner] = (cur.n + 1, cur.b32 + s32); }
                    }
                }
                else s32 = A(ti.Inst32, 8);
                long sg = Sgen(s32);
                if (GC.GetGeneration(o) == int.MaxValue) { r.FrozenN++; r.Frozen64 += s64; if (detail) ti.Frozen64 += s64; }
                if (detail) { if (ti.N == 0) ti.Real64 = s64; ti.N++; ti.B64 += s64; ti.B32 += s32; ti.B32Sg += sg; }
                r.N++; r.B64 += s64; r.B32 += s32; r.B32Sg += sg;
                if (s64 >= 85000) { r.BigN++; r.Big64 += s64; }
                rt.Add(s64, s32, sg);
                if (detail || cut != null) { if (!r.Owners.TryGetValue(owner, out var ot)) r.Owners[owner] = ot = new Tally(); ot.Add(s64, s32, sg); if (!r.Types.TryGetValue(ti.T, out var tt)) r.Types[ti.T] = tt = new Tally(); tt.Add(s64, s32, sg); }
                if (r.Joint != null) { if (!r.Joint.TryGetValue((owner, ti.T), out var jt)) r.Joint[(owner, ti.T)] = jt = new Tally(); jt.Add(s64, s32, sg); }
                if (ti.Leaf) continue;
                if (strSite != null) site = ti.IsArray ? (strSite.GetValueOrDefault(o) ?? "?") + " -> " + TypeLabel(ti.T) : null;
                if (ti.IsArray)
                {
                    if (ti.ElemRef) { if (ti.SzArray) foreach (var e in (object[])o) Push(e, owner); else foreach (var e in (Array)o) Push(e, owner); }
                    else { var arr = (Array)o; if (ti.SzArray) for (int i = 0; i < arr.Length; i++) PushStruct(arr.GetValue(i), ti.ElemV, owner, 0); else foreach (var e in arr) PushStruct(e, ti.ElemV, owner, 0); }
                    continue;
                }
                for (int i = 0; i < ti.Refs.Length; i++)
                {
                    int ow = ti.RefOwner != null ? ti.RefOwner[i] : owner; if (cut != null && ti.RefOwner != null && cut.Contains(OwnerBase[ow])) continue;
                    object x; try { x = ti.Refs[i].GetValue(o); } catch { r.ReadErrors++; continue; }
                    if (x != null && detail && ti.IsNObj && BigRecordFields.Contains(ow)) ow = RecordFieldOwner((NObj)o, ow);
                    if (strSite != null && x is string) site = ti.T.Name + "." + ti.Refs[i].Name;
                    Push(x, ow);
                }
                for (int i = 0; i < ti.Structs.Length; i++)
                {
                    int ow = ti.StructOwner != null ? ti.StructOwner[i] : owner; if (cut != null && ti.StructOwner != null && cut.Contains(OwnerBase[ow])) continue;
                    object x; try { x = ti.Structs[i].f.GetValue(o); } catch { r.ReadErrors++; continue; }
                    if (strSite != null) site = ti.T.Name + "." + ti.Structs[i].f.Name + " (struct)";
                    PushStruct(x, ti.Structs[i].v, ow, 0);
                }
            }
        }
        void Do(Root root, int i)
        {
            rt = new Tally(); r.Roots.Add(rt);
            if (skipRoot != null && skipRoot(i)) return;
            if (root.V == null) Push(root.Value, staticOwner); else PushStruct(root.Value, root.V, staticOwner, 0);
            Drain();
        }
        if (discover != null) discover(root => { roots.Add(root); Do(root, roots.Count - 1); });
        else for (int i = 0; i < roots.Count; i++) Do(roots[i], i);
        return r;
    }

    /// <summary>Property-bag bytes by (managed type, key), first-reach over all bags (64-bit actual sizes); the walk stops at
    /// UnityEngine.Object / engine records / asset files, so it is the bag's own payload (LazyRef objects count, not their file).</summary>
    /// <summary>kit #13 basec: a short generic type label (Dictionary`2+Entry[String,Object][] style) for the duplicate-string sites.</summary>
    static string TypeLabel(Type t)
    {
        if (t.IsArray) return TypeLabel(t.GetElementType()) + "[]";
        if (!t.IsGenericType) return t.Name;
        string n = t.Name; int q = n.IndexOf('`'); if (q > 0) n = n[..q];
        if (t.DeclaringType is { IsGenericType: true } d) { string dn = d.Name; int dq = dn.IndexOf('`'); n = (dq > 0 ? dn[..dq] : dn) + "+" + n; }
        return n + "<" + string.Join(",", t.GetGenericArguments().Select(TypeLabel)) + ">";
    }

    static void PropsBreakdown()
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var byKey = new Dictionary<string, (long n, long b, long vals)>(); var byType = new Dictionary<string, (long bags, long b)>();
        var stack = new Stack<object>();
        long Size(object root)
        {
            long total = 0; if (root == null || !visited.Add(root)) return 0; stack.Push(root);
            while (stack.Count > 0)
            {
                var o = stack.Pop(); var t = o.GetType();
                if (o is UnityEngine.Object || o is NObj || o is SerializedFile || o is BundleFile || o is MemberInfo || o is Assembly) continue;
                total += Math.Max(24L, A((long)RawDataSize(o) + 16, 8L));
                var ti = TOf(t); if (ti.Leaf || ti.IsString) continue;
                if (ti.IsArray) { if (ti.ElemRef) foreach (var e in (Array)o) { if (e != null && visited.Add(e)) stack.Push(e); } continue; }
                foreach (var f in ti.Refs)
                {
                    if (o is Shepherd.Engine.Assets.LazyRef) break;   // LazyRef.File is the asset file: not the bag's
                    object x; try { x = f.GetValue(o); } catch { continue; }
                    if (x != null && visited.Add(x)) stack.Push(x);
                }
            }
            return total;
        }
        var recs = new HashSet<NObj>(Objects.All);
        foreach (var n in Assets.Loader.LiveRecords) recs.Add(n);
        long all = 0;
        foreach (var n in recs)
        {
            if (n.Props == null) continue;
            var tn = n.Managed?.GetType().Name ?? n.GetType().Name;
            long bag = Math.Max(24L, A((long)RawDataSize(n.Props) + 16, 8L)); visited.Add(n.Props);
            if (n.Props is Dictionary<string, object>)   // (kit6 mem L4a: a shaped bag is one object[], its size is the line above)
            {
                var entries = typeof(Dictionary<string, object>).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(n.Props);
                if (entries != null && visited.Add(entries)) bag += Math.Max(24L, A((long)RawDataSize(entries) + 16, 8L));
                var buckets = typeof(Dictionary<string, object>).GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(n.Props);
                if (buckets != null && visited.Add(buckets)) bag += Math.Max(24L, A((long)RawDataSize(buckets) + 16, 8L));
            }
            long sum = bag;
            foreach (var kv in PropBag.Entries(n.Props))
            {
                long b = Size(kv.Value); sum += b;
                var k = tn + "." + kv.Key; var e = byKey.GetValueOrDefault(k); byKey[k] = (e.n + 1, e.b + b, e.vals + (kv.Value is System.Collections.ICollection c ? c.Count : 1));
            }
            var et = byType.GetValueOrDefault(tn); byType[tn] = (et.bags + 1, et.b + sum); all += sum;
        }
        W($"--- property bags (NObj.Props), first-reach 64-bit bytes incl. dictionaries: {MB(all)} in {byType.Values.Sum(x => x.bags)} bags; by record type (top 15):");
        foreach (var kv in byType.OrderByDescending(x => x.Value.b).Take(15)) W($"   {MB(kv.Value.b),11}  {kv.Value.bags,6} bags  {kv.Key}");
        W("   by type.key (top 40): bytes | bags | elements");
        foreach (var kv in byKey.OrderByDescending(x => x.Value.b).Take(40)) W($"   {MB(kv.Value.b),11}  {kv.Value.n,6}  {kv.Value.vals,9:N0}  {kv.Key}");
    }

    static readonly HashSet<int> BigRecordFields = new();
    static void HeapCensus(int frame, bool light = false)
    {
        if (IsMono) { W("heap census skipped: it sizes objects with CoreCLR's internal RuntimeHelpers.GetRawObjectDataSize (absent on Mono)"); return; }
        RawDataSize ??= (Func<object, nuint>)Delegate.CreateDelegate(typeof(Func<object, nuint>), typeof(RuntimeHelpers).GetMethod("GetRawObjectDataSize", BindingFlags.Static | BindingFlags.NonPublic)!);
        RfhGetValue ??= typeof(RuntimeFieldHandle).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(m => m.Name == "GetValue" && m.GetParameters().Length == 5);
        foreach (var n in new[] { "NObj.Data", "NObj.Props", "NObj.SourceFile", "NTexture.Rgba" }) BigRecordFields.Add(OwnerId(n));
        W($"===== heap census @ frame {frame} (end of the timing window; all reachable objects from enumerable roots) =====");
        CompactLohOnce();
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
        var gi = FullGcInfo(); long totalMem = GC.GetTotalMemory(false);
        long genSum = 0; for (int g = 0; g < gi.GenerationInfo.Length; g++) genSum += gi.GenerationInfo[g].SizeAfterBytes;
        long liveObj = gi.HeapSizeBytes - gi.FragmentedBytes;
        W($"GC after forced compacting GC: HeapSizeBytes={MB(gi.HeapSizeBytes)} (fragmented {MB(gi.FragmentedBytes)} => live objects ~{MB(liveObj)}) GC.GetTotalMemory(false)={MB(totalMem)} sum(gen SizeAfter)={MB(genSum)} " +
          string.Join(" ", Enumerable.Range(0, gi.GenerationInfo.Length).Select(g => $"{(g < 3 ? "gen" + g : g == 3 ? "LOH" : "POH")}={MB(gi.GenerationInfo[g].SizeAfterBytes)}/frag {MB(gi.GenerationInfo[g].FragmentationAfterBytes)}")));
        var sw = Stopwatch.StartNew();
        long readErrors = 0, staticFields = 0, skippedStatics = 0, skippedGenericCctor = 0, cctorNotRun = 0; int typeCount = 0, genericRoots = 0;
        var roots = new List<Root>();

        // ---- roots, in a deliberate order: game -> Unity -> engine -> BCL -> closed generics met on the way
        // Reading statics must never run a static constructor the game has not run yet (it crashed the process once:
        // UnityEngine.Yoga.YogaConfig..cctor threw, then its finalizer). So:
        //  - type without a .cctor: FieldInfo.GetValue (class init runs no user code);
        //  - non-generic type with a .cctor: reference statics are read with RuntimeFieldHandle.GetValue(domainInitialized: true),
        //    which skips CheckRunClassInitThrowing and returns the slot as-is (null if never initialized); if any of them is
        //    non-null the class has been initialized (stsfld/cctor ran), so its value-type statics are then safe to read too;
        //  - closed generic type with a .cctor: dynamic statics may not even be allocated (reading would AV), so only BCL types
        //    (well-behaved cctors, e.g. ArrayPool<T>.Shared) are read normally; game/Unity ones are skipped and counted.
        string Group(Assembly a)
        {
            var n = a.GetName().Name ?? ""; var l = a.IsDynamic ? "" : a.Location ?? "";
            if (n.StartsWith("Shepherd")) return "engine";
            if (n.StartsWith("UnityEngine") || n.StartsWith("Unity.")) return "unity";
            if (l.StartsWith(Program.GameManaged) || l.StartsWith(Program.RuntimeManaged)) return "game";
            return "bcl";
        }
        void Discover(Action<Root> add)
        {
            void RootField(FieldInfo f, string label, int mode)
            {
                var ft = f.FieldType;
                if (f.IsLiteral || ft.IsPointer || ft.IsByRefLike || ft.IsFunctionPointer || ft.IsUnmanagedFunctionPointer) return;
                bool isRef = IsRefType(ft); VInfo v = null;
                if (!isRef) { if (Prim(ft, false).size > 0) return; v = VOf(ft); if (!v.HasRefs) return; }
                object val;
                try
                {
                    if (mode == ReadNormal) val = f.GetValue(null);
                    else if (mode == ReadNoInit && isRef) val = NoInitStatic(f);
                    else { skippedStatics++; return; }
                }
                catch { readErrors++; return; }
                if (val == null) return;
                staticFields++; add(new Root { Label = label, Value = val, V = v });
            }
            void TypeStatics(Type t, string group)
            {
                if (t.ContainsGenericParameters || t == typeof(MemReport)) return;   // (our own census caches are not game memory)
                FieldInfo[] fs; try { fs = t.GetFields(Stat); } catch { readErrors++; return; }
                if (fs.Length == 0) return;
                if (t.TypeInitializer == null) { foreach (var f in fs) RootField(f, $"[{group}] {Pretty(t)}.{f.Name}", ReadNormal); return; }
                if (t.IsConstructedGenericType)
                {
                    if (Group(t.Assembly) != "bcl") { skippedGenericCctor++; return; }
                    foreach (var f in fs) RootField(f, $"[{group}] {Pretty(t)}.{f.Name}", ReadNormal); return;
                }
                bool inited = false;
                foreach (var f in fs) if (!f.IsLiteral && IsRefType(f.FieldType)) { try { if (NoInitStatic(f) != null) { inited = true; break; } } catch { } }
                if (!inited) cctorNotRun++;
                foreach (var f in fs) RootField(f, $"[{group}] {Pretty(t)}.{f.Name}", inited ? ReadNormal : ReadNoInit);
            }
            var asms = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
            foreach (var grp in new[] { "game", "unity", "engine", "bcl" })
                foreach (var a in asms.Where(a => Group(a) == grp).OrderBy(a => a.GetName().Name))
                {
                    Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray()!; }
                    foreach (var t in ts) { typeCount++; TypeStatics(t, grp); }
                }
            while (PendingGenericStatics.Count > 0) { var t = PendingGenericStatics.Dequeue(); genericRoots++; TypeStatics(t, "generic"); }
        }
        var full = Walk(roots, null, null, true, Discover, 1 << 22);
        sw.Stop();
        int cap = (int)Math.Min(int.MaxValue / 4, full.N + full.N / 4);

        // ---- report: totals
        long b64 = full.B64, b32 = full.B32, b32sg = full.B32Sg, reachedHeap64 = b64 - full.Frozen64;
        W($"walk: {full.N:N0} objects from {staticFields:N0} non-null static fields of {typeCount:N0} types (+{genericRoots} closed generic types found on the way) in {sw.Elapsed.TotalSeconds:F1} s; {readErrors + full.ReadErrors} field reads failed");
        W($"   static-constructor safety: {cctorNotRun:N0} non-generic types with a .cctor the game never ran (read without init -> all null), {skippedStatics:N0} value-type statics of such types skipped, {skippedGenericCctor:N0} non-BCL closed generic types with a .cctor skipped");
        W($"TOTAL reached, 64-bit CoreCLR (actual sizes)   = {MB(b64)}  ({MB(full.Frozen64)} of it in frozen segments = {full.FrozenN:N0} literal strings/RuntimeTypes, not GC heap)");
        W($"   => reached GC-heap objects {MB(reachedHeap64)} of {MB(liveObj)} live (HeapSizeBytes - fragmentation) = {100.0 * reachedHeap64 / Math.Max(1, liveObj):F1}%; unreached {MB(liveObj - reachedHeap64)} (stack / GC-handle / unenumerated-generic-static roots). GC.GetTotalMemory(false) said {MB(totalMem)} (+{MB(totalMem - liveObj)} vs live: not a live-bytes meter here)");
        W($"TOTAL projected 32-bit Mono (align 8)          = {MB(b32)}  ({100.0 * b32 / Math.Max(1, b64):F1}% of 64-bit)");
        W($"TOTAL projected 32-bit Mono + SGen size classes = {MB(b32sg)}  (major-heap block size-class rounding + LOS pages; +{100.0 * (b32sg - b32) / Math.Max(1, b32):F1}%)");
        W($"   arrays of primitives/no-ref structs: {full.PrimArrN:N0} objects, 64-bit {MB(full.PrimArr64)}, 32-bit {MB(full.PrimArr32)}; objects >= 85000 B (LOH on CoreCLR): {full.BigN:N0} = {MB(full.Big64)}");
        W($"   everything else (object graph proper): 64-bit {MB(b64 - full.PrimArr64)} -> 32-bit {MB(b32 - full.PrimArr32)} ({100.0 * (b32 - full.PrimArr32) / Math.Max(1, b64 - full.PrimArr64):F1}%)");
#if !SHEPHERD_NO_K13WALKC   // (kit #13 walkc: compiled out with the build switch back to kit #12, SHEPHERD_K13_WALKC_CODE=0)
        // kit #13 walkc: the "MiB short" meter -- the census's 32-bit figures against the GC heap's cap at the device partitions on the walk (kit #12
        // VLX: 00098 ~140 MiB, 00095 ~135 MiB at the intro's newlib; SHEPHERD_K13_SHORT_CAPS=<a>,<b> replaces them). DERIVED: the object graph is
        // the yardstick that matched the Vita (kit13-prep-findings section 2: the intro's 172.9 vs the Vita's ~175); the total adds the Mac's
        // primitive arrays (texture copies, unpacked TextAsset bytes) that a pack run on the Vita mostly does not hold.
        {
            double c98 = 140, c95 = 135;
            var cs = (Environment.GetEnvironmentVariable("SHEPHERD_K13_SHORT_CAPS") ?? "").Split(',');
            if (cs.Length == 2 && double.TryParse(cs[0], out var x98) && double.TryParse(cs[1], out var x95)) { c98 = x98; c95 = x95; }
            double og = (b32 - full.PrimArr32) / 1048576.0, tot = b32sg / 1048576.0;
            string S(double need, double cap) => need > cap ? $"SHORT {need - cap:F1} MiB" : $"spare {cap - need:F1} MiB";
            W($"K13 SHORT (DERIVED; kit #13 walkc's meter): against the GC heap cap at the device partitions on the walk (00098 {c98:F0} / 00095 {c95:F0} MiB): the object graph {og:F1} MiB -> {S(og, c98)} / {S(og, c95)}; the whole 32-bit + SGen total {tot:F1} MiB -> {S(tot, c98)} / {S(tot, c95)}");
        }
#endif
        W($"   strings: {full.StrN:N0} = {MB(full.StrB32)} (32-bit); duplicates (<=512 chars, same content seen before): {full.DupStrN:N0} = {MB(full.DupStrB32)} reclaimable by interning/dedupe");
        if (full.DupStrBySite != null)
        {
            W($"   duplicate strings by the field that reached them (SHEPHERD_CENSUS_DUPSTRINGS; first-reach: the copy the walk met second and later), top {MemReportKit4.DupStrings}:");
            foreach (var kv in full.DupStrBySite.OrderByDescending(x => x.Value.b32).Take(MemReportKit4.DupStrings)) W($"      {kv.Value.n,10:N0} {MB(kv.Value.b32),12}  {kv.Key}");
            W($"   the most duplicated values (top {MemReportKit4.DupStrings} by bytes; text cut at 60 characters):");
            foreach (var kv in full.DupStrTop.OrderByDescending(x => x.Value.b32).Take(MemReportKit4.DupStrings)) W($"      {kv.Value.n,10:N0} {MB(kv.Value.b32),12}  \"{(kv.Key.Length > 60 ? kv.Key[..60] + "..." : kv.Key).Replace("\n", "\\n")}\"");
        }
        if (full.DupArrByOwner != null) foreach (var line in MemReportKit4.DupArrayLines(full.DupArrN, full.DupArrB32, full.DupArrByOwner.Select(kv => (OwnerNames[kv.Key], kv.Value.n, kv.Value.b32)), x => MB(x))) W(line);
        var vt = TCache.Values.Where(t => t.N > 0 && !t.IsArray && !t.IsString && !t.Box && !t.T.IsAbstract).ToList();
        var bad = vt.Where(t => t.Inst64Rule != t.Real64).ToList();
        W($"   layout-rule check: CoreCLR rule size == real object size for {vt.Count - bad.Count}/{vt.Count} instantiated class types ({bad.Sum(t => t.N):N0} objects / {MB(bad.Sum(t => Math.Abs(t.Inst64Rule - t.Real64) * t.N))} off); the same field machinery feeds the Mono projection" +
          (bad.Count > 0 ? "; e.g. " + string.Join(", ", bad.OrderByDescending(t => t.N).Take(4).Select(t => $"{t.Name} rule={t.Inst64Rule} real={t.Real64}")) : ""));

        W("--- top 40 types by 64-bit bytes:   count | 64-bit | 32-bit Mono | +SGen classes | 32/64");
        foreach (var t in TCache.Values.Where(t => t.N > 0).OrderByDescending(t => t.B64).Take(40))
            W($"   {t.N,10:N0} {MB(t.B64),12} {MB(t.B32),12} {MB(t.B32Sg),12}  {100.0 * t.B32 / Math.Max(1, t.B64),5:F1}%  {t.Name}{(t.Frozen64 > 0 ? $"  (frozen {MB(t.Frozen64)})" : "")}");

        if (light)
        {
            W("--- top 25 owners:"); foreach (var kv in full.Owners.OrderByDescending(k => k.Value.B64).Take(25)) W($"   {kv.Value.N,10:N0} {MB(kv.Value.B64),12} {MB(kv.Value.B32),12}  {OwnerNames[kv.Key]}");
            W("--- top 15 roots (first-reach):"); foreach (var i in Enumerable.Range(0, roots.Count).OrderByDescending(i => full.Roots[i].B64).Take(15)) W($"   {full.Roots[i].N,10:N0} {MB(full.Roots[i].B64),12} {MB(full.Roots[i].B32),12}  {roots[i].Label}");
            TCache.Clear(); VCache.Clear(); MonoClassCache.Clear(); ClrDataCache.Clear(); OwnerIds.Clear(); OwnerNames.Clear(); OwnerBase.Clear(); BigRecordFields.Clear(); GenericStaticsSeen.Clear(); PendingGenericStatics.Clear();
            return;
        }
        // ---- roots: first-reach (order-dependent) + retained (order-independent: total minus a walk without that root)
        var top = Enumerable.Range(0, roots.Count).OrderByDescending(i => full.Roots[i].B64).Take(20).ToList();
        var retained = new Dictionary<int, WalkResult>();
        var sw2 = Stopwatch.StartNew();
        foreach (var i in top.Take(16)) retained[i] = Walk(roots, j => j == i, null, false, null, cap);
        W($"--- top 20 roots by bytes first reached (walk order game -> unity -> engine -> bcl -> generic) | RETAINED = held only through this root (total - walk without it; {sw2.Elapsed.TotalSeconds:F1} s for {retained.Count} extra walks)");
        W("        objects   first-reach 64 / 32-bit      retained 64 / 32-bit    root");
        foreach (var i in top)
        {
            string ret = retained.TryGetValue(i, out var w) ? $"{MB(b64 - w.B64),12} {MB(b32 - w.B32),12}" : $"{"",12} {"",12}";
            W($"   {full.Roots[i].N,10:N0} {MB(full.Roots[i].B64),12} {MB(full.Roots[i].B32),12}  {ret}  {roots[i].Label}");
        }
        foreach (var grp in new[] { "game", "unity", "engine", "bcl", "generic" })
        {
            var ids = Enumerable.Range(0, roots.Count).Where(i => roots[i].Label.StartsWith("[" + grp + "]")).ToHashSet();
            var w = Walk(roots, j => ids.Contains(j), null, false, null, cap);
            W($"   root group {grp,-8}: {ids.Count,6:N0} non-null fields, first-reach {ids.Sum(i => full.Roots[i].N),10:N0} objects 64-bit {MB(ids.Sum(i => full.Roots[i].B64))} 32-bit {MB(ids.Sum(i => full.Roots[i].B32))}; retained by the group 64-bit {MB(b64 - w.B64)} 32-bit {MB(b32 - w.B32)}");
        }
        W("--- top 40 owners (nearest UnityEngine.Object type / engine record field / asset file above each object; first-reach, so shared data lands on whichever path the walk took first):   objects | 64-bit | 32-bit | +SGen");
        foreach (var kv in full.Owners.OrderByDescending(k => k.Value.B64).Take(40))
            W($"   {kv.Value.N,10:N0} {MB(kv.Value.B64),12} {MB(kv.Value.B32),12} {MB(kv.Value.B32Sg),12}  {OwnerNames[kv.Key]}");

        // ---- Mac-engine asset plumbing, retained (cut walks; non-destructive mirror of the :drop attribution)
        int Oid(string n) => OwnerId(n);
        int rgba = Oid("NTexture.Rgba"), data = Oid("NObj.Data"), props = Oid("NObj.Props"), src = Oid("NObj.SourceFile"),
            sfile = Oid("SerializedFile (whole-file / block buffers)"), bfile = Oid("BundleFile (whole-file / block buffers)");
        var files = new[] { src, sfile, bfile };
        WalkResult Cut(params int[] ids) => Walk(roots, null, ids.ToHashSet(), false, null, cap);
        WalkResult CutJ(params int[] ids) => Walk(roots, null, ids.ToHashSet(), false, null, cap, joint: true);
        W("--- retained by Mac-engine asset plumbing (walks that do not follow those edges; order-independent):   64-bit | 32-bit | 32-bit+SGen  (reached after the cut)");
        void Line(string what, WalkResult w, WalkResult prev) => W($"   {what,-62} retained {MB((prev?.B64 ?? b64) - w.B64),11} {MB((prev?.B32 ?? b32) - w.B32),11}   -> remaining {MB(w.B64),11} {MB(w.B32),11} {MB(w.B32Sg),11}");
        var c1 = Cut(rgba); Line("NTexture.Rgba (decoded RGBA8)", c1, null);
        var c2 = Cut(rgba, data); Line("+ NObj.Data (source TypeTrees)", c2, c1);
        var c3 = Cut(rgba, data, props); Line("+ NObj.Props (property bags)", c3, c2);
        var c4 = CutJ(new[] { rgba, data, props }.Concat(files).ToArray()); Line("+ SerializedFile/BundleFile objects + NObj.SourceFile", c4, c3);
        var cd = Cut(data); Line("(alone) NObj.Data", cd, null);
        var cp = Cut(props); Line("(alone) NObj.Props", cp, null);
        var cf = Cut(files); Line("(alone) SerializedFile/BundleFile + NObj.SourceFile", cf, null);
        var cnp = Cut(new[] { rgba, data }.Concat(files).ToArray()); Line("Rgba + Data + files, Props KEPT (LazyRef/values still pin files)", cnp, null);
        W($"=> RESIDUAL after removing all plumbing: 64-bit {MB(c4.B64)} (+{MB(liveObj - reachedHeap64)} unreached => ~{MB(c4.B64 + liveObj - reachedHeap64)}, cf. :drop residual), projected 32-bit Mono {MB(c4.B32)}, +SGen classes {MB(c4.B32Sg)}");
        W("   residual by type (top 25):   count | 64-bit | 32-bit");
        foreach (var kv in c4.Types.OrderByDescending(k => k.Value.B64).Take(25)) W($"   {kv.Value.N,10:N0} {MB(kv.Value.B64),12} {MB(kv.Value.B32),12}  {Pretty(kv.Key)}");
        W("   residual by owner (top 25):   count | 64-bit | 32-bit");
        foreach (var kv in c4.Owners.OrderByDescending(k => k.Value.B64).Take(25)) W($"   {kv.Value.N,10:N0} {MB(kv.Value.B64),12} {MB(kv.Value.B32),12}  {OwnerNames[kv.Key]}");
        W("   residual: top types inside the 8 biggest owners (32-bit):");
        foreach (var kv in c4.Owners.OrderByDescending(k => k.Value.B64).Take(8))
            W($"     {OwnerNames[kv.Key]}: " + string.Join("; ", c4.Joint.Where(j => j.Key.Item1 == kv.Key).OrderByDescending(j => j.Value.B32).Take(7).Select(j => $"{Pretty(j.Key.Item2)} x{j.Value.N:N0} {MB(j.Value.B32)}")));
        W("   residual by root (first-reach, top 15):   count | 64-bit | 32-bit");
        foreach (var i in Enumerable.Range(0, roots.Count).OrderByDescending(i => c4.Roots[i].B64).Take(15)) W($"   {c4.Roots[i].N,10:N0} {MB(c4.Roots[i].B64),12} {MB(c4.Roots[i].B32),12}  {roots[i].Label}");
        PropsBreakdown();
        W($"census total time {sw2.Elapsed.TotalSeconds + sw.Elapsed.TotalSeconds:F1} s");
        TCache.Clear(); VCache.Clear(); MonoClassCache.Clear(); ClrDataCache.Clear(); OwnerIds.Clear(); OwnerNames.Clear(); OwnerBase.Clear(); BigRecordFields.Clear(); GenericStaticsSeen.Clear(); PendingGenericStatics.Clear();
    }

}
#endif
