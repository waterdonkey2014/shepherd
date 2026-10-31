#!/bin/zsh
# SHEPHERD-VITA device kit #10, track memory -- review fix R1 (vita/notes/kit10-memory.md section 14; vita/shepherd/k10_vglmem.c): in each GL build's linked ELF, every call to vitaGL's vgl_free / vgl_realloc / vgl_malloc_usable_size must
# come from the host's __wrap_ functions (k10_vglmem.c); vitaGL's own call sites must reach the __wrap_ functions (directly or through a
# linker veneer). usage: k10_vgl_wrap_check.sh <build dir holding work/obj/shepherd_menu.elf>... ; exit 1 = a direct call outside the wrappers
export PATH=${VITASDK:-$HOME/vitasdk}/bin:$PATH
rc=0
for b in "$@"; do
  E=$b/work/obj/shepherd_menu.elf; [[ -f $E ]] || { echo "${b:t}: no ELF"; continue; }
  D=$(mktemp -d "${TMPDIR:-/tmp}/k10-wrapcheck.XXXXXX")
  arm-vita-eabi-objdump -d --no-show-raw-insn $E > $D/dis 2>/dev/null
  # caller function -> normalized target (veneer names resolved: ____wrap_X_veneer -> __wrap_X, __X_veneer -> X); veneers themselves skipped
  awk '/^[0-9a-f]+ <[^>]+>:$/ {fn=$2; next}
       fn ~ /_veneer>:$/ {next}
       /\t(bl|blx|b|b\.w|b\.n)[ \t]+[0-9a-f]+ <(__)?(__wrap_)?vgl_(free|realloc|malloc_usable_size)(_veneer)?>/ {
         match($0, /<(__)?(__wrap_)?vgl_(free|realloc|malloc_usable_size)(_veneer)?>/); t=substr($0, RSTART+1, RLENGTH-2)
         if (t ~ /_veneer$/) { sub(/_veneer$/, "", t); sub(/^__/, "", t) }
         print fn, t }' $D/dis | sort | uniq -c > $D/calls
  direct=$(awk '$2 !~ /^<__wrap_vgl_/ && $3 !~ /^__wrap_/ {s+=$1} END {print s+0}' $D/calls)
  fromwrap=$(awk '$2 ~ /^<__wrap_vgl_/ && $3 !~ /^__wrap_/ {s+=$1} END {print s+0}' $D/calls)
  wrapped=$(awk '$3 ~ /^__wrap_/ {s+=$1; n++} END {print s+0 " (" n+0 " caller functions)"}' $D/calls)
  syms=$(arm-vita-eabi-nm $E | grep -cE " T (shepherd_k10_newlib_owns|shepherd_k10_vgl_after_init|__wrap_vgl_free|__wrap_vgl_realloc|__wrap_vgl_malloc_usable_size)$")
  echo "${b:t}: direct calls to vitaGL's vgl_free/realloc/usable_size outside the wrappers: $direct (must be 0); from the wrappers: $fromwrap (3 expected); vitaGL call sites -> __wrap_: $wrapped; host symbols $syms/5"
  [[ $direct == 0 ]] || { grep -v "__wrap" $D/calls | head -5; rc=1; }
  rm -rf $D
done
exit $rc
