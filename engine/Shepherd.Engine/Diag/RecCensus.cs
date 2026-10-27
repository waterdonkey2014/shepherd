using System.Reflection;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>kit6 mem, L4 measurement (diagnostic only, off unless SHEPHERD_RECCENSUS=&lt;frame&gt;[,...]; never in an eboot): the engine records'
/// property bags (NObj.Props) by record kind -- bags, keys, the dictionaries' capacity against what a bag sized to its keys would take, free
/// entries (removals), 32-bit bytes (Mono: Entry&lt;string,object&gt; 16 B, bucket 4 B, array header 16 B, the Dictionary object 48 B) -- and the
/// engine's own maps (Objects.ById / ByPtr, Loader.Live) with their capacity. It sizes L4's exact cuts.</summary>
[ProfileOnly]
public static class RecCensus
{
    static readonly int[] At = (Environment.GetEnvironmentVariable("SHEPHERD_RECCENSUS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.TryParse(x, out var v) ? v : -1).Where(v => v > 0).ToArray();
    const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void After(int frame)
    {
        if (At.Length == 0 || Array.IndexOf(At, frame) < 0) return;
        try { Run(frame); } catch (Exception e) { Log.Exception(e, "RecCensus"); }
    }

    static int Cap(object dict) => (dict.GetType().GetField("_entries", NP)?.GetValue(dict) as Array)?.Length ?? 0;
    static int Free(object dict) => dict.GetType().GetField("_freeCount", NP)?.GetValue(dict) is int f ? f : 0;
    static int Prime(int n) { if (n <= 0) return 0; var hh = typeof(Dictionary<,>).Assembly.GetType("System.Collections.HashHelpers"); return (int)hh.GetMethod("GetPrime", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { n }); }
    static long DictBytes(int cap, int entryBytes) => cap == 0 ? 48 : 48 + ((16 + (long)cap * entryBytes + 7) & ~7L) + ((16 + 4L * cap + 7) & ~7L);

    sealed class Row { public int Bags, Keys, Holes, Sized, Shaped; public long Bytes, BytesSized; public readonly Dictionary<int, int> CapHist = new(); }

    static void Run(int frame)
    {
        var rows = new Dictionary<string, Row>(); var primes = new Dictionary<int, int>();
        int P(int n) => primes.TryGetValue(n, out var p) ? p : primes[n] = Prime(n);
        foreach (var n in Objects.All)
        {
            if (n.Props == null) continue;
            string kind = (n.Managed?.GetType().Name ?? n.GetType().Name) + (n.Destroyed ? " (destroyed)" : "");
            if (!rows.TryGetValue(kind, out var r)) rows[kind] = r = new Row();
            int cnt = PropBag.Count(n.Props);
            if (n.Props is object[] arr) { r.Bags++; r.Keys += cnt; r.Shaped++; long b = (16 + 4L * arr.Length + 7) & ~7L; r.Bytes += b; r.BytesSized += b; r.CapHist[-1] = r.CapHist.GetValueOrDefault(-1) + 1; continue; }
            int cap = Cap(n.Props), free = Free(n.Props);
            r.Bags++; r.Keys += cnt; if (free > 0) r.Holes++; else r.Sized += cap == P(cnt) ? 1 : 0;
            r.Bytes += DictBytes(cap, 16); r.BytesSized += free > 0 ? DictBytes(cap, 16) : DictBytes(P(cnt), 16);
            r.CapHist[cap] = r.CapHist.GetValueOrDefault(cap) + 1;
        }
        long tb = rows.Values.Sum(x => x.Bytes), ts = rows.Values.Sum(x => x.BytesSized);
        Log.Info($"[reccensus] frame {frame}: {rows.Values.Sum(x => x.Bags):N0} property bags of live records, {rows.Values.Sum(x => x.Keys):N0} keys; dictionaries {tb / 1048576.0:F2} MiB (32-bit, entries + buckets + object; boxed values not counted), sized to their keys {ts / 1048576.0:F2} MiB (-{(tb - ts) / 1048576.0:F2}); bags with removals (left as they are): {rows.Values.Sum(x => x.Holes):N0}; shaped bags (object[], capacity -1 below) {rows.Values.Sum(x => x.Shaped):N0} in {PropShape.Shapes:N0} shapes");
        foreach (var (k, r) in rows.OrderByDescending(x => x.Value.Bytes).Take(15))
            Log.Info($"[reccensus]   {k,-28} bags {r.Bags,6:N0} keys/bag {(double)r.Keys / r.Bags,5:F1} {r.Bytes / 1048576.0,6:F2} MiB -> {r.BytesSized / 1048576.0,6:F2} | capacities {string.Join(" ", r.CapHist.OrderBy(x => x.Key).Select(x => x.Key + "x" + x.Value))}");
        foreach (var (name, f) in new[] { ("Objects.ById", typeof(Objects).GetField("ById", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)), ("Objects.ByPtr", typeof(Objects).GetField("ByPtr", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) })
            if (f?.GetValue(null) is System.Collections.IDictionary d) Log.Info($"[reccensus]   map {name}: {d.Count:N0} entries, capacity {Cap(d):N0}, free {Free(d):N0} ({DictBytes(Cap(d), 16) / 1048576.0:F2} MiB 32-bit)");
        if (typeof(Objects).GetField("_byIdx", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is Array bi) Log.Info($"[reccensus]   Objects._byIdx (L4b): {bi.Length:N0} slots ({(16 + 4L * bi.Length) / 1048576.0:F2} MiB 32-bit), PtrIndex={Objects.PtrIndex}");
    }
}
#else
[ProfileOnly]
public static class RecCensus { public static void After(int frame) { } }
#endif
