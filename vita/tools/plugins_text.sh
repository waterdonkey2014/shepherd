#!/bin/zsh
# Kit #12 boot track (vita/notes/kit12-boot.md section 3): the recommended plugins' names, as the game's own loading text draws them, for the
# host to draw over the loading screen when one is missing -> <card>/data/video/boot/plugins.shp (owner-local: the game's font, drawn).
#   one Mac run of the engine (bootscreen_record.sh's New Game route into the 'Game Biome Intro' load, the loading screen's own looks) with the
#   game's loading text showing each name in turn inside the ring's loop (SHEPHERD_K12_REC_TEXT), then vita/tools/plugins_text.py.
# Ids: 0 CapUnlocker, 1 PSVshellPlus, 2 Wi-Fi Turbo (for online play) -- k12_boot.inc.c's K12P_*. usage: plugins_text.sh <card dir> [run dir]
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h}"
CARD=${1:?usage: plugins_text.sh <card dir> [run dir]}; CARD=${CARD:a}
RUN=${2:-${TMPDIR:-/tmp}/plugins-text-run}; RUN=${RUN:a}
[[ -f $CARD/pack/shepherd.pack ]] || { echo "no $CARD/pack/shepherd.pack (the owner's card folder)"; exit 2; }
zsh $REPO/vita/tests/macboot.sh $RUN 3200 PAD=$REPO/vita/tests/fixtures/pads/vlx-intro.txt SHEPHERD_PACK=$CARD/pack SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join \
  SHEPHERD_RENDER=gl SHEPHERD_STOP_SCENE= SHEPHERD_K12_STENCIL=1 SHEPHERD_K12_LOOKS=1 SHEPHERD_SHOT_FRAMES=2955,2965,2975,3135,3145,3155 \
  "SHEPHERD_K12_REC_TEXT=3130=CapUnlocker|3140=PSVshellPlus|3150=Wi-Fi Turbo (for online play)|3160=" || { echo "the Mac run failed (see $RUN)"; exit 1; }
python3 -I $T/plugins_text.py $RUN/shots -o $CARD/data/video/boot --box 112,468,400,44 --line 0:3135 --line 1:3145 --line 2:3155 || exit 1
rm -rf $RUN/shots
