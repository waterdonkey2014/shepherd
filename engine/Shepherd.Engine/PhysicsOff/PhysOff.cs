#if SHEPHERD_NO_PHYS
using System.Reflection;
using Shepherd.Assets;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Physics;

// Kit #8 review fix (k8-physics, vita/notes/kit8-physics.md "Review fixes"): the BUILD switch back to kit #7's engine image. vita/shepherd/
// build.sh SHEPHERD_PHYS_CODE=0 builds the engine with -p:ShepherdPhysCode=0 (Shepherd.Engine.csproj): every file of Physics/ is left out but
// the owner-local provider's contract (Physics/DerivedPhysics.cs: the card's one Shepherd.Engine.Derived.dll is shared by every app and is
// compiled against it, so it stays byte for byte the same), and this file is compiled in instead -- the surface the rest of the engine calls
// (the hooks of Loader / Lifecycle / Objects / Cloner / Triggers2D / the icalls), each answering what SHEPHERD_PHYS=none answers: no
// groundwork, no typed records, no hooks, no loop slots (Triggers2D.Step where kit #7 called it), the frame clock's old step cap, no level
// hooks. That is kit #7's engine paths, and the physics runtime's ~1.1 MiB of engine AOT text (every app's eboot span) is gone with it.
// SHEPHERD_PHYS is not read at all in such a build. Never in the default build (the csproj leaves PhysicsOff/ out of it).
// No string literal here (Assets/Diet.cs reads the engine's literals).

public static class PhysConfig
{
    public const int Level = -1;
    public const bool On = false;
    public const bool TriggersNoMatrix = false, TriggersShadow = false, TriggersParity = false;
}

public static class PhysTime
{
    public const bool FrameFixed = false;
    public const int FrameStepCap = 5;
}

public static class PhysWorld
{
    public const bool Sim = false, Sim2 = false, Sim6 = false;
    public static readonly NTransform WritingTransform = null;
    public static void EarlyUpdate() { }
    public static void FixedStep() => Runtime.Triggers2D.Step();   // (never called: Lifecycle's loop slots are off -- PhysConfig.On)
    public static void Update() { }
    public static void Added(NComponent c) { }
    public static void AddedAtRuntime(NComponent c) { }
    public static void Assembled(NGameObject go) { }
    public static void Destroying(NObj n) { }
    public static void DestroyRequested(NObj n) { }
    public static void Activated(NGameObject go) { }
    public static void Deactivated(NGameObject go) { }
    public static void Enabled(NBehaviour b, bool on) { }
    public static void LayerChanged(NGameObject go) { }
    public static void Reparented(NTransform t) { }
    public static void ParentChanging(NTransform t, NTransform oldParent, NTransform newParent) { }
    public static void SubtreeGone(NTransform root) { }
    public static void TransformReplaced(NTransform old, NTransform rt) { }
    public static void Dirty(NTransform t) { }
    public static void AnimatedField(NComponent c, string key) { }
    public static void IgnoreChanged(NBehaviour a, NBehaviour b, bool ignore) { }
    public static bool CoreColliderMatrix(NBehaviour b, out Matrix4x4 m) { m = default; return false; }
}

public static class PhysRecords
{
    public static NComponent New(Type t) => null;
    public static NObj NewAsset(int classId) => null;
    public static void Fill(NObj rec, int classId, TypeTreeObject d, SerializedFile f) { }
    public static void Copy(NObj src, NObj dst) { }
    public static void AutoFit(NCollider2D c, NGameObject go) { }
    public static void NoteRuntime() { }
}

public static class PhysGuard
{
    public static int Skipped;
    public static bool IsPhysicsModule(Type t) => false;
}

public static class PhysHooks
{
    public static string Outside(List<(MethodInfo m, int level, string key)> scanned) => null;
    public static List<(MethodInfo m, int level, string key)> Scan() => new();
}

// the typed records' types (the hooks' `is` tests): never made in this build
public sealed class NRigidbody2D : NComponent { }
public sealed class NCollider2D : NBehaviour { }
public sealed class NCollider3D : NComponent { }
#endif
