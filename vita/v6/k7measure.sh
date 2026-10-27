#!/bin/zsh
# device kit #7, track measure (vita/notes/kit7-measure.md): the Mac checks of the measurement tools -- CoreCLR (the Debug build), Vita
# profile, GL + DXT, the strict menu pack, the frame clock, SHEPHERD_AUDIO=off (silent); each run its own root (a fresh copy of the sandbox
# saves, never the real save folder).
#   tour-*   the menu tour (vita/v6/pad-menu-tour.txt, 4,900 frames), the stream hash of every frame:
#            off = SHEPHERD_K7=0, norow = the overlay row off, prof = SHEPHERD_K7_PROF=time,sample + a window + the Chrome trace with types,
#            samp = sample only, zones = time,zones + SHEPHERD_K7_STALL -- each = the base build's stream on every frame (BASE_BIN = a
#            kit #6 engine build; without it: = the off run); def (the defaults) differs only while the Graphics tab shows (the fifth row)
#   row      Settings > Graphics: 15 downs to "Performance Overlay", cross (On), circle: vita-settings.txt overlay=on, the overlay drawn
#   persist  a new start with that file: the overlay from the first frame
#   reset    the file's overlay=on, Reset All (square, right, cross): overlay=off, not drawn
#   envoff   the file's overlay=on + SHEPHERD_K7_OVERLAY=0: not drawn;   rowoff: SHEPHERD_K7_OVERLAY_ROW=0: kit #6's four rows, not drawn
#   rowsave  (review fix) the file's overlay=on + SHEPHERD_K7_OVERLAY_ROW=0 + Reset All (a Save of kit #6's rows): overlay=on still in the file
#   boot     the [k7] BOOT line (time to splash, splash, work left after the splash, total) and boot-history.tsv
#   bb       SHEPHERD_K7_BB_TEST=exc@900: the Mac's black box (frames only) written at the unhandled exception
# usage: vita/v6/k7measure.sh [out dir]   (K7_BIN=<engine bin>, BASE_BIN=<kit #6 engine bin>, K7_PACK=<pack dir>). Exit 0 = every check PASS.
setopt pipe_fail
V6="${0:A:h}"; REPO="${V6:h:h}"; R="$REPO/runtime"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
OUT=${1:-$R/logs/k7measure}; PACK=${K7_PACK:-$R/vitapack/menu}; BIN=${K7_BIN:-$REPO/engine/Shepherd.Engine/bin/Debug/net8.0}; BASE=${BASE_BIN:-}
[[ -f $PACK/shepherd.pack ]] || { echo "no pack at $PACK"; exit 2; }
[[ -f $BIN/Shepherd.Engine.dll ]] || { echo "no engine build at $BIN (dotnet build engine/Shepherd.Engine -c Debug)"; exit 2; }
osascript -e 'set volume output muted true' 2>/dev/null
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
rm -rf $OUT; mkdir -p $OUT/in
TOUR=$V6/pad-menu-tour.txt
{ print -l m+244:cross m+494:down m+524:down m+554:cross m+684:r; for i in {1..15}; do print "m+$((760 + (i - 1) * 20)):down"; done; print -l m+1080:cross m+1200:circle; } > $OUT/in/row.txt
print -l m+244:cross m+494:down m+524:down m+554:cross m+684:r m+760:square m+840:right m+880:cross m+1000:circle > $OUT/in/reset.txt
print -l "# a saved choice" core3=on cpu_mhz=444 overlay=on > $OUT/in/vs-on.txt
run() {   # run <name> <frames> <pad or -> [BIN=<bin>] [SEED=<vita-settings.txt>] [ENV=VAL ...]
  local name=$1 frames=$2 pad=$3; shift 3; local o=$OUT/$name root=$OUT/root-$name bin=$BIN seed=
  while [[ $1 == BIN=* || $1 == SEED=* ]]; do [[ $1 == BIN=* ]] && bin=${1#BIN=}; [[ $1 == SEED=* ]] && seed=${1#SEED=}; shift; done
  mkdir -p $o/shots $root/runtime/logs
  for d in Managed ManagedVita derived; do [[ -e $R/$d ]] && ln -s $R/$d $root/runtime/$d; done
  cp -R $R/persistent $root/runtime/persistent; [[ -n $seed ]] && cp $seed $root/vita-settings.txt
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_PACK=$PACK SHEPHERD_PACK_STRICT=1
    SHEPHERD_SHOT_DIR=$o/shots SHEPHERD_STREAM_HASH=$o/stream-hash.txt "$@")
  [[ $pad != - ]] && envs+=(SHEPHERD_PAD_FILE=$pad)
  print -l -- $envs > $o/env.txt
  (cd $root && env $envs dotnet $bin/Shepherd.Engine.dll boot $frames 100000 > $o/console.txt 2>&1)
  for f in $root/runtime/logs/*(N.); do cp $f $o/; done; [[ -f $root/vita-settings.txt ]] && cp $root/vita-settings.txt $o/
  rm -rf $root
}
REF=off; [[ -n $BASE ]] && REF=base
[[ -n $BASE ]] && run tour-base 4900 $TOUR BIN=$BASE &
run tour-off 4900 $TOUR SHEPHERD_K7=0 &
run tour-def 4900 $TOUR &
run tour-norow 4900 $TOUR SHEPHERD_K7_OVERLAY_ROW=0 & wait
run tour-prof 4900 $TOUR SHEPHERD_K7_OVERLAY_ROW=0 SHEPHERD_K7_PROF=time,sample,win=m+495:m+594 SHEPHERD_K7_TRACE=m+500:m+530:types &
run tour-samp 4900 $TOUR SHEPHERD_K7_OVERLAY_ROW=0 SHEPHERD_K7_PROF=sample &
run tour-zones 4900 $TOUR SHEPHERD_K7_OVERLAY_ROW=0 SHEPHERD_K7_PROF=time,zones SHEPHERD_K7_STALL=8192 &
run row 2100 $OUT/in/row.txt SHEPHERD_MENU_PROBE=m+1060 SHEPHERD_SHOT_FRAMES=2090 & wait
run persist 1000 - SEED=$OUT/in/vs-on.txt SHEPHERD_SHOT_FRAMES=990 &
run reset 1900 $OUT/in/reset.txt SEED=$OUT/in/vs-on.txt SHEPHERD_SHOT_FRAMES=1890 &
run envoff 1000 - SEED=$OUT/in/vs-on.txt SHEPHERD_K7_OVERLAY=0 SHEPHERD_SHOT_FRAMES=990 &
run rowoff 1000 - SEED=$OUT/in/vs-on.txt SHEPHERD_K7_OVERLAY_ROW=0 SHEPHERD_SHOT_FRAMES=990 &
run rowsave 1900 $OUT/in/reset.txt SEED=$OUT/in/vs-on.txt SHEPHERD_K7_OVERLAY_ROW=0 & wait
run bb 1000 - SHEPHERD_K7_BB_TEST=exc@900
# the game's threaded Spine update has a known load-dependent race ("ignoring attempt to queue a skeleton on thread more than once", kit #5
# notes: it makes a run's stream diverge in the base build too, more often when the Mac is loaded): such a tour run is run once more, alone
for t in off norow prof samp zones; do
  [[ $t == $REF ]] && continue
  if grep -q "queue a skeleton on thread more than once" $OUT/tour-$t/engine.log 2>/dev/null; then
    echo "note tour-$t: the game's known Spine thread race in this run: run again, alone"
    e=($(<$OUT/tour-$t/env.txt)); e=(${e:#SHEPHERD_ROOT=*}); e=(${e:#SHEPHERD_PROFILE=vita}); e=(${e:#SHEPHERD_AUDIO=off}); e=(${e:#SHEPHERD_RENDER=gl}); e=(${e:#SHEPHERD_GL_DXT=1})
    e=(${e:#SHEPHERD_PACK=*}); e=(${e:#SHEPHERD_PACK_STRICT=1}); e=(${e:#SHEPHERD_SHOT_DIR=*}); e=(${e:#SHEPHERD_STREAM_HASH=*}); e=(${e:#SHEPHERD_PAD_FILE=*})
    rm -rf $OUT/tour-$t; run tour-$t 4900 $TOUR $e
  fi
done
v=PASS
check() { local name=$1 what=$2; shift 2; if eval "$@"; then echo "PASS $name: $what"; else echo "FAIL $name: $what"; v=FAIL; fi; }
has() { grep -qF -- "$2" $OUT/$1/engine.log; }
diffs() { paste <(cut -f1,5 $OUT/tour-$REF/stream-hash.txt) <(cut -f1,5 $OUT/$1/stream-hash.txt) | awk -F'\t' '$1"\t"$2 != $3"\t"$4 {n++} END {print n+0}'; }
lines() { wc -l < $OUT/$1/stream-hash.txt 2>/dev/null | tr -d ' '; }
# the overlay's text pixels in a screenshot's top-left corner (the font's yellow-green over the dark backdrop)
ovl() { python3 - "$1" <<'PY'
import sys, zlib, struct
def png(path):   # (no PIL needed: an RGBA / RGB PNG, filter types 0-4)
    d = open(path, 'rb').read(); i = 8; w = h = ct = 0; idat = b''
    while i < len(d):
        n, t = struct.unpack('>I4s', d[i:i + 8]); c = d[i + 8:i + 8 + n]; i += 12 + n
        if t == b'IHDR': w, h, bd, ct = struct.unpack('>IIBB', c[:10])
        elif t == b'IDAT': idat += c
    bpp = 4 if ct == 6 else 3; raw = zlib.decompress(idat); stride = w * bpp; rows = []; prev = bytearray(stride); p = 0
    for y in range(h):
        f = raw[p]; line = bytearray(raw[p + 1:p + 1 + stride]); p += 1 + stride
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0; b = prev[x]; cc = prev[x - bpp] if x >= bpp else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - cc), abs(a - cc), abs(a + b - 2 * cc)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else cc)) & 255
        rows.append(line); prev = line
    return rows, bpp
rows, bpp = png(sys.argv[1]); n = 0
for y in range(8, 20):
    for x in range(8, 60):
        r, g, b = rows[y][x * bpp], rows[y][x * bpp + 1], rows[y][x * bpp + 2]
        n += r > 180 and g > 200 and b < 170
print(n)
PY
}
for t in off norow prof samp zones; do [[ $t == $REF ]] && continue; check tour-$t "every frame's stream = the $REF run's ($(lines tour-$t) frames)" '[[ $(diffs tour-'$t') == 0 && $(lines tour-'$t') == 4900 ]]'; done
check tour-def "the defaults differ from the $REF run only while the Graphics tab shows (the fifth row): $(diffs tour-def) frames, the first f$(paste <(cut -f1,5 $OUT/tour-$REF/stream-hash.txt) <(cut -f1,5 $OUT/tour-def/stream-hash.txt) | awk -F'\t' '$2 != $4 {print $1; exit}')" '(( $(diffs tour-def) > 0 && $(diffs tour-def) <= 300 ))'
check tour-prof "prof.tsv with the window's rows, a valid Chrome trace" 'grep -q "^\[window" $OUT/tour-prof/prof.tsv && python3 -c "import json,glob,sys; d=json.load(open(glob.glob(\"$OUT/tour-prof/trace-*.json\")[0])); sys.exit(0 if len(d[\"traceEvents\"]) > 100 else 1)"'
check tour-zones "the stall rows (cold / warm per candidate loop)" 'grep -q "	stall	" $OUT/tour-zones/prof.tsv'
check row "15 downs reach Performance Overlay, cross turns it On, saved; the overlay drawn after Settings closes" 'has row "[k7] Performance Overlay On" && grep -qx "overlay=on" $OUT/row/vita-settings.txt && (( $(ovl $OUT/row/shots/f02090.png) > 50 ))'
check persist "the saved overlay=on: drawn from the start" 'has persist "Performance Overlay On" && (( $(ovl $OUT/persist/shots/f00990.png) > 50 ))'
check reset "Reset All: the overlay Off, the file overlay=off, not drawn" 'has reset "Performance Overlay Off" && grep -qx "overlay=off" $OUT/reset/vita-settings.txt && (( $(ovl $OUT/reset/shots/f01890.png) == 0 ))'
check envoff "SHEPHERD_K7_OVERLAY=0 over a saved overlay=on: not drawn" '(( $(ovl $OUT/envoff/shots/f00990.png) == 0 ))'
check rowoff "SHEPHERD_K7_OVERLAY_ROW=0: kit #6's rows (no overlay in the choices), not drawn, the file left alone" '! has rowoff "Performance Overlay" && (( $(ovl $OUT/rowoff/shots/f00990.png) == 0 )) && grep -qx "overlay=on" $OUT/rowoff/vita-settings.txt'
check rowsave "SHEPHERD_K7_OVERLAY_ROW=0 + Reset All (kit #6's rows saved): the file's overlay=on kept, the kit #6 rows at their defaults" 'has rowsave "[k6] saved vita-settings.txt" && grep -qx "overlay=on" $OUT/rowsave/vita-settings.txt && grep -qx "cpu_mhz=444" $OUT/rowsave/vita-settings.txt && grep -qx "core3=on" $OUT/rowsave/vita-settings.txt && ! has rowsave "Performance Overlay"'
check boot "the [k7] BOOT line and boot-history.tsv" 'has tour-def "[k7] BOOT time to splash" && has tour-def "WORK LEFT AFTER THE SPLASH" && [[ -f $OUT/tour-def/boot-history.tsv ]]'
check bb "the Mac's black box at an unhandled exception" 'grep -q "unhandled InvalidOperationException" $OUT/bb/blackbox.txt && (( $(grep -c "^f" $OUT/bb/blackbox.txt) > 100 ))'
rm -rf $OUT/root-*
echo "RESULT $v ($OUT)"
[[ $v == PASS ]]
