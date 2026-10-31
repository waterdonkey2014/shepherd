using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 1 (physics-plan-v2 section 2 "Stage 1"): the icalls of walking and wall sliding, bound only at SHEPHERD_PHYS >= 1
/// (<c>Phys = 1</c>: IcallRegistry binds them in a last pass, over Runtime/Triggers2D's bag-backed Rigidbody2D.simulated / bodyType; below
/// level 1 the table is exactly the stage 0 one). The typed records (Physics/PhysRecords.cs) are the truth; the property bag is written too
/// where Runtime/Triggers2D reads it (isTrigger, radius, offset, size, simulated, bodyType), so the trigger pass sees the game's writes.
/// Getters of body state read the mirror (Physics/PhysWorld.Stage1.cs: 0 native calls, after the autoSync flush); setters write records or
/// the mirror and queue the record for the next flush (shape rebuild: queries see it at once from Stage 2, contacts at the next step).
/// No method names a Unity physics type in its signature (IcallRegistry's start-up scan would load the physics modules early).</summary>
static class Physics2DIcalls
{
    static NRigidbody2D B(object o) => Objects.Get(o) as NRigidbody2D;
    static NCollider2D C(object o) => Objects.Get(o) as NCollider2D;

    // ---- Rigidbody2D ----
    [Icall("UnityEngine.Rigidbody2D::MovePosition_Injected", Phys = 1)]
    static object MovePosition(object[] a) { if (B(a[0]) is { } b) PhysWorld.MovePosition(b, (Vector2)a[1]); return null; }
    [Icall("UnityEngine.Rigidbody2D::get_position_Injected", Phys = 1)]
    static object GetPosition(object[] a) { a[1] = B(a[0]) is { } b ? PhysWorld.Position(b) : default(Vector2); return null; }
    [Icall("UnityEngine.Rigidbody2D::set_position_Injected", Phys = 1)]
    static object SetPosition(object[] a) { if (B(a[0]) is { } b) PhysWorld.SetPosition(b, (Vector2)a[1]); return null; }
    [Icall("UnityEngine.Rigidbody2D::get_velocity_Injected", Phys = 1)]
    static object GetVelocity(object[] a) { a[1] = B(a[0]) is { } b ? PhysWorld.Velocity(b) : default(Vector2); return null; }
    [Icall("UnityEngine.Rigidbody2D::get_rotation", Phys = 1)]
    static object GetRotation(object[] a) => B(a[0]) is { } b ? PhysWorld.RotationDeg(b) : 0f;
    [Icall("UnityEngine.Rigidbody2D::get_drag", Phys = 1)]
    static object GetDrag(object[] a) => B(a[0])?.LinearDrag ?? 0f;
    [Icall("UnityEngine.Rigidbody2D::get_bodyType", Phys = 1)]
    static object GetBodyType(object[] a) => Enum.ToObject(typeof(RigidbodyType2D), B(a[0])?.BodyType ?? 0);
    [Icall("UnityEngine.Rigidbody2D::set_bodyType", Phys = 1)]
    static object SetBodyType(object[] a)
    {
        if (B(a[0]) is { } b) { int v = Convert.ToInt32(a[1]); if (b.BodyType != v) { b.BodyType = v; b.Set("bodyType", v); PhysWorld.Changed(b); } }
        return null;
    }
    [Icall("UnityEngine.Rigidbody2D::get_simulated", Phys = 1)]
    static object GetSimulated(object[] a) => B(a[0])?.Simulated ?? true;
    [Icall("UnityEngine.Rigidbody2D::set_simulated", Phys = 1)]
    static object SetSimulated(object[] a)
    {
        if (B(a[0]) is { } b) { bool v = Convert.ToBoolean(a[1]); if (b.Simulated != v) { if (!v) PhysWorld.LeavingBody(b); b.Simulated = v; b.Set("simulated", v); PhysWorld.Changed(b); } }
        return null;
    }

    // ---- Collider2D and its shapes ----
    [Icall("UnityEngine.Collider2D::get_isTrigger", Phys = 1)]
    static object GetIsTrigger(object[] a) => C(a[0])?.IsTrigger ?? false;
    [Icall("UnityEngine.Collider2D::set_isTrigger", Phys = 1)]
    static object SetIsTrigger(object[] a)
    {
        if (C(a[0]) is { } c) { bool v = Convert.ToBoolean(a[1]); if (c.IsTrigger != v) { PhysWorld.TriggerFlipped(c); c.IsTrigger = v; c.Set("isTrigger", v); PhysWorld.Changed(c); } }
        return null;
    }
    [Icall("UnityEngine.Collider2D::get_offset_Injected", Phys = 1)]
    static object GetOffset(object[] a) { var c = C(a[0]); a[1] = c != null ? PG.V2(c.OffsetX, c.OffsetY) : default; return null; }   // kit #9 glue math rule: Um
    [Icall("UnityEngine.Collider2D::set_offset_Injected", Phys = 1)]
    static object SetOffset(object[] a)
    {
        if (C(a[0]) is { } c) { var v = (Vector2)a[1]; c.OffsetX = v.x; c.OffsetY = v.y; c.Set("offset", v); PhysWorld.Changed(c); if (c.UsedByComposite) PhysWorld.SubColliderChanged(c); }   // (Stage 4a: a Synchronous composite follows)
        return null;
    }
    [Icall("UnityEngine.Collider2D::get_attachedRigidbody", Phys = 1)]
    static object GetAttachedRigidbody(object[] a) => C(a[0]) is { } c ? PhysWorld.AttachedRecord(c)?.Managed : null;
    [Icall("UnityEngine.Collider2D::set_usedByComposite", Phys = 1)]
    static object SetUsedByComposite(object[] a)
    {
        if (C(a[0]) is not { } c) return null;
        bool v = Convert.ToBoolean(a[1]);
        if (v && PhysWorld.Sim4 && !PhysWorld.Compositable(c.Kind))
        {   // physics Stage 4a (Paths4a C11-C14): Unity 2022.3 refuses the flag on a circle / edge / capsule (and a composite) with this warning; it stays false
            Composite.Refused++;
            Log.Warn($"[phys] {c.Kind}Collider2D.usedByComposite assign attempt for '{c.Go?.Name}' is not valid as collider is not capable of being composited.");
            return null;
        }
        if (c.UsedByComposite != v) { c.UsedByComposite = v; PhysWorld.Changed(c); PhysWorld.SubColliderChanged(c); }   // (Stage 4a: a Synchronous composite follows at once)
        return null;
    }
    [Icall("UnityEngine.CircleCollider2D::get_radius", Phys = 1)]
    static object GetRadius(object[] a) => C(a[0])?.Radius ?? 0.5f;
    [Icall("UnityEngine.CircleCollider2D::set_radius", Phys = 1)]
    static object SetRadius(object[] a)
    {
        if (C(a[0]) is { } c) { float v = Convert.ToSingle(a[1]); c.Radius = v; c.Set("radius", v); PhysWorld.Changed(c); }
        return null;
    }
    [Icall("UnityEngine.BoxCollider2D::get_size_Injected", Phys = 1)]
    static object GetSize(object[] a) { var c = C(a[0]); a[1] = c != null ? PG.V2(c.SizeX, c.SizeY) : PG.One2; return null; }   // kit #9 glue math rule (Vector2.one = (1, 1))
    [Icall("UnityEngine.BoxCollider2D::set_size_Injected", Phys = 1)]
    static object SetSize(object[] a)
    {
        if (C(a[0]) is { } c) { var v = (Vector2)a[1]; c.SizeX = v.x; c.SizeY = v.y; c.Set("size", v); PhysWorld.Changed(c); if (c.UsedByComposite) PhysWorld.SubColliderChanged(c); }   // (Stage 4a)
        return null;
    }

    // ---- CompositeCollider2D (plan 1.12) ----
    [Icall("UnityEngine.CompositeCollider2D::set_geometryType", Phys = 1)]
    static object SetGeometryType(object[] a)
    {   // Manual: takes effect at the next GenerateGeometry; physics Stage 4a: a Synchronous composite regenerates at once (Paths4a C7s / C7m)
        if (C(a[0]) is { } c) { c.GeometryType = Convert.ToInt32(a[1]); if (PhysWorld.Sim4 && c.GenerationType == 0) Composite.Refresh(c, true); }
        return null;
    }
    [Icall("UnityEngine.CompositeCollider2D::GenerateGeometry", Phys = 1)]
    static object GenerateGeometry(object[] a) { if (C(a[0]) is { } c) Composite.Generate(c); return null; }

    // ---- Physics2D ----
    [Icall("UnityEngine.Physics2D::SyncTransforms", Phys = 1)]
    static object SyncTransforms(object[] a) { PhysWorld.Flush(); return null; }

    // ---- physics Stage 3 (physics-plan-v2 section 2 "Stage 3"): combat -- bound only at level >= 3 with a core (Phys = 3) ----
    // velocity / angular velocity: the mirror (0 native calls; applied at the next flush); forces: the mirror's accumulators (section 3 "Forces")
    [Icall("UnityEngine.Rigidbody2D::set_velocity_Injected", Phys = 3)]
    static object SetVelocity(object[] a) { if (B(a[0]) is { } b) PhysWorld.SetVelocity(b, (Vector2)a[1]); return null; }
    [Icall("UnityEngine.Rigidbody2D::get_angularVelocity", Phys = 3)]
    static object GetAngularVelocity(object[] a) => B(a[0]) is { } b ? PhysWorld.AngularVelocityDeg(b) : 0f;
    [Icall("UnityEngine.Rigidbody2D::set_angularVelocity", Phys = 3)]
    static object SetAngularVelocity(object[] a) { if (B(a[0]) is { } b) PhysWorld.SetAngularVelocityDeg(b, Convert.ToSingle(a[1])); return null; }
    [Icall("UnityEngine.Rigidbody2D::AddForce_Injected", Phys = 3)]
    static object AddForce(object[] a) { if (B(a[0]) is { } b) PhysWorld.AddForce(b, (Vector2)a[1], Convert.ToInt32(a[2])); return null; }
    [Icall("UnityEngine.Rigidbody2D::AddTorque", Phys = 3)]
    static object AddTorque(object[] a) { if (B(a[0]) is { } b) PhysWorld.AddTorque(b, Convert.ToSingle(a[1]), Convert.ToInt32(a[2])); return null; }
    // rotation: a body-side teleport of the angle; MoveRotation: the mirror's target (degrees, Unity's unit)
    [Icall("UnityEngine.Rigidbody2D::set_rotation", Phys = 3)]
    static object SetRotation(object[] a) { if (B(a[0]) is { } b) PhysWorld.SetRotationDeg(b, Convert.ToSingle(a[1])); return null; }
    [Icall("UnityEngine.Rigidbody2D::SetRotation_Angle", Phys = 3)]
    static object SetRotationAngle(object[] a) { if (B(a[0]) is { } b) PhysWorld.SetRotationDeg(b, Convert.ToSingle(a[1])); return null; }
    [Icall("UnityEngine.Rigidbody2D::MoveRotation_Angle", Phys = 3)]
    static object MoveRotation(object[] a) { if (B(a[0]) is { } b) PhysWorld.MoveRotationDeg(b, Convert.ToSingle(a[1])); return null; }
    // the body's record (DefOf -> sp_ubody_set at the next flush: damping, gravity scale, constraints incl. FreezePosition X / Y, mass)
    [Icall("UnityEngine.Rigidbody2D::set_drag", Phys = 3)]
    static object SetDrag(object[] a) { if (B(a[0]) is { } b) { float v = Convert.ToSingle(a[1]); if (Bits(b.LinearDrag) != Bits(v)) { b.LinearDrag = v; PhysWorld.Changed(b); } } return null; }
    [Icall("UnityEngine.Rigidbody2D::get_angularDrag", Phys = 3)]
    static object GetAngularDrag(object[] a) => B(a[0])?.AngularDrag ?? 0.05f;
    [Icall("UnityEngine.Rigidbody2D::set_angularDrag", Phys = 3)]
    static object SetAngularDrag(object[] a) { if (B(a[0]) is { } b) { float v = Convert.ToSingle(a[1]); if (Bits(b.AngularDrag) != Bits(v)) { b.AngularDrag = v; PhysWorld.Changed(b); } } return null; }
    [Icall("UnityEngine.Rigidbody2D::get_gravityScale", Phys = 3)]
    static object GetGravityScale(object[] a) => B(a[0])?.GravityScale ?? 1f;
    [Icall("UnityEngine.Rigidbody2D::set_gravityScale", Phys = 3)]
    static object SetGravityScale(object[] a) { if (B(a[0]) is { } b) { float v = Convert.ToSingle(a[1]); if (Bits(b.GravityScale) != Bits(v)) { b.GravityScale = v; PhysWorld.Changed(b); } } return null; }
    [Icall("UnityEngine.Rigidbody2D::get_constraints", Phys = 3)]
    static object GetConstraints(object[] a) => Enum.ToObject(typeof(RigidbodyConstraints2D), B(a[0])?.Constraints ?? 0);
    [Icall("UnityEngine.Rigidbody2D::set_constraints", Phys = 3)]
    static object SetConstraints(object[] a) { if (B(a[0]) is { } b) { int v = Convert.ToInt32(a[1]); if (b.Constraints != v) { b.Constraints = v; PhysWorld.Changed(b); } } return null; }
    [Icall("UnityEngine.Rigidbody2D::get_freezeRotation", Phys = 3)]
    static object GetFreezeRotation(object[] a) => ((B(a[0])?.Constraints ?? 0) & 4) != 0;
    [Icall("UnityEngine.Rigidbody2D::set_freezeRotation", Phys = 3)]
    static object SetFreezeRotation(object[] a)
    {
        if (B(a[0]) is { } b) { int v = Convert.ToBoolean(a[1]) ? b.Constraints | 4 : b.Constraints & ~4; if (b.Constraints != v) { b.Constraints = v; PhysWorld.Changed(b); } }
        return null;
    }
    [Icall("UnityEngine.Rigidbody2D::get_mass", Phys = 3)]
    static object GetMass(object[] a) => B(a[0])?.Mass ?? 1f;
    [Icall("UnityEngine.Rigidbody2D::set_mass", Phys = 3)]
    static object SetMass(object[] a) { if (B(a[0]) is { } b) { float v = Convert.ToSingle(a[1]); if (Bits(b.Mass) != Bits(v)) { b.Mass = v; PhysWorld.Changed(b); } } return null; }
    static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
}
