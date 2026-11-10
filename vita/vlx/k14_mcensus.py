#!/usr/bin/env python3
"""vita/vlx/k14_mcensus.py (kit #14 newlib, TEST ONLY): reads the vlx-only newlib census (k14_mcensus_vlx.c) of one run and prints where
newlib's heap goes, by owner.

  k14_mcensus.py <run out dir> [<census file name> ...]   (default: every k14mc-<n>.txt in order, then k14mc-peak.txt)
  k14_mcensus.py --diff <run out dir> <file A> <file B>   (what grew from A to B, by owner)

Owner = the caller of the outermost allocation entry point (malloc / eglib / a container / a mempool call), symbolized by addr2line
(k14mc-syms.txt, written by the vlx-run.sh copy that ran it). Groups: the runtime's mempool chunks (split by pool: the load context's,
the images', the rest), and every other owner by function (inlined frames joined innermost first)."""
import sys, os, re, collections

def load_syms(d):
    syms, cur, frames = {}, None, []
    p = os.path.join(d, 'k14mc-syms.txt')
    if not os.path.exists(p):
        return syms
    lines = open(p).read().split('\n')
    i = 0
    while i < len(lines):
        l = lines[i].strip()
        if l.startswith('0x') and (i + 1 >= len(lines) or not lines[i + 1].startswith('0x') or True) and re.fullmatch(r'0x[0-9a-f]+', l):
            if cur is not None:
                syms[cur] = frames
            cur = int(l, 16) + 1; frames = []; i += 1; continue
        if cur is not None and l:
            fn = l; loc = lines[i + 1].strip() if i + 1 < len(lines) else '?'
            frames.append((fn, os.path.basename(loc.split(' ')[0]))); i += 2; continue
        i += 1
    if cur is not None:
        syms[cur] = frames
    return syms

def name(syms, pc, depth=3):
    fr = syms.get(pc)
    if not fr:
        return f'0x{pc:08x}'
    inner = fr[0]; outer = fr[-1][0]
    s = f'{inner[0]}@{inner[1]}'
    if len(fr) > 1:
        s += ' <- ' + ' <- '.join(f[0] for f in fr[1:depth])
    return s

def func_of(syms, pc):
    fr = syms.get(pc)
    return fr[-1][0] if fr else f'0x{pc:08x}'

def file_of(syms, pc):
    fr = syms.get(pc)
    return fr[0][1].split(':')[0] if fr else '?'

HIST = {}
def parse(path):
    T, M, A, P, MM = {}, {}, [], [], {}
    HIST.clear()
    for l in open(path):
        f = l.split()
        if not f:
            continue
        if f[0] == 'T':
            it = iter(f[1:])
            T = {k: int(v) for k, v in zip(it, it)}
        elif f[0] == 'MM':
            MM[int(f[1])] = int(f[2], 16)
        elif f[0] == 'M':
            M[int(f[1], 16)] = (int(f[2]), int(f[3]))
        elif f[0] == 'A':
            A.append((int(f[1]), int(f[2]), int(f[3], 16), int(f[4]), int(f[5]), int(f[6])))
        elif f[0] == 'P':
            P.append((int(f[1], 16), int(f[2]), int(f[3]), int(f[4]), int(f[5]), int(f[6], 16)))
        elif f[0] == 'H':
            HIST[int(f[1])] = int(f[2])
    return T, M, A, P, MM

MEMPOOL_FUNCS = {'mono_mempool_alloc', 'mono_mempool_alloc0', 'mono_mempool_new', 'mono_mempool_new_size', 'mono_mempool_strdup'}
KIND = {1: 'MM', 2: 'IMG', 3: 'MP'}
KIB = 1024.0

def report(d, fn, syms, top=60):
    T, M, A, P, MM = parse(os.path.join(d, fn))
    print(f'=== {fn}: {T}')
    live = sum(v[0] for v in M.values())
    mp_chunks = sum(v[0] for pc, v in M.items() if func_of(syms, pc) in MEMPOOL_FUNCS)
    print(f'live newlib chunks {live / KIB:,.0f} KiB in {sum(v[1] for v in M.values()):,} chunks; of which the runtime\'s mempool chunks {mp_chunks / KIB:,.0f} KiB; the rest {(live - mp_chunks) / KIB:,.0f} KiB')
    # the rest by owner function
    byf = collections.Counter(); byfc = collections.Counter(); byfile = collections.Counter(); bysite = collections.Counter()
    for pc, (b, c) in M.items():
        f = func_of(syms, pc)
        if f in MEMPOOL_FUNCS:
            continue
        byf[f] += b; byfc[f] += c; byfile[file_of(syms, pc)] += b; bysite[name(syms, pc)] += b
    print(f'-- the rest by source file (top 25)')
    for k, v in byfile.most_common(25):
        print(f'   {v / KIB:10,.0f} KiB  {k}')
    print(f'-- the rest by owner site (top {top})')
    for k, v in bysite.most_common(top):
        print(f'   {v / KIB:10,.0f} KiB  {k}')
    # mempools
    if HIST:
        print('-- live chunks by newlib chunk size: ' + ', '.join(f'{k}B x{v:,}' for k, v in sorted(HIST.items())[:14]) + ' ...')
    if A:
        print('-- mempool bookings by kind / memory manager (KiB aligned / requested, count)')
        agg = collections.defaultdict(lambda: [0, 0, 0])
        for kind, mm, pc, al, rq, cnt in A:
            k = (KIND.get(kind, kind), mm if kind == 1 else '-')
            agg[k][0] += al; agg[k][1] += rq; agg[k][2] += cnt
        for k, (al, rq, cnt) in sorted(agg.items(), key=lambda x: -x[1][0]):
            print(f'   {k[0]} mm#{k[1]} ({MM.get(k[1], 0):#x}): {al / KIB:,.0f} / {rq / KIB:,.0f} KiB, {cnt:,} allocations')
        sites = collections.Counter(); sitec = collections.Counter()
        for kind, mm, pc, al, rq, cnt in A:
            key = f'{KIND.get(kind, kind)}{"#" + str(mm) if kind == 1 else ""} {name(syms, pc)}'
            sites[key] += al; sitec[key] += cnt
        print(f'-- mempool bookings by site (top {top}; KiB aligned, count)')
        for k, v in sites.most_common(top):
            print(f'   {v / KIB:10,.0f} KiB {sitec[k]:9,}  {k}')
    if P:
        print('-- pools (allocated by mono_mempool_get_allocated vs booked): by how they were reached')
        agg = collections.defaultdict(lambda: [0, 0, 0, 0])
        for pool, alloc, booked, cnt, how, obj in P:
            agg[how][0] += alloc; agg[how][1] += booked; agg[how][2] += cnt; agg[how][3] += 1
        for how, (alloc, booked, cnt, n) in sorted(agg.items()):
            print(f'   {["?", "MM", "IMG", "direct"][how] if how < 4 else how}: {n} pools, allocated {alloc / KIB:,.0f} KiB, booked {booked / KIB:,.0f} KiB -> slack + headers {(alloc - booked) / KIB:,.0f} KiB ({cnt:,} allocations)')
        big = sorted(P, key=lambda x: -x[1])[:8]
        for pool, alloc, booked, cnt, how, obj in big:
            print(f'   pool {pool:#x} ({["?", "MM", "IMG", "direct"][how] if how < 4 else how} {obj:#x}): allocated {alloc / KIB:,.0f} KiB, booked {booked / KIB:,.0f}, slack {(alloc - booked) / KIB:,.0f} KiB ({cnt:,} allocations)')
    return T, M, A, P

def diff(d, fa, fb, syms, top=50):
    Ta, Ma, Aa, Pa, _ = parse(os.path.join(d, fa)); Tb, Mb, Ab, Pb, _ = parse(os.path.join(d, fb))
    print(f'=== {fa} -> {fb}: live {Ta.get("live", 0) / KIB:,.0f} -> {Tb.get("live", 0) / KIB:,.0f} KiB')
    c = collections.Counter()
    for pc in set(Ma) | set(Mb):
        c[name(syms, pc)] += Mb.get(pc, (0, 0))[0] - Ma.get(pc, (0, 0))[0]
    print('-- newlib chunks: growth by owner site')
    for k, v in sorted(c.items(), key=lambda x: -x[1])[:top]:
        print(f'   {v / KIB:+10,.0f} KiB  {k}')
    ca = collections.Counter(); cb = collections.Counter()
    for kind, mm, pc, al, rq, cnt in Aa:
        ca[f'{KIND.get(kind, kind)} {name(syms, pc)}'] += al
    for kind, mm, pc, al, rq, cnt in Ab:
        cb[f'{KIND.get(kind, kind)} {name(syms, pc)}'] += al
    print('-- mempool bookings: growth by site')
    for k, v in sorted(((k, cb[k] - ca[k]) for k in set(ca) | set(cb)), key=lambda x: -x[1])[:top]:
        print(f'   {v / KIB:+10,.0f} KiB  {k}')

if __name__ == '__main__':
    a = sys.argv[1:]
    if a and a[0] == '--diff':
        d = a[1]; diff(d, a[2], a[3], load_syms(d)); sys.exit(0)
    d = a[0]; syms = load_syms(d)
    files = a[1:] or sorted((f for f in os.listdir(d) if re.fullmatch(r'k14mc-\d+\.txt', f)), key=lambda f: int(f[6:-4])) + ['k14mc-peak.txt']
    for f in files:
        report(d, f, syms)
