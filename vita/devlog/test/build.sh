#!/bin/zsh
# Builds the Shepherd Log Send Test VPK (SHPD00014) into vita/devlog/out/shepherd_logtest.vpk (git-ignored) and checks it:
# vita-elf-create accepts the ELF, no strong undefined symbol, the imports (libraries + functions) are listed in
# out/logtest-imports.txt, param.sfo says SHPD00014, and the VPK itself: built now (old products deleted first), its
# eboot.bin = this build's, the LiveArea set inside, every PNG 8-bit indexed. CMake/Ninja from the ~/src path (the
# space in "${SHEPHERD_DATA:-$HOME}" breaks builds); the build tree is on ${SHEPHERD_WORK:-$HOME/shepherd-work}. Nothing is downloaded;
# the art is generated (make_assets.py).
set -euo pipefail
SRC="${SHEPHERD_REPO:-$HOME/src/shepherd}/vita/devlog/test"
DEVLOG="${SRC:h}"
cd "$SRC"
[[ -d ${SHEPHERD_WORK:-$HOME/shepherd-work} ]] || { echo "attach the build volume first"; exit 1; }
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
mkdir -p "$TMPDIR"
export VITASDK="${VITASDK:-$HOME/vitasdk}" PATH="$HOME/vitasdk/bin:$PATH"
W=${SHEPHERD_WORK:-$HOME/shepherd-work}/devlog/build/logtest
OUT="$DEVLOG/out"
mkdir -p "$W" "$OUT" sce_sys

# LiveArea set (icon0 + livearea/contents/{bg,startup}.png + template.xml), 8-bit indexed PNGs: generated, no downloads
[[ -f sce_sys/livearea/contents/template.xml && -f sce_sys/livearea/contents/bg.png && -f sce_sys/livearea/contents/startup.png ]] \
	|| PYTHONDONTWRITEBYTECODE=1 python3 make_assets.py
PYTHONDONTWRITEBYTECODE=1 python3 make_assets.py --check > /dev/null || { PYTHONDONTWRITEBYTECODE=1 python3 make_assets.py --check; exit 1; }

COMMIT=$(git -C "$DEVLOG" rev-parse --short HEAD 2>/dev/null || echo unknown)
# a build from uncommitted client / test sources says so (the stamp must name a commit that holds this code)
[[ -n "$(git -C "$DEVLOG" status --porcelain -- . 2>/dev/null)" ]] && COMMIT="$COMMIT-dirty"
BUILD="$(date -u +%Y%m%d-%H%M%S)-$COMMIT"
# no stale output can be packed or copied: the VPK step reuses shepherd_logtest.vpk.out when it thinks nothing changed
# (seen with the "${SHEPHERD_DATA:-$HOME}" path), so every product of the last build goes first
STAMP="$W/.build-start"; : > "$STAMP"
rm -f "$W/shepherd_logtest.vpk" "$W/shepherd_logtest.vpk.out" "$W/shepherd_logtest.vpk_param.sfo" "$W/eboot.bin" \
	"$W/eboot.bin.out" "$W/shepherd_logtest" "$W/shepherd_logtest.velf" "$OUT/shepherd_logtest.vpk" "$OUT/logtest-eboot.bin"
cmake -S . -B "$W" -G Ninja -DCMAKE_BUILD_TYPE=Release -DLOGTEST_BUILD="$BUILD" -DLOGTEST_COMMIT="$COMMIT" > "$W/cmake.log"
cmake --build "$W" > "$W/ninja.log" 2>&1 || { tail -30 "$W/ninja.log"; exit 1; }
grep -i "warning" "$W/ninja.log" | grep -v "RWX permissions" && { echo "compiler warnings (above)"; exit 1; } || true

ELF="$W/shepherd_logtest"
# checks: vita-elf-create accepts it (again, verbosely, for the import list), no strong undefined symbols
vita-elf-create -v "$ELF" "$W/check.velf" > "$W/velf.log" 2>&1 || { tail -20 "$W/velf.log"; exit 1; }
arm-vita-eabi-nm -u "$ELF" | awk '$1=="U"{print $2}' > "$W/undefined.txt"
STRONG=$(wc -l < "$W/undefined.txt" | tr -d ' ')
[[ "$STRONG" == 0 ]] || { echo "strong undefined symbols:"; cat "$W/undefined.txt"; exit 1; }
python3 - "$W/velf.log" "$OUT/logtest-imports.txt" <<'PY'
import re, sys
libs = {}
for line in open(sys.argv[1], errors="replace"):
    m = re.search(r"library_nid=0x[0-9A-Fa-f]+ \((\w+)\) target_nid=0x[0-9A-Fa-f]+ \((\w+)\)", line)
    if m: libs.setdefault(m.group(1), set()).add(m.group(2))
n = sum(len(v) for v in libs.values())
with open(sys.argv[2], "w") as f:
    f.write("imports of shepherd_logtest (vita-elf-create -v): %d functions from %d libraries\n" % (n, len(libs)))
    for lib in sorted(libs):
        f.write("%-26s %3d  %s\n" % (lib, len(libs[lib]), " ".join(sorted(libs[lib]))))
print("imports: %d functions from %d libraries: %s" % (n, len(libs), ", ".join("%s %d" % (l, len(libs[l])) for l in sorted(libs))))
bad = [f for l in libs for f in libs[l] if re.search(r"ForDriver|ForKernel|taiHook|kubridge|Exception", f + l)]
if bad: print("UNEXPECTED kernel/hook imports:", bad); sys.exit(1)
PY
# the VPK must be this build's and complete: newer than the start, its eboot.bin = the one just built, the LiveArea set
# present, every PNG 8-bit indexed (colour type 3), param.sfo inside
[[ "$W/shepherd_logtest.vpk" -nt "$STAMP" && "$W/eboot.bin" -nt "$STAMP" ]] || { echo "VPK / eboot.bin older than this build"; exit 1; }
python3 - "$W/shepherd_logtest.vpk" "$W/eboot.bin" <<'PY'
import hashlib, struct, sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
names = set(z.namelist())
need = {"eboot.bin", "sce_sys/param.sfo", "sce_sys/icon0.png", "sce_sys/livearea/contents/template.xml",
        "sce_sys/livearea/contents/bg.png", "sce_sys/livearea/contents/startup.png"}
miss = sorted(need - names)
if miss: sys.exit("VPK lacks: %s" % ", ".join(miss))
if hashlib.sha256(z.read("eboot.bin")).digest() != hashlib.sha256(open(sys.argv[2], "rb").read()).digest():
    sys.exit("VPK's eboot.bin is not the one just built (stale .vpk.out?)")
for n in sorted(x for x in names if x.lower().endswith(".png")):
    d = z.read(n)
    w, h, bd, ct = struct.unpack(">IIBB", d[16:26])
    if d[:8] != b"\x89PNG\r\n\x1a\n" or (bd, ct) != (8, 3): sys.exit("%s: bit depth %d colour type %d (need 8-bit indexed)" % (n, bd, ct))
    print("vpk png: %-40s %dx%d bit depth %d colour type %d" % (n, w, h, bd, ct))
if b"<livearea" not in z.read("sce_sys/livearea/contents/template.xml"): sys.exit("template.xml is not a livearea template")
print("vpk contents: %d entries, eboot.bin = this build, LiveArea set complete" % len(names))
PY
cp "$W/shepherd_logtest.vpk" "$OUT/shepherd_logtest.vpk"
cp "$W/eboot.bin" "$OUT/logtest-eboot.bin"
arm-vita-eabi-size "$ELF" | tail -1 | awk '{print "elf text/data/bss: "$1" / "$2" / "$3}'
echo "strong undefined symbols: $STRONG"
echo "vita-elf-create: accepted ($W/velf.log)"
unzip -l "$OUT/shepherd_logtest.vpk" | sed -n '4,$p' | grep -v -- '----' | sed '$d'
python3 - "$W/shepherd_logtest.vpk_param.sfo" <<'PY'
import struct, sys
d = open(sys.argv[1], "rb").read()
magic, ver, kt, dt, n = struct.unpack_from("<4sIIII", d, 0)
out = []
for i in range(n):
    ko, fmt, ln, mx, do = struct.unpack_from("<HHIII", d, 20 + 16 * i)
    k = d[kt + ko:d.index(b"\0", kt + ko)].decode()
    v = d[dt + do:dt + do + ln]
    out.append("%s=%s" % (k, v.rstrip(b"\0").decode(errors="replace") if fmt != 0x404 else struct.unpack("<I", v)[0]))
print("param.sfo: " + ", ".join(o for o in out if o.split("=")[0] in ("TITLE_ID", "TITLE", "APP_VER", "ATTRIBUTE2")))
if "TITLE_ID=SHPD00014" not in out: sys.exit("param.sfo: TITLE_ID is not SHPD00014")
PY
ls -la "$OUT/shepherd_logtest.vpk"
