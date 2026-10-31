#!/usr/bin/env python3
"""SHEPHERD-VITA kit #9 (track jit, vita/notes/kit9-jit.md 4.1): compare opcode-census runs (build-runtime-k8.sh census / jit-census,
opcensus.tsv): interpreter dispatches, trace runs (sj_enter), the quickening / crossing candidates. usage: cen_compare.py label=<opcensus.tsv> ... [--frames N]"""
import sys
frames = 0.0; runs = []
args = sys.argv[1:]
if '--frames' in args:
    i = args.index('--frames'); frames = float(args[i + 1]); del args[i:i + 2]
for a in args:
    lab, p = a.split('=', 1); d = {}
    for l in open(p):
        f = l.rstrip('\n').split('\t')
        if len(f) >= 3 and f[0].isdigit(): d[f[2]] = int(f[0])
    runs.append((lab, d))
base = sum(runs[0][1].values()) if runs else 1
print(f"{'run':10} {'dispatches':>14} {'vs first':>8} {'sj_enter':>12} {'sj_entry+nop':>13} {'real ops':>14}" + ("   per frame: dispatches / trace runs" if frames else ""))
for lab, d in runs:
    tot = sum(d.values()); se = d.get('sj_enter', 0); sx = d.get('sj_entry', 0) + d.get('sj_nop', 0)
    pf = f"   {tot / frames:,.0f} / {se / frames:,.0f}" if frames else ""
    print(f"{lab:10} {tot:>14,} {tot / base * 100:7.1f}% {se:>12,} {sx:>13,} {tot - se - sx:>14,}{pf}")
# the quickening candidates (kit #9 STRETCH research): static loads with their init check, virtual / interface calls, type checks, calls
lab0, d0 = runs[0]
groups = {"ldsfld.* (init check each)": lambda n: n.startswith('ldsfld') or n.startswith('stsfld'),
          "callvirt.fast / interface (vtable lookup)": lambda n: n.startswith('callvirt') or 'interface' in n,
          "isinst / castclass (type check)": lambda n: n.startswith('isinst') or n.startswith('castclass'),
          "call (interp -> interp)": lambda n: n == 'call' or n.startswith('call.') and 'delegate' not in n,
          "mono_jit_call (interp -> AOT)": lambda n: n.startswith('mono_jit_call'),
          "icall / calli_nat (interp -> native)": lambda n: n.startswith('icall') or n.startswith('calli_nat'),
          "ret*": lambda n: n.startswith('ret')}
print(f"\n{lab0}: the quickening / crossing candidates (count{' per frame' if frames else ''}):")
for g, fn in groups.items():
    c = sum(v for k, v in d0.items() if fn(k))
    print(f"  {g:45} {c / frames if frames else c:>14,.0f}")
