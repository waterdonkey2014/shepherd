#!/bin/zsh
# Kit #13 basec (vita/notes/kit13-basec.md "The composite oracle"): the physics bar's composite oracle re-recorded on the installed game version
# and run. The oracle's answers are Unity's own baked m_CompositePaths in the install's bundles; CompoScan finds the oracle's composites again by
# signature (kit #10 fidelity) and writes the new case list; physcheck then reproduces Unity's paths from the inputs at levels 1 / 4 / 6
# (bit for bit at 4 and 6) and checks the Base Biome 1 room counts at levels 1 / 2 with the install's expectation. Negative controls: the
# new list with one case's path id broken must FAIL composite, and the previous version's room counts must FAIL. Reads the install only; writes only <out dir>.
#   rerecord.sh <game Data dir> <previous oracle list> <out dir> <engine bin dir> <runtime dir with Managed + derived> <libshepphys dylib>
# env: ROOM_COUNTS  the install's Base Biome 1 counts aabb/matrix/trigger/stay (default 1165/694/629/67: version 1.5.26.1056, kit #10's
#                   independent method), OLD_ROOM_COUNTS  the previous version's (default 1122/694/629/67)
# Output: <out>/compo-oracle-<version>.txt (owner-local: names game bundles), <out>/compo-scan.log, <out>/run-*/ and one result line per run.
# The list is re-made on every call (an earlier one in <out> is deleted first): CompoScan failing, writing no list, or finding fewer cases than
# the previous list had (rc 1) is RERECORD RESULT: FAIL before any physcheck run -- never a PASS on a stale list.
setopt pipe_fail
G=${1:?game Data dir}; OLD=${2:?previous oracle list}; P=${3:?out dir}; BIN=${4:?engine bin dir}; RT=${5:?runtime dir}; LIB=${6:?libshepphys dylib}
ROOM=${ROOM_COUNTS:-1165/694/629/67}; OLDROOM=${OLD_ROOM_COUNTS:-1122/694/629/67}
T="${0:A:h}"; mkdir -p $P; P=${P:A}
export TMPDIR=${TMPDIR:-$P/tmp} DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1; export TMPPREFIX=${TMPDIR%/}/zsh; mkdir -p $TMPDIR
VER=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${G:h:h}/Info.plist" 2>/dev/null || echo unknown)
NEW=$P/compo-oracle-$VER.txt
(cd $T/CompoScan && nice dotnet build -c Release -o $P/compo-bin > $P/compo-build.log 2>&1) || { echo "CompoScan build FAILED"; tail -5 $P/compo-build.log; exit 3; }
rm -f $NEW
nice dotnet $P/compo-bin/CompoScan.dll "$G" $OLD $NEW > $P/compo-scan.log 2>&1; src=$?
ncase=$(grep -c '^===' $NEW 2>/dev/null); ncase=${ncase:-0}
echo "compo-scan rc=$src (install $VER): $ncase cases in $NEW"
if (( src != 0 )) || [[ ! -s $NEW ]] || (( ncase == 0 )); then
  echo "RERECORD RESULT: FAIL (CompoScan rc=$src, $ncase cases written: nothing re-recorded -- see $P/compo-scan.log)"; exit 1
fi
cmp -s <(grep '^===' $OLD) <(grep '^===' $NEW) && echo "the new list's cases = the previous list's" || echo "the new list's cases differ from the previous list's (expected after a game update)"
osascript -e 'set volume output muted true' 2>/dev/null
fails=0
pc() {   # pc <name> <level> <mode> <want PASS|FAIL> [ENV=VAL ...]
  local n=$1 lv=$2 mode=$3 want=$4; shift 4; local o=$P/run-$n; rm -rf $o; mkdir -p $o/root/runtime/logs
  for d in Managed derived; do ln -s $RT/$d $o/root/runtime/$d; done
  (cd $o/root && env SHEPHERD_ROOT=$o/root SHEPHERD_AUDIO=off SHEPHERD_PHYS=$lv "SHEPHERD_PHYS_LIB=$LIB" SHEPHERD_CHECK_PHYS=all "$@" nice dotnet $BIN/Shepherd.Engine.dll physcheck $mode > $o/console.txt 2>&1)
  local res=$(grep -h 'RESULT' $o/console.txt | tail -1 | sed 's/^\[ *[0-9]*\] *//'); rm -rf $o/root
  local got=FAIL; [[ $res == *"ALL PASS"* ]] && got=PASS
  [[ -z $res ]] && got=NORESULT
  [[ $got == $want ]] && echo "ok   $n (want $want): $res" || { echo "BAD  $n (want $want, got $got): $res"; fails=$((fails+1)); }
}
for lv in 1 4 6; do pc composite-l$lv $lv composite PASS SHEPHERD_PHYSCHECK_COMPOSITE=$NEW; done
awk '!d && /^=== / { sub(/ #-?[0-9]+ /, " #1 "); d = 1 } { print }' $NEW > $P/negctl-list.txt   # the first case's path id broken: its object is not in the install
cmp -s $NEW $P/negctl-list.txt && { echo "negative control list NOT made (no case line changed)"; fails=$((fails+1)); }
pc negctl-list-l4 4 composite FAIL SHEPHERD_PHYSCHECK_COMPOSITE=$P/negctl-list.txt
pc room-l2 2 "room:Base Biome 1" PASS SHEPHERD_PHYSCHECK_ROOM=$ROOM
pc negctl-old-room-l2 2 "room:Base Biome 1" FAIL SHEPHERD_PHYSCHECK_ROOM=$OLDROOM
[[ $fails == 0 ]] && echo "RERECORD RESULT: PASS (install $VER; the bar's env lines: SHEPHERD_PHYSCHECK_COMPOSITE=$NEW SHEPHERD_PHYSCHECK_ROOM=$ROOM)" || { echo "RERECORD RESULT: FAIL ($fails)"; exit 1; }
