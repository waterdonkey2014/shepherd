using System.Reflection;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Runtime;

#if !SHEPHERD_EBOOT
/// <summary>kit5 mem, lever L1 PROTOTYPE (off unless SHEPHERD_SPINE_SHARE=1; never default): after the frame in which a Spine
/// SkeletonDataAsset built its SkeletonData (GetSkeletonData parses the skeleton file into a graph of attachments, timelines and
/// float[]/int[]/short[] arrays), every primitive array of that new graph that is byte-identical to one already kept -- by this asset or by any
/// asset processed before -- is replaced by the kept one, so identical vertex, UV, weight and key arrays exist once. The two 'Follower'
/// assets (kit5 L0: the Addressables bundle's copy and the game's Resources copy, the same 20.5 MB skeleton file, both built by the game as
/// in Unity) then share all their arrays. It is the sharing kit #4 measured once at frame 2950 for the whole heap (Diag/ArrayShare.cs), run
/// per new skeleton, across assets; it cannot lower the peak INSIDE a parse (that needs the read itself to share: a reader redirect or a
/// bake, kit4-intro-plan L1/B1), only what stays after it and the next parse's starting point.
/// Fidelity: a reader that WRITES into an array it shares would change every holder. SHEPHERD_SPINE_SHARE_CHECK=&lt;frame&gt;[,...] re-hashes
/// every array that received a re-pointed reference and logs how many changed (0 = no writes since the share); the stream hash (what is
/// drawn) compared with a run without the switch is the other check. Walks managed fields only (generic reflection; it does not cross into
/// UnityEngine.Objects, engine records, delegates or reflection objects), so nothing library-specific is compiled in. The kept arrays are held
/// strongly (prototype: a skeleton the game unloads keeps its distinct arrays alive).</summary>
[ProfileOnly]
public static class SpineShare
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_SHARE") == "1";
    static readonly int[] Checks = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_SHARE_CHECK") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.TryParse(x, out var v) ? v : -1).Where(v => v > 0).ToArray();
    static readonly List<(UnityEngine.Object asset, object done)> Assets = new();
    static readonly Dictionary<object, object> Canon = new(new KeyCmp());
    static readonly List<(object arr, ulong h)> Shared = new();
    static readonly HashSet<object> SharedSet = new(ReferenceEqualityComparer.Instance);
    static long _totalRepointed, _totalBytes; static int _processed;

    /// <summary>Loader: a SkeletonDataAsset object was created (by type short name; only when the switch is on).</summary>
    public static void Register(UnityEngine.Object asset) { if (On) Assets.Add((asset, null)); }

    public static void After(int frame)
    {
        if (!On) return;
        try
        {
            for (int i = 0; i < Assets.Count; i++)
            {
                var (a, done) = Assets[i];
                var sd = FieldValue(a, "skeletonData");
                if (sd == null || ReferenceEquals(sd, done)) continue;
                Assets[i] = (a, sd);
                Process(frame, a, sd);
            }
            if (Shared.Count > 0 && Array.IndexOf(Checks, frame) >= 0)
            {
                int changed = 0; long changedBytes = 0;
                foreach (var (arr, h) in Shared) if (Hash(arr) != h) { changed++; changedBytes += Bytes(arr); }
                Log.Info($"[spineshare] check at frame {frame}: {Shared.Count:N0} shared arrays re-hashed, {changed} changed ({changedBytes / 1024.0:F1} KiB){(changed == 0 ? " -- none written since the share" : " -- WRITTEN: sharing is not safe for these")}");
            }
        }
        catch (Exception e) { Log.Exception(e, "SpineShare"); }
    }

    static object FieldValue(object target, string fieldName)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
            if (t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } fi) return fi.GetValue(target);
        return null;
    }

    static void Process(int frame, UnityEngine.Object asset, object root)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>(); stack.Push(root);
        long arrays = 0, repointed = 0, bytes = 0; int canonBefore = Canon.Count;
        while (stack.Count > 0)
        {
            var o = stack.Pop(); if (!visited.Add(o)) continue;
            var t = o.GetType();
            if (t.IsArray)
            {
                var et = t.GetElementType()!;
                if (o is object[] oa && !et.IsValueType)
                    for (int i = 0; i < oa.Length; i++)
                    {
                        var e = oa[i]; if (e == null) continue;
                        if (Prim(e, out _)) { arrays++; var c = CanonOf(e); if (!ReferenceEquals(c, e)) { oa[i] = c; repointed++; bytes += Bytes(e); } }
                        else if (Follow(e)) stack.Push(e);
                    }
                else if (et.IsValueType && !et.IsPrimitive && !et.IsEnum && t.GetArrayRank() == 1 && HasRefs(et))
                    foreach (var e in (Array)o) PushStruct(e, stack, 0);   // e.g. dictionary entries: reached read-only
                else if (Prim(o, out _)) { arrays++; CanonOf(o); }        // reached through a struct: a canonical candidate only
                continue;
            }
            var (refs, structs) = Fields(t);
            foreach (var f in refs)
            {
                object v; try { v = f.GetValue(o); } catch { continue; }
                if (v == null) continue;
                if (Prim(v, out _)) { arrays++; var c = CanonOf(v); if (!ReferenceEquals(c, v)) { try { f.SetValue(o, c); repointed++; bytes += Bytes(v); } catch { } } }
                else if (Follow(v)) stack.Push(v);
            }
            foreach (var f in structs) { object v; try { v = f.GetValue(o); } catch { continue; } PushStruct(v, stack, 0); }
        }
        _processed++; _totalRepointed += repointed; _totalBytes += bytes;
        Log.Info($"[spineshare] frame {frame}: '{asset.name}' ({(Objects.Get(asset)?.SourceFile as Shepherd.Assets.SerializedFile)?.Name}): {visited.Count:N0} objects, {arrays:N0} primitive arrays, " +
                 $"{repointed:N0} re-pointed to an identical kept array ({bytes / 1048576.0:F2} MiB of payload), {Canon.Count - canonBefore:N0} new kept arrays, {sw.ElapsedMilliseconds} ms; " +
                 $"totals: {_processed} skeletons, {_totalRepointed:N0} re-pointed, {_totalBytes / 1048576.0:F2} MiB, {Canon.Count:N0} kept");
    }

    static object CanonOf(object a)
    {
        if (Canon.TryGetValue(a, out var c))
        {
            if (!ReferenceEquals(c, a) && SharedSet.Add(c)) Shared.Add((c, Hash(c)));
            return c;
        }
        Canon[a] = a; return a;
    }

    static readonly Dictionary<Type, (FieldInfo[] refs, FieldInfo[] structs)> FieldCache = new();
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
    static ulong Hash(object a) { Prim(a, out var b); ulong h = 14695981039346656037UL; foreach (var x in b) { h ^= x; h *= 1099511628211UL; } return h ^ (ulong)b.Length; }
    static long Bytes(object a) => Prim(a, out var b) ? b.Length : 0;
    static bool Follow(object v) => v is not UnityEngine.Object && v is not NObj && v is not string && v is not Type && v is not Delegate && v is not MemberInfo && !v.GetType().IsPrimitive;
}
#else
/// <summary>kit5 mem, lever L1 PROTOTYPE (off by default; no device use: kit5-mem.md). kit #5 integration (vita/notes/kit5-integration.md "Eboot room"): not in the eboot (SHEPHERD_EBOOT); a build with SHEPHERD_ENGINE_TOOLS=1 keeps it. The Mac and vita-sim builds are unchanged.
/// SHEPHERD_SPINE_SHARE=1 in an eboot logs that it is not available and changes nothing.</summary>
[ProfileOnly]
public static class SpineShare
{
    public static readonly bool On = false;
    public static void Register(UnityEngine.Object asset) { }
    public static void After(int frame)
    {
        if (frame == 1 && Environment.GetEnvironmentVariable("SHEPHERD_SPINE_SHARE") == "1") Log.Warn("[spineshare] SHEPHERD_SPINE_SHARE=1 ignored: the L1 prototype is not in this eboot (SHEPHERD_ENGINE_TOOLS=1 builds keep it)");
    }
}
#endif
