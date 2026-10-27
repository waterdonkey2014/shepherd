using System.Collections;
using System.Reflection;
using Shepherd.Assets;
using Shepherd.Engine.Assets;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;
using UPPtr = Shepherd.Assets.PPtr;
namespace Shepherd.Engine.Impl;

public sealed class NAssetBundle : NObj
{
    public string Path; public IReadOnlyList<SerializedFile> Files; public SerializedFile Owner;
    public readonly Dictionary<string, List<UPPtr>> Container = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> SceneToCab = new(StringComparer.OrdinalIgnoreCase);
    public bool IsScene; public UPPtr MainAsset;
}

static class BundleIcalls
{
    static readonly List<NAssetBundle> Loaded = new();
    static object V(TypeTreeObject t, string k) => t != null && t.TryGet(k, out var v) ? v : null;

    public static NAssetBundle Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var existing = Loaded.FirstOrDefault(b => !b.Destroyed && string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var files = Loader.Db.LoadBundleFiles(path);
        var ab = new NAssetBundle { Path = path, Files = files, Name = System.IO.Path.GetFileName(path) };
        foreach (var f in files)
            foreach (var o in f.ObjectsOfClass(142))
            {
                var d = TypeTreeReader.Read(f, o); ab.Owner = f;
                foreach (var e in (V(d, "m_Container") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>())
                {
                    var key = V(e, "first") as string; var info = V(e, "second") as TypeTreeObject;
                    if (key == null || V(info, "asset") is not UPPtr p) continue;
                    (ab.Container.TryGetValue(key, out var l) ? l : ab.Container[key] = new()).Add(p);
                }
                foreach (var e in (V(d, "m_SceneHashes") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>())
                    if (V(e, "first") is string sp && V(e, "second") is string cab) ab.SceneToCab[sp] = cab;
                ab.IsScene = V(d, "m_IsStreamedSceneAssetBundle") is { } iss && Convert.ToInt64(iss) != 0 || ab.SceneToCab.Count > 0;
                if (V(d, "m_MainAsset") is TypeTreeObject ma && V(ma, "asset") is UPPtr mp) ab.MainAsset = mp;
            }
        Objects.Bind(Objects.NewManaged(typeof(AssetBundle)), ab, true);
        Loaded.Add(ab);
        return ab;
    }
    static NAssetBundle B(object o) => Objects.Get<NAssetBundle>(o);

    public static UObject[] Find(NAssetBundle ab, string name, Type type, bool all)
    {
        if (ab == null || name == null) return Array.Empty<UObject>();
        if (!ab.Container.TryGetValue(name, out var ptrs))
        {   // Unity also accepts the bare file name / name without extension
            var k = ab.Container.Keys.FirstOrDefault(x => string.Equals(System.IO.Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase) || string.Equals(System.IO.Path.GetFileNameWithoutExtension(x), name, StringComparison.OrdinalIgnoreCase));
            if (k == null) return Array.Empty<UObject>(); ptrs = ab.Container[k];
        }
        var res = new List<UObject>();
        foreach (var p in ptrs)
        {
            var m = Loader.Managed(ab.Owner, p); if (m == null) continue;
            if (type == null || type.IsInstanceOfType(m)) { res.Add(m); if (!all) break; }
            else if (m is GameObject go && typeof(Component).IsAssignableFrom(type)) { var c = Core.First(Objects.Get<NGameObject>(go), type); if (c != null) { res.Add(c); if (!all) break; } }
        }
        return res.ToArray();
    }

    [Icall("UnityEngine.AssetBundle::LoadFromFile_Internal")] static object LoadFile(object[] a) { try { return Open((string)a[0]).Managed; } catch (Exception e) { Log.Error($"AssetBundle.LoadFromFile({a[0]}): {e.Message}"); return null; } }
    [Icall("UnityEngine.AssetBundle::LoadFromFileAsync_Internal")] static object LoadFileAsync(object[] a)
    {
        UObject result = null; try { result = Open((string)a[0]).Managed; } catch (Exception e) { Log.Error($"AssetBundle.LoadFromFileAsync({a[0]}): {e.Message}"); }
        return AsyncOps.Create<AssetBundleCreateRequest>(poll: () => true, result: result);
    }
    // polish round 1 (survey item 1): reading a request's result completes it on the spot, like Unity (AsyncOps.CompleteNow)
    [Icall("UnityEngine.AssetBundleCreateRequest::get_assetBundle")] static object CreateReqBundle(object[] a) { AsyncOps.CompleteNow((AsyncOperation)a[0]); return AsyncOps.Of((AsyncOperation)a[0])?.Result; }
    [Icall("UnityEngine.AssetBundleCreateRequest::SetEnableCompatibilityChecks")] static object Compat(object[] a) => null;
    [Icall("UnityEngine.AssetBundle::LoadAsset_Internal")] static object LoadAsset(object[] a) => Find(B(a[0]), (string)a[1], (Type)a[2], false).FirstOrDefault();
    [Icall("UnityEngine.AssetBundle::LoadAssetWithSubAssets_Internal")] static object LoadSub(object[] a) => Find(B(a[0]), (string)a[1], (Type)a[2], true);
    [Icall("UnityEngine.AssetBundle::LoadAssetAsync_Internal")] static object LoadAssetAsync(object[] a)
    {
        var r = Find(B(a[0]), (string)a[1], (Type)a[2], false); if (r.Length == 0) Log.Warn($"AssetBundle.LoadAssetAsync('{a[1]}', {((Type)a[2])?.Name}) found nothing in {B(a[0])?.Name}");
        return AsyncOps.Create<AssetBundleRequest>(poll: () => true, result: r);
    }
    [Icall("UnityEngine.AssetBundle::LoadAssetWithSubAssetsAsync_Internal")] static object LoadSubAsync(object[] a) => AsyncOps.Create<AssetBundleRequest>(poll: () => true, result: Find(B(a[0]), (string)a[1], (Type)a[2], true));
    [Icall("UnityEngine.AssetBundleRequest::GetResult")] static object ReqResult(object[] a) { AsyncOps.CompleteNow((AsyncOperation)a[0]); return (AsyncOps.Of((AsyncOperation)a[0])?.Result as UObject[])?.FirstOrDefault(); }
    [Icall("UnityEngine.AssetBundleRequest::get_allAssets")] static object ReqAll(object[] a) { AsyncOps.CompleteNow((AsyncOperation)a[0]); return AsyncOps.Of((AsyncOperation)a[0])?.Result as UObject[] ?? Array.Empty<UObject>(); }
    [Icall("UnityEngine.AssetBundle::Contains")] static object Contains(object[] a) => B(a[0])?.Container.ContainsKey((string)a[1]) ?? false;
    [Icall("UnityEngine.AssetBundle::GetAllAssetNames")] static object Names(object[] a) => B(a[0])?.Container.Keys.ToArray() ?? Array.Empty<string>();
    [Icall("UnityEngine.AssetBundle::GetAllScenePaths")] static object ScenePaths(object[] a) => B(a[0])?.SceneToCab.Keys.ToArray() ?? Array.Empty<string>();
    [Icall("UnityEngine.AssetBundle::get_isStreamedSceneAssetBundle")] static object IsScene(object[] a) => B(a[0])?.IsScene ?? false;
    [Icall("UnityEngine.AssetBundle::returnMainAsset")] static object MainAsset(object[] a) { var b = B(a[0]); return b == null || b.MainAsset.IsNull ? null : Loader.Managed(b.Owner, b.MainAsset); }
    [Icall("UnityEngine.AssetBundle::Unload")] static object Unload(object[] a) { var b = B(a[0]); if (b != null) { Loaded.Remove(b); Objects.Kill(b); } return null; }
    [Icall("UnityEngine.AssetBundle::UnloadAsync")] static object UnloadAsync(object[] a) { Unload(a); return AsyncOps.Create<AssetBundleUnloadOperation>(poll: () => true); }
    [Icall("UnityEngine.AssetBundle::UnloadAllAssetBundles")] static object UnloadAll(object[] a) { foreach (var b in Loaded.ToArray()) Objects.Kill(b); Loaded.Clear(); return null; }
    [Icall("UnityEngine.AssetBundle::GetAllLoadedAssetBundles_Native")] static object AllLoaded(object[] a) => Loaded.Where(b => !b.Destroyed).Select(b => (AssetBundle)b.Managed).ToArray();

    // ---------------- AsyncOperation ----------------
    [Icall("UnityEngine.AsyncOperation::get_isDone")] static object Done(object[] a) => AsyncOps.IsDone((AsyncOperation)a[0]);
    [Icall("UnityEngine.AsyncOperation::get_progress")] static object Progress(object[] a) => AsyncOps.Of((AsyncOperation)a[0])?.Progress ?? 1f;
    [Icall("UnityEngine.AsyncOperation::get_priority")] static object Prio(object[] a) => AsyncOps.Of((AsyncOperation)a[0])?.Priority ?? 0;
    [Icall("UnityEngine.AsyncOperation::set_priority")] static object SetPrio(object[] a) { var o = AsyncOps.Of((AsyncOperation)a[0]); if (o != null) o.Priority = (int)a[1]; return null; }
    [Icall("UnityEngine.AsyncOperation::get_allowSceneActivation")] static object Allow(object[] a) => AsyncOps.Of((AsyncOperation)a[0])?.AllowActivation ?? true;
    [Icall("UnityEngine.AsyncOperation::set_allowSceneActivation")] static object SetAllow(object[] a) { var o = AsyncOps.Of((AsyncOperation)a[0]); if (o != null) o.AllowActivation = (bool)a[1]; return null; }
    [Icall("UnityEngine.AsyncOperation::InternalDestroy")] static object Destroy(object[] a) => null;

    // ---------------- scenes ----------------
    static readonly MethodInfo SceneLoaded = typeof(SceneManager).GetMethod("Internal_SceneLoaded", BindingFlags.Static | BindingFlags.NonPublic);
    static readonly MethodInfo SceneUnloaded = typeof(SceneManager).GetMethod("Internal_SceneUnloaded", BindingFlags.Static | BindingFlags.NonPublic);
    static readonly MethodInfo ActiveChanged = typeof(SceneManager).GetMethod("Internal_ActiveSceneChanged", BindingFlags.Static | BindingFlags.NonPublic);
    static object Sc(int h) => Core.MakeScene(h);
    static void Fire(MethodInfo m, params object[] args)
    {
        try { m?.Invoke(null, args); } catch (Exception e) { Log.Exception(e, m?.Name); }
#if !SHEPHERD_EBOOT
        if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: sceneLoaded / sceneUnloaded / activeSceneChanged handlers)
#endif
    }

    static (SerializedFile file, string path)? FindScene(string name)
    {
        foreach (var b in Loaded)
            foreach (var (path, cab) in b.SceneToCab)
                if (string.Equals(path, name, StringComparison.OrdinalIgnoreCase) || string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase))
                {
                    var f = Loader.Db.GetFile(cab); if (f != null) return (f, path);
                }
        return null;
    }

    public static void SetActive(int handle)
    {
        if (Scenes.ActiveHandle == handle) return; int prev = Scenes.ActiveHandle; Scenes.ActiveHandle = handle;
        Fire(ActiveChanged, Sc(prev), Sc(handle));
    }
    public static void Unload(int handle)
    {
        if (!Scenes.All.TryGetValue(handle, out var s) || handle == Scenes.DontDestroyHandle) return;
        foreach (var r in s.Roots.ToArray()) Lifecycle.DestroyNow(r);
        Scenes.All.Remove(handle);
        Fire(SceneUnloaded, Sc(handle));
        if (Scenes.ActiveHandle == handle) { var next = Scenes.All.Keys.FirstOrDefault(k => k != Scenes.DontDestroyHandle); if (next != 0) SetActive(next); }
    }

    [Icall("UnityEngine.SceneManagement.SceneManagerAPIInternal::LoadSceneAsyncNameIndexInternal_Injected")] static object LoadSceneAsync(object[] a)
    {
        var name = (string)a[0]; int buildIndex = (int)a[1]; var prm = (LoadSceneParameters)a[2]; bool additive = prm.loadSceneMode == LoadSceneMode.Additive;
        Runtime.SceneStop.OnLoadScene(name);   // kit #4: the New Game scope's fallback trigger (Runtime/SceneStop.cs)
        var found = FindScene(name ?? "");
        bool isLevel0 = !found.HasValue && (buildIndex == 0 || name == "level0");
        if (!found.HasValue && !isLevel0) Log.Warn($"LoadSceneAsync('{name}', {buildIndex}): scene not in any loaded bundle — trying the catalog");
        Log.Info($"LoadSceneAsync('{name}', {(additive ? "Additive" : "Single")}) -> {(found?.path ?? (isLevel0 ? "level0" : "catalog"))}");
        int newHandle = 0;
        return AsyncOps.Create<AsyncOperation>(poll: () => true, onComplete: () =>
        {
            var others = Scenes.All.Keys.Where(k => k != Scenes.DontDestroyHandle).ToList();
#if !SHEPHERD_NO_K8
            string k8name = found.HasValue ? System.IO.Path.GetFileNameWithoutExtension(found.Value.path) : isLevel0 ? "level0" : name;
            if (Runtime.K8Preload.On) Runtime.K8Preload.OnLoadStart(k8name);   // kit #8: the read-order map / the preload's store (data only)
#endif
            int created0 = Loader.Created;   // kit #5 integration: SHEPHERD_BLACK_GC=scene counts the objects a game scene load creates
            if (found.HasValue) newHandle = Loader.InstantiateSceneFile(found.Value.file, System.IO.Path.GetFileNameWithoutExtension(found.Value.path), found.Value.path, activate: false);
            else if (isLevel0) newHandle = Loader.LoadLevel0(activate: false);
            else newHandle = Loader.LoadScene(name, activate: false);
            Runtime.BlackFrameGc.NoteSceneLoaded(Loader.Created - created0);
            if (!additive) foreach (var h in others) Unload(h);
            if (!additive) Diag.SimStats.ScenePoint(name, 2);
            if (!additive) SetActive(newHandle);
            Loader.ActivateScene(newHandle);
#if !SHEPHERD_NO_K8
            if (Runtime.K8Preload.On) Runtime.K8Preload.OnLoadDone(k8name);
#endif
            Fire(SceneLoaded, Sc(newHandle), additive ? LoadSceneMode.Additive : LoadSceneMode.Single);
        });
    }
    [Icall("UnityEngine.SceneManagement.SceneManagerAPIInternal::UnloadSceneNameIndexInternal")] static object UnloadScene(object[] a)
    {
        var name = (string)a[0]; var s = Scenes.All.Values.FirstOrDefault(x => x.Name == name || x.Path == name || (int)a[1] >= 0 && x.BuildIndex == (int)a[1]);
        a[4] = s != null; if (s != null) Unload(s.Handle);
        return AsyncOps.Create<AsyncOperation>(poll: () => true);
    }
    [Icall("UnityEngine.SceneManagement.SceneManager::UnloadSceneAsyncInternal_Injected")] static object UnloadSceneS(object[] a) { Unload(Core.HandleOf(a[0])); return AsyncOps.Create<AsyncOperation>(poll: () => true); }
    [Icall("UnityEngine.SceneManagement.SceneManager::UnloadSceneInternal_Injected")] static object UnloadSceneNow(object[] a) { Unload(Core.HandleOf(a[0])); return true; }
    [Icall("UnityEngine.SceneManagement.SceneManagerAPIInternal::GetNumScenesInBuildSettings")] static object NumBuild(object[] a) => 1;
    [Icall("UnityEngine.SceneManagement.SceneManagerAPIInternal::GetSceneByBuildIndex_Injected")] static object ByBuild(object[] a) { a[1] = Sc(Scenes.All.Values.FirstOrDefault(s => s.BuildIndex == (int)a[0])?.Handle ?? 0); return null; }
    static IEnumerable<Scenes.Info> Visible => Scenes.All.Values.Where(s => s.Handle != Scenes.DontDestroyHandle && !(s.Handle == 1 && s.Roots.Count == 0));
    [Icall("UnityEngine.SceneManagement.SceneManager::GetActiveScene_Injected")] static object Active(object[] a) { a[0] = Sc(Scenes.ActiveHandle); return null; }
    [Icall("UnityEngine.SceneManagement.SceneManager::SetActiveScene_Injected")] static object SetActiveI(object[] a) { var h = Core.HandleOf(a[0]); if (!Scenes.All.ContainsKey(h)) return false; SetActive(h); return true; }
    [Icall("UnityEngine.SceneManagement.SceneManager::CanSetAsActiveScene_Injected")] static object CanSet(object[] a) => Scenes.All.ContainsKey(Core.HandleOf(a[0]));
    [Icall("UnityEngine.SceneManagement.SceneManager::get_sceneCount")] static object Count(object[] a) => Visible.Count();
    [Icall("UnityEngine.SceneManagement.SceneManager::get_loadedSceneCount")] static object LCount(object[] a) => Visible.Count();
    [Icall("UnityEngine.SceneManagement.SceneManager::GetSceneAt_Injected")] static object At(object[] a) { a[1] = Sc(Visible.ElementAtOrDefault((int)a[0])?.Handle ?? 0); return null; }
    [Icall("UnityEngine.SceneManagement.SceneManager::GetSceneByName_Injected")] static object ByName(object[] a) { a[1] = Sc(Scenes.All.Values.FirstOrDefault(s => s.Name == (string)a[0])?.Handle ?? 0); return null; }
    [Icall("UnityEngine.SceneManagement.SceneManager::GetSceneByPath_Injected")] static object ByPath(object[] a) { a[1] = Sc(Scenes.All.Values.FirstOrDefault(s => s.Path == (string)a[0])?.Handle ?? 0); return null; }
    [Icall("UnityEngine.SceneManagement.SceneManager::CreateScene_Injected")] static object Create(object[] a) { var s = Scenes.Create((string)a[0], (string)a[0]); a[2] = Sc(s.Handle); return null; }
    [Icall("UnityEngine.SceneManagement.SceneManager::MoveGameObjectToScene_Injected")] static object Move(object[] a)
    {
        var go = Objects.Get<NGameObject>(a[0]); int h = Core.HandleOf(a[1]); if (go == null || !Scenes.All.ContainsKey(h) || go.Transform.Parent != null) return null;
        Scenes.RemoveRoot(go); Scenes.AddRoot(go, h); Scenes.SetSceneRecursive(go, h); return null;
    }
    [Icall("UnityEngine.SceneManagement.SceneManager::MergeScenes_Injected")] static object Merge(object[] a)
    {
        int src = Core.HandleOf(a[0]), dst = Core.HandleOf(a[1]); if (!Scenes.All.TryGetValue(src, out var s)) return null;
        foreach (var r in s.Roots.ToArray()) { Scenes.AddRoot(r, dst); Scenes.SetSceneRecursive(r, dst); }
        Scenes.All.Remove(src); return null;
    }
    static Scenes.Info S(object[] a) => Scenes.All.GetValueOrDefault((int)a[0]);
    [Icall("UnityEngine.SceneManagement.Scene::IsValidInternal")] static object Valid(object[] a) => S(a) != null;
    [Icall("UnityEngine.SceneManagement.Scene::GetNameInternal")] static object Name(object[] a) => S(a)?.Name ?? "";
    [Icall("UnityEngine.SceneManagement.Scene::SetNameInternal")] static object SetName(object[] a) { var s = S(a); if (s != null) s.Name = (string)a[1]; return null; }
    [Icall("UnityEngine.SceneManagement.Scene::GetPathInternal")] static object PathI(object[] a) => S(a)?.Path ?? "";
    [Icall("UnityEngine.SceneManagement.Scene::GetGUIDInternal")] static object Guid(object[] a) => "";
    [Icall("UnityEngine.SceneManagement.Scene::GetIsLoadedInternal")] static object IsLoaded(object[] a) => S(a)?.Loaded ?? false;
    [Icall("UnityEngine.SceneManagement.Scene::GetLoadingStateInternal")] static object LoadState(object[] a) => Enum.ToObject(typeof(Scene).GetNestedType("LoadingState", BindingFlags.NonPublic | BindingFlags.Public)!, S(a) != null ? 2 : 0);
    [Icall("UnityEngine.SceneManagement.Scene::GetBuildIndexInternal")] static object BuildIdx(object[] a) => S(a)?.BuildIndex ?? -1;
    [Icall("UnityEngine.SceneManagement.Scene::GetRootCountInternal")] static object RootCount(object[] a) => S(a)?.Roots.Count ?? 0;
    [Icall("UnityEngine.SceneManagement.Scene::GetRootGameObjectsInternal")] static object Roots(object[] a)
    {
        var s = S(a); if (a[1] is IList l) { l.Clear(); if (s != null) foreach (var r in s.Roots) l.Add(r.Managed); } return null;
    }
    [Icall("UnityEngine.SceneManagement.Scene::IsSubScene")] static object Sub(object[] a) => false;
    [Icall("UnityEngine.SceneManagement.Scene::SetIsSubScene")] static object SetSub(object[] a) => null;
    [Icall("UnityEngine.SceneManagement.Scene::GetIsDirtyInternal")] static object Dirty(object[] a) => false;
    [Icall("UnityEngine.SceneManagement.Scene::GetDirtyID")] static object DirtyId(object[] a) => 0;
}
