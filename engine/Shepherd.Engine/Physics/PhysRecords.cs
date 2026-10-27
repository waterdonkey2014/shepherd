using System.Collections;
using Shepherd.Assets;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 section 1.4 "Membership and records"): typed records for the physics components, built
/// <b>in addition to</b> the property bags (Loader.CreateOther's SeedProps still runs, so the live Triggers2D, the auto-props and the diet
/// keep working until Stage 2). Each holds every field Unity reads, decoded from the component's own type tree at load
/// (<see cref="PhysRecords.Fill"/>), or Unity's defaults for a runtime AddComponent (<see cref="PhysRecords.New"/>). Nothing reads them
/// in stage 0 except the checks; Stage 1 builds the Box2D shapes and bodies from them. Object references (materials, meshes) are kept
/// as the loader's LazyRef and never resolved here: resolving would load objects earlier than kit #4 did.</summary>
public sealed class NRigidbody2D : NComponent
{
    /// <summary>RigidbodyType2D: 0 Dynamic, 1 Kinematic, 2 Static.</summary>
    public int BodyType;
    public bool Simulated = true, UseFullKinematicContacts, UseAutoMass;
    public float Mass = 1f, LinearDrag, AngularDrag = 0.05f, GravityScale = 1f;
    public object Material;                                // LazyRef to a PhysicsMaterial2D, or null
    public uint IncludeLayers, ExcludeLayers;
    /// <summary>RigidbodyInterpolation2D (0 None, 1 Interpolate, 2 Extrapolate), RigidbodySleepMode2D (0 NeverSleep, 1 StartAwake,
    /// 2 StartAsleep), CollisionDetectionMode2D (0 Discrete, 1 Continuous), RigidbodyConstraints2D bits.</summary>
    public int Interpolate, SleepingMode = 1, CollisionDetection, Constraints;
    /// <summary>Decoded from serialized data (false: Unity's defaults of a runtime AddComponent).</summary>
    public bool FromData;
    /// <summary>Physics Stage 1 (Physics/PhysWorld.Stage1.cs): the body in the core while it is in the world; queued for reconcile.</summary>
    internal PhysWorld.BodyState PB; internal bool PhysQueued;
}

/// <summary>Collider2D and its subclasses (a Behaviour: Registry.Colliders2D and Triggers2D see it as before).</summary>
public sealed class NCollider2D : NBehaviour
{
    public PhysRecords.Kind2D Kind;
    public float Density = 1f;
    public object Material;
    public uint IncludeLayers, ExcludeLayers, ForceSendLayers = ~0u, ForceReceiveLayers = ~0u, ContactCaptureLayers = ~0u, CallbackLayers = ~0u;
    public int LayerOverridePriority;
    public bool IsTrigger, UsedByEffector, UsedByComposite, AutoTiling, UseDelaunayMesh;
    public float OffsetX, OffsetY;
    // ---- shape payload ----
    /// <summary>CircleCollider2D radius (Unity default 0.5).</summary>
    public float Radius = 0.5f;
    /// <summary>Box / Capsule size (default (1, 1)); EdgeRadius of Box / Edge / Composite.</summary>
    public float SizeX = 1f, SizeY = 1f, EdgeRadius;
    /// <summary>CapsuleDirection2D: 0 Vertical, 1 Horizontal.</summary>
    public int Direction;
    /// <summary>Polygon: m_Points.m_Paths; Edge: one path, m_Points; Composite: Unity's own m_CompositePaths.m_Paths (the output the
    /// Stage 1 composite pipeline is checked against). Each path is x0, y0, x1, y1, ... in collider space.</summary>
    public float[][] Paths;
    public bool UseAdjacentStartPoint, UseAdjacentEndPoint;
    public float AdjacentStartX, AdjacentStartY, AdjacentEndX, AdjacentEndY;
    /// <summary>CompositeCollider2D: GeometryType (0 Outlines, 1 Polygons), GenerationType (0 Synchronous, 1 Manual), m_ColliderPaths' entry
    /// count (the per-collider inputs, IntPoint x 1e7; read by Stage 1), vertex and offset distances.</summary>
    public int GeometryType, GenerationType, ColliderPathCount;
    /// <summary>CompositeCollider2D: Unity's own serialized per-collider inputs (m_ColliderPaths, every path of every SubCollider in order, IntPoint
    /// x / y pairs) -- Stage 1 compares its own GenerateGeometry inputs with them (Physics/Composite.cs).</summary>
    public long[][] ColliderPaths;
    public float VertexDistance = 0.0005f, OffsetDistance = 0.00005f;
    public bool FromData;
    /// <summary>Physics Stage 1: the shape(s) in the core while in the world; queued for reconcile. A composite's own GenerateGeometry result
    /// (Physics/Composite.cs; null = Unity's serialized m_CompositePaths in <see cref="Paths"/>), the geometry type it was made for, and a
    /// version bumped by every generation.</summary>
    internal PhysWorld.ShapeState PS; internal bool PhysQueued;
    internal float[][] GenPaths; internal int GenGeometry = -1, GenVersion;
    /// <summary>Physics Stage 4a: the hash of the inputs of the last generation (a Synchronous composite regenerates when its inputs' hash moves),
    /// and the cache of Unity's API paths (the source array and offset they were made from: PhysWorld.CompositeApiPaths).</summary>
    internal ulong GenHash; internal bool GenHashSet;
    internal float[][] ApiSrc, ApiPaths; internal float ApiOx, ApiOy;
    /// <summary>Physics Stage 2 (Physics/PhysDispatch.cs): the phase window in which the collider last left the world (H-REENABLE) and in which its
    /// isTrigger or layer last changed while in the world (H-RECREATE); -1 = never.</summary>
    internal int LeftMark = -1, RecreateMark = -1;
    /// <summary>Physics Stage 2 since E1: Object.Destroy was called on it (or its GameObject) this frame -- out of the physics world from the call,
    /// the objects die at the end of the frame (PhysWorld.DestroyRequested).</summary>
    internal bool DestroyPending;
}

/// <summary>The 3D colliders (Box 65, Sphere 135, Capsule 136, Mesh 64): a Component in Unity (enabled is its own flag). Stage 6's
/// island ground ray reads them.</summary>
public sealed class NCollider3D : NComponent
{
    public PhysRecords.Kind3D Kind;
    public bool Enabled = true, IsTrigger, ProvidesContacts, Convex;
    public object Material, Mesh;
    public int LayerOverridePriority, CookingOptions, Direction = 1;
    public uint IncludeLayers, ExcludeLayers;
    public float CenterX, CenterY, CenterZ, SizeX = 1f, SizeY = 1f, SizeZ = 1f, Radius = 0.5f, Height = 2f;
    public bool FromData;
    /// <summary>Physics Stage 6 (Physics/Phys3D.cs): the collider in the 3D scene while it is there; queued for reconcile; the mesh
    /// (re)assigned through MeshCollider.sharedMesh since the last cook (Unity cooks at assignment).</summary>
    internal Phys3D.ColState S3; internal bool Phys3DQueued, MeshAssigned;
}

/// <summary>PhysicsMaterial2D (class 62; an asset, not a component). Unity's defaults: friction 0.4, bounciness 0. The collider's material
/// wins over the body's; mixing is Box2D's (friction sqrt(a * b), restitution max(a, b)) -- Stage 1.</summary>
public sealed class NPhysicsMaterial2D : NObj
{
    public float Friction = 0.4f, Bounciness;
    public bool FromData;
}

/// <summary>Builds and fills the typed records. Loader.CreatePhysics (class ids 50, 58, 60, 61, 62, 64, 65, 66, 68, 70, 135, 136,
/// 19719996) and Lifecycle.NewComponentRecord (runtime AddComponent, Instantiate) call it; Cloner copies the typed state.
/// Type tests go by name (as Registry.IsCollider2D does), so nothing here loads a Unity physics module earlier than the game does.
/// Tree reads use m_-prefixed literals (and PhysicsMaterial2D's two unprefixed ones): none is a property-bag key.</summary>
[ProfileOnly]   // reads type trees, never a property bag (Assets/Diet.cs)
public static class PhysRecords
{
    public enum Kind2D { Other, Circle, Box, Polygon, Edge, Capsule, Composite, Tilemap }
    public enum Kind3D { Other, Box, Sphere, Capsule, Mesh }

    /// <summary>Is this a class id CreatePhysics takes (plan section 1.4)?</summary>
    public static bool IsPhysicsClass(int classId) => classId is 50 or 58 or 60 or 61 or 62 or 64 or 65 or 66 or 68 or 70 or 135 or 136 or 19719996;

    public static int Built, Filled, RuntimeAdds, Copies;

    // ---------------- runtime AddComponent / Instantiate: the record for a component type (null: not a physics type) ----------------
    static readonly Dictionary<Type, int> _typeCode = new();   // 0 none, 1 body, 2 collider2D (+ kind << 4), 3 collider3D (+ kind << 4)
    static int Code(Type t)
    {
        lock (_typeCode)
        {
            if (_typeCode.TryGetValue(t, out var c)) return c;
            c = 0;
            for (var b = t; b != null; b = b.BaseType)
            {
                if (b.Namespace != "UnityEngine") continue;
                if (b.Name == "Rigidbody2D") { c = 1; break; }
                if (b.Name == "Collider2D") { c = 2 | ((int)Kind2DOf(t.Name) << 4); break; }
                if (b.Name == "Collider") { c = 3 | ((int)Kind3DOf(t.Name) << 4); break; }
            }
            return _typeCode[t] = c;
        }
    }
    static Kind2D Kind2DOf(string n) => n switch
    {
        "CircleCollider2D" => Kind2D.Circle, "BoxCollider2D" => Kind2D.Box, "PolygonCollider2D" => Kind2D.Polygon, "EdgeCollider2D" => Kind2D.Edge,
        "CapsuleCollider2D" => Kind2D.Capsule, "CompositeCollider2D" => Kind2D.Composite, "TilemapCollider2D" => Kind2D.Tilemap, _ => Kind2D.Other
    };
    static Kind3D Kind3DOf(string n) => n switch
    {
        "BoxCollider" => Kind3D.Box, "SphereCollider" => Kind3D.Sphere, "CapsuleCollider" => Kind3D.Capsule, "MeshCollider" => Kind3D.Mesh, _ => Kind3D.Other
    };
    static Kind2D Kind2DOfClass(int id) => id switch { 58 => Kind2D.Circle, 61 => Kind2D.Box, 60 => Kind2D.Polygon, 68 => Kind2D.Edge, 70 => Kind2D.Capsule, 66 => Kind2D.Composite, 19719996 => Kind2D.Tilemap, _ => Kind2D.Other };
    static Kind3D Kind3DOfClass(int id) => id switch { 65 => Kind3D.Box, 135 => Kind3D.Sphere, 136 => Kind3D.Capsule, 64 => Kind3D.Mesh, _ => Kind3D.Other };

    static readonly HashSet<string> _warned = new();
    /// <summary>The typed record for component type t with Unity's defaults (plan section 1.4: Rigidbody2D Dynamic, mass 1, gravity scale 1,
    /// drag 0, simulated, Discrete, no interpolation, StartAwake; BoxCollider2D size (1, 1); CircleCollider2D radius 0.5; offset (0, 0);
    /// not a trigger; no material), or null when t is no physics component. Other collider types added at run time log once: their
    /// defaults are UNVERIFIED. H-AUTOFIT stays at its default (off): no fit to an attached SpriteRenderer.</summary>
    /// <summary>H-AUTOFIT=on (E1's AutoFit scene, a player build): a Box / Circle / Capsule collider added at run time to a GameObject whose
    /// SpriteRenderer has a sprite fits the sprite's bounds -- size = the bounds' size (a 2 x 1 sprite: (2, 1)), a circle's radius = half the larger
    /// side (1), a capsule's size likewise (its direction stays Vertical), the offset = the bounds' centre (E1 measured a centred pivot only: an
    /// off-centre pivot's offset is UNVERIFIED). A PolygonCollider2D would take the sprite's outline -- not done (sprite physics shapes / outlines are
    /// Stage 4c). Level 1 and above with a core only (Lifecycle.AddComponent; kit #8 review: a level without a core is stage 0): at 0 and none a
    /// runtime collider keeps Unity's plain defaults, as before E1.</summary>
    internal static void AutoFit(NCollider2D c, NGameObject go)
    {
        if (c.Kind is not (Kind2D.Box or Kind2D.Circle or Kind2D.Capsule) || go == null) return;
        NSprite ns = null;
        foreach (var k in go.Components)
            if (!k.Destroyed && k.Managed is UnityEngine.SpriteRenderer) { ns = Objects.Get<NSprite>(Runtime.Conv.To(k.Get("sprite"), typeof(UnityEngine.Sprite)) as UnityEngine.Sprite); break; }
        if (ns == null || !(ns.PixelsPerUnit > 0f)) return;
        float w = ns.Rect.width / ns.PixelsPerUnit, h = ns.Rect.height / ns.PixelsPerUnit;   // (Sprite.bounds, RenderIcalls' get_bounds)
        c.OffsetX = (0.5f - ns.Pivot.x) * w; c.OffsetY = (0.5f - ns.Pivot.y) * h;
        c.Set("offset", new UnityEngine.Vector2(c.OffsetX, c.OffsetY));
        if (c.Kind == Kind2D.Circle) { c.Radius = 0.5f * MathF.Max(w, h); c.Set("radius", c.Radius); }
        else { c.SizeX = w; c.SizeY = h; c.Set("size", new UnityEngine.Vector2(w, h)); }
        AutoFits++;
    }
    public static long AutoFits;

    public static NComponent New(Type t)
    {
        int c = Code(t);
        if (c == 0) return null;
        Built++;
        switch (c & 15)
        {
            case 1: return new NRigidbody2D();
            case 2:
                var k = (Kind2D)(c >> 4);
                if (k is not (Kind2D.Box or Kind2D.Circle or Kind2D.Polygon or Kind2D.Edge or Kind2D.Composite) && _warned.Add(t.Name)) Log.Info($"[phys] typed record for a {t.Name} made at run time: Unity's defaults for it are UNVERIFIED (plan 1.4)");
                var nc = new NCollider2D { Kind = k };
                // E1's AutoFit scene (results/arm64-r1/AutoFit.csv, no SpriteRenderer): a PolygonCollider2D starts as the radius-1 pentagon below (Unity's
                // exact floats), an EdgeCollider2D as (-0.5, 0) - (0.5, 0); Box (1, 1) and Circle 0.5 are the fields' own defaults; a CompositeCollider2D's
                // (Outlines, Synchronous, vertexDistance 0.0005, offsetDistance 5e-5, edgeRadius 0, no paths) are its fields' defaults too (Stage 4a: Paths4a C1_empty)
                if (k == Kind2D.Polygon) nc.Paths = new[] { new[] { 0f, 1f, -0.95105654f, 0.309016973f, -0.587785184f, -0.809017062f, 0.587785363f, -0.809016943f, 0.95105648f, 0.309017122f } };
                else if (k == Kind2D.Edge) nc.Paths = new[] { new[] { -0.5f, 0f, 0.5f, 0f } };
                return nc;
            default: return new NCollider3D { Kind = (Kind3D)(c >> 4) };
        }
    }
    /// <summary>Instantiate / AddComponent made rec from type t: count the runtime ones (log line at exit).</summary>
    public static void NoteRuntime() => RuntimeAdds++;

    /// <summary>A non-component physics asset record for class id (62: PhysicsMaterial2D), else null.</summary>
    public static NObj NewAsset(int classId) { if (classId != 62) return null; Built++; return new NPhysicsMaterial2D(); }

    // ---------------- serialized data ----------------
    static object V(TypeTreeObject t, string k) => t != null && t.TryGet(k, out var v) ? v : null;
    static int I(TypeTreeObject t, string k, int d) => V(t, k) is { } v ? unchecked((int)Convert.ToInt64(v)) : d;
    static uint U(TypeTreeObject t, string k, uint d) => V(t, k) is TypeTreeObject bf ? (V(bf, "m_Bits") is { } b ? unchecked((uint)Convert.ToInt64(b)) : d) : V(t, k) is { } v ? unchecked((uint)Convert.ToInt64(v)) : d;
    static float F(TypeTreeObject t, string k, float d) => V(t, k) is { } v ? Convert.ToSingle(v) : d;
    static bool B(TypeTreeObject t, string k, bool d) => V(t, k) switch { null => d, bool b => b, var v => Convert.ToInt64(v) != 0 };
    static (float x, float y) V2(TypeTreeObject t, string k, float dx, float dy) => V(t, k) is TypeTreeObject v ? (F(v, "x", dx), F(v, "y", dy)) : (dx, dy);
    static (float x, float y, float z) V3(TypeTreeObject t, string k, float d) => V(t, k) is TypeTreeObject v ? (F(v, "x", d), F(v, "y", d), F(v, "z", d)) : (d, d, d);
    static object Ref(TypeTreeObject t, string k, SerializedFile f) => V(t, k) is PPtr p && !p.IsNull ? new Assets.LazyRef(f, p) : null;
    /// <summary>A list of Vector2f -> x0, y0, x1, y1, ...</summary>
    static float[] Path(object list)
    {
        if (list is not IList l) return null;
        var r = new float[l.Count * 2];
        for (int i = 0; i < l.Count; i++) if (l[i] is TypeTreeObject v) { r[2 * i] = F(v, "x", 0f); r[2 * i + 1] = F(v, "y", 0f); }
        return r;
    }
    /// <summary>m_ColliderPaths (a list of SubCollider { m_Collider, m_ColliderPaths: list of IntPoint lists }) -> every path, x / y pairs.</summary>
    static long[][] IntPaths(object subs)
    {
        if (subs is not IList l || l.Count == 0) return null;
        var r = new List<long[]>();
        foreach (var s in l)
            if (s is TypeTreeObject sub && V(sub, "m_ColliderPaths") is IList paths)
                foreach (var p in paths)
                    if (p is IList pts)
                    {
                        var a = new long[pts.Count * 2];
                        for (int i = 0; i < pts.Count; i++) if (pts[i] is TypeTreeObject ip) { a[2 * i] = Convert.ToInt64(V(ip, "X") ?? 0L); a[2 * i + 1] = Convert.ToInt64(V(ip, "Y") ?? 0L); }
                        r.Add(a);
                    }
        return r.ToArray();
    }
    static float[][] Paths(object polygon2d)
    {
        if (polygon2d is not TypeTreeObject p || V(p, "m_Paths") is not IList l) return null;
        var r = new float[l.Count][];
        for (int i = 0; i < l.Count; i++) r[i] = Path(l[i]) ?? Array.Empty<float>();
        return r;
    }

    /// <summary>Fill rec (made by Lifecycle.NewComponentRecord / <see cref="NewAsset"/>) from its type tree d.</summary>
    public static void Fill(NObj rec, int classId, TypeTreeObject d, SerializedFile f)
    {
        if (d == null) return;
        Filled++;
        switch (rec)
        {
            case NRigidbody2D b:
                b.FromData = true;
                b.BodyType = I(d, "m_BodyType", 0); b.Simulated = B(d, "m_Simulated", true);
                b.UseFullKinematicContacts = B(d, "m_UseFullKinematicContacts", false); b.UseAutoMass = B(d, "m_UseAutoMass", false);
                b.Mass = F(d, "m_Mass", 1f); b.LinearDrag = F(d, "m_LinearDrag", 0f); b.AngularDrag = F(d, "m_AngularDrag", 0.05f);
                b.GravityScale = F(d, "m_GravityScale", 1f); b.Material = Ref(d, "m_Material", f);
                b.IncludeLayers = U(d, "m_IncludeLayers", 0); b.ExcludeLayers = U(d, "m_ExcludeLayers", 0);
                b.Interpolate = I(d, "m_Interpolate", 0); b.SleepingMode = I(d, "m_SleepingMode", 1);
                b.CollisionDetection = I(d, "m_CollisionDetection", 0); b.Constraints = I(d, "m_Constraints", 0);
                break;
            case NCollider2D c:
                c.FromData = true; c.Kind = Kind2DOfClass(classId);
                c.Density = F(d, "m_Density", 1f); c.Material = Ref(d, "m_Material", f);
                c.IncludeLayers = U(d, "m_IncludeLayers", 0); c.ExcludeLayers = U(d, "m_ExcludeLayers", 0);
                c.LayerOverridePriority = I(d, "m_LayerOverridePriority", 0);
                c.ForceSendLayers = U(d, "m_ForceSendLayers", ~0u); c.ForceReceiveLayers = U(d, "m_ForceReceiveLayers", ~0u);
                c.ContactCaptureLayers = U(d, "m_ContactCaptureLayers", ~0u); c.CallbackLayers = U(d, "m_CallbackLayers", ~0u);
                c.IsTrigger = B(d, "m_IsTrigger", false); c.UsedByEffector = B(d, "m_UsedByEffector", false); c.UsedByComposite = B(d, "m_UsedByComposite", false);
                (c.OffsetX, c.OffsetY) = V2(d, "m_Offset", 0f, 0f);
                c.AutoTiling = B(d, "m_AutoTiling", false); c.UseDelaunayMesh = B(d, "m_UseDelaunayMesh", false);
                switch (c.Kind)
                {
                    case Kind2D.Circle: c.Radius = F(d, "m_Radius", 0.5f); break;
                    case Kind2D.Box: (c.SizeX, c.SizeY) = V2(d, "m_Size", 1f, 1f); c.EdgeRadius = F(d, "m_EdgeRadius", 0f); break;
                    case Kind2D.Capsule: (c.SizeX, c.SizeY) = V2(d, "m_Size", 1f, 1f); c.Direction = I(d, "m_Direction", 0); break;
                    case Kind2D.Polygon: c.Paths = Paths(V(d, "m_Points")); break;
                    case Kind2D.Edge:
                        c.EdgeRadius = F(d, "m_EdgeRadius", 0f); var e = Path(V(d, "m_Points")); c.Paths = e == null ? null : new[] { e };
                        (c.AdjacentStartX, c.AdjacentStartY) = V2(d, "m_AdjacentStartPoint", 0f, 0f); (c.AdjacentEndX, c.AdjacentEndY) = V2(d, "m_AdjacentEndPoint", 0f, 0f);
                        c.UseAdjacentStartPoint = B(d, "m_UseAdjacentStartPoint", false); c.UseAdjacentEndPoint = B(d, "m_UseAdjacentEndPoint", false);
                        break;
                    case Kind2D.Composite:
                        c.GeometryType = I(d, "m_GeometryType", 0); c.GenerationType = I(d, "m_GenerationType", 0); c.EdgeRadius = F(d, "m_EdgeRadius", 0f);
                        c.VertexDistance = F(d, "m_VertexDistance", 0.0005f); c.OffsetDistance = F(d, "m_OffsetDistance", 0.00005f);
                        c.ColliderPathCount = V(d, "m_ColliderPaths") is IList cp ? cp.Count : 0;
                        c.ColliderPaths = IntPaths(V(d, "m_ColliderPaths"));
                        c.Paths = Paths(V(d, "m_CompositePaths"));
                        break;
                }
                break;
            case NCollider3D k:
                k.FromData = true; k.Kind = Kind3DOfClass(classId);
                k.Enabled = B(d, "m_Enabled", true); k.IsTrigger = B(d, "m_IsTrigger", false); k.ProvidesContacts = B(d, "m_ProvidesContacts", false);
                k.Material = Ref(d, "m_Material", f); k.LayerOverridePriority = I(d, "m_LayerOverridePriority", 0);
                k.IncludeLayers = U(d, "m_IncludeLayers", 0); k.ExcludeLayers = U(d, "m_ExcludeLayers", 0);
                (k.CenterX, k.CenterY, k.CenterZ) = V3(d, "m_Center", 0f);
                switch (k.Kind)
                {
                    case Kind3D.Box: (k.SizeX, k.SizeY, k.SizeZ) = V3(d, "m_Size", 1f); break;
                    case Kind3D.Sphere: k.Radius = F(d, "m_Radius", 0.5f); break;
                    case Kind3D.Capsule: k.Radius = F(d, "m_Radius", 0.5f); k.Height = F(d, "m_Height", 2f); k.Direction = I(d, "m_Direction", 1); break;
                    case Kind3D.Mesh: k.Convex = B(d, "m_Convex", false); k.CookingOptions = I(d, "m_CookingOptions", 0); k.Mesh = Ref(d, "m_Mesh", f); break;
                }
                break;
            case NPhysicsMaterial2D m:
                m.FromData = true; m.Friction = F(d, "friction", 0.4f); m.Bounciness = F(d, "bounciness", 0f);
                break;
        }
    }

    /// <summary>Cloner.CopyState: the typed state of src onto its clone (the clone's record was made by NewComponentRecord with defaults).
    /// Arrays are shared: records never write into them (a later setter replaces the array).</summary>
    public static void Copy(NObj src, NObj dst)
    {
        switch (src)
        {
            case NRigidbody2D a when dst is NRigidbody2D b:
                b.BodyType = a.BodyType; b.Simulated = a.Simulated; b.UseFullKinematicContacts = a.UseFullKinematicContacts; b.UseAutoMass = a.UseAutoMass;
                b.Mass = a.Mass; b.LinearDrag = a.LinearDrag; b.AngularDrag = a.AngularDrag; b.GravityScale = a.GravityScale; b.Material = a.Material;
                b.IncludeLayers = a.IncludeLayers; b.ExcludeLayers = a.ExcludeLayers; b.Interpolate = a.Interpolate; b.SleepingMode = a.SleepingMode;
                b.CollisionDetection = a.CollisionDetection; b.Constraints = a.Constraints; b.FromData = a.FromData;
                break;
            case NCollider2D a when dst is NCollider2D b:
                b.Kind = a.Kind; b.Density = a.Density; b.Material = a.Material; b.IncludeLayers = a.IncludeLayers; b.ExcludeLayers = a.ExcludeLayers;
                b.ForceSendLayers = a.ForceSendLayers; b.ForceReceiveLayers = a.ForceReceiveLayers; b.ContactCaptureLayers = a.ContactCaptureLayers;
                b.CallbackLayers = a.CallbackLayers; b.LayerOverridePriority = a.LayerOverridePriority; b.IsTrigger = a.IsTrigger;
                b.UsedByEffector = a.UsedByEffector; b.UsedByComposite = a.UsedByComposite; b.AutoTiling = a.AutoTiling; b.UseDelaunayMesh = a.UseDelaunayMesh;
                b.OffsetX = a.OffsetX; b.OffsetY = a.OffsetY; b.Radius = a.Radius; b.SizeX = a.SizeX; b.SizeY = a.SizeY; b.EdgeRadius = a.EdgeRadius;
                b.Direction = a.Direction; b.Paths = a.Paths; b.UseAdjacentStartPoint = a.UseAdjacentStartPoint; b.UseAdjacentEndPoint = a.UseAdjacentEndPoint;
                b.AdjacentStartX = a.AdjacentStartX; b.AdjacentStartY = a.AdjacentStartY; b.AdjacentEndX = a.AdjacentEndX; b.AdjacentEndY = a.AdjacentEndY;
                b.GeometryType = a.GeometryType; b.GenerationType = a.GenerationType; b.ColliderPathCount = a.ColliderPathCount; b.ColliderPaths = a.ColliderPaths;
                b.GenPaths = a.GenPaths; b.GenGeometry = a.GenGeometry;   // (physics Stage 1: a generated composite's geometry is part of its state)
                b.VertexDistance = a.VertexDistance; b.OffsetDistance = a.OffsetDistance; b.FromData = a.FromData;
                break;
            case NCollider3D a when dst is NCollider3D b:
                b.Kind = a.Kind; b.Enabled = a.Enabled; b.IsTrigger = a.IsTrigger; b.ProvidesContacts = a.ProvidesContacts; b.Convex = a.Convex;
                b.Material = a.Material; b.Mesh = a.Mesh; b.LayerOverridePriority = a.LayerOverridePriority; b.CookingOptions = a.CookingOptions;
                b.Direction = a.Direction; b.IncludeLayers = a.IncludeLayers; b.ExcludeLayers = a.ExcludeLayers;
                b.CenterX = a.CenterX; b.CenterY = a.CenterY; b.CenterZ = a.CenterZ; b.SizeX = a.SizeX; b.SizeY = a.SizeY; b.SizeZ = a.SizeZ;
                b.Radius = a.Radius; b.Height = a.Height; b.FromData = a.FromData;
                break;
            default: return;
        }
        Copies++;
    }

    /// <summary>PhysInterest bits a record gives its GameObject's transform (plan 1.5): 1 = owns a Rigidbody2D, 2 = has 2D colliders; physics
    /// Stage 6: 4 = has 3D colliders (only at level 6: below it a 3D collider gives no interest, exactly as before).</summary>
    public static int InterestOf(NObj rec) => rec switch { NRigidbody2D => 1, NCollider2D => 2, NCollider3D => PhysWorld.Sim6 ? 4 : 0, _ => 0 };   // 4: physics Stage 6's 3D colliders (level 6 only)
}
