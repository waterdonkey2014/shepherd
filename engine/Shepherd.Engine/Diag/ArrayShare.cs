using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Diag;

/// <summary>kit4 intro-plan EXPERIMENT (measurement only, off unless set; never a shipping path): SHEPHERD_EXP_ARRAYSHARE=&lt;frame&gt;:&lt;type&gt;[,&lt;type&gt;...]
/// -- after boot frame N, every one-dimensional primitive array (float[], int[], short[], ushort[], byte[]) held in a FIELD (or in an element of
/// an object[] / reference array) of the object graph below each live UnityEngine.Object whose type short name is listed is replaced by the
/// first array with the same type, length and bytes met in that walk. The walk does not cross into other UnityEngine.Objects, engine records,
/// delegates or pointers. It answers two questions about sharing identical read-only arrays (the "Spine data diet" lever in
/// $SHEPHERD_WORK/kit4/intro-plan.md): (1) how much live heap it frees (a forced full GC before and after, logged); (2) whether anything
/// writes into the shared arrays afterwards -- compare SHEPHERD_STREAM_HASH of the run with one without the switch: any write that changes what
/// is drawn shows up as a differing frame. Generic reflection over the game's own objects (no game- or library-specific code).</summary>
[ProfileOnly]
public static class ArrayShare
{
    static readonly int At = -1; static readonly HashSet<string> Types = new(StringComparer.Ordinal);
    static ArrayShare()
    {
        var v = Environment.GetEnvironmentVariable("SHEPHERD_EXP_ARRAYSHARE");
        if (string.IsNullOrEmpty(v)) return;
        var p = v.Split(':', 2); if (p.Length != 2 || !int.TryParse(p[0], out At)) { At = -1; return; }
        foreach (var t in p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Types.Add(t);
    }

    /// <summary>SHEPHERD_EXP_ARRAYSHARE_CHECK=&lt;frame&gt;[,&lt;frame&gt;...]: re-hash every shared array (one that received at least one re-pointed
    /// reference) and count the ones whose bytes changed since the share -- a write into a shared array would corrupt every holder.</summary>
    static readonly int[] Checks = (Environment.GetEnvironmentVariable("SHEPHERD_EXP_ARRAYSHARE_CHECK") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var v) ? v : -1).ToArray();
    static readonly List<(object arr, ulong h, int holders)> Shared = new();
    public static void After(int frame)
    {
        try
        {
            if (frame == At) Run();
            if (Shared.Count > 0 && Array.IndexOf(Checks, frame) >= 0)
            {
                int changed = 0; long changedBytes = 0;
                foreach (var (a, h, _) in Shared) if (Hash(a) != h) { changed++; changedBytes += Bytes(a); }
                Log.Info($"[arrayshare] check at frame {frame}: {Shared.Count:N0} shared arrays re-hashed, {changed} changed ({changedBytes / 1024.0:F1} KiB){(changed == 0 ? " -- none written since the share" : " -- WRITTEN: sharing is not safe for these")}");
            }
        }
        catch (Exception e) { Log.Exception(e, "ArrayShare"); }
    }
    static ulong Hash(object a) { Prim(a, out var b); ulong h = 14695981039346656037UL; foreach (var x in b) { h ^= x; h *= 1099511628211UL; } return h ^ (ulong)b.Length; }

    static readonly Dictionary<Type, (FieldInfo[] refs, FieldInfo[] structs)> FieldCache = new();
    /// <summary>Reference-type instance fields (walked; a primitive array there may be re-pointed) and struct fields that hold references
    /// (walked read-only through a boxed copy: their arrays are counted as reached, never re-pointed).</summary>
    static (FieldInfo[] refs, FieldInfo[] structs) Fields(Type t)
    {
        if (FieldCache.TryGetValue(t, out var f)) return f;
        var l = new List<FieldInfo>(); var st = new List<FieldInfo>();
        for (var c = t; c != null && c != typeof(object); c = c.BaseType)
            foreach (var fi in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var ft = fi.FieldType;
                if (ft.IsPointer || ft.IsByRefLike || typeof(Delegate).IsAssignableFrom(ft)) continue;
                if (!ft.IsValueType) l.Add(fi);
                else if (!ft.IsPrimitive && !ft.IsEnum && HasRefs(ft)) st.Add(fi);
            }
        return FieldCache[t] = (l.ToArray(), st.ToArray());
    }
    static readonly Dictionary<Type, bool> RefCache = new();
    static bool HasRefs(Type vt)
    {
        if (RefCache.TryGetValue(vt, out var r)) return r;
        RefCache[vt] = false;
        r = vt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(x => !x.FieldType.IsValueType || (!x.FieldType.IsPrimitive && !x.FieldType.IsEnum && !x.FieldType.IsPointer && HasRefs(x.FieldType)));
        return RefCache[vt] = r;
    }
    static void PushStruct(object box, Stack<object> stack, int depth)
    {
        if (box == null || depth > 8) return;
        var (refs, structs) = Fields(box.GetType());
        foreach (var f in refs) { object v; try { v = f.GetValue(box); } catch { continue; } if (v != null && (Prim(v, out _) || Follow(v))) stack.Push(v); }
        foreach (var f in structs) { object v; try { v = f.GetValue(box); } catch { continue; } PushStruct(v, stack, depth + 1); }
    }

    static bool Prim(object o, out ReadOnlySpan<byte> b)
    {
        switch (o)
        {
            case float[] f: b = MemoryMarshal.AsBytes<float>(f); return true;
            case int[] i: b = MemoryMarshal.AsBytes<int>(i); return true;
            case short[] s: b = MemoryMarshal.AsBytes<short>(s); return true;
            case ushort[] u: b = MemoryMarshal.AsBytes<ushort>(u); return true;
            case byte[] y: b = y; return true;
            default: b = default; return false;
        }
    }

    sealed class KeyCmp : IEqualityComparer<object>
    {
        public new bool Equals(object x, object y) => x.GetType() == y.GetType() && Prim(x, out var a) && Prim(y, out var b) && a.SequenceEqual(b);
        public int GetHashCode(object o) { Prim(o, out var b); var h = new HashCode(); h.Add(o.GetType()); h.AddBytes(b); return h.ToHashCode(); }
    }

    static void Run()
    {
        long before = Live();
        var canon = new Dictionary<object, object>(new KeyCmp());
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        int roots = 0; long replaced = 0, replacedBytes = 0, arrays = 0;
        foreach (var n in Objects.All.ToList())
        {
            var m = n.Managed; if (m is null || !Types.Contains(m.GetType().Name)) continue;
            roots++; stack.Push(m);
            while (stack.Count > 0)
            {
                var o = stack.Pop(); if (!visited.Add(o)) continue;
                var t = o.GetType();
                if (t.IsArray)
                {
                    var et = t.GetElementType()!;
                    if (o is object[] oa && !et.IsValueType)
                        for (int i = 0; i < oa.Length; i++) { var e = oa[i]; if (e == null) continue; if (Prim(e, out _)) { arrays++; var c = Canon(canon, e); if (!ReferenceEquals(c, e)) { oa[i] = c; replaced++; replacedBytes += Bytes(e); } } else if (Follow(e)) stack.Push(e); }
                    else if (et.IsValueType && !et.IsPrimitive && !et.IsEnum && t.GetArrayRank() == 1 && HasRefs(et))
                        foreach (var e in (Array)o) PushStruct(e, stack, 0);   // e.g. dictionary entries: reached read-only
                    else if (Prim(o, out _)) { arrays++; Canon(canon, o); }   // an array reached through a struct: canonical candidate only
                    continue;
                }
                var (refs, structs) = Fields(t);
                foreach (var f in refs)
                {
                    object v; try { v = f.GetValue(o); } catch { continue; }
                    if (v == null) continue;
                    if (Prim(v, out _)) { arrays++; var c = Canon(canon, v); if (!ReferenceEquals(c, v)) { try { f.SetValue(o, c); replaced++; replacedBytes += Bytes(v); } catch { } } }
                    else if (Follow(v)) stack.Push(v);
                }
                foreach (var f in structs) { object v; try { v = f.GetValue(o); } catch { continue; } PushStruct(v, stack, 0); }
            }
        }
        long after = Live();
        Log.Info($"[arrayshare] frame {At}: {roots} root object(s) of [{string.Join(",", Types)}]; {visited.Count:N0} objects walked, {arrays:N0} primitive arrays seen, " +
                 $"{canon.Count:N0} distinct; {replaced:N0} references re-pointed to an identical array ({replacedBytes / 1048576.0:F2} MiB of array payload, 64-bit); " +
                 $"live heap after a forced full GC {before / 1048576.0:F2} -> {after / 1048576.0:F2} MiB (-{(before - after) / 1048576.0:F2} MiB)");
    }
    static readonly HashSet<object> SharedSet = new(ReferenceEqualityComparer.Instance);
    static object Canon(Dictionary<object, object> canon, object a)
    {
        if (canon.TryGetValue(a, out var c)) { if (!ReferenceEquals(c, a) && SharedSet.Add(c)) Shared.Add((c, Hash(c), 2)); return c; }
        canon[a] = a; return a;
    }
    static long Bytes(object a) => Prim(a, out var b) ? b.Length : 0;
    static bool Follow(object v) => v is not UnityEngine.Object && v is not NObj && v is not string && v is not Type && v is not Delegate && v is not MemberInfo && !v.GetType().IsPrimitive;
    static long Live() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
}
