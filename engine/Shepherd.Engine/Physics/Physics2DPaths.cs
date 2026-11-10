using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 4, step 4a (physics-plan-v2 section 2 "Stage 4": ids 6348 / 6343 / 6341, 6599 / 6603 / 6593 / 6596 / 6595 / 6598 / 6602,
/// 6397, and their siblings): the path icalls of PolygonCollider2D, EdgeCollider2D and CompositeCollider2D, bound only at SHEPHERD_PHYS >= 4 with a
/// core (<c>Phys = 4</c>; below it they stay "missing, log once" as before). Each serves Unity's managed wrapper (which validates the index range
/// itself); the contracts are the Unity 2022.3 oracle's (physics-work/e1, scenes Paths4a / Paths4a2 on primitives, a macOS player;
/// vita/notes/physics-stage4.md):
/// <list type="bullet">
/// <item>PolygonCollider2D: <c>points</c> = a copy of path 0 (an empty array with no path); setting it with a null or empty array does nothing,
/// otherwise the collider has that one path (pathCount 1, any length, the points stored raw -- duplicates, collinear points, 1 or 2 points); a
/// path index at or past pathCount in SetPath logs "Failed setting path. Index is out of bounds." and does nothing; SetPath with an empty array
/// does nothing, with an empty list empties the path; pathCount grows with empty paths and shrinks from the end (a negative count crashes Unity's
/// player: here it is refused); null arrays / lists in SetPath / GetPath(List) throw ArgumentNullException("points") (the native binding's
/// NotNull); GetPath / points return new arrays.</item>
/// <item>EdgeCollider2D: <c>points</c> = a copy; setting null or fewer than 2 points does nothing (SetPoints(List) then returns false), otherwise
/// the points are stored raw (the chain filter is the shape's: PhysWorld.Stage4); pointCount, edgeCount = pointCount - 1; GetPoints(List) clears
/// and fills.</item>
/// <item>CompositeCollider2D: pathCount / pointCount / GetPathPointCount / GetPath(array) / GetPath(List) read the generated (or serialized) paths
/// plus the composite's offset (PhysWorld.CompositeApiPaths); GetPath(array) copies min(array length, points) and returns that count; the
/// paths are the outlines whatever the geometry type; geometryType / generationType read and generationType written (Synchronous composites
/// regenerate on their sub-colliders' changes: PhysWorld.Stage4). A read never syncs transforms (Unity: a moved sub-collider shows only after the
/// transform sync).</item>
/// </list>
/// Every setter replaces the record's arrays (never writes into them: Instantiate's clones share them) and queues the collider (shapes rebuilt at
/// the next flush: queries see them at once, contacts at the next step -- Paths4a P21 / E10). No method names a Unity physics type in its
/// signature (IcallRegistry's start-up scan).</summary>
[ProfileOnly]   // reads no property-bag key (typed records); log text only
static class Physics2DPaths
{
    static readonly Impl I = new();

    [Icall("UnityEngine.PolygonCollider2D::get_points", Phys = 4)] static object PolyGetPoints(object[] a) => I.PolyGetPoints(a);
    [Icall("UnityEngine.PolygonCollider2D::set_points", Phys = 4)] static object PolySetPoints(object[] a) => I.PolySetPoints(a);
    [Icall("UnityEngine.PolygonCollider2D::get_pathCount", Phys = 4)] static object PolyGetPathCount(object[] a) => I.PolyGetPathCount(a);
    [Icall("UnityEngine.PolygonCollider2D::set_pathCount", Phys = 4)] static object PolySetPathCount(object[] a) => I.PolySetPathCount(a);
    [Icall("UnityEngine.PolygonCollider2D::GetTotalPointCount", Phys = 4)] static object PolyTotal(object[] a) => I.PolyTotal(a);
    [Icall("UnityEngine.PolygonCollider2D::GetPath_Internal", Phys = 4)] static object PolyGetPath(object[] a) => I.PolyGetPath(a);
    [Icall("UnityEngine.PolygonCollider2D::GetPathList_Internal", Phys = 4)] static object PolyGetPathList(object[] a) => I.PolyGetPathList(a);
    [Icall("UnityEngine.PolygonCollider2D::SetPath_Internal", Phys = 4)] static object PolySetPath(object[] a) => I.PolySetPath(a);
    [Icall("UnityEngine.PolygonCollider2D::SetPathList_Internal", Phys = 4)] static object PolySetPathList(object[] a) => I.PolySetPathList(a);
    [Icall("UnityEngine.EdgeCollider2D::get_points", Phys = 4)] static object EdgeGetPoints(object[] a) => I.EdgeGetPoints(a);
    [Icall("UnityEngine.EdgeCollider2D::set_points", Phys = 4)] static object EdgeSetPoints(object[] a) => I.EdgeSetPoints(a);
    [Icall("UnityEngine.EdgeCollider2D::get_pointCount", Phys = 4)] static object EdgePointCount(object[] a) => I.EdgePointCount(a);
    [Icall("UnityEngine.EdgeCollider2D::get_edgeCount", Phys = 4)] static object EdgeEdgeCount(object[] a) => I.EdgeEdgeCount(a);
    [Icall("UnityEngine.EdgeCollider2D::GetPoints", Phys = 4)] static object EdgeGetPointsList(object[] a) => I.EdgeGetPointsList(a);
    [Icall("UnityEngine.EdgeCollider2D::SetPoints", Phys = 4)] static object EdgeSetPointsList(object[] a) => I.EdgeSetPointsList(a);
    [Icall("UnityEngine.CompositeCollider2D::get_pathCount", Phys = 4)] static object CompPathCount(object[] a) => I.CompPathCount(a);
    [Icall("UnityEngine.CompositeCollider2D::get_pointCount", Phys = 4)] static object CompPointCount(object[] a) => I.CompPointCount(a);
    [Icall("UnityEngine.CompositeCollider2D::GetPathPointCount_Internal", Phys = 4)] static object CompPathPointCount(object[] a) => I.CompPathPointCount(a);
    [Icall("UnityEngine.CompositeCollider2D::GetPathArray_Internal", Phys = 4)] static object CompGetPathArray(object[] a) => I.CompGetPathArray(a);
    [Icall("UnityEngine.CompositeCollider2D::GetPathList_Internal", Phys = 4)] static object CompGetPathList(object[] a) => I.CompGetPathList(a);
    [Icall("UnityEngine.CompositeCollider2D::get_geometryType", Phys = 4)] static object CompGetGeometryType(object[] a) => I.CompGetGeometryType(a);
    [Icall("UnityEngine.CompositeCollider2D::get_generationType", Phys = 4)] static object CompGetGenerationType(object[] a) => I.CompGetGenerationType(a);
    [Icall("UnityEngine.CompositeCollider2D::set_generationType", Phys = 4)] static object CompSetGenerationType(object[] a) => I.CompSetGenerationType(a);
    // Stage 4b: offsetDistance / vertexDistance (the game never sets them; the oracle's union-alone cases do: Paths4b2 U_min*, both 0)
    [Icall("UnityEngine.CompositeCollider2D::get_offsetDistance", Phys = 4)] static object CompGetOffsetDistance(object[] a) => I.CompGetDistance(a, false);
    [Icall("UnityEngine.CompositeCollider2D::set_offsetDistance", Phys = 4)] static object CompSetOffsetDistance(object[] a) => I.CompSetDistance(a, false);
    [Icall("UnityEngine.CompositeCollider2D::get_vertexDistance", Phys = 4)] static object CompGetVertexDistance(object[] a) => I.CompGetDistance(a, true);
    [Icall("UnityEngine.CompositeCollider2D::set_vertexDistance", Phys = 4)] static object CompSetVertexDistance(object[] a) => I.CompSetDistance(a, true);

    /// <summary>Kit #13 basec (SHEPHERD_K13_POLYPATHS, Runtime/K13Basec.cs): the PolygonCollider2D / EdgeCollider2D hooks above, for IcallRegistry to
    /// bind below level 4 too. They read and write the typed records (made at level 0 and above). At level 0 (no core: the default's only level)
    /// that is all they do. With a core (levels 1-3, only with SHEPHERD_K13_POLYPATHS=all) Changed also queues the collider (PhysWorld.Changed:
    /// Sim), so a path set rebuilds its shapes at the next flush, as at level 4; composites regenerate only at level 4 and above (Sim4).</summary>
    internal static IEnumerable<(System.Reflection.MethodInfo m, string key)> BelowLevel4()
    {
        foreach (var m in typeof(Physics2DPaths).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
            foreach (var a in System.Reflection.CustomAttributeExtensions.GetCustomAttributes<IcallAttribute>(m))
                if (a.Key.StartsWith("UnityEngine.PolygonCollider2D::", StringComparison.Ordinal) || a.Key.StartsWith("UnityEngine.EdgeCollider2D::", StringComparison.Ordinal))
                    yield return (m, a.Key);
    }

    sealed class Impl
    {
        static NCollider2D C(object o, PhysRecords.Kind2D k) => Objects.Get(o) is NCollider2D c && c.Kind == k ? c : null;
        static readonly float[][] NoPaths = Array.Empty<float[]>();
        static Vector2[] ToArray(float[] p)
        {
            if (p == null) return Array.Empty<Vector2>();
            var r = new Vector2[p.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = new Vector2(p[2 * i], p[2 * i + 1]);
            return r;
        }
        static float[] FromArray(Vector2[] v)
        {
            var r = new float[v.Length * 2];
            for (int i = 0; i < v.Length; i++) { r[2 * i] = v[i].x; r[2 * i + 1] = v[i].y; }
            return r;
        }
        static float[] FromList(List<Vector2> v)
        {
            var r = new float[v.Count * 2];
            for (int i = 0; i < v.Count; i++) { r[2 * i] = v[i].x; r[2 * i + 1] = v[i].y; }
            return r;
        }
        static int Fill(float[] p, List<Vector2> l)
        {
            l.Clear();
            if (p == null) return 0;
            for (int i = 0; i + 1 < p.Length; i += 2) l.Add(new Vector2(p[i], p[i + 1]));
            return l.Count;
        }
        /// <summary>The record changed: its shapes are rebuilt at the next flush; a Synchronous composite that uses it regenerates now.</summary>
        static void Changed(NCollider2D c)
        {
            PhysWorld.PathSets++;
            PhysWorld.Changed(c);
            if (c.UsedByComposite) PhysWorld.SubColliderChanged(c);
        }
        static int _boundsLogged;
        static void OutOfBounds(NCollider2D c)
        {   // Unity logs this native error on every such call; the first 20 here, then a count in the summary
            if (++_boundsLogged <= 20) Log.Warn($"[phys] PolygonCollider2D '{c.Go?.Name}': Failed setting path. Index is out of bounds.");
        }
        static float[][] Paths(NCollider2D c) => c.Paths ?? NoPaths;

        // ================= PolygonCollider2D =================
        internal object PolyGetPoints(object[] a)
        {
            var c = C(a[0], PhysRecords.Kind2D.Polygon); PhysWorld.PathReads++;
            var p = c == null ? NoPaths : Paths(c);
            return p.Length == 0 ? Array.Empty<Vector2>() : ToArray(p[0]);
        }
        internal object PolySetPoints(object[] a)
        {
            if (C(a[0], PhysRecords.Kind2D.Polygon) is not { } c || a[1] is not Vector2[] v || v.Length == 0) return null;   // null / empty: nothing (Paths4a P9b, P10b)
            c.Paths = new[] { FromArray(v) };
            Changed(c);
            return null;
        }
        internal object PolyGetPathCount(object[] a) => C(a[0], PhysRecords.Kind2D.Polygon) is { } c ? Paths(c).Length : 0;
        internal object PolySetPathCount(object[] a)
        {
            if (C(a[0], PhysRecords.Kind2D.Polygon) is not { } c) return null;
            int n = Convert.ToInt32(a[1]), have = Paths(c).Length;
            if (n < 0) { Log.Warn($"[phys] PolygonCollider2D '{c.Go?.Name}': pathCount = {n} refused (Unity 2022.3's player crashes on a negative count)"); return null; }
            if (n == have) return null;
            var r = new float[n][];
            for (int i = 0; i < n; i++) r[i] = i < have ? Paths(c)[i] : Array.Empty<float>();
            c.Paths = r;
            Changed(c);
            return null;
        }
        internal object PolyTotal(object[] a)
        {
            if (C(a[0], PhysRecords.Kind2D.Polygon) is not { } c) return 0;
            int n = 0; foreach (var p in Paths(c)) n += (p?.Length ?? 0) / 2;
            return n;
        }
        internal object PolyGetPath(object[] a)
        {
            var c = C(a[0], PhysRecords.Kind2D.Polygon); int i = Convert.ToInt32(a[1]); PhysWorld.PathReads++;
            var p = c == null ? NoPaths : Paths(c);
            return i >= 0 && i < p.Length ? ToArray(p[i]) : Array.Empty<Vector2>();
        }
        internal object PolyGetPathList(object[] a)
        {
            if (a[2] is not List<Vector2> l) throw new ArgumentNullException("points");
            var c = C(a[0], PhysRecords.Kind2D.Polygon); int i = Convert.ToInt32(a[1]); PhysWorld.PathReads++;
            var p = c == null ? NoPaths : Paths(c);
            return Fill(i >= 0 && i < p.Length ? p[i] : null, l);
        }
        bool SetPath(NCollider2D c, int i, float[] pts)
        {
            var p = Paths(c);
            if (i < 0 || i >= p.Length) { OutOfBounds(c); return false; }
            var r = (float[][])p.Clone(); r[i] = pts;   // (a new outer array: a clone may share the old one)
            c.Paths = r;
            Changed(c);
            return true;
        }
        internal object PolySetPath(object[] a)
        {
            if (a[2] is not Vector2[] v) throw new ArgumentNullException("points");
            if (C(a[0], PhysRecords.Kind2D.Polygon) is not { } c) return null;
            int i = Convert.ToInt32(a[1]);
            if (i < 0 || i >= Paths(c).Length) { OutOfBounds(c); return null; }
            if (v.Length == 0) return null;   // an empty array: nothing (Paths4a P10b_setpath_empty_arr)
            SetPath(c, i, FromArray(v));
            return null;
        }
        internal object PolySetPathList(object[] a)
        {
            if (a[2] is not List<Vector2> l) throw new ArgumentNullException("points");
            if (C(a[0], PhysRecords.Kind2D.Polygon) is not { } c) return null;
            SetPath(c, Convert.ToInt32(a[1]), FromList(l));   // an empty list empties the path (P10b_setpath_empty_list)
            return null;
        }

        // ================= EdgeCollider2D =================
        static float[] EdgePts(NCollider2D c) => c?.Paths is { Length: > 0 } p ? p[0] : null;
        internal object EdgeGetPoints(object[] a) { PhysWorld.PathReads++; return ToArray(EdgePts(C(a[0], PhysRecords.Kind2D.Edge))); }
        bool EdgeSet(NCollider2D c, float[] pts)
        {
            if (c == null || pts.Length < 4) return false;   // fewer than 2 points: nothing (Paths4a E3 / E4; SetPoints returns false: E9)
            c.Paths = new[] { pts };
            Changed(c);
            return true;
        }
        internal object EdgeSetPoints(object[] a)
        {
            if (a[1] is Vector2[] v) EdgeSet(C(a[0], PhysRecords.Kind2D.Edge), FromArray(v));   // null: nothing (E5)
            return null;
        }
        internal object EdgePointCount(object[] a) => (EdgePts(C(a[0], PhysRecords.Kind2D.Edge))?.Length ?? 0) / 2;
        internal object EdgeEdgeCount(object[] a) { int n = (EdgePts(C(a[0], PhysRecords.Kind2D.Edge))?.Length ?? 0) / 2; return n > 0 ? n - 1 : 0; }
        internal object EdgeGetPointsList(object[] a)
        {
            if (a[1] is not List<Vector2> l) throw new ArgumentNullException("points");
            PhysWorld.PathReads++;
            return Fill(EdgePts(C(a[0], PhysRecords.Kind2D.Edge)), l);
        }
        internal object EdgeSetPointsList(object[] a)
        {
            if (a[1] is not List<Vector2> l) throw new ArgumentNullException("points");
            return EdgeSet(C(a[0], PhysRecords.Kind2D.Edge), FromList(l));
        }

        // ================= CompositeCollider2D =================
        static float[][] Comp(object o) => C(o, PhysRecords.Kind2D.Composite) is { } c ? PhysWorld.CompositeApiPaths(c) : NoPaths;
        internal object CompPathCount(object[] a) => Comp(a[0]).Length;
        internal object CompPointCount(object[] a) { int n = 0; foreach (var p in Comp(a[0])) n += p.Length / 2; return n; }
        internal object CompPathPointCount(object[] a)
        {
            var p = Comp(a[0]); int i = Convert.ToInt32(a[1]);
            return i >= 0 && i < p.Length ? p[i].Length / 2 : 0;
        }
        internal object CompGetPathArray(object[] a)
        {
            if (a[2] is not Vector2[] arr) throw new ArgumentNullException("points");
            var p = Comp(a[0]); int i = Convert.ToInt32(a[1]); PhysWorld.PathReads++;
            if (i < 0 || i >= p.Length) return 0;
            int n = Math.Min(arr.Length, p[i].Length / 2);   // min(array, path): the rest of the array untouched (Paths4a C3)
            for (int k = 0; k < n; k++) arr[k] = new Vector2(p[i][2 * k], p[i][2 * k + 1]);
            return n;
        }
        internal object CompGetPathList(object[] a)
        {
            if (a[2] is not List<Vector2> l) throw new ArgumentNullException("points");
            var p = Comp(a[0]); int i = Convert.ToInt32(a[1]); PhysWorld.PathReads++;
            return Fill(i >= 0 && i < p.Length ? p[i] : null, l);
        }
        internal object CompGetGeometryType(object[] a) =>
            Enum.ToObject(typeof(CompositeCollider2D.GeometryType), C(a[0], PhysRecords.Kind2D.Composite)?.GeometryType ?? 0);
        internal object CompGetGenerationType(object[] a) =>
            Enum.ToObject(typeof(CompositeCollider2D.GenerationType), C(a[0], PhysRecords.Kind2D.Composite)?.GenerationType ?? 0);
        internal object CompSetGenerationType(object[] a)
        {
            if (C(a[0], PhysRecords.Kind2D.Composite) is { } c) c.GenerationType = Convert.ToInt32(a[1]);   // (switching back to Synchronous: untested in E1; it regenerates on the next change)
            return null;
        }
        internal object CompGetDistance(object[] a, bool vertex) => C(a[0], PhysRecords.Kind2D.Composite) is { } c ? (vertex ? c.VertexDistance : c.OffsetDistance) : (object)0f;
        /// <summary>Stage 4b: the pipeline's parameters (stored as given: Unity took 0 for both, Paths4b2); a Synchronous composite regenerates
        /// when they changed its inputs' hash (Composite.Refresh), a Manual one at its next GenerateGeometry.</summary>
        internal object CompSetDistance(object[] a, bool vertex)
        {
            if (C(a[0], PhysRecords.Kind2D.Composite) is not { } c) return null;
            float v = Convert.ToSingle(a[1]);
            if (vertex) c.VertexDistance = v; else c.OffsetDistance = v;
            if (c.GenerationType == 0 && PhysWorld.Sim4) Composite.Refresh(c, false);
            return null;
        }
    }
}
