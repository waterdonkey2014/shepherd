// SHEPHERD-VITA V5: .NET 8 Mono hello world for the PS Vita (interpreter + static AOT, cooperative GC suspend).
// Pure synthetic test code. Every step reports through the host (shepherd_hello_log -> ux0:data/shepherd/hello.txt,
// one durable line per call), so a crash or hang still shows the last step that worked.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShepherdVita
{
    public struct Pair<T>
    {
        public T A, B;
        public Pair(T a, T b) { A = a; B = b; }
    }

    // M4b: an all-float struct (the shape of Vector2/Color): a VFP "HFA" for P/Invoke, but core registers in managed calls
    public struct V2
    {
        public float X, Y;
        public V2(float x, float y) { X = x; Y = y; }
    }

    // M4b: gsharedvt transitions built at RUN TIME. These are generic VIRTUAL methods called through the abstract base,
    // so the AOT compiler emits only their shared bodies (T_REF and T_GSHAREDVT; build.sh checks Hello.dll.s for that).
    // Main (AOT, normal float/double/struct signatures) reaches the gsharedvt bodies through gsharedvt-IN transitions
    // (T passed by address), and the bodies call the AOT lambdas through gsharedvt-OUT transitions. Both are described
    // by mono_arch_get_gsharedvt_call_info and executed by mono_arm_start_gsharedvt_call: the armhf s-register path.
    public abstract class GsvtProbe
    {
        public abstract T Pick<T>(T a, T b, bool second);
        public abstract double Mix<T>(T a, double d, float c, Func<T, double> conv);
        public abstract T Combine<T>(T a, T b, Func<T, T, T> f);
    }

    sealed class AotGsvtProbe : GsvtProbe
    {
        public override T Pick<T>(T a, T b, bool second) => second ? b : a;
        public override double Mix<T>(T a, double d, float c, Func<T, double> conv) => conv(a) * 100 + d * 10 + c;
        public override T Combine<T>(T a, T b, Func<T, T, T> f) => f(a, b);
    }

    public static unsafe class Hello
    {
        // ---- host functions, linked statically into the eboot (DllImport("__Internal") -> direct call in AOT code) ----
        [DllImport("__Internal", EntryPoint = "shepherd_hello_log")]
        static extern void HostLog(int step, [MarshalAs(UnmanagedType.LPUTF8Str)] string msg);

        [DllImport("__Internal", EntryPoint = "shepherd_hello_add")]
        static extern int NativeAdd(int a, int b);

        // AAPCS-VFP: a->s0, b->d1, c->r0, d->s1 (back-filled), result in d0
        [DllImport("__Internal", EntryPoint = "shepherd_hello_fmix")]
        static extern double NativeFMix(float a, double b, int c, float d);

        // native code calls back into managed code through an [UnmanagedCallersOnly] function pointer
        [DllImport("__Internal", EntryPoint = "shepherd_hello_call_back")]
        static extern int NativeCallBack(delegate* unmanaged<int, int, int> cb, int a, int b);

        [UnmanagedCallersOnly]
        static int ManagedMul(int a, int b) => a * b;

        static int failures;

        // set by vita/hello/mac-check/run.sh; never set on the device
        static readonly bool OnMac = Environment.GetEnvironmentVariable("SHEPHERD_HOST") == "mac";

        static void Check(int step, bool ok, string what)
        {
            if (!ok)
                failures++;
            HostLog(step, (ok ? "ok   " : "FAIL ") + what);
        }

        // keeps the JIT/AOT compiler from folding the values (explicit null/div checks must really run)
        [MethodImpl(MethodImplOptions.NoInlining)]
        static T Opaque<T>(T x) => x;

        static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
        static Pair<T> Swap<T>(Pair<T> p) => new Pair<T>(p.B, p.A);

        static void Step(int step, string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failures++;
                HostLog(step, "FAIL " + name + ": " + e.GetType().FullName + ": " + e.Message);
            }
        }

        public static int Main(string[] args)
        {
            HostLog(1, "managed Main entered, args=" + args.Length);

            Step(2, "console", () =>
            {
                Console.WriteLine("Hello from Shepherd on the PS Vita (.NET 8 Mono, interpreter + static AOT)");
                Console.Out.Flush();
                HostLog(2, "ok   Console.WriteLine returned");
            });

            Step(3, "environment", () =>
            {
                // the device: the 32-bit target with the 3 user cores (mono_cpu_count); mac-check/run.sh sets
                // SHEPHERD_HOST=mac, where the same check expects a 64-bit process
                int cpus = Environment.ProcessorCount;
                bool ok = OnMac ? IntPtr.Size == 8 && cpus >= 1 : IntPtr.Size == 4 && cpus == 3;
                Check(3, ok, "Environment.ProcessorCount=" + cpus + " IntPtr.Size=" + IntPtr.Size + " Version=" + Environment.Version +
                      (OnMac ? " (SHEPHERD_HOST=mac: expects IntPtr.Size=8)" : " (device: expects IntPtr.Size=4, ProcessorCount=3)"));
            });

            Step(4, "generics", () =>
            {
                int g = Max(3, 7) + Max("lamb", "cult").Length + (int)Max(1.5, 2.5) + (int)Swap(new Pair<long>(1, 2)).A;
                var list = new List<string> { "lamb", "cult", "shepherd" };
                list.Sort(StringComparer.Ordinal);
                var dict = new Dictionary<string, int>();
                foreach (var s in list)
                    dict[s] = s.Length;
                Check(4, g == 15 && list[0] == "cult" && dict["shepherd"] == 8,
                      "generic methods (ref/int/double/struct), List<T>.Sort, Dictionary<K,V>: g=" + g);
            });

            Step(5, "exceptions", () =>
            {
                int fin = 0;
                string caught = null;
                try
                {
                    try { throw new InvalidOperationException("thrown"); }
                    finally { fin++; }
                }
                catch (InvalidOperationException e) { caught = e.Message; }
                finally { fin++; }
                Check(5, caught == "thrown" && fin == 2, "try/catch/finally (nested finally, rethrow-free unwind)");

                // No SIGSEGV/SIGFPE handlers exist on the Vita: these must come from explicit checks.
                bool nre = false, dbz = false;
                object o = Opaque<object>(null);
                try { o.GetHashCode(); } catch (NullReferenceException) { nre = true; }
                int zero = Opaque(0);
                try { HostLog(5, "unexpected " + (10 / zero)); } catch (DivideByZeroException) { dbz = true; }
                Check(5, nre && dbz, "NullReferenceException + DivideByZeroException from explicit checks");
            });

            Step(6, "pinvoke", () =>
            {
                int add = NativeAdd(40, 2);
                double mix = NativeFMix(1.5f, 2.25, 3, 4.5f);   // host returns a*b + c + d = 3.375 + 3 + 4.5
                int cb = NativeCallBack(&ManagedMul, 6, 7);
                Check(6, add == 42 && Math.Abs(mix - 10.875) < 1e-9 && cb == 42,
                      "DllImport(__Internal): add=" + add + " fmix=" + mix + " native->managed callback=" + cb);
            });

            Step(7, "interpreter", () =>
            {
                const int n = 1000;
                long got = ShepherdVita.Interp.InterpWork.Run(n);
                long want = 0;
                for (int i = 0; i < n; i++)
                    want += i ^ (i << 1);
                want += (n - 1) * 3 + "interp".Length + 42 + 5 + 4;
                Check(7, got == want, "interpreted assembly (no AOT image) <-> AOT CoreLib: " + got + " == " + want);
            });

            Step(8, "time", () =>
            {
                var sw = Stopwatch.StartNew();
                Thread.Sleep(50);
                long slept = sw.ElapsedMilliseconds;
                var gate = new object();
                long waited;
                lock (gate)
                {
                    sw.Restart();
                    Monitor.Wait(gate, 50);   // timed condition wait: checks the realtime-vs-monotonic clock fix
                    waited = sw.ElapsedMilliseconds;
                }
                var mre = new ManualResetEventSlim(false);
                sw.Restart();
                mre.Wait(30);
                long mwait = sw.ElapsedMilliseconds;
                DateTime now = DateTime.UtcNow;
                Check(8, slept >= 45 && slept < 1000 && waited >= 45 && waited < 1000 && mwait >= 25 && mwait < 1000 && now.Year >= 2020,
                      "Thread.Sleep(50)=" + slept + "ms Monitor.Wait(50)=" + waited + "ms MRES.Wait(30)=" + mwait + "ms UtcNow=" + now.ToString("o"));
            });

            Step(9, "file", () =>
            {
                // relative to the process cwd (app0:) -> app0:/managed/hello-data.txt
                string text = File.ReadAllText("managed/hello-data.txt");
                Check(9, text.StartsWith("Shepherd"), "File.ReadAllText(managed/hello-data.txt) " + text.Length + " chars: " + text.Trim());
                try
                {
                    File.WriteAllText("ux0:data/shepherd/hello-managed.txt", "written by managed code at " + DateTime.UtcNow.ToString("o") + "\n");
                    HostLog(9, "ok   File.WriteAllText(ux0:data/shepherd/hello-managed.txt) (optional)");
                }
                catch (Exception e)
                {
                    HostLog(9, "info File.WriteAllText (optional) failed: " + e.GetType().Name + ": " + e.Message);
                }
            });

            Step(9, "file identity", () => FileIdentity(9));

            Step(10, "threads+gc", () => ThreadsAndGc(10, iterations: 20000, collects: 8));

            int stress = 0;
            foreach (var a in args)
                if (a.StartsWith("--stress-seconds="))
                    stress = int.Parse(a.Substring("--stress-seconds=".Length));
            if (stress > 0)
                Step(11, "gc-stress", () => GcStress(11, stress));

            // runs last: if a run-time gsharedvt transition crashes natively, every other check has already reported
            Step(4, "gsharedvt", () => GsharedvtTransitions(4));

            GC.Collect();
            HostLog(98, "GC counts gen0=" + GC.CollectionCount(0) + " gen1=" + GC.CollectionCount(1) + " gen2=" + GC.CollectionCount(2) +
                    " total=" + (GC.GetTotalMemory(false) / 1024) + " KiB");
            HostLog(99, failures == 0 ? "PASS all managed checks" : "FAIL " + failures + " managed check(s)");
            Console.Out.Flush();
            return failures == 0 ? 0 : 1;
        }

        // M9b: CoreLib decides "same file?" from FileStatus Dev+Ino. The Vita has no inodes; the shim synthesizes them
        // from the case-folded path. With Ino=0 for every file, File.Replace always threw "same file" and File.Move
        // without overwrite silently replaced an existing target. Device: ux0:data/shepherd/; Mac: the scratch cwd.
        static void FileIdentity(int step)
        {
            string dir = OnMac ? "" : "ux0:data/shepherd/";
            string a = dir + "fsid-a.txt", b = dir + "fsid-b.txt", c = dir + "fsid-c.txt", bak = dir + "fsid-b.bak";
            string upper = dir + "FSID-A.TXT", tmp = dir + "fsid-tmp.txt";
            string[] all = { a, b, c, bak, upper, tmp };
            foreach (var f in all)
                File.Delete(f);
            try
            {
                File.WriteAllText(a, "A");
                File.WriteAllText(b, "B");
                File.WriteAllText(c, "C");

                // File.Replace(a, b, null): b gets a's content, a is gone
                string replaced = "ok";
                try { File.Replace(a, b, null); } catch (Exception e) { replaced = e.GetType().Name + ": " + e.Message; }
                bool replaceOk = replaced == "ok" && !File.Exists(a) && File.ReadAllText(b) == "A";

                // File.Replace with a backup (no hard links on the Vita: CoreLib falls back to a copy): b <- c, old b -> bak
                string withBackup = "ok";
                try { File.Replace(c, b, bak); } catch (Exception e) { withBackup = e.GetType().Name + ": " + e.Message; }
                bool backupOk = withBackup == "ok" && !File.Exists(c) && File.ReadAllText(b) == "C" && File.ReadAllText(bak) == "A";

                // File.Move(x, existing) without overwrite must throw IOException and leave both files alone
                File.WriteAllText(a, "A2");
                string moved = "no exception";
                try { File.Move(a, b); } catch (IOException) { moved = "IOException"; } catch (Exception e) { moved = e.GetType().Name + ": " + e.Message; }
                bool moveOk = moved == "IOException" && File.ReadAllText(a) == "A2" && File.ReadAllText(b) == "C";

                // DeleteOnClose + FileShare.None + OpenOrCreate makes FileStream compare FStat(fd) with Stat(path) and
                // re-open until they match: a mismatch is an endless loop here (the log would stop at M9)
                using (var fs = new FileStream(tmp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
                    fs.WriteByte(42);
                HostLog(step, "info FileStream(DeleteOnClose) opened (FStat == Stat); file removed on close: " + !File.Exists(tmp));

                Check(step, replaceOk && backupOk && moveOk,
                      "[M9b] file identity: File.Replace(a, b, null): " + replaced + "; with backup: " + withBackup +
                      "; File.Move(a, existing b) without overwrite: " + moved + " (files intact: " + moveOk + ")");

                // a change of case only renames the SAME file: it must never lose it (the shim's Rename used to remove the
                // "existing target" first, which is the source itself on a case-insensitive volume)
                string caseMove = "ok";
                try { File.Move(a, upper); } catch (Exception e) { caseMove = e.GetType().Name + ": " + e.Message; }
                bool kept = File.Exists(upper) && File.ReadAllText(upper) == "A2";
                Check(step, kept, "[M9b] case-only File.Move(fsid-a.txt, FSID-A.TXT): " + caseMove + ", file kept: " + kept);
            }
            finally
            {
                foreach (var f in all)
                    try { File.Delete(f); } catch (Exception) { }
            }
        }

        // M4b (logged as "M4 ... [M4b]"): see GsvtProbe. Upstream armhf copied VFP arguments as whole d-registers, so
        // b of (float a, float b) read as a, and a float moving between s-registers clobbered its neighbour.
        static void GsharedvtTransitions(int step)
        {
            GsvtProbe p = Opaque<GsvtProbe>(new AotGsvtProbe());
            float f1 = p.Pick(1.5f, 2.5f, true);                 // in: a s0, b s1 (odd) -> T&, T&; ret T -> s0
            float f0 = p.Pick(1.5f, 2.5f, false);
            double d1 = p.Pick(1.25, 2.5, true);                 // doubles: d0, d1
            double mf = p.Mix(1.5f, 2.25, 0.25f, x => x);        // in: a s0 -> T&, d d1 -> d0, c back-filled s1 -> s2; out: T& -> s0
            double md = p.Mix(2.0, 2.25, 0.25f, x => x);         // in: a d0 -> T&, d d1 -> d0, c s4 -> s2
            float fc = p.Combine(1f, 2f, (x, y) => x * 10 + y);  // in: s0, s1 -> T&, T&; out (delegate invoke): T&, T& -> s0, s1
            V2 v = p.Pick(new V2(1, 2), new V2(3, 4), true);     // all-float struct: core registers + stack, ret by address
            V2 vc = p.Combine(new V2(1, 2), new V2(3, 4), (u, w) => new V2(u.X + w.X, u.Y * w.Y));
            var list = new List<V2> { new V2(5, 6), v };
            bool eq = EqualityComparer<V2>.Default.Equals(list[1], new V2(3, 4)) && !EqualityComparer<V2>.Default.Equals(list[0], v);
            bool ok = f1 == 2.5f && f0 == 1.5f && d1 == 2.5 && mf == 172.75 && md == 222.75 && fc == 12f &&
                      v.X == 3 && v.Y == 4 && vc.X == 4 && vc.Y == 8 && eq;
            Check(step, ok, "[M4b] gsharedvt run-time transitions (float/double/back-filled s-reg/all-float struct): pick=" + f1 + "/" + f0 + "/" + d1 +
                  " mix=" + mf + "/" + md + " combine=" + fc + " v2=(" + v.X + "," + v.Y + ") v2comb=(" + vc.X + "," + vc.Y + ") eq=" + eq);
        }

        // Two threads allocate (small objects, strings, arrays that survive a while) while this thread forces
        // full collections: every GC has to stop both workers at a safepoint (cooperative suspend).
        static void ThreadsAndGc(int step, int iterations, int collects)
        {
            var sums = new long[2];
            var threads = new Thread[2];
            int startGen2 = GC.CollectionCount(2);
            for (int t = 0; t < 2; t++)
            {
                int id = t;
                threads[t] = new Thread(() => sums[id] = Allocate(id, iterations));
                threads[t].Start();
            }
            for (int i = 0; i < collects; i++)
            {
                GC.Collect();
                Thread.Sleep(2);
            }
            foreach (var th in threads)
                th.Join();
            long want = Allocate(-1, iterations);   // same work on this thread, no GC racing
            int full = GC.CollectionCount(2) - startGen2;
            Check(step, sums[0] == want && sums[1] == want && full >= collects,
                  "2 threads x " + iterations + " allocations while " + collects + " x GC.Collect(): sums " + sums[0] + "/" + sums[1] +
                  " == " + want + ", full GCs=" + full);
        }

        static long Allocate(int id, int iterations)
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
                    if (keep.Count > 16)
                        keep.RemoveAt(0);
                }
            }
            return s + keep.Count;
        }

        static void GcStress(int step, int seconds)
        {
            var sw = Stopwatch.StartNew();
            int rounds = 0, failuresBefore = failures;
            long nextReport = 30_000;
            while (sw.ElapsedMilliseconds < seconds * 1000L)
            {
                ThreadsAndGcQuiet(5000, 4);
                rounds++;
                if (sw.ElapsedMilliseconds >= nextReport)
                {
                    HostLog(step, "info stress " + (sw.ElapsedMilliseconds / 1000) + "s rounds=" + rounds + " gen2=" + GC.CollectionCount(2) +
                            " heap=" + (GC.GetTotalMemory(false) / 1024) + " KiB");
                    nextReport += 30_000;
                }
            }
            Check(step, failures == failuresBefore, "GC stress " + seconds + "s: " + rounds + " rounds, no stop-the-world hang");
        }

        static void ThreadsAndGcQuiet(int iterations, int collects)
        {
            var sums = new long[2];
            var threads = new Thread[2];
            for (int t = 0; t < 2; t++)
            {
                int id = t;
                threads[t] = new Thread(() => sums[id] = Allocate(id, iterations));
                threads[t].Start();
            }
            for (int i = 0; i < collects; i++)
                GC.Collect();
            foreach (var th in threads)
                th.Join();
            long want = Allocate(-1, iterations);
            if (sums[0] != want || sums[1] != want)
            {
                failures++;
                HostLog(11, "FAIL stress round: sums " + sums[0] + "/" + sums[1] + " != " + want);
            }
        }
    }
}
