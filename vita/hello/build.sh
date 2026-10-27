#!/bin/zsh
# SHEPHERD-VITA V5: builds vita/hello/out/shepherd_hello.vpk -- .NET 8 Mono (forked v8.0.31) hello world for the PS Vita.
#   managed IL (dotnet build) -> static full+interp AOT (mono-aot-cross, armv7-vita-eabihf) -> arm-vita-eabi-as
#   -> link with libmonosgen-2.0.a (HOST_VITA) + the System.Native shim + host.c -> vita-elf-create -> fself -> VPK.
# Prerequisites (see vita/notes/v5-runtime.md): build volume attached, runtime fork configured + built
# (vita/toolchain/build-runtime-vita.sh), mono-aot-cross built (vita/toolchain/build-cross.sh).
# Nothing is downloaded. HelloInterp.dll is deliberately NOT AOT-compiled (it exercises the interpreter).
# SHEPHERD_DIAG=1 (devfail1, DIAG-ONLY): "Shepherd Hello Diag" (SHPD00015, shepherd_hello_diag.vpk) -- the same recipe plus
#   vita/hello/diag (HelloDiag.dll AOT + HelloDiagInterp.dll interpreted: fine-grained exception/resource/GC tests, then Hello's
#   M1-M99), host.c built with HELLO_DIAG, linked with the DIAGNOSTIC runtime (SHEPHERD_VITA_RUNTIME_DIAG_OBJ, from
#   vita/toolchain/build-runtime-diag.sh build) and its own icon (the LiveArea set and the VPK gate, hello/diag/check_diag_vpk.py,
#   are every build's since the integration branch). Work/out dirs
#   default to $B/hello-diag(/out), never the normal ones. Without SHEPHERD_DIAG nothing below changes.
set -euo pipefail
HERE=${0:a:h}
V=${HERE:h}                                         # vita/
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="$HOME/vitasdk/bin:$PATH"
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
B=${SHEPHERD_WORK:-$HOME/shepherd-work}
R=${SHEPHERD_RUNTIME:-$B/runtime}
RT=${SHEPHERD_VITA_RUNTIME_OBJ:-$B/obj/vita-runtime}
X=${SHEPHERD_CROSS_OBJ:-$B/obj/cross-vita}/out/bin/mono-aot-cross
PACK=$B/packs/mono.linux-arm.8.0.31/runtimes/linux-arm
W=${SHEPHERD_HELLO_WORK:-$B/hello-v5}               # big intermediates (.s files) stay on the build volume
OUT=${SHEPHERD_HELLO_OUT:-$HERE/out}
SHIM=$V/runtime/system-native
COMMON=$VITASDK/share/gcc-arm-vita-eabi/samples/common
TITLE_ID=SHPD00005
DIAG=${SHEPHERD_DIAG:-0}
if [ "$DIAG" = 1 ]; then
  RT=${SHEPHERD_VITA_RUNTIME_DIAG_OBJ:?SHEPHERD_DIAG=1 links the diagnostic runtime: set SHEPHERD_VITA_RUNTIME_DIAG_OBJ (vita/toolchain/build-runtime-diag.sh build)}
  W=${SHEPHERD_HELLO_WORK:-$B/hello-diag}; OUT=${SHEPHERD_HELLO_OUT:-$W/out}
  case "$W" in "$B/hello-v5"|"$B/hello-v5/"*) echo "SHEPHERD_DIAG=1 must not use the normal work dir $W"; exit 1 ;; esac
  TITLE_ID=SHPD00015
  arm-vita-eabi-nm "$RT/mono/mini/libmonosgen-2.0.a" 2>/dev/null | grep -q " T shepherd_vita_diag_exc_fail$" || { echo "$RT is not the diagnostic runtime (no shepherd_vita_diag_* hooks)"; exit 1; }
fi
[ -d "$R/src/mono" ] || { echo "build volume not attached: hdiutil attach \"$HOME/shepherd-build.sparsebundle\""; exit 1; }
[ -x "$X" ] || { echo "mono-aot-cross missing: run vita/toolchain/build-cross.sh"; exit 1; }
[ -f "$RT/mono/mini/libmonosgen-2.0.a" ] || { echo "runtime missing: run vita/toolchain/build-runtime-vita.sh"; exit 1; }
mkdir -p "$W/bcl" "$W/aot" "$W/obj" "$OUT"

# AOT-compiled assemblies (Hello's transitive closure) and the packaged set (+ the interpreted HelloInterp)
AOT=(System.Private.CoreLib System.Runtime System.Console System.Collections System.Threading System.Threading.Thread
     System.Runtime.InteropServices Microsoft.Win32.Primitives System.Memory System.Collections.Concurrent Hello)
INTERP=(HelloInterp)
[ "$DIAG" = 1 ] && { AOT+=(HelloDiag); INTERP+=(HelloDiagInterp); }
# [AggressiveInlining] cap in IL bytes (method-to-ir.c, SHEPHERD-VITA): keeps CoreLib's A32 text < 28 MiB
export MONO_AGGRESSIVE_INLINELIMIT=${MONO_AGGRESSIVE_INLINELIMIT:-256}   # 128: 23.49 MiB, 256: 23.76, 512: 26.13, uncapped: 28.54 (too big)
MAX_MODULE_TEXT=$((28 * 1024 * 1024))

echo "== 1. managed IL"
( cd "$HERE/managed" && dotnet build -c Release -o "$OUT/managed-il" -p:DisableTransitiveFrameworkReferenceDownloads=true -nologo -v q )
[ "$DIAG" = 1 ] && ( cd "$HERE/diag" && dotnet build HelloDiag.csproj -c Release -o "$OUT/managed-il" -p:DisableTransitiveFrameworkReferenceDownloads=true -nologo -v q )
rm -rf "$W/bcl"; mkdir -p "$W/bcl"                  # no DLLs left over from older runs (the GUID gate below reads them all)
cp "$PACK"/lib/net8.0/*.dll "$W/bcl/"
cp "$PACK/native/System.Private.CoreLib.dll" "$W/bcl/"
cp "$OUT"/managed-il/Hello.dll "$OUT"/managed-il/HelloInterp.dll "$W/bcl/"
[ "$DIAG" = 1 ] && cp "$OUT"/managed-il/HelloDiag.dll "$OUT"/managed-il/HelloDiagInterp.dll "$W/bcl/"

echo "== 2. static AOT (full + interp entry wrappers, direct P/Invokes and icalls, no debug info)"
AOT_OPTS="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,mtriple=armv7-vita-eabihf"
# Every runtime trampoline comes from CoreLib's image (get_numerous_trampoline -> get_mscorlib_aot_module) and running
# out of a pool is fatal (g_error "Ran out of trampolines of type N"). The defaults (specific 4096, static-rgctx 4096,
# imt 512, gsharedvt-arg 512, ftnptr-arg 128, unbox-arbitrary 256) are sized for small apps; on ARM each interpreted
# method that AOT/native code needs a pointer to costs one FTNPTR_ARG trampoline. A32 sizes: specific 20 B,
# static-rgctx 24, imt 72, gsharedvt-arg 24, ftnptr-arg 32 (+1-2 GOT words each in .bss): ~0.5 MiB text in all.
# The rgctx-fetch pool (128 B per slot, x2) stays at its default 128: it is NOT fatal (past the limit
# mono_aot_get_lazy_fetch_trampoline uses rgctx_fetch_trampoline_general plus one specific + one static-rgctx
# trampoline from the pools above), so raising it would only buy ~230 KiB of text.
CORELIB_TRAMPS=",ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
# Up-to-date stamps hash CONTENT (no paths) of the cross compiler and of every DLL the module may depend on: an AOT
# image records the GUIDs of its dependencies (image_table) and the device refuses it (g_error in aot-only mode) when
# one differs. CoreLib references no other assembly, so its stamp covers only itself (and stays cached when Hello or
# HelloInterp change); every other module's stamp covers the whole closure. The GUID gate below backs this up.
DEPS_ALL=$(cat "$W"/bcl/*.dll "$X" | shasum -a 256 | cut -c1-16)
DEPS_CORELIB=$(cat "$W/bcl/System.Private.CoreLib.dll" "$X" | shasum -a 256 | cut -c1-16)
for a in $AOT; do
  dll="$W/bcl/$a.dll"; s="$W/aot/$a.dll.s"; o="$W/aot/$a.dll.o"
  if [ $a = System.Private.CoreLib ]; then EXTRA=$CORELIB_TRAMPS DEPS=$DEPS_CORELIB; else EXTRA= DEPS=$DEPS_ALL; fi
  stamp="$W/aot/$a.stamp"; want="$DEPS opts=$AOT_OPTS$EXTRA inl=$MONO_AGGRESSIVE_INLINELIMIT"
  if [ -f "$o" ] && [ "$(cat "$stamp" 2>/dev/null)" = "$want" ]; then echo "   $a: up to date"; continue; fi
  rm -f "$stamp"
  MONO_PATH="$W/bcl" "$X" --aot=$AOT_OPTS$EXTRA,outfile="$s" "$dll" > "$W/aot/$a.log" 2>&1 \
    || { tail -20 "$W/aot/$a.log"; exit 1; }
  arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o "$o" "$s"
  echo "$want" > "$stamp"
  echo "   $a: $(grep -o 'Compiled: [0-9/]*' "$W/aot/$a.log" | tail -1)"
done
# GUID gate: each AOT image's own assembly_guid and every image_table GUID must equal the MVID of the DLL that is
# packaged; a mismatch would only show on the device (out-of-date module -> g_error right after H10).
python3 "$HERE/check_aot_guids.py" "$W/bcl" "$W/aot" > "$OUT/aot-guids.txt" || true
grep -qx 'TOTAL MISMATCHES 0' "$OUT/aot-guids.txt" || { cat "$OUT/aot-guids.txt"; echo "AOT images do not match the packaged DLLs (see aot-guids.txt)"; exit 1; }
echo "   AOT GUID gate: $(tail -1 "$OUT/aot-guids.txt")"
# M4b only reaches the run-time gsharedvt transitions while Hello's image has no specific instance of the probes
if grep -qE '^ShepherdVita_AotGsvtProbe_(Pick|Mix|Combine)_(single|double|ShepherdVita_V2)_' "$W/aot/Hello.dll.s" ||
   ! grep -q '^ShepherdVita_AotGsvtProbe_Pick_T_GSHAREDVT_' "$W/aot/Hello.dll.s"; then
  echo "   note: Hello.dll.s has specific AotGsvtProbe instances (or no gsharedvt body): M4b no longer tests run-time gsharedvt"
else
  echo "   M4b probes: gsharedvt bodies only (run-time transitions will be exercised)"
fi
: > "$OUT/aot-sizes.txt"
for a in $AOT; do
  t=$(arm-vita-eabi-size "$W/aot/$a.dll.o" | tail -1 | awk '{print $1}')
  printf "%-36s text %10d (%6.2f MiB)\n" "$a" "$t" "$(echo "$t / 1048576" | bc -l)" >> "$OUT/aot-sizes.txt"
  [ "$t" -lt $MAX_MODULE_TEXT ] || { echo "AOT module $a text $t >= 28 MiB (bl reach margin)"; exit 1; }
done
cat "$OUT/aot-sizes.txt"

echo "== 3. native symbols the AOT code needs"
for a in $AOT; do arm-vita-eabi-nm -u "$W/aot/$a.dll.o"; done | awk '{print $2}' | sort -u > "$W/obj/aot-undefined.txt"
grep -E '^(GlobalizationNative_)' "$W/obj/aot-undefined.txt" > "$W/obj/globalization.txt" || true
{
  echo "/* GENERATED by vita/hello/build.sh: GlobalizationNative_* entry points referenced by the AOT code."
  echo " * The Vita build is InvariantGlobalization=true, so none of these may ever run; each logs once and fails. */"
  echo "#include <stdint.h>"
  echo "void shepherd_vita_native_log (const char *msg);"
  echo "static void once (int *done, const char *name) { if (!*done) { *done = 1; shepherd_vita_native_log (name); } }"
  while read -r s; do
    echo "int32_t $s (void) { static int d; once (&d, \"$s called with InvariantGlobalization (unsupported)\"); return 0; }"
  done < "$W/obj/globalization.txt"
} > "$W/obj/vita_globalization_stubs.c"

echo "== 4. System.Native shim"
sed -e 's/^#cmakedefine01 \([A-Z0-9_]*\)/#define \1 0/' "$R/src/native/libs/Common/pal_config.h.in" > "$W/obj/pal_config.h"
printf '#undef HAVE_GNU_STRERROR_R\n#define HAVE_GNU_STRERROR_R 1\n#undef HAVE_STRLCPY\n#define HAVE_STRLCPY 1\n' >> "$W/obj/pal_config.h"
# the prefix maps keep checkout/work paths out of __FILE__ (asserts in the upstream pal headers), as configure-vita.sh does
CFLAGS=(-O2 -g0 -Wall -Wno-unused-function -ffunction-sections -fdata-sections -D_GNU_SOURCE -DTARGET_UNIX -D__vita__
        -ffile-prefix-map="$R"=/shepherd-runtime -ffile-prefix-map="$W"=/shepherd-hello-work -ffile-prefix-map="$V"=/shepherd-vita)
NATIVE_INC=(-I"$W/obj" -I"$R/src/native/libs/System.Native" -I"$R/src/native/libs/Common" -I"$R/src/native")
arm-vita-eabi-gcc $CFLAGS $NATIVE_INC -c "$SHIM/pal_vita.c" -o "$W/obj/pal_vita.o"
arm-vita-eabi-gcc $CFLAGS -c "$SHIM/pal_vita_misc.c" -o "$W/obj/pal_vita_misc.o"
# the corrected sem_post for vitasdk's pthread-embedded (vita_pthread_fix.c: its own posts the kernel semaphore even with no waiter)
arm-vita-eabi-gcc $CFLAGS -c "$SHIM/vita_pthread_fix.c" -o "$W/obj/vita_pthread_fix.o"
arm-vita-eabi-gcc $CFLAGS -c "$W/obj/vita_globalization_stubs.c" -o "$W/obj/vita_globalization_stubs.o"
rm -f "$W/obj/libSystem.Native.vita.a"
arm-vita-eabi-ar rcs "$W/obj/libSystem.Native.vita.a" "$W/obj/pal_vita.o" "$W/obj/pal_vita_misc.o" "$W/obj/vita_pthread_fix.o" "$W/obj/vita_globalization_stubs.o"

echo "== 5. generated tables (AOT module registration, static P/Invoke symbol table)"
# every AOT-referenced symbol that the shim or the host defines is reachable from the interpreter too
HOST_EXPORTS=(shepherd_hello_log shepherd_hello_add shepherd_hello_fmix shepherd_hello_call_back)
{ arm-vita-eabi-nm -g --defined-only "$W/obj/libSystem.Native.vita.a" | awk 'NF==3 && $2=="T" {print $3}'; printf '%s\n' $HOST_EXPORTS; } | sort -u > "$W/obj/static-candidates.txt"
grep -E '^(SystemNative_|GlobalizationNative_|corehost_|shepherd_hello_)' "$W/obj/static-candidates.txt" > "$W/obj/static-symbols.txt"
{
  echo "/* GENERATED by vita/hello/build.sh */"
  echo "#include <stddef.h>"
  echo "#include <mono/utils/mono-vita.h>"
  for a in $AOT; do echo "extern void *mono_aot_module_${a//./_}_info;"; done
  echo "void mono_aot_register_module (void **aot_info);"
  echo "void shepherd_register_aot_modules (void) {"
  for a in $AOT; do echo "\tmono_aot_register_module (mono_aot_module_${a//./_}_info);"; done
  echo "}"
  echo "const char *const shepherd_aot_module_names [] = {"; for a in $AOT; do echo "\t\"$a\","; done; echo "\tNULL };"
  echo "const char *const shepherd_managed_files [] = {"; for a in $AOT $INTERP; do echo "\t\"$a.dll\","; done; echo "\tNULL };"
  # declared as functions so the linker keeps the Thumb bit in the addresses (all entries are code)
  while read -r s; do echo "extern void $s (void);"; done < "$W/obj/static-symbols.txt"
  echo "const MonoVitaStaticSymbol shepherd_static_symbols [] = {"
  while read -r s; do echo "\t{ \"$s\", (void *) $s },"; done < "$W/obj/static-symbols.txt"
  echo "};"
  echo "const int shepherd_static_symbols_count = sizeof (shepherd_static_symbols) / sizeof (shepherd_static_symbols [0]);"
} > "$W/obj/hello_tables.c"
mkdir -p "$W/obj/include/mono/utils"
cp "$R/src/mono/mono/utils/mono-vita.h" "$W/obj/include/mono/utils/"
MONO_INC=(-I"$W/obj/include" -I"$R/src/native/public")
arm-vita-eabi-gcc $CFLAGS $MONO_INC -c "$W/obj/hello_tables.c" -o "$W/obj/hello_tables.o"
BUILD_ID=${SHEPHERD_BUILD_ID:-"$(date -u +%Y%m%d-%H%M%S)-$(cd "$R" && git rev-parse --short HEAD)"}   # SHEPHERD_BUILD_ID: a fixed id for byte-for-byte build comparisons
DIAG_DEFS=(); DIAG_OBJS=()
if [ "$DIAG" = 1 ]; then
  DIAG_DEFS=(-DHELLO_DIAG)
  # H0's reference: vitasdk's own (buggy) sem_post, renamed, so the self-test can show both on the device
  mkdir -p "$W/obj/stock-sem"
  ( cd "$W/obj/stock-sem" && arm-vita-eabi-ar x "$VITASDK/arm-vita-eabi/lib/libpthread.a" sem_post.o )
  arm-vita-eabi-objcopy --redefine-sym sem_post=shepherd_stock_sem_post "$W/obj/stock-sem/sem_post.o" "$W/obj/stock-sem/stock_sem_post.o"
  DIAG_OBJS=("$W/obj/stock-sem/stock_sem_post.o" -Wl,--wrap=sceKernelCreateThread)   # + the watchdog's thread list (host.c)
fi
arm-vita-eabi-gcc $CFLAGS $MONO_INC -I"$COMMON" $DIAG_DEFS -DHELLO_BUILD_ID="\"$BUILD_ID\"" -c "$HERE/host.c" -o "$W/obj/host.o"
arm-vita-eabi-gcc -O2 -I"$COMMON" -c "$COMMON/debugScreen.c" -o "$W/obj/debugScreen.o"

echo "== 6. link"
AOT_OBJS=(); for a in $AOT; do AOT_OBJS+=("$W/aot/$a.dll.o"); done
MONO_LIBS=("$RT/mono/mini/libmonosgen-2.0.a" "$RT/mono/mini/libmono-component-marshal-ilgen-static.a"
           "$RT/mono/mini/libmono-component-debugger-stub-static.a" "$RT/mono/mini/libmono-component-hot_reload-stub-static.a"
           "$RT/mono/mini/libmono-component-diagnostics_tracing-stub-static.a")
# sceUserMainThreadStackSize is read by vita-elf-create by name and referenced by no code: without --undefined,
# --gc-sections drops it and the main thread silently gets the system default stack.
arm-vita-eabi-gcc -Wl,-q -Wl,--gc-sections -Wl,--undefined=sceUserMainThreadStackSize -Wl,-Map="$W/obj/hello.map" -o "$W/obj/shepherd_hello.elf" \
  "$W/obj/host.o" $DIAG_OBJS "$W/obj/hello_tables.o" "$W/obj/debugScreen.o" $AOT_OBJS \
  -Wl,--start-group $MONO_LIBS "$W/obj/libSystem.Native.vita.a" -Wl,--end-group \
  -lpthread -lm -lc \
  -lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceKernelModulemgr_stub -lSceSysmem_stub -lSceIofilemgr_stub \
  -lSceRtc_stub -lSceDisplay_stub -lSceCtrl_stub -lSceProcessmgr_stub -lScePower_stub 2> "$W/obj/link.log" \
  || { grep -E "undefined reference|error" "$W/obj/link.log" | sed 's/.*undefined reference to/undefined:/' | sort | uniq -c | sort -rn | head -60; exit 1; }
grep -i "warning" "$W/obj/link.log" | grep -v "has a LOAD segment with RWX permissions" | head -10 || true
# process parameters that must survive --gc-sections (vita-elf-create looks them up by name)
arm-vita-eabi-nm "$W/obj/shepherd_hello.elf" > "$W/obj/elf-symbols.txt"   # (no nm | grep -q: SIGPIPE + pipefail)
for sym in sceUserMainThreadStackSize _newlib_heap_size_user _pthread_stack_default_user; do
  grep -qE " [DdRr] $sym\$" "$W/obj/elf-symbols.txt" || { echo "$sym missing from the ELF (discarded by --gc-sections?)"; exit 1; }
done
MTS=$(awk '$3=="sceUserMainThreadStackSize"{print $1}' "$W/obj/elf-symbols.txt")
MTS_VAL=$(arm-vita-eabi-objdump -s --start-address=0x$MTS --stop-address=$(printf '0x%x' $((0x$MTS + 4))) "$W/obj/shepherd_hello.elf" \
  | awk '/^ [0-9a-f]+ /{print $2; exit}' | sed -E 's/(..)(..)(..)(..)/\4\3\2\1/')
echo "   main thread stack (sceUserMainThreadStackSize): $((16#$MTS_VAL / 1024)) KiB"
arm-vita-eabi-size -A "$W/obj/shepherd_hello.elf" | grep -E "^\.(text|rodata|data|bss)|Total" > "$OUT/elf-sections.txt"
arm-vita-eabi-size "$W/obj/shepherd_hello.elf" | tee "$OUT/elf-size.txt"

echo "== 7. SELF + VPK"
vita-elf-create "$W/obj/shepherd_hello.elf" "$W/obj/shepherd_hello.velf" > "$W/obj/velf.log" 2>&1 || { tail -20 "$W/obj/velf.log"; exit 1; }
vita-make-fself -c "$W/obj/shepherd_hello.velf" "$W/obj/eboot.bin"
# every VPK: the LiveArea set (8-bit indexed PNGs, vita/probe's known-good template/bg/startup), out/eboot.bin first, then a fresh
# .vpk (removed, packed again: never a stale one), then the VPK gate (LiveArea set, 8-bit PNGs, TITLE, eboot.bin == this build,
# managed/ == work/bcl). The diag build had this first (devfail1); since the integration branch the normal build does too.
VPK_FILE=shepherd_hello.vpk; VPK_TITLE="Shepherd Hello"; ICON=$HERE/sce_sys/icon0.png; VPK_REQ=(managed/Hello.dll)
if [ "$DIAG" = 1 ]; then
  # DIAG: own title, own icon
  VPK_FILE=shepherd_hello_diag.vpk; VPK_TITLE="Shepherd Hello Diag"; ICON=$W/obj/icon0.png
  VPK_REQ=(managed/HelloDiag.dll managed/HelloDiagInterp.dll managed/Hello.dll)
  python3 "$HERE/diag/make_diag_icon.py" "$W/obj/icon0.png" hello
else
  mkdir -p "$HERE/sce_sys"
  [ -f "$HERE/sce_sys/icon0.png" ] || python3 "$HERE/make_icon.py" "$HERE/sce_sys/icon0.png"
fi
vita-mksfoex -s TITLE_ID=$TITLE_ID -s APP_VER=01.00 -d ATTRIBUTE2=12 "$VPK_TITLE" "$W/obj/param.sfo"
LA=$V/probe/sce_sys/livearea/contents
PACKED=(); for a in $AOT $INTERP; do PACKED+=(-a "$W/bcl/$a.dll=managed/$a.dll"); done
cp "$W/obj/eboot.bin" "$OUT/eboot.bin"
rm -f "$OUT/$VPK_FILE"
vita-pack-vpk -s "$W/obj/param.sfo" -b "$W/obj/eboot.bin" $PACKED \
  -a "$HERE/managed/hello-data.txt=managed/hello-data.txt" -a "$ICON=sce_sys/icon0.png" \
  -a "$LA/template.xml=sce_sys/livearea/contents/template.xml" -a "$LA/bg.png=sce_sys/livearea/contents/bg.png" \
  -a "$LA/startup.png=sce_sys/livearea/contents/startup.png" "$OUT/$VPK_FILE" > /dev/null
python3 "$HERE/diag/check_diag_vpk.py" --bcl="$W/bcl" "$OUT/$VPK_FILE" "$W/obj/eboot.bin" $TITLE_ID "$VPK_TITLE" $VPK_REQ \
  > "$OUT/vpk-check.txt" || { cat "$OUT/vpk-check.txt"; exit 1; }
tail -1 "$OUT/vpk-check.txt"
[ "$DIAG" = 1 ] && { grep -E "^[[:space:]]*(warning|error|Warning|Error)[: ]" "$W/obj/velf.log" | head -5 || true; }
IL=0; for a in $AOT $INTERP; do IL=$((IL + $(stat -f %z "$W/bcl/$a.dll"))); done
{
  echo "build $BUILD_ID"
  echo "elf text/data/bss: $(arm-vita-eabi-size "$W/obj/shepherd_hello.elf" | tail -1 | awk '{print $1" / "$2" / "$3}')"
  echo "eboot.bin (compressed fself): $(stat -f %z "$OUT/eboot.bin") bytes"
  echo "$VPK_FILE: $(stat -f %z "$OUT/$VPK_FILE") bytes"
  echo "managed IL packaged (${#AOT} AOT + ${#INTERP} interpreted assemblies): $IL bytes"
  echo "static P/Invoke table: $(wc -l < "$W/obj/static-symbols.txt" | tr -d ' ') symbols"
} > "$OUT/build-info.txt"
cat "$OUT/build-info.txt"
ls -la "$OUT/$VPK_FILE" "$OUT/eboot.bin"
