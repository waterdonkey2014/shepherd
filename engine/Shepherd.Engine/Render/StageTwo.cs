namespace Shepherd.Engine.Render;

/// <summary>V6 stage 2 helpers kept out of the hot paths' string literals (the V3 diet counts the literals of non-profile code).</summary>
[ProfileOnly]
public static class StageTwo
{
    /// <summary>What Enumerable.Select threw when the old DrawWorld copied a sprite whose geometry array is null.</summary>
    public static void SelectOfNull() => throw new ArgumentNullException("source");
}
