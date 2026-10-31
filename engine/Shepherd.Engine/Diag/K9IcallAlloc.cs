namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>Device kit #9, track engine (vita/notes/kit9-engine.md), measurement only, Mac only (never in an eboot): the engine's own
/// main-thread allocation per icall. <c>SHEPHERD_K9_ICALL_ALLOC=&lt;from&gt;:&lt;to&gt;</c> (frames) wraps every bound icall after the registry is
/// up and counts, inside the window, each id's calls and its SELF allocation (GC.GetAllocatedBytesForCurrentThread before / after, nested
/// icalls subtracted) -- what the engine allocates answering it (result boxes, arrays, copies); the rewritten Unity stub's own argument
/// array is outside (the runtime track's typed stubs). Written to runtime/logs/icall-alloc.tsv at the end. It names the icalls behind the
/// kit #7 profiler's "alloc" rows (a behaviour message's allocation includes the icalls it calls). Unset: nothing is wrapped.</summary>
[ProfileOnly]
public static class K9IcallAlloc
{
    static readonly string Spec = Environment.GetEnvironmentVariable("SHEPHERD_K9_ICALL_ALLOC");
    static int _from = int.MaxValue, _to = int.MinValue, _depth;
    static long[] _bytes, _calls;
    static readonly long[] _child = new long[256];
    public static bool On => _bytes != null;

    public static void Wrap()
    {
        if (string.IsNullOrEmpty(Spec)) return;
        var p = Spec.Split(':');
        if (p.Length != 2 || !int.TryParse(p[0], out _from) || !int.TryParse(p[1], out _to)) { Log.Warn("[k9] SHEPHERD_K9_ICALL_ALLOC wants <from>:<to> (frames)"); return; }
        var t = Shepherd.Hooks.Icalls.Table; _bytes = new long[t.Length]; _calls = new long[t.Length];
        int n = 0;
        for (int i = 0; i < t.Length; i++)
        {
            var f = t[i]; if (f == null) continue;
            int id = i; t[i] = a => Measure(id, f, a); n++;
        }
        Log.Info("[k9] icall allocation measured in frames " + _from + ".." + _to + " (" + n + " icalls wrapped; Mac measurement)");
    }

    static object Measure(int id, Shepherd.Hooks.IcallImpl f, object[] a)
    {
        int fr = Runtime.Time.FrameCount;
        if (fr < _from || fr >= _to || _depth >= _child.Length - 1 || Environment.CurrentManagedThreadId != Impl.Core.MainThread) return f(a);
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        _child[++_depth] = 0;
        try { return f(a); }
        finally
        {
            long incl = GC.GetAllocatedBytesForCurrentThread() - b0;
            long self = incl - _child[_depth]; _depth--;
            _child[_depth] += incl;
            _bytes[id] += self; _calls[id]++;
        }
    }

    public static void Write(string dir)
    {
        if (_bytes == null) return;
        try
        {
            var rows = new List<(long b, long c, string n)>();
            long tb = 0, tc = 0;
            for (int i = 0; i < _bytes.Length; i++) if (_calls[i] > 0) { rows.Add((_bytes[i], _calls[i], IcallRegistry.Names[i])); tb += _bytes[i]; tc += _calls[i]; }
            rows.Sort((x, y) => y.b.CompareTo(x.b));
            int frames = Math.Max(1, _to - _from);
            var lines = new List<string> { "# kit #9 engine: the engine's self allocation per icall, frames " + _from + ".." + _to + " (Mac, 64-bit sizes): total " + (tb / 1024) + " KiB in " + tc + " calls = " + (tb / 1024.0 / frames).ToString("F1") + " KiB / frame", "kib_per_frame\tcalls_per_frame\tbytes_per_call\ticall" };
            foreach (var (b, c, n) in rows) lines.Add((b / 1024.0 / frames).ToString("F2") + "\t" + (c / (double)frames).ToString("F1") + "\t" + (c > 0 ? b / c : 0) + "\t" + n);
            File.WriteAllLines(Path.Combine(dir, "icall-alloc.tsv"), lines);
            Log.Info("[k9] icall allocation: " + (tb / 1024.0 / frames).ToString("F1") + " KiB / frame in " + (tc / (double)frames).ToString("F0") + " calls / frame -> icall-alloc.tsv");
        }
        catch (Exception e) { Log.Exception(e, "K9IcallAlloc"); }
    }
}
#endif
