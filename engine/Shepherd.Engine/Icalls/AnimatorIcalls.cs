using System.Collections;
using System.Reflection;
using Shepherd.Engine.Anim;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

static class AnimatorIcalls
{
    static AnimatorRuntime R(object a) { var r = AnimatorRuntime.For(a); r?.Init(); return r; }
    static uint H(string s) => Crc.Of(s);
    static void SetP(object a, uint id, object v) { var r = R(a); if (r != null) r.P[id] = v; }
    static T GetP<T>(object a, uint id) { var r = R(a); return r != null && r.P.TryGetValue(id, out var v) && v is T t ? t : default; }

    [Icall("UnityEngine.Animator::StringToHash")] static object Hash(object[] a) => unchecked((int)Crc.Of((string)a[0]));
    [Icall("UnityEngine.Animator::Play")] static object Play(object[] a) { R(a[0])?.Play((int)a[1], (int)a[2], (float)a[3]); return null; }
    [Icall("UnityEngine.Animator::PlayInFixedTime")] static object PlayFixed(object[] a) { R(a[0])?.Play((int)a[1], (int)a[2], 0f); return null; }
    [Icall("UnityEngine.Animator::CrossFade")] static object CrossFade(object[] a) { R(a[0])?.Play((int)a[1], (int)a[3], float.IsNegativeInfinity((float)a[4]) ? 0 : (float)a[4]); return null; }
    [Icall("UnityEngine.Animator::CrossFadeInFixedTime")] static object CrossFadeFixed(object[] a) { R(a[0])?.Play((int)a[1], (int)a[3], 0f); return null; }
    [Icall("UnityEngine.Animator::SetTriggerString")] static object TrigS(object[] a) { SetP(a[0], H((string)a[1]), true); return null; }
    [Icall("UnityEngine.Animator::SetTriggerID")] static object TrigI(object[] a) { SetP(a[0], unchecked((uint)(int)a[1]), true); return null; }
    [Icall("UnityEngine.Animator::ResetTriggerString")] static object RTrigS(object[] a) { SetP(a[0], H((string)a[1]), false); return null; }
    [Icall("UnityEngine.Animator::ResetTriggerID")] static object RTrigI(object[] a) { SetP(a[0], unchecked((uint)(int)a[1]), false); return null; }
    [Icall("UnityEngine.Animator::SetBoolString")] static object SetBS(object[] a) { SetP(a[0], H((string)a[1]), (bool)a[2]); return null; }
    [Icall("UnityEngine.Animator::SetBoolID")] static object SetBI(object[] a) { SetP(a[0], unchecked((uint)(int)a[1]), (bool)a[2]); return null; }
    [Icall("UnityEngine.Animator::GetBoolString")] static object GetBS(object[] a) => GetP<bool>(a[0], H((string)a[1]));
    [Icall("UnityEngine.Animator::GetBoolID")] static object GetBI(object[] a) => GetP<bool>(a[0], unchecked((uint)(int)a[1]));
    [Icall("UnityEngine.Animator::SetFloatString")] static object SetFS(object[] a) { SetP(a[0], H((string)a[1]), (float)a[2]); return null; }
    [Icall("UnityEngine.Animator::SetFloatID")] static object SetFI(object[] a) { SetP(a[0], unchecked((uint)(int)a[1]), (float)a[2]); return null; }
    [Icall("UnityEngine.Animator::SetFloatStringDamp")] static object SetFSD(object[] a) { SetP(a[0], H((string)a[1]), (float)a[2]); return null; }
    [Icall("UnityEngine.Animator::SetFloatIDDamp")] static object SetFID(object[] a) { SetP(a[0], unchecked((uint)(int)a[1]), (float)a[2]); return null; }
    [Icall("UnityEngine.Animator::GetFloatString")] static object GetFS(object[] a) => GetP<float>(a[0], H((string)a[1]));
    [Icall("UnityEngine.Animator::GetFloatID")] static object GetFI(object[] a) => GetP<float>(a[0], unchecked((uint)(int)a[1]));
    [Icall("UnityEngine.Animator::SetIntegerString")] static object SetIS(object[] a) { SetP(a[0], H((string)a[1]), (int)a[2]); return null; }
    [Icall("UnityEngine.Animator::SetIntegerID")] static object SetII(object[] a) { SetP(a[0], unchecked((uint)(int)a[1]), (int)a[2]); return null; }
    [Icall("UnityEngine.Animator::GetIntegerString")] static object GetIS(object[] a) => GetP<int>(a[0], H((string)a[1]));
    [Icall("UnityEngine.Animator::GetIntegerID")] static object GetII(object[] a) => GetP<int>(a[0], unchecked((uint)(int)a[1]));
    [Icall("UnityEngine.Animator::IsParameterControlledByCurveString")] static object CtrlS(object[] a) => false;
    [Icall("UnityEngine.Animator::IsParameterControlledByCurveID")] static object CtrlI(object[] a) => false;
    [Icall("UnityEngine.Animator::IsInTransition")] static object InTrans(object[] a) => false;
    [Icall("UnityEngine.Animator::get_isInitialized")] static object Inited(object[] a) => R(a[0])?.Ctrl != null;
    [Icall("UnityEngine.Animator::get_hasBoundPlayables")] static object Bound(object[] a) => R(a[0])?.Ctrl != null;
    [Icall("UnityEngine.Animator::get_layerCount")] static object Layers(object[] a) => R(a[0])?.Layers.Length ?? 0;
    [Icall("UnityEngine.Animator::GetLayerWeight")] static object LWeight(object[] a) => R(a[0])?.GetLayerWeight((int)a[1]) ?? 0f;
    [Icall("UnityEngine.Animator::SetLayerWeight")] static object SetLWeight(object[] a) { R(a[0])?.SetLayerWeight((int)a[1], (float)a[2]); return null; }
    [Icall("UnityEngine.Animator::get_speed")] static object Speed(object[] a) => R(a[0])?.Speed ?? 1f;
    [Icall("UnityEngine.Animator::set_speed")] static object SetSpeed(object[] a) { var r = R(a[0]); if (r != null) r.Speed = (float)a[1]; return null; }
    [Icall("UnityEngine.Animator::Update")] static object Upd(object[] a) { R(a[0])?.Update((float)a[1]); return null; }
    [Icall("UnityEngine.Animator::Rebind")] static object Rebind(object[] a) => null;
    [Icall("UnityEngine.Animator::WriteDefaultValues")] static object WriteDef(object[] a) => null;
    [Icall("UnityEngine.Animator::HasState")] static object HasState(object[] a)
    {
        var r = R(a[0]); uint h = unchecked((uint)(int)a[2]); int layer = (int)a[1];
        return r?.Ctrl != null && r.Ctrl.Layers.Select((l, i) => (l, i)).Where(x => layer < 0 || x.i == layer).Any(x => r.Ctrl.Machines.ElementAtOrDefault(x.l.Machine)?.States.Any(s => s.NameId == h || s.FullPathId == h || s.PathId == h) == true);
    }
    [Icall("UnityEngine.Animator::get_runtimeAnimatorController")] static object GetCtrl(object[] a) => R(a[0])?.CtrlObj ?? Conv.To(Objects.Get(a[0])?.Get("runtimeAnimatorController"), typeof(RuntimeAnimatorController));
    [Icall("UnityEngine.Animator::set_runtimeAnimatorController")] static object SetCtrl(object[] a) { AnimatorRuntime.For(a[0])?.SetController((UObject)a[1]); return null; }
    static readonly FieldInfo[] SiF = typeof(AnimatorStateInfo).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
    static object Fld(string n) => SiF.First(f => f.Name == n);
    [Icall("UnityEngine.Animator::GetAnimatorStateInfo")] static object StateInfo(object[] a)
    {
        var r = R(a[0]); int layer = (int)a[1]; object info = default(AnimatorStateInfo);
        var st = r?.Current(layer);
        if (st != null && Convert.ToInt32(a[2]) == 0)
        {
            float len = r.LengthOf(st); var ls = r.Layers[layer];
            void S(string n, object v) => SiF.First(f => f.Name == n).SetValue(info, v);
            S("m_Name", unchecked((int)st.NameId)); S("m_Path", unchecked((int)st.PathId)); S("m_FullPath", unchecked((int)st.FullPathId));
            S("m_NormalizedTime", ls.Time / len); S("m_Length", len); S("m_Speed", st.Speed); S("m_SpeedMultiplier", 1f); S("m_Tag", unchecked((int)st.TagId)); S("m_Loop", st.Loop ? 1 : 0);
        }
        a[3] = info; return null;
    }
    [Icall("UnityEngine.Animator::GetAnimatorTransitionInfo")] static object TransInfo(object[] a) { a[2] = default(AnimatorTransitionInfo); return null; }
    [Icall("UnityEngine.Animator::GetAnimatorClipInfoCount")] static object ClipInfoCount(object[] a) { var r = R(a[0]); return (bool)a[2] && r?.ClipOf(r.Current((int)a[1])) != null ? 1 : 0; }
    [Icall("UnityEngine.Animator::GetAnimatorClipInfoInternal")] static object ClipInfos(object[] a)
    {
        var r = R(a[0]); var clip = (bool)a[2] ? r?.ClipOf(r.Current((int)a[1])) : null;
        if (a[3] is IList list) { list.Clear(); if (clip != null) { object ci = default(AnimatorClipInfo); var fs = typeof(AnimatorClipInfo).GetFields(BindingFlags.Instance | BindingFlags.NonPublic); fs.First(f => f.Name == "m_ClipInstanceID").SetValue(ci, clip.GetInstanceID()); fs.First(f => f.Name == "m_Weight").SetValue(ci, 1f); list.Add(ci); } }
        return null;
    }
    [Icall("UnityEngine.Animator::GetCurrentAnimatorClipInfo")] static object CurClipInfo(object[] a) { var l = new List<AnimatorClipInfo>(); ClipInfos(new object[] { a[0], a[1], true, l }); return l.ToArray(); }
    [Icall("UnityEngine.Animator::GetNextAnimatorClipInfo")] static object NextClipInfo(object[] a) => Array.Empty<AnimatorClipInfo>();
    [Icall("UnityEngine.Animator::GetLayerName")] static object LayerName(object[] a) { var r = R(a[0]); var l = r?.Ctrl?.Layers.ElementAtOrDefault((int)a[1]); return l != null && r.Ctrl.Tos.TryGetValue(l.Name, out var n) ? n : "Base Layer"; }
    [Icall("UnityEngine.Animator::GetLayerIndex")] static object LayerIndex(object[] a) { var r = R(a[0]); var h = Crc.Of((string)a[1]); return Array.FindIndex(r?.Ctrl?.Layers ?? Array.Empty<ControllerData.Layer>(), l => l.Name == h || r.Ctrl.Tos.GetValueOrDefault(l.Name) == (string)a[1]); }
    [Icall("UnityEngine.Animator::ResolveHash")] static object Resolve(object[] a) { var r = R(a[0]); return r?.Ctrl?.Tos.GetValueOrDefault(unchecked((uint)(int)a[1])) ?? ""; }
    [Icall("UnityEngine.Animator::get_parameterCount")] static object ParamCount(object[] a) => R(a[0])?.Ctrl?.Params.Length ?? 0;
    [Icall("UnityEngine.Animator::GetParameterInternal")] static object Param(object[] a)
    {
        var p = R(a[0])?.Ctrl?.Params.ElementAtOrDefault((int)a[1]); if (p == null) return null;
        var acp = new AnimatorControllerParameter(); typeof(AnimatorControllerParameter).GetField("m_Name", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(acp, p.Name ?? "");
        typeof(AnimatorControllerParameter).GetField("m_Type", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(acp, (AnimatorControllerParameterType)p.Type); return acp;
    }
    // kit3 (sub-menus): the game's MMTab sets it (true) on its tab animators when the Settings screen opens. The engine's animator never
    // resets its state on disable / re-enable, i.e. it already behaves as with true; the flag is stored so the getter reads back what was set
    // (Unity's default: false).
    [Icall("UnityEngine.Animator::get_keepAnimatorStateOnDisable")] static object Keep(object[] a) => AnimatorRuntime.For(a[0])?.KeepStateOnDisable ?? false;
    [Icall("UnityEngine.Animator::set_keepAnimatorStateOnDisable")] static object SetKeep(object[] a) { var r = AnimatorRuntime.For(a[0]); if (r != null) r.KeepStateOnDisable = (bool)a[1]; return null; }
    [Icall("UnityEngine.Animator::get_hasTransformHierarchy")] static object HasHier(object[] a) => true;
    [Icall("UnityEngine.Animator::get_fireEvents")] static object FireEv(object[] a) => true;
    [Icall("UnityEngine.Animator::GetBehaviour")] static object Behaviour(object[] a) => null;
    [Icall("UnityEngine.Animator::InternalGetBehaviours")] static object Behaviours(object[] a) => Array.Empty<ScriptableObject>();
    [Icall("UnityEngine.Animator::InternalGetBehavioursByKey")] static object BehavioursK(object[] a) => Array.Empty<ScriptableObject>();
    [Icall("UnityEngine.Animator::ClearInternalControllerPlayable")] static object ClearPl(object[] a) => null;
    [Icall("UnityEngine.Animator::get_isOptimizable")] static object Optim(object[] a) => false;
    [Icall("UnityEngine.Animator::get_logWarnings")] static object LogW(object[] a) => false;

    [Icall("UnityEngine.RuntimeAnimatorController::get_animationClips")] static object Clips(object[] a) => (ControllerData.Of((UObject)a[0])?.Clips ?? Array.Empty<UObject>()).OfType<AnimationClip>().ToArray();
    [Icall("UnityEngine.AnimationClip::get_length")] static object CLen(object[] a) => ClipData.Of((UObject)a[0])?.Length ?? 0f;
    [Icall("UnityEngine.AnimationClip::get_frameRate")] static object CRate(object[] a) => ClipData.Of((UObject)a[0])?.SampleRate ?? 60f;
    [Icall("UnityEngine.AnimationClip::get_startTime")] static object CStart(object[] a) => ClipData.Of((UObject)a[0])?.Start ?? 0f;
    [Icall("UnityEngine.AnimationClip::get_stopTime")] static object CStop(object[] a) => ClipData.Of((UObject)a[0])?.Stop ?? 0f;
    [Icall("UnityEngine.AnimationClip::get_legacy")] static object CLegacy(object[] a) => false;
    [Icall("UnityEngine.AnimationClip::get_empty")] static object CEmpty(object[] a) => (ClipData.Of((UObject)a[0])?.CurveCount ?? 0) == 0;
    [Icall("UnityEngine.AnimationClip::get_wrapMode")] static object CWrap(object[] a) => (ClipData.Of((UObject)a[0])?.Loop ?? false) ? WrapMode.Loop : WrapMode.Once;
    [Icall("UnityEngine.AnimationClip::get_humanMotion")] static object CHuman(object[] a) => false;
    [Icall("UnityEngine.AnimationClip::GetEventsInternal")] static object CEvents(object[] a) =>
        (ClipData.Of((UObject)a[0])?.Events ?? new()).Select(e => new AnimationEvent { time = e.time, functionName = e.fn, stringParameter = e.s, floatParameter = e.f, intParameter = e.i, objectReferenceParameter = e.o }).ToArray();
    [Icall("UnityEngine.AnimationClip::Internal_CreateAnimationClip")] static object CCreate(object[] a) { Objects.Bind((UObject)a[0], new NObj()); return null; }
}
