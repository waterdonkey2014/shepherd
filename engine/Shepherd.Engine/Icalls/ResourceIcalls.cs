using System.Reflection;
using Shepherd.Engine.Assets;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

static class ResourceIcalls
{
    [Icall("UnityEngine.ResourcesAPIInternal::Load")] static object Load(object[] a)
    {
        var r = Loader.ResourceLoad((string)a[0], (Type)a[1]);
        if (r == null)
        {
            if (MenusCfg.LogRepeatsOnce) MenusCfg.ResourceMiss((string)a[0], (Type)a[1]);   // kit #5 menus: once per (path, type), then counted
            else Log.Warn($"Resources.Load('{a[0]}', {((Type)a[1])?.Name}) -> null");
        }
        return r;
    }
    [Icall("UnityEngine.ResourcesAPIInternal::LoadAll")] static object LoadAll(object[] a)
    {
        var t = (Type)a[1] ?? typeof(UObject); var items = Loader.ResourceLoadAll((string)a[0], t);
        var arr = Array.CreateInstance(t, items.Length); for (int i = 0; i < items.Length; i++) arr.SetValue(items[i], i); return arr;
    }
    [Icall("UnityEngine.ResourcesAPIInternal::LoadAsyncInternal")] static object LoadAsync(object[] a)
    {
        var req = AsyncOps.Create<ResourceRequest>(poll: () => true);
        typeof(ResourceRequest).GetField("m_Path", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(req, (string)a[0]);
        typeof(ResourceRequest).GetField("m_Type", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(req, (Type)a[1]);
        return req;
    }
    [Icall("UnityEngine.ResourcesAPIInternal::FindObjectsOfTypeAll")] static object FindAll(object[] a)
    {
        var t = (Type)a[0];
        if (MenusCfg.FindAllFast && t != null) return FindAllFast(t);
        var items = Objects.All.Where(n => !n.Destroyed && n.Managed is not null && t.IsInstanceOfType(n.Managed)).Select(n => n.Managed).ToArray();
        var arr = Array.CreateInstance(t, items.Length); for (int i = 0; i < items.Length; i++) arr.SetValue(items[i], i); return arr;
    }
    /// <summary>kit #5 menus: the query above, record for record and in the same order (ById's enumeration order), as one loop. For a
    /// MonoBehaviour type (I2's DoLocalizeAll asks for every Localize when Settings closes: ~67k records, ~1.5k matches) a record whose
    /// managed object is not a MonoBehaviour cannot match, and an object of exactly that type does: only the rest takes the reflection
    /// type test. The typed array is filled by Array.Copy (the same elements, checked casts).</summary>
    static object FindAllFast(Type t)
    {
        bool mono = !t.IsInterface && typeof(MonoBehaviour).IsAssignableFrom(t);
        var hits = new List<UObject>();
        foreach (var n in Objects.ById.Values)
        {
            if (n.Destroyed) continue;
            var m = n.Managed;
            if (m is null) continue;
            if (mono) { if (m is not MonoBehaviour) continue; if (m.GetType() != t && !t.IsInstanceOfType(m)) continue; }
            else if (!t.IsInstanceOfType(m)) continue;
            hits.Add(m);
        }
        var arr = Array.CreateInstance(t, hits.Count);
        if (hits.Count > 0) Array.Copy(hits.ToArray(), arr, hits.Count);
        return arr;
    }
    [Icall("UnityEngine.ResourcesAPIInternal::FindShaderByName")] static object FindShader(object[] a) => RenderIcalls.ShaderNamed((string)a[0]).Managed;
    [Icall("UnityEngine.ResourcesAPIInternal::UnloadAsset")] static object Unload(object[] a) => null;
    [Icall("UnityEngine.Resources::UnloadUnusedAssets")] static object UnloadUnused(object[] a)
    {
        Runtime.BlackFrameGc.OnUnloadUnusedAssets();   // kit #4 M1: Unity's full collection, when the screen is covered (SHEPHERD_BLACK_GC)
        return AsyncOps.Create<AsyncOperation>(poll: () => true);
    }
    [Icall("UnityEngine.Resources::UnloadAssetImplResourceManager")] static object UnloadRM(object[] a) => null;
    [Icall("UnityEngine.Resources::GetBuiltinResource")] static object Builtin(object[] a)
    {
        // polish round 2: the built-in meshes (ForceShadowAttenuation asks for "Quad.fbx": two NREs per intro load before)
        if (a[0] is Type t && t == typeof(Mesh) && Core.BuiltinMesh(a[1] as string) is { } m) return m;
        if (MenusCfg.LogRepeatsOnce) MenusCfg.BuiltinMiss(a[0] as Type, a[1] as string);   // kit #5 menus: once per (type, name), then counted
        else Log.Warn($"GetBuiltinResource({((Type)a[0])?.Name}, '{a[1]}') -> null");
        return null;
    }
    [Icall("UnityEngine.Resources::InstanceIDToObject")] static object IdToObj(object[] a) => Objects.ById.TryGetValue((int)a[0], out var n) ? n.Managed : null;
    [Icall("UnityEngine.Resources::InstanceIDIsValid")] static object IdValid(object[] a) => Objects.ById.ContainsKey((int)a[0]);

    // RectOffset is native-backed (m_Ptr)
    // RectOffset's finalizer calls InternalDestroy on the finalizer thread while the main thread creates new ones -> must be thread-safe
    // (a plain Dictionary here intermittently corrupted itself and broke ~1 in 3 boots with IndexOutOfRange in TryInsert).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, int[]> Offsets = new(); static long _next = 0x7000000;
    static readonly FieldInfo RoPtr = typeof(RectOffset).GetField("m_Ptr", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static int[] Ro(object self) => Offsets.GetOrAdd((IntPtr)RoPtr.GetValue(self)!, _ => new int[4]);
    [Icall("UnityEngine.RectOffset::InternalCreate")] static object RoCreate(object[] a) { var p = new IntPtr(Interlocked.Increment(ref _next)); Offsets[p] = new int[4]; return p; }
    [Icall("UnityEngine.RectOffset::InternalDestroy")] static object RoDestroy(object[] a) { Offsets.TryRemove((IntPtr)a[0], out _); return null; }
    [Icall("UnityEngine.RectOffset::get_left")] static object L(object[] a) => Ro(a[0])[0];
    [Icall("UnityEngine.RectOffset::set_left")] static object SL(object[] a) { Ro(a[0])[0] = (int)a[1]; return null; }
    [Icall("UnityEngine.RectOffset::get_right")] static object R(object[] a) => Ro(a[0])[1];
    [Icall("UnityEngine.RectOffset::set_right")] static object SR(object[] a) { Ro(a[0])[1] = (int)a[1]; return null; }
    [Icall("UnityEngine.RectOffset::get_top")] static object T(object[] a) => Ro(a[0])[2];
    [Icall("UnityEngine.RectOffset::set_top")] static object ST(object[] a) { Ro(a[0])[2] = (int)a[1]; return null; }
    [Icall("UnityEngine.RectOffset::get_bottom")] static object B(object[] a) => Ro(a[0])[3];
    [Icall("UnityEngine.RectOffset::set_bottom")] static object SB(object[] a) { Ro(a[0])[3] = (int)a[1]; return null; }
    [Icall("UnityEngine.RectOffset::get_horizontal")] static object H(object[] a) { var v = Ro(a[0]); return v[0] + v[1]; }
    [Icall("UnityEngine.RectOffset::get_vertical")] static object Vt(object[] a) { var v = Ro(a[0]); return v[2] + v[3]; }

    // SystemInfo
    [Icall("UnityEngine.SystemInfo::GetDeviceName")] static object DevName(object[] a) => "Shepherd";
    [Icall("UnityEngine.SystemInfo::GetDeviceModel")] static object DevModel(object[] a) => "Mac";
    [Icall("UnityEngine.SystemInfo::GetDeviceUniqueIdentifier")] static object DevId(object[] a) => "shepherd-0000";
    [Icall("UnityEngine.SystemInfo::GetOperatingSystem")] static object Os(object[] a) => "Mac OS X 15.0";
    [Icall("UnityEngine.SystemInfo::GetOperatingSystemFamily")] static object OsFam(object[] a) => OperatingSystemFamily.MacOSX;
    [Icall("UnityEngine.SystemInfo::GetSystemMemorySize")] static object Mem(object[] a) => 16384;
    [Icall("UnityEngine.SystemInfo::GetGraphicsMemorySize")] static object GMem(object[] a) => 4096;
    [Icall("UnityEngine.SystemInfo::GetProcessorCount")] static object Cpus(object[] a) => Environment.ProcessorCount;
    [Icall("UnityEngine.SystemInfo::GetProcessorType")] static object CpuType(object[] a) => "Apple Silicon";
    [Icall("UnityEngine.SystemInfo::GetProcessorFrequencyMHz")] static object CpuMhz(object[] a) => 3000;
    [Icall("UnityEngine.SystemInfo::GetGraphicsDeviceName")] static object GName(object[] a) => "Shepherd Metal";
    [Icall("UnityEngine.SystemInfo::GetGraphicsDeviceVendor")] static object GVendor(object[] a) => "Apple";
    [Icall("UnityEngine.SystemInfo::GetGraphicsDeviceType")] static object GType(object[] a) => UnityEngine.Rendering.GraphicsDeviceType.Metal;
    [Icall("UnityEngine.SystemInfo::GetGraphicsDeviceVersion")] static object GVer(object[] a) => "Metal";
    [Icall("UnityEngine.SystemInfo::GetGraphicsShaderLevel")] static object GLevel(object[] a) => 50;
    [Icall("UnityEngine.SystemInfo::GetMaxTextureSize")] static object MaxTex(object[] a) => 16384;
    [Icall("UnityEngine.SystemInfo::SupportsTextureFormatNative")] static object TexFmt(object[] a) => true;
    [Icall("UnityEngine.SystemInfo::SupportsRenderTextureFormat")] static object RtFmt(object[] a) => true;
    [Icall("UnityEngine.SystemInfo::IsFormatSupported")] static object FmtSup(object[] a) => true;
    [Icall("UnityEngine.SystemInfo::GetDeviceType")] static object DevType(object[] a) => DeviceType.Desktop;
    [Icall("UnityEngine.SystemInfo::SupportsComputeShaders")] static object Compute(object[] a) => false;
    [Icall("UnityEngine.SystemInfo::GetGraphicsUVStartsAtTop")] static object UvTop(object[] a) => true;
    [Icall("UnityEngine.SystemInfo::HasRenderTextureNative")] static object HasRt(object[] a) => true;
    [Icall("UnityEngine.SystemInfo::GetBatteryLevel")] static object Battery(object[] a) => 1f;
    [Icall("UnityEngine.SystemInfo::GetBatteryStatus")] static object BatteryS(object[] a) => BatteryStatus.Full;
}
