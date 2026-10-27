#!/bin/sh
# Builds the Mac-only Mono-interpreter CoreFoundation shim (see cf_interp_shim.c). Output: MonoCompat/bin/libshepherd_cfshim.dylib
set -e
cd "$(dirname "$0")"; mkdir -p bin
xcrun clang -arch arm64 -dynamiclib -O2 -fvisibility=hidden -o bin/libshepherd_cfshim.dylib cf_interp_shim.c \
  -framework CoreFoundation -Wl,-reexport_framework,CoreFoundation -install_name @rpath/libshepherd_cfshim.dylib
echo "built $(pwd)/bin/libshepherd_cfshim.dylib"
