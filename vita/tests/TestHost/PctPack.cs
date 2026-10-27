using System.Runtime.CompilerServices;
using Shepherd.Assets;
namespace Shepherd.TestHost;

/// <summary>Kit #7 tests track: the pack read-ahead protocol (Shepherd.Assets VitaPack: the reader, the worker thread, the bounded wait) under
/// the PCT scheduler, many seeds in one process (vita/notes/kit7-tests.md). Each seed: a fresh PackReader on a real pack with read-ahead on,
/// a seeded access pattern (sequential runs through the payload with random record sizes, jumps back and forth, re-reads, records across
/// two windows), every returned byte compared with a plain read of the file, the worker stopped at the end (Dispose). Required: 0
/// differing bytes, no deadlock, no stall, for every seed; reported: how many distinct schedules the seeds produced and how often each
/// protocol path ran (window hits, read-ahead hits, waits, timeouts chosen by the scheduler, the reader reading a window itself).</summary>
public static class PctPack
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Bytes")] static extern ReadOnlySpan<byte> Bytes(PackReader r, long offset, int len);

    public static int Run(string[] args)
    {
        string path = args[1]; int seeds = int.Parse(args[2]); int first = args.Length > 3 ? int.Parse(args[3]) : 1; int depth = args.Length > 4 ? int.Parse(args[4]) : 3;
        int accesses = int.TryParse(Environment.GetEnvironmentVariable("PCT_PACK_ACCESSES"), out var ac) ? ac : 120;
        PackReader.ReadAhead = true;
        using var raw = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = RandomAccess.GetLength(raw);
        var traces = new HashSet<ulong>(); int fails = 0; long bad = 0, hits = 0, waits = 0, timeouts = 0, aheadReads = 0, unused = 0, straddles = 0, steps = 0, chosenTimeouts = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var perSeed = Environment.GetEnvironmentVariable("PCT_PACK_TRACES") is { Length: > 0 } tf ? new StreamWriter(tf, false) : null;   // (seed, schedule hash, ...): two runs of the same seeds must agree line by line
        for (int seed = first; seed < first + seeds; seed++)
        {
            var pct = new Pct(seed, depth, int.TryParse(Environment.GetEnvironmentVariable("PCT_PACK_K"), out var kk) ? kk : accesses / 2);   // (k ~ the steps a seed takes: ~0.5 per access)
            if (Environment.GetEnvironmentVariable("PCT_PACK_TRACE_DIR") is { Length: > 0 }) pct.KeepTrace = true;
            Sched.Cur = pct;
            long seedBad = 0;
            var r = PackReader.Open(path);
            var rng = new ShepherdOracle.Rng((ulong)seed * 7919UL + 17);
            long po = r.PayloadOffset, ps = r.PayloadSize, pos = po + (long)(rng.Next() % (ulong)Math.Max(1, ps / 2));
            var expect = new byte[PackReader.WindowSize];
            for (int i = 0; i < accesses; i++)
            {
                int kind = rng.Int(100);
                if (kind < 8) pos = po + (long)(rng.Next() % (ulong)ps);                       // a jump
                else if (kind < 14) pos = Math.Max(po, pos - rng.Int(3 * PackReader.WindowSize)); // back (re-reads, evicted windows)
                int len = 1 + (rng.Chance(20) ? rng.Int(PackReader.WindowSize / 2) : rng.Int(4096));
                if (rng.Chance(10)) { long w = (pos - po) / PackReader.WindowSize; pos = po + (w + 1) * PackReader.WindowSize - rng.Int(len); }   // across two windows
                if (pos < po) pos = po;
                if (pos + len > po + ps) { pos = po; }
                var got = Bytes(r, pos, len);
                RandomAccess.Read(raw, expect.AsSpan(0, len), pos);
                if (!got.SequenceEqual(expect.AsSpan(0, len))) { seedBad++; if (seedBad == 1) Console.Error.WriteLine($"[pct-pack] seed {seed}: bytes differ at access {i} (offset {pos}, {len} B)"); }
                pos += len;
            }
            hits += r.AheadHits; waits += r.AheadWaits; timeouts += r.AheadTimeouts; aheadReads += r.AheadReads; unused += r.AheadUnused; straddles += r.Straddles;
            r.Dispose();
            pct.Drain();
            Sched.Cur = null;
            steps += pct.Steps; chosenTimeouts += pct.Timeouts; bad += seedBad;
            traces.Add(pct.TraceHash);
            if (pct.KeepTrace) File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("PCT_PACK_TRACE_DIR"), $"seed{seed}.trace"), pct.Trace);
            perSeed?.WriteLine($"{seed}\t{pct.TraceHash:x16}\t{pct.Steps}\t{pct.Switches}\t{pct.Timeouts}\t{seedBad}\t{pct.Failure}");
            if (pct.Failure != null || seedBad > 0) { fails++; Console.Error.WriteLine($"[pct-pack] FAIL seed {seed}: {pct.Failure ?? ""} {(seedBad > 0 ? seedBad + " accesses with wrong bytes" : "")}"); }
        }
        Console.WriteLine($"[pct-pack] {seeds} seeds ({first}..{first + seeds - 1}), d {depth}, {accesses} accesses each, pack {Path.GetFileName(path)} ({fileLen / 1048576.0:F1} MiB): " +
            $"{fails} failing seeds, {bad} accesses with wrong bytes; {traces.Count} distinct schedules; {steps} scheduling steps, {chosenTimeouts} timeouts chosen; " +
            $"read-ahead: {aheadReads} windows read by the worker, {hits} taken by the reader, {waits} waits for a window being read, {timeouts} bounded waits that gave up (the reader read it itself), {unused} read ahead and unused, {straddles} records across two windows; {sw.Elapsed.TotalSeconds:F1} s");
        return fails == 0 ? 0 : 1;
    }
}
