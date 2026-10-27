#!/bin/zsh
# Build the Mac-only Mono call-timing profiler of the V6 stage-2 speed pass (see shepherd_interp_prof.c). Xcode clang, no downloads.
# The Mono symbols resolve at load time from the Mono runtime already in the process (libcoreclr.dylib of the osx-arm64 Mono pack).
D="${0:A:h}"; mkdir -p "$D/out"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
clang -O2 -Wall -dynamiclib -arch arm64 -undefined dynamic_lookup -o "$D/out/libmono-profiler-shepherdprof.dylib" "$D/shepherd_interp_prof.c" && echo "$D/out/libmono-profiler-shepherdprof.dylib"
