#!/bin/zsh
# game-aot PROTOTYPE: the four AOT compilers of the prototype, built from ONE private fork tree
#   $P/runtime = ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/runtime-fix (the shared fork + its Vita working-tree diff + mechanics'
#   fork-partial-aot.patch A-D) + the sizes study's fork-gsharedvt-unop.patch (CoreModule's EnumEquals<T> compiler crash).
# The shared fork (${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime) and the shared obj dirs are never touched; every build dir is under $P/obj.
#   build-compilers.sh src                 make $P/runtime (APFS clone of runtime-fix + the unop patch) if missing
#   build-compilers.sh mac|mac-llvm        osx-arm64 mono-sgen (the macbench compiler), without / with LLVM (vita/llvm/build-mac-llvm.sh's cmake)
#   build-compilers.sh cross|cross-llvm    mono-aot-cross for armv7-vita-eabihf (vita/toolchain/build-cross.sh / vita/llvm/build-cross-llvm.sh)
setopt err_exit pipe_fail
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
G=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot; P=$G/proto; R=$P/runtime; WT=${0:A:h:h:h:h}
LLVM=${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm/sdk/arm64
NINJA=${NINJA:-ninja}
common=(-G Ninja -DCMAKE_MAKE_PROGRAM=$NINJA -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 -DENABLE_ICALL_EXPORT=1
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS="-arch arm64" -DCMAKE_CXX_FLAGS="-arch arm64" -DAOT_COMPONENTS=1 -DSTATIC_COMPONENTS=1)
case $1 in
  src)
    [[ -d $R ]] || { cp -cR $G/runtime-fix $R && git -C $R apply $WT/vita/gameaot/fork-gsharedvt-unop.patch; }
    git -C $R diff --stat | tail -1 ;;
  mac|mac-llvm)
    B=$P/obj/mono-osx${1#mac}; mkdir -p $B; cd $B
    local -a l=(); [[ $1 == mac-llvm ]] && l=(-DLLVM_PREFIX=$LLVM)
    [[ -f build.ninja ]] || cmake $common -DCMAKE_INSTALL_PREFIX=$B/out $l -DGC_SUSPEND=hybrid $R/src/mono > configure.log 2>&1
    nice -n 5 $NINJA -C $B mono/mini/mono-sgen > build.log 2>&1 || { grep -E "error|FAILED" build.log | head -30; exit 1; }
    [[ $1 == mac-llvm ]] && { mkdir -p out/llvm-bin; cp -p $LLVM/bin/opt $LLVM/bin/llc out/llvm-bin/; }
    ls -la $B/mono/mini/mono-sgen ;;
  cross|cross-llvm)
    B=$P/obj/cross-vita${1#cross}; mkdir -p $B; cd $B
    cp ${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita/offsets-arm-vita.h $B/offsets-arm-vita.h
    local -a l=(); [[ $1 == cross-llvm ]] && l=(-DLLVM_PREFIX=$LLVM)
    [[ -f build.ninja ]] || cmake $common -DCMAKE_INSTALL_PREFIX=$B/out -DAOT_TARGET_TRIPLE=armv7-vita-eabihf -DENABLE_MINIMAL= \
      -DENABLE_ICALL_SYMBOL_MAP=1 -DDISABLE_SHARED_LIBS=1 -DDISABLE_LIBS=1 -DAOT_OFFSETS_FILE=$B/offsets-arm-vita.h $l \
      -DGC_SUSPEND=coop -DMONO_CROSS_COMPILE_EXECUTABLE_NAME=1 $R/src/mono > configure.log 2>&1
    nice -n 5 $NINJA -C $B install > build.log 2>&1 || { grep -E "error|FAILED" build.log | head -30; exit 1; }
    [[ $1 == cross-llvm ]] && { mkdir -p out/llvm-bin; cp -p $LLVM/bin/opt $LLVM/bin/llc out/llvm-bin/; }
    ls -la $B/out/bin/mono-aot-cross ;;
  *) sed -n '2,9p' $0; exit 2 ;;
esac
