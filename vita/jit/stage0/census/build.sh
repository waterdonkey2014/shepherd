#!/bin/zsh
# Kit #7 JIT stage 0: build the Mac-only Mono profiler module of the long-session code-cache census (shepherd_jit_census.c).
# Xcode clang, no downloads. The Mono symbols resolve at load time from the Mono runtime already in the process (the osx-arm64 Mono pack of
# the vita-sim publish). Output: $JIT_WORK/census/libmono-profiler-shepjit.dylib (default JIT_WORK = ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work),
# so nothing built lands in the tree.
D="${0:A:h}"; OUT=${JIT_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work}/census; mkdir -p "$OUT"
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/tmp}
clang -O2 -Wall -Wextra -dynamiclib -arch arm64 -undefined dynamic_lookup -o "$OUT/libmono-profiler-shepjit.dylib" "$D/shepherd_jit_census.c" && echo "$OUT/libmono-profiler-shepjit.dylib"
