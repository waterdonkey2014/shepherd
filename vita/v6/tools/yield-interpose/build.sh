#!/bin/zsh
# Build the Mac-only sched_yield interposer (see yield_interpose.c). Xcode clang; the policy header comes from the fork checkout.
D="${0:A:h}"; RT=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}; mkdir -p "$D/out"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
clang -O2 -Wall -dynamiclib -arch arm64 -I$RT/src/mono -o "$D/out/libyield-vita.dylib" "$D/yield_interpose.c" && echo "$D/out/libyield-vita.dylib"
