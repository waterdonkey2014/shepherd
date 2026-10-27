#!/bin/zsh
# kit #7 tests track: the combat-flake experiment (vita/notes/kit7-tests.md): the drive without the switch (host timing decides the A*
# return frames), with SHEPHERD_TEST_ASTAR=join / drain (deterministic), and with hold:<seed> (seeded slow-path-thread schedules, each run
# twice to show it replays). P runs at once (the flake grows with the Mac's load). -> $TW/combat/summary.txt
T="${0:A:h}"; TW=${TESTS_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work}; P=${P:-3}
DEFAULT_RUNS="base-1 base-2 base-3 base-4 base-5 base-6 join-1 join-2 join-3 join-4 drain-1 drain-2 hold1-a hold1-b hold2-a hold2-b hold3-a hold4-a hold5-a hold6-a"
L=(${=${RUNS:-$DEFAULT_RUNS}})
typeset -a pids
for t in $L; do
  e=()
  # base-*: no switch; join-* / drain-*: SHEPHERD_TEST_ASTAR only; spine-*: SHEPHERD_TEST_SPINE=join only; both-* / bothd-*: both (A* join /
  # drain); holdS-x: A* hold:S with the spine switch on (only the A* schedule varies); aholdS-x: A* hold:S alone
  case $t in
    join-*) e=(SHEPHERD_TEST_ASTAR=join);; drain-*) e=(SHEPHERD_TEST_ASTAR=drain);; spine-*) e=(SHEPHERD_TEST_SPINE=join);;
    both-*) e=(SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join);; bothd-*) e=(SHEPHERD_TEST_ASTAR=drain SHEPHERD_TEST_SPINE=join);;
    ahold*) s=${t#ahold}; s=${s%%-*}; e=(SHEPHERD_TEST_ASTAR=hold:$s:3);;
    hold*) s=${t#hold}; s=${s%%-*}; e=(SHEPHERD_TEST_ASTAR=hold:$s:3 SHEPHERD_TEST_SPINE=join);;
  esac
  while (( ${#pids} >= P )); do alive=(); for p in $pids; do kill -0 $p 2>/dev/null && alive+=($p); done; pids=($alive); (( ${#pids} >= P )) && sleep 5; done
  zsh $T/astar-combat.sh run $t $e >> $TW/combat/runs.log 2>&1 & pids+=($!); sleep 3
done
wait
python3 - $TW/combat > $TW/combat/summary.txt <<'PY'
import sys, os, glob
d = sys.argv[1]; golden = "913dc162f6e72356"
runs = sorted(x for x in os.listdir(d) if os.path.isfile(os.path.join(d, x, "pose.txt")))
ref = open(os.path.join(d, runs[0], "pose.txt")).read().splitlines() if runs else []
gold_lines = None
for r in runs:
    l = open(os.path.join(d, r, "pose.txt")).read().splitlines()
    if l and golden in l[-1]: gold_lines = l; break
if gold_lines is None and os.environ.get("GOLD_POSE"): gold_lines = open(os.environ["GOLD_POSE"]).read().splitlines()   # a golden run's pose.txt from an earlier batch
for r in runs:
    l = open(os.path.join(d, r, "pose.txt")).read().splitlines()
    last = l[-1].split()[-1] if l else "-"
    first = "-"
    if gold_lines:
        for a, b in zip(l, gold_lines):
            if a != b: first = a.split()[1] if len(a.split()) > 1 else a; break
    v = open(os.path.join(d, r, "verdict.txt")).read().strip() if os.path.exists(os.path.join(d, r, "verdict.txt")) else ""
    load = v.split(" load ")[1].split()[0] if " load " in v else "?"
    bad = " SETUP MISMATCH (invalid run)" if os.path.exists(os.path.join(d, r, "mismatch.txt")) else ""
    print(f"{r}\tlast {last}\t{'= golden' if last == golden else 'DIFFERS from golden, first differing step ' + first}\tload {load}{bad}")
PY
cat $TW/combat/summary.txt
