/* SHEPHERD game-AOT study (vita/gameaot/SIZES.md): a Mac-only Mono profiler module that turns a vita-sim interpreter run into a
 * Mono AOT profile (.aotprofile, the format src/mono/mono/profiler/aot.h describes and aot-compiler.c load_profile_file reads),
 * listing every method the interpreter transformed, i.e. every method the run executed. mono-aot-cross --aot=...,profile=<file>,
 * profile-only then compiles only those methods (+ the wrappers / instances it keeps by itself): PARTIAL AOT, interpreter fallback
 * for the rest. The official AOT profiler module (profiler/aot.c) is not in the osx-arm64 Mono pack, and needs runtime internals.
 *
 *   build:  vita/gameaot/aotprof/build.sh            -> $GA_OUT/tools/aotprof/libmono-profiler-shepherdaot.dylib
 *   use:    DYLD_LIBRARY_PATH=<dir> MONO_ENV_OPTIONS="--interpreter --profile=shepherdaot:<out.aotprofile>"
 *
 * Records (public embedding API only): IMAGE (assembly name), TYPE (MONO_TYPE_CLASS: image, "ns.Outer/Inner"), METHOD (class,
 * param count, name, mono_signature_full_name). Generic instances are written WITHOUT their instantiation (GINST -1): a method of
 * a generic class instance names the generic type definition (the AOT compiler then matches by name + parameter count, signatures
 * are not compared on a generic type definition), and an instance of a generic method is matched against its definition's
 * signature, so it is usually not found (aot-compiler.c "Unable to load method" with the verbose option). Wrappers are skipped.
 * Not part of any Vita build. */
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct _MonoProfiler MonoProfiler;
typedef struct _MonoProfilerDesc *MonoProfilerHandle;
typedef struct _MonoMethod MonoMethod;
typedef struct _MonoClass MonoClass;
typedef struct _MonoImage MonoImage;
typedef struct _MonoJitInfo MonoJitInfo;
typedef struct _MonoMethodSignature MonoMethodSignature;
typedef int mono_bool;

extern MonoProfilerHandle mono_profiler_create (MonoProfiler *prof);
extern void mono_profiler_set_jit_done_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, MonoJitInfo *));
extern void mono_profiler_set_runtime_shutdown_begin_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *));
extern char *mono_method_full_name (MonoMethod *method, mono_bool signature);
extern MonoClass *mono_method_get_class (MonoMethod *method);
extern const char *mono_method_get_name (MonoMethod *method);
extern MonoMethodSignature *mono_method_signature (MonoMethod *method);
extern uint32_t mono_signature_get_param_count (MonoMethodSignature *sig);
extern char *mono_signature_full_name (MonoMethodSignature *sig);
extern MonoImage *mono_class_get_image (MonoClass *klass);
extern const char *mono_class_get_namespace (MonoClass *klass);
extern const char *mono_class_get_name (MonoClass *klass);
extern MonoClass *mono_class_get_nesting_type (MonoClass *klass);
extern const char *mono_image_get_name (MonoImage *image);
extern void mono_free (void *);

struct _MonoProfiler { int unused; };
static MonoProfiler prof;
static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER;
static char out_path [2048] = "shepherd.aotprofile";
static int written;

/* pointer -> int map (open addressing) */
typedef struct { void *k; int v; } Ent;
typedef struct { Ent *t; size_t cap, used; } Map;
static size_t h (void *p) { uint64_t x = (uintptr_t) p; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t) x; }
static Ent *mslot (Map *m, void *k)
{
	if ((m->used + 1) * 2 > m->cap) {
		size_t nc = m->cap ? m->cap * 2 : 65536; Ent *nt = calloc (nc, sizeof (Ent));
		for (size_t i = 0; i < m->cap; i++) if (m->t [i].k) { size_t j = h (m->t [i].k) & (nc - 1); while (nt [j].k) j = (j + 1) & (nc - 1); nt [j] = m->t [i]; }
		free (m->t); m->t = nt; m->cap = nc;
	}
	size_t i = h (k) & (m->cap - 1);
	while (m->t [i].k && m->t [i].k != k) i = (i + 1) & (m->cap - 1);
	return &m->t [i];
}
static Map methods, images, classes;
static uint64_t seen;

static void on_done (MonoProfiler *p, MonoMethod *m, MonoJitInfo *ji)
{
	(void) p; (void) ji;
	pthread_mutex_lock (&mu);
	Ent *e = mslot (&methods, m);
	if (!e->k) { e->k = m; e->v = 0; methods.used++; }
	seen++;
	pthread_mutex_unlock (&mu);
}

static FILE *f; static int next_id = 1;
static void w_byte (int b) { fputc (b, f); }
static void w_int (int v) { uint32_t u = (uint32_t) v; unsigned char b [4] = { u, u >> 8, u >> 16, u >> 24 }; fwrite (b, 1, 4, f); }
static void w_str (const char *s) { int n = (int) strlen (s); w_int (n); fwrite (s, 1, n, f); }

static int image_id (MonoImage *img)
{
	Ent *e = mslot (&images, img);
	if (e->k) return e->v;
	e->k = img; e->v = next_id++; images.used++;
	w_byte (1); w_int (e->v); w_str (mono_image_get_name (img)); w_str ("");   /* AOTPROF_RECORD_IMAGE: name, mvid (unused) */
	return e->v;
}
static int class_id (MonoClass *k)
{
	Ent *e = mslot (&classes, k);
	if (e->k) return e->v;
	char name [4096]; name [0] = 0;
	MonoClass *outer = k;
	/* "Outer/Inner" (mono_class_from_name's nested syntax), namespace of the outermost class */
	char tmp [4096];
	strncpy (name, mono_class_get_name (k), sizeof (name) - 1); name [sizeof (name) - 1] = 0;
	while (mono_class_get_nesting_type (outer)) {
		outer = mono_class_get_nesting_type (outer);
		snprintf (tmp, sizeof (tmp), "%s/%s", mono_class_get_name (outer), name); strcpy (name, tmp);
	}
	snprintf (tmp, sizeof (tmp), "%s.%s", mono_class_get_namespace (outer), name);
	int iid = image_id (mono_class_get_image (k));
	e = mslot (&classes, k);                 /* image_id may have grown another map, not this one; re-slot anyway */
	e->k = k; e->v = next_id++; classes.used++;
	w_byte (2); w_int (e->v); w_byte (0x12 /* MONO_TYPE_CLASS */); w_int (iid); w_int (-1); w_str (tmp);   /* AOTPROF_RECORD_TYPE */
	return e->v;
}

static void write_out (void)
{
	pthread_mutex_lock (&mu);
	if (written) { pthread_mutex_unlock (&mu); return; }
	written = 1;
	f = fopen (out_path, "wb");
	if (!f) { fprintf (stderr, "[aotprof] cannot write %s\n", out_path); pthread_mutex_unlock (&mu); return; }
	fwrite ("AOTPROFILE", 1, 10, f); w_int ((1 << 16) | 0);
	uint64_t nm = 0, nw = 0;
	char list_path [2100]; snprintf (list_path, sizeof (list_path), "%s.methods.tsv", out_path);
	FILE *lf = fopen (list_path, "w");
	for (size_t i = 0; i < methods.cap; i++) {
		MonoMethod *m = methods.t [i].k; if (!m) continue;
		char *full = mono_method_full_name (m, 1);
		if (full && !strncmp (full, "(wrapper", 8)) { nw++; mono_free (full); continue; }
		MonoClass *k = mono_method_get_class (m);
		MonoMethodSignature *sig = mono_method_signature (m);
		if (!k || !sig) { if (full) mono_free (full); continue; }
		int cid = class_id (k);
		char *s = mono_signature_full_name (sig);
		w_byte (4); w_int (next_id++); w_int (cid); w_int (-1); w_int ((int) mono_signature_get_param_count (sig));   /* AOTPROF_RECORD_METHOD */
		w_str (mono_method_get_name (m)); w_str (s ? s : "");
		if (lf) fprintf (lf, "%s\t%s\t%s\n", mono_image_get_name (mono_class_get_image (k)), full ? full : "?", s ? s : "");
		nm++;
		if (s) mono_free (s);
		if (full) mono_free (full);
	}
	w_byte (0); w_int (0);   /* AOTPROF_RECORD_NONE */
	fclose (f); if (lf) fclose (lf);
	fprintf (stderr, "[aotprof] %llu methods (%llu wrappers skipped), %zu classes, %zu images -> %s\n",
	         (unsigned long long) nm, (unsigned long long) nw, classes.used, images.used, out_path);
	pthread_mutex_unlock (&mu);
}
static void on_shutdown (MonoProfiler *p) { (void) p; write_out (); }

__attribute__((visibility ("default"))) void mono_profiler_init_shepherdaot (const char *desc)
{
	const char *c = desc ? strchr (desc, ':') : NULL;
	if (c && c [1]) { strncpy (out_path, c + 1, sizeof (out_path) - 1); }
	MonoProfilerHandle hd = mono_profiler_create (&prof);
	mono_profiler_set_jit_done_callback (hd, on_done);
	mono_profiler_set_runtime_shutdown_begin_callback (hd, on_shutdown);
	atexit (write_out);
	fprintf (stderr, "[aotprof] loaded, output %s\n", out_path);
}
