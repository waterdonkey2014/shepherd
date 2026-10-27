#!/bin/zsh
# SHEPHERD-VITA device kit #5 (track runtime, vita/notes/kit5-runtime.md): builds the PS Vita runtime WITH the kit #5 runtime patches into
# its OWN directory -- nothing shared is written (the fork checkout and its normal builds are only read).
#   build-runtime-k5.sh base <out>   the fork ($B/runtime) + the patches -> <out>/obj   (for every app but the game-AOT ones)
#   build-runtime-k5.sh fix  <out>   the gameaot fork with fix A ($B/gameaot/runtime-fix) + the patches -> <out>/obj  (SHPD00095 / 00190)
#   build-runtime-k5.sh llvm <out>   the fork ($B/runtime) + the patches + vita/llvm/fork-eh-vfp.patch (fixes B + C, kit #5 llvm review) ->
#                                    <out>/obj  (the LLVM A/B apps SHPD00191 / 00192: lever 20 AND the EH-VFP runtime they need; it writes
#                                    <out>/obj/EH-VFP-RUNTIME.txt, which vita/shepherd/build.sh reads to accept it as an EH-VFP runtime)
#   build-runtime-k5.sh plain-base|plain-fix <out>   the same WITHOUT the patches (the reproducibility check: = the shared build)
# Kit #5 integration (vita/notes/kit5-integration.md): vita/shepherd/build.sh links <SHEPHERD_K5_RUNTIMES>/{base,fix,llvm}/obj by default
# (default dir ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit5/runtimes): build-runtime-k5.sh base|fix|llvm ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit5/runtimes/<kind>.
# Then: SHEPHERD_VITA_RUNTIME_OBJ=<out>/obj vita/shepherd/build.sh ...  (vita/vlx/link.sh relinks the same runtime: build.sh records it).
# How: an APFS clone of the fork's eng/, src/native/, src/mono/ and its two version files (vita/toolchain/version-files, as setup-fork.sh
# puts them in artifacts/obj/), the patches applied (patch -p1), then the normal configure + build (configure-vita.sh: the same flags,
# the same -ffile-prefix-map, so the objects do not depend on where the clone lives), then every archive member compared with the shared
# normal build's: <out>/K5-RUNTIME.txt lists the members whose code differs (expected: interp.c.obj only; llvm: + exceptions-arm.c.obj,
# unwind.c.obj) and the ones whose debug info alone differs (the TUs that include interp-internals.h: the union's DWARF).
# GUARD (kit #5 integration, review low): the build FAILS (exit 1, no K5-RUNTIME.txt, the archive renamed *.REJECTED) unless the members
# whose code differs are exactly the expected ones (a plain-* build: none) and the 4 component archives are byte-identical to the normal
# build's. Debug-info-only differences are listed, not refused (the eboot carries no debug info; the shared base build's
# method-to-ir.c.obj differs from every rebuild in its DWARF alone).
# Patches (in order): vita/patches/k5-interp-cicache.patch (lever 20: the AOT -> interpreter call-info cache; SHEPHERD_INTERP_CICACHE);
# kind llvm also vita/llvm/fork-eh-vfp.patch.
# env: SHEPHERD_RUNTIME_BASE (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime), SHEPHERD_RUNTIME_FIX (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/runtime-fix),
#      and their normal builds SHEPHERD_VITA_RUNTIME_OBJ_BASE / _FIX (default $B/obj/vita-runtime, $B/gameaot/obj/vita-runtime-fix)
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
B=${SHEPHERD_WORK:-$HOME/shepherd-work}
KIND=${1:?usage: build-runtime-k5.sh base|fix|llvm|plain-base|plain-fix <out dir>}; OUT=${2:?usage: build-runtime-k5.sh base|fix|llvm|plain-base|plain-fix <out dir>}
case $KIND in
  base|plain-base) R=${SHEPHERD_RUNTIME_BASE:-$B/runtime}; RT=${SHEPHERD_VITA_RUNTIME_OBJ_BASE:-$B/obj/vita-runtime} ;;
  fix|plain-fix)   R=${SHEPHERD_RUNTIME_FIX:-$B/gameaot/runtime-fix}; RT=${SHEPHERD_VITA_RUNTIME_OBJ_FIX:-$B/gameaot/obj/vita-runtime-fix} ;;
  llvm)            R=${SHEPHERD_RUNTIME_BASE:-$B/runtime}; RT=${SHEPHERD_VITA_RUNTIME_OBJ_BASE:-$B/obj/vita-runtime} ;;
  *) echo "usage: build-runtime-k5.sh base|fix|llvm|plain-base|plain-fix <out dir>"; exit 2 ;;
esac
PATCHES=($V/patches/k5-interp-cicache.patch); EXPECT="interp.c.obj"
[[ $KIND == llvm ]] && { PATCHES+=($V/llvm/fork-eh-vfp.patch); EXPECT="exceptions-arm.c.obj interp.c.obj unwind.c.obj"; }
[[ $KIND == plain-* ]] && { PATCHES=(); EXPECT=""; }
mkdir -p "$OUT"; OUT=${OUT:a}
for p in "${R:a}" "${RT:a}" "$B/obj" "$B/runtime" "$B/gameaot"; do
  case $OUT/ in "$p"/*) echo "refusing to write into a shared tree ($p): $OUT"; exit 1 ;; esac
done
[ -f "$RT/mono/mini/libmonosgen-2.0.a" ] || { echo "no normal runtime build at $RT"; exit 1; }
[ -f "$R/artifacts/obj/_version.c" ] || { echo "$R has no artifacts/obj/_version.c (vita/toolchain/setup-fork.sh)"; exit 1; }
S=$OUT/src; rm -rf "$S"; mkdir -p "$S/src" "$S/artifacts/obj"
cp -cR "$R/eng" "$S/eng"; cp -cR "$R/src/native" "$S/src/native"; cp -cR "$R/src/mono" "$S/src/mono"
cp "$R/artifacts/obj/_version.c" "$R/artifacts/obj/runtime_version.h" "$S/artifacts/obj/"
for p in $PATCHES; do (cd "$S" && patch -s -p1 < "$p") || { echo "$p does not apply to $R"; exit 1; }; done
s=$(date +%s)
SHEPHERD_RUNTIME=$S SHEPHERD_VITA_RUNTIME_OBJ=$OUT/obj nice -n 10 sh "$HERE/build-runtime-vita.sh" --reconfigure > "$OUT/build-runtime.log" 2>&1 \
  || { tail -20 "$OUT/build-runtime.log"; exit 1; }
rm -f "$OUT/K5-RUNTIME.txt" "$OUT/obj/EH-VFP-RUNTIME.txt"
set +e
python3 - "$OUT/obj/mono/mini" "$RT/mono/mini" "$EXPECT" "$KIND" > "$OUT/members.txt" <<'EOF'
import sys, os, subprocess, tempfile, hashlib
new, ref, expect, kind = sys.argv[1:5]
expect = set(expect.split())
bad = []
def h(b): return hashlib.sha256(b).hexdigest()
def members(a):
    d = tempfile.mkdtemp(dir=os.environ.get('TMPDIR'))
    subprocess.check_call(['arm-vita-eabi-ar', 'x', a], cwd=d)
    return d, subprocess.check_output(['arm-vita-eabi-ar', 't', a], text=True).split()
def stripped(p):
    t = p + '.nodebug'; subprocess.check_call(['arm-vita-eabi-objcopy', '--strip-debug', p, t]); return h(open(t, 'rb').read())
for lib in sorted(f for f in os.listdir(ref) if f.endswith('.a')):
    dn, mn = members(os.path.join(new, lib)); dr, mr = members(os.path.join(ref, lib))
    same, dbg, diff = 0, [], []
    for m in sorted(set(mn) | set(mr)):
        a, b = os.path.join(dn, m), os.path.join(dr, m)
        if not (os.path.exists(a) and os.path.exists(b)): diff.append(m + ' (only in one)'); continue
        if h(open(a, 'rb').read()) == h(open(b, 'rb').read()): same += 1
        elif stripped(a) == stripped(b): dbg.append(m)
        else: diff.append(m)
    print(f'{lib}: {len(mn)} members; identical {same}; debug info only {len(dbg)} {" ".join(dbg)}; CODE DIFFERS {len(diff)} {" ".join(diff)}')
    if lib == 'libmonosgen-2.0.a':
        if set(diff) != expect: bad.append(f'{lib}: code differs in {sorted(diff)}, expected exactly {sorted(expect)}')
    elif diff or dbg: bad.append(f'{lib}: differs from the normal build ({diff + dbg})')
if bad:
    print('GUARD FAILED: ' + '; '.join(bad)); sys.exit(1)
print(f'GUARD PASSED: code differs exactly in {sorted(expect) or "nothing"}; the component archives are the normal build\'s')
EOF
grc=$?
set -e
if [ $grc != 0 ] || ! grep -q '^GUARD PASSED' "$OUT/members.txt"; then
  cat "$OUT/members.txt"; mv "$OUT/obj/mono/mini/libmonosgen-2.0.a" "$OUT/obj/mono/mini/libmonosgen-2.0.a.REJECTED"
  echo "build-runtime-k5.sh $KIND: the member guard FAILED -- the archive is renamed *.REJECTED (build.sh will not link it)"; exit 1
fi
{
  echo "kit #5 Vita runtime ($KIND) built $(date '+%Y-%m-%d %H:%M:%S') in $(( $(date +%s) - s )) s"
  echo "fork: $R ($(git -C "$R" rev-parse --short HEAD) + its working-tree patch); compared with its normal build $RT"
  for p in $PATCHES; do echo "patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
  [[ ${#PATCHES} == 0 ]] && echo "patch: none (the reproducibility check)"
  cat "$OUT/members.txt"
  (cd "$OUT/obj" && shasum -a 256 mono/mini/*.a)
} > "$OUT/K5-RUNTIME.txt"
if [[ $KIND == llvm ]]; then
  # build.sh accepts an LLVM app's runtime as EH-VFP (fixes B + C) only with this file naming the archive's sha256 (as build-runtime-eh-vfp.sh writes it)
  { echo "EH-VFP Vita runtime (vita/llvm/fork-eh-vfp.patch fixes B + C) WITH the kit #5 lever-20 call-info cache: vita/toolchain/build-runtime-k5.sh llvm, built $(date '+%Y-%m-%d %H:%M:%S')"
    echo "fork: $R ($(git -C "$R" rev-parse --short HEAD) + its working-tree patch); normal build compared: $RT"
    for p in $PATCHES; do echo "patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
    echo "members whose code differs from the normal build: $EXPECT (guard passed)"
    echo "libmonosgen-2.0.a sha256 $(shasum -a 256 "$OUT/obj/mono/mini/libmonosgen-2.0.a" | cut -d' ' -f1)"
  } > "$OUT/obj/EH-VFP-RUNTIME.txt"
  cat "$OUT/obj/EH-VFP-RUNTIME.txt"
fi
cat "$OUT/K5-RUNTIME.txt"
rm -rf "$S"   # the clone (the build keeps its objects; the patch is in the repo)
