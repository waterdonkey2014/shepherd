namespace Shepherd.Assets;

/// <summary>A GameObject of a loaded scene (or prefab) with its components and transform children.</summary>
public sealed class SceneNode
{
    public SerializedFile File;
    public ObjectInfo GameObject;
    public TypeTreeObject GameObjectData;   // m_Name, m_IsActive, m_Layer, m_Tag, m_Component
    public string Name;
    public bool IsActive;
    public int Layer;
    public ObjectInfo Transform;
    public TypeTreeObject TransformData;
    public readonly List<SceneComponent> Components = new();
    public readonly List<SceneNode> Children = new();
    public SceneNode Parent;
    public override string ToString() => Name;
}

public sealed class SceneComponent
{
    public SerializedFile File;      // file the component lives in
#if !SHEPHERD_K11_TOC
    public ObjectInfo Object;        // ObjectInfo.None (IsNull) if unresolved
#else
    public ObjectInfo Object;        // null if unresolved
#endif
    public PPtr Pointer;
    public int ClassId;
    public MonoScriptInfo Script;    // MonoBehaviours only (null if unresolved)
    public string DisplayName =>
#if !SHEPHERD_K11_TOC
        Object.IsNull ? $"<unresolved {Pointer}>" :
#else
        Object == null ? $"<unresolved {Pointer}>" :
#endif
        ClassId == ClassIds.MonoBehaviour ? (Script?.FullName ?? "MonoBehaviour<?>") : ClassIds.Name(ClassId);
}

/// <summary>Builds the GameObject/Transform hierarchy of a serialized file (scene or prefab container).</summary>
public static class SceneGraph
{
    public sealed class Result
    {
        public List<SceneNode> Roots = new();
        public Dictionary<long, SceneNode> ByGameObject = new();
        public int MonoBehaviours, MonoScriptsResolved;
        public List<string> Warnings = new();
    }

    public static Result Build(AssetDatabase db, SerializedFile file)
    {
        var res = new Result();
        var byTransform = new Dictionary<long, SceneNode>();
        // pass 1: game objects
        foreach (var o in file.ObjectsOfClass(ClassIds.GameObject))
        {
            TypeTreeObject go;
            try { go = TypeTreeReader.Read(file, o); }
            catch (Exception e) { res.Warnings.Add($"GameObject {o.PathId}: {e.Message}"); continue; }
            var node = new SceneNode
            {
                File = file, GameObject = o, GameObjectData = go,
                Name = go.Str("m_Name") ?? "", IsActive = go.Bool("m_IsActive"), Layer = go.Int("m_Layer"),
            };
            res.ByGameObject[o.PathId] = node;
            if (go.List("m_Component") is { } comps)
            {
                foreach (var c in comps)
                {
                    var ptr = c is TypeTreeObject pair ? pair.PPtr("component") : default;
                    var (cf, co) = db.Resolve(file, ptr);
#if !SHEPHERD_K11_TOC
                    var sc = new SceneComponent { File = cf, Object = co, Pointer = ptr, ClassId = co.IsNull ? 0 : co.ClassId };
                    if (!co.IsNull && (co.ClassId == ClassIds.Transform || co.ClassId == ClassIds.RectTransform) && node.Transform.IsNull)
#else
                    var sc = new SceneComponent { File = cf, Object = co, Pointer = ptr, ClassId = co?.ClassId ?? 0 };
                    if (co != null && (co.ClassId == ClassIds.Transform || co.ClassId == ClassIds.RectTransform) && node.Transform == null)
#endif
                    {
                        node.Transform = co;
                        byTransform[co.PathId] = node;
                    }
#if !SHEPHERD_K11_TOC
                    if (!co.IsNull && co.ClassId == ClassIds.MonoBehaviour)
#else
                    if (co?.ClassId == ClassIds.MonoBehaviour)
#endif
                    {
                        res.MonoBehaviours++;
                        sc.Script = db.GetMonoScript(cf, co);
                        if (sc.Script != null) res.MonoScriptsResolved++;
                    }
                    node.Components.Add(sc);
                }
            }
        }
        // pass 2: transforms -> parent/children (children order from m_Children)
        foreach (var node in res.ByGameObject.Values)
        {
#if !SHEPHERD_K11_TOC
            if (node.Transform.IsNull) continue;
#else
            if (node.Transform == null) continue;
#endif
            try { node.TransformData = TypeTreeReader.Read(file, node.Transform); }
            catch (Exception e) { res.Warnings.Add($"Transform {node.Transform.PathId}: {e.Message}"); }
        }
        foreach (var node in res.ByGameObject.Values)
        {
            var t = node.TransformData;
            if (t == null) continue;
            var father = t.PPtr("m_Father");
            if (!father.IsNull && father.FileId == 0 && byTransform.TryGetValue(father.PathId, out var p)) node.Parent = p;
            if (t.List("m_Children") is { } kids)
                foreach (var k in kids)
                    if (k is PPtr kp && kp.FileId == 0 && byTransform.TryGetValue(kp.PathId, out var child)) node.Children.Add(child);
        }
        // roots: SceneRoots order when present, else file order
        var rootOrder = new List<long>();
        foreach (var o in file.ObjectsOfClass(ClassIds.SceneRoots))
        {
            try
            {
                var sr = TypeTreeReader.Read(file, o);
                if (sr.List("m_Roots") is { } roots)
                    foreach (var r in roots) if (r is PPtr rp && rp.FileId == 0) rootOrder.Add(rp.PathId);
            }
            catch { }
        }
        var added = new HashSet<SceneNode>();
        foreach (var tid in rootOrder)
            if (byTransform.TryGetValue(tid, out var n) && n.Parent == null && added.Add(n)) res.Roots.Add(n);
        foreach (var o in file.Objects)
        {
            if (o.ClassId != ClassIds.GameObject) continue;
            if (res.ByGameObject.TryGetValue(o.PathId, out var n) && n.Parent == null && added.Add(n)) res.Roots.Add(n);
        }
        return res;
    }
}
