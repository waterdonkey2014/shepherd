using System.Runtime.InteropServices;
using System.Text;
using FMOD;
using FMOD.Studio;
using Shepherd.Engine;
namespace Shepherd.Audio;

/// <summary>Silent FMOD for the Vita profile (V4). tools/Rewriter --vita (pass 3) turns every FMODUnity P/Invoke into
/// `Icalls.Call(id, args)` keyed "fmodstudio!&lt;entry point&gt;"; these are the implementations. No native library, no
/// banks read (the bank key never enters the engine), no audio. Semantics the game depends on:
/// - every create/get returns FMOD_OK and a non-zero, never-reused handle (kind &lt;&lt; 24 | serial: fits a 32-bit IntPtr);
///   IsValid is true until the object is released/unloaded/destroyed.
/// - banks load synchronously and report LOADED (bank + sample data), so RuntimeManager.HaveAllBanksLoaded /
///   HaveMasterBanksLoaded and AudioManager.Start's wait pass on the first frame. Any path under event:/ snapshot:/ bus:/
///   vca:/ bank:/ resolves: LookupID derives a stable GUID from the path (FNV-1a), GetEventByID maps it back.
/// - instances: start() -> STARTING; at the next Studio update -> PLAYING (+STARTING/STARTED callbacks). Then
///   * "one-shot-like" instances — released by the game, or with a STOPPED callback (theirs or their description's) —
///     STOP in that same update and fire STOPPED, so AudioManager.OneShotInstanceCleanup frees the FMODOneShotWrapper
///     GCHandle exactly like on real FMOD; released + stopped instances are then destroyed (DESTROYED callback).
///   * held instances (loops, music, snapshots: not released, no STOPPED callback) keep PLAYING until stop(), so
///     "if (!IsEventInstancePlaying(loop)) CreateLoop()" code doesn't spawn a new instance every frame.
///   SHEPHERD_FMOD_STOP=all makes every instance stop at the first update (strict "zero-length event" model).
///   stop() -> STOPPED now, STOPPED callback at the next update (FMOD's deferred-callback model: Studio is
///   initialised with DEFERRED_CALLBACKS, so all callbacks run on the main thread inside update()).
/// - parameters/volume/pitch/paused/3D/timeline/user data are stored and read back; nothing else happens. kit3 play mode: with the wall
///   clock (Runtime/PlayClock.cs) a playing, unpaused stub instance's timeline position advances on the audio clock
///   (ShepAudio.ClockSeconds: the output's hardware pace, or the wall clock without the bake), as FMOD's does in real time, not on
///   frames; start() after a stop() begins at 0 again (or where setTimelinePosition put it). The frame clock keeps the stored value.
/// - System/Studio error and debug callbacks are accepted and never invoked.
/// Unimplemented entries return default (FMOD_OK, zeroed outs) and are listed in missing-icalls.txt.
/// No field here has an FMOD value type: IcallRegistry reflects over this class in the default profile too, and that must
/// not load FMODUnity early.
/// Stage C (vita/notes/m1-audio.md): with SHEPHERD_AUDIO_BAKE set, descriptions and instances of events in the bake are backed by
/// shepaudio (<see cref="ShepAudio"/>): real GUIDs, the bake's description data, and every instance call (start/stop with the authored
/// release/release/parameters/3D/volume/pitch/pause/timeline) is forwarded; the playback state and the callbacks (STARTING, STARTED,
/// RESTARTED, STOPPED at the bake's stopFrame, DESTROYED, virtual changes) come from shepaudio's state machine, delivered in each
/// Studio update like DEFERRED_CALLBACKS. Buses, global parameters, the listener and snapshots go to the mixer too. Events that are
/// not in the bake keep the silent stub below.</summary>
[ProfileOnly]
static class Fmod
{
    const string P = "vita";
    static readonly object OK = RESULT.OK, NotFound = RESULT.ERR_EVENT_NOTFOUND, BadHandle = RESULT.ERR_INVALID_HANDLE,
        Truncated = RESULT.ERR_TRUNCATED, AlreadyLoaded = RESULT.ERR_EVENT_ALREADY_LOADED, BadParam = RESULT.ERR_INVALID_PARAM, True = true, False = false;
    public static readonly bool StopAll = Environment.GetEnvironmentVariable("SHEPHERD_FMOD_STOP") == "all";

    enum K : byte { Studio = 1, Core, Bank, Bus, Vca, Desc, Inst, ChannelGroup, Dsp, Sound, Channel, Other }
    const int Playing = 0, Stopped = 2, Starting = 3;   // PLAYBACK_STATE
    sealed class Obj
    {
        public K Kind; public IntPtr H; public string Path; public int G0, G1, G2, G3;
        public IntPtr UserData; public Delegate Callback; public uint Mask;
        public Obj Desc; public int Instances;                         // instance -> description; description -> live instance count
        public Obj Sub;                                                // its channel group (instance, bus) / DSP (channel group): created once
        public int State = Stopped; public bool PendingStart, PendingStopped, Released, Destroyed, Paused, Mute;
        public float Volume = 1, Pitch = 1; public int Timeline; public uint ListenerMask = 0xFFFFFFFF;
        public Dictionary<string, float> Params; public Dictionary<ulong, float> ParamsById; public float[] Props;
        public int Ev = -1; public uint Sa; public bool Virtual; public int BusBaked = -2; public byte[] Utf8;   // Stage C: shepaudio backing
        public double TlAt = -1; public bool TlRestart;   // kit3 play mode (wall clock): a silent stub's timeline runs on the audio clock from TlAt
    }
    static readonly object Gate = new();
    static readonly Dictionary<IntPtr, Obj> Objs = new();
    static readonly Dictionary<string, Obj> ByPath = new(StringComparer.Ordinal);            // "<kind>|<lower-case path>"
    static readonly Dictionary<(int, int, int, int), Obj> ByGuid = new();
    static readonly Dictionary<(int, int, int, int), string> GuidPaths = new();
    static readonly List<Obj> Live = new();                                                   // undestroyed instances
    static readonly List<Obj> Banks = new();
    static readonly Dictionary<string, float> Globals = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<ulong, float> GlobalsById = new();
    static readonly Dictionary<ulong, string> ParamNames = new();
    static readonly Dictionary<string, IntPtr> NativeNames = new(StringComparer.Ordinal);    // interned UTF-8 for PARAMETER_DESCRIPTION.name
    static readonly Dictionary<uint, Obj> BySa = new();                                       // Stage C: shepaudio handle -> instance
    static readonly List<(Obj, uint)> FireList = new();
    static readonly List<Obj> DeadList = new();
    static object _listener;                                                                  // the last ATTRIBUTES_3D (boxed: no FMOD-typed field)
    static readonly HashSet<string> UnbakedSeen = new(StringComparer.OrdinalIgnoreCase);
    static int _serial;
    public static long Updates, Created, Starts, Stops, Callbacks, Destroys, BankLoads;

    // ---------------- helpers ----------------
    static Obj New(K k, string path = null)
    {
        var o = new Obj { Kind = k, H = (IntPtr)(((int)k << 24) | (Interlocked.Increment(ref _serial) & 0xFFFFFF)), Path = path };
        Objs[o.H] = o; return o;
    }
    static Obj Get(object h, K k) => h is IntPtr p && Objs.TryGetValue(p, out var o) && o.Kind == k && !o.Destroyed ? o : null;
    static Obj Any(object h) => h is IntPtr p && Objs.TryGetValue(p, out var o) && !o.Destroyed ? o : null;
    static string Str(object o)
    {
        if (o is not byte[] b) return null;
        int n = Array.IndexOf(b, (byte)0); return Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
    }
    static uint Mask(object o) => o is Enum e ? (uint)Convert.ToInt64(e) : 0;
    static ulong Pid(object o) => o is PARAMETER_ID id ? id.data1 | ((ulong)id.data2 << 32) : 0;
    static PARAMETER_ID ToPid(ulong v) => new PARAMETER_ID { data1 = (uint)v, data2 = (uint)(v >> 32) };
    static (int, int, int, int) GKey(object g) => g is GUID x ? (x.Data1, x.Data2, x.Data3, x.Data4) : default;
    static GUID ToGuid(Obj o) => new GUID { Data1 = o.G0, Data2 = o.G1, Data3 = o.G2, Data4 = o.G3 };
    static (int, int, int, int) HashGuid(string s)
    {
        ulong h1 = 14695981039346656037UL, h2 = 0x9E3779B97F4A7C15UL;
        foreach (var c in s.ToLowerInvariant()) { h1 = (h1 ^ c) * 1099511628211UL; h2 = ((h2 ^ c) * 0x100000001B3UL) ^ (h2 >> 29); }
        if (h1 == 0 && h2 == 0) h1 = 1;
        return ((int)h1, (int)(h1 >> 32), (int)h2, (int)(h2 >> 32));
    }
    static ulong HashParam(string s) { ulong h = 14695981039346656037UL; foreach (var c in s.ToLowerInvariant()) h = (h ^ c) * 1099511628211UL; return h == 0 ? 1 : h; }
    static string Prefix(K k) => k switch { K.Bus => "bus:/", K.Vca => "vca:/", K.Bank => "bank:/", _ => "event:/" };
    static bool PathOk(string p, K k) => !string.IsNullOrEmpty(p) && (k == K.Desc ? p.StartsWith("event:/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("snapshot:/", StringComparison.OrdinalIgnoreCase) : p.StartsWith(Prefix(k), StringComparison.OrdinalIgnoreCase));
    /// <summary>The object of kind k at path (created on first use: every path "exists" because every bank is "loaded").</summary>
    static Obj ByPathOf(K k, string path)
    {
        var key = (int)k + "|" + path.ToLowerInvariant();
        if (ByPath.TryGetValue(key, out var o)) return o;
        o = New(k, path);
        int ev = k == K.Desc && ShepAudio.On ? ShepAudio.FindEvent(path) : -1;
        if (ev >= 0) { var x = ShepAudio.Events[ev]; (o.G0, o.G1, o.G2, o.G3) = (x.G0, x.G1, x.G2, x.G3); o.Ev = ev; }   // the bake's (FMOD's) GUID
        else (o.G0, o.G1, o.G2, o.G3) = HashGuid(path);
        ByPath[key] = o; ByGuid[(o.G0, o.G1, o.G2, o.G3)] = o; GuidPaths[(o.G0, o.G1, o.G2, o.G3)] = path; return o;
    }
    static Obj ByGuidOf(K k, (int, int, int, int) g)
    {
        if (g == default) return null;
        if (ByGuid.TryGetValue(g, out var o) && o.Kind == k) return o;
        if (GuidPaths.TryGetValue(g, out var p)) return ByPathOf(k, p);
        if (k == K.Desc && ShepAudio.On && ShepAudio.FindEvent(g) is var ev and >= 0) return ByPathOf(k, ShepAudio.Events[ev].Path);   // an EventReference's GUID
        o = New(k, null); (o.G0, o.G1, o.G2, o.G3) = g; ByGuid[g] = o; return o;   // a GUID from serialized data (EventReference) with no path seen yet
    }
    static string PathOf(Obj o) => o.Path ?? ((Guid)ToGuid(o)).ToString("B");
    /// <summary>FMOD's "copy a string into (ptr, size), report the size incl. NUL" convention.</summary>
    static object PutStr(string s, object ptr, object size, object[] a, int retrievedIdx)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? ""); int need = bytes.Length + 1; a[retrievedIdx] = need;
        var p = ptr is IntPtr ip ? ip : IntPtr.Zero; int sz = size is int i ? i : 0;
        if (p == IntPtr.Zero || sz <= 0) return OK;
        if (sz < need) { Marshal.Copy(bytes, 0, p, sz - 1); Marshal.WriteByte(p, sz - 1, 0); return Truncated; }
        Marshal.Copy(bytes, 0, p, bytes.Length); Marshal.WriteByte(p, bytes.Length, 0); return OK;
    }
    static object FillList(IEnumerable<Obj> items, object arr, object capacity, object[] a, int countIdx)
    {
        var list = items.ToList(); int cap = capacity is int c ? c : 0; int n = 0;
        if (arr is IntPtr[] dst) for (; n < list.Count && n < cap && n < dst.Length; n++) dst[n] = list[n].H;
        a[countIdx] = n; return OK;
    }
    static IntPtr NativeName(string s)
    {
        if (NativeNames.TryGetValue(s, out var p)) return p;
        var b = Encoding.UTF8.GetBytes(s); p = Marshal.AllocHGlobal(b.Length + 1); Marshal.Copy(b, 0, p, b.Length); Marshal.WriteByte(p, b.Length, 0);
        return NativeNames[s] = p;   // lives for the process: one per distinct parameter name
    }
    static object ParamDesc(string name, ulong id, object[] a, int outIdx)
    {
        if (name == null) return NotFound;
        var d = new PARAMETER_DESCRIPTION { name = new StringWrapper(NativeName(name)), id = ToPid(id), minimum = 0, maximum = 1, defaultvalue = 0 };
        a[outIdx] = d; return OK;
    }

    // ---------------- instance state machine ----------------
    static bool WantsStop(Obj i)
    {
        if (StopAll || i.Released) return true;
        if (i.Callback != null) return (i.Mask & (uint)EVENT_CALLBACK_TYPE.STOPPED) != 0;
        return i.Desc?.Callback != null && (i.Desc.Mask & (uint)EVENT_CALLBACK_TYPE.STOPPED) != 0;
    }
    static void Fire(Obj i, EVENT_CALLBACK_TYPE type)
    {
        var cb = i.Callback as EVENT_CALLBACK; uint mask = i.Mask;
        if (cb == null && i.Desc != null) { cb = i.Desc.Callback as EVENT_CALLBACK; mask = i.Desc.Mask; }
        if (cb == null || (mask & (uint)type) == 0) return;
        Interlocked.Increment(ref Callbacks);
        try { cb(type, i.H, IntPtr.Zero); } catch (Exception e) { Log.Exception(e, $"FMOD event callback {type} '{(i.Desc != null ? PathOf(i.Desc) : "?")}'"); }
    }
    /// <summary>Studio::System::update — the only place callbacks run (main thread), like DEFERRED_CALLBACKS.</summary>
    static void Pump()
    {
        Updates++;
        var fire = FireList; fire.Clear();
        lock (Gate)
        {
            foreach (var i in Live)
            {
                if (i.Sa != 0) continue;   // Stage C: shepaudio's state machine (ShepAudio.Update below)
                if (i.PendingStart)
                {
                    i.PendingStart = false; i.State = Playing; fire.Add((i, (uint)EVENT_CALLBACK_TYPE.STARTING)); fire.Add((i, (uint)EVENT_CALLBACK_TYPE.STARTED));
                    if (Shepherd.Engine.Runtime.Time.Wall) TlRun(i);
                    if (WantsStop(i)) { i.State = Stopped; i.PendingStopped = false; Stops++; fire.Add((i, (uint)EVENT_CALLBACK_TYPE.STOPPED)); TlFreeze(i); }
                }
                else if (i.PendingStopped) { i.PendingStopped = false; fire.Add((i, (uint)EVENT_CALLBACK_TYPE.STOPPED)); }
                else if (i.State == Playing && WantsStop(i)) { i.State = Stopped; Stops++; fire.Add((i, (uint)EVENT_CALLBACK_TYPE.STOPPED)); TlFreeze(i); }   // released (or got a STOPPED callback) after it started
            }
        }
        for (int k = 0; k < fire.Count; k++) { var (i, t) = fire[k]; if (!i.Destroyed) Fire(i, (EVENT_CALLBACK_TYPE)t); }   // outside the lock: callbacks call back into FMOD
        fire.Clear();
        var dead = DeadList; dead.Clear();
        lock (Gate) { foreach (var i in Live) if (i.Sa == 0 && i.Released && i.State == Stopped && !i.PendingStart && !i.PendingStopped) dead.Add(i); }
        foreach (var i in dead) { Fire(i, EVENT_CALLBACK_TYPE.DESTROYED); Destroy(i); }
        dead.Clear();
        if (ShepAudio.On) ShepAudio.Update(Deliver);   // Stage C: sa_update, its notifications (callbacks), this frame's audio (file/null output)
    }
    /// <summary>Stage C: one shepaudio notification for a baked instance: the FMOD callback (if the game asked for that type), then the
    /// bookkeeping (STOPPED counts, DESTROYED removes the handle).</summary>
    static readonly Action<uint, uint> Deliver = (h, type) =>
    {
        Obj i; lock (Gate) if (!BySa.TryGetValue(h, out i)) return;
        if (type == (uint)EVENT_CALLBACK_TYPE.STOPPED) Interlocked.Increment(ref Stops);
        else if (type == (uint)EVENT_CALLBACK_TYPE.REAL_TO_VIRTUAL) i.Virtual = true;
        else if (type == (uint)EVENT_CALLBACK_TYPE.VIRTUAL_TO_REAL) i.Virtual = false;
        if (!i.Destroyed) Fire(i, (EVENT_CALLBACK_TYPE)type);
        if (type == (uint)EVENT_CALLBACK_TYPE.DESTROYED) Destroy(i);
    };
    static void Destroy(Obj i)
    {
        lock (Gate) { if (i.Destroyed) return; i.Destroyed = true; Live.Remove(i); Objs.Remove(i.H); if (i.Sub != null) Objs.Remove(i.Sub.H); if (i.Desc != null) i.Desc.Instances--; if (i.Sa != 0) BySa.Remove(i.Sa); Destroys++; }
    }
    static void StopInst(Obj i)
    {
        if (i.PendingStart) { i.PendingStart = false; i.State = Stopped; i.PendingStopped = true; Stops++; return; }
        if (i.State != Stopped) { i.State = Stopped; i.PendingStopped = true; Stops++; TlFreeze(i); }
    }
    // ---- kit3 play mode: a silent stub's timeline on the audio clock (wall clock only; TlAt stays -1 with the frame clock) ----
    static void TlRun(Obj i)
    {
        if (i.TlRestart) { i.Timeline = 0; i.TlRestart = false; }   // start() after a stop: from the beginning (unless setTimelinePosition moved it)
        i.TlAt = i.Paused ? -1 : ShepAudio.ClockSeconds;
    }
    static int TlNow(Obj i) => i.TlAt < 0 ? i.Timeline : i.Timeline + (int)Math.Floor((ShepAudio.ClockSeconds - i.TlAt) * 1000);
    static void TlFreeze(Obj i) { if (i.TlAt >= 0) { i.Timeline = TlNow(i); i.TlAt = -1; i.TlRestart = true; } }

    public static string Summary()
    {
        var audio = ShepAudio.BakeDir != null ? ShepAudio.Finish(LeakLines()) : null;
        lock (Gate)
            return (audio != null ? audio + "\n" : "") + $"[fmod] {(ShepAudio.On ? "stubs + shepaudio" : "silent stubs")}: {Updates} updates, {BankLoads} bank loads ({Banks.Count} loaded), {ByPath.Count(kv => kv.Value.Kind == K.Desc)} event paths, " +
                   $"{Created} instances created / {Starts} starts / {Stops} stops / {Destroys} destroyed / {Live.Count} alive " +
                   $"({Live.Count(i => (i.Sa != 0 ? ShepAudio.State(i.Sa) : i.State) == Playing)} playing), {Callbacks} callbacks fired{(StopAll ? " [SHEPHERD_FMOD_STOP=all]" : "")}" +
                   (Shepherd.Engine.Runtime.Time.Wall && !ShepAudio.On ? StubTimelines() : "");
    }
    /// <summary>kit3 play mode, wall clock without the bake: the playing stub instances' timelines (they run on the audio clock).</summary>
    static string StubTimelines()
    {
        var p = Live.Where(i => i.Sa == 0 && i.State == Playing && i.Desc != null).Take(8).Select(i => $"{PathOf(i.Desc)} {TlNow(i)} ms").ToList();
        return p.Count == 0 ? "" : $"\n[fmod] wall clock: playing stub timelines (audio clock {ShepAudio.ClockSeconds:F2} s): {string.Join(", ", p)}";
    }
    /// <summary>Stage C: the instances still alive at the end, by event, with their state: held loops/music/snapshots are expected;
    /// a one-shot that stopped but was never released, or one still playing, is a leak candidate.</summary>
    static IEnumerable<string> LeakLines()
    {
        var lines = new List<string>();
        lock (Gate)
        {
            var baked = Live.Where(i => i.Sa != 0).ToList();
            lines.Add($"alive at the end: {baked.Count} shepaudio instances, {Live.Count - baked.Count} stub instances; created {ShepAudio.Events.Sum(e => e?.Created ?? 0)}, " +
                      $"STOPPED delivered {ShepAudio.Events.Sum(e => e?.Stopped ?? 0)}, DESTROYED {ShepAudio.Events.Sum(e => e?.Destroyed ?? 0)}");
            int leaks = 0;
            foreach (var g in baked.GroupBy(i => (i.Desc.Ev, St: ShepAudio.State(i.Sa), i.Released)).OrderBy(g => ShepAudio.Events[g.Key.Ev].Path, StringComparer.Ordinal))
            {
                var x = ShepAudio.Events[g.Key.Ev];
                string st = g.Key.St switch { 0 => "PLAYING", 2 => "STOPPED", 3 => "STARTING", 4 => "STOPPING", _ => g.Key.St.ToString() };
                string kind = x.Snapshot ? "snapshot" : x.Oneshot ? "one-shot" : "loop/held";
                // held: snapshots and loops the game keeps; in flight: a one-shot still sounding (it STOPs + DESTROYs by itself);
                // a released instance that is STOPPED but alive would be the bridge's leak; an unreleased stopped one is the game's (as on FMOD)
                string cls = x.Snapshot || (!x.Oneshot && g.Key.St != 2) ? "held     " : g.Key.St != 2 ? "in flight" : g.Key.Released ? "LEAK     " : "unreleased";
                if (cls.StartsWith("LEAK")) leaks += g.Count();
                var tl = x.Oneshot && g.Key.St != 2 ? $", timeline {string.Join("/", g.Select(i => ShepAudio.GetTimeline(i.Sa)))} ms of {(x.LengthMs > 0 ? x.LengthMs + " ms" : "?")}" : "";
                lines.Add($"  {cls} {g.Count()} x {x.Path} ({kind}) {st}{(g.Key.Released ? " released" : "")}{tl}");
            }
            lines.Insert(1, $"bridge leaks (released + STOPPED but not destroyed): {leaks}");
            foreach (var g in Live.Where(i => i.Sa == 0).GroupBy(i => i.Desc != null ? PathOf(i.Desc) : "?"))
                lines.Add($"  stub    {g.Count()} x {g.Key} (not in the bake){(Shepherd.Engine.Runtime.Time.Wall ? $", timeline {string.Join("/", g.Select(TlNow))} ms on the audio clock" : "")}");
        }
        return lines;
    }
    static void NoteUnbaked(Obj d)
    {
        var p = PathOf(d);
        if (UnbakedSeen.Add(p)) { ShepAudio.Log2("unbaked", -1, 0, p); Log.Info($"[audio] not in the bake: {p} (silent stub)"); }
    }

    // ================= Studio::System =================
    [Icall("fmodstudio!FMOD_Studio_System_Create", Profile = P)] static object SysCreate(object[] a)
    {
        lock (Gate) a[0] = New(K.Studio).H;
        ShepAudio.Init();   // Stage C: SHEPHERD_AUDIO_BAKE -> shepaudio; else nothing
        Log.Info(ShepAudio.On ? "[fmod] Studio::System::create -> stub system backed by shepaudio (no fmodstudio library)" : "[fmod] Studio::System::create -> silent stub system (no fmodstudio library)");
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_System_IsValid", Profile = P)] static object SysValid(object[] a) { lock (Gate) return Get(a[0], K.Studio) != null ? True : False; }
    [Icall("fmodstudio!FMOD_Studio_System_GetCoreSystem", Profile = P)] static object SysCore(object[] a)
    {
        lock (Gate) { var s = Get(a[0], K.Studio); if (s == null) return BadHandle; a[1] = SubOf(s, K.Core); return OK; }
    }
    [Icall("fmodstudio!FMOD_Studio_System_Initialize", Profile = P)] static object SysInit(object[] a) => OK;
    [Icall("fmodstudio!FMOD_Studio_System_Release", Profile = P)] static object SysRelease(object[] a) { lock (Gate) { var s = Get(a[0], K.Studio); if (s != null) { s.Destroyed = true; Objs.Remove(s.H); } } return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_Update", Profile = P)] static object SysUpdate(object[] a) { Pump(); return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_FlushCommands", Profile = P)] static object SysFlush(object[] a) => OK;
    [Icall("fmodstudio!FMOD_Studio_System_FlushSampleLoading", Profile = P)] static object SysFlushSamples(object[] a) => OK;
    [Icall("fmodstudio!FMOD_Studio_System_SetAdvancedSettings", Profile = P)] static object SysAdv(object[] a) => OK;   // carries the bank key: never stored, never logged
    [Icall("fmodstudio!FMOD_Studio_System_SetCallback", Profile = P)] static object SysCallback(object[] a) { lock (Gate) { var s = Get(a[0], K.Studio); if (s != null) { s.Callback = a[1] as Delegate; s.Mask = Mask(a[2]); } } return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_SetUserData", Profile = P)] static object SysSetUD(object[] a) { lock (Gate) { var s = Any(a[0]); if (s != null) s.UserData = (IntPtr)a[1]; } return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_GetUserData", Profile = P)] static object SysGetUD(object[] a) { lock (Gate) a[1] = Any(a[0])?.UserData ?? IntPtr.Zero; return OK; }
    static object LoadBank(string name, object[] a, int outIdx)
    {
        lock (Gate)
        {
            BankLoads++;
            var key = (int)K.Bank + "|" + name.ToLowerInvariant();
            if (ByPath.TryGetValue(key, out var existing) && !existing.Destroyed && Banks.Contains(existing)) { a[outIdx] = existing.H; return AlreadyLoaded; }
            var b = New(K.Bank, name); (b.G0, b.G1, b.G2, b.G3) = HashGuid(name); ByPath[key] = b; ByGuid[(b.G0, b.G1, b.G2, b.G3)] = b; GuidPaths[(b.G0, b.G1, b.G2, b.G3)] = name;
            Banks.Add(b); a[outIdx] = b.H; return OK;
        }
    }
    static string BankPath(string file) => "bank:/" + Path.GetFileNameWithoutExtension(file ?? "");
    [Icall("fmodstudio!FMOD_Studio_System_LoadBankFile", Profile = P)] static object LoadFile(object[] a) => LoadBank(BankPath(Str(a[1])), a, 3);
    [Icall("fmodstudio!FMOD_Studio_System_LoadBankMemory", Profile = P)] static object LoadMem(object[] a) => LoadBank("bank:/memory#" + BankLoads, a, 5);
    [Icall("fmodstudio!FMOD_Studio_System_LoadBankCustom", Profile = P)] static object LoadCustom(object[] a) => LoadBank("bank:/custom#" + BankLoads, a, 3);
    [Icall("fmodstudio!FMOD_Studio_System_UnloadAll", Profile = P)] static object UnloadAll(object[] a) { lock (Gate) { foreach (var b in Banks) { b.Destroyed = true; Objs.Remove(b.H); } Banks.Clear(); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_GetBankCount", Profile = P)] static object BankCount(object[] a) { lock (Gate) a[1] = Banks.Count; return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_GetBankList", Profile = P)] static object BankList(object[] a) { lock (Gate) return FillList(Banks, a[1], a[2], a, 3); }
    [Icall("fmodstudio!FMOD_Studio_System_GetBank", Profile = P)] static object GetBank(object[] a)
    {
        lock (Gate) { var p = Str(a[1]); if (!PathOk(p, K.Bank) || !ByPath.TryGetValue((int)K.Bank + "|" + p.ToLowerInvariant(), out var b) || b.Destroyed) { a[2] = IntPtr.Zero; return NotFound; } a[2] = b.H; return OK; }
    }
    [Icall("fmodstudio!FMOD_Studio_System_GetBankByID", Profile = P)] static object GetBankId(object[] a) { lock (Gate) { var b = ByGuid.GetValueOrDefault(GKey(a[1])); if (b == null || b.Kind != K.Bank || b.Destroyed) { a[2] = IntPtr.Zero; return NotFound; } a[2] = b.H; return OK; } }
    static object GetByPath(K k, object[] a)
    {
        var p = Str(a[1]);
        if (p != null && p.StartsWith('{') && Guid.TryParse(p, out var g)) { a[1] = new GUID(g); return GetById(k, a); }   // FMOD also takes "{guid}" strings
        lock (Gate) { if (!PathOk(p, k)) { a[2] = IntPtr.Zero; return NotFound; } a[2] = ByPathOf(k, p).H; return OK; }
    }
    static object GetById(K k, object[] a)
    {
        lock (Gate) { var o = ByGuidOf(k, GKey(a[1])); if (o == null) { a[2] = IntPtr.Zero; return NotFound; } a[2] = o.H; return OK; }
    }
    [Icall("fmodstudio!FMOD_Studio_System_GetEvent", Profile = P)] static object GetEvent(object[] a) => GetByPath(K.Desc, a);
    [Icall("fmodstudio!FMOD_Studio_System_GetEventByID", Profile = P)] static object GetEventId(object[] a) => GetById(K.Desc, a);
    [Icall("fmodstudio!FMOD_Studio_System_GetBus", Profile = P)] static object GetBus(object[] a) => GetByPath(K.Bus, a);
    [Icall("fmodstudio!FMOD_Studio_System_GetBusByID", Profile = P)] static object GetBusId(object[] a) => GetById(K.Bus, a);
    [Icall("fmodstudio!FMOD_Studio_System_GetVCA", Profile = P)] static object GetVca(object[] a) => GetByPath(K.Vca, a);
    [Icall("fmodstudio!FMOD_Studio_System_GetVCAByID", Profile = P)] static object GetVcaId(object[] a) => GetById(K.Vca, a);
    [Icall("fmodstudio!FMOD_Studio_System_LookupID", Profile = P)] static object LookupId(object[] a)
    {
        var p = Str(a[1]);
        if (string.IsNullOrEmpty(p) || !(PathOk(p, K.Desc) || PathOk(p, K.Bus) || PathOk(p, K.Vca) || PathOk(p, K.Bank))) { a[2] = default(GUID); return NotFound; }
        var g = HashGuid(p);
        if (ShepAudio.On && ShepAudio.FindEvent(p) is var ev and >= 0) { var x = ShepAudio.Events[ev]; g = (x.G0, x.G1, x.G2, x.G3); }   // Stage C: FMOD's GUID
        lock (Gate) GuidPaths.TryAdd(g, p);
        a[2] = new GUID { Data1 = g.Item1, Data2 = g.Item2, Data3 = g.Item3, Data4 = g.Item4 }; return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_System_LookupPath", Profile = P)] static object LookupPath(object[] a)
    {
        string p; lock (Gate) p = GuidPaths.GetValueOrDefault(GKey(a[1]));
        if (p == null) { a[4] = 0; return NotFound; }
        return PutStr(p, a[2], a[3], a, 4);
    }
    [Icall("fmodstudio!FMOD_Studio_ParseID", Profile = P)] static object ParseId(object[] a)
    {
        var s = Str(a[0]); if (string.IsNullOrEmpty(s) || !Guid.TryParse(s, out var g)) { a[1] = default(GUID); return BadParam; }
        a[1] = new GUID(g); return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_System_SetParameterByName", Profile = P)] static object SysSetP(object[] a) { var n = Str(a[1]); if (n == null) return BadParam; lock (Gate) Globals[n] = (float)a[2]; if (ShepAudio.On) ShepAudio.Global(n, (float)a[2]); return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_SetParameterByNameWithLabel", Profile = P)] static object SysSetPL(object[] a) => OK;
    [Icall("fmodstudio!FMOD_Studio_System_GetParameterByName", Profile = P)] static object SysGetP(object[] a) { var n = Str(a[1]); float v = 0; lock (Gate) if (n != null) Globals.TryGetValue(n, out v); a[2] = v; a[3] = v; return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_SetParameterByID", Profile = P)] static object SysSetPid(object[] a)
    {
        string n; lock (Gate) { GlobalsById[Pid(a[1])] = (float)a[2]; n = ParamNames.GetValueOrDefault(Pid(a[1])); }
        if (ShepAudio.On && n != null) ShepAudio.Global(n, (float)a[2]);
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_System_SetParameterByIDWithLabel", Profile = P)] static object SysSetPidL(object[] a) => OK;
    [Icall("fmodstudio!FMOD_Studio_System_GetParameterByID", Profile = P)] static object SysGetPid(object[] a) { float v = 0; lock (Gate) GlobalsById.TryGetValue(Pid(a[1]), out v); a[2] = v; a[3] = v; return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_SetParametersByIDs", Profile = P)] static object SysSetPids(object[] a)
    {
        if (a[1] is PARAMETER_ID[] ids && a[2] is float[] vs)
            for (int i = 0; i < Math.Min((int)a[3], Math.Min(ids.Length, vs.Length)); i++)
            {
                string n; lock (Gate) { GlobalsById[Pid(ids[i])] = vs[i]; n = ParamNames.GetValueOrDefault(Pid(ids[i])); }
                if (ShepAudio.On && n != null) ShepAudio.Global(n, vs[i]);
            }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_System_GetParameterDescriptionByName", Profile = P)] static object SysPDescN(object[] a)
    {
        var n = Str(a[1]); if (n == null) return NotFound; var id = HashParam(n); lock (Gate) { ParamNames[id] = n; return ParamDesc(n, id, a, 2); }
    }
    [Icall("fmodstudio!FMOD_Studio_System_GetParameterDescriptionByID", Profile = P)] static object SysPDescI(object[] a) { lock (Gate) { var id = Pid(a[1]); return ParamDesc(ParamNames.GetValueOrDefault(id), id, a, 2); } }
    [Icall("fmodstudio!FMOD_Studio_System_GetNumListeners", Profile = P)] static object NumListeners(object[] a) { a[1] = 1; return OK; }
    [Icall("fmodstudio!FMOD_Studio_System_GetListenerWeight", Profile = P)] static object ListenerWeight(object[] a) { a[2] = 1f; return OK; }

    /// <summary>Accepted no-ops (FMOD_OK, zeroed outs) — listed so missing-icalls.txt only shows entries nobody decided on.
    /// Includes both error/debug callback registrations: accepted, never invoked.</summary>
    [Icall("fmodstudio!FMOD5_System_SetOutput", Profile = P)] [Icall("fmodstudio!FMOD5_System_SetSoftwareChannels", Profile = P)]
    [Icall("fmodstudio!FMOD5_System_SetSoftwareFormat", Profile = P)] [Icall("fmodstudio!FMOD5_System_SetDSPBufferSize", Profile = P)]
    [Icall("fmodstudio!FMOD5_System_SetAdvancedSettings", Profile = P)] [Icall("fmodstudio!FMOD5_System_SetCallback", Profile = P)]
    [Icall("fmodstudio!FMOD5_System_Init", Profile = P)] [Icall("fmodstudio!FMOD5_System_Close", Profile = P)] [Icall("fmodstudio!FMOD5_System_Release", Profile = P)]
    [Icall("fmodstudio!FMOD5_System_Update", Profile = P)] [Icall("fmodstudio!FMOD5_System_MixerSuspend", Profile = P)] [Icall("fmodstudio!FMOD5_System_MixerResume", Profile = P)]
    [Icall("fmodstudio!FMOD5_System_GetChannelsPlaying", Profile = P)] [Icall("fmodstudio!FMOD5_Memory_GetStats", Profile = P)]
    [Icall("fmodstudio!FMOD5_Memory_Initialize", Profile = P)] [Icall("fmodstudio!FMOD5_Thread_SetAttributes", Profile = P)] [Icall("fmodstudio!FMOD5_Debug_Initialize", Profile = P)]
    [Icall("fmodstudio!FMOD5_DSP_SetMeteringEnabled", Profile = P)] [Icall("fmodstudio!FMOD5_DSP_GetMeteringInfo", Profile = P)] [Icall("fmodstudio!FMOD5_DSP_Release", Profile = P)]
    [Icall("fmodstudio!FMOD5_ChannelGroup_Release", Profile = P)] [Icall("fmodstudio!FMOD5_ChannelGroup_SetPaused", Profile = P)] [Icall("fmodstudio!FMOD5_ChannelGroup_SetVolume", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_System_SetNumListeners", Profile = P)] [Icall("fmodstudio!FMOD_Studio_System_SetListenerWeight", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_System_GetCPUUsage", Profile = P)] [Icall("fmodstudio!FMOD_Studio_System_GetBufferUsage", Profile = P)] [Icall("fmodstudio!FMOD_Studio_System_ResetBufferUsage", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_System_GetMemoryUsage", Profile = P)] [Icall("fmodstudio!FMOD_Studio_System_GetParameterDescriptionCount", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_Bank_GetEventCount", Profile = P)] [Icall("fmodstudio!FMOD_Studio_Bank_GetBusCount", Profile = P)] [Icall("fmodstudio!FMOD_Studio_Bank_GetVCACount", Profile = P)] [Icall("fmodstudio!FMOD_Studio_Bank_GetStringCount", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_Bus_LockChannelGroup", Profile = P)] [Icall("fmodstudio!FMOD_Studio_Bus_UnlockChannelGroup", Profile = P)] [Icall("fmodstudio!FMOD_Studio_Bus_GetCPUUsage", Profile = P)] [Icall("fmodstudio!FMOD_Studio_Bus_GetMemoryUsage", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetUserPropertyCount", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_EventDescription_IsStream", Profile = P)] [Icall("fmodstudio!FMOD_Studio_EventDescription_IsDopplerEnabled", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_EventDescription_HasSustainPoint", Profile = P)] [Icall("fmodstudio!FMOD_Studio_EventDescription_GetSoundSize", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_EventInstance_Get3DAttributes", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetReverbLevel", Profile = P)] [Icall("fmodstudio!FMOD_Studio_EventInstance_SetReverbLevel", Profile = P)]
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetCPUUsage", Profile = P)] [Icall("fmodstudio!FMOD_Studio_EventInstance_GetMemoryUsage", Profile = P)]
    static object Accept(object[] a) => OK;

    // ================= core System / ChannelGroup / DSP (only what FMODUnity's init and overlay touch) =================
    /// <summary>Stage C: listener 0 drives the mixer's 3D (both overloads: with and without the attenuation position, which shepaudio
    /// does not separate from the listener's); the last attributes are read back.</summary>
    [Icall("fmodstudio!FMOD_Studio_System_SetListenerAttributes", Profile = P)] static object SetListener(object[] a)
    {
        if (a[2] is not ATTRIBUTES_3D at) return BadParam;
        lock (Gate) _listener = at;
        if (ShepAudio.On && a[1] is int li && li == 0)
            ShepAudio.Listener(V(at.position), V(at.velocity), V(at.forward), V(at.up));
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_System_GetListenerAttributes", Profile = P)] static object GetListener(object[] a)
    {
        lock (Gate) a[2] = _listener is ATTRIBUTES_3D at ? at : default(ATTRIBUTES_3D);
        return OK;
    }
    static ShepAudio.Vec3 V(VECTOR v) => new() { X = v.x, Y = v.y, Z = v.z };

    [Icall("fmodstudio!FMOD5_System_Create", Profile = P)] static object CoreCreate(object[] a) { lock (Gate) a[0] = New(K.Core).H; return OK; }
    [Icall("fmodstudio!FMOD5_System_GetVersion", Profile = P)] static object CoreVersion(object[] a) { a[1] = (uint)VERSION.number; return OK; }
    [Icall("fmodstudio!FMOD5_System_LoadPlugin", Profile = P)] static object CorePlugin(object[] a) { a[2] = (uint)(0x1000 + Interlocked.Increment(ref _serial)); return OK; }   // resonanceaudio: accepted, never loaded
    [Icall("fmodstudio!FMOD5_System_GetMasterChannelGroup", Profile = P)] static object CoreMaster(object[] a)
    {
        lock (Gate) { var s = Get(a[0], K.Core); if (s == null) return BadHandle; a[1] = SubOf(s, K.ChannelGroup); return OK; }   // master channel group
    }
    static IntPtr SubOf(Obj o, K k) => (o.Sub ??= New(k)).H;
    [Icall("fmodstudio!FMOD5_ChannelGroup_GetDSP", Profile = P)] static object CgDsp(object[] a) { lock (Gate) { var cg = Get(a[0], K.ChannelGroup); a[2] = cg != null ? SubOf(cg, K.Dsp) : New(K.Dsp).H; } return OK; }
    [Icall("fmodstudio!FMOD5_System_GetSoftwareFormat", Profile = P)] static object CoreFmt(object[] a) { a[1] = 48000; a[2] = SPEAKERMODE.STEREO; a[3] = 0; return OK; }
    [Icall("fmodstudio!FMOD5_System_GetOutput", Profile = P)] static object CoreOut(object[] a) { a[1] = OUTPUTTYPE.NOSOUND; return OK; }

    // ================= Bank =================
    [Icall("fmodstudio!FMOD_Studio_Bank_IsValid", Profile = P)] static object BankValid(object[] a) { lock (Gate) return Get(a[0], K.Bank) != null ? True : False; }
    [Icall("fmodstudio!FMOD_Studio_Bank_GetLoadingState", Profile = P)] static object BankState(object[] a) { lock (Gate) { if (Get(a[0], K.Bank) == null) { a[1] = LOADING_STATE.UNLOADED; return BadHandle; } } a[1] = LOADING_STATE.LOADED; return OK; }
    [Icall("fmodstudio!FMOD_Studio_Bank_GetSampleLoadingState", Profile = P)] static object BankSState(object[] a) { lock (Gate) { if (Get(a[0], K.Bank) == null) { a[1] = LOADING_STATE.UNLOADED; return BadHandle; } } a[1] = LOADING_STATE.LOADED; return OK; }
    [Icall("fmodstudio!FMOD_Studio_Bank_LoadSampleData", Profile = P)] static object BankLoadS(object[] a) => OK;
    [Icall("fmodstudio!FMOD_Studio_Bank_UnloadSampleData", Profile = P)] static object BankUnloadS(object[] a) => OK;
    [Icall("fmodstudio!FMOD_Studio_Bank_Unload", Profile = P)] static object BankUnload(object[] a) { lock (Gate) { var b = Get(a[0], K.Bank); if (b == null) return BadHandle; Banks.Remove(b); b.Destroyed = true; Objs.Remove(b.H); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_Bank_GetPath", Profile = P)] static object BankPathI(object[] a) { Obj b; lock (Gate) b = Get(a[0], K.Bank); if (b == null) return BadHandle; return PutStr(b.Path, a[1], a[2], a, 3); }
    [Icall("fmodstudio!FMOD_Studio_Bank_GetID", Profile = P)] static object BankId(object[] a) { lock (Gate) { var b = Get(a[0], K.Bank); if (b == null) return BadHandle; a[1] = ToGuid(b); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_Bank_SetUserData", Profile = P)] static object BankSetUD(object[] a) => SysSetUD(a);
    [Icall("fmodstudio!FMOD_Studio_Bank_GetUserData", Profile = P)] static object BankGetUD(object[] a) => SysGetUD(a);

    // ================= Bus / VCA =================
    [Icall("fmodstudio!FMOD_Studio_Bus_IsValid", Profile = P)] static object BusValid(object[] a) { lock (Gate) return Get(a[0], K.Bus) != null ? True : False; }
    [Icall("fmodstudio!FMOD_Studio_Bus_GetPath", Profile = P)] static object BusPath(object[] a) { Obj b; lock (Gate) b = Get(a[0], K.Bus); if (b == null) return BadHandle; return PutStr(PathOf(b), a[1], a[2], a, 3); }
    [Icall("fmodstudio!FMOD_Studio_Bus_GetID", Profile = P)] static object BusId(object[] a) { lock (Gate) { var b = Get(a[0], K.Bus); if (b == null) return BadHandle; a[1] = ToGuid(b); } return OK; }
    /// <summary>Stage C: the bus's UTF-8 path when the bake has that bus (checked once), else null.</summary>
    static byte[] BakedBus(Obj b)
    {
        if (!ShepAudio.On || b?.Path == null) return null;
        if (b.BusBaked == -2) { b.Utf8 = ShepAudio.Utf8(b.Path); b.BusBaked = ShepAudio.BusKnown(b.Utf8) ? 1 : -1; }
        return b.BusBaked == 1 ? b.Utf8 : null;
    }
    [Icall("fmodstudio!FMOD_Studio_Bus_SetPaused", Profile = P)] static object BusSetPaused(object[] a)
    {
        Obj b; lock (Gate) { b = Get(a[0], K.Bus); if (b == null) return BadHandle; b.Paused = (bool)a[1]; }
        if (BakedBus(b) is { } p) ShepAudio.BusPaused(p, (bool)a[1]);
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_Bus_GetPaused", Profile = P)] static object BusGetPaused(object[] a) { lock (Gate) a[1] = Get(a[0], K.Bus)?.Paused ?? false; return OK; }
    [Icall("fmodstudio!FMOD_Studio_Bus_SetMute", Profile = P)] static object BusSetMute(object[] a)
    {
        Obj b; lock (Gate) { b = Get(a[0], K.Bus); if (b == null) return BadHandle; b.Mute = (bool)a[1]; }
        if (BakedBus(b) is { } p) ShepAudio.BusMute(p, (bool)a[1]);
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_Bus_GetMute", Profile = P)] static object BusGetMute(object[] a) { lock (Gate) a[1] = Get(a[0], K.Bus)?.Mute ?? false; return OK; }
    [Icall("fmodstudio!FMOD_Studio_Bus_SetVolume", Profile = P)] static object BusSetVol(object[] a)
    {
        Obj b; lock (Gate) { b = Get(a[0], K.Bus); if (b == null) return BadHandle; b.Volume = (float)a[1]; }
        if (BakedBus(b) is { } p) ShepAudio.BusVolume(p, (float)a[1]);
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_Bus_GetVolume", Profile = P)] static object BusGetVol(object[] a) { float v; lock (Gate) v = Get(a[0], K.Bus)?.Volume ?? 1f; a[1] = v; a[2] = v; return OK; }
    /// <summary>Routing isn't known without the banks: the master bus stops every non-snapshot instance, sub-buses only
    /// one-shot-like ones (held loops on other buses keep "playing").</summary>
    [Icall("fmodstudio!FMOD_Studio_Bus_StopAllEvents", Profile = P)] static object BusStopAll(object[] a)
    {
        Obj b;
        lock (Gate)
        {
            b = Get(a[0], K.Bus); if (b == null) return BadHandle; bool master = b.Path is "bus:/" or "bus:";
            foreach (var i in Live) if (i.Sa == 0 && !(i.Desc?.Path?.StartsWith("snapshot:/", StringComparison.OrdinalIgnoreCase) ?? false) && (master || WantsStop(i))) StopInst(i);
        }
        // Stage C: shepaudio knows the routing (the bake's bus per event): every baked instance under that bus
        if (BakedBus(b) is { } p) ShepAudio.BusStopAll(p, a[1] is STOP_MODE m && m == STOP_MODE.ALLOWFADEOUT);
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_Bus_GetChannelGroup", Profile = P)] static object BusCg(object[] a) { lock (Gate) { var b = Get(a[0], K.Bus); if (b == null) { a[1] = IntPtr.Zero; return BadHandle; } a[1] = SubOf(b, K.ChannelGroup); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_VCA_IsValid", Profile = P)] static object VcaValid(object[] a) { lock (Gate) return Get(a[0], K.Vca) != null ? True : False; }
    [Icall("fmodstudio!FMOD_Studio_VCA_GetPath", Profile = P)] static object VcaPath(object[] a) { Obj b; lock (Gate) b = Get(a[0], K.Vca); if (b == null) return BadHandle; return PutStr(PathOf(b), a[1], a[2], a, 3); }
    [Icall("fmodstudio!FMOD_Studio_VCA_GetID", Profile = P)] static object VcaId(object[] a) { lock (Gate) { var b = Get(a[0], K.Vca); if (b == null) return BadHandle; a[1] = ToGuid(b); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_VCA_SetVolume", Profile = P)] static object VcaSetVol(object[] a) { lock (Gate) { var b = Get(a[0], K.Vca); if (b == null) return BadHandle; b.Volume = (float)a[1]; } return OK; }
    [Icall("fmodstudio!FMOD_Studio_VCA_GetVolume", Profile = P)] static object VcaGetVol(object[] a) { float v; lock (Gate) v = Get(a[0], K.Vca)?.Volume ?? 1f; a[1] = v; a[2] = v; return OK; }

    // ================= EventDescription =================
    [Icall("fmodstudio!FMOD_Studio_EventDescription_IsValid", Profile = P)] static object DescValid(object[] a) { lock (Gate) return Get(a[0], K.Desc) != null ? True : False; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetID", Profile = P)] static object DescId(object[] a) { lock (Gate) { var d = Get(a[0], K.Desc); if (d == null) return BadHandle; a[1] = ToGuid(d); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetPath", Profile = P)] static object DescPath(object[] a) { Obj d; lock (Gate) d = Get(a[0], K.Desc); if (d == null) return BadHandle; return PutStr(PathOf(d), a[1], a[2], a, 3); }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_CreateInstance", Profile = P)] static object DescCreate(object[] a)
    {
        lock (Gate)
        {
            var d = Get(a[0], K.Desc); if (d == null) { a[1] = IntPtr.Zero; return BadHandle; }
            var i = New(K.Inst); i.Desc = d; d.Instances++; Live.Add(i); Created++; a[1] = i.H;
            if (d.Ev >= 0)
            {
                uint h = ShepAudio.Create(d.Ev);   // Stage C: a shepaudio instance (0: its 1,024 instances are all in use -> silent stub)
                if (h != 0) { i.Sa = h; BySa[h] = i; ShepAudio.Routed++; }
                else Log.Warn($"[audio] shepaudio has no free instance for {PathOf(d)}: silent stub");
            }
            else if (ShepAudio.On) { ShepAudio.Unbaked++; NoteUnbaked(d); }
            return OK;
        }
    }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetInstanceCount", Profile = P)] static object DescCount(object[] a) { lock (Gate) a[1] = Get(a[0], K.Desc)?.Instances ?? 0; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetInstanceList", Profile = P)] static object DescList(object[] a) { lock (Gate) { var d = Get(a[0], K.Desc); return FillList(Live.Where(i => i.Desc == d), a[1], a[2], a, 3); } }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_ReleaseAllInstances", Profile = P)] static object DescReleaseAll(object[] a)
    {
        lock (Gate)
        {
            var d = Get(a[0], K.Desc); if (d == null) return BadHandle;
            foreach (var i in Live.Where(i => i.Desc == d))
            {
                if (i.Sa != 0) { ShepAudio.Stop(i.Sa, d.Ev, false); ShepAudio.Release(i.Sa, d.Ev); }
                else StopInst(i);
                i.Released = true;
            }
        }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_SetCallback", Profile = P)] static object DescCallback(object[] a)
    {
        lock (Gate) { var d = Get(a[0], K.Desc); if (d == null) return BadHandle; d.Callback = a[1] as Delegate; d.Mask = Mask(a[2]); }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_SetUserData", Profile = P)] static object DescSetUD(object[] a) => SysSetUD(a);
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetUserData", Profile = P)] static object DescGetUD(object[] a) => SysGetUD(a);
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetSampleLoadingState", Profile = P)] static object DescSState(object[] a) { a[1] = LOADING_STATE.LOADED; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_LoadSampleData", Profile = P)] static object DescLoadS(object[] a) { Obj d; lock (Gate) d = Get(a[0], K.Desc); if (d?.Ev >= 0) ShepAudio.Preload(d.Ev); return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_UnloadSampleData", Profile = P)] static object DescUnloadS(object[] a) { Obj d; lock (Gate) d = Get(a[0], K.Desc); if (d?.Ev >= 0) ShepAudio.Unload(d.Ev); return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_IsSnapshot", Profile = P)] static object DescSnap(object[] a) { lock (Gate) a[1] = Get(a[0], K.Desc)?.Path?.StartsWith("snapshot:/", StringComparison.OrdinalIgnoreCase) ?? false; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_IsOneshot", Profile = P)] static object DescOneshot(object[] a)
    {
        lock (Gate) { var d = Get(a[0], K.Desc); a[1] = d?.Ev >= 0 ? ShepAudio.Events[d.Ev].Oneshot : !(d?.Path?.StartsWith("snapshot:/", StringComparison.OrdinalIgnoreCase) ?? false); }
        return OK;
    }
    // Stage C: the bake's description data (FMOD's own values); unbaked events answer like the stub always did (zeroes)
    static ShepAudio.EvInfo BakedDesc(object h) { lock (Gate) { var d = Get(h, K.Desc); return d?.Ev >= 0 ? ShepAudio.Events[d.Ev] : null; } }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_Is3D", Profile = P)] static object DescIs3D(object[] a) { a[1] = BakedDesc(a[0])?.Is3D ?? false; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetLength", Profile = P)] static object DescLength(object[] a) { a[1] = Math.Max(0, BakedDesc(a[0])?.LengthMs ?? 0); return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetMinMaxDistance", Profile = P)] static object DescMinMax(object[] a) { var x = BakedDesc(a[0]); a[1] = x?.MinDist ?? 0f; a[2] = x?.MaxDist ?? 0f; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetMinMaxDistance", Profile = P)] static object InstMinMax(object[] a)
    {
        ShepAudio.EvInfo x; lock (Gate) { var i = I(a[0]); x = i?.Desc?.Ev >= 0 ? ShepAudio.Events[i.Desc.Ev] : null; }
        a[1] = x?.MinDist ?? 0f; a[2] = x?.MaxDist ?? 0f; return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetParameterDescriptionCount", Profile = P)] static object DescPCount(object[] a) { a[1] = BakedDesc(a[0])?.ParamNames.Length ?? 0; return OK; }
    static object BakedParamDesc(ShepAudio.EvInfo x, int k, object[] a, int outIdx)
    {
        var name = x.ParamNames[k]; var id = HashParam(name);
        lock (Gate) ParamNames[id] = name;
        a[outIdx] = new PARAMETER_DESCRIPTION { name = new StringWrapper(NativeName(name)), id = ToPid(id), minimum = x.PMin[k], maximum = x.PMax[k], defaultvalue = x.PDef[k],
                                                flags = x.PGlobal[k] ? PARAMETER_FLAGS.GLOBAL : 0 };
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetParameterDescriptionByName", Profile = P)] static object DescPDescN(object[] a)
    {
        var x = BakedDesc(a[0]); int k = x != null ? Array.FindIndex(x.ParamNames, n => string.Equals(n, Str(a[1]), StringComparison.OrdinalIgnoreCase)) : -1;
        return k >= 0 ? BakedParamDesc(x, k, a, 2) : SysPDescN(a);
    }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetParameterDescriptionByID", Profile = P)] static object DescPDescI(object[] a) => SysPDescI(a);
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetParameterDescriptionByIndex", Profile = P)] static object DescPDescX(object[] a)
    {
        var x = BakedDesc(a[0]); int k = a[1] is int n ? n : -1;
        if (x != null && k >= 0 && k < x.ParamNames.Length) return BakedParamDesc(x, k, a, 2);
        a[2] = default(PARAMETER_DESCRIPTION); return BadParam;   // unbaked: count is 0
    }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetUserProperty", Profile = P)] static object DescUProp(object[] a) { a[2] = default(USER_PROPERTY); return NotFound; }
    [Icall("fmodstudio!FMOD_Studio_EventDescription_GetUserPropertyByIndex", Profile = P)] static object DescUPropX(object[] a) { a[2] = default(USER_PROPERTY); return BadParam; }

    // ================= EventInstance =================
    static Obj I(object h) => Get(h, K.Inst);
    [Icall("fmodstudio!FMOD_Studio_EventInstance_IsValid", Profile = P)] static object InstValid(object[] a) { lock (Gate) return I(a[0]) != null ? True : False; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetDescription", Profile = P)] static object InstDesc(object[] a) { lock (Gate) { var i = I(a[0]); if (i == null) { a[1] = IntPtr.Zero; return BadHandle; } a[1] = i.Desc.H; } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_Start", Profile = P)] static object InstStart(object[] a)
    {
        lock (Gate)
        {
            var i = I(a[0]); if (i == null) return BadHandle; Starts++;
            if (i.Sa != 0) { ShepAudio.Start(i.Sa, i.Desc.Ev); return OK; }   // Stage C
            i.PendingStart = true; i.PendingStopped = false;
        }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_Stop", Profile = P)] static object InstStop(object[] a)
    {
        lock (Gate)
        {
            var i = I(a[0]); if (i == null) return BadHandle;
            if (i.Sa != 0) ShepAudio.Stop(i.Sa, i.Desc.Ev, a[1] is STOP_MODE m && m == STOP_MODE.ALLOWFADEOUT);   // Stage C: ALLOWFADEOUT = the authored release
            else StopInst(i);
        }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_Release", Profile = P)] static object InstRelease(object[] a)
    {
        lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; if (i.Sa != 0 && !i.Released) ShepAudio.Release(i.Sa, i.Desc.Ev); i.Released = true; }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetPlaybackState", Profile = P)] static object InstState(object[] a)
    {
        lock (Gate) { var i = I(a[0]); if (i == null) { a[1] = PLAYBACK_STATE.STOPPED; return BadHandle; } a[1] = (PLAYBACK_STATE)(i.Sa != 0 ? ShepAudio.State(i.Sa) : i.PendingStart ? Starting : i.State); }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetCallback", Profile = P)] static object InstCallback(object[] a)
    {
        lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; i.Callback = a[1] as Delegate; i.Mask = Mask(a[2]); }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetUserData", Profile = P)] static object InstSetUD(object[] a) { lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; i.UserData = (IntPtr)a[1]; } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetUserData", Profile = P)] static object InstGetUD(object[] a) { lock (Gate) { var i = I(a[0]); a[1] = i?.UserData ?? IntPtr.Zero; return i == null ? BadHandle : OK; } }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetVolume", Profile = P)] static object InstSetVol(object[] a) { lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; i.Volume = (float)a[1]; if (i.Sa != 0) ShepAudio.SetVolume(i.Sa, i.Desc.Ev, i.Volume); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetVolume", Profile = P)] static object InstGetVol(object[] a) { float v; lock (Gate) v = I(a[0])?.Volume ?? 0f; a[1] = v; if (a.Length > 2 && a[2] is not IntPtr) a[2] = v; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetPitch", Profile = P)] static object InstSetPitch(object[] a) { lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; i.Pitch = (float)a[1]; if (i.Sa != 0) ShepAudio.SetPitch(i.Sa, i.Pitch); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetPitch", Profile = P)] static object InstGetPitch(object[] a) { float v; lock (Gate) v = I(a[0])?.Pitch ?? 0f; a[1] = v; if (a.Length > 2 && a[2] is not IntPtr) a[2] = v; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetPaused", Profile = P)] static object InstSetPaused(object[] a)
    {
        lock (Gate)
        {
            var i = I(a[0]); if (i == null) return BadHandle;
            bool p = (bool)a[1];
            if (i.Sa == 0 && Shepherd.Engine.Runtime.Time.Wall && p != i.Paused && i.State == Playing)   // kit3: a paused stub's timeline holds still
            {
                if (p && i.TlAt >= 0) { i.Timeline = TlNow(i); i.TlAt = -1; } else if (!p) i.TlAt = ShepAudio.ClockSeconds;
            }
            i.Paused = p; if (i.Sa != 0) ShepAudio.SetPaused(i.Sa, i.Paused);
        }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetPaused", Profile = P)] static object InstGetPaused(object[] a) { lock (Gate) a[1] = I(a[0])?.Paused ?? false; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetTimelinePosition", Profile = P)] static object InstSetTl(object[] a)
    {
        lock (Gate)
        {
            var i = I(a[0]); if (i == null) return BadHandle; i.Timeline = (int)a[1]; i.TlRestart = false;
            if (i.TlAt >= 0) i.TlAt = ShepAudio.ClockSeconds;   // kit3: a running stub timeline runs on from the new position
            if (i.Sa != 0) ShepAudio.SetTimeline(i.Sa, i.Desc.Ev, i.Timeline);
        }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetTimelinePosition", Profile = P)] static object InstGetTl(object[] a) { lock (Gate) { var i = I(a[0]); a[1] = i == null ? 0 : i.Sa != 0 ? ShepAudio.GetTimeline(i.Sa) : TlNow(i); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetListenerMask", Profile = P)] static object InstSetLm(object[] a) { lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; i.ListenerMask = (uint)a[1]; } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetListenerMask", Profile = P)] static object InstGetLm(object[] a) { lock (Gate) a[1] = I(a[0])?.ListenerMask ?? 0u; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetProperty", Profile = P)] static object InstSetProp(object[] a)
    {
        lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; int k = (int)(EVENT_PROPERTY)a[1]; if (k < 0 || k > 64) return BadParam; i.Props ??= new float[65]; i.Props[k] = (float)a[2]; }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetProperty", Profile = P)] static object InstGetProp(object[] a) { lock (Gate) { var i = I(a[0]); int k = (int)(EVENT_PROPERTY)a[1]; a[2] = i?.Props != null && k >= 0 && k < i.Props.Length ? i.Props[k] : -1f; } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetParameterByName", Profile = P)] static object InstSetP(object[] a)
    {
        var n = Str(a[1]); if (n == null) return BadParam;
        lock (Gate) { var i = I(a[0]); if (i == null) return BadHandle; (i.Params ??= new(StringComparer.OrdinalIgnoreCase))[n] = (float)a[2]; if (i.Sa != 0) ShepAudio.SetParam(i.Sa, i.Desc.Ev, n, (float)a[2]); }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetParameterByName", Profile = P)] static object InstGetP(object[] a)
    {
        var n = Str(a[1]); float v = 0; lock (Gate) { var i = I(a[0]); if (n != null) i?.Params?.TryGetValue(n, out v); }
        a[2] = v; a[3] = v; return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetParameterByID", Profile = P)] static object InstSetPid(object[] a)
    {
        lock (Gate)
        {
            var i = I(a[0]); if (i == null) return BadHandle; (i.ParamsById ??= new())[Pid(a[1])] = (float)a[2];
            if (i.Sa != 0 && ParamNames.TryGetValue(Pid(a[1]), out var n)) ShepAudio.SetParam(i.Sa, i.Desc.Ev, n, (float)a[2]);
        }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetParameterByID", Profile = P)] static object InstGetPid(object[] a) { float v = 0; lock (Gate) I(a[0])?.ParamsById?.TryGetValue(Pid(a[1]), out v); a[2] = v; a[3] = v; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetParametersByIDs", Profile = P)] static object InstSetPids(object[] a)
    {
        lock (Gate)
        {
            var i = I(a[0]); if (i == null) return BadHandle;
            if (a[1] is PARAMETER_ID[] ids && a[2] is float[] vs) for (int k = 0; k < Math.Min((int)a[3], Math.Min(ids.Length, vs.Length)); k++)
            {
                (i.ParamsById ??= new())[Pid(ids[k])] = vs[k];
                if (i.Sa != 0 && ParamNames.TryGetValue(Pid(ids[k]), out var n)) ShepAudio.SetParam(i.Sa, i.Desc.Ev, n, vs[k]);
            }
        }
        return OK;
    }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetParameterByNameWithLabel", Profile = P)] static object InstSetPL(object[] a) { lock (Gate) return I(a[0]) == null ? BadHandle : OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_SetParameterByIDWithLabel", Profile = P)] static object InstSetPidL(object[] a) { lock (Gate) return I(a[0]) == null ? BadHandle : OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_IsVirtual", Profile = P)] static object InstVirtual(object[] a) { lock (Gate) a[1] = I(a[0])?.Virtual ?? false; return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_GetChannelGroup", Profile = P)] static object InstCg(object[] a) { lock (Gate) { var i = I(a[0]); if (i == null) { a[1] = IntPtr.Zero; return BadHandle; } a[1] = SubOf(i, K.ChannelGroup); } return OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_KeyOff", Profile = P)] static object InstKeyOff(object[] a) { lock (Gate) return I(a[0]) == null ? BadHandle : OK; }
    [Icall("fmodstudio!FMOD_Studio_EventInstance_Set3DAttributes", Profile = P)] static object InstSet3D(object[] a)
    {
        lock (Gate)
        {
            var i = I(a[0]); if (i == null) return BadHandle;
            if (i.Sa != 0 && a[1] is ATTRIBUTES_3D at) ShepAudio.Set3D(i.Sa, at.position.x, at.position.y, at.position.z, at.velocity.x, at.velocity.y, at.velocity.z);
        }
        return OK;
    }
}
