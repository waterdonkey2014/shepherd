using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Impl;

static class TransformIcalls
{
    static NTransform T(object o) => Objects.Get<NTransform>(o);
    const float Deg = 57.29578f, Rad = 0.017453292f;

    [Icall("UnityEngine.Transform::get_localPosition_Injected")] static object GetLP(object[] a) { a[1] = K9Memo.Box(T(a[0])?.EffectiveLocalPosition ?? default); return null; }
    [Icall("UnityEngine.Transform::set_localPosition_Injected")] static object SetLP(object[] a) { SetLPv(a[0], (Vector3)a[1]); return null; }
    // kit #9 review fix: the three setters with a typed twin keep ONE body, shared by the hook and the twin (K9 below) -- a setter cannot run
    // twice, so check mode cannot compare them; with one body only the stub's float transport differs
    static void SetLPv(object s, Vector3 v) { var t = T(s); if (t != null) { t.EffectiveLocalPosition = v; t.MarkChanged(); } }
    static void SetLSv(object s, Vector3 v) { var t = T(s); if (t != null) { t.LocalScale = v; t.MarkChanged(); } }
    static void SetPv(object s, Vector3 v) => T(s)?.SetPosition(v);
    [Icall("UnityEngine.Transform::get_localRotation_Injected")] static object GetLR(object[] a) { a[1] = K9Memo.Box(T(a[0])?.LocalRotation ?? Quaternion.identity); return null; }
    [Icall("UnityEngine.Transform::set_localRotation_Injected")] static object SetLR(object[] a) { var t = T(a[0]); if (t != null) { t.LocalRotation = M.QNorm((Quaternion)a[1]); t.MarkChanged(); } return null; }
    [Icall("UnityEngine.Transform::get_localScale_Injected")] static object GetLS(object[] a) { a[1] = K9Memo.Box(T(a[0])?.LocalScale ?? Vector3.one); return null; }
    [Icall("UnityEngine.Transform::set_localScale_Injected")] static object SetLS(object[] a) { SetLSv(a[0], (Vector3)a[1]); return null; }
    [Icall("UnityEngine.Transform::get_position_Injected")] static object GetP(object[] a) { a[1] = K9Memo.Box(T(a[0])?.Position ?? default); return null; }
    [Icall("UnityEngine.Transform::set_position_Injected")] static object SetP(object[] a) { SetPv(a[0], (Vector3)a[1]); return null; }
    [Icall("UnityEngine.Transform::get_rotation_Injected")] static object GetR(object[] a) { a[1] = K9Memo.Box(T(a[0])?.Rotation ?? Quaternion.identity); return null; }
    [Icall("UnityEngine.Transform::set_rotation_Injected")] static object SetR(object[] a) { T(a[0])?.SetRotation(M.QNorm((Quaternion)a[1])); return null; }
    [Icall("UnityEngine.Transform::get_lossyScale_Injected")] static object GetLossy(object[] a) { var t = T(a[0]); a[1] = t != null ? t.LossyScaleBox() : Vector3.one; return null; }   // (menu-speed: one box per unchanged value)
    [Icall("UnityEngine.Transform::get_localToWorldMatrix_Injected")] static object L2W(object[] a) { a[1] = T(a[0])?.LocalToWorld ?? Matrix4x4.identity; return null; }
    [Icall("UnityEngine.Transform::get_worldToLocalMatrix_Injected")] static object W2L(object[] a) { a[1] = T(a[0])?.WorldToLocal ?? Matrix4x4.identity; return null; }
    [Icall("UnityEngine.Transform::GetPositionAndRotation")] static object GetPR(object[] a) { var t = T(a[0]); a[1] = K9Memo.Box(t?.Position ?? default); a[2] = K9Memo.Box(t?.Rotation ?? Quaternion.identity); return null; }
    [Icall("UnityEngine.Transform::GetLocalPositionAndRotation")] static object GetLPR(object[] a) { var t = T(a[0]); a[1] = K9Memo.Box(t?.EffectiveLocalPosition ?? default); a[2] = K9Memo.Box(t?.LocalRotation ?? Quaternion.identity); return null; }
    [Icall("UnityEngine.Transform::SetPositionAndRotation_Injected")] static object SetPR(object[] a) { var t = T(a[0]); if (t != null) { t.SetPosition((Vector3)a[1]); t.SetRotation((Quaternion)a[2]); } return null; }
    [Icall("UnityEngine.Transform::SetLocalPositionAndRotation_Injected")] static object SetLPR(object[] a) { var t = T(a[0]); if (t != null) { t.EffectiveLocalPosition = (Vector3)a[1]; t.LocalRotation = (Quaternion)a[2]; t.MarkChanged(); } return null; }
    [Icall("UnityEngine.Transform::GetLocalEulerAngles_Injected")] static object GetEuler(object[] a) { var e = M.ToEuler(T(a[0])?.LocalRotation ?? Quaternion.identity) * Deg; a[2] = new Vector3(Wrap(e.x), Wrap(e.y), Wrap(e.z)); return null; }
    static float Wrap(float d) { d %= 360f; return d < 0 ? d + 360f : d; }
    [Icall("UnityEngine.Transform::SetLocalEulerAngles_Injected")] static object SetEuler(object[] a) { var t = T(a[0]); if (t != null) { t.LocalRotation = M.Euler((Vector3)a[1] * Rad); t.MarkChanged(); } return null; }
    [Icall("UnityEngine.Transform::SetLocalEulerHint_Injected")] static object EulerHint(object[] a) => null;
    [Icall("UnityEngine.Transform::GetRotationOrderInternal")] static object ROrder(object[] a) => 4;
    [Icall("UnityEngine.Transform::SetRotationOrderInternal")] static object SetROrder(object[] a) => null;
    [Icall("UnityEngine.Transform::get_hasChanged")] static object HasChanged(object[] a) => T(a[0])?.HasChanged ?? false;
    [Icall("UnityEngine.Transform::set_hasChanged")] static object SetHasChanged(object[] a) { var t = T(a[0]); if (t != null) t.HasChanged = (bool)a[1]; return null; }
    [Icall("UnityEngine.Transform::GetParent")] static object Parent(object[] a) => T(a[0])?.Parent?.Managed;
    [Icall("UnityEngine.Transform::GetRoot")] static object Root(object[] a) => T(a[0])?.Root.Managed;
    [Icall("UnityEngine.Transform::get_childCount")] static object ChildCount(object[] a) => T(a[0])?.Children.Count ?? 0;
    [Icall("UnityEngine.Transform::GetChildCount")] static object ChildCount2(object[] a) => ChildCount(a);
    [Icall("UnityEngine.Transform::GetChild")] static object Child(object[] a)
    {
        var t = T(a[0]); int i = (int)a[1];
        if (t == null || i < 0 || i >= t.Children.Count) throw new UnityException("Transform child out of bounds");
        return t.Children[i].Managed;
    }
    [Icall("UnityEngine.Transform::IsChildOf")] static object IsChildOf(object[] a) { var t = T(a[0]); var p = T(a[1]); return t != null && p != null && t.IsChildOf(p); }
    [Icall("UnityEngine.Transform::GetSiblingIndex")] static object SibIdx(object[] a) { var t = T(a[0]); if (t == null) return 0; return t.Parent != null ? t.Parent.Children.IndexOf(t) : Scenes.All.GetValueOrDefault(t.Go.Scene)?.Roots.IndexOf(t.Go) ?? 0; }
    [Icall("UnityEngine.Transform::SetSiblingIndex")] static object SetSib(object[] a) { MoveSibling(T(a[0]), (int)a[1]); return null; }
    [Icall("UnityEngine.Transform::SetAsFirstSibling")] static object First(object[] a) { MoveSibling(T(a[0]), 0); return null; }
    [Icall("UnityEngine.Transform::SetAsLastSibling")] static object Last(object[] a) { MoveSibling(T(a[0]), int.MaxValue); return null; }
    static void MoveSibling(NTransform t, int index)
    {
        if (t == null) return;
        // kit #9 fidelity (stand-in fix "siblingindex", kit7-tests.md finding 4): Unity moves a child given a NEGATIVE index to the end (the
        // index is clamped as an unsigned number; the oracle's transform-model case 253: SetSiblingIndex(-2) of the first of two children made
        // it the last); kit #8 clamped it to 0. Roots keep kit #8's handling (the oracle has no scene-root evidence).
        if (t.Parent != null) { var l = t.Parent.Children; l.Remove(t); l.Insert(index < 0 && K9Fidelity.FixSiblingIndex ? l.Count : Math.Clamp(index, 0, l.Count), t); }
        else if (Scenes.All.TryGetValue(t.Go.Scene, out var s)) { s.Roots.Remove(t.Go); s.Roots.Insert(Math.Clamp(index, 0, s.Roots.Count), t.Go); }
    }
    [Icall("UnityEngine.Transform::MoveAfterSibling")] static object MoveAfter(object[] a) { var t = T(a[0]); var s = T(a[1]); if (t?.Parent != null) { var l = t.Parent.Children; l.Remove(t); int i = s == null ? 0 : l.IndexOf(s) + 1; l.Insert(Math.Clamp(i, 0, l.Count), t); } return null; }
    [Icall("UnityEngine.Transform::DetachChildren")] static object Detach(object[] a) { var t = T(a[0]); if (t != null) foreach (var c in t.Children.ToArray()) SetParent(c, null, true); return null; }

    public static void SetParent(NTransform t, NTransform p, bool worldStays)
    {
        if (t == null || t.Parent == p) return;
        if (p != null && p.IsChildOf(t)) { Log.Error($"SetParent: '{p.Name}' is a child of '{t.Name}'"); return; }
        bool before = t.Go.ActiveInHierarchy;
        Vector3 wp = default, ws = default; Quaternion wr = default;
        if (worldStays) { wp = t.Position; wr = t.Rotation; ws = t.LossyScale; }
        var oldParent = t.Parent;
        Runtime.NativeMessages.Broadcast(t.Go, "OnBeforeTransformParentChanged");
        if (t.Parent != null) t.Parent.Children.Remove(t); else Scenes.RemoveRoot(t.Go);
        t.Parent = p;
        if (p != null) { p.Children.Add(t); Scenes.SetSceneRecursive(t.Go, p.Go.Scene); } else Scenes.AddRoot(t.Go, t.Go.Scene);
        if (worldStays)
        {
            t.SetPosition(wp); t.SetRotation(wr);
            var ps = p?.LossyScale ?? Vector3.one;
            t.LocalScale = new Vector3(Div(ws.x, ps.x), Div(ws.y, ps.y), Div(ws.z, ps.z));
        }
        if (t.PhysDesc != 0) Physics.PhysWorld.Reparented(t);   // physics stage 0: the reparent hook (the counts moved in NTransform.Parent's setter)
        t.MarkChanged();
        bool after = t.Go.ActiveInHierarchy;
        if (!before && after) Lifecycle.BecameActive(t.Go); else if (before && !after) Lifecycle.BecameInactive(t.Go);
        Runtime.NativeMessages.Broadcast(t.Go, "OnTransformParentChanged");
        Runtime.NativeMessages.Broadcast(t.Go, "OnCanvasHierarchyChanged");
        if (oldParent != null && oldParent.Go.ActiveInHierarchy) Runtime.NativeMessages.Send(oldParent.Go, "OnTransformChildrenChanged");
        if (p != null && p.Go.ActiveInHierarchy) Runtime.NativeMessages.Send(p.Go, "OnTransformChildrenChanged");
    }
    static float Div(float a, float b) => MathF.Abs(b) < 1e-8f ? a : a / b;
    [Icall("UnityEngine.Transform::SetParent")] static object SetParentI(object[] a) { SetParent(T(a[0]), T(a[1]), (bool)a[2]); return null; }
    [Icall("UnityEngine.Transform::FindRelativeTransformWithPath")] static object FindRel(object[] a)
    {
        var t = T(a[0]); var path = (string)a[1]; if (t == null || path == null) return null;
        var r = Core.FindPath(t, ("x/" + path.Trim('/')).Split('/'), 1, (bool)a[2]); return r?.Managed;
    }

    [Icall("UnityEngine.Transform::TransformPoint_Injected")] static object TP(object[] a) { a[2] = K9Memo.Box(M.Point(T(a[0])?.LocalToWorld ?? Matrix4x4.identity, (Vector3)a[1])); return null; }
    [Icall("UnityEngine.Transform::InverseTransformPoint_Injected")] static object ITP(object[] a) { a[2] = K9Memo.Box(M.Point(T(a[0])?.WorldToLocal ?? Matrix4x4.identity, (Vector3)a[1])); return null; }
    [Icall("UnityEngine.Transform::TransformVector_Injected")] static object TV(object[] a) { a[2] = M.Vec(T(a[0])?.LocalToWorld ?? Matrix4x4.identity, (Vector3)a[1]); return null; }
    [Icall("UnityEngine.Transform::InverseTransformVector_Injected")] static object ITV(object[] a) { a[2] = M.Vec(T(a[0])?.WorldToLocal ?? Matrix4x4.identity, (Vector3)a[1]); return null; }
    [Icall("UnityEngine.Transform::TransformDirection_Injected")] static object TD(object[] a) { a[2] = K9Memo.Box(M.Rotate(T(a[0])?.Rotation ?? Quaternion.identity, (Vector3)a[1])); return null; }
    [Icall("UnityEngine.Transform::InverseTransformDirection_Injected")] static object ITD(object[] a) { a[2] = M.Rotate(M.QInv(T(a[0])?.Rotation ?? Quaternion.identity), (Vector3)a[1]); return null; }
    static unsafe void Batch(object[] a, Func<Vector3, Vector3> f)
    {
        var src = (Vector3*)(IntPtr)a[1]; int n = (int)a[2]; var dst = (Vector3*)(IntPtr)a[3]; int m = (int)a[4];
        for (int i = 0; i < Math.Min(n, m); i++) dst[i] = f(src[i]);
    }
    [Icall("UnityEngine.Transform::TransformPoints")] static object TPs(object[] a) { var m = T(a[0])?.LocalToWorld ?? Matrix4x4.identity; Batch(a, v => M.Point(m, v)); return null; }
    [Icall("UnityEngine.Transform::InverseTransformPoints")] static object ITPs(object[] a) { var m = T(a[0])?.WorldToLocal ?? Matrix4x4.identity; Batch(a, v => M.Point(m, v)); return null; }
    [Icall("UnityEngine.Transform::TransformVectors")] static object TVs(object[] a) { var m = T(a[0])?.LocalToWorld ?? Matrix4x4.identity; Batch(a, v => M.Vec(m, v)); return null; }
    [Icall("UnityEngine.Transform::InverseTransformVectors")] static object ITVs(object[] a) { var m = T(a[0])?.WorldToLocal ?? Matrix4x4.identity; Batch(a, v => M.Vec(m, v)); return null; }
    [Icall("UnityEngine.Transform::TransformDirections")] static object TDs(object[] a) { var q = T(a[0])?.Rotation ?? Quaternion.identity; Batch(a, v => M.Rotate(q, v)); return null; }
    [Icall("UnityEngine.Transform::InverseTransformDirections")] static object ITDs(object[] a) { var q = M.QInv(T(a[0])?.Rotation ?? Quaternion.identity); Batch(a, v => M.Rotate(q, v)); return null; }
    [Icall("UnityEngine.Transform::RotateAroundInternal_Injected")] static object RotWorld(object[] a) { var t = T(a[0]); if (t != null) t.SetRotation(M.QMul(AxisAngle((Vector3)a[1], (float)a[2]), t.Rotation)); return null; }
    [Icall("UnityEngine.Transform::RotateAroundLocal_Injected")] static object RotLocal(object[] a) { var t = T(a[0]); if (t != null) { t.LocalRotation = M.QNorm(M.QMul(t.LocalRotation, AxisAngle((Vector3)a[1], (float)a[2]))); t.MarkChanged(); } return null; }
    [Icall("UnityEngine.Transform::RotateAround_Injected")] static object RotAround(object[] a) => RotWorld(a);
    public static Quaternion AxisAngle(Vector3 axis, float rad)
    {
        var n = axis.normalized; float s = MathF.Sin(rad * .5f); return new Quaternion(n.x * s, n.y * s, n.z * s, MathF.Cos(rad * .5f));
    }
    [Icall("UnityEngine.Transform::Internal_LookAt_Injected")] static object LookAt(object[] a) { var t = T(a[0]); if (t != null) { var d = (Vector3)a[1] - t.Position; if (d.sqrMagnitude > 1e-12f) t.SetRotation(MathIcalls.LookRotation(d, (Vector3)a[2])); } return null; }
    [Icall("UnityEngine.Transform::IsNonUniformScaleTransform")] static object NonUniform(object[] a) { var s = T(a[0])?.LossyScale ?? Vector3.one; return !(Mathf.Approximately(s.x, s.y) && Mathf.Approximately(s.y, s.z)); }
    [Icall("UnityEngine.Transform::SendTransformChangedScale")] static object ScaleChanged(object[] a) => null;
    [Icall("UnityEngine.Transform::IsConstrainProportionsScale")] static object ConstrainQ(object[] a) => false;
    [Icall("UnityEngine.Transform::SetConstrainProportionsScale")] static object Constrain(object[] a) => null;
    [Icall("UnityEngine.Transform::internal_getHierarchyCapacity")] static object HCap(object[] a) => 1024;
    [Icall("UnityEngine.Transform::internal_getHierarchyCount")] static object HCount(object[] a) => Scenes.Walk(T(a[0])?.Root.Go).Count();
    [Icall("UnityEngine.Transform::internal_setHierarchyCapacity")] static object SetHCap(object[] a) => null;

    // ---------------- RectTransform ----------------
    [Icall("UnityEngine.RectTransform::get_anchorMin_Injected")] static object AMin(object[] a) { a[1] = T(a[0])?.AnchorMin ?? default; return null; }
    [Icall("UnityEngine.RectTransform::set_anchorMin_Injected")] static object SetAMin(object[] a) { var t = T(a[0]); if (t != null) { t.AnchorMin = (Vector2)a[1]; t.MarkChanged(); } return null; }
    [Icall("UnityEngine.RectTransform::get_anchorMax_Injected")] static object AMax(object[] a) { a[1] = T(a[0])?.AnchorMax ?? default; return null; }
    [Icall("UnityEngine.RectTransform::set_anchorMax_Injected")] static object SetAMax(object[] a) { var t = T(a[0]); if (t != null) { t.AnchorMax = (Vector2)a[1]; t.MarkChanged(); } return null; }
    [Icall("UnityEngine.RectTransform::get_anchoredPosition_Injected")] static object APos(object[] a) { a[1] = K9Memo.Box(T(a[0])?.AnchoredPosition ?? default); return null; }
    [Icall("UnityEngine.RectTransform::set_anchoredPosition_Injected")] static object SetAPos(object[] a) { var t = T(a[0]); if (t != null) { t.AnchoredPosition = (Vector2)a[1]; t.MarkChanged(); } return null; }
    [Icall("UnityEngine.RectTransform::get_sizeDelta_Injected")] static object SDelta(object[] a) { a[1] = K9Memo.Box(T(a[0])?.SizeDelta ?? default); return null; }
    [Icall("UnityEngine.RectTransform::set_sizeDelta_Injected")] static object SetSDelta(object[] a) { var t = T(a[0]); if (t != null) { t.SizeDelta = (Vector2)a[1]; t.MarkChanged(); } return null; }
    [Icall("UnityEngine.RectTransform::get_pivot_Injected")] static object Pivot(object[] a) { a[1] = T(a[0])?.Pivot ?? default; return null; }
    [Icall("UnityEngine.RectTransform::set_pivot_Injected")] static object SetPivot(object[] a) { var t = T(a[0]); if (t != null) { t.Pivot = (Vector2)a[1]; t.MarkChanged(); } return null; }
    [Icall("UnityEngine.RectTransform::get_rect_Injected")] static object GetRect(object[] a) { a[1] = T(a[0])?.Rect ?? default; return null; }
    [Icall("UnityEngine.RectTransform::get_drivenByObject")] static object DrivenBy(object[] a) => T(a[0])?.DrivenBy?.Managed;
    [Icall("UnityEngine.RectTransform::set_drivenByObject")] static object SetDrivenBy(object[] a) { var t = T(a[0]); if (t != null) t.DrivenBy = Objects.Get(a[1]); return null; }
    [Icall("UnityEngine.RectTransform::get_drivenProperties")] static object Driven(object[] a) => (DrivenTransformProperties)(T(a[0])?.DrivenProperties ?? 0);
    [Icall("UnityEngine.RectTransform::set_drivenProperties")] static object SetDriven(object[] a) { var t = T(a[0]); if (t != null) t.DrivenProperties = Convert.ToInt32(a[1]); return null; }
    [Icall("UnityEngine.RectTransform::ForceUpdateRectTransforms")] static object ForceUpd(object[] a) => null;

    // kit #9 track runtime (lever 10; Icalls/K9Reg.cs, Runtime/K9Typed.cs): the typed twins of the hooks above -- the same expressions, no
    // object[] / boxes; struct results / arguments as their float fields (Vector3 x y z, Quaternion x y z w, Rect x y width height).
    internal static void K9(K9Reg r)
    {
        r.Add("UnityEngine.Transform::GetParent", (Func<object, object>)(s => T(s)?.Parent?.Managed), Parent);
        r.Add("UnityEngine.Transform::get_childCount", (Func<object, int>)(s => T(s)?.Children.Count ?? 0), ChildCount);
        r.Add("UnityEngine.Transform::get_lossyScale_Injected", (Shepherd.Hooks.TypedIcalls.DV3)((object s, out float x, out float y, out float z) =>
            { var t = T(s); var v = t != null ? (Vector3)t.LossyScaleBox() : Vector3.one; x = v.x; y = v.y; z = v.z; }), GetLossy);
        r.Add("UnityEngine.Transform::get_position_Injected", (Shepherd.Hooks.TypedIcalls.DV3)((object s, out float x, out float y, out float z) =>
            { var v = T(s)?.Position ?? default; x = v.x; y = v.y; z = v.z; }), GetP);
        r.Add("UnityEngine.Transform::get_localPosition_Injected", (Shepherd.Hooks.TypedIcalls.DV3)((object s, out float x, out float y, out float z) =>
            { var v = T(s)?.EffectiveLocalPosition ?? default; x = v.x; y = v.y; z = v.z; }), GetLP);
        r.Add("UnityEngine.Transform::get_localScale_Injected", (Shepherd.Hooks.TypedIcalls.DV3)((object s, out float x, out float y, out float z) =>
            { var v = T(s)?.LocalScale ?? Vector3.one; x = v.x; y = v.y; z = v.z; }), GetLS);
        r.Add("UnityEngine.Transform::get_rotation_Injected", (Shepherd.Hooks.TypedIcalls.DV4)((object s, out float x, out float y, out float z, out float w) =>
            { var q = T(s)?.Rotation ?? Quaternion.identity; x = q.x; y = q.y; z = q.z; w = q.w; }), GetR);
        r.Add("UnityEngine.Transform::get_localRotation_Injected", (Shepherd.Hooks.TypedIcalls.DV4)((object s, out float x, out float y, out float z, out float w) =>
            { var q = T(s)?.LocalRotation ?? Quaternion.identity; x = q.x; y = q.y; z = q.z; w = q.w; }), GetLR);
        r.Add("UnityEngine.RectTransform::get_rect_Injected", (Shepherd.Hooks.TypedIcalls.DV4)((object s, out float x, out float y, out float w, out float h) =>
            { var rc = T(s)?.Rect ?? default; x = rc.x; y = rc.y; w = rc.width; h = rc.height; }), GetRect);
        r.Add("UnityEngine.Transform::set_localScale_Injected", (Shepherd.Hooks.TypedIcalls.DSetV3)((s, x, y, z) => SetLSv(s, new Vector3(x, y, z))), SetLS);
        r.Add("UnityEngine.Transform::set_localPosition_Injected", (Shepherd.Hooks.TypedIcalls.DSetV3)((s, x, y, z) => SetLPv(s, new Vector3(x, y, z))), SetLP);
        r.Add("UnityEngine.Transform::set_position_Injected", (Shepherd.Hooks.TypedIcalls.DSetV3)((s, x, y, z) => SetPv(s, new Vector3(x, y, z))), SetP);   // (one body each: SetLSv / SetLPv / SetPv)
    }
}
