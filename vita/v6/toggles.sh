#!/bin/zsh
# device kit #6 (track toggles, vita/notes/kit6-toggles.md): Settings > Graphics > PS Vita Settings on the Mac -- the rows the engine adds
# to the game's Graphics tab (Runtime/VitaToggles.cs: 4th CPU Core, CPU Speed, the CPU Clock readout), reached by pad and by touch, changed,
# Settings closed and opened again, the app started again (the owner folder's vita-settings.txt), the game's Reset All, the rows greyed out
# when the host cannot do what they ask, and the switch SHEPHERD_K6_TOGGLES=0 (kit #5: no rows, no file). The Mac has no Vita host: the
# engine's stand-in answers for it (SHEPHERD_K6_SIM_CORE3=0: no core-unlock plugin; SHEPHERD_K6_SIM_ARM500=1: a plugin lets 500 MHz through).
# Vita profile, GL + DXT, the strict menu pack, the frame clock (the wall test: SHEPHERD_TIME=wall), SHEPHERD_AUDIO=off (silent), CoreCLR
# (the Debug build, like tour.sh / touch.sh). Each test is its own run with its own root (a fresh copy of the sandbox saves, never the real
# save folder); "persist" and "reset" start from the root "pad" left (its vita-settings.txt).
#   pad      by pad: down to 4th CPU Core, cross (Off), down to CPU Speed, right (500 MHz); circle closes Settings; Settings again -> the rows
#            show Off / 500 MHz; vita-settings.txt says so
#   persist  a new start on pad's root: the file read at boot (Off and 500 applied before the pack opens), the rows show it
#   touch    by touch: a tap passes Press Any Button, tap Settings, the Graphics tab, drag the list to its end, tap the toggle (Off) and
#            CPU Speed's right arrow (500 MHz); circle (pad: touch has no Back) closes Settings; tap Settings again, drag: Off / 500 MHz
#   unavail  SHEPHERD_K6_SIM_CORE3=0 and a stock 444 MHz: both rows greyed out and skipped by the pad (the selection stays on Depth of
#            Field); a tap on the greyed toggle changes nothing
#   reset    pad's file (Off / 500): square (Reset All) + cross (confirm) -> the rows back to On / 444 MHz, the file too
#   resetgame (review fix) pad's file (Off / 500): Reset All on the Game tab, before the Graphics tab ever shows -> the file back to On /
#            444 at once (the Settings menu's controller is watched), and the rows, built afterwards, show On / 444 MHz
#   resetclose (review fix) pad's file (Off / 500): Reset All on the Game tab, Settings closed right after -> the file On / 444
#   wall     SHEPHERD_TIME=wall: the readout shows the clock and the frames per second
#   off      SHEPHERD_K6_TOGGLES=0 SHEPHERD_K6_SNAPPOOL=0 (every switch of the track) on pad's route: no [k6] line, no row (the pad ends on
#            Depth of Field), no file; and with BASE_BIN (a kit #5 engine build) its stream hash = kit #5's on every frame
#   tour     (with BASE_BIN) the whole menu tour vs kit #5: every frame identical except the Graphics tab's (the list is 4 rows longer:
#            its scrollbar), the frames are listed
#   tour-off (with BASE_BIN) the whole tour with every switch of the track off (SHEPHERD_K6_STABLE_ORDER unset) = kit #5 on every frame
#   (kit #6 integration: SHEPHERD_K6_STABLE_ORDER is ON by default and FullScreen Mode is greyed + fixed -- the pads' row walks are
#   unchanged: the greyed default row is not selected when the tab shows, the first down goes to Resolution as before; every comparison
#   with kit #5 turns every other kit #6 switch off ($K6OTHER: since the merge the engine track's resting mouse alone moves the menu
#   frames after the press); "off" allows the engine track's own "[k6] engine track OFF" end line; the 500
#   MHz check now runs once at start (persist reads the boot's line); the FullScreen fix itself is checked under vlx: kit6-integration.md)
#   stable   (with BASE_BIN; review) SHEPHERD_K6_STABLE_ORDER=1 (Lifecycle's Active in a stable order, as Unity's per-order update lists):
#            the tour with the rows vs without them differs only while the Graphics tab shows (the later animation-phase windows of the
#            default, kit #5's unstable sort, are gone); how many frames the stable order itself changes vs kit #5 is printed (kit #6
#            review fix: tour-st / tour-st-norows run with every OTHER kit #6 switch off, $K6OTHER minus the stable order, so that count is
#            the stable order's alone -- before, the engine track's resting mouse and the rest were counted in it)
#   fsdialog (kit #6 review fix, FullScreen Mode) Graphics tab: down to Resolution, square (Reset All) and Back -- the dialog's release
#            (the game's SetActiveStateForMenu(true)) lights every row -- then at once up + left, left: no 'FullScreen Mode value
#            changed' (the row is greyed again in the release frame and its value guarded); fsdrop the same after the Resolution
#            dropdown is opened (cross) and closed (circle); fsdialog0 / fsdrop0 the same pads with SHEPHERD_K6_FULLSCREEN=0 (the PC row):
#            the value DOES change (the pads reach the row)
# usage: vita/v6/toggles.sh [out dir]   (default runtime/logs/toggles; TOGGLES_PACK=<dir>, TOGGLES_BIN=<engine bin dir>, BASE_BIN=<kit #5 bin>)
# Exit 0 = every test PASS. The logs and screenshots are derived game output: they stay under runtime/ (git-ignored).
setopt pipe_fail
V6="${0:A:h}"; REPO="${V6:h:h}"; R="$REPO/runtime"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
OUT=${1:-$R/logs/toggles}; PACK=${TOGGLES_PACK:-$R/vitapack/menu}; BIN=${TOGGLES_BIN:-$REPO/engine/Shepherd.Engine/bin/Debug/net8.0}; BASE=${BASE_BIN:-}
[[ -f $PACK/shepherd.pack ]] || { echo "no pack at $PACK (vita/v4/check-pack.sh)"; exit 2; }
[[ -f $BIN/Shepherd.Engine.dll ]] || { echo "no engine build at $BIN (dotnet build engine/Shepherd.Engine -c Debug)"; exit 2; }
osascript -e 'set volume output muted true' 2>/dev/null
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
rm -rf $OUT; mkdir -p $OUT/in
# the Main Menu lands at f806 (frame clock): m+244 engage, m+494 / m+524 down to Settings, m+554 open (Game tab), m+684 r (Graphics tab);
# then 13 downs from FullScreen Mode reach the first new row (the game's 13 rows: FullScreen Mode .. Depth of Field)
downs() { local from=$1 n=$2; for i in {1..$n}; do print "m+$((from + (i - 1) * 20)):down"; done; }
{ print -l m+244:cross m+494:down m+524:down m+554:cross m+684:r; downs 760 13; print -l m+1040:cross m+1080:down m+1120:right m+1200:circle m+1400:cross m+1530:r; downs 1600 14; } > $OUT/in/pad.txt
{ print -l m+244:cross m+494:down m+524:down m+554:cross m+684:r; downs 760 14; } > $OUT/in/pad-bottom.txt
{ print -l m+244:cross m+494:down m+524:down m+554:cross m+684:r m+760:square m+840:right m+880:cross; downs 1000 14; } > $OUT/in/pad-reset.txt   # (the dialog: Back | Accept, Back selected)
{ print -l m+244:cross m+494:down m+524:down m+554:cross m+684:square m+764:right m+804:cross m+904:r; downs 980 14; } > $OUT/in/pad-resetgame.txt   # (review: Reset All on the Game tab)
print -l m+244:cross m+494:down m+524:down m+554:cross m+684:square m+764:right m+804:cross m+830:circle > $OUT/in/pad-resetclose.txt
print -l m+1200:circle > $OUT/in/pad-touch.txt   # (touch has no Back: circle closes Settings)
# (kit #6 review fix) FullScreen Mode after a dialog's release: Graphics tab, down (Resolution), square (Reset All's dialog, Back selected)
# + cross (Back) / cross (the Resolution dropdown) + circle (closed), then up, left, left right after the release (Reset All's dialog hides
# by m+892: the Mac probes), well inside the old once-a-second re-grey; circle closes Settings (SaveAndApply)
print -l m+244:cross m+494:down m+524:down m+554:cross m+684:r m+760:down m+800:square m+880:cross m+896:up m+902:left m+908:left m+1000:circle > $OUT/in/pad-fsdialog.txt
print -l m+244:cross m+494:down m+524:down m+554:cross m+684:r m+760:down m+800:cross m+880:circle m+896:up m+902:left m+908:left m+1000:circle > $OUT/in/pad-fsdrop.txt
run() {   # run <name> <frames> <pad file or -> <touch items ;-separated or -> [ROOT=<root to keep>] [BIN=<bin>] [ENV=VAL ...]
  local name=$1 frames=$2 pad=$3 touch=$4; shift 4; local o=$OUT/$name root=$OUT/root-$name keep=0 bin=$BIN
  while [[ $1 == ROOT=* || $1 == BIN=* ]]; do [[ $1 == ROOT=* ]] && { root=${1#ROOT=}; keep=1; }; [[ $1 == BIN=* ]] && bin=${1#BIN=}; shift; done
  mkdir -p $o/shots $root/runtime/logs
  for d in Managed ManagedVita derived; do [[ -e $R/$d && ! -e $root/runtime/$d ]] && ln -s $R/$d $root/runtime/$d; done
  [[ -d $root/runtime/persistent ]] || cp -R $R/persistent $root/runtime/persistent
  # (kit #7 measure: these are kit #6's four rows -- the fifth, Performance Overlay, is vita/v6/k7measure.sh's: SHEPHERD_K7_OVERLAY_ROW=0 here,
  #  or "unavail" would land on it, the one row never greyed)
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_K7_OVERLAY_ROW=0 SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_PACK=$PACK SHEPHERD_PACK_STRICT=1
    SHEPHERD_SHOT_DIR=$o/shots SHEPHERD_STREAM_HASH=$o/stream-hash.txt "$@")
  [[ $pad != - ]] && envs+=(SHEPHERD_PAD_FILE=$pad)
  if [[ $touch != - ]]; then print -r -- "$touch" | tr ';' '\n' > $OUT/in/$name-touch.txt; envs+=(SHEPHERD_TOUCH_FILE=$OUT/in/$name-touch.txt); fi
  print -l -- $envs > $o/env.txt
  (cd $root && env $envs dotnet $bin/Shepherd.Engine.dll boot $frames 100000 > $o/console.txt 2>&1)
  cp $root/runtime/logs/engine.log $o/ 2>/dev/null; [[ -f $root/vita-settings.txt ]] && cp $root/vita-settings.txt $o/
  (( keep )) || [[ $name == pad ]] || rm -rf $root
}
P=$OUT/in/pad.txt; S500=SHEPHERD_K6_SIM_ARM500=1
# kit #6 integration: every OTHER kit #6 switch off (the mem, engine, fps, gpulight tracks and the integration's), for the comparisons with
# kit #5 (BASE_BIN) -- since the merge the engine track's resting mouse alone moves every menu frame after the press (+3.5 s)
K6OTHER=(SHEPHERD_SPINE_READSHARE=0 SHEPHERD_SPINE_STREAM=0 SHEPHERD_PROPS_SHAPED=0 SHEPHERD_PTR_INDEX=0 SHEPHERD_LIVE_INDEX=0 SHEPHERD_NOBJ_BLOCK=32 SHEPHERD_K6_ENGINE=0
  SHEPHERD_FPS_VERTEX=0 SHEPHERD_ICALL_HITS=64 SHEPHERD_FPS_UIBLOCKS=512 SHEPHERD_FPS_UISCROLL=0 SHEPHERD_GPU_LIGHT=0 SHEPHERD_K6_STABLE_ORDER=0 SHEPHERD_K6_FULLSCREEN=0
  SHEPHERD_K6_GC_PAUSE=0 SHEPHERD_VIDEO_LENGTHS=0)
run pad 2760 $P - $S500 SHEPHERD_MENU_PROBE=m+1030,m+1100,m+1160,m+1900 SHEPHERD_SHOT_FRAMES=1960,2716 &
run unavail 1900 $OUT/in/pad-bottom.txt "m+1100:tap:600,418" SHEPHERD_K6_SIM_CORE3=0 SHEPHERD_MENU_PROBE=m+1060 SHEPHERD_SHOT_FRAMES=1890 &
run touch 2500 $OUT/in/pad-touch.txt "m+244:tap:480,450;m+600:tap:160,335;m+700:tap:333,115;m+760:drag:600,480:600,160:20;m+800:drag:600,480:600,160:20;m+840:drag:600,480:600,160:20;m+900:tap:600,418;m+1000:tap:711,468;m+1400:tap:160,335;m+1500:tap:333,115;m+1560:drag:600,480:600,160:20;m+1600:drag:600,480:600,160:20;m+1640:drag:600,480:600,160:20" $S500 SHEPHERD_SHOT_FRAMES=1700,1860,2490 &
run wall 1900 $OUT/in/pad-bottom.txt - SHEPHERD_TIME=wall SHEPHERD_FRAME_MS=16 SHEPHERD_SHOT_FRAMES=1890 &   # (16 ms a frame: the wall clock at ~60 fps)
run off 2760 $P - $S500 SHEPHERD_K6_TOGGLES=0 SHEPHERD_K6_SNAPPOOL=0 $K6OTHER SHEPHERD_MENU_PROBE=m+1900 & wait
run persist 1900 $OUT/in/pad-bottom.txt - ROOT=$OUT/root-pad $S500 SHEPHERD_MENU_PROBE=m+1060 SHEPHERD_SHOT_FRAMES=1890
cp -R $OUT/root-pad $OUT/root-reset
cp -R $OUT/root-pad $OUT/root-resetgame; cp -R $OUT/root-pad $OUT/root-resetclose
run reset 1900 $OUT/in/pad-reset.txt - ROOT=$OUT/root-reset $S500 SHEPHERD_MENU_PROBE=m+800,m+860,m+1300 SHEPHERD_SHOT_FRAMES=1890 &
run resetgame 2100 $OUT/in/pad-resetgame.txt - ROOT=$OUT/root-resetgame $S500 SHEPHERD_MENU_PROBE=m+790,m+1270 SHEPHERD_SHOT_FRAMES=2090 &
run resetclose 1800 $OUT/in/pad-resetclose.txt - ROOT=$OUT/root-resetclose $S500 SHEPHERD_MENU_PROBE=m+790 & wait
FSP=SHEPHERD_MENU_PROBE=m+870,m+899,m+912,m+990
run fsdialog 1900 $OUT/in/pad-fsdialog.txt - $FSP &
run fsdrop 1900 $OUT/in/pad-fsdrop.txt - $FSP &
run fsdialog0 1900 $OUT/in/pad-fsdialog.txt - $FSP SHEPHERD_K6_FULLSCREEN=0 &
run fsdrop0 1900 $OUT/in/pad-fsdrop.txt - $FSP SHEPHERD_K6_FULLSCREEN=0 & wait
if [[ -n $BASE ]]; then
  run off-base 2760 $P - BIN=$BASE &
  run tour 4900 $V6/pad-menu-tour.txt - $K6OTHER &
  run tour-base 4900 $V6/pad-menu-tour.txt - BIN=$BASE & wait
  run tour-off 4900 $V6/pad-menu-tour.txt - SHEPHERD_K6_TOGGLES=0 SHEPHERD_K6_SNAPPOOL=0 $K6OTHER &
  # (kit #6 review fix) the stable order alone: every OTHER kit #6 switch off ($K6OTHER without its SHEPHERD_K6_STABLE_ORDER=0)
  K6OTHER_NOST=(${K6OTHER:#SHEPHERD_K6_STABLE_ORDER=0})
  run tour-st 4900 $V6/pad-menu-tour.txt - $K6OTHER_NOST SHEPHERD_K6_STABLE_ORDER=1 &
  run tour-st-norows 4900 $V6/pad-menu-tour.txt - $K6OTHER_NOST SHEPHERD_K6_STABLE_ORDER=1 SHEPHERD_K6_TOGGLES=0 & wait
fi
rm -rf $OUT/root-pad $OUT/root-reset $OUT/root-resetgame $OUT/root-resetclose
v=PASS
ranges() { python3 -c 'import sys
f=[int(x) for x in open(sys.argv[1]) if x.strip()]
r=[];s=p=None
for x in f:
  if s is None: s=p=x
  elif x==p+1: p=x
  else: r.append((s,p)); s=p=x
if s is not None: r.append((s,p))
print(" ".join(f"{a}-{b}" for a,b in r))' $1; }
check() { local name=$1 what=$2; shift 2; if eval "$@"; then echo "PASS $name: $what"; else echo "FAIL $name: $what"; v=FAIL; fi; }
has() { grep -qF -- "$2" $OUT/$1/engine.log; }
probe() { grep -h "\[menu\] frame" $OUT/$1/engine.log | sed -n "${2}p"; }
for n in pad unavail touch off persist reset resetgame resetclose; do a=$(grep -m1 "scene 'Main Menu':" $OUT/$n/engine.log | grep -oE '^\[ *[0-9]+\]' | tr -d '[] '); [[ $a == 806 ]] || echo "note $n: the Main Menu arrived at f$a, not f806 (the frames assume f806)"; done
check pad "pad: 4th CPU Core reached and turned Off, CPU Speed -> 500 MHz (read back 500), saved" 'has pad "[k6] 4th CPU Core Off: 4th core open (core-unlock plugin), workers on USER_2" && has pad "[k6] CPU Speed 500 MHz (the owner'"'"'s choice): the clock reads back 500 MHz" && [[ "$(probe pad 1)" == *"selected='"'"'4th CPU Core'"'"'"* && "$(probe pad 3)" == *"selected='"'"'CPU Speed'"'"'"* ]]'
check pad "Settings closed and opened again: the rows show Off / 500 MHz" '[[ $(grep -c "PS Vita Settings rows added" $OUT/pad/engine.log) == 2 ]] && grep "PS Vita Settings rows added" $OUT/pad/engine.log | tail -1 | grep -qF "4th CPU Core Off, CPU Speed 500 MHz" && [[ "$(probe pad 4)" == *"selected='"'"'CPU Speed'"'"'"* ]]'
check pad "vita-settings.txt = core3=off, cpu_mhz=500" 'grep -qx "core3=off" $OUT/pad/vita-settings.txt && grep -qx "cpu_mhz=500" $OUT/pad/vita-settings.txt'
check persist "a new start: the file read at boot, applied before the pack opens, the rows show it" 'has persist "vita-settings.txt: 4th CPU Core Off, CPU Speed 500 MHz" && has persist "[k6] CPU Speed 500 MHz (the saved choice" && has persist "the clock reads back 500 MHz" && grep "PS Vita Settings rows added" $OUT/persist/engine.log | grep -qF "4th CPU Core Off, CPU Speed 500 MHz"'
check touch "by touch: the list dragged to its end, the toggle tapped (Off), CPU Speed's arrow tapped (500 MHz); reopened by touch: Off / 500" 'has touch "[k6] 4th CPU Core Off" && has touch "[k6] CPU Speed 500 MHz (the owner'"'"'s choice): the clock reads back 500 MHz" && grep "PS Vita Settings rows added" $OUT/touch/engine.log | tail -1 | grep -qF "4th CPU Core Off, CPU Speed 500 MHz"'
check unavail "no plugin: both rows greyed out, skipped by the pad (Depth of Field stays selected), a tap on the toggle changes nothing" 'has unavail "4th CPU Core Off (not available), CPU Speed 444 MHz (500 not available)" && [[ "$(probe unavail 1)" == *"selected='"'"'Depth of Field'"'"'"* ]] && ! has unavail "[k6] 4th CPU Core On" && ! has unavail "[k6] 4th CPU Core Off:" && [[ ! -f $OUT/unavail/vita-settings.txt ]]'
check reset "Reset All (square, right to Accept, cross): the rows back to On / 444 MHz, the file too" '[[ "$(probe reset 2)" == *"label='"'"'Accept'"'"'"* ]] && has reset "[k6] the game'"'"'s Reset All" && grep -qx "core3=on" $OUT/reset/vita-settings.txt && grep -qx "cpu_mhz=444" $OUT/reset/vita-settings.txt'
check resetgame "Reset All on the Game tab (the Graphics tab not shown yet): the file back to On / 444 MHz at once, the rows built later show On / 444" '[[ "$(probe resetgame 1)" == *"label='"'"'Accept'"'"'"* ]] && has resetgame "[k6] the game'"'"'s Reset All: 4th CPU Core and CPU Speed back to their defaults (On, 444 MHz) (the rows not built yet)" && grep -qx "core3=on" $OUT/resetgame/vita-settings.txt && grep -qx "cpu_mhz=444" $OUT/resetgame/vita-settings.txt && grep "PS Vita Settings rows added" $OUT/resetgame/engine.log | grep -qF "4th CPU Core On, CPU Speed 444 MHz" && [[ "$(probe resetgame 2)" == *"selected='"'"'CPU Speed'"'"'"* ]] && (( $(grep -m1 -n "the game'"'"'s Reset All" $OUT/resetgame/engine.log | cut -d: -f1) < $(grep -m1 -n "PS Vita Settings rows added" $OUT/resetgame/engine.log | cut -d: -f1) ))'
check resetclose "Reset All on the Game tab, Settings closed right after: the file back to On / 444 MHz, no rows built" '[[ "$(probe resetclose 1)" == *"label='"'"'Accept'"'"'"* ]] && has resetclose "[k6] the game'"'"'s Reset All" && grep -qx "core3=on" $OUT/resetclose/vita-settings.txt && grep -qx "cpu_mhz=444" $OUT/resetclose/vita-settings.txt && ! has resetclose "PS Vita Settings rows added"'
check wall "the play clock: the readout shows the clock and the frames per second" 'grep -qE "\[k6\] CPU Clock readout '"'"'444 MHz / [0-9]+ fps'"'"'" $OUT/wall/engine.log'
check off "SHEPHERD_K6_TOGGLES=0 SHEPHERD_K6_SNAPPOOL=0: no [k6] line, no row (the pad ends on Depth of Field), no file" '! grep -F "[k6]" $OUT/off/engine.log | grep -qvF "[k6] engine track OFF" && [[ "$(probe off 1)" == *"selected='"'"'Depth of Field'"'"'"* ]] && [[ ! -f $OUT/off/vita-settings.txt ]]'
FSV="FullScreen Mode value changed"
check fsdialog "FullScreen Mode after Reset All's dialog (Back) + at once up / left / left: never changed (greyed in the release frame, value guarded)" 'has fsdialog "FullScreen Mode greyed out and fixed at FullScreen" && [[ "$(probe fsdialog 1)" == *"label='"'"'Back'"'"'"* ]] && ! has fsdialog "$FSV"'
check fsdrop "FullScreen Mode after the Resolution dropdown (opened, closed) + at once up / left / left: never changed" 'has fsdrop "FullScreen Mode greyed out and fixed at FullScreen" && ! has fsdrop "$FSV"'
check fsdialog0 "the same pads with SHEPHERD_K6_FULLSCREEN=0 (the PC row): the value changes (the pads reach the row after each release)" 'has fsdialog0 "$FSV" && has fsdrop0 "$FSV" && ! has fsdialog0 "FullScreen Mode greyed out"'
if [[ -n $BASE ]]; then
  check off "every switch off: the stream hash = kit #5's on every frame ($(wc -l < $OUT/off/stream-hash.txt | tr -d ' ') frames)" 'cmp -s $OUT/off/stream-hash.txt $OUT/off-base/stream-hash.txt'
  diff <(cut -f1,5 $OUT/tour/stream-hash.txt) <(cut -f1,5 $OUT/tour-base/stream-hash.txt) | grep '^<' | cut -c3- | cut -f1 > $OUT/tour-diff-frames.txt
  gfx=$(grep -m1 "PS Vita Settings rows added" $OUT/tour/engine.log | grep -oE '^\[ *[0-9]+\]' | tr -d '[] ')
  echo "tour: $(wc -l < $OUT/tour-diff-frames.txt | tr -d ' ') of $(wc -l < $OUT/tour/stream-hash.txt | tr -d ' ') frames differ from kit #5: $(python3 -c 'import sys
f=[int(x) for x in open(sys.argv[1]) if x.strip()]
r=[];s=p=None
for x in f:
  if s is None: s=p=x
  elif x==p+1: p=x
  else: r.append((s,p)); s=p=x
if s is not None: r.append((s,p))
print(" ".join(f"{a}-{b}" for a,b in r))' $OUT/tour-diff-frames.txt) (the rows were added at f$gfx)"
  check tour "the menu tour: no frame differs before the Graphics tab first shows (f$gfx), the tour's END the same" '[[ -z "$(awk -v g=$gfx "\$1 < g" $OUT/tour-diff-frames.txt)" ]] && [[ "$(tail -1 $OUT/tour/stream-hash.txt | cut -f5)" == "$(tail -1 $OUT/tour-base/stream-hash.txt | cut -f5)" ]]'
  check tour-off "every switch of the track off: the whole tour = kit #5's stream on every frame ($(wc -l < $OUT/tour-off/stream-hash.txt | tr -d ' ') frames)" 'cmp -s $OUT/tour-off/stream-hash.txt $OUT/tour-base/stream-hash.txt'
  # the Graphics tab shows from its r press (m+1534; m+2314 on the way back) until ~8 frames after the press that leaves it (m+1714 / m+2344): +16 kept
  m=$(grep -m1 "scene 'Main Menu':" $OUT/tour-st/engine.log | grep -oE '^\[ *[0-9]+\]' | tr -d '[] ')
  diff <(cut -f1,5 $OUT/tour-st/stream-hash.txt) <(cut -f1,5 $OUT/tour-st-norows/stream-hash.txt) | grep '^<' | cut -c3- | cut -f1 > $OUT/stable-diff-frames.txt
  diff <(cut -f1,5 $OUT/tour-st-norows/stream-hash.txt) <(cut -f1,5 $OUT/tour-base/stream-hash.txt) | grep '^<' | cut -c3- | cut -f1 > $OUT/stable-vs-k5-frames.txt
  out=$(awk -v m=$m '!(($1 >= m+1534 && $1 <= m+1730) || ($1 >= m+2314 && $1 <= m+2360))' $OUT/stable-diff-frames.txt | head -5 | tr '\n' ' ')
  echo "stable: rows vs no rows $(wc -l < $OUT/stable-diff-frames.txt | tr -d ' ') frames differ ($(ranges $OUT/stable-diff-frames.txt)); outside the Graphics tab: ${out:-none}; the stable order itself vs kit #5 (no rows): $(wc -l < $OUT/stable-vs-k5-frames.txt | tr -d ' ') frames ($(ranges $OUT/stable-vs-k5-frames.txt | cut -c1-200))"
  check stable "SHEPHERD_K6_STABLE_ORDER=1: the tour with the rows differs from the tour without them only while the Graphics tab shows, the END the same" '[[ -s $OUT/stable-diff-frames.txt && -z "$out" ]] && [[ "$(tail -1 $OUT/tour-st/stream-hash.txt | cut -f5)" == "$(tail -1 $OUT/tour-st-norows/stream-hash.txt | cut -f5)" ]]'
fi
echo "RESULT: $v"
osascript -e 'set volume output muted true' 2>/dev/null
[[ $v == PASS ]]
