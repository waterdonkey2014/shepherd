#!/usr/bin/env python3
"""vita/vlx/glclass.py (kit #13 fix, vita/notes/kit13-fix.md section 1): the VRAM requests of a VLX_GLLOG=1 run, per mspace size class -- STATIC and STREAM buffers both.

A static-only count hid that kit #12 asks the size class of the poisoned chunk 218 times and that SHEPHERD_K13_KEYART=0 makes the fatal size every frame. Counts a size class, never a safety
proof: VLX_VRAMCHECK=1 is the check (the model's FAIL frame is not the device's, and neither is any frame here).

usage: glclass.py <console> [<lo> <hi>]     (default 0xC00 0xFF0: tree bin 7 up to the 0xFF0 holes the rings leave)"""
import collections, re, sys

def nb(size):            # vitaGL's buffer block as the mspace sees it: 16-byte aligned, 4 bytes of overhead
    return (size + 4 + 15) & ~15

def rows(path):
    out = []
    for l in open(path, errors='replace'):
        m = re.match(r"\[gllog\] B f(\d+) t\w+ (\w+) size (\w+) usage (\w+)", l)
        if m: out.append((int(m[1]), m[2], nb(int(m[3], 16)), 'static' if m[4] == '88e4' else 'stream'))
    return out

def main():
    if len(sys.argv) < 2: sys.exit(__doc__)
    lo = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0xC00
    hi = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0xFF0
    r = rows(sys.argv[1])
    if not r: sys.exit('NOT COMPARED: no [gllog] B lines (the run needs VLX_GLLOG=1): ' + sys.argv[1])
    inr = [x for x in r if lo <= x[2] <= hi]
    kinds = collections.Counter(x[3] for x in inr)
    print(f'{len(r)} buffer requests, {len(inr)} sized {lo:#x}..{hi:#x}: {kinds["static"]} static, {kinds["stream"]} stream')
    g = collections.defaultdict(list)
    for f, what, n, kind in inr: g[(n, what, kind)].append(f)
    for (n, what, kind), fs in sorted(g.items()):
        print(f'  {n:#6x} {what} {kind:6} x{len(fs):<5} f{fs[0]}..f{fs[-1]}')
    per = collections.Counter(x[0] for x in inr)
    if per:   # a replaced buffer waits in vitaGL's 4 purge lists: what one 4-frame window asks is about what is live at once
        win = {f: sum(per.get(f + i, 0) for i in range(4)) for f in range(max(per) + 1)}
        top = max(win.values())
        print(f'  peak in a 4-frame purge window: {top} (first at f{min(f for f, v in win.items() if v == top)})')

if __name__ == '__main__': main()
