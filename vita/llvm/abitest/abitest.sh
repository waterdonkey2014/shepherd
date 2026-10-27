#!/bin/zsh
# vita/llvm/abitest/abitest.sh (kit #5 llvm review fix; vita/notes/kit5-llvm.md "Review fixes"): the managed ABI + exception
# cross-boundary test under vlx. gen_abitest.py writes a synthetic Hello.cs / HelloInterp (no game content: float-argument signatures
# through every call path, and exceptions thrown / caught through LLVM frames that keep floats in callee-saved d8-d15); it runs as
# the hello app (vita/hello), every check logged as M70/M72/M74 ok or M71/M73/M75 FAIL, "M98 ABITEST END failures=N" at the end.
#   abitest.sh base                                   stage this checkout's vita/ (git archive HEAD) under $ABIT_DIR/src, generate the
#                                                     test into its hello app, build it WITHOUT LLVM (the shared compiler + runtime):
#                                                     $ABIT_DIR/w-base (work), out-base
#   abitest.sh llvm <variant> <LLVM compiler obj dir> [VAR=VAL ...]
#                                                     clone w-base -> w-<variant>, AOT EVERY module again with that compiler (LLVM
#                                                     thumb, vita/shepherd/build.sh's LLVM options and CoreLib pools; VAR=VAL go to
#                                                     the compiler's environment, e.g. SHEPHERD_EH_VFP_FIX=0)
#   abitest.sh run <variant> <runtime obj dir|shared> link w-<variant> (or w-base: variant "base") for vlx against that runtime
#                                                     (shared = ${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime), run it, summarize:
#                                                     $ABIT_DIR/vlx-<variant>-<tag>/hello.txt; tag = RUN_TAG (default the runtime dir's name)
# env: ABIT_DIR (required: a directory of your own on the build volume), VLX_OBJ (the vlx shim; default $ABIT_DIR/vlx-obj, built if
#      missing). The Mac stays silent: the run refuses unless output is muted (the hello app makes no sound anyway).
set -euo pipefail
HERE=${0:a:h}; REPO=${HERE:h:h:h}
B=${SHEPHERD_WORK:-$HOME/shepherd-work}
A=${ABIT_DIR:?set ABIT_DIR to a directory of your own on the build volume}
case $A in $B/obj*|$B/runtime*|$B/kit4*|$B/v6*) echo "refusing a shared dir: $A"; exit 1 ;; $B/*) ;; *) echo "ABIT_DIR must be on $B"; exit 1 ;; esac
export PATH="$HOME/vitasdk/bin:$HOME/.dotnet:$PATH" DOTNET_ROOT=$HOME/.dotnet TMPDIR=$A/tmp MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
mkdir -p "$A" "$TMPDIR"
MODS=(System.Private.CoreLib System.Runtime System.Console System.Collections System.Threading System.Threading.Thread
      System.Runtime.InteropServices Microsoft.Win32.Primitives System.Memory System.Collections.Concurrent Hello)
MODE=${1:?usage: abitest.sh base | llvm <variant> <compiler obj> [VAR=VAL ...] | run <variant> <runtime obj|shared>}; shift
case $MODE in
base)
  rm -rf "$A/src"; mkdir -p "$A/src"
  git -C "$REPO" archive HEAD vita | tar -x -C "$A/src"
  python3 "$HERE/gen_abitest.py" "$A/src/vita/hello/managed" > "$A/gen.log"
  rm -rf "$A/w-base" "$A/out-base"
  SHEPHERD_HELLO_WORK=$A/w-base SHEPHERD_HELLO_OUT=$A/out-base nice -n 10 zsh "$A/src/vita/hello/build.sh" > "$A/build-base.log" 2>&1 \
    || { tail -30 "$A/build-base.log"; exit 1; }
  find "$A/w-base/aot" -maxdepth 1 -name '*.s' -delete
  echo "base: $(head -1 "$A/gen.log"); $(grep -o 'AOT GUID gate: .*' "$A/build-base.log" | head -1)"
  ;;
llvm)
  V=${1:?variant}; XO=${2:?compiler obj dir}; shift 2
  X=$XO/out/bin/mono-aot-cross; LB=$XO/out/llvm-bin/
  [ -x "$X" ] && [ -x "$LB/llc" ] || { echo "no LLVM compiler at $XO"; exit 1; }
  W=$A/w-$V; rm -rf "$W"; cp -cR "$A/w-base" "$W" 2>/dev/null || cp -R "$A/w-base" "$W"
  OPTS="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,llvm,llvm-path=$LB,mtriple=thumbv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf -arm-assume-misaligned-load-store,mattr=+d32"
  TR=",ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
  for a in $MODS; do
    EX=; [ "$a" = System.Private.CoreLib ] && EX=$TR
    rm -f "$W/aot/$a.dll.s" "$W/aot/$a.dll.o" "$W/aot/$a-llvm.o" "$W/aot/$a.log"   # (not $a.*: System.Threading.* would match System.Threading.Thread)
    env "$@" MONO_PATH="$W/bcl" MONO_AGGRESSIVE_INLINELIMIT=256 nice -n 10 "$X" --aot=$OPTS$EX,llvm-outfile="$W/aot/$a-llvm.o",outfile="$W/aot/$a.dll.s" \
      "$W/bcl/$a.dll" > "$W/aot/$a.log" 2>&1 || { tail -20 "$W/aot/$a.log"; exit 1; }
    arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o "$W/aot/$a.dll.o" "$W/aot/$a.dll.s"
    echo "  $a: $(grep -o 'Compiled: [0-9/]*' "$W/aot/$a.log" | tail -1) $(grep -o 'LLVM: [0-9]* ([0-9]*%)' "$W/aot/$a.log" | tail -1)"
  done
  # the managed throw trampolines CoreLib's image carries (fix A: fp_regs = sp + the param area; stock: sp + 8)
  python3 "$HERE/../check_eh_tramps.py" "$W/aot/System.Private.CoreLib.dll.o" | sed 's/^/  CoreLib /' || true
  find "$W/aot" -maxdepth 1 \( -name '*.s' -o -name '*.bc' \) -delete
  echo "llvm $V: $XO $*"
  ;;
run)
  V=${1:?variant}; RO=${2:?runtime obj dir or shared}; [ "$RO" = shared ] && RO=$B/obj/vita-runtime
  T=${RUN_TAG:-${RO:t}}; W=$A/w-$V; LL=1; [ "$V" = base ] && LL=0
  [ -d "$W" ] || { echo "no $W"; exit 1; }
  [[ "$(osascript -e 'output muted of (get volume settings)')" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
  export VLX_OBJ=${VLX_OBJ:-$A/vlx-obj}; mkdir -p "$VLX_OBJ"
  [ -f "$VLX_OBJ/vlx.o" ] || zsh "$A/src/vita/vlx/build.sh" > "$A/vlx-shim-build.log" 2>&1 || { tail "$A/vlx-shim-build.log"; exit 1; }
  O=$A/vlx-$V-$T; ROOT=$A/root-$V-$T; rm -rf "$O"; mkdir -p "$O"; ELF=$O/hello-vlx.elf
  SHEPHERD_HELLO_WORK=$W SHEPHERD_VITA_RUNTIME_OBJ=$RO VLX_LLVM=$LL zsh "$A/src/vita/vlx/link.sh" hello "$W/obj/host.o" "$ELF" > "$O/link.log" 2>&1 || { cat "$O/link.log"; exit 1; }
  SHEPHERD_HELLO_WORK=$W zsh "$A/src/vita/vlx/stage.sh" hello "$ROOT" > /dev/null
  t0=$(date +%s); rc=0
  zsh "$A/src/vita/vlx/run.sh" "$ELF" "$ROOT" --name "vlx-k5llvm-abit-$V-$T" > "$O/console.txt" 2>&1 || rc=$?
  cp "$ROOT/ux0/data/shepherd/hello.txt" "$O/hello.txt" 2>/dev/null || true
  rm -rf "$ROOT" "$ELF"
  ok=$(grep -cE 'M7[024] ok' "$O/hello.txt" 2>/dev/null || true); fl=$(grep -cE 'M7[135] FAIL' "$O/hello.txt" 2>/dev/null || true)
  echo "$V on ${RO:t}: vlx rc=$rc $(( $(date +%s) - t0 ))s; $ok checks ok, $fl FAIL; $(grep -ho 'ABITEST END.*' "$O/hello.txt" 2>/dev/null || echo 'no ABITEST END line'); $(tail -1 "$O/link.log" | grep -o 'runtime [^;]*')" | tee "$O/summary.txt"
  ;;
*) echo "usage: abitest.sh base | llvm <variant> <compiler obj> [VAR=VAL ...] | run <variant> <runtime obj|shared>"; exit 2 ;;
esac
