#!/bin/zsh
# SHEPHERD-VITA game-AOT study (vita/gameaot/SIZES.md): static full+interp AOT of ONE owner module (a rewritten Unity module from
# runtime/ManagedVita or a game / third-party assembly of the owner's Managed folder) for the PS Vita, as its OWN module.
# OWNER-LOCAL OUTPUT: compiled game / Unity code. It is written under ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot only; never into the repo,
# git, or a shareable VPK (the script refuses an output dir inside the repo).
# usage: aot-module.sh <variant> <module>
#   variant: nollvm = Mono's A32 backend, exactly vita/shepherd/build.sh's AOT recipe (same mono-aot-cross build, same options, the
#                     same MONO_AGGRESSIVE_INLINELIMIT=256) except the P/Invoke option, see below
#            thumb  = Mono LLVM, Thumb-2 (vita/llvm/aot-vita.sh's thumb flags; mixed mode: LLVM methods Thumb-2, fallbacks A32)
#            arm    = Mono LLVM, A32
#   module:  assembly name; looked up in $GA_OWNER (the owner-managed snapshot) or $GA_BCL
# Options that differ from the engine module's on purpose:
#   - no direct-pinvoke: an owner module may declare P/Invokes into libraries the Vita does not have (steam_api, Galaxy, Rewired's
#     natives, FMOD on the Mac set...). A direct P/Invoke would be an undefined symbol at link time; late-bound, a call fails at
#     run time exactly as it does in the interpreter today.
#   - no SHEPHERD_AOT_INLINE_IMAGES allowlist: an owner module may inline the BCL, the engine and other owner modules (everything
#     it inlines is owner-local or ours). The engine module's own allowlist and foreign-code gate are untouched by this study.
#   - print-skipped,stats: print-only options (the log names every method the compiler skipped and why, and the GOT / method
#     statistics); the generated code is the same.
# env: GA_OUT (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/sizes), GA_BCL / GA_OWNER (inputs), GA_X_NOLLVM / GA_X_LLVM / GA_LLVM_BIN
#      (compiler snapshots; GA_X_* = the private gsharedvt-fix build for UnityEngine.CoreModule, see SIZES.md §3),
#      GA_EXTRA (extra --aot options, e.g. profile-only), GA_TAG (output subdir suffix)
set -euo pipefail
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
export PATH="$HOME/vitasdk/bin:$PATH"
HERE=${0:A:h}; REPO=${HERE:h:h}
GO=${GA_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/sizes}
case $GO in "$REPO"/*|"${REPO:A}"/*) echo "refusing: owner-local AOT output inside the repo ($GO)"; exit 1 ;; esac
BCL=${GA_BCL:-$GO/inputs/bcl}; OWN=${GA_OWNER:-$GO/inputs/owner-managed}
XN=${GA_X_NOLLVM:-$GO/tools/cross-vita/mono-aot-cross}
XL=${GA_X_LLVM:-$GO/tools/cross-vita-llvm/mono-aot-cross}
LB=${GA_LLVM_BIN:-$GO/tools/cross-vita-llvm/llvm-bin/}
VAR=$1; M=$2
case $VAR in
  nollvm) X=$XN; VOPTS="mtriple=armv7-vita-eabihf" ;;
  thumb)  X=$XL; VOPTS="llvm,llvm-path=$LB,mtriple=thumbv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf,mattr=+neon,mattr=+d32" ;;
  arm)    X=$XL; VOPTS="llvm,llvm-path=$LB,mtriple=armv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf,mattr=+neon,mattr=+d32" ;;
  *) echo "variant: nollvm|thumb|arm"; exit 2 ;;
esac
dll=$OWN/$M.dll; [ -f $dll ] || dll=$BCL/$M.dll; [ -f $dll ] || { echo "$M: no such assembly in $OWN or $BCL"; exit 1; }
W=$GO/aot-$VAR${GA_TAG:+-$GA_TAG}; mkdir -p $W
s=$W/$M.dll.s; o=$W/$M.dll.o; lo=$W/$M-llvm.o
export MONO_AGGRESSIVE_INLINELIMIT=${MONO_AGGRESSIVE_INLINELIMIT:-256}
opts="full,interp,static,asmonly,direct-icalls,nodebug,print-skipped,stats,$VOPTS${GA_EXTRA:+,$GA_EXTRA}"
[ $VAR != nollvm ] && opts="$opts,llvm-outfile=$lo"
rm -f $o $lo $W/$M.fail
t0=$(date +%s)
# /usr/bin/time -l: peak RSS of the compiler (the Mac build host's memory need)
if ! /usr/bin/time -l env MONO_PATH=$BCL:$OWN $X --aot=$opts,outfile=$s $dll > $W/$M.log 2> $W/$M.rusage; then
  echo "aot_s=$(( $(date +%s) - t0 )) as_s=0 failed=aot" > $W/$M.time; tail -5 $W/$M.log > $W/$M.fail; rm -f $s.bc $s.opt.bc
  echo "   $VAR $M: AOT FAILED ($(tail -1 $W/$M.log | cut -c1-160))"; exit 1
fi
t1=$(date +%s)
if ! arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o $o $s 2> $W/$M.as.log; then
  echo "aot_s=$((t1 - t0)) as_s=$(( $(date +%s) - t1 )) rss=$(awk '/maximum resident set size/ {print $1}' $W/$M.rusage) failed=as" > $W/$M.time
  head -5 $W/$M.as.log > $W/$M.fail; rm -f $o $s.bc $s.opt.bc
  n=$(grep -c 'branch out of range' $W/$M.as.log || true)
  echo "   $VAR $M: ASSEMBLER FAILED ($n x 'branch out of range'; $(grep -o 'Compiled: [0-9/]*' $W/$M.log | tail -1))"
  if [ "$n" -gt 0 ]; then
    # MEASURE-ONLY object (never linkable): every 'bl <label>' line of Mono's .s (calls to the module's PLT / methods, and the
    # bl tables of method / unbox-trampoline addresses) replaced by a 4-byte '.long 0', so the sections have their real sizes
    mkdir -p $W-measure
    sed -E 's/^bl [A-Za-z0-9_.]+$/.long 0/' $s > $W-measure/$M.dll.s
    arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o $W-measure/$M.dll.o $W-measure/$M.dll.s 2> $W-measure/$M.as.log \
      && echo "      measure-only object: $(arm-vita-eabi-size $W-measure/$M.dll.o | tail -1 | awk '{printf "text %d (%.2f MiB)", $1, $1/1048576}')"
    rm -f $W-measure/$M.dll.s
  fi
  exit 1
fi
echo "aot_s=$((t1 - t0)) as_s=$(( $(date +%s) - t1 )) rss=$(awk '/maximum resident set size/ {print $1}' $W/$M.rusage)" > $W/$M.time
rm -f $s.bc $s.opt.bc
echo "   $VAR $M: $(grep -o 'Compiled: [0-9/]*' $W/$M.log | tail -1) ($(cat $W/$M.time))"
