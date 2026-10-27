#!/bin/zsh
# game-aot HOT METHODS: check that a set's .aotprofile files resolve in mono-aot-cross (Vita cross compiler, the build's AOT
# options + profile=<f>,profile-only) and record what profile-only then compiles. Owner-local output under $G/hot/aotcheck.
# usage: hotcheck.sh <set dir (profiles/union/<set>)> [jobs=4]
setopt pipe_fail null_glob
G=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot; H=$G/hot; S=${1:A}; J=${2:-4}
A=$H/aotcheck; OUT=$A/${S:h:t}-${S:t}; mkdir -p $OUT $A/bcl
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp MONO_AGGRESSIVE_INLINELIMIT=256 PATH=$HOME/vitasdk/bin:$PATH
X=${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita/out/bin/mono-aot-cross
PK=${SHEPHERD_WORK:-$HOME/shepherd-work}/packs/mono.linux-arm.8.0.31/runtimes/linux-arm; OWN=$H/snap/Data/Managed
for f in $PK/lib/net8.0/*.dll $PK/native/System.Private.CoreLib.dll; do ln -sf $f $A/bcl/; done
for a in Shepherd.Hooks Shepherd.Assets Shepherd.Engine; do ln -sf $H/builds/base/pub/$a.dll $A/bcl/; done
one() {
  local p=$1 a=${${1:t}%.aotprofile} dll
  dll=$OWN/$a.dll; [[ -f $dll ]] || dll=$A/bcl/$a.dll; [[ -f $dll ]] || { echo "$a\tno dll"; return; }
  local t0=$SECONDS
  MONO_PATH=$A/bcl:$OWN $X --aot=full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,mtriple=armv7-vita-eabihf,profile=$p,profile-only,outfile=$OUT/$a.dll.s $dll > $OUT/$a.log 2>&1
  local rc=$? txt=""
  if (( rc == 0 )); then arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o $OUT/$a.dll.o $OUT/$a.dll.s 2> $OUT/$a.as.log && txt=$(arm-vita-eabi-size $OUT/$a.dll.o | tail -1 | awk '{print $1}'); rm -f $OUT/$a.dll.s; fi
  printf '%s\trc=%s\t%s\t%s\ttext=%s\t%ss\n' $a $rc "$(grep -o 'Added [0-9]* methods' $OUT/$a.log | grep -o '[0-9]*')" "$(grep -o 'Compiled: [0-9/]*' $OUT/$a.log | tail -1)" "$txt" $((SECONDS - t0))
}
for p in $S/aotprofile/*.aotprofile; do
  while (( $(jobs -r | wc -l) >= J )); do sleep 1; done
  one $p >> $OUT/results.tsv &
done
wait
sort $OUT/results.tsv
