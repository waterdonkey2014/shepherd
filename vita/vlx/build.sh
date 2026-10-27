#!/bin/zsh
# vita/vlx/build.sh: compiles the vlx shim (vlx.c + vlx_entry.S) with the VitaSDK gcc -> $VLX_OBJ/{vlx.o,vlx_entry.o}
# env: VLX_OBJ (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vlx; never under devfail1), VITASDK (default ~/vitasdk)
set -euo pipefail
HERE=${0:a:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
# (default moved off devfail1, 2026-10-13: that tree is a frozen record; the integ copies already refused it)
O=${VLX_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vlx}; case $O in ${SHEPHERD_WORK:-$HOME/shepherd-work}/devfail1*) echo "refusing a VLX_OBJ under devfail1: $O"; exit 1 ;; esac; mkdir -p "$O"
# ARM mode (raw syscalls use r7, the Thumb frame pointer); freestanding: the shim sits under newlib and must not call it
arm-vita-eabi-gcc -O2 -g -marm -mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -ffreestanding -fno-builtin \
  -fno-tree-loop-distribute-patterns -fomit-frame-pointer -Wall -Wno-misleading-indentation -c "$HERE/vlx.c" -o "$O/vlx.o"
arm-vita-eabi-gcc -c "$HERE/vlx_entry.S" -o "$O/vlx_entry.o"
echo "vlx shim: $O/vlx.o $O/vlx_entry.o"
