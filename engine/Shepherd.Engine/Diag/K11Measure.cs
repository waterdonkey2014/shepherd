#if !SHEPHERD_EBOOT
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #11, track measure (vita/notes/kit11-measure.md): the measurement rows' shared switch, output folder and per-frame
/// entry points. The rows themselves:
/// <list type="bullet">
/// <item><see cref="K11Heat"/> -- the frame change heat map (SHEPHERD_K11_HEAT): per 32 x 32 tile, which tiles a frame left unchanged, by the
/// draw stream's proof and (GL) by the pixels, how long they stay unchanged, and what keeps them changing; an on-screen overlay on GL.</item>
/// <item><see cref="K11Green"/> -- the green hole check (SHEPHERD_K11_GREEN): the debug view for the image cache, dirty rectangles and the
/// per-tile prefix cache (each region about to be redrawn is filled with pure green first; green left at the end = a hole).</item>
/// <item><see cref="K11Census"/> -- the pixel-contribution census (SHEPHERD_K11_CENSUS, GL, offline): what every draw of a sampled frame
/// changed, and whether that survived to the final image.</item>
/// <item><see cref="K11Counters"/> -- engine counters for later kits (SHEPHERD_K11_COUNTERS): random draws per frame, followers' task
/// re-decisions, exceptions thrown / caught per method.</item>
/// </list>
/// Every row is measurement only: off (the default) nothing here runs and no frame changes; on, the rows read the frame's recorded draw
/// stream, the backend's pixels (GL) and engine / game state, and write files. <c>SHEPHERD_K11_MEASURE=0</c> turns every row off whatever
/// the other switches say. Mac-only code and tools eboots: the kit apps' eboots leave this out (-p:ShepherdEboot=1); an eboot with it is a
/// tools build (SHEPHERD_ENGINE_TOOLS=1). Output: <c>SHEPHERD_K11_OUT</c> (default the log folder), one file per row, results only.</summary>
[ProfileOnly]
public static class K11Measure
{
    static string Env(string k) => Environment.GetEnvironmentVariable(k);
    /// <summary>SHEPHERD_K11_MEASURE=0: every kit #11 measurement row off (= kit #10's code paths).</summary>
    public static readonly bool Master = Env("SHEPHERD_K11_MEASURE") != "0";
    /// <summary>The frame's draw stream is wanted (heat map / census; the green check reads its world-pass mark): RenderCheck records it even
    /// without SHEPHERD_STREAM_HASH.</summary>
    public static bool StreamTap => K11Heat.On || K11Census.On || K11Green.On;
    /// <summary>Any row on.</summary>
    public static bool Any => StreamTap || K11Green.On || K11Counters.On;

    static string _out;
    /// <summary>The folder the rows write to (created on first use).</summary>
    public static string OutDir
    {
        get
        {
            if (_out != null) return _out;
            _out = Env("SHEPHERD_K11_OUT") is { Length: > 0 } o ? o : Program.LogDir;
            try { Directory.CreateDirectory(_out); } catch (Exception e) { Log.Warn($"[k11] cannot create {_out}: {e.Message}"); }
            return _out;
        }
    }
    public static string OutFile(string name) => Path.Combine(OutDir, name);

    /// <summary>The active scene's name (what the per-scene rows are keyed by).</summary>
    public static string Scene => Scenes.All.TryGetValue(Scenes.ActiveHandle, out var s) ? s.Name : "?";

    static bool _started;
    static void Start()
    {
        _started = true;
        if (!Any) return;
        Log.Info($"[k11] measure: heat={K11Heat.Describe()} green={K11Green.Describe()} census={K11Census.Describe()} counters={K11Counters.Describe()} -> {OutDir}");
        try { K11Counters.Start(); } catch (Exception e) { Log.Warn($"[k11] counters: {e.GetType().Name}: {e.Message} (first-chance / runtime events not available here)"); }
    }

    /// <summary>Boot's frame loop, before the frame's logic (the counters' frame start).</summary>
    public static void Before(int frame)
    {
        if (!_started) Start();
        if (K11Census.On) K11Census.BeginLoop(frame);
        if (K11Counters.On) K11Counters.Before(frame);
    }

    /// <summary>Boot's frame loop, after the frame was rendered (and before Present): every row's per-frame step.</summary>
    public static void After(int frame)
    {
        if (!_started) Start();
        if (!Any) return;
        var g = Render.SceneRenderer.G;
        try { if (K11Heat.On) K11Heat.After(frame, g); } catch (Exception e) { Log.Exception(e, "K11Heat"); }   // (first: it reads the frame's own pixels)
        try { if (K11Green.On) K11Green.After(frame, g); } catch (Exception e) { Log.Exception(e, "K11Green"); }   // (the self-test draws over frame 2)
        try { if (K11Census.On) K11Census.After(frame); } catch (Exception e) { Log.Exception(e, "K11Census"); }
        try { if (K11Counters.On) K11Counters.After(frame); } catch (Exception e) { Log.Exception(e, "K11Counters"); }
    }

    /// <summary>RenderCheck.HashRec / HashFrame: the frame's complete draw stream (any thread under SHEPHERD_MT_RENDER=1: kept for the main
    /// thread, which analyses it in <see cref="After"/>).</summary>
    public static void Stream(int frame, Render.RenderCheck.Rec rec)
    {
        if (K11Heat.On) K11Heat.Stream(frame, rec);
        if (K11Census.On) K11Census.Stream(frame, rec);
    }

    /// <summary>Boot.Run's end: every row's summary lines and files.</summary>
    public static void End()
    {
        if (!Any) return;
        try { K11Heat.End(); } catch (Exception e) { Log.Exception(e, "K11Heat.End"); }
        try { K11Green.End(); } catch (Exception e) { Log.Exception(e, "K11Green.End"); }
        try { K11Census.End(); } catch (Exception e) { Log.Exception(e, "K11Census.End"); }
        try { K11Counters.End(); } catch (Exception e) { Log.Exception(e, "K11Counters.End"); }
    }

    // ---------------------------------------------------------------- shared helpers
    /// <summary>A frame list "a,b,c", "from-to", "every:N" or "every:N@from-to" -> a predicate.</summary>
    public static Func<int, bool> Frames(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return _ => false;
        var set = new HashSet<int>(); var preds = new List<Func<int, bool>>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = raw;
            if (p.StartsWith("every:"))
            {
                var rest = p[6..]; int lo = 1, hi = int.MaxValue;
                int at = rest.IndexOf('@');
                if (at >= 0) { var r = rest[(at + 1)..].Split('-'); int.TryParse(r[0], out lo); if (r.Length > 1 && int.TryParse(r[1], out var h2)) hi = h2; rest = rest[..at]; }
                if (int.TryParse(rest, out var n) && n > 0) { int l = lo, h = hi; preds.Add(f => f >= l && f <= h && (f - l) % n == 0); }
            }
            else if (p.Contains('-')) { var r = p.Split('-'); if (int.TryParse(r[0], out var a) && int.TryParse(r[1], out var b)) preds.Add(f => f >= a && f <= b); }
            else if (int.TryParse(p, out var v)) set.Add(v);
        }
        return f => set.Contains(f) || preds.Any(q => q(f));
    }

    /// <summary>A 64-bit mix step (order-dependent).</summary>
    public static ulong Mix(ulong h, ulong v) { h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2); h *= 0xBF58476D1CE4E5B9UL; return h ^ (h >> 31); }

    /// <summary>Hash of a byte span, 8 bytes at a time.</summary>
    public static ulong Hash(ReadOnlySpan<byte> b, ulong h = 0xCBF29CE484222325UL)
    {
        var u = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(b);
        foreach (var x in u) { h ^= x; h *= 0x100000001B3UL; h ^= h >> 29; }
        for (int i = u.Length * 8; i < b.Length; i++) { h ^= b[i]; h *= 0x100000001B3UL; }
        return h;
    }

    /// <summary>The median of a list (0 when empty).</summary>
    public static double Median(List<double> v) { if (v.Count == 0) return 0; var a = v.ToArray(); Array.Sort(a); return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
}

/// <summary>Texture handles of the frame's draw stream -> the engine's texture record (its name, version, and whether it is a render
/// target), for the heat map's proof and the census's rows. Main thread only (it reads engine records). A handle in the stream that the
/// engine's records do not know (a backend-internal texture) is "unknown": the heat map's proof treats such a draw as changed every frame.
/// (Kit #8's real-video picture, Runtime/K8Video.cs on the Vita GL host, never reaches the stream at all: the heat map handles it apart.)</summary>
[ProfileOnly]
public static class K11Tex
{
    static readonly Dictionary<IntPtr, NTexture> _map = new();
    static readonly Dictionary<IntPtr, int> _unknown = new();   // a handle no record owned at a rebuild -> that frame (no new rebuild for it for 60 frames)
    static int _rebuiltFrame = -1;
    public static int Rebuilds;

    static bool Owns(NTexture t, IntPtr h) => !t.Destroyed && h != IntPtr.Zero && (t.CacheH == h || t.CacheHSdf == h || t.RtTarget == h);

    /// <summary>The record behind <paramref name="h"/> (null: none / unknown). One rebuild of the map per frame at most.</summary>
    public static NTexture Find(IntPtr h, int frame)
    {
        if (h == IntPtr.Zero) return null;
        if (_map.TryGetValue(h, out var t) && Owns(t, h)) return t;
        if (_rebuiltFrame == frame || (_unknown.TryGetValue(h, out var uf) && frame - uf < 60 && frame >= uf)) return null;
        _rebuiltFrame = frame; Rebuilds++; _map.Clear();
        foreach (var o in Objects.ById.Values)
            if (o is NTexture nt && !nt.Destroyed)
            {
                if (nt.CacheH != IntPtr.Zero) _map[nt.CacheH] = nt;
                if (nt.CacheHSdf != IntPtr.Zero) _map[nt.CacheHSdf] = nt;
                if (nt.RtTarget != IntPtr.Zero) _map[nt.RtTarget] = nt;
            }
        if (_map.TryGetValue(h, out t) && Owns(t, h)) { _unknown.Remove(h); return t; }
        _unknown[h] = frame;   // (a texture the engine's records do not own: a backend-internal one -- the heat map counts it as changed every frame)
        return null;
    }

    public static string Name(NTexture t) => t == null ? "?" : string.IsNullOrEmpty(t.Name) ? $"#{t.InstanceId}" : t.Name;
}
#endif
