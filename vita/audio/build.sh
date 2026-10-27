#!/bin/bash
# shepaudio build: Mac dylib + static lib + test/render CLIs (+ ASan/TSan test builds), Vita static lib + link check.
#   vita/audio/build.sh [mac|vita|san|all]      (default: all)
# Outputs: vita/audio/build/{mac,vita,san}/ (gitignored). Nothing here opens an audio device.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/build"
[ -d ${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp ] && export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
what=${1:-all}
CORE="sa_json.c sa_codec.c sa_manifest.c sa_cache.c sa_stream.c sa_mixer.c sa_system.c sa_out_file.c"
WARN="-Wall -Wextra -Wno-unused-parameter -Wshadow -Wpointer-arith -Wstrict-prototypes"

mac() {
  local O="$OUT/mac"; mkdir -p "$O/obj"
  local CF=(-std=c99 -O2 -g $WARN -fvisibility=hidden -fPIC "-I$HERE/include" "-I$HERE/src")
  local objs=()
  for f in $CORE sa_plat_posix.c sa_aac_apple.c sa_out_rt.c; do
    clang "${CF[@]}" -c "$HERE/src/$f" -o "$O/obj/${f%.c}.o"; objs+=("$O/obj/${f%.c}.o")
  done
  rm -f "$O/libshepaudio.a"; ar rcs "$O/libshepaudio.a" "${objs[@]}"
  clang -dynamiclib -o "$O/libshepaudio.dylib" "${objs[@]}" -install_name @rpath/libshepaudio.dylib \
    -framework AudioToolbox -framework CoreFoundation
  for t in sa_test sa_render sa_check; do
    clang "${CF[@]}" -fvisibility=default "$HERE/tools/$t.c" "$O/libshepaudio.a" -framework AudioToolbox -framework CoreFoundation -o "$O/$t"
  done
  echo "mac: $O/libshepaudio.dylib $O/libshepaudio.a $O/sa_test $O/sa_render"
}

san() {
  local O="$OUT/san"; mkdir -p "$O"
  local SRCS=(); for f in $CORE sa_plat_posix.c sa_aac_apple.c sa_out_rt.c; do SRCS+=("$HERE/src/$f"); done
  for s in address thread; do
    clang -std=c99 -O1 -g -fno-omit-frame-pointer -fsanitize=$s $( [ $s = address ] && echo -fsanitize=undefined ) $WARN \
      -I"$HERE/include" -I"$HERE/src" "${SRCS[@]}" "$HERE/tools/sa_test.c" -framework AudioToolbox -framework CoreFoundation -o "$O/sa_test_$s"
  done
  echo "san: $O/sa_test_address $O/sa_test_thread"
}

vita() {
  export VITASDK=${VITASDK:-$HOME/vitasdk}; export PATH="$VITASDK/bin:$PATH"
  local O="$OUT/vita"; mkdir -p "$O/obj"
  local CF=(-std=gnu99 -O2 -g0 $WARN -ffunction-sections -fdata-sections "-ffile-prefix-map=$HERE=/shepaudio" "-I$HERE/include" "-I$HERE/src")
  local objs=()
  for f in $CORE sa_plat_vita.c sa_aac_vita.c sa_out_vita.c; do
    arm-vita-eabi-gcc "${CF[@]}" -c "$HERE/src/$f" -o "$O/obj/${f%.c}.o"; objs+=("$O/obj/${f%.c}.o")
  done
  rm -f "$O/libshepaudio_vita.a"; arm-vita-eabi-ar rcs "$O/libshepaudio_vita.a" "${objs[@]}"
  # link check: every public entry point referenced from a tiny main, all SceAudio*/SceKernel imports resolved
  arm-vita-eabi-gcc "${CF[@]}" -c "$HERE/vita/linkcheck.c" -o "$O/obj/linkcheck.o"
  arm-vita-eabi-gcc -Wl,-q -Wl,--gc-sections -Wl,-Map="$O/linkcheck.map" -o "$O/linkcheck.elf" "$O/obj/linkcheck.o" "$O/libshepaudio_vita.a" \
    -lSceAudio_stub -lSceAudiodec_stub -lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceProcessmgr_stub -lSceIofilemgr_stub \
    -lSceSysmem_stub -lm -lc 2> "$O/link.log" || { grep -E "undefined|error" "$O/link.log" | head -40; exit 1; }
  vita-elf-create "$O/linkcheck.elf" "$O/linkcheck.velf" > "$O/velf.log" 2>&1 || { tail -20 "$O/velf.log"; exit 1; }
  arm-vita-eabi-size "$O/libshepaudio_vita.a" | awk 'NR>1{t+=$1;d+=$2;b+=$3} END{printf "vita lib: text %d data %d bss %d bytes\n",t,d,b}'
  echo "vita: $O/libshepaudio_vita.a  (link check: $O/linkcheck.velf)"
}

case "$what" in
  mac) mac ;; vita) vita ;; san) san ;;
  all) mac; vita; san ;;
  *) echo "usage: $0 [mac|vita|san|all]"; exit 2 ;;
esac
