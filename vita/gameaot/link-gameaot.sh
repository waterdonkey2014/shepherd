#!/bin/zsh
# SHEPHERD-VITA game-AOT study: LINK CHECK of an owner-local eboot = the engine-AOT eboot (13 modules, vita/llvm/aot-vita.sh
# engine <variant>, compiled from this study's own input snapshot) + owner modules compiled by aot-module.sh, each registered as
# its own AOT module. Nothing here is shipped or run; the output is owner-local (compiled game / Unity code) and stays on the
# build volume. The host objects, the shim and the runtime archives are the LLVM study's snapshots (${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm/
# inputs/{link-engine,runtime-libs}), so only the AOT objects differ from that study's engine-AOT link.
# usage: link-gameaot.sh <variant: nollvm|thumb> <name> <module list file | "-" for none>
#   -> $GA_OUT/link/<variant>-<name>/: shepherd_menu_gameaot.elf/.velf/.map, eboot.bin, checks.txt, shepherd_tables_gameaot.c
# The registration table is build.sh's generated shepherd_tables.c with the owner modules appended (the order build.sh uses:
# the engine set first). UnityEngine.CoreModule comes from aot-<variant>-gsvtfix when aot-<variant> has no object for it.
# GA_AOT_TAG=<tag>: owner objects from aot-<variant>-<tag> instead (e.g. partialni = profile-only partial AOT).
set -euo pipefail
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="$HOME/vitasdk/bin:$PATH"
HERE=${0:A:h}; REPO=${HERE:h:h}
GO=${GA_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/sizes}
case $GO in "$REPO"/*|"${REPO:A}"/*) echo "refusing: owner-local output inside the repo ($GO)"; exit 1 ;; esac
VAR=$1; NAME=$2; LIST=$3
E=${GA_ENGSET:-$GO/engset}; A=$E/aot/engine-$VAR; I=${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm/inputs; LK=$I/link-engine; RL=$I/runtime-libs
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
O=$GO/link/$VAR-$NAME; rm -rf $O; mkdir -p $O
ENG=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory System.ObjectModel
     System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces Shepherd.Engine)
OWN=(); [ "$LIST" != - ] && OWN=($(grep -v '^#' $LIST | awk 'NF {print $1}'))
OBJS=()
for m in $ENG; do
  [ -f $A/$m.dll.o ] || { echo "missing $A/$m.dll.o (vita/llvm/aot-vita.sh engine $VAR with SHEPHERD_LLVM_DIR=$E)"; exit 1; }
  OBJS+=($A/$m.dll.o); [ -f $A/$m-llvm.o ] && OBJS+=($A/$m-llvm.o)
done
for m in $OWN; do
  d=$GO/aot-$VAR${GA_AOT_TAG:+-$GA_AOT_TAG}; [ -f $d/$m.dll.o ] || [ -n "${GA_AOT_TAG:-}" ] || d=$GO/aot-$VAR-gsvtfix
  [ -f $d/$m.dll.o ] || { echo "missing $d/$m.dll.o (aot-module.sh $VAR $m)"; exit 1; }
  OBJS+=($d/$m.dll.o); [ -f $d/$m-llvm.o ] && OBJS+=($d/$m-llvm.o)
done
sym() { print -r -- "mono_aot_module_${1//[^A-Za-z0-9_]/_}_info"; }
# the generated table: build.sh's, with every module (engine set + owner modules) registered
{
  sed -n '1,3p' $LK/shepherd_tables.c
  for m in $ENG $OWN; do echo "extern void *$(sym $m);"; done
  echo "void mono_aot_register_module (void **aot_info);"
  echo "void shepherd_register_aot_modules (void) {"
  for m in $ENG $OWN; do echo "\tmono_aot_register_module ($(sym $m));"; done
  echo "}"
  echo "const char *const shepherd_aot_module_names [] = {"; for m in $ENG $OWN; do echo "\t\"$m\","; done; echo "\tNULL };"
  sed -n '/^extern void BrotliDecoderCreateInstance/,$p' $LK/shepherd_tables.c
} > $O/shepherd_tables_gameaot.c
mkdir -p $O/include/mono/utils; cp $R/src/mono/mono/utils/mono-vita.h $O/include/mono/utils/
arm-vita-eabi-gcc -O2 -g0 -Wall -ffunction-sections -fdata-sections -I$O/include -I$R/src/native/public -c $O/shepherd_tables_gameaot.c -o $O/shepherd_tables_gameaot.o
MONO_LIBS=($RL/libmonosgen-2.0.a $RL/libmono-component-marshal-ilgen-static.a $RL/libmono-component-debugger-stub-static.a
           $RL/libmono-component-hot_reload-stub-static.a $RL/libmono-component-diagnostics_tracing-stub-static.a)
STUBS=(-lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceKernelModulemgr_stub -lSceSysmem_stub -lSceIofilemgr_stub
       -lSceRtc_stub -lSceDisplay_stub -lSceCtrl_stub -lSceProcessmgr_stub)
EL=$O/shepherd_menu_gameaot.elf
t0=$(date +%s)
if ! arm-vita-eabi-gcc -Wl,-q -Wl,--gc-sections -Wl,--undefined=sceUserMainThreadStackSize -Wl,-Map=$O/link.map -o $EL \
  $LK/host.o $O/shepherd_tables_gameaot.o $LK/debugScreen.o $OBJS -Wl,--start-group $MONO_LIBS $LK/libSystem.Native.vita.a -Wl,--end-group \
  -lz -lpthread -lm -lc $STUBS 2> $O/link.log; then
  echo "LINK FAILED ($(( $(date +%s) - t0 )) s):"
  grep -E "undefined reference|error|truncated|multiple definition|out of range" $O/link.log | sed -E 's/.*(relocation truncated to fit: [A-Z0-9_]+).*/\1/; s/.*undefined reference to/undefined:/' | sort | uniq -c | sort -rn | head -20 | tee $O/link-fail.txt
  exit 1
fi
echo "link: $(( $(date +%s) - t0 )) s"
t1=$(date +%s)
vfail=0; vita-elf-create -v $EL $O/shepherd_menu_gameaot.velf > $O/velf.log 2>&1 || vfail=1
[ $vfail = 0 ] && vita-make-fself -c $O/shepherd_menu_gameaot.velf $O/eboot.bin > /dev/null
{
  echo "# game-AOT link check $VAR-$NAME ($(date -u +%Y-%m-%dT%H:%MZ)): ${#ENG} engine-set + ${#OWN} owner modules"
  echo "link warnings (minus RWX LOAD): $(grep -i warning $O/link.log | grep -vc 'RWX permissions' || true)"
  arm-vita-eabi-nm $EL > $O/elf-symbols.txt
  arm-vita-eabi-nm -u $EL > $O/elf-undefined.txt || true
  echo "nm -u: $(wc -l < $O/elf-undefined.txt | tr -d ' ') undefined, strong: $(awk '$1=="U"' $O/elf-undefined.txt | wc -l | tr -d ' ')"
  miss=0; for m in $ENG $OWN; do grep -qE " [DdRrTt] $(sym $m)\$" $O/elf-symbols.txt || { echo "aot module symbol $(sym $m): MISSING"; miss=1; }; done
  echo "aot module info symbols: $([ $miss = 0 ] && echo "all $(( ${#ENG} + ${#OWN} )) defined" || echo MISSING)"
  echo "veneers / interworking stubs: $(grep -cE '_veneer$|_from_thumb$|_from_arm$' $O/elf-symbols.txt || true) (long-branch veneers: $(grep -cE '_veneer$' $O/elf-symbols.txt || true))"
  echo "relocations (ELF, -q):"
  arm-vita-eabi-readelf -rW $EL | awk '$3 ~ /^R_ARM_/ {print $3}' | sort | uniq -c | sort -rn | sed 's/^/  /' | tee $O/reloc-types.txt
  echo "sections:"; arm-vita-eabi-size -A $EL | grep -E "^\.(text|rodata|data|bss)" | sed 's/^/  /'
  arm-vita-eabi-size $EL | tail -1 | awk '{printf "text/data/bss: %d / %d / %d  -> loaded %d (%.2f MiB)\n", $1, $2, $3, $1+$2+$3, ($1+$2+$3)/1048576}'
  if [ $vfail = 0 ]; then
    echo "vita-elf-create: accepted ($(( $(date +%s) - t1 )) s); eboot.bin (compressed fself): $(stat -f %z $O/eboot.bin) bytes"
  else
    echo "vita-elf-create: REJECTED: $(grep -iE 'error|fail|unsupported|too' $O/velf.log | head -3 | tr '\n' ' ')"
  fi
} > $O/checks.txt
cat $O/checks.txt
rm -f $O/shepherd_menu_gameaot.velf
