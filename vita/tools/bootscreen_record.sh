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
# DEVICE KIT #11 (track device, vita/notes/kit11-device.md): THE EMBLEM SET. The game picks a random emblem for every load (MMTools.MMTransition:
# Random.Range over its pentagram sprites), so kit #10's single run always caught the one its seed led to (an 'eyes' symbol). With
# BOOTREC_SEEDS="<seed> ..." the tool makes one run per seed -- the engine's random generator started from that seed
# (SHEPHERD_UNITY_RANDOM_SEED, tools only: Unity seeds from the clock, so every seed is one of the original's own starts) -- so the GAME draws
# its emblem itself in each run; each recording goes to <card>/data/video/boot/rec-<seed>.shb, a recording of an emblem already caught (the
# same pixels in the emblem's area: bootscreen_set.py key) is dropped, and bootscreen_set.py make writes SET.txt (the rings check included:
# 'rings drawn' only when no recording shows the white-square rings). Names: BOOTREC_NAMES="<seed>=<name> ..." (default emblem-<key>), the
# game's list slots: BOOTREC_SLOTS="<seed>=<n> ..." (default 1). BOOTREC_DEFAULT_ON=1 (review fix): bootscreen_set.py make --default-on -- the
# OWNER'S YES to the set this run makes (roadmap question 3): only then, and only with drawn rings and his whole pool of 6 emblems, does
# SET.txt say 'default on' and the host show the boot screen by default; never set it before the owner has seen the set. The New Game route's load draws from the BASE list only (the Sins of the Flesh
# pentagram joins the game's list once the save has built the pleasure shrine, Woolhaven's in winter): those recordings need a run from a
# sandbox save in that state (BOOTREC_PERSISTENT=<a sandbox persistent folder>; never the owner's own saves).
# KNOWN (kit #11, this track's recordings): some of the game's emblems draw as NOTHING in Shepherd today (the run's loading screen shows the
# crown and its rings around an empty centre) -- such a recording is dropped (bootscreen_set.py's emblem check), never put into the set.
# KNOWN (kit #10 review fix, finding 6): the engine drew the loading screen's crown rings as WHITE SQUARES (the ring images use the game's
# UI_UberShader, whose texture the engine did not bind: the GPU work's white-panels row); kit #11's rings check marks such a set 'rings white'
# and the host then keeps it opt-in (SHEPHERD_K10_BOOTSCREEN=1). Re-record once the rings draw right.
# KIT #12 (track boot, vita/notes/kit12-boot.md section 1): THE RINGS AND THE PENTAGRAMS DRAWN. Every run now draws with the engine's stencil
# masks (SHEPHERD_K12_STENCIL=1: the rings' round uGUI Mask, no longer its bounding box) and the boot loading screen's own looks (SHEPHERD_K12_LOOKS=1:
# the owner-local programs transcribed from the game's shaders -- the emblem's Watercolor-Sprite, which takes only the sprite's alpha, so the
# black-lined pentagram sprites now show; the rings' and the crown outline's FX_ParticleMaster_A_UI). BOOTREC_K11=1 records the kit #11 way (both
# off: white squares, no pentagrams). BOOTREC_EMBLEMS="<sprite name> ..." (default when no BOOTREC_SEEDS): one run per named emblem instead of
# per seed -- SHEPHERD_K12_REC_EMBLEM fills the game's own emblem list with that sprite (taken from the game's lists), so the game's own pick draws
# it (the same random number is drawn); the Sins of the Flesh Snake pentagram (only in a pleasure-shrine save's list) is recorded this way too.
# Names = the sprite names; slots = the game's base-list slots (BOOTREC_SLOTS). The owner's pool (2026-10-30): Pentagram-Star Pentagram-Star_1 2 5 6
# Pentagram-Snake, every one equally likely (Woolhaven's blue set and the co-op purple set need their season / material: not recorded here).
# usage: bootscreen_record.sh <card dir: the folder holding pack/ and data/> [out dir for the runs, default $TMPDIR/bootscreen-run]
#   env: BOOTREC_FROM / BOOTREC_TO (the screenshot window, default 2850..3400: the loading screen of the kit's pack starts at ~2910),
#        MACBOOT_BIN (the engine build, default the worktree's Debug), BOOTREC_SEEDS / BOOTREC_NAMES / BOOTREC_SLOTS / BOOTREC_PERSISTENT /
#        BOOTREC_DEFAULT_ON (kit #11, above). (bootscreen_pack.py's playback check always runs: no check, no file.)
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h}"
CARD=${1:?usage: bootscreen_record.sh <card dir> [run dir]}; CARD=${CARD:a}
RUN=${2:-${TMPDIR:-/tmp}/bootscreen-run}; RUN=${RUN:a}
[[ -f $CARD/pack/shepherd.pack ]] || { echo "no $CARD/pack/shepherd.pack (the owner's card folder)"; exit 2; }
FROM=${BOOTREC_FROM:-2850}; TO=${BOOTREC_TO:-3400}
one_run() {   # one_run <run dir> [ENV=VAL ...] -> <run dir>/shots
  local rd=$1; shift
  local -a pe; [[ -n ${BOOTREC_PERSISTENT:-} ]] && pe=(SHEPHERD_PERSISTENT=$BOOTREC_PERSISTENT)
  local -a k12; [[ ${BOOTREC_K11:-0} == 1 ]] || k12=(SHEPHERD_K12_STENCIL=1 SHEPHERD_K12_LOOKS=1)   # (kit #12: the rings' stencil mask + the screen's own looks)
  zsh $REPO/vita/tests/macboot.sh $rd $TO PAD=$REPO/vita/tests/fixtures/pads/vlx-intro.txt SHEPHERD_PACK=$CARD/pack SHEPHERD_TEST_ASTAR=join \
    SHEPHERD_TEST_SPINE=join SHEPHERD_RENDER=gl SHEPHERD_STOP_SCENE= SHEPHERD_SHOT_FRAMES=$FROM-$TO $pe $k12 "$@"
}
[[ -z ${BOOTREC_SEEDS:-} && -z ${BOOTREC_SINGLE:-} && ${BOOTREC_K11:-0} != 1 ]] && : ${BOOTREC_EMBLEMS:="Pentagram-Star Pentagram-Star_1 2 5 6 Pentagram-Snake"}   # (kit #12: the owner's pool by name)
if [[ -z ${BOOTREC_SEEDS:-} && -z ${BOOTREC_EMBLEMS:-} ]]; then   # kit #10's single recording (the engine's own seed; kit #12: BOOTREC_SINGLE=1 or BOOTREC_K11=1)
  one_run $RUN || { echo "the Mac run failed (see $RUN)"; exit 1; }
  mkdir -p $CARD/data/video
  python3 -I $T/bootscreen_pack.py $RUN/shots -o $CARD/data/video || exit 1
  rm -rf $RUN/shots   # (the screenshots: ~110 KB each, game-derived -- the .shb is what stays)
  echo "boot screen: $CARD/data/video/BootLoading.shb ($(stat -f %z $CARD/data/video/BootLoading.shb) B)"
  python3 -I $T/bootscreen_set.py check $CARD/data/video/BootLoading.shb
  exit 0
fi
# kit #11: the emblem set, one run per seed
B=$CARD/data/video/boot; mkdir -p $B
typeset -A KEYS NAMES SLOTS
for kv in ${=BOOTREC_NAMES:-}; do NAMES[${kv%%=*}]=${kv#*=}; done
for kv in ${=BOOTREC_SLOTS:-}; do SLOTS[${kv%%=*}]=${kv#*=}; done
typeset -a specs
for f in $B/rec-*.shb(N); do   # recordings already in the folder (an earlier call) keep their places
  k=$(python3 -I $T/bootscreen_set.py key $f) || continue
  KEYS[$k]=${f:t}
done
typeset -a runs; for seed in ${=BOOTREC_SEEDS:-}; do runs+=($seed); done
if [[ -n ${BOOTREC_EMBLEMS:-} ]]; then   # kit #12: one run per named emblem (its file rec-<name>.shb; names default to the sprite names)
  for e in ${=BOOTREC_EMBLEMS}; do runs+=("e:$e"); [[ -n ${NAMES[$e]:-} ]] || NAMES[$e]=$e; done
  [[ -n ${BOOTREC_SLOTS:-} ]] || SLOTS[Pentagram-Star]=2   # (the game's base list holds Pentagram-Star twice: its odds with SHEPHERD_K11_EMBLEM_ODDS=game)
fi
for seed in $runs; do
  rd=$RUN/seed-$seed
  if [[ $seed == e:* ]]; then seed=${seed#e:}; rd=$RUN/emblem-$seed
    one_run $rd SHEPHERD_K12_REC_EMBLEM=$seed || { echo "emblem $seed: the Mac run failed (see $rd)"; continue; }
  else
    one_run $rd SHEPHERD_UNITY_RANDOM_SEED=$seed || { echo "seed $seed: the Mac run failed (see $rd)"; continue; }
  fi
  python3 -I $T/bootscreen_pack.py $rd/shots -o $rd --name rec-$seed || { echo "seed $seed: no recording (see the packer's line)"; rm -rf $rd/shots; continue; }
  rm -rf $rd/shots
  chk=$(python3 -I $T/bootscreen_set.py check $rd/rec-$seed.shb 2>&1)   # (exit 1 for white rings too: the text decides; pipe_fail is on)
  if [[ $chk == *'emblem: MISSING'* ]]; then   # (kit #11 finding: some emblems draw as nothing)
    echo "seed $seed: the emblem is MISSING (Shepherd drew no emblem in this run: ${${chk##*emblem: }%%\)*})) -- dropped"
    rm -f $rd/rec-$seed.shb; continue
  fi
  k=$(python3 -I $T/bootscreen_set.py key $rd/rec-$seed.shb) || continue
  if [[ -n ${KEYS[$k]:-} ]]; then echo "seed $seed: emblem $k = ${KEYS[$k]} (already in the set): dropped"; rm -f $rd/rec-$seed.shb; continue; fi
  mv $rd/rec-$seed.shb $B/rec-$seed.shb; KEYS[$k]=rec-$seed.shb
  echo "seed $seed: emblem $k -> $B/rec-$seed.shb ($(stat -f %z $B/rec-$seed.shb) B)"
done
for k in ${(k)KEYS}; do
  f=${KEYS[$k]}; s=${${f#rec-}%.shb}
  specs+=("$f=${NAMES[$s]:-emblem-$k}:${SLOTS[$s]:-1}")
done
(( ${#specs} )) || { echo "no recording made"; exit 1; }
yes=(); [[ ${BOOTREC_DEFAULT_ON:-0} == 1 ]] && yes=(--default-on)
python3 -I $T/bootscreen_set.py make $B $specs --allow-white $yes || exit 1
echo "boot screen emblem set: $B ($(ls $B/*.shb | wc -l | tr -d ' ') recording(s)); SET.txt:"; grep -v '^#' $B/SET.txt
