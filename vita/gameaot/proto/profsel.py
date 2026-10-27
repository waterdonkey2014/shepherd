#!/usr/bin/env python3
"""profsel.py — .aotprofile helpers for the game-aot prototype (format: mechanics/aotprof/aotprofile.py).

  profsel.py ginst <out> <in...>     keep only METHOD records of generic CLASS instances (TYPE record with a GINST), with every
                                     IMAGE / TYPE / GINST record they need (the generic-instance part of gameaotprof's profiles,
                                     which the hot study's .aotprofile files do not hold)
  profsel.py merge <out> <in...>     union of the inputs' METHOD records with their IMAGE / TYPE / GINST records (ids renumbered;
                                     unlike aotprofile.py merge it keeps generic instances)
  profsel.py stat <in>               methods per image (class instances counted apart)"""
import sys, os, collections
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "mechanics", "aotprof"))
import aotprofile as ap
IMAGE, TYPE, GINST, METHOD = ap.IMAGE, ap.TYPE, ap.GINST, ap.METHOD

def index(recs): return {r[1]: r for r in recs if r[0] != METHOD}

def canon(idx, rid, memo):
    """a structural key of a non-METHOD record (ids differ between files)"""
    if rid in memo: return memo[rid]
    r = idx[rid]
    if r[0] == IMAGE: k = ("I", r[2])
    elif r[0] == TYPE: k = ("T", canon(idx, r[2], memo), canon(idx, r[3], memo) if r[3] != -1 else None, r[4])
    elif r[0] == GINST: k = ("G", tuple(canon(idx, t, memo) for t in r[2]))
    memo[rid] = k; return k

class Out:
    def __init__(s): s.recs = []; s.ids = {}; s.n = 0; s.meth = set()
    def emit(s, idx, rid, memo):
        k = canon(idx, rid, memo)
        if k in s.ids: return s.ids[k]
        r = idx[rid]
        if r[0] == IMAGE: new = (IMAGE, None, r[2], r[3])
        elif r[0] == TYPE: new = (TYPE, None, s.emit(idx, r[2], memo), s.emit(idx, r[3], memo) if r[3] != -1 else -1, r[4])
        elif r[0] == GINST: new = (GINST, None, [s.emit(idx, t, memo) for t in r[2]])
        nid = s.n; s.n += 1; s.ids[k] = nid
        s.recs.append((new[0], nid) + new[2:]); return nid
    def method(s, idx, r, memo):
        k = (canon(idx, r[2], memo), canon(idx, r[3], memo) if r[3] != -1 else None, r[5], r[6])
        if k in s.meth: return
        s.meth.add(k)
        c = s.emit(idx, r[2], memo); g = s.emit(idx, r[3], memo) if r[3] != -1 else -1
        nid = s.n; s.n += 1; s.recs.append((METHOD, nid, c, g, r[4], r[5], r[6]))

def main(a):
    if a[0] in ("ginst", "merge"):
        o = Out()
        for f in a[2:]:
            recs = ap.read(f); idx = index(recs); memo = {}
            for r in recs:
                if r[0] != METHOD: continue
                if a[0] == "ginst" and idx[r[2]][3] == -1: continue
                o.method(idx, r, memo)
        ap.write(a[1], o.recs)
        print(f"{a[1]}: {len(o.meth)} methods, {sum(1 for r in o.recs if r[0] == GINST)} ginsts")
    elif a[0] == "stat":
        recs = ap.read(a[1]); idx = index(recs); c = collections.Counter()
        for r in recs:
            if r[0] == METHOD:
                t = idx[r[2]]; c[(idx[t[2]][2], "inst" if t[3] != -1 else "plain")] += 1
        for (img, kind), n in sorted(c.items(), key=lambda x: -x[1]): print(f"{n}\t{kind}\t{img}")
    else:
        print(__doc__); sys.exit(2)

if __name__ == "__main__":
    main(sys.argv[1:])
