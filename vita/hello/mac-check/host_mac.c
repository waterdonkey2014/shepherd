/* SHEPHERD-VITA V5 Mac logic check: runs the SAME Hello.dll/HelloInterp.dll on Microsoft's osx-arm64 Mono 8 runtime
 * (interpreter only) with the same host exports the Vita host provides, so the managed test logic and expected
 * values are validated before anything goes to the device. Not a Vita test: no AOT, no coop-only backend, no arena. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <mono/jit/jit.h>
#include <mono/jit/mono-private-unstable.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/mono-gc.h>
#include <mono/utils/mono-logger.h>

__attribute__((visibility("default"))) void shepherd_hello_log (int step, const char *msg) { printf ("M%d %s\n", step, msg); fflush (stdout); }
__attribute__((visibility("default"))) int shepherd_hello_add (int a, int b) { return a + b; }
__attribute__((visibility("default"))) double shepherd_hello_fmix (float a, double b, int c, float d) { return a * b + c + d; }
__attribute__((visibility("default"))) int shepherd_hello_call_back (int (*cb) (int, int), int a, int b) { return cb (a, b); }

int main (int argc, char **argv)
{
	/* argv[1] = TPA list (':'-separated), argv[2] = native search dir, argv[3] = Hello.dll, rest = managed args */
	const char *keys [] = { "TRUSTED_PLATFORM_ASSEMBLIES", "NATIVE_DLL_SEARCH_DIRECTORIES", "System.Globalization.Invariant" };
	const char *vals [] = { argv [1], argv [2], "true" };
	monovm_initialize (3, keys, vals);
	mono_jit_set_aot_mode (MONO_AOT_MODE_INTERP_ONLY);
	MonoDomain *d = mono_jit_init_version ("shepherd-mac-check", "v4.0.30319");
	MonoAssembly *a = mono_assembly_open (argv [3], NULL);
	if (!a) { printf ("FAIL cannot open %s\n", argv [3]); return 2; }
	int r = mono_jit_exec (d, a, argc - 3, argv + 3);
	printf ("H11 managed Main returned %d\n", r);
	return r;
}
