#!/usr/bin/env python3
"""kit #10 fidelity (vita/notes/kit10-fidelity.md "4. The loading route"): what each scene load of a run cost -- the measuring stick for kit #10's
load gains on the loading route (and on any route). Reads a run's outputs (a vlx out dir or a Mac fixture run dir): engine.log (the game's own
'Fading Loading scene <X>' line and the engine's LoadSceneAsync lines), simstats-frames.tsv (per-frame wall ms, render ms, allocated bytes,
GC counts and pauses) and the host log (the K7 BOOT line, if any).

Per load (the frame clock makes the frame numbers exact; the wall times are relative under vlx -- qemu is not timing-faithful -- and real
on the device):
  window   = from the frame the game announces the load ('Fading Loading scene X') to the first of 10 frames in a row after the target
             scene's LoadSceneAsync whose wall times are all under 2x the larger of the median frame before the announcement (120
             frames) and the median frame of the segment after the load (to the next announcement or the end): the scene settled
  wall     = the sum of the frame times in the window (ms); excess = wall minus window frames x that median (what the load added)
  alloc    = managed bytes allocated in the window; gcs = minor / major collections in it; gc ms = their pauses;
  fixed    = the bytes allocated from the announcement to LoadSceneAsync + 12 frames: a window set by frames only, so it does not move with
             the host's speed (per-frame allocations repeat within ~0.1 % run to run; SHEPHERD_STREAM_HASH adds its own ~250 KiB a frame)
usage: loadtimes.py <run dir> [<run dir> ...]   (one block per run; a run without simstats-frames.tsv prints frames only)"""
import os, re, sys, statistics, glob

def frames_tsv(d):
    p = os.path.join(d, 'simstats-frames.tsv')
    if not os.path.exists(p): return {}
    out = {}
    with open(p, errors='replace') as f:
        hdr = f.readline().rstrip('\n').split('\t')
        for line in f:
            c = line.rstrip('\n').split('\t')
            if len(c) < len(hdr): continue
            try: out[int(c[0])] = {h: float(v) for h, v in zip(hdr[1:], c[1:])}
            except ValueError: pass
    return out

def loads(d):
    p = os.path.join(d, 'engine.log')
    if not os.path.exists(p): return []
    ann, lsa = [], []
    for line in open(p, errors='replace'):
        m = re.match(r'\[\s*(\d+)\]\s+(.*)', line)
        if not m: continue
        f, t = int(m[1]), m[2]
        a = re.match(r'ULG Fading Loading scene (.+?)\s*$', t)   # the game's own log line (not the scene stop's message quoting it)
        if a: ann.append((f, a[1].strip()))
        b = re.search(r"LoadSceneAsync\('Assets/Scenes/(.+?)\.unity'", t)
        if b: lsa.append((f, b[1]))
    out = []
    for i, (fa, name) in enumerate(ann):
        nxt = ann[i + 1][0] if i + 1 < len(ann) else 10 ** 9
        fl = next((f for f, n in lsa if f >= fa and f < nxt and n == name), None)
        out.append((name, fa, fl, nxt))
    return out

def boot_line(d):
    for p in glob.glob(os.path.join(d, '*.txt')):
        if os.path.basename(p) in ('glrec.txt', 'stream-hash.txt', 'phys-hash.txt', 'state-hash.txt'): continue
        for line in open(p, errors='replace'):
            if line.startswith('K7 BOOT '):
                m = re.search(r'TOTAL ([\d.]+) s \(the Main Menu scene at f(\d+)\)', line)
                if m: return f'boot to the Main Menu {m[1]} s (f{m[2]}; K7 BOOT TOTAL)'
    return None

def main():
    for d in sys.argv[1:]:
        print(f'== {d}')
        b = boot_line(d)
        if b: print(f'  {b}')
        fr = frames_tsv(d)
        last = max(fr) if fr else None
        tot_wall = tot_ex = 0.0
        for name, fa, fl, nxt in loads(d):
            if fl is None:
                print(f'  {name:<22} announced f{fa}: no LoadSceneAsync before the next announcement / the end (the run stopped first)')
                continue
            if not fr:
                print(f'  {name:<22} announced f{fa}, LoadSceneAsync f{fl} (no simstats-frames.tsv)'); continue
            pre = [fr[f]['ms'] for f in range(max(1, fa - 120), fa) if f in fr]
            med = statistics.median(pre) if pre else 0.0
            post = [fr[f]['ms'] for f in range(fl + 1, min(nxt, last + 1)) if f in fr]
            thr = 2 * max(med, statistics.median(post) if post else 0.0)
            end, run = None, 0
            for f in range(fl + 1, min(nxt, last + 1)):
                run = run + 1 if f in fr and fr[f]['ms'] < thr else 0
                if run == 10:
                    end = f - 10; break
            if end is None: end = min(nxt - 1, last)
            win = [fr[f] for f in range(fa, end + 1) if f in fr]
            wall = sum(x['ms'] for x in win)
            ex = wall - len(win) * med
            alloc = sum(x.get('alloc_bytes', 0) for x in win)
            mi = sum(x.get('minor_gcs', 0) for x in win); ma = sum(x.get('major_gcs', 0) for x in win)
            gp = sum(x.get('gc_pause_ms', 0) for x in win)
            fixed = sum(fr[f].get('alloc_bytes', 0) for f in range(fa, fl + 13) if f in fr)
            tot_wall += wall; tot_ex += ex
            print(f'  {name:<22} f{fa}..f{end} ({end - fa + 1} frames; LoadSceneAsync f{fl}): wall {wall / 1000:8.2f} s, excess {ex / 1000:8.2f} s '
                  f'(median frame before {med:.1f} ms, settle under {thr:.0f} ms), alloc {alloc / 1048576:8.1f} MiB (fixed {fixed / 1048576:.1f}), GCs {int(mi)}/{int(ma)} ({gp / 1000:.2f} s)')
        if fr: print(f'  all loads: wall {tot_wall / 1000:.2f} s, excess {tot_ex / 1000:.2f} s; run {last} frames, {sum(x["ms"] for x in fr.values()) / 1000:.1f} s of frame time')

if __name__ == '__main__':
    main()
