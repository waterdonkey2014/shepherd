using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 3 (physics-plan-v2 section 1.8 "Collision2D", semantics.md section 9): the argument of OnCollisionEnter2D / Stay2D /
/// Exit2D. reuseCollisionCallbacks = 0 in the project, so every callback side gets a new Collision2D (and a new contacts array):
/// <list type="bullet">
/// <item>m_Collider = the other collider, m_OtherCollider = this one; m_Rigidbody / m_OtherRigidbody likewise (instance ids; 0 without a body:
/// Collision2D.gameObject is then the collider's).</item>
/// <item>the contacts: the pair's touching points after this step (shim's sp_u_contacts_between over the two colliders' shapes): point,
/// normal (H-NORMAL=toward-self: from the other collider into this one, the grounded-check idiom; toward-other), separation, the last solve's
/// normal / tangent impulses, the relative velocity at the point and the pair's relative linear velocity (H-RELVEL: pre-solve, the velocities
/// the step started from / post-solve; H-RELVELSIGN: the other's minus this one's / the reverse), enabled; an Exit has no contacts.</item>
/// <item>the fields are internal: written through cached FieldInfos (the plan's UnsafeAccessor cannot be used -- a static extern accessor would
/// name Collision2D in a static signature, which IcallRegistry's start-up scan must not see); ContactPoint2D is a blittable struct written
/// through an identical engine layout (56 B, checked once).</item>
/// </list>
/// The cost (reflection writes, an array per callback) is Stage 7's (the Hooks fast path); combat delivers a handful per step.</summary>
[ProfileOnly]   // reads no property-bag key; log text only
static class Collisions
{
    static readonly Impl I = new();
    public static long Built, Points, Exits;
    /// <summary>A new Collision2D for <paramref name="self"/> about <paramref name="other"/> (no Unity physics type in this static signature).</summary>
    public static object Build(NCollider2D self, NCollider2D other, bool exit) => I.Build(self, other, exit);

    [StructLayout(LayoutKind.Sequential)]
    struct CP2D
    {
        public Vector2 Point, Normal, RelativeVelocity;
        public float Separation, NormalImpulse, TangentImpulse;
        public int Collider, OtherCollider, Rigidbody, OtherRigidbody, Enabled;
    }

    sealed class Impl
    {
        FieldInfo _col, _other, _rb, _otherRb, _rel, _enabled, _count, _reused;
        bool _ready, _layoutOk;
        ShepPhys.UContact[] _buf = new ShepPhys.UContact[8];
        void Ready()
        {
            if (_ready) return;
            _ready = true;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var t = typeof(Collision2D);
            _col = t.GetField("m_Collider", F); _other = t.GetField("m_OtherCollider", F); _rb = t.GetField("m_Rigidbody", F); _otherRb = t.GetField("m_OtherRigidbody", F);
            _rel = t.GetField("m_RelativeVelocity", F); _enabled = t.GetField("m_Enabled", F); _count = t.GetField("m_ContactCount", F); _reused = t.GetField("m_ReusedContacts", F);
            int n = Marshal.SizeOf<ContactPoint2D>(), m = Marshal.SizeOf<CP2D>();
            _layoutOk = n == 56 && m == 56;
            if (!_layoutOk || _col == null || _reused == null)
                Log.Error($"[phys] FAIL phys: Collision2D / ContactPoint2D layout unexpected (ContactPoint2D {n} B, the engine's {m} B; fields {(_col != null ? "found" : "missing")}): contacts left empty");
        }
        static ReadOnlySpan<int> Shapes(NCollider2D c) { var ps = c.PS; return ps == null || ps.Count == 0 ? ReadOnlySpan<int>.Empty : new ReadOnlySpan<int>(ps.Handles, 0, ps.Count); }
        public object Build(NCollider2D self, NCollider2D other, bool exit)
        {
            Ready();
            var c = new Collision2D();
            int selfBody = PhysWorld.AttachedRecord(self)?.InstanceId ?? 0, otherBody = PhysWorld.AttachedRecord(other)?.InstanceId ?? 0;
            int n = 0; float rx = 0f, ry = 0f;
            var core = PhysWorld.Core2;
            if (core != null && !exit && _layoutOk)
            {
                // the shapes as the step left them: no flush here (a handler's structural edits wait for the next query or step, so every callback of
                // this phase sees the post-step contacts, as Unity compiles them before it calls out)
                var a = Shapes(self); var b = Shapes(other);
                if (a.Length > 0 && b.Length > 0)
                {
                    const int flags = 0;   // H-RELVEL=pre-solve (E1)
                    n = core.UContactsBetween(PhysWorld.World2, a, b, flags, _buf, out rx, out ry);
                    if (n > _buf.Length) { _buf = new ShepPhys.UContact[n * 2]; n = core.UContactsBetween(PhysWorld.World2, a, b, flags, _buf, out rx, out ry); }
                }
            }
            const float rs = 1f, ns = -1f;   // H-RELVELSIGN=other-minus-self, H-NORMAL=toward-self (E1); the native normal runs self -> other
            var pts = new ContactPoint2D[n];
            bool enabled = n == 0;
            for (int i = 0; i < n; i++)
            {
                ref var u = ref _buf[i];
                var cp = new CP2D
                {
                    Point = new Vector2(u.Px, u.Py), Normal = new Vector2(ns * u.Nx, ns * u.Ny), RelativeVelocity = new Vector2(rs * u.Rvx, rs * u.Rvy),
                    Separation = u.Separation, NormalImpulse = u.NormalImpulse, TangentImpulse = u.TangentImpulse,
                    Collider = other.InstanceId, OtherCollider = self.InstanceId, Rigidbody = otherBody, OtherRigidbody = selfBody,
                    Enabled = (u.Flags & ShepPhys.UcpfEnabled) != 0 ? 1 : 0
                };
                if (cp.Enabled != 0) enabled = true;
                pts[i] = Unsafe.As<CP2D, ContactPoint2D>(ref cp);
            }
            if (_col != null)
            {
                _col.SetValue(c, other.InstanceId); _other.SetValue(c, self.InstanceId); _rb.SetValue(c, otherBody); _otherRb.SetValue(c, selfBody);
                _rel.SetValue(c, new Vector2(rs * rx, rs * ry)); _enabled.SetValue(c, enabled ? 1 : 0); _count.SetValue(c, n); _reused.SetValue(c, pts);
            }
            Built++; Points += n; if (exit) Exits++;
            return c;
        }
    }
}
