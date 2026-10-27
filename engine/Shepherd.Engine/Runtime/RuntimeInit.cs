using System.Reflection;
using System.Text.Json;
namespace Shepherd.Engine.Runtime;

/// <summary>[RuntimeInitializeOnLoadMethod] — the player reads Data/RuntimeInitializeOnLoads.json and calls each static
/// method at its phase: SubsystemRegistration(4) → AfterAssembliesLoaded(2) → BeforeSplashScreen(3) → BeforeSceneLoad(1)
/// → [first scene] → AfterSceneLoad(0).</summary>
public static class RuntimeInit
{
    sealed record Entry(string Asm, string Ns, string Cls, string Method, int Load);
    static List<Entry> _all;
    static List<Entry> All()
    {
        if (_all != null) return _all;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.GameData, "RuntimeInitializeOnLoads.json")));
        _all = doc.RootElement.GetProperty("root").EnumerateArray().Select(e => new Entry(
            e.GetProperty("assemblyName").GetString(), e.TryGetProperty("nameSpace", out var ns) ? ns.GetString() : "", e.GetProperty("className").GetString(),
            e.GetProperty("methodName").GetString(), e.GetProperty("loadTypes").GetInt32())).ToList();
        return _all;
    }
    public static void Run(params int[] phases)
    {
        foreach (var phase in phases)
            foreach (var e in All().Where(x => x.Load == phase))
            {
                var full = string.IsNullOrEmpty(e.Ns) ? e.Cls : e.Ns + "." + e.Cls;
                Type t = null; try { t = Assembly.Load(e.Asm).GetType(full); } catch { }
                var m = t?.GetMethod(e.Method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null) { Log.Warn($"[RuntimeInit] missing {e.Asm}::{full}.{e.Method}"); continue; }
                if (Profile.Vita) VitaMemory.BeforeInitMethod(t, m);   // stage 1: seeds for all-assembly type scans
                try { m.Invoke(null, null); Log.Write("INI", $"[{phase}] {full}.{e.Method}"); }
                catch (Exception ex) { Log.Exception(ex, $"[RuntimeInit {phase}] {full}.{e.Method}"); }
            }
    }
    public const int SubsystemRegistration = 4, AfterAssembliesLoaded = 2, BeforeSplashScreen = 3, BeforeSceneLoad = 1, AfterSceneLoad = 0;
}
