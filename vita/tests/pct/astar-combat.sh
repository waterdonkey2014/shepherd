#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "The combat gate flake explained"): the physics workflow's combat drive (k6-physics, check-phys.sh
# step 5: the Mac intro route through the rebirth into the first fight, SHEPHERD_PHYS=3, per-50-step pose hashes, E1's golden
# 913dc162f6e72356) run on a READ-ONLY copy of the k6-physics engine (git archive into the work dir: the branch, its worktree and its build
# trees are never written) with this track's Diag/TestAstar.cs and Diag/TestSpine.cs dropped in (+ their one-line hooks) -- so the two
# test-only determinism switches can be tried on the one drive that has pathing enemies and physics, before the physics workflow adopts them.
#   astar-combat.sh setup                      extract + patch + build (once); PHYS_REF (default k6-physics: the branch head), PHYS_DYLIB /
#                                              PHYS_DERIVED (default: the physics worktree's current files) pin an earlier setup (review rerun:
#                                              PHYS_REF=d1f9608 + setup B's snapshots in $TW/physsnap-d1f9608)
#   astar-combat.sh run <tag> [ENV=VAL ...]    one drive -> $TW/combat/<tag>/{pose.txt,engine.log}
# Needs the physics workflow's Mac dylib and Derived DLL (both COPIED into the copy at setup, never modified: a Derived DLL rebuilt by the
# physics workflow for a newer engine fails the game's reflection scans -- TextAnimator's ReflectionTypeLoadException -- and changes the
# drive; every run is checked for that and marked SETUP MISMATCH) and the owner's game install (read-only, as the physics gate). Silent.
setopt pipe_fail
T="${0:A:h}"; WT="${T:h:h:h}"; TW=${TESTS_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work}
PC=$TW/physcopy; PREPO="${SHEPHERD_REPO:-$HOME/src/shepherd}"; PWT=${SHEPHERD_WORK:-$HOME/shepherd-work}/kit6/physics
export TMPDIR=$TW/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
case $1 in
setup)
  rm -rf $PC; mkdir -p $PC
  REF=${PHYS_REF:-k6-physics}
  git -C "$PREPO" archive $REF engine vita/physics/README.md | tar -x -C $PC
  echo "physcopy = $REF $(git -C "$PREPO" rev-parse --short $REF) (git archive, read-only source)" > $PC/SOURCE.txt
  cp $WT/engine/Shepherd.Engine/Diag/TestAstar.cs $WT/engine/Shepherd.Engine/Diag/TestSpine.cs $PC/engine/Shepherd.Engine/Diag/
  python3 - $PC <<'PY' || exit 1
import sys
pc = sys.argv[1]
def edit(p, a, b):
    s = open(p).read(); assert s.count(a) == 1, (p, a[:50]); open(p, 'w').write(s.replace(a, b))
edit(pc + '/engine/Shepherd.Engine/Runtime/Lifecycle.cs', "        Shepherd.Audio.Silence.BeforeUpdates();", "        Shepherd.Audio.Silence.BeforeUpdates();\n        Diag.TestAstar.BeforeUpdates();   // kit #7 tests: SHEPHERD_TEST_ASTAR")
edit(pc + '/engine/Shepherd.Engine/Boot.cs', '        Log.Info($"done: {Log.Errors} errors', '        if (Diag.TestAstar.Summary() is { } testAstar) Log.Info(testAstar);\n        if (Diag.TestSpine.Summary() is { } testSpine) Log.Info(testSpine);\n        Log.Info($"done: {Log.Errors} errors')
PY
  # SHEPHERD_TEST_SPINE's hook lines (review fix: every callback site + the bind after the game's own DispatchThread init): spine-hooks.py
  python3 $T/spine-hooks.py $PC/engine | tee -a $PC/SOURCE.txt || { echo "spine-hooks: critical sites missing"; exit 1; }
  DY=${PHYS_DYLIB:-$PWT/vita/physics/build/mac/libshepphys.dylib}; DE=${PHYS_DERIVED:-$PWT/runtime/derived/Shepherd.Engine.Derived.dll}
  cp $DY $PC/libshepphys.dylib
  echo "dylib = $(shasum -a 256 $PC/libshepphys.dylib | cut -c1-16) (copy of $DY, $(stat -f %Sm $DY))" >> $PC/SOURCE.txt
  mkdir -p $PC/derived && cp $DE $PC/derived/Shepherd.Engine.Derived.dll
  echo "derived = $(shasum -a 256 $PC/derived/Shepherd.Engine.Derived.dll | cut -c1-16) (copy of $DE, $(stat -f %Sm $DE))" >> $PC/SOURCE.txt
  (cd $PC/engine/Shepherd.Engine && nice dotnet build -c Release -o $PC/binrel -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)")
  cat $PC/SOURCE.txt ;;
refresh)   # this track's current Diag/TestAstar.cs into the copy, rebuilt (the copy's other files untouched)
  cp $WT/engine/Shepherd.Engine/Diag/TestAstar.cs $WT/engine/Shepherd.Engine/Diag/TestSpine.cs $PC/engine/Shepherd.Engine/Diag/
  (cd $PC/engine/Shepherd.Engine && nice dotnet build -c Release -o $PC/binrel -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)")
  echo "TestAstar.cs / TestSpine.cs refreshed: $(shasum -a 256 $PC/engine/Shepherd.Engine/Diag/TestAstar.cs | cut -c1-16) / $(shasum -a 256 $PC/engine/Shepherd.Engine/Diag/TestSpine.cs | cut -c1-16)" | tee -a $PC/SOURCE.txt ;;
run)
  tag=$2; shift 2
  O=$TW/combat/$tag; rt=$TW/combat/root-$tag; rm -rf $O $rt; mkdir -p $O $rt/runtime/logs
  [[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || osascript -e 'set volume output muted true'
  [[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
  for x in Managed ManagedVita shaders; do [[ -e $PWT/runtime/$x ]] && ln -s $PWT/runtime/$x $rt/runtime/$x; done
  [[ -f $PC/derived/Shepherd.Engine.Derived.dll ]] || { echo "no Derived snapshot in the copy: run setup"; exit 8; }
  ln -s $PC/derived $rt/runtime/derived
  cp -R "$PREPO/runtime/persistent" $rt/runtime/persistent
  CV=(SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_STOP_SCENE= SHEPHERD_PAD_FILE=${SHEPHERD_WORK:-$HOME/shepherd-work}/kit6/physics-work/pads/s3-combat.txt
      SHEPHERD_PHYS=3 SHEPHERD_VIDEO_STANDIN=2 SHEPHERD_RENDERER_DEFAULTMAT=1 SHEPHERD_PHYS_LIB=$PC/libshepphys.dylib SHEPHERD_AUDIO=off SHEPHERD_AUDIO_OUT=null)
  s=$(date +%s); l0=$(sysctl -n vm.loadavg | awk '{print $2}')
  (cd $rt && env SHEPHERD_ROOT=$rt $CV SHEPHERD_PHYS_HASH=$O/pose.txt "$@" nice dotnet $PC/binrel/Shepherd.Engine.dll boot ${COMBAT_FRAMES:-14700} 0 > $O/console.txt 2>&1); rc=$?
  cp $rt/runtime/logs/engine.log $O/ 2>/dev/null; rm -rf $rt
  grep -q -E "Could not load type|EXC .*ReflectionTypeLoadException" $O/engine.log 2>/dev/null && { echo "SETUP MISMATCH (a type load failed: engine / Derived DLL out of step)" > $O/mismatch.txt; rc=7; }
  echo "$tag rc=$rc $(( $(date +%s) - s ))s load $l0 last: $(tail -1 $O/pose.txt 2>/dev/null) | $(grep -h 'test-astar\] .*:' $O/engine.log | tail -1 | sed 's/^\[ *[0-9]*\] *//' | cut -c1-200)" | tee $O/verdict.txt ;;
esac
