#!/bin/sh
# SHEPHERD-VITA LLVM: configure + build an LLVM-enabled mono-aot-cross (host osx-arm64, target armv7-vita-eabihf, coop suspend).
# It is build-cross.sh (vita/toolchain) plus -DLLVM_PREFIX=<Mono LLVM 16 SDK for osx-arm64>: exactly what mono.proj adds for
# MonoAOTEnableLLVM=true (MonoAOTCMakeArgs "-DLLVM_PREFIX=$(MonoLLVMDir)/$(AotHostArchitecture)"). Same fork source, same
# offsets header (the TARGET runtime is unchanged: the Vita runtime does not need ENABLE_LLVM to load LLVM AOT images).
# Separate build dir; the non-LLVM cross (obj/cross-vita) is not touched.
# env: SHEPHERD_RUNTIME, SHEPHERD_CROSS_OBJ (offsets source, default obj/cross-vita), SHEPHERD_CROSS_LLVM_OBJ,
#      SHEPHERD_LLVM_SDK (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm/sdk/arm64), RECONF=1 to reconfigure.
# The LLVM SDK = the official packages runtime.osx-arm64.Microsoft.NETCore.Runtime.Mono.LLVM.{Sdk,Tools} 16.0.5-alpha.1.25311.1
# (eng/Versions.props of v8.0.31; dotnet8 feed), unpacked as llvm-init.proj's CopyLLVMToTree does: see fetch-llvm.sh.
set -e
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
C=${SHEPHERD_CROSS_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita}
B=${SHEPHERD_CROSS_LLVM_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita-llvm}
LLVM=${SHEPHERD_LLVM_SDK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm/sdk/arm64}
[ -f "$LLVM/include/llvm/Config/llvm-config.h" ] || { echo "LLVM SDK missing at $LLVM: run vita/llvm/fetch-llvm.sh"; exit 1; }
[ -x "$LLVM/bin/llc" ] && [ -x "$LLVM/bin/opt" ] || { echo "llc/opt missing in $LLVM/bin (Tools package)"; exit 1; }
mkdir -p "$B" && cd "$B"
# the offsets header describes the TARGET runtime's structs: the same file the non-LLVM cross uses (gen-offsets.sh output)
cmp -s "$C/offsets-arm-vita.h" "$B/offsets-arm-vita.h" 2>/dev/null || cp "$C/offsets-arm-vita.h" "$B/offsets-arm-vita.h"
if [ ! -f build.ninja ] || [ "$RECONF" = 1 ]; then
cmake -G Ninja -DCMAKE_MAKE_PROGRAM=${NINJA:-ninja} \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 -DENABLE_ICALL_EXPORT=1 \
  -DAOT_TARGET_TRIPLE=armv7-vita-eabihf \
  -DCMAKE_INSTALL_PREFIX=$B/out -DCMAKE_BUILD_TYPE=Release \
  -DENABLE_MINIMAL= -DENABLE_ICALL_SYMBOL_MAP=1 -DDISABLE_SHARED_LIBS=1 -DDISABLE_LIBS=1 \
  -DAOT_COMPONENTS=1 -DSTATIC_COMPONENTS=1 \
  -DAOT_OFFSETS_FILE=$B/offsets-arm-vita.h \
  -DLLVM_PREFIX=$LLVM \
  -DCMAKE_C_FLAGS="-arch arm64" -DCMAKE_CXX_FLAGS="-arch arm64" \
  -DGC_SUSPEND=coop -DMONO_CROSS_COMPILE_EXECUTABLE_NAME=1 \
  $R/src/mono > $B/configure.log 2>&1 || { tail -30 $B/configure.log; exit 1; }
fi
${NINJA:-ninja} -C $B install > $B/build.log 2>&1 || { grep -E "error|FAILED" $B/build.log | head -40; exit 1; }
# mono-aot-cross runs opt/llc from llvm-path=; keep them next to the compiler so one directory is the whole toolchain
mkdir -p $B/out/llvm-bin && cp -p "$LLVM/bin/opt" "$LLVM/bin/llc" $B/out/llvm-bin/
ls -la $B/out/bin $B/out/llvm-bin
$B/out/bin/mono-aot-cross --version | head -12
