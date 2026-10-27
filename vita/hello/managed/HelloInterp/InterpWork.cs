using System;
using System.Collections.Generic;

namespace ShepherdVita.Interp
{
    // Runs in the interpreter (this assembly has no AOT image). Expected result is checked by Hello.
    public static class InterpWork
    {
        static T Twice<T>(T x, Func<T, T, T> add) => add(x, x);

        public static long Run(int n)
        {
            long s = 0;
            var list = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(i * 3);
                s += i ^ (i << 1);
            }
            // AOT-compiled List<int>.Sort calls back into an interpreted lambda
            list.Sort((a, b) => b.CompareTo(a));
            s += list[0];
            try
            {
                throw new ArgumentException("interp");
            }
            catch (ArgumentException e)
            {
                s += e.Message.Length;
            }
            Func<int, int> inc = x => x + 1;
            s += inc(41);
            s += (long)Twice(2.5, (a, b) => a + b);   // generic method over double in interpreted code
            s += Twice("ab", string.Concat).Length;
            var d = new Dictionary<string, long> { ["k"] = s };
            return d["k"];
        }
    }
}
