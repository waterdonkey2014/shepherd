#!/usr/bin/env python3
"""kit #9 fidelity, second review: score the Unity curve model (the "curve" fix, vita/tests/oracle/curve/eval.py eval_u = the engine's C# port)
and kit #8's evaluation against the oracle players' answers for the f2240 ease-in-out curve (run.sh's il.tsv).   compare.py <out dir>"""
import sys, os, collections
here = os.path.dirname(os.path.abspath(__file__)); cdir = os.path.join(here, '..', 'curve')
sys.path.insert(0, cdir); sys.argv = [sys.argv[0], cdir, '/dev/null'] + sys.argv[1:]
exec(open(os.path.join(cdir, 'eval.py')).read().split("U = load(sys.argv[2])")[0])
import struct
def fb(h): return f32(struct.unpack('>f', bytes.fromhex(h))[0])
out = sys.argv[3]; rows = {}
for a in ('arm64', 'x64'):
    for l in open(os.path.join(out, a, 'il.tsv')):
        x = l.rstrip('\n').split('\t')
        if x[0] == 'keys': continue
        rows.setdefault((x[0], x[1]), {})[a] = (x[2], x[3])
K = [dict(time=f32(0), value=f32(0), inT=f32(0), outT=f32(0), wm=0, iw=f32(0), ow=f32(0)), dict(time=f32(1), value=f32(1), inT=f32(0), outT=f32(0), wm=0, iw=f32(0), ow=f32(0))]
c = dict(keys=K, pre=8, post=8)
FORM = 'cache'; WRAPFORM_PP = WRAPFORM_REP = 'hermite'
cnt = collections.Counter(); ex = []
for (kind, th), v in sorted(rows.items()):
    grp = kind if kind != 'trace' else 'trace'
    want = {v[a][0] for a in v}; ez = {v[a][1] for a in v}
    t = fb(th); m = hx(eval_u(c, t)); k8 = hx(eval_shep(c, t))
    cnt[(grp, 'players agree', len(want) == 1)] += 1
    cnt[(grp, 'EaseInOut = these keys', want == ez)] += 1
    cnt[(grp, 'model exact', m in want)] += 1
    cnt[(grp, 'kit8 exact', k8 in want)] += 1
    if m not in want and len(ex) < 6: ex.append((kind, th, sorted(want), m, k8))
for k in sorted(cnt): print(k, cnt[k])
print('model misses (first 6):', ex)
