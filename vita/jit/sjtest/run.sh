#!/bin/zsh
# SHEPHERD-VITA device kit #8 (track jit, vita/notes/kit8-jit.md): builds vita/jit/sjtest/sjtest.c with the JIT runtime's own compile command
# for shepjit.c (it #includes that very file), links it with the runtime archive and the vlx shim into a static ARM32 ELF, and runs it under
# vlx (OrbStack's qemu-user, offline, silent).
#   run.sh <configured runtime obj dir> <runtime source tree (patched)> <work dir> [programs] [seed] [heap|heap-inject]
# (heap: sjtest's heap mode -- the load / store opcodes against a harness heap, compared byte for byte; heap-inject: its control, a store
# patched off on purpose, which must FAIL; review fix, kit8-jit.md section 12)
# The vlx shim must be built (VLX_OBJ, default <work dir>/vlx-obj; vita/vlx/build.sh builds it if missing). Prints sjtest's summary line.
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h:h}
OBJ=${1:?usage: run.sh <runtime obj dir> <runtime source tree> <work dir> [programs] [seed]}; OBJ=${OBJ:a}
SRC=${2:?}; SRC=${SRC:a}; WK=${3:?}; mkdir -p "$WK"; WK=${WK:a}
PROGS=${4:-20000}; SEED=${5:-12345}; MODE=${6:-}
export PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-$WK/tmp}; mkdir -p "$TMPDIR"
VO=${VLX_OBJ:-$WK/vlx-obj}; mkdir -p "$VO"
[[ -f $VO/vlx.o ]] || VLX_OBJ=$VO zsh "$V/vlx/build.sh" > "$WK/vlx-build.log" 2>&1
CMD=$(python3 - "$OBJ/compile_commands.json" <<'EOF'
import json, sys
for e in json.load(open(sys.argv[1])):
    if e['file'].endswith('/interp/shepjit.c'):
        print(e['command']); break
else:
    sys.exit('no shepjit.c in compile_commands.json (not a JIT runtime)')
EOF
)
A=(${(z)CMD}); NEW=(); skip=0
for a in $A; do
  if (( skip )); then skip=0; continue; fi
  case $a in -o|-c) skip=1 ;; -Wl,-q) ;; *) NEW+=($a) ;; esac
done
(cd "$OBJ" && $NEW -ffunction-sections -fdata-sections -Wno-unused-function -Wno-missing-prototypes -Wno-missing-declarations \
   -I"$SRC/src/mono/mono/mini/interp" -o "$WK/sjtest.o" -c "$HERE/sjtest.c")
# the runtime archive supplies eglib and the opcode tables (mintops.c); --gc-sections drops the rest of the tier (queue, thread, reports)
arm-vita-eabi-gcc -nodefaultlibs -Wl,--gc-sections -Wl,--undefined=sceUserMainThreadStackSize -Wl,-e,_lx_start -Wl,-Map="$WK/sjtest.map" \
  -o "$WK/sjtest.elf" "$WK/sjtest.o" -Wl,--start-group "$OBJ/mono/mini/libmonosgen-2.0.a" -Wl,--end-group "$VO/vlx_entry.o" "$VO/vlx.o" \
  -Wl,--allow-multiple-definition -Wl,--whole-archive -lpthread -Wl,--no-whole-archive -lm -lc -lgcc -lc 2> "$WK/sjtest.link.log" \
  || { grep -o "undefined reference to \`[^']*'" "$WK/sjtest.link.log" | sort -u | head -20; exit 1; }
R=$WK/root; rm -rf "$R"; mkdir -p "$R/ux0/data"; echo "$PROGS $SEED $MODE" > "$R/ux0/data/sjtest.txt"
osascript -e 'set volume output muted true' 2>/dev/null || true
NAME=sjtest-$$-$RANDOM; LIMIT=${SJTEST_LIMIT:-900}
docker run --rm --pull=never --network none --name $NAME -e VLX_ROOT=/vlxroot -e VLX_USER_KIB=65536 -v "$R:/vlxroot" -v "$WK:/vlxbin:ro" alpine:3.20 /vlxbin/sjtest.elf > "$WK/sjtest.out" 2>&1 &
pid=$!; t0=$(date +%s)
while kill -0 $pid 2>/dev/null; do
  (( $(date +%s) - t0 > LIMIT )) && { echo "SJTEST FAIL TIMEOUT after ${LIMIT}s (docker kill $NAME)" >> "$WK/sjtest.out"; docker kill $NAME > /dev/null 2>&1; }
  sleep 2
done
wait $pid || true
grep -h "^SJTEST\|^MISMATCH\|^  ops\|FORVM RULE\|ForVM at exit" "$WK/sjtest.out" | head -40
grep -q "^SJTEST PASS" "$WK/sjtest.out"
