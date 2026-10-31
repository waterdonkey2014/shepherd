#!/bin/zsh
# SHEPHERD-VITA device kit #9, track gc: Mac unit tests of vita/patches/k9-gc.patch's Vita C code (no device needed) -- run-tests.sh's arena
# test against the PATCHED mono-mmap-vita.c (its defaults are kit #8's: the same test must still pass), then test_arena_k9.c (M8, the
# two-ended arena: placement checks + the fragmentation comparison of policy 0 vs 1 over 3 seeds). Exit 0 = all passed.
# env: as run-tests.sh (SHEPHERD_RUNTIME, SHEPHERD_CROSS_OBJ, TMPDIR)
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h:h}
RT=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}; R=$RT/src/mono
C=${SHEPHERD_CROSS_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita}
[ -f "$C/mono/mini/libmonosgen-2.0.a" ] || { echo "no cross build at $C"; exit 1; }
T=$(mktemp -d "${TMPDIR:-/tmp}/vita-tests-k9.XXXXXX"); trap 'rm -rf "$T"' EXIT
# the patched utils sources: a copy of the fork's mono/utils headers + the two files k9-gc.patch changes there, patched
mkdir -p $T/p/src/mono/mono/utils && cp $R/mono/utils/*.h $T/p/src/mono/mono/utils/ && cp $R/mono/utils/mono-mmap-vita.c $T/p/src/mono/mono/utils/
python3 - "$V/patches/k9-gc.patch" "$T/k9-utils.patch" <<'PY'
import sys
out, keep = [], False
for l in open(sys.argv[1]):
    if l.startswith('diff ') or l.startswith('--- '):
        if l.startswith('diff '): keep = '/mono/utils/' in l
        elif not keep: keep = '/mono/utils/' in l
    if keep: out.append(l)
open(sys.argv[2], 'w').writelines(out)
PY
(cd $T/p && patch -s -p1 < $T/k9-utils.patch)
EG=($C/mono/eglib/CMakeFiles/eglib_objects.dir/*.o $C/mono/eglib/CMakeFiles/eglib_objects.dir/**/minipal/utf8.c.o(N))
INC=(-DHOST_VITA -I$HERE/fake-psp2 -I$C -I$C/mono/eglib -I$T/p/src/mono -I$T/p/src/mono/mono -I$R -I$R/mono -I$R/mono/eglib -I$RT/src/native/public -I$RT/src/native)
clang -O1 -g $INC -o $T/test_arena $HERE/test_arena.c $T/p/src/mono/mono/utils/mono-mmap-vita.c $EG
echo "== test_arena.c against the patched mono-mmap-vita.c (policy 0 = kit #8)"; $T/test_arena
clang -O1 -g $INC -o $T/test_arena_k9 $HERE/test_arena_k9.c $T/p/src/mono/mono/utils/mono-mmap-vita.c $EG
echo "== test_arena_k9.c check"; $T/test_arena_k9 check
echo "== fragmentation: the same sgen-shaped workload, first fit (0) vs two-ended (1)"
for s in 12345 777 4242; do $T/test_arena_k9 frag 0 $s; $T/test_arena_k9 frag 1 $s; $T/test_arena_k9 frag 2 $s; done
