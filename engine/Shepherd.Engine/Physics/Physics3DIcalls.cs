using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 6 (physics-plan-v2 section 2 "Stage 6"): the 3D icalls, bound only at SHEPHERD_PHYS >= 6 with a core (<c>Phys = 6</c>;
/// below that every one of them stays "missing, log once" exactly as before: the ground ray then returns no hit and units keep z = 0).
/// <list type="bullet">
/// <item>7158 <c>Physics.get_defaultPhysicsScene</c>: a constant handle in one shared box -- 0, what the unbound icall left (the game never reads
/// it: no IsValid / ToString / comparison in its code -- a search of the decompiled game assemblies); 7161 <c>Physics.get_gravity</c>: the project's (0, 0, -9.81) (C17).</item>
/// <item>7195 / 7193 / 7194 <c>PhysicsScene.Internal_Raycast / RaycastNonAlloc / RaycastTest</c>: Physics/Phys3D.Ray (the lazy sync, then the
/// native scene); RaycastHit = point, normal, faceID (the triangle; -1 for primitives), distance, barycentric uv, the collider's instance id
/// (RaycastHit.collider = FindObjectFromInstanceID, as for the 2D hits).</item>
/// <item>7149 <c>Physics.SyncTransforms</c>: the sync now.</item>
/// <item>MeshCollider <c>sharedMesh</c> (the ProBuilder ramps assign it at run time: cooked at the assignment), <c>convex</c>; Collider
/// <c>enabled</c>, <c>isTrigger</c>, <c>attachedRigidbody</c> (the nearest 3D Rigidbody up the hierarchy -- none on the main path),
/// <c>bounds</c> (the shape's world box: the local bounds through the pose and scale, PhysX's getWorldBounds without inflation, UNVERIFIED;
/// empty when the collider is not in the scene, as Unity documents), <c>ClosestPoint</c> (Box / Sphere / Capsule; a non-convex mesh returns
/// the point, Unity's warning case); Box / Sphere / Capsule getters and setters (a set re-syncs the shape at the next ray).</item>
/// </list>
/// No method names a Unity physics type in its signature (IcallRegistry's start-up scan of every static method would load
/// UnityEngine.PhysicsModule early): the typed work is in Impl's instance methods.</summary>
[ProfileOnly]   // reads typed records, never a property-bag key (Assets/Diet.cs)
static class Physics3DIcalls
{
    static readonly Impl I = new();
    static NCollider3D K(object o) => Objects.Get(o) as NCollider3D;

    // ---- Physics / PhysicsScene ----
    [Icall("UnityEngine.Physics::get_defaultPhysicsScene_Injected", Phys = 6)] static object DefaultScene(object[] a) { a[0] = I.SceneBox; return null; }
    [Icall("UnityEngine.Physics::get_gravity_Injected", Phys = 6)] static object GetGravity(object[] a) { a[0] = Phys3D.Gravity; return null; }
    [Icall("UnityEngine.Physics::SyncTransforms", Phys = 6)] static object SyncTransforms(object[] a) { Phys3D.Sync(); return null; }
    [Icall("UnityEngine.PhysicsScene::Internal_Raycast_Injected", Phys = 6)] static object Raycast(object[] a) => I.Raycast(a);
    [Icall("UnityEngine.PhysicsScene::Internal_RaycastNonAlloc_Injected", Phys = 6)] static object RaycastNonAlloc(object[] a) => I.RaycastNonAlloc(a);
    [Icall("UnityEngine.PhysicsScene::Internal_RaycastTest_Injected", Phys = 6)] static object RaycastTest(object[] a) => I.RaycastTest(a);

    // ---- MeshCollider ----
    [Icall("UnityEngine.MeshCollider::get_sharedMesh", Phys = 6)] static object GetSharedMesh(object[] a) => K(a[0]) is { } k ? MeshObject(k) : null;
    [Icall("UnityEngine.MeshCollider::set_sharedMesh", Phys = 6)]
    static object SetSharedMesh(object[] a) { if (K(a[0]) is { } k) Phys3D.MeshAssigned(k, a[1] as UnityEngine.Object); return null; }
    [Icall("UnityEngine.MeshCollider::get_convex", Phys = 6)] static object GetConvex(object[] a) => K(a[0])?.Convex ?? false;
    [Icall("UnityEngine.MeshCollider::set_convex", Phys = 6)]
    static object SetConvex(object[] a) { if (K(a[0]) is { } k) { bool v = Convert.ToBoolean(a[1]); if (k.Convex != v) { k.Convex = v; Phys3D.Queue(k); } } return null; }

    // ---- Collider ----
    [Icall("UnityEngine.Collider::get_enabled", Phys = 6)] static object GetEnabled(object[] a) => K(a[0])?.Enabled ?? false;
    [Icall("UnityEngine.Collider::set_enabled", Phys = 6)]
    static object SetEnabled(object[] a) { if (K(a[0]) is { } k) { bool v = Convert.ToBoolean(a[1]); if (k.Enabled != v) { k.Enabled = v; Phys3D.Queue(k); } } return null; }
    [Icall("UnityEngine.Collider::get_isTrigger", Phys = 6)] static object GetIsTrigger(object[] a) => K(a[0])?.IsTrigger ?? false;
    [Icall("UnityEngine.Collider::set_isTrigger", Phys = 6)]
    static object SetIsTrigger(object[] a) { if (K(a[0]) is { } k) { bool v = Convert.ToBoolean(a[1]); if (k.IsTrigger != v) { k.IsTrigger = v; Phys3D.Queue(k); } } return null; }
    [Icall("UnityEngine.Collider::get_attachedRigidbody", Phys = 6)] static object GetAttachedRigidbody(object[] a) => K(a[0]) is { } k ? AttachedBody(k) : null;
    [Icall("UnityEngine.Collider::get_bounds_Injected", Phys = 6)] static object GetBounds(object[] a) { a[1] = K(a[0]) is { } k ? WorldBounds(k) : default(Bounds); return null; }
    [Icall("UnityEngine.Collider::ClosestPoint_Injected", Phys = 6)] static object ClosestPoint(object[] a) { var p = (Vector3)a[1]; a[2] = K(a[0]) is { } k ? Closest(k, p) : p; return null; }

    // ---- Box / Sphere / Capsule ----
    [Icall("UnityEngine.BoxCollider::get_center_Injected", Phys = 6)] static object BoxGetCenter(object[] a) { var k = K(a[0]); a[1] = k != null ? new Vector3(k.CenterX, k.CenterY, k.CenterZ) : default; return null; }
    [Icall("UnityEngine.BoxCollider::set_center_Injected", Phys = 6)] static object BoxSetCenter(object[] a) { SetCenter(K(a[0]), (Vector3)a[1]); return null; }
    [Icall("UnityEngine.BoxCollider::get_size_Injected", Phys = 6)] static object BoxGetSize(object[] a) { var k = K(a[0]); a[1] = k != null ? new Vector3(k.SizeX, k.SizeY, k.SizeZ) : default; return null; }
    [Icall("UnityEngine.BoxCollider::set_size_Injected", Phys = 6)]
    static object BoxSetSize(object[] a) { if (K(a[0]) is { } k) { var v = (Vector3)a[1]; k.SizeX = v.x; k.SizeY = v.y; k.SizeZ = v.z; Phys3D.Queue(k); } return null; }
    [Icall("UnityEngine.SphereCollider::get_center_Injected", Phys = 6)] static object SphereGetCenter(object[] a) { var k = K(a[0]); a[1] = k != null ? new Vector3(k.CenterX, k.CenterY, k.CenterZ) : default; return null; }
    [Icall("UnityEngine.SphereCollider::set_center_Injected", Phys = 6)] static object SphereSetCenter(object[] a) { SetCenter(K(a[0]), (Vector3)a[1]); return null; }
    [Icall("UnityEngine.SphereCollider::get_radius", Phys = 6)] static object SphereGetRadius(object[] a) => K(a[0])?.Radius ?? 0.5f;
    [Icall("UnityEngine.SphereCollider::set_radius", Phys = 6)]
    static object SphereSetRadius(object[] a) { if (K(a[0]) is { } k) { k.Radius = Convert.ToSingle(a[1]); Phys3D.Queue(k); } return null; }
    [Icall("UnityEngine.CapsuleCollider::get_center_Injected", Phys = 6)] static object CapsuleGetCenter(object[] a) { var k = K(a[0]); a[1] = k != null ? new Vector3(k.CenterX, k.CenterY, k.CenterZ) : default; return null; }
    [Icall("UnityEngine.CapsuleCollider::get_radius", Phys = 6)] static object CapsuleGetRadius(object[] a) => K(a[0])?.Radius ?? 0.5f;
    [Icall("UnityEngine.CapsuleCollider::get_height", Phys = 6)] static object CapsuleGetHeight(object[] a) => K(a[0])?.Height ?? 2f;
    [Icall("UnityEngine.CapsuleCollider::get_direction", Phys = 6)] static object CapsuleGetDirection(object[] a) => K(a[0])?.Direction ?? 1;

    public static long Calls;
    static void SetCenter(NCollider3D k, Vector3 v) { if (k == null) return; k.CenterX = v.x; k.CenterY = v.y; k.CenterZ = v.z; Phys3D.Queue(k); }

    /// <summary>The managed Mesh a MeshCollider names (the loader's reference resolved, or the one sharedMesh set).</summary>
    static object MeshObject(NCollider3D k) => k.Mesh switch { Assets.LazyRef lr => lr.Resolve(), UnityEngine.Object u => u, _ => null };
    /// <summary>The nearest 3D Rigidbody on the collider's GameObject or above it (by type name: the 3D Rigidbody is DLC-only, "Later").</summary>
    static object AttachedBody(NCollider3D k)
    {
        for (var t = k.Go?.Transform; t != null; t = t.Parent)
            foreach (var c in t.Go.Components)
                if (!c.Destroyed && c.Managed != null && c.Managed.GetType() is { Name: "Rigidbody", Namespace: "UnityEngine" }) return c.Managed;
        return null;
    }

    /// <summary>Collider.bounds: the shape's world box -- a mesh's local bounds, a box, a sphere's or capsule's extent -- through the transform's pose
    /// and lossy scale (PhysX's getWorldBounds without inflation: UNVERIFIED); empty (centre and size 0) when the collider is not in the scene.</summary>
    static Bounds WorldBounds(NCollider3D k)
    {
        Phys3D.Sync();
        if (k.S3 == null) return new Bounds(Vector3.zero, Vector3.zero);
        var d = k.S3.Def;
        var q = new Quaternion(d.Qx, d.Qy, d.Qz, d.Qw);
        var s = new Vector3(d.Sx, d.Sy, d.Sz);
        Vector3 lc, le;
        switch (d.Kind)
        {
            case ShepPhys.C3Mesh:
                var m = k.S3.Mesh.Src;
                Vector3 mn = m.Pos[0], mx = m.Pos[0];
                foreach (var p in m.Pos) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                lc = Vector3.Scale((mn + mx) * 0.5f, s); le = Vector3.Scale((mx - mn) * 0.5f, Abs(s));
                break;
            case ShepPhys.C3Box: lc = Vector3.Scale(new Vector3(d.Cx, d.Cy, d.Cz), s); le = Vector3.Scale(new Vector3(d.Ex, d.Ey, d.Ez) * 0.5f, Abs(s)); break;
            case ShepPhys.C3Sphere: { lc = Vector3.Scale(new Vector3(d.Cx, d.Cy, d.Cz), s); float r = d.Ex * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))); le = new Vector3(r, r, r); break; }
            default:
            {
                lc = Vector3.Scale(new Vector3(d.Cx, d.Cy, d.Cz), s);
                var (r, hh, axis) = Capsule(d);
                le = new Vector3(r, r, r); le[axis] += hh;
                break;
            }
        }
        var R = Matrix4x4.Rotate(q);
        var c = q * lc + new Vector3(d.Px, d.Py, d.Pz);
        var e = new Vector3(Mathf.Abs(R.m00) * le.x + Mathf.Abs(R.m01) * le.y + Mathf.Abs(R.m02) * le.z,
                            Mathf.Abs(R.m10) * le.x + Mathf.Abs(R.m11) * le.y + Mathf.Abs(R.m12) * le.z,
                            Mathf.Abs(R.m20) * le.x + Mathf.Abs(R.m21) * le.y + Mathf.Abs(R.m22) * le.z);
        return new Bounds(c, e * 2f);
    }
    static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    /// <summary>A capsule's radius, the half length of its segment and its axis, as the native scene scales them.</summary>
    static (float r, float hh, int axis) Capsule(in ShepPhys.Col3Def d)
    {
        int a = d.Direction is >= 0 and <= 2 ? d.Direction : 1;
        float ax = Mathf.Abs(d.Sx), ay = Mathf.Abs(d.Sy), az = Mathf.Abs(d.Sz);
        float sa = a == 0 ? ax : a == 1 ? ay : az, s1 = a == 0 ? ay : ax, s2 = a == 2 ? ay : az;
        float r = Mathf.Abs(d.Ex) * Mathf.Max(s1, s2), hh = Mathf.Max(Mathf.Abs(d.Ey) * sa * 0.5f - r, 0f);
        return (r, hh, a);
    }
    /// <summary>Collider.ClosestPoint (Physics.ClosestPoint): the point itself when inside; Box / Sphere / Capsule only -- a non-convex mesh returns
    /// the point (Unity warns and does so); a collider not in the scene returns the point.</summary>
    static Vector3 Closest(NCollider3D k, Vector3 p)
    {
        Phys3D.Sync();
        if (k.S3 == null) return p;
        var d = k.S3.Def;
        var q = new Quaternion(d.Qx, d.Qy, d.Qz, d.Qw);
        var s = new Vector3(d.Sx, d.Sy, d.Sz);
        var c = q * Vector3.Scale(new Vector3(d.Cx, d.Cy, d.Cz), s) + new Vector3(d.Px, d.Py, d.Pz);
        switch (d.Kind)
        {
            case ShepPhys.C3Box:
            {
                var he = Vector3.Scale(new Vector3(d.Ex, d.Ey, d.Ez) * 0.5f, Abs(s));
                var l = Quaternion.Inverse(q) * (p - c);
                var cl = new Vector3(Mathf.Clamp(l.x, -he.x, he.x), Mathf.Clamp(l.y, -he.y, he.y), Mathf.Clamp(l.z, -he.z, he.z));
                return cl == l ? p : q * cl + c;
            }
            case ShepPhys.C3Sphere:
            {
                float r = d.Ex * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                var v = p - c; float m = v.magnitude;
                return m <= r ? p : c + v * (r / m);
            }
            case ShepPhys.C3Capsule:
            {
                var (r, hh, axis) = Capsule(d);
                var u = Vector3.zero; u[axis] = 1f; var w = q * u * hh;
                var a0 = c - w; var ab = w * 2f;
                float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(p - a0, ab) / ab.sqrMagnitude) : 0f;
                var on = a0 + ab * t; var v = p - on; float m = v.magnitude;
                return m <= r ? p : on + v * (r / m);
            }
            default:
                if (!_warnedMesh) { _warnedMesh = true; Log.Info($"[phys] stage 6: Collider.ClosestPoint on the non-convex MeshCollider '{k.Go?.Name}': the point itself (Unity: \"only supported for Box, Sphere, Capsule and convex Mesh colliders\")"); }
                return p;
        }
    }
    static bool _warnedMesh;

    sealed class Impl
    {
        // ---- RaycastHit: m_Point, m_Normal, m_FaceID, m_Distance, m_UV, m_Collider (instance id) -- the native layout, 44 B ----
        [StructLayout(LayoutKind.Sequential)]
        struct Hit3D { public Vector3 Point, Normal; public uint FaceID; public float Distance; public Vector2 UV; public int Collider; }
        readonly bool _layoutOk;
        public readonly object SceneBox = default(PhysicsScene);   // handle 0 (one shared box; the stub copies it out)
        readonly ShepPhys.Ray3Hit[] _one = new ShepPhys.Ray3Hit[1];
        ShepPhys.Ray3Hit[] _many = new ShepPhys.Ray3Hit[8];
        public Impl()
        {
            int n = Marshal.SizeOf<RaycastHit>();
            _layoutOk = Unsafe.SizeOf<Hit3D>() == 44 && n == 44 && Unsafe.SizeOf<RaycastHit>() == 44;
            if (!_layoutOk) Log.Error($"[phys] FAIL phys: RaycastHit is {n} B ({Unsafe.SizeOf<RaycastHit>()} managed), expected 44 (point, normal, faceID, distance, uv, collider id): stage 6 rays answer 'no hit'");
        }
        RaycastHit ToHit(in ShepPhys.Ray3Hit h)
        {
            var x = new Hit3D { Point = new Vector3(h.Px, h.Py, h.Pz), Normal = new Vector3(h.Nx, h.Ny, h.Nz), FaceID = unchecked((uint)h.Face), Distance = h.Distance, UV = new Vector2(h.U, h.V), Collider = h.User };
            return Unsafe.As<Hit3D, RaycastHit>(ref x);
        }
        // Internal_Raycast_Injected(ref PhysicsScene, ref Ray, float maxDistance, ref RaycastHit hit, int layerMask, QueryTriggerInteraction)
        internal object Raycast(object[] a)
        {
            Calls++;
            if (!_layoutOk) return false;
            var ray = (Ray)a[1];
            int n = Phys3D.Ray(ray.origin, ray.direction, Convert.ToSingle(a[2]), Convert.ToInt32(a[4]), Convert.ToInt32(a[5]), 0, _one);
            if (n > 0) a[3] = ToHit(_one[0]);
            return n > 0;
        }
        // Internal_RaycastNonAlloc_Injected(ref PhysicsScene, ref Ray, RaycastHit[] raycastHits, float maxDistance, int mask, QueryTriggerInteraction) -> int
        internal object RaycastNonAlloc(object[] a)
        {
            Calls++;
            if (!_layoutOk || a[2] is not RaycastHit[] arr || arr.Length == 0) return 0;
            if (_many.Length < arr.Length) _many = new ShepPhys.Ray3Hit[arr.Length];
            var ray = (Ray)a[1];
            int n = Phys3D.Ray(ray.origin, ray.direction, Convert.ToSingle(a[3]), Convert.ToInt32(a[4]), Convert.ToInt32(a[5]), ShepPhys.R3fAll, new Span<ShepPhys.Ray3Hit>(_many, 0, arr.Length));
            int w = Math.Min(n, arr.Length);
            for (int i = 0; i < w; i++) arr[i] = ToHit(_many[i]);
            return w;
        }
        // Internal_RaycastTest_Injected(ref PhysicsScene, ref Ray, float maxDistance, int layerMask, QueryTriggerInteraction) -> bool
        internal object RaycastTest(object[] a)
        {
            Calls++;
            if (!_layoutOk) return false;
            var ray = (Ray)a[1];
            return Phys3D.Ray(ray.origin, ray.direction, Convert.ToSingle(a[2]), Convert.ToInt32(a[3]), Convert.ToInt32(a[4]), ShepPhys.R3fAny, _one) > 0;
        }
    }
}
