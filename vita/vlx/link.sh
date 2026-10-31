#!/bin/zsh
# vita/vlx/link.sh: relinks the objects of a Vita app build -- the very objects that went into its eboot -- against the
# vlx shim instead of the SceKernel NID stubs, giving a static ARM32 Linux ELF for vita/vlx/run.sh.
#   link.sh hello [host.o] [out.elf]   objects of vita/hello/build.sh   (SHEPHERD_HELLO_WORK, default .../hello-v5)
#   link.sh menu  [host.o] [out.elf]   objects of vita/shepherd/build.sh (SHEPHERD_V6_WORK, default .../shepherd-v6; headless)
# A different host.o (e.g. a patched host.c compiled with the app's own flags) can stand in for the build's.
# env: VLX_OBJ (the shim, vita/vlx/build.sh), SHEPHERD_VITA_RUNTIME_OBJ (default: the runtime the build linked, <work>/obj/vita-runtime-obj.txt
#      written by build.sh since kit 4 -- the game-AOT builds link the gameaot runtime with fix A --, else ${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime)
#      the devfail1 diag apps: VLX_AOT_EXTRA (more AOT modules, e.g. HelloDiag), VLX_EXTRA_OBJS (more objects after host.o),
#      VLX_LDFLAGS (more link flags, e.g. @<work>/obj/wraps.rsp)
#      VLX_LLVM (SHEPHERD_AOT_LLVM builds): link each module's <M>-llvm.o right after its <M>.dll.o, the eboot's own order, so the
#      LLVM code sits next to its Mono object and the AOT trampolines stay in branch reach (passing the -llvm.o files through
#      VLX_EXTRA_OBJS instead put them all after host.o: ~9,800 veneers and a tramp-arm.c assertion at run time, an artifact of
#      the harness). auto (default) = on when the build's own link map names a -llvm.o; 1 = on (every -llvm.o must exist); 0 = off.
#      VLX_AOT_OWNER (kit #4, build.sh SHEPHERD_AOT_OWNER=1 builds: SHPD00095 / 00190): link the owner-local game modules' objects
#      ($W/aot-owner/<M>.dll.o) after the other AOT objects, in the eboot's order ($W/obj/aot-owner-modules.txt, written by build.sh).
#      auto (default) = on when that list exists; 0 = off (the build's shepherd_tables.o then lacks their symbols: an owner build needs them)
set -euo pipefail
HERE=${0:a:h}
export PATH="${VITASDK:-$HOME/vitasdk}/bin:$PATH" TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
B=${SHEPHERD_WORK:-$HOME/shepherd-work}; O=${VLX_OBJ:-$B/obj/vlx}
case $O in $B/devfail1*) echo "refusing a VLX_OBJ under devfail1: $O"; exit 1 ;; esac
APP=${1:?usage: link.sh hello|menu [host.o] [out.elf]}
case $APP in
  hello) W=${SHEPHERD_HELLO_WORK:-$B/hello-v5}; TABLES=$W/obj/hello_tables.o; EXTRA=()
         AOT=(System.Private.CoreLib System.Runtime System.Console System.Collections System.Threading System.Threading.Thread
              System.Runtime.InteropServices Microsoft.Win32.Primitives System.Memory System.Collections.Concurrent Hello) ;;
  menu)  W=${SHEPHERD_V6_WORK:-$B/shepherd-v6}; TABLES=$W/obj/shepherd_tables.o; EXTRA=(-lz)
         AOT=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
              System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces) ;;
  *) echo "usage: link.sh hello|menu [host.o] [out.elf]"; exit 2 ;;
esac
# kit #8 speed: a build that records its base AOT set (build.sh: $W/obj/aot-base-modules.txt -- the kit #8 BCL trim leaves four modules out) is
# relinked with exactly that set, in its order; older builds keep the list above
[[ $APP == menu && -f $W/obj/aot-base-modules.txt ]] && AOT=(${(f)"$(<$W/obj/aot-base-modules.txt)"})
AOT+=(${=VLX_AOT_EXTRA:-})
# the runtime archives: the ones the build linked (kit 4: a game-AOT eboot links the gameaot runtime with fix A; relinking it against the
# shared runtime ran the stock unbox-trampoline lookup on its partial images -- an assertion the eboot cannot hit)
RT=${SHEPHERD_VITA_RUNTIME_OBJ:-}
[[ -z $RT && $APP == menu && -f $W/obj/vita-runtime-obj.txt ]] && RT=$(<$W/obj/vita-runtime-obj.txt)
RT=${RT:-$B/obj/vita-runtime}
HOST=${2:-$W/obj/host.o}; OUT=${3:-$O/$APP-vlx.elf}
# kit #4 M2: a host.o with the GC.Collect governor is linked as the eboot is (--wrap of GC.Collect's icall); older ones as before
GCGOV_LD=(); arm-vita-eabi-nm "$HOST" 2>/dev/null | grep -q ' T __wrap_ves_icall_System_GC_InternalCollect$' && GCGOV_LD=(-Wl,--wrap=ves_icall_System_GC_InternalCollect)
# kit #10 boot track: a host.o with the boot screen's display wrap (k10_boot.inc.c) is linked as the eboot is (--wrap of sceDisplaySetFrameBuf;
# in a launcher run the launcher's own splash calls go through the wrapper too -- it passes them on: the boot screen starts after the splash)
# (grep without -q: with pipefail an early grep exit fails the pipeline through nm's SIGPIPE)
arm-vita-eabi-nm "$HOST" 2>/dev/null | grep ' T __wrap_sceDisplaySetFrameBuf$' > /dev/null && GCGOV_LD+=(-Wl,--wrap=sceDisplaySetFrameBuf)
[[ -f $O/vlx.o ]] || zsh "$HERE/build.sh"
case $APP in hello) MAP=$W/obj/hello.map ;; menu) MAP=$W/obj/shepherd_menu.map ;; esac
LLVM=${VLX_LLVM:-auto}
if [[ $LLVM == auto ]]; then LLVM=0; [[ -f $MAP ]] && grep -q -- '-llvm\.o' "$MAP" && LLVM=1; fi
[[ $LLVM == 1 && -n ${VLX_EXTRA_OBJS:-} && ${VLX_EXTRA_OBJS:-} == *-llvm.o* ]] && { echo "VLX_LLVM=1 links the -llvm.o objects itself: leave them out of VLX_EXTRA_OBJS"; exit 1; }
AOT_OBJS=(); for a in $AOT; do
  AOT_OBJS+=("$W/aot/$a.dll.o")
  if [[ $LLVM == 1 ]]; then [[ -f $W/aot/$a-llvm.o ]] || { echo "VLX_LLVM=1: $W/aot/$a-llvm.o missing"; exit 1; }; AOT_OBJS+=("$W/aot/$a-llvm.o"); fi
done
NOWNER=0
if [[ $APP == menu && ${VLX_AOT_OWNER:-auto} != 0 && -f $W/obj/aot-owner-modules.txt ]]; then
  for a in ${(f)"$(<$W/obj/aot-owner-modules.txt)"}; do
    [[ -f $W/aot-owner/$a.dll.o ]] || { echo "owner module object $W/aot-owner/$a.dll.o missing"; exit 1; }
    AOT_OBJS+=("$W/aot-owner/$a.dll.o"); NOWNER=$((NOWNER + 1))
  done
fi
# kit #8 (vita/shepherd/build.sh SHEPHERD_PHYS_LINK): a build that linked libshepphys left a copy in $W/obj -- relinked as the eboot links
# it (after the build's objects, with libsupc++ for operator new / delete). VLX_PHYS=0 leaves it out (the host's table then fails to link).
PHYS_LIBS=(); [[ $APP == menu && ${VLX_PHYS:-auto} != 0 && -f $W/obj/libshepphys_vita.a ]] && PHYS_LIBS=("$W/obj/libshepphys_vita.a" -lsupc++)
MONO_LIBS=("$RT/mono/mini/libmonosgen-2.0.a" "$RT/mono/mini/libmono-component-marshal-ilgen-static.a"
           "$RT/mono/mini/libmono-component-debugger-stub-static.a" "$RT/mono/mini/libmono-component-hot_reload-stub-static.a"
           "$RT/mono/mini/libmono-component-diagnostics_tracing-stub-static.a")
# kit #8 boot track: a LAUNCHER build (build.sh SHEPHERD_K8_LAUNCHER=1 kept $W/obj/launcher.o): the launcher's and the engine module's objects in
# one ELF -- no crt0 (the launcher's _start is the entry vlx_boot runs; the module's k8_entry starts newlib), the toolchain's crti / crtbegin
# / crtend / crtn kept; vlx.c's sceKernelLoadStartModule calls the linked module_start. VLX_LAUNCHER=0 = refuse (no such ELF can be made
# without the launcher: the module has no crt0 _start of its own).
LAUNCH_OBJS=(); CRT_PRE=(); CRT_POST=(); NOSTART=()
if [[ $APP == menu && -f $W/obj/launcher.o ]]; then
  [[ ${VLX_LAUNCHER:-1} == 0 ]] && { echo "VLX_LAUNCHER=0: $W is a launcher build (its module has no crt0); link it with the launcher"; exit 1; }
  LAUNCH_OBJS=("$W/obj/launcher.o" "$W/obj/k8_module.o"); NOSTART=(-nostartfiles)
  [[ -f $W/obj/k8_latelibs.o ]] && LAUNCH_OBJS+=("$W/obj/k8_latelibs.o")   # kit #8.1: the module's late-library trampolines (first, so they win)
  CRT_PRE=("$(arm-vita-eabi-gcc -print-file-name=crti.o)" "$(arm-vita-eabi-gcc -print-file-name=crtbegin.o)")
  CRT_POST=("$(arm-vita-eabi-gcc -print-file-name=crtend.o)" "$(arm-vita-eabi-gcc -print-file-name=crtn.o)")
fi
# the eboot's link line minus -Wl,-q and the Sce*_stub libraries; entry = the shim's Linux entry, which runs crt0's _start
# (--allow-multiple-definition: the whole-archive libpthread brings its own sem_post.o, which the eboot never links -- the shim
#  archive's corrected sem_post, vita/runtime/system-native/vita_pthread_fix.c, comes first in the link and wins, as in the eboot)
arm-vita-eabi-gcc -nodefaultlibs $NOSTART -Wl,--gc-sections -Wl,--undefined=sceUserMainThreadStackSize -Wl,-e,_lx_start -Wl,-Map="${OUT%.elf}.map" \
  -o "$OUT" $CRT_PRE $LAUNCH_OBJS "$HOST" ${=VLX_EXTRA_OBJS:-} "$TABLES" "$W/obj/debugScreen.o" $AOT_OBJS ${=VLX_LDFLAGS:-} $GCGOV_LD \
  -Wl,--start-group $MONO_LIBS "$W/obj/libSystem.Native.vita.a" -Wl,--end-group $PHYS_LIBS "$O/vlx_entry.o" "$O/vlx.o" \
  $EXTRA -Wl,--allow-multiple-definition -Wl,--whole-archive -lpthread -Wl,--no-whole-archive -lm -lc -lgcc -lc $CRT_POST 2> "${OUT%.elf}.link.log" \
  || { grep -o "undefined reference to \`[^']*'" "${OUT%.elf}.link.log" | sort -u | head -20; echo "(new imports: add them to vlx.c)"; exit 1; }
# the device eboot's LOAD span (lowest segment address to the end of the highest, as the Vita lays it out) -> <out>.eboot-span:
# run.sh derives the user-memory budget from it (the free memory a Vita reports shrinks 1:1 with the eboot; the vlx ELF's own
# size says nothing: it carries the shim instead of the stubs, and a stand-in GL library instead of vitaGL)
case $APP in hello) VELF=$W/obj/shepherd_hello.velf ;; menu) VELF=$W/obj/shepherd_menu.velf ;; esac
rm -f "${OUT%.elf}.eboot-span"
span_of () { local lo=-1 hi=0 va ms
  for va ms in $(arm-vita-eabi-readelf -lW "$1" | awk '$1=="LOAD" {print $3, $6}'); do
    if (( lo < 0 || va < lo )); then lo=$(( va )); fi
    if (( va + ms > hi )); then hi=$(( va + ms )); fi
  done
  (( lo >= 0 )) && echo $(( hi - lo )) || echo 0; }
if [[ -f $VELF ]]; then
  SP=$(span_of "$VELF")
  # kit #8: a launcher build loads the engine module (this velf) next to the launcher eboot: both spans count (ESTIMATE: the module's own
  # placement granularity on the device is not known; the device's MEM start line decides)
  if [[ -f $W/obj/launcher.velf && ${#LAUNCH_OBJS} -gt 0 ]]; then SP=$(( SP + $(span_of "$W/obj/launcher.velf") )); VELF="$VELF + $W/obj/launcher.velf"; fi
  (( SP > 0 )) && echo "$SP $VELF" > "${OUT%.elf}.eboot-span"
fi
echo "linked $OUT ($(stat -f %z "$OUT") bytes) from $W; runtime $RT; owner game modules: $NOWNER; libshepphys: $([[ ${#PHYS_LIBS} -gt 0 ]] && echo "the build's copy" || echo none); LLVM objects: $([[ $LLVM == 1 ]] && echo "adjacent to their modules" || echo none); $(arm-vita-eabi-nm "$OUT" | grep -c '_veneer$' || true) linker veneers; eboot span: $([[ -f ${OUT%.elf}.eboot-span ]] && cut -d' ' -f1 "${OUT%.elf}.eboot-span" || echo "unknown (no $VELF)")"
