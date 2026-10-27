namespace Shepherd.FmodBake;

/// <summary>
/// 3D measurement for the Vita mixer: how FMOD changes the level of each output channel with the source position, measured
/// with the event's own content. The event is rendered at positions around the listener (origin, facing +Z, up +Y, right = +X)
/// and the energy per output channel is compared with the same render at distance 0, where the takes are baked. This is the
/// whole FMOD path: spatializer(s) on any track or nested event, timeline/distance automation, master-track effects, the bus
/// and its reverb sends. (A probe signal injected at the event's master track was tried first; it reproduced the panner but
/// missed spatialization that changes along the timeline, e.g. harvest_meat_done: −21.5 vs −8.0 dB L−R at 6 m.)
///
/// Model written to the manifest (all optional fields; vita/notes/sound-bake.md §3D):
///   rolloff: on-axis amplitude gain (energy over the render, L+R) vs distance, relative to distance 0.
///   pan: the share of energy on the channel away from the source ("far"), symmetric between left and right and between front
///        and back (FMOD's stereo panner folds them; measured: identical within 0.01 dB). far(θ) at distance pan.distance for
///        θ = 15..90°, far(0) = 0.5; near[d].blend: how much of that pan applies at distance d &lt; pan.distance (0 at the listener).
///   Mixer gains: q = 0.5 + blend(d)·(far(θ) − 0.5); far channel × rolloff(d)·sqrt(2q), near channel × rolloff(d)·sqrt(2(1−q)).
/// Randomised events: rolloff and the distance-0 reference average two renders per point (render-to-render level spread);
/// the pan shares are ratios inside one render and need no averaging.
/// Everything runs in the silent NRT session (NOSOUND_NRT); nothing is played.
/// </summary>
sealed class SpatialOut
{
    public List<(float d, double gain)> Rolloff = new();
    public float PanDistance;
    public List<(float az, double far)> PanAz = new();
    public List<(float d, double blend)> PanNear = new();
    public double FrontShare;          // L share at distance 0 (the takes' own balance; ~0.5)
    public int Renders;
}

static class Spatial
{
    public static readonly float[] RolloffFractions = { 0f, 0.04f, 0.1f, 0.2f, 0.35f, 0.5f, 0.65f, 0.8f, 0.92f, 1f };
    public static readonly float[] Azimuths = { 15, 30, 45, 60, 75, 90 };
    public static readonly float[] NearFactors = { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2.5f };

    public static float PanDistanceFor(float min, float max) => Math.Max(min * 1.5f, Math.Min(4 * min, min + 0.25f * (max - min)));

    /// <summary>render(x, z, reps) returns (energyL, energyR) of a content render at (x, 0, z), averaged over reps renders,
    /// or null on error. Returns null when the event is silent at distance 0.</summary>
    public static SpatialOut Measure(EventInfo e, bool randomized, Func<float, float, int, (double el, double er)?> render)
    {
        var o = new SpatialOut();
        float min = e.MinDist > 0 ? e.MinDist : 1f, max = e.MaxDist > min ? e.MaxDist : min * 20;
        int reps = randomized ? 2 : 1;
        var r0 = render(0, 0, reps);
        o.Renders += reps;
        if (r0 == null || r0.Value.el + r0.Value.er <= 1e-14) return null;
        o.FrontShare = r0.Value.el / (r0.Value.el + r0.Value.er);
        double f0 = Math.Clamp(o.FrontShare, 1e-3, 1 - 1e-3);
        var roll = new List<(float d, double t)>();
        foreach (var fr in RolloffFractions)
        {
            float d = min + fr * (max - min);
            var x = render(0, d, reps); o.Renders += reps;
            if (x != null) roll.Add((d, x.Value.el + x.Value.er));
        }
        // share of the far channel (L for a source on the right), corrected for the takes' own L/R balance
        double Far((double el, double er) x)
        {
            double t = x.el + x.er;
            if (t <= 1e-14) return 0.5;
            double s = x.el / t;
            double a = s / f0, b = (1 - s) / (1 - f0);
            return a / (a + b);
        }
        o.PanDistance = PanDistanceFor(min, max);
        foreach (var az in Azimuths)
        {
            double th = az * Math.PI / 180;
            var x = render((float)(o.PanDistance * Math.Sin(th)), (float)(o.PanDistance * Math.Cos(th)), 1); o.Renders++;
            if (x != null) o.PanAz.Add((az, Far(x.Value)));
        }
        double far90 = o.PanAz.Count > 0 && o.PanAz[^1].az == 90 ? o.PanAz[^1].far : 0.5;
        var near = new List<(float d, double t)>();
        if (Math.Abs(0.5 - far90) > 1e-3)
            foreach (var k in NearFactors)
            {
                float d = k * min;
                if (d >= o.PanDistance) break;
                var x = render(d, 0, 1); o.Renders++;
                if (x == null) continue;
                o.PanNear.Add((d, Math.Clamp((0.5 - Far(x.Value)) / (0.5 - far90), 0, 1)));
                near.Add((d, x.Value.el + x.Value.er));
            }
        // the distance-0 reference: one render is exact for a deterministic event. A randomised one varies in level from
        // render to render, and the reference error would shift the whole curve, so it pools every render inside the minimum
        // distance (where FMOD does not attenuate; the panner keeps the total), and the curve is capped at 0 dB.
        double t0 = r0.Value.el + r0.Value.er;
        if (randomized)
        {
            double sum = t0 * reps; int cnt = reps;
            foreach (var (d, t) in roll) if (d <= min * 1.0001f) { sum += t * reps; cnt += reps; }
            foreach (var (d, t) in near) if (d <= min * 1.0001f) { sum += t; cnt++; }
            t0 = sum / cnt;
        }
        foreach (var (d, t) in roll) o.Rolloff.Add((d, Math.Sqrt(t / t0)));
        if (randomized) for (int i = 0; i < o.Rolloff.Count; i++) o.Rolloff[i] = (o.Rolloff[i].d, Math.Min(1.0, o.Rolloff[i].gain));
        // the rolloff must not rise with distance, the near blend must not fall (render-to-render spread of random events)
        for (int i = 1; i < o.Rolloff.Count; i++) if (o.Rolloff[i].gain > o.Rolloff[i - 1].gain) o.Rolloff[i] = (o.Rolloff[i].d, o.Rolloff[i - 1].gain);
        for (int i = 1; i < o.PanNear.Count; i++) if (o.PanNear[i].blend < o.PanNear[i - 1].blend) o.PanNear[i] = (o.PanNear[i].d, o.PanNear[i - 1].blend);
        return o;
    }

    /// <summary>Energy per channel of a render (interleaved stereo).</summary>
    public static (double el, double er) Energy(float[] pcm)
    {
        double el = 0, er = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2) { el += pcm[i] * (double)pcm[i]; er += pcm[i + 1] * (double)pcm[i + 1]; }
        return (el, er);
    }
}

/// <summary>spatialdiag &lt;regex&gt; [n] [x,z;x,z;...]: content renders of matching 3D events at positions (dB tables vs distance 0),
/// then the model the bake would write.</summary>
static class SpatialDiag
{
    public static int Run(string[] a)
    {
        var re = new System.Text.RegularExpressions.Regex(a.Length > 0 ? a[0] : ".", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int n = a.Length > 1 ? int.Parse(a[1]) : 3;
        var pos = new List<(float, float)>();
        if (a.Length > 2) foreach (var q in a[2].Split(';')) { var xz = q.Split(','); pos.Add((float.Parse(xz[0]), float.Parse(xz[1]))); }
        else pos.AddRange(new (float, float)[] { (0, 2), (0, 4), (0, 8), (0, 12), (0, 18), (6, 0), (-3, 3) });
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        string mode = Environment.GetEnvironmentVariable("SPATIAL_DIAG_MODE") ?? "";
        if (mode.Contains("meters")) { r.SetupMeters(); r.CaptureInitialState(events); }
        int cap = mode.Contains("short") ? Session.Rate * 5 / 2 : Session.Rate * 6, tail = mode.Contains("short") ? Session.Rate / 2 : Session.Rate * 5;
        foreach (var e in events.Where(e => !e.Snapshot && e.Is3D && re.IsMatch(e.Path)).Take(n))
        {
            (double el, double er)? Ren(float x, float z, int reps)
            {
                double el = 0, er = 0;
                for (int k = 0; k < reps; k++)
                {
                    var rr = r.Render(e, null, new RenderOpts { MaxFrames = cap, TailMaxFrames = tail, Position = (x, 0, z) });
                    if (rr.Error != null) return null;
                    var en = Spatial.Energy(rr.Pcm); el += en.el; er += en.er;
                }
                return (el / reps, er / reps);
            }
            Console.WriteLine($"{e.Path} min={e.MinDist} max={e.MaxDist}");
            double Db(double num, double den) => num <= 0 || den <= 0 ? -240 : 10 * Math.Log10(num / den);
            var g0 = Ren(0, 0, 1).Value;
            foreach (var (x, z) in pos)
            {
                var pr = Ren(x, z, 1);
                if (pr == null) { Console.WriteLine($"  pos ({x},{z}) error"); continue; }
                Console.WriteLine($"  pos ({x,5:F2},{z,5:F2})  L {Db(pr.Value.el, g0.el),7:F2} R {Db(pr.Value.er, g0.er),7:F2} mid {Db(pr.Value.el + pr.Value.er, g0.el + g0.er),7:F2} L-R {Db(pr.Value.el, pr.Value.er),7:F2}");
            }
            var m = Spatial.Measure(e, false, Ren);
            if (m == null) { Console.WriteLine("  model: none (silent)"); continue; }
            Console.WriteLine($"  model: rolloff {string.Join(" ", m.Rolloff.Select(p => $"{p.d:G3}:{Analysis.Db(p.gain):F1}"))}");
            Console.WriteLine($"         pan @{m.PanDistance:G3} far {string.Join(" ", m.PanAz.Select(p => $"{p.az}:{p.far:F4}"))}  near {string.Join(" ", m.PanNear.Select(p => $"{p.d:G3}:{p.blend:F3}"))}  front {m.FrontShare:F3}");
        }
        return 0;
    }
}

/// <summary>spatialtime &lt;event&gt; x z: per-block level of a content render at (x,0,z) relative to the distance-0 render.</summary>
static class SpatialTime
{
    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        var e = events.First(x => x.Path == a[0]);
        float x = float.Parse(a[1]), z = float.Parse(a[2]);
        var r0 = r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * 6 });
        var r1 = r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * 6, Position = (x, 0, z) });
        Console.WriteLine($"{e.Path} frames {r0.Frames}/{r1.Frames} lead {r0.TrimmedLeadFrames}/{r1.TrimmedLeadFrames}");
        int B = 512;
        for (int b = 0; b * B < Math.Min(r0.Frames, r1.Frames) && b < 60; b++)
        {
            double l0 = 0, rr0 = 0, l1 = 0, rr1 = 0;
            for (int f = b * B; f < (b + 1) * B && f < r0.Frames && f < r1.Frames; f++)
            { l0 += r0.Pcm[2 * f] * (double)r0.Pcm[2 * f]; rr0 += r0.Pcm[2 * f + 1] * (double)r0.Pcm[2 * f + 1]; l1 += r1.Pcm[2 * f] * (double)r1.Pcm[2 * f]; rr1 += r1.Pcm[2 * f + 1] * (double)r1.Pcm[2 * f + 1]; }
            Console.Write($"{b}: lvl {Analysis.Db(Math.Sqrt((l0 + rr0) / B / 2)):F0} L {10 * Math.Log10(Math.Max(l1, 1e-30) / Math.Max(l0, 1e-30)):F1} R {10 * Math.Log10(Math.Max(rr1, 1e-30) / Math.Max(rr0, 1e-30)):F1} | ");
            if (b % 4 == 3) Console.WriteLine();
        }
        Console.WriteLine();
        return 0;
    }
}

/// <summary>metertest &lt;regex&gt; [n]: does locking the bus channel groups (and metering them, as the bake's routing meters do)
/// change an event's output? Renders at distance 0 and at (6,0,0): unlocked, locked, locked+metered, unlocked again.</summary>
static class MeterTest
{
    static FMOD.ATTRIBUTES_3D FmodAttr(float x, float y, float z) => new() { position = Renderer.V(x, y, z), velocity = Renderer.V(0, 0, 0), forward = Renderer.V(0, 0, 1), up = Renderer.V(0, 1, 0) };
    static void Tree(FMOD.ChannelGroup g, int depth)
    {
        g.getName(out string name, 64); g.getNumChannels(out int nc); g.getNumGroups(out int ng); g.getAudibility(out float au);
        g.getNumDSPs(out int nd); var ds = new List<string>();
        for (int i = 0; i < nd; i++) { g.getDSP(i, out var d); d.getType(out var t); ds.Add(t.ToString()); }
        g.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, out var head); head.getNumInputs(out int nin);
        if (au > 1e-4 || depth < 2) Console.WriteLine($"{new string(' ', depth * 2)}'{name}' ch={nc} groups={ng} aud={au:G3} inputs(head)={nin} [{string.Join(">", ds)}]");
        for (int i = 0; i < ng; i++) { g.getGroup(i, out var c); Tree(c, depth + 1); }
    }
    public static int Run(string[] a)
    {
        var re = new System.Text.RegularExpressions.Regex(a.Length > 0 ? a[0] : ".", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int n = a.Length > 1 ? int.Parse(a[1]) : 5;
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        var sel = events.Where(e => !e.Snapshot && re.IsMatch(e.Path)).Take(n).ToList();
        if (Environment.GetEnvironmentVariable("METERTEST_STATS") == "1") sel = sel.Where((e, i) => i % Math.Max(1, int.Parse(Environment.GetEnvironmentVariable("METERTEST_STRIDE") ?? "1")) == 0).ToList();
        string Row(EventInfo e)
        {
            var x0 = r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * 6 });
            var en0 = Spatial.Energy(x0.Pcm);
            string t = $"rms {Analysis.Db(Analysis.Rms(x0.Pcm)),7:F2}";
            if (e.Is3D)
            {
                var x6 = r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * 6, Position = (6, 0, 0) });
                var en = Spatial.Energy(x6.Pcm);
                t += $" L-R@6m {10 * Math.Log10(Math.Max(en.el, 1e-30) / Math.Max(en.er, 1e-30)),7:F2}";
            }
            return t;
        }
        var rows = Environment.GetEnvironmentVariable("METERTEST_STATS") == "1" ? sel.ToDictionary(e => e.Path, e => new List<string>()) : sel.ToDictionary(e => e.Path, e => new List<string> { Row(e) });
        if (Environment.GetEnvironmentVariable("METERTEST_STATS") == "1") goto stats;
        foreach (var b in buses) b.Bus.lockChannelGroup();
        s.Studio.flushCommands(); s.Step();
        foreach (var e in sel) rows[e.Path].Add(Row(e));
        r.SetupMeters();
        foreach (var e in sel) rows[e.Path].Add(Row(e));
        foreach (var b in buses) { b.Bus.unlockChannelGroup(); b.Bus.unlockChannelGroup(); }
        s.Studio.flushCommands(); for (int i = 0; i < 8; i++) s.Step();
        foreach (var e in sel) rows[e.Path].Add(Row(e));
        stats:
        if (Environment.GetEnvironmentVariable("METERTEST_STATS") == "1")
        {
            // locked vs unlocked over many events: residual of the distance-0 render (deterministic events only)
            foreach (var b in buses) { b.Bus.unlockChannelGroup(); b.Bus.unlockChannelGroup(); }
            s.Studio.flushCommands(); for (int i = 0; i < 8; i++) s.Step();
            var un = sel.ToDictionary(e => e.Path, e => (r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * 4 }), r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * 4 })));
            foreach (var b in buses) b.Bus.lockChannelGroup();
            s.Studio.flushCommands(); s.Step();
            int det = 0, changed = 0; var dl = new List<double>();
            foreach (var e in sel)
            {
                var (u1, u2) = un[e.Path];
                if (Analysis.DiffDb(u1.Pcm, u2.Pcm) > -60 || Analysis.Peak(u1.Pcm) < 3e-5f) continue;
                det++;
                var l1 = r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * 4 });
                double d = Analysis.DiffDb(u1.Pcm, l1.Pcm);
                double lv = Analysis.Db(Analysis.Rms(l1.Pcm)) - Analysis.Db(Analysis.Rms(u1.Pcm));
                if (d > -40) { changed++; dl.Add(lv); Console.WriteLine($"  CHANGED {e.Path}: residual {d:F1} dB, level locked-unlocked {lv:+0.00;-0.00} dB"); }
            }
            dl.Sort();
            Console.WriteLine($"deterministic {det}, changed by locking (residual > -40 dB) {changed}; level change median {(dl.Count > 0 ? dl[dl.Count / 2] : 0):F2} dB, min {(dl.Count > 0 ? dl[0] : 0):F2}, max {(dl.Count > 0 ? dl[^1] : 0):F2}");
            return 0;
        }
        Console.WriteLine("columns: unlocked | locked | locked+metered | unlocked again");
        if (Environment.GetEnvironmentVariable("METERTEST_TREE") == "1")
            foreach (bool lockit in new[] { false, true })
            {
                if (lockit) { foreach (var b in buses) b.Bus.lockChannelGroup(); s.Studio.flushCommands(); s.Step(); }
                var e = sel[0];
                r.EnsureSamples(e); s.Drain();
                e.Desc.createInstance(out var inst);
                inst.set3DAttributes(FmodAttr(6, 0, 0)); inst.start();
                for (int i = 0; i < 10; i++) s.Step();
                s.Core.getMasterChannelGroup(out var mcg);
                Console.WriteLine($"--- tree, {(lockit ? "locked" : "unlocked")}, {e.Path} at (6,0,0), block 10");
                Tree(mcg, 0);
                inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); inst.release(); s.Drain();
                if (lockit) { foreach (var b in buses) b.Bus.unlockChannelGroup(); s.Studio.flushCommands(); s.Step(); }
            }
        foreach (var e in sel) Console.WriteLine($"{e.Path}: {string.Join(" | ", rows[e.Path])}");
        return 0;
    }
}

/// <summary>lockbisect &lt;event&gt;: which single bus, when its channel group is locked (kept alive), changes the event's render?
/// Prints the peak and residual against the unlocked render for every bus whose lock changes it.</summary>
static class LockBisect
{
    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        var e = events.First(x => x.Path == a[0]);
        int cap = Session.Rate * (a.Length > 1 ? int.Parse(a[1]) : 8);
        var u = r.Render(e, null, new RenderOpts { MaxFrames = cap });
        Console.WriteLine($"{e.Path}: unlocked peak {Analysis.Db(Analysis.Peak(u.Pcm)):F1} dB, frames {u.Frames}");
        foreach (var b in buses) b.Bus.lockChannelGroup();
        s.Studio.flushCommands(); s.Step();
        var l = r.Render(e, null, new RenderOpts { MaxFrames = cap });
        foreach (var b in buses) b.Bus.unlockChannelGroup();
        s.Studio.flushCommands(); s.Drain();
        Console.WriteLine($"  all locked: peak {Analysis.Db(Analysis.Peak(l.Pcm)):F1} dB, residual vs unlocked {Analysis.DiffDb(l.Pcm, u.Pcm):F1} dB");
        foreach (var b in buses)
        {
            b.Bus.lockChannelGroup(); s.Studio.flushCommands(); s.Step();
            var x = r.Render(e, null, new RenderOpts { MaxFrames = cap });
            b.Bus.unlockChannelGroup(); s.Studio.flushCommands(); s.Drain();
            double d = Analysis.DiffDb(x.Pcm, u.Pcm);
            if (d > -40 || Math.Abs(Analysis.Peak(x.Pcm) - Analysis.Peak(u.Pcm)) > 1e-4)
                Console.WriteLine($"  lock {b.Path}: peak {Analysis.Db(Analysis.Peak(x.Pcm)):F1} dB, residual vs unlocked {d:F1} dB");
        }
        return 0;
    }
}
