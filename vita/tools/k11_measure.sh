#!/bin/zsh
# Kit #11 measure (vita/notes/kit11-measure.md): the Mac measurement runs -- the fixture routes (vita/tests/fixtures/manifest.tsv +
# manifest-k10.tsv) replayed headless with the kit #11 measurement switches, one folder per route (engine.log, state / stream hashes,
# k11/ = the rows' files), then the report tables (vita/tools/k11_report.py mac).
#   k11_measure.sh <out dir> [route ...]          default: every fixture route
# env: K11_ENV     the measurement switches, "K=V ..." (default: SHEPHERD_K11_HEAT=stream SHEPHERD_K11_COUNTERS=1)
#      K11_RENDER  null (default: the fixtures' null backend: the stream heat map + counters) | gl (the Mac's GL backend, headless: also
#                  the pixel heat map, the census and the green check)
#      MACBOOT_BIN (the engine build, as macboot.sh), FIXTURE_PACK_MENU / FIXTURE_PACK_CARD / FIXTURE_PACK_INTRO (the packs of the
#      manifest's menu / card / intro columns; menu defaults to runtime/vitapack/menu), P (parallel runs, default 2),
#      K11_FRAMES (a frame count for every route instead of the manifest's), SHEPHERD_TMPDIR (macboot.sh's temp folder)
# Every run also has the fixtures' determinism switches (SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join) and a fresh copy of the
# sandbox saves (macboot.sh): never the real save folder. Silent (macboot.sh mutes the Mac and refuses to run unmuted). The output is
# results only, but derived from the game's frames (hashes, counts; PNGs only when a png= switch asks): keep it owner-local.
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h}"; FX=$REPO/vita/tests/fixtures
O=${1:?out dir}; shift; want=("$@")
ENVS=(${=${K11_ENV:-SHEPHERD_K11_HEAT=stream SHEPHERD_K11_COUNTERS=1}})
REND=${K11_RENDER:-null}; [[ $REND == null || $REND == gl ]] || { echo "K11_RENDER must be null or gl"; exit 2; }
PM=${FIXTURE_PACK_MENU:-$REPO/runtime/vitapack/menu}; PC=${FIXTURE_PACK_CARD:-}; PI=${FIXTURE_PACK_INTRO:-}
DET=(SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join)
mkdir -p $O
typeset -A F_FR F_PACK F_PAD F_ENV; names=()
for M in $FX/manifest.tsv $FX/manifest-k10.tsv; do
  [[ -f $M ]] || continue
  while IFS=$'\t' read -r n fr pk pad env what; do
    [[ -z $n || $n == \#* ]] && continue
    (( ${#want} )) && [[ ${want[(Ie)$n]} == 0 ]] && continue
    names+=($n); F_FR[$n]=$fr; F_PACK[$n]=$pk; F_PAD[$n]=$pad; F_ENV[$n]=$env
  done < $M
done
(( ${#names} )) || { echo "no route matches: ${want[*]}"; exit 2; }
pack() { case $1 in menu) print $PM;; card) print $PC;; intro) print $PI;; *) print $1;; esac; }
one() {
  local n=$1 p=$(pack $F_PACK[$n]) fr=${K11_FRAMES:-$F_FR[$n]}
  [[ -n $p && -e $p ]] || { echo "$n: SKIPPED (no pack for '$F_PACK[$n]': set FIXTURE_PACK_${(U)F_PACK[$n]})" > $O/$n.verdict; return; }
  local -a e=(SHEPHERD_PACK=$p $DET SHEPHERD_K11_OUT=$O/$n/k11)
  [[ $F_ENV[$n] != - ]] && e+=(${=F_ENV[$n]})
  [[ $F_PAD[$n] != - ]] && e+=(PAD=$FX/$F_PAD[$n])
  [[ $REND == gl ]] && e+=(SHEPHERD_RENDER=gl)
  e+=($ENVS)
  zsh $REPO/vita/tests/macboot.sh $O/$n $fr $e > $O/$n.verdict 2>&1
}
P=${P:-2}; pids=()
run_bg() { while (( ${#pids} >= P )); do local -a alive=(); for p in $pids; do kill -0 $p 2>/dev/null && alive+=($p); done; pids=($alive); (( ${#pids} >= P )) && sleep 2; done; "$@" & pids+=($!); }
for n in $names; do run_bg one $n; done
wait
for n in $names; do print -r -- "$(tail -1 $O/$n.verdict)"; done
python3 $T/k11_report.py mac $O > $O/report.md 2>&1; echo "report: $O/report.md"
