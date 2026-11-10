#!/bin/zsh
# Kit #12 boot track (vita/notes/kit12-boot.md section 2): the Massive Monster logo clip for the owner's boot sequence -- the game's own level0
# splash as Shepherd draws it, from the owner's OWN copy, into his card folder (owner-local: never in git, never in a VPK, never shared).
#   1. one Mac run of the engine (vita/tests/macboot.sh: the worktree's Debug engine, the Vita profile at 960 x 544, the GL backend, the frame
#      clock, silent, a sandbox copy of the saves) through level0's splash, a screenshot every frame of the window around the logo (no pad: the
#      game's own sequence plays by itself);
#   2. vita/tools/mmlogo_pack.py: the logo's frames (the first lit frame after the 2 s black to the last before black), every second one, as
#      an H.264 .shv with the logo's pre-roll sound from the card's audio bake -> <card>/data/video/MassiveMonster.shv.
# Nothing in the game is changed or driven. usage: mmlogo_record.sh <card dir> [run dir]   env: MMREC_FROM / MMREC_TO (default 100..600),
#   MACBOOT_BIN (the engine build), MMREC_CHECK=1 (PSNR of the clip against the screenshots)
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h}"
CARD=${1:?usage: mmlogo_record.sh <card dir> [run dir]}; CARD=${CARD:a}
RUN=${2:-${TMPDIR:-/tmp}/mmlogo-run}; RUN=${RUN:a}
[[ -f $CARD/pack/shepherd.pack ]] || { echo "no $CARD/pack/shepherd.pack (the owner's card folder)"; exit 2; }
FROM=${MMREC_FROM:-100}; TO=${MMREC_TO:-600}
zsh $REPO/vita/tests/macboot.sh $RUN $TO SHEPHERD_PACK=$CARD/pack SHEPHERD_TEST_SPINE=join SHEPHERD_RENDER=gl SHEPHERD_SHOT_FRAMES=$FROM-$TO || { echo "the Mac run failed (see $RUN)"; exit 1; }
mkdir -p $CARD/data/video
chk=(); [[ ${MMREC_CHECK:-0} == 1 ]] && chk=(--check)
python3 -I $T/mmlogo_pack.py $RUN/shots -o $CARD/data/video --audio-bake $CARD/audiobake/menu $chk || exit 1
[[ ${MMREC_KEEP:-0} == 1 ]] || rm -rf $RUN/shots   # (MMREC_KEEP=1 keeps them for a look; the screenshots: game-derived -- the .shv is what stays)
echo "Massive Monster clip: $CARD/data/video/MassiveMonster.shv ($(stat -f %z $CARD/data/video/MassiveMonster.shv) B)"
