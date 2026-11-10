#if !SHEPHERD_EBOOT   // a Mac planning tool, never in an eboot
using System.Reflection;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Diag;

/// <summary>Kit #13 walkc (test only, Mac): how much of the live records' memory is ENGINE-side data that a shelf could hold elsewhere and give
/// back on the next touch (every touch of it goes through the engine). SHEPHERD_K13_BAGS=&lt;frame&gt;: one report at that frame --
/// (1) the asset-space records the release keeps (Runtime/K13Unload.cs's candidates that are still reached) and the scene records, each with its
/// property bags (32-bit projection: the shaped array + every value it alone holds), UI mesh arrays (NCanvasRenderer), mesh arrays (NMesh);
/// (2) for the intro: IntroManager.DeathScene / GameScene (the two halves the game switches), the same sums over each half's records, and the
/// half's GameObjects / active ones. 32-bit sizes follow MemReport's rules roughly (object header 8, array header 16, refs 4, boxed values 16).</summary>
[ProfileOnly]
public static class K13Bags
{
    static readonly int At = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K13_BAGS"), out var f) ? f : -1;
    public static void Frame(int frame) { if (frame == At) try { Report(frame); } catch (Exception e) { Log.Info($"[k13bags] failed: {e.GetType().Name} {e.Message}"); } }

    sealed class Sum { public long Recs, Bags, Slots, BagB, UiB, MeshB, Gos, ActiveGos; }
    static long Arr(Array a) { if (a == null) return 0; var et = a.GetType().GetElementType(); int es = et == typeof(byte) || et == typeof(bool) ? 1 : et == typeof(short) || et == typeof(char) || et == typeof(ushort) ? 2 : et == typeof(double) || et == typeof(long) ? 8 : et.IsValueType && !et.IsPrimitive ? System.Runtime.InteropServices.Marshal.SizeOf(et) : 4; return 16 + (long)es * a.Length; }
    static long Val(object v, HashSet<object> seen)
    {
        if (v == null || v is UnityEngine.Object || !seen.Add(v)) return 0;
        switch (v)
        {
            case string s: return 20 + 2L * s.Length;
            case Array a: { long b = Arr(a); if (a is object[] oa) foreach (var x in oa) b += Val(x, seen); return b; }
            case System.Collections.IList l: { long b = 24 + 16 + 4L * l.Count; foreach (var x in l) b += Val(x, seen); return b; }
            case Assets.LazyRef: return 16;
            default: return v.GetType().IsValueType ? 16 : 24;
        }
    }
    static void Add(Sum s, NObj n, HashSet<object> seen)
    {
        s.Recs++;
        if (n.Props != null) { s.Bags++; s.Slots += PropBag.Count(n.Props); s.BagB += n.Props is object[] oa ? 16 + 4L * oa.Length : 64 + 20L * PropBag.Count(n.Props); foreach (var kv in PropBag.Entries(n.Props)) s.BagB += Val(kv.Value, seen); }
        if (n is NCanvasRenderer c) s.UiB += Arr(c.Pos) + Arr(c.Col) + Arr(c.Uv0) + Arr(c.Idx) + Arr(c.Tan);
        if (n is NMesh m) { s.MeshB += Arr(m.Pos) + Arr(m.Col) + Arr(m.Uv0) + Arr(m.Normals) + Arr(m.Tan); if (m.Sub != null) foreach (var x in m.Sub) s.MeshB += Arr(x); }
        if (n is NGameObject g) { s.Gos++; if (g.ActiveInHierarchy) s.ActiveGos++; }
    }
    static string Line(string what, Sum s) => $"{what}: {s.Recs} records ({s.Gos} GameObjects, {s.ActiveGos} active in hierarchy) | property bags {s.Bags} ({s.Slots} values) ~{s.BagB / 1048576.0:F2} MiB | UI meshes ~{s.UiB / 1048576.0:F2} MiB | meshes ~{s.MeshB / 1048576.0:F2} MiB (32-bit, values held by one bag counted once)";

    static void Report(int frame)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Sum asset = new(), scene = new(), other = new();
        foreach (var n in Objects.ById.Values)
        {
            if (n.Destroyed) continue;
            int sc = n is NGameObject g ? g.Scene : n is NComponent c && c.Go != null ? c.Go.Scene : -1;
            Add(sc == 0 ? asset : sc > 0 ? scene : other, n, seen);
        }
        var top = new List<(long b, string what)>();
        foreach (var n in Objects.ById.Values)
        {
            if (n.Destroyed || n.Props == null) continue;
            long b = 0; var sn = new HashSet<object>(ReferenceEqualityComparer.Instance); foreach (var kv in PropBag.Entries(n.Props)) b += Val(kv.Value, sn);
            if (b > 256 * 1024) top.Add((b, $"{n.Managed?.GetType().Name ?? n.GetType().Name} '{n.Name}' keys [{string.Join(",", PropBag.Entries(n.Props).Select(kv => kv.Key + (kv.Value is Array a ? $"({a.GetType().GetElementType()?.Name}[{a.Length}])" : "")).Take(6))}]"));
        }
        foreach (var x in top.OrderByDescending(x => x.b).Take(12)) Log.Info($"[k13bags]   big bag ~{x.b / 1048576.0:F2} MiB {x.what}");
        Log.Info($"[k13bags] f{frame} {Line("asset space (prefab templates, kept by what reaches them)", asset)}");
        Log.Info($"[k13bags] f{frame} {Line("in a scene", scene)}");
        Log.Info($"[k13bags] f{frame} {Line("not GameObject-bound (textures, sprites, materials, data assets ...)", other)}");
        // the intro's two halves
        foreach (var n in Objects.ById.Values)
        {
            if (n.Destroyed || n is not NMono m || m.Managed?.GetType().Name != "IntroManager") continue;
            foreach (var half in new[] { "DeathScene", "GameScene" })
            {
                var fi = m.Managed.GetType().GetField(half, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fv = fi?.GetValue(m.Managed);
                System.Collections.IEnumerable list = fv as System.Collections.IEnumerable ?? (fv is UnityEngine.Object one ? new[] { one } : null);
                if (list == null) { Log.Info($"[k13bags] IntroManager.{half}: not found ({fi?.FieldType.Name ?? "no field"}; fields {string.Join(",", m.Managed.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(x => x.Name).Take(30))})"); continue; }
                var s = new Sum(); var seen2 = new HashSet<object>(ReferenceEqualityComparer.Instance); int roots = 0, active = 0;
                foreach (var o in list)
                {
                    if (Objects.Get(o) is not NGameObject root) continue;
                    roots++; if (root.ActiveSelf) active++;
                    var stack = new Stack<NTransform>(); stack.Push(root.Transform);
                    while (stack.Count > 0)
                    {
                        var t = stack.Pop(); if (t?.Go == null) continue;
                        foreach (var comp in t.Go.Components) Add(s, comp, seen2);
                        Add(s, t.Go, seen2);
                        foreach (var k in t.Children) stack.Push(k);
                    }
                }
                Log.Info($"[k13bags] f{frame} IntroManager.{half} ({roots} roots, {active} active): {Line("its subtree", s)}");
            }
            break;
        }
    }
}
#endif
