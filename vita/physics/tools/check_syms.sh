#!/bin/zsh
# Vita-side checks for libshepphys_vita.a, run by build.sh vita:
#  1. every symbol the archive needs from outside itself is defined by newlib (libc/libm), libstdc++/libsupc++ or libgcc
#  2. the linked T1 test ELF has no strong undefined symbols (vita-elf-create already accepted it: eboot.bin exists)
#  3. the code has no fused multiply-add (VFPv4 vfma/vfms/vfnma/vfnms) and no NEON single-precision arithmetic (NEON
#     flushes denormals regardless of FPSCR on ARMv7); scalar VFP vmla/vmls (non-fused, bit-identical to vmul+vadd) counted
# usage: check_syms.sh <libshepphys_vita.a> <t1.elf> [report]
set -euo pipefail
LIB="$1"; ELF="$2"; REPORT="${3:-}"
VITASDK="${VITASDK:-$HOME/vitasdk}"
VB="$VITASDK/bin"
L="$VITASDK/arm-vita-eabi/lib"
LIBGCC="$("$VB/arm-vita-eabi-gcc" -print-libgcc-file-name)"
fail=0
lines=()
say() { lines+=("$*"); print -r -- "$*"; }

defs_in="$("$VB/arm-vita-eabi-nm" -g --defined-only "$LIB" 2>/dev/null | awk 'NF>=3 {print $3}' | sort -u)"
need=(${(f)"$("$VB/arm-vita-eabi-nm" -u "$LIB" 2>/dev/null | awk '$1=="U" {print $2}' | sort -u)"})
ext=()
# (membership through associative arrays: `print | grep -q` under pipefail failed at random once the lists grew -- grep's early exit gives the
# printer SIGPIPE -- and reported defined symbols as missing; physics Stage 2)
typeset -A defined
for s in ${(f)defs_in}; do defined[$s]=1; done
for s in $need; do [[ -n "${defined[$s]:-}" ]] || ext+=("$s"); done
typeset -A where
for lib in "$L/libc.a" "$L/libm.a" "$L/libsupc++.a" "$L/libstdc++.a" "$LIBGCC"; do
  typeset -A d=()
  for x in ${(f)"$("$VB/arm-vita-eabi-nm" -g --defined-only "$lib" 2>/dev/null | awk 'NF>=3 {print $3}' | sort -u)"}; do d[$x]=1; done
  for s in $ext; do [[ -z "${where[$s]:-}" && -n "${d[$s]:-}" ]] && where[$s]="${lib:t}"; done
done
unres=()
say "1. libshepphys_vita.a needs ${#ext} external symbols:"
for s in $ext; do
  if [[ -n "${where[$s]:-}" ]]; then say "     $s  <- ${where[$s]}"; else unres+=("$s"); fi
done
say "   unresolved by newlib/libstdc++/libsupc++/libgcc: ${unres[*]:-none}"
(( ${#unres} == 0 )) || fail=1

eund="$("$VB/arm-vita-eabi-nm" -u "$ELF" | awk '$1=="U" {print $2}' | tr '\n' ' ')"
weak="$("$VB/arm-vita-eabi-nm" -u "$ELF" | awk '$1=="w"' | wc -l | tr -d ' ')"
say "2. T1 ELF strong undefined symbols: ${eund:-none} (weak undefined: $weak); eboot.bin $( [[ -f ${ELF:h}/eboot.bin ]] && print present || print MISSING)"
[[ -z "$eund" && -f "${ELF:h}/eboot.bin" ]] || fail=1

dis="$("$VB/arm-vita-eabi-objdump" -d "$LIB" 2>/dev/null)"
fma=$(print -r -- "$dis" | grep -cE $'\t''vf(n)?m[as]\.' || true)
# NEON f32 data-processing (vmov.f32 dN, #imm only materialises constants, so it is not counted)
neon=$(print -r -- "$dis" | grep -E $'\t''v[a-z]+\.f32'$'\t''[dq][0-9]+|'$'\t''vcvt\.[fsu]32\.[fsu]32'$'\t''[dq][0-9]+' | grep -cvE $'\t''vmov\.f32'$'\t' || true)
neonmov=$(print -r -- "$dis" | grep -cE $'\t''vmov\.f32'$'\t''[dq][0-9]+, #' || true)
vmla=$(print -r -- "$dis" | grep -cE $'\t''vn?ml[as]\.f(32|64)'$'\t' || true)
vfp=$(print -r -- "$dis" | grep -cE $'\t''v(add|sub|mul|div|sqrt)\.f(32|64)'$'\t' || true)
say "3. libshepphys_vita.a code: fused FMA $fma, NEON f32 arithmetic $neon (constant vmov.f32 d/q #imm: $neonmov), VFP vmla/vmls $vmla, VFP add/sub/mul/div/sqrt $vfp"
(( fma == 0 && neon == 0 )) || fail=1
say "check_syms: $([[ $fail -eq 0 ]] && print OK || print FAILED)"
[[ -n "$REPORT" ]] && print -l -- $lines > "$REPORT"
exit $fail
