using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 sections 1.4, 1.5, 3, 4): the world's seams into the engine, every one of them inert at
/// SHEPHERD_PHYS=0 (and absent at <c>none</c>).
/// <list type="bullet">
/// <item><b>Loop slots</b> (plan section 3): A <see cref="EarlyUpdate"/> before the fixed loop (Stage 1: interpolated bodies back to their true
/// pose), B <see cref="FixedStep"/> where Lifecycle called Triggers2D.Step (Stage 0: exactly that call; Stage 1: the core step,
/// write-back, then Triggers2D on post-step poses), C <see cref="Update"/> after the fixed loop (Stage 1: slot C interpolation and the
/// render-pose snapshot). Both the frame clock's loop and the wall clock's (Unity's StepFixedTime) go through them.</item>
/// <item><b>Membership hooks</b> (plan 1.4 table): activation, enable, add, destroy, reparent, layer, animated collider fields -- facades with
/// empty bodies (SHEPHERD_CHECK_PHYS=hooks counts the calls, so the wiring is visible).</item>
/// <item><b>PhysInterest / PhysDesc</b> on NTransform (plan 1.5): bit 1 = the transform's GameObject owns a Rigidbody2D, bit 2 = it has 2D
/// colliders; PhysDesc = the interested transforms in its subtree (itself included), kept through attach / detach / reparent / destroy
/// (Stage 7 walks MarkChanged only into PhysDesc > 0). The corrected dirty hook in NTransform.MarkChanged calls <see cref="Dirty"/> for an
/// interested transform other than the one physics itself is writing (<see cref="WritingTransform"/>); stage 0 has no world to flush,
/// so Dirty only feeds the sync check.</item>
/// <item><b>Checks</b> (SHEPHERD_CHECK_PHYS): <c>sync</c> compares, every fixed step, the dirty set with a full WorldStamp scan of the
/// interested transforms and counts the misses (a transform whose world pose changed without a MarkChanged reaching it: what Stage 1's
/// flush would miss); <c>tree</c> recounts PhysInterest / PhysDesc from the components every frame and counts mismatches; <c>hooks</c>
/// counts the hook calls. The exit summary prints all of it.</item>
/// </list></summary>
[ProfileOnly]   // reads no property-bag key (Assets/Diet.cs)
public static partial class PhysWorld
{
    public static readonly bool On = PhysConfig.On;
    /// <summary>The one transform physics is writing (write-back, slot A / C), for that write only: its own MarkChanged does not re-dirty it.
    /// Null in stage 0 (nothing writes).</summary>
    public static NTransform WritingTransform;

    // ---------------- loop slots ----------------
    public static long SlotA, SlotB, SlotC;
    /// <summary>Slot A: Lifecycle.Frame, before the fixed loop (Stage 1: the transform sync, then interpolated bodies back to their true pose).</summary>
    public static void EarlyUpdate() { SlotA++; if (Sim) EarlyUpdate1(); }
    /// <summary>Slot B: one fixed step's physics (Lifecycle.FixedStep, after the step's FixedUpdate loop, InFixedStep set).</summary>
    /// <summary>True while slot B runs (physics Stage 2's physcheck: a handler called from the dispatcher's phase).</summary>
    public static bool SlotBInside;
    public static void FixedStep()
    {
        SlotB++;
        if (Sim2) { SlotBInside = true; try { FixedStep1(); } finally { SlotBInside = false; } return; }
        if (Sim) { FixedStep1(); return; }   // Stage 1: the core step, write-back, then Triggers2D on post-step poses
        if (PhysConfig.CheckSync) SyncCheck();
        Triggers2D.Step();   // stage 0: the live trigger pass, exactly as Lifecycle called it before
    }
    /// <summary>Slot C: Lifecycle.Frame, after the fixed loop, before the Update phase (Stage 1: interpolation and the render-pose snapshot).</summary>
    public static void Update() { SlotC++; if (Sim) Update1(); if (PhysConfig.CheckTree) TreeCheck(); if (PhysConfig.CheckLazy) { NTransform.HcCompareAll(Registry.Transforms.Items()); NTransform.HcCheckWrite(Registry.Transforms.Items()); } }

    // ---------------- membership hooks (facades) ----------------
    public static long[] HookCalls = new long[8];
    public enum Hook { Activated, Deactivated, Enabled, Added, Destroying, Reparented, Layer, AnimatedField }
    static void Count(Hook h) { if (PhysConfig.CheckHooks) HookCalls[(int)h]++; }
    /// <summary>Lifecycle.BecameActive / BecameInactive, for a GameObject whose transform has PhysInterest (its colliders and body join or
    /// leave the world in Stage 1).</summary>
    public static void Activated(NGameObject go) { Count(Hook.Activated); if (Sim) QueueGo(go); if (Sim6) Phys3D.QueueGo(go); }
    public static void Deactivated(NGameObject go) { Count(Hook.Deactivated); if (Sim) QueueGo(go); if (Sim2) LeavingGo(go); if (Sim6) Phys3D.QueueGo(go); }
    /// <summary>Lifecycle.SetEnabled of a 2D collider (Behaviour.enabled).</summary>
    public static void Enabled(NBehaviour b, bool on)
    {
        Count(Hook.Enabled); if (Sim) Queue(b); if (Sim2 && !on && b is NCollider2D c && c.PS != null) PhysDispatch.Leaving(c);
        if (Sim4 && b is NCollider2D s && s.UsedByComposite) SubColliderChanged(s);   // physics Stage 4a: a Synchronous composite follows at once
    }
    /// <summary>A physics record joined a GameObject (load, AddComponent, Instantiate): its PhysInterest bit is set here.</summary>
    public static void Added(NComponent c)
    {
        Count(Hook.Added);
        if (Sim) Queue(c);
        if (Sim6 && c is NCollider3D k) Phys3D.Queue(k);   // physics Stage 6
        if (Sim4 && c is NCollider2D { Kind: PhysRecords.Kind2D.Composite } && TransformOf(c.Go) is { } tc) QueueSubtree(tc);   // Stage 4a: its used colliders give up their own shapes
        if (TransformOf(c.Go) is { } t) Recompute(t);   // (no transform yet: Loader.CreateGameObject recomputes once it has placed one)
    }
    /// <summary>The GameObject's transform, or null while the loader is still assembling its component list (NGameObject.Transform casts
    /// Components[0], which may not be the transform yet there).</summary>
    static NTransform TransformOf(NGameObject go)
    {
        if (go == null) return null;
        foreach (var c in go.Components) if (c is NTransform t) return t;
        return null;
    }
    /// <summary>Loader.CreateGameObject, after it put the transform first: the interest of a GameObject whose physics components joined it
    /// before its transform did.</summary>
    public static void Assembled(NGameObject go) { if (TransformOf(go) is { } t) Recompute(t); }
    /// <summary>Lifecycle.DestroyNow (before the record dies) and Objects.Kill of a physics record.</summary>
    public static void Destroying(NObj n) { Count(Hook.Destroying); if (n is NComponent c) Detached(c); if (Sim) Queue(n); if (Sim4 && n is NCollider2D { Kind: PhysRecords.Kind2D.Composite } dc && TransformOf(dc.Go) is { } dt) QueueSubtree(dt); if (Sim2 && n is NCollider2D k && k.PS != null) PhysDispatch.Leaving(k); if (Sim6 && n is NCollider3D k3) Phys3D.Destroying(k3); }
    /// <summary>Transform.SetParent (the parent link itself is kept by NTransform.Parent's setter: <see cref="ParentChanging"/>).</summary>
    public static void Reparented(NTransform t) { Count(Hook.Reparented); if (Sim) QueueSubtree(t); if (Sim6) Phys3D.Subtree(t); }
    /// <summary>GameObject.set_layer of an interested GameObject (LightingManagerVolume.Start sets layer 21 at run time).</summary>
    public static void LayerChanged(NGameObject go) { Count(Hook.Layer); if (Sim) QueueGo(go); if (Sim2) foreach (var c in go.Components) if (c is NCollider2D k && k.PS != null) PhysDispatch.Recreated(k); if (Sim6) Phys3D.QueueGo(go); }
    /// <summary>AnimatorRuntime writing a collider's or body's field (its bag key).</summary>
    public static void AnimatedField(NComponent c, string key) { Count(Hook.AnimatedField); if (Sim) FromAnimation(c, key); }

    // ---------------- PhysInterest / PhysDesc ----------------
    public static long InterestFlips, DescMoves;
    /// <summary>The interest bits of t from its GameObject's live components (records of destroyed components excluded).</summary>
    static int InterestFrom(NTransform t)
    {
        int bits = 0; var go = t.Go; if (go == null || go.Destroyed) return 0;
        foreach (var c in go.Components) if (!c.Destroyed) bits |= PhysRecords.InterestOf(c);
        return bits;
    }
    /// <summary>Re-derive t's PhysInterest from its components; keep PhysDesc up the chain when it turns on or off.</summary>
    public static void Recompute(NTransform t)
    {
        int bits = InterestFrom(t), old = t.PhysInterest;
        if (bits == old) return;
        t.PhysInterest = bits; InterestFlips++;
        if ((old == 0) != (bits == 0)) AddDesc(t, bits != 0 ? 1 : -1);
    }
    static void AddDesc(NTransform from, int d) { for (var p = from; p != null; p = p.Parent) p.PhysDesc += d; }
    /// <summary>NTransform.Parent's setter, for a transform with PhysDesc != 0, before the link changes: its subtree's count leaves the old
    /// ancestors and joins the new ones.</summary>
    public static void ParentChanging(NTransform t, NTransform oldParent, NTransform newParent)
    {
        int d = t.PhysDesc; DescMoves++;
        for (var p = oldParent; p != null; p = p.Parent) p.PhysDesc -= d;
        for (var p = newParent; p != null; p = p.Parent) p.PhysDesc += d;
    }
    /// <summary>Lifecycle.DestroyNow of a GameObject, before its transform is unlinked (DestroyTree does not go through the Parent setter):
    /// the ancestors lose the subtree's count.</summary>
    public static void SubtreeGone(NTransform root)
    {
        int d = root.PhysDesc; if (d == 0) return;
        for (var p = root.Parent; p != null; p = p.Parent) p.PhysDesc -= d;
    }
    /// <summary>A physics component left its GameObject (destroyed, removed): the transform's bits are re-derived.</summary>
    public static void Detached(NComponent c) { if (TransformOf(c.Go) is { } t && !t.Destroyed) Recompute(t); }
    /// <summary>Lifecycle.ConvertToRect replaced old by rt (same GameObject, same parent, the children moved over through the setter).</summary>
    public static void TransformReplaced(NTransform old, NTransform rt)
    {
        int bits = old.PhysInterest;
        if (bits != 0) { old.PhysInterest = 0; AddDesc(old, -1); }
        int rest = old.PhysDesc; if (rest != 0) { for (var p = old; p != null; p = p.Parent) p.PhysDesc -= rest; }   // (0 once the children moved)
        if (bits != 0) { rt.PhysInterest = bits; AddDesc(rt, 1); }
    }

    // ---------------- dirty tracking (inert in stage 0: feeds the sync check only) ----------------
    static readonly List<NTransform> _dirty = new();
    public static long DirtyCalls;
    /// <summary>NTransform.MarkChanged's hook: an interested transform changed (and it is not the one physics is writing).</summary>
    public static void Dirty(NTransform t)
    {
        DirtyCalls++;
        if ((t.PhysInterest & 4) != 0 && Sim6) Phys3D.Dirty(t);   // physics Stage 6: a 3D collider's transform (bit 4 exists only at level 6)
        if ((t.PhysInterest & 3) == 0) return;                          // a 3D-only transform (level 6); below level 6 never taken (Dirty is called for PhysInterest != 0)
        if (Sim) { if (t.PhysDirty) return; t.PhysDirty = true; _dirty.Add(t); return; }   // Stage 1: the flush's queue
        if (!PhysConfig.CheckSync || t.PhysDirty) return;
        t.PhysDirty = true; _dirty.Add(t);
    }

    // SHEPHERD_CHECK_PHYS=sync: every step, every interested transform's WorldStamp against the last step's; a change without a Dirty is a miss
    static readonly Dictionary<NTransform, long> _stamps = new();
    public static long SyncSteps, SyncChanged, SyncMisses, SyncDirtyOnly;
    static readonly Dictionary<string, int> _missNames = new();
    static void SyncCheck()
    {
        SyncSteps++;
        foreach (var o in Registry.Transforms.Items())
        {
            if (o is not NTransform t || t.Destroyed || (t.PhysInterest & 3) == 0) continue;   // (the 2D interest: 3D colliders' transforms are Phys3D's, level 6)
            long s = t.ChainStamp;
            bool had = _stamps.TryGetValue(t, out var old);
            _stamps[t] = s;
            if (!had) continue;   // new this step: Stage 1's flush builds it whole
            if (s != old)
            {
                SyncChanged++;
                if (!t.PhysDirty) { SyncMisses++; var k = t.Go?.Name ?? "?"; _missNames[k] = _missNames.GetValueOrDefault(k) + 1; }
            }
            else if (t.PhysDirty) SyncDirtyOnly++;   // MarkChanged without a real change (a write of the same bits): harmless
        }
        if (Sim) return;   // Stage 1: the flush consumes the dirty set (physics' own writes re-stamp their transforms: StampWritten)
        foreach (var t in _dirty) t.PhysDirty = false;
        _dirty.Clear();
        if (_stamps.Count > 4096 && SyncSteps % 256 == 0) { var gone = _stamps.Keys.Where(t => t.Destroyed).ToList(); foreach (var t in gone) _stamps.Remove(t); }
    }

    // SHEPHERD_CHECK_PHYS=tree: the maintained bits and counts against a recount, every frame
    public static long TreeFrames, TreeChecked, TreeBadInterest, TreeBadDesc;
    static readonly Dictionary<NTransform, int> _desc = new();
    static void TreeCheck()
    {
        TreeFrames++;
        _desc.Clear();
        foreach (var o in Registry.Transforms.Items())
        {
            if (o is not NTransform t || t.Destroyed) continue;
            TreeChecked++;
            int want = InterestFrom(t);
            if (want != t.PhysInterest) { if (TreeBadInterest++ < 8) Log.Warn($"[phys] tree check f{Runtime.Time.FrameCount}: '{t.Go?.Name}' PhysInterest {t.PhysInterest}, components say {want}"); }
            if (want != 0) for (var p = t; p != null; p = p.Parent) _desc[p] = _desc.GetValueOrDefault(p) + 1;
        }
        foreach (var o in Registry.Transforms.Items())
        {
            if (o is not NTransform t || t.Destroyed) continue;
            int want = _desc.GetValueOrDefault(t);
            if (want != t.PhysDesc && TreeBadDesc++ < 8) Log.Warn($"[phys] tree check f{Runtime.Time.FrameCount}: '{t.Go?.Name}' PhysDesc {t.PhysDesc}, recount {want}");
        }
    }

    /// <summary>The exit line(s) (Boot.Run): what stage 0 built and saw.</summary>
    public static string Summary()
    {
        if (!On) return "[phys] level none (kit #4): no typed physics records, no hooks, no loop slots";
        int interested = 0; long desc = 0;
        foreach (var o in Registry.Transforms.Items()) if (o is NTransform t && !t.Destroyed) { if (t.PhysInterest != 0) interested++; if (t.Parent == null) desc += t.PhysDesc; }
        var s = $"[phys] {(Sim ? "stage 1" : "stage 0")} (level {PhysConfig.Level}): typed records built {PhysRecords.Built} (filled from data {PhysRecords.Filled}, runtime adds {PhysRecords.RuntimeAdds}, " +
                $"clones {PhysRecords.Copies}); transforms with PhysInterest {interested} (root PhysDesc sum {desc}; interest flips {InterestFlips}, subtree moves {DescMoves}); " +
                $"loop slots A {SlotA} / B {SlotB} / C {SlotC}; dirty hook calls {DirtyCalls}";
        if (PhysConfig.CheckHooks) s += $"; hooks: {string.Join(", ", Enum.GetNames(typeof(Hook)).Select((n, i) => n + " " + HookCalls[i]))}";
        if (PhysConfig.CheckSync) s += $"; sync check: {SyncSteps} steps, {SyncChanged} interested-transform changes, {SyncMisses} MISSED by the dirty hook" +
                                       (SyncMisses > 0 ? $" (by GameObject: {string.Join(", ", _missNames.OrderByDescending(k => k.Value).Take(8).Select(k => $"'{k.Key}' {k.Value}"))})" : "") +
                                       $", {SyncDirtyOnly} dirty without a change";
        if (PhysConfig.CheckTree) s += $"; tree check: {TreeFrames} frames, {TreeChecked} transform visits, PhysInterest mismatches {TreeBadInterest}, PhysDesc mismatches {TreeBadDesc}";
        s += $"\n[hc] hasChanged (kit #9): {(NTransform.HcLazy ? "lazy" : "eager (SHEPHERD_LAZY_HASCHANGED=0: kit #8)")}; MarkChanged calls {NTransform.HcMarks}, walk visits {NTransform.HcVisits} (into physics interest only), " +
             $"parent-change rebases {NTransform.HcRebases} ({NTransform.HcRebaseVisits} visits), hasChanged reads {NTransform.HcReads}, writes {NTransform.HcWrites}" +
             (PhysConfig.CheckLazy ? $"; check lazy: {NTransform.HcCheckFrames} frames, {NTransform.HcChecked} transforms compared, {NTransform.HcMismatches} mismatches" +
                                     (NTransform.HcCheckW ? $" (lazyw: {NTransform.HcCheckWrites} seeded writes)" : "") : "");
        if (Sim) s += "\n" + Summary1();
        if (Sim2) s += "\n" + PhysDispatch.Summary() + "\n" + Summary2();
        if (Sim3) s += "\n" + Summary3();
        if (Sim4) s += "\n" + Stage4Summary();   // physics Stage 4a
        if (Sim6) s += "\n" + Phys3D.Summary();
        return s;
    }
}
