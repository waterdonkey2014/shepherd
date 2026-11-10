#!/usr/bin/env python3
# names the stacks of a console's VRAMCHECK lines from the run's ELF. usage: vcsym.py <console> <elf>
import sys, re, subprocess, os
con, elf = sys.argv[1:3]
lines = [l for l in open(con, errors="replace") if l.startswith("VRAMCHECK")]
addrs = []
for l in lines:
    m = re.search(r"stack ((?:[0-9a-f]+ ?)+)$", l.strip())
    if m: addrs += m[1].split()
names = {}
if addrs:
    uniq = sorted(set(addrs))
    out = subprocess.run([os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-addr2line"), "-f", "-C", "-e", elf], input="\n".join(hex(int(a, 16) & ~1) for a in uniq) + "\n", capture_output=True, text=True).stdout.split("\n")
    for i, a in enumerate(uniq): names[a] = out[i * 2][:70]
for l in lines:
    l = l.rstrip()
    m = re.search(r"^(.*stack )((?:[0-9a-f]+ ?)+)$", l)
    if not m: print(l); continue
    print(m[1].rstrip())
    for a in m[2].split():
        if names.get(a, "??") != "??": print("       ", a, names[a])
