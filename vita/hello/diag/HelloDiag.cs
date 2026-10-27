// SHEPHERD-VITA DIAG (devfail1, 2026-10-07): "Shepherd Hello Diag" (SHPD00015). The first device run of the V5 hello died right after
// M5 with "exception.c:137 ... mono_exception_from_token ... NullReferenceException" (cause found off-device: the runtime has no
// EventPipe and the host did not turn EventSource off; see $SHEPHERD_WORK/devfail1/hello-nre.md). This app splits that area
// into one-thing-per-line tests, each LOGGED BEFORE IT RUNS ("run  Dn ..."), so a crash or hang names the exact test; the diagnostic
// runtime (vita/patches/shepherd-vita-diag.patch) adds "N> EXC ..." lines for every exception the runtime creates and, if a creation
// fails, "N> EXC-FAIL ..." lines with the full MonoError, the image, the token, the failed step and the managed stack.
// Then the unchanged V5 hello (Hello.dll: M1-M99, incl. M10 threads+GC), then a finer threads+GC ladder (G1-G5).
// Modes (the host passes --diag-mode=...):
//   normal   EventSource off (the fix): every test should pass
//   repro    EventSource ON (the 2026-10-07 setting): D3 is expected to die the old way, now with the EXC-FAIL details
//   gcfirst  the G ladder runs before Hello's M-tests (for when M10 dies): finds the smallest threads+GC case that fails
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ShepherdVita.Diag.Interp;

namespace ShepherdVita.Diag
{
    public static class HelloDiag
    {
        [DllImport("__Internal", EntryPoint = "shepherd_hello_log")]
        static extern void HostLog(int step, [MarshalAs(UnmanagedType.LPUTF8Str)] string msg);

        const int D = 100, G = 110, X = 120;   // log step numbers: "M100 ..." = D tests, "M110" = G ladder, "M120" = other
        static int failures, ran;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static T Opaque<T>(T x) => x;

        sealed class NullEnumerable : IEnumerable<string>
        {
            public IEnumerator<string> GetEnumerator() => null;   // CoreLib's string.Join calls MoveNext on this: a null deref INSIDE CoreLib
            IEnumerator IEnumerable.GetEnumerator() => null;
        }

        // an explicit static constructor (not beforefieldinit): the cctor runs at the first access inside the try below, not at the
        // calling method's entry (with a field initializer only, the AOT code ran it before the try: vlx, 2026-10-08)
        static class ThrowingCctor
        {
            public static readonly int Value;
            static ThrowingCctor() { Value = Fail(); }
            static int Fail() => throw new InvalidOperationException("thrown by a static constructor");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ReadThrowingCctor() => ThrowingCctor.Value;

        /// <summary>one test: "run" line first (a crash leaves it as the last line), then ok/FAIL with what was seen</summary>
        static void T(int step, string id, string what, Func<string> body, Func<string, bool> ok = null)
        {
            ran++;
            HostLog(step, "run  " + id + " " + what);
            string got;
            try
            {
                got = body();
            }
            catch (Exception e)
            {
                failures++;
                HostLog(step, "FAIL " + id + " escaped: " + e.GetType().FullName + ": " + SafeMessage(e));
                return;
            }
            bool pass = ok == null || ok(got);
            if (!pass)
                failures++;
            HostLog(step, (pass ? "ok   " : "FAIL ") + id + " " + got);
        }

        /// <summary>expects exactly exception type E from body; reports its message (a resource string for most runtime exceptions)</summary>
        static void Expect<E>(int step, string id, string what, Action body) where E : Exception
        {
            T(step, id, what, () =>
            {
                try
                {
                    body();
                }
                catch (E e)
                {
                    return "caught " + e.GetType().FullName + ": '" + SafeMessage(e) + "'";
                }
                return "NO EXCEPTION";
            }, s => s.StartsWith("caught ", StringComparison.Ordinal));
        }

        static string SafeMessage(Exception e)
        {
            try { return e.Message; } catch (Exception m) { return "(Message threw " + m.GetType().Name + ")"; }
        }

        public static int Main(string[] args)
        {
            string mode = "normal";
            foreach (var a in args)
                if (a.StartsWith("--diag-mode=", StringComparison.Ordinal))
                    mode = a.Substring("--diag-mode=".Length);
            HostLog(X, "info HelloDiag Main entered, mode=" + mode + ", args=" + string.Join(" ", args));

            Exceptions(mode);
            T(X, "M4b", "Hello's gsharedvt run-time transitions (Hello.GsharedvtTransitions through reflection: they run before M10 here)", () =>
            {
                var m = typeof(ShepherdVita.Hello).GetMethod("GsharedvtTransitions", BindingFlags.NonPublic | BindingFlags.Static);
                m.Invoke(null, new object[] { 4 });
                return "returned (its own M4 line above has the verdict)";
            });
            if (mode == "gcfirst")
                GcLadder();
            HostLog(X, "info running the unchanged V5 hello now: Hello.Main (M1-M99)");
            int helloRc = ShepherdVita.Hello.Main(new string[0]);
            HostLog(X, "info Hello.Main returned " + helloRc);
            if (helloRc != 0)
                failures++;
            if (mode != "gcfirst")
                GcLadder();

            HostLog(X, "info GC counts gen0=" + GC.CollectionCount(0) + " gen1=" + GC.CollectionCount(1) + " gen2=" + GC.CollectionCount(2) + " heap=" + (GC.GetTotalMemory(false) / 1024) + " KiB");
            HostLog(99, failures == 0 ? "PASS all " + ran + " diag tests + Hello M1-M99" : "FAIL " + failures + " of " + ran + " diag tests (incl. Hello.Main's result)");
            return failures == 0 ? 0 : 1;
        }

        static void Exceptions(string mode)
        {
            T(D, "D0", "runtime facts (no exception)", () =>
            {
                bool has = AppContext.TryGetSwitch("System.Diagnostics.Tracing.EventSource.IsSupported", out bool on);
                return "EventSource.IsSupported switch " + (has ? on.ToString() : "unset (defaults to true)") + "; IntPtr.Size=" + IntPtr.Size +
                       "; cores=" + Environment.ProcessorCount + "; mode=" + mode;
            });
            T(D, "D1", "new InvalidOperationException(\"msg\") -- an exception object with its own message (no resource lookup)",
              () => new InvalidOperationException("msg").Message, s => s == "msg");
            Expect<InvalidOperationException>(D, "D2", "throw + catch of it (M5's first check)", () => throw new InvalidOperationException("thrown"));
            T(D, "D3", "ArrayPool<byte>.Shared.Rent(16) -- the first ArrayPool use: runs ArrayPoolEventSource's static constructor (repro mode: dies here, the old way)", () =>
            {
                byte[] b = ArrayPool<byte>.Shared.Rent(16);
                int n = b.Length;
                ArrayPool<byte>.Shared.Return(b);
                return "rented " + n + " bytes and returned them";
            });
            T(D, "D4", "new NullReferenceException() -- managed new; its ctor looks up SR.Arg_NullReferenceException (the first resource string)",
              () => "Message='" + new NullReferenceException().Message + "'", s => s.Length > 11);
            Expect<NullReferenceException>(D, "D5", "throw + catch of a managed-new NullReferenceException", () => throw new NullReferenceException());
            Expect<NullReferenceException>(D, "D6", "null dereference in AOT code (Hello's M5: an explicit null check -> mono_exception_from_token)",
              () => Opaque<object>(null).GetHashCode());
            Expect<NullReferenceException>(D, "D7", "null dereference in INTERPRETED code (virtual call on null: the interpreter creates it by name)",
              () => InterpTests.NullDeref(null));
            Expect<NullReferenceException>(D, "D7b", "null dereference in interpreted code (array length of null)", () => InterpTests.NullField(null));
            Expect<NullReferenceException>(D, "D8", "null dereference INSIDE AOT CoreLib code (string.Join calls MoveNext on a null enumerator)",
              () => string.Join(",", new NullEnumerable()));
            Expect<NullReferenceException>(D, "D9", "explicit 'throw null' in AOT code", () => ThrowNullAot());
            Expect<NullReferenceException>(D, "D9i", "explicit 'throw null' in interpreted code", () => InterpTests.ThrowNull());
            Expect<IndexOutOfRangeException>(D, "D10", "IndexOutOfRangeException by token (AOT bounds check)", () => Use(Opaque(new int[2])[Opaque(5)]));
            Expect<IndexOutOfRangeException>(D, "D10i", "IndexOutOfRangeException in the interpreter", () => Use(InterpTests.IndexOutOfRange(new int[2], 5)));
            Expect<InvalidCastException>(D, "D11", "InvalidCastException by token (AOT castclass)", () => Use(((string)Opaque<object>(42)).Length));
            Expect<InvalidCastException>(D, "D11i", "InvalidCastException in the interpreter", () => Use(InterpTests.InvalidCast(42).Length));
            Expect<DivideByZeroException>(D, "D12", "DivideByZeroException by token (AOT explicit division check)", () => Use(10 / Opaque(0)));
            Expect<DivideByZeroException>(D, "D12i", "DivideByZeroException in the interpreter", () => Use(InterpTests.DivideByZero(10, 0)));
            Expect<OverflowException>(D, "D13", "OverflowException by token (AOT checked add)", () => Use(checked(Opaque(1) + int.MaxValue)));
            Expect<OverflowException>(D, "D13i", "OverflowException in the interpreter (checked add)", () => Use(InterpTests.Overflow(1)));
            Expect<ArithmeticException>(D, "D13b", "int.MinValue / -1 in AOT code (Overflow/ArithmeticException)", () => Use(Opaque(int.MinValue) / Opaque(-1)));
            Expect<ArithmeticException>(D, "D13bi", "int.MinValue / -1 in the interpreter", () => Use(InterpTests.OverflowMinDivMinusOne(int.MinValue, -1)));
            Expect<ArrayTypeMismatchException>(D, "D14", "ArrayTypeMismatchException (covariant array store, AOT)", () => { object[] a = Opaque<object[]>(new string[1]); a[0] = 42; });
            Expect<ArrayTypeMismatchException>(D, "D14i", "ArrayTypeMismatchException in the interpreter", () => InterpTests.ArrayTypeMismatch(new string[1]));
            Expect<NullReferenceException>(D, "D15", "invoking a null delegate", () => Opaque<Action>(null)());
            T(D, "D16", "new NullReferenceException().Message from interpreted code", () => "'" + InterpTests.NewNullReference() + "'", s => s.Length > 2);

            // resource strings (SR.*): the paths the 2026-10-07 cascade went through
            T(D, "D17a", "SR: new ArgumentException().Message", () => "'" + new ArgumentException().Message + "'", s => s.Length > 2);
            Expect<ArgumentNullException>(D, "D17b", "SR: ArgumentNullException.ThrowIfNull(null)", () => ArgumentNullException.ThrowIfNull(Opaque<object>(null)));
            Expect<FormatException>(D, "D17c", "SR: int.Parse(\"lamb\") -> FormatException (formatted resource string)", () => Use(int.Parse(Opaque("lamb"))));
            Expect<KeyNotFoundException>(D, "D17d", "SR: Dictionary lookup of a missing key (message formats the key)", () => Use(new Dictionary<string, int>()["missing"]));
            T(D, "D17e", "SR.GetResourceString(\"Arg_NullReferenceException\") through reflection", () =>
            {
                var sr = typeof(object).Assembly.GetType("System.SR");
                var m = sr?.GetMethod("GetResourceString", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
                return m == null ? "SR.GetResourceString(string) not found (trimmed?)" : "'" + (string)m.Invoke(null, new object[] { "Arg_NullReferenceException" }) + "'";
            });
            T(D, "D18", "EventPipe icall: System.Runtime.JitInfo.GetCompiledMethodCount() (this runtime has no EventPipe: NotImplementedException expected)", () =>
            {
                try { return "returned " + System.Runtime.JitInfo.GetCompiledMethodCount(false) + " (EventPipe present?)"; }
                catch (NotImplementedException e) { return "NotImplementedException as expected: '" + SafeMessage(e) + "'"; }
            });
            T(D, "D19", "an exception's StackTrace string (trace capture + frame names)", () =>
            {
                try { ThrowDeep(3); return "no exception"; }
                catch (InvalidOperationException e) { var st = e.StackTrace ?? "(null)"; return st.Split('\n').Length + " frame line(s), first: " + st.Split('\n')[0].Trim(); }
            }, s => !s.StartsWith("no ", StringComparison.Ordinal));
            T(D, "D20", "exception in a static constructor -> TypeInitializationException with the inner exception", () =>
            {
                try { return "no exception: " + ReadThrowingCctor(); }
                catch (TypeInitializationException e) { return "TypeInitializationException, inner " + e.InnerException?.GetType().Name + ": '" + SafeMessage(e.InnerException) + "'"; }
            }, s => s.StartsWith("TypeInitializationException, inner InvalidOperationException", StringComparison.Ordinal));
            T(D, "D21", "exception filter (catch ... when) + rethrow", () =>
            {
                int filtered = 0;
                try
                {
                    try { throw new ArgumentException("inner"); }
                    catch (ArgumentException e) when (++filtered > 0 && e.Message == "inner") { throw; }
                }
                catch (ArgumentException e) { return "filter ran " + filtered + "x, rethrown '" + e.Message + "' caught"; }
                return "no exception";
            }, s => s.StartsWith("filter ran 1x", StringComparison.Ordinal));
            T(D, "D22", "AOT List.Sort calling an interpreted comparer that throws (caught in the interpreter)", () => InterpTests.ThrowThroughAotSort(),
              s => s.StartsWith("caught in the interpreter", StringComparison.Ordinal));
            Expect<ArgumentException>(D, "D23", "an interpreted method throwing into this AOT caller", () => InterpTests.ThrowToAotCaller("from the interpreter"));
            T(D, "D24", "25 x throw/catch NullReferenceException from an explicit check (the exception path, repeated)", () =>
            {
                int n = 0;
                for (int i = 0; i < 25; i++)
                    try { Use(Opaque<object>(null).GetHashCode()); } catch (NullReferenceException) { n++; }
                return n + " of 25 caught";
            }, s => s.StartsWith("25 of 25", StringComparison.Ordinal));
            HostLog(X, "info exception tests done: " + failures + " failure(s) so far");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNullAot() => throw null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDeep(int n) { if (n == 0) throw new InvalidOperationException("deep"); ThrowDeep(n - 1); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Use<V>(V v) { }

        // ---------------------------------------------------------------- threads + GC, smallest first (cooperative stop-the-world)

        static void GcLadder()
        {
            T(G, "G1", "GC.Collect() x3 on this thread only (no other managed thread running)", () =>
            {
                int g2 = GC.CollectionCount(2);
                for (int i = 0; i < 3; i++) GC.Collect();
                return "full GCs " + (GC.CollectionCount(2) - g2);
            });
            T(G, "G2", "start + join 1 thread (no allocation, no GC)", () =>
            {
                int x = 0;
                var t = new Thread(() => x = 42);
                t.Start(); t.Join();
                return "thread set " + x;
            }, s => s == "thread set 42");
            T(G, "G3", "1 worker thread allocating 20000 x while this thread runs 4 x GC.Collect()", () => Workers(1, 20000, 4));
            T(G, "G4", "2 workers x 20000 allocations + 8 x GC.Collect() (the shape of Hello's M10)", () => Workers(2, 20000, 8));
            T(G, "G5", "3 rounds of 3 workers x 10000 allocations + 6 x GC.Collect(), threads exiting during collections", () =>
            {
                string r = "";
                for (int i = 0; i < 3; i++) r += Workers(3, 10000, 6) + (i < 2 ? " | " : "");
                return r;
            });
        }

        static string Workers(int n, int iterations, int collects)
        {
            var sums = new long[n];
            var threads = new Thread[n];
            var sw = Stopwatch.StartNew();
            int g2 = GC.CollectionCount(2), g0 = GC.CollectionCount(0);
            for (int t = 0; t < n; t++)
            {
                int id = t;
                threads[t] = new Thread(() => sums[id] = Allocate(iterations));
                threads[t].Start();
            }
            for (int i = 0; i < collects; i++)
            {
                GC.Collect();
                Thread.Sleep(2);
            }
            foreach (var th in threads)
                th.Join();
            long want = Allocate(iterations);
            bool same = true;
            foreach (var s in sums) same &= s == want;
            if (!same)
                failures++;
            return (same ? "sums match" : "SUMS DIFFER") + ", full GCs " + (GC.CollectionCount(2) - g2) + ", minor " + (GC.CollectionCount(0) - g0) + ", " + sw.ElapsedMilliseconds + " ms";
        }

        static long Allocate(int iterations)
        {
            long s = 0;
            var keep = new List<object>();
            for (int i = 0; i < iterations; i++)
            {
                var a = new byte[(i % 64) * 16 + 16];
                a[a.Length - 1] = (byte)i;
                string str = "lamb" + i;
                s += a.Length + str.Length + a[a.Length - 1];
                if ((i & 255) == 0)
                {
                    keep.Add(new int[256]);
                    if (keep.Count > 16) keep.RemoveAt(0);
                }
            }
            return s + keep.Count;
        }
    }
}
