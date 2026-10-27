#!/usr/bin/env python3
"""Kit #7 JIT stage 0: one summary line per bench.sh run (outcome, scenes, window frame time, census totals).
usage: runline.py <run dir>"""
import os, re, statistics as st, sys

def main(d):
    log = open(os.path.join(d, "engine.log"), errors="replace").read()
    env = dict(l.rstrip("\n").split("=", 1) for l in open(os.path.join(d, "env.txt")) if "=" in l)
    m = re.search(r"done: (\d+) errors, (\d+) exceptions", log); ee = f"{m.group(1)}/{m.group(2)}" if m else "?/?"
    m = re.search(r"Main Menu scene present at frame (\d+)", log); menu = m.group(1) if m else "-"
    scenes = []
    for s in re.findall(r"\[ *(\d+)\]\s+scene '([^']+)': (\d+) GameObjects", log):
        scenes.append(f"{s[1]}@{s[0]}")
    last = log.rstrip().splitlines()[-1] if log.strip() else ""
    last = re.sub(r"^\[ *\d+\] *", "", last)[:70]
    win = ""
    m = re.search(r"window frames (\d+)\.\.(\d+)", log)
    p = os.path.join(d, "simstats-frames.tsv")
    if m and os.path.exists(p):
        a, b = int(m.group(1)), int(m.group(2))
        ms = [float(x.split("\t")[1]) for x in list(open(p))[1:] if x.strip() and a <= int(x.split("\t")[0]) <= b]
        if ms: win = f"win {a}..{b} mean {st.mean(ms):.3f} ms p50 {sorted(ms)[len(ms)//2]:.3f}"
    cen = ""
    cp = os.path.join(d, "census.methods.tsv")
    if os.path.exists(cp):
        h = open(cp).readline()
        m = re.search(r"unique code: JIT (\d+) methods (\d+) B, AOT (\d+) methods (\d+) B, interp (\d+)", h)
        if m: cen = f"| JIT {m.group(1)} m {int(m.group(2))/1048576:.2f} MiB, AOT {m.group(3)} m, interp {m.group(5)}"
    w = dict(l.strip().split("=", 1) for l in open(os.path.join(d, "wall.txt")) if "=" in l)
    sh = os.path.join(d, "stream-hash.txt")
    hs = ""
    if os.path.exists(sh):
        import hashlib
        lines = [l.split()[1] if len(l.split()) > 1 else l.strip() for l in open(sh) if l.strip()]
        hs = f"| hash {len(lines)} frames"
    print(f"[{env.get('config')}/{env.get('scenario')}] {ee} menu f{menu} {win} {cen} {hs} | {' '.join(scenes[-4:])} | wall {float(w.get('wall_s','nan')):.1f} s | muted {w.get('muted_after')} | {last}")

if __name__ == "__main__":
    main(sys.argv[1])
