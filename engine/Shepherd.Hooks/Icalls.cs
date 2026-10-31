// The single seam between Unity's managed layer and our engine. Every Unity native hook ("icall") was rewritten by
// tools/Rewriter to `return Icalls.Ret<T>(Icalls.Call(id, args))` (args[0] = `this` for instance methods; ref/out
// params are written back from args after the call). The engine fills Table by name from runtime/icalls.json.
using System;
using System.Threading;
namespace Shepherd.Hooks
{
    public delegate object IcallImpl(object[] args);

    public static class Icalls
    {
        public static IcallImpl[] Table = new IcallImpl[0];
        public static string[] Names = new string[0];
        /// <summary>Per-id hit counts (the missing-icall report, icall-hits.txt, MemReport). Read them through <see cref="HitCount"/> /
        /// <see cref="HitCounts"/>: kit #6 (track fps) counts each hit in a 32-bit counter first (<see cref="_lo"/>, folded into this array
        /// before it could overflow), so the two together are the count kit #5 kept here alone.</summary>
        public static long[] Hits = new long[0];
        /// <summary>kit #6 fps: the 32-bit part of each count. Interlocked.Increment(ref int) is one inline ldrex/strex sequence in the
        /// Vita's AOT code; the 64-bit Interlocked.Increment kit #5 made on every icall is a managed-to-native wrapper + an icall on ARM32
        /// (no 64-bit atomic intrinsic in Mono's 32-bit backend). Same counts (exact up to 2^62 per id), same atomicity.</summary>
        static int[] _lo = new int[0];
        /// <summary>SHEPHERD_ICALL_HITS=64 (env file): every hit counted with the 64-bit Interlocked.Increment, as kit #5 did.</summary>
        static readonly bool Wide = Environment.GetEnvironmentVariable("SHEPHERD_ICALL_HITS") == "64";
        /// <summary>A 32-bit counter is folded into <see cref="Hits"/> when it reaches this (a single hit per id never overflows it).</summary>
        const int FoldAt = 1 << 30;
        static readonly object _fold = new object();
        /// <summary>Called once per unimplemented icall id (engine logs it — that log is the bring-up work queue).</summary>
        public static Action<int> OnMissing;
        static int[] _missingSeen = new int[0];

        public static object Call(int id, object[] args)
        {
            var t = Table;
            if ((uint)id < (uint)t.Length)
            {
                if (!Wide) { if (Interlocked.Increment(ref _lo[id]) >= FoldAt) Fold(id); }
                else Interlocked.Increment(ref Hits[id]);
                var f = t[id];
                if (f != null) return f(args);
                if (Interlocked.Exchange(ref _missingSeen[id], 1) == 0) OnMissing?.Invoke(id);
            }
            return null;
        }

        /// <summary>Kit #9 (track runtime, K9Typed.cs): one hit of id counted exactly as <see cref="Call"/> counts it -- the typed fast paths call
        /// this, so icall-hits.txt / MemReport see the same counts with or without them.</summary>
        public static void Count(int id)
        {
            if (!Wide) { if (Interlocked.Increment(ref _lo[id]) >= FoldAt) Fold(id); }
            else Interlocked.Increment(ref Hits[id]);
        }

        /// <summary>Moves a 32-bit counter's hits into the 64-bit count (the counter restarts at 0; no hit is lost or counted twice:
        /// the exchange takes exactly the hits counted so far, and only folds add to <see cref="Hits"/> in this mode).</summary>
        static void Fold(int id)
        {
            lock (_fold)
            {
                if (Volatile.Read(ref _lo[id]) < FoldAt) return;   // another thread folded it already
                int v = Interlocked.Exchange(ref _lo[id], 0);
                Interlocked.Add(ref Hits[id], v);
            }
        }

        /// <summary>The hits of id so far (the 64-bit count + the 32-bit counter not yet folded + -- kit #9 review fix -- the calls the E5
        /// mirrored stubs answered themselves, Mirror.Hits: a mirrored icall keeps its count).</summary>
        public static long HitCount(int id) => Interlocked.Read(ref Hits[id]) + (id < _lo.Length ? Volatile.Read(ref _lo[id]) : 0) + (id < Mirror.Hits.Length ? Mirror.Hits[id] : 0);
        /// <summary>Every id's hits so far, as one array (what kit #5's Hits held).</summary>
        public static long[] HitCounts()
        {
            var h = Hits; var r = new long[h.Length];
            for (int i = 0; i < r.Length; i++) r[i] = HitCount(i);
            return r;
        }

        public static T Ret<T>(object o) => o is T v ? v : default(T);

        public static void Init(int count, string[] names)
        {
            Names = names; Table = new IcallImpl[count]; Hits = new long[count]; _lo = new int[count]; _missingSeen = new int[count];
            Mirror.Hits = new long[count];   // (kit #9 review fix: the mirrored stubs' own counts, per id)
        }
    }
}
