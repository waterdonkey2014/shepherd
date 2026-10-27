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
${NINJA:-ninja} -C "$B" mono/mini/libmonosgen-2.0.a \
  mono/mini/libmono-component-marshal-ilgen-static.a mono/mini/libmono-component-debugger-stub-static.a \
  mono/mini/libmono-component-hot_reload-stub-static.a mono/mini/libmono-component-diagnostics_tracing-stub-static.a \
  > "$B/build.log" 2>&1 || { grep -E "FAILED|error:" "$B/build.log" | head -40; exit 1; }
grep -c "warning:" "$B/build.log" | sed 's/^/compiler warnings: /'
ls -la "$B"/mono/mini/libmonosgen-2.0.a "$B"/mono/mini/libmono-component-*-static.a
