#!/bin/zsh
# kit4 intro-plan: Mac proxies of the Vita execution models on the intro (from vita/llvm/macbench.sh): Mono 8.0.29 osx-arm64, coop, the Vita GC
# shape, Vita profile, null renderer, the strict INTRO pack (menu + New Game + intro), absolute pad frames (the frame clock lands the menu at f806).
# usage: bench.sh <interp|aoteng|aotd> <name> <SimStats from:to:heap> <stop frame> [ENV=VAL ...]   -> mac/runs/<name>/
#   interp = the default VPK (engine interpreted); aoteng = engine-AOT set (--full-aot-interp); aotd = + the owner-local derived module AOT (kit #3's device config)
setopt pipe_fail; zmodload zsh/datetime
K=${SHEPHERD_WORK:-$HOME/shepherd-work}/kit4/intro-work; M=$K/mac; export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1
c=$1 name=$2 win=$3 stop=$4; shift 4
case $c in interp) P=$M/pub-interp D=$M/Data mo=--interpreter ;; aoteng) P=$M/pub-aot-nollvm-eng D=$M/Data mo=--full-aot-interp ;; aotd) P=$M/pub-aotd D=$M/Data-aotd mo=--full-aot-interp ;; *) echo "config?"; exit 2 ;; esac
out=$M/runs/$name root=$M/roots/$name; rm -rf $out $root; mkdir -p $out $root/runtime/logs; cp -R $M/snap/persistent $root/runtime/persistent
envs=(SHEPHERD_ROOT=$root SHEPHERD_GAME_DATA=$D SHEPHERD_MANAGED=$D/Managed SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null SHEPHERD_AUDIO=off
  SHEPHERD_PACK=$M/snap/pack SHEPHERD_PACK_STRICT=1 SHEPHERD_PAD_FILE=$K/pads/intro-explore.txt SHEPHERD_SIMSTATS=$win SHEPHERD_STOP_AT=$stop
  MONO_ENV_OPTIONS=$mo MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS=major=marksweep,no-concurrent-sweep,nursery-size=4m "$@")
print -l -- config=$c $envs > $out/env.txt
t0=$EPOCHREALTIME; (cd $root && env $envs $P/Shepherd.Engine boot $(( stop + 10 )) 50 > $out/console.txt 2>&1); rc=$?
printf 'wall_s=%.3f\nrc=%d\nload1=%s\n' $(( EPOCHREALTIME - t0 )) $rc "$(sysctl -n vm.loadavg)" > $out/wall.txt
cp $root/runtime/logs/*(N) $out/ 2>/dev/null; rm -rf $root
echo "$name: rc=$rc $(grep -E '\[sim\]   frame wall' $out/engine.log | sed 's/^.*frame wall *//' | cut -c1-60) | $(grep -E '\[sim\]   render part' $out/engine.log | sed 's/^.*render part *//' | cut -c1-30) | $(grep -E '\[sim\]   fixed steps' $out/engine.log | sed 's/^.*fixed steps *//' | cut -c1-90) | $(tail -1 $out/engine.log | sed 's/^\[ *[0-9]*\] *//' | cut -c1-60)"
