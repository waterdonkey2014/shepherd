#!/bin/zsh
# SHEPHERD-VITA kit #5 (vita/notes/kit5-llvm.md): the LLVM-enabled mono-aot-cross WITH the armhf float-argument fix
# (fork-llvm-armhf-cc.patch: mini-llvm.c + mini-llvm-cpp.cpp, both compiled only into an LLVM-enabled compiler) AND the review's
# exception fix A (fork-eh-vfp.patch: exceptions-arm.c's throw trampolines, which the AOT compiler emits into CoreLib's image; the
# patch's runtime half, fixes B / C, goes into the LLVM apps' runtime: build-runtime-eh-vfp.sh), built from a PRIVATE copy of the fork
# so the shared fork and obj/cross-vita-llvm are never written. Steps: rsync the fork (without src/libraries and src/tests, which the
# cross build never reads) to $DEST/runtime, check that every file a patch touches is the fork's (= stock v8.0.31), apply the patches
# that are not applied yet, then run build-cross-llvm.sh with SHEPHERD_RUNTIME / SHEPHERD_CROSS_LLVM_OBJ pointing at the copy. Use:
#   SHEPHERD_AOT_LLVM=thumb SHEPHERD_CROSS_LLVM_OBJ=$DEST/obj/cross-llvm-armhf SHEPHERD_VITA_RUNTIME_OBJ=<build-runtime-eh-vfp.sh out> \
#     vita/shepherd/build.sh ...
# env: SHEPHERD_RUNTIME (the fork to copy; default ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime), DEST (required: a directory of your own on the
# build volume), SHEPHERD_CROSS_LLVM_OBJ (default $DEST/obj/cross-llvm-armhf), SHEPHERD_LLVM_SDK (as build-cross-llvm.sh),
# RECOPY=1 to refresh the copy, TMPDIR (default $DEST/tmp; created here: patch needs it).
# The patched compiler with SHEPHERD_LLVM_ARMHF_CC=0 SHEPHERD_EH_VFP_FIX=0 in its environment writes exactly the unpatched compiler's
# output (checked on CoreLib and Shepherd.Engine with the `deterministic` AOT option: .s, .dll.o and -llvm.o byte-identical), and
# the non-LLVM cross compiler and the Vita runtime compile neither mini-llvm file (they are in mini's llvm_sources); the Vita runtime
# is DISABLE_JIT, so fix A is not compiled into it at all.
set -euo pipefail
HERE=${0:a:h}
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
D=${DEST:?set DEST to a directory of your own (never a shared build dir)}
case $D in ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime*|${SHEPHERD_WORK:-$HOME/shepherd-work}/obj*) echo "DEST must not be the shared fork or obj/: $D"; exit 1 ;; esac
export TMPDIR=${TMPDIR:-$D/tmp}
mkdir -p "$TMPDIR" || { echo "cannot create TMPDIR $TMPDIR"; exit 1; }
if [[ ! -d $D/runtime/src/mono || ${RECOPY:-0} == 1 ]]; then
  mkdir -p $D/runtime
  rsync -a --delete --exclude /src/libraries --exclude /src/tests "$R/" "$D/runtime/"
fi
# apply <patch> <marker string> <file with the marker> <files the patch touches...>
apply () {
  local p=$1 mark=$2 mf=$3; shift 3
  grep -q "$mark" "$D/runtime/$mf" && return 0
  for f in "$@"; do cmp -s "$D/runtime/$f" "$R/$f" || { echo "$D/runtime/$f differs from the fork's: RECOPY=1"; exit 1; }; done
  local log=$TMPDIR/apply-${p:t}.log
  (cd $D/runtime && patch -p1 --dry-run < "$HERE/$p" > "$log" 2>&1) || { echo "$p: dry run failed against $D/runtime (patch says:)"; cat "$log"; exit 1; }
  (cd $D/runtime && patch -p1 < "$HERE/$p")
}
M=src/mono/mono/mini
apply fork-llvm-armhf-cc.patch SHEPHERD_LLVM_ARMHF_CC $M/mini-llvm.c $M/mini-llvm.c $M/mini-llvm-cpp.cpp
apply fork-eh-vfp.patch SHEPHERD_EH_VFP_FIX $M/exceptions-arm.c $M/exceptions-arm.c $M/unwind.c
SHEPHERD_RUNTIME=$D/runtime SHEPHERD_CROSS_LLVM_OBJ=${SHEPHERD_CROSS_LLVM_OBJ:-$D/obj/cross-llvm-armhf} \
  sh "$HERE/build-cross-llvm.sh"
XB=${SHEPHERD_CROSS_LLVM_OBJ:-$D/obj/cross-llvm-armhf}/out/bin/mono-aot-cross
strings $XB | grep -c SHEPHERD_LLVM_ARMHF_CC | sed 's/^/armhf float-argument fix strings in the compiler: /'
strings $XB | grep -c SHEPHERD_EH_VFP_FIX | sed 's/^/exception fix A strings in the compiler: /'
