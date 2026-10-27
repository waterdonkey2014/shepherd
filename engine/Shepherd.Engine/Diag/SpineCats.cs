using System.Reflection;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>kit6 mem, L1 measurement (diagnostic only, off unless SHEPHERD_SPINE_CATS=1; never in an eboot): after the frame in which a
/// Spine SkeletonDataAsset built its SkeletonData, its graph is walked (generic reflection, as kit5's SpineShare prototype; nothing changes)
/// and every primitive array is filed under the field that holds it ("RegionAttachment.uvs", "DeformTimeline.frameVertices[]" for the
/// elements of an array held by that field, ...): count, 32-bit Mono bytes (16-byte header, 8-aligned), and how many of them have the
/// content of an array seen before -- in this skeleton or an earlier one (= what read-time sharing at that field would save). It tells
/// which reader sites a read-time intern has to cover. SHEPHERD_SPINE_CATS_TOP=&lt;n&gt; rows per skeleton (default 30).</summary>
[ProfileOnly]
public static class SpineCats
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CATS") is "1" or "hash";
    /// <summary>SHEPHERD_SPINE_CATS=hash: instead of the table, one deep CONTENT hash per skeleton (every field value in a fixed walk order,
    /// arrays by their elements' bits, wherever they are reached -- so a run that shares arrays and one that does not hash alike exactly when
    /// the skeleton data has the same content): the identity check of read-time sharing (kit6 mem L1).</summary>
    static readonly bool HashMode = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CATS") == "hash";
    static readonly int Top = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CATS_TOP"), out var t) ? t : 30;
    static readonly List<(UnityEngine.Object asset, object done)> Assets = new();
    static readonly Dictionary<object, string> Canon = new(new KeyCmp());   // content -> the asset that first had it
    sealed class Row { public long N, B, DupN, DupB, DupSelfN, DupSelfB; }
    static readonly Dictionary<string, Row> Totals = new();

    public static void Register(UnityEngine.Object asset) { if (On) Assets.Add((asset, null)); }

    /// <summary>kit #7 L2 check: SHEPHERD_SPINE_CATS_AT=&lt;frame&gt; (with =hash) hashes every built skeleton again after that frame (lazy skins
    /// read first): the data after gameplay, whatever was read when.</summary>
    static readonly int AgainAt = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CATS_AT"), out var aa) ? aa : -1;
    /// <summary>kit #7 review fixes R1 / R4 (kit7-spine.md "Review fixes"): SHEPHERD_SPINE_CATS_RACE=1 (with =hash and _AT=&lt;frame&gt;) leaves the
    /// lazy skins lazy at the build frames (no hash there) and at the _AT frame reads every skin still lazy on two worker threads while a third
    /// thread builds MeshAttachments one by one (VertexAttachment ids from the shared counter, as a skeleton read or a Copy elsewhere would) and
    /// a fourth hands arrays to the L1 intern hook (another thread's skeleton read). Checked: the builder's ids are consecutive (no id handed out
    /// twice), the counter moved by exactly the builder's count (each late read put back only its own), L1 sharing is still on. The counter is
    /// then set back (later ids as without the test) and every skeleton deep-hashed (= kit #6's hash at that frame).</summary>
    static readonly bool Race = HashMode && Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CATS_RACE") == "1";
    public static void After(int frame)
    {
        if (!On) return;
        if (frame == AgainAt && HashMode)
        {
            if (Race) try { RaceForce(frame); } catch (Exception e) { Log.Exception(e, "SpineCats race"); }
            foreach (var (a, done) in Assets) if (done != null) try { DeepHash(frame, a, done); } catch (Exception e) { Log.Exception(e, "SpineCats again"); }
        }
        try
        {
            for (int i = 0; i < Assets.Count; i++)
            {
                var (a, done) = Assets[i];
                var sd = FieldValue(a, "skeletonData");
                if (sd == null || ReferenceEquals(sd, done)) continue;
                Assets[i] = (a, sd);
                if (HashMode) { if (!Race) DeepHash(frame, a, sd); } else Process(frame, a, sd);
            }
        }
        catch (Exception e) { Log.Exception(e, "SpineCats"); }
    }

    static object FieldValue(object target, string fieldName)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
            if (t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } fi) return fi.GetValue(target);
        return null;
    }

    static long B32(object a) { Prim(a, out var b, out int esz); long n = esz == 0 ? 0 : b.Length / esz; return (16 + n * esz + 7) & ~7L; }

    static void Process(int frame, UnityEngine.Object asset, object root)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string name = asset.name;
        var rows = new Dictionary<string, Row>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(object o, string via)>(); stack.Push((root, "root"));
        void Note(object arr, string cat)
        {
            if (!visited.Add(arr)) return;   // the same array reached twice (Spine's own sharing: linked meshes): counted once
            if (!rows.TryGetValue(cat, out var r)) rows[cat] = r = new Row();
            if (!Totals.TryGetValue(cat, out var tr)) Totals[cat] = tr = new Row();
            long b = B32(arr); r.N++; r.B += b; tr.N++; tr.B += b;
            if (Canon.TryGetValue(arr, out var first)) { r.DupN++; r.DupB += b; tr.DupN++; tr.DupB += b; if (first == name) { r.DupSelfN++; r.DupSelfB += b; } }
            else Canon[arr] = name;
        }
        long objs = 0;
        while (stack.Count > 0)
        {
            var (o, via) = stack.Pop(); if (!visited.Add(o)) continue; objs++;
            var t = o.GetType();
            if (t.IsArray)
            {
                var et = t.GetElementType()!;
                if (o is object[] oa && !et.IsValueType)
                    for (int i = 0; i < oa.Length; i++)
                    {
                        var e = oa[i]; if (e == null) continue;
                        if (Prim(e, out _, out _)) Note(e, via + "[]");
                        else if (Follow(e)) stack.Push((e, via + "[]"));
                    }
                else if (et.IsValueType && !et.IsPrimitive && !et.IsEnum && t.GetArrayRank() == 1 && HasRefs(et))
                    foreach (var e in (Array)o) PushStruct(e, via + "[]", stack, 0, Note);
                continue;
            }
            var (refs, structs) = Fields(t);
            foreach (var f in refs)
            {
                object v; try { v = f.GetValue(o); } catch { continue; }
                if (v == null) continue;
                string cat = t.Name + "." + f.Name;
                if (Prim(v, out _, out _)) Note(v, cat);
                else if (Follow(v)) stack.Push((v, cat));
            }
            foreach (var f in structs) { object v; try { v = f.GetValue(o); } catch { continue; } PushStruct(v, t.Name + "." + f.Name, stack, 0, Note); }
        }
        long n = rows.Values.Sum(r => r.N), b = rows.Values.Sum(r => r.B), dn = rows.Values.Sum(r => r.DupN), db = rows.Values.Sum(r => r.DupB);
        Log.Info($"[spinecats] frame {frame}: '{name}': {objs:N0} objects, {n:N0} primitive arrays {b / 1048576.0:F2} MiB (32-bit), {dn:N0} with content seen before = {db / 1048576.0:F2} MiB ({sw.ElapsedMilliseconds} ms)");
        foreach (var (cat, r) in rows.OrderByDescending(x => x.Value.B).Take(Top))
            Log.Info($"[spinecats]   {cat,-48} n={r.N,8:N0} {r.B / 1048576.0,7:F2} MiB | seen before n={r.DupN,8:N0} {r.DupB / 1048576.0,7:F2} MiB (of them in this skeleton {r.DupSelfN:N0} {r.DupSelfB / 1048576.0:F2} MiB)");
    }

    static void DeepHash(int frame, UnityEngine.Object asset, object root)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);   // non-array objects: numbered in first-visit order
        ulong h = 14695981039346656037UL; long arrays = 0, objs = 0;
        var tr = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CATS_TRACE") == asset.name ? new System.Text.StringBuilder() : null;   // kit #7: the walk as text (Mac diagnostic)
        void Mix(ulong x) { h ^= x; h *= 1099511628211UL; tr?.Append(x.ToString("x")).Append(' '); }
        void MixS(string s) { if (s == null) { Mix(0xFFFF); return; } Mix((ulong)s.Length); foreach (var c in s) Mix(c); }
        var stack = new Stack<object>(); stack.Push(root);
        // depth-first in field order; an object seen before contributes its number only (identity structure is part of the content)
        void Val(object v)
        {
            if (v == null) { Mix(1); return; }
            if (Prim(v, out var b, out _)) { arrays++; Mix(2); MixS(v.GetType().Name); Mix((ulong)b.Length); foreach (var x in b) Mix(x); return; }
            switch (v)
            {
                case string s: Mix(3); MixS(s); return;
                case bool bo: Mix(bo ? 5UL : 4UL); return;
                case float f: Mix((ulong)BitConverter.SingleToInt32Bits(f)); return;
                case double d: Mix((ulong)BitConverter.DoubleToInt64Bits(d)); return;
                case int i: Mix((ulong)(uint)i); return;
                case long l: Mix((ulong)l); return;
                case Enum e: Mix(Convert.ToUInt64(e)); return;
                case UnityEngine.Object u: Mix(6); MixS(u.GetType().Name); MixS(u ? u.name : "<destroyed>"); return;   // asset refs by type + name (instance ids differ per run)
            }
            var t = v.GetType();
            if (t.IsPrimitive) { Mix((ulong)v.GetHashCode()); return; }
            if (v is Delegate || v is System.Reflection.MemberInfo || v is NObj) { Mix(7); MixS(t.FullName); return; }
            if (ids.TryGetValue(v, out var id)) { Mix(8); Mix((ulong)id); return; }
            ids[v] = ids.Count; objs++; tr?.Append('\n').Append(t.FullName).Append(": "); Mix(9); MixS(t.FullName);
            if (v is Array arr) { Mix((ulong)arr.Length); foreach (var e in arr) Val(e); return; }
            // hashed collections by their entries in enumeration order (= insertion order without removals): their bucket arrays and the
            // cached hash codes depend on string.GetHashCode, which CoreCLR randomizes per process
            if (v is System.Collections.IDictionary dict) { Mix(11); Mix((ulong)dict.Count); foreach (System.Collections.DictionaryEntry de in dict) { Val(de.Key); Val(de.Value); } return; }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>)) { Mix(12); foreach (var e in (System.Collections.IEnumerable)v) Val(e); return; }
            if (t.IsValueType && !HasRefs(t)) { foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).OrderBy(x => x.MetadataToken)) if (!(f.FieldType == typeof(int) && f.Name.Contains("hashCode", StringComparison.OrdinalIgnoreCase))) Val(f.GetValue(v)); return; }
            for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                foreach (var f in c.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly).OrderBy(x => x.MetadataToken))
                {
                    if (f.FieldType.IsPointer || f.FieldType.IsByRefLike) continue;
                    if (f.FieldType == typeof(int) && f.Name.Contains("hashCode", StringComparison.OrdinalIgnoreCase)) continue;   // e.g. Skin.SkinKey's cached string hash (per process)
                    if (f.Name.StartsWith("<Shepherd>", StringComparison.Ordinal)) continue;   // kit #7 L2: the rewrite's own bookkeeping (null once read), not skeleton content
                    object fv; try { fv = f.GetValue(v); } catch { Mix(10); continue; }
                    Val(fv);
                }
        }
        int forced = ForceLazySkins(root);   // kit #7 L2: every lazy skin read first (through the rewritten getter), so the hash covers what an eager read builds
        Val(root);
        if (tr != null) File.WriteAllText(Path.Combine(Program.LogDir, "spinecats-trace-" + asset.name + ".txt"), tr.ToString());
        Log.Info($"[spinecats] frame {frame}: '{asset.name}' deep content hash {h:x16} ({objs:N0} objects, {arrays:N0} array visits, {sw.ElapsedMilliseconds} ms){(forced > 0 ? $"; {forced} lazy skins read first (L2)" : "")}");
    }

    /// <summary>kit #7 L2: reads every lazy skin of a SkeletonData (Skin.Attachments' rewritten getter loads it); returns how many were lazy.</summary>
    static int ForceLazySkins(object data)
    {
        int n = 0;
        if (FieldValue(data, "skins") is not { } skins || FieldValue(skins, "Items") is not Array items || FieldValue(skins, "Count") is not int count) return 0;
        for (int i = 0; i < count; i++)
        {
            var skin = items.GetValue(i); if (skin == null) continue;
            var lz = skin.GetType().GetField("<Shepherd>lazy", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (lz == null || lz.GetValue(skin) == null) continue;
            n++; skin.GetType().GetProperty("Attachments")?.GetValue(skin);
        }
        return n;
    }

    static void RaceForce(int frame)
    {
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, In = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var skins = new List<object>();
        foreach (var (a, done) in Assets)
        {
            if (done == null || FieldValue(done, "skins") is not { } sk || FieldValue(sk, "Items") is not Array items || FieldValue(sk, "Count") is not int count) continue;
            for (int i = 0; i < count; i++) { var s = items.GetValue(i); if (s?.GetType().GetField("<Shepherd>lazy", In) is { } lz && lz.GetValue(s) != null) skins.Add(s); }
        }
        if (skins.Count == 0) { Log.Info($"[spinecats] race f{frame}: no lazy skin left (nothing to race)"); return; }
        var asm = skins[0].GetType().Assembly;
        var vaT = asm.GetType("Spine.VertexAttachment", true)!; var meshT = asm.GetType("Spine.MeshAttachment", true)!;
        var fNext = vaT.GetField("nextID", S)!; var lockObj = vaT.GetField("nextIdLock", S)!.GetValue(null)!; var fId = vaT.GetField("id", In)!;
        var ctor = meshT.GetConstructor(new[] { typeof(string) })!; var attachments = skins[0].GetType().GetProperty("Attachments")!;
        int before; lock (lockObj) before = (int)fNext.GetValue(null)!;
        var ids = new List<int>(); var stop = new int[1]; long handed = 0; Exception err = null;
        var builder = new Thread(() => { while (Volatile.Read(ref stop[0]) == 0 && ids.Count < 60000) { ids.Add((int)fId.GetValue(ctor.Invoke(new object[] { "<race>" }))!); Thread.SpinWait(50); } }) { IsBackground = true, Name = "spinecats-race-ids" };
        var interner = new Thread(() =>
        {
            var f = Shepherd.Hooks.SpineShare.Floats; if (f == null) return; var rnd = new Random(7);
            while (Volatile.Read(ref stop[0]) == 0 && handed < 400000) { var x = new float[1 + rnd.Next(8)]; for (int i = 0; i < x.Length; i++) x[i] = rnd.Next(12); f(x); handed++; }
        }) { IsBackground = true, Name = "spinecats-race-intern" };
        var workers = new Thread[2];
        for (int w = 0; w < workers.Length; w++)
        {
            int ww = w;
            workers[w] = new Thread(() => { try { for (int i = ww; i < skins.Count; i += 2) attachments.GetValue(skins[i]); } catch (Exception e) { err ??= e; } }) { IsBackground = true, Name = "spinecats-race-read" + w };
        }
        builder.Start(); interner.Start(); Thread.Sleep(2); foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
        Volatile.Write(ref stop[0], 1); builder.Join(); interner.Join();
        int after; lock (lockObj) { after = (int)fNext.GetValue(null)!; fNext.SetValue(null, before); }   // later ids as without the test
        int bad = 0; for (int k = 1; k < ids.Count; k++) if ((((ids[k] >> 11) - (ids[k - 1] >> 11)) & 0xFFFF) != 1) bad++;
        var srs = typeof(Runtime.SpineReadShare); bool broken = srs.GetField("_broken", S)?.GetValue(null) is true; var busyKept = srs.GetField("_busyKept", S)?.GetValue(null);
        // the L1 table after the race: a used slot must hold an array whose content hash is its key; _n must count the used slots
        string table = "";
        if (srs.GetField("_h", S)?.GetValue(null) is int[] th && srs.GetField("_a", S)?.GetValue(null) is object[] ta && srs.GetMethod("HashOf", S) is { } hashOf)
        {
            int used = 0, noArr = 0, wrongKey = 0, n = (int)srs.GetField("_n", S)!.GetValue(null)!;
            for (int i = 0; i < th.Length && i < ta.Length; i++)
            {
                if (th[i] == 0) continue; used++;
                if (ta[i] == null) { noArr++; continue; }
                if (((int)((uint)hashOf.Invoke(null, new[] { ta[i] })! | 1u) & ~2) != (th[i] & ~2)) wrongKey++;
            }
            table = $"; L1 table: {used:N0} used slots, {noArr} without an array, {wrongKey} whose array does not match its key, kept count {n:N0} ({n - used:+0;-0;0} vs the used slots)";
        }
        int still = skins.Count(s => s.GetType().GetField("<Shepherd>lazy", In)!.GetValue(s) != null);
        Log.Info($"[spinecats] race f{frame}: {skins.Count} lazy skins read on 2 threads ({still} still lazy) while {ids.Count:N0} MeshAttachments were built on a third and {handed:N0} arrays were interned on a fourth: " +
                 $"builder ids not consecutive {bad} (0 = no id handed out twice); counter moved {after - before} for {ids.Count} built ({(after - before == ids.Count ? "exact" : "WRONG: a late read put back other threads' ids")}); " +
                 $"L1 sharing {(broken ? "STOPPED (a table race)" : "still on")}{(busyKept != null ? $" ({busyKept} arrays kept unshared: the table was busy)" : "")}{table}{(err != null ? "; worker exception " + err.GetType().Name + ": " + err.Message : "")}");
    }

    /// <summary>End of run: the totals over every skeleton processed.</summary>
    public static void Summary()
    {
        if (!On || HashMode || Totals.Count == 0) return;
        Log.Info($"[spinecats] totals over {Assets.Count(a => a.done != null)} skeletons: {Totals.Values.Sum(r => r.N):N0} arrays {Totals.Values.Sum(r => r.B) / 1048576.0:F2} MiB, content seen before {Totals.Values.Sum(r => r.DupB) / 1048576.0:F2} MiB");
        foreach (var (cat, r) in Totals.OrderByDescending(x => x.Value.B).Take(Top))
            Log.Info($"[spinecats]   total {cat,-48} n={r.N,8:N0} {r.B / 1048576.0,7:F2} MiB | seen before n={r.DupN,8:N0} {r.DupB / 1048576.0,7:F2} MiB");
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
    static void PushStruct(object box, string via, Stack<(object, string)> stack, int depth, Action<object, string> note)
    {
        if (box == null || depth > 8) return;
        var (refs, structs) = Fields(box.GetType());
        foreach (var f in refs) { object v; try { v = f.GetValue(box); } catch { continue; } if (v == null) continue; if (Prim(v, out _, out _)) note(v, via + "." + f.Name); else if (Follow(v)) stack.Push((v, via + "." + f.Name)); }
        foreach (var f in structs) { object v; try { v = f.GetValue(box); } catch { continue; } PushStruct(v, via + "." + f.Name, stack, depth + 1, note); }
    }
    static bool Prim(object o, out ReadOnlySpan<byte> b, out int esz)
    {
        switch (o)
        {
            case float[] f: b = MemoryMarshal.AsBytes<float>(f); esz = 4; return true;
            case int[] i: b = MemoryMarshal.AsBytes<int>(i); esz = 4; return true;
            case short[] s: b = MemoryMarshal.AsBytes<short>(s); esz = 2; return true;
            case ushort[] u: b = MemoryMarshal.AsBytes<ushort>(u); esz = 2; return true;
            case byte[] y: b = y; esz = 1; return true;
            default: b = default; esz = 0; return false;
        }
    }
    sealed class KeyCmp : IEqualityComparer<object>
    {
        public new bool Equals(object x, object y) => x.GetType() == y.GetType() && Prim(x, out var a, out _) && Prim(y, out var b, out _) && a.SequenceEqual(b);
        public int GetHashCode(object o) { Prim(o, out var b, out _); var h = new HashCode(); h.Add(o.GetType()); h.AddBytes(b); return h.ToHashCode(); }
    }
    static bool Follow(object v) => v is not UnityEngine.Object && v is not NObj && v is not string && v is not Type && v is not Delegate && v is not MemberInfo && !v.GetType().IsPrimitive;
}
#else
[ProfileOnly]
public static class SpineCats
{
    public static readonly bool On = false;
    public static void Register(UnityEngine.Object asset) { }
    public static void After(int frame) { }
    public static void Summary() { }
}
#endif
