using System.Reflection;
namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 section 1.4): the AutoProps guard and its assert. AutoProps.Register backs every still
/// unimplemented instance get_X / set_X hook with a property bag -- for a physics getter that would be a silent wrong answer
/// (Rigidbody2D.velocity read back from a bag). Today the two physics modules load after the registration, so the guard skips nothing;
/// it keeps it that way if either ever loads earlier. The assert (physcheck, and every SHEPHERD_CHECK_PHYS run's exit) re-checks the
/// table once the modules are loaded: no icall of UnityEngine.Physics2DModule / UnityEngine.PhysicsModule may be an AutoProps closure.</summary>
[ProfileOnly]   // module names and log text only (Assets/Diet.cs)
public static class PhysGuard
{
    /// <summary>Hooks the guard kept from AutoProps (expected: 0 -- the modules are not loaded at registration).</summary>
    public static int Skipped;
    static readonly Dictionary<Assembly, bool> _isPhys = new();
    public static bool IsPhysicsModule(Type t)
    {
        if (!PhysConfig.On || t == null) return false;   // SHEPHERD_PHYS=none: AutoProps exactly as kit #4
        var a = t.Assembly;
        lock (_isPhys)
        {
            if (!_isPhys.TryGetValue(a, out var v)) _isPhys[a] = v = a.GetName().Name is "UnityEngine.Physics2DModule" or "UnityEngine.PhysicsModule";
            return v;
        }
    }

    /// <summary>The assert: (rows of the loaded physics modules, how many an [Icall] serves, how many are bag-backed). Bag-backed must be 0.</summary>
    public static (int rows, int icalls, int bagBacked, string first) Check()
    {
        var names = IcallRegistry.Names; var table = Shepherd.Hooks.Icalls.Table;
        var types = new Dictionary<string, Type>();
        int rows = 0, icalls = 0, bag = 0; string first = null;
        for (int i = 0; i < names.Length && i < table.Length; i++)
        {
            var k = names[i]; int c = k.IndexOf("::", StringComparison.Ordinal); if (c <= 0) continue;
            var tn = k[..c];
            if (!types.TryGetValue(tn, out var t)) types[tn] = t = FindLoaded(tn.Replace('/', '+'));
            if (t == null || !(t.Assembly.GetName().Name is "UnityEngine.Physics2DModule" or "UnityEngine.PhysicsModule")) continue;
            rows++;
            var d = table[i]; if (d == null) continue;
            if (d.Method.IsDefined(typeof(IcallAttribute), true)) { icalls++; continue; }
            for (var dt = d.Method.DeclaringType; dt != null; dt = dt.DeclaringType)
                if (dt == typeof(Impl.AutoProps)) { bag++; first ??= k; break; }
        }
        return (rows, icalls, bag, first);
    }
    /// <summary>The type among the loaded assemblies only (never loads one).</summary>
    static Type FindLoaded(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = asm.GetName().Name;
            if (n is not ("UnityEngine.Physics2DModule" or "UnityEngine.PhysicsModule")) continue;
            var t = asm.GetType(name); if (t != null) return t;
        }
        return null;
    }
    public static string Describe()
    {
        var (rows, icalls, bag, first) = Check();
        return $"[phys] AutoProps guard: {Skipped} physics hooks kept from the bags at registration; loaded physics modules' icall rows {rows}, " +
               $"[Icall]-served {icalls}, bag-backed {bag}{(bag > 0 ? $" -- FAIL phys autoprops: e.g. {first}" : " (assert OK)")}";
    }
}
