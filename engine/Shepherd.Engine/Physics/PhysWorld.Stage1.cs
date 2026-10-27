using System.Runtime.CompilerServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 1 (physics-plan-v2 section 4 "Stage 1", sections 1.4-1.6 and 3): the world at SHEPHERD_PHYS=1 -- the Lamb walks and
/// slides along walls. Everything here runs only at level 1 or above (<see cref="Sim"/>); at 0 and <c>none</c> nothing of it is reached.
/// <list type="bullet">
/// <item><b>Membership</b> (1.4): every Rigidbody2D and solid 2D collider of an active GameObject in a scene is in the core (shim/sp_unity.cpp):
/// a body with a mirror record in the pool, shapes on their attached body (the nearest Rigidbody2D up the hierarchy) or on the world's
/// static ground body. The stage 0 hooks queue records; <see cref="Flush"/> reconciles them (bodies first, then colliders), so the order of
/// creation follows the hooks' order (deterministic). Trigger colliders stay with Runtime/Triggers2D until Stage 2 (plan section 3,
/// SHEPHERD_PHYS = 1: "steps 1-6 without sensors"); colliders used by a composite feed the composite instead.</item>
/// <item><b>Shapes</b>: built natively from the record's parameters under the collider-to-body affine (the body transform's world 2x2 and the
/// collider's local chain below it; the ground body: the collider's world 2x3); rebuilt only when those inputs change bitwise. Composites:
/// Unity's serialized m_CompositePaths until the game calls GenerateGeometry, then this engine's own pipeline (Physics/Composite.cs, the
/// game's ClipperLib); Outlines = chain loops, Polygons = convex pieces.</item>
/// <item><b>The step</b> (section 3, slot B): flush (membership, the dirty transforms: H-SYNC teleports, shape rebuilds), the native step
/// (MovePosition targets -> velocity, H-MOVEDRAG / H-MOVEVEL, Box2D 2.3.1, history, write-back flags), write-back parent-first by
/// (depth, serial) of bodies whose pose differs from the last written one or that were teleported body-side (C6), under
/// <see cref="WritingTransform"/>; then Triggers2D on post-step poses (H-TRIGPHASE final; `collide` runs it before the step).</item>
/// <item><b>Interpolation</b> (1.5): slot A restores the true pose of Interpolate bodies (H-EARLY), slot C writes Unity's lerp at
/// alpha = (time - fixedTime) / fdt (H-INTERP), natively computed; the render-pose snapshot (1.6) is taken there.</item>
/// <item><b>Checks / outputs</b>: SHEPHERD_PHYS_TRACE (the native trace of every core call, replayable), SHEPHERD_PHYS_HASH (a pose hash every
/// 50 steps, the determinism gate), SHEPHERD_PHYS_POSELOG (+ _MATCH: per-step poses of matching bodies, the slide analysis).</item>
/// </list></summary>
public static partial class PhysWorld
{
    /// <summary>Level 1 or above and the core binds: the core simulates (Stage 1). A level-1 run without the core (the Vita eboot until Stage 7
    /// links it; a Mac without the dylib) is exactly Stage 0 -- "FAIL phys: libshepphys missing" once (ShepPhys.Bind), the Stage 1 icalls not
    /// bound (IcallRegistry asks <see cref="Sim"/>), no world (plan section 4, the bar's rule 4). The binding happens at this class's first use.</summary>
    public static readonly bool Sim = PhysConfig.Level >= 1 && HostSettings() && ShepPhys.Core != null;
    /// <summary>Kit #8 review fix (vita/notes/kit8-physics.md "Review fixes"): under the Vita host (SHEPHERD_PHYS_HOST=1) the core starts only on
    /// the project's Physics2DSettings -- the owner file SHEPHERD_PHYS2D_SETTINGS names (the host sets it; the card's strict pack has no record),
    /// read here before the core binds (Runtime/Physics2DSettings.cs LoadFileNow). Without it the world would run on Unity's defaults (every
    /// layer pair colliding, gravity -9.81, the default clamps / iterations) -- a game-visible difference -- so there is no core instead: one
    /// FAIL line and stage 0 behaviour (= level 0, kit #7's records). The host already drops to level 0 when the file is missing
    /// (vita/shepherd/phys_host.inc.c); this catches a file that is there but is not the 2022.3 record. The Mac (dylib) is unchanged.</summary>
    static bool HostSettings()
    {
        if (!PhysConfig.HostProc || Runtime.Physics2DSettings.LoadFileNow()) return true;
        Log.Error("[phys] FAIL phys: the Vita host's core needs the project's Physics2DSettings (SHEPHERD_PHYS2D_SETTINGS=" + Environment.GetEnvironmentVariable("SHEPHERD_PHYS2D_SETTINGS") +
                  ": the owner folder's data/physics2d-settings.bin, vita/shepherd/stage-owner.sh) -- not there or not the 2022.3 record: no core, stage 0 behaviour (= SHEPHERD_PHYS=0), never Unity's default settings");
        return false;
    }

    public sealed class BodyState
    {
        public NRigidbody2D Rec; public NTransform T; public int Handle, Mirror; public long Serial; public int Depth;
        public ShepPhys.UBodyDef Def; public float LastAngle; public readonly List<ShapeState> Shapes = new();
    }
    public sealed class ShapeState
    {
        public NCollider2D Rec; public BodyState Body; public int[] Handles = Array.Empty<int>(); public int Count;
        public ShepPhys.UShapeDef Key; public object PathsRef; public int GenVersion = -1; public bool Ground;
    }

    static ShepPhys _core; static int _world, _ground; static bool _up, _failed;
    static readonly uint[] _matrix = new uint[32]; static int _matrixVer = int.MinValue;
    static long _serial; static int _stepIndex;
    static readonly List<NObj> _pending = new();
    static readonly List<BodyState> _bodies = new(); static bool _orderDirty;
    static readonly HashSet<BodyState> _massDirty = new();
    static readonly List<NTransform> _flushT = new();
    static ShepPhys.USyncRec[] _syncBuf = new ShepPhys.USyncRec[64];
    static bool _inFlush;
    // Stage 1's hypothesis rules since E1 (physics-plan-v2 section 5 / T6; vita/notes/physics-ledger.md), each Unity's only: H-SYNC=A (the native
    // sync's mode 0: a game write within Unity's tolerance of the pose physics last wrote is no teleport), H-EARLY=none (slot A restores nothing:
    // FixedUpdate sees the interpolated transform), H-INTERP=lerp, H-MOVEVEL=restore, H-MOVEDRAG=drag-off-gravity-on (the step flag below),
    // H-WAKETP=wake, H-CHILDSYNC=follow, H-TRIGPHASE=final (level 1: Triggers2D after the step and write-back)
    static readonly int StepFlags = ShepPhys.UstepMoveGravOn;
    /// <summary>H-SYNC=A's tolerance since E1 (results/probe2/SyncDelta__fine.csv: a LateUpdate write 9e-5 off the pose physics wrote is no teleport,
    /// 1e-4 is): the midpoint of E1's bracket, per axis -- the native sp_u_sync's constant (shim/sp_unity.cpp, SP_SYNC_TOL) is the same; the exact
    /// value, per axis vs distance, and the angle's tolerance (kept at 1e-6 rad) are E1's open items.</summary>
    internal const float SyncTol = 9.5e-5f, SyncTolAngle = 1e-6f;
    static float _clampT, _clampR;
    /// <summary>The world's per-step clamps (patch P2), for the logs.</summary>
    internal static string ClampsDesc => $"P2 clamps {_clampT:R} u / {_clampR:R} rad per step";
    // counters for the summary
    public static long Steps1, Teleports, WrittenBack, Moves, BodiesMade, BodiesGone, ShapesMade, ShapesGone, ShapeRebuilds, SlotCWrites, SlotAWrites, Flushes;
    public static int PeakBodies, PeakShapes, LastContacts, LastTouching;
    static int _liveShapes;

    // ---------------- the world ----------------
    /// <summary>The core's world, made at the first fixed step (or the first physics icall) at level 1+. False: the library is missing or
    /// failed its handshake (logged once: "FAIL phys"); the engine then runs Stage-0 behaviour (plan section 4, the bar's rule 4).</summary>
    public static bool EnsureWorld()
    {
        if (_up) return true;
        if (_failed) return false;
        _core = ShepPhys.Core as ShepPhys;
        if (_core == null) { _failed = true; Log.Error("[phys] FAIL phys: no core (" + ShepPhys.Status + "): stage 0 behaviour"); return false; }
        bool b2malloc = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_B2") is "malloc" or "0" or "off";
        int flags = (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? ShepPhys.InitPoolGrow : 0) | (b2malloc ? ShepPhys.InitB2Malloc : 0);
        int r = _core.Init(PhysConfig.PoolMb << 20, flags);
        if (r != 0) { _failed = true; Log.Error($"[phys] FAIL phys capacity pool: sp_init {r}"); return false; }
        Physics2DSettings.EnsureLoaded();
        Physics2DSettings.CopyMatrix(_matrix); _matrixVer = Physics2DSettings.MatrixVersion;
        var v = Physics2DSettings.Values;
        var wd = new ShepPhys.UWorldDef { GravityX = v.GravityX, GravityY = v.GravityY, Flags = 1 | 4 | WorldFlags2, UseMatrix = 1 };   // sleep + warm start; TOI off; Stage 2: P1 + contact serials
        unsafe { for (int i = 0; i < 32; i++) wd.Matrix[i] = _matrix[i]; }
        if (PhysConfig.TracePath != null)
        {
            int tr = _core.TraceBegin(PhysConfig.TracePath, 0);
            Log.Info($"[phys] trace -> {PhysConfig.TracePath} ({(tr == 0 ? "open" : "FAILED " + tr)})");
        }
        _world = _core.UWorldCreate(wd);
        if (_world <= 0) { _failed = true; Log.Error($"[phys] FAIL phys: sp_uworld_create {_world}"); return false; }
        _ground = _core.UWorldGround(_world);
        // H-MAXTRANS / H-MAXROT=per-step (E1's MaxTransRot: 300 u/s moves 6 u a step unclamped, 5000 deg/s 99.9 deg a step): Physics2DSettings'
        // maxTranslationSpeed / maxRotationSpeed are per-step clamps (100 u, 360 deg), given to Box2D's integrator by patch P2 in place of its
        // compiled b2_maxTranslation 2.0 u / b2_maxRotation 90 deg
        _clampT = v.MaxTranslationSpeed; _clampR = v.MaxRotationSpeed * (MathF.PI / 180f);
        int cr = _core.WorldSetClamps(_world, _clampT, _clampR);
        if (cr != 0) Log.Error($"[phys] FAIL phys: sp_world_set_clamps {cr}");
        _up = true;
        Init3();   // Stage 3's switches (nothing below level 3)
        // the boot self-test (plan Stage 0 -> Stage 1: at boot): the run-time-decoded settings against the structural invariants and Box2D
        int f = PhysSettings.SelfTest((ok, line) => { if (!ok) Log.Error("[phys] FAIL " + line); }, false);
        Log.Info($"[phys] stage {(Sim2 ? 2 : 1)} world up (world flags 0x{wd.Flags:x}): {_core.BuildInfo}; gravity ({v.GravityX}, {v.GravityY}), iterations {v.VelocityIterations} / {v.PositionIterations}, " +
                 $"fdt {Runtime.Time.FixedDeltaTime}; settings self-test {(f == 0 ? "PASS" : f + " FAILED (see FAIL lines)")}; autoSyncTransforms {Physics2DSettings.AutoSyncTransforms}; " +
                 $"B2 {(b2malloc ? "off (malloc)" : "arena")}; {ClampsDesc}; hypotheses {Hypotheses.Describe()}");
        // every record that exists now: bodies, then colliders (registry order = Objects.All order); later changes come through the hooks
        foreach (var o in Registry.Bodies2D.Items()) if (o is NRigidbody2D b) Queue(b);
        foreach (var o in Registry.Colliders2D.Items()) if (o is NCollider2D c) Queue(c);
        return true;
    }

    /// <summary>Boot.Run's exit: the trace closed (its record count logged), the pool / arena high water.</summary>
    public static void Shutdown()
    {
        if (!_up) return;
        if (PhysConfig.TracePath != null) Log.Info($"[phys] trace closed: {_core.TraceEnd()} records");
        _hashOut?.Flush(); _poseLog?.Flush();
        if (Sim2)
        {
            RayProbe(); PhysDispatch.Flush();
#if !SHEPHERD_EBOOT
            PhysShadow.Flush();   // (the swap A/B check mode: Mac-only)
#endif
            Physics2DQueries.FlushLog(); DumpAstar();
        }
    }

    // ---------------- the queue ----------------
    static void Queue(NObj n)
    {
        switch (n)
        {
            case NRigidbody2D b when !b.PhysQueued: b.PhysQueued = true; _pending.Add(b); break;
            case NCollider2D c when !c.PhysQueued: c.PhysQueued = true; _pending.Add(c); break;
        }
    }
    static void QueueGo(NGameObject go) { if (go != null) foreach (var c in go.Components) Queue(c); }
    /// <summary>Every physics record in t's subtree (a reparent, a body made or gone).</summary>
    static void QueueSubtree(NTransform t)
    {
        if (t == null || t.Destroyed || t.PhysDesc == 0) return;
        if (t.PhysInterest != 0) QueueGo(t.Go);
        foreach (var ch in t.Children) if (ch.PhysDesc != 0) QueueSubtree(ch);
    }
    /// <summary>An animation curve wrote a collider's or body's bag key: the typed record follows (radius, size, offset, isTrigger, edge radius).</summary>
    static void FromAnimation(NComponent c, string key)
    {
        if (c is NCollider2D k)
        {
            var root = key.Split('.')[0];
            switch (root)
            {
                case "m_Radius": if (k.Get("radius") is float r) k.Radius = r; break;
                case "m_Size": if (k.Get("size") is Vector2 z) { k.SizeX = z.x; k.SizeY = z.y; } break;
                case "m_Offset": if (k.Get("offset") is Vector2 o) { k.OffsetX = o.x; k.OffsetY = o.y; } break;
                case "m_IsTrigger": if (k.Get("isTrigger") is { } t) k.IsTrigger = Convert.ToBoolean(t); break;
                case "m_EdgeRadius": if (k.Get("edgeRadius") is float e) k.EdgeRadius = e; break;
            }
        }
        Queue(c);
    }
    /// <summary>An icall changed a record (Physics/Physics2DIcalls.cs).</summary>
    internal static void Changed(NObj n) { if (Sim) Queue(n); }

    // ---------------- flush (plan 1.5: the top of every reading icall, every step, Physics2D.SyncTransforms) ----------------
    /// <summary>Applies the queued membership changes and the dirty transforms (H-SYNC teleports, shape rebuilds), then the mirror's pending
    /// teleports / velocity sets. Safe to call any time outside a step.</summary>
    public static void Flush()
    {
        if (!Sim || _inFlush || !EnsureWorld()) return;
        _inFlush = true;
        try
        {
            Flushes++;
            if (PhysConfig.CheckSync) SyncCheck();   // (level 1: at every flush, before it consumes the dirty set: a change no flush sees is a miss)
            if (Physics2DSettings.MatrixVersion != _matrixVer)
            {
                Physics2DSettings.CopyMatrix(_matrix); _matrixVer = Physics2DSettings.MatrixVersion;
                _core.SetMatrix(_world, _matrix);
            }
            // 1. the dirty transforms: their colliders are re-checked; their bodies are synced after the membership pass
            _flushT.Clear();
            for (int i = 0; i < _dirty.Count; i++)
            {
                var t = _dirty[i]; t.PhysDirty = false;
                if (t.Destroyed || t.Go == null) continue;
                _flushT.Add(t);
                foreach (var c in t.Go.Components) if (c is NCollider2D) Queue(c);
            }
            _dirty.Clear();
            // 2. membership (bodies first: a new or gone body re-queues the colliders below it)
            ProcessPending();
            // 3. the body syncs (H-SYNC)
            int n = 0;
            foreach (var t in _flushT)
            {
                NRigidbody2D b = null;
                foreach (var c in t.Go.Components) if (c is NRigidbody2D r && r.PB != null && ReferenceEquals(r.PB.T, t)) { b = r; break; }
                if (b == null) continue;
                var m = t.LocalToWorld;
                if (n == _syncBuf.Length) Array.Resize(ref _syncBuf, n * 2);
                _syncBuf[n++] = new ShepPhys.USyncRec { Body = b.PB.Handle, Flags = 0, X = m.m03, Y = m.m13, M00 = m.m00, M10 = m.m10 };
            }
            _flushT.Clear();
            if (n > 0) Teleports += _core.USync(_world, new ReadOnlySpan<ShepPhys.USyncRec>(_syncBuf, 0, n), 0);   // H-SYNC=A
            foreach (var b in _massDirty) if (b.Handle > 0) _core.UBodyMass(_world, b.Handle);
            _massDirty.Clear();
            _core.UFlush(_world, StepFlags);
            if (PhysConfig.CheckSync && Sim3) ChildCheck();   // Stage 3 acceptance #2 (C7): the child bodies after the flush
        }
        finally { _inFlush = false; }
    }
    /// <summary>Flush only when something is queued (the getters' autoSync).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void FlushIfNeeded() { if (_dirty.Count > 0 || _pending.Count > 0) Flush(); }

    static readonly List<NObj> _batch = new();
    static void ProcessPending()
    {
        int rounds = 0;
        while (_pending.Count > 0 && rounds++ < 64)
        {
            _batch.Clear(); _batch.AddRange(_pending); _pending.Clear();
            foreach (var o in _batch) { if (o is NRigidbody2D b) b.PhysQueued = false; else if (o is NCollider2D c) c.PhysQueued = false; }
            foreach (var o in _batch) if (o is NRigidbody2D b) ReconcileBody(b);
            foreach (var o in _batch) if (o is NCollider2D c) ReconcileCollider(c);
            if (Sim4) AfterReconcile4(_batch);   // physics Stage 4a: Synchronous composites whose sub-colliders moved / joined
        }
        _batch.Clear();
    }

    static bool InWorld(NComponent c) => !c.Destroyed && c.Go != null && !c.Go.Destroyed && c.Go.Scene != 0 && c.Go.ActiveInHierarchy && !(c is NCollider2D { DestroyPending: true });

    // ---------------- bodies ----------------
    static int TypeOf(int unityType) => unityType switch { 0 => ShepPhys.BodyDynamic, 1 => ShepPhys.BodyKinematic, _ => ShepPhys.BodyStatic };
    static readonly HashSet<string> _warned = new();
    static void WarnOnce(string what) { if (_warned.Add(what)) Log.Info("[phys] " + what); }
    static ShepPhys.UBodyDef DefOf(NRigidbody2D b, in Matrix4x4 m)
    {
        int f = (b.Simulated ? ShepPhys.UbSimulated : 0) | (b.SleepingMode == 0 ? ShepPhys.UbNeverSleep : 0) | (b.SleepingMode == 2 ? ShepPhys.UbStartAsleep : 0) |
                ((b.Constraints & 4) != 0 ? ShepPhys.UbFreezeRotation : 0) | (b.UseAutoMass ? ShepPhys.UbAutoMass : 0) |
                ((b.Constraints & 1) != 0 ? ShepPhys.UbFreezeX : 0) | ((b.Constraints & 2) != 0 ? ShepPhys.UbFreezeY : 0);
        if ((b.Constraints & 3) != 0 && !Sim3) WarnOnce("RigidbodyConstraints2D.FreezePosition X / Y: not simulated below level 3 (logged once)");   // Stage 3: shim/sp_unity.cpp holds the axis
        if (b.CollisionDetection == 1) WarnOnce("CollisionDetectionMode2D.Continuous: simulated as Discrete (plan 1.2 item 7: CotL's dynamic bodies are all Discrete)");
        return new ShepPhys.UBodyDef
        {
            Type = TypeOf(b.BodyType), Flags = f, X = m.m03, Y = m.m13, M00 = m.m00, M10 = m.m10, Mass = b.Mass, LinearDrag = b.LinearDrag, AngularDrag = b.AngularDrag,
            GravityScale = b.GravityScale, Interpolation = b.Interpolate, Serial = 0, User = b.InstanceId
        };
    }
    static bool SameProps(in ShepPhys.UBodyDef a, in ShepPhys.UBodyDef b) =>
        a.Type == b.Type && a.Flags == b.Flags && Bits(a.Mass) == Bits(b.Mass) && Bits(a.LinearDrag) == Bits(b.LinearDrag) && Bits(a.AngularDrag) == Bits(b.AngularDrag) &&
        Bits(a.GravityScale) == Bits(b.GravityScale) && a.Interpolation == b.Interpolation;
    static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    static void ReconcileBody(NRigidbody2D b)
    {
        if (!InWorld(b)) { if (b.PB != null) DestroyBody(b); return; }
        var t = b.Go.Transform;
        if (b.PB == null) { CreateBody(b, t); return; }
        var pb = b.PB;
        if (!ReferenceEquals(pb.T, t)) { pb.T = t; pb.Depth = DepthOf(t); _orderDirty = true; _flushT.Add(t); }
        var d = DefOf(b, t.LocalToWorld);
        if (!SameProps(d, pb.Def))
        {
            d.Serial = pb.Def.Serial;
            _core.UBodySet(_world, pb.Handle, d);
            pb.Def = d;
            unsafe { _core.MirrorAt(pb.Mirror)->Mode = (d.Interpolation & 3) | ((d.Type & 3) << 4) | ((d.Flags & ShepPhys.UbSimulated) != 0 ? 64 : 0); }
        }
    }
    static int DepthOf(NTransform t) { int d = 0; for (var p = t.Parent; p != null; p = p.Parent) d++; return d; }
    static void CreateBody(NRigidbody2D b, NTransform t)
    {
        var d = DefOf(b, t.LocalToWorld);
        d.Serial = (int)++_serial;
        int h = _core.UBodyCreate(_world, d);
        if (h <= 0) { Log.Error($"[phys] FAIL phys: sp_ubody_create '{b.Go?.Name}' {h}"); return; }
        var pb = new BodyState { Rec = b, T = t, Handle = h, Mirror = _core.UBodyMirror(_world, h), Serial = d.Serial, Depth = DepthOf(t), Def = d };
        unsafe { pb.LastAngle = _core.MirrorAt(pb.Mirror)->Angle; }
        b.PB = pb; _bodies.Add(pb); _orderDirty = true; BodiesMade++;
        if (_bodies.Count > PeakBodies) PeakBodies = _bodies.Count;
        QueueSubtree(t);   // the colliders below attach to it now
    }
    static void DestroyBody(NRigidbody2D b)
    {
        var pb = b.PB; b.PB = null;
        foreach (var s in pb.Shapes) { if (Sim2) Retire(s); _liveShapes -= s.Count; s.Count = 0; s.Body = null; s.Rec.PS = null; Queue(s.Rec); ShapesGone++; }
        pb.Shapes.Clear();
        _core.UBodyDestroy(_world, pb.Handle);
        pb.Handle = 0;
        _bodies.Remove(pb); _massDirty.Remove(pb); BodiesGone++;
        if (!b.Destroyed && b.Go?.Transform is { } t) QueueSubtree(t); else if (pb.T != null && !pb.T.Destroyed) QueueSubtree(pb.T);
    }

    // ---------------- colliders ----------------
    static bool SupportedKind(PhysRecords.Kind2D k) => k is PhysRecords.Kind2D.Circle or PhysRecords.Kind2D.Box or PhysRecords.Kind2D.Polygon or
        PhysRecords.Kind2D.Edge or PhysRecords.Kind2D.Capsule or PhysRecords.Kind2D.Composite;
    /// <summary>Collider2D.attachedRigidbody's record: the nearest Rigidbody2D up the hierarchy (the collider's own GameObject first).</summary>
    internal static NRigidbody2D AttachedRecord(NCollider2D c)
    {
        for (var t = c.Go?.Transform; t != null; t = t.Parent)
            foreach (var k in t.Go.Components) if (k is NRigidbody2D b && !b.Destroyed) return b;
        return null;
    }
    static void ReconcileCollider(NCollider2D c)
    {
        bool composited = c.UsedByComposite && (!Sim4 || CompositeOf(c) != null);   // Stage 4a: usedByComposite without a composite on the body = a normal collider (Paths4a2 Y_flag_first)
        bool want = InWorld(c) && c.Enabled && (Sim2 || !c.IsTrigger) && !composited && SupportedKind(c.Kind);   // Stage 2: triggers are sensors in the core
        if (want && c.Kind == PhysRecords.Kind2D.Tilemap) { WarnOnce("TilemapCollider2D: Stage 4"); want = false; }
        if (!want) { if (c.PS != null) DestroyShape(c); return; }
        var rb = AttachedRecord(c);
        BodyState body = null;
        if (rb != null) { body = rb.PB; if (body == null) { if (c.PS != null) DestroyShape(c); return; } }   // its body is not in the world
        var key = KeyOf(c, body, out var pathsRef);
        var ps = c.PS;
        if (ps != null && ReferenceEquals(ps.Body, body) && ps.Ground == (body == null) && ReferenceEquals(ps.PathsRef, pathsRef) && ps.GenVersion == c.GenVersion && SameKey(ps.Key, key)) return;
        if (ps != null) { DestroyShape(c); ShapeRebuilds++; }
        CreateShape(c, body, key, pathsRef);
    }
    static unsafe bool SameKey(in ShepPhys.UShapeDef a, in ShepPhys.UShapeDef b)
    {
        fixed (ShepPhys.UShapeDef* pa = &a) fixed (ShepPhys.UShapeDef* pb = &b)
            return new ReadOnlySpan<byte>(pa, sizeof(ShepPhys.UShapeDef)).SequenceEqual(new ReadOnlySpan<byte>(pb, sizeof(ShepPhys.UShapeDef)));
    }
    /// <summary>The shape's inputs: kind parameters, the body transform's world 2x2 and the collider's chain below it (or its world 2x3 on the
    /// ground body), material, layer, filter masks. Bitwise-equal inputs = the same fixtures.</summary>
    static unsafe ShepPhys.UShapeDef KeyOf(NCollider2D c, BodyState body, out object pathsRef)
    {
        var d = new ShepPhys.UShapeDef();
        var tc = c.Go.Transform;
        if (body == null)   // H-TILT=matrix (E1): the full matrices
        {
            d.Body2[0] = 1f; d.Body2[3] = 1f;
            var w = tc.LocalToWorld;
            d.Rel[0] = w.m00; d.Rel[1] = w.m01; d.Rel[2] = w.m03; d.Rel[3] = w.m10; d.Rel[4] = w.m11; d.Rel[5] = w.m13;
        }
        else
        {
            var mb = body.T.LocalToWorld;
            d.Body2[0] = mb.m00; d.Body2[1] = mb.m01; d.Body2[2] = mb.m10; d.Body2[3] = mb.m11;
            if (ReferenceEquals(tc, body.T)) { d.Rel[0] = 1f; d.Rel[4] = 1f; }
            else
            {
                var r = tc.LocalMatrix;
                for (var p = tc.Parent; p != null && !ReferenceEquals(p, body.T); p = p.Parent) r = M.Mul(p.LocalMatrix, r);
                d.Rel[0] = r.m00; d.Rel[1] = r.m01; d.Rel[2] = r.m03; d.Rel[3] = r.m10; d.Rel[4] = r.m11; d.Rel[5] = r.m13;
            }
        }
        d.Ox = c.OffsetX; d.Oy = c.OffsetY; d.Density = c.Density;
        var (fr, bo) = MaterialOf(c, body?.Rec);
        d.Friction = fr; d.Restitution = bo;
        d.Layer = c.Go.Layer & 31;
        d.Include = c.IncludeLayers | (body?.Rec.IncludeLayers ?? 0); d.Exclude = c.ExcludeLayers | (body?.Rec.ExcludeLayers ?? 0);
        d.User = c.InstanceId;
        if (Sim2 && c.IsTrigger) d.Flags |= ShepPhys.UsfSensor;   // Stage 2 (the edge's adjacency flags are or'ed in below)
        pathsRef = null;
        switch (c.Kind)
        {
            case PhysRecords.Kind2D.Circle: d.Kind = ShepPhys.UsCircle; d.Radius = c.Radius; break;
            case PhysRecords.Kind2D.Box:
                d.Kind = ShepPhys.UsBox; d.Sx = c.SizeX; d.Sy = c.SizeY; d.Radius = c.EdgeRadius;
                if (c.EdgeRadius > 0f) WarnOnce("BoxCollider2D.edgeRadius > 0: a rounded polygon (b2_polygonRadius + edge radius; UNVERIFIED)");
                break;
            case PhysRecords.Kind2D.Capsule: d.Kind = ShepPhys.UsCapsule; d.Sx = c.SizeX; d.Sy = c.SizeY; d.Direction = c.Direction; WarnOnce("CapsuleCollider2D: a rounded box (plan Stage 1)"); break;
            case PhysRecords.Kind2D.Polygon: d.Kind = ShepPhys.UsPolygon; pathsRef = c.Paths; break;
            case PhysRecords.Kind2D.Edge:
                d.Kind = ShepPhys.UsEdge; d.Radius = c.EdgeRadius; pathsRef = c.Paths;
                d.Flags |= (c.UseAdjacentStartPoint ? ShepPhys.UsfAdjStart : 0) | (c.UseAdjacentEndPoint ? ShepPhys.UsfAdjEnd : 0);   // (H-CHAIN=two-sided, E1)
                d.Adj[0] = c.AdjacentStartX; d.Adj[1] = c.AdjacentStartY; d.Adj[2] = c.AdjacentEndX; d.Adj[3] = c.AdjacentEndY;
                if (Sim4) d.Flags |= ShepPhys.UsfChainWeld;   // Stage 4a: Unity's open-chain vertex filter (sqrt(2.01) x b2_linearSlop, body space)
                break;
            case PhysRecords.Kind2D.Composite:
                int geom = c.GenGeometry >= 0 ? c.GenGeometry : c.GeometryType;
                d.Kind = geom == 1 ? ShepPhys.UsPolygon : ShepPhys.UsLoop; d.Radius = c.EdgeRadius; pathsRef = c.GenPaths ?? c.Paths;
                if (Sim4 && body != null)
                {   // Stage 4a: Unity's composite paths are body-space points (the body's scale baked in at generation) plus the composite's offset: the
                    // shape is exactly those points (no offset); an outline loop takes Unity's loop vertex filter (b2_linearSlop). The affine maps Unity's
                    // body frame into the core's (4b review, UnityToCoreBody: the identity unless the body is mirrored, Y-flipped or tilted)
                    var k = UnityToCoreBody(body.T);
                    d.Body2[0] = 1f; d.Body2[1] = 0f; d.Body2[2] = 0f; d.Body2[3] = 1f;
                    d.Rel[0] = k.K00; d.Rel[1] = k.K01; d.Rel[2] = 0f; d.Rel[3] = k.K10; d.Rel[4] = k.K11; d.Rel[5] = 0f;
                    d.Ox = 0f; d.Oy = 0f;
                    pathsRef = CompositeApiPaths(c);
                    if (geom != 1) d.Flags |= ShepPhys.UsfChainWeld;
                }
                break;
        }
        return d;
    }
    /// <summary>friction / bounciness: the collider's PhysicsMaterial2D, else its body's, else Unity's default (0.4, 0); the project has no
    /// default material (Physics2DSettings m_DefaultMaterial is null).</summary>
    static (float, float) MaterialOf(NCollider2D c, NRigidbody2D b)
    {
        var m = Material(c.Material) ?? Material(b?.Material);
        return m != null ? (m.Friction, m.Bounciness) : (0.4f, 0f);
    }
    static NPhysicsMaterial2D Material(object r)
    {
        if (r is not Assets.LazyRef lr) return r as NPhysicsMaterial2D;
        var u = lr.Resolve();
        return u == null ? null : Objects.Get(u) as NPhysicsMaterial2D;
    }
    static readonly int[] _hbuf = new int[256];
    static void CreateShape(NCollider2D c, BodyState body, in ShepPhys.UShapeDef key, object pathsRef)
    {
        var ps = new ShapeState { Rec = c, Body = body, Key = key, PathsRef = pathsRef, GenVersion = c.GenVersion, Ground = body == null };
        if ((key.Flags & ShepPhys.UsfChainWeld) != 0 && key.Kind == ShepPhys.UsLoop && pathsRef is float[][] lp) CountLoopDrops(lp);   // (Stage 4a: the summary's count)
        int bh = body?.Handle ?? _ground;
        var made = new List<int>();
        void One(in ShepPhys.UShapeDef d, ReadOnlySpan<float> xy)
        {
            int n = _core.UShapeCreate(_world, bh, d, xy, _hbuf);
            if (n < 0) { Log.Error($"[phys] FAIL phys: sp_ushape_create '{c.Go?.Name}' {c.Kind} {n}"); return; }
            for (int i = 0; i < n && i < _hbuf.Length; i++) made.Add(_hbuf[i]);
        }
        float[][] pieces = null;
        if (Sim4 && key.Kind == ShepPhys.UsPolygon && pathsRef is float[][] && DerivedPhysics.Decomposer != null)
        {   // Stage 4b (H-DECOMP): Unity's convex decomposition of every path at once, in Unity's body space (PhysWorld.Stage4.cs), each piece mapped
            // into the core's body frame (UnityToCoreBody) and built natively by Unity's polygon rules (SP_USF_UNITY_POLY: its weld, the hull, its
            // collinear-vertex removal)
            if (c.UseDelaunayMesh)
            {   // (4b review) Unity 2022.3 adds a Delaunay step to such a collider's decomposition: not modelled -- libtess2's pieces (no oracle case)
                if (++DelaunayIgnored == 1)
                    WarnOnce($"H-DECOMP: useDelaunayMesh = true (first: {c.Kind} '{c.Go?.Name}'): Unity's Delaunay decomposition is not modelled -- libtess2's pieces (UNVERIFIED; the count is in the stage 4b summary)");
            }
            if (c.Kind == PhysRecords.Kind2D.Polygon)
                pieces = UnityPieces(UnityPolygonBodySpace(c, body?.T), body != null ? UnityToCoreBody(body.T) : BodyMap.Identity);
            else if (c.Kind == PhysRecords.Kind2D.Composite && body != null)
                pieces = UnityPieces(CompositeApiPaths(c), UnityToCoreBody(body.T));
        }
        if (pieces != null)
        {
            foreach (var piece in pieces)
            {
                if (piece == null || piece.Length < 6) continue;
                var d = key; d.Flags |= ShepPhys.UsfUnityPoly; d.VertexCount = piece.Length / 2;
                One(d, piece);
            }
        }
        else if (pathsRef is float[][] paths)
        {
            if (Sim4 && key.Kind == ShepPhys.UsPolygon) WarnOnce($"H-DECOMP: no owner-local decomposer ({DerivedPhysics.Status}): Stage 1's native decomposition, per path");
            foreach (var path in paths)
            {
                if (path == null || path.Length < 4) continue;
                var d = key; d.VertexCount = path.Length / 2;
                One(d, path);
            }
        }
        else if (c.Kind is PhysRecords.Kind2D.Circle or PhysRecords.Kind2D.Box or PhysRecords.Kind2D.Capsule) One(key, ReadOnlySpan<float>.Empty);
        ps.Handles = made.ToArray(); ps.Count = made.Count;
        c.PS = ps; body?.Shapes.Add(ps);
        if (Sim2) foreach (var h in ps.Handles) _shapeOwner[h] = c;   // Stage 2: the dispatcher's and the queries' shape -> collider
        _liveShapes += ps.Count; ShapesMade++;
        if (_liveShapes > PeakShapes) PeakShapes = _liveShapes;
        if (body != null && body.Def.Type == ShepPhys.BodyDynamic) _massDirty.Add(body);
        if (_ignore.Count > 0 && _ignore.TryGetValue(c, out var partners))
            foreach (var o in partners) if (o.PS != null) foreach (var ha in ps.Handles) foreach (var hb in o.PS.Handles) _core.IgnorePair(_world, ha, hb, true);
    }
    static void DestroyShape(NCollider2D c)
    {
        var ps = c.PS; c.PS = null;
        if (ps.Body != null) { ps.Body.Shapes.Remove(ps); if (ps.Body.Def.Type == ShepPhys.BodyDynamic && ps.Body.Handle > 0) _massDirty.Add(ps.Body); }
        if (ps.Body != null || ps.Ground) foreach (var h in ps.Handles) _core.ShapeDestroy(_world, h);
        if (Sim2) Retire(ps);
        _liveShapes -= ps.Count; ShapesGone++;
    }

    // ---------------- IgnoreCollision (Runtime/Physics2DSettings' icall -> Triggers2D.Ignore -> here) ----------------
    static readonly Dictionary<NCollider2D, List<NCollider2D>> _ignore = new();
    internal static void IgnoreChanged(NBehaviour a, NBehaviour b, bool ignore)
    {
        if (!Sim || a is not NCollider2D ca || b is not NCollider2D cb || ReferenceEquals(ca, cb)) return;
        void Link(NCollider2D x, NCollider2D y)
        {
            if (!_ignore.TryGetValue(x, out var l)) _ignore[x] = l = new List<NCollider2D>();
            if (ignore) { if (!l.Contains(y)) l.Add(y); } else l.Remove(y);
        }
        Link(ca, cb); Link(cb, ca);
        if (_up && ca.PS != null && cb.PS != null) foreach (var ha in ca.PS.Handles) foreach (var hb in cb.PS.Handles) _core.IgnorePair(_world, ha, hb, ignore);
    }

    // ---------------- slot B ----------------
    static void FixedStep1()
    {
        if (!EnsureWorld()) { Triggers2D.Step(); return; }
        Flush();
        var def = new ShepPhys.UStepDef { Dt = Runtime.Time.FixedDeltaTime, VelocityIterations = Physics2DSettings.Values.VelocityIterations,
            PositionIterations = Physics2DSettings.Values.PositionIterations, StepIndex = _stepIndex, Flags = StepFlags };
        if (PhysConfig.TracePath != null) _core.TraceNote(1, stackalloc int[] { Runtime.Time.FrameCount, _stepIndex });
        _tPrev = _tCur; _tCur = Runtime.Time.FixedNow;   // the fixed times of P[k-1], P[k] (render poses: exactly the clock's values)
        int r = _core.UStep(_world, def, out var o);
        if (r != 0) Log.Error($"[phys] FAIL phys: sp_u_step {r}");
        Steps1++; _stepIndex++; Moves += o.Moved; Teleports += o.Teleported; LastContacts = o.Contacts; LastTouching = o.Touching;
        if (o.Bodies > PeakBodies) PeakBodies = o.Bodies;
        WriteBack();
        if (_poseLog != null) PoseLog(o);
        if (_hashOut != null && _stepIndex % 50 == 0) PoseHash();
        if (Sim2) PhysDispatch.Phase(_core, _world, _stepIndex - 1);   // Stage 2: the core's trigger events -> the one dispatcher (Triggers2D's pair search is not run)
        else Triggers2D.Step();    // H-TRIGPHASE=final (E1): after the step and write-back (plan 1.7, Stage 1)
    }

    static void SortBodies()
    {
        if (!_orderDirty) return;
        _bodies.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Serial.CompareTo(b.Serial));   // parent-first (plan 1.5)
        _orderDirty = false;
    }
    /// <summary>Write-back (plan 1.5, C6): bodies flagged by the step, parent-first, under WritingTransform; the written bodies' descendants are
    /// dirtied by the hook as any change (H-CHILDSYNC=follow, E1: a child body follows its parent's write-back at the next flush).</summary>
    static unsafe void WriteBack()
    {
        SortBodies();
        foreach (var pb in _bodies)
        {
            var m = _core.MirrorAt(pb.Mirror);
            if ((m->Flags & ShepPhys.MfWrite) == 0) continue;
            bool ang = (m->Flags & ShepPhys.MfAngle) != 0;
            m->Flags &= ~(ShepPhys.MfWrite | ShepPhys.MfAngle);
            if (WritePose(pb, m->X, m->Y, m->Angle, ang)) WrittenBack++;
        }
    }
    /// <summary>The body transform's x / y (z kept) and, when the angle changed, its rotation turned about world Z by the change (X / Y rotation
    /// kept), under WritingTransform.</summary>
    static bool WritePose(BodyState pb, float x, float y, float angle, bool angleChanged)
    {
        var t = pb.T;
        if (t == null || t.Destroyed || pb.Rec.Destroyed) return false;
        WritingTransform = t;
        try
        {
            var p = t.Position;
            t.SetPosition(new Vector3(x, y, p.z));
            if (angleChanged && Bits(angle) != Bits(pb.LastAngle))
            {
                float h = (angle - pb.LastAngle) * 0.5f;
                var dq = new Quaternion(0f, 0f, _core.Sin(h), _core.Cos(h));
                t.SetRotation(M.QNorm(M.QMul(dq, t.Rotation)));
            }
            pb.LastAngle = angle;
        }
        finally { WritingTransform = null; }
        if (PhysConfig.CheckSync) _stamps[t] = t.ChainStamp;   // physics' own write: not a miss of the dirty hook
        return true;
    }

    // ---------------- slots A and C ----------------
    /// <summary>Slot A: the game's transform writes since slot C (H-SYNC: a real teleport; within Unity's tolerance of the pose physics last wrote,
    /// none). H-EARLY=none (E1's Interp60: FixedUpdate reads the interpolated transform, 0.05 against the body's 0.06, until the step's write-back):
    /// interpolated bodies are not put back to their true pose here (the native sp_u_early stays unused; SlotAWrites stays 0).</summary>
    static void EarlyUpdate1()
    {
        if (!_up) return;
        Flush();
    }
    static unsafe void Update1()
    {
        if (!_up) return;
        if (_core.UInterp(_world, (float)Lifecycle.FixedAlpha, Runtime.Time.FixedDeltaTime, 0) > 0)   // H-INTERP=lerp (E1)
        {
            SortBodies();
            foreach (var pb in _bodies)
            {
                var m = _core.MirrorAt(pb.Mirror);
                if ((m->Flags & ShepPhys.MfPose) == 0) continue;
                m->Flags &= ~ShepPhys.MfPose;
                if (WritePose(pb, m->Ix, m->Iy, m->Ia, Bits(m->Ia) != Bits(pb.LastAngle))) SlotCWrites++;
            }
        }
        _core.USnapshot(_world);
        _snapTk = _tCur; _snapTk1 = _tPrev;
    }

    // ---------------- render poses (plan 1.6, C2) ----------------
    static double _tCur = double.NaN, _tPrev = double.NaN, _snapTk = double.NaN, _snapTk1 = double.NaN;
    static ShepPhys.RPose[] _rposes = new ShepPhys.RPose[64];
    static readonly Dictionary<int, BodyState> _byHandle = new();
    static readonly List<(BodyState pb, float x, float y, float a)> _tickPoses = new();
    public static long RenderRequests, RenderClamped;
    /// <summary>The render poses at render time tr (seconds of game time) from the last tick's snapshot only: Interpolate bodies by Unity's
    /// formula at tr, None bodies at their stepped pose (plan 1.6 rule 3). SHEPHERD_PHYS_RENDERPOSE=generic (A/B only) returns 0: the caller
    /// keeps its generic blending.</summary>
    public static int RenderPoses(double tr, out ReadOnlySpan<ShepPhys.RPose> poses)
    {
        poses = default;
        if (!_up || RenderPoseGeneric) return 0;
        double fdt = Runtime.Time.FixedDeltaTime, tk = _snapTk;
        int jrel = tr >= tk ? 0 : -1;
        double tj = jrel == 0 ? tk : _snapTk1;
        var def = new ShepPhys.RenderDef { JRel = jrel, Alpha = (float)((tr - tj) / fdt), Fdt = (float)fdt };
        int n = _core.RenderPoses(_world, def, _rposes);
        bool clamped = (n & 0x40000000) != 0; n &= 0xFFFF;
        if (n > _rposes.Length) { Array.Resize(ref _rposes, n * 2); n = _core.RenderPoses(_world, def, _rposes) & 0xFFFF; }
        RenderRequests++;
        if (clamped) { RenderClamped++; if (PhysConfig.CheckSync) Log.Error($"[phys] FAIL phys render-time {tr:F6} outside the served window (T_k {tk:F6})"); }
        poses = new ReadOnlySpan<ShepPhys.RPose>(_rposes, 0, n);
        return n;
    }
    public static readonly bool RenderPoseGeneric = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_RENDERPOSE") == "generic";
    /// <summary>For an L60 render-only frame (the renderer's hook point; physcheck render60): body transforms -> their render poses at tr,
    /// the tick's poses remembered; <see cref="RestoreTickPoses"/> puts them back.</summary>
    public static int ApplyRenderPoses(double tr)
    {
        int n = RenderPoses(tr, out var poses);
        _tickPoses.Clear();
        if (n == 0) return 0;
        _byHandle.Clear(); foreach (var pb in _bodies) _byHandle[pb.Handle] = pb;
        foreach (var p in poses)
        {
            if (!_byHandle.TryGetValue(p.Body, out var pb) || pb.T == null || pb.T.Destroyed) continue;
            var cur = pb.T.Position;
            _tickPoses.Add((pb, cur.x, cur.y, pb.LastAngle));
            WritePose(pb, p.X, p.Y, p.Angle, Bits(p.Angle) != Bits(pb.LastAngle));
        }
        return _tickPoses.Count;
    }
    public static void RestoreTickPoses()
    {
        foreach (var (pb, x, y, a) in _tickPoses) WritePose(pb, x, y, a, Bits(a) != Bits(pb.LastAngle));
        _tickPoses.Clear();
    }

    // ---------------- icall helpers (Physics/Physics2DIcalls.cs) ----------------
    /// <summary>rb.position: the true pose (mirror; after a flush), or the transform's when the body is not in the world.</summary>
    internal static unsafe Vector2 Position(NRigidbody2D b)
    {
        if (Physics2DSettings.AutoSyncTransforms || b.PB == null) FlushIfNeeded();
        if (b.PB == null || !_up) { var p = b.Go?.Transform?.Position ?? default; return new Vector2(p.x, p.y); }
        var m = _core.MirrorAt(b.PB.Mirror); return new Vector2(m->X, m->Y);
    }
    internal static unsafe float RotationDeg(NRigidbody2D b)
    {
        if (Physics2DSettings.AutoSyncTransforms || b.PB == null) FlushIfNeeded();
        if (b.PB == null || !_up) { var r = b.Go?.Transform?.Rotation ?? Quaternion.identity; return M.ToEuler(r).z * 57.29578f; }
        return _core.MirrorAt(b.PB.Mirror)->Angle * 57.29578f;
    }
    internal static unsafe Vector2 Velocity(NRigidbody2D b)
    {
        if (b.PB == null || !_up) return default;
        var m = _core.MirrorAt(b.PB.Mirror); return new Vector2(m->Vx, m->Vy);
    }
    /// <summary>rb.MovePosition: the target in the mirror (0 native calls); the next step moves the body there (the last call wins).</summary>
    internal static unsafe void MovePosition(NRigidbody2D b, Vector2 p)
    {
        if (b.PB == null) { FlushIfNeeded(); if (b.PB == null) return; }
        var m = _core.MirrorAt(b.PB.Mirror); m->Tx = p.x; m->Ty = p.y; m->Flags |= ShepPhys.MfMovePos;
    }
    /// <summary>rb.position = p: a body-side teleport (mirror pose + TELEPORT; the next flush applies it with a wake, the history reset and a forced
    /// write-back after the next step: C6).</summary>
    internal static unsafe void SetPosition(NRigidbody2D b, Vector2 p)
    {
        if (b.PB == null) { FlushIfNeeded(); if (b.PB == null) return; }
        var m = _core.MirrorAt(b.PB.Mirror); m->X = p.x; m->Y = p.y; m->Flags |= ShepPhys.MfTeleport;
    }

    // ---------------- determinism outputs ----------------
    static readonly StreamWriter _hashOut = Open("SHEPHERD_PHYS_HASH"), _poseLog = Open("SHEPHERD_PHYS_POSELOG");
    static readonly string _poseMatch = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_POSELOG_MATCH") ?? "";
    static StreamWriter Open(string env)
    {
        var p = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrEmpty(p) || PhysConfig.Level < 1) return null;
        try { return new StreamWriter(p, false) { AutoFlush = false }; } catch (Exception e) { Log.Error($"[phys] {env}: {e.Message}"); return null; }
    }
    /// <summary>Every 50 steps: FNV-1a 64 over every body's (serial, x, y, angle) bits in creation order -- the per-50-step pose hash (plan Stage 1
    /// acceptance #4; frame streams vary run to run, poses must not).</summary>
    static unsafe void PoseHash()
    {
        ulong h = 14695981039346656037ul;
        var list = new List<BodyState>(_bodies); list.Sort((a, b) => a.Serial.CompareTo(b.Serial));
        void Mix(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211ul; } }
        foreach (var pb in list) { var m = _core.MirrorAt(pb.Mirror); Mix((int)pb.Serial); Mix(Bits(m->X)); Mix(Bits(m->Y)); Mix(Bits(m->Angle)); }
        _hashOut.WriteLine($"step {_stepIndex} frame {Runtime.Time.FrameCount} bodies {list.Count} hash {h:x16}");
    }
    static unsafe void PoseLog(in ShepPhys.UStepOut o)
    {
        foreach (var pb in _bodies)
        {
            var name = pb.Rec.Go?.Name ?? "";
            if (_poseMatch.Length > 0 && !name.Contains(_poseMatch, StringComparison.Ordinal)) continue;
            var m = _core.MirrorAt(pb.Mirror);
            _poseLog.WriteLine($"{_stepIndex - 1}\t{Runtime.Time.FrameCount}\t{pb.Serial}\t{name}\t{m->X:R}\t{m->Y:R}\t{m->Angle:R}\t{m->Vx:R}\t{m->Vy:R}\t{m->Tx:R}\t{m->Ty:R}\t{o.Touching}");
        }
    }

    static string Summary1()
    {
        string b2 = "";
        if (_up && _core.B2Stats(out var bs) == 0) b2 = $"; B2 arena {(bs.Grows != 0 ? "on" : "off")}: high water {bs.HighWater} of {bs.Bytes} B, {bs.LiveBlocks} live blocks, {bs.Failures} malloc fallbacks";
        string pool = "";
        if (_up && _core.PoolStats(out var ps) == 0) pool = $"; pool high water {ps.HighWater} of {ps.Bytes} B (grows {ps.Grows})";
        return $"[phys] stage 1 world: {(_up ? "up" : _failed ? "FAILED (stage 0 behaviour)" : "never made")}; steps {Steps1}, flushes {Flushes}; bodies made {BodiesMade} / gone {BodiesGone} " +
               $"(peak {PeakBodies}), shape records made {ShapesMade} / gone {ShapesGone}, rebuilds {ShapeRebuilds} (peak fixtures {PeakShapes}); MovePosition steps {Moves}, " +
               $"teleports {Teleports}, write-backs {WrittenBack}, slot A writes {SlotAWrites}, slot C writes {SlotCWrites}; render-pose requests {RenderRequests} " +
               $"(clamped {RenderClamped}){pool}{b2}; hypotheses used: {string.Join(" ", new[] { "SYNC", "EARLY", "INTERP", "MOVEVEL", "MOVEDRAG", "WAKETP", "CHILDSYNC", "TRIGPHASE", "COMPORIENT", "CLEAN", "CHAIN", "AUTOFIT" }.Select(n => $"H-{n}={Hypotheses.Get(n)}"))}";
    }
}
