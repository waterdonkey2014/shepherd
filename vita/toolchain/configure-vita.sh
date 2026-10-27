#!/bin/sh
# SHEPHERD-VITA: configure the Mono runtime (src/mono) for arm-vita-eabi. Usage: configure-vita.sh [extra cmake args]
# V5: logging is no longer compiled out (MONO_LOG_LEVEL/MASK work on the device); JIT, SSA and the debugger agent are.
set -e
export VITASDK=$HOME/vitasdk PATH=$HOME/vitasdk/bin:$PATH
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
B=${SHEPHERD_VITA_RUNTIME_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime}
# Reproducible output: __FILE__ (g_assert/g_error messages in .rodata) and the debug info must not depend on where the
# checkout and the build directory live, so a fresh worktree builds the same .rodata/.text sizes as the main checkout.
# (The toolchain file appends -Wl,-q to CMAKE_C_FLAGS/CMAKE_CXX_FLAGS; a value given here survives that.)
PREFIX_MAP="-ffile-prefix-map=$R=/shepherd-runtime -ffile-prefix-map=$B=/shepherd-obj"
rm -rf $B && mkdir -p $B && cd $B
cmake -G Ninja -DCMAKE_MAKE_PROGRAM=${NINJA:-ninja} \
  -DCMAKE_TOOLCHAIN_FILE=$VITASDK/share/vita.toolchain.cmake -DCMAKE_C_COMPILER_TARGET=arm-vita-eabi \
  -DCMAKE_C_FLAGS="$PREFIX_MAP" -DCMAKE_CXX_FLAGS="$PREFIX_MAP" \
  -DCMAKE_BUILD_TYPE=Release -DGC_SUSPEND=coop \
  -DCMAKE_INSTALL_PREFIX=$B/out -DCMAKE_INSTALL_LIBDIR=lib \
  -DMONO_LIB_NAME=monosgen-2.0 -DMONO_SHARED_LIB_NAME=monosgen-2.0 \
  -DCLR_CMAKE_HOST_ARCH=arm -DMONO_COMPONENTS_RID=vita-arm \
  -DENABLE_MINIMAL=jit,ssa,debugger_agent -DENABLE_LAZY_GC_THREAD_CREATION=1 -DCMAKE_EXPORT_COMPILE_COMMANDS=ON -DENABLE_ICALL_EXPORT=1 \
  "$@" $R/src/mono > $B/configure.log 2>&1 || { tail -40 $B/configure.log; exit 1; }
echo "configured $B"
