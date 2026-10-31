#if !SHEPHERD_EBOOT
#define SHEPHERD_LOADPROF   // (kit #10: the loader profile's zone calls -- Assets/LoadProf.cs -- are compiled into Mac / engine-tools builds only)
#endif
using System.Collections;
using Shepherd.Assets;
using Shepherd.Engine.Assets;
using Shepherd.Engine.Native;
using UnityEngine;
using UPPtr = Shepherd.Assets.PPtr;
namespace Shepherd.Engine.Anim;

public static class Crc
{
    static readonly uint[] T = Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
    public static uint Of(string s)
    {
        if (!Fast) return Compute(s);
        s ??= "";
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < s.Length; i++) { char ch = s[i]; if (ch >= 0x80) return Compute(s); c = T[(c ^ ch) & 0xFF] ^ (c >> 8); }   // (an ASCII char = its one UTF-8 byte)
        return ~c;
    }
    static uint Compute(string s) { uint c = 0xFFFFFFFF; foreach (var b in System.Text.Encoding.UTF8.GetBytes(s ?? "")) c = T[(c ^ b) & 0xFF] ^ (c >> 8); return ~c; }
    /// <summary>Kit #10 (Animator binding, SHEPHERD_K10_ANIMBIND): the binding asks the CRC of every path, every candidate property name, every
    /// constant like "m_IsActive", per Animator -- each a UTF-8 copy. The CRC is taken over an ASCII name's chars directly (its UTF-8 bytes are
    /// its chars: the same CRC, no copy); any other name takes the UTF-8 copy as before. Kit #10 review: this was a memo of up to 65,536 names,
    /// which kept the name strings alive -- +3.5..+5 MiB of live heap / arena peak on the menu tour (route A); nothing is kept now.</summary>
    static readonly bool Fast = Shepherd.Engine.Runtime.K10LoadTrack.AnimBind;
}

static class TT
{
    public static object V(TypeTreeObject t, string k) => t != null && t.TryGet(k, out var v) ? v : null;
    public static TypeTreeObject O(object o) => o is TypeTreeObject t ? (t.TryGet("data", out var d) && d is TypeTreeObject dt && t.Keys.Count == 1 ? dt : t) : null;   // unwrap OffsetPtr
    public static TypeTreeObject Ob(TypeTreeObject t, string k) => O(V(t, k));
    public static List<object> L(TypeTreeObject t, string k) => (V(t, k) as IList)?.Cast<object>().ToList() ?? new List<object>();
    public static int I(TypeTreeObject t, string k, int d = 0) => V(t, k) is { } v ? unchecked((int)Convert.ToInt64(v)) : d;
    public static uint U(TypeTreeObject t, string k) => V(t, k) is { } v ? unchecked((uint)Convert.ToInt64(v)) : 0u;
    public static float F(TypeTreeObject t, string k, float d = 0) => V(t, k) is { } v ? Convert.ToSingle(v) : d;
    public static bool B(TypeTreeObject t, string k) => V(t, k) is { } v && Convert.ToInt64(v) != 0;
}

/// <summary>A compiled AnimationClip: curves (streamed / dense / constant) + generic bindings + events.</summary>
public sealed class ClipData
{
    public string Name; public float Start, Stop, SampleRate = 60; public bool Loop;
    public struct Key { public float T, A, B, C, D; }
    public Key[][] Streamed = Array.Empty<Key[]>(); public int DenseCount, DenseFrames; public float DenseRate, DenseBegin; public float[] Dense = Array.Empty<float>();
    public float[] Constants = Array.Empty<float>();
    public sealed class Binding { public uint Path, Attribute; public int TypeId; public Type Script; public bool IsPPtr; public int Curve, Count; }
    public readonly List<Binding> Bindings = new();
    public UnityEngine.Object[] PPtrMap = Array.Empty<UnityEngine.Object>();
    public readonly List<(float time, string fn, string s, float f, int i, UnityEngine.Object o)> Events = new();
    public float Length => Math.Max(1e-4f, Stop - Start);
    public int CurveCount => Streamed.Length + DenseCount + Constants.Length;
    public UnityEngine.Object Managed;

    static readonly Dictionary<NObj, ClipData> Cache = new();
    public static ClipData Of(UnityEngine.Object clip)
    {
        var n = Objects.Get(clip); if (n == null) return null;
        if (Cache.TryGetValue(n, out var c)) return c;
        if (Assets.LoadProf.On) { Assets.LoadProf.Enter(Assets.LoadProf.AnimClip); try { c = Decode(n); } finally { Assets.LoadProf.Exit(Assets.LoadProf.AnimClip); } }
        else c = Decode(n);
        c.Managed = clip; return Cache[n] = c;
    }

    static ClipData Decode(NObj n)
    {
        var d = n.SourceTree(); var f = n.SourceFile as SerializedFile; var c = new ClipData { Name = n.Name };
        if (d == null) return c;
        c.SampleRate = TT.F(d, "m_SampleRate", 60);
        var mc = TT.Ob(d, "m_MuscleClip");
        c.Start = TT.F(mc, "m_StartTime"); c.Stop = TT.F(mc, "m_StopTime"); c.Loop = TT.B(mc, "m_LoopTime");
        var clip = TT.Ob(mc, "m_Clip");
        var sc = TT.Ob(clip, "m_StreamedClip");
        if (sc != null)
        {
            int count = TT.I(sc, "curveCount"); uint[] data;
            if (Shepherd.Engine.Runtime.K10LoadTrack.AnimBind && TT.V(sc, "data") is List<object> rl)
            {   // kit #10: the same values without the list copy; a uint box read as it is (Convert.ToInt64 of a uint, then unchecked to uint: itself)
                data = new uint[rl.Count];
                for (int i = 0; i < data.Length; i++) { var x = rl[i]; data[i] = x is uint u ? u : unchecked((uint)Convert.ToInt64(x)); }
            }
            else { var raw = TT.L(sc, "data"); data = new uint[raw.Count]; for (int i = 0; i < data.Length; i++) data[i] = unchecked((uint)Convert.ToInt64(raw[i])); }
            var keys = Enumerable.Range(0, count).Select(_ => new List<Key>()).ToArray();
            int p = 0;
            while (p + 1 < data.Length)
            {
                float time = BitConverter.Int32BitsToSingle((int)data[p++]); int nk = (int)data[p++];
                for (int k = 0; k < nk && p + 4 < data.Length; k++)
                {
                    int idx = (int)data[p++]; var key = new Key { T = time, A = Fl(data[p]), B = Fl(data[p + 1]), C = Fl(data[p + 2]), D = Fl(data[p + 3]) }; p += 4;
                    if (idx >= 0 && idx < count) keys[idx].Add(key);
                }
            }
            c.Streamed = keys.Select(l => l.ToArray()).ToArray();
        }
        var dc = TT.Ob(clip, "m_DenseClip");
        if (dc != null)
        {
            c.DenseCount = TT.I(dc, "m_CurveCount"); c.DenseFrames = TT.I(dc, "m_FrameCount"); c.DenseRate = TT.F(dc, "m_SampleRate", 30); c.DenseBegin = TT.F(dc, "m_BeginTime");
            c.Dense = Shepherd.Engine.Runtime.K10LoadTrack.AnimBind && TT.V(dc, "m_SampleArray") is List<object> dl ? Floats(dl) : TT.L(dc, "m_SampleArray").Cast<object>().Select(Convert.ToSingle).ToArray();
        }
        var cc = TT.Ob(clip, "m_ConstantClip");
        if (cc != null) c.Constants = Shepherd.Engine.Runtime.K10LoadTrack.AnimBind && TT.V(cc, "data") is List<object> cl ? Floats(cl) : TT.L(cc, "data").Cast<object>().Select(Convert.ToSingle).ToArray();
        // bindings -> curve ranges (bindings are laid out in curve order)
        var bc = TT.Ob(d, "m_ClipBindingConstant"); int curve = 0;
        foreach (var bo in TT.L(bc, "genericBindings").OfType<TypeTreeObject>())
        {
            var b = new Binding { Path = TT.U(bo, "path"), Attribute = TT.U(bo, "attribute"), TypeId = TT.I(bo, "typeID"), IsPPtr = TT.I(bo, "isPPtrCurve") != 0, Curve = curve };
            b.Count = (b.TypeId == 4 || b.TypeId == 224) && b.Attribute >= 1 && b.Attribute <= 4 ? (b.Attribute == 2 ? 4 : 3) : 1;
            if (TT.V(bo, "script") is UPPtr sp && !sp.IsNull && f != null)
            {
                try { var (sf, so, sd) = Loader.Db.ResolveRead(f, sp); if (sd != null) b.Script = Loader.ScriptType(TT.V(sd, "m_AssemblyName") as string, TT.V(sd, "m_Namespace") as string, TT.V(sd, "m_ClassName") as string); } catch { }
            }
            c.Bindings.Add(b); curve += b.Count;
        }
        if (f != null) c.PPtrMap = TT.L(bc, "pptrCurveMapping").OfType<UPPtr>().Select(p => Loader.Managed(f, p)).ToArray();
        foreach (var e in TT.L(d, "m_Events").OfType<TypeTreeObject>())
            c.Events.Add((TT.F(e, "time"), TT.V(e, "functionName") as string, TT.V(e, "data") as string, TT.F(e, "floatParameter"), TT.I(e, "intParameter"),
                TT.V(e, "objectReferenceParameter") is UPPtr op && f != null ? Loader.Managed(f, op) : null));
        return c;
    }
    static float Fl(uint u) => BitConverter.Int32BitsToSingle((int)u);
    /// <summary>Kit #10: Convert.ToSingle of each value (a float box: the float itself), without the LINQ chain and the list copy.</summary>
    static float[] Floats(List<object> l) { var r = new float[l.Count]; for (int i = 0; i < r.Length; i++) { var x = l[i]; r[i] = x is float f ? f : Convert.ToSingle(x); } return r; }

    /// <summary>Value of curve <paramref name="index"/> at clip-local time t (seconds from Start).</summary>
    public float Sample(int index, float t)
    {
        float time = Start + t;
        if (index < Streamed.Length)
        {
            var keys = Streamed[index]; if (keys.Length == 0) return 0;
            int lo = 0, hi = keys.Length - 1, best = 0;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (keys[mid].T <= time) { best = mid; lo = mid + 1; } else hi = mid - 1; }
            var k = keys[best]; float dt = k.T < -1e30f ? 0 : Math.Max(0, time - k.T);
            if (keys[best].T > time) dt = 0;
            return ((k.A * dt + k.B) * dt + k.C) * dt + k.D;
        }
        index -= Streamed.Length;
        if (index < DenseCount)
        {
            if (DenseFrames <= 0) return 0;
            float fr = (time - DenseBegin) * DenseRate; int f0 = Math.Clamp((int)MathF.Floor(fr), 0, DenseFrames - 1), f1 = Math.Min(f0 + 1, DenseFrames - 1); float w = Math.Clamp(fr - f0, 0, 1);
            float a = Dense.ElementAtOrDefault(f0 * DenseCount + index), b = Dense.ElementAtOrDefault(f1 * DenseCount + index); return a + (b - a) * w;
        }
        index -= DenseCount;
        return index < Constants.Length ? Constants[index] : 0;
    }
}

/// <summary>A compiled AnimatorController (or override): layers -> state machines -> states/transitions; parameters.</summary>
public sealed class ControllerData
{
    public sealed class Cond { public int Mode; public uint Param; public float Threshold; }
    public sealed class Trans { public int Dest; public bool HasExit; public float ExitTime, Offset, Duration; public bool CanToSelf; public Cond[] Conds; }
    public sealed class State { public uint NameId, PathId, FullPathId, TagId; public float Speed = 1; public bool Loop, WriteDefaults = true; public int ClipIndex = -1; public Trans[] Transitions; }
    public sealed class Machine { public State[] States; public Trans[] AnyState; public int Default; }
    public sealed class Layer { public int Machine; public uint Name; public float Weight; }
    public sealed class Param { public uint Id; public int Type; public object Default; public string Name; }
    public Layer[] Layers = Array.Empty<Layer>(); public Machine[] Machines = Array.Empty<Machine>(); public Param[] Params = Array.Empty<Param>();
    public UnityEngine.Object[] Clips = Array.Empty<UnityEngine.Object>(); public readonly Dictionary<uint, string> Tos = new();

    static readonly Dictionary<NObj, ControllerData> Cache = new();
    public static ControllerData Of(UnityEngine.Object ctrl)
    {
        var n = Objects.Get(ctrl); if (n == null) return null;
        if (Cache.TryGetValue(n, out var c)) return c;
        Assets.LoadProf.Enter(Assets.LoadProf.AnimCtrl);
        try { var d = n.SourceTree(); c = d != null && d.TryGet("m_Clips", out _) && d.TryGet("m_Controller", out var baseRef) ? Override(n, d) : Decode(n, d); }
        catch (Exception e) { Log.Exception(e, $"decode controller '{n.Name}'"); c = new ControllerData(); }
        Assets.LoadProf.Exit(Assets.LoadProf.AnimCtrl);   // (after a catch of every exception: always reached)
        return Cache[n] = c;
    }
    static ControllerData Override(NObj n, TypeTreeObject d)
    {
        var f = (SerializedFile)n.SourceFile; var baseCtrl = TT.V(d, "m_Controller") is UPPtr bp ? Loader.Managed(f, bp) : null;
        var b = baseCtrl != null ? Of(baseCtrl) : new ControllerData();
        var o = new ControllerData { Layers = b.Layers, Machines = b.Machines, Params = b.Params, Clips = (UnityEngine.Object[])b.Clips.Clone() };
        foreach (var kv in b.Tos) o.Tos[kv.Key] = kv.Value;
        foreach (var e in TT.L(d, "m_Clips").OfType<TypeTreeObject>())
        {
            var orig = TT.V(e, "m_OriginalClip") is UPPtr op ? Loader.Managed(f, op) : null; var ovr = TT.V(e, "m_OverrideClip") is UPPtr vp ? Loader.Managed(f, vp) : null;
            if (orig == null || ovr == null) continue;
            for (int i = 0; i < o.Clips.Length; i++) if (ReferenceEquals(o.Clips[i], orig)) o.Clips[i] = ovr;
        }
        return o;
    }
    static ControllerData Decode(NObj n, TypeTreeObject d)
    {
        var f = n.SourceFile as SerializedFile; var c = new ControllerData();
        if (d == null) return c;
        foreach (var e in TT.L(d, "m_TOS").OfType<TypeTreeObject>()) c.Tos[TT.U(e, "first")] = TT.V(e, "second") as string;
        c.Clips = TT.L(d, "m_AnimationClips").OfType<UPPtr>().Select(p => f == null ? null : Loader.Managed(f, p)).ToArray();
        var cc = TT.Ob(d, "m_Controller");
        c.Layers = TT.L(cc, "m_LayerArray").Select(TT.O).Where(x => x != null).Select(l => new Layer { Machine = TT.I(l, "m_StateMachineIndex"), Name = TT.U(l, "m_Binding"), Weight = TT.F(l, "m_DefaultWeight") }).ToArray();
        c.Machines = TT.L(cc, "m_StateMachineArray").Select(TT.O).Where(x => x != null).Select(sm => new Machine
        {
            Default = TT.I(sm, "m_DefaultState"),
            AnyState = TT.L(sm, "m_AnyStateTransitionConstantArray").Select(TT.O).Where(x => x != null).Select(Tr).ToArray(),
            States = TT.L(sm, "m_StateConstantArray").Select(TT.O).Where(x => x != null).Select(st =>
            {
                var s = new State
                {
                    NameId = TT.U(st, "m_NameID"), PathId = TT.U(st, "m_PathID"), FullPathId = TT.U(st, "m_FullPathID"), TagId = TT.U(st, "m_TagID"), Speed = TT.F(st, "m_Speed", 1),
                    Loop = TT.B(st, "m_Loop"), WriteDefaults = TT.V(st, "m_WriteDefaultValues") is null || TT.B(st, "m_WriteDefaultValues"),
                    Transitions = TT.L(st, "m_TransitionConstantArray").Select(TT.O).Where(x => x != null).Select(Tr).ToArray()
                };
                var bt = TT.L(st, "m_BlendTreeConstantArray").Select(TT.O).FirstOrDefault();
                var leaf = TT.L(bt, "m_NodeArray").Select(TT.O).Where(x => x != null).FirstOrDefault(nd => TT.U(nd, "m_ClipID") != uint.MaxValue);
                if (leaf != null) s.ClipIndex = TT.I(leaf, "m_ClipID");
                return s;
            }).ToArray()
        }).ToArray();
        var vals = TT.Ob(cc, "m_Values"); var defs = TT.Ob(cc, "m_DefaultValues");
        c.Params = TT.L(vals, "m_ValueArray").OfType<TypeTreeObject>().Select(v =>
        {
            int type = TT.I(v, "m_Type"), idx = TT.I(v, "m_Index"); uint id = TT.U(v, "m_ID");
            object def = type switch
            {
                1 => TT.L(defs, "m_FloatValues").Cast<object>().ElementAtOrDefault(idx) is { } fv ? Convert.ToSingle(fv) : 0f,
                3 => TT.L(defs, "m_IntValues").Cast<object>().ElementAtOrDefault(idx) is { } iv ? Convert.ToInt32(iv) : 0,
                _ => TT.L(defs, "m_BoolValues").Cast<object>().ElementAtOrDefault(idx) is { } bv && Convert.ToInt64(bv) != 0
            };
            return new Param { Id = id, Type = type, Default = def, Name = c.Tos.GetValueOrDefault(id) };
        }).ToArray();
        return c;
    }
    static Trans Tr(TypeTreeObject t) => new()
    {
        Dest = TT.I(t, "m_DestinationState"), HasExit = TT.B(t, "m_HasExitTime"), ExitTime = TT.F(t, "m_ExitTime"), Offset = TT.F(t, "m_TransitionOffset"), Duration = TT.F(t, "m_TransitionDuration"),
        CanToSelf = TT.B(t, "m_CanTransitionToSelf"),
        Conds = TT.L(t, "m_ConditionConstantArray").Select(TT.O).Where(x => x != null).Select(cn => new Cond { Mode = TT.I(cn, "m_ConditionMode"), Param = TT.U(cn, "m_EventID"), Threshold = TT.F(cn, "m_EventThreshold") }).ToArray()
    };
}
