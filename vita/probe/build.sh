#!/bin/zsh
# Builds the Mac CPU reference (out/mac_ref.h) and the Vita probe VPK (out/shepherd_probe.vpk).
# Both sides compile the SAME bench.c with matching scalar flags so the per-kernel ratios mean something.
set -euo pipefail
cd "${0:a:h}"
export VITASDK="${VITASDK:-$HOME/vitasdk}" PATH="$HOME/vitasdk/bin:$PATH"
mkdir -p out sce_sys

# 1) Mac reference — regenerated only when bench.c/bench.h change, so one canonical reference sticks across builds. The owner-local
#    stylizer kernel (engine/Derived/vita-probe, fid1-fixup D1) is part of the source only with PROBE_OWNER_LOCAL=1 (fid1-fixup round 2:
#    opt-in): that probe holds derived code -> out/shepherd_probe-OWNER-ONLY.vpk, owner-only TITLE_ID SHPD00093, never shared.
OWN=../../engine/Derived/vita-probe; OWNI=(); VPKNAME=shepherd_probe.vpk; OWNER_CMAKE=OFF
if [[ ${PROBE_OWNER_LOCAL:-0} == 1 ]]; then
  [[ -f $OWN/probe_stylizer_cpu.h ]] || { echo "PROBE_OWNER_LOCAL=1: no engine/Derived/vita-probe here"; exit 1; }
  OWNI=(-I"$OWN"); VPKNAME=shepherd_probe-OWNER-ONLY.vpk; OWNER_CMAKE=ON
  echo "PROBE_OWNER_LOCAL=1: building the OWNER-ONLY probe (derived code inside, SHPD00093: never share it)"
fi
SHA=$(cat bench.c bench.h ${OWNI:+$OWN/probe_stylizer_cpu.h} | shasum -a 256 | cut -c1-64)
rm -f out/mac_ref_test.h
if ! grep -q "$SHA" out/mac_ref.h 2>/dev/null; then
  clang -O2 -fno-vectorize -fno-slp-vectorize -ffp-contract=off $OWNI -o out/bench_mac bench_mac.c bench.c -lm
  ./out/bench_mac out/mac_ref.h "$SHA" "$(sysctl -n machdep.cpu.brand_string)" "-O2 -fno-vectorize -fno-slp-vectorize -ffp-contract=off"
else
  echo "mac_ref.h is current ($SHA)"
fi

# 2) LiveArea icon (128x128 8-bit indexed PNG, generated — no downloads)
[ -f sce_sys/icon0.png ] || python3 - <<'EOF'
import struct, zlib
W = H = 128
pal = bytes([24, 20, 22,  209, 43, 39,  238, 232, 211])          # ink, red, paper
rows = b''
for y in range(H):
    row = bytearray([0])                                          # filter: none
    for x in range(W):
        dx, dy = x - 64, y - 70
        eye = (dx * dx) / 900 + (dy * dy) / 260 <= 1.0           # an eye, like the cult sigil
        pupil = dx * dx + dy * dy <= 110
        row.append(1 if pupil else (2 if eye else 0))
    rows += bytes(row)
def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 3, 0, 0, 0)) + chunk(b'PLTE', pal) \
    + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')
open('sce_sys/icon0.png', 'wb').write(png)
EOF

# 3) Vita build
cmake -S . -B out/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBENCH_SHA="$SHA" -DPROBE_OWNER_LOCAL=$OWNER_CMAKE >/dev/null
cmake --build out/build
cp out/build/$VPKNAME out/$VPKNAME
ls -la out/$VPKNAME
