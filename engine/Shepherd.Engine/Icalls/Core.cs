using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

static class Core
{
    public static readonly int MainThread = Environment.CurrentManagedThreadId;
    static T N<T>(object o) where T : NObj => Objects.Get<T>(o);
    static readonly FieldInfo SceneHandle = typeof(Scene).GetField("m_Handle", BindingFlags.Instance | BindingFlags.NonPublic);
    public static object MakeScene(int h) { object s = default(Scene); SceneHandle.SetValue(s, h); return s; }
    public static int HandleOf(object scene) => scene is Scene ? (int)SceneHandle.GetValue(scene) : 0;

    // ---------------- Object ----------------
    [Icall("UnityEngine.Object::CurrentThreadIsMainThread")] static object MainThreadQ(object[] a) => Environment.CurrentManagedThreadId == MainThread;
    [Icall("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject")] static object IdOffset(object[] a) => 8;
    [Icall("UnityEngine.Object::GetName")] static object GetName(object[] a) => Objects.Get(a[0])?.Name ?? "";
    [Icall("UnityEngine.Object::SetName")] static object SetName(object[] a) { var n = Objects.Get(a[0]); if (n != null) { n.Name = (string)a[1] ?? ""; if (n is NGameObject g) foreach (var c in g.Components) c.Name = n.Name; } return null; }
    [Icall("UnityEngine.Object::ToString")] static object ToStr(object[] a) { var n = Objects.Get(a[0]); return n == null ? "null" : $"{n.Name} ({a[0].GetType().FullName})"; }
    [Icall("UnityEngine.Object::get_hideFlags")] static object GetHide(object[] a) => (HideFlags)(Objects.Get(a[0])?.HideFlags ?? 0);
    [Icall("UnityEngine.Object::set_hideFlags")] static object SetHide(object[] a) { var n = Objects.Get(a[0]); if (n != null) n.HideFlags = Convert.ToInt32(a[1]); return null; }
    [Icall("UnityEngine.Object::DoesObjectWithInstanceIDExist")] static object Exists(object[] a) => Objects.ById.ContainsKey((int)a[0]);
    [Icall("UnityEngine.Object::FindObjectFromInstanceID")] static object FromId(object[] a) => Objects.ById.TryGetValue((int)a[0], out var n) ? n.Managed : null;
    [Icall("UnityEngine.Object::ForceLoadFromInstanceID")] static object ForceFromId(object[] a) => FromId(a);
    [Icall("UnityEngine.Object::IsPersistent")] static object IsPersistent(object[] a) => (Objects.Get(a[0])?.InstanceId ?? 0) > 0;
    [Icall("UnityEngine.Object::MarkDirty")] static object MarkDirty(object[] a) => null;
    [Icall("UnityEngine.Object::Destroy")] static object Destroy(object[] a) { Lifecycle.Destroy(Objects.Get(a[0]), (float)a[1]); return null; }
    [Icall("UnityEngine.Object::DestroyImmediate")] static object DestroyImm(object[] a) { Lifecycle.DestroyNow(Objects.Get(a[0])); return null; }
    [Icall("UnityEngine.Object::DontDestroyOnLoad")] static object DDOL(object[] a)
    {
        var n = Objects.Get(a[0]); var go = n as NGameObject ?? (n as NComponent)?.Go;
        if (go != null && go.Transform.Parent != null && K6Engine.DdolRootOnly) { K6Engine.DdolRefused(go.Name, go.Transform.Parent.Go?.Name); return null; }   // kit #6 (b): Unity's root rule
        if (go != null) Scenes.DontDestroyOnLoad(go); else if (n != null) n.DontDestroy = true; return null;
    }
    static Array FindOfType(Type t, bool includeInactive)
    {
        var list = new List<UObject>();
        foreach (var n in Objects.Snapshot())
        {
            if (n.Destroyed || n.Managed is null || !t.IsInstanceOfType(n.Managed)) continue;
            var go = n as NGameObject ?? (n as NComponent)?.Go;
            if (go != null && !includeInactive && !go.ActiveInHierarchy) continue;
            // Unity: scene objects only -- a prefab asset's GameObjects and components (Scene 0: in no scene) are never found. Before,
            // MonoSingleton<T>.Instance (FindObjectOfType) could return the prefab's component, so the real instance destroyed itself as
            // a duplicate (polish round 1: UIManager -> its controller/binding templates were never assigned, Settings' Controls tab threw)
            if (go != null && go.Scene == 0) continue;
            if (go == null && n.HasSource && !(n.Managed is ScriptableObject)) continue;   // unloaded-ish assets are not "found" by FindObjectsOfType
            list.Add(n.Managed);
        }
        var arr = Array.CreateInstance(t, list.Count); for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i); return arr;
    }
    [Icall("UnityEngine.Object::FindObjectsOfType")] static object FindOfType(object[] a) => FindOfType((Type)a[0], (bool)a[1]);
    [Icall("UnityEngine.Object::FindObjectsByType")] static object FindByType(object[] a) => FindOfType((Type)a[0], Convert.ToInt32(a[1]) == 1);
    [Icall("UnityEngine.Object::FindObjectsOfTypeIncludingAssets")] static object FindIncl(object[] a) => FindOfType((Type)a[0], true);

    // Instantiate family -> Cloner
    [Icall("UnityEngine.Object::Internal_CloneSingle")] static object Clone1(object[] a) => Cloner.Clone((UObject)a[0], null, false, null, null);
    [Icall("UnityEngine.Object::Internal_CloneSingleWithParent")] static object Clone2(object[] a) => Cloner.Clone((UObject)a[0], (Transform)a[1], (bool)a[2], null, null);
    [Icall("UnityEngine.Object::Internal_InstantiateSingle_Injected")] static object Clone3(object[] a) => Cloner.Clone((UObject)a[0], null, true, (Vector3)a[1], (Quaternion)a[2]);
    [Icall("UnityEngine.Object::Internal_InstantiateSingleWithParent_Injected")] static object Clone4(object[] a) => Cloner.Clone((UObject)a[0], (Transform)a[1], true, (Vector3)a[2], (Quaternion)a[3]);
    [Icall("UnityEngine.Object::Internal_CloneSingleWithScene_Injected")] static object Clone5(object[] a) => Cloner.Clone((UObject)a[0], null, false, null, null);

    // ---------------- GameObject ----------------
    [Icall("UnityEngine.GameObject::Internal_CreateGameObject")] static object CreateGo(object[] a) { var go = Lifecycle.NewGameObject((GameObject)a[0], (string)a[1]); return null; }
    [Icall("UnityEngine.GameObject::get_transform")] static object GoTransform(object[] a) => N<NGameObject>(a[0])?.Transform.Managed;
    [Icall("UnityEngine.GameObject::get_activeSelf")] static object ActiveSelf(object[] a) => N<NGameObject>(a[0])?.ActiveSelf ?? false;
    [Icall("UnityEngine.GameObject::get_active")] static object ActiveOld(object[] a) => N<NGameObject>(a[0])?.ActiveInHierarchy ?? false;
    [Icall("UnityEngine.GameObject::get_activeInHierarchy")] static object ActiveH(object[] a) => Boxes.Of(N<NGameObject>(a[0])?.ActiveInHierarchy ?? false);
    [Icall("UnityEngine.GameObject::SetActive")] static object SetActive(object[] a) { var g = N<NGameObject>(a[0]); if (g != null) Lifecycle.SetActive(g, (bool)a[1]); return null; }
    [Icall("UnityEngine.GameObject::set_active")] static object SetActiveOld(object[] a) => SetActive(a);
    [Icall("UnityEngine.GameObject::SetActiveRecursively")] static object SetActiveRec(object[] a) { var g = N<NGameObject>(a[0]); if (g != null) foreach (var x in Scenes.Walk(g).ToArray()) Lifecycle.SetActive(x, (bool)a[1]); return null; }
    [Icall("UnityEngine.GameObject::get_layer")] static object Layer(object[] a) => N<NGameObject>(a[0])?.Layer ?? 0;
    [Icall("UnityEngine.GameObject::set_layer")] static object SetLayer(object[] a) { var g = N<NGameObject>(a[0]); if (g != null) { g.Layer = (int)a[1]; if (g.Transform.PhysInterest != 0) Physics.PhysWorld.LayerChanged(g); } return null; }   // physics stage 0: the layer hook
    [Icall("UnityEngine.GameObject::get_tag")] static object Tag(object[] a) => N<NGameObject>(a[0])?.Tag ?? "Untagged";
    [Icall("UnityEngine.GameObject::set_tag")] static object SetTag(object[] a) { var g = N<NGameObject>(a[0]); if (g != null) g.Tag = (string)a[1]; return null; }
    [Icall("UnityEngine.GameObject::CompareTag")] static object CompareTag(object[] a) => N<NGameObject>(a[0])?.Tag == (string)a[1];
    [Icall("UnityEngine.GameObject::get_isStatic")] static object IsStatic(object[] a) => N<NGameObject>(a[0])?.IsStatic ?? false;
    [Icall("UnityEngine.GameObject::set_isStatic")] static object SetStatic(object[] a) { var g = N<NGameObject>(a[0]); if (g != null) g.IsStatic = (bool)a[1]; return null; }
    [Icall("UnityEngine.GameObject::get_isStaticBatchable")] static object Batchable(object[] a) => false;
    [Icall("UnityEngine.GameObject::get_sceneCullingMask")] static object CullMask(object[] a) => ulong.MaxValue;
    [Icall("UnityEngine.GameObject::get_scene_Injected")] static object GoScene(object[] a) { a[1] = MakeScene(N<NGameObject>(a[0])?.Scene ?? 0); return null; }
    [Icall("UnityEngine.GameObject::GetScene_Injected")] static object GoSceneById(object[] a) { a[1] = MakeScene(Objects.ById.TryGetValue((int)a[0], out var n) && n is NGameObject g ? g.Scene : 0); return null; }
    [Icall("UnityEngine.GameObject::Internal_AddComponentWithType")] static object AddComp(object[] a) { var g = N<NGameObject>(a[0]); return g == null ? null : Lifecycle.AddComponent(g, (Type)a[1]); }
    [Icall("UnityEngine.GameObject::AddComponentInternal")] static object AddCompByName(object[] a)
    {
        var g = N<NGameObject>(a[0]); var t = TypeByName((string)a[1]); return g == null || t == null ? null : Lifecycle.AddComponent(g, t);
    }
    public static Type TypeByName(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(name) ?? asm.GetType("UnityEngine." + name); if (t != null) return t;
        }
        return null;
    }

    // components
    public static Component First(NGameObject g, Type t) { if (g == null || t == null) return null; foreach (var c in g.Components) if (!c.Destroyed && t.IsInstanceOfType(c.Managed)) return (Component)c.Managed; return null; }
    static unsafe void WriteFast(IntPtr oneFurther, object value) => Unsafe.AsRef<object>((void*)(oneFurther - IntPtr.Size)) = value;
    [Icall("UnityEngine.GameObject::GetComponent")] static object GetComp(object[] a) => First(N<NGameObject>(a[0]), (Type)a[1]);
    [Icall("UnityEngine.GameObject::GetComponentFastPath")] static object GetCompFast(object[] a) { WriteFast((IntPtr)a[2], First(N<NGameObject>(a[0]), (Type)a[1])); return null; }
    [Icall("UnityEngine.GameObject::TryGetComponentFastPath")] static object TryGetCompFast(object[] a) => GetCompFast(a);
    [Icall("UnityEngine.GameObject::TryGetComponentInternal")] static object TryGetComp(object[] a) => GetComp(a);
    [Icall("UnityEngine.GameObject::GetComponentByName")] static object GetCompName(object[] a) { var g = N<NGameObject>(a[0]); return g?.Components.FirstOrDefault(c => c.Managed.GetType().Name == (string)a[1])?.Managed; }
    [Icall("UnityEngine.GameObject::GetComponentByNameWithCase")] static object GetCompName2(object[] a) { var g = N<NGameObject>(a[0]); var cmp = (bool)a[2] ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; return g?.Components.FirstOrDefault(c => string.Equals(c.Managed.GetType().Name, (string)a[1], cmp))?.Managed; }
    [Icall("UnityEngine.GameObject::GetComponentCount")] static object CompCount(object[] a) => N<NGameObject>(a[0])?.Components.Count ?? 0;
    [Icall("UnityEngine.GameObject::QueryComponentAtIndex")] static object CompAt(object[] a) { var g = N<NGameObject>(a[0]); int i = (int)a[1]; return g != null && i >= 0 && i < g.Components.Count ? g.Components[i].Managed : null; }
    [Icall("UnityEngine.GameObject::GetComponentIndex")] static object CompIndex(object[] a) { var g = N<NGameObject>(a[0]); var c = Objects.Get(a[1]); return g?.Components.IndexOf(c as NComponent) ?? -1; }
    [Icall("UnityEngine.GameObject::GetComponentInChildren")] static object CompInChildren(object[] a) => InChildren(N<NGameObject>(a[0]), (Type)a[1], (bool)a[2]);
    [Icall("UnityEngine.GameObject::GetComponentInParent")] static object CompInParent(object[] a) => InParent(N<NGameObject>(a[0]), (Type)a[1], (bool)a[2]);
    public static Component InChildren(NGameObject g, Type t, bool includeInactive)
    {
        if (g == null) return null;
        foreach (var x in Scenes.Walk(g)) { if (!includeInactive && !x.ActiveInHierarchy) continue; var c = First(x, t); if (c is not null) return c; }
        return null;
    }
    public static Component InParent(NGameObject g, Type t, bool includeInactive)
    {
        for (var tr = g?.Transform; tr != null; tr = tr.Parent) { if (!includeInactive && !tr.Go.ActiveInHierarchy) continue; var c = First(tr.Go, t); if (c is not null) return c; }
        return null;
    }
    public static object Collect(NGameObject g, Type t, bool typed, bool recursive, bool includeInactive, bool reverse, object resultList)
    {
        // kit #5 menus: GetComponents(Type, List<Component>) on one object -- uGUI's layout asks it for every element, ~18,000 times when
        // Credits opens -- straight into the caller's list: the same components in the same order, the list cleared first as below
        // (no array of one object, no list of finds, no IList adds). Off unless SHEPHERD_FAST_COMPS=1 (Runtime/MenusCfg.cs says why).
        if (!recursive && t != null && resultList is List<Component> rl && Runtime.MenusCfg.FastComps)
        {
            rl.Clear();
            if (g != null) { var cs = g.Components; for (int i = 0; i < cs.Count; i++) { var c = cs[i]; if (!c.Destroyed && t.IsInstanceOfType(c.Managed)) rl.Add((Component)c.Managed); } }
            return null;
        }
        var found = new List<UObject>();
        if (g != null)
        {
            IEnumerable<NGameObject> gos = !recursive ? new[] { g } : reverse ? Ancestors(g) : Scenes.Walk(g);
            foreach (var x in gos)
            {
                if (recursive && !includeInactive && !x.ActiveInHierarchy) continue;
                foreach (var c in x.Components) if (!c.Destroyed && t.IsInstanceOfType(c.Managed)) found.Add(c.Managed);
            }
        }
        if (resultList is IList list) { list.Clear(); foreach (var f in found) list.Add(f); return null; }
        var arr = Array.CreateInstance(typed ? t : typeof(Component), found.Count);
        for (int i = 0; i < found.Count; i++) arr.SetValue(found[i], i);
        return arr;
    }
    static IEnumerable<NGameObject> Ancestors(NGameObject g) { for (var tr = g.Transform; tr != null; tr = tr.Parent) yield return tr.Go; }
    [Icall("UnityEngine.GameObject::GetComponentsInternal")] static object GetComps(object[] a) => Collect(N<NGameObject>(a[0]), (Type)a[1], (bool)a[2], (bool)a[3], (bool)a[4], (bool)a[5], a[6]);

    [Icall("UnityEngine.GameObject::Find")] static object Find(object[] a)
    {
        var path = (string)a[0]; if (string.IsNullOrEmpty(path)) return null;
        var parts = path.TrimStart('/').Split('/');
        // menu-speed (top hotspots): a part no live GameObject is named -> the walk finds nothing (not while a subtree is being destroyed: its
        // killed records are still linked until the root is unlinked, Lifecycle.DestroyDepth)
        if (Render.HotCfg.On && Lifecycle.DestroyDepth == 0 && NoSuchName(parts))
        {
            if (Render.HotCfg.Check) Render.HotCheck.Ref(Render.HotCheck.Find, null, FindWalk(parts));
            return null;
        }
        return FindWalk(parts);
    }
    static bool NoSuchName(string[] parts) { foreach (var p in parts) if (!Objects.AnyGameObjectNamed(p)) return true; return false; }
    static object FindWalk(string[] parts)
    {
        foreach (var s in Scenes.All.Values.ToArray())
            foreach (var r in s.Roots)
            {
                if (!r.ActiveInHierarchy) continue;
                if (parts.Length == 1) { var g = FindActiveByName(r.Transform, parts[0]); if (g != null) return g.Managed; }
                else if (r.Name == parts[0]) { var t = FindPath(r.Transform, parts, 1, true); if (t != null) return t.Go.Managed; }
            }
        return null;
    }
    /// <summary>Same pre-order as Scenes.Walk, but only through the active part of the hierarchy (an inactive GameObject hides its
    /// whole subtree from Find, so pruning it changes nothing) and without the per-level iterator/ToArray allocations: Beffio
    /// Stylizer calls Find("Pixelator") every frame (a miss = a walk over every scene object; ~1.2 ms Debug before this).</summary>
    static NGameObject FindActiveByName(NTransform t, string name)
    {
        if (t.Go.Name == name) return t.Go;   // caller guarantees t is active in hierarchy
        var ch = t.Children;
        for (int i = 0; i < ch.Count; i++)
        {
            var c = ch[i]; if (!c.Go.ActiveSelf) continue;
            var r = FindActiveByName(c, name); if (r != null) return r;
        }
        return null;
    }
    public static NTransform FindPath(NTransform from, string[] parts, int i, bool activeOnly)
    {
        if (i >= parts.Length) return from;
        foreach (var c in from.Children)
            if (c.Go.Name == parts[i] && (!activeOnly || c.Go.ActiveInHierarchy)) { var r = FindPath(c, parts, i + 1, activeOnly); if (r != null) return r; }
        return null;
    }
    // kit #8 speed: the tag index (Runtime/K8Speed.cs TagIndex: the walk's answers in the walk's order; SHEPHERD_K8_TAGS=0 = the walk below)
    [Icall("UnityEngine.GameObject::FindGameObjectWithTag")] static object FindTag(object[] a) => K8Speed.Tags ? TagIndex.FindOne((string)a[0]) : Scenes.AllGameObjects().FirstOrDefault(g => g.Tag == (string)a[0] && g.ActiveInHierarchy)?.Managed;
    [Icall("UnityEngine.GameObject::FindGameObjectsWithTag")] static object FindTags(object[] a) => K8Speed.Tags ? TagIndex.FindAll((string)a[0]) : Scenes.AllGameObjects().Where(g => g.Tag == (string)a[0] && g.ActiveInHierarchy).Select(g => (GameObject)g.Managed).ToArray();
    /// <summary>Like Unity: each call makes a new GameObject + MeshFilter + MeshRenderer, but they reference SHARED built-in assets
    /// (one "Quad"/"Cube"/... Mesh per primitive type, one default Material) instead of allocating a Mesh and a Material per call.
    /// Beffio Stylizer.Update destroys and rebuilds its Pixelator (+ CreatePrimitive quad) every frame when Pixelate is off, which
    /// leaked one Mesh + one Material record per frame (and paid a Shader.Find scan) before this.</summary>
    [Icall("UnityEngine.GameObject::CreatePrimitive")] static object Primitive(object[] a)
    {
        var type = a[0] is PrimitiveType pt ? pt : (PrimitiveType)Convert.ToInt32(a[0]);
        var go = new GameObject(type.ToString()); var mf = go.AddComponent<MeshFilter>(); var mr = go.AddComponent<MeshRenderer>();
        mf.sharedMesh = BuiltinPrimitiveMesh(type); mr.sharedMaterial = BuiltinPrimitiveMaterial(); return go;
    }
    static readonly Dictionary<PrimitiveType, Mesh> PrimitiveMeshes = new();
    static Material _primitiveMaterial;
    static bool AliveAsset(UObject o) => (object)o != null && Objects.Get(o) is { Destroyed: false };
    static Mesh BuiltinPrimitiveMesh(PrimitiveType type)
    {
        if (PrimitiveMeshes.TryGetValue(type, out var mesh) && AliveAsset(mesh)) return mesh;
        mesh = new Mesh { name = type.ToString() };   // (every type is still the unit quad, as before)
        mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        mesh.triangles = new[] { 0, 3, 1, 3, 0, 2 };
        return PrimitiveMeshes[type] = mesh;
    }
    /// <summary>Polish round 2: Unity's built-in meshes by resource name, as Resources.GetBuiltinResource&lt;Mesh&gt; returns them: "Quad.fbx" is
    /// the same shared unit quad CreatePrimitive(Quad) uses (XY plane, facing -Z, uv 0..1); "Cube.fbx" a unit cube with 4 vertices per
    /// face (24, normals and uv per face); "Plane.fbx" the 10x10-unit XZ plane on an 11x11 vertex grid facing +Y. Others: null.</summary>
    internal static Mesh BuiltinMesh(string name)
    {
        switch (name)
        {
            case "Quad.fbx": return BuiltinPrimitiveMesh(PrimitiveType.Quad);
            case "Cube.fbx":
            {
                if (_builtinCube != null && AliveAsset(_builtinCube)) return _builtinCube;
                var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
                foreach (var (nrm, u, w) in new[] { (Vector3.forward, Vector3.left, Vector3.up), (Vector3.back, Vector3.right, Vector3.up), (Vector3.up, Vector3.right, Vector3.forward), (Vector3.down, Vector3.right, Vector3.back), (Vector3.left, Vector3.back, Vector3.up), (Vector3.right, Vector3.forward, Vector3.up) })
                {
                    int b = v.Count; var c = nrm * 0.5f;
                    v.Add(c - u * 0.5f - w * 0.5f); v.Add(c + u * 0.5f - w * 0.5f); v.Add(c + u * 0.5f + w * 0.5f); v.Add(c - u * 0.5f + w * 0.5f);
                    for (int k = 0; k < 4; k++) n.Add(nrm);
                    uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                    tri.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });   // clockwise seen from outside (Unity's front faces)
                }
                return _builtinCube = new Mesh { name = "Cube", vertices = v.ToArray(), normals = n.ToArray(), uv = uv.ToArray(), triangles = tri.ToArray() };
            }
            case "Plane.fbx":
            {
                if (_builtinPlane != null && AliveAsset(_builtinPlane)) return _builtinPlane;
                var v = new Vector3[121]; var n = new Vector3[121]; var uv = new Vector2[121]; var tri = new List<int>();
                for (int z = 0; z <= 10; z++) for (int x = 0; x <= 10; x++) { int i = z * 11 + x; v[i] = new Vector3(5f - x, 0, 5f - z); n[i] = Vector3.up; uv[i] = new Vector2(1f - x / 10f, 1f - z / 10f); }
                for (int z = 0; z < 10; z++) for (int x = 0; x < 10; x++) { int i = z * 11 + x; tri.AddRange(new[] { i, i + 1, i + 12, i, i + 12, i + 11 }); }
                return _builtinPlane = new Mesh { name = "Plane", vertices = v, normals = n, uv = uv, triangles = tri.ToArray() };
            }
            default: return null;
        }
    }
    static Mesh _builtinCube, _builtinPlane;
    static Material BuiltinPrimitiveMaterial()
    {
        if (AliveAsset(_primitiveMaterial)) return _primitiveMaterial;
        return _primitiveMaterial = new Material(Shader.Find("Unlit/Texture")) { name = "Default-Material" };
    }

    // messages
    public static void Send(NGameObject g, string method, object value, int options, bool upwards = false, bool broadcast = false)
    {
        if (g == null) return;
        IEnumerable<NGameObject> targets = broadcast ? Scenes.Walk(g).Where(x => x.ActiveInHierarchy) : upwards ? Ancestors(g) : new[] { g };
        bool any = false;
        foreach (var x in targets.ToArray())
            foreach (var c in x.Components.ToArray())
            {
                if (c is not NMono m || m.Destroyed) continue;
                for (var t = m.Managed.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    var mi = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).FirstOrDefault(q => q.Name == method && q.GetParameters().Length <= 1);
                    if (mi == null) continue;
                    any = true;
                    try
                    {
                        var r = mi.Invoke(m.Managed, mi.GetParameters().Length == 1 ? new[] { value } : null);
#if !SHEPHERD_EBOOT
                        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: SendMessage / Broadcast targets)
#endif
                        if (r is IEnumerator e) Coroutines.Start(m, e, method);
                    }
                    catch (Exception e)
                    {
                        Log.Exception(e, $"SendMessage {method}");
#if !SHEPHERD_EBOOT
                        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE
#endif
                    }
                    break;
                }
            }
        if (!any && options == 0) Log.Warn($"SendMessage {method} has no receiver on '{g.Name}'");
    }
    [Icall("UnityEngine.GameObject::SendMessage")] static object Msg(object[] a) { Send(N<NGameObject>(a[0]), (string)a[1], a[2], Convert.ToInt32(a[3])); return null; }
    [Icall("UnityEngine.GameObject::SendMessageUpwards")] static object MsgUp(object[] a) { Send(N<NGameObject>(a[0]), (string)a[1], a[2], Convert.ToInt32(a[3]), upwards: true); return null; }
    [Icall("UnityEngine.GameObject::BroadcastMessage")] static object MsgDown(object[] a) { Send(N<NGameObject>(a[0]), (string)a[1], a[2], Convert.ToInt32(a[3]), broadcast: true); return null; }

    // ---------------- Component ----------------
    [Icall("UnityEngine.Component::get_gameObject")] static object CGo(object[] a) => N<NComponent>(a[0])?.Go?.Managed;
    [Icall("UnityEngine.Component::get_transform")] static object CTr(object[] a) => N<NComponent>(a[0])?.Go?.Transform.Managed;
    [Icall("UnityEngine.Component::GetComponentFastPath")] static object CFast(object[] a) { WriteFast((IntPtr)a[2], First(N<NComponent>(a[0])?.Go, (Type)a[1])); return null; }
    [Icall("UnityEngine.Component::GetComponent")] static object CGetByName(object[] a) { var g = N<NComponent>(a[0])?.Go; return g?.Components.FirstOrDefault(c => c.Managed.GetType().Name == (string)a[1])?.Managed; }
    [Icall("UnityEngine.Component::GetComponentsForListInternal")] static object CList(object[] a) => Collect(N<NComponent>(a[0])?.Go, (Type)a[1], true, false, true, false, a[2]);
    [Icall("UnityEngine.Component::GetComponentIndex")] static object CIndex(object[] a) { var c = N<NComponent>(a[0]); return c?.Go?.Components.IndexOf(c) ?? -1; }
    [Icall("UnityEngine.Component::SendMessage")] static object CMsg(object[] a) { Send(N<NComponent>(a[0])?.Go, (string)a[1], a[2], Convert.ToInt32(a[3])); return null; }
    [Icall("UnityEngine.Component::SendMessageUpwards")] static object CMsgUp(object[] a) { Send(N<NComponent>(a[0])?.Go, (string)a[1], a[2], Convert.ToInt32(a[3]), upwards: true); return null; }
    [Icall("UnityEngine.Component::BroadcastMessage")] static object CMsgDown(object[] a) { Send(N<NComponent>(a[0])?.Go, (string)a[1], a[2], Convert.ToInt32(a[3]), broadcast: true); return null; }

    // ---------------- Behaviour / MonoBehaviour ----------------
    [Icall("UnityEngine.Behaviour::get_enabled")] static object Enabled(object[] a) => Boxes.Of(Objects.Get(a[0]) is NBehaviour b ? b.Enabled : (Objects.Get(a[0])?.Get("enabled") as bool? ?? true));
    [Icall("UnityEngine.Behaviour::set_enabled")] static object SetEnabled(object[] a) { if (Objects.Get(a[0]) is NBehaviour b) Lifecycle.SetEnabled(b, (bool)a[1]); return null; }
    [Icall("UnityEngine.Behaviour::get_isActiveAndEnabled")] static object ActEn(object[] a) => Objects.Get(a[0]) is NBehaviour b && b.Enabled && b.Go != null && b.Go.ActiveInHierarchy && (b is not NMono m || m.IsEnabledCalled);
    [Icall("UnityEngine.MonoBehaviour::StartCoroutineManaged2")] static object StartCo(object[] a)
    {
        var m = N<NMono>(a[0]); if (m == null) return null;
        if (!m.Go.ActiveInHierarchy) { Log.Warn($"Coroutine couldn't be started because the game object '{m.Name}' is inactive!"); return null; }
        return Coroutines.Start(m, (IEnumerator)a[1]);
    }
    [Icall("UnityEngine.MonoBehaviour::StartCoroutineManaged")] static object StartCoName(object[] a)
    {
        var m = N<NMono>(a[0]); if (m == null) return null;
        var mi = Invokes.FindMethod(m.Managed.GetType(), (string)a[1]); if (mi == null) { Log.Error($"StartCoroutine: no method {a[1]}"); return null; }
        var e = mi.Invoke(m.Managed, mi.GetParameters().Length == 1 ? new[] { a[2] } : null) as IEnumerator;
        return Coroutines.Start(m, e, (string)a[1]);
    }
    [Icall("UnityEngine.MonoBehaviour::StopCoroutine")] static object StopCoName(object[] a) { var m = N<NMono>(a[0]); if (m != null) Coroutines.Stop(m, (string)a[1]); return null; }
    [Icall("UnityEngine.MonoBehaviour::StopCoroutineManaged")] static object StopCo(object[] a) { Coroutines.Stop((Coroutine)a[1]); return null; }
    [Icall("UnityEngine.MonoBehaviour::StopCoroutineFromEnumeratorManaged")] static object StopCoE(object[] a) { var m = N<NMono>(a[0]); if (m != null) Coroutines.Stop(m, (IEnumerator)a[1]); return null; }
    [Icall("UnityEngine.MonoBehaviour::StopAllCoroutines")] static object StopAll(object[] a) { var m = N<NMono>(a[0]); if (m != null) Coroutines.StopAll(m); return null; }
    [Icall("UnityEngine.Coroutine::ReleaseCoroutine")] static object ReleaseCo(object[] a) => null;
    [Icall("UnityEngine.MonoBehaviour::InvokeDelayed")] static object InvokeD(object[] a) { var m = N<NMono>(a[0]); if (m != null) Invokes.Add(m, (string)a[1], (float)a[2], (float)a[3]); return null; }
    [Icall("UnityEngine.MonoBehaviour::CancelInvoke")] static object CancelInv(object[] a) { var m = N<NMono>(a[0]); if (m != null) Invokes.Cancel(m, (string)a[1]); return null; }
    [Icall("UnityEngine.MonoBehaviour::Internal_CancelInvokeAll")] static object CancelAllInv(object[] a) { var m = N<NMono>(a[0]); if (m != null) Invokes.CancelAll(m); return null; }
    [Icall("UnityEngine.MonoBehaviour::IsInvoking")] static object IsInv(object[] a) { var m = N<NMono>(a[0]); return m != null && Invokes.IsInvoking(m, (string)a[1]); }
    [Icall("UnityEngine.MonoBehaviour::Internal_IsInvokingAll")] static object IsInvAll(object[] a) { var m = N<NMono>(a[0]); return m != null && Invokes.IsInvoking(m, null); }
    [Icall("UnityEngine.MonoBehaviour::IsObjectMonoBehaviour")] static object IsMB(object[] a) => a[0] is MonoBehaviour;
    [Icall("UnityEngine.MonoBehaviour::GetScriptClassName")] static object ScriptName(object[] a) => a[0]?.GetType().Name;
    [Icall("UnityEngine.MonoBehaviour::get_useGUILayout")] static object GuiLayout(object[] a) => true;
    [Icall("UnityEngine.MonoBehaviour::set_useGUILayout")] static object SetGuiLayout(object[] a) => null;
    [Icall("UnityEngine.MonoBehaviour::OnCancellationTokenCreated")] static object CancelTok(object[] a) => null;

    // ---------------- ScriptableObject ----------------
    [Icall("UnityEngine.ScriptableObject::CreateScriptableObject")] static object CreateSO(object[] a)
    {
        var so = (UObject)a[0]; if (Objects.Get(so) == null) Objects.Bind(so, new NObj { Name = "" }); return null;
    }
    [Icall("UnityEngine.ScriptableObject::CreateScriptableObjectInstanceFromType")] static object CreateSOType(object[] a)
    {
        var t = (Type)a[0]; if (t == null || t.IsAbstract) return null;
        var so = Objects.NewManaged(t); var n = Objects.Bind(so, new NObj { Name = "" });
        Objects.RunCtor(so); Lifecycle.ScriptableAwakeEnable(n); return so;
    }
    [Icall("UnityEngine.ScriptableObject::CreateScriptableObjectInstanceFromName")] static object CreateSOName(object[] a) => CreateSOType(new object[] { TypeByName((string)a[0]), true });
    [Icall("UnityEngine.ScriptableObject::ResetAndApplyDefaultInstances")] static object ResetSO(object[] a) => null;
    [Icall("UnityEngine.ScriptableObject::SetDirty")] static object SODirty(object[] a) => null;
}
