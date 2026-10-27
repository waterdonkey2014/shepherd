#!/bin/zsh
# vita/vlx/run.sh: runs a vlx ELF under OrbStack's ARM32 user-mode emulation (qemu-user behind binfmt; the local
# alpine:3.20 image, never pulled), silent and offline (--network none unless a gdb port is asked for).
#   run.sh <elf> <root> [docker args...]       e.g. run.sh $VLX_OBJ/hello-vlx.elf ${SHEPHERD_WORK:-$HOME/shepherd-work}/x/root-hello
# env passed through: VLX_TRACE VLX_BREAK VLX_TRAP_AFTER_S VLX_POLL_MS VLX_USER_KIB VLX_CDRAM_KIB VLX_PHYCONT_KIB VLX_CORE3 VLX_ARM500 VLX_TOUCH VLX_PROF QEMU_STRACE
#   (kit #7 probes: VLX_STORES VLX_LAUNCH_ARM VLX_PLUGIN_ARM -- vlx.c; kit #7 integration review, test only: VLX_IO_DELAY=<path part>:<ms>)
#   (kit #8 boot: VLX_MODEL=pch2000|pch1000|pch1100|pstv, VLX_VSH_FAIL=1, VLX_FW_REPORTED=<x.yy> -- the model / firmware checks;
#    VLX_AVC_FAIL=<n> the n-th video decode refuses, VLX_MODULE_MS=<ms> the engine module's load time -- the launcher build;
#    VLX_MODULE_IMPORTS=0 the module load does not model the import binding (kit #8.1; default: refuse a late library with 0x8002D003);
#    VLX_CARD=1 the memory card's read cost, timing comparisons only; review: VLX_STRICT_PATHS=1 refuses rooted "/ux0:..." sceIo paths as the Vita)
#   VLX_STACK_REPORT VLX_STACK_ACCOUNT (kit5 mem: thread stack high-water lines; stacks taken from the user budget as on the device)
#   VLX_GDB=<port>: qemu gdbstub on 127.0.0.1:<port> (lldb: target create --arch armv7-unknown-linux-gnueabihf <elf>;
#   gdb-remote 127.0.0.1:<port>); the only case that publishes a port, bound to localhost.
set -euo pipefail
ELF=${1:a}; ROOT=${2:a}; shift 2
ARGS=(--rm --pull=never -e VLX_ROOT=/vlxroot -v "$ROOT:/vlxroot" -v "${ELF:h}:/vlxbin:ro")
if [[ -n ${VLX_GDB:-} ]]; then ARGS+=(-p 127.0.0.1:$VLX_GDB:$VLX_GDB -e QEMU_GDB=$VLX_GDB); else ARGS+=(--network none); fi
# user-memory budget = what the device leaves after loading THIS eboot (device kit #2 review: one fixed figure for every menu ELF
# overstated the budget of the bigger GL and engine-AOT eboots). The Vita's free user memory shrinks 1:1 with the eboot's LOAD span
# and comes in whole MiB: free + newlib heap = floor(T - span), T = 359.104 MiB, calibrated on the PCH-2000 / 3.65 runs: 2026-10-09
# (menu-diag 20261008-060234: span 38.945 MiB, 192 MiB free with the 128 MiB newlib heap; hello-diag 20261008-060232: span 32.677,
# 278 free with 48 -> T in [358.95, 359.68)) and device kit #3, 2026-10-13 (k4-gameaot review: menu-aot.txt 00011, span 51,488,604 B
# = 49.1034 MiB, 194 free with 116 -> T >= 359.1034; play / play-mt / measure, spans 56,664,708 / 56,598,508 B, 189 free with 116)
# -> T in [359.1034, 359.68). T was 359.0 before: every eboot whose span's fraction lies in (.0, .1034] got 1 MiB less than the
# device gives it (kit #3's play apps: 188 free in vlx, 189 on the device). The span comes from link.sh (<elf>.eboot-span).
# VLX_USER_KIB set = used as given.
SPANF=${ELF%.elf}.eboot-span
if [[ -z ${VLX_USER_KIB:-} && -f $SPANF && ${VLX_STACK_ACCOUNT:-0} == 1 ]]; then
  # kit5 mem: with thread stacks taken from the budget (VLX_STACK_ACCOUNT=1) the budget keeps the device's fraction of a MiB, and the shim
  # reports free user memory floored to whole MiB as the device does (every device MEM value is a multiple of 1024 KiB: a 64 KiB or 4 KiB
  # thread stack does not move it); without the fraction a 4 KiB probe thread cost a whole MiB of arena under vlx (the MT apps: 187 for 188)
  export VLX_USER_KIB=$(( (376548836 - $(cut -d' ' -f1 $SPANF)) / 1024 ))
  echo "[run.sh] VLX_USER_KIB=$VLX_USER_KIB (exact, VLX_STACK_ACCOUNT=1: free user reported floored to MiB): eboot span $(cut -d' ' -f1 $SPANF) bytes ($SPANF) -> $(( VLX_USER_KIB / 1024 )) MiB free + newlib" >&2
elif [[ -z ${VLX_USER_KIB:-} && -f $SPANF ]]; then
  export VLX_USER_KIB=$(( (376548836 - $(cut -d' ' -f1 $SPANF)) / 1048576 * 1024 ))   # 376,548,836 B = 359.104 MiB
  echo "[run.sh] VLX_USER_KIB=$VLX_USER_KIB: eboot span $(cut -d' ' -f1 $SPANF) bytes ($SPANF) -> $(( VLX_USER_KIB / 1024 )) MiB free + newlib" >&2
elif [[ -z ${VLX_USER_KIB:-} && ${ELF:t} == menu* ]]; then
  export VLX_USER_KIB=327680   # no span file (an ELF linked before 2026-10-13): the 2026-10-09 device figure for a ~38.9 MiB eboot
  echo "[run.sh] WARNING: no $SPANF: VLX_USER_KIB=327680 (right only for a ~38.9 MiB eboot; relink with link.sh)" >&2
fi
for v in VLX_TRACE VLX_BREAK VLX_TRAP_AFTER_S VLX_POLL_MS VLX_USER_KIB VLX_CDRAM_KIB VLX_PHYCONT_KIB VLX_CORE3 VLX_ARM500 VLX_STACK_REPORT VLX_STACK_ACCOUNT VLX_TOUCH VLX_TOUCH_BACK VLX_MEASURED_ARM VLX_PROF QEMU_STRACE VLX_STORES VLX_LAUNCH_ARM VLX_PLUGIN_ARM VLX_IO_DELAY VLX_FORVM_QUOTA_KIB VLX_FORVM_MAX_KIB VLX_FORVM_NOPROTECT VLX_MODEL VLX_VSH_FAIL VLX_FW_REPORTED VLX_AVC_FAIL VLX_MODULE_MS VLX_MODULE_IMPORTS VLX_CARD VLX_STRICT_PATHS; do
  [[ -n ${(P)v:-} ]] && ARGS+=(-e $v=${(P)v}); done
exec docker run $ARGS "$@" alpine:3.20 /vlxbin/${ELF:t}
