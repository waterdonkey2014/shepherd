#!/usr/bin/env python3
"""Kit #7 JIT stage 0 (vita/notes/kit7-jit.md): analysis of the shepjit census (census/shepherd_jit_census.c) of one bench.sh run.

  jitcensus.py <run dir> [--a32 1.4] [--a32-lo 1.23] [--a32-hi 1.6] [--json out.json] [--md] [--include-post]

What it reports (arm64 = MEASURED-M on the Mac's stock Mono JIT; A32 = DERIVED with the stated code-size ratio):
  - code memory as the code managers hold it: every recorded allocation (methods, trampolines, thunks) placed in its chunk by address;
    footprint of an allocation = distance to the next allocation in the same chunk (Mono's code managers bump-allocate, so the gap is its
    code + the in-method thunk area + unwind info + alignment); the last allocation in a chunk counts its own size rounded to 32 B;
    chunk tails (reserved, never used) reported apart;
  - by kind (JIT method / trampoline types / AOT / interp), by image group (BCL-AOT set, other BCL, engine, rewritten Unity,
    game + third party, owner-local derived), wrappers and generic instances;
  - growth over the session: cumulative footprint at each scene arrival and every 1,000 frames (time -> frame through
    simstats-frames.tsv + simstats.txt's startup line), and what the last part of the run added;
  - code compiled AFTER the run's last frame (the engine's shutdown and its run reports: Boot.Finish, FrameTimes / SimStats / NativeCensus
    summaries, the Linq they use) is not session code: reported on its own line and left out of every total (kit #7 review fix; it had
    been counted in the totals and shown as the last growth step); --include-post puts it back;
  - the A32 projection against the Vita's 16 MiB ForVM block: method code x ratio + thunk areas x 12/16 (ARM32 THUNK_SIZE 12 vs arm64 16)
    + trampolines x ratio, plus the J1 hybrid view (only what normal AOT images of the BCL set / + the engine would not cover).
Image groups use the jitstudy gain lane's split (vita/llvm/macbench.sh SET = the Vita's BCL-AOT set)."""
import argparse, bisect, collections, json, os, re, statistics as st, sys

BCL_AOT = {"System.Private.CoreLib", "System.Linq", "System.Collections", "System.Collections.Concurrent", "System.Console",
           "System.Memory", "System.ObjectModel", "System.Runtime.InteropServices", "System.Threading", "Shepherd.Assets",
           "Shepherd.Hooks", "Microsoft.Bcl.AsyncInterfaces"}
ENGINE = {"Shepherd.Engine"}
DERIVED = {"Shepherd.Engine.Derived"}
REWRITTEN = {"FMODUnity", "GalaxyCSharp", "com.steamworks.net"}   # + every UnityEngine.*Module the Vita set rewrites (see group())
BUF_TYPES = {0: "method", 1: "method-trampoline", 2: "unbox-trampoline", 3: "imt-trampoline", 4: "generics-trampoline",
             5: "specific-trampoline", 6: "helper", 7: "monitor", 8: "delegate-invoke", 9: "exception-handling"}
MiB = 1048576.0
FORVM = 16 * MiB

def group(img, managed_dir=None):
    if img in BCL_AOT: return "bcl-aot-set"
    if img in ENGINE: return "engine"
    if img in DERIVED: return "derived"
    if img in REWRITTEN or (img.startswith("UnityEngine.") and img.endswith("Module") and img not in ("UnityEngine.SharedInternalsModule", "UnityEngine.TextCoreTextEngineModule")):
        return "rewritten-unity"
    if img.startswith("System.") or img in ("mscorlib", "netstandard", "Microsoft.Win32.Primitives", "Microsoft.VisualBasic.Core") or img.startswith("Microsoft.CSharp"):
        return "other-bcl"
    if img == "?": return "unknown"
    return "game-third-party"

def load(d):
    meths, bufs, chunks = [], [], []
    hdr = ""
    with open(os.path.join(d, "census.methods.tsv")) as f:
        hdr = f.readline()
        f.readline()
        for l in f:
            x = l.rstrip("\n").split("\t", 9)
            if len(x) < 10: continue
            meths.append(dict(t=int(x[0]), start=int(x[1], 16), size=int(x[2]), kind=x[3], thunk=int(x[4]), freed=int(x[7]), image=x[8], name=x[9]))
    with open(os.path.join(d, "census.buffers.tsv")) as f:
        f.readline()
        for l in f:
            x = l.split("\t")
            if len(x) >= 4: bufs.append(dict(t=int(x[0]), start=int(x[1], 16), size=int(x[2]), type=int(x[3])))
    with open(os.path.join(d, "census.chunks.tsv")) as f:
        f.readline()
        for l in f:
            x = l.split("\t")
            if len(x) >= 4: chunks.append(dict(t=int(x[0]), start=int(x[1], 16), size=int(x[2]), gone=int(x[3])))
    m = re.search(r"t0_unix_ms (\d+)", hdr)
    return hdr, meths, bufs, chunks, int(m.group(1)) if m else None

def frame_clock(d, t0_unix_ms):
    """t_us (since the profiler's load) -> frame number, from simstats.txt's startup line and simstats-frames.tsv."""
    try:
        s = open(os.path.join(d, "simstats.txt")).read()
        m = re.search(r"Main entered \S+ \(unix ms (\d+)\).*?frame 1 done \+(\d+) ms", s)
        rows = [l.split("\t") for l in list(open(os.path.join(d, "simstats-frames.tsv")))[1:] if l.strip()]
    except OSError:
        return None, None
    if not m or t0_unix_ms is None or not rows: return None, None
    main_ms, f1 = int(m.group(1)), int(m.group(2))
    ends, fr = [], []
    acc = (main_ms - t0_unix_ms) + f1     # ms since the profiler's t0 at the end of frame 1
    first = int(rows[0][0])
    for r in rows:
        f, ms = int(r[0]), float(r[1])
        if f == first: e = acc if f == 1 else acc
        else: acc += ms; e = acc
        ends.append(e * 1000.0); fr.append(f)
    def to_frame(t_us):
        i = bisect.bisect_left(ends, t_us)
        return fr[i] if i < len(fr) else fr[-1] + 1
    return to_frame, (ends, fr)

def scenes(d):
    out = []
    try:
        for l in open(os.path.join(d, "engine.log"), errors="replace"):
            m = re.match(r"\[ *(\d+)\]\s+scene '([^']+)': (\d+) GameObjects", l)
            if m: out.append((int(m.group(1)), m.group(2)))
    except OSError:
        pass
    return out

def analyse(d, a32=1.4, a32_lo=1.23, a32_hi=1.6, include_post=False):
    hdr, meths, bufs, chunks, t0 = load(d)
    to_frame, clock = frame_clock(d, t0)
    last_run_frame = clock[1][-1] if clock else None
    # every allocation: methods (from jit_done; AOT / interp records are not in code memory the JIT owns) + non-method buffers
    allocs = []
    for m in meths:
        if m["kind"] == "jit": allocs.append((m["start"], m["size"], "method", m))
    for b in bufs:
        if b["type"] != 0: allocs.append((b["start"], b["size"], BUF_TYPES.get(b["type"], f"type{b['type']}"), b))
    allocs.sort(key=lambda a: a[0])
    cs = sorted(chunks, key=lambda c: c["start"])
    cstarts = [c["start"] for c in cs]
    def chunk_of(addr):
        i = bisect.bisect_right(cstarts, addr) - 1
        if i >= 0 and cs[i]["start"] <= addr < cs[i]["start"] + cs[i]["size"]: return i
        return None
    per_chunk = collections.defaultdict(list)
    outside = []
    for a in allocs:
        i = chunk_of(a[0])
        (per_chunk[i].append(a) if i is not None else outside.append(a))
    foot = {}            # id(record) -> footprint bytes
    tails = 0; used_total = 0
    for i, lst in per_chunk.items():
        c = cs[i]
        lst.sort(key=lambda a: a[0])
        for k, a in enumerate(lst):
            if k + 1 < len(lst): fp = lst[k + 1][0] - a[0]
            else: fp = (a[1] + 31) // 32 * 32
            foot[id(a[3])] = max(fp, a[1])
        end = max(a[0] + foot[id(a[3])] for a in lst)
        used_total += end - c["start"]
        tails += c["start"] + c["size"] - end
    for a in outside: foot[id(a[3])] = (a[1] + 31) // 32 * 32
    # allocations made after the run's last frame (shutdown + run reports): footprints above use every allocation (a gap is a gap), but
    # the totals below count only the session's own code unless --include-post
    post = [a for a in allocs if to_frame and to_frame(a[3]["t"]) > last_run_frame]
    post_ids = set(id(a[3]) for a in post)
    post_stats = dict(methods=sum(1 for a in post if a[2] == "method"), method_code=sum(a[1] for a in post if a[2] == "method"),
                      fp=sum(foot[id(a[3])] for a in post), tramp=sum(1 for a in post if a[2] != "method"),
                      tramp_fp=sum(foot[id(a[3])] for a in post if a[2] != "method"), last_frame=last_run_frame, included=include_post)
    allocs_all = allocs
    if not include_post: allocs = [a for a in allocs if id(a[3]) not in post_ids]
    first_off = sum(lst[0][0] - cs[i]["start"] for i, lst in per_chunk.items() if lst)
    reserved = sum(c["size"] for c in chunks)
    reserved_live = sum(c["size"] for c in chunks if not c["gone"])
    # by kind / group
    kinds = collections.Counter(); kinds_n = collections.Counter(); kinds_code = collections.Counter()
    groups = collections.Counter(); groups_n = collections.Counter(); groups_code = collections.Counter()
    wrap = collections.Counter(); gen = collections.Counter(); thunk_bytes = 0
    for a in allocs:
        fp = foot[id(a[3])]
        kinds[a[2]] += fp; kinds_n[a[2]] += 1; kinds_code[a[2]] += a[1]
        if a[2] == "method":
            m = a[3]; g = group(m["image"])
            groups[g] += fp; groups_n[g] += 1; groups_code[g] += m["size"]
            if m["name"].startswith("(wrapper"): wrap["n"] += 1; wrap["fp"] += fp
            if re.search(r"<[^>]*>", m["name"].split("(")[0] if not m["name"].startswith("(wrapper") else ""): gen["n"] += 1; gen["fp"] += fp
            pad = (m["size"] + 31) // 32 * 32
            if m["thunk"] and fp > pad: thunk_bytes += fp - pad
    meth_code = kinds_code["method"]; meth_fp = kinds["method"]
    tramp_fp = sum(v for k, v in kinds.items() if k != "method")
    # A32: method code x ratio, thunk areas x 12/16, alignment as measured, trampolines x ratio
    align_fp = meth_fp - meth_code - thunk_bytes
    def a32_of(code, thunks, align, tr, r):
        return code * r + thunks * 12 / 16 + align + tr * r
    proj = {k: a32_of(meth_code, thunk_bytes, align_fp, tramp_fp, r) for k, r in (("lo", a32_lo), ("mid", a32), ("hi", a32_hi))}
    # J1 views: normal AOT images of the BCL set (shareable) / + the engine would hold those methods' code in the eboot. Generic instances
    # over game types and wrappers are still JIT'd under J1 (DERIVED, as the gain lane's hybrid bound): counted as JIT here.
    def j1(excl):
        code = fp = th = 0
        for a in allocs:
            if a[2] != "method": continue
            m = a[3]; g = group(m["image"])
            if g in excl and not m["name"].startswith("(wrapper") and not re.search(r"<[^>]*>", m["name"].split("(")[0]): continue
            f = foot[id(m)]; code += m["size"]; fp += f
            pad = (m["size"] + 31) // 32 * 32
            if m["thunk"] and f > pad: th += f - pad
        return code, fp, th
    j1s = {}
    for nm, ex in (("J1 (BCL set AOT)", {"bcl-aot-set"}), ("J1E (+ engine AOT)", {"bcl-aot-set", "engine"})):
        code, fp, th = j1(ex)
        al = fp - code - th
        j1s[nm] = dict(code=code, fp=fp, a32_mid=a32_of(code, th, al, tramp_fp, a32), a32_hi=a32_of(code, th, al, tramp_fp, a32_hi))
    # growth: cumulative footprint at frames (the post-run allocations, when included, form their own last step)
    growth = []
    sc = scenes(d)
    if to_frame:
        ev = sorted(((a[3]["t"], foot[id(a[3])], a[2]) for a in allocs), key=lambda e: e[0])
        fr_of = [to_frame(e[0]) for e in ev]
        last_frame = max(fr_of) if fr_of else 0
        marks = sorted(set([f for f, _ in sc] + [m for m in range(1000, last_frame + 1000, 1000) if m <= last_run_frame] + [last_run_frame]))
        if include_post and post: marks.append(last_run_frame + 1)
        cum = 0; j = 0; n = 0
        for mk in marks:
            while j < len(ev) and fr_of[j] <= mk:
                cum += ev[j][1]; n += ev[j][2] == "method"; j += 1
            name = ",".join(s for f, s in sc if f == mk)
            if mk == last_run_frame: name = (name + " " if name else "") + "(the run's last frame)"
            if mk == last_run_frame + 1: name = "after the last frame (shutdown + run reports)"
            growth.append(dict(frame=mk, methods=n, fp=cum, scene=name))
    res = dict(run=os.path.basename(d.rstrip("/")), header=hdr.strip(), methods_jit=kinds_n["method"], method_code=meth_code, method_fp=meth_fp,
               thunk_bytes=thunk_bytes, align_bytes=align_fp, tramp_fp=tramp_fp, tramp=dict((k, (kinds_n[k], kinds[k])) for k in kinds if k != "method"),
               used_extent=used_total, first_offsets=first_off, chunk_tails=tails, reserved=reserved, reserved_live=reserved_live, chunks=len(chunks),
               outside=len(outside), groups={g: (groups_n[g], groups_code[g], groups[g]) for g in groups}, wrappers=dict(wrap), generics=dict(gen),
               a32=proj, ratio=(a32_lo, a32, a32_hi), j1=j1s, growth=growth,
               aot_methods=sum(1 for m in meths if m["kind"] == "aot"), interp_methods=sum(1 for m in meths if m["kind"] == "interp"),
               freed=sum(1 for m in meths if m["freed"]), post=post_stats)
    return res

def report(r, md=False):
    p = print
    p(f"## {r['run']}")
    p(f"- census: {r['header'][2:]}")
    ps = r["post"]
    if ps["last_frame"] is not None:
        p(f"- after the run's last frame (f{ps['last_frame']}: shutdown + run reports) {ps['methods']:,} methods, code {ps['method_code']/MiB:.2f} MiB, "
          f"footprint incl. {ps['tramp']:,} trampolines {ps['fp']/MiB:.2f} MiB -- {'INCLUDED in' if ps['included'] else 'left out of'} the totals below")
    p(f"- JIT methods {r['methods_jit']:,}: code {r['method_code']/MiB:.2f} MiB, footprint in the code managers {r['method_fp']/MiB:.2f} MiB "
      f"(thunk areas {r['thunk_bytes']/MiB:.2f}, alignment / unwind {r['align_bytes']/MiB:.2f}); AOT methods used {r['aot_methods']:,}; interp {r['interp_methods']:,}; freed (dynamic) {r['freed']}")
    p(f"- trampolines / thunks outside methods: {r['tramp_fp']/MiB:.2f} MiB -- " + ", ".join(f"{k} {n:,} ({b/1024:.0f} KiB)" for k, (n, b) in sorted(r['tramp'].items(), key=lambda kv: -kv[1][1])))
    p(f"- code managers: {r['chunks']} chunks reserved {r['reserved']/MiB:.2f} MiB (live at exit {r['reserved_live']/MiB:.2f}); used extent {r['used_extent']/MiB:.2f} MiB; never-used chunk tails {r['chunk_tails']/MiB:.2f} MiB; allocations outside any recorded chunk {r['outside']}")
    p("- by image group (methods / code MiB / footprint MiB): " + "; ".join(f"{g} {n:,} / {c/MiB:.2f} / {f/MiB:.2f}" for g, (n, c, f) in sorted(r['groups'].items(), key=lambda kv: -kv[1][2])))
    p(f"- wrappers {r['wrappers'].get('n',0):,} ({r['wrappers'].get('fp',0)/MiB:.2f} MiB), generic instances {r['generics'].get('n',0):,} ({r['generics'].get('fp',0)/MiB:.2f} MiB)")
    lo, mid, hi = r["a32"]["lo"], r["a32"]["mid"], r["a32"]["hi"]
    p(f"- **A32 projection (pure JIT, DERIVED x{r['ratio'][0]}/{r['ratio'][1]}/{r['ratio'][2]})**: {lo/MiB:.2f} / **{mid/MiB:.2f}** / {hi/MiB:.2f} MiB "
      f"of the 16 MiB ForVM block ({mid/FORVM*100:.0f} %; headroom {(FORVM-hi)/MiB:.2f} MiB at the high ratio)")
    for nm, v in r["j1"].items():
        p(f"- {nm}: JIT'd code {v['code']/MiB:.2f} MiB arm64, footprint {v['fp']/MiB:.2f}; A32 + trampolines {v['a32_mid']/MiB:.2f} (high {v['a32_hi']/MiB:.2f}) MiB")
    if r["growth"]:
        p("- growth (frame: methods, arm64 footprint incl. trampolines MiB, scene):")
        prev = 0
        for g in r["growth"]:
            p(f"  - f{g['frame']}: {g['methods']:,} methods, {g['fp']/MiB:.3f} MiB (+{(g['fp']-prev)/1024:.0f} KiB){' ' + g['scene'] if g['scene'] else ''}")
            prev = g["fp"]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--a32", type=float, default=1.4); ap.add_argument("--a32-lo", type=float, default=1.23); ap.add_argument("--a32-hi", type=float, default=1.6)
    ap.add_argument("--json"); ap.add_argument("--md", action="store_true"); ap.add_argument("--include-post", action="store_true")
    a = ap.parse_args()
    out = []
    for d in a.runs:
        r = analyse(d, a.a32, a.a32_lo, a.a32_hi, a.include_post); report(r, a.md); out.append(r)
    if a.json: json.dump(out, open(a.json, "w"), indent=1, default=str)

if __name__ == "__main__":
    main()
