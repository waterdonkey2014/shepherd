using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ShepherdVita
{
    public struct Pair<T> { public T A, B; public Pair(T a, T b) { A = a; B = b; } }

    public static class Hello
    {
        // generic method: instantiated over a reference type, a primitive and a struct (gsharedvt / full-AOT instances)
        public static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
        public static Pair<T> Swap<T>(Pair<T> p) => new Pair<T>(p.B, p.A);

        // floating point across calls exercises the hard-float (AAPCS-VFP) calling convention
        public static double Mix(float f, double d, int i, long l, float g) => f * d + i + l * g;

        // native entry the Vita host would provide statically (direct-pinvoke target)
        [DllImport("__Internal", EntryPoint = "shepherd_vita_ticks")]
        public static extern long NativeTicks();

        // hard-float P/Invoke: float/double must travel in s/d registers both ways (AAPCS-VFP)
        [DllImport("__Internal", EntryPoint = "shepherd_vita_fmix")]
        public static extern double NativeFMix(float a, double b, int c, float d);

        // native -> managed entry with float args (UnmanagedCallersOnly callback)
        [System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = "shepherd_vita_managed_cb")]
        public static float ManagedCallback(float x, double y) => (float)(x * y);

        public static int Main()
        {
            Console.WriteLine("Hello from Shepherd on the PS Vita (Mono AOT armv7-hf)");
            int score = Max(3, 7) + Max("a", "b").Length;
            var p = Swap(new Pair<double>(1.5, 2.5));
            var list = new List<string> { "lamb", "cult" };
            list.Sort();
            try
            {
                object o = null;
                if (score > 0) throw new InvalidOperationException("caught " + list[0] + " " + o?.ToString());
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine(e.Message);
            }
            finally
            {
                score += (int)Mix(0.5f, p.A, 1, 2L, 0.25f) + (int)NativeFMix(1.0f, 2.0, 3, 4.0f) + (int)NativeTicks();
            }
            return score;
        }
    }
}
