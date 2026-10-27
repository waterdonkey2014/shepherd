#!/usr/bin/env python3
"""kit4 intro-plan: inclusive sample counts of chosen symbols on the main thread of a macOS `sample` call graph (outermost occurrence only)."""
import re, sys
path, syms = sys.argv[1], sys.argv[2:]
lines = open(path).read().splitlines()
start = next(i for i, l in enumerate(lines) if 'Call graph:' in l)
# main thread block: first "Thread_" line containing main-thread
blk = []; inmain = False
for l in lines[start + 1:]:
    if re.match(r'^\s{4}\d+ Thread_', l):
        inmain = 'main-thread' in l
        if inmain: total = int(l.split()[0])
        continue
    if l.startswith('Total number in stack') or l.startswith('Sort by top'): break
    if inmain: blk.append(l)
rx = re.compile(r'^([\s+!:|]*)(\d+) (.*)$')
res = {s: 0 for s in syms}; stack = []   # (depth, symbol-matches)
for l in blk:
    m = rx.match(l)
    if not m: continue
    depth = len(m.group(1)); n = int(m.group(2)); name = m.group(3)
    while stack and stack[-1][0] >= depth: stack.pop()
    for s in syms:
        if s in name and not any(s in st[1] for st in stack): res[s] += n
    stack.append((depth, name))
print(f'main thread total samples {total}')
for s in syms: print(f'{res[s]:7d} {100.0*res[s]/total:5.1f} %  {s}')
