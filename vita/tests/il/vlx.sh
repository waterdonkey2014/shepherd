#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "Compiler + instruction differential tests"): the VITA tiers of the IL conformance suite --
# Shepherd's Vita runtime (the .NET 8 Mono fork, armv7) under vlx. Review fix: the step that builds and runs them was only in the work dir.
# A vlx-only copy of the hello app (vita/hello, SHPD00005's recipe) is built in the work dir, NEVER for the device (NOT-FOR-DEVICE.txt):
# hello-gen.py puts the same test sources in Hello.dll (static full AOT = the AOT tier) and HelloInterp.dll (interpreted = the interpreter
# tier, which runs the game's DLLs in 00097 / 00098 / 00099 / 00011); Hello's Main runs both tables and logs one line per test (M50 = AOT,
# M51 = interpreter) into ux0:data/shepherd/hello.txt.
#   vlx.sh [out dir]   -> <out>/vlx-hello.txt (default: il/runs, next to run.sh's Mac tiers) + the logs in $HT
#   then: vita-compare.py <out>/vlx-hello.txt <il/runs> [<fuzz runs dir>]   (the Vita tiers against the Mac tiers, test by test)
# env: HT (the work dir, default tests-work/hellotier; wiped and rebuilt), FLOATPC / FLOATPC_DLL / FLOATPC_ARGS / EXTRA_TESTS (kit #9, below), MONO_MINI (Mono's mini tests, as build.sh), MINI_FILES (default
#      "basic basic-float basic-long basic-math"), FUZZ (random float programs, fuzz.py output; default tests-work/il/fuzz2/fuzz-31-f.cs),
#      FPMODE (default this dir's FpMode.cs). The original run (2026-10-20: 436 tests per tier) used these defaults.
#      (kit #9 runtime review fix, test-only, all unset = the run above:) IL_LLVM=<LLVM compiler obj dir, e.g. build-cross-k9.sh llvm's
#      <dest>/obj/cross-llvm-armhf> = after the hello build every AOT module is compiled AGAIN by that compiler with Mono's LLVM backend
#      (Thumb-2, vita/shepherd/build.sh's LLVM options and CoreLib pools, as vita/llvm/abitest/abitest.sh) and vlx links the -llvm.o objects
#      (VLX_LLVM=1): the AOT tier is then LLVM code -- SHEPHERD_VITA_RUNTIME_OBJ must be an LLVM app's runtime (the EH-VFP fixes B + C);
#      IL_LLVM_ENV="VAR=VAL ..." = that compiler's environment (e.g. SHEPHERD_AOT_DIVFAST=1); IL_RUN_ENV="VAR=VAL ..." = written to the
#      staged card's ux0:data/shepherd/hello-env.txt, which the hello host setenv's before the runtime starts (e.g. SHEPHERD_K9_DIVFAST=0).
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
python3 $T/hello-gen.py $M $FPMODE $mini $FUZZ ${=EXTRA_TESTS:-} 2>&1 | tee $HT/gen.log
# kit #9 fidelity (vita/notes/kit9-fidelity.md): FLOATPC=1 rewrites Hello.dll / HelloInterp.dll with tools/FloatPc right after the hello's
# managed build (FLOATPC_DLL = the built FloatPc.dll; FLOATPC_ARGS e.g. --x64-f2i); EXTRA_TESTS = more test sources (e.g. F2iMode.cs)
if [[ ${FLOATPC:-0} == 1 ]]; then
  [[ -f ${FLOATPC_DLL:-} ]] || { echo "FLOATPC=1 needs FLOATPC_DLL (dotnet build -c Release tools/FloatPc)"; exit 1; }
  FLOATPC_DLL=$FLOATPC_DLL FLOATPC_ARGS=${FLOATPC_ARGS:-} python3 - $HT/vita/hello/build.sh <<'PY'
import sys, os
p = sys.argv[1]; s = open(p).read()
old = 'cp "$OUT"/managed-il/Hello.dll "$OUT"/managed-il/HelloInterp.dll "$W/bcl/"\n'
assert old in s, "hook point not found"
new = old + ('for _a in Hello HelloInterp; do DOTNET_ROOT=$HOME/.dotnet $HOME/.dotnet/dotnet ' + os.environ["FLOATPC_DLL"] + ' "$W/bcl/$_a.dll" "$W/bcl/$_a.pc.dll" --report "$W/floatpc.txt" '
             + os.environ.get("FLOATPC_ARGS", "") + ' && mv "$W/bcl/$_a.pc.dll" "$W/bcl/$_a.dll" || exit 1; done\n')
open(p, "w").write(s.replace(old, new))
print("hello build.sh patched: FloatPc after the managed build")
PY
fi
# 2. the build (the hello recipe: dotnet build, mono-aot-cross static full AOT of Hello + its closure, HelloInterp left interpreted, link, VPK)
SHEPHERD_HELLO_WORK=$HT/work SHEPHERD_HELLO_OUT=$HT/out nice zsh $HT/vita/hello/build.sh > $HT/build.log 2>&1 || { echo "hello build failed: $HT/build.log"; tail -5 $HT/build.log; exit 1; }
echo "vlx-only test build of the kit #7 IL conformance suite (vita/tests/il/hello-gen.py): NOT FOR THE DEVICE -- it reuses the hello's title id SHPD00005" > $HT/out/NOT-FOR-DEVICE.txt
rm -f $HT/work/aot/*.s   # (disk: the AOT assembly listings)
VLXLL=auto   # (link.sh's own default when unset)
if [[ -n ${IL_LLVM:-} ]]; then   # (kit #9 runtime review fix, test-only) the AOT tier compiled again with the LLVM backend (header)
  export PATH="$HOME/vitasdk/bin:$PATH"   # (arm-vita-eabi-as: the hello build sets it only for itself)
  X=$IL_LLVM/out/bin/mono-aot-cross; LB=$IL_LLVM/out/llvm-bin/
  [[ -x $X && -x $LB/llc ]] || { echo "IL_LLVM: no LLVM compiler at $IL_LLVM"; exit 1; }
  OPTS="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,llvm,llvm-path=$LB,mtriple=thumbv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf -arm-assume-misaligned-load-store,mattr=+d32"
  TR=",ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
  : > $HT/llvm-aot.log
  for d in $HT/work/aot/*.dll.o; do a=${${d:t}%.dll.o}; EX=; [[ $a == System.Private.CoreLib ]] && EX=$TR
    rm -f $HT/work/aot/$a.dll.o $HT/work/aot/$a-llvm.o
    env ${=IL_LLVM_ENV:-} MONO_PATH=$HT/work/bcl MONO_AGGRESSIVE_INLINELIMIT=256 nice "$X" --aot=$OPTS$EX,llvm-outfile=$HT/work/aot/$a-llvm.o,outfile=$HT/work/aot/$a.dll.s \
      $HT/work/bcl/$a.dll > $HT/work/aot/$a.llvm.log 2>&1 || { tail -20 $HT/work/aot/$a.llvm.log; exit 1; }
    arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o $HT/work/aot/$a.dll.o $HT/work/aot/$a.dll.s
    rm -f $HT/work/aot/$a.dll.s $HT/work/aot/$a.dll.s.bc $HT/work/aot/$a.dll.s.opt.bc
    echo "$a: $(grep -o 'Compiled: [0-9/]*' $HT/work/aot/$a.llvm.log | tail -1) $(grep -h '^SHEPHERD kit #9' $HT/work/aot/$a.llvm.log | cut -c1-160)" >> $HT/llvm-aot.log
  done
  VLXLL=1
fi
# 3. vlx: the shim, the build's own objects relinked against it, the root (app0 = the VPK's managed/), the run
VLX_OBJ=$HT/vlxobj zsh $WT/vita/vlx/build.sh > $HT/vlx-build.log 2>&1
SHEPHERD_HELLO_WORK=$HT/work VLX_OBJ=$HT/vlxobj VLX_LLVM=$VLXLL zsh $WT/vita/vlx/link.sh hello $HT/work/obj/host.o $HT/vlxobj/conf-hello.elf > $HT/vlx-link.log 2>&1
SHEPHERD_HELLO_WORK=$HT/work zsh $WT/vita/vlx/stage.sh hello $HT/root > $HT/vlx-stage.log 2>&1
[[ -z ${IL_RUN_ENV:-} ]] || { mkdir -p $HT/root/ux0/data/shepherd; for kv in ${=IL_RUN_ENV}; do echo $kv; done > $HT/root/ux0/data/shepherd/hello-env.txt; }   # (kit #9 review fix, test-only)
zsh $WT/vita/vlx/run.sh $HT/vlxobj/conf-hello.elf $HT/root --name vlx-k7t-il-$$ > $HT/vlx-run.log 2>&1 || true
cp $HT/root/ux0/data/shepherd/hello.txt $OUT/vlx-hello.txt
a=$(grep -c '^M50 ' $OUT/vlx-hello.txt || true); i=$(grep -c '^M51 ' $OUT/vlx-hello.txt || true)
echo "vlx: $a AOT-tier and $i interpreter-tier results -> $OUT/vlx-hello.txt ($(grep -h 'CONF end' $OUT/vlx-hello.txt | cut -c1-120); $(grep -h '^PASS\|^FAIL' $HT/vlx-run.log | head -1 | cut -c1-80))"
grep -q '^M99 CONF end' $OUT/vlx-hello.txt
