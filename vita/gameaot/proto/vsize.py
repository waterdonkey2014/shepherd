#!/usr/bin/env python3
"""vsize.py <dir> [<module list file>...]: ELF section sizes of Vita AOT objects (<M>.dll.o [+ <M>-llvm.o]) in <dir>, per module and
in total: text (+ rodata) = build.sh's 28 MiB measure, data, bss; the 'compiled' count from <M>.log. With list files: totals per list."""
import os, re, subprocess, sys
def secs(o):
    r = {"text": 0, "rodata": 0, "data": 0, "bss": 0}
    out = subprocess.run([os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-size"), "-A", o], capture_output=True, text=True).stdout
    for l in out.splitlines():
        x = l.split()
        if len(x) >= 2 and x[0].startswith("."):
            for k in r:
                if x[0] == "." + k or x[0].startswith("." + k + "."): r[k] += int(x[1])
    return r
def mod(d, m):
    t = {"text": 0, "rodata": 0, "data": 0, "bss": 0}
    for o in (f"{d}/{m}.dll.o", f"{d}/{m}-llvm.o"):
        if os.path.exists(o):
            for k, v in secs(o).items(): t[k] += v
    c = ""
    if os.path.exists(f"{d}/{m}.log"):
        mm = re.findall(r"Compiled: (\d+/\d+)", open(f"{d}/{m}.log", errors="replace").read()); c = mm[-1] if mm else ""
    return t, c
d = sys.argv[1]
lists = sys.argv[2:] or [None]
MiB = 1048576
for L in lists:
    mods = [l.strip() for l in open(L)] if L else sorted(f[:-6] for f in os.listdir(d) if f.endswith(".dll.o"))
    tot = {"text": 0, "rodata": 0, "data": 0, "bss": 0}; miss = []
    rows = []
    for m in mods:
        if not os.path.exists(f"{d}/{m}.dll.o"): miss.append(m); continue
        t, c = mod(d, m); rows.append((m, t, c))
        for k in tot: tot[k] += t[k]
    if not L:
        for m, t, c in sorted(rows, key=lambda r: -(r[1]["text"] + r[1]["rodata"])):
            print(f"{m}\t{c}\t{(t['text'] + t['rodata']) / MiB:.3f}\t{t['data'] / MiB:.3f}\t{t['bss'] / MiB:.3f}")
    print(f"TOTAL {os.path.basename(d)} {os.path.basename(L) if L else 'all'}: {len(rows)} modules, text+rodata {(tot['text'] + tot['rodata']) / MiB:.2f} MiB "
          f"(text {tot['text'] / MiB:.2f}, rodata {tot['rodata'] / MiB:.2f}), data {tot['data'] / MiB:.2f}, bss {tot['bss'] / MiB:.2f}" + (f"; missing {len(miss)}: {' '.join(miss[:5])}" if miss else ""))
