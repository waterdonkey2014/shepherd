using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>kit #7 spine track (vita/notes/kit7-spine.md), measurement only (off unless SHEPHERD_SPINE_CENSUS is set; never in an eboot):
/// what the live Spine skeletons draw, read by reflection after the frames listed (nothing changes). Per SkeletonDataAsset: the
/// renderers, their slots / bones, the attachments shown (region, mesh -- weighted or not --, clipping, other), the vertices and
/// triangles MeshGenerator makes, the pieces whose vertex colour is all zero (PMA: a, r, g, b bytes 0 = the draw adds nothing under the
/// premultiplied blend unless the shader adds light of its own), the additive pieces (alpha byte 0, colour kept), deform keys, the
/// renderer flags (tintBlack, singleSubmesh, useClipping, mask interaction), the materials' shaders, SkeletonUtilityBones, the skins
/// (SkeletonData.Skins: how many, how many attachments, which are in use) and the animation tracks. The engine side: the drawn meshes'
/// triangles whose three vertices are colour 0 (the "skip provably invisible pieces" candidates).
/// SHEPHERD_SPINE_CENSUS=&lt;frame&gt;[,&lt;frame&gt;...] or &lt;from&gt;-&lt;to&gt; (one summary line per frame, the detail at the first frame of the range).</summary>
[ProfileOnly]
public static class SpineCensus
{
    static readonly int[] At; static readonly int From = -1, To = -1;
    public static readonly bool On;
    static readonly bool Dbg = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CENSUS_DBG") == "1";
    static SpineCensus()
    {
        var s = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CENSUS");
        if (string.IsNullOrEmpty(s)) { At = Array.Empty<int>(); return; }
        On = true;
        if (s.Contains('-')) { var p = s.Split('-'); From = int.Parse(p[0]); To = int.Parse(p[1]); At = Array.Empty<int>(); }
        else At = s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    }
    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Dictionary<(Type, string), FieldInfo> _f = new();
    static object F(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        if (!_f.TryGetValue((t, n), out var fi))
        {
            for (var c = t; c != null && fi == null; c = c.BaseType) fi = c.GetField(n, I | BindingFlags.DeclaredOnly);
            _f[(t, n)] = fi;
        }
        return fi?.GetValue(o);
    }
    static float Fl(object o, string n) => F(o, n) is float f ? f : 0f;
    static bool Bo(object o, string n) => F(o, n) is bool b && b;
    static int Count(object exposedList) => F(exposedList, "Count") is int c ? c : 0;
    static Array Items(object exposedList) => F(exposedList, "Items") as Array;
    static bool Is(object o, string typeName) { for (var t = o?.GetType(); t != null; t = t.BaseType) if (t.Name == typeName) return true; return false; }

    sealed class Agg
    {
        public int ClipTris, ClipPolyVerts;
        public int Renderers, Visible, Slots, Bones, Region, Mesh, Weighted, Clip, Other, Verts, Tris, ZeroPieces, ZeroVerts, ZeroTris, AddPieces, Deform, ClipActive, TintBlack, Single, NoClip, Mask, UtilBones, Mixing;
        public HashSet<string> Shaders = new(), Anims = new(), SkinsUsed = new();
        public string SkinInfo;
    }
    static int _lastDetail = -1;
    static readonly HashSet<object> _skinsReported = new(ReferenceEqualityComparer.Instance);

    public static void After(int frame)
    {
        if (!On) return;
        bool inRange = From >= 0 && frame >= From && frame <= To;
        if (!inRange && Array.IndexOf(At, frame) < 0) return;
        try { Run(frame, detail: !inRange || _lastDetail < 0); _lastDetail = frame; }
        catch (Exception e) { Log.Exception(e, "SpineCensus"); }
    }

    static void Run(int frame, bool detail)
    {
        var groups = new Dictionary<string, Agg>();
        int engineTris = 0, engineZeroTris = 0, engineDraws = 0, engineZeroDraws = 0, utilAll = 0;
        foreach (var n in Objects.All.ToArray())
        {
            if (n is not NComponent c || c.Destroyed || c.Managed == null) continue;
            var m = c.Managed;
            if (Is(m, "SkeletonUtilityBone")) { if (c.Go != null && c.Go.ActiveInHierarchy) utilAll++; continue; }
            if (!Is(m, "SkeletonRenderer")) continue;
            if (c.Go == null || c.Go.Scene == 0) continue;
            var asset = F(m, "skeletonDataAsset") as UnityEngine.Object;
            string key = asset != null ? asset.name : "(none)";
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Agg();
            g.Renderers++;
            // (CotL's SkeletonAnimationLOD managers disable the SkeletonAnimation component and drive OnUpdate / BaseLateUpdate themselves:
            // what draws is an active GameObject with a valid skeleton and an enabled MeshRenderer)
            NComponent mr = null; foreach (var x in c.Go.Components) if (x.Managed is MeshRenderer) { mr = x; break; }
            bool goAct = c.Go.ActiveInHierarchy, en = c is NBehaviour nb ? nb.Enabled : true, valid = Bo(m, "valid");
            bool mrOn = mr != null && !(mr.Get("enabled") is bool mre && !mre);
            if (detail && Dbg) Log.Info($"[spinecensus]     '{c.Go.Name}' ({key}): active {goAct} component enabled {en} valid {valid} renderer {mrOn} LOD {F(m, "isLODInitialized")} threaded {F(m, "useMultithreading")} visibleToRender {F(m, "isVisibleToRender")} lodVisible {F(m, "lodVisible")}");
            if (!(goAct && valid && mrOn)) continue;
            g.Visible++;
            if (Bo(m, "tintBlack")) g.TintBlack++;
            if (Bo(m, "singleSubmesh")) g.Single++;
            if (!Bo(m, "useClipping")) g.NoClip++;
            if (F(m, "maskInteraction") is { } mi && Convert.ToInt32(mi) != 0) g.Mask++;
            bool pma = Bo(m, "pmaVertexColors");
            var sk = F(m, "skeleton"); if (sk == null) continue;
            g.Slots += Count(F(sk, "slots")); g.Bones += Count(F(sk, "bones"));
            var skin = F(sk, "skin"); if (skin != null) g.SkinsUsed.Add((string)F(skin, "name") ?? "?");
            float ska = Fl(sk, "a"), skr = Fl(sk, "r"), skg = Fl(sk, "g"), skb = Fl(sk, "b");
            var dro = F(sk, "drawOrder"); int nd = Count(dro); var items = Items(dro);
            object clipEnd = null;   // the triangles MeshGenerator hands SkeletonClipping: every mesh / region slot from a clipping attachment to its end slot
            for (int i = 0; i < nd; i++)
            {
                var slotC = items.GetValue(i); var attC = F(slotC, "attachment");
                if (clipEnd == null && Is(attC, "ClippingAttachment") && Bo(F(slotC, "bone"), "active")) { clipEnd = F(attC, "endSlot"); g.ClipPolyVerts += (F(attC, "worldVerticesLength") is int cw ? cw : 0) >> 1; continue; }
                if (clipEnd != null && Bo(F(slotC, "bone"), "active"))
                {
                    if (Is(attC, "RegionAttachment")) g.ClipTris += 2; else if (Is(attC, "MeshAttachment")) g.ClipTris += ((F(attC, "triangles") as int[])?.Length ?? 0) / 3;
                }
                if (clipEnd != null && ReferenceEquals(F(slotC, "data"), clipEnd)) clipEnd = null;
            }
            for (int i = 0; i < nd; i++)
            {
                var slot = items.GetValue(i); var bone = F(slot, "bone");
                if (!Bo(bone, "active")) continue;
                var att = F(slot, "attachment"); if (att == null) continue;
                int nv, nt;
                if (Is(att, "RegionAttachment")) { g.Region++; nv = 4; nt = 6; }
                else if (Is(att, "MeshAttachment")) { g.Mesh++; if (F(att, "bones") != null) g.Weighted++; nv = (F(att, "worldVerticesLength") is int wl ? wl : 0) >> 1; nt = (F(att, "triangles") as int[])?.Length ?? 0; }
                else { if (Is(att, "ClippingAttachment")) { g.Clip++; g.ClipActive++; } else g.Other++; continue; }
                g.Verts += nv; g.Tris += nt / 3;
                if (Count(F(slot, "deform")) > 0) g.Deform++;
                // MeshGenerator's vertex colour bytes (pmaVertexColors: a = (byte)(a * slot.a * att.a * 255f), rgb x (float)(int)a; additive: a = 0)
                float sa = Fl(slot, "a"), aa = Fl(att, "a");
                byte ca = (byte)(ska * sa * aa * 255f);
                byte cr, cg, cb;
                if (pma) { cr = (byte)(skr * Fl(slot, "r") * Fl(att, "r") * (float)(int)ca); cg = (byte)(skg * Fl(slot, "g") * Fl(att, "g") * (float)(int)ca); cb = (byte)(skb * Fl(slot, "b") * Fl(att, "b") * (float)(int)ca); }
                else { cr = (byte)(skr * Fl(slot, "r") * Fl(att, "r") * 255f); cg = (byte)(skg * Fl(slot, "g") * Fl(att, "g") * 255f); cb = (byte)(skb * Fl(slot, "b") * Fl(att, "b") * 255f); }
                var sd = F(slot, "data"); bool additive = F(sd, "blendMode") is { } bm && Convert.ToInt32(bm) == 1;
                if (pma && additive) ca = 0;
                if (ca == 0 && cr == 0 && cg == 0 && cb == 0) { g.ZeroPieces++; g.ZeroVerts += nv; g.ZeroTris += nt / 3; }
                else if (ca == 0) g.AddPieces++;
            }
            // animation tracks
            var st = F(m, "state");
            if (st != null) { var tr = F(st, "tracks"); int ntr = Count(tr); var ti = Items(tr); for (int k = 0; k < ntr; k++) { var te = ti.GetValue(k); if (te == null) continue; var an = F(te, "animation"); g.Anims.Add((string)F(an, "name") ?? "?"); if (F(te, "mixingFrom") != null) g.Mixing++; } }
            // materials + the engine's drawn mesh
            if (mr != null)
            {
                foreach (var mat in Impl.RendererIcalls.Shared(mr)) { var nm = Objects.Get<NMaterial>(mat); if (nm != null) g.Shaders.Add(nm.ShaderName ?? "?"); }
                var mf = Render.SceneRenderer.FirstMeshFilter(c.Go);
                var mesh = mf == null ? null : Objects.Get<NMesh>(Conv.To(mf.Get("sharedMesh") ?? mf.Get("mesh"), typeof(Mesh)));
                if (mesh != null && mesh.Col != null)
                {
                    var col = mesh.Col;
                    foreach (var sub in mesh.Sub)
                    {
                        if (sub == null || sub.Length < 3) continue;
                        engineDraws++; int z = 0, t = sub.Length / 3;
                        for (int k = 0; k + 2 < sub.Length; k += 3)
                        {
                            int a = sub[k], b = sub[k + 1], d = sub[k + 2];
                            if ((uint)a < (uint)col.Length && (uint)b < (uint)col.Length && (uint)d < (uint)col.Length && Zero(col[a]) && Zero(col[b]) && Zero(col[d])) z++;
                        }
                        engineTris += t; engineZeroTris += z; if (z == t) engineZeroDraws++;
                    }
                }
            }
            // skins of this skeleton data (once per SkeletonData)
            var data = F(sk, "data");
            if (data != null && detail && _skinsReported.Add(data))
            {
                var skins = F(data, "skins"); int ns = Count(skins); var si = Items(skins); long atts = 0; int maxA = 0;
                for (int k = 0; k < ns; k++) { var s = si.GetValue(k); int na = (F(s, "attachments") as System.Collections.ICollection)?.Count ?? 0; atts += na; maxA = Math.Max(maxA, na); }
                g.SkinInfo = $"{ns} skins, {atts} skin attachments (largest {maxA}), {Count(F(data, "animations"))} animations, {Count(F(data, "bones"))} bones, {Count(F(data, "slots"))} slots";
            }
        }
        int vis = groups.Values.Sum(x => x.Visible), verts = groups.Values.Sum(x => x.Verts), tris = groups.Values.Sum(x => x.Tris), zt = groups.Values.Sum(x => x.ZeroTris), clips = groups.Values.Sum(x => x.ClipActive);
        Log.Info($"[spinecensus] f{frame}: {groups.Values.Sum(x => x.Renderers)} SkeletonRenderers ({vis} drawing) of {groups.Count} assets; skinned {verts} verts / {tris} tris; zero-colour {zt} tris ({groups.Values.Sum(x => x.ZeroPieces)} pieces); clipping attachments active {clips} (triangles through the clipper {groups.Values.Sum(x => x.ClipTris)}, clip polygon vertices {groups.Values.Sum(x => x.ClipPolyVerts)}); weighted meshes {groups.Values.Sum(x => x.Weighted)}; deform slots {groups.Values.Sum(x => x.Deform)}; utility bones {utilAll}; engine side: {engineDraws} draws / {engineTris} tris, all-zero-colour tris {engineZeroTris}, whole draws zero {engineZeroDraws}");
        if (!detail) return;
        // every built SkeletonData (drawing or not): skins and their attachments (the L2 lazy-skins measure)
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var n in Objects.All.ToArray())
        {
            var m = n.Managed; if (m == null || m.GetType().Name != "SkeletonDataAsset") continue;
            var data = F(m, "skeletonData"); if (data == null || !seen.Add(data)) continue;
            var skins = F(data, "skins"); int ns = Count(skins); var si = Items(skins); long atts = 0, reg = 0, mesh = 0, linked = 0, other = 0; int maxA = 0;
            for (int k = 0; k < ns; k++)
            {
                var s2 = si.GetValue(k); var dict = F(s2, "attachments") as System.Collections.IDictionary; int na = dict?.Count ?? 0; atts += na; maxA = Math.Max(maxA, na);
                if (dict != null) foreach (var v in dict.Values) { var a = F(v, "attachment"); if (Is(a, "RegionAttachment")) reg++; else if (Is(a, "MeshAttachment")) { mesh++; if (F(a, "parentMesh") != null) linked++; } else other++; }
            }
            // which skins an eager read cannot leave lazy: deform timelines hold their attachment (ReadAnimation looks it up in the skin it
            // names), and a linked mesh's parent (resolved at the end of the read) -- counted per skin
            var owner = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < ns; k++) { var d2 = F(si.GetValue(k), "attachments") as System.Collections.IDictionary; if (d2 != null) foreach (var v in d2.Values) { var a = F(v, "attachment"); if (a != null) owner.TryAdd(a, k); } }
            var deformSkins = new HashSet<int>(); var parentSkins = new HashSet<int>(); int deformTl = 0, crossLinks = 0;
            var anims = F(data, "animations"); int nan = Count(anims); var ai = Items(anims);
            for (int k = 0; k < nan; k++) { var tls = F(ai.GetValue(k), "timelines"); int nt = Count(tls); var tli = Items(tls); for (int q = 0; q < nt; q++) { var tl = tli.GetValue(q); if (!Is(tl, "DeformTimeline")) continue; deformTl++; var a = F(tl, "attachment"); if (a != null && owner.TryGetValue(a, out int sk)) deformSkins.Add(sk); } }
            foreach (var (a, sk) in owner) { var pm = F(a, "parentMesh"); if (pm != null && owner.TryGetValue(pm, out int ps) && ps != sk) { crossLinks++; parentSkins.Add(ps); } }
            Log.Info($"[spinecensus]   data '{((UnityEngine.Object)m).name}': deform timelines {deformTl} on {deformSkins.Count} skins [{string.Join(",", deformSkins.Take(12))}{(deformSkins.Count > 12 ? ",..." : "")}]; linked meshes with a parent in another skin {crossLinks} (parents in {parentSkins.Count} skins: [{string.Join(",", parentSkins.Take(12))}{(parentSkins.Count > 12 ? ",..." : "")}])");
            // skins with an attachment in a slot whose blend mode is not Normal (a BlendModeMaterialsAsset modifier touches only those)
            var slotsD = F(data, "slots"); var sdi = Items(slotsD); int nsl = Count(slotsD); var blendSlots = new HashSet<int>();
            for (int k = 0; k < nsl; k++) if (F(sdi.GetValue(k), "blendMode") is { } bmv && Convert.ToInt32(bmv) != 0) blendSlots.Add(k);
            int blendSkins = 0;
            for (int k = 0; k < ns; k++) { var d2 = F(si.GetValue(k), "attachments") as System.Collections.IDictionary; if (d2 == null) continue; foreach (var v in d2.Values) if (F(v, "slotIndex") is int sx && blendSlots.Contains(sx)) { blendSkins++; break; } }
            Log.Info($"[spinecensus]   data '{((UnityEngine.Object)m).name}': {blendSlots.Count} slots with a non-Normal blend mode; {blendSkins} of {ns} skins have an attachment in one; modifiers {(F(m, "skeletonDataModifiers") as System.Collections.ICollection)?.Count ?? 0}");
            {   // the distinct string objects the read attachments / keys hold (eager vs lazy)
                var held2 = new HashSet<object>(ReferenceEqualityComparer.Instance); long ch2 = 0;
                for (int k = 0; k < ns; k++) { var d2 = F(si.GetValue(k), "attachments") as System.Collections.IDictionary; if (d2 == null) continue; foreach (var v in d2.Values) { foreach (var sx in new[] { F(v, "name"), F(F(v, "attachment"), "name"), F(F(v, "attachment"), "path") }) if (sx is string st && held2.Add(st)) ch2 += st.Length; } }
                Log.Info($"[spinecensus]   data '{((UnityEngine.Object)m).name}': read skin attachments hold {held2.Count} distinct string objects ({ch2} chars)");
            }
            // kit #7 L2: the lazy skins' kept string table (one per read) vs the strings the read attachments hold
            {
                object table = null; int lazySkins = 0;
                for (int k = 0; k < ns; k++) { var lz = F(si.GetValue(k), "<Shepherd>lazy"); if (lz != null) { lazySkins++; table ??= F(lz, "strings"); } }
                if (table != null)
                {
                    int tn = Count(table); var ti2 = Items(table); long chars = 0; var held = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    for (int k = 0; k < tn; k++) if (ti2.GetValue(k) is string ts) chars += ts.Length;
                    for (int k = 0; k < ns; k++) { var d2 = F(si.GetValue(k), "attachments") as System.Collections.IDictionary; if (d2 == null) continue; foreach (var v in d2.Values) { if (F(v, "name") is string kn) held.Add(kn); var a = F(v, "attachment"); if (F(a, "name") is string an) held.Add(an); if (F(a, "path") is string ap) held.Add(ap); } }
                    int inTable = 0; for (int k = 0; k < tn; k++) if (ti2.GetValue(k) is string ts && held.Contains(ts)) inTable++;
                    Log.Info($"[spinecensus]   data '{((UnityEngine.Object)m).name}': {lazySkins} lazy skins; the kept string table {tn} strings ({chars} chars), {inTable} of them held by read attachments / keys");
                    if (Dbg) { var distinct = new HashSet<string>(); for (int k = 0; k < tn; k++) if (ti2.GetValue(k) is string ts) distinct.Add(ts); Log.Info($"[spinecensus]     table: {distinct.Count} distinct contents; samples: {string.Join(" | ", Enumerable.Range(0, 12).Select(q => ti2.GetValue((int)((long)q * tn / 12)) as string))}"); }
                }
            }
            Log.Info($"[spinecensus]   data '{((UnityEngine.Object)m).name}': {ns} skins, {atts} skin attachments (region {reg}, mesh {mesh} of which linked {linked}, other {other}; largest skin {maxA}), default skin {(F(F(data, "defaultSkin"), "attachments") as System.Collections.ICollection)?.Count ?? 0}, {Count(F(data, "animations"))} animations, {Count(F(data, "bones"))} bones, {Count(F(data, "slots"))} slots");
        }
        foreach (var (k, g) in groups.OrderByDescending(x => x.Value.Verts))
            Log.Info($"[spinecensus]   '{k}': {g.Renderers} renderers ({g.Visible} drawing); slots {g.Slots} bones {g.Bones}; region {g.Region} mesh {g.Mesh} (weighted {g.Weighted}) clip {g.Clip} ({g.ClipTris} tris clipped, polygon {g.ClipPolyVerts} verts) other {g.Other}; {g.Verts} verts {g.Tris} tris; zero-colour {g.ZeroPieces} pieces / {g.ZeroVerts} verts / {g.ZeroTris} tris; additive(a=0) {g.AddPieces}; deform {g.Deform}; tintBlack {g.TintBlack} single {g.Single} noClip {g.NoClip} mask {g.Mask}; mixing {g.Mixing}; shaders [{string.Join(", ", g.Shaders)}]; skins in use [{string.Join(", ", g.SkinsUsed.Take(6))}{(g.SkinsUsed.Count > 6 ? ", ..." : "")}]; anims [{string.Join(", ", g.Anims.Take(5))}{(g.Anims.Count > 5 ? ", ..." : "")}]{(g.SkinInfo != null ? "; data: " + g.SkinInfo : "")}");
    }
    static bool Zero(Color32 c) => c.r == 0 && c.g == 0 && c.b == 0 && c.a == 0;
}

/// <summary>kit #7 spine track, row "lazy visual-only animation" (measurement only; SHEPHERD_ANIM_CENSUS=&lt;frame&gt;[,...]; never in an eboot):
/// the active Animators the engine updates (AnimatorRuntime.UpdateAll's filter), their controllers, the clips playing and what those
/// clips' bindings drive -- GameObject active, transform / RectTransform channels, script fields or enables (read back by game code by
/// definition), built-in component properties by class (sprite flipbooks, colours, material values) -- and the events. A "visual-only"
/// candidate (the row's definition): no events, no script field / enable bindings, no GameObject-active or component-enable bindings and
/// no transform channels (a transform is read by layout, physics, children and scripts); it drives only renderer / graphic appearance.</summary>
[ProfileOnly]
public static class AnimCensus
{
    static readonly int[] At = (Environment.GetEnvironmentVariable("SHEPHERD_ANIM_CENSUS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    public static void After(int frame)
    {
        if (At.Length == 0 || Array.IndexOf(At, frame) < 0) return;
        try { Run(frame); } catch (Exception e) { Log.Exception(e, "AnimCensus"); }
    }
    static void Run(int frame)
    {
        int active = 0, visual = 0, withEvents = 0, total = 0; var kinds = new Dictionary<string, int>(); var rows = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew(); long tAll = 0, tVisual = 0;
        foreach (var n in Native.Registry.Animators.Snapshot())
        {
            total++;
            if (n is not NBehaviour b || b.Managed is not Animator || b.Destroyed || !b.Enabled || b.Go == null || b.Go.Scene == 0 || !b.Go.ActiveInHierarchy) continue;
            active++;
            var r = Anim.AnimatorRuntime.For(b.Managed); r.Init(); if (r.Ctrl == null) { rows.Add($"'{b.Go.Name}': no controller"); continue; }
            var mine = new Dictionary<string, int>(); bool ev = false, readBack = false;
            for (int l = 0; l < r.Layers.Length; l++)
            {
                var st = r.Current(l); var clip = r.ClipOf(st); if (clip == null) continue; var cd = Anim.ClipData.Of(clip); if (cd == null) continue;
                if (cd.Events.Count > 0) ev = true;
                foreach (var bd in cd.Bindings)
                {
                    string k = bd.TypeId switch { 1 => "GameObject.active", 4 => "Transform", 224 => bd.Attribute >= 1 && bd.Attribute <= 4 ? "Transform" : "RectTransform", 114 => "script", _ => Shepherd.Assets.ClassIds.Name(bd.TypeId) + (bd.IsPPtr ? ".pptr" : "") };
                    mine[k] = mine.GetValueOrDefault(k) + bd.Count;
                    if (bd.TypeId is 1 or 4 or 224 or 114) readBack = true;
                }
            }
            if (ev) withEvents++;
            bool vis = !ev && !readBack && mine.Count > 0;
            if (vis) visual++;
            foreach (var (k, c) in mine) kinds[k] = kinds.GetValueOrDefault(k) + c;
            if (rows.Count < 40) rows.Add($"'{b.Go.Name}' ctrl '{r.CtrlObj?.name}': {r.Layers.Length} layers, clips [{string.Join(", ", Enumerable.Range(0, r.Layers.Length).Select(l => r.ClipOf(r.Current(l))?.name ?? "-"))}]{(ev ? " EVENTS" : "")}{(vis ? " VISUAL-ONLY" : "")}: {string.Join(", ", mine.Select(x => x.Key + " " + x.Value))}");
        }
        Log.Info($"[animcensus] f{frame}: {total} Animator records, {active} updated this frame; {visual} visual-only candidates, {withEvents} with events; channels by kind: {string.Join(", ", kinds.OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value))}");
        foreach (var x in rows) Log.Info("[animcensus]   " + x);
    }
}

#else
[ProfileOnly]
public static class AnimCensus { public static void After(int frame) { } }
[ProfileOnly]
public static class SpineCensus
{
    public static readonly bool On = false;
    public static void After(int frame) { }
}
#endif
