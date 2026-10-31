using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Impl;

/// <summary>CanvasGroup state lives in the props bag (as AutoProps would keep it); changes that affect interaction
/// broadcast OnCanvasGroupChanged to the subtree like Unity's native side.</summary>
public static class CanvasGroupIcalls
{
    static object Get(object o, string k, object d) => Objects.Get(o)?.Get(k) ?? d;
    static object SetAndNotify(object[] a, string k)
    {
        var n = Objects.Get(a[0]) as NComponent; if (n == null) return null;
        bool v = (bool)a[1]; bool old = n.Get(k) is bool b ? b : true;
        n.Set(k, v);
        if (old != v) NativeMessages.Broadcast(n.Go, "OnCanvasGroupChanged");
        return null;
    }
    [Icall("UnityEngine.CanvasGroup::get_interactable")] static object GI(object[] a) => Get(a[0], "interactable", true);
    [Icall("UnityEngine.CanvasGroup::set_interactable")] static object SI(object[] a) => SetAndNotify(a, "interactable");
    [Icall("UnityEngine.CanvasGroup::get_blocksRaycasts")] static object GB(object[] a) => Get(a[0], "blocksRaycasts", true);
    [Icall("UnityEngine.CanvasGroup::set_blocksRaycasts")] static object SB(object[] a) => SetAndNotify(a, "blocksRaycasts");
    [Icall("UnityEngine.CanvasGroup::get_ignoreParentGroups")] static object GP(object[] a) => Get(a[0], "ignoreParentGroups", false);
    [Icall("UnityEngine.CanvasGroup::set_ignoreParentGroups")] static object SP(object[] a)
    {
        var n = Objects.Get(a[0]) as NComponent; if (n == null) return null;
        bool v = (bool)a[1]; bool old = n.Get("ignoreParentGroups") is bool b && b; n.Set("ignoreParentGroups", v);
        if (old != v) NativeMessages.Broadcast(n.Go, "OnCanvasGroupChanged");
        return null;
    }
    [Icall("UnityEngine.CanvasGroup::get_alpha")] static object GA(object[] a) => Get(a[0], "alpha", 1f);
    // kit #9 fidelity (stand-in fix "canvasgroup", kit7-tests.md finding 17): Unity clamps the alpha a script writes to [0, 1] (the oracle:
    // -0.5 reads back 0, 1.5 reads back 1, in every case of d-ui-canvasgroup; NaN passes through either way); kit #8 stored it as given
    [Icall("UnityEngine.CanvasGroup::set_alpha")] static object SA(object[] a)
    {
        float v = Convert.ToSingle(a[1]);
        if (K9Fidelity.FixCanvasGroup) v = v < 0f ? 0f : v > 1f ? 1f : v;
        Objects.Get(a[0])?.Set("alpha", v); return null;
    }
}
