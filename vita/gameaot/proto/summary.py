#!/usr/bin/env python3
"""summary.py — the game-aot prototype's Mac-proxy numbers (vita/gameaot/proto/bench.sh runs).

  summary.py --line <run dir>                     one line per run (bench.sh prints it)
  summary.py [--md] [--base A] <run dir>...       per run, then per (scenario, window, config): median of the window means
                                                  [min..max], render / logic, alloc, GCs, outcome; ratios vs the base config
                                                  and paired per-round differences (round k of config X vs round k of base)
Windows: idle = SimStats m+487..m+586 (engine.log "window frames A..B"); tour = m+494..m+1793; intro = frames 1..5000 split at
the scenes' arrival frames: boot 1-805, menu 806-1785, quote 1786-2934, gameplay 2935-5000 (hot study's segments).
Frame ms = simstats-frames.tsv `ms` (wall per frame); render = `render_ms`; logic = ms - render; alloc = main-thread bytes.
Device column: the plan's A9-vs-M4 rule applied to the Mac full-AOT median (x43; x65 memory-bound) -- a PROJECTION, no device run."""
import os, re, sys, statistics as st, collections

INTRO_SEGS = [("boot", 1, 805), ("menu", 806, 1785), ("quote", 1786, 2934), ("gameplay", 2935, 5000)]

def pct(xs, q):
    xs = sorted(xs); return xs[min(len(xs) - 1, int(round(q * (len(xs) - 1))))] if xs else float("nan")

def info(d):
    r = {"dir": d, "name": os.path.basename(d.rstrip("/"))}
    log = open(os.path.join(d, "engine.log"), errors="replace").read()
    env = dict(l.rstrip("\n").split("=", 1) for l in open(os.path.join(d, "env.txt")) if "=" in l)
    r["config"] = env.get("config", "?"); r["scenario"] = env.get("scenario", "?")
    m = re.search(r"m = ", "")
    m = re.search(r"done: (\d+) errors, (\d+) exceptions", log); r["ee"] = f"{m.group(1)}/{m.group(2)}" if m else "?/?"
    sels = re.findall(r"\[menu\] frame (\d+): selected='([^']*)' label='([^']*)'", log)
    r["sels"] = ",".join(s[2] or s[1] for s in sels)
    m = re.search(r"Main Menu scene present at frame (\d+)", log); r["menu"] = int(m.group(1)) if m else None
    sc = re.findall(r"scene '([^']+)': (\d+) GameObjects", log); r["scenes"] = [s[0] for s in sc]
    m = re.search(r"LIVE after = ([0-9.]+) MiB", open(os.path.join(d, "simstats.txt")).read()) if os.path.exists(os.path.join(d, "simstats.txt")) else None
    r["live"] = float(m.group(1)) if m else None
    rows = []
    p = os.path.join(d, "simstats-frames.tsv")
    if os.path.exists(p):
        for l in list(open(p))[1:]:
            x = l.rstrip("\n").split("\t")
            if len(x) >= 6: rows.append((int(x[0]), float(x[1]), float(x[2]), int(x[3]), int(x[4]), int(x[5])))
    fr = {x[0]: x for x in rows}
    wins = {}
    if r["scenario"] == "intro":
        for nm, a, b in INTRO_SEGS:
            wins[nm] = [fr[f] for f in range(a, b + 1) if f in fr]
        wins["all"] = [fr[f] for f in sorted(fr)]
    else:
        m = re.search(r"window frames (\d+)\.\.(\d+)", log)
        if m: wins[r["scenario"]] = [fr[f] for f in range(int(m.group(1)), int(m.group(2)) + 1) if f in fr]
    r["wins"] = {}
    for nm, w in wins.items():
        if not w: continue
        ms = [x[1] for x in w]; rd = [x[2] for x in w]
        r["wins"][nm] = dict(n=len(w), mean=st.mean(ms), p50=pct(ms, .5), p95=pct(ms, .95), render=st.mean(rd),
                             logic=st.mean(ms) - st.mean(rd), alloc=st.mean(x[3] for x in w) / 1024,
                             minor=sum(x[4] for x in w), major=sum(x[5] for x in w))
    w = dict(l.strip().split("=", 1) for l in open(os.path.join(d, "wall.txt")) if "=" in l)
    r["wall"] = float(w.get("wall_s", "nan")); r["rc"] = w.get("rc"); r["load"] = w.get("load1", ""); r["muted"] = w.get("muted_after")
    return r

def line(r):
    if not r["wins"]: return f"[{r['config']}/{r['scenario']}] no window (rc={r['rc']}, {r['ee']})"
    ws = " | ".join(f"{k} {v['mean']:.3f} (p50 {v['p50']:.3f}; r {v['render']:.3f} l {v['logic']:.3f}; {v['alloc']:.1f} KiB)" for k, v in r["wins"].items() if k != "all")
    return f"[{r['config']}/{r['scenario']}] {ws} | {r['ee']} | menu f{r['menu']} | {r['sels']} | wall {r['wall']:.1f} s | muted {r['muted']}"

def rnd(name):
    m = re.match(r"[A-Za-z]*(\d+)-", name); return int(m.group(1)) if m else 0

def main(a):
    if a and a[0] == "--line":
        print(line(info(a[1]))); return
    base = "A"
    if a and a[0] == "--base": base = a[1]; a = a[2:]
    runs = [info(d) for d in a if os.path.exists(os.path.join(d, "engine.log"))]
    by = collections.defaultdict(list)     # (scenario, window, config) -> runs
    for r in runs:
        for wn in r["wins"]:
            if wn == "all": continue
            by[(r["scenario"], wn, r["config"])].append(r)
    order = ["I", "A", "B", "C", "P", "D", "DP", "E", "H", "HP"]
    print("| scenario / window | config | runs | frame ms, median of window means [min..max] | p50 | render | logic | vs base (ratio of medians) | paired diff vs base, median [min..max] (faster/rounds) | alloc KiB/frame | GCs minor+major / window (median) | outcome (errors/exc, menu frame, probes) | live MiB | device x43 (x65) PROJECTION |")
    print("|---|---|---:|---|---:|---:|---:|---:|---|---:|---|---|---|---|")
    for (sc, wn) in sorted({(k[0], k[1]) for k in by}, key=lambda t: (["idle", "tour", "intro"].index(t[0]) if t[0] in ("idle", "tour", "intro") else 9, [s[0] for s in INTRO_SEGS].index(t[1]) if t[1] in [s[0] for s in INTRO_SEGS] else 0)):
        cfgs = sorted({k[2] for k in by if k[0] == sc and k[1] == wn}, key=lambda c: order.index(c) if c in order else 99)
        bm = None
        if (sc, wn, base) in by:
            bm = st.median([x["wins"][wn]["mean"] for x in by[(sc, wn, base)]])
            brr = {rnd(x["name"]): x["wins"][wn]["mean"] for x in by[(sc, wn, base)]}
        for c in cfgs:
            rs = by[(sc, wn, c)]; ms = [x["wins"][wn]["mean"] for x in rs]
            med = st.median(ms)
            ratio = f"{bm / med:.3f}x" if bm else ""
            pd = ""
            if bm and c != base:
                d = [x["wins"][wn]["mean"] - brr[rnd(x["name"])] for x in rs if rnd(x["name"]) in brr]
                if d: pd = f"{st.median(d):+.3f} [{min(d):+.3f}..{max(d):+.3f}] ({sum(1 for v in d if v < 0)}/{len(d)})"
            oc = collections.Counter(f"{x['ee']}, f{x['menu']}, {x['sels']}" for x in rs)
            live = [x["live"] for x in rs if x["live"]]
            print(f"| {sc} / {wn} | {c} | {len(rs)} | {med:.3f} [{min(ms):.3f}..{max(ms):.3f}] | {st.median(x['wins'][wn]['p50'] for x in rs):.3f} | "
                  f"{st.median(x['wins'][wn]['render'] for x in rs):.3f} | {st.median(x['wins'][wn]['logic'] for x in rs):.3f} | {ratio} | {pd} | "
                  f"{st.median(x['wins'][wn]['alloc'] for x in rs):.1f} | {st.median(x['wins'][wn]['minor'] for x in rs):.0f}+{st.median(x['wins'][wn]['major'] for x in rs):.0f} | "
                  f"{'; '.join(f'{k} x{v}' for k, v in oc.items())} | {f'{min(live):.2f}..{max(live):.2f}' if live else ''} | "
                  f"{med * 43:.0f} ms = {1000 / (med * 43):.1f} fps ({med * 65:.0f} ms = {1000 / (med * 65):.1f} fps) |")
    loads = [r["load"] for r in runs]
    print(f"\n{len(runs)} runs; muted after every run: {all(r['muted'] == 'true' for r in runs)}; rc 0: {sum(1 for r in runs if r['rc'] == '0')}/{len(runs)}")

if __name__ == "__main__":
    main(sys.argv[1:])
