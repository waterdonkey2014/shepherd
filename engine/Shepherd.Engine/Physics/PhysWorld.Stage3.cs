using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 3 (physics-plan-v2 section 4 "Stage 3", section 2 "Stage 3", section 3 "Forces" / "Time fixes"): the world at
/// SHEPHERD_PHYS=3 -- combat. Stage 2's world plus:
/// <list type="bullet">
/// <item><b>Dynamics setters</b> (the Stage 3 icalls, Physics/Physics2DIcalls.cs): velocity / angular velocity sets in the mirror (0 native calls,
/// applied at the next flush), AddForce / AddTorque into the mirror's accumulators (the first force of a step is a copy, 0 calls; a second one is
/// added natively -- plan 1.10; applied by the next step, then cleared; a force added outside FixedUpdate waits for the next step and a tick without
/// a step carries it), impulses natively into the mirror's velocity (Unity's ForceMode2D.Impulse acts at once), rotation sets (a teleport) and
/// MoveRotation; drag, angular drag, gravity scale, constraints (FreezePosition X / Y now simulated: shim/sp_unity.cpp), mass.</item>
/// <item><b>Hypotheses</b> since E1 (vita/notes/physics-ledger.md; the losing variants deleted): H-DAMP=pade (Box2D's own damping), H-CHAIN=two-sided,
/// H-TILT=matrix (the collider-to-body affine from the full matrices), H-RELVEL=pre-solve, H-RELVELSIGN=other-minus-self, H-NORMAL=toward-self
/// (Collision2D), H-MAXTRANS / H-MAXROT=per-step (Physics2DSettings' maxTranslationSpeed / maxRotationSpeed per step through patch P2: the world's
/// clamps, set at every level from 1 -- PhysWorld.Stage1's world creation), H-CHILDSYNC=follow (Stage 1's write-back; checked here).</item>
/// <item><b>Collision callbacks</b>: Physics/PhysDispatch.cs delivers the collision group (OnCollisionEnter2D / Stay2D / Exit2D with a new
/// Collision2D per callback, reuseCollisionCallbacks = 0) from the contact snapshot of the pair (sp_u_contacts_between).</item>
/// <item><b>Checks</b>: SHEPHERD_CHECK_PHYS=sync at level 3 adds the child-body check (C7, Stage 3 acceptance #2): at every flush, every body under
/// another body's transform against its transform's world pose (follow: equal within H-SYNC's tolerance; frozen: counted as held in place).</item>
/// </list>
/// At level 2 and below nothing here is reached (<see cref="Sim3"/> false); a level-3 run without a core is Stage 0.</summary>
public static partial class PhysWorld
{
    /// <summary>Level 3 or above and the core binds.</summary>
    public static bool Sim3 => Sim2 && PhysConfig.Level >= 3;
    /// <summary>The time fixes (plan section 3; PhysConfig.FixedTimeEnv): on where Stage 3 runs unless SHEPHERD_FIXEDTIME says otherwise.</summary>
    public static bool FixedTimeOn => PhysConfig.FixedTimeEnv >= 0 ? PhysConfig.FixedTimeEnv == 1 : Sim3;
    /// <summary>The frame clock's step cap (plan section 3, D7): Unity's rule (no cap) where Stage 3 runs unless SHEPHERD_PHYS_STEPCAP says otherwise.</summary>
    public static int StepCap => PhysConfig.StepCapEnv > 0 ? PhysConfig.StepCapEnv : Sim3 ? int.MaxValue : 5;

    static void Init3()
    {
        if (!Sim3) return;
        Log.Info($"[phys] stage 3 world (E1's rules): H-DAMP=pade H-CHAIN=two-sided H-TILT=matrix H-RELVEL=pre-solve H-RELVELSIGN=other-minus-self H-NORMAL=toward-self " +
                 $"H-MAXTRANS / H-MAXROT=per-step ({ClampsDesc}) H-CHILDSYNC=follow; time fixes {(FixedTimeOn ? "on" : "off")}, step rule {(StepCap == int.MaxValue ? "unity" : StepCap.ToString())}");
    }

    // ---------------- icall helpers (Physics/Physics2DIcalls.cs, Phys = 3) ----------------
    static BodyState Live(NRigidbody2D b) { if (b.PB == null) FlushIfNeeded(); return b.PB != null && _up ? b.PB : null; }
    /// <summary>rb.velocity = v: the mirror's velocity + SET_VEL (applied at the next flush / step); a later get returns it.</summary>
    internal static unsafe void SetVelocity(NRigidbody2D b, Vector2 v)
    {
        if (Live(b) is not { } pb) return;
        var m = _core.MirrorAt(pb.Mirror); m->Vx = v.x; m->Vy = v.y; m->Flags |= ShepPhys.MfSetVel;
    }
    /// <summary>rb.angularVelocity (degrees / s, Unity's unit; the mirror holds Box2D's rad / s).</summary>
    internal static unsafe float AngularVelocityDeg(NRigidbody2D b) => Live(b) is { } pb ? _core.MirrorAt(pb.Mirror)->W * 57.29578f : 0f;
    internal static unsafe void SetAngularVelocityDeg(NRigidbody2D b, float deg)
    {
        if (Live(b) is not { } pb) return;
        var m = _core.MirrorAt(pb.Mirror); m->W = deg * 0.017453292f; m->Flags |= ShepPhys.MfSetVel;
    }
    public static long ForcesCopied, ForcesAdded, Impulses, Torques;
    /// <summary>rb.AddForce(f, mode): ForceMode2D.Force into the accumulators (the step's first: a copy; a second: added natively), Impulse at once
    /// (natively, into the mirror's velocity).</summary>
    internal static unsafe void AddForce(NRigidbody2D b, Vector2 f, int mode)
    {
        if (Live(b) is not { } pb) return;
        if (mode == 1) { _core.UApply(_world, pb.Handle, ShepPhys.ApplyImpulse, f.x, f.y); Impulses++; return; }
        var m = _core.MirrorAt(pb.Mirror);
        if ((m->Flags & ShepPhys.MfForce) == 0) { m->Fx = f.x; m->Fy = f.y; m->Torque = 0f; m->Flags |= ShepPhys.MfForce; ForcesCopied++; }
        else { _core.UApply(_world, pb.Handle, ShepPhys.ApplyForce, f.x, f.y); ForcesAdded++; }
    }
    /// <summary>rb.AddTorque(t, mode): as AddForce, Box2D's units (N m / kg m^2 s^-1; the unit Unity passes is UNVERIFIED -- one census site).</summary>
    internal static unsafe void AddTorque(NRigidbody2D b, float t, int mode)
    {
        if (Live(b) is not { } pb) return;
        Torques++;
        if (mode == 1) { _core.UApply(_world, pb.Handle, ShepPhys.ApplyAngularImpulse, t, 0f); return; }
        var m = _core.MirrorAt(pb.Mirror);
        if ((m->Flags & ShepPhys.MfForce) == 0) { m->Fx = 0f; m->Fy = 0f; m->Torque = t; m->Flags |= ShepPhys.MfForce; }
        else _core.UApply(_world, pb.Handle, ShepPhys.ApplyTorque, t, 0f);
    }
    /// <summary>rb.rotation = deg: a body-side teleport of the angle (the next flush: SetTransform + wake, a forced write-back, history reset).</summary>
    internal static unsafe void SetRotationDeg(NRigidbody2D b, float deg)
    {
        if (Live(b) is not { } pb) return;
        var m = _core.MirrorAt(pb.Mirror); m->Angle = deg * 0.017453292f; m->Flags |= ShepPhys.MfTeleport;
    }
    /// <summary>rb.MoveRotation(deg): the mirror's angle target (the next step turns the body there; the last call wins).</summary>
    internal static unsafe void MoveRotationDeg(NRigidbody2D b, float deg)
    {
        if (Live(b) is not { } pb) return;
        var m = _core.MirrorAt(pb.Mirror); m->Ta = deg * 0.017453292f; m->Flags |= ShepPhys.MfMoveRot;
    }

    // ---------------- SHEPHERD_CHECK_PHYS=sync at level 3: the child bodies (C7, acceptance #2) ----------------
    public static long ChildChecks, ChildMisses;
    static readonly Dictionary<string, int> _childMissNames = new();
    /// <summary>After a flush: every body whose transform has an ancestor owning a body (a ColliderEvents hitbox, the swipe under the Lamb) against its
    /// transform's world pose. H-CHILDSYNC=follow (E1): the flush has teleported it there (within H-SYNC's tolerance, <see cref="SyncTol"/>), else a miss.</summary>
    static unsafe void ChildCheck()
    {
        foreach (var pb in _bodies)
        {
            if (pb.T == null || pb.T.Destroyed || pb.Handle <= 0) continue;
            bool child = false;
            for (var p = pb.T.Parent; p != null && !child; p = p.Parent) if ((p.PhysInterest & 1) != 0) child = true;
            if (!child) continue;
            ChildChecks++;
            var w = pb.T.LocalToWorld; var m = _core.MirrorAt(pb.Mirror);
            float a = _core.Atan2(w.m10, w.m00);
            bool same = MathF.Abs(m->X - w.m03) <= SyncTol && MathF.Abs(m->Y - w.m13) <= SyncTol && MathF.Abs(m->Angle - a) <= SyncTolAngle;
            if (same) continue;
            ChildMisses++;
            var k = pb.Rec.Go?.Name ?? "?"; _childMissNames[k] = _childMissNames.GetValueOrDefault(k) + 1;
            if (ChildMisses <= 8) Log.Error($"[phys] FAIL phys child sync f{Runtime.Time.FrameCount}: '{k}' core ({m->X:R},{m->Y:R},{m->Angle:R}) transform ({w.m03:R},{w.m13:R},{a:R})");
        }
    }

    static string Summary3() =>
        $"[phys] stage 3: forces copied {ForcesCopied}, added natively {ForcesAdded}, impulses {Impulses}, torques {Torques}" +
        (PhysConfig.CheckSync ? $"; child-body check (H-CHILDSYNC=follow): {ChildChecks} checks, {ChildMisses} misses" +
            (ChildMisses > 0 ? $" ({string.Join(", ", _childMissNames.OrderByDescending(k => k.Value).Take(8).Select(k => $"'{k.Key}' {k.Value}"))})" : "") : "") +
        $"; {PhysDispatch.Summary3()}";
}
