// GAME-AOT mechanics experiment (vita/gameaot/mech, $SHEPHERD_WORK/gameaot/mechanics.md): "Lib" stands in for an
// owner-local module (a rewritten Unity module such as UnityEngine.CoreModule, or a game assembly): small struct methods
// the engine calls per vertex (Rect getters, Vector3 operators), medium methods, generics, closures, exceptions, and a
// virtual call back into the caller's assembly. It is AOT-compiled whole, partially (profile-only) or not at all.
// Synthetic code written for this experiment; nothing here is derived from game or Unity code.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace UnityLike
{
    public struct Vec3
    {
        public float x, y, z;
        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.x - b.x, a.y - b.y, a.z - b.z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(a.x < b.x ? a.x : b.x, a.y < b.y ? a.y : b.y, a.z < b.z ? a.z : b.z);
        public static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(a.x > b.x ? a.x : b.x, a.y > b.y ? a.y : b.y, a.z > b.z ? a.z : b.z);
        public override string ToString() => $"({x:R},{y:R},{z:R})";
    }

    public struct Rct
    {
        float m_X, m_Y, m_W, m_H;
        public Rct(float x, float y, float w, float h) { m_X = x; m_Y = y; m_W = w; m_H = h; }
        public float x => m_X;
        public float y => m_Y;
        public float width => m_W;
        public float height => m_H;
    }

    // value-type methods reached through an interface, a virtual Object method or a delegate on a BOXED struct: the runtime
    // needs an unbox trampoline for each (the partial-image hazard)
    public struct Counter : IComparable<Counter>, IEquatable<Counter>
    {
        public int n;
        public Counter(int n) { this.n = n; }
        public int CompareTo(Counter o) => n.CompareTo(o.n);
        public bool Equals(Counter o) => n == o.n;
        public override bool Equals(object o) => o is Counter c && c.n == n;
        public override int GetHashCode() => n * 31;
        public override string ToString() => "Counter#" + n;
        public int Get() => n + 1000;
    }

    // a struct layout + signature no other image has: the interpreter -> AOT call to HotOdd needs a gsharedvt_out_sig wrapper
    // for exactly this (normalized) signature, and only Lib's own image can carry it
    public struct Odd5 { public float a; public int b; public double c; public short d; public byte e; }

    // the misdispatch probe: Hot() is hot (in the profile), GetHashCode() is not; both are int-returning instance methods
    public struct Disp
    {
        public int v;
        public Disp(int v) { this.v = v; }
        public override int GetHashCode() => v;
        public int Hot() { int s = 0; for (int i = 0; i < 3; i++) s += 259; return s; }   // 777
    }

    public struct Wrap<T>
    {
        public T v;
        public Wrap(T v) { this.v = v; }
        public override string ToString() => "Wrap(" + v + ")";
    }

    public abstract class Component
    {
        public abstract int Tick(int frame);
        public virtual string Name => GetType().Name;
    }

    public sealed class Pool<T>
    {
        readonly List<T> items = new List<T>();
        public void Add(T v) => items.Add(v);
        public T this[int i] => items[i];
        public int Count => items.Count;
    }

    public delegate int Op(int a, int b);

    public static class Mathx
    {
        public static int AddOp(int a, int b) => a + b;
        public static int MulOp(int a, int b) => a * b;
        public static Op Multi() => (Op) AddOp + MulOp;      // multicast: Op.Invoke goes through a delegate-invoke wrapper
        // medium-size hot methods (loops: never inlined by the interpreter or the AOT compiler)
        public static Vec3 Hot(Vec3 a, float k)
        {
            Vec3 r = a;
            for (int i = 0; i < 4; i++) r = new Vec3(r.y * k + 1f, r.z - k, r.x * 0.5f);
            return r;
        }
        public static int HotPrim(int a, int b)
        {
            int s = 0;
            for (int i = 0; i < 8; i++) s += (a ^ i) * b;
            return s;
        }
        // cold callers of the hot methods: not in the training profile, so in the partial image they stay interpreted and
        // call the AOT'd hot methods through the interpreter's JIT_CALL path (interp -> AOT)
        public static Vec3 ColdCallsHot(Vec3 a) => Hot(a, 2f) + Hot(a, 3f);
        public static Odd5 HotOdd(Odd5 x, double k, byte m, Odd5 y)
        {
            Odd5 r = x;
            for (int i = 0; i < 2; i++) { r.a = r.a * (float) k + y.a; r.b += y.b ^ m; r.c = r.c * k - y.c; r.d += y.d; r.e ^= m; }
            return r;
        }
        public static double ColdCallsHotOdd(int n)
        {
            var x = new Odd5 { a = 1.5f, b = n, c = 2.25, d = 3, e = 4 }; var y = new Odd5 { a = 0.5f, b = 7, c = 0.125, d = 1, e = 9 };
            var r = HotOdd(x, 1.5, 3, y);
            return r.a + r.b + r.c + r.d + r.e;
        }
        public static int ColdCallsHotPrim(int a) => HotPrim(a, 3) + HotPrim(a, 5);
        public static T Pick<T>(bool c, T a, T b) => c ? a : b;
        public static int Throws(int x)
        {
            if (x < 0) throw new ArgumentOutOfRangeException(nameof(x), "negative " + x);
            return x * 2;
        }
        public static int ColdCatches(int x)
        {
            try { return Throws(x); } catch (ArgumentOutOfRangeException e) { return -e.Message.Length; }
        }
        public static Func<int, int> Adder(int k) => v => v + k;
        public static int RunAll(List<Component> cs, int frame)
        {
            int s = 0;
            foreach (var c in cs) s += c.Tick(frame);   // virtual call into the caller's assembly
            return s;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int NeverInlined(int x) => x + 1;
#if LIB_VARIANT2
        public static int Cold(int x) => x * 7 + 2;       // variant 2: same API, new MVID (GUID-gate experiment)
#else
        public static int Cold(int x) => x * 7 + 1;       // never called in the training run
#endif
    }

    // the global namespace, a nested type and a compiler-generated closure class: the profile writer's name forms
    public static class Outer
    {
        public static class Inner { public static int Twice(int x) { int s = 0; for (int i = 0; i < 2; i++) s += x; return s; } }
    }
}

public static class GlobalNs
{
    public static int Thrice(int x) { int s = 0; for (int i = 0; i < 3; i++) s += x; return s; }
}
