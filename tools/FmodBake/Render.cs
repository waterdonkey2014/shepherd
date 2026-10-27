using FMOD;
using FMOD.Studio;

namespace Shepherd.FmodBake;

sealed class RenderOpts
{
    public int MaxFrames = Session.Rate * 30;      // capture cap while the instance is alive
    public int TailMaxFrames = Session.Rate * 5;   // after STOPPED: keep mixing until silent (send/reverb tails), capped
    public bool Meter;                             // accumulate per-bus input energy (routing discovery)
    public int MeterFrames = Session.Rate * 4;     // metered for this long after the first audible block (routing is static)
    public (float x, float y, float z)? Position;  // 3D events: source position (listener at the origin facing +Z); null = at the listener
    public int StopAtFrame = -1;                   // >= 0: stop(ALLOWFADEOUT) once this many frames of timeline were captured (release measurement)
    public int AfterStopFrames = Session.Rate * 20;  // with StopAtFrame: cap on the capture after the stop call
}

sealed class RenderResult
{
    public float[] Pcm;          // interleaved stereo float, from the update that started the instance
    public int Frames;
    public bool StoppedByItself; // reached PLAYBACK_STATE.STOPPED without stop()
    public int StopFrame = -1;   // capture frame at which STOPPED was first observed
    public int StopCallFrame = -1;  // StopAtFrame: capture frame (trimmed timeline) at which stop(ALLOWFADEOUT) was called
    public string Error;
    public Dictionary<string, double> BusEnergy;   // bus path -> sum of squares of the bus input (metered)
    public long Missed;
    public int TrimmedLeadFrames;  // scheduling latency removed from the front (stream schedule delay / stream open)
}

/// <summary>Event rendering on a <see cref="Session"/>: isolated (drained before and after), deterministic, NRT.</summary>
sealed class Renderer
{
    Session _s;
    readonly List<BusInfo> _buses;
    readonly List<(BusInfo bus, ChannelGroup cg, DSP tail, DSP head)> _meters = new();
    public const float Silence = 1e-6f;    // -120 dBFS block peak = digital-silence threshold

    // ------------------------------------------------------------------ state guard (isolation between renders)
    // Events can change global state: FMOD 2.02 command instruments set (global) parameters and start/stop other events,
    // nested/snapshot instruments can outlive their parent. After every render the guard puts globals, buses and
    // instances back to the state at bake start and records what leaked, so no render depends on the one before.
    List<(PARAMETER_ID id, string name, float value)> _globals0;
    List<(BusInfo bus, float vol, bool paused, bool mute)> _bus0;
    List<(BusInfo bus, float final)> _final0;
    /// <summary>Set when a render left a bus's final (modulated) volume changed and it did not recover within 5 s: nothing
    /// visible holds it (no snapshot instance, no global), so only a fresh Studio system restores isolation.</summary>
    public bool NeedsReset;
    List<EventInfo> _all;
    public readonly Dictionary<string, List<string>> Leaks = new(StringComparer.Ordinal);

    public void CaptureInitialState(List<EventInfo> allEvents)
    {
        _all = allEvents;
        _globals0 = new();
        if (_s.Studio.getParameterDescriptionList(out var gl) == RESULT.OK)
            foreach (var g in gl) { _s.Studio.getParameterByID(g.id, out float v); _globals0.Add((g.id, (string)g.name, v)); }
        _bus0 = _buses.Select(b => { b.Bus.getVolume(out float v); b.Bus.getPaused(out bool p); b.Bus.getMute(out bool m); return (b, v, p, m); }).ToList();
        var f0 = ReadFinals();
        _final0 = _buses.Select((b, k) => (b, f0[k])).ToList();
    }

    /// <summary>Final (modulated) volume of every bus, read with every bus instantiated. Renders run with unlocked bus groups,
    /// and an unlocked bus that does not exist reads 1: a duck held on an idle bus (dlc/env/midas/choke_start leaves the
    /// footstep bus at 0) would go unseen, and the next footstep renders silent. Once instantiated, a bus reads its real level
    /// (the SFX reverb return: 0.75, not 1). The lock is held for this reading only (two silent mixes); unlocking restores
    /// the unlocked output exactly.</summary>
    float[] ReadFinals()
    {
        foreach (var b in _buses) b.Bus.lockChannelGroup();
        _s.Studio.flushCommands(); _s.Step();
        var f = _buses.Select(b => { b.Bus.getVolume(out _, out float v); return v; }).ToArray();
        foreach (var b in _buses) b.Bus.unlockChannelGroup();
        _s.Studio.flushCommands(); _s.Step();
        return f;
    }

    void Guard(EventInfo e)
    {
        if (_all == null) return;
        var leak = new List<string>();
        foreach (var (id, name, v0) in _globals0)
        {
            _s.Studio.getParameterByID(id, out float v);
            if (Math.Abs(v - v0) > 1e-6) { leak.Add($"global {name}={v}"); _s.Studio.setParameterByID(id, v0, true); }
        }
        foreach (var (b, v0, p0, m0) in _bus0)
        {
            b.Bus.getVolume(out float v); b.Bus.getPaused(out bool p); b.Bus.getMute(out bool m);
            if (Math.Abs(v - v0) > 1e-6 || p != p0 || m != m0) { leak.Add($"bus {b.Path} vol={v} paused={p} mute={m}"); b.Bus.setVolume(v0); b.Bus.setPaused(p0); b.Bus.setMute(m0); }
        }
        bool stopped = false;
        foreach (var d in _all)
        {
            if (d.Desc.getInstanceCount(out int n) != RESULT.OK || n == 0) continue;
            if (d.Desc.getInstanceList(out var list) != RESULT.OK) continue;
            foreach (var i in list)
            {
                i.getPlaybackState(out var st);
                leak.Add($"instance {d.Path} ({st})");
                i.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); i.release();
                stopped = true;
            }
        }
        if (stopped) _s.Drain();
        // modulated (final) bus volumes: allow 5 s for snapshot releases, then ask for a fresh system
        List<string> Final()
        {
            var now = ReadFinals();
            return _final0.Select((x, k) => Math.Abs(now[k] - x.final) > 1e-4 ? $"bus {x.bus.Path} final volume {now[k]:G4} (was {x.final:G4})" : null).Where(x => x != null).ToList();
        }
        var fin = Final();
        for (int i = 0; fin.Count > 0 && i < 5 * Session.Rate / Session.Block; i += 8) { for (int k = 0; k < 8; k++) _s.Step(); fin = Final(); }
        if (fin.Count > 0) { leak.AddRange(fin.Select(x => x + " -> session rebuilt")); NeedsReset = true; }
        if (leak.Count > 0)
        {
            if (!Leaks.TryGetValue(e.Path, out var l)) Leaks[e.Path] = l = new List<string>();
            foreach (var x in leak) if (!l.Contains(x)) l.Add(x);
        }
    }

    /// <summary>After a session rebuild: same Renderer (and leak log), new system. Bus channel groups stay unlocked.</summary>
    public void Rebind(Session s)
    {
        _s = s; _meters.Clear(); NeedsReset = false;
        var la = new ATTRIBUTES_3D { position = V(0, 0, 0), velocity = V(0, 0, 0), forward = V(0, 0, 1), up = V(0, 1, 0) };
        s.Studio.setListenerAttributes(0, la);
        CaptureInitialState(_all);
    }

    public Renderer(Session s, List<BusInfo> buses)
    {
        _s = s; _buses = buses;
        // listener at the origin facing +Z, up +Y (FMOD's default, set explicitly)
        var la = new ATTRIBUTES_3D { position = V(0, 0, 0), velocity = V(0, 0, 0), forward = V(0, 0, 1), up = V(0, 1, 0) };
        s.Studio.setListenerAttributes(0, la);
    }

    public static VECTOR V(float x, float y, float z) => new VECTOR { x = x, y = y, z = z };

    /// <summary>Lock every bus's channel group and enable input metering on its tail DSP (and output metering on its head).
    /// Only for the snapshot measurement, which plays a test signal into each bus's group. Locking changes what FMOD renders
    /// for some events (measured: harvest_meat_done -0.8 dB and -20.5 instead of -8.0 dB L-R at 6 m; the game never locks
    /// buses), so no event take, routing render or 3D/release measurement runs with locked buses.</summary>
    public void SetupMeters()
    {
        foreach (var b in _buses) b.Bus.lockChannelGroup();
        _s.Studio.flushCommands();
        _s.Step();
        foreach (var b in _buses)
        {
            if (b.Bus.getChannelGroup(out var cg) != RESULT.OK) continue;
            cg.getDSP(CHANNELCONTROL_DSP_INDEX.TAIL, out var tail);
            cg.getDSP(CHANNELCONTROL_DSP_INDEX.HEAD, out var head);
            tail.setMeteringEnabled(true, false);
            head.setMeteringEnabled(false, true);
            _meters.Add((b, cg, tail, head));
        }
        _s.Step();
    }
    public IReadOnlyList<(BusInfo bus, ChannelGroup cg, DSP tail, DSP head)> Meters => _meters;

    public static double MeterIn(DSP d)
    {
        if (d.getMeteringInfo(out DSP_METERING_INFO mi, IntPtr.Zero) != RESULT.OK || mi.numchannels <= 0) return 0;
        double e = 0; for (int c = 0; c < mi.numchannels; c++) e += mi.rmslevel[c] * (double)mi.rmslevel[c];
        return e / mi.numchannels;
    }
    public static double MeterOut(DSP d)
    {
        if (d.getMeteringInfo(IntPtr.Zero, out DSP_METERING_INFO mo) != RESULT.OK || mo.numchannels <= 0) return 0;
        double e = 0; for (int c = 0; c < mo.numchannels; c++) e += mo.rmslevel[c] * (double)mo.rmslevel[c];
        return e / mo.numchannels;
    }

    public bool EnsureSamples(EventInfo e)
    {
        e.Desc.loadSampleData();
        for (int i = 0; i < 20000; i++)
        {
            e.Desc.getSampleLoadingState(out var st);
            if (st == LOADING_STATE.LOADED) return true;
            if (st == LOADING_STATE.ERROR) return false;
            _s.Step();
        }
        return false;
    }

    public RenderResult Render(EventInfo e, IReadOnlyDictionary<ParamInfo, float> values, RenderOpts o)
    {
        var res = new RenderResult();
        _s.Drain();
        if (!EnsureSamples(e)) { res.Error = "sample data did not load"; return res; }
        var r = e.Desc.createInstance(out var inst);
        if (r != RESULT.OK) { res.Error = "createInstance " + r; return res; }
        if (e.Is3D)
        {
            // at the listener: distance 0 (inside minDistance) = unattenuated; the Vita runtime applies rolloff + pan
            var p = o.Position ?? (0f, 0f, 0f);
            inst.set3DAttributes(new ATTRIBUTES_3D { position = V(p.x, p.y, p.z), velocity = V(0, 0, 0), forward = V(0, 0, 1), up = V(0, 1, 0) });
        }
        var globalsSet = new List<ParamInfo>();
        if (values != null)
            foreach (var (p, v) in values)
            {
                if (p.Global) { _s.Studio.setParameterByID(p.Id, v, true); globalsSet.Add(p); }
                else inst.setParameterByID(p.Id, v, true);
            }
        if (o.Meter) res.BusEnergy = new Dictionary<string, double>();
        long missed0 = _s.MissedCallbacks;
        _s.BeginCapture();
        r = inst.start();
        if (r != RESULT.OK) { _s.EndCapture(); inst.release(); res.Error = "start " + r; Guard(e); return res; }
        bool stopped = false;
        int meterUntil = int.MaxValue;
        // Studio holds the timeline of an event with streamed sounds at 0 for its stream schedule delay (8192 frames)
        // and longer while the stream is still opening on FMOD's non-blocking thread (real time). The bake (a) gives that
        // thread real time while the timeline is held, so opens finish, and (b) drops the blocks mixed before the timeline
        // started, so a take never carries scheduling latency and the result does not depend on I/O timing.
        int block = 0, startBlock = -1, firstAudible = -1;
        int maxFrames = o.StopAtFrame >= 0 ? int.MaxValue : o.MaxFrames;
        while (_s.CapFrames < maxFrames)
        {
            if (o.StopAtFrame >= 0 && res.StopCallFrame < 0 && startBlock >= 0 && _s.CapFrames - startBlock * Session.Block >= o.StopAtFrame)
            {
                inst.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                res.StopCallFrame = _s.CapFrames;
                maxFrames = _s.CapFrames + o.AfterStopFrames;
            }
            else if (o.StopAtFrame >= 0 && res.StopCallFrame < 0 && _s.CapFrames >= o.MaxFrames) break;
            float bpk = _s.Step();
            if (firstAudible < 0 && bpk >= 1e-6f) firstAudible = block;
            if (o.Meter && _s.CapFrames <= meterUntil)
            {
                Accumulate(res.BusEnergy);
                if (meterUntil == int.MaxValue && bpk >= 3e-5f) meterUntil = _s.CapFrames + o.MeterFrames;
            }
            inst.getPlaybackState(out var st);
            if (startBlock < 0)
            {
                inst.getTimelinePosition(out int tl);
                if (tl > 0) startBlock = Math.Max(0, block - 1);
                else if (e.IsStream && bpk < 1e-6f && block < 2000) System.Threading.Thread.Sleep(1);
            }
            block++;
            if (st == PLAYBACK_STATE.STOPPED) { stopped = true; break; }
        }
        if (startBlock < 0) startBlock = e.IsStream && firstAudible > 0 ? Math.Min(firstAudible, Session.StreamLookahead / Session.Block) : 0;   // no timeline: the schedule delay only
        res.TrimmedLeadFrames = startBlock * Session.Block;
        if (stopped)
        {
            res.StoppedByItself = true;
            res.StopFrame = _s.CapFrames;
            int tailStart = _s.CapFrames, quiet = 0;
            while (_s.CapFrames - tailStart < o.TailMaxFrames)
            {
                float pk = _s.Step();
                if (o.Meter && _s.CapFrames <= meterUntil) Accumulate(res.BusEnergy);
                if (pk < Silence) { if (++quiet >= 4) break; } else quiet = 0;
            }
        }
        else inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        inst.release();
        res.Pcm = _s.EndCapture();
        if (res.TrimmedLeadFrames > 0)
        {
            int keep = Math.Max(0, res.Pcm.Length / 2 - res.TrimmedLeadFrames);
            res.Pcm = Analysis.Slice(res.Pcm, res.TrimmedLeadFrames, keep);
            if (res.StopFrame >= 0) res.StopFrame = Math.Max(0, res.StopFrame - res.TrimmedLeadFrames);
            if (res.StopCallFrame >= 0) res.StopCallFrame = Math.Max(0, res.StopCallFrame - res.TrimmedLeadFrames);
        }
        res.Frames = res.Pcm.Length / 2;
        res.Missed = _s.MissedCallbacks - missed0;
        foreach (var p in globalsSet) _s.Studio.setParameterByID(p.Id, p.Default, true);
        _s.Drain();
        Guard(e);
        return res;
    }

    /// <summary>Routing meters without locking: every bus whose channel group exists right now (Studio creates them on demand
    /// while something is routed through) gets input metering on its tail DSP; the reading is the previous mix's. Metering is
    /// passive: the output is the same as without it (checked with metertest).</summary>
    void Accumulate(Dictionary<string, double> into)
    {
        foreach (var b in _buses)
        {
            if (b.Bus.getChannelGroup(out var cg) != RESULT.OK) continue;
            if (cg.getDSP(CHANNELCONTROL_DSP_INDEX.TAIL, out var tail) != RESULT.OK) continue;
            tail.getMeteringEnabled(out bool inOn, out _);
            if (!inOn) { tail.setMeteringEnabled(true, false); continue; }
            double e = MeterIn(tail);
            if (e > 0) into[b.Path] = into.GetValueOrDefault(b.Path) + e;
        }
    }

    // ------------------------------------------------------------------ snapshots: per-bus gain with a test signal

    /// <summary>
    /// Bus gains with a test signal: low-level deterministic noise (-35 dBFS RMS, below any compressor/limiter knee) is played
    /// into ONE bus's channel group at a time and the master output (the capture DSP) is measured, giving the composite gain
    /// bus→master. A bus's own gain = composite(bus) / composite(parent). Done with no snapshot (= the authored fader levels,
    /// already baked into every take) and with each snapshot started and settled (3 s), optionally at given parameter values.
    /// The master output is used instead of per-bus meters because a bus driven to -inf stops metering (stale meters).
    /// </summary>
    public sealed class SnapshotState { public string Path; public Dictionary<string, float> Params; public Dictionary<string, double> Own; }

    public (List<SnapshotState> states, Dictionary<string, double> baseline) MeasureSnapshots(List<EventInfo> snapshots, Action<string> log)
    {
        var rng = new Random(12345);
        int n = Session.Rate;
        var pcm = new short[n * 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)((rng.NextDouble() * 2 - 1) * 1000);
        var wav = Encode.WavPcm16(pcm, 2, Session.Rate);
        var ex = new CREATESOUNDEXINFO { cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(), length = (uint)wav.Length };
        var rr = _s.Core.createSound(wav, MODE.OPENMEMORY | MODE.CREATESAMPLE | MODE.LOOP_NORMAL | MODE._2D, ref ex, out var noise);
        if (rr != RESULT.OK) throw new InvalidOperationException("createSound(noise) " + rr);
        double inRms = 1000.0 / 32767 / Math.Sqrt(3);

        Dictionary<string, double> Composite()
        {
            var g = new Dictionary<string, double>();
            foreach (var m in _meters)
            {
                _s.Drain(4, 400);
                if (_s.Core.playSound(noise, m.cg, false, out var ch) != RESULT.OK) continue;
                _s.BeginCapture();
                for (int i = 0; i < 32; i++) _s.Step();
                ch.stop();
                var cap = _s.EndCapture();
                int from = 12 * Session.Block * 2;
                double e = 0; for (int i = from; i < cap.Length; i++) e += cap[i] * (double)cap[i];
                g[m.bus.Path] = Math.Sqrt(e / Math.Max(1, cap.Length - from)) / inRms;
            }
            _s.Drain(4, 800);
            return g;
        }
        static string Parent(string p) { if (p == "bus:/") return null; int i = p.LastIndexOf('/'); return i <= 4 ? "bus:/" : p[..i]; }
        // own gain = composite / parent composite; NaN when the parent path is silent (unmeasurable: the parent carries it)
        static Dictionary<string, double> Own(Dictionary<string, double> comp)
        {
            var o = new Dictionary<string, double>();
            foreach (var (bus, c) in comp)
            {
                string par = Parent(bus);
                double pc = par != null && comp.TryGetValue(par, out var v) ? v : 1.0;
                o[bus] = pc > 1e-6 ? c / pc : double.NaN;
            }
            return o;
        }

        var baseComp = Composite();
        var baseline = Own(baseComp);
        var states = new List<SnapshotState>();
        foreach (var sn in snapshots)
        {
            var pvSets = new List<Dictionary<string, float>> { null };
            foreach (var p in sn.Controllable)
                foreach (var f in new[] { 0f, 0.25f, 0.5f, 0.75f })
                {
                    float v = p.Min + f * (p.Max - p.Min);
                    if (Math.Abs(v - p.Default) > 1e-6) pvSets.Add(new Dictionary<string, float> { [p.Name] = v });
                }
            foreach (var pv in pvSets)
            {
                if (sn.Desc.createInstance(out var si) != RESULT.OK) { log($"snapshot {sn.Path}: createInstance failed"); break; }
                if (pv != null) foreach (var (k, v) in pv) si.setParameterByName(k, v, true);
                si.start();
                for (int i = 0; i < Session.Rate * 3 / Session.Block; i++) _s.Step();   // settle (fade-ins)
                var comp = Composite();
                si.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); si.release();
                for (int i = 0; i < Session.Rate * 3 / Session.Block; i++) _s.Step();   // settle (fade-outs)
                var own = Own(comp);
                // a bus whose ancestor path dropped by more than 40 dB is unmeasurable (what is left comes through sends):
                // the ancestor carries the change, descendants are not reported
                foreach (var bus in own.Keys.ToList())
                    for (string a = Parent(bus); a != null; a = Parent(a))
                        if (comp.TryGetValue(a, out var ca) && baseComp.TryGetValue(a, out var cb) && cb > 1e-6 && ca < cb * 1e-2) { own[bus] = double.NaN; break; }
                var d = new Dictionary<string, double>();
                foreach (var (bus, gain) in own)
                {
                    double b0 = baseline.GetValueOrDefault(bus, double.NaN);
                    if (double.IsNaN(b0) || double.IsNaN(gain) || b0 < 1e-3) continue;   // bus does not pass the test signal at baseline (gated / fader at -inf / sidechain key bus): unmeasurable
                    double ratio = gain / b0;
                    if (Math.Abs(20 * Math.Log10(Math.Max(ratio, 1e-6))) > 0.25) d[bus] = ratio < 1e-5 ? 0 : ratio;   // ±0.25 dB = measurement noise
                }
                states.Add(new SnapshotState { Path = sn.Path, Params = pv, Own = d });
            }
        }
        noise.release();
        return (states, baseline);
    }
}
