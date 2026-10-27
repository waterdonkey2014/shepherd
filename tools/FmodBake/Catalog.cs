using FMOD.Studio;

namespace Shepherd.FmodBake;

sealed class ParamInfo
{
    public string Name;
    public float Min, Max, Default;
    public PARAMETER_TYPE Type;
    public PARAMETER_FLAGS Flags;
    public PARAMETER_ID Id;
    public List<string> Labels;
    public bool Global => (Flags & PARAMETER_FLAGS.GLOBAL) != 0;
    public bool Automatic => (Flags & PARAMETER_FLAGS.AUTOMATIC) != 0 || Type != PARAMETER_TYPE.GAME_CONTROLLED;
    public bool ReadOnly => (Flags & PARAMETER_FLAGS.READONLY) != 0;
    public bool Discrete => (Flags & (PARAMETER_FLAGS.DISCRETE | PARAMETER_FLAGS.LABELED)) != 0;
}

sealed class EventInfo
{
    public string Path, Guid, Bank;
    public EventDescription Desc;
    public bool Oneshot, Is3D, IsStream, Sustain, Snapshot, Doppler;
    public int LengthMs;
    public float MinDist, MaxDist;
    public List<ParamInfo> Params = new();
    public Dictionary<string, object> UserProps = new();
    /// <summary>Game-controlled parameters a variant can vary: instance parameters, plus global ones (READONLY on the event,
    /// set through Studio::System).</summary>
    public IEnumerable<ParamInfo> Controllable => Params.Where(p => !p.Automatic && (p.Global || !p.ReadOnly));
}

sealed class BusInfo
{
    public string Path, Guid, Bank;
    public Bus Bus;
}

static class Catalog
{
    public static string GuidStr(FMOD.GUID g)
    {
        var b = new byte[16];
        BitConverter.GetBytes(g.Data1).CopyTo(b, 0); BitConverter.GetBytes(g.Data2).CopyTo(b, 4);
        BitConverter.GetBytes(g.Data3).CopyTo(b, 8); BitConverter.GetBytes(g.Data4).CopyTo(b, 12);
        return new Guid(b).ToString("B");
    }

    public static (List<EventInfo> events, List<BusInfo> buses, int vcas) Enumerate(Session s)
    {
        var events = new Dictionary<string, EventInfo>(StringComparer.Ordinal);
        var buses = new Dictionary<string, BusInfo>(StringComparer.Ordinal);
        int vcas = 0;
        foreach (var (file, bank) in s.Banks)
        {
            if (bank.getEventList(out var list) == FMOD.RESULT.OK)
                foreach (var d in list)
                {
                    d.getID(out var id);
                    string g = GuidStr(id);
                    if (events.ContainsKey(g)) continue;
                    events[g] = Describe(d, g, file);
                }
            if (bank.getBusList(out var bl) == FMOD.RESULT.OK)
                foreach (var b in bl)
                {
                    b.getID(out var id); string g = GuidStr(id);
                    if (buses.ContainsKey(g)) continue;
                    b.getPath(out string p);
                    buses[g] = new BusInfo { Path = p ?? g, Guid = g, Bank = file, Bus = b };
                }
            if (bank.getVCACount(out int vc) == FMOD.RESULT.OK) vcas += vc;
        }
        var ev = events.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        var bu = buses.Values.OrderBy(b => b.Path, StringComparer.Ordinal).ToList();
        return (ev, bu, vcas);
    }

    static EventInfo Describe(EventDescription d, string guid, string bank)
    {
        var e = new EventInfo { Desc = d, Guid = guid, Bank = bank };
        d.getPath(out e.Path); e.Path ??= guid;
        d.isOneshot(out e.Oneshot); d.is3D(out e.Is3D); d.isStream(out e.IsStream); d.hasSustainPoint(out e.Sustain);
        d.isSnapshot(out e.Snapshot); d.isDopplerEnabled(out e.Doppler);
        d.getLength(out e.LengthMs);
        d.getMinMaxDistance(out e.MinDist, out e.MaxDist);
        d.getParameterDescriptionCount(out int pc);
        for (int i = 0; i < pc; i++)
        {
            if (d.getParameterDescriptionByIndex(i, out var pd) != FMOD.RESULT.OK) continue;
            var p = new ParamInfo { Name = (string)pd.name, Min = pd.minimum, Max = pd.maximum, Default = pd.defaultvalue, Type = pd.type, Flags = pd.flags, Id = pd.id };
            if ((pd.flags & PARAMETER_FLAGS.LABELED) != 0)
            {
                p.Labels = new List<string>();
                for (int li = 0; li <= (int)(pd.maximum - pd.minimum) && li < 64; li++)
                    if (d.getParameterLabelByIndex(i, li, out string lab) == FMOD.RESULT.OK) p.Labels.Add(lab); else break;
            }
            e.Params.Add(p);
        }
        d.getUserPropertyCount(out int uc);
        for (int i = 0; i < uc; i++)
        {
            if (d.getUserPropertyByIndex(i, out var up) != FMOD.RESULT.OK) continue;
            object v = up.type switch
            {
                USER_PROPERTY_TYPE.INTEGER => up.intValue(),
                USER_PROPERTY_TYPE.BOOLEAN => up.boolValue(),
                USER_PROPERTY_TYPE.FLOAT => up.floatValue(),
                _ => up.stringValue(),
            };
            e.UserProps[(string)up.name] = v;
        }
        return e;
    }
}
