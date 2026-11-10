using System.Runtime.InteropServices;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Runtime;

/// <summary>kit6 mem, lever L1 (vita/notes/kit6-mem.md): Spine's skeleton arrays shared WHILE the skeleton is read. The owner's
/// spine-unity.dll rewritten by tools/Rewriter --spine-share hands every array its binary reader has finished (vertices, UVs, triangles,
/// weights, timeline keys and curves, deform / draw-order frames, region offsets / UVs) to Shepherd.Hooks.SpineShare; this class answers
/// with an array it already keeps when the content is bit-for-bit the same (float bits, so -0 / NaN payloads count), else keeps the new
/// one. The reader then stores the kept array; the fresh one is garbage at once (nursery). Kit #4 / #5 measured the intro's 'Follower'
/// data as two copies of one 20.5 MB skeleton file with ~34 MiB of repeats inside each copy; kit #5's post-parse prototype (SpineShare.cs)
/// could not lower the peak inside a parse and cost ~70 device seconds per walk: here the sharing happens during the read, costs one hash
/// per array, and the second copy keeps almost no new arrays.
/// Why it is exact: an array is handed in only after the reader's last write to it, and nothing writes these arrays afterwards (static
/// scan of spine-unity and every game assembly: the only writers are the reader itself, MeshAttachment.UpdateUVs / RegionAttachment.
/// UpdateOffset / SetUVs on attachments being loaded, and Copy / NewLinkedMesh / GetRemappedClone / GetRepackedAttachments on their own
/// fresh copies; Spine itself already shares vertices / UVs / triangles between linked meshes). SHEPHERD_SPINE_READSHARE_CHECK re-hashes
/// every array that was handed out more than once and reports any change (the guard; kit6-mem.md has the measured 0).
/// Switches (env files): SHEPHERD_SPINE_READSHARE=0 -- no handler installed: every hook returns its argument (kit #5's arrays);
/// SHEPHERD_SPINE_READSHARE_SKIP=&lt;asset&gt;[,...] -- those SkeletonDataAssets (by name) read with their own arrays (the per-asset switch
/// back); SHEPHERD_SPINE_READSHARE_CHECK=&lt;frame&gt;[,...][,end] -- the guard; =shadow (Mac diagnostic, not in eboots) shares nothing but keeps
/// every would-be-shared pair and compares both arrays at the check frames (a write to either one would show).
/// Without the rewritten spine-unity.dll on the card nothing calls in: no line, no effect.</summary>
[ProfileOnly]
public static class SpineReadShare
{
    static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_READSHARE") ?? "").Trim().ToLowerInvariant();
    public static readonly bool On = Profile.Vita && Setting is "" or "1" or "on";
#if !SHEPHERD_EBOOT
    static readonly bool Shadow = Profile.Vita && Setting == "shadow";
    static readonly List<(object fresh, object kept)> _pairs = new();
#else
    const bool Shadow = false;
#endif
    static readonly string[] Skip = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_READSHARE_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    /// <summary>L1b: GetSkeletonData's binary read streams a big skeleton file from the pack (Assets/PackAssets.cs RangeStream: a 32 KiB buffer)
    /// instead of TextAsset.bytes' one array of the whole file (the intro's 'Follower' asks for one 20,032 KiB large object per read; kit #5's
    /// device-partition run broke there, with 15.4 MiB the arena's largest hole). The reader gets the same bytes in the same order. Only pack
    /// TextAssets of at least SHEPHERD_SPINE_STREAM_MIN_KB (default 64); SHEPHERD_SPINE_STREAM=0 = TextAsset.bytes as in kit #5.</summary>
    static readonly string StreamSetting = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_STREAM") ?? "").Trim().ToLowerInvariant();
    public static readonly bool Stream = Profile.Vita && StreamSetting is "" or "1" or "on";
    static readonly int StreamMin = (int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_SPINE_STREAM_MIN_KB"), out var smk) && smk >= 0 ? smk : 64) << 10;
    static long _pStreamed;
    static readonly string[] CheckAt = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_READSHARE_CHECK") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // the kept arrays: open addressing, linear probing; _h[i] = 0 empty, else (hash | 1) with bit 1 = "handed out again" (shared)
    static int[] _h; static object[] _a; static int _n, _mask;
    static bool _active, _broken; static string _asset;
    static int _pArrays, _pShared, _pKept, _parses; static long _pBytes, _pT0;
    static long _tArrays, _tShared, _tBytes;

    /// <summary>Profile.StartVita: the handlers (nothing else happens until the rewritten reader calls in).</summary>
    public static void Install()
    {
        if (Stream) Shepherd.Hooks.SpineShare.OnOpen = Open;
        if (!On && !Shadow) { if (Stream) { Shepherd.Hooks.SpineShare.OnBegin = Begin; Shepherd.Hooks.SpineShare.OnEnd = End; } return; }
        _h = new int[1024]; _a = new object[1024]; _mask = 1023; _active = true;
        Shepherd.Hooks.SpineShare.Floats = F; Shepherd.Hooks.SpineShare.Ints = I;
        Shepherd.Hooks.SpineShare.OnBegin = Begin; Shepherd.Hooks.SpineShare.OnEnd = End;
    }

    /// <summary>VitaMemory.BeforeRuntimeInit: one line saying what is installed (none when the setting is unset and the handlers are off).</summary>
    public static string ModeLine() => (On || Shadow)
        ? "[spineshare] read-time sharing of Spine arrays: handlers installed" + (Stream ? ", big skeleton files streamed from the pack" : ", streaming off (SHEPHERD_SPINE_STREAM=" + StreamSetting + ")") + (Shadow ? " (SHADOW: nothing is shared, pairs are kept for the check)" : "") + (Skip.Length > 0 ? "; read unshared: " + string.Join(", ", Skip) : "") + (CheckAt.Length > 0 ? "; guard at " + string.Join(", ", CheckAt) : "") + "; a line per skeleton read follows when the owner's spine-unity.dll is the --spine-share rewrite (SHEPHERD_SPINE_READSHARE=0 = kit #5)"
        : Setting.Length > 0 || StreamSetting.Length > 0 ? "[spineshare] read-time sharing off (SHEPHERD_SPINE_READSHARE=" + Setting + "): every Spine array is the reader's own (kit #5)" + (Stream ? "; big skeleton files streamed from the pack" : "; streaming off") : null;

    static float[] F(float[] a)
    {
        if (!_active || a.Length == 0 || (SpineLazySkins.Skipping > 0 && SpineLazySkins.SkippingHere)) return a;   // kit #7 L2: a lazy skin's first read keeps none of its arrays (this thread's skip read only: R4)
        unsafe { fixed (float* p = a) return (float[])Intern(a, a.Length, Hash((int*)p, a.Length, 0x51ED270Bu)); }
    }
    static int[] I(int[] a)
    {
        if (!_active || a.Length == 0 || (SpineLazySkins.Skipping > 0 && SpineLazySkins.SkippingHere)) return a;
        unsafe { fixed (int* p = a) return (int[])Intern(a, a.Length, Hash(p, a.Length, 0x2F1A5C3Du)); }
    }

    static unsafe uint Hash(int* p, int n, uint seed)
    {
        uint h = 2166136261u ^ seed;
        for (int i = 0; i < n; i++) h = (h ^ (uint)p[i]) * 16777619u;
        h ^= (uint)n * 0x9E3779B1u; h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
        return h;
    }
    static uint HashOf(object o)
    {
        unsafe
        {
            if (o is float[] f) { fixed (float* p = f) return Hash((int*)p, f.Length, 0x51ED270Bu); }
            var x = (int[])o; fixed (int* p = x) return Hash(p, x.Length, 0x2F1A5C3Du);
        }
    }
    static bool Same(object a, object b) => a.GetType() == b.GetType() && (a is float[] fa
        ? MemoryMarshal.AsBytes<float>(fa).SequenceEqual(MemoryMarshal.AsBytes<float>((float[])b))
        : MemoryMarshal.AsBytes<int>((int[])a).SequenceEqual(MemoryMarshal.AsBytes<int>((int[])b)));
    static long Size32(int n) => (16 + 4L * n + 7) & ~7L;   // a float[] / int[] on 32-bit Mono: 16-byte header, 8-aligned

    /// <summary>kit #7 L2 (review fix R4, kit7-spine.md): a lazy skin read after its skeleton read interns from whichever thread first uses the
    /// skin, possibly while another thread reads a skeleton. With lazy skins on, the table is entered by one thread at a time: a thread that
    /// finds it busy keeps its own array (exact, as with sharing off for that array) instead of racing the table (a race could end sharing for
    /// the whole session: _broken). With SHEPHERD_SPINE_LAZYSKIN=0 every read is a skeleton read and kit #6's path runs unchanged.</summary>
    static int _busy, _busyKept;
    static object Intern(object a, int len, uint hash)
    {
        if (_broken) return a;
        if (!SpineLazySkins.On) return InternOne(a, len, hash);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref _busyKept); return a; }
        try { return InternOne(a, len, hash); }
        finally { Volatile.Write(ref _busy, 0); }
    }
    static object InternOne(object a, int len, uint hash)
    {
        try
        {
            _pArrays++;
            int key = (int)(hash | 1u) & ~2, i = (int)(hash >> 2) & _mask;
            for (int s; (s = _h[i]) != 0; i = (i + 1) & _mask)
            {
                if ((s & ~2) != key) continue;
                var k = _a[i];
                if (((Array)k).Length != len || !Same(k, a)) continue;
                if ((s & 2) == 0) _h[i] = s | 2;
                _pShared++; _pBytes += Size32(len);
#if !SHEPHERD_EBOOT
                if (Shadow) { _pairs.Add((a, k)); return a; }
#endif
                return k;
            }
            _h[i] = key; _a[i] = a; _n++; _pKept++;
            if (_n * 4 > _h.Length * 3) Grow();
            return a;
        }
        catch (Exception e)
        {
            _broken = true;   // e.g. no memory for a bigger table: the rest is read unshared (always exact)
            Log.Warn("[spineshare] stopped sharing (" + e.GetType().Name + ": " + e.Message + "); arrays from here on are the reader's own");
            return a;
        }
    }

    static void Grow()
    {
        int cap = _h.Length * 2; var h = new int[cap]; var a = new object[cap]; int mask = cap - 1;
        for (int j = 0; j < _h.Length; j++)
        {
            int s = _h[j]; if (s == 0) continue;
            int i = (int)((uint)s >> 2) & mask;
            while (h[i] != 0) i = (i + 1) & mask;
            h[i] = s; a[i] = _a[j];
        }
        _h = h; _a = a; _mask = mask;
    }

    /// <summary>L1b: a stream over a big pack TextAsset's bytes, or null (TextAsset.bytes as the game reads it).</summary>
    static object Open(object textAsset)
    {
        // kit #13 basec (SHEPHERD_K13_LAZYSKINS, SpineLazySkins.Resident): a skeleton file outside the pack keeps its bytes in the TextAsset record;
        // the read gets a stream over that same array (the bytes TextAsset.bytes holds, in the same order), so L2 can find a skin's section again
        if (SpineLazySkins.Resident && Objects.Get(textAsset)?.Get("__bytes") is byte[] rb && rb.Length >= StreamMin && Array.IndexOf(Skip, _asset) < 0)
        {
            var ms = new MemoryStream(rb, 0, rb.Length, false, true); SpineLazySkins.OpenedResident(rb, ms); return ms;
        }
        if (Objects.Get(textAsset)?.Get("__bytes") is not Assets.PackAssets.PackBytes pb || pb.Length < StreamMin || Array.IndexOf(Skip, _asset) >= 0) return null;
        _pStreamed = pb.Length; var s = pb.OpenStream(); SpineLazySkins.Opened(pb, s); return s;   // kit #7 L2: the sections of this read can be found again
    }

    static void Begin(object asset)
    {
        _asset = (asset as UnityEngine.Object)?.name ?? "?";
        _active = !_broken && (On || Shadow) && Array.IndexOf(Skip, _asset) < 0;
        SpineLazySkins.Begin(_asset);   // kit #7 L2
        _pArrays = _pShared = _pKept = 0; _pBytes = 0; _pStreamed = 0; _pT0 = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    static object End(object data)
    {
        try
        {
            _parses++; _tArrays += _pArrays; _tShared += _pShared; _tBytes += _pBytes;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _pT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string lazy = SpineLazySkins.End();   // kit #7 L2
            Log.Info("[spineshare] '" + _asset + "' read" + (data == null ? " (FAILED: the game's error handling follows)" : "") + (_pStreamed > 0 ? " streamed from the pack (no " + (_pStreamed >> 10).ToString("N0") + " KiB array)" : "") +
                     (!On && !Shadow ? "; read-time sharing off" : (_active ? "" : " WITHOUT sharing (SHEPHERD_SPINE_READSHARE_SKIP)")) + ": " +
                     _pArrays.ToString("N0") + " arrays through the reader, " + _pShared.ToString("N0") + (Shadow ? " would be shared (shadow mode: none is)" : " shared") + " (" + (_pBytes / 1048576.0).ToString("F2") + " MiB on 32-bit), " +
                     _pKept.ToString("N0") + " new kept; parse " + ms.ToString("F0") + " ms incl. the hashing; totals " + _parses + " skeletons, " + _tShared.ToString("N0") + " shared " + (_tBytes / 1048576.0).ToString("F2") +
                     " MiB, table " + _n.ToString("N0") + " kept / " + (_h?.Length ?? 0).ToString("N0") + " slots (" + ((_h?.Length ?? 0) * 8 / 1024).ToString("N0") + " KiB)" + lazy);
        }
        catch { }
        _asset = null; _active = !_broken && (On || Shadow);
        return data;
    }

    /// <summary>Boot's frame loop: the guard at SHEPHERD_SPINE_READSHARE_CHECK frames.</summary>
    public static void After(int frame)
    {
        if (CheckAt.Length == 0 || _h == null) return;
        foreach (var c in CheckAt) if (int.TryParse(c, out var f) && f == frame) { Check("frame " + frame); return; }
    }
    /// <summary>End of the run: the guard (when "end" is listed) and the totals.</summary>
    public static void Summary()
    {
        if (_h == null && _parses == 0) return;
        if (Array.IndexOf(CheckAt, "end") >= 0 && _h != null) Check("at the end");
        Log.Info("[spineshare] totals: " + _parses + " skeleton reads, " + _tArrays.ToString("N0") + " arrays through the reader, " + _tShared.ToString("N0") + " shared (" + (_tBytes / 1048576.0).ToString("F2") +
                 " MiB on 32-bit), " + _n.ToString("N0") + " kept in a " + (_h?.Length ?? 0).ToString("N0") + "-slot table" + (_broken ? "; STOPPED early (see the warning)" : "") + (_busyKept > 0 ? "; " + _busyKept.ToString("N0") + " kept unshared (the table was busy on another thread: L2 late reads)" : "") + "; " + Assets.PackAssets.PackBytes.Streams + " skeleton files streamed (" + (Assets.PackAssets.PackBytes.StreamBytes >> 10).ToString("N0") + " KiB never held in one array)");
    }

    /// <summary>The guard: every kept array that was handed out again is re-hashed; a changed hash means a write after the share.</summary>
    static void Check(string when)
    {
        if (!SpineLazySkins.On) { CheckTable(when); return; }
        while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) Thread.Yield();   // R4: not while a late read on another thread interns
        try { CheckTable(when); } finally { Volatile.Write(ref _busy, 0); }
    }
    static void CheckTable(string when)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int shared = 0, sharedBad = 0, keptBad = 0; long badBytes = 0; var first = new List<string>();
        for (int i = 0; i < _h.Length; i++)
        {
            int s = _h[i]; if (s == 0) continue;
            var a = _a[i]; int now = (int)(HashOf(a) | 1u) & ~2;
            bool sh = (s & 2) != 0; if (sh) shared++;
            if (now == (s & ~2)) continue;
            if (sh) { sharedBad++; badBytes += Size32(((Array)a).Length); if (first.Count < 5) first.Add(a.GetType().Name + "[" + ((Array)a).Length + "]"); } else keptBad++;
        }
        int pairBad = 0;
#if !SHEPHERD_EBOOT
        foreach (var (fresh, kept) in _pairs) if (!Same(fresh, kept)) pairBad++;
#endif
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Log.Info("[spineshare] check " + when + ": " + shared.ToString("N0") + " shared arrays re-hashed, " + sharedBad + " changed" + (sharedBad > 0 ? " (" + (badBytes / 1024.0).ToString("F1") + " KiB: " + string.Join(", ", first) + ") -- WRITTEN AFTER THE SHARE" : " -- none written since the share") +
                 "; " + (_n - shared).ToString("N0") + " kept but not shared, " + keptBad + " changed" + (Shadow ? "; shadow pairs " + PairCount() + ", " + pairBad + " differ" : "") + " (" + ms.ToString("F0") + " ms)");
    }
#if !SHEPHERD_EBOOT
    static int PairCount() => _pairs.Count;
#else
    static int PairCount() => 0;
#endif
}
