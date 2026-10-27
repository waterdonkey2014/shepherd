/* SHEPHERD-VITA V2 link test: AOT Hello module + Vita-compiled libmonosgen objects. Never executed. */
#include <stddef.h>
#include <psp2/kernel/processmgr.h>
void *mono_jit_init_version (const char *, const char *);
int mono_jit_exec (void *, void *, int, char **);
void mono_jit_set_aot_mode (int);
void mono_aot_register_module (void **);
extern void *mono_aot_module_Hello_info;
long long shepherd_vita_ticks(void) { return (long long)sceKernelGetProcessTimeWide(); }
int main(int argc, char **argv) {
    mono_aot_register_module (&mono_aot_module_Hello_info);
    mono_jit_set_aot_mode (3 /* MONO_AOT_MODE_INTERP: full AOT + interpreter fallback */);
    void *d = mono_jit_init_version ("shepherd", "v4.0.30319");
    int r = mono_jit_exec (d, NULL, argc, argv);
    sceKernelExitProcess(r);
    return r;
}
