using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Shepherd.Engine;
namespace Shepherd.Audio;

/// <summary>Stage C (vita/notes/m1-audio.md): the Vita profile's FMOD stubs (<see cref="Fmod"/>) play the audio bake through shepaudio
/// (vita/audio, the C mixer) when SHEPHERD_AUDIO_BAKE=&lt;dir holding manifest.json&gt; is set. Unset (the default) the stubs stay
/// silent, exactly as before. Events that are not in the bake keep the silent stub.
///
/// Binding: every shepaudio entry point is called through an unmanaged function pointer (the same native-call path as Gl's 51 entry
/// points), resolved either from the native host (SHEPHERD_AUDIO_HOST=1: the Vita GL+audio eboot exports <c>shepaudio_proc</c> from
/// its static P/Invoke table and links libshepaudio_vita.a; the Mac rehearsal links libshepaudio.a the same way) or, without a
/// host, from a dylib (SHEPHERD_AUDIO_LIB, default vita/audio/build/mac/libshepaudio.dylib under the repo). Only entry points
/// without a struct whose layout differs between 32 and 64 bits are used (sa_init_opts, sa_get_stats_v, offsets for the info structs).
///
/// Output (SHEPHERD_AUDIO_OUT):
/// - <c>vita</c> (the host's default): sceAudioOut on shepaudio's own thread (sa_vita_out_start); the IO thread decodes streams. Both threads
///   are pinned to the spare core USER_2 (the multicore plan, PLAN.md: audio + streaming on USER_2; SHEPHERD_AUDIO_AFFINITY /
///   SHEPHERD_AUDIO_IO_AFFINITY, 0 = inherit) so the game's frames never delay them. The audio clock is the hardware's: music and sounds
///   play at the right speed and pitch whatever the frame rate (kit3 play mode, vita/notes/play-mode.md).
/// - <c>rtfile:&lt;path.wav&gt;</c> (Mac, kit3 play mode): the Mac stand-in for the Vita's output -- shepaudio's real-time output thread
///   (sa_rt_out_start: mixes a grain, appends it to the WAV, waits for the wall clock), the IO thread for streams, no in-line mixing:
///   what the Vita's speaker plays, at the output rate, whatever the game's frame rate. NEVER an audio device.
/// - <c>file:&lt;path.wav&gt;</c> (the Mac default: &lt;root&gt;/runtime/audiobake/_engine/boot.wav): every Studio update mixes the audio up to
///   the end of the current game frame (the engine's realtime + one frame) and appends it to a float WAV. Deterministic: SYNC_IO,
///   no threads. Sample s belongs to engine frame s / (rate * frame time). A path inside a git work tree must be under its
///   runtime/ (derived audio never lands where git could pick it up). NEVER an audio device: the Mac library has no device backend.
/// - <c>null</c>: the same pacing, mixed and discarded (CPU measurements).
/// SHEPHERD_AUDIO_OPTS appends sa_init_opts keys (e.g. cache_kib=8192); SHEPHERD_AUDIO_TRACE=1 also logs every routed call (and
/// SHEPHERD_AUDIO_TRACE_STACKS=1 the managed caller of every create); SHEPHERD_AUDIO_MUTE=&lt;paths&gt; plays those events at volume 0 (A/B).
/// The event log (frame, audio frame, what) goes to runtime/logs/audio-events.tsv with the file/null outputs (or SHEPHERD_AUDIO_EVENTS=1).</summary>
[ProfileOnly]
static unsafe class ShepAudio
{
    public static readonly string BakeDir = Environment.GetEnvironmentVariable("SHEPHERD_AUDIO_BAKE") is { Length: > 0 } b ? b : null;
    static readonly bool HostProc = Environment.GetEnvironmentVariable("SHEPHERD_AUDIO_HOST") == "1";
    static readonly bool Trace = Environment.GetEnvironmentVariable("SHEPHERD_AUDIO_TRACE") == "1";
    public static bool On { get; private set; }
    public static string Status { get; private set; } = "off (SHEPHERD_AUDIO_BAKE unset: silent stubs)";
    static IntPtr _sys, _lib;
    static string _out = "";            // vita | rtfile | file | null
    static string _wav;
    static int _rate = 48000;
    static long _rendered;              // file/null: output frames mixed so far (audio clock)
    static int _firstFrame = -1;

    // ---- function pointers (resolved once in Init) ----
    static delegate* unmanaged<byte*, byte*, IntPtr, IntPtr, IntPtr, IntPtr> _init;
    static delegate* unmanaged<IntPtr> _lastError;
    static delegate* unmanaged<IntPtr, void> _shutdown;
    static delegate* unmanaged<IntPtr, int> _update;
    static delegate* unmanaged<IntPtr, int, void> _setTiming;
    static delegate* unmanaged<IntPtr, uint, int> _setPollMask;
    static delegate* unmanaged<IntPtr, Notification*, int, int> _poll;
    static delegate* unmanaged<IntPtr, double*, int, int> _statsV;
    static delegate* unmanaged<IntPtr, int> _eventCount;
    static delegate* unmanaged<IntPtr, int, byte*, int> _eventInfo;
    static delegate* unmanaged<IntPtr, int, int, byte*, int> _eventParam;
    static delegate* unmanaged<IntPtr, int, int, int> _preload;
    static delegate* unmanaged<IntPtr, int, int> _unload;
    static delegate* unmanaged<IntPtr, int, uint> _create;
    static delegate* unmanaged<IntPtr, uint, int> _start, _release, _state;
    static delegate* unmanaged<IntPtr, uint, int, int> _stop, _setPaused, _setTimeline;
    static delegate* unmanaged<IntPtr, uint, int, float, int> _setParamIndex;
    static delegate* unmanaged<IntPtr, uint, float, int> _setVolume, _setPitch;
    static delegate* unmanaged<IntPtr, uint, Vec3*, Vec3*, int> _set3d;
    static delegate* unmanaged<IntPtr, uint, int*, int> _getTimeline;
    static delegate* unmanaged<IntPtr, Vec3*, Vec3*, Vec3*, Vec3*, int> _setListener;
    static delegate* unmanaged<IntPtr, byte*, float, int> _setGlobal, _busSetVolume;
    static delegate* unmanaged<IntPtr, byte*, int> _busFind;
    static delegate* unmanaged<IntPtr, byte*, int, int> _busSetMute, _busSetPaused, _busStopAll, _fileOpen;
    static delegate* unmanaged<IntPtr, int, int> _fileWrite;
    static delegate* unmanaged<IntPtr, int> _fileClose, _vitaOutStop;
    static delegate* unmanaged<IntPtr, int*, int> _vitaOutStart;
    static delegate* unmanaged<uint*, int, int> _poolStats;   // host only: shepherd_audio_pool_stats
    static delegate* unmanaged<IntPtr, byte*, int, int, int> _rtStart;   // rtfile only (the Mac library): sa_rt_out_start
    static delegate* unmanaged<IntPtr, double*, int> _rtStop;

    [StructLayout(LayoutKind.Sequential)] public struct Vec3 { public float X, Y, Z; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct Notification
    {
        [FieldOffset(0)] public uint Instance; [FieldOffset(4)] public uint Type; [FieldOffset(8)] public int Event;
        [FieldOffset(16)] public ulong UserData;
    }
    public const uint CbAll = 0x7FFFFFFF & ~1u;   // every EVENT_CALLBACK_TYPE but CREATED (never fired)

    // ---- the bake's descriptions (event index -> info), filled at Init ----
    public sealed class EvInfo
    {
        public string Path; public int G0, G1, G2, G3; public bool Oneshot, Is3D, Snapshot; public int LengthMs; public float MinDist, MaxDist;
        public string[] ParamNames; public float[] PMin, PMax, PDef; public bool[] PGlobal;
        public int Created, Started, Stopped, Destroyed;   // bookkeeping for the leak report
    }
    public static EvInfo[] Events = Array.Empty<EvInfo>();
    static readonly Dictionary<string, int> EvByPath = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(int, int, int, int), int> EvByGuid = new();
    static readonly Dictionary<string, byte[]> Utf8Cache = new(StringComparer.Ordinal);
    static readonly HashSet<string> GlobalNotInBake = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHEPHERD_AUDIO_MUTE=&lt;event path&gt;[,...] (measurements): those events play at volume 0 -- same instances, same take draws,
    /// same game logic -- so a full render minus this one is exactly their contribution (vita/audio/tools/engine_wav_check.py).</summary>
    static readonly HashSet<string> Muted = new((Environment.GetEnvironmentVariable("SHEPHERD_AUDIO_MUTE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
    static bool[] _muted = Array.Empty<bool>();

    // ---- measurements ----
    static long _updTicks, _mixTicks, _frames; static readonly List<float> FrameMs = new(4096);
    static double _lastMixSec;
    static StreamWriter _events;
    public static long Routed, Unbaked;

    public static int FindEvent(string path) => path != null && EvByPath.TryGetValue(path, out var e) ? e : -1;
    public static int FindEvent((int, int, int, int) guid) => EvByGuid.TryGetValue(guid, out var e) ? e : -1;
    public static int Frame => Shepherd.Engine.Runtime.Time.FrameCount;
    /// <summary>The audio clock in output frames: what the file/null outputs rendered, or (vita, rtfile) what the output thread mixed.</summary>
    public static long AudioFrame => _out is "vita" or "rtfile" ? MixedFrames() : _rendered;
    static Stopwatch _clockSw; static double _noteWall; static long _noteFrames;
    static long MixedFrames() { double* a = stackalloc double[24]; _statsV(_sys, a, 24); return (long)a[23]; }
    /// <summary>kit3 play mode: the audio clock in seconds (shepaudio's mixed frames / the output rate) -- the hardware's time on the Vita,
    /// the wall clock's with rtfile, the frames' with file/null; the silent stubs' timelines advance on it (Fmod.cs).</summary>
    public static double ClockSeconds => On ? AudioFrame / (double)_rate : Shepherd.Engine.Runtime.Time.Realtime;

    static string Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;

    /// <summary>Studio::System::create: load the bake, pick the output. Failure = the silent stubs (logged).</summary>
    public static void Init()
    {
        if (BakeDir == null || On || _sys != IntPtr.Zero) return;
        try
        {
            var manifest = Path.Combine(BakeDir, "manifest.json");
            if (!File.Exists(manifest)) { Status = $"FAILED: no {manifest} (silent stubs)"; Log.Error("[audio] " + Status); return; }
            Bind();
            _out = (Env("SHEPHERD_AUDIO_OUT") ?? (HostProc ? "vita" : "file:" + Path.Combine(Program.Root, "runtime", "audiobake", "_engine", "boot.wav"))).Trim();
            bool vita = _out == "vita";
            if (_out.StartsWith("file:", StringComparison.Ordinal)) { _wav = Path.GetFullPath(_out[5..]); _out = "file"; GuardWavPath(_wav); }
            else if (_out.StartsWith("rtfile:", StringComparison.Ordinal)) { _wav = Path.GetFullPath(_out[7..]); _out = "rtfile"; GuardWavPath(_wav); }
            else if (_out != "vita" && _out != "null") throw new InvalidOperationException($"SHEPHERD_AUDIO_OUT='{_out}' (expected vita, rtfile:<path.wav>, file:<path.wav> or null)");
            bool threaded = vita || _out == "rtfile";   // an output thread at the hardware's pace + the IO thread (no in-line mixing)
            var opts = $"rate=48000,voices=48,instances=1024,streams=8,cache_kib={(vita ? 12288 : 24576)},seed=24301,{(threaded ? "sync_io=0" : "sync_io=1")}";
            if (vita) opts += $",io_affinity={IntEnv("SHEPHERD_AUDIO_IO_AFFINITY", UserCore2)}";   // the IO thread (AAC decode, file reads) on USER_2 too
            if (Env("SHEPHERD_AUDIO_OPTS") is { } extra) opts += "," + extra;
            IntPtr alloc = IntPtr.Zero, free = IntPtr.Zero;
            if (HostProc && Env("SHEPHERD_AUDIO_POOL") != "0") { alloc = Proc("shepherd_audio_alloc"); free = Proc("shepherd_audio_free"); }
            var sw = Stopwatch.StartNew();
            fixed (byte* m = Utf8(manifest)) fixed (byte* o = Utf8(opts))
                _sys = _init(m, o, alloc, free, IntPtr.Zero);
            if (_sys == IntPtr.Zero && vita && Marshal.PtrToStringUTF8(_lastError()) is { } ioErr && ioErr.Contains("IO thread") && !opts.Contains("io_affinity=0,") && !opts.EndsWith("io_affinity=0"))
            {
                // kit #3 review: pinning the IO thread to USER_2 is new in kit 3 and has not run on a Vita -- if the kernel refuses the mask,
                // start it with the inherited affinity instead of losing the sound (sa_init_opts cleaned up after itself)
                Log.Warn($"[audio] sa_init_opts: {ioErr} with io_affinity=0x{IntEnv("SHEPHERD_AUDIO_IO_AFFINITY", UserCore2):x}: again with io_affinity=0 (inherit)");
                opts += ",io_affinity=0";
                fixed (byte* m = Utf8(manifest)) fixed (byte* o = Utf8(opts))
                    _sys = _init(m, o, alloc, free, IntPtr.Zero);
            }
            if (_sys == IntPtr.Zero) { Status = $"FAILED: sa_init_opts: {Marshal.PtrToStringUTF8(_lastError())} (silent stubs)"; Log.Error("[audio] " + Status); return; }
            _setTiming(_sys, 1);
            _setPollMask(_sys, CbAll);
            LoadEvents();
            if (vita)
            {
                int* cfg = stackalloc int[5];
                cfg[0] = IntEnv("SHEPHERD_AUDIO_PORT", 1); cfg[1] = IntEnv("SHEPHERD_AUDIO_GRAIN", 512); cfg[2] = IntEnv("SHEPHERD_AUDIO_PRIORITY", 64);
                cfg[3] = IntEnv("SHEPHERD_AUDIO_AFFINITY", UserCore2); cfg[4] = 64 * 1024;   // kit3: pinned to the spare core (0 = inherit)
                int r = _vitaOutStart(_sys, cfg);
                if (r != 0 && cfg[3] != 0)
                {
                    // kit #3 review: the out thread's USER_2 mask is new in kit 3 (never run on a Vita): if the kernel refuses it, the
                    // inherited affinity instead of silence
                    Log.Warn($"[audio] sa_vita_out_start -> {r} with affinity 0x{cfg[3]:x}: again with affinity 0 (inherit)");
                    cfg[3] = 0;
                    r = _vitaOutStart(_sys, cfg);
                }
                if (r != 0)
                {
                    // no audio port: mix in-line and discard, so the audio clock still runs and STOPPED/DESTROYED keep coming (the
                    // game's one-shot cleanup callbacks must not stall); logged as an error, the run goes on silent
                    Log.Error($"[audio] sa_vita_out_start -> {r}: no sound; mixing in-line into nothing so the instances keep time");
                    _out = "null-fallback";
                }
            }
            else if (_out == "file")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_wav)!);
                int r; fixed (byte* p = Utf8(_wav)) r = _fileOpen(_sys, p, 1);
                if (r != 0) throw new IOException($"sa_file_open({_wav}) -> {r}");
            }
            else if (_out == "rtfile")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_wav)!);
                _rtStart = (delegate* unmanaged<IntPtr, byte*, int, int, int>)Proc("sa_rt_out_start"); _rtStop = (delegate* unmanaged<IntPtr, double*, int>)Proc("sa_rt_out_stop");
                int r; fixed (byte* p = Utf8(_wav)) r = _rtStart(_sys, p, IntEnv("SHEPHERD_AUDIO_GRAIN", 512), 1);
                if (r != 0) throw new IOException($"sa_rt_out_start({_wav}) -> {r}");
            }
            _clockSw = Stopwatch.StartNew();
            if (_out is "file" or "null" or "rtfile" || Env("SHEPHERD_AUDIO_EVENTS") == "1")
            {
                var ev = Path.Combine(Program.LogDir, "audio-events.tsv");
                Directory.CreateDirectory(Path.GetDirectoryName(ev)!);
                _events = new StreamWriter(ev, false, new UTF8Encoding(false)) { AutoFlush = false };
                _events.WriteLine("frame\taudio_frame\twhat\tevent\thandle\tvalue");
            }
            On = true;
            Status = $"on: {Events.Length} events from {BakeDir}, output {(_out == "file" ? "file " + _wav : _out)}, opts {opts}{(alloc != IntPtr.Zero ? ", host pool" : "")}, " +
                     $"binding {(HostProc ? "host shepaudio_proc" : "dylib " + LibPath())}, init {sw.Elapsed.TotalMilliseconds:F1} ms";
            Log.Info("[audio] " + Status);
        }
        catch (Exception e) { Status = $"FAILED: {e.GetBaseException().Message} (silent stubs)"; Log.Error("[audio] " + Status); On = false; }
    }

    static int IntEnv(string k, int d) { var s = Env(k); return s == null ? d : s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : int.TryParse(s, out var v) ? v : d; }
    /// <summary>SCE_KERNEL_CPU_MASK_USER_2 (psp2/kernel/cpu.h): the spare user core of the multicore plan (USER_0 scripts, USER_1 render).</summary>
    const int UserCore2 = 0x40000;
    static string LibPath() => Env("SHEPHERD_AUDIO_LIB") ?? Path.Combine(Program.Root, "vita", "audio", "build", "mac", "libshepaudio.dylib");

    /// <summary>Derived audio must not land where git could pick it up: inside a work tree only under its runtime/ (gitignored).</summary>
    static void GuardWavPath(string wav)
    {
        if (!wav.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"SHEPHERD_AUDIO_OUT file must be a .wav ({wav})");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(wav)!); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
            {
                var rt = Path.Combine(d.FullName, "runtime") + Path.DirectorySeparatorChar;
                if (!wav.StartsWith(rt, StringComparison.Ordinal)) throw new InvalidOperationException($"refusing to write derived audio into the git work tree outside runtime/: {wav}");
                return;
            }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static IntPtr HostLookup(string name) => shepaudio_proc(name);
    [DllImport("__Internal")] static extern IntPtr shepaudio_proc([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    static IntPtr Proc(string name)
    {
        IntPtr p;
        if (HostProc) p = HostLookup(name);
        else
        {
            if (_lib == IntPtr.Zero) _lib = NativeLibrary.Load(LibPath());
            p = NativeLibrary.TryGetExport(_lib, name, out var q) ? q : IntPtr.Zero;
        }
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"shepaudio: {name} not found ({(HostProc ? "host shepaudio_proc" : LibPath())})");
        return p;
    }

    static void Bind()
    {
        _init = (delegate* unmanaged<byte*, byte*, IntPtr, IntPtr, IntPtr, IntPtr>)Proc("sa_init_opts");
        _lastError = (delegate* unmanaged<IntPtr>)Proc("sa_last_error");
        _shutdown = (delegate* unmanaged<IntPtr, void>)Proc("sa_shutdown");
        _update = (delegate* unmanaged<IntPtr, int>)Proc("sa_update");
        _setTiming = (delegate* unmanaged<IntPtr, int, void>)Proc("sa_set_timing");
        _setPollMask = (delegate* unmanaged<IntPtr, uint, int>)Proc("sa_set_poll_mask");
        _poll = (delegate* unmanaged<IntPtr, Notification*, int, int>)Proc("sa_poll");
        _statsV = (delegate* unmanaged<IntPtr, double*, int, int>)Proc("sa_get_stats_v");
        _eventCount = (delegate* unmanaged<IntPtr, int>)Proc("sa_event_count");
        _eventInfo = (delegate* unmanaged<IntPtr, int, byte*, int>)Proc("sa_event_get_info");
        _eventParam = (delegate* unmanaged<IntPtr, int, int, byte*, int>)Proc("sa_event_get_param");
        _preload = (delegate* unmanaged<IntPtr, int, int, int>)Proc("sa_event_preload");
        _unload = (delegate* unmanaged<IntPtr, int, int>)Proc("sa_event_unload");
        _create = (delegate* unmanaged<IntPtr, int, uint>)Proc("sa_event_create_index");
        _start = (delegate* unmanaged<IntPtr, uint, int>)Proc("sa_instance_start");
        _release = (delegate* unmanaged<IntPtr, uint, int>)Proc("sa_instance_release");
        _state = (delegate* unmanaged<IntPtr, uint, int>)Proc("sa_instance_get_playback_state");
        _stop = (delegate* unmanaged<IntPtr, uint, int, int>)Proc("sa_instance_stop");
        _setPaused = (delegate* unmanaged<IntPtr, uint, int, int>)Proc("sa_instance_set_paused");
        _setTimeline = (delegate* unmanaged<IntPtr, uint, int, int>)Proc("sa_instance_set_timeline_ms");
        _setParamIndex = (delegate* unmanaged<IntPtr, uint, int, float, int>)Proc("sa_instance_set_param_index");
        _setVolume = (delegate* unmanaged<IntPtr, uint, float, int>)Proc("sa_instance_set_volume");
        _setPitch = (delegate* unmanaged<IntPtr, uint, float, int>)Proc("sa_instance_set_pitch");
        _set3d = (delegate* unmanaged<IntPtr, uint, Vec3*, Vec3*, int>)Proc("sa_instance_set_3d");
        _getTimeline = (delegate* unmanaged<IntPtr, uint, int*, int>)Proc("sa_instance_get_timeline_ms");
        _setListener = (delegate* unmanaged<IntPtr, Vec3*, Vec3*, Vec3*, Vec3*, int>)Proc("sa_set_listener");
        _setGlobal = (delegate* unmanaged<IntPtr, byte*, float, int>)Proc("sa_set_global_param");
        _busFind = (delegate* unmanaged<IntPtr, byte*, int>)Proc("sa_bus_find");
        _busSetVolume = (delegate* unmanaged<IntPtr, byte*, float, int>)Proc("sa_bus_set_volume");
        _busSetMute = (delegate* unmanaged<IntPtr, byte*, int, int>)Proc("sa_bus_set_mute");
        _busSetPaused = (delegate* unmanaged<IntPtr, byte*, int, int>)Proc("sa_bus_set_paused");
        _busStopAll = (delegate* unmanaged<IntPtr, byte*, int, int>)Proc("sa_bus_stop_all");
        _fileOpen = (delegate* unmanaged<IntPtr, byte*, int, int>)Proc("sa_file_open");
        _fileWrite = (delegate* unmanaged<IntPtr, int, int>)Proc("sa_file_write");
        _fileClose = (delegate* unmanaged<IntPtr, int>)Proc("sa_file_close");
        _vitaOutStart = (delegate* unmanaged<IntPtr, int*, int>)Proc("sa_vita_out_start");
        _vitaOutStop = (delegate* unmanaged<IntPtr, int>)Proc("sa_vita_out_stop");
        if (HostProc) _poolStats = (delegate* unmanaged<uint*, int, int>)Proc("shepherd_audio_pool_stats");
    }

    static void LoadEvents()
    {
        int n = _eventCount(_sys), P = IntPtr.Size;
        var evs = new EvInfo[n];
        byte* info = stackalloc byte[96]; byte* pinfo = stackalloc byte[48];
        for (int e = 0; e < n; e++)
        {
            new Span<byte>(info, 96).Clear();
            if (_eventInfo(_sys, e, info) != 0) continue;
            var x = new EvInfo
            {
                Path = Marshal.PtrToStringUTF8(*(IntPtr*)info),
                G0 = *(int*)(info + P), G1 = *(int*)(info + P + 4), G2 = *(int*)(info + P + 8), G3 = *(int*)(info + P + 12),
                Oneshot = *(int*)(info + P + 16) != 0, Is3D = *(int*)(info + P + 20) != 0, Snapshot = *(int*)(info + P + 24) != 0,
                LengthMs = *(int*)(info + P + 28), MinDist = *(float*)(info + P + 32), MaxDist = *(float*)(info + P + 36),
            };
            int np = *(int*)(info + P + 44);
            x.ParamNames = new string[np]; x.PMin = new float[np]; x.PMax = new float[np]; x.PDef = new float[np]; x.PGlobal = new bool[np];
            for (int k = 0; k < np; k++)
            {
                new Span<byte>(pinfo, 48).Clear();
                if (_eventParam(_sys, e, k, pinfo) != 0) continue;
                x.ParamNames[k] = Marshal.PtrToStringUTF8(*(IntPtr*)pinfo);
                x.PMin[k] = *(float*)(pinfo + P); x.PMax[k] = *(float*)(pinfo + P + 4); x.PDef[k] = *(float*)(pinfo + P + 8); x.PGlobal[k] = *(int*)(pinfo + P + 12) != 0;
            }
            evs[e] = x;
            if (x.Path != null) EvByPath[x.Path] = e;
            EvByGuid[(x.G0, x.G1, x.G2, x.G3)] = e;
        }
        Events = evs;
        _muted = evs.Select(x => x?.Path != null && Muted.Contains(x.Path)).ToArray();
        if (Muted.Count > 0) Log.Info($"[audio] SHEPHERD_AUDIO_MUTE: {_muted.Count(m => m)} of {Muted.Count} event(s) found in the bake play at volume 0");
    }

    public static byte[] Utf8(string s)
    {
        if (s == null) return null;
        lock (Utf8Cache)
        {
            if (Utf8Cache.TryGetValue(s, out var b)) return b;
            b = new byte[Encoding.UTF8.GetByteCount(s) + 1]; Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
            if (Utf8Cache.Count < 4096) Utf8Cache[s] = b;
            return b;
        }
    }

    public static int ParamIndex(int ev, string name)
    {
        var x = Events[ev]; if (x?.ParamNames == null || name == null) return -1;
        for (int k = 0; k < x.ParamNames.Length; k++) if (string.Equals(x.ParamNames[k], name, StringComparison.OrdinalIgnoreCase)) return k;
        return -1;
    }

    // ---- instance calls (h != 0) ----
    public static uint Create(int ev)
    {
        uint h = _create(_sys, ev);
        if (h != 0 && (_muted[ev] || (Shepherd.Engine.Runtime.K12BootSeq.On && Shepherd.Engine.Runtime.K12BootSeq.Muted(ev)))) _setVolume(_sys, h, 0f);   // (kit #12 boot: the skipped splash's sounds at volume 0)
        if (h != 0) { Events[ev].Created++; Log2("create", ev, h, ""); if (Trace && Environment.GetEnvironmentVariable("SHEPHERD_AUDIO_TRACE_STACKS") == "1") Log.Info("[audio]   at " + string.Join(" <- ", new StackTrace(2, false).GetFrames().Take(14).Select(f => f.GetMethod()).Where(m => m != null).Select(m => m.DeclaringType?.Name + "." + m.Name))); }
        return h;
    }
    public static void Start(uint h, int ev) { _start(_sys, h); Events[ev].Started++; Log2("start", ev, h, ""); }
    public static void Stop(uint h, int ev, bool fade) { _stop(_sys, h, fade ? 1 : 0); Log2("stop", ev, h, fade ? "fade" : "immediate"); }
    public static void Release(uint h, int ev) { _release(_sys, h); Log2("release", ev, h, ""); }
    /// <summary>FMOD PLAYBACK_STATE; a handle shepaudio already destroyed reads STOPPED.</summary>
    public static int State(uint h) { int s = _state(_sys, h); return s < 0 ? 2 : s; }
    public static int SetParam(uint h, int ev, string name, float v)
    {
        int k = ParamIndex(ev, name);
        if (k < 0) return -1;
        if (Events[ev].PGlobal[k]) return -2;
        Log2("param", ev, h, name + "=" + v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return _setParamIndex(_sys, h, k, v);
    }
    public static void SetVolume(uint h, int ev, float v) => _setVolume(_sys, h, _muted[ev] ? 0f : v);
    public static void SetPitch(uint h, float v) => _setPitch(_sys, h, v);
    public static void SetPaused(uint h, bool p) => _setPaused(_sys, h, p ? 1 : 0);
    public static void SetTimeline(uint h, int ev, int ms) { _setTimeline(_sys, h, ms); Log2("seek", ev, h, ms.ToString()); }
    public static int GetTimeline(uint h) { int ms = 0; _getTimeline(_sys, h, &ms); return ms; }
    public static void Set3D(uint h, float px, float py, float pz, float vx, float vy, float vz)
    {
        var p = new Vec3 { X = px, Y = py, Z = pz }; var v = new Vec3 { X = vx, Y = vy, Z = vz };
        _set3d(_sys, h, &p, &v);
    }
    public static void Listener(Vec3 pos, Vec3 vel, Vec3 fwd, Vec3 up) => _setListener(_sys, &pos, &vel, &fwd, &up);
    public static void Preload(int ev) => _preload(_sys, ev, 0);
    public static void Unload(int ev) => _unload(_sys, ev);
    public static void Global(string name, float v)
    {
        int r; fixed (byte* p = Utf8(name)) r = _setGlobal(_sys, p, v);
        if (r != 0 && GlobalNotInBake.Add(name) && Trace) Log.Info($"[audio] global parameter '{name}' is not used by any baked event (stored only)");
        Log2("global", -1, 0, name + "=" + v.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public static bool BusKnown(byte[] path) { fixed (byte* p = path) return _busFind(_sys, p) >= 0; }
    public static void BusVolume(byte[] path, float v) { fixed (byte* p = path) _busSetVolume(_sys, p, v); Log2("bus-volume", -1, 0, BusName(path) + "=" + v.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    public static void BusMute(byte[] path, bool m) { fixed (byte* p = path) _busSetMute(_sys, p, m ? 1 : 0); Log2("bus-mute", -1, 0, BusName(path) + "=" + (m ? 1 : 0)); }
    public static void BusPaused(byte[] path, bool m) { fixed (byte* p = path) _busSetPaused(_sys, p, m ? 1 : 0); Log2("bus-paused", -1, 0, BusName(path) + "=" + (m ? 1 : 0)); }
    static string BusName(byte[] path) => Encoding.UTF8.GetString(path, 0, path.Length - 1);
    public static void BusStopAll(byte[] path, bool fade) { fixed (byte* p = path) _busStopAll(_sys, p, fade ? 1 : 0); Log2("bus-stop-all", -1, 0, Encoding.UTF8.GetString(path, 0, path.Length - 1) + (fade ? " fade" : " immediate")); }

    public static void Log2(string what, int ev, uint h, string value)
    {
        if (Trace) Log.Info($"[audio] f{Frame} {what} {(ev >= 0 ? Events[ev].Path : "")} #{h:X} {value}");
        var w = _events; if (w == null) return;
        lock (w) w.WriteLine($"{Frame}\t{AudioFrame}\t{what}\t{(ev >= 0 ? Events[ev].Path : "")}\t{h:X}\t{value}");
    }

    /// <summary>Studio::System::update: sa_update, then the queued notifications (in order) to <paramref name="deliver"/> (the stubs map
    /// handles to their objects and run FMOD's callbacks), then -- file/null outputs -- this frame's audio.</summary>
    public static void Update(Action<uint, uint> deliver)
    {
        if (!On) return;
        long t0 = Stopwatch.GetTimestamp();
        _update(_sys);
        Notification* buf = stackalloc Notification[64];
        for (int n; (n = _poll(_sys, buf, 64)) > 0;)
            for (int k = 0; k < n; k++)
            {
                if (_events != null && (buf[k].Type & 0x22) != 0) Log2(buf[k].Type == 0x20 ? "STOPPED" : "DESTROYED", buf[k].Event, buf[k].Instance, "");
                if (buf[k].Type == 0x20 && (uint)buf[k].Event < (uint)Events.Length) Events[buf[k].Event].Stopped++;
                if (buf[k].Type == 0x02 && (uint)buf[k].Event < (uint)Events.Length) Events[buf[k].Event].Destroyed++;
                deliver(buf[k].Instance, buf[k].Type);
            }
        long t1 = Stopwatch.GetTimestamp();
        if (_out is "file" or "null" or "null-fallback")
        {
            var T = Shepherd.Engine.Runtime.Time.Realtime;   // headless: the frame clock (frames / 60); window: the wall clock
            double dt = Shepherd.Engine.Runtime.Time.CaptureDeltaTime > 0 ? Shepherd.Engine.Runtime.Time.CaptureDeltaTime : Shepherd.Engine.Runtime.Time.UnscaledDeltaTime;
            long target = (long)Math.Round((T + dt) * _rate);
            if (_firstFrame < 0) { _firstFrame = Frame; Log.Info($"[audio] first Studio update at frame {Frame} (engine time {T:F4} s): the WAV starts at engine time 0, sample s = frame s/{_rate * dt:F0}"); }
            while (_rendered < target)
            {
                int n = (int)Math.Min(target - _rendered, 48000);
                if (_out == "file") _fileWrite(_sys, n); else NullMix(n);   // null / null-fallback: mixed and discarded
                _rendered += n;
            }
        }
        long t2 = Stopwatch.GetTimestamp();
        _updTicks += t1 - t0; _mixTicks += t2 - t1; _frames++;
        if (FrameMs.Count < 100000) FrameMs.Add((float)((t2 - t0) * 1000.0 / Stopwatch.Frequency));
        if (_events != null && _frames % 50 == 0) lock (_events) _events.Flush();
    }

    static float[] _nullBuf;
    static delegate* unmanaged<IntPtr, float*, int, int> _mix;
    static void NullMix(int n)
    {
        if (_mix == null) _mix = (delegate* unmanaged<IntPtr, float*, int, int>)Proc("sa_mix");
        _nullBuf ??= new float[2 * 4096];
        fixed (float* p = _nullBuf) for (int d = 0; d < n; d += 4096) _mix(_sys, p, Math.Min(4096, n - d));
    }

    static double[] Stats()
    {
        var v = new double[40];
        fixed (double* p = v) _statsV(_sys, p, 40);   // (30: the output's failed sceAudioOutOutput calls, 31: the first one's code; kit #3 review)
        return v;
    }

    /// <summary>The heartbeat part (Vita host): voices, streams, instances, cache, underruns, the output thread's CPU share.</summary>
    public static string Note()
    {
        if (!On) return BakeDir != null ? " | audio " + Status : "";
        var v = Stats();
        double mixSec = v[24], dmix = mixSec - _lastMixSec; _lastMixSec = mixSec;
        var pool = PoolLine();
        // kit3 play mode: the audio clock against the wall clock since the last heartbeat (vita / rtfile: 1.00x = the hardware's pace)
        double wall = _clockSw?.Elapsed.TotalSeconds ?? 0, dw = wall - _noteWall; long fr = (long)v[23], dfr = fr - _noteFrames; _noteWall = wall; _noteFrames = fr;
        string clock = dw > 0.5 ? $", audio clock {dfr / (double)_rate:F1} s in {dw:F1} s ({dfr / (_rate * dw):F2}x real time)" : "";
        return $" | audio: {v[0]:F0} voices ({v[3]:F0} virtual, {v[2]:F0} tails), {v[6]:F0} streams, {v[4]:F0} instances, cache {v[17] / 1024:F0}/{v[18] / 1024:F0} KiB (peak {v[29] / 1024:F0}), " +
               $"underruns {v[12]:F0}, load failures {v[16]:F0}, mix {mixSec:F2} s total (+{dmix * 1000:F0} ms){clock}, limiter {v[26]:F0} frames{(pool.Length > 0 ? ", pool " + pool : "")}{OutErrors(v)}";
    }

    /// <summary>kit #3 review: the Vita output's refused grains (sa_out_vita.c keeps the audio clock on real time through them; their sound
    /// is lost). Empty when there were none.</summary>
    static string OutErrors(double[] v) => v[30] > 0 ? $", OUTPUT ERRORS {v[30]:F0} (sceAudioOutOutput refused grains; first 0x{(uint)(int)v[31]:x8})" : "";

    static string PoolLine()
    {
        if (_poolStats == null) return "";
        uint* s = stackalloc uint[6];
        _poolStats(s, 6);   // total KiB, used KiB, peak KiB, allocs, frees, failures
        return $"{s[1]}/{s[0]} KiB (peak {s[2]}, {s[5]} failed)";
    }

    /// <summary>End of the run: stop the output, close the file, log the numbers.</summary>
    public static string Finish(IEnumerable<string> leakLines)
    {
        if (!On) return BakeDir != null ? "[audio] " + Status : null;
        var v = Stats();
        var sb = new StringBuilder();
        if (_out == "vita") _vitaOutStop(_sys);
        if (_out == "file") _fileClose(_sys);
        double* rt = stackalloc double[6];
        if (_out == "rtfile") _rtStop(_sys, rt);
        double f = Stopwatch.Frequency / 1000.0;
        var ms = FrameMs.ToArray(); Array.Sort(ms);
        float P(double q) => ms.Length == 0 ? 0 : ms[Math.Min(ms.Length - 1, (int)(q * ms.Length))];
        sb.Append($"[audio] end: {_frames} Studio updates, output {_out}{(_wav != null ? " " + _wav : "")}, {v[23]:F0} frames mixed ({v[23] / _rate:F2} s)");
        if (_out == "rtfile")
            sb.Append($"\n[audio] rt output (the Vita's sceAudioOut pace): {rt[0]:F0} frames = {rt[1]:F3} s of audio in {rt[2]:F3} s of wall time ({rt[3]:F4}x), worst lateness {rt[4] * 1000:F2} ms, {rt[5]:F0} late grain(s)");
        int nu = (int)Math.Max(1, _frames);
        sb.Append($"\n[audio] cpu per Studio update: update+notifications {_updTicks / f / nu:F4} ms (sa_update, poll, callbacks: this thread); " +
                  (_out is "vita" or "rtfile" ? $"output thread: mixer {v[24] * 1000 / nu:F4} ms per update ({v[24] * 1000 / Math.Max(1e-9, v[23] / _rate):F2} ms per second of audio)"
                                  : $"output in-line: {_mixTicks / f / nu:F4} ms, of which the mixer {(v[24] - v[25]) * 1000 / nu:F4} ms and SYNC_IO AAC decode {v[25] * 1000 / nu:F4} ms (AudioToolbox; sceAudiodec on the Vita's IO thread)") +
                  $"; total p50 {P(0.5):F3} p99 {P(0.99):F3} max {P(1.0):F3} ms");
        sb.Append($"\n[audio] mixer: steals {v[8]:F0}, virtualized {v[9]:F0}, devirtualized {v[10]:F0}, hard cuts {v[11]:F0}, underruns {v[12]:F0}, cache hits {v[13]:F0} / misses {v[14]:F0} / evictions {v[15]:F0}, " +
                  $"load failures {v[16]:F0}, cache peak {v[29] / 1048576:F2} MiB of {v[18] / 1048576:F0}, cmd queue peak {v[19]:F0} (dropped {v[20]:F0}), notifications dropped {v[21]:F0}");
        if (OutErrors(v) is { Length: > 0 } oe) sb.Append($"\n[audio] output:{oe.TrimStart(',')}");
        sb.Append($"\n[audio] limiter: {v[26]:F0} frames turned down, min gain {v[27]:F4} ({20 * Math.Log10(Math.Max(1e-9, v[27])):F2} dB), pre-limiter peak {v[28]:F4} ({20 * Math.Log10(Math.Max(1e-9, v[28])):F2} dBFS)");
        if (PoolLine() is { Length: > 0 } pool) sb.Append($"\n[audio] host pool: {pool}");
        sb.Append($"\n[audio] routed {Routed} instances to shepaudio, {Unbaked} to the silent stub (not in the bake)");
        foreach (var l in leakLines) sb.Append("\n[audio] ").Append(l);
        if (_events != null) lock (_events) { _events.Flush(); _events.Dispose(); _events = null; }
        return sb.ToString();
    }
}
