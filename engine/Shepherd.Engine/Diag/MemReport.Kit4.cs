#if !SHEPHERD_EBOOT   // kit #7 measure: a Mac planning tool, left out of the eboots (Diag/MemReport.Eboot.cs; SHEPHERD_ENGINE_TOOLS=1 keeps it)
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Diag;

/// <summary>kit4 intro-plan additions to MemReport's window and heap census (vita/notes/kit4-intro-plan.md). [ProfileOnly]: they read no
/// property-bag keys, and their string literals stay out of the V3 diet's readable-key scan (Assets/Diet.cs BuildReadable), so the diet
/// keeps exactly the keys it kept before. Every part is off unless its switch is set.
/// - SHEPHERD_CENSUS_NAMED=&lt;type short name&gt;[,...] (e.g. SkeletonDataAsset,SkeletonAnimation): each instance of those UnityEngine.Object
///   types is its own owner in the census, "UObj T 'object name'" (the per-type label stays the base for the plumbing cuts).
/// - SHEPHERD_CENSUS_DUPARRAYS=1: every reached one-dimensional primitive array is keyed by (type, length, FNV-1a 64 + HashCode of its
///   bytes); an array whose key was seen before counts as a duplicate -- what sharing identical read-only arrays would reclaim (an upper
///   bound: it says nothing about whether a reader writes into them; Diag/ArrayShare.cs is the experiment that checks that).
/// - a MemReport bench that throws is reported as a WRN line and the window goes on (the heap census used to be skipped).</summary>
[ProfileOnly]
static class MemReportKit4
{
    public static readonly HashSet<string> NamedTypes = new((Environment.GetEnvironmentVariable("SHEPHERD_CENSUS_NAMED") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
    public static readonly bool DupArrays = Environment.GetEnvironmentVariable("SHEPHERD_CENSUS_DUPARRAYS") == "1";

    public static string NamedLabel(string typeName, object o)
    {
        string name; try { name = Objects.Get(o)?.Name ?? "?"; } catch { name = "?"; }
        return $"UObj {typeName} '{name}'";
    }

    public static (bool ok, int len, ulong h1, int h2) ArrayKey(object o)
    {
        ReadOnlySpan<byte> b = o switch
        {
            float[] f => MemoryMarshal.AsBytes<float>(f), int[] i => MemoryMarshal.AsBytes<int>(i), byte[] y => y, short[] sh => MemoryMarshal.AsBytes<short>(sh),
            ushort[] u => MemoryMarshal.AsBytes<ushort>(u), double[] d => MemoryMarshal.AsBytes<double>(d), long[] l => MemoryMarshal.AsBytes<long>(l),
            _ => default
        };
        if (b.IsEmpty) return (false, 0, 0, 0);
        ulong h = 14695981039346656037UL; foreach (var x in b) { h ^= x; h *= 1099511628211UL; }
        var hc = new HashCode(); hc.AddBytes(b);
        return (true, b.Length, h, hc.ToHashCode());
    }

    public static void BenchFailed(Action bench, Exception e, Action<string> w)
    {
        var x = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
        Log.Warn($"MemReport bench {bench.Method.Name} skipped: {x.GetType().Name}: {x.Message}");
        w($"({bench.Method.Name} skipped: {x.GetType().Name}: {x.Message})");
    }

    public static IEnumerable<string> DupArrayLines(long n, long b32, IEnumerable<(string owner, long n, long b32)> byOwner, Func<double, string> mb)
    {
        yield return $"   primitive arrays with the same contents as one seen before (SHEPHERD_CENSUS_DUPARRAYS): {n:N0} = {mb(b32)} (32-bit); by owner (top 15):";
        foreach (var (owner, cn, cb) in byOwner.OrderByDescending(x => x.b32).Take(15)) yield return $"      {cn,10:N0} {mb(cb),12}  {owner}";
    }
}
#endif
