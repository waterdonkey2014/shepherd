#!/usr/bin/env python3
"""k9_order.py -- device kit #9, track runtime (vita/notes/kit9-runtime.md), R5 profile-ordered AOT code layout.

  k9_order.py order <out file> [--top N] <run dir>:<log>:<from>:<to> ...
      the hot-first symbol list for the kit #9 compiler (SHEPHERD_K9_ORDER): every function sampled by VLX_PROF in the given frame windows of
      the given runs (vita/vlx/vprof.py's inputs: <run dir>/vlxprof.bin + syms.txt + the host log's P lines), all threads, by samples, hottest
      first. Runtime C functions are listed too (the compiler ignores names it does not compile). Derived from the game's runs: owner-local.
  k9_order.py pages <symsz.txt> <run dir>:<log>:<from>:<to> ...
      the layout statistic: the sampled PCs of the windows mapped onto the functions of a build (<symsz.txt>: `nm -S --defined-only` of its
      vlx ELF, written by the runtime track's vlx-run.sh next to VLX_PROF's syms.txt); for the AOT functions hit, the distinct 4 KiB pages
      and 32-byte lines their code spans (each function's whole extent: what the instruction fetches and the TLB see when they run), and the
      sample-weighted cover: how many pages hold 50 / 90 / 99 % of the samples.
  k9_order.py keep <out file> <image> <prof-calls.tsv> ...
      the CoreLib keep list for the kit #9 compiler (SHEPHERD_K9_CORELIB_KEEP): "<image><TAB><token>" for every method of <image> the
      first-call traces (SHEPHERD_K7_PROF=calls, any kind: AOT, interpreted, no jit info) saw called. Derived from the game's runs: owner-local.
  (order / keep refuse an <out file> inside the repo; derived_leaks.py fails on a git-carried file that starts with either list's header)
"""
import sys, os, struct, bisect, collections, re, subprocess

def load_run(spec):
    d, log, f0, f1 = spec.split(':'); f0, f1 = int(f0), int(f1)
    b = open(os.path.join(d, 'vlxprof.bin'), 'rb').read(); assert b[:8] == b'VLXPROF1'
    n = struct.unpack_from('<I', b, 8)[0]; off = 12 + 32 * 256
    recs = [struct.unpack_from('<IIIHH', b, off + 16 * i) for i in range(n)]
    pts = []
    for line in open(os.path.join(d, log), errors='replace'):
        m = re.match(r'^P f=(\d+) wall=([\d.]+)s', line)
        if m: pts.append((int(m.group(1)), float(m.group(2))))
    pts.sort()
    def wall(f):
        for (fa, wa), (fb, wb) in zip(pts, pts[1:]):
            if fa <= f <= fb: return wa + (wb - wa) * (f - fa) / (fb - fa)
        raise SystemExit(f'{d}: frame {f} outside the P lines')
    t0, t1 = wall(f0) * 1e4, wall(f1) * 1e4
    syms = []
    for line in open(os.path.join(d, 'syms.txt')):
        p = line.split()
        if len(p) >= 3 and p[1] in 'tTwW': syms.append((int(p[0], 16) & ~1, p[2]))
    syms.sort()
    return [r[1] for r in recs if t0 <= r[0] < t1], syms

WAITS = {'kwait', 'sceKernelDelayThread', '__sceKernelDelayThread_from_thumb', 'sys6', 'kwait_us'}

REPO = os.path.realpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
def owner_local(out):
    """Review fix: the order list and the keep list come from the game's runs (the order list names ported game methods) -- owner-local,
    never in the repo (build.sh refuses either from inside it too)."""
    o = os.path.realpath(os.path.abspath(out))
    if o == REPO or o.startswith(REPO + os.sep):
        raise SystemExit(f'k9_order.py: {out} is inside the repo ({REPO}): the list is derived from the game\'s runs -- write it to your work dir')
    return out

def order(out, top, specs):
    cnt = collections.Counter()
    for s in specs:
        pcs, syms = load_run(s); addrs = [a for a, _ in syms]
        for pc in pcs:
            i = bisect.bisect_right(addrs, pc & ~1) - 1
            if i >= 0 and syms[i][1] not in WAITS: cnt[syms[i][1]] += 1
    names = [n for n, _ in cnt.most_common(top)]
    with open(out, 'w') as f:
        f.write('# kit #9 R5: hot-first symbol order (k9_order.py order) from ' + ' '.join(specs) + '\n')
        for n in names: f.write(n + '\n')
    tot = sum(cnt.values()); cum = sum(cnt[n] for n in names)
    print(f'{out}: {len(names)} symbols ({cum} of {tot} samples, {100.0 * cum / max(tot, 1):.1f} %)')

def pages(elf, specs):
    nm = open(elf).read()
    fn = []
    for line in nm.splitlines():
        p = line.split()
        if len(p) == 4 and p[2] in 'tTwW': fn.append((int(p[0], 16) & ~1, int(p[1], 16), p[3]))
    fn.sort(); addrs = [a for a, _, _ in fn]
    cnt = collections.Counter()
    for s in specs:
        pcs, syms = load_run(s); sa = [a for a, _ in syms]
        for pc in pcs:
            # the run's own symbol for this PC, then that symbol's extent in <elf> (the runs may be of another build's ELF)
            j = bisect.bisect_right(sa, pc & ~1) - 1
            if j >= 0 and syms[j][1] not in WAITS: cnt[syms[j][1]] += 1
    ext = {n: (a, sz) for a, sz, n in fn}
    aot = {n: c for n, c in cnt.items() if n in ext and ext[n][1] > 0 and not n.startswith(('mono_', 'sgen_', 'sce', '__', 'pte_', 'vita', 'mix', 'interp_', 'do_', 'g_', 'monoeg_'))}
    tot = sum(aot.values())
    pg, ln = set(), set()
    for n in aot:
        a, sz = ext[n]
        for x in range(a >> 12, ((a + sz - 1) >> 12) + 1): pg.add(x)
        for x in range(a >> 5, ((a + sz - 1) >> 5) + 1): ln.add(x)
    # weighted: pages sorted by the samples of the functions they hold (a function's samples spread evenly over its pages)
    w = collections.Counter()
    for n, c in aot.items():
        a, sz = ext[n]; ps = list(range(a >> 12, ((a + sz - 1) >> 12) + 1))
        for x in ps: w[x] += c / len(ps)
    acc, cover = 0.0, {}
    for i, (x, c) in enumerate(w.most_common(), 1):
        acc += c
        for q in (0.5, 0.9, 0.99):
            if q not in cover and acc >= q * tot: cover[q] = i
    print(f'{elf}: {len(aot)} AOT functions hit ({tot} samples): {len(pg)} distinct 4 KiB pages ({len(pg) * 4} KiB), {len(ln)} 32-byte lines '
          f'({len(ln) * 32 // 1024} KiB); pages holding 50 / 90 / 99 % of the samples: {cover.get(0.5)} / {cover.get(0.9)} / {cover.get(0.99)}; '
          f'span of the hit functions {min(ext[n][0] for n in aot):#x}-{max(ext[n][0] + ext[n][1] for n in aot):#x}')

def keep(out, image, files):
    toks, per = set(), {}
    for f in files:
        n0 = len(toks)
        for line in open(f, errors='replace'):
            x = line.rstrip('\n').split('\t')
            if len(x) < 8 or x[0] in ('seq',) or x[0].startswith('#'): continue
            if x[4] == image and x[5] != '00000000' and re.fullmatch(r'06[0-9a-f]{6}', x[5]): toks.add(x[5])
        per[f] = len(toks) - n0
    with open(out, 'w') as w:
        w.write(f'# kit #9 CoreLib trimming: the methods of {image} the first-call traces saw called (k9_order.py keep)\n')
        for f, n in per.items(): w.write(f'# {f}: +{n}\n')
        for t in sorted(toks): w.write(f'{image}\t{t}\n')
    print(f'{out}: {len(toks)} methods of {image} from {len(files)} traces')

if __name__ == '__main__':
    a = sys.argv[1:]
    if a and a[0] == 'order':
        top = 4000
        if '--top' in a: i = a.index('--top'); top = int(a[i + 1]); del a[i:i + 2]
        order(owner_local(a[1]), top, a[2:])
    elif a and a[0] == 'pages': pages(a[1], a[2:])
    elif a and a[0] == 'keep': keep(owner_local(a[1]), a[2], a[3:])
    else: print(__doc__); sys.exit(2)
