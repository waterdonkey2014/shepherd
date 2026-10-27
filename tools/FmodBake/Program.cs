using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Shepherd.FmodBake;

/// <summary>
/// FmodBake: offline, silent bake of CotL's FMOD Studio banks into the Vita audio set (vita/notes/sound-bake.md).
///   probe                         key check (length only), FMOD up, banks, counts
///   catalog                       + writes runtime/audiobake/catalog.json (metadata of every event/bus/snapshot)
///   bake &lt;set&gt; [options]           renders + encodes runtime/audiobake/&lt;set&gt;/ (manifest.json + takes)
///        --events &lt;file&gt;           event paths to bake (one per line); default = every event
///        --match &lt;regex&gt;           only paths matching
///        --limit N                 first N events (after filtering)
///   verify &lt;set&gt;                  re-reads every take of a set and checks it against the manifest (objective checks)
/// </summary>
static class Program
{
    public static string App = Environment.GetEnvironmentVariable("SHEPHERD_GAME_APP")
        ?? "$SHEPHERD_GAME_DIR/Cult Of The Lamb.app";
    public static string Data => Path.Combine(App, "Contents/Resources/Data");
    public static string Managed => Path.Combine(Data, "Managed");
    public static string Streaming => Path.Combine(Data, "StreamingAssets");
    public static string FmodLib => Path.Combine(App, "Contents/PlugIns/fmodstudio.bundle/Contents/MacOS/fmodstudio");
    public static string Repo = FindRepo();
    public static string OutRoot => Path.Combine(Repo, "runtime", "audiobake");

    static int Main(string[] args)
    {
        // SILENCE RULE, belt and braces: nothing in this process may open an audio device. FMOD is forced to NOSOUND_NRT in
        // Session; SDL is never loaded, but if anything ever did load it, it would get the dummy driver.
        Environment.SetEnvironmentVariable("SDL_AUDIO_DRIVER", "dummy");
        AssemblyLoadContext.Default.Resolving += (ctx, an) =>
        {
            string p = Path.Combine(Managed, an.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        try { return Run(args); }
        catch (Exception e) { Console.Error.WriteLine("ERROR: " + e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        NativeLibrary.SetDllImportResolver(typeof(FMOD.VERSION).Assembly, (name, asm, path) =>
            name == FMOD.VERSION.dll ? NativeLibrary.Load(FmodLib) : IntPtr.Zero);
        string cmd = args.Length > 0 ? args[0] : "probe";
        switch (cmd)
        {
            case "probe": return Probe(writeCatalog: false);
            case "catalog": return Probe(writeCatalog: true);
            case "bake": return Baker.Run(args.Skip(1).ToArray());
            case "verify": return Verifier.Run(args.Skip(1).ToArray());
            case "try": return Try.Run(args.Skip(1).ToArray());
            case "subset": return Subset.Run(args.Skip(1).ToArray());
            case "keycheck": return KeyCheck(args.Skip(1).ToArray());
            case "snapev": return SnapEv.Run(args.Skip(1).ToArray());
            case "loopdiag": return LoopDiag.Run(args.Skip(1).ToArray());
            case "leakhunt": return LeakHunt.Run(args.Skip(1).ToArray());
            case "statediff": return StateDiff.Run(args.Skip(1).ToArray());
            case "startdiag": return StartDiag.Run(args.Skip(1).ToArray());
            case "dspdiag": return DspDiag.Run(args.Skip(1).ToArray());
            case "spatialdiag": return SpatialDiag.Run(args.Skip(1).ToArray());
            case "spatialtime": return SpatialTime.Run(args.Skip(1).ToArray());
            case "metertest": return MeterTest.Run(args.Skip(1).ToArray());
            case "lockbisect": return LockBisect.Run(args.Skip(1).ToArray());
            default: Console.Error.WriteLine("usage: FmodBake probe|catalog|bake <set> [--events f] [--match re] [--limit n]|verify <set>"); return 2;
        }
    }

    public static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

    public static Session OpenSession(out List<EventInfo> events, out List<BusInfo> buses, out int vcas, bool quiet = false)
    {
        Action<string> log = quiet ? _ => { } : Log;
        var sw = Stopwatch.StartNew();
        var k = GameKey.Read(Data);
        log($"bank key: found via {k.How} in FMODStudioSettings ({k.ObjectBytes} B object), {k.Key.Length} chars (value never shown)");
        var s = Session.Open(App, k.Key, log);
        k = null; // drop our reference; FMOD holds its own copy
        log($"session up in {sw.ElapsedMilliseconds} ms; loading banks...");
        sw.Restart();
        s.LoadBanks(Streaming);
        log($"{s.Banks.Count} banks + sample data loaded in {sw.ElapsedMilliseconds} ms ({s.Updates} updates)");
        (events, buses, vcas) = Catalog.Enumerate(s);
        return s;
    }

    static int Probe(bool writeCatalog)
    {
        using var s = OpenSession(out var events, out var buses, out int vcas);
        var ev = events.Where(e => !e.Snapshot).ToList();
        var sn = events.Where(e => e.Snapshot).ToList();
        Log($"events {ev.Count}: oneshot {ev.Count(e => e.Oneshot)}, not {ev.Count(e => !e.Oneshot)}; 3D {ev.Count(e => e.Is3D)}; " +
            $"stream {ev.Count(e => e.IsStream)}; sustain {ev.Count(e => e.Sustain)}; with game params {ev.Count(e => e.Controllable.Any())}; " +
            $"with any params {ev.Count(e => e.Params.Count > 0)}; user props {ev.Count(e => e.UserProps.Count > 0)}");
        Log($"snapshots {sn.Count}; buses {buses.Count}; VCAs {vcas}");
        foreach (var (file, bank) in s.Banks)
        {
            if (bank.getStringCount(out int sc) != FMOD.RESULT.OK || sc == 0) continue;
            var pre = new Dictionary<string, int>();
            for (int i = 0; i < sc; i++)
                if (bank.getStringInfo(i, out _, out string sp) == FMOD.RESULT.OK && sp != null)
                { string k = sp.Contains(":/") ? sp[..(sp.IndexOf(":/") + 2)] : "?"; pre[k] = pre.GetValueOrDefault(k) + 1; }
            Log($"strings in {file}: {sc} [{string.Join(", ", pre.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}]");
        }
        var gp = ev.SelectMany(e => e.Params).Where(p => p.Global).Select(p => p.Name).Distinct().ToList();
        Log($"global params referenced: {gp.Count} [{string.Join(", ", gp.Take(20))}]");
        if (writeCatalog)
        {
            Directory.CreateDirectory(OutRoot);
            var o = new
            {
                events = events.Select(e => new
                {
                    e.Path, e.Guid, e.Bank, e.Snapshot, e.Oneshot, e.Is3D, e.IsStream, e.Sustain, e.LengthMs, e.MinDist, e.MaxDist,
                    @params = e.Params.Select(p => new { p.Name, p.Min, p.Max, p.Default, type = p.Type.ToString(), flags = p.Flags.ToString(), p.Labels }),
                    userProps = e.UserProps,
                }),
                buses = buses.Select(b => new { b.Path, b.Guid, b.Bank }),
                vcas,
            };
            string path = Path.Combine(OutRoot, "catalog.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Log($"wrote {path}");
        }
        return 0;
    }

    /// <summary>Hygiene check: scans files under the given roots for the bank key's bytes and reports only counts.</summary>
    static int KeyCheck(string[] roots)
    {
        var key = System.Text.Encoding.UTF8.GetBytes(GameKey.Read(Data).Key);
        int files = 0, hits = 0; long bytes = 0;
        foreach (var root in roots)
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (f.Contains("/obj/") || f.Contains("/bin/")) continue;
                byte[] d; try { d = File.ReadAllBytes(f); } catch { continue; }
                files++; bytes += d.Length;
                if (d.AsSpan().IndexOf(key) >= 0) { hits++; Console.WriteLine("KEY FOUND in " + f); }
            }
        Log($"keycheck: {files} files, {bytes / 1048576.0:F1} MiB scanned, {hits} contain the key");
        return hits == 0 ? 0 : 1;
    }

    static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "vita", "PLAN.md"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root (vita/PLAN.md) not found above " + AppContext.BaseDirectory);
    }
}
