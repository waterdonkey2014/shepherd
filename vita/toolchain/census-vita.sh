#!/bin/sh
# SHEPHERD-VITA (V2, superseded in V5 by build-runtime-vita.sh + vita/hello/build.sh, which link with NO stand-ins): configure the Vita runtime, compile every TU keep-going, then link-census it with the Hello AOT module.
export VITASDK=$HOME/vitasdk PATH=$HOME/vitasdk/bin:$PATH
O=${SHEPHERD_WORK:-$HOME/shepherd-work}/obj; R=$O/vita-runtime; C=$O/vita-link-census
$O/configure-vita.sh || exit 1
cd $R && ${NINJA:-ninja} -k 0 > census.log 2>&1
sh -c "$(${NINJA:-ninja} -t commands mono/mini/cpu-arm.h | tail -1)"
python3 - <<'PY'
import json, os, subprocess, concurrent.futures as cf
os.chdir(os.path.join(os.environ.get('SHEPHERD_WORK') or os.path.expanduser('~/shepherd-work'), 'obj/vita-runtime'))
cc=json.load(open('compile_commands.json'))
todo=[]
for e in cc:
    a=e['command'].split(); out=a[a.index('-o')+1]; path=os.path.join(e['directory'], out)
    if not os.path.exists(path): todo.append((e,path))
def run(t):
    e,path=t; os.makedirs(os.path.dirname(path), exist_ok=True)
    r=subprocess.run(e['command'], shell=True, cwd=e['directory'], capture_output=True, text=True)
    return e['file'], r.returncode, r.stderr
with cf.ThreadPoolExecutor(14) as ex: res=list(ex.map(run, todo))
with open('census-direct.log','w') as fh:
    for f,rc,err in res:
        if rc: fh.write("==== %s rc=%d\n%s\n" % (f,rc,err))
print("TUs:", len(cc), "failed:", sum(1 for r in res if r[1]))
for f,rc,err in res:
    if rc: print("  FAIL", f.replace(os.path.join(os.environ.get('SHEPHERD_WORK') or os.path.expanduser('~/shepherd-work'), 'runtime') + '/',''))
PY
mkdir -p $C && cd $C && rm -f libmono-census.a
find $R/mono/eglib $R/mono/metadata $R/mono/utils $R/mono/sgen $R/mono/mini/CMakeFiles/monosgen-objects.dir $R/mono/mini/CMakeFiles/debugger-stub-objects.dir $R/mono/mini/CMakeFiles/diagnostics_tracing-stub-objects.dir $R/mono/mini/CMakeFiles/hot_reload-stub-objects.dir $R/mono/mini/CMakeFiles/marshal-ilgen-objects.dir -name "*.c.obj" > objs.txt
arm-vita-eabi-ar rcs libmono-census.a $(cat objs.txt)
L() { arm-vita-eabi-gcc -Wl,-q -o full.elf vita_full_main.o Hello.o libmono-census.a "$@" -lpthread -lm -lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceRtc_stub -lSceSysmem_stub -lSceNet_stub -lSceNetCtl_stub; }
L > full-link1.log 2>&1
grep -o "undefined reference to \`[^']*'" full-link1.log | sed "s/undefined reference to \`//;s/'//" | sort -u > full-undef.txt
echo "undefined at link: $(wc -l < full-undef.txt)"
{ echo "/* GENERATED stand-ins, never executed */"; while read s; do echo "void $s(void) {}"; done < full-undef.txt; } > full_stubs.c
arm-vita-eabi-gcc -w -c full_stubs.c -o full_stubs.o && L full_stubs.o > full-link2.log 2>&1 || { head full-link2.log; exit 1; }
arm-vita-eabi-size full.elf
arm-vita-eabi-readelf -r full.elf | grep R_ARM | awk '{print $3}' | sort | uniq -c | sort -rn
vita-elf-create full.elf full.velf > full-velf.log 2>&1; echo "vita-elf-create rc=$?"; tail -3 full-velf.log
