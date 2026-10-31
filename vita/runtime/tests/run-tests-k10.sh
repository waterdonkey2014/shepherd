#!/bin/zsh
# SHEPHERD-VITA device kit #10, track memory: Mac unit tests of vita/patches/k10-memory.patch's arena code (no device needed) -- the kit #9
# arena tests (test_arena.c, test_arena_k9.c) against mono-mmap-vita.c with k9-gc.patch + k10-memory.patch (their policies 0 / 1 / 2 must
# behave exactly as before), then test_arena_k10.c (the shared pool: policy 3 + mono_vita_vm_arena_pool_*; and policy 0 refusing the pool
# API); and test_newlib_k10.c (the host's newlib side, review fixes R1 / R4 / R5). Exit 0 = all passed. env: as run-tests.sh (SHEPHERD_RUNTIME, SHEPHERD_CROSS_OBJ, TMPDIR)
set -euo pipefail
HERE=${0:a:h}; V=${HERE:h:h}
RT=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}; R=$RT/src/mono
C=${SHEPHERD_CROSS_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita}
[ -f "$C/mono/mini/libmonosgen-2.0.a" ] || { echo "no cross build at $C"; exit 1; }
T=$(mktemp -d "${TMPDIR:-/tmp}/vita-tests-k10.XXXXXX"); trap 'rm -rf "$T"' EXIT
# review fixes R1 / R4 / R5: the host's newlib side (vita/shepherd/k10_newlib.inc.c as it is, its device section on, mocked sce* calls)
sed -e 's/#if !defined (SHEPHERD_MAC_CHECK)/#if 1/' -e 's/^#include <reent.h>//' "$V/shepherd/k10_newlib.inc.c" > $T/k10_newlib.inc.c
clang -O1 -Wall -Wno-unused-function -Wno-unused-variable -I$T -o $T/test_newlib_k10 $HERE/test_newlib_k10.c
echo "== test_newlib_k10.c (the host's newlib side: who owns a pointer for the vitaGL wrappers, the fallback, the pre-main env read)"; (cd $T && ./test_newlib_k10)
mkdir -p $T/p/src/mono/mono/utils && cp $R/mono/utils/*.h $T/p/src/mono/mono/utils/ && cp $R/mono/utils/mono-mmap-vita.c $T/p/src/mono/mono/utils/
# the mono/utils parts of k9-gc.patch, then of k10-memory.patch (each patch's other files are sgen's: not part of this test)
utils_part () {
python3 - "$1" "$2" <<'PY'
import sys
out, keep = [], False
for l in open(sys.argv[1]):
    if l.startswith('diff '): keep = '/mono/utils/' in l
    elif l.startswith('--- ') and not keep: keep = '/mono/utils/' in l
    if keep: out.append(l)
open(sys.argv[2], 'w').writelines(out)
PY
}
utils_part "$V/patches/k9-gc.patch" $T/k9-utils.patch
utils_part "$V/patches/k10-memory.patch" $T/k10-utils.patch
(cd $T/p && patch -s -p1 < $T/k9-utils.patch && patch -s -p1 < $T/k10-utils.patch)
EG=($C/mono/eglib/CMakeFiles/eglib_objects.dir/*.o $C/mono/eglib/CMakeFiles/eglib_objects.dir/**/minipal/utf8.c.o(N))
INC=(-DHOST_VITA -I$HERE/fake-psp2 -I$C -I$C/mono/eglib -I$T/p/src/mono -I$T/p/src/mono/mono -I$R -I$R/mono -I$R/mono/eglib -I$RT/src/native/public -I$RT/src/native)
clang -O1 -g $INC -o $T/test_arena $HERE/test_arena.c $T/p/src/mono/mono/utils/mono-mmap-vita.c $EG
echo "== test_arena.c against mono-mmap-vita.c + k9-gc + k10-memory (policy 0 = kit #9)"; $T/test_arena
clang -O1 -g $INC -o $T/test_arena_k9 $HERE/test_arena_k9.c $T/p/src/mono/mono/utils/mono-mmap-vita.c $EG
echo "== test_arena_k9.c check (policies 1 / 2 / 0 as in kit #9)"; $T/test_arena_k9 check
for s in 12345 777; do $T/test_arena_k9 frag 0 $s; $T/test_arena_k9 frag 1 $s; done
clang -O1 -g $INC -o $T/test_arena_k10 $HERE/test_arena_k10.c $T/p/src/mono/mono/utils/mono-mmap-vita.c $EG
echo "== test_arena_k10.c check (the shared pool)"; $T/test_arena_k10 check
echo "== test_arena_k10.c policy0 (kit #9: the pool API refuses)"; $T/test_arena_k10 policy0
echo "ALL K10 ARENA TESTS PASSED"
