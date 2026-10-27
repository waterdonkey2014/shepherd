#!/usr/bin/env python3
# Summarises mech.sh bench rounds: <dir>/r<k>-<cfg>.txt hold "BENCH <name> <median ns/iter> ..." lines (each the median of 7
# repetitions inside one run). Prints, per bench and configuration, the median over the rounds and the range (min..max).
import glob, os, re, statistics, sys
d, cfgs = sys.argv[1], sys.argv[2:]
data = {}
for f in glob.glob(os.path.join(d, 'r*-*.txt')):
    m = re.match(r'r(\d+)-(.+)\.txt$', os.path.basename(f))
    if not m or m.group(2) not in cfgs: continue
    for line in open(f):
        b = re.match(r'BENCH (\S+) ([0-9.]+) ns/iter', line)
        if b: data.setdefault(b.group(1), {}).setdefault(m.group(2), []).append(float(b.group(2)))
benches = [b for b in ['rect4', 'vec3ops', 'sqrmag', 'hot', 'hotprim', 'coldcallshot', 'virt16', 'local'] if b in data]
rounds = max((len(v) for b in data.values() for v in b.values()), default=0)
print('ns per iteration, median of %d interleaved rounds [range]; each round value = median of 7 repetitions in that run\n' % rounds)
print('| bench | ' + ' | '.join(cfgs) + ' |')
print('|---|' + '---:|' * len(cfgs))
for b in benches:
    cells = []
    for c in cfgs:
        v = data[b].get(c, [])
        cells.append('%.2f [%.2f..%.2f]' % (statistics.median(v), min(v), max(v)) if v else '-')
    print('| %s | %s |' % (b, ' | '.join(cells)))
