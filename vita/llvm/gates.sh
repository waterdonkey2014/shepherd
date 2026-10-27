#!/bin/zsh
# SHEPHERD-VITA LLVM: the Vita build's gates, applied to an aot-vita.sh + link-vita.sh output.
# usage: gates.sh <set> <variant>   -> $L/out/<set>-<variant>/gates.txt (exit 1 if any gate fails)
#   1. GUID gate: nollvm = vita/hello/check_aot_guids.py on the .s files; LLVM = check_aot_guids_llvm.py on the -llvm.o files
#      (the tables live in the LLVM object). Owner dirs for the engine set (Shepherd.Engine's Unity/game references).
#   2. module size gate: text + rodata of each module (Mono part + LLVM part) < 28 MiB, as vita/shepherd/build.sh measures it.
#   3. foreign-code gate (engine set; foreign-gate.sh, after its --self-test): every code symbol of Shepherd.Engine's image is
#      engine, BCL, a runtime wrapper / PLT / unbox trampoline or the image's own data. Mono part: the exact regex of
#      vita/shepherd/build.sh (+ the PLT entries it emits for LLVM callers, p_<n>_plt_<callee>_llvm: call stubs named after their
#      callee, not bodies). LLVM part: every method is named "Shepherd_Engine_<name>" in a static build, so the prefix is stripped
#      before the (managed-names-only) allow-list, the gsharedvt sig wrappers aot_wrapper_gsharedvt_{in,out}_sig_* are allowed, and
#      an unprefixed code symbol must be a module helper (gc.safepoint_poll, mono_aot_Shepherd_Engine_*). No Unity / game method
#      body may be in the image.
#   4. LLVM EH tables vs the linked ELF (check_llvm_eh.py): table order = address order, Thumb bits, CFA ops Mono can decode.
set -uo pipefail
export PATH="$HOME/vitasdk/bin:$PATH"
HERE=${0:A:h}; V=${HERE:h}
L=${SHEPHERD_LLVM_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm}
SET=$1; VAR=$2; A=$L/aot/$SET-$VAR; O=$L/out/$SET-$VAR; mkdir -p $O
case $SET in hello) IN=$L/inputs/hello; OWN=(); ELF=$O/shepherd_hello.elf ;; engine) IN=$L/inputs/bcl; OWN=($L/inputs/owner-managed); ELF=$O/shepherd_menu_aot.elf ;; esac
fail=0
{
  echo "# gates $SET-$VAR"
  if [ $VAR = nollvm ]; then python3 $V/hello/check_aot_guids.py $IN $A $OWN > $O/aot-guids.txt; else python3 $HERE/check_aot_guids_llvm.py $IN $A $OWN > $O/aot-guids.txt; fi
  g=$(tail -1 $O/aot-guids.txt); echo "1. GUID gate: $g"; [ "$g" = "TOTAL MISMATCHES 0" ] || fail=1
  python3 $HERE/sizes.py --tsv $O/aot-sizes.tsv $A > $O/aot-sizes.md
  over=$(awk -F'\t' 'NR>1 && $5+$6 >= 28*1048576 {print $2" "($5+$6)}' $O/aot-sizes.tsv)
  big=$(awk -F'\t' 'NR>1 {t=$5+$6; if (t>m) {m=t; n=$2}} END {printf "%s %.2f MiB", n, m/1048576}' $O/aot-sizes.tsv)
  echo "2. module size gate (< 28 MiB text+rodata): ${over:-all pass} (largest: $big)"; [ -z "$over" ] || fail=1
  if [ $SET = engine ]; then
    $HERE/foreign-gate.sh --self-test > $O/foreign-gate-selftest.txt || { cat $O/foreign-gate-selftest.txt; fail=1; }
    LO=-; [ -f $A/Shepherd.Engine-llvm.o ] && LO=$A/Shepherd.Engine-llvm.o
    [ $VAR = nollvm ] || [ $LO != - ] || { echo "   no $A/Shepherd.Engine-llvm.o"; fail=1; }
    fg=$($HERE/foreign-gate.sh Shepherd.Engine $A/Shepherd.Engine.dll.o $LO $O/engine-aot); r=$?
    echo "3. foreign-code gate: ${fg:-could not run (exit $r)}; $(grep -o '[0-9]* inline(s) of methods outside SHEPHERD_AOT_INLINE_IMAGES refused ([0-9]* distinct methods)' $A/Shepherd.Engine.log)"
    echo "   gate self-test: $(grep -q FAIL $O/foreign-gate-selftest.txt && echo FAIL || echo pass) ($(sed -n 1p $O/foreign-gate-selftest.txt | sed 's/^self-test: //'); $(sed -n 2p $O/foreign-gate-selftest.txt | sed 's/^self-test: //'))"
    [ $r = 0 ] || { head -20 $O/engine-aot-foreign.txt 2>/dev/null; fail=1; }
  fi
  if [ $VAR != nollvm ]; then
    python3 $HERE/check_llvm_eh.py $A $ELF > $O/llvm-eh.txt; r=$?
    echo "4. LLVM EH tables vs the ELF: $(grep -c ': OK' $O/llvm-eh.txt) modules OK; $(grep '^TOTAL' $O/llvm-eh.txt)"; echo "   $(grep '^CFA ops' $O/llvm-eh.txt)"
    [ $r = 0 ] || { grep FAIL $O/llvm-eh.txt | cut -c1-300; fail=1; }
  fi
  echo "RESULT: $([ $fail = 0 ] && echo PASS || echo FAIL)"
} | tee $O/gates.txt
grep -q "^RESULT: PASS" $O/gates.txt
