#!/usr/bin/env python3
"""Kit #7 tests track (vita/notes/kit7-tests.md): compare two probe-suite result directories line by line -- the Unity 2022.3 oracle
player's (vita/tests/oracle) and Shepherd's (vita/tests/TestHost probe mode), or two Unity runs (the determinism / architecture checks).

  compare.py <A dir> <B dir> [--either <C dir>] [--suites a,b] [--show N] [--json out.json]

Each <suite>.tsv holds "<case>\t<label>\t<value>" lines (+ "#suite" / "#end" / "<case>\t#case\t<hash>" lines). Lines are matched by
(case, label). A difference is classified:
  value      the values differ (floats shown decoded: the raw bits are the comparison, the decimals are only for reading)
  nanbits    both values are NaN with different payload / sign bits (reported apart; still a difference)
  missing    a key only one side wrote (an exception path that skipped later lines, a different control flow)
--either C: B and C are the two references (Unity's arm64 and x64 players: the game ships both, and they disagree on some native maths);
an A value equal to C's where it differs from B's counts as "spread" (matches one of the original's platforms), not as a difference.
Exit 0 iff every compared suite has 0 differences of any class."""
import sys, os, struct, json, argparse, re

def load(path):
    d = {}; order = []; meta = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line: continue
            if line.startswith("#suite") or line.startswith("#end"):
                p = line.split("\t"); meta[p[0]] = p; continue
            p = line.split("\t", 2)
            if len(p) < 3: continue
            k = (p[0], p[1])
            if k in d: k = (p[0], p[1] + "#dup" + str(sum(1 for x in d if x[0] == p[0] and x[1].startswith(p[1]))))
            d[k] = p[2]; order.append(k)
    return d, order, meta

HEX8 = re.compile(r"^[0-9a-f]{8}$"); HEX16 = re.compile(r"^[0-9a-f]{16}$")
def dec1(v):
    if HEX8.match(v): return repr(struct.unpack("<f", bytes.fromhex(v)[::-1])[0])
    if HEX16.match(v): return repr(struct.unpack("<d", bytes.fromhex(v)[::-1])[0])
    return v
def decode(v):
    if "," in v and all(HEX8.match(x) or HEX16.match(x) for x in v.split(",")): return "(" + ", ".join(dec1(x) for x in v.split(",")) + ")"
    return dec1(v)
def isnan8(x):
    if not HEX8.match(x): return False
    u = int(x, 16); return (u & 0x7f800000) == 0x7f800000 and (u & 0x7fffff) != 0
def nan_only(a, b):
    xa, xb = a.split(","), b.split(",")
    if len(xa) != len(xb): return False
    diff = False
    for p, q in zip(xa, xb):
        if p == q: continue
        if isnan8(p) and isnan8(q): diff = True; continue
        return False
    return diff
def ulps(a, b):
    """For single floats or float tuples: the largest ulp distance (None when not comparable)."""
    xa, xb = a.split(","), b.split(",")
    if len(xa) != len(xb) or not all(HEX8.match(x) for x in xa + xb): return None
    m = 0
    for p, q in zip(xa, xb):
        u, v = int(p, 16), int(q, 16)
        if u == v: continue
        def key(z): return z if z < 0x80000000 else 0x80000000 - z
        if (u & 0x7f800000) == 0x7f800000 or (v & 0x7f800000) == 0x7f800000: return None
        m = max(m, abs(key(u) - key(v)))
    return m

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("a"); ap.add_argument("b"); ap.add_argument("--either"); ap.add_argument("--suites"); ap.add_argument("--show", type=int, default=6); ap.add_argument("--json")
    o = ap.parse_args()
    suites = sorted(f[:-4] for f in os.listdir(o.a) if f.endswith(".tsv") and os.path.exists(os.path.join(o.b, f)))
    if o.suites: suites = [s for s in suites if s in o.suites.split(",")]
    total = 0; report = {}
    for s in suites:
        A, oa, ma = load(os.path.join(o.a, s + ".tsv")); B, ob, mb = load(os.path.join(o.b, s + ".tsv"))
        C = load(os.path.join(o.either, s + ".tsv"))[0] if o.either and os.path.exists(os.path.join(o.either, s + ".tsv")) else {}
        spread = 0
        classes = {"value": [], "nanbits": [], "missing": []}
        labels = {}
        for k in oa:
            if k not in B: classes["missing"].append((k, A[k], None)); continue
            if A[k] != B[k]:
                if C.get(k) == A[k]: spread += 1; continue
                c = "nanbits" if nan_only(A[k], B[k]) or (k in C and nan_only(A[k], C[k])) else "value"
                classes[c].append((k, A[k], B[k]))
                base = re.sub(r"^s\d+\.", "s*.", re.sub(r"\d+$", "", k[1].split("#")[0]))
                labels[base] = labels.get(base, 0) + 1
        for k in ob:
            if k not in A: classes["missing"].append((k, None, B[k]))
        n = sum(len(v) for v in classes.values()); total += n
        cases = len({k[0] for k in oa}); casebad = len({x[0][0] for v in classes.values() for x in v})
        ha = ma.get("#end", ["", "", "", "", "", "?"])[-1]; hb = mb.get("#end", ["", "", "", "", "", "?"])[-1]
        print(f"{s}: {len(oa)} lines / {cases} cases (B {len(ob)}); suite hash A {ha} B {hb}; differences {n} in {casebad} cases (value {len(classes['value'])}, nanbits {len(classes['nanbits'])}, missing {len(classes['missing'])})" + (f"; {spread} lines = the other reference (spread)" if o.either else ""))
        if labels:
            top = sorted(labels.items(), key=lambda x: -x[1])[:12]
            print("   by label: " + ", ".join(f"{l} {c}" for l, c in top))
        for c in ("value", "nanbits", "missing"):
            for (k, va, vb) in classes[c][:o.show]:
                inp = [f"{lab[3:]}={decode(A[(k[0], lab)])}" for (cc, lab) in oa if cc == k[0] and lab.startswith("in.")][:6] if c != "missing" else []
                u = ulps(va, vb) if va and vb else None
                print(f"   [{c}] case {k[0]} {k[1]}: A {decode(va) if va else '-'} | B {decode(vb) if vb else '-'}" + (f" ({u} ulp)" if u is not None else "") + (f"   inputs {' '.join(inp)}" if inp else ""))
        report[s] = {"spread": spread, "lines": len(oa), "differences": n, "value": len(classes["value"]), "nanbits": len(classes["nanbits"]), "missing": len(classes["missing"]), "by_label": labels, "hashA": ha, "hashB": hb}
    if o.json: json.dump(report, open(o.json, "w"), indent=1)
    print(f"TOTAL differences: {total} over {len(suites)} suites")
    return 0 if total == 0 else 1

if __name__ == "__main__": sys.exit(main())
