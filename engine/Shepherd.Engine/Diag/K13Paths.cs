#if !SHEPHERD_EBOOT   // a Mac planning tool, never in an eboot
using System.Reflection;
using System.Runtime.CompilerServices;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Diag;

/// <summary>Kit #13 walkc (test only, Mac): WHO still reaches the asset records the release kept (Runtime/K13Unload.cs). SHEPHERD_K13_PATHS=&lt;frame&gt;:
/// at the first release at or after that frame, a breadth-first walk of the managed object graph from (1) every live record that is not an
/// asset candidate (scene objects, DontDestroyOnLoad, engine-made records) -- its fields and its managed object -- then (2) the static fields of
/// the engine's own assemblies, never through the registries the release lets go of (ById, the block index, the live-record list, the Registry
/// lists, the tag sets). Each kept asset record is charged to the first holder the walk met: the nearest non-asset record's managed type and the
/// field path, or the engine static. Whatever neither reaches was reached through a game / Unity / BCL static (not walked here).</summary>
[ProfileOnly]
public static class K13Paths
{
    static readonly int From = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K13_PATHS"), out var f) ? f : -1;
    static bool _done;
    public static bool Want(int frame) => From >= 0 && !_done && frame >= From;

    const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;   // (every instance field)
    const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static readonly Dictionary<Type, FieldInfo[]> Fields = new();
    static FieldInfo[] FieldsOf(Type t)
    {
        if (Fields.TryGetValue(t, out var r)) return r;
        var l = new List<FieldInfo>();
        for (var b = t; b != null && b != typeof(object); b = b.BaseType)
            foreach (var fi in b.GetFields(Inst | BindingFlags.DeclaredOnly))
                if (!fi.FieldType.IsPointer && !fi.FieldType.IsPrimitive && !fi.FieldType.IsEnum && fi.FieldType != typeof(IntPtr) && fi.FieldType != typeof(string)) l.Add(fi);
        return Fields[t] = l.ToArray();
    }
    static bool Leaf(object o) => o is string || o is Type || o is MemberInfo || o is Assembly || o is Module || o is Pointer;

    public static void Run(List<NObj> kept, HashSet<NObj> candidates)
    {
        _done = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var parent = new Dictionary<object, (object from, string via)>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        // the registries the release lets go of: never walked
        foreach (var o in Blocked()) if (o != null) parent[o] = (null, "(blocked registry)");
        var keptSet = new HashSet<object>(kept, ReferenceEqualityComparer.Instance);
        var label = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);   // root object -> its label
        void Root(object o, string l) { if (o == null || parent.ContainsKey(o)) return; parent[o] = (null, l); label[o] = l; queue.Enqueue(o); }
        void Walk()
        {
            while (queue.Count > 0)
            {
                var o = queue.Dequeue();
                if (Leaf(o)) continue;
                var t = o.GetType();
                if (t.IsArray)
                {
                    if (t.GetElementType() is { } et && (et.IsPrimitive || et.IsEnum || et == typeof(string) || et.IsPointer)) continue;
                    var a = (Array)o; if (a.Rank != 1) continue;
                    for (int i = 0; i < a.Length; i++) { object x; try { x = a.GetValue(i); } catch { continue; } Edge(o, x, "[]"); }
                    continue;
                }
                foreach (var fi in FieldsOf(t)) { object x; try { x = fi.GetValue(o); } catch { continue; } Edge(o, x, fi.Name); }
                if (o is UnityEngine.Object uo && Objects.Get(uo) is { } rec) Edge(o, rec, "(its record: the release's ephemeron)");   // managed object -> record
            }
        }
        void Edge(object from, object x, string via)
        {
            if (x == null || Leaf(x)) return;
            if (x.GetType().IsValueType)
            {   // a struct: walk its reference fields here (one level of boxing per hop)
                foreach (var fi in FieldsOf(x.GetType())) { object y; try { y = fi.GetValue(x); } catch { continue; } if (y != null && !y.GetType().IsValueType) Edge(from, y, via + "." + fi.Name); }
                return;
            }
            if (parent.ContainsKey(x)) return;
            parent[x] = (from, via); queue.Enqueue(x);
        }
        // (1) every live record that is not an asset candidate, in ById's order
        foreach (var n in Objects.ById.Values)
        {
            if (n.Destroyed || candidates.Contains(n)) continue;
            string l = $"record {n.GetType().Name} '{n.Name}' ({n.Managed?.GetType().Name ?? "no managed"}; scene {(n is NGameObject g ? g.Scene : n is NComponent c && c.Go != null ? c.Go.Scene : 0)})";
            Root(n, l); if (n.Managed is { } m) Root(m, l + " .Managed");
        }
        Walk();
        int phase1 = parent.Count;
        // (2) the engine's own statics
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var an = asm.GetName().Name ?? "";
            if (!an.StartsWith("Shepherd")) continue;
            Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray()!; }
            foreach (var t in ts)
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var fi in t.GetFields(Stat))
                {
                    if (fi.FieldType.IsPrimitive || fi.FieldType.IsEnum || fi.FieldType == typeof(string) || fi.IsLiteral) continue;
                    object x; try { x = fi.GetValue(null); } catch { continue; }
                    if (x == null) continue;
                    if (x.GetType().IsValueType) { foreach (var sf in FieldsOf(x.GetType())) { object y; try { y = sf.GetValue(x); } catch { continue; } Root(y, $"static {t.Name}.{fi.Name}.{sf.Name}"); } }
                    else Root(x, $"static {t.Name}.{fi.Name}");
                }
            }
        }
        Walk();
        int phase2 = parent.Count;
        // (3) every other static: game, Unity, BCL (the census's rule: a .cctor the game never ran is not run -- such a type's statics are read raw)
        var rfh = typeof(RuntimeFieldHandle).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).FirstOrDefault(mm => mm.Name == "GetValue" && mm.GetParameters().Length == 5);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var an = asm.GetName().Name ?? "";
            if (asm.IsDynamic || an.StartsWith("Shepherd")) continue;
            string grp = an.StartsWith("UnityEngine") || an.StartsWith("Unity.") ? "unity" : an.StartsWith("System") || an.StartsWith("Microsoft") || an == "netstandard" || an == "mscorlib" ? "bcl" : "game";
            Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray()!; }
            foreach (var t in ts)
            {
                if (t.ContainsGenericParameters) continue;
                FieldInfo[] fs; try { fs = t.GetFields(Stat); } catch { continue; }
                if (fs.Length == 0) continue;
                bool raw = t.TypeInitializer != null;
                if (raw && t.IsConstructedGenericType) continue;
                foreach (var fi in fs)
                {
                    if (fi.IsLiteral || fi.FieldType.IsPointer || fi.FieldType.IsByRefLike || fi.FieldType.IsValueType || fi.FieldType == typeof(string)) continue;
                    object x; try { x = raw && rfh != null ? rfh.Invoke(null, new object[] { fi, null, fi.FieldType, fi.DeclaringType, true }) : fi.GetValue(null); } catch { continue; }
                    Root(x, $"{grp} static {t.FullName}.{fi.Name}");
                }
            }
        }
        Walk();
        // charge every kept record to its root
        var byRoot = new Dictionary<string, int>(); int unreached = 0; var samples = new List<string>();
        foreach (var n in kept)
        {
            if (!parent.TryGetValue(n, out _)) { unreached++; continue; }
            object cur = n; string rootLabel = null; var path = new List<string>(); int hops = 0;
            while (parent.TryGetValue(cur, out var p) && hops < 4000)
            {
                if (p.from == null) { rootLabel = p.via; break; }
                if (path.Count < 12) path.Add($"{p.via}<-{Short(p.from)}");
                cur = p.from; hops++;
            }
            rootLabel ??= "(cycle?)";
            var key = Trim(rootLabel);
            byRoot[key] = (byRoot.TryGetValue(key, out var c) ? c : 0) + 1;
            if (samples.Count < 60 && (n is NGameObject || n is NTexture || n is NMaterial || n is NSprite) && byRoot[key] <= 2)
                samples.Add($"{n.GetType().Name} '{n.Name}' <- {string.Join(" <- ", path)} <- ROOT {rootLabel}");
        }
        Log.Info($"[k13paths] {kept.Count} kept asset records; walk: {phase1} objects from the non-asset records, {phase2 - phase1} more from the engine's statics, {parent.Count - phase2} more from the game / Unity / BCL statics, {sw.ElapsedMilliseconds} ms; {unreached} reached by none (a stack / GC-handle root)");
        foreach (var kv in byRoot.OrderByDescending(x => x.Value).Take(40)) Log.Info($"[k13paths]   {kv.Value,7}  {kv.Key}");
        foreach (var s in samples) Log.Info($"[k13paths] sample: {s}");
    }
    // ---- SHEPHERD_K13_DEAD=<frame> (test only): destroyed records the engine's statics still reach (a leak: Unity frees a destroyed object) ----
    static readonly int DeadAt = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K13_DEAD"), out var da) ? da : -1;
    public static void DeadFrame(int frame) { if (frame == DeadAt) try { Dead(frame); } catch (Exception e) { Log.Info($"[k13dead] failed: {e.GetType().Name} {e.Message}"); } }
    static void Dead(int frame)
    {
        var parent = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var q = new Queue<(object o, string root)>();
        foreach (var o in Blocked()) if (o != null) parent[o] = "(blocked)";
        // every live record is a root of its own (what a live object reaches is live): walked first, so a destroyed record is charged to an engine
        // static only when no live record reaches it
        foreach (var n in Objects.ById.Values) if (!n.Destroyed && parent.TryAdd(n, "live")) { q.Enqueue((n, "live")); if (n.Managed is { } m && parent.TryAdd(m, "live")) q.Enqueue((m, "live")); }
        void Drain()
        {
            while (q.Count > 0)
            {
                var (o, root) = q.Dequeue();
                if (Leaf(o)) continue;
                var t = o.GetType();
                IEnumerable<object> kids;
                if (t.IsArray) { var a = (Array)o; if (a.Rank != 1 || t.GetElementType() is { } et && (et.IsPrimitive || et.IsEnum || et == typeof(string))) continue; var l = new List<object>(); for (int i = 0; i < a.Length; i++) { object x; try { x = a.GetValue(i); } catch { continue; } l.Add(x); } kids = l; }
                else { var l = new List<object>(); foreach (var fi in FieldsOf(t)) { object x; try { x = fi.GetValue(o); } catch { continue; } l.Add(x); } kids = l; }
                foreach (var x0 in kids)
                {
                    if (x0 == null || Leaf(x0)) continue;
                    var xs = x0.GetType().IsValueType ? FieldsOf(x0.GetType()).Select(f => { try { return f.GetValue(x0); } catch { return null; } }).Where(y => y != null && !y.GetType().IsValueType && !Leaf(y)) : new[] { x0 };
                    foreach (var x in xs) if (parent.TryAdd(x, root)) q.Enqueue((x, root));
                }
                if (o is UnityEngine.Object uo && Objects.Get(uo) is { } rec && parent.TryAdd(rec, root)) q.Enqueue((rec, root));
            }
        }
        Drain();
        int live = parent.Count;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!(asm.GetName().Name ?? "").StartsWith("Shepherd")) continue;
            Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray()!; }
            foreach (var t in ts)
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var fi in t.GetFields(Stat))
                {
                    if (fi.IsLiteral || fi.FieldType.IsPrimitive || fi.FieldType.IsEnum || fi.FieldType == typeof(string) || fi.FieldType.IsValueType) continue;
                    object x; try { x = fi.GetValue(null); } catch { continue; }
                    string r = $"static {t.Name}.{fi.Name}";
                    if (x != null && parent.TryAdd(x, r)) { q.Enqueue((x, r)); Drain(); }
                }
            }
        }
        var dead = new Dictionary<string, (int recs, int objs)>();
        foreach (var kv in parent)
        {
            if (kv.Value == "live" || kv.Value == "(blocked)") continue;
            (int recs, int objs) e = dead.TryGetValue(kv.Value, out var v) ? v : (0, 0);
            dead[kv.Value] = (e.recs + (kv.Key is NObj { Destroyed: true } ? 1 : 0), e.objs + 1);
        }
        Log.Info($"[k13dead] f{frame}: {live} objects reached from the live records; reached only through an engine static (by static: destroyed records / all objects):");
        foreach (var kv in dead.Where(x => x.Value.recs > 0).OrderByDescending(x => x.Value.objs).Take(30)) Log.Info($"[k13dead]   {kv.Value.recs,7} destroyed records, {kv.Value.objs,8} objects  {kv.Key}");
    }
    // ---- SHEPHERD_K13_DEADSIZE=<frame> (test only): how much stays reachable ONLY through destroyed records' own links (Unity frees a destroyed
    // object's native side at once; here a destroyed record keeps its Components / Children / Parent / property bag / mesh data while anything
    // reaches it). Two walks from every root (live records, every static of every assembly): with and without following a destroyed record's
    // fields (its managed object is still followed: a C# reference to a destroyed object keeps the managed object in Unity too). ----
    static readonly int DeadSizeAt = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K13_DEADSIZE"), out var dz) ? dz : -1;
    public static void DeadSizeFrame(int frame) { if (frame == DeadSizeAt) try { DeadSize(frame); } catch (Exception e) { Log.Info($"[k13deadsize] failed: {e.GetType().Name} {e.Message}"); } }
    static Func<object, nuint> _raw;
    static long Size64(object o) { _raw ??= (Func<object, nuint>)Delegate.CreateDelegate(typeof(Func<object, nuint>), typeof(RuntimeHelpers).GetMethod("GetRawObjectDataSize", BindingFlags.Static | BindingFlags.NonPublic)!); return Math.Max(24L, ((long)_raw(o) + 16 + 7) & ~7L); }
    static (long n, long b, long deadRecs) Reach(bool followDead, List<object> roots)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance); var q = new Queue<object>(); long n = 0, b = 0, dr = 0;
        foreach (var r in roots) if (r != null && seen.Add(r)) q.Enqueue(r);
        while (q.Count > 0)
        {
            var o = q.Dequeue(); n++; b += Size64(o);
            if (o is NObj { Destroyed: true } dn) { dr++; if (!followDead) { if (dn.Managed is { } dm && seen.Add(dm)) q.Enqueue(dm); continue; } }
            if (Leaf(o)) continue;
            var t = o.GetType();
            if (t.IsArray) { var a = (Array)o; if (a.Rank != 1 || t.GetElementType() is { } et && (et.IsPrimitive || et.IsEnum || et == typeof(string) || et.IsPointer)) continue; for (int i = 0; i < a.Length; i++) { object x; try { x = a.GetValue(i); } catch { continue; } Push(x); } continue; }
            foreach (var fi in FieldsOf(t)) { object x; try { x = fi.GetValue(o); } catch { continue; } Push(x); }
            if (o is UnityEngine.Object uo && Objects.Get(uo) is { } rec && seen.Add(rec)) q.Enqueue(rec);
        }
        return (n, b, dr);
        void Push(object x)
        {
            if (x == null || Leaf(x)) return;
            if (x.GetType().IsValueType) { foreach (var fi in FieldsOf(x.GetType())) { object y; try { y = fi.GetValue(x); } catch { continue; } if (y != null && !y.GetType().IsValueType && !Leaf(y) && seen.Add(y)) q.Enqueue(y); } return; }
            if (seen.Add(x)) q.Enqueue(x);
        }
    }
    static void DeadSize(int frame)
    {
        var roots = new List<object>();
        foreach (var n in Objects.ById.Values) if (!n.Destroyed) { roots.Add(n); roots.Add(n.Managed); }
        var rfh = typeof(RuntimeFieldHandle).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).FirstOrDefault(mm => mm.Name == "GetValue" && mm.GetParameters().Length == 5);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray()!; }
            foreach (var t in ts)
            {
                if (t.ContainsGenericParameters || t == typeof(K13Paths)) continue;
                FieldInfo[] fs; try { fs = t.GetFields(Stat); } catch { continue; }
                bool raw = t.TypeInitializer != null; if (raw && t.IsConstructedGenericType) continue;
                foreach (var fi in fs)
                {
                    if (fi.IsLiteral || fi.FieldType.IsPointer || fi.FieldType.IsByRefLike || fi.FieldType.IsValueType || fi.FieldType == typeof(string)) continue;
                    object x; try { x = raw && rfh != null ? rfh.Invoke(null, new object[] { fi, null, fi.FieldType, fi.DeclaringType, true }) : fi.GetValue(null); } catch { continue; }
                    if (x != null) roots.Add(x);
                }
            }
        }
        var all = Reach(true, roots); var cut = Reach(false, roots);
        Log.Info($"[k13deadsize] f{frame}: reachable from every root {all.n} objects ~{all.b / 1048576.0:F2} MiB (64-bit sizes), {all.deadRecs} destroyed records among them; " +
                 $"without following destroyed records' own links {cut.n} objects ~{cut.b / 1048576.0:F2} MiB ({cut.deadRecs} destroyed records reached): " +
                 $"{all.n - cut.n} objects ~{(all.b - cut.b) / 1048576.0:F2} MiB (64-bit; ~0.75x in 32-bit Mono) are held only through destroyed records' links");
    }
    static string Short(object o) => o is NObj n ? $"{n.GetType().Name}('{n.Name}')" : o.GetType().Name;
    static string Trim(string l) { int q = l.IndexOf('\''); if (l.StartsWith("record ") && q > 0) { int q2 = l.IndexOf('\'', q + 1); return q2 > q ? l[..q] + l[(q2 + 1)..] : l; } return l; }

    static IEnumerable<object> Blocked()
    {
        yield return Objects.ById;
        foreach (var name in new[] { "_byIdx", "ByPtr", "_rebound" }) yield return typeof(Objects).GetField(name, Stat)?.GetValue(null);
        foreach (var name in new[] { "_liveOrder", "Live" }) yield return typeof(Assets.Loader).GetField(name, Stat)?.GetValue(null);
        yield return typeof(Registry).GetField("Log", Stat)?.GetValue(null);
        foreach (var fi in typeof(Registry).GetFields(Stat))
            if (fi.GetValue(null) is Registry r) yield return typeof(Registry).GetField("_items", Inst)?.GetValue(r);
            else if (fi.GetValue(null) is Registry[] ra) { yield return ra; foreach (var r2 in ra) if (r2 != null) yield return typeof(Registry).GetField("_items", Inst)?.GetValue(r2); }
        if (typeof(Registry).GetField("_typed", Stat)?.GetValue(null) is System.Collections.IDictionary td)
            foreach (var v in td.Values) yield return typeof(Registry).GetField("_items", Inst)?.GetValue(v);
        yield return typeof(Runtime.TagIndex).GetField("ByTag", Stat)?.GetValue(null);
    }
}
#endif
