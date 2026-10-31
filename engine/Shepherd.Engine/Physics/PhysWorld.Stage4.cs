using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 4, step 4a (physics-plan-v2 section 4 "Stage 4", section 2 "Stage 4"; vita/notes/physics-stage4.md): at SHEPHERD_PHYS=4
/// (and 6: the levels are cumulative) the path icalls of PolygonCollider2D, EdgeCollider2D and CompositeCollider2D (Physics/Physics2DPaths.cs) and
/// the rules the Unity 2022.3 oracle measured for them on primitives (physics-work/e1, scenes Paths4a / Paths4a2, owner-local):
/// <list type="bullet">
/// <item><b>Chain shapes</b>: Unity's vertex filter (native SP_USF_CHAIN_WELD, ABI 8) on EdgeCollider2D chains (a point within sqrt(2.01) x
/// b2_linearSlop of the last point kept is dropped) and on CompositeCollider2D outlines (within b2_linearSlop), in body space; the stored points
/// are never filtered (the getters return what was set).</item>
/// <item><b>Composite paths</b> are in the body's space with the body transform's scale baked in (its rotation and position left out) -- the
/// inputs too (Physics/Composite.cs) -- and the composite's own offset is added to the pipeline's output as a float add (it does not reach the
/// union); the composite's shapes are those paths mapped into the core's body frame (<see cref="UnityToCoreBody"/>: the identity on unrotated and
/// Z-rotated bodies; a later scale change of the body leaves them).</item>
/// <item><b>Synchronous composites</b> (GenerationType 0, every runtime AddComponent; the game's 305 are all Manual) regenerate at once when a
/// sub-collider's path / offset / size / usedByComposite / enabled changes or the geometry type is set, and at the next flush (the transform
/// sync) when a sub-collider moved; Manual ones only in GenerateGeometry (and set_geometryType takes effect there).</item>
/// <item><b>usedByComposite</b> is refused for a Circle / Edge / Capsule / Composite collider (Unity's warning, the flag stays false).</item>
/// </list>
/// Below level 4 nothing here is reached (<see cref="Sim4"/> false): the Stage 4 icalls stay "missing, log once", composites keep Stage 1's
/// space, chains are built from every point.</summary>
public static partial class PhysWorld
{
    /// <summary>Level 4 or above and the core binds (Stage 4 step 4a).</summary>
    public static bool Sim4 => Sim3 && PhysConfig.Level >= 4;

    /// <summary>Can this collider kind be used by a composite (Unity 2022.3: Box and Polygon -- Tilemap at Stage 4c)?</summary>
    internal static bool Compositable(PhysRecords.Kind2D k) => k is PhysRecords.Kind2D.Box or PhysRecords.Kind2D.Polygon or PhysRecords.Kind2D.Tilemap;

    /// <summary>The CompositeCollider2D on the GameObject of c's attached body (the composite that would take c), or null.</summary>
    internal static NCollider2D CompositeOf(NCollider2D c)
    {
        var b = AttachedRecord(c);
        if (b?.Go == null) return null;
        foreach (var k in b.Go.Components) if (k is NCollider2D x && !x.Destroyed && x.Kind == PhysRecords.Kind2D.Composite) return x;
        return null;
    }

    /// <summary>A sub-collider changed through the API (a path, points, offset, size, usedByComposite, enabled): a Synchronous composite on its body
    /// regenerates now (Unity: the composite's paths and shapes follow inside the call -- Paths4a C1).</summary>
    internal static void SubColliderChanged(NCollider2D c)
    {
        if (!Sim4 || c == null) return;
        if (CompositeOf(c) is { GenerationType: 0 } comp && !ReferenceEquals(comp, c)) Composite.Refresh(comp, false);   // (when its inputs moved: a no-op set rebuilds nothing)
    }

    /// <summary>Lifecycle.AddComponent (run time only: not a scene load, not Instantiate): a CompositeCollider2D added at run time is Synchronous and
    /// generates at once from the used colliders already on its body (Paths4a2 Y_flag_first: pathCount 1 right after AddComponent; none: 0).</summary>
    public static void AddedAtRuntime(NComponent c)
    {
        if (Sim4 && c is NCollider2D { Kind: PhysRecords.Kind2D.Composite, GenerationType: 0 } comp) Composite.Refresh(comp, false);
    }

    /// <summary>The flush (ProcessPending, after the colliders were reconciled): a Synchronous composite whose sub-colliders were queued (a moved
    /// transform, an activation) or that joined itself regenerates when its inputs changed (Unity: a moved sub-collider is picked up by the
    /// transform sync -- Physics2D.SyncTransforms, a query's auto-sync or the step; Paths4a2 Y / Y2).</summary>
    static readonly List<NCollider2D> _syncComps = new();
    static void AfterReconcile4(List<NObj> batch)
    {
        _syncComps.Clear();
        foreach (var o in batch)
        {
            if (o is not NCollider2D c || c.Destroyed) continue;
            var comp = c.Kind == PhysRecords.Kind2D.Composite ? c : c.UsedByComposite ? CompositeOf(c) : null;
            if (comp is { GenerationType: 0, Destroyed: false } && !_syncComps.Contains(comp)) _syncComps.Add(comp);
        }
        foreach (var comp in _syncComps) Composite.Refresh(comp, false);
        _syncComps.Clear();
    }

    static float Atan2C(float y, float x) => _core != null ? _core.Atan2(y, x) : MathF.Atan2(y, x);
    static float CosC(float a) => _core != null ? _core.Cos(a) : MathF.Cos(a);
    static float SinC(float a) => _core != null ? _core.Sin(a) : MathF.Sin(a);
    /// <summary>Stage 4b: Unity's body angle as <see cref="UnityBodyInverseTR"/> takes it -- 2 atan2(q.z, q.w) of the body transform's world
    /// rotation, exactly 0 for a transform without rotation (q.x = q.y = q.z = 0).</summary>
    static float UnityBodyAngle(in Quaternion q) => q.x != 0f || q.y != 0f || q.z != 0f ? 2f * Atan2C(q.z, q.w) : 0f;

    /// <summary>Stage 4b review: the map K from Unity's body space (the frame of <see cref="UnityBodyInverseTR"/>: angle 2 atan2(q.z, q.w)) to the
    /// core's body space (the angle sp_ubody_create / sp_u_sync give the body: atan2(m10, m00) of the transform's world matrix). Pieces and composite
    /// paths are built in Unity's frame (their bits are Unity's); the core places them at its own angle, so a body-space point p is K p there,
    /// K = R(unity angle - core angle):
    /// <list type="bullet">
    /// <item><b>identity</b> when the two angles agree within <see cref="KTol"/> -- an unrotated body, a pure Z rotation (the angles then differ
    /// only by rounding): the pieces' bits go to the core unchanged (unit4b's bodies, the game's composites);</item>
    /// <item><b>-I</b> (an exact negation) when they are 180 degrees apart -- a (-1, 1)-mirrored body (Paths4a2 S_mirror: Unity's angle 0, the
    /// matrix's 180) or a Y flip, rotation (0, 180, 0) (Unity's angle 0 or 360, the matrix's 180);</item>
    /// <item><b>the rotation</b> otherwise -- a body tilted about X or Y (euler (30, 0, 45): Unity's 45 degrees, the matrix's 40.9), a body under a
    /// non-uniformly scaled parent.</item>
    /// </list>
    /// Either way the world shape at creation is the collider's localToWorld image, as Unity's is (its body and its body-space shapes share one
    /// angle). Which angle Unity's Rigidbody2D itself has on a tilted / Y-flipped body (rb.rotation) is untested (physics-stage4.md section 5).</summary>
    internal readonly struct BodyMap
    {
        public readonly byte Kind;   // 0 identity, 1 negate, 2 rotation
        public readonly float K00, K01, K10, K11;
        public BodyMap(byte kind, float k00, float k01, float k10, float k11) { Kind = kind; K00 = k00; K01 = k01; K10 = k10; K11 = k11; }
        public static readonly BodyMap Identity = new(0, 1f, 0f, 0f, 1f), Negate = new(1, -1f, 0f, 0f, -1f);
        public void Apply(float[] xy)
        {
            if (Kind == 1) { for (int i = 0; i < xy.Length; i++) xy[i] = -xy[i]; return; }
            if (Kind != 2) return;
            for (int i = 0; i + 1 < xy.Length; i += 2) { float x = xy[i], y = xy[i + 1]; xy[i] = K00 * x + K01 * y; xy[i + 1] = K10 * x + K11 * y; }
        }
    }
    /// <summary>The angle difference below which Unity's and the core's body angles count as one (rad): rounding of a pure Z rotation is a few
    /// 1e-7 (2 atan2 of the quaternion vs atan2 of the matrix built from it); a tilt that small leaves at most 1e-5 rad of rotation unmapped.</summary>
    internal const float KTol = 1e-5f;
    internal static BodyMap UnityToCoreBody(NTransform tb)
    {
        if (tb == null) return BodyMap.Identity;
        var w = tb.LocalToWorld;
        float d = UnityBodyAngle(tb.Rotation) - Atan2C(w.m10, w.m00);
        while (d > MathF.PI) d -= 2f * MathF.PI;
        while (d <= -MathF.PI) d += 2f * MathF.PI;
        if (MathF.Abs(d) <= KTol) return BodyMap.Identity;
        if (MathF.PI - MathF.Abs(d) <= KTol) return BodyMap.Negate;
        float c = CosC(d), s = SinC(d);
        return new BodyMap(2, c, -s, s, c);
    }

    /// <summary>Stage 4b: the inverse of a body's translation x rotation in Unity's float form, as a matrix to put in front of a collider's
    /// localToWorld (Unity's body space: the body transform's scale and mirror stay in it). The rotation is the body angle's cos / sin (the angle
    /// 2 atan2(q.z, q.w) of the transform's world rotation; the core's deterministic atan2 / sin / cos); a transform without rotation (q.x = q.y =
    /// q.z = 0) takes exactly 1 and 0. Rows: [c s 0 -(c px + s py)], [-s c 0 -(-s px + c py)], [0 0 1 0], [0 0 0 1]. Measured: the game's 8
    /// composites with serialized inputs (unrotated bodies) and the E1 oracle's shapes on rotated bodies (Paths4b B_*_rot30, bit for bit
    /// against the x64 player; the arm64 player differs by one ulp there: its transform math fuses multiply-adds). The core's body may sit at
    /// another angle (a mirrored, Y-flipped or tilted body): <see cref="UnityToCoreBody"/> maps the results into its space.</summary>
    internal static Matrix4x4 UnityBodyInverseTR(NTransform tb)
    {
        var w = tb.LocalToWorld; var q = tb.Rotation;
        float c = 1f, s = 0f;
        if (q.x != 0f || q.y != 0f || q.z != 0f)
        {
            float a = UnityBodyAngle(q);
            c = CosC(a); s = SinC(a);
        }
        float px = w.m03, py = w.m13;
        var m = new Matrix4x4();
        m.m00 = c; m.m01 = s; m.m10 = -s; m.m11 = c; m.m22 = 1f; m.m33 = 1f;
        m.m03 = -(m.m00 * px + m.m01 * py); m.m13 = -(m.m10 * px + m.m11 * py);
        return m;
    }

    /// <summary>Stage 4b: Unity's coordinate snap in a polygon collider's body-space points: a coordinate whose magnitude is below 10 x FLT_EPSILON
    /// (1.1920929e-6) reads 0 (Paths4b3 / 4b4 / 4b5: 1.1e-6 snapped, 1.2e-6 kept, on bodies and on the ground body alike; before the decomposition:
    /// a snapped vertex changes libtess2's pieces). The exact boundary value is untested.</summary>
    const float SnapEps = 1.1920929e-6f;
    static float Snap(float v) => MathF.Abs(v) < SnapEps ? 0f : v;

    /// <summary>Stage 4b: a PolygonCollider2D's paths in Unity's body space -- the ground body: (p + offset) through the collider's localToWorld;
    /// on a body: through <see cref="UnityBodyInverseTR"/> x localToWorld -- each coordinate snapped (<see cref="Snap"/>), as Unity's convex
    /// decomposition sees them (x' = m00 x + m01 y + m03, float).</summary>
    internal static float[][] UnityPolygonBodySpace(NCollider2D c, NTransform bodyT)
    {
        var src = c.Paths;
        if (src == null) return Array.Empty<float[]>();
        var lt = c.Go.Transform.LocalToWorld;
        var m = bodyT == null ? lt : M.Mul(UnityBodyInverseTR(bodyT), lt);
        var r = new float[src.Length][];
        for (int k = 0; k < src.Length; k++)
        {
            var p = src[k] ?? Array.Empty<float>(); var q = new float[p.Length];
            for (int i = 0; i + 1 < p.Length; i += 2)
            {
                float x = p[i] + c.OffsetX, y = p[i + 1] + c.OffsetY;
                q[i] = Snap(m.m00 * x + m.m01 * y + m.m03); q[i + 1] = Snap(m.m10 * x + m.m11 * y + m.m13);
            }
            r[k] = q;
        }
        return r;
    }

    /// <summary>Stage 4b (H-DECOMP): Unity's convex pieces of body-space contours through the owner-local decomposer (Physics/DerivedPhysics.cs:
    /// libtess2, ODD winding, at most 8 vertices, libtess2's merge order), decomposed in Unity's body frame and each piece then mapped into the
    /// core's (<see cref="UnityToCoreBody"/>: the identity for unrotated and Z-rotated bodies, a negation for mirrored / Y-flipped ones, a rotation
    /// for tilted ones); null when no decomposer is installed (Stage 1's native decomposition then).</summary>
    internal static float[][] UnityPieces(float[][] contours, in BodyMap k)
    {
        var d = DerivedPhysics.Decomposer;
        if (d == null) return null;
        var pieces = d.Decompose(contours);
        DecompCalls++; DecompPieces += pieces.Length;
        if (k.Kind != 0) { foreach (var p in pieces) if (p != null) k.Apply(p); FrameMapped++; }
        return pieces;
    }
    public static long DecompCalls, DecompPieces, FrameMapped, DelaunayIgnored;
    /// <summary>(physcheck) A body's pose as the core holds it -- position and angle in radians, after a flush; false when it is not in the world.</summary>
    internal static unsafe bool CorePose(NRigidbody2D b, out float x, out float y, out float a)
    {
        FlushIfNeeded();
        x = y = a = 0f;
        if (b?.PB == null || !_up) return false;
        var m = _core.MirrorAt(b.PB.Mirror); x = m->X; y = m->Y; a = m->Angle;
        return true;
    }
    /// <summary>Stage 4b (physcheck): a collider's polygon shapes as the core holds them (Box2D's body-space vertices, sp_shape_get_polygon), in
    /// creation order; other shape kinds left out.</summary>
    internal static Vector2[][] ShapePolygons(NCollider2D c)
    {
        if (c?.PS == null || _core == null) return Array.Empty<Vector2[]>();
        var r = new List<Vector2[]>(); var buf = new float[16];
        foreach (var h in c.PS.Handles)
        {
            int n = _core.ShapeGetPolygon(_world, h, buf);
            if (n <= 0) continue;
            var p = new Vector2[Math.Min(n, 8)];
            for (int i = 0; i < p.Length; i++) p[i] = PG.V2(buf[2 * i], buf[2 * i + 1]);   // kit #9 glue math rule
            r.Add(p);
        }
        return r.ToArray();
    }

    /// <summary>The composite's paths as Unity's API and shapes see them (Stage 4a): the generated (or serialized) paths plus the composite's
    /// offset, each coordinate one float add (Paths4a2 S_unit_off_odd = S_unit + offset bit for bit; a polygon offset of the same value gives other
    /// bits: the composite offset does not reach the union). Cached per source array and offset.</summary>
    internal static float[][] CompositeApiPaths(NCollider2D c)
    {
        var src = c.GenPaths ?? c.Paths;
        if (src == null) return Array.Empty<float[]>();
        if (ReferenceEquals(c.ApiSrc, src) && Bits(c.ApiOx) == Bits(c.OffsetX) && Bits(c.ApiOy) == Bits(c.OffsetY)) return c.ApiPaths;
        var r = new float[src.Length][];
        for (int k = 0; k < src.Length; k++)
        {
            var p = src[k] ?? Array.Empty<float>(); var q = new float[p.Length];
            for (int i = 0; i + 1 < p.Length; i += 2) { q[i] = p[i] + c.OffsetX; q[i + 1] = p[i + 1] + c.OffsetY; }
            r[k] = q;
        }
        c.ApiSrc = src; c.ApiOx = c.OffsetX; c.ApiOy = c.OffsetY; c.ApiPaths = r;
        return r;
    }

    /// <summary>The summary's count of outline vertices Unity's loop filter drops (the native SP_USF_CHAIN_WELD rule for a loop, recomputed here on
    /// the body-space points the core gets: b2_linearSlop^2 = 2.5e-5, the last-kept rule, no wrap test) -- what the filter changed in a run.</summary>
    static void CountLoopDrops(float[][] paths)
    {
        const float t2 = 0.005f * 0.005f;
        foreach (var p in paths)
        {
            if (p == null || p.Length < 6) continue;
            float lx = p[0], ly = p[1];
            for (int i = 2; i + 1 < p.Length; i += 2)
            {
                float dx = p[i] - lx, dy = p[i + 1] - ly;
                if (dx * dx + dy * dy > t2) { lx = p[i]; ly = p[i + 1]; } else LoopDrops++;
            }
        }
        LoopShapes++;
    }

    // counters for the summary line
    public static long PathSets, PathReads, SyncRegens, LoopShapes, LoopDrops;
    internal static string Stage4Summary() => $"[phys] stage 4a: {PathSets} path / point sets, {PathReads} path reads, {SyncRegens} Synchronous composite regenerations, " +
                                              $"{Composite.Generations} GenerateGeometry ({Composite.MemoHits} from the memo), {Composite.Refused} usedByComposite refusals; " +
                                              $"outline loops built {LoopShapes}, vertices Unity's loop filter dropped {LoopDrops}; stage 4b: {DecompCalls} decompositions -> {DecompPieces} pieces " +
                                              $"(decomposer {DerivedPhysics.Status}), {FrameMapped} mapped from Unity's body frame to the core's, {DelaunayIgnored} useDelaunayMesh shapes " +
                                              $"built without Unity's Delaunay step";
}
