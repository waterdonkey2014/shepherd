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
#if !SHEPHERD_NO_K13WALKC
    [Icall("UnityEngine.AsyncOperation::InternalDestroy")] static object Destroy(object[] a) { if (a.Length > 0 && a[0] is IntPtr p) AsyncOps.Destroyed(p); return null; }   // (kit #13 walkc: the finalizer's call -- SHEPHERD_K13_ASYNC)
#else
    [Icall("UnityEngine.AsyncOperation::InternalDestroy")] static object Destroy(object[] a) => null;
#endif

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
            if (Runtime.K9Gc.SceneNursery) Runtime.K9Gc.OnLoadStart(found.HasValue ? System.IO.Path.GetFileNameWithoutExtension(found.Value.path) : isLevel0 ? "level0" : name);   // kit #9 gc: the per-scene nursery
            int created0 = Loader.Created;   // kit #5 integration: SHEPHERD_BLACK_GC=scene counts the objects a game scene load creates
            if (K12IntroOn && found.HasValue && found.Value.file.Objects.Count >= K12IntroBigScene)
            {   // kit #12 walka (opt-in SHEPHERD_K12_INTRO=1): a covered-frame collection before a big instantiate -- K12IntroOn, at the end of the class
                int k12f = Runtime.Time.FrameCount, k12n = found.Value.file.Objects.Count; string k12s = System.IO.Path.GetFileNameWithoutExtension(found.Value.path);
                double k12top = Render.BlackFrame._lastTop;
                if (Render.BlackFrame._lastCover && k12top <= K12IntroMaxOnTop && Render.BlackFrame.LastFrame >= k12f - 1)
                {
                    long k12use = GC.GetTotalMemory(false);
                    Runtime.K9House.BeforeCollect();
                    var k12sw = System.Diagnostics.Stopwatch.StartNew();
                    GC.Collect();
                    k12sw.Stop();
                    Log.Info($"[k12] f{k12f} before the instantiate of '{k12s}' ({k12n} objects; {Render.BlackFrame.LastNote}): full collection {k12sw.Elapsed.TotalMilliseconds:F0} ms, GC heap in use {k12use / 1048576.0:F1} -> {GC.GetTotalMemory(false) / 1048576.0:F1} MiB");
                }
                else Log.Info($"[k12] f{k12f} before the instantiate of '{k12s}' ({k12n} objects): the frame on screen is not the loading black hold ({(Render.BlackFrame._lastCover ? $"covered, {k12top * 100:F0} % drawn on top > {K12IntroMaxOnTop * 100:F0} %" : "not covered")}) -- no collection");
            }
            if (found.HasValue) newHandle = Loader.InstantiateSceneFile(found.Value.file, System.IO.Path.GetFileNameWithoutExtension(found.Value.path), found.Value.path, activate: false);
            else if (isLevel0) newHandle = Loader.LoadLevel0(activate: false);
            else newHandle = Loader.LoadScene(name, activate: false);
            Runtime.BlackFrameGc.NoteSceneLoaded(Loader.Created - created0);
            // kit #12 fix + kit (the owner's 19:00 answer: "we should have that tested"): the covered-frame cleanup at every SCENE CHANGE the
            // route reaches (Main Menu -> BufferScene -> QuoteScreen -> the intro), not only before the intro's big instantiate -- what the old
            // scene leaves behind is collected as soon as it is gone, on the same loading black hold (the cover test below), before the new
            // scene activates. K12IntroScenes: on with SHEPHERD_K12_INTRO=1 unless SHEPHERD_K12_INTRO_SCENES=0 (= walka's big-scene arm alone).
            // Only WHEN a collection happens changes; nothing is created, destroyed or reordered (the GL record is the gate).
            bool k12sc = K12IntroScenes && !additive && others.Count > 0 && Render.BlackFrame._lastCover && Render.BlackFrame._lastTop <= K12IntroMaxOnTop && Render.BlackFrame.LastFrame >= Runtime.Time.FrameCount - 1;
            // kit #13 bootc (vita/notes/kit13-bootc.md section 2): no cleanup in the BOOT CHAIN (level0 -> BufferScene -> the first Main Menu): kit #12's
            // device ABAB -- the Main Menu at ~101.8 s with these two vs ~98.2 s without, each freeing only ~4 MiB (595 / 801 ms pauses). The menu exit
            // (Main Menu -> BufferScene: 8.5-10 MiB) and the big-scene arm stay. SHEPHERD_K13_BOOTGC=1 (or SHEPHERD_K13_BOOTC=0) = kit #12's: every scene change.
            // Opt-in, SHEPHERD_K13_GC_ONEHOLD=1: one cleanup per loading hold (a change within 60 frames of a cleanup, on the covered hold, is skipped:
            // kit #12's device BufferScene -> QuoteScreen, 9 frames after the menu exit's, freed 0.3 MiB for ~1 s).
            bool k13skip = false;
            if (k12sc && K13BootSkip && !_k13MenuSeen)
            { k12sc = false; k13skip = true; _k13Skipped++; Log.Info($"[k13] f{Runtime.Time.FrameCount} scene change to '{name}': in the boot chain (no Main Menu yet) -- no cleanup (SHEPHERD_K13_BOOTGC=1 = kit #12's)"); }
            else if (k12sc && K13OneHold && _k13LastGc >= 0 && Runtime.Time.FrameCount - _k13LastGc <= 60)
            { k12sc = false; k13skip = true; _k13Skipped++; Log.Info($"[k13] f{Runtime.Time.FrameCount} scene change to '{name}': {Runtime.Time.FrameCount - _k13LastGc} frames after the cleanup at f{_k13LastGc}, on the loading hold -- no cleanup (SHEPHERD_K13_GC_ONEHOLD=1)"); }
            if (k12sc) _k13LastGc = Runtime.Time.FrameCount;
            if (!additive && name != null && (name.EndsWith("Main Menu.unity", StringComparison.Ordinal) || name == "Main Menu")) _k13MenuSeen = true;
            long k12u0 = 0; double k12t0 = -1;
            if (k12sc)
            {
                k12u0 = GC.GetTotalMemory(false);
                if (Render.MtRender.Inner(Render.SceneRenderer.G) is Render.GlBackend k12gl0) { Render.MtRender.SyncForRead(); k12t0 = k12gl0.ResidentMiB(); }
            }
            else if (K12IntroScenes && !additive && others.Count > 0 && !k13skip)
                Log.Info($"[k12] f{Runtime.Time.FrameCount} scene change to '{name}': the frame on screen is not the loading black hold ({(Render.BlackFrame._lastCover ? $"covered, {Render.BlackFrame._lastTop * 100:F0} % drawn on top > {K12IntroMaxOnTop * 100:F0} %" : "not covered")}) -- no collection");
            if (!additive) foreach (var h in others) Unload(h);
            if (k12sc)
            {
                long k12u1 = GC.GetTotalMemory(false);
                Runtime.K9House.BeforeCollect();
                var k12sw2 = System.Diagnostics.Stopwatch.StartNew();
#if !SHEPHERD_NO_K13WALKC
                var k13n = Runtime.K13Unload.Collect();   // kit #13 walkc: a full collection; with SHEPHERD_K13_UNLOAD on, Unity's release of unreached assets inside it
#else
                GC.Collect();
#endif
                k12sw2.Stop();
                double k12t1 = -1;
                if (Render.MtRender.Inner(Render.SceneRenderer.G) is Render.GlBackend k12gl1) { Render.MtRender.SyncForRead(); k12t1 = k12gl1.ResidentMiB(); }
                Log.Info($"[k12] f{Runtime.Time.FrameCount} scene change to '{name}' ({others.Count} scene(s) unloaded; {Render.BlackFrame.LastNote}): full collection {k12sw2.Elapsed.TotalMilliseconds:F0} ms, GC heap in use {k12u0 / 1048576.0:F1} (old scene loaded) -> {k12u1 / 1048576.0:F1} (unloaded) -> {GC.GetTotalMemory(false) / 1048576.0:F1} MiB (collected); textures {k12t0:F1} -> {k12t1:F1} MiB"
#if !SHEPHERD_NO_K13WALKC
                    + k13n
#endif
                    );
            }
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

    /// <summary>Kit #12 walka (vita/notes/kit12-walka.md section 3, "the intro's load peak"): OPT-IN, a measured arm (SHEPHERD_K12_INTRO=1;
    /// SHEPHERD_K12_WALKA=0 turns it off too). The intro's scene load is one frame in which the heap climbs ~100 -> ~205 MiB through ten majors
    /// (kit11-walk.md section 1). With the switch, a scene load whose scene file holds at least <see cref="K12IntroBigScene"/> objects (the
    /// intro's 20,826; the BufferScene's and the Main Menu's stay below) runs one full collection right BEFORE its instantiate -- after the
    /// frames before it made their garbage (the game's preloads: the Follower skeleton read, the pools' reset), the previous scene's purgeable
    /// memory released first (K9House.BeforeCollect, as the black-frame collection does) -- but only when the frame on screen is the loading
    /// black hold: BlackFrame's cover with at most <see cref="K12IntroMaxOnTop"/> drawn on top (the loading icon and its text cover 16-18 %
    /// there; the same frames kit #11's wide cover rule, SHEPHERD_K11_COVER=1, identified), so the pause lands where only the loading icon
    /// is seen. The game's creation order is unchanged: nothing is created, destroyed or reordered; only WHEN a collection happens changes
    /// (kit #4's argument). Off (default) = kit #11 exactly: the one site is skipped. The arm is written into the load's completion -- no new
    /// method or type (the AOT cold lists key engine methods by token: a new one shifts the hot / cold split of every later method), and
    /// these fields come last (no compiler-generated name changes); kit12-walka.md "Review fixes". SHEPHERD_K12_INTRO_TOP=&lt;percent&gt;
    /// (TEST ONLY, the negative control of the cover test: e.g. 10 makes the intro's 16 % hold "not the loading hold" -> the "-- no
    /// collection" line, nothing collected).</summary>
    static readonly bool K12IntroOn = Environment.GetEnvironmentVariable("SHEPHERD_K12_INTRO") == "1" && Environment.GetEnvironmentVariable("SHEPHERD_K12_WALKA") != "0";
    /// <summary>Kit #12 fix + kit: the same cleanup after every non-additive scene change once the old scene is unloaded (see LoadSceneAsync).
    /// On with <see cref="K12IntroOn"/>; SHEPHERD_K12_INTRO_SCENES=0 = walka's big-scene arm alone.</summary>
    static readonly bool K12IntroScenes = K12IntroOn && Environment.GetEnvironmentVariable("SHEPHERD_K12_INTRO_SCENES") != "0";
    const int K12IntroBigScene = 20000;
    /// <summary>Kit #13 bootc: no covered-frame cleanup before the first Main Menu (see LoadSceneAsync); SHEPHERD_K13_BOOTGC=1 or SHEPHERD_K13_BOOTC=0 = kit #12's.</summary>
    static readonly bool K13BootSkip = K12IntroScenes && Environment.GetEnvironmentVariable("SHEPHERD_K13_BOOTGC") != "1" && Environment.GetEnvironmentVariable("SHEPHERD_K13_BOOTC") != "0";
    /// <summary>Kit #13 bootc (opt-in, SHEPHERD_K13_GC_ONEHOLD=1): one cleanup per loading hold.</summary>
    static readonly bool K13OneHold = K12IntroScenes && Environment.GetEnvironmentVariable("SHEPHERD_K13_GC_ONEHOLD") == "1" && Environment.GetEnvironmentVariable("SHEPHERD_K13_BOOTC") != "0";
    static bool _k13MenuSeen; static int _k13LastGc = -1, _k13Skipped;
    static readonly double K12IntroMaxOnTop = K12IntroOn && int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K12_INTRO_TOP"), out var k12p) && k12p >= 0 && k12p <= 100 ? k12p / 100.0 : 0.20;
}
