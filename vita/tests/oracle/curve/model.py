#!/usr/bin/env python3
"""kit #9 fidelity: AnimationCurve.Evaluate -- regenerate the oracle's d-data-curve inputs (vita/tests/drafted/active/D_Data.cs D_Curve with
OracleCore's SplitMix64 Rng) under BOTH float evaluation modes (Unity's player Mono: double intermediates; CoreCLR / Shepherd: float32 per op),
then test a model of Unity's native evaluation (float32 per op, no FMA = the x64 player) against the recorded Unity outputs."""
import sys, struct, math, collections
import numpy as np
f32 = np.float32
M64 = (1 << 64) - 1
class Rng:
    def __init__(s, seed): s.s = seed & M64
    def next(s):
        s.s = (s.s + 0x9E3779B97F4A7C15) & M64; z = s.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M64; z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M64
        return z ^ (z >> 31)
    def u32(s): return s.next() >> 32
    def int_(s, n): return 0 if n <= 1 else (s.next() >> 33) % n
    def bool_(s): return (s.next() & 1) != 0
    def chance(s, p): return s.int_(100) < p
    def unit(s): return f32((s.u32() >> 8) * (1.0 / 16777216.0))
    def exact(s, e):
        k = (s.u32() >> 8) - (1 << 23); return f32(k * 2.0 ** max(-126, min(127, e)))
    def pick(s, a): return a[s.int_(len(a))]
def seed_of(seed, suite, i):
    h = 14695981039346656037
    for c in suite: h = ((h ^ ord(c)) * 1099511628211) & M64
    r = Rng(seed ^ h ^ ((i * 0xD1B54A32D192ED03) & M64)); return r.next()
def FM(r): return f32(r.int_(721) - 360) if r.chance(20) else r.exact(-13)
INF = f32(np.inf)
WRAPS = [1, 2, 4, 8, 0]   # Clamp, Loop, PingPong, ClampForever, Default
import os
SEED = int(os.environ.get("CURVE_SEED", "2"))   # the oracle run's -seed (the model was fitted on seed 2; the second review held out 3 and 4)
def gen(i, dbl):
    """the case's keys and eval xs. dbl: Unity's Mono (double intermediates, rounding at stores / float args)."""
    r = Rng(seed_of(SEED, "d-data-curve", i))
    n = 1 + r.int_(5); keys = []; t = r.exact(-20)
    for k in range(n):
        u = r.unit()
        if dbl: t = f32(float(t) + (float(f32(0.05)) + float(u) * 2.0))
        else: t = f32(t + f32(f32(0.05) + f32(u * f32(2))))
        fm = FM(r); v = f32(fm * f32(0.01))   # one op: identical both ways
        it = INF if r.chance(10) else r.exact(-21); ot = INF if r.chance(10) else r.exact(-21)
        wm, iw, ow = 0, f32(0), f32(0)   # Keyframe(t,v,in,out): weightedMode None, weights 1/3? (Unity's ctor: 0)
        if r.chance(30): wm = r.int_(4); iw = r.unit(); ow = r.unit()
        keys.append(dict(time=t, value=v, inT=it, outT=ot, wm=wm, iw=iw, ow=ow))
    pre = r.pick(WRAPS); post = r.pick(WRAPS)
    keys.sort(key=lambda k: float(k['time']))
    t0, t1 = keys[0]['time'], keys[-1]['time']
    xs = []
    for j in range(12):
        if j < 2: xs.append(t0 if j == 0 else t1)
        else:
            u = r.unit()
            if dbl: xs.append(f32(float(t0) - 1.0 + (float(t1) - float(t0) + 2.0) * float(u)))
            else: xs.append(f32(f32(t0 - f32(1)) + f32(f32(f32(t1 - t0) + f32(2)) * u)))
    return n, keys, pre, post, xs
def hx(v): return struct.pack('>f', float(v)).hex()
def load(p):
    d = collections.defaultdict(dict)
    for l in open(p):
        x = l.rstrip('\n').split('\t')
        if len(x) >= 3 and not x[0].startswith('#'): d[int(x[0])][x[1]] = x[2]
    return d
