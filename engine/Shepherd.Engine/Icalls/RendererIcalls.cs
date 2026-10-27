using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

/// <summary>Renderer material slots (shared vs per-renderer instances) and MaterialPropertyBlock storage.</summary>
public static class RendererIcalls
{
    static NComponent R(object o) => Objects.Get<NComponent>(o);
    public static Material[] Shared(NComponent r)
    {
        if (r == null) return Array.Empty<Material>();
        var v = r.Get("sharedMaterials");
        if (v is Material[] arr) return arr;
        var conv = Conv.To(v, typeof(Material[])) as Material[] ?? (Conv.To(r.Get("sharedMaterial"), typeof(Material)) is Material m ? new[] { m } : Array.Empty<Material>());
        r.Set("sharedMaterials", conv); return conv;
    }
    static Material[] Instances(NComponent r)
    {
        var shared = Shared(r); var inst = r.Get("__instMats") as Material[];
        if (inst != null && inst.Length == shared.Length) return inst;
        inst = shared.Select(m => m == null ? null : (Material)Cloner.Clone(m, null, false, null, null)).ToArray();
        r.Set("__instMats", inst); r.Set("sharedMaterials", inst); return inst;
    }
    [Icall("UnityEngine.Renderer::GetSharedMaterial")] static object GetShared(object[] a) => Shared(R(a[0])).FirstOrDefault();
    [Icall("UnityEngine.Renderer::GetMaterial")] static object GetInst(object[] a)
    {
        var r = R(a[0]);
        if (K6Engine.DefaultMaterial && r != null && Shared(r) is var sh && (sh.Length == 0 || sh[0] == null)) return DefaultInstance(r, sh);   // kit #6 (a)
        return Instances(r).FirstOrDefault();
    }
    /// <summary>Kit #6 (a): Renderer.material on a renderer whose first slot is empty (no materials, or a null one). Unity's
    /// GetAndAssignInstantiatedMaterial (read from the game's UnityPlayer) takes the default material in its place, instantiates it for this
    /// renderer, grows the material array to the slot and assigns the instance there, and returns it -- never null. Kit #5 returned null:
    /// UIDodgePromptTutorial.Play (the intro's first hit on the Lamb) set timeScale 0, then threw on the swordsman's empty 'Warning Icon'
    /// before the coroutine that restores it, and the game froze. The other slots get instances as GetInst's own rule gives them.</summary>
    static Material DefaultInstance(NComponent r, Material[] shared)
    {
        var d = (Material)RenderIcalls.NewMaterial(RenderIcalls.ShaderNamed(K6Engine.DefaultShaderName), K6Engine.DefaultMaterialInstanceName).Managed;
        var inst = new Material[Math.Max(1, shared.Length)]; inst[0] = d;
        for (int i = 1; i < shared.Length; i++) inst[i] = shared[i] == null ? null : (Material)Cloner.Clone(shared[i], null, false, null, null);
        r.Set("__instMats", inst); r.Set("sharedMaterials", inst);
        return d;
    }
    [Icall("UnityEngine.Renderer::SetMaterial")] static object SetMat(object[] a) { var r = R(a[0]); if (r != null) { r.Set("sharedMaterials", new[] { (Material)a[1] }); r.RemoveProp("__instMats"); } return null; }
    [Icall("UnityEngine.Renderer::GetSharedMaterialArray")] static object GetSharedArr(object[] a) => (Material[])Shared(R(a[0])).Clone();
    [Icall("UnityEngine.Renderer::GetMaterialArray")] static object GetInstArr(object[] a) => (Material[])Instances(R(a[0])).Clone();
    [Icall("UnityEngine.Renderer::SetMaterialArray")] static object SetArr(object[] a)
    {
        var r = R(a[0]); var arr = (Material[])a[1] ?? Array.Empty<Material>(); int n = a.Length > 2 && a[2] is int len ? Math.Min(len, arr.Length) : arr.Length;
        if (r != null) { var first = new Material[Math.Max(0, n)]; Array.Copy(arr, first, first.Length); r.Set("sharedMaterials", first); r.RemoveProp("__instMats"); } return null;   // (was arr.Take(n).ToArray())
    }
    [Icall("UnityEngine.Renderer::GetMaterialCount")] static object Count(object[] a) => Shared(R(a[0])).Length;
    [Icall("UnityEngine.Renderer::CopySharedMaterialArray")] static object CopyShared(object[] a) { a[1] = (Material[])Shared(R(a[0])).Clone(); return null; }
    [Icall("UnityEngine.Renderer::CopyMaterialArray")] static object CopyInst(object[] a) { a[1] = (Material[])Instances(R(a[0])).Clone(); return null; }
    [Icall("UnityEngine.Renderer::get_isVisible")] static object Visible(object[] a) => !Visibility.On || (Visibility.IsVisible(R(a[0])) ?? true);   // kit #8 SHEPHERD_K8_VISIBILITY=1 (opt-in): from the culling; off = kit #7's true
    static readonly Func<Vector3, object> UnitBoundsBox = c => { var bb = Um.ToBounds(c, Um.One3); if (HopCheck.On) HopCheck.Bnd(HopCheck.BoundsIcall, bb, new UnityEngine.Bounds(c, Vector3.one)); return bb; };
    [Icall("UnityEngine.Renderer::get_bounds_Injected")] static object Bounds(object[] a)
    {   // new Bounds(position or Vector3.zero, Vector3.one) (hop fix: through Um, the same fields: centre, extents = (1,1,1) * 0.5f)
        var r = R(a[0]); var tr = r?.Go?.Transform;
        if (tr != null && Render.HotCfg.On) { a[1] = tr.PositionBox(UnitBoundsBox); return null; }   // menu-speed (top hotspots): one box per unchanged position
        var c = tr?.Position ?? Um.Zero3; var bb = Um.ToBounds(c, Um.One3); a[1] = bb;
        if (HopCheck.On) { HopCheck.V3(HopCheck.BoundsIcall, Um.Zero3, Vector3.zero); HopCheck.Bnd(HopCheck.BoundsIcall, bb, new UnityEngine.Bounds(c, Vector3.one)); }
        return null;
    }
    [Icall("UnityEngine.Renderer::get_localToWorldMatrix_Injected")] static object L2W(object[] a) { a[1] = R(a[0])?.Go?.Transform.LocalToWorld ?? Matrix4x4.identity; return null; }
    [Icall("UnityEngine.Renderer::get_worldToLocalMatrix_Injected")] static object W2L(object[] a) { a[1] = R(a[0])?.Go?.Transform.WorldToLocal ?? Matrix4x4.identity; return null; }

    // ---- sorting layers (polish round 1, survey item 10): Unity keeps a renderer's layer as its unique ID; the name is a view of it.
    // Before, set_sortingLayerName only filled an auto-prop bag key the renderer never read, so script-assigned layers
    // (GenerateRoom: the room back sprite -> "Island") drew in Default. TagManager's list (Loader.SortingLayers) is the source. ----
    static int LayerId(NComponent r) => r?.Get("sortingLayerID") is { } v ? unchecked((int)Convert.ToInt64(v)) : 0;
    static int IdOfName(string name) { foreach (var (n, id) in Assets.Loader.SortingLayers) if (n == name) return unchecked((int)id); return 0; }
    static string NameOfId(int id) { foreach (var (n, i) in Assets.Loader.SortingLayers) if (unchecked((int)i) == id) return n; return null; }
    static int IndexOfId(int id) { var l = Assets.Loader.SortingLayers; for (int i = 0; i < l.Count; i++) if (unchecked((int)l[i].id) == id) return i; return -1; }
    /// <summary>SortingLayer.value: the layer's place in the TagManager order, relative to Default (Default = 0, layers listed before it
    /// negative); 0 for an unknown ID.</summary>
    static int ValueOfId(int id) { int i = IndexOfId(id), d = IndexOfId(0); return i < 0 ? 0 : d < 0 ? i : i - d; }
    [Icall("UnityEngine.Renderer::get_sortingLayerName")] [Icall("UnityEngine.Rendering.SortingGroup::get_sortingLayerName")] [Icall("UnityEngine.Canvas::get_sortingLayerName")]
    static object GetLayerName(object[] a) => NameOfId(LayerId(R(a[0]))) ?? NameOfId(0) ?? "Default";
    [Icall("UnityEngine.Renderer::set_sortingLayerName")] [Icall("UnityEngine.Rendering.SortingGroup::set_sortingLayerName")] [Icall("UnityEngine.Canvas::set_sortingLayerName")]
    static object SetLayerName(object[] a) { R(a[0])?.Set("sortingLayerID", IdOfName((string)a[1])); return null; }
    [Icall("UnityEngine.Renderer::get_sortingLayerID")] [Icall("UnityEngine.Rendering.SortingGroup::get_sortingLayerID")] [Icall("UnityEngine.Canvas::get_sortingLayerID")]
    static object GetLayerId(object[] a) => LayerId(R(a[0]));
    [Icall("UnityEngine.Renderer::set_sortingLayerID")] [Icall("UnityEngine.Rendering.SortingGroup::set_sortingLayerID")] [Icall("UnityEngine.Canvas::set_sortingLayerID")]
    static object SetLayerId(object[] a) { R(a[0])?.Set("sortingLayerID", (int)a[1]); return null; }
    [Icall("UnityEngine.Canvas::get_cachedSortingLayerValue")] static object CanvasLayerValue(object[] a) => ValueOfId(LayerId(R(a[0])));
    [Icall("UnityEngine.SortingLayer::NameToID")] static object NameToId(object[] a) => IdOfName((string)a[0]);
    [Icall("UnityEngine.SortingLayer::IDToName")] static object IdToName(object[] a) => NameOfId((int)a[0]) ?? "<unknown layer>";
    [Icall("UnityEngine.SortingLayer::IsValid")] static object LayerValid(object[] a) => IndexOfId((int)a[0]) >= 0;
    [Icall("UnityEngine.SortingLayer::GetLayerValueFromID")] static object ValueFromId(object[] a) => ValueOfId((int)a[0]);
    [Icall("UnityEngine.SortingLayer::GetLayerValueFromName")] static object ValueFromName(object[] a) => ValueOfId(IdOfName((string)a[0]));
    [Icall("UnityEngine.SortingLayer::GetSortingLayerIDsInternal")] static object LayerIds(object[] a) => Assets.Loader.SortingLayers.Select(l => unchecked((int)l.id)).ToArray();

    // ---- MaterialPropertyBlock: native-backed (m_Ptr) -> per-handle property dictionaries ----
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Dictionary<int, object>> Blocks = new(); static long _next = 0x9000000;   // finalizer thread destroys
    static Dictionary<int, object> B(object mpb)
    {
        var p = (IntPtr)typeof(MaterialPropertyBlock).GetField("m_Ptr", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mpb);
        return Blocks.GetOrAdd(p, _ => new());
    }
    [Icall("UnityEngine.MaterialPropertyBlock::CreateImpl")] static object Create(object[] a) { var p = new IntPtr(Interlocked.Increment(ref _next)); Blocks[p] = new(); return p; }
    [Icall("UnityEngine.MaterialPropertyBlock::DestroyImpl")] static object Destroy(object[] a) { Blocks.TryRemove((IntPtr)a[0], out _); return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::Clear")] static object Clear(object[] a) { B(a[0]).Clear(); return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::get_isEmpty")] static object Empty(object[] a) => B(a[0]).Count == 0;
    [Icall("UnityEngine.MaterialPropertyBlock::SetFloatImpl")] static object SetF(object[] a) { B(a[0])[(int)a[1]] = a[2]; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::SetIntImpl")] static object SetI(object[] a) { B(a[0])[(int)a[1]] = a[2]; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::SetVectorImpl_Injected")] static object SetV(object[] a) { B(a[0])[(int)a[1]] = a[2]; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::SetColorImpl_Injected")] static object SetC(object[] a) { B(a[0])[(int)a[1]] = a[2]; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::SetMatrixImpl_Injected")] static object SetM(object[] a) { B(a[0])[(int)a[1]] = a[2]; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::SetTextureImpl")] static object SetT(object[] a) { B(a[0])[(int)a[1]] = a[2]; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::GetFloatImpl")] static object GetF(object[] a) => B(a[0]).TryGetValue((int)a[1], out var v) ? Conv.To(v, typeof(float)) : 0f;
    [Icall("UnityEngine.MaterialPropertyBlock::GetIntImpl")] static object GetI(object[] a) => B(a[0]).TryGetValue((int)a[1], out var v) ? Conv.To(v, typeof(int)) : 0;
    [Icall("UnityEngine.MaterialPropertyBlock::GetVectorImpl_Injected")] static object GetV(object[] a) { a[2] = B(a[0]).TryGetValue((int)a[1], out var v) && v is Vector4 q ? q : Vector4.zero; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::GetColorImpl_Injected")] static object GetC(object[] a) { a[2] = B(a[0]).TryGetValue((int)a[1], out var v) && v is Color c ? c : Color.white; return null; }
    [Icall("UnityEngine.MaterialPropertyBlock::GetTextureImpl")] static object GetT(object[] a) => B(a[0]).TryGetValue((int)a[1], out var v) ? v as Texture : null;
    [Icall("UnityEngine.MaterialPropertyBlock::HasPropertyImpl")] static object Has(object[] a) => B(a[0]).ContainsKey((int)a[1]);
    [Icall("UnityEngine.Renderer::Internal_SetPropertyBlock")] static object SetBlock(object[] a) { var r = R(a[0]); if (r != null) r.Set("__mpb", a[1] == null ? null : new Dictionary<int, object>(B(a[1]))); return null; }
    [Icall("UnityEngine.Renderer::Internal_SetPropertyBlockMaterialIndex")] static object SetBlockIdx(object[] a) => SetBlock(a);
    [Icall("UnityEngine.Renderer::Internal_GetPropertyBlock")] static object GetBlock(object[] a) { var r = R(a[0]); var d = B(a[1]); d.Clear(); if (r?.Get("__mpb") is Dictionary<int, object> s) foreach (var kv in s) d[kv.Key] = kv.Value; return null; }
    [Icall("UnityEngine.Renderer::Internal_GetPropertyBlockMaterialIndex")] static object GetBlockIdx(object[] a) => GetBlock(a);
    [Icall("UnityEngine.Renderer::HasPropertyBlock")] static object HasBlock(object[] a) => R(a[0])?.Get("__mpb") is Dictionary<int, object> d && d.Count > 0;
}
