#!/bin/zsh
# libshepphys build + checks (vendored Box2D 2.3.1, our shim, the T1 determinism scenarios).
#   vita/physics/build.sh [mac|vita|qemu|verify|golden|san|prof|all]   (default: all = mac vita qemu verify san)
#   mac     Mac arm64: build/mac/libshepphys.dylib (+ .a, t1_runner); T1 check against tests/golden; FMA scan
#   vita    Vita: build/vita/libshepphys_vita.a, the T1 test eboot + VPK (SHPD00020), symbol check, FMA/NEON scan
#   qemu    runs libshepphys_vita.a's scenarios on QEMU's emulated Cortex-A9 (bare metal, silent) against the goldens
#   verify  determinism variants on the Mac: -O0 + asserts, -O3, x86_64 (Rosetta), ASan/UBSan, flush-to-zero; and the
#           two expected-to-differ risk probes: -ffp-contract=on (FMA) and Apple-libm trig
#   golden  re-record tests/golden (canonical build: arm64 -O2 -ffp-contract=off, own trig) -- only after an intended change
#   san     Stage 0: the ASan + UBSan build (asserts on, -O1) running T1, the Stage 0 self-test and the trace replay
#   prof    the Mac library with Box2D's stock b2Timer (b2Profile phase splits) -> build/mac-prof/ (physbench_mac_prof links it)
#   qemu-replay <trace.sptr>   Stage 1: an engine drive's trace replayed on the Mac and on QEMU's Cortex-A9 (the Vita archive), bit for bit
# Stage 0 (physics-plan-v2 section 4) also made `mac` check B3 (the link map: b2Timer's symbols from sp_timer_stub.o), run the Stage 0
# self-test (t1_runner s0) and trace + replay every T1 scenario (t1_runner trace), and build the prof library; `qemu` runs the
# self-test on the emulated Cortex-A9 too, whose hash must equal the Mac's.
# CMake/Ninja run from the ~/src path (the space in "${SHEPHERD_DATA:-$HOME}" breaks builds); build trees on ${SHEPHERD_WORK:-$HOME/shepherd-work};
# copies of the products in vita/physics/build/ and reports in vita/physics/out/ (both git-ignored). No audio, no network.
# env (both default to the main checkout's setup): SHEPHERD_PHYS_SRC (the vita/physics to build, e.g. a worktree's) and
# SHEPHERD_PHYS_WORK (the build trees; give each worktree its own).
set -euo pipefail
zmodload zsh/datetime
SRC="${SHEPHERD_PHYS_SRC:-${SHEPHERD_REPO:-$HOME/src/shepherd}/vita/physics}"   # a worktree without a space in its path: its own vita/physics
cd "$SRC"
[[ -d ${SHEPHERD_WORK:-$HOME/shepherd-work} ]] || { echo "attach the build volume first"; exit 1; }
export TMPDIR=${SHEPHERD_PHYS_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}   # (kit #8: a track keeps its scratch in its own work dir)
mkdir -p "$TMPDIR"
W=${SHEPHERD_PHYS_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/physics1/build}
OUT="$SRC/out"
mkdir -p "$W" "$OUT" build/mac build/vita
export VITASDK="${VITASDK:-$HOME/vitasdk}"
VBIN="$VITASDK/bin"

secs() { print -r -- $(( EPOCHREALTIME - $1 )) | cut -c1-5; }

mac_build() {   # <dir> [cmake -D args...]
  local d="$1"; shift
  cmake -S "$SRC" -B "$d" -G Ninja -DCMAKE_BUILD_TYPE=Release "$@" > "$d.cmake.log" 2>&1 || { tail -20 "$d.cmake.log"; exit 1; }
  cmake --build "$d" > "$d.build.log" 2>&1 || { grep -E "error|FAILED" "$d.build.log" | head -20; exit 1; }
  grep -E "warning:" "$d.build.log" | grep -v "^ld: warning" | head -5 || true
}

mac() {
  local t0=$EPOCHREALTIME
  mac_build "$W/mac"
  cp "$W/mac/libshepphys.dylib" "$W/mac/libshepphys.a" "$W/mac/t1_runner" build/mac/
  print "mac build $(secs $t0) s: $(stat -f %z build/mac/libshepphys.dylib) B dylib, $(stat -f %z build/mac/libshepphys.a) B static"
  build/mac/t1_runner info | tee "$OUT/mac-info.txt"
  build/mac/t1_runner check tests/golden "$W/t1-mac" | tee "$OUT/t1-mac.txt" | grep -E "MISMATCH|SUMMARY|first|golden:|got:" || true
  grep -q "8/8 match the golden files, 8/8" "$OUT/t1-mac.txt" || { print "T1 mac FAILED (see $OUT/t1-mac.txt)"; exit 1; }
  build/mac/t1_runner time 5 | tee "$OUT/t1-mac-time.txt" | tail -2
  # no fused multiply-add may appear in the arm64 code (the -ffp-contract=off contract)
  local fma=$(otool -tv build/mac/libshepphys.dylib | grep -cE $'\t''(fmadd|fmsub|fnmadd|fnmsub|fmla|fmls)'$'\t' || true)
  print "mac FMA instructions in libshepphys.dylib: $fma" | tee "$OUT/mac-fma.txt"
  [[ "$fma" == 0 ]] || exit 1
  b3_check "$W/mac/libshepphys.map" | tee "$OUT/b3-linkmap.txt"
  grep -q "^B3 OK" "$OUT/b3-linkmap.txt" || exit 1
  b2_check "$W/mac/libshepphys.map" | tee "$OUT/b2-linkmap.txt"
  grep -q "^B2 OK" "$OUT/b2-linkmap.txt" || exit 1
  s0_trace build/mac/t1_runner mac
  prof
}

# B3: in the dylib's link map b2Timer.cpp.o is absent and every b2Timer symbol is defined by sp_timer_stub.o. ld64 deduplicates
# identical function bodies by default (the empty constructors and Reset are one `ret`): the map then names such a symbol "linker
# synthesized"; it is accepted only if sp_timer_stub.o itself defines that symbol (nm of the object in the build tree).
b3_check() {
  local stub=$(find "${1:h}" -name 'sp_timer_stub.cpp.o' | head -1)
  python3 - "$1" "$stub" <<'PYEOF'
import re, subprocess, sys
files, syms, sec = {}, [], None
for line in open(sys.argv[1], errors="replace"):
    if line.startswith("# Object files:"): sec = "o"; continue
    if line.startswith("# Sections:"): sec = "s"; continue
    if line.startswith("# Symbols:"): sec = "y"; continue
    m = re.match(r"\[\s*(\d+)\]\s+(.*)$", line.strip())
    if sec == "o" and m: files[int(m.group(1))] = m.group(2).rsplit("/", 1)[-1]
    elif sec == "y":
        m = re.match(r"0x[0-9A-Fa-f]+\s+0x[0-9A-Fa-f]+\s+\[\s*(\d+)\]\s+(\S+)", line)
        if m and "b2Timer" in m.group(2): syms.append((m.group(2), files.get(int(m.group(1)), "?")))
stubdefs = set()
if len(sys.argv) > 2 and sys.argv[2]:
    out = subprocess.run(["nm", "-g", "--defined-only", sys.argv[2]], capture_output=True, text=True).stdout
    stubdefs = {l.split()[-1] for l in out.splitlines() if l.strip()}
timer_linked = any(f.startswith("b2Timer") for f in files.values())
good = lambda n, f: f.startswith("sp_timer_stub") or (f == "linker synthesized" and n in stubdefs)
ok = bool(syms) and all(good(n, f) for n, f in syms) and not timer_linked
for n, f in syms: print(f"  {n}  <- {f}{' (deduplicated by ld64; sp_timer_stub.cpp.o defines it)' if f == 'linker synthesized' and n in stubdefs else ''}")
print(f"  sp_timer_stub.cpp.o defines: {' '.join(sorted(stubdefs)) or '?'}")
print(f"B3 {'OK' if ok else 'FAILED'}: {len(syms)} b2Timer symbols, all from sp_timer_stub.cpp.o; b2Timer.cpp.o "
      f"{'IS LINKED' if timer_linked else 'is not linked'}")
PYEOF
}

# B2 (Stage 1): b2Settings.cpp.o is not linked; b2Alloc / b2Free / b2Log / b2_version come from sp_b2glue.cpp.o
b2_check() {
  python3 - "$1" <<'PYEOF'
import re, sys
files, sec, defs = {}, None, {}
for line in open(sys.argv[1], errors="replace"):
    if line.startswith("# Object files:"): sec = "o"; continue
    if line.startswith("# Sections:"): sec = "s"; continue
    if line.startswith("# Symbols:"): sec = "y"; continue
    m = re.match(r"\[\s*(\d+)\]\s+(.*)$", line.strip())
    if sec == "o" and m: files[int(m.group(1))] = m.group(2).rsplit("/", 1)[-1]
    elif sec == "y":
        m = re.match(r"0x[0-9A-Fa-f]+\s+0x[0-9A-Fa-f]+\s+\[\s*(\d+)\]\s+(\S+)$", line.strip())
        if m and m.group(2) in ("__Z7b2Alloci", "__Z6b2FreePv", "__Z5b2LogPKcz", "_b2_version"): defs[m.group(2)] = files.get(int(m.group(1)), "?")
bad = [f for f in files.values() if f.startswith("b2Settings.cpp")]
for k, v in sorted(defs.items()): print(f"  {k}  <- {v}")
ok = not bad and len(defs) == 4 and all(v == "sp_b2glue.cpp.o" for v in defs.values())
print(("B2 OK: b2Alloc / b2Free / b2Log / b2_version from sp_b2glue.cpp.o; b2Settings.cpp.o is not linked") if ok else f"B2 FAILED: {defs} {bad}")
PYEOF
}

# Stage 0: the self-test (pool + settings check) and the trace writer + replay of every T1 scenario, with one t1_runner
s0_trace() {   # <t1_runner> <label>
  "$1" s0 > "$OUT/s0-$2.txt" 2>&1 || { tail -5 "$OUT/s0-$2.txt"; print "S0 $2 FAILED"; exit 1; }
  grep "S0 RESULT" "$OUT/s0-$2.txt"
  "$1" s1 > "$OUT/s1-$2.txt" 2>&1 || { tail -5 "$OUT/s1-$2.txt"; print "S1 $2 FAILED"; exit 1; }   # Stage 1: the Unity layer
  grep "S1 RESULT" "$OUT/s1-$2.txt"
  "$1" s2 > "$OUT/s2-$2.txt" 2>&1 || { tail -5 "$OUT/s2-$2.txt"; print "S2 $2 FAILED"; exit 1; }   # Stage 2: P1, serials, queries
  grep "S2 RESULT" "$OUT/s2-$2.txt"
  "$1" s3 > "$OUT/s3-$2.txt" 2>&1 || { tail -5 "$OUT/s3-$2.txt"; print "S3 $2 FAILED"; exit 1; }   # Stage 3: forces, damping, contacts
  grep "S3 RESULT" "$OUT/s3-$2.txt"
  "$1" s6 > "$OUT/s6-$2.txt" 2>&1 || { tail -5 "$OUT/s6-$2.txt"; print "S6 $2 FAILED"; exit 1; }   # Stage 6: the 3D island-ground ray
  grep "S6 RESULT" "$OUT/s6-$2.txt"
  local hs=(); for k in s1 s2 s3 s6; do hs+=($(sed -nE 's/.*RESULT PASS hash ([0-9a-f]+).*/\1/p' "$OUT/$k-$2.txt")); done
  "$1" host $hs > "$OUT/host-$2.txt" 2>&1 || { cat "$OUT/host-$2.txt"; print "HOST $2 FAILED"; exit 1; }   # kit #8: the Vita host's pool block (sp_host_memory)
  grep -E "HOST (RESULT|contract)" "$OUT/host-$2.txt"
  rm -rf "$W/trace-$2"
  "$1" trace "$W/trace-$2" > "$OUT/trace-$2.txt" 2>&1 || { cat "$OUT/trace-$2.txt"; print "TRACE $2 FAILED"; exit 1; }
  grep "TRACE SUMMARY" "$OUT/trace-$2.txt"
  rm -rf "$W/trace-$2"
}

prof() {
  mac_build "$W/mac-prof" -DSHEPPHYS_PROF=ON
  mkdir -p build/mac-prof
  cp "$W/mac-prof/libshepphys.a" build/mac-prof/
  print "prof library (stock b2Timer, b2Profile on): build/mac-prof/libshepphys.a"
}

san() {
  local d="$W/san" f="-fsanitize=address,undefined -fno-sanitize-recover=undefined"
  mac_build "$d" -DSHEPPHYS_OPT=-O1 -DSHEPPHYS_NDEBUG=OFF "-DCMAKE_C_FLAGS=$f" "-DCMAKE_CXX_FLAGS=$f" \
    "-DCMAKE_SHARED_LINKER_FLAGS=$f" "-DCMAKE_EXE_LINKER_FLAGS=$f"
  local rep="$OUT/san.txt" r=0
  : > "$rep"
  export UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1 ASAN_OPTIONS=detect_leaks=0
  "$d/t1_runner" check tests/golden "$W/t1-san" >> "$rep" 2>&1 || r=1
  "$d/t1_runner" s0 >> "$rep" 2>&1 || r=1
  "$d/t1_runner" s1 >> "$rep" 2>&1 || r=1
  "$d/t1_runner" s2 >> "$rep" 2>&1 || r=1
  "$d/t1_runner" s3 >> "$rep" 2>&1 || r=1
  "$d/t1_runner" s6 >> "$rep" 2>&1 || r=1
  "$d/t1_runner" trace "$W/trace-san" >> "$rep" 2>&1 || r=1
  rm -rf "$W/trace-san"
  grep -E "T1 SUMMARY|S0 RESULT|S1 RESULT|S2 RESULT|S3 RESULT|S6 RESULT|TRACE SUMMARY|ERROR|runtime error" "$rep" | cut -c1-160
  grep -qE "ERROR: AddressSanitizer|runtime error" "$rep" && r=1
  print "san (ASan + UBSan, -O1, asserts on): $( (( r == 0 )) && print 'clean: T1 8/8, S0 + S1 + S2 + S3 + S6 pass, trace replay 8/8, no reports' || print FAILED)" | tee -a "$rep"
  (( r == 0 )) || exit 1
}

golden() {
  mac_build "$W/mac"
  "$W/mac/t1_runner" record tests/golden
  mac_build "$W/mac"            # recompile with the new compiled-in hashes
  mac
  print "golden recorded: tests/golden (rebuild the Vita side: build.sh vita qemu)"
}

vita() {
  local t0=$EPOCHREALTIME
  mkdir -p vita/sce_sys
  [[ -f vita/sce_sys/icon0.png ]] || python3 - <<'EOF'
import struct, zlib
W = H = 128
pal = bytes([24, 20, 22,  209, 43, 39,  238, 232, 211,  70, 140, 200])   # ink, red, paper, blue
rows = b''
for y in range(H):
    row = bytearray([0])
    for x in range(W):
        # three stacked boxes falling onto a ground line (a physics test), with the cult's eye on the top box
        c = 0
        if y >= 104 and y <= 110 and 12 <= x <= 116: c = 3
        for (bx, by, s) in ((40, 76, 26), (72, 76, 26), (56, 48, 26)):
            if bx <= x < bx + s and by <= y < by + s: c = 2
        dx, dy = x - 69, y - 61
        if c == 2 and (dx * dx) / 90 + (dy * dy) / 30 <= 1.0: c = 1
        row.append(c)
    rows += bytes(row)
def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 3, 0, 0, 0)) + chunk(b'PLTE', pal) \
    + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')
open('vita/sce_sys/icon0.png', 'wb').write(png)
EOF
  local d="$W/vita"
  PATH="$VBIN:$PATH" cmake -S "$SRC" -B "$d" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" > "$d.cmake.log" 2>&1 || { tail -20 "$d.cmake.log"; exit 1; }
  PATH="$VBIN:$PATH" cmake --build "$d" > "$d.build.log" 2>&1 || { grep -E "error|undefined|FAILED" "$d.build.log" | head -30; exit 1; }
  cp "$d/libshepphys_vita.a" build/vita/
  cp "$d/shepherd_phys_t1.vpk" "$d/eboot.bin" build/vita/
  print "vita build $(secs $t0) s"
  zsh tools/check_syms.sh "$d/libshepphys_vita.a" "$d/shepherd_phys_t1" "$OUT/vita-check.txt"
  "$VBIN/arm-vita-eabi-size" "$d/libshepphys_vita.a" | awk 'NR>1{t+=$1;dd+=$2;b+=$3} END{printf "libshepphys_vita.a: text %d data %d bss %d bytes (all members)\n",t,dd,b}' | tee -a "$OUT/vita-check.txt"
  "$VBIN/arm-vita-eabi-size" "$d/shepherd_phys_t1" | tee -a "$OUT/vita-check.txt"
  ls -l build/vita/ | awk 'NR>1{print "  " $5, $9}' | tee -a "$OUT/vita-check.txt"
}

qemu_link() {   # <out.elf> <libshepphys_vita.a> [extra libs...]
  local elf="$1" lib="$2"; shift 2
  local d="$W/qemu" CC="$VBIN/arm-vita-eabi-gcc"
  local CF=(-mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -O2 -ffp-contract=off -ffunction-sections -fdata-sections -Wall
            -I"$SRC/shim" -I"$SRC/tests")
  mkdir -p "$d"
  "$CC" "${CF[@]}" -c qemu/start.S -o "$d/start.o"
  "$CC" "${CF[@]}" -ffreestanding -fno-builtin -fno-tree-loop-distribute-patterns -fno-math-errno -c qemu/harness.c -o "$d/harness.o"
  "$CC" -nostartfiles -nostdlib -mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -T qemu/link.ld -Wl,--gc-sections \
    -Wl,--no-warn-rwx-segments -Wl,-Map="${elf%.elf}.map" -o "$elf" "$d/start.o" "$d/harness.o" "$lib" "$@" \
    "$("$CC" -print-libgcc-file-name)" 2> "$d/link.log" \
    || { grep -E "undefined|error" "$d/link.log" | head -30; exit 1; }
}

qemu_run() {   # <elf> <out.txt>: silent and offline (no display, serial, monitor, audio backend or network device)
  /opt/homebrew/bin/qemu-system-arm -M vexpress-a9 -cpu cortex-a9 -m 256M -nographic -nodefaults -monitor none -serial none \
    -audio none -nic none -semihosting-config enable=on,target=native -kernel "$1" > "$2" 2>&1   # semihosting -> stderr
}

qemu() {
  local t0=$EPOCHREALTIME d="$W/qemu"
  qemu_link "$d/t1_qemu.elf" "$W/vita/libshepphys_vita.a"
  print "qemu harness linked: $("$VBIN/arm-vita-eabi-size" "$d/t1_qemu.elf" | awk 'NR==2{print $1+$2 " B"}')"
  local rc=0
  qemu_run "$d/t1_qemu.elf" "$d/run.txt" || rc=$?
  grep -v "^DUMP" "$d/run.txt" | tee "$OUT/t1-qemu.txt" | grep -E "abi|fpscr at|SUMMARY|RESULT|MISMATCH|FAIL" || true
  if grep -q "^DUMP" "$d/run.txt"; then
    for n in $(grep "^DUMP" "$d/run.txt" | awk '{print $2}' | sort -u); do
      grep "^DUMP $n " "$d/run.txt" | cut -d' ' -f3- > "$d/dump-$n.txt"
      grep -v '^#' tests/golden/$n.txt | grep -v '^hash' | sed 's/ | [^|]*$/ |/' > "$d/golden-$n.txt"
      print "  first differences for $n:"; diff "$d/golden-$n.txt" "$d/dump-$n.txt" | head -6
    done
  fi
  print "qemu run $(secs $t0) s, exit $rc"
  [[ $rc -eq 0 ]] || exit 1
  # Stage 0: the self-test's hash on the emulated Cortex-A9 must equal the Mac's (pool offsets, settings-check floats)
  local qh=$(grep "QEMU S0 RESULT" "$d/run.txt" | sed -E 's/.*hash ([0-9a-f]+).*/\1/') mh=$(grep "S0 RESULT" "$OUT/s0-mac.txt" 2>/dev/null | sed -E 's/.*hash ([0-9a-f]+).*/\1/')
  print "S0 qemu hash $qh, mac hash ${mh:-missing (run build.sh mac first)}: $([[ -n "$mh" && "$qh" == "$mh" ]] && print equal || print DIFFERENT)" | tee "$OUT/s0-qemu-vs-mac.txt"
  [[ -n "$mh" && "$qh" == "$mh" ]] || exit 1
  # Stage 1: the Unity layer's self-test hash likewise (MovePosition velocities, affines, decomposition, lerps, render poses)
  local q1=$(grep "QEMU S1 RESULT" "$d/run.txt" | sed -E 's/.*hash ([0-9a-f]+).*/\1/') m1=$(grep "S1 RESULT" "$OUT/s1-mac.txt" 2>/dev/null | sed -E 's/.*hash ([0-9a-f]+).*/\1/')
  print "S1 qemu hash $q1, mac hash ${m1:-missing (run build.sh mac first)}: $([[ -n "$m1" && "$q1" == "$m1" ]] && print equal || print DIFFERENT)" | tee "$OUT/s1-qemu-vs-mac.txt"
  [[ -n "$m1" && "$q1" == "$m1" ]] || exit 1
  # Stage 2: P1's probes, contact serials and the Unity queries likewise (sweeps, bounds, closest points)
  local q2=$(grep "QEMU S2 RESULT" "$d/run.txt" | sed -E 's/.*hash ([0-9a-f]+).*/\1/') m2=$(grep "S2 RESULT" "$OUT/s2-mac.txt" 2>/dev/null | sed -E 's/.*hash ([0-9a-f]+).*/\1/')
  print "S2 qemu hash $q2, mac hash ${m2:-missing (run build.sh mac first)}: $([[ -n "$m2" && "$q2" == "$m2" ]] && print equal || print DIFFERENT)" | tee "$OUT/s2-qemu-vs-mac.txt"
  [[ -n "$m2" && "$q2" == "$m2" ]] || exit 1
  # Stage 3: forces, damping variants, restitution, the clamp, frozen axes, one-sided chains, contact snapshots likewise
  local q3=$(grep "QEMU S3 RESULT" "$d/run.txt" | sed -E 's/.*hash ([0-9a-f]+).*/\1/') m3=$(grep "S3 RESULT" "$OUT/s3-mac.txt" 2>/dev/null | sed -E 's/.*hash ([0-9a-f]+).*/\1/')
  print "S3 qemu hash $q3, mac hash ${m3:-missing (run build.sh mac first)}: $([[ -n "$m3" && "$q3" == "$m3" ]] && print equal || print DIFFERENT)" | tee "$OUT/s3-qemu-vs-mac.txt"
  [[ -n "$m3" && "$q3" == "$m3" ]] || exit 1
  # Stage 6: the 3D island-ground ray (the tilted Unity plane's hit bits, culling, primitives, modes, 20,000 fast = general = brute) likewise
  local q6=$(grep "QEMU S6 RESULT" "$d/run.txt" | sed -E 's/.*hash ([0-9a-f]+).*/\1/') m6=$(grep "S6 RESULT" "$OUT/s6-mac.txt" 2>/dev/null | sed -E 's/.*hash ([0-9a-f]+).*/\1/')
  print "S6 qemu hash $q6, mac hash ${m6:-missing (run build.sh mac first)}: $([[ -n "$m6" && "$q6" == "$m6" ]] && print equal || print DIFFERENT)" | tee "$OUT/s6-qemu-vs-mac.txt"
  [[ -n "$m6" && "$q6" == "$m6" ]] || exit 1
}

verify() {
  local v rep="$OUT/t1-verify.txt"
  : > "$rep"
  typeset -A ARGS
  ARGS=(
    o0-asserts  "-DSHEPPHYS_OPT=-O0 -DSHEPPHYS_NDEBUG=OFF -DCMAKE_BUILD_TYPE=Debug"
    o3          "-DSHEPPHYS_OPT=-O3"
    x86_64      "-DCMAKE_OSX_ARCHITECTURES=x86_64"
    asan-ubsan  "-DSHEPPHYS_OPT=-O1 -DSHEPPHYS_NDEBUG=OFF -DCMAKE_C_FLAGS=-fsanitize=address,undefined -DCMAKE_CXX_FLAGS=-fsanitize=address,undefined -DCMAKE_SHARED_LINKER_FLAGS=-fsanitize=address,undefined -DCMAKE_EXE_LINKER_FLAGS=-fsanitize=address,undefined"
    fma-on      "-DSHEPPHYS_FP_CONTRACT=on"
    libm-trig   "-DSHEPPHYS_TRIG_OWN=OFF"
  )
  for v in o0-asserts o3 x86_64 asan-ubsan fma-on libm-trig; do
    mac_build "$W/v-$v" ${=ARGS[$v]}
    local r=0
    local runner=("$W/v-$v/t1_runner")
    [[ "$v" == x86_64 ]] && runner=(arch -x86_64 "$W/v-$v/t1_runner")
    UBSAN_OPTIONS=halt_on_error=1 ASAN_OPTIONS=detect_leaks=0 "${runner[@]}" check tests/golden "$W/t1-$v" > "$W/t1-$v.txt" 2>&1 || r=$?
    print "== $v (expect $( [[ $v == fma-on || $v == libm-trig ]] && print 'differences: risk probe' || print 'all 8 OK'))" | tee -a "$rep"
    grep -E "MISMATCH|SUMMARY|ERROR|runtime error" "$W/t1-$v.txt" | cut -c1-150 | tee -a "$rep" || true
  done
  print "== fz (flush-to-zero on the canonical build)" | tee -a "$rep"
  build/mac/t1_runner check tests/golden "$W/t1-fz" --fz | grep -E "MISMATCH|SUMMARY" | tee -a "$rep" || true
  # the Vita side of the trig risk: the same Vita build with newlib's sinf/cosf, on the emulated Cortex-A9
  local d="$W/vita-libm"
  PATH="$VBIN:$PATH" cmake -S "$SRC" -B "$d" -G Ninja -DCMAKE_BUILD_TYPE=Release -DSHEPPHYS_TRIG_OWN=OFF \
    -DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" > "$d.cmake.log" 2>&1 || { tail -20 "$d.cmake.log"; exit 1; }
  PATH="$VBIN:$PATH" cmake --build "$d" --target shepphys_vita > "$d.build.log" 2>&1 || { tail -20 "$d.build.log"; exit 1; }
  qemu_link "$W/qemu/t1_qemu_libm.elf" "$d/libshepphys_vita.a" "$VITASDK/arm-vita-eabi/lib/libm.a"
  qemu_run "$W/qemu/t1_qemu_libm.elf" "$W/qemu/run-libm.txt" || true
  print "== qemu-newlib-trig (Vita build with newlib sinf/cosf, emulated Cortex-A9; expect differences: risk probe)" | tee -a "$rep"
  grep -E "^QEMU A .*MISMATCH|^QEMU A SUMMARY" "$W/qemu/run-libm.txt" | sed 's/^QEMU A /T1 /' | cut -c1-150 | tee -a "$rep" || true
}

# Stage 1: an engine run's trace (SHEPHERD_PHYS_TRACE) replayed on the Mac (t1_runner replay) and on QEMU's Cortex-A9 against the Vita
# archive (the harness's replay mode: replay.sptr in QEMU's working directory, read through semihosting) -- the Mac = Vita check of a
# real game drive (plan section 1.10). Needs `build.sh vita` (the archive) first; the trace stays owner-local (game-derived poses).
qemu_replay() {   # <trace.sptr>
  local tr="${1:A}" d="$W/qemu-replay"
  [[ -f "$tr" ]] || { print "qemu-replay: no trace $tr"; exit 2; }
  qemu_link "$W/qemu/t1_qemu.elf" "$W/vita/libshepphys_vita.a"
  rm -rf "$d"; mkdir -p "$d"; ln -s "$tr" "$d/replay.sptr"
  local t0=$EPOCHREALTIME rc=0
  build/mac/t1_runner replay "$tr" | tee "$OUT/replay-mac.txt"
  (cd "$d" && qemu_run "$W/qemu/t1_qemu.elf" "$d/run.txt") || rc=$?
  grep "QEMU REPLAY" "$d/run.txt" | tee "$OUT/replay-qemu.txt"
  print "qemu-replay $(secs $t0) s, exit $rc"
  grep -q "^REPLAY OK" "$OUT/replay-mac.txt" && grep -q "QEMU REPLAY OK" "$OUT/replay-qemu.txt" || exit 1
}

what=${1:-all}
case "$what" in
  mac) mac ;; vita) vita ;; qemu) qemu ;; verify) verify ;; golden) golden ;; san) san ;; prof) prof ;;
  qemu-replay) qemu_replay "${2:-}" ;;
  all) mac; vita; qemu; verify; san ;;
  *) print "usage: $0 [mac|vita|qemu|verify|golden|san|prof|all|qemu-replay <trace.sptr>]"; exit 2 ;;
esac
