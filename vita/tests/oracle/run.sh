#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md): run the shared probe suites on one side.
#   run.sh unity <arm64|x64> <out dir> <suites|all> <seed> <n>     the oracle player (vita/tests/oracle/setup.sh build), one fresh process
#   run.sh shepherd <out dir> <suites|all> <seed> <n> [ENV=VAL ...] the Mac test host on Shepherd's engine (Vita profile; ENV e.g.
#                                                                  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, the Vita's setting)
# Shepherd: a case that hangs (the test host's watchdog: no output for 20 s) is recorded as "<case>\t!hang\t<label>" and the suite goes on
# from the next case (a hang is a difference: Unity's lines for that case have no partner). Silent; derived output stays in <out dir>.
setopt pipe_fail
T="${0:A:h}"; TESTS="${T:h}"; WT="${TESTS:h:h}"; OR=${ORACLE_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/oracle}; TW=${TESTS_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work}
export TMPDIR=$TW/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || osascript -e 'set volume output muted true'
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
side=$1; shift
if [[ $side == unity ]]; then
  arch=$1 out=${2:a} suites=$3 seed=$4 n=$5; mkdir -p $out
  B=$OR/build/$arch/Oracle.app/Contents/MacOS/Oracle; pre=(); [[ $arch == x64 ]] && pre=(arch -x86_64)
  s=$(date +%s); timeout 3600 nice $pre $B -batchmode -nographics -logFile $out/player.log -oracle $suites -seed $seed -n $n -out $out > /dev/null 2>&1; rc=$?
  echo "unity $arch rc=$rc $(( $(date +%s) - s ))s: $(tr '\n' ' ' < $out/oracle-run.txt 2>/dev/null | cut -c1-600)"
  exit $rc
fi
out=${1:a} suites=$2 seed=$3 n=$4; shift 4; mkdir -p $out
R=$TW/probe-root; mkdir -p $R/runtime/logs; for d in Managed ManagedVita derived; do [[ -e $R/runtime/$d ]] || ln -s $WT/runtime/$d $R/runtime/$d; done
TH=$WT/vita/tests/TestHost/bin/Debug/net8.0/Shepherd.TestHost.dll
list=($suites); [[ $suites == all ]] && list=($(env SHEPHERD_ROOT=$R SHEPHERD_PROFILE=vita dotnet $TH list 2>/dev/null | grep -E '^[a-z0-9-]+$')); [[ $suites == *,* ]] && list=(${(s:,:)suites})
for s in $list; do
  first=0; : > $out/$s.tsv; : > $out/$s.icalls; seg=0
  while (( first < n )); do
    seg=$((seg + 1))
    env SHEPHERD_ROOT=$R SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off PROBE_HANG_S=${PROBE_HANG_S:-6} "$@" nice dotnet $TH probe $s $seed $((n - first)) $out/seg $first > $out/$s.seg.out 2> $out/$s.seg.err; rc=$?
    # (a segment's own header / end lines are kept only for the first / last segment)
    if [[ -f $out/seg/$s.tsv ]]; then
      if (( seg == 1 )); then grep -v '^#end' $out/seg/$s.tsv >> $out/$s.tsv; else grep -v '^#' $out/seg/$s.tsv >> $out/$s.tsv; fi
      cat $out/seg/$s.icalls >> $out/$s.icalls 2>/dev/null
    fi
    if (( rc == 4 )) && grep -q '^HANG:' $out/$s.seg.err; then
      k=$(grep '^HANG:' $out/$s.seg.err | sed -E 's/.* case ([0-9]+) after label .(.*). \(no output.*/\1/'); lab=$(grep '^HANG:' $out/$s.seg.err | sed -E "s/.* after label '(.*)' \(no output.*/\1/")
      # the hung case's partial lines are dropped (the buffered writer lost them anyway): one !hang line stands for the case
      python3 - $out/$s.tsv $k <<'PY'
import sys
p, k = sys.argv[1], sys.argv[2]
lines = [l for l in open(p, encoding="utf-8") if not l.startswith(k + "\t")]
open(p, "w", encoding="utf-8").writelines(lines)
PY
      print -r -- "$k	!hang	after $lab" >> $out/$s.tsv
      echo "shepherd $s: HANG at case $k after '$lab' -- continuing from case $((k + 1))"
      first=$((k + 1)); rm -rf $out/seg; continue
    fi
    (( rc == 0 )) || { echo "shepherd $s rc=$rc (see $out/$s.seg.err)"; }
    grep '^#end' $out/seg/$s.tsv >> $out/$s.tsv 2>/dev/null
    break
  done
  rm -rf $out/seg; echo "shepherd $s: $(grep -c . $out/$s.tsv) lines"
done
