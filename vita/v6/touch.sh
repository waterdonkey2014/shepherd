#!/bin/zsh
# kit #5 (track touch, vita/notes/kit5-touch.md): the front touch screen as the game's mouse, on the Mac. The engine's own touch script
# (SHEPHERD_TOUCH_FILE: screen pixels from the top-left, frames m+N after the Main Menu scene's arrival) drives the same pointer the
# Vita host's front-panel reader drives (Icalls/VitaTouch.cs), with the Vita profile, GL + DXT, the strict menu pack, the frame clock,
# SHEPHERD_AUDIO=off (silent), CoreCLR (the Debug build, like tour.sh). Each test is its own run with its own root (a fresh copy of the
# sandbox saves; never the real save folder):
#   play      tap Play -> the save slots                         settings  tap Settings -> the Settings menu
#   dropdown  Settings, the Graphics tab, the Resolution dropdown, 960 x 544 chosen (the game logs "Resolution value changed to 0")
#   engage    touch only: a tap passes "Press Any Button", a tap on Play opens the slots
#   credits   Credits by tap, scrolled by a drag (the module's ScrollRect drag; the picture differs from a run without the drag)
#   slider    Settings > Vibration Intensity dragged (the slider drag; the picture changes)
#   padback   after a tap the pad takes over: down moves the selection slot 0 -> 1 -> 2 (the game's UINavigatorNew rule)
#   off       SHEPHERD_TOUCH=0: the same tap does nothing
#   park      in the menus (no player in play) the pointer leaves the screen after the release: the game's aim point (Rewired's
#             mouse screenPosition, InputManager.General.GetMousePosition) goes to (-1,-1) -- a finger has no hover
#   rest      SHEPHERD_TOUCH_PARK=never: the pointer rests where the finger left, the aim point stays there for 120 frames (the rule
#             with a player in play: kit #5 touch review -- the game aims at the mouse in gameplay, never at the corner)
#   gameplay  only with TOUCH_INTRO_PACK=<intro pack dir> (owner-local): New Game -> the intro (SHEPHERD_STOP_SCENE=0), a tap at f3500;
#             with a player in play (PlayerFarming.playersCount > 0) the pointer must rest and the aim point never be (-1,-1); with
#             none (the intro this engine reaches today registers no PlayerFarming) it leaves the screen as in the menus (noted)
# usage: vita/v6/touch.sh [out dir]     (default runtime/logs/touch; TOUCH_PACK=<dir>, TOUCH_BIN=<engine bin dir> as tour.sh)
# Exit 0 = every test PASS. The logs and screenshots are derived game output: they stay under runtime/ (git-ignored).
setopt pipe_fail
V6="${0:A:h}"; REPO="${V6:h:h}"; R="$REPO/runtime"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
OUT=${1:-$R/logs/touch}; PACK=${TOUCH_PACK:-$R/vitapack/menu}; BIN=${TOUCH_BIN:-$REPO/engine/Shepherd.Engine/bin/Debug/net8.0}
[[ -f $PACK/shepherd.pack ]] || { echo "no pack at $PACK (vita/v4/check-pack.sh)"; exit 2; }
[[ -f $BIN/Shepherd.Engine.dll ]] || { echo "no engine build at $BIN (dotnet build engine/Shepherd.Engine -c Debug)"; exit 2; }
osascript -e 'set volume output muted true' 2>/dev/null
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
rm -rf $OUT; mkdir -p $OUT/in
print -l 'm+244:cross' > $OUT/in/pad-engage.txt
print -l 'm+244:cross' 'm+700:down' 'm+760:down' > $OUT/in/pad-down.txt
run() {   # run <name> <frames> <pad file or -> <touch items ;-separated> [ENV=VAL ...]
  local name=$1 frames=$2 pad=$3 touch=$4; shift 4; local o=$OUT/$name root=$OUT/root-$name
  print -r -- "$touch" | tr ';' '\n' > $OUT/in/$name.txt
  mkdir -p $o/shots $root/runtime/logs
  for d in Managed ManagedVita derived; do [[ -e $R/$d ]] && ln -s $R/$d $root/runtime/$d; done
  cp -R $R/persistent $root/runtime/persistent
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_PACK=$PACK SHEPHERD_PACK_STRICT=1
    SHEPHERD_SHOT_DIR=$o/shots SHEPHERD_TOUCH_FILE=$OUT/in/$name.txt "$@")
  [[ $pad != - ]] && envs+=(SHEPHERD_PAD_FILE=$pad)
  print -l -- $envs > $o/env.txt
  (cd $root && env $envs dotnet $BIN/Shepherd.Engine.dll boot $frames 100000 > $o/console.txt 2>&1)
  cp $root/runtime/logs/engine.log $o/ 2>/dev/null; rm -rf $root
}
P1=$OUT/in/pad-engage.txt
run play 1520 $P1 "m+600:tap:160,263" SHEPHERD_MENU_PROBE=m+610 &
run settings 1520 $P1 "m+600:tap:160,335" SHEPHERD_MENU_PROBE=m+610 &
run dropdown 1760 $P1 "m+600:tap:160,335;m+700:tap:333,115;m+800:tap:710,274;m+900:tap:600,344" SHEPHERD_MENU_PROBE=m+820 &
run engage 1520 - "m+244:tap:480,450;m+600:tap:160,263" SHEPHERD_MENU_PROBE=m+500,m+610 & wait
run credits0 1560 $P1 "m+600:tap:160,371" SHEPHERD_SHOT_FRAMES=1551 &
run credits 1560 $P1 "m+600:tap:160,371;m+720:drag:480,420:480,120:20" SHEPHERD_SHOT_FRAMES=1551 SHEPHERD_TOUCH_DIAG=1 &
run slider 1560 $P1 "m+600:tap:160,335;m+700:drag:634,224:520,224:15" SHEPHERD_SHOT_FRAMES=1500,1540 SHEPHERD_TOUCH_DIAG=1 &
run padback 1600 $OUT/in/pad-down.txt "m+600:tap:160,263" SHEPHERD_MENU_PROBE=m+690,m+710,m+770 &
run off 1520 $P1 "m+600:tap:160,263" SHEPHERD_TOUCH=0 SHEPHERD_MENU_PROBE=m+610 & wait
run park 1560 $P1 "m+600:tap:160,263" SHEPHERD_TOUCH_DIAG=1 &
run rest 1560 $P1 "m+600:tap:160,263" SHEPHERD_TOUCH_DIAG=1 SHEPHERD_TOUCH_PARK=never &
if [[ -n ${TOUCH_INTRO_PACK:-} ]]; then
  print -l 1050:cross 1400:cross 1600:cross 3100:cross 3160:cross 3220:cross 3280:cross 3340:cross > $OUT/in/pad-intro.txt
  PACK=$TOUCH_INTRO_PACK run gameplay 3640 $OUT/in/pad-intro.txt "3500:tap:800,100" SHEPHERD_TOUCH_DIAG=1 SHEPHERD_STOP_SCENE=0 &
fi
wait
v=PASS
probe() { grep -h "\[menu\] frame" $OUT/$1/engine.log | sed -n "${2}p"; }
check() { local name=$1 what=$2; shift 2; if eval "$@"; then echo "PASS $name: $what"; else echo "FAIL $name: $what"; v=FAIL; fi; }
for n in play settings dropdown engage credits0 credits slider padback off; do grep -q "scene 'Main Menu':" $OUT/$n/engine.log || echo "note $n: no Main Menu"; a=$(grep -m1 "scene 'Main Menu':" $OUT/$n/engine.log | grep -oE '^\[ *[0-9]+\]' | tr -d '[] '); [[ $a == 806 ]] || echo "note $n: the Main Menu arrived at f$a, not f806 (the screenshot frames assume f806)"; done
check play "tap Play -> the save slots" '[[ "$(probe play 1)" == *"label='"'"'New Save'"'"'"* ]]'
check settings "tap Settings -> the Settings menu" '[[ "$(probe settings 1)" == *"Settings Menu(Clone)"* ]]'
check dropdown "Settings > Graphics > the Resolution dropdown -> 960 x 544 chosen" '[[ "$(probe dropdown 1)" == *"label='"'"'960 x 544'"'"'"* ]] && grep -q "Resolution value changed to 0" $OUT/dropdown/engine.log'
check engage "touch only: a tap passes Press Any Button, a tap on Play opens the slots" '[[ "$(probe engage 1)" == *"label='"'"'Play'"'"'"* && "$(probe engage 2)" == *"New Save"* ]]'
check credits "Credits by tap, scrolled by a drag" 'grep -q "drag='"'"'Scroll View'"'"' dragging=True" $OUT/credits/engine.log && ! cmp -s $OUT/credits0/shots/f01551.png $OUT/credits/shots/f01551.png'
check slider "Settings > Vibration Intensity dragged" 'grep -q "drag='"'"'Slider'"'"' dragging=True" $OUT/slider/engine.log && ! cmp -s $OUT/slider/shots/f01500.png $OUT/slider/shots/f01540.png'
check padback "after a tap the pad takes over (slot 0 -> 1 -> 2)" '[[ "$(probe padback 1)" == *"selected='"'"'0'"'"'"* && "$(probe padback 2)" == *"selected='"'"'1'"'"'"* && "$(probe padback 3)" == *"selected='"'"'2'"'"'"* ]]'
check off "SHEPHERD_TOUCH=0: the same tap does nothing" '[[ "$(probe off 1)" == *"label='"'"'Play'"'"'"* ]] && ! grep -q "\[touch\] f" $OUT/off/engine.log'
aims() { grep -h "\[touch\] after f" $OUT/$1/engine.log | grep -oE "aim=\([^)]*\)" | sort | uniq -c | tr -s ' ' | tr '\n' ' '; }
check park "menus: the pointer leaves the screen after the release, the game's aim point goes to (-1,-1)" 'grep -q "pointer leave (off the screen) at (-1.0,545.0)" $OUT/park/engine.log && [[ "$(aims park)" == *"aim=(-1.0,-1.0)"* ]]'
check rest "SHEPHERD_TOUCH_PARK=never: the pointer rests at the tap, the game's aim point stays there (Unity 160,281)" 'grep -q "pointer rest (SHEPHERD_TOUCH_PARK=never) at (160.0,263.0)" $OUT/rest/engine.log && [[ "$(aims rest)" == *"aim=(160.0,281.0)"* && "$(aims rest)" != *"aim=(-1.0,-1.0)"* ]]'
if [[ -n ${TOUCH_INTRO_PACK:-} ]]; then
  pl=$(grep -h "\[touch\] f3502 pointer up" $OUT/gameplay/engine.log | grep -oE "players=[0-9]+" | cut -d= -f2)
  if (( ${pl:-0} > 0 )); then
    check gameplay "a tap in gameplay ($pl player(s) in play): the pointer rests at the tap, the aim point never (-1,-1)" 'grep -q "pointer rest (a player in play) at (800.0,100.0)" $OUT/gameplay/engine.log && [[ "$(aims gameplay)" != *"aim=(-1.0,-1.0)"* ]]'
  else
    echo "note gameplay: no player in play at the tap (players=${pl:-?}; the intro this engine reaches registers no PlayerFarming yet): the menus' rule applies"
    check gameplay "a tap in the intro with no player in play: the pointer leaves the screen (the menus' rule)" 'grep -q "pointer leave (off the screen)" $OUT/gameplay/engine.log'
  fi
fi
echo "RESULT: $v"
osascript -e 'set volume output muted true' 2>/dev/null
[[ $v == PASS ]]
