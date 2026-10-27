#!/bin/sh
# SHEPHERD-VITA: configure + build mono-aot-cross (host osx-arm64, target armv7-vita-eabihf, coop suspend)
# env: SHEPHERD_RUNTIME, SHEPHERD_CROSS_OBJ, RECONF=1 to reconfigure. Needs offsets-arm-vita.h (gen-offsets.sh).
set -e
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
B=${SHEPHERD_CROSS_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita}
mkdir -p $B && cd $B
[ -f "$B/offsets-arm-vita.h" ] || { echo "missing $B/offsets-arm-vita.h: run configure-vita.sh then gen-offsets.sh"; exit 1; }
if [ ! -f build.ninja ] || [ "$RECONF" = 1 ]; then
cmake -G Ninja -DCMAKE_MAKE_PROGRAM=${NINJA:-ninja} \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 -DENABLE_ICALL_EXPORT=1 \
  -DAOT_TARGET_TRIPLE=armv7-vita-eabihf \
  -DCMAKE_INSTALL_PREFIX=$B/out -DCMAKE_BUILD_TYPE=Release \
  -DENABLE_MINIMAL= -DENABLE_ICALL_SYMBOL_MAP=1 -DDISABLE_SHARED_LIBS=1 -DDISABLE_LIBS=1 \
  -DAOT_COMPONENTS=1 -DSTATIC_COMPONENTS=1 \
  -DAOT_OFFSETS_FILE=$B/offsets-arm-vita.h \
  -DCMAKE_C_FLAGS="-arch arm64" -DCMAKE_CXX_FLAGS="-arch arm64" \
  -DGC_SUSPEND=coop -DMONO_CROSS_COMPILE_EXECUTABLE_NAME=1 \
  $R/src/mono > $B/configure.log 2>&1 || { tail -30 $B/configure.log; exit 1; }
fi
${NINJA:-ninja} -C $B install > $B/build.log 2>&1 || { grep -E "error|FAILED" $B/build.log | head -40; exit 1; }
ls -la $B/out/bin
