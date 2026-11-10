using Shepherd.Engine.Native;

namespace Shepherd.Engine.Runtime;

/// <summary>kit #7 spine track, lever L2 (vita/notes/kit4-intro-plan.md 3.3, kit7-spine.md): a Spine skin's attachments read when the skin is
/// first used. The intro's 'Follower' skeleton data (two copies) holds 624 skins and 84,143 skin attachments each, 'Player' 123 skins and 24,793;
/// a few are ever shown (MEASURED-M census). With the owner's spine-unity.dll rewritten by tools/Rewriter --spine-lazyskin, ReadSkin asks
/// <see cref="Mark"/> after a non-default skin's header whether its slot section may wait: yes (a token) when this read streams the skeleton file
/// from the pack (kit #6 L1b: the only reads the section can be found again in) and the asset is not on the skip list. The section is then read
/// once with every attachment dropped (the real loader still runs: a missing region throws where it did; L1's interning is suspended for those
/// arrays) and read again from the pack, into the same dictionary in the same order with the same reader method and linked-mesh resolution,
/// the first time anything touches that skin's attachments (SetSkin, AddSkin of it, GetAttachment of a slot it fills, Attachments, ...). Skins
/// that animations deform are read during the load itself (the deform timeline holds its attachment). Exact by construction: every access to
/// the attachments is guarded; GetAttachment(s) of a slot the skin does not fill answer as the empty lookup would.
/// Switches (env files): SHEPHERD_SPINE_LAZYSKIN=0 -- no handler: every skin read at load (kit #6, also with the rewritten DLL);
/// SHEPHERD_SPINE_LAZYSKIN_SKIP=&lt;asset&gt;[,...] -- those SkeletonDataAssets read eagerly; SHEPHERD_SPINE_LAZYSKIN_LOG=&lt;n&gt; -- log the first n
/// later reads (default 40; each with the frame and the thread); Mac only: SHEPHERD_SPINE_LAZYSKIN_TRACE=1 adds the managed stack of each.
/// Without the rewritten spine-unity.dll on the card nothing calls in.</summary>
[ProfileOnly]
public static class SpineLazySkins
{
    static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_LAZYSKIN") ?? "").Trim().ToLowerInvariant();
    public static readonly bool On = Profile.Vita && Setting is "" or "1" or "on";
    static readonly string[] Skip = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_LAZYSKIN_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static readonly int LogN = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_SPINE_LAZYSKIN_LOG"), out var ln) ? ln : 40;
    /// <summary>Kit #13 basec (SHEPHERD_K13_LAZYSKINS, default on; SHEPHERD_K13_BASEC=0 off): skeleton files OUTSIDE the pack too -- a scene
    /// read from the game's own files keeps each TextAsset's bytes resident in its record (Assets/Loader.cs: one array of the whole file), so
    /// a waiting skin's section is found again in that same array (SpineReadShare.Open hands the read a stream over it). The base's follower
    /// skeleton holds hundreds of skins (outfits, faces, necklaces) of which a follower wears a few: the rest stay unread until first used,
    /// exactly as L2 does for pack files. Off = kit #12: such files are read eagerly (every skin's attachments at load).</summary>
    public static readonly bool Resident = On && Environment.GetEnvironmentVariable("SHEPHERD_K13_LAZYSKINS") is not ("0" or "off")
                                           && Environment.GetEnvironmentVariable("SHEPHERD_K13_BASEC") != "0";
    [ThreadStatic] static byte[] _rb;
#if !SHEPHERD_EBOOT
    static readonly bool Trace = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_LAZYSKIN_TRACE") == "1";
#endif

    /// <summary>Where a lazy skin's slot section starts: the pack record, the offset in its raw bytes, and for the log the asset and a sequence number.</summary>
    sealed class Token { public Assets.PackAssets.PackBytes Pb; public byte[] Bytes; public int Offset; public string Asset; public int Seq; public long T0; }

    // the read in progress on this thread (set by SpineReadShare's L1b Open / Begin, cleared at End)
    [ThreadStatic] static Assets.PackAssets.PackBytes _pb;
    [ThreadStatic] static Stream _stream;
    [ThreadStatic] static string _asset;
    [ThreadStatic] static int _pMarked, _pSeq;
    /// <summary>&gt; 0 while a lazy skin's slot section is read for the first time on some thread (its arrays are not interned by L1: nothing keeps
    /// them): the cheap gate. Which thread: <see cref="SkippingHere"/> (review fix R4, kit7-spine.md: a skip read on one thread no longer turns
    /// interning off for another thread's read; the thread-static is read only while some skip read runs).</summary>
    public static volatile int Skipping;
    [ThreadStatic] static int _skipping;
    public static bool SkippingHere => _skipping > 0;
    static int _marked, _loaded, _loadedDuringRead, _logged, _reads, _residentReads, _residentMarked; static long _loadTicks;

    public static void Install()
    {
        if (!On) return;
        Shepherd.Hooks.SpineLazy.OnMark = Mark; Shepherd.Hooks.SpineLazy.OnOpen = Open; Shepherd.Hooks.SpineLazy.OnSkip = SkipRead; Shepherd.Hooks.SpineLazy.OnLoaded = Loaded;
    }
    public static string ModeLine() => On
        ? "[spinelazy] lazy Spine skins: handler installed (a non-default skin of a streamed skeleton file is read when first used; SHEPHERD_SPINE_LAZYSKIN=0 = kit #6)" + (Skip.Length > 0 ? "; read eagerly: " + string.Join(", ", Skip) : "")
        : Setting.Length > 0 ? "[spinelazy] lazy Spine skins off (SHEPHERD_SPINE_LAZYSKIN=" + Setting + "): every skin read at load (kit #6)" : null;

    /// <summary>SpineReadShare (L1b): this read streams from the pack (the only reads whose sections can be found again).</summary>
    internal static void Opened(Assets.PackAssets.PackBytes pb, Stream s) { if (On) { _pb = pb; _rb = null; _stream = s; } }
    /// <summary>Kit #13 basec: this read streams a resident TextAsset array (<see cref="Resident"/>).</summary>
    internal static void OpenedResident(byte[] b, Stream s) { if (Resident) { _pb = null; _rb = b; _stream = s; } }
    internal static void Begin(string asset) { if (On) { _asset = asset; _pb = null; _rb = null; _stream = null; _pMarked = 0; _pSeq = 0; } }
    internal static string End()
    {
        if (!On) return null;
        string r = _pMarked > 0 ? $"; {_pMarked} of its skins wait until first used (L2)" : null;
        if (_stream != null) _reads++;
        if (_rb != null) _residentReads++;
        _pb = null; _rb = null; _stream = null; _asset = null; _pMarked = 0;
        return r;
    }

    static object Mark(int count)
    {
        try
        {
            if (Array.IndexOf(Skip, _asset) >= 0) return null;
            if (_rb != null && _stream is MemoryStream ms)   // kit #13 basec: a resident array (Resident)
            {
                _pMarked++; _marked++; _residentMarked++;
                return new Token { Bytes = _rb, Offset = checked((int)ms.Position), Asset = _asset, Seq = ++_pSeq };
            }
            if (_stream is not Assets.PackAssets.RangeStream rs || _pb == null) return null;
            _pMarked++; _marked++;
            return new Token { Pb = _pb, Offset = checked((int)rs.Position), Asset = _asset, Seq = ++_pSeq };
        }
        catch (Exception e) { Log.Warn("[spinelazy] not lazy (" + e.GetType().Name + ": " + e.Message + ")"); return null; }
    }
    static void SkipRead(object token, bool begin)   // Skip(true) / Skip(false) bracket the read on one thread
    {
        if (begin) { _skipping++; Interlocked.Increment(ref Skipping); } else { _skipping--; Interlocked.Decrement(ref Skipping); }
    }

    static object Open(object token)
    {
        var t = (Token)token; t.T0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (t.Bytes != null) return new MemoryStream(t.Bytes, t.Offset, t.Bytes.Length - t.Offset, false, true);   // kit #13 basec: the same array, from the section
        return t.Pb.OpenStreamAt(t.Offset);
    }
    static void Loaded(object token, object stream)
    {
        var t = (Token)token; long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t.T0; _loadTicks += dt;
        (stream as Stream)?.Dispose();
        _loaded++; bool during = _asset != null; if (during) _loadedDuringRead++;
        if (_logged < LogN)
        {
            _logged++;
            string line = $"[spinelazy] f{Time.FrameCount} '{t.Asset}' skin #{t.Seq} read when first used{(during ? " (during the skeleton read: an animation deforms it, or a linked mesh's parent)" : "")} in {dt * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms on thread {Environment.CurrentManagedThreadId}";
#if !SHEPHERD_EBOOT
            if (Trace) line += "\n" + Environment.StackTrace;
#endif
            Log.Info(line);
        }
    }

    public static void Summary()
    {
        if (!On || (_marked == 0 && _reads == 0)) return;
        Log.Info($"[spinelazy] totals: {_marked} skins waited at load in {_reads} streamed skeleton reads; {_loaded} read later ({_loadedDuringRead} during a skeleton read), {_marked - _loaded} never read; later reads {_loadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms in all" +
                 (_residentReads > 0 ? $"; kit #13: {_residentMarked} of the waiting skins in {_residentReads} reads of resident (non-pack) skeleton files (SHEPHERD_K13_LAZYSKINS=0: read at load)" : ""));
    }
}
