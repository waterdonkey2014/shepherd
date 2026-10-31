#!/bin/sh
# SHEPHERD-VITA V5: configure (once) and build the PS Vita Mono runtime: libmonosgen-2.0.a (interpreter + sgen + coop,
# no JIT) and the static component libraries the host links (marshal-ilgen real, debugger/hot_reload/diagnostics stubs).
# Usage: build-runtime-vita.sh [--reconfigure]   (env: SHEPHERD_RUNTIME, SHEPHERD_VITA_RUNTIME_OBJ)
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
B=${SHEPHERD_VITA_RUNTIME_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime}
export VITASDK=$HOME/vitasdk PATH=$HOME/vitasdk/bin:$PATH
if [ ! -f "$B/build.ninja" ] || [ "$1" = "--reconfigure" ]; then
  "$HERE/configure-vita.sh"
fi
# (kit #9 track jit: ninja runs from a `cd` into $B -- not `ninja -C` -- so the compilers inherit PWD = $B as given: GCC takes the debug info's
# comp_dir from $PWD when it names the current directory, and the prefix map then applies. A build directory under a symlink (kit #9's
# ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit9 -> "${SHEPHERD_DATA:-$HOME}/shepherd-kit9") otherwise records the resolved path, and every member differs from
# kit #5's in its debug info; a build directory without a symlink builds exactly as before.)
cd "$B" && ${NINJA:-ninja} mono/mini/libmonosgen-2.0.a \
  mono/mini/libmono-component-marshal-ilgen-static.a mono/mini/libmono-component-debugger-stub-static.a \
  mono/mini/libmono-component-hot_reload-stub-static.a mono/mini/libmono-component-diagnostics_tracing-stub-static.a \
  > "$B/build.log" 2>&1 || { grep -E "FAILED|error:" "$B/build.log" | head -40; exit 1; }
grep -c "warning:" "$B/build.log" | sed 's/^/compiler warnings: /'
ls -la "$B"/mono/mini/libmonosgen-2.0.a "$B"/mono/mini/libmono-component-*-static.a
