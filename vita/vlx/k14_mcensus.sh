#!/bin/zsh
# vita/vlx/k14_mcensus.sh (kit #14 newlib, TEST ONLY): compiles the vlx-only newlib census (k14_mcensus_vlx.c) into $VLX_OBJ and prints the
# link flags that put it into a vlx ELF: the object for VLX_EXTRA_OBJS and one --wrap per wrapper in the source (VLX_LDFLAGS).
#   k14_mcensus.sh obj    -> the object's path        k14_mcensus.sh ldflags -> the --wrap flags
# Never part of an eboot: vita/shepherd/build.sh does not know this file.
set -euo pipefail
HERE=${0:a:h}; O=${VLX_OBJ:?VLX_OBJ}
export PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH"
SRC=$HERE/k14_mcensus_vlx.c; OBJ=$O/k14_mcensus_vlx.o
case ${1:?obj|ldflags} in
  obj)
    if ! [[ -f $OBJ && $OBJ -nt $SRC ]]; then
      arm-vita-eabi-gcc -O2 -g -marm -mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -ffreestanding -fno-builtin -fno-tree-loop-distribute-patterns \
        -fomit-frame-pointer -ffunction-sections -Wall -c $SRC -o $OBJ.$$ && mv $OBJ.$$ $OBJ
    fi
    echo $OBJ ;;
  ldflags)
    # the wrappers: written out (__wrap_<name> (...)) or made by the macros (OWNER_WRAP<n> (R, <name>, ...), OWNER_WRAPV<n> (<name>, ...), MMW (R, <name>, ...))
    { grep -oE '__wrap_[A-Za-z0-9_]+ \(' $SRC | sed 's/^__wrap_//; s/ (//'
      grep -E '^OWNER_WRAP[0-9] \(|^MMW \(' $SRC | sed -E 's/^[A-Z0-9_]+ \([^,]+, *([A-Za-z0-9_]+).*/\1/'
      grep -E '^OWNER_WRAPV[0-9] \(' $SRC | sed -E 's/^[A-Z0-9_]+ \(([A-Za-z0-9_]+).*/\1/'; } | sort -u | sed 's/^/-Wl,--wrap=/' | tr '\n' ' ' ;;
esac
