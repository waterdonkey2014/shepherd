/* Kit #7 JIT stage 0 (vita/notes/kit7-jit.md): a Mac-only Mono profiler module that takes a census of everything the JIT puts in
 * code memory over a whole session, so the long-session code cache can be compared with the Vita's 16 MiB ForVM budget.
 *
 *   build:  vita/jit/stage0/census/build.sh       -> out/libmono-profiler-shepjit.dylib (Xcode clang, public Mono headers only)
 *   use:    DYLD_LIBRARY_PATH=<out dir> MONO_ENV_OPTIONS="--profile=shepjit:<prefix>"   (any execution mode: JIT, AOT + JIT, interpreter)
 *
 * What it records (all from the public profiler API of the osx-arm64 Mono 8.0.x pack the vita-sim publish runs):
 *   jit_done        every method whose code was produced or loaded: code start / size, from_aot, is_interp (MonoJitInfo bitfield, the
 *                   8.0.x jit-info.h layout; checked against the public mono_jit_info_get_code_size for every record), image, full name.
 *                   Deduplicated by code start (Mono raises jit_done twice for icall wrappers and for methods mapped onto shared code).
 *   jit_code_buffer every trampoline / thunk / helper the runtime emits outside a method (type = MonoProfilerCodeBufferType).
 *   jit_chunk_*     every code-manager chunk created / destroyed (reserved code memory, slack included).
 *   method_free     dynamic methods whose code is given back.
 * A sampler thread writes the cumulative totals every 250 ms (<prefix>.timeline.tsv), so growth over a long session can be plotted
 * against time; the analysis (vita/jit/stage0/jitcensus.py) maps time to frames with the engine's simstats-frames.tsv.
 * Per-allocation footprint (code + the in-method thunk area + alignment) is derived offline from address gaps inside each chunk: Mono's
 * code managers bump-allocate, so the distance from one allocation's start to the next one's in the same chunk is everything it took.
 *
 * Not part of any Vita build: the Vita runtime has no profiler modules and no dlopen. It allocates no executable memory and generates no
 * code; it only observes the stock runtime of the publish. */
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

typedef struct _MonoProfiler MonoProfiler;
typedef struct _MonoProfilerDesc *MonoProfilerHandle;
typedef struct _MonoMethod MonoMethod;
typedef struct _MonoClass MonoClass;
typedef struct _MonoImage MonoImage;
typedef struct _MonoJitInfo MonoJitInfo;
typedef int mono_bool;
typedef unsigned char mono_byte;

/* public Mono embedding API (src/native/public/mono/metadata/{profiler,profiler-events,class,image,debug-helpers}.h) */
extern MonoProfilerHandle mono_profiler_create (MonoProfiler *prof);
extern void mono_profiler_set_jit_begin_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *));
extern void mono_profiler_set_jit_failed_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *));
extern void mono_profiler_set_jit_done_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, MonoJitInfo *));
extern void mono_profiler_set_jit_chunk_created_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, const mono_byte *, uintptr_t));
extern void mono_profiler_set_jit_chunk_destroyed_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, const mono_byte *));
extern void mono_profiler_set_jit_code_buffer_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, const mono_byte *, uint64_t, int, const void *));
extern void mono_profiler_set_method_free_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *));
extern void mono_profiler_set_runtime_shutdown_begin_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *));
extern char *mono_method_full_name (MonoMethod *method, mono_bool signature);
extern MonoClass *mono_method_get_class (MonoMethod *method);
extern MonoImage *mono_class_get_image (MonoClass *klass);
extern const char *mono_image_get_name (MonoImage *image);
extern int mono_jit_info_get_code_size (MonoJitInfo *ji);
extern void *mono_jit_info_get_code_start (MonoJitInfo *ji);
extern void mono_free (void *);

struct _MonoProfiler { int unused; };
static MonoProfiler prof;

/* jit-info.h (8.0.x): method/next unions, code_start, unwind_info, code_size, then one 32-bit bitfield word */
typedef struct { void *d, *n, *code_start; uint32_t unwind_info; int32_t code_size; uint32_t bits; } JiPrefix;
#define JI_HAS_THUNK(b)   (((b) >> 18) & 1)
#define JI_FROM_AOT(b)    (((b) >> 20) & 1)
#define JI_FROM_LLVM(b)   (((b) >> 21) & 1)
#define JI_IS_TRAMP(b)    (((b) >> 27) & 1)
#define JI_IS_INTERP(b)   (((b) >> 28) & 1)

typedef struct { uint64_t t_us; uintptr_t start; uint32_t size; uint8_t aot, interp, thunk, llvm, tramp; MonoMethod *m; char *image; char *name; uint8_t freed; } MRec;
typedef struct { uint64_t t_us; uintptr_t start; uint64_t size; int type; } BRec;
typedef struct { uint64_t t_us; uintptr_t start; uint64_t size; uint64_t t_destroyed_us; } CRec;

static MRec *mr; static size_t mr_n, mr_cap;
static BRec *br; static size_t br_n, br_cap;
static CRec *cr; static size_t cr_n, cr_cap;
/* code start -> index (dedup), open addressing */
static uintptr_t *hk; static size_t *hv; static size_t h_cap, h_used;

static uint64_t begins, dones, dup_dones, failed, layout_mismatch, frees, chunk_destroys;
static uint64_t sum_jit_code, sum_aot_code, sum_interp_ir, sum_buf, sum_chunk_live;
static uint64_t n_jit, n_aot, n_interp;
static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER;
static char prefix [2048] = "jit-census";
static int written, stop_sampler;
static struct timespec t0;
static unsigned long long t0_unix_ms;
static FILE *tl;
static pthread_t sampler;

static uint64_t now_us (void)
{
	struct timespec t; clock_gettime (CLOCK_MONOTONIC, &t);
	return (uint64_t) (t.tv_sec - t0.tv_sec) * 1000000ull + (uint64_t) ((t.tv_nsec - t0.tv_nsec) / 1000);
}

static void *grow (void *p, size_t *cap, size_t el, size_t need)
{
	if (need <= *cap) return p;
	size_t nc = *cap ? *cap * 2 : 65536; while (nc < need) nc *= 2;
	void *np = realloc (p, nc * el); if (!np) { fprintf (stderr, "[shepjit] out of memory\n"); abort (); }
	*cap = nc; return np;
}

static size_t hash (uintptr_t k) { uint64_t x = k; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t) x; }
static size_t *hslot (uintptr_t k, int *found)
{
	if ((h_used + 1) * 2 > h_cap) {
		size_t nc = h_cap ? h_cap * 2 : 131072; uintptr_t *nk = calloc (nc, sizeof (uintptr_t)); size_t *nv = calloc (nc, sizeof (size_t));
		for (size_t i = 0; i < h_cap; i++) if (hk [i]) { size_t j = hash (hk [i]) & (nc - 1); while (nk [j]) j = (j + 1) & (nc - 1); nk [j] = hk [i]; nv [j] = hv [i]; }
		free (hk); free (hv); hk = nk; hv = nv; h_cap = nc;
	}
	size_t i = hash (k) & (h_cap - 1);
	while (hk [i] && hk [i] != k) i = (i + 1) & (h_cap - 1);
	*found = hk [i] == k;
	if (!*found) { hk [i] = k; h_used++; }
	return &hv [i];
}

static void on_begin (MonoProfiler *p, MonoMethod *m) { (void) p; (void) m; __atomic_fetch_add (&begins, 1, __ATOMIC_RELAXED); }
static void on_failed (MonoProfiler *p, MonoMethod *m) { (void) p; (void) m; __atomic_fetch_add (&failed, 1, __ATOMIC_RELAXED); }

static void on_done (MonoProfiler *p, MonoMethod *m, MonoJitInfo *ji)
{
	(void) p;
	if (!ji) return;
	int size = mono_jit_info_get_code_size (ji);
	uintptr_t start = (uintptr_t) mono_jit_info_get_code_start (ji);
	const JiPrefix *jp = (const JiPrefix *) ji;
	uint32_t bits = 0; int ok = jp->code_size == size && (uintptr_t) jp->code_start == start;
	if (ok) bits = jp->bits;
	uint64_t t = now_us ();
	MonoClass *k = m ? mono_method_get_class (m) : NULL;
	const char *img = k ? mono_image_get_name (mono_class_get_image (k)) : NULL;
	char *name = m ? mono_method_full_name (m, 1) : NULL;
	pthread_mutex_lock (&mu);
	dones++;
	if (!ok) layout_mismatch++;
	int found; size_t *v = hslot (start, &found);
	if (found) { dup_dones++; pthread_mutex_unlock (&mu); if (name) mono_free (name); return; }
	mr = grow (mr, &mr_cap, sizeof (MRec), mr_n + 1);
	MRec *r = &mr [mr_n]; *v = mr_n++;
	memset (r, 0, sizeof (*r));
	r->t_us = t; r->start = start; r->size = (uint32_t) size; r->m = m;
	r->aot = ok && JI_FROM_AOT (bits); r->interp = ok && JI_IS_INTERP (bits); r->thunk = ok && JI_HAS_THUNK (bits);
	r->llvm = ok && JI_FROM_LLVM (bits); r->tramp = ok && JI_IS_TRAMP (bits);
	r->image = strdup (img ? img : "?");
	r->name = name ? strdup (name) : strdup ("?");
	if (r->interp) { n_interp++; sum_interp_ir += size; }
	else if (r->aot) { n_aot++; sum_aot_code += size; }
	else { n_jit++; sum_jit_code += size; }
	pthread_mutex_unlock (&mu);
	if (name) mono_free (name);
}

static void on_buffer (MonoProfiler *p, const mono_byte *buf, uint64_t size, int type, const void *data)
{
	(void) p; (void) data;
	uint64_t t = now_us ();
	pthread_mutex_lock (&mu);
	br = grow (br, &br_cap, sizeof (BRec), br_n + 1);
	br [br_n++] = (BRec) { t, (uintptr_t) buf, size, type };
	sum_buf += size;
	pthread_mutex_unlock (&mu);
}

static void on_chunk (MonoProfiler *p, const mono_byte *chunk, uintptr_t size)
{
	(void) p;
	uint64_t t = now_us ();
	pthread_mutex_lock (&mu);
	cr = grow (cr, &cr_cap, sizeof (CRec), cr_n + 1);
	cr [cr_n++] = (CRec) { t, (uintptr_t) chunk, size, 0 };
	sum_chunk_live += size;
	pthread_mutex_unlock (&mu);
}

static void on_chunk_gone (MonoProfiler *p, const mono_byte *chunk)
{
	(void) p;
	uint64_t t = now_us ();
	pthread_mutex_lock (&mu);
	chunk_destroys++;
	for (size_t i = cr_n; i-- > 0;) if (cr [i].start == (uintptr_t) chunk && !cr [i].t_destroyed_us) { cr [i].t_destroyed_us = t; sum_chunk_live -= cr [i].size; break; }
	pthread_mutex_unlock (&mu);
}

static void on_free (MonoProfiler *p, MonoMethod *m)
{
	(void) p;
	pthread_mutex_lock (&mu);
	frees++;
	for (size_t i = mr_n; i-- > 0;) if (mr [i].m == m && !mr [i].freed) { mr [i].freed = 1; break; }
	pthread_mutex_unlock (&mu);
}

static void timeline_row (void)
{
	if (!tl) return;
	pthread_mutex_lock (&mu);
	fprintf (tl, "%llu\t%llu\t%llu\t%llu\t%llu\t%llu\t%llu\t%llu\t%zu\t%llu\t%zu\t%llu\n",
	         (unsigned long long) now_us (), (unsigned long long) n_jit, (unsigned long long) sum_jit_code,
	         (unsigned long long) n_aot, (unsigned long long) sum_aot_code, (unsigned long long) n_interp, (unsigned long long) sum_interp_ir,
	         (unsigned long long) begins, br_n, (unsigned long long) sum_buf, cr_n, (unsigned long long) sum_chunk_live);
	fflush (tl);
	pthread_mutex_unlock (&mu);
}

static void *sample_loop (void *a)
{
	(void) a;
	while (!__atomic_load_n (&stop_sampler, __ATOMIC_RELAXED)) { usleep (250000); timeline_row (); }
	return NULL;
}

static void write_out (void)
{
	pthread_mutex_lock (&mu);
	if (written) { pthread_mutex_unlock (&mu); return; }
	written = 1;
	pthread_mutex_unlock (&mu);
	__atomic_store_n (&stop_sampler, 1, __ATOMIC_RELAXED);
	timeline_row ();
	pthread_mutex_lock (&mu);
	char path [2100];
	snprintf (path, sizeof (path), "%s.methods.tsv", prefix);
	FILE *f = fopen (path, "w");
	if (f) {
		fprintf (f, "# shepjit census: jit_begin %llu, jit_done %llu (%llu duplicates of an already recorded code start), jit_failed %llu, "
		         "method_free %llu, JitInfo layout mismatches %llu, t0_unix_ms %llu; unique code: JIT %llu methods %llu B, AOT %llu methods %llu B, interp %llu methods %llu B IR\n",
		         (unsigned long long) begins, (unsigned long long) dones, (unsigned long long) dup_dones, (unsigned long long) failed,
		         (unsigned long long) frees, (unsigned long long) layout_mismatch, t0_unix_ms, (unsigned long long) n_jit, (unsigned long long) sum_jit_code,
		         (unsigned long long) n_aot, (unsigned long long) sum_aot_code, (unsigned long long) n_interp, (unsigned long long) sum_interp_ir);
		fprintf (f, "t_us\tstart\tsize\tkind\tthunk\tllvm\ttramp\tfreed\timage\tmethod\n");
		for (size_t i = 0; i < mr_n; i++) {
			MRec *r = &mr [i];
			fprintf (f, "%llu\t%llx\t%u\t%s\t%u\t%u\t%u\t%u\t%s\t%s\n", (unsigned long long) r->t_us, (unsigned long long) r->start, r->size,
			         r->interp ? "interp" : r->aot ? "aot" : "jit", r->thunk, r->llvm, r->tramp, r->freed, r->image, r->name);
		}
		fclose (f);
	}
	snprintf (path, sizeof (path), "%s.buffers.tsv", prefix);
	if ((f = fopen (path, "w"))) {
		fprintf (f, "t_us\tstart\tsize\ttype\n");
		for (size_t i = 0; i < br_n; i++) fprintf (f, "%llu\t%llx\t%llu\t%d\n", (unsigned long long) br [i].t_us, (unsigned long long) br [i].start, (unsigned long long) br [i].size, br [i].type);
		fclose (f);
	}
	snprintf (path, sizeof (path), "%s.chunks.tsv", prefix);
	if ((f = fopen (path, "w"))) {
		fprintf (f, "t_us\tstart\tsize\tt_destroyed_us\n");
		for (size_t i = 0; i < cr_n; i++) fprintf (f, "%llu\t%llx\t%llu\t%llu\n", (unsigned long long) cr [i].t_us, (unsigned long long) cr [i].start, (unsigned long long) cr [i].size, (unsigned long long) cr [i].t_destroyed_us);
		fclose (f);
	}
	fprintf (stderr, "[shepjit] JIT %llu methods %.2f MiB, AOT %llu methods, interp %llu; buffers %zu (%.2f MiB); chunks %zu (%.2f MiB live) -> %s.*\n",
	         (unsigned long long) n_jit, sum_jit_code / 1048576.0, (unsigned long long) n_aot, (unsigned long long) n_interp,
	         br_n, sum_buf / 1048576.0, cr_n, sum_chunk_live / 1048576.0, prefix);
	if (tl) { fclose (tl); tl = NULL; }
	pthread_mutex_unlock (&mu);
}
static void on_shutdown (MonoProfiler *p) { (void) p; write_out (); }

__attribute__((visibility ("default"))) void mono_profiler_init_shepjit (const char *desc)
{
	clock_gettime (CLOCK_MONOTONIC, &t0);
	{ struct timespec r; clock_gettime (CLOCK_REALTIME, &r); t0_unix_ms = (unsigned long long) r.tv_sec * 1000ull + (unsigned long long) (r.tv_nsec / 1000000); }
	const char *c = desc ? strchr (desc, ':') : NULL;
	if (c && c [1]) strncpy (prefix, c + 1, sizeof (prefix) - 1);
	char path [2100]; snprintf (path, sizeof (path), "%s.timeline.tsv", prefix);
	tl = fopen (path, "w");
	if (tl) fprintf (tl, "# t0_unix_ms %llu (t_us counts from here: the profiler's load, before the engine's Main)\n", t0_unix_ms);
	if (tl) fprintf (tl, "t_us\tjit_methods\tjit_bytes\taot_methods\taot_bytes\tinterp_methods\tinterp_ir\tjit_begin\tbuffers\tbuffer_bytes\tchunks\tchunk_live_bytes\n");
	MonoProfilerHandle h = mono_profiler_create (&prof);
	mono_profiler_set_jit_begin_callback (h, on_begin);
	mono_profiler_set_jit_failed_callback (h, on_failed);
	mono_profiler_set_jit_done_callback (h, on_done);
	mono_profiler_set_jit_code_buffer_callback (h, on_buffer);
	mono_profiler_set_jit_chunk_created_callback (h, on_chunk);
	mono_profiler_set_jit_chunk_destroyed_callback (h, on_chunk_gone);
	mono_profiler_set_method_free_callback (h, on_free);
	mono_profiler_set_runtime_shutdown_begin_callback (h, on_shutdown);
	atexit (write_out);   /* if the host exits without a runtime shutdown */
	pthread_create (&sampler, NULL, sample_loop, NULL);
	pthread_detach (sampler);
	fprintf (stderr, "[shepjit] loaded, output %s.*\n", prefix);
}
