#!/usr/bin/env python3
"""k13_idle_profile.py -- device kit #13, track speed (vita/notes/kit13-speed.md): the reader for the IDLE MENU PROFILE step (Shepherd
Measure with SHEPHERD_K7_PROF=time,win=1677:1776, vita/tools/k13_speed_env.py's m-prof arm).

Reads the profiled run's <logs>/prof.tsv (Diag/K7Prof.cs) -- only its "[window ...]" rows, the frames of the [sim] window -- and its
simstats.txt, and prints what the Vita's idle-menu frame is made of: the window's frame time, the engine's zones by self time (kit #13 splits
"render: layout + canvases" into root canvases / rect size messages / willRenderCanvases), the behaviour types (all their messages and
coroutines) by self time, the coroutine bodies, and the GC. With --control <logs of Measure as shipped> every row also gets an ESTIMATE
column scaled to the unprofiled window's frame time (the profiler's own cost removed in proportion: an ESTIMATE, never DEVICE).
Labels: the profiled run's numbers are what the device measured under the profiler (DEVICE when the logs are the owner's).
usage: k13_idle_profile.py <profiled logs dir> [--control <logs dir>] [--top N]"""
import argparse, collections, os, re, sys

ap = argparse.ArgumentParser()
ap.add_argument('logs'); ap.add_argument('--control'); ap.add_argument('--top', type=int, default=25)
a = ap.parse_args()

def sim(d):
    """the [sim] window's frame wall / render / logic means (simstats.txt), or None"""
    p = os.path.join(d, 'simstats.txt')
    if not os.path.isfile(p): return None
    r = {}
    for l in open(p, errors='replace'):
        l = l.replace('[sim]', '').strip()
        m = re.match(r'(frame wall|render part|logic part)\s+mean ([\d.]+) p50 ([\d.]+)', l)
        if m: r[m.group(1)] = (float(m.group(2)), float(m.group(3)))
        m = re.match(r'window frames (\d+)\.\.(\d+)', l)
        if m: r['window'] = (int(m.group(1)), int(m.group(2)))
    return r or None

p = os.path.join(a.logs, 'prof.tsv')
if not os.path.isfile(p): sys.exit(f'NOT READ: {p} missing (was SHEPHERD_K7_PROF set? read-logs.sh copies prof.tsv with the logs)')
rows = []; frames = 0; wall = gc = 0.0; win = None
for l in open(p, errors='replace'):
    if l.startswith('#') or l.startswith('scene\t'): continue
    f = l.rstrip('\n').split('\t')
    if len(f) < 9 or not f[0].startswith('[window'): continue
    win = f[0].split(']')[0] + ']'
    kind, name, calls, incl, self_ = f[2], f[3], int(f[4]), float(f[5]), float(f[6])
    if kind == 'scene': frames += int(f[1]); wall += incl; gc += self_; continue
    rows.append((kind, name, calls, incl, self_, int(f[1])))
if not rows: sys.exit(f'NOT READ: {p} has no [window ...] rows (the window frames were never reached?)')
nf = max(r[5] for r in rows)
zones = collections.Counter(); types = collections.Counter(); coro = collections.Counter(); calls = collections.Counter()
for kind, name, c, incl, s, n in rows:
    if kind == 'zone': zones[name] += s
    elif kind in ('type', 'coro'):
        own = name.replace(' [+SkeletonUtility]', '').replace(' [coroutine]', '')
        own = own.rsplit('.', 1)[0] if '.' in own else own
        types[own] += s; calls[own] += c
        if kind == 'coro': coro[name] += s
fwall = wall / frames if frames else sum(zones.values()) / nf
ctl = sim(a.control) if a.control else None; me = sim(a.logs)
k = (ctl['frame wall'][0] / fwall) if ctl and 'frame wall' in ctl and fwall > 0 else None
def ms(x): return x / nf
def est(x): return f'   ESTIMATE {ms(x) * k:6.2f}' if k else ''
print(f'k13_idle_profile: {win} {nf} frames of {a.logs}')
print(f'  frame wall under the profiler {fwall:.2f} ms' + (f'; this run\'s [sim] window {me["frame wall"][0]:.2f} ms (render {me["render part"][0]:.2f}, logic {me["logic part"][0]:.2f})' if me and 'frame wall' in me else '')
      + (f'; CONTROL (unprofiled) {ctl["frame wall"][0]:.2f} ms (render {ctl["render part"][0]:.2f}, logic {ctl["logic part"][0]:.2f}) -> ESTIMATE scale x{k:.3f}' if k else ''))
if gc: print(f'  GC pauses in the window: {gc / max(frames, 1):.2f} ms a frame')
print('  -- engine zones (self ms / frame) --')
for n, s in zones.most_common():
    if ms(s) >= 0.005: print(f'    {ms(s):7.3f}  {100 * ms(s) / fwall:5.1f} %  {n}{est(s)}')
print(f'  -- behaviour types (all their messages + coroutines; self ms / frame), top {a.top} --')
for n, s in types.most_common(a.top): print(f'    {ms(s):7.3f}  {100 * ms(s) / fwall:5.1f} %  {n} ({calls[n] / nf:.1f} calls / frame){est(s)}')
if coro:
    print('  -- coroutine bodies (self ms / frame), top 10 --')
    for n, s in coro.most_common(10): print(f'    {ms(s):7.3f}  {n}')
tz, tt = sum(zones.values()), sum(types.values())
print(f'  totals: zones {ms(tz):.2f} + behaviours {ms(tt):.2f} = {ms(tz + tt):.2f} ms of the {fwall:.2f} ms frame (the rest: the boot loop between zones)')
