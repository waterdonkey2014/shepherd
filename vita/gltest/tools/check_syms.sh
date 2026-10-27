#!/bin/zsh
# Symbol checks for the GL glue (arm-vita-eabi-nm), run by build.sh after the Vita build:
#  1. every one of Gl.cs's 51 names is a defined function (T) in libvitaGL.a; size <= 16 bytes is listed as a possible stub
#  2. every symbol libshepgl.a needs is defined by vitaGL / vitaShaRK / newlib / the SceLibKernel and SceKernelThreadMgr stubs
#     (the thread manager: shepgl ends vitaGL's splash after a failed init with the same semaphore calls vitaGL's scene_reset makes)
#  3. the linked gltest ELF has no undefined symbols and keeps sceUserMainThreadStackSize + _newlib_heap_size_user
# usage: check_syms.sh <libshepgl.a> <gltest.elf> [report]
set -euo pipefail
LIBSHEPGL="$1"; ELF="$2"; REPORT="${3:-}"
HERE="${0:a:h}"
L="$VITASDK/arm-vita-eabi/lib"
NM=arm-vita-eabi-nm
fail=0
out=()
echo() { out+=("$*"); }   # collect lines; printed (and saved) at the end, in this shell (no subshell: fail must survive)
  echo "check_syms: libvitaGL.a sha256 $(shasum -a 256 "$L/libvitaGL.a" | cut -c1-16), libvitashark.a $(shasum -a 256 "$L/libvitashark.a" | cut -c1-16)"
  names=(${(f)"$(sed -nE 's/^SHEPGL_E\( *[0-9]+, *[A-Za-z0-9]+, *(gl[A-Za-z0-9]+),.*/\1/p' "$HERE/../shepgl/shepgl_entries.h")"})
  [[ ${#names} -eq 51 ]] || { echo "ERROR shepgl_entries.h lists ${#names} names, expected 51"; fail=1; }
  defs="$($NM -S -g --defined-only "$L/libvitaGL.a" 2>/dev/null | awk 'NF==4 && $3=="T" {print $4, $2}')"
  missing=0; small=()
  for n in $names; do
    line="$(print -r -- "$defs" | awk -v n="$n" '$1==n {print; exit}')"
    if [[ -z "$line" ]]; then echo "ERROR $n is not a defined function in libvitaGL.a"; missing=$((missing+1)); fail=1; continue; fi
    sz=$((16#${line#* }))
    (( sz <= 16 )) && small+=("$n=${sz}B")
  done
  echo "1. libvitaGL.a defines $((51-missing))/51 of Gl.cs's entry points; <= 16 bytes (check for stubs): ${small[*]:-none}"
  # 2. libshepgl's own undefined symbols
  und=(${(f)"$($NM -u "$LIBSHEPGL" | awk '$1=="U" {print $2}' | sort -u)"})
  have="$( { $NM -g --defined-only "$L/libvitaGL.a" "$L/libvitashark.a" "$L/libc.a" "$L/libSceLibKernel_stub.a" "$L/libSceKernelThreadMgr_stub.a" 2>/dev/null; } | awk 'NF>=3 {print $3}' | sort -u)"
  unres=()
  for s in $und; do print -r -- "$have" | grep -qx -- "$s" || unres+=("$s"); done
  echo "2. libshepgl.a needs ${#und} symbols; unresolved by vitaGL/vitaShaRK/newlib/SceLibKernel/SceKernelThreadMgr: ${unres[*]:-none}"
  (( ${#unres} == 0 )) || fail=1
  echo "   libshepgl.a exports: $($NM -g --defined-only "$LIBSHEPGL" | awk '$2=="T"{print $3}' | sort | tr '\n' ' ')"
  # 3. the linked test ELF
  eund="$($NM -u "$ELF" | awk '$1=="U" {print $2}' | tr '\n' ' ')"; weak="$($NM -u "$ELF" | awk '$1=="w"' | wc -l | tr -d ' ')"
  echo "3. gltest ELF strong undefined symbols: ${eund:-none} (weak undefined, from libgcc/libstdc++, resolve to 0: $weak)"
  [[ -z "$eund" ]] || fail=1
  for s in sceUserMainThreadStackSize _newlib_heap_size_user; do
    $NM "$ELF" | awk -v s="$s" '$3==s {found=1} END {exit !found}' && echo "   $s present" || { echo "ERROR $s missing from the ELF"; fail=1; }
  done
  echo "check_syms: $([[ $fail -eq 0 ]] && print OK || print FAILED)"
unfunction echo
print -l -- $out
[[ -n "$REPORT" ]] && print -l -- $out > "$REPORT"
exit $fail
