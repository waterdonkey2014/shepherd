#!/bin/zsh
# kit3 (sub-menus): the WHOLE Main Menu on the Mac, every sub-menu opened and closed (vita/v6/pad-menu-tour.txt: Play's save slots,
# DLC, Settings with every tab -- a value stepped down and back up on the Game and Audio tabs --, Credits, Roadmap, Achievements, the
# Quit dialog cancelled twice), GL + DXT, Vita profile, frame clock, SHEPHERD_AUDIO=off (silent), twice:
#   strict      the menu pack with SHEPHERD_PACK_STRICT=1 -- the Vita's behaviour: anything the pack lacks resolves to null
#   reference   no pack: the game's own data
# and compares them: errors / exceptions (expect the same lines, no new exception), "MISSING from pack" lines (expect 0), missing icalls
# hit, the menu probes (selected button at every screen, expect identical). Screenshots of every screen (both runs, the same frames)
# and a labelled contact sheet of the strict run.
#   usage: vita/v6/tour.sh [out dir]      (default runtime/logs/tour; the pack: runtime/vitapack/menu, or TOUR_PACK=<dir>)
#   env:   TOUR_BIN=<engine bin dir> (default engine/Shepherd.Engine/bin, the Debug build), TOUR_FRAMES (default 4900)
# Exit 0 = PASS (0 MISSING, the strict run's EXC / ERR / UER lines = the reference's, probes identical).
# The Main Menu scene lands at f806 on the frame clock (CoreCLR, Vita profile); the pad file is relative to it (m+N), the screenshot
# frames below are absolute (806 + N) and the script checks the arrival frame. The run's logs and screenshots are derived game
# output: they stay under runtime/ (git-ignored).
setopt pipe_fail
V6="${0:A:h}"; REPO="${V6:h:h}"; R="$REPO/runtime"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
OUT=${1:-$R/logs/tour}; PACK=${TOUR_PACK:-$R/vitapack/menu}; BIN=${TOUR_BIN:-$REPO/engine/Shepherd.Engine/bin}; FRAMES=${TOUR_FRAMES:-4900}
PAD=$V6/pad-menu-tour.txt
[[ -f $PACK/shepherd.pack ]] || { echo "no pack at $PACK (vita/v4/check-pack.sh)"; exit 2; }
[[ -f $BIN/Shepherd.Engine.dll ]] || { echo "no engine build at $BIN"; exit 2; }
osascript -e 'set volume output muted true' 2>/dev/null
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
M=806
# one screenshot per screen (relative to the menu's arrival): its opening (+20 / +60 frames after the press) and its settled look
SHOTS_REL=(300 540 620 820 880 1040 1200 1260 1330 1500 1600 1680 1750 1860 1960 2010 2130 2200 2300 2460 2560 2600 2700 2860 2900
           3030 3100 3220 3320 3360 3440 3520 3560 3680 3700 3760 3860 3940 4040 4090)
local -a abs; for r in $SHOTS_REL; do abs+=$((M + r)); done; SHOTS=${(j:,:)abs}
PROBES=m+300,m+520,m+700,m+800,m+1000,m+1100,m+1200,m+1320,m+1500,m+1560,m+1700,m+1740,m+1880,m+1920,m+2100,m+2140,m+2240,m+2400,m+2500,m+2600,m+3000,m+3150,m+3350,m+3500,m+3650,m+3700,m+3800,m+3880,m+3960,m+4060
rm -rf $OUT; mkdir -p $OUT
run() {   # run <name> [ENV=VAL ...]: its own root (logs, a fresh copy of the sandbox saves; never the real save folder)
  local name=$1; shift; local o=$OUT/$name root=$OUT/root-$name
  mkdir -p $o/shots $root/runtime/logs
  for d in Managed ManagedVita derived; do [[ -e $R/$d ]] && ln -s $R/$d $root/runtime/$d; done
  cp -R $R/persistent $root/runtime/persistent
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_PAD_FILE=$PAD
    SHEPHERD_SHOT_DIR=$o/shots SHEPHERD_SHOT_FRAMES=$SHOTS SHEPHERD_MENU_PROBE=$PROBES "$@")
  print -l -- $envs > $o/env.txt
  (cd $root && env $envs dotnet $BIN/Shepherd.Engine.dll boot $FRAMES 100000 > $o/console.txt 2>&1); local rc=$?
  cp $root/runtime/logs/engine.log $root/runtime/logs/missing-icalls.txt $o/ 2>/dev/null; rm -rf $root
  echo "$name: rc=$rc $(ls $o/shots/*.png(N) | wc -l | tr -d ' ') shots; $(grep -oE 'done: [0-9]+ errors, [0-9]+ exceptions' $o/engine.log | tail -1); MISSING $(grep -c 'MISSING from pack' $o/engine.log); missing icalls hit $(grep -c . $o/missing-icalls.txt 2>/dev/null)"
}
run strict SHEPHERD_PACK=$PACK SHEPHERD_PACK_STRICT=1 &
run reference &
wait
S=$OUT/strict F=$OUT/reference v=PASS
for o in $S $F; do grep -q "scene 'Main Menu':" $o/engine.log || { echo "FAIL ${o:t}: no Main Menu"; v=FAIL; }
  a=$(grep -m1 "scene 'Main Menu':" $o/engine.log | grep -oE '^\[ *[0-9]+\]' | tr -d '[] '); [[ $a == $M ]] || echo "note ${o:t}: the Main Menu arrived at f$a, not f$M (the screenshot frames assume f$M)"; done
ms=$(grep -c 'MISSING from pack' $S/engine.log); (( ms == 0 )) || { echo "FAIL: $ms MISSING lines in the strict run"; v=FAIL; }
lines() { grep -E "\] (EXC|ERR|UER) " $1/engine.log | sed -E 's/^\[ *[0-9]+\]//' | sort; }
if diff <(lines $S) <(lines $F) > $OUT/exc-diff.txt; then echo "errors/exceptions: strict = reference ($(lines $S | wc -l | tr -d ' ') lines)"
else echo "FAIL: strict and reference differ in EXC/ERR/UER lines (see $OUT/exc-diff.txt):"; head -10 $OUT/exc-diff.txt; v=FAIL; fi
probes() { grep -h '\[menu\] frame' $1/engine.log | sed -E "s/^\[ *[0-9]+\] *//"; }
if diff <(probes $S) <(probes $F) > $OUT/probe-diff.txt; then echo "menu probes: identical ($(probes $S | wc -l | tr -d ' '))"
else echo "FAIL: menu probes differ (see $OUT/probe-diff.txt)"; v=FAIL; fi
probes $S | sed -E "s/.*frame ([0-9]+): selected='([^']*)' label='([^']*)'.*/\1:\3/" | tr '\n' ' ' | fold -w 220; echo
echo "missing icalls hit (strict): $(sed -E 's/^ *[0-9]+ +//' $S/missing-icalls.txt 2>/dev/null | tr '\n' ' ')"
python3 - $S/shots $OUT/tour-strict-sheet.png <<'PY' 2>/dev/null && echo "contact sheet: $OUT/tour-strict-sheet.png"
import sys, os
from PIL import Image, ImageDraw
d, out = sys.argv[1], sys.argv[2]; fs = sorted(f for f in os.listdir(d) if f.endswith('.png'))
if not fs: sys.exit(1)
ims = [Image.open(os.path.join(d, f)).convert('RGB') for f in fs]; w, h = ims[0].width // 3, ims[0].height // 3; cols = 6
sheet = Image.new('RGB', (cols * w, ((len(ims) + cols - 1) // cols) * (h + 12)), (32, 32, 32)); g = ImageDraw.Draw(sheet)
for i, (f, im) in enumerate(zip(fs, ims)):
    x, y = (i % cols) * w, (i // cols) * (h + 12); sheet.paste(im.resize((w, h)), (x, y + 12)); g.text((x + 2, y), f, fill=(255, 255, 0))
sheet.save(out)
PY
echo "RESULT: $v"
osascript -e 'set volume output muted true' 2>/dev/null
[[ $v == PASS ]]
