/* SHEPHERD-VITA V2 link test: never executed. Pulls the static AOT module into a Vita ELF. */
#include <psp2/kernel/processmgr.h>
extern void *mono_aot_module_Hello_info;
long long shepherd_vita_ticks(void) { return (long long)sceKernelGetProcessTimeWide(); }
void *volatile shepherd_keep_aot;
int main(void) {
    shepherd_keep_aot = &mono_aot_module_Hello_info;
    sceKernelExitProcess(0);
    return 0;
}
