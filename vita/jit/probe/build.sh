#!/bin/zsh
# Build the Shepherd JIT probe VPK (vita/jit/probe/README.md). BUILT but NOT RUN on a device (its first exec-memory step waits for the
# owner's confirmation). Output: $JITP_OUT/shepherd_jitprobe.vpk (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/probe). Needs ~/vitasdk, cmake, ninja.
set -euo pipefail
cd "${0:A:h}"
export VITASDK="${VITASDK:-$HOME/vitasdk}" PATH="$HOME/vitasdk/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/tmp}
OUT=${JITP_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/probe}; mkdir -p "$OUT" "$TMPDIR"
cmake -S . -B "$OUT/build" -G Ninja -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$OUT/build"
cp "$OUT/build/shepherd_jitprobe.vpk" "$OUT/shepherd_jitprobe.vpk"
ls -la "$OUT/shepherd_jitprobe.vpk"
