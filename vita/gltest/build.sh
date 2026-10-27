#!/bin/zsh
# Builds libshepgl.a + the "Shepherd GL Test" VPK (SHPD00007) and runs every host-side check:
#   gen_shaders.py   GlBackend's 5 programs exactly as the essl100 dialect hands them to the driver (+ glslang ES 1.00)
#   check_abi.py     Gl.cs's 51 signatures vs vitaGL.h by ABI class, and shepgl_entries.h vs Gl.Load's order
#   cmake + ninja    vitasdk toolchain (vita.cmake), build dir on ${SHEPHERD_WORK:-$HOME/shepherd-work}
#   check_syms.sh    arm-vita-eabi-nm: the 51 in libvitaGL.a, libshepgl.a's needs, the ELF has no undefined symbols
#   mac self-check   the same test program on the Mac's desktop GL (headless, silent): checks the test itself
# usage: ./build.sh [--no-mac]        outputs: out/shepherd_gltest.vpk, out/libshepgl.a, out/*.txt|md
# Read only: engine/** and vita/v4/** (never written). Writes: vita/gltest/out (gitignored) and ${SHEPHERD_WORK:-$HOME/shepherd-work}/gltest
# (GLTEST_WORK=<dir> elsewhere). By default the tracked programs only (the Stylizer checks skipped), whatever engine/Derived holds.
# GLTEST_OWNER_LOCAL=1 (fid1-fixup round 2: opt-in) builds the owner-local stylizer (engine/Derived: stylizer.frag + vita-gltest/
# stylizer_ref.c) in and checks the Stylizer programs: that VPK holds derived code, so it is out/shepherd_gltest-OWNER-ONLY.vpk with the
# owner-only TITLE_ID SHPD00092 -- install it on the owner's Vita only, never share it.
set -euo pipefail
HERE="${0:a:h}"
cd "$HERE"
REPO="${HERE:h:h}"
export VITASDK="${VITASDK:-$HOME/vitasdk}" PATH="$HOME/vitasdk/bin:$PATH"
[[ -d ${SHEPHERD_WORK:-$HOME/shepherd-work} ]] || { echo "attach the build volume first: hdiutil attach \"$HOME/shepherd-build.sparsebundle\""; exit 1; }
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
mkdir -p "$TMPDIR" out/gen sce_sys
W=${GLTEST_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gltest}
BUILD="$W/build-vita"
mkdir -p "$W" "$BUILD"
STAMP="$(date -u +%Y%m%d-%H%M%S)-$(cd "$REPO" && git rev-parse --short HEAD)"
OWNER=${GLTEST_OWNER_LOCAL:-0}; OWNER_ARG=(); VPKNAME=shepherd_gltest.vpk; OWNER_CMAKE=OFF
[[ $OWNER == 1 ]] && { OWNER_ARG=(--owner); VPKNAME=shepherd_gltest-OWNER-ONLY.vpk; OWNER_CMAKE=ON; echo "GLTEST_OWNER_LOCAL=1: building the OWNER-ONLY gltest (derived code inside, SHPD00092: never share it)"; }

# 1) shaders + ABI table
python3 tools/gen_shaders.py "$REPO" out/gen/shaders_gen.h $OWNER_ARG
python3 tools/check_abi.py "$REPO" "$VITASDK/arm-vita-eabi/include/vitaGL.h" out/abi-table.md

# 2) LiveArea icon (128x128 8-bit indexed PNG, generated -- no downloads, no game art)
[[ -f sce_sys/icon0.png ]] || python3 - <<'EOF'
import struct, zlib
W = H = 128
pal = bytes([24, 20, 22,  209, 43, 39,  238, 232, 211,  60, 170, 90])   # ink, red, paper, green
rows = b''
for y in range(H):
    row = bytearray([0])
    for x in range(W):
        # a triangle (the first thing any GL test draws) with the cult's eye inside
        inside = y >= 24 and y <= 104 and abs(x - 64) <= (y - 24) * 0.62
        dx, dy = x - 64, y - 78
        eye = (dx * dx) / 380 + (dy * dy) / 110 <= 1.0
        pupil = dx * dx + dy * dy <= 40
        row.append(1 if pupil else (2 if eye else (3 if inside else 0)))
    rows += bytes(row)
def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 3, 0, 0, 0)) + chunk(b'PLTE', pal) \
    + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')
open('sce_sys/icon0.png', 'wb').write(png)
EOF

# 3) Vita build
cmake -S "$HERE" -B "$BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release -DGLTEST_BUILD="$STAMP" -DGEN_DIR="$BUILD/gen" -DGLTEST_OWNER_LOCAL=$OWNER_CMAKE >/dev/null
cmake --build "$BUILD"
cp "$BUILD/$VPKNAME" out/$VPKNAME
cp "$BUILD/libshepgl.a" out/libshepgl.a
zsh tools/check_syms.sh "$BUILD/libshepgl.a" "$BUILD/shepherd_gltest" out/check-syms.txt
arm-vita-eabi-size "$BUILD/shepherd_gltest" | tee out/size.txt
unzip -l out/$VPKNAME | tee out/vpk-contents.txt
ls -la out/$VPKNAME

# 4) Mac self-check: the same gltest.c + ref.c on desktop GL (CGL legacy context, headless, no audio)
if [[ "${1:-}" != "--no-mac" ]]; then
  mkdir -p "$W/mac"
  OWNER_REF=(); [[ $OWNER == 1 ]] && grep -q "GLTEST_HAVE_STYLIZER 1" out/gen/shaders_gen.h && OWNER_REF=("$REPO/engine/Derived/vita-gltest/stylizer_ref.c")
  clang -std=gnu11 -O2 -ffp-contract=off -Wall -Wextra -Wno-unused-parameter -Ishepgl -Isrc -Iout/gen -DGLTEST_BUILD="\"mac-$STAMP\"" \
    src/gltest.c src/ref.c $OWNER_REF src/plat_mac.c shepgl/shepgl_mac.c -framework OpenGL -o "$W/mac/gltest_mac"
  GLTEST_OUT="$W/mac" "$W/mac/gltest_mac" > "$W/mac/stdout.txt" 2>&1 && rc=0 || rc=$?
  grep -E "^(SUMMARY|DONE)" "$W/mac/gltest.txt" | cut -c1-220
  [[ $rc -eq 0 ]] || { echo "Mac self-check FAILED (rc $rc): $W/mac/gltest.txt"; exit 1; }
fi
echo "built $STAMP: out/$VPKNAME"
