#!/bin/zsh
# Kit #7 JIT stage 0 (vita/notes/kit7-jit.md): one engine run of a Mac execution-model proxy, from the jitstudy gain lane's harness
# (${SHEPHERD_WORK:-$HOME/shepherd-work}/jitstudy/gain/tools/bench2.sh = vita/llvm/macbench.sh's method): the stock osx-arm64 Mono 8.0.x pack, coop, the
# Vita GC shape, SHEPHERD_PROFILE=vita, the null renderer, FMOD stubs, SHEPHERD_AUDIO=off, a fresh copy of the sandbox saves per run.
# Configs = setup.sh cfg trees: I (--interpreter), J (Mono's JIT, no AOT image), A (--full-aot-interp, the Vita's model: 12 BCL + engine
# full AOT), J1 / J1E (Mono's default mode with NORMAL AOT images of the 12 BCL modules / + the engine: AOT where present, JIT the rest),
# Jnd / J1nd / J1End (the same with RuntimeFeature.IsDynamicCodeSupported=false in the runtimeconfig: NOT the Vita's behaviour, see
# setup.sh cfg -- kept to show the pin's effect).
#   bench.sh run <config> <idle|tour|intro|long> <name> [ENV=VAL ...]   -> $JIT_WORK/mac/runs/<name>/
#       CENSUS=1   load the shepjit census profiler (census/build.sh): every code allocation, trampoline, chunk -> <run>/census.*
#       HASH=1     the engine's per-frame draw-stream hash (SHEPHERD_STREAM_HASH) -> <run>/stream-hash.txt
#       SAMPLE=1   /usr/bin/sample the process (1 ms) -> <run>/sample.txt
#       STOP=<f>   override the scenario's stop frame (m+N or N)
#   bench.sh rounds <n> "<configs>" "<scenarios>" <prefix>   interleaved rounds, config order rotated every round
# Scenarios: idle / tour = the strict kit #6 card pack (menu); intro = no pack (the owner's install, read only), the survey pad, 5,000
# frames; long = no pack, pads/long-session.txt (tour + every Settings tab + New Game + a long intro loop + pause + Main Menu reload + a
# second New Game), stop at the script's end.
# The Mac stays silent: refuses to run unless the output is muted, and checks after each run that no audio library was bound.
setopt pipe_fail
zmodload zsh/datetime
HERE=${0:A:h}; REPO=${HERE:h:h:h}
W=${JIT_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work}/mac
CENSUS_DIR=${JIT_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work}/census
export TMPDIR=${JIT_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work}/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p $TMPDIR $W/runs $W/roots
GC_PARAMS=major=marksweep,no-concurrent-sweep,nursery-size=4m

run() {   # run <config> <scenario> <name> [ENV=VAL ...]
  local c=$1 sc=$2 name=$3; shift 3
  local C=$W/cfg-$c frames=4000 pad data=$W/Data; local -a timing pack
  [[ -x $C/pub/Shepherd.Engine ]] || { echo "no $C (setup.sh cfg $c)"; return 1; }
  case $sc in
    idle)  pad=$REPO/vita/llvm/pad-menu-dlc-rel.txt
           timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+487:m+586:m+486 SHEPHERD_STOP_AT=${STOP:-m+646}) ;;
    tour)  pad=$REPO/vita/gameaot/pads/pad-tour-rel.txt
           timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+494:m+1793:m+486 SHEPHERD_STOP_AT=${STOP:-m+1800}) ;;
    intro) pad=$REPO/vita/gameaot/pads/pad-main-joy.txt frames=${STOP:-5000} data=$W/GameData; pack=(SHEPHERD_STOP_SCENE=0)
           timing=(SHEPHERD_MENU_PROBE=1040,1300,1450 SHEPHERD_SIMSTATS=1:$frames) ;;
    long)  pad=$HERE/pads/long-session.txt data=$W/GameData; pack=(SHEPHERD_STOP_SCENE=0)
           local stop=${STOP:-$(sed -n '1s/.*SHEPHERD_STOP_AT=\(m+[0-9]*\).*/\1/p' $pad)}
           frames=60000; timing=(SHEPHERD_SIMSTATS=1:59999 SHEPHERD_STOP_AT=$stop) ;;
    *) echo "scenario: idle|tour|intro|long"; return 2 ;;
  esac
  [[ $sc == idle || $sc == tour ]] && pack=(SHEPHERD_PACK=$W/snap/pack SHEPHERD_PACK_STRICT=1)
  local out=$W/runs/$name root=$W/roots/$name; rm -rf $out $root; mkdir -p $out $root/runtime/logs
  local muted=$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)
  [[ $muted == true ]] || { echo "$name: output NOT muted - refusing"; return 9; }
  cp -R $W/snap/persistent $root/runtime/persistent
  local mo
  case $c in
    I) mo=--interpreter ;;
    J|J1|J1E|Jnd|J1nd|J1End) mo= ;;
    A) mo=--full-aot-interp ;;
    *) echo "config?"; return 2 ;;
  esac
  local -a extra=()
  if [[ -n ${CENSUS:-} ]]; then
    [[ -f $CENSUS_DIR/libmono-profiler-shepjit.dylib ]] || { echo "no census profiler (census/build.sh)"; return 1; }
    mo="$mo --profile=shepjit:$out/census"; extra=(DYLD_LIBRARY_PATH=$CENSUS_DIR)
  fi
  [[ -n ${HASH:-} ]] && extra+=(SHEPHERD_STREAM_HASH=$root/runtime/logs/stream-hash.txt)
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_GAME_DATA=$data SHEPHERD_MANAGED=$W/Data/Managed SHEPHERD_AUDIO=off
    SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null $pack SHEPHERD_PAD_FILE=$pad $timing
    MONO_ENV_OPTIONS="${mo# }" MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS=$GC_PARAMS $extra "$@")
  print -l -- config=$c scenario=$sc muted=$muted census=${CENSUS:-} hash=${HASH:-} sample=${SAMPLE:-} commit=$(git -C $REPO rev-parse --short HEAD) \
    cfg="$(cat $C/cfg.txt)" $envs > $out/env.txt
  local t0=$EPOCHREALTIME rc
  pushd -q $root
  env $envs nice -n 5 $C/pub/Shepherd.Engine boot $(( frames + 10 )) 50 > $out/console.txt 2>&1 &
  local pid=$!
  popd -q
  local spid=
  if [[ -n ${SAMPLE:-} ]]; then sample $pid 1800 1 -mayDie -file $out/sample.txt > $out/sample.log 2>&1 & spid=$!; fi
  wait $pid; rc=$?
  [[ -n $spid ]] && wait $spid
  printf 'wall_s=%.3f\nrc=%d\nload1=%s\nmuted_after=%s\n' $(( EPOCHREALTIME - t0 )) $rc "$(sysctl -n vm.loadavg)" \
    "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" > $out/wall.txt
  cp $root/runtime/logs/*(N) $out/ 2>/dev/null
  ( cd $root/runtime && find persistent -type f -exec md5 -r {} \; | sort -k2 ) > $out/persistent-after.md5 2>/dev/null
  rm -rf $root
  [[ -f $out/engine.log ]] || { echo "$name: rc=$rc, no engine.log"; tail -20 $out/console.txt; return 1; }
  local nb=$(sed -n '/^## 1\./,/^## 1b/p' $out/native-resolve.txt 2>/dev/null | grep -ciE 'fmod|sdl|audio|openal')
  print "audio_bindings=$nb" >> $out/wall.txt
  [[ $nb == 0 ]] || { echo "$name: audio binding found - stopping"; return 8; }
  echo "$name: rc=$rc $(python3 $HERE/runline.py $out)"
}

case $1 in
  run) shift; run "$@" ;;
  rounds) local n=$2 cs=(${=3}) scs=(${=4}) pf=$5
          for ((k = 1; k <= n; k++)); do local r=$(( (k - 1) % ${#cs} )); local -a ord=(${cs[r+1,-1]} ${cs[1,r]})
            for s in $scs; do for c in $ord; do run $c $s $pf$k-$s-$c || echo "RUN FAILED $pf$k-$s-$c"; done; done; done ;;
  *) sed -n '2,22p' $0; exit 2 ;;
esac
