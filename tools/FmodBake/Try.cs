using System.Diagnostics;
using System.Text.RegularExpressions;
namespace Shepherd.FmodBake;
static class Try
{
    public static int Run(string[] a)
    {
        var re = new Regex(a.Length > 0 ? a[0] : ".", RegexOptions.IgnoreCase);
        int n = a.Length > 1 ? int.Parse(a[1]) : 10;
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        int reps = a.Length > 2 ? int.Parse(a[2]) : 1;
        var sel = events.Where(e => !e.Snapshot && re.IsMatch(e.Path)).Take(n).SelectMany(e => Enumerable.Repeat(e, reps)).ToList();
        var sw = Stopwatch.StartNew();
        foreach (var e in sel)
        {
            var o = new RenderOpts { Meter = true, MaxFrames = e.Oneshot ? Session.Rate * 30 : Session.Rate * 20 };
            long u0 = s.Updates; var t0 = sw.ElapsedMilliseconds;
            var x = r.Render(e, null, o);
            var y = r.Render(e, null, new RenderOpts { MaxFrames = o.MaxFrames });
            if (x.Error != null) { Console.WriteLine($"{e.Path}: ERROR {x.Error}"); continue; }
            var top = x.BusEnergy.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key.Replace("bus:/SFXBus/paused_by_pause_menu/affected_by_loading_screen","…SFX/pbpm/abls")}={Analysis.Db(Math.Sqrt(kv.Value)):F0}");
            Console.WriteLine($"{e.Path} os={e.Oneshot} 3D={e.Is3D} len={e.LengthMs} | frames={x.Frames} stop={x.StoppedByItself}@{x.StopFrame} lead={Analysis.FirstAbove(x.Pcm, 1e-6f)} last={Analysis.LastAbove(x.Pcm, 3e-5f)} pk={Analysis.Db(Analysis.Peak(x.Pcm)):F1}dB mono={Analysis.IsMono(x.Pcm)} missed={x.Missed} take2diff={Analysis.DiffDb(x.Pcm, y.Pcm):F1}dB (f2={y.Frames}) {sw.ElapsedMilliseconds - t0}ms/{s.Updates - u0}upd | {string.Join(" ", top)}");
        }
        return 0;
    }
}
static class SnapEv
{
    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        var sn = events.First(e => e.Path == a[0]);
        var re = new System.Text.RegularExpressions.Regex(a[1]);
        foreach (var e in events.Where(e => !e.Snapshot && re.IsMatch(e.Path)).Take(4))
        {
            var o = new RenderOpts { MaxFrames = Session.Rate * 8 };
            var x0 = r.Render(e, null, o);
            sn.Desc.createInstance(out var si); si.start();
            for (int i = 0; i < 200; i++) s.Step();
            var x1 = r.Render(e, null, o);
            si.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); si.release();
            for (int i = 0; i < 200; i++) s.Step();
            Console.WriteLine($"{e.Path}: rms {Analysis.Db(Analysis.Rms(x0.Pcm)):F1} -> {Analysis.Db(Analysis.Rms(x1.Pcm)):F1} dB");
        }
        return 0;
    }
}
static class LoopDiag
{
    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        var e = events.First(x => x.Path == a[0]);
        int secs = a.Length > 1 ? int.Parse(a[1]) : 120;
        var x = r.Render(e, null, new RenderOpts { MaxFrames = Session.Rate * secs });
        int P = (int)((long)e.LengthMs * Session.Rate / 1000);
        Console.WriteLine($"{e.Path}: frames {x.Frames}, timeline P={P}, first signal {Analysis.FirstAbove(x.Pcm, 3e-5f)}");
        var f = Analysis.FindExactLoop(x.Pcm, P, Session.Rate / 4);
        Console.WriteLine(f == null ? "no exact loop" : $"exact: start {f.Start} end {f.End} period {f.Period} resid {f.ResidualDb:F1}");
        int per = f?.Period ?? P;
        for (int t = 0; t + per + 4096 < x.Frames; t += Session.Rate / 2)
            Console.Write($"{t / (double)Session.Rate:F1}s:{Analysis.SegDiffDb(x.Pcm, t, t + per, 4096):F0} ");
        Console.WriteLine();
        return 0;
    }
}
static class LeakHunt
{
    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses); r.CaptureInitialState(events);
        var probe = events.First(e => e.Path == (a.Length > 0 ? a[0] : "event:/material/footstep_grass"));
        bool Ok() { var x = r.Render(probe, null, new RenderOpts { MaxFrames = Session.Rate * 5 }); return x.StoppedByItself && Analysis.Peak(x.Pcm) > 1e-3; }
        Console.WriteLine($"probe initially ok={Ok()}");
        foreach (var e in events.Where(e => !e.Snapshot && string.CompareOrdinal(e.Path, probe.Path) < 0))
        {
            var x = r.Render(e, null, new RenderOpts { MaxFrames = e.Oneshot ? Session.Rate * 30 : Session.Rate * 20 });
            s.Core.getChannelsPlaying(out int chs, out int real);
            if (!Ok()) { Console.WriteLine($"PROBE FAILS after {e.Path} (stopped={x.StoppedByItself}, channels playing {chs}/{real})"); return 0; }
        }
        Console.WriteLine("probe never failed");
        return 0;
    }
}
static class StateDiff
{
    static Dictionary<string, string> Dump(Session s, List<BusInfo> buses, List<EventInfo> events)
    {
        var d = new Dictionary<string, string>();
        if (s.Studio.getParameterDescriptionList(out var gl) == FMOD.RESULT.OK)
            foreach (var g in gl) { s.Studio.getParameterByID(g.id, out float v, out float fv); d["global " + (string)g.name] = $"{v}/{fv}"; }
        foreach (var b in buses)
        {
            b.Bus.getVolume(out float v, out float fv); b.Bus.getPaused(out bool p); b.Bus.getMute(out bool m);
            string cgs = "";
            if (b.Bus.getChannelGroup(out var cg) == FMOD.RESULT.OK) { cg.getPaused(out bool cp); cg.getPitch(out float pi); cg.getVolume(out float cv); cg.getMute(out bool cm); cg.getAudibility(out float au); cgs = $" cg paused={cp} pitch={pi} vol={cv} mute={cm}"; }
            d["bus " + b.Path] = $"vol={v}/{fv} paused={p} mute={m}{cgs}";
        }
        s.Core.getMasterChannelGroup(out var mcg); mcg.getPitch(out float mp); mcg.getPaused(out bool mpz); d["master cg"] = $"pitch={mp} paused={mpz}";
        int inst = 0; foreach (var e in events) { e.Desc.getInstanceCount(out int n); inst += n; } d["instances"] = inst.ToString();
        s.Core.getChannelsPlaying(out int ch, out int real); d["channels"] = $"{ch}/{real}";
        return d;
    }
    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses); r.CaptureInitialState(events);
        var culprit = events.First(e => e.Path == a[0]);
        var d0 = Dump(s, buses, events);
        var x = r.Render(culprit, null, new RenderOpts { MaxFrames = Session.Rate * 30 });
        Console.WriteLine($"{culprit.Path}: stopped={x.StoppedByItself} frames={x.Frames} leaks={string.Join(";", r.Leaks.GetValueOrDefault(culprit.Path) ?? new())}");
        var d1 = Dump(s, buses, events);
        foreach (var k in d0.Keys) if (d0[k] != d1.GetValueOrDefault(k)) Console.WriteLine($"  {k}: {d0[k]} -> {d1.GetValueOrDefault(k)}");
        Console.WriteLine($"params of culprit: {string.Join(", ", culprit.Params.Select(p => p.Name + ":" + p.Flags))}");
        var fb = buses.First(b => b.Path.EndsWith("/Player/footstep"));
        foreach (int secs in new[] { 5, 30, 120 })
        {
            for (int i = 0; i < secs * Session.Rate / Session.Block; i++) s.Step();
            fb.Bus.getVolume(out float v, out float fv);
            int snapInst = 0; foreach (var e in events.Where(e => e.Snapshot)) { e.Desc.getInstanceCount(out int n); snapInst += n; }
            Console.WriteLine($"  +{secs}s footstep final volume {fv}, snapshot instances {snapInst}");
        }
        return 0;
    }
}
static class StartDiag
{
    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        foreach (var path in a)
        {
            var e = events.First(x => x.Path == path);
            r.EnsureSamples(e); s.Drain();
            e.Desc.createInstance(out var inst); inst.start();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 40; i++)
            {
                float pk = s.Step();
                inst.getPlaybackState(out var st); inst.getTimelinePosition(out int tl);
                sb.Append($"{i}:{st.ToString()[..4]}/{tl}/{(pk > 1e-5 ? "S" : "-")} ");
            }
            inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); inst.release(); s.Drain();
            Console.WriteLine($"{path} stream={e.IsStream}: {sb}");
        }
        return 0;
    }
}
