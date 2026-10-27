#!/bin/zsh
# SHEPHERD-VITA kit #5 review fix (vita/notes/kit5-llvm.md "Review fixes"): the Vita runtime for the LLVM A/B apps (SHPD00191 /
# SHPD00192) -- the shared runtime's libmonosgen-2.0.a with exactly two members rebuilt from the fork + vita/llvm/fork-eh-vfp.patch:
#   exceptions-arm.c.obj  fix B: mono_arch_unwind_frame carries d8-d15 through the DWARF unwind on HOST_VITA (stock: iOS / tvOS only),
#                         so a frame above an LLVM frame that saved d8-d15 gets its values back after an exception is caught
#   unwind.c.obj          fix C: a saved d-register is restored as the 64-bit value it is (stock truncated it to 32 bits on ARM32)
# (fix A, the throw trampolines' fp_regs pointer, lives in the AOT compiler: vita/llvm/build-cross-llvm-armhf.sh. The runtime is
# DISABLE_JIT, so the patch's fix A code is not even compiled here.) Every other member and the 4 component archives are the shared
# runtime's own bytes (checked below), so the LLVM apps differ from the shared runtime by these two objects only. Nothing shared is
# written: the fork ($R) and the shared runtime build ($RT) are only read.
#   build-runtime-eh-vfp.sh       APFS-clone $R/src/mono twice (plain, patched), compile the two TUs from each with the shared build's
#                                 exact commands (compile_commands.json, sources re-pointed at the clone, the clone mapped to
#                                 /shepherd-runtime like the fork), check that the PLAIN objects equal the shared archive's members
#                                 byte for byte (the commands reproduce the shared build), write $OUT/mono/mini/libmonosgen-2.0.a =
#                                 the shared archive with the two PATCHED members replaced, check every other member is unchanged,
#                                 write $OUT/EH-VFP-RUNTIME.txt (build.sh reads its archive sha256 line to recognise this runtime)
# Use: SHEPHERD_VITA_RUNTIME_OBJ=$OUT vita/shepherd/build.sh (with SHEPHERD_AOT_LLVM and the fixed compiler).
# env: SHEPHERD_RUNTIME (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime), SHEPHERD_VITA_RUNTIME_OBJ (the shared build to start from, default
#      ${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime), SHEPHERD_VITA_RUNTIME_EH_VFP_OBJ (REQUIRED: the output dir; never a shared one)
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
RT=${SHEPHERD_VITA_RUNTIME_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime}
OUT=${SHEPHERD_VITA_RUNTIME_EH_VFP_OBJ:?set SHEPHERD_VITA_RUNTIME_EH_VFP_OBJ, e.g. ${SHEPHERD_WORK:-$HOME/shepherd-work}/<your dir>/obj/vita-runtime-eh-vfp}
mkdir -p "$OUT" "$TMPDIR"; OUT=${OUT:a}
PATCH=$HERE/fork-eh-vfp.patch
case $OUT in "${RT:a}"|"${RT:a}"/*|"${R:a}"|"${R:a}"/*|${SHEPHERD_WORK:-$HOME/shepherd-work}/obj|${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/*) echo "refusing to write into the fork or a shared build: $OUT"; exit 1 ;; esac
[ -f "$RT/compile_commands.json" ] || { echo "no runtime build at $RT (vita/toolchain/build-runtime-vita.sh)"; exit 1; }
TUS=(mono/mini/exceptions-arm.c mono/mini/unwind.c)
MEMBERS=(exceptions-arm.c.obj unwind.c.obj)

# clone <name> <patched 0|1>: an APFS clone of the fork's src/mono (cheap), optionally patched; prints its root
clone () {
  local d=$OUT/src/$1
  rm -rf "$d"; mkdir -p "$d/src"
  cp -cR "$R/src/mono" "$d/src/mono" 2>/dev/null || cp -R "$R/src/mono" "$d/src/mono"
  if [ "$2" = 1 ]; then (cd "$d" && patch -s -p1 < "$PATCH") || { echo "fork-eh-vfp.patch does not apply to $R"; exit 1; }; fi
  echo "$d"
}

# cmds <clone root> <obj out dir> <tu ...>: the shared build's compile commands for those TUs, re-pointed at the clone (sources and
# src/mono include dirs), objects into <obj out dir>, the clone's path mapped to /shepherd-runtime like the fork's
cmds () {
  python3 - "$RT" "$R" "$@" <<'EOF'
import json, shlex, sys, os
rt, r, root, objdir = sys.argv[1:5]
tus = sys.argv[5:]
cc = json.load(open(os.path.join(rt, 'compile_commands.json')))
mono = os.path.join(r, 'src/mono/')
want = {t: 0 for t in tus}
out = []
for e in cc:
    f = e['file']
    rel = f[len(mono):] if f.startswith(mono) else None
    if rel not in want:
        continue
    args = e.get('arguments') or shlex.split(e['command'])
    a2 = []; i = 0
    while i < len(args):
        x = args[i]
        if x == '-o':
            a2 += ['-o', os.path.join(objdir, os.path.basename(args[i + 1]))]; i += 2; continue
        if x.startswith(mono) or x.startswith('-I' + mono):
            x = x.replace(mono, os.path.join(root, 'src/mono/'), 1)
        a2.append(x); i += 1
    a2 += ['-ffile-prefix-map=%s=/shepherd-runtime' % root]
    out.append('cd %s && %s' % (shlex.quote(e['directory']), ' '.join(shlex.quote(y) for y in a2)))
    want[rel] += 1
bad = [t for t, n in want.items() if n != 1]
if bad: sys.exit('not exactly once in compile_commands.json: %s' % bad)
print('\n'.join(out))
EOF
}

clone plain 0 > /dev/null; clone patched 1 > /dev/null
for s in plain patched; do
  O=$OUT/objs-$s; rm -rf "$O"; mkdir -p "$O"
  cmds "$OUT/src/$s" "$O" $TUS > "$OUT/compile-$s.sh"
  zsh -e "$OUT/compile-$s.sh" > "$OUT/compile-$s.log" 2>&1 || { grep -E "error" "$OUT/compile-$s.log" | head -20; exit 1; }
done
# the shared archive's members
X=$OUT/members-shared; rm -rf "$X"; mkdir -p "$X"; (cd "$X" && arm-vita-eabi-ar x "$RT/mono/mini/libmonosgen-2.0.a")
for m in $MEMBERS; do
  [ -f "$X/$m" ] || { echo "the shared archive has no member $m"; exit 1; }
  cmp -s "$OUT/objs-plain/$m" "$X/$m" || { echo "PLAIN recompile of $m differs from the shared archive's: the shared runtime is not this fork's build of it (rebuild it or fix the commands)"; exit 1; }
  cmp -s "$OUT/objs-patched/$m" "$X/$m" && { echo "the patched $m equals the stock one: the patch did not take"; exit 1; }
done
L=$OUT/mono/mini; rm -rf "$L"; mkdir -p "$L"
cp "$RT"/mono/mini/libmonosgen-2.0.a "$RT"/mono/mini/libmono-component-*-static.a "$L/"
( cd "$OUT/objs-patched" && arm-vita-eabi-ar r "$L/libmonosgen-2.0.a" $MEMBERS && arm-vita-eabi-ranlib "$L/libmonosgen-2.0.a" )
# every member but the two is the shared one, the member list and order are unchanged
Y=$OUT/members-new; rm -rf "$Y"; mkdir -p "$Y"; (cd "$Y" && arm-vita-eabi-ar x "$L/libmonosgen-2.0.a")
[ "$(arm-vita-eabi-ar t "$L/libmonosgen-2.0.a")" = "$(arm-vita-eabi-ar t "$RT/mono/mini/libmonosgen-2.0.a")" ] || { echo "member list / order changed"; exit 1; }
nd=0; ns=0
for f in "$X"/*; do m=${f:t}
  if (( ${MEMBERS[(Ie)$m]} )); then cmp -s "$f" "$Y/$m" && { echo "$m not replaced"; exit 1; }; nd=$((nd + 1))
  else cmp -s "$f" "$Y/$m" || { echo "member $m changed"; exit 1; }; ns=$((ns + 1)); fi
done
for a in "$RT"/mono/mini/libmono-component-*-static.a; do cmp -s "$a" "$L/${a:t}" || { echo "${a:t} differs"; exit 1; }; done
SH=$(shasum -a 256 "$L/libmonosgen-2.0.a" | cut -d' ' -f1)
{
  echo "EH-VFP Vita runtime (vita/llvm/fork-eh-vfp.patch fixes B + C) built $(date '+%Y-%m-%d %H:%M:%S')"
  echo "fork: $R ($(cd "$R" && git rev-parse --short HEAD 2>/dev/null || echo '?') + its working-tree patch); shared build: $RT"
  echo "patch sha256: $(shasum -a 256 "$PATCH" | cut -c1-16)"
  echo "replaced members: $MEMBERS (patched); plain recompiles of both = the shared members byte for byte"
  echo "unchanged members: $ns of $((ns + nd)) byte-identical to $RT's; the 4 component archives copied unchanged"
  echo "libmonosgen-2.0.a sha256 $SH"
  (cd "$OUT" && shasum -a 256 mono/mini/*.a)
} > "$OUT/EH-VFP-RUNTIME.txt"
rm -rf "$X" "$Y" "$OUT/src"
cat "$OUT/EH-VFP-RUNTIME.txt"
