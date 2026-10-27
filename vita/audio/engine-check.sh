#!/bin/zsh
# Stage C regression (vita/notes/m1-audio.md): sound in the Vita profile. The engine's FMOD stubs routed to shepaudio with the menu
# bake, on the V6 sim boot (Mono INTERPRETER, coop, the Vita GC shape, null renderer, strict menu pack, pad 1050:cross 1400:down),
# 1,500 frames, rendered to float WAVs -- FILE OUTPUT ONLY: the Mac library has no device backend, nothing is ever played.
#   runs   base (no bake: the silent stubs), A + A2 (full mix, twice), and A with one event at volume 0 for each probed event
#   checks vita/audio/tools/engine_wav_check.py (determinism, clipping, envelope, per-event onset/gain from A - B, STOPPED timing),
#          the end-of-run instance report (no bridge leak), the CPU numbers (per Studio update; frame time vs base),
#          `boot vitatest` with the bake (FMODUnity's wrapper through the game's patterns: 35 checks)
# Derived audio stays under runtime/audiobake/_engine/ (gitignored); logs under runtime/logs/v6/c-audio-*.
# Needs: vita/audio/build.sh mac, vita/v6/sim.sh publish + stage, the bake (runtime/audiobake/menu). Exit 0 = PASS.
set -uo pipefail
HERE=${0:a:h}; V=${HERE:h}; REPO=${V:h}; R=$REPO/runtime
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
BAKE=${AUDIO_BAKE:-$R/audiobake/menu}
LIB=$HERE/build/mac/libshepaudio.dylib
W=$R/audiobake/_engine/check
FR=${AUDIO_FRAMES:-1500}
[[ -f $BAKE/manifest.json ]] || { echo "no bake at $BAKE"; exit 1; }
[[ -f $LIB ]] || bash "$HERE/build.sh" mac > /dev/null || exit 1
mkdir -p $W
echo "muted at start: $(osascript -e 'output muted of (get volume settings)')"
PROBES=(event:/music/menu/menu_title event:/ui/change_selection event:/ui/start_game event:/music/mm_pre_roll/mm_pre_roll)
names=(music ui start splash)
AUD=(SHEPHERD_AUDIO_BAKE=$BAKE SHEPHERD_AUDIO_LIB=$LIB)
run() { local n=$1; shift; SIM_FRAMES=$FR zsh $V/v6/sim.sh run $n "$@" | head -1 | sed 's/^/  /'; }
run c-audio-base
run c-audio-a $AUD SHEPHERD_AUDIO_OUT=file:$W/a.wav
run c-audio-a2 $AUD SHEPHERD_AUDIO_OUT=file:$W/a2.wav
MUTES=()
for i in {1..${#PROBES}}; do
  run c-audio-mute-${names[$i]} $AUD SHEPHERD_AUDIO_OUT=file:$W/mute-${names[$i]}.wav SHEPHERD_AUDIO_MUTE=${PROBES[$i]}
  MUTES+=(--mute "${PROBES[$i]}=$W/mute-${names[$i]}.wav")
done
L=$R/logs/v6
fail=0
echo "== engine_wav_check"
python3 $HERE/tools/engine_wav_check.py --manifest $BAKE/manifest.json --lib $LIB --full $W/a.wav --events $L/c-audio-a/audio-events.tsv \
  --repeat $W/a2.wav $MUTES --json $W/check.json || fail=1
echo "== the run's own report (c-audio-a)"
grep -E "\[audio\]|\[fmod\]" $L/c-audio-a/engine.log | sed 's/^\[ *[0-9]*\] *//' | grep -vE "^\[audio\] f[0-9]+ " | sed 's/^/  /'
grep -q "bridge leaks (released + STOPPED but not destroyed): 0" $L/c-audio-a/engine.log && echo "ok   no bridge leak" || { echo "FAIL bridge leak (see above)"; fail=1; }
grep -q "LEAK " $L/c-audio-a/engine.log && { echo "FAIL a leaked instance"; fail=1; }
grep -q "done: 7 errors, 3 exceptions" $L/c-audio-a/engine.log && echo "ok   boot with audio: 7 errors / 3 exceptions (the sim's numbers), menu: $(grep -o "selected='DLC'" $L/c-audio-a/engine.log | tail -1)" || { echo "FAIL boot errors differ: $(tail -1 $L/c-audio-a/engine.log)"; fail=1; }
cmp -s <(grep -o "selected='[^']*' label='[^']*'" $L/c-audio-base/engine.log) <(grep -o "selected='[^']*' label='[^']*'" $L/c-audio-a/engine.log) \
  && echo "ok   the menu probes are the same with and without audio" || { echo "FAIL menu probes differ with audio"; fail=1; }
echo "== CPU (Mac M4 Pro, Mono interpreter): frame time over 1301..1400, base (silent stubs) vs A (shepaudio + file output)"
for n in base a; do echo "  $n: $(grep -E '^  frame wall' $L/c-audio-$n/simstats.txt | sed 's/^ *//')"; done
echo "== vitatest with the bake (CoreCLR, null output)"
(cd $REPO/engine/Shepherd.Engine && dotnet build -c Debug -o bin -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -q " 0 Error(s)") || { echo "FAIL engine build"; fail=1; }
(cd $REPO/engine/Shepherd.Engine && env SHEPHERD_ROOT=$REPO SHEPHERD_PROFILE=vita SHEPHERD_AUDIO_BAKE=$BAKE SHEPHERD_AUDIO_LIB=$LIB SHEPHERD_AUDIO_OUT=null dotnet bin/Shepherd.Engine.dll vitatest > $TMPDIR/c-audio-vitatest.txt 2>&1); vt=$?
mkdir -p $L/c-audio-vitatest; cp $R/logs/engine.log $L/c-audio-vitatest/
grep -E "\[vitatest\] (FAIL|[0-9]+ passed)" $L/c-audio-vitatest/engine.log | sed 's/^\[ *[0-9]*\] */  /'
[[ $vt == 0 ]] && echo "ok   vitatest with the bake" || { echo "FAIL vitatest with the bake (rc $vt)"; fail=1; }
echo "muted at end: $(osascript -e 'output muted of (get volume settings)')"
(( fail )) && { echo "FAIL engine-check"; exit 1; }
echo "PASS engine-check"
