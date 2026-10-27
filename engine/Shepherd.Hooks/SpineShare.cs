// kit6 mem, lever L1 (vita/notes/kit6-mem.md): the seam for Spine's skeleton reader. tools/Rewriter --spine-share rewrites the owner's
// copy of spine-unity.dll (owner-local, like the rest of runtime/ManagedVita; the game's own DLL on disk is never touched) so that every
// array its binary reader finishes -- vertex / UV / triangle / weight arrays, timeline keys and curves, deform and draw-order frames,
// region offsets / UVs -- passes through F / I here, and SkeletonDataAsset.GetSkeletonData brackets each parse with Begin / End. With no
// engine handler installed (the Mac default, or SHEPHERD_SPINE_READSHARE=0) every call returns its argument: the game's own arrays.
using System;
namespace Shepherd.Hooks
{
    public static class SpineShare
    {
        /// <summary>The engine's interning handlers (Shepherd.Engine Runtime/SpineReadShare.cs); null = pass-through.</summary>
        public static Func<float[], float[]> Floats;
        public static Func<int[], int[]> Ints;
        public static Action<object> OnBegin;
        public static Func<object, object> OnEnd;
        public static Func<object, object> OnOpen;

        public static float[] F(float[] a) { var f = Floats; return f == null || a == null ? a : f(a); }
        public static int[] I(int[] a) { var f = Ints; return f == null || a == null ? a : f(a); }
        /// <summary>GetSkeletonData, just before it parses: the SkeletonDataAsset (a UnityEngine.Object).</summary>
        public static void Begin(object asset) { OnBegin?.Invoke(asset); }
        /// <summary>GetSkeletonData, right after the parse (also after a parse that threw): the SkeletonData or null; returned unchanged.</summary>
        public static object End(object data) { var f = OnEnd; return f == null ? data : f(data); }
        /// <summary>GetSkeletonData's binary read (kit6 mem L1b): a System.IO.Stream over the TextAsset's bytes, or null = read TextAsset.bytes as
        /// the game does.</summary>
        public static object Open(object textAsset) { var f = OnOpen; return f == null ? null : f(textAsset); }
    }
}
