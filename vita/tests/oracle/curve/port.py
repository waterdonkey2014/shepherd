#!/usr/bin/env python3
# kit #9 fidelity: see README.md (the Unity curve model; run: eval.py <this dir> <u-x64 tsv> <u-arm64 tsv> cache hermite hermite)
import sys; sys.path.insert(0, sys.argv[1])
exec(open(sys.argv[1] + '/eval.py').read().split("U = load(sys.argv[2])")[0])
S = load(sys.argv[2]); c = collections.Counter(); ex = []
for i in range(300):
    n, keys, pre, post, xs = gen(i, False)
    cc = dict(keys=keys, pre=pre, post=post); w = any(k['wm'] for k in keys)
    for j in range(12):
        got = S[i]['eval%d' % j].split('->')[1]; mine = hx(eval_u(cc, xs[j]))
        c[(w, got == mine)] += 1
        if got != mine and not w and len(ex) < 5: ex.append((i, j, got, mine))
    c[('pre', S[i]['in.pre'] == str(8 if pre == 1 else pre))] += 1
print(sorted(c.items(), key=str)); print(ex)
