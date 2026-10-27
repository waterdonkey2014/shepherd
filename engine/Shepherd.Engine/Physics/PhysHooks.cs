using System.Reflection;
namespace Shepherd.Engine.Physics;

/// <summary>Kit #8 (k8-physics: k6-physics merged onto kit #7's main). The physics level hooks ([Icall(..., Phys = n)]) for a start whose
/// [Icall] list came from kit #5's scan cache (Runtime/IcallScanCache.cs: no type scan): the same methods IcallRegistry's full scan finds,
/// in the same order -- the physics icall classes and their nested types in metadata order (= Assembly.GetTypes' order), each type's static
/// methods in GetMethods' order. The cache file itself never holds a physics hook (IcallRegistry leaves them out of its list), so the file
/// does not depend on SHEPHERD_PHYS. Only called at level 1+ with a core. Every signature here is (object[]) -> object: no Unity
/// assembly is loaded by the reflection below (the classes' own rule, Physics2DIcalls' summary).</summary>
static class PhysHooks
{
    /// <summary>The classes that declare physics level hooks. A scanned start warns (IcallRegistry) if it finds one elsewhere.</summary>
    public static readonly Type[] Types = { typeof(Physics2DIcalls), typeof(Physics2DQueries), typeof(Physics2DPaths), typeof(Physics3DIcalls) };

    public static List<(MethodInfo m, int level, string key)> Scan()
    {
        var types = new List<Type>();
        void Add(Type t) { types.Add(t); foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) Add(n); }
        foreach (var t in Types) Add(t);
        types.Sort((x, y) => x.MetadataToken.CompareTo(y.MetadataToken));
        var list = new List<(MethodInfo m, int level, string key)>();
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!m.IsDefined(typeof(IcallAttribute), true)) continue;
                foreach (var a in m.GetCustomAttributes<IcallAttribute>())
                    if (a.Phys > 0) list.Add((m, a.Phys, a.Key));
            }
        return list;
    }

    /// <summary>A scanned start: physics hooks declared outside <see cref="Types"/> (a cached start would miss them), or null.</summary>
    public static string Outside(List<(MethodInfo m, int level, string key)> scanned)
    {
        var known = new HashSet<Type>();
        void Add(Type t) { known.Add(t); foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) Add(n); }
        foreach (var t in Types) Add(t);
        var bad = scanned.Where(h => !known.Contains(h.m.DeclaringType)).Select(h => h.m.DeclaringType.FullName + "." + h.m.Name).Distinct().ToList();
        return bad.Count == 0 ? null : "[phys] physics level hooks outside PhysHooks.Types (a cached start would not bind them): " + string.Join(", ", bad);
    }
}
