using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

public static partial class PhysWorld
{
    /// <summary>Level 6 and the core binds: physics Stage 6's 3D scene (Physics/Phys3D.cs) on top of Stage 3. Stages 4 and 5 are not built, so
    /// levels 4 and 5 are Stage 3 exactly; below 6 no 3D collider is in any scene and the 3D icalls stay "missing, log once".</summary>
    public static bool Sim6 => Sim && PhysConfig.Level >= 6;
}

/// <summary>Physics Stage 6 (physics-plan-v2 section 4 "Stage 6: island raycast", section 2 "Stage 6", section 3 "3D"): the 3D scene the
/// game's ground rays meet. CotL runs no 3D simulation on the intro -> dungeon -> base path; its 3D physics is a ray: UnitObject.LateUpdate's
/// ground lock (every unit, every logic tick: from 10 u behind the unit along +z; a MeshCollider hit sets the unit's z to the hit point's z, no
/// hit sets z = 0), OnGround's Island-layer ray, the followers' RaycastNonAlloc every 30 frames, CompanionBaseArea.StickToFloor.
/// <list type="bullet">
/// <item><b>Membership</b>: every enabled 3D collider (Box, Sphere, Capsule, Mesh) of an active GameObject, as PhysX's scene holds them; the
/// stage 0 hooks (activation, add, destroy, reparent, layer) and the transform hook (PhysInterest bit 4, level 6 only) queue records, and
/// the scene is synced <b>lazily, on the next ray</b> (the project's Physics.autoSyncTransforms is true: a ray sees every transform change
/// made before it). A MeshCollider's triangles are cooked from its Mesh (every Triangles submesh, in order) when the collider joins the
/// scene from data, and at every MeshCollider.sharedMesh assignment (Unity cooks at assignment: later edits of the Mesh do not reach the
/// collider until it is assigned again -- ProBuilder reassigns after each rebuild); identical (mesh, version) pairs share one native mesh.</item>
/// <item><b>Queries</b> (vita/physics/shim/sp_mesh3d.cpp): the closest hit over every collider on the mask (Physics.Raycast), one hit per
/// collider (RaycastNonAlloc: H-NONALLOC orders them), any hit (RaycastTest); triggers per QueryTriggerInteraction (UseGlobal: the project's
/// queriesHitTriggers, true), backfaces per queriesHitBackfaces (false); a ray whose mask meets no layer present costs nothing (the
/// layers-present early-out); a +z ray takes the native grid fast path (bit for bit the general path's answer).</item>
/// <item><b>Checks</b>: SHEPHERD_CHECK_PHYS=ray3d (or all): every ray re-run on the general path and by brute force (no BVH, no grid) -- the
/// three must agree bit for bit -- and every collider's synced pose / shape against one rebuilt from its transform now (a change the
/// hooks missed); SHEPHERD_PHYS_RAYLOG=&lt;file&gt; (owner-local: game positions): one line per ray.</item>
/// </list>
/// The 3D settings: the project's (PhysicsManager, class 55: queriesHitTriggers true, queriesHitBackfaces false, autoSyncTransforms true, gravity
/// (0, 0, -9.81) -- VERIFIED by `physcheck settings` against the run-time-decoded record, which a strict pack does not carry, so a normal run
/// does not read it).</summary>
[ProfileOnly]   // reads typed records and meshes, never a property-bag key (Assets/Diet.cs)
public static class Phys3D
{
    public sealed class ColState { public int Handle; public ShepPhys.Col3Def Def; public MeshState Mesh; }
    public sealed class MeshState { public NMesh Src; public int Version, Handle, Refs, Verts, Tris; }

    // the project's 3D settings (PhysicsManager): VERIFIED by `physcheck settings` (PhysCheck6.SettingsCheck); a decoded record wins when present
    public static bool QueriesHitTriggers => PhysSettings.PhysicsManager.Bytes == 272 ? PhysSettings.PhysicsManager.QueriesHitTriggers : true;
    public static bool QueriesHitBackfaces => PhysSettings.PhysicsManager.Bytes == 272 ? PhysSettings.PhysicsManager.QueriesHitBackfaces : false;
    public static Vector3 Gravity => PhysSettings.PhysicsManager.Bytes == 272 ? PG.V3(PhysSettings.PhysicsManager.GravityX, PhysSettings.PhysicsManager.GravityY, PhysSettings.PhysicsManager.GravityZ)
                                                                              : PG.V3(0f, 0f, -9.81f);   // kit #9 glue math rule
    // hypothesis switches (Appendix A rows added by Stage 6): H-RAYPOINT=bary is E1's (the `ray` variant deleted); H-NONALLOC stays open (E1: PhysX's pruner order)
    static readonly bool NonAllocSorted = Hypotheses.Get("NONALLOC") == "closest";
    static readonly bool CheckRay = PhysConfig.Check == "all" || PhysConfig.Check.Split(',').Contains("ray3d");
    internal static bool CheckRayOn => CheckRay;
    /// <summary>SHEPHERD_PHYS_RAYZ0=1 -- an isolation A/B knob, never a default and not a hypothesis: a MeshCollider hit's point z is reported as +0.0
    /// (what the unbound icall's "no hit" branch gave UnitObject), everything else unchanged -- so a drive at level 6 with it shows what Stage 6 changes
    /// besides the hit z (Intro Room 1: nothing, physics-stage6.md).</summary>
    static readonly bool ZeroZ = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_RAYZ0") == "1";
    public static long ZeroZApplied;

    static ShepPhys _core; static int _scene; static bool _up, _failed;
    static readonly List<NCollider3D> _queue = new(), _batch = new();
    static readonly List<NTransform> _dirty = new(); static readonly HashSet<NTransform> _dirtySet = new();
    static readonly Dictionary<(NMesh, int), MeshState> _meshes = new();
    static readonly HashSet<string> _warned = new();

    // counters (the exit summary)
    public static long Rays, RaysClosest, RaysNonAlloc, RaysTest, RaysHit, Creates, Sets, Destroys, Cooks, CookAtAssign, Syncs, Reconciles;
    public static long ZPlus0, ZMinus0, ZOther, ZChecks; public static float ZMaxAbs; public static string ZMaxWhere = "";
    public static long CheckRays, CheckDiffs, CheckSyncMisses; static string _firstDiff;
    static readonly Dictionary<string, long> _hitsBy = new();

    // ---------------- the scene ----------------
    public static bool EnsureScene()
    {
        if (_up) return true;
        if (_failed || !PhysWorld.Sim6) return false;
        if (!PhysWorld.EnsureWorld()) { _failed = true; return false; }
        _core = ShepPhys.Core as ShepPhys;
        _scene = _core.Scene3DCreate(0);
        if (_scene <= 0) { _failed = true; Log.Error($"[phys] FAIL phys: sp_scene3d_create {_scene}: stage 6 off"); return false; }
        _up = true;
        int n = 0;
        foreach (var o in Objects.All) if (o is NCollider3D k && !k.Destroyed) { Queue(k); n++; }
        Log.Info($"[phys] stage 6: 3D scene up ({n} 3D collider records queued); queriesHitTriggers {QueriesHitTriggers}, queriesHitBackfaces {QueriesHitBackfaces}, gravity {Gravity}; " +
                 $"H-RAYPOINT={Hypotheses.Get("RAYPOINT")}, H-NONALLOC={Hypotheses.Get("NONALLOC")}{(CheckRay ? "; check ray3d on" : "")}");
        return true;
    }
    public static int Scene => _scene;
    internal static ShepPhys Core => _core;

    public static void Queue(NCollider3D k) { if (k != null && !k.Phys3DQueued) { k.Phys3DQueued = true; _queue.Add(k); } }
    public static void QueueGo(NGameObject go) { if (go != null) foreach (var c in go.Components) if (c is NCollider3D k) Queue(k); }
    public static void Destroying(NCollider3D k) => Queue(k);
    /// <summary>NTransform.MarkChanged's hook for a transform with a 3D collider (PhysInterest bit 4).</summary>
    public static void Dirty(NTransform t) { if (_dirtySet.Add(t)) _dirty.Add(t); }
    /// <summary>A reparent: every 3D-collider transform below t moved.</summary>
    public static void Subtree(NTransform t)
    {
        if (t == null || t.Destroyed || t.PhysDesc == 0) return;
        if ((t.PhysInterest & 4) != 0) Dirty(t);
        foreach (var ch in t.Children) if (ch.PhysDesc != 0) Subtree(ch);
    }

    /// <summary>The lazy sync (autoSyncTransforms): the dirty transforms' colliders and the queued records reconciled with the native scene.</summary>
    public static bool Sync()
    {
        if (!EnsureScene()) return false;
        if (_dirty.Count > 0)
        {
            foreach (var t in _dirty) if (!t.Destroyed && t.Go != null) QueueGo(t.Go);
            _dirty.Clear(); _dirtySet.Clear();
        }
        if (_queue.Count == 0) return true;
        Syncs++;
        int rounds = 0;
        while (_queue.Count > 0 && rounds++ < 8)
        {
            _batch.Clear(); _batch.AddRange(_queue); _queue.Clear();
            foreach (var k in _batch) k.Phys3DQueued = false;
            foreach (var k in _batch) Reconcile(k);
        }
        _batch.Clear();
        return true;
    }

    static int KindOf(NCollider3D k) => k.Kind switch
    {
        PhysRecords.Kind3D.Box => ShepPhys.C3Box, PhysRecords.Kind3D.Sphere => ShepPhys.C3Sphere,
        PhysRecords.Kind3D.Capsule => ShepPhys.C3Capsule, PhysRecords.Kind3D.Mesh => ShepPhys.C3Mesh, _ => 0
    };
    static void WarnOnce(string key, string line) { if (_warned.Add(key)) Log.Info(line); }

    /// <summary>The Mesh a MeshCollider names: the loader's reference (a data-loaded collider) or the managed Mesh set by sharedMesh.</summary>
    internal static NMesh MeshOf(NCollider3D k)
    {
        switch (k.Mesh)
        {
            case Assets.LazyRef lr: { var u = lr.Resolve(); return u == null ? null : Objects.Get(u) as NMesh; }
            case UnityEngine.Object uo: return uo == null ? null : Objects.Get(uo) as NMesh;
            default: return null;
        }
    }

    /// <summary>A native mesh for m as it is now (every Triangles submesh in order; other topologies are not collision triangles), shared by
    /// every collider cooked from the same (mesh, version).</summary>
    internal static MeshState Cook(NMesh m)
    {
        if (m == null || m.VertexCount == 0) return null;
        if (_meshes.TryGetValue((m, m.Version), out var ms)) return ms;
        var pos = m.Pos;
        var xyz = new float[pos.Length * 3];
        for (int i = 0; i < pos.Length; i++) { xyz[3 * i] = pos[i].x; xyz[3 * i + 1] = pos[i].y; xyz[3 * i + 2] = pos[i].z; }
        int nidx = 0;
        for (int s = 0; s < m.Sub.Count; s++) if (s >= m.SubTopology.Count || m.SubTopology[s] == 0) nidx += m.Sub[s].Length / 3 * 3;
        var tri = new int[nidx];
        int at = 0, bad = 0;
        for (int s = 0; s < m.Sub.Count; s++)
        {
            if (s < m.SubTopology.Count && m.SubTopology[s] != 0) continue;
            var ix = m.Sub[s];
            for (int i = 0; i + 2 < ix.Length; i += 3)
            {
                if ((uint)ix[i] >= (uint)pos.Length || (uint)ix[i + 1] >= (uint)pos.Length || (uint)ix[i + 2] >= (uint)pos.Length) { bad++; continue; }
                tri[at++] = ix[i]; tri[at++] = ix[i + 1]; tri[at++] = ix[i + 2];
            }
        }
        if (bad > 0) WarnOnce("badidx:" + m.Name, $"[phys] stage 6: mesh '{m.Name}' has {bad} triangle(s) with an index out of range: left out");
        int h = _core.Mesh3DCreate(_scene, xyz, new ReadOnlySpan<int>(tri, 0, at), 0);
        if (h <= 0) { WarnOnce("cook:" + m.Name, $"[phys] FAIL phys: sp_mesh3d_create '{m.Name}' {h}"); return null; }
        ms = new MeshState { Src = m, Version = m.Version, Handle = h, Verts = pos.Length, Tris = at / 3 };
        _meshes[(m, m.Version)] = ms;
        Cooks++;
        return ms;
    }
    static void Acquire(MeshState ms) { if (ms != null) ms.Refs++; }
    static void Release(MeshState ms)
    {
        if (ms == null || --ms.Refs > 0) return;
        _core.Mesh3DDestroy(_scene, ms.Handle);
        _meshes.Remove((ms.Src, ms.Version));
    }

    /// <summary>MeshCollider.sharedMesh = value: the record names it, and (as Unity cooks at assignment) its triangles are taken now; the
    /// cooked mesh waits (one reference held) until the collider is next reconciled.</summary>
    internal static void MeshAssigned(NCollider3D k, UnityEngine.Object mesh)
    {
        k.Mesh = mesh;
        if (EnsureScene())
        {
            var nm = mesh == null ? null : Objects.Get(mesh) as NMesh;
            var ms = Cook(nm);
            Acquire(ms);
            if (_pendingMesh.Remove(k, out var old)) Release(old);
            if (ms != null) { _pendingMesh[k] = ms; CookAtAssign++; }
        }
        k.MeshAssigned = true;
        Queue(k);
    }
    static readonly Dictionary<NCollider3D, MeshState> _pendingMesh = new();

    internal static ShepPhys.Col3Def Def(NCollider3D k, int kind, MeshState ms)
    {
        var t = k.Go.Transform;
        Vector3 p = t.Position, s = t.LossyScale; Quaternion q = t.Rotation;
        var d = new ShepPhys.Col3Def
        {
            Kind = kind, Flags = k.IsTrigger ? ShepPhys.C3fTrigger : 0, Layer = k.Go.Layer & 31, User = k.InstanceId, Mesh = ms?.Handle ?? 0, Direction = k.Direction,
            Px = p.x, Py = p.y, Pz = p.z, Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w, Sx = s.x, Sy = s.y, Sz = s.z, Cx = k.CenterX, Cy = k.CenterY, Cz = k.CenterZ
        };
        switch (kind)
        {
            case ShepPhys.C3Box: d.Ex = k.SizeX; d.Ey = k.SizeY; d.Ez = k.SizeZ; break;
            case ShepPhys.C3Sphere: d.Ex = k.Radius; break;
            case ShepPhys.C3Capsule: d.Ex = k.Radius; d.Ey = k.Height; break;
        }
        return d;
    }
    static bool Degenerate(NCollider3D k, int kind)
    {
        var s = k.Go.Transform.LossyScale;
        float ax = Math.Abs(s.x), ay = Math.Abs(s.y), az = Math.Abs(s.z);
        return kind switch
        {
            ShepPhys.C3Box => !(Math.Abs(k.SizeX) * ax > 0f && Math.Abs(k.SizeY) * ay > 0f && Math.Abs(k.SizeZ) * az > 0f),
            ShepPhys.C3Sphere => !(Math.Abs(k.Radius) * Math.Max(ax, Math.Max(ay, az)) > 0f),
            _ => !(Math.Abs(k.Radius) > 0f && Math.Max(ax, Math.Max(ay, az)) > 0f),
        };
    }
    static bool SameBits(in ShepPhys.Col3Def a, in ShepPhys.Col3Def b) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in a), 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in b), 1)));

    static void Reconcile(NCollider3D k)
    {
        Reconciles++;
        // in a scene (Go.Scene != 0: a prefab asset's template is in no scene), active in hierarchy, enabled -- the 2D world's InWorld rule
        bool want = !k.Destroyed && k.Go != null && !k.Go.Destroyed && k.Go.Scene != 0 && k.Enabled && k.Go.ActiveInHierarchy && k.Go.Transform != null;
        int kind = KindOf(k);
        if (kind == 0) { if (want) WarnOnce("kind:" + k.Kind, $"[phys] stage 6: '{k.Go?.Name}' is a 3D collider of an unsupported kind ({k.Kind}): not in the scene"); want = false; }
        // the mesh the collider uses, with one reference held for it in `ms` (it becomes the collider's, or is released below)
        MeshState ms = null;
        if (want && kind == ShepPhys.C3Mesh)
        {
            if (k.MeshAssigned)
            {
                k.MeshAssigned = false;
                if (_pendingMesh.Remove(k, out var pm)) ms = pm;           // cooked at the assignment (its reference moves here)
                else { ms = Cook(MeshOf(k)); Acquire(ms); }
            }
            else if (k.S3?.Mesh != null) { ms = k.S3.Mesh; Acquire(ms); }   // the collider's cooked mesh stays (later Mesh edits do not reach it)
            else { ms = Cook(MeshOf(k)); Acquire(ms); }                       // from data: cooked as the collider joins the scene
            if (k.Convex) WarnOnce("convex", $"[phys] stage 6: '{k.Go?.Name}' is a convex MeshCollider: tested as its triangles, not PhysX's hull (UNVERIFIED; none on the main path)");
            if (ms == null) want = false;   // no mesh, or an empty one: no shape (never hit)
        }
        if (want && kind != ShepPhys.C3Mesh && Degenerate(k, kind))
        {   // PhysX's geometry must have positive extents (PxBoxGeometry / PxSphereGeometry / PxCapsuleGeometry::isValid): a zero-size primitive (the
            // HUD's 'Cube' under a zero-scale canvas) has no shape in PhysX's scene, so none here (UNVERIFIED how Unity reports it; nothing hits it)
            WarnOnce("degenerate:" + k.Go?.Name, $"[phys] stage 6: '{k.Go?.Name}' ({k.Kind}) has a zero extent under its scale: no shape (PhysX's geometry must be positive)");
            want = false;
        }
        var st = k.S3;
        if (!want)
        {
            if (st != null) { _core.Col3DDestroy(_scene, st.Handle); Release(st.Mesh); k.S3 = null; Destroys++; }
            if (k.Destroyed && _pendingMesh.Remove(k, out var dp)) Release(dp);   // (an assignment that never reached the scene)
            return;
        }
        var def = Def(k, kind, ms);
        if (st == null)
        {
            int h = _core.Col3DCreate(_scene, def);
            if (h <= 0) { WarnOnce("create", $"[phys] FAIL phys: sp_col3d_create '{k.Go.Name}' {h}"); Release(ms); return; }
            k.S3 = new ColState { Handle = h, Def = def, Mesh = ms };
            Creates++;
            return;
        }
        if (!ReferenceEquals(st.Mesh, ms)) { Release(st.Mesh); st.Mesh = ms; } else Release(ms);   // (the collider keeps exactly one reference)
        if (!SameBits(def, st.Def)) { _core.Col3DSet(_scene, st.Handle, def); st.Def = def; Sets++; }
    }

    // ---------------- queries ----------------
    static ShepPhys.Ray3Hit[] _chk = new ShepPhys.Ray3Hit[64], _chk2 = new ShepPhys.Ray3Hit[64];
    /// <summary>One ray (the icalls: Physics2DIcalls' 3D sibling, Physics/Physics3DIcalls.cs): qti = QueryTriggerInteraction (0 UseGlobal,
    /// 1 Ignore, 2 Collide); mode = 0 closest / R3fAll / R3fAny. Returns the hits found (written: min(found, o.Length)); 0 without a scene.</summary>
    internal static int Ray(Vector3 o, Vector3 d, float maxDistance, int layerMask, int qti, int mode, Span<ShepPhys.Ray3Hit> hits)
    {
        Rays++;
        if (mode == 0) RaysClosest++; else if ((mode & ShepPhys.R3fAll) != 0) RaysNonAlloc++; else RaysTest++;
        if (!Sync()) return 0;
        int flags = mode;
        if (qti == 2 || (qti == 0 && QueriesHitTriggers)) flags |= ShepPhys.R3fTriggers;
        if (QueriesHitBackfaces) flags |= ShepPhys.R3fBackfaces;
        if ((mode & ShepPhys.R3fAll) != 0 && NonAllocSorted) flags |= ShepPhys.R3fSort;
        var q = new ShepPhys.Ray3Query { Ox = o.x, Oy = o.y, Oz = o.z, Dx = d.x, Dy = d.y, Dz = d.z, MaxDistance = maxDistance, LayerMask = unchecked((uint)layerMask), Flags = flags };
        int n = _core.Ray3D(_scene, q, hits);
        int w = Math.Min(n, hits.Length);
        if (n > 0) RaysHit++;
        if (w > 0 && mode == 0) ZStat(hits[0]);
        if (CheckRay) CheckOne(q, n, hits);
        if (ZeroZ) for (int i = 0; i < w; i++) if (hits[i].Face >= 0) { hits[i].Pz = 0f; ZeroZApplied++; }   // (after the check: the check compares the native answers)
        if (_log != null) LogRay(q, n, hits);
        return n;
    }
    static void ZStat(in ShepPhys.Ray3Hit h)
    {
        if (!Objects.ById.TryGetValue(h.User, out var r) || r is not NCollider3D k) return;
        string name = k.Go?.Name ?? "?";
        _hitsBy[name] = _hitsBy.GetValueOrDefault(name) + 1;
        if (k.Kind != PhysRecords.Kind3D.Mesh) return;
        ZChecks++;
        int bits = BitConverter.SingleToInt32Bits(h.Pz);
        if (bits == 0) ZPlus0++; else if (bits == unchecked((int)0x80000000)) ZMinus0++; else ZOther++;
        float a = Math.Abs(h.Pz);
        if (a > ZMaxAbs) { ZMaxAbs = a; ZMaxWhere = $"'{name}' at ({h.Px:R}, {h.Py:R}) f{Runtime.Time.FrameCount}"; }
    }
    /// <summary>SHEPHERD_CHECK_PHYS=ray3d: the same ray on the general path and by brute force (both must equal the answer given, bit for bit:
    /// the 44 bytes before the collider handle), and the scene's synced colliders against their transforms now.</summary>
    static void CheckOne(in ShepPhys.Ray3Query q0, int n, ReadOnlySpan<ShepPhys.Ray3Hit> hits)
    {
        CheckRays++;
        int w = Math.Min(n, hits.Length);
        foreach (int f in new[] { ShepPhys.R3fGeneral, ShepPhys.R3fBrute })
        {
            var q = q0; q.Flags |= f;
            if (_chk.Length < hits.Length) _chk = new ShepPhys.Ray3Hit[hits.Length];
            int m = _core.Ray3D(_scene, q, new Span<ShepPhys.Ray3Hit>(_chk, 0, Math.Max(hits.Length, 1)));
            bool ok = m == n;
            for (int i = 0; ok && i < w; i++) ok = SameHit(hits[i], _chk[i]);
            if (ok) continue;
            CheckDiffs++;
            _firstDiff ??= $"f{Runtime.Time.FrameCount} ray ({q0.Ox:R}, {q0.Oy:R}, {q0.Oz:R}) dir ({q0.Dx:R}, {q0.Dy:R}, {q0.Dz:R}) flags 0x{q0.Flags:x}: {n} hit(s) vs {m} with 0x{f:x}";
        }
        foreach (var o in Objects.All)
        {
            if (o is not NCollider3D k || k.S3 == null || k.Destroyed || k.Go?.Transform == null) continue;
            var d = Def(k, KindOf(k), k.S3.Mesh);
            if (!SameBits(d, k.S3.Def)) { CheckSyncMisses++; _firstDiff ??= $"f{Runtime.Time.FrameCount} '{k.Go.Name}': its synced pose / shape differs from its transform now (a change the hooks missed)"; }
        }
    }
    static unsafe bool SameHit(in ShepPhys.Ray3Hit a, in ShepPhys.Ray3Hit b)
    {
        fixed (ShepPhys.Ray3Hit* pa = &a) fixed (ShepPhys.Ray3Hit* pb = &b) return new ReadOnlySpan<byte>(pa, 44).SequenceEqual(new ReadOnlySpan<byte>(pb, 44));
    }

    /// <summary>SHEPHERD_PHYS_RAYLOG=&lt;file&gt;: one line per ray (owner-local: game positions): step, frame, origin and direction bits, max distance,
    /// mask, flags, hits, the first hit's collider, point and distance.</summary>
    static readonly StreamWriter _log = OpenLog();
    static StreamWriter OpenLog()
    {
        var p = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_RAYLOG");
        if (string.IsNullOrEmpty(p) || PhysConfig.Level < 6) return null;
        try { return new StreamWriter(p, false) { AutoFlush = false }; } catch { return null; }
    }
    static void LogRay(in ShepPhys.Ray3Query q, int n, ReadOnlySpan<ShepPhys.Ray3Hit> hits)
    {
        string first = "-";
        if (n > 0 && hits.Length > 0)
        {
            var h = hits[0];
            string name = Objects.ById.TryGetValue(h.User, out var r) && r is NCollider3D k ? $"{k.Go?.Name}:{k.Kind}" : "#" + h.User;
            first = $"{name}\t{h.Px:R},{h.Py:R},{h.Pz:R}\t0x{BitConverter.SingleToInt32Bits(h.Pz):x8}\t{h.Distance:R}\t{h.Face}";
        }
        _log.WriteLine($"{PhysWorld.StepIndex}\t{Runtime.Time.FrameCount}\t{q.Ox:R},{q.Oy:R},{q.Oz:R}\t{q.Dx:R},{q.Dy:R},{q.Dz:R}\t{q.MaxDistance:R}\t0x{q.LayerMask:x}\t0x{q.Flags:x}\t{n}\t{first}");
    }
    public static void Flush() => _log?.Flush();

    /// <summary>The exit line.</summary>
    public static string Summary()
    {
        if (!_up) return $"[phys] stage 6 (3D): no ray asked{(_failed ? " (the scene failed to come up)" : "")}";
        int live = 0, mesh = 0, box = 0, sph = 0, cap = 0;
        foreach (var o in Objects.All)
            if (o is NCollider3D k && k.S3 != null) { live++; switch (k.Kind) { case PhysRecords.Kind3D.Mesh: mesh++; break; case PhysRecords.Kind3D.Box: box++; break; case PhysRecords.Kind3D.Sphere: sph++; break; default: cap++; break; } }
        Span<int> st = stackalloc int[16];
        _core.Scene3DStats(_scene, st);
        Flush();
        var by = string.Join(", ", _hitsBy.OrderByDescending(x => x.Value).Take(6).Select(x => $"'{x.Key}' {x.Value}"));
        var s = $"[phys] stage 6 (3D): {live} colliders in the scene (mesh {mesh}, box {box}, sphere {sph}, capsule {cap}); native meshes {st[1]} ({st[2]} triangles, {st[13]} BVH nodes; " +
                $"cooked {Cooks}, at sharedMesh sets {CookAtAssign}); creates {Creates}, sets {Sets}, removes {Destroys}, syncs {Syncs}; rays {Rays} (Raycast {RaysClosest}, NonAlloc {RaysNonAlloc}, " +
                $"Test {RaysTest}; with a hit {RaysHit}; closest hits by collider: {(by.Length > 0 ? by : "none")}); native: +z fast path {st[5]}, general {st[6]}, layers-absent early-outs {st[4]}, " +
                $"triangle tests {st[7]}, primitive tests {st[8]}, grid builds {st[9]} ({st[10]} cells, {st[11]} entries), layers present 0x{st[12]:x}; " +
                $"mesh-hit z: +0.0 {ZPlus0}, -0.0 {ZMinus0}, other {ZOther} (largest |z| {ZMaxAbs:R}{(ZMaxWhere.Length > 0 ? " " + ZMaxWhere : "")}); H-RAYPOINT={Hypotheses.Get("RAYPOINT")}, H-NONALLOC={Hypotheses.Get("NONALLOC")}";
        if (ZeroZ) s += $"\n[phys] stage 6: SHEPHERD_PHYS_RAYZ0=1 (isolation A/B): {ZeroZApplied} mesh hits reported with z = +0.0";
        if (CheckRay) s += $"\n[phys] stage 6 check ray3d: {CheckRays} rays re-run on the general path and by brute force: {CheckDiffs} differences; synced colliders against their transforms: {CheckSyncMisses} misses{(_firstDiff != null ? "; first: " + _firstDiff : "")}";
        return s;
    }
}
