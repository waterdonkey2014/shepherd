// GAME-AOT mechanics experiment: "App" stands in for Shepherd.Engine -- always AOT-compiled (in the Vita-like configs with the
// fork's inline allowlist, so it never inlines Lib's methods: every call into Lib is a real call) -- calling an owner-local
// module (Lib) that is interpreted, fully AOT'd or partially AOT'd. Modes:
//   check  deterministic results of every feature (CHECK lines; must be identical in every configuration)
//   train  the profile run: hot paths many times, the "cold" paths once, Mathx.Cold never
//   bench  per-call timings of the engine->module call shapes the menu profile found (BENCH lines, ns per iteration)
// Synthetic code written for this experiment.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityLike;

namespace Engine
{
    sealed class Mover : Component
    {
        public Vec3 pos;
        public override int Tick(int frame) { pos = new Vec3(pos.x + 1f, pos.y, frame); return (int) pos.x; }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "check";
            if (mode == "check") return Check();
            if (mode == "train") { Train(); Console.WriteLine("TRAIN done"); return 0; }
            if (mode == "bench") { Bench(args.Length > 1 ? int.Parse(args[1]) : 200000); return 0; }
            Console.WriteLine("usage: check | train | bench [n]");
            return 2;
        }

        static void C(string name, object v) => Console.WriteLine("CHECK " + name + " " + v);

        static int Check()
        {
            object disp = new Disp(5); C("struct-hash-disp", disp.GetHashCode());   // first: a misdispatch shows as a wrong number
            var a = new Vec3(1.5f, -2f, 3.25f); var b = new Vec3(0.5f, 4f, -1f);
            C("add", a + b); C("sub", a - b); C("sqr", (a - b).sqrMagnitude.ToString("R"));
            C("min", Vec3.Min(a, b)); C("max", Vec3.Max(a, b));
            var r = new Rct(1f, 2f, 30f, 40f); C("rect", r.x + r.y * r.width - r.height);
            C("hot", Mathx.Hot(a, 1.25f)); C("hotprim", Mathx.HotPrim(7, 3));
            C("coldcallshot", Mathx.ColdCallsHot(a)); C("coldcallshotprim", Mathx.ColdCallsHotPrim(9));
            { var o = Mathx.HotOdd(new Odd5 { a = 1f, b = 2, c = 3, d = 4, e = 5 }, 2.0, 1, new Odd5 { a = 1f, b = 1, c = 1, d = 1, e = 1 }); C("hotodd", o.a + "," + o.b + "," + o.c + "," + o.d + "," + o.e); }
            C("coldcallshotodd", Mathx.ColdCallsHotOdd(5).ToString("R"));
            C("pick-int", Mathx.Pick(true, 1, 2)); C("pick-str", Mathx.Pick(false, "x", "y")); C("pick-vec", Mathx.Pick(true, a, b));
            C("throws-ok", Mathx.Throws(21));
            try { Mathx.Throws(-3); C("throws", "no exception"); } catch (ArgumentOutOfRangeException e) { C("throws", e.ParamName + "|" + e.Message.Split('\n')[0].Trim()); }
            C("coldcatches", Mathx.ColdCatches(-5));
            C("adder", Mathx.Adder(40)(2));
            { Op single = Mathx.AddOp; Op multi = Mathx.Multi(); C("delegate-single", single(3, 4)); C("delegate-multicast", multi(3, 4)); }
            var cs = new List<Component> { new Mover(), new Mover() }; C("runall", Mathx.RunAll(cs, 3) + Mathx.RunAll(cs, 4)); C("name", cs[0].Name);
            var pi = new Pool<int>(); pi.Add(5); pi.Add(6); C("pool-int", pi[1] + pi.Count);
            var pv = new Pool<Vec3>(); pv.Add(a); pv.Add(b); C("pool-vec", pv[0] + pv[1]);
            var ps = new Pool<string>(); ps.Add("s"); C("pool-str", ps[0] + ps.Count);
            C("noinline", Mathx.NeverInlined(41)); C("cold", Mathx.Cold(6));
            C("nested", Outer.Inner.Twice(21)); C("global", GlobalNs.Thrice(14));
            var ctrs = new List<Counter> { new Counter(3), new Counter(1), new Counter(2) }; ctrs.Sort();
            C("struct-sort", ctrs[0] + "," + ctrs[1] + "," + ctrs[2]);
            object boxed = new Counter(7); C("struct-tostring", boxed.ToString()); C("struct-hash", boxed.GetHashCode());
            C("struct-equals", boxed.Equals(new Counter(7)) + "/" + boxed.Equals(new Counter(8)));
            IComparable<Counter> ic = new Counter(5); C("struct-iface", ic.CompareTo(new Counter(9)));
            Func<int> getter = new Counter(9).Get; C("struct-delegate", getter());
            object w = new Wrap<int>(4); C("gstruct-tostring", w.ToString()); object ws = new Wrap<string>("s"); C("gstruct-tostring-ref", ws.ToString());
            Console.WriteLine("CHECK-END");
            return 0;
        }

        static void Train()
        {
            var a = new Vec3(1.5f, -2f, 3.25f); var b = new Vec3(0.5f, 4f, -1f); var r = new Rct(1f, 2f, 30f, 40f);
            float s = 0; int t = 0;
            var cs = new List<Component> { new Mover(), new Mover() };
            var pv = new Pool<Vec3>();
            for (int i = 0; i < 2000; i++) {
                s += r.x + r.y + r.width + r.height;
                a = Vec3.Min(a, b) + Vec3.Max(a, b) - b; s += (a - b).sqrMagnitude;
                a = Mathx.Hot(a, 0.5f); t += Mathx.HotPrim(i, 3);
                t += Mathx.RunAll(cs, i); t += Mathx.Pick(true, i, 1);
                pv.Add(a); t += Outer.Inner.Twice(i) + GlobalNs.Thrice(i);
                t += Mathx.HotOdd(new Odd5 { a = i, b = i }, 1.0, 1, new Odd5()).b + new Disp(i).Hot();
            }
            // the cold paths: once each
            a = Mathx.ColdCallsHot(a); t += (int) Mathx.ColdCallsHotOdd(1) + Mathx.ColdCallsHotPrim(3) + Mathx.ColdCatches(-1) + Mathx.Adder(1)(2) + Mathx.NeverInlined(1);
            Console.WriteLine("TRAIN sum " + s + " " + t + " " + a + " " + pv.Count);
        }

        static void Bench(int n)
        {
            var results = new Dictionary<string, List<double>>();
            for (int rep = 0; rep < 7; rep++) {
                Run(results, "rect4", n, BRect);
                Run(results, "vec3ops", n, BVec);
                Run(results, "sqrmag", n, BSqr);
                Run(results, "hot", n, BHot);
                Run(results, "hotprim", n, BHotPrim);
                Run(results, "coldcallshot", n / 4, BColdCallsHot);
                Run(results, "virt16", n / 16, BVirt);
                Run(results, "local", n, BLocal);
            }
            foreach (var kv in results) {
                kv.Value.Sort();
                Console.WriteLine($"BENCH {kv.Key} {kv.Value[kv.Value.Count / 2]:F2} ns/iter (min {kv.Value[0]:F2} max {kv.Value[kv.Value.Count - 1]:F2})");
            }
            Console.WriteLine("BENCH sink " + sink);
        }

        static double sink;
        static void Run(Dictionary<string, List<double>> res, string name, int n, Func<int, double> f)
        {
            long t0 = Stopwatch.GetTimestamp();
            sink += f(n);
            long t1 = Stopwatch.GetTimestamp();
            if (!res.TryGetValue(name, out var l)) res[name] = l = new List<double>();
            l.Add((t1 - t0) * 1e9 / Stopwatch.Frequency / n);
        }

        // 4 tiny struct getters per iteration (SceneRenderer.Project's Rect.x/y/width/height per vertex)
        static double BRect(int n) { var r = new Rct(1f, 2f, 3f, 4f); float s = 0; for (int i = 0; i < n; i++) s += r.x + r.y * r.width - r.height; return s; }
        // 3 struct operators per iteration (MeshOutside's Vector3.Min/Max per vertex)
        static double BVec(int n) { var lo = new Vec3(9, 9, 9); var hi = new Vec3(-9, -9, -9); var v = new Vec3(0, 1, 2); var d = new Vec3(0.001f, -0.001f, 0.002f); for (int i = 0; i < n; i++) { lo = Vec3.Min(lo, v); hi = Vec3.Max(hi, v); v = v + d; } return lo.x + hi.z; }
        // 2 calls: operator - and the sqrMagnitude getter (DrawWorld's sort distance)
        static double BSqr(int n) { var a = new Vec3(1, 2, 3); var b = new Vec3(0.5f, 0.25f, 0.125f); float s = 0; for (int i = 0; i < n; i++) { s += (a - b).sqrMagnitude; a.x += 1e-6f; } return s; }
        static double BHot(int n) { var v = new Vec3(1, 2, 3); for (int i = 0; i < n; i++) v = Mathx.Hot(v, 0.999f); return v.x; }
        static double BHotPrim(int n) { int s = 0; for (int i = 0; i < n; i++) s += Mathx.HotPrim(i, 3); return s; }
        // 1 call to a cold (interpreted in the partial image) Lib method that calls 2 hot (AOT'd) Lib methods
        static double BColdCallsHot(int n) { var v = new Vec3(1, 2, 3); for (int i = 0; i < n; i++) v = Mathx.ColdCallsHot(v) - v; return v.y; }
        // Lib.RunAll over 16 App overrides: 16 virtual calls Lib -> App per iteration
        static List<Component> movers;
        static double BVirt(int n) { if (movers == null) { movers = new List<Component>(); for (int i = 0; i < 16; i++) movers.Add(new Mover()); } int s = 0; for (int i = 0; i < n; i++) s += Mathx.RunAll(movers, i); return s; }
        // the same arithmetic as BRect on App's own fields: the "field arithmetic" fix's speed (no call into Lib)
        struct R4 { public float x, y, w, h; }
        static double BLocal(int n) { var r = new R4 { x = 1f, y = 2f, w = 3f, h = 4f }; float s = 0; for (int i = 0; i < n; i++) s += r.x + r.y * r.w - r.h; return s; }
    }
}
