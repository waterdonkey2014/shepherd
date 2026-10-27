namespace Shepherd.Engine.Render;

/// <summary>Kit #6 track fps switches (vita/notes/kit6-fps.md). Each one is read once; "0" in the app's env file restores kit #5's code path.
/// The literals live in a [ProfileOnly] type, so the V3 diet's readable-key scan (Assets/Diet.cs) is unchanged.
/// SHEPHERD_FPS_VERTEX=0: the per-vertex call to <see cref="SceneRenderer.Vertex"/> (kit #5) in the UI pass and the world mesh build, instead
/// of its body written out in those loops with the byte -> float colour table (the same values and stores: SceneRenderer.B2F).</summary>
[ProfileOnly]
public static class FpsCfg
{
    public static readonly bool InlineVertex = Environment.GetEnvironmentVariable("SHEPHERD_FPS_VERTEX") != "0";
    /// <summary>SHEPHERD_FPS_UISCROLL=0: a UI renderer that only moved (a scrolling list: the Credits) is rebuilt in full every pass (kit #5),
    /// instead of getting new positions from its block's partial sums (SceneRenderer.UiCache.cs; also off with SHEPHERD_FPS_VERTEX=0).</summary>
    public static readonly bool UiTranslate = Environment.GetEnvironmentVariable("SHEPHERD_FPS_UISCROLL") != "0";
    /// <summary>SHEPHERD_FPS_UIBLOCKS=&lt;KiB&gt;: the retained UI pass's block budget (SceneRenderer.UiCache.cs BlockBudget); default 2048, kit #5 512
    /// (the switch back). A renderer over the budget is built in the shared buffers every pass: the same bytes, more work.</summary>
    public static readonly long UiBlockBudget = (long.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_FPS_UIBLOCKS"), out var kib) && kib >= 0 ? kib : 2048) * 1024;
}
