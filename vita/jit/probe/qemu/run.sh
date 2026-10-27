#!/bin/zsh
# Shepherd JIT probe, qemu encoding check (qemu_test.c): builds emit.c + native.c bare-metal for the Cortex-A9 with the vitasdk compiler
# and runs them on qemu-system-arm (vexpress-a9, already installed; nothing downloaded), audio off (-audio none) and the Mac muted.
# Proves the A32 / Thumb-2 encodings by execution; NOT a device run (no ForVM, no cache maintenance: qemu keeps code coherent itself).
# Output: $JITP_OUT/qemu (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/probe/qemu). Exit 0 = every check passed (the PASS summary
# line is required: a hang is killed after 30 s and fails).
set -euo pipefail
D="${0:A:h}"; OUT=${JITP_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/probe}/qemu; mkdir -p "$OUT"
CC=$HOME/vitasdk/bin/arm-vita-eabi-gcc
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "output NOT muted - refusing"; exit 9; }
$CC -O2 -Wall -Wextra -mcpu=cortex-a9 -ffreestanding -fno-builtin -nostdlib -nostartfiles -fno-exceptions -fno-unwind-tables \
  -fno-asynchronous-unwind-tables -mno-unaligned-access -T "$D/link.ld" -o "$OUT/qemu_test.elf" \
  "$D/start.S" "$D/qemu_test.c" "$D/../emit.c" "$D/../native.c"
qemu-system-arm -M vexpress-a9 -cpu cortex-a9 -m 128M -nographic -monitor none -serial null -audio none \
  -semihosting-config enable=on,target=native -kernel "$OUT/qemu_test.elf" > "$OUT/qemu_test.txt" 2>&1 &
pid=$!
( sleep 30; kill $pid 2>/dev/null ) &   # a wrong branch hangs the emulated core: killed, and no PASS line
killer=$!
rc=0; wait $pid || rc=$?
kill $killer 2>/dev/null || true
cat "$OUT/qemu_test.txt"
grep -q 'failures 0 -> PASS' "$OUT/qemu_test.txt" || { echo "qemu exit $rc: no PASS summary (a check failed, faulted or hung) -> FAIL"; exit 1; }
echo "qemu exit $rc"
exit $rc
