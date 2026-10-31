// Device kit #9, track runtime (vita/notes/kit9-runtime.md): the fast paths of the Unity hooks -- lever 10 (typed icall stubs), E5 (zero-hop
// mirrors) and D6 (the dense instance-id table). tools/Rewriter --typed rewrites the listed icall stubs of the owner's Unity modules
// (owner-local, like the rest of runtime/ManagedVita) so that each first asks here, before the generic object[] forwarder of kit #8:
//   E5  Mirror: a getter whose answer is engine state the engine keeps in the fields below (Time, Screen) or a constant the engine set here
//       returns it from the stub itself -- no call, no allocation (the formula in the stub is the engine icall's own, over the same fields:
//       the engine's Runtime.Time / AppIcalls.ScreenW keep their state IN these fields, so there is one copy of it);
//   10  TypedIcalls: a hot icall calls one of the shape methods below with its arguments as they are (no object[], no boxes); the engine's
//       typed implementation (Shepherd.Engine Icalls/K9TypedIcalls.cs, the same code as its object[] hook) answers through a delegate;
//   D6  Object.FindObjectFromInstanceID (the physics results' hop, ~40 calls a frame in the intro fight) is one of the typed icalls (O_I): the
//       engine answers from Objects.ById itself -- no object[], no box, no Ret -- and keeps no second table (instance ids are never reused,
//       so an id-indexed table would grow with every object ever bound).
// Every fast path is gated: Mirror.On / TypedIcalls.On false (SHEPHERD_K9_MIRROR=0 / SHEPHERD_K9_TYPED=0, or no engine binding) -> the stub runs kit #8's forwarder unchanged. A shape method returns false when no typed implementation is bound
// for the id -> the stub runs the forwarder. Calls through here are counted in Icalls' hit counters exactly as Icalls.Call counts them; a
// mirrored getter counts itself in Mirror.Hits (a plain increment in the stub, no call: review fix), which Icalls.HitCount adds in, so
// icall-hits.txt / MemReport see every listed icall with or without the fast paths.
using System;
namespace Shepherd.Hooks
{
    /// <summary>E5: the engine state the mirrored getters read. The engine's Runtime.Time / AppIcalls keep their fields here (properties
    /// over these), so the stub's formula reads the very values the engine's icall would.</summary>
    public static class Mirror
    {
        /// <summary>The stubs' gate (set by the engine after it checked that every mirrored icall is bound to the hook the formula copies).</summary>
        public static bool On;
        // Shepherd.Engine.Runtime.Time's state (Time.cs: one copy, here)
        public static int FrameCount;
        public static float DeltaTime = 1f / 60f, UnscaledDeltaTime = 1f / 60f, TimeScale = 1f, FixedDeltaTime = 0.02f, FixedUnscaledDeltaTime = 0.02f;
        public static double Now, FixedNow;
        public static bool InFixedStep, Wall;
        /// <summary>Physics.PhysTime.FrameFixed (read once at engine start; the AppIcalls' WallFixed term).</summary>
        public static bool FrameFixed;
        // AppIcalls.ScreenW / ScreenH (one copy, here)
        public static int ScreenW = 1280, ScreenH = 720;
        // constants the engine's hooks return (set by the engine with Mirror.On)
        public static bool MeshCanAccess = true, MaterialHasProperty = true, MousePresent = true, RunInBackground = true;
        public static int TouchCount;
        /// <summary>Kit #9 review fix: per icall id, the calls a mirrored stub answered itself (the stub increments it: no call, so the
        /// zero-hop path stays a few loads; a plain increment, not Interlocked -- the mirrored getters are main-thread-only in Unity, so
        /// the count is exact unless two threads call the same getter at once). Icalls.HitCount adds it to the forwarder's count;
        /// Icalls.Init sizes it.</summary>
        public static long[] Hits = new long[0];
    }

    /// <summary>Lever 10: the typed icall shapes. Each method answers icall <c>id</c> through the typed implementation the engine bound for it
    /// (true) or says it has none (false: the stub then runs the object[] forwarder). Struct arguments / results travel as their float fields
    /// in declaration order (Vector3: x y z; Quaternion: x y z w; Rect: m_XMin m_YMin m_Width m_Height), so no Unity type crosses into Hooks.</summary>
    public static class TypedIcalls
    {
        public static bool On;
        /// <summary>id -> the engine's typed delegate (one of the delegate types below), or null.</summary>
        public static Delegate[] Table = new Delegate[0];

        public delegate void DV3(object self, out float x, out float y, out float z);
        public delegate void DV4(object self, out float x, out float y, out float z, out float w);
        public delegate void DSetV3(object self, float x, float y, float z);

        /// <summary>The bound delegate of id as D, counted like Icalls.Call counts (only when there is one).</summary>
        static Delegate At(int id) { var t = Table; return (uint)id < (uint)t.Length ? t[id] : null; }

        // static getters
        public static bool F_V(int id, out float r) { if (At(id) is Func<float> d) { Icalls.Count(id); r = d(); return true; } r = 0; return false; }
        public static bool I_V(int id, out int r) { if (At(id) is Func<int> d) { Icalls.Count(id); r = d(); return true; } r = 0; return false; }
        public static bool B_V(int id, out bool r) { if (At(id) is Func<bool> d) { Icalls.Count(id); r = d(); return true; } r = false; return false; }
        // static, one argument
        public static bool F_S(int id, string a, out float r) { if (At(id) is Func<string, float> d) { Icalls.Count(id); r = d(a); return true; } r = 0; return false; }
        public static bool B_S(int id, string a, out bool r) { if (At(id) is Func<string, bool> d) { Icalls.Count(id); r = d(a); return true; } r = false; return false; }
        public static bool B_I(int id, int a, out bool r) { if (At(id) is Func<int, bool> d) { Icalls.Count(id); r = d(a); return true; } r = false; return false; }
        public static bool O_I(int id, int a, out object r) { if (At(id) is Func<int, object> d) { Icalls.Count(id); r = d(a); return true; } r = null; return false; }
        // instance getters
        public static bool O_O(int id, object self, out object r) { if (At(id) is Func<object, object> d) { Icalls.Count(id); r = d(self); return true; } r = null; return false; }
        public static bool B_O(int id, object self, out bool r) { if (At(id) is Func<object, bool> d) { Icalls.Count(id); r = d(self); return true; } r = false; return false; }
        public static bool I_O(int id, object self, out int r) { if (At(id) is Func<object, int> d) { Icalls.Count(id); r = d(self); return true; } r = 0; return false; }
        public static bool F_OI(int id, object self, int a, out float r) { if (At(id) is Func<object, int, float> d) { Icalls.Count(id); r = d(self, a); return true; } r = 0; return false; }
        public static bool V3_O(int id, object self, out float x, out float y, out float z)
        {
            if (At(id) is DV3 d) { Icalls.Count(id); d(self, out x, out y, out z); return true; }
            x = y = z = 0; return false;
        }
        public static bool V4_O(int id, object self, out float x, out float y, out float z, out float w)
        {
            if (At(id) is DV4 d) { Icalls.Count(id); d(self, out x, out y, out z, out w); return true; }
            x = y = z = w = 0; return false;
        }
        // instance setters
        public static bool S_OV3(int id, object self, float x, float y, float z) { if (At(id) is DSetV3 d) { Icalls.Count(id); d(self, x, y, z); return true; } return false; }
    }
}
