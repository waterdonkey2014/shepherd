using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
namespace Shepherd.TestHost;

/// <summary>Kit #7 tests track: the Mac test host (vita/notes/kit7-tests.md). Modes:
///   boot ...                       the engine's own Main, with SHEPHERD_TESTHOST_PCT=&lt;seed&gt;:&lt;depth&gt;:&lt;k&gt; installing the PCT scheduler
///                                  (Pct.cs) first; the summary goes to stderr and SHEPHERD_TESTHOST_PCT_OUT
///   probe &lt;suites|all&gt; &lt;seed&gt; &lt;n&gt; &lt;out dir&gt; [first]   the shared probe suites (vita/tests/probes) on Shepherd's engine:
///                                  &lt;out dir&gt;/&lt;suite&gt;.tsv, the same format the Unity oracle players write
///   list                           the suite names
///   pct-pack &lt;pack&gt; &lt;seeds&gt; [first seed] [depth]   the read-ahead protocol under PCT (Shepherd.Assets VitaPack) on a real pack:
///                                  a seeded access pattern, every byte checked against plain reads, every seed's schedule hashed
///   mutate &lt;icall&gt;=&lt;op&gt; probe ...   the probe mode with one icall mutated (Mutate.cs: the drafted probes' mutation check)
///   powercut ...                   the crash-injected cache tests (PowerCut.cs)
///   k9wheel                        kit #9 engine review fixes (K9Wheel.cs): the timer wheel's heap compaction and the WHEEL / FIND check modes
/// Engine init for the probe modes (no game boot): the engine's assembly resolver (runtime/ManagedVita or Managed under SHEPHERD_ROOT),
/// engine.log, the icall table -- what Boot.Run does before its modes.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "help";
        switch (mode)
        {
            case "boot": return Boot(args);
            case "probe": InitEngine("probe"); return ProbeMode(args, 1);
            case "list": InitEngine("list"); return ListMode();
            case "mutate": InitEngine("mutate"); Mutate.Install(args[1]); return ProbeMode(args, 3);
            case "pct-pack": return PctPack.Run(args);
            case "powercut": InitEngine("powercut"); return PowerCut.Run(args);
            case "scene": InitEngine("scene"); return SceneMode.Run(args);
            case "k9wheel": InitEngine("k9wheel"); return K9Wheel.Run(args);   // kit #9 engine review fixes: the wheel compaction + check modes
            default:
                Console.Error.WriteLine("usage: Shepherd.TestHost boot ... | probe <suites|all> <seed> <n> <out> [first] | list | mutate <icall>=<op> probe ... | pct-pack <pack> <seeds> [first] [depth] | powercut <out>");
                return 2;
        }
    }

    static int Boot(string[] args)
    {
        Pct pct = null;
        if (Environment.GetEnvironmentVariable("SHEPHERD_TESTHOST_PCT") is { Length: > 0 } p)
        {
            var a = p.Split(':'); int seed = int.Parse(a[0]); int d = a.Length > 1 ? int.Parse(a[1]) : 3; long k = a.Length > 2 ? long.Parse(a[2]) : 20000;
            pct = new Pct(seed, d, k) { KeepTrace = Environment.GetEnvironmentVariable("SHEPHERD_TESTHOST_PCT_TRACE") == "1" };
            Shepherd.Assets.Sched.Cur = pct;
        }
        int rc = Shepherd.Engine.Program.Main(args);
        if (pct != null)
        {
            var s = pct.Summary(); Console.Error.WriteLine(s);
            if (Environment.GetEnvironmentVariable("SHEPHERD_TESTHOST_PCT_OUT") is { Length: > 0 } o)
            {
                File.WriteAllText(o, s + "\n");
                if (pct.KeepTrace) File.WriteAllLines(o + ".trace", pct.Trace);
            }
            if (pct.Failure != null && rc == 0) rc = 3;
        }
        return rc;
    }

    static bool _init;
    /// <summary>The engine's runtime setup without the game boot: assembly resolution exactly as Shepherd.Engine.Program.Main does it, the
    /// log, the icall table. Must run before anything touches a UnityEngine type (so the probe code lives in other methods).</summary>
    public static void InitEngine(string what)
    {
        if (_init) return; _init = true;
        var managed = Shepherd.Engine.Program.RuntimeManaged; var game = Shepherd.Engine.Program.GameManaged;
        if (!Shepherd.Engine.Profile.Check(managed)) throw new InvalidOperationException("profile check failed");
        static bool IsBcl(string n) => n is "mscorlib" or "netstandard" || n.StartsWith("System") || n.StartsWith("Microsoft.") || n.StartsWith("Mono.");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (IsBcl(name.Name)) return null;
            foreach (var dir in new[] { managed, game })
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return ctx.LoadFromAssemblyPath(path);
            }
            return null;
        };
        Directory.CreateDirectory(Shepherd.Engine.Program.LogDir);
        Shepherd.Engine.Log.Open(Path.Combine(Shepherd.Engine.Program.LogDir, "engine.log"));
        Shepherd.Engine.IcallRegistry.Init(Path.Combine(managed, "icalls.json"));
        // the game's global settings as the real boot has them (TagManager: layers, tags, sorting layers; script orders): the asset layer on
        // the owner's install, read only (PROBE_LOAD_GAME=0: without -- the first drafted-probe round ran so, and LayerMask / tags differed
        // from Unity for that reason alone)
        if (Environment.GetEnvironmentVariable("PROBE_LOAD_GAME") != "0") Shepherd.Engine.Assets.Loader.Init();
        Shepherd.Engine.Log.Info($"[testhost] {what}: engine initialised (managed {managed}; game settings {(Environment.GetEnvironmentVariable("PROBE_LOAD_GAME") != "0" ? "loaded" : "not loaded")})");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ListMode() { foreach (var n in ShepherdOracle.Suites.Names()) Console.WriteLine(n); return 0; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ProbeMode(string[] args, int at)
    {
        // probe <suites|all> <seed> <n> <out dir> [first]
        string suites = args[at + 0]; ulong seed = ulong.Parse(args[at + 1]); int n = int.Parse(args[at + 2]); string outDir = args[at + 3]; int first = args.Length > at + 4 ? int.Parse(args[at + 4]) : 0;
        Directory.CreateDirectory(outDir);
        var names = suites == "all" ? ShepherdOracle.Suites.Names().ToList() : suites.Split(',').ToList();
        var hitsBefore = Shepherd.Hooks.Icalls.HitCounts();
        // a hang watchdog: the same case and no line written for 20 s -> name it and stop (a probe that loops forever is a finding)
        new Thread(() =>
        {
            long last = -1; int lastCase = -1; int still = 0; int limit = int.TryParse(Environment.GetEnvironmentVariable("PROBE_HANG_S"), out var hs) && hs > 0 ? hs : 20;
            while (true)
            {
                Thread.Sleep(1000);
                var o = ShepherdOracle.Suites.Current; if (o == null) continue;
                long wr = Interlocked.Read(ref o.Writes);
                if (wr == last && o.Case == lastCase) { if (++still >= limit) { Console.Error.WriteLine($"HANG: suite {ShepherdOracle.Suites.CurrentSuite} case {o.Case} after label '{o.LastLabel}' (no output for {limit} s)"); Console.Out.Flush(); Environment.Exit(4); } }
                else { still = 0; last = wr; lastCase = o.Case; }
            }
        }) { IsBackground = true, Name = "probe-watchdog" }.Start();
        foreach (var s in names)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var w = new StreamWriter(Path.Combine(outDir, s + ".tsv"), false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
            {
                var h = ShepherdOracle.Suites.Run(s, seed, first, n, w, m => Console.Error.WriteLine(m));
                Console.WriteLine($"{s}\t{h:x16}\t{sw.ElapsedMilliseconds} ms");
            }
            // icall coverage of this suite (the drafted probes' filter: which engine icalls each suite reaches)
            var hits = Shepherd.Hooks.Icalls.HitCounts();
            using (var cw = new StreamWriter(Path.Combine(outDir, s + ".icalls"), false))
                for (int i = 0; i < hits.Length; i++) if (hits[i] > hitsBefore[i]) cw.WriteLine($"{Shepherd.Hooks.Icalls.Names[i]}\t{hits[i] - hitsBefore[i]}\t{(Shepherd.Hooks.Icalls.Table[i] != null ? "impl" : "missing")}");
            hitsBefore = hits;
        }
        Shepherd.Engine.Log.Info($"[testhost] probe done: {Shepherd.Engine.Log.Errors} errors, {Shepherd.Engine.Log.Exceptions} exceptions");
        return 0;
    }
}
