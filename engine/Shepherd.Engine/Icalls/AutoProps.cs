using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using Shepherd.Hooks;
using UnityEngine;
namespace Shepherd.Engine.Impl;

/// <summary>Fills every still-unimplemented INSTANCE get_X/set_X(_Injected) hook with a per-object property bag.
/// Scene data seeds the bag (m_Color -> "color"); runtime code reads back what it wrote. Real behaviour is added by
/// explicit [Icall]s, which always win.</summary>
static class AutoProps
{
    static readonly Dictionary<string, object> Defaults = new()
    {
        ["color"] = Color.white, ["enabled"] = true, ["fieldOfView"] = 60f, ["orthographicSize"] = 5f, ["nearClipPlane"] = 0.3f,
        ["farClipPlane"] = 1000f, ["depth"] = 0f, ["aspect"] = 16f / 9f, ["alpha"] = 1f, ["scaleFactor"] = 1f, ["referencePixelsPerUnit"] = 100f,
        ["pixelsPerUnit"] = 100f, ["sortingLayerName"] = "Default", ["cullingMask"] = -1, ["volume"] = 1f, ["pitch"] = 1f, ["speed"] = 1f,
        ["rect"] = new Rect(0, 0, 1, 1), ["intensity"] = 1f, ["range"] = 10f, ["spotAngle"] = 30f, ["isActiveAndEnabled"] = true,
        ["lineSpacing"] = 1f, ["interactable"] = true, ["blocksRaycasts"] = true, ["renderMode"] = RenderMode.ScreenSpaceOverlay, ["fontSize"] = 14, ["planeDistance"] = 100f, ["normalizedSize"] = Vector2.one, ["size"] = Vector2.one,
    };
    public static int Registered;
    /// <summary>Every property-bag key some auto getter reads (any declaring type). The V3 diet never drops these from a bag.</summary>
    public static readonly HashSet<string> GetterKeys = new(StringComparer.Ordinal);

    public static void Register(IcallRow[] rows)
    {
        var typeCache = new Dictionary<string, Type>();
        // kit #4 boot (B-1): each type's declared instance methods, by name, fetched once per type (it was one GetMethods call per row);
        // the first method of a name wins, as FirstOrDefault over the same array did
        var methodCache = new Dictionary<Type, Dictionary<string, MethodInfo>>();
        for (int id = 0; id < rows.Length; id++)
        {
            if (Icalls.Table[id] != null) continue;
            ref readonly var r = ref rows[id];
            if (r.Static) continue;
            var name = r.Name; int np = r.ParamCount;
            bool injected = name.EndsWith("_Injected", StringComparison.Ordinal);   // (ordinal: see InertStubs)
            string bare = injected ? name[..^9] : name;
            bool get = bare.StartsWith("get_", StringComparison.Ordinal), set = bare.StartsWith("set_", StringComparison.Ordinal);
            if (!get && !set) continue;
            var key = bare[4..];
            var tname = r.Type;
            if (!typeCache.TryGetValue(tname, out var declType)) typeCache[tname] = declType = Core.TypeByName(tname.Replace('/', '+'));
            if (declType == null) continue;
            // physics stage 0 (plan v2 1.4, the AutoProps guard): the physics modules' getters / setters are never bag-backed -- explicit
            // [Icall]s serve them (Stage 1 on), or they stay loud-missing. Today those modules load after this registration, so the guard
            // skips nothing (PhysGuard.Skipped stays 0, which the boot assert checks); without it ~690 getters would become inert bags if
            // either module ever loaded before IcallRegistry.Init.
            if (Physics.PhysGuard.IsPhysicsModule(declType)) { Physics.PhysGuard.Skipped++; continue; }
            if (!methodCache.TryGetValue(declType, out var byName))
            {
                methodCache[declType] = byName = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
                foreach (var m in declType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) byName.TryAdd(m.Name, m);
            }
            if (!byName.TryGetValue(name, out var mi)) continue;
            if (get && (np == 0 || injected && np == 1)) GetterKeys.Add(key);
            if (get && !injected && np == 0)
            {
                var rt = mi.ReturnType;
                Icalls.Table[id] = a => Conv.To(Objects.Get(a[0])?.Get(key) ?? Default(key, rt), rt);
            }
            else if (get && injected && np == 1)
            {
                var rt = mi.GetParameters()[0].ParameterType.GetElementType();
                Icalls.Table[id] = a => { a[1] = Conv.To(Objects.Get(a[0])?.Get(key) ?? Default(key, rt), rt); return null; };
            }
            else if (set && np == 1)
                Icalls.Table[id] = a => { Objects.Get(a[0])?.Set(key, a[1]); return null; };
            else continue;
            Registered++;
        }
        Log.Info($"auto-props: {Registered} get/set hooks backed by property bags");
    }
    static object Default(string key, Type t) => Defaults.TryGetValue(key, out var d) && t.IsInstanceOfType(d) ? d : null;
    /// <summary>Kit #9 fidelity: exactly what an auto getter of <paramref name="key"/> returns for <paramref name="self"/> (the explicit
    /// getters that replace one keep this as their switch-off path).</summary>
    public static object Read(object self, string key, Type t) => Conv.To(Objects.Get(self)?.Get(key) ?? Default(key, t), t);
}

/// <summary>Whole hook families that are safe as no-ops (profiling, analytics, command buffers…). Returns a harmless
/// default for the declared return type; pointer-returning "create" hooks get a non-null dummy handle.</summary>
static class InertStubs
{
    static readonly string[] Prefixes =
    {
        "Unity.Profiling.", "UnityEngine.Profiling.", "UnityEngine.Rendering.CommandBuffer::", "UnityEngine.Rendering.GraphicsSettings::",
        "UnityEngine.Analytics.", "UnityEngine.CrashReportHandler.", "UnityEngine.Rendering.SupportedRenderingFeatures", "UnityEngine.Rendering.ScriptableRenderContext::",
        "UnityEngine.Rendering.RenderPipelineManager", "UnityEngine.Experimental.", "Unity.Burst.", "Unity.Jobs.", "UnityEngine.Rendering.OnDemandRendering::",
        "UnityEngine.QualitySettings::", "UnityEngineInternal.Input.", "UnityEngine.InputSystem.", "UnityEngine.Rendering.GraphicsFence", "UnityEngine.Windows.", "UnityEngine.Display::", "UnityEngine.Lightmapping",
    };
    public static int Registered;
    public static void Register(IcallRow[] rows)
    {
        long dummy = 0x10000;
        for (int id = 0; id < rows.Length; id++)
        {
            if (Shepherd.Hooks.Icalls.Table[id] != null) continue;
            ref readonly var r = ref rows[id];
            var key = r.Key;
            // ordinal (kit #4 boot): the hook keys are ASCII API names, and the Vita runs in invariant globalization, where the culture
            // StartsWith this used was the same comparison, through CultureInfo.CurrentCulture, ~170k times per boot
            bool hit = false; foreach (var p in Prefixes) if (key.StartsWith(p, StringComparison.Ordinal)) { hit = true; break; }
            if (!hit) continue;
            var ret = r.Ret;
            object val = ret switch
            {
                "System.Void" => null, "System.IntPtr" => (IntPtr)(++dummy), "System.Boolean" => false, "System.Int32" => 0, "System.UInt32" => 0u,
                "System.Int64" => 0L, "System.UInt64" => 0UL, "System.Single" => 0f, "System.Double" => 0.0, "System.UInt16" => (ushort)1, "System.String" => "", "System.Byte" => (byte)0,
                _ => null
            };
            var name = r.Name;
            if (ret == "System.Int32" && (name.Contains("QualityLevel") || name.Contains("Count"))) val = name.Contains("Count") ? 1 : 0;
            var captured = val;
            Shepherd.Hooks.Icalls.Table[id] = ret == "System.IntPtr" ? (_ => (IntPtr)Interlocked.Increment(ref dummy)) : (_ => captured);
            Registered++;
        }
        Log.Info($"inert stubs: {Registered} hooks (profiling/analytics/command buffers/quality…)");
    }
}
