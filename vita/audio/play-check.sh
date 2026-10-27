#!/bin/zsh
# kit3 play mode (vita/notes/play-mode.md): the play clock at the device's frame rate, on the Mac. The V6 sim boot (Mono interpreter,
# null renderer, strict menu pack) with SHEPHERD_TIME=wall, every frame padded to PLAY_FRAME_MS (166 = the kit #2 GL menu's ~6 fps),
# the menu bake through shepaudio's REAL-TIME output (rtfile: the Mac stand-in for the Vita's sceAudioOut thread -- a WAV, never a
# device), pad m+30 cross / m+90 down, stop at m+150 (~50 s). Then:
#   vita/audio/tools/play_wav_check.py   the audio clock = the wall clock; the title music = the take sample for sample at 1.0x
#   the run's own lines                  game time vs wall time, FixedUpdate steps per second of game time (50), menu arrival
#   a second run without the bake         the silent stubs' timelines advance on the audio (wall) clock
# Derived audio stays under runtime/audiobake/_engine/play/ (gitignored); logs runtime/logs/v6/play-*. Exit 0 = PASS.
# Needs: vita/audio/build.sh mac, vita/v6/sim.sh publish + stage (SHEPHERD_V6_PUB / SHEPHERD_SIM as usual), the bake.
set -uo pipefail
HERE=${0:a:h}; V=${HERE:h}; REPO=${V:h}; R=$REPO/runtime
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
BAKE=${AUDIO_BAKE:-$R/audiobake/menu}; LIB=$HERE/build/mac/libshepaudio.dylib; E=$R/audiobake/_engine/play; L=$R/logs/v6
MS=${PLAY_FRAME_MS:-166}
[[ -f $BAKE/manifest.json ]] || { echo "no bake at $BAKE"; exit 1; }
[[ -f $LIB ]] || bash "$HERE/build.sh" mac > /dev/null || exit 1
[[ "$(osascript -e 'output muted of (get volume settings)')" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
mkdir -p $E $TMPDIR; PAD=$TMPDIR/play-check-pad.txt
printf 'm+30:cross\nm+90:down\n' > $PAD
WALL=(SHEPHERD_TIME=wall SHEPHERD_FRAME_MS=$MS SHEPHERD_PAD_FILE=$PAD SHEPHERD_MENU_PROBE=m+25,m+85,m+145 SHEPHERD_STOP_AT=m+150 SHEPHERD_SIMSTATS=)
SIM_FRAMES=0 zsh $V/v6/sim.sh run play-wall $WALL SHEPHERD_AUDIO_BAKE=$BAKE SHEPHERD_AUDIO_LIB=$LIB SHEPHERD_AUDIO_OUT=rtfile:$E/play-wall.wav | head -1 | sed 's/^/  /'
SIM_FRAMES=0 zsh $V/v6/sim.sh run play-wall-stubs $WALL | head -1 | sed 's/^/  /'
fail=0
python3 $HERE/tools/play_wav_check.py --manifest $BAKE/manifest.json --lib $LIB --wav $E/play-wall.wav --events $L/play-wall/audio-events.tsv \
  --log $L/play-wall/engine.log --json $E/play-wall-check.json || fail=1
for r in play-wall play-wall-stubs; do
  grep -h "\[time\] clock\|\[time\] wall clock\|menuclock\] Main\|\[fmod\] wall clock" $L/$r/engine.log | sed 's/^\[ *[0-9]*\] *//' | cut -c1-260 | sed "s/^/  $r: /"
done
grep -q "\[time\] clock WALL" $L/play-wall/engine.log && echo "ok   the wall clock was on" || { echo "FAIL the wall clock was not on"; fail=1; }
sps=$(grep -oE '\(([0-9.]+) steps per second of game time' $L/play-wall/engine.log | grep -oE '[0-9.]+' | head -1)
[[ -n $sps ]] && (( sps > 49.5 && sps < 50.5 )) && echo "ok   FixedUpdate at $sps steps per second of game time (50 Hz)" || { echo "FAIL FixedUpdate rate '$sps'"; fail=1; }
grep -q "done: 7 errors, 3 exceptions" $L/play-wall/engine.log && echo "ok   boot outcome 7 errors / 3 exceptions" || { echo "FAIL boot outcome: $(tail -1 $L/play-wall/engine.log)"; fail=1; }
grep -qE "\[fmod\] wall clock: playing stub timelines .*event:/music/menu/menu_title [1-9][0-9]{3,} ms" $L/play-wall-stubs/engine.log \
  && echo "ok   the silent stubs' timelines advance on the audio clock" || { echo "FAIL stub timelines"; fail=1; }
echo "muted at end: $(osascript -e 'output muted of (get volume settings)')"
(( fail )) && { echo "FAIL play-check"; exit 1; }
echo "PASS play-check"
