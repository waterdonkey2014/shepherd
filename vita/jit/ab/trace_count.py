#!/usr/bin/env python3
"""SHEPHERD-VITA kit #9 (track jit, vita/notes/kit9-jit.md 4.2): analyse a count-mode shepjit-traces.tsv (SHEPHERD_JIT_COUNT=1, optionally
SHEPHERD_JIT_COUNT_WIN): trace runs, coverage vs code bytes, runs per frame, what the traces stop at. The "opcodes" are an estimate (IR slots /
slots per opcode) that understates kit #9 traces -- the census is the measure. usage: trace_count.py <traces.tsv> [frames] [--win]"""
import sys, collections
path = sys.argv[1]; frames = float(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2][0].isdigit() else 0.0
win = '--win' in sys.argv
rows = []; hdr = None; head = ''
for line in open(path, errors='replace'):
    if line.startswith('#order'):
        hdr = line[1:].rstrip('\n').split('\t'); continue
    if line.startswith('#'):
        head = line; continue
    f = line.rstrip('\n').split('\t')
    if hdr and len(f) >= len(hdr) - 3:
        rows.append(dict(zip(hdr, f)))
def num(r, k, d=0.0):
    try: return float(r.get(k, d))
    except: return d
tot_runs = tot_ops = tot_bytes = 0.0; per = []
for r in rows:
    runs = num(r, 'win_runs') if win else num(r, 'runs')
    if win:
        slots = num(r, 'win_slots')
        avg = slots / runs if runs else 0
    else:
        avg = num(r, 'avg_slots')
    nops = num(r, 'ops'); full = num(r, 'full_slots'); b = num(r, 'bytes')
    spo = full / nops if nops else 3.0
    ops_run = runs * (avg / spo if spo else 0)
    tot_runs += runs; tot_ops += ops_run; tot_bytes += b
    per.append((ops_run, b, runs, r))
live = [p for p in per if p[2] > 0]
print(f"traces {len(rows)} (run at least once: {len(live)}), code {tot_bytes/1024:.0f} KiB; trace runs {tot_runs:,.0f}, opcodes run in traces ~{tot_ops:,.0f} ({tot_ops/tot_runs if tot_runs else 0:.1f} per run)")
if frames:
    print(f"per frame ({frames:.0f}): trace runs {tot_runs/frames:,.0f}, opcodes in traces ~{tot_ops/frames:,.0f}")
# coverage vs code bytes (hot first by ops per byte)
live.sort(key=lambda p: -(p[0] / max(p[1], 1)))
cum_o = cum_b = 0.0; marks = [0.5, 0.8, 0.9, 0.95, 0.99]; mi = 0; out = []
for o, b, runs, r in live:
    cum_o += o; cum_b += b
    while mi < len(marks) and cum_o >= marks[mi] * tot_ops:
        out.append(f"{int(marks[mi]*100)} % of the trace opcodes in {cum_b/1024:.0f} KiB ({live.index((o,b,runs,r))+1} traces)"); mi += 1
print("coverage: " + "; ".join(out))
# runs per trace distribution
buckets = collections.Counter()
for o, b, runs, r in per:
    rf = runs / frames if frames else runs
    k = '0' if runs == 0 else '<1/frame' if frames and rf < 1 else '1-4/frame' if frames and rf < 4 else '4-16/frame' if frames and rf < 16 else '>=16/frame' if frames else 'run'
    buckets[k] += 1
print("traces by runs per frame: " + ", ".join(f"{k} {v}" for k, v in sorted(buckets.items())))
# what the traces stop at (weighted by the runs that reached the end)
stop = collections.Counter(); stop_runs = collections.Counter(); callee = collections.Counter(); getter = collections.Counter()
for o, b, runs, r in per:
    a = r.get('abort', '?'); er = num(r, 'end_runs')
    stop[a] += 1; stop_runs[a] += er
    cd = r.get('callee', '-')
    if cd not in ('-', '?', ''):
        callee[(a, cd, r.get('callee_il', ''), r.get('callee_getter', ''))] += er
        getter[r.get('callee_getter', '')] += er
print("runs that reached the trace's end, by the instruction it stops at: " + ", ".join(f"{k} {v:,.0f}" for k, v in stop_runs.most_common(10)))
print("  ... of those at calls, the callee's IL verdict: " + ", ".join(f"{k} {v:,.0f}" for k, v in getter.most_common()))
print("top callees the traces stop at (end runs):")
for (a, cd, il, g), v in callee.most_common(25):
    print(f"  {v:>10,.0f}  {a:16} il {il:>4} {g:14} {cd[:110]}")
k9 = [p for p in per if num(p[3], 'cflags') >= 4]
if k9:
    calls = sum(num(p[3], 'calls') for p in per); intr = sum(num(p[3], 'intrinsics') for p in per); traps = sum(num(p[3], 'traps') for p in per)
    tex = sum(num(p[3], 'trap_exits') for p in per); slow = sum(num(p[3], 'slow_exits') for p in per); rec = sum(num(p[3], 'recompiles') for p in per)
    print(f"kit #9: calls compiled {calls:.0f}, intrinsics {intr:.0f}, trap sites {traps:.0f} (exits {tex:,.0f}), other slow exits {slow:,.0f}, recompiles {rec:.0f}")
