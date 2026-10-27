#!/usr/bin/env python3
"""macbench_summary.py [--line] <run dir>...: the vita-sim menu-window numbers of vita/llvm/macbench.sh runs
(the same window and parsing as vita/v6/tools/sim_summary.py: engine.log "[sim] window frames A..B" + simstats-frames.tsv).
Per run: window frame ms mean/p50/p95, render and logic means, errors/exceptions, the menu selection at the probes, wall s.
Then per config (the run's env.txt config=): the median over its runs, and the ratios of every config to aot-nollvm
(and of aot-llvm-eng to aot-nollvm-eng)."""
import os, re, sys, statistics as st

def pct(xs, q):
    xs = sorted(xs); return xs[min(len(xs) - 1, int(round(q * (len(xs) - 1))))] if xs else float("nan")

def info(d):
    r = {"dir": d, "name": os.path.basename(d.rstrip("/"))}
    log = open(os.path.join(d, "engine.log"), errors="replace").read()
    env = dict(l.rstrip("\n").split("=", 1) for l in open(os.path.join(d, "env.txt")) if "=" in l)
    r["config"] = env.get("config", "?")
    m = re.search(r"done: (\d+) errors, (\d+) exceptions", log); r["ee"] = f"{m.group(1)}/{m.group(2)}" if m else "?/?"
    sels = re.findall(r"\[menu\] frame (\d+): selected='([^']*)' label='([^']*)'", log)
    r["sels"] = ",".join(s[2] or s[1] for s in sels)
    m = re.search(r"window frames (\d+)\.\.(\d+)", log)
    rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(d, "simstats-frames.tsv"))][1:] if os.path.exists(os.path.join(d, "simstats-frames.tsv")) else []
    fr = {int(x[0]): (float(x[1]), float(x[2])) for x in rows}
    if m:
        w = [fr[f] for f in range(int(m.group(1)), int(m.group(2)) + 1) if f in fr]
        r["ms"] = [x[0] for x in w]; r["render"] = [x[1] for x in w]
    else:
        r["ms"] = r["render"] = []
    m = re.search(r"Main Menu scene present at frame (\d+)", log); r["menu"] = int(m.group(1)) if m else None
    w = dict(l.strip().split("=", 1) for l in open(os.path.join(d, "wall.txt")) if "=" in l)
    r["wall"] = float(w.get("wall_s", "nan")); r["rc"] = w.get("rc"); r["load"] = w.get("load1", "")
    return r

def line(r):
    if not r["ms"]: return f"[{r['config']}] no window (rc={r['rc']}, {r['ee']})"
    return (f"[{r['config']}] window mean {st.mean(r['ms']):.3f} p50 {pct(r['ms'], .5):.3f} p95 {pct(r['ms'], .95):.3f} ms | "
            f"render {st.mean(r['render']):.3f} logic {st.mean(r['ms']) - st.mean(r['render']):.3f} | {r['ee']} | menu f{r['menu']} | "
            f"{r['sels']} | wall {r['wall']:.1f} s")

def main(a):
    if a and a[0] == "--line":
        print(line(info(a[1]))); return
    runs = [info(d) for d in a]
    print("| run | config | frame mean | p50 | p95 | render | logic | errors/exc | menu at | probes | wall s |")
    print("|---|---|---:|---:|---:|---:|---:|---|---|---|---:|")
    for r in runs:
        if not r["ms"]: print(f"| {r['name']} | {r['config']} | – | | | | | {r['ee']} | | {r['sels']} | {r['wall']:.1f} |"); continue
        print(f"| {r['name']} | {r['config']} | {st.mean(r['ms']):.3f} | {pct(r['ms'], .5):.3f} | {pct(r['ms'], .95):.3f} | {st.mean(r['render']):.3f} | "
              f"{st.mean(r['ms']) - st.mean(r['render']):.3f} | {r['ee']} | f{r['menu']} | {r['sels']} | {r['wall']:.1f} |")
    by = {}
    for r in runs:
        if r["ms"]: by.setdefault(r["config"], []).append(r)
    med = {c: dict(n=len(v), ms=st.median([st.mean(x["ms"]) for x in v]), p50=st.median([pct(x["ms"], .5) for x in v]),
                   render=st.median([st.mean(x["render"]) for x in v]), logic=st.median([st.mean(x["ms"]) - st.mean(x["render"]) for x in v]))
           for c, v in by.items()}
    print("\n| config | runs | median of window means (ms) | median p50 | render | logic | vs aot-nollvm | vs aot-nollvm-eng |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|")
    for c in ["interp", "jit", "aot-nollvm", "aot-llvm", "aot-nollvm-eng", "aot-llvm-eng", "aot-nollvm-eng-inl", "aot-llvm-eng-inl"]:
        if c not in med: continue
        m = med[c]
        r1 = f"{med['aot-nollvm']['ms'] / m['ms']:.3f}x" if "aot-nollvm" in med else ""
        r2 = f"{med['aot-nollvm-eng']['ms'] / m['ms']:.3f}x" if "aot-nollvm-eng" in med else ""
        print(f"| {c} | {m['n']} | {m['ms']:.3f} | {m['p50']:.3f} | {m['render']:.3f} | {m['logic']:.3f} | {r1} | {r2} |")

if __name__ == "__main__":
    main(sys.argv[1:])
