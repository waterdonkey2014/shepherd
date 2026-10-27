#!/bin/sh
# SHEPHERD-VITA LLVM (speed proxy, Mac only): a native osx-arm64 Mono (mono-sgen) with LLVM, from the same fork source, used as
# the AOT compiler for the Mac LLVM-vs-non-LLVM benchmark (vita/llvm/macbench.sh). Host = target = osx-arm64, so no offsets
# header is involved; the fork's Vita changes are all gated on HOST_VITA/TARGET_VITA/VITA (upstream behaviour here) apart
# from the env-var knobs (MONO_AGGRESSIVE_INLINELIMIT, SHEPHERD_AOT_INLINE_IMAGES: unset = upstream).
# Suspend = hybrid, Microsoft's default for osx (mono.proj: MonoThreadSuspend defaults to hybrid); hybrid and coop both emit
# safepoints, so the images carry MONO_AOT_FILE_FLAG_SAFEPOINTS and load under the sim's MONO_THREADS_SUSPEND=coop.
# The images it writes are run by Microsoft's osx-arm64 Mono 8.0.29 runtime pack (the vita-sim runtime); the compiler is 8.0.31
# (MONO_AOT_FILE_VERSION 185 in both; the sim run itself is the compatibility check, see vita/notes/llvm-aot.md).
# env: SHEPHERD_RUNTIME, SHEPHERD_MAC_LLVM_OBJ (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/mono-osx-llvm), SHEPHERD_LLVM_SDK, RECONF=1
set -e
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
B=${SHEPHERD_MAC_LLVM_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/mono-osx-llvm}
LLVM=${SHEPHERD_LLVM_SDK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm/sdk/arm64}
mkdir -p "$B" && cd "$B"
if [ ! -f build.ninja ] || [ "$RECONF" = 1 ]; then
cmake -G Ninja -DCMAKE_MAKE_PROGRAM=${NINJA:-ninja} \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 -DENABLE_ICALL_EXPORT=1 \
  -DCMAKE_INSTALL_PREFIX=$B/out -DCMAKE_BUILD_TYPE=Release \
  -DLLVM_PREFIX=$LLVM \
  -DCMAKE_C_FLAGS="-arch arm64" -DCMAKE_CXX_FLAGS="-arch arm64" \
  -DGC_SUSPEND=hybrid -DAOT_COMPONENTS=1 -DSTATIC_COMPONENTS=1 \
  $R/src/mono > $B/configure.log 2>&1 || { tail -30 $B/configure.log; exit 1; }
fi
${NINJA:-ninja} -C $B mono/mini/mono-sgen > $B/build.log 2>&1 || { grep -E "error|FAILED" $B/build.log | head -40; exit 1; }
mkdir -p $B/out/llvm-bin && cp -p "$LLVM/bin/opt" "$LLVM/bin/llc" $B/out/llvm-bin/
ls -la $B/mono/mini/mono-sgen
$B/mono/mini/mono-sgen --version | head -12
