#!/bin/zsh
# SHEPHERD-VITA device kit #9 (track runtime, vita/notes/kit9-runtime.md): the kit #9 AOT cross compilers -- kit #8's compilers PLUS
# vita/patches/k9-aot-compiler.patch (R5 SHEPHERD_AOT_ORDER: the profile's hot methods first; R3 SHEPHERD_AOT_SKIP_INSTANCES /
# SHEPHERD_AOT_INSTANCES_OUT: the dead copies of generic instances left out) -- each from a PRIVATE copy of its fork, into its own
# directory; the shared forks, obj/ and kit #5's compilers are only read.
#   build-cross-k9.sh llvm <dest>      the LLVM compiler of the LLVM apps (= kit #5's llvm-compiler: the fork + vita/llvm/fork-llvm-armhf-cc.patch +
#                                      vita/llvm/fork-eh-vfp.patch) + the k9 patch -> <dest>/obj/cross-llvm-armhf/out (bin/mono-aot-cross, llvm-bin/)
#   build-cross-k9.sh gameaot <dest>   the game-AOT compiler of 00095 / 00194 (= gameaot/proto/obj/cross-vita: runtime-fix + the partial-AOT
#                                      patch A-D + fork-gsharedvt-unop.patch) + the k9 patch -> <dest>/obj/cross-vita/out
# Both kinds also get vita/patches/k9-divfast-compiler.patch (E2 divfast, session 2: SHEPHERD_AOT_DIVFAST=1 = an inline guard in front of
# every emulated division / remainder -- the emulation function called directly, without its wrapper, whenever the divisor cannot raise;
# 64-bit operands that fit in 32 bits divided by the 32-bit function; anything else = the wrapper call as before) and, first,
# vita/patches/k9-divfast-flag.patch (review fix: the runtime's switch word every fast path checks -- SHEPHERD_K9_DIVFAST /
# SHEPHERD_K9_DIVMOD in the env file -- registered as a JIT icall data address; the kit #9 runtimes carry the same patch, so compiler and
# runtime agree on its id).
# With none of the patches' environment variables set the compiler writes what kit #8's writes (the patches' own rule; checked by
# check-cross-k9.sh with the `deterministic` AOT option).
# DEVICE KIT #10 (track aot, vita/notes/kit10-aot.md): SHEPHERD_K10_COMPILER=1 also applies vita/patches/k10-aot-compiler.patch (last, both
# kinds): AOT that learns from play (SHEPHERD_AOT_LEARNED / _LEARNED_IMAGE), cold code for size (SHEPHERD_AOT_COLD / _COLD_MODE / _COLD_HOT),
# the hot methods' inline limit and census (SHEPHERD_AOT_HOT_INLINE / _HOT_INLINE_LIMIT / SHEPHERD_AOT_INLINE_CENSUS) -- with none of those set
# the compiler writes what the kit #9 compiler writes (check-cross-k9.sh against compilers3). Unset = the kit #9 compiler.
# env: SHEPHERD_RUNTIME (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime), SHEPHERD_GAMEAOT_RUNTIME (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/proto/runtime),
#      RECOPY=1 refreshes the private copy.
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH"
B=${SHEPHERD_WORK:-$HOME/shepherd-work}
KIND=${1:?usage: build-cross-k9.sh llvm|gameaot <dest>}; D=${2:?usage: build-cross-k9.sh llvm|gameaot <dest>}
mkdir -p "$D"; D=${D:a}
for p in $B/runtime $B/obj $B/gameaot $B/kit5 $B/kit6 $B/kit7 $B/kit8; do case $D/ in "$p"/*) echo "refusing to write into a shared tree ($p): $D"; exit 1 ;; esac; done
export TMPDIR=${TMPDIR:-$D/tmp}; mkdir -p $TMPDIR
K9P=$V/patches/k9-aot-compiler.patch
K9D=$V/patches/k9-divfast-compiler.patch   # (session 2) E2 divfast: SHEPHERD_AOT_DIVFAST
K9F=$V/patches/k9-divfast-flag.patch       # (review fix) divfast's run-time switch word (needed by K9D's guards)
K10C=$V/patches/k10-aot-compiler.patch     # (device kit #10, track aot) SHEPHERD_K10_COMPILER=1
apply () {   # apply <patch> <marker string> <file with the marker>
  local p=$1 mark=$2 mf=$3
  grep -q "$mark" "$D/runtime/$mf" && return 0
  (cd $D/runtime && patch -p1 --dry-run < "$p" > $TMPDIR/apply-${p:t}.log 2>&1) || { echo "$p: dry run failed against $D/runtime:"; cat $TMPDIR/apply-${p:t}.log; exit 1; }
  (cd $D/runtime && patch -s -p1 < "$p")
}
M=src/mono/mono/mini
case $KIND in
  llvm)
    R=${SHEPHERD_RUNTIME:-$B/runtime}
    if [[ ! -d $D/runtime/src/mono || ${RECOPY:-0} == 1 ]]; then mkdir -p $D/runtime; rsync -a --delete --exclude /src/libraries --exclude /src/tests "$R/" "$D/runtime/"; fi
    apply $V/llvm/fork-llvm-armhf-cc.patch SHEPHERD_LLVM_ARMHF_CC $M/mini-llvm.c
    apply $V/llvm/fork-eh-vfp.patch SHEPHERD_EH_VFP_FIX $M/exceptions-arm.c
    apply $K9P "SHEPHERD-VITA device kit #9, track runtime" $M/aot-compiler.c
    apply $K9F "k9-divfast-flag.patch" $M/mini-runtime.c
    apply $K9D "SHEPHERD-VITA device kit #9, track runtime: E2 divfast" $M/decompose.c
    [[ ${SHEPHERD_K10_COMPILER:-0} == 1 ]] && apply $K10C "SHEPHERD-VITA device kit #10, track aot (vita/patches/k10-aot-compiler.patch" $M/aot-compiler.c
    SHEPHERD_RUNTIME=$D/runtime SHEPHERD_CROSS_LLVM_OBJ=$D/obj/cross-llvm-armhf nice -n 10 sh $V/llvm/build-cross-llvm.sh > $D/build.log 2>&1 || { tail -30 $D/build.log; exit 1; }
    X=$D/obj/cross-llvm-armhf/out/bin/mono-aot-cross ;;
  gameaot)
    R=${SHEPHERD_GAMEAOT_RUNTIME:-$B/gameaot/proto/runtime}
    if [[ ! -d $D/runtime/src/mono || ${RECOPY:-0} == 1 ]]; then mkdir -p $D/runtime; rsync -a --delete --exclude /src/libraries --exclude /src/tests "$R/" "$D/runtime/"; fi
    apply $K9P "SHEPHERD-VITA device kit #9, track runtime" $M/aot-compiler.c
    apply $K9F "k9-divfast-flag.patch" $M/mini-runtime.c
    apply $K9D "SHEPHERD-VITA device kit #9, track runtime: E2 divfast" $M/decompose.c
    [[ ${SHEPHERD_K10_COMPILER:-0} == 1 ]] && apply $K10C "SHEPHERD-VITA device kit #10, track aot (vita/patches/k10-aot-compiler.patch" $M/aot-compiler.c
    O=$D/obj/cross-vita; mkdir -p $O
    cp $B/obj/cross-vita/offsets-arm-vita.h $O/offsets-arm-vita.h
    if [[ ! -f $O/build.ninja ]]; then
      (cd $O && cmake -G Ninja -DCMAKE_MAKE_PROGRAM=${NINJA:-ninja} -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 -DENABLE_ICALL_EXPORT=1 \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS="-arch arm64" -DCMAKE_CXX_FLAGS="-arch arm64" -DAOT_COMPONENTS=1 -DSTATIC_COMPONENTS=1 \
        -DCMAKE_INSTALL_PREFIX=$O/out -DAOT_TARGET_TRIPLE=armv7-vita-eabihf -DENABLE_MINIMAL= -DENABLE_ICALL_SYMBOL_MAP=1 -DDISABLE_SHARED_LIBS=1 -DDISABLE_LIBS=1 \
        -DAOT_OFFSETS_FILE=$O/offsets-arm-vita.h -DGC_SUSPEND=coop -DMONO_CROSS_COMPILE_EXECUTABLE_NAME=1 $D/runtime/src/mono > $O/configure.log 2>&1) || { tail -20 $O/configure.log; exit 1; }
    fi
    nice -n 10 ${NINJA:-ninja} -C $O install > $D/build.log 2>&1 || { grep -E "error|FAILED" $D/build.log | head -30; exit 1; }
    X=$O/out/bin/mono-aot-cross ;;
  *) echo "usage: build-cross-k9.sh llvm|gameaot <dest>"; exit 2 ;;
esac
n=$(strings $X | grep -c "SHEPHERD_AOT_ORDER" || true)
{ echo "kit #9 cross compiler ($KIND) built $(date '+%Y-%m-%d %H:%M:%S') from a private copy of $R ($(git -C $R rev-parse --short HEAD 2>/dev/null || echo '?') + its working-tree patches)"
  echo "k9 patch: ${K9P:t} sha256 $(shasum -a 256 $K9P | cut -c1-16); SHEPHERD_AOT_ORDER strings in the compiler: $n"
  echo "k9 patch: ${K9D:t} sha256 $(shasum -a 256 $K9D | cut -c1-16) ($(grep -c 'E2 divfast' $D/runtime/$M/decompose.c) marker lines in decompose.c); SHEPHERD_AOT_DIVFAST strings in the compiler: $(strings $X | grep -c SHEPHERD_AOT_DIVFAST || true)"
  echo "k9 patch: ${K9F:t} sha256 $(shasum -a 256 $K9F | cut -c1-16); mono_shepherd_k9_divfast_flags strings in the compiler: $(strings $X | grep -c mono_shepherd_k9_divfast_flags || true)"
  [[ ${SHEPHERD_K10_COMPILER:-0} == 1 ]] && echo "k10 patch: ${K10C:t} sha256 $(shasum -a 256 $K10C | cut -c1-16); SHEPHERD_AOT_LEARNED strings in the compiler: $(strings $X | grep -c SHEPHERD_AOT_LEARNED || true); SHEPHERD_AOT_COLD strings: $(strings $X | grep -c SHEPHERD_AOT_COLD || true)"
  echo "mono-aot-cross sha256 $(shasum -a 256 $X | cut -c1-16)"; } > $D/K9-CROSS.txt
cat $D/K9-CROSS.txt
