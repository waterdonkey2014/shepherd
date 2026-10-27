#!/bin/zsh
# SHEPHERD game-AOT study: which owner assemblies / methods the menu + intro execute, on the vita-sim shape (osx-arm64 Mono 8.0.29
# INTERPRETER, coop, the Vita GC shape, SHEPHERD_PROFILE=vita, null renderer, silent: SHEPHERD_AUDIO=off + the Vita profile's FMOD
# stubs). NO pack: the intro is outside the menu pack, so the bundles are read from the owner's game Data through symlinks (read
# only); Managed/ is a private merged copy (game Managed + runtime/ManagedVita + Shepherd.Engine.Derived). The sandbox saves are a
# fresh copy of runtime/persistent per run (the real CotL save dir is never touched).
# usage: census-run.sh stage                       build $GA_OUT/intro/Data (once)
#        census-run.sh run <name> [frames] [prof]   prof: census (interp-methods.tsv, vita/v6/tools/interp-census) |
#                                                   aotprof (an .aotprofile of every executed method: aotprof/shepherd_aotprof.c) | none
# Engine: $GA_OUT/build/pubmono (dotnet publish of this worktree, see SIZES.md §1). Pad: the gameplay survey's pad-main (menu -> new game
# -> quotes -> Game Biome Intro, then move/attack). Output: $GA_OUT/intro/<name>/ (assemblies.txt, engine.log, the profile).
set -uo pipefail
GO=${GA_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/sizes}; HERE=${0:A:h}; REPO=${HERE:h:h}
MAIN=${SHEPHERD_MAIN_REPO:-${SHEPHERD_REPO:-$HOME/src/shepherd}}          # git-ignored inputs: runtime/ManagedVita, runtime/derived, runtime/persistent
GD="${SHEPHERD_GAME_DATA}"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
case ${1:-} in
stage)
  D=$GO/intro/Data; rm -rf $D; mkdir -p $D/Managed
  ls "$GD" | while read n; do [ "$n" = Managed ] || ln -sfn "$GD/$n" "$D/$n"; done
  cp "$GD"/Managed/*.dll $D/Managed/; cp $MAIN/runtime/ManagedVita/* $D/Managed/; cp $MAIN/runtime/derived/Shepherd.Engine.Derived.dll $D/Managed/
  cp $HERE/pad-main.txt $GO/intro/pad-main.txt
  echo "staged $D: $(ls $D/Managed | wc -l | tr -d ' ') files in Managed/" ;;
run)
  N=$2; FR=${3:-4200}; P=${4:-census}
  [ "$(osascript -e 'output muted of (get volume settings)')" = true ] || { echo "Mac output not muted: refusing"; exit 1; }
  R=$GO/intro/root-$N; O=$GO/intro/$N; rm -rf $R $O; mkdir -p $R/runtime/logs $O
  cp -R $MAIN/runtime/persistent $R/runtime/persistent
  MO="--interpreter"; DY=
  case $P in
    census)  MO="--interpreter --profile=shepherd:$R/runtime/logs/interp-methods.tsv"; DY=$GO/tools/interp-census ;;
    aotprof) MO="--interpreter --profile=shepherdaot:$R/runtime/logs/menu-intro.aotprofile"; DY=$GO/tools/aotprof ;;
  esac
  envs=(SHEPHERD_ROOT=$R SHEPHERD_GAME_DATA=$GO/intro/Data SHEPHERD_MANAGED=$GO/intro/Data/Managed SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null
        SHEPHERD_AUDIO=off SHEPHERD_PAD_FILE=$GO/intro/pad-main.txt SHEPHERD_SIMSTATS=1301:1400:1300
        MONO_ENV_OPTIONS="$MO" MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS=major=marksweep,no-concurrent-sweep,nursery-size=4m)
  [ -n "$DY" ] && envs+=(DYLD_LIBRARY_PATH=$DY)
  print -l -- $envs > $O/env.txt
  (cd $R && env -u SHEPHERD_PACK $envs $GO/build/pubmono/Shepherd.Engine boot $FR 100000 > $O/console.txt 2>&1); echo "rc=$?" > $O/rc.txt
  cp -R $R/runtime/logs/. $O/ 2>/dev/null; rm -rf $R
  grep -E "scene '|done:" $O/engine.log | sed 's/^/   /' | cut -c1-160
  [ "$(osascript -e 'output muted of (get volume settings)')" = true ] || echo "WARNING: Mac output not muted at the end" ;;
*) echo "usage: census-run.sh stage | run <name> [frames] [census|aotprof|none]"; exit 2 ;;
esac
