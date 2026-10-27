#!/usr/bin/env python3
"""Kit #7 tests track review fix (vita/notes/kit7-tests.md 9.2): memory numbers of vlx runs as medians and spreads, never one run against
one run. vlx's MEMSUM line (vita-run.sh / vlx-run.sh: the last heartbeat's GC heap and GC counts, MEM end's newlib used / peak and arena
used / peak) moves between runs of the SAME build with identical frames (kit #6's own repeats: mf1 vs mf3 newlib 95,543 vs 95,227 KiB), so
a difference means something only against that spread.
  vlx-memsum.py <label>=<verdict glob>[,<glob>...] ...   e.g.  ra95-k6='out/glaudio-mra95-k6-*.verdict'  ra95-t7r='out/glaudio-*ra95*-t7r*.verdict'
Prints per label: n, then per metric median [min..max]; for each pair of labels sharing a prefix before the last '-' (ra95-k6 / ra95-t7r):
the difference of medians and whether it lies inside the larger of the two spreads (max - min)."""
import sys, re, glob, statistics

PAT = {"gc_heap": r"gc heap (\d+) KiB", "gc0": r"gcs (\d+)/\d+", "gc1": r"gcs \d+/(\d+)", "newlib_used": r"newlib used=(\d+) KiB",
       "newlib_peak": r"peak (\d+)\)", "arena_used": r"arena used=(\d+) KiB", "arena_peak": r"arena used=\d+ KiB peak=(\d+)",
       # kit #8 speed (SHEPHERD_K8_HEAP, the default): the heartbeat's heap is sgen's at the last collection, labelled "gc heap@gc" -- a
       # different number from kit #7's "gc heap" (GC.GetTotalMemory: in use now); compare like with like (SHEPHERD_K8_HEAP=total = kit #7's)
       "gc_heap_gc": r"gc heap@gc (\d+) KiB"}
OPTIONAL = {"gc_heap", "gc_heap_gc"}   # (a run has one of the two)

def read(path):
    for l in open(path, errors="replace"):
        if l.startswith("MEMSUM"):
            r = {}
            for k, p in PAT.items():
                m = re.search(p, l)
                if m: r[k] = int(m.group(1))
            return r if all(k in r for k in PAT if k not in OPTIONAL) and any(k in r for k in OPTIONAL) else None
    return None

groups = {}
for a in sys.argv[1:]:
    label, _, globs = a.partition("=")
    files = sorted(f for g in globs.split(",") for f in glob.glob(g))
    rows = [(f, read(f)) for f in files]
    groups[label] = [r for f, r in rows if r]
    print(f"{label}: {len(groups[label])} runs" + (f" ({len(rows) - len(groups[label])} without a MEMSUM line)" if len(rows) != len(groups[label]) else ""))
    if not groups[label]: continue
    for k in PAT:
        if not all(k in r for r in groups[label]): continue   # (kit #8: gc_heap / gc_heap_gc -- whichever the runs have)
        v = [r[k] for r in groups[label]]
        print(f"   {k:12s} median {statistics.median(v):>9.0f}  [{min(v)}..{max(v)}]  spread {max(v) - min(v)}")
labels = list(groups)
for i, a in enumerate(labels):
    for b in labels[i + 1:]:
        if a.rsplit("-", 1)[0] != b.rsplit("-", 1)[0] or not groups[a] or not groups[b]: continue
        print(f"{b} vs {a}:")
        for k in PAT:
            if not all(k in r for r in groups[a] + groups[b]): continue
            va, vb = [r[k] for r in groups[a]], [r[k] for r in groups[b]]
            d = statistics.median(vb) - statistics.median(va); spread = max(max(va) - min(va), max(vb) - min(vb))
            print(f"   {k:12s} {d:+8.0f}  ({'inside' if abs(d) <= spread else 'OUTSIDE'} the larger spread {spread})")
