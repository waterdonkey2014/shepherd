#!/usr/bin/env python3
"""kit4 intro-plan: the main thread's call tree below a symbol (macOS `sample` output), to a depth, entries >= a share."""
import re, sys
path, root, maxd, minpct = sys.argv[1], sys.argv[2], int(sys.argv[3]), float(sys.argv[4])
lines = open(path).read().splitlines(); start = next(i for i, l in enumerate(lines) if 'Call graph:' in l)
blk = []; inmain = False; total = 1
for l in lines[start + 1:]:
    if re.match(r'^\s{4}\d+ Thread_', l):
        inmain = 'main-thread' in l
        if inmain: total = int(l.split()[0])
        continue
    if l.startswith('Total number in stack') or l.startswith('Sort by top'): break
    if inmain: blk.append(l)
rx = re.compile(r'^([\s+!:|]*)(\d+) (.*?)(\s+\(in .*)?$')
nodes = []
for l in blk:
    m = rx.match(l)
    if m: nodes.append((len(m.group(1)), int(m.group(2)), m.group(3)))
# find first occurrence of root; print subtree
for i, (d, n, name) in enumerate(nodes):
    if root in name:
        print(f'{n:6d} {100*n/total:5.1f}%  {name[:150]}'); base = d; rel = []
        for d2, n2, name2 in nodes[i + 1:]:
            if d2 <= base: break
            lvl = (d2 - base) // 2
            if lvl <= maxd and 100 * n2 / total >= minpct: print(f'{n2:6d} {100*n2/total:5.1f}%  {"  " * lvl}{name2[:140]}')
        break
