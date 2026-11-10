#!/bin/zsh
# SHEPHERD-VITA device kit #9 (track runtime): the kit #9 compiler's "off = kit #8" rule, checked -- with none of the patch's environment
# variables set (SHEPHERD_AOT_ORDER / _SKIP_INSTANCES / _INSTANCES_OUT / _KEEP), the kit #9 compiler must write exactly what kit #8's
# compiler writes. Compiles the given modules of a build's packaged set with both compilers, the build's own AOT options + `deterministic`,
# into a scratch dir, and compares the .s and (LLVM) the -llvm.o byte for byte.
#   check-cross-k9.sh <kit #8 compiler out dir> <kit #9 compiler out dir> <build work dir> <scratch dir> [llvm|mono] <module> ...
# e.g. check-cross-k9.sh $K5RT/llvm-compiler/obj/cross-llvm-armhf/out <k9>/obj/cross-llvm-armhf/out vb/b1-measure/work /tmp/x llvm Shepherd.Hooks
set -u
A=${1:?}; K=${2:?}; W=${3:?}; T=${4:?}; MODE=${5:?}; shift 5
export PATH="$HOME/vitasdk/bin:$PATH" MONO_AGGRESSIVE_INLINELIMIT=256
unset SHEPHERD_AOT_ORDER SHEPHERD_AOT_SKIP_INSTANCES SHEPHERD_AOT_INSTANCES_OUT SHEPHERD_AOT_KEEP SHEPHERD_AOT_DIVFAST SHEPHERD_AOT_DEDUP_SHARED SHEPHERD_AOT_KEEP_GENERIC
mkdir -p $T; bad=0
for m in "$@"; do
  for side in k8 k9; do
    X=$A; [[ $side == k9 ]] && X=$K; d=$T/$side; mkdir -p $d
    if [[ $MODE == llvm ]]; then
      O="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,deterministic,llvm,llvm-path=$X/llvm-bin/,mtriple=thumbv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf -arm-assume-misaligned-load-store,mattr=+d32,llvm-outfile=$d/$m-llvm.o,outfile=$d/$m.dll.s"
    else
      O="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,deterministic,mtriple=armv7-vita-eabihf,outfile=$d/$m.dll.s"
    fi
    MONO_PATH=$W/bcl nice $X/bin/mono-aot-cross --aot=$O $W/bcl/$m.dll > $d/$m.log 2>&1 || { echo "$m ($side): the compile failed (see $d/$m.log)"; bad=1; }
    rm -f $d/$m.dll.s.bc $d/$m.dll.s.opt.bc
  done
  r="$m:"
  cmp -s $T/k8/$m.dll.s $T/k9/$m.dll.s && r+=" .s identical ($(wc -c < $T/k9/$m.dll.s | tr -d ' ') B)" || { r+=" .s DIFFERS"; bad=1; }
  if [[ $MODE == llvm ]]; then cmp -s $T/k8/$m-llvm.o $T/k9/$m-llvm.o && r+="; -llvm.o identical ($(wc -c < $T/k9/$m-llvm.o | tr -d ' ') B)" || { r+="; -llvm.o DIFFERS"; bad=1; }; fi
  echo "$r"
  rm -f $T/k8/$m.dll.s $T/k9/$m.dll.s
done
(( bad )) && echo "CHECK FAILED: the kit #9 compiler with its switches unset does not write kit #8's output" || echo "CHECK PASSED: identical output with the kit #9 switches unset"
exit $bad
