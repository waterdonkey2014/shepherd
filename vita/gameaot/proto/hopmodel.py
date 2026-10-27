#!/usr/bin/env python3
"""hopmodel.py — AOT<->interpreter crossings per frame for the prototype's exact AOT sets, from the hot study's exact-count
profiles (hot.sh runs: r1-<sc>-prof = interpreter inliner ON, n1-<sc>-profni = OFF) with hotan.py's transition model
(profile.md §2: interp->AOT = non-inlined calls of the inline-ON profile from interpreted to AOT code; AOT->interp = calls of the
inline-OFF profile from AOT code to interpreted code the caller's AOT compile would not inline).

  hopmodel.py <hot runs dir> <partial profile .aotprofile> [--hopfix-runs <dir>] > table

Configs (bench.sh): A engine-AOT; B + the 27 rewritten Unity modules whole; C B + the partial game/third-party modules;
P A + every owner module partial; E A + every owner module whole (= hotan C4). D/DP = C/P (LLVM changes code, not the split).
A partial module's AOT methods = the METHOD records of the profile the images were compiled from (PH95 + generic class instances
+ value-type closure), matched by (image, class, name, signature); a generic class instance counts as AOT when its definition
method is in the profile (approximation); generic METHOD instances are never in it (interpreted)."""
import sys, os, collections, argparse
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, ".."))
sys.path.insert(0, os.path.join(HERE, "..", "mechanics", "aotprof"))
import hotan, aotprofile as ap

WINDOWS = [("idle", "idle", 0, 100), ("tour", "tour", 0, 1300), ("intro / boot", "intro", 0, 805), ("intro / menu", "intro", 1, 980),
           ("intro / quote", "intro", 2, 1149), ("intro / gameplay", "intro", 3, 2066)]
GAME_GROUPS = {"unity-original", "unity-pkg", "game", "game-lib", "engine-derived"}

def profile_keys(path):
    recs = ap.read(path); idx = {r[1]: r for r in recs if r[0] != ap.METHOD}
    plain, inst = set(), set()
    for r in recs:
        if r[0] != ap.METHOD: continue
        t = idx[r[2]]; img = idx[t[2]][2]
        tn = t[4][1:] if t[4].startswith(".") else t[4]   # hotunion writes global-namespace types as ".Name"
        if t[3] == -1: plain.add((img, tn, r[5], r[6]))
        else: inst.add((img, tn, r[5]))
    return plain, inst

def main():
    a = argparse.ArgumentParser(); a.add_argument("runs"); a.add_argument("profile"); a.add_argument("--hopfix-runs")
    a = a.parse_args()
    plain, inst = profile_keys(a.profile)
    def cls(m): return (m.ns + "." if m.ns else "") + m.cls
    def partial_set(meths, groups):
        s = set()
        for f, m in meths.items():
            if m.grp not in groups: continue
            if not m.inflated and (m.img, cls(m), m.name, m.sig) in plain: s.add(f)
            elif m.inflated and (m.img, cls(m), m.name) in inst: s.add(f)
        return s
    base = {"aot-default", "engine"}
    unity = {"unity-rewritten"}
    owner = unity | GAME_GROUPS
    print("| window | config | AOT->interp / frame | interp->AOT / frame | total | top AOT->interp call sites |")
    print("|---|---|---:|---:|---:|---|")
    cache = {}
    for wname, sc, seg, frames in WINDOWS:
        for tag, rdir in (("", a.runs),) + ((("hopfix ", a.hopfix_runs),) if a.hopfix_runs else ()):
            key = (rdir, sc)
            if key not in cache:
                pr, ni = os.path.join(rdir, f"r1-{sc}-prof"), os.path.join(rdir, f"n1-{sc}-profni")
                if not (os.path.exists(pr) and os.path.exists(ni)): cache[key] = None
                else:
                    P = hotan.load(pr); N = hotan.load(ni)
                    meths = dict(P[0]); [meths.setdefault(k, v) for k, v in N[0].items()]
                    cache[key] = (meths, P[2], N[2])
            if cache[key] is None: continue
            meths, ed_on, ed_off = cache[key]
            cfgs = [("A", base, None), ("B", base | unity, None), ("C", base | unity, partial_set(meths, GAME_GROUPS)),
                    ("P", base, partial_set(meths, owner)), ("E", base | owner, None)]
            if tag: cfgs = [("H", base, None), ("HP", base, partial_set(meths, owner)), ("HC", base | unity, partial_set(meths, GAME_GROUPS))]
            for cname, groups, am in cfgs:
                a2i, i2a, sites = hotan.transitions(meths, ed_on, ed_off, seg, frames, groups, am)
                top = "; ".join(f"{hotan.short(s[2], 40)} -> {hotan.short(s[3], 40)} {s[0]:.0f}" for s in sites if s[1] == "AOT->interp")[:0]
                tops = [s for s in sites if s[1] == "AOT->interp"][:3]
                top = "; ".join(f"{hotan.short(s[3], 50)} {s[0]:.0f}" for s in tops)
                print(f"| {wname} | {cname} | {a2i:,.0f} | {i2a:,.0f} | {a2i + i2a:,.0f} | {top} |")

if __name__ == "__main__":
    main()
