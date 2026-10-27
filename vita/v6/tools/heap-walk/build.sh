#!/bin/zsh
# Build the Mac-only Mono heap-census profiler module (see shepherd_heap_walk.c). Xcode clang, no downloads.
# The Mono symbols resolve at load time from the Mono runtime already in the process (libcoreclr.dylib of the osx-arm64 Mono pack).
D="${0:A:h}"; mkdir -p "$D/out"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
clang -O2 -Wall -dynamiclib -arch arm64 -undefined dynamic_lookup -o "$D/out/libmono-profiler-shepherdheap.dylib" "$D/shepherd_heap_walk.c" && echo "$D/out/libmono-profiler-shepherdheap.dylib"
