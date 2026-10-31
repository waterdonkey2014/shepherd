#!/usr/bin/env python3
# kit #9 fidelity: see README.md (the Unity curve model; run: eval.py <this dir> <u-x64 tsv> <u-arm64 tsv> cache hermite hermite)
import sys; sys.path.insert(0, sys.argv[1])
exec(open(sys.argv[1] + '/eval.py').read().split("U = load(sys.argv[2])")[0])
U = load(sys.argv[2]); A = load(sys.argv[3])
def u32(h): return struct.unpack('>i', bytes.fromhex(h))[0]
def ulpd(a, b):
    ia, ib = u32(a), u32(b)
    if ia < 0: ia = -2**31 - ia
    if ib < 0: ib = -2**31 - ib
    return abs(ia - ib)
cat = collections.Counter(); ex = []
for i in range(300):
    n, keys, pre, post, xs = gen(i, True)
    if any(k['wm'] for k in keys): continue
    c = dict(keys=keys, pre=pre, post=post); t0, t1 = keys[0]['time'], keys[-1]['time']
    for j in range(12):
        want = U[i]['eval%d' % j].split('->')[1]; wa = A[i]['eval%d' % j].split('->')[1]
        got = hx(eval_u(c, xs[j])); d = min(ulpd(got, want), ulpd(got, wa))
        where = 'in' if t0 < xs[j] < t1 else 'edge' if xs[j] in (t0, t1) else 'pre' if xs[j] < t0 else 'post'
        mode = {'pre': imode(pre), 'post': imode(post)}.get(where, '-')
        b = 0 if d == 0 else 1 if d <= 16 else 2 if d <= 4096 else 3
        cat[(where, mode, b)] += 1
        if b == 3 and len(ex) < 8: ex.append((i, j, n, where, mode, hx(xs[j]), want, wa, got))
for k in sorted(cat): print(k, cat[k])
for e in ex: print(e)
