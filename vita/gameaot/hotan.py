#!/usr/bin/env python3
"""hotan.py — analysis of the game-aot hot-method profiles (vita/gameaot/hot.sh, tools/hotprof).

usage: hotan.py <out dir> <scenario> --frames N[,N...] --prof <run dir>... --profni <run dir>... [--plain <run dir>...]
                [--segnames a,b,c,d]

Inputs per run dir: hotprof.tsv (tools/hotprof), census.tsv (vita/v6/tools/interp-census), simstats(-frames).tsv.
  --prof    runs with the interpreter's inliner ON  (self times: median over the runs; calls the interpreter really makes)
  --profni  runs with --interp=-inline              (exact call counts and caller->callee edges; must agree with each other)
  --plain   unprofiled runs of the same scenario    (frame-time calibration)
Segments (hotprof's SHEPHERD_HOT_SEGMENTS) are analysed separately (frames per segment from --frames).

Writes into <out dir>/<scenario>[/<segment>]:
  summary.md            per-assembly table (methods executed, calls, self time, IL, whole-run census), coverage curve of the
                        not-yet-AOT time, AOT<->interpreter transitions per configuration (C0..C5 and profile-guided partial sets)
  assemblies.tsv        the per-assembly table as TSV
  methods/<asm>.tsv     every method executed in the window, by self time, with the .aotprofile key fields
  hot.tsv               all methods outside the engine-AOT set (C1) by self time, cumulative share (the partial-AOT candidate list)
  transitions-<cfg>.tsv every call site (caller -> callee method) that crosses AOT<->interpreter under that configuration
Model of the transitions (see profile.md §2): interp->AOT = calls in the inline-ON profile from an interpreted caller to an AOT
callee (the interpreter inlines small non-virtual callees whatever their execution mode, as transform.c does); AOT->interp =
calls in the inline-OFF profile from an AOT caller to an interpreted callee that the caller's AOT compile would not inline
(IL >= 20 bytes unless AggressiveInlining <= 256, virtual, NoInlining, wrapper, or a callee image outside the caller's inline
allowlist: BCL images inline only BCL, Shepherd.Engine only Shepherd.* + BCL, owner-local Unity/game images anything).
Calls into native code ((wrapper managed-to-native): icalls, P/Invokes) are not transitions; (wrapper dynamic-method)
(reflection InvokeStub_*) is always interpreted."""
import sys, os, re, json, statistics, collections, argparse

AOT_DEFAULT = {"System.Private.CoreLib", "System.Linq", "System.Collections", "System.Collections.Concurrent", "System.Console",
               "System.Memory", "System.ObjectModel", "System.Runtime.InteropServices", "System.Threading", "Shepherd.Assets",
               "Shepherd.Hooks", "Microsoft.Bcl.AsyncInterfaces"}     # vita/shepherd/build.sh AOT=(...) at ff30827
# the rewritten set laid over the game's Managed (runtime/ManagedVita, tools/Rewriter --vita): 58 assemblies
REWRITTEN = set("""FMODUnity GalaxyCSharp com.steamworks.net UnityEngine.AIModule UnityEngine.ARModule UnityEngine.AndroidJNIModule
UnityEngine.AnimationModule UnityEngine.AssetBundleModule UnityEngine.AudioModule UnityEngine.ClothModule UnityEngine.ClusterInputModule
UnityEngine.ClusterRendererModule UnityEngine.ContentLoadModule UnityEngine.CoreModule UnityEngine.CrashReportingModule
UnityEngine.DSPGraphModule UnityEngine.DirectorModule UnityEngine.GameCenterModule UnityEngine.GridModule UnityEngine.IMGUIModule
UnityEngine.ImageConversionModule UnityEngine.InputLegacyModule UnityEngine.InputModule UnityEngine.JSONSerializeModule
UnityEngine.LocalizationModule UnityEngine.ParticleSystemModule UnityEngine.PerformanceReportingModule UnityEngine.Physics2DModule
UnityEngine.PhysicsModule UnityEngine.ScreenCaptureModule UnityEngine.SpriteMaskModule UnityEngine.SpriteShapeModule
UnityEngine.StreamingModule UnityEngine.SubsystemsModule UnityEngine.TLSModule UnityEngine.TerrainModule UnityEngine.TerrainPhysicsModule
UnityEngine.TextCoreFontEngineModule UnityEngine.TextRenderingModule UnityEngine.TilemapModule UnityEngine.UIElementsModule
UnityEngine.UIModule UnityEngine.UnityAnalyticsCommonModule UnityEngine.UnityAnalyticsModule UnityEngine.UnityConnectModule
UnityEngine.UnityCurlModule UnityEngine.UnityWebRequestAssetBundleModule UnityEngine.UnityWebRequestAudioModule
UnityEngine.UnityWebRequestModule UnityEngine.UnityWebRequestTextureModule UnityEngine.UnityWebRequestWWWModule UnityEngine.VFXModule
UnityEngine.VRModule UnityEngine.VehiclesModule UnityEngine.VideoModule UnityEngine.VirtualTexturingModule UnityEngine.WindModule
UnityEngine.XRModule""".split())
GAME = {"Assembly-CSharp", "Assembly-CSharp-firstpass"}

def is_bcl(img): return img in ("mscorlib", "netstandard") or img.startswith("System") or img.startswith("Microsoft.")
def group(img):
    if img in AOT_DEFAULT: return "aot-default"
    if img == "Shepherd.Engine": return "engine"
    if img == "Shepherd.Engine.Derived": return "engine-derived"
    if img in REWRITTEN: return "unity-rewritten"
    if is_bcl(img): return "bcl-interp"
    if img.startswith("UnityEngine.UI") or img.startswith("Unity."): return "unity-pkg"
    if img.startswith("UnityEngine"): return "unity-original"
    if img in GAME: return "game"
    return "game-lib"
GROUP_ORDER = ["engine", "aot-default", "unity-rewritten", "unity-original", "unity-pkg", "game", "game-lib", "engine-derived", "bcl-interp", "reflection-stub"]
GROUP_DESC = {"engine": "Shepherd.Engine (AOT only in the opt-in engine-AOT build)", "aot-default": "AOT in the default VPK (BCL set + Shepherd.Assets/Hooks)",
              "unity-rewritten": "rewritten Unity modules (ManagedVita, owner-derived)", "unity-original": "unmodified UnityEngine assemblies (facade, SharedInternals)",
              "unity-pkg": "Unity packages shipped with the game (uGUI, TMP, InputSystem, Addressables...)", "game": "the game (Assembly-CSharp, -firstpass)",
              "game-lib": "third-party game libraries (Rewired, spine-unity, DOTween, Sirenix, ...)", "engine-derived": "Shepherd.Engine.Derived (owner-local port)",
              "bcl-interp": "BCL outside the Vita AOT set (interpreted today)", "reflection-stub": "(wrapper dynamic-method) InvokeStub_*: reflection invoke stubs, always interpreted"}
CONFIGS = [("C0", "default VPK: BCL set + Shepherd.Assets/Hooks AOT", {"aot-default"}),
           ("C1", "+ Shepherd.Engine (the opt-in engine-AOT VPK)", {"aot-default", "engine"}),
           ("C2", "+ the rewritten Unity modules (owner-local images)", {"aot-default", "engine", "unity-rewritten"}),
           ("C3", "+ unmodified Unity assemblies and Unity packages", {"aot-default", "engine", "unity-rewritten", "unity-original", "unity-pkg"}),
           ("C4", "+ the game and its libraries (+ Engine.Derived)", {"aot-default", "engine", "unity-rewritten", "unity-original", "unity-pkg", "game", "game-lib", "engine-derived"}),
           ("C5", "+ the rest of the BCL (everything that can be AOT)", set(GROUP_ORDER) - {"reflection-stub"})]
OWNER_GROUPS = {"unity-rewritten", "unity-original", "unity-pkg", "game", "game-lib", "engine-derived"}

def wrapper_kind(full):
    m = re.match(r"\(wrapper ([^)]*)\)", full)
    return m.group(1) if m else None

class Meth:
    __slots__ = ("id", "img", "ns", "cls", "name", "pc", "sig", "has_il", "il", "flags", "iflags", "inflated", "full", "wk", "grp")
    def __init__(s, x):
        s.id, s.img, s.ns, s.cls, s.name = x[1], x[2], x[3], x[4], x[5]
        s.pc, s.sig, s.has_il, s.il = int(x[6]), x[7], x[8] == "1", int(x[9])
        s.flags, s.iflags, s.inflated, s.full = int(x[10], 16), int(x[11], 16), x[12] == "1", x[13]
        s.wk = wrapper_kind(s.full)
        s.grp = "reflection-stub" if s.wk == "dynamic-method" else group(s.img)
    def key(s): return s.full   # stable across runs (ids are per run)

MERGE = False
def load(run):
    """-> meths {full: Meth}, stats {(seg, thread_kind, full): [calls, incl, excl]}, edges {(seg, thread_kind, a, b): [calls, incl]}, segs, threads"""
    meths, byid = {}, {}
    st = collections.defaultdict(lambda: [0, 0.0, 0.0]); ed = collections.defaultdict(lambda: [0, 0.0])
    segs, threads, main = {}, {}, set()
    thr = collections.defaultdict(float)   # (seg, thread name, group) -> excl ms
    for l in open(os.path.join(run, "hotprof.tsv"), errors="replace"):
        x = l.rstrip("\n").split("\t")
        if x[0] == "M":
            m = Meth(x); byid[m.id] = m; meths.setdefault(m.full, m)
        elif x[0] == "S":
            if MERGE and 0 in segs: segs[0] = (segs[0][0], segs[0][1] + float(x[3]))
            else: segs[0 if MERGE else int(x[1])] = (int(x[2]), float(x[3]))
        elif x[0] == "T":
            threads[x[1]] = (x[2] == "1", x[4], int(x[5]))
            if x[2] == "1": main.add(x[1])
    for l in open(os.path.join(run, "hotprof.tsv"), errors="replace"):
        x = l.rstrip("\n").split("\t")
        if x[0] == "m":
            if MERGE: x[2] = "0"
            k = (int(x[2]), "main" if x[1] in main else "other", byid[x[3]].full)
            s = st[k]; s[0] += int(x[4]); s[1] += float(x[5]); s[2] += float(x[6])
            thr[(int(x[2]), threads[x[1]][1] + (" (main)" if x[1] in main else ""), byid[x[3]].grp)] += float(x[6])
        elif x[0] == "e":
            if MERGE: x[2] = "0"
            k = (int(x[2]), "main" if x[1] in main else "other", byid[x[3]].full, byid[x[4]].full)
            e = ed[k]; e[0] += int(x[5]); e[1] += float(x[6])
    return meths, st, ed, segs, threads, thr

def census(run):
    """whole-run census (all threads): image -> [methods transformed by the interpreter, IR bytes (last tier)]"""
    c = collections.defaultdict(lambda: [0, 0]); p = os.path.join(run, "census.tsv")
    if not os.path.exists(p): return c
    for l in open(p, errors="replace"):
        x = l.rstrip("\n").split("\t")
        if len(x) >= 6 and x[0] == "interp": c[x[1]][0] += 1; c[x[1]][1] += int(x[3])
    return c

def plain_frames(run):
    rows = []
    p = os.path.join(run, "simstats-frames.tsv")
    for l in open(p):
        x = l.split("\t")
        if x[0].isdigit(): rows.append((int(x[0]), float(x[1])))
    return rows

def med(v): return statistics.median(v) if v else 0.0

def aot_inlinable(caller, callee):
    """would the caller's AOT compile inline this callee (so the call is not a transition)?"""
    if callee.wk or not callee.has_il: return False
    if callee.iflags & 0x8: return False                     # NoInlining
    if callee.iflags & 0x20: return False                    # Synchronized
    if (callee.flags & 0x40) and not (callee.flags & 0x20): return False   # virtual and not final: dispatched (approximation)
    agg = callee.iflags & 0x100
    if callee.il >= (256 if agg else 20): return False
    cg, kimg = caller.grp, callee.img
    if cg in OWNER_GROUPS: return True                        # owner-local image: may inline anything on its MONO_PATH
    if cg == "engine": return kimg.startswith("Shepherd.") or is_bcl(kimg)   # SHEPHERD_AOT_INLINE_IMAGES
    return is_bcl(kimg)                                       # BCL / Assets / Hooks images: compiled against the BCL only

def mode_of(m, aotgroups, aotmeths):
    if m.wk == "managed-to-native": return "native"
    if m.wk == "dynamic-method": return "interp"
    if m.wk: return "aot" if "aot-default" in aotgroups else "interp"   # other wrappers live in CoreLib's image
    if m.grp in aotgroups or (aotmeths is not None and m.full in aotmeths): return "aot"
    return "interp"

def transitions(meths, ed_on, ed_off, seg, frames, aotgroups, aotmeths=None, thread="main"):
    """-> (a2i total/frame, i2a total/frame, [(n/frame, dir, caller, callee)])"""
    sites = []
    for (s, tk, a, b), (n, _) in ed_off.items():
        if s != seg or tk != thread: continue
        ma, mb = meths[a], meths[b]
        if mode_of(ma, aotgroups, aotmeths) == "aot" and mode_of(mb, aotgroups, aotmeths) == "interp" and not aot_inlinable(ma, mb):
            sites.append((n / frames, "AOT->interp", a, b))
    for (s, tk, a, b), (n, _) in ed_on.items():
        if s != seg or tk != thread: continue
        ma, mb = meths[a], meths[b]
        if mode_of(ma, aotgroups, aotmeths) == "interp" and mode_of(mb, aotgroups, aotmeths) == "aot":
            sites.append((n / frames, "interp->AOT", a, b))
    a2i = sum(x[0] for x in sites if x[1] == "AOT->interp"); i2a = sum(x[0] for x in sites if x[1] == "interp->AOT")
    sites.sort(key=lambda t: -t[0])
    return a2i, i2a, sites

def hop_closure(meths, ed_off, seg, frames, aotgroups, base, min_per_frame=1.0):
    """base + every interpreted callee that AOT code (incl. the methods added) calls >= min_per_frame times per frame without
    inlining it: what a profile-guided partial AOT must add so that the AOT code does not hop back into the interpreter"""
    s = set(base)
    for _ in range(12):
        add = set()
        for (sg, tk, a, b), (n, _) in ed_off.items():
            if sg != seg or tk != "main" or n / frames < min_per_frame or b in s: continue
            ma, mb = meths[a], meths[b]
            if mb.wk or mb.grp == "reflection-stub": continue
            if mode_of(ma, aotgroups, s) == "aot" and mode_of(mb, aotgroups, s) == "interp" and not aot_inlinable(ma, mb): add.add(b)
        if not add: break
        s |= add
    return s

def short(full, n=100):
    s = re.sub(r" \(.*", "", full)
    return s if len(s) <= n else s[: n - 1] + "…"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out"); ap.add_argument("scenario")
    ap.add_argument("--frames", required=True); ap.add_argument("--segnames", default="")
    ap.add_argument("--prof", nargs="+", required=True); ap.add_argument("--profni", nargs="+", required=True)
    ap.add_argument("--plain", nargs="*", default=[])
    ap.add_argument("--merge", action="store_true", help="treat all segments as one window")
    ap.add_argument("--asmsize", help="AsmSize sizes TSV (vita/v6/tools/AsmSize): whole-assembly IL bytes and method counts")
    a = ap.parse_args()
    global MERGE; MERGE = a.merge
    asz = {}
    if a.asmsize:
        for l in open(a.asmsize):
            x = l.rstrip("\n").split("\t")
            if len(x) > 6 and x[0] != "assembly": asz[x[0]] = (int(x[4]), int(x[5]))
    frames = [int(x) for x in a.frames.split(",")]
    segnames = a.segnames.split(",") if a.segnames else [""]
    P = [load(r) for r in a.prof]; N = [load(r) for r in a.profni]
    meths = {}
    for r in P + N:
        for k, m in r[0].items(): meths.setdefault(k, m)
    # exact counts must agree between the inline-off runs (main thread)
    agree = []
    if len(N) > 1:
        k0 = {k: v[0] for k, v in N[0][2].items() if k[1] == "main"}
        for r in N[1:]:
            k1 = {k: v[0] for k, v in r[2].items() if k[1] == "main"}
            diff = sum(1 for k in set(k0) | set(k1) if k0.get(k, 0) != k1.get(k, 0))
            agree.append((len(set(k0) | set(k1)), diff))
    cen = census(a.prof[0])
    for si, segname in enumerate(segnames):
        F = frames[si]
        od = os.path.join(a.out, a.scenario, segname) if segname else os.path.join(a.out, a.scenario)
        os.makedirs(os.path.join(od, "methods"), exist_ok=True)
        # per method: exact calls (profni run 0), non-inlined calls (prof run 0), self/incl times (prof median)
        exact = collections.defaultdict(lambda: [0, 0])     # full -> [main, other]
        for (s, tk, f), v in N[0][1].items():
            if s == si: exact[f][0 if tk == "main" else 1] += v[0]
        nonin = collections.defaultdict(int)
        for (s, tk, f), v in P[0][1].items():
            if s == si and tk == "main": nonin[f] += v[0]
        selfs = collections.defaultdict(list); incls = collections.defaultdict(list); oselfs = collections.defaultdict(list)
        names = set(exact) | set(nonin)
        walls = []
        for r in P:
            em, im, eo = collections.defaultdict(float), collections.defaultdict(float), collections.defaultdict(float)
            for (s, tk, f), v in r[1].items():
                if s != si: continue
                names.add(f)
                if tk == "main": em[f] += v[2]; im[f] += v[1]
                else: eo[f] += v[2]
            for f in names:
                selfs[f].append(em.get(f, 0.0)); incls[f].append(im.get(f, 0.0)); oselfs[f].append(eo.get(f, 0.0))
            walls.append(r[3].get(si, (0, 0.0))[1])
        for f in names:   # methods first seen in a later run: pad the earlier rounds with 0
            for d in (selfs, incls, oselfs):
                d[f] = [0.0] * (len(P) - len(d[f])) + d[f]
        prof_frame = med(walls) / F if walls else 0.0
        # calibration from the plain runs: the same frames' mean wall time
        seg_lo = P[0][3].get(si, (0, 0))[0]
        seg_hi = P[0][3].get(si + 1, (10 ** 9, 0))[0] - 1 if si + 1 in P[0][3] else None
        plain = []
        for r in a.plain:
            rows = plain_frames(r)
            if not rows: continue
            if seg_lo == 0 or MERGE:   # relative window (m+N) or merged segments: the whole simstats window
                sel = rows
                txt = open(os.path.join(r, "simstats.txt")).read()
                m = re.search(r"window frames (\d+)\.\.(\d+)", txt)
                if m: sel = [x for x in rows if int(m.group(1)) <= x[0] <= int(m.group(2))]
            else:
                sel = [x for x in rows if x[0] >= seg_lo and (seg_hi is None or x[0] <= seg_hi)]
            if sel: plain.append(sum(x[1] for x in sel) / len(sel))
        plain_frame = med(plain) if plain else None
        scale = plain_frame / prof_frame if plain_frame and prof_frame else None
        # per assembly
        A = collections.defaultdict(lambda: {"methods": 0, "methods_main": 0, "calls": 0.0, "ocalls": 0.0, "self": [0.0] * len(P), "oself": [0.0] * len(P), "il": 0, "il_n": 0})
        for f in names:
            m = meths[f]; d = A[m.img]
            c = exact.get(f, [0, 0])
            if c[0] + c[1] == 0 and max(selfs[f]) == 0 and max(oselfs[f]) == 0 and nonin.get(f, 0) == 0: continue
            d["methods"] += 1; d["methods_main"] += 1 if (c[0] or max(selfs[f]) > 0) else 0
            d["calls"] += c[0] / F; d["ocalls"] += c[1] / F
            for i in range(len(P)): d["self"][i] += selfs[f][i] / F; d["oself"][i] += oselfs[f][i] / F
            if m.has_il: d["il"] += m.il; d["il_n"] += 1
        tot_main = med([sum(d["self"][i] for d in A.values()) for i in range(len(P))])
        rows = []
        for img, d in A.items():
            sm = med(d["self"]); rows.append((group(img), img, d, sm, min(d["self"]), max(d["self"]), med(d["oself"])))
        rows.sort(key=lambda t: (GROUP_ORDER.index(t[0]), -t[3]))
        with open(os.path.join(od, "assemblies.tsv"), "w") as w:
            w.write("group\tassembly\tmethods_window\tmethods_main\tcalls_per_frame_main\tcalls_per_frame_other\tself_ms_main_median\tmin\tmax\tself_ms_other_median\tshare_main\til_bytes_executed\tcensus_methods_run\tcensus_ir_bytes_run\tasm_methods\tasm_il_bytes\n")
            for g, img, d, sm, lo, hi, so in rows:
                w.write(f"{g}\t{img}\t{d['methods']}\t{d['methods_main']}\t{d['calls']:.2f}\t{d['ocalls']:.2f}\t{sm:.4f}\t{lo:.4f}\t{hi:.4f}\t{so:.4f}\t{(sm / tot_main if tot_main else 0):.4f}\t{d['il']}\t{cen.get(img, [0, 0])[0]}\t{cen.get(img, [0, 0])[1]}\t{asz.get(img, ('', ''))[1]}\t{asz.get(img, ('', ''))[0]}\n")
        # method lists per assembly
        byimg = collections.defaultdict(list)
        for f in names:
            m = meths[f]; c = exact.get(f, [0, 0])
            if c[0] + c[1] == 0 and max(selfs[f]) == 0 and max(oselfs[f]) == 0 and nonin.get(f, 0) == 0: continue
            byimg[m.img].append((med(selfs[f]) / F, f))
        hdr = "self_ms_per_frame_main_median\tmin\tmax\tincl_ms_per_frame_main_median\tself_ms_per_frame_other_median\tcalls_per_frame_main_exact\tcalls_per_frame_other_exact\tcalls_per_frame_main_not_inlined_by_interp\til_bytes\tflags\tiflags\tinflated\twrapper\timage\tnamespace\tclass\tmethod\tparam_count\tsignature\tfull_name\n"
        for img, lst in byimg.items():
            lst.sort(key=lambda t: (-t[0], t[1]))
            fn = re.sub(r"[^A-Za-z0-9._-]", "_", img)
            with open(os.path.join(od, "methods", fn + ".tsv"), "w") as w:
                w.write(hdr)
                for _, f in lst:
                    m = meths[f]; c = exact.get(f, [0, 0])
                    w.write(f"{med(selfs[f]) / F:.5f}\t{min(selfs[f]) / F:.5f}\t{max(selfs[f]) / F:.5f}\t{med(incls[f]) / F:.5f}\t{med(oselfs[f]) / F:.5f}\t"
                            f"{c[0] / F:.3f}\t{c[1] / F:.3f}\t{nonin.get(f, 0) / F:.3f}\t{m.il if m.has_il else ''}\t0x{m.flags:x}\t0x{m.iflags:x}\t{int(m.inflated)}\t{m.wk or ''}\t"
                            f"{m.img}\t{m.ns}\t{m.cls}\t{m.name}\t{m.pc}\t{m.sig}\t{m.full}\n")
        # hot list outside C1 (main + other threads' self time) and its coverage curve
        c1 = CONFIGS[1][2]
        hot = []
        for f in names:
            m = meths[f]
            if m.grp in c1 or m.wk: continue   # wrappers: native transitions, reflection stubs (never AOT), runtime-generated
            t = (med(selfs[f]) + med(oselfs[f])) / F
            if t > 0 or exact.get(f, [0, 0])[0]: hot.append((t, f))
        hot.sort(key=lambda t: (-t[0], t[1]))
        tot_hot = sum(t for t, _ in hot)
        cov = []
        with open(os.path.join(od, "hot.tsv"), "w") as w:
            w.write("rank\tself_ms_per_frame_all_threads\tcum_share\tgroup\timage\til_bytes\tcalls_per_frame_main_exact\tinflated\tfull_name\n")
            cum = 0.0; il = 0; marks = [0.5, 0.8, 0.9, 0.95, 0.99, 1.0]; mi = 0
            for i, (t, f) in enumerate(hot, 1):
                cum += t; m = meths[f]; il += m.il if m.has_il else 0
                w.write(f"{i}\t{t:.5f}\t{(cum / tot_hot if tot_hot else 0):.4f}\t{m.grp}\t{m.img}\t{m.il if m.has_il else ''}\t{exact.get(f, [0, 0])[0] / F:.3f}\t{int(m.inflated)}\t{f}\n")
                while mi < len(marks) and tot_hot and cum / tot_hot >= marks[mi] - 1e-9:
                    cov.append((marks[mi], i, il, set(g for _, g in hot[:i]))); mi += 1
        # transitions per configuration (main thread; other threads listed in totals)
        ed_on, ed_off = P[0][2], N[0][2]
        T = []
        for name, desc, groups in CONFIGS:
            a2i, i2a, sites = transitions(meths, ed_on, ed_off, si, F, groups)
            oa2i, oi2a, _ = transitions(meths, ed_on, ed_off, si, F, groups, thread="other")
            T.append((name, desc, a2i, i2a, oa2i, oi2a, sites))
            fn = os.path.join(od, f"transitions-{name}.tsv")
            with open(fn, "w") as w:
                w.write("calls_per_frame\tdirection\tcaller_image\tcaller\tcallee_image\tcallee\tcallee_il\n")
                for n, d, x, y in sites:
                    w.write(f"{n:.3f}\t{d}\t{meths[x].img}\t{x}\t{meths[y].img}\t{y}\t{meths[y].il if meths[y].has_il else ''}\n")
        PT = []
        for frac, n, il, mset in cov:
            if frac in (0.8, 0.9, 0.95, 0.99):
                a2i, i2a, sites = transitions(meths, ed_on, ed_off, si, F, c1, mset)
                PT.append((f"P{int(frac * 100)}", f"C1 + the {n} hottest methods outside C1 ({int(frac * 100)} % of that time, {il / 1024:.0f} KiB IL)", a2i, i2a, sites, mset))
        for frac, n, il, mset in cov:
            if frac in (0.9, 0.95, 0.99):
                hs = hop_closure(meths, ed_off, si, F, c1, mset)
                hil = sum(meths[f].il for f in hs if meths[f].has_il)
                a2i, i2a, sites = transitions(meths, ed_on, ed_off, si, F, c1, hs)
                PT.append((f"PH{int(frac * 100)}", f"P{int(frac * 100)} + the interpreted callees its AOT code calls >= 1/frame (closure): {len(hs)} methods, {hil / 1024:.0f} KiB IL", a2i, i2a, sites, hs))
        hs = hop_closure(meths, ed_off, si, F, c1, set())
        a2i, i2a, sites = transitions(meths, ed_on, ed_off, si, F, c1, hs)
        PT.append(("PH0", f"C1 + only the interpreted callees engine AOT code calls >= 1/frame (closure): {len(hs)} methods, {sum(meths[f].il for f in hs if meths[f].has_il) / 1024:.0f} KiB IL", a2i, i2a, sites, hs))
        for name, desc, a2i, i2a, sites, mset in PT:
            with open(os.path.join(od, f"set-{name}.txt"), "w") as w:
                for f in sorted(mset): w.write(f + "\n")
        # summary.md
        with open(os.path.join(od, "summary.md"), "w") as w:
            title = f"{a.scenario}" + (f" / segment {segname}" if segname else "")
            w.write(f"# {title}: method-level profile (interpreter, Vita profile, null renderer)\n\n")
            w.write(f"Window {F} frames (main-thread profile wall, median of {len(P)} inline-on runs: {med(walls):.1f} ms = {prof_frame:.3f} ms/frame inflated)")
            if plain_frame: w.write(f"; unprofiled median of {len(plain)} plain runs {plain_frame:.3f} ms/frame (scale {scale:.3f})")
            w.write(".\n")
            if agree: w.write(f"Exact counts: {len(N)} inline-off runs, main-thread (seg, caller, callee) edges differing: " + ", ".join(f"{d} of {n}" for n, d in agree) + ".\n")
            thr = P[0][4]
            w.write("Threads seen in the window (run 1): " + "; ".join(f"{v[1]}{' (main)' if v[0] else ''} {v[2]:,} enters" for v in thr.values()) + ".\n\n")
            tt = collections.defaultdict(lambda: collections.Counter())
            for (sg, tn, g), v in P[0][5].items():
                if sg == si: tt[tn][g] += v / F
            w.write("Self time per thread and group, run 1 (inflated ms/frame; another thread's time is not on the main thread's frame unless the main thread waits for it):\n\n| thread | total | by group |\n|---|---:|---|\n")
            for tn, c in sorted(tt.items(), key=lambda t: -sum(t[1].values())):
                tot = sum(c.values())
                if tot < 0.001: continue
                w.write(f"| {tn} | {tot:.3f} | " + ", ".join(f"{g} {v:.3f}" for g, v in c.most_common(6) if v >= 0.0005) + " |\n")
            w.write("\n")
            w.write("## Per assembly (main thread unless noted; ms are the profiler's inflated self time per frame, median [min..max] over the inline-on runs; share = of the main thread's self time)\n\n")
            w.write("| group | assembly | methods (window) | calls/frame (exact) | self ms/frame | share | est. real ms/frame | other threads: calls/frame, self ms | IL KiB executed (window) | whole run: methods executed / interp IR KiB | assembly: methods / IL KiB |\n|---|---|---:|---:|---:|---:|---:|---|---:|---|---|\n")
            gt = collections.defaultdict(lambda: [0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0])
            for g, img, d, sm, lo, hi, so in rows:
                cm = cen.get(img, [0, 0])
                gtv = gt[g]; gtv[0] += d["methods"]; gtv[1] += d["calls"]; gtv[2] += sm; gtv[3] += d["ocalls"]; gtv[4] += so; gtv[5] += d["il"]; gtv[6] += cm[0]; gtv[7] += cm[1]
                if sm / (tot_main or 1) < 0.0005 and d["calls"] < 1 and so < 0.0005: continue
                az = asz.get(img)
                w.write(f"| {g} | {img} | {d['methods']} | {d['calls']:,.0f} | {sm:.3f} [{lo:.3f}..{hi:.3f}] | {100 * sm / (tot_main or 1):.1f} % | {(sm * scale if scale else float('nan')):.3f} | {d['ocalls']:,.0f}, {so:.3f} | {d['il'] / 1024:.1f} | {cm[0]} / {cm[1] / 1024:.0f} | " + (f"{az[1]:,} / {az[0] / 1024:,.0f}" if az else "") + " |\n")
            w.write("\n(rows below 0.05 % of the main thread's self time and 1 call/frame are omitted here; assemblies.tsv has all)\n\n### Per group\n\n")
            w.write("| group | what | methods (window) | calls/frame | self ms/frame (main) | share | est. real ms/frame | other threads self ms | IL KiB executed (window) | whole run: methods executed / interp IR KiB | all methods / IL KiB of the assemblies (the intro run's 143 loaded) |\n|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|\n")
            gsz = collections.defaultdict(lambda: [0, 0])
            for img, (il_, n_) in asz.items(): gsz[group(img)][0] += il_; gsz[group(img)][1] += n_
            for g in GROUP_ORDER:
                if g not in gt: continue
                v = gt[g]
                w.write(f"| {g} | {GROUP_DESC[g]} | {v[0]} | {v[1]:,.0f} | {v[2]:.3f} | {100 * v[2] / (tot_main or 1):.1f} % | {(v[2] * scale if scale else float('nan')):.3f} | {v[4]:.3f} | {v[5] / 1024:.1f} | {v[6]} / {v[7] / 1024:.0f} | " + (f"{gsz[g][1]:,} / {gsz[g][0] / 1024:,.0f}" if g in gsz else "") + " |\n")
            w.write(f"\n## Coverage of the time outside the engine-AOT set (C1), all threads: {tot_hot:.3f} ms/frame inflated, {len(hot)} methods\n\n")
            w.write("| share of that time | methods | IL KiB of those methods | assemblies they are in |\n|---:|---:|---:|---|\n")
            for frac, n, il, mset in cov:
                imgs = collections.Counter(meths[f].img for f in mset)
                w.write(f"| {int(frac * 100)} % | {n} | {il / 1024:.1f} | " + ", ".join(f"{k} {v}" for k, v in imgs.most_common(6)) + (" …" if len(imgs) > 6 else "") + " |\n")
            w.write("\nTop 40 (hot.tsv has all):\n\n| # | self ms/frame | cum | image | IL | calls/frame | method |\n|---:|---:|---:|---|---:|---:|---|\n")
            cum = 0.0
            for i, (t, f) in enumerate(hot[:40], 1):
                cum += t; m = meths[f]
                w.write(f"| {i} | {t:.4f} | {100 * cum / (tot_hot or 1):.1f} % | {m.img} | {m.il if m.has_il else ''} | {exact.get(f, [0, 0])[0] / F:,.1f} | {short(f, 110)} |\n")
            w.write("\n## AOT<->interpreter transitions per configuration (calls per frame; main thread, other threads in brackets)\n\n")
            w.write("| config | AOT set | AOT->interp | interp->AOT | total |\n|---|---|---:|---:|---:|\n")
            for name, desc, a2i, i2a, oa2i, oi2a, _ in T:
                w.write(f"| {name} | {desc} | {a2i:,.0f} [{oa2i:,.0f}] | {i2a:,.0f} [{oi2a:,.0f}] | {a2i + i2a:,.0f} |\n")
            for name, desc, a2i, i2a, _, _ in PT:
                w.write(f"| {name} | {desc} | {a2i:,.0f} | {i2a:,.0f} | {a2i + i2a:,.0f} |\n")
            w.write("\n(partial sets: set-<name>.txt; the closure adds callees until no AOT code calls an interpreted method >= 1/frame without inlining it)\n")
            for name, desc, a2i, i2a, oa2i, oi2a, sites in T + [(n_, "", x, y, 0, 0, s_) for n_, d_, x, y, s_, m_ in PT if n_ in ("P95", "PH95")]:
                w.write(f"\n### {name}: top call sites (transitions-{name}.tsv has all)\n\n" if name.startswith("C") else f"\n### {name}: top call sites\n\n")
                w.write("| calls/frame | direction | caller | callee | callee IL |\n|---:|---|---|---|---:|\n")
                for n, d, x, y in sites[:15]:
                    w.write(f"| {n:,.1f} | {d} | {meths[x].img}: {short(x, 70)} | {meths[y].img}: {short(y, 70)} | {meths[y].il if meths[y].has_il else ''} |\n")
                # pair matrix
                pm = collections.Counter()
                for n, d, x, y in sites: pm[(d, meths[x].grp, meths[y].grp)] += n
                w.write("\nby group pair: " + "; ".join(f"{d} {ga}->{gb} {n:,.0f}" for (d, ga, gb), n in pm.most_common(8)) + "\n")
        # key numbers for the cross-scenario tables (hotsum.py)
        gk = collections.defaultdict(lambda: [0.0, 0.0, 0.0, 0])
        for g, img, d, sm, lo, hi, so in rows:
            gk[g][0] += sm; gk[g][1] += so; gk[g][2] += d["calls"]; gk[g][3] += d["methods"]
        key = {"scenario": a.scenario, "segment": segname, "frames": F, "prof_runs": len(P), "profni_runs": len(N), "plain_runs": len(plain),
               "prof_ms_per_frame": prof_frame, "plain_ms_per_frame": [med(plain), min(plain), max(plain)] if plain else None,
               "main_self_total": tot_main, "groups": {g: {"self_main": v[0], "self_other": v[1], "calls": v[2], "methods": v[3]} for g, v in gk.items()},
               "outside_c1_ms": tot_hot, "outside_c1_methods": len(hot),
               "coverage": [{"frac": fr, "methods": n, "il": il} for fr, n, il, _ in cov],
               "configs": {t[0]: {"a2i": t[2], "i2a": t[3], "other_a2i": t[4], "other_i2a": t[5]} for t in T},
               "partial": {t[0]: {"desc": t[1], "a2i": t[2], "i2a": t[3], "methods": len(t[5]), "il": sum(meths[f].il for f in t[5] if meths[f].has_il)} for t in PT},
               "agree": agree,
               "threads": {f"{v[1]}{' (main)' if v[0] else ''}": v[2] for v in P[0][4].values()}}
        json.dump(key, open(os.path.join(od, "key.json"), "w"), indent=1)
        print(f"{od}: {len(names)} methods, main self {tot_main:.3f} ms/frame (inflated), outside C1 {tot_hot:.3f}; " +
              ", ".join(f"{t[0]} {t[2] + t[3]:,.0f}" for t in T) + ", " + ", ".join(f"{t[0]} {t[2] + t[3]:,.0f}" for t in PT) + " transitions/frame")

if __name__ == "__main__":
    main()
