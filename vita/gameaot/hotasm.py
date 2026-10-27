#!/usr/bin/env python3
"""hotasm.py <profiles dir> <menu run> <intro run>: the assemblies the menu (idle run) and the intro run load, with group, size
(AsmSize) and how much of each the whole run executes (interp census, all threads) -> <profiles dir>/assemblies-loaded.{tsv,md}"""
import sys, os, collections
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hotan import group, census, GROUP_ORDER   # noqa: E402
O, menu, intro = sys.argv[1:4]
def loaded(run): return [l.split("\t")[0] for l in open(os.path.join(run, "assemblies.txt")) if not l.startswith("#")]
lm, li = set(loaded(menu)), loaded(intro)
asz = {}
for l in open(os.path.join(O, "asmsize.tsv")):
    x = l.rstrip("\n").split("\t")
    if x[0] != "assembly" and len(x) > 6: asz[x[0]] = (int(x[4]), int(x[5]))
cm, ci = census(menu), census(intro)
rows = sorted(li, key=lambda a: (GROUP_ORDER.index(group(a)) if group(a) in GROUP_ORDER else 99, -asz.get(a, (0, 0))[0]))
with open(os.path.join(O, "assemblies-loaded.tsv"), "w") as w:
    w.write("assembly\tgroup\tin_menu_run\til_bytes\tmethods\tmenu_run_methods_executed\tintro_run_methods_executed\tintro_run_interp_ir_bytes\n")
    for a in rows:
        il, n = asz.get(a, (0, 0))
        w.write(f"{a}\t{group(a)}\t{int(a in lm)}\t{il}\t{n}\t{cm.get(a, [0, 0])[0]}\t{ci.get(a, [0, 0])[0]}\t{ci.get(a, [0, 0])[1]}\n")
g = collections.defaultdict(lambda: [0, 0, 0, 0, 0, 0, 0])
for a in rows:
    il, n = asz.get(a, (0, 0)); v = g[group(a)]
    v[0] += 1; v[1] += a in lm; v[2] += il; v[3] += n; v[4] += cm.get(a, [0, 0])[0]; v[5] += ci.get(a, [0, 0])[0]; v[6] += ci.get(a, [0, 0])[1]
with open(os.path.join(O, "assemblies-loaded.md"), "w") as w:
    w.write(f"Assemblies loaded: menu run {len(lm)}, intro run {len(li)} (assemblies-loaded.tsv: every one)\n\n")
    w.write("| group | assemblies (intro / menu) | IL KiB | methods | executed, menu run | executed, intro run (all threads, whole run) | interp IR KiB, intro run |\n|---|---|---:|---:|---:|---:|---:|\n")
    for k in GROUP_ORDER:
        if k not in g: continue
        v = g[k]
        w.write(f"| {k} | {v[0]} / {v[1]} | {v[2] / 1024:,.0f} | {v[3]:,} | {v[4]:,} | {v[5]:,} ({100 * v[5] / max(v[3], 1):.1f} %) | {v[6] / 1024:,.0f} |\n")
    w.write("\nNon-BCL, non-engine assemblies of the intro run (group: names):\n\n")
    for k in ["unity-rewritten", "unity-original", "unity-pkg", "game", "game-lib", "engine-derived"]:
        names = [f"{a}{'' if a in lm else '*'}" for a in rows if group(a) == k]
        if names: w.write(f"- **{k}** ({len(names)}): " + ", ".join(names) + "\n")
    w.write("\n(* = loaded by the intro run only, not by the menu run)\n")
print(open(os.path.join(O, "assemblies-loaded.md")).read())
