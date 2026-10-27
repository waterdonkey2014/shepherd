#!/bin/zsh
# SHEPHERD-VITA LLVM: link-check a Vita eboot from the AOT objects of vita/llvm/aot-vita.sh (no VPK; nothing here is shipped).
# usage: link-vita.sh <set> <variant>      (set: hello|engine, variant: nollvm|arm|thumb)
#   hello  = vita/hello's eboot recipe (host + tables + shim + libmonosgen + 11 AOT modules)
#   engine = the headless engine-AOT recipe of vita/shepherd/build.sh (SHEPHERD_AOT_ENGINE=1, SHPD00011): 13 AOT modules. It is
#            bound to the owner's DLL MVIDs, so it stays on the build volume (like the existing engine-AOT VPK).
# The host objects, the static P/Invoke tables, the shim and the runtime archives are SNAPSHOTS (inputs/link-<set>,
# inputs/runtime-libs, taken 2026-10-07 from hello-v5/obj, shepherd-v6-engineaot/obj and obj/vita-runtime), so the three variants
# differ only in their AOT objects. Writes $L/out/<set>-<variant>/: <name>.elf/.velf/.map, eboot.bin (vita-make-fself -c),
# checks.txt (undefined symbols, process params, AOT module symbols, global-symbol clashes, veneers, relocation types, sections).
set -euo pipefail
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="$HOME/vitasdk/bin:$PATH"
L=${SHEPHERD_LLVM_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm}
SET=$1; VAR=$2
A=$L/aot/$SET-$VAR; O=$L/out/$SET-$VAR; I=$L/inputs; mkdir -p $O
RL=$I/runtime-libs
MONO_LIBS=($RL/libmonosgen-2.0.a $RL/libmono-component-marshal-ilgen-static.a $RL/libmono-component-debugger-stub-static.a
           $RL/libmono-component-hot_reload-stub-static.a $RL/libmono-component-diagnostics_tracing-stub-static.a)
STUBS=(-lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceKernelModulemgr_stub -lSceSysmem_stub -lSceIofilemgr_stub
       -lSceRtc_stub -lSceDisplay_stub -lSceCtrl_stub -lSceProcessmgr_stub)
case $SET in
  hello)  LK=$I/link-hello;  NAME=shepherd_hello; HOST=($LK/host.o $LK/hello_tables.o $LK/debugScreen.o); EXTRA=()
          MODS=(System.Private.CoreLib System.Runtime System.Console System.Collections System.Threading System.Threading.Thread
                System.Runtime.InteropServices Microsoft.Win32.Primitives System.Memory System.Collections.Concurrent Hello) ;;
  engine) LK=$I/link-engine; NAME=shepherd_menu_aot; HOST=($LK/host.o $LK/shepherd_tables.o $LK/debugScreen.o); EXTRA=(-lz)
          MODS=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
                System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks
                Microsoft.Bcl.AsyncInterfaces Shepherd.Engine) ;;
  *) echo "set: hello|engine"; exit 2 ;;
esac
AOT_OBJS=()
for m in $MODS; do
  [ -f $A/$m.dll.o ] || { echo "missing $A/$m.dll.o: run aot-vita.sh $SET $VAR"; exit 1; }
  AOT_OBJS+=($A/$m.dll.o); [ -f $A/$m-llvm.o ] && AOT_OBJS+=($A/$m-llvm.o)
done
arm-vita-eabi-gcc -Wl,-q -Wl,--gc-sections -Wl,--undefined=sceUserMainThreadStackSize -Wl,-Map=$O/$NAME.map -o $O/$NAME.elf \
  $HOST $AOT_OBJS -Wl,--start-group $MONO_LIBS $LK/libSystem.Native.vita.a -Wl,--end-group \
  $EXTRA -lpthread -lm -lc $STUBS 2> $O/link.log \
  || { grep -E "undefined reference|error|multiple definition" $O/link.log | sed 's/.*undefined reference to/undefined:/' | sort | uniq -c | sort -rn | head -40; exit 1; }
vita-elf-create -v $O/$NAME.elf $O/$NAME.velf > $O/velf.log 2>&1 || { tail -20 $O/velf.log; exit 1; }
vita-make-fself -c $O/$NAME.velf $O/eboot.bin > /dev/null
{
  echo "# $SET-$VAR link check ($(date -u +%Y-%m-%dT%H:%MZ))"
  echo "link warnings (minus RWX LOAD): $(grep -i warning $O/link.log | grep -vc 'RWX permissions' || true)"
  grep -i warning $O/link.log | grep -v 'RWX permissions' | head -5 || true
  arm-vita-eabi-nm $O/$NAME.elf > $O/elf-symbols.txt
  arm-vita-eabi-nm -u $O/$NAME.elf > $O/elf-undefined.txt || true
  echo "nm -u: $(wc -l < $O/elf-undefined.txt | tr -d ' ') undefined, strong: $(awk '$1=="U"' $O/elf-undefined.txt | wc -l | tr -d ' ')"
  for s in sceUserMainThreadStackSize _newlib_heap_size_user _pthread_stack_default_user; do
    grep -qE " [DdRr] $s\$" $O/elf-symbols.txt && echo "process param $s: present" || echo "process param $s: MISSING"
  done
  for m in $MODS; do
    s=mono_aot_module_${${m//./_}//-/_}_info
    grep -qE " [DdRrTt] $s\$" $O/elf-symbols.txt && echo "aot module symbol $s: defined" || echo "aot module symbol $s: MISSING"
  done
  # global (exported) symbols the AOT objects define: anything defined in two objects would be a clash (ld would have failed),
  # weak ones are listed; the LLVM part should export nothing but the Mono-visible entry points
  for f in $AOT_OBJS; do arm-vita-eabi-nm -g --defined-only $f | awk -v f=${f:t} '{print f"\t"$2"\t"$3}'; done > $O/aot-globals.tsv
  echo "AOT objects' global defined symbols: $(wc -l < $O/aot-globals.tsv | tr -d ' ') (weak: $(awk -F'\t' '$2 ~ /^[WwVv]$/' $O/aot-globals.tsv | wc -l | tr -d ' '); by kind: $(awk -F'\t' '{print $2}' $O/aot-globals.tsv | sort | uniq -c | tr '\n' ' '))"
  awk -F'\t' '$2 ~ /^[WwVv]$/ {print "  weak: "$1" "$3}' $O/aot-globals.tsv | sort -u -k3 | head -5
  # long-branch veneers (__<sym>_veneer) and ARM<->Thumb interworking stubs (__<sym>_from_thumb / _from_arm); the ones that
  # target a symbol defined by an AOT object mean AOT code crossed the +-32 MiB (A32) / +-16 MiB (Thumb-2) branch reach
  awk -F'\t' '{print $3}' $O/aot-globals.tsv | sort -u > $O/aot-global-names.txt
  grep -E '_veneer$|_from_thumb$|_from_arm$' $O/elf-symbols.txt | awk '{print $3}' | sed -E 's/^__//; s/_(veneer|from_thumb|from_arm)$//' | sort > $O/veneer-targets.txt || true
  echo "linker veneers + interworking stubs: $(wc -l < $O/veneer-targets.txt | tr -d ' ') (to AOT-defined symbols: $(comm -12 $O/veneer-targets.txt $O/aot-global-names.txt | wc -l | tr -d ' '); _from_thumb: $(grep -c '_from_thumb$' $O/elf-symbols.txt || true))"
  echo "relocations (ELF, -q):"
  arm-vita-eabi-readelf -rW $O/$NAME.elf | awk '$3 ~ /^R_ARM_/ {print $3}' | sort | uniq -c | sort -rn | sed 's/^/  /'
  echo "sections:"; arm-vita-eabi-size -A $O/$NAME.elf | grep -E "^\.(text|rodata|data|bss|ARM)" | sed 's/^/  /'
  arm-vita-eabi-size $O/$NAME.elf | tail -1 | awk '{printf "text/data/bss: %d / %d / %d\n", $1, $2, $3}'
  echo "eboot.bin (compressed fself): $(stat -f %z $O/eboot.bin) bytes"
  echo "vita-elf-create: accepted (exit 0); $(grep -c 'type R_ARM_' $O/velf.log || true) relocations listed; imports: $(grep -c 'stub=0x' $O/velf.log || true) function stubs"
} > $O/checks.txt
cat $O/checks.txt
