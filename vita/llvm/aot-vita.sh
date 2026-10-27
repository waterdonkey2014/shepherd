#!/bin/zsh
# SHEPHERD-VITA LLVM: static full+interp AOT of a module set for the PS Vita, with or without Mono's LLVM backend.
# usage: aot-vita.sh <set> <variant> [module ...]
#   set:     hello  = vita/hello's 11 AOT modules (CoreLib + Hello's BCL closure + Hello)
#            engine = vita/shepherd's 12 AOT modules + Shepherd.Engine (the opt-in engine-AOT set; owner-local)
#   variant: nollvm = the current Vita recipe (the snapshot of obj/cross-vita's mono-aot-cross), for same-input comparisons
#            arm    = LLVM, A32 code (mtriple=armv7-vita-eabihf)
#            thumb  = LLVM, Thumb-2 code (mtriple=thumbv7-vita-eabihf: Mono's "thumb_mixed" mode -- LLVM methods are Thumb-2,
#                     the methods LLVM cannot compile fall back to Mono's A32 backend, PLT entries called from LLVM are Thumb-2)
#   module:  optional subset of the set (default: all of it)
# Inputs are the snapshots under $L/inputs (taken 2026-10-07 from shepherd-v6-engineaot/bcl, the staged owner folder and
# hello-v5/bcl), so every variant compiles the same DLLs. Outputs: $L/aot/<set>-<variant>/: <M>.dll.s (Mono part), <M>.dll.o,
# <M>-llvm.o (LLVM part, written by llc as an object: llc's asm streamer crashes on the Mono EH table, see vita/notes/llvm-aot.md),
# <M>.log, <M>.time, <M>.stamp.
# Same knobs as vita/shepherd/build.sh: MONO_AGGRESSIVE_INLINELIMIT=256, CoreLib's trampoline pools, direct-icalls/pinvoke,
# Shepherd.Engine with SHEPHERD_AOT_INLINE_IMAGES (engine + BCL only) and direct-pinvokes=__Internal!shepherd_vita_engine_checkpoint.
# LLVM flags: llvm-path = the Mono LLVM 16 tools (opt/llc), llvmllc="-mcpu=cortex-a9 -exception-model=dwarf" (the second flag drops
# llc's ARM EHABI .ARM.exidx/.extab tables -- nothing reads them, Mono unwinds LLVM frames from its own EH table, which is
# byte-identical either way, as is the code; ~0.75 MiB in the engine eboot), mattr=+d32 (the A9's VFPv3-D32; Mono's arch_init adds
# -mattr=+vfp2,-neon,+d16 -float-abi=hard first, ours come last and win; NEON stays off since 2026-10-13, see below), opt = Mono's default
# -passes="default<O2>,place-safepoints" -spp-all-backedges (coop safepoints at every backedge + entry), llc -O2.
# -arm-assume-misaligned-load-store (2026-10-13, vita/notes/llvm-aot.md §14): llc's ARM load/store optimizer may not pair or group
# word accesses into LDRD/STRD/LDM/STM. mini-llvm gives IL loads through pointers their natural (ABI) alignment, but CoreLib's
# unsafe code reads ulongs through 2-byte-aligned char pointers (UnicodeEncoding.GetChars); Mono's A32 backend does that with
# two LDRs, which the Vita allows unaligned, while a paired LDRD faults on any address that is not word-aligned (SIGBUS in vlx).
# NEON off (was mattr=+neon): no vector access with an alignment the IR cannot guarantee, and VFP (not flush-to-zero) float maths.
set -euo pipefail
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
export PATH="$HOME/vitasdk/bin:$PATH"
L=${SHEPHERD_LLVM_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm}
XN=$L/tools/mono-aot-cross-nollvm
XL=${SHEPHERD_CROSS_LLVM_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita-llvm}/out/bin/mono-aot-cross
LLVMBIN=${SHEPHERD_CROSS_LLVM_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita-llvm}/out/llvm-bin/
SET=$1; VAR=$2; shift 2
case $SET in
  hello)  IN=$L/inputs/hello; OWNER=
          MODS=(System.Private.CoreLib System.Runtime System.Console System.Collections System.Threading System.Threading.Thread
                System.Runtime.InteropServices Microsoft.Win32.Primitives System.Memory System.Collections.Concurrent Hello) ;;
  engine) IN=$L/inputs/bcl; OWNER=$L/inputs/owner-managed
          MODS=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
                System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks
                Microsoft.Bcl.AsyncInterfaces Shepherd.Engine) ;;
  *) echo "set: hello|engine"; exit 2 ;;
esac
(( $# )) && MODS=("$@")
case $VAR in
  nollvm) X=$XN; VOPTS="mtriple=armv7-vita-eabihf" ;;
  arm)    X=$XL; VOPTS="llvm,llvm-path=$LLVMBIN,mtriple=armv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf -arm-assume-misaligned-load-store,mattr=+d32" ;;
  thumb)  X=$XL; VOPTS="llvm,llvm-path=$LLVMBIN,mtriple=thumbv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf -arm-assume-misaligned-load-store,mattr=+d32" ;;
  *) echo "variant: nollvm|arm|thumb"; exit 2 ;;
esac
W=$L/aot/$SET-$VAR; mkdir -p $W
export MONO_AGGRESSIVE_INLINELIMIT=${MONO_AGGRESSIVE_INLINELIMIT:-256}
BASE="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug"
CORELIB_TRAMPS=",ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
XSUM=$(shasum -a 256 $X | cut -c1-16)
for a in $MODS; do
  dll=$IN/$a.dll; s=$W/$a.dll.s; o=$W/$a.dll.o; lo=$W/$a-llvm.o
  EXTRA=; [ $a = System.Private.CoreLib ] && EXTRA=$CORELIB_TRAMPS
  opts="$BASE,$VOPTS$EXTRA"
  envs=(); mp=$IN
  if [ $a = Shepherd.Engine ]; then
    opts=${opts/direct-pinvoke,/direct-pinvokes=__Internal!shepherd_vita_engine_checkpoint,}   # the headless engine-AOT recipe (build.sh)
    envs=(SHEPHERD_AOT_INLINE_IMAGES="Shepherd.Engine,Shepherd.Assets,Shepherd.Hooks,System.*,Microsoft.*,mscorlib,netstandard")
    mp=$IN:$OWNER
  fi
  [ $VAR != nollvm ] && opts="$opts,llvm-outfile=$lo"
  want="x=$XSUM in=$(cat $IN/*.dll ${OWNER:+$OWNER/*.dll} | shasum -a 256 | cut -c1-16) opts=$opts inl=$MONO_AGGRESSIVE_INLINELIMIT ${envs[*]:-}"
  if [ -f $o ] && [ "$(cat $W/$a.stamp 2>/dev/null)" = "$want" ]; then echo "   $SET-$VAR $a: up to date"; continue; fi
  rm -f $W/$a.stamp $o $lo
  t0=$(date +%s)
  env $envs MONO_PATH=$mp $X --aot=$opts,outfile=$s $dll > $W/$a.log 2>&1 || { tail -20 $W/$a.log; exit 1; }
  t1=$(date +%s)
  arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o $o $s
  echo "aot_s=$((t1 - t0)) as_s=$(( $(date +%s) - t1 ))" > $W/$a.time
  rm -f $s.bc $s.opt.bc
  echo "$want" > $W/$a.stamp
  echo "   $SET-$VAR $a: $(grep -o 'Compiled: [0-9/]*' $W/$a.log | tail -1) ($(cat $W/$a.time))"
done
