#!/bin/sh
# SHEPHERD-VITA: generate the cross-compiler offsets header from the Vita runtime config.h (run configure-vita.sh first)
# env: SHEPHERD_RUNTIME (runtime checkout), SHEPHERD_VITA_RUNTIME_OBJ (configured Vita runtime), SHEPHERD_CROSS_OBJ
set -e
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
T=${SHEPHERD_VITA_RUNTIME_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime}
C=${SHEPHERD_CROSS_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita}
mkdir -p "$C"
# the offsets tool parses mini-arm.h, which includes the generated cpu-arm.h
${NINJA:-ninja} -C "$T" mono/mini/cpu-arm.h > /dev/null
cd "$R/src/mono" && python3 mono/tools/offsets-tool/offsets-tool.py --abi=armv7-vita-eabihf --netcore \
  --targetdir="$T" --monodir="$R/src/mono" \
  --nativedir="$R/src/native" --outfile="$C/offsets-arm-vita.h" \
  --libclang=/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib/libclang.dylib \
  --sysroot=$HOME/vitasdk/arm-vita-eabi "$@"
