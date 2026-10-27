#!/bin/zsh
# Build the GAME-AOT mechanics profile writer (gameaot_aotprof.c): a Mac-only Mono profiler module, Xcode clang, no downloads.
# The Mono symbols resolve at load time from the runtime already in the process (libcoreclr.dylib of the osx-arm64 Mono pack).
# Output on the build volume only: ${GAMEAOT_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot}/aotprof/libmono-profiler-gameaotprof.dylib
set -e
D="${0:A:h}"; O=${GAMEAOT_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot}/aotprof; mkdir -p "$O"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
clang -O2 -Wall -Wextra -dynamiclib -arch arm64 -undefined dynamic_lookup -o "$O/libmono-profiler-gameaotprof.dylib" "$D/gameaot_aotprof.c"
echo "$O/libmono-profiler-gameaotprof.dylib"
