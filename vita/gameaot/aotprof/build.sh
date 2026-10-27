#!/bin/zsh
# Build the Mac-only Mono profiler module shepherd_aotprof.c (Xcode clang, public Mono embedding API resolved at load time
# from the osx-arm64 Mono runtime in the process). Output on the build volume only.
D="${0:A:h}"; O=${GA_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/sizes}/tools/aotprof; mkdir -p "$O"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
clang -O2 -Wall -dynamiclib -arch arm64 -undefined dynamic_lookup -o "$O/libmono-profiler-shepherdaot.dylib" "$D/shepherd_aotprof.c" && echo "$O/libmono-profiler-shepherdaot.dylib"
