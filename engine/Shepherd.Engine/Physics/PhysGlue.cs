using System.Runtime.CompilerServices;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Kit #9 (physics-plan-v2 Stage 7 item 5, the "glue math rule" of the hop fix): the physics glue builds Unity structs with
/// <see cref="Um"/>'s field stores instead of calling Unity's constructors, constants and Mathf (each such call is an AOT -> interpreter
/// hop on the Vita: the engine image may not inline Unity's methods). The same IEEE operations on the same operands (Um's contract), so the
/// values are bit-identical. SHEPHERD_PHYS_GLUE=k8: Unity's own methods, as kit #8 called them (the switch back).</summary>
[ProfileOnly]   // switch name only
public static class PG
{
    public static readonly bool K8 = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_GLUE") == "k8";
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 V2(float x, float y) => K8 ? new Vector2(x, y) : Um.V2(x, y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector3 V3(float x, float y, float z) => K8 ? new Vector3(x, y, z) : Um.V3(x, y, z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Quaternion Q(float x, float y, float z, float w) => K8 ? new Quaternion(x, y, z, w) : Um.Q(x, y, z, w);
    public static Vector2 One2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => K8 ? Vector2.one : Um.V2(1f, 1f); }
    public static Quaternion QIdentity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => K8 ? Quaternion.identity : Um.QIdentity; }
    public static Matrix4x4 Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => K8 ? Matrix4x4.identity : Um.Identity; }
    /// <summary>Mathf.Abs(f) = Math.Abs(f).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Abs(float f) => K8 ? Mathf.Abs(f) : MathF.Abs(f);
    /// <summary>Mathf.Max(a, b) = a &gt; b ? a : b.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Max(float a, float b) => K8 ? Mathf.Max(a, b) : Um.Max(a, b);
    /// <summary>Rect.width / height = m_Width / m_Height (Um's mirror; Unity's getters when its layout check failed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float W(Rect r) => K8 ? r.width : Um.Of(r).W;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float H(Rect r) => K8 ? r.height : Um.Of(r).H;
}
