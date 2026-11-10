#!/usr/bin/env python3
# device kit #14 (track small, vita/notes/kit14-small.md): the library keep list for SHEPHERD_K14_LIBTRIM=1: the given CoreLib keep list + every method (generic
# definitions included) of the library images that ran as AOT code on the kit routes' first-call traces (SHEPHERD_K7_PROF=calls, prof-calls.tsv). Owner-local output.
# usage: k14_keep.py <old keep> <out> <trace dir> ...
import glob, sys, collections
LIB = {'System.Private.CoreLib', 'System.Linq', 'System.Collections', 'System.Collections.Concurrent', 'System.Console'}
old, out, dirs = sys.argv[1], sys.argv[2], sys.argv[3:]
head = [l for l in open(old) if l.startswith('#')]; keep = {l.rstrip('\n') for l in open(old) if not l.startswith('#') and l.strip()}
n0 = len(keep); per = collections.Counter(); rows = 0
for d in dirs:
    for l in open(d + '/prof-calls.tsv', errors='replace'):
        p = l.rstrip('\n').split('\t')
        if len(p) < 8 or p[0] in ('seq',) or p[0].startswith('#'): continue
        kind, img, tok = p[3], p[4], p[5]
        if kind != 'A' or img not in LIB or tok == '00000000': continue
        rows += 1; k = img + '\t' + tok
        if k not in keep: keep.add(k); per[img] += 1
with open(out, 'w') as f:
    f.write('# kit 14 small: + the library images\' methods that ran as AOT code on the kit routes (first-call traces); the generic definitions rule their instances (SHEPHERD_AOT_KEEP_GENERIC=1)\n')
    f.writelines(head)
    for k in sorted(keep): f.write(k + '\n')
print(f'{out}: {n0} old entries + {sum(per.values())} new from {rows} A rows of {len(dirs)} traces: {dict(per)}; {len(keep)} entries')
