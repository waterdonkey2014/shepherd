using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 2 (physics-plan-v2 section 2 "Stage 2", section 4 "Stage 2" content and acceptance #3): the query icalls, bound only
/// at SHEPHERD_PHYS >= 2 with a core (<c>Phys = 2</c>; below that every one of them stays "missing, log once" exactly as before). Each serves
/// Unity's managed wrapper as the rewritten module calls it (args in order, ref / out structs written back into their slots) from the core's
/// broadphase-direct queries (vita/physics/shim/sp_query.cpp), after the autoSync flush. Filters (ContactFilter2D): useTriggers and the layer
/// mask natively; depth (the collider GameObject's transform z, Unity's IsFilteringDepth) and normal angle (casts) here. The project's
/// queriesStartInColliders (Runtime/Physics2DSettings) decides start-inside hits (H-STARTNORMAL=-dir). Results: RaycastHit2D with the collider
/// as its instance id (RaycastHit2D.collider = FindObjectFromInstanceID, C14), arrays filled up to their length (the count stored is
/// returned), lists cleared and filled, *All arrays new each call. No method names a Unity physics type in its signature (IcallRegistry's
/// start-up scan would load the physics modules early).</summary>
[ProfileOnly]   // reads no property-bag key (typed records, the core); log text only
static class Physics2DQueries
{
    // The [Icall] entry points (static, object[] -> object: no Unity physics type in any static signature -- IcallRegistry's start-up scan of
    // every static method would load UnityEngine.Physics2DModule ahead of the game, Diag/TriggerTest.cs); the work is in Impl's instance
    // methods, which the scan does not visit.
    static readonly Impl I = new();
    [Icall("UnityEngine.ContactFilter2D::CheckConsistency_Injected", Phys = 2)] static object CheckConsistency(object[] a) => I.CheckConsistency(a);
    [Icall("UnityEngine.ContactFilter2D::IsFilteringNormalAngle_Injected", Phys = 2)] static object IsFilteringNormal(object[] a) => I.IsFilteringNormal(a);
    [Icall("UnityEngine.ContactFilter2D::IsFilteringNormalAngleUsingAngle_Injected", Phys = 2)] static object IsFilteringAngle(object[] a) => I.IsFilteringAngle(a);
    [Icall("UnityEngine.PhysicsScene2D::Raycast_Internal_Injected", Phys = 2)] static object Raycast(object[] a) => I.Raycast(a);
    [Icall("UnityEngine.PhysicsScene2D::RaycastArray_Internal_Injected", Phys = 2)] static object RaycastArray(object[] a) => I.RaycastArray(a);
    [Icall("UnityEngine.PhysicsScene2D::RaycastList_Internal_Injected", Phys = 2)] static object RaycastList(object[] a) => I.RaycastList(a);
    [Icall("UnityEngine.PhysicsScene2D::Linecast_Internal_Injected", Phys = 2)] static object Linecast(object[] a) => I.Linecast(a);
    [Icall("UnityEngine.PhysicsScene2D::LinecastArray_Internal_Injected", Phys = 2)] static object LinecastArray(object[] a) => I.LinecastArray(a);
    [Icall("UnityEngine.PhysicsScene2D::LinecastNonAllocList_Internal_Injected", Phys = 2)] static object LinecastList(object[] a) => I.LinecastList(a);
    [Icall("UnityEngine.Physics2D::RaycastAll_Internal_Injected", Phys = 2)] static object RaycastAll(object[] a) => I.RaycastAll(a);
    [Icall("UnityEngine.Physics2D::LinecastAll_Internal_Injected", Phys = 2)] static object LinecastAll(object[] a) => I.LinecastAll(a);
    [Icall("UnityEngine.PhysicsScene2D::CircleCast_Internal_Injected", Phys = 2)] static object CircleCast(object[] a) => I.CircleCast(a);
    [Icall("UnityEngine.PhysicsScene2D::CircleCastArray_Internal_Injected", Phys = 2)] static object CircleCastArray(object[] a) => I.CircleCastArray(a);
    [Icall("UnityEngine.PhysicsScene2D::CircleCastList_Internal_Injected", Phys = 2)] static object CircleCastList(object[] a) => I.CircleCastList(a);
    [Icall("UnityEngine.Physics2D::CircleCastAll_Internal_Injected", Phys = 2)] static object CircleCastAll(object[] a) => I.CircleCastAll(a);
    [Icall("UnityEngine.PhysicsScene2D::BoxCast_Internal_Injected", Phys = 2)] static object BoxCast(object[] a) => I.BoxCast(a);
    [Icall("UnityEngine.PhysicsScene2D::BoxCastArray_Internal_Injected", Phys = 2)] static object BoxCastArray(object[] a) => I.BoxCastArray(a);
    [Icall("UnityEngine.PhysicsScene2D::BoxCastList_Internal_Injected", Phys = 2)] static object BoxCastList(object[] a) => I.BoxCastList(a);
    [Icall("UnityEngine.Physics2D::BoxCastAll_Internal_Injected", Phys = 2)] static object BoxCastAll(object[] a) => I.BoxCastAll(a);
    [Icall("UnityEngine.PhysicsScene2D::CapsuleCast_Internal_Injected", Phys = 2)] static object CapsuleCast(object[] a) => I.CapsuleCast(a);
    [Icall("UnityEngine.PhysicsScene2D::CapsuleCastArray_Internal_Injected", Phys = 2)] static object CapsuleCastArray(object[] a) => I.CapsuleCastArray(a);
    [Icall("UnityEngine.PhysicsScene2D::CapsuleCastList_Internal_Injected", Phys = 2)] static object CapsuleCastList(object[] a) => I.CapsuleCastList(a);
    [Icall("UnityEngine.Physics2D::CapsuleCastAll_Internal_Injected", Phys = 2)] static object CapsuleCastAll(object[] a) => I.CapsuleCastAll(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapPoint_Internal_Injected", Phys = 2)] static object OverlapPoint(object[] a) => I.OverlapPoint(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapPointArray_Internal_Injected", Phys = 2)] static object OverlapPointArray(object[] a) => I.OverlapPointArray(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapPointList_Internal_Injected", Phys = 2)] static object OverlapPointList(object[] a) => I.OverlapPointList(a);
    [Icall("UnityEngine.Physics2D::OverlapPointAll_Internal_Injected", Phys = 2)] static object OverlapPointAll(object[] a) => I.OverlapPointAll(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapCircle_Internal_Injected", Phys = 2)] static object OverlapCircle(object[] a) => I.OverlapCircle(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapCircleArray_Internal_Injected", Phys = 2)] static object OverlapCircleArray(object[] a) => I.OverlapCircleArray(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapCircleList_Internal_Injected", Phys = 2)] static object OverlapCircleList(object[] a) => I.OverlapCircleList(a);
    [Icall("UnityEngine.Physics2D::OverlapCircleAll_Internal_Injected", Phys = 2)] static object OverlapCircleAll(object[] a) => I.OverlapCircleAll(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapBox_Internal_Injected", Phys = 2)] static object OverlapBox(object[] a) => I.OverlapBox(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapBoxArray_Internal_Injected", Phys = 2)] static object OverlapBoxArray(object[] a) => I.OverlapBoxArray(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapBoxList_Internal_Injected", Phys = 2)] static object OverlapBoxList(object[] a) => I.OverlapBoxList(a);
    [Icall("UnityEngine.Physics2D::OverlapBoxAll_Internal_Injected", Phys = 2)] static object OverlapBoxAll(object[] a) => I.OverlapBoxAll(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapCapsule_Internal_Injected", Phys = 2)] static object OverlapCapsule(object[] a) => I.OverlapCapsule(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapCapsuleArray_Internal_Injected", Phys = 2)] static object OverlapCapsuleArray(object[] a) => I.OverlapCapsuleArray(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapCapsuleList_Internal_Injected", Phys = 2)] static object OverlapCapsuleList(object[] a) => I.OverlapCapsuleList(a);
    [Icall("UnityEngine.Physics2D::OverlapCapsuleAll_Internal_Injected", Phys = 2)] static object OverlapCapsuleAll(object[] a) => I.OverlapCapsuleAll(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapColliderArray_Internal_Injected", Phys = 2)] static object OverlapColliderArray(object[] a) => I.OverlapColliderArray(a);
    [Icall("UnityEngine.PhysicsScene2D::OverlapColliderList_Internal_Injected", Phys = 2)] static object OverlapColliderList(object[] a) => I.OverlapColliderList(a);
    [Icall("UnityEngine.Collider2D::OverlapPoint_Injected", Phys = 2)] static object ColliderOverlapPoint(object[] a) => I.ColliderOverlapPoint(a);
    [Icall("UnityEngine.Collider2D::get_bounds_Injected", Phys = 2)] static object GetBounds(object[] a) => I.GetBounds(a);
    [Icall("UnityEngine.Physics2D::ClosestPoint_Collider_Injected", Phys = 2)] static object ClosestPoint(object[] a) => I.ClosestPoint(a);
    [Icall("UnityEngine.Physics2D::GetColliderContactsCollidersOnlyList_Injected", Phys = 2)] static object ContactsList(object[] a) => I.ContactsList(a);
    [Icall("UnityEngine.Physics2D::GetColliderContactsCollidersOnlyArray_Injected", Phys = 2)] static object ContactsArray(object[] a) => I.ContactsArray(a);
    [Icall("UnityEngine.Physics2D::IsTouching", Phys = 2)] static object IsTouching(object[] a) => I.IsTouching(a);
    [Icall("UnityEngine.Collider2D::IsTouching", Phys = 2)] static object ColliderIsTouching(object[] a) => I.ColliderIsTouching(a);
    [Icall("UnityEngine.Physics2D::IsTouchingLayers", Phys = 2)] static object IsTouchingLayers(object[] a) => I.IsTouchingLayers(a);
    [Icall("UnityEngine.Collider2D::IsTouchingLayers", Phys = 2)] static object ColliderIsTouchingLayers(object[] a) => I.ColliderIsTouchingLayers(a);
    [Icall("UnityEngine.Collider2D::get_excludeLayers_Injected", Phys = 2)] static object GetExclude(object[] a) => I.GetExclude(a);
    [Icall("UnityEngine.Collider2D::set_excludeLayers_Injected", Phys = 2)] static object SetExclude(object[] a) => I.SetExclude(a);
    [Icall("UnityEngine.Collider2D::get_includeLayers_Injected", Phys = 2)] static object GetInclude(object[] a) => I.GetInclude(a);
    [Icall("UnityEngine.Collider2D::set_includeLayers_Injected", Phys = 2)] static object SetInclude(object[] a) => I.SetInclude(a);
    [Icall("UnityEngine.Collider2D::get_callbackLayers_Injected", Phys = 2)] static object GetCallback(object[] a) => I.GetCallback(a);
    [Icall("UnityEngine.Collider2D::get_contactCaptureLayers_Injected", Phys = 2)] static object GetCapture(object[] a) => I.GetCapture(a);

    public static long Rays, Casts, Overlaps, Points, Bounds, Closest, Contacts, Filtered, Calls, BruteQueries, BruteDiffs;
    public static void FlushLog() => I.FlushLog();
    public static string BruteSummary() => $"{BruteQueries} queries re-run without the broadphase, {BruteDiffs} differences";
    public static string Summary() => $"{Calls} calls: rays {Rays}, casts {Casts}, overlaps {Overlaps}, collider point tests {Points}, bounds {Bounds}, closest points {Closest}, contact lists {Contacts}; hits removed by depth / normal-angle filters {Filtered}";

    sealed class Impl
    {
        // ---- RaycastHit2D: m_Centroid, m_Point, m_Normal, m_Distance, m_Fraction, m_Collider (instance id) -- the native layout, 36 B ----
        [StructLayout(LayoutKind.Sequential)]
        struct Hit2D { public Vector2 Centroid, Point, Normal; public float Distance, Fraction; public int Collider; }
        readonly bool LayoutOk;
        public Impl() { LayoutOk = Unsafe.SizeOf<Hit2D>() == 36 && CheckLayout(); }
        bool CheckLayout()
        {
            int n = Marshal.SizeOf<RaycastHit2D>();
            if (n != 36) Log.Error($"[phys] FAIL phys: RaycastHit2D is {n} B, expected 36 (centroid, point, normal, distance, fraction, collider id)");
            return n == 36;
        }
        RaycastHit2D ToHit(in ShepPhys.UHit h)
        {
            var x = new Hit2D { Centroid = new Vector2(h.Cx, h.Cy), Point = new Vector2(h.Px, h.Py), Normal = new Vector2(h.Nx, h.Ny), Distance = h.Distance, Fraction = h.Fraction, Collider = h.User };
            return Unsafe.As<Hit2D, RaycastHit2D>(ref x);
        }
        Collider2D Col(int user) => user != 0 && Objects.ById.TryGetValue(user, out var n) ? n.Managed as Collider2D : null;


        // ---- the engine ----
        static ShepPhys.UHit[] _hits = new ShepPhys.UHit[64];
        static int _n;
        /// <summary>Runs one query with the filter; the hits land in _hits[0.._n). single = the closest / first only.</summary>
        int Run(ShepPhys.UQuery q, in ContactFilter2D f, bool single, bool cast)
        {
            Calls++;
            _n = 0;
            if (!LayoutOk || !PhysWorld.EnsureWorld()) return 0;
            PhysWorld.Flush();
            var core = PhysWorld.Core2; int world = PhysWorld.World2;
            if (core == null) return 0;
            q.Flags |= (f.useTriggers ? ShepPhys.UqfTriggers : 0) | (Runtime.Physics2DSettings.QueriesStartInColliders ? ShepPhys.UqfStartIn : 0);
            q.LayerMask = f.useLayerMask ? unchecked((uint)f.layerMask.value) : 0xFFFFFFFFu;
            bool post = f.useDepth || (cast && f.useNormalAngle);
            if (!single || post) q.Flags |= ShepPhys.UqfAll | ShepPhys.UqfSort;
            else if (!cast) q.Flags |= ShepPhys.UqfAny;
            int max = single && !post ? 1 : _hits.Length;
            int n = core.Query(world, q, new Span<ShepPhys.UHit>(_hits, 0, max));
            if (n > max && !(single && !post)) { Array.Resize(ref _hits, n * 2); n = core.Query(world, q, _hits); }
            if (n > _hits.Length) n = _hits.Length;
            if (single && !post && n > 1) n = 1;
            if (PhysConfig.CheckBrute) Brute(core, world, q, n, single && !post);
            if (_qlog != null) QLog(q, n);
            if (post)
            {
                int k = 0;
                for (int i = 0; i < n; i++)
                {
                    var c = Col(_hits[i].User);
                    bool drop = c == null || (f.useDepth && FilteredDepth(f, c)) || (cast && f.useNormalAngle && FilteredAngle(f, new Vector2(_hits[i].Nx, _hits[i].Ny)));
                    if (drop) { Filtered++; continue; }
                    _hits[k++] = _hits[i];
                    if (single) break;
                }
                n = k;
            }
            return _n = n;
        }
        /// <summary>SHEPHERD_PHYS_QUERYLOG=&lt;file&gt;: one line per query (owner-local: game-derived positions): step, frame, kind, origin, direction,
        /// distance, hits, the first hit's collider and distance (the intro check: the guards' line-of-sight rays end at walls).</summary>
        readonly StreamWriter _qlog = OpenQ();
        static StreamWriter OpenQ()
        {
            var p = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_QUERYLOG");
            if (string.IsNullOrEmpty(p) || PhysConfig.Level < 2) return null;
            try { return new StreamWriter(p, false) { AutoFlush = false }; } catch { return null; }
        }
        void QLog(in ShepPhys.UQuery q, int n)
        {
            string first = n > 0 && Objects.ById.TryGetValue(_hits[0].User, out var c) && c is NCollider2D k ? PhysDispatch.Desc(k) : "-";
            _qlog.WriteLine($"{PhysWorld.StepIndex}\t{Runtime.Time.FrameCount}\t{q.Kind}\t0x{q.Flags:x}\t{q.Ox:R},{q.Oy:R}\t{q.Dx:R},{q.Dy:R}\t{q.Distance:R}\t{q.Radius:R}\t{n}\t{first}\t{(n > 0 ? _hits[0].Distance.ToString("R") : "-")}");
        }
        internal void FlushLog() => _qlog?.Flush();

        /// <summary>SHEPHERD_CHECK_PHYS=brute: the same query over every shape without the broadphase; the hits must agree (sets of colliders, the
        /// closest distance; an "any" query: hit or not).</summary>
        ShepPhys.UHit[] _brute = new ShepPhys.UHit[64];
        void Brute(ShepPhys core, int world, ShepPhys.UQuery q, int n, bool single)
        {
            q.Flags |= ShepPhys.UqfBrute;
            int bn = core.Query(world, q, _brute);
            if (bn > _brute.Length) { Array.Resize(ref _brute, bn * 2); bn = core.Query(world, q, _brute); }
            BruteQueries++;
            bool any = (q.Flags & ShepPhys.UqfAny) != 0, ok;
            if (any) ok = (n > 0) == (bn > 0);
            else if (single || (q.Flags & ShepPhys.UqfAll) == 0) ok = (n > 0) == (bn > 0) && (n == 0 || _hits[0].Distance == _brute[0].Distance);
            else
            {
                var a = new HashSet<int>(); for (int i = 0; i < n; i++) a.Add(_hits[i].User);
                var b = new HashSet<int>(); for (int i = 0; i < Math.Min(bn, _brute.Length); i++) b.Add(_brute[i].User);
                ok = a.SetEquals(b);
            }
            if (ok) return;
            BruteDiffs++;
            if (BruteDiffs <= 20) Log.Error($"[phys] FAIL phys brute query kind {q.Kind} flags 0x{q.Flags:x} at ({q.Ox:R}, {q.Oy:R}) dir ({q.Dx:R}, {q.Dy:R}) dist {q.Distance:R}: broadphase {n} hit(s){(n > 0 ? $" (first #{_hits[0].User} at {_hits[0].Distance:R})" : "")}, brute force {bn}{(bn > 0 ? $" (first #{_brute[0].User} at {_brute[0].Distance:R})" : "")}");
        }

        /// <summary>Unity's ContactFilter2D.IsFilteringDepth: the collider GameObject's transform z outside [min, max] (swapped when reversed), or
        /// inside with useOutsideDepth.</summary>
        bool FilteredDepth(in ContactFilter2D f, Collider2D c)
        {
            float lo = f.minDepth, hi = f.maxDepth; if (lo > hi) (lo, hi) = (hi, lo);
            var go = Objects.Get(c) is NComponent nc ? nc.Go : null;
            float z = go?.Transform?.Position.z ?? 0f;
            bool outside = z < lo || z > hi;
            return f.useOutsideDepth ? !outside : outside;
        }
        /// <summary>The normal-angle filter (unused by the game): the angle of the normal in degrees [0, 360) against [min, max] (or outside).</summary>
        bool FilteredAngle(in ContactFilter2D f, Vector2 n) => AngleFiltered(f, Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg);
        bool AngleFiltered(in ContactFilter2D f, float angle)
        {
            if (!f.useNormalAngle) return false;
            float lo = f.minNormalAngle, hi = f.maxNormalAngle;
            angle = ((angle % 360f) + 360f) % 360f;
            bool inside = lo <= hi ? (angle >= lo && angle <= hi) : (angle >= lo || angle <= hi);
            return f.useOutsideNormalAngle ? inside : !inside;
        }

        ShepPhys.UQuery Q(int kind, Vector2 o) => new ShepPhys.UQuery { Kind = kind, Ox = o.x, Oy = o.y };
        ShepPhys.UQuery Ray(Vector2 o, Vector2 dir, float dist) { var q = Q(ShepPhys.UqRay, o); q.Dx = dir.x; q.Dy = dir.y; q.Distance = dist; return q; }
        ShepPhys.UQuery Line(Vector2 a, Vector2 b) { var d = b - a; return Ray(a, d, d.magnitude); }
        ShepPhys.UQuery Circle(Vector2 o, float r, Vector2 dir, float dist, bool cast) { var q = Q(cast ? ShepPhys.UqCircleCast : ShepPhys.UqCircle, o); q.Radius = r; q.Dx = dir.x; q.Dy = dir.y; q.Distance = dist; return q; }
        ShepPhys.UQuery Box(Vector2 o, Vector2 size, float angleDeg, Vector2 dir, float dist, bool cast) { var q = Q(cast ? ShepPhys.UqBoxCast : ShepPhys.UqBox, o); q.Sx = size.x; q.Sy = size.y; q.Angle = angleDeg * Mathf.Deg2Rad; q.Dx = dir.x; q.Dy = dir.y; q.Distance = dist; return q; }
        ShepPhys.UQuery Capsule(Vector2 o, Vector2 size, object direction, float angleDeg, Vector2 dir, float dist, bool cast)
        {
            var q = Q(cast ? ShepPhys.UqCapsuleCast : ShepPhys.UqCapsule, o); q.Sx = size.x; q.Sy = size.y; q.Direction = Convert.ToInt32(direction); q.Angle = angleDeg * Mathf.Deg2Rad;
            q.Dx = dir.x; q.Dy = dir.y; q.Distance = dist; return q;
        }
        ContactFilter2D F(object o) => o is ContactFilter2D f ? f : default;
        Vector2 V(object o) => o is Vector2 v ? v : default;
        float S(object o) => o == null ? 0f : Convert.ToSingle(o);

        // ---- result shapes ----
        object One() => _n > 0 ? ToHit(_hits[0]) : default(RaycastHit2D);
        int ToArray(object arr)
        {
            if (arr is not RaycastHit2D[] r) return 0;
            int k = Math.Min(_n, r.Length);
            for (int i = 0; i < k; i++) r[i] = ToHit(_hits[i]);
            return k;
        }
        int ToList(object list)
        {
            if (list is not List<RaycastHit2D> l) return 0;
            l.Clear();
            for (int i = 0; i < _n; i++) l.Add(ToHit(_hits[i]));
            return _n;
        }
        RaycastHit2D[] ToAll() { var r = new RaycastHit2D[_n]; for (int i = 0; i < _n; i++) r[i] = ToHit(_hits[i]); return r; }
        object FirstCol() { for (int i = 0; i < _n; i++) if (Col(_hits[i].User) is { } c) return c; return null; }
        int ColArray(object arr)
        {
            if (arr is not Collider2D[] r) return 0;
            int k = 0;
            for (int i = 0; i < _n && k < r.Length; i++) if (Col(_hits[i].User) is { } c) r[k++] = c;
            return k;
        }
        int ColList(object list)
        {
            if (list is not List<Collider2D> l) return 0;
            l.Clear();
            for (int i = 0; i < _n; i++) if (Col(_hits[i].User) is { } c) l.Add(c);
            return l.Count;
        }
        Collider2D[] ColAll() { var l = new List<Collider2D>(_n); for (int i = 0; i < _n; i++) if (Col(_hits[i].User) is { } c) l.Add(c); return l.ToArray(); }

        // ================= ContactFilter2D =================
        internal object CheckConsistency(object[] a)
        {   // native: clamp the normal angles to +-359.9999 (the managed code already swaps the depths): the boxed struct written back
            if (a[0] is ContactFilter2D f)
            {
                f.minNormalAngle = Mathf.Clamp(f.minNormalAngle, -359.9999f, 359.9999f); f.maxNormalAngle = Mathf.Clamp(f.maxNormalAngle, -359.9999f, 359.9999f);
                a[0] = f;
            }
            return null;
        }
        internal object IsFilteringNormal(object[] a) { var n = V(a[1]); return AngleFiltered(F(a[0]), Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg); }
        internal object IsFilteringAngle(object[] a) => AngleFiltered(F(a[0]), S(a[1]));

        // ================= PhysicsScene2D: rays and line casts =================
        internal object Raycast(object[] a) { Rays++; Run(Ray(V(a[1]), V(a[2]), S(a[3])), F(a[4]), true, true); a[5] = One(); return null; }
        internal object RaycastArray(object[] a) { Rays++; Run(Ray(V(a[1]), V(a[2]), S(a[3])), F(a[4]), false, true); return ToArray(a[5]); }
        internal object RaycastList(object[] a) { Rays++; Run(Ray(V(a[1]), V(a[2]), S(a[3])), F(a[4]), false, true); return ToList(a[5]); }
        internal object Linecast(object[] a) { Rays++; Run(Line(V(a[1]), V(a[2])), F(a[3]), true, true); a[4] = One(); return null; }
        internal object LinecastArray(object[] a) { Rays++; Run(Line(V(a[1]), V(a[2])), F(a[3]), false, true); return ToArray(a[4]); }
        internal object LinecastList(object[] a) { Rays++; Run(Line(V(a[1]), V(a[2])), F(a[3]), false, true); return ToList(a[4]); }
        internal object RaycastAll(object[] a) { Rays++; Run(Ray(V(a[1]), V(a[2]), S(a[3])), F(a[4]), false, true); return ToAll(); }
        internal object LinecastAll(object[] a) { Rays++; Run(Line(V(a[1]), V(a[2])), F(a[3]), false, true); return ToAll(); }

        // ================= shape casts =================
        internal object CircleCast(object[] a) { Casts++; Run(Circle(V(a[1]), S(a[2]), V(a[3]), S(a[4]), true), F(a[5]), true, true); a[6] = One(); return null; }
        internal object CircleCastArray(object[] a) { Casts++; Run(Circle(V(a[1]), S(a[2]), V(a[3]), S(a[4]), true), F(a[5]), false, true); return ToArray(a[6]); }
        internal object CircleCastList(object[] a) { Casts++; Run(Circle(V(a[1]), S(a[2]), V(a[3]), S(a[4]), true), F(a[5]), false, true); return ToList(a[6]); }
        internal object CircleCastAll(object[] a) { Casts++; Run(Circle(V(a[1]), S(a[2]), V(a[3]), S(a[4]), true), F(a[5]), false, true); return ToAll(); }
        internal object BoxCast(object[] a) { Casts++; Run(Box(V(a[1]), V(a[2]), S(a[3]), V(a[4]), S(a[5]), true), F(a[6]), true, true); a[7] = One(); return null; }
        internal object BoxCastArray(object[] a) { Casts++; Run(Box(V(a[1]), V(a[2]), S(a[3]), V(a[4]), S(a[5]), true), F(a[6]), false, true); return ToArray(a[7]); }
        internal object BoxCastList(object[] a) { Casts++; Run(Box(V(a[1]), V(a[2]), S(a[3]), V(a[4]), S(a[5]), true), F(a[6]), false, true); return ToList(a[7]); }
        internal object BoxCastAll(object[] a) { Casts++; Run(Box(V(a[1]), V(a[2]), S(a[3]), V(a[4]), S(a[5]), true), F(a[6]), false, true); return ToAll(); }
        internal object CapsuleCast(object[] a) { Casts++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), V(a[5]), S(a[6]), true), F(a[7]), true, true); a[8] = One(); return null; }
        internal object CapsuleCastArray(object[] a) { Casts++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), V(a[5]), S(a[6]), true), F(a[7]), false, true); return ToArray(a[8]); }
        internal object CapsuleCastList(object[] a) { Casts++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), V(a[5]), S(a[6]), true), F(a[7]), false, true); return ToList(a[8]); }
        internal object CapsuleCastAll(object[] a) { Casts++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), V(a[5]), S(a[6]), true), F(a[7]), false, true); return ToAll(); }

        // ================= overlaps =================
        internal object OverlapPoint(object[] a) { Overlaps++; Run(Q(ShepPhys.UqPoint, V(a[1])), F(a[2]), true, false); return FirstCol(); }
        internal object OverlapPointArray(object[] a) { Overlaps++; Run(Q(ShepPhys.UqPoint, V(a[1])), F(a[2]), false, false); return ColArray(a[3]); }
        internal object OverlapPointList(object[] a) { Overlaps++; Run(Q(ShepPhys.UqPoint, V(a[1])), F(a[2]), false, false); return ColList(a[3]); }
        internal object OverlapPointAll(object[] a) { Overlaps++; Run(Q(ShepPhys.UqPoint, V(a[1])), F(a[2]), false, false); return ColAll(); }
        internal object OverlapCircle(object[] a) { Overlaps++; Run(Circle(V(a[1]), S(a[2]), default, 0f, false), F(a[3]), true, false); return FirstCol(); }
        internal object OverlapCircleArray(object[] a)
        {   // A*'s GraphCollision.Check: one per node with a 1-element dummy array -- an "any hit" query (stops at the first)
            Overlaps++;
            var arr = a[4] as Collider2D[];
            var q = Circle(V(a[1]), S(a[2]), default, 0f, false);
            var f = F(a[3]);
            if (arr != null && arr.Length == 1 && !f.useDepth) { Run(q, f, true, false); return ColArray(arr); }
            Run(q, f, false, false); return ColArray(arr);
        }
        internal object OverlapCircleList(object[] a) { Overlaps++; Run(Circle(V(a[1]), S(a[2]), default, 0f, false), F(a[3]), false, false); return ColList(a[4]); }
        internal object OverlapCircleAll(object[] a) { Overlaps++; Run(Circle(V(a[1]), S(a[2]), default, 0f, false), F(a[3]), false, false); return ColAll(); }
        internal object OverlapBox(object[] a) { Overlaps++; Run(Box(V(a[1]), V(a[2]), S(a[3]), default, 0f, false), F(a[4]), true, false); return FirstCol(); }
        internal object OverlapBoxArray(object[] a) { Overlaps++; Run(Box(V(a[1]), V(a[2]), S(a[3]), default, 0f, false), F(a[4]), false, false); return ColArray(a[5]); }
        internal object OverlapBoxList(object[] a) { Overlaps++; Run(Box(V(a[1]), V(a[2]), S(a[3]), default, 0f, false), F(a[4]), false, false); return ColList(a[5]); }
        internal object OverlapBoxAll(object[] a) { Overlaps++; Run(Box(V(a[1]), V(a[2]), S(a[3]), default, 0f, false), F(a[4]), false, false); return ColAll(); }
        internal object OverlapCapsule(object[] a) { Overlaps++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), default, 0f, false), F(a[5]), true, false); return FirstCol(); }
        internal object OverlapCapsuleArray(object[] a) { Overlaps++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), default, 0f, false), F(a[5]), false, false); return ColArray(a[6]); }
        internal object OverlapCapsuleList(object[] a) { Overlaps++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), default, 0f, false), F(a[5]), false, false); return ColList(a[6]); }
        internal object OverlapCapsuleAll(object[] a) { Overlaps++; Run(Capsule(V(a[1]), V(a[2]), a[3], S(a[4]), default, 0f, false), F(a[5]), false, false); return ColAll(); }
        /// <summary>Physics2D.OverlapCollider: the colliders overlapping this collider's shapes (itself excluded).</summary>
        int OverlapCollider(NCollider2D c, in ContactFilter2D f)
        {
            Calls++; Overlaps++; _n = 0;
            if (c == null || !LayoutOk || !PhysWorld.EnsureWorld()) return 0;
            var shapes = PhysWorld.ShapesOf(c);
            if (shapes.IsEmpty) return 0;
            var q = new ShepPhys.UQuery { Flags = f.useTriggers ? ShepPhys.UqfTriggers : 0, LayerMask = f.useLayerMask ? unchecked((uint)f.layerMask.value) : 0xFFFFFFFFu, ExcludeUser = c.InstanceId };
            var core = PhysWorld.Core2;
            int n = core.UOverlapShapes(PhysWorld.World2, shapes, q, _hits);
            if (n > _hits.Length) { Array.Resize(ref _hits, n * 2); n = core.UOverlapShapes(PhysWorld.World2, shapes, q, _hits); }
            if (f.useDepth) { int k = 0; for (int i = 0; i < n; i++) { var o = Col(_hits[i].User); if (o != null && !FilteredDepth(f, o)) _hits[k++] = _hits[i]; else Filtered++; } n = k; }
            return _n = Math.Min(n, _hits.Length);
        }
        internal object OverlapColliderArray(object[] a) { OverlapCollider(Objects.Get(a[0]) as NCollider2D, F(a[1])); return ColArray(a[2]); }
        internal object OverlapColliderList(object[] a) { OverlapCollider(Objects.Get(a[0]) as NCollider2D, F(a[1])); return ColList(a[2]); }

        // ================= one collider =================
        internal object ColliderOverlapPoint(object[] a)
        {
            Calls++; Points++;
            var c = Objects.Get(a[0]) as NCollider2D; var p = V(a[1]);
            if (c == null || !PhysWorld.EnsureWorld()) return false;
            var shapes = PhysWorld.ShapesOf(c);
            if (shapes.IsEmpty) return false;
            if (c.Kind == PhysRecords.Kind2D.Composite && (c.GenGeometry >= 0 ? c.GenGeometry : c.GeometryType) == 0) return InComposite(c, p);   // Outlines: the union's inside
            return PhysWorld.Core2.UTestPoint(PhysWorld.World2, shapes, p.x, p.y) != 0;
        }
        /// <summary>A point inside an Outlines composite: even-odd over its paths (Clipper's union output: outer paths and holes) in collider space.</summary>
        bool InComposite(NCollider2D c, Vector2 p)
        {
            var paths = c.GenPaths ?? c.Paths; var t = c.Go?.Transform;
            if (paths == null || t == null) return false;
            var m = t.LocalToWorld;
            float det = m.m00 * m.m11 - m.m01 * m.m10; if (det == 0f) return false;
            float dx = p.x - m.m03, dy = p.y - m.m13;
            float lx = (m.m11 * dx - m.m01 * dy) / det, ly = (-m.m10 * dx + m.m00 * dy) / det;
            bool inside = false;
            foreach (var path in paths)
            {
                if (path == null) continue;
                int n = path.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    float xi = path[2 * i], yi = path[2 * i + 1], xj = path[2 * j], yj = path[2 * j + 1];
                    if ((yi > ly) != (yj > ly) && lx < (xj - xi) * (ly - yi) / (yj - yi) + xi) inside = !inside;
                }
            }
            return inside;
        }
        internal object GetBounds(object[] a)
        {   // the world AABB of the collider's shapes, no Box2D skin (H-BOUNDSSKIN); z = the transform's z, no depth; empty when not in the world
            Calls++; Bounds++;
            var c = Objects.Get(a[0]) as NCollider2D;
            var b = default(Bounds);
            if (c != null && PhysWorld.EnsureWorld())
            {
                var shapes = PhysWorld.ShapesOf(c);
                if (!shapes.IsEmpty && PhysWorld.Core2.UBounds(PhysWorld.World2, shapes, out var x0, out var y0, out var x1, out var y1) > 0)
                {
                    float z = c.Go?.Transform?.Position.z ?? 0f;
                    b = new Bounds(new Vector3(0.5f * (x0 + x1), 0.5f * (y0 + y1), z), new Vector3(x1 - x0, y1 - y0, 0f));
                }
            }
            a[1] = b; return null;
        }
        internal object ClosestPoint(object[] a)
        {   // the closest point on the collider; p itself when inside; p when the collider is not in the world
            Calls++; Closest++;
            var p = V(a[0]); var c = Objects.Get(a[1]) as NCollider2D;
            var r = p;
            if (c != null && PhysWorld.EnsureWorld())
            {
                var shapes = PhysWorld.ShapesOf(c);
                if (!shapes.IsEmpty && PhysWorld.Core2.UClosest(PhysWorld.World2, shapes, p.x, p.y, out var x, out var y, out _) > 0) r = new Vector2(x, y);
            }
            a[2] = r; return null;
        }

        // ================= contacts (the last step's touching contacts) =================
        static readonly int[] _other = new int[256];
        static readonly List<NCollider2D> _touch = new();
        /// <summary>The colliders touching c in the current contact list, deduplicated, filtered (triggers, layer mask, depth); contactCaptureLayers of
        /// c limits what is captured (H-CAPTURE=report-only).</summary>
        List<NCollider2D> Touching(NCollider2D c, in ContactFilter2D f)
        {
            Calls++; Contacts++;
            _touch.Clear();
            if (c == null || !PhysWorld.EnsureWorld()) return _touch;
            var shapes = PhysWorld.ShapesOf(c);
            if (shapes.IsEmpty) return _touch;
            int n = PhysWorld.Core2.UContactsOf(PhysWorld.World2, shapes, f.useTriggers ? 0 : ShepPhys.ContactsNoSensors, _other);
            for (int i = 0; i < n; i++)
            {
                var o = PhysWorld.ColliderOfShape(_other[i]);
                if (o == null || _touch.Contains(o)) continue;
                int ol = (o.Go?.Layer ?? 0) & 31;
                if (((c.ContactCaptureLayers >> ol) & 1) == 0) continue;
                if (f.useLayerMask && ((f.layerMask.value >> ol) & 1) == 0) continue;
                if (f.useDepth && o.Managed is Collider2D oc && FilteredDepth(f, oc)) continue;
                _touch.Add(o);
            }
            return _touch;
        }
        internal object ContactsList(object[] a)
        {
            var l = a[2] as List<Collider2D>; var t = Touching(Objects.Get(a[0]) as NCollider2D, F(a[1]));
            if (l == null) return 0;
            l.Clear(); foreach (var o in t) if (o.Managed is Collider2D oc) l.Add(oc);
            return l.Count;
        }
        internal object ContactsArray(object[] a)
        {
            var r = a[2] as Collider2D[]; var t = Touching(Objects.Get(a[0]) as NCollider2D, F(a[1]));
            if (r == null) return 0;
            int k = 0; foreach (var o in t) if (k < r.Length && o.Managed is Collider2D oc) r[k++] = oc;
            return k;
        }
        ContactFilter2D NoFilter() { var f = default(ContactFilter2D); f.useTriggers = true; return f; }
        internal object IsTouching(object[] a) { var o = Objects.Get(a[1]) as NCollider2D; return o != null && Touching(Objects.Get(a[0]) as NCollider2D, NoFilter()).Contains(o); }
        internal object ColliderIsTouching(object[] a) => IsTouching(a);
        internal object IsTouchingLayers(object[] a)
        {
            int mask = Convert.ToInt32(a[1]);
            foreach (var o in Touching(Objects.Get(a[0]) as NCollider2D, NoFilter())) if (((mask >> ((o.Go?.Layer ?? 0) & 31)) & 1) != 0) return true;
            return false;
        }
        internal object ColliderIsTouchingLayers(object[] a) => IsTouchingLayers(a);

        // ================= Collider2D layer masks (re-filtered at the next flush) =================
        internal object GetExclude(object[] a) { a[1] = (LayerMask)unchecked((int)((Objects.Get(a[0]) as NCollider2D)?.ExcludeLayers ?? 0u)); return null; }
        internal object SetExclude(object[] a)
        {
            if (Objects.Get(a[0]) is NCollider2D c && a[1] is LayerMask m) { uint v = unchecked((uint)m.value); if (c.ExcludeLayers != v) { c.ExcludeLayers = v; PhysWorld.Changed(c); } }
            return null;
        }
        internal object GetInclude(object[] a) { a[1] = (LayerMask)unchecked((int)((Objects.Get(a[0]) as NCollider2D)?.IncludeLayers ?? 0u)); return null; }
        internal object SetInclude(object[] a)
        {
            if (Objects.Get(a[0]) is NCollider2D c && a[1] is LayerMask m) { uint v = unchecked((uint)m.value); if (c.IncludeLayers != v) { c.IncludeLayers = v; PhysWorld.Changed(c); } }
            return null;
        }
        internal object GetCallback(object[] a) { a[1] = (LayerMask)unchecked((int)((Objects.Get(a[0]) as NCollider2D)?.CallbackLayers ?? ~0u)); return null; }
        internal object GetCapture(object[] a) { a[1] = (LayerMask)unchecked((int)((Objects.Get(a[0]) as NCollider2D)?.ContactCaptureLayers ?? ~0u)); return null; }

    }
}
