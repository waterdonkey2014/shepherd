/* GAME-AOT mechanics experiment host (vita/gameaot/mech/mech.sh): embeds Microsoft's osx-arm64 Mono 8.0.29 (libcoreclr.dylib
 * of the runtime pack) the way the Vita host embeds the fork -- monovm_initialize, then STATIC AOT modules registered with
 * mono_aot_register_module before any assembly loads (the generated gameaot_register_static (), as build.sh's
 * shepherd_register_aot_modules), then the runtime -- and runs App.dll through mono_main, so the classic options work
 * (--full-aot-interp = mono_jit_set_aot_mode (MONO_AOT_MODE_INTERP), the Vita's mode; --interpreter; --interp=-inline;
 * --profile=...). Dynamic AOT images (<assembly>.dylib next to the assembly) are found as on any Mac Mono.
 *   argv: host <TPA list> <native dir> <mono options...> App.dll <managed args...> */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern int monovm_initialize (int propertyCount, const char **propertyKeys, const char **propertyValues);
extern int mono_main (int argc, char *argv []);
extern void gameaot_register_static (void);
extern const char *gameaot_static_names;

int main (int argc, char **argv)
{
	if (argc < 4) { fprintf (stderr, "usage: %s <TPA> <native dir> <mono options...> App.dll [args]\n", argv [0]); return 2; }
	const char *keys [] = { "TRUSTED_PLATFORM_ASSEMBLIES", "NATIVE_DLL_SEARCH_DIRECTORIES", "System.Globalization.Invariant" };
	const char *vals [] = { argv [1], argv [2], "true" };
	int r = monovm_initialize (3, keys, vals);
	if (r != 0) { fprintf (stderr, "monovm_initialize -> %d\n", r); return 3; }
	gameaot_register_static ();
	fprintf (stderr, "HOST static AOT modules: %s\n", *gameaot_static_names ? gameaot_static_names : "(none)");
	char **margv = calloc ((size_t) argc, sizeof (char *));
	int margc = 0;
	margv [margc++] = argv [0];
	for (int i = 3; i < argc; i++) margv [margc++] = argv [i];
	return mono_main (margc, margv);
}
