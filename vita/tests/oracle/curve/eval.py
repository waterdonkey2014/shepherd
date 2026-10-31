#!/usr/bin/env python3
# kit #9 fidelity: see README.md (the Unity curve model; run: eval.py <this dir> <u-x64 tsv> <u-arm64 tsv> cache hermite hermite)
import sys; sys.path.insert(0, sys.argv[1])
from model import *
import numpy as np
np.seterr(all='ignore')
def isfin(v): return np.isfinite(v)
def floor32(x): return f32(math.floor(float(x))) if np.isfinite(x) else x
def repeat_u(t, length): return f32(t - f32(floor32(f32(t / length)) * length))   # C++: t - floorf(t / length) * length
def pingpong_u(t, length):
    t = repeat_u(t, f32(length * f32(2))); return f32(length - f32(abs(f32(t - length))))
def imode(m): return 'clamp' if m in (1, 8) else 'pp' if m == 4 else 'rep'
POSTGE = True
FORM = sys.argv[4] if len(sys.argv) > 4 else 'hermite'
WRAPFORM_PP = sys.argv[5] if len(sys.argv) > 5 else FORM
WRAPFORM_REP = sys.argv[6] if len(sys.argv) > 6 else FORM
def wrap_u(c, x):
    k = c['keys']; t0, t1 = k[0]['time'], k[-1]['time']
    if x < t0:
        m = c['pre']; m = 2 if imode(m) == 'rep' else 4 if imode(m) == 'pp' else 1
        if m == 2: return f32(repeat_u(f32(x - t0), f32(t1 - t0)) + t0)
        if m == 4: return f32(pingpong_u(f32(x - t0), f32(t1 - t0)) + t0)
        return t0
    if x > t1 or (POSTGE and x == t1 and imode(c['post']) != 'clamp'):
        m = c['post']; m = 2 if imode(m) == 'rep' else 4 if imode(m) == 'pp' else 1
        if m == 2: return f32(repeat_u(f32(x - t0), f32(t1 - t0)) + t0)
        if m == 4: return f32(pingpong_u(f32(x - t0), f32(t1 - t0)) + t0)
        return t1
    return x
def hermite_u(t, p0, m0, m1, p1):
    t2 = f32(t * t); t3 = f32(t2 * t)
    a = f32(f32(f32(f32(2) * t3) - f32(f32(3) * t2)) + f32(1))
    b = f32(f32(t3 - f32(f32(2) * t2)) + t)
    c = f32(t3 - t2)
    d = f32(f32(f32(-2) * t3) + f32(f32(3) * t2))
    return f32(f32(f32(f32(a * p0) + f32(b * m0)) + f32(c * m1)) + f32(d * p1))
def wrap_kind(c, x):
    k = c['keys']; t0, t1 = k[0]['time'], k[-1]['time']
    if x < t0: return imode(c['pre'])
    if x > t1 or (POSTGE and x == t1 and imode(c['post']) != 'clamp'): return imode(c['post'])
    return 'none'
def eval_u(c, x):
    k = c['keys']
    if len(k) == 0: return f32(0)
    if len(k) == 1: return k[0]['value']
    x0 = x; wk = wrap_kind(c, x)
    x = wrap_u(c, x)
    global FORM
    saved = FORM
    if wk == 'pp': FORM = WRAPFORM_PP
    elif wk == 'rep': FORM = WRAPFORM_REP
    try: return eval_seg(c, x, x0)
    finally: FORM = saved
def eval_seg(c, x, x0):
    k = c['keys']
    if x >= k[-1]['time']: return k[-1]['value']
    if x <= k[0]['time']: return k[0]['value']
    # FindIndexForSampling: lhs = last key with time <= x (rhs = lhs + 1), clamped to [0, n-2]
    lhs = 0
    for j in range(len(k) - 1):
        if k[j]['time'] <= x: lhs = j
    rhs = lhs + 1
    L, R = k[lhs], k[rhs]
    if FORM in ('cache', 'cacheoff'):
        dx = f32(R['time'] - L['time']); dx = max(dx, f32(0.0001)); dy = f32(R['value'] - L['value']); length = f32(f32(1) / f32(dx * dx))
        m1 = L['outT']; m2 = R['inT']; d1 = f32(m1 * dx); d2 = f32(m2 * dx)
        c0 = f32(f32(f32(f32(d1 + d2) - dy) - dy) * f32(length / dx))
        c1 = f32(f32(f32(f32(f32(f32(dy + dy) + dy) - d1) - d1) - d2) * length)
        c2 = m1; c3 = L['value']
        if not isfin(L['outT']) or not isfin(R['inT']): c0 = c1 = c2 = f32(0); c3 = L['value']
        t = f32(x - L['time'])
        if FORM == 'cacheoff':
            off = f32(x0 - x); ct = f32(L['time'] + off); t = f32(x0 - ct)
        return f32(f32(t * f32(f32(t * f32(f32(t * c0) + c1)) + c2)) + c3)
    dx = f32(R['time'] - L['time'])
    if dx != 0: t = f32(f32(x - L['time']) / dx); m1 = f32(L['outT'] * dx); m2 = f32(R['inT'] * dx)
    else: t = f32(0); m1 = f32(0); m2 = f32(0)
    out = hermite_u(t, L['value'], m1, m2, R['value'])
    if not isfin(L['outT']) or not isfin(R['inT']): out = L['value']
    return out
def eval_shep(c, x):   # kit #8 (engine/Shepherd.Engine/Icalls/DataIcalls.cs), float32
    k = c['keys']
    if len(k) == 0: return f32(0)
    if len(k) == 1: return k[0]['value']
    t0, t1 = k[0]['time'], k[-1]['time']; ln = f32(t1 - t0); t = x
    def mrep(t, l): return f32(min(max(f32(t - f32(floor32(f32(t / l)) * l)), f32(0)), l))
    def mpp(t, l): t = mrep(t, f32(l * f32(2))); return f32(l - f32(abs(f32(t - l))))
    def wrap(m, t):
        if ln <= 0: return t0
        if m == 2: return f32(t0 + mrep(f32(t - t0), ln))
        if m == 4: return f32(t0 + mpp(f32(t - t0), ln))
        return t
    if t < t0: t = wrap(c['pre'], t)
    elif t > t1: t = wrap(c['post'], t)
    if t <= t0: return k[0]['value']
    if t >= t1: return k[-1]['value']
    i = 0
    while i < len(k) - 2 and t > k[i + 1]['time']: i += 1
    a, b = k[i], k[i + 1]; dt = f32(b['time'] - a['time'])
    if dt <= 0: return b['value']
    if np.isinf(a['outT']) or np.isinf(b['inT']): return a['value']
    s = f32(f32(t - a['time']) / dt); s2 = f32(s * s); s3 = f32(s2 * s)
    h00 = f32(f32(f32(f32(2) * s3) - f32(f32(3) * s2)) + f32(1)); h10 = f32(f32(s3 - f32(f32(2) * s2)) + s)
    h01 = f32(f32(f32(-2) * s3) + f32(f32(3) * s2)); h11 = f32(s3 - s2)
    return f32(f32(f32(f32(h00 * a['value']) + f32(f32(h10 * a['outT']) * dt)) + f32(h01 * b['value'])) + f32(f32(h11 * b['inT']) * dt))
U = load(sys.argv[2]); A = load(sys.argv[3]) if len(sys.argv) > 3 else None
cnt = collections.Counter(); ex = collections.defaultdict(list)
for i in range(300):
    n, keys, pre, post, xs = gen(i, True)
    c = dict(keys=keys, pre=pre, post=post)
    weighted = any(k['wm'] for k in keys)
    for j in range(12):
        want = U[i]['eval%d' % j].split('->')[1]
        wa = A[i]['eval%d' % j].split('->')[1] if A else None
        for name, fn in (('model', eval_u), ('kit8', eval_shep)):
            got = hx(fn(c, xs[j]))
            cat = ('weighted' if weighted else 'plain')
            ok = got == want or got == wa
            cnt[(name, cat, ok)] += 1
            if not ok and len(ex[(name, cat)]) < 3: ex[(name, cat)].append((i, j, n, pre, post, hx(xs[j]), want, wa, got))
for k in sorted(cnt): print(k, cnt[k])
for k, v in ex.items(): print(k, v)
