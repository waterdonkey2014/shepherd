#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "Compiler + instruction differential tests"): the VITA tiers of the IL conformance suite --
# Shepherd's Vita runtime (the .NET 8 Mono fork, armv7) under vlx. Review fix: the step that builds and runs them was only in the work dir.
# A vlx-only copy of the hello app (vita/hello, SHPD00005's recipe) is built in the work dir, NEVER for the device (NOT-FOR-DEVICE.txt):
# hello-gen.py puts the same test sources in Hello.dll (static full AOT = the AOT tier) and HelloInterp.dll (interpreted = the interpreter
# tier, which runs the game's DLLs in 00097 / 00098 / 00099 / 00011); Hello's Main runs both tables and logs one line per test (M50 = AOT,
# M51 = interpreter) into ux0:data/shepherd/hello.txt.
#   vlx.sh [out dir]   -> <out>/vlx-hello.txt (default: il/runs, next to run.sh's Mac tiers) + the logs in $HT
#   then: vita-compare.py <out>/vlx-hello.txt <il/runs> [<fuzz runs dir>]   (the Vita tiers against the Mac tiers, test by test)
# env: HT (the work dir, default tests-work/hellotier; wiped and rebuilt), MONO_MINI (Mono's mini tests, as build.sh), MINI_FILES (default
#      "basic basic-float basic-long basic-math"), FUZZ (random float programs, fuzz.py output; default tests-work/il/fuzz2/fuzz-31-f.cs),
#      FPMODE (default this dir's FpMode.cs). The original run (2026-10-20: 436 tests per tier) used these defaults.
# Uses the shared runtime / cross compiler / vlx shim sources read-only (vita/hello/build.sh's defaults); nothing on a card; silent.
setopt pipe_fail err_exit
T=${0:A:h}; WT=${T:h:h:h}; B=${SHEPHERD_WORK:-$HOME/shepherd-work}
HT=${HT:-$B/kit7/tests-work/hellotier}; OUT=${1:-${IL_RUNS:-$B/kit7/tests-work/il/runs}}
case $HT in $B/*) ;; *) echo "refusing a work dir outside $B: $HT"; exit 1 ;; esac
MINI=${MONO_MINI:-$B/repro6/runtime/src/mono/mono/mini}; FPMODE=${FPMODE:-$T/FpMode.cs}
FUZZ=${FUZZ:-$B/kit7/tests-work/il/fuzz2/fuzz-31-f.cs}; files=(${=${MINI_FILES:-basic basic-float basic-long basic-math}})
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || osascript -e 'set volume output muted true' 2>/dev/null
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
mini=(); for f in $files; do mini+=($MINI/$f.cs); done
for f in $FPMODE $FUZZ $MINI/TestDriver.cs $mini; do [[ -f $f ]] || { echo "missing input: $f"; exit 1; }; done
rm -rf $HT/vita $HT/work $HT/out $HT/root $HT/vlxobj; mkdir -p $HT/vita $HT/tmp $OUT
export TMPDIR=$HT/tmp
# 1. the hello app's copy: its own sources (sce_sys, host.c, build.sh, ...), the managed tests generated in place of its managed code
rsync -a --exclude out --exclude 'obj/' --exclude 'bin/' $WT/vita/hello/ $HT/vita/hello/
ln -s $WT/vita/probe $HT/vita/probe; ln -s $WT/vita/runtime $HT/vita/runtime   # (build.sh reads ../probe's LiveArea set, ../runtime's shim)
M=$HT/vita/hello/managed; rm -f $M/*.cs $M/HelloInterp/*.cs
cp $MINI/TestDriver.cs $M/; cp $MINI/TestDriver.cs $M/HelloInterp/   # (the mini tests' driver, as is: one copy per assembly)
python3 $T/hello-gen.py $M $FPMODE $mini $FUZZ 2>&1 | tee $HT/gen.log
# 2. the build (the hello recipe: dotnet build, mono-aot-cross static full AOT of Hello + its closure, HelloInterp left interpreted, link, VPK)
SHEPHERD_HELLO_WORK=$HT/work SHEPHERD_HELLO_OUT=$HT/out nice zsh $HT/vita/hello/build.sh > $HT/build.log 2>&1 || { echo "hello build failed: $HT/build.log"; tail -5 $HT/build.log; exit 1; }
echo "vlx-only test build of the kit #7 IL conformance suite (vita/tests/il/hello-gen.py): NOT FOR THE DEVICE -- it reuses the hello's title id SHPD00005" > $HT/out/NOT-FOR-DEVICE.txt
rm -f $HT/work/aot/*.s   # (disk: the AOT assembly listings)
# 3. vlx: the shim, the build's own objects relinked against it, the root (app0 = the VPK's managed/), the run
VLX_OBJ=$HT/vlxobj zsh $WT/vita/vlx/build.sh > $HT/vlx-build.log 2>&1
SHEPHERD_HELLO_WORK=$HT/work VLX_OBJ=$HT/vlxobj zsh $WT/vita/vlx/link.sh hello $HT/work/obj/host.o $HT/vlxobj/conf-hello.elf > $HT/vlx-link.log 2>&1
SHEPHERD_HELLO_WORK=$HT/work zsh $WT/vita/vlx/stage.sh hello $HT/root > $HT/vlx-stage.log 2>&1
zsh $WT/vita/vlx/run.sh $HT/vlxobj/conf-hello.elf $HT/root --name vlx-k7t-il-$$ > $HT/vlx-run.log 2>&1 || true
cp $HT/root/ux0/data/shepherd/hello.txt $OUT/vlx-hello.txt
a=$(grep -c '^M50 ' $OUT/vlx-hello.txt || true); i=$(grep -c '^M51 ' $OUT/vlx-hello.txt || true)
echo "vlx: $a AOT-tier and $i interpreter-tier results -> $OUT/vlx-hello.txt ($(grep -h 'CONF end' $OUT/vlx-hello.txt | cut -c1-120); $(grep -h '^PASS\|^FAIL' $HT/vlx-run.log | head -1 | cut -c1-80))"
grep -q '^M99 CONF end' $OUT/vlx-hello.txt
