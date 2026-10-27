/* SHEPHERD-VITA V2 link census: never executed. */
#include <stddef.h>
void *mono_jit_init_version (const char *, const char *);
int mono_jit_exec (void *, void *, int, char **);
void mono_jit_set_aot_mode (int);
void mono_aot_register_module (void **);
int main(int argc, char **argv) {
    mono_jit_set_aot_mode (3 /* MONO_AOT_MODE_INTERP */);
    void *d = mono_jit_init_version ("shepherd", "v4.0.30319");
    return mono_jit_exec (d, NULL, argc, argv);
}
