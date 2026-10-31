#!/bin/zsh
# SHEPHERD-VITA device kit #9 (track runtime, vita/notes/kit9-runtime.md; kit #9 integration, vita/notes/kit9-integration.md: ONE runtime
# per kind with every kit #9 track's runtime patches -- runtime + gc, and for the JIT app jit too): builds the kit #9 PS Vita runtimes --
# kit #8's runtimes PLUS the kit #9 runtime patches (the K9 list below) -- each into its OWN directory; nothing shared is written (the
# forks and every other runtime are only read).
#   build-runtime-k9.sh llvm <out>    kit #5's LLVM runtime ($K5RT/llvm: the fork + k5-interp-cicache + vita/llvm/fork-eh-vfp) + K9
#                                     -> the LLVM apps (00099 / 00011 / 00098 / 00193); writes <out>/obj/EH-VFP-RUNTIME.txt (build.sh's gate)
#   build-runtime-k9.sh fix <out>     kit #5's fix runtime ($K5RT/fix: the gameaot fork with fix A + k5-interp-cicache) + K9 -> 00095 / 00194
#   build-runtime-k9.sh jit <out>     kit #8's JIT runtime (llvm + vita/patches/k8-jit-ops + k8-baseline-jit, the stencils regenerated and
#                                     checked as build-runtime-k8.sh does) + K9 -> the JIT app 00097; writes K8-RUNTIME.txt + EH-VFP-RUNTIME.txt
#   build-runtime-k9.sh base <out>    kit #5's base runtime ($K5RT/base: the fork + k5-interp-cicache) + K9 (no fix A: no trimmed CoreLib)
#                                     -> SHEPHERD_AOT_LLVM=0 non-owner builds only (the gc track's kind)
#   build-runtime-k9.sh plain-llvm|plain-fix|plain-jit <out>   the same WITHOUT the K9 patches: the reproducibility check (code = the
#                                     reference runtime's in every member)
# K9 patches (in order): vita/patches/k9-fast-tls.patch (R1: the runtime's thread-local keys mirrored in the Vita's TLS slots;
# SHEPHERD_FAST_TLS), vita/patches/k9-e2-divmod.patch (E2: AOT code's 64-bit division / remainder of 32-bit-sized operands done in 32 bits;
# SHEPHERD_K9_DIVMOD), vita/patches/k9-divfast-flag.patch (review fix: E2 divfast's RUN-TIME switch word, SHEPHERD_K9_DIVFAST /
# SHEPHERD_K9_DIVMOD, registered as a JIT icall data address -- the same patch is in the kit #9 compilers, so both agree on its id; it
# changes metadata/jit-icall-reg.h, which nearly every member includes, so the member guard below accepts nearly every member for this
# patch: vita/notes/kit9-runtime.md "Review fixes" compares the new runtimes with the previous ones member by member), and for the kinds llvm / jit
# vita/patches/k9-partial-unbox.patch (the gameaot fork's fix A -- already in the fix runtime's source -- with a fallback cache and
# counters: a value-type method a trimmed image did not compile enters the interpreter through the arbitrary unbox trampoline instead of
# another method's; needed by SHEPHERD_K9_CORELIB_KEEP on the LLVM apps; unreachable with complete images), for the kind fix
# vita/patches/k9-unbox-cache-fix.patch (review fix: the same cache and counters over the fork's own fix A); then, for EVERY kind
# (kit #9 integration), the gc track's vita/patches/k9-gc.patch (lever 32's worker hook, the per-scene nursery target, the used-size modes,
# M8's two-ended arena -- every default in it is kit #8's behaviour; the host's env-file GC switches turn them on: vita/shepherd/k9_gc.inc.c);
# and for the kind jit LAST the JIT patches (the jit track's rule, vita/notes/kit9-jit.md section 9: another kit #9 track's runtime patch
# goes BEFORE all JIT patches): kit #8's k8-jit-ops + k8-baseline-jit, then the jit track's vita/patches/k9-jit.patch (JIT round 2:
# records + traps, calls into AOT code, getter intrinsics opt-in, counter decay; SHEPHERD_JIT9=0 = kit #8's tier). No runtime / gc patch
# touches interp.c / transform.c / mintops / shepjit, so the ops patch needs no regeneration (make-ops.py: kit #8's rule).
# SHEPHERD_K9_PATCHES="<file> ..." overrides the list (a subset; measurement builds).
# How: as build-runtime-k5.sh / -k8.sh (an APFS clone of the fork's eng/, src/native/, src/mono/ and its two version files, the patches
# applied with patch -p1, configure-vita.sh's flags), then every archive member compared with the reference runtime.
# GUARD: the build FAILS (exit 1, the archive renamed *.REJECTED, no K9-RUNTIME.txt) unless (1) the 4 component archives equal the
# reference's in code (their debug info may differ: the clone lives behind the kit #9 link, so DWARF paths differ; the eboot has no DWARF), (2) no member is added or lost, and (3) every libmonosgen member whose CODE differs from the reference's was compiled
# from a source that depends on a file the K9 patches change (ninja's own dependency records: `ninja -t deps`) -- a member whose inputs the
# patches never touched must come out identical. A plain-* build must differ in no member's code. Debug-info-only differences are listed.
# env: SHEPHERD_RUNTIME_BASE (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime), SHEPHERD_RUNTIME_FIX (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/runtime-fix),
#      SHEPHERD_K5_RUNTIMES (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit5/runtimes), SHEPHERD_K8_JIT_RUNTIME (the reference of kind jit, default
#      kit #8's kit build: ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit8/integ-work/kit-build/runtimes/llvm-jit)
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
B=${SHEPHERD_WORK:-$HOME/shepherd-work}
KIND=${1:?usage: build-runtime-k9.sh llvm|fix|jit|base|plain-llvm|plain-fix|plain-jit <out dir>}; OUT=${2:?usage: build-runtime-k9.sh <kind> <out dir>}
K5RT=${SHEPHERD_K5_RUNTIMES:-$B/kit5/runtimes}
K8JIT=${SHEPHERD_K8_JIT_RUNTIME:-$B/kit8/integ-work/kit-build/runtimes/llvm-jit}
# (kit #9 integration review fix) only an explicit SHEPHERD_K9_PATCHES is word-split; the default list is a plain array, so a checkout reached by
# a path with spaces (${0:a:h} keeps the "${SHEPHERD_DATA:-$HOME}/..." spelling when the script is called through it) no longer splits $V
if [[ -n ${SHEPHERD_K9_PATCHES:-} ]]; then K9=(${=SHEPHERD_K9_PATCHES})
else K9=("$V/patches/k9-fast-tls.patch" "$V/patches/k9-e2-divmod.patch" "$V/patches/k9-divfast-flag.patch"); fi
JITP=()   # (kit #9 integration) kit #8's JIT patches of the kind jit, applied after BASEP + K9 (its reference, kit #8's JIT runtime, has them)
case $KIND in
  llvm|plain-llvm) R=${SHEPHERD_RUNTIME_BASE:-$B/runtime}; REF=$K5RT/llvm; BASEP=($V/patches/k5-interp-cicache.patch $V/llvm/fork-eh-vfp.patch) ;;
  fix|plain-fix)   R=${SHEPHERD_RUNTIME_FIX:-$B/gameaot/runtime-fix}; REF=$K5RT/fix; BASEP=($V/patches/k5-interp-cicache.patch) ;;
  base)            R=${SHEPHERD_RUNTIME_BASE:-$B/runtime}; REF=$K5RT/base; BASEP=($V/patches/k5-interp-cicache.patch) ;;
  jit|plain-jit)   R=${SHEPHERD_RUNTIME_BASE:-$B/runtime}; REF=$K8JIT
                   BASEP=($V/patches/k5-interp-cicache.patch $V/llvm/fork-eh-vfp.patch); JITP=($V/patches/k8-jit-ops.patch $V/patches/k8-baseline-jit.patch) ;;
  *) echo "usage: build-runtime-k9.sh llvm|fix|jit|base|plain-llvm|plain-fix|plain-jit <out dir>"; exit 2 ;;
esac
[[ -z ${SHEPHERD_K9_PATCHES:-} && ( $KIND == llvm || $KIND == jit ) ]] && K9+=($V/patches/k9-partial-unbox.patch)
[[ -z ${SHEPHERD_K9_PATCHES:-} && $KIND == fix ]] && K9+=($V/patches/k9-unbox-cache-fix.patch)   # (review fix: the fork's fix A cached)
[[ -z ${SHEPHERD_K9_PATCHES:-} ]] && K9+=($V/patches/k9-gc.patch)   # (kit #9 integration: the gc track's sgen patch, every kind)
# (kit #10 memory, vita/notes/kit10-memory.md) SHEPHERD_K10_PATCHES="<file>[:<file>...]" -- kit #10 runtime patches applied after the K9 list
# (after k9-gc.patch, before every JIT patch; ':'-separated, so a spaced path stays one path), e.g. vita/patches/k10-memory.patch (the shared
# pool + the size-class table + run-time GC limits). Unset = kit #9's runtime exactly; the member guard counts their files as patched.
[[ -n ${SHEPHERD_K10_PATCHES:-} && $KIND != plain-* ]] && K9+=("${(@s.:.)SHEPHERD_K10_PATCHES}")
K9J=(); [[ -z ${SHEPHERD_K9_PATCHES:-} && $KIND == jit && -f $V/patches/k9-jit.patch ]] && K9J=($V/patches/k9-jit.patch)   # (kit #9 integration: JIT round 2)
[[ $KIND == plain-* ]] && { K9=(); K9J=(); }
RT=$REF/obj
mkdir -p "$OUT"; OUT=${OUT:a}
for p in "${R:a}" "${K5RT:a}" "${K8JIT:a}" "$B/obj" "$B/runtime" "$B/gameaot" "$B/kit5" "$B/kit6" "$B/kit7" "$B/kit8"; do
  case $OUT/ in "$p"/*) echo "refusing to write into a shared tree ($p): $OUT"; exit 1 ;; esac
done
[ -f "$RT/mono/mini/libmonosgen-2.0.a" ] || { echo "no reference runtime at $RT"; exit 1; }
[ -f "$R/artifacts/obj/_version.c" ] || { echo "$R has no artifacts/obj/_version.c (vita/toolchain/setup-fork.sh)"; exit 1; }
S=$OUT/src; rm -rf "$S"; mkdir -p "$S/src" "$S/artifacts/obj"
cp -cR "$R/eng" "$S/eng"; cp -cR "$R/src/native" "$S/src/native"; cp -cR "$R/src/mono" "$S/src/mono"
cp "$R/artifacts/obj/_version.c" "$R/artifacts/obj/runtime_version.h" "$S/artifacts/obj/"
for p in $BASEP $K9 $JITP $K9J; do (cd "$S" && patch -s -p1 < "$p") || { echo "$p does not apply to $R"; exit 1; }; done
# the files the K9 patches change (paths as ninja records them: absolute, inside the clone); the kind jit's reference already has kit #8's
# JIT patches, so only k9-jit.patch's files join the list
# (review fix) one path per line, split on newlines only: a clone path with spaces stays one path
TOUCHED=$(for p in $K9 $K9J; do grep -E '^\+\+\+ b/' "$p" | sed -E 's#^\+\+\+ b/##; s#[[:space:]].*##'; done | sort -u | while IFS= read -r f; do print -r -- "$S/$f"; done)
s=$(date +%s)
# configure, then build from INSIDE the obj dir by its given path (the gc track's comp_dir fix; the jit track's build-runtime-vita.sh does the
# same with its own cd): under ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit9 -- a symlink to the WD drive -- the compiler's DW_AT_comp_dir is getcwd(), the
# resolved "${SHEPHERD_DATA:-$HOME}/..." path that configure-vita.sh's -ffile-prefix-map does not cover, unless $PWD names the same directory
SHEPHERD_RUNTIME=$S SHEPHERD_VITA_RUNTIME_OBJ=$OUT/obj nice -n 10 sh "$HERE/configure-vita.sh" > "$OUT/build-runtime.log" 2>&1 \
  || { tail -20 "$OUT/build-runtime.log"; exit 1; }
(cd "$OUT/obj" && export PWD="$OUT/obj" && SHEPHERD_RUNTIME=$S SHEPHERD_VITA_RUNTIME_OBJ=$OUT/obj nice -n 10 sh "$HERE/build-runtime-vita.sh") >> "$OUT/build-runtime.log" 2>&1 \
  || { tail -20 "$OUT/build-runtime.log"; exit 1; }
rm -f "$OUT/K9-RUNTIME.txt" "$OUT/K8-RUNTIME.txt" "$OUT/obj/EH-VFP-RUNTIME.txt"
STENCILS=""
if [[ $KIND == jit || $KIND == plain-jit ]]; then
  INC=$S/src/mono/mono/mini/interp/shepjit-stencils.inc
  zsh "$V/jit/stencils/build-stencils.sh" "$OUT/obj" "$S" "$OUT/stencils/shepjit-stencils.inc" "$OUT/stencils" > "$OUT/stencils.log" 2>&1 \
    || { tail -5 "$OUT/stencils.log"; mv "$OUT/obj/mono/mini/libmonosgen-2.0.a" "$OUT/obj/mono/mini/libmonosgen-2.0.a.REJECTED"; echo "the stencils did not regenerate"; exit 1; }
  cmp -s "$INC" "$OUT/stencils/shepjit-stencils.inc" || { mv "$OUT/obj/mono/mini/libmonosgen-2.0.a" "$OUT/obj/mono/mini/libmonosgen-2.0.a.REJECTED"; echo "the regenerated stencil table differs from the patch's shepjit-stencils.inc"; exit 1; }
  STENCILS="stencils: $(grep '^gen-stencils.py' "$OUT/stencils.log") -- regenerated = the patch's shepjit-stencils.inc (sha256 $(shasum -a 256 "$INC" | cut -c1-16))"
fi
set +e
python3 - "$OUT/obj/mono/mini" "$RT/mono/mini" "$OUT/obj" "$KIND" "$TOUCHED" > "$OUT/members.txt" <<'EOF'
import sys, os, subprocess, tempfile, hashlib
new, ref, objdir, kind, touched = sys.argv[1:6]
touched = set(t for t in touched.split('\n') if t)
bad = []
def h(b): return hashlib.sha256(b).hexdigest()
def members(a):
    d = tempfile.mkdtemp(dir=os.environ.get('TMPDIR'))
    subprocess.check_call(['arm-vita-eabi-ar', 'x', a], cwd=d)
    return d, subprocess.check_output(['arm-vita-eabi-ar', 't', a], text=True).split()
def stripped(p):
    t = p + '.nodebug'; subprocess.check_call(['arm-vita-eabi-objcopy', '--strip-debug', p, t]); return h(open(t, 'rb').read())
# member name -> the object path ninja knows (mono/mini/CMakeFiles/.../<member>), then its recorded dependencies
deps_out = subprocess.run([os.environ.get('NINJA', 'ninja'), '-C', objdir, '-t', 'deps'], capture_output=True, text=True).stdout
deps, cur = {}, None
for line in deps_out.splitlines():
    if line and not line.startswith(' '):
        cur = line.split(':')[0]; deps[cur] = set()
    elif cur and line.strip():
        deps[cur].add(os.path.normpath(line.strip()))
def depends_on_touched(member):
    for o, ds in deps.items():
        if os.path.basename(o) == member and ds & touched: return True
    return False
for lib in sorted(f for f in os.listdir(ref) if f.endswith('.a')):
    dn, mn = members(os.path.join(new, lib)); dr, mr = members(os.path.join(ref, lib))
    same, dbg, diff, added, lost = 0, [], [], [], []
    for m in sorted(set(mn) | set(mr)):
        a, b = os.path.join(dn, m), os.path.join(dr, m)
        if os.path.exists(a) and not os.path.exists(b): added.append(m); continue
        if not os.path.exists(a): lost.append(m); continue
        if h(open(a, 'rb').read()) == h(open(b, 'rb').read()): same += 1
        elif stripped(a) == stripped(b): dbg.append(m)
        else: diff.append(m)
    print(f'{lib}: {len(mn)} members; identical {same}; debug info only {len(dbg)} {" ".join(dbg)}; CODE DIFFERS {len(diff)} {" ".join(diff)}; NEW {len(added)} {" ".join(added)}; LOST {len(lost)} {" ".join(lost)}')
    if added or lost: bad.append(f'{lib}: members added {added} / lost {lost}')
    if lib != 'libmonosgen-2.0.a':
        if diff: bad.append(f'{lib}: a component archive differs in code from the reference ({diff})')
        continue
    if kind.startswith('plain-'):
        if diff: bad.append(f'{lib}: the plain build differs in code from the reference: {diff}')
    else:
        stray = [m for m in diff if not depends_on_touched(m)]
        if stray: bad.append(f'{lib}: code differs in members whose sources the K9 patches never touched: {stray}')
        print(f'{lib}: every code-differing member depends on a K9-patched file ({len(diff)}): ' + ('yes' if not stray else 'NO'))
if bad:
    print('GUARD FAILED: ' + '; '.join(bad)); sys.exit(1)
print('GUARD PASSED: ' + ('no member differs in code from the reference (plain build)' if kind.startswith('plain-') else 'only members compiled from K9-patched sources differ in code; the component archives are the reference\'s in code; no member added or lost'))
EOF
grc=$?
set -e
if [ $grc != 0 ] || ! grep -q '^GUARD PASSED' "$OUT/members.txt"; then
  cat "$OUT/members.txt"; mv "$OUT/obj/mono/mini/libmonosgen-2.0.a" "$OUT/obj/mono/mini/libmonosgen-2.0.a.REJECTED"
  echo "build-runtime-k9.sh $KIND: the member guard FAILED -- the archive is renamed *.REJECTED (build.sh will not link it)"; exit 1
fi
{
  echo "kit #9 Vita runtime ($KIND) built $(date '+%Y-%m-%d %H:%M:%S') in $(( $(date +%s) - s )) s"
  echo "fork: $R ($(git -C "$R" rev-parse --short HEAD) + its working-tree patch); compared with the reference runtime $RT"
  for p in $BASEP $JITP; do echo "base patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
  for p in $K9 $K9J; do echo "K9 patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
  # (kit #9 integration) the whole list again in the gc / jit tracks' form, for build.sh's build-info greps (^patch: k9-gc, ^patch: k[89]-)
  for p in $BASEP $K9 $JITP $K9J; do echo "patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
  [[ -n $STENCILS ]] && echo "$STENCILS"
  cat "$OUT/members.txt"
  (cd "$OUT/obj" && shasum -a 256 mono/mini/*.a)
} > "$OUT/K9-RUNTIME.txt"
# build.sh's runtime gates: an LLVM app's runtime needs EH-VFP-RUNTIME.txt naming the archive (kinds llvm / jit carry fixes B + C);
# a JIT app's runtime needs K8-RUNTIME.txt next to obj/ (build.sh SHEPHERD_JIT_APP=1)
if [[ $KIND == llvm || $KIND == jit || $KIND == plain-llvm || $KIND == plain-jit ]]; then
  { echo "EH-VFP Vita runtime (vita/llvm/fork-eh-vfp.patch fixes B + C) WITH the kit #5 lever-20 call-info cache, kit #8's patches of kind $KIND and the kit #9 runtime patches: vita/toolchain/build-runtime-k9.sh $KIND, built $(date '+%Y-%m-%d %H:%M:%S')"
    for p in $BASEP $K9 $JITP $K9J; do echo "patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
    echo "libmonosgen-2.0.a sha256 $(shasum -a 256 "$OUT/obj/mono/mini/libmonosgen-2.0.a" | cut -d' ' -f1)"
  } > "$OUT/obj/EH-VFP-RUNTIME.txt"
fi
[[ $KIND == jit || $KIND == plain-jit ]] && cp "$OUT/K9-RUNTIME.txt" "$OUT/K8-RUNTIME.txt"
# build.sh's kit #5 runtime gate (SHEPHERD_K5_RUNTIMES=<dir with llvm/ fix/>) wants K5-RUNTIME.txt next to obj/
[[ $KIND == llvm || $KIND == fix || $KIND == base || $KIND == plain-llvm || $KIND == plain-fix ]] && cp "$OUT/K9-RUNTIME.txt" "$OUT/K5-RUNTIME.txt"
cat "$OUT/K9-RUNTIME.txt"
[[ ${SHEPHERD_K9_KEEP_SRC:-0} == 1 ]] || rm -rf "$S"
