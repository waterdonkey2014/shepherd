#!/bin/zsh
# Device kit #10, track boot (vita/notes/kit10-boot.md): the boot screen's recording -- the game's own loading screen as Shepherd draws it,
# from the owner's OWN copy, into his card folder (owner-local: never in git, never in a VPK, never shared).
#   1. one Mac run of the engine (vita/tests/macboot.sh: the worktree's Debug engine, the Vita profile at 960 x 544, the GL backend -- the
#      Vita's own draw path -- on the deterministic frame clock, silent, a sandbox copy of the saves) on the New Game route (the kit's pad)
#      into the 'Game Biome Intro' load (SHEPHERD_STOP_SCENE= empty), a screenshot every frame of the window around its loading screen;
#   2. vita/tools/bootscreen_pack.py: the fade-in + one exact ring cycle of 180 frames from those screenshots -> <card>/data/video/BootLoading.shb
#      (lossless: frame 0 whole, then each frame's changed rectangle, zlib; the tool plays it back and writes nothing unless every shown
#      frame equals its screenshot byte for byte).
# Nothing in the game is changed or driven: the run plays the route as every kit test does; the screenshots are what the engine drew.
# KNOWN (kit #10 review fix, finding 6): today's engine draws the loading screen's crown rings as WHITE SQUARES (the ring images use the game's
# UI_UberShader, whose texture the engine does not bind yet: kit #11's GPU work), and nothing here can tell -- so the host shows the file only
# with SHEPHERD_K10_BOOTSCREEN=1 in the env file (the owner's choice; default = kit #9's black). Re-record after the GPU work.
# usage: bootscreen_record.sh <card dir: the folder holding pack/ and data/> [out dir for the run, default $TMPDIR/bootscreen-run]
#   env: BOOTREC_FROM / BOOTREC_TO (the screenshot window, default 2850..3400: the loading screen of the kit's pack starts at ~2910),
#        MACBOOT_BIN (the engine build, default the worktree's Debug). (bootscreen_pack.py's playback check always runs: no check, no file.)
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h}"
CARD=${1:?usage: bootscreen_record.sh <card dir> [run dir]}; CARD=${CARD:a}
RUN=${2:-${TMPDIR:-/tmp}/bootscreen-run}; RUN=${RUN:a}
[[ -f $CARD/pack/shepherd.pack ]] || { echo "no $CARD/pack/shepherd.pack (the owner's card folder)"; exit 2; }
FROM=${BOOTREC_FROM:-2850}; TO=${BOOTREC_TO:-3400}
zsh $REPO/vita/tests/macboot.sh $RUN $TO PAD=$REPO/vita/tests/fixtures/pads/vlx-intro.txt SHEPHERD_PACK=$CARD/pack SHEPHERD_TEST_ASTAR=join \
  SHEPHERD_TEST_SPINE=join SHEPHERD_RENDER=gl SHEPHERD_STOP_SCENE= SHEPHERD_SHOT_FRAMES=$FROM-$TO || { echo "the Mac run failed (see $RUN)"; exit 1; }
mkdir -p $CARD/data/video
python3 -I $T/bootscreen_pack.py $RUN/shots -o $CARD/data/video || exit 1
rm -rf $RUN/shots   # (the screenshots: ~110 KB each, game-derived -- the .shb is what stays)
echo "boot screen: $CARD/data/video/BootLoading.shb ($(stat -f %z $CARD/data/video/BootLoading.shb) B)"
echo "NOTE: the recording shows Shepherd's current loading screen -- its crown rings are WHITE SQUARES until the GPU work binds UI_UberShader's texture;"
echo "      the host shows it only with SHEPHERD_K10_BOOTSCREEN=1 in the env file (owner decision pending; default = kit #9's black)"
