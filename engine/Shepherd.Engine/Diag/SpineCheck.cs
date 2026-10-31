using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Render;
using UnityEngine;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>Kit #9 fidelity track, "Spine -- rebuild the pose-bake CHECK" (vita/notes/kit9-fidelity.md; roadmap "Pose-bake research" 2026-10-21):
/// the groundwork for kit #13's native steady-state evaluator. Mac only, test only: nothing here changes a frame, nothing ships (no bake is
/// used anywhere). SHEPHERD_SPINE_CHECK=&lt;outdir&gt; (SHEPHERD_SPINE_CHECK_ASSETS=a,b filters; SHEPHERD_SPINE_CHECK_HZ = the bake rate, 60).
/// Every frame, every drawing SkeletonAnimation is sorted into
///   OUT OF SCOPE  more than one track, a track other than 0, a crossfade (mixingFrom) or alpha below 1 -- the mixing kit #13 ports;
///   FROZEN        in scope, but its track time and clip did not change since the previous check (off-screen LOD, a paused state): counted
///                 apart, and whether its pose changed anyway (bones written by game code, not the animation) is counted too;
///   UPDATED       in scope with a new time: checked twice --
///   (1) STEADY REPLAY, the evaluator's reference: a shadow skeleton with THE LIVE SKIN (never a merged skin: the kit #7 bake posed every
///       skeleton through one skin of all skins, which switched on other outfits' skin-only constraints), the live skeleton's scale / position,
///       restored to the live state of the previous update (bone locals, constraint mixes, slot attachments / colours / deform: the
///       history MixBlend.First keeps), then the game's own Animation.Apply(lastTime, time, MixBlend.First, alpha 1) + UpdateWorldTransform.
///       Compared BIT FOR BIT with the live skeleton: every active bone's 7 locals and its world a, b, c, d, x, y; every slot's attachment
///       (the object), colour (and dark colour), deform; the draw order; and every shown vertex through Spine's own ComputeWorldVertices,
///       projected to screen pixels by the camera that draws it. A native evaluator must reproduce exactly this.
///   (2) PER-SKIN BAKE, the test tool the roadmap keeps: the pose sampled at the bake rate with the live skin (two samples bracketing the
///       live time, world transforms lerped -- what a pose bake can store), and the slots of the floor sample; vertices in pixels.
/// Per clip (asset x skin x clip) the verdicts: replay EXACT / NOT EXACT (the first mismatch named), bake WITHIN 0.1 PX / NOT (a bake is never
/// bit-identical for a moving clip: arbitrary float times), frames frozen / updated / out of scope / without history. Written to
/// &lt;outdir&gt;/spine-check.txt (+ one summary line in engine.log). Game-derived output: owner-local, never in git.</summary>
[ProfileOnly]
public static class SpineCheck
{
    static readonly string Out = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CHECK");
    static readonly HashSet<string> AssetFilter = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CHECK_ASSETS") is { Length: > 0 } f ? new HashSet<string>(f.Split(',')) : null;
    static readonly float Hz = float.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CHECK_HZ"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hz) && hz > 0 ? hz : 60f;
    public static bool On => Out != null;
    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ---------- reflection helpers (compiled; the same as kit #7's SpineBake / the kit #8 research tool) ----------
    static readonly Dictionary<(Type, string), Func<object, object>> _get = new();
    static readonly Dictionary<(Type, string), Func<object, float>> _getF = new();
    static readonly Dictionary<(Type, string), Action<object, float>> _setF = new();
    static readonly Dictionary<(Type, string), Action<object, object>> _setO = new();
    static FieldInfo FiOrNull(Type t, string n) { for (var c = t; c != null; c = c.BaseType) { var fi = c.GetField(n, I | BindingFlags.DeclaredOnly); if (fi != null) return fi; } return null; }
    static FieldInfo Fi(Type t, string n) => FiOrNull(t, n) ?? throw new MissingFieldException(t.Name, n);
    static object G(object o, string n)
    {
        var t = o.GetType();
        if (!_get.TryGetValue((t, n), out var g))
        {
            var p = Expression.Parameter(typeof(object)); var fi = Fi(t, n);
            g = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Field(Expression.Convert(p, fi.DeclaringType), fi), typeof(object)), p).Compile();
            _get[(t, n)] = g;
        }
        return g(o);
    }
    static float GF(object o, string n)
    {
        var t = o.GetType();
        if (!_getF.TryGetValue((t, n), out var g))
        {
            var p = Expression.Parameter(typeof(object)); var fi = Fi(t, n);
            g = Expression.Lambda<Func<object, float>>(Expression.Field(Expression.Convert(p, fi.DeclaringType), fi), p).Compile();
            _getF[(t, n)] = g;
        }
        return g(o);
    }
    static void SF(object o, string n, float v)
    {
        var t = o.GetType();
        if (!_setF.TryGetValue((t, n), out var s))
        {
            var p = Expression.Parameter(typeof(object)); var vv = Expression.Parameter(typeof(float)); var fi = Fi(t, n);
            s = Expression.Lambda<Action<object, float>>(Expression.Assign(Expression.Field(Expression.Convert(p, fi.DeclaringType), fi), vv), p, vv).Compile();
            _setF[(t, n)] = s;
        }
        s(o, v);
    }
    static void SO(object o, string n, object v)
    {
        var t = o.GetType();
        if (!_setO.TryGetValue((t, n), out var s))
        {
            var p = Expression.Parameter(typeof(object)); var vv = Expression.Parameter(typeof(object)); var fi = Fi(t, n);
            s = Expression.Lambda<Action<object, object>>(Expression.Assign(Expression.Field(Expression.Convert(p, fi.DeclaringType), fi), Expression.Convert(vv, fi.FieldType)), p, vv).Compile();
            _setO[(t, n)] = s;
        }
        s(o, v);
    }
    static int Count(object exposedList) => (int)G(exposedList, "Count");
    static Array Items(object exposedList) => (Array)G(exposedList, "Items");
    static bool Is(object o, string typeName) { for (var t = o?.GetType(); t != null; t = t.BaseType) if (t.Name == typeName) return true; return false; }
    static string Name(object dataOwner) => (string)G(G(dataOwner, "data"), "name");
    static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    static Action<object> _setup, _uwt; static Action<object, object, float> _applySetup; static Action<object, object, float, float> _applyFirst; static Action<object, object> _setSkin;
    static Action<object, object, float[]> _regionCwv; static Action<object, object, int, float[]> _meshCwv;
    static bool _darkFields;
    static void Bind(Type skelT)
    {
        if (_setup != null) return;
        var asm = skelT.Assembly; Type T(string n) => asm.GetType(n, true);
        var animT = T("Spine.Animation"); var skinT = T("Spine.Skin"); var boneT = T("Spine.Bone"); var slotT = T("Spine.Slot");
        var mixBlendT = T("Spine.MixBlend"); var mixDirT = T("Spine.MixDirection"); object dirIn = Enum.Parse(mixDirT, "In");
        var ps = Expression.Parameter(typeof(object), "sk"); var pa = Expression.Parameter(typeof(object), "an"); var pl = Expression.Parameter(typeof(float), "l"); var pt = Expression.Parameter(typeof(float), "t");
        _setup = Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(ps, skelT), skelT.GetMethod("SetToSetupPose")), ps).Compile();
        _uwt = Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(ps, skelT), skelT.GetMethod("UpdateWorldTransform", Type.EmptyTypes)), ps).Compile();
        var applyM = animT.GetMethod("Apply"); var evT = applyM.GetParameters()[4].ParameterType;
        Expression Call(Expression last, string blend) => Expression.Call(Expression.Convert(pa, animT), applyM, Expression.Convert(ps, skelT), last, pt, Expression.Constant(false), Expression.Constant(null, evT), Expression.Constant(1f), Expression.Constant(Enum.Parse(mixBlendT, blend), mixBlendT), Expression.Constant(dirIn, mixDirT));
        _applySetup = Expression.Lambda<Action<object, object, float>>(Call(Expression.Constant(0f), "Setup"), pa, ps, pt).Compile();
        _applyFirst = Expression.Lambda<Action<object, object, float, float>>(Call(pl, "First"), pa, ps, pl, pt).Compile();
        var pk = Expression.Parameter(typeof(object), "skin");
        _setSkin = Expression.Lambda<Action<object, object>>(Expression.Call(Expression.Convert(ps, skelT), skelT.GetMethod("SetSkin", new[] { skinT }), Expression.Convert(pk, skinT)), ps, pk).Compile();
        var regT = T("Spine.RegionAttachment"); var vaT = T("Spine.VertexAttachment");
        var patt = Expression.Parameter(typeof(object), "att"); var pb = Expression.Parameter(typeof(object), "bone"); var pw = Expression.Parameter(typeof(float[]), "w"); var pn = Expression.Parameter(typeof(int), "n");
        _regionCwv = Expression.Lambda<Action<object, object, float[]>>(Expression.Call(Expression.Convert(patt, regT), regT.GetMethod("ComputeWorldVertices", new[] { boneT, typeof(float[]), typeof(int), typeof(int) }), Expression.Convert(pb, boneT), pw, Expression.Constant(0), Expression.Constant(2)), patt, pb, pw).Compile();
        _meshCwv = Expression.Lambda<Action<object, object, int, float[]>>(Expression.Call(Expression.Convert(patt, vaT), vaT.GetMethod("ComputeWorldVertices", new[] { slotT, typeof(int), typeof(int), typeof(float[]), typeof(int), typeof(int) }), Expression.Convert(pb, slotT), Expression.Constant(0), pn, pw, Expression.Constant(0), Expression.Constant(2)), patt, pb, pn, pw).Compile();
        _darkFields = FiOrNull(slotT, "r2") != null;
    }

    // ---------- the live state that MixBlend.First carries from one update to the next ----------
    static readonly string[] LocalNames = { "x", "y", "rotation", "scaleX", "scaleY", "shearX", "shearY" };
    static readonly string[] WorldNames = { "a", "b", "c", "d", "worldX", "worldY" };
    static readonly (string list, string[] fields)[] MixFields = { ("ikConstraints", new[] { "mix", "softness" }), ("transformConstraints", new[] { "rotateMix", "translateMix", "scaleMix", "shearMix" }), ("pathConstraints", new[] { "position", "spacing", "rotateMix", "translateMix" }) };
    sealed class Snap { public float[] Loc, Mix, Col; public object[] Att; public float[][] Deform; public int[] Dro; }
    static string[] SlotColNames => _darkFields ? new[] { "r", "g", "b", "a", "r2", "g2", "b2" } : new[] { "r", "g", "b", "a" };
    static Snap Take(object sk)
    {
        var s = new Snap();
        var bi = Items(G(sk, "bones")); int nb = Count(G(sk, "bones")); s.Loc = new float[nb * 7];
        for (int i = 0; i < nb; i++) { var b = bi.GetValue(i); for (int c = 0; c < 7; c++) s.Loc[i * 7 + c] = GF(b, LocalNames[c]); }
        var mix = new List<float>();
        foreach (var (list, fields) in MixFields)
        {
            var fi = FiOrNull(sk.GetType(), list); if (fi == null) continue; var a = fi.GetValue(sk); int n = Count(a); var ai = Items(a);
            for (int i = 0; i < n; i++) { var c = ai.GetValue(i); foreach (var f in fields) if (FiOrNull(c.GetType(), f) != null) mix.Add(GF(c, f)); if (list == "ikConstraints") { mix.Add((int)G(c, "bendDirection")); mix.Add((bool)G(c, "compress") ? 1 : 0); mix.Add((bool)G(c, "stretch") ? 1 : 0); } }
        }
        s.Mix = mix.ToArray();
        var sli = Items(G(sk, "slots")); int ns = Count(G(sk, "slots")); var cn = SlotColNames; s.Col = new float[ns * cn.Length]; s.Att = new object[ns]; s.Deform = new float[ns][];
        for (int i = 0; i < ns; i++)
        {
            var sl = sli.GetValue(i); s.Att[i] = G(sl, "attachment");
            for (int c = 0; c < cn.Length; c++) s.Col[i * cn.Length + c] = GF(sl, cn[c]);
            var d = G(sl, "deform"); int dn = Count(d); s.Deform[i] = new float[dn]; Array.Copy(Items(d), s.Deform[i], dn);
        }
        var dro = Items(G(sk, "drawOrder")); s.Dro = new int[ns]; for (int i = 0; i < ns; i++) s.Dro[i] = (int)G(G(dro.GetValue(i), "data"), "index");
        return s;
    }
    static void Restore(object S, Snap s, object liveSk)
    {
        var bi = Items(G(S, "bones")); int nb = Count(G(S, "bones"));
        for (int i = 0; i < nb; i++) { var b = bi.GetValue(i); for (int c = 0; c < 7; c++) SF(b, LocalNames[c], s.Loc[i * 7 + c]); }
        int o = 0;
        foreach (var (list, fields) in MixFields)
        {
            var fi = FiOrNull(S.GetType(), list); if (fi == null) continue; var a = fi.GetValue(S); int n = Count(a); var ai = Items(a);
            for (int i = 0; i < n; i++) { var c = ai.GetValue(i); foreach (var f in fields) if (FiOrNull(c.GetType(), f) != null) SF(c, f, s.Mix[o++]); if (list == "ikConstraints") { SO(c, "bendDirection", (int)s.Mix[o++]); SO(c, "compress", s.Mix[o++] != 0); SO(c, "stretch", s.Mix[o++] != 0); } }
        }
        var sli = Items(G(S, "slots")); int ns = Count(G(S, "slots")); var cn = SlotColNames;
        for (int i = 0; i < ns; i++)
        {
            var sl = sli.GetValue(i); SO(sl, "attachment", s.Att[i]);
            for (int c = 0; c < cn.Length; c++) SF(sl, cn[c], s.Col[i * cn.Length + c]);
            SetDeform(G(sl, "deform"), s.Deform[i]);
        }
        // the draw order as the previous update left it (a draw order timeline with First keys it from the setup order anyway)
        var dS = Items(G(S, "drawOrder")); var slS = Items(G(S, "slots"));
        for (int i = 0; i < ns && i < s.Dro.Length; i++) dS.SetValue(slS.GetValue(s.Dro[i]), i);
        SF(S, "scaleX", GF(liveSk, "scaleX")); SF(S, "scaleY", GF(liveSk, "scaleY")); SF(S, "x", GF(liveSk, "x")); SF(S, "y", GF(liveSk, "y"));
    }
    static void SetDeform(object list, float[] v)
    {
        var resize = list.GetType().GetMethod("Resize"); resize.Invoke(list, new object[] { v.Length });
        Array.Copy(v, Items(list), v.Length);
    }

    // ---------- per live skeleton ----------
    sealed class Sh { public object Skin, R, Work, B; public string CurClip; public float CurT = float.NaN; public Snap Cur; public int CurFrame = -1; }
    static readonly Dictionary<object, Sh> _sh = new(ReferenceEqualityComparer.Instance);
    static object NewSkel(object liveSk, object skin) { var sk = Activator.CreateInstance(liveSk.GetType(), G(liveSk, "data")); if (skin != null) _setSkin(sk, skin); _setup(sk); _uwt(sk); return sk; }

    sealed class Stat
    {
        public string Asset, Skin, Clip;
        public long Updated, Frozen, FrozenMoved, OutOfScope, NoHistory, BonesMoved, AttChanged;
        public long Rep, RepExact, RepBones, RepAtt, RepCol, RepDro, RepDeform, RepVertsInexact; public double RepMaxPx; public string RepFirst; public int RepFirstFrame = -1;
        public long Bake, BakeOver, BakeExact, BakeAtt, BakeCol, BakeDro; public double BakeMaxPx; public string BakeWorst; public string OutWhy;
    }
    static readonly Dictionary<(string, string, string), Stat> _stats = new();
    static long _frames, _skelFrames;
    static readonly List<float[]> _liveVerts = new(); static readonly List<int> _liveSlot = new();

    public static void After(int frame)
    {
        if (!On) return;
        try { Run(frame); } catch (Exception e) { Log.Exception(e, "SpineCheck"); }
    }

    static void Run(int frame)
    {
        var cam = FirstScreenCamera(); if (cam == null) return; _frames++;
        var vp = M.Mul(CameraIcalls.Proj(cam), CameraIcalls.View(cam)); var pr = CameraIcalls.PixelRectF(cam); float gh = SceneRenderer.G?.H ?? 544;
        foreach (var n in Objects.All.ToArray())
        {
            if (n is not NComponent comp || comp.Destroyed || comp.Managed == null || !Is(comp.Managed, "SkeletonAnimation")) continue;
            if (comp.Go == null || comp.Go.Scene == 0 || !comp.Go.ActiveInHierarchy) continue;
            var m = comp.Managed; if (G(m, "valid") is not true || G(m, "processingComplete") is false) continue;
            var asset = G(m, "skeletonDataAsset") as UnityEngine.Object; if (asset == null) continue;
            if (AssetFilter != null && !AssetFilter.Contains(asset.name)) continue;
            var st = G(m, "state"); var sk = G(m, "skeleton"); if (st == null || sk == null) continue;
            Bind(sk.GetType());
            var tracks = G(st, "tracks"); int ntr = Count(tracks); var ti = Items(tracks); object entry = null; int live = 0;
            for (int k = 0; k < ntr; k++) if (ti.GetValue(k) is { } te) { live++; entry ??= te; }
            if (live == 0) continue;
            var an = G(entry, "animation"); if (an == null) continue; var clip = (string)G(an, "name");
            var liveSkin = G(sk, "skin"); string skinName = liveSkin == null ? "(none)" : (string)G(liveSkin, "name");
            var key = (asset.name, skinName, clip);
            if (!_stats.TryGetValue(key, out var s)) _stats[key] = s = new Stat { Asset = asset.name, Skin = skinName, Clip = clip };
            if (!_sh.TryGetValue(sk, out var sh)) _sh[sk] = sh = new Sh();
            _skelFrames++;
            string why = live != 1 ? "several tracks" : (int)G(entry, "trackIndex") != 0 ? "track > 0" : G(entry, "mixingFrom") != null ? "crossfade" : GF(entry, "alpha") != 1f ? "alpha < 1" : null;
            float t = GF(entry, "nextAnimationLast");
            var snap = Take(sk);
            bool changed = sh.CurClip != clip || Bits(t) != Bits(sh.CurT);
            if (why != null || t < 0)
            {
                s.OutOfScope++; s.OutWhy ??= why ?? "not applied yet";
                sh.CurClip = clip; sh.CurT = t; sh.Cur = snap; sh.CurFrame = frame;   // (the history a later steady update starts from)
                continue;
            }
            if (!changed)
            {
                s.Frozen++;
                if (sh.Cur != null && !SameBones(sh.Cur, snap)) s.FrozenMoved++;   // the pose changed with no new animation time: written by game code
                sh.Cur = snap; sh.CurFrame = frame;
                continue;
            }
            s.Updated++;
            if (sh.Cur != null && sh.CurClip == clip) { if (!SameBones(sh.Cur, snap)) s.BonesMoved++; for (int q = 0; q < snap.Att.Length; q++) if (!ReferenceEquals(snap.Att[q], sh.Cur.Att[q])) { s.AttChanged++; break; } }
            if (!ReferenceEquals(sh.Skin, liveSkin) || sh.R == null) { sh.R = NewSkel(sk, liveSkin); sh.Work = NewSkel(sk, liveSkin); sh.B = NewSkel(sk, liveSkin); sh.Skin = liveSkin; }
            var lw = comp.Go.Transform.LocalToWorld;
            LiveVerts(sk);
            // (1) the steady replay from the previous update's state
            bool history = sh.Cur != null && sh.CurClip == clip && !float.IsNaN(sh.CurT);
            if (!history) s.NoHistory++;
            else
            {
                Restore(sh.R, sh.Cur, sk);
                _applyFirst(an, sh.R, sh.CurT, t); _uwt(sh.R);
                s.Rep++;
                string first = null; bool ok = true;
                if (!BonesEqual(sk, sh.R, out var bw)) { ok = false; s.RepBones++; first ??= "bone " + bw; }
                var mis = SlotsEqual(sk, sh.R);
                if (mis.att != null) { ok = false; s.RepAtt++; first ??= "attachment of slot " + mis.att; }
                if (mis.col != null) { ok = false; s.RepCol++; first ??= "colour of slot " + mis.col; }
                if (mis.dro) { ok = false; s.RepDro++; first ??= "draw order"; }
                if (mis.deform != null) { ok = false; s.RepDeform++; first ??= "deform of slot " + mis.deform; }
                double px = VertErr(sk, sh.R, in lw, in vp, in pr, gh, copyDeform: false, out var vw, out bool vexact);
                if (!vexact) { ok = false; s.RepVertsInexact++; first ??= "vertices at " + vw; }
                if (px > s.RepMaxPx) s.RepMaxPx = px;
                if (ok) s.RepExact++;
                else if (s.RepFirst == null) { s.RepFirst = first + $" (t {t:R}, previous update t {sh.CurT:R})"; s.RepFirstFrame = frame; }
            }
            // (2) the per-skin bake at Hz: two samples bracketing t, world lerped; slots of the floor sample
            {
                float dur = GF(an, "duration"); int frames = Math.Max(1, (int)MathF.Ceiling(dur * Hz)) + 1;
                float uu = Math.Clamp(t * Hz, 0, frames - 1); int f0 = (int)MathF.Floor(uu), f1 = Math.Min(f0 + 1, frames - 1); float w = uu - f0;
                float t0 = Math.Min(f0 / Hz, dur), t1 = Math.Min(f1 / Hz, dur);
                int nb = Count(G(sk, "bones")); var wbi = Items(G(sh.Work, "bones"));
                var p1 = PoseW(sh.Work, an, t1, nb, wbi); var p0 = PoseW(sh.Work, an, t0, nb, wbi);   // (floor sample last: Work's slots = the floor sample's)
                WriteWorld(sh.B, nb, p0, p1, w, GF(sk, "scaleX"), GF(sk, "scaleY"), GF(sk, "x"), GF(sk, "y"));
                CopySlotsForVerts(sh.Work, sh.B);
                var mis = SlotsEqual(sk, sh.Work);
                s.Bake++;
                if (mis.att != null) s.BakeAtt++; if (mis.col != null) s.BakeCol++; if (mis.dro) s.BakeDro++;
                double px = VertErr(sk, sh.B, in lw, in vp, in pr, gh, copyDeform: true, out var vw, out bool vexact);
                if (vexact) s.BakeExact++;
                if (px >= 0.1) s.BakeOver++;
                if (px > s.BakeMaxPx) { s.BakeMaxPx = px; s.BakeWorst = $"f{frame} t {t:R} ({t * Hz:F3} samples) at {vw}"; }
            }
            sh.CurClip = clip; sh.CurT = t; sh.Cur = snap; sh.CurFrame = frame;
        }
    }

    static bool SameBones(Snap a, Snap b) { if (a.Loc.Length != b.Loc.Length) return false; for (int i = 0; i < a.Loc.Length; i++) if (Bits(a.Loc[i]) != Bits(b.Loc[i])) return false; return true; }
    static bool BonesEqual(object live, object S, out string worst)
    {
        worst = null; var bi = Items(G(live, "bones")); var si = Items(G(S, "bones")); int nb = Count(G(live, "bones"));
        for (int i = 0; i < nb; i++)
        {
            var lb = bi.GetValue(i); if (G(lb, "active") is not true) continue; var sb = si.GetValue(i);
            foreach (var c in LocalNames) if (Bits(GF(lb, c)) != Bits(GF(sb, c))) { worst = $"'{Name(lb)}' local {c} live {GF(lb, c):R} replay {GF(sb, c):R}"; return false; }
            foreach (var c in WorldNames) if (Bits(GF(lb, c)) != Bits(GF(sb, c))) { worst = $"'{Name(lb)}' world {c} live {GF(lb, c):R} replay {GF(sb, c):R}"; return false; }
        }
        return true;
    }
    static (string att, string col, bool dro, string deform) SlotsEqual(object live, object S)
    {
        string att = null, col = null, deform = null; bool dro = false; var cn = SlotColNames;
        var li = Items(G(live, "slots")); var si = Items(G(S, "slots")); int ns = Count(G(live, "slots"));
        for (int i = 0; i < ns; i++)
        {
            var ls = li.GetValue(i); var ss = si.GetValue(i); if (G(G(ls, "bone"), "active") is not true) continue;
            if (att == null && !ReferenceEquals(G(ls, "attachment"), G(ss, "attachment"))) att = $"'{Name(ls)}' live '{AttName(G(ls, "attachment"))}' vs '{AttName(G(ss, "attachment"))}'";
            if (col == null) foreach (var c in cn) if (Bits(GF(ls, c)) != Bits(GF(ss, c))) { col = $"'{Name(ls)}' {c} live {GF(ls, c):R} vs {GF(ss, c):R}"; break; }
            if (deform == null)
            {
                var ld = G(ls, "deform"); var sd = G(ss, "deform"); int ln = Count(ld), sn = Count(sd);
                if (ln != sn) deform = $"'{Name(ls)}' {ln} vs {sn} values";
                else { var la = (float[])Items(ld); var sa = (float[])Items(sd); for (int k = 0; k < ln; k++) if (Bits(la[k]) != Bits(sa[k])) { deform = $"'{Name(ls)}' value {k}"; break; } }
            }
        }
        var ld2 = Items(G(live, "drawOrder")); var sd2 = Items(G(S, "drawOrder"));
        for (int i = 0; i < ns; i++) if ((int)G(G(ld2.GetValue(i), "data"), "index") != (int)G(G(sd2.GetValue(i), "data"), "index")) { dro = true; break; }
        return (att, col, dro, deform);
    }
    static string AttName(object a) => a == null ? null : (string)a.GetType().GetProperty("Name")?.GetValue(a);
    static float[] PoseW(object W, object an, float t, int nb, Array wbi)
    {
        _setup(W); _applySetup(an, W, t); _uwt(W);
        var p = new float[nb * 6];
        for (int i = 0; i < nb; i++) { var b = wbi.GetValue(i); for (int c = 0; c < 6; c++) p[i * 6 + c] = GF(b, WorldNames[c]); }
        return p;
    }
    static void WriteWorld(object S, int nb, float[] p0, float[] p1, float w, float sx, float sy, float skx, float sky)
    {
        var sbi = Items(G(S, "bones"));
        for (int i = 0; i < nb; i++)
        {
            var b = sbi.GetValue(i); int o = i * 6;
            float L(int c) => p0[o + c] + (p1[o + c] - p0[o + c]) * w;
            SF(b, "a", L(0) * sx); SF(b, "b", L(1) * sx); SF(b, "c", L(2) * sy); SF(b, "d", L(3) * sy); SF(b, "worldX", L(4) * sx + skx); SF(b, "worldY", L(5) * sy + sky);
        }
    }
    static void CopySlotsForVerts(object from, object to)
    {
        var fi = Items(G(from, "slots")); var ti = Items(G(to, "slots")); int ns = Count(G(from, "slots"));
        for (int i = 0; i < ns; i++) SO(ti.GetValue(i), "attachment", G(fi.GetValue(i), "attachment"));
    }
    static void LiveVerts(object sk)
    {
        _liveVerts.Clear(); _liveSlot.Clear();
        var sli = Items(G(sk, "slots")); int nsl = Count(G(sk, "slots"));
        for (int si = 0; si < nsl; si++)
        {
            var sl = sli.GetValue(si); var att = G(sl, "attachment"); if (att == null || G(G(sl, "bone"), "active") is not true) continue;
            float[] v;
            if (Is(att, "RegionAttachment")) { v = new float[8]; _regionCwv(att, G(sl, "bone"), v); }
            else if (Is(att, "MeshAttachment")) { int nv = (int)G(att, "worldVerticesLength"); v = new float[nv]; _meshCwv(att, sl, nv, v); }
            else continue;
            _liveVerts.Add(v); _liveSlot.Add(si);
        }
    }
    /// <summary>Every live shown vertex against the same attachment through the shadow's bones (the live attachment; the shadow's own deform,
    /// or the live deform copied for the bake, which stores none) -- the largest screen distance, and whether every coordinate is bit-equal.</summary>
    static double VertErr(object sk, object S, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, bool copyDeform, out string where, out bool exact)
    {
        double mx = 0; where = null; exact = true;
        var sli = Items(G(sk, "slots")); var ssli = Items(G(S, "slots"));
        for (int q = 0; q < _liveVerts.Count; q++)
        {
            int si = _liveSlot[q]; var sl = sli.GetValue(si); var ssl = ssli.GetValue(si); var att = G(sl, "attachment"); var lv = _liveVerts[q];
            var sv = new float[lv.Length]; var keep = G(ssl, "attachment");
            if (Is(att, "RegionAttachment")) _regionCwv(att, G(ssl, "bone"), sv);
            else { if (copyDeform) SetDeform(G(ssl, "deform"), (float[])Items(G(sl, "deform")) is var dl ? dl[..Count(G(sl, "deform"))] : Array.Empty<float>()); SO(ssl, "attachment", att); _meshCwv(att, ssl, lv.Length, sv); SO(ssl, "attachment", keep); }
            for (int k = 0; k + 1 < lv.Length; k += 2)
            {
                if (Bits(lv[k]) != Bits(sv[k]) || Bits(lv[k + 1]) != Bits(sv[k + 1])) exact = false;
                double e = PxDist(in lw, in vp, in pr, gh, lv[k], lv[k + 1], sv[k], sv[k + 1]);
                if (e > mx || (where == null && !exact)) { mx = Math.Max(mx, e); where = $"slot '{Name(sl)}' {att.GetType().Name.Replace("Attachment", "")} '{AttName(att)}' v{k / 2} ({e:F4} px)"; }
            }
        }
        return mx;
    }
    static double PxDist(in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, float x0, float y0, float x1, float y1)
    {
        Vector4 ca = default, cb = default; bool outside = false;
        SceneRenderer.Project(in lw, in vp, in pr, gh, x0, y0, 0, out var ax, out var ay, ref ca, ref outside);
        SceneRenderer.Project(in lw, in vp, in pr, gh, x1, y1, 0, out var bx, out var by, ref cb, ref outside);
        if (outside || !OnScreen(ca) || !OnScreen(cb)) return 0;
        double dx = ax - bx, dy = ay - by; return Math.Sqrt(dx * dx + dy * dy);
    }
    static bool OnScreen(in Vector4 c) => c.w > 1e-5f && c.x >= -c.w && c.x <= c.w && c.y >= -c.w && c.y <= c.w;
    static NBehaviour FirstScreenCamera() { foreach (var c in CameraIcalls.AllCameras()) if (CameraIcalls.TargetOf(c) == null) return c; return null; }

    public static void Summary()
    {
        if (!On) return;
        var sb = new StringBuilder();
        long upd = _stats.Values.Sum(x => x.Updated), fro = _stats.Values.Sum(x => x.Frozen), oos = _stats.Values.Sum(x => x.OutOfScope), rep = _stats.Values.Sum(x => x.Rep), repX = _stats.Values.Sum(x => x.RepExact);
        int clipsExact = _stats.Values.Count(x => x.Rep > 0 && x.RepExact == x.Rep), clipsRep = _stats.Values.Count(x => x.Rep > 0);
        int bakeOk = _stats.Values.Count(x => x.Bake > 0 && x.BakeOver == 0 && x.BakeAtt == 0 && x.BakeCol == 0 && x.BakeDro == 0), bakeClips = _stats.Values.Count(x => x.Bake > 0);
        string head = $"[spinecheck] {_frames:N0} frames, {_skelFrames:N0} skeleton-frames: updated {upd:N0}, frozen {fro:N0} (pose moved anyway in {_stats.Values.Sum(x => x.FrozenMoved):N0}), out of scope {oos:N0} (mixing / several tracks / alpha); steady replay bit-exact in {repX:N0} of {rep:N0} updates ({clipsExact} of {clipsRep} clips EXACT); per-skin bake at {Hz} Hz within 0.1 px with the live slots in {bakeOk} of {bakeClips} clips, bit-exact in {_stats.Values.Sum(x => x.BakeExact):N0} of {_stats.Values.Sum(x => x.Bake):N0} updates";
        sb.AppendLine(head);
        sb.AppendLine("[spinecheck] per clip (asset / skin / clip): frames updated / frozen (moved) / out of scope / no history; STEADY REPLAY verdict -- bit-exact updates, mismatching updates by part (bones, attachment, colour, draw order, deform, vertices), max px, the first mismatch; BAKE verdict -- max px, updates >= 0.1 px, slot mismatches at the floor sample (attachment, colour, draw order)");
        foreach (var s in _stats.Values.OrderByDescending(x => x.Rep - x.RepExact).ThenByDescending(x => x.BakeMaxPx))
        {
            string rv = s.Rep == 0 ? "NOT COVERED (no update with history)" : s.RepExact == s.Rep ? "EXACT" : "NOT EXACT";
            string bv = s.Bake == 0 ? "NOT COVERED" : s.BakeOver == 0 && s.BakeAtt == 0 && s.BakeCol == 0 && s.BakeDro == 0 ? (s.BakeExact == s.Bake ? "EXACT" : "WITHIN 0.1 PX") : "NOT WITHIN 0.1 PX";
            sb.AppendLine($"== '{s.Asset}' / skin '{s.Skin}' / '{s.Clip}': updated {s.Updated} (bones moved in {s.BonesMoved}{(s.BonesMoved == 0 ? ": a STATIC pose" : "")}, attachments changed in {s.AttChanged}), frozen {s.Frozen} (moved {s.FrozenMoved}), out of scope {s.OutOfScope}{(s.OutWhy != null ? " (" + s.OutWhy + ")" : "")}, no history {s.NoHistory}");
            sb.AppendLine($"   replay {rv}: {s.RepExact} / {s.Rep} bit-exact; bones {s.RepBones}, attachment {s.RepAtt}, colour {s.RepCol}, draw order {s.RepDro}, deform {s.RepDeform}, vertices {s.RepVertsInexact}; max {s.RepMaxPx:F4} px{(s.RepFirst != null ? $"; first at f{s.RepFirstFrame}: {s.RepFirst}" : "")}");
            sb.AppendLine($"   bake {bv}: max {s.BakeMaxPx:F4} px, {s.BakeOver} / {s.Bake} updates >= 0.1 px, bit-exact {s.BakeExact}; slots at the floor sample: attachment {s.BakeAtt}, colour {s.BakeCol}, draw order {s.BakeDro}{(s.BakeWorst != null ? "; worst " + s.BakeWorst : "")}");
        }
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "spine-check.txt"), sb.ToString());
        Log.Info(head + " -> " + Path.Combine(Out, "spine-check.txt"));
    }
}
#else
[ProfileOnly]
public static class SpineCheck { public static bool On => false; public static void After(int frame) { } public static void Summary() { } }
#endif
