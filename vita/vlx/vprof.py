#!/usr/bin/env python3
"""vprof.py (kit #6 track fps): reads a VLX_PROF run (<out dir>/vlxprof.bin + syms.txt + the host log's P lines) and prints, for the frames
[from, to) (placed by the P lines' wall times: one P line every 50 frames), the CPU samples per thread and per function (self), plus
inclusive-ish 'caller' attribution via lr for the top leaves.
  vprof.py <out dir> <host log name> <from frame> <to frame> [top N] [--callers fn,fn]"""
import sys, struct, bisect, collections, re, os

d, logname, F0, F1 = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
TOP = int(sys.argv[5]) if len(sys.argv) > 5 and not sys.argv[5].startswith('--') else 40
callers = []
if '--callers' in sys.argv: callers = sys.argv[sys.argv.index('--callers') + 1].split(',')
b = open(os.path.join(d, 'vlxprof.bin'), 'rb').read()
assert b[:8] == b'VLXPROF1'
n = struct.unpack_from('<I', b, 8)[0]
names = [b[12 + 32 * i:12 + 32 * i + 32].split(b'\0')[0].decode('latin1') for i in range(256)]
off = 12 + 32 * 256
recs = [struct.unpack_from('<IIIHH', b, off + 16 * i) for i in range(n)]
syms = []
for line in open(os.path.join(d, 'syms.txt')):
    p = line.split()
    if len(p) >= 3 and p[1] in 'tTwW': syms.append((int(p[0], 16) & ~1, p[2]))
syms.sort(); addrs = [a for a, _ in syms]
def sym(a):
    i = bisect.bisect_right(addrs, a & ~1) - 1
    return syms[i][1] if i >= 0 else '?%08x' % a
# P lines: frame -> wall seconds
pts = []
for line in open(os.path.join(d, logname), errors='replace'):
    m = re.match(r'^P f=(\d+) wall=([\d.]+)s', line)
    if m: pts.append((int(m.group(1)), float(m.group(2))))
pts.sort()
def wall_of(f):
    for (fa, wa), (fb, wb) in zip(pts, pts[1:]):
        if fa <= f <= fb: return wa + (wb - wa) * (f - fa) / (fb - fa)
    raise SystemExit('frame %d outside the P lines (%d..%d)' % (f, pts[0][0], pts[-1][0]))
t0, t1 = wall_of(F0) * 1e4, wall_of(F1) * 1e4
WAITS = ('kwait', 'sceKernelDelayThread', '__sceKernelDelayThread_from_thumb', 'sys6', 'kwait_us')
win_all = [r for r in recs if t0 <= r[0] < t1]
win = [r for r in win_all if sym(r[1]) not in WAITS]   # running samples only (the wall-clock sampler also lands in waits)
print('%s: %d samples in all, %d in frames %d..%d (%d running) (wall %.2f..%.2f s)' % (d, n, len(win_all), F0, F1, len(win), t0 / 1e4, t1 / 1e4))
bythr = collections.Counter(r[3] for r in win)
tot = len(win)
print('threads: ' + ', '.join('%s %d (%.1f%%)' % (names[t] if t < 256 else '?', c, 100.0 * c / tot) for t, c in bythr.most_common()))
for t, c in bythr.most_common(4):
    fs = collections.Counter(sym(r[1]) for r in win if r[3] == t)
    print('--- %s: %d samples (%.1f%% of all), %.1f per frame' % (names[t], c, 100.0 * c / tot, c / (F1 - F0)))
    for nm, k in fs.most_common(TOP): print('%6d %5.1f%%  %s' % (k, 100.0 * k / c, nm))
for fn in callers:
    cs = collections.Counter(sym(r[2]) for r in win if sym(r[1]) == fn)
    print('--- callers (lr) of %s: %d samples' % (fn, sum(cs.values())))
    for nm, k in cs.most_common(15): print('%6d  %s' % (k, nm))
