using System.Collections;
using System.Reflection;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;

/// <summary>Object.Instantiate: clone a GameObject hierarchy (or a single asset) with Unity's semantics — new objects get
/// their ctor run, serialized fields are deep-copied, references INTO the cloned hierarchy are remapped to the clones.</summary>
public static class Cloner
{
    static bool _fieldCopyLogged;
    public static string[] TraceNames = (Environment.GetEnvironmentVariable("SHEPHERD_TRACE_INST") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    public static UObject Clone(UObject original, Transform parent, bool worldStays, Vector3? pos, Quaternion? rot)
    {
        var n = Objects.Get(original);
        if (n == null) { Log.Error("Instantiate: original is null/destroyed"); return null; }
        if (!_fieldCopyLogged) { _fieldCopyLogged = true; Log.Info(FieldCopy.StartLine()); }   // kit #5 menus: how fields are copied (once)
        var go = n as NGameObject ?? (n as NComponent)?.Go;
        if (go == null) return CloneLoose(n);

        var map = new Dictionary<NObj, NObj>();
        var newRoot = CloneTree(go, null, map);
        newRoot.Name = go.Name + "(Clone)"; foreach (var c in newRoot.Components) c.Name = newRoot.Name;
        if (TraceNames.Any(t => go.Name.Contains(t)))
            Log.Info($"[instantiate] '{go.Name}' active={go.ActiveSelf} <- {string.Join(" < ", new System.Diagnostics.StackTrace(2).GetFrames().Take(6).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name))}");
        // copy serialized MonoBehaviour fields + property bags now that every clone exists (references can be remapped)
        foreach (var (src, dst) in map) CopyState(src, dst, map);

        var t = newRoot.Transform; var p = Objects.Get<NTransform>(parent);
        Scenes.RemoveRoot(newRoot);
        bool wasActive = newRoot.ActiveSelf; newRoot.ActiveSelf = false;     // suppress callbacks until placed
        // Unity: the whole clone joins the parent's scene, or (no parent) the active scene. CloneTree copied each source object's
        // Scene, which is 0 for a prefab asset's children, so without the recursive set an unparented prefab instance had only its
        // root in the scene (polish round 1: its children were skipped by every scene-filtered pass -- rect-change messages,
        // animators, world draws).
        if (p != null) { t.Parent = p; p.Children.Add(t); Scenes.SetSceneRecursive(newRoot, p.Go.Scene); } else { Scenes.AddRoot(newRoot); Scenes.SetSceneRecursive(newRoot, newRoot.Scene); }
        if (pos.HasValue) t.SetPosition(pos.Value);
        if (rot.HasValue) t.SetRotation(rot.Value);
        if (p != null && worldStays && !pos.HasValue)
        {
            var src = go.Transform; t.SetPosition(src.Position); t.SetRotation(src.Rotation);
        }
        t.MarkChanged();
        newRoot.ActiveSelf = wasActive;
        Render.SceneRenderer.LayoutNewRootCanvases(newRoot);   // k4-display: Unity lays a new root canvas out before its scripts wake up
        if (newRoot.ActiveInHierarchy) Lifecycle.BecameActive(newRoot);
        return map.TryGetValue(n, out var res) ? res.Managed : newRoot.Managed;
    }

    static NGameObject CloneTree(NGameObject src, NTransform newParent, Dictionary<NObj, NObj> map)
    {
        var mgo = (GameObject)Objects.NewManaged(typeof(GameObject));
        var go = Objects.Bind(mgo, new NGameObject { Name = src.Name, ActiveSelf = src.ActiveSelf, Layer = src.Layer, Tag = src.Tag, IsStatic = src.IsStatic, Scene = src.Scene });
        map[src] = go;
        foreach (var c in src.Components)
        {
            var managed = Objects.NewManaged(c.Managed.GetType());
            var rec = Objects.Bind(managed, Lifecycle.NewComponentRecord(c.Managed.GetType()));
            rec.Go = go; rec.Name = go.Name; rec.CopySource(c);
            go.Components.Add(rec); map[c] = rec;
            if (rec is Physics.NRigidbody2D or Physics.NCollider2D or Physics.NCollider3D) Physics.PhysWorld.Added(rec);   // physics stage 0: the add hook
            if (rec is NTransform nt && c is NTransform st)
            {
                nt.IsRect = st.IsRect; nt.LocalPosition = st.LocalPosition; nt.LocalRotation = st.LocalRotation; nt.LocalScale = st.LocalScale;
                nt.AnchorMin = st.AnchorMin; nt.AnchorMax = st.AnchorMax; nt.AnchoredPosition = st.AnchoredPosition; nt.SizeDelta = st.SizeDelta; nt.Pivot = st.Pivot;
                nt.Parent = newParent; newParent?.Children.Add(nt);
            }
            if (rec is NBehaviour b && c is NBehaviour sb) b.Enabled = sb.Enabled;
            if (rec is NMono) Objects.RunCtor(managed);
        }
        foreach (var ch in src.Transform.Children) CloneTree(ch.Go, go.Transform, map);
        return go;
    }

    static void CopyState(NObj src, NObj dst, Dictionary<NObj, NObj> map)
    {
        if (src.Props != null) foreach (var kv in PropBag.Entries(src.Props)) dst.Set(kv.Key, Remap(kv.Value, map));
        if (src is Physics.NRigidbody2D or Physics.NCollider2D or Physics.NCollider3D) Physics.PhysRecords.Copy(src, dst);   // physics stage 0 (plan 1.4): the typed state
        if (src is NMono)
        {
            var plan = PlanOf(src.Managed.GetType()); var fs = plan.Fields;
            if (plan.Fast != null) { CopyFieldsFast(plan, src.Managed, dst.Managed, map, 0, true); if (FieldCopy.Check) CheckCopy(plan, src.Managed, dst.Managed, map); return; }   // kit #5 menus
            for (int i = 0; i < fs.Length; i++)
            {
                var f = fs[i];
                try { var v = f.GetValue(src.Managed); f.SetValue(dst.Managed, plan.Deep[i] ? DeepCopy(v, f.FieldType, map, 0) : v); }
                catch (Exception e) { Log.Warn($"clone field {f.DeclaringType.Name}.{f.Name}: {e.Message}"); }
            }
        }
    }

    /// <summary>kit3 (sub-menus): what DeepCopy decides per serialized field, decided once per type. Opening the Settings screen clones
    /// 17,787 objects with 117,450 serialized fields (+29,185 nested): DeepCopy ran for every one of them, and asked the runtime type of
    /// each value whether it is a built-in value every time. A field whose DECLARED type is a built-in value (primitive, enum, string,
    /// decimal, Unity math struct -- all sealed, so the value's runtime type is the declared one) is copied as it is, which is exactly
    /// what DeepCopy returned for it. Deep = the field still goes through DeepCopy. Pure = a struct (or field list) with no Deep field:
    /// DeepCopy of such a boxed struct rewrote every field of the box with its own value, i.e. returned the box unchanged. Ctor = the
    /// parameterless constructor DeepCopy looked up for every class instance it copied.</summary>
    sealed class Plan { public FieldInfo[] Fields; public bool[] Deep; public bool Pure; public ConstructorInfo Ctor; public bool CtorLooked; public byte[] Fast; public int[] Off, Size; }
    static readonly Dictionary<Type, Plan> Plans = new();
    static Plan PlanOf(Type t)
    {
        if (Plans.TryGetValue(t, out var p)) return p;
        var fs = SerRules.Fields(t); var deep = new bool[fs.Length]; bool pure = true;
        for (int i = 0; i < fs.Length; i++) { deep[i] = !SerRules.IsBuiltinValue(fs[i].FieldType); pure &= !deep[i]; }
        p = new Plan { Fields = fs, Deep = deep, Pure = pure };
        if (FieldCopy.On && !t.IsValueType) FastPlan(p);
        return Plans[t] = p;
    }
    /// <summary>kit #5 menus (Runtime/FieldCopy.cs): how each field is copied by offset: 1 = its bytes (a built-in value), 2 = the
    /// reference as it is (a string), 3 = the reference through DeepCopy, 0 = GetValue / SetValue (a value-type field that needs DeepCopy,
    /// or an offset the runtime would not give).</summary>
    static void FastPlan(Plan p)
    {
        var fs = p.Fields; var k = new byte[fs.Length]; var off = new int[fs.Length]; var size = new int[fs.Length];
        for (int i = 0; i < fs.Length; i++)
        {
            var ft = fs[i].FieldType; off[i] = FieldCopy.Offset(fs[i]);
            if (off[i] < 0) continue;
            if (ft.IsValueType) { if (!p.Deep[i] && (size[i] = FieldCopy.ValueSize(ft)) > 0) k[i] = 1; }
            else k[i] = p.Deep[i] ? (byte)3 : (byte)2;
        }
        p.Fast = k; p.Off = off; p.Size = size;
    }
    /// <summary>kit #5 menus: the serialized-field loop of CopyState (top = true: each field's exception caught and logged as there) and of
    /// DeepCopy's class copy (top = false: an exception goes up, as there), by field offset. deepDepth = the depth DeepCopy is called with.</summary>
    static void CopyFieldsFast(Plan plan, object s, object d, Dictionary<NObj, NObj> map, int deepDepth, bool top)
    {
        var fs = plan.Fields;
        for (int i = 0; i < fs.Length; i++)
        {
            if (!top) { CopyOne(plan, i, s, d, map, deepDepth); continue; }
            try { CopyOne(plan, i, s, d, map, deepDepth); }
            catch (Exception e) { var f = fs[i]; Log.Warn($"clone field {f.DeclaringType.Name}.{f.Name}: {e.Message}"); }
        }
    }
    static void CopyOne(Plan plan, int i, object s, object d, Dictionary<NObj, NObj> map, int deepDepth)
    {
        int o = plan.Off[i];
        switch (plan.Fast[i])
        {
            case 1: FieldCopy.CopyBytes(s, d, o, plan.Size[i]); return;
            case 2: FieldCopy.SetRef(d, o, FieldCopy.GetRef(s, o)); return;
            case 3:
            {
                var f = plan.Fields[i]; var v = FieldCopy.GetRef(s, o); var c = DeepCopy(v, f.FieldType, map, deepDepth);
                if (c == null || ReferenceEquals(c, v) || c.GetType() == v.GetType()) FieldCopy.SetRef(d, o, c);   // (the source value's type fits the field)
                else f.SetValue(d, c);   // another type: SetValue checks it, as before
                return;
            }
            default: { var f = plan.Fields[i]; var v = f.GetValue(s); f.SetValue(d, plan.Deep[i] ? DeepCopy(v, f.FieldType, map, deepDepth) : v); return; }
        }
    }
    /// <summary>SHEPHERD_CLONE_CHECK=1: every field the fast copy wrote, read back through FieldInfo and compared (FieldCopy.Summary).</summary>
    static void CheckCopy(Plan plan, object s, object d, Dictionary<NObj, NObj> map)
    {
        var fs = plan.Fields;
        for (int i = 0; i < fs.Length; i++)
        {
            if (plan.Fast[i] == 0) continue;
            object expect = null;
            if (plan.Fast[i] == 3 && fs[i].GetValue(s) is UObject u) expect = Remap(u, map);
            FieldCopy.CheckField(fs[i], s, d, plan.Fast[i] == 3, expect);
        }
    }

    static object Remap(object v, Dictionary<NObj, NObj> map)
    {
        if (v is UObject u) { var n = Objects.Get(u); return n != null && map.TryGetValue(n, out var c) ? c.Managed : v; }
        return v;
    }

    public static object DeepCopy(object v, Type declared, Dictionary<NObj, NObj> map, int depth)
    {
        if (v == null || depth > 12) return v;
        var t = v.GetType();
        if (v is UObject) return Remap(v, map);
        if (SerRules.IsBuiltinValue(t)) return v;
        if (t.IsArray)
        {
            var src = (Array)v; var et = t.GetElementType(); var arr = Array.CreateInstance(et, src.Length);
            if (SerRules.IsBuiltinValue(et)) Array.Copy(src, arr, src.Length);   // kit3: what the element loop did for built-in values (DeepCopy returned each as it was)
            else for (int i = 0; i < src.Length; i++) arr.SetValue(DeepCopy(src.GetValue(i), et, map, depth + 1), i);
            return arr;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var src = (IList)v; var dst = (IList)Activator.CreateInstance(t); var et = t.GetGenericArguments()[0];
            if (SerRules.IsBuiltinValue(et)) foreach (var e in src) dst.Add(e);   // kit3: the same Adds, without a DeepCopy call that returned e
            else foreach (var e in src) dst.Add(DeepCopy(e, et, map, depth + 1));
            return dst;
        }
        if (t == typeof(AnimationCurve) || t == typeof(Gradient)) return v;   // shared is fine for now (immutable in practice)
        if (!t.IsSerializable) return v;
        var plan = PlanOf(t);
        if (t.IsValueType && plan.Pure) return v;   // kit3: rewriting every built-in field of the box with its own value left it as it was
        object copy;
        try
        {
            if (t.IsValueType) copy = v;
            else
            {
                if (!plan.CtorLooked) { plan.Ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes); plan.CtorLooked = true; }
                copy = plan.Ctor is { } c ? c.Invoke(null) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            }
        }
        catch { return v; }
        if (plan.Fast != null) { CopyFieldsFast(plan, v, copy, map, depth + 1, false); return copy; }   // kit #5 menus (a class instance)
        var fs = plan.Fields;
        for (int i = 0; i < fs.Length; i++) { var f = fs[i]; var x = f.GetValue(v); f.SetValue(copy, plan.Deep[i] ? DeepCopy(x, f.FieldType, map, depth + 1) : x); }
        return copy;
    }

    static UObject CloneLoose(NObj n)
    {
        var t = n.Managed.GetType(); var managed = Objects.NewManaged(t);
        var rec = Objects.Bind(managed, new NObj { Name = n.Name + "(Clone)" }); rec.CopySource(n);
        if (n.Props != null) rec.ReplaceProps(PropBag.Copy(n.Props));
        if (managed is ScriptableObject)
        {
            Objects.RunCtor(managed);
            foreach (var f in SerRules.Fields(t)) f.SetValue(managed, DeepCopy(f.GetValue(n.Managed), f.FieldType, new(), 0));
            Lifecycle.ScriptableAwakeEnable(rec);
        }
        return managed;
    }
}
