#!/bin/zsh
# SHEPHERD-VITA DIAG (devfail1, 2026-10-07): builds the DIAGNOSTIC PS Vita runtime -- libmonosgen-2.0.a with the line-neutral hooks
# of vita/patches/shepherd-vita-diag.patch compiled in (-DSHEPHERD_VITA_DIAG) -- into its OWN object dir. Nothing shared is
# written: the fork checkout ($R) and its normal build ($RT) are only read.
#   build-runtime-diag.sh build   clone $R/src/mono, apply the patch, compile the 3 hooked TUs + mono/mini/shepherd-vita-diag.c with
#                                  the normal build's exact commands (compile_commands.json) + -DSHEPHERD_VITA_DIAG, and write
#                                  $OUT/mono/mini/libmonosgen-2.0.a = the normal archive with those members replaced/added (and the
#                                  4 component archives copied), ready for SHEPHERD_VITA_RUNTIME_OBJ=$OUT in the app build scripts
#   build-runtime-diag.sh prove   recompile EVERY runtime TU twice from clones -- the plain fork and the PATCHED fork, both WITHOUT
#                                  the flag -- and compare: full objects equal for every TU the patch does not touch, and the
#                                  sections that reach the eboot (everything but debug info) equal for the hooked ones (their
#                                  DWARF column numbers move). Also compares the plain recompile with $RT's objects (the shared
#                                  build is up to date with the fork). Report: $OUT/prove/PROOF.txt
# env: SHEPHERD_RUNTIME (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime), SHEPHERD_VITA_RUNTIME_OBJ (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime),
#      SHEPHERD_VITA_RUNTIME_DIAG_OBJ (REQUIRED: the output dir; never the normal build's)
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
RT=${SHEPHERD_VITA_RUNTIME_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime}
OUT=${SHEPHERD_VITA_RUNTIME_DIAG_OBJ:?set SHEPHERD_VITA_RUNTIME_DIAG_OBJ, e.g. ${SHEPHERD_WORK:-$HOME/shepherd-work}/devfail1/obj/vita-runtime-diag}
OUT=${OUT:a}
PATCH=$V/patches/shepherd-vita-diag.patch
MODE=${1:-build}
case $OUT in "${RT:a}"|"${RT:a}"/*|"${R:a}"|"${R:a}"/*) echo "refusing to write into the fork or its normal build: $OUT"; exit 1 ;; esac
[ -f "$RT/compile_commands.json" ] || { echo "no normal runtime build at $RT (vita/toolchain/build-runtime-vita.sh)"; exit 1; }
mkdir -p "$OUT"

# clone <name> <patched 0|1>: an APFS clone of the fork's src/mono (cheap), optionally patched; prints its root
clone () {
  local d=$OUT/src/$1
  rm -rf "$d"; mkdir -p "$d/src"
  cp -cR "$R/src/mono" "$d/src/mono" 2>/dev/null || cp -R "$R/src/mono" "$d/src/mono"
  if [ "$2" = 1 ]; then (cd "$d" && patch -s -p1 < "$PATCH") || { echo "the diag patch does not apply to $R"; exit 1; }; fi
  echo "$d"
}

# cmds <clone root> <obj out dir> <flags> <only|all> [tu ...]: the normal build's compile commands, re-pointed at the clone
# (sources and src/mono include dirs), objects into <obj out dir>, the clone's path mapped to /shepherd-runtime like the fork's
cmds () {
  python3 - "$RT" "$R" "$@" <<'EOF'
import json, shlex, sys, os
rt, r, root, objdir, flags, sel = sys.argv[1:7]
tus = sys.argv[7:]
cc = json.load(open(os.path.join(rt, 'compile_commands.json')))
mono = os.path.join(r, 'src/mono/')
want = {t: None for t in tus}
out = []
for e in cc:
    f = e['file']
    rel = f[len(mono):] if f.startswith(mono) else None
    if sel == 'only' and rel not in want:
        continue
    args = e.get('arguments') or shlex.split(e['command'])
    a2 = []
    i = 0
    while i < len(args):
        x = args[i]
        if x == '-o':
            a2 += ['-o', os.path.join(objdir, args[i + 1])]; i += 2; continue
        if x.startswith(mono) or x.startswith('-I' + mono):
            x = x.replace(mono, os.path.join(root, 'src/mono/'), 1)
        a2.append(x); i += 1
    a2 += ['-ffile-prefix-map=%s=/shepherd-runtime' % root] + (shlex.split(flags) if flags else [])
    out.append('mkdir -p %s && cd %s && %s' % (shlex.quote(os.path.dirname(os.path.join(objdir, args[args.index('-o') + 1]))), shlex.quote(e['directory']), ' '.join(shlex.quote(y) for y in a2)))
    if rel in want: want[rel] = 1
# the diag-only TU (not in the normal build): compiled like mini/exceptions-arm.c
if sel == 'only' and 'mono/mini/shepherd-vita-diag.c' in want:
    base = [l for l in out if '/mono/mini/exceptions-arm.c' in l][0]
    out.append(base.replace('exceptions-arm.c', 'shepherd-vita-diag.c'))
    want['mono/mini/shepherd-vita-diag.c'] = 1
missing = [t for t, v in want.items() if not v]
if missing: sys.exit('not in compile_commands.json: %s' % missing)
print('\n'.join(out))
EOF
}
HOOKED=(mono/metadata/exception.c mono/mini/exceptions-arm.c mono/utils/mono-threads.c)
NCPU=$(sysctl -n hw.ncpu)

case $MODE in
build)
  P=$(clone patched 1)
  O=$OUT/objs; rm -rf "$O"; mkdir -p "$O"
  cmds "$P" "$O" "-DSHEPHERD_VITA_DIAG=1" only $HOOKED mono/mini/shepherd-vita-diag.c > "$OUT/compile-diag.sh"
  zsh -e "$OUT/compile-diag.sh" > "$OUT/compile-diag.log" 2>&1 || { grep -E "error|warning" "$OUT/compile-diag.log" | head -30; exit 1; }
  grep -E "warning:" "$OUT/compile-diag.log" | head -10 || true
  L=$OUT/mono/mini; rm -rf "$L"; mkdir -p "$L"
  cp "$RT"/mono/mini/libmonosgen-2.0.a "$RT"/mono/mini/libmono-component-*-static.a "$L/"
  M=$OUT/members; rm -rf "$M"; mkdir -p "$M"
  for f in $(find "$O" -name '*.c.obj'); do cp "$f" "$M/${f:t}"; done
  ( cd "$M" && arm-vita-eabi-ar r "$L/libmonosgen-2.0.a" *.c.obj && arm-vita-eabi-ranlib "$L/libmonosgen-2.0.a" )
  for m in exception.c.obj exceptions-arm.c.obj mono-threads.c.obj shepherd-vita-diag.c.obj; do
    arm-vita-eabi-ar t "$L/libmonosgen-2.0.a" | grep -qx "$m" || { echo "member $m missing"; exit 1; }
  done
  [ "$(arm-vita-eabi-ar t "$L/libmonosgen-2.0.a" | wc -l)" -eq $(( $(arm-vita-eabi-ar t "$RT/mono/mini/libmonosgen-2.0.a" | wc -l) + 1 )) ] || { echo "unexpected member count"; exit 1; }
  arm-vita-eabi-nm "$L/libmonosgen-2.0.a" 2>/dev/null | grep -q " T shepherd_vita_diag_exc_fail$" || { echo "the diag hooks are not in the archive"; exit 1; }
  {
    echo "DIAGNOSTIC Vita runtime (vita/patches/shepherd-vita-diag.patch, -DSHEPHERD_VITA_DIAG) built $(date '+%Y-%m-%d %H:%M:%S')"
    echo "fork: $R ($(cd "$R" && git rev-parse --short HEAD) + its working-tree patch); normal build: $RT"
    echo "patch sha256: $(shasum -a 256 "$PATCH" | cut -c1-16)"
    echo "replaced members: exception.c.obj exceptions-arm.c.obj mono-threads.c.obj; added: shepherd-vita-diag.c.obj"
    (cd "$OUT" && shasum -a 256 mono/mini/*.a)
  } > "$OUT/DIAG-RUNTIME.txt"
  cat "$OUT/DIAG-RUNTIME.txt"
  ;;
prove)
  PL=$(clone plain 0); PA=$(clone patched-noflag 1)
  W=$OUT/prove; rm -rf "$W"; mkdir -p "$W"
  # the same absolute-path length is not needed: every path is mapped to /shepherd-runtime (the fork's own convention)
  cmds "$PL" "$W/plain" "" all > "$W/plain.sh"
  cmds "$PA" "$W/patched" "" all > "$W/patched.sh"
  for s in plain patched; do
    tr '\n' '\0' < "$W/$s.sh" | xargs -0 -n1 -P "$NCPU" zsh -c > "$W/$s.log" 2>&1 || { grep -E "error" "$W/$s.log" | head; exit 1; }
  done
  python3 - "$W" "$RT" > "$W/PROOF.txt" <<'EOF'
import os, sys, subprocess, filecmp, tempfile
w, rt = sys.argv[1:3]
def objs(d):
    r = {}
    for dp, _, fs in os.walk(d):
        for f in fs:
            if f.endswith('.obj'): p = os.path.join(dp, f); r[os.path.relpath(p, d)] = p
    return r
pl, pa = objs(os.path.join(w, 'plain')), objs(os.path.join(w, 'patched'))
def stripped(p):
    t = tempfile.mktemp(suffix='.o', dir=os.environ.get('TMPDIR'))
    subprocess.check_call(['arm-vita-eabi-objcopy', '--strip-debug', p, t]); b = open(t, 'rb').read(); os.unlink(t); return b
full_eq, strip_eq, diff = [], [], []
for k in sorted(pl):
    if k not in pa: diff.append(k + ' (missing in patched)'); continue
    if filecmp.cmp(pl[k], pa[k], shallow=False): full_eq.append(k)
    elif stripped(pl[k]) == stripped(pa[k]): strip_eq.append(k)
    else: diff.append(k)
print('PROOF: the patched fork compiled WITHOUT -DSHEPHERD_VITA_DIAG vs the plain fork (every runtime TU, the normal build commands)')
print('  translation units: %d' % len(pl))
print('  byte-identical objects: %d' % len(full_eq))
print('  identical except debug info (code, data, relocations, symbols equal; DWARF columns moved): %d' % len(strip_eq))
for k in strip_eq: print('     ' + k)
print('  DIFFERENT: %d' % len(diff))
for k in diff: print('     ' + k)
built = [k for k in pl if os.path.exists(os.path.join(rt, k))]
same_rt = [k for k in built if filecmp.cmp(pl[k], os.path.join(rt, k), shallow=False)]
strip_rt = [k for k in built if k not in same_rt and stripped(pl[k]) == stripped(os.path.join(rt, k))]
print('  (plain recompile vs the shared normal build %s: of the %d objects it built (%d TUs feed no archive it builds), %d byte-identical,' % (rt, len(built), len(pl) - len(built), len(same_rt)))
print('   %d identical except debug info, %d with different code%s)' % (len(strip_rt), len(built) - len(same_rt) - len(strip_rt),
      ': ' + ', '.join(k for k in built if k not in same_rt and k not in strip_rt) if len(built) - len(same_rt) - len(strip_rt) else ''))
print('VERDICT: %s' % ('normal builds are unchanged by the diag patch' if not diff else 'THE PATCH CHANGES NORMAL CODE'))
EOF
  cat "$W/PROOF.txt"
  grep -q "^VERDICT: normal builds are unchanged" "$W/PROOF.txt"
  ;;
*) echo "usage: build-runtime-diag.sh build|prove"; exit 2 ;;
esac
