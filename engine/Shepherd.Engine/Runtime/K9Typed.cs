using Shepherd.Engine.Impl;
using Shepherd.Hooks;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #9, track runtime (vita/notes/kit9-runtime.md): lever 10 typed icall stubs, E5 zero-hop mirrors and D6 -- the engine side.
/// The owner's Unity modules are rewritten by tools/Rewriter --typed tools/Rewriter/k9-typed.txt (owner-local, like the rest of
/// runtime/ManagedVita): each listed stub first asks Shepherd.Hooks (Mirror / TypedIcalls) and falls back to kit #8's object[] forwarder.
/// Here, after IcallRegistry bound every hook:
/// <list type="bullet">
/// <item>typed: each Icalls class's twins (K9(K9Reg): the same expression as its object[] hook, typed) go into TypedIcalls.Table -- each
/// only while the object[] hook it copies is the one bound for its key (a profile / physics hook bound over it keeps the forwarder);</item>
/// <item>mirrors: Runtime/Time.cs keeps the Time state in Shepherd.Hooks.Mirror (one copy); Mirror.On is set once every mirrored key is
/// bound to the very hook method its formula copies (AppIcalls.MirrorHooks; the constants / no-ops: the K9MirrorConsts lists of RenderIcalls,
/// InputState, AppIcalls -- by method identity, review fix: no longer by calling the hook once with null arguments, which a rewritten hook
/// can still pass). A hook whose BODY changes in place keeps its identity: SHEPHERD_K9_MIRROR=check compares every mirrored hook's answer
/// (Time formulas and constants) with the mirror's on the routes, and each mirrored hook carries a "kit #9 E5 MIRROR" comment;</item>
/// <item>D6: FindObjectFromInstanceID is one of the typed twins (Objects.ById, no second table).</item>
/// </list>
/// Switches (env file): SHEPHERD_K9_TYPED = unset / 1 (on) | 0 (no twin bound: every stub runs kit #8's forwarder) | check (the getters'
/// twins run AND the hook, compared, the hook's answer returned -- a Unity object by reference identity, review fix; setters run their twin,
/// which shares the hook's one body: TransformIcalls SetLSv / SetLPv / SetPv); SHEPHERD_K9_MIRROR = unset / 1 | 0 (the stubs ask the engine,
/// kit #8) | check (the stubs ask the engine and every mirrored hook's answer -- the Time getters and, review fix, the constants / no-ops -- is
/// compared with the mirror's). With rewritten modules missing from the card (no --typed), nothing reads any of this: kit #8's stubs.</summary>
[ProfileOnly]
public static class K9Typed
{
    public static readonly string TypedMode = (Environment.GetEnvironmentVariable("SHEPHERD_K9_TYPED") ?? "1").Trim().ToLowerInvariant();
    public static readonly string MirrorMode = (Environment.GetEnvironmentVariable("SHEPHERD_K9_MIRROR") ?? "1").Trim().ToLowerInvariant();
    static bool Off(string m) => m is "0" or "off";
    static int _bound, _listed, _skipped, _mirrors; static string _skipNote = "", _mirrorNote = "";
    static long _checks, _bad; static readonly List<string> _badLines = new();

    /// <summary>IcallRegistry.Init's last step: bind the twins and decide the mirrors.</summary>
    public static void Bind()
    {
        var names = IcallRegistry.Names; var ids = new Dictionary<string, int>(names.Length);
        for (int i = 0; i < names.Length; i++) ids[names[i]] = i;
        TypedIcalls.Table = new Delegate[names.Length];
        var reg = new K9Reg();
        Core.K9(reg); TransformIcalls.K9(reg); InputState.K9(reg); RenderIcalls.K9(reg); CameraIcalls.K9(reg); AppIcalls.K9(reg);
        _listed = reg.List.Count;
        if (!Off(TypedMode))
        {
            foreach (var (key, typed, slow) in reg.List)
            {
                if (!ids.TryGetValue(key, out int id) || Icalls.Table[id] is not { } bound || bound.Method != slow.Method)
                { _skipped++; _skipNote += " " + key; continue; }
                TypedIcalls.Table[id] = TypedMode == "check" ? Checked(key, typed, bound) : typed;
                _bound++;
            }
            TypedIcalls.On = _bound > 0;
        }
        BindMirrors(ids);
        Log.Info(Describe());
    }

    static void BindMirrors(Dictionary<string, int> ids)
    {
        if (Off(MirrorMode)) { _mirrorNote = "OFF (SHEPHERD_K9_MIRROR=0: the stubs ask the engine, kit #8)"; return; }
        var hooks = AppIcalls.MirrorHooks().ToList();
        // the constants and no-ops (review fix): by method identity too -- each key must be bound to the very hook whose answer the Mirror
        // field / the empty stub copies (another hook bound over the key, e.g. a real Material.HasProperty, turns the mirrors off)
        var consts = RenderIcalls.K9MirrorConsts().Concat(InputState.K9MirrorConsts()).Concat(AppIcalls.K9MirrorConsts()).ToList();
        string Bad(string key, IcallImpl hook) => ids.TryGetValue(key, out int id) && Icalls.Table[id] is { } b && b.Method == hook.Method ? null : key;
        var bad = hooks.Select(h => Bad(h.key, h.hook)).Concat(consts.Select(c => Bad(c.key, c.hook))).Where(x => x != null).ToList();
        if (bad.Count > 0) { _mirrorNote = "OFF: not bound to the hooks the mirrors copy:" + string.Join(" ", bad); return; }
        Mirror.FrameFixed = Physics.PhysTime.FrameFixed;
        Mirror.MeshCanAccess = true; Mirror.MaterialHasProperty = true; Mirror.MousePresent = true; Mirror.RunInBackground = true; Mirror.TouchCount = 0;
        _mirrors = hooks.Count + consts.Count;
        if (MirrorMode == "check")
        {
            foreach (var (key, hook) in hooks)
            {
                int id = ids[key]; var k = key;
                Icalls.Table[id] = a => { var o = hook(a); Compare(k, MirrorFormula(k), o); return o; };
            }
            foreach (var (key, hook, value) in consts)   // (review fix: the constants' answers compared too -- a hook rewritten in place is caught on the routes)
            {
                int id = ids[key]; var k = key; var v = value;
                Icalls.Table[id] = a => { var o = hook(a); Compare(k, v, o); return o; };
            }
            _mirrorNote = $"CHECK ({_mirrors} getters: the stubs ask the engine, every mirrored hook's answer compared with its formula / constant over Shepherd.Hooks.Mirror)";
            return;
        }
        Mirror.On = true;
        _mirrorNote = $"ON ({_mirrors} getters answered by the stubs from Shepherd.Hooks.Mirror: Time, constants, no-ops)";
    }
    static object MirrorFormula(string key) => AppIcalls.MirrorFormula(key);

    // ---- check mode: each getter twin wrapped -- the hook runs too, its answer (through Icalls.Ret's rule) is compared and returned ----
    static T Ret<T>(object o) => o is T v ? v : default;
    static unsafe bool SameF(float a, float b) => *(uint*)&a == *(uint*)&b;
    /// <summary>Review fix: values by value (floats bit for bit, strings by content), every other reference -- a Unity object -- by identity:
    /// UnityEngine.Object.Equals calls a destroyed object equal to null and to every other destroyed one, but the game sees the difference
    /// through ReferenceEquals / 'is null' / '?.'.</summary>
    static bool Same(object want, object got) => want switch
    {
        null => got is null,
        float f1 => got is float f2 && SameF(f1, f2),
        string s1 => got is string s2 && s1 == s2,
        ValueType => Equals(want, got),
        _ => ReferenceEquals(want, got),
    };
    static void Compare(string key, object want, object got)
    {
        Interlocked.Increment(ref _checks);
        if (Same(want, got)) return;
        Interlocked.Increment(ref _bad);
        lock (_badLines) if (_badLines.Count < 20) { var l = $"[k9] typed/mirror check MISMATCH {key}: typed/mirror {want ?? "null"} vs hook {got ?? "null"} (f{Time.FrameCount})"; _badLines.Add(l); Log.Warn(l); }
    }
    static void CompareF(string key, float[] t, float[] h)
    {
        Interlocked.Increment(ref _checks);
        for (int i = 0; i < t.Length; i++) if (!SameF(t[i], h[i])) { Interlocked.Increment(ref _bad); lock (_badLines) if (_badLines.Count < 20) { var l = $"[k9] typed check MISMATCH {key}: field {i} typed {t[i]} vs hook {h[i]} (f{Time.FrameCount})"; _badLines.Add(l); Log.Warn(l); } return; }
    }
    static float[] V4(object o) => o switch { Quaternion q => new[] { q.x, q.y, q.z, q.w }, Rect r => new[] { r.x, r.y, r.width, r.height }, _ => new float[4] };
    static Delegate Checked(string k, Delegate typed, IcallImpl hook) => typed switch
    {
        Func<object, object> d => (Func<object, object>)(s => { var t = d(s); var h = hook(new object[] { s }); Compare(k, t, h); return h; }),
        Func<object, bool> d => (Func<object, bool>)(s => { var t = d(s); var h = Ret<bool>(hook(new object[] { s })); Compare(k, t, h); return h; }),
        Func<object, int> d => (Func<object, int>)(s => { var t = d(s); var h = Ret<int>(hook(new object[] { s })); Compare(k, t, h); return h; }),
        Func<object, int, float> d => (Func<object, int, float>)((s, a) => { var t = d(s, a); var h = Ret<float>(hook(new object[] { s, a })); Compare(k, t, h); return h; }),
        Func<float> d => (Func<float>)(() => { var t = d(); var h = Ret<float>(hook(new object[0])); Compare(k, t, h); return h; }),
        Func<string, float> d => (Func<string, float>)(a => { var t = d(a); var h = Ret<float>(hook(new object[] { a })); Compare(k, t, h); return h; }),
        Func<string, bool> d => (Func<string, bool>)(a => { var t = d(a); var h = Ret<bool>(hook(new object[] { a })); Compare(k, t, h); return h; }),
        Func<int, bool> d => (Func<int, bool>)(a => { var t = d(a); var h = Ret<bool>(hook(new object[] { a })); Compare(k, t, h); return h; }),
        Func<int, object> d => (Func<int, object>)(a => { var t = d(a); var h = hook(new object[] { a }); Compare(k, t, h); return h; }),
        TypedIcalls.DV3 d => (TypedIcalls.DV3)((object s, out float x, out float y, out float z) =>
        {
            d(s, out var tx, out var ty, out var tz); var a = new object[2]; a[0] = s; hook(a); var v = Ret<Vector3>(a[1]);
            CompareF(k, new[] { tx, ty, tz }, new[] { v.x, v.y, v.z }); x = v.x; y = v.y; z = v.z;
        }),
        TypedIcalls.DV4 d => (TypedIcalls.DV4)((object s, out float x, out float y, out float z, out float w) =>
        {
            d(s, out var tx, out var ty, out var tz, out var tw); var a = new object[2]; a[0] = s; hook(a); var h = V4(a[1]);
            CompareF(k, new[] { tx, ty, tz, tw }, h); x = h[0]; y = h[1]; z = h[2]; w = h[3];
        }),
        _ => typed   // setters: their twin alone (a setter cannot run twice; the twin and the hook share one body -- TransformIcalls SetLSv /
                     // SetLPv / SetPv -- so only the stub's float transport differs, which the record hashes cover)
    };

    public static string Describe() =>
        $"[k9] typed icalls (lever 10 + D6): {(Off(TypedMode) ? "OFF (SHEPHERD_K9_TYPED=0: every stub runs kit #8's object[] forwarder)" : $"{_bound} of {_listed} twins bound{(TypedMode == "check" ? " in CHECK mode (each getter twin AND its hook run, compared; the hook's answer returned)" : "")}{(_skipped > 0 ? $"; {_skipped} kept on the forwarder (another hook bound):{_skipNote}" : "")}")} | mirrors (E5): {_mirrorNote}";

    /// <summary>Boot's exit: the check modes' verdict.</summary>
    public static string Summary() => TypedMode == "check" || MirrorMode == "check"
        ? $"[k9] typed/mirror check: {_checks} calls compared, {_bad} mismatch(es){(_bad == 0 ? "" : " -- see the WRN lines")}" : null;
}
