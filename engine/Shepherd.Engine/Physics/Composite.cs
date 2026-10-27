using System.Collections;
using System.Reflection;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 1 (physics-plan-v2 section 1.12): CompositeCollider2D.GenerateGeometry with the game's own Pathfinding.ClipperLib (loaded
/// from the owner's install at run time, never copied; reached through cached reflection), the verified pipeline:
/// <c>IntPoint x 1e7 -> OffsetPolygons(+offsetDistance x 1e7, jtMiter, 2) -> Union(NonZero) -> OffsetPolygons(-...) -> CleanPolygons(vertexDistance x 1e7)</c>
/// (CotL: +500 / -500 / 5000; fidelity/compo-oracle2: Hausdorff 2.9e-5..4.2e-5 against Unity's own m_CompositePaths on the 4 non-stale
/// composites). Inputs: the enabled usedByComposite colliders attached to the composite's Rigidbody2D (depth-first hierarchy order; the start
/// vertex H-STARTVTX stays open, Stage 4), in composite space (their local chain below the body transform). Output: paths in composite space
/// (Physics/PhysWorld.Stage1.cs makes Outlines chain loops or Polygons pieces of them). Memo: the exact input IntPoints + parameters
/// (RegenerateDecorationsWithPool then SetCollider regenerate the same inputs, GenerateRoom.cs).</summary>
[ProfileOnly]   // log text and reflection names only: no property-bag key (Assets/Diet.cs)
public static class Composite
{
    public static long Generations, MemoHits, InputPaths, OutputPaths, Refused;
    static bool _tried, _ok;
    static Type _tPt, _tPath, _tPaths;
    static MethodInfo _offset, _clean, _addPaths, _exec;
    static ConstructorInfo _ctor, _ptCtor; static object[] _ctorArgs;
    static FieldInfo _fx, _fy;
    static object _miter, _subject, _union, _nonZero;
    static object _positive, _negative, _square, _evenOdd; static PropertyInfo _revSol; static MethodInfo _bounds; static FieldInfo _rLeft, _rTop, _rRight, _rBottom;   // Stage 4b
    static readonly HashSet<string> _warned = new();
    static void Warn(string s) { if (_warned.Add(s)) Log.Info("[phys] composite: " + s); }

    static bool Bind()
    {
        if (_tried) return _ok;
        _tried = true;
        try
        {
            var asm = Assembly.Load("Pathfinding.ClipperLib");
            _tPt = asm.GetType("Pathfinding.ClipperLib.IntPoint"); var tClip = asm.GetType("Pathfinding.ClipperLib.Clipper");
            var tJoin = asm.GetType("Pathfinding.ClipperLib.JoinType"); var tPoly = asm.GetType("Pathfinding.ClipperLib.PolyType");
            var tCt = asm.GetType("Pathfinding.ClipperLib.ClipType"); var tFill = asm.GetType("Pathfinding.ClipperLib.PolyFillType");
            _tPath = typeof(List<>).MakeGenericType(_tPt); _tPaths = typeof(List<>).MakeGenericType(_tPath);
            _offset = tClip.GetMethod("OffsetPolygons", new[] { _tPaths, typeof(double), tJoin, typeof(double) });
            _clean = tClip.GetMethod("CleanPolygons", new[] { _tPaths, typeof(double) });
            _addPaths = tClip.GetMethod("AddPaths", new[] { _tPaths, tPoly, typeof(bool) });
            _exec = tClip.GetMethod("Execute", new[] { tCt, _tPaths, tFill, tFill });
            _ctor = tClip.GetConstructor(Type.EmptyTypes) ?? tClip.GetConstructor(new[] { typeof(int) });
            _ctorArgs = _ctor?.GetParameters().Length == 1 ? new object[] { 0 } : Array.Empty<object>();
            _ptCtor = _tPt.GetConstructor(new[] { typeof(long), typeof(long) });
            _fx = _tPt.GetField("X"); _fy = _tPt.GetField("Y");
            _miter = Enum.Parse(tJoin, "jtMiter"); _square = Enum.Parse(tJoin, "jtSquare"); _subject = Enum.Parse(tPoly, "ptSubject"); _union = Enum.Parse(tCt, "ctUnion"); _nonZero = Enum.Parse(tFill, "pftNonZero");
            _positive = Enum.Parse(tFill, "pftPositive"); _negative = Enum.Parse(tFill, "pftNegative"); _evenOdd = Enum.Parse(tFill, "pftEvenOdd");
            _revSol = tClip.GetProperty("ReverseSolution"); _bounds = tClip.GetMethod("GetBounds", Type.EmptyTypes);
            var tRect = asm.GetType("Pathfinding.ClipperLib.IntRect");
            _rLeft = tRect?.GetField("left"); _rTop = tRect?.GetField("top"); _rRight = tRect?.GetField("right"); _rBottom = tRect?.GetField("bottom");
            _ok = _offset != null && _clean != null && _addPaths != null && _exec != null && _ctor != null && _ptCtor != null && _fx != null && _fy != null &&
                  _revSol != null && _bounds != null && _square != null && _rLeft != null && _rTop != null && _rRight != null && _rBottom != null;
            if (!_ok) Log.Error("[phys] FAIL phys composite: a Pathfinding.ClipperLib member is missing");
        }
        catch (Exception e) { Log.Error("[phys] FAIL phys composite: Pathfinding.ClipperLib: " + e.GetBaseException().Message); _ok = false; }
        return _ok;
    }

    /// <summary>The IntPoint of a composite-space coordinate: Clipper's own rounding (Math.Round, away from zero at .5) of x * 1e7, in double.</summary>
    public static long ToInt(float v) => (long)Math.Round((double)v * 1e7, MidpointRounding.AwayFromZero);

    /// <summary>H-COMPORIENT=normalize (Stage 1's choice, decided by E1: a clockwise input unions like a counter-clockwise one, a contained clockwise
    /// square is no hole -- results/arm64-r1/Composite2D.csv): every input path gets positive orientation before the pipeline (a clockwise
    /// sub-collider path is reversed). The intro's bridge piece ("Island Piece 9", Unity's own serialized input is clockwise) otherwise cancels
    /// against the two islands under NonZero. The `as-is` variant was deleted with E1 (vita/notes/physics-ledger.md).</summary>
    static long Area2(long[] p)
    {
        long a = 0; int n = p.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++) a += p[2 * j] * p[2 * i + 1] - p[2 * i] * p[2 * j + 1];   // (|coords| < 2^31 / 2: no overflow at 1e7 x 100 u)
        return a;
    }
    public static long Reversed;
    /// <summary>The pipeline on IntPoint inputs (x, y pairs per path); null if ClipperLib is unavailable.</summary>
    public static long[][] Run(IReadOnlyList<long[]> inputs, double offset, double clean)
    {
        if (!Bind()) return null;
        var paths = (IList)Activator.CreateInstance(_tPaths);
        foreach (var p in inputs)
        {
            var path = (IList)Activator.CreateInstance(_tPath);
            bool rev = Area2(p) < 0;
            if (rev) Reversed++;
            int n = p.Length / 2;
            for (int k = 0; k < n; k++) { int i = rev ? n - 1 - k : k; path.Add(_ptCtor.Invoke(new object[] { p[2 * i], p[2 * i + 1] })); }
            paths.Add(path);
        }
        var grown = _offset.Invoke(null, new[] { paths, (object)offset, _miter, (object)2.0 });
        var c = _ctor.Invoke(_ctorArgs); _addPaths.Invoke(c, new[] { grown, _subject, (object)true });
        var sol = (IList)Activator.CreateInstance(_tPaths); _exec.Invoke(c, new[] { _union, sol, _nonZero, _nonZero });
        var shrunk = _offset.Invoke(null, new[] { (object)sol, -offset, _miter, 2.0 });
        var done = (IList)shrunk;
        var r = new List<long[]>(done.Count);
        for (int k = 0; k < done.Count; k++)
        {
            var path = (IList)done[k]; var a = new long[path.Count * 2];
            for (int i = 0; i < path.Count; i++) { a[2 * i] = (long)_fx.GetValue(path[i]); a[2 * i + 1] = (long)_fy.GetValue(path[i]); }
            a = CleanPolygon(a, clean); if (a.Length == 0) continue;   // (an emptied path is dropped, as CleanPolygons gives [])
            r.Add(a);
        }
        return r.ToArray();
    }

    // ---------------- Stage 4b: Unity 2022.3's pipeline (H-STARTVTX, the outline's own vertices) ----------------
    /// <summary>Stage 4b (vita/notes/physics-stage4.md section 10): Unity's IntPoint of a body-space coordinate -- a float multiply by 1e7 and a
    /// truncation toward zero (the game's 8 composites with serialized inputs: every m_ColliderPaths point rebuilt from its source this way;
    /// a double multiply or a rounding differs).</summary>
    public static long ToIntUnity(float v) => (long)(v * 1e7f);
    /// <summary>Stage 4b: Unity's float of an output IntPoint, (float)n x 1e-7f (the E1 oracle's bits).</summary>
    public static float ToFloatUnity(long n) => (float)n * 1e-7f;
    public static float[][] ToFloatUnity(long[][] p)
    {
        var r = new float[p.Length][];
        for (int k = 0; k < p.Length; k++) { r[k] = new float[p[k].Length]; for (int i = 0; i < p[k].Length; i++) r[k][i] = ToFloatUnity(p[k][i]); }
        return r;
    }

    /// <summary>Stage 4b: Unity 2022.3's CompositeCollider2D pipeline, measured bit for bit on the E1 oracle (physics-work/e1: Composite2D, Paths4b /
    /// 4b2 / 4b3 / 4b4) and on the game's own data (the 4 non-stale composites with serialized inputs: their m_CompositePaths; 10 rooms' baked island
    /// splines): each input path counter-clockwise (H-COMPORIENT=normalize); a ClipperOffset of +offsetDistance with square joins; a NonZero union;
    /// a ClipperOffset of -2 offsetDistance; a ClipperOffset of +offsetDistance (square joins throughout: every corner, convex and concave, is cut
    /// 293-294 IntPoint units along an edge -- the residue of the closing and opening); each output path then starts one point earlier than the
    /// game's Clipper 6.1 starts it (Clipper 6.2+'s BuildResult walks from OutRec.Pts.Prev); CleanPolygons(vertexDistance) (Clipper 6.1+'s rule,
    /// <see cref="CleanPolygon"/>). The unions are the game's own Pathfinding.ClipperLib; its local minima at equal Y are processed newest first,
    /// Unity's Clipper keeps them in insertion order (the path order of tie cases: two squares side by side list the right one first), so every
    /// union here is given its paths in reverse (the NonZero union here; the offsets' cleanup unions inside the owner-local offsetter,
    /// DerivedPhysics.cs IPathOffsetter -- Clipper's offset is third-party code, ported only there; without it the game's own OffsetPolygons
    /// with square joins, whose cleanup unions keep the game's order). delta and clean in IntPoint units (Unity: offsetDistance x 1e7f = 500, vertexDistance x 1e7f =
    /// 5000). Known residual (section 10): where offset edges of slanted overlapping pieces cross, a vertex may differ by 1-3 units (never in the
    /// game data checked).</summary>
    public static long[][] RunUnity(IReadOnlyList<long[]> inputs, double delta, double clean)
    {
        if (!Bind()) return null;
        var ins = new List<long[]>(inputs.Count);
        foreach (var p in inputs)
        {
            if (Area2(p) >= 0) { ins.Add(p); continue; }
            Reversed++;
            int n = p.Length / 2; var q = new long[p.Length];
            for (int k = 0; k < n; k++) { q[2 * k] = p[2 * (n - 1 - k)]; q[2 * k + 1] = p[2 * (n - 1 - k) + 1]; }
            ins.Add(q);
        }
        var o = DerivedPhysics.Offsetter;
        if (o == null) Warn("H-STARTVTX: no owner-local offsetter (Shepherd.Engine.Derived: " + DerivedPhysics.Status + ") -- the game's Clipper.OffsetPolygons with square joins: Unity's outlines, but the start / order of tie cases may differ");
        List<long[]> Offset(List<long[]> p, double d) => o != null ? o.OffsetSquare(p, d, Host) : GameOffset(p, d);
        var grown = Offset(ins, delta);
        var merged = ClipUnion(grown, _nonZero);
        var shrunk = Offset(merged, -2.0 * delta);
        var done = Offset(shrunk, delta);
        var r = new List<long[]>(done.Count);
        foreach (var p in done)
        {
            int n = p.Length / 2; if (n == 0) continue;
            var q = new long[p.Length];   // (rotated left by one: Clipper 6.2+'s start)
            for (int i = 0; i < n; i++) { int j = (i + 1) % n; q[2 * i] = p[2 * j]; q[2 * i + 1] = p[2 * j + 1]; }
            var c = CleanPolygon(q, clean); if (c.Length == 0) continue;
            r.Add(c);
        }
        return r.ToArray();
    }

    static IList ToClipper(IEnumerable<long[]> paths)
    {
        var res = (IList)Activator.CreateInstance(_tPaths);
        foreach (var p in paths)
        {
            var path = (IList)Activator.CreateInstance(_tPath);
            for (int i = 0; i + 1 < p.Length; i += 2) path.Add(_ptCtor.Invoke(new object[] { p[i], p[i + 1] }));
            res.Add(path);
        }
        return res;
    }
    static List<long[]> FromClipper(IList sol)
    {
        var r = new List<long[]>(sol.Count);
        foreach (IList path in sol)
        {
            var a = new long[path.Count * 2];
            for (int i = 0; i < path.Count; i++) { a[2 * i] = (long)_fx.GetValue(path[i]); a[2 * i + 1] = (long)_fy.GetValue(path[i]); }
            r.Add(a);
        }
        return r;
    }
    /// <summary>A union through the game's Clipper with the paths added in reverse (Unity's tie order, <see cref="RunUnity"/>).</summary>
    static List<long[]> ClipUnion(List<long[]> paths, object fill)
    {
        var rev = new List<long[]>(paths); rev.Reverse();
        var c = _ctor.Invoke(_ctorArgs); _addPaths.Invoke(c, new[] { ToClipper(rev), _subject, (object)true });
        var sol = (IList)Activator.CreateInstance(_tPaths); _exec.Invoke(c, new[] { _union, sol, fill, fill });
        return FromClipper(sol);
    }
    /// <summary>Without the owner-local offsetter: the game's own Clipper.OffsetPolygons with square joins (its cleanup union in its own order).</summary>
    static List<long[]> GameOffset(List<long[]> paths, double delta) => FromClipper((IList)_offset.Invoke(null, new[] { ToClipper(paths), (object)delta, _square, (object)2.0 }));

    /// <summary>The game's Clipper for the owner-local offsetter (DerivedPhysics.cs IClipperHost): plain API calls, paths in the order given.</summary>
    sealed class ClipperHost : IClipperHost
    {
        static object Fill(ClipFill f) => f switch { ClipFill.NonZero => _nonZero, ClipFill.Positive => _positive, ClipFill.Negative => _negative, _ => _evenOdd };
        public List<long[]> Union(List<long[]> paths, ClipFill fill, bool reverseSolution)
        {
            var c = _ctor.Invoke(_ctorArgs); _addPaths.Invoke(c, new[] { ToClipper(paths), _subject, (object)true });
            if (reverseSolution) _revSol.SetValue(c, true);
            var sol = (IList)Activator.CreateInstance(_tPaths); var f = Fill(fill); _exec.Invoke(c, new[] { _union, sol, f, f });
            return FromClipper(sol);
        }
        public (long left, long top, long right, long bottom) Bounds(List<long[]> paths)
        {
            var c = _ctor.Invoke(_ctorArgs); _addPaths.Invoke(c, new[] { ToClipper(paths), _subject, (object)true });
            var r = _bounds.Invoke(c, null);
            return ((long)_rLeft.GetValue(r), (long)_rTop.GetValue(r), (long)_rRight.GetValue(r), (long)_rBottom.GetValue(r));
        }
    }
    static readonly ClipperHost Host = new();

    /// <summary>H-CLEAN=spikes (Stage 1's choice, decided by E1: a 3.2e-4 crack closes into one 4-point rectangle with no spike, a 2e-3 crack keeps
    /// its notch -- results/arm64-r1/Composite2D.csv): the shrunk union is cleaned with this file's own <see cref="CleanPolygon"/> -- Clipper
    /// 6.1+'s rule, which also strips spikes. On the intro room the islands' tilted top edge leaves a 1.9 u long, up to 3.2e-4 u wide crack under
    /// the bridge piece; the game's own Pathfinding.ClipperLib CleanPolygons (Clipper 6.0) would keep it as a spike (a wall across the gate).
    /// The `game` variant was deleted with E1 (vita/notes/physics-ledger.md).</summary>
    /// <summary>Clipper 6.1+'s CleanPolygon (the published algorithm, written here on long x / y pairs): a ring of the points walked until every
    /// remaining vertex was accepted once; a vertex goes when it is within <paramref name="distance"/> of its predecessor; it and its
    /// successor go when its predecessor and successor are that close (a spike's two sides); it goes when the three are near collinear,
    /// measured at whichever of the three lies geometrically between the others (so a spike's tip, with a neighbour between, goes).
    /// Fewer than 3 points left: empty.</summary>
    public static long[] CleanPolygon(long[] p, double distance)
    {
        int n = p.Length / 2;
        if (n == 0) return p;
        var nx = new int[n]; var pv = new int[n]; var ok = new bool[n];
        for (int i = 0; i < n; i++) { nx[i] = (i + 1) % n; pv[nx[i]] = i; }
        double d2 = distance * distance;
        long X(int i) => p[2 * i]; long Y(int i) => p[2 * i + 1];
        bool Close(int a, int b) { double dx = (double)X(a) - X(b), dy = (double)Y(a) - Y(b); return dx * dx + dy * dy <= d2; }
        double LineD2(int q, int l1, int l2)
        {
            double A = Y(l1) - Y(l2), B = X(l2) - X(l1), C = A * X(l1) + B * Y(l1);
            C = A * X(q) + B * Y(q) - C;
            return C * C / (A * A + B * B);
        }
        bool NearCollinear(int a, int b, int c)
        {
            if (Math.Abs(X(a) - X(b)) > Math.Abs(Y(a) - Y(b)))
            {
                if ((X(a) > X(b)) == (X(a) < X(c))) return LineD2(a, b, c) < d2;
                if ((X(b) > X(a)) == (X(b) < X(c))) return LineD2(b, a, c) < d2;
                return LineD2(c, a, b) < d2;
            }
            if ((Y(a) > Y(b)) == (Y(a) < Y(c))) return LineD2(a, b, c) < d2;
            if ((Y(b) > Y(a)) == (Y(b) < Y(c))) return LineD2(b, a, c) < d2;
            return LineD2(c, a, b) < d2;
        }
        int Exclude(int i) { int r = pv[i]; nx[r] = nx[i]; pv[nx[i]] = r; ok[r] = false; return r; }
        int op = 0, cnt = n;
        while (!ok[op] && nx[op] != pv[op])
        {
            if (Close(op, pv[op])) { op = Exclude(op); cnt--; }
            else if (Close(pv[op], nx[op])) { Exclude(nx[op]); op = Exclude(op); cnt -= 2; }
            else if (NearCollinear(pv[op], op, nx[op])) { op = Exclude(op); cnt--; }
            else { ok[op] = true; op = nx[op]; }
        }
        if (cnt < 3) return Array.Empty<long>();
        var r = new long[cnt * 2];
        for (int i = 0; i < cnt; i++) { r[2 * i] = X(op); r[2 * i + 1] = Y(op); op = nx[op]; }
        return r;
    }
    public static float[][] ToFloat(long[][] p)
    {
        var r = new float[p.Length][];
        for (int k = 0; k < p.Length; k++) { r[k] = new float[p[k].Length]; for (int i = 0; i < p[k].Length; i++) r[k][i] = (float)(p[k][i] / 1e7); }
        return r;
    }

    // ---------------- GenerateGeometry ----------------
    sealed class MemoEntry { public long[][] In; public double Off, Clean; public float[][] Out; }
    static readonly Dictionary<ulong, List<MemoEntry>> _memo = new();
    /// <summary>The memo's bound (4b review): at most this many entries, the oldest dropped first -- its use is a generation repeated with the
    /// same inputs soon after (RegenerateDecorationsWithPool then SetCollider), and a composite whose inputs keep changing (a sub-collider animated
    /// relative to its body) would otherwise grow it for as long as it runs. A dropped entry only costs a recomputation (the same output).</summary>
    const int MemoCap = 64;
    static readonly Queue<ulong> _memoOrder = new();
    public static int MemoEntries;
    static void MemoAdd(ulong h, MemoEntry e)
    {
        if (!_memo.TryGetValue(h, out var l)) _memo[h] = l = new List<MemoEntry>();
        l.Add(e); _memoOrder.Enqueue(h); MemoEntries++;
        while (MemoEntries > MemoCap && _memoOrder.Count > 0)
        {
            var old = _memoOrder.Dequeue();   // (entries of one key are appended in order: its first is the oldest)
            if (!_memo.TryGetValue(old, out var ol) || ol.Count == 0) continue;
            ol.RemoveAt(0); MemoEntries--;
            if (ol.Count == 0) _memo.Remove(old);
        }
    }
    /// <summary>The composite's body transform (the composite sits on its Rigidbody2D's GameObject), or null (logged once).</summary>
    static NTransform BodyTransformOf(NCollider2D comp)
    {
        var tb = PhysWorld.AttachedRecord(comp)?.Go?.Transform;
        if (tb == null || !ReferenceEquals(comp.Go?.Transform, tb)) { Warn("a CompositeCollider2D not on its Rigidbody2D's GameObject: no inputs"); return null; }
        return tb;
    }
    /// <summary>The colliders a composite takes, in Unity's input order: the enabled, active usedByComposite colliders on its body's GameObject and
    /// below it (depth first), not past a nested Rigidbody2D.</summary>
    static void ForEachUsed(NCollider2D comp, NTransform tb, Action<NCollider2D, NTransform> f)
    {
        void Walk(NTransform t)
        {
            foreach (var k in t.Go.Components)
                if (k is NCollider2D c && !c.Destroyed && c.UsedByComposite && c.Enabled && c.Go.ActiveInHierarchy && !ReferenceEquals(c, comp)) f(c, t);
            foreach (var ch in t.Children)
            {
                if (ch.Go == null || !ch.Go.ActiveInHierarchy) continue;
                bool nested = false; foreach (var k in ch.Go.Components) if (k is NRigidbody2D b && !b.Destroyed) { nested = true; break; }
                if (!nested) Walk(ch);
            }
        }
        Walk(tb);
    }
    /// <summary>A collider's local chain below the body transform (the identity on the body's own GameObject): its pose relative to the body,
    /// which a move or turn of the body leaves bit for bit unchanged.</summary>
    static Matrix4x4 RelChain(NTransform t, NTransform tb)
    {
        if (ReferenceEquals(t, tb)) return Matrix4x4.identity;
        var r = t.LocalMatrix;
        for (var p = t.Parent; p != null && !ReferenceEquals(p, tb); p = p.Parent) r = M.Mul(p.LocalMatrix, r);
        return r;
    }

    /// <summary>The composite's inputs in composite space as IntPoint paths (for the pipeline and for physcheck's comparison with Unity's own
    /// serialized m_ColliderPaths).</summary>
    public static List<long[]> Inputs(NCollider2D comp, out int colliders)
    {
        colliders = 0;
        var res = new List<long[]>();
        var tb = BodyTransformOf(comp);
        if (tb == null) return res;
        int n = 0;
        // physics Stage 4a (Paths4a C4, Paths4a2 S_*): Unity's composite space is the body's space with the body transform's scale (and mirror)
        // baked in, its rotation and position left out. Stage 4b: the exact float form -- M = inverse(T R of the body) x the collider's
        // localToWorld, (p + offset) through M, then ToIntUnity (PhysWorld.UnityBodyInverseTR; every serialized m_ColliderPaths point of the game's
        // 8 composites with inputs rebuilt bit for bit, incl. the DLC shrine's rotated / tilted sources; the E1 oracle's C_* cases)
        bool q4 = PhysWorld.Sim4; Matrix4x4 tri = q4 ? PhysWorld.UnityBodyInverseTR(tb) : default;
        ForEachUsed(comp, tb, (c, t) => { Add(c, t); n++; });
        void Add(NCollider2D c, NTransform t)
        {
            if (Environment.GetEnvironmentVariable("SHEPHERD_PHYS_DUMP") == "1")
                Log.Info($"[phys] composite input collider '{c.Go?.Name}' {c.Kind} offset ({c.OffsetX},{c.OffsetY}) local {t.LocalPosition} scale {t.LocalScale} rot {t.LocalRotation} parent '{t.Parent?.Go?.Name}' paths {c.Paths?.Length ?? 0}");
            Matrix4x4 r = q4 ? M.Mul(tri, t.LocalToWorld) : RelChain(t, tb);
            long[] Path(ReadOnlySpan<float> pts)
            {
                var a = new long[pts.Length];
                for (int i = 0; i + 1 < pts.Length; i += 2)
                {
                    float x = pts[i] + c.OffsetX, y = pts[i + 1] + c.OffsetY;
                    if (!q4) { a[i] = ToInt(r.m00 * x + r.m01 * y + r.m03); a[i + 1] = ToInt(r.m10 * x + r.m11 * y + r.m13); continue; }
                    a[i] = ToIntUnity(r.m00 * x + r.m01 * y + r.m03); a[i + 1] = ToIntUnity(r.m10 * x + r.m11 * y + r.m13);
                }
                return a;
            }
            if (PhysWorld.Sim4 && !PhysWorld.Compositable(c.Kind)) { Warn($"a {c.Kind} collider marked usedByComposite: not compositable in Unity 2022.3 (Stage 4a: skipped)"); return; }
            switch (c.Kind)
            {
                case PhysRecords.Kind2D.Polygon: if (c.Paths != null) foreach (var p in c.Paths) if (p != null && p.Length >= 6) res.Add(Path(p)); break;
                case PhysRecords.Kind2D.Box:
                    float hx = 0.5f * c.SizeX, hy = 0.5f * c.SizeY;
                    res.Add(Path(new[] { -hx, -hy, hx, -hy, hx, hy, -hx, hy }));
                    break;
                case PhysRecords.Kind2D.Circle:
                    Warn("a CircleCollider2D used by a composite: a 32-gon (Unity's segment count UNVERIFIED)");
                    var q = new float[64];
                    for (int i = 0; i < 32; i++) { float a = i * (MathF.PI * 2f / 32f); q[2 * i] = c.Radius * MathF.Cos(a); q[2 * i + 1] = c.Radius * MathF.Sin(a); }
                    res.Add(Path(q));
                    break;
                default: Warn($"a {c.Kind} collider used by a composite: not supported in Stage 1"); break;
            }
        }
        colliders = n;
        return res;
    }

    /// <summary>Stage 4b review: what a Synchronous composite's regeneration (<see cref="Refresh"/>) is keyed on at level 4+ -- the offset /
    /// vertex distances, the body transform's lossy scale (Unity bakes the body's scale into the inputs), and per used collider (in input order) its
    /// identity, kind, offset, size / radius / path points and its pose relative to the body (<see cref="RelChain"/>). Since 4b the inputs
    /// themselves go through world space (inverse(T R of the body) x localToWorld, Unity's float form), so their last bits move with the body's
    /// position and angle: keyed on them, a composite on a moving body regenerated at every flush it was queued (its children are dirtied by the
    /// write-back), rebuilding its fixtures (contacts and warm starting lost) and adding a memo entry each time. This key is the body-relative state,
    /// which a move or turn of the body leaves unchanged; Unity regenerates for a sub-collider moved relative to its body (Paths4a2 Y / Y2). A body
    /// tilted about X / Y after generation does not regenerate here (untested in Unity).</summary>
    static ulong RegenKey(NCollider2D comp)
    {
        ulong h = 14695981039346656037ul;
        void Mix(long v) { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211ul; } }
        void MixF(float v) => Mix(BitConverter.SingleToInt32Bits(v));
        MixF((float)comp.OffsetDistance); MixF((float)comp.VertexDistance);
        var tb = BodyTransformOf(comp);
        if (tb == null) return h;
        var ls = tb.LossyScale; MixF(ls.x); MixF(ls.y);
        int n = 0;
        ForEachUsed(comp, tb, (c, t) =>
        {
            n++; Mix(c.InstanceId); Mix((int)c.Kind); MixF(c.OffsetX); MixF(c.OffsetY); MixF(c.SizeX); MixF(c.SizeY); MixF(c.Radius);
            var r = RelChain(t, tb); MixF(r.m00); MixF(r.m01); MixF(r.m03); MixF(r.m10); MixF(r.m11); MixF(r.m13);
            if (c.Paths == null) { Mix(-1); return; }
            Mix(c.Paths.Length);
            foreach (var p in c.Paths) { if (p == null) { Mix(-1); continue; } Mix(p.Length); foreach (var v in p) MixF(v); }
        });
        Mix(n);
        return h;
    }

    /// <summary>The memo key of a composite's inputs and parameters (FNV-1a over the IntPoints, the offset and the vertex distance).</summary>
    static ulong InputHash(List<long[]> ins, double off, double cl)
    {
        ulong h = 14695981039346656037ul;
        void Mix(long v) { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211ul; } }
        Mix(BitConverter.DoubleToInt64Bits(off)); Mix(BitConverter.DoubleToInt64Bits(cl)); Mix(ins.Count);
        foreach (var p in ins) { Mix(p.Length); foreach (var v in p) Mix(v); }
        return h;
    }

    /// <summary>Physics Stage 4a: a Synchronous composite's regeneration (PhysWorld.SubColliderChanged / AfterReconcile4): it generates when its
    /// body-relative inputs (<see cref="RegenKey"/>, 4b review) changed since the last generation, or always when <paramref name="force"/>
    /// (set_geometryType). A composite loaded from data that never generated takes its serialized paths as the current inputs' (no generation until
    /// they change).</summary>
    public static void Refresh(NCollider2D comp, bool force)
    {
        if (comp == null || comp.Destroyed) return;
        if (!force)
        {
            ulong h;
            if (PhysWorld.Sim4) h = RegenKey(comp);
            else { var ins = Inputs(comp, out _); h = InputHash(ins, Math.Round(comp.OffsetDistance * 1e7), Math.Round(comp.VertexDistance * 1e7)); }   // (not reached: Refresh's callers are level 4+)
            if (!comp.GenHashSet && comp.FromData && comp.GenVersion == 0 && comp.Paths != null) { comp.GenHash = h; comp.GenHashSet = true; return; }
            if (comp.GenHashSet && h == comp.GenHash) return;
        }
        PhysWorld.SyncRegens++;
        Generate(comp);
    }

    /// <summary>CompositeCollider2D.GenerateGeometry: the pipeline on the current inputs; the composite's shapes are rebuilt at the next flush.</summary>
    public static void Generate(NCollider2D comp)
    {
        Generations++;
        var ins = Inputs(comp, out int n);
        bool u4 = PhysWorld.Sim4;   // Stage 4b: Unity's pipeline and float conversions (below level 4 Stage 1's)
        double off = u4 ? (float)comp.OffsetDistance * 1e7f : Math.Round(comp.OffsetDistance * 1e7), cl = u4 ? (float)comp.VertexDistance * 1e7f : Math.Round(comp.VertexDistance * 1e7);
        ulong h = InputHash(ins, off, cl);
        comp.GenHash = u4 ? RegenKey(comp) : h; comp.GenHashSet = true;   // (4b review: Refresh compares the body-relative key at level 4+)
        float[][] outp = null; bool hit = false;
        if (_memo.TryGetValue(h, out var l))
            foreach (var e in l)
                if (e.Off == off && e.Clean == cl && e.In.Length == ins.Count && e.In.Zip(ins).All(z => z.First.AsSpan().SequenceEqual(z.Second))) { outp = e.Out; hit = true; MemoHits++; break; }
        if (outp == null)
        {
            var r = ins.Count == 0 ? Array.Empty<long[]>() : u4 ? RunUnity(ins, off, cl) : Run(ins, off, cl);
            if (r == null) return;   // (logged by Bind)
            outp = u4 ? ToFloatUnity(r) : ToFloat(r);
            MemoAdd(h, new MemoEntry { In = ins.ToArray(), Off = off, Clean = cl, Out = outp });
        }
        InputPaths += ins.Count; OutputPaths += outp.Length;
        string vs = "";
        if (comp.ColliderPaths != null && comp.ColliderPaths.Length > 0 && !hit)
        {   // the plan's in-data oracle on live game data: our inputs against Unity's own serialized m_ColliderPaths
            // each of Unity's paths against the closest of ours (the collection order is not compared: H-STARTVTX / Stage 4)
            int exactPaths = 0, pts = 0, same = 0; long worst = 0; var order = new List<int>(); var shifts = new List<string>();
            foreach (var u in comp.ColliderPaths)
            {
                int bi = -1; long bw = long.MaxValue; int bs = 0;
                for (int k = 0; k < ins.Count; k++)
                {
                    if (ins[k].Length != u.Length) continue;
                    long w = 0; int sm = 0;
                    for (int i = 0; i < u.Length; i++) { long d = Math.Abs(ins[k][i] - u[i]); if (d == 0) sm++; if (d > w) w = d; }
                    if (w < bw) { bw = w; bi = k; bs = sm; }
                }
                order.Add(bi);
                if (bi < 0) continue;
                pts += u.Length; same += bs; if (bw == 0) exactPaths++; if (bw > worst) worst = bw;
                if (bw != 0)
                {   // a stale bake moved as a whole (the room generator places the pieces at run time)? the residual after removing the mean shift
                    long sx = 0, sy = 0; int np = u.Length / 2;
                    for (int i = 0; i < np; i++) { sx += ins[bi][2 * i] - u[2 * i]; sy += ins[bi][2 * i + 1] - u[2 * i + 1]; }
                    double mx = (double)sx / np, my = (double)sy / np, res = 0;
                    for (int i = 0; i < np; i++) res = Math.Max(res, Math.Max(Math.Abs(ins[bi][2 * i] - u[2 * i] - mx), Math.Abs(ins[bi][2 * i + 1] - u[2 * i + 1] - my)));
                    shifts.Add($"({mx / 1e7:F4},{my / 1e7:F4}) res {res:F0}");
                }
            }
            vs = $"; inputs vs Unity's serialized m_ColliderPaths: {exactPaths} of {comp.ColliderPaths.Length} paths identical, {same} of {pts} coordinates exact, worst {worst} (1e-7 u); " +
                 $"Unity's order = ours {string.Join(",", order)}{(shifts.Count > 0 ? "; the others are shifted as a whole by " + string.Join(", ", shifts) + " (residual, 1e-7 u)" : "")}";
            if (comp.Paths != null && comp.Paths.Length > 0) vs += $"; output vs Unity's m_CompositePaths: {Compare(outp, comp.Paths)}";
            LastInputCheck = vs;
        }
        comp.GenPaths = outp; comp.GenGeometry = comp.GeometryType; comp.GenVersion++;
        PhysWorld.Changed(comp);
        Dump(comp, outp, h);
        if (Environment.GetEnvironmentVariable("SHEPHERD_PHYS_DUMP") == "1" && !hit)
            for (int k = 0; k < ins.Count; k++)
                Log.Info($"[phys] composite '{comp.Go?.Name}' input {k} (composite space, 1e-7 u):{string.Concat(Enumerable.Range(0, ins[k].Length / 2).Select(i => $" ({ins[k][2 * i]},{ins[k][2 * i + 1]})"))}" +
                         (comp.ColliderPaths != null && k < comp.ColliderPaths.Length ? $" | Unity's [{k}]:{string.Concat(Enumerable.Range(0, comp.ColliderPaths[k].Length / 2).Select(i => $" ({comp.ColliderPaths[k][2 * i]},{comp.ColliderPaths[k][2 * i + 1]})"))}" : ""));
        Log.Info($"[phys] composite '{comp.Go?.Name}' GenerateGeometry: {n} colliders, {ins.Count} input paths ({ins.Sum(p => p.Length / 2)} points) -> " +
                 $"{outp.Length} paths ({string.Join(",", outp.Select(p => p.Length / 2))} points), {(comp.GeometryType == 1 ? "Polygons" : "Outlines")}{(hit ? " (memo)" : "")}{vs}");
    }
    public static string LastInputCheck = "";
    static readonly HashSet<ulong> _dumped = new();
    /// <summary>SHEPHERD_PHYS_DUMP=1: each distinct generated geometry once, in world space (owner-local logs only: game-derived numbers).</summary>
    static void Dump(NCollider2D comp, float[][] outp, ulong h)
    {
        if (Environment.GetEnvironmentVariable("SHEPHERD_PHYS_DUMP") != "1" || !_dumped.Add(h)) return;
        var m = comp.Go.Transform.LocalToWorld;
        for (int k = 0; k < outp.Length; k++)
        {
            var p = outp[k]; var sb = new System.Text.StringBuilder();
            for (int i = 0; i + 1 < p.Length; i += 2) sb.Append($" ({m.m00 * p[i] + m.m01 * p[i + 1] + m.m03:F3},{m.m10 * p[i] + m.m11 * p[i + 1] + m.m13:F3})");
            Log.Info($"[phys] composite '{comp.Go?.Name}' path {k} (world):{sb}");
        }
    }

    /// <summary>Unity's paths against ours (plan Stage 1 acceptance #2): each Unity path matched to its nearest path here (symmetric Hausdorff,
    /// max-norm); counts, point counts, direction (the next point after the matched start), the worst Hausdorff distance.</summary>
    public static string Compare(float[][] ours, float[][] unity) => Compare(ours, unity, out _, out _, out _);
    public static string Compare(float[][] ours, float[][] unity, out double worst, out bool countsEqual, out bool dirEqual)
    {
        worst = 0; countsEqual = ours.Length == unity.Length; dirEqual = true;
        var parts = new List<string>();
        for (int k = 0; k < unity.Length; k++)
        {
            var u = unity[k]; int nu = u.Length / 2; int best = -1; double bh = double.MaxValue;
            for (int s = 0; s < ours.Length; s++)
            {
                var c = ours[s]; int nc = c.Length / 2; double h = 0;
                for (int i = 0; i < nu; i++) { double m = double.MaxValue; for (int j = 0; j < nc; j++) m = Math.Min(m, Math.Max(Math.Abs(c[2 * j] - u[2 * i]), Math.Abs(c[2 * j + 1] - u[2 * i + 1]))); h = Math.Max(h, m); }
                for (int j = 0; j < nc; j++) { double m = double.MaxValue; for (int i = 0; i < nu; i++) m = Math.Min(m, Math.Max(Math.Abs(c[2 * j] - u[2 * i]), Math.Abs(c[2 * j + 1] - u[2 * i + 1]))); h = Math.Max(h, m); }
                if (h < bh) { bh = h; best = s; }
            }
            if (best < 0) { parts.Add($"u{k}: none"); countsEqual = false; continue; }
            var cc = ours[best]; int ncc = cc.Length / 2;
            if (ncc != nu) countsEqual = false;
            int start = 0; double sd = double.MaxValue;
            for (int j = 0; j < ncc; j++) { double d = Math.Abs(cc[2 * j] - u[0]) + Math.Abs(cc[2 * j + 1] - u[1]); if (d < sd) { sd = d; start = j; } }
            int nx = (start + 1) % ncc;
            bool same = nu > 1 && Math.Abs(cc[2 * nx] - u[2]) + Math.Abs(cc[2 * nx + 1] - u[3]) < 1e-3;
            if (!same) dirEqual = false;
            worst = Math.Max(worst, bh);
            parts.Add($"u{k}->s{best} n={nu}/{ncc} hausd={bh:E1} start={start} dir={(same ? "same" : "?")}");
        }
        return $"{unity.Length} Unity paths / {ours.Length} ours: " + string.Join(" | ", parts);
    }
}
