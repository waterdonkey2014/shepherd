#!/usr/bin/env python3
"""sim_summary.py <run>...: the V6 vita-sim numbers from runtime/logs/v6/<run>/ (vita/notes/v6-mac-sim.md).
Timing runs: engine.log [sim] lines + simstats-frames.tsv + wall.txt. A census run (mono.log present) adds the exact per-GC
pauses (sgen's GC_MINOR / GC_MAJOR stw times; the window's GCs are picked by GC count, not by time), the interpreter census
(interp-methods.tsv) and the Mono DllImport census (native-resolve.txt section 1m)."""
import os, re, sys, statistics as st

LOGS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "runtime", "logs", "v6")

def pct(xs, q):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(round(q * (len(xs) - 1))))] if xs else float("nan")

def run_info(name):
    d = os.path.join(LOGS, name); r = {"name": name}
    log = open(os.path.join(d, "engine.log"), errors="replace").read()
    m = re.search(r"done: (\d+) errors, (\d+) exceptions", log); r["errors"], r["exc"] = (int(m.group(1)), int(m.group(2))) if m else (None, None)
    m = re.search(r"\[menu\] frame 1450: selected='([^']*)' label='([^']*)'", log); r["sel1450"] = m.group(2) if m else None
    m = re.search(r"\[menu\] frame 1300: selected='([^']*)' label='([^']*)'", log); r["sel1300"] = m.group(2) if m else None
    m = re.search(r"\[menu\]   buttons \(\d+\): (.*)", log); r["buttons"] = m.group(1).strip() if m else None
    m = re.search(r"LIVE after = ([\d.]+) MiB", log); r["live"] = float(m.group(1)) if m else None
    m = re.search(r"forced full GC #1 ([\d.]+) ms, #2 ([\d.]+) ms", log); r["fullgc"] = (float(m.group(1)), float(m.group(2))) if m else None
    m = re.search(r"unix ms (\d+)\); asset layer up \+(\d+) ms; frame 1 done \+(\d+) ms; Main Menu scene present at frame (\d+) \(\+(\d+) ms\); run total \+(\d+) ms", log)
    if m: r["main_ms"], r["assets"], r["frame1"], r["menu_frame"], r["menu_ms"], r["total"] = [int(x) for x in m.groups()]
    m = re.search(r"GC index range minor (\d+)\.\.(\d+) major (\d+)\.\.(\d+)", log); r["gcidx"] = tuple(int(x) for x in m.groups()) if m else None
    m = re.search(r"JIT-compiled methods (\d+)", log); r["jitted"] = int(m.group(1)) if m else None
    m = re.search(r"assemblies loaded: (\d+), ([\d.]+) MiB", log); r["asm"] = (int(m.group(1)), float(m.group(2))) if m else None
    w = dict(l.strip().split("=", 1) for l in open(os.path.join(d, "wall.txt")) if "=" in l)
    r["wall"] = float(w["wall_s"]); r["launch_ms"] = float(w["launch_unix_s"]) * 1000
    if "main_ms" in r: r["premain"] = r["main_ms"] - r["launch_ms"]
    rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(d, "simstats-frames.tsv"))][1:]
    fr = {int(x[0]): (float(x[1]), float(x[2]), int(x[3]), int(x[4]), int(x[5])) for x in rows}
    m = re.search(r"window frames (\d+)\.\.(\d+)", log)   # SIM_REL=1 (stage 2): the window is relative to the Main Menu's arrival
    w0, w1 = (int(m.group(1)), int(m.group(2))) if m else (1301, 1400)
    win = [fr[f] for f in range(w0, w1 + 1) if f in fr]
    r["ms"] = [x[0] for x in win]; r["render"] = [x[1] for x in win]; r["alloc"] = [x[2] for x in win]
    r["gcframes"] = sum(1 for x in win if x[3] + x[4] > 0)
    r["boot_ms"] = [fr[f][0] for f in sorted(fr)]
    env = {}
    for l in open(os.path.join(d, "env.txt")):
        k, _, v = l.rstrip("\n").partition("="); env[k] = v   # later entries override, like env(1)
    mo = env.get("MONO_ENV_OPTIONS", "")
    r["engine"] = "Mono interp" if "--interpreter" in mo else "CoreCLR" if "SHEPHERD_NATIVE_CENSUS_STACKS" in env else "Mono JIT"
    if os.path.exists(os.path.join(d, "mono.log")): r.update(census(d, r))
    return r

def census(d, r):
    out = {}
    minors, majors = [], []
    for l in open(os.path.join(d, "mono.log"), errors="replace"):
        m = re.search(r"GC_(MINOR|MAJOR)(?:_OVERFLOW)?: \(([^)]*)\) time ([\d.]+)ms, (?:stw ([\d.]+)ms)?", l)
        if not m: continue
        rec = (m.group(2), float(m.group(4) or m.group(3)))
        (minors if m.group(1) == "MINOR" else majors).append(rec)
    out["gc_all"] = (minors, majors)
    if r.get("gcidx"):
        a, b, c, e = r["gcidx"]
        out["gc_win"] = (minors[a:b], majors[c:e])
    p = os.path.join(d, "interp-methods.tsv")
    if os.path.exists(p):
        head = open(p).readline()
        out["interp_head"] = head.strip("# \n")
        rows = [l.rstrip("\n").split("\t") for l in open(p) if not l.startswith("#")][1:]
        by = {}
        for x in rows:
            if x[0] != "interp": continue
            k = by.setdefault(x[1], [0, 0]); k[0] += 1; k[1] += int(x[3])
        out["interp_by_image"] = sorted(by.items(), key=lambda kv: -kv[1][1])
    return out

def ms(xs): return f"mean {st.mean(xs):.2f} p50 {pct(xs, .5):.2f} p95 {pct(xs, .95):.2f} p99 {pct(xs, .99):.2f} max {max(xs):.2f}"

def main(names):
    runs = [run_info(n) for n in names]
    print("| run | engine | errors/exc | f1300 / f1450 selected | frame ms (window 1301-1400) | render ms | alloc KiB/frame | GC frames | live MiB (forced GC) | forced full GC ms | startup: pre-Main / frame 1 / menu scene | wall s |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for r in runs:
        print(f"| {r['name']} | {r['engine']} | {r['errors']}/{r['exc']} | {r['sel1300']} / {r['sel1450']} | {ms(r['ms'])} | {st.mean(r['render']):.2f} | {st.mean(r['alloc'])/1024:.0f} | {r['gcframes']} | {r['live']:.1f} | {r['fullgc'][0]:.1f} / {r['fullgc'][1]:.1f} | "
              f"{r.get('premain', float('nan')):.0f} ms / {r.get('frame1', 0)/1000:.2f} s / f{r.get('menu_frame')} {r.get('menu_ms', 0)/1000:.2f} s | {r['wall']:.1f} |")
    timing = [r for r in runs if r["engine"] == "Mono interp" and "gc_all" not in r]
    if timing:
        print(f"\ninterpreter timing runs ({len(timing)}): median of means {st.median([st.mean(r['ms']) for r in timing]):.2f} ms/frame, "
              f"render {st.median([st.mean(r['render']) for r in timing]):.2f}, logic {st.median([st.mean(r['ms']) - st.mean(r['render']) for r in timing]):.2f}; "
              f"frame 1 at {st.median([r['frame1'] for r in timing])/1000:.2f} s after Main, pre-Main {st.median([r['premain'] for r in timing]):.0f} ms; "
              f"menu scene at {st.median([r['menu_ms'] for r in timing])/1000:.2f} s (frame {st.median([r['menu_frame'] for r in timing]):.0f})")
    for r in runs:
        if "gc_all" not in r: continue
        mi, ma = r["gc_all"]
        print(f"\n{r['name']} (census run):")
        print(f"  boot GCs: {len(mi)} minor (stw {ms([x[1] for x in mi])} ms), {len(ma)} major (stw {ms([x[1] for x in ma])} ms); major reasons: " +
              ", ".join(f"{k} x{v}" for k, v in sorted({k: sum(1 for x in ma if x[0] == k) for k in set(x[0] for x in ma)}.items(), key=lambda kv: -kv[1])))
        if "gc_win" in r:
            wmi, wma = r["gc_win"]; allp = [x[1] for x in wmi + wma]
            print(f"  window 1301-1400: {len(wmi)} minor (stw {ms([x[1] for x in wmi]) if wmi else 'n/a'}), {len(wma)} major ({', '.join(f'{x[0]} {x[1]:.1f}' for x in wma)} ms); "
                  f"all pauses p50 {pct(allp, .5):.2f} p99 {pct(allp, .99):.2f} max {max(allp):.2f} ms; GC time {sum(allp):.1f} ms per 100 frames")
        if "interp_head" in r:
            print("  " + r["interp_head"])
            print("  interpreted methods by image (methods, IR KiB): " + "; ".join(f"{k} {v[0]} / {v[1]/1024:.0f}" for k, v in r["interp_by_image"][:14]))

if __name__ == "__main__":
    main(sys.argv[1:])
