#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "Recorded replay fixtures"; from OpenRCT2's headless replays): the fixture suite on the Mac
# null backend.
#   run-fixtures.sh record [names]   run each fixture twice; both runs must agree on every frame (state + stream) -- then they are the
#                                    expected files: $STORE/<name>/{state-hash,stream-hash}.txt + meta.txt (engine build, env, date)
#   run-fixtures.sh check [names]    run each fixture once against $STORE; PASS = every frame's state and stream hash equal; FAIL names the
#                                    FIRST mismatching frame and which part moved (live / active / transforms / objects / behaviours /
#                                    rng / time / the render stream) and writes a state dump of that frame (SHEPHERD_STATE_DUMP) next to it
# env: FIXTURES_STORE (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/fixtures/expected: owner-local -- hashes of game output), MACBOOT_BIN
#      (the engine build to test, default the worktree's Debug build), FIXTURES_OUT (run dirs), FIXTURE_PACK_MENU / _CARD / _INTRO, P (parallel
#      runs, default 3), FIXTURE_ENV ("K=V ..." added to every run: check a switch against the recording), FIXTURES_K10=1 (kit #10: also
#      manifest-k10.tsv's rows, the ones not yet in every store; a named row is found in either file). Exit 0 = all PASS. Silent.
# Review fix (kit7-tests.md "Review fixes"): a run counts only if it ended cleanly -- macboot rc 0, the engine's END line ('done: ...') as
# engine.log's last line, non-empty state and stream hashes, and the manifest's frame count or a clean early stop at its last frame (the
# menu clock's 'stopping after frame N', the scene stop's 'END intro at frame N'). record keeps nothing unless BOTH runs are clean (two runs
# that crash at the same frame agree, and used to be recorded); check FAILs an unclean run even when its hashes match.
# Kit #11 integration fix (a full boot disk made zsh's here-document fail, the comparison never ran and the row printed PASS): the
# comparison must say it ran. first_diff's Python prints "COMPARED" (+ the first difference); a row whose comparison cannot run -- the
# here-document cannot be written, Python fails, an expected or run hash file is missing or empty -- prints "NOT COMPARED <name>: <why>"
# and counts as FAIL (check and record alike). zsh writes here-documents under TMPPREFIX (default /tmp/zsh, the system disk): this script
# keeps them next to the temp files (SHEPHERD_TMPDIR, else TMPDIR). Negative control (test only): FIXTURES_NEGCTL=nocompare points the
# comparison's TMPPREFIX at a folder that does not exist -- the here-document cannot be written, the disk-full failure exactly -- and every
# row must print NOT COMPARED and the run RESULT: FAIL.
# Kit #11 review fix (a PASS with no row compared): a named row that no manifest lists, a manifest that cannot be read, and a run in which
# no row was compared at all each print NOT COMPARED and count as FAIL -- a misspelt name (check idel) or an unreadable manifest.tsv used to
# leave the row list empty and print RESULT: PASS. Negative controls (test only): a bogus row name; FIXTURES_NEGCTL=nomanifest (the
# manifests are read from a path that does not exist).
setopt pipe_fail
TD=${SHEPHERD_TMPDIR:-${TMPDIR:-/tmp}}; mkdir -p $TD 2>/dev/null; export TMPPREFIX=${TD%/}/zsh   # (kit #11: here-documents off /tmp)
T="${0:A:h}"; TESTS="${T:h}"; WT="${TESTS:h:h}"
STORE=${FIXTURES_STORE:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/fixtures/expected}; OUTD=${FIXTURES_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/fixtures/runs}
PM=${FIXTURE_PACK_MENU:-$WT/runtime/vitapack/menu}; PC=${FIXTURE_PACK_CARD:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit6/kit-build/owner/shepherd/pack}; PI=${FIXTURE_PACK_INTRO:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit4/packs/intro}
DET=(SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join)
mode=${1:?record|check}; shift; want=("$@")
mkdir -p $STORE $OUTD
typeset -A F_FR F_PACK F_PAD F_ENV
names=(); v=PASS; ncmp=0   # (ncmp: rows whose comparison ran -- RESULT: PASS needs at least one)
# kit #10 fidelity review fix: rows not yet in every store (manifest-k10.tsv) are read only when named or with FIXTURES_K10=1 -- a plain
# check / record is kit #9's suite (manifest.tsv), green against the kit #9 store
MANIFESTS=($T/manifest.tsv)
[[ -f $T/manifest-k10.tsv ]] && { (( ${#want} )) || [[ ${FIXTURES_K10:-0} == 1 ]]; } && MANIFESTS+=($T/manifest-k10.tsv)
[[ ${FIXTURES_NEGCTL:-} == nomanifest ]] && MANIFESTS=(/nonexistent-shepherd-fixtures-negctl/manifest.tsv)   # (a negative control)
for M in $MANIFESTS; do
[[ -r $M && -f $M ]] || { echo "NOT COMPARED: cannot read the manifest $M (counted as FAIL)"; v=FAIL; continue; }
while IFS=$'\t' read -r n fr pk pad env what; do
  [[ -z $n || $n == \#* ]] && continue
  (( ${#want} )) && [[ ${want[(Ie)$n]} == 0 ]] && continue
  names+=($n); F_FR[$n]=$fr; F_PACK[$n]=$pk; F_PAD[$n]=$pad; F_ENV[$n]=$env
done < $M
done
for w in $want; do [[ ${names[(Ie)$w]} == 0 ]] && { echo "NOT COMPARED $w: not in any manifest ($MANIFESTS) (counted as FAIL)"; v=FAIL; }; done
packdir() { case $1 in menu) print $PM;; card) print $PC;; intro) print $PI;; *) print $1;; esac; }
one() {   # one <name> <run dir> [extra env...]
  local n=$1 o=$2; shift 2
  local -a e=(SHEPHERD_PACK=$(packdir $F_PACK[$n]) $DET)
  [[ $F_ENV[$n] != - ]] && e+=(${=F_ENV[$n]})
  [[ -n ${FIXTURE_ENV:-} ]] && e+=(${=FIXTURE_ENV})   # (extra env for every run: an A/B -- e.g. a switch that must change nothing)
  local pad=$F_PAD[$n]; [[ $pad != - ]] && e+=(PAD=$T/$pad)
  zsh $TESTS/macboot.sh $o $F_FR[$n] $e "$@" > $o.verdict 2>&1
}
run_problem() {   # run_problem <run dir> <manifest frames> -> "" (a whole, clean run) or why not
  local o=$1 fr=$2 rc n stop
  [[ -f $o.verdict ]] || { print "no macboot verdict"; return; }
  rc=$(grep -oE ' rc=-?[0-9]+ ' $o.verdict | tail -1 | tr -dc '0-9-')
  [[ $rc == 0 ]] || { print "macboot rc=${rc:-?} ($(tail -1 $o.verdict | cut -c1-160))"; return; }
  [[ -s $o/state-hash.txt && -s $o/stream-hash.txt ]] || { print "no state / stream hashes"; return; }
  tail -1 $o/engine.log 2>/dev/null | grep -q ' done: ' || { print "no END line (engine.log's last line is not 'done: ...')"; return; }
  n=$(wc -l < $o/state-hash.txt | tr -d ' ')
  if (( n != fr )); then
    stop=$(grep -hoE 'stopping after frame [0-9]+|END intro at frame [0-9]+' $o/engine.log | grep -oE '[0-9]+$' | tail -1)
    [[ -n $stop && $stop == $n ]] || { print "$n of $fr frames and no clean stop at frame $n"; return; }
  fi
}
first_diff() {   # first_diff <expected dir> <run dir> -> "COMPARED" + TAB "frame part" (a difference) or "COMPARED" alone; rc != 0 = not compared
  local TMPPREFIX=$TMPPREFIX   # (zsh reads the parameter itself for a here-document: a 'VAR=x cmd' prefix does not reach it)
  [[ ${FIXTURES_NEGCTL:-} == nocompare ]] && TMPPREFIX=/nonexistent-shepherd-fixtures-negctl/zsh   # (the negative control)
  python3 - $1 $2 <<'PY'
import sys, os
e, r = sys.argv[1], sys.argv[2]
cols = ["frame", "live", "active", "transforms", "objects", "behaviours", "rng", "time", "total"]
for d in (e, r):
    for f in ("state-hash.txt", "stream-hash.txt"):
        q = os.path.join(d, f)
        if not os.path.isfile(q) or os.path.getsize(q) == 0:
            print(f"missing or empty: {q}"); sys.exit(3)
def rd(p): return [l.rstrip("\n").split("\t") for l in open(p)]
es, rs = rd(e + "/state-hash.txt"), rd(r + "/state-hash.txt")
et, rt = rd(e + "/stream-hash.txt"), rd(r + "/stream-hash.txt")
best = None
for a, b in zip(es, rs):
    if a != b:
        parts = [cols[i] for i in range(1, min(len(a), len(b))) if a[i] != b[i] and cols[i] != "total"]
        best = (int(a[0]), "state: " + ",".join(parts)); break
for a, b in zip(et, rt):
    if a != b:
        if best is None or int(a[0]) < best[0]: best = (int(a[0]), "render stream")
        elif int(a[0]) == best[0]: best = (best[0], best[1] + " + render stream")
        break
if best is None and (len(es) != len(rs) or len(et) != len(rt)):
    best = (min(len(es), len(rs)) + 1, f"length: state {len(rs)} of {len(es)} frames, stream {len(rt)} of {len(et)}")
print(f"COMPARED\t{best[0]}\t{best[1]}" if best else "COMPARED")
PY
}
compared() {   # compared <first_diff's output> <its rc> -> "" (it ran) or why not
  [[ $2 == 0 && ( $1 == COMPARED || $1 == COMPARED$'\t'* ) ]] && return
  print "the comparison did not run (rc=$2${1:+: ${1[1,160]}})"
}
P=${P:-3}
typeset -a pids
run_bg() { while (( ${#pids} >= P )); do local -a alive=(); for p in $pids; do kill -0 $p 2>/dev/null && alive+=($p); done; pids=($alive); (( ${#pids} >= P )) && sleep 2; done; "$@" & pids+=($!); }
if [[ $mode == record ]]; then
  for n in $names; do run_bg one $n $OUTD/rec-$n-a; run_bg one $n $OUTD/rec-$n-b; done; wait
  for n in $names; do
    pa=$(run_problem $OUTD/rec-$n-a $F_FR[$n]); pb=$(run_problem $OUTD/rec-$n-b $F_FR[$n])
    if [[ -n $pa || -n $pb ]]; then echo "FAIL record $n: not a clean run (${pa:+a: $pa}${pa:+${pb:+; }}${pb:+b: $pb}) -- not recorded"; v=FAIL; continue; fi
    d=$(first_diff $OUTD/rec-$n-a $OUTD/rec-$n-b 2>&1); dr=$?; nc=$(compared "$d" $dr)
    if [[ -n $nc ]]; then echo "NOT COMPARED $n: $nc -- not recorded (counted as FAIL)"; v=FAIL; continue; fi
    d=${d#COMPARED}; d=${d#$'\t'}
    (( ncmp++ ))
    if [[ -n $d ]]; then echo "FAIL record $n: the two runs disagree at frame ${d%%	*} (${d#*	}) -- not deterministic, not recorded"; v=FAIL; continue; fi
    rm -rf $STORE/$n; mkdir -p $STORE/$n
    cp $OUTD/rec-$n-a/{state-hash.txt,stream-hash.txt,env.txt} $STORE/$n/ || { echo "FAIL record $n: copying the expected files failed"; rm -rf $STORE/$n; v=FAIL; continue; }
    { echo "recorded $(date '+%Y-%m-%d %H:%M') from $(git -C $WT rev-parse --short HEAD) ($(git -C $WT status --porcelain --untracked-files=no | wc -l | tr -d ' ') changed tracked files)"
      echo "engine $(shasum -a 256 ${MACBOOT_BIN:-$WT/engine/Shepherd.Engine/bin/Debug/net8.0}/Shepherd.Engine.dll | cut -c1-16); frames $(wc -l < $STORE/$n/state-hash.txt | tr -d ' '); last state $(tail -1 $STORE/$n/state-hash.txt | cut -f9) stream $(tail -1 $STORE/$n/stream-hash.txt | cut -f5)"
      echo "verdict: $(tail -1 $OUTD/rec-$n-a.verdict)"; } > $STORE/$n/meta.txt
    echo "recorded $n: $(sed -n 2p $STORE/$n/meta.txt) (two runs identical)"
  done
else
  for n in $names; do [[ -d $STORE/$n ]] || { echo "FAIL $n: no expected files in $STORE (record first)"; v=FAIL; continue; }; run_bg one $n $OUTD/chk-$n; done; wait
  for n in $names; do
    [[ -d $STORE/$n ]] || continue
    p=$(run_problem $OUTD/chk-$n $F_FR[$n])
    if [[ -n $p ]]; then echo "FAIL $n: not a clean run ($p)"; v=FAIL; continue; fi
    d=$(first_diff $STORE/$n $OUTD/chk-$n 2>&1); dr=$?; nc=$(compared "$d" $dr)
    if [[ -n $nc ]]; then echo "NOT COMPARED $n: $nc (counted as FAIL)"; v=FAIL; continue; fi
    d=${d#COMPARED}; d=${d#$'\t'}; (( ncmp++ ))
    if [[ -z $d ]]; then echo "PASS $n: $(wc -l < $OUTD/chk-$n/state-hash.txt | tr -d ' ') frames, state and stream identical to the recording"
    else
      f=${d%%	*}; echo "FAIL $n: first mismatch at frame $f (${d#*	}); dump: $OUTD/chk-$n-dump/state-hash.txt.f$f.tsv"; v=FAIL
      one $n $OUTD/chk-$n-dump SHEPHERD_STATE_DUMP=$f > /dev/null 2>&1
    fi
  done
fi
(( ncmp > 0 )) || { echo "NOT COMPARED: no row was compared (counted as FAIL)"; v=FAIL; }
echo "RESULT: $v ($ncmp row(s) compared)"
[[ $v == PASS ]]
