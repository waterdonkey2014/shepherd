#!/bin/zsh
# game-aot PROTOTYPE: Vita (armv7) sizes of the prototype's owner module sets, with the prototype's compilers (build-compilers.sh:
# gameaot patches) through the sizes study's aot-module.sh. Owner-local output: $P/vita-sizes (compiled game / Unity code).
#   vita-sizes.sh <nollvm|thumb> <part|nocl|full> <unity|game|owner|LIST...>
#     part = profile-only + strict, profile $P/prof/part.aotprofile (PH95 + generic class instances + value-type closure)
#     nocl = the same without the closure (a runtime with mechanics' fix A);  full = whole modules
setopt pipe_fail
D=${0:A:h}; P=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/proto
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
UNITY=(${(f)"$(<$D/modules-rewritten.txt)"}); GAME=(${(f)"$(<$D/modules-game.txt)"}); OWNER=($UNITY $GAME)
v=$1 m=$2; shift 2; local -a mods
case $1 in unity) mods=($UNITY) ;; game) mods=($GAME) ;; owner) mods=($OWNER) ;; *) mods=($@) ;; esac
export GA_OUT=$P/vita-sizes GA_BCL=${GA_BCL:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/sizes/inputs/bcl} GA_OWNER=$P/snap/Data/Managed
export GA_X_NOLLVM=$P/obj/cross-vita/out/bin/mono-aot-cross GA_X_LLVM=$P/obj/cross-vita-llvm/out/bin/mono-aot-cross GA_LLVM_BIN=$P/obj/cross-vita-llvm/out/llvm-bin/
export SHEPHERD_AOT_INLINE_IMAGES="${(j:,:)OWNER},System.*,Microsoft.*,mscorlib,netstandard"
case $m in
  part) export GA_EXTRA="profile=$P/prof/part.aotprofile,profile-only" GA_TAG=part SHEPHERD_AOT_PROFILE_STRICT=1 ;;
  nocl) export GA_EXTRA="profile=$P/prof/nocl.aotprofile,profile-only" GA_TAG=nocl SHEPHERD_AOT_PROFILE_STRICT=1 ;;
  full) export GA_TAG=full ;;
esac
mkdir -p $GA_OUT
for a in $mods; do nice -n 5 zsh $D/../aot-module.sh $v $a || echo "FAILED $v-$m $a"; done
