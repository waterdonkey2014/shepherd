#if !SHEPHERD_EBOOT
#define SHEPHERD_LOADPROF   // (kit #10: the loader profile's zone calls -- Assets/LoadProf.cs -- are compiled into Mac / engine-tools builds only)
#endif
using System.Reflection;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using Time = Shepherd.Engine.Runtime.Time;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Anim;

/// <summary>Per-Animator runtime: Mecanim state machine (no blending — transitions are instant) + generic curve binding.</summary>
public sealed class AnimatorRuntime
{
    public readonly NBehaviour Owner; public ControllerData Ctrl; public UObject CtrlObj;
    public readonly Dictionary<uint, object> P = new();      // parameter values by hash
    /// <summary>Weight: Unity's layer weight (the base layer is always 1; the others start at the controller's m_DefaultWeight and
    /// follow Animator.SetLayerWeight).</summary>
    public sealed class LayerState { public int State = -1; public float Time, LastLocal = -1, Weight = 1f; }
    public LayerState[] Layers = Array.Empty<LayerState>();
    public float Speed = 1; bool _init; public bool Disabled;
    /// <summary>Animator.keepAnimatorStateOnDisable as last set (Unity's default false). Stored only: this runtime keeps its state across
    /// disable / re-enable either way.</summary>
    public bool KeepStateOnDisable;
    readonly Dictionary<(uint path, int type, uint attr, Type script, int sub), Target> _targets = new();
    readonly Dictionary<UObject, List<(ClipData.Binding b, int sub, Target t)>> _clipBind = new();

    public sealed class Target
    {
        public Action<float> Set; public Func<float> Get; public Action<UObject> SetObj; public float Default, Last = float.NaN; public UObject DefaultObj; public NObj Owner; public Action Commit;
    }

    static readonly Dictionary<NObj, AnimatorRuntime> All = new();
    public static AnimatorRuntime For(object animator) { var n = Objects.Get<NBehaviour>(animator); if (n == null) return null; if (!All.TryGetValue(n, out var r)) All[n] = r = new AnimatorRuntime(n); return r; }
    AnimatorRuntime(NBehaviour owner) { Owner = owner; }

    public void Init()
    {
        if (_init) return;
        if (!Assets.LoadProf.On) { InitCore(); return; }
        Assets.LoadProf.Enter(Assets.LoadProf.AnimInit); try { InitCore(); } finally { Assets.LoadProf.Exit(Assets.LoadProf.AnimInit); }
    }
    void InitCore()
    {
        if (_init) return; _init = true;
        CtrlObj = Conv.To(Owner.Get("runtimeAnimatorController"), typeof(RuntimeAnimatorController)) as UObject;
        Ctrl = CtrlObj != null ? ControllerData.Of(CtrlObj) : null;
        if (Ctrl == null) return;
        foreach (var p in Ctrl.Params) if (!P.ContainsKey(p.Id)) P[p.Id] = p.Default;
        Layers = Ctrl.Layers.Select((l, i) => new LayerState { Weight = i == 0 ? 1f : l.Weight }).ToArray();
        for (int i = 0; i < Layers.Length; i++) { var m = Machine(i); Layers[i].State = m == null || m.States.Length == 0 ? -1 : Math.Clamp(m.Default, 0, m.States.Length - 1); }
        foreach (var clip in Ctrl.Clips.Where(c => c != null).Distinct()) Bind(clip);
    }
    /// <summary>Animator.GetLayerWeight / SetLayerWeight (polish round 1). Unity keeps the base layer at 1.</summary>
    public float GetLayerWeight(int layer) { Init(); return layer >= 0 && layer < Layers.Length ? (layer == 0 ? 1f : Layers[layer].Weight) : 0f; }
    public void SetLayerWeight(int layer, float w) { Init(); if (layer > 0 && layer < Layers.Length) Layers[layer].Weight = w; }
    public void SetController(UObject ctrl) { Owner.Set("runtimeAnimatorController", ctrl); _init = false; _targets.Clear(); _clipBind.Clear(); Init(); }
    ControllerData.Machine Machine(int layer) => Ctrl == null || layer >= Ctrl.Layers.Length ? null : Ctrl.Machines.ElementAtOrDefault(Ctrl.Layers[layer].Machine);
    public ControllerData.State Current(int layer) { var m = Machine(layer); var ls = Layers.ElementAtOrDefault(layer); return m == null || ls == null || ls.State < 0 || ls.State >= m.States.Length ? null : m.States[ls.State]; }
    public UObject ClipOf(ControllerData.State s) => s == null || s.ClipIndex < 0 || Ctrl == null || s.ClipIndex >= Ctrl.Clips.Length ? null : Ctrl.Clips[s.ClipIndex];
    public float LengthOf(ControllerData.State s) { var c = ClipOf(s); return c == null ? 1f : ClipData.Of(c)?.Length ?? 1f; }

    // ---------------- binding ----------------
    void Bind(UObject clip)
    {
        if (!Assets.LoadProf.On) { BindCore(clip); return; }
        Assets.LoadProf.Enter(Assets.LoadProf.AnimBind); try { BindCore(clip); } finally { Assets.LoadProf.Exit(Assets.LoadProf.AnimBind); }
    }
    void BindCore(UObject clip)
    {
        var cd = ClipData.Of(clip); if (cd == null) return; var list = new List<(ClipData.Binding, int, Target)>();
        var paths = PathMap();
        foreach (var b in cd.Bindings)
            for (int sub = 0; sub < b.Count; sub++)
            {
                var key = (b.Path, b.TypeId, b.Attribute, b.Script, sub);
                if (!_targets.TryGetValue(key, out var t)) _targets[key] = t = Resolve(b, sub, paths);
                if (t != null) list.Add((b, sub, t));
            }
        _clipBind[clip] = list;
        if (Traced)
            foreach (var b in cd.Bindings)
            {
                var res = list.Any(x => x.Item1 == b);
                var pname = PathMap().TryGetValue(b.Path, out var tr) ? tr.Go.Name : $"<path {b.Path:x8} not found>";
                var attr = b.TypeId == 114 && tr != null ? tr.Go.Components.OfType<NMono>().Where(c => b.Script != null && b.Script.IsInstanceOfType(c.Managed)).Select(c => NamesFor(c.Managed.GetType()).GetValueOrDefault(b.Attribute)).FirstOrDefault() : null;
                Log.Info($"[anim '{Owner.Go?.Name}'] clip '{clip.name}' bind path='{pname}' type={b.TypeId} script={b.Script?.Name} attr={attr ?? b.Attribute.ToString()} -> {(res ? "ok" : "UNRESOLVED")}");
            }
    }
    bool? _traced; bool Traced => _traced ??= Environment.GetEnvironmentVariable("SHEPHERD_TRACE_ANIM") is string tn && Owner.Go?.Name == tn;
    Dictionary<uint, NTransform> _paths;
    Dictionary<uint, NTransform> PathMap()
    {
        if (_paths != null) return _paths; _paths = new();
        void Walk(NTransform t, string path) { _paths[Crc.Of(path)] = t; foreach (var c in t.Children) Walk(c, path.Length == 0 ? c.Go.Name : path + "/" + c.Go.Name); }
        Walk(Owner.Go.Transform, ""); return _paths;
    }

    static readonly string[] RectProps = { "m_AnchoredPosition.x", "m_AnchoredPosition.y", "m_SizeDelta.x", "m_SizeDelta.y", "m_AnchorMin.x", "m_AnchorMin.y", "m_AnchorMax.x", "m_AnchorMax.y", "m_Pivot.x", "m_Pivot.y", "m_LocalPosition.z" };
    static readonly Dictionary<Type, Dictionary<uint, string>> MbNames = new();
    static Dictionary<uint, string> NamesFor(Type t)
    {
        if (MbNames.TryGetValue(t, out var d)) return d; d = new();
        void Add(string s) => d[Crc.Of(s)] = s;
        Add("m_Enabled");
        void Rec(Type ty, string prefix, int depth)
        {
            foreach (var f in SerRules.Fields(ty))
            {
                var name = prefix + f.Name; Add(name);
                var ft = f.FieldType;
                foreach (var sub in Subs(ft)) Add(name + "." + sub);
                if (depth < 3 && !ft.IsPrimitive && !ft.IsEnum && ft != typeof(string) && !typeof(UObject).IsAssignableFrom(ft) && !ft.IsArray && !(ft.IsGenericType) && SerRules.IsSerializableType(ft) && !SerRules.IsBuiltinValue(ft)) Rec(ft, name + ".", depth + 1);
            }
        }
        Rec(t, "", 0); return MbNames[t] = d;
    }
    static string[] Subs(Type t) => t == typeof(Color) ? new[] { "r", "g", "b", "a" } : t == typeof(Vector2) ? new[] { "x", "y" } : t == typeof(Vector3) ? new[] { "x", "y", "z" } : t == typeof(Vector4) || t == typeof(Quaternion) ? new[] { "x", "y", "z", "w" } : t == typeof(Rect) ? new[] { "x", "y", "width", "height" } : Array.Empty<string>();

    Target Resolve(ClipData.Binding b, int sub, Dictionary<uint, NTransform> paths)
    {
        if (!paths.TryGetValue(b.Path, out var tr)) return null;
        var go = tr.Go;
        switch (b.TypeId)
        {
            case 1:   // GameObject
                if (b.Attribute != Crc.Of("m_IsActive")) return null;
                return new Target { Owner = go, Get = () => go.ActiveSelf ? 1 : 0, Set = v => Lifecycle.SetActive(go, v > 0.5f) };
            case 4: case 224 when b.Attribute >= 1 && b.Attribute <= 4:
                return TransformTarget(tr, (int)b.Attribute, sub);
            case 224:
            {
                var name = RectProps.FirstOrDefault(p => Crc.Of(p) == b.Attribute); if (name == null) return null;
                return RectTarget(tr, name);
            }
            case 114:
            {
                var comp = go.Components.FirstOrDefault(c => c is NMono && b.Script != null && b.Script.IsInstanceOfType(c.Managed)) as NMono;
                if (comp == null) return null;
                if (!NamesFor(comp.Managed.GetType()).TryGetValue(b.Attribute, out var path)) return null;
                return MbTarget(comp, path, b.IsPPtr);
            }
            default:
            {
                var cname = Shepherd.Assets.ClassIds.Name(b.TypeId);
                var comp = go.Components.FirstOrDefault(c => c.Managed.GetType().Name == cname); if (comp == null) return null;
                var name = BuiltinName(comp, b.Attribute); if (name == null) return null;
                return comp is Physics.NRigidbody2D or Physics.NCollider2D or Physics.NCollider3D ? PhysTarget(PropTarget(comp, name, b.IsPPtr), comp, name) : PropTarget(comp, name, b.IsPPtr);
            }
        }
    }

    /// <summary>The first candidate property name of a built-in component whose CRC is the binding's attribute. Kit #10 (SHEPHERD_K10_ANIMBIND):
    /// for a component whose candidates come from its source object (the diet: Diet.SourceFieldPaths -- a pack tree decoded for every binding of
    /// every Animator) the answer is a function of (source file, path id, attribute): kept; clones of one prefab share it.</summary>
    static string BuiltinName(NComponent comp, uint attr)
    {
        if (!K10LoadTrack.AnimBind || comp.Data is Shepherd.Assets.TypeTreeObject || !comp.HasSource || comp.SourceFile == null)
            return CandidateBuiltin(comp).FirstOrDefault(n => Crc.Of(n) == attr);
        var key = new BKey(comp.SourceFile, comp.PathId, attr);
        if (_builtinNames.TryGetValue(key, out var name)) { _builtinHits++; return name; }
        name = CandidateBuiltin(comp).FirstOrDefault(n => Crc.Of(n) == attr);
        _builtinNames[key] = name;
        return name;
    }
    /// <summary>(source file, path id, attribute) as a reference-type key: no value-tuple dictionary in the AOT image.</summary>
    sealed class BKey : IEquatable<BKey>
    {
        readonly object _f; readonly long _p; readonly uint _a;
        public BKey(object f, long p, uint a) { _f = f; _p = p; _a = a; }
        public bool Equals(BKey o) => o != null && ReferenceEquals(_f, o._f) && _p == o._p && _a == o._a;
        public override bool Equals(object o) => Equals(o as BKey);
        public override int GetHashCode() => (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_f) * 31 + _p.GetHashCode()) * 31 + (int)_a;
    }
    static readonly Dictionary<BKey, string> _builtinNames = new();
    static long _builtinHits, _fieldHits;
    public static string BindSummary() => !K10LoadTrack.AnimBind ? null : $"[k10] animator binding: {_builtinNames.Count} built-in property names kept ({_builtinHits} reused), {_fields.Values.Sum(x => x.Count)} script fields kept ({_fieldHits} reused)";
    static IEnumerable<string> CandidateBuiltin(NComponent c)
    {
        yield return "m_Enabled";
        if (c.Data is Shepherd.Assets.TypeTreeObject d)
            foreach (var kv in d)
            {
                yield return kv.Key;
                if (kv.Value is Shepherd.Assets.TypeTreeObject sub) foreach (var k2 in sub.Keys) yield return kv.Key + "." + k2;
            }
        else foreach (var k in Assets.Diet.SourceFieldPaths(c)) yield return k;   // V3 diet: same names, from the type tree (no bytes read)
    }

    // pending transform channels (rotation needs all 4 components)
    sealed class TrPending { public Vector3 P, S, E; public Quaternion R; public bool DirtyP, DirtyR, DirtyS, DirtyE; }
    readonly Dictionary<NTransform, TrPending> _tp = new();
    Target TransformTarget(NTransform t, int attr, int sub)
    {
        if (!_tp.TryGetValue(t, out var p)) _tp[t] = p = new TrPending();
        var tg = new Target { Owner = t };
        switch (attr)
        {
            case 1: tg.Get = () => t.EffectiveLocalPosition[sub]; tg.Set = v => { if (!p.DirtyP) { p.P = t.EffectiveLocalPosition; p.DirtyP = true; } p.P[sub] = v; }; break;
            case 2: tg.Get = () => t.LocalRotation[sub]; tg.Set = v => { if (!p.DirtyR) { p.R = t.LocalRotation; p.DirtyR = true; } p.R[sub] = v; }; break;
            case 3: tg.Get = () => t.LocalScale[sub]; tg.Set = v => { if (!p.DirtyS) { p.S = t.LocalScale; p.DirtyS = true; } p.S[sub] = v; }; break;
            case 4: tg.Get = () => M.ToEuler(t.LocalRotation)[sub] * Mathf.Rad2Deg; tg.Set = v => { if (!p.DirtyE) { p.E = M.ToEuler(t.LocalRotation) * Mathf.Rad2Deg; p.DirtyE = true; } p.E[sub] = v; }; break;
        }
        tg.Commit = () =>
        {
            if (p.DirtyP) { t.EffectiveLocalPosition = p.P; p.DirtyP = false; }
            if (p.DirtyR) { t.LocalRotation = M.QNorm(p.R); p.DirtyR = false; }
            if (p.DirtyE) { t.LocalRotation = M.Euler(p.E * Mathf.Deg2Rad); p.DirtyE = false; }
            if (p.DirtyS) { t.LocalScale = p.S; p.DirtyS = false; }
            t.MarkChanged();
        };
        return tg;
    }
    static Target RectTarget(NTransform t, string name)
    {
        int c = name.EndsWith(".y") ? 1 : 0;
        Func<float> get; Action<float> set;
        switch (name[..name.IndexOf('.')])
        {
            case "m_AnchoredPosition": get = () => t.AnchoredPosition[c]; set = v => { var a = t.AnchoredPosition; a[c] = v; t.AnchoredPosition = a; }; break;
            case "m_SizeDelta": get = () => t.SizeDelta[c]; set = v => { var a = t.SizeDelta; a[c] = v; t.SizeDelta = a; }; break;
            case "m_AnchorMin": get = () => t.AnchorMin[c]; set = v => { var a = t.AnchorMin; a[c] = v; t.AnchorMin = a; }; break;
            case "m_AnchorMax": get = () => t.AnchorMax[c]; set = v => { var a = t.AnchorMax; a[c] = v; t.AnchorMax = a; }; break;
            case "m_Pivot": get = () => t.Pivot[c]; set = v => { var a = t.Pivot; a[c] = v; t.Pivot = a; }; break;
            default: get = () => t.LocalPosition.z; set = v => { var a = t.LocalPosition; a.z = v; t.LocalPosition = a; }; break;
        }
        return new Target { Owner = t, Get = get, Set = set, Commit = t.MarkChanged };
    }

    static readonly BindingFlags AnyI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static FieldInfo FindField(Type t, string n) { for (var c = t; c != null; c = c.BaseType) { var f = c.GetField(n, AnyI | BindingFlags.DeclaredOnly); if (f != null) return f; } return null; }
    /// <summary>Kit #10 (SHEPHERD_K10_ANIMBIND): FindField / the OnDidApplyAnimationProperties lookup are functions of the type (and name): kept.</summary>
    static readonly Dictionary<Type, Dictionary<string, FieldInfo>> _fields = new(); static readonly Dictionary<Type, MethodInfo> _commitMs = new();
    static FieldInfo FieldOf(Type t, string n)
    {
        if (!K10LoadTrack.AnimBind) return FindField(t, n);
        if (!_fields.TryGetValue(t, out var byName)) _fields[t] = byName = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        if (byName.TryGetValue(n, out var f)) { _fieldHits++; return f; }
        return byName[n] = FindField(t, n);
    }
    static MethodInfo CommitOf(Type t)
    {
        if (!K10LoadTrack.AnimBind) return t.GetMethod("OnDidApplyAnimationProperties", AnyI);
        if (_commitMs.TryGetValue(t, out var m)) return m;
        return _commitMs[t] = t.GetMethod("OnDidApplyAnimationProperties", AnyI);
    }
    static Target MbTarget(NMono mb, string path, bool pptr)
    {
        var obj = mb.Managed;
        if (path == "m_Enabled") return new Target { Owner = mb, Get = () => mb.Enabled ? 1 : 0, Set = v => Lifecycle.SetEnabled(mb, v > 0.5f) };
        var parts = path.Split('.'); var f = FieldOf(obj.GetType(), parts[0]); if (f == null) return null;
        var commit = CommitOf(obj.GetType());
        Action doCommit = commit == null ? null : () =>
        {
            try { commit.Invoke(obj, null); } catch (Exception e) { Log.Exception(e, "OnDidApplyAnimationProperties"); }
#if !SHEPHERD_EBOOT
            if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: OnDidApplyAnimationProperties)
#endif
        };
        if (pptr) return new Target { Owner = mb, SetObj = o => f.SetValue(obj, o != null && f.FieldType.IsInstanceOfType(o) ? o : null), DefaultObj = f.GetValue(obj) as UObject, Commit = doCommit };
        if (parts.Length == 1)
            return new Target { Owner = mb, Get = () => ToF(f.GetValue(obj)), Set = v => f.SetValue(obj, FromF(v, f.FieldType)), Commit = doCommit };
        var sub = parts[1];
        return new Target { Owner = mb, Get = () => SubGet(f.GetValue(obj), sub), Set = v => f.SetValue(obj, SubSet(f.GetValue(obj), sub, v)), Commit = doCommit };
    }
    /// <summary>Physics stage 0 (plan v2 1.4, the "animated collider fields" hook): an animation curve on a physics component's field still
    /// writes the property bag as before, then tells the world (an empty facade in stage 0; Stage 1 rebuilds the shape from it).</summary>
    static Target PhysTarget(Target t, NComponent c, string name)
    {
        if (t == null) return null;
        if (t.Set is { } set) t.Set = v => { set(v); Physics.PhysWorld.AnimatedField(c, name); };
        if (t.SetObj is { } so) t.SetObj = o => { so(o); Physics.PhysWorld.AnimatedField(c, name); };
        return t;
    }
    static Target PropTarget(NComponent c, string name, bool pptr)
    {
        var parts = name.Split('.'); var key = parts[0] switch { "m_Enabled" => "enabled", var k => k.StartsWith("m_") ? char.ToLowerInvariant(k[2]) + k[3..] : k };
        if (key == "enabled" && c is NBehaviour nb) return new Target { Owner = c, Get = () => nb.Enabled ? 1 : 0, Set = v => Lifecycle.SetEnabled(nb, v > 0.5f) };
        if (pptr) return new Target { Owner = c, SetObj = o => c.Set(key, o), DefaultObj = Conv.To(c.Get(key), typeof(UObject)) as UObject };
        if (parts.Length == 1)
        {
            // keep the stored type (bools stay bools); CanvasGroup interaction flags notify the subtree like the native setter
            bool notify = c.Managed is CanvasGroup && key is "interactable" or "blocksRaycasts" or "ignoreParentGroups";
            return new Target { Owner = c, Get = () => ToF(c.Get(key)), Set = v =>
            {
                var old = c.Get(key);
                object nv = old switch { bool => v > 0.5f, int => (int)MathF.Round(v), _ => notify ? v > 0.5f : v };
                if (Equals(old, nv)) return;
                c.Set(key, nv);
                if (notify) NativeMessages.Broadcast(c.Go, "OnCanvasGroupChanged");
            } };
        }
        var sub = parts[1];
        return new Target { Owner = c, Get = () => SubGet(c.Get(key), sub), Set = v => c.Set(key, SubSet(c.Get(key) ?? DefaultFor(sub), sub, v)) };
    }
    static object DefaultFor(string sub) => sub is "r" or "g" or "b" or "a" ? Color.white : Vector4.zero;
    static float ToF(object o) => o switch { null => 0, bool b => b ? 1 : 0, Enum e => Convert.ToSingle(Convert.ToInt64(e)), IConvertible c => Convert.ToSingle(c), _ => 0 };
    static object FromF(float v, Type t) => t == typeof(bool) ? v > 0.5f : t.IsEnum ? Enum.ToObject(t, (long)MathF.Round(v)) : t == typeof(int) ? (int)MathF.Round(v) : t == typeof(float) ? v : Convert.ChangeType(v, t);
    static float SubGet(object o, string s) => o switch
    {
        Color c => s switch { "r" => c.r, "g" => c.g, "b" => c.b, _ => c.a },
        Vector2 v => s == "x" ? v.x : v.y, Vector3 v => s == "x" ? v.x : s == "y" ? v.y : v.z,
        Vector4 v => s switch { "x" => v.x, "y" => v.y, "z" => v.z, _ => v.w }, Quaternion q => s switch { "x" => q.x, "y" => q.y, "z" => q.z, _ => q.w },
        Rect r => s switch { "x" => r.x, "y" => r.y, "width" => r.width, _ => r.height }, _ => 0
    };
    static object SubSet(object o, string s, float v)
    {
        switch (o)
        {
            case Color c: if (s == "r") c.r = v; else if (s == "g") c.g = v; else if (s == "b") c.b = v; else c.a = v; return c;
            case Vector2 a: if (s == "x") a.x = v; else a.y = v; return a;
            case Vector3 a: if (s == "x") a.x = v; else if (s == "y") a.y = v; else a.z = v; return a;
            case Vector4 a: if (s == "x") a.x = v; else if (s == "y") a.y = v; else if (s == "z") a.z = v; else a.w = v; return a;
            case Quaternion q: if (s == "x") q.x = v; else if (s == "y") q.y = v; else if (s == "z") q.z = v; else q.w = v; return q;
            case Rect r: if (s == "x") r.x = v; else if (s == "y") r.y = v; else if (s == "width") r.width = v; else r.height = v; return r;
            default: return o;
        }
    }

    // ---------------- state machine ----------------
    bool CondOk(ControllerData.Cond c)
    {
        P.TryGetValue(c.Param, out var v);
        float f = v switch { bool b => b ? 1 : 0, int i => i, float x => x, _ => 0 };
        return c.Mode switch { 1 => f != 0, 2 => f == 0, 3 => f > c.Threshold, 4 => f < c.Threshold, 6 => Math.Abs(f - c.Threshold) < 1e-4f, 7 => Math.Abs(f - c.Threshold) >= 1e-4f, _ => true };
    }
    bool IsTrigger(uint id) => Ctrl.Params.FirstOrDefault(p => p.Id == id)?.Type == 9;
    void Enter(int layer, int state, float normTime)
    {
        var m = Machine(layer); if (m == null) return;
        if (state < 0 || state >= m.States.Length) state = Math.Clamp(m.Default, 0, m.States.Length - 1);
        Layers[layer].State = state; Layers[layer].Time = normTime * LengthOf(m.States[state]); Layers[layer].LastLocal = -1;
        if (Traced) Log.Info($"[anim '{Owner.Go?.Name}'] layer {layer} -> state '{(Ctrl.Tos.TryGetValue(m.States[state].FullPathId, out var sn) ? sn : m.States[state].NameId.ToString())}' clip='{ClipOf(m.States[state])?.name}'");
    }
    public void Play(int hash, int layer, float normTime)
    {
        Init(); if (Ctrl == null) return; uint h = unchecked((uint)hash);
        for (int l = 0; l < Layers.Length; l++)
        {
            if (layer >= 0 && l != layer) continue;
            var m = Machine(l); if (m == null) continue;
            int idx = Array.FindIndex(m.States, s => s.NameId == h || s.FullPathId == h || s.PathId == h);
            if (idx < 0) continue;
            bool same = Layers[l].State == idx;
            Enter(l, idx, float.IsNegativeInfinity(normTime) ? (same ? Layers[l].Time / LengthOf(m.States[idx]) : 0) : normTime);
            return;
        }
        Log.Warn($"Animator.Play: state {(Ctrl.Tos.TryGetValue(h, out var nm) ? nm : h.ToString())} not found on '{Owner.Go?.Name}'");
    }

    // V6 stage 2: the per-call collections, reused (cleared before and after; a nested Update on the same runtime, should one
    // happen, gets fresh ones as before). A cleared HashSet enumerates in insertion order like a new one.
    Dictionary<Target, float> _values; Dictionary<Target, UObject> _objs; HashSet<Action> _commits; bool _inUpdate;
    public void Update(float dt)
    {
        Init(); if (Ctrl == null || Layers.Length == 0) return;
        bool pooled = !_inUpdate; _inUpdate = true;
        var values = pooled ? _values ??= new() : new Dictionary<Target, float>(); var objs = pooled ? _objs ??= new() : new Dictionary<Target, UObject>();
        var commits = pooled ? _commits ??= new() : new HashSet<Action>();
        try { UpdateCore(dt, values, objs, commits); }
        finally { if (pooled) { _inUpdate = false; values.Clear(); objs.Clear(); commits.Clear(); } }
    }
    void UpdateCore(float dt, Dictionary<Target, float> values, Dictionary<Target, UObject> objs, HashSet<Action> commits)
    {
        values.Clear(); objs.Clear(); commits.Clear(); bool writeDefaults = false;
        for (int l = 0; l < Layers.Length; l++)
        {
            var m = Machine(l); var ls = Layers[l]; if (m == null || ls.State < 0) continue;
            var st = m.States[ls.State]; float len = LengthOf(st);
            ls.Time += dt * st.Speed * Speed;
            float norm = ls.Time / len;
            // transitions: any-state first, then the current state's (was foreach over m.AnyState.Concat(st.Transitions): both arrays
            // as they were here, the first match ends the scan)
            var anyS = m.AnyState; var own = st.Transitions;
            for (int pass = 0; pass < 2; pass++)
            {
                var arr = pass == 0 ? anyS : own; bool entered = false;
                for (int k = 0; k < arr.Length; k++)
                {
                    var t = arr[k];
                    if (m.AnyState.Contains(t) && t.Dest == ls.State && !t.CanToSelf) continue;
                    if (t.HasExit && norm < t.ExitTime) continue;
                    if (!t.HasExit && t.Conds.Length == 0) continue;
                    bool all = true; foreach (var c in t.Conds) if (!CondOk(c)) { all = false; break; }   // was t.Conds.All(CondOk)
                    if (!all) continue;
                    foreach (var c in t.Conds) if (IsTrigger(c.Param)) P[c.Param] = false;
                    Enter(l, t.Dest, t.Offset); st = m.States[ls.State]; len = LengthOf(st); entered = true; break;
                }
                if (entered) break;
            }
            writeDefaults |= st.WriteDefaults;
            var clip = ClipOf(st); if (clip == null) continue; var cd = ClipData.Of(clip);
            bool loop = st.Loop || cd.Loop;
            float local = loop ? ls.Time % len : Math.Min(ls.Time, len); if (local < 0) local += len;
            if (!_clipBind.TryGetValue(clip, out var binds)) { Bind(clip); _clipBind.TryGetValue(clip, out binds); }
            // Unity's layer stack (override blending): a layer above the base mixes its curves over what the layers below produced
            // (or the property's default value where none did) by its weight; a weight-0 layer still runs its state machine and events
            // but leaves the values alone. (Additive layers are blended the same way: none is known in the scenes run so far.)
            float w = l == 0 ? 1f : ls.Weight;
            if (binds != null && w > 0f)
                foreach (var (b, sub, tg) in binds)
                {
                    float v = cd.Sample(b.Curve + sub, local);
                    if (tg.SetObj != null) { if (w >= 0.5f || l == 0) { int i = (int)MathF.Round(v); objs[tg] = i >= 0 && i < cd.PPtrMap.Length ? cd.PPtrMap[i] : null; } }
                    else if (w >= 1f) values[tg] = v;
                    else { float below = values.TryGetValue(tg, out var bv) ? bv : tg.Default; values[tg] = below + (v - below) * w; }
                }
            FireEvents(cd, ls.LastLocal, local, loop && local < ls.LastLocal);
            ls.LastLocal = local;
        }
        foreach (var tg in _targets.Values)
        {
            if (tg == null) continue;
            if (tg.SetObj != null) { var o = objs.TryGetValue(tg, out var oo) ? oo : writeDefaults ? tg.DefaultObj : null; if (objs.ContainsKey(tg) || writeDefaults) { tg.SetObj(o); if (tg.Commit != null) commits.Add(tg.Commit); } continue; }
            if (!tg.Default.Equals(tg.Default) || !_defaultsTaken) { }
            float v; if (values.TryGetValue(tg, out var vv)) v = vv; else if (writeDefaults) v = tg.Default; else continue;
            if (tg.Last == v) continue;
            tg.Last = v; try { tg.Set(v); } catch (Exception e) { Log.Exception(e, "animator set"); }
            if (tg.Commit != null) commits.Add(tg.Commit);
        }
        foreach (var c in commits) c();
    }
    bool _defaultsTaken;
    public void TakeDefaults()
    {
        if (_defaultsTaken) return; _defaultsTaken = true;
        foreach (var tg in _targets.Values) if (tg?.Get != null) { try { tg.Default = tg.Get(); } catch { } }
    }

    void FireEvents(ClipData cd, float from, float to, bool wrapped)
    {
        if (cd.Events.Count == 0) return;
        foreach (var e in cd.Events)
        {
            float et = e.time - cd.Start;
            bool hit = wrapped ? (et > from || et <= to) : (et > from && et <= to) || (from < 0 && et <= to && et >= 0 && to >= et);
            if (!hit || string.IsNullOrEmpty(e.fn)) continue;
            foreach (var c in Owner.Go.Components.ToArray())
            {
                if (c is not NMono m || !m.Enabled || m.Destroyed) continue;
                var mi = Invokes.FindMethod(m.Managed.GetType(), e.fn); if (mi == null) continue;
                var ps = mi.GetParameters(); object[] args = null;
                if (ps.Length == 1)
                {
                    var pt = ps[0].ParameterType;
                    args = new object[] { pt == typeof(string) ? e.s : pt == typeof(float) ? e.f : pt == typeof(int) ? e.i : pt == typeof(AnimationEvent) ? new AnimationEvent { functionName = e.fn, stringParameter = e.s, floatParameter = e.f, intParameter = e.i, objectReferenceParameter = e.o } : (object)e.o };
                }
                try { mi.Invoke(m.Managed, args); } catch (Exception ex) { Log.Exception(ex, $"AnimationEvent {e.fn}"); }
#if !SHEPHERD_EBOOT
                if (Diag.TestSpine.On) Diag.TestSpine.After();   // kit #7 tests: SHEPHERD_TEST_SPINE (review fix: AnimationEvents)
#endif
            }
        }
    }

    /// <summary>Player loop hook: update every active Animator (after Update/coroutines, before LateUpdate).</summary>
    public static void UpdateAll()
    {
        foreach (var n in Registry.Animators.Snapshot())   // stage 2: the Animator records, in Objects.All order
        {
            if (n is not NBehaviour b || b.Managed is not Animator || b.Destroyed || !b.Enabled || b.Go == null || b.Go.Scene == 0 || !b.Go.ActiveInHierarchy) continue;
            var r = For(b.Managed);
            try
            {
                r.Init(); r.TakeDefaults();
                int mode = b.Get("updateMode") is { } um ? Convert.ToInt32(um) : 0;
                r.Update(mode == 2 ? Time.UnscaledDeltaTime : Time.DeltaTime);
            }
            catch (Exception e) { Log.Exception(e, $"Animator '{b.Go.Name}'"); }
        }
    }
}
