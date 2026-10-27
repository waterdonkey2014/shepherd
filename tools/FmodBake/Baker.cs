using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shepherd.FmodBake;

/// <summary>
/// bake &lt;set&gt;: renders every selected event with the game's FMOD (silent NRT), encodes the takes and writes
/// runtime/audiobake/&lt;set&gt;/manifest.json per the shared spec (vita/notes/sound-bake.md), plus report.json.
/// FMOD runs on this thread only; encoding (IMA ADPCM, afconvert AAC, decode-back checks) runs on worker threads.
/// </summary>
static class Baker
{
    const int R = Session.Rate;
    public const int ShortFrames = 4 * R;            // takes up to 4 s → ima-adpcm (pcm16 if ADPCM quality is poor)
    const float TrimThr = 1e-5f;                       // -100 dBFS: trailing tail below this is cut
    const float AudibleThr = 3e-5f;                    // -90 dBFS peak: below this a render counts as silent
    const int MaxTakes = 4, MaxVariantTakes = 2, MaxVariants = 32;

    sealed class Opts
    {
        public string Set, EventsFile, Match; public int Limit = int.MaxValue; public bool Snapshots = true; public int Workers = 6;
    }

    public static int Run(string[] a)
    {
        if (a.Length == 0) { Console.Error.WriteLine("usage: bake <set> [--events file] [--match re] [--limit n] [--no-snapshots] [--workers n]"); return 2; }
        var o = new Opts { Set = a[0] };
        for (int i = 1; i < a.Length; i++)
            switch (a[i])
            {
                case "--events": o.EventsFile = a[++i]; break;
                case "--match": o.Match = a[++i]; break;
                case "--limit": o.Limit = int.Parse(a[++i]); break;
                case "--no-snapshots": o.Snapshots = false; break;
                case "--workers": o.Workers = int.Parse(a[++i]); break;
                default: throw new ArgumentException("unknown option " + a[i]);
            }
        if (!Regex.IsMatch(o.Set, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("set name: letters, digits, _ and - only");
        return new Job(o).Bake();
    }

    // ------------------------------------------------------------------ one bake

    sealed class TakeOut
    {
        public string File, Codec; public int Channels, Rate = R, Frames; public (int s, int e)? Loop;
        public Dictionary<string, float> ParamValues; public int EncoderDelay = -1; public long Bytes;
        public double PeakDb, RmsDb, QualityDb, SeamRatio = double.NaN, SideDb; public int Clipped; public string QualityKind;
        public int MeasuredDelay = -1;
        public int StopFrame = -1;   // frame (of this take) at which FMOD reported STOPPED for this render; -1 = loop / never (capped)
        public TakeOut DupOf;   // a parameter variant that renders the same as an already kept take: same file, own paramValues
        public TakeOut Src => DupOf ?? this;
    }

    sealed class EventOut
    {
        public EventInfo E; public string Bus, BusFrom; public List<string> Sends = new();
        public List<TakeOut> Takes = new(); public bool Randomized, Silent, Capped, Looping, Music; public string Error, LoopKind;
        public int Variants, DupVariants; public double RenderMs; public long RenderedFrames;
        public bool SilentAtDefault; public int SilentStopFrame = -1;   // no take at the defaults: FMOD renders silence there
        public ReleaseOut Release; public SpatialOut Spatial; public long SpatialFrames, ReleaseFrames;
        public readonly object Lock = new();
    }

    /// <summary>stop(ALLOWFADEOUT) measured on a settled render: the gain envelope after the call relative to the same render
    /// without the stop ("ratio", deterministic events) or to the level before the call ("level"), the time to -60 dB and the
    /// time FMOD took to report STOPPED.</summary>
    sealed class ReleaseOut
    {
        public double ReleaseMs, StopMs = -1; public List<(double ms, double gain)> Curve = new(); public string From;
        public bool Capped; public int StopAtFrame; public double MatchDb;
    }

    sealed class EncodeJob { public EventOut Ev; public TakeOut Take; public float[] Pcm; public bool Loop; public string AbsPath; }

    sealed class Job
    {
        readonly Opts _o;
        readonly string _dir;
        readonly BlockingCollection<EncodeJob> _queue = new(16);
        readonly ConcurrentBag<string> _encodeErrors = new();
        readonly HashSet<string> _usedFiles = new(StringComparer.OrdinalIgnoreCase);
        Session _s; Renderer _r; List<BusInfo> _buses;

        public Job(Opts o) { _o = o; _dir = Path.Combine(Program.OutRoot, o.Set); }

        public int Bake()
        {
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(_dir);
            // a re-bake replaces the set's takes (derived data only, inside runtime/audiobake/<set>)
            foreach (var sub in new[] { "ev" }) { string p = Path.Combine(_dir, sub); if (Directory.Exists(p)) Directory.Delete(p, true); }
            _s = Program.OpenSession(out var events, out _buses, out int vcas);
            _events = events;
            _r = new Renderer(_s, _buses);
            _r.CaptureInitialState(events);   // bus channel groups stay unlocked (as in the game) for every event render
            var sel = Select(events);
            var snaps = events.Where(e => e.Snapshot && (_o.EventsFile == null || _wanted.Contains(e.Path))).ToList();
            Program.Log($"bake '{_o.Set}': {sel.Count} events, {snaps.Count} snapshots, {_o.Workers} encode workers -> {_dir}");

            // warm-up: the very first renders of a session can differ slightly (first use of the bus graph); not kept
            if (sel.Count > 0) { _r.Render(sel[0], null, new RenderOpts { MaxFrames = 2 * R }); _r.Render(sel[0], null, new RenderOpts { MaxFrames = 2 * R }); }
            var workers = Enumerable.Range(0, _o.Workers).Select(_ => new Thread(Worker) { IsBackground = true }).ToList();
            workers.ForEach(t => t.Start());
            var outs = new List<EventOut>();
            int n = 0; var tRender = Stopwatch.StartNew();
            foreach (var e in sel)
            {
                var eo = new EventOut { E = e };
                var t0 = Stopwatch.StartNew();
                try { BakeEvent(eo); }
                catch (Exception ex) { eo.Error = ex.GetType().Name + ": " + ex.Message; }
                eo.RenderMs = t0.Elapsed.TotalMilliseconds;
                outs.Add(eo);
                if (++n % 100 == 0 || n == sel.Count)
                    Program.Log($"  {n}/{sel.Count} events rendered ({tRender.Elapsed.TotalSeconds:F0} s, {_s.Updates} mixes, queue {_queue.Count})");
            }
            double renderSec = tRender.Elapsed.TotalSeconds;
            _queue.CompleteAdding();
            workers.ForEach(t => t.Join());
            double encodeSec = tRender.Elapsed.TotalSeconds - renderSec;

            // buses: routing from metering, prefix table for the rest
            AssignPrefixBuses(outs);
            List<Renderer.SnapshotState> snapVol = new(); Dictionary<string, double> baseline = new();
            if (_o.Snapshots) _r.SetupMeters();   // snapshots only: a test signal is played into each (locked) bus group
            if (_o.Snapshots && snaps.Count > 0)
            {
                var ts = Stopwatch.StartNew();
                (snapVol, baseline) = _r.MeasureSnapshots(snaps, Program.Log);
                Program.Log($"snapshots measured in {ts.ElapsedMilliseconds} ms");
            }
            else if (_o.Snapshots) (_, baseline) = _r.MeasureSnapshots(new List<EventInfo>(), Program.Log);

            WriteManifest(outs, snaps, snapVol, baseline);
            var rep = Report(outs, snaps, sw.Elapsed.TotalSeconds, renderSec, encodeSec, vcas);
            _s.Dispose();
            return rep;
        }

        readonly HashSet<string> _wanted = new(StringComparer.Ordinal);

        List<EventInfo> Select(List<EventInfo> events)
        {
            IEnumerable<EventInfo> q = events.Where(e => !e.Snapshot);
            if (_o.EventsFile != null)
            {
                foreach (var line in File.ReadAllLines(_o.EventsFile))
                {
                    var p = line.Split('\t')[0].Trim();
                    if (p.Length > 0 && !p.StartsWith('#')) _wanted.Add(p);
                }
                var known = events.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
                foreach (var w in _wanted.Where(w => !known.Contains(w))) Program.Log($"  (not in banks: {w})");
                q = q.Where(e => _wanted.Contains(e.Path));
            }
            if (_o.Match != null) { var re = new Regex(_o.Match, RegexOptions.IgnoreCase); q = q.Where(e => re.IsMatch(e.Path)); }
            return q.Take(_o.Limit).ToList();
        }

        // ------------------------------------------------------------------ per event

        static bool IsMusic(EventInfo e) => e.Path.StartsWith("event:/music/", StringComparison.Ordinal) || e.Path.Contains("/music/", StringComparison.Ordinal);

        static int OneshotCap(EventInfo e) => e.LengthMs > 25000 ? Math.Min(240 * R, (int)((e.LengthMs + 5000L) * R / 1000)) : 30 * R;

        /// <summary>Non-one-shot render length: two timeline periods + 4 s when the timeline is ≤ 150 s (exact-loop search),
        /// else 96 s for music / 40 s for the rest (crossfade body + settle).</summary>
        static int LoopRenderFrames(EventInfo e)
        {
            long lf = (long)e.LengthMs * R / 1000;
            if (lf > 0 && lf <= 150L * R) return (int)Math.Clamp(2 * lf + 4 * R, 40L * R, 300L * R);
            return IsMusic(e) ? 96 * R : 40 * R;
        }

        /// <summary>Parameter variants (one parameter off its default at a time): every value of a discrete/labeled parameter
        /// (≤ 25 values); for non-one-shots also the far end(s) of continuous parameters. Baseline (all defaults) is not listed.</summary>
        static List<Dictionary<ParamInfo, float>> Variants(EventInfo e, bool oneshotContinuous, bool music)
        {
            var list = new List<Dictionary<ParamInfo, float>>();
            foreach (var p in e.Controllable)
            {
                var vals = new List<float>();
                if (p.Discrete && p.Max - p.Min <= 24) for (int v = (int)Math.Ceiling(p.Min); v <= (int)Math.Floor(p.Max); v++) vals.Add(v);
                else if ((!e.Oneshot && music) || oneshotContinuous)
                {
                    if (p.Default <= p.Min) vals.Add(p.Max);
                    else if (p.Default >= p.Max) vals.Add(p.Min);
                    else { vals.Add(p.Min); vals.Add(p.Max); }
                }
                foreach (var v in vals)
                    if (Math.Abs(v - p.Default) > 1e-6) list.Add(new Dictionary<ParamInfo, float> { [p] = v });
            }
            return list.Take(MaxVariants).ToList();
        }

        void BakeEvent(EventOut eo)
        {
            var e = eo.E;
            // baseline: first render, metered (routing)
            var first = RenderOnce(e, null, meter: true);
            if (first.Error != null) { eo.Error = first.Error; return; }
            eo.RenderedFrames += first.Frames;
            AssignMeteredBus(eo, first);
            bool looping = !first.StoppedByItself;
            eo.Looping = looping;
            if (e.Oneshot && looping) eo.Capped = true;   // a one-shot that hit the cap: kept as a one-shot take, flagged

            bool music = IsMusic(e) || (eo.Bus?.StartsWith("bus:/MusicBus", StringComparison.Ordinal) ?? false);
            eo.Music = music;
            var variants = Variants(e, oneshotContinuous: false, music);
            bool baseSilent = Analysis.Peak(first.Pcm) < AudibleThr;
            if (baseSilent && e.Controllable.Any())
                variants = Variants(e, oneshotContinuous: true, music);   // the default may mute it (e.g. an intensity of 0): try the far ends too
            eo.Variants = variants.Count;

            // determinism (needed for take counting and for variant de-duplication)
            RenderResult second = null;
            if (!looping || variants.Count > 0)
            {
                second = RenderOnce(e, null, meter: false);
                eo.RenderedFrames += second.Frames;
            }
            bool deterministic = second != null && second.Error == null && Analysis.DiffDb(first.Pcm, second.Pcm) < -60;
            if (!deterministic && second != null && second.Error == null && Analysis.DiffDb(first.Pcm, second.Pcm) < -40)
            {
                // a bus used for the first time in this session can make the first render differ very slightly (-59 dB seen);
                // a third render tells that apart from randomisation
                var third = RenderOnce(e, null, meter: false);
                eo.RenderedFrames += third.Frames;
                if (third.Error == null && Analysis.DiffDb(second.Pcm, third.Pcm) < -60) deterministic = true;
            }
            eo.Randomized = second != null && !deterministic;

            // baseline takes
            var baseTakes = new List<(float[] pcm, int stop)>();
            if (looping && !eo.Capped) baseTakes.Add((first.Pcm, -1));
            else CollectTakes(e, null, first, second, deterministic, MaxTakes, baseTakes, eo);
            var kept = new List<(Dictionary<ParamInfo, float> pv, List<TakeOut> takes, float[] probe)>();
            var baseOuts = EmitTakes(eo, null, baseTakes, looping && !eo.Capped);
            kept.Add((null, baseOuts, baseTakes.Count > 0 ? Probe(baseTakes[0].pcm) : null));
            // the first audible parameter set (defaults unless they are silent) and its render: release and 3D are measured there
            Dictionary<ParamInfo, float> measPv = null; float[] measRef = null;
            if (!baseSilent) measRef = first.Pcm;

            foreach (var pv in variants)
            {
                var r1 = RenderOnce(e, pv, meter: false);
                if (r1.Error != null) { lock (eo.Lock) eo.Error ??= "variant: " + r1.Error; continue; }
                eo.RenderedFrames += r1.Frames;
                // duplicate of an already kept variant (or the baseline)? (one-shots compare trimmed takes)
                var probe1 = Probe(looping && !eo.Capped ? r1.Pcm : TrimTail(r1.Pcm));
                var dup = kept.FirstOrDefault(k => k.probe != null && SameSound(k.probe, probe1, deterministic));
                if (dup.takes != null)
                {
                    eo.DupVariants++;
                    foreach (var t in dup.takes)
                        lock (eo.Lock) eo.Takes.Add(CloneWithParams(t, pv));
                    continue;
                }
                var vt = new List<(float[] pcm, int stop)>();
                bool vLoop = looping && !eo.Capped;
                if (vLoop) vt.Add((r1.Pcm, -1));
                else
                {
                    RenderResult r2 = null;
                    if (eo.Randomized) { r2 = RenderOnce(e, pv, meter: false); eo.RenderedFrames += r2.Frames; }
                    CollectTakes(e, pv, r1, r2, !eo.Randomized, MaxVariantTakes, vt, eo);
                }
                if (measRef == null && vt.Count > 0) { measPv = pv; measRef = r1.Pcm; }
                kept.Add((pv, EmitTakes(eo, pv, vt, vLoop), vt.Count > 0 ? Probe(vt[0].pcm) : null));
            }
            lock (eo.Lock)
            {
                eo.Silent = eo.Takes.Count == 0 && eo.Error == null;
                // parameter-gated events (draw_power_loop at power 0, river_loop, ...): FMOD is silent at the defaults, so the
                // mixer must be too. The bake has no take for that case; say so explicitly.
                if (!eo.Silent && e.Controllable.Any() && !eo.Takes.Any(t => t.ParamValues == null))
                {
                    eo.SilentAtDefault = true;
                    if (first.StoppedByItself) eo.SilentStopFrame = first.StopFrame;
                }
            }
            if (measRef == null) return;
            // stop(ALLOWFADEOUT): the authored release of loops (AHDSR, master-track fades, effect tails)
            if (looping && !eo.Capped)
            {
                try { eo.Release = MeasureRelease(eo, measPv, measRef); }
                catch (Exception ex) { lock (eo.Lock) eo.Error ??= "release: " + ex.Message; }
            }
            // 3D: distance rolloff and pan, measured with the event's own content at positions around the listener
            if (e.Is3D)
            {
                try
                {
                    // loops with content that never repeats (crossfaded: scatterers, random instruments) are random too, although
                    // the loop path never renders them twice; they also get a longer window (their energy comes in bursts)
                    bool randomLoop = eo.Looping && !eo.Capped && eo.LoopKind != "exact";
                    int cap = randomLoop ? 4 * R : SpatialCapFrames;
                    (double el, double er)? Ren(float x, float z, int reps)
                    {
                        double el = 0, er = 0;
                        for (int k = 0; k < reps; k++)
                        {
                            var rr = RenderOnce(e, measPv, false, new RenderOpts { MaxFrames = cap, TailMaxFrames = R / 2, Position = (x, 0, z) });
                            if (rr.Error != null) return null;
                            eo.SpatialFrames += rr.Frames;
                            var en = Spatial.Energy(rr.Pcm); el += en.el; er += en.er;
                        }
                        return (el / reps, er / reps);
                    }
                    eo.Spatial = Spatial.Measure(e, eo.Randomized || randomLoop, Ren);
                }
                catch (Exception ex) { lock (eo.Lock) eo.Error ??= "spatial: " + ex.Message; }
            }
        }

        const int SpatialCapFrames = 5 * R / 2;   // 3D renders: the first 2.5 s (+ 0.5 s tail) of the event

        /// <summary>Release of a loop: render to a settled point, call stop(ALLOWFADEOUT), keep mixing until FMOD reports STOPPED
        /// and the output is silent. Envelope in 512-frame windows from the call, made non-increasing; points where it crosses
        /// -1, -3, -6, -10, -15, -20, -30, -40, -50 and -60 dB.</summary>
        ReleaseOut MeasureRelease(EventOut eo, Dictionary<ParamInfo, float> pv, float[] reference)
        {
            const int W = 512;
            int firstSig = Analysis.FirstAbove(reference, AudibleThr);
            if (firstSig < 0) return null;
            int refFrames = reference.Length / 2;
            int stopAt = Math.Clamp(firstSig + 4 * R, R, Math.Max(R, refFrames - 2 * R));
            var rr = RenderOnce(eo.E, pv, false, new RenderOpts { MaxFrames = stopAt + 4 * R, StopAtFrame = stopAt, AfterStopFrames = 20 * R, TailMaxFrames = 5 * R });
            eo.ReleaseFrames += rr.Frames;
            if (rr.Error != null || rr.StopCallFrame < 0) return null;
            var o = new ReleaseOut { StopAtFrame = stopAt };
            int sc = rr.StopCallFrame, frames = rr.Frames;
            if (rr.StopFrame >= 0) o.StopMs = (rr.StopFrame - sc) * 1000.0 / R;
            static double En(float[] x, int f0, int n) { double e = 0; int a = Math.Max(0, f0) * 2, b = Math.Min(x.Length, (f0 + n) * 2); for (int i = a; i < b; i++) e += x[i] * (double)x[i]; return e; }
            // same render up to the call? (deterministic): ratio against the reference, else against the level before the call
            int pre = Math.Min(R / 2, sc);
            double eDiff = 0, eRef = 0;
            for (int i = (sc - pre) * 2; i < sc * 2 && i < reference.Length && i < rr.Pcm.Length; i++) { double d = rr.Pcm[i] - reference[i]; eDiff += d * d; eRef += reference[i] * (double)reference[i]; }
            o.MatchDb = eRef > 0 ? 10 * Math.Log10(Math.Max(eDiff, 1e-30) / eRef) : 0;
            bool ratio = eRef > 0 && o.MatchDb < -30;
            o.From = ratio ? "ratio" : "level";
            double lpre = En(rr.Pcm, sc - R, R) / R * W;          // mean window energy over the second before the call
            if (lpre <= 1e-16) return null;
            var g = new List<double>();
            double held = 1;
            for (int f = sc; f + W <= frames; f += W)
            {
                double er = En(rr.Pcm, f, W), eref;
                if (ratio && f + W <= refFrames) { eref = En(reference, f, W); if (eref < 1e-3 * lpre) eref = -1; }   // a quiet reference window says nothing
                else eref = lpre;
                double gi;
                if (!ratio)
                {
                    // level: an 8-window running mean (85 ms) evens out the content's own envelope
                    double acc = 0; int nw = 0;
                    for (int k = Math.Max(sc, f - 7 * W); k <= f; k += W) { acc += En(rr.Pcm, k, W); nw++; }
                    gi = Math.Sqrt(acc / nw / lpre);
                }
                else gi = eref < 0 ? held : Math.Sqrt(er / eref);
                gi = Math.Min(gi, held);   // non-increasing
                held = gi;
                g.Add(gi);
            }
            int w60 = g.FindIndex(x => x <= 1e-3);
            o.Capped = w60 < 0;
            o.ReleaseMs = (w60 < 0 ? g.Count : w60 + 1) * (double)W * 1000 / R;
            o.Curve.Add((0, 1));
            foreach (var thr in new[] { -1, -3, -6, -10, -15, -20, -30, -40, -50 })
            {
                int w = g.FindIndex(x => x <= Math.Pow(10, thr / 20.0));
                if (w < 0 || (w60 >= 0 && w >= w60)) break;
                double ms = (w + 0.5) * W * 1000.0 / R, gain = Math.Pow(10, thr / 20.0);
                if (o.Curve[^1].ms == ms) o.Curve[^1] = (ms, gain);   // several thresholds inside one window: keep the lowest
                else o.Curve.Add((ms, gain));
            }
            o.Curve.Add((Math.Max(o.ReleaseMs, o.Curve[^1].ms + 1), 0));
            return o;
        }

        RenderResult RenderOnce(EventInfo e, Dictionary<ParamInfo, float> pv, bool meter, RenderOpts custom = null)
        {
            var o = custom ?? new RenderOpts { Meter = meter, MaxFrames = e.Oneshot ? OneshotCap(e) : LoopRenderFrames(e) };
            // metering (first render only) covers 4 s from the first audible block, so late starts are routed too
            var res = _r.Render(e, pv, o);
            if (_r.NeedsReset) Rebuild();
            return res;
        }

        public int Rebuilds;
        List<EventInfo> _events;

        /// <summary>Fresh Studio system (same seed, banks, meters); every EventInfo/ParamInfo/BusInfo is re-pointed in place
        /// by GUID / name, so the bake continues with the same objects.</summary>
        void Rebuild()
        {
            long updates = _s.Updates, missed = _s.MissedCallbacks;
            _s.Dispose();
            _s = Program.OpenSession(out var ev2, out var bu2, out _, quiet: true);
            _s.Updates += updates; _s.MissedCallbacks += missed;
            var eg = ev2.ToDictionary(x => x.Guid);
            foreach (var e in _events)
            {
                var n = eg[e.Guid];
                e.Desc = n.Desc;
                foreach (var p in e.Params) { var np = n.Params.First(q => q.Name == p.Name); p.Id = np.Id; }
            }
            var bg = bu2.ToDictionary(x => x.Guid);
            foreach (var b in _buses) b.Bus = bg[b.Guid].Bus;
            _r.Rebind(_s);
            Rebuilds++;
        }

        /// <summary>One-shot takes: 1 if deterministic, else up to max distinct renders (residual &gt; -40 dB between them).</summary>
        void CollectTakes(EventInfo e, Dictionary<ParamInfo, float> pv, RenderResult first, RenderResult second, bool deterministic, int max, List<(float[] pcm, int stop)> into, EventOut eo)
        {
            void Consider(RenderResult r)
            {
                var t = TrimTail(r.Pcm);
                if (Analysis.Peak(t) < AudibleThr) return;
                if (into.Any(k => Analysis.DiffDb(k.pcm, t) < -40)) return;
                into.Add((t, r.StoppedByItself ? r.StopFrame : -1));
            }
            Consider(first);
            if (deterministic || second == null || second.Error != null) return;
            Consider(second);
            for (int i = 2; i < max; i++)
            {
                var r = RenderOnce(e, pv, meter: false);
                if (r.Error != null) break;
                eo.RenderedFrames += r.Frames;
                Consider(r);
            }
            while (into.Count > max) into.RemoveAt(into.Count - 1);
        }

        /// <summary>What variant de-duplication compares: the first 20 s (memory bound with many long music variants).</summary>
        static float[] Probe(float[] st) => st.Length <= 40 * R ? st : Analysis.Slice(st, 0, 20 * R);

        static float[] TrimTail(float[] st)
        {
            int last = Analysis.LastAbove(st, TrimThr);
            if (last <= 0) return Array.Empty<float>();
            return Analysis.Slice(st, 0, last);
        }

        /// <summary>Two renders sound the same: waveform residual below -40 dB when the event is deterministic; otherwise level
        /// within 0.5 dB, length within 10 % and the 24-band long-term spectrum within 1.5 dB everywhere.</summary>
        static bool SameSound(float[] a, float[] b, bool deterministic)
        {
            if (deterministic) return Analysis.DiffDb(a, b) < -40;
            double la = Analysis.Db(Analysis.Rms(a)), lb = Analysis.Db(Analysis.Rms(b));
            if (Math.Abs(la - lb) > 0.5) return false;
            if (Math.Abs(a.Length - b.Length) > 0.1 * Math.Max(a.Length, b.Length)) return false;
            var sa = Spectrum.Bands(a); var sb = Spectrum.Bands(b);
            for (int i = 0; i < sa.Length; i++) if (Math.Abs(sa[i] - sb[i]) > 1.5 && Math.Max(sa[i], sb[i]) > -100) return false;
            return true;
        }

        static TakeOut CloneWithParams(TakeOut t, Dictionary<ParamInfo, float> pv) => new()
        {
            DupOf = t.Src, ParamValues = pv?.ToDictionary(kv => kv.Key.Name, kv => kv.Value),
        };

        /// <summary>Queues the encode of each take; for loops the loop is built here (exact repeat, else crossfaded).</summary>
        List<TakeOut> EmitTakes(EventOut eo, Dictionary<ParamInfo, float> pv, List<(float[] pcm, int stop)> takes, bool loop)
        {
            var outs = new List<TakeOut>();
            int vIndex;
            lock (eo.Lock) vIndex = eo.Takes.Select(t => t.ParamValues == null ? "" : string.Join(",", t.ParamValues.Select(kv => kv.Key + "=" + kv.Value))).Distinct().Count();
            for (int j = 0; j < takes.Count; j++)
            {
                var pcm = takes[j].pcm;
                (int s, int e)? lp = null;
                string loopKind = null;
                if (loop)
                {
                    var built = BuildLoop(eo.E, pcm, eo.Music, out loopKind);
                    if (built.pcm == null) continue;    // silent loop
                    pcm = built.pcm; lp = (built.s, built.e);
                    lock (eo.Lock) eo.LoopKind = eo.LoopKind == null ? loopKind : eo.LoopKind == loopKind ? loopKind : "mixed";
                }
                if (pcm.Length == 0) continue;
                var t = new TakeOut { Frames = pcm.Length / 2, Loop = lp, ParamValues = pv?.ToDictionary(kv => kv.Key.Name, kv => kv.Value), StopFrame = lp == null ? takes[j].stop : -1 };
                string rel = FileFor(eo.E, pv == null ? null : vIndex, j);
                t.File = rel;
                outs.Add(t);
                lock (eo.Lock) eo.Takes.Add(t);
                _queue.Add(new EncodeJob { Ev = eo, Take = t, Pcm = pcm, Loop = lp != null, AbsPath = Path.Combine(_dir, rel) });
            }
            return outs;
        }

        /// <summary>Exact repeat when the render has one (file = intro + one period); else a crossfaded loop whose body is
        /// capped at 30 s (90 s for music) after a 2 s settle past the first audible frame.</summary>
        (float[] pcm, int s, int e) BuildLoop(EventInfo e, float[] st, bool music, out string kind)
        {
            kind = null;
            int frames = st.Length / 2;
            int firstSig = Analysis.FirstAbove(st, AudibleThr);
            if (firstSig < 0) return (null, 0, 0);
            int lf = (int)Math.Min(int.MaxValue, (long)e.LengthMs * R / 1000);
            var ex = Analysis.FindExactLoop(st, lf, R / 4);
            if (ex != null)
            {
                kind = "exact";
                return (Analysis.Slice(st, 0, ex.End), ex.Start, ex.End);
            }
            kind = "crossfade";
            int fade = (int)(1.5 * R);
            int b0 = Math.Clamp(firstSig + 2 * R, fade, frames / 3);
            int b1 = Math.Min(frames, b0 + (music ? 90 : 30) * R + fade);
            fade = Math.Min(fade, (b1 - b0) / 4);
            var (pcm, s, en) = Analysis.CrossfadeLoop(st, b0, b1, fade);
            return (pcm, s, en);
        }

        string FileFor(EventInfo e, int? variant, int take)
        {
            string p = e.Path.StartsWith("event:/") ? e.Path[7..] : e.Path;
            var parts = p.Split('/').Select(Sanitize).ToArray();
            string stem = Path.Combine(new[] { "ev" }.Concat(parts).ToArray());
            string suffix = (variant is int v ? $".v{v}" : "") + $".t{take}";
            string rel = stem + suffix;
            lock (_usedFiles)
            {
                if (!_usedFiles.Add(rel)) { rel = stem + "-" + e.Guid.Trim('{', '}')[..8] + suffix; _usedFiles.Add(rel); }
            }
            return rel;   // extension is added by the encoder once the codec is known
        }

        static string Sanitize(string s)
        {
            var c = s.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_').ToArray();
            var r = new string(c).Trim('.');
            return r.Length == 0 ? "_" : r;
        }

        // ------------------------------------------------------------------ routing

        void AssignMeteredBus(EventOut eo, RenderResult r)
        {
            if (r.BusEnergy == null || r.BusEnergy.Count == 0) return;
            double max = r.BusEnergy.Values.Max();
            if (max <= 1e-14) return;
            var cand = r.BusEnergy.Where(kv => kv.Value >= max * 1e-4).Select(kv => kv.Key).ToList();
            static int Depth(string p) => p == "bus:/" ? 0 : p[5..].Split('/').Length;
            var pick = cand.OrderByDescending(Depth).ThenByDescending(p => r.BusEnergy[p]).First();
            eo.Bus = pick; eo.BusFrom = "metered";
            eo.Sends = cand.Where(c => c != pick && !(pick.StartsWith(c == "bus:/" ? "bus:/" : c + "/", StringComparison.Ordinal))).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        void AssignPrefixBuses(List<EventOut> outs)
        {
            // event path prefix (two segments, then one) -> most common metered bus
            var table = new Dictionary<string, Dictionary<string, int>>();
            foreach (var eo in outs.Where(x => x.BusFrom == "metered"))
                foreach (var k in Prefixes(eo.E.Path))
                {
                    if (!table.TryGetValue(k, out var d)) table[k] = d = new();
                    d[eo.Bus] = d.GetValueOrDefault(eo.Bus) + 1;
                }
            foreach (var eo in outs.Where(x => x.Bus == null))
            {
                foreach (var k in Prefixes(eo.E.Path))
                    if (table.TryGetValue(k, out var d)) { eo.Bus = d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key; eo.BusFrom = "prefix:" + k; break; }
                if (eo.Bus == null) { eo.Bus = "bus:/"; eo.BusFrom = "default"; }
            }
        }

        static IEnumerable<string> Prefixes(string path)
        {
            var seg = path.Split('/');
            for (int n = seg.Length - 1; n >= 2; n--) yield return string.Join("/", seg.Take(n)) + "/";
        }

        // ------------------------------------------------------------------ encoding (worker threads)

        void Worker()
        {
            foreach (var j in _queue.GetConsumingEnumerable())
            {
                try { EncodeTake(j); }
                catch (Exception ex) { _encodeErrors.Add($"{j.Take.File}: {ex.Message}"); lock (j.Ev.Lock) j.Ev.Error ??= "encode: " + ex.Message; }
            }
        }

        static void EncodeTake(EncodeJob j)
        {
            var t = j.Take;
            var st = j.Pcm;
            t.SideDb = Analysis.SideToMidDb(st);
            bool mono = Analysis.IsMono(st);
            float[] x = mono ? Analysis.ToMono(st) : st;
            int ch = mono ? 1 : 2;
            t.Channels = ch;
            t.PeakDb = Analysis.Db(Analysis.Peak(x));
            t.RmsDb = Analysis.Db(Analysis.Rms(x));
            t.Clipped = Encode.ClipCount(x);
            if (t.Loop is { } lp) t.SeamRatio = Analysis.SeamRatio(st, lp.s, lp.e);
            Directory.CreateDirectory(Path.GetDirectoryName(j.AbsPath));
            byte[] bytes;
            if (t.Frames <= ShortFrames)
            {
                var pcm16 = Encode.ToPcm16(x);
                var ima = Encode.ImaWav(pcm16, ch, R);
                var dec = Encode.ReadWav(ima).pcm;
                double snr = Snr(pcm16, dec);
                if (snr >= 20 || t.RmsDb < -60)
                {
                    t.Codec = "ima-adpcm"; bytes = ima; t.QualityDb = snr; t.QualityKind = "snr-vs-pcm16";
                }
                else
                {
                    t.Codec = "pcm16"; bytes = Encode.WavPcm16(pcm16, ch, R); t.QualityDb = 96; t.QualityKind = "pcm16 (adpcm snr " + snr.ToString("F1") + " dB)";
                }
                t.File += ".wav";
            }
            else
            {
                // music 128/64 kbps, other long takes 96/48 kbps (stereo/mono), AAC-LC 48 kHz
                int br = j.Ev.Music ? (ch == 2 ? 128000 : 64000) : (ch == 2 ? 96000 : 48000);
                bytes = Encode.AacAdts(x, ch, R, br);
                var info = Encode.AdtsInfo(bytes);
                if (info.profile != 1 || info.channels != ch) throw new InvalidDataException($"ADTS profile {info.profile} ch {info.channels}");
                var (dec, dch) = Encode.DecodeAac(bytes);
                var (delay, corr, resid) = AacAlign(x, ch, dec, dch);
                // afconvert's AAC-LC priming is a constant 2112 frames; the cross-correlation estimate can land ±1 on some
                // content, so the constant is recorded and real disagreements are reported
                t.EncoderDelay = Math.Abs(delay - AacPriming) <= 2 ? AacPriming : delay;
                t.MeasuredDelay = delay;
                t.QualityDb = -resid; t.QualityKind = $"aac-corr {corr:F4}";
                // against the recorded delay (2112): a cross-correlation estimate one frame off must not fail a take that fits exactly
                if ((long)info.frames * 1024 < t.EncoderDelay + t.Frames) throw new InvalidDataException($"AAC too short: {info.frames} frames for {t.Frames}+{t.EncoderDelay}");
                t.Codec = "aac-adts"; t.File += ".aac";
            }
            string abs = j.AbsPath + (t.Codec == "aac-adts" ? ".aac" : ".wav");
            File.WriteAllBytes(abs, bytes);
            t.Bytes = bytes.Length;
        }

        const int AacPriming = 2112;

        static double Snr(short[] src, float[] dec)
        {
            double es = 0, en = 0;
            for (int i = 0; i < src.Length && i < dec.Length; i++) { double s = src[i] / 32767.0; double d = s - dec[i]; es += s * s; en += d * d; }
            return en <= 1e-20 ? 120 : 10 * Math.Log10(Math.Max(es, 1e-20) / en);
        }

        /// <summary>Finds the decoder offset (priming) by cross-correlation in a high-energy region and returns the residual
        /// (dB, relative to the signal) of the aligned decode over the whole take.</summary>
        static (int delay, double corr, double residDb) AacAlign(float[] x, int ch, float[] dec, int dch)
        {
            int frames = x.Length / ch, dframes = dec.Length / dch;
            // pick the 8192-frame window with the most energy
            const int W = 8192;
            int bestS = 0; double bestE = -1;
            for (int s0 = 0; s0 + W <= frames; s0 += W / 2)
            {
                double e = 0; for (int f = s0; f < s0 + W; f++) { double v = x[f * ch]; e += v * v; }
                if (e > bestE) { bestE = e; bestS = s0; }
            }
            int bestD = 0; double bestC = double.NegativeInfinity;
            for (int d = 0; d <= 4096; d++)
            {
                if (bestS + d + W > dframes) break;
                double sxy = 0, syy = 0, sxx = 0;
                for (int f = 0; f < W; f++) { double a = x[(bestS + f) * ch], b = dec[(bestS + d + f) * dch]; sxy += a * b; syy += b * b; sxx += a * a; }
                double c = sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
                if (c > bestC) { bestC = c; bestD = d; }
            }
            double es = 0, en = 0;
            for (int f = 0; f < frames && f + bestD < dframes; f++)
                for (int c = 0; c < ch; c++) { double a = x[f * ch + c], b = dec[(f + bestD) * dch + Math.Min(c, dch - 1)]; es += a * a; en += (a - b) * (a - b); }
            double resid = es <= 1e-20 ? -120 : 10 * Math.Log10(Math.Max(en, 1e-24) / es);
            return (bestD, bestC, resid);
        }

        // ------------------------------------------------------------------ manifest + report

        void WriteManifest(List<EventOut> outs, List<EventInfo> snaps, List<Renderer.SnapshotState> snapVol, Dictionary<string, double> baseline)
        {
            var root = new JsonObject
            {
                ["version"] = 1,
                ["sampleRate"] = R,
                ["generator"] = "tools/FmodBake (FMOD Studio 2.02.07, NOSOUND_NRT, seed 0x" + Session.Seed.ToString("X8") + ")",
                ["buses"] = new JsonArray(_buses.Select(b => (JsonNode)new JsonObject
                {
                    ["path"] = b.Path,
                    ["inferredFrom"] = "bank:" + b.Bank,
                    ["defaultVolume"] = 1.0,
                    ["guid"] = b.Guid,
                    ["faderGain"] = baseline.TryGetValue(b.Path, out var g) && !double.IsNaN(g) ? (JsonNode)Math.Round(g, 5) : null,
                }).ToArray()),
            };
            var evs = new JsonArray();
            foreach (var eo in outs.OrderBy(x => x.E.Path, StringComparer.Ordinal))
            {
                var e = eo.E;
                var o = new JsonObject
                {
                    ["path"] = e.Path,
                    ["guid"] = e.Guid,
                    ["oneshot"] = e.Oneshot,
                    ["lengthMs"] = e.LengthMs > 0 ? (JsonNode)e.LengthMs : null,
                    ["is3D"] = e.Is3D,
                    ["minDistance"] = e.MinDist,
                    ["maxDistance"] = e.MaxDist,
                    ["bus"] = eo.Bus,
                    ["busFrom"] = eo.BusFrom,
                    ["params"] = new JsonArray(e.Controllable.Select(p => (JsonNode)new JsonObject
                    {
                        ["name"] = p.Name, ["min"] = p.Min, ["max"] = p.Max, ["default"] = p.Default, ["global"] = p.Global,
                        ["discrete"] = p.Discrete,
                        ["labels"] = p.Labels == null ? null : new JsonArray(p.Labels.Select(l => (JsonNode)l).ToArray()),
                    }).ToArray()),
                    ["takes"] = new JsonArray(eo.Takes.Where(t => t.Src.Codec != null).Select(t => (JsonNode)new JsonObject
                    {
                        ["file"] = t.Src.File.Replace('\\', '/'),
                        ["codec"] = t.Src.Codec,
                        ["channels"] = t.Src.Channels,
                        ["rate"] = t.Src.Rate,
                        ["frames"] = t.Src.Frames,
                        ["loop"] = t.Src.Loop is { } lp ? new JsonObject { ["start"] = lp.s, ["end"] = lp.e } : null,
                        ["paramValues"] = t.ParamValues == null ? null : new JsonObject(t.ParamValues.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value))),
                        ["weight"] = 1,
                        ["encoderDelay"] = t.Src.Codec == "aac-adts" ? (JsonNode)t.Src.EncoderDelay : null,
                        ["stopFrame"] = t.Src.StopFrame >= 0 ? (JsonNode)t.Src.StopFrame : null,
                    }).ToArray()),
                };
                if (eo.SilentAtDefault)
                {
                    o["silentAtDefault"] = true;
                    if (e.Oneshot && eo.SilentStopFrame >= 0) o["silentStopFrame"] = eo.SilentStopFrame;
                }
                if (eo.Release is { } rl)
                {
                    o["releaseMs"] = Math.Round(rl.ReleaseMs, 1);
                    o["releaseStopMs"] = rl.StopMs >= 0 ? (JsonNode)Math.Round(rl.StopMs, 1) : null;
                    o["releaseCurve"] = new JsonArray(rl.Curve.Select(c => (JsonNode)new JsonObject { ["ms"] = Math.Round(c.ms, 1), ["gain"] = Math.Round(c.gain, 5) }).ToArray());
                    o["releaseFrom"] = rl.From;
                }
                if (eo.Spatial is { } sp)
                {
                    o["rolloff"] = new JsonArray(sp.Rolloff.Select(p => (JsonNode)new JsonObject { ["d"] = Math.Round(p.d, 3), ["gain"] = Math.Round(p.gain, 5) }).ToArray());
                    o["pan"] = new JsonObject
                    {
                        ["distance"] = Math.Round(sp.PanDistance, 3),
                        ["front"] = Math.Round(sp.FrontShare, 4),
                        ["az"] = new JsonArray(sp.PanAz.Select(p => (JsonNode)new JsonObject { ["az"] = p.az, ["far"] = Math.Round(p.far, 5) }).ToArray()),
                        ["near"] = new JsonArray(sp.PanNear.Select(p => (JsonNode)new JsonObject { ["d"] = Math.Round(p.d, 3), ["blend"] = Math.Round(p.blend, 4) }).ToArray()),
                    };
                }
                var auto = e.Params.Where(p => p.Automatic).Select(p => (JsonNode)p.Name).ToArray();
                if (auto.Length > 0) o["automaticParams"] = new JsonArray(auto);
                if (eo.Sends.Count > 0) o["sends"] = new JsonArray(eo.Sends.Select(s => (JsonNode)s).ToArray());
                if (eo.Randomized) o["randomized"] = true;
                if (e.IsStream) o["stream"] = true;
                if (eo.Capped) o["capped"] = true;
                if (eo.Silent) o["silent"] = true;
                if (_r.Leaks.TryGetValue(e.Path, out var lk)) o["leaked"] = new JsonArray(lk.Select(x => (JsonNode)x).ToArray());
                if (eo.Error != null) o["error"] = eo.Error;
                evs.Add(o);
            }
            root["events"] = evs;
            static JsonObject Vols(Dictionary<string, double> d) =>
                new(d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)Math.Round(kv.Value, 5))));
            root["snapshots"] = new JsonArray(snaps.OrderBy(s => s.Path, StringComparer.Ordinal).Select(s =>
            {
                var st = snapVol.Where(x => x.Path == s.Path).ToList();
                var def = st.FirstOrDefault(x => x.Params == null);
                var o = new JsonObject
                {
                    ["path"] = s.Path,
                    ["guid"] = s.Guid,
                    ["busVolumes"] = def != null ? Vols(def.Own) : new JsonObject(),
                    ["measured"] = def != null,
                };
                var ctl = s.Controllable.ToList();
                if (ctl.Count > 0)
                {
                    o["params"] = new JsonArray(ctl.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["min"] = p.Min, ["max"] = p.Max, ["default"] = p.Default, ["global"] = p.Global }).ToArray());
                    o["curve"] = new JsonArray(st.Where(x => x.Params != null).Select(x => (JsonNode)new JsonObject
                    {
                        ["paramValues"] = new JsonObject(x.Params.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value))),
                        ["busVolumes"] = Vols(x.Own),
                    }).ToArray());
                }
                return (JsonNode)o;
            }).ToArray());
            File.WriteAllText(Path.Combine(_dir, "manifest.json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        int Report(List<EventOut> outs, List<EventInfo> snaps, double totalSec, double renderSec, double encodeSec, int vcas)
        {
            var takes = outs.SelectMany(o => o.Takes).ToList();
            var files = takes.Where(t => t.DupOf == null && t.Bytes > 0).ToList();
            var byCodec = files.GroupBy(t => t.Codec).ToDictionary(g => g.Key, g => new { takes = g.Count(), bytes = g.Sum(t => t.Bytes), seconds = Math.Round(g.Sum(t => (double)t.Frames) / R, 1) });
            var failed = outs.Where(o => o.Error != null).Select(o => new { o.E.Path, o.Error }).ToList();
            double audioSec = outs.Sum(o => (double)(o.RenderedFrames + o.SpatialFrames + o.ReleaseFrames)) / R;
            var rep = new
            {
                set = _o.Set,
                events = outs.Count,
                oneshot = outs.Count(o => o.E.Oneshot),
                notOneshot = outs.Count(o => !o.E.Oneshot),
                looping = outs.Count(o => o.Looping && !o.Capped),
                loopExact = outs.Count(o => o.LoopKind == "exact"),
                loopCrossfade = outs.Count(o => o.LoopKind == "crossfade"),
                randomized = outs.Count(o => o.Randomized),
                withVariants = outs.Count(o => o.Variants > 0),
                variantsRendered = outs.Sum(o => o.Variants),
                variantsDeduplicated = outs.Sum(o => o.DupVariants),
                silent = outs.Count(o => o.Silent),
                capped = outs.Where(o => o.Capped).Select(o => o.E.Path).ToList(),
                failed,
                encodeErrors = _encodeErrors.ToList(),
                leakingEvents = _r.Leaks.Count,
                sessionRebuilds = Rebuilds,
                leaks = _r.Leaks,
                busMetered = outs.Count(o => o.BusFrom == "metered"),
                busPrefix = outs.Count(o => o.BusFrom?.StartsWith("prefix") == true),
                busDefault = outs.Count(o => o.BusFrom == "default"),
                snapshots = snaps.Count,
                vcas,
                takes = takes.Count,
                takeFiles = files.Count,
                bytesTotal = files.Sum(t => t.Bytes),
                byCodec,
                mono = files.Count(t => t.Channels == 1),
                stereo = files.Count(t => t.Channels == 2),
                clippedTakes = files.Count(t => t.Clipped > 0),
                adpcmSnrDb = Stats(files.Where(t => t.Codec == "ima-adpcm").Select(t => t.QualityDb)),
                aacResidualDb = Stats(files.Where(t => t.Codec == "aac-adts").Select(t => -t.QualityDb)),
                aacDelay = files.Where(t => t.Codec == "aac-adts").GroupBy(t => t.EncoderDelay).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                aacMeasuredDelay = files.Where(t => t.Codec == "aac-adts").GroupBy(t => t.MeasuredDelay).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                sideToMidDb = Stats(files.Select(t => t.SideDb)),
                stereoTakesSideBelow30dB = files.Count(t => t.Channels == 2 && t.SideDb < -30),
                loopSeamRatio = Stats(files.Where(t => !double.IsNaN(t.SeamRatio) && !double.IsInfinity(t.SeamRatio)).Select(t => t.SeamRatio)),
                // STOPPED vs the end of the take (ms; positive = the take's audio goes on after STOPPED: bus/reverb tail)
                stopFrame = new
                {
                    takes = files.Count(t => t.StopFrame >= 0),
                    takeEndMinusStopMs = Stats(files.Where(t => t.StopFrame >= 0).Select(t => (t.Frames - t.StopFrame) * 1000.0 / R)),
                    takesEndingBeforeStop = files.Count(t => t.StopFrame >= 0 && t.Frames < t.StopFrame),
                    takesEndingOver1sBeforeStop = files.Count(t => t.StopFrame >= 0 && t.StopFrame - t.Frames > R),
                },
                silentAtDefault = outs.Where(o => o.SilentAtDefault).Select(o => o.E.Path).ToList(),
                release = new
                {
                    measured = outs.Count(o => o.Release != null),
                    loops = outs.Count(o => o.Looping && !o.Capped),
                    ratio = outs.Count(o => o.Release?.From == "ratio"),
                    level = outs.Count(o => o.Release?.From == "level"),
                    capped = outs.Where(o => o.Release?.Capped == true).Select(o => o.E.Path).ToList(),
                    releaseMs = Stats(outs.Where(o => o.Release != null).Select(o => o.Release.ReleaseMs)),
                    stopMs = Stats(outs.Where(o => o.Release != null && o.Release.StopMs >= 0).Select(o => o.Release.StopMs)),
                    noStopped = outs.Where(o => o.Release != null && o.Release.StopMs < 0).Select(o => o.E.Path).ToList(),
                    renderedSeconds = Math.Round(outs.Sum(o => (double)o.ReleaseFrames) / R, 1),
                },
                spatial = new
                {
                    measured = outs.Count(o => o.Spatial != null),
                    is3D = outs.Count(o => o.E.Is3D),
                    renders = outs.Sum(o => o.Spatial?.Renders ?? 0),
                    renderedSeconds = Math.Round(outs.Sum(o => (double)o.SpatialFrames) / R, 1),
                    rolloffAtQuarterDb = Stats(outs.Where(o => o.Spatial?.Rolloff.Count >= 5).Select(o => Analysis.Db(o.Spatial.Rolloff[4].gain))),
                    far90Db = Stats(outs.Where(o => o.Spatial?.PanAz.Count == Spatial.Azimuths.Length).Select(o => 10 * Math.Log10(Math.Max(o.Spatial.PanAz[^1].far, 1e-12) / Math.Max(1 - o.Spatial.PanAz[^1].far, 1e-12)))),
                },
                renderedAudioSeconds = Math.Round(audioSec, 1),
                mixes = _s.Updates,
                missedCallbacks = _s.MissedCallbacks,
                seconds = new { total = Math.Round(totalSec, 1), render = Math.Round(renderSec, 1), encodeTail = Math.Round(encodeSec, 1) },
                realtimeFactor = Math.Round(audioSec / Math.Max(renderSec, 1e-3), 1),
                perTake = files.OrderBy(t => t.File, StringComparer.Ordinal).Select(t => new
                {
                    t.File, t.Codec, t.Channels, t.Frames, t.Bytes, peakDb = Math.Round(t.PeakDb, 2), rmsDb = Math.Round(t.RmsDb, 2),
                    qualityDb = Math.Round(t.QualityDb, 2), t.QualityKind, t.Clipped, t.EncoderDelay,
                    seam = double.IsNaN(t.SeamRatio) ? (double?)null : Math.Round(t.SeamRatio, 3), sideDb = Math.Round(t.SideDb, 1), t.StopFrame,
                }),
            };
            File.WriteAllText(Path.Combine(_dir, "report.json"), JsonSerializer.Serialize(rep, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Program.Log($"DONE '{_o.Set}': {rep.events} events ({rep.oneshot} one-shot, {rep.notOneshot} not; {rep.looping} looping: {rep.loopExact} exact, {rep.loopCrossfade} crossfade), " +
                        $"{rep.randomized} randomized, {rep.withVariants} with variants ({rep.variantsRendered} rendered, {rep.variantsDeduplicated} duplicates), " +
                        $"{rep.silent} silent, {rep.capped.Count} capped, {failed.Count} failed, {_encodeErrors.Count} encode errors");
            Program.Log($"  takes {rep.takes} ({rep.takeFiles} files, {rep.mono} mono / {rep.stereo} stereo), {rep.bytesTotal / 1048576.0:F1} MiB: " +
                        string.Join(", ", byCodec.Select(kv => $"{kv.Key} {kv.Value.takes} takes {kv.Value.bytes / 1048576.0:F1} MiB {kv.Value.seconds} s")));
            Program.Log($"  isolation: {_r.Leaks.Count} events left state behind (reset after each render, {Rebuilds} session rebuilds; see report.leaks)");
            Program.Log($"  routing: metered {rep.busMetered}, prefix {rep.busPrefix}, default {rep.busDefault}; clipped takes {rep.clippedTakes}; AAC delay {string.Join(",", rep.aacDelay.Select(kv => kv.Key + "x" + kv.Value))}");
            Program.Log($"  stopFrame on {rep.stopFrame.takes} takes; silentAtDefault {rep.silentAtDefault.Count}; release measured {rep.release.measured}/{rep.release.loops} loops " +
                        $"({rep.release.ratio} ratio, {rep.release.level} level, {rep.release.capped.Count} capped); 3D measured {rep.spatial.measured}/{rep.spatial.is3D} ({rep.spatial.renders} renders, {rep.spatial.renderedSeconds} s)");
            Program.Log($"  time: total {rep.seconds.total} s (render {rep.seconds.render} s for {rep.renderedAudioSeconds} s of audio = {rep.realtimeFactor}x realtime, encode tail {rep.seconds.encodeTail} s)");
            foreach (var f in failed.Take(20)) Program.Log($"  FAILED {f.Path}: {f.Error}");
            return failed.Count == 0 && _encodeErrors.IsEmpty ? 0 : 1;
        }

        static object Stats(IEnumerable<double> xs)
        {
            var a = xs.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToArray();
            if (a.Length == 0) return null;
            return new { n = a.Length, min = Math.Round(a[0], 2), p10 = Math.Round(a[a.Length / 10], 2), median = Math.Round(a[a.Length / 2], 2), p90 = Math.Round(a[a.Length * 9 / 10], 2), max = Math.Round(a[^1], 2) };
        }
    }
}

/// <summary>24-band long-term spectrum (dB) for similarity checks.</summary>
static class Spectrum
{
    public static double[] Bands(float[] st)
    {
        const int N = 2048, B = 24;
        var bands = new double[B];
        int frames = st.Length / 2;
        if (frames < N) { var pad = new float[N * 2]; Array.Copy(st, pad, st.Length); st = pad; frames = N; }
        var re = new double[N]; var im = new double[N];
        int count = 0;
        int hop = Math.Max(N, (frames - N) / 64);   // at most ~64 windows
        for (int s0 = 0; s0 + N <= frames; s0 += hop)
        {
            for (int i = 0; i < N; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N); re[i] = (st[2 * (s0 + i)] + st[2 * (s0 + i) + 1]) * 0.5 * w; im[i] = 0; }
            Analysis.Fft(re, im, false);
            for (int k = 1; k < N / 2; k++)
            {
                double hz = k * (double)Session.Rate / N;
                int b = (int)Math.Clamp(Math.Floor(Math.Log(Math.Max(hz, 40) / 40) / Math.Log(20000.0 / 40) * B), 0, B - 1);
                bands[b] += re[k] * re[k] + im[k] * im[k];
            }
            count++;
        }
        for (int b = 0; b < B; b++) bands[b] = 10 * Math.Log10(Math.Max(bands[b] / Math.Max(1, count), 1e-20));
        return bands;
    }
}
