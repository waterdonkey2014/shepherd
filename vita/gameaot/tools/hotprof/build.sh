#!/bin/zsh
# Build the Mac-only Mono profiler module of the game-aot hot-method study (hotprof.c) into <out dir> (default: the study's
# build-volume tools dir; never inside the repo). Xcode clang, public Mono API only, resolved at load time from the Mono runtime
# already in the process (libcoreclr.dylib of the osx-arm64 Mono pack). No downloads.
D="${0:A:h}"; OUT=${1:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/hot/tools/hotprof}; mkdir -p "$OUT"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
clang -O2 -Wall -Wno-unused-function -dynamiclib -arch arm64 -undefined dynamic_lookup -o "$OUT/libmono-profiler-shepherdhot.dylib" "$D/hotprof.c" && echo "$OUT/libmono-profiler-shepherdhot.dylib"
