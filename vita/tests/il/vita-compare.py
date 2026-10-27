#!/usr/bin/env python3
"""Kit #7 tests track: the Vita tiers (vlx: hello-gen.py's conformance hello -- ux0:data/shepherd/hello.txt, M50 = AOT, M51 = interpreter)
against the Mac tiers of vita/tests/il/run.sh (Unity's Mono with / without float32, the Unity players, CoreCLR) for the tests both ran.
  vita-compare.py <hello.txt> <mac runs dir> [<fuzz runs dir>]"""
import sys, os, re
hello, runs = sys.argv[1], sys.argv[2]; fuzz = sys.argv[3] if len(sys.argv) > 3 else None
vita = {"vita-aot": {}, "vita-interp": {}}
for l in open(hello, encoding="utf-8", errors="replace"):
    m = re.match(r"M5([01]) (aot|interp)\t([^\t]+)\t([^\t]+)\t(.*)$", l.rstrip("\n"))
    if m:
        f = m.group(3); f = "fpmode" if f == "FpMode" else f
        vita["vita-" + m.group(2)][(f, m.group(4))] = m.group(5)
def mac(d, t):
    r = {}
    p = os.path.join(d, t + ".tsv")
    if not os.path.exists(p): return r
    for l in open(p):
        x = l.rstrip("\n").split("\t")
        if len(x) < 3: continue
        f = x[0].replace("conf-", "").replace(".dll", "")
        r[(f, x[1])] = x[2]
    return r
tiers = ["unity-mono", "unity-mono-nof32", "player-arm64", "player-x64", "coreclr"]
M = {t: {**mac(runs, t), **(mac(fuzz, t) if fuzz else {})} for t in tiers}
keys = sorted(vita["vita-aot"])
print(f"{len(keys)} tests ran on the Vita tiers (vlx)")
for v in ("vita-aot", "vita-interp"):
    for t in tiers:
        common = [k for k in keys if k in M[t] and k in vita[v]]
        diff = [k for k in common if vita[v][k] != M[t][k]]
        fams = {}
        for k in diff: fams[k[0]] = fams.get(k[0], 0) + 1
        print(f"  {v} vs {t}: {len(common)} compared, {len(diff)} differ" + (f" ({', '.join(f'{a} {b}' for a, b in sorted(fams.items()))})" if diff else ""))
a, i = vita["vita-aot"], vita["vita-interp"]
d = [k for k in keys if a.get(k) != i.get(k)]
print(f"  vita-aot vs vita-interp: {len(d)} differ" + (": " + ", ".join(f"{k[1]}={a.get(k)}/{i.get(k)}" for k in d[:8]) if d else ""))
print("fpmode probes (vita-aot / vita-interp / unity-mono (float32) / player-arm64 (the game's runtime) / coreclr):")
for k in keys:
    if k[0] == "fpmode": print(f"  {k[1].split('.')[-1]:16s} {a.get(k)} / {i.get(k)} / {M['unity-mono'].get(k)} / {M['player-arm64'].get(k)} / {M['coreclr'].get(k)}")
