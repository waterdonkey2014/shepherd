#!/bin/zsh
# SHEPHERD-VITA device kit #8 (track jit, vita/notes/kit8-jit.md): builds the PS Vita runtime of the JIT app(s) -- kit #5's LLVM runtime
# (the fork + vita/patches/k5-interp-cicache.patch + vita/llvm/fork-eh-vfp.patch, = $K5RT/llvm) PLUS the kit #8 baseline JIT tier
# (vita/patches/k8-baseline-jit.patch) -- into its OWN directory; nothing shared is written (the fork checkout and every other runtime are
# only read).
#   build-runtime-k8.sh jit <out>      the JIT app's runtime -> <out>/obj (vita/shepherd/build.sh SHEPHERD_JIT_APP=1 links it; its
#                                      SHEPHERD_JIT env switch = 0 runs the kit #7 interpreter + AOT path): + vita/patches/k8-jit-ops.patch
#                                      (the one definition: the compiled opcodes' bodies moved to shepjit-ops.h) + k8-baseline-jit.patch
#                                      + (kit #9, vita/notes/kit9-jit.md) vita/patches/k9-jit.patch -- JIT round 2: records + traps, calls
#                                      into AOT code, getter intrinsics, counter decay, the count mode; its env switch SHEPHERD_JIT9=0 = kit
#                                      #8's tier (the same compile, entry points, cache and reports)
#   build-runtime-k8.sh jit8 <out>     kit #8's JIT runtime exactly (the two k8 patches, no k9-jit.patch): the reference / byte-exact way back
#   build-runtime-k8.sh ops <out>      kit #5's LLVM runtime + k8-jit-ops.patch ONLY: the gate of the one definition -- the interpreter's
#                                      object code must be kit #5's byte for byte (interp.c.obj: debug info only)
#   build-runtime-k8.sh census <out>   kit #5's LLVM runtime + vita/jit/census/opcensus.patch (the opcode census: a measurement build,
#                                      never shipped: every dispatched interpreter opcode counted, written to opcensus.tsv)
#   build-runtime-k8.sh jit-census <out>  the JIT runtime + the opcode census (a measurement build, never shipped: how many opcodes the
#                                      interpreter still dispatches with the tier on -- the ones the traces ran are not dispatched)
#   build-runtime-k8.sh plain <out>    kit #5's LLVM runtime rebuilt with no kit #8 patch (the reproducibility check: = $K5RT/llvm)
# How: as build-runtime-k5.sh (an APFS clone of the fork's eng/, src/native/, src/mono/ and its two version files, the patches applied
# with patch -p1, configure-vita.sh's flags), then every archive member compared with kit #5's LLVM runtime ($K5RT/llvm/obj, the runtime
# kit #7's LLVM apps link): <out>/K8-RUNTIME.txt lists the members whose code differs.
# GUARD (as kit #5's): the build FAILS (exit 1, no K8-RUNTIME.txt, the archive renamed *.REJECTED) unless the members whose code differs
# are exactly the expected ones and the 4 component archives are byte-identical to kit #5's. Debug-info-only differences are listed.
#   jit:    code differs in interp.c.obj, transform.c.obj, mintops.c.obj (the three SJ opcodes after the last executed one) + the NEW
#           member shepjit.c.obj (the tier); and the stencil table is regenerated from shepjit-stencils.c with interp.c's own compile
#           command (vita/jit/stencils/build-stencils.sh) and must equal the patch's shepjit-stencils.inc byte for byte
#   ops:    nothing (interp.c.obj differs in its debug info only: the refactor moved no instruction)
#   census: interp.c.obj
#   plain:  nothing
# The output also gets EH-VFP-RUNTIME.txt (fixes B + C are in it, as in $K5RT/llvm), which vita/shepherd/build.sh requires of an LLVM
# app's runtime.
# env: SHEPHERD_RUNTIME_BASE (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime), SHEPHERD_K5_RUNTIMES (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit5/runtimes)
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
B=${SHEPHERD_WORK:-$HOME/shepherd-work}
KIND=${1:?usage: build-runtime-k8.sh jit|jit8|ops|census|jit-census|plain <out dir>}; OUT=${2:?usage: build-runtime-k8.sh jit|jit8|ops|census|jit-census|plain <out dir>}
R=${SHEPHERD_RUNTIME_BASE:-$B/runtime}; K5RT=${SHEPHERD_K5_RUNTIMES:-$B/kit5/runtimes}; RT=$K5RT/llvm/obj
PATCHES=($V/patches/k5-interp-cicache.patch $V/llvm/fork-eh-vfp.patch)
case $KIND in
  jit)    PATCHES+=($V/patches/k8-jit-ops.patch $V/patches/k8-baseline-jit.patch $V/patches/k9-jit.patch); EXPECT="interp.c.obj mintops.c.obj transform.c.obj"; NEWM="shepjit.c.obj" ;;
  jit8)   PATCHES+=($V/patches/k8-jit-ops.patch $V/patches/k8-baseline-jit.patch); EXPECT="interp.c.obj mintops.c.obj transform.c.obj"; NEWM="shepjit.c.obj" ;;
  ops)    PATCHES+=($V/patches/k8-jit-ops.patch); EXPECT=""; NEWM="" ;;
  jit-census) PATCHES+=($V/patches/k8-jit-ops.patch $V/patches/k8-baseline-jit.patch $V/patches/k9-jit.patch $V/jit/census/opcensus.patch); EXPECT="interp.c.obj mintops.c.obj transform.c.obj"; NEWM="shepjit.c.obj" ;;
  census) PATCHES+=($V/jit/census/opcensus.patch); EXPECT="interp.c.obj"; NEWM="" ;;
  plain)  EXPECT=""; NEWM="" ;;
  *) echo "usage: build-runtime-k8.sh jit|jit8|ops|census|jit-census|plain <out dir>"; exit 2 ;;
esac
mkdir -p "$OUT"; OUT=${OUT:a}
for p in "${R:a}" "${K5RT:a}" "$B/obj" "$B/runtime" "$B/gameaot" "$B/kit5" "$B/kit6" "$B/kit7" "$B/kit8"; do
  case $OUT/ in "$p"/*) echo "refusing to write into a shared tree ($p): $OUT"; exit 1 ;; esac
done
[ -f "$RT/mono/mini/libmonosgen-2.0.a" ] && [ -f "$K5RT/llvm/K5-RUNTIME.txt" ] || { echo "no kit #5 LLVM runtime at $RT (vita/toolchain/build-runtime-k5.sh llvm $K5RT/llvm)"; exit 1; }
[ -f "$R/artifacts/obj/_version.c" ] || { echo "$R has no artifacts/obj/_version.c (vita/toolchain/setup-fork.sh)"; exit 1; }
S=$OUT/src; rm -rf "$S"; mkdir -p "$S/src" "$S/artifacts/obj"
cp -cR "$R/eng" "$S/eng"; cp -cR "$R/src/native" "$S/src/native"; cp -cR "$R/src/mono" "$S/src/mono"
cp "$R/artifacts/obj/_version.c" "$R/artifacts/obj/runtime_version.h" "$S/artifacts/obj/"
for p in $PATCHES; do (cd "$S" && patch -s -p1 < "$p") || { echo "$p does not apply to $R"; exit 1; }; done
s=$(date +%s)
SHEPHERD_RUNTIME=$S SHEPHERD_VITA_RUNTIME_OBJ=$OUT/obj nice -n 10 sh "$HERE/build-runtime-vita.sh" --reconfigure > "$OUT/build-runtime.log" 2>&1 \
  || { tail -20 "$OUT/build-runtime.log"; exit 1; }
rm -f "$OUT/K8-RUNTIME.txt" "$OUT/obj/EH-VFP-RUNTIME.txt"
STENCILS=""
if [[ $KIND == jit || $KIND == jit8 || $KIND == jit-census ]]; then
  # the stencil table must be what interp.c's own compiler and flags make of shepjit-stencils.c (no hand edits, no other compiler)
  INC=$S/src/mono/mono/mini/interp/shepjit-stencils.inc
  zsh "$V/jit/stencils/build-stencils.sh" "$OUT/obj" "$S" "$OUT/stencils/shepjit-stencils.inc" "$OUT/stencils" > "$OUT/stencils.log" 2>&1 \
    || { tail -5 "$OUT/stencils.log"; mv "$OUT/obj/mono/mini/libmonosgen-2.0.a" "$OUT/obj/mono/mini/libmonosgen-2.0.a.REJECTED"; echo "the stencils did not regenerate"; exit 1; }
  if ! cmp -s "$INC" "$OUT/stencils/shepjit-stencils.inc"; then
    mv "$OUT/obj/mono/mini/libmonosgen-2.0.a" "$OUT/obj/mono/mini/libmonosgen-2.0.a.REJECTED"
    echo "build-runtime-k8.sh jit: the regenerated stencil table differs from the patch's shepjit-stencils.inc (diff $INC $OUT/stencils/shepjit-stencils.inc) -- the archive is renamed *.REJECTED"; exit 1
  fi
  STENCILS="stencils: $(grep '^gen-stencils.py' "$OUT/stencils.log") -- regenerated = the patch's shepjit-stencils.inc (sha256 $(shasum -a 256 "$INC" | cut -c1-16))"
fi
set +e
python3 - "$OUT/obj/mono/mini" "$RT/mono/mini" "$EXPECT" "$NEWM" > "$OUT/members.txt" <<'EOF'
import sys, os, subprocess, tempfile, hashlib
new, ref, expect, newm = sys.argv[1:5]
expect = set(expect.split()); newm = set(newm.split())
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
    same, dbg, diff, added = 0, [], [], []
    for m in sorted(set(mn) | set(mr)):
        a, b = os.path.join(dn, m), os.path.join(dr, m)
        if os.path.exists(a) and not os.path.exists(b): added.append(m); continue
        if not os.path.exists(a): diff.append(m + ' (only in the reference)'); continue
        if h(open(a, 'rb').read()) == h(open(b, 'rb').read()): same += 1
        elif stripped(a) == stripped(b): dbg.append(m)
        else: diff.append(m)
    print(f'{lib}: {len(mn)} members; identical {same}; debug info only {len(dbg)} {" ".join(dbg)}; CODE DIFFERS {len(diff)} {" ".join(diff)}; NEW {len(added)} {" ".join(added)}')
    if lib == 'libmonosgen-2.0.a':
        if set(diff) != expect: bad.append(f'{lib}: code differs in {sorted(diff)}, expected exactly {sorted(expect)}')
        if set(added) != newm: bad.append(f'{lib}: new members {sorted(added)}, expected exactly {sorted(newm)}')
    elif diff or dbg or added: bad.append(f'{lib}: differs from kit #5\'s LLVM runtime ({diff + dbg + added})')
if bad:
    print('GUARD FAILED: ' + '; '.join(bad)); sys.exit(1)
print(f'GUARD PASSED: code differs exactly in {sorted(expect) or "nothing"}, new members {sorted(newm) or "none"}; the component archives are kit #5\'s')
EOF
grc=$?
set -e
if [ $grc != 0 ] || ! grep -q '^GUARD PASSED' "$OUT/members.txt"; then
  cat "$OUT/members.txt"; mv "$OUT/obj/mono/mini/libmonosgen-2.0.a" "$OUT/obj/mono/mini/libmonosgen-2.0.a.REJECTED"
  echo "build-runtime-k8.sh $KIND: the member guard FAILED -- the archive is renamed *.REJECTED (build.sh will not link it)"; exit 1
fi
{
  echo "kit #8 Vita runtime ($KIND) built $(date '+%Y-%m-%d %H:%M:%S') in $(( $(date +%s) - s )) s"
  echo "fork: $R ($(git -C "$R" rev-parse --short HEAD) + its working-tree patch); compared with kit #5's LLVM runtime $RT"
  for p in $PATCHES; do echo "patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
  [[ -n $STENCILS ]] && echo "$STENCILS"
  cat "$OUT/members.txt"
  (cd "$OUT/obj" && shasum -a 256 mono/mini/*.a)
} > "$OUT/K8-RUNTIME.txt"
# vita/shepherd/build.sh accepts an LLVM app's runtime as EH-VFP (fixes B + C) only with this file naming the archive's sha256
{ echo "EH-VFP Vita runtime (vita/llvm/fork-eh-vfp.patch fixes B + C) WITH the kit #5 lever-20 call-info cache AND kit #8's $KIND patch: vita/toolchain/build-runtime-k8.sh $KIND, built $(date '+%Y-%m-%d %H:%M:%S')"
  echo "fork: $R ($(git -C "$R" rev-parse --short HEAD) + its working-tree patch); compared with kit #5's LLVM runtime: $RT"
  for p in $PATCHES; do echo "patch: ${p:t} sha256 $(shasum -a 256 "$p" | cut -c1-16)"; done
  echo "members whose code differs from kit #5's LLVM runtime: ${EXPECT:-none}; new: ${NEWM:-none} (guard passed)"
  echo "libmonosgen-2.0.a sha256 $(shasum -a 256 "$OUT/obj/mono/mini/libmonosgen-2.0.a" | cut -d' ' -f1)"
} > "$OUT/obj/EH-VFP-RUNTIME.txt"
cat "$OUT/K8-RUNTIME.txt"
[[ ${SHEPHERD_K8_KEEP_SRC:-0} == 1 ]] || rm -rf "$S"   # the clone (the build keeps its objects; the patches are in the repo)
