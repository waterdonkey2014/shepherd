#!/usr/bin/env python3
# kit #9 fidelity: see README.md (the Unity curve model; run: eval.py <this dir> <u-x64 tsv> <u-arm64 tsv> cache hermite hermite)
import sys; sys.path.insert(0, sys.argv[1])
exec(open(sys.argv[1] + '/eval.py').read().split("U = load(sys.argv[2])")[0])
exec(open(sys.argv[1] + '/big.py').read().split("cat = collections.Counter()")[0].split("U = load(sys.argv[2]); A = load(sys.argv[3])")[1])
U = load(sys.argv[2]); A = load(sys.argv[3]); t = collections.Counter(); tw = collections.Counter()
for i in range(300):
    n, keys, pre, post, xs = gen(i, True)
    c = dict(keys=keys, pre=pre, post=post); w = any(k['wm'] for k in keys)
    for j in range(12):
        want = U[i]['eval%d' % j].split('->')[1]; wa = A[i]['eval%d' % j].split('->')[1]
        for nm, fn in (('kit8', eval_shep), ('k9', eval_u)):
            d = min(ulpd(hx(fn(c, xs[j])), want), ulpd(hx(fn(c, xs[j])), wa)); b = 0 if d == 0 else 1 if d <= 16 else 2 if d <= 4096 else 3
            (tw if w else t)[(nm, b)] += 1
print('plain', sorted(t.items())); print('weighted', sorted(tw.items()))
