#!/usr/bin/env python3
"""hotsum.py <profiles dir>: cross-scenario tables from every key.json under <profiles dir> (hotan.py) -> stdout (markdown)."""
import sys, os, json, glob
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hotan import GROUP_ORDER   # noqa: E402
O = sys.argv[1]
keys = []
for p in sorted(glob.glob(os.path.join(O, "**", "key.json"), recursive=True)):
    k = json.load(open(p)); k["name"] = k["scenario"] + (("/" + k["segment"]) if k["segment"] else ""); keys.append(k)
order = {"idle": 0, "tour": 1, "intro": 2, "intro-all": 3}
keys.sort(key=lambda k: (order.get(k["scenario"], 9), k["segment"]))
print("| scenario | frames | plain ms/frame median [min..max] (runs) | profiled ms/frame | " + " | ".join(GROUP_ORDER) + " |")
print("|---|---:|---|---:|" + "---:|" * len(GROUP_ORDER))
for k in keys:
    pl = k["plain_ms_per_frame"]; t = k["main_self_total"] or 1
    print(f"| {k['name']} | {k['frames']} | " + (f"{pl[0]:.2f} [{pl[1]:.2f}..{pl[2]:.2f}] ({k['plain_runs']})" if pl else "") + f" | {k['prof_ms_per_frame']:.2f} | " +
          " | ".join(f"{100 * k['groups'][g]['self_main'] / t:.1f} %" if g in k["groups"] else "–" for g in GROUP_ORDER) + " |")
print("\n(share of the main thread's self time; median over the inline-on profile runs)\n")
print("| scenario | " + " | ".join(["C0", "C1", "C2", "C3", "C4", "C5"]) + " | PH0 | P90 | PH90 | P95 | PH95 | P99 | PH99 |")
print("|---|" + "---:|" * 13)
for k in keys:
    c = k["configs"]; p = k["partial"]
    print(f"| {k['name']} | " + " | ".join(f"{c[n]['a2i']:,.0f} + {c[n]['i2a']:,.0f}" for n in ["C0", "C1", "C2", "C3", "C4", "C5"]) + " | " +
          " | ".join((f"{p[n]['a2i'] + p[n]['i2a']:,.0f} ({p[n]['methods']} m, {p[n]['il'] / 1024:.0f} KiB)" if n in p else "–") for n in ["PH0", "P90", "PH90", "P95", "PH95", "P99", "PH99"]) + " |")
print("\n(transitions per frame on the main thread: AOT->interp + interp->AOT for C0..C5; total for the partial sets, with their size)\n")
print("| scenario | outside C1: ms/frame (profiled), methods | 50 % | 80 % | 90 % | 95 % | 99 % | 100 % |\n|---|---|---|---|---|---|---|---|")
for k in keys:
    cv = {round(x["frac"], 2): x for x in k["coverage"]}
    print(f"| {k['name']} | {k['outside_c1_ms']:.3f}, {k['outside_c1_methods']} | " + " | ".join((f"{cv[f]['methods']} m / {cv[f]['il'] / 1024:.0f} KiB" if f in cv else "–") for f in [0.5, 0.8, 0.9, 0.95, 0.99, 1.0]) + " |")
