/* GAME-AOT (vita/gameaot, $SHEPHERD_WORK/gameaot/mechanics.md): a Mac-only Mono profiler module that counts the
 * interpreted calls of every method of selected assemblies and writes a Mono AOT profile (.aotprofile, the format of
 * mono/profiler/aot.h that `mono-aot-cross --aot=...,profile=<file>,profile-only` reads) holding the methods called at least
 * `min` times -- the input of profile-guided PARTIAL AOT of an owner-local module (only the hot methods compiled; everything
 * else stays interpreted).
 *
 *   build:  vita/gameaot/aotprof/build.sh   -> $GAMEAOT_OUT/aotprof/libmono-profiler-gameaotprof.dylib (public Mono API only)
 *   use:    DYLD_LIBRARY_PATH=<dir> MONO_ENV_OPTIONS="--interpreter --interp=-inline --profile=gameaotprof:out=<prefix>,images=<A>;<B>,min=<n>[,window]"
 *           -> <prefix>.aotprofile + <prefix>.tsv (calls, kept, image, method, signature: every counted method)
 *   options: images=  ';'-separated assembly names to count (default: every image except CoreLib)
 *            min=     call count from which a method goes into the profile (default 1: "executed at least once")
 *            vt=0|1   (default 1) add every instance method of every non-generic value type of the counted images: the
 *                     unbox-trampoline hazard of partial images (see write_all)
 *            closure= ';'-separated assembly names whose value-type closure is written even if none of their methods ran
 *                     (the images must be loaded by the end of the run)
 *            window   count only between Shepherd.Engine.Diag.ProfMarks.WindowStart / WindowEnd (the vita-sim measurement
 *                     window, as vita/v6/tools/interp-prof), on the thread that called WindowStart
 *
 * What it can see: methods the INTERPRETER executes as their own frame (MINT_PROF_ENTER). A callee the interpreter inlined raises
 * nothing -- run with --interp=-inline when the profile must also list the tiny methods (Rect getters, Vector3 operators) that
 * AOT callers would call; AOT-compiled methods raise nothing either (profile with the module under test interpreted).
 * What it writes: every counted method of a non-generic class, and of a closed generic class instance (List<Vector3>.Add:
 * a GINST record of its arguments + the definition's TYPE record), as mono/profiler/aot.c does. Not written: instances of
 * generic METHODS (GetComponent<T>: the public API has no method context) and instances over arrays/pointers/generic
 * parameters. Stock mono-aot-cross keeps every inflated method it enumerates in profile-only mode anyway (compile_method:
 * `keep = method->is_inflated`); the fork's SHEPHERD_AOT_PROFILE_STRICT=1 (vita/gameaot/fork-partial-aot.patch) keeps only
 * the instances the profile names. Instances are compiled into the image of the assembly being compiled only when they
 * reference it (aot-compiler.c add_single_profile_method). Not part of any Vita build. */
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
typedef struct _MonoType MonoType;
typedef struct _MonoMethodSignature MonoMethodSignature;
typedef struct _MonoProfilerCallContext MonoProfilerCallContext;
typedef int mono_bool;
enum { INSTR_ENTER = 1 << 1 };                       /* profiler-types.h MONO_PROFILER_CALL_INSTRUMENTATION_ENTER */
enum { MONO_TYPE_GENERICINST = 0x15 };

extern MonoProfilerHandle mono_profiler_create (MonoProfiler *prof);
extern void mono_profiler_set_call_instrumentation_filter_callback (MonoProfilerHandle h, int (*cb) (MonoProfiler *, MonoMethod *));
extern void mono_profiler_set_method_enter_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, MonoProfilerCallContext *));
extern void mono_profiler_set_runtime_shutdown_begin_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *));
extern char *mono_method_full_name (MonoMethod *method, mono_bool signature);
extern const char *mono_method_get_name (MonoMethod *method);
extern MonoClass *mono_method_get_class (MonoMethod *method);
extern MonoMethodSignature *mono_method_signature (MonoMethod *method);
extern uint32_t mono_signature_get_param_count (MonoMethodSignature *sig);
extern char *mono_signature_full_name (MonoMethodSignature *sig);
extern const char *mono_class_get_name (MonoClass *klass);
extern const char *mono_class_get_namespace (MonoClass *klass);
extern MonoClass *mono_class_get_nesting_type (MonoClass *klass);
extern MonoType *mono_class_get_type (MonoClass *klass);
extern int mono_type_get_type (MonoType *type);
extern MonoImage *mono_class_get_image (MonoClass *klass);
extern const char *mono_image_get_name (MonoImage *image);
extern const char *mono_image_get_guid (MonoImage *image);
extern void mono_free (void *);
extern int mono_image_get_table_rows (MonoImage *image, int table_id);
extern MonoClass *mono_class_get_generic_type_definition (MonoClass *klass);
extern void *mono_class_get_context (MonoClass *klass);           /* MonoGenericContext * */
extern MonoClass *mono_class_from_mono_type (MonoType *type);
extern MonoImage *mono_image_loaded (const char *name);
extern MonoClass *mono_class_get (MonoImage *image, uint32_t type_token);
extern mono_bool mono_class_is_valuetype (MonoClass *klass);
extern mono_bool mono_class_is_enum (MonoClass *klass);
extern MonoMethod *mono_class_get_methods (MonoClass *klass, void **iter);
extern uint32_t mono_method_get_flags (MonoMethod *method, uint32_t *iflags);

struct _MonoProfiler { int unused; };
static MonoProfiler prof;

typedef struct { MonoMethod *m; uint64_t calls; } Stat;
static Stat *st; static size_t st_cap, st_used;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static char out_prefix [2048] = "gameaot";
static char images [4096];                           /* ";A;B;" or empty = all but CoreLib */
static uint64_t min_calls = 1;
static int window_mode, recording, written, vt_closure = 1;
static char closure_images [4096];                   /* closure=A;B: value-type closure of these images even if nothing ran */
static pthread_t owner;
static MonoMethod *mark_start, *mark_end;

static size_t h1 (const void *p) { uint64_t x = (uintptr_t) p; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t) x; }
static Stat *stat_of (MonoMethod *m)
{
	if ((st_used + 1) * 2 > st_cap) {
		size_t nc = st_cap ? st_cap * 2 : 65536; Stat *nt = calloc (nc, sizeof (Stat));
		for (size_t i = 0; i < st_cap; i++) if (st [i].m) { size_t j = h1 (st [i].m) & (nc - 1); while (nt [j].m) j = (j + 1) & (nc - 1); nt [j] = st [i]; }
		free (st); st = nt; st_cap = nc;
	}
	size_t i = h1 (m) & (st_cap - 1);
	while (st [i].m && st [i].m != m) i = (i + 1) & (st_cap - 1);
	if (!st [i].m) { st [i].m = m; st_used++; }
	return &st [i];
}

static int image_selected (MonoImage *img)
{
	const char *n = img ? mono_image_get_name (img) : NULL;
	if (!n) return 0;
	if (!images [0]) return strcmp (n, "System.Private.CoreLib") != 0;
	char key [512]; snprintf (key, sizeof key, ";%s;", n);
	return strstr (images, key) != NULL;
}

static int filter (MonoProfiler *p, MonoMethod *m)
{
	(void) p;
	MonoClass *k = mono_method_get_class (m);
	if (window_mode && k) {
		const char *cn = mono_class_get_name (k), *mn = mono_method_get_name (m);
		if (cn && mn && !strcmp (cn, "ProfMarks")) {
			if (!strcmp (mn, "WindowStart")) { mark_start = m; return INSTR_ENTER; }
			if (!strcmp (mn, "WindowEnd")) { mark_end = m; return INSTR_ENTER; }
		}
	}
	return k && image_selected (mono_class_get_image (k)) ? INSTR_ENTER : 0;
}

static void on_enter (MonoProfiler *p, MonoMethod *m, MonoProfilerCallContext *ctx)
{
	(void) p; (void) ctx;
	if (window_mode) {
		if (m == mark_start) { owner = pthread_self (); recording = 1; return; }
		if (m == mark_end) { recording = 0; return; }
		if (!recording || !pthread_equal (owner, pthread_self ())) return;
	}
	pthread_mutex_lock (&lock);
	stat_of (m)->calls++;
	pthread_mutex_unlock (&lock);
}

/* ---- the .aotprofile writer (mono/profiler/aot.h: magic, version 1.0, records [type byte, id int32, data], NONE ends) ---- */
static FILE *out;
static int next_id;
typedef struct { const void *key; int id; } Id;
static Id ids [65536]; static int nids;
static int id_of (const void *key) { for (int i = 0; i < nids; i++) if (ids [i].key == key) return ids [i].id; return -1; }
static void put_id (const void *key, int id) { if (nids < 65536) { ids [nids].key = key; ids [nids].id = id; nids++; } }
static void w_int (int32_t v) { fwrite (&v, 4, 1, out); }          /* little endian on arm64 */
static void w_byte (uint8_t v) { fwrite (&v, 1, 1, out); }
static void w_str (const char *s) { int32_t n = (int32_t) strlen (s); w_int (n); fwrite (s, 1, (size_t) n, out); }
static void w_rec (int type, int id) { w_byte ((uint8_t) type); w_int (id); }

static int image_rec (MonoImage *img)
{
	int id = id_of (img); if (id >= 0) return id;
	id = next_id++; w_rec (1 /* IMAGE */, id); w_str (mono_image_get_name (img)); w_str (mono_image_get_guid (img)); put_id (img, id);
	return id;
}

/* "ns.Outer/Inner/Leaf" (the reader splits at the LAST '.', then mono_class_from_name resolves the '/' chain) of a class or of
 * the generic type definition of a generic instance; NULL if a nested name holds a '.' */
static char *class_name (MonoClass *k)
{
	MonoClass *chain [16]; int n = 0;
	if (mono_type_get_type (mono_class_get_type (k)) == MONO_TYPE_GENERICINST) k = mono_class_get_generic_type_definition (k);
	for (MonoClass *c = k; c && n < 16; c = mono_class_get_nesting_type (c))
		chain [n++] = c;
	char *buf = malloc (4096); size_t len = 0;
	len += (size_t) snprintf (buf + len, 4096 - len, "%s.", mono_class_get_namespace (chain [n - 1]));
	for (int i = n - 1; i >= 0; i--) {
		const char *nm = mono_class_get_name (chain [i]);
		if (i < n - 1 && strchr (nm, '.')) { free (buf); return NULL; }
		len += (size_t) snprintf (buf + len, 4096 - len, "%s%s", nm, i ? "/" : "");
	}
	return buf;
}

/* MonoGenericInst (class-internals.h, not MONO_SMALL_CONFIG): gint32 id; guint type_argc:22, is_open:1; MonoType *type_argv[] */
typedef struct { int32_t id; uint32_t bits; MonoType *type_argv [1]; } GInstView;
typedef struct { GInstView *class_inst, *method_inst; } GCtxView;

static int class_rec (MonoClass *k)
{
	int id = id_of (k); if (id >= 0) return id;
	int ginst = -1;
	if (mono_type_get_type (mono_class_get_type (k)) == MONO_TYPE_GENERICINST && mono_class_get_generic_type_definition (k) != k) {
		/* a generic instance: GINST record of its (closed, class/value/primitive) arguments, then the TYPE of its definition.
		 * (A generic type DEFINITION is written as a plain TYPE record of its own name: the reader resolves it to the definition
		 * and takes its methods by name -- the signature check is skipped for definitions -- and mono-aot-cross then compiles
		 * their shared (T_REF) bodies.) */
		GCtxView *ctx = (GCtxView *) mono_class_get_context (k);
		GInstView *gi = ctx ? ctx->class_inst : NULL;
		if (!gi || (gi->bits >> 22) & 1) return -1;                        /* open */
		int argc = (int) (gi->bits & 0x3fffff), ids_ [16];
		if (argc < 1 || argc > 16) return -1;
		for (int i = 0; i < argc; i++) {
			int t = mono_type_get_type (gi->type_argv [i]);
			/* the reader resolves TYPE records only: classes, value types, primitives, string, object, nested instances */
			if (!((t >= 0x02 && t <= 0x0e) || t == 0x12 || t == 0x11 || t == 0x15 || t == 0x18 || t == 0x19 || t == 0x1c)) return -1;
			if ((ids_ [i] = class_rec (mono_class_from_mono_type (gi->type_argv [i]))) < 0) return -1;
		}
		ginst = next_id++; w_rec (3 /* GINST */, ginst); w_int (argc); for (int i = 0; i < argc; i++) w_int (ids_ [i]);
	}
	char *name = class_name (k); if (!name) return -1;
	MonoClass *def = ginst >= 0 ? mono_class_get_generic_type_definition (k) : k;
	int img = image_rec (mono_class_get_image (def));
	id = next_id++; w_rec (2 /* TYPE */, id); w_byte (0x12 /* MONO_TYPE_CLASS */); w_int (img); w_int (ginst); w_str (name);
	free (name); put_id (k, id);
	return id;
}

static int cmp_calls (const void *a, const void *b) { const Stat *x = a, *y = b; return x->calls < y->calls ? 1 : x->calls > y->calls ? -1 : 0; }

static void write_all (MonoProfiler *p)
{
	(void) p;
	if (written) return; written = 1;
	char path [2100];
	snprintf (path, sizeof path, "%s.aotprofile", out_prefix);
	out = fopen (path, "wb");
	snprintf (path, sizeof path, "%s.tsv", out_prefix);
	FILE *tsv = fopen (path, "w");
	if (!out || !tsv) { fprintf (stderr, "gameaotprof: cannot write %s.*\n", out_prefix); return; }
	fwrite ("AOTPROFILE", 1, 10, out); w_int ((1 << 16) | 0);
	Stat *v = malloc ((st_used + 1) * sizeof (Stat)); size_t n = 0;
	for (size_t i = 0; i < st_cap; i++) if (st [i].m) v [n++] = st [i];
	qsort (v, n, sizeof (Stat), cmp_calls);
	fprintf (tsv, "calls\tkept\timage\tmethod\tsignature\n");
	int kept = 0, generic = 0, below = 0;
	for (size_t i = 0; i < n; i++) {
		MonoMethod *m = v [i].m; MonoClass *k = mono_method_get_class (m);
		char *full = mono_method_full_name (m, 0);
		MonoMethodSignature *sig = mono_method_signature (m);
		char *s = sig ? mono_signature_full_name (sig) : NULL;
		const char *why = "yes";
		/* an inflated generic METHOD prints as Class:Name<args> (mono_method_full_name), its name alone has no <args> */
		size_t fl = strlen (full), nl = strlen (mono_method_get_name (m));
		int inflated_method = fl > nl && full [fl - 1] == '>' && mono_method_get_name (m) [nl - 1] != '>';
		int cid = -2;
		if (v [i].calls < min_calls) { why = "below-min"; below++; }
		else if (inflated_method || !s) { why = "generic"; generic++; }
		else if ((cid = class_rec (k)) < 0) { why = "generic"; generic++; }
		else {
			int id = next_id++;
			w_rec (4 /* METHOD */, id); w_int (cid); w_int (-1); w_int ((int32_t) mono_signature_get_param_count (sig));
			w_str (mono_method_get_name (m)); w_str (s);
			kept++;
		}
		fprintf (tsv, "%llu\t%s\t%s\t%s\t%s\n", (unsigned long long) v [i].calls, why, mono_image_get_name (mono_class_get_image (k)), full, s ? s : "");
		mono_free (full); if (s) mono_free (s);
	}
	/* value-type closure (vt=1, the default): every instance method of every non-generic value type of every image that had a
	 * counted method. Stock Mono 8 (aot-runtime.c mono_aot_get_unbox_trampoline) assumes that a non-generic value-type method of
	 * an image WITH an AOT module was compiled: for one that was not (a partial image), its binary search of the module's unbox
	 * trampoline table ends on a neighbour's entry and returns ANOTHER method's unbox trampoline -- a virtual / interface /
	 * delegate call on a boxed struct then silently runs the wrong method (mechanics.md). Compiling them all avoids it. */
	int closure = 0;
	if (vt_closure) {
		MonoImage *seen_img [256]; int nimg = 0;
		for (size_t i = 0; i < n; i++) {
			MonoImage *img = mono_class_get_image (mono_method_get_class (v [i].m)); int j = 0;
			while (j < nimg && seen_img [j] != img) j++;
			if (j == nimg && nimg < 256) seen_img [nimg++] = img;
		}
		if (closure_images [0]) {
			char *names = strdup (closure_images), *save = NULL;
			for (char *t = strtok_r (names, ";", &save); t; t = strtok_r (NULL, ";", &save)) {
				MonoImage *img = mono_image_loaded (t); int j = 0;
				if (!img) { fprintf (stderr, "gameaotprof: closure image '%s' is not loaded\n", t); continue; }
				while (j < nimg && seen_img [j] != img) j++;
				if (j == nimg && nimg < 256) seen_img [nimg++] = img;
			}
			free (names);
		}
		for (int j = 0; j < nimg; j++) {
			int rows = mono_image_get_table_rows (seen_img [j], 2 /* MONO_TABLE_TYPEDEF */);
			for (int r = 2; r <= rows; r++) {
				MonoClass *k = mono_class_get (seen_img [j], 0x02000000u | (uint32_t) r);
				if (!k || !mono_class_is_valuetype (k) || mono_class_is_enum (k)) continue;
				/* generic structs too: an instance over a REFERENCE type is looked up by its definition's token in the unbox
				 * table (mono_aot_get_unbox_trampoline's non-inflated branch), so its shared body must be compiled as well
				 * (measured: Wrap<string>.ToString through a boxed receiver ran the wrong method without it) */
				char *cn = class_name (k);
				if (!cn) continue;
				free (cn);
				void *iter = NULL; MonoMethod *m;
				while ((m = mono_class_get_methods (k, &iter))) {
					uint32_t fl = mono_method_get_flags (m, NULL);
					if (fl & (0x0010 /* STATIC */ | 0x0400 /* ABSTRACT */)) continue;
					Stat *sm = NULL;
					for (size_t q = 0; q < n; q++) if (v [q].m == m) { sm = &v [q]; break; }
					if (sm && sm->calls >= min_calls) continue;   /* already a record */
					MonoMethodSignature *sig = mono_method_signature (m);
					char *s2 = sig ? mono_signature_full_name (sig) : NULL;
					int cid = s2 ? class_rec (k) : -1;
					if (cid >= 0) {
						int id = next_id++;
						w_rec (4 /* METHOD */, id); w_int (cid); w_int (-1); w_int ((int32_t) mono_signature_get_param_count (sig));
						w_str (mono_method_get_name (m)); w_str (s2);
						char *full = mono_method_full_name (m, 0);
						fprintf (tsv, "0\tvt-closure\t%s\t%s\t%s\n", mono_image_get_name (seen_img [j]), full, s2);
						mono_free (full);
						closure++;
					}
					if (s2) mono_free (s2);
				}
			}
		}
	}
	w_rec (0 /* NONE */, 0);
	fclose (out); fclose (tsv);
	fprintf (stderr, "gameaotprof: %zu methods counted, %d in %s.aotprofile (min %llu) + %d value-type closure, %d generic left out, %d below min\n",
		 n, kept, out_prefix, (unsigned long long) min_calls, closure, generic, below);
}

static void write_at_exit (void) { write_all (&prof); }

void mono_profiler_init_gameaotprof (const char *desc);
void mono_profiler_init_gameaotprof (const char *desc)
{
	/* desc = "gameaotprof[:out=P,images=A;B,min=N,window]" */
	const char *a = desc ? strchr (desc, ':') : NULL;
	if (a) {
		char *opts = strdup (a + 1), *save = NULL;
		for (char *t = strtok_r (opts, ",", &save); t; t = strtok_r (NULL, ",", &save)) {
			if (!strncmp (t, "out=", 4)) snprintf (out_prefix, sizeof out_prefix, "%s", t + 4);
			else if (!strncmp (t, "images=", 7)) snprintf (images, sizeof images, ";%s;", t + 7);
			else if (!strncmp (t, "min=", 4)) min_calls = strtoull (t + 4, NULL, 10);
			else if (!strcmp (t, "window")) window_mode = 1;
			else if (!strncmp (t, "vt=", 3)) vt_closure = atoi (t + 3);
			else if (!strncmp (t, "closure=", 8)) snprintf (closure_images, sizeof closure_images, "%s", t + 8);
			else fprintf (stderr, "gameaotprof: unknown option '%s'\n", t);
		}
		free (opts);
	}
	if (getenv ("GAMEAOTPROF_VERBOSE")) fprintf (stderr, "gameaotprof: loaded (%s)\n", desc ? desc : "");
	MonoProfilerHandle h = mono_profiler_create (&prof);
	mono_profiler_set_call_instrumentation_filter_callback (h, filter);
	mono_profiler_set_method_enter_callback (h, on_enter);
	mono_profiler_set_runtime_shutdown_begin_callback (h, write_all);
	atexit (write_at_exit);   /* .NET 8 Mono never raises runtime_shutdown_begin (as vita/v6/tools/interp-prof found): write at exit */
}
