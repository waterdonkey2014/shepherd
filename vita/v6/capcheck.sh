#!/bin/zsh
# V6 stage 2 review (vita/notes/engine-pass-speed.md §8): the heap-cap probe as a regression. The vita-sim boot (sim.sh: Mono
# interpreter, coop, the Vita GC shape, the strict menu pack) must reach DLC cleanly -- 7 errors / 3 exceptions (polish round 1:
# the frame-1..3 SkeletonAnimationLODGlobalManager NRE now counts 3, vita/notes/polish-round1.md §2), no
# OutOfMemoryException, DLC selected at frame 1450 -- under max-heap-size=CAPCHECK_MIB (default 117) in every one of CAPCHECK_RUNS
# (default 3) serial runs; one run at CAPCHECK_LOW (default 112) is reported for the edge (expected to fail: informational).
# Serial on purpose: parallel runs share the machine and the stage-2 boot's GC timing (finalizer-thread trims, background threads)
# moves the edge by a few MiB. Exit 0 = every run at CAPCHECK_MIB passed. Needs sim.sh publish + stage (the current engine).
# Logs: runtime/logs/v6/capcheck-*; summary -> runtime/logs/v6/capcheck.txt. Never touches runtime/persistent (own roots).
V6="${0:A:h}"; REPO="${V6:h:h}"
CAP=${CAPCHECK_MIB:-117} RUNS=${CAPCHECK_RUNS:-3} LOW=${CAPCHECK_LOW:-112}
OUT="$REPO/runtime/logs/v6/capcheck.txt"; mkdir -p "${OUT:h}"; : > "$OUT"
echo "muted at start: $(osascript -e 'output muted of (get volume settings)')" | tee -a "$OUT"
fail=0
for i in $(seq 1 $RUNS); do
  line=$(zsh "$V6/sim.sh" cap capcheck-$CAP-r$i $CAP); echo "run $i: $line" | tee -a "$OUT"
  [[ "$line" == PASS* ]] || fail=$((fail+1))
done
[[ "$LOW" == 0 ]] || echo "edge (informational): $(zsh "$V6/sim.sh" cap capcheck-$LOW-edge $LOW)" | tee -a "$OUT"
echo "muted at end: $(osascript -e 'output muted of (get volume settings)')" | tee -a "$OUT"
if (( fail )); then echo "CAPCHECK FAIL: $fail of $RUNS runs at ${CAP}m did not pass" | tee -a "$OUT"; exit 1; fi
echo "CAPCHECK PASS: $RUNS of $RUNS runs at ${CAP}m clean (7/3, no OOM, DLC at 1450)" | tee -a "$OUT"
