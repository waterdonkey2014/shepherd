// kit #7 spine track, "native clipping" (vita/notes/kit7-spine.md): the seam for Spine's SkeletonClipping. tools/Rewriter --spine-clip rewrites
// the owner's copy of spine-unity.dll (owner-local; the game's own DLL is never touched): ClipStart hands its clipping polygon to Start right
// after computing its world vertices, and ClipTriangles takes the engine's result when ClipStart did; in check mode both keep the managed
// result and the engine compares its own. Mode 0 (no engine handler: the Mac default, SHEPHERD_SPINE_CLIP=0, no native port in the eboot)
// = the original code everywhere.
using System;
namespace Shepherd.Hooks
{
    /// <summary>ClipTriangles' result as the three managed lists must hold it after the call: V / UV (NV floats each), T (NT indices; the
    /// triangle list reached MaxT on the way, which sets its capacity).</summary>
    public sealed class ClipResult { public float[] V, UV; public int[] T; public int NV, NT, MaxT; }

    public static class SpineClip
    {
        /// <summary>0 = the original code; 1 = native (ClipStart / ClipTriangles take the engine's results); 2 = check (the managed results
        /// are kept, the engine computes its own and compares them call by call).</summary>
        public static int Mode;
        public static Func<object, float[], int, object> OnStart;
        public static Func<object, int> OnPolygons;
        public static Func<object, float[], int, int[], int, float[], ClipResult> OnTris;
        public static Action<object, object> OnCheckStart;
        public static Action<object, float[], int, int[], int, float[], object, object, object> OnCheckTris;

        /// <summary>ClipStart, after the clipping polygon's world vertices (poly[0..n)): the clipper's state (kept by it for the next calls).</summary>
        public static object Start(object state, float[] poly, int n) => OnStart(state, poly, n);
        /// <summary>The polygon count ClipStart returns when the engine's polygons are used, or -1: the managed code goes on.</summary>
        public static int Polygons(object state) => OnPolygons(state);
        public static ClipResult Tris(object state, float[] vertices, int verticesLength, int[] triangles, int trianglesLength, float[] uvs)
            => OnTris(state, vertices, verticesLength, triangles, trianglesLength, uvs);
        /// <summary>Check mode, end of ClipStart: the managed polygons (an ExposedList of ExposedList&lt;float&gt;).</summary>
        public static void CheckStart(object state, object polygons) { OnCheckStart?.Invoke(state, polygons); }
        /// <summary>Check mode, end of ClipTriangles: its inputs and the three managed output lists.</summary>
        public static void CheckTris(object state, float[] vertices, int verticesLength, int[] triangles, int trianglesLength, float[] uvs, object outV, object outUV, object outT)
        { OnCheckTris?.Invoke(state, vertices, verticesLength, triangles, trianglesLength, uvs, outV, outUV, outT); }
    }
}
