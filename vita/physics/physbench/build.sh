#!/bin/zsh
# PhysBench build + checks: the physics plan's Stage 0 device micro-benchmark (D4) and its Mac reference.
#   vita/physics/physbench/build.sh [lib|mac|vita|qemu|tsan|selftest|all] [--force-ref]   (default: all = every step)
#   lib    (re)builds libshepphys for the Mac and the Vita with vita/physics/build.sh (unchanged stage-0a script)
#   mac    physbench_mac; regenerates out/mac_ref.h + out/physbench-mac.txt (5 passes, best of) only when the sources'
#          sha changed (or --force-ref), so one canonical reference sticks across builds (the vita/probe pattern); the
#          reference links a no-op b2Timer as the Vita has (pb_timer_stub.cpp); physbench_mac_prof (canonical link, b2Profile
#          on) adds out/physbench-mac-prof.txt
#   vita   shepherd_physbench.vpk (SHPD00012) into out/; checks: vita-elf-create, strong undefined symbols, FMA / NEON f32 in
#          the bench objects, param.sfo, VPK contents
#   qemu   the eboot's scene objects on QEMU's emulated Cortex-A9: every scene's pose hash must equal the Mac reference's
#   tsan   the Mac bench built with ThreadSanitizer (library compiled in), threads cases only: race report summary
#   selftest  the comparators' failure paths: the bench linked against stage 0a's deliberately non-conforming risk-probe
#          libraries (FMA contraction on; Apple-libm trig) must report T1 DIFFERS with ulp counts and changed scene hashes
# CMake/Ninja from the ~/src path (the space in "${SHEPHERD_DATA:-$HOME}" breaks builds); build trees on ${SHEPHERD_WORK:-$HOME/shepherd-work};
# products and reports in physbench/out/ (git-ignored). Silent: no audio anywhere; no network.
# env: SHEPHERD_PHYS_SRC / SHEPHERD_PHYS_WORK as in vita/physics/build.sh (the lib step passes them on).
set -euo pipefail
zmodload zsh/datetime
PHYS="${SHEPHERD_PHYS_SRC:-${SHEPHERD_REPO:-$HOME/src/shepherd}/vita/physics}"   # a worktree without a space in its path: its own vita/physics
SRC="$PHYS/physbench"
cd "$SRC"
[[ -d ${SHEPHERD_WORK:-$HOME/shepherd-work} ]] || { echo "attach the build volume first"; exit 1; }
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
mkdir -p "$TMPDIR"
W=${SHEPHERD_PHYS_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/physics1/build}/physbench
OUT="$SRC/out"
mkdir -p "$W" "$OUT"
export VITASDK="${VITASDK:-$HOME/vitasdk}"
VBIN="$VITASDK/bin"
FORCE=0
for a in "$@"; do [[ "$a" == --force-ref ]] && FORCE=1; done

secs() { print -r -- $(( EPOCHREALTIME - $1 )) | cut -c1-5; }

# the sources the Mac reference's numbers depend on (the VPK refuses ratios against a reference of other sources)
src_sha() {
  { cat pb_bench.c pb_bench.h pb_scene.c pb_scene.h pb_b2.cpp pb_b2.h pb_pool.c pb_pool.h pb_plat.h pb_plat_mac.c mac_main.c \
        pb_timer_stub.cpp \
        CMakeLists.txt tools/mk_golden.py
    cat "$PHYS"/CMakeLists.txt "$PHYS"/shim/* "$PHYS"/tests/sp_t1.cpp "$PHYS"/tests/sp_t1.h "$PHYS"/tests/golden/*
    find "$PHYS/box2d/Box2D" -type f | LC_ALL=C sort | xargs cat
  } | shasum -a 256 | cut -c1-64
}
SHA=$(src_sha)

lib() {
  local t0=$EPOCHREALTIME
  zsh "$PHYS/build.sh" mac > "$W/lib-mac.log" 2>&1 || { tail -20 "$W/lib-mac.log"; exit 1; }
  zsh "$PHYS/build.sh" vita > "$W/lib-vita.log" 2>&1 || { tail -20 "$W/lib-vita.log"; exit 1; }
  grep -E "SUMMARY|check_syms" "$W/lib-mac.log" "$W/lib-vita.log" | sed 's/^.*log://' || true
  print "libshepphys (mac + vita) rebuilt by vita/physics/build.sh in $(secs $t0) s"
}

golden() { python3 tools/mk_golden.py "$PHYS/tests/golden" "$OUT/pb_t1_golden.h"; }

icon() {
  [[ -f sce_sys/icon0.png ]] && return
  mkdir -p sce_sys
  python3 - <<'EOF'
import struct, zlib, math
W = H = 128
pal = bytes([24, 20, 22,  209, 43, 39,  238, 232, 211,  70, 140, 200])   # ink, red, paper, blue
rows = b''
for y in range(H):
    row = bytearray([0])
    for x in range(W):
        c = 0
        dx, dy = x - 64, y - 60
        r = math.hypot(dx, dy)
        if 38 <= r <= 45: c = 2                                   # a stopwatch ring
        if 56 <= x <= 72 and 6 <= y <= 14: c = 2                  # its crown
        a = math.atan2(dy, dx)
        if r < 36 and abs(math.sin(a - math.radians(-55))) * r < 2.5 and math.cos(a - math.radians(-55)) > 0: c = 1   # the hand
        for (bx, by) in ((44, 108), (66, 108), (55, 94)):         # three boxes stacked under it (a physics test)
            if bx <= x < bx + 18 and by <= y < by + 12: c = 3
        row.append(c)
    rows += bytes(row)
def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 3, 0, 0, 0)) + chunk(b'PLTE', pal) \
    + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')
open('sce_sys/icon0.png', 'wb').write(png)
EOF
}

mac() {
  local t0=$EPOCHREALTIME d="$W/mac"
  golden
  cmake -S "$SRC" -B "$d" -G Ninja -DCMAKE_BUILD_TYPE=Release -DPB_SRC_SHA="$SHA" > "$d.cmake.log" 2>&1 || { tail -20 "$d.cmake.log"; exit 1; }
  cmake --build "$d" > "$d.build.log" 2>&1 || { grep -E "error|FAILED" "$d.build.log" | head -20; exit 1; }
  grep -E "warning:" "$d.build.log" | grep -v "^ld: warning" | head -5 || true
  cp "$d/physbench_mac" "$OUT/"
  print "mac build $(secs $t0) s (sources $SHA)"
  if (( FORCE == 0 )) && grep -q "$SHA" "$OUT/mac_ref.h" 2>/dev/null; then
    print "mac_ref.h is current ($SHA)"
  else
    local t1=$EPOCHREALTIME
    "$OUT/physbench_mac" --passes 5 --ref "$OUT/mac_ref.h" --sha "$SHA" --machine "$(sysctl -n machdep.cpu.brand_string)" \
      --report "$OUT/physbench-mac.txt" > "$W/mac-run.txt" 2>&1 || { tail -30 "$W/mac-run.txt"; print "physbench_mac FAILED"; exit 1; }
    "$d/physbench_mac_prof" --passes 3 --sections step,b2 --report "$OUT/physbench-mac-prof.txt" > "$W/mac-prof-run.txt" 2>&1 \
      || { tail -30 "$W/mac-prof-run.txt"; print "physbench_mac_prof FAILED"; exit 1; }
    print "mac reference regenerated in $(secs $t1) s: out/mac_ref.h, out/physbench-mac.txt (+ b2Profile: out/physbench-mac-prof.txt)"
  fi
  grep -E "^SUMMARY|^T1 mac RESULT" "$OUT/physbench-mac.txt" | tail -2
}

vita() {
  local t0=$EPOCHREALTIME d="$W/vita" rep="$OUT/vita-check.txt"
  [[ -f "$OUT/mac_ref.h" ]] && grep -q "$SHA" "$OUT/mac_ref.h" || { print "out/mac_ref.h missing or stale: run build.sh mac first"; exit 1; }
  golden
  icon
  PATH="$VBIN:$PATH" cmake -S "$SRC" -B "$d" -G Ninja -DCMAKE_BUILD_TYPE=Release -DPB_SRC_SHA="$SHA" \
    -DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" > "$d.cmake.log" 2>&1 || { tail -20 "$d.cmake.log"; exit 1; }
  PATH="$VBIN:$PATH" cmake --build "$d" > "$d.build.log" 2>&1 || { grep -E "error|undefined|FAILED" "$d.build.log" | head -30; exit 1; }
  grep -E "warning:" "$d.build.log" | head -5 || true
  cp "$d/shepherd_physbench.vpk" "$d/eboot.bin" "$OUT/"
  print "vita build $(secs $t0) s" | tee "$rep"
  local fail=0
  # 1. vita-elf-create accepts the ELF (the build ran it to make eboot.bin; run it again, verbosely counted)
  if "$VBIN/vita-elf-create" "$d/shepherd_physbench" "$W/check.velf" > "$W/elf-create.log" 2>&1; then
    print "1. vita-elf-create: OK ($(stat -f %z "$W/check.velf") B velf)" | tee -a "$rep"
  else print "1. vita-elf-create: FAILED" | tee -a "$rep"; tail -5 "$W/elf-create.log"; fail=1; fi
  # 2. no strong undefined symbols; imported libraries
  local und=$("$VBIN/arm-vita-eabi-nm" -u "$d/shepherd_physbench" | awk '$1=="U"{print $2}' | tr '\n' ' ')
  print "2. strong undefined symbols: ${und:-none}" | tee -a "$rep"
  [[ -z "$und" ]] || fail=1
  # 3. the bench's own objects: no fused multiply-add, no NEON f32 arithmetic (the determinism contract)
  local objs=($(find "$d/CMakeFiles" -name 'pb_*.o*' -o -name 'vita_main*.o*' | grep -v '\.d$'))
  local dis="$("$VBIN/arm-vita-eabi-objdump" -d $objs 2>/dev/null)"
  local fma=$(print -r -- "$dis" | grep -cE $'\t''vf(n)?m[as]\.' || true)
  local neon=$(print -r -- "$dis" | grep -E $'\t''v[a-z]+\.f32'$'\t''[dq][0-9]+|'$'\t''vcvt\.[fsu]32\.[fsu]32'$'\t''[dq][0-9]+' | grep -cvE $'\t''vmov\.f32'$'\t' || true)
  print "3. bench objects (${#objs}): fused FMA $fma, NEON f32 arithmetic $neon" | tee -a "$rep"
  (( fma == 0 && neon == 0 )) || fail=1
  # 4. param.sfo and VPK contents
  python3 - "$OUT/shepherd_physbench.vpk" <<'EOF' | tee -a "$rep"
import struct, sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
names = sorted(z.namelist())
sfo = z.read('sce_sys/param.sfo')
magic, ver, kt, dt, n = struct.unpack('<IIIII', sfo[:20])
vals = {}
for i in range(n):
    ko, fmt, ln, mx, do = struct.unpack('<HHIII', sfo[20 + 16 * i: 36 + 16 * i])
    k = sfo[kt + ko: sfo.index(b'\0', kt + ko)].decode()
    raw = sfo[dt + do: dt + do + ln]
    vals[k] = raw.rstrip(b'\0').decode() if fmt == 0x0204 else struct.unpack('<I', raw[:4])[0]
print("4. param.sfo TITLE_ID %s, TITLE %r, APP_VER %s, ATTRIBUTE2 %s" % (vals.get('TITLE_ID'), vals.get('TITLE'), vals.get('APP_VER'), vals.get('ATTRIBUTE2', '-')))
print("   VPK files: " + ", ".join("%s (%d B)" % (nm, z.getinfo(nm).file_size) for nm in names))
EOF
  "$VBIN/arm-vita-eabi-size" "$d/shepherd_physbench" | tee -a "$rep"
  ls -l "$OUT/shepherd_physbench.vpk" "$OUT/eboot.bin" | awk '{print "   " $5, $9}' | tee -a "$rep"
  print "vita checks: $([[ $fail -eq 0 ]] && print OK || print FAILED)" | tee -a "$rep"
  [[ $fail -eq 0 ]] || exit 1
}

qemu() {
  local t0=$EPOCHREALTIME d="$W/qemu" CC="$VBIN/arm-vita-eabi-gcc"
  mkdir -p "$d"
  local CF=(-mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -O2 -ffp-contract=off -Wall -I"$SRC" -I"$PHYS/shim")
  "$CC" "${CF[@]}" -c "$PHYS/qemu/start.S" -o "$d/start.o"
  "$CC" "${CF[@]}" -ffreestanding -fno-builtin -fno-tree-loop-distribute-patterns -fno-math-errno -c qemu/pb_qemu.c -o "$d/pb_qemu.o"
  "$CC" -nostartfiles -nostdlib -mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -T "$PHYS/qemu/link.ld" -Wl,--gc-sections \
    -Wl,--no-warn-rwx-segments -o "$d/pb_qemu.elf" "$d/start.o" "$d/pb_qemu.o" "$W/vita/libpbscene_vita.a" \
    "$PHYS/build/vita/libshepphys_vita.a" "$("$CC" -print-libgcc-file-name)" 2> "$d/link.log" \
    || { grep -E "undefined|error" "$d/link.log" | head -30; exit 1; }
  local rc=0
  /opt/homebrew/bin/qemu-system-arm -M vexpress-a9 -cpu cortex-a9 -m 256M -nographic -nodefaults -monitor none -serial none \
    -audio none -nic none -semihosting-config enable=on,target=native -kernel "$d/pb_qemu.elf" > "$d/run.txt" 2>&1 || rc=$?
  local ok=0 n=0
  : > "$OUT/qemu-scenes.txt"
  for line in ${(f)"$(grep '^QEMU SCENE' "$d/run.txt")"}; do
    local name=$(print -r -- "$line" | awk '{print $3}') h=$(print -r -- "$line" | awk '{print $5}')
    local m=$(grep "\"hash.$name\"" "$OUT/mac_ref.h" | sed -E 's/.*0x([0-9a-f]{16})ull.*/\1/')
    n=$((n + 1))
    if [[ "$h" == "$m" ]]; then ok=$((ok + 1)); print "QEMU $name $h = mac" | tee -a "$OUT/qemu-scenes.txt"
    else print "QEMU $name $h != mac $m" | tee -a "$OUT/qemu-scenes.txt"; fi
  done
  print "qemu: $ok/$n scene hashes on the emulated Cortex-A9 equal the Mac reference ($(secs $t0) s, exit $rc)" | tee -a "$OUT/qemu-scenes.txt"
  [[ $rc -eq 0 && $ok -eq $n && $n -gt 0 ]] || exit 1
}

tsan() {
  local t0=$EPOCHREALTIME d="$W/tsan"
  golden
  cmake -S "$SRC" -B "$d" -G Ninja -DCMAKE_BUILD_TYPE=Release -DPB_SRC_SHA="$SHA" -DPB_LIB_FROM_SOURCE=ON \
    -DPB_SAN=-fsanitize=thread > "$d.cmake.log" 2>&1 || { tail -20 "$d.cmake.log"; exit 1; }
  cmake --build "$d" > "$d.build.log" 2>&1 || { grep -E "error|FAILED" "$d.build.log" | head -20; exit 1; }
  TSAN_OPTIONS="halt_on_error=0 history_size=2" "$d/physbench_mac" --quick --sections threads > "$W/tsan-run.txt" 2>&1 || true
  local races=$(grep -c "WARNING: ThreadSanitizer: data race" "$W/tsan-run.txt" || true)
  {
    print "tsan: $races data-race reports (physbench_mac --quick; Box2D + shim compiled in with -fsanitize=thread); racing objects:"
    grep -E "Location is (global|heap)" "$W/tsan-run.txt" | sed -E 's/ at 0x[0-9a-f]+//; s/ \(physbench_mac.*\)//; s/^ +//' \
      | sort | uniq -c | sort -rn | head -10
    print "first frames of the racing accesses:"
    grep -A3 -E "^  (Write|Read|Previous write|Previous read) of size" "$W/tsan-run.txt" | grep -E "#0 |#1 " \
      | sed -E 's/ <null>.*//; s/^ +//' | sort | uniq -c | sort -rn | head -8
  } | tee "$OUT/tsan-summary.txt"
  print "tsan run $(secs $t0) s"
}

selftest() {
  local t0=$EPOCHREALTIME rep="$OUT/selftest.txt"
  golden
  : > "$rep"
  for v in fma-on libm-trig; do
    local lib=${W:h}/v-$v/libshepphys.a d="$W/selftest-$v"   # the risk-probe builds of vita/physics/build.sh verify (SHEPHERD_PHYS_WORK)
    [[ -f "$lib" ]] || { print "selftest: $lib missing (run vita/physics/build.sh verify)" | tee -a "$rep"; continue; }
    cmake -S "$SRC" -B "$d" -G Ninja -DCMAKE_BUILD_TYPE=Release -DPB_SRC_SHA="$SHA" -DPB_MAC_LIB="$lib" > "$d.cmake.log" 2>&1 \
      || { tail -20 "$d.cmake.log"; exit 1; }
    cmake --build "$d" --target physbench_mac > "$d.build.log" 2>&1 || { grep -E "error|FAILED" "$d.build.log" | head; exit 1; }
    "$d/physbench_mac" --passes 1 --sections t1,step > "$d.run.txt" 2>&1 || true
    print "== library variant $v (expected: T1 differences; scene hashes change with FMA, need not with trig)" | tee -a "$rep"
    grep -E "^T1 mac .*(DIFFERS|RESULT)" "$d.run.txt" | cut -c1-230 | tee -a "$rep"
    for line in ${(f)"$(grep -E '^STEP mac .* pose hash' "$d.run.txt")"}; do
      local name=$(print -r -- "$line" | awk '{print $3}') h=$(print -r -- "$line" | awk '{print $6}')
      local m=$(grep "\"hash.$name\"" "$OUT/mac_ref.h" | sed -E 's/.*0x([0-9a-f]{16})ull.*/\1/')
      print "   scene $name: $h $([[ $h == $m ]] && print '= reference' || print '!= reference')" | tee -a "$rep"
    done
  done
  print "selftest $(secs $t0) s" | tee -a "$rep"
}

what=${1:-all}
[[ "$what" == --force-ref ]] && what=all
case "$what" in
  lib) lib ;; mac) mac ;; vita) vita ;; qemu) qemu ;; tsan) tsan ;; selftest) selftest ;;
  all) lib; mac; vita; qemu; tsan; selftest ;;
  *) print "usage: $0 [lib|mac|vita|qemu|tsan|selftest|all] [--force-ref]"; exit 2 ;;
esac
