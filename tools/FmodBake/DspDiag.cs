using FMOD;
using FMOD.Studio;

namespace Shepherd.FmodBake;

/// <summary>dspdiag &lt;regex&gt; [n]: prints the channel-group / DSP tree of a playing instance of each matching event
/// (silent NRT session; nothing is written). Used to see where an event's spatializer sits.</summary>
static class DspDiag
{
    public static int Run(string[] a)
    {
        var re = new System.Text.RegularExpressions.Regex(a.Length > 0 ? a[0] : ".", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int n = a.Length > 1 ? int.Parse(a[1]) : 5;
        bool summary = a.Length > 2 && a[2] == "summary";
        using var s = Program.OpenSession(out var events, out var buses, out _);
        var r = new Renderer(s, buses);
        if (summary) return Summary(s, r, events.Where(e => !e.Snapshot && e.Is3D && re.IsMatch(e.Path)).Take(n).ToList());
        foreach (var e in events.Where(e => !e.Snapshot && re.IsMatch(e.Path)).Take(n))
        {
            r.EnsureSamples(e); s.Drain();
            e.Desc.createInstance(out var inst);
            inst.set3DAttributes(new ATTRIBUTES_3D { position = Renderer.V(3, 0, 4), forward = Renderer.V(0, 0, 1), up = Renderer.V(0, 1, 0) });
            inst.start();
            int[] at = { 2, 6, 15, 30 };
            int done = 0;
            foreach (int t in at)
            {
                while (done < t) { s.Step(); done++; }
                var res = inst.getChannelGroup(out var cg);
                inst.getPlaybackState(out var pst);
                Console.WriteLine($"{e.Path} @block {t} state={pst} 3D={e.Is3D} min={e.MinDist} max={e.MaxDist} getChannelGroup={res}");
                if (res == RESULT.OK) Dump(cg, 1);
                if (t == 6) { s.Core.getMasterChannelGroup(out var mcg); Console.WriteLine("  -- core master tree:"); DumpNames(mcg, 2); }
            }
            inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); inst.release(); s.Drain();
        }
        return 0;
    }

    static int Summary(Session s, Renderer r, List<EventInfo> evs)
    {
        var hist = new Dictionary<string, int>();
        void Add(string k) => hist[k] = hist.GetValueOrDefault(k) + 1;
        foreach (var e in evs)
        {
            r.EnsureSamples(e); s.Drain();
            e.Desc.createInstance(out var inst);
            inst.set3DAttributes(new ATTRIBUTES_3D { position = Renderer.V(3, 0, 4), forward = Renderer.V(0, 0, 1), up = Renderer.V(0, 1, 0) });
            inst.start();
            for (int i = 0; i < 3; i++) s.Step();
            if (inst.getChannelGroup(out var cg) != RESULT.OK) { Add("no group"); inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); inst.release(); continue; }
            var master = new List<string>(); int childPan = 0, childSend = 0, masterSend = 0; string roll = "-"; int panCh = -1;
            cg.getNumDSPs(out int nd);
            for (int i = 0; i < nd; i++)
            {
                cg.getDSP(i, out var d); d.getType(out var t); master.Add(t.ToString());
                if (t == DSP_TYPE.PAN) { d.getParameterInt(16, out int ro); roll = ro.ToString(); d.getInfo(out _, out _, out panCh, out _, out _); }
                if (t == DSP_TYPE.SEND) masterSend++;
            }
            void Walk(ChannelGroup g)
            {
                g.getNumGroups(out int ng);
                for (int i = 0; i < ng; i++)
                {
                    g.getGroup(i, out var c); c.getNumDSPs(out int cd);
                    for (int k = 0; k < cd; k++) { c.getDSP(k, out var d); d.getType(out var t); if (t == DSP_TYPE.PAN) childPan++; if (t == DSP_TYPE.SEND) childSend++; if (t != DSP_TYPE.FADER && t != DSP_TYPE.PAN && t != DSP_TYPE.SEND) Add("child dsp " + t); }
                    Walk(c);
                }
            }
            Walk(cg);
            Add("master chain " + string.Join(">", master));
            Add("rolloff " + roll);
            Add("pan input ch " + panCh);
            if (childPan > 0) { Add("child PAN"); Console.WriteLine($"  child PAN: {e.Path}"); }
            if (childSend > 0) { Add("child SEND"); }
            if (masterSend > 0) Add("master SEND");
            inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); inst.release(); s.Drain();
        }
        foreach (var kv in hist.OrderByDescending(kv => kv.Value)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
        return 0;
    }

    static void DumpNames(ChannelGroup g, int depth)
    {
        g.getName(out string name, 64); g.getNumChannels(out int nc); g.getNumGroups(out int ng); g.getAudibility(out float au);
        if (nc > 0 || depth < 3) Console.WriteLine($"{new string(' ', depth * 2)}'{name}' channels={nc} groups={ng} aud={au:G3}");
        for (int i = 0; i < ng; i++) { g.getGroup(i, out var c); DumpNames(c, depth + 1); }
    }

    static void Dump(ChannelGroup g, int depth)
    {
        string ind = new string(' ', depth * 2);
        g.getName(out string name, 64);
        g.getNumDSPs(out int nd); g.getNumGroups(out int ng); g.getNumChannels(out int nc);
        g.getVolume(out float vol);
        Console.WriteLine($"{ind}group '{name}' dsps={nd} groups={ng} channels={nc} vol={vol}");
        for (int i = 0; i < nd; i++)
        {
            g.getDSP(i, out var d);
            d.getType(out var t);
            d.getInfo(out string dn, out _, out int ch, out _, out _);
            d.getNumParameters(out int np);
            d.getBypass(out bool by); d.getActive(out bool act);
            var ps = new List<string>();
            if (t == DSP_TYPE.PAN || t == DSP_TYPE.OBJECTPAN || t == DSP_TYPE.FADER || t == DSP_TYPE.CHANNELMIX || t == DSP_TYPE.UNKNOWN)
                for (int p = 0; p < np && p < 40; p++)
                {
                    d.getParameterInfo(p, out DSP_PARAMETER_DESC pd);
                    string pn = System.Text.Encoding.ASCII.GetString(pd.name).TrimEnd('\0');
                    string v = pd.type switch
                    {
                        DSP_PARAMETER_TYPE.FLOAT => d.getParameterFloat(p, out float f) == RESULT.OK ? f.ToString("G4") : "?",
                        DSP_PARAMETER_TYPE.INT => d.getParameterInt(p, out int iv) == RESULT.OK ? iv.ToString() : "?",
                        DSP_PARAMETER_TYPE.BOOL => d.getParameterBool(p, out bool bv) == RESULT.OK ? bv.ToString() : "?",
                        _ => "data",
                    };
                    ps.Add($"{pn}={v}");
                }
            Console.WriteLine($"{ind}  dsp[{i}] {t} '{dn}' ch={ch} bypass={by} active={act} {string.Join(" ", ps)}");
        }
        for (int i = 0; i < ng; i++) { g.getGroup(i, out var c); Dump(c, depth + 1); }
    }
}
