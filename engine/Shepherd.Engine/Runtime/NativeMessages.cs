using System.Reflection;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>Messages Unity's native side sends to MonoBehaviours outside the frame loop
/// (OnCanvasGroupChanged, OnTransformParentChanged, OnTransformChildrenChanged, OnCanvasHierarchyChanged).
/// uGUI depends on them: Selectable recomputes interactability from CanvasGroups only here and in OnEnable.</summary>
public static class NativeMessages
{
    static readonly Dictionary<(Type, string), MethodInfo> Cache = new();
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static MethodInfo Find(Type t, string msg)
    {
        if (Cache.TryGetValue((t, msg), out var mi)) return mi;
        for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType) { mi = c.GetMethod(msg, Any, null, Type.EmptyTypes, null); if (mi != null) break; }
        return Cache[(t, msg)] = mi;
    }
    public static void Send(NGameObject go, string msg)
    {
        var cs = Pooled<NComponent>.Copy(go.Components, out int n);   // (menu-speed: was go.Components.ToArray(); the same copy, pooled)
        try
        {
            for (int i = 0; i < n; i++)
            {
                if (cs[i] is not NMono m || m.Destroyed || !m.Awoken || m.Managed == null) continue;
                var mi = Find(m.Managed.GetType(), msg); if (mi == null) continue;
                try { mi.Invoke(m.Managed, null); } catch (Exception e) { Log.Exception(e, $"{msg} {m.Managed.GetType().Name} '{go.Name}'"); }
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: native messages)
#endif
            }
        }
        finally { Pooled<NComponent>.Back(cs, n); }
    }
    /// <summary>Send to go and every descendant that is active in the hierarchy.</summary>
    public static void Broadcast(NGameObject go, string msg)
    {
        if (go == null || !go.ActiveInHierarchy) return;
        Send(go, msg);
        var ch = Pooled<NTransform>.Copy(go.Transform.Children, out int n);   // (was Children.ToArray())
        try { for (int i = 0; i < n; i++) Broadcast(ch[i].Go, msg); }
        finally { Pooled<NTransform>.Back(ch, n); }
    }
}
