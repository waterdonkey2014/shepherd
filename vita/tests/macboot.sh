#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md): one headless Mac boot of the engine in its own root -- the shared runner of the replay
# fixtures, the lockstep rig, the A* seeds and the PCT runs. A fresh copy of the sandbox saves (runtime/persistent) per run: never the real
# save folder. Silent (SHEPHERD_AUDIO=off, the Mac's output muted and checked). Logs, hashes and shots are derived game output: they stay in
# the run's out dir (the work dir, never git).
#   macboot.sh <out dir> <frames> [PAD=<pad file>] [BIN=<engine bin dir>] [HOST=1] [KEEPROOT=1] [CACHE_IN=<dir>] [ENV=VAL ...]
# CACHE_IN: copied to the root's cache/ before the run (the owner folder's cache: icall hook list, catalog bake); the root's cache/ after
# the run is always copied to <out>/cache (the power-cut end-to-end check feeds damaged copies back in).
# defaults: the Vita profile, SHEPHERD_RENDER=null (the null backend), the frame clock, the strict menu pack (runtime/vitapack/menu),
# SHEPHERD_STREAM_HASH=<out>/stream-hash.txt and SHEPHERD_STATE_HASH=<out>/state-hash.txt. HOST=1 runs it through the test host
# (vita/tests/TestHost: SHEPHERD_TESTHOST_* modes, e.g. the PCT scheduler) instead of the engine's own Main.
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h}"; R="$REPO/runtime"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
O=${1:?out dir}; O=${O:a}; FR=${2:?frames}; shift 2
BIN=${MACBOOT_BIN:-$REPO/engine/Shepherd.Engine/bin/Debug/net8.0}; HOSTBIN=${MACBOOT_HOSTBIN:-$REPO/vita/tests/TestHost/bin/Debug/net8.0}
host=0 keep=0 pad= cachein=
local -a envs extra
while (( $# )); do
  case $1 in
    PAD=*) pad=${1#PAD=} ;; BIN=*) BIN=${1#BIN=} ;; HOST=1) host=1 ;; KEEPROOT=1) keep=1 ;; CACHE_IN=*) cachein=${1#CACHE_IN=} ;; *) extra+=("$1") ;;
  esac; shift
done
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || osascript -e 'set volume output muted true' 2>/dev/null
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
rm -rf $O; mkdir -p $O/root/runtime/logs
for d in Managed ManagedVita derived; do [[ -e $R/$d ]] && ln -s $R/$d $O/root/runtime/$d; done
cp -R $R/persistent $O/root/runtime/persistent
[[ -n $cachein ]] && { mkdir -p $O/root/cache; cp -R $cachein/. $O/root/cache/; }
envs=(SHEPHERD_ROOT=$O/root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=null SHEPHERD_PACK=$R/vitapack/menu SHEPHERD_PACK_STRICT=1
  SHEPHERD_STREAM_HASH=$O/stream-hash.txt SHEPHERD_STATE_HASH=$O/state-hash.txt SHEPHERD_SHOT_DIR=$O/shots)
[[ -n $pad ]] && envs+=(SHEPHERD_PAD_FILE=$pad)
envs+=($extra)
print -l -- $envs > $O/env.txt
dll=$BIN/Shepherd.Engine.dll; (( host )) && dll=$HOSTBIN/Shepherd.TestHost.dll
s=$(date +%s)
(cd $O/root && env $envs nice dotnet $dll boot $FR 100000 > $O/console.txt 2>&1); rc=$?
cp $O/root/runtime/logs/engine.log $O/ 2>/dev/null
cp $O/root/runtime/logs/icall-hits.txt $O/root/runtime/logs/missing-icalls.txt $O/ 2>/dev/null
[[ -d $O/root/cache ]] && cp -R $O/root/cache $O/cache
(( keep )) || rm -rf $O/root
echo "$O rc=$rc $(( $(date +%s) - s ))s frames $(wc -l < $O/state-hash.txt 2>/dev/null | tr -d ' ') $(tail -1 $O/engine.log 2>/dev/null | sed 's/^\[ *[0-9]*\] *//')"
exit $rc
