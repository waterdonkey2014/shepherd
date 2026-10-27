#!/bin/zsh
# SHEPHERD-VITA: Mac-side unit tests of the Vita-only C code (no device needed). Uses the cross build's host eglib.
# env: SHEPHERD_RUNTIME (fork checkout), SHEPHERD_CROSS_OBJ (build-cross.sh output), TMPDIR (scratch; point it at the
# build volume, e.g. TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp, when the main disk is full). Exit status 0 = every test passed.
set -euo pipefail
HERE=${0:a:h}
RT=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
R=$RT/src/mono
C=${SHEPHERD_CROSS_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/cross-vita}
[ -d "$R/mono" ] || { echo "no runtime checkout at $RT (set SHEPHERD_RUNTIME)"; exit 1; }
[ -f "$C/mono/mini/libmonosgen-2.0.a" ] || { echo "no cross build at $C (run build-cross.sh or set SHEPHERD_CROSS_OBJ)"; exit 1; }
T=$(mktemp -d "${TMPDIR:-/tmp}/vita-tests.XXXXXX")
trap 'rm -rf "$T"' EXIT

# host eglib from the cross build; CMake names objects of sources outside src/mono after their absolute path
EG=($C/mono/eglib/CMakeFiles/eglib_objects.dir/*.o $C/mono/eglib/CMakeFiles/eglib_objects.dir/**/minipal/utf8.c.o(N))
(( ${#EG} > 1 )) || { echo "eglib objects missing under $C/mono/eglib"; exit 1; }
clang -O1 -g -DHOST_VITA -I$HERE/fake-psp2 -I$C -I$C/mono/eglib -I$R -I$R/mono -I$R/mono/eglib -I$RT/src/native/public -I$RT/src/native \
  -o $T/test_arena $HERE/test_arena.c $R/mono/utils/mono-mmap-vita.c $EG
$T/test_arena

# V6 stage 2: managed Thread.Yield's back-off (mono-vita-yield.c): the policy, simulated game patterns, a real Mac thread's CPU
clang -O1 -g -Wall -DHOST_VITA -I$HERE/fake-psp2 -I$C -I$R -I$R/mono -o $T/test_yield $HERE/test_yield.c $R/mono/utils/mono-vita-yield.c
$T/test_yield

# the shim's pure path helpers (resolve_path, normalize_path, path_identity), copied between the BEGIN/END markers
awk '/BEGIN pure path helpers/{p=1} p{print} /END pure path helpers/{exit}' $HERE/../system-native/pal_vita.c > $T/path_helpers.inc
grep -q 'END pure path helpers' $T/path_helpers.inc || { echo "pure path helper markers missing in pal_vita.c"; exit 1; }
clang -O1 -g -Wall -Wno-unused-function -I$T -o $T/test_paths $HERE/test_paths.c && $T/test_paths

# kit #5 B-2: the shim's one-shot read redirect (the "pure redirect" block, with the path helpers it uses)
awk '/BEGIN pure redirect/{p=1} p{print} /END pure redirect/{exit}' $HERE/../system-native/pal_vita.c > $T/redirect.inc
grep -q 'END pure redirect' $T/redirect.inc || { echo "pure redirect markers missing in pal_vita.c"; exit 1; }
clang -O1 -g -Wall -Wno-unused-function -I$T -o $T/test_redirect $HERE/test_redirect.c && $T/test_redirect

# kit #4 B-3: the shim's read-ahead for small sequential reads (the "pure read-ahead" block), against direct preads of the same data
awk '/BEGIN pure read-ahead/{p=1} p{print} /END pure read-ahead/{exit}' $HERE/../system-native/pal_vita.c > $T/readahead.inc
grep -q 'END pure read-ahead' $T/readahead.inc || { echo "pure read-ahead markers missing in pal_vita.c"; exit 1; }
clang -O1 -g -Wall -Wno-unused-function -I$T -o $T/test_readahead $HERE/test_readahead.c && $T/test_readahead

# armhf gsharedvt transition: the cross build's libmonosgen (host code, Vita hard-float target ABI, the fork's
# TARGET_VITA copier) against the upstream copier from git HEAD, renamed. Needs build-cross.sh to be current.
MINI_FLAGS=(-arch arm64 -std=gnu99 -O1 -g -fno-strict-aliasing -fwrapv -Wno-unused-function -Wno-asm-operand-widths
  -DARM_FPU_VFP_HARD=1 -DHAVE_CONFIG_H -DMONO_DLL_EXPORT -DNO_GLOBALIZATION_SHIM -D_THREAD_SAFE
  -I$RT/artifacts/obj -I$RT/src/native -I$C/mono/mini -I$C -I$R -I$R/mono -I$R/mono/mini -I$R/mono/eventpipe
  -I$C/mono/mini/eventpipe -I$RT/src/native/public -I$C/mono/eglib -I$R/mono/eglib)
git -C $RT show HEAD:src/mono/mono/mini/tramp-arm-gsharedvt.c > $T/tramp-arm-gsharedvt-upstream.c
clang $MINI_FLAGS -Dmono_arm_start_gsharedvt_call=upstream_arm_start_gsharedvt_call \
  -Dmono_arch_get_gsharedvt_trampoline=upstream_arch_get_gsharedvt_trampoline \
  -c $T/tramp-arm-gsharedvt-upstream.c -o $T/upstream.o
clang $MINI_FLAGS -o $T/test_gsharedvt_arm $HERE/test_gsharedvt_arm.c $T/upstream.o \
  $C/mono/mini/CMakeFiles/marshal-ilgen-objects.dir/__/component/marshal-ilgen.c.o $C/mono/mini/libmonosgen-2.0.a \
  -framework CoreFoundation -framework Foundation -lz -lm
$T/test_gsharedvt_arm

# V6: the crypto shim -- known-answer vectors (FIPS 197 / SP 800-38A / FIPS 180-4 / RFC 1321 / 2202 / 4231 / 6070) and
# random differential runs against CommonCrypto, through the CryptoNative_* entry points
S=$HERE/../system-native
clang -O1 -g -Wall -Wno-unused-function -Wno-deprecated-declarations -I$S/crypto -o $T/test_crypto $HERE/test_crypto.c $S/crypto/vita_crypto.c $S/pal_vita_crypto.c
$T/test_crypto | grep -v '^     (log)'

# V6: the managed contract -- the linux-arm System.Security.Cryptography / System.IO.Compression / System.Net.* IL bound
# to a Mac build of the shim's crypto / network / zlib / stub files, compared with the Mac BCL
SHEPHERD_RUNTIME=$RT $HERE/contract/run.sh 2>&1 | grep -v '^N> '
echo "PASS all Vita unit tests (arena, yield, paths, redirect, readahead, gsharedvt-arm, crypto, contract)"
