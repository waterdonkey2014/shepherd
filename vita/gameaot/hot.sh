#!/bin/zsh
# game-aot study, HOT METHODS: method-level execution profiles of the vita-sim boot on the Mono INTERPRETER (the Mac proxy of the
# Vita: Mono 8.0.29 osx-arm64, coop, the Vita GC shape, SHEPHERD_PROFILE=vita, null renderer, FMOD stubs, SHEPHERD_AUDIO=off).
# Isolated: builds from this worktree, inputs are private snapshots under $H/snap, every output under $G (never the repo).
#
# usage:
#   hot.sh build [name=base]                         publish the engine from this worktree -> $H/builds/<name>/pub
#   hot.sh run <scenario> <mode> <run name> [ENV=VAL ...]    one run -> $H/runs/<run name>/
#   hot.sh rounds <n> "<scenarios>" "<modes>" <prefix>       interleaved rounds: for k, scenario, mode: run <prefix><k>-<scenario>-<mode>
# scenarios (SimStats window = the profile window; the Main Menu lands at f806 on the frame clock):
#   idle   the idle main menu, Play selected: pad-menu-dlc-rel (m+236 cross, m+586 down), window m+487..m+586 (100 frames),
#          stop m+646, strict menu pack (= menuspeed HARNESS.md §3, vita/llvm/macbench.sh)
#   tour   the menu tour (polish round 1's DLC / Credits / Roadmap / Achievements, relative): window m+494..m+1793 (1300 frames),
#          stop m+1800, strict menu pack (= menuspeed's tour window)
#   intro  polish round 1's pad-main-joy (menu -> new game -> quote -> Game Biome Intro, then move / attack / dodge), absolute
#          frames, boot 5000 frames, NO pack (the menu pack holds the menu only): the game's own Data files read through
#          symlinks ($H/snap/GameData, Managed = the private merged snapshot); window ${INTRO_WINDOW:-1:5000}, profile segments
#          at ${INTRO_SEGMENTS-806,1786,2935} (boot+title | Main Menu | QuoteScreen | Game Biome Intro: the scenes' arrival frames)
# modes:
#   plain    no profiler (frame times for calibration)
#   prof     the call profiler (vita/gameaot/tools/hotprof) + the interpreter census (vita/v6/tools/interp-census), interpreter
#            inlining ON: the calls the interpreter really makes; times (inflated) for the self-time split
#   profni   the same with --interp=-inline: every call is an event (exact call counts and caller->callee edges)
# Silent: the Vita profile has no audio library; the output-muted state is checked and logged before every run.
setopt pipe_fail
zmodload zsh/datetime
G=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot; H=$G/hot; D=${0:A:h}; WT=${D:h:h}
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR $H/runs $H/roots
GC_PARAMS=major=marksweep,no-concurrent-sweep,nursery-size=4m
PROFDIR=$H/tools/hotprof CENSUSDIR=$H/tools/census
B=${HOT_BUILD:-$H/builds/base}

build() {
  local name=${1:-base}; local O=$H/builds/$name; mkdir -p $O
  print -r -- "src=$WT commit=$(git -C $WT rev-parse HEAD) dirty=$(git -C $WT status --porcelain | grep -vc '^??')" > $O/source.txt
  dotnet publish $WT/engine/Shepherd.Engine/Shepherd.Engine.csproj -c Release -r osx-arm64 --self-contained -p:UseMonoRuntime=true \
    -p:InvariantGlobalization=true -p:PredefinedCulturesOnly=false -p:DisableTransitiveFrameworkReferenceDownloads=true \
    --source ~/.nuget/packages --disable-build-servers -p:UseSharedCompilation=false --artifacts-path $O/art -o $O/pub 2>&1 \
    | grep -E " error |Shepherd.Engine -> " || return 1
  grep -q '"System.Globalization.Invariant": true' $O/pub/Shepherd.Engine.runtimeconfig.json || { echo "publish: runtimeconfig not invariant"; return 1; }
}

tools() {
  [[ -f $PROFDIR/libmono-profiler-shepherdhot.dylib ]] || zsh $D/tools/hotprof/build.sh $PROFDIR >/dev/null || return 1
  [[ -f $CENSUSDIR/libmono-profiler-shepherd.dylib ]] || { mkdir -p $CENSUSDIR && clang -O2 -Wall -dynamiclib -arch arm64 -undefined dynamic_lookup \
    -o $CENSUSDIR/libmono-profiler-shepherd.dylib $WT/vita/v6/tools/interp-census/shepherd_interp_census.c; } || return 1
}

run() {   # run <scenario> <mode> <name> [ENV=VAL ...]
  local sc=$1 mode=$2 name=$3; shift 3
  local data=$H/snap/Data frames=4000 pad; local -a timing pack
  case $sc in
    idle)  pad=$WT/vita/v6/pad-menu-dlc-rel.txt
           timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+487:m+586:m+486 SHEPHERD_STOP_AT=m+646) ;;
    tour)  pad=$D/pads/pad-tour-rel.txt
           timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+494:m+1793:m+486 SHEPHERD_STOP_AT=m+1800) ;;
    intro) pad=$D/pads/pad-main-joy.txt data=$H/snap/GameData frames=5000
           timing=(SHEPHERD_MENU_PROBE=1040,1300,1450 SHEPHERD_SIMSTATS=${INTRO_WINDOW:-1:5000} SHEPHERD_HOT_SEGMENTS=${INTRO_SEGMENTS-806,1786,2935}) ;;
    *) echo "scenario: idle|tour|intro"; return 2 ;;
  esac
  [[ $sc != intro ]] && pack=(SHEPHERD_PACK=$H/snap/pack SHEPHERD_PACK_STRICT=1)
  local out=$H/runs/$name root=$H/roots/$name; rm -rf $out $root; mkdir -p $out $root/runtime/logs
  local muted=$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)
  [[ $muted == true ]] || { echo "$name: output NOT muted - refusing"; return 9; }
  cp -R $H/snap/persistent $root/runtime/persistent
  local mo=--interpreter; local -a extra
  case $mode in
    plain) ;;
    prof|profni) tools || return 1
           [[ $mode == profni ]] && mo="--interpreter --interp=-inline"
           mo="$mo --profile=shepherdhot:$out/hotprof.tsv --profile=shepherd:$out/census.tsv"
           extra=(DYLD_LIBRARY_PATH=$PROFDIR:$CENSUSDIR) ;;
    *) echo "mode: plain|prof|profni"; return 2 ;;
  esac
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_GAME_DATA=$data SHEPHERD_MANAGED=$H/snap/Data/Managed SHEPHERD_AUDIO=off
    SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null $pack SHEPHERD_PAD_FILE=$pad $timing
    MONO_ENV_OPTIONS=$mo MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS=$GC_PARAMS $extra "$@")
  print -l -- build=$B scenario=$sc mode=$mode muted=$muted $envs > $out/env.txt
  local t0=$EPOCHREALTIME
  (cd $root && env $envs $B/pub/Shepherd.Engine boot $frames 50 > $out/console.txt 2>&1); local rc=$?
  printf 'wall_s=%.3f\nrc=%d\nload1=%s\nmuted_after=%s\n' $(( EPOCHREALTIME - t0 )) $rc "$(sysctl -n vm.loadavg)" \
    "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" > $out/wall.txt
  cp $root/runtime/logs/*(N) $out/ 2>/dev/null; rm -rf $root
  [[ -f $out/engine.log ]] || { echo "$name: rc=$rc, no engine.log"; tail -20 $out/console.txt; return 1; }
  echo "$name: rc=$rc wall=$(printf %.1f $(( EPOCHREALTIME - t0 )))s $(tail -1 $out/engine.log | sed 's/^\[ *[0-9]*\] *//') | $(grep -m1 'window' $out/simstats.txt 2>/dev/null | cut -c1-160)"
}

case $1 in
  build) build $2 ;;
  tools) tools ;;
  run) shift; run "$@" ;;
  rounds) local n=$2 scs=(${=3}) ms=(${=4}) pf=$5
          for ((k = 1; k <= n; k++)); do for s in $scs; do for m in $ms; do run $s $m $pf$k-$s-$m || return 1; done; done; done ;;
  *) sed -n '2,26p' $0; exit 2 ;;
esac
