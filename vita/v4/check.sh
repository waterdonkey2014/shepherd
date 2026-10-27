#!/bin/zsh
# V4 Vita-profile regression (vita/notes/v4-profile.md). Builds the engine (Debug), regenerates runtime/ManagedVita if missing
# (or with --rewrite), then runs: vitatest self-test, the Vita profile done-test (pad events, 960x544, census), and the Mac
# default boot (must stay "7 errors, 3 exceptions"; polish round 1, was 10/4). Logs + screenshots -> runtime/logs/v4/check-*/. Headless, offline.
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
REPO="${0:A:h:h:h}"; R="$REPO/runtime"; ENG="$REPO/engine/Shepherd.Engine"   # this checkout (the main one resolves to the old fixed path)
export SHEPHERD_ROOT="${SHEPHERD_ROOT:-$REPO}"   # the engine's root = this checkout (its default is the main checkout's)
# fid1-fixup D1: the owner-local render code (engine/Derived -> runtime/derived; absent = unlit, ungraded, no Stylizer pass, so the
# Mac default's f01350 then differs from the racecheck references, which carry the Stylizer)
DER=$(zsh "$REPO/vita/shepherd/build-derived.sh") || exit 1; echo "    owner-local Shepherd.Engine.Derived: $DER"
GAME_MANAGED="${SHEPHERD_GAME_DATA}/Managed"
B=(-p:DisableTransitiveFrameworkReferenceDownloads=true)

if [[ "$1" == "--rewrite" || ! -f "$R/ManagedVita/icalls.json" ]]; then
  (cd "$REPO/tools/Rewriter" && dotnet build -c Release -o bin $B >/dev/null && dotnet bin/Rewriter.dll "$GAME_MANAGED" "$R/ManagedVita" --vita | grep -E "FMOD|rewrote") || exit 1
  zsh "$REPO/tools/IcallBake/bake.sh" "$R/ManagedVita" || exit 1   # kit #4 B-1: icalls.bin beside the new icalls.json (else the engine parses the json)
fi
(cd "$ENG" && dotnet build -c Debug -o bin $B 2>&1 | grep -E " error |rror\(s\)") || exit 1
stamp=$(stat -f %m "$R/persistent/saves/settings.json" 2>/dev/null)

run() {   # run <name> <env...> -- <boot args...>
  local name=$1; shift; local -a envs; while [[ "$1" != "--" ]]; do envs+=$1; shift; done; shift
  local out="$R/logs/v4/check-$name"; rm -rf "$out"; mkdir -p "$out"; rm -f "$R"/shots/boot/*.png(N) "$R/logs/native-resolve.txt"
  (cd "$ENG" && env $envs dotnet bin/Shepherd.Engine.dll "$@" > "$out/console.txt" 2>&1); local rc=$?
  cp "$R/logs/engine.log" "$R/logs/missing-icalls.txt" "$out/" 2>/dev/null; cp "$R/logs/native-resolve.txt" "$out/" 2>/dev/null; cp "$R"/shots/boot/*.png(N) "$out/" 2>/dev/null
  echo "$name: rc=$rc  $(tail -1 "$out/engine.log" | sed 's/^\[ *[0-9]*\] *//')"
}
run vitatest SHEPHERD_PROFILE=vita -- vitatest
grep "\[vitatest\] [0-9]* passed" "$R/logs/v4/check-vitatest/engine.log" | sed 's/^\[ *[0-9]*\] */    /'
run vita SHEPHERD_PROFILE=vita "SHEPHERD_PAD=1050:cross,1400:down" -- boot 1460 50
grep -h "VERDICT" "$R/logs/v4/check-vita/native-resolve.txt" | sed 's/^/    /'
python3 -c "from PIL import Image; im=Image.open('$R/logs/v4/check-vita/f01450.png'); print('    f01450', im.size, '(expect 960x544, DLC selected: look at it)')"
run default SHEPHERD_PRESS=1050:32 -- boot 1400 50
grep -c " EXC " "$R/logs/v4/check-default/engine.log" | sed 's/^/    EXC lines: /;s/$/ (expect 2: SkeletonAnimationLOD NRE + repeat; TextAnimator and MenuAdController fixed in polish round 1)/'
python3 "$REPO/vita/v2/tools/imgdiff.py" "$R/logs/v4/check-default/f01350.png" "$R/logs/racecheck/f01350-run3.png" 2>/dev/null | sed 's/^/    /'
# V6 stage 2 review: the same frame against the candidate reference taken on the headless frame clock (engine-pass-speed.md §7);
# informational until the owner accepts it in place of the wall-clock run3 reference (expect byte-identical: 0 px)
[[ -f "$R/logs/racecheck/f01350-vclock-candidate.png" ]] && python3 "$REPO/vita/v2/tools/imgdiff.py" "$R/logs/v4/check-default/f01350.png" "$R/logs/racecheck/f01350-vclock-candidate.png" 2>/dev/null | sed 's/^/    candidate (frame clock, owner decision pending): /'
# polish round 1 (vita/notes/polish-round1.md §R): the frame-clock candidate re-taken after the round's menu fixes -- CoreCLR's TextAnimator
# effects (Febucci database built: the Woolhaven card's "AVAILABLE NOW!" wiggles), the store ad the Random-driven shuffle then picks,
# and SortingGroup's floor unit (5 px); expect byte-identical (0 px). Informational until the owner accepts it.
[[ -f "$R/logs/racecheck/f01350-polish1-candidate.png" ]] && python3 "$REPO/vita/v2/tools/imgdiff.py" "$R/logs/v4/check-default/f01350.png" "$R/logs/racecheck/f01350-polish1-candidate.png" 2>/dev/null | sed 's/^/    candidate (polish round 1, owner decision pending): /'
[[ "$(stat -f %m "$R/persistent/saves/settings.json" 2>/dev/null)" == "$stamp" ]] || echo "WARNING: runtime/persistent/saves/settings.json changed during the checks (restore it: see notes)"
cp "$R/logs/v4/check-vita/native-resolve.txt" "$R/logs/native-resolve.txt" 2>/dev/null   # leave the Vita census at its canonical path
