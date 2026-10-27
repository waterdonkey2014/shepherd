#!/bin/zsh
# Build the Mac harness of the Shepherd JIT probe (vita/jit/probe/README.md): the same probe_core.c + emit.c on the Mac backend
# (MAP_JIT), so the harness, threads, handshakes, timing and the crash-safe log are exercised natively before any device run.
# Xcode clang, no downloads. Output + results: $JITP_OUT (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/probe).
D="${0:A:h}"; OUT=${JITP_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/probe}; mkdir -p "$OUT"
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/tmp} JITP_OUT="$OUT"
# the JIT entitlement lets a signed binary map MAP_JIT pages; ad-hoc signing with it is enough on Apple silicon for a local dev build
cat > "$OUT/jit.entitlements" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>
EOF
clang -O2 -Wall -Wextra -arch arm64 -o "$OUT/jitprobe" "$D/main_mac.c" "$D/probe_core.c" "$D/emit.c" "$D/native.c" "$D/plat_mac.c" || exit 1
codesign -s - --entitlements "$OUT/jit.entitlements" -f "$OUT/jitprobe" 2>/dev/null || echo "note: codesign failed; MAP_JIT may be refused"
echo "$OUT/jitprobe"
