/* V6 vita-sim (vita/notes/v6-mac-sim.md): a Mac-only Mono profiler module that lists every method the Mono interpreter
 * transformed (and the few the JIT still compiles under --interpreter), with the size of the interpreter IR it produced.
 *
 *   build:  vita/v6/tools/interp-census/build.sh          -> out/libmono-profiler-shepherd.dylib (Xcode clang, public Mono headers)
 *   use:    DYLD_LIBRARY_PATH=<out dir> MONO_ENV_OPTIONS="--interpreter --profile=shepherd:<output.tsv>"
 *
 * Mono (.NET 8) stubs mono_counters_dump, so `--stats` no longer prints "Methods transformed"; the profiler API still raises
 * jit_begin/jit_done for interpreter transforms (transform.c generate_code / mono_interp_transform_method), with a MonoJitInfo whose
 * code is the interpreter IR (code_size = IR bytes). Tiering transforms a hot method twice (untiered, then optimized): both
 * are counted; "ir_bytes" is the last one, "ir_total" the sum (the untiered IR is not freed).
 * JIT vs interpreter: MonoJitInfo.is_interp (bit 28 of the bitfield word after code_size, jit-info.h layout of 8.0.x),
 * checked against the public mono_jit_info_get_code_size() for every record (a layout mismatch is reported, not guessed).
 * Not part of any Vita build: the Vita runtime has no profiler modules and no dlopen. */
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
typedef int mono_bool;

/* public Mono embedding API (src/native/public/mono/metadata/{profiler,class,image,debug-helpers,appdomain}.h) */
extern MonoProfilerHandle mono_profiler_create (MonoProfiler *prof);
extern void mono_profiler_set_jit_begin_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *));
extern void mono_profiler_set_jit_done_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, MonoJitInfo *));
extern void mono_profiler_set_runtime_shutdown_begin_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *));
extern char *mono_method_full_name (MonoMethod *method, mono_bool signature);
extern MonoClass *mono_method_get_class (MonoMethod *method);
extern MonoImage *mono_class_get_image (MonoClass *klass);
extern const char *mono_image_get_name (MonoImage *image);
extern int mono_jit_info_get_code_size (MonoJitInfo *ji);
extern void mono_free (void *);

struct _MonoProfiler { int unused; };
static MonoProfiler prof;

typedef struct { void *d, *n, *code_start; uint32_t unwind_info; int32_t code_size; uint32_t bits; } JiPrefix;   /* jit-info.h, 8.0.x */

typedef struct { MonoMethod *m; uint32_t transforms; uint32_t ir; uint64_t ir_total; uint8_t interp, jit; } Ent;
static Ent *tab; static size_t cap, used;
static uint64_t begins, dones, layout_mismatch;
static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER;
static char out_path [2048] = "interp-methods.tsv";
static int written;

static size_t hash (MonoMethod *m) { uint64_t x = (uintptr_t) m; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t) x; }
static Ent *slot (Ent *t, size_t c, MonoMethod *m)
{
	size_t i = hash (m) & (c - 1);
	while (t [i].m && t [i].m != m) i = (i + 1) & (c - 1);
	return &t [i];
}
static Ent *get (MonoMethod *m)
{
	if ((used + 1) * 2 > cap) {
		size_t nc = cap ? cap * 2 : 65536; Ent *nt = calloc (nc, sizeof (Ent));
		for (size_t i = 0; i < cap; i++) if (tab [i].m) *slot (nt, nc, tab [i].m) = tab [i];
		free (tab); tab = nt; cap = nc;
	}
	Ent *e = slot (tab, cap, m);
	if (!e->m) { e->m = m; used++; }
	return e;
}

static void on_begin (MonoProfiler *p, MonoMethod *m) { (void) p; (void) m; __atomic_fetch_add (&begins, 1, __ATOMIC_RELAXED); }
static void on_done (MonoProfiler *p, MonoMethod *m, MonoJitInfo *ji)
{
	(void) p;
	int size = ji ? mono_jit_info_get_code_size (ji) : 0;
	int interp = 0;
	if (ji) { const JiPrefix *jp = (const JiPrefix *) ji; if (jp->code_size == size) interp = (jp->bits >> 28) & 1; else __atomic_fetch_add (&layout_mismatch, 1, __ATOMIC_RELAXED); }
	pthread_mutex_lock (&mu);
	dones++;
	Ent *e = get (m);
	e->transforms++; e->ir = (uint32_t) size; e->ir_total += (uint32_t) size;
	if (interp) e->interp = 1; else e->jit = 1;
	pthread_mutex_unlock (&mu);
}

static void write_out (void)
{
	pthread_mutex_lock (&mu);
	if (written) { pthread_mutex_unlock (&mu); return; }
	written = 1;
	FILE *f = fopen (out_path, "w");
	if (!f) { fprintf (stderr, "[interp-census] cannot write %s\n", out_path); pthread_mutex_unlock (&mu); return; }
	uint64_t ni = 0, nj = 0, irb = 0, irt = 0, tiered = 0;
	for (size_t i = 0; i < cap; i++) {
		Ent *e = &tab [i]; if (!e->m) continue;
		if (e->interp) { ni++; irb += e->ir; irt += e->ir_total; if (e->transforms > 1) tiered++; } else nj++;
	}
	fprintf (f, "# shepherd interp census: %llu methods transformed by the interpreter (%llu transformed twice: tiering), %llu compiled by the JIT; "
	         "IR %llu bytes (last tier) / %llu bytes (all tiers); jit_begin %llu, jit_done %llu, JitInfo layout mismatches %llu\n",
	         (unsigned long long) ni, (unsigned long long) tiered, (unsigned long long) nj, (unsigned long long) irb, (unsigned long long) irt,
	         (unsigned long long) begins, (unsigned long long) dones, (unsigned long long) layout_mismatch);
	fprintf (f, "engine\timage\ttransforms\tir_bytes\tir_total\tmethod\n");
	for (size_t i = 0; i < cap; i++) {
		Ent *e = &tab [i]; if (!e->m) continue;
		MonoClass *k = mono_method_get_class (e->m);
		const char *img = k ? mono_image_get_name (mono_class_get_image (k)) : "?";
		char *name = mono_method_full_name (e->m, 1);
		fprintf (f, "%s\t%s\t%u\t%u\t%llu\t%s\n", e->interp ? "interp" : "jit", img ? img : "?", e->transforms, e->ir, (unsigned long long) e->ir_total, name ? name : "?");
		if (name) mono_free (name);
	}
	fclose (f);
	fprintf (stderr, "[interp-census] %llu interpreted methods, %llu JIT, IR %.2f MiB (all tiers %.2f MiB) -> %s\n",
	         (unsigned long long) ni, (unsigned long long) nj, irb / 1048576.0, irt / 1048576.0, out_path);
	pthread_mutex_unlock (&mu);
}
static void on_shutdown (MonoProfiler *p) { (void) p; write_out (); }

__attribute__((visibility ("default"))) void mono_profiler_init_shepherd (const char *desc)
{
	const char *c = desc ? strchr (desc, ':') : NULL;
	if (c && c [1]) { strncpy (out_path, c + 1, sizeof (out_path) - 1); }
	MonoProfilerHandle h = mono_profiler_create (&prof);
	mono_profiler_set_jit_begin_callback (h, on_begin);
	mono_profiler_set_jit_done_callback (h, on_done);
	mono_profiler_set_runtime_shutdown_begin_callback (h, on_shutdown);
	atexit (write_out);   /* if the host exits without a runtime shutdown */
	fprintf (stderr, "[interp-census] loaded, output %s\n", out_path);
}
