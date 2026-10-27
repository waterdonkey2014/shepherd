#!/usr/bin/env python3
"""Kit #7 tests track: the differences of a Shepherd-vs-Unity probe comparison, by suite and label, sized: for float results the largest ulp
distance per line (1 = last bit, 2-16 = rounding / evaluation-order noise, >16 = a different formula, NaN / sign / non-float = a different
result), for text the kind. Reads the same directories compare.py does. A and B as in compare.py (A = Shepherd, B = Unity arm64,
--either C = Unity x64)."""
import sys, os, argparse, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare import load, ulps, nan_only, decode
ap = argparse.ArgumentParser(); ap.add_argument("a"); ap.add_argument("b"); ap.add_argument("--either"); ap.add_argument("--suites"); ap.add_argument("--examples", type=int, default=1)
o = ap.parse_args()
def bucket(va, vb):
    if va is None or vb is None: return "missing"
    if nan_only(va, vb): return "nanbits"
    u = ulps(va, vb)
    if u is None:
        if va.startswith("EXC") or vb.startswith("EXC"): return "exception"
        return "text/other"
    return "1ulp" if u <= 1 else "2-16ulp" if u <= 16 else "17-4096ulp" if u <= 4096 else ">4096ulp"
order = ["1ulp", "2-16ulp", "17-4096ulp", ">4096ulp", "nanbits", "exception", "text/other", "missing"]
for s in sorted(f[:-4] for f in os.listdir(o.a) if f.endswith(".tsv") and os.path.exists(os.path.join(o.b, f))):
    if o.suites and s not in o.suites.split(","): continue
    A, oa, _ = load(os.path.join(o.a, s + ".tsv")); B, ob, _ = load(os.path.join(o.b, s + ".tsv"))
    C = load(os.path.join(o.either, s + ".tsv"))[0] if o.either else {}
    per = collections.defaultdict(collections.Counter); ex = {}
    for k in set(oa) | set(ob):
        va, vb = A.get(k), B.get(k)
        if va == vb or (C and va is not None and C.get(k) == va): continue
        lab = k[1].split("#")[0]
        if lab.startswith("s") and "." in lab and lab[1:lab.index(".")].isdigit(): lab = "s*." + lab.split(".", 1)[1].rstrip("0123456789")
        b = bucket(va, vb); per[lab][b] += 1
        ex.setdefault((lab, b), (k, va, vb))
    if not per: print(f"{s}: 0 differences"); continue
    print(f"{s}:")
    for lab, c in sorted(per.items(), key=lambda x: -sum(x[1].values())):
        print(f"   {lab:22s} " + " ".join(f"{b} {c[b]}" for b in order if c[b]))
        for b in order:
            if c[b] and o.examples and (lab, b) in ex:
                k, va, vb = ex[(lab, b)]
                print(f"      e.g. [{b}] case {k[0]}: Shepherd {decode(va)[:150] if va else '-'} | Unity {decode(vb)[:150] if vb else '-'}")
                break
