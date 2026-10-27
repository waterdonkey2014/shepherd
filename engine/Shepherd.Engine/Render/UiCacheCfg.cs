namespace Shepherd.Engine.Render;

/// <summary>menu-speed switches of the retained UI pass (Render/SceneRenderer.UiCache.cs), like SHEPHERD_FX_LOG. The literals live in a
/// [ProfileOnly] type, so the V3 diet's readable-key scan (Assets/Diet.cs) is unchanged.
/// SHEPHERD_UICACHE=0: the reference UI pass (the pass as it was), for A/B. SHEPHERD_UICACHE_CHECK=1: see <see cref="UiCacheCheck"/>.</summary>
[ProfileOnly]
public static class UiCacheCfg
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_UICACHE") != "0";
    public static readonly bool Check = On && Environment.GetEnvironmentVariable("SHEPHERD_UICACHE_CHECK") == "1";
}
