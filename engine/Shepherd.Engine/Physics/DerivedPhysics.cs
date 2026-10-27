using System.Reflection;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 4 step 4b: the owner-local provider's second contract -- Unity 2022.3's ClipperOffset passes of the composite pipeline
/// (closed polygons, square joins; <see cref="Composite.RunUnity"/>). Clipper's offset is third-party code the engine saw only as the game's
/// decompiled Pathfinding.ClipperLib, so a port of it lives only in Shepherd.Engine.Derived (vita/PLAN.md "Hygiene"). Without it the engine
/// calls the game's own Clipper.OffsetPolygons (square joins): the same outlines, but its cleanup union takes the paths in the other order, so
/// the start vertex / path order of tie cases (H-STARTVTX) may differ from Unity's.</summary>
public interface IPathOffsetter
{
    /// <summary>Unity's offset of closed IntPoint paths (x, y pairs) by delta IntPoint units with square joins, its cleanup union included
    /// (through <paramref name="clipper"/>, the game's Clipper).</summary>
    List<long[]> OffsetSquare(List<long[]> paths, double delta, IClipperHost clipper);
}

/// <summary>Clipper's polygon fill types, in the order of Pathfinding.ClipperLib.PolyFillType.</summary>
public enum ClipFill { EvenOdd, NonZero, Positive, Negative }

/// <summary>The engine's calls into the game's own Pathfinding.ClipperLib (loaded from the owner's install, reached by reflection: Composite.cs),
/// for <see cref="IPathOffsetter"/>.</summary>
public interface IClipperHost
{
    /// <summary>A union of the closed subject paths, added in the order given; <paramref name="reverseSolution"/> sets Clipper.ReverseSolution.</summary>
    List<long[]> Union(List<long[]> paths, ClipFill fill, bool reverseSolution);
    /// <summary>Clipper.GetBounds after adding the paths as closed subjects.</summary>
    (long left, long top, long right, long bottom) Bounds(List<long[]> paths);
}

/// <summary>Physics Stage 4 step 4b (vita/notes/physics-stage4.md section 10): Unity 2022.3 splits a PolygonCollider2D's paths and a Polygons-mode
/// CompositeCollider2D's paths into convex pieces with the libtess2 tessellator (TESS_WINDING_ODD, TESS_POLYGONS of at most b2_maxPolygonVertices
/// = 8 vertices, libtess2's edge-list MergeConvexFaces) -- measured bit for bit on the E1 oracle's Paths4b* scenes (owner-local physics-work/e1).
/// A tessellator port is third-party code, so it lives only in the owner-local assembly Shepherd.Engine.Derived (engine/Derived/, git-ignored;
/// vita/PLAN.md "Hygiene", like Render/DerivedRender.cs): the engine defines only this contract and loads the provider by name on first use.
/// Without it (every shareable build, or SHEPHERD_DERIVED=off) the engine keeps the native decomposition of Stage 1 (ear clipping + merging,
/// per path) and logs once.</summary>
public interface IPolygonDecomposer
{
    /// <summary>The contract version the provider was built against (<see cref="DerivedPhysics.Api"/>).</summary>
    int Api { get; }
    /// <summary>Unity's convex decomposition of the contours (x, y pairs, body space, every path of the collider): the pieces in libtess2's
    /// output order, each an x, y array of at most 8 vertices in its face order. Degenerate input gives no pieces.</summary>
    float[][] Decompose(float[][] contours);
}

[ProfileOnly]   // log text and reflection names only: no property-bag key
public static class DerivedPhysics
{
    /// <summary>Bump when <see cref="IPolygonDecomposer"/> changes shape or meaning.</summary>
    public const int Api = 1;
    static bool _tried; static IPolygonDecomposer _p; static IPathOffsetter _o;
    public static string Status { get; private set; } = "not loaded yet";
    /// <summary>The loaded decomposer, or null (no owner-local assembly, SHEPHERD_DERIVED=off, or another <see cref="Api"/>).</summary>
    public static IPolygonDecomposer Decomposer { get { if (!_tried) Load(); return _p; } }
    /// <summary>The loaded offsetter (the same provider), or null as <see cref="Decomposer"/>.</summary>
    public static IPathOffsetter Offsetter { get { if (!_tried) Load(); return _o; } }

    static void Load()
    {
        _tried = true;
        if ((Environment.GetEnvironmentVariable("SHEPHERD_DERIVED") ?? "").Trim().ToLowerInvariant() is "off" or "0")
        { Status = "off (SHEPHERD_DERIVED)"; Log.Info("[derived] physics: SHEPHERD_DERIVED=off -- Stage 1's native polygon decomposition (H-DECOMP not Unity's)"); return; }
        Assembly asm;
        try { asm = Assembly.Load(new AssemblyName(Shepherd.Engine.Render.DerivedRender.AssemblyName)); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        { Status = "absent"; Log.Info($"[derived] physics: no owner-local Shepherd.Engine.Derived ({e.GetType().Name}) -- Stage 1's native polygon decomposition (H-DECOMP not Unity's)"); return; }
        try
        {
            var t = asm.GetType("Shepherd.Engine.Derived.PhysicsProvider", false);
            if (t == null) { Status = "no provider"; Log.Info($"[derived] physics: {asm.Location} has no physics provider (built before Stage 4b) -- Stage 1's native polygon decomposition"); return; }
            var p = (IPolygonDecomposer)Activator.CreateInstance(t);
            if (p.Api != Api) { Status = $"api {p.Api} != {Api}"; Log.Warn($"[derived] physics: provider API {p.Api}, the engine's is {Api}: rebuild it (vita/shepherd/build-derived.sh) -- Stage 1's native polygon decomposition"); return; }
            _p = p; _o = p as IPathOffsetter; Status = $"loaded (api {Api}){(_o == null ? ", no offsetter" : "")}";
            Log.Info($"[derived] physics: polygon decomposer{(_o != null ? " + composite offsetter" : "")} api {Api} from {asm.Location} (Unity 2022.3's libtess2 pieces{(_o != null ? ", Clipper's offset" : "")})");
        }
        catch (Exception e) when (e is TypeLoadException or MissingMethodException or MissingFieldException or InvalidCastException or TargetInvocationException)
        { Status = "incompatible"; Log.Warn($"[derived] physics: the owner-local provider does not fit this engine ({e.GetBaseException().Message}): rebuild it -- Stage 1's native polygon decomposition"); }
    }
}
