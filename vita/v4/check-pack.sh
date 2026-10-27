#!/bin/zsh
# V3 part 2 regression (vita/notes/v3b-pack.md): the Vita pack. Builds the engine (Debug) and tools/VitaPack (Release), then:
#   record      (only with --record, or when a recording is missing) profile boots with SHEPHERD_PACK_RECORD:
#               GL+DXT and SDL -> runtime/vitapack/record-{gl,sdl}.txt, and (kit3) the whole Main Menu tour -- every sub-menu opened
#               and closed, vita/v6/pad-menu-tour.txt, 4900 frames -> record-tour-gl.txt: the boot recordings stop at the DLC
#               selection (f1460), before any confirm press, so the objects only a sub-menu or a confirm loads -- the MMButton
#               confirm's Resources.Load('MMVibrate/RumbleManager') prefab, resources.assets #3936 + its 2 components, reached
#               only through the ResourceManager table the baker does not follow -- were never baked (the Vita's first confirm
#               press threw "The Object you want to instantiate is null", and the Settings screen's controller stopped in its open)
#   bake        runtime/vitapack/menu from the three recordings, --language ${PACK_LANGUAGE:-en} (V6 stage 1: one I2 language and
#               its fonts, Rewired joystick maps left out); the baker re-reads the written pack and compares every value tree,
#               raw record, image and bundle list with its source                       (expect "verify: ... ALL OK")
#               FMODUnity.Settings is baked with an EMPTY EncryptionKey (the Vita needs no FMOD bank key) and the bake ends
#               with a key scan of its output                                         (expect "bank key blanked in 1", "CLEAN")
#   key scan    every file under runtime/vitapack (all packs, not only menu): the key read in memory from resources.assets,
#               never printed; a positive control on resources.assets proves the matcher (expect "0 files contain ... CLEAN")
#   pack-gl     SHEPHERD_PROFILE=vita + GL + DXT + SHEPHERD_PACK, pad boot to DLC       (expect 7 errors, 3 exceptions, MISSING 0)
#   pack-strict the same with SHEPHERD_PACK_STRICT=1: no fallback to the game data      (expect 7 errors, 3 exceptions, MISSING 0)
#   nopack      the same boot without the pack                                         (expect 7 errors, 3 exceptions)
#   tour-strict (kit3) the whole menu tour, strict pack: every sub-menu from the pack     (expect 7 errors, 3 exceptions, MISSING 0;
#               vita/v6/tour.sh compares it with the reference and takes the screenshots)
#   imgcmp      f01450 pack vs nopack (softer edges from half-res textures; the animated centre also differs run to run)
# The pack and recordings are derived game data: they stay under runtime/vitapack/ (gitignored); never copy them into git or
# share them. Logs + screenshots -> runtime/logs/v4r/check-pack-*/. Headless, offline. ~2 min (+40 s with --record).
V4="${0:A:h}"; REPO="${V4:h:h}"; R="$REPO/runtime"; ENG="$REPO/engine/Shepherd.Engine"; VP="$R/vitapack"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR "$VP"
DATA="${SHEPHERD_GAME_DATA:-${SHEPHERD_GAME_DATA}}"
B=(-p:DisableTransitiveFrameworkReferenceDownloads=true)
(cd "$ENG" && dotnet build -c Debug -o bin $B 2>&1 | grep -E " error |rror\(s\)") || exit 1
(cd "$REPO/tools/VitaPack" && dotnet build -c Release -o bin $B 2>&1 | grep -E " error |rror\(s\)") || exit 1
L="$R/logs/v4r"; PAD="SHEPHERD_PAD=1050:cross,1400:down"
TOUR="SHEPHERD_PAD_FILE=$REPO/vita/v6/pad-menu-tour.txt"
if [[ "$1" == "--record" || ! -f "$VP/record-gl.txt" || ! -f "$VP/record-sdl.txt" ]]; then
  "$V4/run1.sh" check-pack-rec-gl SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 $PAD "SHEPHERD_PACK_RECORD=$VP/record-gl.txt" -- boot 1460 50
  "$V4/run1.sh" check-pack-rec-sdl SHEPHERD_PROFILE=vita $PAD "SHEPHERD_PACK_RECORD=$VP/record-sdl.txt" -- boot 1460 50
fi
if [[ "$1" == "--record" || ! -f "$VP/record-tour-gl.txt" ]]; then   # kit3: the sub-menus (see "record" above)
  "$V4/run1.sh" check-pack-rec-tour SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_AUDIO=off "$TOUR" "SHEPHERD_PACK_RECORD=$VP/record-tour-gl.txt" -- boot 4900 100000
fi
# V6 stage 1: single-language pack (owner decision; --language picks the one I2 language + its fonts), joystick maps left out
dotnet "$REPO/tools/VitaPack/bin/VitaPack.dll" --data "$DATA" --record "$VP/record-gl.txt" --record "$VP/record-sdl.txt" --record "$VP/record-tour-gl.txt" --out "$VP/menu" --language ${PACK_LANGUAGE:-en} > "$TMPDIR/check-pack-bake.txt" 2>&1 || { tail -20 "$TMPDIR/check-pack-bake.txt"; exit 1; }
grep -E "^(recording|closure|size|textures:|GPU bytes|re-encode|verify|baked|trims|key scan)|FMODUnity.Settings'" "$VP/menu/report.txt" | sed 's/^ */    /' | cut -c1-230
dotnet "$REPO/tools/VitaPack/bin/VitaPack.dll" --data "$DATA" --scan-key "$VP" > "$TMPDIR/check-pack-keyscan.txt" 2>&1 \
  && sed 's/^/    /' "$TMPDIR/check-pack-keyscan.txt" | cut -c1-230 || { echo "FAIL key scan of $VP:"; cat "$TMPDIR/check-pack-keyscan.txt"; exit 1; }
for m in gl strict; do
  "$V4/run1.sh" check-pack-$m SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 $PAD "SHEPHERD_PACK=$VP/menu" $([[ $m == strict ]] && echo SHEPHERD_PACK_STRICT=1) -- boot 1460 50
  echo "    MISSING lines: $(grep -c 'MISSING from pack' "$L/check-pack-$m/engine.log") (expect 0)"
  grep -E "resident textures" "$L/check-pack-$m/engine.log" | sed 's/^\[ *[0-9]*\] *\[render\] */    /' | cut -c1-170
  grep -E "\[pack\] pack " "$L/check-pack-$m/engine.log" | sed 's/^\[ *[0-9]*\] *\[pack\] */    /' | cut -c1-230
done
"$V4/run1.sh" check-pack-nopack SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 $PAD -- boot 1460 50
diff <(grep -E "\] (UER|EXC) " "$L/check-pack-gl/engine.log" | sed 's/^\[ *[0-9]*\]//') <(grep -E "\] (UER|EXC) " "$L/check-pack-nopack/engine.log" | sed 's/^\[ *[0-9]*\]//') > /dev/null \
  && echo "    error/exception lines: identical with and without the pack" || echo "    error/exception lines DIFFER (diff the two engine.log files)"
python3 "$V4/imgcmp.py" "$L/check-pack-gl/f01450.png" "$L/check-pack-nopack/f01450.png" "$L/check-pack-gl/f01450-diff.png" | sed 's/^/    /'
# kit3: every sub-menu from the strict pack (the Vita's behaviour): no object may be missing anywhere in the tour
"$V4/run1.sh" check-pack-tour-strict SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_AUDIO=off "$TOUR" "SHEPHERD_PACK=$VP/menu" SHEPHERD_PACK_STRICT=1 -- boot 4900 100000
echo "    tour MISSING lines: $(grep -c 'MISSING from pack' "$L/check-pack-tour-strict/engine.log") (expect 0)"
grep -q 'MISSING from pack' "$L/check-pack-tour-strict/engine.log" && { echo "FAIL: the strict tour misses pack objects (grep 'MISSING from pack' $L/check-pack-tour-strict/engine.log)"; exit 1; }
exit 0
