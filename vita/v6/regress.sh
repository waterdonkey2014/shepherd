#!/bin/zsh
# V6 stage-2 regression set (vita/notes/engine-pass-speed.md §10), sequential; logs -> $REGRESS_DIR (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/s3/regress).
# The V4 checks, the V5 hello mac-check, the runtime unit tests, the V6 host mac-check, and (stage-2 review) the vita-sim live-heap
# distribution (SIM_RUNS serial runs: every one must read the same live heap at the menu), the heap-cap probe (capcheck.sh) and the
# screenshot identity (stage2-identity.sh). Needs: sim.sh publish + stage done for the current engine. runtime/persistent is
# md5-checked against $PERSIST_MD5 (taken before the work); the Mac output's mute state is logged at start and end.
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
REPO="${SHEPHERD_REPO:-$HOME/src/shepherd}"; cd "$REPO"
O=${REGRESS_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/s3/regress}; mkdir -p $O; : > $O/summary.txt
PERSIST_MD5=${PERSIST_MD5:-${SHEPHERD_WORK:-$HOME/shepherd-work}/s3/persistent-orig.md5}
echo "muted at start: $(osascript -e 'output muted of (get volume settings)')" >> $O/summary.txt
for t in vita/v4/check.sh vita/v4/check-gl.sh vita/v4/check-pack.sh vita/hello/mac-check/run.sh vita/runtime/tests/run-tests.sh vita/shepherd/mac-check/run.sh vita/v6/capcheck.sh; do
  n=${t//\//_}; s=$(date +%s)
  zsh $t > $O/$n.log 2>&1; rc=$?
  echo "$t rc=$rc $(( $(date +%s) - s ))s" >> $O/summary.txt
done
# live heap at the idle menu (f1300, forced full GC): the distribution over SIM_RUNS serial vita-sim runs
for i in $(seq 1 ${SIM_RUNS:-10}); do zsh vita/v6/sim.sh run regress-live-r$i > /dev/null 2>&1; done
python3 - "$REPO/runtime/logs/v6" ${SIM_RUNS:-10} >> $O/summary.txt <<'EOF'
import sys, re, collections
base, n = sys.argv[1], int(sys.argv[2]); live = []; frame = []
for i in range(1, n + 1):
    t = open(f"{base}/regress-live-r{i}/engine.log").read()
    m = re.search(r"LIVE after = ([0-9.]+) MiB", t); live.append(float(m.group(1)) if m else None)
    m = re.search(r"frame wall +mean [0-9.]+ p50 ([0-9.]+)", t); frame.append(float(m.group(1)) if m else None)
c = collections.Counter(round(x, 1) for x in live if x is not None)
print(f"vita-sim live heap at f1300 over {n} runs: " + ", ".join(f"{k:.1f} MiB x{v}" for k, v in sorted(c.items())) + (" (ONE VALUE)" if len(c) == 1 else " (SPREAD > 0.1 MiB)"))
f = sorted(x for x in frame if x is not None); print(f"vita-sim menu frame p50: min {f[0]:.2f} median {f[len(f)//2]:.2f} max {f[-1]:.2f} ms")
EOF
zsh vita/v6/stage2-identity.sh > $O/identity.log 2>&1; echo "vita/v6/stage2-identity.sh rc=$? ($(grep -c 'byte-identical' ${SHEPHERD_WORK:-$HOME/shepherd-work}/s2/identity/summary.txt) comparisons, $(grep 'byte-identical' ${SHEPHERD_WORK:-$HOME/shepherd-work}/s2/identity/summary.txt | grep -c 'differ') with differences)" >> $O/summary.txt
diff <(md5 -r $(find runtime/persistent -type f) | sort) $PERSIST_MD5 > /dev/null && echo "runtime/persistent unchanged" >> $O/summary.txt || echo "runtime/persistent CHANGED" >> $O/summary.txt
echo "muted at end: $(osascript -e 'output muted of (get volume settings)')" >> $O/summary.txt
echo done >> $O/summary.txt
