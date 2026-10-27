#!/bin/zsh
# SHEPHERD-VITA V6 Stage B mac-check: runs the UNCHANGED Vita host (vita/shepherd/host.c, compiled with SHEPHERD_MAC_CHECK)
# on Microsoft's osx-arm64 Mono 8.0.29 (interpreter, coop), with the device's folder layout mapped into a scratch dir:
#   app0:managed/          <- the Mac BCL + the engine (the V6 sim publish, vita/v6/sim.sh publish)
#   ux0:data/shepherd/     <- managed/ (the sim's merged Managed), pack/, data/, persistent/, pad.txt, env.txt
# What it proves on the Mac: the host's assembly loading (preload hook, images from memory, BCL vs game folder rule), the
# runtime properties, the engine bridge (__Internal checkpoints, MenuProbe.Current), the GC-pause/OOM profiler hooks, the
# partition arithmetic and every log line of menu.txt, through the real boot to the Main Menu. Not the Vita parts (sceIo,
# arena, AOT, the shim): those are the device run. Exit 0 = the END line is PASS with 'DLC' selected.
# Derived game data stays on the build volume (scratch dir under $TMPDIR, deleted at exit unless KEEP=1).
set -euo pipefail
HERE=${0:a:h}
S=${HERE:h}
V=${S:h}
REPO=${V:h}
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
PUB=${SHEPHERD_V6_PUB:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/pubmono}
SIM=${SHEPHERD_SIM:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/sim}
PACK=$REPO/runtime/vitapack/menu
FRAMES=${MAC_FRAMES:-1460}
[ -f "$PUB/libcoreclr.dylib" ] || { echo "no Mono publish at $PUB: run vita/v6/sim.sh publish"; exit 1; }
[ -f "$SIM/Data/Managed/icalls.json" ] || { echo "no sim Managed at $SIM: run vita/v6/sim.sh stage"; exit 1; }
T=$(mktemp -d "$TMPDIR/shepherd-maccheck.XXXXXX")
[[ ${KEEP:-0} = 1 ]] || trap 'rm -rf "$T"' EXIT
mkdir -p "$T/include/mono/utils" "$T/app0/managed" "$T/ux0/data/shepherd"
cp "$R/src/mono/mono/utils/mono-vita.h" "$T/include/mono/utils/"

clang -O1 -g -Wall -Wno-unused-function -Wno-unused-variable -Wno-deprecated-declarations -DSHEPHERD_MAC_CHECK -DSHP_BUILD_ID='"mac-check"' \
  -I"$HERE/include" -I"$T/include" -I"$R/src/native/public" -o "$T/shepherd-mac" "$S/host.c" "$HERE/vita_mac_compat.c" \
  -L"$PUB" -lcoreclr -Wl,-rpath,"$PUB" -Wl,-export_dynamic

ln -s "$PUB"/*.dll "$T/app0/managed/"
D=$T/ux0/data/shepherd
# the owner's staged folder (vita/shepherd/stage-owner.sh) when it exists -- the exact files he copies -- else the sim's
OWNER=${MAC_OWNER:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6b/owner/shepherd}
if [[ -f "$OWNER/managed/icalls.json" && -f "$OWNER/pack/shepherd.pack" ]]; then
  echo "mac-check: ux0:data/shepherd = the staged owner folder $OWNER"
  ln -s "$OWNER/managed" "$D/managed"
  mkdir -p "$D/pack"; ln -s "$OWNER/pack/shepherd.pack" "$D/pack/shepherd.pack"
  cp -R "$OWNER/data" "$D/data"
  cp -R "$OWNER/persistent" "$D/persistent"   # a copy: the run may rewrite the sandbox saves
  cp "$OWNER/pad.txt" "$D/pad.txt"
else
  echo "mac-check: ux0:data/shepherd = the vita-sim folders ($SIM, $PACK)"
  ln -s "$SIM/Data/Managed" "$D/managed"
  mkdir -p "$D/pack"; ln -s "$PACK/shepherd.pack" "$D/pack/shepherd.pack"
  cp -R "$PACK/data" "$D/data"
  cp -R "$REPO/runtime/persistent" "$D/persistent"
  printf '# V6 pad script\n1050:cross\n1400:down\n' > "$D/pad.txt"
fi
# managed code sees the Mac file system (no shim here): the engine's paths are the mapped ones
cat > "$D/env.txt" <<EOF
SHEPHERD_ROOT=$D
SHEPHERD_GAME_DATA=$D/data
SHEPHERD_MANAGED=$D/managed
SHEPHERD_PACK=$D/pack
SHEPHERD_PERSISTENT=$D/persistent
SHEPHERD_PAD_FILE=$D/pad.txt
SHEPHERD_ARENA_MB=${MAC_ARENA_MB:-272}
MONO_THREADS_SUSPEND=coop
SHEPHERD_IL_DIR=$PUB
EOF
[[ -n ${MAC_EXTRA_ENV:-} ]] && print -l -- ${(s: :)MAC_EXTRA_ENV} >> "$D/env.txt"
printf 'boot %s 0\n' $FRAMES > "$D/args.txt"
# a stand-in for the previous run's log: the host must keep it as menu.prev.txt (renamed before the fresh menu.txt)
printf 'mac-check previous-run marker\n' > "$D/menu.txt"; printf 'stale\n' > "$D/menu.prev.txt"

echo "mac-check: running the Vita host on Mac Mono ($FRAMES frames, ~1 min)"
# MAC_PRELOAD: an interposer dylib for the run (vita/v6/tools/newlib-frag: the malloc trace behind the newlib fragmentation term)
# (exported inside this shell: SIP strips DYLD_* when they pass through /usr/bin/env)
(cd "$T" && if [[ -n ${MAC_PRELOAD:-} ]]; then export DYLD_INSERT_LIBRARIES=$MAC_PRELOAD; fi
 MAC_APP0="$T/app0" MAC_UX0="$T/ux0" MAC_NATIVE_DIR="$PUB" ./shepherd-mac > "$T/console.txt" 2>&1) || true
OUT=${MAC_OUT:-$REPO/runtime/logs/v6/mac-check}
rm -rf "$OUT"; mkdir -p "$OUT"
cp "$D/menu.txt" "$OUT/" 2>/dev/null || true
cp "$D"/runtime/logs/*(N) "$OUT/" 2>/dev/null || true
cp "$T/console.txt" "$OUT/host-console.txt"
grep -E "^(BEGIN|H[0-9]+|CP|PASS|FAIL|WARN|LIMIT|END)" "$OUT/menu.txt" | cut -c1-260 || true
if grep -qx "mac-check previous-run marker" "$D/menu.prev.txt" 2>/dev/null && ! grep -q "previous-run marker" "$D/menu.txt"; then
  echo "mac-check: the previous run's menu.txt was kept as menu.prev.txt"
else echo "FAIL mac-check: the previous run's menu.txt was not kept as menu.prev.txt"; exit 1; fi
grep -q "^PASS END .*label='DLC'" "$OUT/menu.txt" && echo "PASS mac-check (menu.txt: $OUT/menu.txt)" || { echo "FAIL mac-check (see $OUT)"; exit 1; }
