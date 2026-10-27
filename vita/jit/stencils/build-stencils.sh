#!/bin/zsh
# SHEPHERD-VITA device kit #8 (track jit, vita/notes/kit8-jit.md): compiles interp/shepjit-stencils.c with interp.c's OWN compile command
# (from the configured runtime's compile_commands.json: the same arm-vita-eabi-gcc, defines, includes, -O3 -ffp-contract=off -fwrapv
# -fno-strict-aliasing) plus the stencil flags, and turns the object into the stencil table with gen-stencils.py.
#   build-stencils.sh <runtime obj dir (configured)> <runtime source tree> <out.inc> [work dir]
# The stencil flags: -mlong-calls (every call a MOVW/MOVT + BLX: the code block may be any distance from the runtime), -ffunction-sections
# (one section per stencil), -fno-unwind-tables -fno-asynchronous-unwind-tables (no .ARM.exidx), -g0. The continuation holes are declared
# short_call in the source (direct B.W).
set -euo pipefail
HERE=${0:a:h}
OBJ=${${1:?usage: build-stencils.sh <runtime obj dir> <runtime source tree> <out.inc> [work dir]}:a}; SRC=${2:?}; SRC=${SRC:a}; OUT=${3:?}; OUT=${OUT:a}; WK=${4:-${OUT:h}}; WK=${WK:a}
export PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH"
mkdir -p "$WK"
CMD=$(python3 - "$OBJ/compile_commands.json" <<'EOF'
import json, sys
for e in json.load(open(sys.argv[1])):
    if e['file'].endswith('/interp/interp.c'):
        print(e['command']); break
else:
    sys.exit('no interp.c in compile_commands.json')
EOF
)
ST=$SRC/src/mono/mono/mini/interp/shepjit-stencils.c
[[ -f $ST ]] || { echo "no $ST"; exit 1; }
# interp.c's command with: its -o / -c replaced, -g -> -g0, the stencil flags added
A=(${(z)CMD})
NEW=()
skip=0
for a in $A; do
  if (( skip )); then skip=0; continue; fi
  case $a in
    -o|-c) skip=1 ;;
    -g) NEW+=(-g0) ;;
    -Wl,-q) ;;
    *) NEW+=($a) ;;
  esac
done
(cd "$OBJ" && $NEW -mlong-calls -ffunction-sections -fno-unwind-tables -fno-asynchronous-unwind-tables -Wno-unused-but-set-variable -Wno-unused-variable \
   -Wno-missing-prototypes -o "$WK/shepjit-stencils.o" -c "$ST")
python3 "$HERE/gen-stencils.py" "$WK/shepjit-stencils.o" "$OUT" "$WK/shepjit-stencils.list"
